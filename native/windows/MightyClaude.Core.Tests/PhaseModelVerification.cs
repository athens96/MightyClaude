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

            // OmcAgentCatalog against a fixture install
            var catalog = new MightyClaude.Core.OmcAgentCatalog(dir);
            Check(catalog.Scan() is null, "no installed_plugins.json → omc not installed");
            var pluginsDirectory = Path.Combine(dir, ".claude", "plugins");
            Directory.CreateDirectory(pluginsDirectory);
            var install = Path.Combine(dir, "cache", "omc", "oh-my-claudecode", "5.4.0");
            var projectInstall = Path.Combine(dir, "project-omc");
            Directory.CreateDirectory(Path.Combine(projectInstall, "agents"));
            File.WriteAllText(Path.Combine(projectInstall, "agents", "planner.md"), "---\nmodel: haiku\n---\n");
            void WriteInstalled(string records) => File.WriteAllText(catalog.InstalledPluginsPath,
                "{\"version\":2,\"plugins\":{\"other@x\":[],\"oh-my-claudecode@omc\":[" + records + "]}}");
            string Record(string scope, string path) =>
                "{\"scope\":\"" + scope + "\",\"installPath\":" + JsonSerializer.Serialize(path) + "}";

            WriteInstalled(Record("project", projectInstall));
            Check(catalog.Scan() is null, "a project-scope record alone is not an install");
            WriteInstalled(Record("user", install));
            Check(catalog.Scan() is null, "a user record without agents/*.md is not an install");

            Directory.CreateDirectory(Path.Combine(install, "agents"));
            File.WriteAllText(Path.Combine(install, "agents", "code-reviewer.md"), "---\nname: code-reviewer\nmodel: opus\n---\nmodel: ignored\n");
            File.WriteAllText(Path.Combine(install, "agents", "security-reviewer.md"), "---\nmodel: sonnet\n---\n");
            File.WriteAllText(Path.Combine(install, "agents", "verifier.md"), "---\nmodel: sonnet\n---\n");
            File.WriteAllText(Path.Combine(install, "agents", "git-master.md"), "no frontmatter\n");
            File.WriteAllText(Path.Combine(install, "agents", "README.txt"), "not an agent");
            WriteInstalled(Record("project", projectInstall) + "," + Record("user", install));
            var scanned = catalog.Scan();
            Check(scanned is not null, "user record with agents/*.md → omc installed");
            scanned ??= [];
            Check(scanned.Keys.OrderBy(key => key, StringComparer.Ordinal)
                    .SequenceEqual(new[] { "codeReviewer", "gitMaster", "securityReviewer", "verifier" }),
                "agent keys are the file names in omc camelCase: " + string.Join(",", scanned.Keys));
            Check(scanned["codeReviewer"] == "opus", "frontmatter model read, body ignored");
            Check(scanned["verifier"] == "sonnet", "single-word name unchanged");
            Check(scanned["gitMaster"] == "default", "no frontmatter → default");
            Check(MightyClaude.Core.OmcAgentCatalog.KebabToCamelCase("document-specialist") == "documentSpecialist", "kebab → camelCase");
        }
        finally { Directory.Delete(dir, true); }
        return Task.CompletedTask;
    }

    // 4b. A pick writes only the agents it changed; frontmatter defaults never reach config.jsonc
    internal static Task OmcSaveWritesOnlyChangedAgents()
    {
        var dir = Temp();
        try
        {
            var install = Path.Combine(dir, "omc-install");
            Directory.CreateDirectory(Path.Combine(install, "agents"));
            foreach (var (file, model) in new[] { ("planner", "opus"), ("architect", "opus"), ("executor", "sonnet"), ("code-reviewer", "opus"), ("verifier", "sonnet") })
                File.WriteAllText(Path.Combine(install, "agents", file + ".md"), "---\nmodel: " + model + "\n---\n");
            Directory.CreateDirectory(Path.Combine(dir, ".claude", "plugins"));
            File.WriteAllText(Path.Combine(dir, ".claude", "plugins", "installed_plugins.json"),
                "{\"plugins\":{\"oh-my-claudecode@omc\":[{\"scope\":\"user\",\"installPath\":" + JsonSerializer.Serialize(install) + "}]}}");

            var store = new ModelSettingsFileStore(dir);
            Directory.CreateDirectory(Path.GetDirectoryName(store.OmcConfigPath)!);
            File.WriteAllText(store.OmcConfigPath, """
                {
                  // user file
                  "theme": "dark",
                  "agents": { "executor": { "model": "haiku", "prompt": "keep" } }
                }
                """);

            var tools = PhaseModelSection.LoadTools(dir);
            Check(tools.OmcAgents!["executor"] == "haiku", "a configured agent shows its config.jsonc value");
            Check(tools.OmcAgents["planner"] == "default", "an unconfigured agent shows default, not its frontmatter model");

            var saved = PhaseModelSection.SaveTools(tools, PhaseModelSection.ApplyPhaseRow(Phase.Planning, "fable", new(), tools), dir);
            Check(saved.Error is null, "save succeeds");
            var written = store.LoadOmcAgents()!;
            Check(written.Keys.OrderBy(key => key, StringComparer.Ordinal).SequenceEqual(new[] { "architect", "executor", "planner" }),
                "only the changed planning agents were added to config.jsonc: " + string.Join(",", written.Keys));
            Check(written["planner"] == "fable" && written["architect"] == "fable", "planning agents written");
            Check(written["executor"] == "haiku", "an untouched agent keeps its value");
            Check(!written.ContainsKey("codeReviewer") && !written.ContainsKey("verifier"),
                "frontmatter defaults of untouched agents never reach config.jsonc");
            using (var written2 = JsonDocument.Parse(File.ReadAllBytes(store.OmcConfigPath)))
            {
                var root = written2.RootElement;
                Check(root.GetProperty("theme").GetString() == "dark", "unrelated keys survive");
                Check(root.GetProperty("agents").GetProperty("executor").GetProperty("prompt").GetString() == "keep",
                    "unrelated agent fields survive");
            }

            // Picking the value an agent already has writes nothing at all.
            var bytes = File.ReadAllBytes(store.OmcConfigPath);
            var backups = Directory.GetFiles(Path.GetDirectoryName(store.OmcConfigPath)!, "config.mighty-backup-*").Length;
            var same = PhaseModelSection.SaveTools(saved, PhaseModelSection.ApplyKnob("omc.agent.executor", "haiku", new(), saved), dir);
            Check(same.Error is null, "a no-op pick is not an error");
            Check(File.ReadAllBytes(store.OmcConfigPath).SequenceEqual(bytes), "a no-op pick leaves config.jsonc byte-identical");
            Check(Directory.GetFiles(Path.GetDirectoryName(store.OmcConfigPath)!, "config.mighty-backup-*").Length == backups,
                "a no-op pick makes no backup");

            // Changed() is the rule: only keys whose value differs.
            var changed = PhaseModelSection.Changed(
                new() { ["a"] = "x", ["b"] = "y" }, new() { ["a"] = "x", ["b"] = "z", ["c"] = "w" });
            Check(changed.Count == 2 && changed["b"] == "z" && changed["c"] == "w", "Changed keeps only differing keys");
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

    // 7. 화면 등록: phaseModels 칸이 macOS 차례 그대로 remoteConnection과 styles 사이에 있다
    internal static Task SectionRegisteredInMacOrder()
    {
        var ids = SettingsSections.MacOrder.Select(slot => slot.Id).ToArray();
        var index = Array.IndexOf(ids, SettingsSections.PhaseModels);
        Check(index > 0, "phaseModels slot is registered");
        Check(ids[index - 1] == SettingsSections.RemoteConnection, "phaseModels sits after remoteConnection");
        Check(ids[index + 1] == SettingsSections.Styles, "phaseModels sits before styles");

        var slot = SettingsSections.MacOrder[index];
        Check(slot.OnWindows, "phaseModels is shown on Windows");
        Check(slot.WindowsTitle == Locale.Get("settings.phaseModels.sectionTitle"),
            "phaseModels uses the same locale key as macOS");
        Check(SettingsSections.WindowsTitles.Contains(slot.WindowsTitle!),
            "the smoke order includes the phase model heading");

        // 화면 글은 모두 열쇠말에서 온다 — 날 열쇠말이 그대로 보이지 않는다.
        foreach (var text in new[]
                 {
                     PhaseModelSection.SectionTitle, PhaseModelSection.Description,
                     PhaseModelSection.DefaultOption, PhaseModelSection.MixedLabel,
                     PhaseModelSection.PhaseLabel(Phase.Planning), PhaseModelSection.PhaseLabel(Phase.Execution),
                     PhaseModelSection.PhaseLabel(Phase.Review), PhaseModelSection.PhaseLabel(Phase.Subagents),
                     PhaseModelSection.ToolLabel(PhaseModelSection.OmcTool),
                     PhaseModelSection.ToolLabel(PhaseModelSection.OuroborosTool),
                 })
            Check(text.Length > 0 && !text.StartsWith("settings.", StringComparison.Ordinal),
                "screen copy comes from the locale catalogue: " + text);
        return Task.CompletedTask;
    }

    // 8. 화면이 그리는 줄: 네 페이즈 줄, 도구별 자세히 묶음, 설치되지 않은 도구는 빠짐
    internal static Task SectionRowsFollowInstalledTools()
    {
        var dir = Temp();
        try
        {
            // 아무것도 설치되지 않았을 때: omc·Ouroboros 묶음이 없다
            var bare = PhaseModelSection.LoadTools(dir);
            Check(bare.OmcAgents is null, "omc absent → null");
            Check(bare.OuroborosKeys is null, "Ouroboros absent → null");
            Check(bare.Error is null, "nothing to read is not an error");
            var bareBlocks = PhaseModelSection.ToolBlocks(new(), bare);
            Check(bareBlocks.Count == 2, "only Claude and Codex blocks without the other tools");
            Check(bareBlocks.All(block => block.Tool is PhaseModelSection.ClaudeTool or PhaseModelSection.CodexTool),
                "the two blocks are Claude and Codex");

            // omc 설치를 흉내 낸다: installed_plugins.json + agents/*.md frontmatter
            var install = Path.Combine(dir, "cache", "omc", "oh-my-claudecode", "5.4.0");
            Directory.CreateDirectory(Path.Combine(install, "agents"));
            File.WriteAllText(Path.Combine(install, "agents", "code-reviewer.md"), "---\nmodel: opus\n---\nbody\n");
            File.WriteAllText(Path.Combine(install, "agents", "planner.md"), "---\nmodel: fable\n---\nbody\n");
            File.WriteAllText(Path.Combine(install, "agents", "executor.md"), "---\nname: executor\n---\nbody\n");
            var pluginsDirectory = Path.Combine(dir, ".claude", "plugins");
            Directory.CreateDirectory(pluginsDirectory);
            File.WriteAllText(Path.Combine(pluginsDirectory, "installed_plugins.json"),
                "{\"plugins\":{\"oh-my-claudecode@omc\":[{\"scope\":\"user\",\"installPath\":" +
                JsonSerializer.Serialize(install) + "}]}}");

            // Ouroboros 설치를 흉내 낸다
            var ouroborosDirectory = Path.Combine(dir, ".ouroboros");
            Directory.CreateDirectory(ouroborosDirectory);
            File.WriteAllText(Path.Combine(ouroborosDirectory, "config.yaml"),
                "orchestrator:\n  cli_path: /usr/local/bin/claude-nested\nclarification:\n  default_model: sonnet\nconsensus:\n  judge_model: sonnet\n");

            var tools = PhaseModelSection.LoadTools(dir);
            Check(tools.OmcAgents is not null, "omc installed → agent list");
            Check(tools.OmcAgents!["codeReviewer"] == "default", "without config.jsonc the value is default, not the frontmatter model");
            Check(tools.OmcAgents["executor"] == "default", "an agent without frontmatter model shows default");
            Check(tools.OmcDefaults!["codeReviewer"] == "opus", "file name → camelCase key, frontmatter model kept as the displayed default");
            Check(tools.OuroborosKeys is not null, "Ouroboros installed → key list");
            Check(tools.OuroborosKeys!["clarification.default_model"] == "sonnet", "Ouroboros value read");
            Check(!tools.OuroborosKeys.ContainsKey("orchestrator.cli_path"), "only *_model keys are owned");

            var blocks = PhaseModelSection.ToolBlocks(new(), tools);
            Check(blocks.Count == 4, "four blocks when both tools are installed");
            Check(blocks[2].Tool == PhaseModelSection.OmcTool && blocks[3].Tool == PhaseModelSection.OuroborosTool,
                "omc and Ouroboros come after Claude and Codex");
            var reviewerKnob = blocks[2].Knobs.Single(knob => knob.KnobId == "omc.agent.codeReviewer");
            Check(reviewerKnob.Value == "default" && reviewerKnob.Label.Contains("codeReviewer") && reviewerKnob.Label.Contains("opus"),
                "the omc detail row shows the frontmatter model only in its label");
            Check(blocks[2].Knobs.Single(knob => knob.KnobId == "omc.agent.executor").Label == "executor",
                "an agent without frontmatter model is labelled by its key alone");
            Check(blocks[3].Knobs.Any(knob => knob.KnobId == "ouroboros.consensus.judge_model"),
                "the Ouroboros knob id is its dotted key");
            Check(blocks[1].Knobs.Single(knob => knob.KnobId == "codex.codexPlanModeReasoningEffort").IsEffort,
                "plan mode reasoning effort is an effort knob, edited in details only");

            // 페이즈 줄: 모두 default면 한 값, 하나만 다르면 혼합
            var rows = PhaseModelSection.SummaryRows(new(), tools);
            Check(rows.Count == 4, "four phase rows");
            Check(rows.Select(row => row.Phase).SequenceEqual(new[] { Phase.Planning, Phase.Execution, Phase.Review, Phase.Subagents }),
                "phase row order matches macOS");
            Check(!rows.Single(row => row.Phase == Phase.Execution).Mixed && !rows.Single(row => row.Phase == Phase.Subagents).Mixed,
                "rows whose knobs are all default are not mixed");
            Check(rows.Single(row => row.Phase == Phase.Execution).Value == "default", "an all-default row shows default");
            Check(rows.Single(row => row.Phase == Phase.Planning).Mixed,
                "Ouroboros sonnet against Claude default makes the planning row mixed across tools");

            var mixedRows = PhaseModelSection.SummaryRows(new() { ClaudeMain = "a" }, tools);
            Check(mixedRows.Single(row => row.Phase == Phase.Execution).Mixed, "execution row is mixed");
            Check(mixedRows.Single(row => row.Phase == Phase.Execution).Value == PhaseModelSection.MixedSentinel,
                "a mixed row carries the mixed sentinel, never a model name");
            Check(!mixedRows.Single(row => row.Phase == Phase.Subagents).Mixed, "the subagents row is untouched");

            // 페이즈 줄 하나가 네 도구를 모두 건드린다
            var edit = PhaseModelSection.ApplyPhaseRow(Phase.Planning, "fable", new(), tools);
            Check(edit.Config.ClaudeOpusAlias == "fable", "planning → claude opus alias");
            Check(edit.Config.ClaudeMain == "default", "planning does not touch the main model");
            Check(edit.OmcAgents!["planner"] == "fable", "planning → omc planner");
            Check(edit.OmcAgents["codeReviewer"] == "default", "a review agent is untouched by the planning row");
            Check(!edit.OmcAgents.ContainsKey("architect") && !edit.OmcAgents.ContainsKey("critic"),
                "a phase row never adds an agent the install does not have");
            Check(edit.OuroborosKeys!["clarification.default_model"] == "fable", "planning → Ouroboros clarification");
            Check(edit.OuroborosKeys["consensus.judge_model"] == "sonnet", "a review key is untouched by the planning row");

            var reloaded = PhaseModelSection.SaveTools(tools, edit, dir);
            Check(reloaded.Error is null, "writing the two files reports no error");
            Check(reloaded.OmcAgents!["planner"] == "fable", "omc value survives the write");
            Check(reloaded.OuroborosKeys!["clarification.default_model"] == "fable", "Ouroboros value survives the write");
            Check(File.ReadAllText(Path.Combine(ouroborosDirectory, "config.yaml")).Contains("cli_path: /usr/local/bin/claude-nested"),
                "orchestrator.cli_path survives");

            // 자세히 줄 하나만 바꾸기
            var knobEdit = PhaseModelSection.ApplyKnob("omc.agent.codeReviewer", "haiku", new(), reloaded);
            Check(knobEdit.OmcAgents!["codeReviewer"] == "haiku", "a detail row changes one agent");
            Check(knobEdit.OmcAgents["planner"] == "fable", "the other agents are untouched");
            var afterKnob = PhaseModelSection.SaveTools(reloaded, knobEdit, dir);
            Check(afterKnob.OmcAgents!["codeReviewer"] == "haiku" && afterKnob.OmcAgents["planner"] == "fable",
                "a detail row change reaches config.jsonc and reloads");
            var effortEdit = PhaseModelSection.ApplyKnob("codex.codexPlanModeReasoningEffort", "high", new(), reloaded);
            Check(effortEdit.Config.CodexPlanModeReasoningEffort == "high", "the effort knob is edited in details");

            // 읽을 수 없는 파일: 보여 줄 수 있는 말, 파일은 그대로
            var store = new ModelSettingsFileStore(dir);
            File.WriteAllText(store.OmcConfigPath, "{ not json");
            var brokenBytes = File.ReadAllBytes(store.OmcConfigPath);
            var broken = PhaseModelSection.LoadTools(dir);
            Check(broken.Error is not null && broken.Error.Contains(store.OmcConfigPath), "unreadable file → visible error with its path");
            Check(!broken.Error!.StartsWith("settings.", StringComparison.Ordinal), "the error is localized copy, not a raw key");

            // 고른 값을 쓰려 해도 파일은 한 바이트도 바뀌지 않고 오류가 보인다.
            var refused = PhaseModelSection.SaveTools(broken,
                PhaseModelSection.ApplyPhaseRow(Phase.Review, "opus", new(), broken), dir);
            Check(refused.Error is not null && refused.Error.Contains(store.OmcConfigPath), "a refused write shows the localized error");
            Check(File.ReadAllBytes(store.OmcConfigPath).SequenceEqual(brokenBytes), "the unparseable file stays byte-identical");
        }
        finally { Directory.Delete(dir, true); }
        return Task.CompletedTask;
    }
}
