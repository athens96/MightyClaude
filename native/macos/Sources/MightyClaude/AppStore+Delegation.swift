import Foundation
import MightyCore

/// The last answer a child's card got, shown on the card until its next action.
struct DelegationCardNote: Equatable {
    enum Result: Equatable, Sendable {
        case done
        /// Done in git, but the delegation file could not be saved (why); the
        /// next save writes it.
        case doneUnsaved(String)
        /// Refused with this reason; nothing changed.
        case refused(DelegationReasonCode)
        case failed(String)
        /// More worktrees are nested in the child's than its discard's
        /// confirmation named: nothing was removed, and it asks again with these.
        case reconfirm([String])
        /// The merge was refused with `merge_conflict`, and the child's pane
        /// was asked to resolve the conflict and report again.
        case conflictSent
        /// The merge was refused with `merge_conflict`, and the child's pane
        /// took the request to resolve it but did not start it.
        case conflictNotStarted
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

/// The app side of parent → child delegation between agent panes (macOS
/// only; ``DelegationProviders`` says which): AppStore is the coordinator's ``DelegationHost``. The coordinator
/// owns the records, the tools, delivery and the git actions; the store only
/// makes panes, starts and steers runs and reports what its panes do.
extension AppStore {
    /// Makes the coordinator from the profile's delegation file, whether or
    /// not the delegation switch is on: switching it off only detaches the
    /// delegation server, so existing children keep working. Worktrees go
    /// under `~/.mightyclaude/worktrees`, or the profile for smoke runs;
    /// `freeBytes` stands in for the disk's free space only in the delegation smoke.
    func startDelegation(freeBytes: (@Sendable (URL) -> Int64?)? = nil) {
        guard delegation == nil else { return }
        let root = smokeTesting ? dataDirectory.appendingPathComponent("worktrees", isDirectory: true) : ChildWorktreeMaker.defaultRoot
        let maker = freeBytes.map { ChildWorktreeMaker(root: root, freeBytes: $0) } ?? ChildWorktreeMaker(root: root)
        do {
            let coordinator = try DelegationCoordinator(store: DelegationFileStore(directory: dataDirectory), host: self, worktrees: maker)
            delegation = coordinator
            // A child's end is recorded (the app's commit of its work among
            // it) before its queue or delivery may start its next run.
            delegationRunEvents = DelegationRunEventPump { [weak self] event in
                switch event {
                case .started(let id, let runId): await coordinator.childRunStarted(id, runId: runId)
                case .ended(let id, let runId, let end):
                    await coordinator.childRunEnded(id, runId: runId, end: end)
                    await self?.childRunEndRecorded(id)
                }
            }
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
        if snapshot.sessions.first(where: { $0.id == id })?.parentSessionId != nil, let delegationRunEvents {
            // Its queue and delivery wait until the end is recorded (``childRunEndRecorded(_:)``).
            delegation?.endingRuns.begin(id)
            delegationRunEvents.send(.ended(childId: id, runId: ended.runId, end: ended.end))
        }
        offerDelegationPending()
    }

    /// The coordinator recorded an end of the child `id`'s run: what its
    /// queue held back meanwhile may run now.
    func childRunEndRecorded(_ id: String) {
        guard !ending, let session = snapshot.sessions.first(where: { $0.id == id }), delegation?.endingRuns.contains(id) != true else { return }
        settleQueue(id, status: session.status)
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
        /// The user's own send on this Mac, which the pane's views move to (natural submit B2).
        var isOwn: Bool { if case .phone = self { false } else { true } }
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
                await self?.startDelivered(id, input: whole, attachments: attachments, restoringDraft: origin.draft, queued: origin.queued, titleFrom: input, own: origin.isOwn)
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
    /// `own`: the user's own request (`start`).
    private func startDelivered(_ id: String, input: String, attachments: [RunAttachment], restoringDraft: String?, queued: QueuedInput? = nil, titleFrom: String, own: Bool) async -> String? {
        guard !ending, let session = snapshot.sessions.first(where: { $0.id == id }),
              let workspace = snapshot.workspaces.first(where: { $0.id == session.workspaceId }),
              start(id, session: session, workspace: workspace, input: input, attachments: attachments,
                    restoringDraft: (drafts[id] ?? "").isEmpty ? restoringDraft : nil, queued: queued, titleFrom: titleFrom, own: own) else { return nil }
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
            switch deferInput(id, session: session, workspace: workspace, item: item) {
            case .queued: return .queued
            case .refused:
                giveBack(id, attachments: attachments, from: origin)
                return .dropped
            }
        }
        if start(id, session: session, workspace: workspace, input: input, attachments: attachments,
                 restoringDraft: (drafts[id] ?? "").isEmpty ? origin.draft : nil, queued: origin.queued, own: origin.isOwn) { return .started }
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

    // MARK: The composer's new worktree

    /// The folder a top-level agent pane's runs work in, where its children's
    /// worktrees branch from; nil for a pane without its workspace.
    func worktreeHostFolder(_ session: RunSession) -> String? {
        // A remote workspace's folder is on its host: no worktree branches here.
        snapshot.workspaces.first { $0.id == session.workspaceId && $0.remote == nil }.map { DelegationPanes.runWorkspace(for: session, in: $0).path }
    }

    /// A delegated child's worktree when its runs work in a folder inside it,
    /// for a Codex or Antigravity run to write REPORT.md there; nil for any
    /// other pane.
    func delegationWorktreeRoot(_ session: RunSession) -> String? {
        guard session.parentSessionId != nil, ["codex", "gemini"].contains(session.provider), let folder = session.workingFolder,
              let root = delegation?.worktrees.worktreePath(for: session.id), folder != root, folder.hasPrefix(root + "/") else { return nil }
        return root
    }

    /// Checks again whether `session`'s folder can host a child worktree, for
    /// ``composerSecondSend(_:)`` and the worktree button's menu: a top-level
    /// Claude, Codex or Antigravity pane whose child, of its own agent or
    /// another, could change files, with delegation on. One check per folder at a time.
    func refreshWorktreeReadiness(for session: RunSession) {
        guard let delegation, DelegationSwitch.isOn(), session.parentSessionId == nil, DelegationProviders.mayHaveHumanChild(kind: session.kind, provider: session.provider, mode: session.settings.permissionMode),
              let folder = worktreeHostFolder(session), worktreeReadinessChecks.insert(folder).inserted else { return }
        Task { [weak self] in
            let ready = await delegation.worktrees.check(workspace: folder).canHostWorktree
            guard let self else { return }
            worktreeReadinessChecks.remove(folder)
            if worktreeReadiness[folder] != ready { worktreeReadiness[folder] = ready }
        }
    }

    /// What the worktree button's menu on the agent pane `session` offers:
    /// Claude, Codex and Antigravity, each with why it cannot run the text
    /// now. Read from what the app already knows (its CLI search and the
    /// sign-in status it last read), never by asking a CLI.
    func worktreeChildChoices(_ session: RunSession) -> [DelegationChildChoice] {
        DelegationProviders.humanChildChoices(parent: session.provider, parentMode: session.settings.permissionMode, planFirst: styleLaunchesInPlanMode(session),
                                              openHumanChildren: DelegationSidebar.openHumanChildCount(of: session.id, children: delegationChildren),
                                              availability: agentAvailabilityNow)
    }

    /// Whether the `provider` agent's CLI can run a child, as the app last
    /// learnt it: found by its CLI search (``refreshRuntime()``), and not
    /// signed out by the account status Settings last read.
    func agentAvailabilityNow(_ provider: String) -> DelegationAgentAvailability {
        if smokeProvidersTreatedAvailable.contains(provider) { return .ready }
        return DelegationProviders.availability(installed: runtime?.providers?.first { $0.id == provider }?.available, account: cliAccounts[provider])
    }

    /// The composer's second send on the busy agent pane `id`
    /// (``ComposerSecondSend/worktree``), or the worktree button's menu
    /// naming the child's `agent`: the text and its attachments run now
    /// in a new child pane, in its own worktree beside the pane, made by
    /// delegation as delegate makes one, in plan mode when the pane's style
    /// starts every request so and the child runs the pane's agent. The pane's run goes on undisturbed, and the
    /// new child shows at once. When no child can be made, or it cannot
    /// start after all, the pane's transcript says why and the text goes on
    /// by ``runAfterNoWorktree(_:input:attachments:draft:reason:)``; a text
    /// meant for another agent goes back to the composer instead
    /// (``ComposerSecondSend/returnsToComposer(childAgent:parentAgent:)``).
    func runInNewWorktree(_ id: String, input: String, attachments: [RunAttachment], draft: String, agent: String? = nil) {
        guard let delegation, let parent = snapshot.sessions.first(where: { $0.id == id }) else { return }
        let toComposer = ComposerSecondSend.returnsToComposer(childAgent: agent, parentAgent: parent.provider)
        // Taken from the composer now, so a second ⌘Enter cannot send it again.
        drafts[id] = ""
        let submittedIds = Set(attachments.map(\.id))
        attachmentDrafts[id]?.removeAll { submittedIds.contains($0.id) }
        let starter = HumanChildStarter(start: { [weak self] childId, run in
            await self?.startHumanChildRun(childId, run: run, attachments: attachments)
        }, failed: { [weak self] childId in
            await self?.humanChildFailedToStart(childId, parentId: id, input: input, attachments: attachments, draft: draft, toComposer: toComposer)
        })
        let planFirst = styleLaunchesInPlanMode(parent)
        Task { [weak self] in
            let outcome = await delegation.startHumanChild(input, parentId: id, planFirst: planFirst, agent: agent, starter: starter)
            guard let self else { return }
            let reason: String
            switch outcome {
            case .started: return
            case .refused(let code): reason = DelegationText.reason(code)
            case .failed(let message): reason = message
            }
            if toComposer { returnToComposer(id, attachments: attachments, draft: draft, reason: reason) }
            else { runAfterNoWorktree(id, input: input, attachments: attachments, draft: draft, reason: reason) }
        }
    }

    /// The text of a child of another agent that could not be made or
    /// started goes back to the composer with its attachments (a draft typed
    /// meanwhile is kept), and the pane's transcript says why. It never runs in the pane.
    private func returnToComposer(_ id: String, attachments: [RunAttachment], draft: String, reason: String) {
        guard !ending, !closingSessions.contains(id), snapshot.sessions.contains(where: { $0.id == id }) else { return }
        updateSession(id) { $0.logs.append(LogEntry(kind: "system", text: L("delegation.human.returnedToComposer", ["reason": reason]))); $0.logs = TranscriptRetention.trimmed($0.logs) }
        giveBack(id, attachments: attachments, from: .composer(draft: draft))
    }

    /// The first run of a child the human started, with the send's
    /// attachments: its id, or nil when it did not start. A slash command's
    /// note follows as the child's next request.
    private func startHumanChildRun(_ childId: String, run: HumanChildFirstRun, attachments: [RunAttachment]) async -> String? {
        // A ⌘Enter child shown in its tab is moved to like the user's own request;
        // one in the background shows its 전송 중 on its sidebar row (natural submit B2).
        guard let runId = await startDelivered(childId, input: run.input, attachments: attachments, restoringDraft: nil, titleFrom: "", own: paneTabShown(childId)) else { return nil }
        if let note = run.followingNote, !ending, !closingSessions.contains(childId), snapshot.sessions.contains(where: { $0.id == childId }) {
            queuedInputs[childId, default: []].append(QueuedInput(text: note, attachments: []))
            // The command's run already ended: the note runs now.
            if snapshot.sessions.first(where: { $0.id == childId })?.status != "running", !pendingRuns.contains(childId) { runNextQueuedRow(childId) }
        }
        return runId
    }

    /// A child the human started whose worktree, pane or first run could not
    /// be made after all: it stays listed as failed to start, and the text
    /// and its attachments go on from the parent as if no child had been made.
    private func humanChildFailedToStart(_ childId: String, parentId: String, input: String, attachments: [RunAttachment], draft: String, toComposer: Bool) {
        // A failed start puts the run's attachments in the child's composer; they go with the text instead.
        let ids = Set(attachments.map(\.id))
        attachmentDrafts[childId]?.removeAll { ids.contains($0.id) }
        if toComposer { returnToComposer(parentId, attachments: attachments, draft: draft, reason: L("delegation.human.error.startFailed")) }
        else { runAfterNoWorktree(parentId, input: input, attachments: attachments, draft: draft, reason: L("delegation.human.error.startFailed")) }
    }

    /// The text of a second send that no new child runs
    /// (``ComposerSecondSend/choose(kind:provider:mode:isChild:delegationOn:canHostWorktree:)``):
    /// the pane's next request, queued behind the run or started now when the
    /// run ended meanwhile, with any items held for the pane ahead of it; it
    /// never joins the running turn. With the queue
    /// full it goes back to the composer instead. The pane's transcript says
    /// which, and why; a draft typed meanwhile is never touched.
    private func runAfterNoWorktree(_ id: String, input: String, attachments: [RunAttachment], draft: String, reason: String) {
        guard !ending, !closingSessions.contains(id), let session = snapshot.sessions.first(where: { $0.id == id }),
              let workspace = snapshot.workspaces.first(where: { $0.id == session.workspaceId }) else { return }
        let busy = session.status == "running" || pendingRuns.contains(id)
        func note(_ key: String) {
            updateSession(id) { $0.logs.append(LogEntry(kind: "system", text: L(key, ["reason": reason]))); $0.logs = TranscriptRetention.trimmed($0.logs) }
        }
        guard (queuedInputs[id]?.count ?? 0) < QueuedInput.maximumItems else {
            note("delegation.human.returned")
            error = L("queue.fullCount", ["count": "\(QueuedInput.maximumItems)"])
            giveBack(id, attachments: attachments, from: .composer(draft: draft))
            return
        }
        note("delegation.human.queued")
        let item = QueuedInput(text: input, attachments: attachments, permissionModeOverride: BackgroundQueuePolicy.queuedOverride(launchesInPlan: styleLaunchesInPlanMode(session)))
        if busy {
            deferInput(id, session: session, workspace: workspace, item: item, takingDraft: false)
            return
        }
        let waiting = queuedInputs[id]?.isEmpty == false
        queuedInputs[id, default: []].append(item)
        // The run ended meanwhile: it is the next request now. A queue paused
        // after an error stays paused, this text behind it.
        if !waiting { runNextQueuedRow(id) }
    }

    // MARK: Closing panes and removing workspaces

    /// A human's close of the pane `id` from the Mac: a parent pane with open
    /// children asks first, in the view layer, and then closes only the
    /// parent; any other pane closes now. The phone's close never asks.
    func requestCloseSession(_ id: String) {
        // A pane already closing, a discarded child's among them, is no open child.
        let open = DelegationSidebar.openChildCount(of: id, children: delegationChildren, sessions: snapshot.sessions.filter { !closingSessions.contains($0.id) })
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
        guard let delegation, workspace.remote == nil else { return nil }
        let panes = Set(snapshot.sessions.filter { $0.workspaceId == workspace.id }.map(\.id))
        return await delegation.workspaceRemovalRefusal(paneIds: panes, folder: workspace.path)
    }

    // MARK: Child cards

    /// The human's merge from the child's card. Once it went through, the
    /// human is offered to discard the child right away: its work is in the
    /// parent's branch, so only its worktree, its branch and the undo go.
    /// When it conflicts, the child's pane is asked once per report to bring
    /// the parent branch in, resolve the conflict and report again.
    func mergeChildFromCard(_ id: String) {
        runCardAction(id, .merge) { delegation in
            switch await delegation.mergeFromCard(id) {
            case .merged: .done
            case .mergedUnsaved(_, let error): .doneUnsaved(error)
            case .refused(let reason): .refused(reason)
            case .failed(let message): .failed(message)
            }
        } then: { [weak self] result in
            guard let self else { return }
            switch result {
            case .done: offerDiscardAfterMerge(id)
            case .refused(.mergeConflict): askChildToResolveConflict(id)
            default: break
            }
        }
    }

    /// Starts the request for the child `id` to resolve its conflict with the
    /// parent branch as its pane's next run, once per report
    /// (``DelegationCoordinator/conflictResolution(for:)``), and the card says
    /// so. A pane that would refuse the start now (busy, a CLI update, a model
    /// reset, its worktree gone…) is not asked: the refusal stays on the card
    /// and the next merge that conflicts asks again. A start that was taken
    /// but did not reach the CLI may already have ended the child's report,
    /// so the card then says the request did not start. A child that cannot
    /// commit in its permission mode now (a Codex or Antigravity child
    /// outside fullAccess) cannot merge either, so it is not asked.
    private func askChildToResolveConflict(_ id: String) {
        guard let delegation, let session = snapshot.sessions.first(where: { $0.id == id }), !delegationCardBusy.contains(id),
              DelegationProviders.canCommit(provider: session.provider, mode: session.settings.permissionMode),
              let workspace = snapshot.workspaces.first(where: { $0.id == session.workspaceId }),
              startRefusal(session, in: workspace, input: "conflict", attachments: []) == nil else { return }
        delegationCardBusy.insert(id)
        Task { [weak self] in
            let text = await delegation.conflictResolution(for: id)
            guard let self else { return }
            guard let text else { delegationCardBusy.remove(id); return }
            let runId = await startRun(sessionId: id, input: text)
            delegationCardBusy.remove(id)
            let refused = DelegationCardNote(action: .merge, result: .refused(.mergeConflict))
            guard runId != nil else {
                await delegation.conflictResolutionNotStarted(id)
                if delegationCardNotes[id] == refused { delegationCardNotes[id] = DelegationCardNote(action: .merge, result: .conflictNotStarted) }
                return
            }
            if delegationCardNotes[id] == refused { delegationCardNotes[id] = DelegationCardNote(action: .merge, result: .conflictSent) }
        }
    }

    /// Asks whether the merged child goes now, naming the worktrees nested in
    /// its own. Nothing is asked when those cannot be listed (the card's own
    /// discard says why) or another confirmation is already up.
    private func offerDiscardAfterMerge(_ id: String) {
        guard let delegation, pendingMergedDiscard == nil, pendingChildDiscard == nil,
              let row = delegationChildren.first(where: { $0.id == id }) else { return }
        let title = snapshot.sessions.first(where: { $0.id == id })?.title ?? row.task ?? L("delegation.child.untitled")
        Task { [weak self] in
            guard let nested = await delegation.nestedWorktrees(of: id), let self,
                  pendingMergedDiscard == nil, pendingChildDiscard == nil else { return }
            pendingMergedDiscard = PendingChildDiscard(id: id, title: title, branch: ChildRecord.branchName(for: id), nested: nested)
        }
    }

    /// The discard the human chose right after a merge.
    func discardMergedChild(_ pending: PendingChildDiscard) {
        pendingMergedDiscard = nil
        discardChildConfirmed(pending)
    }

    /// The human's undo of the child's last merge from its card.
    func undoChildMergeFromCard(_ id: String) {
        runCardAction(id, .undo) { delegation in
            switch await delegation.undoMergeFromCard(id) {
            case .undone: .done
            case .undoneUnsaved(_, _, let error): .doneUnsaved(error)
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
            case .doneUnsaved, .refused, .failed, .conflictSent, .conflictNotStarted:
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

    /// The child's pane of its own agent, as a tab beside its parent: in the mode the
    /// parent asked for, never the last-used pane's, linked to its parent and
    /// working in its worktree folder. Its title is its task, kept as the
    /// pane's name. The link is saved before its first run. It shows its
    /// requests in its parent's view (basic or Mighty; diagram, timeline or
    /// pipeline). A child of its parent's agent that the human started takes
    /// its parent's model and Mighty style; a child of another agent takes
    /// the model and effort that agent's last-used pane has, as a new pane of
    /// it would. A child the human started is selected and focused, unless
    /// the human has moved on or is typing in the parent.
    func createPane(_ pane: DelegationChildPane) async -> Bool {
        guard !ending, isLoaded, snapshot.sessions.count < 128, !snapshot.sessions.contains(where: { $0.id == pane.sessionId }),
              let parent = snapshot.sessions.first(where: { $0.id == pane.parentSessionId }), !closingSessions.contains(parent.id),
              snapshot.workspaces.contains(where: { $0.id == parent.workspaceId }) else { return false }
        let file = await delegation?.file
        let task = file?.copy(childId: pane.sessionId, kind: .task)?.text
        let agentDefaults = parent.provider == pane.provider ? nil : RunSession.template(kind: SessionKind.claude, provider: pane.provider, in: snapshot.sessions)
        var session = DelegationPanes.childSession(pane, workspaceId: parent.workspaceId, title: task.flatMap(PaneTitle.shortened) ?? ProviderOptions.label(pane.provider),
                                                   parent: parent, agentDefaults: agentDefaults)
        session.titleMode = "fixed"
        // Checked again: the pane list may have changed while the task was read.
        guard !ending, !snapshot.sessions.contains(where: { $0.id == session.id }), snapshot.sessions.contains(where: { $0.id == parent.id }) else { return false }
        reconcilePaneLayout(parent.workspaceId)
        let root = layoutForWorkspace(parent.workspaceId)
        let group = root?.group(containing: parent.id)
        // The human's own child shows at once while they are still on its
        // parent and not typing there; otherwise the new tab is selected on insert and the group
        // keeps showing what it showed.
        let shows = pane.startedByHuman && snapshot.activeSessionId == parent.id && !showsDashboard && (drafts[parent.id] ?? "").isEmpty
        guard let next = PaneLayouts.selecting(root: PaneLayouts.inserting(root: root, sessionId: session.id, targetGroupId: group?.id, placement: "tab"),
                                               id: shows ? session.id : group?.selectedSessionId ?? parent.id),
              next.group(containing: session.id) != nil else { return false }
        let mode = paneLayoutMode(parent.workspaceId)
        snapshot.sessions.append(session)
        savePaneLayout(next, workspaceId: parent.workspaceId)
        if mode != "focus" { setPaneLayoutMode(next.kind == "split" ? "custom" : "tabs", workspaceId: parent.workspaceId) }
        if shows { selectSession(session.id) }
        do { try await flush() } catch { NSLog("MightyClaude could not save a new child pane: %@", error.localizedDescription) }
        return snapshot.sessions.contains { $0.id == session.id }
    }

    /// A run with `input` in the pane, the way a send starts one: its id, or
    /// nil when the pane cannot run now (a child without its worktree among them).
    /// Its words are delegation's, so they never title the pane.
    func startRun(sessionId: String, input: String) async -> String? {
        await startDelivered(sessionId, input: input, attachments: [], restoringDraft: nil, titleFrom: "", own: false)
    }

    /// A delegation notice or follow-up: steered into the pane's running
    /// Claude run and shown in its transcript, or started as its next run.
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

    func agentAvailability(_ provider: String) async -> DelegationAgentAvailability { agentAvailabilityNow(provider) }

    /// The app's note in the pane's transcript, as a system line.
    func note(_ text: String, in sessionId: String) async {
        guard !closingSessions.contains(sessionId), snapshot.sessions.contains(where: { $0.id == sessionId }) else { return }
        updateSession(sessionId) { $0.logs.append(LogEntry(kind: "system", text: text)); $0.logs = TranscriptRetention.trimmed($0.logs) }
    }
}
