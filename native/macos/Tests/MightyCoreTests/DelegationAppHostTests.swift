import Foundation
import Testing
@testable import MightyCore

/// The hidden switch, which a test turns off partway.
private final class Switch: @unchecked Sendable {
    private let lock = NSLock()
    private var on = true
    var isOn: Bool { lock.withLock { on } }
    func turnOff() { lock.withLock { on = false } }
}

/// A value shared with the closures a runner or pump calls.
private final class Shared<Value>: @unchecked Sendable {
    private let lock = NSLock()
    private var value: Value
    init(_ value: Value) { self.value = value }
    func with<Result>(_ body: (inout Value) -> Result) -> Result { lock.withLock { body(&value) } }
}

/// The app side as these tests need it: the parent pane, and each child pane
/// delegate makes, with its first run named "first-<id>".
private final class AppHost: DelegationHost, @unchecked Sendable {
    private let lock = NSLock()
    private var panes: [String: DelegationPaneState] = [:]
    private(set) var made: [DelegationChildPane] = []

    func set(_ pane: DelegationPaneState) { lock.withLock { panes[pane.sessionId] = pane } }
    var children: [DelegationChildPane] { lock.withLock { made } }

    func createPane(_ pane: DelegationChildPane) async -> Bool {
        lock.withLock { made.append(pane) }
        set(DelegationPaneState(sessionId: pane.sessionId, permissionMode: pane.mode, folder: pane.folder, parentSessionId: pane.parentSessionId))
        return true
    }
    func startRun(sessionId: String, input: String) async -> String? { "first-\(sessionId)" }
    func deliver(_ input: String, to sessionId: String, route: DeliveryRoute) async -> String? { nil }
    func paneState(sessionId: String) async -> DelegationPaneState? { lock.withLock { panes[sessionId] } }
    func stopRun(sessionId: String) async {}
}

/// Fixture git, kept away from the user's and the system's settings.
@discardableResult
private func git(_ arguments: [String], in folder: URL) async throws -> String {
    let executable = try #require(DelegationGit.executable)
    var environment = ProcessInfo.processInfo.environment.filter { !$0.key.hasPrefix("GIT_") }
    environment["GIT_CONFIG_GLOBAL"] = "/dev/null"; environment["GIT_CONFIG_NOSYSTEM"] = "1"; environment["GIT_OPTIONAL_LOCKS"] = "0"
    for key in ["GIT_AUTHOR", "GIT_COMMITTER"] { environment[key + "_NAME"] = "Fixture"; environment[key + "_EMAIL"] = "fixture@example.invalid" }
    let result = try await ProcessCapture.run(executable: executable, arguments: ["-C", folder.path] + arguments, environment: environment, cwd: folder, timeout: 120)
    #expect(result.exitCode == 0, "git \(arguments.joined(separator: " ")): \(String(decoding: result.stderr, as: UTF8.self))")
    return DelegationGit.line(result.stdout)
}

private func waitUntil(timeout: TimeInterval = 60, _ condition: () async -> Bool) async -> Bool {
    let deadline = Date().addingTimeInterval(timeout)
    while Date() < deadline {
        if await condition() { return true }
        try? await Task.sleep(nanoseconds: 10_000_000)
    }
    return await condition()
}

/// What the app's DelegationHost (AppStore) does with a child's pane, checked
/// without the app: the rules it applies, its run ids and the order its run
/// events reach the coordinator in.
@Suite(.serialized) struct DelegationAppHostTests {
    static let pane = DelegationChildPane(sessionId: "child-1", parentSessionId: "parent-1", mode: "plan", folder: "/tmp/mighty-worktrees/child-1")

    @Test func aChildPaneIsAClaudePaneInTheRequestedModeNeverTheLastUsedPanes() throws {
        // The last-used Claude pane, which a new pane would copy, is in full access.
        var used = RunSession(id: "used", workspaceId: "ws", title: "Claude", model: "claude-opus-5", settings: RunSettings(effort: "high", permissionMode: "fullAccess"))
        used.agentViewMode = "mighty"
        let template = try #require(RunSession.template(kind: SessionKind.claude, provider: "claude", in: [used]))
        #expect(template.settings.permissionMode == "fullAccess")

        let child = DelegationPanes.childSession(Self.pane, workspaceId: "ws", title: "Write the notes")
        #expect(child.id == "child-1" && child.workspaceId == "ws" && child.title == "Write the notes")
        #expect(DelegationSwitch.isClaudePane(kind: child.kind, provider: child.provider))
        // Exactly the mode asked for; nothing comes from the last-used pane.
        #expect(child.settings == RunSettings(permissionMode: "plan"))
        #expect(child.model == "default" && child.agentViewMode == nil && child.resumeId == nil && child.logs.isEmpty)
        #expect(child.parentSessionId == "parent-1" && child.workingFolder == "/tmp/mighty-worktrees/child-1")
        for mode in DelegationCoordinator.startingModes {
            var asked = Self.pane; asked.mode = mode
            #expect(DelegationPanes.childSession(asked, workspaceId: "ws", title: "t").settings.permissionMode == mode)
        }
    }

    @Test func theParentLinkAndWorktreeFolderAreSavedAndLoadedWithThePane() async throws {
        let root = try shortTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: root) }
        let repo = root.appendingPathComponent("repo", isDirectory: true)
        try FileManager.default.createDirectory(at: repo, withIntermediateDirectories: true)
        let profile = root.appendingPathComponent("profile", isDirectory: true)
        let repository = StateRepository(directory: profile, legacyStateURL: nil)
        let workspace = try await repository.approveWorkspace(Workspace(id: "ws", name: "repo", path: repo.path))
        let parent = RunSession(id: "parent-1", workspaceId: workspace.id, title: "Parent", settings: RunSettings(permissionMode: "acceptEdits"))
        let child = DelegationPanes.childSession(Self.pane, workspaceId: workspace.id, title: "Write the notes")
        try await repository.save(AppSnapshot(workspaces: [workspace], sessions: [parent, child], activeWorkspaceId: workspace.id))

        let loaded = try await StateRepository(directory: profile, legacyStateURL: nil).load()
        let restored = try #require(loaded.sessions.first { $0.id == "child-1" })
        #expect(restored.parentSessionId == "parent-1" && restored.workingFolder == Self.pane.folder)
        #expect(restored.settings.permissionMode == "plan")
        #expect(loaded.sessions.first { $0.id == "parent-1" }?.parentSessionId == nil)
    }

    @Test func aChildRunsInItsWorktreeAndRefusesWithWorktreeMissingWithoutIt() throws {
        let root = try shortTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: root) }
        let worktree = root.appendingPathComponent("worktrees/child-1", isDirectory: true)
        try FileManager.default.createDirectory(at: worktree, withIntermediateDirectories: true)
        let workspace = Workspace(id: "ws", name: "repo", path: root.appendingPathComponent("repo").path)
        var pane = Self.pane; pane.folder = worktree.path
        let child = DelegationPanes.childSession(pane, workspaceId: "ws", title: "t")
        let parent = RunSession(id: "parent-1", workspaceId: "ws", title: "Parent")

        let run = DelegationPanes.runWorkspace(for: child, in: workspace)
        #expect(run.id == "ws" && run.name == "repo" && run.path == worktree.path)
        #expect(DelegationPanes.runWorkspace(for: parent, in: workspace) == workspace)
        #expect(DelegationPanes.runRefusal(child) == nil && DelegationPanes.runRefusal(parent) == nil)
        let state = DelegationPanes.paneState(of: child, in: workspace, runId: "run-1", activity: .finished)
        #expect(state == DelegationPaneState(sessionId: "child-1", permissionMode: "plan", folder: worktree.path, parentSessionId: "parent-1", runId: "run-1", activity: .finished))
        #expect(DelegationPanes.paneState(of: parent, in: workspace, runId: nil, activity: .idle).folder == workspace.path)

        // Its worktree gone, it never falls back to the parent's checkout.
        try FileManager.default.removeItem(at: worktree)
        #expect(DelegationPanes.runRefusal(child) == .worktreeMissing)
        var unsaved = child; unsaved.workingFolder = nil
        #expect(DelegationPanes.runRefusal(unsaved) == .worktreeMissing)
        try Data("x".utf8).write(to: worktree.deletingLastPathComponent().appendingPathComponent("child-1"))
        #expect(DelegationPanes.runRefusal(child) == .worktreeMissing)
    }

    @Test func aChildsRunAndItsMCPBindingWorkInItsWorktreeThroughTheRealRunner() async throws {
        let root = try shortTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: root) }
        let fake = try fakeClaudeExecutable(in: root)
        let repo = root.appendingPathComponent("repo", isDirectory: true), worktree = root.appendingPathComponent("worktrees/child-1", isDirectory: true)
        for folder in [repo, worktree] { try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true) }
        let config = root.appendingPathComponent("claude-config", isDirectory: true), script = root.appendingPathComponent("script.json")
        let session = UUID().uuidString.lowercased()
        try JSONSerialization.data(withJSONObject: ["sessionId": session, "turns": [[["say": "Working in my worktree."]]]]).write(to: script)
        let plugin = root.appendingPathComponent("plugin", isDirectory: true)
        try FileManager.default.createDirectory(at: plugin.appendingPathComponent(".claude-plugin"), withIntermediateDirectories: true)
        try Data("{\"name\":\"mighty\"}".utf8).write(to: plugin.appendingPathComponent(".claude-plugin/plugin.json"))
        let bindings = PaneMCPBindingRegistry()
        let service = ProviderService(binaryOverrides: ["claude": fake], environment: [
            "PATH": "/usr/bin:/bin:/usr/sbin:/sbin", "HOME": root.path, "CLAUDE_CONFIG_DIR": config.path,
            "FAKE_CLAUDE_SCRIPT": script.path, "FAKE_CLAUDE_LOG": root.appendingPathComponent("fake.jsonl").path,
        ])
        let statuses = Shared<[String]>([])
        let runner = ProcessRunner(providerService: service, pluginDirectory: plugin, paneMCPServer: PaneMCPServerLocation(socketPath: root.appendingPathComponent("io.sock").path, executable: fake),
                                   paneMCPBindings: bindings, delegationEnabled: { false }, onEvent: { event in
                                       if event.type == "status", let status = event.status { statuses.with { $0.append(status) } }
                                   })
        var pane = Self.pane; pane.folder = worktree.path
        let child = DelegationPanes.childSession(pane, workspaceId: "ws", title: "t")
        let workspace = Workspace(id: "ws", name: "repo", path: repo.path)
        try await runner.start(request: StartRunRequest(sessionId: child.id, workspaceId: "ws", kind: SessionKind.claude, input: "Write the notes.", provider: "claude", settings: child.settings),
                               workspace: DelegationPanes.runWorkspace(for: child, in: workspace), allowPermissionPrompts: true)
        #expect(await waitUntil { statuses.with { $0.last.map { $0 != "running" } ?? false } })
        #expect(statuses.with { $0 } == ["running", "completed"])

        // The binding the terminal tool's pane is made from names the worktree.
        let binding = try #require(bindings.binding(forPane: child.id))
        #expect(binding.workspacePath == worktree.path && binding.workspaceId == "ws")
        // With the switch off the child still runs, only without the delegation server.
        #expect(!binding.delegation)
        // Claude worked in the worktree, never in the parent's checkout.
        let record = config.appendingPathComponent("projects/\(SessionHistory.claudeProjectFolder(worktree.path))/\(session).jsonl")
        // Every message line names its folder; the CLI's bookkeeping lines name none.
        let messages = jsonLines(record).filter { $0["uuid"] != nil }
        #expect(!messages.isEmpty && messages.allSatisfy { $0["cwd"] as? String == worktree.path })
        #expect(!FileManager.default.fileExists(atPath: config.appendingPathComponent("projects/\(SessionHistory.claudeProjectFolder(repo.path))").path))
        await runner.shutdown()
    }

    @Test func runIdsAndNormalFinishesSinceLaunchAreTrackedPerPane() {
        var ledger = DelegationRunLedger()
        #expect(ledger.runId("p") == nil && ledger.activity("p", running: false) == .idle)
        #expect(ledger.end("p", status: "completed", quitting: false) == nil)

        let first = ledger.begin("p")
        #expect(ledger.runId("p") == first && ledger.activity("p", running: true) == .running)
        let finished = ledger.end("p", status: "completed", quitting: false)
        #expect(finished?.runId == first && finished?.end == .finished)
        #expect(ledger.activity("p", running: false) == .finished && ledger.runId("p") == first)
        // A second end of the same run is no end.
        #expect(ledger.end("p", status: "error", quitting: false) == nil && ledger.activity("p", running: false) == .finished)

        let second = ledger.begin("p")
        #expect(second != first && ledger.activity("p", running: false) == .idle)
        #expect(ledger.end("p", status: "stopped", quitting: false)?.end == .stopped && ledger.activity("p", running: false) == .idle)
        _ = ledger.begin("p")
        #expect(ledger.end("p", status: "error", quitting: false)?.end == .errored)
        _ = ledger.begin("p")
        #expect(ledger.end("p", status: "stopped", quitting: true)?.end == .quit && ledger.activity("p", running: false) == .idle)
        _ = ledger.begin("p")
        ledger.forget("p")
        #expect(ledger.runId("p") == nil && ledger.end("p", status: "completed", quitting: false) == nil)
    }

    @Test func runEventsReachTheirHandlerOneAtATimeInTheOrderSent() async {
        let seen = Shared<[DelegationRunEvent]>([]), busy = Shared(false), overlapped = Shared(false)
        let pump = DelegationRunEventPump { event in
            busy.with { if $0 { overlapped.with { $0 = true } }; $0 = true }
            try? await Task.sleep(nanoseconds: UInt64.random(in: 0 ... 2_000_000))
            seen.with { $0.append(event) }; busy.with { $0 = false }
        }
        let sent = (0 ..< 40).flatMap { [DelegationRunEvent.started(childId: "c\($0 % 3)", runId: "r\($0)"), .ended(childId: "c\($0 % 3)", runId: "r\($0)", end: .finished)] }
        for event in sent { pump.send(event) }
        await pump.finish()
        #expect(seen.with { $0 } == sent)
        #expect(!overlapped.with { $0 })
        // After finishing, nothing more is handed on.
        pump.send(.started(childId: "c0", runId: "late"))
        #expect(seen.with { $0.count } == sent.count)
    }

    @Test func turningTheSwitchOffLeavesAnExistingChildWorkingAndOnlyDetachesTheServer() async throws {
        let base = try shortTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: base) }
        let repo = base.appendingPathComponent("repo", isDirectory: true)
        try FileManager.default.createDirectory(at: repo, withIntermediateDirectories: true)
        try await git(["init", "-q", "-b", "main"], in: repo)
        try Data("start\n".utf8).write(to: repo.appendingPathComponent("file.txt"))
        try await git(["add", "-A"], in: repo); try await git(["commit", "-q", "-m", "start"], in: repo)
        let store = DelegationFileStore(directory: base.appendingPathComponent("profile", isDirectory: true))
        let host = AppHost(), power = Switch()
        host.set(DelegationPaneState(sessionId: "parent-1", permissionMode: "acceptEdits", folder: repo.path, runId: "parent-run", activity: .running))
        let maker = ChildWorktreeMaker(root: base.appendingPathComponent("worktrees", isDirectory: true), freeBytes: { _ in 50_000_000_000 })
        let coordinator = try DelegationCoordinator(store: store, host: host, worktrees: maker, isSwitchOn: { power.isOn })
        let location = PaneMCPServerLocation(socketPath: base.appendingPathComponent("io.sock").path, executable: URL(fileURLWithPath: "/usr/bin/true"))
        let binding = PaneMCPBinding(agentPaneId: "parent-1", token: "token-parent", server: location, workspaceId: "ws", workspacePath: repo.path, provider: "claude", delegation: true)

        let id = try #require(await coordinator.handle(DelegationRequest(tool: "delegate", arguments: ["task": "Write the notes.", "mode": "plan"]), binding: binding).child?.id)
        #expect(await waitUntil { await coordinator.file.children.first { $0.id == id }?.state == .running })
        let made = try #require(host.children.first)
        #expect(made.sessionId == id && made.parentSessionId == "parent-1" && made.mode == "plan")
        let child = DelegationPanes.childSession(made, workspaceId: "ws", title: "Write the notes.")
        let worktree = try #require(await coordinator.file.children.first { $0.id == id }?.worktreePath)

        power.turnOff()
        // The server is detached: a call reaches nothing and changes nothing.
        let before = try Data(contentsOf: store.fileURL)
        #expect(await coordinator.handle(DelegationRequest(tool: "list_children"), binding: binding).error == DelegationIOHandler.detachedMessage)
        #expect(try Data(contentsOf: store.fileURL) == before)

        // The child still runs in its worktree, and its run's end, through
        // the app's ordered events, still records its report for the parent.
        #expect(DelegationPanes.runRefusal(child) == nil && DelegationPanes.runWorkspace(for: child, in: Workspace(id: "ws", name: "repo", path: repo.path)).path == made.folder)
        #expect(made.folder.hasPrefix(worktree))
        try Data("done\n".utf8).write(to: URL(fileURLWithPath: worktree).appendingPathComponent("notes.txt"))
        try await git(["add", "-A"], in: URL(fileURLWithPath: worktree)); try await git(["commit", "-q", "-m", "notes"], in: URL(fileURLWithPath: worktree))
        try Data("Wrote the notes.\n".utf8).write(to: URL(fileURLWithPath: ChildWorktree.reportFile(worktreePath: worktree)))
        let pump = DelegationRunEventPump(coordinator: coordinator)
        pump.send(.started(childId: id, runId: "first-\(id)"))
        pump.send(.ended(childId: id, runId: "first-\(id)", end: .finished))
        await pump.finish()
        let reported = try #require(await coordinator.file.children.first { $0.id == id })
        #expect(reported.state == .reported && reported.reportRevision == 1)
        #expect(await coordinator.file.notices.filter { $0.childId == id }.map(\.kind) == [.reported])
        #expect(try store.load() == (await coordinator.file))
    }
}
