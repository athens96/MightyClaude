import Foundation
import Testing
@testable import MightyCore

/// One pane of the scenario: its state as the coordinator reads it, its own
/// runner with its own fake Claude script and log, and what its runs did.
private struct ScenarioPane {
    var state: DelegationPaneState
    let runner: ProcessRunner
    let script: URL
    let log: URL
    var resumeId: String?
    /// The runs the host started here, oldest first, and how each ended.
    var runs: [String] = []
    var ends: [String: String] = [:]
    /// Every permission card event of the pane, in order.
    var cards: [ToolPermissionRequest] = []
}

/// A run event and the run it belongs to, as the host named that run.
private struct Arrival {
    var pane: String
    var runId: String?
    var event: RunEvent
}

/// No terminal calls are made here.
private struct NoTerminalCalls: AgentIORequestHandler {
    func handle(_ request: AgentIORequest, binding: PaneMCPBinding) async -> AgentIOResponse { .failure("not under test") }
}

/// The app side for the scenario: each pane runs the scripted fake Claude
/// through the real ProcessRunner, one runner per pane so each pane follows
/// its own script, all bound in one registry behind one socket server whose
/// delegation handler is the real coordinator. Run events reach the
/// coordinator one at a time, in the order the runners sent them.
private final class ScenarioHost: DelegationHost, @unchecked Sendable {
    static let workspaceId = "ws-s1"
    let base: URL
    let fake: URL
    let socketPath: String
    let switchSuite: String
    let bindings = PaneMCPBindingRegistry()
    var config: URL { base.appendingPathComponent("claude-config", isDirectory: true) }
    var plugin: URL { base.appendingPathComponent("plugin", isDirectory: true) }
    private let lock = NSLock()
    private var panes: [String: ScenarioPane] = [:]
    /// A child's script, picked by a phrase of the task its first run carries.
    private var childScripts: [(phrase: String, steps: [[String: Any]])] = []
    private var coordinator: DelegationCoordinator?
    private let arrivals: AsyncStream<Arrival>
    private let arrive: AsyncStream<Arrival>.Continuation
    private var pump: Task<Void, Never>?

    init(base: URL, fake: URL, socketPath: String, switchSuite: String) throws {
        self.base = base; self.fake = fake; self.socketPath = socketPath; self.switchSuite = switchSuite
        (arrivals, arrive) = AsyncStream<Arrival>.makeStream()
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

    func addPane(_ sessionId: String, mode: String, folder: String, parent: String? = nil) {
        let script = base.appendingPathComponent("script-\(sessionId).json"), log = base.appendingPathComponent("log-\(sessionId).jsonl")
        let pane = ScenarioPane(state: DelegationPaneState(sessionId: sessionId, permissionMode: mode, folder: folder, parentSessionId: parent),
                                runner: makeRunner(sessionId, script: script, log: log), script: script, log: log)
        lock.withLock { panes[sessionId] = pane }
    }

    /// What the pane's next run plays: one turn of `steps`.
    func script(_ sessionId: String, _ steps: [[String: Any]]) throws {
        guard let url = lock.withLock({ panes[sessionId]?.script }) else { throw MightyError("No pane \(sessionId).") }
        try JSONSerialization.data(withJSONObject: ["turns": [steps]]).write(to: url)
    }

    /// What a child whose task contains `phrase` plays in its first run.
    func childScript(forTaskContaining phrase: String, _ steps: [[String: Any]]) { lock.withLock { childScripts.append((phrase, steps)) } }

    func pane(_ sessionId: String) -> ScenarioPane? { lock.withLock { panes[sessionId] } }
    func runs(_ sessionId: String) -> [String] { pane(sessionId)?.runs ?? [] }
    func end(of runId: String, in sessionId: String) -> String? { pane(sessionId)?.ends[runId] }

    /// The card for `tool` still open in the pane, skipping `excluding`.
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
            "PATH": "/usr/bin:/bin:/usr/sbin:/sbin", "HOME": base.path, "CLAUDE_CONFIG_DIR": config.path,
            "FAKE_CLAUDE_SCRIPT": script.path, "FAKE_CLAUDE_LOG": log.path,
            // The children's commits, kept away from the user's and the system's git settings.
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
        return true
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
            panes[sessionId]?.runs.append(runId)
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
        arrive.yield(Arrival(pane: sessionId, runId: runId, event: event))
    }

    private func handle(_ arrival: Arrival) async {
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

/// Polls `condition` until it holds. The bound only ends a scenario that
/// broke; nothing here asserts how long any step took.
private func until(_ what: String, _ condition: () async -> Bool) async throws {
    let deadline = Date().addingTimeInterval(120)
    while !(await condition()) {
        guard Date() < deadline else { throw MightyError("Scenario S1 never reached: \(what).") }
        try await Task.sleep(nanoseconds: 20_000_000)
    }
}

private func awaited<Value>(_ what: String, _ produce: () async -> Value?) async throws -> Value {
    var found: Value?
    try await until(what) { found = await produce(); return found != nil }
    return try #require(found)
}

/// The value the approval card shows for the input `name`.
private func shown(_ name: String, on card: ToolPermissionRequest) -> String? {
    ToolPermissionPresentation.make(toolName: card.toolName, inputJSON: card.inputJSON).fields.first { $0.label == name }?.value
}

private func toolText(_ result: Any?) -> String {
    ((result as? [String: Any])?["content"] as? [[String: Any]] ?? []).compactMap { $0["text"] as? String }.joined(separator: "\n")
}

/// Scenario S1, headless: its own temp repository and profile, the switch on,
/// a fake Claude parent in an asking mode delegating two children, a child's
/// approval only a human answers, both reports, the parent's fast-forward of
/// child 1 and the human's discard of child 2. Judged from what is on disk.
@Suite(.serialized) struct DelegationScenarioS1Tests {
    static let delegateTool = "mcp__\(DelegationMCPServer.serverName)__delegate"
    static let mergeTool = "mcp__\(DelegationMCPServer.serverName)__merge"
    static let parentMode = "acceptEdits"

    private struct Scenario {
        let base: URL
        let host: ScenarioHost
        let store: DelegationFileStore
        let coordinator: DelegationCoordinator
        let server: AgentIOSocketServer
        let switchSuite: String
        var repo: URL { base.appendingPathComponent("repo", isDirectory: true) }
        var profile: URL { base.appendingPathComponent("profile", isDirectory: true) }

        static func make() async throws -> Scenario {
            let base = try shortTemporaryDirectory()
            // The hidden switch, on, in a defaults domain of this scenario alone.
            let suite = "mightyclaude.s1." + UUID().uuidString
            UserDefaults(suiteName: suite)?.set(true, forKey: DelegationSwitch.defaultsKey)
            let socketPath = base.appendingPathComponent("io.sock").path
            let host = try ScenarioHost(base: base, fake: try fakeClaudeExecutable(in: base), socketPath: socketPath, switchSuite: suite)
            let store = DelegationFileStore(directory: base.appendingPathComponent("profile", isDirectory: true))
            let maker = ChildWorktreeMaker(root: base.appendingPathComponent("worktrees", isDirectory: true), freeBytes: { _ in 50_000_000_000 })
            let coordinator = try DelegationCoordinator(store: store, host: host, worktrees: maker, isSwitchOn: { DelegationSwitch.isOn(UserDefaults(suiteName: suite) ?? .standard) })
            host.attach(coordinator)
            let server = AgentIOSocketServer(socketPath: socketPath, bindings: host.bindings, handler: NoTerminalCalls(), delegation: coordinator)
            try server.start()
            let scenario = Scenario(base: base, host: host, store: store, coordinator: coordinator, server: server, switchSuite: suite)
            try FileManager.default.createDirectory(at: scenario.repo, withIntermediateDirectories: true)
            try await scenario.git(["init", "-q", "-b", "main"])
            try Data("# Practice\n".utf8).write(to: scenario.repo.appendingPathComponent("README.md"))
            try await scenario.git(["add", "-A"]); try await scenario.git(["commit", "-q", "-m", "Start"])
            host.addPane("parent", mode: DelegationScenarioS1Tests.parentMode, folder: scenario.repo.path)
            return scenario
        }

        /// Ends every pane's runner, so the logs and transcripts on disk are final.
        func tearDownRuns() async {
            for id in ["parent"] + ((try? store.load().children.map(\.id)) ?? []) {
                if let runner = host.pane(id)?.runner { await runner.shutdown() }
            }
        }

        func tearDown() async {
            await host.shutDown()
            server.stop()
            UserDefaults().removePersistentDomain(forName: switchSuite)
            try? FileManager.default.removeItem(at: base)
        }

        /// The human's send in the pane: what is held goes first through the
        /// coordinator, and with nothing held the text starts the next run.
        func humanSend(_ text: String, in pane: String) async throws -> String {
            #expect(await coordinator.send(text, in: pane) == .nothingHeld)
            return try #require(await host.startRun(sessionId: pane, input: text))
        }

        /// Waits for the pane's run `runId` to end; how it ended.
        func ended(_ runId: String, in pane: String) async throws -> String {
            try await awaited("the end of run \(runId) in \(pane)") { host.end(of: runId, in: pane) }
        }

        /// The child's steps: a tracked file, its commit (asked for), then REPORT.md.
        func childSteps(file: String, text: String, report: String) throws -> [[String: Any]] {
            let git = try #require(DelegationGit.executable).path
            return [
                ["write": file, "text": text],
                ["bash": "'\(git)' add -- \(file) && '\(git)' commit -q -m 'Add \(file)'", "ask": true],
                ["write": "\(ChildWorktree.notesFolder)/REPORT.md", "text": "# Report\n\n\(report)\n"],
                ["say": "Committed and reported."],
            ]
        }

        /// Fixture git, kept away from the user's and the system's settings.
        @discardableResult
        func git(_ arguments: [String], in directory: String? = nil) async throws -> String {
            let executable = try #require(DelegationGit.executable)
            var environment = ProcessInfo.processInfo.environment.filter { !$0.key.hasPrefix("GIT_") }
            environment["GIT_CONFIG_GLOBAL"] = "/dev/null"; environment["GIT_CONFIG_NOSYSTEM"] = "1"; environment["GIT_OPTIONAL_LOCKS"] = "0"
            for key in ["GIT_AUTHOR", "GIT_COMMITTER"] { environment[key + "_NAME"] = "Fixture"; environment[key + "_EMAIL"] = "fixture@example.invalid" }
            let folder = directory ?? repo.path
            let result = try await ProcessCapture.run(executable: executable, arguments: ["-C", folder] + arguments, environment: environment, cwd: URL(fileURLWithPath: folder), timeout: 120)
            #expect(result.exitCode == 0, "git \(arguments.joined(separator: " ")): \(String(decoding: result.stderr, as: UTF8.self))")
            return DelegationGit.line(result.stdout)
        }
    }

    @Test func aParentInAnAskingModeDelegatesTwoChildrenMergesOneAndAHumanDiscardsTheOther() async throws {
        let s = try await Scenario.make()
        do { try await play(s) } catch { await s.tearDown(); throw error }
        await s.tearDown()
    }

    private func play(_ s: Scenario) async throws {
        let base = try await s.git(["rev-parse", "HEAD"])
        let task1 = "Feature one: add feature-one.txt saying one."
        let task2 = "Feature two: add feature-two.txt saying two."
        try s.host.childScript(forTaskContaining: task1, s.childSteps(file: "feature-one.txt", text: "one\n", report: "Added feature-one.txt."))
        try s.host.childScript(forTaskContaining: task2, s.childSteps(file: "feature-two.txt", text: "two\n", report: "Added feature-two.txt."))

        // 1. The parent, in an asking mode, delegates one child at its own
        //    mode and one narrower; each delegate call waits on a human
        //    approval that shows the starting mode asked for.
        try s.host.script("parent", [
            ["call": "delegate", "arguments": ["task": task1, "mode": Self.parentMode], "ask": true],
            ["call": "delegate", "arguments": ["task": task2, "mode": "manual"], "ask": true],
            ["say": "Two children are on it."],
        ])
        let first = try await s.humanSend("Delegate feature one at your own mode and feature two at manual.", in: "parent")
        let approveOne = try await awaited("the first delegate approval") { s.host.openCard("parent", tool: Self.delegateTool) }
        #expect(shown("mode", on: approveOne) == Self.parentMode)
        #expect(shown("task", on: approveOne) == task1)
        try await s.host.answer("parent", approveOne, allow: true)
        let approveTwo = try await awaited("the second delegate approval") { s.host.openCard("parent", tool: Self.delegateTool, excluding: [approveOne.id]) }
        #expect(shown("mode", on: approveTwo) == "manual")
        #expect(shown("task", on: approveTwo) == task2)
        try await s.host.answer("parent", approveTwo, allow: true)
        #expect(try await s.ended(first, in: "parent") == "completed")
        let (one, two) = try await awaited("both children on disk") { () -> (ChildRecord, ChildRecord)? in
            guard let file = try? s.store.load(), file.children.count == 2,
                  let one = file.children.first(where: { $0.startingMode == Self.parentMode }),
                  let two = file.children.first(where: { $0.startingMode == "manual" }) else { return nil }
            return (one, two)
        }

        // 2. Both children ask a human before committing. Child 2's request
        //    stays open while child 1 reports and the parent's run for that
        //    notice tries to answer it: the parent has no way to.
        let askTwo = try await awaited("child 2's approval request") { s.host.openCard(two.id, tool: "Bash") }
        let askOne = try await awaited("child 1's approval request") { s.host.openCard(one.id, tool: "Bash") }
        // Each child pane was made in the mode its parent asked for.
        #expect(s.host.pane(one.id)?.state.permissionMode == Self.parentMode)
        #expect(s.host.pane(two.id)?.state.permissionMode == "manual")
        try s.host.script("parent", [
            ["call": "list_children", "arguments": [:] as [String: String]],
            ["call": "answer_permission", "arguments": ["child": two.id, "request": askTwo.id, "behavior": "allow"]],
            ["call": "child_status", "arguments": ["child": two.id]],
            ["say": "Child 2 waits for its human."],
        ])
        try await s.host.answer(one.id, askOne, allow: true)
        let noticeRunOne = try await awaited("child 1's notice starting a parent run") { s.host.runs("parent").dropFirst(1).first }
        #expect(try await s.ended(noticeRunOne, in: "parent") == "completed")
        #expect(s.host.openCard(two.id, tool: "Bash")?.id == askTwo.id)
        #expect(jsonLines(try #require(s.host.pane(two.id)).log).filter { $0["event"] as? String == "permission" }.isEmpty)
        #expect(try s.store.load().children.first { $0.id == two.id }?.reportRevision == 0)

        // A human answers child 2's request; child 2 commits and reports too.
        try s.host.script("parent", [["call": "list_children", "arguments": [:] as [String: String]], ["say": "Both children reported."]])
        try await s.host.answer(two.id, askTwo, allow: true)
        let noticeRunTwo = try await awaited("child 2's notice starting a parent run") { s.host.runs("parent").dropFirst(2).first }
        #expect(try await s.ended(noticeRunTwo, in: "parent") == "completed")
        let head1 = try await awaited("child 1's reported head") { () -> String? in
            guard let file = try? s.store.load(), file.children.allSatisfy({ $0.state == .reported }) else { return nil }
            return file.children.first { $0.id == one.id }?.reportHead
        }

        // 3. Asked by the human, the parent fast-forwards child 1 through the
        //    merge tool, which raises a permission request in this mode.
        try s.host.script("parent", [
            ["call": "child_status", "arguments": ["child": one.id]],
            ["call": "merge", "arguments": ["child": one.id, "expected_head": head1], "ask": true],
            ["say": "Merged child 1."],
        ])
        let mergeRun = try await s.humanSend("Merge child 1 if its report is right.", in: "parent")
        let approveMerge = try await awaited("the merge approval") { s.host.openCard("parent", tool: Self.mergeTool) }
        #expect(shown("child", on: approveMerge) == one.id)
        #expect(shown("expected_head", on: approveMerge) == head1)
        try await s.host.answer("parent", approveMerge, allow: true)
        #expect(try await s.ended(mergeRun, in: "parent") == "completed")

        // 4. A human discards child 2 from its card.
        #expect(await s.coordinator.discardChild(two.id) == .discarded)
        await s.tearDownRuns()

        // 5. The verdict, from disk alone.
        try await judge(s, base: base, head1: head1, one: one.id, two: two.id, noticeRuns: [noticeRunOne, noticeRunTwo])
    }

    /// Scenario S1's rules over the profile's delegation file, the
    /// repository, the fake Claude logs and the Claude-format transcripts.
    private func judge(_ s: Scenario, base: String, head1: String, one: String, two: String, noticeRuns: [String]) async throws {
        let file = try DelegationFileStore(directory: s.profile).load()
        #expect(Set(file.children.map(\.id)) == [one, two])
        let child1 = try #require(file.children.first { $0.id == one }), child2 = try #require(file.children.first { $0.id == two })
        #expect(file.children.allSatisfy { $0.parentSessionId == "parent" && $0.parentBranch == "main" && $0.baseCommit == base })

        // Each stored mode equals its request: one the parent's own, one narrower.
        #expect(child1.startingMode == Self.parentMode)
        #expect(child2.startingMode == "manual")
        #expect(DelegationCoordinator.isNoWider(child2.startingMode, than: Self.parentMode) && child2.startingMode != Self.parentMode)

        // Both reported once; child 1 is merged, child 2 discarded with its report kept.
        #expect(child1.state == .merged && child1.reportRevision == 1 && child1.reportHead == head1)
        #expect(child2.state == .discarded && child2.reportRevision == 1 && child2.reportHead != nil && child2.reportHead != head1)
        #expect(file.copy(childId: one, kind: .report)?.text.contains("Added feature-one.txt.") == true)
        #expect(file.copy(childId: two, kind: .report)?.text.contains("Added feature-two.txt.") == true)

        // One reported notice per child, each delivered once, as the next run of the idle parent.
        #expect(file.notices.count == 2 && Set(file.notices.map(\.id)).count == 2)
        for (child, run) in zip([one, two], noticeRuns) {
            let notices = file.notices.filter { $0.childId == child }
            #expect(notices.count == 1)
            #expect(notices.allSatisfy { $0.kind == .reported && $0.reportRevision == 1 && $0.lane == .delivered && $0.receipt?.route == .queue && $0.receipt?.runId == run })
        }
        #expect(file.followUps.isEmpty)

        // The tool's one fast-forward of main, from the base to child 1's head.
        #expect(file.merges.count == 1)
        let merge = try #require(file.merges.first)
        #expect(merge.kind == .toolFastForward && merge.childId == one && merge.parentBranch == "main")
        #expect(merge.preMergeCommit == base && merge.mergedCommit == head1 && merge.childHead == head1)

        // The repository: main moved to child 1's head with no merge commit;
        // child 1's branch and worktree stay, child 2's are gone.
        #expect(try await s.git(["symbolic-ref", "--short", "HEAD"]) == "main")
        #expect(try await s.git(["rev-parse", "refs/heads/main"]) == head1)
        #expect(try await s.git(["rev-list", "--parents", "-n", "1", head1]) == "\(head1) \(base)")
        #expect(try await s.git(["status", "--porcelain", "--untracked-files=no"]).isEmpty)
        #expect(FileManager.default.fileExists(atPath: s.repo.appendingPathComponent("feature-one.txt").path))
        #expect(!FileManager.default.fileExists(atPath: s.repo.appendingPathComponent("feature-two.txt").path))
        #expect(try await s.git(["for-each-ref", "--format=%(refname)", "refs/heads/mighty"]) == "refs/heads/\(child1.branch)")
        #expect(try await s.git(["rev-parse", "refs/heads/\(child1.branch)"]) == head1)
        let worktrees = try await s.git(["worktree", "list", "--porcelain"])
        #expect(worktrees.contains("worktree \(child1.worktreePath)"))
        #expect(!worktrees.contains(child2.worktreePath))
        #expect(!FileManager.default.fileExists(atPath: child2.worktreePath))

        // The parent ran in its asking mode every time, and a human allowed
        // each delegate call and the merge.
        let parentLog = jsonLines(try #require(s.host.pane("parent")).log)
        let launches = parentLog.filter { $0["event"] as? String == "argv" }.compactMap { $0["arguments"] as? [String] }
        #expect(launches.count == 4)
        #expect(launches.allSatisfy { optionValue("--permission-mode", $0) == Self.parentMode && optionValue("--permission-prompt-tool", $0) == "stdio" })
        let answers = parentLog.filter { $0["event"] as? String == "permission" }
        #expect(answers.map { $0["tool"] as? String } == [Self.delegateTool, Self.delegateTool, Self.mergeTool])
        #expect(answers.allSatisfy { $0["behavior"] as? String == "allow" })
        let calls = parentLog.filter { $0["event"] as? String == "call" }
        let delegated = calls.filter { $0["tool"] as? String == "delegate" }.map { (($0["result"] as? [String: Any])?["structuredContent"] as? [String: Any])?["child"] as? [String: Any] }
        #expect(delegated.map { $0?["id"] as? String } == [one, two])
        #expect(delegated.map { $0?["mode"] as? String } == [Self.parentMode, "manual"])
        // The parent's tools hold no way to answer a child's approval: its
        // try was refused and answered nothing.
        #expect(parentLog.first { $0["event"] as? String == "mcp" }?["tools"] as? [String] == DelegationToolManifest.all.map(\.name))
        let tried = try #require(calls.first { $0["tool"] as? String == "answer_permission" })
        #expect((tried["result"] as? [String: Any])?["isError"] as? Bool == true)
        #expect(toolText(tried["result"]).contains("Unknown delegation tool answer_permission."))
        let merged = try #require(calls.first { $0["tool"] as? String == "merge" })
        #expect((merged["result"] as? [String: Any])?["isError"] as? Bool != true)

        // Each child ran in its stored mode and committed after exactly one
        // human answer to its approval request.
        for child in [child1, child2] {
            let log = jsonLines(try #require(s.host.pane(child.id)).log)
            let argv = log.filter { $0["event"] as? String == "argv" }.compactMap { $0["arguments"] as? [String] }
            #expect(argv.count == 1 && argv.allSatisfy { optionValue("--permission-mode", $0) == child.startingMode })
            let asked = log.filter { $0["event"] as? String == "permission" }
            #expect(asked.count == 1 && asked.first?["tool"] as? String == "Bash" && asked.first?["behavior"] as? String == "allow")
            #expect(log.filter { $0["event"] as? String == "bash" }.map { $0["code"] as? Int } == [0])
        }

        // The parent's Claude-format transcript: one session across its four
        // runs, holding each notice exactly once.
        let projects = s.host.config.appendingPathComponent("projects/\(SessionHistory.claudeProjectFolder(s.repo.path))", isDirectory: true)
        let transcripts = try FileManager.default.contentsOfDirectory(at: projects, includingPropertiesForKeys: nil).filter { $0.pathExtension == "jsonl" }
        #expect(transcripts.count == 1)
        let transcript = try String(contentsOf: try #require(transcripts.first), encoding: .utf8)
        for notice in file.notices { #expect(transcript.components(separatedBy: notice.id).count - 1 == 1) }
        #expect(launches.dropFirst().allSatisfy { optionValue("--resume", $0) == transcripts.first?.deletingPathExtension().lastPathComponent })
    }
}
