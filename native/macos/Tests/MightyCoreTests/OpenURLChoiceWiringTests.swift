import Foundation
import Testing
@testable import MightyCore

// MARK: - Fakes

/// Virtual time; `onSleep` runs once, at the first poll, standing in for the
/// user acting while the dialog is up.
private final class DialogClock: WebOpenChoiceClock, @unchecked Sendable {
    private let lock = NSLock()
    private var current = Date(timeIntervalSince1970: 1_000_000)
    private var hook: (() -> Void)?
    private(set) var slept: TimeInterval = 0

    init(onFirstSleep: (() -> Void)? = nil) { hook = onFirstSleep }

    var now: Date { lock.withLock { current } }

    func sleep(for interval: TimeInterval) async {
        let action = lock.withLock { () -> (() -> Void)? in
            defer { hook = nil }
            return hook
        }
        action?()
        lock.withLock {
            current = current.addingTimeInterval(interval)
            slept += interval
        }
    }
}

/// The browser pane of one agent pane: records what it was navigated to.
private final class FakeBrowserPane: AgentBrowserPane, @unchecked Sendable {
    let agentPaneId: String
    private let lock = NSLock()
    private var urls: [URL] = []
    var shows = true
    var shown: [URL] { lock.withLock { urls } }

    init(agentPaneId: String) { self.agentPaneId = agentPaneId }

    func show(_ url: URL) async -> Bool {
        lock.withLock { urls.append(url) }
        return shows
    }
}

/// Makes browser panes and counts how many it made, or makes none when the
/// in-app browser is off.
private final class FakeBrowserPanes: @unchecked Sendable {
    private let lock = NSLock()
    private var made: [FakeBrowserPane] = []
    let available: Bool

    init(available: Bool = true) { self.available = available }

    var panes: [FakeBrowserPane] { lock.withLock { made } }

    func make(agentPaneId: String) async -> (any AgentBrowserPane)? {
        guard available else { return nil }
        let pane = FakeBrowserPane(agentPaneId: agentPaneId)
        lock.withLock { made.append(pane) }
        return pane
    }
}

/// Stands in for NSWorkspace.
private final class FakeSystemBrowser: ExternalURLOpener, @unchecked Sendable {
    private let lock = NSLock()
    private var urls: [URL] = []
    let accepts: Bool
    var opened: [URL] { lock.withLock { urls } }

    init(accepts: Bool = true) { self.accepts = accepts }

    func open(_ url: URL) async -> Bool {
        lock.withLock { urls.append(url) }
        return accepts
    }
}

/// The production opener over fakes, plus the service in front of it.
private struct Wiring {
    let store: WebOpenChoiceStore
    let prompts: WebOpenChoicePrompts
    let panes: FakeBrowserPanes
    let system: FakeSystemBrowser
    let registry: AgentIOPaneRegistry
    let opener: AgentWebOpener
    let service: WebOpenService

    init(store: WebOpenChoiceStore = WebOpenChoiceStore(), clock: any WebOpenChoiceClock = DialogClock(),
         inAppAvailable: Bool = true, systemAccepts: Bool = true, prompts: WebOpenChoicePrompts = WebOpenChoicePrompts()) {
        let panes = FakeBrowserPanes(available: inAppAvailable)
        let system = FakeSystemBrowser(accepts: systemAccepts)
        let registry = AgentIOPaneRegistry()
        let opener = AgentWebOpener(external: system) { agentPaneId, _ in await panes.make(agentPaneId: agentPaneId) }
        self.store = store
        self.prompts = prompts
        self.panes = panes
        self.system = system
        self.registry = registry
        self.opener = opener
        self.service = WebOpenService(store: store, presenter: prompts, opener: opener, clock: clock, paneRegistry: registry)
    }
}

private func freshDefaults() -> (defaults: UserDefaults, name: String) {
    let name = "mightyclaude-tests-web-open-" + UUID().uuidString
    return (UserDefaults(suiteName: name)!, name)
}

private let page = URL(string: "https://example.com/docs")!
private let other = URL(string: "http://localhost:3000/")!

// MARK: - The choice survives a restart

struct OpenURLChoicePersistenceTests {

    @Test func rememberedChoicesSurviveANewStoreInstance() {
        let (defaults, name) = freshDefaults(); defer { defaults.removePersistentDomain(forName: name) }
        let before = WebOpenChoiceStore(defaults: defaults)
        before.setChoice(.external, forWorkspace: "ws1")
        before.applySetting(.inApp, forWorkspace: "ws2")

        // What a restarted app sees.
        let after = WebOpenChoiceStore(defaults: defaults)
        #expect(after.choice(forWorkspace: "ws1") == .external)
        #expect(after.setting(forWorkspace: "ws2") == .inApp)
        #expect(after.setting(forWorkspace: "ws3") == .ask)

        // Back to "ask" is persisted too.
        after.applySetting(.ask, forWorkspace: "ws1")
        let again = WebOpenChoiceStore(defaults: defaults)
        #expect(again.choice(forWorkspace: "ws1") == nil)
        #expect(again.choice(forWorkspace: "ws2") == .inApp)
    }

    @Test func dialogRememberIsPersisted() async {
        let (defaults, name) = freshDefaults(); defer { defaults.removePersistentDomain(forName: name) }
        let prompts = WebOpenChoicePrompts()
        let clock = DialogClock { prompts.choose(prompts.pending[0].id, destination: .external, remember: true) }
        let wiring = Wiring(store: WebOpenChoiceStore(defaults: defaults), clock: clock, prompts: prompts)
        _ = await wiring.service.open(page.absoluteString, workspaceId: "ws", agentPaneId: "agent-1")
        #expect(WebOpenChoiceStore(defaults: defaults).choice(forWorkspace: "ws") == .external)
    }

    @Test func storedValuesThatAreNotAChoiceAreIgnored() {
        let (defaults, name) = freshDefaults(); defer { defaults.removePersistentDomain(forName: name) }
        defaults.set(["ws1": "sideways", "ws2": "in_app"], forKey: WebOpenChoiceStore.defaultsKey)
        let store = WebOpenChoiceStore(defaults: defaults)
        #expect(store.choice(forWorkspace: "ws1") == nil)
        #expect(store.choice(forWorkspace: "ws2") == .inApp)
    }

    @Test func aStoreWithoutDefaultsWritesNothing() {
        let (defaults, name) = freshDefaults(); defer { defaults.removePersistentDomain(forName: name) }
        WebOpenChoiceStore().setChoice(.external, forWorkspace: "ws")
        #expect(defaults.dictionary(forKey: WebOpenChoiceStore.defaultsKey) == nil)
        #expect(WebOpenChoiceStore.defaultsKey == "agentTerminal.webOpenChoices")
    }
}

// MARK: - Settings reaches panes that are already open

struct OpenURLChoiceSettingsTests {

    @Test func settingsChangeAppliesToAnOpenPanesNextOpen() async {
        let (defaults, name) = freshDefaults(); defer { defaults.removePersistentDomain(forName: name) }
        let store = WebOpenChoiceStore(defaults: defaults)
        store.applySetting(.inApp, forWorkspace: "ws-1")
        let wiring = Wiring(store: store)
        let handler = AgentTerminalIOHandler(processes: AgentProcessRegistry(), panes: wiring.registry, webOpen: wiring.service)
        let binding = testPaneBinding(pane: "agent-1", workspaceId: "ws-1")

        let first = await handler.handle(AgentIORequest(tool: "open_url", url: page.absoluteString), binding: binding)
        #expect(first.destination == WebOpenDestination.inApp.rawValue)
        #expect(wiring.panes.panes.first?.shown == [page])

        // The user flips the Settings entry while the agent pane stays open.
        store.applySetting(.external, forWorkspace: "ws-1")
        let second = await handler.handle(AgentIORequest(tool: "open_url", url: other.absoluteString), binding: binding)
        #expect(second.destination == WebOpenDestination.external.rawValue)
        #expect(wiring.system.opened == [other])
        #expect(wiring.panes.panes.first?.shown == [page])
        #expect(AgentIOMCPServer.describeOpen(second) == "Opened \(other.absoluteString) in the user's system browser.")
        #expect(wiring.prompts.pending.isEmpty)
    }
}

// MARK: - Where the page actually opens

struct OpenURLChoiceOpenerTests {

    @Test func inAppOpensGoToTheAgentPanesBrowserPaneAndReuseIt() async {
        let wiring = Wiring()
        wiring.store.setChoice(.inApp, forWorkspace: "ws")
        let first = await wiring.service.open(page.absoluteString, workspaceId: "ws", agentPaneId: "agent-1")
        let second = await wiring.service.open(other.absoluteString, workspaceId: "ws", agentPaneId: "agent-1")
        #expect(first == .opened(destination: .inApp, url: page))
        #expect(second == .opened(destination: .inApp, url: other))
        // One pane, made on the first open and navigated again by the second.
        #expect(wiring.panes.panes.count == 1)
        #expect(wiring.panes.panes[0].agentPaneId == "agent-1")
        #expect(wiring.panes.panes[0].shown == [page, other])
        #expect(wiring.system.opened.isEmpty)
        #expect(wiring.registry.hasBrowserPane(agentPaneId: "agent-1"))
    }

    @Test func eachAgentPaneGetsItsOwnBrowserPane() async {
        let wiring = Wiring()
        wiring.store.setChoice(.inApp, forWorkspace: "ws")
        _ = await wiring.service.open(page.absoluteString, workspaceId: "ws", agentPaneId: "agent-1")
        _ = await wiring.service.open(other.absoluteString, workspaceId: "ws", agentPaneId: "agent-2")
        #expect(wiring.panes.panes.map(\.agentPaneId) == ["agent-1", "agent-2"])
        #expect(wiring.panes.panes.map(\.shown) == [[page], [other]])
    }

    @Test func concurrentFirstOpensMakeOnePane() async {
        let wiring = Wiring()
        await withTaskGroup(of: Bool.self) { group in
            for _ in 0 ..< 8 { group.addTask { await wiring.opener.openInApp(page, agentPaneId: "agent-1", workspaceId: "ws") } }
            for await _ in group {}
        }
        #expect(wiring.panes.panes.count == 1)
        #expect(wiring.panes.panes[0].shown.count == 8)
    }

    @Test func forgottenPaneIsMadeAgainOnTheNextOpen() async {
        let wiring = Wiring()
        #expect(await wiring.opener.openInApp(page, agentPaneId: "agent-1", workspaceId: "ws"))
        await wiring.opener.forgetPane(agentPaneId: "agent-1")
        #expect(await wiring.opener.openInApp(other, agentPaneId: "agent-1", workspaceId: "ws"))
        #expect(wiring.panes.panes.count == 2)
        #expect(wiring.panes.panes[1].shown == [other])
    }

    @Test func externalOpensGoToTheSystemBrowserOpener() async {
        let wiring = Wiring()
        wiring.store.setChoice(.external, forWorkspace: "ws")
        let result = await wiring.service.open(page.absoluteString, workspaceId: "ws", agentPaneId: "agent-1")
        #expect(result == .opened(destination: .external, url: page))
        #expect(wiring.system.opened == [page])
        #expect(wiring.panes.panes.isEmpty)
        #expect(!wiring.registry.hasBrowserPane(agentPaneId: "agent-1"))
    }

    @Test func inAppWithoutTheBrowserEngineOpensInTheSystemBrowserAndSaysSo() async {
        let wiring = Wiring(inAppAvailable: false)
        wiring.store.setChoice(.inApp, forWorkspace: "ws-1")
        let handler = AgentTerminalIOHandler(processes: AgentProcessRegistry(), panes: wiring.registry, webOpen: wiring.service)
        let response = await handler.handle(AgentIORequest(tool: "open_url", url: page.absoluteString), binding: testPaneBinding(pane: "agent-1", workspaceId: "ws-1"))
        #expect(response.error == nil)
        #expect(response.destination == WebOpenDestination.external.rawValue)
        #expect(response.inAppUnavailable == true)
        #expect(wiring.system.opened == [page])
        #expect(!wiring.registry.hasBrowserPane(agentPaneId: "agent-1"))
        let text = AgentIOMCPServer.describeOpen(response)
        #expect(text.hasPrefix("Opened \(page.absoluteString) in the user's system browser."))
        #expect(text.contains("could not show it"))
        // With no pane made, a later open tries again.
        #expect(await wiring.service.open(page.absoluteString, workspaceId: "ws-1", agentPaneId: "agent-1") == .openedExternallyInstead(url: page))
    }

    @Test func inAppToolTextNamesTheBrowserPane() async {
        let wiring = Wiring()
        wiring.store.setChoice(.inApp, forWorkspace: "ws-1")
        let handler = AgentTerminalIOHandler(processes: AgentProcessRegistry(), panes: wiring.registry, webOpen: wiring.service)
        let response = await handler.handle(AgentIORequest(tool: "open_url", url: page.absoluteString), binding: testPaneBinding(pane: "agent-1", workspaceId: "ws-1"))
        #expect(AgentIOMCPServer.describeOpen(response) == "Opened \(page.absoluteString) in the MightyClaude browser pane next to this agent pane.")
    }

    @Test func aPageNothingAcceptsIsAnErrorForTheAgent() async {
        let wiring = Wiring(inAppAvailable: false, systemAccepts: false)
        wiring.store.setChoice(.external, forWorkspace: "ws-1")
        #expect(await wiring.service.open(page.absoluteString, workspaceId: "ws-1", agentPaneId: "agent-1") == .failed(url: page))
        let handler = AgentTerminalIOHandler(processes: AgentProcessRegistry(), panes: wiring.registry, webOpen: wiring.service)
        let response = await handler.handle(AgentIORequest(tool: "open_url", url: page.absoluteString), binding: testPaneBinding(pane: "agent-1", workspaceId: "ws-1"))
        #expect(response.error?.contains("could not be opened") == true)
    }

    @Test func rejectedURLsReachNeitherOpener() async {
        let wiring = Wiring()
        wiring.store.setChoice(.inApp, forWorkspace: "ws")
        for raw in ["file:///etc/passwd", "javascript:alert(1)", ""] {
            guard case .rejected = await wiring.service.open(raw, workspaceId: "ws", agentPaneId: "agent-1") else {
                Issue.record("\(raw) should have been rejected")
                continue
            }
        }
        #expect(wiring.panes.panes.isEmpty)
        #expect(wiring.system.opened.isEmpty)
    }
}

// MARK: - The dialog's view model

struct OpenURLChoiceDialogTests {

    @Test func aClickResolvesTheRequestAndRememberStoresTheChoice() async {
        let prompts = WebOpenChoicePrompts()
        var seen: [WebOpenPromptRequest] = []
        let clock = DialogClock {
            seen = prompts.pending
            prompts.choose(prompts.pending[0].id, destination: .external, remember: true)
        }
        let wiring = Wiring(clock: clock, prompts: prompts)
        let result = await wiring.service.open(page.absoluteString, workspaceId: "ws", agentPaneId: "agent-1")
        #expect(result == .opened(destination: .external, url: page))
        #expect(wiring.system.opened == [page])
        // The dialog showed this request in the pane that asked.
        #expect(seen.count == 1)
        #expect(seen.first?.url == page && seen.first?.workspaceId == "ws" && seen.first?.agentPaneId == "agent-1")
        #expect(seen.first?.timeoutSeconds == 30)
        // Answered and taken down, well before the fallback.
        #expect(prompts.pending.isEmpty)
        #expect(prompts.answer(for: seen[0].id) == nil)
        #expect(clock.slept < 1)
        #expect(wiring.store.choice(forWorkspace: "ws") == .external)
    }

    @Test func aClickWithoutRememberDoesNotStoreTheChoice() async {
        let prompts = WebOpenChoicePrompts()
        let clock = DialogClock { prompts.choose(prompts.pending[0].id, destination: .inApp, remember: false) }
        let wiring = Wiring(clock: clock, prompts: prompts)
        let result = await wiring.service.open(page.absoluteString, workspaceId: "ws", agentPaneId: "agent-1")
        #expect(result == .opened(destination: .inApp, url: page))
        #expect(wiring.panes.panes.first?.shown == [page])
        #expect(prompts.pending.isEmpty)
        #expect(wiring.store.choice(forWorkspace: "ws") == nil)
    }

    @Test func theTimeoutResolvesTheRequestInAppAndTakesTheDialogDown() async {
        let changes = ChangeCounter()
        let prompts = WebOpenChoicePrompts { changes.bump() }
        var shownDuringWait = 0
        let clock = DialogClock { shownDuringWait = prompts.pending.count }
        let wiring = Wiring(clock: clock, prompts: prompts)
        let result = await wiring.service.open(page.absoluteString, workspaceId: "ws", agentPaneId: "agent-1")
        #expect(shownDuringWait == 1)
        #expect(result == .opened(destination: .inApp, url: page))
        #expect(wiring.panes.panes.first?.shown == [page])
        #expect(clock.slept >= 30 && clock.slept < 31)
        // The fallback dismissed the dialog: the UI has nothing left to show.
        #expect(prompts.pending.isEmpty)
        #expect(changes.count == 2) // shown, then taken down
        #expect(wiring.store.choice(forWorkspace: "ws") == nil)
    }

    @Test func aLateClickAfterTheTimeoutDoesNothing() async {
        let prompts = WebOpenChoicePrompts()
        var requestId = ""
        let clock = DialogClock { requestId = prompts.pending[0].id }
        let wiring = Wiring(clock: clock, prompts: prompts)
        _ = await wiring.service.open(page.absoluteString, workspaceId: "ws", agentPaneId: "agent-1")
        prompts.choose(requestId, destination: .external, remember: true)
        #expect(prompts.answer(for: requestId) == nil)
        #expect(prompts.pending.isEmpty)
        #expect(wiring.store.choice(forWorkspace: "ws") == nil)
    }

    @Test func twoWaitingRequestsAreAnsweredSeparately() {
        let prompts = WebOpenChoicePrompts()
        let a = WebOpenPromptRequest(url: page, workspaceId: "ws", agentPaneId: "agent-1", timeoutSeconds: 30)
        let b = WebOpenPromptRequest(url: other, workspaceId: "ws", agentPaneId: "agent-2", timeoutSeconds: 30)
        prompts.present(a)
        prompts.present(b)
        #expect(prompts.pending.map(\.agentPaneId) == ["agent-1", "agent-2"])
        prompts.choose(b.id, destination: .external, remember: false)
        #expect(prompts.answer(for: b.id) == WebOpenPromptAnswer(destination: .external, remember: false))
        #expect(prompts.answer(for: a.id) == nil)
        #expect(prompts.pending.map(\.id) == [a.id])
        prompts.dismiss(b.id)
        #expect(prompts.answer(for: b.id) == nil)
    }
}

private final class ChangeCounter: @unchecked Sendable {
    private let lock = NSLock()
    private var value = 0
    var count: Int { lock.withLock { value } }
    func bump() { lock.withLock { value += 1 } }
}
