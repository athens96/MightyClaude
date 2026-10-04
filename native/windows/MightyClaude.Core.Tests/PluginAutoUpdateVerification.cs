using System.Text.Json;
using MightyClaude.Core;

internal static class PluginAutoUpdateVerification
{
    private static void Check(bool value, string detail) { if (!value) throw new InvalidOperationException(detail); }
    private sealed class Runner : ICliRunner
    {
        internal List<string[]> Calls = [];
        internal string Listing = "{\"installed\":[],\"available\":[]}";
        internal string Markets = "[]";
        internal Func<string[], CancellationToken, Task<CliRunResult>> Mutation = (_, _) => Task.FromResult(new CliRunResult(0, "{}", "", false));
        public Task<CliRunResult> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellation = default, IReadOnlyDictionary<string, string>? environment = null, string? workingDirectory = null)
        {
            var args = arguments.ToArray(); Calls.Add(args); cancellation.ThrowIfCancellationRequested();
            var output = args.Contains("--help") ? "--json --available" : args is ["--version"] ? "2.1.271" : args is ["plugin", "list", ..] ? Listing : args is ["plugin", "marketplace", "list", ..] ? Markets : null;
            return output is null ? Mutation(args, cancellation) : Task.FromResult(new CliRunResult(0, output, "", false));
        }
        internal string[][] Mutations => Calls.Where(a => !a.Contains("--help") && (a is ["plugin", "update", ..] or ["plugin", "marketplace", "upgrade", ..])).ToArray();
    }
    private static IPluginReader Reader(string provider, Runner runner, string home)
    {
        var env = new Dictionary<string, string> { ["PATH"] = home };
        return provider == "claude" ? new ClaudePluginReader(runner, env, _ => true, _ => true) : new CodexPluginReader(runner, env, _ => true, _ => true);
    }
    internal static async Task DefaultPreferenceAndScheduling()
    {
        Check(new AppSnapshot().AutoUpdatePlugins != false && JsonSerializer.Deserialize<AppSnapshot>("{}", Wire.Json)!.AutoUpdatePlugins != false, "Absent plugin preference must default on, matching Mac migration.");
        foreach (var enabled in new[] { true, false })
        {
            var state = StateStore.Normalize(new AppSnapshot { AutoUpdatePlugins = enabled, AutoUpdateCLIs = false }, restoring: true);
            var roundtrip = JsonSerializer.Deserialize<AppSnapshot>(JsonSerializer.Serialize(state, Wire.Json), Wire.Json)!;
            Check(roundtrip.AutoUpdatePlugins == enabled && roundtrip.AutoUpdateCLIs == false, "Explicit plugin preference must survive normalization and restart independently of CLI preference.");
        }
        var at = DateTimeOffset.UtcNow; var schedule = new CliAutoUpdateSchedule();
        Check(schedule.IsDue(at, true) && !schedule.IsDue(at, false), "Only an enabled launch starts a pass."); schedule.PassStarted(at);
        Check(!schedule.IsDue(at.AddHours(6).AddTicks(-1), true) && schedule.IsDue(at.AddHours(6), true), "Periodic update is due at6h, not each tick.");
        Check(!CliAutoUpdateSchedule.IdleLongEnough(false, false, at, at.AddMinutes(3).AddTicks(-1)) && CliAutoUpdateSchedule.IdleLongEnough(false, false, at, at.AddMinutes(3)), "Provider must be idle for the full3min.");
        Check(!CliAutoUpdateSchedule.IdleLongEnough(true, false, null, at) && !CliAutoUpdateSchedule.IdleLongEnough(false, true, null, at), "Running/starting and queued input both block background mutation.");
        schedule.SkippedBusy("codex"); schedule.SkippedBusy("claude");
        Check(schedule.DueRetries(true, p => p == "claude").SequenceEqual(["claude"]), "Only ready deferred providers retry.");
        schedule.DueRetries(false, _ => true); Check(schedule.DueRetries(true, _ => true).Length == 0, "Turning both preferences off forgets deferred work.");
        await Task.CompletedTask;
    }
    internal static async Task ClaudeScopeApprovalAndBudget()
    {
        var home = Verification.Temp();
        try
        {
            var runner = new Runner { Listing = """{"installed":[{"id":"safe@sample","scope":"user"},{"id":"safe@sample","scope":"user"},{"id":"review@sample","scope":"user"},{"id":"project@sample","scope":"project"},{"id":"local@sample","scope":"local"},{"id":"mighty-bridge@sample","scope":"user"}],"available":[]}""" };
            runner.Mutation = (args, _) => Task.FromResult(new CliRunResult(0, args[2] == "review@sample" ? "marketplace text\n{\"shownCommand\":\"do-not-run\"}" : "{\"outcome\":\"ok\"}", "", false));
            var reader = (ClaudePluginReader)Reader("claude", runner, home);
            var result = await reader.UpdateInstalledAsync(new() { Path = home }, TimeSpan.FromMinutes(5));
            Check(result.Status == "succeeded" && result.Updated!.SequenceEqual(["safe@sample"]) && result.NeedsApproval!.SequenceEqual(["review@sample"]), "Changed marketplace command is deferred, never approved automatically.");
            Check(runner.Mutations.Length == 2 && runner.Mutations.All(a => a.Length == 6 && a[3] == "--scope" && a[4] == "user" && a[5] == "--json" && !a.Contains("-y") && !a.Contains("--yes")), "Only deduplicated user plugins update with exact no-consent argv.");
            runner.Calls.Clear(); await reader.UpdateInstalledAsync(new() { Path = home }, TimeSpan.Zero); Check(runner.Mutations.Length == 0, "Spent budget must start no plugin mutation.");
            runner.Listing = "malformed"; Check((await reader.UpdateInstalledAsync(new() { Path = home }, TimeSpan.FromMinutes(5))).Status == "failed", "Malformed listing is a failure, not an empty successful update.");
        }
        finally { Directory.Delete(home, true); }
    }
    internal static async Task CodexMarketplacesAndGlobalExclusion()
    {
        var home = Verification.Temp();
        try
        {
            var runner = new Runner { Markets = """{"marketplaces":[{"name":"sample","marketplaceSource":{"sourceType":"git","source":"https://example.test/sample"}}]}""" };
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            runner.Mutation = async (_, token) => { entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); return new(0, "{}", "", false); };
            var operations = new PluginOperations(runner); using var stop = new CancellationTokenSource();
            var active = operations.UpdateInstalledAsync("codex", new() { Path = home }, TimeSpan.FromMinutes(5), stop.Token, p => Reader(p, runner, home));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(operations.IsRunning && (await operations.UpdateInstalledAsync("claude", new() { Path = home }, TimeSpan.FromMinutes(5), readerFactory: p => Reader(p, runner, home))).Status == "busy", "Claude/Codex background updates share the same global mutation gate.");
            stop.Cancel(); Check((await active).Status == "cancelled" && !operations.IsRunning, "Shutdown cancellation releases the mutation gate.");
            Check(runner.Mutations.Single().SequenceEqual(["plugin", "marketplace", "upgrade", "--json"]), "Codex upgrades registered Git marketplaces through the exact CLI command.");
            runner.Calls.Clear(); runner.Markets = """{"marketplaces":[{"name":"local","marketplaceSource":{"sourceType":"local","source":"./local"}}]}""";
            Check((await ((CodexPluginReader)Reader("codex", runner, home)).UpgradeMarketplacesAsync(new() { Path = home })).Status == "skipped" && runner.Mutations.Length == 0, "Local-only marketplaces are never upgraded.");
            runner.Markets = """{"marketplaces":[{"name":"sample","marketplaceSource":{"sourceType":"git","source":"https://example.test/sample"}}]}""";
            runner.Mutation = (_, _) => Task.FromResult(new CliRunResult(0, "{\"selectedMarketplaces\":[\"sample\"],\"errors\":[\"fixture\"]}", "", false));
            Check((await ((CodexPluginReader)Reader("codex", runner, home)).UpgradeMarketplacesAsync(new() { Path = home })).Status == "failed", "A successful process with explicit marketplace errors must still fail.");
        }
        finally { Directory.Delete(home, true); }
    }
}
