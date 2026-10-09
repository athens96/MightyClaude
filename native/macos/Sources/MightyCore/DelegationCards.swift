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

/// A human's merge and undo from a child's card, and the nested worktrees its
/// discard confirmation names (macOS only). The discard itself is
/// ``discardChild(_:)``. The tools never reach these.
extension DelegationCoordinator {
    /// A human's merge of the child from its card, by
    /// ``ChildMerge/cardMerge(_:parentCheckout:)`` in the parent's checkout
    /// recorded when the child was made, else the parent pane's folder. It
    /// takes its turn with the tool's merges, so two never run git in one
    /// checkout at once. A refusal carries one reason code and changes
    /// nothing; with no parent checkout known it is `branch_not_checked_out`.
    /// A merge writes its MergeRecord and moves the child to merged; should
    /// the child have moved on meanwhile, the record is written all the
    /// same, so the merge can still be undone from the card.
    public func mergeFromCard(_ id: String) async -> ChildMergeOutcome {
        await takeMergeTurn()
        defer { passMergeTurn() }
        guard let child = file.children.first(where: { $0.id == id }) else { return .failed("No child has this id.") }
        guard let checkout = await parentCheckout(of: child) else { return .refused(.branchNotCheckedOut) }
        let outcome = await ChildMerge.cardMerge(child, parentCheckout: checkout)
        guard case .merged(let record) = outcome else { return outcome }
        if !file.recordMerge(record), file.children.contains(where: { $0.id == record.childId }) { file.merges.append(record) }
        let context = await pruneContext()
        try? commit(file, context: context)
        return outcome
    }

    /// A human's undo of the child's last merge from its card, by
    /// ``ChildMerge/undo(_:of:parentCheckout:parentActivity:)``. The parent
    /// pane's activity decides `parent_busy`, and a closed parent pane counts
    /// as idle. A refusal carries one reason code and changes nothing. An
    /// undo takes the merge record out of the file: a merged child is
    /// reported again, and a closed one stays closed, its branch made again
    /// at its recorded head.
    public func undoMergeFromCard(_ id: String) async -> ChildUndoOutcome {
        await takeMergeTurn()
        defer { passMergeTurn() }
        guard let child = file.children.first(where: { $0.id == id }), let record = file.merges.last(where: { $0.childId == id }) else {
            return .failed("This child has no merge to undo.")
        }
        guard let checkout = await parentCheckout(of: child) else { return .refused(.branchNotCheckedOut) }
        let activity = await host.paneState(sessionId: child.parentSessionId)?.activity
        // As it is now: the child may have moved on while the host answered.
        guard let current = file.children.first(where: { $0.id == id }) else { return .failed("This child has no merge to undo.") }
        let outcome = await ChildMerge.undo(record, of: current, parentCheckout: checkout, parentActivity: activity)
        guard case .undone(let undone, _) = outcome else { return outcome }
        // Undone on disk, so its record goes whatever the child did meanwhile.
        if !file.recordUndo(undone), let index = file.merges.lastIndex(of: undone) { file.merges.remove(at: index) }
        let context = await pruneContext()
        try? commit(file, context: context)
        return outcome
    }

    /// The registered worktrees nested in the child's, deepest first, for the
    /// discard confirmation to name; nil when they could not be listed (the
    /// parent's checkout is not known, or git failed).
    public func nestedWorktrees(of id: String) async -> [String]? {
        guard let child = file.children.first(where: { $0.id == id }), let checkout = await parentCheckout(of: child) else { return nil }
        return await ChildCleanup.nestedWorktrees(of: child, parentCheckout: checkout)
    }
}
