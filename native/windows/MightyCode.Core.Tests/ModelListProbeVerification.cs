using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MightyCode.Core;

/// <summary>
/// Each CLI's model-list read against what the real CLI on the developer's Mac printed (native/contracts/fixtures/model-list-*), the
/// arguments each read may use, and what the menu gets when a read fails: the last list the CLI gave, kept and marked, or the built-in
/// list with the reason (M/MightyCore/Tests ModelListProbeTests).
/// </summary>
internal static class ModelListProbeVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "native", "contracts", "fixtures", "model-list-agy.txt"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("the repository root was not found");
    }
    private static string FixturePath(string name) => Path.Combine(Root(), "native", "contracts", "fixtures", name);
    private static string Fixture(string name) => File.ReadAllText(FixturePath(name));
    private static string Json(object? value) => JsonSerializer.Serialize(value, Wire.Json);
    private static async Task Korean(Func<Task> body)
    {
        var saved = Locale.LanguagePreference; Locale.LanguagePreference = "ko"; Locale.ResetCache();
        try { await body(); } finally { Locale.LanguagePreference = saved; Locale.ResetCache(); }
    }

    /// <summary>
    /// What each real CLI accepted, run by hand with exactly these arguments (versions beside them). A flag one CLI takes is not another's:
    /// agy 1.5.5 refuses <c>models --standalone</c> ("flags provided but not defined"), which once left Antigravity's menu on the built-in
    /// list on the Mac. The fake CLIs below refuse anything else, as the real ones do.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string[]> Verified = new Dictionary<string, string[]>
    {
        // claude 2.1.322, checked on the Mac with these very arguments: a headless stream-json session, sent only `initialize`.
        ["claude"] = ["--print", "--verbose", "--input-format", "stream-json", "--output-format", "stream-json", "--no-session-persistence", "--permission-mode", "dontAsk", "--permission-prompts", "none", "--strict-mcp-config", "--mcp-config", "{\"mcpServers\":{}}", "--tools", "", "--safe-mode"],
        // codex-cli 0.167.0: the app-server over stdio, `initialize` then `model/list`.
        ["codex"] = ["app-server", "--listen", "stdio://"],
        // agy 1.5.5
        ["gemini"] = ["models"],
        // pi 1.5.2
        ["pi"] = ["--list-models"],
        // opencode 2.2.7, without OpenCode 2's background service
        ["opencode"] = ["models", "--standalone"],
    };

    /// <summary>With DISABLE_TELEMETRY Claude Code answers with its built-in list (Fable 5.2 read as Fable 5.1 [1m]).</summary>
    internal static Task TheProbeLetsTheCliFetchTheAccountsCurrentModels()
    {
        // Only a variable the user's own environment already sets may reach the CLIs; the app adds none.
        var inherited = CliEnvironment.Current().ContainsKey("DISABLE_TELEMETRY");
        var claude = ProviderCatalog.ClaudeHeadlessEnvironment();
        Check(inherited || !claude.ContainsKey("DISABLE_TELEMETRY"), "the Claude model list probe sets no DISABLE_TELEMETRY");
        Check(inherited || !ProviderCatalog.QuietEnvironment().ContainsKey("DISABLE_TELEMETRY"), "no other read or run sets it either");
        Check(claude.GetValueOrDefault("DISABLE_AUTOUPDATER") == "1" && claude.GetValueOrDefault("CLAUDE_CODE_SAFE_MODE") == "1" && claude.GetValueOrDefault("DISABLE_ERROR_REPORTING") == "1",
            "the probe stays quiet otherwise");
        return Task.CompletedTask;
    }

    internal static Task EveryReadRunsTheArgumentsItsCliWasCheckedWith()
    {
        foreach (var (provider, arguments) in Verified)
            Check(ProviderCatalog.ModelListArguments(provider)?.SequenceEqual(arguments) == true, provider + ": " + string.Join(' ', ProviderCatalog.ModelListArguments(provider) ?? []));
        Check(ProviderCatalog.ModelListArguments("hermes") is null && ProviderCatalog.ModelListArguments("other") is null, "no list command for anything else");
        // Hermes 0.23.1 has no list command: its menu is the picker its last session offered.
        Check(Wire.Providers.Where(p => p != "hermes").All(Verified.ContainsKey), "every provider's read is pinned");
        Check(ProviderCatalog.ModelListCommand("opencode") == "opencode models --standalone", "opencode is named with its arguments");
        Check(ProviderCatalog.ModelListCommand("gemini") == "agy models" && ProviderCatalog.ModelListCommand("pi") == "pi --list-models", "agy and pi are named with their arguments");
        Check(ProviderCatalog.ModelListCommand("claude") == "claude" && ProviderCatalog.ModelListCommand("codex") == "codex app-server --listen stdio://", "claude by its name alone");
        return Task.CompletedTask;
    }

    // ── The real CLIs' output ────────────────────────────────────────────────

    internal static Task TheRealCliOutputsReachEachList()
    {
        var agyText = Fixture("model-list-agy.txt");
        var agy = ProviderCatalog.AntigravityCatalog(agyText);
        Check(agy.IsFresh && agy.Models.Count == 17, "agy: 17 rows, got " + agy.Models.Count);
        Check(agy.Models.Select(m => m.Value).Take(4).SequenceEqual(["default", "gemini-4-carbon-medium", "gemini-4-carbon-high", "gemini-4-carbon-low"]), "agy: its new models first");
        Check(agy.Models.First(m => m.Value == "gemini-4-carbon-medium").DisplayName == "Gemini 4 Carbon (Medium)" && agy.Models[^1].Value == "gpt-oss-120b-medium", "agy: its names and last row");
        // Not in the built-in list: only the CLI's own list brings it.
        Check(!ProviderCatalog.Fallback("gemini").Models.Any(m => m.Value == "gemini-4-carbon-medium"), "Gemini 4 Carbon is the CLI's alone");
        // agy 1.5.5 writes "Fetching available models..." to standard error; an older one wrote it first on standard output.
        Check(Json(ProviderCatalog.AntigravityCatalog("Fetching available models...\n" + agyText)) == Json(agy), "agy: the older first line changes nothing");

        var pi = ProviderCatalog.PiCatalog(Fixture("model-list-pi.txt"));
        Check(pi.IsFresh && pi.Models.Count == 19, "pi: every row, got " + pi.Models.Count);
        foreach (var model in new[] { "anthropic/claude-fable-5-2", "anthropic/claude-haiku-5-5", "anthropic/claude-opus-5-5", "anthropic/claude-sonnet-5-5" }) Check(pi.Models.Any(m => m.Value == model), "pi: " + model);
        Check(pi.Models.Skip(1).All(m => m.SupportsEffort == true), "pi: every row thinks");

        var codexLines = Fixture("model-list-codex.jsonl").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        using var answer = JsonDocument.Parse(codexLines.Single(line => line.StartsWith("{\"id\":2,", StringComparison.Ordinal)));
        var codex = ProviderCatalog.CodexCatalog(answer.RootElement.GetProperty("result").GetProperty("data"));
        Check(codex.IsFresh && codex.Models.Select(m => m.Value).SequenceEqual(["default", "gpt-6.1-sol", "gpt-6-astra", "gpt-6-sol", "gpt-6-luna", "gpt-5.6-sol", "gpt-6.1-terra", "gpt-5.6-terra", "gpt-5.6-luna", "gpt-5.5"]),
            "codex: " + string.Join(", ", codex.Models.Select(m => m.Value)));
        Check(codex.Models[0].ResolvedModel == "gpt-6.1-sol", "codex: the default is the row it marks isDefault");
        // codex lists `ultra` ("Maximum reasoning with automatic task delegation") for this model, which a Codex pane may take.
        Check(codex.Models[1].SupportedEffortLevels?.SequenceEqual(["low", "medium", "high", "xhigh", "max", "ultra"]) == true, "codex: " + string.Join(", ", codex.Models[1].SupportedEffortLevels ?? []));

        var claudeLines = Fixture("model-list-claude.jsonl").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        using var initialize = JsonDocument.Parse(claudeLines[1]);
        var claude = ProviderCatalog.ClaudeCatalog(initialize.RootElement.GetProperty("response").GetProperty("response").GetProperty("models"));
        Check(claude.IsFresh && claude.Models.Count == 14, "claude: 14 rows, got " + claude.Models.Count);
        Check(claude.Models[0].ResolvedModel == "claude-opus-5-5", "claude: the default stands for Opus 5.5");
        Check(claude.Models.FirstOrDefault(m => m.Value == "fable") is { DisplayName: "Fable 5.2", ResolvedModel: "claude-fable-5-2" }, "claude: fable is Fable 5.2");
        Check(claude.Models.Any(m => m.Value == "claude-fable-5-1") && claude.Models.Any(m => m.Value == "haiku" && m.ResolvedModel == "claude-haiku-5-5"), "claude: the older Fable and Haiku 5.5");
        // An answer that lists no model is a failed read, as on the Mac.
        using var empty = JsonDocument.Parse("[]");
        Check(ProviderCatalog.ClaudeCatalog(empty.RootElement).Source == "fallback" && ProviderCatalog.CodexCatalog(empty.RootElement).Source == "fallback", "no model is the built-in list");
        return Task.CompletedTask;
    }

    internal static Task AFailedReadNamesTheCommandAndTheLineThatSaysWhy() => Korean(() =>
    {
        var usage = Fixture("model-list-agy-unknown-flag.txt");
        var exit = ProviderCatalog.ReadFailure("gemini", Locale.Get("provider.catalog.failure.exit", new Dictionary<string, string> { ["code"] = "1" }), usage);
        Check(exit == "agy models: 종료 코드 1 · Error: flags provided but not defined: -standalone", exit);
        // No line naming an error: the first that says anything (pi without a sign-in).
        var none = ProviderCatalog.ReadFailure("pi", Locale.Get("provider.catalog.failure.noModels"), "No models available. Use /login to log in.\n  /x/docs/providers.md\n");
        Check(none == "pi --list-models: 출력에서 모델을 찾지 못했습니다 · No models available. Use /login to log in.", none);
        Check(ProviderCatalog.ReadFailure("codex", Locale.Get("process.error.timeout")) == "codex app-server --listen stdio://: " + Locale.Get("process.error.timeout"), "a timeout has no line of its own");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var redacted = ProviderCatalog.ReadFailure("pi", "x", "error: " + Path.Combine(home, ".pi", "agent", "auth.json") + " unreadable");
        Check(!redacted.Contains(home, StringComparison.OrdinalIgnoreCase) && redacted.Contains("~", StringComparison.Ordinal), "the home folder reads ~: " + redacted);
        Check(Encoding.UTF8.GetByteCount(ProviderCatalog.ReadFailure("pi", "x", string.Concat(Enumerable.Repeat("error ", 400)))) <= 400, "one bounded line");
        return Task.CompletedTask;
    });

    // ── Each read, end to end, with a fake CLI that refuses other arguments ────────────

    /// <summary>
    /// The four CLIs, as this test program answers for them (<c>--model-list-cli</c>): each answers its verified arguments with the real CLI's
    /// recorded output and refuses any other, as the real ones do (agy with its own usage error). Each writes the arguments it got.
    /// </summary>
    private sealed class FakeListClis : IDisposable
    {
        internal readonly string State = Verification.Temp();
        internal ProviderCatalog Catalog(string? storage = null) => new((provider, _) => Task.FromResult<CliCommand?>(Command(provider)), storage);
        internal CliCommand Command(string provider) => Verification.Self("--model-list-cli", provider, State);
        internal string[]? Arguments(string provider) => File.Exists(Path.Combine(State, ProviderCatalog.Executable(provider) + "-argv.json"))
            ? JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(State, ProviderCatalog.Executable(provider) + "-argv.json"))) : null;
        internal void Fail(string provider, string stderr) => File.WriteAllText(Path.Combine(State, ProviderCatalog.Executable(provider) + "-fail"), stderr);
        internal void Recover(string provider) => File.Delete(Path.Combine(State, ProviderCatalog.Executable(provider) + "-fail"));
        internal void Output(string provider, string stdout) => File.WriteAllText(Path.Combine(State, ProviderCatalog.Executable(provider) + "-output"), stdout);
        public void Dispose() { try { Directory.Delete(State, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    /// <summary>The fake CLI's side (Program.cs <c>--model-list-cli provider state …</c>).</summary>
    internal static async Task FakeCliAsync(string provider, string state, string[] args)
    {
        var name = ProviderCatalog.Executable(provider);
        await using var stdout = Console.OpenStandardOutput(); await using var stderr = Console.OpenStandardError();
        // Bytes, not the console's code page: the parent reads UTF-8.
        static async Task Say(Stream stream, string text) { await stream.WriteAsync(Encoding.UTF8.GetBytes(text)); await stream.FlushAsync(); }
        await File.WriteAllTextAsync(Path.Combine(state, name + "-argv.json"), JsonSerializer.Serialize(args));
        if (provider == "claude")
            await File.WriteAllTextAsync(Path.Combine(state, "claude-env.json"), JsonSerializer.Serialize(new Dictionary<string, string?> { ["CLAUDE_CODE_SAFE_MODE"] = Environment.GetEnvironmentVariable("CLAUDE_CODE_SAFE_MODE"), ["DISABLE_TELEMETRY"] = Environment.GetEnvironmentVariable("DISABLE_TELEMETRY") }));
        var failure = Path.Combine(state, name + "-fail");
        if (File.Exists(failure)) { await Say(stderr, await File.ReadAllTextAsync(failure)); Environment.ExitCode = 1; return; }
        if (!Verified.TryGetValue(provider, out var verified) || !args.SequenceEqual(verified))
        {
            if (provider == "gemini") { await Say(stderr, Fixture("model-list-agy-unknown-flag.txt")); Environment.ExitCode = 1; return; }
            await Say(stderr, "error: unexpected arguments: " + string.Join(' ', args) + "\n"); Environment.ExitCode = 2; return;
        }
        if (provider is "gemini" or "pi" or "opencode")
        {
            if (provider == "gemini") await Say(stderr, "Fetching available models...\n");
            var replaced = Path.Combine(state, name + "-output");
            await Say(stdout, File.Exists(replaced) ? await File.ReadAllTextAsync(replaced) : Fixture("model-list-" + name + ".txt"));
            return;
        }
        var lines = Fixture("model-list-" + provider + ".jsonl").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        static string WithId(string line, JsonNode? id) { var node = JsonNode.Parse(line)!.AsObject(); node["id"] = id?.DeepClone(); return node.ToJsonString(); }
        while (await Console.In.ReadLineAsync() is { } line)
        {
            var request = JsonNode.Parse(line)!.AsObject();
            if (provider == "claude")
            {
                if ((string?)request["type"] != "control_request") continue;
                await Say(stdout, lines[0] + "\n" + lines[1].Replace("REQUEST_ID", (string?)request["request_id"], StringComparison.Ordinal) + "\n");
            }
            else if ((string?)request["method"] == "initialize") await Say(stdout, WithId(lines[0], request["id"]) + "\n" + lines[1] + "\n");
            else if ((string?)request["method"] == "model/list") await Say(stdout, WithId(lines[2], request["id"]) + "\n");
        }
    }

    internal static Task AFakeCliGivesItsFixtureList() => Korean(async () =>
    {
        var expected = new Dictionary<string, string> { ["claude"] = "fable", ["codex"] = "gpt-6.1-sol", ["gemini"] = "gemini-4-carbon-medium", ["pi"] = "anthropic/claude-fable-5-2", ["opencode"] = "opencode/big-pickle" };
        using var fake = new FakeListClis();
        await using var catalog = fake.Catalog();
        foreach (var provider in Wire.Providers.Where(Verified.ContainsKey))
        {
            var runtime = await catalog.RefreshProviderAsync(provider, fake.State);
            Check(runtime.ModelCatalog.IsFresh, provider + ": " + runtime.ModelCatalog.ReadFailure);
            Check(fake.Arguments(provider)?.SequenceEqual(Verified[provider]) == true, provider + " was run with " + string.Join(' ', fake.Arguments(provider) ?? []));
            Check(runtime.ModelCatalog.Models.Any(m => m.Value == expected[provider]), provider + " lists " + expected[provider]);
        }
        var environment = JsonSerializer.Deserialize<Dictionary<string, string?>>(await File.ReadAllTextAsync(Path.Combine(fake.State, "claude-env.json")))!;
        Check(environment["CLAUDE_CODE_SAFE_MODE"] == "1" && (environment["DISABLE_TELEMETRY"] is null || CliEnvironment.Current().ContainsKey("DISABLE_TELEMETRY")), "Claude's read is quiet and lets it fetch the current models");
    });

    internal static async Task AgyRefusesTheOpencodeFlagAndItsOwnReadLists()
    {
        using var fake = new FakeListClis();
        // What the Mac ran before its fix, answered as the real agy 1.5.5 answers it.
        var command = fake.Command("gemini");
        await using (var child = ChildProcess.Start(ChildProcess.StartInfo(command.Binary, command.Prefix.Concat(["models", "--standalone"]), fake.State)))
        {
            child.Input.Close();
            var errors = await child.Error.ReadToEndAsync(); await child.Output.ReadToEndAsync();
            Check(await child.Completion == 1 && errors.Contains("flags provided but not defined: -standalone", StringComparison.Ordinal), "agy refuses --standalone: " + errors);
        }
        Check(Json(await ProviderCatalog.ReadAntigravityModelsAsync(command, workingDirectory: fake.State)) == Json(ProviderCatalog.AntigravityCatalog(Fixture("model-list-agy.txt"))), "agy models gives the recorded list");
    }

    // ── A read that fails keeps the last list ──────────────────────────────────

    internal static Task AFailedReadKeepsTheLastListMarkedAndARestartStillHasIt() => Korean(async () =>
    {
        using var fake = new FakeListClis();
        var storage = Path.Combine(fake.State, "storage"); var workspace = Path.Combine(fake.State, "site"); Directory.CreateDirectory(workspace);
        var file = Path.Combine(storage, KeptModelLists.FileName);
        const string signedOut = "Error: authentication required. Run 'agy' to log in, then retry.\n";
        ModelCatalog fresh, kept;
        await using (var catalog = fake.Catalog(storage))
        {
            fresh = (await catalog.RefreshProviderAsync("gemini", workspace)).ModelCatalog;
            Check(fresh.IsFresh && fresh.Notice() is null, "a list the CLI just gave needs no line");
            Check(KeptModelLists.Load(file).List("gemini", workspace) is { } saved && Json(saved.Models) == Json(fresh.Models), "the list is in the file");
            if (!OperatingSystem.IsWindows()) Check(File.GetUnixFileMode(file) == (UnixFileMode.UserRead | UnixFileMode.UserWrite), "readable by this user only");

            fake.Fail("gemini", signedOut);
            kept = (await catalog.RefreshProviderAsync("gemini", workspace)).ModelCatalog;
            Check(kept.Source == "cli" && !kept.IsFresh && Json(kept.Models) == Json(fresh.Models) && kept.Detail == fresh.Detail, "the last list stays, marked");
            Check(kept.ReadFailure == "agy models: 종료 코드 1 · Error: authentication required. Run 'agy' to log in, then retry.", "why: " + kept.ReadFailure);
            Check(KeptModelLists.Date(kept.ListedAt) is not null, "when it was read");
            Check(kept.Notice() == new ModelListNotice(Locale.Get("composer.model.listKept", new Dictionary<string, string> { ["age"] = Locale.Get("resume.time.now") }), kept.ReadFailure!), "the menu line: " + kept.Notice());
            // Another workspace that never read a list gets this provider's newest, marked the same way.
            var other = (await catalog.RefreshProviderAsync("gemini", fake.State)).ModelCatalog;
            Check(Json(other.Models) == Json(fresh.Models) && other.ReadFailure is not null, "another workspace gets the newest list");
        }
        // After a restart the read still fails: the list comes back from the file, not the built-in one.
        await using (var restarted = fake.Catalog(storage))
        {
            var restored = (await restarted.RefreshProviderAsync("gemini", workspace)).ModelCatalog;
            Check(restored.Source == "cli" && Json(restored.Models) == Json(fresh.Models) && restored.ReadFailure == kept.ReadFailure, "a restart keeps the list");
            // A list the CLI gives again is fresh again, and only that one.
            fake.Recover("gemini");
            Check((await restarted.RefreshProviderAsync("gemini", workspace)).ModelCatalog.IsFresh, "the next good read is fresh");
        }
        // With nothing kept, the built-in list, and why.
        fake.Fail("gemini", signedOut);
        await using (var empty = fake.Catalog(Path.Combine(fake.State, "empty")))
        {
            var builtIn = (await empty.RefreshProviderAsync("gemini", workspace)).ModelCatalog;
            Check(builtIn.Source == "fallback" && Json(builtIn.Models) == Json(ProviderCatalog.Fallback("gemini").Models) && builtIn.ReadFailure == kept.ReadFailure, "the built-in list with the reason");
            Check(builtIn.Notice() == new ModelListNotice(Locale.Get("composer.model.listFallback"), kept.ReadFailure!), "the built-in list's line");
        }
        // A catalog without a storage folder (the checks) keeps the lists in memory only.
        Check(!File.Exists(Path.Combine(fake.State, KeptModelLists.FileName)), "nothing is written without a storage folder");
    });

    internal static Task AMissingCliOrNoModelsIsAFailedReadToo() => Korean(async () =>
    {
        using var fake = new FakeListClis();
        await using (var missing = new ProviderCatalog((_, _) => Task.FromResult<CliCommand?>(null)))
        {
            var none = (await missing.RefreshProviderAsync("pi", fake.State)).ModelCatalog;
            Check(none.Source == "fallback" && none.ReadFailure == Locale.Get("run.error.cliExecutableMissing", new Dictionary<string, string> { ["name"] = "pi" }), "no CLI: " + none.ReadFailure);
        }
        await using var catalog = fake.Catalog();
        fake.Output("pi", "No models available. Use /login to log into a provider via OAuth or API key. See:\n  /x/docs/providers.md\n");
        var empty = (await catalog.RefreshProviderAsync("pi", fake.State)).ModelCatalog;
        Check(empty.Source == "fallback" && empty.ReadFailure == "pi --list-models: 출력에서 모델을 찾지 못했습니다 · No models available. Use /login to log into a provider via OAuth or API key. See:", "no models: " + empty.ReadFailure);
        fake.Fail("codex", "Error: not logged in\n");
        var codex = (await catalog.RefreshProviderAsync("codex", fake.State)).ModelCatalog;
        Check(codex.Source == "fallback" && codex.ReadFailure == "codex app-server --listen stdio://: 종료 코드 1 · Error: not logged in", "codex ended: " + codex.ReadFailure);
        fake.Fail("claude", "Error: something broke\n");
        var claude = (await catalog.RefreshProviderAsync("claude", fake.State)).ModelCatalog;
        Check(claude.Source == "fallback" && claude.ReadFailure == "claude: 종료 코드 1 · Error: something broke", "claude ended: " + claude.ReadFailure);
    });

    // ── The kept lists file ──────────────────────────────────────────────────

    internal static Task TheKeptListsFileIsBoundedAndReadTolerantly()
    {
        var directory = Verification.Temp();
        try
        {
            var file = Path.Combine(directory, KeptModelLists.FileName);
            Check(KeptModelLists.Load(file).Entries.Count == 0 && KeptModelLists.Load(null).Entries.Count == 0, "no file, no lists");
            File.WriteAllText(file, "not json");
            Check(KeptModelLists.Load(file).Entries.Count == 0, "a broken file reads as none");
            File.WriteAllText(file, """{"version":2,"lists":[]}""");
            Check(KeptModelLists.Load(file).Entries.Count == 0, "another version reads as none");
            var now = KeptModelLists.Timestamp(DateTimeOffset.UtcNow);
            JsonObject Good() => new() { ["provider"] = "gemini", ["workspace"] = "/w", ["listedAt"] = now, ["detail"] = "d", ["models"] = new JsonArray(new JsonObject { ["value"] = "gemini-4-carbon-medium", ["displayName"] = "Carbon", ["description"] = "" }) };
            JsonObject With(Action<JsonObject> change) { var row = Good(); change(row); return row; }
            var rows = new JsonArray(Good(),
                With(row => row["models"] = new JsonArray(new JsonObject { ["value"] = "--bad", ["displayName"] = "x", ["description"] = "" })),
                With(row => row["provider"] = "hermes"), With(row => row["provider"] = "gemini-cli"),
                With(row => { row["listedAt"] = "yesterday"; row["workspace"] = "/x"; }), With(row => row["workspace"] = "w"),
                With(row => row["models"] = new JsonArray(new JsonObject { ["value"] = "gemini-4-argon-low" })),
                "text", new JsonObject { ["provider"] = "pi" });
            File.WriteAllText(file, new JsonObject { ["version"] = 1, ["lists"] = rows }.ToJsonString());
            var loaded = KeptModelLists.Load(file);
            Check(loaded.Entries.Count == 1 && loaded.Entries[0].Models.Select(m => m.Value).SequenceEqual(["gemini-4-carbon-medium"]), "only the entry that checks out: " + loaded.Entries.Count);
            // Over the size bound the file is not read at all.
            File.WriteAllBytes(file, new byte[KeptModelLists.MaximumBytes + 1]);
            Check(KeptModelLists.Load(file).Entries.Count == 0, "an oversized file is not read");

            var lists = new KeptModelLists();
            var start = DateTimeOffset.UtcNow;
            var catalog = new ModelCatalog("cli", [new("m1", "M1", "")], "d");
            var first = lists.Remember(catalog, "pi", "/w", start, null);
            // The same list again soon after: kept in memory with its new time, not written again.
            var again = lists.Remember(catalog, "pi", "/w", start.AddSeconds(10), start);
            Check(first && !again, "a list is written when it is new, not when it is read again unchanged");
            Check(lists.List("pi", "/w")?.ListedAt == KeptModelLists.Timestamp(start.AddSeconds(10)), "with the time of its latest read");
            var later = lists.Remember(catalog, "pi", "/w", start + KeptModelLists.RewriteAge + TimeSpan.FromSeconds(11), start);
            var different = lists.Remember(catalog with { Models = [.. catalog.Models, new("m2", "M2", "")] }, "pi", "/w", start.AddSeconds(400), start.AddSeconds(390));
            Check(later && different, "written again once the file is five minutes old, or when the list changed");
            // Neither a kept nor a built-in list is ever kept.
            var keptAgain = lists.Remember(KeptModelLists.Kept(lists.Entries[0], "r"), "pi", "/w", start, null);
            var builtIn = lists.Remember(ProviderCatalog.Fallback("pi"), "pi", "/v", start, null);
            Check(!keptAgain && !builtIn && lists.Entries.Count == 1, "a kept or built-in list is never kept");
            for (var index = 0; index < 40; index++) lists.Remember(catalog, "codex", "/w" + index, start.AddSeconds(500 + index), null);
            Check(lists.Entries.Count == KeptModelLists.MaximumEntries && lists.Entries[0].Workspace == "/w39", "at most 24, the newest first");
            // The newest of a provider stands in for a workspace that never read one.
            Check(lists.List("codex", "/elsewhere")?.Workspace == "/w39" && lists.List("claude", null) is null, "the provider's newest stands in");
            File.WriteAllBytes(file, lists.Encoded());
            Check(Encoding.UTF8.GetString(KeptModelLists.Load(file).Encoded()) == Encoding.UTF8.GetString(lists.Encoded()), "the file reads back as written");
            return Task.CompletedTask;
        }
        finally { Directory.Delete(directory, true); }
    }

    // ── A kept list is shown, never trusted ─────────────────────────────────

    internal static Task AKeptListRetiresNothing()
    {
        var fresh = new ModelCatalog("cli", [new("default", "Default", ""), new("claude-fable-5-2", "Fable 5.2", "")], "");
        var kept = new ModelCatalog("cli", [new("default", "Default", "")], "", "claude: 종료 코드 1", KeptModelLists.Timestamp(DateTimeOffset.UtcNow));
        Check(!kept.IsFresh && fresh.IsFresh && !ProviderCatalog.Fallback("claude").IsFresh, "only a list the latest read gave is fresh");
        Check(ModelSelectionSupport.Reconcile("claude-fable-5-2", "high", fresh, kept, "claude") == new ModelSelectionResolution("claude-fable-5-2", "high"), "a kept list keeps the pane's choice");
        Check(ModelSelectionSupport.Reconcile("claude-fable-5-2", "high", fresh, kept with { ReadFailure = null }, "claude") == new ModelSelectionResolution("default", "default"), "a fresh list without it retires it");
        Check(!AntigravityMigration.ModelUnavailable("gemini-4-carbon-medium", kept), "a kept list never resets an Antigravity pane");
        Check(AntigravityMigration.ModelUnavailable("gemini-4-carbon-medium", kept with { ReadFailure = null }), "a fresh one does");
        return Task.CompletedTask;
    }

    internal static Task TheMenuLineSaysWhichListItIs() => Korean(() =>
    {
        var now = DateTimeOffset.UtcNow;
        Check(new ModelCatalog("cli", [], "").Notice() is null, "a fresh list: no line");
        // Not read yet (or a read another one replaced): no line.
        Check(ProviderCatalog.Fallback("claude").Notice() is null, "nothing read yet: no line");
        var builtIn = ProviderCatalog.Fallback("claude") with { ReadFailure = "claude: 종료 코드 1" };
        Check(builtIn.Notice(now) == new ModelListNotice("기본 목록 · CLI에서 목록을 읽지 못했습니다", "claude: 종료 코드 1"), "the built-in list: " + builtIn.Notice(now));
        var kept = new ModelCatalog("cli", [], "", "agy models: 종료 코드 1", KeptModelLists.Timestamp(now.AddSeconds(-300)));
        Check(kept.Notice(now) == new ModelListNotice("이전 목록 · 새로 읽지 못했습니다 (5분 전 목록)", "agy models: 종료 코드 1"), "a kept list: " + kept.Notice(now));
        return Task.CompletedTask;
    });

    // ── Reading again ────────────────────────────────────────────────────────

    /// <summary>A sign-in or sign-out drops every list of that provider in hand, whatever its workspace, and only that provider's.</summary>
    internal static async Task ASignInOrSignOutDropsThatProvidersLists()
    {
        var loads = new Dictionary<ModelContext, int>();
        ProviderRuntime Runtime(ModelContext key) => new(key.Provider, key.Provider, true, "1", "", new("cli", [new("default", "Default", ""), new("m" + loads[key], "M", "")], ""), ProviderCatalog.Capabilities(key.Provider));
        var refresh = new ModelCatalogRefresh(key => { lock (loads) loads[key] = loads.GetValueOrDefault(key) + 1; return Task.FromResult(Runtime(key)); }, (_, _, _) => { });
        var here = new ModelContext("a", "/a", "gemini"); var there = new ModelContext("b", "/b", "gemini"); var claude = new ModelContext("a", "/a", "claude");
        foreach (var key in new[] { here, there, claude }) await refresh.Request(key);
        Check(loads.Values.All(count => count == 1) && refresh.IsCurrent(here) && refresh.IsCurrent(there), "each list read once");
        refresh.Invalidate("gemini");
        Check(!refresh.IsCurrent(here) && !refresh.IsCurrent(there) && refresh.IsCurrent(claude), "only that provider's lists are old, in every workspace");
        // A run about to start reads anew instead of using the old list for its minute.
        Check((await refresh.Request(there))?.ModelCatalog.Models[^1].Value == "m2" && (await refresh.Request(claude))?.ModelCatalog.Models[^1].Value == "m1", "the next request reads again");
        refresh.Shutdown();
    }

    // ── The wire ─────────────────────────────────────────────────────────────

    internal static Task TheNewFieldsAreAdditiveOnTheWire()
    {
        var fresh = JsonNode.Parse(JsonSerializer.Serialize(new ModelCatalog("cli", [new("opus", "Opus", "")], ""), Wire.Json))!.AsObject();
        Check(!fresh.ContainsKey("readFailure") && !fresh.ContainsKey("listedAt") && !fresh.ContainsKey("isFresh"), "a fresh list is encoded as before: " + fresh.ToJsonString());
        var kept = new ModelCatalog("cli", [new("opus", "Opus", "")], "d", "claude: 종료 코드 1", "2026-11-11T02:00:00Z");
        var data = JsonSerializer.Serialize(kept, Wire.Json);
        var back = JsonSerializer.Deserialize<ModelCatalog>(data, RemoteWire.Json)!;
        Check(back.ReadFailure == kept.ReadFailure && back.ListedAt == kept.ListedAt && Json(back) == data, "a kept list round-trips with the Mac's names: " + data);
        // An older peer's catalogue (no new keys) reads as fresh.
        var older = JsonSerializer.Deserialize<ModelCatalog>("""{"source":"cli","models":[{"value":"opus","displayName":"Opus","description":""}],"detail":"d"}""", RemoteWire.Json)!;
        Check(older.ReadFailure is null && older.ListedAt is null && older.IsFresh, "an older host's list is fresh");

        // A newer Mac host's /v2/info with a kept list: this client reads and accepts it; the fixture itself (an older host) has neither field.
        var raw = File.ReadAllText(FixturePath(Path.Combine("remote-v2", "info-darwin.json")));
        var plain = JsonSerializer.Deserialize<RemoteWireInfo>(raw, RemoteWire.Json)!;
        Check(plain.Runtime.Providers?.All(p => p.ModelCatalog.ReadFailure is null && p.ModelCatalog.ListedAt is null) == true, "the shared fixture has neither field");
        RemoteWireInfo? Info(string? failure)
        {
            var root = JsonNode.Parse(raw)!.AsObject();
            var catalog = root["runtime"]!["providers"]![0]!["modelCatalog"]!.AsObject();
            catalog["readFailure"] = failure; catalog["listedAt"] = "2026-11-11T02:00:00Z";
            return JsonSerializer.Deserialize<RemoteWireInfo>(root.ToJsonString(), RemoteWire.Json);
        }
        var info = Info("claude: 종료 코드 1");
        RemoteWorkspaceValidation.Info(info);
        Check(info!.Runtime.Providers![0].ModelCatalog.ReadFailure == "claude: 종료 코드 1" && info.Runtime.Providers[0].ModelCatalog.Notice() is not null, "the host's failure reaches this client's menu");
        var refused = false;
        try { RemoteWorkspaceValidation.Info(Info(new string('x', 2_001))); } catch (InvalidDataException) { refused = true; }
        Check(refused, "an overlong reason is refused");
        return Task.CompletedTask;
    }
}
