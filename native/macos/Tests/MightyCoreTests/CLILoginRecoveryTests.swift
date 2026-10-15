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
        // How a headless run words a sign-in that expired and could not be refreshed.
        #expect(CLIAuthFailure.claude(["type": "result", "subtype": "success", "is_error": true, "result": "Failed to authenticate: OAuth session expired and could not be refreshed"]))
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

    @Test func aResultRepeatingTheMarkedSignInFailureKeepsIt() throws {
        // The CLI marks the turn's failed message `authentication_failed` and copies its
        // text into the error result, so the result keeps the signal whatever the wording.
        for text in ["Failed to authenticate: OAuth session expired and could not be refreshed", "Sign-in went stale · try later"] {
            let parser = CLIStreamParser(provider: "claude", log: { _, _ in }, resume: { _ in })
            try send(assistant(error: "authentication_failed", text: text), to: parser)
            try send(["type": "result", "subtype": "success", "is_error": true, "result": text], to: parser)
            parser.flush()
            #expect(parser.failed && parser.authFailure, "\(text)")
        }
        // Unmarked, the same unknown words are no sign-in signal.
        let unmarked = CLIStreamParser(provider: "claude", log: { _, _ in }, resume: { _ in })
        try send(assistant(error: "server_error", text: "Sign-in went stale · try later"), to: unmarked)
        try send(["type": "result", "subtype": "success", "is_error": true, "result": "Sign-in went stale · try later"], to: unmarked)
        unmarked.flush()
        #expect(unmarked.failed && !unmarked.authFailure)
        // A sub-agent's own failure in between does not decide; a Bedrock policy denial beside the words never counts.
        let child = CLIStreamParser(provider: "claude", log: { _, _ in }, resume: { _ in })
        try send(assistant(error: "authentication_failed", text: "Sign-in went stale · try later"), to: child)
        var childError = assistant(error: "rate_limit", text: "Rate limited"); childError["parent_tool_use_id"] = "toolu_child"
        try send(childError, to: child)
        try send(["type": "result", "subtype": "success", "is_error": true, "result": "Sign-in went stale · try later"], to: child)
        #expect(child.failed && child.authFailure)
        let denied = CLIStreamParser(provider: "claude", log: { _, _ in }, resume: { _ in })
        try send(assistant(error: "authentication_failed", text: "Sign-in went stale · try later"), to: denied)
        try send(["type": "result", "subtype": "error_during_execution", "is_error": true, "errors": [scpDenial], "result": "Sign-in went stale · try later"], to: denied)
        #expect(denied.failed && !denied.authFailure)
        // Only a message marked authentication_failed is remembered, trimmed, and only an error result repeating it counts.
        #expect(CLIAuthFailure.claudeFailureText(assistant(error: "authentication_failed", text: "  Stale  ")) == "Stale")
        #expect(CLIAuthFailure.claudeFailureText(assistant(error: "rate_limit", text: "Stale")) == nil)
        #expect(CLIAuthFailure.claude(result: ["type": "result", "is_error": true, "result": "Stale\n"], repeating: "Stale"))
        #expect(!CLIAuthFailure.claude(result: ["type": "result", "is_error": true, "result": "Stale"], repeating: nil))
        #expect(!CLIAuthFailure.claude(result: ["type": "result", "is_error": true, "result": ""], repeating: ""))
        #expect(!CLIAuthFailure.claude(result: ["type": "result", "is_error": false, "result": "Stale"], repeating: "Stale"))
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
        // Antigravity's status is a live call: only signed out asks for a sign-in.
        #expect(!CLIAuthFailure.signInCanFix(CLIAccountStatus(provider: "gemini", loggedIn: true, method: "Google account", methodId: CLIAccountMethod.geminiGoogle)))
        #expect(CLIAuthFailure.signInCanFix(CLIAccountStatus(provider: "gemini", loggedIn: false, method: CLIAccountSupport.geminiGoogleMethod, methodId: CLIAccountMethod.geminiGoogle)))
        #expect(!CLIAuthFailure.signInCanFix(CLIAccountStatus(provider: "gemini", detail: "offline")))
        for json in [#"{"loggedIn":true,"authMethod":"third_party","apiProvider":"bedrock"}"#, #"{"loggedIn":true,"apiProvider":"vertex"}"#,
                     #"{"loggedIn":true,"apiProvider":"foundry"}"#, #"{"loggedIn":true,"authMethod":"api_key"}"#] {
            #expect(!CLIAuthFailure.signInCanFix(CLIAccountSupport.parseClaudeStatus(Data(json.utf8))), "\(json)")
        }
        #expect(CLIAuthFailure.signInCanFix(CLIAccountSupport.parseClaudeStatus(Data(#"{"loggedIn":true,"authMethod":"claude.ai"}"#.utf8))))
    }
}

struct CLIAntigravityAuthFailureTests {
    private func send(_ value: [String: Any], to parser: CLIStreamParser) throws {
        parser.push(try JSONSerialization.data(withJSONObject: value)); parser.push("\n")
    }
    private func antigravity() -> CLIStreamParser { CLIStreamParser(provider: "gemini", log: { _, _ in }, resume: { _ in }) }

    /// agy's own sign-in failures (its print-mode and missing-credentials errors).
    @Test func antigravityMatchesItsSignInFailures() {
        for text in ["Error: authentication required. Run 'agy' to log in, then retry.", "Error: authentication required. Run 'agy' to log in.",
                     "Print mode: auth error: oauth2: token expired"] {
            #expect(CLIAuthFailure.antigravity(text: text), "\(text)")
        }
        for text in ["warning: conversation \"0000\" not found", "Error: --print took \"--input-format\" as its prompt",
                     "jetski: no output produced — a tool required the \"command\" permission that headless mode cannot prompt for, so it was auto-denied.",
                     "no model configuration is available for this account", "401 files changed",
                     "Print mode: auth timed out", "407 Proxy Authentication Required", "authentication required"] {
            #expect(!CLIAuthFailure.antigravity(text: text), "\(text)")
        }
    }

    @Test func aRunThatCannotSignInIsSomethingASignInCanFix() {
        #expect(CLIAuthFailure.signInCanFix(CLIAccountSupport.antigravityStatus(exitCode: 1, output: "Error: authentication required. Run 'agy' to log in.")))
        #expect(!CLIAuthFailure.signInCanFix(CLIAccountSupport.antigravityStatus(exitCode: 0, output: "")))
        #expect(!CLIAuthFailure.signInCanFix(CLIAccountStatus(provider: "gemini", installed: false, loggedIn: false)))
        let gate = CLIAutoLoginGate()
        #expect(gate.shouldStart(provider: "gemini", enabled: true, status: CLIAccountSupport.antigravityStatus(exitCode: 1, output: "Error: authentication required. Run 'agy' to log in."), loginActive: false))
        #expect(!gate.shouldStart(provider: "gemini", enabled: true, status: CLIAccountSupport.antigravityStatus(exitCode: 0, output: ""), loginActive: false))
    }

    @Test func theParserRaisesTheSignalFromStderrPlainLinesAndFailedResultsOnly() throws {
        // No sign-in: agy says so on stderr, before any JSON.
        let missing = antigravity()
        missing.receiveStderr("Error: authentication requ")
        missing.receiveStderr("ired. Run 'agy' to log in, then retry.\n")
        #expect(missing.authFailure)
        // A last line without a newline counts once the process ended.
        let unfinished = antigravity()
        unfinished.receiveStderr("Print mode: auth error: token revoked")
        #expect(!unfinished.authFailure)
        unfinished.finishStderr()
        #expect(unfinished.authFailure)
        let plain = antigravity()
        plain.push("Error: authentication required. Run 'agy' to log in, then retry.\n")
        #expect(plain.authFailure)
        // A cut-off JSON line quoting the words never counts.
        let cut = antigravity()
        cut.push(#"{"event":"step_update","step_update":{"text_delta":"Error: authentication required. Run 'agy' to log in"# + "\n")
        #expect(!cut.authFailure)
        // A failed result saying so counts; another failure does not.
        let rejected = antigravity()
        try send(["event": "result", "result": ["status": "ERROR", "error": "Print mode: auth error: token revoked"]], to: rejected)
        #expect(rejected.failed && rejected.authFailure)
        let quota = antigravity()
        try send(["event": "result", "result": ["status": "ERROR", "error": ["message": "Resource has been exhausted"]]], to: quota)
        #expect(quota.failed && !quota.authFailure)
        // The phrase in the model's own text or a tool's output, or another CLI's stderr, never counts.
        let quoted = antigravity()
        try send(["event": "step_update", "step_update": ["step_index": 1, "state": "DONE", "step_type": "agent_response", "text_delta": "Error: authentication required. Run 'agy' to log in."]], to: quoted)
        try send(["event": "step_update", "step_update": ["step_index": 2, "state": "DONE", "step_type": "tool", "tool_info": ["name": "run_command", "output": "Error: authentication required. Run 'agy' to log in."]]], to: quoted)
        #expect(!quoted.authFailure)
        let claude = CLIStreamParser(provider: "claude", log: { _, _ in }, resume: { _ in })
        claude.receiveStderr("Error: authentication required. Run 'agy' to log in.\n")
        #expect(!claude.authFailure)
        // The run's last word decides: a later successful result clears it.
        let recovered = antigravity()
        recovered.receiveStderr("Print mode: auth error: token revoked\n")
        #expect(recovered.authFailure)
        try send(["event": "result", "result": ["status": "SUCCESS", "response": "ok"]], to: recovered)
        #expect(!recovered.authFailure)
    }
}

struct CLIAntigravityLoginTests {
    private let signedOut = CLIAccountStatus(provider: "gemini", loggedIn: false, method: CLIAccountSupport.geminiGoogleMethod, methodId: CLIAccountMethod.geminiGoogle, canSignOut: false)
    private let signedIn = CLIAccountStatus(provider: "gemini", loggedIn: true, method: CLIAccountSupport.geminiGoogleMethod, methodId: CLIAccountMethod.geminiGoogle, canSignOut: false)

    @Test func theWaitEndsSignedInClosedOrTimedOut() async throws {
        let reads = LockedBox(0)
        let signedOut = signedOut, signedIn = signedIn
        let status: @Sendable () async -> CLIAccountStatus = { reads.update { $0 += 1 }; return signedOut }
        // The status is read once per interval while the terminal stays open, then once more at the limit.
        let idle = await CLIAntigravityLogin.wait(status: status, isOpen: { true }, interval: 10, limit: 0.3, tick: 0.02)
        #expect(idle == .timedOut(signedOut))
        #expect(reads.get() == 1)
        // The terminal closed without a sign-in.
        let closed = await CLIAntigravityLogin.wait(status: status, isOpen: { false }, interval: 10, limit: 20, tick: 0.02)
        #expect(closed == .exited(signedOut))
        // The status turns signed in while the terminal is open.
        let turned = LockedBox(false)
        let task = Task { await CLIAntigravityLogin.wait(status: { turned.get() ? signedIn : signedOut }, isOpen: { true }, interval: 0.05, limit: 20, tick: 0.02) }
        try await Task.sleep(for: .milliseconds(100))
        turned.update { $0 = true }
        #expect(await task.value == .loggedIn(signedIn))
        // Another provider's status never counts.
        let other = await CLIAntigravityLogin.wait(status: { CLIAccountStatus(provider: "claude", loggedIn: true) }, isOpen: { true }, interval: 0, limit: 0.1, tick: 0.02)
        #expect(other == .timedOut(CLIAccountStatus(provider: "claude", loggedIn: true)))
        let cancelled = Task { await CLIAntigravityLogin.wait(status: status, isOpen: { true }, interval: 1, limit: 20, tick: 0.02) }
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
        // Antigravity starts its terminal sign-in through the same gate, and a status of another provider never counts.
        #expect(gate.shouldStart(provider: "gemini", enabled: true, status: CLIAccountStatus(provider: "gemini", loggedIn: false), loginActive: false, now: start))
        // An Antigravity status that cannot be read (offline) never opens a sign-in.
        #expect(!gate.shouldStart(provider: "gemini", enabled: true, status: CLIAccountStatus(provider: "gemini"), loginActive: false, now: start))
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
