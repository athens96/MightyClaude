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

/// External opens: the system's default browser, through NSWorkspace.
struct WorkspaceURLOpener: ExternalURLOpener {
    func open(_ url: URL) async -> Bool {
        await MainActor.run { NSWorkspace.shared.open(url) }
    }
}

/// An agent pane's browser pane as ``AgentWebOpener`` sees it. The engine and
/// the pane itself live in the store; this handle asks the store to show a page.
final class AgentBrowserHandle: AgentBrowserPane, @unchecked Sendable {
    private let agentPaneId: String
    private weak var store: AppStore?

    init(agentPaneId: String, store: AppStore) {
        self.agentPaneId = agentPaneId
        self.store = store
    }

    func show(_ url: URL) async -> Bool {
        await MainActor.run { store?.showAgentBrowser(url, agentPaneId: agentPaneId) ?? false }
    }
}

extension AppStore {
    /// Listen on the per-profile agent IO socket so runs can be given their
    /// per-pane MCP server. Without it runs simply start without the four tools.
    func startAgentIO() {
        guard agentIOServer == nil, let executable = Bundle.main.executableURL else { return }
        let socketPath = AgentIOWire.socketPath(dataDirectory: dataDirectory)
        // In-app opens go to the asking agent pane's own browser pane; with the
        // browser engine off or missing, no pane is made and the page opens in
        // the system browser instead. The choice is read per call from the
        // persisted store Settings writes to.
        let opener = makeAgentWebOpener()
        agentWebOpener = opener
        let webOpen = WebOpenService(store: .shared, presenter: webOpenPrompts, opener: opener, paneRegistry: .shared)
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
    /// as a tab beside it when the layout has no room for a split.
    func openAgentTerminalPane(_ agentPaneId: String, select: Bool) {
        guard agentTerminals[agentPaneId] != nil else { return }
        openAgentIOPane(AgentIOPaneRegistry.shared.terminalPaneId(for: agentPaneId), kind: AgentIOPaneKind.terminal,
                        title: L("agentTerminal.terminalPane.title"), agentPaneId: agentPaneId, select: select)
    }

    /// Whether an in-app open from this agent pane can show a page: the pane
    /// is open and the browser engine was turned on at launch and started.
    func canShowAgentBrowser(_ agentPaneId: String) -> Bool {
        !ending && snapshot.sessions.contains(where: { $0.id == agentPaneId }) && CefBrowserEngine.canShowPages
    }

    /// An agent opened `url` in the app: show it in the agent pane's one
    /// browser pane, made on first use and navigated on every later open, and
    /// put the pane back on screen if the user closed it. False when the page
    /// cannot be shown, so the caller opens it in the system browser instead.
    func showAgentBrowser(_ url: URL, agentPaneId: String) -> Bool {
        guard canShowAgentBrowser(agentPaneId), let agent = snapshot.sessions.first(where: { $0.id == agentPaneId }) else { return false }
        let engine = agentBrowsers[agentPaneId] ?? CefBrowserEngine(profileKey: agent.workspaceId)
        guard engine.isAvailable, openAgentIOPane(AgentIOPaneRegistry.shared.browserPaneId(for: agentPaneId), kind: AgentIOPaneKind.browser,
                                                  title: L("agentTerminal.browserPane.title"), agentPaneId: agentPaneId, select: false) else { return false }
        agentBrowsers[agentPaneId] = engine
        engine.loadURL(url)
        return true
    }

    private func makeAgentWebOpener() -> AgentWebOpener {
        AgentWebOpener(external: WorkspaceURLOpener()) { agentPaneId, _ in
            await MainActor.run { self.canShowAgentBrowser(agentPaneId) ? AgentBrowserHandle(agentPaneId: agentPaneId, store: self) : nil }
        }
    }

    /// The app itself opens a page for an agent pane (a background execution's
    /// dashboard) the way an agent's open_url does: the workspace's remembered
    /// choice without asking, the choice dialog otherwise. Nothing opens for a
    /// pane that closed meanwhile; false when nothing was shown.
    func openInAgentBrowser(_ url: URL, agentPaneId: String) async -> Bool {
        guard !ending, let agent = snapshot.sessions.first(where: { $0.id == agentPaneId }) else { return false }
        let service = WebOpenService(store: .shared, presenter: webOpenPrompts, opener: agentWebOpener ?? makeAgentWebOpener(), paneRegistry: .shared)
        switch await service.open(url.absoluteString, workspaceId: agent.workspaceId, agentPaneId: agentPaneId, provider: agent.provider) {
        case .opened, .openedExternallyInstead: return true
        case .failed, .rejected: return false
        }
    }

    /// Open an agent pane's terminal or browser pane to the right of the agent
    /// pane, or as a tab beside it when the layout has no room for a split. An
    /// open pane stays where the user put it. The pane is never saved: its
    /// processes and pages end with the app. False when the layout has no
    /// place for it.
    @discardableResult
    private func openAgentIOPane(_ id: String, kind: String, title: String, agentPaneId: String, select: Bool) -> Bool {
        guard let agent = snapshot.sessions.first(where: { $0.id == agentPaneId }) else { return false }
        if !snapshot.sessions.contains(where: { $0.id == id }), snapshot.sessions.count < 128 {
            var session = RunSession(id: id, workspaceId: agent.workspaceId, title: agent.title + " \u{2014} " + title, kind: kind, provider: agent.provider)
            session.ownerSessionId = agentPaneId
            reconcilePaneLayout(agent.workspaceId)
            let root = layoutForWorkspace(agent.workspaceId)
            let group = root?.group(containing: agentPaneId)
            var next = PaneLayouts.inserting(root: root, sessionId: id, targetGroupId: group?.id, placement: "right")
            if next?.group(containing: id) == nil {
                // A new tab is selected on insert; keep showing what the group showed.
                next = PaneLayouts.selecting(root: PaneLayouts.inserting(root: root, sessionId: id, targetGroupId: group?.id, placement: "tab"), id: group?.selectedSessionId ?? agentPaneId)
            }
            guard let next, next.group(containing: id) != nil else { return false }
            let mode = paneLayoutMode(agent.workspaceId)
            snapshot.sessions.append(session)
            savePaneLayout(next, workspaceId: agent.workspaceId)
            if mode != "focus" { setPaneLayoutMode(next.kind == "split" ? "custom" : "tabs", workspaceId: agent.workspaceId) }
        }
        guard snapshot.sessions.contains(where: { $0.id == id }) else { return false }
        if select { selectSession(id) }
        return true
    }

    /// Let go of terminal views and browser engines nothing can show again:
    /// both the agent pane and its IO pane are closed. Processes keep running
    /// until quit; a later in-app open makes a new browser pane.
    func releaseUnreachableAgentIOPanes() {
        let ids = Set(snapshot.sessions.map(\.id))
        for (agentPaneId, host) in agentTerminals where !ids.contains(agentPaneId) && !ids.contains(AgentIOPaneRegistry.shared.terminalPaneId(for: agentPaneId)) {
            host.dispose()
            agentTerminals.removeValue(forKey: agentPaneId)
        }
        for agentPaneId in agentBrowsers.keys where !ids.contains(agentPaneId) && !ids.contains(AgentIOPaneRegistry.shared.browserPaneId(for: agentPaneId)) {
            agentBrowsers.removeValue(forKey: agentPaneId)
            if let opener = agentWebOpener { Task { await opener.forgetPane(agentPaneId: agentPaneId) } }
        }
    }
}
