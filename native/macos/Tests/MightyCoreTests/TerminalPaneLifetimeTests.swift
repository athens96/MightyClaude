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

private final class PTYFactoryLog: @unchecked Sendable {
    private let lock = NSLock()
    private var made: [PTYAgentTerminalPane] = []
    private var shown: [ObjectIdentifier] = []
    var panes: [PTYAgentTerminalPane] { lock.lock(); defer { lock.unlock() }; return made }
    /// The panes the app was asked to show, once per started command.
    var launches: [ObjectIdentifier] { lock.lock(); defer { lock.unlock() }; return shown }
    func make(_ folder: URL) -> any AgentTerminalPane {
        let pane = PTYAgentTerminalPane(workingDirectory: folder) { [weak self] pane in
            guard let self else { return }
            self.lock.lock(); self.shown.append(ObjectIdentifier(pane)); self.lock.unlock()
        }
        lock.lock(); made.append(pane); lock.unlock()
        return pane
    }
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

    // MARK: Real PTY

    /// Runs return at once unless `clock` lets them wait for a quick command to finish.
    private func ptyHandler(_ folder: URL, processes: AgentProcessRegistry, made: PTYFactoryLog, clock: AgentTerminalClock = SkipInitialWaitClock()) -> AgentTerminalIOHandler {
        AgentTerminalIOHandler(processes: processes, panes: AgentIOPaneRegistry(), webOpen: silentWebOpenService(), clock: clock) { _ in made.make(folder) }
    }

    @Test func realPaneRunsTheCommandOnATTY() async throws {
        let folder = try shortTemporaryDirectory(); defer { try? FileManager.default.removeItem(at: folder) }
        let pane = PTYAgentTerminalPane(workingDirectory: folder)
        let result = try await AgentTerminalRunner(pane: pane).runInTerminal(command: "tty; test -t 0 && test -t 1 && test -t 2 && echo all-tty")
        #expect(result.status == .done && result.exitCode == 0)
        #expect(result.output.hasPrefix("/dev/ttys"))
        #expect(result.output.contains("all-tty\n"))
    }

    @Test func realPaneDeliversTypedTextToAReadingCommandAndTheAgentReadsIt() async throws {
        let folder = try shortTemporaryDirectory(); defer { try? FileManager.default.removeItem(at: folder) }
        let made = PTYFactoryLog(), processes = AgentProcessRegistry()
        let io = ptyHandler(folder, processes: processes, made: made)
        let started = await io.handle(AgentIORequest(tool: "run_in_terminal", command: "read x; echo got:$x"), binding: testPaneBinding(pane: "a", workspacePath: folder.path))
        let handle = try #require(started.handle)
        #expect(started.status == "running")
        let pane = try #require(made.panes.first)
        #expect(pane.inputHandle == handle)
        pane.sendUserInput("hello\r")
        #expect(await waitFor { !pane.isRunning(handle: handle) })
        let read = await io.handle(AgentIORequest(tool: "read_latest_output", handle: handle), binding: testPaneBinding(pane: "a", workspacePath: folder.path))
        #expect(read.status == "done" && read.exitCode == 0)
        // The typed line comes back once, through the tty's echo, then the command's answer.
        #expect(read.output == "hello\ngot:hello\n")
        #expect(pane.inputHandle == nil)
    }

    @Test func realPaneTypedCtrlCInterruptsTheRunningCommand() async throws {
        let folder = try shortTemporaryDirectory(); defer { try? FileManager.default.removeItem(at: folder) }
        let pane = PTYAgentTerminalPane(workingDirectory: folder)
        let runner = AgentTerminalRunner(pane: pane, clock: SkipInitialWaitClock())
        let run = try await runner.runInTerminal(command: "sleep 30")
        #expect(run.status == .running)
        pane.sendUserInput("\u{03}")
        #expect(await waitFor(seconds: 3) { !pane.isRunning(handle: run.handle) })
        #expect(pane.terminationSignal(handle: run.handle) == SIGINT)
    }

    @Test func realPaneIsOnePerAgentPaneAndShowsEveryCommandUnderItsHeader() async throws {
        let folder = try shortTemporaryDirectory(); defer { try? FileManager.default.removeItem(at: folder) }
        let made = PTYFactoryLog(), processes = AgentProcessRegistry()
        let io = ptyHandler(folder, processes: processes, made: made, clock: SystemAgentTerminalClock())
        let first = await io.handle(AgentIORequest(tool: "run_in_terminal", command: "echo one"), binding: testPaneBinding(pane: "a", workspacePath: folder.path))
        let second = await io.handle(AgentIORequest(tool: "run_in_terminal", command: "echo two"), binding: testPaneBinding(pane: "a", workspacePath: folder.path))
        #expect(first.output == "one\n" && second.output == "two\n")
        #expect(made.panes.count == 1)
        let pane = try #require(made.panes.first)
        #expect((processes.terminalPane(forAgentPane: "a") as AnyObject?) === pane)
        #expect(made.launches == [ObjectIdentifier(pane), ObjectIdentifier(pane)])
        let probe = TranscriptProbe()
        pane.subscribe { probe.receive($0) }
        #expect(await waitFor { probe.text.contains("two") })
        #expect(probe.text == "\u{1B}[2m$ echo one\u{1B}[0m\r\none\r\n\u{1B}[2m$ echo two\u{1B}[0m\r\ntwo\r\n")
    }

    @Test func realProcessesSurviveTheTerminalPaneClosingAndReopening() async throws {
        let folder = try shortTemporaryDirectory(); defer { try? FileManager.default.removeItem(at: folder) }
        let made = PTYFactoryLog(), processes = AgentProcessRegistry(), bindings = PaneMCPBindingRegistry()
        let io = ptyHandler(folder, processes: processes, made: made)
        let location = PaneMCPServerLocation(socketPath: "/tmp/unused.sock", executable: URL(fileURLWithPath: "/bin/false"))
        let first = bindings.bind(agentPaneId: "a", server: location, workspaceId: "ws-1", workspacePath: folder.path, provider: "claude")
        let started = await io.handle(AgentIORequest(tool: "run_in_terminal", command: "echo started; exec cat"), binding: first)
        let handle = try #require(started.handle)
        let pane = try #require(made.panes.first)
        let shown = TranscriptProbe()
        let subscription = pane.subscribe { shown.receive($0) }
        #expect(await waitFor { shown.text.contains("started") })

        // The terminal pane and its agent pane close: the view lets go, the token is revoked.
        pane.unsubscribe(subscription)
        bindings.revoke(agentPaneId: "a")
        pane.sendUserInput("while closed\r")
        #expect(await waitFor { pane.readOutput(handle: handle, fromOffset: 0, maxBytes: 1024).output.contains("while closed\r\nwhile closed\r\n") })
        #expect(pane.isRunning(handle: handle))
        #expect(!shown.text.contains("while closed"))

        // Reopened: the same pane replays everything, and a new token reaches the same process.
        let reopened = TranscriptProbe()
        pane.subscribe { reopened.receive($0) }
        #expect(await waitFor { reopened.text.contains("while closed\r\nwhile closed\r\n") })
        #expect(reopened.text.hasPrefix("\u{1B}[2m$ echo started; exec cat\u{1B}[0m\r\nstarted\r\n"))
        let second = bindings.bind(agentPaneId: "a", server: location, workspaceId: "ws-1", workspacePath: folder.path, provider: "claude")
        let read = await io.handle(AgentIORequest(tool: "read_latest_output", handle: handle), binding: second)
        #expect(read.status == "running")
        #expect((started.output ?? "") + (read.output ?? "") == "started\nwhile closed\nwhile closed\n")
        let stopped = await io.handle(AgentIORequest(tool: "stop", handle: handle), binding: second)
        #expect(stopped.status == "done" && stopped.signal == SIGINT)
        #expect(made.panes.count == 1)
    }

    @Test func quitTerminatesEveryTrackedProcessGroupWithinTheBound() async throws {
        let folder = try shortTemporaryDirectory(); defer { try? FileManager.default.removeItem(at: folder) }
        let processes = AgentProcessRegistry()
        let polite = PTYAgentTerminalPane(workingDirectory: folder)
        let stubborn = PTYAgentTerminalPane(workingDirectory: folder)
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
