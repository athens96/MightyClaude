using MightyClaude.Core;

/// <summary>
/// The rules the Windows app took from the Mac app's small places: which panes the status bar counts as
/// running, a shell pane's status, the components rows' buttons, the model reset and the pane cap.
/// </summary>
internal static class WindowsParityVerification
{
    private static void Check(bool value, string why) { if (!value) throw new InvalidOperationException(why); }
    private static RunSession Pane(string id, string kind, string status = "running") => new() { Id = id, WorkspaceId = "w", Kind = kind, Status = status };
    private static string? Text(string language, string key) => Locale.Catalogue(language).GetValueOrDefault(key);

    // M/WorkspaceView.swift:580, M/MightyCore/WorkDashboard.swift:47-49, 69-77.
    internal static Task RunningCountLeavesOutAgentAndFilePanes()
    {
        var sessions = new[] { Pane("agent", "claude"), Pane("shell", "shell"), Pane("web", "browser"), Pane("io-terminal", AgentIOPaneKind.Terminal), Pane("io-browser", AgentIOPaneKind.Browser), Pane("files", FilePaneKind.Kind), Pane("prompt", "shell", "idle") };
        Check(WorkDashboard.Count(sessions, _ => new()).Running == 3, "the running count is the agent, the shell and the browser pane: an agent's own terminal and browser and the files pane are left out");
        Check(sessions.Count(s => s.Status == "running") == 6, "the fixture must hold running panes the count leaves out");
        return Task.CompletedTask;
    }

    // M/MightyCore/ShellActivity.swift, M/LocalTerminalSession.swift:144, 204, 211-216, M/AppStore+Terminals.swift:25-27.
    internal static async Task ShellPaneStatusFollowsItsShell()
    {
        const int shell = 400; const long console = 4001;
        static ShellActivity.Entry Process(int id, long on = console) => new(id, on);
        Check(!ShellActivity.IsBusy(shell, [Process(shell)]), "a shell alone in its job is at its prompt");
        Check(ShellActivity.IsBusy(shell, [Process(shell), Process(12)]), "a process on the shell's console is a command running");
        Check(!ShellActivity.IsBusy(shell, [Process(shell), Process(12, on: shell)]), "a GUI program the shell started is on no console: the shell is at its prompt");
        Check(!ShellActivity.IsBusy(shell, [Process(shell), Process(16, on: 5001), Process(20, on: 5001)]), "a program in a console window of its own, and what runs in that window, are not on the terminal");
        Check(!ShellActivity.IsBusy(shell, [Process(shell), Process(24, on: 0)]), "a process whose console could not be read is not counted");
        Check(!ShellActivity.IsBusy(shell, [Process(shell, on: 0), Process(12, on: 0)]) && !ShellActivity.IsBusy(shell, [Process(12)]), "with the shell's console unknown nothing is running");
        Check(ShellActivity.IsBusy(shell, [Process(shell), Process(16, on: 5001), Process(28)]), "a command on the terminal beside a windowed program is running");

        Check(ShellActivity.NextStatus("idle", true) == "running" && ShellActivity.NextStatus("running", false) == "idle", "a reading moves the pane between idle and running");
        Check(ShellActivity.NextStatus("idle", false) is null && ShellActivity.NextStatus("running", true) is null, "only a different status is reported");
        Check(ShellActivity.NextStatus("idle", null) is null && ShellActivity.NextStatus("running", null) is null, "a reading that could not be taken changes nothing");
        Check(ShellActivity.NextStatus("completed", true) is null && ShellActivity.NextStatus("error", false) is null, "a pane that has exited or failed keeps that");

        var directory = Verification.Temp(); var workspacePath = Verification.Temp();
        await using var catalog = new ProviderCatalog((_, _) => Task.FromResult<CliCommand?>(null));
        try
        {
            string shellId;
            var service = new DesktopService(directory, null, "", catalog);
            try
            {
                await service.InitializeAsync();
                var workspace = await service.AddWorkspaceAsync(workspacePath);
                var pane = new RunSession { WorkspaceId = workspace.Id, Kind = "shell", Title = "shell" }; var agent = new RunSession { WorkspaceId = workspace.Id, Title = "agent" };
                shellId = pane.Id;
                await service.UpdateAsync(s => s with { Sessions = [pane, agent] });
                var events = 0; service.RunEventReceived += _ => events++;
                Check(service.SessionStatus(pane.Id) == "idle" && !service.SetShellStatus(pane.Id, "idle"), "the status a pane already has is no change");
                Check(service.SetShellStatus(pane.Id, "running") && service.SessionStatus(pane.Id) == "running", "a command running sets the pane running");
                Check(WorkDashboard.Count(service.Snapshot.Sessions, _ => new()).Running == 1, "a running shell pane is counted, as on the Mac");
                var file = Path.Combine(directory, "workspace-state.json");
                Check(service.SaveStatus == "saved" && !File.ReadAllText(file).Contains("\"running\"", StringComparison.Ordinal), "idle and running are not saved: a restart reads a running shell as idle anyway");
                Check(service.SetShellStatus(pane.Id, "completed") && service.SetShellStatus(pane.Id, "error") && service.SetShellStatus(pane.Id, "idle"), "exit, failure and restart are written");
                await Verification.Until(() => service.SaveStatus == "saved", 5000);
                Check(File.ReadAllText(file).Contains("\"error\"", StringComparison.Ordinal), "an exit or a failure is saved");
                Check(!service.SetShellStatus(agent.Id, "running") && service.SessionStatus(agent.Id) == "idle", "only a shell pane takes a terminal's status");
                Check(!service.SetShellStatus(pane.Id, "stopped") && !service.SetShellStatus("missing", "running") && service.SessionStatus("missing") is null, "an unknown status or pane changes nothing");
                Check(events == 0, "a shell pane's status is not a run event");
                service.SetShellStatus(pane.Id, "running");
            }
            finally { await service.DisposeAsync(); }
            // A shell that was running a command when the app closed is at a new prompt next time (M/MightyCore/StateStore.swift:201).
            var restored = new DesktopService(directory, null, "", catalog);
            try
            {
                await restored.InitializeAsync();
                Check(restored.SessionStatus(shellId) == "idle", "a shell pane saved as running must be idle after a restart; got " + restored.SessionStatus(shellId));
            }
            finally { await restored.DisposeAsync(); }
        }
        finally { Directory.Delete(directory, true); Directory.Delete(workspacePath, true); }
    }

    /// <summary>The real reading: a cmd shell in ConPTY is idle at its prompt, running while ping runs under it, and idle again after.</summary>
    internal static async Task RealShellReadsRunningOnlyWhileACommandRuns()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("This verification needs real Windows ConPTY.");
        var path = Verification.Temp();
        try
        {
            var cmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
            var terminal = PseudoTerminal.Start(path, _ => { }, executable: cmd, arguments: ["/d", "/q"]);
            try
            {
                Check(terminal.JobProcessIds() is { } job && job.Contains(terminal.ProcessId) && ShellProcessProbe.Console(terminal.ProcessId) is not null, "the shell's job must hold the shell, and its console must be readable");
                await Verification.Until(() => ShellProcessProbe.IsBusy(terminal) == false, 15000);
                Check(terminal.TryWrite("ping -n 6 127.0.0.1 >nul\r"), "The command was rejected.");
                await Verification.Until(() => ShellProcessProbe.IsBusy(terminal) == true, 15000);
                await Verification.Until(() => ShellProcessProbe.IsBusy(terminal) == false, 30000);
                // A GUI program the shell starts (wscript is one, and shows no window with //B) runs in the job but on no console: the shell is at its prompt.
                File.WriteAllText(Path.Combine(path, "wait.js"), "WScript.Sleep(8000);");
                Check(terminal.TryWrite("wscript //B //Nologo wait.js\r"), "The GUI program was rejected.");
                await Verification.Until(() => terminal.JobProcessIds() is { Count: > 1 }, 15000);
                Check(ShellProcessProbe.IsBusy(terminal) == false, "a GUI program started from the shell must leave its pane idle");
            }
            finally { await terminal.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15)); }
        }
        // The GUI program (wscript) can outlive the pane for a moment and still hold the
        // folder; the temp folder goes once it lets go, and a failure to tidy it is no failure of the test.
        finally { await DeleteWhenFree(path); }
    }

    private static async Task DeleteWhenFree(string path)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, true); return; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { await Task.Delay(250); }
        }
    }

    // M/AppStore+Components.swift:29, 34, 57-62, M/ComponentsSettingsView.swift:28-38.
    internal static Task ComponentActionsCarryTheirOwnTitles()
    {
        static ProviderRuntime Provider(string id, bool available, string? version) => new(id, id, available, version, "", new ModelCatalog("cli", [], ""), new ProviderCapabilities(true, [], true, true, true));
        static RuntimeInfo Runtime(params ProviderRuntime[] providers) => new("win32", "1.0.0", false, null, null, providers.ToList(), null);
        var missing = ComponentSection.SectionRows(Runtime());
        Check(missing.All(row => row.Actions is [{ Id: "copy-command" } copy] && copy.Title == Locale.Get("settings.components.copyInstallCommand")), "a missing CLI's button must say what it does, copy the install command");
        var old = ComponentSection.SectionRows(Runtime(Provider("claude", true, "2.1.263"), Provider("codex", true, "0.153.4"), Provider("gemini", true, "1.0.0")));
        Check(old.First(row => row.Id == "claude").Actions is [{ Id: "update" } update] && update.Title == Locale.Get("settings.components.updateCLI"), "a Claude below the Mods minimum must offer the CLI update by name");
        // M/AppStore+Components.swift:20-40: the detail sentences are the Mac's, case by case.
        string Install(string id) => Locale.Get("settings.components.installInTerminal", new Dictionary<string, string> { ["command"] = ComponentSection.InstallCommand(id)! });
        Check(missing.All(row => row.Detail == Locale.Get("provider.fallback.checkInstall") + Install(row.Id)), "a CLI not looked for yet says the Mac's fallback words and the command to install it");
        var absent = ComponentSection.SectionRows(Runtime(Provider("claude", false, null) with { Detail = "not found" }));
        Check(absent.First(row => row.Id == "claude").Detail == "not found" + Install("claude"), "a missing CLI says its runtime's words and the command to install it");
        Check(old.First(row => row.Id == "claude").Detail == Locale.Get("settings.components.modsNeedsVersion", new Dictionary<string, string> { ["version"] = "2.1.271" }), "an old Claude names the Mods version it needs");
        var ready = ComponentSection.SectionRows(Runtime(Provider("claude", true, "2.1.271"), Provider("codex", true, "0.153.4"), Provider("gemini", true, "1.0.0")));
        Check(ready.Select(row => row.Detail).SequenceEqual(new[] { Locale.Get("settings.components.readyClaude"), Locale.Get("settings.components.readyCodex"), Locale.Get("settings.components.ready") }), "each ready CLI says its own ready sentence");
        foreach (var language in Locale.Languages)
            foreach (var key in new[] { "settings.components.copyInstallCommand", "settings.components.updateCLI" })
                foreach (var state in new[] { "settings.components.statusMissing", "settings.components.statusAttention" })
                    Check(Text(language, key) is { Length: > 0 } title && title != Text(language, state), language + ": a button's title must not be a state word: " + key);
        Check(ComponentSection.CopiedMessage("npm install -g x") == Locale.Get("settings.components.copied", new Dictionary<string, string> { ["command"] = "npm install -g x" }) && ComponentSection.CopiedMessage("npm install -g x").Contains("npm install -g x", StringComparison.Ordinal), "the confirmation must name the command that was copied");
        Check(ComponentSection.RecheckInterval == TimeSpan.FromSeconds(5), "the open section looks again every 5 s, as on the Mac");
        Check(ComponentSection.NeedsRecheck(missing) && ComponentSection.NeedsRecheck(old), "a row that still needs the user keeps the section looking");
        Check(!ComponentSection.NeedsRecheck(ComponentSection.SectionRows(Runtime(Provider("claude", true, "2.1.271"), Provider("codex", true, "0.153.4"), Provider("gemini", true, "1.0.0")))), "with every CLI ready there is nothing to look for");
        return Task.CompletedTask;
    }

    // M/AppStore+ClaudeModelReset.swift:16-47.
    internal static async Task ModelResetReadsEveryWorkspaceAndSaysHowItWent()
    {
        var workspaces = new[] { new Workspace { Name = "a", Path = "a" }, new Workspace { Name = "b", Path = "b" }, new Workspace { Name = "c", Path = "c" } };
        var state = new AppSnapshot
        {
            Workspaces = workspaces.ToList(),
            Sessions =
            [
                new() { Id = "claude", WorkspaceId = workspaces[0].Id, Model = "opus", Settings = new(Effort: "high", PermissionMode: "acceptEdits"), Logs = [new("entry", "user", "kept", "2026-10-01T10:00:00Z")] },
                new() { Id = "codex", WorkspaceId = workspaces[1].Id, Provider = "codex", Model = "gpt-5.6-sol", Settings = new(Effort: "high") },
                new() { Id = "shell", WorkspaceId = workspaces[2].Id, Kind = "shell", Model = "opus" },
            ],
        };
        var reset = ClaudeModelReset.Apply(state).Sessions;
        Check(reset[0] is { Model: "default", Settings: { Effort: "default", PermissionMode: "acceptEdits" }, Logs.Count: 1 }, "a Claude pane goes back to the default model and effort and keeps the rest");
        Check(reset[1] is { Model: "gpt-5.6-sol", Settings.Effort: "high" } && reset[2].Model == "opus", "only Claude panes are reset");

        static Dictionary<string, string> Count(int value) => new() { ["count"] = value.ToString() };
        var read = new List<string>();
        Task<ModelCatalog> Read(Workspace workspace, string failing) { lock (read) read.Add(workspace.Path); return Task.FromResult(workspace.Path == failing ? ProviderCatalog.Fallback("claude") : new ModelCatalog("cli", [], "")); }
        Check(await ClaudeModelReset.ReloadAsync(workspaces, (workspace, _) => Read(workspace, "")) == Locale.Get("settings.cliAccounts.modelReset.done", Count(3)), "every list read from the CLI: the sentence counts the workspaces");
        Check(read.Order().SequenceEqual(new[] { "a", "b", "c" }), "every workspace's list is read, not the active one's alone");
        Check(await ClaudeModelReset.ReloadAsync(workspaces, (workspace, _) => Read(workspace, "b")) == Locale.Get("settings.cliAccounts.modelReset.partial", Count(1)), "a list that did not come from the CLI is counted as not read");
        Check(await ClaudeModelReset.ReloadAsync([], (workspace, _) => Read(workspace, "")) == Locale.Get("settings.cliAccounts.modelReset.cacheCleared"), "with no workspace there is nothing to read");
        Check(ClaudeModelReset.Result(3, 0).Contains('3') && ClaudeModelReset.Result(3, 2).Contains('2') && ClaudeModelReset.Result(3, 0) != ClaudeModelReset.Result(3, 2), "the two sentences carry their own counts");

        // A read whose time runs out says so, not that a sign-in is missing; the reads start off the caller's thread, a few at a time.
        async Task<ModelCatalog> Hangs(CancellationToken token) { try { await Task.Delay(Timeout.Infinite, token); } catch (OperationCanceledException) { } return ProviderCatalog.Fallback("claude"); }
        var timedOut = await ClaudeModelReset.ReloadAsync(workspaces, (workspace, token) => workspace.Path == "a" ? Hangs(token) : Read(workspace, "c"), timeout: TimeSpan.FromMilliseconds(200));
        Check(timedOut == Locale.Get("settings.cliAccounts.modelReset.partial", Count(1)) + " " + Locale.Get("settings.cliAccounts.modelReset.timedOut", Count(1)), "a timed-out list and a refused one are each said as what they are; got " + timedOut);
        Check(ClaudeModelReset.Result(2, 0, 2) == Locale.Get("settings.cliAccounts.modelReset.timedOut", Count(2)) && !ClaudeModelReset.Result(2, 0, 2).Equals(ClaudeModelReset.Result(2, 2), StringComparison.Ordinal), "only timeouts: the timeout sentence alone");
        // Started from a thread of its own (the UI thread's stand-in): a read made in place would run on it.
        var running = 0; var most = 0; var pooled = new List<bool>();
        var many = Enumerable.Range(0, 8).Select(i => new Workspace { Name = "w" + i, Path = "w" + i }).ToList();
        Task<string>? reload = null;
        var caller = new Thread(() => reload = ClaudeModelReset.ReloadAsync(many, async (_, _) =>
        {
            lock (pooled) { pooled.Add(Thread.CurrentThread.IsThreadPoolThread); most = Math.Max(most, ++running); }
            await Task.Delay(30);
            lock (pooled) running--;
            return new ModelCatalog("cli", [], "");
        }));
        caller.Start(); caller.Join(); await reload!;
        Check(most <= ClaudeModelReset.Concurrency && most > 1, "at most " + ClaudeModelReset.Concurrency + " lists are read at once; got " + most);
        Check(pooled.Count == 8 && pooled.All(onPool => onPool), "every read starts on the thread pool, never on the caller's thread");
        using var closed = new CancellationTokenSource(); closed.Cancel();
        var cancelled = false;
        try { await ClaudeModelReset.ReloadAsync(workspaces, (workspace, token) => Hangs(token), closed.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled, "a reset whose window closed ends without a sentence");
        foreach (var language in Locale.Languages)
            Check(Text(language, "settings.cliAccounts.modelReset.timedOut") is { Length: > 0 } sentence && sentence.Contains("{count}", StringComparison.Ordinal), language + ": the timeout sentence must carry its count");
    }

    // M/AppStore.swift:573, M/MightyCore/PaneLayout.swift:37.
    internal static Task TheHundredAndTwentyNinthPaneIsRefused()
    {
        static AppSnapshot With(int panes) => new() { Sessions = Enumerable.Range(0, panes).Select(i => Pane("p" + i, i % 2 == 0 ? "claude" : "shell", "idle")).ToList() };
        Check(PaneLayout.MaximumSessions == 128, "the cap is the Mac's 128");
        Check(PaneLayout.HasRoom(With(0)) && PaneLayout.HasRoom(With(127)), "the 128th pane may be added");
        Check(!PaneLayout.HasRoom(With(128)) && !PaneLayout.HasRoom(With(129)), "the 129th pane is refused");
        foreach (var language in Locale.Languages)
            Check(Text(language, "workspace.paneLimit") is { } sentence && sentence.Contains("128", StringComparison.Ordinal), language + ": the refusal must name the cap");
        return Task.CompletedTask;
    }

    // M/PaneDockDrag.swift:79-82, M/PaneDockView.swift:8-15.
    internal static Task TabDropZonesFollowTheMac()
    {
        Check(PaneLayout.DropZone(300, 10, 600, 400) == "center" && PaneLayout.DropZone(5, 35, 600, 400) == "center", "the strip over the tabs joins them, its empty end too, never splitting above");
        Check(PaneLayout.DropZone(300, 36, 600, 200) == "top" && PaneLayout.DropZone(300, 200, 600, 400) == "center", "under the strip the top edge splits above; the middle joins the tabs");
        Check(PaneLayout.DropZone(100, 200, 600, 400) == "left" && PaneLayout.DropZone(520, 200, 600, 400) == "right" && PaneLayout.DropZone(300, 330, 600, 400) == "bottom", "an edge within 24% of the group splits that way");
        Check(PaneLayout.DropZone(150, 200, 600, 400) == "center" && PaneLayout.DropZone(143, 200, 600, 400) == "left", "24% is the edge's share (0.25 of the width joins, 0.238 splits)");
        Check(PaneLayout.DropZone(40, 360, 600, 400) == "left" && PaneLayout.DropZone(60, 380, 600, 400) == "bottom", "the nearest edge wins, not the first in a fixed order");
        Check(PaneLayout.DropZone(60, 60, 600, 600) == "left" && PaneLayout.DropZone(540, 540, 600, 600) == "right", "a tie goes left, right, top, bottom, as on the Mac");
        Check(PaneLayout.DropZone(-50, 500, 600, 400) == "left" && PaneLayout.DropZone(10, 100, 0, 400) == "center", "a point outside is clamped in; a group with no size joins");
        return Task.CompletedTask;
    }

    // M/FilePaneView.swift:384-385 (AppKit's find bar).
    internal static Task SourceFindMatchesAndSteps()
    {
        var matches = SourceFind.Matches("let Foo = foo + FOO; fo", "foo");
        Check(matches.SequenceEqual(new[] { 4, 10, 16 }), "matches ignore case, in order");
        Check(SourceFind.Matches("aaaa", "aa").SequenceEqual(new[] { 0, 2 }) && SourceFind.Matches("abc", "").Count == 0 && SourceFind.Matches("abc", "abcd").Count == 0, "matches do not overlap; nothing is found for nothing or for more than the text");
        Check(SourceFind.Matches(new string('a', 5_000), "a").Count == SourceFind.MaximumMatches, "only the first " + SourceFind.MaximumMatches + " are marked");
        Check(SourceFind.Step(matches, -1, true) == 0 && SourceFind.Step(matches, -1, true, from: 10) == 1 && SourceFind.Step(matches, -1, true, from: 17) == 0, "a new search goes to the first match from where the last one was, wrapping round");
        Check(SourceFind.Step(matches, -1, false, from: 10) == 0, "back from a place goes to the match before it");
        Check(SourceFind.Step(matches, 2, true) == 0 && SourceFind.Step(matches, 0, false) == 2 && SourceFind.Step(matches, 1, true) == 2, "Enter goes to the next and Shift+Enter to the previous, wrapping round");
        Check(SourceFind.Step([], -1, true) == -1 && SourceFind.Step([], 0, false) == -1, "with no match there is nowhere to go");
        foreach (var language in Locale.Languages)
            foreach (var key in new[] { "files.find.placeholder", "files.find.previous", "files.find.next", "files.find.done" })
                Check(Text(language, key) is { Length: > 0 }, language + ": the find bar's words must be there: " + key);
        return Task.CompletedTask;
    }

    // M/MightyCore/ResourceHealthChecker.swift, M/AppStore.swift:420-431.
    internal static Task MissingResourcesAreNamedInTheBand()
    {
        var root = Verification.Temp();
        try
        {
            var all = ResourceHealth.Missing(root);
            Check(all.SequenceEqual(new[] { "pets/mighty-raccoon" }.Concat(ResourceHealth.TerminalFiles.Select(name => "Assets/Terminal/" + name)).Append("claude-mods/.claude-plugin/plugin.json")), "an empty folder misses every resource, named in order");
            var pet = Directory.CreateDirectory(Path.Combine(root, "Assets", "pets", "mighty-raccoon")).FullName;
            File.WriteAllText(Path.Combine(pet, "pet.json"), """{ "spritesheetPath": "" }""");
            Check(ResourceHealth.Missing(root).Contains("pets/mighty-raccoon"), "a pet with no spritesheet is missing, as on the Mac");
            File.WriteAllText(Path.Combine(pet, "pet.json"), """{ "spritesheetPath": "spritesheet.png" }""");
            var terminal = Directory.CreateDirectory(Path.Combine(root, "Assets", "Terminal")).FullName;
            foreach (var name in ResourceHealth.TerminalFiles) File.WriteAllText(Path.Combine(terminal, name), "x");
            Directory.CreateDirectory(Path.Combine(root, "claude-mods", ".claude-plugin"));
            File.WriteAllText(Path.Combine(root, "claude-mods", ".claude-plugin", "plugin.json"), "{}");
            Check(ResourceHealth.Missing(root).Count == 0 && ResourceHealth.Warning([]) is null, "with every file there, there is no band");
            File.Delete(Path.Combine(terminal, "xterm.js"));
            Check(ResourceHealth.Warning(ResourceHealth.Missing(root)) == Locale.Get("store.error.resourcesMissing", new Dictionary<string, string> { ["names"] = "Assets/Terminal/xterm.js" }), "the band names what is missing in the Mac's sentence");
            // Every terminal file checked is one the app ships (the source folder the build copies).
            for (var up = new DirectoryInfo(AppContext.BaseDirectory); up is not null; up = up.Parent)
                if (Path.Combine(up.FullName, "native", "windows", "MightyClaude.WinUI", "Assets", "Terminal") is var source && Directory.Exists(source))
                { Check(ResourceHealth.TerminalFiles.All(name => File.Exists(Path.Combine(source, name))), "every terminal file checked must be one the app ships"); break; }
        }
        finally { Directory.Delete(root, true); }
        return Task.CompletedTask;
    }
}
