import Foundation

/// One notice or follow-up as delivery hands it to a pane (macOS only).
public struct DelegationDeliveryItem: Sendable, Equatable, Identifiable {
    public enum Kind: String, Sendable, CaseIterable { case notice, followUp = "follow_up" }

    /// The notice's or follow-up's own id; its text names it too.
    public var id: String
    public var kind: Kind
    /// The child the notice is about, or the follow-up is for.
    public var childId: String
    /// The pane it goes to: the child's parent for a notice, the child itself
    /// for a follow-up.
    public var sessionId: String
    /// What the pane's run gets.
    public var text: String
}

/// What one release of a pane's held items did.
public enum DelegationRelease: Sendable, Equatable {
    /// Nothing is held for the pane: the caller sends or runs next as it
    /// would anyway.
    case nothingHeld
    /// Every item held for the pane went to it, oldest first, in the one run
    /// `runId`.
    case released(runId: String, itemIds: [String])
    /// No run started (or the pane is closed), so they all stay held.
    case notStarted
}

extension DelegationFile {
    /// Holds every notice and follow-up still pending; true when any was.
    /// At launch nothing is delivered on its own: what the last launch left
    /// pending waits for the human's next send or run next.
    mutating func holdPending() -> Bool {
        var held = false
        for index in notices.indices where notices[index].lane == .pending { notices[index].lane = .held; held = true }
        for index in followUps.indices where followUps[index].lane == .pending { followUps[index].lane = .held; held = true }
        return held
    }
}

/// Delivery (macOS only): each notice goes to its child's parent pane and
/// each follow-up to its child's pane, exactly once.
///
/// A new item is offered to its pane at once, by the first rule that applies:
///
/// 1. The pane runs Claude now: the item is steered into that run.
/// 2. The pane is idle after a run that finished normally since this launch:
///    the item starts its next run.
/// 3. Otherwise (the pane stopped, errored, has had no run since this launch,
///    or is closed) the item is held. Only the human's next send or run next
///    releases it, all of the pane's held items together, oldest first, in
///    one run. A closed parent's held notices stay on its children's cards.
///
/// Before a pane gets an item, the item is saved delivered with its receipt,
/// so it is never handed over twice; should the pane not take it after all,
/// it goes back as it was. Should the pane not take a new item twice running,
/// the item stays pending and is offered again with the next one; what is
/// still pending at launch is held.
extension DelegationCoordinator {
    /// How often one new item is offered to its pane in a row: a run that
    /// ends just as an item is steered into it gets it as its next run.
    static let deliveryOffers = 2

    /// The items held for the pane `sessionId`, oldest first: rows its queued
    /// list shows, which cannot be removed.
    public func heldItems(for sessionId: String) -> [DelegationDeliveryItem] { items(for: sessionId, in: .held) }

    /// The held notices from the child `childId`. When its parent pane is
    /// closed nothing releases them: they stay on the child's card.
    public func heldNotices(of childId: String) -> [Notice] { file.notices.filter { $0.childId == childId && $0.lane == .held } }

    /// The human's send of `text` in the pane `sessionId`. When items are held
    /// for it, all of them go with the send, oldest first and ahead of
    /// `text`, in the one run the host starts. Otherwise nothing changes and
    /// the caller sends `text` as it would anyway.
    public func send(_ text: String, in sessionId: String) async -> DelegationRelease { await release(in: sessionId, ahead: text) }

    /// Run next in the pane `sessionId`. When items are held for it, all of
    /// them and nothing else go to the pane, oldest first, in one run; the
    /// human's queued rows follow in later runs. Otherwise nothing changes
    /// and the caller runs its next queued row.
    public func runNext(in sessionId: String) async -> DelegationRelease { await release(in: sessionId, ahead: nil) }

    /// Offers every pending item to its pane, oldest first. A call while a
    /// pass is going asks that pass for one more and returns at once.
    func deliverPending() async {
        wantsDelivery = true
        guard !isDelivering else { return }
        isDelivering = true
        while wantsDelivery {
            wantsDelivery = false
            let pending = file.notices.filter { $0.lane == .pending }.map { ($0.id, DelegationDeliveryItem.Kind.notice) }
                + file.followUps.filter { $0.lane == .pending }.map { ($0.id, DelegationDeliveryItem.Kind.followUp) }
            for (id, kind) in pending { await offer(id, kind) }
        }
        isDelivering = false
    }

    /// Offers the pending item `id` to its pane as the pane is now.
    private func offer(_ id: String, _ kind: DelegationDeliveryItem.Kind) async {
        for _ in 0..<Self.deliveryOffers {
            guard let found = lookUp(id, kind, in: .pending) else { return }
            let pane = await host.paneState(sessionId: found.sessionId)
            let context = await pruneContext()
            // Nothing below suspends until the item's lane is saved, so
            // nothing else moves it meanwhile.
            guard let item = lookUp(id, kind, in: .pending) else { return }
            guard let route = Self.automaticRoute(to: pane) else {
                keep(moving: [item], to: .held, receipt: nil, context: context)
                return
            }
            let receipt = DeliveryReceipt(time: Self.now(), route: route, runId: route == .steer ? pane?.runId ?? "" : "")
            guard save(moving: [item], to: .delivered, receipt: receipt, context: context) else { return }
            if let runId = await host.deliver(item.text, to: item.sessionId, route: route) {
                await received([item], runId: runId, context: context)
                return
            }
            keep(moving: [item], to: .pending, receipt: nil, context: context)
        }
    }

    /// All of `sessionId`'s held items, oldest first, ahead of `text` (none
    /// for run next) in one run.
    private func release(in sessionId: String, ahead text: String?) async -> DelegationRelease {
        guard !items(for: sessionId, in: .held).isEmpty else { return .nothingHeld }
        guard await host.paneState(sessionId: sessionId) != nil else { return .notStarted }
        let context = await pruneContext()
        // Nothing below suspends until the release is saved, so two sends at
        // once never take the same item.
        let held = items(for: sessionId, in: .held)
        guard !held.isEmpty else { return .nothingHeld }
        guard save(moving: held, to: .delivered, receipt: DeliveryReceipt(time: Self.now(), route: .queue, runId: ""), context: context) else { return .notStarted }
        let input = (held.map(\.text) + [text ?? ""].filter { !$0.isEmpty }).joined(separator: "\n\n")
        guard let runId = await host.startRun(sessionId: sessionId, input: input) else {
            keep(moving: held, to: .held, receipt: nil, context: context)
            return .notStarted
        }
        await received(held, runId: runId, context: context)
        return .released(runId: runId, itemIds: held.map(\.id))
    }

    /// The run `runId` got `items`: their receipts name it, and a follow-up's
    /// run is the one its child's parent now awaits.
    private func received(_ items: [DelegationDeliveryItem], runId: String, context: DelegationPruneContext) async {
        var next = file
        next.update(items) { lane, receipt in if lane == .delivered { receipt?.runId = runId } }
        saveOrKeep(next, context: context)
        for childId in Set(items.filter { $0.kind == .followUp }.map(\.childId)) { await awaitRun(runId, of: childId) }
    }

    /// How an item reaches `pane` with no human: steered into its running
    /// Claude run, or as the next run of a pane idle after a run that finished
    /// normally since this launch. nil when it is held.
    static func automaticRoute(to pane: DelegationPaneState?) -> DeliveryRoute? {
        guard let pane else { return nil }
        switch pane.activity {
        case .running: return DelegationSwitch.isClaudePane(kind: pane.kind, provider: pane.provider) ? .steer : nil
        case .finished: return .queue
        case .idle: return nil
        }
    }

    /// A notice as its parent's run reads it: a pointer naming the notice and
    /// the child. The report itself is read through child_status.
    static func text(of notice: Notice) -> String {
        let head = "[Mighty Claude notice \(notice.id)]"
        switch notice.kind {
        case .reported:
            return "\(head) Your child \(notice.childId) reported revision \(notice.reportRevision). Read the report with child_status; merge it with merge when it is right."
        case .endedWithoutReport:
            return "\(head) Your child \(notice.childId)'s run ended without a new report; its report revision is still \(notice.reportRevision). Call child_status to see where it stands."
        case .failedToStart:
            return "\(head) Your child \(notice.childId) could not be started. Only a human can discard it, from its card."
        }
    }

    /// A follow-up as its child's run reads it.
    static func text(of followUp: FollowUp, reportFile: String) -> String {
        """
        [Mighty Claude follow-up \(followUp.id)] Your parent sent you this follow-up:

        \(followUp.text)

        When you are done, commit your work on your branch and write your updated report to \(reportFile).
        """
    }

    /// The items for the pane `sessionId` in `lane`, oldest first.
    private func items(for sessionId: String, in lane: DeliveryLane) -> [DelegationDeliveryItem] {
        (file.notices.filter { $0.lane == lane }.compactMap(deliveryItem(of:)) + file.followUps.filter { $0.lane == lane }.compactMap(deliveryItem(of:)))
            .filter { $0.sessionId == sessionId }
    }

    private func lookUp(_ id: String, _ kind: DelegationDeliveryItem.Kind, in lane: DeliveryLane) -> DelegationDeliveryItem? {
        switch kind {
        case .notice: return file.notices.first { $0.id == id && $0.lane == lane }.flatMap(deliveryItem(of:))
        case .followUp: return file.followUps.first { $0.id == id && $0.lane == lane }.flatMap(deliveryItem(of:))
        }
    }

    private func deliveryItem(of notice: Notice) -> DelegationDeliveryItem? {
        guard let child = file.children.first(where: { $0.id == notice.childId }) else { return nil }
        return DelegationDeliveryItem(id: notice.id, kind: .notice, childId: notice.childId, sessionId: child.parentSessionId, text: Self.text(of: notice))
    }

    private func deliveryItem(of followUp: FollowUp) -> DelegationDeliveryItem? {
        guard let child = file.children.first(where: { $0.id == followUp.childId }) else { return nil }
        let text = Self.text(of: followUp, reportFile: ChildWorktree.reportFile(worktreePath: child.worktreePath))
        return DelegationDeliveryItem(id: followUp.id, kind: .followUp, childId: followUp.childId, sessionId: followUp.childId, text: text)
    }

    /// Saves `items` moved to `lane`; false, changing nothing, when the disk
    /// refuses it.
    private func save(moving items: [DelegationDeliveryItem], to lane: DeliveryLane, receipt: DeliveryReceipt?, context: DelegationPruneContext) -> Bool {
        var next = file
        next.update(items) { $0 = lane; $1 = receipt }
        do { try commit(next, context: context); return true } catch { return false }
    }

    /// Moves `items` to `lane`; should the disk refuse it, the app goes on
    /// with the move and the next save writes it.
    private func keep(moving items: [DelegationDeliveryItem], to lane: DeliveryLane, receipt: DeliveryReceipt?, context: DelegationPruneContext) {
        var next = file
        next.update(items) { $0 = lane; $1 = receipt }
        saveOrKeep(next, context: context)
    }

    /// Saves `next`; should the disk refuse it, the app goes on with it and
    /// the next save writes it.
    private func saveOrKeep(_ next: DelegationFile, context: DelegationPruneContext) {
        do { try commit(next, context: context) } catch { file = next }
    }

    static func now() -> String { ISO8601DateFormatter().string(from: Date()) }
}

private extension DelegationFile {
    /// Changes the lane and receipt of each of `items`.
    mutating func update(_ items: [DelegationDeliveryItem], _ change: (inout DeliveryLane, inout DeliveryReceipt?) -> Void) {
        for item in items {
            switch item.kind {
            case .notice:
                guard let index = notices.firstIndex(where: { $0.id == item.id }) else { continue }
                var notice = notices[index]
                change(&notice.lane, &notice.receipt)
                notices[index] = notice
            case .followUp:
                guard let index = followUps.firstIndex(where: { $0.id == item.id }) else { continue }
                var followUp = followUps[index]
                change(&followUp.lane, &followUp.receipt)
                followUps[index] = followUp
            }
        }
    }
}
