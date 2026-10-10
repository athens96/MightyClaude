import Foundation
import Testing
@testable import MightyCore

/// Opens once; everyone waiting goes on.
private actor PaneGate {
    private var isOpen = false
    private var waiters: [CheckedContinuation<Void, Never>] = []
    func wait() async {
        if isOpen { return }
        await withCheckedContinuation { waiters.append($0) }
    }
    func open() { isOpen = true; waiters.forEach { $0.resume() }; waiters.removeAll() }
}

/// The app side as a human's close and discard see it: the panes a test sets,
/// and each stop it is asked for, noting whether the pane's folder was still
/// there at that moment. A child pane it is asked for is held until `gate`
/// opens, and then never made.
private final class CleanupHost: DelegationHost, @unchecked Sendable {
    private let lock = NSLock()
    private var panes: [String: DelegationPaneState] = [:]
    private var stopped: [String] = []
    private var asked: [String] = []
    private let gate: PaneGate?

    init(gate: PaneGate? = nil) { self.gate = gate }

    func set(_ pane: DelegationPaneState) { lock.withLock { panes[pane.sessionId] = pane } }
    var stops: [String] { lock.withLock { stopped } }
    var paneRequests: [String] { lock.withLock { asked } }

    func createPane(_ pane: DelegationChildPane) async -> Bool {
        lock.withLock { asked.append(pane.sessionId) }
        if let gate { await gate.wait() }
        return false
    }
    func startRun(sessionId: String, input: String) async -> String? { nil }
    func deliver(_ input: String, to sessionId: String, route: DeliveryRoute) async -> String? { nil }
    func paneState(sessionId: String) async -> DelegationPaneState? { lock.withLock { panes[sessionId] } }
    func stopRun(sessionId: String) async {
        lock.withLock {
            let there = panes[sessionId].map { FileManager.default.fileExists(atPath: $0.folder) } ?? false
            stopped.append("\(sessionId) \(there ? "with" : "without") its folder")
            panes[sessionId]?.activity = .idle
        }
    }
}

/// A ``MergePlace`` with a profile holding the delegation file, and
/// coordinators over it whose children's worktrees go under the place's root.
private struct CleanupPlace {
    let place: MergePlace
    let host: CleanupHost
    init(place: MergePlace, gate: PaneGate? = nil) { self.place = place; host = CleanupHost(gate: gate) }

    var store: DelegationFileStore { DelegationFileStore(directory: place.base.appendingPathComponent("profile", isDirectory: true)) }

    /// Saves `file` in the profile and loads a coordinator from it.
    func coordinator(_ file: DelegationFile) throws -> DelegationCoordinator {
        try store.save(file)
        return try DelegationCoordinator(store: store, host: host, worktrees: ChildWorktreeMaker(root: place.root, freeBytes: { _ in 50_000_000_000 }), isSwitchOn: { true })
    }

    /// The parent working in the repository, and child `id` working in its worktree.
    func openPanes(child id: String, folder: String, activity: DelegationPaneActivity) {
        host.set(DelegationPaneState(sessionId: "parent", permissionMode: "auto", folder: place.repo.path, runId: "run-1", activity: .finished))
        host.set(DelegationPaneState(sessionId: id, permissionMode: "acceptEdits", folder: folder, parentSessionId: "parent", runId: "run-\(id)", activity: activity))
    }
}

private enum CleanupFixtureError: Error { case notMerged }

/// A child made the app's way and linked to the parent's checkout, which
/// committed `<id>.txt`, wrote its report and was fast-forward-merged by the
/// parent's tool: the child as merged, and its merge record.
private func mergedChild(_ place: MergePlace, _ id: String = "c1") async throws -> (child: ChildRecord, merge: MergeRecord) {
    var child = try await place.child(id)
    child.parentCheckout = place.repo.path
    let head = try await place.commit(in: child.worktreePath, "\(id).txt", "\(id)\n")
    try Data("# Report of \(id)\nDone.\n".utf8).write(to: URL(fileURLWithPath: ChildWorktree.reportFile(worktreePath: child.worktreePath)))
    guard child.recordReport(head: head), case .merged(let merge) = await ChildMerge.toolMerge(child, expectedHead: head, parentCheckout: place.repo.path),
          child.apply(.merge) else { throw CleanupFixtureError.notMerged }
    return (child, merge)
}

/// Adds `pattern` to the repository's shared exclude file, which every worktree of it reads.
private func ignore(_ place: MergePlace, _ pattern: String) throws {
    let exclude = place.repo.appendingPathComponent(".git/info/exclude")
    try FileManager.default.createDirectory(at: exclude.deletingLastPathComponent(), withIntermediateDirectories: true)
    let old = (try? String(contentsOf: exclude, encoding: .utf8)) ?? ""
    try Data((old + pattern + "\n").utf8).write(to: exclude)
}

/// Every ref, every registered worktree, the parent's head and file status,
/// and every file under the worktree root with what it holds: a kept
/// cleanup leaves all of it as it was.
private func disk(_ place: MergePlace) async throws -> [String] {
    let parts = [try await place.git(["for-each-ref", "--format=%(refname) %(objectname)"]),
                 try await place.git(["worktree", "list", "--porcelain"]),
                 try await place.git(["rev-parse", "HEAD"]),
                 try await place.git(["status", "--porcelain", "--untracked-files=all"])]
    let root = place.root.path
    let files = ((try? FileManager.default.subpathsOfDirectory(atPath: root)) ?? []).map { sub -> String in
        let path = root + "/" + sub
        var folder: ObjCBool = false
        _ = FileManager.default.fileExists(atPath: path, isDirectory: &folder)
        return path + (folder.boolValue ? "/" : ": " + ((try? String(contentsOfFile: path, encoding: .utf8)) ?? "?"))
    }
    return parts + files.sorted()
}

/// Waits, within a generous bound, until `done` holds; whether it did.
private func eventually(_ done: () async -> Bool) async -> Bool {
    for _ in 0 ..< 1_000 {
        if await done() { return true }
        try? await Task.sleep(nanoseconds: 10_000_000)
    }
    return await done()
}

/// The paths of the registered worktrees, as git lists them.
private func worktreeList(_ place: MergePlace) async throws -> [String] {
    try await place.git(["worktree", "list", "--porcelain"]).split(separator: "\n").filter { $0.hasPrefix("worktree ") }.map { String($0.dropFirst(9)) }
}

/// A human's close of a merged child's pane and a human's discard from a
/// child's card, on real git repositories in a temp folder: cleanup stores
/// the copies first and never forces anything; discard is the one path that
/// removes unmerged work, including whatever a failed start left.
@Suite(.enabled(if: DelegationGit.executable != nil, "Requires a local Git executable"), .delegationLane)
struct DelegationCleanupTests {
    @Test func closingAMergedChildStoresItsCopiesThenRemovesItsWorktreeAndBranch() async throws {
        let place = try MergePlace(); defer { place.remove() }
        let base = try await place.repository()
        let (child, merge) = try await mergedChild(place)
        // Ignored build output beside TASK.md and REPORT.md goes with the worktree.
        try ignore(place, "build/")
        let build = URL(fileURLWithPath: child.worktreePath).appendingPathComponent("build", isDirectory: true)
        try FileManager.default.createDirectory(at: build, withIntermediateDirectories: true)
        try Data("o\n".utf8).write(to: build.appendingPathComponent("out.o"))
        let fixture = CleanupPlace(place: place)
        fixture.openPanes(child: "c1", folder: child.worktreePath, activity: .finished)
        let coordinator = try fixture.coordinator(DelegationFile(children: [child], merges: [merge]))

        #expect(await coordinator.closeChild("c1") == .cleaned)
        // Plain removal from the parent's side: the worktree with its ignored files, and the branch, are gone; the merge stays on main.
        #expect(!FileManager.default.fileExists(atPath: child.worktreePath))
        #expect(try await worktreeList(place) == [ChildCleanup.canonical(place.repo.path)])
        #expect(try await place.git(["for-each-ref", "--format=%(refname)", "refs/heads/mighty/"]).isEmpty)
        #expect(try await place.git(["rev-parse", "refs/heads/main"]) == merge.mergedCommit)
        #expect(try await place.git(["status", "--porcelain", "--untracked-files=all"]).isEmpty)
        #expect(fixture.host.stops.isEmpty)

        // The copies hold files that are gone now, so they were read and stored before the removal; the file on disk keeps them.
        let saved = try fixture.store.load()
        #expect(await coordinator.file == saved)
        let closed = try #require(saved.children.first)
        #expect(closed.state == .closed); #expect(closed.closedHead == merge.childHead)
        let report = try #require(saved.copy(childId: "c1", kind: .report))
        #expect(report.text == "# Report of c1\nDone.\n"); #expect(report.revision == 1); #expect(!report.truncated)
        #expect(saved.copy(childId: "c1", kind: .task)?.text == "Do the work.\n")
        #expect(saved.merges == [merge])

        // The merge record survives the close: undo makes the branch again at its recorded head and leaves the child closed.
        #expect(await ChildMerge.undo(merge, of: closed, parentCheckout: place.repo.path, parentActivity: .finished) == .undone(merge, restoredBranch: true))
        #expect(try await place.git(["rev-parse", "refs/heads/mighty/c1"]) == merge.childHead)
        #expect(try await place.git(["rev-parse", "HEAD"]) == base)
    }

    @Test func cleanupChangesNothingOnDiskUnlessEveryConditionHolds() async throws {
        let place = try MergePlace(); defer { place.remove() }
        try await place.repository()
        let (child, merge) = try await mergedChild(place)
        let worktree = URL(fileURLWithPath: child.worktreePath)
        func expectKept(_ hold: ChildCleanupHold, _ subject: ChildRecord? = nil, _ record: MergeRecord? = nil, activity: DelegationPaneActivity? = .finished) async throws {
            let before = try await disk(place)
            let outcome = await ChildCleanup.cleanUp(subject ?? child, merge: record ?? merge, parentCheckout: place.repo.path, childActivity: activity)
            #expect(outcome == .kept(hold), "\(hold)")
            #expect(try await disk(place) == before, "\(hold)")
        }

        // Not merged: every other state, or another child's merge record.
        for state in ChildState.allCases where state != .merged {
            var other = child; other.state = state
            try await expectKept(.notMerged, other)
        }
        var stranger = merge; stranger.childId = "c9"
        try await expectKept(.notMerged, nil, stranger)
        // A run is going in the child's pane.
        try await expectKept(.childBusy, activity: .running)
        // The worktree is gone, or is a folder the parent's repository does not list.
        var gone = child; gone.worktreePath = place.root.appendingPathComponent("gone").path
        try await expectKept(.worktreeMissing, gone)
        let stray = place.root.appendingPathComponent("stray", isDirectory: true)
        try FileManager.default.createDirectory(at: stray, withIntermediateDirectories: true)
        var unlisted = child; unlisted.worktreePath = stray.path
        try await expectKept(.worktreeMissing, unlisted)
        try FileManager.default.removeItem(at: stray)

        // The parent's checkout left the recorded branch, or that branch no longer holds the child's head.
        try await place.git(["checkout", "-q", "-b", "elsewhere"])
        try await expectKept(.branchNotCheckedOut)
        try await place.git(["checkout", "-q", "main"])
        try await place.git(["branch", "-q", "-D", "elsewhere"])
        try await place.git(["reset", "-q", "--hard", child.baseCommit])
        try await expectKept(.notInParent)
        try await place.git(["reset", "-q", "--hard", merge.mergedCommit])

        // The child's branch moved past the merge, or its worktree left the branch at the merged head.
        _ = try await place.commit(in: child.worktreePath, "later.txt", "later\n")
        try await expectKept(.headMoved)
        try await place.git(["reset", "-q", "--hard", merge.childHead], in: child.worktreePath)
        try await place.git(["checkout", "-q", "--detach"], in: child.worktreePath)
        try await expectKept(.headMoved)
        try await place.git(["checkout", "-q", child.branch], in: child.worktreePath)

        // A tracked change, unstaged and then staged.
        try Data("changed\n".utf8).write(to: worktree.appendingPathComponent("c1.txt"))
        try await expectKept(.trackedChanges)
        try await place.git(["add", "c1.txt"], in: child.worktreePath)
        try await expectKept(.trackedChanges)
        try await place.git(["reset", "-q", "--hard"], in: child.worktreePath)

        // A file no ignore rule covers.
        try Data("note\n".utf8).write(to: worktree.appendingPathComponent("notes.txt"))
        try await expectKept(.untrackedFiles)
        try FileManager.default.removeItem(at: worktree.appendingPathComponent("notes.txt"))

        // A registered worktree inside the child's, hidden by an ignore rule and holding work of its own: plain removal would take it along.
        try ignore(place, "nested/")
        let nested = worktree.appendingPathComponent("nested", isDirectory: true)
        try await place.git(["worktree", "add", "-q", "-b", "side", nested.path, "main"])
        try Data("side work\n".utf8).write(to: nested.appendingPathComponent("side.txt"))
        try await expectKept(.nestedWorktree)
        #expect(await ChildCleanup.nestedWorktrees(of: child, parentCheckout: place.repo.path) == [ChildCleanup.canonical(nested.path)])
        #expect(try String(contentsOf: nested.appendingPathComponent("side.txt"), encoding: .utf8) == "side work\n")
        try await place.git(["worktree", "remove", "--force", nested.path])

        // With every condition met, and the pane already closed, the cleanup goes through and leaves the nested worktree's branch alone.
        #expect(await ChildCleanup.cleanUp(child, merge: merge, parentCheckout: place.repo.path, childActivity: nil) == .cleaned)
        #expect(!FileManager.default.fileExists(atPath: child.worktreePath))
        #expect(try await worktreeList(place) == [ChildCleanup.canonical(place.repo.path)])
        #expect(try await place.git(["for-each-ref", "--format=%(refname)", "refs/heads/"]) == "refs/heads/main\nrefs/heads/side")
    }

    @Test func closingKeepsTheWorktreeAndBranchUnlessTheChildIsMergedCleanAndItsCopiesAreSaved() async throws {
        let place = try MergePlace(); defer { place.remove() }
        try await place.repository()
        // c1 merged but left an untracked file; c2 runs unmerged work; c3 merged clean.
        let (c1, m1) = try await mergedChild(place, "c1")
        try Data("note\n".utf8).write(to: URL(fileURLWithPath: c1.worktreePath).appendingPathComponent("notes.txt"))
        var c2 = try await place.child("c2"); c2.parentCheckout = place.repo.path
        _ = try await place.commit(in: c2.worktreePath, "c2.txt", "c2\n")
        try Data("unsaved\n".utf8).write(to: URL(fileURLWithPath: c2.worktreePath).appendingPathComponent("c2.txt"))
        let (c3, m3) = try await mergedChild(place, "c3")
        let fixture = CleanupPlace(place: place)
        fixture.openPanes(child: "c2", folder: c2.worktreePath, activity: .running)
        let coordinator = try fixture.coordinator(DelegationFile(children: [c1, c2, c3], merges: [m1, m3]))

        // Not merged: only the pane closes, and the card stays.
        var before = try await disk(place)
        #expect(await coordinator.closeChild("c2") == .kept(.notMerged))
        #expect(try await disk(place) == before)
        var saved = try fixture.store.load()
        #expect(saved.children.map(\.state) == [.merged, .closed, .merged])
        #expect(saved.children[1].closedHead == nil); #expect(saved.copy(childId: "c2", kind: .report) == nil)
        // A closed child's unmerged work stays until a human discards it from its card.
        #expect(await coordinator.discardChild("c2") == .discarded)
        #expect(fixture.host.stops == ["c2 with its folder"])
        #expect(!FileManager.default.fileExists(atPath: c2.worktreePath))
        #expect(try await place.git(["for-each-ref", "--format=%(refname)", "refs/heads/mighty/"]) == "refs/heads/mighty/c1\nrefs/heads/mighty/c3")

        // Merged, but a check fails: the copies are stored all the same, and nothing on disk changes.
        before = try await disk(place)
        #expect(await coordinator.closeChild("c1") == .kept(.untrackedFiles))
        #expect(try await disk(place) == before)
        saved = try fixture.store.load()
        #expect(saved.children[0].state == .closed); #expect(saved.children[0].closedHead == m1.childHead)
        #expect(saved.copy(childId: "c1", kind: .report)?.text == "# Report of c1\nDone.\n")
        #expect(saved.copy(childId: "c1", kind: .task)?.text == "Do the work.\n")
        #expect(saved.merges == [m1, m3])
        // A closed child closes no further.
        #expect(await coordinator.closeChild("c1") == .kept(.notMerged))
        #expect(try await disk(place) == before)

        // Merged and clean, but its copies cannot be saved: nothing is removed.
        try FileManager.default.removeItem(at: fixture.store.fileURL)
        try FileManager.default.createDirectory(at: fixture.store.fileURL, withIntermediateDirectories: true)
        try Data("in the way\n".utf8).write(to: fixture.store.fileURL.appendingPathComponent("blocker"))
        before = try await disk(place)
        guard case .failed = await coordinator.closeChild("c3") else { Issue.record("closed without its copies saved"); return }
        #expect(try await disk(place) == before)
        #expect(FileManager.default.fileExists(atPath: c3.worktreePath))
        #expect(try await place.git(["rev-parse", "refs/heads/mighty/c3"]) == m3.childHead)
        #expect(await coordinator.file.children[2].state == .closed)
    }

    @Test func discardStopsTheChildKeepsItsReportAndForceRemovesItsUnmergedWork() async throws {
        let place = try MergePlace(); defer { place.remove() }
        let base = try await place.repository()
        var child = try await place.child("c1"); child.parentCheckout = place.repo.path
        let worktree = URL(fileURLWithPath: child.worktreePath)
        // Unmerged work of every kind: a commit only on the child's branch, a tracked change, an untracked file, and a locked nested worktree with work of its own.
        _ = try await place.commit(in: child.worktreePath, "c1.txt", "c1\n")
        try Data("more\n".utf8).write(to: worktree.appendingPathComponent("c1.txt"))
        try Data("note\n".utf8).write(to: worktree.appendingPathComponent("notes.txt"))
        let nested = worktree.appendingPathComponent("nested", isDirectory: true)
        try await place.git(["worktree", "add", "-q", "-b", "side", nested.path, "main"])
        try await place.git(["worktree", "lock", nested.path])
        try Data("side work\n".utf8).write(to: nested.appendingPathComponent("side.txt"))
        try Data("# Report\nHalf done.\n".utf8).write(to: URL(fileURLWithPath: ChildWorktree.reportFile(worktreePath: child.worktreePath)))
        let fixture = CleanupPlace(place: place)
        fixture.openPanes(child: "c1", folder: child.worktreePath, activity: .running)
        let coordinator = try fixture.coordinator(DelegationFile(children: [child]))

        // The confirmation can name the nested worktree.
        #expect(await ChildCleanup.nestedWorktrees(of: child, parentCheckout: place.repo.path) == [ChildCleanup.canonical(nested.path)])
        #expect(await coordinator.discardChild("c1") == .discarded)
        // The child's run was stopped first, while its worktree was still there.
        #expect(fixture.host.stops == ["c1 with its folder"])
        #expect(await fixture.host.paneState(sessionId: "c1")?.activity == .idle)
        // Its worktree, the nested one and its unmerged branch are gone, by force.
        #expect(!FileManager.default.fileExists(atPath: child.worktreePath))
        #expect(try await worktreeList(place) == [ChildCleanup.canonical(place.repo.path)])
        #expect(try await place.git(["for-each-ref", "--format=%(refname)", "refs/heads/mighty/"]).isEmpty)
        // The parent's branch and checkout are untouched, and so is the nested worktree's own branch.
        #expect(try await place.git(["rev-parse", "refs/heads/main"]) == base)
        #expect(try await place.git(["rev-parse", "refs/heads/side"]) == base)
        #expect(try await place.git(["status", "--porcelain", "--untracked-files=all"]).isEmpty)
        // A copy of its report is kept, and the child is discarded, in the file on disk.
        let saved = try fixture.store.load()
        #expect(await coordinator.file == saved)
        #expect(saved.children.map(\.state) == [.discarded])
        #expect(saved.copy(childId: "c1", kind: .report)?.text == "# Report\nHalf done.\n")
        // Discarded is final: another discard is refused and stops nothing.
        guard case .failed = await coordinator.discardChild("c1") else { Issue.record("discarded twice"); return }
        #expect(fixture.host.stops.count == 1)
        #expect(try fixture.store.load() == saved)
    }

    @Test func discardRemovesWhatAFailedStartLeft() async throws {
        let place = try MergePlace(); defer { place.remove() }
        try await place.repository()
        let maker = ChildWorktreeMaker(root: place.root, freeBytes: { _ in 50_000_000_000 })
        guard case .ready(let first) = await maker.check(workspace: place.repo.path) else { Issue.record("the workspace cannot host a child"); return }
        func failed(_ id: String) -> ChildRecord {
            ChildRecord(id: id, parentSessionId: "parent", worktreePath: maker.worktreePath(for: id), parentBranch: "main", baseCommit: first.baseCommit,
                        startingMode: "plan", requestKey: "key-\(id)", state: .failed, parentCheckout: place.repo.path)
        }

        // f1: its place was taken, so `git worktree add` failed after making the branch.
        let taken = place.root.appendingPathComponent("f1", isDirectory: true)
        try FileManager.default.createDirectory(at: taken, withIntermediateDirectories: true)
        try Data("junk\n".utf8).write(to: taken.appendingPathComponent("junk.txt"))
        guard case .failed = await maker.make(sessionId: "f1", base: first, task: "Task\n") else { Issue.record("f1 was made"); return }
        // f2: its worktree was made but not its notes, since the workspace has a folder of that name.
        try FileManager.default.createDirectory(at: place.repo.appendingPathComponent(ChildWorktree.notesFolder), withIntermediateDirectories: true)
        _ = try await place.commit(in: place.repo.path, ChildWorktree.notesFolder + "/keep", "keep\n")
        guard case .ready(let second) = await maker.check(workspace: place.repo.path) else { Issue.record("the workspace cannot host a child"); return }
        guard case .failed = await maker.make(sessionId: "f2", base: second, task: "Task\n") else { Issue.record("f2 was made"); return }
        #expect(FileManager.default.fileExists(atPath: maker.worktreePath(for: "f2")))
        // f3: a start killed partway, its worktree still locked while being made and its folder gone.
        try await place.git(["worktree", "add", "-q", "-b", "mighty/f3", maker.worktreePath(for: "f3"), "main"])
        try await place.git(["worktree", "lock", "--reason", "initializing", maker.worktreePath(for: "f3")])
        try FileManager.default.removeItem(atPath: maker.worktreePath(for: "f3"))
        // f4: a start that failed before it made anything.
        #expect(try await place.git(["for-each-ref", "--format=%(refname)", "refs/heads/mighty/"]) == "refs/heads/mighty/f1\nrefs/heads/mighty/f2\nrefs/heads/mighty/f3")
        #expect(try await worktreeList(place).count == 3)
        let main = try await place.git(["rev-parse", "refs/heads/main"])

        let fixture = CleanupPlace(place: place)
        let coordinator = try fixture.coordinator(DelegationFile(children: ["f1", "f2", "f3", "f4"].map(failed)))
        for id in ["f1", "f2", "f3", "f4"] { #expect(await coordinator.discardChild(id) == .discarded, "\(id)") }
        // Nothing of them is left: no branch, no registration, no folder under the root; the parent's branch is untouched.
        #expect(try await place.git(["for-each-ref", "--format=%(refname)", "refs/heads/mighty/"]).isEmpty)
        #expect(try await worktreeList(place) == [ChildCleanup.canonical(place.repo.path)])
        #expect(try FileManager.default.contentsOfDirectory(atPath: place.root.path).isEmpty)
        #expect(try await place.git(["rev-parse", "refs/heads/main"]) == main)
        #expect(try await place.git(["status", "--porcelain", "--untracked-files=all"]).isEmpty)
        #expect(try fixture.store.load().children.map(\.state) == [.discarded, .discarded, .discarded, .discarded])

        // A folder that is not the child's place under the worktree root is never removed.
        let elsewhere = place.base.appendingPathComponent("elsewhere/f5", isDirectory: true)
        try FileManager.default.createDirectory(at: elsewhere, withIntermediateDirectories: true)
        try Data("mine\n".utf8).write(to: elsewhere.appendingPathComponent("keep.txt"))
        var outside = failed("f5"); outside.worktreePath = elsewhere.path
        guard case .failed = await ChildCleanup.discard(outside, parentCheckout: place.repo.path, worktreeRoot: place.root) else { Issue.record("a folder outside the root was removed"); return }
        #expect(try String(contentsOf: elsewhere.appendingPathComponent("keep.txt"), encoding: .utf8) == "mine\n")
    }

    @Test func discardWaitsForAChildsStartThenRemovesWhatItLeft() async throws {
        let place = try MergePlace(); defer { place.remove() }
        try await place.repository()
        let gate = PaneGate()
        let fixture = CleanupPlace(place: place, gate: gate)
        fixture.host.set(DelegationPaneState(sessionId: "parent", permissionMode: "auto", folder: place.repo.path, runId: "run-1", activity: .running))
        let coordinator = try fixture.coordinator(DelegationFile())
        let location = PaneMCPServerLocation(socketPath: place.base.appendingPathComponent("io.sock").path, executable: URL(fileURLWithPath: "/usr/bin/true"))
        let binding = PaneMCPBinding(agentPaneId: "parent", token: "token-parent", server: location, workspaceId: "ws-1", workspacePath: place.repo.path, provider: "claude", delegation: true)
        let answer = await coordinator.handle(DelegationRequest(tool: "delegate", arguments: ["task": "Do the work.", "mode": "plan"]), binding: binding)
        let id = try #require(answer.child?.id)
        // The record keeps the parent's side, for cleanup and discard after the parent pane closes.
        let recorded = try #require(await coordinator.file.children.first?.parentCheckout)
        #expect(ChildCleanup.canonical(recorded) == ChildCleanup.canonical(place.repo.path))

        // Its worktree is made and its pane asked for, but its start has not finished: discard is refused and changes nothing.
        #expect(await eventually { fixture.host.paneRequests == [id] })
        let before = try await disk(place)
        guard case .failed = await coordinator.discardChild(id) else { Issue.record("discarded while starting"); return }
        #expect(try await disk(place) == before)
        #expect(fixture.host.stops.isEmpty)
        #expect(await coordinator.file.children.first?.state == .creating)

        // No pane was made, so the start failed and left its worktree and branch for a human to discard.
        await gate.open()
        #expect(await eventually { await coordinator.file.children.first?.state == .failed })
        #expect(FileManager.default.fileExists(atPath: ChildWorktree.taskFile(worktreePath: place.root.appendingPathComponent(id).path)))
        #expect(try await place.git(["rev-parse", "-q", "--verify", "refs/heads/mighty/\(id)"]).count == 40)
        #expect(await coordinator.discardChild(id) == .discarded)
        #expect(try await place.git(["for-each-ref", "--format=%(refname)", "refs/heads/mighty/"]).isEmpty)
        #expect(try await worktreeList(place) == [ChildCleanup.canonical(place.repo.path)])
        #expect(try FileManager.default.contentsOfDirectory(atPath: place.root.path).isEmpty)
        #expect(try fixture.store.load().children.map(\.state) == [.discarded])
    }

    // MARK: Removing a workspace

    private func removalChild(_ id: String, parent: String, state: ChildState, checkout: String? = "/work/app") -> ChildRecord {
        ChildRecord(id: id, parentSessionId: parent, worktreePath: "/worktrees/" + id, parentBranch: "main", baseCommit: String(repeating: "a", count: 40),
                    startingMode: "default", requestKey: "key-" + id, state: state, parentCheckout: checkout)
    }

    @Test func aWorkspaceWithAnOpenChildIsRefusedWhicheverWayTheChildIsItsOwn() {
        let none: (String) -> Bool = { _ in false }
        for state in ChildState.allCases where state.isOpen {
            // By the parent's pane, the child's own pane, or the parent's checkout alone.
            #expect(DelegationWorkspaceRemoval.refusal(children: [removalChild("c1", parent: "p1", state: state, checkout: nil)], paneIds: ["p1"], folder: "/work/app", worktreeExists: none) == .workspaceHasChildren)
            #expect(DelegationWorkspaceRemoval.refusal(children: [removalChild("c1", parent: "gone", state: state, checkout: nil)], paneIds: ["c1"], folder: "/work/app", worktreeExists: none) == .workspaceHasChildren)
            #expect(DelegationWorkspaceRemoval.refusal(children: [removalChild("c1", parent: "gone", state: state)], paneIds: [], folder: "/work/app", worktreeExists: none) == .workspaceHasChildren)
            #expect(DelegationWorkspaceRemoval.refusal(children: [removalChild("c1", parent: "gone", state: state, checkout: "/work/app/sub")], paneIds: [], folder: "/work/app", worktreeExists: none) == .workspaceHasChildren)
        }
    }

    @Test func aWorkspaceWithAnUncleanedChildWorktreeIsRefusedUntilItIsGoneOrDiscarded() {
        var onDisk: Set<String> = ["/worktrees/c1"]
        let exists: (String) -> Bool = { onDisk.contains($0) }
        for state in [ChildState.closed, .failed] {
            #expect(DelegationWorkspaceRemoval.refusal(children: [removalChild("c1", parent: "p1", state: state)], paneIds: [], folder: "/work/app", worktreeExists: exists) == .workspaceHasChildren)
        }
        // A discarded child never counts; a cleaned one's worktree is gone.
        #expect(DelegationWorkspaceRemoval.refusal(children: [removalChild("c1", parent: "p1", state: .discarded)], paneIds: ["p1"], folder: "/work/app", worktreeExists: exists) == nil)
        onDisk = []
        #expect(DelegationWorkspaceRemoval.refusal(children: [removalChild("c1", parent: "p1", state: .closed)], paneIds: ["p1"], folder: "/work/app", worktreeExists: exists) == nil)
        #expect(DelegationWorkspaceRemoval.refusal(children: [removalChild("c1", parent: "p1", state: .failed)], paneIds: ["p1"], folder: "/work/app", worktreeExists: exists) == nil)
    }

    @Test func anotherWorkspacesChildrenNeverRefuseTheRemoval() {
        let all: (String) -> Bool = { _ in true }
        let children = [removalChild("c1", parent: "p1", state: .running, checkout: "/work/other"),
                        removalChild("c2", parent: "p2", state: .closed, checkout: "/work/apple")]
        #expect(DelegationWorkspaceRemoval.refusal(children: children, paneIds: ["p3"], folder: "/work/app", worktreeExists: all) == nil)
        #expect(DelegationWorkspaceRemoval.refusal(children: children, paneIds: ["p2"], folder: "/work/app", worktreeExists: all) == .workspaceHasChildren)
    }
}
