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

/// A pane's queued list with delegation's held items in it (macOS only). The
/// items held for the pane come first, oldest first, as they run first; the
/// human's queued requests follow. Held rows cannot be removed, do not count
/// toward the queue's 16-item cap, and keep run next on offer while there
/// are any. The phone gets them in its existing `queued` item shape.
public enum DelegationQueueRows {
    /// The pane's rows in the phone's `queued` array: held first, then the
    /// human's requests.
    public static func mobileItems(held: [DelegationDeliveryItem], queued: [QueuedInput]) -> [MobileQueuedItem] {
        held.map { MobileQueuedItem(id: $0.id, text: $0.text) } + queued.map { MobileQueuedItem(id: $0.id, text: $0.text) }
    }

    /// `held_not_removable` when `itemId` is one of the pane's held rows, and
    /// the remove route changes nothing; nil when it may go on.
    public static func removeRefusal(itemId: String, held: [DelegationDeliveryItem]) -> DelegationReasonCode? {
        held.contains { $0.id == itemId } ? .heldNotRemovable : nil
    }

    /// Whether the queued list offers run next: whenever items are held,
    /// otherwise only while the pane is not busy.
    public static func offersRunNext(busy: Bool, held: Int) -> Bool { held > 0 || !busy }
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

    /// The items held for each pane, oldest first: the rows its queued list
    /// shows. A pane is a parent with notices or a child with follow-ups,
    /// never both, so each list is in the order its items were made.
    func heldItemsByPane() -> [String: [DelegationDeliveryItem]] {
        var rows: [String: [DelegationDeliveryItem]] = [:]
        let held = notices.filter { $0.lane == .held }.compactMap(deliveryItem(of:)) + followUps.filter { $0.lane == .held }.compactMap(deliveryItem(of:))
        for item in held { rows[item.sessionId, default: []].append(item) }
        return rows
    }

    /// The held notices from the child `childId`, oldest first.
    func heldNotices(of childId: String) -> [Notice] { notices.filter { $0.childId == childId && $0.lane == .held } }

    /// The notice as its child's parent pane gets it; nil without the child.
    func deliveryItem(of notice: Notice) -> DelegationDeliveryItem? {
        guard let child = children.first(where: { $0.id == notice.childId }) else { return nil }
        return DelegationDeliveryItem(id: notice.id, kind: .notice, childId: notice.childId, sessionId: child.parentSessionId, text: DelegationCoordinator.text(of: notice))
    }

    /// The follow-up as its child's pane gets it; nil without the child.
    func deliveryItem(of followUp: FollowUp) -> DelegationDeliveryItem? {
        guard let child = children.first(where: { $0.id == followUp.childId }) else { return nil }
        let text = DelegationCoordinator.text(of: followUp, reportFile: ChildWorktree.reportFile(worktreePath: child.worktreePath))
        return DelegationDeliveryItem(id: followUp.id, kind: .followUp, childId: followUp.childId, sessionId: followUp.childId, text: text)
    }
}

/// Delivery (macOS only): each notice goes to its child's parent pane and
/// each follow-up to its child's pane, exactly once.
///
/// A new item is offered to its pane at once, by the first rule that applies:
///
/// 1. The pane runs Claude now: the item is steered into that run.
/// 2. The pane is idle after a run that finished normally since this launch,
///    and a run can start in it now: the item starts its next run.
/// 3. Otherwise (the pane stopped, errored, has had no run since this launch,
///    cannot start a run now, or is closed) the item is held. Only the
///    human's next send or run next releases it, all of the pane's held items
///    together, oldest first, in one run. A closed parent's held notices stay
///    on its children's cards.
///
/// Before a pane gets an item, the item is saved delivered with its receipt,
/// so it is never handed over twice; should the pane not take it after all,
/// it goes back as it was. Should the pane not take a new item twice running,
/// the item stays pending and is offered again with the next one, or when the
/// app sees one of its panes' runs start or end; what is still pending at
/// launch is held.
extension DelegationCoordinator {
    /// How often one new item is offered to its pane in a row: a run that
    /// ends just as an item is steered into it gets it as its next run.
    static let deliveryOffers = 2

    /// The items held for the pane `sessionId`, oldest first: rows its queued
    /// list shows, which cannot be removed.
    public func heldItems(for sessionId: String) -> [DelegationDeliveryItem] { items(for: sessionId, in: .held) }

    /// The held notices from the child `childId`. When its parent pane is
    /// closed nothing releases them: they stay on the child's card, which
    /// counts them (``DelegationChildRow/heldNotices``).
    public func heldNotices(of childId: String) -> [Notice] { file.heldNotices(of: childId) }

    /// The human's send of `text` in the pane `sessionId`. When items are held
    /// for it, all of them go with the send, oldest first and ahead of
    /// `text`, in the one run the host starts. Otherwise nothing changes and
    /// the caller sends `text` as it would anyway.
    public func send(_ text: String, in sessionId: String) async -> DelegationRelease { await release(in: sessionId, ahead: text, start: nil) }

    /// The same send, with the run started by `start` instead of the host:
    /// the app's send starts it with the human's attachments too. `start`
    /// gets the run's whole input and answers the new run's id, or nil when
    /// none started.
    public func send(_ text: String, in sessionId: String, start: @escaping @Sendable (String) async -> String?) async -> DelegationRelease {
        await release(in: sessionId, ahead: text, start: start)
    }

    /// Run next in the pane `sessionId`. When items are held for it, all of
    /// them and nothing else go to the pane, oldest first, in one run: steered
    /// into its running Claude run, or as its next run. The human's queued
    /// rows follow in later runs. Otherwise nothing changes and the caller
    /// runs its next queued row.
    public func runNext(in sessionId: String) async -> DelegationRelease { await release(in: sessionId, ahead: nil, start: nil) }

    /// Offers every pending item to its pane, oldest first. The app calls it
    /// whenever one of its panes' runs starts or ends, so an item that a run
    /// still starting or already ending could not take is steered into the
    /// run once it is up, or goes by the pane's next run or is held once it
    /// ended. A call while a pass is going asks that pass for one more and
    /// returns at once.
    public func deliverPending() async {
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
            // A card's merge or undo is changing the pane's checkout: no run
            // starts there now, and the item is offered again once it is over.
            if route == .queue, cardPanes.contains(item.sessionId) { return }
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
    /// for run next) in one run, which `start` starts (the host when nil).
    /// Run next steers them into the pane's running Claude run instead.
    private func release(in sessionId: String, ahead text: String?, start: (@Sendable (String) async -> String?)?) async -> DelegationRelease {
        guard !items(for: sessionId, in: .held).isEmpty else { return .nothingHeld }
        guard let pane = await host.paneState(sessionId: sessionId) else { return .notStarted }
        let steers = text == nil && pane.activity == .running && DelegationSwitch.isClaudePane(kind: pane.kind, provider: pane.provider)
        let context = await pruneContext()
        // Nothing below suspends until the release is saved, so two sends at
        // once never take the same item.
        let held = items(for: sessionId, in: .held)
        guard !held.isEmpty else { return .nothingHeld }
        let receipt = DeliveryReceipt(time: Self.now(), route: steers ? .steer : .queue, runId: steers ? pane.runId ?? "" : "")
        guard save(moving: held, to: .delivered, receipt: receipt, context: context) else { return .notStarted }
        let input = (held.map(\.text) + [text ?? ""].filter { !$0.isEmpty }).joined(separator: "\n\n")
        let runId: String?
        if steers { runId = await host.deliver(input, to: sessionId, route: .steer) }
        else if let start { runId = await start(input) }
        else { runId = await host.startRun(sessionId: sessionId, input: input) }
        guard let runId else {
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
        (file.notices.filter { $0.lane == lane }.compactMap(file.deliveryItem(of:)) + file.followUps.filter { $0.lane == lane }.compactMap(file.deliveryItem(of:)))
            .filter { $0.sessionId == sessionId }
    }

    private func lookUp(_ id: String, _ kind: DelegationDeliveryItem.Kind, in lane: DeliveryLane) -> DelegationDeliveryItem? {
        switch kind {
        case .notice: return file.notices.first { $0.id == id && $0.lane == lane }.flatMap(file.deliveryItem(of:))
        case .followUp: return file.followUps.first { $0.id == id && $0.lane == lane }.flatMap(file.deliveryItem(of:))
        }
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
