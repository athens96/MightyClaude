using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MightyCode.Core;

/// <summary>
/// opencode (OpenCode 2) and Hermes Agent in the CLI accounts, the CLI updates and the account usage, as the Mac has them
/// (M/MightyCore/OpencodeSupport.swift opencodeStatus, M/MightyCore/HermesSupport.swift probe and hermesStatus, CLIUpdateService.swift,
/// AccountUsageService.swift; native/macos/Tests/MightyCoreTests/OpencodeTests.swift and HermesTests.swift). Hermes answers from the
/// recorded <c>initialize</c> answers in native/contracts/fixtures/hermes-acp-*.jsonl, played by this test program (<c>--hermes-acp-cli</c>);
/// every other CLI is a fake runner. No real opencode or Hermes is started.
/// </summary>
internal static class AcpAgentAccountVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "native", "contracts", "fixtures", "setup-catalog.json"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("the repository root was not found");
    }
    private static string FixtureLine(string name) => File.ReadLines(Path.Combine(Root(), "native", "contracts", "fixtures", name)).First(line => line.Length > 0);

    /// <summary>What <c>opencode auth list --format json</c> prints (OpencodeTests.authList): an OAuth sign-in, an environment key and a key that needs signing in again.</summary>
    private const string AuthList = """[{"id":"anthropic","name":"Anthropic","connections":[{"type":"credential","id":"cred_01","label":"OAuth","method":"oauth"}]},{"id":"openrouter","name":"OpenRouter","connections":[{"type":"env","name":"OPENROUTER_API_KEY"}]},{"id":"openai","name":"OpenAI","connections":[{"type":"credential","id":"cred_02","label":"API key","method":"key","status":{"status":"needs_auth","message":"expired"}}]}]""";
    private static readonly string[] ListArguments = ["auth", "list", "--format", "json", "--standalone"];

    private sealed class FakeRunner(Func<string, IReadOnlyList<string>, CliRunResult> respond) : ICliRunner
    {
        internal readonly List<(string Executable, string[] Arguments, IReadOnlyDictionary<string, string>? Environment)> Calls = [];
        public Task<CliRunResult> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellation = default,
            IReadOnlyDictionary<string, string>? environment = null, string? workingDirectory = null)
        {
            lock (Calls) Calls.Add((executable, arguments.ToArray(), environment));
            return Task.FromResult(respond(executable, arguments));
        }
    }
    private static CliRunResult Ok(string output) => new(0, output, "", false);
    private static string Touch(params string[] parts) { var path = Path.Combine(parts); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, ""); return path; }

    // ── opencode's sign-in ─────────────────────────────────────────────────

    // Mirrors OpencodeTests.signInIsWhatAuthListNamesAndOtherwiseZensFreeModelsOnly.
    internal static Task OpencodeSignInIsWhatAuthListNamesAndOtherwiseZensFreeModelsOnly()
    {
        var signedIn = OpencodeAccounts.Status(0, AuthList);
        // A connection that needs signing in again does not count.
        Check(signedIn is { Provider: "opencode", LoggedIn: true, MethodId: CliAccountMethod.OpencodeSubscription, Account: "Anthropic, OpenRouter", CanSignOut: false }, "signed in: " + signedIn);
        Check(signedIn.Method == Locale.Get("cliAccounts.method.opencodeSubscription") && signedIn.Detail == Locale.Get("settings.cliAccounts.detailOpencodeSignOut") && signedIn.Detail.Contains("opencode auth logout", StringComparison.Ordinal),
            "a subscription, signed out in a terminal");
        Check(signedIn.Summary == "Anthropic, OpenRouter · " + signedIn.Method, "the row's line: " + signedIn.Summary);
        var environment = OpencodeAccounts.Status(0, """[{"id":"openrouter","name":"OpenRouter","connections":[{"type":"env","name":"OPENROUTER_API_KEY"}]}]""");
        Check(environment is { LoggedIn: true, MethodId: CliAccountMethod.OpencodeApiKey } && environment.Method == Locale.Get("cliAccounts.method.opencodeEnvironmentKey"), "an environment key");
        var stored = OpencodeAccounts.Status(0, """[{"id":"openai","connections":[{"type":"credential","id":"cred_03","label":"API key","method":"key"}]}]""");
        Check(stored is { LoggedIn: true, MethodId: CliAccountMethod.OpencodeApiKey, Account: "openai" } && stored.Method == Locale.Get("windows.cli.method.apiKey"), "a stored key, named by its id without a name");
        var free = OpencodeAccounts.Status(0, "[]\n");
        Check(free is { LoggedIn: false, CanSignOut: false } && free.Detail == Locale.Get("settings.cliAccounts.detailOpencodeFreeOnly") && free.Summary == CliAccountStrings.SummarySignedOut, "no sign-in: opencode Zen's free models only");
        Check(OpencodeAccounts.Status(0, """[{"id":"openai","name":"OpenAI","connections":[{"type":"credential","method":"oauth","status":{"status":"needs_auth"}}]},{"id":"x","name":"X","connections":[]}]""").LoggedIn == false, "only lapsed or no connections: signed out");
        // Anything else stays unknown, never signed out.
        var timedOut = OpencodeAccounts.Status(-1, "", timedOut: true);
        Check(timedOut.LoggedIn is null && timedOut.Detail == Locale.Get("settings.cliAccounts.detailOpencodeTimeout") && timedOut.Summary == timedOut.Detail, "a timeout");
        foreach (var (code, output) in new[] { (1, AuthList), (0, "No authenticated integrations\n"), (0, """{"integrations":[]}"""), (0, "") })
            Check(OpencodeAccounts.Status(code, output) is { LoggedIn: null } unknown && unknown.Detail == Locale.Get("settings.cliAccounts.detailOpencodeUnknown"), "unknown: " + code + " " + output);
        // OpenCode 1 has no --format json: its version names it, and it asks for OpenCode 2.
        var legacy = OpencodeAccounts.Status(1, "", "1.19.5");
        Check(legacy.LoggedIn is null && !legacy.CanSignOut && legacy.Detail == Locale.Get("settings.cliAccounts.detailOpencodeV1"), "OpenCode 1");
        Check(OpencodeAccounts.Status(1, "Unknown argument: format", "opencode v2.2.7").Detail == Locale.Get("settings.cliAccounts.detailOpencodeUnknown"), "OpenCode 2 whose list failed is unknown");
        Check(OpencodeAccounts.IsSupported("opencode v2.2.7") && OpencodeAccounts.IsSupported("2.0.0") && !OpencodeAccounts.IsSupported("1.19.5") && !OpencodeAccounts.IsSupported("local"), "OpenCode 2 is 2.0 and later");
        // Names only: no credential ever reaches the row, and a long list is cut at sixteen.
        var secret = OpencodeAccounts.Status(0, """[{"id":"anthropic","name":"Anthro\u0007pic","connections":[{"type":"credential","method":"key","key":"sk-ant-FIXTURE-SECRET","token":"FIXTURE-TOKEN"}]}]""");
        var visible = string.Join("\n", secret.ToString(), JsonSerializer.Serialize(secret), secret.Summary);
        Check(secret.Account == "Anthropic" && !visible.Contains("FIXTURE-SECRET", StringComparison.Ordinal) && !visible.Contains("FIXTURE-TOKEN", StringComparison.Ordinal), "a credential is never shown");
        var many = "[" + string.Join(",", Enumerable.Range(10, 20).Select(n => "{\"id\":\"p" + n + "\",\"connections\":[{\"type\":\"env\",\"name\":\"K" + n + "\"}]}")) + "]";
        Check(OpencodeAccounts.Status(0, many).Account!.Split(", ").Length == 16 && OpencodeAccounts.Connections(many)!.Integrations.Count == 20, "sixteen names shown");
        // Signing in can fix a failed run only while signed out.
        Check(CliAuthFailure.SignInCanFix(free) && !CliAuthFailure.SignInCanFix(signedIn) && !CliAuthFailure.SignInCanFix(timedOut), "sign-in recovery");
        Check(CliAccountSupport.StatusArguments("opencode")!.SequenceEqual(["opencode", .. ListArguments]) && CliAccountSupport.LoginArguments("opencode")!.SequenceEqual(["opencode", "auth", "login"])
            && CliAccountSupport.LogoutArguments("opencode") is null, "argv: auth list, auth login, and no sign-out the app runs");
        Check(CliAccountSupport.LoginHint("opencode") == Locale.Get("settings.cliAccounts.opencodeLoginHint") && CliAccountSupport.LoginHint("opencode")!.Contains("opencode auth login", StringComparison.Ordinal)
            && CliAccountSupport.LoginHint("pi") == Locale.Get("settings.cliAccounts.piLoginHint") && CliAccountSupport.LoginHint("claude") is null && CliAccountSupport.LoginHint("gemini") is null, "what the terminal asks");
        return Task.CompletedTask;
    }

    // Mirrors OpencodeTests.theAccountServiceAsksAuthListAndNamesOpenCode1.
    internal static async Task TheCoordinatorAsksAuthListAndAsksItsVersionOnlyWhenTheListIsNotJson()
    {
        var home = Verification.Temp();
        try
        {
            var empty = Path.Combine(home, "empty"); Directory.CreateDirectory(empty);
            var roaming = Path.Combine(home, "AppData", "Roaming");
            // npm's global folder is not on PATH: opencode.cmd is found where npm puts it.
            var shim = Touch(roaming, "npm", "opencode.cmd");
            var list = new CliRunResult(1, "", "Unknown argument: format\n", false);
            var runner = new FakeRunner((_, arguments) => arguments.SequenceEqual(["--version"]) ? Ok("1.19.5\n") : arguments.SequenceEqual(ListArguments) ? list : new(2, "", "unexpected", false));
            var coordinator = new CliAccountsCoordinator(runner, new Dictionary<string, string> { ["PATH"] = empty, ["APPDATA"] = roaming }, home);
            await coordinator.RefreshAsync(["opencode"]);
            var legacy = coordinator.Statuses["opencode"];
            Check(legacy is { Installed: true, LoggedIn: null } && legacy.Detail == Locale.Get("settings.cliAccounts.detailOpencodeV1"), "OpenCode 1: " + legacy);
            Check(runner.Calls.Select(c => string.Join(' ', c.Arguments)).SequenceEqual(["auth list --format json --standalone", "--version"]) && runner.Calls.All(c => c.Executable == shim), "auth list, then its version, from the shim");
            Check(runner.Calls.All(c => c.Environment?.GetValueOrDefault("NO_COLOR") == "1" && c.Environment?.GetValueOrDefault("CI") == "1"), "quiet");

            runner.Calls.Clear(); list = Ok(AuthList);
            var status = await coordinator.StatusAsync("opencode");
            Check(status is { LoggedIn: true, Account: "Anthropic, OpenRouter", CanSignOut: false } && runner.Calls.Count == 1, "a JSON list asks nothing more");
            // The app runs no sign-out: the status is read again, unchanged.
            runner.Calls.Clear();
            var after = await coordinator.LogoutAsync("opencode");
            Check(after.LoggedIn == true && runner.Calls.Select(c => c.Arguments).SingleOrDefault()?.SequenceEqual(ListArguments) == true, "no opencode auth logout from the app");

            // On PATH it is found there first; nowhere, it is not installed.
            var bin = Path.Combine(home, "bin");
            var onPath = Touch(bin, OperatingSystem.IsWindows() ? "opencode.exe" : "opencode");
            runner.Calls.Clear();
            await new CliAccountsCoordinator(runner, new Dictionary<string, string> { ["PATH"] = bin, ["APPDATA"] = roaming }, home).StatusAsync("opencode");
            Check(runner.Calls.All(c => c.Executable == onPath) && runner.Calls.Count == 1, "PATH first");
            // The profile folders are given empty, so a Windows PC's own opencode is never found instead.
            var missing = await new CliAccountsCoordinator(runner, new Dictionary<string, string> { ["PATH"] = empty, ["APPDATA"] = empty }, home).StatusAsync("opencode");
            Check(!missing.Installed && missing.Detail.Contains("opencode", StringComparison.Ordinal) && !missing.Detail.Contains("Claude", StringComparison.Ordinal), "not installed, by its own name: " + missing.Detail);
        }
        finally { Directory.Delete(home, true); }
    }

    // ── Hermes's sign-in ───────────────────────────────────────────────────

    // Mirrors HermesTests.signInIsTheProviderInitializeNames.
    internal static Task HermesSignInIsTheProviderItsAcpServerNames()
    {
        // The recorded answers: a configured Hermes names its provider beside the terminal setup, an unconfigured one the setup alone.
        var configured = HermesAccounts.ParseInitialize("warning line\n" + FixtureLine("hermes-acp-answer.jsonl") + "\n");
        Check(configured is { Answered: true, TimedOut: false, Provider: "custom", Version: "0.23.1" }, "configured: " + configured);
        var unconfigured = HermesAccounts.ParseInitialize(FixtureLine("hermes-acp-unconfigured.jsonl"));
        Check(unconfigured is { Answered: true, Provider: null }, "unconfigured: " + unconfigured);
        var signedIn = HermesAccounts.Status(configured);
        Check(signedIn is { Provider: "hermes", LoggedIn: true, Account: "custom", MethodId: CliAccountMethod.HermesProvider, CanSignOut: false } && signedIn.Method == Locale.Get("cliAccounts.method.hermesProvider"), "signed in: " + signedIn);
        Check(signedIn.Detail == Locale.Get("settings.cliAccounts.detailHermesSignOut") && signedIn.Detail.Contains("hermes auth", StringComparison.Ordinal), "signed out in a terminal");
        var signedOut = HermesAccounts.Status(unconfigured);
        Check(signedOut is { LoggedIn: false, CanSignOut: false } && signedOut.Detail == Locale.Get("settings.cliAccounts.detailHermesNoProvider"), "no provider: signed out");
        // No answer is unknown, not signed out.
        Check(HermesAccounts.ParseInitialize("The ACP server can't start: its protocol packages are missing from this install.\n") is null, "the missing ACP packages are no answer");
        Check(HermesAccounts.ParseInitialize("""{"jsonrpc":"2.0","id":"mighty-1","error":{"code":-32603,"message":"Internal error"}}""") is { Answered: false, TimedOut: false }, "an error answer");
        Check(HermesAccounts.ParseInitialize(FixtureLine("hermes-acp-answer.jsonl").Replace("\"mighty-1\"", "\"mighty-2\"")) is null && HermesAccounts.ParseInitialize("") is null, "only the answer to the probe's request");
        Check(HermesAccounts.Status(new(null, false, false, null)) is { LoggedIn: null } unknown && unknown.Detail == Locale.Get("settings.cliAccounts.detailHermesUnknown") && unknown.Detail.Contains("hermes pm install --extra acp", StringComparison.Ordinal), "unknown says how to add the ACP packages");
        Check(HermesAccounts.Status(new(null, false, true, null)) is { LoggedIn: null } late && late.Detail == Locale.Get("settings.cliAccounts.detailHermesTimeout"), "a timeout");
        Check(HermesAccounts.Status(null).Detail == CliAccountStrings.DetailRunFailed, "a Hermes that cannot start");
        // Only an agent method other than the terminal setup is a provider.
        using (var methods = JsonDocument.Parse("""{"authMethods":[{"id":"hermes-setup","type":"terminal"},{"id":"other-setup","type":"terminal"},{"id":""},{"id":"nous","name":"Nous Portal"}]}"""))
            Check(HermesAccounts.Provider(methods.RootElement) == "nous", "the provider's method");
        // The one request: initialize with the capabilities a run announces, under the channel's first id.
        Check(HermesAccounts.InitializeLine == """{"jsonrpc":"2.0","id":"mighty-1","method":"initialize","params":{"protocolVersion":1,"clientCapabilities":{"fs":{"readTextFile":false,"writeTextFile":false},"terminal":false,"elicitation":{"form":{}}},"clientInfo":{"name":"mighty-code","title":"Mighty Code","version":"1.0"}}}""", "the initialize line");
        Check(HermesAccounts.VersionLine("Hermes Agent v0.23.1 (2026.9.24) · stable · local 3b4215785882\r\nInstall: C:\\Users\\someone\\AppData\\Local\\hermes\\hermes-agent\r\nPython: 3.12.8\r\nUp to date\r\n") == "Hermes Agent v0.23.1 (2026.9.24) · stable · local 3b4215785882"
            && HermesAccounts.VersionLine("\n  0.23.0  \nmore") == "0.23.0" && HermesAccounts.VersionLine("") == "", "the version is the first line");
        Check(CliAuthFailure.SignInCanFix(signedOut) && !CliAuthFailure.SignInCanFix(signedIn), "sign-in recovery");
        Check(CliAccountSupport.StatusArguments("hermes")!.SequenceEqual(["hermes", "acp"]) && CliAccountSupport.LoginArguments("hermes")!.SequenceEqual(["hermes", "model"]) && CliAccountSupport.LogoutArguments("hermes") is null
            && CliAccountSupport.LoginHint("hermes") == Locale.Get("settings.cliAccounts.hermesLoginHint") && CliAccountSupport.LoginHint("hermes")!.Contains("hermes model", StringComparison.Ordinal), "argv and what the terminal asks");
        return Task.CompletedTask;
    }

    private static string Marker(string state) => "mighty-hermes-probe-" + Path.GetFileName(state.TrimEnd(Path.DirectorySeparatorChar)) + ".marker";
    private static Dictionary<string, string> ProcessEnvironment() => Environment.GetEnvironmentVariables().Keys.Cast<string>().ToDictionary(key => key, key => Environment.GetEnvironmentVariable(key) ?? "");

    /// <summary>
    /// <c>hermes acp</c> as 0.23.1 answers the probe: it reads the one request, answers with the recorded line (after a log line on standard
    /// error), then serves until its input ends. <paramref name="mode"/> <c>file</c> reads the mode from <c>mode.txt</c>.
    /// </summary>
    internal static async Task FakeHermesAsync(string state, string mode, string[] args)
    {
        if (mode == "file") mode = (await File.ReadAllTextAsync(Path.Combine(state, "mode.txt"))).Trim();
        await File.WriteAllTextAsync(Path.Combine(state, mode + ".argv.json"), JsonSerializer.Serialize(args));
        await File.WriteAllTextAsync(Path.Combine(state, mode + ".run.json"), JsonSerializer.Serialize(new Dictionary<string, string?>
        {
            ["NO_COLOR"] = Environment.GetEnvironmentVariable("NO_COLOR"),
            ["inTemporaryFolder"] = File.Exists(Path.Combine(Environment.CurrentDirectory, Marker(state))) ? "yes" : "no",
        }));
        if (!args.SequenceEqual(["acp"])) { await Console.Error.WriteLineAsync("hermes: error: argument command: invalid choice: '" + string.Join(' ', args) + "'"); Environment.ExitCode = 2; return; }
        if (mode == "missing")
        {
            await Console.Error.WriteLineAsync("The ACP server can't start: its protocol packages are missing from this install.");
            await Console.Error.WriteLineAsync("From the Hermes environment, run: hermes pm install --extra acp");
            Environment.ExitCode = 1; return;
        }
        var request = await Console.In.ReadLineAsync();
        await File.WriteAllTextAsync(Path.Combine(state, mode + ".stdin"), request ?? "<none>");
        await Console.Error.WriteLineAsync("2026-11-11 01:18:54 [INFO] acp_adapter.entry: serving ACP on stdio");
        var answer = mode switch
        {
            "answer" => "warning line\n" + FixtureLine("hermes-acp-answer.jsonl"),
            "unconfigured" => FixtureLine("hermes-acp-unconfigured.jsonl"),
            "error" => """{"jsonrpc":"2.0","id":"mighty-1","error":{"code":-32603,"message":"Internal error","data":{"details":"boom"}}}""",
            _ => null,
        };
        if (answer is not null) { await Console.Out.WriteLineAsync(answer); await Console.Out.FlushAsync(); }
        var more = 0;
        while (await Console.In.ReadLineAsync() is not null) more++;
        await File.WriteAllTextAsync(Path.Combine(state, mode + ".closed"), "input ended after " + more + " more line(s)");
    }

    // Mirrors HermesTests.theAccountServiceAsksHermessACPServerAndClosesIt, on the probe itself.
    internal static async Task TheProbeWritesInitializeOnceReadsTheAnswerAndClosesItsInput()
    {
        var state = Verification.Temp();
        var marker = Path.Combine(Path.GetTempPath(), Marker(state));
        try
        {
            await File.WriteAllTextAsync(marker, "");
            var environment = ProcessEnvironment();
            async Task<HermesAccounts.Probe?> Run(string mode, double seconds = 20) =>
                await HermesAccounts.ProbeAsync(Verification.Self("--hermes-acp-cli", state, mode), environment, Path.GetTempPath(), TimeSpan.FromSeconds(seconds));
            string Read(string name) => File.ReadAllText(Path.Combine(state, name));

            var answer = await Run("answer");
            Check(answer is { Answered: true, TimedOut: false, Provider: "custom", Version: "0.23.1" }, "the configured answer: " + answer);
            Check(Read("answer.stdin") == HermesAccounts.InitializeLine, "initialize, written once as one line: " + Read("answer.stdin"));
            Check(JsonSerializer.Deserialize<string[]>(Read("answer.argv.json"))!.SequenceEqual(["acp"]), "hermes acp");
            Check(Read("answer.closed") == "input ended after 0 more line(s)", "its input closed after the answer, with nothing more written");
            var run = JsonSerializer.Deserialize<Dictionary<string, string?>>(Read("answer.run.json"))!;
            Check(run["NO_COLOR"] == "1" && run["inTemporaryFolder"] == "yes", "quiet, in the temporary folder");

            Check(await Run("unconfigured") is { Answered: true, Provider: null } && File.Exists(Path.Combine(state, "unconfigured.closed")), "the setup alone");
            var missing = await Run("missing");
            Check(missing is { Answered: false, TimedOut: false } && HermesAccounts.Status(missing).Detail == Locale.Get("settings.cliAccounts.detailHermesUnknown"), "no ACP packages: unknown, " + missing);
            Check(await Run("error") is { Answered: false, TimedOut: false }, "an error answer");
            var silent = await Run("silent", 1.5);
            Check(silent is { Answered: false, TimedOut: true } && HermesAccounts.Status(silent).Detail == Locale.Get("settings.cliAccounts.detailHermesTimeout"), "no answer in time: " + silent);
            Check(File.Exists(Path.Combine(state, "silent.closed")), "its input closed when the wait ran out");
            Check(await HermesAccounts.ProbeAsync(new CliCommand(Path.Combine(state, "no-hermes-here"), []), environment, Path.GetTempPath(), TimeSpan.FromSeconds(5)) is null, "a Hermes that cannot start");
        }
        finally { File.Delete(marker); Directory.Delete(state, true); }
    }

    internal static async Task TheCoordinatorFindsHermesWhereItsInstallerPutsItAndAsksItsAcpServer()
    {
        var home = Verification.Temp();
        try
        {
            var empty = Path.Combine(home, "empty"); Directory.CreateDirectory(empty);
            var local = Path.Combine(home, "AppData", "Local");
            var installed = Touch(local, "hermes", "bin", "hermes.exe");
            var asked = new List<(CliCommand Command, IReadOnlyDictionary<string, string> Environment)>();
            HermesAccounts.Probe? answer = new("nous", true, false, "0.23.1");
            var runner = new FakeRunner((_, _) => new(2, "", "the runner is not for Hermes", false));
            var fixedEnvironment = new Dictionary<string, string> { ["PATH"] = empty, ["LOCALAPPDATA"] = local };
            var coordinator = new CliAccountsCoordinator(runner, fixedEnvironment, home, (command, values, _) => { asked.Add((command, values)); return Task.FromResult<HermesAccounts.Probe?>(answer); });
            await coordinator.RefreshAsync(["hermes"]);
            Check(coordinator.Statuses["hermes"] is { Installed: true, LoggedIn: true, Account: "nous", CanSignOut: false }, "signed in: " + coordinator.Statuses["hermes"]);
            Check(asked.Single().Command.Binary == installed && asked.Single().Command.Prefix.Length == 0 && asked.Single().Environment.GetValueOrDefault("LOCALAPPDATA") == local && runner.Calls.Count == 0,
                "%LOCALAPPDATA%\\hermes\\bin\\hermes.exe is asked, never through the runner");
            answer = null;
            Check((await coordinator.StatusAsync("hermes")).Detail == CliAccountStrings.DetailRunFailed, "one that cannot start");
            // The app runs no sign-out either: the status is read again.
            answer = new(null, true, false, "0.23.1");
            Check(await coordinator.LogoutAsync("hermes") is { LoggedIn: false } && runner.Calls.Count == 0, "no sign-out command");
            // $HERMES_HOME\bin when it is set (its installer's .cmd fallback too); nowhere, not installed.
            var hermesHome = Path.Combine(home, "hermes-home");
            var fallback = Touch(hermesHome, "bin", "hermes.cmd");
            asked.Clear(); answer = new("openrouter", true, false, null);
            await new CliAccountsCoordinator(runner, new Dictionary<string, string> { ["PATH"] = empty, ["LOCALAPPDATA"] = local, ["HERMES_HOME"] = hermesHome }, home, (command, values, _) => { asked.Add((command, values)); return Task.FromResult<HermesAccounts.Probe?>(answer); }).StatusAsync("hermes");
            Check(asked.Single().Command.Binary == fallback, "$HERMES_HOME\\bin\\hermes.cmd");
            Check(CliAccountSupport.InstallerCopies("hermes", new Dictionary<string, string> { ["HERMES_HOME"] = "relative", ["LOCALAPPDATA"] = local }).SequenceEqual([installed, Path.Combine(local, "hermes", "bin", "hermes.cmd")]), "a HERMES_HOME that is not a full path is not used");
            Check(CliAccountSupport.InstallerCopies("claude", fixedEnvironment).Count == 0, "only opencode and Hermes");
            var missing = await new CliAccountsCoordinator(runner, new Dictionary<string, string> { ["PATH"] = empty, ["LOCALAPPDATA"] = empty }, home, (_, _, _) => throw new InvalidOperationException("nothing to ask")).StatusAsync("hermes");
            Check(!missing.Installed && missing.Detail.Contains("Hermes", StringComparison.Ordinal), "not installed: " + missing.Detail);

            // The app's own probe, end to end, on a Hermes found along the PATH (a shell launcher; Windows runs the probe check above).
            if (OperatingSystem.IsWindows()) return;
            var state = Verification.Temp();
            var marker = Path.Combine(Path.GetTempPath(), Marker(state));
            try
            {
                await File.WriteAllTextAsync(marker, "");
                var bin = Path.Combine(home, "bin"); Directory.CreateDirectory(bin);
                var self = Verification.Self("--hermes-acp-cli", state, "file");
                static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";
                var launcher = Path.Combine(bin, "hermes");
                await File.WriteAllTextAsync(launcher, "#!/bin/sh\nexec " + string.Join(' ', new[] { self.Binary }.Concat(self.Prefix).Select(Quote)) + " \"$@\"\n");
                File.SetUnixFileMode(launcher, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                var values = ProcessEnvironment(); values["PATH"] = bin;
                var live = new CliAccountsCoordinator(runner, values, home);
                await File.WriteAllTextAsync(Path.Combine(state, "mode.txt"), "unconfigured");
                var signedOut = await live.StatusAsync("hermes");
                Check(signedOut is { Installed: true, LoggedIn: false }, "the live probe, unconfigured: " + signedOut);
                await File.WriteAllTextAsync(Path.Combine(state, "mode.txt"), "answer");
                var signedIn = await live.StatusAsync("hermes");
                Check(signedIn is { LoggedIn: true, Account: "custom" }, "the live probe, configured: " + signedIn);
                var run = JsonSerializer.Deserialize<Dictionary<string, string?>>(File.ReadAllText(Path.Combine(state, "answer.run.json")))!;
                Check(run["inTemporaryFolder"] == "yes" && File.ReadAllText(Path.Combine(state, "answer.stdin")) == HermesAccounts.InitializeLine && File.Exists(Path.Combine(state, "answer.closed")) && runner.Calls.Count == 0,
                    "in the temporary folder, one initialize, its input closed");
            }
            finally { File.Delete(marker); Directory.Delete(state, true); }
        }
        finally { Directory.Delete(home, true); }
    }

    // ── CLI updates ────────────────────────────────────────────────────────

    // Mirrors CLIUpdateService.swift's opencode and hermes plans.
    internal static async Task OpencodeUpgradesItselfOpenCode1IsLeftToTheSetupScreenAndHermesUpdatesWithYes()
    {
        var home = Verification.Temp();
        try
        {
            var empty = Path.Combine(home, "empty"); Directory.CreateDirectory(empty);
            var roaming = Path.Combine(home, "AppData", "Roaming"); var local = Path.Combine(home, "AppData", "Local");
            var opencode = Touch(roaming, "npm", "opencode.cmd");
            var hermes = Touch(local, "hermes", "bin", "hermes.exe");
            var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["PATH"] = empty, ["APPDATA"] = roaming, ["LOCALAPPDATA"] = local };
            var opencodeVersion = "opencode v2.2.7";
            var hermesVersion = "Hermes Agent v0.23.0 (2026.9.1) · stable · local 1a2b3c4d5e6f\nInstall: C:\\Users\\someone\\AppData\\Local\\hermes\\hermes-agent\nPython: 3.12.8\nUpdate available\n";
            var runner = new FakeRunner((executable, arguments) =>
            {
                if (arguments.SequenceEqual(["--version"])) return Ok((executable == hermes ? hermesVersion : opencodeVersion) + "\n");
                if (executable == opencode && arguments.SequenceEqual(["upgrade"])) { opencodeVersion = "opencode v2.2.8"; return Ok("Upgraded to 2.2.8\n"); }
                if (executable == hermes && arguments.SequenceEqual(["update", "--yes"])) { hermesVersion = hermesVersion.Replace("v0.23.0 (2026.9.1)", "v0.23.1 (2026.9.24)"); return Ok("Updated\n"); }
                return new(2, "", "unexpected: " + string.Join(' ', arguments), false);
            });
            var service = new CliUpdateService(runner, environment, home);

            var inspected = await service.InspectAsync("opencode");
            Check(inspected is { Method: "opencode", CanUpdate: true, Version: "opencode v2.2.7" } && inspected.ExecutablePath == opencode && inspected.Detail == Locale.Get("settings.cliUpdate.detailOpencode"), "OpenCode 2 upgrades itself: " + inspected);
            var upgraded = await service.UpdateAsync("opencode");
            Check(upgraded is { Status: "updated", BeforeVersion: "opencode v2.2.7", AfterVersion: "opencode v2.2.8", Method: "opencode" }, "upgraded: " + upgraded);
            Check(runner.Calls.Single(c => !c.Arguments.SequenceEqual(["--version"])) is var call && call.Executable == opencode && call.Arguments.SequenceEqual(["upgrade"]), "opencode upgrade, by the shim npm left");
            Check(CliUpdateService.ResultRow(upgraded) == "opencode · " + CliUpdateStrings.StatusUpdated && CliUpdateService.VersionChange(upgraded) == "opencode v2.2.7 → opencode v2.2.8", "the row");

            // OpenCode 1's own upgrade stays on 1: it is not run, and the setup screen's reinstall is named instead.
            runner.Calls.Clear(); opencodeVersion = "1.19.5";
            var legacy = await service.UpdateAsync("opencode");
            Check(legacy is { Status: "skipped", Method: "opencode", BeforeVersion: "1.19.5" } && legacy.Detail == Locale.Get("settings.cliUpdate.detailOpencodeV1") && runner.Calls.All(c => c.Arguments.SequenceEqual(["--version"])), "OpenCode 1 is skipped: " + legacy);
            Check((await service.InspectAsync("opencode")) is { CanUpdate: false, Method: "opencode" }, "and not offered");

            runner.Calls.Clear();
            var before = await service.InspectAsync("hermes");
            Check(before is { Method: "hermes", CanUpdate: true, Version: "Hermes Agent v0.23.0 (2026.9.1) · stable · local 1a2b3c4d5e6f" } && before.ExecutablePath == hermes && before.Detail == Locale.Get("settings.cliUpdate.detailHermes"), "Hermes, by its version's first line: " + before);
            var updated = await service.UpdateAsync("hermes");
            Check(updated is { Status: "updated", AfterVersion: "Hermes Agent v0.23.1 (2026.9.24) · stable · local 1a2b3c4d5e6f", Method: "hermes" }, "updated: " + updated);
            Check(runner.Calls.Single(c => !c.Arguments.SequenceEqual(["--version"])) is var update && update.Executable == hermes && update.Arguments.SequenceEqual(["update", "--yes"]), "hermes update --yes");
            Check(CliUpdateService.ResultRow(updated) == "Hermes · " + CliUpdateStrings.StatusUpdated && CliUpdateService.ProviderLabel("opencode") == "opencode" && CliUpdateService.ProviderLabel("hermes") == "Hermes", "the rows' names");

            // $HERMES_HOME\bin is looked in when it is set; with neither CLI anywhere, nothing runs.
            var hermesHome = Path.Combine(home, "hermes-home");
            var fallback = Touch(hermesHome, "bin", "hermes.cmd");
            runner.Calls.Clear();
            var elsewhere = await new CliUpdateService(new FakeRunner((executable, _) => Ok(executable == fallback ? "Hermes Agent v0.23.1\n" : "")), new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["PATH"] = empty, ["HERMES_HOME"] = hermesHome }, home).InspectAsync("hermes");
            Check(elsewhere is { Method: "hermes", ExecutablePath: var path } && path == fallback, "$HERMES_HOME\\bin: " + elsewhere);
            var none = new FakeRunner((_, _) => Ok("x"));
            var nowhere = new CliUpdateService(none, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["PATH"] = empty, ["APPDATA"] = empty, ["LOCALAPPDATA"] = empty }, home);
            Check(await nowhere.UpdateAsync("opencode") is { Status: "skipped", Method: "missing" } && await nowhere.UpdateAsync("hermes") is { Status: "skipped", Method: "missing" } && none.Calls.Count == 0, "missing: nothing runs");
        }
        finally { Directory.Delete(home, true); }
    }

    // ── Account usage ──────────────────────────────────────────────────────

    // Mirrors AccountUsageService.swift: opencode and Hermes have no account usage to read.
    internal static async Task OpencodeAndHermesHaveNoAccountUsageAndNoCliIsAsked()
    {
        var home = Verification.Temp();
        try
        {
            var looked = new List<string>();
            var probe = AccountUsageRuntime.Probe((provider, _) => { looked.Add(provider); return Task.FromResult<CliCommand?>(null); }, () => new Dictionary<string, string>(), () => home, home);
            foreach (var (provider, key) in new[] { ("opencode", "accountUsage.detailOpencodeUnavailable"), ("hermes", "accountUsage.detailHermesUnavailable"), ("pi", "accountUsage.detailPiUnavailable") })
            {
                AccountUsageFailure? failure = null;
                try { await probe(provider, CancellationToken.None); } catch (AccountUsageFailure caught) { failure = caught; }
                Check(failure is { Kind: AccountUsageFailureKind.Unavailable } && failure.Detail == Locale.Get(key), provider + ": unavailable, " + failure?.Detail);
            }
            Check(looked.Count == 0, "no CLI is looked for");

            // The bar's chips and the popover's cards, through the service as the app reads them.
            await using var service = new AccountUsageService(probe);
            var status = new AccountUsageStatus(service);
            status.Update(new AppSnapshot { Workspaces = [new Workspace { Id = "w", Path = home }], Sessions = [new() { WorkspaceId = "w", Provider = "hermes" }, new() { WorkspaceId = "w", Provider = "opencode" }, new() { WorkspaceId = "w", Provider = "opencode" }] });
            Check(status.Providers.SequenceEqual(["opencode", "hermes"]) && status.Targets().SequenceEqual(["opencode", "hermes"]), "a chip each, in the provider order");
            await status.RefreshAsync(force: true);
            var cards = status.Cards();
            Check(cards.Select(c => (c.Provider, c.Title, c.Detail)).SequenceEqual([("opencode", ProviderCatalog.Name("opencode"), Locale.Get("accountUsage.detailOpencodeUnavailable")), ("hermes", ProviderCatalog.Name("hermes"), Locale.Get("accountUsage.detailHermesUnavailable"))])
                && cards.All(c => c.Windows.Count == 0), "each card says why it has none");
            Check(status.Snapshot("opencode")?.Status == "unavailable" && status.Snapshot("hermes")?.Status == "unavailable", "unavailable, not an error");
            Check(status.Chips().All(chip => chip.Text == AccountUsageStrings.ChipEmpty && !chip.Warning), "the chips stay empty");
            Check(status.LeadingBars().Count == 0, "the dashboard draws no bars for them");
        }
        finally { Directory.Delete(home, true); }
    }

    // ── WinUI ──────────────────────────────────────────────────────────────

    /// <summary>The Windows CLI accounts section lists every provider, as the Mac's lists ProviderOptions.ids, and says what a terminal asks.</summary>
    internal static Task TheWindowsAccountsSectionListsEveryProvider()
    {
        var source = File.ReadAllText(Path.Combine(Root(), "native", "windows", "MightyCode.WinUI", "MainWindow.Settings.cs"));
        var list = Regex.Match(source, @"CliAccountProviders = \[(?<names>[^\]]*)\];");
        Check(list.Success, "CliAccountProviders is a list");
        var names = Regex.Matches(list.Groups["names"].Value, "\"([a-z]+)\"").Select(match => match.Groups[1].Value).ToList();
        Check(names.SequenceEqual(Wire.Providers), "the accounts section lists " + string.Join(", ", names));
        Check(source.Contains("CliAccountSupport.LoginHint(provider)", StringComparison.Ordinal), "a terminal sign-in's row says what the terminal asks");
        return Task.CompletedTask;
    }
}
