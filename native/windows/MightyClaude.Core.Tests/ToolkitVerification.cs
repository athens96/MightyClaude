using System.Text.Json;
using System.Text.RegularExpressions;
using MightyClaude.Core;
using ToolkitFileEntry = MightyClaude.Core.ToolkitFileReader.ToolkitFileEntry;

/// Tests for AC 2 (WIN_CORE_OK): toolkit store, runner, probe, approval and components.
/// Every test uses temp directories; no real user paths, no real installs.
internal static class ToolkitVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    // ── 1: toolkit platform rule matches macOS ────────────────────────────────

    internal static Task ToolkitPlatformRuleMatchesMacOS()
    {
        // brew and repoScript are macOS-only (not Windows).
        var brewEntry = MakeEntry("brew-pkg", new ToolkitFileReader.PackageSpec("brew", "ripgrep"));
        var repoEntry = MakeEntry("my-script", new ToolkitFileReader.RepoScriptSpec("https://github.com/x/y.git", "abc123", "install.sh"));
        Check(ToolkitFileReader.IsMacOSOnly(brewEntry), "brew is macOS-only");
        Check(ToolkitFileReader.IsMacOSOnly(repoEntry), "repoScript is macOS-only");
        Check(!ToolkitFileReader.IsWindowsPlatform(brewEntry), "brew is not Windows");
        Check(!ToolkitFileReader.IsWindowsPlatform(repoEntry), "repoScript is not Windows");

        // plugin, mcp, skill, npm, winget are both platforms.
        var pluginEntry = MakeEntry("p", new ToolkitFileReader.PluginSpec("org/repo", "repo@repo"));
        var mcpEntry = MakeEntry("m", new ToolkitFileReader.McpSpec("my-mcp", "/usr/bin/node", []));
        var skillEntry = MakeEntry("s", new ToolkitFileReader.SkillSpec("https://github.com/x/y.git"));
        var npmEntry = MakeEntry("n", new ToolkitFileReader.PackageSpec("npm", "typescript"));
        var wingetEntry = MakeEntry("w", new ToolkitFileReader.PackageSpec("winget", "OpenJS.NodeJS", "node.exe"));

        foreach (var e in new[] { pluginEntry, mcpEntry, skillEntry, npmEntry, wingetEntry })
        {
            Check(!ToolkitFileReader.IsMacOSOnly(e), e.Id + " should not be macOS-only");
            Check(ToolkitFileReader.IsWindowsPlatform(e), e.Id + " should be Windows platform");
        }
        return Task.CompletedTask;
    }

    // ── 2: toolkit winget template decodes and builds its command ─────────────

    internal static Task ToolkitWingetTemplateDecodesAndBuildsItsCommand()
    {
        const string json = """
            {
              "version": 1,
              "entries": [
                {
                  "id": "nodejs",
                  "displayName": "Node.js",
                  "install": {
                    "kind": "package",
                    "manager": "winget",
                    "name": "OpenJS.NodeJS",
                    "executable": "node.exe"
                  }
                }
              ]
            }
            """;
        var file = ToolkitFileReader.Parse(json);
        Check(file.Entries.Count == 1, "winget entry parsed");
        var entry = file.Entries[0];
        Check(entry.Install is ToolkitFileReader.PackageSpec { Manager: "winget", Name: "OpenJS.NodeJS", Executable: "node.exe" }, "winget spec fields");

        var ctx = FakeContext();
        var commands = ToolkitRunner.InstallCommands(entry, null, ctx);
        Check(commands.Count == 1, "winget produces one command");
        var cmd = commands[0];
        Check(cmd[0] == "winget", "first arg is winget");
        Check(cmd.Contains("--exact"), "has --exact");
        Check(cmd.Contains("--id"), "has --id");
        Check(cmd.Contains("OpenJS.NodeJS"), "has package name");
        Check(cmd.Contains("--source"), "has --source");
        Check(cmd.Contains("winget"), "has source winget");
        Check(cmd.Contains("--scope"), "has --scope");
        Check(cmd.Contains("user"), "has scope user");
        Check(cmd.Contains("--accept-source-agreements"), "has --accept-source-agreements");
        Check(cmd.Contains("--accept-package-agreements"), "has --accept-package-agreements");
        Check(cmd.Contains("--disable-interactivity"), "has --disable-interactivity");

        // winget without executable should be rejected at parse time.
        const string noExe = """
            {
              "version": 1,
              "entries": [
                {
                  "id": "bad-winget",
                  "displayName": "Bad",
                  "install": { "kind": "package", "manager": "winget", "name": "Some.Package" }
                }
              ]
            }
            """;
        var noExeFile = ToolkitFileReader.Parse(noExe);
        Check(noExeFile.Entries.Count == 0, "winget without executable is skipped");
        return Task.CompletedTask;
    }

    // ── 3: toolkit store keeps other-OS entries ───────────────────────────────

    internal static Task ToolkitStoreKeepsOtherOsEntries()
    {
        var dir = TempDir();
        try
        {
            // Write a toolkit.json that has one Windows-compatible npm entry
            // and one macOS-only brew entry.
            var json = """
                {
                  "version": 1,
                  "entries": [
                    {
                      "displayName": "TypeScript",
                      "id": "typescript",
                      "install": { "kind": "package", "manager": "npm", "name": "typescript" }
                    },
                    {
                      "displayName": "ripgrep",
                      "id": "ripgrep",
                      "install": { "kind": "package", "manager": "brew", "name": "ripgrep" }
                    }
                  ]
                }
                """;
            File.WriteAllText(Path.Combine(dir, "toolkit.json"), json);

            var store = new ToolkitStore(dir);
            var (entries, error) = store.List();
            Check(error is null, "no error");

            // Bundled + typescript; ripgrep (brew) should not appear.
            var ids = entries.Select(e => e.Id).ToArray();
            Check(ids.Contains("typescript"), "npm entry visible");
            Check(!ids.Contains("ripgrep"), "brew entry hidden from list");

            // Add a new Windows entry and persist, then reload.
            var winget = MakeEntry("nodejs", new ToolkitFileReader.PackageSpec("winget", "OpenJS.NodeJS", "node.exe"));
            store.Add(winget);

            var store2 = new ToolkitStore(dir);
            var (entries2, _) = store2.List();
            var ids2 = entries2.Select(e => e.Id).ToArray();
            Check(ids2.Contains("typescript"), "npm still there after reload");
            Check(ids2.Contains("nodejs"), "winget added after reload");
            Check(!ids2.Contains("ripgrep"), "brew still hidden after reload");

            // The raw file must still contain ripgrep.
            var fileText = File.ReadAllText(Path.Combine(dir, "toolkit.json"));
            Check(fileText.Contains("ripgrep"), "brew entry preserved in file bytes");
            Check(fileText.Contains("brew"), "brew manager preserved in file bytes");

            // Remove the npm entry; brew must still survive.
            store2.Remove("typescript");
            var store3 = new ToolkitStore(dir);
            var (entries3, _) = store3.List();
            var ids3 = entries3.Select(e => e.Id).ToArray();
            Check(!ids3.Contains("typescript"), "removed npm entry gone");
            Check(!ids3.Contains("ripgrep"), "brew still hidden after remove");
            var fileText3 = File.ReadAllText(Path.Combine(dir, "toolkit.json"));
            Check(fileText3.Contains("ripgrep"), "brew entry byte-identical after remove");
        }
        finally { Directory.Delete(dir, true); }
        return Task.CompletedTask;
    }

    // ── 4: toolkit approval binds to content hash ─────────────────────────────

    internal static Task ToolkitApprovalBindsToContentHash()
    {
        var dir = TempDir();
        try
        {
            var store = new ToolkitStore(dir);
            var entry = MakeEntry("my-npm", new ToolkitFileReader.PackageSpec("npm", "typescript"));
            store.Add(entry);
            Check(store.GetApproval(entry) is null, "unapproved is null");

            store.Approve("my-npm");
            var approval = store.GetApproval(entry);
            Check(approval is not null, "approval set after Approve");
            Check(approval!.ContentHash.Length == 64, "SHA-256 is 64 hex chars");

            // Changing the entry invalidates the approval.
            var changed = entry with { DisplayName = "TypeScript (changed)" };
            Check(store.GetApproval(changed) is null, "approval invalid after entry change");

            // Add (replace) the entry with changed content: approval clears.
            store.Add(changed);
            Check(store.GetApproval(changed) is null, "approval cleared after re-add");

            // Approve again and verify hash is updated.
            store.Approve("my-npm");
            var newApproval = store.GetApproval(changed);
            Check(newApproval is not null, "re-approved");
            Check(newApproval!.ContentHash != approval.ContentHash, "new hash differs after content change");

            // Bundled entry always returns null from GetApproval.
            var bundled = ToolkitStore.Bundled[0];
            Check(store.GetApproval(bundled) is null, "bundled entry approval is always null");

            // Cross-load: approval survives serialise/deserialise cycle.
            var store2 = new ToolkitStore(dir);
            var (entries, _) = store2.List();
            var loaded = entries.FirstOrDefault(e => e.Id == "my-npm");
            Check(loaded is not null, "entry reloaded");
            var reloaded = store2.GetApproval(loaded!);
            Check(reloaded?.ContentHash == newApproval.ContentHash, "approval hash survives reload");
        }
        finally { Directory.Delete(dir, true); }
        return Task.CompletedTask;
    }

    // ── 5: toolkit plan runs only missing approved entries ────────────────────

    internal static Task ToolkitPlanRunsOnlyMissingApprovedEntries()
    {
        var dir = TempDir();
        try
        {
            var store = new ToolkitStore(dir);

            var approved = MakeEntry("approved-npm", new ToolkitFileReader.PackageSpec("npm", "typescript"));
            var unapproved = MakeEntry("unapproved-npm", new ToolkitFileReader.PackageSpec("npm", "eslint"));

            store.Add(approved);
            store.Add(unapproved);
            store.Approve("approved-npm");

            // All missing: approved → Run, unapproved → Skip.
            var ctx = FakeContext();
            var runner = new ToolkitRunner(store, ctx);
            var plan = runner.Plan();

            var approvedItem = plan.FirstOrDefault(i => i.Entry.Id == "approved-npm");
            var unapprovedItem = plan.FirstOrDefault(i => i.Entry.Id == "unapproved-npm");
            Check(approvedItem is not null, "approved entry is in plan");
            Check(approvedItem!.Action == ToolkitPlanItem.PlanAction.Run, "approved gets Run action");
            Check(unapprovedItem is not null, "unapproved entry is in plan");
            Check(unapprovedItem!.Action == ToolkitPlanItem.PlanAction.Skip, "unapproved gets Skip action");

            // Bundled entry is always in the plan as Run when missing.
            var bundledItem = plan.FirstOrDefault(i => i.Entry.Id == "mighty-styles");
            Check(bundledItem is not null, "bundled entry in plan when missing");
            Check(bundledItem!.Action == ToolkitPlanItem.PlanAction.Run, "bundled gets Run action");

            // Run returns all skipped for unapproved; fake executor tracks calls.
            var executed = new List<IReadOnlyList<string>>();
            var fakeExec = new FakeExecutor(executed);
            var results = runner.Run(plan, fakeExec);

            var unapprovedResult = results.FirstOrDefault(r => r.EntryId == "unapproved-npm");
            Check(unapprovedResult?.RunVerdict == ToolkitRunItem.Verdict.Skipped, "unapproved entry result is Skipped");

            // Approved commands were executed (even though probe still returns Missing after fake executor).
            Check(executed.Any(cmd => cmd.Contains("typescript") || cmd.Any(a => a.Contains("npm"))), "approved entry commands ran");
        }
        finally { Directory.Delete(dir, true); }
        return Task.CompletedTask;
    }

    // ── 6: toolkit probes are file-only and run twice and decide the result table ──

    internal static Task ToolkitProbesAreFileOnly()
    {
        var home = TempDir();
        var localAppData = TempDir();
        try
        {
            var ctx = new ToolkitProbeContext
            {
                HomeDirectory = home,
                PathDirectories = [],
                LocalAppData = localAppData,
            };

            // Plugin: missing when installed_plugins.json absent.
            var pluginEntry = MakeEntry("my-plugin", new ToolkitFileReader.PluginSpec("org/repo", "repo@repo"));
            Check(ToolkitProbe.Probe(pluginEntry, null, ctx) == ToolkitProbe.Result.Missing, "plugin missing when no file");

            // Create installed_plugins.json with user scope.
            var pluginDir = Path.Combine(home, ".claude", "plugins");
            Directory.CreateDirectory(pluginDir);
            File.WriteAllText(Path.Combine(pluginDir, "installed_plugins.json"),
                """{"plugins":{"repo@repo":[{"scope":"user"}]}}""");
            Check(ToolkitProbe.Probe(pluginEntry, null, ctx) == ToolkitProbe.Result.Installed, "plugin installed when user-scope record present");

            // MCP: missing when .claude.json absent.
            var mcpEntry = MakeEntry("my-mcp", new ToolkitFileReader.McpSpec("my-mcp", "/bin/node", []));
            Check(ToolkitProbe.Probe(mcpEntry, null, ctx) == ToolkitProbe.Result.Missing, "mcp missing when no file");

            File.WriteAllText(Path.Combine(home, ".claude.json"),
                """{"mcpServers":{"my-mcp":{}}}""");
            Check(ToolkitProbe.Probe(mcpEntry, null, ctx) == ToolkitProbe.Result.Installed, "mcp installed when key present");

            // Skill: missing when SKILL.md absent.
            var skillEntry = MakeEntry("my-skill", new ToolkitFileReader.SkillSpec("https://github.com/example/my-skill.git"));
            Check(ToolkitProbe.Probe(skillEntry, null, ctx) == ToolkitProbe.Result.Missing, "skill missing when no SKILL.md");

            var skillDir = Path.Combine(home, ".claude", "skills", "my-skill");
            Directory.CreateDirectory(skillDir);
            File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), "# My Skill");
            Check(ToolkitProbe.Probe(skillEntry, null, ctx) == ToolkitProbe.Result.Installed, "skill installed when SKILL.md present");

            // winget: probe via %LOCALAPPDATA%\Microsoft\WinGet\Links.
            var wingetEntry = MakeEntry("nodejs", new ToolkitFileReader.PackageSpec("winget", "OpenJS.NodeJS", "node.exe"));
            Check(ToolkitProbe.Probe(wingetEntry, null, ctx) == ToolkitProbe.Result.Missing, "winget missing when executable absent");

            var linksDir = Path.Combine(localAppData, "Microsoft", "WinGet", "Links");
            Directory.CreateDirectory(linksDir);
            File.WriteAllText(Path.Combine(linksDir, "node.exe"), "");
            Check(ToolkitProbe.Probe(wingetEntry, null, ctx) == ToolkitProbe.Result.Installed, "winget installed when executable in Links dir");

            // brew and repoScript are always Missing on Windows (macOS-only).
            var brewEntry = MakeEntry("ripgrep", new ToolkitFileReader.PackageSpec("brew", "ripgrep"));
            var repoEntry = MakeEntry("my-script", new ToolkitFileReader.RepoScriptSpec("https://github.com/x/y.git", "abc123", "install.sh"));
            Check(ToolkitProbe.Probe(brewEntry, null, ctx) == ToolkitProbe.Result.Missing, "brew always missing on Windows");
            Check(ToolkitProbe.Probe(repoEntry, null, ctx) == ToolkitProbe.Result.Missing, "repoScript always missing on Windows");
        }
        finally
        {
            Directory.Delete(home, true);
            Directory.Delete(localAppData, true);
        }

        // "Run twice": Plan() uses a pre_plan probe; Run() uses a separate post_run probe.
        // Verify by creating the probe file after Plan() but before Run() — the post_run probe
        // sees it installed and reports Installed even though the executor did nothing.
        var dir2 = TempDir();
        var home2 = TempDir();
        try
        {
            var ctx2 = new ToolkitProbeContext { HomeDirectory = home2, PathDirectories = [], LocalAppData = home2 };
            var store2 = new ToolkitStore(dir2);
            var se = MakeEntry("probe-skill", new ToolkitFileReader.SkillSpec("https://github.com/example/probe-skill.git"));
            store2.Add(se);
            store2.Approve("probe-skill");
            var runner2 = new ToolkitRunner(store2, ctx2);
            var plan2 = runner2.Plan();
            Check(plan2.Any(i => i.Entry.Id == "probe-skill" && i.Action == ToolkitPlanItem.PlanAction.Run),
                "entry appears in plan: pre_plan probe says missing");
            // Simulate the install completing between Plan and Run.
            var sd = Path.Combine(home2, ".claude", "skills", "probe-skill");
            Directory.CreateDirectory(sd);
            File.WriteAllText(Path.Combine(sd, "SKILL.md"), "# Probe Skill");
            // Run with no-op executor: post_run probe (file-only) sees the SKILL.md → Installed.
            var noopLog = new List<IReadOnlyList<string>>();
            var results2 = runner2.Run(plan2, new FakeExecutor(noopLog));
            var r = results2.FirstOrDefault(x => x.EntryId == "probe-skill");
            Check(r?.RunVerdict == ToolkitRunItem.Verdict.Installed,
                "post_run probe decides Installed even when executor ran nothing (two separate probe phases)");
        }
        finally { Directory.Delete(dir2, true); Directory.Delete(home2, true); }
        return Task.CompletedTask;
    }

    // ── 7: toolkit install is an ordered step list ────────────────────────────

    internal static Task ToolkitInstallIsAnOrderedStepList()
    {
        // A plugin entry must produce exactly two steps: marketplace add first, install second.
        var plugin = MakeEntry("my-plugin", new ToolkitFileReader.PluginSpec("athens96/mighty-styles", "mighty-styles@mighty-styles"));
        var ctx = FakeContext();
        var commands = ToolkitRunner.InstallCommands(plugin, null, ctx);
        Check(commands.Count == 2, "plugin entry produces two install steps");

        // Step 1: claude plugin marketplace add --scope user <source>
        var step1 = commands[0];
        Check(step1[0] == "claude", "step 1 binary is claude");
        Check(step1.Contains("marketplace"), "step 1 is a marketplace command");
        Check(step1.Contains("add"), "step 1 is an add command");
        Check(step1.Contains("athens96/mighty-styles"), "step 1 carries the source");
        Check(step1.Contains("--scope"), "step 1 has --scope");
        Check(step1.Contains("user"), "step 1 scope is user");

        // Step 2: claude plugin install <pluginID> --scope user --json
        var step2 = commands[1];
        Check(step2[0] == "claude", "step 2 binary is claude");
        Check(step2.Contains("install"), "step 2 is install");
        Check(step2.Contains("mighty-styles@mighty-styles"), "step 2 carries the pluginID");
        Check(step2.Contains("--scope"), "step 2 has --scope");
        Check(step2.Contains("user"), "step 2 scope is user");
        Check(step2.Contains("--json"), "step 2 has --json");

        // The confirm view enumerates every step argv (checked via the plan item's Commands property).
        var dir = TempDir();
        try
        {
            var store = new ToolkitStore(dir);
            store.Add(plugin);
            store.Approve("my-plugin");
            var runner = new ToolkitRunner(store, ctx);
            var plan = runner.Plan();
            var planItem = plan.FirstOrDefault(i => i.Entry.Id == "my-plugin");
            Check(planItem is not null, "plugin entry is in the plan");
            Check(planItem!.Commands.Count == 2, "plan item carries both steps");
            // Each step is a full argv (not a shell string).
            foreach (var cmd in planItem.Commands)
                Check(cmd.Count > 1, "each step is a full argv, not a single string");
        }
        finally { Directory.Delete(dir, true); }

        // One failing step stops later steps of the SAME entry; later entries still run.
        // Use a distinct source token so the executor can target exactly this entry's step.
        const string stepSrc = "org/step-plugin-test";
        var stepPlugin = MakeEntry("step-plugin", new ToolkitFileReader.PluginSpec(stepSrc, "step@step"));
        var stepDir = TempDir();
        try
        {
            var stepStore = new ToolkitStore(stepDir);
            stepStore.Add(stepPlugin);
            stepStore.Approve("step-plugin");
            var secondEntry = MakeEntry("second-npm", new ToolkitFileReader.PackageSpec("npm", "eslint"));
            stepStore.Add(secondEntry);
            stepStore.Approve("second-npm");
            var stepRunner = new ToolkitRunner(stepStore, ctx);
            var stepPlan = stepRunner.Plan();
            var callLog = new List<IReadOnlyList<string>>();
            // Fail only the command that carries the step-plugin source token.
            var results = stepRunner.Run(stepPlan, new SpecificFailExecutor(callLog, stepSrc));
            // step-plugin: step 0 (marketplace add) failed → step 1 (install) skipped
            var pluginResult = results.FirstOrDefault(r => r.EntryId == "step-plugin");
            Check(pluginResult is not null, "step-plugin entry has a result");
            Check(pluginResult!.Steps.Count == 2, "step-plugin has 2 step results");
            Check(pluginResult.Steps[0].Outcome == ToolkitStepResult.StepOutcome.Failed, "plugin step 0 is Failed");
            Check(pluginResult.Steps[1].Outcome == ToolkitStepResult.StepOutcome.Skipped, "plugin step 1 is Skipped after failure");
            // npm entry still ran despite the plugin entry failing
            Check(callLog.Any(cmd => cmd.Any(a => a.Contains("eslint") || a == "npm")),
                "second entry ran despite first entry step failure");
        }
        finally { Directory.Delete(stepDir, true); }

        return Task.CompletedTask;
    }

    // ── 8: bundled entry sorts first and survives an unreadable store ─────────

    internal static Task BundledEntrySortsFirstAndSurvivesAnUnreadableStore()
    {
        var dir = TempDir();
        try
        {
            var filePath = Path.Combine(dir, "toolkit.json");
            File.WriteAllText(filePath, "not-json{{{");
            var originalBytes = File.ReadAllBytes(filePath);

            var store = new ToolkitStore(dir);
            var (entries, error) = store.List();

            // Error must be set.
            Check(error is not null, "error is non-null when toolkit.json is unreadable");
            Check(error == Locale.Get("settings.toolkit.errorBanner"), "error uses the errorBanner locale key");

            // Bundled entry is present and is first.
            Check(entries.Count == 1, "only the bundled row when the file is unreadable");
            Check(entries[0].Id == "mighty-styles", "bundled mighty-styles row is present");
            Check(entries[0].Source == ToolkitFileReader.ToolkitEntrySource.Bundled, "the bundled row has Bundled source");

            // File is byte-identical (never written by the load path).
            var currentBytes = File.ReadAllBytes(filePath);
            Check(currentBytes.Length == originalBytes.Length && currentBytes.SequenceEqual(originalBytes),
                "toolkit.json is byte-identical after an unreadable load");

            return Task.CompletedTask;
        }
        finally { Directory.Delete(dir, true); }
    }

    // ── 9: result table separates not attempted, failed and succeeded ─────────

    internal static Task ResultTableSeparatesNotAttemptedFailedAndSucceeded()
    {
        var dir = TempDir();
        var home = TempDir();
        try
        {
            var ctx = new ToolkitProbeContext
            {
                HomeDirectory = home,
                PathDirectories = [],
                LocalAppData = home,
            };

            var store = new ToolkitStore(dir);

            // Entry 1: unapproved → not attempted (Skipped).
            var notAttemptedEntry = MakeEntry("unapproved-skill",
                new ToolkitFileReader.SkillSpec("https://github.com/x/unapproved.git"));
            store.Add(notAttemptedEntry);

            // Entry 2: approved, but executor does nothing → probe stays Missing (Failed).
            var failingEntry = MakeEntry("failing-mcp",
                new ToolkitFileReader.McpSpec("failing-mcp", "/bin/node", []));
            store.Add(failingEntry);
            store.Approve("failing-mcp");

            // Entry 3: approved, executor creates SKILL.md → probe returns Installed (Installed/succeeded).
            var succeedingEntry = MakeEntry("succeeding",
                new ToolkitFileReader.SkillSpec("https://github.com/x/succeeding.git"));
            store.Add(succeedingEntry);
            store.Approve("succeeding");

            var runner = new ToolkitRunner(store, ctx);
            var plan = runner.Plan();

            var executed = new List<IReadOnlyList<string>>();
            IToolkitRunnerExecutor fakeExec = new SkillCreatingExecutor(executed);
            var results = runner.Run(plan, fakeExec);

            // Unapproved entry → Skipped (not attempted).
            var notAttempted = results.FirstOrDefault(r => r.EntryId == "unapproved-skill");
            Check(notAttempted is not null, "unapproved entry has a result row");
            Check(notAttempted!.RunVerdict == ToolkitRunItem.Verdict.Skipped,
                "unapproved entry verdict is Skipped (not attempted)");

            // MCP failed (no .claude.json created) → Failed.
            var failed = results.FirstOrDefault(r => r.EntryId == "failing-mcp");
            Check(failed is not null, "failing entry has a result row");
            Check(failed!.RunVerdict == ToolkitRunItem.Verdict.Failed,
                "failing entry verdict is Failed");

            // Skill succeeded (SKILL.md was created) → Installed.
            var succeeded = results.FirstOrDefault(r => r.EntryId == "succeeding");
            Check(succeeded is not null, "succeeding entry has a result row");
            Check(succeeded!.RunVerdict == ToolkitRunItem.Verdict.Installed,
                "succeeding entry verdict is Installed (succeeded)");

            // All three are distinct.
            Check(notAttempted.RunVerdict != failed.RunVerdict, "not-attempted differs from failed");
            Check(failed.RunVerdict != succeeded.RunVerdict, "failed differs from succeeded");
            Check(notAttempted.RunVerdict != succeeded.RunVerdict, "not-attempted differs from succeeded");

            return Task.CompletedTask;
        }
        finally
        {
            Directory.Delete(dir, true);
            Directory.Delete(home, true);
        }
    }

    // ── 10: components rows follow installed CLIs ──────────────────────────────

    internal static Task ComponentsRowsFollowInstalledCLIs()
    {
        // All three providers missing.
        var runtime = MakeRuntime([]);
        var rows = ComponentSection.SectionRows(runtime);
        Check(rows.Count == 3, "3 rows always");
        foreach (var row in rows)
            Check(row.State == "missing", row.Id + " missing when not in runtime");

        // All three installed.
        var fullRuntime = MakeRuntime(["claude", "codex", "gemini"]);
        var fullRows = ComponentSection.SectionRows(fullRuntime);
        Check(fullRows.All(r => r.State == "installed"), "all installed when all available");

        // Claude below Mods minimum → attention.
        var oldClaude = MakeProviderRuntime("claude", available: true, version: "2.1.263");
        var rtOld = MakeRuntime([], extraProviders: [oldClaude]);
        var oldRows = ComponentSection.SectionRows(rtOld);
        var claudeRow = oldRows.First(r => r.Id == "claude");
        Check(claudeRow.State == "attention", "claude below Mods minimum → attention");
        Check(claudeRow.Actions.Any(a => a.Id == "update"), "claude attention has update action");

        // Claude at exactly 2.1.271 → installed.
        var minClaude = MakeProviderRuntime("claude", available: true, version: "2.1.271");
        var rtMin = MakeRuntime(["codex", "gemini"], extraProviders: [minClaude]);
        var minRows = ComponentSection.SectionRows(rtMin);
        Check(minRows.First(r => r.Id == "claude").State == "installed", "claude at 2.1.271 is installed");

        // Section title comes from the locale key.
        Check(ComponentSection.SectionTitle == Locale.Get("settings.components.sectionTitle"), "SectionTitle uses locale");

        // Missing provider has a copy-command action with the npm install string.
        var missingRow = rows.First(r => r.Id == "claude");
        Check(missingRow.Actions.Any(a => a.Id == "copy-command"), "missing claude has copy-command action");
        Check(ComponentSection.InstallCommand("claude")?.Contains("@anthropic-ai/claude-code") == true, "claude install command is correct");
        Check(ComponentSection.InstallCommand("codex")?.Contains("@openai/codex") == true, "codex install command is correct");
        Check(ComponentSection.InstallCommand("gemini")?.Contains("@google/gemini-cli") == true, "gemini install command is correct");

        return Task.CompletedTask;
    }

    // ── 8: the Components screen is live in the running app ───────────────────

    /// AC 3 (DELIVERED_OK). The Components section is not just Core logic: the
    /// running Windows app registers the slot, draws the CLI rows and the toolkit
    /// list, offers add/remove/approve/export/import/install, shows one confirm
    /// view holding every argv before anything runs, and renders the result
    /// table. Read from the WinUI source the same way the plugin-marketplace
    /// wiring test does, because WinUI itself cannot be built on this machine.
    internal static Task ComponentsScreenIsLiveInTheRunningApp()
    {
        var winui = ClaudePluginVerification.WinUISource();
        Check(Directory.Exists(winui), "the WinUI project folder is missing: " + winui);
        var file = Path.Combine(winui, "MainWindow.Settings.cs");
        Check(File.Exists(file), "MainWindow.Settings.cs draws the settings sections and must exist");
        var source = File.ReadAllText(file);

        // The slot is registered, titled from Core, and kept in the macOS order.
        var slots = SettingsSections.MacOrder;
        var components = slots.FirstOrDefault(slot => slot.Id == SettingsSections.Components);
        Check(components is not null, "the Components slot must exist in the macOS order");
        Check(components!.OnWindows, "the Components slot must be shown on Windows");
        Check(components.WindowsTitle == ComponentSection.SectionTitle,
            "the Components heading must come from ComponentSection.SectionTitle");
        Check(ComponentSection.SectionTitle == Locale.Get("settings.components.sectionTitle"),
            "ComponentSection.SectionTitle must read settings.components.sectionTitle");
        Check(SettingsSections.WindowsTitles.Contains(ComponentSection.SectionTitle),
            "the smoke run's expected headings must include the Components heading");
        var order = slots.Select(slot => slot.Id).ToList();
        Check(order.IndexOf(SettingsSections.Components) > order.IndexOf(SettingsSections.PhaseModels)
            && order.IndexOf(SettingsSections.Components) < order.IndexOf(SettingsSections.CliUpdate),
            "Components must sit between the phase models and the CLI update sections, as on macOS");

        // WinUI supplies the builder for that slot, so opening Settings draws it.
        Check(source.Contains("SettingsSections.Components => BuildComponentsSection"),
            "WinUI must map the Components slot to BuildComponentsSection");
        Check(source.Contains("private StackPanel BuildComponentsSection()"),
            "BuildComponentsSection must exist in WinUI");

        // Every control the section promises, by the automation id it carries.
        foreach (var id in new[]
                 {
                     "components-refresh", "settings-toolkit-add", "settings-toolkit-export",
                     "settings-toolkit-import", "settings-toolkit-install",
                 })
            Check(source.Contains("\"" + id + "\""), "the section must draw the control " + id);
        foreach (var prefix in new[] { "\"component-\" + row.Id", "\"toolkit-entry-\" + entry.Id", "\"toolkit-approve-\" + entryId", "\"toolkit-remove-\" + removeEntryId" })
            Check(source.Contains(prefix), "each row must carry its own id: " + prefix);

        // The CLI rows come from Core, one per provider, with Core's own labels
        // and Core's install command — never a decision made in the view.
        Check(source.Contains("ComponentSection.SectionRows(rt)"),
            "the CLI rows must come from ComponentSection.SectionRows");
        Check(source.Contains("ComponentSection.InstallCommand(row.Id)"),
            "the copy-install-command action must use ComponentSection.InstallCommand");
        Check(source.Contains("actionId == \"copy-command\""),
            "the missing state's action must be the copy-install-command action");

        // The toolkit list, approval, removal and export all go through the store.
        foreach (var call in new[]
                 {
                     "new ToolkitStore(StateDirectory)", "store.List()", "store.Approve(entryId)",
                     "store.Remove(removeEntryId)", "store.Add(entry)", "store.Export()",
                     "store.GetApproval(entry)",
                 })
            Check(source.Contains(call), "the section must ask Core: " + call);
        Check(source.Contains("options.ProfileDirectory ??"),
            "toolkit.json must sit in the StateStore folder, which follows --profile");

        // One confirm view lists every argv, and nothing runs unless it is
        // accepted. The plan itself is Core's, and only approved+missing entries
        // are in it.
        Check(source.Contains("var plan = runner.Plan();"), "the plan must come from ToolkitRunner.Plan");
        Check(source.Contains("if (plan.Count == 0) return;"), "an empty plan must run nothing");
        Check(source.Contains("string.Join(\" \", argv)"), "the confirm view must list every argv");
        Check(source.Contains("item.Commands"), "the confirm view must read the argv list from the plan item");
        Check(source.Contains("new ContentDialog") && source.Contains("settings.toolkit.confirmTitle"),
            "the confirm view must be one dialog titled settings.toolkit.confirmTitle");
        Check(source.Contains("if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;"),
            "cancelling the confirm view must run nothing");
        Check(source.Contains("if (options.SmokeTest) return;"),
            "the smoke run must never reach a real install");
        Check(source.Contains("runner.Run(plan, new CliToolkitExecutor())"),
            "the run must go through ToolkitRunner.Run with the argv executor");

        // The result table is rendered from Core's verdicts, and the list is
        // probed again afterwards.
        foreach (var verdict in new[] { "ToolkitRunItem.Verdict.Installed", "ToolkitRunItem.Verdict.Failed" })
            Check(source.Contains(verdict), "the result table must read " + verdict);
        Check(source.Contains("FillToolkitResults(toolkitResultsPanel, results)"),
            "the result table must be filled from the run results");
        Check(source.Contains("FillToolkitList(toolkitListPanel, store, reloaded)"),
            "the list must be rebuilt from a fresh probe after the run");

        // Commands are argv arrays through the existing launcher, never a shell
        // string.
        Check(source.Contains("runner.RunAsync(binary, args, TimeSpan.FromMinutes(5))"),
            "each toolkit command must start from an argv array through ICliRunner");
        Check(!source.Contains("/bin/sh") && !source.Contains("cmd.exe /c"),
            "no toolkit command may be joined into a shell string");

        // Every visible word is a locale key that both catalogues answer, so the
        // smoke leak scan cannot find a raw key in this section.
        var keys = Regex.Matches(source, "Locale\\.Get\\(\"((?:settings\\.components|settings\\.toolkit)\\.[^\"]+)\"")
            .Select(m => m.Groups[1].Value).Distinct().ToList();
        Check(keys.Count >= 20, "the section must read its copy from the locale catalogue, found " + keys.Count + " keys");
        var ko = Locale.Catalogue("ko");
        var en = Locale.Catalogue("en");
        foreach (var key in keys)
        {
            Check(ko.ContainsKey(key), "locales/ko.json is missing " + key);
            Check(en.ContainsKey(key), "locales/en.json is missing " + key);
            Check(!string.IsNullOrWhiteSpace(ko[key]) && !string.IsNullOrWhiteSpace(en[key]),
                "the copy for " + key + " must not be empty");
        }
        foreach (var required in new[]
                 {
                     "settings.components.sectionDescription", "settings.components.recheckButton",
                     "settings.toolkit.sectionTitle", "settings.toolkit.addButton",
                     "settings.toolkit.removeButton", "settings.toolkit.approveButton",
                     "settings.toolkit.exportButton", "settings.toolkit.importButton",
                     "settings.toolkit.installButton", "settings.toolkit.confirmTitle",
                     "settings.toolkit.cancelButton", "settings.toolkit.errorBanner",
                     "settings.toolkit.verdictInstalled", "settings.toolkit.verdictFailed",
                     "settings.toolkit.verdictSkipped",
                 })
            Check(keys.Contains(required), "the section must show " + required);

        // No Korean of its own: the section types no Hangul literal.
        var start = source.IndexOf("private StackPanel BuildComponentsSection()", StringComparison.Ordinal);
        var end = source.IndexOf("private StackPanel BuildProvidersSection()", StringComparison.Ordinal);
        Check(start > 0 && end > start, "the Components section must sit before the providers section");
        foreach (var literal in Regex.Matches(source[start..end], "\"([^\"\\n]*)\"").Select(m => m.Groups[1].Value))
            Check(!Regex.IsMatch(literal, "[\\uAC00-\\uD7A3]"),
                "a Korean sentence is typed into the Components section instead of read from the catalogue: " + literal);

        // The smoke leak scan builds every registered section, so it now covers
        // this one without a second registration.
        var smoke = File.ReadAllText(Path.Combine(winui, "MainWindow.Smoke.cs"));
        Check(smoke.Contains("var settingsSectionsForLeak = GetSettingsSections();"),
            "the leak scan must build the sections the app registers");
        Check(smoke.Contains("settingsPanelForLeak.Children.Add(BuildSectionContainer(sec.Title, sec.Build()));"),
            "the leak scan must build each registered section's controls");
        Check(smoke.Contains("result[\"localeKeyLeaks\"] = keyLeaks;"),
            "the leak scan must report under localeKeyLeaks");

        return Task.CompletedTask;
    }

    // ── 11: shared toolkit.json format is decoded and round-tripped on both platforms ──

    // This constant is reproduced verbatim in macOS
    // MightyCoreTests/ToolkitSharedFormatTests.swift (sharedFixtureJSON).
    // Any edit here must be reflected there, and vice-versa.
    internal const string SharedFormatFixture = """
        {
          "version": 1,
          "entries": [
            {
              "id": "shared-brew",
              "displayName": "ripgrep",
              "install": {"kind": "package", "manager": "brew", "name": "ripgrep"},
              "approval": {"contentHash": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}
            },
            {
              "id": "shared-npm",
              "displayName": "TypeScript",
              "install": {"kind": "package", "manager": "npm", "name": "typescript"},
              "approval": {"contentHash": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"}
            },
            {
              "id": "shared-winget",
              "displayName": "Node.js",
              "install": {"kind": "package", "manager": "winget", "name": "OpenJS.NodeJS", "executable": "node.exe"},
              "approval": {"contentHash": "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"}
            }
          ]
        }
        """;

    internal static Task SharedFormatFixtureRoundTrip()
    {
        var dir = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "toolkit.json"), SharedFormatFixture);

            // Force a persist cycle: Add creates the lazy-load; Remove triggers a second persist.
            var store = new ToolkitStore(dir);
            var dummy = MakeEntry("tmp-dummy", new ToolkitFileReader.SkillSpec("https://github.com/x/tmp.git"));
            store.Add(dummy);
            store.Remove("tmp-dummy");

            // Re-read the saved JSON and verify every entry, field and approval value.
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "toolkit.json")));
            var entries = doc.RootElement.GetProperty("entries");

            JsonElement? FindEntry(string id)
            {
                foreach (var e in entries.EnumerateArray())
                    if (e.GetProperty("id").GetString() == id) return e;
                return null;
            }

            string? InstallField(JsonElement e, string key) =>
                e.GetProperty("install").TryGetProperty(key, out var v) ? v.GetString() : null;

            string? ApprovalHash(JsonElement e) =>
                e.TryGetProperty("approval", out var a) && a.TryGetProperty("contentHash", out var h)
                    ? h.GetString() : null;

            // brew (macOS-only → stored as raw element, written back unchanged)
            var brew = FindEntry("shared-brew") ?? throw new InvalidOperationException("shared-brew missing from saved file");
            Check(brew.GetProperty("displayName").GetString() == "ripgrep", "brew displayName preserved");
            Check(InstallField(brew, "kind") == "package",  "brew install.kind preserved");
            Check(InstallField(brew, "manager") == "brew",  "brew install.manager preserved");
            Check(InstallField(brew, "name") == "ripgrep",  "brew install.name preserved");
            Check(ApprovalHash(brew) == new string('a', 64), "brew approval.contentHash preserved");

            // npm (both platforms → re-serialised by Persist)
            var npm = FindEntry("shared-npm") ?? throw new InvalidOperationException("shared-npm missing from saved file");
            Check(npm.GetProperty("displayName").GetString() == "TypeScript", "npm displayName preserved");
            Check(InstallField(npm, "kind") == "package",       "npm install.kind preserved");
            Check(InstallField(npm, "manager") == "npm",        "npm install.manager preserved");
            Check(InstallField(npm, "name") == "typescript",    "npm install.name preserved");
            Check(ApprovalHash(npm) == new string('b', 64),     "npm approval.contentHash preserved");

            // winget (Windows-only → re-serialised by Persist)
            var winget = FindEntry("shared-winget") ?? throw new InvalidOperationException("shared-winget missing from saved file");
            Check(winget.GetProperty("displayName").GetString() == "Node.js", "winget displayName preserved");
            Check(InstallField(winget, "kind") == "package",         "winget install.kind preserved");
            Check(InstallField(winget, "manager") == "winget",       "winget install.manager preserved");
            Check(InstallField(winget, "name") == "OpenJS.NodeJS",   "winget install.name preserved");
            Check(InstallField(winget, "executable") == "node.exe",  "winget install.executable preserved");
            Check(ApprovalHash(winget) == new string('c', 64),       "winget approval.contentHash preserved");

            // The removed dummy must not appear.
            Check(FindEntry("tmp-dummy") is null, "removed dummy entry must not be in saved file");

            return Task.CompletedTask;
        }
        finally { Directory.Delete(dir, true); }
    }

    // ── 12: toolkit file round-trip is a fixed point ──────────────────────────

    internal static Task ToolkitFileRoundTripIsAFixedPoint()
    {
        // Canonical file: sorted top-level keys (entries < version), 2-space indent,
        // LF line endings, one trailing newline. Entry uses sorted keys (displayName < id < install).
        var canonical = "{\n  \"entries\": [\n    {\"displayName\":\"TypeScript\",\"id\":\"npm-fp\",\"install\":{\"kind\":\"package\",\"manager\":\"npm\",\"name\":\"typescript\"}}\n  ],\n  \"version\": 1\n}\n";
        var dir = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "toolkit.json"), canonical, System.Text.Encoding.UTF8);
            // Force a save: add+remove dummy (original entry stays untouched → raw bytes re-emitted).
            var store = new ToolkitStore(dir);
            var dummy = MakeEntry("_fp_dummy_", new ToolkitFileReader.SkillSpec("https://github.com/x/dummy.git"));
            store.Add(dummy);
            store.Remove("_fp_dummy_");
            var result = File.ReadAllText(Path.Combine(dir, "toolkit.json"), System.Text.Encoding.UTF8);
            Check(result == canonical,
                "canonical file is a fixed point of save.\n  Expected: " + canonical.Replace("\n", "\\n") +
                "\n  Got:      " + result.Replace("\n", "\\n"));
        }
        finally { Directory.Delete(dir, true); }
        return Task.CompletedTask;
    }

    // ── 13: step fetch eligibility is derived ─────────────────────────────────

    internal static Task StepFetchEligibilityIsDerived()
    {
        // Fetch steps (eligible for network-error retry, no stored flag — derived from argv).
        Check(ToolkitRunner.IsFetchStep(["git", "clone", "url", "dest"]), "git clone is a fetch step");
        Check(ToolkitRunner.IsFetchStep(["git", "ls-remote", "url", "ref"]), "git ls-remote is a fetch step");
        Check(ToolkitRunner.IsFetchStep(["npm", "install", "-g", "pkg"]), "npm install is a fetch step");
        Check(ToolkitRunner.IsFetchStep(["claude", "plugin", "marketplace", "add", "--scope", "user", "src"]), "claude plugin marketplace add is a fetch step");
        Check(ToolkitRunner.IsFetchStep(["claude", "plugin", "install", "id", "--scope", "user", "--json"]), "claude plugin install is a fetch step");
        Check(ToolkitRunner.IsFetchStep(["winget", "install", "--exact", "--id", "Pkg.Id"]), "winget install is a fetch step");

        // Non-fetch steps (never retried).
        Check(!ToolkitRunner.IsFetchStep(["claude", "mcp", "add", "--scope", "user", "n", "--", "/bin/node"]), "mcp add is not a fetch step");
        Check(!ToolkitRunner.IsFetchStep(["git", "status"]), "git status is not a fetch step");
        Check(!ToolkitRunner.IsFetchStep([]), "empty argv is not a fetch step");

        // A fetch step matching the network-error pattern is retried exactly once.
        var dir = TempDir();
        try
        {
            var store = new ToolkitStore(dir);
            var npmEntry = MakeEntry("retry-npm", new ToolkitFileReader.PackageSpec("npm", "retry-pkg"));
            store.Add(npmEntry);
            store.Approve("retry-npm");
            var ctx = FakeContext();
            var runner = new ToolkitRunner(store, ctx);
            var plan = runner.Plan();
            var counts = new Dictionary<string, int>();
            runner.Run(plan, new NetworkFailExecutor(counts));
            // npm install is a fetch step → retried once → called 2 times total
            Check(counts.GetValueOrDefault("npm install", 0) == 2,
                "fetch step retried exactly once (called 2 times), got " + counts.GetValueOrDefault("npm install", 0));
        }
        finally { Directory.Delete(dir, true); }

        // A non-fetch step is never retried even when the output matches the network-error pattern.
        var dir2 = TempDir();
        try
        {
            var store2 = new ToolkitStore(dir2);
            var mcpEntry = MakeEntry("retry-mcp", new ToolkitFileReader.McpSpec("mymcp", "/bin/node", []));
            store2.Add(mcpEntry);
            store2.Approve("retry-mcp");
            var ctx2 = FakeContext();
            var runner2 = new ToolkitRunner(store2, ctx2);
            var plan2 = runner2.Plan();
            var counts2 = new Dictionary<string, int>();
            runner2.Run(plan2, new NetworkFailExecutor(counts2));
            // "claude mcp" is not a fetch step → never retried → called exactly 1 time
            Check(counts2.GetValueOrDefault("claude mcp", 0) == 1,
                "non-fetch step never retried (called 1 time), got " + counts2.GetValueOrDefault("claude mcp", 0));
        }
        finally { Directory.Delete(dir2, true); }

        return Task.CompletedTask;
    }

    internal static Task SharedFormatPlatformTable()
    {
        // Build the same three entries as in the fixture — same (kind, manager) pairs as the macOS test.
        var brew   = MakeEntry("shared-brew",   new ToolkitFileReader.PackageSpec("brew",   "ripgrep"));
        var npm    = MakeEntry("shared-npm",    new ToolkitFileReader.PackageSpec("npm",    "typescript"));
        var winget = MakeEntry("shared-winget", new ToolkitFileReader.PackageSpec("winget", "OpenJS.NodeJS", "node.exe"));

        // (package, brew) is macOS-only.
        Check(ToolkitFileReader.IsMacOSOnly(brew),        "brew is macOS-only");
        Check(!ToolkitFileReader.IsWindowsPlatform(brew), "brew is not Windows");

        // (package, npm) is both platforms.
        Check(!ToolkitFileReader.IsMacOSOnly(npm),       "npm is not macOS-only");
        Check(ToolkitFileReader.IsWindowsPlatform(npm),  "npm is Windows (both)");

        // (package, winget) is Windows-only.
        Check(!ToolkitFileReader.IsMacOSOnly(winget),      "winget is not macOS-only");
        Check(ToolkitFileReader.IsWindowsPlatform(winget), "winget is Windows");

        return Task.CompletedTask;
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static ToolkitFileEntry MakeEntry(string id, ToolkitFileReader.ToolkitInstallSpec install) =>
        new(id, id, KindFor(install), install, null, ToolkitFileReader.ToolkitEntrySource.User);

    private static string KindFor(ToolkitFileReader.ToolkitInstallSpec install) => install switch
    {
        ToolkitFileReader.PluginSpec => "plugin",
        ToolkitFileReader.McpSpec => "mcp",
        ToolkitFileReader.SkillSpec => "skill",
        ToolkitFileReader.PackageSpec => "package",
        ToolkitFileReader.RepoScriptSpec => "repoScript",
        _ => "unknown",
    };

    private static ToolkitProbeContext FakeContext()
    {
        var tmp = Path.GetTempPath();
        return new ToolkitProbeContext
        {
            HomeDirectory = tmp,
            PathDirectories = [],
            LocalAppData = tmp,
        };
    }

    private static string TempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "mighty-toolkit-test-" + Wire.Id());
        Directory.CreateDirectory(path);
        return path;
    }

    private static RuntimeInfo MakeRuntime(string[] availableIds, ProviderRuntime[]? extraProviders = null)
    {
        var providers = new List<ProviderRuntime>();
        foreach (var id in Wire.Providers)
        {
            if (availableIds.Contains(id))
                providers.Add(MakeProviderRuntime(id, available: true, version: "2.1.271"));
        }
        if (extraProviders is not null) providers.AddRange(extraProviders);
        return new RuntimeInfo("win32", "1.0.0", providers.Any(p => p.Id == "claude" && p.Available), null, null, providers, null);
    }

    private static ProviderRuntime MakeProviderRuntime(string id, bool available, string? version) =>
        new(id, id, available, version, available ? Locale.Get("provider.available") : Locale.Get("provider.notInstalled"),
            new ModelCatalog("cli", [], ""), new ProviderCapabilities(true, [], true, true, true));

    private sealed class FakeExecutor(List<IReadOnlyList<string>> log) : IToolkitRunnerExecutor
    {
        public ToolkitCommandOutput Run(IReadOnlyList<string> argv) { log.Add(argv); return ToolkitCommandOutput.Success; }
    }

    // Fails any command whose argv contains the given token.
    private sealed class SpecificFailExecutor(List<IReadOnlyList<string>> log, string failToken) : IToolkitRunnerExecutor
    {
        public ToolkitCommandOutput Run(IReadOnlyList<string> argv)
        {
            log.Add(argv);
            return argv.Contains(failToken)
                ? ToolkitCommandOutput.Failure("not a network error", 1)
                : ToolkitCommandOutput.Success;
        }
    }

    // ── 15: hash parity (fixture: native/contracts/toolkit-hash-parity.json) ─────
    //
    // Canonical bytes = compact JSON of {displayName, id, install} with sorted keys,
    // no whitespace, NFC UTF-8; approval and unknown keys (e.g. stale `platforms`)
    // excluded.  Matches ToolkitStore.canonicalJson / canonicalHash on macOS.

    private static ToolkitFileEntry ParityEntry(string id, string displayName, ToolkitFileReader.ToolkitInstallSpec install)
        => new(id, displayName, "package", install, null);

    internal static Task HashParityCanonicalBytes()
    {
        // Four entries covering every kind in the fixture.
        var brew   = ParityEntry("shared-brew",   "ripgrep",    new ToolkitFileReader.PackageSpec("brew",   "ripgrep"));
        var npm    = ParityEntry("shared-npm",    "TypeScript", new ToolkitFileReader.PackageSpec("npm",    "typescript"));
        var winget = ParityEntry("shared-winget", "Node.js",    new ToolkitFileReader.PackageSpec("winget", "OpenJS.NodeJS", "node.exe"));
        var plugin = new ToolkitFileEntry("shared-plugin", "My Plugin", "plugin",
                         new ToolkitFileReader.PluginSpec("owner/repo", "myplugin@marketplace"), null);

        Check(ToolkitStore.BuildCanonicalJson(brew)   == "{\"displayName\":\"ripgrep\",\"id\":\"shared-brew\",\"install\":{\"kind\":\"package\",\"manager\":\"brew\",\"name\":\"ripgrep\"}}",
              "brew canonical bytes");
        Check(ToolkitStore.BuildCanonicalJson(npm)    == "{\"displayName\":\"TypeScript\",\"id\":\"shared-npm\",\"install\":{\"kind\":\"package\",\"manager\":\"npm\",\"name\":\"typescript\"}}",
              "npm canonical bytes");
        Check(ToolkitStore.BuildCanonicalJson(winget) == "{\"displayName\":\"Node.js\",\"id\":\"shared-winget\",\"install\":{\"executable\":\"node.exe\",\"kind\":\"package\",\"manager\":\"winget\",\"name\":\"OpenJS.NodeJS\"}}",
              "winget canonical bytes");
        Check(ToolkitStore.BuildCanonicalJson(plugin) == "{\"displayName\":\"My Plugin\",\"id\":\"shared-plugin\",\"install\":{\"kind\":\"plugin\",\"pluginID\":\"myplugin@marketplace\",\"source\":\"owner/repo\"}}",
              "plugin canonical bytes");
        return Task.CompletedTask;
    }

    internal static Task HashParityDigests()
    {
        var brew   = ParityEntry("shared-brew",   "ripgrep",    new ToolkitFileReader.PackageSpec("brew",   "ripgrep"));
        var npm    = ParityEntry("shared-npm",    "TypeScript", new ToolkitFileReader.PackageSpec("npm",    "typescript"));
        var winget = ParityEntry("shared-winget", "Node.js",    new ToolkitFileReader.PackageSpec("winget", "OpenJS.NodeJS", "node.exe"));
        var plugin = new ToolkitFileEntry("shared-plugin", "My Plugin", "plugin",
                         new ToolkitFileReader.PluginSpec("owner/repo", "myplugin@marketplace"), null);

        Check(ToolkitStore.CanonicalHash(brew)   == "60c2f1ee819f3e586e7b4edbc21e33f042f578dbf07e1d4a2b50c8ae81d38c4d", "brew sha256");
        Check(ToolkitStore.CanonicalHash(npm)    == "6ed516dbfd00a154262de7a15ad2d96460ce255b15d4bfd25935909d1db1bdae", "npm sha256");
        Check(ToolkitStore.CanonicalHash(winget) == "17bb3a3b5145260f6a69293472356d16f24c5769c57c22cff177ac8e090f4764", "winget sha256");
        Check(ToolkitStore.CanonicalHash(plugin) == "8b7b568e58fa4ecfad28cda421b8ba26a4943493892ddae27cef78da4453919f", "plugin sha256");
        return Task.CompletedTask;
    }

    internal static Task HashParityStalePlatformsKeyExcluded()
    {
        // A winget entry loaded from a file that carries a stale `platforms` key
        // must hash identically to the same entry without it.
        var withoutPlatforms = ParityEntry("shared-winget", "Node.js", new ToolkitFileReader.PackageSpec("winget", "OpenJS.NodeJS", "node.exe"));
        // Load an entry from a JSON string that includes a stale `platforms` key.
        var json = "{\"displayName\":\"Node.js\",\"id\":\"shared-winget\",\"install\":{\"executable\":\"node.exe\",\"kind\":\"package\",\"manager\":\"winget\",\"name\":\"OpenJS.NodeJS\"},\"platforms\":[\"windows\"]}";
        using var doc = JsonDocument.Parse(json);
        var decoded = ToolkitFileReader.ParseEntry(doc.RootElement);
        var withPlatforms = decoded ?? throw new InvalidOperationException("ParseEntry returned null");
        Check(ToolkitStore.CanonicalHash(withPlatforms) == ToolkitStore.CanonicalHash(withoutPlatforms),
              "stale platforms key must not change the hash");
        Check(ToolkitStore.CanonicalHash(withPlatforms) == "17bb3a3b5145260f6a69293472356d16f24c5769c57c22cff177ac8e090f4764",
              "hash matches fixture digest even for entry loaded from a file with stale platforms");
        return Task.CompletedTask;
    }

    internal static Task HashParityChangingFieldChangesHash()
    {
        var original = ParityEntry("shared-winget", "Node.js", new ToolkitFileReader.PackageSpec("winget", "OpenJS.NodeJS", "node.exe"));
        var modified = ParityEntry("shared-winget", "Node.js", new ToolkitFileReader.PackageSpec("winget", "OpenJS.NodeJS.LTS", "node.exe"));
        Check(ToolkitStore.CanonicalHash(original) != ToolkitStore.CanonicalHash(modified),
              "changing the winget name must change the digest");
        return Task.CompletedTask;
    }

    // Round-trip: Windows canonical file (single-line entries) with a stale platforms
    // key on the winget entry → add+remove dummy → whole file bytes identical.
    internal static Task HashParityRoundTripWithStalePlatformsKey()
    {
        // Fixture file in Windows canonical format: 4 entries, winget carries stale platforms.
        // Entry order matches allEntryIds order; each entry line is compact JSON.
        var fixture =
            "{\n  \"entries\": [\n" +
            "    {\"displayName\":\"ripgrep\",\"id\":\"shared-brew\",\"install\":{\"kind\":\"package\",\"manager\":\"brew\",\"name\":\"ripgrep\"}},\n" +
            "    {\"displayName\":\"TypeScript\",\"id\":\"shared-npm\",\"install\":{\"kind\":\"package\",\"manager\":\"npm\",\"name\":\"typescript\"}},\n" +
            "    {\"displayName\":\"Node.js\",\"id\":\"shared-winget\",\"install\":{\"executable\":\"node.exe\",\"kind\":\"package\",\"manager\":\"winget\",\"name\":\"OpenJS.NodeJS\"},\"platforms\":[\"windows\"]},\n" +
            "    {\"displayName\":\"My Plugin\",\"id\":\"shared-plugin\",\"install\":{\"kind\":\"plugin\",\"pluginID\":\"myplugin@marketplace\",\"source\":\"owner/repo\"}}\n" +
            "  ],\n  \"version\": 1\n}\n";
        var dir = TempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "toolkit.json"), fixture, System.Text.Encoding.UTF8);
            var store = new ToolkitStore(dir);
            var dummy = MakeEntry("_hp_stale_dummy_", new ToolkitFileReader.SkillSpec("https://github.com/x/dummy.git"));
            store.Add(dummy);
            store.Remove("_hp_stale_dummy_");
            var result = File.ReadAllText(Path.Combine(dir, "toolkit.json"), System.Text.Encoding.UTF8);
            Check(result == fixture,
                "file with stale platforms key must be byte-identical after a save that does not touch original entries.\n  Expected: " +
                fixture.Replace("\n", "\\n") + "\n  Got:      " + result.Replace("\n", "\\n"));
        }
        finally { Directory.Delete(dir, true); }
        return Task.CompletedTask;
    }

    // ── 14: probe is authoritative for the result table ──────────────────────────

    internal static Task ResultTruthIsAuthoritative()
    {
        // ── Case 1: all steps ok + probe finds the file → Installed ──────────────
        var dir1 = TempDir(); var home1 = TempDir();
        try
        {
            var ctx1 = new ToolkitProbeContext { HomeDirectory = home1, PathDirectories = [], LocalAppData = home1 };
            var store1 = new ToolkitStore(dir1);
            var plugin1 = MakeEntry("truth-plugin", new ToolkitFileReader.PluginSpec("org/repo", "truth@repo"));
            store1.Add(plugin1); store1.Approve("truth-plugin");
            // Pre-create the probe file so the post-run probe reports Installed.
            var pluginDir1 = Path.Combine(home1, ".claude", "plugins");
            Directory.CreateDirectory(pluginDir1);
            File.WriteAllText(Path.Combine(pluginDir1, "installed_plugins.json"),
                """{"plugins":{"truth@repo":[{"scope":"user"}]}}""");
            var runner1 = new ToolkitRunner(store1, ctx1);
            var cmds1 = ToolkitRunner.InstallCommands(plugin1, null, ctx1);
            var plan1 = new[] { new ToolkitPlanItem { Entry = plugin1, Action = ToolkitPlanItem.PlanAction.Run, Commands = cmds1 } };
            var log1 = new List<IReadOnlyList<string>>();
            var results1 = runner1.Run(plan1, new FakeExecutor(log1));
            var r1 = results1.FirstOrDefault(r => r.EntryId == "truth-plugin");
            Check(r1 is not null, "case1: result present");
            // (a) verdict from post-run probe, not from step outcomes
            Check(r1!.RunVerdict == ToolkitRunItem.Verdict.Installed, "case1: verdict Installed from probe");
            // (b) step outcomes stored as explanation (both ok)
            Check(r1.Steps.Count == 2, "case1: two steps");
            Check(r1.Steps[0].Outcome == ToolkitStepResult.StepOutcome.Ok, "case1: step0 Ok");
            Check(r1.Steps[1].Outcome == ToolkitStepResult.StepOutcome.Ok, "case1: step1 Ok");
        }
        finally { Directory.Delete(dir1, true); Directory.Delete(home1, true); }

        // ── Case 2: all steps ok + probe finds nothing → Failed, steps still ok ─
        var dir2 = TempDir(); var home2 = TempDir();
        try
        {
            var ctx2 = new ToolkitProbeContext { HomeDirectory = home2, PathDirectories = [], LocalAppData = home2 };
            var store2 = new ToolkitStore(dir2);
            var plugin2 = MakeEntry("truth-plugin", new ToolkitFileReader.PluginSpec("org/repo", "truth@repo"));
            store2.Add(plugin2); store2.Approve("truth-plugin");
            // No installed_plugins.json → probe reports Missing.
            var runner2 = new ToolkitRunner(store2, ctx2);
            var cmds2 = ToolkitRunner.InstallCommands(plugin2, null, ctx2);
            var plan2 = new[] { new ToolkitPlanItem { Entry = plugin2, Action = ToolkitPlanItem.PlanAction.Run, Commands = cmds2 } };
            var log2 = new List<IReadOnlyList<string>>();
            var results2 = runner2.Run(plan2, new FakeExecutor(log2));
            var r2 = results2.FirstOrDefault(r => r.EntryId == "truth-plugin");
            Check(r2 is not null, "case2: result present");
            // (a) verdict from probe (Missing → Failed), not from step outcomes (all ok)
            Check(r2!.RunVerdict == ToolkitRunItem.Verdict.Failed, "case2: verdict Failed from probe");
            // (b) steps are ok — explanation, not the verdict
            Check(r2.Steps.Count == 2, "case2: two steps");
            Check(r2.Steps[0].Outcome == ToolkitStepResult.StepOutcome.Ok, "case2: step0 Ok");
            Check(r2.Steps[1].Outcome == ToolkitStepResult.StepOutcome.Ok, "case2: step1 Ok");
        }
        finally { Directory.Delete(dir2, true); Directory.Delete(home2, true); }

        // ── Case 3: step 0 fails → step 1 is skipped; next entry still runs ─────
        var dir3 = TempDir(); var home3 = TempDir();
        try
        {
            var ctx3 = new ToolkitProbeContext { HomeDirectory = home3, PathDirectories = [], LocalAppData = home3 };
            var store3 = new ToolkitStore(dir3);
            const string failSrc3 = "org/truth-fail-step0";
            var plugin3 = MakeEntry("truth-plugin", new ToolkitFileReader.PluginSpec(failSrc3, "truth@repo"));
            var npm3 = MakeEntry("truth-npm", new ToolkitFileReader.PackageSpec("npm", "typescript"));
            store3.Add(plugin3); store3.Add(npm3);
            store3.Approve("truth-plugin"); store3.Approve("truth-npm");
            var runner3 = new ToolkitRunner(store3, ctx3);
            var plan3 = runner3.Plan().Where(i => i.Entry.Id is "truth-plugin" or "truth-npm").ToList();
            var log3 = new List<IReadOnlyList<string>>();
            var results3 = runner3.Run(plan3, new SpecificFailExecutor(log3, failSrc3));
            var pr3 = results3.FirstOrDefault(r => r.EntryId == "truth-plugin");
            Check(pr3 is not null, "case3: plugin result present");
            // (a) verdict from probe (no file → Failed)
            Check(pr3!.RunVerdict == ToolkitRunItem.Verdict.Failed, "case3: verdict Failed from probe");
            Check(pr3.Steps.Count == 2, "case3: plugin has 2 steps");
            // (c) step 0 Failed; step 1 is Skipped — never Failed
            Check(pr3.Steps[0].Outcome == ToolkitStepResult.StepOutcome.Failed, "case3: step0 Failed");
            Check(pr3.Steps[1].Outcome == ToolkitStepResult.StepOutcome.Skipped, "case3: step1 Skipped");
            // (d) failing entry did not stop the npm entry
            var nr3 = results3.FirstOrDefault(r => r.EntryId == "truth-npm");
            Check(nr3 is not null, "case3: npm result present — next entry ran (d)");
            Check(log3.Any(cmd => cmd.Any(a => a == "npm")), "case3: npm commands called (d)");
        }
        finally { Directory.Delete(dir3, true); Directory.Delete(home3, true); }

        // ── Case 4: step 0 ok / step 1 fails; next entry still runs ─────────────
        var dir4 = TempDir(); var home4 = TempDir();
        try
        {
            var ctx4 = new ToolkitProbeContext { HomeDirectory = home4, PathDirectories = [], LocalAppData = home4 };
            var store4 = new ToolkitStore(dir4);
            const string failID4 = "truth@fail-step1";
            var plugin4 = MakeEntry("truth-plugin", new ToolkitFileReader.PluginSpec("org/repo", failID4));
            var npm4 = MakeEntry("truth-npm", new ToolkitFileReader.PackageSpec("npm", "typescript"));
            store4.Add(plugin4); store4.Add(npm4);
            store4.Approve("truth-plugin"); store4.Approve("truth-npm");
            var runner4 = new ToolkitRunner(store4, ctx4);
            var plan4 = runner4.Plan().Where(i => i.Entry.Id is "truth-plugin" or "truth-npm").ToList();
            var log4 = new List<IReadOnlyList<string>>();
            var results4 = runner4.Run(plan4, new SpecificFailExecutor(log4, failID4));
            var pr4 = results4.FirstOrDefault(r => r.EntryId == "truth-plugin");
            Check(pr4 is not null, "case4: plugin result present");
            // (a) verdict from probe (no file → Failed)
            Check(pr4!.RunVerdict == ToolkitRunItem.Verdict.Failed, "case4: verdict Failed from probe");
            Check(pr4.Steps.Count == 2, "case4: plugin has 2 steps");
            Check(pr4.Steps[0].Outcome == ToolkitStepResult.StepOutcome.Ok, "case4: step0 Ok");
            Check(pr4.Steps[1].Outcome == ToolkitStepResult.StepOutcome.Failed, "case4: step1 Failed");
            // (d) failing entry did not stop the npm entry
            var nr4 = results4.FirstOrDefault(r => r.EntryId == "truth-npm");
            Check(nr4 is not null, "case4: npm result present — next entry ran (d)");
            Check(log4.Any(cmd => cmd.Any(a => a == "npm")), "case4: npm commands called (d)");
        }
        finally { Directory.Delete(dir4, true); Directory.Delete(home4, true); }

        // ── (e) macOS-only entry absent from plan and from the missing count ──────
        var dirE = TempDir();
        try
        {
            // brew is macOS-only; on Windows it must not appear in the plan or results.
            var storeE = new ToolkitStore(dirE);
            var brew = MakeEntry("macos-only-brew", new ToolkitFileReader.PackageSpec("brew", "ripgrep"));
            storeE.Add(brew); storeE.Approve("macos-only-brew");
            var runnerE = new ToolkitRunner(storeE, FakeContext());
            var planE = runnerE.Plan();
            // (e1) brew entry is absent from plan on Windows
            Check(!planE.Any(i => i.Entry.Id == "macos-only-brew"), "(e): brew absent from plan on Windows");
            // (e2) running the plan produces no result row for the brew entry
            var logE = new List<IReadOnlyList<string>>();
            var resultsE = runnerE.Run(planE, new FakeExecutor(logE));
            Check(!resultsE.Any(r => r.EntryId == "macos-only-brew"), "(e): brew absent from results on Windows");
            // (e3) brew command is never sent to the executor
            Check(!logE.Any(cmd => cmd.Any(a => a == "brew")), "(e): brew command never called");
        }
        finally { Directory.Delete(dirE, true); }

        return Task.CompletedTask;
    }

    // Always fails with a network-error message; counts calls per "binary subcmd" key.
    private sealed class NetworkFailExecutor(Dictionary<string, int> counts) : IToolkitRunnerExecutor
    {
        public ToolkitCommandOutput Run(IReadOnlyList<string> argv)
        {
            var key = argv.Count > 1 ? argv[0] + " " + argv[1] : (argv.Count > 0 ? argv[0] : "");
            counts[key] = counts.GetValueOrDefault(key, 0) + 1;
            return ToolkitCommandOutput.Failure("could not resolve host npmjs.com", 1);
        }
    }

    // Fake executor that simulates a successful git clone by creating SKILL.md at the destination.
    private sealed class SkillCreatingExecutor(List<IReadOnlyList<string>> log) : IToolkitRunnerExecutor
    {
        public ToolkitCommandOutput Run(IReadOnlyList<string> argv)
        {
            log.Add(argv);
            // git clone <url> <dest>  → create SKILL.md at dest so the probe returns Installed.
            if (argv.Count >= 4 && argv[0] == "git" && argv[1] == "clone")
            {
                var dest = argv[^1];
                Directory.CreateDirectory(dest);
                File.WriteAllText(Path.Combine(dest, "SKILL.md"), "# Test Skill");
            }
            return ToolkitCommandOutput.Success;
        }
    }
}
