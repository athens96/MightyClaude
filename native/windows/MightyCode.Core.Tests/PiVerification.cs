using System.Collections.Concurrent;
using System.Text.Json;
using MightyCode.Core;

/// <summary>
/// pi (<c>pi --mode rpc</c>) as the fourth agent: the recorded RPC output in native/contracts/fixtures/pi-rpc-*.jsonl (the Mac replays the
/// same files, native/macos/Tests/MightyCoreTests/PiTests.swift), the argv a permission becomes, the model list, the sign-in reading,
/// the setup item and the session list, and a real run against a pi that answers from a fixture.
/// </summary>
internal static class PiVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private const string SessionId = "0197c1d2-7f3a-7c4e-9b1a-5d2e8f6a4b30";

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "native", "contracts", "fixtures", "setup-catalog.json"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("the repository root was not found");
    }
    private static string FixturePath(string name) => Path.Combine(Root(), "native", "contracts", "fixtures", $"pi-rpc-{name}.jsonl");
    private static string[] Fixture(string name) => File.ReadAllLines(FixturePath(name)).Where(l => l.Length > 0).ToArray();
    private static JsonElement Json(string value) { using var document = JsonDocument.Parse(value); return document.RootElement.Clone(); }
    private static async Task English(Func<Task> body)
    {
        var saved = Locale.LanguagePreference; Locale.LanguagePreference = "en"; Locale.ResetCache();
        try { await body(); } finally { Locale.LanguagePreference = saved; Locale.ResetCache(); }
    }

    private sealed class Replay
    {
        internal readonly List<(string Kind, string Text)> Logs = [];
        internal readonly List<AgentActivity> Activities = [];
        internal readonly List<string> Resumes = [];
        internal readonly List<SessionUsage> Usage = [];
        internal readonly List<ExecutionGraphNode> Graph = [];
        internal readonly List<string> Replies = [];
        internal readonly List<bool> TurnOpenAfterEach = [];
        internal bool Failed;
        internal MightyGraphRun? Run;
    }
    private static Replay Play(string name)
    {
        var result = new Replay();
        var tracker = new ExecutionGraphTracker("run-pi", "go", "pi", "default", result.Graph.Add);
        var parser = new OutputParser("pi", (kind, text) => result.Logs.Add((kind, text)), result.Resumes.Add, value => { result.Activities.Add(value); tracker.Activity(value); }, result.Usage.Add, "run-pi", piReply: result.Replies.Add);
        foreach (var line in Fixture(name)) { tracker.Consume(Json(line)); parser.Parse(line); result.TurnOpenAfterEach.Add(parser.PiTurnOpen); }
        parser.Flush(); parser.FinishActivities(false); tracker.Finish("completed");
        result.Failed = parser.Failed; result.Run = tracker.BuildRun();
        return result;
    }

    // ── Events to the log and the graph ──────────────────────────────────

    internal static Task AnAnswerIsLoggedOnceWhateverTheDeltasAndTheEndOfTheMessage() => English(() =>
    {
        var run = Play("answer");
        Check(run.Logs.Select(l => l.Kind).SequenceEqual(["assistant"]) && run.Logs[0].Text == "Hello! How can I help with this project?", "one reply: " + string.Join("|", run.Logs));
        Check(!run.Logs.Any(l => l.Text.Contains("greets")), "the thinking text is not copied");
        Check(run.Resumes.SequenceEqual([SessionId]) && !run.Failed, "get_state names the session");
        Check(run.TurnOpenAfterEach.Contains(true) && !run.TurnOpenAfterEach[^1], "open from agent_start to agent_end");
        Check(run.Run is { Status: "completed", FinalOutput: "Hello! How can I help with this project?", Usage: var usage } && usage == new GraphTokenUsage(1203, 18), "the request's block");
        return Task.CompletedTask;
    });

    internal static Task ToolsAreRowsThatSettleByTheirCallIdAndTheSecondMessageIsLoggedToo() => English(() =>
    {
        var run = Play("tools");
        Check(run.Logs.Where(l => l.Kind == "assistant").Select(l => l.Text).SequenceEqual(["Let me look at the entry point and run the tests.", "The edit did not apply, but the read shows `main` returns 1 and one test fails."]), "both replies");
        var read = run.Activities.Where(a => a.ToolName == "read").ToList();
        Check(read.Select(a => a.State).SequenceEqual(["running", "completed"]) && read[^1].Kind == "read" && read[^1].Summary == "src/main.ts" && read[^1].Output!.Contains("export function main()"), "read");
        var bash = run.Activities.Where(a => a.ToolName == "bash").ToList();
        Check(bash.Select(a => a.State).SequenceEqual(["running", "running", "completed"]) && bash[0].Kind == "command" && bash[0].Summary == "npm test" && bash[^1].Output!.Contains("1 failed | 3 passed"), "bash");
        var edit = run.Activities.Where(a => a.ToolName == "edit").ToList();
        Check(edit.Select(a => a.State).SequenceEqual(["running", "error"]) && edit[^1].Kind == "edit" && edit[^1].Output == "Could not find the exact text in src/main.ts.", "edit");
        Check(!run.Failed, "no failure");
        return Task.CompletedTask;
    });

    internal static Task UsageAddsEachMessageWithCachedInputInsideInput() => English(() =>
    {
        var run = Play("tools");
        var usage = run.Usage[^1];
        Check(usage.Provider == "pi" && usage.TokenScope == "run" && usage.Source == "pi.rpc", "identity");
        Check(usage.InputTokens == (2410 + 1800) + (3120 + 1800) + (3380 + 1800) && usage.OutputTokens == 96 + 64 + 41, "input and output");
        Check(usage.CacheReadTokens == 3600 && usage.CacheWriteTokens == 1800 && usage.TotalTokens == usage.InputTokens + usage.OutputTokens, "cache and total");
        Check(usage.ProviderSessionId == SessionId && usage.Model == "claude-sonnet-4-5" && usage.ContextWindowTokens == 200_000 && usage.ContextUsedTokens == 3380 + 1800, "session, model and context");
        Check(usage.CostUSD > 0 && usage.CostScope == "run" && SessionUsageSupport.Normalize(usage) is not null, "cost");
        Check(run.Run!.Usage!.CacheReadTokens == 3600 && run.Run.Usage.OutputTokens == 201 && run.Run.FinalOutput == "The edit did not apply, but the read shows `main` returns 1 and one test fails.", "the block adds each message and the last text is the answer");
        return Task.CompletedTask;
    });

    internal static Task ARetriedErrorIsNotTheRunsFailureAndKeepsTheTurnOpen() => English(() =>
    {
        var run = Play("retry");
        Check(!run.Failed && !run.Logs.Any(l => l.Kind == "error"), "a retried error is no failure");
        var overloaded = "529 {\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\",\"message\":\"Overloaded\"}}";
        Check(run.Logs.Any(l => l.Kind == "system" && l.Text == Locale.Get("run.notice.piRetry", new Dictionary<string, string> { ["attempt"] = "1", ["max"] = "3", ["error"] = ActivitySupport.Clean(overloaded, 512, true) })), "the retry notice");
        Check(run.Logs.Where(l => l.Kind == "assistant").Select(l => l.Text).SequenceEqual(["Done: the summary is below."]), "the answer");
        var lines = Fixture("retry");
        var ends = Enumerable.Range(0, lines.Length).Where(i => lines[i].Contains("\"type\":\"agent_end\"")).ToList();
        Check(ends.Count == 2, "two agent_end");
        Check(!run.TurnOpenAfterEach[ends[0]] && run.TurnOpenAfterEach[ends[0] + 1], "the retry announced right after reopens the turn");
        Check(!run.TurnOpenAfterEach[ends[1]] && !run.TurnOpenAfterEach[ends[1] + 1], "the last end closes it");
        Check(run.Logs.Any(l => l.Kind == "system" && l.Text.StartsWith(ContextCompaction.Title, StringComparison.Ordinal) && l.Text.Contains("88,320")), "the compaction is a line");
        var start = Array.FindIndex(lines, l => l.Contains("compaction_start"));
        Check(run.TurnOpenAfterEach[start] && !run.TurnOpenAfterEach[^1], "the compaction holds the turn");
        return Task.CompletedTask;
    });

    internal static Task AnErrorNoRetryAnsweredFailsTheRunAndADialogIsCancelled() => English(() =>
    {
        var run = Play("error");
        var errors = run.Logs.Where(l => l.Kind == "error").Select(l => l.Text).ToList();
        Check(run.Failed && errors.Count == 1 && errors[0].Contains("authentication_error"), "the unanswered error fails the run: " + string.Join("|", errors));
        Check(run.Replies.Count == 1, "the confirm is answered, the notify needs none");
        var reply = Json(run.Replies[0]);
        Check(reply.Text("type") == "extension_ui_response" && reply.Text("id") == "d7a0f1e2-0000-4000-8000-000000000001" && reply.GetProperty("cancelled").GetBoolean() && run.Replies[0].EndsWith('\n'), "a cancel, one line");
        Check(run.Logs.Any(l => l.Kind == "system" && l.Text == Locale.Get("run.notice.piDialogDismissed", new Dictionary<string, string> { ["title"] = "Allow dangerous command?" })), "the notice");
        return Task.CompletedTask;
    });

    internal static Task ARejectedCommandFailsTheRun() => English(() =>
    {
        var run = Play("rejected");
        Check(run.Failed && run.Logs.Any(l => l.Kind == "error" && l.Text.Contains("already processing")), "rejected");
        return Task.CompletedTask;
    });

    // ── Permissions and settings to argv ─────────────────────────────────

    private static StartRunRequest Request(string mode = "manual", string model = "default", string effort = "default", string? resume = null) =>
        new("pane", "w", "claude", "hi", [], model, "pi", new RunSettings(Effort: effort, PermissionMode: mode), resume);

    internal static Task PermissionsBecomeTheToolAllowlistAndThinkingLevelsAreTheEffort()
    {
        Check(ProviderCatalog.Arguments(Request("manual"), "").SequenceEqual(["--mode", "rpc", "--tools", "read,grep,find,ls"]), "read-only");
        Check(ProviderCatalog.Arguments(Request("fullAccess"), "").SequenceEqual(["--mode", "rpc"]), "full access leaves every tool on");
        Check(ProviderCatalog.Arguments(Request("manual", "anthropic/claude-sonnet-4-5", "high", SessionId), "").SequenceEqual(["--mode", "rpc", "--model", "anthropic/claude-sonnet-4-5", "--thinking", "high", "--tools", "read,grep,find,ls", "--session", SessionId]), "model, thinking, tools, session");
        Check(ProviderCatalog.Arguments(Request("fullAccess", effort: "off"), "").SequenceEqual(["--mode", "rpc", "--thinking", "off"]), "off is a level");
        Check(ProviderCatalog.PermissionModes("pi").SequenceEqual(["manual", "fullAccess"]), "the two modes pi has");
        Check(ProviderCatalog.NormalizeSettings("pi", new RunSettings(Effort: "minimal", PermissionMode: "acceptEdits")).PermissionMode == "manual", "another mode is not pi's");
        Check(ProviderCatalog.NormalizeSettings("pi", new RunSettings(Effort: "minimal", PermissionMode: "fullAccess")).Effort == "minimal" && ProviderCatalog.NormalizeSettings("claude", new RunSettings(Effort: "minimal")).Effort == "default", "minimal is pi's level only");
        var catalog = ProviderCatalog.PiCatalog(ModelList);
        Check(ProviderCatalog.Efforts("pi", "default", ProviderCatalog.Fallback("pi")).SequenceEqual(["off", "minimal", "low", "medium", "high", "xhigh"]), "pi's levels");
        Check(ProviderCatalog.Efforts("pi", "openai/gpt-4o", catalog).Length == 0 && ProviderCatalog.Efforts("pi", "anthropic/claude-sonnet-4-5", catalog).Length == 6, "a model without thinking has no levels");
        Request("manual", effort: "minimal").Validate();
        var rejected = false;
        try { new StartRunRequest("p", "w", "claude", "x", [], Provider: "pi", Settings: new RunSettings(Effort: "max")).Validate(); } catch (ArgumentException) { rejected = true; }
        Check(rejected, "max is not a pi level");
        return Task.CompletedTask;
    }

    internal static Task TheRpcStdinIsGetStateThenThePromptAndASteerIsOneLine()
    {
        var lines = ProviderInput.PiStartup("fix \"it\" / now").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Check(lines.Length == 2 && lines[0] == "{\"type\":\"get_state\"}", "get_state first");
        var prompt = Json(lines[1]);
        Check(prompt.Text("type") == "prompt" && prompt.Text("message") == "fix \"it\" / now" && !prompt.TryGetProperty("images", out _), "the prompt");
        var withImage = Json(ProviderInput.PiStartup("look", [("image/png", "AAAA")]).Split('\n')[1]).GetProperty("images")[0];
        Check(withImage.Text("type") == "image" && withImage.Text("data") == "AAAA" && withImage.Text("mimeType") == "image/png", "an image rides in the prompt");
        Check(ProviderInput.PiSteer("stop and do this") == "{\"type\":\"steer\",\"message\":\"stop and do this\"}\n", "a steer");
        return Task.CompletedTask;
    }

    internal static async Task AttachmentsRideInThePromptOrAreNamedByPath()
    {
        var files = new[] { AttachmentSupport.Make("image.png", AttachmentVerification.Png), AttachmentSupport.Make("source.cs", "class A {}"u8.ToArray()) };
        await using var staged = await StagedAttachments.CreateAsync(files);
        var request = new StartRunRequest("pane", "w", "claude", "see", [], Provider: "pi", Attachments: files, ResumeId: "resume-id");
        var arguments = staged.ArgumentsFor(request, "");
        Check(arguments.Take(2).SequenceEqual(["--mode", "rpc"]) && !arguments.Contains("--add-dir") && arguments.TakeLast(2).SequenceEqual(["--session", "resume-id"]), "no added folder: " + string.Join(" ", arguments));
        var lines = staged.InputFor(request).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var prompt = Json(lines[1]);
        var images = prompt.GetProperty("images");
        Check(lines.Length == 2 && images.GetArrayLength() == 1 && images[0].Text("mimeType") == "image/png" && images[0].Text("data") == files[0].DataBase64, "the picture is in the prompt");
        var message = prompt.Text("message")!;
        Check(message.Contains(staged.Files[1].Path) && !message.Contains(staged.Files[0].Path), "the other file is named by the path of its copy");
    }

    // ── Model list ───────────────────────────────────────────────────────

    private const string ModelList = """
        provider   model                      context  max-out  thinking  images
        anthropic  claude-sonnet-4-5          200K     64K      yes       yes
        anthropic  claude-opus-4-1            200K     32K      yes       yes
        openai     gpt-4o                     128K     16.4K    no        yes
        openrouter anthropic/claude-3.5-haiku 200K     8.2K     no        yes

        """;

    internal static Task TheModelListBecomesProviderSlashModelRowsAndWithoutASignInIsJustPisDefault() => English(() =>
    {
        var catalog = ProviderCatalog.PiCatalog(ModelList);
        Check(catalog.Source == "cli" && catalog.Models.Select(m => m.Value).SequenceEqual(["default", "anthropic/claude-sonnet-4-5", "anthropic/claude-opus-4-1", "openai/gpt-4o", "openrouter/anthropic/claude-3.5-haiku"]), "rows");
        Check(catalog.Models[0].DisplayName == Locale.Get("provider.fallback.defaultLabel", new Dictionary<string, string> { ["name"] = "pi" }), "the default row");
        Check(catalog.Models[1].SupportsEffort == true && catalog.Models[3].SupportsEffort == false, "thinking yes or no");
        Check(catalog.Models[3].Description == Locale.Get("provider.catalog.piModelRow", new Dictionary<string, string> { ["context"] = "128K", ["output"] = "16.4K" }), "context and output");
        Check(catalog.Models.All(m => Wire.Model(m.Value)), "every value is a model name");
        var none = "No models available. Use /login to log into a provider via OAuth or API key. See:\n  /x/docs/providers.md\n";
        Check(ProviderCatalog.PiCatalog(none) == ProviderCatalog.Fallback("pi") || ProviderCatalog.PiCatalog(none).Models.Select(m => m.Value).SequenceEqual(["default"]), "no sign-in keeps the fallback");
        Check(ProviderCatalog.PiCatalog("").Models.Select(m => m.Value).SequenceEqual(["default"]) && ProviderCatalog.Fallback("pi").Models.Select(m => m.Value).SequenceEqual(["default"]), "the fallback");
        Check(ProviderMark.Label("pi") == "pi" && ProviderCatalog.Executable("pi") == "pi" && ProviderCatalog.Name("pi") == "pi" && Wire.Providers.SequenceEqual(["claude", "codex", "gemini", "pi", "opencode", "hermes"]), "name and place");
        return Task.CompletedTask;
    });

    // ── Sign-in ──────────────────────────────────────────────────────────

    private const string NoModels = "No models available. Use /login to log into a provider via OAuth or API key. See:\n";
    private static readonly Dictionary<string, string> NoEnvironment = [];

    internal static Task NoModelsReadsSignedOutAndCredentialsAndEnvironmentKeysSignPiIn()
    {
        var out1 = PiAccounts.Status(0, NoModels, null, NoEnvironment);
        Check(out1.LoggedIn == false && out1.Provider == "pi" && !out1.CanSignOut, "no models: signed out");
        Check(PiAccounts.Status(0, NoModels, """{"anthropic":{"type":"api_key","key":"x"}}""", new Dictionary<string, string> { ["OPENAI_API_KEY"] = "k" }).LoggedIn == false, "whatever else is around");
        var subscription = PiAccounts.Status(0, ModelList, """{"anthropic":{"type":"oauth","access":"a","refresh":"r","expires":1}}""", NoEnvironment);
        Check(subscription.LoggedIn == true && subscription.MethodId == CliAccountMethod.PiSubscription && subscription.Account == "anthropic" && !subscription.CanSignOut, "a subscription");
        var key = PiAccounts.Status(0, ModelList, """{"openai":{"type":"api_key","key":"sk"},"google":{"type":"api_key","key":"g"}}""", NoEnvironment);
        Check(key.LoggedIn == true && key.MethodId == CliAccountMethod.PiApiKey && key.Account == "google, openai", "API keys");
        var environment = PiAccounts.Status(0, ModelList, "{}", new Dictionary<string, string> { ["GEMINI_API_KEY"] = "k" });
        Check(environment.LoggedIn == true && environment.MethodId == CliAccountMethod.PiApiKey && environment.Account is null, "the environment's key");
        Check(PiAccounts.Status(0, ModelList, null, NoEnvironment).MethodId == CliAccountMethod.PiModels, "models.json alone");
        Check(PiAccounts.Status(1, "boom", null, NoEnvironment).LoggedIn is null && PiAccounts.Status(1, "", null, NoEnvironment, timedOut: true).LoggedIn is null, "a failed or timed-out call is unknown");
        Check(CliAccountSupport.LoginArguments("pi")!.SequenceEqual(["pi"]) && CliAccountSupport.StatusArguments("pi")!.SequenceEqual(["pi", "--list-models"]) && CliAccountSupport.LogoutArguments("pi") is null, "argv");
        return Task.CompletedTask;
    }

    internal static async Task TheAuthFileAndTheCoordinatorAreReadFromTheHomeAndNeverShown()
    {
        var home = Verification.Temp();
        try
        {
            var auth = PiAgent.AuthFile(home, NoEnvironment);
            Check(PiAgent.Credentials(CliAccountSupport.BoundedFileText(auth)).Providers.Count == 0, "no file: no credential");
            Directory.CreateDirectory(Path.GetDirectoryName(auth)!);
            File.WriteAllText(auth, """{"anthropic":{"type":"api_key","key":"sk-ant-SECRET"},"note":"text"}""");
            var credentials = PiAgent.Credentials(CliAccountSupport.BoundedFileText(auth));
            Check(credentials.Providers.SequenceEqual(["anthropic"]) && !credentials.Subscription, "providers only");
            var status = PiAccounts.Status(0, ModelList, CliAccountSupport.BoundedFileText(auth), NoEnvironment);
            Check(!(status.Summary + status.Detail + status.Account).Contains("SECRET"), "a credential is never shown");
            var elsewhere = OperatingSystem.IsWindows() ? @"C:\elsewhere\agent" : "/elsewhere/agent";
            Check(PiAgent.AuthFile(home, new Dictionary<string, string> { ["PI_CODING_AGENT_DIR"] = elsewhere }) == Path.Combine(elsewhere, "auth.json"), "PI_CODING_AGENT_DIR");
            Check(PiAgent.EnvironmentKeys(new Dictionary<string, string> { ["ANTHROPIC_API_KEY"] = "", ["XAI_API_KEY"] = "x", ["PATH"] = "/bin" }).SequenceEqual(["XAI_API_KEY"]), "only a set key counts");

            // The coordinator asks pi for its models and reads the home's auth file.
            var bin = Path.Combine(home, "bin"); Directory.CreateDirectory(bin);
            File.WriteAllText(Path.Combine(bin, OperatingSystem.IsWindows() ? "pi.exe" : "pi"), "");
            File.WriteAllText(auth, """{"github-copilot":{"type":"oauth","refresh":"r","access":"a","expires":1}}""");
            var empty = false;
            var runner = new FakeRunner(call => empty ? new(0, "No models available. Use /login\n", "", false) : new(0, ModelList, "", false));
            var coordinator = new CliAccountsCoordinator(runner, new Dictionary<string, string> { ["PATH"] = bin, ["NO_COLOR"] = "1" }, home);
            await coordinator.RefreshAsync(["pi"]);
            Check(coordinator.Statuses["pi"] is { Installed: true, LoggedIn: true, MethodId: CliAccountMethod.PiSubscription, Account: "github-copilot" } && runner.Calls.Single().SequenceEqual(["--list-models"]), "signed in by a model list and a subscription");
            empty = true; await coordinator.RefreshAsync(["pi"]);
            Check(coordinator.Statuses["pi"].LoggedIn == false, "no models: signed out");
            var missing = new CliAccountsCoordinator(runner, new Dictionary<string, string> { ["PATH"] = Path.Combine(home, "nowhere") }, home);
            await missing.RefreshAsync(["pi"]);
            Check(!missing.Statuses["pi"].Installed, "not installed");
        }
        finally { Directory.Delete(home, true); }
    }
    private sealed class FakeRunner(Func<(string Executable, IReadOnlyList<string> Arguments), CliRunResult> respond) : ICliRunner
    {
        internal readonly List<IReadOnlyList<string>> Calls = [];
        public Task<CliRunResult> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellation = default, IReadOnlyDictionary<string, string>? environment = null, string? workingDirectory = null)
        { Calls.Add(arguments); return Task.FromResult(respond((executable, arguments))); }
    }

    // ── Setup, update ────────────────────────────────────────────────────

    internal static Task TheSetupItemInstallsPiWithNpmAfterNodeAndThenSignsIn()
    {
        var catalog = SetupCatalogDecoder.Decode(File.ReadAllText(Path.Combine(Root(), "native", "contracts", "fixtures", "setup-catalog.json")));
        var item = catalog.Item("pi")!;
        Check(item.Group == SetupGroup.Agent && !item.Required && item.Provider == "pi" && item.LabelKey == "setup.item.pi.title" && item.DescriptionKey == "setup.item.pi.detail", "the item");
        foreach (var platform in new[] { SetupPlatform.MacOS, SetupPlatform.Windows })
        {
            var entry = item.Entry(platform)!;
            Check(entry.Probe is SetupProbe.Executable { Name: "pi" } && entry.Install is SetupRecipe.NpmGlobal { Package: "@mariozechner/pi-coding-agent" } && entry.Prerequisites.SequenceEqual(["node"]) && entry.Source == new Uri("https://github.com/badlogic/pi-mono"), platform + " entry");
        }
        Check(SetupPlan.Order(new HashSet<string> { "pi" }, catalog, SetupPlatform.Windows, _ => false).SequenceEqual(["node", "pi"]), "Node.js first, then pi");
        Check(SetupPlan.Order(new HashSet<string> { "pi" }, catalog, SetupPlatform.Windows, _ => true).SequenceEqual(["pi"]), "with Node.js there, pi alone");
        Check(ComponentSection.InstallCommand("pi") == "npm install -g @mariozechner/pi-coding-agent", "the install command");
        return Task.CompletedTask;
    }

    internal static async Task PiUpdatesItselfWithUpdateSelfWhereverItIsInstalled()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Verification.Temp();
        try
        {
            var bin = Path.Combine(root, "bin"); Directory.CreateDirectory(bin);
            var pi = Path.Combine(bin, "pi");
            await File.WriteAllTextAsync(pi, "#!/bin/sh\nfolder=\"$(/usr/bin/dirname \"$0\")\"\ncase \"$1\" in\n  --version) if [ -e \"$folder/updated\" ]; then printf '2.0.0\\n'; else printf '1.0.0\\n'; fi; exit 0;;\n  update) printf '%s\\n' \"$@\" > \"$folder/arguments\"; : > \"$folder/updated\"; exit 0;;\nesac\nexit 2\n");
            File.SetUnixFileMode(pi, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var updates = new CliUpdateService(new CliRunner(), new Dictionary<string, string> { ["PATH"] = bin + ":/usr/bin:/bin" }, root);
            var installation = await updates.InspectAsync("pi");
            Check(installation.CanUpdate && installation.Method == "pi" && installation.Version == "1.0.0", $"pi updates itself: {installation.Method} {installation.Detail}");
            var result = await updates.UpdateAsync("pi");
            Check(result.Status == "updated" && result.AfterVersion == "2.0.0", "updated: " + result.Status + " " + result.Detail);
            Check(File.ReadAllText(Path.Combine(bin, "arguments")) == "update\nself\n", "pi update self");
            Check(CliUpdateService.ProviderLabel("pi") == "pi", "the row's name");
        }
        finally { Directory.Delete(root, true); }
    }

    // ── Session list ─────────────────────────────────────────────────────

    internal static Task SessionsOfTheWorkspaceFolderAreListedWithTheirFirstRequest()
    {
        var home = Verification.Temp();
        try
        {
            var workspace = Path.Combine(home, "project"); Directory.CreateDirectory(workspace);
            var real = ResumableSessions.WorkspacePaths(workspace).Last();
            var folder = Path.Combine(home, ".pi", "agent", "sessions", PiAgent.SessionFolder(real)); Directory.CreateDirectory(folder);
            const string id = "0197c1d2-7f3a-7c4e-9b1a-5d2e8f6a4b30", other = "0197c1d2-aaaa-7c4e-9b1a-5d2e8f6a4b31", empty = "0197c1d2-bbbb-7c4e-9b1a-5d2e8f6a4b32";
            var cwd = JsonSerializer.Serialize(real);
            string[] lines =
            [
                $$"""{"type":"session","version":3,"id":"{{id}}","timestamp":"2026-11-10T09:30:00.000Z","cwd":{{cwd}}}""",
                """{"type":"model_change","id":"a1b2c3d4","parentId":null,"timestamp":"2026-11-10T09:30:00.100Z","provider":"anthropic","modelId":"claude-sonnet-4-5"}""",
                """{"type":"message","id":"b2c3d4e5","parentId":"a1b2c3d4","timestamp":"2026-11-10T09:30:01.000Z","message":{"role":"user","content":[{"type":"text","text":"  Fix the\nfailing test  "}],"timestamp":1762767001000}}""",
                """{"type":"message","id":"c3d4e5f6","parentId":"b2c3d4e5","timestamp":"2026-11-10T09:30:05.000Z","message":{"role":"assistant","content":[{"type":"text","text":"Done."}],"provider":"anthropic","model":"claude-sonnet-4-5","stopReason":"stop","timestamp":1762767005000}}""",
                """{"type":"message","id":"d4e5f6a7","parentId":"c3d4e5f6","timestamp":"2026-11-10T09:31:00.000Z","message":{"role":"user","content":"And the lint?","timestamp":1762767060000}}""",
                """{"type":"message","id":"e5f6a7b8","parentId":"d4e5f6a7","timestamp":"2026-11-10T09:31:09.000Z","message":{"role":"assistant","content":[{"type":"text","text":"Clean."}],"provider":"anthropic","model":"claude-opus-4-1","stopReason":"stop","timestamp":1762767069000}}""",
            ];
            File.WriteAllText(Path.Combine(folder, $"2026-11-10T09-30-00-000Z_{id}.jsonl"), string.Join("\n", lines) + "\n");
            // Another folder's header, and a session nobody wrote a request in, are not listed.
            File.WriteAllText(Path.Combine(folder, $"2026-11-09T09-30-00-000Z_{other}.jsonl"), lines[0].Replace(id, other).Replace(cwd, "\"/somewhere/else\"") + "\n" + lines[2] + "\n");
            File.WriteAllText(Path.Combine(folder, $"2026-11-08T09-30-00-000Z_{empty}.jsonl"), lines[0].Replace(id, empty) + "\n");
            File.WriteAllText(Path.Combine(folder, "notes.txt"), "not a session");

            var query = new ResumableSessionQuery(workspace) { Home = home, Now = new DateTimeOffset(2026, 11, 11, 0, 0, 0, TimeSpan.Zero) };
            var listing = ResumableSessions.Listing(query, "pi");
            Check(listing.Items.Count == 1 && listing.Hidden == 0, "one session: " + listing.Items.Count);
            var item = listing.Items[0];
            Check(item.Provider == "pi" && item.SessionID == id && item.Title == "Fix the failing test" && item.Requests == 2 && item.Model == "claude-opus-4-1", "the row");
            Check(ResumableSessions.Listing(query).Items.Select(i => i.Id).SequenceEqual(["pi:" + id]) && ResumableSessions.Providers.Contains("pi"), "the whole list holds it");
            Check(ResumableSessions.Listing(query with { Excluding = new HashSet<string> { id.ToUpperInvariant().ToLowerInvariant() } }, "pi").Items.Count == 0, "a session an open pane continues is left out");
            Check(Path.GetFileName(PiAgent.SessionFile(id, workspace, home, NoEnvironment)) == $"2026-11-10T09-30-00-000Z_{id}.jsonl" && PiAgent.SessionFile("0197c1d2-cccc-7c4e-9b1a-5d2e8f6a4b33", workspace, home, NoEnvironment) is null, "the file a pane resumes is found by the id");
            Check(PiAgent.SessionFolder("/Users/dev/my project") == "--Users-dev-my project--" && PiAgent.SessionFolder(@"C:\Users\dev") == "--C--Users-dev--", "the folder name");
            Check(ResumableSessions.OffersResume("claude", "pi"), "창 추가 offers to continue");
        }
        finally { Directory.Delete(home, true); }
        return Task.CompletedTask;
    }

    // ── A real run ───────────────────────────────────────────────────────

    /// <summary>A pi that answers on stdin as <c>pi --mode rpc</c> does, from a fixture, and exits when stdin ends.</summary>
    internal static async Task FakeAsync(string record, string[] args)
    {
        var fixture = File.ReadAllLines(args[Array.IndexOf(args, "--pi-fixture") + 1]).Where(l => l.Length > 0).ToArray();
        while (await Console.In.ReadLineAsync() is { } line)
        {
            await File.AppendAllTextAsync(record + ".stdin", line + "\n");
            if (line.Contains("\"get_state\"")) Console.WriteLine(fixture[0]);
            else if (line.Contains("\"prompt\""))
            {
                foreach (var output in fixture.Skip(1).Take(3)) Console.WriteLine(output);
                if (args.Contains("--pi-hang")) await Task.Delay(60_000);
                if (args.Contains("--pi-steer") && await Console.In.ReadLineAsync() is { } steer) await File.AppendAllTextAsync(record + ".stdin", steer + "\n");
                foreach (var output in fixture.Skip(4)) Console.WriteLine(output);
            }
        }
    }
    private static ProviderCatalog FakePi(string record, string fixture, params string[] extra) =>
        new((_, _) => Task.FromResult<CliCommand?>(Verification.Self(["--fake-cli", "pi", record, "--pi-fixture", FixturePath(fixture), .. extra])));
    private static bool Ended(RunEvent value) => value.Type == "status" && value.Status is "completed" or "error" or "stopped";
    private static string[] Lines(string path) => File.ReadAllLines(path).Where(l => l.Length > 0).ToArray();

    internal static async Task ARunWritesTheStartupLinesKeepsStdinOpenUntilTheTurnEndsThenCompletes()
    {
        var directory = Verification.Temp();
        try
        {
            var record = Path.Combine(directory, "pi"); var events = new ConcurrentQueue<RunEvent>(); var workspace = new Workspace { Path = directory };
            await using var catalog = FakePi(record, "answer");
            await using (var manager = new RunManager(_ => Task.FromResult(workspace), catalog, "", events.Enqueue))
            {
                await manager.StartAsync(new("pane", workspace.Id, "claude", "hi there", [], Provider: "pi"));
                await Verification.Until(() => events.Any(Ended), 30000);
            }
            var all = events.ToList();
            Check(all.Last(e => e.Type == "status").Status == "completed", "completed: " + string.Join(",", all.Select(e => e.Type + ":" + e.Status + ":" + e.Entry?.Text)));
            Check(all.Any(e => e.Type == "log" && e.Entry is { Kind: "assistant", Text: "Hello! How can I help with this project?" }), "the reply");
            Check(all.Any(e => e.Type == "resume" && e.ResumeId == SessionId), "the session id");
            Check(all.Any(e => e.Type == "usage" && e.Usage is { Provider: "pi", OutputTokens: 18 }), "the usage");
            Check(all.Any(e => e.Type == "graph_run" && e.GraphRun is { Provider: "pi" }), "the graph");
            var argv = JsonSerializer.Deserialize<string[]>(File.ReadAllText(record + ".args"))!;
            Check(argv.SkipWhile(a => a != "--mode").SequenceEqual(["--mode", "rpc", "--tools", "read,grep,find,ls"]), "argv: " + string.Join(" ", argv));
            var stdin = Lines(record + ".stdin");
            Check(stdin.Length == 2 && stdin[0] == "{\"type\":\"get_state\"}" && stdin[1].Contains("\"message\":\"hi there\""), "stdin: get_state then the prompt");
        }
        finally { try { Directory.Delete(directory, true); } catch (IOException) { } }
    }

    internal static async Task ASteerIsWrittenWhileTheTurnIsOpenAndRefusedBeforeAndAfter()
    {
        var directory = Verification.Temp();
        try
        {
            var record = Path.Combine(directory, "pi"); var events = new ConcurrentQueue<RunEvent>(); var workspace = new Workspace { Path = directory };
            await using var catalog = FakePi(record, "answer", "--pi-steer");
            await using (var manager = new RunManager(_ => Task.FromResult(workspace), catalog, "", events.Enqueue))
            {
                Check(!await manager.TrySteerAsync("pane", "nobody home"), "nothing running");
                await manager.StartAsync(new("pane", workspace.Id, "claude", "hi", [], Provider: "pi"));
                var steered = false; var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
                while (!steered && DateTimeOffset.UtcNow < deadline) { steered = await manager.TrySteerAsync("pane", "also check the lint"); if (!steered) await Task.Delay(20); }
                Check(steered, "accepted once the turn is open");
                await Verification.Until(() => events.Any(Ended), 30000);
                Check(events.Last(e => e.Type == "status").Status == "completed", "completed");
                Check(File.ReadAllText(record + ".stdin").Contains("{\"type\":\"steer\",\"message\":\"also check the lint\"}"), "the steer line");
                Check(!await manager.TrySteerAsync("pane", "too late"), "refused after the run");
            }
        }
        finally { try { Directory.Delete(directory, true); } catch (IOException) { } }
    }

    internal static async Task StoppingARunEndsItStoppedAndAResumeOfAMissingSessionStartsANewOne()
    {
        var directory = Verification.Temp();
        try
        {
            var record = Path.Combine(directory, "pi"); var events = new ConcurrentQueue<RunEvent>(); var workspace = new Workspace { Path = directory };
            await using var catalog = FakePi(record, "answer", "--pi-hang");
            await using (var manager = new RunManager(_ => Task.FromResult(workspace), catalog, "", events.Enqueue))
            {
                // This session was never written: pi would exit on it, so the run drops --session.
                await manager.StartAsync(new("pane", workspace.Id, "claude", "stop me", [], Provider: "pi", Settings: new RunSettings(PermissionMode: "fullAccess"), ResumeId: "0197c1d2-dead-7c4e-9b1a-5d2e8f6a4b34"));
                await Verification.Until(() => File.Exists(record + ".stdin") && File.ReadAllText(record + ".stdin").Contains("prompt"), 30000);
                await manager.StopAsync("pane");
                Check(events.Last(e => e.Type == "status").Status == "stopped", "stopped");
            }
            var argv = JsonSerializer.Deserialize<string[]>(File.ReadAllText(record + ".args"))!;
            Check(argv.SkipWhile(a => a != "--mode").TakeWhile(a => a != "--pi-fixture").SequenceEqual(["--mode", "rpc"]) || argv.SkipWhile(a => a != "--mode").SequenceEqual(["--mode", "rpc"]), "no --session: " + string.Join(" ", argv));
            Check(!argv.Contains("--session"), "the missing session is dropped");
        }
        finally { try { Directory.Delete(directory, true); } catch (IOException) { } }
    }

    // ── Not offered where it cannot run yet ──────────────────────────────

    internal static Task PiIsNeitherADelegationChildNorAParentNorInARemoteWorkspaceAndTheHostAdvertisesIt() => English(() =>
    {
        Check(!DelegationProviders.IsAgentPane("claude", "pi") && !DelegationProviders.MayHaveHumanChild("claude", "pi", "fullAccess"), "not a delegation agent");
        Check(DelegationProviders.HumanChildChoices("claude", "acceptEdits", false, 0, _ => DelegationAgentAvailability.Ready).Select(c => c.Agent).SequenceEqual(["claude", "codex", "gemini"]), "the menu offers the three");
        var remote = new Workspace { Path = "C:\\far", Remote = new("conn-1", "ws-1", "studio-pc") };
        Check(!RemoteWorkspaceFeatures.AddPaneEntries(remote).Contains(AddPaneMenu.AgentPrefix + "pi") && AddPaneMenu.Entries().Contains(AddPaneMenu.AgentPrefix + "pi"), "a remote workspace's menu leaves pi out");
        Check(MobileDesktopRouter.Capabilities.Contains("pi"), "the host advertises pi");
        Check(SlashCommandCatalog.Builtins("pi").Any(c => c.Invocation == "model") && SlashCommandCatalog.Builtins("pi").Any(c => c.Invocation == "new"), "slash commands");
        Check(ProviderMark.Colors("pi").Count == 1 && ProviderMark.Figures("pi").Count > 0 && ProviderMark.Outline("pi") != ProviderMark.Outline("claude") && ProviderMark.MarkedProvider("pi") == "pi", "the mark");
        Check(ProviderCatalog.IsBeta("pi") && SessionHistory.Providers.SequenceEqual(["claude", "codex", "gemini"]) && MightyGraphSupport.Providers.Contains("pi"), "beta, no history replay, a graph");
        Check(ActivitySupport.Kind("find") == "search" && ActivitySupport.Kind("ls") == "read", "pi's tool names");
        return Task.CompletedTask;
    });
}
