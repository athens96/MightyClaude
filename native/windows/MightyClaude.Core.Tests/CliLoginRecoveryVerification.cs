using System.Collections.Concurrent;
using System.Text.Json;
using MightyClaude.Core;

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
    /// <summary>The terminal sign-in waits for agy's status to read signed in (M/MightyCore/Tests AntigravityTests.theTerminalSignInWaitsForTheStatus).</summary>
    internal static async Task GeminiSignInIsTheCredentialsFileChanging()
    {
        var signedOut = new CliAccountStatus { Provider = "gemini", LoggedIn = false, MethodId = CliAccountMethod.GeminiGoogle, CanSignOut = false };
        var signedIn = signedOut with { LoggedIn = true };
        var reads = 0;
        var outcome = await CliAntigravityLogin.RunAsync(_ => Task.FromResult(++reads >= 3 ? signedIn : signedOut), () => true, interval: TimeSpan.Zero, limit: TimeSpan.FromSeconds(20), tick: TimeSpan.FromMilliseconds(10));
        Check(outcome.Outcome == CliLoginOutcome.LoggedIn && outcome.Status == signedIn, "the status reading signed in ends the wait signed in");
        var closed = await CliAntigravityLogin.RunAsync(_ => Task.FromResult(signedOut), () => false, interval: TimeSpan.FromSeconds(60), tick: TimeSpan.FromMilliseconds(10));
        Check(closed.Outcome == CliLoginOutcome.Exited && closed.Status == signedOut, "a closed terminal without a sign-in ends the wait");
        var expired = await CliAntigravityLogin.RunAsync(_ => Task.FromResult(signedOut), () => true, interval: TimeSpan.FromSeconds(60), limit: TimeSpan.FromMilliseconds(50), tick: TimeSpan.FromMilliseconds(10));
        Check(expired.Outcome == CliLoginOutcome.TimedOut, "the limit ends the wait");
        var polled = 0;
        var slow = await CliAntigravityLogin.RunAsync(_ => { polled++; return Task.FromResult(signedOut); }, () => true, interval: TimeSpan.FromSeconds(60), limit: TimeSpan.FromMilliseconds(200), tick: TimeSpan.FromMilliseconds(10));
        Check(slow.Outcome == CliLoginOutcome.TimedOut && polled == 1, "the status is read only every interval, and once more at the limit; got " + polled);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        try { await CliAntigravityLogin.RunAsync(_ => Task.FromResult(signedOut), () => true, cancel.Token); throw new InvalidOperationException("cancel was ignored"); } catch (OperationCanceledException) { }
        var plan = CliAccountTerminal.LaunchPlan(CliAccountSupport.LoginArguments("gemini")!, windowsTerminalAvailable: true, directory: @"C:\Projects\app");
        Check(plan.Arguments.SequenceEqual(["new-tab", "-d", @"C:\Projects\app", "--", "agy"]), "the Antigravity sign-in terminal starts in the pane's workspace");
        Check(CliAccountTerminal.LaunchPlan(["agy"], true, @"C:\a;b").Arguments.SequenceEqual(["new-tab", "--", "agy"]), "a folder Windows Terminal would split is not passed on");
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
}
