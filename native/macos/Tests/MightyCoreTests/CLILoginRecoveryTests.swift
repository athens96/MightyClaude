import Foundation
import Testing
@testable import MightyCore

struct CLIAuthFailureTests {
    private let scpDenial: String = {
        let message = "User: arn:aws:sts::123456789012:assumed-role/fixture/session is not authorized to perform: bedrock:InvokeModel on resource: arn:aws:bedrock:us-east-1::foundation-model/fixture with an explicit deny in a service control policy"
        let json = String(decoding: try! JSONSerialization.data(withJSONObject: ["Message": message]), as: UTF8.self)
        return "AWS authentication failed · refresh your AWS credentials and retry · API Error: 403 \(json)"
    }()
    private func send(_ value: [String: Any], to parser: CLIStreamParser) throws {
        parser.push(try JSONSerialization.data(withJSONObject: value)); parser.push("\n")
    }
    private func assistant(error: String?, text: String) -> [String: Any] {
        var value: [String: Any] = ["type": "assistant", "message": ["id": "m", "content": [["type": "text", "text": text]]]]
        if let error { value["error"] = error }
        return value
    }

    @Test func claudeSignalsComeFromErrorMessagesOnly() {
        #expect(CLIAuthFailure.claude(assistant(error: "authentication_failed", text: "Invalid API key · Please run /login")))
        #expect(CLIAuthFailure.claude(assistant(error: "authentication_failed", text: "Anything")))
        #expect(CLIAuthFailure.claude(["type": "result", "is_error": true, "result": "OAuth token has expired. Please obtain a new token."]))
        #expect(CLIAuthFailure.claude(["type": "result", "subtype": "error_during_execution", "errors": ["API Error: 401 {\"type\":\"error\",\"error\":{\"type\":\"authentication_error\"}}"]]))
        // Ordinary text quoting the phrase, a successful result, and other failures.
        #expect(!CLIAuthFailure.claude(assistant(error: nil, text: "Please run /login to continue")))
        #expect(!CLIAuthFailure.claude(["type": "result", "is_error": false, "result": "Please run /login"]))
        #expect(!CLIAuthFailure.claude(["type": "result", "is_error": true, "result": "API Error: 500 Internal server error"]))
        #expect(!CLIAuthFailure.claude(["type": "result", "is_error": true, "result": "API Error: 429 rate_limit_error"]))
        #expect(!CLIAuthFailure.claude(assistant(error: "rate_limit", text: "You've hit your usage limit")))
    }

    @Test func bedrockPolicyDenialIsNeverALostLogin() throws {
        #expect(!CLIAuthFailure.claude(assistant(error: "authentication_failed", text: scpDenial)))
        #expect(!CLIAuthFailure.claude(["type": "result", "is_error": true, "errors": [scpDenial], "result": scpDenial]))
        #expect(!CLIAuthFailure.claude(text: scpDenial))
        #expect(!CLIAuthFailure.codex(text: scpDenial))
        // The parser keeps its Bedrock guidance and raises no sign-in signal.
        var logs: [String] = []
        let parser = CLIStreamParser(provider: "claude", log: { logs.append($1) }, resume: { _ in })
        try send(assistant(error: "authentication_failed", text: scpDenial), to: parser)
        try send(["type": "result", "subtype": "error_during_execution", "is_error": true, "errors": [scpDenial]], to: parser)
        parser.flush()
        #expect(parser.failed && !parser.authFailure)
        #expect(logs.allSatisfy { $0 == BedrockAuthDiagnostics.scpInvokeModelDeniedMessage })
    }

    @Test func codexMatchesSignInFailuresButNotServerOrRateLimits() {
        for text in ["unexpected status 401 Unauthorized: token expired", "You are not logged in. Run codex login.",
                     "Your access token could not be refreshed because your refresh token was already used. Please log out and sign in again."] {
            #expect(CLIAuthFailure.codex(text: text), "\(text)")
        }
        for text in ["unexpected status 500 Internal Server Error", "exceeded retry limit, last status: 429 Too Many Requests",
                     "Rate limit reached for requests", "stream disconnected before completion", "401 items processed",
                     "Reconnecting... 2/5 (unexpected status 401 Unauthorized: token expired)"] {
            #expect(!CLIAuthFailure.codex(text: text), "\(text)")
        }
    }

    @Test func onlyTheRunsFinalFailureDecides() throws {
        // A 401 the CLI recovered from, then a server error: not a lost sign-in.
        let claude = CLIStreamParser(provider: "claude", log: { _, _ in }, resume: { _ in })
        try send(assistant(error: "authentication_failed", text: "Invalid API key · Please run /login"), to: claude)
        try send(["type": "result", "is_error": true, "result": "API Error: 500 Internal server error"], to: claude)
        #expect(claude.failed && !claude.authFailure)
        // An auth error followed by a successful result.
        let recovered = CLIStreamParser(provider: "claude", log: { _, _ in }, resume: { _ in })
        try send(assistant(error: "authentication_failed", text: "Please run /login"), to: recovered)
        try send(["type": "result", "subtype": "success", "is_error": false, "result": "done"], to: recovered)
        #expect(!recovered.authFailure)
        // Codex retry notices never decide; the turn's own failure does.
        let codex = CLIStreamParser(provider: "codex", log: { _, _ in }, resume: { _ in })
        try send(["type": "error", "message": "Reconnecting... 1/5 (unexpected status 401 Unauthorized)"], to: codex)
        #expect(!codex.authFailure)
        try send(["type": "turn.failed", "error": ["message": "unexpected status 500 Internal Server Error"]], to: codex)
        #expect(!codex.authFailure)
        let late = CLIStreamParser(provider: "codex", log: { _, _ in }, resume: { _ in })
        try send(["type": "error", "message": "unexpected status 500"], to: late)
        try send(["type": "turn.failed", "error": ["message": "unexpected status 401 Unauthorized"]], to: late)
        #expect(late.authFailure)
    }

    @Test func theReasonTravelsOnTheStatusEvent() throws {
        let event = RunEvent(sessionId: "pane", type: "status", status: "error", reason: "auth")
        #expect(try JSONDecoder().decode(RunEvent.self, from: JSONEncoder().encode(event)) == event)
        let old = try JSONDecoder().decode(RunEvent.self, from: Data(#"{"sessionId":"pane","type":"status","status":"error"}"#.utf8))
        #expect(old.reason == nil)
    }

    @Test func parserRaisesTheSignalForTheRunOnlyOnMatchingFailures() throws {
        let claude = CLIStreamParser(provider: "claude", log: { _, _ in }, resume: { _ in })
        try send(assistant(error: "authentication_failed", text: "Invalid API key · Please run /login"), to: claude)
        try send(["type": "result", "is_error": true, "result": "Invalid API key · Please run /login"], to: claude)
        claude.flush()
        #expect(claude.failed && claude.authFailure)
        let codex = CLIStreamParser(provider: "codex", log: { _, _ in }, resume: { _ in })
        try send(["type": "turn.failed", "error": ["message": "unexpected status 401 Unauthorized"]], to: codex)
        #expect(codex.failed && codex.authFailure)
        let other = CLIStreamParser(provider: "codex", log: { _, _ in }, resume: { _ in })
        try send(["type": "turn.failed", "error": ["message": "unexpected status 500 Internal Server Error"]], to: other)
        #expect(other.failed && !other.authFailure)
    }

    @Test func statusOnlyRulesOutMethodsASignInCannotRenew() {
        // A dropped sign-in often still reads as signed in, or cannot be read.
        #expect(CLIAuthFailure.signInCanFix(CLIAccountStatus(provider: "claude", loggedIn: false)))
        #expect(CLIAuthFailure.signInCanFix(CLIAccountStatus(provider: "claude", loggedIn: true, method: "Claude subscription", methodId: CLIAccountMethod.claudeSubscription)))
        #expect(CLIAuthFailure.signInCanFix(CLIAccountStatus(provider: "claude", loggedIn: nil, detail: "timeout")))
        #expect(CLIAuthFailure.signInCanFix(CLIAccountSupport.parseCodexStatus(text: "Logged in using ChatGPT", authJSON: nil)))
        #expect(!CLIAuthFailure.signInCanFix(CLIAccountStatus(provider: "codex", installed: false, loggedIn: false)))
        #expect(!CLIAuthFailure.signInCanFix(CLIAccountSupport.parseCodexStatus(text: "Logged in using an API key - sk-***", authJSON: nil)))
        // The decision reads the language-free id, never the displayed label.
        for language in [AppLanguage.ko, .en] {
            LocaleOverride.$language.withValue(language) {
                let keyed = CLIAccountSupport.parseCodexStatus(text: "Logged in using an API key - sk-***", authJSON: nil)
                #expect(keyed.methodId == CLIAccountMethod.codexAPIKey && !CLIAuthFailure.signInCanFix(keyed))
            }
        }
        #expect(!CLIAuthFailure.signInCanFix(CLIAccountStatus(provider: "codex", loggedIn: true, method: "any label", methodId: CLIAccountMethod.codexAPIKey)))
        #expect(CLIAuthFailure.signInCanFix(CLIAccountStatus(provider: "codex", loggedIn: true, method: CLIAccountSupport.codexAPIKeyMethod, methodId: CLIAccountMethod.codexChatGPT)))
        #expect(CLIAuthFailure.signInCanFix(CLIAccountStatus(provider: "gemini", loggedIn: true, method: "Google account", methodId: CLIAccountMethod.geminiGoogle)))
        #expect(!CLIAuthFailure.signInCanFix(CLIAccountStatus(provider: "gemini", loggedIn: true, method: CLIAccountSupport.geminiGoogleMethod, methodId: CLIAccountMethod.geminiVertex)))
        for json in [#"{"loggedIn":true,"authMethod":"third_party","apiProvider":"bedrock"}"#, #"{"loggedIn":true,"apiProvider":"vertex"}"#,
                     #"{"loggedIn":true,"apiProvider":"foundry"}"#, #"{"loggedIn":true,"authMethod":"api_key"}"#] {
            #expect(!CLIAuthFailure.signInCanFix(CLIAccountSupport.parseClaudeStatus(Data(json.utf8))), "\(json)")
        }
        #expect(CLIAuthFailure.signInCanFix(CLIAccountSupport.parseClaudeStatus(Data(#"{"loggedIn":true,"authMethod":"claude.ai"}"#.utf8))))
    }
}

struct CLIGeminiAuthFailureTests {
    private func send(_ value: [String: Any], to parser: CLIStreamParser) throws {
        parser.push(try JSONSerialization.data(withJSONObject: value)); parser.push("\n")
    }
    private func gemini() -> CLIStreamParser { CLIStreamParser(provider: "gemini", log: { _, _ in }, resume: { _ in }) }

    /// What Gemini CLI 0.43 printed for each case in a headless stream-json run (temp HOME, fake credentials).
    @Test func geminiMatchesItsSignInFailures() {
        for text in [
            "Opening authentication page in your browser. Do you want to continue? [Y/n]: fix the bug",
            "Error authenticating: FatalCancellationError: Authentication cancelled by user.\n    at initOauthClient (file:///…/chunk.js:245553:15)",
            "Please set an Auth method in your /Users/me/.gemini/settings.json or specify one of the following environment variables before running: GEMINI_API_KEY, GOOGLE_GENAI_USE_VERTEXAI, GOOGLE_GENAI_USE_GCA",
            "Manual authorization is required but the current session is non-interactive. Please run the Gemini CLI in an interactive terminal to log in, provide a GEMINI_API_KEY, or ensure Application Default Credentials are configured.",
            "No authentication method selected.",
            "[API Error: invalid_grant]",
            "Token has been expired or revoked.",
            "[API Error: Request had invalid authentication credentials. Expected OAuth 2 access token, login cookie or other valid authentication credential. (Status: UNAUTHENTICATED)]",
            #"{"error":{"code":401,"message":"…","status":"UNAUTHENTICATED"}}"#,
        ] { #expect(CLIAuthFailure.gemini(text: text), "\(text)") }
        for text in [
            #"[API Error: {"error":{"code":400,"message":"API key not valid. Please pass a valid API key.","status":"INVALID_ARGUMENT"}}]"#,
            "[API Error: Resource has been exhausted (e.g. check quota). (Status: RESOURCE_EXHAUSTED)]",
            "[API Error: The caller does not have permission (Status: PERMISSION_DENIED)]",
            "[API Error: Internal error encountered. (Status: INTERNAL)]",
            "No input provided via stdin. Input can be provided by piping data into gemini or using the --prompt option.",
            "Gemini CLI is not running in a trusted directory.",
            "401 files changed",
            "Error authenticating: getaddrinfo ENOTFOUND oauth2.googleapis.com",
        ] { #expect(!CLIAuthFailure.gemini(text: text), "\(text)") }
        // On stderr and plain stdout lines only the CLI's own sign-in messages count: its API error
        // dumps there may quote anything, so token rejections are left to the failed result.
        for line in ["Error authenticating: FatalCancellationError: Authentication cancelled by user.",
                     "Error authenticating: FatalAuthenticationError: Failed to authenticate with user code.",
                     "Please set an Auth method in your /Users/me/.gemini/settings.json", "No authentication method selected.",
                     "Manual authorization is required but the current session is non-interactive.",
                     "Opening authentication page in your browser. Do you want to continue? [Y/n]: "] {
            #expect(CLIAuthFailure.geminiConsole(line: line), "\(line)")
        }
        for line in ["Error when talking to Gemini API Full report available at: /tmp/x.json _ApiError: {\"error\":{\"code\":401,\"status\":\"UNAUTHENTICATED\"}}",
                     "GaxiosError: invalid_grant", "Token has been expired or revoked.", "Request had invalid authentication credentials.",
                     "Error authenticating: getaddrinfo ENOTFOUND oauth2.googleapis.com", "Authentication consent could not be obtained."] {
            #expect(!CLIAuthFailure.geminiConsole(line: line), "\(line)")
        }
    }

    @Test func onlyAGoogleSignInIsSomethingASignInCanFix() throws {
        let home = FileManager.default.temporaryDirectory.appendingPathComponent("mighty-gemini-method-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: home) }
        let directory = home.appendingPathComponent(".gemini")
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        func select(_ type: String?) throws -> CLIAccountStatus {
            let settings: [String: Any] = type.map { ["security": ["auth": ["selectedType": $0]]] } ?? [:]
            try JSONSerialization.data(withJSONObject: settings).write(to: directory.appendingPathComponent("settings.json"))
            return CLIAccountSupport.geminiStatus(home: home, environment: ["GEMINI_API_KEY": "fixture", "GOOGLE_CLOUD_PROJECT": "fixture"])
        }
        // Signed out, and a stale Google sign-in whose file is still there.
        #expect(CLIAuthFailure.signInCanFix(try select("oauth-personal")))
        try Data("{}".utf8).write(to: directory.appendingPathComponent("oauth_creds.json"))
        #expect(CLIAuthFailure.signInCanFix(try select("oauth-personal")))
        #expect(CLIAuthFailure.signInCanFix(try select(nil)))
        // A key, Vertex AI or any other method is not renewed by a Google sign-in.
        for type in ["gemini-api-key", "vertex-ai", "compute-default-credentials"] {
            #expect(!CLIAuthFailure.signInCanFix(try select(type)), "\(type)")
        }
        #expect(!CLIAuthFailure.signInCanFix(CLIAccountStatus(provider: "gemini", installed: false, loggedIn: false)))
        let gate = CLIAutoLoginGate()
        #expect(!gate.shouldStart(provider: "gemini", enabled: true, status: try select("gemini-api-key"), loginActive: false))
        #expect(!gate.shouldStart(provider: "gemini", enabled: true, status: try select("vertex-ai"), loginActive: false))
        #expect(gate.shouldStart(provider: "gemini", enabled: true, status: try select("oauth-personal"), loginActive: false))
    }

    @Test func theParserRaisesTheSignalFromStderrPlainLinesAndFailedResultsOnly() throws {
        // A stale sign-in: the consent prompt on stdout, the cancellation on stderr, no JSON at all.
        let stale = gemini()
        stale.push("\nOpening authentication page in your browser. Do you want to continue? [Y/n]: fix the bug\n")
        #expect(stale.authFailure)
        let cancelled = gemini()
        cancelled.receiveStderr("Error authenticating: FatalCancel")
        cancelled.receiveStderr("lationError: Authentication cancelled by user.\n")
        #expect(cancelled.authFailure)
        // A last line without a newline counts once the process ended.
        let unset = gemini()
        unset.receiveStderr("Please set an Auth method in your /Users/me/.gemini/settings.json or specify one of the following environment variables before running: GEMINI_API_KEY")
        #expect(!unset.authFailure)
        unset.finishStderr()
        #expect(unset.authFailure)
        // An API error dump on stderr, and a cut-off JSON line quoting the prompt, never count.
        let dump = gemini()
        dump.receiveStderr("Error when talking to Gemini API _ApiError: {\"error\":{\"code\":401,\"message\":\"invalid_grant\",\"status\":\"UNAUTHENTICATED\"}}\n")
        dump.push(#"{"type":"message","role":"assistant","content":"Opening authentication page in your browser"# + "\n")
        dump.finishStderr()
        #expect(!dump.authFailure)
        // A token rejected mid-run arrives as the failed result.
        let rejected = gemini()
        try send(["type": "init", "session_id": "s"], to: rejected)
        try send(["type": "result", "status": "error", "error": ["type": "unknown", "message": "[API Error: Request had invalid authentication credentials. (Status: UNAUTHENTICATED)]"]], to: rejected)
        #expect(rejected.failed && rejected.authFailure)
        // A bad API key, a quota error, and the phrase in the model's own text or another CLI's stderr never count.
        let key = gemini()
        try send(["type": "result", "status": "error", "error": ["type": "unknown", "message": #"[API Error: {"error":{"code":400,"message":"API key not valid. Please pass a valid API key."}}]"#]], to: key)
        #expect(key.failed && !key.authFailure)
        let quoted = gemini()
        try send(["type": "message", "role": "assistant", "content": "Fix the invalid_grant error by signing in again.", "delta": false], to: quoted)
        try send(["type": "tool_result", "tool_id": "t", "status": "success", "output": "Please set an Auth method"], to: quoted)
        #expect(!quoted.authFailure)
        let claude = CLIStreamParser(provider: "claude", log: { _, _ in }, resume: { _ in })
        claude.receiveStderr("Please set an Auth method")
        #expect(!claude.authFailure)
        // The run's last word decides: a retry that recovered, or a later other failure.
        let recovered = gemini()
        try send(["type": "error", "severity": "error", "message": "[API Error: Request had invalid authentication credentials.]"], to: recovered)
        #expect(recovered.authFailure)
        try send(["type": "result", "status": "success"], to: recovered)
        recovered.receiveStderr("[STARTUP] Phase 'cleanup_ops' was started but never ended.\n")
        #expect(!recovered.authFailure)
        let other = gemini()
        try send(["type": "error", "severity": "error", "message": "[API Error: Request had invalid authentication credentials.]"], to: other)
        try send(["type": "result", "status": "error", "error": ["message": "[API Error: Internal error encountered. (Status: INTERNAL)]"]], to: other)
        #expect(other.failed && !other.authFailure)
    }
}

struct CLIGeminiLoginTests {
    private func home() throws -> URL {
        let home = FileManager.default.temporaryDirectory.appendingPathComponent("mighty-gemini-login-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: home.appendingPathComponent(".gemini"), withIntermediateDirectories: true)
        return home
    }
    private func write(_ home: URL, at date: Date) throws {
        let url = CLIGeminiLogin.credentialsURL(home: home)
        // Fixture contents only; the code under test reads nothing but the file's dates.
        try Data(#"{"fixture":true}"#.utf8).write(to: url)
        try FileManager.default.setAttributes([.modificationDate: date], ofItemAtPath: url.path)
    }
    private let google = CLIAccountStatus(provider: "gemini", loggedIn: true, method: CLIAccountSupport.geminiGoogleMethod, methodId: CLIAccountMethod.geminiGoogle)

    @Test func aNewSignInIsTheCredentialsFileAppearingOrRewrittenAfterTheTerminalOpened() throws {
        let home = try home(); defer { try? FileManager.default.removeItem(at: home) }
        let opened = Date(timeIntervalSince1970: 1_800_000_000)
        #expect(CLIGeminiLogin.credentialsStamp(home: home) == nil)
        // Signed out when the terminal opened: the file appearing counts.
        try write(home, at: opened)
        let stamp = try #require(CLIGeminiLogin.credentialsStamp(home: home))
        #expect(abs(stamp.timeIntervalSince(opened)) < 1)
        #expect(CLIGeminiLogin.signedIn(since: nil, stamp: stamp, status: google))
        // A stale file left from the dropped sign-in does not, until it is rewritten.
        #expect(!CLIGeminiLogin.signedIn(since: stamp, stamp: CLIGeminiLogin.credentialsStamp(home: home), status: google))
        try write(home, at: opened.addingTimeInterval(30))
        #expect(CLIGeminiLogin.signedIn(since: stamp, stamp: CLIGeminiLogin.credentialsStamp(home: home), status: google))
        // The status must read as a Google sign-in, not a key or Vertex, and must be signed in.
        let later = CLIGeminiLogin.credentialsStamp(home: home)
        #expect(!CLIGeminiLogin.signedIn(since: stamp, stamp: later, status: CLIAccountStatus(provider: "gemini", loggedIn: true, method: "Gemini API key", methodId: CLIAccountMethod.geminiAPIKey)))
        #expect(!CLIGeminiLogin.signedIn(since: stamp, stamp: later, status: CLIAccountStatus(provider: "gemini", loggedIn: false)))
        #expect(!CLIGeminiLogin.signedIn(since: stamp, stamp: nil, status: google))
        // The status read from the same files agrees.
        #expect(CLIGeminiLogin.signedIn(since: stamp, stamp: later, status: CLIAccountSupport.geminiStatus(home: home, environment: [:])))
    }

    @Test func theWaitEndsSignedInClosedOrTimedOut() async throws {
        let home = try home(); defer { try? FileManager.default.removeItem(at: home) }
        try write(home, at: Date().addingTimeInterval(-3_600))
        let baseline = CLIGeminiLogin.credentialsStamp(home: home)
        let reads = LockedBox(0)
        let status: @Sendable () async -> CLIAccountStatus = { reads.update { $0 += 1 }; return CLIAccountSupport.geminiStatus(home: home, environment: [:]) }
        // Nothing changed and the terminal stays open: the status is never read until the limit.
        let idle = await CLIGeminiLogin.wait(baseline: baseline, stamp: { CLIGeminiLogin.credentialsStamp(home: home) }, status: status,
                                             isOpen: { true }, interval: 0.05, limit: 0.3, tick: 0.02)
        #expect(idle == .timedOut(CLIAccountSupport.geminiStatus(home: home, environment: [:])))
        #expect(reads.get() == 1)
        // The terminal closed without a sign-in.
        let closed = await CLIGeminiLogin.wait(baseline: baseline, stamp: { CLIGeminiLogin.credentialsStamp(home: home) }, status: status,
                                               isOpen: { false }, interval: 0.05, limit: 20, tick: 0.02)
        guard case .exited = closed else { Issue.record("expected exited, got \(closed)"); return }
        // The CLI rewrites the file while the terminal is open.
        let task = Task {
            await CLIGeminiLogin.wait(baseline: baseline, stamp: { CLIGeminiLogin.credentialsStamp(home: home) }, status: status,
                                      isOpen: { true }, interval: 0.05, limit: 20, tick: 0.02)
        }
        try await Task.sleep(for: .milliseconds(100))
        try write(home, at: Date())
        let outcome = await task.value
        guard case .loggedIn(let value) = outcome else { Issue.record("expected loggedIn, got \(outcome)"); return }
        #expect(value.methodId == CLIAccountMethod.geminiGoogle)
        let cancelled = Task { await CLIGeminiLogin.wait(baseline: nil, stamp: { nil }, status: status, isOpen: { true }, interval: 1, limit: 20, tick: 0.02) }
        cancelled.cancel()
        #expect(await cancelled.value == .cancelled)
    }
}

struct CLIAutoLoginGateTests {
    private let signedIn = CLIAccountStatus(provider: "claude", loggedIn: true, method: "Claude 구독")
    private let start = Date(timeIntervalSince1970: 1_000_000)

    @Test func startsOncePerProviderWhileASignInRuns() {
        let gate = CLIAutoLoginGate()
        // The first lost sign-in starts; every pane failing meanwhile finds it running.
        #expect(gate.shouldStart(provider: "claude", enabled: true, status: signedIn, loginActive: false, now: start))
        #expect(!gate.shouldStart(provider: "claude", enabled: true, status: signedIn, loginActive: true, now: start))
        // Another provider is decided on its own.
        #expect(gate.shouldStart(provider: "codex", enabled: true, status: CLIAccountStatus(provider: "codex"), loginActive: false, now: start))
        // Gemini starts its terminal sign-in through the same gate, and a status of another provider never counts.
        #expect(gate.shouldStart(provider: "gemini", enabled: true, status: CLIAccountStatus(provider: "gemini"), loginActive: false, now: start))
        #expect(!gate.shouldStart(provider: "gemini", enabled: true, status: signedIn, loginActive: false, now: start))
        #expect(!gate.shouldStart(provider: "codex", enabled: true, status: signedIn, loginActive: false, now: start))
    }

    @Test func aFailedOrCancelledSignInWaitsForTheCooldown() {
        var gate = CLIAutoLoginGate()
        gate.stopped(provider: "claude", at: start)
        #expect(!gate.shouldStart(provider: "claude", enabled: true, status: signedIn, loginActive: false, now: start))
        #expect(!gate.shouldStart(provider: "claude", enabled: true, status: signedIn, loginActive: false, now: start.addingTimeInterval(CLIAutoLoginGate.cooldown - 1)))
        #expect(gate.shouldStart(provider: "claude", enabled: true, status: signedIn, loginActive: false, now: start.addingTimeInterval(CLIAutoLoginGate.cooldown)))
        #expect(CLIAutoLoginGate.cooldown == 120)
        // The cooldown is per provider.
        #expect(gate.shouldStart(provider: "codex", enabled: true, status: CLIAccountStatus(provider: "codex"), loginActive: false, now: start))
    }

    @Test func aSuccessHoldsAutomaticStartsAndStaleOrResentFailuresOnlyRaiseTheCard() {
        var gate = CLIAutoLoginGate()
        let approved = start
        gate.succeeded(provider: "claude", at: approved)
        // A request failing again right after approval never reopens the browser.
        let soon = approved.addingTimeInterval(5)
        #expect(!gate.shouldStart(provider: "claude", enabled: true, status: signedIn, loginActive: false, sentAt: soon, now: soon))
        #expect(!gate.shouldStart(provider: "claude", enabled: true, status: signedIn, loginActive: false, sentAt: approved.addingTimeInterval(CLIAutoLoginGate.cooldown - 1), now: approved.addingTimeInterval(CLIAutoLoginGate.cooldown - 1)))
        let later = approved.addingTimeInterval(CLIAutoLoginGate.cooldown)
        #expect(gate.shouldStart(provider: "claude", enabled: true, status: signedIn, loginActive: false, sentAt: later, now: later))
        // A run that started before the last sign-in, however late it fails, only raises the card.
        #expect(!gate.shouldStart(provider: "claude", enabled: true, status: signedIn, loginActive: false, sentAt: approved.addingTimeInterval(-1), now: later.addingTimeInterval(600)))
        // A request login recovery resent only raises the card, whenever it fails.
        #expect(!gate.shouldStart(provider: "claude", enabled: true, status: signedIn, loginActive: false, resent: true, sentAt: later, now: later.addingTimeInterval(600)))
        // Other providers are unaffected.
        #expect(gate.shouldStart(provider: "codex", enabled: true, status: CLIAccountStatus(provider: "codex"), loginActive: false, sentAt: soon, now: soon))
        #expect(gate.signedInAt["claude"] == approved && gate.heldAt["claude"] == approved)
    }

    @Test func aRetryRemembersWhetherItWasResent() {
        var book = CLILoginRetryBook()
        let sent = Date(timeIntervalSince1970: 5)
        book.remember(CLILoginRetryRequest(sessionId: "a", provider: "claude", input: "x", resent: true, sentAt: sent))
        let taken = book.take(sessionId: "a")
        #expect(taken?.resent == true && taken?.sentAt == sent)
        #expect(CLILoginRetryRequest(sessionId: "b", provider: "codex", input: "y").resent == false)
    }

    @Test func theSettingAndTheMethodDecide() {
        let gate = CLIAutoLoginGate()
        #expect(!gate.shouldStart(provider: "claude", enabled: false, status: signedIn, loginActive: false, now: start))
        // Methods a browser sign-in does not renew never start one.
        let bedrock = CLIAccountSupport.parseClaudeStatus(Data(#"{"loggedIn":true,"authMethod":"third_party","apiProvider":"bedrock"}"#.utf8))
        #expect(!CLIAuthFailure.signInCanFix(bedrock))
        #expect(!gate.shouldStart(provider: "claude", enabled: true, status: bedrock, loginActive: false, now: start))
        let apiKey = CLIAccountSupport.parseCodexStatus(text: "Logged in using an API key - sk-***", authJSON: nil)
        #expect(!gate.shouldStart(provider: "codex", enabled: true, status: apiKey, loginActive: false, now: start))
        #expect(!gate.shouldStart(provider: "claude", enabled: true, status: CLIAccountStatus(provider: "claude", installed: false), loginActive: false, now: start))
    }

    @Test func theSwitchIsOnUnlessSavedOff() async throws {
        // Older state has no field: nil, which the app reads as on.
        let legacy = Data(#"{"version":1,"workspaces":[],"sessions":[],"layout":"grid","theme":"dark","sidebarWidth":300}"#.utf8)
        #expect(StateRepository.decodeSnapshot(legacy).autoLoginCLIs == nil)
        #expect(AppSnapshot().autoLoginCLIs == nil)
        var object = try #require(JSONSerialization.jsonObject(with: legacy) as? [String: Any])
        for invalid: Any in [0, "false", NSNull()] {
            object["autoLoginCLIs"] = invalid
            #expect(StateRepository.decodeSnapshot(try JSONSerialization.data(withJSONObject: object)).autoLoginCLIs == nil)
        }
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("mighty-auto-login-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: directory) }
        for value in [false, true] {
            try await StateRepository(directory: directory, legacyStateURL: nil).save(AppSnapshot(autoLoginCLIs: value))
            #expect(try await StateRepository(directory: directory, legacyStateURL: nil).load().autoLoginCLIs == value)
        }
    }
}

struct CLILoginOutputTests {
    @Test func firstHttpsLinkIsFoundAfterEscapesAndOnlyOnceComplete() {
        var output = CLILoginOutput()
        output.append(Data("\u{1B}[1mStarting local login server on http://localhost:1455.\u{1B}[0m\r\nOpen: https://auth.example.com/oauth/authorize?client_id=abc&re".utf8))
        #expect(output.url == nil)
        output.append(Data("direct_uri=http%3A%2F%2Flocalhost.\r\n".utf8))
        #expect(output.url?.absoluteString == "https://auth.example.com/oauth/authorize?client_id=abc&redirect_uri=http%3A%2F%2Flocalhost")
        output.append(Data("later https://other.example.com/x\n".utf8))
        #expect(output.url?.host == "auth.example.com")

        var tail = CLILoginOutput()
        tail.append(Data("Visit https://claude.ai/oauth/authorize?code=true".utf8))
        #expect(tail.url == nil)
        tail.finish()
        #expect(tail.url?.absoluteString == "https://claude.ai/oauth/authorize?code=true")
    }

    @Test func hyperlinkTargetsAndPastePromptsAreRecognised() {
        var output = CLILoginOutput()
        output.append(Data("\u{1B}]8;;https://claude.ai/oauth/authorize?x=1\u{07}Sign in\u{1B}]8;;\u{07}\r\n".utf8))
        #expect(output.url?.absoluteString == "https://claude.ai/oauth/authorize?x=1")
        #expect(!output.asksForCode)
        output.append(Data("\u{1B}[2KPaste code here if prompted > ".utf8))
        #expect(output.asksForCode)
        #expect(!CLILoginOutput.asksForCode("Enter this one-time code in your browser: ABCD-1234"))
        #expect(CLILoginOutput.firstURL(in: "no link here\n", final: true) == nil)
        #expect(CLILoginOutput.firstURL(in: "http://insecure.example.com/ \n", final: true) == nil)
    }

    @Test func aSignInHostWinsOverAnEarlierLink() {
        var output = CLILoginOutput()
        output.append(Data("Docs: https://docs.example.com/login \n".utf8))
        #expect(output.url?.host == "docs.example.com")
        output.append(Data("Sign in: https://auth.openai.com/oauth/authorize?x=1 \n".utf8))
        #expect(output.url?.host == "auth.openai.com")
        output.append(Data("Or: https://claude.ai/oauth \n".utf8))
        #expect(output.url?.host == "auth.openai.com")
        #expect(CLILoginOutput.firstURL(in: "a https://x.example.com/ b https://console.anthropic.com/login c", final: true)?.host == "console.anthropic.com")
        #expect(CLILoginOutput.firstURL(in: "https://evilclaude.ai/ https://x.example.com/ ", final: true)?.host == "evilclaude.ai")
    }

    @Test func outputIsBounded() {
        var output = CLILoginOutput()
        output.append(Data(repeating: 65, count: CLILoginOutput.maximumBytes))
        output.append(Data("\nhttps://late.example.com/ \n".utf8))
        #expect(output.url == nil)
    }
}

struct CLILoginRetryBookTests {
    private func request(_ id: String, _ provider: String = "claude") -> CLILoginRetryRequest {
        CLILoginRetryRequest(sessionId: id, provider: provider, input: "input " + id,
                             attachments: [RunAttachment(id: "a-" + id, name: "a.txt", mediaType: "text/plain", dataBase64: "YQ==")])
    }

    @Test func onlyIdlePanesOnTheSameProviderAreResent() {
        var book = CLILoginRetryBook()
        for id in ["idle", "busy", "gone", "switched"] { book.remember(request(id)) }
        book.remember(request("codex-pane", "codex"))
        let due = book.take(provider: "claude") { id in
            switch id {
            case "idle": .idle(provider: "claude")
            case "busy": .busy
            case "switched": .idle(provider: "codex")
            default: .gone
            }
        }
        #expect(due == [request("idle")])
        #expect(due.first?.attachments.first?.id == "a-idle")
        // Every claude request left the book, resent or dropped; codex waits.
        #expect(book.sessions(provider: "claude").isEmpty)
        #expect(book.sessions(provider: "codex") == ["codex-pane"])
    }

    @Test func sendingSomethingElseOrDismissingDropsTheRetry() {
        var book = CLILoginRetryBook()
        book.remember(request("a")); book.remember(request("b"))
        book.drop(sessionId: "a")
        let due = book.take(provider: "claude") { _ in .idle(provider: "claude") }
        #expect(due == [request("b")])
        #expect(book.requests.isEmpty)
        // A later failure in the same pane replaces the earlier request.
        book.remember(request("c")); book.remember(CLILoginRetryRequest(sessionId: "c", provider: "claude", input: "newer"))
        let latest = book.take(provider: "claude") { _ in .idle(provider: "claude") }
        #expect(latest.map(\.input) == ["newer"])
        book.remember(request("d"))
        #expect(book.take(sessionId: "d") == request("d"))
        #expect(book.take(sessionId: "d") == nil)
    }
}

@Suite(.serialized)
final class CLIBackgroundLoginTests {
    private var directories: [URL] = []
    deinit { for directory in directories { try? FileManager.default.removeItem(at: directory) } }

    /// A fake sign-in command in a temp folder; never a real CLI.
    private func script(_ body: String) throws -> (root: URL, command: String) {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("mighty-login-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        directories.append(root)
        let file = root.appendingPathComponent("fake-login")
        try Data(("#!/bin/sh\nROOT='" + root.path + "'\n" + body).utf8).write(to: file)
        try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: file.path)
        return (root, "'" + file.path + "'")
    }
    private var environment: [String: String] { ["PATH": "/usr/bin:/bin", "SHELL": "/bin/sh", "HOME": NSTemporaryDirectory()] }
    private func signedIn(_ root: URL) -> CLIAccountStatus {
        CLIAccountStatus(provider: "claude", loggedIn: FileManager.default.fileExists(atPath: root.appendingPathComponent("signed-in").path))
    }
    private func wait(_ condition: () -> Bool) async throws {
        for _ in 0..<500 { if condition() { return }; try await Task.sleep(for: .milliseconds(10)) }
        throw MightyError("condition not met")
    }

    private func wait(for login: CLIBackgroundLogin, _ root: URL, interval: TimeInterval = 30, limit: TimeInterval = 20, status: (@Sendable () -> CLIAccountStatus)? = nil) async -> CLILoginWaitOutcome {
        let read = status ?? { self.signedIn(root) }
        return await CLILoginWait.run(isRunning: { login.isRunning }, exitCode: { login.exitCode }, status: { read() },
                                      interval: interval, limit: limit, tick: 0.05)
    }

    @Test func signedOutAtStartSucceedsOnceStatusTurnsWithoutKillingTheCommand() async throws {
        // The fake signs in only once the wait has taken its starting reading
        // (signed out): a head start in seconds could be overtaken on a busy
        // runner, and the wait would then rightly treat the user as signed in
        // already and hold out for the command's own exit.
        let fake = try script("printf 'Opening browser...\\r\\nIf it did not open, visit: https://auth.example.com/login?state=1\\r\\n'\nwhile [ ! -e \"$ROOT/go\" ]; do /bin/sleep 0.05; done\n: > \"$ROOT/signed-in\"\n/bin/sleep 30\n")
        let seen = LockedBox<CLILoginOutput?>(nil)
        let login = CLIBackgroundLogin(command: fake.command, environment: environment, directory: fake.root) { seen.set($0) }
        try await login.start()
        let go = fake.root.appendingPathComponent("go")
        let outcome = await wait(for: login, fake.root, interval: 0.1, status: {
            let value = self.signedIn(fake.root)
            FileManager.default.createFile(atPath: go.path, contents: nil)
            return value
        })
        guard case .loggedIn(let status) = outcome else { Issue.record("unexpected \(outcome)"); login.cancel(); return }
        #expect(status.loggedIn == true)
        #expect(login.isRunning)
        try await wait { seen.get()?.url != nil }
        #expect(seen.get()?.url?.absoluteString == "https://auth.example.com/login?state=1")
        login.cancel()
        try await wait { !login.isRunning }
    }

    @Test func signedInAtStartNeedsTheCommandToEndWithZero() async throws {
        // Credentials already on disk: the status alone proves nothing.
        let always: @Sendable () -> CLIAccountStatus = { CLIAccountStatus(provider: "claude", loggedIn: true) }
        let success = try script("/bin/sleep 0.3\nexit 0\n")
        let good = CLIBackgroundLogin(command: success.command, environment: environment, directory: success.root)
        try await good.start()
        #expect(await wait(for: good, success.root, interval: 0.05, status: always) == .loggedIn(CLIAccountStatus(provider: "claude", loggedIn: true)))
        let failure = try script("/bin/sleep 0.2\nexit 3\n")
        let bad = CLIBackgroundLogin(command: failure.command, environment: environment, directory: failure.root)
        try await bad.start()
        #expect(await wait(for: bad, failure.root, interval: 0.05, status: always) == .exited(CLIAccountStatus(provider: "claude", loggedIn: true)))
        // A long command is never ended by a poll: only the limit stops the wait.
        let slow = try script("/bin/sleep 30\n")
        let pending = CLIBackgroundLogin(command: slow.command, environment: environment, directory: slow.root)
        try await pending.start()
        let unknown: @Sendable () -> CLIAccountStatus = { CLIAccountStatus(provider: "codex", loggedIn: nil) }
        #expect(await wait(for: pending, slow.root, interval: 0.05, limit: 0.4, status: unknown) == .timedOut(CLIAccountStatus(provider: "codex", loggedIn: nil)))
        #expect(pending.isRunning)
        pending.cancel()
        try await wait { !pending.isRunning }
    }

    @Test func pastedCodeReachesThePromptAndExitWithoutSignInFails() async throws {
        let fake = try script("printf 'Paste code here if prompted > '\nread code\nprintf '%s' \"$code\" > \"$ROOT/code\"\nexit 3\n")
        let login = CLIBackgroundLogin(command: fake.command, environment: environment, directory: fake.root)
        try await login.start()
        try await wait { login.currentOutput.asksForCode }
        login.send(code: "  secret-code-123 \n")
        #expect(await wait(for: login, fake.root) == .exited(CLIAccountStatus(provider: "claude", loggedIn: false)))
        #expect((try? String(contentsOf: fake.root.appendingPathComponent("code"), encoding: .utf8)) == "secret-code-123")
        #expect(login.exitCode == 3)
    }

    @Test func linkAtTheVeryEndCountsOnceTheCommandEnded() async throws {
        let fake = try script("printf 'Visit https://claude.ai/oauth/authorize?code=true'\nexit 1\n")
        let login = CLIBackgroundLogin(command: fake.command, environment: environment, directory: fake.root)
        try await login.start()
        try await wait { !login.isRunning }
        try await Task.sleep(for: .milliseconds(100))
        #expect(login.currentOutput.url == nil)
        login.finishOutput()
        #expect(login.currentOutput.url?.absoluteString == "https://claude.ai/oauth/authorize?code=true")
    }

    @Test func statusIsPolledAtTheIntervalWhileRunning() async throws {
        let calls = LockedBox(0)
        let task = Task {
            await CLILoginWait.run(isRunning: { true }, exitCode: { nil }, status: {
                calls.update { $0 += 1 }
                return CLIAccountStatus(provider: "codex", loggedIn: calls.get() >= 3)
            }, interval: 0.1, limit: 20, tick: 0.02)
        }
        let outcome = await task.value
        #expect(outcome == .loggedIn(CLIAccountStatus(provider: "codex", loggedIn: true)))
        // The first read is the starting status (signed out).
        #expect(calls.get() == 3)
        let cancelled = Task { await CLILoginWait.run(isRunning: { true }, exitCode: { nil }, status: { CLIAccountStatus(provider: "codex", loggedIn: false) }, interval: 10, limit: 20, tick: 0.02) }
        cancelled.cancel()
        #expect(await cancelled.value == .cancelled)
    }
}

private final class LockedBox<Value>: @unchecked Sendable {
    private let lock = NSLock()
    private var value: Value
    init(_ value: Value) { self.value = value }
    func get() -> Value { lock.withLock { value } }
    func set(_ newValue: Value) { lock.withLock { value = newValue } }
    func update(_ change: (inout Value) -> Void) { lock.withLock { change(&value) } }
}
