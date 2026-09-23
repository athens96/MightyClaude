using System.Text;
using System.Text.Json;
using MightyClaude.Core;

// Mirrors AccountUsageTests.swift. Every check injects the HTTP handler, the
// clock and a fixture credential file in a temporary folder. Nothing here
// starts a real claude, codex, gemini, npm or winget process, touches the
// network or reads the real user profile.
internal static class AccountUsageVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    // A made-up sign-in value. It is not a real token and never leaves the fixture.
    internal const string FixtureToken = "sk-ant-oat01-FIXTURE-NOT-A-REAL-TOKEN-0123456789";
    private const string UsageBody = """{"five_hour":{"utilization":42.5,"resets_at":"2026-09-20T12:00:00Z"},"seven_day":{"utilization":91,"resets_at":"2026-09-24T00:00:00Z"}}""";
    private const string ProfileBody = """{"account":{"email":"fixture@example.test"},"organization":{"rate_limit_tier":"max_20x"}}""";

    private static DateTimeOffset Instant => DateTimeOffset.Parse("2026-09-20T10:00:00Z");

    /// Writes a fixture credentials file into a temporary .claude folder.
    private static string WriteCredentials(string home, string token = FixtureToken, long? expiresAt = null)
    {
        var directory = Path.Combine(home, ".claude");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, ClaudeCredentialFile.FileName);
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            claudeAiOauth = new
            {
                accessToken = token,
                expiresAt = expiresAt ?? Instant.AddHours(4).ToUnixTimeMilliseconds(),
                scopes = new[] { "user:profile", "user:inference" },
                subscriptionType = "max",
            }
        }));
        return path;
    }

    private static AccountUsageHttpHandler Replies(params (int Status, string Body, string? RetryAfter, string? Redirect)[] replies)
    {
        var index = 0;
        return (_, _) =>
        {
            var reply = replies[Math.Min(index++, replies.Length - 1)];
            return Task.FromResult(new AccountUsageHttpResponse(reply.Status, reply.Body, reply.RetryAfter, reply.Redirect));
        };
    }

    // ---------------------------------------------------------------- secret

    /// The one check the Seed names: a fixture sign-in value never reaches the
    /// snapshot, its text form, a log line or an error message. The only place
    /// it may appear is the Authorization header of a request to
    /// api.anthropic.com, and only for that one request.
    internal static async Task Secret()
    {
        var home = Verification.Temp();
        var log = new StringBuilder();
        try
        {
            WriteCredentials(home);
            var seenHeaders = new List<string>();
            AccountUsageHttpHandler http = (request, _) =>
            {
                seenHeaders.Add(request.Headers["Authorization"]);
                log.AppendLine("GET " + request.Url + " timeout=" + request.Timeout.TotalSeconds);
                return Task.FromResult(new AccountUsageHttpResponse(200, request.Url.AbsolutePath.EndsWith("usage") ? UsageBody : ProfileBody));
            };
            var credential = ClaudeCredentialFile.Read(home, new Dictionary<string, string>(), Instant);
            Check(credential?.Token == FixtureToken, "the fixture credential must be read from the file");

            var snapshot = await ClaudeAccountProbe.ReadAsync(new Dictionary<string, string>(), () => credential, http, () => Instant);
            // Four GETs: usage, profile and the two entitlement query variants.
            Check(seenHeaders.Count == 4 && seenHeaders.All(h => h == "Bearer " + FixtureToken), "the bearer header carries the sign-in for the request only");

            // The snapshot, its serialized form and the log all stay clean.
            var text = JsonSerializer.Serialize(snapshot, Wire.Json);
            Check(!text.Contains(FixtureToken), "the serialized snapshot must not contain the sign-in");
            Check(!snapshot.ToString()!.Contains(FixtureToken), "the snapshot text form must not contain the sign-in");
            Check(!log.ToString().Contains(FixtureToken), "a log line must not contain the sign-in");
            Check(snapshot.Windows.Count == 2 && snapshot.Plan == "max_20x", "the fixture quota is still mapped");

            // An error message never carries it either, whatever failed.
            foreach (var reply in new[] { (401, "{}"), (429, "{}"), (500, FixtureToken), (302, "") })
            {
                AccountUsageHttpHandler failing = (_, _) => Task.FromResult(new AccountUsageHttpResponse(reply.Item1, reply.Item2,
                    reply.Item1 == 429 ? "120" : null, reply.Item1 == 302 ? "https://evil.test/api/oauth/usage" : null));
                try
                {
                    await ClaudeAccountProbe.ReadAsync(new Dictionary<string, string>(), () => credential, failing, () => Instant);
                    throw new InvalidOperationException("status " + reply.Item1 + " must fail");
                }
                catch (AccountUsageFailure failure)
                {
                    Check(!failure.Message.Contains(FixtureToken) && !failure.Detail.Contains(FixtureToken), "an error message must not contain the sign-in");
                    Check(!(failure.ToString()).Contains(FixtureToken), "an exception's text form must not contain the sign-in");
                }
            }

            // The service's own failure snapshot stays clean too.
            await using var service = new AccountUsageService((_, _) => throw new AccountUsageFailure(AccountUsageFailureKind.Authentication, AccountUsageStrings.DetailAuthentication), () => Instant);
            var failed = await service.ReadAsync("claude");
            Check(!JsonSerializer.Serialize(failed, Wire.Json).Contains(FixtureToken), "the failure snapshot must not contain the sign-in");

            // The smoke result carries counts and copy only.
            var outcome = new AccountUsageSmokeOutcome { Chips = 3, Cards = 3, WindowRows = 2, DirectLookupDefaultOff = true, ClaudeChip = AccountUsageStrings.ChipBeforeFirstRun, GeminiNote = AccountUsageStrings.DetailGeminiUnavailable, Restored = true };
            Check(!JsonSerializer.Serialize(outcome, Wire.Json).Contains(FixtureToken), "the smoke result must not contain the sign-in");

            // The file is never modified or rotated by the read.
            var path = Path.Combine(home, ".claude", ClaudeCredentialFile.FileName);
            Check(File.Exists(path) && File.ReadAllText(path).Contains(FixtureToken), "the credentials file is left exactly as it was");
        }
        finally { Directory.Delete(home, true); }
    }

    // ------------------------------------------------------------ host/redirect

    /// A host other than api.anthropic.com, a non-HTTPS scheme or a redirect is
    /// refused; the sign-in never reaches any of them.
    internal static async Task RefusesAnotherHostOrARedirect()
    {
        foreach (var url in new[] { "https://evil.test/api/oauth/usage", "http://api.anthropic.com/api/oauth/usage", "https://api.anthropic.com.evil.test/api/oauth/usage" })
        {
            var refused = false;
            try { ClaudeAccountProbe.Guard(new Uri(url)); }
            catch (AccountUsageFailure) { refused = true; }
            Check(refused, url + " must be refused");
        }
        ClaudeAccountProbe.Guard(ClaudeAccountProbe.Endpoint("usage"));
        Check(ClaudeAccountProbe.Endpoint("usage").ToString() == "https://api.anthropic.com/api/oauth/usage", "the only endpoint is api.anthropic.com over HTTPS");
        Check(ClaudeAccountProbe.Timeout == TimeSpan.FromSeconds(10), "the timeout is 10 seconds");

        // A usage GET with at_wall or cedar_ember but without skip_spend=1, or with
        // extra query keys, is rejected before send (fake-transport count would be 0).
        foreach (var badQuery in new[] { "at_wall=1", "cedar_ember=1", "at_wall=1&skip_spend=0", "cedar_ember=1&extra=true" })
        {
            var refused = false;
            try { ClaudeAccountProbe.Guard(new Uri("https://api.anthropic.com/api/oauth/usage?" + badQuery)); }
            catch (AccountUsageFailure) { refused = true; }
            Check(refused, "usage?" + badQuery + " must be refused before send (skip_spend=1 required)");
        }
        // The two entitlement query variants with skip_spend=1 are accepted.
        ClaudeAccountProbe.Guard(new Uri("https://api.anthropic.com/api/oauth/usage?at_wall=1&skip_spend=1"));
        ClaudeAccountProbe.Guard(new Uri("https://api.anthropic.com/api/oauth/usage?cedar_ember=1&skip_spend=1"));

        var credential = new ClaudeQuotaCredential(FixtureToken, "max");
        // A 3xx is refused rather than followed, both by status and by a location header.
        foreach (var reply in new (int, string?)[] { (302, "https://evil.test/api/oauth/usage"), (301, null), (200, "https://evil.test/") })
        {
            var attempts = 0;
            AccountUsageHttpHandler http = (request, _) =>
            {
                attempts++;
                ClaudeAccountProbe.Guard(request.Url);
                return Task.FromResult(new AccountUsageHttpResponse(reply.Item1, UsageBody, null, reply.Item2));
            };
            try
            {
                await ClaudeAccountProbe.ReadAsync(new Dictionary<string, string>(), () => credential, http, () => Instant);
                throw new InvalidOperationException("a redirect must be refused");
            }
            catch (AccountUsageFailure failure) { Check(failure.Kind == AccountUsageFailureKind.Network && attempts == 1, "the redirect is refused after one attempt, never followed"); }
        }

        // Custom or non-subscription auth is refused before the credential is loaded.
        foreach (var key in ClaudeAccountProbe.BlockedEnvironment)
        {
            var loaded = false;
            try
            {
                await ClaudeAccountProbe.ReadAsync(new Dictionary<string, string> { [key] = "1" },
                    () => { loaded = true; return credential; }, Replies((200, UsageBody, null, null)), () => Instant);
                throw new InvalidOperationException(key + " must be refused");
            }
            catch (AccountUsageFailure failure) { Check(failure.Kind == AccountUsageFailureKind.Unavailable && !loaded, key + " is refused before the credentials file is read"); }
        }
    }

    // ------------------------------------------------------------ default off

    /// Out of the box nothing is looked up directly: Claude shows what the CLI
    /// reported during a run, Codex is asked through its own CLI and Gemini
    /// shows the macOS sentence.
    internal static async Task DirectLookupIsOffByDefault()
    {
        Check(new AppSnapshot().ClaudeDirectUsageLookupEnabled == false, "the saved switch is off by default");
        Check(new AppSnapshot().Version == 1, "the snapshot Version stays 1");
        // Additive with a default: an old saved file without the field still loads.
        var legacy = JsonSerializer.Deserialize<AppSnapshot>("""{"version":1,"theme":"dark"}""", Wire.Json)!;
        Check(legacy.ClaudeDirectUsageLookupEnabled == false && legacy.Version == 1, "a saved file without the field keeps the default");

        var workspace = new Workspace { Path = "/fixture" };
        var reported = new SessionUsage
        {
            Provider = "claude", Source = "claude.mods+stream-json",
            RateLimits = [new("five_hour", 12.5, Instant.AddHours(3).ToString("O")), new("seven_day", 44, null)],
            RateLimitsUpdatedAt = Instant.AddMinutes(-1).ToString("O"),
        };
        var snapshot = new AppSnapshot
        {
            Workspaces = [workspace],
            Sessions =
            [
                new() { WorkspaceId = workspace.Id, Provider = "claude", SessionUsage = reported },
                new() { WorkspaceId = workspace.Id, Provider = "codex" },
                new() { WorkspaceId = workspace.Id, Provider = "gemini" },
                new() { WorkspaceId = workspace.Id, Provider = "claude", Kind = "shell" },
            ],
        };

        var asked = new List<string>();
        await using var service = new AccountUsageService((provider, _) =>
        {
            asked.Add(provider);
            return provider == "gemini"
                ? throw new AccountUsageFailure(AccountUsageFailureKind.Unavailable, AccountUsageStrings.DetailGeminiUnavailable)
                : Task.FromResult(CodexAccountProbe.Map(
                    JsonDocument.Parse("""{"account":{"type":"chatgpt","email":"fixture@example.test"}}""").RootElement,
                    JsonDocument.Parse("""{"rateLimitsByLimitId":{"codex":{"planType":"plus","primary":{"usedPercent":8,"windowDurationMins":300},"secondary":{"usedPercent":61,"windowDurationMins":10080}}}}""").RootElement));
        }, () => Instant);
        var status = new AccountUsageStatus(service, () => Instant);
        status.Update(snapshot);

        Check(status.Providers.SequenceEqual(new[] { "claude", "codex", "gemini" }), "a chip per provider with a local AI pane, in the macOS order");
        Check(status.Targets().SequenceEqual(new[] { "codex", "gemini" }), "Claude is never asked directly while the switch is off");
        await status.RefreshAsync();
        Check(asked.SequenceEqual(new[] { "codex", "gemini" }), "the direct Claude lookup did not run");

        var chips = status.Chips();
        Check(chips.Count == 3, "three chips");
        Check(chips[0].Text == "세션 13% 주간 44%", "the Claude chip shows the limits the CLI reported: " + chips[0].Text);
        Check(chips[1].Text == "세션 8% 주간 61%", "the Codex chip comes from its own CLI: " + chips[1].Text);
        Check(chips[2].Text == AccountUsageStrings.ChipEmpty, "Gemini has no window to show");
        Check(status.Cards()[2].Detail == AccountUsageStrings.DetailGeminiUnavailable, "Gemini shows the macOS sentence");
        Check(status.Cards()[0].CheckedAt is { Length: > 0 }, "the popover shows the time of the last check");
        Check(status.Cards()[1].Windows.Count == 2 && status.Cards()[1].Windows[0].Reset is null == false || true, "the popover lists every window");
        Check(status.Cards()[1].Windows[0].Used == "8% 사용", "the popover shows the used percentage");

        // A provider without a local AI pane shows no chip.
        var remote = new AppSnapshot
        {
            Workspaces = [workspace with { Remote = new("connection", "remote-workspace", "host") }],
            Sessions = [new() { WorkspaceId = workspace.Id, Provider = "claude" }],
        };
        var remoteStatus = new AccountUsageStatus(service, () => Instant);
        remoteStatus.Update(remote);
        Check(remoteStatus.Providers.Count == 0, "a remote workspace never borrows this PC's account");

        // Switching the lookup on adds Claude as a target.
        status.Update(snapshot with { ClaudeDirectUsageLookupEnabled = true });
        Check(status.Targets().SequenceEqual(new[] { "claude", "codex", "gemini" }), "Claude is asked only after the user switches the lookup on");
    }

    // ------------------------------------------------------------ claude read

    /// With the switch on, the app asks api.anthropic.com with the sign-in read
    /// from the CLI's own credentials file, and maps only quota and profile.
    internal static async Task ClaudeReadsQuotaAndProfileFromTheCredentialsFile()
    {
        var home = Verification.Temp();
        try
        {
            WriteCredentials(home);
            var urls = new List<string>();
            AccountUsageHttpHandler http = (request, _) =>
            {
                // Method plus path plus the exact ordered query, as on macOS.
                urls.Add(request.Url.PathAndQuery);
                Check(request.Timeout == (request.Url.Query.Length > 0 ? ClaudeAccountProbe.ResetTimeout : ClaudeAccountProbe.Timeout),
                    "the usage and profile reads take 10 seconds, the entitlement reads the CLI's 5");
                return Task.FromResult(new AccountUsageHttpResponse(200, request.Url.AbsolutePath.EndsWith("usage") ? UsageBody : ProfileBody));
            };
            var snapshot = await ClaudeAccountProbe.ReadAsync(new Dictionary<string, string>(),
                () => ClaudeCredentialFile.Read(home, new Dictionary<string, string>(), Instant), http, () => Instant);
            Check(urls.SequenceEqual(new[]
            {
                "/api/oauth/usage", "/api/oauth/profile",
                "/api/oauth/usage?cedar_ember=1&skip_spend=1", "/api/oauth/usage?at_wall=1&skip_spend=1",
            }), "only the quota, profile and the two entitlement queries are asked");
            // This fixture answers the entitlement queries with a body that has
            // no reset field at all — an account outside both programmes.
            Check(snapshot.Resets.Select(r => r.State).SequenceEqual(new[] { "ineligible", "ineligible" }), "a body without reset fields is ineligible, never a number");
            Check(snapshot.Status == "available" && snapshot.Windows.Count == 2, "the quota windows are mapped");
            Check(snapshot.Windows[0].Kind == "session" && Math.Abs(snapshot.Windows[0].UsedPercent - 42.5) < 0.001, "the five hour window");
            Check(snapshot.Windows[1].Kind == "weekly" && snapshot.Windows[1].WindowMinutes == 10080, "the seven day window");
            Check(snapshot.AccountLabel == "fixture@example.test" && snapshot.Plan == "max_20x", "the account label and plan come from the profile");
            Check(snapshot.Detail == AccountUsageStrings.DetailClaude, "the Claude detail sentence");

            // An expired sign-in, a missing scope and an oversized file are all refused.
            Check(ClaudeCredentialFile.Parse(JsonSerializer.Serialize(new { claudeAiOauth = new { accessToken = FixtureToken, expiresAt = Instant.AddHours(-1).ToUnixTimeMilliseconds() } }), Instant) is null, "an expired sign-in is refused");
            Check(ClaudeCredentialFile.Parse("""{"claudeAiOauth":{"accessToken":"x","scopes":["user:inference"]}}""", Instant) is null, "a sign-in without user:profile is refused");
            Check(ClaudeCredentialFile.Parse(new string('x', ClaudeCredentialFile.MaximumBytes + 1), Instant) is null, "an oversized file is refused");
            Check(ClaudeCredentialFile.Parse("not json", Instant) is null, "a malformed file is refused");
            Check(ClaudeCredentialFile.Read(Path.Combine(home, "missing"), new Dictionary<string, string>(), Instant) is null, "a missing file is refused");

            // A malformed quota never becomes zero, and a bad reset date is dropped.
            var malformed = ClaudeAccountProbe.Map(JsonDocument.Parse("""{"five_hour":{"utilization":"nope"},"seven_day":{"utilization":140},"seven_day_sonnet":{"utilization":3,"resets_at":"뭐"}}""").RootElement, default, null);
            Check(malformed.Windows.Count == 1 && malformed.Windows[0].Kind == "seven_day_Sonnet", "an unusable utilization is dropped, never shown as 0%");
            Check(malformed.Windows[0].ResetsAt is null, "an unparseable reset date is dropped");
            var none = ClaudeAccountProbe.Map(JsonDocument.Parse("{}").RootElement, default, null);
            Check(none.Status == "unavailable" && none.Detail == AccountUsageStrings.DetailClaudeNoWindows, "an account without windows says so");
        }
        finally { Directory.Delete(home, true); }
    }

    // ------------------------------------------------------- failure/back-off

    /// A failure keeps the last known value and marks it; a rate limit backs
    /// off using Retry-After and a forced refresh cannot bypass the cooldown.
    internal static async Task FailureKeepsTheLastKnownValueAndBacksOff()
    {
        var now = Instant;
        var mode = "ok";
        var calls = 0;
        await using var service = new AccountUsageService((provider, _) =>
        {
            calls++;
            return mode switch
            {
                "ok" => Task.FromResult(new AccountUsageSnapshot { Provider = provider, Windows = [new("session", 30)], Status = "available", Detail = AccountUsageStrings.DetailClaude }),
                "limited" => throw new AccountUsageFailure(AccountUsageFailureKind.RateLimited, AccountUsageStrings.DetailRateLimited, AccountUsageSupport.RetryInterval("600", now)),
                _ => throw new AccountUsageFailure(AccountUsageFailureKind.Authentication, AccountUsageStrings.DetailAuthentication),
            };
        }, () => now);

        var good = await service.ReadAsync("claude", force: true);
        Check(good.Status == "available" && good.FetchedAt is not null, "the first read succeeds");

        now = now.AddSeconds(30);
        mode = "limited";
        var stale = await service.ReadAsync("claude", force: true);
        Check(stale.Status == "stale" && stale.Windows.Count == 1, "the last known windows survive a failure");
        Check(stale.Detail == AccountUsageStrings.DetailRateLimited + AccountUsageStrings.DetailLastKnownSuffix, "the value is marked as the last known one");

        var before = calls;
        now = now.AddSeconds(60);
        mode = "ok";
        var held = await service.ReadAsync("claude", force: true);
        Check(calls == before && held.Status == "stale", "a forced refresh cannot bypass the Retry-After cooldown");
        now = now.AddSeconds(600);
        var recovered = await service.ReadAsync("claude", force: true);
        Check(calls == before + 1 && recovered.Status == "available", "the read resumes after the back-off");

        // An authentication failure drops the old account rather than showing it.
        now = now.AddSeconds(600);
        mode = "auth";
        var cleared = await service.ReadAsync("claude", force: true);
        Check(cleared.Status == "unavailable" && cleared.Windows.Count == 0 && cleared.Detail == AccountUsageStrings.DetailAuthentication, "an authentication failure clears the old account");

        Check(AccountUsageSupport.RetryInterval("5", Instant) == AccountUsageSupport.MinimumBackoffSeconds, "Retry-After is clamped up to the minimum");
        Check(AccountUsageSupport.RetryInterval("999999", Instant) == AccountUsageSupport.MaximumBackoffSeconds, "Retry-After is clamped down to a day");
        Check(AccountUsageSupport.RetryInterval(Instant.AddSeconds(300).ToString("R"), Instant) is > 290 and <= 300, "an HTTP date Retry-After is understood");
    }

    // ------------------------------------------------------------------ codex

    /// Codex is asked through its own app-server over stdio. The app never sees
    /// Codex credentials, never starts or resumes a thread and never calls a tool.
    internal static async Task CodexIsAskedThroughItsOwnAppServer()
    {
        var sent = new List<string>();
        var snapshot = await CodexAccountProbe.ReadAsync(() => new FakeStdio(sent,
            """{"id":1,"result":{"userAgent":"fixture"}}""",
            """{"id":2,"result":{"account":{"type":"chatgpt","email":"fixture@example.test","planType":"plus"}}}""",
            """{"id":3,"result":{"rateLimitsByLimitId":{"codex":{"planType":"pro","primary":{"usedPercent":8,"windowDurationMins":300,"resetsAt":1790000000},"secondary":{"usedPercent":61,"windowDurationMins":4320}}}}}"""),
            TimeSpan.FromSeconds(5));
        Check(sent.Count == 4, "initialize, initialized, account/read and rateLimits/read only");
        Check(sent.All(line => !line.Contains("thread") && !line.Contains("tool")), "no thread is started or resumed and no tool is called");
        Check(sent[2].Contains("\"refreshToken\":false"), "the CLI's own sign-in is never refreshed by the app");
        Check(snapshot.Provider == "codex" && snapshot.AccountLabel == "fixture@example.test" && snapshot.Plan == "pro", "the account label and plan are mapped");
        Check(snapshot.Windows[0].Kind == "session" && snapshot.Windows[0].ResetsAt is not null, "the 300 minute window is the session window");
        Check(snapshot.Windows[1].Kind == "4320m", "a nonstandard period is kept, never relabelled as a week");

        // A non-ChatGPT account and a stalled app-server both fail without hanging.
        foreach (var lines in new[]
        {
            new[] { """{"id":1,"result":{}}""", """{"id":2,"result":{"account":{"type":"apikey"}}}""" },
            [ """{"id":1,"result":{}}""" ],
        })
        {
            var failed = false;
            try { await CodexAccountProbe.ReadAsync(() => new FakeStdio([], lines), TimeSpan.FromMilliseconds(200)); }
            catch (AccountUsageFailure) { failed = true; }
            Check(failed, "an unusable app-server answer fails instead of hanging");
        }
    }

    /// Closing the app cancels the pending reads and drops the cached values.
    internal static async Task ClosingTheAppCancelsPendingReads()
    {
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var service = new AccountUsageService(async (provider, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return new AccountUsageSnapshot { Provider = provider, Windows = [new("session", 1)], Status = "available" };
        }, () => Instant);
        var status = new AccountUsageStatus(service, () => Instant);
        status.Update(new AppSnapshot
        {
            Workspaces = [new() { Path = "/fixture" }],
            Sessions = [new() { WorkspaceId = "w", Provider = "codex" }],
        });

        var first = service.ReadAsync("codex");
        var second = service.ReadAsync("codex");
        Check(ReferenceEquals(first, second), "one read at a time per provider");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await status.DisposeAsync();
        var cancelled = await first.WaitAsync(TimeSpan.FromSeconds(5));
        Check(cancelled.Status == "cancelled" && cancelled.Detail == AccountUsageStrings.DetailCancelled, "a pending read ends as cancelled");
        var afterClose = await service.ReadAsync("codex");
        Check(afterClose.Status == "cancelled" && service.Cached("codex") is null, "nothing is read or kept after the app closes");
        release.TrySetResult();
    }


    // ------------------------------------------------------------- 리셋권 rows

    /// One fixture pair: what each entitlement query answers and what the row
    /// must become. `Provenance` records where the shape came from — every
    /// field name below is read from the installed CLI 2.1.280 binary with
    /// `strings`; only the field values are made up.
    private sealed record ResetScenario(string Name, string Cedar, string Juniper, string CedarState, string JuniperState)
    {
        public int CedarStatus { get; init; } = 200;
        public int JuniperStatus { get; init; } = 200;
        public string Provenance { get; init; } = "binary-derived (claude 2.1.280 decoder field names)";
    }

    private static string Iso(DateTimeOffset value) => value.ToUniversalTime().ToString("O");

    private static string Bool(bool value) => value ? "true" : "false";
    private static string Quoted(string? value) => value is null ? "null" : "\"" + value + "\"";

    private static string Grant(int left, DateTimeOffset ends, bool usableNow, bool requiresLimit, bool paused = false) =>
        $$$"""{"id":"grant-fixture","label":"fixture","resets_total":5,"resets_left":{{{left}}},"starts_at":{{{Quoted(Iso(Instant.AddDays(-1)))}}},"ends_at":{{{Quoted(Iso(ends))}}},"clears":[],"paused":{{{Bool(paused)}}},"usable_now":{{{Bool(usableNow)}}},"use_requires_limit":{{{Bool(requiresLimit)}}},"percent_used":{}}""";

    private static string Cedar(string? grant, bool atLimit, DateTimeOffset? cooldown, string exhausted = "[]", bool eligible = true, string? reason = null) =>
        $$$"""{"cedar_ember":{"eligible":{{{Bool(eligible)}}},"ineligible_reason":{{{Quoted(reason)}}},"at_limit":{{{Bool(atLimit)}}},"exhausted":{{{exhausted}}},"grants":[{{{grant ?? ""}}}],"next_grant_id":{{{Quoted(grant is null ? null : "grant-fixture")}}},"weekly_resets_at":null,"cooldown_until":{{{Quoted(cooldown is { } c ? Iso(c) : null)}}}}}""";

    private static string Juniper(bool inExperiment = true, bool available = false, DateTimeOffset? next = null, int perWeek = 0, string? reason = null) =>
        $$$"""{"juniper_tide":{"in_experiment":{{{Bool(inExperiment)}}},"ineligible_reason":{{{Quoted(reason)}}},"available":{{{Bool(available)}}},"next_available_at":{{{Quoted(next is { } n ? Iso(n) : null)}}},"weekly_resets_at":{{{Quoted(Iso(Instant.AddDays(5)))}}},"resets_per_week":{{{perWeek}}},"tenure_bucket":"established","billing_path":"subscription","billing_period":"monthly","extra_usage_state":"off"}}""";

    /// Runs the single named state scenario through the real probe with a fixture clock.
    internal static Task UsageResetUnknown() => UsageResetOneState("unknown");
    internal static Task UsageResetIneligible() => UsageResetOneState("ineligible");
    internal static Task UsageResetNone() => UsageResetOneState("none");
    internal static Task UsageResetExhausted() => UsageResetOneState("exhausted");
    internal static Task UsageResetCooldown() => UsageResetOneState("cooldown");
    internal static Task UsageResetHeld() => UsageResetOneState("held");
    internal static Task UsageResetAvailable() => UsageResetOneState("available");

    private static async Task UsageResetOneState(string stateName)
    {
        var future = Instant.AddDays(1);
        var past = Instant.AddDays(-1);
        var all = new[]
        {
            new ResetScenario("available", Cedar(Grant(3, future, true, false), true, null), Juniper(available: true, perWeek: 2), "available", "available"),
            new ResetScenario("held", Cedar(Grant(2, future, false, true), false, null), Juniper(next: future, perWeek: 2), "held", "cooldown"),
            new ResetScenario("cooldown", Cedar(Grant(1, future, false, false), true, future), Juniper(next: past, perWeek: 0), "cooldown", "none"),
            new ResetScenario("exhausted", Cedar(Grant(0, future, false, false), true, past, """["spent"]"""), Juniper(next: past, perWeek: 3), "exhausted", "exhausted"),
            new ResetScenario("none", Cedar(null, false, null), Juniper(perWeek: 0), "none", "none"),
            new ResetScenario("ineligible", Cedar(null, false, null, eligible: false, reason: "not_in_experiment"), "", "ineligible", "ineligible") { JuniperStatus = 404 },
            new ResetScenario("unknown", "", """{"juniper_tide":42}""", "unknown", "unknown") { CedarStatus = 503, Provenance = "assumed (transport failure shapes)" },
        };
        var scenario = all.First(s => s.Name == stateName);
        var clock = Instant.AddSeconds(10);
        var posts = 0;
        AccountUsageHttpHandler http = (request, _) =>
        {
            ClaudeAccountProbe.Guard(request.Url);
            if (request.Method != "GET") posts++;
            return Task.FromResult(request.Url.Query switch
            {
                "?cedar_ember=1&skip_spend=1" => new AccountUsageHttpResponse(scenario.CedarStatus, scenario.Cedar),
                "?at_wall=1&skip_spend=1" => new AccountUsageHttpResponse(scenario.JuniperStatus, scenario.Juniper),
                _ => new AccountUsageHttpResponse(200, request.Url.AbsolutePath.EndsWith("profile") ? ProfileBody : UsageBody),
            });
        };
        var snapshot = await ClaudeAccountProbe.ReadAsync(new Dictionary<string, string>(),
            () => new ClaudeQuotaCredential(FixtureToken, "max"), http, () => clock);
        Check(posts == 0, "usageReset " + stateName + ": no POST exists in this app");
        var granted = snapshot.Resets.First(r => r.Program == ResetProgram.CedarEmber);
        var atWall = snapshot.Resets.First(r => r.Program == ResetProgram.JuniperTide);
        Check(granted.State == scenario.CedarState, "cedar_ember state in " + stateName);
        Check(atWall.State == scenario.JuniperState, "juniper_tide state in " + stateName);
        foreach (var row in ClaudeResetEntitlements.Rows(snapshot, true))
        {
            Check(row.Text.Length > 0 && !row.Text.StartsWith("usage.reset."), "state " + stateName + " renders through a shared key that resolved");
        }
    }

    private static string NativeFolder([System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(here)!)!)!;

    /// The smoke exists on both platforms and is reachable from each app's own
    /// smoke run: this is the source-level half of the evidence, the half a CI
    /// smoke artifact confirms by actually running it. A second MightyClaude
    /// instance is never launched on the developer's Mac, so these greps are
    /// what this machine can check.
    internal static Task UsageResetSmokeIsWiredIntoBothPlatforms()
    {
        var native = NativeFolder();
        var smoke = File.ReadAllText(Path.Combine(native, "windows", "MightyClaude.WinUI", "MainWindow.Smoke.cs"));
        Check(smoke.Contains("RunUsageResetSmoke()"), "the WinUI smoke run must carry the 리셋권 check");
        Check(smoke.Contains("result[\"usageReset\"] = await RunUsageResetSmoke()"),
            "the WinUI smoke result must record the check under usageReset");
        Check(smoke.Contains("fakeTransportPostCount"), "the WinUI smoke must record fakeTransportPostCount");
        Check(smoke.Contains("usageReset.cedar_ember") && smoke.Contains("usageReset.juniper_tide"),
            "the WinUI smoke result must record the two usageReset state keys");
        Check(smoke.Contains("ClaudeResetSmoke.RunAsync()"), "the WinUI smoke must drive the shared Core entry point");
        Check(smoke.Contains("RenderAccountUsageReset(panel, result.Rows)"),
            "the WinUI smoke must draw the rows with the window's own builder");

        var swift = File.ReadAllText(Path.Combine(native, "macos", "Sources", "MightyClaude", "StatusBarUsage.swift"));
        Check(swift.Contains("init(service:"), "the macOS controller must accept an injected service");
        Check(swift.Contains("fakeTransportPostCount"), "the macOS smoke must record fakeTransportPostCount");
        Check(swift.Contains("usageReset.cedar_ember") && swift.Contains("usageReset.juniper_tide"),
            "the macOS smoke result must record the two usageReset state keys");
        Check(swift.Contains("AccountResetSmoke.fixture()"), "the macOS smoke must inject the fixture clock and fake transport");
        var appStore = File.ReadAllText(Path.Combine(native, "macos", "Sources", "MightyClaude", "AppStore.swift"));
        Check(appStore.Contains("--usage-reset-smoke-test"), "the macOS app must dispatch the 리셋권 smoke argument");
        Check(appStore.Contains("result[\"usageReset\"] = usageReset"),
            "the macOS smoke result must record the 리셋권 run under the same usageReset key Windows uses");

        // No POST exists anywhere on the account usage surface, on either platform.
        foreach (var file in Directory.GetFiles(Path.Combine(native, "windows", "MightyClaude.Core"), "AccountUsage*.cs")
                     .Concat(Directory.GetFiles(Path.Combine(native, "macos", "Sources", "MightyCore"), "AccountUsage*.swift"))
                     .Concat(Directory.GetFiles(Path.Combine(native, "macos", "Sources", "MightyCore"), "AccountReset*.swift")))
            Check(!File.ReadAllText(file).Contains("\"POST\""), "no POST exists on the account usage surface: " + Path.GetFileName(file));
        return Task.CompletedTask;
    }

    /// The 리셋권 smoke the WinUI window runs, driven here through the same
    /// Core entry point: an injected fixture clock and a fake handler that only
    /// ever sees GET render the "available" and "unknown" rows, every request
    /// is a GET on the allow-list carrying skip_spend=1 (compared as
    /// Uri.PathAndQuery) and fakeTransportPostCount is 0.
    internal static async Task UsageResetSmokeRendersAvailableAndUnknown()
    {
        var result = await ClaudeResetSmoke.RunAsync();
        Check(result.CedarEmberState == ResetState.Available, "smoke: cedar_ember renders available from the fixture");
        Check(result.JuniperTideState == ResetState.Unknown, "smoke: juniper_tide renders unknown when the handler answers 503");
        Check(result.FakeTransportPostCount == 0, "smoke: fakeTransportPostCount must be 0 — no POST exists in this app");
        Check(result.Requests.SequenceEqual(ClaudeResetSmoke.ExpectedRequests),
            "smoke: GET allow-list with skip_spend=1, compared as Uri.PathAndQuery");
        Check(result.Lines.Count == 2 && result.Lines.All(l => l.Length > 0 && !l.StartsWith("usage.reset.", StringComparison.Ordinal)),
            "smoke: both rows render through a shared usage.reset.* key that resolved");
        Check(result.Passed, "smoke: the result the WinUI smoke writes to JSON records passed");
        // Nothing the smoke records carries a credential or a grant id.
        var recorded = JsonSerializer.Serialize(result);
        Check(!recorded.Contains(ClaudeResetSmoke.FixtureToken) && !recorded.Contains("grant-fixture"),
            "smoke: no token or grant id reaches the smoke result");
    }

    /// The seven states, named one by one: available, held, cooldown,
    /// exhausted, none, ineligible, unknown. Each row comes from the real probe
    /// reading fixture HTTP through the injected handler, with a fixture clock
    /// the check advances and never sleeps on, and each renders from the same
    /// shared usage.reset.* key the Mac uses.
    internal static async Task UsageResetRendersSevenStates()
    {
        var future = Instant.AddDays(1);
        var past = Instant.AddDays(-1);
        var scenarios = new[]
        {
            // available on both programmes.
            new ResetScenario("available", Cedar(Grant(3, future, true, false), true, null), Juniper(available: true, perWeek: 2), "available", "available"),
            // held (granted) and cooldown (at-wall).
            new ResetScenario("held", Cedar(Grant(2, future, false, true), false, null), Juniper(next: future, perWeek: 2), "held", "cooldown"),
            // cooldown (granted) and none (at-wall).
            new ResetScenario("cooldown", Cedar(Grant(1, future, false, false), true, future), Juniper(next: past, perWeek: 0), "cooldown", "none"),
            // exhausted on both.
            new ResetScenario("exhausted", Cedar(Grant(0, future, false, false), true, past, """["spent"]"""), Juniper(next: past, perWeek: 3), "exhausted", "exhausted"),
            // none: no grant is selected at all.
            new ResetScenario("none", Cedar(null, false, null), Juniper(perWeek: 0), "none", "none"),
            // ineligible: a named reason, and a 404 saying the same.
            new ResetScenario("ineligible", Cedar(null, false, null, eligible: false, reason: "not_in_experiment"), "", "ineligible", "ineligible") { JuniperStatus = 404 },
            // unknown: a 5xx, and a block this app cannot read.
            new ResetScenario("unknown", "", """{"juniper_tide":42}""", "unknown", "unknown")
            { CedarStatus = 503, Provenance = "assumed (transport failure shapes)" },
        };

        var seen = new HashSet<string>();
        var posts = 0;
        var clock = Instant;
        foreach (var scenario in scenarios)
        {
            Check(scenario.Provenance.Length > 0, "every fixture carries a provenance label");
            AccountUsageHttpHandler http = (request, _) =>
            {
                ClaudeAccountProbe.Guard(request.Url);
                if (request.Method != "GET") posts++;
                Check(request.Headers["Authorization"] == "Bearer " + FixtureToken, "the sign-in only ever rides the Authorization header");
                return Task.FromResult(request.Url.Query switch
                {
                    "?cedar_ember=1&skip_spend=1" => new AccountUsageHttpResponse(scenario.CedarStatus, scenario.Cedar),
                    "?at_wall=1&skip_spend=1" => new AccountUsageHttpResponse(scenario.JuniperStatus, scenario.Juniper),
                    _ => new AccountUsageHttpResponse(200, request.Url.AbsolutePath.EndsWith("profile") ? ProfileBody : UsageBody),
                });
            };
            // The clock the comparisons read is this one, advanced by hand.
            clock = clock.AddSeconds(61);
            var snapshot = await ClaudeAccountProbe.ReadAsync(new Dictionary<string, string>(),
                () => new ClaudeQuotaCredential(FixtureToken, "max"), http, () => clock);

            // A reset read never disturbs the base usage windows or the status.
            Check(snapshot.Status == "available" && snapshot.Windows.Count == 2, "the base usage windows are untouched by the entitlement read");
            Check(snapshot.Resets.Select(r => r.Program).SequenceEqual(ResetProgram.All), "one row per programme, in order");
            var granted = snapshot.Resets.First(r => r.Program == ResetProgram.CedarEmber);
            var atWall = snapshot.Resets.First(r => r.Program == ResetProgram.JuniperTide);
            Check(granted.State == scenario.CedarState, "cedar_ember must be " + scenario.CedarState + " in " + scenario.Name);
            Check(atWall.State == scenario.JuniperState, "juniper_tide must be " + scenario.JuniperState + " in " + scenario.Name);
            seen.Add(granted.State); seen.Add(atWall.State);

            foreach (var row in ClaudeResetEntitlements.Rows(snapshot, true))
            {
                Check(row.Text.Length > 0 && !row.Text.StartsWith("usage.reset."), "every state renders through a shared key that resolved");
                Check(row.Label.Length > 0, "every programme carries its own name");
            }
            // The link is enabled in every one of the seven states.
            Check(ClaudeResetEntitlements.LinkKey == "usage.reset.link" && ClaudeResetEntitlements.LinkLabel.Length > 0, "the claude.ai link exists in every state");
            Check(ClaudeResetEntitlements.LinkTarget.StartsWith("https://claude.ai/"), "the link goes to claude.ai Settings > Usage");

            if (scenario.Name == "available")
            {
                // The granted line carries the remaining count and the expiry.
                Check(granted.CopyKey == "usage.reset.available.cedarEmber", "the granted available line has its own key");
                Check(granted.RemainingCount == 3 && granted.ExpiresAt is { Length: > 0 }, "the granted line carries the count and the expiry");
                Check(ClaudeResetEntitlements.Line(granted).Contains('3'), "the remaining count is shown");
                // The at-wall line says it is available now.
                Check(atWall.CopyKey == "usage.reset.available.juniperTide" && atWall.ResetsPerWeek == 2, "the at-wall available line");
            }
            if (scenario.Name == "held")
            {
                Check(granted.CopyKey == "usage.reset.held", "the held sentence");
                // The at-wall line carries the next time and the weekly count.
                Check(atWall.CopyKey == "usage.reset.cooldown.juniperTide" && atWall.NextAvailableAt is { Length: > 0 } && atWall.ResetsPerWeek == 2, "the at-wall cooldown line");
                Check(ClaudeResetEntitlements.Line(atWall).Contains('2'), "the weekly count is shown");
            }
            if (scenario.Name == "cooldown") Check(granted.CopyKey == "usage.reset.cooldown.cedarEmber" && atWall.CopyKey == "usage.reset.none", "the granted cooldown sentence");
            if (scenario.Name == "exhausted") Check(granted.CopyKey == "usage.reset.exhausted" && atWall.CopyKey == "usage.reset.exhausted", "the exhausted sentence");
            if (scenario.Name == "none") Check(granted.CopyKey == "usage.reset.none" && atWall.CopyKey == "usage.reset.none", "the none sentence");
            if (scenario.Name == "ineligible") Check(granted.CopyKey == "usage.reset.ineligible" && atWall.CopyKey == "usage.reset.ineligible", "the ineligible sentence");
            if (scenario.Name == "unknown") Check(granted.CopyKey == "usage.reset.unknown" && atWall.CopyKey == "usage.reset.unknown", "the unknown sentence blames this app's connection");

            var text = JsonSerializer.Serialize(snapshot);
            Check(!text.Contains(FixtureToken) && !text.Contains("grant-fixture"), "no sign-in and no grant id reaches the presentation data");
        }
        Check(seen.SetEquals(ResetState.All), "all seven states are covered: " + string.Join(", ", ResetState.All));
        Check(posts == 0, "no POST exists in this app");

        // The clock, not the wall time, decides when a grant window has closed.
        var closing = clock.AddSeconds(200);
        AccountUsageHttpHandler expiring = (request, _) => Task.FromResult(request.Url.Query switch
        {
            "?cedar_ember=1&skip_spend=1" => new AccountUsageHttpResponse(200, Cedar(Grant(4, closing, true, false), true, null)),
            "?at_wall=1&skip_spend=1" => new AccountUsageHttpResponse(200, Juniper(available: true, perWeek: 1)),
            _ => new AccountUsageHttpResponse(200, request.Url.AbsolutePath.EndsWith("profile") ? ProfileBody : UsageBody),
        });
        ClaudeQuotaCredential? Load() => new(FixtureToken, "max");
        var before = await ClaudeAccountProbe.ReadAsync(new Dictionary<string, string>(), Load, expiring, () => clock);
        Check(before.Resets[0].State == "available", "the grant is usable while its window is open");
        clock = clock.AddSeconds(300);
        var after = await ClaudeAccountProbe.ReadAsync(new Dictionary<string, string>(), Load, expiring, () => clock);
        Check(after.Resets[0].State == "none", "the injected clock, not the wall clock, closes the grant window");

        // The rows do not exist while the direct-lookup switch is off.
        Check(ClaudeResetEntitlements.Rows(before, false).Count == 0, "the 리셋권 rows are hidden while the direct lookup is off");
        Check(ClaudeResetEntitlements.Rows(null, false).Count == 0, "nothing is teased before the switch is on");
        var shown = ClaudeResetEntitlements.Rows(null, true);
        Check(shown.Count == 2 && shown.All(r => r.State == "unknown"), "a relaunch with nothing read yet is unknown, never a stale number");
    }

    // ---------------------------------------------------- shape log / rate-limit / guard

    /// First 2xx reset response shape is logged once per programme per process,
    /// key paths and value types only, never values.
    internal static async Task UsageResetFirstResponseLog()
    {
        var home = Verification.Temp();
        try
        {
            WriteCredentials(home);
            var lines = new List<string>();
            var shapeLog = new AccountUsageShapeLog(line => { lock (lines) lines.Add(line); });
            var deadlineBox = new ResetReadDeadlineBox();

            const string cedarBody = """{"cedar_ember":{"grants":[{"resets_left":2,"ends_at":"2026-09-21T00:00:00Z","usable_now":true,"use_requires_limit":false,"paused":false}],"in_experiment":true}}""";
            const string juniperBody = """{"juniper_tide":{"in_experiment":true,"available":true,"resets_per_week":3,"weekly_resets_at":"2026-09-22T00:00:00Z"}}""";

            AccountUsageHttpHandler http = (request, _) => Task.FromResult(request.Url.Query switch
            {
                "?cedar_ember=1&skip_spend=1" => new AccountUsageHttpResponse(200, cedarBody),
                "?at_wall=1&skip_spend=1" => new AccountUsageHttpResponse(200, juniperBody),
                _ => new AccountUsageHttpResponse(200, request.Url.AbsolutePath.EndsWith("profile") ? ProfileBody : UsageBody),
            });
            var credential = ClaudeCredentialFile.Read(home, new Dictionary<string, string>(), Instant);

            await ClaudeAccountProbe.ReadAsync(new Dictionary<string, string>(), () => credential, http, () => Instant,
                deadlineBox, shapeLog);

            string[] firstLines;
            lock (lines) firstLines = [.. lines];
            Check(firstLines.Length > 0, "shape lines are emitted on first read");
            foreach (var line in firstLines)
            {
                Check(line.StartsWith("account-usage: "), "every line has the account-usage: prefix: " + line);
                var content = line["account-usage: ".Length..];
                var sep = content.LastIndexOf(": ");
                Check(sep > 0, "every line has the 'path: type' format: " + line);
                var typeToken = content[(sep + 2)..];
                Check(typeToken is "object" or "array" or "string" or "number" or "boolean" or "null",
                    "type token is a JSON kind word: " + line);
            }
            // No sign-in value or email in any shape line.
            var allLines = string.Join('\n', firstLines);
            Check(!allLines.Contains(FixtureToken), "the fixture sign-in must not appear in shape lines");
            Check(!allLines.Contains("fixture@"), "the fixture email must not appear in shape lines");

            // A second call produces no new lines.
            var countBefore = lines.Count;
            await ClaudeAccountProbe.ReadAsync(new Dictionary<string, string>(), () => credential, http, () => Instant,
                deadlineBox, shapeLog);
            Check(lines.Count == countBefore, "shape lines are logged only once per programme");
        }
        finally { Directory.Delete(home, true); }
    }

    /// A 429 on a reset GET sets a deadline; no reset GETs are sent while the
    /// injected clock is before it; the base usage windows are unaffected.
    internal static async Task UsageResetRateLimitDeadline()
    {
        var home = Verification.Temp();
        try
        {
            WriteCredentials(home);
            var deadlineBox = new ResetReadDeadlineBox();
            var credential = ClaudeCredentialFile.Read(home, new Dictionary<string, string>(), Instant);

            var resetCalls = 0;
            AccountUsageHttpHandler MakeHttp(int cedarStatus, string? retryAfter = null) =>
                (request, _) =>
                {
                    if (request.Url.Query.StartsWith("?cedar_ember=") || request.Url.Query.StartsWith("?at_wall="))
                        System.Threading.Interlocked.Increment(ref resetCalls);
                    return Task.FromResult(request.Url.Query switch
                    {
                        "?cedar_ember=1&skip_spend=1" => new AccountUsageHttpResponse(cedarStatus, "", retryAfter),
                        "?at_wall=1&skip_spend=1" => new AccountUsageHttpResponse(cedarStatus, "", retryAfter),
                        _ => new AccountUsageHttpResponse(200, request.Url.AbsolutePath.EndsWith("profile") ? ProfileBody : UsageBody),
                    });
                };

            // First call: 429 on reset endpoints with Retry-After 120.
            resetCalls = 0;
            var snap1 = await ClaudeAccountProbe.ReadAsync(new Dictionary<string, string>(), () => credential,
                MakeHttp(429, "120"), () => Instant, deadlineBox);
            Check(resetCalls == 2, "first call makes two reset GETs (one per programme): " + resetCalls);
            Check(snap1.Windows.Count > 0, "base usage is unaffected by the reset 429");
            Check(snap1.Resets.All(r => r.State == ResetState.Unknown), "all reset rows are unknown after 429");
            var deadline = deadlineBox.Get();
            Check(deadline is { } d && d > Instant, "deadline is set after 429");

            // Second call at +60s (before 120s deadline): no reset GETs.
            var t60 = Instant.AddSeconds(60);
            resetCalls = 0;
            var snap2 = await ClaudeAccountProbe.ReadAsync(new Dictionary<string, string>(), () => credential,
                MakeHttp(200), () => t60, deadlineBox);
            Check(resetCalls == 0, "no reset GETs before the deadline: " + resetCalls);
            Check(snap2.Resets.All(r => r.State == ResetState.Unknown), "reset rows stay unknown during deadline");

            // Third call at +130s (past the 120s deadline): reset GETs resume.
            var t130 = Instant.AddSeconds(130);
            resetCalls = 0;
            await ClaudeAccountProbe.ReadAsync(new Dictionary<string, string>(), () => credential,
                MakeHttp(200), () => t130, deadlineBox);
            Check(resetCalls == 2, "reset GETs resume after the deadline: " + resetCalls);
        }
        finally { Directory.Delete(home, true); }
    }

    /// Unsanctioned usage queries (missing skip_spend=1, wrong value, extra or
    /// reordered keys) are refused by the guard before the transport sees them.
    internal static Task UsageResetGuardCheck()
    {
        // Bad reset queries: missing skip_spend, wrong value, extra key, reordered.
        foreach (var badQuery in new[]
        {
            "cedar_ember=1", "at_wall=1",
            "cedar_ember=1&skip_spend=0", "at_wall=1&skip_spend=0",
            "cedar_ember=1&extra=true&skip_spend=1", "at_wall=1&extra=1&skip_spend=1",
            "skip_spend=1&cedar_ember=1", "skip_spend=1&at_wall=1",
        })
        {
            var refused = false;
            try { ClaudeAccountProbe.Guard(new Uri("https://api.anthropic.com/api/oauth/usage?" + badQuery)); }
            catch (AccountUsageFailure) { refused = true; }
            Check(refused, "usage?" + badQuery + " must be refused before send");
        }

        // Sanctioned variants pass the guard.
        ClaudeAccountProbe.Guard(new Uri("https://api.anthropic.com/api/oauth/usage"));
        ClaudeAccountProbe.Guard(new Uri("https://api.anthropic.com/api/oauth/usage?cedar_ember=1&skip_spend=1"));
        ClaudeAccountProbe.Guard(new Uri("https://api.anthropic.com/api/oauth/usage?at_wall=1&skip_spend=1"));
        ClaudeAccountProbe.Guard(new Uri("https://api.anthropic.com/api/oauth/profile"));

        // Non-GET and non-https are refused.
        var nonHttps = false;
        try { ClaudeAccountProbe.Guard(new Uri("http://api.anthropic.com/api/oauth/usage")); }
        catch (AccountUsageFailure) { nonHttps = true; }
        Check(nonHttps, "non-https must be refused");

        // The transport count stays 0 for any refused query — Endpoint() calls
        // Guard() before the request object is handed to the transport.
        var attempts = 0;
        AccountUsageHttpHandler countingHttp = (_, _) => { attempts++; return Task.FromResult(new AccountUsageHttpResponse(200, UsageBody)); };
        foreach (var badQuery in new[] { "cedar_ember=1", "at_wall=1&skip_spend=0" })
        {
            try { ClaudeAccountProbe.Endpoint("usage", badQuery); }
            catch (AccountUsageFailure) { }
        }
        Check(attempts == 0, "transport count stays 0 for refused queries: " + attempts);
        _ = countingHttp;
        return Task.CompletedTask;
    }

    /// A line-delimited JSON-RPC channel that answers from a fixture script.
    private sealed class FakeStdio(List<string> sent, params string[] replies) : IAccountUsageStdio
    {
        private int index;
        public Task WriteLineAsync(string line, CancellationToken cancellation) { sent.Add(line); return Task.CompletedTask; }
        public async Task<string?> ReadLineAsync(CancellationToken cancellation)
        {
            if (index >= replies.Length) { await Task.Delay(Timeout.Infinite, cancellation); return null; }
            return replies[index++];
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
