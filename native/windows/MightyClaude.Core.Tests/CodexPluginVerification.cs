using System.Reflection;
using MightyClaude.Core;

// Behaviour checks for the Codex plugin list. Every name registered in
// Verification.RunAsync starts with "codex plugin".
// A fake ICliRunner answers the Codex subcommands and the capability probes.
// No real codex, claude, gemini, npm or winget process starts, no network is
// touched and the real user profile is never read.
internal static class CodexPluginVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static CliRunResult Ok(string output = "") => new(0, output, "", false);
    private static CliRunResult Err(string error = "") => new(1, "", error, false);
    private static CliRunResult TimedOut() => new(-1, "", "", true);

    private sealed class FakeRunner(Func<string, string[], CliRunResult> script) : ICliRunner
    {
        internal readonly List<(string Executable, string[] Arguments)> Calls = [];
        internal Func<string, string[], Task<CliRunResult>>? Async;
        public Task<CliRunResult> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout,
            CancellationToken cancellation = default, IReadOnlyDictionary<string, string>? environment = null,
            string? workingDirectory = null)
        {
            var args = arguments.ToArray();
            lock (Calls) Calls.Add((executable, args));
            cancellation.ThrowIfCancellationRequested();
            return Async is { } asynchronous ? asynchronous(executable, args) : Task.FromResult(script(executable, args));
        }
        // Reading the help of "plugin add" is still only reading; a call that
        // would change the registry is one that names the verb without --help.
        internal bool Mutated => Calls.Any(c => !c.Arguments.Contains("--help") && c.Arguments.Any(a =>
            a is "install" or "uninstall" or "remove" or "enable" or "disable" or "update" or "add" or "upgrade"));
    }

    // PATH whose only entry holds a file named codex. The fake runner answers.
    private static (CodexPluginReader Reader, string Workspace, string Bin) Fixture(FakeRunner runner, bool installed = true)
    {
        var workspace = Verification.Temp();
        var bin = Verification.Temp();
        var environment = new Dictionary<string, string> { ["PATH"] = bin };
        var executable = Path.Combine(bin, "codex");
        var reader = new CodexPluginReader(runner, environment,
            isExecutable: path => installed && path == executable,
            directoryExists: Directory.Exists);
        return (reader, workspace, executable);
    }

    // Help responses that pass all four capability probes.
    private static CliRunResult ListHelp() => Ok("  codex plugin list [OPTIONS]\n  --json    Output JSON\n  --available  Include catalog\n");
    private static CliRunResult AddHelp() => Ok("  codex plugin add [OPTIONS]\n  --json    Output JSON\n");
    private static CliRunResult MarketListHelp() => Ok("  codex plugin marketplace list [OPTIONS]\n  --json    Output JSON\n");
    private static CliRunResult MarketUpgradeHelp() => Ok("  codex plugin marketplace upgrade [OPTIONS]\n  --json    Output JSON\n");

    private static CliRunResult HelpFor(string[] args) => args switch
    {
        [_, "list", "--help"] => ListHelp(),
        [_, "add", "--help"] => AddHelp(),
        [_, "marketplace", "list", "--help"] => MarketListHelp(),
        [_, "marketplace", "upgrade", "--help"] => MarketUpgradeHelp(),
        _ => Err("unknown"),
    };

    private const string Markets =
        """{"marketplaces":[{"name":"sample","marketplaceSource":{"sourceType":"git","source":"https://example.com/sample"}}]}""";

    private const string EmptyMarkets = """{"marketplaces":[]}""";

    private static string Listing(bool withInstalled = true) =>
        withInstalled
            ? """{"installed":[{"pluginId":"fmt@sample","name":"fmt","marketplaceName":"sample","version":"1.0.0","installed":true,"enabled":true}],"available":[{"pluginId":"fmt@sample","name":"fmt","marketplaceName":"sample","version":"1.0.0","installed":false,"enabled":false,"installPolicy":"AVAILABLE"},{"pluginId":"docs@sample","name":"docs","marketplaceName":"sample","version":"0.5.0","installed":false,"enabled":false,"installPolicy":"AVAILABLE"}]}"""
            : """{"installed":[],"available":[{"pluginId":"fmt@sample","name":"fmt","marketplaceName":"sample","version":"1.0.0","installed":false,"enabled":false,"installPolicy":"AVAILABLE"}]}""";

    // Full read returns a ready snapshot with the expected counts.
    internal static async Task ReadsParsesListing()
    {
        var runner = new FakeRunner((_, args) =>
            args.Contains("--version") ? Ok("1.2.3")
            : args.Contains("--help") ? HelpFor(args)
            : args.Contains("--available") ? Ok(Listing())
            : Ok(Markets));
        var (reader, workspace, _) = Fixture(runner);
        var browser = new ClaudePluginBrowser("codex", new Workspace { Path = workspace });
        browser.Apply(await reader.SnapshotAsync(browser.Workspace));
        Check(browser.IsReady, "ready: " + browser.Snapshot!.Detail);
        Check(browser.InstalledCount == 1, "installed count: " + browser.InstalledCount);
        Check(browser.AvailableCount == 2, "available count: " + browser.AvailableCount);
        Check(browser.Marketplaces.Count == 1 && browser.Marketplaces[0] == "sample", "marketplace: " + string.Join(",", browser.Marketplaces));
        Check(browser.Snapshot!.Detail == CodexPluginStrings.DetailReady, "detail: " + browser.Snapshot.Detail);
        Check(!runner.Mutated, "a read changes nothing");
    }

    // When the help output does not name a required flag: unsupported.
    internal static async Task CapabilityProbeBlocksUnsupportedCli()
    {
        var runner = new FakeRunner((_, args) =>
            args.Contains("--version") ? Ok("1.0.0")
            : args.Contains("--help") && args.Contains("list") && !args.Contains("marketplace") ? Ok("  --json\n") // missing --available
            : args.Contains("--help") ? Ok("  --json\n")
            : Err());
        var (reader, workspace, _) = Fixture(runner);
        var snap = await reader.SnapshotAsync(new Workspace { Path = workspace });
        Check(snap.Status == ClaudePluginStatus.Unsupported, "unsupported when --available missing: " + snap.Status);
        Check(snap.Detail == CodexPluginStrings.DetailUnsupported, "detail: " + snap.Detail);
        Check(!runner.Mutated, "a probe changes nothing");
    }

    // Missing codex on PATH → Missing status, not an exception.
    internal static async Task MissingCodexBecomesMissing()
    {
        var runner = new FakeRunner((_, _) => Err());
        var (reader, workspace, _) = Fixture(runner, installed: false);
        var snap = await reader.SnapshotAsync(new Workspace { Path = workspace });
        Check(snap.Status == ClaudePluginStatus.Missing, "missing CLI: " + snap.Status);
        Check(snap.Detail == CodexPluginStrings.DetailMissingCli, "detail: " + snap.Detail);
    }

    // Malformed or oversized output → Failed, never a ready-but-empty list.
    internal static async Task MalformedOrOversizedNeverBecomesEmptyList()
    {
        var runner = new FakeRunner((_, args) =>
            args.Contains("--version") ? Ok("1.0.0")
            : args.Contains("--help") ? HelpFor(args)
            : args.Contains("--available") ? Ok("not json at all")
            : Ok(Markets));
        var (reader, workspace, _) = Fixture(runner);
        var snap = await reader.SnapshotAsync(new Workspace { Path = workspace });
        Check(snap.Status == ClaudePluginStatus.Failed, "failed on malformed listing: " + snap.Status);
        Check(snap.Detail == PluginStrings.DetailMalformed, "detail: " + snap.Detail);

        // A bare marketplace array instead of {"marketplaces":[...]} is also malformed.
        var runner2 = new FakeRunner((_, args) =>
            args.Contains("--version") ? Ok("1.0.0")
            : args.Contains("--help") ? HelpFor(args)
            : args.Contains("--available") ? Ok(Listing())
            : Ok("""[{"name":"sample"}]""")); // bare array, not envelope
        var (reader2, workspace2, _) = Fixture(runner2);
        var snap2 = await reader2.SnapshotAsync(new Workspace { Path = workspace2 });
        Check(snap2.Status == ClaudePluginStatus.Failed, "failed on bare marketplace array: " + snap2.Status);
    }

    // Rows with a policy other than AVAILABLE or INSTALLED_BY_DEFAULT are
    // excluded and counted in the restricted suffix, not shown as errors.
    internal static async Task RestrictedPolicyExcludesRows()
    {
        var restrictedListing = """{"installed":[],"available":[{"pluginId":"fmt@sample","name":"fmt","marketplaceName":"sample","installed":false,"enabled":false,"installPolicy":"AVAILABLE"},{"pluginId":"sec@sample","name":"sec","marketplaceName":"sample","installed":false,"enabled":false,"installPolicy":"RESTRICTED"}]}""";
        var runner = new FakeRunner((_, args) =>
            args.Contains("--version") ? Ok("1.0.0")
            : args.Contains("--help") ? HelpFor(args)
            : args.Contains("--available") ? Ok(restrictedListing)
            : Ok(Markets));
        var (reader, workspace, _) = Fixture(runner);
        var snap = await reader.SnapshotAsync(new Workspace { Path = workspace });
        Check(snap.Status == ClaudePluginStatus.Ready, "ready: " + snap.Status);
        Check(snap.Available.Count == 1, "only AVAILABLE shown: " + snap.Available.Count);
        Check(snap.Detail.Contains("1"), "restricted count in detail: " + snap.Detail);
    }

    // Codex installed rows are always user scope.
    internal static async Task InstalledRowsAreAlwaysUserScope()
    {
        var runner = new FakeRunner((_, args) =>
            args.Contains("--version") ? Ok("1.0.0")
            : args.Contains("--help") ? HelpFor(args)
            : args.Contains("--available") ? Ok(Listing())
            : Ok(Markets));
        var (reader, workspace, _) = Fixture(runner);
        var browser = new ClaudePluginBrowser("codex", new Workspace { Path = workspace });
        browser.Apply(await reader.SnapshotAsync(browser.Workspace));
        Check(browser.IsReady, "ready");
        Check(browser.InstalledCount == 1, "installed");
        Check(browser.Snapshot!.Installed[0].Scope == "user", "scope: " + browser.Snapshot.Installed[0].Scope);
        // SupportedScopes for Codex is user-only; other scopes would be hidden.
        Check(browser.SupportedScopes.SequenceEqual(["user"]), "scopes: " + string.Join(",", browser.SupportedScopes));

        // A row claiming a scope Codex does not have is neither drawn nor
        // counted, so the tab count always equals the rows the tab lists.
        browser.Apply(browser.Snapshot with
        {
            Installed = [.. browser.Snapshot.Installed,
                new ClaudeInstalledPlugin { PluginId = "stray@sample", Name = "stray", Marketplace = "sample", Scope = "project", ProjectPath = "/elsewhere" }],
        });
        Check(browser.InstalledRows().Count == 1, "a project-scope row is not listed under the Codex title");
        Check(browser.InstalledCount == 1 && browser.TabLabel(ClaudePluginBrowser.InstalledTab) == "설치됨 1",
            "the tab count matches the rows the tab lists: " + browser.TabLabel(ClaudePluginBrowser.InstalledTab));

        // The Claude window counts every scope its CLI reports, as before.
        var claude = new ClaudePluginBrowser("claude", browser.Workspace);
        claude.Apply(browser.Snapshot!);
        Check(claude.InstalledCount == 2 && claude.InstalledRows().Count == 2,
            "the Claude window still counts and lists every scope: " + claude.InstalledCount);
    }

    // Remote workspace returns the remote status without running any commands.
    internal static async Task RemoteWorkspaceRunsNothing()
    {
        var calls = 0;
        var runner = new FakeRunner((_, _) => { calls++; return Ok(); });
        var (reader, workspace, _) = Fixture(runner);
        var snap = await reader.SnapshotAsync(new Workspace { Path = workspace, Remote = new RemoteReference("id", "peer", "Host") });
        Check(snap.Status == ClaudePluginStatus.Remote, "remote: " + snap.Status);
        Check(snap.Detail == PluginStrings.DetailRemote, "remote detail: " + snap.Detail);
        Check(calls == 0, "no CLI calls for a remote workspace");
    }

    // A second concurrent read joins the running read rather than starting a new one.
    internal static async Task SecondRequestJoinsTheRunningRead()
    {
        var called = 0;
        var gate = new TaskCompletionSource();
        var runner = new FakeRunner((_, args) => Ok());
        runner.Async = (_, args) =>
        {
            if (args.Contains("--version")) { Interlocked.Increment(ref called); return gate.Task.ContinueWith(_ => Ok("1.0.0")); }
            if (args.Contains("--help")) return Task.FromResult(HelpFor(args));
            if (args.Contains("--available")) return Task.FromResult(Ok(Listing()));
            return Task.FromResult(Ok(Markets));
        };
        var (reader, workspace, _) = Fixture(runner);
        var ws = new Workspace { Path = workspace };
        var first = reader.SnapshotAsync(ws);
        var second = reader.SnapshotAsync(ws);
        gate.SetResult();
        var (s1, s2) = (await first, await second);
        Check(s1.Status == ClaudePluginStatus.Ready && s2.Status == ClaudePluginStatus.Ready, "both ready");
        Check(called == 1, "only one version call: " + called);
        Check(ReferenceEquals(s1, s2) || s1.UpdatedAt == s2.UpdatedAt, "same snapshot");
    }

    // Browser shows Codex-specific footer, user scope only, no Claude help link.
    internal static async Task BrowserShowsCodexFooterAndScopes()
    {
        var runner = new FakeRunner((_, args) =>
            args.Contains("--version") ? Ok("1.0.0")
            : args.Contains("--help") ? HelpFor(args)
            : args.Contains("--available") ? Ok(Listing(withInstalled: false))
            : Ok(EmptyMarkets));
        var (reader, workspace, _) = Fixture(runner);
        var browser = new ClaudePluginBrowser("codex", new Workspace { Path = workspace });
        browser.Apply(await reader.SnapshotAsync(browser.Workspace));
        Check(browser.IsReady, "ready");
        // Footer note is the Codex-specific (PC) version.
        Check(browser.FooterNote == CodexPluginStrings.FooterNote, "footer: " + browser.FooterNote);
        Check(browser.FooterNote.Contains("이 PC의"), "footer names this PC, not a Mac");
        // No marketplace tab: EmptyMessage shows the DetailNoMarketplaces sentence.
        browser.Tab = ClaudePluginBrowser.MarketplaceTab;
        Check(browser.EmptyMessage == PluginStrings.EmptyAvailable, "empty message: " + browser.EmptyMessage);
        // macOS puts the Codex sentence under that copy instead of Claude's link.
        Check(browser.MarketplaceHelpText == CodexPluginStrings.MarketplaceHelp, "help sentence: " + browser.MarketplaceHelpText);
        Check(!browser.ShowsMarketplaceHelpLink, "no Claude help link for Codex");
        Check(browser.StatusText == CodexPluginStrings.DetailNoMarketplaces, "Codex shows its detail even after a good read: " + browser.StatusText);
        // The Claude window is untouched by any of this.
        var claude = new ClaudePluginBrowser("claude", browser.Workspace);
        claude.Apply(new ClaudePluginSnapshot { Status = ClaudePluginStatus.Ready, Detail = PluginStrings.DetailNoMarketplaces });
        claude.Tab = ClaudePluginBrowser.MarketplaceTab;
        Check(claude.FooterNote == PluginStrings.FooterNote && claude.ShowsMarketplaceHelpLink && claude.MarketplaceHelpText is null,
            "the Claude window keeps its own footer and its help link");
        Check(claude.StatusText is null, "a ready Claude list still shows no status sentence");
        Check(claude.EmptyMessage == PluginStrings.EmptyAvailable, "the Claude empty copy is unchanged");
    }

    // CodexPluginStrings constants match the macOS literals from CodexPluginService.swift
    // and ClaudePluginView.swift (Codex branches). The OS-bound substitution
    // (이 Mac의 → 이 PC의 in FooterNote) is recorded and verified.
    internal static Task StringsMatchMacOS()
    {
        var macOS = new Dictionary<string, string>
        {
            ["DetailReady"] = "Codex 사용자 설치 목록과 현재 작업 폴더의 설정을 반영한 목록입니다. 이미 실행 중인 세션의 로드 상태와 다를 수 있습니다.",
            ["DetailNoMarketplaces"] = "등록된 마켓플레이스가 없습니다. Codex CLI에서 plugin marketplace add로 등록한 뒤 목록을 다시 읽으세요.",
            ["DetailRestrictedSuffix"] = " 설치 정책으로 설치할 수 없는 항목 {count}개는 제외했습니다.",
            ["DetailWarningSuffix"] = " CLI 경고가 있습니다. 일부 목록이 최신 상태가 아닐 수 있으니 진단 출력을 확인하세요.",
            ["DetailMissingCli"] = "Codex CLI가 설치되어 있지 않습니다. 먼저 CLI를 설치하세요.",
            ["DetailUnknownVersion"] = "설치된 Codex CLI 버전을 확인하지 못했습니다.",
            ["DetailUnsupported"] = "설치된 Codex CLI가 필요한 JSON 플러그인 명령을 지원하지 않습니다. 최신 Codex CLI로 업데이트하세요.",
            ["DetailListingFailed"] = "Codex CLI에서 플러그인 목록을 읽지 못했습니다.",
            ["MarketplaceHelp"] = "Codex CLI에서 마켓플레이스를 등록한 뒤 목록을 새로고침하세요.",
            // OS-bound substitution, recorded in docs/windows-plugins.md:
            // macOS reads "이 Mac의 Codex 설치 목록과 마켓플레이스 목록입니다."
            ["FooterNote"] = "이 PC의 Codex 설치 목록과 마켓플레이스 목록입니다. 설치 후 새 Codex 세션을 시작하세요.",
        };

        var actual = typeof(CodexPluginStrings)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .ToDictionary(f => f.Name, f => (string)f.GetRawConstantValue()!);

        var shared = typeof(PluginStrings).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .ToDictionary(f => (string)f.GetRawConstantValue()!, f => f.Name);
        foreach (var (name, value) in actual)
        {
            Check(value.Length > 0, "CodexPluginStrings." + name + " is empty");
            Check(macOS.TryGetValue(name, out var expected), "CodexPluginStrings." + name + " mirrors no macOS literal");
            Check(value == expected, "CodexPluginStrings." + name + " differs from macOS: " + value);
            Check(!shared.ContainsKey(value),
                "CodexPluginStrings." + name + " repeats a PluginStrings sentence; reuse the shared one instead");
        }
        Check(actual.Count == macOS.Count, "the copy table and the class must hold the same fields");
        foreach (var name in macOS.Keys)
            Check(actual.ContainsKey(name), "CodexPluginStrings is missing " + name);

        // OS-bound substitution: FooterNote says "이 PC의", not "이 Mac의".
        Check(CodexPluginStrings.FooterNote.Contains("이 PC의") && !CodexPluginStrings.FooterNote.Contains("Mac"),
            "CodexPluginStrings.FooterNote must say 이 PC의, not 이 Mac의");
        return Task.CompletedTask;
    }


    // Codex /plugins is back in the Windows palette, and no app action is left
    // out of it any more (docs/windows-slash-commands.md).
    internal static Task PaletteOffersCodexPlugins()
    {
        Check(SlashPalette.Builtins("codex").Any(c => c.Action == SlashCommandAction.OpenPlugins && c.Invocation == "plugins"),
            "the Codex palette offers /plugins");
        Check(SlashPalette.UnavailableActions.Length == 0, "no app action is universally unavailable");
        foreach (var provider in new[] { "claude", "codex", "gemini" })
            Check(SlashPalette.Builtins(provider).Length == SlashCommandCatalog.Builtins(provider).Length,
                "the Windows palette offers every macOS built-in for " + provider);
        Check(SlashPalette.Builtins("claude").Any(c => c.Action == SlashCommandAction.OpenPlugins && c.Invocation == "plugin"),
            "the Claude palette is unchanged");
        return Task.CompletedTask;
    }

    // A CLI that exits badly, answers nonsense or runs too long: each becomes a
    // status with its macOS sentence, never an exception the screen must catch.
    internal static async Task FailedRunsAndTimeoutsKeepTheScreenIntact()
    {
        static FakeRunner Runner(Func<string[], CliRunResult> reads) => new((_, args) =>
            args.Contains("--version") ? Ok("1.0.0")
            : args.Contains("--help") ? HelpFor(args)
            : reads(args));

        var listFailed = Runner(args => args.Contains("--available") ? Err("boom") : Ok(Markets));
        var (a, workspaceA, _) = Fixture(listFailed);
        var failed = await a.SnapshotAsync(new Workspace { Path = workspaceA });
        Check(failed.Status == ClaudePluginStatus.Failed && failed.Detail == CodexPluginStrings.DetailListingFailed,
            "a failing list call is explained in the Codex words: " + failed.Detail);
        Check(failed.DiagnosticOutput.Contains("boom"), "the bounded CLI output is kept for the diagnostics disclosure");
        Check(!listFailed.Mutated, "a failing read changes nothing");

        var marketsFailed = Runner(args => args.Contains("--available") ? Ok(Listing()) : Err("no markets"));
        var (b, workspaceB, _) = Fixture(marketsFailed);
        var marketFailure = await b.SnapshotAsync(new Workspace { Path = workspaceB });
        Check(marketFailure.Status == ClaudePluginStatus.Failed && marketFailure.Detail == PluginStrings.DetailMarketplacesFailed,
            "a failing marketplace call is explained: " + marketFailure.Detail);

        var slow = Runner(_ => TimedOut());
        var (c, workspaceC, _) = Fixture(slow);
        var timedOut = await c.SnapshotAsync(new Workspace { Path = workspaceC });
        Check(timedOut.Status == ClaudePluginStatus.Failed && timedOut.Detail == PluginStrings.DetailIncomplete,
            "a slow CLI is explained: " + timedOut.Detail);

        var slowProbe = new FakeRunner((_, args) => args.Contains("--version") ? Ok("1.0.0") : TimedOut());
        var (d, workspaceD, _) = Fixture(slowProbe);
        Check((await d.SnapshotAsync(new Workspace { Path = workspaceD })).Status == ClaudePluginStatus.Unsupported,
            "a probe that never answers is treated as a CLI that cannot do it");

        var unreadableVersion = new FakeRunner((_, args) => args.Contains("--version") ? Err("nope") : Ok(Markets));
        var (e, workspaceE, _) = Fixture(unreadableVersion);
        var unknown = await e.SnapshotAsync(new Workspace { Path = workspaceE });
        Check(unknown.Status == ClaudePluginStatus.Failed && unknown.Detail == CodexPluginStrings.DetailUnknownVersion,
            "an unreadable version is explained: " + unknown.Detail);

        var throwing = new FakeRunner((_, _) => throw new IOException("pipe"));
        var (f, workspaceF, _) = Fixture(throwing);
        Check((await f.SnapshotAsync(new Workspace { Path = workspaceF })).Status is ClaudePluginStatus.Failed or ClaudePluginStatus.Unsupported,
            "a runner that throws never escapes as an exception");

        var gone = new FakeRunner((_, _) => Ok("never"));
        var (g, workspaceG, _) = Fixture(gone);
        Check((await g.SnapshotAsync(new Workspace { Path = Path.Combine(workspaceG, "not-there") })).Detail == PluginStrings.DetailMissingWorkspace,
            "a folder that is gone is explained");
        Check((await g.SnapshotAsync(new Workspace { Path = "relative/path" })).Detail == PluginStrings.DetailInvalidWorkspace,
            "a path that is not rooted is refused");
        Check(gone.Calls.Count == 0, "neither case started a process");

        // A CLI that warns on stderr but still answers keeps its warning.
        var warned = new FakeRunner((_, args) =>
            args.Contains("--version") ? Ok("1.0.0")
            : args.Contains("--help") ? HelpFor(args)
            : args.Contains("--available") ? new CliRunResult(0, Listing(), "Remote catalog unavailable; using cache", false)
            : Ok(Markets));
        var (h, workspaceH, _) = Fixture(warned);
        var stale = await h.SnapshotAsync(new Workspace { Path = workspaceH });
        Check(stale.Status == ClaudePluginStatus.Ready, "a warning is not a failure: " + stale.Status);
        Check(stale.DiagnosticOutput.Contains("Remote catalog unavailable"), "the warning is kept");
        Check(stale.Detail.EndsWith(CodexPluginStrings.DetailWarningSuffix, StringComparison.Ordinal),
            "the list says it may be stale: " + stale.Detail);
    }

    // The Codex plugin window is a real WinUI surface wired into the running app.
    internal static Task WindowIsARealWinUISurfaceWiredIntoTheApp()
    {
        // From this source file, so the check still finds the window when the
        // build output lives outside the repository.
        var winui = ClaudePluginVerification.WinUISource();
        Check(Directory.Exists(winui), "WinUI folder not found: " + winui);

        // Smoke key recorded and drive method exist.
        var smoke = File.ReadAllText(Path.Combine(winui, "MainWindow.Smoke.cs"));
        Check(smoke.Contains("CodexPluginSmokeOutcome.ResultKey") && smoke.Contains("RunCodexPluginSmoke()"),
            "the smoke run must record the codex plugin result under codexPluginList");

        // The Codex plugin menu is wired into the run pane, and /plugins into
        // the palette. A window only the smoke check opens is unfinished.
        var main = File.ReadAllText(Path.Combine(winui, "MainWindow.cs"));
        Check(main.Contains("OpenPluginBrowser("), "the run pane menu must call OpenPluginBrowser");
        Check(main.Contains("pane.Provider is \"claude\" or \"codex\""),
            "the run pane menu must offer the plugin window for Codex too");
        var palette = File.ReadAllText(Path.Combine(winui, "MainWindow.SlashPalette.cs"));
        Check(palette.Contains("SlashCommandAction.OpenPlugins") && palette.Contains("OpenPluginBrowser(pane.Provider)"),
            "/plugins must open the plugin window for the pane's own provider");

        // One window, two readers, picked by the provider in Core's words.
        var source = File.ReadAllText(Path.Combine(winui, "MainWindow.Plugins.cs"));
        Check(source.Contains("IPluginReader reader") && source.Contains("new CodexPluginReader(") && source.Contains("new ClaudePluginReader("),
            "the window must pick its reader from the provider");
        Check(source.Contains("ClaudePluginBrowser.CodexProvider"), "the provider name is Core's constant, not a literal in WinUI");
        Check(source.Contains("browser.FooterNote") && source.Contains("browser.MarketplaceHelpText"),
            "the footer and the empty-marketplace sentence are Core's decision");

        // The smoke run drives that same window with a fixture and puts back
        // the two hooks it set. No codex process starts.
        Check(source.Contains("await ShowPluginBrowser(\"codex\", workspace"), "the smoke run must drive the same window the app opens");
        Check(source.Contains("CodexPluginSmokeSnapshot") && source.Contains("is { } fixture"),
            "the smoke run must show a fixture snapshot instead of starting a codex process");
        Check(source.Contains("smokeCodexPluginRead = beforeRead") && source.Contains("smokeCodexPluginDialog = beforeDialog"),
            "the smoke run must put back the two hooks it set");
        Check(CodexPluginSmokeOutcome.ResultKey == "codexPluginList", "the smoke key is codexPluginList");

        // Looking changes nothing: no control in this window names a change.
        foreach (var part in ClaudePluginVerification.WindowAutomationParts(source))
            Check(!ClaudePluginSupport.NamesAChange(ClaudePluginSupport.AutomationId("codex", part), "codex"),
                "the read-only window must draw no control that names a change: " + part);
        foreach (var change in new[] { "install-fmt", "marketplace-upgrade", "add-marketplace", "scope-picker" })
            Check(ClaudePluginSupport.NamesAChange(ClaudePluginSupport.AutomationId("codex", change), "codex"),
                "a changing control must be caught: " + change);

        // No Codex Korean typed directly in WinUI.
        var plugins = File.ReadAllText(Path.Combine(winui, "MainWindow.Plugins.cs"));
        foreach (var field in typeof(CodexPluginStrings).GetFields(BindingFlags.Public | BindingFlags.Static)
                     .Where(f => f.IsLiteral && f.FieldType == typeof(string)))
            Check(!plugins.Contains("\"" + (string)field.GetRawConstantValue()! + "\""),
                "CodexPluginStrings." + field.Name + " is typed into WinUI instead of read from Core");

        return Task.CompletedTask;
    }
}
