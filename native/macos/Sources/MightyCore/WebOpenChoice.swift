import Foundation

/// Where an http/https URL opened by the agent should be shown.
public enum WebOpenDestination: String, Sendable, Equatable, Codable {
    /// Open inside the app's CEF browser pane.
    case inApp = "in_app"
    /// Open in the system's default browser.
    case external = "external"
}

/// What Settings offers per workspace: ask every time, or a remembered destination.
public enum WebOpenSetting: String, Sendable, Equatable, CaseIterable {
    case ask
    case inApp = "in_app"
    case external = "external"

    public var destination: WebOpenDestination? {
        switch self {
        case .ask: return nil
        case .inApp: return .inApp
        case .external: return .external
        }
    }

    public init(destination: WebOpenDestination?) {
        switch destination {
        case .none: self = .ask
        case .some(.inApp): self = .inApp
        case .some(.external): self = .external
        }
    }
}

/// Validates a raw URL string before the agent-terminal open_url tool accepts it.
public enum WebOpenURLValidator {
    /// Maximum accepted URL length in characters.
    public static let maxLength = 8_192
    /// Schemes the open_url tool accepts; everything else is rejected with an error.
    public static let allowedSchemes: Set<String> = ["http", "https"]

    public enum Failure: Error, Equatable {
        case empty
        case tooLong(Int)
        case disallowedScheme(String)
    }

    /// Returns the validated URL or the reason it was rejected.
    public static func validate(_ raw: String) -> Result<URL, Failure> {
        guard !raw.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return .failure(.empty) }
        guard raw.count <= maxLength else { return .failure(.tooLong(raw.count)) }
        guard let url = URL(string: raw),
              let scheme = url.scheme?.lowercased(),
              allowedSchemes.contains(scheme),
              !(url.host ?? "").isEmpty else {
            return .failure(.disallowedScheme(URL(string: raw)?.scheme?.lowercased() ?? ""))
        }
        return .success(url)
    }

    /// The localized message handed back when a URL is refused.
    public static func message(for failure: Failure) -> String {
        switch failure {
        case .empty: return WebOpenChoiceCopy.errorEmpty
        case .tooLong: return WebOpenChoiceCopy.errorTooLong
        case .disallowedScheme: return WebOpenChoiceCopy.errorScheme
        }
    }
}

/// The user-facing copy of the URL choice dialog and of its Settings entry.
/// The L("key") calls live here so locales/ko.json and locales/en.json stay the
/// single source (checked by scripts/check-locales.js).
public enum WebOpenChoiceCopy {
    public static var dialogTitle: String { L("agentTerminal.urlOpen.dialogTitle") }
    public static var dialogMessage: String { L("agentTerminal.urlOpen.dialogMessage") }
    public static var inAppButton: String { L("agentTerminal.urlOpen.inAppButton") }
    public static var externalButton: String { L("agentTerminal.urlOpen.externalButton") }
    public static var rememberToggle: String { L("agentTerminal.urlOpen.rememberToggle") }
    public static func fallbackHint(seconds: TimeInterval) -> String { L("agentTerminal.urlOpen.fallbackHint", ["seconds": String(Int(seconds))]) }
    public static var errorEmpty: String { L("agentTerminal.urlOpen.errorEmpty") }
    public static var errorTooLong: String { L("agentTerminal.urlOpen.errorTooLong") }
    public static var errorScheme: String { L("agentTerminal.urlOpen.errorScheme") }
    public static var settingTitle: String { L("agentTerminal.urlOpen.settingTitle") }
    public static var settingAsk: String { L("agentTerminal.urlOpen.settingAsk") }
    public static var settingInApp: String { L("agentTerminal.urlOpen.settingInApp") }
    public static var settingExternal: String { L("agentTerminal.urlOpen.settingExternal") }

    public static func settingLabel(_ setting: WebOpenSetting) -> String {
        switch setting {
        case .ask: return settingAsk
        case .inApp: return settingInApp
        case .external: return settingExternal
        }
    }
}

/// Per-workspace user-chosen destination for URLs opened by agent tools.
///
/// One instance is shared by Settings and by every open agent pane, so a change
/// made in Settings is visible to the panes that are already open on their very
/// next open_url call — there is no per-pane copy to go stale.
///
/// With `defaults` the choices live in the app's settings (UserDefaults, one
/// dictionary keyed by workspace id) and survive a restart; without it they
/// live in memory only, which is what tests use.
public final class WebOpenChoiceStore: @unchecked Sendable {
    /// The UserDefaults key holding `[workspaceId: destination]`.
    public static let defaultsKey = "agentTerminal.webOpenChoices"
    /// The app-wide store. Settings writes here; every agent pane reads here.
    public static let shared = WebOpenChoiceStore(defaults: .standard)

    private let lock = NSLock()
    private let defaults: UserDefaults?
    private let key: String
    private var remembered: [String: WebOpenDestination] = [:]

    public init(defaults: UserDefaults? = nil, key: String = WebOpenChoiceStore.defaultsKey) {
        self.defaults = defaults
        self.key = key
        let saved = defaults?.dictionary(forKey: key) as? [String: String] ?? [:]
        remembered = saved.compactMapValues(WebOpenDestination.init(rawValue:))
    }

    /// The remembered destination for `workspaceId`, or nil if not yet set.
    public func choice(forWorkspace workspaceId: String) -> WebOpenDestination? {
        lock.lock()
        defer { lock.unlock() }
        return remembered[workspaceId]
    }

    /// Records a destination for `workspaceId`. Called on dialog confirm or Settings change.
    public func setChoice(_ destination: WebOpenDestination, forWorkspace workspaceId: String) {
        lock.lock()
        remembered[workspaceId] = destination
        persist()
        lock.unlock()
    }

    /// Removes the remembered destination so the next call asks again.
    public func clearChoice(forWorkspace workspaceId: String) {
        lock.lock()
        remembered.removeValue(forKey: workspaceId)
        persist()
        lock.unlock()
    }

    /// Write the whole map back. Called with `lock` held.
    private func persist() {
        defaults?.set(remembered.mapValues(\.rawValue), forKey: key)
    }

    // MARK: - Settings entry

    /// What the Settings row shows for `workspaceId`.
    public func setting(forWorkspace workspaceId: String) -> WebOpenSetting {
        WebOpenSetting(destination: choice(forWorkspace: workspaceId))
    }

    /// Apply a Settings change. `.ask` forgets the choice so the dialog returns.
    public func applySetting(_ setting: WebOpenSetting, forWorkspace workspaceId: String) {
        if let destination = setting.destination {
            setChoice(destination, forWorkspace: workspaceId)
        } else {
            clearChoice(forWorkspace: workspaceId)
        }
    }
}

/// Injectable clock for the 30-second prompt fallback. Tests drive a fake clock
/// and never sleep for real time.
public protocol WebOpenChoiceClock: Sendable {
    var now: Date { get }
    func sleep(for interval: TimeInterval) async
}

public struct RealWebOpenClock: WebOpenChoiceClock {
    public static let shared = RealWebOpenClock()
    public init() {}

    public var now: Date { Date() }

    public func sleep(for interval: TimeInterval) async {
        try? await Task.sleep(nanoseconds: UInt64(max(0, interval) * 1_000_000_000))
    }
}

/// One open_url call waiting for the user's answer.
public struct WebOpenPromptRequest: Sendable, Equatable, Identifiable {
    public let id: String
    public let url: URL
    public let workspaceId: String
    /// The agent pane that asked; the dialog shows in that pane.
    public let agentPaneId: String?
    /// How long the dialog waits before the page opens in the app.
    public let timeoutSeconds: TimeInterval

    public init(id: String = UUID().uuidString, url: URL, workspaceId: String, agentPaneId: String?, timeoutSeconds: TimeInterval) {
        self.id = id; self.url = url; self.workspaceId = workspaceId; self.agentPaneId = agentPaneId; self.timeoutSeconds = timeoutSeconds
    }
}

/// What the user picked in the dialog.
public struct WebOpenPromptAnswer: Sendable, Equatable {
    public let destination: WebOpenDestination
    /// "Remember for this workspace" was ticked.
    public let remember: Bool

    public init(destination: WebOpenDestination, remember: Bool) {
        self.destination = destination; self.remember = remember
    }
}

/// Presents the URL-open choice dialog to the user.
///
/// Deliberately poll-shaped rather than a suspending call: ``present(_:)``
/// only puts the dialog on screen, and the service asks ``answer(for:)`` until
/// the answer arrives or the fallback deadline passes. Nothing the service does can
/// outlive its own deadline, so the agent is never left waiting on the dialog.
/// Every call has its own request id, so two panes can ask at the same time.
public protocol WebOpenPromptPresenter: Sendable {
    /// Put the dialog for `request` on screen. Called at most once per open_url call.
    func present(_ request: WebOpenPromptRequest)
    /// The user's answer to request `id`, or nil while the dialog is still waiting.
    func answer(for id: String) -> WebOpenPromptAnswer?
    /// Take the dialog for request `id` down — the answer arrived, or the fallback fired.
    func dismiss(_ id: String)
}

/// Performs the actual open once a destination is resolved.
public protocol WebOpener: Sendable {
    /// Show `url` in the asking agent pane's browser pane. Returns false when
    /// the in-app browser cannot show it (engine off or missing, or the agent
    /// pane is gone); nothing was opened then.
    func openInApp(_ url: URL, agentPaneId: String?, workspaceId: String) async -> Bool
    /// Hand `url` to the system's default browser. Returns false when it refused.
    func openExternally(_ url: URL) async -> Bool
}

/// Result of a ``WebOpenService/open(_:workspaceId:agentPaneId:provider:)`` call.
public enum WebOpenOpenResult: Sendable, Equatable {
    /// The URL passed validation and was opened at `destination`.
    case opened(destination: WebOpenDestination, url: URL)
    /// In-app was chosen but the in-app browser could not show the page, so
    /// it opened in the system browser instead.
    case openedExternallyInstead(url: URL)
    /// The URL passed validation but nothing accepted it.
    case failed(url: URL)
    /// The URL was rejected and nothing was opened.
    case rejected(reason: WebOpenURLValidator.Failure)
}

/// Orchestrates the full URL-open flow for the agent-terminal `open_url` tool:
/// validates the URL, honours the per-workspace remembered choice without asking,
/// otherwise asks once and falls back to `.inApp` when the injected clock passes
/// `promptTimeoutSeconds`, and records the choice when the user ticks remember.
public final class WebOpenService: @unchecked Sendable {
    /// Default fallback delay for the choice dialog.
    public static let defaultPromptTimeoutSeconds: TimeInterval = 30
    /// How often the pending answer is checked while the dialog is up.
    static let pollInterval: TimeInterval = 0.25

    private let store: WebOpenChoiceStore
    private let presenter: any WebOpenPromptPresenter
    private let opener: any WebOpener
    private let clock: any WebOpenChoiceClock
    /// Where an in-app open records the browser pane it opened into, so the
    /// relay pane list carries that pane beside the agent pane that owns it.
    private let paneRegistry: AgentIOPaneRegistry?
    public let promptTimeoutSeconds: TimeInterval

    public init(
        store: WebOpenChoiceStore,
        presenter: any WebOpenPromptPresenter,
        opener: any WebOpener,
        clock: any WebOpenChoiceClock = RealWebOpenClock.shared,
        promptTimeoutSeconds: TimeInterval = WebOpenService.defaultPromptTimeoutSeconds,
        paneRegistry: AgentIOPaneRegistry? = nil
    ) {
        self.store = store
        self.presenter = presenter
        self.opener = opener
        self.clock = clock
        self.promptTimeoutSeconds = promptTimeoutSeconds
        self.paneRegistry = paneRegistry
    }

    /// Validate `rawURL`, resolve the workspace's destination, and open it.
    ///
    /// A rejected URL opens nothing: no dialog is shown and the opener is never
    /// called. Never waits longer than `promptTimeoutSeconds` on the injected clock.
    /// `agentPaneId` names the agent pane asking: its dialog shows in that pane,
    /// and an in-app open shows the page in that pane's browser pane and
    /// registers it, which is how the pane shows up in the relay pane list. An
    /// external open opens no pane of ours and so registers nothing. When the
    /// in-app browser cannot show the page, it opens in the system browser and
    /// the result says so.
    public func open(_ rawURL: String, workspaceId: String, agentPaneId: String? = nil, provider: String = "claude") async -> WebOpenOpenResult {
        switch WebOpenURLValidator.validate(rawURL) {
        case .failure(let reason):
            return .rejected(reason: reason)
        case .success(let url):
            switch await resolveDestination(url: url, workspaceId: workspaceId, agentPaneId: agentPaneId) {
            case .inApp:
                if await opener.openInApp(url, agentPaneId: agentPaneId, workspaceId: workspaceId) {
                    if let agentPaneId {
                        paneRegistry?.registerBrowserPane(agentPaneId: agentPaneId, workspaceId: workspaceId, provider: provider)
                    }
                    return .opened(destination: .inApp, url: url)
                }
                return await opener.openExternally(url) ? .openedExternallyInstead(url: url) : .failed(url: url)
            case .external:
                return await opener.openExternally(url) ? .opened(destination: .external, url: url) : .failed(url: url)
            }
        }
    }

    private func resolveDestination(url: URL, workspaceId: String, agentPaneId: String?) async -> WebOpenDestination {
        // A remembered choice is honoured without asking.
        if let remembered = store.choice(forWorkspace: workspaceId) { return remembered }

        let deadline = clock.now.addingTimeInterval(promptTimeoutSeconds)
        let request = WebOpenPromptRequest(url: url, workspaceId: workspaceId, agentPaneId: agentPaneId, timeoutSeconds: promptTimeoutSeconds)
        presenter.present(request)
        while true {
            if let answer = presenter.answer(for: request.id) {
                presenter.dismiss(request.id)
                if answer.remember { store.setChoice(answer.destination, forWorkspace: workspaceId) }
                return answer.destination
            }
            if clock.now >= deadline { break }
            await clock.sleep(for: Self.pollInterval)
        }
        // No answer within promptTimeoutSeconds: open inside the app and do not
        // remember anything — the question is still unanswered for this workspace.
        presenter.dismiss(request.id)
        return .inApp
    }
}
