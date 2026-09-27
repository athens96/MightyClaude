import Foundation

/// App-lifetime registry that maps each agent pane to its long-lived
/// `AgentTerminalRunner` and its one dedicated `AgentTerminalPane`.
///
/// The runner and pane are created on first terminal-tool use and kept here
/// even after the agent pane closes, so that:
/// - Processes launched before the pane closed keep running in the background.
/// - When the agent pane is reopened (with a new MCP token) it re-attaches to
///   the same runner and can `readLatestOutput` or `stop` existing handles.
///
/// Processes stop only when the app quits (``terminateAll(graceSeconds:)``).
///
/// Thread-safe via NSLock.
public final class AgentProcessRegistry: @unchecked Sendable {
    public static let shared = AgentProcessRegistry()
    /// How long quit waits after SIGTERM before sending SIGKILL.
    public static let quitGraceSeconds: TimeInterval = 1

    private let lock = NSLock()
    private var runners: [String: AgentTerminalRunner] = [:]
    private var panes: [String: any AgentTerminalPane] = [:]

    public init() {}

    /// Return the existing runner for `agentPaneId`, or nil if none exists yet.
    public func runner(forAgentPane id: String) -> AgentTerminalRunner? {
        lock.lock(); defer { lock.unlock() }
        return runners[id]
    }

    /// Store a runner for `agentPaneId`, along with the pane it drives.
    public func setRunner(_ runner: AgentTerminalRunner, forAgentPane id: String) {
        lock.lock(); defer { lock.unlock() }
        runners[id] = runner
        panes[id] = runner.terminalPane
    }

    /// Return the existing terminal pane for `agentPaneId`, or nil if none.
    public func terminalPane(forAgentPane id: String) -> (any AgentTerminalPane)? {
        lock.lock(); defer { lock.unlock() }
        return panes[id]
    }

    /// Return the existing runner for `agentPaneId`, or create the agent pane's
    /// one terminal pane with `makePane` and a runner for it. `makePane` runs
    /// only on first use, so the pane is created once and reused afterwards.
    public func makeOrReuseRunner(forAgentPane id: String, clock: AgentTerminalClock = SystemAgentTerminalClock(), makePane: () -> any AgentTerminalPane) -> AgentTerminalRunner {
        lock.lock(); defer { lock.unlock() }
        if let existing = runners[id] { return existing }
        let pane = makePane()
        let runner = AgentTerminalRunner(pane: pane, clock: clock)
        runners[id] = runner
        panes[id] = pane
        return runner
    }

    /// Stop every tracked process group and forget all runners. Called when the
    /// app quits: SIGTERM to each running process group, then SIGKILL to any
    /// still running after `graceSeconds`. Bounded by `graceSeconds` plus one poll.
    public func terminateAll(graceSeconds: TimeInterval = AgentProcessRegistry.quitGraceSeconds) async {
        let owned = lock.withLock {
            let owned = Array(panes.values)
            runners.removeAll(); panes.removeAll()
            return owned
        }
        let targets = owned.flatMap { pane in pane.runningHandles().map { (pane, $0) } }
        guard !targets.isEmpty else { return }
        for (pane, handle) in targets { pane.sendSIGTERM(handle: handle) }
        let deadline = Date().addingTimeInterval(graceSeconds)
        while Date() < deadline, targets.contains(where: { $0.0.isRunning(handle: $0.1) }) {
            try? await Task.sleep(nanoseconds: 50_000_000)
        }
        for (pane, handle) in targets where pane.isRunning(handle: handle) { pane.sendSIGKILL(handle: handle) }
    }

    /// IDs of agent panes that currently have a runner.
    public var activePaneIds: [String] {
        lock.lock(); defer { lock.unlock() }
        return runners.keys.sorted()
    }
}
