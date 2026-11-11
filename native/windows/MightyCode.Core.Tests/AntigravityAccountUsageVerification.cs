using System.Text;
using System.Text.Json;
using MightyCode.Core;

/// <summary>
/// Antigravity's account limits (M/MightyCore/Tests AntigravityAccountUsageTests): <c>agy -p /usage --output-format json</c>, each model
/// group's 5-hour and weekly window as a grouped kind, the chip's most-used group, and a signed-out agy read as a sign-in to check. The
/// reply below is a real agy 1.5.5 output, as the Mac's checks quote it; the probe runs against this test program standing in for agy.
/// </summary>
internal static class AntigravityAccountUsageVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    internal const string Usage = """{"conversation_id":"","status":"SUCCESS","response":"group\tbucket","usage":{"input_tokens":0},"command":{"name":"usage","data":{"description":"Within each group, models share a weekly limit and a 5-hour limit.","groups":[{"name":"Gemini Models","description":"Models within this group: Gemini Flash, Gemini Pro","buckets":[{"id":"gemini-weekly","name":"Weekly Limit Remaining","window":"weekly","remaining_fraction":0.983944296836853,"reset_time":"2026-11-14T03:59:25Z"},{"id":"gemini-5h","name":"Five Hour Limit Remaining","window":"5h","remaining_fraction":0.9997177720069885,"reset_time":"2026-11-10T17:58:15Z"}]},{"name":"Claude and GPT models","description":"Models within this group: Claude Opus, Claude Sonnet, GPT-OSS","buckets":[{"id":"3p-weekly","name":"Weekly Limit Remaining","window":"weekly","remaining_fraction":1,"reset_time":"2026-11-17T13:01:31Z"},{"id":"3p-5h","name":"Five Hour Limit Remaining","window":"5h","remaining_fraction":1,"reset_time":"2026-11-10T18:01:31Z"}]}]}}}""";

    private static T InLanguage<T>(string language, Func<T> body)
    {
        var saved = Locale.LanguagePreference; Locale.LanguagePreference = language; Locale.ResetCache();
        try { return body(); } finally { Locale.LanguagePreference = saved; Locale.ResetCache(); }
    }

    internal static Task TheUsageReplyGivesFourGroupedWindows()
    {
        var value = AntigravityAccountProbe.Map(Usage);
        Check(value.Provider == "gemini" && value.Status == "available", "an available reading");
        Check(value.Windows.Select(w => w.Kind).SequenceEqual(["weekly@gemini", "session@gemini", "weekly@claude_gpt", "session@claude_gpt"]), "four grouped windows: " + string.Join(", ", value.Windows.Select(w => w.Kind)));
        var weekly = value.Windows[0];
        Check(Math.Abs(weekly.UsedPercent - 1.6055703163147) < 0.0001 && weekly.WindowMinutes == 10080, "the weekly window's use and length");
        Check(AccountUsageSupport.Date(weekly.ResetsAt) == DateTimeOffset.Parse("2026-11-14T03:59:25Z"), "its reset: " + weekly.ResetsAt);
        Check(value.Windows[1].WindowMinutes == 300 && value.Windows[2].UsedPercent == 0, "the 5-hour window, and an unused group");
        InLanguage("ko", () =>
        {
            Check(AccountUsageSupport.WindowLabel("session@gemini") == "Gemini · 세션", AccountUsageSupport.WindowLabel("session@gemini"));
            Check(AccountUsageSupport.WindowLabel("weekly@claude_gpt") == "Claude·GPT · 주간", AccountUsageSupport.WindowLabel("weekly@claude_gpt"));
            Check(AccountUsageSupport.WindowLabel("weekly@Other Models") == "Other Models · 주간", "another group keeps agy's name");
            return true;
        });
        InLanguage("en", () => { Check(AccountUsageSupport.WindowLabel("weekly@claude_gpt") == "Claude & GPT · Weekly", AccountUsageSupport.WindowLabel("weekly@claude_gpt")); return true; });
        // The chip keeps the most-used group per window: session first, then weekly.
        Check(AccountUsageStatus.Leading(value.Windows).Select(w => w.Kind).SequenceEqual(["session@gemini", "weekly@gemini"]), "the chip's windows: " + string.Join(", ", AccountUsageStatus.Leading(value.Windows).Select(w => w.Kind)));
        return Task.CompletedTask;
    }

    internal static Task LeadingKeepsUngroupedOrder()
    {
        var windows = new[] { new AccountUsageWindow("Sonnet", 90), new AccountUsageWindow("weekly", 10), new AccountUsageWindow("session", 5) };
        Check(AccountUsageStatus.Leading(windows).Select(w => w.Kind).SequenceEqual(["session", "weekly"]), "the session window, then the weekly one");
        Check(AccountUsageStatus.Leading([new("session@a", 10), new("session@b", 70), new("weekly@a", 40), new("weekly@b", 5)]).Select(w => w.Kind).SequenceEqual(["session@b", "weekly@a"]),
            "the most-used group of each window");
        return Task.CompletedTask;
    }

    internal static Task InvalidBucketsAreSkippedAndUnknownGroupsKeepTheirName()
    {
        const string body = """{"status":"SUCCESS","command":{"data":{"groups":[{"name":"New Models","buckets":[{"id":"n-5h","window":"5h","remaining_fraction":0.25},{"id":"n-w","window":"weekly","remaining_fraction":"x"},{"id":"n-w2","window":"weekly","remaining_fraction":7},{"id":"n-5h-dup","window":"5h","remaining_fraction":0.5}]}]}}}""";
        var value = AntigravityAccountProbe.Map("noise\n" + body + "\n");
        Check(value.Windows.Count == 1 && value.Windows[0].Kind == "session@New Models" && value.Windows[0].UsedPercent == 75, "one valid bucket, under agy's own group name");
        var empty = AntigravityAccountProbe.Map("""{"status":"SUCCESS","command":{"data":{"groups":[]}}}""");
        Check(empty.Status == "unavailable" && empty.Windows.Count == 0 && empty.Detail == AccountUsageStrings.DetailAntigravityNoWindows, "no group, no window");
        return Task.CompletedTask;
    }

    internal static Task SignedOutReadsAsAuthenticationAndGarbageAsInvalid()
    {
        static AccountUsageFailureKind? Failure(string stdout, string stderr = "")
        {
            try { AntigravityAccountProbe.Map(stdout, stderr); return null; } catch (AccountUsageFailure failure) { return failure.Kind; }
        }
        Check(Failure("", "Error: Please sign in to continue.\n") == AccountUsageFailureKind.Authentication, "signed out on standard error");
        Check(Failure("""{"status":"ERROR","response":"Authentication required. Run agy to sign in."}""") == AccountUsageFailureKind.Authentication, "signed out in its reply");
        Check(Failure("<<garbage>> {not json") == AccountUsageFailureKind.InvalidResponse, "anything else is an invalid reply");
        return Task.CompletedTask;
    }

    /// <summary>The probe the app gives the usage service: agy run with exactly the /usage arguments in the temporary folder, or why not.</summary>
    internal static async Task TheAppsProbeRunsAgyUsage()
    {
        var state = Verification.Temp();
        // A file only the temporary folder holds: the fake says whether it found it where it was started, whatever that folder's spelling.
        var marker = Path.Combine(Path.GetTempPath(), Marker(state));
        try
        {
            await File.WriteAllTextAsync(marker, "");
            var agy = Verification.Self("--agy-usage-cli", state);
            var environment = Environment.GetEnvironmentVariables().Keys.Cast<string>().ToDictionary(key => key, key => Environment.GetEnvironmentVariable(key) ?? "");
            var probe = AccountUsageRuntime.Probe((provider, _) => Task.FromResult<CliCommand?>(provider == "gemini" ? agy : null), () => environment, () => state, state);
            var snapshot = await probe("gemini", CancellationToken.None);
            Check(snapshot.Status == "available" && snapshot.Windows.Count == 4, "agy's four windows: " + snapshot.Status + " " + snapshot.Detail);
            Check(JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(Path.Combine(state, "agy-argv.json")))!.SequenceEqual(AntigravityAccountProbe.Arguments) && AntigravityAccountProbe.Arguments.SequenceEqual(["-p", "/usage", "--output-format", "json"]),
                "agy -p /usage --output-format json");
            var ran = JsonSerializer.Deserialize<Dictionary<string, string?>>(await File.ReadAllTextAsync(Path.Combine(state, "agy-run.json")))!;
            Check(ran["NO_COLOR"] == "1" && ran["CI"] == "1", "quiet");
            Check(ran["inTemporaryFolder"] == "yes", "run in the temporary folder, so no project's configuration is read");
            // Signed out: a sign-in to check, not an empty card.
            await File.WriteAllTextAsync(Path.Combine(state, "agy-signed-out"), "");
            AccountUsageFailureKind? kind = null;
            try { await probe("gemini", CancellationToken.None); } catch (AccountUsageFailure failure) { kind = failure.Kind; }
            Check(kind == AccountUsageFailureKind.Authentication, "a signed-out agy is a sign-in to check");
            // No agy: why, in the card.
            var none = AccountUsageRuntime.Probe((_, _) => Task.FromResult<CliCommand?>(null), () => environment, () => state, state);
            string? detail = null;
            try { await none("gemini", CancellationToken.None); } catch (AccountUsageFailure failure) when (failure.Kind == AccountUsageFailureKind.Unavailable) { detail = failure.Detail; }
            Check(detail == AccountUsageStrings.DetailAntigravityNotInstalled, "no agy: " + detail);

            // The bar's chip and the popover's card, through the service as the app reads them.
            var workspace = new Workspace { Id = "w", Path = state };
            await using var service = new AccountUsageService(probe);
            var status = new AccountUsageStatus(service);
            status.Update(new AppSnapshot { Workspaces = [workspace], Sessions = [new() { WorkspaceId = "w", Provider = "gemini" }] });
            File.Delete(Path.Combine(state, "agy-signed-out"));
            await status.RefreshAsync(force: true);
            InLanguage("ko", () =>
            {
                var chip = status.Chips().Single();
                Check(chip.Provider == "gemini" && chip.Text == "Gemini · 세션 0% Gemini · 주간 2%" && !chip.Warning, "the chip: " + chip.Text);
                var card = status.Cards().Single();
                Check(card.Windows.Count == 4 && card.Windows[0].Label == "Gemini · 주간 (7일)" && card.Windows[1].Label == "Gemini · 세션 (5시간)" && card.Detail is null, "the card: " + string.Join(", ", card.Windows.Select(w => w.Label)));
                Check(status.LeadingBars().Single().Bars.Select(bar => bar.Label).SequenceEqual(["Gemini · 세션", "Gemini · 주간"]), "the dashboard's bars");
                return true;
            });
        }
        finally { File.Delete(marker); try { Directory.Delete(state, true); } catch (IOException) { } }
    }

    private static string Marker(string state) => "mighty-agy-usage-" + Path.GetFileName(state.TrimEnd(Path.DirectorySeparatorChar)) + ".marker";

    /// <summary>The fake agy's side (Program.cs <c>--agy-usage-cli state …</c>): records how it was run and answers as agy 1.5.5 does.</summary>
    internal static async Task FakeAgyUsageAsync(string state, string[] args)
    {
        await File.WriteAllTextAsync(Path.Combine(state, "agy-argv.json"), JsonSerializer.Serialize(args));
        await File.WriteAllTextAsync(Path.Combine(state, "agy-run.json"), JsonSerializer.Serialize(new Dictionary<string, string?>
        {
            ["NO_COLOR"] = Environment.GetEnvironmentVariable("NO_COLOR"), ["CI"] = Environment.GetEnvironmentVariable("CI"),
            ["inTemporaryFolder"] = File.Exists(Path.Combine(Environment.CurrentDirectory, Marker(state))) ? "yes" : "no",
        }));
        await using var stdout = Console.OpenStandardOutput(); await using var stderr = Console.OpenStandardError();
        if (!args.SequenceEqual(["-p", "/usage", "--output-format", "json"])) { await stderr.WriteAsync(Encoding.UTF8.GetBytes("Error: unexpected arguments\n")); Environment.ExitCode = 2; return; }
        if (File.Exists(Path.Combine(state, "agy-signed-out"))) { await stderr.WriteAsync(Encoding.UTF8.GetBytes("Error: Please sign in to view your usage. Launch the CLI without arguments to sign in.\n")); Environment.ExitCode = 1; return; }
        await stdout.WriteAsync(Encoding.UTF8.GetBytes(Usage + "\n"));
    }
}
