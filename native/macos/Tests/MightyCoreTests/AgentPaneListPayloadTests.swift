import Foundation
import Testing
@testable import MightyCore

struct AgentPaneListPayloadTests {

    private func makeSession(id: String, workspaceId: String = "ws-1", title: String = "My Agent", provider: String = "claude") -> RunSession {
        RunSession(id: id, workspaceId: workspaceId, title: title, kind: "claude", provider: provider)
    }

    // MARK: - Terminal pane registration

    @Test func registerTerminalPaneReturnsDeterministicId() {
        let registry = AgentIOPaneRegistry()
        let paneId = registry.registerTerminalPane(agentPaneId: "agent-1", workspaceId: "ws-1", provider: "claude")
        #expect(paneId == "agent-terminal:agent-1")
    }

    @Test func registerTerminalPaneSetsHasTerminalPane() {
        let registry = AgentIOPaneRegistry()
        #expect(!registry.hasTerminalPane(agentPaneId: "agent-1"))
        registry.registerTerminalPane(agentPaneId: "agent-1", workspaceId: "ws-1", provider: "claude")
        #expect(registry.hasTerminalPane(agentPaneId: "agent-1"))
    }

    @Test func registerIsIdempotent() {
        let registry = AgentIOPaneRegistry()
        registry.registerTerminalPane(agentPaneId: "agent-1", workspaceId: "ws-1", provider: "claude")
        registry.registerTerminalPane(agentPaneId: "agent-1", workspaceId: "ws-1", provider: "claude")
        #expect(registry.hasTerminalPane(agentPaneId: "agent-1"))
    }

    // MARK: - extraPaneSummaries includes the terminal pane

    @Test func terminalPaneAppearsInExtraSummaries() {
        let registry = AgentIOPaneRegistry()
        registry.registerTerminalPane(agentPaneId: "agent-1", workspaceId: "ws-1", provider: "claude")
        let session = makeSession(id: "agent-1", workspaceId: "ws-1", title: "My Agent", provider: "claude")
        let summaries = registry.extraPaneSummaries(agentSessions: [session], revision: 1, updatedAt: "2026-09-27T00:00:00Z")
        #expect(summaries.count == 1)
        let summary = try! #require(summaries.first)
        #expect(summary.id == "agent-terminal:agent-1")
        #expect(summary.kind == AgentIOPaneKind.terminal)
        #expect(summary.workspaceId == "ws-1")
        #expect(summary.provider == "claude")
        #expect(summary.revision == 1)
        #expect(summary.title.contains("My Agent"))
    }

    @Test func multiplePanesProduceMultipleSummaries() {
        let registry = AgentIOPaneRegistry()
        registry.registerTerminalPane(agentPaneId: "agent-1", workspaceId: "ws-1", provider: "claude")
        registry.registerTerminalPane(agentPaneId: "agent-2", workspaceId: "ws-1", provider: "codex")
        let sessions = [
            makeSession(id: "agent-1", workspaceId: "ws-1", provider: "claude"),
            makeSession(id: "agent-2", workspaceId: "ws-1", provider: "codex"),
        ]
        let summaries = registry.extraPaneSummaries(agentSessions: sessions, revision: 2, updatedAt: "2026-09-27T00:00:00Z")
        #expect(summaries.count == 2)
        let ids = Set(summaries.map(\.id))
        #expect(ids.contains("agent-terminal:agent-1"))
        #expect(ids.contains("agent-terminal:agent-2"))
    }

    @Test func closedAgentPaneIsOmittedFromSummaries() {
        let registry = AgentIOPaneRegistry()
        registry.registerTerminalPane(agentPaneId: "agent-1", workspaceId: "ws-1", provider: "claude")
        // Agent session is gone (pane closed before deregister ran)
        let summaries = registry.extraPaneSummaries(agentSessions: [], revision: 1, updatedAt: "2026-09-27T00:00:00Z")
        #expect(summaries.isEmpty)
    }

    // MARK: - Deregistration

    @Test func deregisterRemovesTerminalEntry() {
        let registry = AgentIOPaneRegistry()
        registry.registerTerminalPane(agentPaneId: "agent-1", workspaceId: "ws-1", provider: "claude")
        registry.deregister(agentPaneId: "agent-1")
        #expect(!registry.hasTerminalPane(agentPaneId: "agent-1"))
        let session = makeSession(id: "agent-1")
        let summaries = registry.extraPaneSummaries(agentSessions: [session], revision: 1, updatedAt: "2026-09-27T00:00:00Z")
        #expect(summaries.isEmpty)
    }

    @Test func deregisterAllClearsRegistry() {
        let registry = AgentIOPaneRegistry()
        registry.registerTerminalPane(agentPaneId: "agent-1", workspaceId: "ws-1", provider: "claude")
        registry.registerTerminalPane(agentPaneId: "agent-2", workspaceId: "ws-1", provider: "codex")
        registry.deregisterAll()
        #expect(!registry.hasTerminalPane(agentPaneId: "agent-1"))
        #expect(!registry.hasTerminalPane(agentPaneId: "agent-2"))
    }

    // MARK: - The agent-opened browser pane

    @Test func registerBrowserPaneReturnsDeterministicId() {
        let registry = AgentIOPaneRegistry()
        let paneId = registry.registerBrowserPane(agentPaneId: "agent-1", workspaceId: "ws-1", provider: "claude")
        #expect(paneId == "agent-browser:agent-1")
        #expect(registry.hasBrowserPane(agentPaneId: "agent-1"))
    }

    @Test func browserPaneAppearsInExtraSummaries() {
        let registry = AgentIOPaneRegistry()
        registry.registerBrowserPane(agentPaneId: "agent-1", workspaceId: "ws-1", provider: "codex")
        let session = makeSession(id: "agent-1", workspaceId: "ws-1", title: "My Agent", provider: "codex")
        let summaries = registry.extraPaneSummaries(agentSessions: [session], revision: 3, updatedAt: "2026-09-27T00:00:00Z")
        #expect(summaries.count == 1)
        let summary = try! #require(summaries.first)
        #expect(summary.id == "agent-browser:agent-1")
        #expect(summary.kind == AgentIOPaneKind.browser)
        #expect(summary.workspaceId == "ws-1")
        #expect(summary.provider == "codex")
        #expect(summary.revision == 3)
        #expect(summary.title.contains("My Agent"))
    }

    /// Both IO panes of one agent pane ride in the same payload, beside the
    /// agent pane itself.
    @Test func terminalAndBrowserPanesRideTogether() {
        let registry = AgentIOPaneRegistry()
        registry.registerTerminalPane(agentPaneId: "agent-1", workspaceId: "ws-1", provider: "claude")
        registry.registerBrowserPane(agentPaneId: "agent-1", workspaceId: "ws-1", provider: "claude")
        let session = makeSession(id: "agent-1", workspaceId: "ws-1")
        let summaries = registry.extraPaneSummaries(agentSessions: [session], revision: 1, updatedAt: "2026-09-27T00:00:00Z")
        let ids = Set(summaries.map(\.id))
        #expect(ids == ["agent-terminal:agent-1", "agent-browser:agent-1"])
        let kinds = Set(summaries.map(\.kind))
        #expect(kinds == [AgentIOPaneKind.terminal, AgentIOPaneKind.browser])
    }

    /// The phone lists these panes but may not command them, so the payload
    /// marks them the way it marks the Mac's own terminal panes.
    @Test func ioPanesAreMarkedNonCommandable() {
        let registry = AgentIOPaneRegistry()
        registry.registerTerminalPane(agentPaneId: "agent-1", workspaceId: "ws-1", provider: "claude")
        registry.registerBrowserPane(agentPaneId: "agent-1", workspaceId: "ws-1", provider: "claude")
        let summaries = registry.extraPaneSummaries(agentSessions: [makeSession(id: "agent-1")], revision: 1, updatedAt: "2026-09-27T00:00:00Z")
        #expect(summaries.count == 2)
        let nonCommandable = summaries.filter { $0.terminal }
        #expect(nonCommandable.count == 2)
    }

    @Test func closedAgentPaneOmitsItsBrowserPane() {
        let registry = AgentIOPaneRegistry()
        registry.registerBrowserPane(agentPaneId: "agent-1", workspaceId: "ws-1", provider: "claude")
        #expect(registry.extraPaneSummaries(agentSessions: [], revision: 1, updatedAt: "2026-09-27T00:00:00Z").isEmpty)
    }

    @Test func deregisterRemovesBothIOPanes() {
        let registry = AgentIOPaneRegistry()
        registry.registerTerminalPane(agentPaneId: "agent-1", workspaceId: "ws-1", provider: "claude")
        registry.registerBrowserPane(agentPaneId: "agent-1", workspaceId: "ws-1", provider: "claude")
        registry.deregister(agentPaneId: "agent-1")
        #expect(!registry.hasTerminalPane(agentPaneId: "agent-1"))
        #expect(!registry.hasBrowserPane(agentPaneId: "agent-1"))
        #expect(registry.extraPaneSummaries(agentSessions: [makeSession(id: "agent-1")], revision: 1, updatedAt: "2026-09-27T00:00:00Z").isEmpty)
    }

    @Test func deregisterAllClearsBrowserPanesToo() {
        let registry = AgentIOPaneRegistry()
        registry.registerBrowserPane(agentPaneId: "agent-1", workspaceId: "ws-1", provider: "claude")
        registry.deregisterAll()
        #expect(!registry.hasBrowserPane(agentPaneId: "agent-1"))
    }

    /// When the Mac already stores the browser pane as a RunSession of its own
    /// (kind "browser", ownerSessionId set), the payload lists it once.
    @Test func browserPaneHeldAsRunSessionIsNotDuplicated() {
        let registry = AgentIOPaneRegistry()
        registry.registerBrowserPane(agentPaneId: "agent-1", workspaceId: "ws-1", provider: "claude")
        var browserSession = RunSession(id: registry.browserPaneId(for: "agent-1"), workspaceId: "ws-1", title: "Browser", kind: "browser", provider: "claude")
        browserSession.ownerSessionId = "agent-1"
        let summaries = registry.extraPaneSummaries(agentSessions: [makeSession(id: "agent-1"), browserSession],
                                                    revision: 1, updatedAt: "2026-09-27T00:00:00Z")
        #expect(summaries.isEmpty)
        #expect(browserSession.ownerSessionId == "agent-1")
    }

    /// The publisher compares this against the last published pane order, so it
    /// must name exactly the panes the payload carries.
    @Test func extraPaneIdsMatchTheSummaries() {
        let registry = AgentIOPaneRegistry()
        registry.registerTerminalPane(agentPaneId: "agent-1", workspaceId: "ws-1", provider: "claude")
        registry.registerBrowserPane(agentPaneId: "agent-1", workspaceId: "ws-1", provider: "claude")
        let sessions = [makeSession(id: "agent-1")]
        let ids = registry.extraPaneIds(agentSessions: sessions)
        let summaryIds = registry.extraPaneSummaries(agentSessions: sessions, revision: 1, updatedAt: "2026-09-27T00:00:00Z").map(\.id)
        #expect(ids == summaryIds)
        #expect(ids == ["agent-terminal:agent-1", "agent-browser:agent-1"])
        #expect(registry.extraPaneIds(agentSessions: []).isEmpty)
    }

    // MARK: - An in-app open registers the pane it opened into

    private final class NoopOpener: WebOpener, @unchecked Sendable {
        func openInApp(_ url: URL, agentPaneId: String?, workspaceId: String) async -> Bool { true }
        func openExternally(_ url: URL) async -> Bool { true }
    }

    private final class NeverAsked: WebOpenPromptPresenter, @unchecked Sendable {
        func present(_ request: WebOpenPromptRequest) {}
        func answer(for id: String) -> WebOpenPromptAnswer? { nil }
        func dismiss(_ id: String) {}
    }

    @Test func inAppOpenRegistersTheBrowserPane() async {
        let registry = AgentIOPaneRegistry()
        let store = WebOpenChoiceStore()
        store.setChoice(.inApp, forWorkspace: "ws-1")
        let service = WebOpenService(store: store, presenter: NeverAsked(), opener: NoopOpener(), paneRegistry: registry)
        _ = await service.open("https://example.com", workspaceId: "ws-1", agentPaneId: "agent-1", provider: "claude")
        #expect(registry.hasBrowserPane(agentPaneId: "agent-1"))
        let summaries = registry.extraPaneSummaries(agentSessions: [makeSession(id: "agent-1")], revision: 1, updatedAt: "2026-09-27T00:00:00Z")
        #expect(summaries.map(\.id) == ["agent-browser:agent-1"])
    }

    @Test func externalOpenRegistersNoPane() async {
        let registry = AgentIOPaneRegistry()
        let store = WebOpenChoiceStore()
        store.setChoice(.external, forWorkspace: "ws-1")
        let service = WebOpenService(store: store, presenter: NeverAsked(), opener: NoopOpener(), paneRegistry: registry)
        _ = await service.open("https://example.com", workspaceId: "ws-1", agentPaneId: "agent-1", provider: "claude")
        #expect(!registry.hasBrowserPane(agentPaneId: "agent-1"))
    }

    // MARK: - The agent-browser pane the Mac holds as its own pane

    /// Both agent IO kinds are view-only on the phone, so the Mac marks its own
    /// agent-terminal and agent-browser panes `terminal: true`, like the extras.
    @Test func agentIOKindsAreRecognised() {
        #expect(AgentIOPaneKind.isAgentIOPane(AgentIOPaneKind.terminal))
        #expect(AgentIOPaneKind.isAgentIOPane(AgentIOPaneKind.browser))
        for kind in ["claude", "shell", "browser", ""] { #expect(!AgentIOPaneKind.isAgentIOPane(kind)) }
    }

    /// Once the Mac shows the agent-browser pane as a `RunSession`, the payload
    /// lists it once, as that session, and not again as an extra.
    @Test func agentBrowserPaneHeldAsRunSessionIsListedOnce() {
        let registry = AgentIOPaneRegistry()
        registry.registerTerminalPane(agentPaneId: "agent-1", workspaceId: "ws-1", provider: "claude")
        registry.registerBrowserPane(agentPaneId: "agent-1", workspaceId: "ws-1", provider: "claude")
        var browserPane = RunSession(id: registry.browserPaneId(for: "agent-1"), workspaceId: "ws-1", title: "My Agent \u{2014} Browser", kind: AgentIOPaneKind.browser, provider: "claude")
        browserPane.ownerSessionId = "agent-1"
        let sessions = [makeSession(id: "agent-1"), browserPane]
        let extras = registry.extraPaneSummaries(agentSessions: sessions, revision: 1, updatedAt: "2026-09-27T00:00:00Z")
        #expect(extras.map(\.id) == ["agent-terminal:agent-1"])
        #expect(registry.extraPaneIds(agentSessions: sessions) == ["agent-terminal:agent-1"])
        // The user closes the browser pane: the phone still lists it beside its
        // agent pane, as it does a closed terminal pane, until the agent pane goes.
        let closed = registry.extraPaneSummaries(agentSessions: [makeSession(id: "agent-1")], revision: 2, updatedAt: "2026-09-27T00:00:00Z")
        #expect(closed.map(\.id) == ["agent-terminal:agent-1", "agent-browser:agent-1"])
        #expect(closed.filter(\.terminal).count == 2)
    }

    @Test func browserPaneExtraCarriesTheBrowserPaneTitle() {
        let registry = AgentIOPaneRegistry()
        registry.registerBrowserPane(agentPaneId: "agent-1", workspaceId: "ws-1", provider: "claude")
        let summary = registry.extraPaneSummaries(agentSessions: [makeSession(id: "agent-1", title: "Fixer")], revision: 1, updatedAt: "2026-09-27T00:00:00Z").first
        #expect(summary?.title == "Fixer \u{2014} " + L("agentTerminal.browserPane.title"))
    }

    // MARK: - Registration through the production opener

    private final class ShownPane: AgentBrowserPane, @unchecked Sendable {
        func show(_ url: URL) async -> Bool { true }
    }

    private struct AcceptingSystemBrowser: ExternalURLOpener {
        func open(_ url: URL) async -> Bool { true }
    }

    @Test func inAppOpenIntoTheAgentBrowserPaneRegistersItOnce() async {
        let registry = AgentIOPaneRegistry()
        let store = WebOpenChoiceStore()
        store.setChoice(.inApp, forWorkspace: "ws-1")
        let opener = AgentWebOpener(external: AcceptingSystemBrowser()) { _, _ in ShownPane() }
        let service = WebOpenService(store: store, presenter: NeverAsked(), opener: opener, paneRegistry: registry)
        _ = await service.open("https://example.com", workspaceId: "ws-1", agentPaneId: "agent-1", provider: "codex")
        _ = await service.open("https://example.com/2", workspaceId: "ws-1", agentPaneId: "agent-1", provider: "codex")
        let summaries = registry.extraPaneSummaries(agentSessions: [makeSession(id: "agent-1", provider: "codex")], revision: 1, updatedAt: "2026-09-27T00:00:00Z")
        #expect(summaries.map(\.id) == ["agent-browser:agent-1"])
        #expect(summaries.first?.kind == AgentIOPaneKind.browser)
        #expect(summaries.first?.provider == "codex")
    }

    /// With the in-app browser off the page opens in the system browser, so no
    /// browser pane exists to list.
    @Test func inAppOpenWithoutTheBrowserEngineRegistersNoPane() async {
        let registry = AgentIOPaneRegistry()
        let store = WebOpenChoiceStore()
        store.setChoice(.inApp, forWorkspace: "ws-1")
        let opener = AgentWebOpener(external: AcceptingSystemBrowser()) { _, _ in nil }
        let service = WebOpenService(store: store, presenter: NeverAsked(), opener: opener, paneRegistry: registry)
        let result = await service.open("https://example.com", workspaceId: "ws-1", agentPaneId: "agent-1", provider: "claude")
        #expect(result == .openedExternallyInstead(url: URL(string: "https://example.com")!))
        #expect(!registry.hasBrowserPane(agentPaneId: "agent-1"))
    }
}
