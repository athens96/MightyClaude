import Foundation
import Darwin
import Testing
@testable import MightyCore

private final class PaneFactoryLog: @unchecked Sendable {
    private let lock = NSLock()
    private var made: [String: [FakeAgentTerminalPane]] = [:]
    var running = false
    func make(_ binding: PaneMCPBinding) -> any AgentTerminalPane {
        let pane = FakeAgentTerminalPane()
        lock.lock(); pane.nextRunning = running; made[binding.agentPaneId, default: []].append(pane); lock.unlock()
        return pane
    }
    func panes(_ id: String) -> [FakeAgentTerminalPane] { lock.lock(); defer { lock.unlock() }; return made[id] ?? [] }
}

/// One terminal pane per agent pane, reused, surviving pane close until the app quits.
struct TerminalPaneLifetimeTests {
    private func handler(_ log: PaneFactoryLog, processes: AgentProcessRegistry = AgentProcessRegistry(), panes: AgentIOPaneRegistry = AgentIOPaneRegistry()) -> AgentTerminalIOHandler {
        AgentTerminalIOHandler(processes: processes, panes: panes, webOpen: silentWebOpenService(), clock: FakeAgentTerminalClock()) { log.make($0) }
    }

    @Test func onePanePerAgentPaneCreatedOnFirstUseAndReused() async {
        let log = PaneFactoryLog()
        let processes = AgentProcessRegistry()
        let io = handler(log, processes: processes)
        _ = await io.handle(AgentIORequest(tool: "run_in_terminal", command: "echo one"), binding: testPaneBinding(pane: "a"))
        _ = await io.handle(AgentIORequest(tool: "run_in_terminal", command: "echo two"), binding: testPaneBinding(pane: "a"))
        _ = await io.handle(AgentIORequest(tool: "run_in_terminal", command: "echo other"), binding: testPaneBinding(pane: "b"))
        #expect(log.panes("a").count == 1)
        #expect(log.panes("a").first?.launched == ["echo one", "echo two"])
        #expect(log.panes("b").count == 1)
        #expect((processes.terminalPane(forAgentPane: "a") as AnyObject?) === log.panes("a").first)
        #expect(processes.activePaneIds == ["a", "b"])
    }

    @Test func processesSurviveClosingTheAgentPaneAndReattachWithANewToken() async throws {
        let log = PaneFactoryLog(); log.running = true
        let processes = AgentProcessRegistry()
        let bindings = PaneMCPBindingRegistry()
        let io = handler(log, processes: processes)
        let location = PaneMCPServerLocation(socketPath: "/tmp/unused.sock", executable: URL(fileURLWithPath: "/bin/false"))
        let first = bindings.bind(agentPaneId: "a", server: location, workspaceId: "ws-1", workspacePath: "/tmp", provider: "claude")
        let started = await io.handle(AgentIORequest(tool: "run_in_terminal", command: "npm run dev"), binding: first)
        let handle = try #require(started.handle)
        #expect(started.status == "running")

        // The agent pane closes: its token is revoked, the process keeps running.
        bindings.revoke(agentPaneId: "a")
        #expect(bindings.binding(forToken: first.token) == nil)
        let pane = try #require(log.panes("a").first)
        #expect(pane.isRunning(handle: handle))
        pane.appendUserTyped(text: "compiled while closed\n", handle: handle)

        // Reopened with a new token: same process, same handle.
        let second = bindings.bind(agentPaneId: "a", server: location, workspaceId: "ws-1", workspacePath: "/tmp", provider: "claude")
        #expect(second.token != first.token)
        let read = await io.handle(AgentIORequest(tool: "read_latest_output", handle: handle), binding: second)
        #expect(read.error == nil)
        #expect(read.output == "compiled while closed\n")
        let stopped = await io.handle(AgentIORequest(tool: "stop", handle: handle), binding: second)
        #expect(stopped.status == "done")
        #expect(stopped.signal == SIGINT)
        #expect(log.panes("a").count == 1)
    }

    @Test func userTypedTextAppearsInTheReadBack() async throws {
        let pane = FakeAgentTerminalPane()
        pane.nextOutput = "Proceed? [y/N] "
        pane.nextRunning = true
        let runner = AgentTerminalRunner(pane: pane, clock: FakeAgentTerminalClock())
        let run = try await runner.runInTerminal(command: "./setup.sh")
        pane.appendUserTyped(text: "y\n", handle: run.handle)
        pane.appendUserTyped(text: "^C\n", handle: run.handle)
        let read = try #require(await runner.readLatestOutput(handle: run.handle))
        #expect(read.output == "y\n^C\n")
    }

    @Test func realPaneMixesUserTypedTextWithProcessOutput() async throws {
        let folder = try shortTemporaryDirectory(); defer { try? FileManager.default.removeItem(at: folder) }
        let pane = SubprocessAgentTerminalPane(workingDirectory: folder)
        try await pane.launch(command: "echo prompt; exec cat", handle: "p")
        defer { pane.sendSIGKILL(handle: "p") }
        let deadline = Date().addingTimeInterval(5)
        while pane.readOutput(handle: "p", fromOffset: 0, maxBytes: 64).output.isEmpty, Date() < deadline { try await Task.sleep(nanoseconds: 20_000_000) }
        pane.appendUserTyped(text: "answer\n", handle: "p")
        #expect(pane.readOutput(handle: "p", fromOffset: 0, maxBytes: 1024).output == "prompt\nanswer\n")
    }

    @Test func quitTerminatesEveryTrackedProcessGroupWithinTheBound() async throws {
        let folder = try shortTemporaryDirectory(); defer { try? FileManager.default.removeItem(at: folder) }
        let processes = AgentProcessRegistry()
        let polite = SubprocessAgentTerminalPane(workingDirectory: folder)
        let stubborn = SubprocessAgentTerminalPane(workingDirectory: folder)
        _ = processes.makeOrReuseRunner(forAgentPane: "polite") { polite }
        _ = processes.makeOrReuseRunner(forAgentPane: "stubborn") { stubborn }
        try await polite.launch(command: "sleep 30", handle: "p")
        try await stubborn.launch(command: "trap '' TERM; while :; do sleep 1; done", handle: "s")
        try await Task.sleep(nanoseconds: 200_000_000)
        let began = Date()
        await processes.terminateAll(graceSeconds: 0.5)
        #expect(Date().timeIntervalSince(began) < 2)
        let deadline = Date().addingTimeInterval(3)
        while (polite.isRunning(handle: "p") || stubborn.isRunning(handle: "s")), Date() < deadline { try await Task.sleep(nanoseconds: 20_000_000) }
        #expect(polite.terminationSignal(handle: "p") == SIGTERM)
        #expect(stubborn.terminationSignal(handle: "s") == SIGKILL)
        #expect(processes.activePaneIds.isEmpty)
        #expect(processes.runner(forAgentPane: "polite") == nil)
    }

    @Test func quitWithNothingRunningReturnsAtOnce() async {
        let processes = AgentProcessRegistry()
        _ = processes.makeOrReuseRunner(forAgentPane: "idle") { FakeAgentTerminalPane() }
        let began = Date()
        await processes.terminateAll()
        #expect(Date().timeIntervalSince(began) < 0.5)
        #expect(processes.activePaneIds.isEmpty)
    }
}
