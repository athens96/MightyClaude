import Foundation

/// What a human can do from a delegated child's card (macOS only).
public enum DelegationCardAction: String, Sendable, CaseIterable {
    /// Merge the reported head into the recorded parent branch.
    case merge
    /// Undo the child's last merge.
    case undo
    /// Remove the child's worktree and branch by force, after a confirmation.
    case discard
}

extension DelegationChildRow {
    /// The actions the child's card offers now, in this order: merge while
    /// it is reported, or still working (the merge then waits for its report,
    /// ``DelegationMergeQueue``), undo while its last merge can be undone,
    /// and discard until it is discarded.
    public var cardActions: [DelegationCardAction] {
        var actions: [DelegationCardAction] = []
        if [.reported, .running, .waiting].contains(state) { actions.append(.merge) }
        if canUndo { actions.append(.undo) }
        if state != .discarded { actions.append(.discard) }
        return actions
    }
}

/// What a human's merge from a child's card waits on before it goes through
/// by itself (macOS only).
public enum DelegationMergeWait: String, Sendable, CaseIterable {
    /// The child is still working: it merges once the child reports.
    case childRunning
    /// The parent pane is running: it merges once that run ends.
    case parentRunning
    /// The parent's checkout has uncommitted changes to tracked files: it
    /// merges once they are committed or put back.
    case parentChanges
}

/// A human's merge from a child's card does not give up on what passes: it
/// is queued and tried again until it goes through, is refused for good or
/// the child can no longer merge (macOS only). A merge conflict asks the
/// child to resolve it, and its next report merges.
public enum DelegationMergeQueue {
    public enum Step: Sendable, Equatable {
        /// Try the merge now.
        case attempt
        /// Keep it queued, waiting on this.
        case wait(DelegationMergeWait)
        /// Take it out of the queue: the child ended without a new report,
        /// was merged, closed, failed or discarded.
        case drop
    }

    /// What a queued merge of a child in `state` does now; `parentRunning`
    /// is whether its parent pane runs (a closed parent pane does not).
    public static func step(state: ChildState, parentRunning: Bool) -> Step {
        switch state {
        case .creating, .running, .waiting: .wait(.childRunning)
        case .reported: parentRunning ? .wait(.parentRunning) : .attempt
        case .ended, .interrupted, .merged, .failed, .closed, .discarded: .drop
        }
    }

    /// What a merge refused for `reason` waits on while it stays queued; nil
    /// when the refusal is for good (a conflict is the child's to resolve).
    public static func wait(after reason: DelegationReasonCode) -> DelegationMergeWait? {
        switch reason {
        case .trackedChanges: .parentChanges
        case .parentBusy: .parentRunning
        default: nil
        }
    }
}

/// The answer to a human's discard from a child's card, once confirmed (macOS only).
public enum ChildCardDiscardOutcome: Sendable, Equatable {
    /// The child's worktree, the worktrees nested in it and its branch are gone.
    case discarded
    /// More worktrees are nested in the child's now than the confirmation
    /// named, so nothing was removed. These are all of them, deepest first,
    /// for the card to ask again.
    case nestedChanged([String])
    /// Nothing after this step was removed; a later discard goes on from here.
    /// The words are in the app's language, with git's own output kept as it is.
    case failed(String)
}

extension DelegationFile {
    /// The merge a child's card can undo: the child's last one, while the
    /// child is merged, or closed with that merge's head as the head it was
    /// closed at. nil otherwise, so an older merge never comes back to undo.
    public func undoableMerge(of id: String) -> MergeRecord? {
        guard let child = children.first(where: { $0.id == id }), let record = merges.last(where: { $0.childId == id }) else { return nil }
        switch child.state {
        case .merged: return record
        case .closed: return child.closedHead?.lowercased() == record.childHead.lowercased() ? record : nil
        default: return nil
        }
    }
}

/// A human's merge, undo and discard from a child's card, and the nested
/// worktrees its discard confirmation names (macOS only). The tools never
/// reach these.
extension DelegationCoordinator {
    /// A human's merge of the child from its card, by
    /// ``ChildMerge/cardMerge(_:parentCheckout:)`` in the parent's checkout
    /// recorded when the child was made, else the parent pane's folder. It
    /// takes its turn with the tool's merges, so two never run git in one
    /// checkout at once, and while it runs delivery starts no run in the
    /// parent's pane or the child's. A refusal carries one reason code and
    /// changes nothing; with no parent checkout known it is
    /// `branch_not_checked_out`. A merge writes its MergeRecord and moves the
    /// child to merged; should the child have moved on meanwhile, the record
    /// is written all the same.
    public func mergeFromCard(_ id: String) async -> ChildMergeOutcome {
        await takeMergeTurn()
        let outcome = await cardMerge(id)
        cardPanes.removeAll()
        passMergeTurn()
        await deliverPending()
        return outcome
    }

    /// The request for the child `id` to resolve its conflict with the
    /// parent branch (``ChildMerge/conflictPrompt(_:)``), after its card's
    /// merge was refused with `merge_conflict`: once per report, so nil when
    /// the child is not reported or was already asked at this report. A
    /// request that did not start is given back with
    /// ``conflictResolutionNotStarted(_:)``.
    public func conflictResolution(for id: String) -> String? {
        guard let child = file.children.first(where: { $0.id == id }), child.state == .reported,
              conflictAsked[id] != child.reportRevision else { return nil }
        conflictAsked[id] = child.reportRevision
        return ChildMerge.conflictPrompt(child)
    }

    /// The child's pane did not start the request to resolve its conflict,
    /// so the next merge that conflicts at this report asks again.
    public func conflictResolutionNotStarted(_ id: String) { conflictAsked[id] = nil }

    private func cardMerge(_ id: String) async -> ChildMergeOutcome {
        guard let child = file.children.first(where: { $0.id == id }) else { return .failed(L("delegation.card.error.noChild")) }
        cardPanes = [child.parentSessionId, child.id]
        guard let checkout = await parentCheckout(of: child) else { return .refused(.branchNotCheckedOut) }
        guard let child = await followParentBranch(id, in: checkout) else { return .failed(L("delegation.card.error.noChild")) }
        let outcome = await ChildMerge.cardMerge(child, parentCheckout: checkout)
        guard case .merged(let record) = outcome else { return outcome }
        if !file.recordMerge(record), file.children.contains(where: { $0.id == record.childId }) { file.merges.append(record) }
        let context = await pruneContext()
        // The merge stands and stays on record in memory; the human hears
        // that the file could not be saved, and the next save writes it.
        do { try commit(file, context: context) } catch { return .mergedUnsaved(record, Self.unsavedReason(error)) }
        return outcome
    }

    /// The child `id` as it is once its recorded parent branch follows
    /// `checkout`: when another branch checked out there holds the recorded
    /// one's work (``ChildMerge/mergeBranch(of:in:)``), as when the recorded
    /// branch was merged into main, the record names that branch from now on
    /// and is saved (should the disk refuse, the next save writes it). nil
    /// when no child has the id.
    func followParentBranch(_ id: String, in checkout: String) async -> ChildRecord? {
        guard let found = file.children.first(where: { $0.id == id }) else { return nil }
        guard let branch = await ChildMerge.mergeBranch(of: found, in: checkout), branch != found.parentBranch,
              let index = file.children.firstIndex(where: { $0.id == id }), file.children[index].parentBranch == found.parentBranch else {
            return file.children.first { $0.id == id }
        }
        file.children[index].parentBranch = branch
        let child = file.children[index]
        let context = await pruneContext()
        try? commit(file, context: context)
        return child
    }

    /// Why the delegation file was not saved, for a card's note: in the
    /// app's language, with no closing stop, since the note goes on after it.
    static func unsavedReason(_ error: Error) -> String {
        switch error as? DelegationFileError {
        case .full?: L("delegation.card.error.fileFull")
        case .unreadable?: L("delegation.card.error.fileUnreadable")
        case nil: error.localizedDescription.trimmingCharacters(in: CharacterSet(charactersIn: ".。 \n"))
        }
    }

    /// The same in English, for the merge tool's agent.
    static func unsavedReasonForAgent(_ error: Error) -> String {
        switch error as? DelegationFileError {
        case .full?: "the delegation file is at its 4 MiB cap"
        case .unreadable?: "the delegation file on disk cannot be read"
        case nil: error.localizedDescription.trimmingCharacters(in: CharacterSet(charactersIn: ".。 \n"))
        }
    }

    /// A human's undo from the child's card of the merge
    /// ``DelegationFile/undoableMerge(of:)`` names, by
    /// ``ChildMerge/undo(_:of:parentCheckout:parentActivity:)``. The parent
    /// pane's activity decides `parent_busy`, and a closed parent pane counts
    /// as idle; while the undo runs, delivery starts no run in the parent's
    /// pane, so it stays idle throughout. A refusal carries one reason code
    /// and changes nothing. An undo takes the merge record out of the file:
    /// a merged child is reported again, and a closed one stays closed, its
    /// branch made again at its recorded head.
    public func undoMergeFromCard(_ id: String) async -> ChildUndoOutcome {
        await takeMergeTurn()
        let outcome = await cardUndo(id)
        cardPanes.removeAll()
        passMergeTurn()
        await deliverPending()
        return outcome
    }

    private func cardUndo(_ id: String) async -> ChildUndoOutcome {
        guard let child = file.children.first(where: { $0.id == id }), file.undoableMerge(of: id) != nil else { return .failed(L("delegation.card.error.noMergeToUndo")) }
        cardPanes = [child.parentSessionId]
        guard let checkout = await parentCheckout(of: child) else { return .refused(.branchNotCheckedOut) }
        let activity = await host.paneState(sessionId: child.parentSessionId)?.activity
        // As it is now: the child may have moved on while the host answered.
        guard let current = file.children.first(where: { $0.id == id }), let record = file.undoableMerge(of: id) else { return .failed(L("delegation.card.error.noMergeToUndo")) }
        let outcome = await ChildMerge.undo(record, of: current, parentCheckout: checkout, parentActivity: activity)
        guard case .undone(let undone, let restored) = outcome else { return outcome }
        // Undone on disk, so its record goes whatever the child did meanwhile.
        if !file.recordUndo(undone), let index = file.merges.lastIndex(of: undone) { file.merges.remove(at: index) }
        let context = await pruneContext()
        do { try commit(file, context: context) } catch { return .undoneUnsaved(undone, restoredBranch: restored, Self.unsavedReason(error)) }
        return outcome
    }

    /// A human's discard from the child's card once its confirmation named
    /// `confirmed` as the worktrees nested in the child's (nil when it could
    /// not list them). The nested worktrees are listed before anything else:
    /// when they cannot be, the answer is a failure and nothing is stopped or
    /// removed. Then the child's run is stopped, so nothing nests another
    /// worktree in it, and the nested worktrees are listed again: when one is
    /// there that the confirmation did not name, nothing is removed and the
    /// answer names them all, to ask again. Otherwise ``discardChild(_:)``
    /// removes the child's worktree, those nested in it and its branch by
    /// force, in its turn with the merges and undos.
    public func discardFromCard(_ id: String, confirmedNested confirmed: [String]?) async -> ChildCardDiscardOutcome {
        guard file.children.contains(where: { $0.id == id && $0.state != .discarded }) else { return .failed(L("delegation.card.error.cannotDiscard")) }
        guard !starting.contains(id) else { return .failed(L("delegation.card.error.stillStarting")) }
        guard await nestedWorktrees(of: id) != nil else { return .failed(L("delegation.card.error.nestedUnlisted")) }
        await host.stopRun(sessionId: id)
        await takeMergeTurn()
        let outcome = await cardDiscard(id, confirmed: confirmed)
        passMergeTurn()
        return outcome
    }

    private func cardDiscard(_ id: String, confirmed: [String]?) async -> ChildCardDiscardOutcome {
        guard let nested = await nestedWorktrees(of: id) else { return .failed(L("delegation.card.error.nestedUnlisted")) }
        guard Set(nested).isSubset(of: Set(confirmed ?? [])) else { return .nestedChanged(nested) }
        switch await discardChild(id) {
        case .discarded: return .discarded
        case .failed(let message): return .failed(message)
        }
    }

    /// The registered worktrees nested in the child's, deepest first, for the
    /// discard confirmation to name; nil when they could not be listed (the
    /// parent's checkout is not known, or git failed).
    public func nestedWorktrees(of id: String) async -> [String]? {
        guard let child = file.children.first(where: { $0.id == id }), let checkout = await parentCheckout(of: child) else { return nil }
        return await ChildCleanup.nestedWorktrees(of: child, parentCheckout: checkout)
    }
}
