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

/// Merges a delegated child into its recorded parent branch.
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

        guard parentCheckout.hasPrefix("/"), !parentCheckout.contains("\0"), isFolder(parentCheckout),
              let parentBranch = await DelegationGit.run(["symbolic-ref", "-q", "HEAD"], in: parentCheckout),
              parentBranch.exitCode == 0, DelegationGit.line(parentBranch.stdout) == "refs/heads/" + child.parentBranch,
              let preMerge = await head(of: parentCheckout) else { return .refused(.branchNotCheckedOut) }
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

    private static func isFolder(_ path: String) -> Bool {
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
}
