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
        Check(!CliAuthFailure.SignInCanFix(new() { Provider = "codex", Method = "API 키" }), "Codex API-key login is excluded");
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
        Check(!gate.ShouldStart("gemini", true, new() { Provider = "gemini" }, false, now) && !gate.ShouldStart("codex", true, claude, false, now), "only Claude and Codex, decided on their own status");
        Check(!gate.ShouldStart("claude", true, claude with { Method = "AWS Bedrock", AccessVerified = false }, false, now)
            && !gate.ShouldStart("codex", true, codex with { Method = CliAccountSupport.CodexApiKeyMethod }, false, now)
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
