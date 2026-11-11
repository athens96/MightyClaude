using System.Collections.Concurrent;
using System.Text.Json;
using MightyCode.Core;

internal static class CliLoginRecoveryVerification
{
    private static void Check(bool value, string why) { if (!value) throw new InvalidOperationException(why); }
    internal static Task ErrorSignalsExcludeContentAndExternalProviders()
    {
        using var ordinary = JsonDocument.Parse("""{"type":"assistant","message":{"content":[{"type":"text","text":"Please run /login"}]}}""");
        Check(!CliAuthFailure.Claude(ordinary.RootElement), "normal assistant prose cannot request a sign-in");
        using var error = JsonDocument.Parse("""{"type":"assistant","error":"authentication_failed","message":{"content":[{"type":"text","text":"OAuth token has expired"}]}}""");
        Check(CliAuthFailure.Claude(error.RootElement), "CLI-marked authentication error qualifies");
        Check(!CliAuthFailure.Codex("Reconnecting... 2/5 (401 Unauthorized)") && CliAuthFailure.Codex("401 Unauthorized"), "retry notices must not invalidate a live turn");
        Check(!CliAuthFailure.Claude("Bedrock service control policy explicit deny, invalid API key"), "organization policy cannot be fixed by browser sign-in");
        foreach (var method in new[] { "AWS Bedrock", "Google Vertex AI", "Microsoft Foundry", "Anthropic API key" })
            Check(!CliAuthFailure.SignInCanFix(new() { Provider = "claude", Method = method, AccessVerified = false, LoggedIn = true }), "external configuration is not a browser login: " + method);
        Check(!CliAuthFailure.SignInCanFix(new() { Provider = "codex", Method = "any label", MethodId = CliAccountMethod.CodexApiKey }), "Codex API-key login is excluded");
        Check(!CliAuthFailure.SignInCanFix(CliAccountSupport.ParseCodexStatus("Logged in using an API key - sk-***", null)), "a parsed Codex API-key login is excluded, whatever its label reads");
        Check(CliAuthFailure.SignInCanFix(new() { Provider = "codex", Method = CliAccountSupport.CodexApiKeyMethod, MethodId = CliAccountMethod.CodexChatGpt }), "the decision reads the method id, not the label");
        Check(!CliAuthFailure.SignInCanFix(new() { Provider = "gemini", Method = CliAccountSupport.GeminiGoogleMethod, MethodId = CliAccountMethod.GeminiGoogle }), "an Antigravity status that cannot tell opens no sign-in");
        Check(CliAuthFailure.SignInCanFix(new() { Provider = "claude", LoggedIn = true }) && CliAuthFailure.SignInCanFix(new() { Provider = "codex" }), "stale persisted credentials or unknown status do not rule out login recovery");
        return Task.CompletedTask;
    }
    internal static Task AutomaticStartOncePerProviderWithCooldown()
    {
        var gate = new CliAutoLoginGate(); var now = DateTimeOffset.UnixEpoch.AddDays(10000);
        var claude = new CliAccountStatus { Provider = "claude", LoggedIn = true, Method = "claude.ai" };
        var codex = new CliAccountStatus { Provider = "codex" };
        Check(gate.ShouldStart("claude", true, claude, false, now), "a lost sign-in starts one sign-in");
        Check(!gate.ShouldStart("claude", true, claude, true, now), "panes failing while the provider's sign-in runs join it instead of starting another");
        Check(!gate.ShouldStart("claude", false, claude, false, now), "the switch off leaves only the card");
        Check(gate.ShouldStart("gemini", true, new() { Provider = "gemini", LoggedIn = false }, false, now) && !gate.ShouldStart("gemini", true, claude, false, now) && !gate.ShouldStart("codex", true, claude, false, now), "Claude, Codex and Antigravity, each decided on its own status");
        Check(!gate.ShouldStart("other", true, new() { Provider = "other" }, false, now), "a provider without login recovery never starts one");
        Check(!gate.ShouldStart("claude", true, claude with { Method = "AWS Bedrock", AccessVerified = false }, false, now)
            && !gate.ShouldStart("codex", true, codex with { Method = CliAccountSupport.CodexApiKeyMethod, MethodId = CliAccountMethod.CodexApiKey }, false, now)
            && !gate.ShouldStart("claude", true, claude with { Installed = false }, false, now), "methods a browser sign-in cannot renew never start one");
        gate.Stopped("claude", now);
        Check(!gate.ShouldStart("claude", true, claude, false, now) && !gate.ShouldStart("claude", true, claude, false, now + CliAutoLoginGate.Cooldown - TimeSpan.FromSeconds(1)), "a failed or cancelled sign-in waits for the cooldown");
        Check(gate.ShouldStart("codex", true, codex, false, now), "the cooldown is per provider");
        Check(gate.ShouldStart("claude", true, claude, false, now + CliAutoLoginGate.Cooldown) && CliAutoLoginGate.Cooldown == TimeSpan.FromMinutes(2), "a new failure after two minutes starts again");
        var approved = now + TimeSpan.FromMinutes(10); gate.Succeeded("claude", approved);
        Check(!gate.ShouldStart("claude", true, claude, false, approved + TimeSpan.FromSeconds(5), sentAt: approved + TimeSpan.FromSeconds(1))
            && !gate.ShouldStart("claude", true, claude, false, approved + CliAutoLoginGate.Cooldown - TimeSpan.FromSeconds(1)), "a success holds automatic starts for the cooldown, so a failure right after approval never reopens the browser");
        Check(gate.ShouldStart("claude", true, claude, false, approved + CliAutoLoginGate.Cooldown, sentAt: approved + CliAutoLoginGate.Cooldown), "a new failure after the hold starts again");
        Check(!gate.ShouldStart("claude", true, claude, false, approved + TimeSpan.FromHours(1), sentAt: approved - TimeSpan.FromSeconds(1)), "a run that started before the last sign-in only raises the card");
        Check(!gate.ShouldStart("claude", true, claude, false, approved + TimeSpan.FromHours(1), resent: true, sentAt: approved + TimeSpan.FromHours(1)), "a request login recovery resent only raises the card");
        var book = new CliLoginRetryBook(); var at = DateTimeOffset.UnixEpoch.AddDays(1);
        book.ExpectResend("pane"); book.Sent(new StartRunRequest("pane", "workspace", "claude", "again", []), at);
        Check(book.Settled("pane") is { Resent: true } resentRetry && resentRetry.SentAt == at, "login recovery's own resend is marked with its start time");
        book.Sent(new StartRunRequest("pane", "workspace", "claude", "typed", []), at);
        Check(book.Settled("pane") is { Resent: false }, "the mark is used once; the user's next send is not a resend");
        book.ExpectResend("pane"); book.CancelResend("pane"); book.Sent(new StartRunRequest("pane", "workspace", "claude", "typed", []));
        Check(book.Settled("pane") is { Resent: false }, "a resend that never started leaves no mark");
        Check(new AppSnapshot().AutoLoginCLIs != false && JsonSerializer.Deserialize<AppSnapshot>("{}", Wire.Json)!.AutoLoginCLIs != false, "absent preference must default on");
        foreach (var invalid in new[] { "\"false\"", "0", "1", "null", "[]" })
            Check(JsonSerializer.Deserialize<AppSnapshot>("{\"autoLoginCLIs\":" + invalid + "}", Wire.Json)!.AutoLoginCLIs is null, "only a JSON boolean may switch automatic sign-in: " + invalid);
        var saved = JsonSerializer.Serialize(StateStore.Normalize(new AppSnapshot { AutoLoginCLIs = false }, restoring: true), Wire.Json);
        Check(saved.Contains("\"autoLoginCLIs\":false", StringComparison.Ordinal) && JsonSerializer.Deserialize<AppSnapshot>(saved, Wire.Json)!.AutoLoginCLIs == false, "an explicit off must survive normalization and a restart under the Mac's key; got " + saved);
        return Task.CompletedTask;
    }
    internal static Task OutputHandlesPartialUrlsAndCodePrompts()
    {
        var parser = new CliLoginOutputParser();
        parser.Append("https://docs.example.com/help\nhttps://auth.openai.com/oauth?state=abc");
        Check(parser.Current.Url?.Host == "docs.example.com", "incomplete URL remains pending");
        parser.Append("DEF\nPaste code here >");
        Check(parser.Current.Url?.Query == "?state=abcDEF" && parser.Current.AsksForCode, "complete auth host replaces earlier docs URL and detects prompt");
        parser = new(); parser.Append("\u001b]8;;https://claude.ai/oauth?state=fixture\u0007Sign in\u001b]8;;\u0007");
        Check(parser.Current.Url?.Host == "claude.ai", "OSC hyperlink retains its target");
        parser = new(); parser.Append("Enter this code in your browser: ABC123\nhttps://auth.openai.com/device");
        Check(!parser.Current.AsksForCode && parser.Current.Url is null, "displayed code is not a code-input prompt and end-of-output URL waits");
        parser.Finish(); Check(parser.Current.Url?.AbsolutePath == "/device", "process completion finishes trailing URL");
        parser.Clear(); parser.Append(new string('가', CliLoginOutputParser.MaximumBytes)); parser.Append("\nhttps://evil.example.com/\nPaste code");
        Check(parser.Current.Url is null && !parser.Current.AsksForCode, "UTF-8 byte cap bounds hostile output");
        return Task.CompletedTask;
    }
    internal static Task RetryGenerationPreservesAttachmentsAndDropsStaleWork()
    {
        var book = new CliLoginRetryBook();
        var request = new StartRunRequest("pane", "workspace", "claude", "first", [], Provider: "codex");
        book.Sent(request); var failed = book.Settled("pane")!;
        Check(book.Remember(failed) && book.Requests.Count == 1, "failed request retained for retry");
        book.Sent(request with { Input = "second" });
        Check(book.Requests.Count == 0 && !book.Remember(failed), "new prompt invalidates previous failure and async confirmation");
        var second = book.Settled("pane")!; book.Remember(second); book.Drop("pane");
        Check(!book.Remember(second) && book.Take("pane") is null, "dismissed or closed pane cannot be resurrected by late status");
        book.Sent(request); var third = book.Settled("pane")!; book.Remember(third);
        Check(book.Take("pane")?.Request.Input == "first" && book.Take("pane") is null, "retry consumed once");
        return Task.CompletedTask;
    }
    /// <summary>
    /// Antigravity CLI's own sign-in failures (M/MightyCore/Tests AntigravityTests.failedCancelledAndSignedOutTurnsEndTheirOwnWay and
    /// statusComesFromTheModelListAndThereIsNoSignOut): only agy's own words, and only a status that reads signed out asks for a sign-in.
    /// </summary>
    internal static Task GeminiSignalsAndMethods()
    {
        foreach (var text in new[] { "Error: authentication required. Run 'agy' to log in, then retry.", "Print mode: auth error: token expired" })
            Check(CliAuthFailure.Antigravity(text) && CliAuthFailure.Matches("gemini", text), "antigravity sign-in failure: " + text);
        // Only agy's own wording: not a timed-out sign-in, a proxy's 407, a missing conversation or the bare phrase.
        foreach (var text in new[] { "warning: conversation \"x\" not found", "Print mode: auth timed out", "HTTP 407 Proxy Authentication Required", "authentication required", "401 files changed" })
            Check(!CliAuthFailure.Antigravity(text), "not an antigravity sign-in failure: " + text);
        Check(!CliAuthFailure.Matches("claude", "Print mode: auth error") && !CliAuthFailure.Matches("other", "authentication required. Run 'agy'"), "each provider is matched by its own rules");
        Check(CliAuthFailure.AntigravitySucceeded("{\"event\":\"result\",\"result\":{\"status\":\"SUCCESS\"}}") && !CliAuthFailure.AntigravitySucceeded("{\"event\":\"result\",\"result\":{\"status\":\"ERROR\"}}")
            && !CliAuthFailure.AntigravitySucceeded("{\"event\":\"step_update\",\"step_update\":{\"text_delta\":\"\\\"result\\\"\"}}") && !CliAuthFailure.AntigravitySucceeded("{\"result\""), "only a successful result line clears the signal");
        var signedIn = CliAccountSupport.AntigravityStatus(0, "gemini-3.8-flash-low\tGemini 3.8 Flash (Low)\n");
        var signedOut = CliAccountSupport.AntigravityStatus(1, "Error: authentication required. Run 'agy' to log in, then retry.\n");
        var offline = CliAccountSupport.AntigravityStatus(1, "dial tcp: lookup failed");
        // A live status: only signed out asks for a sign-in; offline (unknown) never opens one.
        Check(!CliAuthFailure.SignInCanFix(signedIn) && CliAuthFailure.SignInCanFix(signedOut) && !CliAuthFailure.SignInCanFix(offline), "only a signed-out status can be fixed by signing in");
        Check(!CliAuthFailure.SignInCanFix(signedOut with { Installed = false }), "no CLI, no sign-in");
        var gate = new CliAutoLoginGate(); var now = DateTimeOffset.UnixEpoch.AddDays(10000);
        Check(!gate.ShouldStart("gemini", true, offline, false, now) && !gate.ShouldStart("gemini", true, signedIn, false, now), "an unknown or signed-in status never opens the sign-in terminal");
        Check(gate.ShouldStart("gemini", true, signedOut, false, now) && !gate.ShouldStart("gemini", true, signedOut, true, now), "one Antigravity sign-in at a time");
        gate.Stopped("gemini", now);
        Check(!gate.ShouldStart("gemini", true, signedOut, false, now + CliAutoLoginGate.Cooldown - TimeSpan.FromSeconds(1)) && gate.ShouldStart("gemini", true, signedOut, false, now + CliAutoLoginGate.Cooldown), "a cancelled Antigravity sign-in waits for the cooldown");
        gate.Succeeded("gemini", now + TimeSpan.FromHours(1));
        Check(!gate.ShouldStart("gemini", true, signedOut, false, now + TimeSpan.FromHours(2), sentAt: now + TimeSpan.FromMinutes(59)), "an Antigravity run older than the last sign-in only raises the card");
        var book = new CliLoginRetryBook();
        book.Sent(new StartRunRequest("pane", "workspace", "claude", "again", [], Provider: "gemini"));
        Check(book.Settled("pane") is { Request.Provider: "gemini" }, "an Antigravity request is kept for its retry");
        return Task.CompletedTask;
    }
    /// <summary>The terminal sign-in waits for its provider's status to read signed in (M/MightyCore/Tests AntigravityTests.theTerminalSignInWaitsForTheStatus, CLITerminalLoginTests).</summary>
    internal static async Task AntigravitySignInIsItsStatusReadingSignedIn()
    {
        var signedOut = new CliAccountStatus { Provider = "gemini", LoggedIn = false, MethodId = CliAccountMethod.GeminiGoogle, CanSignOut = false };
        var signedIn = signedOut with { LoggedIn = true };
        var reads = 0;
        var outcome = await CliTerminalSignIn.WaitAsync("gemini", _ => Task.FromResult(++reads >= 3 ? signedIn : signedOut), () => true, interval: TimeSpan.Zero, limit: TimeSpan.FromSeconds(20), tick: TimeSpan.FromMilliseconds(10));
        Check(outcome.Outcome == CliLoginOutcome.LoggedIn && outcome.Status == signedIn, "the status reading signed in ends the wait signed in");
        var closed = await CliTerminalSignIn.WaitAsync("gemini", _ => Task.FromResult(signedOut), () => false, interval: TimeSpan.FromSeconds(60), tick: TimeSpan.FromMilliseconds(10));
        Check(closed.Outcome == CliLoginOutcome.Exited && closed.Status == signedOut, "a closed terminal without a sign-in ends the wait");
        var expired = await CliTerminalSignIn.WaitAsync("gemini", _ => Task.FromResult(signedOut), () => true, interval: TimeSpan.FromSeconds(60), limit: TimeSpan.FromMilliseconds(50), tick: TimeSpan.FromMilliseconds(10));
        Check(expired.Outcome == CliLoginOutcome.TimedOut, "the limit ends the wait");
        var polled = 0;
        var slow = await CliTerminalSignIn.WaitAsync("gemini", _ => { polled++; return Task.FromResult(signedOut); }, () => true, interval: TimeSpan.FromSeconds(60), limit: TimeSpan.FromMilliseconds(200), tick: TimeSpan.FromMilliseconds(10));
        Check(slow.Outcome == CliLoginOutcome.TimedOut && polled == 1, "the status is read only every interval, and once more at the limit; got " + polled);
        // Another provider's status never counts.
        var claude = new CliAccountStatus { Provider = "claude", LoggedIn = true };
        var other = await CliTerminalSignIn.WaitAsync("gemini", _ => Task.FromResult(claude), () => true, interval: TimeSpan.Zero, limit: TimeSpan.FromMilliseconds(100), tick: TimeSpan.FromMilliseconds(10));
        Check(other.Outcome == CliLoginOutcome.TimedOut && other.Status == claude, "another provider's sign-in is not this one's");
        var pi = await CliTerminalSignIn.WaitAsync("pi", _ => Task.FromResult(new CliAccountStatus { Provider = "pi", LoggedIn = true }), () => true, interval: TimeSpan.Zero, limit: TimeSpan.FromSeconds(20), tick: TimeSpan.FromMilliseconds(10));
        Check(pi.Outcome == CliLoginOutcome.LoggedIn, "pi's terminal sign-in ends with pi's status");
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        try { await CliTerminalSignIn.WaitAsync("gemini", _ => Task.FromResult(signedOut), () => true, cancel.Token); throw new InvalidOperationException("cancel was ignored"); } catch (OperationCanceledException) { }
        var plan = CliAccountTerminal.LaunchPlan(CliAccountSupport.LoginArguments("gemini")!, windowsTerminalAvailable: true, directory: @"C:\Projects\app");
        Check(plan.Arguments.SequenceEqual(["new-tab", "-d", @"C:\Projects\app", "--", "agy"]), "the Antigravity sign-in terminal starts in the pane's workspace");
        Check(CliAccountTerminal.LaunchPlan(["agy"], true, @"C:\a;b").Arguments.SequenceEqual(["new-tab", "--", "agy"]), "a folder Windows Terminal would split is not passed on");
        Check(CliAccountTerminal.LaunchPlan(["agy"], true, @"C:\work", @"C:\Tools;x\agy.exe") is (var host, var resolved) && host == CliAccountTerminal.ConsoleHost && resolved.SequenceEqual([@"C:\Tools;x\agy.exe"])
            && CliAccountTerminal.LaunchPlan(["agy", "a;b"], true).Executable == CliAccountTerminal.ConsoleHost && CliAccountTerminal.LaunchPlan(["agy"], true, null, @"C:\Tools\agy.exe").Executable == "wt.exe",
            "a command Windows Terminal would split runs in conhost");
        Check(CliAccountTerminal.LaunchPlan(["agy"], false, @"C:\Projects\app").Arguments.SequenceEqual(["agy"]), "the console host inherits the folder instead");
        Check(CliAccountTerminal.LaunchPlan([@"C:\Users\me\AppData\Local\agy\bin\agy.exe"], false, null, @"C:\Users\me\AppData\Local\agy\bin\agy.exe").Arguments.SequenceEqual([@"C:\Users\me\AppData\Local\agy\bin\agy.exe"]),
            "the console host runs agy by its full path");
    }
    internal static async Task GeminiFakeCliRunRaisesTheReasonFromStderrOnly()
    {
        var directory = Verification.Temp(); var workspace = new Workspace { Path = directory };
        try
        {
            foreach (var (flag, expected) in new[] { ("--agy-signin-lost", (string?)"authentication"), ("--agy-quoted", null) })
            {
                var events = new ConcurrentQueue<RunEvent>();
                await using var catalog = new ProviderCatalog((_, _) => Task.FromResult<CliCommand?>(Verification.Self("--fake-cli", "gemini", Path.Combine(directory, flag.TrimStart('-')), flag)));
                await using var manager = new RunManager(_ => Task.FromResult(workspace), catalog, "", events.Enqueue);
                await manager.StartAsync(new("gemini-pane", workspace.Id, "claude", "fix the bug", [], Provider: "gemini"));
                await Verification.Until(() => events.Any(e => e.Type == "status" && e.Status is "completed" or "error" or "stopped"), 20000);
                var last = events.Last(e => e.Type == "status");
                Check(last.Status == "error" && last.Reason == expected, $"{flag}: expected error with reason {expected ?? "none"}, got {last.Status} with {last.Reason ?? "none"}");
            }
        }
        finally { Directory.Delete(directory, true); }
    }
    /// <summary>
    /// A Claude run whose sign-in expired: the CLI marks the failed message authentication_failed and copies its text
    /// into the error result, so the result keeps the reason whatever the wording; unmarked, the same words do not.
    /// </summary>
    internal static async Task ClaudeFakeCliRunKeepsTheReasonWhenTheResultRepeatsTheMarkedFailure()
    {
        var directory = Verification.Temp(); var workspace = new Workspace { Path = directory };
        try
        {
            var plugin = Path.Combine(directory, "plugin"); Directory.CreateDirectory(Path.Combine(plugin, ".claude-plugin"));
            await File.WriteAllTextAsync(Path.Combine(plugin, ".claude-plugin", "plugin.json"), "{}");
            foreach (var (prompt, expected) in new[] { ("expired sign-in fixture", (string?)"authentication"), ("reworded sign-in fixture", "authentication"), ("child sign-in fixture", "authentication"),
                ("unmarked sign-in fixture", null), ("mismatched sign-in fixture", null), ("denied sign-in fixture", null) })
            {
                var events = new ConcurrentQueue<RunEvent>();
                await using var catalog = new ProviderCatalog((_, _) => Task.FromResult<CliCommand?>(Verification.Self("--fake-cli", "claude", Path.Combine(directory, prompt.Split(' ')[0]))));
                await using var manager = new RunManager(_ => Task.FromResult(workspace), catalog, plugin, events.Enqueue, _ => { });
                await manager.StartAsync(new("claude-pane", workspace.Id, "claude", prompt, []));
                await Verification.Until(() => events.Any(e => e.Type == "status" && e.Status is "completed" or "error" or "stopped"), 20000);
                var last = events.Last(e => e.Type == "status");
                var logs = string.Join(" | ", events.Where(e => e.Type == "log" && e.Entry is { Kind: "error" or "system" }).Select(e => e.Entry!.Text));
                Check(last.Status == "error" && last.Reason == expected, $"{prompt}: expected error with reason {expected ?? "none"}, got {last.Status} with {last.Reason ?? "none"}; logs: {logs}");
            }
        }
        finally { try { Directory.Delete(directory, true); } catch (IOException) { } }
        Check(CliAuthFailure.Claude("Failed to authenticate: OAuth session expired and could not be refreshed"), "a headless run's expired sign-in wording qualifies on its own");
        using var marked = JsonDocument.Parse("""{"type":"assistant","error":"authentication_failed","message":{"content":[{"type":"text","text":"  Stale  "}]}}""");
        using var limited = JsonDocument.Parse("""{"type":"assistant","error":"rate_limit","message":{"content":[{"type":"text","text":"Stale"}]}}""");
        using var repeated = JsonDocument.Parse("""{"type":"result","is_error":true,"result":"Stale\n"}""");
        using var succeeded = JsonDocument.Parse("""{"type":"result","is_error":false,"result":"Stale"}""");
        Check(CliAuthFailure.ClaudeFailureText(marked.RootElement) == "Stale" && CliAuthFailure.ClaudeFailureText(limited.RootElement) is null, "only a message marked authentication_failed is remembered, trimmed");
        Check(CliAuthFailure.Claude(repeated.RootElement, "Stale") && !CliAuthFailure.Claude(repeated.RootElement, null) && !CliAuthFailure.Claude(repeated.RootElement, "")
            && !CliAuthFailure.Claude(succeeded.RootElement, "Stale"), "only an error result repeating a remembered message keeps the signal");
    }
    internal static async Task WaitRequiresFreshSuccessWhenAlreadySignedIn()
    {
        var reads = 0;
        Task<CliAccountStatus> SignedIn(CancellationToken _) { reads++; return Task.FromResult(new CliAccountStatus { Provider = "codex", LoggedIn = true }); }
        var expired = await CliLoginWait.RunAsync(() => true, () => null, SignedIn, limit: TimeSpan.FromMilliseconds(15), tick: TimeSpan.FromMilliseconds(2));
        Check(expired.Outcome == CliLoginOutcome.TimedOut && reads == 2, "stale signed-in status cannot auto-resend while login still runs");
        var failed = await CliLoginWait.RunAsync(() => false, () => 1, SignedIn, tick: TimeSpan.FromMilliseconds(1));
        Check(failed.Outcome == CliLoginOutcome.Exited, "nonzero exit cannot confirm an already-signed-in account");
        var passed = await CliLoginWait.RunAsync(() => false, () => 0, SignedIn, tick: TimeSpan.FromMilliseconds(1));
        Check(passed.Outcome == CliLoginOutcome.LoggedIn, "zero exit plus signed-in status confirms recovery");
        reads = 0;
        var transition = await CliLoginWait.RunAsync(() => true, () => null, _ => Task.FromResult(new CliAccountStatus { Provider = "claude", LoggedIn = ++reads > 1 }), interval: TimeSpan.Zero, tick: TimeSpan.FromMilliseconds(1));
        Check(transition.Outcome == CliLoginOutcome.LoggedIn, "signed-out to signed-in transition confirms a still-running login");
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        try { await CliLoginWait.RunAsync(() => true, () => null, SignedIn, cancel.Token); throw new InvalidOperationException("cancel was ignored"); } catch (OperationCanceledException) { }
    }

    private const string AgySignedOut = "Fetching available models...\nError: Please sign in to view available models. Launch the CLI without arguments to sign in.";

    /// <summary>agy 1.5.x says it is signed out with exit 0, and its newer phrases are a lost sign-in (M/MightyCore/Tests CLITerminalSignInTests).</summary>
    internal static Task AgySignedOutWithExitZeroReadsSignedOut()
    {
        Check(CliAccountSupport.AntigravityStatus(0, AgySignedOut).LoggedIn == false, "exit 0 with its sign-in words is signed out");
        Check(CliAccountSupport.AntigravityStatus(1, AgySignedOut).LoggedIn == false, "and with exit 1");
        Check(CliAccountSupport.AntigravityStatus(0, "Fetching available models...\ngemini-3.8-flash-low\tGemini 3.8 Flash (Low)\n").LoggedIn == true, "a clean list with exit 0 is signed in");
        Check(CliAccountSupport.AntigravityStatus(-1, "").LoggedIn is null && CliAccountSupport.AntigravityStatus(0, "", timedOut: true).LoggedIn is null, "a failure or a timeout stays unknown");
        Check(CliAuthFailure.SignInCanFix(CliAccountSupport.AntigravityStatus(0, AgySignedOut)), "a sign-in can fix it");
        foreach (var text in new[] { "authentication failed or timed out", "Error: Please sign in to view available models.", "You are not signed in", AgySignedOut })
            Check(CliAuthFailure.Antigravity(text) && CliAuthFailure.Matches("gemini", text), "agy's newer words: " + text);
        Check(!CliAuthFailure.Antigravity("407 Proxy Authentication Required"), "a proxy's 407 is not a lost sign-in");
        return Task.CompletedTask;
    }

    private static string PiMessageEnd(string? error) => JsonSerializer.Serialize(new { type = "message_end", message = new { role = "assistant", content = Array.Empty<object>(), stopReason = error is null ? "stop" : "error", errorMessage = error } }, Wire.Json);

    /// <summary>pi's own sign-in failures, read from its RPC stream where the Mac's parser reads them, and the table of terminal sign-ins.</summary>
    internal static Task PiSignInFailuresAndTheTerminalTable()
    {
        foreach (var text in new[] { "No API key found for anthropic. Use /login or set ANTHROPIC_API_KEY.", "No models available. Use /login or set an API key.", "Please use /login" })
            Check(CliAuthFailure.Pi(text), "pi's sign-in failure: " + text);
        foreach (var text in new[] { "rate limit exceeded", "407 Proxy Authentication Required", "context window exceeded" })
            Check(!CliAuthFailure.Pi(text), "not pi's sign-in failure: " + text);
        var rejected = new OutputParser("pi", (_, _) => { }, _ => { });
        rejected.Parse("""{"type":"response","command":"prompt","success":false,"error":"No API key found for anthropic. Use /login or set ANTHROPIC_API_KEY."}""");
        Check(rejected.Failed && rejected.AuthenticationFailure, "a prompt pi refuses for its sign-in is a lost sign-in");
        var other = new OutputParser("pi", (_, _) => { }, _ => { });
        other.Parse("""{"type":"response","command":"prompt","success":false,"error":"rate limit exceeded"}""");
        Check(other.Failed && !other.AuthenticationFailure, "another refusal is not");
        // A message that ended in it, until a retry starts or a later message answers; a retry that gave up on it.
        var turn = new OutputParser("pi", (_, _) => { }, _ => { });
        turn.Parse(PiMessageEnd("No models available. Use /login or set an API key."));
        Check(turn.AuthenticationFailure, "a message that ended in it");
        turn.Parse("""{"type":"auto_retry_start","attempt":1,"maxAttempts":3,"delayMs":1000,"errorMessage":"No models available."}""");
        Check(!turn.AuthenticationFailure, "a retry starting clears it");
        turn.Parse(PiMessageEnd("No API key found for openai. Use /login."));
        turn.Parse(PiMessageEnd(null));
        Check(!turn.AuthenticationFailure, "a later answer clears it");
        turn.Parse("""{"type":"auto_retry_end","success":false,"attempt":3,"finalError":"No API key found for openai. Use /login."}""");
        Check(turn.Failed && turn.AuthenticationFailure, "a retry that gave up on it");
        var claudeParser = new OutputParser("claude", (_, _) => { }, _ => { });
        claudeParser.Parse("""{"type":"response","command":"prompt","success":false,"error":"No API key found. Use /login"}""");
        Check(!claudeParser.AuthenticationFailure, "only pi's stream is read this way");

        Check(CliAuthFailure.SignInCanFix(new() { Provider = "pi", LoggedIn = false }) && !CliAuthFailure.SignInCanFix(new() { Provider = "pi", LoggedIn = true }), "pi: only a signed-out status asks for a sign-in");
        Check(!CliAuthFailure.SignInCanFix(new() { Provider = "pi", Detail = "offline" }) && !CliAuthFailure.SignInCanFix(new() { Provider = "pi", Installed = false, LoggedIn = false }), "unknown or no CLI opens none");
        Check(CliAuthFailure.SignInCanFix(PiAccounts.Status(0, "No models available. Use /login to log in.", null, new Dictionary<string, string>())), "pi's own no-models reading can be fixed by signing in");

        Check(CliAuthFailure.Providers.SequenceEqual(["claude", "codex", "gemini", "pi", "opencode", "hermes"]), "the providers whose lost sign-in raises the card");
        Check(CliTerminalSignIn.Table.Keys.Order().SequenceEqual(["gemini", "hermes", "opencode", "pi"]), "Antigravity, pi, opencode and Hermes sign in in a terminal");
        // M/MightyCore/CLILoginRecovery.swift: opencode asks in `opencode auth login`, Hermes in `hermes model`.
        Check(CliTerminalSignIn.Table["opencode"].Command?.SequenceEqual(["opencode", "auth", "login"]) == true && CliTerminalSignIn.Table["hermes"].Command?.SequenceEqual(["hermes", "model"]) == true
            && CliTerminalSignIn.Table["opencode"].Title == Locale.Get("loginRecovery.opencodeTerminalTitle") && CliTerminalSignIn.Table["hermes"].Title == Locale.Get("loginRecovery.hermesTerminalTitle"), "opencode's and Hermes's terminals");
        Check(CliAuthFailure.Opencode("provider authentication required") && CliAuthFailure.Opencode("Free usage limit reached. Connect a provider to keep going") && CliAuthFailure.Opencode("401 Unauthorized")
            && !CliAuthFailure.Opencode("407 Proxy Authentication Required") && !CliAuthFailure.Opencode("rate limited"), "opencode's sign-in words");
        Check(CliAuthFailure.Hermes("⚠️ provider 'auto' has no endpoint configured, so the request is not sent anywhere") && CliAuthFailure.Hermes("Hermes is not connected to any AI provider yet. Run `hermes model`.")
            && !CliAuthFailure.Hermes("407 Proxy Authentication Required") && !CliAuthFailure.Hermes("The file is written."), "Hermes's sign-in words");
        Check(CliTerminalSignIn.Table["pi"].Command?.SequenceEqual(["pi"]) == true && CliTerminalSignIn.Table["pi"].TitleKey is not null, "pi's terminal runs pi, titled with what to type");
        Check(CliTerminalSignIn.Table["gemini"].Command?.SequenceEqual([ProviderCatalog.Executable("gemini")]) == true, "Antigravity's runs agy");
        Check(CliTerminalSignIn.Table.Values.All(entry => CliAuthFailure.Providers.Contains(entry.Provider) && Locale.Get(entry.WaitingKey) != entry.WaitingKey), "each has its card sentence");
        Check(CliTerminalSignIn.WaitingText("pi") == Locale.Get("loginRecovery.piTerminal") && CliTerminalSignIn.WaitingText("gemini") == Locale.Get("loginRecovery.antigravityTerminal")
            && CliTerminalSignIn.WaitingText("claude") == Locale.Get("loginRecovery.waiting"), "the card's words while it waits");
        Check(CliTerminalSignIn.Table["pi"].Title == Locale.Get("loginRecovery.piTerminalTitle")
            && CliTerminalSignIn.Table["gemini"].Title == Locale.Get("loginRecovery.terminalTitle", new Dictionary<string, string> { ["provider"] = "Antigravity" }), "the terminal's title");
        var book = new CliLoginRetryBook();
        book.Sent(new StartRunRequest("pane", "workspace", "claude", "again", [], Provider: "pi"));
        Check(book.Settled("pane") is { Request.Provider: "pi" }, "a pi request is kept for its retry");
        var gate = new CliAutoLoginGate(); var now = DateTimeOffset.UnixEpoch.AddDays(10000);
        Check(gate.ShouldStart("pi", true, new() { Provider = "pi", LoggedIn = false }, false, now) && !gate.ShouldStart("pi", true, new() { Provider = "pi" }, false, now), "pi's sign-in starts only for a signed-out status");

        // pi.cmd by its full path through cmd /c, in Windows Terminal or the console host; the tab says what to type.
        var shim = @"C:\Users\me\AppData\Roaming\npm\pi.cmd"; var cmd = WindowsCommand.CommandInterpreter();
        var title = CliTerminalSignIn.Table["pi"].Title;
        var tab = CliAccountTerminal.LaunchPlan([shim], true, @"C:\Projects\app", shim, @"C:\wt.exe", title);
        Check(tab.Executable == @"C:\wt.exe" && tab.Arguments.SequenceEqual(["new-tab", "--title", title, "-d", @"C:\Projects\app", "--", cmd, "/c", shim]), "Windows Terminal: " + string.Join(" | ", tab.Arguments));
        var host = CliAccountTerminal.LaunchPlan([shim], false, @"C:\Projects\app", shim, null, title);
        Check(host.Executable == CliAccountTerminal.ConsoleHost && host.Arguments.SequenceEqual([cmd, "/c", shim]), "the console host: " + string.Join(" | ", host.Arguments));
        Check(CliAccountTerminal.LaunchPlan(["agy"], true, null, null, null, "a;b").Arguments.SequenceEqual(["new-tab", "--", "agy"]), "a title Windows Terminal would split is left out");
        return Task.CompletedTask;
    }

    /// <summary>A pi run whose turn ended on its own sign-in failure ends with the authentication reason; another failure does not.</summary>
    internal static async Task PiFakeCliRunLostItsSignIn()
    {
        var directory = Verification.Temp(); var workspace = new Workspace { Path = directory };
        try
        {
            foreach (var (error, expected) in new[] { ("No API key found for anthropic. Use /login or set ANTHROPIC_API_KEY.", (string?)"authentication"), ("rate limit exceeded", null) })
            {
                var fixture = Path.Combine(directory, "pi-" + (expected ?? "other") + ".jsonl");
                await File.WriteAllLinesAsync(fixture, ["""{"id":"s1","type":"response","command":"get_state","success":true,"data":{"sessionId":"0197c1d2-7f3a-7c4e-9b1a-5d2e8f6a4b30"}}""", """{"type":"agent_start"}""", PiMessageEnd(error), """{"type":"agent_end","messages":[]}"""]);
                var events = new ConcurrentQueue<RunEvent>();
                await using var catalog = new ProviderCatalog((_, _) => Task.FromResult<CliCommand?>(Verification.Self(["--fake-cli", "pi", Path.Combine(directory, "record-" + (expected ?? "other")), "--pi-fixture", fixture])));
                await using (var manager = new RunManager(_ => Task.FromResult(workspace), catalog, "", events.Enqueue))
                {
                    await manager.StartAsync(new("pi-pane", workspace.Id, "claude", "hello", [], Provider: "pi"));
                    await Verification.Until(() => events.Any(e => e.Type == "status" && e.Status is "completed" or "error" or "stopped"), 30000);
                }
                var last = events.Last(e => e.Type == "status");
                Check(last.Status == "error" && last.Reason == expected, $"{error}: expected error with reason {expected ?? "none"}, got {last.Status} with {last.Reason ?? "none"}");
            }
        }
        finally { try { Directory.Delete(directory, true); } catch (IOException) { } }
    }
}
