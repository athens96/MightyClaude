import Foundation

/// Serves the four agent tools for the pane a token resolved to.
///
/// run, read and stop go through the pane's own ``AgentTerminalRunner`` in
/// ``AgentProcessRegistry``, so a handle launched by another pane is unknown
/// here and rejected. open_url goes through ``WebOpenService``. The terminal
/// pane itself comes from `makePane`, where the app also puts it on screen.
public final class AgentTerminalIOHandler: AgentIORequestHandler, @unchecked Sendable {
    public typealias PaneFactory = @Sendable (PaneMCPBinding) -> any AgentTerminalPane

    private let processes: AgentProcessRegistry
    private let panes: AgentIOPaneRegistry
    private let webOpen: WebOpenService
    private let makePane: PaneFactory
    private let clock: AgentTerminalClock

    public init(processes: AgentProcessRegistry, panes: AgentIOPaneRegistry, webOpen: WebOpenService, clock: AgentTerminalClock = SystemAgentTerminalClock(),
                makePane: @escaping PaneFactory = { PTYAgentTerminalPane(workingDirectory: URL(fileURLWithPath: $0.workspacePath, isDirectory: true)) }) {
        self.processes = processes; self.panes = panes; self.webOpen = webOpen; self.clock = clock; self.makePane = makePane
    }

    public func handle(_ request: AgentIORequest, binding: PaneMCPBinding) async -> AgentIOResponse {
        switch request.tool {
        case PaneMCPToolManifest.runInTerminal.name:
            guard let command = request.command, !command.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return .failure("command must be a non-empty string.") }
            guard command.utf8.count <= AgentIOWire.maxCommandBytes else { return .failure("command is longer than 64 KB.") }
            let runner = processes.makeOrReuseRunner(forAgentPane: binding.agentPaneId, clock: clock) { makePane(binding) }
            panes.registerTerminalPane(agentPaneId: binding.agentPaneId, workspaceId: binding.workspaceId, provider: binding.provider)
            do { return AgentIOResponse(try await runner.runInTerminal(command: command)) }
            catch { return .failure("The command could not be started: \(error.localizedDescription)") }
        case PaneMCPToolManifest.readLatestOutput.name, PaneMCPToolManifest.stop.name:
            guard let handle = request.handle, !handle.isEmpty, handle.count <= AgentIOWire.maxHandleLength else { return .failure("handle must be the handle returned by run_in_terminal.") }
            let runner = processes.runner(forAgentPane: binding.agentPaneId)
            let result = request.tool == PaneMCPToolManifest.stop.name ? await runner?.stop(handle: handle) : await runner?.readLatestOutput(handle: handle)
            guard let result else { return .failure("Unknown handle for this agent pane. Use a handle returned by run_in_terminal in this pane.") }
            return AgentIOResponse(result)
        case PaneMCPToolManifest.openURL.name:
            guard let url = request.url else { return .failure("url must be a string.") }
            switch await webOpen.open(url, workspaceId: binding.workspaceId, agentPaneId: binding.agentPaneId, provider: binding.provider) {
            case .opened(let destination, let opened): return AgentIOResponse(destination: destination.rawValue, url: opened.absoluteString)
            case .rejected(let reason): return .failure(WebOpenURLValidator.message(for: reason))
            }
        default:
            return .failure("Unknown tool \(request.tool).")
        }
    }
}
