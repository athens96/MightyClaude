import Foundation

/// Why closing a merged child's pane kept its worktree and branch (macOS
/// only). Nothing on disk changed: only the pane closed, and a human may
/// still discard the child from its card.
public enum ChildCleanupHold: String, Codable, Sendable, CaseIterable {
    /// The child is not merged, or the merge record is another child's.
    case notMerged = "not_merged"
    /// A run is going in the child's pane.
    case childBusy = "child_busy"
    /// The child's worktree is gone, or is no worktree of the parent's repository.
    case worktreeMissing = "worktree_missing"
    /// The worktree is not on the child's branch at the head that was merged.
    case headMoved = "head_moved"
    /// Tracked files in the worktree differ from its head, staged or not.
    case trackedChanges = "tracked_changes"
    /// The worktree holds files that are neither tracked nor ignored.
    case untrackedFiles = "untracked_files"
    /// Another registered worktree lies inside the child's.
    case nestedWorktree = "nested_worktree"
    /// The parent's checkout does not have the recorded parent branch checked out.
    case branchNotCheckedOut = "branch_not_checked_out"
    /// The recorded parent branch no longer holds the child's head, so
    /// `git branch -d` would refuse.
    case notInParent = "not_in_parent"
}

/// The answer to a human closing a delegated child's pane (macOS only).
public enum ChildCleanupOutcome: Sendable, Equatable {
    /// The worktree went by plain `git worktree remove` and the branch by `git branch -d`.
    case cleaned
    /// A condition did not hold, so only the pane closed: nothing on disk changed.
    case kept(ChildCleanupHold)
    /// The copies could not be saved, or git was missing or failed. Git
    /// refuses before it deletes anything, and the branch goes last, so the
    /// child's work stays on disk or on its branch.
    case failed(String)
}

/// The answer to a human's discard of a delegated child from its card (macOS only).
public enum ChildDiscardOutcome: Sendable, Equatable {
    /// The child's worktree, every worktree nested in it, a leftover folder
    /// at its place and its branch are gone, whichever of them existed.
    case discarded
    /// Nothing after this step was removed; a later discard goes on from here.
    case failed(String)
}

/// Removes a delegated child's worktree and branch: by cleanup when a human
/// closes a merged child's pane, which never forces anything, and by a
/// human's discard, the only path that force-removes unmerged work.
public enum ChildCleanup {
    /// The longest one git step may take. A human waits on these, not a tool call.
    public static let gitTimeout: TimeInterval = 120

    /// The cleanup of a merged child whose pane a human closed, after its
    /// copies are stored. `record` is the child's merge, `parentCheckout` the
    /// parent's side and `childActivity` the child pane's (nil once it is
    /// closed, which counts as idle). The first rule that applies keeps
    /// everything, and every rule is checked before anything is removed:
    ///
    /// 1. `not_merged`: the child is not merged, or `record` is not its merge.
    /// 2. `child_busy`: a run is going in the child's pane.
    /// 3. `worktree_missing`: the worktree is gone, or the parent's
    ///    repository does not list it.
    /// 4. `branch_not_checked_out`: the parent's checkout does not have the
    ///    recorded parent branch checked out.
    /// 5. `head_moved`: the worktree is not on the child's branch at the
    ///    head that was merged.
    /// 6. `tracked_changes`: tracked files in the worktree differ from its head.
    /// 7. `untracked_files`: the worktree holds files no ignore rule covers.
    ///    Ignored files, TASK.md and REPORT.md among them, go with the worktree.
    /// 8. `nested_worktree`: another registered worktree lies inside it; an
    ///    ignored one would otherwise go with the folder, its own work included.
    /// 9. `not_in_parent`: the parent branch no longer holds the child's head.
    ///
    /// Otherwise the worktree goes by plain `git worktree remove`, which
    /// checks for changes again itself, and then the branch by
    /// `git branch -d` from the parent's side. Never `--force`.
    public static func cleanUp(_ child: ChildRecord, merge record: MergeRecord, parentCheckout: String, childActivity: DelegationPaneActivity?) async -> ChildCleanupOutcome {
        let head = record.childHead.lowercased()
        guard child.state == .merged, record.childId == child.id, DelegationGit.isObjectName(head) else { return .kept(.notMerged) }
        guard childActivity != .running else { return .kept(.childBusy) }
        guard ChildMerge.isFolder(child.worktreePath) else { return .kept(.worktreeMissing) }
        guard DelegationGit.executable != nil else { return .failed("Git is missing.") }
        guard let parentHead = await ChildMerge.checkedOutHead(of: record.parentBranch, in: parentCheckout) else { return .kept(.branchNotCheckedOut) }
        guard let listed = await worktrees(in: parentCheckout) else { return .failed("Git could not list the worktrees.") }
        let own = canonical(child.worktreePath)
        guard listed.contains(own) else { return .kept(.worktreeMissing) }

        let worktree = child.worktreePath
        guard let branch = await DelegationGit.run(["symbolic-ref", "-q", "HEAD"], in: worktree) else { return .failed("Git did not finish.") }
        guard branch.exitCode == 0, DelegationGit.line(branch.stdout) == "refs/heads/" + child.branch,
              await ChildMerge.head(of: worktree) == head else { return .kept(.headMoved) }
        switch await ChildMerge.hasTrackedChanges(worktree) {
        case nil: return .failed("Git could not read the child's changes.")
        case true?: return .kept(.trackedChanges)
        case false?: break
        }
        // What plain `git worktree remove` checks itself: with tracked files
        // clean, anything listed is a file no ignore rule covers.
        guard let status = await DelegationGit.run(["status", "--porcelain", "--ignore-submodules=none", "--untracked-files=normal"], in: worktree),
              status.exitCode == 0 else { return .failed("Git could not read the child's files.") }
        guard status.stdout.isEmpty else { return .kept(.untrackedFiles) }
        guard !listed.contains(where: { $0.hasPrefix(own + "/") }) else { return .kept(.nestedWorktree) }
        guard let merged = await DelegationGit.run(["merge-base", "--is-ancestor", head, parentHead], in: parentCheckout) else { return .failed("Git did not finish.") }
        guard merged.exitCode != 1 else { return .kept(.notInParent) }
        guard merged.exitCode == 0 else { return .failed(DelegationGit.message(merged)) }

        guard let removed = await DelegationGit.run(["worktree", "remove", worktree], in: parentCheckout, timeout: gitTimeout) else { return .failed("Git did not finish.") }
        guard removed.exitCode == 0 else { return .failed(DelegationGit.message(removed)) }
        guard let deleted = await DelegationGit.run(["branch", "-d", child.branch], in: parentCheckout) else { return .failed("Git did not finish; the branch stays.") }
        guard deleted.exitCode == 0 else { return .failed(DelegationGit.message(deleted)) }
        return .cleaned
    }

    /// The registered worktrees inside the child's worktree, deepest first,
    /// for the discard confirmation to name; nil when git failed.
    public static func nestedWorktrees(of child: ChildRecord, parentCheckout: String) async -> [String]? {
        guard let listed = await worktrees(in: parentCheckout) else { return nil }
        let own = canonical(child.worktreePath)
        return listed.filter { $0.hasPrefix(own + "/") }.sorted { $0.count > $1.count }
    }

    /// A human's discard from the child's card, once its run is stopped and
    /// its report copy kept. `parentCheckout` is the parent's side and
    /// `worktreeRoot` the root children's worktrees are made under. A child
    /// whose recorded worktree is not its place `<root>/<child id>` is
    /// refused before anything is removed. Otherwise, in this order, each
    /// whichever exists:
    ///
    /// 1. every registered worktree nested in the child's, deepest first,
    ///    then the child's own, each by `git worktree remove --force --force`
    ///    (twice, so a worktree a killed start left locked goes too; one
    ///    whose folder is already gone loses its registration);
    /// 2. a folder git does not know at the child's place, as a failed start
    ///    may leave it;
    /// 3. the child's branch, by `git branch -D`: it may hold unmerged work.
    ///
    /// The parent's branch and checkout, and the branches of nested
    /// worktrees, are never touched.
    public static func discard(_ child: ChildRecord, parentCheckout: String, worktreeRoot: URL) async -> ChildDiscardOutcome {
        // Only the child's own place is ever forced away, never a folder a record names elsewhere.
        let own = canonical(child.worktreePath)
        guard ChildWorktreeMaker.isSafeSessionId(child.id), own == canonical(worktreeRoot.appendingPathComponent(child.id, isDirectory: true).path) else {
            return .failed("The child's worktree is not its place under the worktree root, so nothing was removed.")
        }
        guard DelegationGit.executable != nil else { return .failed("Git is missing.") }
        guard let listed = await worktrees(in: parentCheckout) else { return .failed("Git could not reach the parent's checkout, so nothing was removed.") }
        let doomed = listed.filter { $0.hasPrefix(own + "/") }.sorted { $0.count > $1.count } + listed.filter { $0 == own }
        for path in doomed {
            guard let removed = await DelegationGit.run(["worktree", "remove", "--force", "--force", path], in: parentCheckout, timeout: gitTimeout) else { return .failed("Git did not finish.") }
            guard removed.exitCode == 0 else { return .failed(DelegationGit.message(removed)) }
        }
        if FileManager.default.fileExists(atPath: child.worktreePath) {
            do { try FileManager.default.removeItem(atPath: child.worktreePath) } catch { return .failed(error.localizedDescription) }
        }
        guard let found = await DelegationGit.run(["rev-parse", "-q", "--verify", "refs/heads/" + child.branch], in: parentCheckout),
              found.exitCode == 0 || found.exitCode == 1 else { return .failed("Git could not read the child's branch.") }
        if found.exitCode == 0 {
            guard let deleted = await DelegationGit.run(["branch", "-D", child.branch], in: parentCheckout) else { return .failed("Git did not finish.") }
            guard deleted.exitCode == 0 else { return .failed(DelegationGit.message(deleted)) }
        }
        return .discarded
    }

    /// The canonical paths of the worktrees registered in `folder`'s
    /// repository, or nil when git failed.
    static func worktrees(in folder: String) async -> [String]? {
        guard folder.hasPrefix("/"), !folder.contains("\0"), ChildMerge.isFolder(folder),
              let list = await DelegationGit.run(["worktree", "list", "--porcelain", "-z"], in: folder), list.exitCode == 0 else { return nil }
        return String(decoding: list.stdout, as: UTF8.self).split(separator: "\0").compactMap { $0.hasPrefix("worktree ") ? canonical(String($0.dropFirst(9))) : nil }
    }

    /// `path` with its symlinks resolved as far as it exists, the way git
    /// lists worktrees: `/var/folders/…` is `/private/var/folders/…`.
    static func canonical(_ path: String) -> String {
        var existing = URL(fileURLWithPath: path).standardizedFileURL, missing: [String] = []
        while true {
            if let resolved = realpath(existing.path, nil) {
                defer { free(resolved) }
                return missing.reversed().reduce(URL(fileURLWithPath: String(cString: resolved))) { $0.appendingPathComponent($1) }.path
            }
            let parent = existing.deletingLastPathComponent()
            guard parent.path != existing.path else { return URL(fileURLWithPath: path).standardizedFileURL.path }
            missing.append(existing.lastPathComponent); existing = parent
        }
    }
}

/// Whether a human may remove a workspace while delegation has children
/// there (macOS only).
public enum DelegationWorkspaceRemoval {
    /// `workspace_has_children` when the workspace at `folder`, with the panes
    /// `paneIds`, still has a child that is open or whose worktree is still on
    /// disk (a closed child kept, or what a failed start left); nil when it may
    /// go. A child is the workspace's when its pane or its parent's is one of
    /// `paneIds`, or its parent's checkout is `folder` or inside it. A
    /// discarded child's worktree is gone, so it never counts.
    public static func refusal(children: [ChildRecord], paneIds: Set<String>, folder: String,
                               worktreeExists: (String) -> Bool = { FileManager.default.fileExists(atPath: $0) }) -> DelegationReasonCode? {
        let root = ChildCleanup.canonical(folder)
        let inside = root.hasSuffix("/") ? root : root + "/"
        for child in children where child.state != .discarded {
            let checkout = child.parentCheckout.map(ChildCleanup.canonical)
            let ours = paneIds.contains(child.id) || paneIds.contains(child.parentSessionId) || checkout.map { $0 == root || $0.hasPrefix(inside) } == true
            if ours, child.state.isOpen || worktreeExists(child.worktreePath) { return .workspaceHasChildren }
        }
        return nil
    }
}

extension DelegationCoordinator {
    /// ``DelegationWorkspaceRemoval/refusal(children:paneIds:folder:worktreeExists:)``
    /// for the children in the delegation file.
    public func workspaceRemovalRefusal(paneIds: Set<String>, folder: String) -> DelegationReasonCode? {
        DelegationWorkspaceRemoval.refusal(children: file.children, paneIds: paneIds, folder: folder)
    }
}

/// A human's close and discard of a child (macOS only). The tools never reach these.
extension DelegationCoordinator {
    /// A human closed the child's pane, on the Mac or from the phone. An open
    /// child becomes closed and keeps its card. A merged one first has
    /// copies of its REPORT.md and TASK.md and its branch head stored in the
    /// delegation file, and only once they are saved is its worktree and
    /// branch cleaned up, when every rule of
    /// ``ChildCleanup/cleanUp(_:merge:parentCheckout:childActivity:)`` holds.
    /// Otherwise nothing on disk changes. The merge record stays, so the
    /// merge can still be undone.
    public func closeChild(_ id: String) async -> ChildCleanupOutcome {
        guard let found = file.children.first(where: { $0.id == id }) else { return .failed("No child has this id.") }
        let activity = await host.paneState(sessionId: id)?.activity
        let checkout = await parentCheckout(of: found)
        var head: String?
        if found.state == .merged { head = await branchHead(of: found, parentCheckout: checkout) }
        let context = await pruneContext()
        // Nothing below suspends until the close is saved.
        guard let index = file.children.firstIndex(where: { $0.id == id }), file.children[index].state.isOpen else { return .kept(.notMerged) }
        let child = file.children[index]
        let merge = child.state == .merged ? file.merges.last(where: { $0.childId == id }) : nil
        var next = file
        if merge != nil {
            let notes: [(DelegationCopy.Kind, String, Int)] = [(.report, ChildWorktree.reportFile(worktreePath: child.worktreePath), child.reportRevision),
                                                                (.task, ChildWorktree.taskFile(worktreePath: child.worktreePath), 0)]
            for (kind, path, revision) in notes {
                if let copy = try? DelegationCopy.read(childId: id, kind: kind, revision: revision, from: URL(fileURLWithPath: path)) { next.setCopy(copy) }
            }
            next.children[index].closedHead = head
        }
        next.children[index].apply(.closePane)
        do { try commit(next, context: context) } catch {
            // Closed all the same; the next save writes it. Without the copies on disk, nothing is removed.
            file = next
            return merge == nil ? .kept(.notMerged) : .failed("The delegation file could not be saved, so the child's worktree and branch were kept.")
        }
        guard let merge else { return .kept(.notMerged) }
        guard let checkout else { return .kept(.branchNotCheckedOut) }
        return await ChildCleanup.cleanUp(child, merge: merge, parentCheckout: checkout, childActivity: activity)
    }

    /// A human's discard of the child from its card, after a confirmation
    /// naming any nested worktrees (``ChildCleanup/nestedWorktrees(of:parentCheckout:)``).
    /// It is the only path that removes unmerged work: the child's run is
    /// stopped, a copy of its REPORT.md is kept in the delegation file, and
    /// then ``ChildCleanup/discard(_:parentCheckout:worktreeRoot:)`` removes
    /// its worktree, the worktrees nested in it and its branch by force, with
    /// whatever a failed start left. While the child's start is still going
    /// the discard is refused with nothing changed. The child is discarded
    /// once its worktree and branch are gone; until then a discard may be
    /// tried again.
    public func discardChild(_ id: String) async -> ChildDiscardOutcome {
        guard let found = file.children.first(where: { $0.id == id }), found.state != .discarded else { return .failed("No child with this id can be discarded.") }
        guard !starting.contains(id) else { return .failed("The child is still starting, so nothing was removed. Discard it once its start has finished.") }
        await host.stopRun(sessionId: id)
        guard let checkout = await parentCheckout(of: found) else { return .failed("The parent's checkout is not known, so nothing was removed.") }
        let context = await pruneContext()
        guard let index = file.children.firstIndex(where: { $0.id == id }), file.children[index].state != .discarded else { return .failed("No child with this id can be discarded.") }
        let child = file.children[index]
        var next = file
        let report = URL(fileURLWithPath: ChildWorktree.reportFile(worktreePath: child.worktreePath))
        if let copy = try? DelegationCopy.read(childId: id, kind: .report, revision: child.reportRevision, from: report), copy.originalBytes > 0 { next.setCopy(copy) }
        do { try commit(next, context: context) } catch { return .failed("The delegation file could not be saved, so nothing was removed.") }

        let outcome = await ChildCleanup.discard(child, parentCheckout: checkout, worktreeRoot: worktrees.root)
        guard outcome == .discarded else { return outcome }
        let after = await pruneContext()
        var done = file
        if let index = done.children.firstIndex(where: { $0.id == id }), done.children[index].apply(.discard) {
            do { try commit(done, context: after) } catch { file = done }
        }
        return .discarded
    }

    /// Where git runs on the parent's side: the checkout recorded when the
    /// child was made, else the parent pane's folder while it is open.
    func parentCheckout(of child: ChildRecord) async -> String? {
        if let recorded = child.parentCheckout { return recorded }
        return await host.paneState(sessionId: child.parentSessionId)?.folder
    }

    /// The head of the child's branch, read on the parent's side or else in
    /// its worktree; nil when neither has it.
    private func branchHead(of child: ChildRecord, parentCheckout: String?) async -> String? {
        for folder in [parentCheckout, child.worktreePath].compactMap({ $0 }) where ChildMerge.isFolder(folder) {
            guard let found = await DelegationGit.run(["rev-parse", "-q", "--verify", "refs/heads/\(child.branch)^{commit}"], in: folder), found.exitCode == 0 else { continue }
            let sha = DelegationGit.line(found.stdout)
            if DelegationGit.isObjectName(sha) { return sha }
        }
        return nil
    }
}
