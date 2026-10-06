import Foundation
import Darwin

/// Recognises a run that failed because the CLI lost its sign-in. The app
/// still confirms with the CLI's own account status before asking the user to
/// sign in again, so these matchers stay conservative rather than complete.
public enum CLIAuthFailure {
    /// One Claude stream-json event. Only an assistant message the CLI marked
    /// as an error, or an error result, can carry the signal.
    public static func claude(_ event: [String: Any]) -> Bool {
        switch event["type"] as? String {
        case "assistant":
            guard let error = event["error"] as? String, !error.isEmpty else { return false }
            let blocks = (event["message"] as? [String: Any])?["content"] as? [[String: Any]] ?? []
            let texts = blocks.filter { $0["type"] as? String == "text" }.compactMap { $0["text"] as? String }
            // A Bedrock SCP denial also reaches Claude as an auth-shaped error;
            // signing in again cannot fix an organisation policy.
            if texts.contains(where: { BedrockAuthDiagnostics.runtimeFailureGuidance($0) != nil }) { return false }
            return error == "authentication_failed" || texts.contains(where: claude(text:))
        case "result":
            guard event["is_error"] as? Bool == true || (event["subtype"] as? String ?? "").hasPrefix("error") else { return false }
            let texts = (event["errors"] as? [String] ?? []) + [event["result"] as? String].compactMap { $0 }
            if texts.contains(where: { BedrockAuthDiagnostics.runtimeFailureGuidance($0) != nil }) { return false }
            return texts.contains(where: claude(text:))
        default: return false
        }
    }

    /// Claude's own sign-in failures ("Invalid API key · Please run /login",
    /// "OAuth token has expired", an API 401 authentication_error).
    public static func claude(text: String) -> Bool {
        guard BedrockAuthDiagnostics.runtimeFailureGuidance(text) == nil else { return false }
        let lowered = text.prefix(16_384).lowercased()
        if ["please run /login", "oauth token has expired", "oauth token revoked", "invalid api key"].contains(where: lowered.contains) { return true }
        return lowered.contains("401") && lowered.contains("authentication_error")
    }

    /// A Codex `turn.failed` / `error` message.
    public static func codex(text: String) -> Bool {
        guard BedrockAuthDiagnostics.runtimeFailureGuidance(text) == nil, !isCodexRetryNotice(text) else { return false }
        let lowered = text.prefix(16_384).lowercased()
        if lowered.contains("401") && lowered.contains("unauthorized") { return true }
        return ["not logged in", "please log in", "please login", "log in again", "sign in again", "refresh token"].contains(where: lowered.contains)
    }

    /// Codex reports each stream retry as an `error` event ("Reconnecting...
    /// 2/5 (unexpected status 401 ...)") and may still finish the turn.
    public static func isCodexRetryNotice(_ text: String) -> Bool {
        text.trimmingCharacters(in: .whitespacesAndNewlines).lowercased().hasPrefix("reconnecting")
    }

    /// One line of Gemini CLI's own console output (stderr, or a stdout line
    /// that is not JSON) that says its sign-in is gone. Only the CLI's own
    /// sign-in messages count here, since this output also carries API error
    /// dumps that quote other text:
    /// - a stale or revoked Google sign-in: the CLI cannot open a browser
    ///   headless, so it prints its consent prompt ("Opening authentication
    ///   page in your browser. Do you want to continue? [Y/n]") to stdout, reads
    ///   the request as the answer and reports "Error authenticating:
    ///   FatalCancellationError: Authentication cancelled by user." on stderr;
    /// - no method chosen: "Please set an Auth method in your …settings.json"
    ///   (or "No authentication method selected.");
    /// - with the browser suppressed: "Manual authorization is required but
    ///   the current session is non-interactive".
    public static func geminiConsole(line: String) -> Bool {
        let lowered = line.prefix(16_384).lowercased()
        if ["please set an auth method", "no authentication method selected", "opening authentication page in your browser",
            "manual authorization is required"].contains(where: lowered.contains) { return true }
        return lowered.contains("error authenticating")
            && (lowered.contains("fatalauthenticationerror") || lowered.contains("authentication cancelled by user"))
    }

    /// The message of a failed Gemini `result` / `error` event, never model or
    /// tool text: the console messages above, or a token rejected mid-run —
    /// OAuth `invalid_grant` ("Token has been expired or revoked") or the API's
    /// 401 UNAUTHENTICATED ("Request had invalid authentication credentials").
    /// An invalid API key ("API key not valid") is not matched, and a 401 under
    /// Vertex or an API key is ruled out by `signInCanFix`.
    public static func gemini(text: String) -> Bool {
        if geminiConsole(line: text) { return true }
        let lowered = text.prefix(16_384).lowercased()
        if ["authentication consent could not be obtained", "invalid_grant", "token has been expired or revoked",
            "request had invalid authentication credentials"].contains(where: lowered.contains) { return true }
        return lowered.contains("401") && lowered.contains("unauthenticated")
    }

    /// Whether signing in again can fix a run that failed with a sign-in
    /// signal. A dropped sign-in usually leaves credentials on disk, so the
    /// status still says signed in (or cannot tell): that does not rule it out.
    /// Only a method a browser sign-in does not renew does: no CLI, Bedrock /
    /// Vertex / Foundry or a Claude API key (status reports those as
    /// configuration, `accessVerified == false`), a Codex API-key login, or a
    /// Gemini method other than the Google sign-in (an API key, Vertex AI,
    /// Compute ADC, …).
    public static func signInCanFix(_ status: CLIAccountStatus) -> Bool {
        guard status.installed, status.accessVerified != false else { return false }
        if status.provider == "gemini" { return status.methodId == nil || status.methodId == CLIAccountMethod.geminiGoogle }
        return !(status.provider == "codex" && status.methodId == CLIAccountMethod.codexAPIKey)
    }

    /// The providers whose lost sign-in raises the card and may start a sign-in.
    public static let providers = ["claude", "codex", "gemini"]
}

/// Gemini has no sign-in command: its sign-in is the interactive CLI itself
/// ("Login with Google"), so it runs in a terminal the user sees. A dropped
/// sign-in usually leaves `oauth_creds.json` on disk, so a new sign-in is told
/// apart from the stale one by the file appearing or being rewritten after the
/// terminal opened. Only the file's modification time is read, never its contents.
public enum CLIGeminiLogin {
    public static func credentialsURL(home: URL) -> URL {
        home.appendingPathComponent(".gemini", isDirectory: true).appendingPathComponent("oauth_creds.json")
    }

    /// When the credentials file was last written; nil while it does not exist.
    public static func credentialsStamp(home: URL) -> Date? {
        // A linked file is read through its target, as the CLI reads it.
        guard let values = try? credentialsURL(home: home).resolvingSymlinksInPath().resourceValues(forKeys: [.contentModificationDateKey, .isRegularFileKey]),
              values.isRegularFile == true else { return nil }
        return values.contentModificationDate
    }

    /// Signed in since `baseline` (the stamp when the terminal opened): the
    /// file is newer than it, or appeared, and the status reads as a Google sign-in.
    public static func signedIn(since baseline: Date?, stamp: Date?, status: CLIAccountStatus) -> Bool {
        guard let stamp, baseline.map({ stamp > $0 }) ?? true else { return false }
        return status.provider == "gemini" && status.loggedIn == true && CLIAuthFailure.signInCanFix(status)
    }

    /// Waits for a sign-in in the terminal. The stamp is checked every `tick`;
    /// the status is read only once the stamp moved (then at most every
    /// `interval` until it confirms), and once more when the terminal closed
    /// (`exited`) or `limit` passed (`timedOut`).
    public static func wait(baseline: Date?, stamp: @Sendable () -> Date?, status: @Sendable () async -> CLIAccountStatus,
                            isOpen: @Sendable () async -> Bool, interval: TimeInterval, limit: TimeInterval, tick: TimeInterval = 1,
                            now: @Sendable () -> Date = { Date() }) async -> CLILoginWaitOutcome {
        let started = now()
        var lastRead: Date?
        while !Task.isCancelled {
            try? await Task.sleep(for: .seconds(tick))
            guard !Task.isCancelled else { return .cancelled }
            let open = await isOpen(), current = now()
            let expired = current.timeIntervalSince(started) >= limit
            let moved = stamp().map { value in baseline.map { value > $0 } ?? true } ?? false
            let final = !open || expired
            guard final || (moved && lastRead.map { current.timeIntervalSince($0) >= interval } ?? true) else { continue }
            lastRead = current
            let value = await status()
            guard !Task.isCancelled else { return .cancelled }
            if signedIn(since: baseline, stamp: stamp(), status: value) { return .loggedIn(value) }
            if !open { return .exited(value) }
            if expired { return .timedOut(value) }
        }
        return .cancelled
    }
}

/// Whether a lost sign-in starts its provider's background sign-in by itself
/// (the Settings switch "로그인이 끊기면 자동으로 다시 로그인"). One sign-in per
/// provider: none while one already runs, and none for `cooldown` after one
/// failed, was cancelled or succeeded, so neither a refused sign-in nor a
/// request that fails again right after approval reopens the browser in a loop.
/// A request login recovery itself resent, or one sent before the last
/// sign-in, only raises the card. The card's button is never held by this.
public struct CLIAutoLoginGate: Sendable, Equatable {
    public static let cooldown: TimeInterval = 120
    /// When each provider's last sign-in ended (failed, cancelled or succeeded).
    public private(set) var heldAt: [String: Date] = [:]
    /// When each provider was last confirmed signed in.
    public private(set) var signedInAt: [String: Date] = [:]
    public init() {}

    /// `loginActive`: a background sign-in, a sign-in terminal or another
    /// account change of the provider is under way, or it cannot change now.
    /// `resent`: the failed request was itself resent after a sign-in.
    /// `sentAt`: when the failed request started its run.
    public func shouldStart(provider: String, enabled: Bool, status: CLIAccountStatus, loginActive: Bool,
                            resent: Bool = false, sentAt: Date? = nil, now: Date = Date()) -> Bool {
        guard enabled, !loginActive, !resent, CLIAuthFailure.providers.contains(provider), status.provider == provider,
              CLIAuthFailure.signInCanFix(status) else { return false }
        if let sentAt, let signedIn = signedInAt[provider], sentAt < signedIn { return false }
        guard let held = heldAt[provider] else { return true }
        return now.timeIntervalSince(held) >= Self.cooldown
    }
    /// A sign-in of the provider failed, timed out or was cancelled.
    public mutating func stopped(provider: String, at now: Date = Date()) { heldAt[provider] = now }
    /// Signed in again (in the background, a terminal or Settings).
    public mutating func succeeded(provider: String, at now: Date = Date()) { heldAt[provider] = now; signedInAt[provider] = now }
}

/// What a sign-in command printed so far, reduced to the two things the pane
/// card shows: the first https link and whether it asks for a pasted code.
public struct CLILoginOutput: Sendable, Equatable {
    public static let maximumBytes = 65_536
    public private(set) var url: URL?
    public private(set) var asksForCode = false
    private var raw = Data()
    private var finished = false

    public init() {}

    public mutating func append(_ data: Data) {
        let room = Self.maximumBytes - raw.count
        guard room > 0 else { return }
        raw.append(data.prefix(room))
        parse()
    }
    /// The process ended: a link at the very end of its output is complete.
    public mutating func finish() { finished = true; parse() }

    private mutating func parse() {
        let text = Self.plainText(String(decoding: raw, as: UTF8.self))
        // Until a sign-in host shows up, a later one may still replace a first other link.
        if !Self.isAuthURL(url) { url = Self.firstURL(in: text, final: finished) ?? url }
        if !asksForCode { asksForCode = Self.asksForCode(text) }
    }

    /// Terminal output without escape sequences. An OSC 8 hyperlink keeps its
    /// target, since some CLIs print the link text only as a label.
    public static func plainText(_ raw: String) -> String {
        var text = raw.replacingOccurrences(of: "\u{1B}\\]8;[^;\u{07}\u{1B}]*;([^\u{07}\u{1B}]*)(?:\u{07}|\u{1B}\\\\)", with: " $1 ", options: .regularExpression)
        text = text.replacingOccurrences(of: "\u{1B}(?:\\[[0-?]*[ -/]*[@-~]|\\][^\u{07}\u{1B}]*(?:\u{07}|\u{1B}\\\\)|[@-Z\\\\-_])", with: "", options: .regularExpression)
        return text.replacingOccurrences(of: "\r\n", with: "\n").replacingOccurrences(of: "\r", with: "\n")
    }

    /// The sign-in pages of the supported CLIs. A link on one of them wins
    /// over any other link printed before it (a docs or status page).
    public static let authHosts = ["claude.ai", "claude.com", "console.anthropic.com", "auth.openai.com", "chatgpt.com"]

    /// The first complete https link on an expected sign-in host, or else the
    /// first complete https link. A link running into the end of the text may
    /// still be arriving, so it only counts once something follows it or `final`.
    public static func firstURL(in text: String, final: Bool) -> URL? {
        guard let pattern = try? NSRegularExpression(pattern: "https://[^\\s\"'<>`\\x00-\\x1F\\x7F]+") else { return nil }
        let whole = NSRange(text.startIndex..., in: text)
        var first: URL?
        for match in pattern.matches(in: text, range: whole) {
            guard let range = Range(match.range, in: text), final || range.upperBound < text.endIndex else { continue }
            var value = String(text[range])
            while let last = value.last, ".,;:!?)]}>".contains(last) { value.removeLast() }
            guard value.utf8.count <= 8_192, let url = URL(string: value), url.scheme == "https", url.host?.isEmpty == false else { continue }
            if isAuthURL(url) { return url }
            if first == nil { first = url }
        }
        return first
    }

    static func isAuthURL(_ url: URL?) -> Bool {
        guard let host = url?.host?.lowercased() else { return false }
        return authHosts.contains { host == $0 || host.hasSuffix("." + $0) }
    }

    /// A line asking to paste a code ("Paste code here if prompted >").
    /// "Enter this code" lines are codes shown for the browser, not asked for.
    public static func asksForCode(_ text: String) -> Bool {
        text.split(whereSeparator: \.isNewline).contains { line in
            let lowered = line.lowercased()
            return lowered.contains("paste") && lowered.contains("code")
        }
    }
}

/// A request that failed for a lost sign-in, kept to resend once signed in.
public struct CLILoginRetryRequest: Sendable, Equatable {
    public var sessionId: String
    public var provider: String
    public var input: String
    public var attachments: [RunAttachment]
    /// Login recovery itself sent this request again after a sign-in.
    public var resent: Bool
    /// When the request started its run.
    public var sentAt: Date?
    public init(sessionId: String, provider: String, input: String, attachments: [RunAttachment] = [], resent: Bool = false, sentAt: Date? = nil) {
        self.sessionId = sessionId; self.provider = provider; self.input = input; self.attachments = attachments; self.resent = resent; self.sentAt = sentAt
    }
}

/// How a pane looks when its retry comes due.
public enum CLILoginRetryPane: Sendable, Equatable {
    case gone
    case busy
    case idle(provider: String)
}

/// Failed requests waiting for their provider's sign-in, one per pane. A
/// retry is only ever the pane's latest request: sending anything else in
/// the pane drops it, and so does a pane that is busy or changed provider
/// when the sign-in completes.
public struct CLILoginRetryBook: Sendable, Equatable {
    public private(set) var requests: [String: CLILoginRetryRequest] = [:]
    public init() {}

    public mutating func remember(_ request: CLILoginRetryRequest) { requests[request.sessionId] = request }
    /// The pane sent something new, or its card was dismissed.
    public mutating func drop(sessionId: String) { requests.removeValue(forKey: sessionId) }
    /// One pane's request, taken out to resend it now.
    public mutating func take(sessionId: String) -> CLILoginRetryRequest? { requests.removeValue(forKey: sessionId) }
    public func sessions(provider: String) -> [String] { requests.values.filter { $0.provider == provider }.map(\.sessionId).sorted() }

    /// Takes every request of `provider` out of the book and returns the ones
    /// whose pane is idle on the same provider, in a stable order.
    public mutating func take(provider: String, pane: (String) -> CLILoginRetryPane) -> [CLILoginRetryRequest] {
        let due = requests.values.filter { $0.provider == provider }.sorted { $0.sessionId < $1.sessionId }
        for request in due { requests.removeValue(forKey: request.sessionId) }
        return due.filter { pane($0.sessionId) == .idle(provider: provider) }
    }
}

/// A sign-in command running in a hidden pseudo-terminal, so the CLI behaves
/// as it does in a terminal (it opens the browser itself) while the app only
/// reads what it prints. Nothing it prints or is sent is logged or persisted.
public final class CLIBackgroundLogin: @unchecked Sendable {
    private static let handle = "login"
    private let terminal: PTYAgentTerminalPane
    private let command: String
    private let changed: @Sendable (CLILoginOutput) -> Void
    private let lock = NSLock()
    private var output = CLILoginOutput()
    private var subscription: UUID?

    /// `changed` runs off the main thread whenever the link or code prompt appears.
    public init(command: String, environment: [String: String], directory: URL, changed: @escaping @Sendable (CLILoginOutput) -> Void = { _ in }) {
        self.command = command; self.changed = changed
        // A wide tty keeps a long sign-in link on one line for CLIs that wrap
        // their own output to the terminal width.
        terminal = PTYAgentTerminalPane(workingDirectory: directory, environment: environment, columns: 1_000)
    }

    public func start() async throws {
        subscription = terminal.subscribe { [weak self] data in self?.receive(data) }
        try await terminal.launch(command: command, handle: Self.handle)
    }

    public var isRunning: Bool { terminal.isRunning(handle: Self.handle) }
    public var exitCode: Int32? { terminal.exitCode(handle: Self.handle) }
    public var currentOutput: CLILoginOutput { lock.withLock { output } }

    /// Types the pasted code and Enter. The tty turns the carriage return into
    /// a newline for line-reading prompts and raw-mode prompts see Enter.
    public func send(code: String) {
        let clean = code.trimmingCharacters(in: .whitespacesAndNewlines).filter { !$0.isNewline }
        guard !clean.isEmpty, clean.utf8.count <= 4_096 else { return }
        terminal.appendUserTyped(text: clean + "\r", handle: Self.handle)
    }

    /// Ends the command's whole process group; a second signal follows if it lingers.
    public func cancel() {
        terminal.sendSIGTERM(handle: Self.handle)
        let terminal = terminal
        DispatchQueue.global(qos: .utility).asyncAfter(deadline: .now() + 0.5) { terminal.sendSIGKILL(handle: Self.handle) }
        if let subscription { terminal.unsubscribe(subscription) }
    }

    /// Marks the output complete once the process ended.
    public func finishOutput() {
        let value: CLILoginOutput? = lock.withLock {
            let before = output; output.finish()
            return output == before ? nil : output
        }
        if let value { changed(value) }
    }

    private func receive(_ data: Data) {
        let value: CLILoginOutput? = lock.withLock {
            let before = output; output.append(data)
            return output == before ? nil : output
        }
        if let value { changed(value) }
    }
}

public enum CLILoginWaitOutcome: Sendable, Equatable {
    case loggedIn(CLIAccountStatus)
    /// The command ended without a confirmed sign-in.
    case exited(CLIAccountStatus)
    case timedOut(CLIAccountStatus)
    case cancelled
}

/// Waits for a background sign-in. The status is read once at the start:
/// - signed out then: signed in as soon as the status turns signed in (asked
///   every `interval`, and at once when the command ends);
/// - signed in or unknown then (a dropped sign-in often leaves credentials on
///   disk): only the command itself ending with code 0, followed by a signed-in
///   status, counts. A poll result never ends such a command early.
/// Past `limit` it reports `timedOut`; stopping the command is the caller's.
public enum CLILoginWait {
    public static func run(isRunning: @Sendable () -> Bool, exitCode: @Sendable () -> Int32?,
                           status: @Sendable () async -> CLIAccountStatus,
                           interval: TimeInterval, limit: TimeInterval, tick: TimeInterval = 0.25,
                           now: @Sendable () -> Date = { Date() }) async -> CLILoginWaitOutcome {
        let started = now()
        let wasSignedOut = await status().loggedIn == false
        func signedIn(_ value: CLIAccountStatus) -> Bool { value.loggedIn == true && value.accessVerified != false }
        var lastCheck = now()
        while !Task.isCancelled {
            try? await Task.sleep(for: .seconds(tick))
            guard !Task.isCancelled else { return .cancelled }
            let running = isRunning(), current = now()
            let expired = current.timeIntervalSince(started) >= limit
            if !wasSignedOut {
                if running && !expired { continue }
                let value = await status()
                guard !Task.isCancelled else { return .cancelled }
                if running { return .timedOut(value) }
                return exitCode() == 0 && signedIn(value) ? .loggedIn(value) : .exited(value)
            }
            guard !running || expired || current.timeIntervalSince(lastCheck) >= interval else { continue }
            lastCheck = current
            let value = await status()
            guard !Task.isCancelled else { return .cancelled }
            if signedIn(value) { return .loggedIn(value) }
            if !running { return .exited(value) }
            if expired { return .timedOut(value) }
        }
        return .cancelled
    }
}
