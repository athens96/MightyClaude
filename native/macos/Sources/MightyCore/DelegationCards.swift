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
    /// it is reported, undo while its last merge can be undone, and discard
    /// until it is discarded.
    public var cardActions: [DelegationCardAction] {
        var actions: [DelegationCardAction] = []
        if state == .reported { actions.append(.merge) }
        if canUndo { actions.append(.undo) }
        if state != .discarded { actions.append(.discard) }
        return actions
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

    private func cardMerge(_ id: String) async -> ChildMergeOutcome {
        guard let child = file.children.first(where: { $0.id == id }) else { return .failed("No child has this id.") }
        cardPanes = [child.parentSessionId, child.id]
        guard let checkout = await parentCheckout(of: child) else { return .refused(.branchNotCheckedOut) }
        let outcome = await ChildMerge.cardMerge(child, parentCheckout: checkout)
        guard case .merged(let record) = outcome else { return outcome }
        if !file.recordMerge(record), file.children.contains(where: { $0.id == record.childId }) { file.merges.append(record) }
        let context = await pruneContext()
        try? commit(file, context: context)
        return outcome
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
        guard let child = file.children.first(where: { $0.id == id }), file.undoableMerge(of: id) != nil else { return .failed("This child has no merge to undo.") }
        cardPanes = [child.parentSessionId]
        guard let checkout = await parentCheckout(of: child) else { return .refused(.branchNotCheckedOut) }
        let activity = await host.paneState(sessionId: child.parentSessionId)?.activity
        // As it is now: the child may have moved on while the host answered.
        guard let current = file.children.first(where: { $0.id == id }), let record = file.undoableMerge(of: id) else { return .failed("This child has no merge to undo.") }
        let outcome = await ChildMerge.undo(record, of: current, parentCheckout: checkout, parentActivity: activity)
        guard case .undone(let undone, _) = outcome else { return outcome }
        // Undone on disk, so its record goes whatever the child did meanwhile.
        if !file.recordUndo(undone), let index = file.merges.lastIndex(of: undone) { file.merges.remove(at: index) }
        let context = await pruneContext()
        try? commit(file, context: context)
        return outcome
    }

    /// A human's discard from the child's card once its confirmation named
    /// `confirmed` as the worktrees nested in the child's (nil when it could
    /// not list them). The child's run is stopped first, so nothing nests
    /// another worktree in it, and the nested worktrees are listed again:
    /// when one is there that the confirmation did not name, nothing is
    /// removed and the answer names them all, to ask again. Otherwise
    /// ``discardChild(_:)`` removes the child's worktree, those nested in it
    /// and its branch by force, in its turn with the merges and undos.
    public func discardFromCard(_ id: String, confirmedNested confirmed: [String]?) async -> ChildCardDiscardOutcome {
        guard file.children.contains(where: { $0.id == id && $0.state != .discarded }) else { return .failed("No child with this id can be discarded.") }
        guard !starting.contains(id) else { return .failed("The child is still starting, so nothing was removed. Discard it once its start has finished.") }
        await host.stopRun(sessionId: id)
        await takeMergeTurn()
        let outcome = await cardDiscard(id, confirmed: confirmed)
        passMergeTurn()
        return outcome
    }

    private func cardDiscard(_ id: String, confirmed: [String]?) async -> ChildCardDiscardOutcome {
        guard let nested = await nestedWorktrees(of: id) else { return .failed("The worktrees nested in the child's could not be listed, so nothing was removed.") }
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
