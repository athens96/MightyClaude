import Foundation
import Testing
@testable import MightyCore

/// One pane of one launch: its state as the coordinator reads it, its own
/// runner with its own fake Claude script and log, and the runs the host started.
private struct S3Pane {
    var state: DelegationPaneState
    let runner: ProcessRunner
    let script: URL
    let log: URL
    var resumeId: String?
    /// The runs the host started here, oldest first, what each got and how it ended.
    var runs: [String] = []
    var inputs: [String: String] = [:]
    var ends: [String: String] = [:]
    /// Every permission card event of the pane, in order.
    var cards: [ToolPermissionRequest] = []
}

private struct S3Arrival {
    var pane: String
    var runId: String?
    var event: RunEvent
}

/// No terminal calls are made here.
private struct S3NoTerminalCalls: AgentIORequestHandler {
    func handle(_ request: AgentIORequest, binding: PaneMCPBinding) async -> AgentIOResponse { .failure("not under test") }
}

/// The app side for one launch of the scenario: each pane runs the scripted
/// fake Claude through the real ProcessRunner, one runner per pane, all bound
/// in one registry behind one socket server whose delegation handler is the
/// real coordinator. Run events reach the coordinator one at a time, in the
/// order the runners sent them. Both launches share one folder, so the
/// profile, the Claude config with the transcripts, and each pane's fake
/// Claude log carry over from the first launch to the second.
private final class S3Host: DelegationHost, @unchecked Sendable {
    static let workspaceId = "ws-s3"
    let folder: URL
    let home: URL
    let fake: URL
    let switchSuite: String
    let bindings = PaneMCPBindingRegistry()
    var config: URL { folder.appendingPathComponent("claude-config", isDirectory: true) }
    var plugin: URL { folder.appendingPathComponent("plugin", isDirectory: true) }
    var socketPath: String { folder.appendingPathComponent("io.sock").path }
    private let lock = NSLock()
    private var panes: [String: S3Pane] = [:]
    /// A child's script, picked by a phrase of the input its run starts with.
    private var childScripts: [(phrase: String, steps: [[String: Any]])] = []
    private var coordinator: DelegationCoordinator?
    private let arrivals: AsyncStream<S3Arrival>
    private let arrive: AsyncStream<S3Arrival>.Continuation
    private var pump: Task<Void, Never>?

    init(folder: URL, home: URL, fake: URL, switchSuite: String) throws {
        self.folder = folder; self.home = home; self.fake = fake; self.switchSuite = switchSuite
        (arrivals, arrive) = AsyncStream<S3Arrival>.makeStream()
        try FileManager.default.createDirectory(at: plugin.appendingPathComponent(".claude-plugin"), withIntermediateDirectories: true)
        try Data("{\"name\":\"mighty\"}".utf8).write(to: plugin.appendingPathComponent(".claude-plugin/plugin.json"))
    }

    /// Starts handing the panes' run events to `coordinator`, in order.
    func attach(_ coordinator: DelegationCoordinator) {
        lock.withLock { self.coordinator = coordinator }
        let arrivals = arrivals
        pump = Task { [weak self] in
            for await arrival in arrivals { await self?.handle(arrival) }
        }
    }

    func shutDown() async {
        let runners = lock.withLock { panes.values.map(\.runner) }
        for runner in runners { await runner.shutdown() }
        arrive.finish()
        await pump?.value
    }

    // MARK: Panes and scripts

    @discardableResult func addPane(_ sessionId: String, mode: String, folder paneFolder: String, parent: String? = nil, resumeId: String? = nil) -> Bool {
        let script = folder.appendingPathComponent("script-\(sessionId).json"), log = folder.appendingPathComponent("log-\(sessionId).jsonl")
        let pane = S3Pane(state: DelegationPaneState(sessionId: sessionId, permissionMode: mode, folder: paneFolder, parentSessionId: parent),
                          runner: makeRunner(sessionId, script: script, log: log), script: script, log: log, resumeId: resumeId)
        lock.withLock { panes[sessionId] = pane }
        return true
    }

    /// What the pane's next run plays: one turn of `steps`.
    func script(_ sessionId: String, _ steps: [[String: Any]]) throws {
        guard let url = lock.withLock({ panes[sessionId]?.script }) else { throw MightyError("No pane \(sessionId).") }
        try JSONSerialization.data(withJSONObject: ["turns": [steps]]).write(to: url)
    }

    /// What a child plays in a run whose input contains `phrase`.
    func childScript(forInputContaining phrase: String, _ steps: [[String: Any]]) { lock.withLock { childScripts.append((phrase, steps)) } }

    func pane(_ sessionId: String) -> S3Pane? { lock.withLock { panes[sessionId] } }
    func runs(_ sessionId: String) -> [String] { pane(sessionId)?.runs ?? [] }
    /// Every run the host started in any pane.
    var allRuns: [String] { lock.withLock { panes.values.flatMap(\.runs) } }
    func input(of runId: String, in sessionId: String) -> String? { pane(sessionId)?.inputs[runId] }
    /// What every run the host started in the pane began with, oldest first.
    func inputs(_ sessionId: String) -> [String] { pane(sessionId).map { pane in pane.runs.compactMap { pane.inputs[$0] } } ?? [] }
    func end(of runId: String, in sessionId: String) -> String? { pane(sessionId)?.ends[runId] }

    /// The card for `tool` still open in the pane, other than `excluding`.
    func openCard(_ sessionId: String, tool: String, excluding: Set<String> = []) -> ToolPermissionRequest? {
        let cards = pane(sessionId)?.cards ?? []
        return cards.first { card in
            card.toolName == tool && card.state == "pending" && !excluding.contains(card.id) && !cards.contains { $0.id == card.id && $0.state != "pending" }
        }
    }

    /// The human's answer to `card`, from the pane's own approval card.
    func answer(_ sessionId: String, _ card: ToolPermissionRequest, allow: Bool) async throws {
        guard let runner = pane(sessionId)?.runner else { throw MightyError("No pane \(sessionId).") }
        try await runner.respondToPermission(sessionId: sessionId, runId: card.runId, requestId: card.id, allow: allow)
    }

    private func makeRunner(_ sessionId: String, script: URL, log: URL) -> ProcessRunner {
        var environment = [
            "PATH": "/usr/bin:/bin:/usr/sbin:/sbin", "HOME": home.path, "CLAUDE_CONFIG_DIR": config.path,
            "FAKE_CLAUDE_SCRIPT": script.path, "FAKE_CLAUDE_LOG": log.path,
            "GIT_CONFIG_GLOBAL": "/dev/null", "GIT_CONFIG_NOSYSTEM": "1",
            "GIT_AUTHOR_NAME": "Fake Claude", "GIT_AUTHOR_EMAIL": "fake-claude@example.invalid",
            "GIT_COMMITTER_NAME": "Fake Claude", "GIT_COMMITTER_EMAIL": "fake-claude@example.invalid",
        ]
        if let developer = ProcessInfo.processInfo.environment["DEVELOPER_DIR"] { environment["DEVELOPER_DIR"] = developer }
        let service = ProviderService(binaryOverrides: ["claude": fake], environment: environment)
        let suite = switchSuite
        return ProcessRunner(providerService: service, pluginDirectory: plugin, paneMCPServer: PaneMCPServerLocation(socketPath: socketPath, executable: fake),
                             paneMCPBindings: bindings, delegationEnabled: { DelegationSwitch.isOn(UserDefaults(suiteName: suite) ?? .standard) },
                             onEvent: { [weak self] event in self?.received(event, pane: sessionId) })
    }

    // MARK: DelegationHost

    func createPane(_ pane: DelegationChildPane) async -> Bool {
        addPane(pane.sessionId, mode: pane.mode, folder: pane.folder, parent: pane.parentSessionId)
    }

    func startRun(sessionId: String, input: String) async -> String? {
        guard let pane = pane(sessionId) else { return nil }
        if pane.state.parentSessionId != nil {
            let steps = lock.withLock { childScripts.first { input.contains($0.phrase) }?.steps } ?? []
            try? JSONSerialization.data(withJSONObject: ["turns": [steps]]).write(to: pane.script)
        }
        let runId = "run-" + UUID().uuidString.lowercased()
        lock.withLock {
            panes[sessionId]?.state.runId = runId; panes[sessionId]?.state.activity = .running
            panes[sessionId]?.runs.append(runId); panes[sessionId]?.inputs[runId] = input
        }
        let request = StartRunRequest(sessionId: sessionId, workspaceId: Self.workspaceId, kind: SessionKind.claude, input: input, provider: "claude",
                                      settings: RunSettings(permissionMode: pane.state.permissionMode), resumeId: pane.resumeId)
        do {
            try await pane.runner.start(request: request, workspace: Workspace(id: Self.workspaceId, name: "repo", path: pane.state.folder), allowPermissionPrompts: true)
            return runId
        } catch {
            lock.withLock { panes[sessionId]?.state.activity = .idle; panes[sessionId]?.ends[runId] = "not started" }
            return nil
        }
    }

    func deliver(_ input: String, to sessionId: String, route: DeliveryRoute) async -> String? {
        switch route {
        case .queue:
            return await startRun(sessionId: sessionId, input: input)
        case .steer:
            guard let pane = pane(sessionId), pane.state.activity == .running else { return nil }
            return await pane.runner.steer(sessionId: sessionId, text: input) ? pane.state.runId : nil
        }
    }

    func paneState(sessionId: String) async -> DelegationPaneState? { pane(sessionId)?.state }

    func stopRun(sessionId: String) async {
        guard let runner = pane(sessionId)?.runner else { return }
        await runner.stop(id: sessionId)
    }

    // MARK: Run events

    private func received(_ event: RunEvent, pane sessionId: String) {
        let runId = lock.withLock { panes[sessionId]?.state.runId }
        arrive.yield(S3Arrival(pane: sessionId, runId: runId, event: event))
    }

    private func handle(_ arrival: S3Arrival) async {
        let event = arrival.event, id = arrival.pane
        let (coordinator, isChild) = lock.withLock { (self.coordinator, panes[id]?.state.parentSessionId != nil) }
        switch event.type {
        case "resume":
            lock.withLock { panes[id]?.resumeId = event.resumeId }
        case "permission":
            if let card = event.permission { lock.withLock { panes[id]?.cards.append(card) } }
        case "status":
            guard let runId = arrival.runId, let status = event.status else { return }
            if status == "running" {
                if isChild { await coordinator?.childRunStarted(id, runId: runId) }
                return
            }
            lock.withLock {
                panes[id]?.ends[runId] = status
                if panes[id]?.state.runId == runId { panes[id]?.state.activity = status == "completed" ? .finished : .idle }
            }
            if isChild { await coordinator?.childRunEnded(id, runId: runId, end: status == "completed" ? .finished : status == "stopped" ? .stopped : .errored) }
        default:
            break
        }
    }
}

/// One launch of the app: its host, the profile's delegation file, a
/// coordinator built from it, and the socket server the runs reach it by.
private struct S3Launch {
    let host: S3Host
    let store: DelegationFileStore
    let coordinator: DelegationCoordinator
    let server: AgentIOSocketServer

    /// Restores the panes with `restore`, then builds the coordinator from
    /// the profile in `folder`, as a launch of the app does.
    static func start(folder: URL, home: URL, fake: URL, switchSuite: String, worktrees: URL, restore: (S3Host) -> Void) throws -> S3Launch {
        let host = try S3Host(folder: folder, home: home, fake: fake, switchSuite: switchSuite)
        restore(host)
        let store = DelegationFileStore(directory: folder.appendingPathComponent("profile", isDirectory: true))
        let maker = ChildWorktreeMaker(root: worktrees, freeBytes: { _ in 50_000_000_000 })
        let coordinator = try DelegationCoordinator(store: store, host: host, worktrees: maker, isSwitchOn: { DelegationSwitch.isOn(UserDefaults(suiteName: switchSuite) ?? .standard) })
        host.attach(coordinator)
        let server = AgentIOSocketServer(socketPath: host.socketPath, bindings: host.bindings, handler: S3NoTerminalCalls(), delegation: coordinator)
        try server.start()
        return S3Launch(host: host, store: store, coordinator: coordinator, server: server)
    }

    /// Quits: every runner ends, so the transcripts on disk are final.
    func quit() async {
        await host.shutDown()
        server.stop()
    }

    /// Waits for the pane's run `runId` to end; how it ended.
    func ended(_ runId: String, in pane: String) async throws -> String {
        try await s3Awaited("the end of run \(runId) in \(pane)") { host.end(of: runId, in: pane) }
    }

    /// The human's send with nothing held: the text starts the next run.
    func humanSend(_ text: String, in pane: String) async throws -> String {
        #expect(await coordinator.send(text, in: pane) == .nothingHeld)
        return try #require(await host.startRun(sessionId: pane, input: text))
    }

    /// The one Claude-format transcript of the session run in `folder`.
    func transcript(of folder: String) throws -> String {
        let projects = host.config.appendingPathComponent("projects/\(SessionHistory.claudeProjectFolder(folder))", isDirectory: true)
        let transcripts = try FileManager.default.contentsOfDirectory(at: projects, includingPropertiesForKeys: nil).filter { $0.pathExtension == "jsonl" }
        #expect(transcripts.count == 1)
        return try String(contentsOf: try #require(transcripts.first), encoding: .utf8)
    }
}

/// Polls `condition` until it holds. The bound only ends a scenario that
/// broke; nothing here asserts how long any step took.
private func s3Until(_ what: String, _ condition: () async -> Bool) async throws {
    let deadline = Date().addingTimeInterval(120)
    while !(await condition()) {
        guard Date() < deadline else { throw MightyError("Scenario S3 never reached: \(what).") }
        try await Task.sleep(nanoseconds: 20_000_000)
    }
}

private func s3Awaited<Value>(_ what: String, _ produce: () async -> Value?) async throws -> Value {
    var found: Value?
    try await s3Until(what) { found = await produce(); return found != nil }
    return try #require(found)
}

/// Fixture git, kept away from the user's and the system's settings.
@discardableResult
private func s3Git(_ arguments: [String], in folder: URL) async throws -> String {
    let executable = try #require(DelegationGit.executable)
    var environment = ProcessInfo.processInfo.environment.filter { !$0.key.hasPrefix("GIT_") }
    environment["GIT_CONFIG_GLOBAL"] = "/dev/null"; environment["GIT_CONFIG_NOSYSTEM"] = "1"; environment["GIT_OPTIONAL_LOCKS"] = "0"
    for key in ["GIT_AUTHOR", "GIT_COMMITTER"] { environment[key + "_NAME"] = "Fixture"; environment[key + "_EMAIL"] = "fixture@example.invalid" }
    let result = try await ProcessCapture.run(executable: executable, arguments: ["-C", folder.path] + arguments, environment: environment, cwd: folder, timeout: 120)
    #expect(result.exitCode == 0, "git \(arguments.joined(separator: " ")): \(String(decoding: result.stderr, as: UTF8.self))")
    return DelegationGit.line(result.stdout)
}

/// How many times `needle` appears in `text`.
private func s3Count(of needle: String, in text: String) -> Int { text.components(separatedBy: needle).count - 1 }

/// The structured content of one tool call the fake Claude logged.
private func s3Structured(_ call: [String: Any]) -> [String: Any] {
    (call["result"] as? [String: Any])?["structuredContent"] as? [String: Any] ?? [:]
}

/// Scenario S3, headless: its own temp repository and profile, the switch on,
/// a fake Claude parent with children A and B made from one base. A is
/// fast-forwarded into main by the merge tool; B's fast-forward is then
/// refused with diverged and changes nothing. A human stopped B after it
/// reported, so the parent's two follow-ups to B are held; the app quits, and
/// a coordinator rebuilt from the same profile hands them to B exactly once,
/// with the human's next send in B. B brings in main and reports revision 2,
/// which the merge tool fast-forwards with its new head as the expected head.
/// A third follow-up is refused with follow_up_limit.
@Suite(.serialized) struct DelegationScenarioS3Tests {
    static let parentMode = "auto"
    static let childMode = "acceptEdits"
    static let taskA = "Feature A: add a.txt saying a."
    static let taskB = "Feature B: add b.txt saying b."
    static let bringInMain = "Bring in main: merge main into your branch, then report again."
    static let keepB = "Keep b.txt as it is, and say so in REPORT.md."

    /// The scenario's repository, profile and fake Claude, shared by both launches.
    private struct World {
        let base: URL
        let repo: URL
        let fake: URL
        let suite: String
        var folder: URL { base.appendingPathComponent("app", isDirectory: true) }
        var worktrees: URL { base.appendingPathComponent("worktrees", isDirectory: true) }

        func launch(_ restore: (S3Host) -> Void) throws -> S3Launch {
            try S3Launch.start(folder: folder, home: base, fake: fake, switchSuite: suite, worktrees: worktrees, restore: restore)
        }

        func git(_ arguments: [String], in folder: String? = nil) async throws -> String {
            try await s3Git(arguments, in: folder.map { URL(fileURLWithPath: $0, isDirectory: true) } ?? repo)
        }

        /// A child's steps: a tracked file and its commit, asked for first.
        func commitSteps(file: String, text: String) throws -> [[String: Any]] {
            let git = try #require(DelegationGit.executable).path
            return [
                ["write": file, "text": text],
                ["bash": "'\(git)' add -- \(file) && '\(git)' commit -q -m 'Add \(file)'", "ask": true],
            ]
        }
    }

    /// What the first launch leaves on disk and in its panes.
    private struct AfterFirst {
        let host: S3Host
        let start: String
        let a: ChildRecord
        let b: ChildRecord
        let headA: String
        let headB1: String
        let noticeRuns: [String]
        let followUps: [FollowUp]
        let resumeIds: [String: String]
        let file: DelegationFile
    }

    @Test func aDivergedChildGetsItsHeldFollowUpOnceAfterARelaunchAndItsSecondRevisionIsFastForwarded() async throws {
        let base = try shortTemporaryDirectory()
        let suite = "mightyclaude.s3." + UUID().uuidString
        UserDefaults(suiteName: suite)?.set(true, forKey: DelegationSwitch.defaultsKey)
        defer {
            UserDefaults().removePersistentDomain(forName: suite)
            try? FileManager.default.removeItem(at: base)
        }
        let fake = try fakeClaudeExecutable(in: base)
        let repo = base.appendingPathComponent("repo", isDirectory: true)
        try FileManager.default.createDirectory(at: repo, withIntermediateDirectories: true)
        try await s3Git(["init", "-q", "-b", "main"], in: repo)
        try Data("# Practice\n".utf8).write(to: repo.appendingPathComponent("README.md"))
        try await s3Git(["add", "-A"], in: repo); try await s3Git(["commit", "-q", "-m", "Start"], in: repo)
        let world = World(base: base, repo: repo, fake: fake, suite: suite)

        let first = try await firstLaunch(world)
        try await secondLaunch(world, after: first)
    }

    // MARK: The first launch

    private func firstLaunch(_ w: World) async throws -> AfterFirst {
        let launch = try w.launch { host in host.addPane("parent", mode: Self.parentMode, folder: w.repo.path) }
        let played: AfterFirst
        do {
            played = try await playFirst(launch, w)
            await launch.quit()
        } catch {
            await launch.quit()
            throw error
        }
        // Quitting changed nothing in the profile.
        #expect(try launch.store.load() == played.file)
        return played
    }

    private func playFirst(_ launch: S3Launch, _ w: World) async throws -> AfterFirst {
        let host = launch.host
        let start = try await w.git(["rev-parse", "HEAD"])
        try host.childScript(forInputContaining: Self.taskA, w.commitSteps(file: "a.txt", text: "a\n") + [
            ["write": "\(ChildWorktree.notesFolder)/REPORT.md", "text": "# Report\n\nAdded a.txt.\n"],
            ["say": "Committed and reported."],
        ])
        try host.childScript(forInputContaining: Self.taskB, w.commitSteps(file: "b.txt", text: "b\n") + [
            ["write": "\(ChildWorktree.notesFolder)/REPORT.md", "text": "# Report\n\nAdded b.txt.\n"],
            // B then asks to run its checks, and a human stops it there.
            ["permission": "Bash", "input": ["command": "make check"]],
            ["say": "Checked."],
        ])

        // 1. The parent delegates A and B, both from main's one head.
        try host.script("parent", [
            ["call": "delegate", "arguments": ["task": Self.taskA, "mode": Self.childMode]],
            ["call": "delegate", "arguments": ["task": Self.taskB, "mode": Self.childMode]],
            ["say": "A and B are on it."],
        ])
        let delegating = try await launch.humanSend("Split features A and B.", in: "parent")
        #expect(try await launch.ended(delegating, in: "parent") == "completed")
        let (a, b) = try await s3Awaited("both children on disk") { () -> (ChildRecord, ChildRecord)? in
            guard let file = try? launch.store.load(), file.children.count == 2,
                  let a = file.children.first(where: { file.copy(childId: $0.id, kind: .task)?.text.contains(Self.taskA) == true }),
                  let b = file.children.first(where: { file.copy(childId: $0.id, kind: .task)?.text.contains(Self.taskB) == true }) else { return nil }
            return (a, b)
        }
        #expect(a.baseCommit == start && b.baseCommit == start)
        #expect(a.parentBranch == "main" && b.parentBranch == "main")

        // 2. A commits and reports while the parent is idle after a normal
        //    finish: its notice is the parent's next run.
        let askA = try await s3Awaited("A's commit request") { host.openCard(a.id, tool: "Bash") }
        try host.script("parent", [["say": "A reported."]])
        try await host.answer(a.id, askA, allow: true)
        let noticeRunA = try await s3Awaited("A's notice starting a parent run") { host.runs("parent").dropFirst(1).first }
        #expect(try await launch.ended(noticeRunA, in: "parent") == "completed")
        let headA = try await s3Awaited("A's reported head") { () -> String? in
            guard let child = try? launch.store.load().children.first(where: { $0.id == a.id }), child.state == .reported, child.reportRevision == 1 else { return nil }
            return child.reportHead
        }

        // 3. B commits and reports too, then waits to run its checks, and a
        //    human stops it there. Its notice is the parent's next run, and
        //    B's pane is idle after a stop.
        let askB = try await s3Awaited("B's commit request") { host.openCard(b.id, tool: "Bash") }
        try host.script("parent", [["say": "B reported."]])
        try await host.answer(b.id, askB, allow: true)
        _ = try await s3Awaited("B's request to run its checks") { host.openCard(b.id, tool: "Bash", excluding: [askB.id]) }
        let runB1 = try #require(host.runs(b.id).first)
        await host.stopRun(sessionId: b.id)
        #expect(try await launch.ended(runB1, in: b.id) == "stopped")
        let noticeRunB = try await s3Awaited("B's notice starting a parent run") { host.runs("parent").dropFirst(2).first }
        #expect(try await launch.ended(noticeRunB, in: "parent") == "completed")
        let headB1 = try await s3Awaited("B's reported head") { () -> String? in
            guard let child = try? launch.store.load().children.first(where: { $0.id == b.id }), child.state == .reported, child.reportRevision == 1 else { return nil }
            return child.reportHead
        }
        #expect(headA != headB1 && headA != start && headB1 != start)

        // 4. The merge tool fast-forwards main to A's head.
        try host.script("parent", [["call": "merge", "arguments": ["child": a.id, "expected_head": headA]], ["say": "Merged A."]])
        let mergeA = try await launch.humanSend("Merge A.", in: "parent")
        #expect(try await launch.ended(mergeA, in: "parent") == "completed")
        #expect(try await w.git(["rev-parse", "refs/heads/main"]) == headA)

        // 5. B's fast-forward is refused with diverged: main moved on from
        //    the base B was made from. Nothing changes.
        let fileBefore = try launch.store.load()
        let refsBefore = try await w.git(["for-each-ref", "--format=%(refname) %(objectname)"])
        let statusBefore = try await w.git(["status", "--porcelain"])
        let checkoutBefore = try await w.git(["rev-parse", "HEAD:"])
        try host.script("parent", [["call": "merge", "arguments": ["child": b.id, "expected_head": headB1]], ["say": "B diverged."]])
        let mergeB = try await launch.humanSend("Merge B.", in: "parent")
        #expect(try await launch.ended(mergeB, in: "parent") == "completed")
        #expect(try launch.store.load() == fileBefore)
        #expect(try await w.git(["for-each-ref", "--format=%(refname) %(objectname)"]) == refsBefore)
        #expect(try await w.git(["status", "--porcelain"]) == statusBefore)
        #expect(try await w.git(["rev-parse", "HEAD:"]) == checkoutBefore)
        #expect(try await w.git(["rev-parse", "HEAD"], in: b.worktreePath) == headB1)
        #expect(try await w.git(["symbolic-ref", "HEAD"], in: b.worktreePath) == "refs/heads/\(b.branch)")
        #expect(!FileManager.default.fileExists(atPath: w.repo.appendingPathComponent(".git/MERGE_HEAD").path))

        // 6. The parent sends B two follow-ups. B's pane is idle after a
        //    stop, so both are held, and B runs nothing.
        try host.script("parent", [
            ["call": "follow_up", "arguments": ["child": b.id, "text": Self.bringInMain]],
            ["call": "follow_up", "arguments": ["child": b.id, "text": Self.keepB]],
            ["say": "B has its follow-ups."],
        ])
        let following = try await launch.humanSend("B diverged: have it bring in main.", in: "parent")
        #expect(try await launch.ended(following, in: "parent") == "completed")
        let file = try launch.store.load()
        #expect(file.followUps.map(\.text) == [Self.bringInMain, Self.keepB])
        #expect(file.followUps.allSatisfy { $0.childId == b.id && $0.lane == .held && $0.receipt == nil })
        #expect(file.children.first { $0.id == b.id }?.followUpCount == 2)
        #expect(file.children.first { $0.id == b.id }?.state == .reported)
        #expect(await launch.coordinator.heldItems(for: b.id).map(\.id) == file.followUps.map(\.id))
        #expect(host.runs(b.id) == [runB1])
        #expect(host.runs("parent") == [delegating, noticeRunA, noticeRunB, mergeA, mergeB, following])

        var resumeIds: [String: String] = [:]
        for id in ["parent", a.id, b.id] { resumeIds[id] = try #require(host.pane(id)?.resumeId) }
        let savedA = try #require(file.children.first { $0.id == a.id })
        let savedB = try #require(file.children.first { $0.id == b.id })
        return AfterFirst(host: host, start: start, a: savedA, b: savedB, headA: headA, headB1: headB1, noticeRuns: [noticeRunA, noticeRunB],
                          followUps: file.followUps, resumeIds: resumeIds, file: file)
    }

    // MARK: The relaunch

    private func secondLaunch(_ w: World, after first: AfterFirst) async throws {
        let launch = try w.launch { host in
            // The relaunched app's panes: none running, each with its session to resume.
            host.addPane("parent", mode: Self.parentMode, folder: w.repo.path, resumeId: first.resumeIds["parent"])
            for child in [first.a, first.b] {
                host.addPane(child.id, mode: child.startingMode, folder: child.worktreePath, parent: "parent", resumeId: first.resumeIds[child.id])
            }
        }
        let played: (runB2: String, headB2: String, mergeRun: String, notice2: Notice)
        do {
            played = try await playSecond(launch, w, after: first)
            await launch.quit()
        } catch {
            await launch.quit()
            throw error
        }
        try await judge(launch, w, first: first, runB2: played.runB2, headB2: played.headB2, mergeRun: played.mergeRun, notice2: played.notice2)
    }

    private func playSecond(_ launch: S3Launch, _ w: World, after first: AfterFirst) async throws -> (runB2: String, headB2: String, mergeRun: String, notice2: Notice) {
        let (host, coordinator, b) = (launch.host, launch.coordinator, first.b)
        let (followUp1, followUp2) = (first.followUps[0], first.followUps[1])

        // The rebuilt coordinator finds no run the quit killed, starts no run
        // by itself, and still holds both follow-ups for B.
        #expect(await coordinator.interruptRunsKilledByQuit().isEmpty)
        await coordinator.deliverPending()
        #expect(host.allRuns.isEmpty)
        #expect(try launch.store.load() == first.file)
        #expect(await coordinator.heldItems(for: b.id).map(\.id) == [followUp1.id, followUp2.id])
        #expect(await coordinator.heldItems(for: "parent").isEmpty)

        // 7. The human's next send in B releases both, oldest first, ahead of
        //    the text, in one run: B brings in main and reports revision 2.
        let git = try #require(DelegationGit.executable).path
        host.childScript(forInputContaining: Self.bringInMain, [
            ["bash": "'\(git)' merge --no-edit -q main"],
            ["write": "\(ChildWorktree.notesFolder)/REPORT.md", "text": "# Report\n\nAdded b.txt and brought in main; b.txt is as it was.\n"],
            ["say": "Brought in main and reported again."],
        ])
        let reportFile = ChildWorktree.reportFile(worktreePath: b.worktreePath)
        let heldTexts = [DelegationCoordinator.text(of: followUp1, reportFile: reportFile), DelegationCoordinator.text(of: followUp2, reportFile: reportFile)]
        let release = await coordinator.send("Go ahead.", in: b.id)
        guard case .released(let runB2, let itemIds) = release else { throw MightyError("B's send released nothing: \(release)") }
        #expect(itemIds == [followUp1.id, followUp2.id])
        #expect(host.input(of: runB2, in: b.id) == (heldTexts + ["Go ahead."]).joined(separator: "\n\n"))
        #expect(try await launch.ended(runB2, in: b.id) == "completed")
        let revised = try await s3Awaited("B's revision 2") { () -> ChildRecord? in
            guard let child = try? launch.store.load().children.first(where: { $0.id == b.id }), child.state == .reported, child.reportRevision == 2 else { return nil }
            return child
        }
        let headB2 = try #require(revised.reportHead)
        #expect(try await w.git(["rev-parse", "HEAD"], in: b.worktreePath) == headB2)
        #expect(try await w.git(["rev-list", "--parents", "-n", "1", headB2]) == "\(headB2) \(first.headB1) \(first.headA)")

        // Each follow-up reached B once: saved delivered by that one run, and
        // nothing more goes to B by a pass, a send or run next.
        let saved = try launch.store.load().followUps
        #expect(saved.map(\.id) == [followUp1.id, followUp2.id])
        #expect(saved.allSatisfy { $0.lane == .delivered && $0.receipt?.route == .queue && $0.receipt?.runId == runB2 })
        await coordinator.deliverPending()
        #expect(await coordinator.send("Anything else?", in: b.id) == .nothingHeld)
        #expect(await coordinator.runNext(in: b.id) == .nothingHeld)
        #expect(host.runs(b.id) == [runB2])
        for followUp in [followUp1, followUp2] {
            #expect((first.host.inputs(b.id) + host.inputs(b.id)).filter { $0.contains(followUp.id) }.count == 1, "\(followUp.id)")
        }

        // 8. The parent has had no run since the relaunch, so B's notice for
        //    revision 2 is held; the human's send in the parent releases it
        //    ahead of the text, and the merge tool fast-forwards main to B's
        //    new head.
        let notice2 = try await s3Awaited("B's revision 2 notice held") {
            try? launch.store.load().notices.first { $0.childId == b.id && $0.reportRevision == 2 && $0.lane == .held }
        }
        #expect(notice2.kind == .reported)
        #expect(host.runs("parent").isEmpty)
        #expect(await coordinator.heldItems(for: "parent").map(\.id) == [notice2.id])
        try host.script("parent", [
            ["call": "child_status", "arguments": ["child": b.id]],
            ["call": "merge", "arguments": ["child": b.id, "expected_head": headB2]],
            ["say": "Merged B."],
        ])
        let text = "B reported again: merge it."
        let parentRelease = await coordinator.send(text, in: "parent")
        guard case .released(let mergeRun, let noticeIds) = parentRelease else { throw MightyError("The parent's send released nothing: \(parentRelease)") }
        #expect(noticeIds == [notice2.id])
        #expect(host.input(of: mergeRun, in: "parent") == [DelegationCoordinator.text(of: notice2), text].joined(separator: "\n\n"))
        #expect(try await launch.ended(mergeRun, in: "parent") == "completed")
        #expect(try await w.git(["rev-parse", "refs/heads/main"]) == headB2)

        // 9. A third follow-up to B is refused with follow_up_limit and changes nothing.
        let fileBefore = try launch.store.load()
        try host.script("parent", [["call": "follow_up", "arguments": ["child": b.id, "text": "Also add a changelog line."]], ["say": "B has had its two."]])
        let third = try await launch.humanSend("Ask B for one more change.", in: "parent")
        #expect(try await launch.ended(third, in: "parent") == "completed")
        #expect(try launch.store.load() == fileBefore)
        #expect(host.runs(b.id) == [runB2])
        #expect(host.runs("parent") == [mergeRun, third])
        return (runB2, headB2, mergeRun, notice2)
    }

    // MARK: The verdict

    /// Scenario S3's rules over the profile's delegation file, the
    /// repository, the fake Claude logs and the Claude-format transcripts.
    private func judge(_ launch: S3Launch, _ w: World, first: AfterFirst, runB2: String, headB2: String, mergeRun: String, notice2: Notice) async throws {
        let (a, b, start, headA, headB1) = (first.a, first.b, first.start, first.headA, first.headB1)
        let file = try DelegationFileStore(directory: w.folder.appendingPathComponent("profile", isDirectory: true)).load()

        // Both children came from one base; A merged at revision 1, B at
        // revision 2 with its two follow-ups.
        #expect(Set(file.children.map(\.id)) == [a.id, b.id])
        let childA = try #require(file.children.first { $0.id == a.id }), childB = try #require(file.children.first { $0.id == b.id })
        #expect(file.children.allSatisfy { $0.parentSessionId == "parent" && $0.parentBranch == "main" && $0.baseCommit == start && $0.startingMode == Self.childMode })
        #expect(childA.state == .merged && childA.reportRevision == 1 && childA.reportHead == headA && childA.followUpCount == 0)
        #expect(childB.state == .merged && childB.reportRevision == 2 && childB.reportHead == headB2 && childB.followUpCount == 2)
        #expect(file.copy(childId: b.id, kind: .report)?.text.contains("brought in main") == true)

        // Two tool fast-forwards: main from the base to A's head, then from
        // A's head to B's revision 2 head.
        #expect(file.merges.count == 2)
        let mergeA = try #require(file.merges.first { $0.childId == a.id }), mergeB = try #require(file.merges.first { $0.childId == b.id })
        #expect(mergeA.kind == .toolFastForward && mergeA.parentBranch == "main" && mergeA.preMergeCommit == start && mergeA.mergedCommit == headA && mergeA.childHead == headA)
        #expect(mergeB.kind == .toolFastForward && mergeB.parentBranch == "main" && mergeB.preMergeCommit == headA && mergeB.mergedCommit == headB2 && mergeB.childHead == headB2)

        // Three reported notices, each delivered once by the queue: A's and
        // B's revision 1 as runs of the idle parent, B's revision 2 with the
        // human's send after the relaunch.
        #expect(file.notices.count == 3 && Set(file.notices.map(\.id)).count == 3)
        #expect(file.notices.allSatisfy { $0.kind == .reported && $0.lane == .delivered && $0.receipt?.route == .queue })
        #expect(file.notices.first { $0.childId == a.id }?.receipt?.runId == first.noticeRuns[0])
        #expect(file.notices.first { $0.childId == b.id && $0.reportRevision == 1 }?.receipt?.runId == first.noticeRuns[1])
        #expect(file.notices.first { $0.id == notice2.id }?.receipt?.runId == mergeRun)

        // Two follow-ups, both delivered once, by B's one run after the relaunch.
        #expect(file.followUps.map(\.id) == first.followUps.map(\.id))
        #expect(file.followUps.allSatisfy { $0.childId == b.id && $0.lane == .delivered && $0.receipt?.route == .queue && $0.receipt?.runId == runB2 })

        // The repository: main at B's revision 2 head, reached by fast-forwards
        // only; B's own commit brought main in. Both branches stay.
        #expect(try await w.git(["symbolic-ref", "--short", "HEAD"]) == "main")
        #expect(try await w.git(["rev-parse", "refs/heads/main"]) == headB2)
        #expect(try await w.git(["status", "--porcelain", "--untracked-files=no"]).isEmpty)
        #expect(try await w.git(["rev-list", "--count", "main"]) == "4")
        #expect(try await w.git(["rev-list", "--parents", "-n", "1", headA]) == "\(headA) \(start)")
        #expect(try await w.git(["rev-list", "--parents", "-n", "1", headB1]) == "\(headB1) \(start)")
        #expect(FileManager.default.fileExists(atPath: w.repo.appendingPathComponent("a.txt").path))
        #expect(FileManager.default.fileExists(atPath: w.repo.appendingPathComponent("b.txt").path))
        #expect(try await w.git(["rev-parse", "refs/heads/\(a.branch)"]) == headA)
        #expect(try await w.git(["rev-parse", "refs/heads/\(b.branch)"]) == headB2)

        // The parent's calls, across both launches: merge A, B refused with
        // diverged, merge B's revision 2; two follow-ups held, the third
        // refused with follow_up_limit.
        let parentLog = jsonLines(try #require(launch.host.pane("parent")).log)
        let calls = parentLog.filter { $0["event"] as? String == "call" }
        let merges = calls.filter { $0["tool"] as? String == "merge" }.map(s3Structured)
        try #require(merges.count == 3)
        #expect((merges[0]["merged"] as? [String: Any])?["mergedCommit"] as? String == headA)
        #expect(merges[1]["refused"] as? String == DelegationReasonCode.diverged.rawValue && merges[1]["merged"] == nil)
        #expect((merges[2]["merged"] as? [String: Any])?["mergedCommit"] as? String == headB2)
        #expect((merges[2]["merged"] as? [String: Any])?["preMergeCommit"] as? String == headA)
        let followUps = calls.filter { $0["tool"] as? String == "follow_up" }.map(s3Structured)
        try #require(followUps.count == 3)
        let answered = followUps.prefix(2).map { $0["followUp"] as? [String: Any] ?? [:] }
        #expect(answered.map { $0["id"] as? String } == first.followUps.map(\.id))
        #expect(answered.map { $0["lane"] as? String } == [DeliveryLane.held.rawValue, DeliveryLane.held.rawValue])
        #expect(answered.map { $0["remaining"] as? Int } == [1, 0])
        #expect(followUps[2]["refused"] as? String == DelegationReasonCode.followUpLimit.rawValue && followUps[2]["followUp"] == nil)

        // B ran twice, in its stored mode: the delegate's run, stopped by a
        // human, then the run the two follow-ups started after the relaunch.
        let logB = jsonLines(try #require(launch.host.pane(b.id)).log)
        let argvB = logB.filter { $0["event"] as? String == "argv" }.compactMap { $0["arguments"] as? [String] }
        #expect(argvB.count == 2 && argvB.allSatisfy { optionValue("--permission-mode", $0) == Self.childMode })
        #expect(logB.filter { $0["event"] as? String == "bash" }.map { $0["code"] as? Int } == [0, 0])

        // The Claude-format transcripts, one session per pane across both
        // launches: each notice in the parent's once, each follow-up in B's once.
        let parentTranscript = try launch.transcript(of: w.repo.path)
        for notice in file.notices { #expect(s3Count(of: notice.id, in: parentTranscript) == 1, "\(notice.id)") }
        let transcriptB = try launch.transcript(of: b.worktreePath)
        for followUp in file.followUps { #expect(s3Count(of: followUp.id, in: transcriptB) == 1, "\(followUp.id)") }
    }
}
