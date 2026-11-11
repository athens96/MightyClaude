using System.Text.Json;
using System.Text.Json.Nodes;
using MightyCode.Core;

/// <summary>
/// The efforts a model takes are what its CLI's own list says (M/MightyCore/Tests ModelEffortTests, native/contracts/fixtures/model-list-*):
/// Claude Code 2.1.322 names low to max for Haiku 5.5 and nothing for Haiku 4.5, and a Haiku model no list speaks for keeps the old rule, no
/// effort. Codex 0.167.0 names <c>ultra</c> for six of its nine models, which only Codex is ever offered.
/// </summary>
internal static class ModelEffortVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static string Fixture(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "native", "contracts", "fixtures", name))) directory = directory.Parent;
        return File.ReadAllText(Path.Combine(directory?.FullName ?? throw new InvalidOperationException("the repository root was not found"), "native", "contracts", "fixtures", name));
    }
    internal static ModelCatalog ClaudeCatalog()
    {
        using var initialize = JsonDocument.Parse(Fixture("model-list-claude.jsonl").Split('\n', StringSplitOptions.RemoveEmptyEntries)[1]);
        return ProviderCatalog.ClaudeCatalog(initialize.RootElement.GetProperty("response").GetProperty("response").GetProperty("models"));
    }
    internal static ModelCatalog CodexCatalog()
    {
        using var answer = JsonDocument.Parse(Fixture("model-list-codex.jsonl").Split('\n', StringSplitOptions.RemoveEmptyEntries).Single(line => line.StartsWith("{\"id\":2,", StringComparison.Ordinal)));
        return ProviderCatalog.CodexCatalog(answer.RootElement.GetProperty("result").GetProperty("data"));
    }
    private static ModelCatalog Rows(string provider, string json)
    {
        using var rows = JsonDocument.Parse(json);
        return provider == "codex" ? ProviderCatalog.CodexCatalog(rows.RootElement) : ProviderCatalog.ClaudeCatalog(rows.RootElement);
    }

    internal static Task Haiku55TakesTheLevelsClaudeCodeNamesAndHaiku45None()
    {
        var catalog = ClaudeCatalog();
        var haiku = catalog.Models.First(m => m.Value == "haiku");
        Check(haiku.ResolvedModel == "claude-haiku-5-5" && haiku.SupportsEffort == true && haiku.SupportedEffortLevels?.SequenceEqual(Wire.Efforts) == true, "Haiku 5.5's levels from the list");
        foreach (var model in new[] { "haiku", "claude-haiku-5-5" }) Check(ProviderCatalog.Efforts("claude", model, catalog).SequenceEqual(Wire.Efforts), model);
        // Haiku 4.5 is listed without a word on effort: the old rule.
        var older = catalog.Models.First(m => m.Value == "claude-haiku-4-5-20251001");
        Check(older.SupportsEffort == false && older.SupportedEffortLevels is { Length: 0 } && ProviderCatalog.Efforts("claude", older.Value, catalog).Length == 0, "Haiku 4.5 takes none");
        // The built-in list, or a list that does not name it: the old rule.
        var elsewhere = new ModelCatalog("cli", [new("opus", "Opus", "")], "");
        foreach (var list in new[] { ProviderCatalog.Fallback("claude"), elsewhere }) Check(ProviderCatalog.Efforts("claude", "haiku", list).Length == 0, "no list speaks for haiku");
        // A list that says a Haiku model takes an effort without naming the levels is believed; one that says no is too.
        var unnamed = Rows("claude", """[{"value":"claude-haiku-6","supportsEffort":true}]""");
        Check(unnamed.Models[^1].SupportsEffort == true && ProviderCatalog.Efforts("claude", "claude-haiku-6", unnamed).SequenceEqual(["low", "medium", "high"]), "an unnamed yes");
        Check(ProviderCatalog.Efforts("claude", "claude-haiku-6", Rows("claude", """[{"value":"claude-haiku-6","supportsEffort":false}]""")).Length == 0, "a no");
        // A model the user registered says for itself, Haiku or not.
        var registered = new List<RegisteredModelEntry> { new("claude-haiku-7", true, ["low", "high"]) };
        Check(ProviderCatalog.Efforts("claude", "claude-haiku-7", elsewhere, registered).SequenceEqual(["low", "high"]), "a registered model");
        return Task.CompletedTask;
    }

    internal static Task AHaikuRunWithAnEffortIsCheckedAgainstTheCLIsList()
    {
        var saved = Locale.LanguagePreference; Locale.LanguagePreference = "ko"; Locale.ResetCache();
        try
        {
            var catalog = ClaudeCatalog();
            // No rule by name before the run: its list decides.
            new StartRunRequest("pane", "space", "claude", "Reply with ok.", [], "haiku", "claude", new RunSettings(Effort: "max")).Validate();
            foreach (var model in new[] { "haiku", "claude-haiku-5-5" }) Check(ProviderCatalog.EffortRefusal("claude", model, "max", catalog, null) is null, model + " takes max");
            // A Haiku model the CLI says nothing about takes none, refused in Haiku's words.
            Check(ProviderCatalog.EffortRefusal("claude", "claude-haiku-4-5-20251001", "high", catalog, null) == Locale.Get("validation.run.haikuEffort"), "Haiku 4.5");
            Check(ProviderCatalog.EffortRefusal("claude", "haiku", "high", ProviderCatalog.Fallback("claude"), null) == Locale.Get("validation.run.haikuEffort"), "Haiku on the built-in list");
            // A level its list does not name is an unknown one, not a Haiku refusal.
            Check(ProviderCatalog.EffortRefusal("claude", "haiku", "max", Rows("claude", """[{"value":"haiku","supportsEffort":true,"supportedEffortLevels":["low"]}]"""), null) == Locale.Get("run.error.effortNotSupported"), "max on a list of low");
            // The run passes the effort as it does any Claude model's.
            var arguments = ProviderCatalog.Arguments(new StartRunRequest("pane", "space", "claude", "Reply with ok.", [], "haiku", "claude", new RunSettings(Effort: "max")), "/tmp/plugin");
            Check(arguments[arguments.IndexOf("--effort") + 1] == "max" && JsonNode.Parse(arguments[arguments.IndexOf("--settings") + 1])!["env"]!["CLAUDE_CODE_EFFORT_LEVEL"]!.GetValue<string>() == "max", "--effort max");
            // A fresh list keeps a Haiku pane's effort; Haiku 4.5's goes back to the CLI's default.
            Check(ModelSelectionSupport.Reconcile("haiku", "max", null, catalog, "claude") == new ModelSelectionResolution("haiku", "max"), "haiku keeps max");
            Check(ModelSelectionSupport.Reconcile("claude-haiku-4-5-20251001", "max", null, catalog, "claude") == new ModelSelectionResolution("claude-haiku-4-5-20251001", "default"), "Haiku 4.5 drops it");
            return Task.CompletedTask;
        }
        finally { Locale.LanguagePreference = saved; Locale.ResetCache(); }
    }

    internal static Task CodexsListKeepsUltraForTheModelsThatNameIt()
    {
        var catalog = CodexCatalog();
        Check(catalog.Models.Skip(1).Where(m => m.SupportedEffortLevels?.Contains("ultra") == true).Select(m => m.Value).SequenceEqual(["gpt-6.1-sol", "gpt-6-astra", "gpt-6-sol", "gpt-5.6-sol", "gpt-6.1-terra", "gpt-5.6-terra"]),
            "the six that name ultra");
        Check(ProviderCatalog.Efforts("codex", "gpt-6.1-sol", catalog).SequenceEqual(["low", "medium", "high", "xhigh", "max", "ultra"]), "gpt-6.1-sol's levels");
        // The default stands for gpt-6.1-sol, and takes what it takes.
        Check(catalog.Models[0].ResolvedModel == "gpt-6.1-sol" && ProviderCatalog.Efforts("codex", "default", catalog)[^1] == "ultra", "the default");
        Check(ProviderCatalog.Efforts("codex", "gpt-6-luna", catalog).SequenceEqual(Wire.Efforts) && ProviderCatalog.Efforts("codex", "gpt-5.5", catalog).SequenceEqual(["low", "medium", "high", "xhigh"]), "the others are not offered it");
        // An effort no agent knows is still left out.
        Check(Rows("codex", """[{"model":"gpt-7","supportedReasoningEfforts":[{"reasoningEffort":"ultra"},{"reasoningEffort":"persistent"}]}]""").Models[^1].SupportedEffortLevels?.SequenceEqual(["ultra"]) == true, "persistent left out");
        return Task.CompletedTask;
    }

    internal static Task NoOtherAgentIsOfferedUltra()
    {
        // A Claude list that named it would still not bring it.
        Check(Rows("claude", """[{"value":"opus","supportsEffort":true,"supportedEffortLevels":["high","max","ultra"]}]""").Models[^1].SupportedEffortLevels?.SequenceEqual(["high", "max"]) == true, "Claude's list never offers ultra");
        var named = new ModelCatalog("cli", [new("m", "M", "", null, true, Wire.CodexEfforts)], "");
        foreach (var provider in new[] { "claude", "gemini", "pi" })
        {
            foreach (var model in new[] { "default", "m", "opus" }) Check(!ProviderCatalog.Efforts(provider, model, named).Contains("ultra"), provider + " " + model);
            Check(!Wire.RunEfforts(provider).Contains("ultra"), provider + "'s run efforts");
            var refused = false;
            try { new StartRunRequest("pane", "space", "claude", "x", [], Provider: provider, Settings: new RunSettings(Effort: "ultra")).Validate(); } catch (ArgumentException) { refused = true; }
            Check(refused, provider + " refuses a run set to ultra");
            Check(ProviderCatalog.NormalizeSettings(provider, new RunSettings(Effort: "ultra")).Effort == "default", provider + " saved ultra goes back to the default");
        }
        Check(Wire.RunEfforts("codex").SequenceEqual(Wire.CodexEfforts) && ProviderCatalog.NormalizeSettings("codex", new RunSettings(Effort: "ultra")).Effort == "ultra", "a Codex pane keeps it");
        // Settings › Models offers it on no knob: a host refuses a run whose knobs carry it, as an older Mac or Windows host would.
        Check(!RemoteWorkspaceValidation.PhaseModels(new PhaseModelsSnapshot { CodexMainEffort = "ultra" }) && !RemoteWorkspaceValidation.PhaseModels(new PhaseModelsSnapshot { CodexPlanModeReasoningEffort = "ultra" }), "no phase knob takes ultra");
        return Task.CompletedTask;
    }

    internal static Task ACodexRunPassesUltraAsItPassesEveryEffort()
    {
        var catalog = CodexCatalog();
        var request = new StartRunRequest("pane", "space", "claude", "Reply with ok.", [], "gpt-6.1-sol", "codex", new RunSettings(Effort: "ultra"));
        Check(ProviderCatalog.EffortRefusal("codex", "gpt-6.1-sol", "ultra", catalog, null) is null, "gpt-6.1-sol takes ultra");
        var exec = ProviderCatalog.Arguments(request, "/tmp/plugin");
        Check(exec.SequenceEqual(["-c", "approval_policy=\"never\"", "-c", "sandbox_mode=\"read-only\"", "-c", "sandbox_workspace_write.network_access=false", "-c", "features.fast_mode=false",
            "-c", "service_tier=\"default\"", "-c", "model_reasoning_effort=\"ultra\"", "exec", "--json", "--skip-git-repo-check", "--model", "gpt-6.1-sol", "-"]), "codex exec: " + string.Join(' ', exec));
        Check(ProviderCatalog.EffortRefusal("codex", "gpt-6-luna", "ultra", catalog, null) is not null, "a model that does not name it refuses it");
        var server = ProviderCatalog.Arguments(request with { Settings = request.Settings! with { PermissionMode = "onRequest" } }, "/tmp/plugin");
        Check(server.Contains("model_reasoning_effort=\"ultra\"") && server.TakeLast(3).SequenceEqual(["app-server", "--listen", "stdio://"]), "the app-server: " + string.Join(' ', server));
        return Task.CompletedTask;
    }

    internal static Task UltraIsNamedInEveryLanguageAndReachesTheStatusLine()
    {
        var saved = Locale.LanguagePreference;
        try
        {
            foreach (var (language, name) in new[] { ("ko", "Ultra · 자동 위임"), ("en", "Ultra · auto-delegation"), ("ja", "Ultra · 自動委任"), ("zh", "Ultra · 自动委派") })
            {
                Locale.LanguagePreference = language; Locale.ResetCache();
                Check(Locale.Get("composer.effort.ultra") == name && MobileRemoteSupport.EffortLabel("ultra") == name, language);
            }
        }
        finally { Locale.LanguagePreference = saved; Locale.ResetCache(); }
        StatusLineContext Context(string effort) => new("s", "/w", "/w", "gpt-6.1-sol", "GPT-6.1-Sol", "0.167.0", null, null, null, null, null, null, null, null, null, effort, false, null, null, null, null);
        Check(JsonNode.Parse(StatusLineSupport.BuildPayload(Context("ultra")))!["effort"]?["level"]?.GetValue<string>() == "ultra", "the status line passes ultra");
        Check(JsonNode.Parse(StatusLineSupport.BuildPayload(Context("persistent")))!["effort"] is null, "and nothing it does not know");
        return Task.CompletedTask;
    }
}

/// <summary>
/// A remote workspace's host info carries each provider's model list (M/MightyCore/Tests RemoteModelListBoundTests): the host sends, and the client
/// keeps, each list's first models up to what this app's own read of that CLI keeps, so one long list never drops the host.
/// </summary>
internal static class RemoteModelListBoundVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static JsonObject Catalog(string prefix, int count)
    {
        var models = new JsonArray(new JsonObject { ["value"] = "default", ["displayName"] = "Default", ["description"] = "" });
        for (var index = 1; index < count; index++) models.Add(new JsonObject { ["value"] = prefix + index, ["displayName"] = prefix + index, ["description"] = "row " + index });
        return new() { ["source"] = "cli", ["models"] = models, ["detail"] = prefix + " list" };
    }
    private static JsonObject Provider(string id, JsonObject catalog) => new()
    {
        ["id"] = id, ["name"] = id, ["available"] = true, ["detail"] = "", ["modelCatalog"] = catalog,
        ["capabilities"] = new JsonObject { ["effort"] = true, ["permissionModes"] = new JsonArray("manual", "fullAccess"), ["maxTurns"] = false, ["maxBudgetUsd"] = false, ["resume"] = true },
    };
    /// <summary>info-darwin.json as an older Mac host sends it with long lists: Claude's (both fields) 140, Codex's 200 and pi's <paramref name="pi"/>.</summary>
    private static RemoteWireInfo OlderHostInfo(int pi = 600)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "native", "contracts", "fixtures", "remote-v2", "info-darwin.json"))) directory = directory.Parent;
        var root = JsonNode.Parse(File.ReadAllText(Path.Combine(directory!.FullName, "native", "contracts", "fixtures", "remote-v2", "info-darwin.json")))!.AsObject();
        var runtime = root["runtime"]!.AsObject(); var providers = runtime["providers"]!.AsArray();
        runtime["modelCatalog"] = Catalog("claude-m", 140); providers[0]!["modelCatalog"] = Catalog("claude-m", 140);
        providers[1]!["modelCatalog"] = Catalog("gpt-m", 200);
        providers.Add(Provider("pi", Catalog("anthropic/m", pi)));
        return JsonSerializer.Deserialize<RemoteWireInfo>(root.ToJsonString(), RemoteWire.Json)!;
    }
    private static Dictionary<string, int> Counts(RuntimeInfo runtime) => runtime.Providers.ToDictionary(p => p.Id, p => p.ModelCatalog.Models.Count);

    internal static Task AnOlderHostsLongListsAreCutToWhatALocalReadKeeps()
    {
        var info = OlderHostInfo();
        var refused = false;
        try { RemoteWorkspaceValidation.Info(info); } catch (InvalidDataException) { refused = true; }
        Check(refused, "unbounded, the info does not pass: the client cuts first");
        var kept = info with { Runtime = RemoteWorkspaceValidation.Bounded(info.Runtime) };
        RemoteWorkspaceValidation.Info(kept);
        var counts = Counts(kept.Runtime);
        Check(counts["claude"] == 129 && counts["codex"] == 129 && counts["gemini"] == 1 && counts["pi"] == 513 && kept.Runtime.ModelCatalog?.Models.Count == 129,
            "each list cut to its bound: " + string.Join(", ", counts.Select(c => c.Key + "=" + c.Value)));
        var pi = kept.Runtime.Providers.Single(p => p.Id == "pi").ModelCatalog;
        Check(pi.Models.Select(m => m.Value).SequenceEqual(["default", .. Enumerable.Range(1, 512).Select(i => "anthropic/m" + i)]) && pi.Detail == "anthropic/m list" && pi.Models[^1].Description == "row 512",
            "the first models in the host's order");
        var shorter = OlderHostInfo(300);
        var whole = RemoteWorkspaceValidation.Bounded(shorter.Runtime);
        Check(ReferenceEquals(whole.Providers.Single(p => p.Id == "pi").ModelCatalog, shorter.Runtime.Providers.Single(p => p.Id == "pi").ModelCatalog), "a list within its bound is left whole");
        RemoteWorkspaceValidation.Info(shorter with { Runtime = whole });
        return Task.CompletedTask;
    }

    internal static Task TheBoundIsWhatEachReadOfItsCLIKeeps()
    {
        using var claudeRows = JsonDocument.Parse(JsonSerializer.Serialize(Enumerable.Range(1, 200).Select(i => new { value = "claude-m" + i })));
        using var codexRows = JsonDocument.Parse(JsonSerializer.Serialize(Enumerable.Range(1, 200).Select(i => new { model = "gpt-m" + i })));
        Check(ProviderCatalog.ClaudeCatalog(claudeRows.RootElement).Models.Count == RemoteWorkspaceValidation.MaximumModels("claude"), "claude");
        Check(ProviderCatalog.CodexCatalog(codexRows.RootElement).Models.Count == RemoteWorkspaceValidation.MaximumModels("codex"), "codex");
        Check(ProviderCatalog.AntigravityCatalog(string.Join('\n', Enumerable.Range(1, 200).Select(i => $"gemini-m{i}\tGemini {i}"))).Models.Count == RemoteWorkspaceValidation.MaximumModels("gemini"), "gemini");
        Check(ProviderCatalog.PiCatalog(string.Join('\n', Enumerable.Range(1, 600).Select(i => $"anthropic m{i} 200K 64K yes yes"))).Models.Count == RemoteWorkspaceValidation.MaximumModels("pi"), "pi");
        // A kept list is read back to the longest of them, whatever its provider: the host cuts it to its own.
        Check(KeptModelLists.MaximumModels == RemoteWorkspaceValidation.MaximumModels("opencode"), "kept lists");
        return Task.CompletedTask;
    }
}
