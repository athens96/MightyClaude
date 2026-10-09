import Foundation

/// The answer to a merge of a delegated child (macOS only).
public enum ChildMergeOutcome: Sendable, Equatable {
    /// The recorded parent branch now holds the child's head. The record is
    /// what ``DelegationFile/recordMerge(_:)`` writes.
    case merged(MergeRecord)
    /// Refused before any git write; nothing changed.
    case refused(DelegationReasonCode)
    /// Git was missing or failed. A fast-forward git refuses leaves the parent as it was.
    case failed(String)
}

/// The answer to a human's undo of a merge, from the child's card (macOS only).
public enum ChildUndoOutcome: Sendable, Equatable {
    /// The recorded parent branch, its index and its files are back at the
    /// pre-merge commit; `restoredBranch` says the child's deleted branch was
    /// made again at its recorded head. ``DelegationFile/recordUndo(_:)``
    /// writes it.
    case undone(MergeRecord, restoredBranch: Bool)
    /// Refused before any git write; nothing changed.
    case refused(DelegationReasonCode)
    /// The record is no undoable merge of this child, or git was missing or
    /// failed. The steps already taken are put back.
    case failed(String)
}

/// Merges a delegated child into its recorded parent branch, and undoes it.
public enum ChildMerge {
    /// The longest the one git write may take, well inside the 60 s limit of a tool call.
    public static let fastForwardTimeout: TimeInterval = 30

    /// The parent's merge tool, fast-forward only. `parentCheckout` is the
    /// parent pane's folder. The first rule that applies answers, and every
    /// refusal comes before any git write:
    ///
    /// 1. `not_reported`: the child is not reported (a new run, an end without
    ///    a report or a merge clears it).
    /// 2. `worktree_missing`: the child's worktree is gone.
    /// 3. `head_moved`: `expectedHead` is not the reported head, or the child's
    ///    worktree is no longer on its own branch at that head.
    /// 4. `tracked_changes`: the child's worktree has changes to tracked files.
    /// 5. `branch_not_checked_out`: the parent checkout does not have the
    ///    recorded parent branch checked out.
    /// 6. `tracked_changes`: the parent checkout has changes to tracked files.
    /// 7. `diverged`: the parent branch's head is not an ancestor of the
    ///    expected head, so it cannot fast-forward to it.
    ///
    /// Otherwise its only git write is one `git merge --ff-only` of the
    /// expected head in the parent checkout. The child's branch is only read.
    public static func toolMerge(_ child: ChildRecord, expectedHead: String, parentCheckout: String) async -> ChildMergeOutcome {
        guard child.state == .reported, let reported = child.reportHead else { return .refused(.notReported) }
        guard isFolder(child.worktreePath) else { return .refused(.worktreeMissing) }
        guard DelegationGit.executable != nil else { return .failed("Git is missing.") }
        let expected = expectedHead.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        guard DelegationGit.isObjectName(expected), expected == reported.lowercased() else { return .refused(.headMoved) }

        guard let childBranch = await DelegationGit.run(["symbolic-ref", "-q", "HEAD"], in: child.worktreePath) else { return .failed("Git did not finish.") }
        guard childBranch.exitCode == 0 || childBranch.exitCode == 1 else { return .refused(.worktreeMissing) }
        guard childBranch.exitCode == 0, DelegationGit.line(childBranch.stdout) == "refs/heads/" + child.branch,
              await head(of: child.worktreePath) == expected else { return .refused(.headMoved) }
        switch await hasTrackedChanges(child.worktreePath) {
        case nil: return .failed("Git could not read the child's changes.")
        case true?: return .refused(.trackedChanges)
        case false?: break
        }

        guard let preMerge = await checkedOutHead(of: child.parentBranch, in: parentCheckout) else { return .refused(.branchNotCheckedOut) }
        switch await hasTrackedChanges(parentCheckout) {
        case nil: return .failed("Git could not read the parent's changes.")
        case true?: return .refused(.trackedChanges)
        case false?: break
        }
        guard let ancestor = await DelegationGit.run(["merge-base", "--is-ancestor", preMerge, expected], in: parentCheckout) else { return .failed("Git did not finish.") }
        guard ancestor.exitCode != 1 else { return .refused(.diverged) }
        guard ancestor.exitCode == 0 else { return .failed(DelegationGit.message(ancestor)) }

        guard let merged = await DelegationGit.run(["merge", "--ff-only", "--no-autostash", "--no-stat", "-q", expected], in: parentCheckout, timeout: fastForwardTimeout) else { return .failed("Git did not finish.") }
        guard merged.exitCode == 0 else { return .failed(DelegationGit.message(merged)) }
        guard await head(of: parentCheckout) == expected else { return .failed("The parent branch is not at the merged head.") }
        return .merged(MergeRecord(childId: child.id, kind: .toolFastForward, parentBranch: child.parentBranch, preMergeCommit: preMerge, mergedCommit: expected, childHead: expected))
    }

    /// The longest one git step of a card merge or undo may take. A human
    /// waits on these, not a tool call.
    public static let cardTimeout: TimeInterval = 120

    /// A human's merge from the child's card. The reported head goes into the
    /// recorded parent branch by fast-forward when the branch's head is an
    /// ancestor of it, and otherwise as exactly one merge commit whose parents
    /// are the old parent head and the child's head. `parentCheckout` is the
    /// parent's folder. The first rule that applies answers, and every
    /// refusal comes before any git write:
    ///
    /// 1. `not_reported`: the child is not reported.
    /// 2. `branch_not_checked_out`: the parent checkout does not have the
    ///    recorded parent branch checked out.
    /// 3. `tracked_changes`: the parent checkout has changes to tracked files.
    /// 4. `merge_conflict`: `git merge-tree` finds conflicts between the
    ///    parent's head and the child's.
    ///
    /// The merge commit holds exactly the tree merge-tree checked (made with
    /// `git commit-tree`). The branch, index and files then move to it, or to
    /// the child's head, by one `git merge --ff-only`, which never overwrites
    /// an untracked file. The child's branch is only read.
    public static func cardMerge(_ child: ChildRecord, parentCheckout: String) async -> ChildMergeOutcome {
        guard child.state == .reported, let reported = child.reportHead?.lowercased(), DelegationGit.isObjectName(reported) else { return .refused(.notReported) }
        guard DelegationGit.executable != nil else { return .failed("Git is missing.") }
        guard let preMerge = await checkedOutHead(of: child.parentBranch, in: parentCheckout) else { return .refused(.branchNotCheckedOut) }
        switch await hasTrackedChanges(parentCheckout) {
        case nil: return .failed("Git could not read the parent's changes.")
        case true?: return .refused(.trackedChanges)
        case false?: break
        }
        guard let preview = await DelegationGit.run(["merge-tree", "--write-tree", preMerge, reported], in: parentCheckout, timeout: cardTimeout) else { return .failed("Git did not finish.") }
        guard preview.exitCode != 1 else { return .refused(.mergeConflict) }
        let tree = String(DelegationGit.line(preview.stdout).prefix { $0 != "\n" })
        guard preview.exitCode == 0, DelegationGit.isObjectName(tree) else { return .failed(DelegationGit.message(preview)) }

        guard let ancestor = await DelegationGit.run(["merge-base", "--is-ancestor", preMerge, reported], in: parentCheckout) else { return .failed("Git did not finish.") }
        let target: String, kind: MergeRecord.Kind
        switch ancestor.exitCode {
        case 0: target = reported; kind = .cardFastForward
        case 1:
            let message = "Merge branch '\(child.branch)' into \(child.parentBranch)"
            guard let made = await DelegationGit.run(["commit-tree", tree, "-p", preMerge, "-p", reported, "-m", message], in: parentCheckout, timeout: cardTimeout) else { return .failed("Git did not finish.") }
            let commit = DelegationGit.line(made.stdout)
            guard made.exitCode == 0, DelegationGit.isObjectName(commit) else { return .failed(DelegationGit.message(made)) }
            target = commit; kind = .cardMergeCommit
        default: return .failed(DelegationGit.message(ancestor))
        }
        guard let moved = await DelegationGit.run(["merge", "--ff-only", "--no-autostash", "--no-stat", "-q", target], in: parentCheckout, timeout: cardTimeout) else { return .failed("Git did not finish.") }
        guard moved.exitCode == 0 else { return .failed(DelegationGit.message(moved)) }
        guard await head(of: parentCheckout) == target else { return .failed("The parent branch is not at the merged commit.") }
        return .merged(MergeRecord(childId: child.id, kind: kind, parentBranch: child.parentBranch, preMergeCommit: preMerge, mergedCommit: target, childHead: reported))
    }

    /// A human's undo of `record`, a merge of `child`, from the child's card.
    /// `parentActivity` is the parent pane's, nil when that pane is closed (a
    /// closed parent counts as idle). The child must be merged, or closed
    /// after the merge; any other child, or another child's record, fails with
    /// nothing changed. The first rule that applies answers, and every refusal
    /// comes before any git write:
    ///
    /// 1. `branch_not_checked_out`: the parent checkout does not have the
    ///    recorded parent branch checked out.
    /// 2. `undo_parent_moved`: that branch's head is not the merged commit.
    /// 3. `parent_busy`: the parent pane is running.
    /// 4. `tracked_changes`: the parent checkout has changes to tracked files.
    ///
    /// Otherwise a deleted child branch is made again at its recorded head,
    /// the index and files go back to the pre-merge commit by
    /// `git read-tree -m -u` (never `reset --hard`: untracked files stay, and
    /// one in the way fails the undo), and the branch moves back from the
    /// merged commit by compare-and-swap. A step that fails puts the earlier
    /// ones back.
    public static func undo(_ record: MergeRecord, of child: ChildRecord, parentCheckout: String, parentActivity: DelegationPaneActivity?) async -> ChildUndoOutcome {
        guard record.childId == child.id, child.state.after(.undoMerge) != nil else { return .failed("This child has no merge to undo.") }
        let merged = record.mergedCommit.lowercased(), preMerge = record.preMergeCommit.lowercased(), childHead = record.childHead.lowercased()
        guard [merged, preMerge, childHead].allSatisfy(DelegationGit.isObjectName) else { return .failed("The merge record does not name commits.") }
        guard DelegationGit.executable != nil else { return .failed("Git is missing.") }
        guard let head = await checkedOutHead(of: record.parentBranch, in: parentCheckout) else { return .refused(.branchNotCheckedOut) }
        guard head == merged else { return .refused(.undoParentMoved) }
        guard parentActivity != .running else { return .refused(.parentBusy) }
        switch await hasTrackedChanges(parentCheckout) {
        case nil: return .failed("Git could not read the parent's changes.")
        case true?: return .refused(.trackedChanges)
        case false?: break
        }

        let reason = "mighty: undo the merge of \(child.branch)"
        let childRef = "refs/heads/" + child.branch, parentRef = "refs/heads/" + record.parentBranch
        guard let found = await DelegationGit.run(["rev-parse", "-q", "--verify", childRef], in: parentCheckout), found.exitCode == 0 || found.exitCode == 1 else { return .failed("Git could not read the child's branch.") }
        let restoresBranch = found.exitCode == 1
        if restoresBranch {
            // An empty old value: made only while no such branch exists.
            guard let made = await DelegationGit.run(["update-ref", "-m", reason, childRef, childHead, ""], in: parentCheckout) else { return .failed("Git did not finish.") }
            guard made.exitCode == 0 else { return .failed(DelegationGit.message(made)) }
        }
        func putBranchBack() async { if restoresBranch { _ = await DelegationGit.run(["update-ref", "-m", reason, "-d", childRef, childHead], in: parentCheckout) } }

        // Fresh file stamps, so read-tree compares what the files hold.
        _ = await DelegationGit.run(["update-index", "-q", "--refresh"], in: parentCheckout, timeout: cardTimeout)
        guard let files = await DelegationGit.run(["read-tree", "-m", "-u", merged, preMerge], in: parentCheckout, timeout: cardTimeout), files.exitCode == 0 else {
            await putBranchBack(); return .failed("Git could not put the parent's files back; nothing changed.")
        }
        guard let moved = await DelegationGit.run(["update-ref", "-m", reason, parentRef, preMerge, merged], in: parentCheckout), moved.exitCode == 0 else {
            _ = await DelegationGit.run(["read-tree", "-m", "-u", preMerge, merged], in: parentCheckout, timeout: cardTimeout)
            await putBranchBack(); return .failed("The parent branch moved during the undo; nothing changed.")
        }
        return .undone(record, restoredBranch: restoresBranch)
    }

    /// The head of `branch` when `folder` is a checkout with that branch checked out, else nil.
    static func checkedOutHead(of branch: String, in folder: String) async -> String? {
        guard folder.hasPrefix("/"), !folder.contains("\0"), isFolder(folder),
              let current = await DelegationGit.run(["symbolic-ref", "-q", "HEAD"], in: folder),
              current.exitCode == 0, DelegationGit.line(current.stdout) == "refs/heads/" + branch else { return nil }
        return await head(of: folder)
    }

    /// The commit checked out in `folder`, or nil.
    static func head(of folder: String) async -> String? {
        guard let result = await DelegationGit.run(["rev-parse", "-q", "--verify", "HEAD^{commit}"], in: folder), result.exitCode == 0 else { return nil }
        let sha = DelegationGit.line(result.stdout)
        return DelegationGit.isObjectName(sha) ? sha : nil
    }

    /// Whether tracked files in `folder`'s checkout differ from its HEAD,
    /// staged or not; untracked and ignored files do not count. nil when git failed.
    static func hasTrackedChanges(_ folder: String) async -> Bool? {
        guard let status = await DelegationGit.run(["status", "--porcelain", "--untracked-files=no"], in: folder), status.exitCode == 0 else { return nil }
        return !status.stdout.isEmpty
    }

    static func isFolder(_ path: String) -> Bool {
        var folder: ObjCBool = false
        return FileManager.default.fileExists(atPath: path, isDirectory: &folder) && folder.boolValue
    }
}

extension DelegationFile {
    /// Writes a finished merge: its MergeRecord joins the file and the child
    /// moves from reported to merged. Refused, changing nothing, when no child
    /// here has the record's id or that child cannot move to merged.
    @discardableResult public mutating func recordMerge(_ record: MergeRecord) -> Bool {
        guard let index = children.firstIndex(where: { $0.id == record.childId }), children[index].apply(.merge) else { return false }
        merges.append(record)
        return true
    }

    /// Writes a finished undo: the merge record leaves the file, a merged
    /// child goes back to reported and a closed one stays closed. Refused,
    /// changing nothing, when the record is not here or its child cannot be undone.
    @discardableResult public mutating func recordUndo(_ record: MergeRecord) -> Bool {
        guard let recordIndex = merges.lastIndex(of: record), let index = children.firstIndex(where: { $0.id == record.childId }),
              children[index].apply(.undoMerge) else { return false }
        merges.remove(at: recordIndex)
        return true
    }
}
