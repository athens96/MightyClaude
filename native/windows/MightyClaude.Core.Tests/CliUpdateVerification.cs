using System.Text.Json;
using System.Threading;
using MightyClaude.Core;

// The CLI updater proven with a fake runner: no claude, codex, gemini, npm or
// winget process is started and nothing touches the network. Every fixture is a
// private temp tree, so recognition runs against real paths without a real install.
internal static class CliUpdateVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private sealed class FakeRunner(Func<string, string[], CancellationToken, Task<CliRunResult>> script) : ICliRunner
    {
        internal readonly List<(string Executable, string[] Arguments)> Calls = [];
        public async Task<CliRunResult> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout,
            CancellationToken cancellation = default, IReadOnlyDictionary<string, string>? environment = null, string? workingDirectory = null)
        {
            var args = arguments.ToArray();
            lock (Calls) Calls.Add((executable, args));
            cancellation.ThrowIfCancellationRequested();
            return await script(executable, args, cancellation);
        }
        internal IEnumerable<string[]> Updates => Calls.Where(c => !c.Arguments.Contains("--version")).Select(c => c.Arguments);
    }

    private static CliRunResult Ok(string output = "") => new(0, output, "", false);
    private static Task<CliRunResult> Version(string value) => Task.FromResult(Ok(value + "\n"));

    private static string Dir(params string[] parts) { var path = Path.Combine(parts); Directory.CreateDirectory(path); return path; }
    private static string Exe(string directory, string name) { var path = Path.Combine(directory, name); File.WriteAllText(path, ""); return path; }
    private static Dictionary<string, string> Env(params string[] entries) => new(StringComparer.OrdinalIgnoreCase) { ["PATH"] = string.Join(Path.PathSeparator, entries) };

    private static void Package(string root, string name, string version, string binary)
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "package.json"), JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["name"] = name,
            ["version"] = version,
            ["bin"] = new Dictionary<string, string> { [binary] = "cli.js" },
        }));
    }

    /// Nothing installed: skipped, method missing, and not one command is run.
    internal static async Task MissingCliIsSkipped()
    {
        var home = Verification.Temp();
        try
        {
            var runner = new FakeRunner((_, _, _) => Version("2.1.271"));
            var service = new CliUpdateService(runner, Env(Dir(home, "empty")), home);
            var result = await service.UpdateAsync("claude");
            Check(result.Status == "skipped" && result.Method == "missing", "missing CLI must be skipped: " + result.Status + "/" + result.Method);
            Check(result.Detail == CliUpdateStrings.DetailMissing, "missing detail must be the macOS sentence");
            Check(runner.Calls.Count == 0, "a missing CLI must not start any command");
            var installation = await service.InspectAsync("claude");
            Check(installation is { Method: "missing", CanUpdate: false, Version: null }, "inspect must report the missing install");
        }
        finally { Directory.Delete(home, true); }
    }

    /// A native Claude install is updated with Claude Code's own update command.
    internal static async Task NativeClaudeUsesItsOwnUpdateCommand()
    {
        var home = Verification.Temp();
        try
        {
            var versions = Dir(home, ".local", "share", "claude", "versions", "2.1.270");
            var claude = Exe(versions, "claude");
            var version = "2.1.270";
            var runner = new FakeRunner((_, args, _) =>
            {
                if (args.Contains("--version")) return Version(version);
                version = "2.1.271";
                return Task.FromResult(Ok("updated\n"));
            });
            var service = new CliUpdateService(runner, Env(versions), home);

            var installation = await service.InspectAsync("claude");
            Check(installation is { Method: "native", CanUpdate: true, Version: "2.1.270" }, "native install must be recognised: " + installation.Method);
            Check(installation.Detail == CliUpdateStrings.DetailNativeClaude, "native detail must be the macOS sentence");
            Check(runner.Updates.Any() == false, "inspect must not run an installer");

            var result = await service.UpdateAsync("claude");
            Check(result is { Status: "updated", BeforeVersion: "2.1.270", AfterVersion: "2.1.271", Method: "native" }, "native update must report both versions");
            Check(result.Detail == CliUpdateStrings.DetailUpdated, "updated detail must be the macOS sentence");
            var update = runner.Updates.Single();
            Check(update.SequenceEqual(["update"]), "native update must be the CLI's own update command: " + string.Join(" ", update));
            Check(runner.Calls.Single(c => !c.Arguments.Contains("--version")).Executable == claude, "the CLI's own executable must run the update");
            Check(CliUpdateService.ResultRow(result) == "Claude · " + CliUpdateStrings.StatusUpdated, "the row must be {provider} · {status}");
            Check(CliUpdateService.VersionChange(result) == "2.1.270 → 2.1.271", "the version line must be {before} → {after}");
        }
        finally { Directory.Delete(home, true); }
    }

    /// winget upgrades exactly the one package the install location names, with
    /// no --all, no fresh install and no interactive or elevation prompt.
    internal static async Task WingetUpgradesExactlyThePackageItFound()
    {
        var home = Verification.Temp();
        try
        {
            var package = Dir(home, "Local", "Microsoft", "WinGet", "Packages", "Anthropic.ClaudeCode_8wekyb3d8bbwe");
            var target = Exe(package, "claude");
            var links = Dir(home, "Local", "Microsoft", "WinGet", "Links");
            File.CreateSymbolicLink(Path.Combine(links, "claude"), target);
            var tools = Dir(home, "tools");
            var winget = Exe(tools, "winget");

            var runner = new FakeRunner((_, args, _) => args.Contains("--version") ? Version("2.1.271") : Task.FromResult(Ok("No applicable upgrade found.\n")));
            var service = new CliUpdateService(runner, Env(links, tools), home);

            var result = await service.UpdateAsync("claude");
            Check(result is { Status: "current", Method: "winget", BeforeVersion: "2.1.271", AfterVersion: "2.1.271" }, "an unchanged winget package must read current: " + result.Status);
            Check(result.Detail == CliUpdateStrings.DetailUnchanged, "current detail must be the macOS sentence");
            var update = runner.Updates.Single();
            Check(update.SequenceEqual(["upgrade", "--id", "Anthropic.ClaudeCode", "--exact", "--silent",
                "--accept-source-agreements", "--accept-package-agreements", "--disable-interactivity"]), "winget arguments changed: " + string.Join(" ", update));
            Check(!update.Contains("--all") && !update.Contains("install"), "winget must never upgrade everything or install anything new");
            Check(runner.Calls.Single(c => !c.Arguments.Contains("--version")).Executable == winget, "winget itself must run the upgrade");
            Check(CliUpdateService.VersionChange(result) is null, "an unchanged version shows no version line");
        }
        finally { Directory.Delete(home, true); }
    }

    /// The npm plan updates only the official package, in the prefix it is
    /// already installed in, through Node and npm's own cli script.
    internal static async Task NpmUpdatesOnlyTheOfficialPackageInItsPrefix()
    {
        var home = Verification.Temp();
        try
        {
            var prefix = Dir(home, "AppData", "Roaming", "npm");
            Exe(prefix, "codex.cmd");
            Package(Path.Combine(prefix, "node_modules", "@openai", "codex"), "@openai/codex", "0.51.0", "codex");
            var npmScript = Path.Combine(Dir(prefix, "node_modules", "npm", "bin"), "npm-cli.js");
            File.WriteAllText(npmScript, "");
            var nodeDirectory = Dir(home, "nodejs");
            var node = Exe(nodeDirectory, "node.exe");

            var version = "0.51.0";
            var runner = new FakeRunner((_, args, _) =>
            {
                if (args.Contains("--version")) return Version(version);
                version = "0.52.0";
                return Task.FromResult(Ok("added 1 package\n"));
            });
            var service = new CliUpdateService(runner, Env(prefix, nodeDirectory), home);

            var installation = await service.InspectAsync("codex");
            Check(installation is { Method: "npm", CanUpdate: true }, "npm install must be recognised: " + installation.Method);
            Check(installation.Detail == CliUpdateStrings.DetailNpmPlan, "npm detail must be the macOS sentence");

            var result = await service.UpdateAsync("codex");
            Check(result is { Status: "updated", Method: "npm", BeforeVersion: "0.51.0", AfterVersion: "0.52.0" }, "npm update must report both versions");
            var update = runner.Updates.Single();
            Check(update.SequenceEqual([npmScript, "install", "--global", "--prefix", prefix, "@openai/codex@latest", "--no-audit", "--no-fund"]),
                "npm arguments changed: " + string.Join(" ", update));
            Check(runner.Calls.Single(c => !c.Arguments.Contains("--version")).Executable == node, "npm must run through Node, never a shell");
            Check(update.Count(a => a.EndsWith("@latest", StringComparison.Ordinal)) == 1, "exactly one package may be named");
        }
        finally { Directory.Delete(home, true); }
    }

    /// A prerelease npm channel is reported as skipped with the macOS sentence
    /// and is never changed.
    internal static async Task PrereleaseNpmChannelIsSkipped()
    {
        var home = Verification.Temp();
        try
        {
            var prefix = Dir(home, "AppData", "Roaming", "npm");
            Exe(prefix, "gemini.cmd");
            Package(Path.Combine(prefix, "node_modules", "@google", "gemini-cli"), "@google/gemini-cli", "0.9.0-nightly.20260919", "gemini");
            File.WriteAllText(Path.Combine(Dir(prefix, "node_modules", "npm", "bin"), "npm-cli.js"), "");
            var nodeDirectory = Dir(home, "nodejs");
            Exe(nodeDirectory, "node.exe");

            var runner = new FakeRunner((_, _, _) => Version("0.9.0-nightly.20260919"));
            var service = new CliUpdateService(runner, Env(prefix, nodeDirectory), home);

            var result = await service.UpdateAsync("gemini");
            Check(result is { Status: "skipped", Method: "npm" }, "a prerelease channel must be skipped: " + result.Status);
            Check(result.Detail == CliUpdateStrings.DetailNpmPrerelease, "the skipped sentence must be the macOS one");
            Check(!runner.Updates.Any(), "a prerelease channel must not be changed");
        }
        finally { Directory.Delete(home, true); }
    }

    /// An install method the app does not recognise is skipped, not guessed at.
    internal static async Task UnknownInstallMethodIsSkipped()
    {
        var home = Verification.Temp();
        try
        {
            var manual = Dir(home, "tools");
            Exe(manual, "claude.exe");
            var runner = new FakeRunner((_, _, _) => Version("2.1.271"));
            var service = new CliUpdateService(runner, Env(manual), home);
            var result = await service.UpdateAsync("claude");
            Check(result is { Status: "skipped", Method: "unknown", BeforeVersion: "2.1.271" }, "an unknown method must be skipped: " + result.Status);
            Check(result.Detail == CliUpdateStrings.DetailUnknownMethod, "the unknown-method sentence must be the macOS one");
            Check(!runner.Updates.Any(), "an unrecognised install must not be touched");
        }
        finally { Directory.Delete(home, true); }
    }

    /// A failing installer is reported as failed with the exit code, and its
    /// diagnostic output is bounded and control-character free.
    internal static async Task FailingInstallerIsReportedWithBoundedOutput()
    {
        var home = Verification.Temp();
        try
        {
            var versions = Dir(home, ".local", "share", "claude", "versions", "2.1.270");
            Exe(versions, "claude");
            var noisy = new string('x', 20000) + "\u0007tail";
            var runner = new FakeRunner((_, args, _) => args.Contains("--version")
                ? Version("2.1.270")
                : Task.FromResult(new CliRunResult(3, noisy, "권한이 없습니다.", false)));
            var service = new CliUpdateService(runner, Env(versions), home);

            var result = await service.UpdateAsync("claude");
            Check(result is { Status: "failed", Method: "native", AfterVersion: null }, "a failing installer must read failed: " + result.Status);
            Check(result.Detail == CliUpdateStrings.DetailFailedExitTemplate.Replace("{code}", "3"), "the failure sentence must carry the exit code");
            Check(result.Output.Length == CliUpdateService.DiagnosticCharacterCap, "diagnostic output must be bounded: " + result.Output.Length);
            Check(!result.Output.Any(c => char.IsControl(c) && c is not ('\n' or '\t')), "control characters must not reach the details view");
            Check(result.Output.EndsWith("tail\n권한이 없습니다.", StringComparison.Ordinal), "the tail of the output must be kept");
        }
        finally { Directory.Delete(home, true); }
    }

    /// One update at a time: a second request reports busy, cancel stops the
    /// running installer, and closing the app refuses new work.
    internal static async Task SecondRequestIsBusyAndCancelStopsTheRun()
    {
        var home = Verification.Temp();
        try
        {
            var versions = Dir(home, ".local", "share", "claude", "versions", "2.1.270");
            Exe(versions, "claude");
            var started = new TaskCompletionSource();
            var gate = new TaskCompletionSource();
            var runner = new FakeRunner(async (_, args, token) =>
            {
                if (args.Contains("--version")) return Ok("2.1.270\n");
                started.TrySetResult();
                await gate.Task.WaitAsync(token);
                return Ok();
            });
            var service = new CliUpdateService(runner, Env(versions), home);

            var running = service.UpdateAsync("claude");
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var busy = await service.UpdateAsync("codex");
            Check(busy is { Status: "busy", Provider: "codex" }, "a second request must report busy: " + busy.Status);
            Check(busy.Detail == CliUpdateStrings.DetailBusy, "the busy sentence must be the macOS one");

            await service.CancelAsync();
            var cancelled = await running;
            Check(cancelled is { Status: "cancelled", Method: "native" }, "cancel must stop the running installer: " + cancelled.Status);
            Check(cancelled.Detail == CliUpdateStrings.DetailCancelled, "the cancelled sentence must be the macOS one");

            await service.ShutdownAsync();
            var afterShutdown = await service.UpdateAsync("claude");
            Check(afterShutdown is { Status: "cancelled" } && afterShutdown.Detail == CliUpdateStrings.DetailClosing, "closing the app must refuse new updates");
            gate.TrySetResult();
        }
        finally { Directory.Delete(home, true); }
    }

    /// The start-up pass covers all three CLIs and runs only when the saved
    /// switch is a JSON true.
    internal static async Task StartupPassCoversEveryProviderOnlyWhenTheSwitchIsOn()
    {
        Check(!CliUpdateService.ShouldRunAtStartup(new AppSnapshot()), "the default must not update at start");
        Check(!CliUpdateService.ShouldRunAtStartup(new AppSnapshot { AutoUpdateCLIs = false }), "an explicit off must not update at start");
        Check(CliUpdateService.ShouldRunAtStartup(new AppSnapshot { AutoUpdateCLIs = true }), "an explicit on must update at start");

        var home = Verification.Temp();
        try
        {
            var runner = new FakeRunner((_, _, _) => Version("2.1.271"));
            var service = new CliUpdateService(runner, Env(Dir(home, "empty")), home);
            var results = await service.UpdateAllAsync();
            Check(results.Select(r => r.Provider).SequenceEqual(Wire.Providers), "the start-up pass must cover Claude Code, Codex and Gemini in order");
            Check(results.All(r => r.Status == "skipped"), "nothing installed means every provider is skipped");
            Check(runner.Calls.Count == 0, "the start-up pass must not start anything when no CLI is installed");
        }
        finally { Directory.Delete(home, true); }
    }

    // ── CliUpdateCoordinator tests ────────────────────────────────────────────

    private static CliUpdateCoordinator FakeCoordinator(
        Func<string, CancellationToken, Task<CliUpdateResult>>? fn = null)
        => new(fn ?? ((provider, _) => Task.FromResult(new CliUpdateResult(provider, "updated", "1.0", "2.0", "native", CliUpdateStrings.DetailUpdated))));

    /// The coordinator runs all three providers in order, emits StateChanged
    /// after each result, and sets isUpdating / finishedAt correctly.
    internal static async Task CoordinatorTracksStateAndRunsInOrder()
    {
        var changes = new List<(bool isUpdating, int resultCount)>();
        var coordinator = FakeCoordinator();
        coordinator.StateChanged += () => changes.Add((coordinator.IsUpdating, coordinator.Results.Count));

        Check(!coordinator.IsUpdating, "must not be updating before Start");
        Check(coordinator.FinishedAt is null, "finishedAt must be null before any run");

        var started = coordinator.Start();
        Check(started, "Start must return true when idle");
        Check(coordinator.IsUpdating, "must be updating immediately after Start");

        await Verification.Until(() => !coordinator.IsUpdating);

        Check(coordinator.Results.Count == 3, "coordinator must have one result per provider");
        Check(coordinator.Results.Select(r => r.Provider).SequenceEqual(Wire.Providers), "providers must appear in Wire.Providers order");
        Check(coordinator.Results.All(r => r.Status == "updated"), "all results must be updated");
        Check(coordinator.FinishedAt is not null, "finishedAt must be set after the run");
        // StateChanged fires: started (isUpdating=true, 0 results), then once per provider, then done
        Check(changes.Count >= 4, "StateChanged must fire at start and after each provider plus at end");
        Check(changes[0].isUpdating && changes[0].resultCount == 0, "first StateChanged must show isUpdating=true");
        Check(!changes[^1].isUpdating, "last StateChanged must show isUpdating=false");
    }

    /// A second Start call while running returns false; the coordinator keeps
    /// running the first call to completion.
    internal static async Task CoordinatorRefusesSecondStartWhileRunning()
    {
        var gate = new TaskCompletionSource();
        var coordinator = new CliUpdateCoordinator(async (provider, token) =>
        {
            if (provider == Wire.Providers[0]) await gate.Task.WaitAsync(token);
            return new CliUpdateResult(provider, "updated");
        });

        var first = coordinator.Start();
        Check(first, "first Start must succeed");
        Check(coordinator.IsUpdating, "must be updating after first Start");

        var second = coordinator.Start();
        Check(!second, "second Start while running must return false");

        gate.SetResult();
        await Verification.Until(() => !coordinator.IsUpdating);
        Check(coordinator.Results.Count == Wire.Providers.Length, "first run must complete all providers");
    }

    /// BeginAutomaticIfNeeded starts a run only when AutoUpdateCLIs is true and
    /// only fires once regardless of how many times it is called.
    internal static async Task CoordinatorBeginsAutomaticOnlyOnceAndOnlyWhenSwitchIsOn()
    {
        var calls = 0;
        var coordinator = new CliUpdateCoordinator((provider, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(new CliUpdateResult(provider, "updated"));
        });

        coordinator.BeginAutomaticIfNeeded(new AppSnapshot());
        coordinator.BeginAutomaticIfNeeded(new AppSnapshot { AutoUpdateCLIs = false });
        Check(!coordinator.IsUpdating, "BeginAutomaticIfNeeded must not start when the switch is off or default");

        coordinator.BeginAutomaticIfNeeded(new AppSnapshot { AutoUpdateCLIs = true });
        Check(coordinator.IsUpdating, "BeginAutomaticIfNeeded must start when the switch is on");

        await Verification.Until(() => !coordinator.IsUpdating);
        var countAfterFirst = calls;
        Check(countAfterFirst == Wire.Providers.Length, "the automatic run must cover all providers");

        // A second call must not start another run even when the switch is still on.
        coordinator.BeginAutomaticIfNeeded(new AppSnapshot { AutoUpdateCLIs = true });
        await Task.Delay(50);
        Check(calls == countAfterFirst, "BeginAutomaticIfNeeded must not start a second automatic run");
    }

    /// CancelAsync stops the running update, and ShutdownAsync refuses any
    /// subsequent Start call.
    internal static async Task CoordinatorCancelStopsRunAndShutdownRefusesNew()
    {
        var started = new TaskCompletionSource();
        var blocked = new TaskCompletionSource();
        var coordinator = new CliUpdateCoordinator(async (provider, token) =>
        {
            started.TrySetResult();
            await blocked.Task.WaitAsync(token);
            return new CliUpdateResult(provider, "updated");
        });

        coordinator.Start();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await coordinator.CancelAsync();
        Check(!coordinator.IsUpdating, "CancelAsync must stop the running update");

        await coordinator.ShutdownAsync();
        var afterShutdown = coordinator.Start();
        Check(!afterShutdown, "Start after ShutdownAsync must return false");
        blocked.TrySetResult();
    }
}
