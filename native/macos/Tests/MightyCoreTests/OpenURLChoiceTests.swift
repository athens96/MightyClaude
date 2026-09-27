import Foundation
import Testing
@testable import MightyCore

// MARK: - Fakes

/// Clock the tests drive: `sleep(for:)` advances virtual time and returns at once,
/// so the 30-second fallback is exercised without waiting for real time.
private final class FakeWebOpenClock: WebOpenChoiceClock, @unchecked Sendable {
    private let lock = NSLock()
    private var current: Date
    private(set) var slept: TimeInterval = 0

    init(start: Date = Date(timeIntervalSince1970: 1_000_000)) { current = start }

    var now: Date {
        lock.lock(); defer { lock.unlock() }
        return current
    }

    func sleep(for interval: TimeInterval) async {
        lock.lock()
        current = current.addingTimeInterval(interval)
        slept += interval
        lock.unlock()
    }
}

/// Dialog stand-in. `answer` is what the user taps; nil means they never answer.
private final class FakeWebOpenPrompt: WebOpenPromptPresenter, @unchecked Sendable {
    private let lock = NSLock()
    private var answer: (destination: WebOpenDestination, remember: Bool)?
    private(set) var presentedURLs: [URL] = []
    private(set) var presentedWorkspaces: [String] = []
    private(set) var dismissCount = 0

    init(answer: (destination: WebOpenDestination, remember: Bool)? = nil) { self.answer = answer }

    var presentCount: Int {
        lock.lock(); defer { lock.unlock() }
        return presentedURLs.count
    }

    func present(url: URL, workspaceId: String) {
        lock.lock()
        presentedURLs.append(url)
        presentedWorkspaces.append(workspaceId)
        lock.unlock()
    }

    func pendingChoice() -> (destination: WebOpenDestination, remember: Bool)? {
        lock.lock(); defer { lock.unlock() }
        return answer
    }

    func dismiss() {
        lock.lock()
        dismissCount += 1
        lock.unlock()
    }
}

/// Records what actually got opened, so "nothing opens" can be asserted.
private final class FakeWebOpener: WebOpener, @unchecked Sendable {
    private let lock = NSLock()
    private(set) var inApp: [URL] = []
    private(set) var external: [URL] = []

    var openedCount: Int {
        lock.lock(); defer { lock.unlock() }
        return inApp.count + external.count
    }

    func openInApp(_ url: URL) async {
        lock.lock(); inApp.append(url); lock.unlock()
    }

    func openExternally(_ url: URL) async {
        lock.lock(); external.append(url); lock.unlock()
    }
}

private struct Rig {
    let store: WebOpenChoiceStore
    let prompt: FakeWebOpenPrompt
    let opener: FakeWebOpener
    let clock: FakeWebOpenClock
    let service: WebOpenService

    init(answer: (destination: WebOpenDestination, remember: Bool)? = nil,
         timeout: TimeInterval = WebOpenService.defaultPromptTimeoutSeconds) {
        let store = WebOpenChoiceStore()
        let prompt = FakeWebOpenPrompt(answer: answer)
        let opener = FakeWebOpener()
        let clock = FakeWebOpenClock()
        self.store = store
        self.prompt = prompt
        self.opener = opener
        self.clock = clock
        self.service = WebOpenService(store: store, presenter: prompt, opener: opener,
                                      clock: clock, promptTimeoutSeconds: timeout)
    }
}

// MARK: - URL validation: only http and https, nothing else opens

struct OpenURLChoiceValidationTests {

    @Test func emptyURLRejected() {
        #expect(WebOpenURLValidator.validate("") == .failure(.empty))
        #expect(WebOpenURLValidator.validate("   ") == .failure(.empty))
    }

    @Test func tooLongURLRejected() {
        let long = "https://x.co/" + String(repeating: "a", count: WebOpenURLValidator.maxLength)
        #expect(WebOpenURLValidator.validate(long) == .failure(.tooLong(long.count)))
        #expect(WebOpenURLValidator.maxLength == 8_192)
    }

    @Test func urlAtExactMaxLengthAccepted() {
        let base = "https://x.co/"
        let pad = String(repeating: "a", count: WebOpenURLValidator.maxLength - base.count)
        let raw = base + pad
        #expect(raw.count == WebOpenURLValidator.maxLength)
        guard case .success = WebOpenURLValidator.validate(raw) else {
            Issue.record("URL at exactly the max length should be accepted")
            return
        }
    }

    @Test func httpAndHttpsAccepted() {
        #expect(WebOpenURLValidator.validate("http://example.com") == .success(URL(string: "http://example.com")!))
        #expect(WebOpenURLValidator.validate("https://example.com/a?b=c") == .success(URL(string: "https://example.com/a?b=c")!))
    }

    @Test func localhostAccepted() {
        guard case .success = WebOpenURLValidator.validate("http://localhost:3000/panel") else {
            Issue.record("localhost http should be accepted")
            return
        }
    }

    @Test func otherSchemesRejected() {
        for raw in ["file:///usr/bin", "javascript:alert(1)", "data:text/html,<b>x</b>",
                    "mailto:a@b.co", "ftp://example.com", "example.com", "://nope"] {
            guard case .failure(.disallowedScheme) = WebOpenURLValidator.validate(raw) else {
                Issue.record("\(raw) must be rejected")
                continue
            }
        }
    }

    @Test func rejectionCarriesAMessage() {
        #expect(!WebOpenURLValidator.message(for: .empty).isEmpty)
        #expect(!WebOpenURLValidator.message(for: .tooLong(9_000)).isEmpty)
        #expect(!WebOpenURLValidator.message(for: .disallowedScheme("file")).isEmpty)
    }
}

// MARK: - The per-workspace store behind Settings

struct OpenURLChoiceStoreTests {

    @Test func unsetWorkspaceAsksEveryTime() {
        let store = WebOpenChoiceStore()
        #expect(store.choice(forWorkspace: "ws") == nil)
        #expect(store.setting(forWorkspace: "ws") == .ask)
    }

    @Test func choiceIsPerWorkspaceAndOverridable() {
        let store = WebOpenChoiceStore()
        store.setChoice(.inApp, forWorkspace: "ws1")
        store.setChoice(.external, forWorkspace: "ws2")
        #expect(store.choice(forWorkspace: "ws1") == .inApp)
        #expect(store.choice(forWorkspace: "ws2") == .external)
        store.setChoice(.external, forWorkspace: "ws1")
        #expect(store.choice(forWorkspace: "ws1") == .external)
        store.clearChoice(forWorkspace: "ws1")
        #expect(store.choice(forWorkspace: "ws1") == nil)
        #expect(store.choice(forWorkspace: "ws2") == .external)
    }

    @Test func settingsEntryRoundTripsAndAskForgets() {
        let store = WebOpenChoiceStore()
        store.applySetting(.external, forWorkspace: "ws")
        #expect(store.setting(forWorkspace: "ws") == .external)
        store.applySetting(.inApp, forWorkspace: "ws")
        #expect(store.setting(forWorkspace: "ws") == .inApp)
        store.applySetting(.ask, forWorkspace: "ws")
        #expect(store.setting(forWorkspace: "ws") == .ask)
        #expect(store.choice(forWorkspace: "ws") == nil)
        #expect(WebOpenSetting.allCases.count == 3)
    }
}

// MARK: - The open flow

struct OpenURLChoiceServiceTests {
    private static let url = URL(string: "https://example.com")!

    @Test func firstOpenInAWorkspaceAsksTheUser() async {
        let rig = Rig(answer: (.external, false))
        let result = await rig.service.open("https://example.com", workspaceId: "ws")
        #expect(result == .opened(destination: .external, url: Self.url))
        #expect(rig.prompt.presentCount == 1)
        #expect(rig.prompt.presentedWorkspaces == ["ws"])
        #expect(rig.opener.external == [Self.url])
        #expect(rig.opener.inApp.isEmpty)
    }

    @Test func rememberTickStoresTheChoiceForThatWorkspaceOnly() async {
        let rig = Rig(answer: (.external, true))
        _ = await rig.service.open("https://example.com", workspaceId: "ws")
        #expect(rig.store.choice(forWorkspace: "ws") == .external)
        #expect(rig.store.choice(forWorkspace: "other") == nil)
    }

    @Test func withoutRememberTheChoiceIsNotStored() async {
        let rig = Rig(answer: (.external, false))
        _ = await rig.service.open("https://example.com", workspaceId: "ws")
        #expect(rig.store.choice(forWorkspace: "ws") == nil)
    }

    @Test func rememberedChoiceIsReusedWithoutAsking() async {
        let rig = Rig(answer: (.external, true))
        _ = await rig.service.open("https://example.com", workspaceId: "ws")
        #expect(rig.prompt.presentCount == 1)

        let second = await rig.service.open("https://example.com/again", workspaceId: "ws")
        #expect(second == .opened(destination: .external, url: URL(string: "https://example.com/again")!))
        #expect(rig.prompt.presentCount == 1)  // still only the first ask
        #expect(rig.opener.external.count == 2)
    }

    @Test func noAnswerFallsBackToInAppAfterThirtySeconds() async {
        let rig = Rig(answer: nil)  // the user never answers
        let result = await rig.service.open("https://example.com", workspaceId: "ws")
        #expect(result == .opened(destination: .inApp, url: Self.url))
        #expect(rig.opener.inApp == [Self.url])
        #expect(rig.prompt.dismissCount == 1)
        // The wait ended on the injected clock at the 30 s mark, with no real sleep.
        #expect(rig.clock.slept >= 30)
        #expect(rig.clock.slept < 31)
        // A timed-out question stays unanswered: nothing was remembered.
        #expect(rig.store.choice(forWorkspace: "ws") == nil)
    }

    @Test func fallbackDoesNotWaitLongerThanTheTimeout() async {
        let rig = Rig(answer: nil, timeout: 5)
        _ = await rig.service.open("https://example.com", workspaceId: "ws")
        #expect(rig.clock.slept >= 5)
        #expect(rig.clock.slept < 6)
    }

    @Test func settingsChangeAppliesImmediatelyToAnOpenPane() async {
        // A pane that has already asked and remembered in_app.
        let rig = Rig(answer: (.inApp, true))
        _ = await rig.service.open("https://example.com", workspaceId: "ws")
        #expect(rig.opener.inApp == [Self.url])

        // The user changes the workspace's Settings entry while the pane stays open.
        rig.store.applySetting(.external, forWorkspace: "ws")

        let after = await rig.service.open("https://example.com", workspaceId: "ws")
        #expect(after == .opened(destination: .external, url: Self.url))
        #expect(rig.opener.external == [Self.url])
        #expect(rig.prompt.presentCount == 1)  // the change did not re-open the dialog
    }

    @Test func settingsBackToAskBringsTheDialogBack() async {
        let rig = Rig(answer: (.external, true))
        _ = await rig.service.open("https://example.com", workspaceId: "ws")
        #expect(rig.prompt.presentCount == 1)

        rig.store.applySetting(.ask, forWorkspace: "ws")
        _ = await rig.service.open("https://example.com", workspaceId: "ws")
        #expect(rig.prompt.presentCount == 2)
    }

    @Test func rejectedURLsOpenNothingAndNeverAsk() async {
        let long = "https://x.co/" + String(repeating: "a", count: WebOpenURLValidator.maxLength)
        for raw in ["", "   ", "file:///etc/passwd", "javascript:alert(1)",
                    "data:text/html,<b>x</b>", "ftp://example.com", long] {
            let rig = Rig(answer: (.external, true))
            let result = await rig.service.open(raw, workspaceId: "ws")
            guard case .rejected = result else {
                Issue.record("\(raw.prefix(24)) should have been rejected")
                continue
            }
            #expect(rig.opener.openedCount == 0)
            #expect(rig.prompt.presentCount == 0)
            #expect(rig.store.choice(forWorkspace: "ws") == nil)
        }
    }

    @Test func eachWorkspaceIsAskedOnItsOwn() async {
        let rig = Rig(answer: (.inApp, true))
        _ = await rig.service.open("https://example.com", workspaceId: "ws1")
        #expect(rig.prompt.presentCount == 1)
        _ = await rig.service.open("https://example.com", workspaceId: "ws2")
        #expect(rig.prompt.presentCount == 2)
        #expect(rig.prompt.presentedWorkspaces == ["ws1", "ws2"])
    }
}
