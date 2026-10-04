using MightyClaude.Core;
using System.Text.Json;

internal static class PhaseModelPreferencesVerification
{
    private static void Check(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
    internal static Task ProviderRowsAndVersionPins()
    {
        var original = new PhaseModelsSnapshot { CodexReviewModel = "review-kept", CodexSubagentDefault = "child-kept", ClaudeMain = "main-kept" };
        var claude = PhaseModelPreferences.ApplyRow("claude", Phase.Execution, "fable", original, PhaseModelSection.SmokeFixtureTools);
        Check(claude.Config.ClaudeMain == "fable" && claude.Config.ClaudeSonnetAlias == "fable" && claude.Config.CodexReviewModel == "review-kept" && claude.Config.CodexSubagentDefault == "child-kept", "Claude row cannot overwrite Codex choices");
        Check(claude.OmcAgents?["executor"] == "fable" && claude.OuroborosKeys?["clarification.default_model"] == "default", "only matching installed external phase knobs change");
        var codex = PhaseModelPreferences.ApplyRow("codex", Phase.Review, "new-review", original, PhaseModelSection.SmokeFixtureTools);
        Check(codex.Config.ClaudeMain == "main-kept" && codex.Config.CodexReviewModel == "new-review" && codex.OuroborosKeys?["evaluation.semantic_model"] == "new-review", "Codex review preserves Claude and applies external review knobs");
        try { PhaseModelPreferences.ApplyRow("codex", Phase.Execution, "ignored", original, new(null, null)); throw new InvalidOperationException("ignored model knob was accepted"); } catch (ArgumentException) { }
        var catalog = new ModelCatalog("fixture", [new("default", "Default", ""), new("sonnet", "Sonnet", "", "claude-sonnet-pinned"), new("custom", "custom", "")], "");
        var options = PhaseModelPreferences.Versions(catalog, [new("custom")], "retired-version");
        Check(options.Select(v => v.Value).SequenceEqual(["sonnet", "claude-sonnet-pinned", "retired-version", "custom"]), "latest aliases, fixed versions, saved retired values and registrations remain separate and deduplicated");
        Check(options[0].Label.Contains(ModelLabel.Option(catalog.Models[1])) && options[0].Label != options[1].Label, "alias is labelled as latest separately from concrete pin");
        var registered = PhaseModelPreferences.Registration(" custom ", [], true, ["xhigh", "low", "xhigh"]);
        Check(registered.Name == "custom" && registered.SupportedEffortLevels!.SequenceEqual(["low", "xhigh"]), "registrations trim and canonicalize levels");
        foreach (var invalid in new[] { "", "default", "bad\"model", "custom" })
            try { PhaseModelPreferences.Registration(invalid, [registered], false, []); throw new InvalidOperationException("invalid registration accepted"); } catch (ArgumentException) { }
        try { PhaseModelPreferences.Registration("effort", [], true, []); throw new InvalidOperationException("empty effort set accepted"); } catch (ArgumentException) { }
        return Task.CompletedTask;
    }

    internal static async Task EffortAndArgumentsReachAllLaunchPaths()
    {
        var config = new PhaseModelsSnapshot { ClaudeMain = "registered-main", ClaudeMainEffort = "max", CodexMainEffort = "high", CodexSubagentEffort = "xhigh", CodexSubagentDefault = "child", CodexReviewModel = "review" };
        var req = new StartRunRequest("session", "workspace", "claude", "hello", [new("registered-main", true, ["max", "low"])]) { PhaseModels = config };
        var resolved = PhaseModelPreferences.ResolveEffort(req, ProviderCatalog.Fallback("claude"));
        Check(resolved.Settings?.Effort == "max" && req.Settings is null && PhaseModelPreferences.EffectiveModel(req) == "registered-main", "phase effort uses effective main model without changing saved pane settings");
        var explicitPane = req with { Settings = new(Effort: "low") };
        Check(PhaseModelPreferences.ResolveEffort(explicitPane, ProviderCatalog.Fallback("claude")).Settings?.Effort == "low", "explicit pane effort wins");
        Check(PhaseModelPreferences.ResolveEffort(req with { Model = "haiku" }, ProviderCatalog.Fallback("claude")).Settings is null, "unsupported inherited effort falls back to CLI default");
        var args = ProviderCatalog.Arguments(resolved, "plugin");
        Check(args[args.IndexOf("--model") + 1] == "registered-main" && args[args.IndexOf("--effort") + 1] == "max" && args.Count(a => a == "--settings") == 1, "effective main and effort reach merged Claude invocation");
        var resumed = ProviderCatalog.Arguments(req with { PhaseModels = null, ResumeId = "previous" }, "plugin");
        Check(resumed[resumed.IndexOf("--model") + 1] == "default", "resumed Claude resets a previously pinned model when user selects default");
        var codex = req with { Provider = "codex", Model = "codex-fixture", RegisteredModels = [new("codex-fixture", true, ["high"])] };
        codex = PhaseModelPreferences.ResolveEffort(codex, ProviderCatalog.Fallback("codex"));
        var codexArgs = ProviderCatalog.Arguments(codex, "plugin");
        Check(codexArgs.Contains("model_reasoning_effort=\"high\"") && codexArgs.Contains("agents.default_subagent_reasoning_effort=\"xhigh\"") && codexArgs.Contains("review_model=\"review\""), "Codex main/review/subagent settings reach exec");
        var approvalArgs = ProviderCatalog.Arguments(codex with { Settings = codex.Settings! with { PermissionMode = "onRequest" } }, "plugin");
        Check(approvalArgs.Contains("app-server") && approvalArgs.Contains("agents.default_subagent_reasoning_effort=\"xhigh\""), "approval app-server receives phase settings too");
        var attachment = new RunAttachment("attachment", "fixture.txt", "text/plain", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("fixture")));
        await using var staged = await StagedAttachments.CreateAsync([attachment], CancellationToken.None);
        var attachedArgs = staged.ArgumentsFor(resolved with { Attachments = [attachment] }, "plugin");
        Check(attachedArgs.Contains("registered-main") && attachedArgs.Contains("max") && attachedArgs.Contains("--input-format"), "attachment path retains exact phase snapshot and reasoning");
    }

    internal static async Task PersistenceAndLocalStartSnapshot()
    {
        var config = new PhaseModelsSnapshot { ClaudeMainEffort = "max", CodexMainEffort = "high", CodexSubagentEffort = "xhigh" };
        var roundTrip = JsonSerializer.Deserialize<AppSnapshot>(JsonSerializer.Serialize(new AppSnapshot { PhaseModels = config }, Wire.Json), Wire.Json)!;
        Check(roundTrip.PhaseModels == config && JsonSerializer.Serialize(config, Wire.Json).Contains("codexSubagentEffort"), "optional effort fields use Mac wire names and survive restart");
        var unsafeConfig = PhaseModelPreferences.Normalize(config with { ClaudeMain = "bad\"injection", CodexSubagentEffort = "high\"injection" })!;
        Check(unsafeConfig.ClaudeMain == "default" && unsafeConfig.CodexSubagentEffort is null, "persisted malformed config cannot enter CLI config strings");
        var folder = Path.Combine(Path.GetTempPath(), "phase-local-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            await using var service = new DesktopService(folder, null, "unused", new ProviderCatalog((_, _) => Task.FromResult<CliCommand?>(null)));
            await service.InitializeAsync();
            var workspace = await service.AddWorkspaceAsync(folder);
            var pane = new RunSession { Id = "phase-pane", WorkspaceId = workspace.Id, Kind = "claude" };
            await service.UpdateAsync(s => s with { Sessions = [pane], PhaseModels = config });
            StartRunRequest? accepted = null;
            service.RequestStarting += request => { accepted = request; throw new InvalidOperationException("fixture stops before process creation"); };
            try { await service.StartAsync(new(pane.Id, workspace.Id, "claude", "hello", []) { PhaseModels = new() { ClaudeMain = "untrusted" } }); } catch (InvalidOperationException) { }
            Check(accepted?.PhaseModels == config, "all local/mobile starts snapshot application phase preferences and ignore caller override");
            var wireRequest = JsonSerializer.Deserialize<StartRunRequest>("{\"sessionId\":\"pane\",\"workspaceId\":\"workspace\",\"kind\":\"claude\",\"input\":\"hello\",\"registeredModels\":[],\"phaseModels\":{\"claudeMain\":\"untrusted\"}}", Wire.Json);
            Check(wireRequest?.PhaseModels is null, "remote JSON cannot inject phase configuration");
        }
        finally { Directory.Delete(folder, true); }
    }
}
