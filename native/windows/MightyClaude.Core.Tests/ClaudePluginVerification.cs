using System.Reflection;
using MightyClaude.Core;

// Behaviour checks for the Claude plugin list. Every name registered in
// Verification.RunAsync starts with "claude plugin".
// A fake ICliRunner answers the plugin subcommands, the same idea as the fake
// claude script in ClaudePluginTests.swift: no real claude, codex, gemini, npm
// or winget process starts, no network, no real user profile.
internal static class ClaudePluginVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static CliRunResult Ok(string output = "") => new(0, output, "", false);
    private static CliRunResult Failed(string error = "") => new(1, "", error, false);
    private static CliRunResult TimedOut() => new(-1, "", "", true);

    private sealed class FakeRunner(Func<string, string[], CliRunResult> script) : ICliRunner
    {
        internal readonly List<(string Executable, string[] Arguments, string? WorkingDirectory, IReadOnlyDictionary<string, string>? Environment)> Calls = [];
        internal Func<string, string[], Task<CliRunResult>>? Async;
        public Task<CliRunResult> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout,
            CancellationToken cancellation = default, IReadOnlyDictionary<string, string>? environment = null,
            string? workingDirectory = null)
        {
            var args = arguments.ToArray();
            lock (Calls) Calls.Add((executable, args, workingDirectory, environment));
            cancellation.ThrowIfCancellationRequested();
            return Async is { } asynchronous ? asynchronous(executable, args) : Task.FromResult(script(executable, args));
        }
        internal bool Mutated => Calls.Any(c => c.Arguments.Any(a =>
            a is "install" or "uninstall" or "remove" or "enable" or "disable" or "update" or "add"));
    }

    // A PATH whose only entry holds a file named claude, so the reader's
    // discovery finds one candidate. The fake runner answers it.
    private static (ClaudePluginReader Reader, string Workspace, string Bin) Fixture(FakeRunner runner, bool installed = true)
    {
        var workspace = Verification.Temp();
        var bin = Verification.Temp();
        var environment = new Dictionary<string, string> { ["PATH"] = bin, ["FORCE_AUTOUPDATE_PLUGINS"] = "1" };
        var executable = Path.Combine(bin, "claude");
        var reader = new ClaudePluginReader(runner, environment,
            isExecutable: path => installed && path == executable,
            directoryExists: Directory.Exists);
        return (reader, workspace, executable);
    }

    private static string Listing(string workspacePath, string? otherPath = null)
    {
        static string Json(string value) => System.Text.Json.JsonSerializer.Serialize(value);
        return "{\"installed\":["
            + "{\"id\":\"fmt@sample\",\"scope\":\"user\",\"enabled\":false,\"version\":\"1.2.0\"},"
            + "{\"id\":\"fmt@sample\",\"scope\":\"project\",\"enabled\":true,\"projectPath\":" + Json(workspacePath) + ",\"errors\":[\"\ud53d\uc2a4\ucc98 \uacbd\uace0\"]},"
            + "{\"id\":\"fmt@sample\",\"scope\":\"local\",\"enabled\":true,\"projectPath\":" + Json(otherPath ?? "/somewhere/else") + "},"
            + "{\"id\":\"invalid;touch@sample\",\"scope\":\"user\",\"enabled\":true}"
            + "],\"available\":["
            + "{\"pluginId\":\"fmt@sample\",\"name\":\"fmt\",\"marketplaceName\":\"sample\",\"description\":\"Formats source files\",\"version\":\"1.2.0\",\"source\":{\"source\":\"github\"}},"
            + "{\"pluginId\":\"ghost@nowhere\",\"name\":\"ghost\",\"marketplaceName\":\"nowhere\",\"description\":\"Unregistered marketplace\",\"source\":\"git\"}"
            + "]}";
    }

    private const string Markets = "[{\"name\":\"sample\",\"source\":{\"source\":\"github\"}}]";

    private static FakeRunner Healthy(string workspacePath, string? listing = null, string markets = Markets, string version = "2.1.271 (Claude Code)")
    {
        var body = listing ?? Listing(workspacePath);
        return new FakeRunner((_, args) =>
            args.Contains("--version") ? Ok(version)
            : args.Contains("marketplace") ? Ok(markets)
            : Ok(body));
    }

    // ClaudePluginTests.listsCurrentWorkspaceScopesAndCachedCatalogWithoutMutation
    internal static async Task ListsWorkspaceScopesAndCachedCatalogWithoutMutation()
    {
        var workspace = Verification.Temp();
        var runner = Healthy(workspace);
        var (reader, _, executable) = Fixture(runner);
        var snapshot = await reader.SnapshotAsync(new Workspace { Path = workspace });

        Check(snapshot.Status == ClaudePluginStatus.Ready, "status must be ready: " + snapshot.Status + " / " + snapshot.Detail);
        Check(snapshot.Detail == PluginStrings.DetailReady, "ready detail must be the macOS sentence");
        Check(snapshot.CliVersion == "2.1.271 (Claude Code)", "the CLI version is shown: " + snapshot.CliVersion);
        Check(snapshot.Installed.Count == 2, "user + this workspace's project record only: " + snapshot.Installed.Count);
        Check(snapshot.Installed.Any(p => p.Scope == "user" && p.Enabled == false), "the user record keeps its disabled flag");
        Check(snapshot.Installed.Any(p => p.Scope == "project" && p.Errors.SequenceEqual(new[] { "픽스처 경고" })), "row errors survive");
        Check(!snapshot.Installed.Any(p => p.Scope == "local"), "another project's local record must not be listed");
        Check(snapshot.Installed.All(p => p.Description == "Formats source files"), "the catalog description fills an installed row");
        Check(snapshot.Installed.All(p => p.Marketplace == "sample"), "marketplace comes from the plugin id");
        Check(snapshot.Available.Count == 1 && snapshot.Available[0].Id == "fmt@sample", "only registered marketplaces are offered");
        Check(snapshot.Available[0].SourceKind == "github", "source kind: " + snapshot.Available[0].SourceKind);
        Check(snapshot.Marketplaces.Count == 1 && snapshot.Marketplaces[0] == new ClaudePluginMarketplace("sample", "github"), "marketplace row");
        Check(snapshot.UpdatedAt is { Length: > 0 }, "the read is timestamped");

        Check(runner.Calls.Count == 3, "exactly --version, plugin list and marketplace list: " + runner.Calls.Count);
        Check(runner.Calls.All(c => c.Executable == executable), "every call uses the discovered CLI");
        Check(runner.Calls.All(c => c.WorkingDirectory == Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace))), "every call runs in the workspace folder");
        Check(runner.Calls.Any(c => c.Arguments.SequenceEqual(new[] { "plugin", "list", "--json", "--available" })), "the catalog read needs --available");
        Check(runner.Calls.Any(c => c.Arguments.SequenceEqual(new[] { "plugin", "marketplace", "list", "--json" })), "marketplaces are only listed");
        Check(!runner.Mutated, "looking must never install, enable, remove, update or add anything");
        var environment = runner.Calls[0].Environment!;
        Check(environment["DISABLE_AUTOUPDATER"] == "1" && environment["CLAUDE_CODE_DISABLE_OFFICIAL_MARKETPLACE_AUTOINSTALL"] == "1"
            && environment["CLAUDE_CODE_DISABLE_BACKGROUND_TASKS"] == "1" && environment["GIT_TERMINAL_PROMPT"] == "0",
            "the macOS switches are forced on every call");
        Check(!environment.ContainsKey("FORCE_AUTOUPDATE_PLUGINS"), "a caller's FORCE_AUTOUPDATE_PLUGINS is dropped");
    }

    // An ancestor's local record still applies to a nested workspace.
    internal static async Task NestedWorkspaceSeesItsAncestorsRecord()
    {
        var parent = Verification.Temp();
        var nested = Path.Combine(parent, "packages", "app");
        Directory.CreateDirectory(nested);
        var runner = Healthy(nested, Listing(nested, parent));
        var (reader, _, _) = Fixture(runner);
        var snapshot = await reader.SnapshotAsync(new Workspace { Path = nested });
        Check(snapshot.Status == ClaudePluginStatus.Ready, "status: " + snapshot.Status);
        Check(snapshot.Installed.Any(p => p.Scope == "local"), "the ancestor's local record applies to the nested folder");
        Check(snapshot.Installed.Count == 3, "user + project + ancestor local: " + snapshot.Installed.Count);
        Check(snapshot.Installed.Select(p => p.Id).Distinct().Count() == 3, "each scope/path pair is its own row");
    }

    // ClaudePluginTests.missingOrOldCLIAndEmptySourcesAreExplainedWithoutInstalling
    internal static async Task MissingOldAndEmptyAreExplainedWithoutInstalling()
    {
        var missingRunner = new FakeRunner((_, _) => Ok("never"));
        var (missing, missingWorkspace, _) = Fixture(missingRunner, installed: false);
        var absent = await missing.SnapshotAsync(new Workspace { Path = missingWorkspace });
        Check(absent.Status == ClaudePluginStatus.Missing && absent.Detail == PluginStrings.DetailMissingCli, "a missing CLI is explained: " + absent.Status);
        Check(missingRunner.Calls.Count == 0, "a missing CLI starts no process");

        var workspace = Verification.Temp();
        var oldRunner = Healthy(workspace, version: "2.1.267 (Claude Code)");
        var (old, _, _) = Fixture(oldRunner);
        var unsupported = await old.SnapshotAsync(new Workspace { Path = workspace });
        Check(unsupported.Status == ClaudePluginStatus.Unsupported && unsupported.Detail == PluginStrings.DetailUnsupported, "an old CLI is explained: " + unsupported.Status);
        Check(oldRunner.Calls.Count == 1, "an old CLI is never asked for the list");
        Check(!oldRunner.Mutated, "an old CLI changes nothing");

        var emptyRunner = Healthy(workspace, """{"installed": [], "available": []}""", "[]");
        var (empty, _, _) = Fixture(emptyRunner);
        var none = await empty.SnapshotAsync(new Workspace { Path = workspace });
        Check(none.Status == ClaudePluginStatus.Ready, "an empty but well-formed answer is ready: " + none.Status);
        Check(none.Detail == PluginStrings.DetailNoMarketplaces && none.Detail.Contains("marketplace add"), "the empty-sources sentence names marketplace add");
        Check(none.Marketplaces.Count == 0 && !emptyRunner.Mutated, "nothing was registered on the user's behalf");
    }

    // ClaudePluginTests.malformedOrOversizedCatalogDoesNotBecomeReadyEmpty
    internal static async Task MalformedOrOversizedAnswerNeverBecomesAnEmptyList()
    {
        var workspace = Verification.Temp();
        foreach (var body in new[] { "{broken", "[]", """{"installed":[],"available":"wrong"}""", "", """{"available":[]}""" })
        {
            var runner = Healthy(workspace, body);
            var (reader, _, _) = Fixture(runner);
            var snapshot = await reader.SnapshotAsync(new Workspace { Path = workspace });
            Check(snapshot.Status == ClaudePluginStatus.Failed, "malformed listing must fail, not read empty: " + body);
            Check(snapshot.Detail == PluginStrings.DetailMalformed, "malformed listing uses the macOS sentence");
            Check(!runner.Mutated, "a malformed answer changes nothing");
        }
        var badMarkets = Healthy(workspace, markets: "{}");
        var (marketReader, _, _) = Fixture(badMarkets);
        Check((await marketReader.SnapshotAsync(new Workspace { Path = workspace })).Status == ClaudePluginStatus.Failed, "a non-array marketplace answer must fail");

        var tooMany = "{\"installed\":[" + string.Join(",", Enumerable.Range(0, 10_001).Select(i => "{\"id\":\"p" + i + "@sample\",\"scope\":\"user\"}")) + "],\"available\":[]}";
        var oversized = Healthy(workspace, tooMany);
        var (oversizedReader, _, _) = Fixture(oversized);
        Check((await oversizedReader.SnapshotAsync(new Workspace { Path = workspace })).Status == ClaudePluginStatus.Failed, "more rows than the cap must fail");

        var huge = new string('x', ClaudePluginSupport.MaximumListingBytes + 1);
        Check(ClaudePluginSupport.ParseSnapshot(huge, Markets, workspace, "2.1.271").Status == ClaudePluginStatus.Failed, "output past the byte cap must fail");
    }

    // A CLI that answers nonsense on the pipe, exits badly, or runs too long.
    internal static async Task FailedRunsAndTimeoutsKeepTheScreenIntact()
    {
        var workspace = Verification.Temp();

        var listFailed = new FakeRunner((_, args) => args.Contains("--version") ? Ok("2.1.271") : args.Contains("marketplace") ? Ok(Markets) : Failed("boom"));
        var (a, _, _) = Fixture(listFailed);
        var failed = await a.SnapshotAsync(new Workspace { Path = workspace });
        Check(failed.Status == ClaudePluginStatus.Failed && failed.Detail == PluginStrings.DetailListingFailed, "a failing list call is explained: " + failed.Detail);
        Check(failed.DiagnosticOutput.Contains("boom"), "the bounded CLI output is kept for the diagnostics disclosure");

        var marketsFailed = new FakeRunner((_, args) => args.Contains("--version") ? Ok("2.1.271") : args.Contains("marketplace") ? Failed("no markets") : Ok(Listing(workspace)));
        var (b, _, _) = Fixture(marketsFailed);
        var marketFailure = await b.SnapshotAsync(new Workspace { Path = workspace });
        Check(marketFailure.Status == ClaudePluginStatus.Failed && marketFailure.Detail == PluginStrings.DetailMarketplacesFailed, "a failing marketplace call is explained");

        var slow = new FakeRunner((_, args) => args.Contains("--version") ? Ok("2.1.271") : TimedOut());
        var (c, _, _) = Fixture(slow);
        var timedOut = await c.SnapshotAsync(new Workspace { Path = workspace });
        Check(timedOut.Status == ClaudePluginStatus.Failed && timedOut.Detail == PluginStrings.DetailIncomplete, "a slow CLI is explained: " + timedOut.Detail);

        var unreadableVersion = new FakeRunner((_, args) => args.Contains("--version") ? Failed("nope") : Ok(Markets));
        var (d, _, _) = Fixture(unreadableVersion);
        var unknown = await d.SnapshotAsync(new Workspace { Path = workspace });
        Check(unknown.Status == ClaudePluginStatus.Failed && unknown.Detail == PluginStrings.DetailUnknownVersion, "an unreadable version is explained");

        var throwing = new FakeRunner((_, _) => throw new IOException("pipe"));
        var (e, _, _) = Fixture(throwing);
        Check((await e.SnapshotAsync(new Workspace { Path = workspace })).Status == ClaudePluginStatus.Failed, "a runner that throws never escapes as an exception");

        var gone = new FakeRunner((_, _) => Ok("never"));
        var (f, _, _) = Fixture(gone);
        var missingFolder = await f.SnapshotAsync(new Workspace { Path = Path.Combine(workspace, "not-there") });
        Check(missingFolder.Status == ClaudePluginStatus.Failed && missingFolder.Detail == PluginStrings.DetailMissingWorkspace, "a folder that is gone is explained");
        var relative = await f.SnapshotAsync(new Workspace { Path = "relative/path" });
        Check(relative.Detail == PluginStrings.DetailInvalidWorkspace, "a path that is not rooted is refused");
        Check(gone.Calls.Count == 0, "neither case started a process");
    }

    // A remote workspace shows the two macOS sentences and runs nothing.
    internal static async Task RemoteWorkspaceRunsNothing()
    {
        var runner = new FakeRunner((_, _) => throw new InvalidOperationException("a remote workspace must not run the CLI"));
        var (reader, workspace, _) = Fixture(runner);
        var remote = new Workspace { Path = workspace, Remote = new RemoteReference("connection", "peer", "Host") };
        var snapshot = await reader.SnapshotAsync(remote);
        Check(snapshot.Status == ClaudePluginStatus.Remote, "a remote workspace is remote: " + snapshot.Status);
        Check(snapshot.Detail == PluginStrings.DetailRemote, "the remote sentence is the macOS one with the PC substitution");
        Check(runner.Calls.Count == 0, "a remote workspace starts no process");

        var browser = new ClaudePluginBrowser("claude", remote);
        Check(browser.IsRemote, "the browser knows the workspace is remote");
        browser.Apply(snapshot);
        Check(browser.Snapshot!.Installed.Count == 0 && browser.Snapshot.Available.Count == 0, "a remote read lists nothing");
    }

    // One read at a time: a second request joins the running read.
    internal static async Task SecondRequestJoinsTheRunningRead()
    {
        var workspace = Verification.Temp();
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource<CliRunResult>();
        var runner = Healthy(workspace);
        runner.Async = (_, args) =>
        {
            if (args.Contains("--version")) return Task.FromResult(Ok("2.1.271"));
            if (args.Contains("marketplace")) return Task.FromResult(Ok(Markets));
            started.TrySetResult();
            return release.Task;
        };
        var (reader, _, _) = Fixture(runner);
        var first = reader.SnapshotAsync(new Workspace { Path = workspace });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = reader.SnapshotAsync(new Workspace { Path = workspace });
        Check(ReferenceEquals(first, second), "a second request while one runs is answered from the running one");
        release.SetResult(Ok(Listing(workspace)));
        Check((await first).Status == ClaudePluginStatus.Ready && ReferenceEquals(await first, await second), "both callers get the one answer");
        Check(runner.Calls.Count(c => c.Arguments.Contains("list") && c.Arguments.Contains("--available")) == 1, "only one catalog read ran");

        reader.Shutdown();
        var afterClose = await reader.SnapshotAsync(new Workspace { Path = workspace });
        Check(afterClose.Status == ClaudePluginStatus.Cancelled && afterClose.Detail == PluginStrings.DetailCancelled, "a closed reader admits nothing more");
    }

    // The window: tabs with counts, the marketplace filter with 전체, the search
    // box, the composed row subtitles and the empty copy.
    internal static async Task BrowserTabsFilterSearchAndRowsMatchMacOS()
    {
        var workspace = Verification.Temp();
        var (reader, _, _) = Fixture(Healthy(workspace));
        var browser = new ClaudePluginBrowser("claude", new Workspace { Path = workspace, Name = "Fixture" });
        Check(browser.Title == "Claude 플러그인", "window title: " + browser.Title);
        browser.BeginLoad();
        Check(browser.EmptyMessage == PluginStrings.EmptyLoading, "loading copy");
        browser.Apply(await reader.SnapshotAsync(browser.Workspace));

        Check(browser.TabLabel(ClaudePluginBrowser.InstalledTab) == "설치됨 2", "installed tab: " + browser.TabLabel(ClaudePluginBrowser.InstalledTab));
        Check(browser.TabLabel(ClaudePluginBrowser.MarketplaceTab) == "마켓플레이스 1", "marketplace tab: " + browser.TabLabel(ClaudePluginBrowser.MarketplaceTab));
        Check(browser.Marketplaces.SequenceEqual(new[] { "sample" }), "the filter offers every marketplace the list mentions");
        Check(browser.MarketplaceFilter == "", "전체 is the empty filter");

        var installed = browser.InstalledRows();
        Check(installed.Count == 2, "installed rows: " + installed.Count);
        Check(installed.Any(r => r.Subtitle == "sample · 사용자 · 전체" && r.State == PluginStrings.StateDisabled && r.Version == "1.2.0"), "user row: " + installed[0].Subtitle);
        Check(installed.Any(r => r.Subtitle == "sample · 프로젝트 · 공유" && r.State == PluginStrings.StateEnabled), "project row");
        Check(installed.All(r => r.Description == "Formats source files"), "the description is drawn");

        browser.Tab = ClaudePluginBrowser.MarketplaceTab;
        var available = browser.Rows();
        Check(available.Count == 1 && available[0].Subtitle == "sample · github", "catalog row shows marketplace and source kind: " + available[0].Subtitle);
        Check(available[0].State is null, "a catalog row has no enabled badge");

        browser.MarketplaceFilter = "nowhere";
        Check(browser.Rows().Count == 0 && browser.EmptyMessage == PluginStrings.EmptyFiltered, "an unmatched filter empties the list with the filtered copy");
        browser.MarketplaceFilter = "sample";
        Check(browser.Rows().Count == 1, "the filter keeps its own marketplace");
        browser.Search = "FORMAT";
        Check(browser.Rows().Count == 1, "search is case-insensitive over name and description");
        browser.Search = "없는이름";
        Check(browser.Rows().Count == 0 && browser.EmptyMessage == PluginStrings.EmptyFiltered, "a search with no hit shows the filtered copy");

        browser.Search = ""; browser.MarketplaceFilter = "";
        Check(browser.StatusText is null, "a ready list shows no status sentence");
        browser.Apply(new ClaudePluginSnapshot { Status = ClaudePluginStatus.Missing, Detail = PluginStrings.DetailMissingCli });
        Check(browser.StatusText == PluginStrings.DetailMissingCli, "a failed read explains itself in the macOS words");
        Check(browser.EmptyMessage == PluginStrings.EmptyFailed, "a failed read offers the reload copy");
        Check(browser.MarketplaceFilter == "", "a filter the new list no longer offers falls back to 전체");
    }

    // Asking again re-reads the CLI and replaces the list.
    internal static async Task ReloadReadsAgainAndReplacesTheList()
    {
        var workspace = Verification.Temp();
        var runner = Healthy(workspace);
        var (reader, _, _) = Fixture(runner);
        var browser = new ClaudePluginBrowser("claude", new Workspace { Path = workspace });
        browser.Apply(await reader.SnapshotAsync(browser.Workspace));
        Check(browser.InstalledCount == 2, "first read");
        runner.Async = (_, args) => Task.FromResult(args.Contains("--version") ? Ok("2.1.271")
            : args.Contains("marketplace") ? Ok(Markets)
            : Ok("""{"installed": [], "available": []}"""));
        browser.BeginLoad();
        browser.Apply(await reader.SnapshotAsync(browser.Workspace));
        Check(browser.InstalledCount == 0 && browser.IsReady, "the reload replaced the list: " + browser.InstalledCount);
        Check(runner.Calls.Count == 6, "the reload ran the same three reads again: " + runner.Calls.Count);
        Check(!runner.Mutated, "a reload changes nothing either");
    }

    // The palette offers /plugin for Claude again and still hides Codex /plugins.
    internal static Task PaletteOffersPluginForClaudeOnly()
    {
        Check(SlashPalette.Builtins("claude").Any(c => c.Action == SlashCommandAction.OpenPlugins && c.Invocation == "plugin"),
            "the Claude palette offers /plugin again");
        Check(!SlashPalette.Builtins("codex").Any(c => c.Action == SlashCommandAction.OpenPlugins),
            "Codex /plugins stays out until the Codex plugin feature");
        Check(!SlashPalette.Builtins("gemini").Any(c => c.Action == SlashCommandAction.OpenPlugins),
            "Gemini has no plugin browser on macOS either");
        Check(SlashCommandCatalog.Builtins("codex").Any(c => c.Action == SlashCommandAction.OpenPlugins),
            "the macOS catalog itself is left untouched");
        return Task.CompletedTask;
    }

    // Korean copy: every PluginStrings constant is the macOS literal.
    internal static Task StringsMatchMacOS()
    {
        // ClaudePluginView.swift and ClaudePluginService.swift, by field name.
        var macOS = new Dictionary<string, string>
        {
            ["TitleTemplate"] = "{provider} 플러그인",
            ["TabInstalled"] = "설치됨",
            ["TabMarketplace"] = "마켓플레이스",
            ["TabCountTemplate"] = "{title} {count}",
            ["SearchPlaceholder"] = "이름 또는 설명 검색",
            ["FilterAll"] = "전체",
            ["ButtonReload"] = "목록 새로고침",
            ["ButtonClose"] = "닫기",
            ["DiagnosticsDisclosure"] = "명령 실행 상세",
            ["MarketplaceHelpLink"] = "마켓플레이스 추가 방법",
            ["RemoteTitle"] = "원격 워크스페이스에서는 관리할 수 없습니다.",
            ["RemoteNote"] = "원격 컴퓨터의 MightyClaude에서 플러그인을 관리하세요.",
            ["DirectInstall"] = "직접 설치",
            ["SubtitleTemplate"] = "{left} · {right}",
            ["ScopeLocal"] = "로컬 · 나만",
            ["ScopeProject"] = "프로젝트 · 공유",
            ["ScopeUser"] = "사용자 · 전체",
            ["ScopeManaged"] = "관리자 관리",
            ["StateEnabled"] = "활성",
            ["StateDisabled"] = "비활성",
            ["StateUnknown"] = "상태 미확인",
            ["NoDescription"] = "설명이 제공되지 않았습니다.",
            ["EmptyLoading"] = "플러그인 목록을 불러오는 중…",
            ["EmptyFailed"] = "목록을 불러오지 못했습니다. 목록 새로고침으로 다시 확인하세요.",
            ["EmptyFiltered"] = "검색 조건에 맞는 플러그인이 없습니다.",
            ["EmptyInstalled"] = "설치된 플러그인이 없습니다.",
            ["EmptyAvailable"] = "등록된 마켓플레이스에서 제공한 플러그인이 없습니다.",
            ["ProgressLoading"] = "목록을 불러오는 중…",
            ["FooterNote"] = "현재 폴더의 설정과 저장된 목록입니다. 새 설치는 다음 Claude 실행부터 적용됩니다.",
            ["DetailReady"] = "현재 작업 폴더의 CLI 설정과 등록된 마켓플레이스의 캐시 목록입니다. 이미 실행 중인 세션의 로드 상태와 다를 수 있습니다.",
            ["DetailNoMarketplaces"] = "등록된 마켓플레이스가 없습니다. Claude CLI에서 marketplace add로 등록한 뒤 목록을 다시 읽으세요.",
            // OS-bound substitution, recorded in docs/windows-plugins.md:
            // macOS reads "이 Mac의 설치는 변경하지 않습니다."
            ["DetailRemote"] = "원격 워크스페이스의 플러그인은 해당 호스트에서 관리하세요. 이 PC의 설치는 변경하지 않습니다.",
            ["DetailCancelled"] = "플러그인 조회를 취소했습니다.",
            ["DetailFailed"] = "플러그인 목록을 읽지 못했습니다.",
            ["DetailInvalidWorkspace"] = "로컬 작업 폴더가 올바르지 않습니다.",
            ["DetailMissingWorkspace"] = "작업 폴더를 찾지 못했습니다.",
            ["DetailMissingCli"] = "Claude CLI가 설치되어 있지 않습니다. 먼저 CLI를 설치하세요.",
            ["DetailUnknownVersion"] = "설치된 Claude CLI 버전을 확인하지 못했습니다.",
            ["DetailUnsupported"] = "이 플러그인 관리 화면은 JSON 설치 결과를 지원하는 Claude Code 2.1.268 이상이 필요합니다.",
            ["DetailListingFailed"] = "Claude CLI에서 플러그인 목록을 읽지 못했습니다.",
            ["DetailMarketplacesFailed"] = "등록된 마켓플레이스 목록을 읽지 못했습니다.",
            ["DetailMalformed"] = "플러그인 목록 형식 또는 크기가 올바르지 않습니다. 빈 목록으로 처리하지 않았습니다.",
            ["DetailIncomplete"] = "플러그인 작업을 완료하지 못했습니다. 실행 시간·출력 한도 또는 CLI 접근 상태를 확인하세요.",
        };

        var actual = typeof(PluginStrings)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .ToDictionary(f => f.Name, f => (string)f.GetRawConstantValue()!);

        var seen = new Dictionary<string, string>();
        foreach (var (name, value) in actual)
        {
            Check(value.Length > 0, "PluginStrings." + name + " is empty");
            Check(!seen.TryGetValue(value, out var twin), "PluginStrings." + name + " duplicates " + (seen.TryGetValue(value, out var t) ? t : ""));
            seen[value] = name;
            Check(macOS.TryGetValue(name, out var expected), "PluginStrings." + name + " mirrors no macOS literal");
            Check(value == expected, "PluginStrings." + name + " differs from macOS: " + value);
        }
        foreach (var name in macOS.Keys) Check(actual.ContainsKey(name), "PluginStrings is missing " + name);
        Check(actual.Count == macOS.Count, "the copy table and the class must hold the same fields");

        // The two sentences a remote workspace shows, and the one OS-bound word.
        Check(PluginStrings.DetailRemote.Contains("이 PC의") && !PluginStrings.DetailRemote.Contains("Mac"),
            "the remote sentence names the Windows PC, not a Mac");
        return Task.CompletedTask;
    }

    // The model shapes the Codex feature and the marketplace feature reuse.
    internal static Task ModelsStayReusable()
    {
        var installed = new ClaudeInstalledPlugin { PluginId = "fmt@sample", Name = "fmt", Scope = "local", ProjectPath = "/a" };
        var twin = installed with { ProjectPath = "/a/b" };
        Check(installed.Id != twin.Id, "the row key separates two paths");
        Check(new ClaudeInstalledPlugin { PluginId = "a@b", Scope = "x|y" }.Id != new ClaudeInstalledPlugin { PluginId = "a@b|x", Scope = "y" }.Id,
            "the row key is length-prefixed so no field can forge another row");
        Check(new ClaudePluginSnapshot().Status == ClaudePluginStatus.Failed, "a default snapshot is never ready");
        var operation = new ClaudePluginOperationResult("succeeded", "d");
        Check(operation.Output == "" && operation.Status == "succeeded", "the operation result shape is ready for the marketplace feature");
        Check(ClaudePluginSupport.PluginParts("fmt@sample") == ("fmt", "sample"), "plugin id split");
        Check(ClaudePluginSupport.PluginParts("fmt") is null && ClaudePluginSupport.PluginParts("a@b@c") is null && ClaudePluginSupport.PluginParts("!@b") is null, "a bad plugin id is refused");
        Check(ClaudePluginSupport.SupportedVersion("2.1.268") && ClaudePluginSupport.SupportedVersion("2.2.0 (Claude Code)") && !ClaudePluginSupport.SupportedVersion("2.1.267") && !ClaudePluginSupport.SupportedVersion("x"), "version gate");
        Check(ClaudePluginSupport.Display("a\u0000b\u001Bc", 100) == "abc", "control characters never reach the screen");
        Check(ClaudePluginSupport.Display(new string('a', 50), 10).Length == 10, "display text is bounded");
        Check(ClaudePluginBrowser.ScopeLabel("managed") == PluginStrings.ScopeManaged && ClaudePluginBrowser.ScopeLabel("session") == "session", "scope labels");
        Check(ClaudePluginSmokeOutcome.ResultKey == "claudePluginList", "the smoke key is claudePluginList");
        return Task.CompletedTask;
    }
}
