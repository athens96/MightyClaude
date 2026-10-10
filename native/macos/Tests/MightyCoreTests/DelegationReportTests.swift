import CryptoKit
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

/// The app side as report revisions see it: the panes a test sets up, every
/// child pane delegate asks for, and its first run, named "first-<id>". A
/// test may hook the moment that run starts, before startRun returns, and
/// may hold every pane-state answer until a gate opens.
private final class ReportHost: DelegationHost, @unchecked Sendable {
    private let lock = NSLock()
    private var panes: [String: DelegationPaneState] = [:]
    private var hook: (@Sendable (String, String) async -> Void)?
    private var gate: Gate?

    func set(_ pane: DelegationPaneState) { lock.withLock { panes[pane.sessionId] = pane } }
    /// Called with the child's id and its first run's id while startRun is going.
    func onFirstRun(_ hook: @escaping @Sendable (String, String) async -> Void) { lock.withLock { self.hook = hook } }
    /// Pane-state answers wait for `gate` to open.
    func hold(until gate: Gate) { lock.withLock { self.gate = gate } }

    func createPane(_ pane: DelegationChildPane) async -> Bool {
        set(DelegationPaneState(sessionId: pane.sessionId, permissionMode: pane.mode, folder: pane.folder, parentSessionId: pane.parentSessionId))
        return true
    }

    func startRun(sessionId: String, input: String) async -> String? {
        let runId = "first-\(sessionId)"
        if let hook = lock.withLock({ hook }) { await hook(sessionId, runId) }
        return runId
    }

    func deliver(_ input: String, to sessionId: String, route: DeliveryRoute) async -> String? { nil }
    func paneState(sessionId: String) async -> DelegationPaneState? {
        if let gate = lock.withLock({ gate }) { await gate.wait() }
        return lock.withLock { panes[sessionId] }
    }
    func stopRun(sessionId: String) async {}
}

/// A temp folder holding the workspace repository, the worktree root and the
/// profile with the delegation file, and a coordinator over them. The pane
/// "parent" works in the repository.
private struct Fixture {
    let base: URL
    let host: ReportHost
    let store: DelegationFileStore
    let coordinator: DelegationCoordinator
    var repo: URL { base.appendingPathComponent("repo", isDirectory: true) }

    static func make(file: DelegationFile = DelegationFile()) async throws -> Fixture {
        let base = FileManager.default.temporaryDirectory.appendingPathComponent("mighty-report-\(UUID().uuidString)", isDirectory: true)
        let store = DelegationFileStore(directory: base.appendingPathComponent("profile", isDirectory: true))
        try store.save(file)
        let host = ReportHost()
        let maker = ChildWorktreeMaker(root: base.appendingPathComponent("worktrees", isDirectory: true), freeBytes: { _ in 50_000_000_000 })
        let fixture = Fixture(base: base, host: host, store: store, coordinator: try DelegationCoordinator(store: store, host: host, worktrees: maker, isSwitchOn: { true }))
        try FileManager.default.createDirectory(at: fixture.repo, withIntermediateDirectories: true)
        try await fixture.git(["init", "-q", "-b", "main"])
        try Data("start\n".utf8).write(to: fixture.repo.appendingPathComponent("file.txt"))
        try await fixture.git(["add", "-A"]); try await fixture.git(["commit", "-q", "-m", "start"])
        host.set(DelegationPaneState(sessionId: "parent", permissionMode: "auto", folder: fixture.repo.path, runId: "parent-run", activity: .running))
        return fixture
    }

    func remove() { try? FileManager.default.removeItem(at: base) }

    func call(_ tool: String, _ arguments: [String: String]) async -> DelegationResponse {
        let location = PaneMCPServerLocation(socketPath: base.appendingPathComponent("io.sock").path, executable: URL(fileURLWithPath: "/usr/bin/true"))
        let binding = PaneMCPBinding(agentPaneId: "parent", token: "token-parent", server: location, workspaceId: "ws-1", workspacePath: repo.path, provider: "claude", delegation: true)
        return await coordinator.handle(DelegationRequest(tool: tool, arguments: arguments), binding: binding)
    }

    /// delegate from "parent", once the child's first run has started.
    func delegateRunningChild() async throws -> ChildRecord {
        let id = try #require(await call("delegate", ["task": "Fix the parser.", "mode": "acceptEdits"]).child?.id)
        #expect(await waitUntil { await child(id).state != .creating })
        return await child(id)
    }

    func child(_ id: String) async -> ChildRecord { await coordinator.file.children.first { $0.id == id }! }
    func notices(_ id: String) async -> [Notice] { await coordinator.file.notices.filter { $0.childId == id } }

    /// The child writes `text` to its REPORT.md.
    func writeReport(_ child: ChildRecord, _ text: String) throws {
        try Data(text.utf8).write(to: URL(fileURLWithPath: ChildWorktree.reportFile(worktreePath: child.worktreePath)))
    }

    /// The child commits a change on its branch; its new head.
    func commitWork(_ child: ChildRecord, _ name: String) async throws -> String {
        try Data("\(name)\n".utf8).write(to: URL(fileURLWithPath: child.worktreePath).appendingPathComponent("\(name).txt"))
        try await git(["add", "-A"], in: child.worktreePath); try await git(["commit", "-q", "-m", name], in: child.worktreePath)
        return try await git(["rev-parse", "HEAD"], in: child.worktreePath)
    }

    /// The delegation file on disk is what the coordinator holds.
    func expectSaved() async throws {
        let held = await coordinator.file
        #expect(try store.load() == held)
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

private func sha256(_ text: String) -> String { SHA256.hash(data: Data(text.utf8)).map { String(format: "%02x", $0) }.joined() }

/// A child record that only needs to exist: no worktree behind it.
private func record(_ id: String, state: ChildState, runId: String? = nil) -> ChildRecord {
    var child = ChildRecord(id: id, parentSessionId: "parent", worktreePath: "/nonexistent/worktrees/\(id)", parentBranch: "main", baseCommit: String(repeating: "a", count: 40),
                            startingMode: "plan", requestKey: "key-\(id)", state: state)
    child.runId = runId
    return child
}

/// Report revisions with a fake host and real temp repositories: what each
/// run that ends in a child's pane leaves for its parent.
@Suite(.serialized, .delegationLane) struct DelegationReportTests {
    @Test func aRunEndingWithAChangedReportRecordsTheNextRevisionAtTheChildHeadAndSendsOneReportedNotice() async throws {
        let fixture = try await Fixture.make()
        defer { fixture.remove() }
        let started = try await fixture.delegateRunningChild()
        let id = started.id, run = "first-\(id)"
        #expect(started.state == .running && started.runId == run && started.awaitedRunId == run)
        #expect(started.reportRevision == 0 && started.reportHead == nil && started.reportDigest == nil)

        let head = try await fixture.commitWork(started, "parser")
        let text = "Fixed the parser.\nLeft: the docs.\n"
        try fixture.writeReport(started, text)
        let notice = try #require(await fixture.coordinator.childRunEnded(id, runId: run, end: .finished))
        #expect(notice.childId == id && notice.kind == .reported && notice.reportRevision == 1 && notice.lane == .pending && notice.receipt == nil)

        let reported = await fixture.child(id)
        #expect(reported.state == .reported && reported.reportRevision == 1 && reported.reportHead == head)
        #expect(reported.reportDigest == sha256(text))
        #expect(await fixture.notices(id) == [notice])
        let copy = try #require(await fixture.coordinator.file.copy(childId: id, kind: .report))
        #expect(copy.revision == 1 && copy.text == text && !copy.truncated)
        try await fixture.expectSaved()

        // The same end reported again, or a start of that ended run, changes nothing.
        let bytes = try Data(contentsOf: fixture.store.fileURL)
        #expect(await fixture.coordinator.childRunEnded(id, runId: run, end: .finished) == nil)
        await fixture.coordinator.childRunStarted(id, runId: run)
        #expect(try Data(contentsOf: fixture.store.fileURL) == bytes)
        #expect(await fixture.child(id) == reported)

        // The parent reads the revision, its head and its body through child_status.
        let status = try #require(await fixture.call("child_status", ["child": id]).status)
        #expect(status.state == .reported && status.revision == 1 && status.head == head && status.report == text)
    }

    @Test func anyNewRunClearsReportedAndOnlyAChangedReportMakesTheNextRevision() async throws {
        let fixture = try await Fixture.make()
        defer { fixture.remove() }
        let started = try await fixture.delegateRunningChild()
        let id = started.id
        let firstHead = try await fixture.commitWork(started, "one")
        try fixture.writeReport(started, "First report.\n")
        #expect(await fixture.coordinator.childRunEnded(id, runId: "first-\(id)", end: .finished)?.kind == .reported)

        // A human's run in the child's pane clears reported, so a merge waits.
        await fixture.coordinator.childRunStarted(id, runId: "human-2")
        let running = await fixture.child(id)
        #expect(running.state == .running && running.runId == "human-2" && running.reportRevision == 1 && running.reportHead == firstHead)
        #expect(await fixture.call("merge", ["child": id, "expected_head": firstHead]).refused == .notReported)
        try await fixture.expectSaved()

        // It ends with the report unchanged: ended, and no notice for a human's run.
        #expect(await fixture.coordinator.childRunEnded(id, runId: "human-2", end: .finished) == nil)
        #expect(await fixture.child(id).state == .ended)
        #expect(await fixture.notices(id).count == 1)

        // A changed report makes revision 2 at the new head, with its one notice;
        // a report over 64 KiB is kept as the marked copy.
        await fixture.coordinator.childRunStarted(id, runId: "human-3")
        let secondHead = try await fixture.commitWork(started, "two")
        let long = String(repeating: "a line of the second report\n", count: 4_000)
        try fixture.writeReport(started, long)
        let second = try #require(await fixture.coordinator.childRunEnded(id, runId: "human-3", end: .stopped))
        #expect(second.kind == .reported && second.reportRevision == 2)
        let reported = await fixture.child(id)
        #expect(reported.state == .reported && reported.reportRevision == 2 && reported.reportHead == secondHead && reported.reportDigest == sha256(long))
        #expect(await fixture.notices(id).map(\.reportRevision) == [1, 2])
        let copy = try #require(await fixture.coordinator.file.copy(childId: id, kind: .report))
        #expect(copy.revision == 2 && copy.truncated && copy.text.hasSuffix(DelegationCopy.truncationMarker) && copy.originalBytes == long.utf8.count)
        #expect(await fixture.call("merge", ["child": id, "expected_head": secondHead]).merged?.mergedCommit == secondHead)

        // The same report written again is no new revision, even after a merge.
        await fixture.coordinator.childRunStarted(id, runId: "human-4")
        #expect(await fixture.child(id).state == .running)
        try fixture.writeReport(started, long)
        #expect(await fixture.coordinator.childRunEnded(id, runId: "human-4", end: .errored) == nil)
        let unchanged = await fixture.child(id)
        #expect(unchanged.state == .ended && unchanged.reportRevision == 2)

        // A REPORT.md that is a link is not followed, and one with no text is no report.
        await fixture.coordinator.childRunStarted(id, runId: "human-5")
        let outside = fixture.base.appendingPathComponent("outside.md")
        try Data("Not the child's report.\n".utf8).write(to: outside)
        let reportPath = ChildWorktree.reportFile(worktreePath: started.worktreePath)
        try FileManager.default.removeItem(atPath: reportPath)
        try FileManager.default.createSymbolicLink(atPath: reportPath, withDestinationPath: outside.path)
        #expect(await fixture.coordinator.childRunEnded(id, runId: "human-5", end: .finished) == nil)
        try FileManager.default.removeItem(atPath: reportPath)
        await fixture.coordinator.childRunStarted(id, runId: "human-6")
        try fixture.writeReport(started, " \n\t\n")
        #expect(await fixture.coordinator.childRunEnded(id, runId: "human-6", end: .finished) == nil)
        #expect(await fixture.child(id).reportRevision == 2)
        #expect(await fixture.notices(id).count == 2)
        try await fixture.expectSaved()
    }

    @Test func aDelegateOrFollowUpRunEndingWithoutANewReportSendsOneEndedWithoutReportNotice() async throws {
        let fixture = try await Fixture.make()
        defer { fixture.remove() }
        let started = try await fixture.delegateRunningChild()
        let id = started.id

        // Delegate's first run ends with REPORT.md still empty.
        let first = try #require(await fixture.coordinator.childRunEnded(id, runId: "first-\(id)", end: .stopped))
        #expect(first.kind == .endedWithoutReport && first.reportRevision == 0 && first.lane == .pending)
        #expect(await fixture.child(id).state == .ended)
        #expect(await fixture.coordinator.childRunEnded(id, runId: "first-\(id)", end: .finished) == nil)
        #expect(await fixture.notices(id) == [first])
        try await fixture.expectSaved()

        // A follow-up's run: the follow-up is recorded, its delivery names the
        // run it went to, and that run ends with no new report.
        #expect(await fixture.call("follow_up", ["child": id, "text": "Also update the docs."]).followUp != nil)
        await fixture.coordinator.childRunStarted(id, runId: "follow-2")
        #expect(await fixture.coordinator.awaitRun("follow-2", of: id) == nil)
        #expect(await fixture.child(id).awaitedRunId == "follow-2")
        let second = try #require(await fixture.coordinator.childRunEnded(id, runId: "follow-2", end: .errored))
        #expect(second.kind == .endedWithoutReport && second.reportRevision == 0)
        #expect(await fixture.coordinator.childRunEnded(id, runId: "follow-2", end: .errored) == nil)
        #expect(await fixture.coordinator.awaitRun("follow-2", of: id) == nil)
        #expect(await fixture.notices(id) == [first, second])

        // When the run's end came before its delivery named it, the notice goes out then, once.
        await fixture.coordinator.childRunStarted(id, runId: "follow-3")
        #expect(await fixture.coordinator.childRunEnded(id, runId: "follow-3", end: .finished) == nil)
        let late = try #require(await fixture.coordinator.awaitRun("follow-3", of: id))
        #expect(late.kind == .endedWithoutReport)
        #expect(await fixture.coordinator.awaitRun("follow-3", of: id) == nil)
        #expect(await fixture.notices(id).map(\.kind) == [.endedWithoutReport, .endedWithoutReport, .endedWithoutReport])

        // A follow-up's run that ends with a report sends the reported notice only.
        await fixture.coordinator.childRunStarted(id, runId: "follow-4")
        #expect(await fixture.coordinator.awaitRun("follow-4", of: id) == nil)
        try fixture.writeReport(started, "Docs updated.\n")
        #expect(await fixture.coordinator.childRunEnded(id, runId: "follow-4", end: .finished)?.kind == .reported)
        #expect(await fixture.coordinator.awaitRun("follow-4", of: id) == nil)
        #expect(await fixture.notices(id).map(\.kind) == [.endedWithoutReport, .endedWithoutReport, .endedWithoutReport, .reported])

        // A human's run that ends with no new report sends nothing.
        await fixture.coordinator.childRunStarted(id, runId: "human-5")
        #expect(await fixture.coordinator.childRunEnded(id, runId: "human-5", end: .finished) == nil)
        #expect(await fixture.notices(id).count == 4)
        try await fixture.expectSaved()
    }

    @Test func theFirstRunsEndMayComeBeforeStartRunReturnsAndStillSendsOneNotice() async throws {
        let fixture = try await Fixture.make()
        defer { fixture.remove() }
        let coordinator = fixture.coordinator
        fixture.host.onFirstRun { id, run in
            await coordinator.childRunStarted(id, runId: run)
            await coordinator.childRunEnded(id, runId: run, end: .errored)
        }
        let id = try #require(await fixture.call("delegate", ["task": "Fix the parser.", "mode": "plan"]).child?.id)
        #expect(await waitUntil { await coordinator.starting.isEmpty })
        let child = await fixture.child(id)
        #expect(child.state == .ended && child.runId == "first-\(id)" && child.awaitedRunId == "first-\(id)")
        #expect(await fixture.notices(id).map(\.kind) == [.endedWithoutReport])
        try await fixture.expectSaved()
    }

    @Test func runEventsTakeTurnsSoAnEndHalfRecordedIsNeverOvertaken() async throws {
        let fixture = try await Fixture.make()
        defer { fixture.remove() }
        let coordinator = fixture.coordinator
        let id = try await fixture.delegateRunningChild().id
        let gate = Gate()
        fixture.host.hold(until: gate)

        // Delegate's first run ends; its end waits halfway, before it is saved,
        // while the next run's start and its parent's await come in.
        let end = Task { await coordinator.childRunEnded(id, runId: "first-\(id)", end: .finished) }
        #expect(await waitUntil { await coordinator.isRecordingRun })
        let start = Task { await coordinator.childRunStarted(id, runId: "follow-2") }
        #expect(await waitUntil { await coordinator.runTurns.count == 1 })
        let awaited = Task { await coordinator.awaitRun("follow-2", of: id) }
        #expect(await waitUntil { await coordinator.runTurns.count == 2 })
        #expect(await fixture.child(id).runId == "first-\(id)")
        await gate.open()

        // Each is recorded whole, in the order it came: the first run's notice is not lost.
        let first = try #require(await end.value)
        #expect(first.kind == .endedWithoutReport && first.reportRevision == 0)
        await start.value
        #expect(await awaited.value == nil)
        let child = await fixture.child(id)
        #expect(child.state == .running && child.runId == "follow-2" && child.awaitedRunId == "follow-2")
        #expect(await coordinator.isRecordingRun == false)
        #expect(await coordinator.runTurns.isEmpty)
        #expect(await coordinator.childRunEnded(id, runId: "follow-2", end: .finished)?.kind == .endedWithoutReport)
        #expect(await fixture.notices(id).map(\.kind) == [.endedWithoutReport, .endedWithoutReport])
        try await fixture.expectSaved()
    }

    @Test func aRunKilledByQuittingTheAppSendsNoNoticeAndShowsInterrupted() async throws {
        let fixture = try await Fixture.make()
        defer { fixture.remove() }
        let started = try await fixture.delegateRunningChild()
        let id = started.id

        // Killed by quitting with a report written: interrupted, no notice, no revision.
        try fixture.writeReport(started, "Half done.\n")
        #expect(await fixture.coordinator.childRunEnded(id, runId: "first-\(id)", end: .quit) == nil)
        let interrupted = await fixture.child(id)
        #expect(interrupted.state == .interrupted && interrupted.reportRevision == 0 && interrupted.reportHead == nil)
        #expect(await fixture.notices(id).isEmpty)
        #expect(await fixture.call("list_children", [:]).children?.first?.state == .interrupted)
        try await fixture.expectSaved()

        // The report it left is the next run's to record.
        await fixture.coordinator.childRunStarted(id, runId: "human-2")
        #expect(await fixture.coordinator.childRunEnded(id, runId: "human-2", end: .finished)?.reportRevision == 1)
        #expect(await fixture.child(id).state == .reported)

        // At the next launch, the children whose runs the quit killed come back interrupted, with no notice.
        let file = DelegationFile(children: [record("running", state: .running, runId: "r"), record("waiting", state: .waiting, runId: "w"),
                                             record("reported", state: .reported), record("closed", state: .closed, runId: "c")])
        let relaunched = try await Fixture.make(file: file)
        defer { relaunched.remove() }
        #expect(await relaunched.coordinator.interruptRunsKilledByQuit() == ["running", "waiting"])
        #expect(await relaunched.coordinator.file.children.map(\.state) == [.interrupted, .interrupted, .reported, .closed])
        #expect(await relaunched.coordinator.file.notices.isEmpty)
        try await relaunched.expectSaved()
        // Their killed runs' ends, should the host still report them, and runs of a closed child change nothing.
        let bytes = try Data(contentsOf: relaunched.store.fileURL)
        #expect(await relaunched.coordinator.childRunEnded("running", runId: "r", end: .finished) == nil)
        await relaunched.coordinator.childRunStarted("closed", runId: "c2")
        #expect(await relaunched.coordinator.childRunEnded("closed", runId: "c2", end: .finished) == nil)
        #expect(await relaunched.coordinator.interruptRunsKilledByQuit() == [])
        #expect(try Data(contentsOf: relaunched.store.fileURL) == bytes)
    }
}
