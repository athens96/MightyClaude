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
        Check(!CliAuthFailure.SignInCanFix(new() { Provider = "gemini", Method = CliAccountSupport.GeminiGoogleMethod, MethodId = CliAccountMethod.GeminiVertex }), "a Gemini method other than Google is excluded by id");
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
        Check(gate.ShouldStart("gemini", true, new() { Provider = "gemini" }, false, now) && !gate.ShouldStart("gemini", true, claude, false, now) && !gate.ShouldStart("codex", true, claude, false, now), "Claude, Codex and Gemini, each decided on its own status");
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
    /// <summary>What Gemini CLI 0.43 printed for each case in a headless stream-json run (temp HOME, fake credentials).</summary>
    internal static Task GeminiSignalsAndMethods()
    {
        foreach (var text in new[]
        {
            "Opening authentication page in your browser. Do you want to continue? [Y/n]: fix the bug",
            "Error authenticating: FatalCancellationError: Authentication cancelled by user.",
            "Please set an Auth method in your C:\\Users\\me\\.gemini\\settings.json or specify one of the following environment variables before running: GEMINI_API_KEY, GOOGLE_GENAI_USE_VERTEXAI, GOOGLE_GENAI_USE_GCA",
            "Manual authorization is required but the current session is non-interactive. Please run the Gemini CLI in an interactive terminal to log in.",
            "No authentication method selected.",
            "[API Error: invalid_grant]",
            "Token has been expired or revoked.",
            "[API Error: Request had invalid authentication credentials. Expected OAuth 2 access token. (Status: UNAUTHENTICATED)]",
            """{"error":{"code":401,"status":"UNAUTHENTICATED"}}""",
        })
            Check(CliAuthFailure.Gemini(text) && CliAuthFailure.Matches("gemini", text), "gemini sign-in failure: " + text);
        foreach (var text in new[]
        {
            """[API Error: {"error":{"code":400,"message":"API key not valid. Please pass a valid API key.","status":"INVALID_ARGUMENT"}}]""",
            "[API Error: Resource has been exhausted (e.g. check quota). (Status: RESOURCE_EXHAUSTED)]",
            "[API Error: The caller does not have permission (Status: PERMISSION_DENIED)]",
            "No input provided via stdin. Input can be provided by piping data into gemini or using the --prompt option.",
            "401 files changed",
            "Error authenticating: getaddrinfo ENOTFOUND oauth2.googleapis.com",
        })
            Check(!CliAuthFailure.Gemini(text), "not a gemini sign-in failure: " + text);
        Check(!CliAuthFailure.Matches("claude", "Please set an Auth method") && !CliAuthFailure.Matches("other", "invalid_grant"), "each provider is matched by its own rules");
        // On stderr and plain stdout lines only the CLI's own sign-in messages count; token rejections are left to the failed result.
        foreach (var line in new[] { "Error authenticating: FatalCancellationError: Authentication cancelled by user.", "Error authenticating: FatalAuthenticationError: Failed to authenticate with user code.",
            "Please set an Auth method in your settings.json", "No authentication method selected.", "Manual authorization is required but the current session is non-interactive.",
            "Opening authentication page in your browser. Do you want to continue? [Y/n]: " })
            Check(CliAuthFailure.GeminiConsole(line), "gemini console sign-in message: " + line);
        foreach (var line in new[] { "Error when talking to Gemini API _ApiError: {\"error\":{\"code\":401,\"status\":\"UNAUTHENTICATED\"}}", "GaxiosError: invalid_grant",
            "Token has been expired or revoked.", "Request had invalid authentication credentials.", "Error authenticating: getaddrinfo ENOTFOUND oauth2.googleapis.com" })
            Check(!CliAuthFailure.GeminiConsole(line), "not a gemini console sign-in message: " + line);
        Check(CliAuthFailure.GeminiSucceeded("{\"type\":\"result\",\"status\":\"success\"}") && !CliAuthFailure.GeminiSucceeded("{\"type\":\"result\",\"status\":\"error\"}")
            && !CliAuthFailure.GeminiSucceeded("{\"type\":\"message\",\"content\":\"\\\"result\\\"\"}") && !CliAuthFailure.GeminiSucceeded("{\"result\""), "only a successful result line clears the signal");
        var home = Path.Combine(Path.GetTempPath(), "mighty-gemini-method-" + Guid.NewGuid().ToString("N"));
        var directory = Path.Combine(home, ".gemini"); Directory.CreateDirectory(directory);
        try
        {
            CliAccountStatus Select(string? type)
            {
                File.WriteAllText(Path.Combine(directory, "settings.json"), type is null ? "{}" : "{\"security\":{\"auth\":{\"selectedType\":\"" + type + "\"}}}");
                return CliAccountSupport.GeminiStatus(home, new Dictionary<string, string> { ["GEMINI_API_KEY"] = "fixture", ["GOOGLE_CLOUD_PROJECT"] = "fixture" });
            }
            Check(CliAuthFailure.SignInCanFix(Select("oauth-personal")), "a signed-out Google sign-in can be fixed");
            File.WriteAllText(Path.Combine(directory, "oauth_creds.json"), "{}");
            Check(CliAuthFailure.SignInCanFix(Select("oauth-personal")) && CliAuthFailure.SignInCanFix(Select(null)), "a stale Google sign-in still on disk can be fixed");
            foreach (var type in new[] { "gemini-api-key", "vertex-ai", "compute-default-credentials" })
                Check(!CliAuthFailure.SignInCanFix(Select(type)), "a Google sign-in does not renew " + type);
            Check(!CliAuthFailure.SignInCanFix(new() { Provider = "gemini", Installed = false }), "no CLI, no sign-in");
            var gate = new CliAutoLoginGate(); var now = DateTimeOffset.UnixEpoch.AddDays(10000);
            Check(!gate.ShouldStart("gemini", true, Select("gemini-api-key"), false, now) && !gate.ShouldStart("gemini", true, Select("vertex-ai"), false, now), "an API key or Vertex AI never opens the sign-in terminal");
            var google = Select("oauth-personal");
            Check(gate.ShouldStart("gemini", true, google, false, now) && !gate.ShouldStart("gemini", true, google, true, now), "one Gemini sign-in at a time");
            gate.Stopped("gemini", now);
            Check(!gate.ShouldStart("gemini", true, google, false, now + CliAutoLoginGate.Cooldown - TimeSpan.FromSeconds(1)) && gate.ShouldStart("gemini", true, google, false, now + CliAutoLoginGate.Cooldown), "a cancelled Gemini sign-in waits for the cooldown");
            gate.Succeeded("gemini", now + TimeSpan.FromHours(1));
            Check(!gate.ShouldStart("gemini", true, google, false, now + TimeSpan.FromHours(2), sentAt: now + TimeSpan.FromMinutes(59)), "a Gemini run older than the last sign-in only raises the card");
            var book = new CliLoginRetryBook();
            book.Sent(new StartRunRequest("pane", "workspace", "claude", "again", [], Provider: "gemini"));
            Check(book.Settled("pane") is { Request.Provider: "gemini" }, "a Gemini request is kept for its retry");
        }
        finally { Directory.Delete(home, true); }
        return Task.CompletedTask;
    }
    internal static async Task GeminiSignInIsTheCredentialsFileChanging()
    {
        var home = Path.Combine(Path.GetTempPath(), "mighty-gemini-login-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(home, ".gemini"));
        // Fixture contents only; the code under test reads nothing but the file's dates.
        void Write(DateTime utc) { var path = CliGeminiLogin.CredentialsPath(home); File.WriteAllText(path, "{\"fixture\":true}"); File.SetLastWriteTimeUtc(path, utc); }
        var google = new CliAccountStatus { Provider = "gemini", LoggedIn = true, Method = CliAccountSupport.GeminiGoogleMethod, MethodId = CliAccountMethod.GeminiGoogle };
        try
        {
            Check(CliGeminiLogin.CredentialsStamp(home) is null, "no file, no stamp");
            var opened = new DateTime(2027, 1, 15, 8, 0, 0, DateTimeKind.Utc);
            Write(opened);
            var stamp = CliGeminiLogin.CredentialsStamp(home);
            Check(stamp is { } value && Math.Abs((value - new DateTimeOffset(opened)).TotalSeconds) < 1, "the stamp is the file's modification time");
            Check(CliGeminiLogin.SignedIn(null, stamp, google), "signed out when the terminal opened: the file appearing counts");
            Check(!CliGeminiLogin.SignedIn(stamp, CliGeminiLogin.CredentialsStamp(home), google), "the stale file of the dropped sign-in does not count");
            Write(opened.AddSeconds(30));
            Check(CliGeminiLogin.SignedIn(stamp, CliGeminiLogin.CredentialsStamp(home), google), "a rewrite after the terminal opened counts");
            var later = CliGeminiLogin.CredentialsStamp(home);
            Check(!CliGeminiLogin.SignedIn(stamp, later, google with { Method = "Vertex AI", MethodId = CliAccountMethod.GeminiVertex }) && !CliGeminiLogin.SignedIn(stamp, later, google with { LoggedIn = false }) && !CliGeminiLogin.SignedIn(stamp, null, google),
                "only a signed-in Google status with the file present counts");
            Check(CliGeminiLogin.SignedIn(stamp, later, CliAccountSupport.GeminiStatus(home)), "the status read from the same files agrees");

            Write(DateTime.UtcNow.AddHours(-1)); var baseline = CliGeminiLogin.CredentialsStamp(home);
            var reads = 0;
            Task<CliAccountStatus> Status(CancellationToken _) { reads++; return Task.FromResult(CliAccountSupport.GeminiStatus(home)); }
            var idle = await CliGeminiLogin.RunAsync(baseline, () => CliGeminiLogin.CredentialsStamp(home), Status, () => true, limit: TimeSpan.FromMilliseconds(200), tick: TimeSpan.FromMilliseconds(10));
            Check(idle.Outcome == CliLoginOutcome.TimedOut && reads == 1, "an unchanged file is never read as a sign-in, and the status is read only at the limit; got " + reads);
            var closed = await CliGeminiLogin.RunAsync(baseline, () => CliGeminiLogin.CredentialsStamp(home), Status, () => false, tick: TimeSpan.FromMilliseconds(10));
            Check(closed.Outcome == CliLoginOutcome.Exited, "a closed terminal without a sign-in ends the wait");
            var waiting = CliGeminiLogin.RunAsync(baseline, () => CliGeminiLogin.CredentialsStamp(home), Status, () => true, interval: TimeSpan.FromMilliseconds(20), tick: TimeSpan.FromMilliseconds(10));
            await Task.Delay(80); Write(DateTime.UtcNow);
            var signed = await waiting.WaitAsync(TimeSpan.FromSeconds(10));
            Check(signed.Outcome == CliLoginOutcome.LoggedIn && signed.Status.MethodId == CliAccountMethod.GeminiGoogle, "the CLI rewriting the file while the terminal is open signs in");
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            try { await CliGeminiLogin.RunAsync(null, () => null, Status, () => true, cancel.Token); throw new InvalidOperationException("cancel was ignored"); } catch (OperationCanceledException) { }
            var plan = CliAccountTerminal.LaunchPlan(CliAccountSupport.LoginArguments("gemini")!, windowsTerminalAvailable: true, directory: @"C:\Projects\app");
            Check(plan.Arguments.SequenceEqual(["new-tab", "-d", @"C:\Projects\app", "--", "gemini"]), "the Gemini sign-in terminal starts in the pane's workspace");
            Check(CliAccountTerminal.LaunchPlan(["gemini"], true, @"C:\a;b").Arguments.SequenceEqual(["new-tab", "--", "gemini"]), "a folder Windows Terminal would split is not passed on");
            Check(CliAccountTerminal.LaunchPlan(["gemini"], false, @"C:\Projects\app").Arguments.SequenceEqual(["gemini"]), "the console host inherits the folder instead");
            Check(CliAccountTerminal.LaunchPlan(["gemini"], false, null, @"C:\npm\gemini.cmd").Arguments.SequenceEqual(["cmd.exe", "/c", @"C:\npm\gemini.cmd"])
                && CliAccountTerminal.LaunchPlan(["gemini"], false, null, @"C:\bin\gemini.exe").Arguments.SequenceEqual([@"C:\bin\gemini.exe"])
                && CliAccountTerminal.LaunchPlan(["gemini"], true, null, @"C:\npm\gemini.cmd").Arguments.SequenceEqual(["new-tab", "--", "gemini"]), "the console host runs the resolved Gemini, an npm shim through cmd /c");
        }
        finally { Directory.Delete(home, true); }
    }
    internal static async Task GeminiFakeCliRunRaisesTheReasonFromStderrOnly()
    {
        var directory = Verification.Temp(); var workspace = new Workspace { Path = directory };
        try
        {
            foreach (var (flag, expected) in new[] { ("--gemini-signin-lost", (string?)"authentication"), ("--gemini-quoted", null) })
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
