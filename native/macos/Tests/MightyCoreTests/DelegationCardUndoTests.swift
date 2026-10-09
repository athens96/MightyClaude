import Foundation
import Testing
@testable import MightyCore

/// The card's merge and undo on real git repositories in a temp folder: a
/// fast-forward when it can, otherwise exactly one merge commit; an undo puts
/// the recorded parent branch, its index and its files back by
/// compare-and-swap; and every refusal changes nothing.
@Suite(.enabled(if: DelegationGit.executable != nil, "Requires a local Git executable"))
struct DelegationCardUndoTests {
    @Test func fastForwardsWhenItCanAndUndoReturnsTheChildToReported() async throws {
        let place = try MergePlace(); defer { place.remove() }
        let base = try await cardRepository(place)
        var child = try await place.child("c1")
        let head = try await place.commit(in: child.worktreePath, "a.txt", "a\n")
        let reported = child.recordReport(head: head); #expect(reported)

        guard case .merged(let record) = await ChildMerge.cardMerge(child, parentCheckout: place.repo.path) else { Issue.record("not merged"); return }
        #expect(record == MergeRecord(childId: "c1", kind: .cardFastForward, parentBranch: "main", preMergeCommit: base, mergedCommit: head, childHead: head))
        #expect(try await place.git(["rev-parse", "HEAD"]) == head)
        #expect(try await place.git(["rev-list", "--merges", "HEAD"]).isEmpty)
        var file = DelegationFile(children: [child])
        let written = file.recordMerge(record); #expect(written)
        let merged = try #require(file.children.first); #expect(merged.state == .merged)

        // An open, idle parent: the branch, index and files go back; an untracked file stays.
        try Data("scratch\n".utf8).write(to: place.repo.appendingPathComponent("scratch.txt"))
        #expect(await ChildMerge.undo(record, of: merged, parentCheckout: place.repo.path, parentActivity: .finished) == .undone(record, restoredBranch: false))
        #expect(try await place.git(["symbolic-ref", "HEAD"]) == "refs/heads/main")
        #expect(try await place.git(["rev-parse", "HEAD"]) == base)
        #expect(try await place.git(["status", "--porcelain", "--untracked-files=no"]).isEmpty)
        #expect(!FileManager.default.fileExists(atPath: place.repo.appendingPathComponent("a.txt").path))
        #expect(try String(contentsOf: place.repo.appendingPathComponent("scratch.txt"), encoding: .utf8) == "scratch\n")
        #expect(try await place.git(["rev-parse", "refs/heads/mighty/c1"]) == head)

        // The open child is reported again, at the same head, with the undone record gone; it can be merged again.
        let undone = file.recordUndo(record); #expect(undone)
        let again = try #require(file.children.first)
        #expect(again.state == .reported); #expect(again.reportHead == head); #expect(file.merges.isEmpty)
        let twice = file.recordUndo(record); #expect(!twice)
        guard case .merged = await ChildMerge.cardMerge(again, parentCheckout: place.repo.path) else { Issue.record("not merged again"); return }
        #expect(try await place.git(["rev-parse", "HEAD"]) == head)
    }

    @Test func leavesExactlyOneMergeCommitWhenTheParentMovedOnAndUndoesIt() async throws {
        let place = try MergePlace(); defer { place.remove() }
        try await cardRepository(place)
        var child = try await place.child("c1")
        _ = try await place.commit(in: child.worktreePath, "a.txt", "a\n")
        let head = try await place.commit(in: child.worktreePath, "b.txt", "b\n")
        let reported = child.recordReport(head: head); #expect(reported)
        let moved = try await place.commit(in: place.repo.path, "parent.txt", "p\n")

        guard case .merged(let record) = await ChildMerge.cardMerge(child, parentCheckout: place.repo.path) else { Issue.record("not merged"); return }
        #expect(record.kind == .cardMergeCommit); #expect(record.parentBranch == "main")
        #expect(record.preMergeCommit == moved); #expect(record.childHead == head)
        // One merge commit on top of the old parent head, its parents the old parent head and the child head.
        #expect(try await place.git(["rev-parse", "HEAD"]) == record.mergedCommit)
        #expect(try await place.git(["rev-list", "--parents", "-n", "1", "HEAD"]) == "\(record.mergedCommit) \(moved) \(head)")
        #expect(try await place.git(["rev-list", "--merges", "\(moved)..HEAD"]) == record.mergedCommit)
        #expect(try await place.git(["rev-list", "--count", "\(moved)..HEAD"]) == "3")
        #expect(try await place.git(["status", "--porcelain", "--untracked-files=all"]).isEmpty)
        for name in ["a.txt", "b.txt", "parent.txt"] { #expect(FileManager.default.fileExists(atPath: place.repo.appendingPathComponent(name).path), "\(name)") }
        #expect(try await place.git(["rev-parse", "refs/heads/mighty/c1"]) == head)

        var file = DelegationFile(children: [child])
        let written = file.recordMerge(record); #expect(written)
        let merged = try #require(file.children.first)
        #expect(await ChildMerge.undo(record, of: merged, parentCheckout: place.repo.path, parentActivity: .idle) == .undone(record, restoredBranch: false))
        #expect(try await place.git(["rev-parse", "HEAD"]) == moved)
        #expect(try await place.git(["status", "--porcelain", "--untracked-files=all"]).isEmpty)
        #expect(try await place.git(["ls-files"]) == "file-0.txt\nparent.txt")
        let undone = file.recordUndo(record); #expect(undone)
        #expect(file.children.first?.state == .reported)
    }

    @Test func refusesAConflictTrackedChangesOrAnotherBranchChangingNothing() async throws {
        let place = try MergePlace(); defer { place.remove() }
        let base = try await cardRepository(place)
        var clashing = try await place.child("c1")
        let clash = try await place.commit(in: clashing.worktreePath, "file-0.txt", "child\n")
        let clashReported = clashing.recordReport(head: clash); #expect(clashReported)
        var child = try await place.child("c2")
        let head = try await place.commit(in: child.worktreePath, "a.txt", "a\n")
        let reported = child.recordReport(head: head); #expect(reported)
        _ = try await place.commit(in: place.repo.path, "file-0.txt", "parent\n")

        // Both sides changed the same line.
        var before = try await parentSnapshot(place)
        #expect(await ChildMerge.cardMerge(clashing, parentCheckout: place.repo.path) == .refused(.mergeConflict))
        #expect(try await parentSnapshot(place) == before)
        #expect(!FileManager.default.fileExists(atPath: place.repo.appendingPathComponent(".git/MERGE_HEAD").path))

        // A tracked change in the parent, unstaged and then staged.
        try Data("edit\n".utf8).write(to: place.repo.appendingPathComponent("file-0.txt"))
        before = try await parentSnapshot(place)
        #expect(await ChildMerge.cardMerge(child, parentCheckout: place.repo.path) == .refused(.trackedChanges))
        #expect(try await parentSnapshot(place) == before)
        try await place.git(["add", "file-0.txt"])
        before = try await parentSnapshot(place)
        #expect(await ChildMerge.cardMerge(child, parentCheckout: place.repo.path) == .refused(.trackedChanges))
        #expect(try await parentSnapshot(place) == before)
        try await place.git(["reset", "-q", "--hard"])

        // Another branch, a detached HEAD, a folder that is no checkout of it.
        try await place.git(["checkout", "-q", "-b", "other"])
        before = try await parentSnapshot(place)
        #expect(await ChildMerge.cardMerge(child, parentCheckout: place.repo.path) == .refused(.branchNotCheckedOut))
        #expect(try await parentSnapshot(place) == before)
        try await place.git(["checkout", "-q", "--detach", "main"])
        #expect(await ChildMerge.cardMerge(child, parentCheckout: place.repo.path) == .refused(.branchNotCheckedOut))
        for folder in [place.base.path, "relative/folder"] {
            #expect(await ChildMerge.cardMerge(child, parentCheckout: folder) == .refused(.branchNotCheckedOut), "\(folder)")
        }
        try await place.git(["checkout", "-q", "main"])

        // Only a reported child is merged.
        for state in ChildState.allCases where state != .reported {
            var other = child; other.state = state
            #expect(await ChildMerge.cardMerge(other, parentCheckout: place.repo.path) == .refused(.notReported), "\(state)")
        }
        #expect(try await place.git(["for-each-ref", "--format=%(refname)", "refs/heads/main"]) == "refs/heads/main")
        #expect(try await place.git(["rev-list", "--count", "\(base)..main"]) == "1")
    }

    @Test func undoIsRefusedUnlessTheBranchIsCheckedOutAtTheMergeTheParentIdleAndTrackedFilesClean() async throws {
        let place = try MergePlace(); defer { place.remove() }
        let base = try await cardRepository(place)
        var child = try await place.child("c1")
        let head = try await place.commit(in: child.worktreePath, "a.txt", "a\n")
        let reported = child.recordReport(head: head); #expect(reported)
        guard case .merged(let record) = await ChildMerge.cardMerge(child, parentCheckout: place.repo.path) else { Issue.record("not merged"); return }
        var file = DelegationFile(children: [child])
        let written = file.recordMerge(record); #expect(written)
        let merged = try #require(file.children.first)
        func undo(_ activity: DelegationPaneActivity? = .idle) async -> ChildUndoOutcome {
            await ChildMerge.undo(record, of: merged, parentCheckout: place.repo.path, parentActivity: activity)
        }

        // The recorded branch is not checked out.
        try await place.git(["checkout", "-q", "-b", "other"])
        var before = try await parentSnapshot(place)
        #expect(await undo() == .refused(.branchNotCheckedOut))
        #expect(try await parentSnapshot(place) == before)
        try await place.git(["checkout", "-q", "main"])

        // The parent pane is running.
        before = try await parentSnapshot(place)
        #expect(await undo(.running) == .refused(.parentBusy))
        #expect(try await parentSnapshot(place) == before)

        // Tracked files are not clean.
        try Data("edit\n".utf8).write(to: place.repo.appendingPathComponent("a.txt"))
        before = try await parentSnapshot(place)
        #expect(await undo() == .refused(.trackedChanges))
        #expect(try await parentSnapshot(place) == before)
        try await place.git(["checkout", "-q", "--", "a.txt"])

        // A file the undo would put back is in the way as an untracked file: never overwritten.
        try await place.git(["rm", "-q", "file-0.txt"]); try await place.git(["commit", "-q", "-m", "drop file-0"])
        let dropped = try await place.git(["rev-parse", "HEAD"])
        let dropRecord = MergeRecord(childId: "c1", kind: .cardFastForward, parentBranch: "main", preMergeCommit: head, mergedCommit: dropped, childHead: head)
        try Data("mine\n".utf8).write(to: place.repo.appendingPathComponent("file-0.txt"))
        before = try await parentSnapshot(place)
        guard case .failed = await ChildMerge.undo(dropRecord, of: merged, parentCheckout: place.repo.path, parentActivity: .idle) else { Issue.record("an untracked file was overwritten"); return }
        #expect(try await parentSnapshot(place) == before)
        #expect(try String(contentsOf: place.repo.appendingPathComponent("file-0.txt"), encoding: .utf8) == "mine\n")
        try FileManager.default.removeItem(at: place.repo.appendingPathComponent("file-0.txt"))

        // The branch moved past the merged commit.
        before = try await parentSnapshot(place)
        #expect(await undo() == .refused(.undoParentMoved))
        #expect(try await parentSnapshot(place) == before)
        try await place.git(["reset", "-q", "--hard", head])

        // A record of another child, or a child that ran again since the merge, has nothing to undo.
        var other = merged; other.id = "c9"
        guard case .failed = await ChildMerge.undo(record, of: other, parentCheckout: place.repo.path, parentActivity: .idle) else { Issue.record("another child's record was undone"); return }
        var runAgain = merged; let ran = runAgain.apply(.startRun); #expect(ran)
        guard case .failed = await ChildMerge.undo(record, of: runAgain, parentCheckout: place.repo.path, parentActivity: .idle) else { Issue.record("a running child was undone"); return }
        #expect(try await place.git(["rev-parse", "HEAD"]) == head)

        // With every condition met, the undo goes through.
        #expect(await undo(.finished) == .undone(record, restoredBranch: false))
        #expect(try await place.git(["rev-parse", "HEAD"]) == base)
    }

    @Test func undoOnAClosedChildRecreatesItsBranchAndLeavesItClosed() async throws {
        let place = try MergePlace(); defer { place.remove() }
        try await cardRepository(place)
        var child = try await place.child("c1")
        let head = try await place.commit(in: child.worktreePath, "a.txt", "a\n")
        let reported = child.recordReport(head: head); #expect(reported)
        let moved = try await place.commit(in: place.repo.path, "parent.txt", "p\n")
        guard case .merged(let record) = await ChildMerge.cardMerge(child, parentCheckout: place.repo.path) else { Issue.record("not merged"); return }
        var file = DelegationFile(children: [child])
        let written = file.recordMerge(record); #expect(written)

        // The human closed the merged child's pane: its worktree and branch were cleaned up from the parent's side.
        try await place.git(["worktree", "remove", child.worktreePath])
        try await place.git(["branch", "-d", "mighty/c1"])
        let closed = file.children[0].apply(.closePane); #expect(closed)
        let closedChild = try #require(file.children.first)

        // The parent pane is closed too, which counts as idle.
        #expect(await ChildMerge.undo(record, of: closedChild, parentCheckout: place.repo.path, parentActivity: nil) == .undone(record, restoredBranch: true))
        #expect(try await place.git(["rev-parse", "refs/heads/mighty/c1"]) == head)
        #expect(try await place.git(["rev-parse", "HEAD"]) == moved)
        #expect(try await place.git(["status", "--porcelain", "--untracked-files=all"]).isEmpty)
        let undone = file.recordUndo(record); #expect(undone)
        let card = try #require(file.children.first)
        #expect(card.state == .closed); #expect(!card.state.isOpen); #expect(file.merges.isEmpty)
        // Unmerged again, the closed child's work stays on its branch for a human to discard.
        #expect(card.state.after(.discard) == .discarded)
    }
}

/// `place.repository()` with an identity of its own for the card's merge
/// commit, since a CI runner has none.
@discardableResult
private func cardRepository(_ place: MergePlace) async throws -> String {
    let base = try await place.repository()
    for (key, value) in [("user.name", "Fixture"), ("user.email", "fixture@example.invalid"), ("commit.gpgsign", "false")] { try await place.git(["config", key, value]) }
    return base
}

/// Every ref, and the parent checkout's HEAD, index entries and file status.
private func parentSnapshot(_ place: MergePlace) async throws -> [String] {
    [try await place.git(["for-each-ref", "--format=%(refname) %(objectname)"]),
     try await place.git(["rev-parse", "--symbolic-full-name", "HEAD"]),
     try await place.git(["rev-parse", "HEAD"]),
     try await place.git(["ls-files", "-s"]),
     try await place.git(["status", "--porcelain", "--untracked-files=all"])]
}
