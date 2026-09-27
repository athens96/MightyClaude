import AppKit
import Foundation
import MightyCore

/// Handles `--agent-io-mcp`: the per-pane stdio MCP server that Claude and Codex
/// runs launch. Called before CEF, NSApplication or any other initialisation,
/// so it never opens a window or a second GUI instance. Exits when stdin closes.
enum AgentIOMCPCommand {
    static func run() -> Never {
        signal(SIGPIPE, SIG_IGN)
        AgentIOMCPServer().run(input: .standardInput, output: .standardOutput)
        exit(0)
    }
}

/// Stage-1 URL choice presenter: answers "inside the app" at once without
/// remembering it. The workspace choice dialog replaces it in stage 3.
struct ImmediateInAppWebOpenPresenter: WebOpenPromptPresenter {
    func present(url: URL, workspaceId: String) {}
    func pendingChoice() -> (destination: WebOpenDestination, remember: Bool)? { (.inApp, false) }
    func dismiss() {}
}

/// Stage-1 opener: hands pages to the system browser. The in-app path does the
/// same until stage 3 routes it to the CEF browser pane, so no agent browser
/// pane is registered for the phone's pane list yet.
struct SystemWebOpener: WebOpener {
    func openInApp(_ url: URL) async { await open(url) }
    func openExternally(_ url: URL) async { await open(url) }
    @MainActor private func open(_ url: URL) { NSWorkspace.shared.open(url) }
}

extension AppStore {
    /// Listen on the per-profile agent IO socket so runs can be given their
    /// per-pane MCP server. Without it runs simply start without the four tools.
    func startAgentIO() {
        guard agentIOServer == nil, let executable = Bundle.main.executableURL else { return }
        let socketPath = AgentIOWire.socketPath(dataDirectory: dataDirectory)
        let webOpen = WebOpenService(store: .shared, presenter: ImmediateInAppWebOpenPresenter(), opener: SystemWebOpener())
        let handler = AgentTerminalIOHandler(processes: .shared, panes: .shared, webOpen: webOpen)
        let server = AgentIOSocketServer(socketPath: socketPath, bindings: paneBindings, handler: handler)
        do {
            try server.start()
            agentIOServer = server
            agentIOLocation = PaneMCPServerLocation(socketPath: socketPath, executable: executable)
        } catch {
            server.stop()
            NSLog("MightyClaude agent IO socket unavailable; runs start without the terminal and web tools: %@", error.localizedDescription)
        }
    }

    /// Stop accepting tool calls, revoke every pane token and remove the socket.
    func stopAgentIO() {
        agentIOServer?.stop()
        agentIOServer = nil
        paneBindings.revokeAll()
    }
}
