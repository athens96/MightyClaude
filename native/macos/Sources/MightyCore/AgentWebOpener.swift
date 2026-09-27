import Foundation

/// The one in-app browser pane an agent pane opens pages into. The app's pane
/// is a CEF browser pane placed next to the agent pane; tests use a fake.
public protocol AgentBrowserPane: AnyObject, Sendable {
    /// Navigate the pane to `url` and put it back on screen if the user closed
    /// it. Returns false when it can no longer be shown (the agent pane is gone).
    func show(_ url: URL) async -> Bool
}

/// Hands a URL to the system's default browser. The app uses NSWorkspace.
public protocol ExternalURLOpener: Sendable {
    /// Returns false when the system refused to open `url`.
    func open(_ url: URL) async -> Bool
}

/// The production ``WebOpener``: in-app opens go to the asking agent pane's own
/// browser pane, external opens go to the system browser.
///
/// The browser pane is made on an agent pane's first in-app open and reused
/// for every later one, so a second open navigates the same pane. `makePane`
/// returns nil when the in-app browser cannot show pages (engine turned off in
/// Settings, or missing from this build); the open then reports false and the
/// service opens the page in the system browser instead.
public actor AgentWebOpener: WebOpener {
    public typealias PaneFactory = @Sendable (_ agentPaneId: String, _ workspaceId: String) async -> (any AgentBrowserPane)?

    private let external: any ExternalURLOpener
    private let makePane: PaneFactory
    /// One pending or made pane per agent pane. Holding the task, not the
    /// pane, keeps two concurrent first opens from making two panes.
    private var panes: [String: Task<(any AgentBrowserPane)?, Never>] = [:]

    public init(external: any ExternalURLOpener, makePane: @escaping PaneFactory) {
        self.external = external
        self.makePane = makePane
    }

    public func openInApp(_ url: URL, agentPaneId: String?, workspaceId: String) async -> Bool {
        guard let agentPaneId else { return false }
        let task: Task<(any AgentBrowserPane)?, Never>
        if let existing = panes[agentPaneId] {
            task = existing
        } else {
            let makePane = makePane
            task = Task { await makePane(agentPaneId, workspaceId) }
            panes[agentPaneId] = task
        }
        guard let pane = await task.value else {
            // Nothing was made; a later open may try again.
            if panes[agentPaneId] == task { panes[agentPaneId] = nil }
            return false
        }
        return await pane.show(url)
    }

    public func openExternally(_ url: URL) async -> Bool {
        await external.open(url)
    }

    /// Forget the agent pane's browser pane once the app has let go of it, so
    /// the next in-app open makes a new one.
    public func forgetPane(agentPaneId: String) {
        panes[agentPaneId] = nil
    }
}
