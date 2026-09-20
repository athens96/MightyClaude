using System.Reflection;
using MightyClaude.Core;

// Behaviour checks for the two plugin mutations macOS has: install a plugin and
// refresh a marketplace. Every name registered in Verification.RunAsync starts
// with "plugin marketplace".
//
// A fake ICliRunner answers every subcommand, so no real claude, codex, gemini,
// npm or winget process starts, no network is touched and the real user profile
// is never read. A real install stays an on-device checklist item.
internal static class PluginMarketplaceVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static CliRunResult Ok(string output = "") => new(0, output, "", false);
    private static CliRunResult Failed(string error = "") => new(1, "", error, false);

    private sealed class FakeRunner(Func<string, string[], CliRunResult> script) : ICliRunner
    {
        internal readonly List<string[]> Calls = [];
        internal Func<string, string[], CancellationToken, Task<CliRunResult>>? Async;

        public Task<CliRunResult> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout,
            CancellationToken cancellation = default, IReadOnlyDictionary<string, string>? environment = null,
            string? workingDirectory = null)
        {
            var args = arguments.ToArray();
            lock (Calls) Calls.Add(args);
            cancellation.ThrowIfCancellationRequested();
            return Async is { } asynchronous
                ? asynchronous(executable, args, cancellation)
                : Task.FromResult(script(executable, args));
        }

        // Reading the help of "plugin add" is still only reading; a run that
        // would change the registry names the verb without --help.
        private static bool Changes(string[] arguments) =>
            !arguments.Contains("--help")
            && arguments is ["plugin", "install", ..] or ["plugin", "add", ..]
                or ["plugin", "marketplace", "update", ..] or ["plugin", "marketplace", "upgrade", ..];

        /// The argument list of the one run that asked for a change, if any.
        internal string[]? Mutation { get { lock (Calls) return Calls.FirstOrDefault(Changes); } }

        internal int Mutations { get { lock (Calls) return Calls.Count(Changes); } }
    }

    // ---- Claude fixtures -------------------------------------------------

    private const string ClaudeMarkets = """[{"name":"sample","source":{"source":"github"}}]""";

    private static string ClaudeListing(string installedScope = "", string workspacePath = "") =>
        "{\"installed\":[" + (installedScope.Length == 0 ? "" :
            "{\"id\":\"fmt@sample\",\"scope\":\"" + installedScope + "\",\"enabled\":true"
            + (installedScope is "project" or "local"
                ? ",\"projectPath\":" + System.Text.Json.JsonSerializer.Serialize(workspacePath) : "") + "}")
        + "],\"available\":["
        + "{\"pluginId\":\"fmt@sample\",\"name\":\"fmt\",\"marketplaceName\":\"sample\",\"description\":\"Formats source files\",\"version\":\"1.2.0\",\"source\":{\"source\":\"github\"}}"
        + "]}";

    private static (ClaudePluginReader Reader, string Workspace) Claude(FakeRunner runner)
    {
        var workspace = Verification.Temp();
        var bin = Verification.Temp();
        var executable = Path.Combine(bin, "claude");
        var reader = new ClaudePluginReader(runner, new Dictionary<string, string> { ["PATH"] = bin },
            isExecutable: path => path == executable, directoryExists: Directory.Exists);
        return (reader, workspace);
    }

    /// A claude that answers the version probe and both read subcommands, and
    /// hands the mutation to the supplied answer.
    private static FakeRunner ClaudeRunner(Func<string[], CliRunResult> mutation, string? listing = null,
        string markets = ClaudeMarkets, string version = "2.1.271 (Claude Code)") =>
        new((_, args) => args switch
        {
            ["--version"] => Ok(version),
            ["plugin", "list", ..] => Ok(listing ?? ClaudeListing()),
            ["plugin", "marketplace", "list", ..] => Ok(markets),
            _ => mutation(args),
        });

    private const string InstallOk = """{"command":"install","outcome":"ok","pluginId":"fmt@sample","scope":"local"}""";

    // ---- Codex fixtures --------------------------------------------------

    private const string CodexMarkets =
        """{"marketplaces":[{"name":"sample","marketplaceSource":{"sourceType":"git","source":"https://example.com/sample"}}]}""";

    private const string CodexLocalMarkets =
        """{"marketplaces":[{"name":"sample","marketplaceSource":{"sourceType":"local","source":"./sample"}}]}""";

    private static string CodexListing(bool installed) =>
        "{\"installed\":[" + (installed
            ? "{\"pluginId\":\"fmt@sample\",\"name\":\"fmt\",\"marketplaceName\":\"sample\",\"installed\":true,\"enabled\":true}" : "")
        + "],\"available\":["
        + "{\"pluginId\":\"fmt@sample\",\"name\":\"fmt\",\"marketplaceName\":\"sample\",\"installed\":false,\"enabled\":false,\"installPolicy\":\"AVAILABLE\"}"
        + "]}";

    private static (CodexPluginReader Reader, string Workspace) Codex(FakeRunner runner)
    {
        var workspace = Verification.Temp();
        var bin = Verification.Temp();
        var executable = Path.Combine(bin, "codex");
        var reader = new CodexPluginReader(runner, new Dictionary<string, string> { ["PATH"] = bin },
            isExecutable: path => path == executable, directoryExists: Directory.Exists);
        return (reader, workspace);
    }

    private static CliRunResult CodexHelp(string[] args) => args switch
    {
        [_, "list", "--help"] => Ok("  codex plugin list [OPTIONS]\n  --json    Output JSON\n  --available  Include catalog\n"),
        [_, "add", "--help"] => Ok("  codex plugin add [OPTIONS]\n  --json    Output JSON\n"),
        [_, "marketplace", "list", "--help"] => Ok("  codex plugin marketplace list [OPTIONS]\n  --json    Output JSON\n"),
        [_, "marketplace", "upgrade", "--help"] => Ok("  codex plugin marketplace upgrade [OPTIONS]\n  --json    Output JSON\n"),
        _ => Failed("unknown"),
    };

    /// A codex whose reads answer from a queue, so the verifying second read an
    /// install does can report the plugin as installed.
    private static FakeRunner CodexRunner(Func<string[], CliRunResult> mutation, Queue<string>? listings = null,
        string markets = CodexMarkets, string version = "codex-cli 0.9.0")
    {
        var queue = listings ?? new Queue<string>();
        return new FakeRunner((_, args) => args switch
        {
            ["--version"] => Ok(version),
            [_, _, "--help"] or [_, _, _, "--help"] => CodexHelp(args),
            ["plugin", "list", ..] => Ok(queue.Count > 0 ? queue.Dequeue() : CodexListing(false)),
            ["plugin", "marketplace", "list", ..] => Ok(markets),
            _ => mutation(args),
        });
    }

    // ---- Claude install --------------------------------------------------

    internal static async Task ClaudeInstallRunsTheCliCommandAndReportsSuccess()
    {
        var runner = ClaudeRunner(_ => Ok(InstallOk));
        var (reader, workspace) = Claude(runner);
        var result = await reader.InstallAsync("fmt@sample", "local", new Workspace { Path = workspace });

        Check(result.Status == ClaudePluginStatus.Succeeded, "an ok outcome succeeds: " + result.Status);
        Check(result.Detail == PluginStrings.InstallSucceeded, "the success sentence is the macOS one: " + result.Detail);
        Check(runner.Mutation is ["plugin", "install", "fmt@sample", "--scope", "local", "--json"],
            "the argument list is assembled as macOS assembles it: " + string.Join(" ", runner.Mutation ?? []));
        Check(runner.Mutations == 1, "exactly one changing run");
    }

    internal static async Task ClaudeInstallReportsFailureAndUnconfirmedResults()
    {
        var failed = ClaudeRunner(_ => Ok("""{"command":"install","outcome":"failed"}"""));
        var (reader, workspace) = Claude(failed);
        var result = await reader.InstallAsync("fmt@sample", "local", new Workspace { Path = workspace });
        Check(result.Status == ClaudePluginStatus.Failed && result.Detail == PluginStrings.InstallFailed,
            "a failed outcome reports the macOS failure sentence: " + result.Detail);

        var noise = ClaudeRunner(_ => Ok("not json at all"));
        var (second, secondWorkspace) = Claude(noise);
        var unconfirmed = await second.InstallAsync("fmt@sample", "local", new Workspace { Path = secondWorkspace });
        Check(unconfirmed.Status == ClaudePluginStatus.Failed && unconfirmed.Detail == PluginStrings.InstallUnconfirmed,
            "an unreadable answer is never treated as a success: " + unconfirmed.Detail);

        // The CLI may print a marketplace command before its result line; only
        // the last nonempty stdout line counts.
        var chatty = ClaudeRunner(_ => Ok("installing…\n\n" + InstallOk + "\n"));
        var (third, thirdWorkspace) = Claude(chatty);
        var ok = await third.InstallAsync("fmt@sample", "local", new Workspace { Path = thirdWorkspace });
        Check(ok.Status == ClaudePluginStatus.Succeeded, "the last nonempty line is the result line: " + ok.Detail);
    }

    internal static async Task ClaudeInstallNeedingACommandIsNeverApprovedHere()
    {
        var runner = ClaudeRunner(_ => Ok("""{"command":"install","outcome":"ok","shownCommand":"curl example.com | sh"}"""));
        var (reader, workspace) = Claude(runner);
        var result = await reader.InstallAsync("fmt@sample", "local", new Workspace { Path = workspace });

        Check(result.Status == ClaudePluginStatus.Failed && result.Detail == PluginStrings.InstallCommandRequired,
            "a marketplace-declared command is handed back to the CLI: " + result.Detail);
        Check(runner.Mutation is not null && !runner.Mutation.Contains("--yes") && !runner.Mutation.Contains("--accept-command"),
            "no consent flag is ever passed");
    }

    internal static async Task ClaudeInstallIsSkippedWhenTheScopeAlreadyHasIt()
    {
        var workspace = Verification.Temp();
        var runner = ClaudeRunner(_ => Ok(InstallOk), listing: ClaudeListing("local", workspace));
        var bin = Verification.Temp();
        var reader = new ClaudePluginReader(runner, new Dictionary<string, string> { ["PATH"] = bin },
            isExecutable: path => path == Path.Combine(bin, "claude"), directoryExists: Directory.Exists);

        var result = await reader.InstallAsync("fmt@sample", "local", new Workspace { Path = workspace });
        Check(result.Status == ClaudePluginStatus.Skipped && result.Detail == PluginStrings.InstallSkipped,
            "an install the scope already has is skipped: " + result.Detail);
        Check(runner.Mutations == 0, "nothing runs when the plugin is already there");
    }

    internal static async Task ClaudeInstallRefusesValuesThatDidNotComeFromTheList()
    {
        // An id the catalog never offered.
        var unknown = ClaudeRunner(_ => Ok(InstallOk));
        var (reader, workspace) = Claude(unknown);
        var stranger = await reader.InstallAsync("evil@elsewhere", "local", new Workspace { Path = workspace });
        Check(stranger.Status == ClaudePluginStatus.Failed && stranger.Detail == PluginStrings.InstallNotFound,
            "an id outside the catalog is refused: " + stranger.Detail);
        Check(unknown.Mutations == 0, "an id outside the catalog never becomes an argument");

        // A scope the picker does not offer, and an id that is not an identifier.
        var badScope = ClaudeRunner(_ => Ok(InstallOk));
        var (second, secondWorkspace) = Claude(badScope);
        var scoped = await second.InstallAsync("fmt@sample", "--json", new Workspace { Path = secondWorkspace });
        Check(scoped.Status == ClaudePluginStatus.Failed && scoped.Detail == PluginStrings.InstallBadIdOrScope,
            "a scope outside the three macOS scopes is refused: " + scoped.Detail);
        var injected = await second.InstallAsync("fmt@sample; rm -rf /", "local", new Workspace { Path = secondWorkspace });
        Check(injected.Status == ClaudePluginStatus.Failed && injected.Detail == PluginStrings.InstallBadIdOrScope,
            "an id that is not name@marketplace is refused: " + injected.Detail);
        lock (badScope.Calls) Check(badScope.Calls.Count == 0, "a refused argument starts no process at all");

        // A marketplace name the list does not carry.
        var refresh = ClaudeRunner(_ => Ok());
        var (third, thirdWorkspace) = Claude(refresh);
        var unregistered = await third.RefreshMarketplaceAsync("nowhere", new Workspace { Path = thirdWorkspace });
        Check(unregistered.Status == ClaudePluginStatus.Failed && unregistered.Detail == PluginStrings.MarketplaceNotRegistered,
            "an unregistered marketplace is refused: " + unregistered.Detail);
        Check(refresh.Mutations == 0, "an unregistered marketplace never becomes an argument");
        var badName = await third.RefreshMarketplaceAsync("sample --json", new Workspace { Path = thirdWorkspace });
        Check(badName.Status == ClaudePluginStatus.Failed && badName.Detail == PluginStrings.MarketplaceBadName,
            "a marketplace name that is not an identifier is refused: " + badName.Detail);
    }

    internal static async Task ClaudeMutationsNeverRunForARemoteWorkspace()
    {
        var runner = ClaudeRunner(_ => Ok(InstallOk));
        var (reader, workspace) = Claude(runner);
        var remote = new Workspace { Path = workspace, Remote = new RemoteReference("connection", "peer", "Host") };

        var install = await reader.InstallAsync("fmt@sample", "local", remote);
        var refresh = await reader.RefreshMarketplaceAsync("sample", remote);
        var refused = await reader.InstallAsync("fmt@sample", "nonsense", remote);

        foreach (var result in new[] { install, refresh, refused })
        {
            Check(result.Status == ClaudePluginStatus.Remote, "a remote workspace answers remote: " + result.Status);
            Check(result.Detail == PluginStrings.DetailRemote, "the remote sentence is the macOS one: " + result.Detail);
        }
        lock (runner.Calls) Check(runner.Calls.Count == 0, "a remote workspace starts no process");
    }

    internal static async Task ClaudeInstallReportsAnUnsupportedCliVersion()
    {
        var runner = ClaudeRunner(_ => Ok(InstallOk), version: "2.1.267 (Claude Code)");
        var (reader, workspace) = Claude(runner);
        var result = await reader.InstallAsync("fmt@sample", "local", new Workspace { Path = workspace });

        Check(result.Status == ClaudePluginStatus.Failed, "an old CLI cannot install: " + result.Status);
        Check(result.Detail == PluginStrings.DetailUnsupported, "the unsupported sentence is the macOS one: " + result.Detail);
        Check(runner.Mutations == 0, "an old CLI is never asked to install");
    }

    internal static async Task ClaudeInstallIsCancellable()
    {
        var gate = new TaskCompletionSource();
        var runner = ClaudeRunner(_ => Ok(InstallOk));
        runner.Async = async (_, args, token) =>
        {
            if (args is ["plugin", "install", ..])
            {
                await gate.Task.WaitAsync(token);
                return Ok(InstallOk);
            }
            return args switch
            {
                ["--version"] => Ok("2.1.271 (Claude Code)"),
                ["plugin", "marketplace", "list", ..] => Ok(ClaudeMarkets),
                _ => Ok(ClaudeListing()),
            };
        };
        var (reader, workspace) = Claude(runner);
        using var cancel = new CancellationTokenSource();
        var running = reader.InstallAsync("fmt@sample", "local", new Workspace { Path = workspace }, cancel.Token);
        while (runner.Mutations == 0) await Task.Delay(5);
        await cancel.CancelAsync();
        var result = await running;

        Check(result.Status == ClaudePluginStatus.Cancelled, "a cancelled install reports cancelled: " + result.Status);
        Check(result.Detail == PluginStrings.OperationCancelled, "the cancel sentence is the macOS one: " + result.Detail);
        gate.TrySetResult();
    }

    internal static async Task ClaudeRunsOneOperationAtATime()
    {
        var gate = new TaskCompletionSource();
        var runner = ClaudeRunner(_ => Ok(InstallOk));
        runner.Async = async (_, args, token) =>
        {
            if (args is ["plugin", "install", ..]) { await gate.Task.WaitAsync(token); return Ok(InstallOk); }
            return args switch
            {
                ["--version"] => Ok("2.1.271 (Claude Code)"),
                ["plugin", "marketplace", "list", ..] => Ok(ClaudeMarkets),
                _ => Ok(ClaudeListing()),
            };
        };
        var (reader, workspace) = Claude(runner);
        var first = reader.InstallAsync("fmt@sample", "local", new Workspace { Path = workspace });
        while (runner.Mutations == 0) await Task.Delay(5);

        var second = await reader.InstallAsync("fmt@sample", "user", new Workspace { Path = workspace });
        Check(second.Status == ClaudePluginStatus.Busy, "a second operation is refused while one runs: " + second.Status);
        Check(second.Detail == PluginStrings.OperationBusy, "the busy sentence is the macOS one: " + second.Detail);
        Check(runner.Mutations == 1, "the refused operation started no process");

        gate.SetResult();
        Check((await first).Status == ClaudePluginStatus.Succeeded, "the first operation still finishes");

        var third = await reader.InstallAsync("fmt@sample", "local", new Workspace { Path = workspace });
        Check(third.Status == ClaudePluginStatus.Succeeded, "the next operation is admitted once the first ended: " + third.Status);
    }

    // ---- Claude marketplace refresh -------------------------------------

    internal static async Task ClaudeMarketplaceRefreshRunsTheCliCommand()
    {
        var runner = ClaudeRunner(_ => Ok("updated"));
        var (reader, workspace) = Claude(runner);
        var result = await reader.RefreshMarketplaceAsync("sample", new Workspace { Path = workspace });

        Check(result.Status == ClaudePluginStatus.Succeeded, "a clean exit succeeds: " + result.Status);
        Check(result.Detail == PluginStrings.MarketplaceRefreshSucceeded, "the success sentence is the macOS one: " + result.Detail);
        Check(runner.Mutation is ["plugin", "marketplace", "update", "sample"],
            "the refresh argument list is the macOS one: " + string.Join(" ", runner.Mutation ?? []));

        var broken = ClaudeRunner(_ => Failed("network down"));
        var (second, secondWorkspace) = Claude(broken);
        var failure = await second.RefreshMarketplaceAsync("sample", new Workspace { Path = secondWorkspace });
        Check(failure.Status == ClaudePluginStatus.Failed && failure.Detail == PluginStrings.MarketplaceRefreshFailed,
            "a failing refresh reports the macOS failure sentence: " + failure.Detail);
        Check(failure.Output.Contains("network down"), "the bounded CLI output is kept for the disclosure");
    }

    // ---- Codex ------------------------------------------------------------

    internal static async Task CodexInstallRunsAddAtUserLevelAndVerifiesTheResult()
    {
        var listings = new Queue<string>([CodexListing(false), CodexListing(true)]);
        var runner = CodexRunner(_ => Ok("""{"pluginId":"fmt@sample","name":"fmt","marketplaceName":"sample","installedPath":"/tmp/fmt"}"""), listings);
        var (reader, workspace) = Codex(runner);
        var result = await reader.InstallAsync("fmt@sample", "user", new Workspace { Path = workspace });

        Check(result.Status == ClaudePluginStatus.Succeeded, "a verified Codex install succeeds: " + result.Status + " " + result.Detail);
        Check(result.Detail == CodexPluginStrings.InstallSucceeded, "the Codex success sentence is the macOS one: " + result.Detail);
        Check(runner.Mutation is ["plugin", "add", "fmt@sample", "--json"],
            "the Codex install argument list is the macOS one: " + string.Join(" ", runner.Mutation ?? []));
    }

    internal static async Task CodexInstallRefusesAnotherScopeAndAnIdNotFromTheList()
    {
        var runner = CodexRunner(_ => Ok("{}"));
        var (reader, workspace) = Codex(runner);

        var scoped = await reader.InstallAsync("fmt@sample", "local", new Workspace { Path = workspace });
        Check(scoped.Status == ClaudePluginStatus.Failed && scoped.Detail == PluginStrings.InstallBadIdOrScope,
            "Codex installs at user level only: " + scoped.Detail);

        var stranger = await reader.InstallAsync("evil@elsewhere", "user", new Workspace { Path = workspace });
        Check(stranger.Status == ClaudePluginStatus.Failed && stranger.Detail == CodexPluginStrings.InstallNotFound,
            "an id outside the policy-filtered catalog is refused: " + stranger.Detail);
        Check(runner.Mutations == 0, "neither refusal became an argument");

        var already = CodexRunner(_ => Ok("{}"), new Queue<string>([CodexListing(true)]));
        var (second, secondWorkspace) = Codex(already);
        var skipped = await second.InstallAsync("fmt@sample", "user", new Workspace { Path = secondWorkspace });
        Check(skipped.Status == ClaudePluginStatus.Skipped && skipped.Detail == CodexPluginStrings.InstallSkipped,
            "an already installed Codex plugin is skipped: " + skipped.Detail);
    }

    internal static async Task CodexInstallReportsAnUnverifiableResult()
    {
        var mismatched = CodexRunner(_ => Ok("""{"pluginId":"other@sample","name":"other","marketplaceName":"sample","installedPath":"/tmp/x"}"""));
        var (reader, workspace) = Codex(mismatched);
        var wrong = await reader.InstallAsync("fmt@sample", "user", new Workspace { Path = workspace });
        Check(wrong.Status == ClaudePluginStatus.Failed && wrong.Detail == PluginStrings.InstallUnconfirmed,
            "a result naming another plugin is refused: " + wrong.Detail);

        // The CLI answered for the right plugin, but the list still does not show it.
        var unverified = CodexRunner(_ => Ok("""{"pluginId":"fmt@sample","name":"fmt","marketplaceName":"sample","installedPath":"/tmp/fmt"}"""));
        var (second, secondWorkspace) = Codex(unverified);
        var result = await second.InstallAsync("fmt@sample", "user", new Workspace { Path = secondWorkspace });
        Check(result.Status == ClaudePluginStatus.Failed && result.Detail == CodexPluginStrings.InstallVerifyFailed,
            "an install the list does not confirm is reported: " + result.Detail);

        var broken = CodexRunner(_ => Failed("policy denied"));
        var (third, thirdWorkspace) = Codex(broken);
        var failure = await third.InstallAsync("fmt@sample", "user", new Workspace { Path = thirdWorkspace });
        Check(failure.Status == ClaudePluginStatus.Failed && failure.Detail == CodexPluginStrings.OperationFailed,
            "a failing Codex run reports the macOS sentence: " + failure.Detail);
    }

    internal static async Task CodexMarketplaceUpgradeRunsOnlyForARegisteredGitSource()
    {
        var runner = CodexRunner(_ => Ok("""{"selectedMarketplaces":["sample"],"upgradedRoots":["/tmp/sample"],"errors":[]}"""));
        var (reader, workspace) = Codex(runner);
        var result = await reader.RefreshMarketplaceAsync("sample", new Workspace { Path = workspace });
        Check(result.Status == ClaudePluginStatus.Succeeded && result.Detail == PluginStrings.MarketplaceRefreshSucceeded,
            "a Git marketplace upgrade succeeds: " + result.Detail);
        Check(runner.Mutation is ["plugin", "marketplace", "upgrade", "sample", "--json"],
            "the Codex refresh argument list is the macOS one: " + string.Join(" ", runner.Mutation ?? []));

        var local = CodexRunner(_ => Ok("{}"), markets: CodexLocalMarkets);
        var (second, secondWorkspace) = Codex(local);
        var skipped = await second.RefreshMarketplaceAsync("sample", new Workspace { Path = secondWorkspace });
        Check(skipped.Status == ClaudePluginStatus.Skipped && skipped.Detail == CodexPluginStrings.MarketplaceNotGit,
            "a non-Git source cannot be upgraded: " + skipped.Detail);
        Check(local.Mutations == 0, "a non-Git source never becomes an argument");

        var unknown = CodexRunner(_ => Ok("{}"));
        var (third, thirdWorkspace) = Codex(unknown);
        var unregistered = await third.RefreshMarketplaceAsync("nowhere", new Workspace { Path = thirdWorkspace });
        Check(unregistered.Status == ClaudePluginStatus.Failed && unregistered.Detail == PluginStrings.MarketplaceNotRegistered,
            "an unregistered Codex marketplace is refused: " + unregistered.Detail);

        var noisy = CodexRunner(_ => Ok("""{"selectedMarketplaces":["other"],"upgradedRoots":[],"errors":[]}"""));
        var (fourth, fourthWorkspace) = Codex(noisy);
        var unconfirmed = await fourth.RefreshMarketplaceAsync("sample", new Workspace { Path = fourthWorkspace });
        Check(unconfirmed.Status == ClaudePluginStatus.Failed && unconfirmed.Detail == CodexPluginStrings.MarketplaceRefreshUnconfirmed,
            "an answer about another marketplace is refused: " + unconfirmed.Detail);
    }

    internal static async Task CodexMutationsNeverRunForARemoteWorkspace()
    {
        var runner = CodexRunner(_ => Ok("{}"));
        var (reader, workspace) = Codex(runner);
        var remote = new Workspace { Path = workspace, Remote = new RemoteReference("connection", "peer", "Host") };

        var install = await reader.InstallAsync("fmt@sample", "user", remote);
        var refresh = await reader.RefreshMarketplaceAsync("sample", remote);
        foreach (var result in new[] { install, refresh })
            Check(result.Status == ClaudePluginStatus.Remote && result.Detail == PluginStrings.DetailRemote,
                "a remote Codex workspace changes nothing: " + result.Status);
        lock (runner.Calls) Check(runner.Calls.Count == 0, "a remote workspace starts no process");
    }

    // ---- The window -------------------------------------------------------

    internal static Task WindowOffersTheMacOSScopeChoiceAndControls()
    {
        var claude = new ClaudePluginBrowser("claude", new Workspace { Path = Verification.Temp() });
        Check(claude.Scope == "local", "Claude starts on the local scope");
        Check(claude.ScopeOptions.Select(o => o.Label).SequenceEqual(
                [PluginStrings.ScopeLocalOption, PluginStrings.ScopeProjectOption, PluginStrings.ScopeUserOption]),
            "the three Claude scopes carry the macOS labels");
        Check(claude.ScopeNote == PluginStrings.ScopeNoteLocal, "the local scope explains itself");
        claude.Scope = "user";
        Check(claude.ScopeNote == PluginStrings.ScopeNoteUser, "the user scope explains itself");

        var codex = new ClaudePluginBrowser(ClaudePluginBrowser.CodexProvider, new Workspace { Path = Verification.Temp() });
        Check(codex.Scope == "user" && codex.ScopeOptions.Count == 1
            && codex.ScopeOptions[0].Label == PluginStrings.ScopeUserOption, "Codex offers the user scope only");
        Check(codex.RefreshUnavailableNote == CodexPluginStrings.RefreshGitOnly,
            "Codex says only registered Git sources can be refreshed");
        Check(claude.RefreshUnavailableNote is null, "Claude carries no Git-only sentence");

        // The window is idle, closable and offers no cancel until a change runs.
        Check(!claude.IsBusy && !claude.IsMutating && claude.CanClose && !claude.CanCancel && claude.ProgressLabel is null,
            "an idle window is closable and shows no progress");
        return Task.CompletedTask;
    }

    internal static async Task WindowInstallShowsProgressCancelResultAndReloads()
    {
        var workspace = Verification.Temp();
        // Three plugin-list calls: SnapshotAsync, the OperateAsync pre-mutation
        // re-read, and the reload after the install completes.
        var listings = new Queue<string>([ClaudeListing(), ClaudeListing(), ClaudeListing("local", workspace)]);
        var gate = new TaskCompletionSource();
        var runner = new FakeRunner((_, _) => Ok());
        runner.Async = async (_, args, token) =>
        {
            if (args is ["plugin", "install", ..]) { await gate.Task.WaitAsync(token); return Ok(InstallOk); }
            return args switch
            {
                ["--version"] => Ok("2.1.271 (Claude Code)"),
                ["plugin", "marketplace", "list", ..] => Ok(ClaudeMarkets),
                _ => Ok(listings.Count > 0 ? listings.Dequeue() : ClaudeListing("local", workspace)),
            };
        };
        var bin = Verification.Temp();
        var reader = new ClaudePluginReader(runner, new Dictionary<string, string> { ["PATH"] = bin },
            isExecutable: path => path == Path.Combine(bin, "claude"), directoryExists: Directory.Exists);

        var browser = new ClaudePluginBrowser("claude", new Workspace { Path = workspace });
        browser.BeginLoad();
        browser.Apply(await reader.SnapshotAsync(new Workspace { Path = workspace }));
        Check(browser.IsReady && browser.CanInstall("fmt@sample"), "the catalog row offers 설치");
        Check(browser.InstallButtonLabel("fmt@sample") == PluginStrings.ButtonInstall, "the button reads 설치");

        var running = browser.InstallAsync(reader, "fmt@sample");
        while (runner.Mutations == 0) await Task.Delay(5);
        Check(browser.IsMutating && browser.ProgressLabel == PluginStrings.ProgressInstalling,
            "the window shows the install progress line");
        Check(!browser.CanClose, "닫기 is disabled while the install runs");
        Check(browser.CanCancel && browser.CancelLabel == PluginStrings.ButtonCancelOperation, "the cancel button is offered");
        Check(browser.InstallButtonLabel("fmt@sample") == PluginStrings.ButtonInstalling, "the pressed row reads 설치 중…");

        gate.SetResult();
        var result = await running;
        Check(result.Status == ClaudePluginStatus.Succeeded, "the install succeeded: " + result.Detail);
        Check(browser.ResultText == PluginStrings.InstallSucceeded, "the result line is the macOS sentence");
        Check(!browser.IsBusy && browser.CanClose && browser.ProgressLabel is null, "the window is idle again");
        // The list was read again, so the row now counts as installed.
        Check(browser.InstalledInSelectedScope("fmt@sample"), "the list was reloaded after the install");
        Check(browser.InstallButtonLabel("fmt@sample") == PluginStrings.TabInstalled && !browser.CanInstall("fmt@sample"),
            "an installed row no longer offers 설치");
    }

    internal static async Task WindowCancelStopsTheOperationAndSaysSo()
    {
        var workspace = Verification.Temp();
        var gate = new TaskCompletionSource();
        var runner = new FakeRunner((_, _) => Ok());
        runner.Async = async (_, args, token) =>
        {
            if (args is ["plugin", "marketplace", "update", ..]) { await gate.Task.WaitAsync(token); return Ok(); }
            return args switch
            {
                ["--version"] => Ok("2.1.271 (Claude Code)"),
                ["plugin", "marketplace", "list", ..] => Ok(ClaudeMarkets),
                _ => Ok(ClaudeListing()),
            };
        };
        var bin = Verification.Temp();
        var reader = new ClaudePluginReader(runner, new Dictionary<string, string> { ["PATH"] = bin },
            isExecutable: path => path == Path.Combine(bin, "claude"), directoryExists: Directory.Exists);

        var browser = new ClaudePluginBrowser("claude", new Workspace { Path = workspace });
        browser.Apply(await reader.SnapshotAsync(new Workspace { Path = workspace }));
        Check(browser.RefreshableMarketplaces is ["sample"], "the registered marketplace can be refreshed");
        Check(browser.CanRefreshMarketplaces, "마켓플레이스 새로고침 is offered");

        var running = browser.RefreshMarketplacesAsync(reader);
        while (runner.Mutations == 0) await Task.Delay(5);
        Check(browser.ProgressLabel == PluginStrings.ProgressRefreshing, "the refresh progress line is shown");
        // RequestCancel() sets IsCancelling synchronously and then calls Cancel(), which
        // may complete the entire async chain inline before returning, so the transient
        // "cancelling" state is not reliably observable here. Verify the final outcome.
        browser.RequestCancel();
        var result = await running;
        gate.TrySetResult();

        Check(result.Status == ClaudePluginStatus.Cancelled, "the refresh was cancelled: " + result.Status);
        Check(browser.ResultText == PluginStrings.OperationCancelledByUser,
            "a cancel the user asked for says the list is read again: " + browser.ResultText);
        Check(browser.IsReady && !browser.IsBusy && browser.CanClose, "the window is idle and the list was read again");
    }

    internal static async Task WindowRefusesAChangeItCannotStart()
    {
        var reader = new ClaudePluginReader(new FakeRunner((_, _) => Ok()),
            new Dictionary<string, string> { ["PATH"] = Verification.Temp() }, isExecutable: _ => false);

        // Nothing loaded yet.
        var cold = new ClaudePluginBrowser("claude", new Workspace { Path = Verification.Temp() });
        var early = await cold.InstallAsync(reader, "fmt@sample");
        Check(early.Status == ClaudePluginStatus.Failed && early.Detail == PluginStrings.LoadListFirst,
            "a window with no list asks for the list first: " + early.Detail);

        // A remote workspace.
        var remote = new ClaudePluginBrowser("claude", new Workspace
        {
            Path = Verification.Temp(),
            Remote = new RemoteReference("connection", "peer", "Host"),
        });
        var blocked = await remote.InstallAsync(reader, "fmt@sample");
        Check(blocked.Status == ClaudePluginStatus.Remote && blocked.Detail == PluginStrings.RemoteNote,
            "a remote workspace is told where to manage plugins: " + blocked.Detail);
        Check(remote.BlockedReason == PluginStrings.RemoteNote, "the window says why it is blocked");

        // A ready list with no registered marketplace.
        var empty = new ClaudePluginBrowser("claude", new Workspace { Path = Verification.Temp() });
        empty.Apply(new ClaudePluginSnapshot { Status = ClaudePluginStatus.Ready, Detail = PluginStrings.DetailNoMarketplaces });
        var nothing = await empty.RefreshMarketplacesAsync(reader);
        Check(nothing.Status == ClaudePluginStatus.Skipped && nothing.Detail == PluginStrings.NoRefreshableMarketplaces,
            "nothing to refresh says so: " + nothing.Detail);
        Check(!empty.CanRefreshMarketplaces, "마켓플레이스 새로고침 is disabled when nothing can be refreshed");

        // An id the catalog never offered never reaches the CLI.
        var ready = new ClaudePluginBrowser("claude", new Workspace { Path = Verification.Temp() });
        ready.Apply(new ClaudePluginSnapshot
        {
            Status = ClaudePluginStatus.Ready,
            Available = [new ClaudeCatalogPlugin { Id = "fmt@sample", Name = "fmt", Marketplace = "sample" }],
            Marketplaces = [new ClaudePluginMarketplace("sample", "github")],
        });
        var stranger = await ready.InstallAsync(reader, "evil@elsewhere");
        Check(stranger.Status == ClaudePluginStatus.Failed && stranger.Detail == PluginStrings.SelectPluginAndScopeAgain,
            "the window refuses an id it never drew: " + stranger.Detail);
    }

    /// The copy of both mutations, against the macOS literals it mirrors.
    internal static Task CopyMatchesMacOS()
    {
        // ClaudePluginView.swift scope picker and buttons.
        Check(PluginStrings.ScopeLocalOption == "로컬 · 이 워크스페이스, 나만", "the local scope label");
        Check(PluginStrings.ScopeProjectOption == "프로젝트 · 팀과 공유", "the project scope label");
        Check(PluginStrings.ScopeUserOption == "사용자 · 모든 프로젝트", "the user scope label");
        Check(PluginStrings.ButtonInstall == "설치", "the install button");
        Check(PluginStrings.ButtonMarketplaceRefresh == "마켓플레이스 새로고침", "the refresh button");
        Check(PluginStrings.ButtonCancelOperation == "작업 취소", "the cancel button");

        // The OS-bound substitution is written down and carries no Mac.
        Check(PluginStrings.ScopeNoteUser.Contains("이 PC의") && !PluginStrings.ScopeNoteUser.Contains("Mac"),
            "the user scope sentence names the Windows PC, not a Mac");

        // Every sentence a mutation can report is a constant of one of the two
        // classes, so no result line is ever composed at the screen.
        var known = typeof(PluginStrings).GetFields().Concat(typeof(CodexPluginStrings).GetFields())
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!).ToHashSet(StringComparer.Ordinal);
        foreach (var sentence in new[]
                 {
                     PluginStrings.OperationBusy, PluginStrings.OperationCancelled, PluginStrings.OperationCancelledByUser,
                     PluginStrings.InstallSucceeded, PluginStrings.InstallSkipped, PluginStrings.InstallFailed,
                     PluginStrings.InstallUnconfirmed, PluginStrings.InstallNotFound, PluginStrings.InstallBadIdOrScope,
                     PluginStrings.InstallCommandRequired, PluginStrings.MarketplaceBadName,
                     PluginStrings.MarketplaceNotRegistered, PluginStrings.MarketplaceRefreshFailed,
                     PluginStrings.MarketplaceRefreshSucceeded, CodexPluginStrings.InstallSucceeded,
                     CodexPluginStrings.InstallSkipped, CodexPluginStrings.InstallNotFound,
                     CodexPluginStrings.InstallVerifyFailed, CodexPluginStrings.OperationFailed,
                     CodexPluginStrings.MarketplaceNotGit, CodexPluginStrings.MarketplaceRefreshUnconfirmed,
                 })
            Check(known.Contains(sentence), "a result sentence must be a Core constant: " + sentence);

        // The two mutations macOS has, and no invented third one.
        var methods = typeof(IPluginReader).GetMethods().Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        Check(methods.SetEquals(["SnapshotAsync", "Shutdown", "InstallAsync", "RefreshMarketplaceAsync"]),
            "the plugin gateway offers exactly the macOS reads and mutations: " + string.Join(", ", methods));
        return Task.CompletedTask;
    }

    // The window the running app opens is the window that installs and
    // refreshes: the controls are drawn from ClaudePluginBrowser, their Click
    // handlers call the Core methods, and the smoke run drives those same
    // controls with a fake reader under the key pluginMarketplace.
    internal static Task WindowIsWiredIntoTheRunningAppAndTheSmokeRun()
    {
        var winui = ClaudePluginVerification.WinUISource();
        var source = File.ReadAllText(Path.Combine(winui, "MainWindow.Plugins.cs"));

        // The controls exist and carry the ids the read-only guard now allows.
        var parts = ClaudePluginVerification.WindowAutomationParts(source);
        foreach (var part in new[] { "scope-picker", "refresh-marketplaces", "cancel-operation", "operation-progress", "operation-result" })
            Check(parts.Contains(part), "the window must draw the control " + part);
        Check(source.Contains("PluginAutomationId(provider, \"install-\" + row.Id)"),
            "each available plugin row must carry its own install button id");

        // Every one of them is wired to Core rather than to a local decision.
        foreach (var call in new[]
                 {
                     "browser.InstallAsync(reader,", "browser.RefreshMarketplacesAsync(reader)", "browser.RequestCancel",
                     "browser.CanInstall(", "browser.InstallButtonLabel(", "browser.CanRefreshMarketplaces",
                     "browser.ScopeOptions", "browser.Scope =", "browser.ScopeNote", "browser.ProgressLabel",
                     "browser.CancelLabel", "browser.CanCancel", "browser.IsMutating", "browser.ResultText",
                     "browser.CanClose",
                 })
            Check(source.Contains(call), "the window must ask Core: " + call);

        // The user reaches them from real events, not from the smoke check.
        Check(source.Contains("installBtn.Click +=") && source.Contains("refreshBtn.Click +=")
            && source.Contains("cancelBtn.Click +=") && source.Contains("scopePicker.SelectionChanged +="),
            "install, refresh, cancel and the scope choice must run on the user's own click");

        // 닫기 stays disabled while an operation runs, as on macOS.
        Check(source.Contains("dialog.Closing +=") && source.Contains("args.Cancel = true"),
            "the window must refuse to close while an operation runs");

        // No Korean of its own: every visible word is a Core constant.
        foreach (var field in typeof(PluginStrings).GetFields(BindingFlags.Public | BindingFlags.Static)
                     .Concat(typeof(CodexPluginStrings).GetFields(BindingFlags.Public | BindingFlags.Static))
                     .Where(f => f.IsLiteral && f.FieldType == typeof(string)))
            Check(!source.Contains("\"" + (string)field.GetRawConstantValue()! + "\""),
                "a plugin sentence is typed into WinUI instead of read from Core: " + field.Name);

        // The smoke run records pluginMarketplace, drives the same window and
        // never starts a real CLI: a fake reader replaces both readers.
        Check(PluginMarketplaceSmokeOutcome.ResultKey == "pluginMarketplace", "the smoke key is pluginMarketplace");
        var smoke = File.ReadAllText(Path.Combine(winui, "MainWindow.Smoke.cs"));
        Check(smoke.Contains("PluginMarketplaceSmokeOutcome.ResultKey") && smoke.Contains("RunPluginMarketplaceSmoke()"),
            "the smoke run must record its result under pluginMarketplace");
        Check(source.Contains("internal async Task<PluginMarketplaceSmokeOutcome> RunPluginMarketplaceSmoke()"),
            "the marketplace smoke must live beside the window it drives");
        Check(source.Contains("class FakeMarketplaceReader") && source.Contains("smokeReaderFactory = _ => new FakeMarketplaceReader("),
            "the smoke run must install through a fake reader so no claude or codex process starts");
        Check(!source.Contains("new ClaudePluginReader(runner)\n            : new CodexPluginReader"),
            "the smoke run must not fall back to the real readers");
        Check(source.Contains("surface.Install(") && source.Contains("surface.Refresh()") && source.Contains("surface.RequestCancel()"),
            "the smoke run must drive the real install, refresh and cancel actions");

        // Everything it changed is put back.
        foreach (var restore in new[]
                 {
                     "smokePluginRead = beforeRead", "smokeCodexPluginRead = beforeCodexRead",
                     "smokePluginDialog = beforeDialog", "smokeCodexPluginDialog = beforeCodexDialog",
                     "smokeReaderFactory = beforeReaderFactory",
                 })
            Check(source.Contains(restore), "the smoke run must put back what it changed: " + restore);
        return Task.CompletedTask;
    }
}
