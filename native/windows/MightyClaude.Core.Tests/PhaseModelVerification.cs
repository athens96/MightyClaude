using System.Text.Json;
using MightyClaude.Core;

internal static class PhaseModelVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static string Temp() { var path = Path.Combine(Path.GetTempPath(), "pm-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }

    // 1. Phase → knob mapping matches macOS PhaseModelRouting
    internal static Task RowRuleMatchesMacOS()
    {
        var c = new PhaseModelsSnapshot();

        // Claude
        var cp = PhaseModelRouting.ApplyClaudeRow(Phase.Planning, "opus-x", c);
        Check(cp.ClaudeOpusAlias == "opus-x" && cp.ClaudeMain == "default", "claude planning → opusAlias only");

        var ce = PhaseModelRouting.ApplyClaudeRow(Phase.Execution, "main-x", c);
        Check(ce.ClaudeMain == "main-x" && ce.ClaudeSonnetAlias == "main-x", "claude execution → main + sonnetAlias");

        var cr = PhaseModelRouting.ApplyClaudeRow(Phase.Review, "r", c);
        Check(cr == c, "claude review → nothing changes");

        var cs = PhaseModelRouting.ApplyClaudeRow(Phase.Subagents, "sub-x", c);
        Check(cs.ClaudeSubagentDefault == "sub-x", "claude subagents → subagentDefault");

        // Codex
        var drev = PhaseModelRouting.ApplyCodexRow(Phase.Review, "rev-x", c);
        Check(drev.CodexReviewModel == "rev-x", "codex review → reviewModel");

        var dsub = PhaseModelRouting.ApplyCodexRow(Phase.Subagents, "dsub-x", c);
        Check(dsub.CodexSubagentDefault == "dsub-x", "codex subagents → subagentDefault");

        var dplan = PhaseModelRouting.ApplyCodexRow(Phase.Planning, "p", c);
        Check(dplan == c, "codex planning → nothing changes");

        var dexec = PhaseModelRouting.ApplyCodexRow(Phase.Execution, "e", c);
        Check(dexec == c, "codex execution → nothing changes");

        // RowState: uniform when all mapped knobs share a value
        var uniform = c with { ClaudeMain = "m", ClaudeSonnetAlias = "m" };
        var execState = PhaseModelRouting.ClaudeRowState(Phase.Execution, uniform);
        Check(execState!.IsUniform && execState.Value == "m", "uniform row state");

        var mixed = c with { ClaudeMain = "a", ClaudeSonnetAlias = "b" };
        var mixedState = PhaseModelRouting.ClaudeRowState(Phase.Execution, mixed);
        Check(!mixedState!.IsUniform, "mixed row state");

        // Claude review → null (no single-model knob)
        Check(PhaseModelRouting.ClaudeRowState(Phase.Review, c) is null, "claude review row state is null");
        // Codex planning → null
        Check(PhaseModelRouting.CodexRowState(Phase.Planning, c) is null, "codex planning row state is null");

        // omc phase mapping
        Check(PhaseModelRouting.OmcAgentPhase("planner") == Phase.Planning, "planner → planning");
        Check(PhaseModelRouting.OmcAgentPhase("architect") == Phase.Planning, "architect → planning");
        Check(PhaseModelRouting.OmcAgentPhase("critic") == Phase.Planning, "critic → planning");
        Check(PhaseModelRouting.OmcAgentPhase("executor") == Phase.Execution, "executor → execution");
        Check(PhaseModelRouting.OmcAgentPhase("codeReviewer") == Phase.Review, "codeReviewer → review");
        Check(PhaseModelRouting.OmcAgentPhase("verifier") == Phase.Review, "verifier → review");
        Check(PhaseModelRouting.OmcAgentPhase("other") is null, "unknown omc key → nil");

        // Ouroboros phase mapping
        Check(PhaseModelRouting.OuroborosKeyPhase("clarification.default_model") == Phase.Planning, "clarification → planning");
        Check(PhaseModelRouting.OuroborosKeyPhase("evaluation.semantic_model") == Phase.Review, "evaluation → review");
        Check(PhaseModelRouting.OuroborosKeyPhase("consensus.judge_model") == Phase.Review, "consensus → review");
        Check(PhaseModelRouting.OuroborosKeyPhase("llm.qa_model") == Phase.Review, "llm.qa → review");
        Check(PhaseModelRouting.OuroborosKeyPhase("orchestrator.cli_path") is null, "orchestrator path → nil");

        // omc row state absent when section is nil
        Check(PhaseModelRouting.OmcRowState(Phase.Planning, null) is null, "omc nil → null row state");
        Check(PhaseModelRouting.OuroborosRowState(Phase.Review, null) is null, "ouroboros nil → null row state");

        // omc row state mixed
        var omcAgents = new Dictionary<string, string> { ["planner"] = "a", ["architect"] = "b", ["critic"] = "b" };
        var omcState = PhaseModelRouting.OmcRowState(Phase.Planning, omcAgents);
        Check(!omcState!.IsUniform, "omc planning mixed when values differ");

        return Task.CompletedTask;
    }

    // 2. File store keeps unrelated keys
    internal static Task FileStoreKeepsUnrelatedKeys()
    {
        var dir = Temp();
        try
        {
            // Write a config.jsonc with extra keys
            var omcDir = Path.Combine(dir, ".config", "claude-omc");
            Directory.CreateDirectory(omcDir);
            var jsonc = """
                {
                  "otherKey": "preserved",
                  "agents": {
                    "planner": { "model": "old-model", "extraField": "kept" },
                    "other": { "setting": "value" }
                  }
                }
                """;
            File.WriteAllText(Path.Combine(omcDir, "config.jsonc"), jsonc);

            var store = new ModelSettingsFileStore(dir);
            store.SaveOmcAgents(new() { ["planner"] = "new-model" });

            var loaded = store.LoadOmcAgents()!;
            Check(loaded["planner"] == "new-model", "planner model updated");

            // Read back raw JSON to verify unrelated keys survived
            var raw = File.ReadAllText(store.OmcConfigPath);
            Check(raw.Contains("otherKey"), "otherKey must survive");
            Check(raw.Contains("preserved"), "otherKey value must survive");
            Check(raw.Contains("other"), "other agent key must survive");
            Check(raw.Contains("extraField"), "extraField inside planner must survive");

            // Write a config.yaml with orchestrator.cli_path
            var oboDir = Path.Combine(dir, ".ouroboros");
            Directory.CreateDirectory(oboDir);
            var yaml = """
                orchestrator:
                  cli_path: /custom/claude
                clarification:
                  default_model: opus
                llm:
                  qa_model: sonnet
                """;
            File.WriteAllText(Path.Combine(oboDir, "config.yaml"), yaml);

            store.SaveOuroborosKeys(new() { ["clarification.default_model"] = "fable" });

            var raw2 = File.ReadAllText(store.OuroborosConfigPath);
            Check(raw2.Contains("cli_path: /custom/claude"), "orchestrator.cli_path must survive");
            Check(raw2.Contains("default_model: fable"), "default_model updated");
            Check(raw2.Contains("qa_model: sonnet"), "qa_model unchanged");
        }
        finally { Directory.Delete(dir, true); }
        return Task.CompletedTask;
    }

    // 3. Unparseable file is refused; file left byte-identical
    internal static Task RefuseUnparseableFile()
    {
        var dir = Temp();
        try
        {
            var omcDir = Path.Combine(dir, ".config", "claude-omc");
            Directory.CreateDirectory(omcDir);
            var bad = "{ this is not json }";
            var path = Path.Combine(omcDir, "config.jsonc");
            File.WriteAllText(path, bad);

            var store = new ModelSettingsFileStore(dir);
            var threw = false;
            try { store.SaveOmcAgents(new() { ["planner"] = "model" }); }
            catch { threw = true; }
            Check(threw, "SaveOmcAgents must throw on unparseable JSONC");
            Check(File.ReadAllText(path) == bad, "file must be byte-identical after refusal");

            // LoadOmcAgents also throws
            threw = false;
            try { store.LoadOmcAgents(); }
            catch { threw = true; }
            Check(threw, "LoadOmcAgents must throw on unparseable JSONC");
        }
        finally { Directory.Delete(dir, true); }
        return Task.CompletedTask;
    }

    // 4. omc agent catalog: scan agents from config.jsonc
    internal static Task OmcAgentCatalog()
    {
        var dir = Temp();
        try
        {
            var store = new ModelSettingsFileStore(dir);

            // File absent → null
            Check(store.LoadOmcAgents() is null, "absent file → null");

            // File present with some model keys
            var omcDir = Path.Combine(dir, ".config", "claude-omc");
            Directory.CreateDirectory(omcDir);
            var jsonc = """
                {
                  "agents": {
                    "planner": { "model": "fable" },
                    "executor": { "model": "sonnet" },
                    "noModel": { "prompt": "only" },
                    "codeReviewer": { "model": "opus" }
                  }
                }
                """;
            File.WriteAllText(store.OmcConfigPath, jsonc);

            var agents = store.LoadOmcAgents()!;
            Check(agents["planner"] == "fable", "planner model");
            Check(agents["executor"] == "sonnet", "executor model");
            Check(agents["codeReviewer"] == "opus", "codeReviewer model");
            Check(!agents.ContainsKey("noModel"), "entry without model key is absent");

            // Saving "default" removes the model key
            store.SaveOmcAgents(new() { ["planner"] = "default" });
            var after = store.LoadOmcAgents()!;
            Check(!after.ContainsKey("planner"), "default removes model key");
            Check(after["executor"] == "sonnet", "executor unchanged after partial save");
        }
        finally { Directory.Delete(dir, true); }
        return Task.CompletedTask;
    }

    // 5. Launch args merge into a single --settings JSON
    internal static Task LaunchArgsMergeOneSettingsJson()
    {
        // With effort only
        var effortArgs = ProviderCatalog.Arguments(
            new("s", "w", "claude", "x", [], Settings: new("high")),
            "/plugin");
        var settingsCount = effortArgs.Count(a => a == "--settings");
        Check(settingsCount == 1, "effort alone produces one --settings flag");
        Check(effortArgs.Any(a => a.Contains("CLAUDE_CODE_EFFORT_LEVEL")), "effort env key present");

        // With phase models only (no effort)
        var pm = new PhaseModelsSnapshot { ClaudeOpusAlias = "custom-opus", ClaudeSubagentDefault = "custom-sub" };
        var pmArgs = ProviderCatalog.Arguments(
            new("s", "w", "claude", "x", []),
            "/plugin",
            pm);
        var pmSettingsCount = pmArgs.Count(a => a == "--settings");
        Check(pmSettingsCount == 1, "phase models alone produces one --settings flag");
        Check(pmArgs.Any(a => a.Contains("ANTHROPIC_DEFAULT_OPUS_MODEL")), "opus env key present");
        Check(pmArgs.Any(a => a.Contains("CLAUDE_CODE_SUBAGENT_MODEL")), "subagent env key present");
        // sonnet alias is "default" so should NOT appear
        Check(!pmArgs.Any(a => a.Contains("ANTHROPIC_DEFAULT_SONNET_MODEL")), "default sonnet alias omitted");

        // With both effort AND phase models → still exactly one --settings
        var bothArgs = ProviderCatalog.Arguments(
            new("s", "w", "claude", "x", [], Settings: new("high")),
            "/plugin",
            pm);
        var bothCount = bothArgs.Count(a => a == "--settings");
        Check(bothCount == 1, "effort + phase models merges into one --settings flag");
        var settingsJson = bothArgs[bothArgs.IndexOf("--settings") + 1];
        Check(settingsJson.Contains("CLAUDE_CODE_EFFORT_LEVEL"), "effort key in merged JSON");
        Check(settingsJson.Contains("ANTHROPIC_DEFAULT_OPUS_MODEL"), "opus key in merged JSON");

        // claudeMain → --model when session model is default
        var mainPm = new PhaseModelsSnapshot { ClaudeMain = "fable" };
        var mainArgs = ProviderCatalog.Arguments(new("s", "w", "claude", "x", []), "/plugin", mainPm);
        var modelIdx = mainArgs.IndexOf("--model");
        Check(modelIdx >= 0 && mainArgs[modelIdx + 1] == "fable", "claudeMain injects --model");

        // claudeMain NOT injected when session already has a model
        var explicitModel = ProviderCatalog.Arguments(new("s", "w", "claude", "x", [], "opus"), "/plugin", mainPm);
        Check(explicitModel.Count(a => a == "--model") == 1, "session model takes precedence");
        Check(explicitModel[explicitModel.IndexOf("--model") + 1] == "opus", "session model value");

        // Codex phase model args
        var codexPm = new PhaseModelsSnapshot { CodexReviewModel = "review-m", CodexSubagentDefault = "sub-m", CodexPlanModeReasoningEffort = "high" };
        var codexArgs = ProviderCatalog.Arguments(new("s", "w", "claude", "x", [], Provider: "codex"), "/plugin", codexPm);
        Check(codexArgs.Contains("review_model=\"review-m\""), "codex review_model");
        Check(codexArgs.Contains("agents.default_subagent_model=\"sub-m\""), "codex subagent");
        Check(codexArgs.Contains("plan_mode_reasoning_effort=\"high\""), "codex plan effort");
        // No --settings for codex
        Check(!codexArgs.Contains("--settings"), "codex has no --settings flag");

        return Task.CompletedTask;
    }

    // 6. AppSnapshot PhaseModels field uses macOS JSON field names
    internal static Task SnapshotSharesMacOSFieldNames()
    {
        // Default snapshot → phaseModels absent (null)
        var snap = new AppSnapshot();
        Check(snap.PhaseModels is null, "phaseModels defaults to null");
        var json = JsonSerializer.Serialize(snap, Wire.Json);
        Check(!json.Contains("phaseModels"), "null phaseModels omitted from JSON");

        // Snapshot without phaseModels key → loads with all defaults
        var loaded = JsonSerializer.Deserialize<AppSnapshot>("""
            {"version":1,"workspaces":[],"sessions":[]}
            """, Wire.Json)!;
        Check(loaded.PhaseModels is null, "missing phaseModels → null");

        // With PhaseModels set
        var pm = new PhaseModelsSnapshot
        {
            ClaudeMain = "fable",
            ClaudeOpusAlias = "opus-x",
            ClaudeSonnetAlias = "sonnet-x",
            ClaudeHaikuAlias = "haiku-x",
            ClaudeSubagentDefault = "sub-x",
            CodexReviewModel = "rev-x",
            CodexSubagentDefault = "csub-x",
            CodexPlanModeReasoningEffort = "high",
        };
        var snap2 = new AppSnapshot { PhaseModels = pm };
        var json2 = JsonSerializer.Serialize(snap2, Wire.Json);

        // macOS field names in the JSON
        Check(json2.Contains("\"claudeMain\""), "claudeMain field name");
        Check(json2.Contains("\"claudeOpusAlias\""), "claudeOpusAlias field name");
        Check(json2.Contains("\"claudeSonnetAlias\""), "claudeSonnetAlias field name");
        Check(json2.Contains("\"claudeHaikuAlias\""), "claudeHaikuAlias field name");
        Check(json2.Contains("\"claudeSubagentDefault\""), "claudeSubagentDefault field name");
        Check(json2.Contains("\"codexReviewModel\""), "codexReviewModel field name");
        Check(json2.Contains("\"codexSubagentDefault\""), "codexSubagentDefault field name");
        Check(json2.Contains("\"codexPlanModeReasoningEffort\""), "codexPlanModeReasoningEffort field name");

        // Round-trip
        var rt = JsonSerializer.Deserialize<AppSnapshot>(json2, Wire.Json)!;
        Check(rt.PhaseModels?.ClaudeMain == "fable", "claudeMain round-trip");
        Check(rt.PhaseModels?.CodexReviewModel == "rev-x", "codexReviewModel round-trip");
        Check(rt.PhaseModels?.CodexPlanModeReasoningEffort == "high", "codexPlanModeReasoningEffort round-trip");

        // Version stays 1
        Check(snap2.Version == 1, "version stays 1");
        Check(rt.Version == 1, "version stays 1 after round-trip");

        return Task.CompletedTask;
    }
}
