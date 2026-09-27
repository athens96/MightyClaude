import Foundation

/// The kind values used by agent-owned IO pane entries in the pane list.
public enum AgentIOPaneKind {
    public static let terminal = "agent-terminal"
    public static let browser = "agent-browser"

    /// Whether `kind` is one of the agent-owned IO panes. The phone lists these
    /// panes but may not send them commands, so their summaries carry
    /// `terminal: true`, whether they come from `extraPaneSummaries` or from a
    /// pane the Mac holds as a `RunSession` of its own.
    public static func isAgentIOPane(_ kind: String) -> Bool {
        kind == terminal || kind == browser
    }
}

/// Tracks the IO panes an agent pane owns — its one dedicated terminal pane and
/// the in-app browser pane it opened a URL into — and projects them into the
/// relay pane-list payload alongside the agent pane itself.
///
/// One instance lives for the app's lifetime (`shared`). The Mac calls
/// `registerTerminalPane` on first terminal-tool use, `registerBrowserPane` on
/// the first in-app URL open, and `deregister` when the agent pane closes. The
/// phone then lists both panes beside the agent pane that owns them.
///
/// The projection carries `terminal: true` so a phone treats these panes as
/// view-only: it lists them, and its composer refuses to send them commands.
/// Rendering their contents on the phone is out of scope.
///
/// Thread-safe via NSLock.
public final class AgentIOPaneRegistry: @unchecked Sendable {
    public static let shared = AgentIOPaneRegistry()

    private let lock = NSLock()
    private var terminalEntries: [String: IOPaneEntry] = [:]
    private var browserEntries: [String: IOPaneEntry] = [:]

    public init() {}

    private struct IOPaneEntry: Sendable {
        let agentPaneId: String
        let workspaceId: String
        let provider: String
    }

    // MARK: - Registration

    /// Register the dedicated terminal pane for `agentPaneId`.
    /// Idempotent: a second call updates the stored metadata.
    /// Returns the stable pane id for this terminal pane.
    @discardableResult
    public func registerTerminalPane(agentPaneId: String, workspaceId: String, provider: String) -> String {
        lock.lock()
        terminalEntries[agentPaneId] = IOPaneEntry(agentPaneId: agentPaneId, workspaceId: workspaceId, provider: provider)
        lock.unlock()
        return terminalPaneId(for: agentPaneId)
    }

    /// Register the in-app browser pane `agentPaneId` opened a URL into.
    /// Idempotent: an agent pane keeps one browser pane, so a second open
    /// updates the stored metadata rather than adding a pane.
    /// Returns the stable pane id for this browser pane.
    @discardableResult
    public func registerBrowserPane(agentPaneId: String, workspaceId: String, provider: String) -> String {
        lock.lock()
        browserEntries[agentPaneId] = IOPaneEntry(agentPaneId: agentPaneId, workspaceId: workspaceId, provider: provider)
        lock.unlock()
        return browserPaneId(for: agentPaneId)
    }

    /// Whether a terminal pane has been opened for this agent pane.
    public func hasTerminalPane(agentPaneId: String) -> Bool {
        lock.lock()
        defer { lock.unlock() }
        return terminalEntries[agentPaneId] != nil
    }

    /// Whether this agent pane has opened a URL into an in-app browser pane.
    public func hasBrowserPane(agentPaneId: String) -> Bool {
        lock.lock()
        defer { lock.unlock() }
        return browserEntries[agentPaneId] != nil
    }

    /// Remove all registrations for `agentPaneId`. Called when an agent pane closes.
    public func deregister(agentPaneId: String) {
        lock.lock()
        terminalEntries.removeValue(forKey: agentPaneId)
        browserEntries.removeValue(forKey: agentPaneId)
        lock.unlock()
    }

    /// Remove all registrations. Called on app quit.
    public func deregisterAll() {
        lock.lock()
        terminalEntries.removeAll()
        browserEntries.removeAll()
        lock.unlock()
    }

    // MARK: - Pane list

    /// Extra `MobileSessionSummary` entries for the relay pane-list payload.
    ///
    /// Returns the terminal pane and then the browser pane of every agent pane
    /// still present in `agentSessions`. Entries whose owning agent pane has
    /// closed (missing from `agentSessions`) are silently omitted, as are panes
    /// that `agentSessions` already carries, so a browser pane the Mac stores as
    /// its own `RunSession` is listed once rather than twice.
    ///
    /// - Parameters:
    ///   - agentSessions: The current RunSession list.
    ///   - revision: Revision number stamped on the new summaries.
    ///   - updatedAt: ISO-8601 timestamp for the new summaries.
    public func extraPaneSummaries(
        agentSessions: [RunSession],
        revision: Int,
        updatedAt: String
    ) -> [MobileSessionSummary] {
        lock.lock()
        let terminals = terminalEntries
        let browsers = browserEntries
        lock.unlock()

        var byId: [String: RunSession] = [:]
        for session in agentSessions { byId[session.id] = session }
        var result: [MobileSessionSummary] = []

        func append(_ entries: [String: IOPaneEntry], kind: String, paneId: (String) -> String, suffix: String) {
            for entry in entries.values.sorted(by: { $0.agentPaneId < $1.agentPaneId }) {
                guard let agent = byId[entry.agentPaneId] else { continue }
                let id = paneId(entry.agentPaneId)
                // The Mac may already hold this pane as a RunSession of its own;
                // the payload lists it once.
                guard byId[id] == nil else { continue }
                result.append(MobileSessionSummary(
                    id: id,
                    workspaceId: entry.workspaceId,
                    title: agent.title + " \u{2014} " + suffix,
                    kind: kind,
                    provider: entry.provider,
                    model: "default",
                    status: "idle",
                    revision: revision,
                    updatedAt: updatedAt,
                    terminal: true
                ))
            }
        }

        append(terminals, kind: AgentIOPaneKind.terminal, paneId: terminalPaneId(for:), suffix: L("agentTerminal.terminalPane.title"))
        append(browsers, kind: AgentIOPaneKind.browser, paneId: browserPaneId(for:), suffix: L("agentTerminal.browserPane.title"))

        return result
    }

    /// The ids `extraPaneSummaries` would return, in the same order.
    ///
    /// The publisher folds these into the pane order it compares against the
    /// last published one, so opening or closing an agent-owned pane bumps the
    /// state revision and the phone's list is re-sent.
    public func extraPaneIds(agentSessions: [RunSession]) -> [String] {
        extraPaneSummaries(agentSessions: agentSessions, revision: 0, updatedAt: "").map(\.id)
    }

    // MARK: - Helpers

    /// The stable pane id for the terminal pane owned by `agentPaneId`.
    public func terminalPaneId(for agentPaneId: String) -> String {
        AgentIOPaneKind.terminal + ":" + agentPaneId
    }

    /// The stable pane id for the browser pane owned by `agentPaneId`.
    public func browserPaneId(for agentPaneId: String) -> String {
        AgentIOPaneKind.browser + ":" + agentPaneId
    }
}
