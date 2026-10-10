import Foundation
import Testing
@testable import MightyCore

/// Opens once; everyone waiting goes on.
private actor Gate {
    private var isOpen = false
    private var waiters: [CheckedContinuation<Void, Never>] = []
    func wait() async {
        if isOpen { return }
        await withCheckedContinuation { waiters.append($0) }
    }
    func open() { isOpen = true; waiters.forEach { $0.resume() }; waiters.removeAll() }
}

/// The app side as delegate sees it: the panes a test sets up, and every
/// child pane and run it is asked for. With a gate, a pane is asked for and
/// then held until the gate opens.
private final class DelegateHost: DelegationHost, @unchecked Sendable {
    private let lock = NSLock()
    private var panes: [String: DelegationPaneState] = [:]
    private var made: [DelegationChildPane] = []
    private var runs: [(sessionId: String, input: String)] = []
    private var paneFails = false, runFails = false
    private let gate: Gate?

    init(gate: Gate?) { self.gate = gate }

    func set(_ pane: DelegationPaneState) { lock.withLock { panes[pane.sessionId] = pane } }
    func fail(pane: Bool = false, run: Bool = false) { lock.withLock { paneFails = pane; runFails = run } }
    var createdPanes: [DelegationChildPane] { lock.withLock { made } }
    var startedRuns: [(sessionId: String, input: String)] { lock.withLock { runs } }

    func createPane(_ pane: DelegationChildPane) async -> Bool {
        lock.withLock { made.append(pane) }
        if let gate { await gate.wait() }
        return lock.withLock {
            guard !paneFails else { return false }
            panes[pane.sessionId] = DelegationPaneState(sessionId: pane.sessionId, permissionMode: pane.mode, folder: pane.folder, parentSessionId: pane.parentSessionId)
            return true
        }
    }

    func startRun(sessionId: String, input: String) async -> String? {
        lock.withLock { runs.append((sessionId, input)); return runFails ? nil : "run-of-\(sessionId)" }
    }

    func deliver(_ input: String, to sessionId: String, route: DeliveryRoute) async -> String? { nil }
    func paneState(sessionId: String) async -> DelegationPaneState? { lock.withLock { panes[sessionId] } }
    func stopRun(sessionId: String) async {}
}

/// A temp folder holding the workspace repository, the worktree root and the
/// profile with the delegation file, and a coordinator over them. The pane
/// "parent" works in the repository in run "run-1".
private struct Fixture {
    let base: URL
    let host: DelegateHost
    let store: DelegationFileStore
    let coordinator: DelegationCoordinator
    var repo: URL { base.appendingPathComponent("repo", isDirectory: true) }
    var root: URL { base.appendingPathComponent("worktrees", isDirectory: true) }

    /// `commits` nil leaves the repository folder without git.
    static func make(commits: Int? = 1, file: DelegationFile = DelegationFile(), parentMode: String = "auto", freeBytes: Int64 = 50_000_000_000, gate: Gate? = nil) async throws -> Fixture {
        let base = FileManager.default.temporaryDirectory.appendingPathComponent("mighty-delegate-\(UUID().uuidString)", isDirectory: true)
        let store = DelegationFileStore(directory: base.appendingPathComponent("profile", isDirectory: true))
        try store.save(file)
        let host = DelegateHost(gate: gate)
        let maker = ChildWorktreeMaker(root: base.appendingPathComponent("worktrees", isDirectory: true), freeBytes: { _ in freeBytes })
        let fixture = Fixture(base: base, host: host, store: store, coordinator: try DelegationCoordinator(store: store, host: host, worktrees: maker, isSwitchOn: { true }))
        try FileManager.default.createDirectory(at: fixture.repo, withIntermediateDirectories: true)
        if let commits {
            try await fixture.git(["init", "-q", "-b", "main"])
            for index in 0 ..< commits {
                try Data("\(index)\n".utf8).write(to: fixture.repo.appendingPathComponent("file-\(index).txt"))
                try await fixture.git(["add", "-A"]); try await fixture.git(["commit", "-q", "-m", "commit \(index)"])
            }
        }
        fixture.setParent(mode: parentMode)
        return fixture
    }

    func setParent(mode: String, runId: String = "run-1") {
        host.set(DelegationPaneState(sessionId: "parent", permissionMode: mode, folder: repo.path, runId: runId, activity: .running))
    }

    func remove() { try? FileManager.default.removeItem(at: base) }

    /// delegate(task, mode) from `caller`, as its pane token resolves.
    func delegate(_ task: String, mode: String, from caller: String = "parent") async -> DelegationResponse {
        let location = PaneMCPServerLocation(socketPath: base.appendingPathComponent("io.sock").path, executable: URL(fileURLWithPath: "/usr/bin/true"))
        let binding = PaneMCPBinding(agentPaneId: caller, token: "token-\(caller)", server: location, workspaceId: "ws-1", workspacePath: repo.path, provider: "claude", delegation: true)
        return await coordinator.handle(DelegationRequest(tool: "delegate", arguments: ["task": task, "mode": mode]), binding: binding)
    }

    /// The delegation file's bytes and the coordinator's copy of it.
    func snapshot() async throws -> (Data, DelegationFile) { (try Data(contentsOf: store.fileURL), await coordinator.file) }

    /// A refused call made nothing: the same file, no worktree root, no
    /// `mighty/*` branch, no second worktree, and no pane or run asked for.
    func expectNothingMade(since before: (Data, DelegationFile), git hasGit: Bool = true) async throws {
        #expect(try Data(contentsOf: store.fileURL) == before.0)
        #expect(await coordinator.file == before.1)
        #expect(!FileManager.default.fileExists(atPath: root.path))
        if hasGit {
            #expect(try await git(["for-each-ref", "--format=%(refname)", "refs/heads/mighty"]).isEmpty)
            #expect(try await git(["worktree", "list", "--porcelain"]).components(separatedBy: "worktree ").count == 2)
        }
        #expect(host.createdPanes.isEmpty)
        #expect(host.startedRuns.isEmpty)
    }

    /// The child's state as the coordinator holds it, once it equals `state`.
    /// Until the child is in `state` and its start is over, the delivery of a failed start's notice included.
    func waitFor(_ id: String, _ state: ChildState) async -> Bool {
        await waitUntil {
            let reached = await coordinator.file.children.first(where: { $0.id == id })?.state == state
            let starting = await coordinator.starting.contains(id)
            return reached && !starting
        }
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

/// Polls a condition on real time; the bound only ends a test that broke.
private func waitUntil(timeout: TimeInterval = 60, _ condition: () async -> Bool) async -> Bool {
    let deadline = Date().addingTimeInterval(timeout)
    while Date() < deadline {
        if await condition() { return true }
        try? await Task.sleep(nanoseconds: 10_000_000)
    }
    return await condition()
}

/// A child record that only needs to exist: no worktree behind it.
private func record(_ id: String, parent: String = "parent", state: ChildState) -> ChildRecord {
    ChildRecord(id: id, parentSessionId: parent, worktreePath: "/nonexistent/worktrees/\(id)", parentBranch: "main", baseCommit: String(repeating: "a", count: 40),
                startingMode: "plan", requestKey: "key-\(id)", state: state)
}

/// Real temp repositories and a fake host: the delegate tool from the parent's call to its child's first run.
@Suite(.enabled(if: DelegationGit.executable != nil, "Requires a local Git executable"), .delegationLane)
struct DelegationDelegateToolTests {
    @Test func delegateAnswersOnceTheRecordExistsInCreatingThenOpensTheChildInTheAskedModeAndRunsIt() async throws {
        let gate = Gate()
        let fixture = try await Fixture.make(gate: gate)
        defer { fixture.remove() }
        let head = try await fixture.git(["rev-parse", "HEAD"])
        let task = "Write the release notes.\n"

        let answer = await fixture.delegate(task, mode: "acceptEdits")
        let info = try #require(answer.child, "\(answer)")
        #expect(answer.refused == nil && answer.error == nil)
        #expect(info.state == .creating)
        #expect(info.branch == "mighty/" + info.id)
        #expect(info.startingMode == "acceptEdits")
        #expect(ChildWorktreeMaker.isSafeSessionId(info.id))

        // The answer came once the record was saved in creating, with the call's key and a copy of its task.
        let saved = try fixture.store.load()
        let child = try #require(saved.children.first)
        #expect(saved.children.count == 1)
        #expect(child.id == info.id && child.state == .creating)
        #expect(child.parentSessionId == "parent")
        #expect(child.startingMode == "acceptEdits")
        #expect(child.requestKey == DelegationRequestKey.make(parentSessionId: "parent", parentRunId: "run-1", task: task, startingMode: "acceptEdits"))
        #expect(child.worktreePath == fixture.root.appendingPathComponent(info.id).path)
        #expect(child.parentBranch == "main" && child.baseCommit == head)
        #expect(saved.copy(childId: info.id, kind: .task)?.text == task)
        #expect(saved.notices.isEmpty)

        // The host is asked for the child's pane in the asked mode, working in its own worktree.
        #expect(await waitUntil { fixture.host.createdPanes.count == 1 })
        #expect(fixture.host.createdPanes == [DelegationChildPane(sessionId: info.id, parentSessionId: "parent", mode: "acceptEdits", folder: child.worktreePath)])
        #expect(try String(contentsOfFile: ChildWorktree.taskFile(worktreePath: child.worktreePath), encoding: .utf8) == task)
        #expect(try await fixture.git(["rev-parse", "refs/heads/" + info.branch]) == head)
        #expect(await fixture.coordinator.file.children.first?.state == .creating)
        #expect(fixture.host.startedRuns.isEmpty)

        // Once its pane is made, its first run starts and it is running.
        await gate.open()
        #expect(await fixture.waitFor(info.id, .running))
        #expect(try fixture.store.load().children.map(\.state) == [.running])
        #expect(try fixture.store.load().notices.isEmpty)
        let runs = fixture.host.startedRuns
        #expect(runs.count == 1)
        #expect(runs.first?.sessionId == info.id)
        #expect(runs.first?.input.contains(task) == true)
        #expect(runs.first?.input.contains(ChildWorktree.reportFile(worktreePath: child.worktreePath)) == true)
        // The parent's checkout is untouched.
        #expect(try await fixture.git(["symbolic-ref", "--short", "HEAD"]) == "main")
        #expect(try await fixture.git(["status", "--porcelain", "--untracked-files=all"]).isEmpty)

        // Through the delegation MCP server the child is tool data.
        let result = DelegationMCPServer.result(answer)
        #expect(result["isError"] == nil)
        #expect(result["structuredContent"] as? [String: [String: String]] == ["child": ["id": info.id, "state": "creating", "branch": info.branch, "mode": "acceptEdits"]])
    }

    @Test func theSameCallTwiceInOneParentRunReturnsTheSameChild() async throws {
        let fixture = try await Fixture.make()
        defer { fixture.remove() }
        let first = try #require(await fixture.delegate("Fix the parser.", mode: "plan").child)
        #expect(await fixture.delegate("Fix the parser.", mode: "plan").child?.id == first.id)
        #expect(await fixture.waitFor(first.id, .running))
        // Again once it runs, and twice at once: still that child, now running.
        async let again = fixture.delegate("Fix the parser.", mode: "plan")
        async let twice = fixture.delegate("Fix the parser.", mode: "plan")
        let repeats = await [again, twice]
        #expect(repeats.map(\.child?.id) == [first.id, first.id])
        #expect(repeats.map(\.child?.state) == [.running, .running])

        // A new call made twice at once is still one child.
        async let one = fixture.delegate("Write the docs.", mode: "plan")
        async let other = fixture.delegate("Write the docs.", mode: "plan")
        let both = await [one, other]
        let second = try #require(both[0].child)
        #expect(both[1].child?.id == second.id)
        #expect(second.id != first.id)

        // The same task and mode in the parent's next run is a new call.
        fixture.setParent(mode: "auto", runId: "run-2")
        let third = try #require(await fixture.delegate("Fix the parser.", mode: "plan").child)
        #expect(Set([first.id, second.id, third.id]).count == 3)

        for id in [second.id, third.id] { #expect(await fixture.waitFor(id, .running)) }
        let saved = try fixture.store.load()
        #expect(saved.children.map(\.id) == [first.id, second.id, third.id])
        #expect(Set(saved.children.map(\.requestKey)).count == 3)
        #expect(fixture.host.createdPanes.map(\.sessionId) == [first.id, second.id, third.id])
        #expect(fixture.host.startedRuns.map(\.sessionId) == [first.id, second.id, third.id])
        let branches = try await fixture.git(["for-each-ref", "--format=%(refname)", "refs/heads/mighty"])
        #expect(Set(branches.split(separator: "\n").map(String.init)) == Set([first, second, third].map { "refs/heads/" + $0.branch }))
    }

    @Test func aPaneWithAParentLinkCannotDelegateAndNothingIsMade() async throws {
        // "recorded" is a child in the delegation file; "linked" is one by its pane's link.
        let fixture = try await Fixture.make(file: DelegationFile(children: [record("recorded", state: .running)]))
        defer { fixture.remove() }
        fixture.host.set(DelegationPaneState(sessionId: "recorded", permissionMode: "fullAccess", folder: fixture.repo.path, runId: "run-r", activity: .running))
        fixture.host.set(DelegationPaneState(sessionId: "linked", permissionMode: "fullAccess", folder: fixture.repo.path, parentSessionId: "parent", runId: "run-l", activity: .running))
        let before = try await fixture.snapshot()
        for caller in ["recorded", "linked"] {
            #expect(await fixture.delegate("Split the work further.", mode: "plan", from: caller) == .refusal(.childCannotDelegate), "\(caller)")
        }
        try await fixture.expectNothingMade(since: before)
        #expect(DelegationCoordinator.maximumDepth == 1)
    }

    @Test func threeOpenChildrenHitTheWidthCapAndOnlyOpenChildrenCount() async throws {
        let others = [record("waiting", state: .waiting), record("merged", state: .merged), record("failed", state: .failed),
                      record("closed", state: .closed), record("discarded", state: .discarded), record("not-mine", parent: "someone-else", state: .running)]
        let fixture = try await Fixture.make(file: DelegationFile(children: others))
        defer { fixture.remove() }
        // Two open children of its own: a third may be made.
        let third = try #require(await fixture.delegate("The third one.", mode: "plan").child)
        #expect(await fixture.waitFor(third.id, .running))

        let before = try await fixture.snapshot()
        let branches = try await fixture.git(["for-each-ref", "--format=%(refname)", "refs/heads/mighty"])
        #expect(await fixture.delegate("A fourth one.", mode: "plan") == .refusal(.widthCap))
        #expect(await fixture.delegate("A fourth one.", mode: "manual") == .refusal(.widthCap))
        #expect(try Data(contentsOf: fixture.store.fileURL) == before.0)
        #expect(await fixture.coordinator.file == before.1)
        #expect(try await fixture.git(["for-each-ref", "--format=%(refname)", "refs/heads/mighty"]) == branches)
        #expect(try FileManager.default.contentsOfDirectory(atPath: fixture.root.path) == [third.id])
        #expect(fixture.host.createdPanes.map(\.sessionId) == [third.id])
        #expect(DelegationCoordinator.maximumOpenChildren == 3)
    }

    @Test func aModeWiderThanTheParentsStoredModeIsRefusedAndStoresNoKey() async throws {
        let modes = DelegationCoordinator.startingModes
        #expect(modes == ["plan", "manual", "acceptEdits", "auto", "fullAccess"])
        for (rank, own) in modes.enumerated() {
            for (asked, mode) in modes.enumerated() { #expect(DelegationCoordinator.isNoWider(mode, than: own) == (asked <= rank), "\(mode) under \(own)") }
        }
        #expect(!DelegationCoordinator.isNoWider("plan", than: "onRequest"))
        #expect(!DelegationCoordinator.isNoWider("bypassPermissions", than: "fullAccess"))

        let fixture = try await Fixture.make(parentMode: "acceptEdits")
        defer { fixture.remove() }
        let before = try await fixture.snapshot()
        for mode in ["auto", "fullAccess"] {
            #expect(await fixture.delegate("Ship it.", mode: mode) == .refusal(.widerMode), "\(mode)")
        }
        // Not a starting mode, or no task: an error, and nothing made either.
        for mode in ["bypassPermissions", "default", "", "PLAN"] {
            let answer = await fixture.delegate("Ship it.", mode: mode)
            #expect(answer.refused == nil && answer.child == nil && answer.error?.hasPrefix("mode must be one of") == true, "\(mode)")
        }
        #expect(await fixture.delegate(" \n\t", mode: "plan").error?.hasPrefix("delegate needs a task") == true)
        // No run input may hold a NUL character, and the task goes out in the child's first.
        #expect(await fixture.delegate("Ship it.\0Now.", mode: "plan").error == "A task may not contain a NUL character.")
        // Nor may it be longer than its TASK.md copy may be, since all of it goes out in that run.
        let long = String(repeating: "x", count: DelegationFileStore.maximumCopyBytes + 1)
        #expect(await fixture.delegate(long, mode: "plan").error == "A task may be at most 64 KiB.")
        try await fixture.expectNothingMade(since: before)

        // The parent's stored mode is now auto: the same call in the same run makes a child in auto.
        fixture.setParent(mode: "auto")
        let child = try #require(await fixture.delegate("Ship it.", mode: "auto").child)
        #expect(child.startingMode == "auto")
        #expect(await fixture.waitFor(child.id, .running))
        #expect(try fixture.store.load().children.map(\.startingMode) == ["auto"])
        #expect(fixture.host.createdPanes.map(\.mode) == ["auto"])
    }

    @Test func aFolderThatCannotHostAChildWorktreeIsRefusedBeforeAnythingIsMade() async throws {
        let plain = try await Fixture.make(commits: nil)
        defer { plain.remove() }
        let unborn = try await Fixture.make(commits: 0)
        defer { unborn.remove() }
        let detached = try await Fixture.make(commits: 2)
        defer { detached.remove() }
        try await detached.git(["checkout", "-q", "--detach", "HEAD~1"])
        let full = try await Fixture.make(freeBytes: ChildWorktreeMaker.minimumFreeBytes - 1)
        defer { full.remove() }

        for (fixture, reason) in [(plain, DelegationReasonCode.notGit), (unborn, .unbornBranch), (detached, .detachedHead), (full, .lowDisk)] {
            let before = try await fixture.snapshot()
            #expect(await fixture.delegate("Do the work.", mode: "plan") == .refusal(reason), "\(reason)")
            try await fixture.expectNothingMade(since: before, git: reason != .notGit)
        }
    }

    @Test func noRoomForTheChildAndItsReportEvenAfterPruningIsStoreFull() async throws {
        let full = try await Fixture.make(file: try Self.nearlyFullFile(closedFirst: false))
        defer { full.remove() }
        let before = try await full.snapshot()
        #expect(await full.delegate("One more child.", mode: "plan") == .refusal(.storeFull))
        try await full.expectNothingMade(since: before)

        // The same file with a closed child's report copy first: pruning that copy makes the room.
        let prunable = try await Fixture.make(file: try Self.nearlyFullFile(closedFirst: true))
        defer { prunable.remove() }
        let child = try #require(await prunable.delegate("One more child.", mode: "plan").child)
        let saved = try prunable.store.load()
        #expect(saved.children.contains { $0.id == child.id })
        #expect(saved.copy(childId: "filler-0", kind: .report) == nil)
        #expect(saved.copies.filter { $0.kind == .report }.count == (try Self.nearlyFullFile(closedFirst: true)).copies.count - 1)
        #expect(try Data(contentsOf: prunable.store.fileURL).count <= DelegationFileStore.maximumFileBytes)
        #expect(await prunable.waitFor(child.id, .running))
    }

    @Test func aTaskAtTheMostTheToolTakesStillFitsTheChildsFirstRunInput() throws {
        let id = UUID().uuidString.lowercased()
        let path = "/tmp/" + String(repeating: "deep/", count: 40) + "worktrees/" + id
        let worktree = ChildWorktree(sessionId: id, worktreePath: path, workingFolder: path, branch: ChildRecord.branchName(for: id), parentBranch: "main",
                                     baseCommit: String(repeating: "a", count: 40))
        let task = String(repeating: "x", count: DelegationFileStore.maximumCopyBytes)
        let request = StartRunRequest(sessionId: id, workspaceId: "ws-1", kind: SessionKind.claude, input: DelegationCoordinator.firstInput(task: task, worktree: worktree), provider: "claude")
        #expect(throws: Never.self) { try CoreValidation.validate(request) }
    }

    @Test func aStartThatFailsLeavesTheChildFailedWithOneFailedToStartNoticeAndKeepsWhatItMade() async throws {
        let fixture = try await Fixture.make()
        defer { fixture.remove() }
        // The host could not make the pane.
        fixture.host.fail(pane: true)
        let noPane = try #require(await fixture.delegate("Try it.", mode: "plan").child)
        #expect(noPane.state == .creating)
        #expect(await fixture.waitFor(noPane.id, .failed))
        #expect(fixture.host.startedRuns.isEmpty)
        // The pane was made but its run did not start.
        fixture.host.fail(run: true)
        let noRun = try #require(await fixture.delegate("Try again.", mode: "plan").child)
        #expect(await fixture.waitFor(noRun.id, .failed))
        #expect(fixture.host.startedRuns.map(\.sessionId) == [noRun.id])

        let saved = try fixture.store.load()
        #expect(saved.children.map(\.state) == [.failed, .failed])
        #expect(saved.notices.map(\.childId) == [noPane.id, noRun.id])
        #expect(saved.notices.allSatisfy { $0.kind == .failedToStart && $0.reportRevision == 0 && $0.lane == .pending && $0.receipt == nil })
        // What the failed starts made stays until a human discards them.
        for child in saved.children { #expect(FileManager.default.fileExists(atPath: ChildWorktree.taskFile(worktreePath: child.worktreePath))) }
        let branches = try await fixture.git(["for-each-ref", "--format=%(refname)", "refs/heads/mighty"])
        #expect(Set(branches.split(separator: "\n").map(String.init)) == Set([noPane, noRun].map { "refs/heads/" + $0.branch }))
        // The same call again is that failed child, with no second start or notice.
        #expect(await fixture.delegate("Try it.", mode: "plan").child?.id == noPane.id)
        #expect(await fixture.delegate("Try it.", mode: "plan").child?.state == .failed)
        #expect(fixture.host.createdPanes.map(\.sessionId) == [noPane.id, noRun.id])
        #expect(try fixture.store.load().notices.count == 2)

        // Its worktree cannot be made (a file stands where the root goes): failed before any pane is asked for.
        let blocked = try await Fixture.make()
        defer { blocked.remove() }
        try Data("not a folder".utf8).write(to: blocked.root)
        let noWorktree = try #require(await blocked.delegate("Try it.", mode: "plan").child)
        #expect(await blocked.waitFor(noWorktree.id, .failed))
        #expect(try blocked.store.load().notices.map(\.kind) == [.failedToStart])
        #expect(blocked.host.createdPanes.isEmpty)
    }

    /// Open children of other parents, each with a full report copy, then one
    /// smaller copy, leaving about 2 KB under the cap. With `closedFirst` the
    /// first child is closed, so its report copy is the first thing pruned.
    private static func nearlyFullFile(closedFirst: Bool) throws -> DelegationFile {
        let cap = DelegationFileStore.maximumFileBytes
        let fullCopy = Data(repeating: UInt8(ascii: "r"), count: DelegationFileStore.maximumCopyBytes)
        var file = DelegationFile()
        for index in 0... {
            var next = file
            let child = record("filler-\(index)", parent: "other-\(index)", state: closedFirst && index == 0 ? .closed : .running)
            next.children.append(child)
            next.setCopy(DelegationCopy(childId: child.id, kind: .report, revision: 1, contents: fullCopy))
            guard try DelegationFileStore.encode(next).count <= cap else { break }
            file = next
        }
        let room = cap - (try DelegationFileStore.encode(file).count)
        file.children.append(record("pad", parent: "other-pad", state: .running))
        file.setCopy(DelegationCopy(childId: "pad", kind: .report, revision: 1, contents: Data(repeating: UInt8(ascii: "p"), count: max(0, room - 3_000))))
        let size = try DelegationFileStore.encode(file).count
        #expect(size <= cap && cap - size < 4_000)
        return file
    }
}
