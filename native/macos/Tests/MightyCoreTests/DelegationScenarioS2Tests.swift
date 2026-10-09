import Darwin
import Foundation
import Testing
@testable import MightyCore

/// The FakeClaude fixture built beside this test bundle, copied into `folder`
/// as `claude` and signed ad hoc, so an arm64 build is not killed at launch.
private func scenarioS2FakeClaude(in folder: URL) throws -> URL {
    var info = Dl_info()
    guard dladdr(#dsohandle, &info) != 0, let image = info.dli_fname else { throw MightyError("The test bundle's path is unknown.") }
    var directory = URL(fileURLWithPath: String(cString: image)).deletingLastPathComponent()
    var built: URL?
    for _ in 0 ..< 6 where built == nil {
        let candidate = directory.appendingPathComponent("FakeClaude")
        if FileManager.default.isExecutableFile(atPath: candidate.path) { built = candidate }
        directory = directory.deletingLastPathComponent()
    }
    guard let built else { throw MightyError("FakeClaude was not built beside the tests.") }
    let copy = folder.appendingPathComponent("claude")
    try FileManager.default.copyItem(at: built, to: copy)
    let sign = Process()
    sign.executableURL = URL(fileURLWithPath: "/usr/bin/codesign")
    sign.arguments = ["--force", "--sign", "-", copy.path]
    sign.standardOutput = FileHandle.nullDevice; sign.standardError = FileHandle.nullDevice
    try sign.run(); sign.waitUntilExit()
    guard sign.terminationStatus == 0 else { throw MightyError("FakeClaude could not be signed.") }
    return copy
}

/// One pane of one launch: its state as the coordinator reads it, its own
/// runner with its own fake Claude script, and the runs the host started.
private struct S2Pane {
    var state: DelegationPaneState
    let runner: ProcessRunner
    let script: URL
    var resumeId: String?
    /// The runs the host started here, oldest first, what each got and how it ended.
    var runs: [String] = []
    var inputs: [String: String] = [:]
    var ends: [String: String] = [:]
    /// Every permission card event of the pane, in order.
    var cards: [ToolPermissionRequest] = []
}

private struct S2Arrival {
    var pane: String
    var runId: String?
    var event: RunEvent
}

/// No terminal calls are made here.
private struct S2NoTerminalCalls: AgentIORequestHandler {
    func handle(_ request: AgentIORequest, binding: PaneMCPBinding) async -> AgentIOResponse { .failure("not under test") }
}

/// The app side for one launch of the scenario: each pane runs the scripted
/// fake Claude through the real ProcessRunner, one runner per pane, all bound
/// in one registry behind one socket server whose delegation handler is the
/// real coordinator. Run events reach the coordinator one at a time, in the
/// order the runners sent them. A launch keeps the fake's scripts and its
/// Claude config, where the transcripts are, in its own folder.
private final class S2Host: DelegationHost, @unchecked Sendable {
    static let workspaceId = "ws-s2"
    let folder: URL
    let home: URL
    let fake: URL
    let switchSuite: String
    let bindings = PaneMCPBindingRegistry()
    var config: URL { folder.appendingPathComponent("claude-config", isDirectory: true) }
    var plugin: URL { folder.appendingPathComponent("plugin", isDirectory: true) }
    var socketPath: String { folder.appendingPathComponent("io.sock").path }
    private let lock = NSLock()
    private var panes: [String: S2Pane] = [:]
    /// A child's script, picked by a phrase of the task its first run carries.
    private var childScripts: [(phrase: String, steps: [[String: Any]])] = []
    private var coordinator: DelegationCoordinator?
    private let arrivals: AsyncStream<S2Arrival>
    private let arrive: AsyncStream<S2Arrival>.Continuation
    private var pump: Task<Void, Never>?

    init(folder: URL, home: URL, fake: URL, switchSuite: String) throws {
        self.folder = folder; self.home = home; self.fake = fake; self.switchSuite = switchSuite
        (arrivals, arrive) = AsyncStream<S2Arrival>.makeStream()
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
        let pane = S2Pane(state: DelegationPaneState(sessionId: sessionId, permissionMode: mode, folder: paneFolder, parentSessionId: parent),
                          runner: makeRunner(sessionId, script: script, log: log), script: script, resumeId: resumeId)
        lock.withLock { panes[sessionId] = pane }
        return true
    }

    /// What the pane's next run plays: one turn of `steps`.
    func script(_ sessionId: String, _ steps: [[String: Any]]) throws {
        guard let url = lock.withLock({ panes[sessionId]?.script }) else { throw MightyError("No pane \(sessionId).") }
        try JSONSerialization.data(withJSONObject: ["turns": [steps]]).write(to: url)
    }

    /// What a child whose task contains `phrase` plays in its first run.
    func childScript(forTaskContaining phrase: String, _ steps: [[String: Any]]) { lock.withLock { childScripts.append((phrase, steps)) } }

    func pane(_ sessionId: String) -> S2Pane? { lock.withLock { panes[sessionId] } }
    func runs(_ sessionId: String) -> [String] { pane(sessionId)?.runs ?? [] }
    /// Every run the host started in any pane.
    var allRuns: [String] { lock.withLock { panes.values.flatMap(\.runs) } }
    func input(of runId: String, in sessionId: String) -> String? { pane(sessionId)?.inputs[runId] }
    func end(of runId: String, in sessionId: String) -> String? { pane(sessionId)?.ends[runId] }

    /// The card for `tool` still open in the pane.
    func openCard(_ sessionId: String, tool: String) -> ToolPermissionRequest? {
        let cards = pane(sessionId)?.cards ?? []
        return cards.first { card in card.toolName == tool && card.state == "pending" && !cards.contains { $0.id == card.id && $0.state != "pending" } }
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
        arrive.yield(S2Arrival(pane: sessionId, runId: runId, event: event))
    }

    private func handle(_ arrival: S2Arrival) async {
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
private struct S2Launch {
    let host: S2Host
    let store: DelegationFileStore
    let coordinator: DelegationCoordinator
    let server: AgentIOSocketServer

    /// Restores the panes with `restore`, then builds the coordinator from
    /// the profile in `folder`, as a launch of the app does.
    static func start(folder: URL, home: URL, fake: URL, switchSuite: String, worktrees: URL, restore: (S2Host) -> Void) throws -> S2Launch {
        let host = try S2Host(folder: folder, home: home, fake: fake, switchSuite: switchSuite)
        restore(host)
        let store = DelegationFileStore(directory: folder.appendingPathComponent("profile", isDirectory: true))
        let maker = ChildWorktreeMaker(root: worktrees, freeBytes: { _ in 50_000_000_000 })
        let coordinator = try DelegationCoordinator(store: store, host: host, worktrees: maker, isSwitchOn: { DelegationSwitch.isOn(UserDefaults(suiteName: switchSuite) ?? .standard) })
        host.attach(coordinator)
        let server = AgentIOSocketServer(socketPath: host.socketPath, bindings: host.bindings, handler: S2NoTerminalCalls(), delegation: coordinator)
        try server.start()
        return S2Launch(host: host, store: store, coordinator: coordinator, server: server)
    }

    /// Quits: every runner ends, so the transcripts on disk are final.
    func quit() async {
        await host.shutDown()
        server.stop()
    }

    /// Waits for the pane's run `runId` to end; how it ended.
    func ended(_ runId: String, in pane: String) async throws -> String {
        try await s2Awaited("the end of run \(runId) in \(pane)") { host.end(of: runId, in: pane) }
    }

    /// The human's send with nothing held: the text starts the next run.
    func humanSend(_ text: String, in pane: String) async throws -> String {
        #expect(await coordinator.send(text, in: pane) == .nothingHeld)
        return try #require(await host.startRun(sessionId: pane, input: text))
    }

    /// The parent's one Claude-format transcript in this launch's config.
    func parentTranscript(repo: URL) throws -> String {
        let projects = host.config.appendingPathComponent("projects/\(SessionHistory.claudeProjectFolder(repo.path))", isDirectory: true)
        let transcripts = try FileManager.default.contentsOfDirectory(at: projects, includingPropertiesForKeys: nil).filter { $0.pathExtension == "jsonl" }
        #expect(transcripts.count == 1)
        return try String(contentsOf: try #require(transcripts.first), encoding: .utf8)
    }
}

/// Polls `condition` until it holds. The bound only ends a scenario that
/// broke; nothing here asserts how long any step took.
private func s2Until(_ what: String, _ condition: () async -> Bool) async throws {
    let deadline = Date().addingTimeInterval(120)
    while !(await condition()) {
        guard Date() < deadline else { throw MightyError("Scenario S2 never reached: \(what).") }
        try await Task.sleep(nanoseconds: 20_000_000)
    }
}

private func s2Awaited<Value>(_ what: String, _ produce: () async -> Value?) async throws -> Value {
    var found: Value?
    try await s2Until(what) { found = await produce(); return found != nil }
    return try #require(found)
}

/// Fixture git, kept away from the user's and the system's settings.
@discardableResult
private func s2Git(_ arguments: [String], in folder: URL) async throws -> String {
    let executable = try #require(DelegationGit.executable)
    var environment = ProcessInfo.processInfo.environment.filter { !$0.key.hasPrefix("GIT_") }
    environment["GIT_CONFIG_GLOBAL"] = "/dev/null"; environment["GIT_CONFIG_NOSYSTEM"] = "1"; environment["GIT_OPTIONAL_LOCKS"] = "0"
    for key in ["GIT_AUTHOR", "GIT_COMMITTER"] { environment[key + "_NAME"] = "Fixture"; environment[key + "_EMAIL"] = "fixture@example.invalid" }
    let result = try await ProcessCapture.run(executable: executable, arguments: ["-C", folder.path] + arguments, environment: environment, cwd: folder, timeout: 120)
    #expect(result.exitCode == 0, "git \(arguments.joined(separator: " ")): \(String(decoding: result.stderr, as: UTF8.self))")
    return DelegationGit.line(result.stdout)
}

/// How many times `needle` appears in `text`.
private func s2Count(of needle: String, in text: String) -> Int { text.components(separatedBy: needle).count - 1 }

/// Scenario S2, headless: its own temp repository and profile, the switch on,
/// a fake Claude parent with three children. Child 1's notice reaches the
/// parent while it is idle after a normal finish; after the human stops the
/// parent, children 2 and 3 report and their notices are held. The app quits,
/// and from that state each release path is played on its own copy of the
/// profile and the Claude config, by a coordinator rebuilt from that profile:
/// the Mac's and the phone's send both call ``DelegationCoordinator/send(_:in:)``,
/// their run next both call ``DelegationCoordinator/runNext(in:)``.
@Suite(.serialized) struct DelegationScenarioS2Tests {
    static let parentMode = "acceptEdits"

    enum ReleasePath: String, CaseIterable {
        case macSend, phoneSend, macRunNext, phoneRunNext
        var isSend: Bool { self == .macSend || self == .phoneSend }
    }

    /// The state the first launch leaves on disk.
    private struct Held {
        let launch: URL
        let resumeId: String
        let children: [ChildRecord]
        let notices: [Notice]
        let file: DelegationFile
    }

    @Test func heldNoticesAfterAStopAreReleasedTogetherOldestFirstByEachSendAndRunNextAfterARelaunch() async throws {
        let base = try shortTemporaryDirectory()
        let suite = "mightyclaude.s2." + UUID().uuidString
        UserDefaults(suiteName: suite)?.set(true, forKey: DelegationSwitch.defaultsKey)
        defer {
            UserDefaults().removePersistentDomain(forName: suite)
            try? FileManager.default.removeItem(at: base)
        }
        let fake = try scenarioS2FakeClaude(in: base)
        let repo = base.appendingPathComponent("repo", isDirectory: true)
        try FileManager.default.createDirectory(at: repo, withIntermediateDirectories: true)
        try await s2Git(["init", "-q", "-b", "main"], in: repo)
        try Data("# Practice\n".utf8).write(to: repo.appendingPathComponent("README.md"))
        try await s2Git(["add", "-A"], in: repo); try await s2Git(["commit", "-q", "-m", "Start"], in: repo)

        let held = try await holdTwoNotices(base: base, repo: repo, fake: fake, suite: suite)
        for (index, path) in ReleasePath.allCases.enumerated() {
            try await release(path, from: held, folder: base.appendingPathComponent("launch-\(index + 2)", isDirectory: true), base: base, repo: repo, fake: fake, suite: suite)
        }
    }

    /// The first launch: three children, child 1's notice delivered to the
    /// idle parent, the human's stop, then children 2 and 3's notices held.
    private func holdTwoNotices(base: URL, repo: URL, fake: URL, suite: String) async throws -> Held {
        let folder = base.appendingPathComponent("launch-1", isDirectory: true)
        let worktrees = base.appendingPathComponent("worktrees", isDirectory: true)
        let first = try S2Launch.start(folder: folder, home: base, fake: fake, switchSuite: suite, worktrees: worktrees) { host in
            host.addPane("parent", mode: Self.parentMode, folder: repo.path)
        }
        do {
            let tasks = ["Feature one: note what one needs.", "Feature two: note what two needs.", "Feature three: note what three needs."]
            for (index, task) in tasks.enumerated() {
                // Each child reports only once a human answers its request.
                first.host.childScript(forTaskContaining: task, [
                    ["permission": "Write", "input": ["file_path": "\(ChildWorktree.notesFolder)/REPORT.md"]],
                    ["write": "\(ChildWorktree.notesFolder)/REPORT.md", "text": "# Report\n\nChild \(index + 1) is done.\n"],
                    ["say": "Reported."],
                ])
            }

            // 1. The parent delegates three children and finishes normally.
            try first.host.script("parent", tasks.map { task -> [String: Any] in ["call": "delegate", "arguments": ["task": task, "mode": Self.parentMode]] } + [["say": "Three children are on it."]])
            let delegating = try await first.humanSend("Split the three features.", in: "parent")
            #expect(try await first.ended(delegating, in: "parent") == "completed")
            let children = try await s2Awaited("three children on disk") { () -> [ChildRecord]? in
                guard let file = try? first.store.load(), file.children.count == 3 else { return nil }
                let ordered = tasks.compactMap { task in file.children.first { file.copy(childId: $0.id, kind: .task)?.text.contains(task) == true } }
                return ordered.count == 3 ? ordered : nil
            }
            var asks: [ToolPermissionRequest] = []
            for child in children { asks.append(try await s2Awaited("\(child.id)'s request") { first.host.openCard(child.id, tool: "Write") }) }

            // 2. Child 1 reports while the parent is idle after its normal
            //    finish: its notice starts the parent's next run.
            try first.host.script("parent", [["say": "Child one reported."]])
            try await first.host.answer(children[0].id, asks[0], allow: true)
            let noticeRun = try await s2Awaited("child 1's notice starting a parent run") { first.host.runs("parent").dropFirst(1).first }
            #expect(try await first.ended(noticeRun, in: "parent") == "completed")
            let notice1 = try await s2Awaited("child 1's notice delivered") { try? first.store.load().notices.first { $0.childId == children[0].id && $0.lane == .delivered } }
            #expect(notice1.kind == .reported && notice1.reportRevision == 1)
            #expect(notice1.receipt?.route == .queue && notice1.receipt?.runId == noticeRun)
            #expect(first.host.input(of: noticeRun, in: "parent") == DelegationCoordinator.text(of: notice1))

            // 3. The human stops the parent's next run while it waits.
            try first.host.script("parent", [["permission": "Bash", "input": ["command": "swift build"]], ["say": "Built."]])
            let stopped = try await first.humanSend("Check the build while they work.", in: "parent")
            _ = try await s2Awaited("the parent's request in the run to stop") { first.host.openCard("parent", tool: "Bash") }
            await first.host.stopRun(sessionId: "parent")
            #expect(try await first.ended(stopped, in: "parent") == "stopped")
            #expect(first.host.pane("parent")?.state.activity == .idle)

            // 4. Children 2 and 3 report, in that order: both notices are held.
            for (child, ask) in zip(children.dropFirst(), asks.dropFirst()) {
                try await first.host.answer(child.id, ask, allow: true)
                _ = try await s2Awaited("\(child.id)'s notice held") { try? first.store.load().notices.first { $0.childId == child.id && $0.lane == .held } }
            }
            #expect(first.host.runs("parent") == [delegating, noticeRun, stopped])
            let file = try first.store.load()
            let notices = try children.map { child in try #require(file.notices.first { $0.childId == child.id }) }
            #expect(file.notices.count == 3)
            #expect(notices.map(\.lane) == [.delivered, .held, .held])
            #expect(notices.allSatisfy { $0.kind == .reported && $0.reportRevision == 1 })
            #expect(await first.coordinator.heldItems(for: "parent").map(\.id) == [notices[1].id, notices[2].id])
            #expect(try first.store.load().children.allSatisfy { $0.state == .reported })
            let resumeId = try #require(first.host.pane("parent")?.resumeId)

            await first.quit()
            return Held(launch: folder, resumeId: resumeId, children: children, notices: notices, file: try first.store.load())
        } catch {
            await first.quit()
            throw error
        }
    }

    /// A relaunch on a copy of the first launch's profile and Claude config:
    /// it starts no run by itself, and `path` releases both held notices
    /// together, oldest first, each reaching the parent's transcript once.
    private func release(_ path: ReleasePath, from held: Held, folder: URL, base: URL, repo: URL, fake: URL, suite: String) async throws {
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        for name in ["profile", "claude-config"] {
            try FileManager.default.copyItem(at: held.launch.appendingPathComponent(name, isDirectory: true), to: folder.appendingPathComponent(name, isDirectory: true))
        }
        let launch = try S2Launch.start(folder: folder, home: base, fake: fake, switchSuite: suite, worktrees: base.appendingPathComponent("worktrees", isDirectory: true)) { host in
            // The relaunched app's panes: none running, the parent with its session to resume.
            host.addPane("parent", mode: Self.parentMode, folder: repo.path, resumeId: held.resumeId)
            for child in held.children { host.addPane(child.id, mode: child.startingMode, folder: child.worktreePath, parent: "parent") }
        }
        let after: String?
        do {
            after = try await play(path, launch: launch, held: held)
            await launch.quit()
        } catch {
            await launch.quit()
            throw error
        }
        guard let after else { return }

        // The parent's Claude-format transcript, one session across both
        // launches: each notice id exactly once, the released two oldest first
        // and ahead of the human's text or row.
        let label = Comment(rawValue: path.rawValue)
        let transcript = try launch.parentTranscript(repo: repo)
        for notice in held.notices { #expect(s2Count(of: notice.id, in: transcript) == 1, "\(path.rawValue) \(notice.id)") }
        #expect(s2Count(of: after, in: transcript) == 1, label)
        let positions = [held.notices[1].id, held.notices[2].id, after].compactMap { transcript.range(of: $0)?.lowerBound }
        #expect(positions.count == 3 && positions == positions.sorted(), label)

        // The Mac send's record, for scripts/delegation-record.py in macOS CI.
        if path == .macSend {
            let panes = (["parent"] + held.children.map(\.id)).compactMap { id in launch.host.pane(id).map { ($0.state, $0.resumeId) } }
            try exportDelegationRecord("s2", profile: folder.appendingPathComponent("profile", isDirectory: true), repo: repo, claudeConfig: launch.host.config, panes: panes)
        }
    }

    /// Plays `path` in the relaunch; the text that follows the released
    /// notices in the parent's transcript, or nil when nothing was released.
    private func play(_ path: ReleasePath, launch: S2Launch, held: Held) async throws -> String? {
        let (notice1, notice2, notice3) = (held.notices[0], held.notices[1], held.notices[2])
        let label = Comment(rawValue: path.rawValue)

        // The rebuilt coordinator starts no run by itself, not even in a delivery pass.
        await launch.coordinator.deliverPending()
        #expect(launch.host.allRuns.isEmpty, label)
        #expect(try launch.store.load() == held.file, label)
        #expect(await launch.coordinator.heldItems(for: "parent").map(\.id) == [notice2.id, notice3.id], label)

        try launch.host.script("parent", [["say": "Read both reports."]])
        let heldTexts = [DelegationCoordinator.text(of: notice2), DelegationCoordinator.text(of: notice3)]
        let releaseRun: String
        let after: String
        if path.isSend {
            let text = path == .macSend ? "From the Mac: merge what is ready." : "From the phone: merge what is ready."
            let release = await launch.coordinator.send(text, in: "parent")
            guard case .released(let runId, let itemIds) = release else { Issue.record("\(path.rawValue): \(release)"); return nil }
            #expect(itemIds == [notice2.id, notice3.id], label)
            #expect(launch.host.input(of: runId, in: "parent") == (heldTexts + [text]).joined(separator: "\n\n"), label)
            #expect(try await launch.ended(runId, in: "parent") == "completed", label)
            #expect(launch.host.runs("parent") == [runId], label)
            (releaseRun, after) = (runId, text)
        } else {
            // The run-next route: what is held first; with nothing held, the
            // pane's next queued human row.
            var rows = ["Human row: run the tests."]
            func runNext() async -> DelegationRelease {
                let release = await launch.coordinator.runNext(in: "parent")
                if release == .nothingHeld, !rows.isEmpty { _ = await launch.host.startRun(sessionId: "parent", input: rows.removeFirst()) }
                return release
            }
            let release = await runNext()
            guard case .released(let runId, let itemIds) = release else { Issue.record("\(path.rawValue): \(release)"); return nil }
            #expect(itemIds == [notice2.id, notice3.id], label)
            #expect(launch.host.input(of: runId, in: "parent") == heldTexts.joined(separator: "\n\n"), label)
            #expect(try await launch.ended(runId, in: "parent") == "completed", label)
            // The queued human row runs in the next run.
            try launch.host.script("parent", [["say": "Tests pass."]])
            #expect(await runNext() == .nothingHeld, label)
            let runs = launch.host.runs("parent")
            #expect(runs.count == 2 && runs.first == runId, label)
            let rowRun = try #require(runs.dropFirst().first)
            #expect(launch.host.input(of: rowRun, in: "parent") == "Human row: run the tests.", label)
            #expect(try await launch.ended(rowRun, in: "parent") == "completed", label)
            (releaseRun, after) = (runId, "Human row: run the tests.")
        }

        // Saved delivered, each once, by the one release run.
        let file = try launch.store.load()
        #expect(file.notices.first { $0.id == notice1.id } == notice1, label)
        for notice in [notice2, notice3] {
            let saved = try #require(file.notices.first { $0.id == notice.id })
            #expect(saved.lane == .delivered && saved.receipt?.route == .queue && saved.receipt?.runId == releaseRun, label)
        }
        #expect(await launch.coordinator.heldItems(for: "parent").isEmpty, label)
        #expect(await launch.coordinator.send("Again.", in: "parent") == .nothingHeld, label)
        #expect(await launch.coordinator.runNext(in: "parent") == .nothingHeld, label)
        return after
    }
}
