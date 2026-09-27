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
        // Each agent pane's terminal pane runs its commands under PTYs and shows
        // itself next to the agent pane whenever a command starts. The store
        // lives as long as the app, so the factory holds it strongly.
        let handler = AgentTerminalIOHandler(processes: .shared, panes: .shared, webOpen: webOpen) { binding in
            let agentPaneId = binding.agentPaneId
            return PTYAgentTerminalPane(workingDirectory: URL(fileURLWithPath: binding.workspacePath, isDirectory: true)) { pane in
                Task { @MainActor in self.showAgentTerminal(pane, agentPaneId: agentPaneId) }
            }
        }
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

    /// A command started in the agent pane's terminal pane: give the pane its
    /// view on first use, and put it back on screen if the user closed it.
    func showAgentTerminal(_ pane: PTYAgentTerminalPane, agentPaneId: String) {
        guard !ending, snapshot.sessions.contains(where: { $0.id == agentPaneId }) else { return }
        if agentTerminals[agentPaneId] == nil {
            let id = AgentIOPaneRegistry.shared.terminalPaneId(for: agentPaneId)
            agentTerminals[agentPaneId] = AgentTerminalHost(pane: pane, sessionId: id, controller: sharedTerminalController(), focused: { [weak self] in
                guard self?.snapshot.activeSessionId != id else { return }
                self?.selectSession(id)
            }, closeRequested: { [weak self] in self?.closeSession(id) })
        }
        openAgentTerminalPane(agentPaneId, select: false)
    }

    /// Open the agent pane's terminal pane to the right of the agent pane, or
    /// as a tab beside it when the layout has no room for a split. An open
    /// pane stays where the user put it. The pane is never saved: its
    /// processes end with the app.
    func openAgentTerminalPane(_ agentPaneId: String, select: Bool) {
        guard agentTerminals[agentPaneId] != nil, let agent = snapshot.sessions.first(where: { $0.id == agentPaneId }) else { return }
        let id = AgentIOPaneRegistry.shared.terminalPaneId(for: agentPaneId)
        if !snapshot.sessions.contains(where: { $0.id == id }), snapshot.sessions.count < 128 {
            var session = RunSession(id: id, workspaceId: agent.workspaceId, title: agent.title + " \u{2014} " + L("agentTerminal.terminalPane.title"), kind: AgentIOPaneKind.terminal, provider: agent.provider)
            session.ownerSessionId = agentPaneId
            reconcilePaneLayout(agent.workspaceId)
            let root = layoutForWorkspace(agent.workspaceId)
            let group = root?.group(containing: agentPaneId)
            var next = PaneLayouts.inserting(root: root, sessionId: id, targetGroupId: group?.id, placement: "right")
            if next?.group(containing: id) == nil {
                // A new tab is selected on insert; keep showing what the group showed.
                next = PaneLayouts.selecting(root: PaneLayouts.inserting(root: root, sessionId: id, targetGroupId: group?.id, placement: "tab"), id: group?.selectedSessionId ?? agentPaneId)
            }
            guard let next, next.group(containing: id) != nil else { return }
            let mode = paneLayoutMode(agent.workspaceId)
            snapshot.sessions.append(session)
            savePaneLayout(next, workspaceId: agent.workspaceId)
            if mode != "focus" { setPaneLayoutMode(next.kind == "split" ? "custom" : "tabs", workspaceId: agent.workspaceId) }
        }
        if select { selectSession(id) }
    }

    /// Let go of terminal views nothing can show again: both the agent pane
    /// and its terminal pane are closed. The processes keep running until quit.
    func releaseUnreachableAgentTerminals() {
        let ids = Set(snapshot.sessions.map(\.id))
        for (agentPaneId, host) in agentTerminals where !ids.contains(agentPaneId) && !ids.contains(AgentIOPaneRegistry.shared.terminalPaneId(for: agentPaneId)) {
            host.dispose()
            agentTerminals.removeValue(forKey: agentPaneId)
        }
    }
}
