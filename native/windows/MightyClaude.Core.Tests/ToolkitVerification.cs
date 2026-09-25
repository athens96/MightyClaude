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

    // ── 6: toolkit probes are file-only ──────────────────────────────────────

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
        return Task.CompletedTask;
    }

    // ── 7: components rows follow installed CLIs ──────────────────────────────

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
}
