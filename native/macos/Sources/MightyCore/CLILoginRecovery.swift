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

    /// Whether signing in again can fix a run that failed with a sign-in
    /// signal. A dropped sign-in usually leaves credentials on disk, so the
    /// status still says signed in (or cannot tell): that does not rule it out.
    /// Only a method a browser sign-in does not renew does: no CLI, Bedrock /
    /// Vertex / Foundry or a Claude API key (status reports those as
    /// configuration, `accessVerified == false`), or a Codex API-key login.
    public static func signInCanFix(_ status: CLIAccountStatus) -> Bool {
        guard status.installed, status.accessVerified != false else { return false }
        return !(status.provider == "codex" && status.method == CLIAccountSupport.codexAPIKeyMethod)
    }
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
    public init(sessionId: String, provider: String, input: String, attachments: [RunAttachment] = []) {
        self.sessionId = sessionId; self.provider = provider; self.input = input; self.attachments = attachments
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
