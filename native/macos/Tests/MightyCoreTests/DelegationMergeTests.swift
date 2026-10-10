import Foundation
import Testing
@testable import MightyCore

/// The parent's merge tool on real git repositories in a temp folder: a
/// reported child fast-forwards the recorded parent branch, and every refusal
/// changes nothing.
@Suite(.enabled(if: DelegationGit.executable != nil, "Requires a local Git executable"), .delegationLane)
struct DelegationMergeTests {
    @Test func fastForwardsTheParentBranchAndWritesTheMergeRecord() async throws {
        let place = try MergePlace(); defer { place.remove() }
        let base = try await place.repository()
        var child = try await place.child("c1")
        _ = try await place.commit(in: child.worktreePath, "a.txt", "a\n")
        let head = try await place.commit(in: child.worktreePath, "b.txt", "b\n")
        try Data("# Report\nDone.\n".utf8).write(to: URL(fileURLWithPath: ChildWorktree.reportFile(worktreePath: child.worktreePath)))
        let reported = child.recordReport(head: head); #expect(reported)

        let outcome = await ChildMerge.toolMerge(child, expectedHead: head, parentCheckout: place.repo.path)
        guard case .merged(let record) = outcome else { Issue.record("not merged: \(outcome)"); return }
        #expect(record == MergeRecord(childId: "c1", kind: .toolFastForward, parentBranch: "main", preMergeCommit: base, mergedCommit: head, childHead: head))
        // A fast-forward alone: main is still checked out, now at the child's head, with no merge commit and a clean checkout.
        #expect(try await place.git(["symbolic-ref", "HEAD"]) == "refs/heads/main")
        #expect(try await place.git(["rev-parse", "HEAD"]) == head)
        #expect(try await place.git(["rev-list", "--count", "\(base)..HEAD"]) == "2")
        #expect(try await place.git(["rev-list", "--merges", "HEAD"]).isEmpty)
        #expect(try await place.git(["status", "--porcelain", "--untracked-files=all"]).isEmpty)
        #expect(try String(contentsOf: place.repo.appendingPathComponent("b.txt"), encoding: .utf8) == "b\n")
        // The child's branch and checkout are only read.
        #expect(try await place.git(["rev-parse", "refs/heads/mighty/c1"]) == head)
        #expect(try await place.git(["symbolic-ref", "HEAD"], in: child.worktreePath) == "refs/heads/mighty/c1")

        // The record is written into the delegation file, which keeps it across a restart.
        var file = DelegationFile(children: [child])
        let written = file.recordMerge(record); #expect(written)
        #expect(file.children.first?.state == .merged); #expect(file.merges == [record])
        let store = DelegationFileStore(directory: place.base.appendingPathComponent("profile", isDirectory: true))
        try store.save(file)
        let restored = try store.load()
        #expect(restored.merges == [record]); #expect(restored.merges.first?.preMergeCommit == base)
        #expect(restored.children.first?.state == .merged)

        // A merged child is no longer reported: a second merge is refused and writes no second record.
        let merged = try #require(restored.children.first)
        let before = try await place.snapshot(merged)
        #expect(await ChildMerge.toolMerge(merged, expectedHead: head, parentCheckout: place.repo.path) == .refused(.notReported))
        #expect(try await place.snapshot(merged) == before)
        var again = restored
        let twice = again.recordMerge(record); #expect(!twice)
        let stranger = again.recordMerge(MergeRecord(childId: "missing", kind: .toolFastForward, parentBranch: "main", preMergeCommit: base, mergedCommit: head, childHead: head)); #expect(!stranger)
        #expect(again == restored)
    }

    @Test func refusesAChildThatHasNotReported() async throws {
        let place = try MergePlace(); defer { place.remove() }
        try await place.repository()
        let child = try await place.child("c1")
        let head = try await place.commit(in: child.worktreePath, "a.txt", "a\n")
        let before = try await place.snapshot(child)

        // Running with no report yet.
        #expect(await ChildMerge.toolMerge(child, expectedHead: head, parentCheckout: place.repo.path) == .refused(.notReported))
        // Every other state, even with a report head on record.
        for state in ChildState.allCases where state != .reported {
            var other = child; other.state = state; other.reportRevision = 1; other.reportHead = head
            #expect(await ChildMerge.toolMerge(other, expectedHead: head, parentCheckout: place.repo.path) == .refused(.notReported), "\(state)")
        }
        #expect(try await place.snapshot(child) == before)
    }

    @Test func refusesTrackedChangesInTheChildOrTheParent() async throws {
        let place = try MergePlace(); defer { place.remove() }
        let base = try await place.repository()
        var child = try await place.child("c1")
        let head = try await place.commit(in: child.worktreePath, "a.txt", "a\n")
        let reported = child.recordReport(head: head); #expect(reported)
        let worktree = URL(fileURLWithPath: child.worktreePath)

        // An unstaged, then a staged change to a tracked file in the child.
        try Data("changed\n".utf8).write(to: worktree.appendingPathComponent("a.txt"))
        var before = try await place.snapshot(child)
        #expect(await ChildMerge.toolMerge(child, expectedHead: head, parentCheckout: place.repo.path) == .refused(.trackedChanges))
        #expect(try await place.snapshot(child) == before)
        try await place.git(["add", "a.txt"], in: child.worktreePath)
        before = try await place.snapshot(child)
        #expect(await ChildMerge.toolMerge(child, expectedHead: head, parentCheckout: place.repo.path) == .refused(.trackedChanges))
        #expect(try await place.snapshot(child) == before)
        try await place.git(["reset", "-q", "--hard"], in: child.worktreePath)

        // An untracked file and REPORT.md in the child do not count; a tracked change in the parent does.
        try Data("note\n".utf8).write(to: worktree.appendingPathComponent("untracked.txt"))
        try Data("# Report\n".utf8).write(to: URL(fileURLWithPath: ChildWorktree.reportFile(worktreePath: child.worktreePath)))
        try Data("parent edit\n".utf8).write(to: place.repo.appendingPathComponent("file-0.txt"))
        before = try await place.snapshot(child)
        #expect(await ChildMerge.toolMerge(child, expectedHead: head, parentCheckout: place.repo.path) == .refused(.trackedChanges))
        #expect(try await place.snapshot(child) == before)
        #expect(try await place.git(["rev-parse", "HEAD"]) == base)

        // With the parent's tracked files clean again, untracked files on either side do not block the merge.
        try await place.git(["checkout", "-q", "--", "file-0.txt"])
        try Data("scratch\n".utf8).write(to: place.repo.appendingPathComponent("scratch.txt"))
        guard case .merged(let record) = await ChildMerge.toolMerge(child, expectedHead: head, parentCheckout: place.repo.path) else { Issue.record("a clean merge was refused"); return }
        #expect(record.preMergeCommit == base); #expect(record.mergedCommit == head)
        #expect(try String(contentsOf: place.repo.appendingPathComponent("scratch.txt"), encoding: .utf8) == "scratch\n")
        #expect(FileManager.default.fileExists(atPath: worktree.appendingPathComponent("untracked.txt").path))
    }

    @Test func refusesAMovedHead() async throws {
        let place = try MergePlace(); defer { place.remove() }
        try await place.repository()
        var child = try await place.child("c1")
        let head = try await place.commit(in: child.worktreePath, "a.txt", "a\n")
        let reported = child.recordReport(head: head); #expect(reported)

        // The expected head is not the reported one.
        var before = try await place.snapshot(child)
        for wrong in [child.baseCommit, String(head.prefix(12)), String(repeating: "0", count: 40), "HEAD", "mighty/c1", ""] {
            #expect(await ChildMerge.toolMerge(child, expectedHead: wrong, parentCheckout: place.repo.path) == .refused(.headMoved), "\(wrong)")
        }
        #expect(try await place.snapshot(child) == before)

        // The child's branch moved past its report.
        let later = try await place.commit(in: child.worktreePath, "b.txt", "b\n")
        before = try await place.snapshot(child)
        #expect(await ChildMerge.toolMerge(child, expectedHead: head, parentCheckout: place.repo.path) == .refused(.headMoved))
        #expect(await ChildMerge.toolMerge(child, expectedHead: later, parentCheckout: place.repo.path) == .refused(.headMoved))
        #expect(try await place.snapshot(child) == before)

        // The child's checkout left its branch, though at the reported head.
        try await place.git(["reset", "-q", "--hard", head], in: child.worktreePath)
        try await place.git(["checkout", "-q", "--detach", head], in: child.worktreePath)
        before = try await place.snapshot(child)
        #expect(await ChildMerge.toolMerge(child, expectedHead: head, parentCheckout: place.repo.path) == .refused(.headMoved))
        #expect(try await place.snapshot(child) == before)
    }

    @Test func refusesDivergedBranches() async throws {
        let place = try MergePlace(); defer { place.remove() }
        try await place.repository()
        var child = try await place.child("c1")
        let head = try await place.commit(in: child.worktreePath, "a.txt", "a\n")
        let reported = child.recordReport(head: head); #expect(reported)
        // The parent branch moved on after the child was made.
        let moved = try await place.commit(in: place.repo.path, "parent.txt", "p\n")

        let before = try await place.snapshot(child)
        #expect(await ChildMerge.toolMerge(child, expectedHead: head, parentCheckout: place.repo.path) == .refused(.diverged))
        #expect(try await place.snapshot(child) == before)
        #expect(try await place.git(["rev-parse", "refs/heads/main"]) == moved)
    }

    @Test func refusesWhenTheRecordedParentBranchIsNotCheckedOut() async throws {
        let place = try MergePlace(); defer { place.remove() }
        let base = try await place.repository()
        var child = try await place.child("c1")
        let head = try await place.commit(in: child.worktreePath, "a.txt", "a\n")
        let reported = child.recordReport(head: head); #expect(reported)

        // Another branch at the same commit, then a detached HEAD.
        try await place.git(["checkout", "-q", "-b", "other"])
        var before = try await place.snapshot(child)
        #expect(await ChildMerge.toolMerge(child, expectedHead: head, parentCheckout: place.repo.path) == .refused(.branchNotCheckedOut))
        #expect(try await place.snapshot(child) == before)
        try await place.git(["checkout", "-q", "--detach", "main"])
        before = try await place.snapshot(child)
        #expect(await ChildMerge.toolMerge(child, expectedHead: head, parentCheckout: place.repo.path) == .refused(.branchNotCheckedOut))
        #expect(try await place.snapshot(child) == before)
        #expect(try await place.git(["rev-parse", "refs/heads/main"]) == base)

        // A parent folder that is no checkout of it.
        for folder in [place.base.path, place.base.appendingPathComponent("missing").path, "relative/folder"] {
            #expect(await ChildMerge.toolMerge(child, expectedHead: head, parentCheckout: folder) == .refused(.branchNotCheckedOut), "\(folder)")
        }

        // Back on the recorded branch, the merge goes through.
        try await place.git(["checkout", "-q", "main"])
        guard case .merged = await ChildMerge.toolMerge(child, expectedHead: head, parentCheckout: place.repo.path) else { Issue.record("not merged on main"); return }
        #expect(try await place.git(["rev-parse", "refs/heads/main"]) == head)
    }

    @Test func refusesAChildWhoseWorktreeIsGone() async throws {
        let place = try MergePlace(); defer { place.remove() }
        try await place.repository()
        var child = try await place.child("c1")
        let head = try await place.commit(in: child.worktreePath, "a.txt", "a\n")
        let reported = child.recordReport(head: head); #expect(reported)
        var gone = child; gone.worktreePath = place.root.appendingPathComponent("gone").path
        let before = try await place.snapshot(child)
        #expect(await ChildMerge.toolMerge(gone, expectedHead: head, parentCheckout: place.repo.path) == .refused(.worktreeMissing))
        #expect(try await place.snapshot(child) == before)
    }
}

/// A temp folder holding `repo` (the parent's checkout) and `worktrees` (the children's root). DelegationCardUndoTests shares it.
struct MergePlace {
    let base: URL
    var repo: URL { base.appendingPathComponent("repo", isDirectory: true) }
    var root: URL { base.appendingPathComponent("worktrees", isDirectory: true) }

    init() throws {
        base = FileManager.default.temporaryDirectory.appendingPathComponent("mighty-merge-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: base, withIntermediateDirectories: true)
    }

    func remove() { try? FileManager.default.removeItem(at: base) }

    /// `git init -b main` with one commit adding file-0.txt; returns its head.
    @discardableResult
    func repository() async throws -> String {
        try FileManager.default.createDirectory(at: repo, withIntermediateDirectories: true)
        try await git(["init", "-q", "-b", "main"])
        return try await commit(in: repo.path, "file-0.txt", "0\n")
    }

    /// A running child made the app's way: its own `mighty/<id>` branch and worktree at the parent's head.
    func child(_ id: String) async throws -> ChildRecord {
        let maker = ChildWorktreeMaker(root: root, freeBytes: { _ in 50_000_000_000 })
        guard case .created(let made) = await maker.create(sessionId: id, workspace: repo.path, task: "Do the work.\n") else { throw MergeFixtureError.childNotMade }
        return ChildRecord(id: id, parentSessionId: "parent", worktreePath: made.worktreePath, parentBranch: made.parentBranch, baseCommit: made.baseCommit,
                           startingMode: "acceptEdits", requestKey: "key-\(id)", state: .running)
    }

    /// Writes `name` in `folder` and commits it; returns the new head.
    func commit(in folder: String, _ name: String, _ text: String) async throws -> String {
        try Data(text.utf8).write(to: URL(fileURLWithPath: folder).appendingPathComponent(name))
        try await git(["add", "--", name], in: folder)
        try await git(["commit", "-q", "-m", "add \(name)"], in: folder)
        return try await git(["rev-parse", "HEAD"], in: folder)
    }

    /// Every ref, and each checkout's HEAD, index entries and file status: a
    /// refused merge leaves all of it as it was.
    func snapshot(_ child: ChildRecord) async throws -> [String] {
        var parts = [try await git(["for-each-ref", "--format=%(refname) %(objectname)"])]
        for folder in [repo.path, child.worktreePath] {
            parts.append(try await git(["rev-parse", "--symbolic-full-name", "HEAD"], in: folder))
            parts.append(try await git(["rev-parse", "HEAD"], in: folder))
            parts.append(try await git(["ls-files", "-s"], in: folder))
            parts.append(try await git(["status", "--porcelain", "--untracked-files=all"], in: folder))
        }
        return parts
    }

    /// Fixture git, kept away from the user's and the system's settings.
    @discardableResult
    func git(_ arguments: [String], in directory: String? = nil) async throws -> String {
        let executable = try #require(DelegationGit.executable)
        var environment = ProcessInfo.processInfo.environment.filter { !$0.key.hasPrefix("GIT_") }
        environment["GIT_CONFIG_GLOBAL"] = "/dev/null"; environment["GIT_CONFIG_NOSYSTEM"] = "1"; environment["GIT_OPTIONAL_LOCKS"] = "0"
        for key in ["GIT_AUTHOR", "GIT_COMMITTER"] { environment[key + "_NAME"] = "Fixture"; environment[key + "_EMAIL"] = "fixture@example.invalid" }
        let folder = directory ?? repo.path
        // Generous: the first git launch on a cold CI runner can take seconds.
        let result = try await ProcessCapture.run(executable: executable, arguments: ["-C", folder] + arguments, environment: environment, cwd: URL(fileURLWithPath: folder), timeout: 120)
        #expect(result.exitCode == 0, "git \(arguments.joined(separator: " ")): \(String(decoding: result.stderr, as: UTF8.self))")
        return DelegationGit.line(result.stdout)
    }
}

enum MergeFixtureError: Error { case childNotMade }
