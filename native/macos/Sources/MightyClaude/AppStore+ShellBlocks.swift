import Foundation
import MightyCore

/// One reply shell-block run the app follows: the terminal handle once it has
/// launched, and whether the user pressed Stop.
struct ShellBlockRunFollow {
    var handle: String?
    var stopRequested = false

    static func id(sessionId: String, key: String) -> String { sessionId + "\n" + key }
}

/// What a pane's transcript needs to draw and press its reply shell blocks' Run
/// buttons (docs/agent-terminal-and-web.md, "Running a shell block from a reply").
struct ShellBlockRunContext {
    /// The pane's runs by `ShellBlocks.key`.
    let records: [String: ShellBlockRun]
    let run: (_ key: String, _ command: String) -> Void
    let stop: (_ key: String) -> Void

    /// One command at a time per pane terminal: a run still going disables the other buttons.
    var busy: Bool { records.values.contains { $0.state == .running } }
}

extension AppStore {
    /// The Run buttons of `session`'s replies: an agent pane in a folder on this Mac.
    func shellBlockContext(_ session: RunSession) -> ShellBlockRunContext? {
        guard session.kind == SessionKind.claude, worktreeHostFolder(session) != nil else { return nil }
        let sessionId = session.id
        return ShellBlockRunContext(records: Dictionary((session.shellRuns ?? []).map { ($0.key, $0) }, uniquingKeysWith: { _, last in last }),
                                    run: { [weak self] key, command in self?.runShellBlock(sessionId: sessionId, key: key, command: command) },
                                    stop: { [weak self] key in self?.stopShellBlock(sessionId: sessionId, key: key) })
    }

    /// The agent pane's terminal pane, the one `run_in_terminal` uses: made on first
    /// use in the folder the pane's runs work in, and shown beside the pane on every launch.
    func agentTerminalRunner(for agentPaneId: String) -> AgentTerminalRunner? {
        guard !ending, let session = snapshot.sessions.first(where: { $0.id == agentPaneId }), let folder = worktreeHostFolder(session) else { return nil }
        let runner = AgentProcessRegistry.shared.makeOrReuseRunner(forAgentPane: agentPaneId) { makeAgentTerminalPane(agentPaneId: agentPaneId, workspacePath: folder) }
        _ = AgentIOPaneRegistry.shared.registerTerminalPane(agentPaneId: agentPaneId, workspaceId: session.workspaceId, provider: session.provider)
        return runner
    }

    /// An agent pane's terminal pane: a PTY per command in `workspacePath`, put on
    /// screen next to the agent pane whenever a command starts.
    nonisolated func makeAgentTerminalPane(agentPaneId: String, workspacePath: String) -> PTYAgentTerminalPane {
        PTYAgentTerminalPane(workingDirectory: URL(fileURLWithPath: workspacePath, isDirectory: true)) { pane in
            Task { @MainActor in self.showAgentTerminal(pane, agentPaneId: agentPaneId) }
        }
    }

    /// The user pressed a reply block's Run (or Run again): the block's command runs in
    /// the pane's terminal and its record follows the output until the command ends.
    /// Nothing runs while another of this pane's runs is still going.
    func runShellBlock(sessionId: String, key: String, command: String) {
        guard let index = snapshot.sessions.firstIndex(where: { $0.id == sessionId }),
              !(snapshot.sessions[index].shellRuns ?? []).contains(where: { $0.state == .running }),
              !command.isEmpty, command.utf8.count <= AgentIOWire.maxCommandBytes,
              let runner = agentTerminalRunner(for: sessionId) else { return }
        let id = ShellBlockRunFollow.id(sessionId: sessionId, key: key)
        shellBlockFollows[id] = ShellBlockRunFollow()
        updateShellRuns(sessionId) { runs in
            runs.removeAll { $0.key == key }
            runs.append(ShellBlockRun(key: key, command: command))
            if runs.count > ShellBlockRun.maximumRuns { runs.removeFirst(runs.count - ShellBlockRun.maximumRuns) }
        }
        Task { [weak self] in
            let handle: String
            do { handle = try await runner.start(command: command) }
            catch {
                self?.updateShellRun(sessionId, key: key) { $0.append(error.localizedDescription); $0.finish(exitCode: nil, signal: nil, stopped: false) }
                self?.shellBlockFollows[id] = nil
                return
            }
            guard let self else { return }
            self.shellBlockFollows[id]?.handle = handle
            if self.shellBlockFollows[id]?.stopRequested == true { self.sendShellBlockStop(runner: runner, handle: handle, sessionId: sessionId, key: key) }
            await self.followShellBlock(runner: runner, handle: handle, sessionId: sessionId, key: key)
        }
    }

    /// Reads the run's cleaned output into its record a few times a second until it ends.
    private func followShellBlock(runner: AgentTerminalRunner, handle: String, sessionId: String, key: String) async {
        let id = ShellBlockRunFollow.id(sessionId: sessionId, key: key)
        while let result = await runner.readLatestOutput(handle: handle) {
            let done = result.status == .done && !result.moreRemains
            let stopped = shellBlockFollows[id]?.stopRequested == true
            if !result.output.isEmpty || done {
                updateShellRun(sessionId, key: key) { run in
                    run.append(result.output)
                    if done { run.finish(exitCode: result.exitCode, signal: result.signal, stopped: stopped) }
                }
            }
            if done { break }
            if !result.moreRemains { try? await Task.sleep(for: .milliseconds(250)) }
        }
        // The runner no longer knows the handle (it was let go): end the record here so the
        // pane is not busy for ever. A record that already ended is left as it is.
        let stopped = shellBlockFollows[id]?.stopRequested == true
        updateShellRun(sessionId, key: key) { $0.finish(exitCode: nil, signal: nil, stopped: stopped) }
        shellBlockFollows[id] = nil
    }

    /// The user pressed Stop: the same SIGINT → SIGTERM → SIGKILL escalation as the
    /// agent's `stop`; the record ends `stopped` when the command has gone.
    func stopShellBlock(sessionId: String, key: String) {
        let id = ShellBlockRunFollow.id(sessionId: sessionId, key: key)
        guard shellBlockFollows[id] != nil, shellBlockFollows[id]?.stopRequested == false else { return }
        shellBlockFollows[id]?.stopRequested = true
        // Not launched yet: the stop is sent as soon as the handle arrives.
        guard let handle = shellBlockFollows[id]?.handle, let runner = AgentProcessRegistry.shared.runner(forAgentPane: sessionId) else { return }
        sendShellBlockStop(runner: runner, handle: handle, sessionId: sessionId, key: key)
    }

    private func sendShellBlockStop(runner: AgentTerminalRunner, handle: String, sessionId: String, key: String) {
        Task { [weak self] in
            // The stop's own read takes the last output; it belongs to the record too.
            guard let result = await runner.stop(handle: handle), !result.output.isEmpty else { return }
            self?.updateShellRun(sessionId, key: key) { $0.append(result.output) }
        }
    }

    private func updateShellRun(_ sessionId: String, key: String, _ change: (inout ShellBlockRun) -> Void) {
        updateShellRuns(sessionId) { runs in
            guard let index = runs.lastIndex(where: { $0.key == key }) else { return }
            change(&runs[index])
        }
    }

    private func updateShellRuns(_ sessionId: String, _ change: (inout [ShellBlockRun]) -> Void) {
        guard let index = snapshot.sessions.firstIndex(where: { $0.id == sessionId }) else { return }
        var runs = snapshot.sessions[index].shellRuns ?? []
        change(&runs)
        let next = runs.isEmpty ? nil : runs
        if snapshot.sessions[index].shellRuns != next { snapshot.sessions[index].shellRuns = next }
    }
}
