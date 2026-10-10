import Foundation
import MightyCore

/// The last answer a child's card got, shown on the card until its next action.
struct DelegationCardNote: Equatable {
    enum Result: Equatable, Sendable {
        case done
        /// Refused with this reason; nothing changed.
        case refused(DelegationReasonCode)
        case failed(String)
        /// More worktrees are nested in the child's than its discard's
        /// confirmation named: nothing was removed, and it asks again with these.
        case reconfirm([String])
    }

    var action: DelegationCardAction
    var result: Result
}

/// A discard from a child's card waiting on the human's confirmation.
struct PendingChildDiscard: Identifiable, Equatable {
    /// The child's id.
    var id: String
    var title: String
    /// The child's branch, `mighty/<id>`.
    var branch: String
    /// The worktrees nested in the child's that go with it, deepest first;
    /// nil when they could not be listed, and then the child cannot be
    /// discarded now: the confirmation says why and offers no discard.
    var nested: [String]?
}

/// A Mac close of a parent pane whose children are still open, waiting on
/// the human's confirmation: only the parent closes, and its children stay
/// as cards under a 'parent closed' node.
struct PendingParentClose: Identifiable, Equatable {
    /// The parent pane's id.
    var id: String
    var title: String
    /// How many of its children are open.
    var openChildren: Int
}

/// The app side of parent → child delegation between Claude panes (macOS
/// only): AppStore is the coordinator's ``DelegationHost``. The coordinator
/// owns the records, the tools, delivery and the git actions; the store only
/// makes panes, starts and steers runs and reports what its panes do.
extension AppStore {
    /// Makes the coordinator from the profile's delegation file, whether or
    /// not the hidden switch is on: switching it off only detaches the
    /// delegation server, so existing children keep working. Worktrees go
    /// under `~/.mightyclaude/worktrees`, or the profile for smoke runs.
    func startDelegation() {
        guard delegation == nil else { return }
        let root = smokeTesting ? dataDirectory.appendingPathComponent("worktrees", isDirectory: true) : ChildWorktreeMaker.defaultRoot
        do {
            let coordinator = try DelegationCoordinator(store: DelegationFileStore(directory: dataDirectory), host: self, worktrees: ChildWorktreeMaker(root: root))
            delegation = coordinator
            delegationRunEvents = DelegationRunEventPump(coordinator: coordinator)
            // The sidebar's tree follows the file, switch on or off.
            delegationChildWatch = Task { [weak self] in
                for await rows in coordinator.childRows {
                    guard let self else { return }
                    if delegationChildren != rows { delegationChildren = rows }
                }
            }
            // So do the held rows of each pane's queued list, Mac and phone.
            delegationHeldWatch = Task { [weak self] in
                for await rows in coordinator.heldRows {
                    guard let self else { return }
                    if delegationHeld != rows { delegationHeld = rows }
                }
            }
        } catch {
            NSLog("MightyClaude delegation file unreadable; delegation is unavailable this launch: %@", error.localizedDescription)
        }
    }

    /// A run of the pane `id` ended with `status`. A child's end reaches the
    /// coordinator after its start; while the app quits it counts as killed by quitting.
    /// Any pane's end offers what it could not take while ending again: as
    /// the pane's next run after a normal finish, held otherwise.
    func delegationRunEnded(_ id: String, status: String) {
        guard let ended = delegationRuns.end(id, status: status, quitting: ending) else { return }
        if snapshot.sessions.first(where: { $0.id == id })?.parentSessionId != nil {
            delegationRunEvents?.send(.ended(childId: id, runId: ended.runId, end: ended.end))
        }
        offerDelegationPending()
    }

    /// Has delegation offer its pending notices and follow-ups to their panes
    /// as they are now: a running Claude run takes them by steer.
    func offerDelegationPending() {
        guard !ending, let delegation else { return }
        Task { await delegation.deliverPending() }
    }

    /// Where a send that goes with the pane's held items came from, and so
    /// where it goes back when no run took it.
    enum HeldSendOrigin {
        /// The Mac composer: its draft and attachments go back to it.
        case composer(draft: String)
        /// The phone: it is told the send was dropped, and the Mac's error
        /// banner is left as it was.
        case phone
        /// A queued request starting: back at the head of the queue.
        case queue(QueuedInput)

        var draft: String? { if case .composer(let draft) = self { draft } else { nil } }
        var queued: QueuedInput? { if case .queue(let item) = self { item } else { nil } }
    }

    /// The human's send of `input` in the idle pane `id` while items are held
    /// for it, from the composer, the phone or the queue: delegation releases
    /// them all, oldest first, ahead of `input` and with its attachments, in
    /// one run. What the send did.
    @discardableResult
    func releaseHeld(_ id: String, input: String, attachments: [RunAttachment], from origin: HeldSendOrigin) -> Task<SubmitOutcome, Never> {
        // Taken from the composer now, so a second Enter cannot send it again.
        if origin.draft != nil { drafts[id] = "" }
        let submittedIds = Set(attachments.map(\.id))
        attachmentDrafts[id]?.removeAll { submittedIds.contains($0.id) }
        return Task { [weak self] in
            guard let self else { return .dropped }
            let macError = error
            defer { if case .phone = origin { error = macError } }
            guard let delegation else { return sendUnheld(id, input: input, attachments: attachments, from: origin) }
            let release = await delegation.send(input, in: id) { [weak self] whole in
                await self?.startDelivered(id, input: whole, attachments: attachments, restoringDraft: origin.draft, queued: origin.queued, titleFrom: input)
            }
            switch release {
            case .released: return .started
            // Released meanwhile by another send: this one goes as it would.
            case .nothingHeld: return sendUnheld(id, input: input, attachments: attachments, from: origin)
            case .notStarted:
                giveBack(id, attachments: attachments, from: origin)
                return .dropped
            }
        }
    }

    /// A run of `input` that delegation hands the pane: its id once the run's
    /// process has it, or nil when the pane cannot run now or its start
    /// failed or was stopped, so what it carried is not lost. A draft typed
    /// meanwhile is never cleared.
    private func startDelivered(_ id: String, input: String, attachments: [RunAttachment], restoringDraft: String?, queued: QueuedInput? = nil, titleFrom: String) async -> String? {
        guard !ending, let session = snapshot.sessions.first(where: { $0.id == id }),
              let workspace = snapshot.workspaces.first(where: { $0.id == session.workspaceId }),
              start(id, session: session, workspace: workspace, input: input, attachments: attachments,
                    restoringDraft: (drafts[id] ?? "").isEmpty ? restoringDraft : nil, queued: queued, titleFrom: titleFrom) else { return nil }
        let runId = delegationRuns.runId(id)
        guard await startReachedRunner(id) else { return nil }
        return runId
    }

    /// The human's send with nothing held: started now, or queued behind the
    /// run that is starting.
    private func sendUnheld(_ id: String, input: String, attachments: [RunAttachment], from origin: HeldSendOrigin) -> SubmitOutcome {
        guard let session = snapshot.sessions.first(where: { $0.id == id }), let workspace = snapshot.workspaces.first(where: { $0.id == session.workspaceId }) else { return .dropped }
        if session.status == "running" || pendingRuns.contains(id) {
            if let item = origin.queued { queuedInputs[id, default: []].insert(item, at: 0); return .queued }
            let item = QueuedInput(text: input, attachments: attachments, permissionModeOverride: BackgroundQueuePolicy.queuedOverride(launchesInPlan: styleLaunchesInPlanMode(session)))
            switch deferInput(id, session: session, workspace: workspace, item: item, steering: false) {
            case .queued: return .queued
            case .refused, .steering:
                giveBack(id, attachments: attachments, from: origin)
                return .dropped
            }
        }
        if start(id, session: session, workspace: workspace, input: input, attachments: attachments,
                 restoringDraft: (drafts[id] ?? "").isEmpty ? origin.draft : nil, queued: origin.queued) { return .started }
        giveBack(id, attachments: attachments, from: origin)
        return .dropped
    }

    /// Gives a send that no run took back where it came from: the composer,
    /// unless something was typed meanwhile, or the head of the queue. The
    /// attachments of a phone's send or a queued request never stay in the
    /// Mac composer, where a failed start puts a send's attachments back.
    private func giveBack(_ id: String, attachments: [RunAttachment], from origin: HeldSendOrigin) {
        guard canEditAttachments(id) else { return }
        switch origin {
        case .composer(let draft):
            if (drafts[id] ?? "").isEmpty { drafts[id] = draft }
            let existing = Set((attachmentDrafts[id] ?? []).map(\.id))
            let missing = attachments.filter { !existing.contains($0.id) }
            if !missing.isEmpty { attachmentDrafts[id, default: []].insert(contentsOf: missing, at: 0) }
        case .queue(let item):
            let ids = Set(attachments.map(\.id))
            attachmentDrafts[id]?.removeAll { ids.contains($0.id) }
            if queuedInputs[id]?.contains(where: { $0.id == item.id }) != true { queuedInputs[id, default: []].insert(item, at: 0) }
            updateSession(id) { $0.logs.append(LogEntry(kind: "system", text: L("queue.log.keptAfterFailure", ["error": error ?? ""]))) }
        case .phone:
            let ids = Set(attachments.map(\.id))
            attachmentDrafts[id]?.removeAll { ids.contains($0.id) }
        }
    }

    // MARK: Closing panes and removing workspaces

    /// A human's close of the pane `id` from the Mac: a parent pane with open
    /// children asks first, in the view layer, and then closes only the
    /// parent; any other pane closes now. The phone's close never asks.
    func requestCloseSession(_ id: String) {
        let open = delegationChildren.filter { $0.parentSessionId == id && $0.state.isOpen }.count
        guard open > 0, let session = snapshot.sessions.first(where: { $0.id == id }) else { closeSession(id); return }
        guard pendingParentClose == nil else { return }
        pendingParentClose = PendingParentClose(id: id, title: session.title, openChildren: open)
    }

    /// The parent close the human confirmed.
    func closeParentConfirmed(_ pending: PendingParentClose) {
        pendingParentClose = nil
        closeSession(pending.id)
    }

    /// `workspace_has_children` while delegation has open children or child
    /// worktrees not cleaned up in the workspace; nil when it may be removed.
    func workspaceRemovalRefusal(_ workspace: Workspace) async -> DelegationReasonCode? {
        guard let delegation else { return nil }
        let panes = Set(snapshot.sessions.filter { $0.workspaceId == workspace.id }.map(\.id))
        return await delegation.workspaceRemovalRefusal(paneIds: panes, folder: workspace.path)
    }

    // MARK: Child cards

    /// The human's merge from the child's card.
    func mergeChildFromCard(_ id: String) {
        runCardAction(id, .merge) { delegation in
            switch await delegation.mergeFromCard(id) {
            case .merged: .done
            case .refused(let reason): .refused(reason)
            case .failed(let message): .failed(message)
            }
        }
    }

    /// The human's undo of the child's last merge from its card.
    func undoChildMergeFromCard(_ id: String) {
        runCardAction(id, .undo) { delegation in
            switch await delegation.undoMergeFromCard(id) {
            case .undone: .done
            case .refused(let reason): .refused(reason)
            case .failed(let message): .failed(message)
            }
        }
    }

    /// The human's discard from the child's card: first the confirmation,
    /// which names the worktrees nested in the child's that go with it, or
    /// says it cannot be discarded now when they cannot be listed. One
    /// confirmation at a time; a second is not asked over the first.
    func askToDiscardChild(_ id: String) {
        guard let delegation, pendingChildDiscard == nil, !delegationCardBusy.contains(id), let row = delegationChildren.first(where: { $0.id == id }) else { return }
        delegationCardBusy.insert(id)
        delegationCardNotes[id] = nil
        let title = snapshot.sessions.first(where: { $0.id == id })?.title ?? row.task ?? L("delegation.child.untitled")
        Task { [weak self] in
            let nested = await delegation.nestedWorktrees(of: id)
            guard let self else { return }
            delegationCardBusy.remove(id)
            guard pendingChildDiscard == nil else { return }
            pendingChildDiscard = PendingChildDiscard(id: id, title: title, branch: ChildRecord.branchName(for: id), nested: nested)
        }
    }

    /// The discard the human confirmed, with the nested worktrees its
    /// confirmation named. When more are nested now, nothing is removed and
    /// the confirmation asks again naming them all. The child's pane, its
    /// worktree gone, closes once the child is discarded.
    func discardChildConfirmed(_ pending: PendingChildDiscard) {
        pendingChildDiscard = nil
        let id = pending.id
        runCardAction(id, .discard) { delegation in
            switch await delegation.discardFromCard(id, confirmedNested: pending.nested) {
            case .discarded: .done
            case .nestedChanged(let nested): .reconfirm(nested)
            case .failed(let message): .failed(message)
            }
        } then: { [weak self] result in
            guard let self else { return }
            switch result {
            case .done:
                guard snapshot.sessions.contains(where: { $0.id == id }) else { return }
                delegationCardNotes[id] = nil
                closeSession(id)
            case .reconfirm(let nested):
                delegationCardNotes[id] = nil
                if pendingChildDiscard == nil { pendingChildDiscard = PendingChildDiscard(id: id, title: pending.title, branch: pending.branch, nested: nested) }
            case .refused, .failed:
                break
            }
        }
    }

    /// Runs one card action of the child `id`, one at a time per child, and
    /// keeps its answer for the card.
    private func runCardAction(_ id: String, _ action: DelegationCardAction, _ work: @escaping @Sendable (DelegationCoordinator) async -> DelegationCardNote.Result,
                               then: @escaping (DelegationCardNote.Result) -> Void = { _ in }) {
        guard let delegation, !delegationCardBusy.contains(id) else { return }
        delegationCardBusy.insert(id)
        delegationCardNotes[id] = nil
        Task { [weak self] in
            let result = await work(delegation)
            guard let self else { return }
            delegationCardBusy.remove(id)
            delegationCardNotes[id] = DelegationCardNote(action: action, result: result)
            then(result)
        }
    }

    // MARK: DelegationHost

    /// The child's Claude pane, as a tab beside its parent: in the mode the
    /// parent asked for, never the last-used pane's, linked to its parent and
    /// working in its worktree folder. Its title is its task, kept as the
    /// pane's name. The link is saved before its first run.
    func createPane(_ pane: DelegationChildPane) async -> Bool {
        guard !ending, isLoaded, snapshot.sessions.count < 128, !snapshot.sessions.contains(where: { $0.id == pane.sessionId }),
              let parent = snapshot.sessions.first(where: { $0.id == pane.parentSessionId }), !closingSessions.contains(parent.id),
              snapshot.workspaces.contains(where: { $0.id == parent.workspaceId }) else { return false }
        let file = await delegation?.file
        let task = file?.copy(childId: pane.sessionId, kind: .task)?.text
        var session = DelegationPanes.childSession(pane, workspaceId: parent.workspaceId, title: task.flatMap(PaneTitle.shortened) ?? ProviderOptions.label("claude"))
        session.titleMode = "fixed"
        // Checked again: the pane list may have changed while the task was read.
        guard !ending, !snapshot.sessions.contains(where: { $0.id == session.id }), snapshot.sessions.contains(where: { $0.id == parent.id }) else { return false }
        reconcilePaneLayout(parent.workspaceId)
        let root = layoutForWorkspace(parent.workspaceId)
        let group = root?.group(containing: parent.id)
        // A new tab is selected on insert; the group keeps showing what it showed.
        guard let next = PaneLayouts.selecting(root: PaneLayouts.inserting(root: root, sessionId: session.id, targetGroupId: group?.id, placement: "tab"),
                                               id: group?.selectedSessionId ?? parent.id),
              next.group(containing: session.id) != nil else { return false }
        let mode = paneLayoutMode(parent.workspaceId)
        snapshot.sessions.append(session)
        savePaneLayout(next, workspaceId: parent.workspaceId)
        if mode != "focus" { setPaneLayoutMode(next.kind == "split" ? "custom" : "tabs", workspaceId: parent.workspaceId) }
        do { try await flush() } catch { NSLog("MightyClaude could not save a new child pane: %@", error.localizedDescription) }
        return snapshot.sessions.contains { $0.id == session.id }
    }

    /// A run with `input` in the pane, the way a send starts one: its id, or
    /// nil when the pane cannot run now (a child without its worktree among them).
    /// Its words are delegation's, so they never title the pane.
    func startRun(sessionId: String, input: String) async -> String? {
        await startDelivered(sessionId, input: input, attachments: [], restoringDraft: nil, titleFrom: "")
    }

    /// `input` steered into the pane's running Claude run, shown in its
    /// transcript like a steered send, or started as its next run.
    func deliver(_ input: String, to sessionId: String, route: DeliveryRoute) async -> String? {
        switch route {
        case .queue:
            return await startRun(sessionId: sessionId, input: input)
        case .steer:
            guard !ending, !closingSessions.contains(sessionId), let session = snapshot.sessions.first(where: { $0.id == sessionId }),
                  session.status == "running", canSteer(session), let runId = delegationRuns.runId(sessionId) else { return nil }
            guard await steerRunningTurn(sessionId, text: input) else { return nil }
            updateSession(sessionId) { $0.logs.append(LogEntry(kind: "user", text: input)); $0.logs = TranscriptRetention.trimmed($0.logs) }
            return runId
        }
    }

    /// The pane as delegation reads it. One idle after a normal finish whose
    /// start would be refused now (a CLI update, a Claude model reset, a
    /// child without its worktree…) reads as idle, so what comes for it is
    /// held as a row of its queued list instead of waiting unseen for a run
    /// it cannot take. Every notice and follow-up is a valid run input, so
    /// the start is asked about with a stand-in for its words.
    func paneState(sessionId: String) async -> DelegationPaneState? {
        guard !closingSessions.contains(sessionId), let session = snapshot.sessions.first(where: { $0.id == sessionId }),
              let workspace = snapshot.workspaces.first(where: { $0.id == session.workspaceId }) else { return nil }
        let running = session.status == "running" || pendingRuns.contains(sessionId)
        let activity = delegationRuns.activity(sessionId, running: running)
        let refused = activity == .finished && startRefusal(session, in: workspace, input: "notice", attachments: []) != nil
        return DelegationPanes.paneState(of: session, in: workspace, runId: delegationRuns.runId(sessionId), activity: activity, startRefused: refused)
    }

    func stopRun(sessionId: String) async { await stop(sessionId) }
}
