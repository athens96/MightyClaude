using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using MightyClaude.Core;

internal static class RemoteDesktopVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    internal static async Task ARemotePaneRunsThroughTheClientAndThePhoneNeverSeesIt()
    {
        await using var fixture = await RemoteHostVerification.MakeHost();
        var state = await fixture.Host.StartAsync([fixture.Shared.Id], 0); fixture.Host.SetAllowsFullAccess(true);
        var directory = Verification.Temp(); var local = Verification.Temp();
        try
        {
            await using var service = new DesktopService(Path.Combine(directory, "state"), null, directory, delegationEnabled: () => true); await service.InitializeAsync();
            var client = service.AttachRemote(Path.Combine(directory, "state"), new MemoryRemoteKeyVault(), new() { AllowLoopbackForTests = true });
            var near = await service.AddWorkspaceAsync(local);
            var info = await client.ConnectAsync("", "127.0.0.1:" + state.Port, state.Key!);
            var far = await service.OpenRemoteWorkspaceAsync(info.Id, fixture.Shared.Id);
            Check(service.IsRemoteWorkspace(far.Id) && !service.IsRemoteWorkspace(near.Id) && far.Path == fixture.Shared.Path, "the host's workspace is listed as remote with the host's path");
            var again = await service.OpenRemoteWorkspaceAsync(info.Id, fixture.Shared.Id);
            Check(again.Id == far.Id && service.Snapshot.Workspaces.Count(w => w.Remote is not null) == 1, "opening it again keeps one entry");
            var permissions = new ConcurrentQueue<ToolPermissionRequest>(); service.ToolPermissionChanged += permissions.Enqueue;
            await service.UpdateAsync(s => s with { Sessions = [new RunSession { Id = "cmd", WorkspaceId = far.Id, Kind = "shell" }, new RunSession { Id = "agent", WorkspaceId = far.Id }, new RunSession { Id = "mine", WorkspaceId = near.Id }] });
            Check(service.IsRemoteSession("cmd") && service.IsRemoteSession("agent") && !service.IsRemoteSession("mine"), "panes are remote by their workspace");
            Check(service.RemoteReference("cmd") == far.Remote && service.RemoteReference("agent") == far.Remote && service.RemoteReference("mine") is null && service.RemoteReference("gone") is null,
                "a pane's host is its workspace's, read without a copy of the state");

            await service.StartAsync(new StartRunRequest("cmd", far.Id, "shell", "dir", []));
            await Verification.Until(() => service.Snapshot.Sessions.Single(s => s.Id == "cmd").Status == "completed");
            Check(service.Snapshot.Sessions.Single(s => s.Id == "cmd").Logs.Any(l => l.Text == "echo:dir"), "the host's output is the pane's");

            await service.StartAsync(new StartRunRequest("agent", far.Id, "claude", "ask", []));
            await Verification.Until(() => permissions.Any(p => p.RunId == "agent" && p.State == "pending"));
            service.RespondToToolPermission("agent", "perm-1", true);
            await Verification.Until(() => fixture.Runner!.Answers.Any(a => a.Request == "perm-1" && a.Allow));
            await Verification.Until(() => service.Snapshot.Sessions.Single(s => s.Id == "agent").Status == "completed");

            await service.StartAsync(new StartRunRequest("agent", far.Id, "claude", "hold", []));
            await Verification.Until(() => service.IsSessionRunning("agent"));
            Check(!service.HasActiveProvider("claude"), "a remote run never holds this PC's CLI updates");
            var pane = service.Snapshot.Sessions.Single(s => s.Id == "agent");
            Check(service.SecondSend(pane) != ComposerSecondSend.Worktree && !service.OffersWorktreeMenu(pane, false), "a remote pane never offers a child worktree");
            var child = await service.StartHumanChildAsync("agent", "work", [], false, _ => Task.CompletedTask);
            Check(child is HumanChildStart.Failed { Message: var refusal } && refusal == Locale.Get("remote.pane.agentNote", new Dictionary<string, string> { ["host"] = far.Remote!.HostName }),
                "a remote pane never makes a child: " + child);
            Check(await ((IDelegationHost)service).PaneState("agent") is null && await ((IDelegationHost)service).PaneState("mine") is not null, "delegation never reads a remote pane, nor takes its host's path for a folder here");
            Check(service.WorkspaceRemovalRefusal(far.Id) is null, "a remote workspace has no children here");
            Check(service.AnswerPlan("agent", "plan-x", PlanDecision.Cancel) is null, "a remote pane's plan answer goes to the host, its record comes back as an event");
            await service.StopAsync("agent");
            await Verification.Until(() => service.Snapshot.Sessions.Single(s => s.Id == "agent").Status == "stopped");
            Check(fixture.Runner!.Stopped.Count == 1, "the stop reached the host");
            await Verification.Until(() => !service.IsSessionRunning("agent"));

            using var router = new MobileDesktopRouter(service, Path.Combine(directory, "mobile"), "Fixture", (_, _) => Task.FromResult<object?>("created"), _ => new([]));
            router.Observe();
            var scopes = router.TakeNotifications().Select(n => n.Scope).ToList();
            Check(scopes.Contains("session:mine") && !scopes.Contains("session:cmd") && !scopes.Contains("session:agent"), "the phone is told of this PC's panes only: " + string.Join(",", scopes));
            var reply = JsonSerializer.Serialize(await router.RouteAsync("GET", "/m1/state?since=0&wait=0", null, "device-1", CancellationToken.None), Wire.Json);
            Check(reply.Contains(near.Id, StringComparison.Ordinal) && !reply.Contains(far.Id, StringComparison.Ordinal) && !reply.Contains("\"cmd\"", StringComparison.Ordinal) && !reply.Contains("\"agent\"", StringComparison.Ordinal),
                "the phone's state leaves remote workspaces out: " + reply);
            foreach (var (method, path, body) in new (string, string, string?)[]
            {
                ("GET", "/m1/sessions/agent", null), ("GET", "/m1/sessions/agent/entries", null), ("GET", "/m1/sessions/cmd/entries", null), ("POST", "/m1/sessions/agent/stop", "{}"),
                ("GET", $"/m1/workspaces/{far.Id}/files", null), ("GET", $"/m1/workspaces/{far.Id}/file?path=a.txt", null), ("POST", $"/m1/workspaces/{far.Id}/sessions", "{\"kind\":\"claude\"}"),
                ("POST", "/m1/sessions/agent/permission", "{\"requestId\":\"perm-1\",\"runId\":\"agent\",\"allow\":true}"),
                ("POST", "/m1/sessions/agent/answers", "{\"requestId\":\"perm-1\",\"runId\":\"agent\",\"answers\":{}}"),
                ("POST", "/m1/sessions/agent/plan", "{\"requestId\":\"perm-1\",\"runId\":\"agent\",\"decision\":\"cancel\"}"),
                ("POST", "/m1/sessions/agent/submit", "{\"text\":\"hello\"}"), ("POST", "/m1/sessions/agent/uploads", "{\"size\":1,\"name\":\"a.txt\"}"),
                ("GET", "/m1/sessions/agent/commands", null), ("POST", "/m1/sessions/agent/queue/run-next", "{}"), ("POST", "/m1/sessions/agent/queue/item-1/remove", "{}"),
                ("POST", "/m1/sessions/agent/settings", "{\"agentViewMode\":\"mighty\"}"), ("POST", "/m1/sessions/agent/rename", "{\"title\":\"x\"}"), ("POST", "/m1/sessions/agent/close", "{}"),
            })
            {
                var answer = await router.RouteAsync(method, path, body is null ? null : JsonDocument.Parse(body).RootElement, "device-1", CancellationToken.None);
                Check(answer.Status == 404, $"the phone never reaches a remote workspace or pane: {method} {path} -> {answer.Status} {JsonSerializer.Serialize(answer.Body, Wire.Json)}");
            }
            // The host's folder exists on this PC in this check, so a files route that took the remote path would list it.
            File.WriteAllText(Path.Combine(fixture.Shared.Path, "secret.txt"), "host");
            var listed = await router.RouteAsync("GET", $"/m1/workspaces/{far.Id}/files", null, "device-1", CancellationToken.None);
            Check(!JsonSerializer.Serialize(listed.Body, Wire.Json).Contains("secret.txt", StringComparison.Ordinal), "a remote workspace's path is never read as a folder here");
            var mine = await router.RouteAsync("GET", "/m1/sessions/mine", null, "device-1", CancellationToken.None);
            Check(mine.Status == 200, "this PC's own pane still reaches the phone: " + mine.Status);
        }
        finally { Directory.Delete(directory, true); Directory.Delete(local, true); }
    }

    internal static async Task TheSharingHostRunsOnlyInSharedFoldersWithNoPaneTools()
    {
        var directory = Verification.Temp();
        try
        {
            var plugin = Path.Combine(directory, "plugin"); Directory.CreateDirectory(Path.Combine(plugin, ".claude-plugin")); await File.WriteAllTextAsync(Path.Combine(plugin, ".claude-plugin", "plugin.json"), "{}");
            await using var catalog = new ProviderCatalog((_, _) => Task.FromResult<CliCommand?>(null));
            await using var service = new DesktopService(Path.Combine(directory, "state"), null, plugin, catalog, delegationEnabled: () => true); await service.InitializeAsync();
            var shared = await service.AddWorkspaceAsync(Directory.CreateDirectory(Path.Combine(directory, "project")).FullName);
            var hidden = await service.AddWorkspaceAsync(Directory.CreateDirectory(Path.Combine(directory, "private")).FullName);
            var counts = new ConcurrentQueue<int>();
            var options = service.HostOptions("studio-pc", "1.2.3", _ => null, counts.Enqueue, allowLoopbackForTests: true);
            Check(options.AllowLoopbackForTests && options.HostName == "studio-pc" && options.Workspaces().Select(w => w.Id).SequenceEqual(service.Snapshot.Workspaces.Select(w => w.Id)), "the host reads this PC's saved workspaces");

            // One reading of this PC's CLIs answers every caller while it goes and for a while after.
            var first = options.Runtime(); var second = options.Runtime();
            var runtime = await first;
            Check(ReferenceEquals(first, second) && ReferenceEquals(options.Runtime(), first), "callers share one reading of this PC's CLIs");
            Check(runtime.AppVersion == "1.2.3" && runtime.Providers.Count == Wire.Providers.Length, "the reading carries the app's version and every CLI");

            RemoteRunnerSinks? sinks = null; IRemoteRunner? runner = null; var events = new ConcurrentQueue<RunEvent>();
            await using var host = new RemoteHostService(options with
            {
                CreateRunner = given => { sinks = given; return runner = options.CreateRunner(given with { Event = value => { events.Enqueue(value); given.Event(value); } }); },
            });
            await host.StartAsync([shared.Id], 0);
            var manager = runner as RunManager ?? throw new InvalidOperationException("the host's runner is this PC's RunManager");
            var found = sinks ?? throw new InvalidOperationException("the host made its runner");
            Check(manager.AgentIOBindingFactory is null && manager.DelegationHandler is null && !manager.DelegationEnabled() && manager.PowerShellCommands && manager.GraphNodes == found.GraphNode,
                "the host's runner has no pane tools, no delegation, PowerShell commands, and hands graph nodes to the host");
            Check((await found.Workspace(shared.Id)).Path == shared.Path, "the runner finds the shared workspace at its shared path");
            var refused = false;
            try { await found.Workspace(hidden.Id); } catch (InvalidOperationException) { refused = true; }
            Check(refused, "the runner never finds a workspace that is not shared");

            // The runner's own look-up at launch goes through the shared check: a run named for another registered folder never starts there.
            await manager.StartAsync(new StartRunRequest("job-hidden", hidden.Id, "shell", "echo leaked", []));
            await manager.StartAsync(new StartRunRequest("job-shared", shared.Id, "shell", "echo shared-ok", []));
            await Verification.Until(() => events.Any(e => e.SessionId == "job-hidden" && e.Status is "completed" or "error") && events.Any(e => e.SessionId == "job-shared" && e.Status is "completed" or "error"), 20000);
            Check(events.Any(e => e.SessionId == "job-hidden" && e.Status == "error") && !events.Any(e => e.SessionId == "job-hidden" && e.Entry?.Text.Contains("leaked", StringComparison.Ordinal) == true),
                "a run for an unshared folder fails before it starts: " + string.Join(" | ", events.Where(e => e.SessionId == "job-hidden").Select(e => e.Status ?? e.Entry?.Text)));
            Check(events.Any(e => e.SessionId == "job-shared" && e.Entry?.Text.Contains("shared-ok", StringComparison.Ordinal) == true), "a run in the shared folder runs");

            await host.StopAsync();
            refused = false;
            try { await found.Workspace(shared.Id); } catch (Exception ex) when (ex is not OutOfMemoryException) { refused = ex.Message == Locale.Get("remote.host.error.stopped"); }
            Check(refused, "once sharing stops, its runner finds nothing: sharing has stopped");
            Check(counts.LastOrDefault() == 0, "the host tells the app how many client runs go on");
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>A remote pane's start never enters the client while holding the service's lock: a route check that blocks leaves every other pane free.</summary>
    internal static async Task NoClientCodeRunsUnderTheServicesLock()
    {
        await using var fixture = await RemoteHostVerification.MakeHost();
        var state = await fixture.Host.StartAsync([fixture.Shared.Id], 0);
        var directory = Verification.Temp(); var local = Verification.Temp();
        using var entered = new ManualResetEventSlim(false); using var hold = new ManualResetEventSlim(false); var armed = 0;
        try
        {
            await using var catalog = new ProviderCatalog((_, _) => Task.FromResult<CliCommand?>(null));
            await using var service = new DesktopService(Path.Combine(directory, "state"), null, directory, catalog); await service.InitializeAsync();
            var client = service.AttachRemote(Path.Combine(directory, "state"), new MemoryRemoteKeyVault(), new()
            {
                AllowLoopbackForTests = true,
                RouteCheck = _ => { if (Volatile.Read(ref armed) == 1) { entered.Set(); hold.Wait(TimeSpan.FromSeconds(20)); } return true; },
            });
            var near = await service.AddWorkspaceAsync(local);
            var info = await client.ConnectAsync("", "127.0.0.1:" + state.Port, state.Key!);
            var far = await service.OpenRemoteWorkspaceAsync(info.Id, fixture.Shared.Id);
            await service.UpdateAsync(s => s with { Sessions = [new RunSession { Id = "far-agent", WorkspaceId = far.Id }, new RunSession { Id = "near-cmd", WorkspaceId = near.Id, Kind = "shell" }] });
            var events = new ConcurrentQueue<RunEvent>(); service.RunEventReceived += events.Enqueue;

            Volatile.Write(ref armed, 1);
            var starting = Task.Run(() => service.StartAsync(new StartRunRequest("far-agent", far.Id, "claude", "hold", [])));
            Check(entered.Wait(TimeSpan.FromSeconds(10)), "the remote start reached the client's route check");
            Check(Task.Run(() => service.Snapshot).Wait(TimeSpan.FromSeconds(2)), "the service's lock is free while the client checks the route");
            // Another pane's run reaches Receive while the remote start waits on the client.
            await service.StartAsync(new StartRunRequest("near-cmd", near.Id, "shell", "echo near-ok", [])).WaitAsync(TimeSpan.FromSeconds(10));
            await Verification.Until(() => service.Snapshot.Sessions.Single(s => s.Id == "near-cmd").Status is "completed" or "error", 20000);
            Check(service.Snapshot.Sessions.Single(s => s.Id == "near-cmd").Logs.Any(l => l.Text.Contains("near-ok", StringComparison.Ordinal)), "this PC's other pane ran and was told while the remote start waited");

            // A stop while connecting: the host never gets the run, and the pane hears one stop.
            await service.StopAsync("far-agent").WaitAsync(TimeSpan.FromSeconds(10));
            Volatile.Write(ref armed, 0); hold.Set();
            await starting.WaitAsync(TimeSpan.FromSeconds(20));
            Check(service.Snapshot.Sessions.Single(s => s.Id == "far-agent").Status == "stopped" && !service.IsSessionRunning("far-agent"), "a stop while connecting ends the run stopped");
            Check(events.Count(e => e.SessionId == "far-agent" && e.Status == "stopped") == 1, "the pane hears one stop: " + events.Count(e => e.SessionId == "far-agent" && e.Status == "stopped"));
            Check(fixture.Runner!.Started.IsEmpty, "a run stopped while connecting never reaches the host");
        }
        finally { Volatile.Write(ref armed, 0); hold.Set(); Directory.Delete(directory, true); Directory.Delete(local, true); }
    }

    /// <summary>A stop while the host accepts ends that run alone: the pane's next send runs on, and hears no second stop.</summary>
    internal static async Task AStopDuringTheHostsAcceptNeverEndsTheNextSend()
    {
        using var accepting = new ManualResetEventSlim(false); using var release = new ManualResetEventSlim(false); var holding = 0;
        await using var fixture = await RemoteHostVerification.MakeHost(admission: _ =>
        {
            if (Interlocked.Exchange(ref holding, 0) == 1) { accepting.Set(); release.Wait(TimeSpan.FromSeconds(20)); }
            return null;
        });
        var state = await fixture.Host.StartAsync([fixture.Shared.Id], 0);
        var directory = Verification.Temp();
        try
        {
            await using var service = new DesktopService(Path.Combine(directory, "state"), null, directory); await service.InitializeAsync();
            var client = service.AttachRemote(Path.Combine(directory, "state"), new MemoryRemoteKeyVault(), new() { AllowLoopbackForTests = true });
            var info = await client.ConnectAsync("", "127.0.0.1:" + state.Port, state.Key!);
            var far = await service.OpenRemoteWorkspaceAsync(info.Id, fixture.Shared.Id);
            await service.UpdateAsync(s => s with { Sessions = [new RunSession { Id = "agent", WorkspaceId = far.Id }] });
            var events = new ConcurrentQueue<RunEvent>(); service.RunEventReceived += events.Enqueue;
            int Stops() => events.Count(e => e.SessionId == "agent" && e.Status == "stopped");
            string Status() => service.Snapshot.Sessions.Single(s => s.Id == "agent").Status;

            // Without attachments: the stop lands while the host accepts; the start ends quietly.
            Volatile.Write(ref holding, 1);
            var first = Task.Run(() => service.StartAsync(new StartRunRequest("agent", far.Id, "claude", "hold", [])));
            Check(accepting.Wait(TimeSpan.FromSeconds(10)), "the host is accepting the run");
            await service.StopAsync("agent");
            await Verification.Until(() => Status() == "stopped");
            release.Set();
            await first.WaitAsync(TimeSpan.FromSeconds(20));
            Check(Status() == "stopped" && !service.IsSessionRunning("agent") && Stops() == 1, "a stop during the accept ends the run once: " + Stops());
            await Verification.Until(() => fixture.Host.Current().ActiveRuns == 0, 10000);

            // With attachments the start is refused as cancelled; meanwhile the pane was sent again, and that run goes on.
            accepting.Reset(); release.Reset(); Volatile.Write(ref holding, 1);
            var attachment = new RunAttachment("attachment", "fixture.txt", "text/plain", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("fixture")));
            var held = Task.Run(() => service.StartAsync(new StartRunRequest("agent", far.Id, "claude", "hold", [], Attachments: [attachment])));
            Check(accepting.Wait(TimeSpan.FromSeconds(10)), "the host is accepting the run with attachments");
            await service.StopAsync("agent");
            await Verification.Until(() => Stops() == 2);
            var errorsBefore = service.Snapshot.Sessions.Single(s => s.Id == "agent").Logs.Count(l => l.Kind == "error");
            await service.StartAsync(new StartRunRequest("agent", far.Id, "claude", "hold", []));
            await Verification.Until(() => service.IsSessionRunning("agent") && Status() == "running");
            release.Set();
            var cancelled = false;
            try { await held.WaitAsync(TimeSpan.FromSeconds(20)); } catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled, "the start with attachments says it was cancelled, so its attachments stay");
            Check(Status() == "running" && service.IsSessionRunning("agent"), "the newer send runs on: " + Status());
            Check(Stops() == 2, "the pane heard each stop once, never one for the newer send: " + Stops());
            Check(service.Snapshot.Sessions.Single(s => s.Id == "agent").Logs.Count(l => l.Kind == "error") == errorsBefore, "the older start writes nothing into the newer run");
            await service.StopAsync("agent");
            await Verification.Until(() => Status() == "stopped" && Stops() == 3);
        }
        finally { release.Set(); Directory.Delete(directory, true); }
    }

    /// <summary>A send connects on a connection that still shares the workspace, as the Mac's connectForRemoteRun checks.</summary>
    internal static async Task ASendToAWorkspaceTheHostNoLongerSharesIsRefused()
    {
        await using var fixture = await RemoteHostVerification.MakeHost();
        var state = await fixture.Host.StartAsync([fixture.Shared.Id, fixture.Private.Id], 0); fixture.Host.SetAllowsFullAccess(true);
        var directory = Verification.Temp();
        try
        {
            await using var service = new DesktopService(Path.Combine(directory, "state"), null, directory); await service.InitializeAsync();
            var client = service.AttachRemote(Path.Combine(directory, "state"), new MemoryRemoteKeyVault(), new() { AllowLoopbackForTests = true });
            var info = await client.ConnectAsync("", "127.0.0.1:" + state.Port, state.Key!);
            var gone = await service.OpenRemoteWorkspaceAsync(info.Id, fixture.Private.Id);
            await service.UpdateAsync(s => s with { Sessions = [new RunSession { Id = "cmd", WorkspaceId = gone.Id, Kind = "shell" }] });
            // The host forgets the workspace; the client's next reading no longer lists it.
            await fixture.Store.SaveAsync(new AppSnapshot { Workspaces = [fixture.Shared, fixture.Remote] });
            await client.RefreshAsync(info.Id);
            var refused = "";
            try { await service.StartAsync(new StartRunRequest("cmd", gone.Id, "shell", "dir", [])); } catch (InvalidOperationException ex) { refused = ex.Message; }
            Check(refused == Locale.Get("remote.workspace.error.notShared"), "the send is refused as not shared: " + refused);
            var pane = service.Snapshot.Sessions.Single(s => s.Id == "cmd");
            Check(pane.Status == "error" && pane.Logs.Any(l => l.Kind == "error" && l.Text == refused), "the pane says why");
            Check(fixture.Runner!.Started.IsEmpty, "nothing reached the host");
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>The sharing host's reading of this PC's CLIs: shared while it goes, kept a while after it finished, read again once stale, failed or forgotten.</summary>
    internal static async Task TheHostReadsThisPCsCLIsOnceAndAgainWhenStaleFailedOrForgotten()
    {
        var directory = Verification.Temp();
        try
        {
            await using var service = new DesktopService(Path.Combine(directory, "state"), null, directory); await service.InitializeAsync();
            var reads = 0; var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var reading = RemoteWireFixtureVerification.Node("info.json")["runtime"].Deserialize<RuntimeInfo>(RemoteWire.Json)!;
            service.HostRuntimeReader = async () =>
            {
                var count = Interlocked.Increment(ref reads);
                if (count == 1) throw new IOException("the CLI could not be read");
                if (count == 2) await gate.Task;
                return reading;
            };
            service.HostRuntimeFresh = TimeSpan.FromMinutes(5);
            var failed = false;
            try { await service.HostRuntimeAsync("1.2.3"); } catch (IOException) { failed = true; }
            Check(failed && reads == 1, "the first reading failed");
            var slow = service.HostRuntimeAsync("1.2.3"); var waiting = service.HostRuntimeAsync("1.2.3");
            await Verification.Until(() => Volatile.Read(ref reads) == 2); // a failed reading is read again
            Check(ReferenceEquals(slow, waiting) && !slow.IsCompleted, "callers share the reading that goes on");
            gate.SetResult();
            Check((await slow).AppVersion == "1.2.3" && ReferenceEquals(service.HostRuntimeAsync("1.2.3"), slow) && reads == 2, "a finished reading is kept");
            service.ForgetHostRuntime();
            var fresh = service.HostRuntimeAsync("1.2.3"); await fresh;
            Check(!ReferenceEquals(fresh, slow) && reads == 3, "a forgotten reading is read again");
            service.HostRuntimeFresh = TimeSpan.FromSeconds(2);
            Check(ReferenceEquals(service.HostRuntimeAsync("1.2.3"), fresh), "a reading inside its time is kept");
            await Task.Delay(2500);
            var later = service.HostRuntimeAsync("1.2.3"); await later;
            Check(!ReferenceEquals(later, fresh) && reads == 4, "a reading past its time is read again");
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>
    /// The window reads a workspace's folder only through MainWindow.LocalPath (RemoteWorkspaceFeatures.LocalPath), so a host's path (a UNC
    /// path among them) never reaches this PC's disk, network or processes. A read anywhere else fails here unless it only shows or compares
    /// the path; smoke code works in its own local workspace.
    /// </summary>
    internal static Task TheWindowReadsAWorkspacesFolderOnlyThroughLocalPath()
    {
        var folder = ClaudePluginVerification.WinUISource();
        // A Path read on anything named for a workspace (activeWorkspace, ws, w), a property pattern on Path, or the folder of a looked-up workspace.
        var read = new Regex(@"\b\w*[Ww]orkspace\w*\??\.Path\b|\b(?:w|ws)\??\.Path\b|\{\s*Path\s*:|Workspaces\.(?:First|FirstOrDefault|Single|SingleOrDefault|Last|LastOrDefault)\([^;]*\)\??\.Path\b|RunWorkspace\([^)]*\)\.Path\b", RegexOptions.CultureInvariant);
        // Core calls that take a whole Workspace and read its Path inside: each must come after a LocalPath guard in its file.
        var wholeWorkspace = new Regex(@"new ClaudePluginBrowser\(|\.SnapshotAsync\(|Bindings\.Bind\(|\.UpdateInstalledAsync\(|\.UpgradeMarketplacesAsync\(", RegexOptions.CultureInvariant);
        (string File, string Guard)[] guarded =
        [
            ("MainWindow.Plugins.cs", "if (LocalPath(workspace) is null) throw"), ("MainWindow.AgentIO.cs", "LocalPath(workspace) is null ? null :"),
            // Automatic plugin updates run in the user's home folder, never a workspace's.
            ("MainWindow.AutomaticUpdates.cs", "var home = new Workspace { Name = \"Home\", Path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) };"),
        ];
        (string File, string Fragment)[] shownOrCompared =
        [
            ("MainWindow.DashboardWorkspaces.cs", "Text = workspace.Path"), ("MainWindow.DashboardWorkspaces.cs", "SetToolTip(path, workspace.Path)"),
            ("MainWindow.DashboardWorkspaces.cs", "MiddleTrim.Fit(path, workspace.Path"), ("MainWindow.Plugins.cs", "SettingsTertiary(workspace.Path"),
            ("MainWindow.Plugins.cs", "SetToolTip(workspacePath, workspace.Path)"), ("MainWindow.ResumeSession.cs", "MiddleTrim.Fit(path, workspace.Path"),
            ("MainWindow.SessionInfo.cs", "Row(\"path\", workspace?.Path)"), ("MainWindow.Shell.cs", "workspaceHeaderFullPath = workspace.Path"),
            ("MainWindow.Sidebar.cs", "SetToolTip(select, workspace.Path)"), ("MainWindow.UpdateAdmission.cs", "w.Path == workspace.Path"),
            ("MainWindow.DashboardWorkspaces.cs", "c.Path != w.Path"), ("MainWindow.ModelRefresh.cs", "w.Path == key.Path"),
            // A reference preview target's own Path, in the transcript's self-check.
            ("AgentTranscript.Actions.cs", "TextTargetAt(plain, pathAt + 24) is { Path: path"),
            // The pictures smoke writes its fixture into the smoke's own local workspace.
            ("MainWindow.AgentImages.cs", "workspace.Path, \"shots\""),
        ];
        var unexpected = new List<string>(); var used = new HashSet<(string, string)>(); var usedGuards = new HashSet<(string, string)>(); var scanned = 0;
        foreach (var file in Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(folder, file);
            if (relative.StartsWith("obj", StringComparison.Ordinal) || relative.StartsWith("bin", StringComparison.Ordinal)) continue;
            var name = Path.GetFileName(file);
            if (name.Contains("Smoke", StringComparison.Ordinal) || name == "MainWindow.LocalPaths.cs") continue;
            scanned++;
            var text = File.ReadAllText(file);
            foreach (Match call in wholeWorkspace.Matches(text))
            {
                if (text.LastIndexOf('\n', call.Index) is var start && text.IndexOf('\n', call.Index) is var end && text[(start + 1)..(end < 0 ? text.Length : end)].TrimStart() is var line
                    && (line.StartsWith("public ", StringComparison.Ordinal) || line.StartsWith("///", StringComparison.Ordinal))) continue;
                var guard = guarded.FirstOrDefault(g => g.File == name);
                if (guard.File is null || text.IndexOf(guard.Guard, StringComparison.Ordinal) is var at && (at < 0 || at > call.Index))
                    unexpected.Add(name + ": " + call.Value + " without a LocalPath guard before it");
                else usedGuards.Add(guard);
            }
            var lines = text.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (!read.IsMatch(lines[i])) continue;
                var allowed = shownOrCompared.FirstOrDefault(a => a.File == name && lines[i].Contains(a.Fragment, StringComparison.Ordinal));
                if (allowed.File is null) unexpected.Add(name + ":" + (i + 1) + ": " + lines[i].Trim()); else used.Add(allowed);
            }
        }
        Check(scanned > 50, "the window's sources were scanned: " + scanned);
        Check(unexpected.Count == 0, "a workspace's folder is read only through LocalPath:\n" + string.Join("\n", unexpected));
        Check(usedGuards.Count == guarded.Length, "every guarded call is still there: " + string.Join(", ", guarded.Where(g => !usedGuards.Contains(g)).Select(g => g.File)));
        Check(used.Count == shownOrCompared.Length, "every allowed use is still there: " + string.Join(", ", shownOrCompared.Where(a => !used.Contains(a)).Select(a => a.File + " " + a.Fragment)));
        Check(File.ReadAllText(Path.Combine(folder, "MainWindow.LocalPaths.cs")).Contains("RemoteWorkspaceFeatures.LocalPath(workspace)", StringComparison.Ordinal), "the window's LocalPath is the shared rule");
        Check(RemoteWorkspaceFeatures.LocalPath(new Workspace { Path = "C:\\work" }) == "C:\\work" && RemoteWorkspaceFeatures.LocalPath(new Workspace { Path = "\\\\server\\share", Remote = new("conn-1", "ws-1", "studio-pc") }) is null
            && RemoteWorkspaceFeatures.LocalPath(null) is null, "a remote workspace has no folder here");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Settings → Remote Connection's wiring, read from the WinUI source (WinUI does not build here): the key leaves only through the secret
    /// copy (out of clipboard history and the cloud clipboard, cleared after a minute), and another computer's runs on this PC hold automatic
    /// and manual CLI updates and plugin changes, as the Mac's localCLIIsRunning does.
    /// </summary>
    internal static Task TheRemotePageCopiesTheKeySecretlyAndHostRunsHoldUpdates()
    {
        var folder = ClaudePluginVerification.WinUISource();
        string Source(string file) => File.ReadAllText(Path.Combine(folder, file));
        static string Member(string text, string signature) => SourceMember(text, signature);
        var page = Source("MainWindow.RemoteWorkspaces.cs");
        var button = page.Split('\n').Where(line => line.Contains("var copyKey = ", StringComparison.Ordinal)).ToArray();
        Check(button.Length == 1 && button[0].Contains("CopyRemoteKeyAsync(", StringComparison.Ordinal) && !button[0].Contains(" Copy(", StringComparison.Ordinal)
            && !button[0].Contains("SetContent(", StringComparison.Ordinal), "the key's copy button goes through CopyRemoteKeyAsync only");
        var copy = Member(page, "private async Task CopyRemoteKeyAsync(");
        Check(copy.Contains("Clipboard.SetContentWithOptions(", StringComparison.Ordinal) && !copy.Contains("Clipboard.SetContent(", StringComparison.Ordinal)
            && copy.Contains("IsAllowedInHistory = RemoteSecretCopy.Key.AllowedInHistory", StringComparison.Ordinal) && copy.Contains("IsRoamable = RemoteSecretCopy.Key.Roamable", StringComparison.Ordinal)
            && copy.Contains("Task.Delay(RemoteSecretCopy.Key.ClearAfter)", StringComparison.Ordinal) && copy.Contains("RemoteSecretCopy.ShouldClear(", StringComparison.Ordinal), "the key copy stays out of history and is cleared");
        Check(page.Split('\n').Count(line => line.Contains("CopyRemoteKeyAsync(", StringComparison.Ordinal)) == 2, "only the key button calls the key copy");
        var automatic = Member(Source("MainWindow.AutomaticUpdates.cs"), "private bool ReadyForAutomaticUpdate(");
        Check(automatic.Contains("remoteHostRuns > 0", StringComparison.Ordinal), "automatic updates wait for another computer's runs here");
        var admission = Source("MainWindow.UpdateAdmission.cs");
        Check(Member(admission, "private Task<CliUpdateResult> UpdateManualProvider(").Contains("remoteHostRuns > 0", StringComparison.Ordinal), "a manual update waits for another computer's runs here");
        Check(Member(admission, "private string? PluginMutationBlockReason(").Contains("remoteHostRuns > 0", StringComparison.Ordinal), "plugin changes wait for another computer's runs here");
        return Task.CompletedTask;
    }

    /// <summary>The member whose declaration contains the signature, up to its closing brace at member indent (a pane's member: to its class's end).</summary>
    private static string SourceMember(string text, string signature)
    {
        var at = text.IndexOf(signature, StringComparison.Ordinal);
        Check(at >= 0, "the member is there: " + signature);
        var end = text.IndexOf("\n    }\n", at, StringComparison.Ordinal);
        return text[at..(end < 0 ? text.Length : end)];
    }

    /// <summary>
    /// What the window shows of a remote workspace and what its panes do, as the Mac's (M/WorkspaceView.swift, M/SessionPaneView.swift,
    /// M/SessionInfoViews.swift): the status bar says "host · OS" connected or not, an empty remote pane says where it runs, and, read
    /// from the WinUI source (WinUI does not build here), a remote command pane never starts a ConPTY terminal, the add-pane menus offer
    /// agents and remote commands only and end with Connect to Another Computer…, no browser pane is stamped, the files button, Explorer
    /// and the files item are off, the host and its OS are named, a remote pane reads its host's CLI, and this PC's holds never hold it.
    /// </summary>
    internal static Task TheWindowNamesARemoteHostAndKeepsItsPanesToIt()
    {
        var reference = new RemoteWorkspaceReference("conn-1", "ws-1", "studio-pc");
        RemoteConnectionInfo Info(RemoteConnectionStatus status, string? platform) => new("conn-1", "studio", "100.101.102.103:43137", status, "studio-pc", [],
            platform is null ? null : new RuntimeInfo(platform, "1.0.0", true, null, null, [], null));
        string Line(bool connected, string osKey) => Locale.Get(connected ? "remote.status.connected" : "remote.status.notConnected",
            new Dictionary<string, string> { ["host"] = Locale.Get("remote.hostWithOS", new Dictionary<string, string> { ["host"] = "studio-pc", ["os"] = Locale.Get(osKey) }) });
        foreach (var (platform, osKey) in new[] { ("darwin", "remote.hostOS.darwin"), ("win32", "remote.hostOS.win32"), ("linux", "remote.hostOS.linux"), ("freebsd", "remote.hostOS.unknown") })
        {
            Check(RemoteWorkspaceFeatures.StatusLine(reference, Info(RemoteConnectionStatus.Connected, platform)) == Line(true, osKey), "connected to a " + platform + " host: " + RemoteWorkspaceFeatures.StatusLine(reference, Info(RemoteConnectionStatus.Connected, platform)));
            Check(RemoteWorkspaceFeatures.StatusLine(reference, Info(RemoteConnectionStatus.Disconnected, platform)) == Line(false, osKey), "a " + platform + " host not connected keeps its OS");
        }
        Check(RemoteWorkspaceFeatures.StatusLine(reference, Info(RemoteConnectionStatus.Saved, null)) == Line(false, "remote.hostOS.unknown")
            && RemoteWorkspaceFeatures.StatusLine(reference, null) == Line(false, "remote.hostOS.unknown"), "before the host answered, or without its connection, the OS is unknown and it is not connected");
        Check(Locale.Get("remote.hostWithOS", new Dictionary<string, string> { ["host"] = "studio-pc", ["os"] = "Windows" }) == "studio-pc · Windows", "the host and its OS read \"{host} · {os}\"");
        Check(RemoteWorkspaceFeatures.PaneNote(new RunSession { Kind = "shell" }, reference, Info(RemoteConnectionStatus.Connected, "darwin")) == RemoteWindowsStrings.CommandNote("studio-pc", "darwin")
            && RemoteWindowsStrings.CommandNote("studio-pc", "darwin").Contains(Locale.Get("remote.hostOS.darwin"), StringComparison.Ordinal), "a command pane names the host and its OS, whose shell runs each command");
        Check(RemoteWorkspaceFeatures.PaneNote(new RunSession { Kind = "claude", Provider = "codex" }, reference, null) == Locale.Get("remote.pane.agentNote", new Dictionary<string, string> { ["host"] = "studio-pc" }), "an agent pane says it runs on the host's CLI");

        var folder = ClaudePluginVerification.WinUISource();
        string Source(string file) => File.ReadAllText(Path.Combine(folder, file));
        // Code only: a comment that repeats a guard's words must not stand in for the guard.
        static string Code(string text) => string.Join('\n', text.Split('\n').Select(line => line.TrimStart().StartsWith("//", StringComparison.Ordinal) ? "" : line));
        static void Before(string text, string first, string then, string what)
        {
            var code = Code(text);
            var a = code.IndexOf(first, StringComparison.Ordinal); var b = code.IndexOf(then, StringComparison.Ordinal);
            Check(a >= 0 && b >= 0 && a < b, what);
        }
        static void Has(string text, string fragment, string what) => Check(Code(text).Contains(fragment, StringComparison.Ordinal), what);
        var terminal = Source("MainWindow.Terminal.cs");
        Before(SourceMember(terminal, "private void InitializeTerminal("), "if (LocalPath(Workspace) is null) return;", "new WebView2()", "a remote command pane returns before any terminal is made");
        Before(SourceMember(terminal, "private async Task StartTerminalShellAsync("), "LocalFolder(Workspace)", "PseudoTerminal.Start(", "the ConPTY shell starts only in a folder on this PC");
        var navigation = Source("MainWindow.Navigation.cs"); var dashboard = Source("MainWindow.DashboardWorkspaces.cs");
        foreach (var (menu, what) in new[] { (SourceMember(navigation, "private MenuFlyout NewSessionMenu("), "a tab group's"), (SourceMember(dashboard, "private MenuFlyout DashboardAddMenu("), "the sidebar's and the dashboard's") })
            Check(menu.Contains("RemoteWorkspaceFeatures.AddPaneEntries(", StringComparison.Ordinal) && !menu.Contains("AddPaneMenu.Entries()", StringComparison.Ordinal)
                && menu.Contains("MarkAddPaneMenu(menu, entries, ", StringComparison.Ordinal) && Code(menu).Contains("ConnectRemoteMenuItem(", StringComparison.Ordinal) && Code(menu).IndexOf("ConnectRemoteMenuItem(", StringComparison.Ordinal) > Code(menu).IndexOf("MarkAddPaneMenu(", StringComparison.Ordinal), what + " add-pane menu follows the workspace and ends with Connect to Another Computer…");
        Check(SourceMember(navigation, "private static string? AddPaneTitle(").Contains("remote ? \"remote.menu.newCommand\" : \"workspace.newTerminal\"", StringComparison.Ordinal), "a remote workspace's terminal entry is a remote command");
        Check(SourceMember(navigation, "private MenuFlyoutItem ConnectRemoteMenuItem(").Contains("MenuItem(Locale.Get(\"remote.menu.connect\"), OpenRemoteSettings)", StringComparison.Ordinal), "Connect to Another Computer… opens Settings → Remote Connection");
        Before(SourceMember(navigation, "private MenuFlyout WorkspaceMenu("), "if (remote) return menu;", "menu.showInExplorer", "a remote workspace's menu has no Explorer or files item");
        Check(Source("MainWindow.Sidebar.cs").Contains("WorkspaceMenu(workspace.Id, workspace.Remote is not null)", StringComparison.Ordinal), "the sidebar's workspace menu knows a remote workspace");
        Before(SourceMember(Source("MainWindow.Browser.cs"), "internal async Task AddBrowserPane("), "if (service.IsRemoteWorkspace(service.Snapshot.ActiveWorkspaceId)) { error.Text = RemoteWindowsStrings.PaneUnavailable; return; }", "await AddPane(\"browser\"",
            "no browser pane is added, nor stamped onto another pane, in a remote workspace");
        var shell = Source("MainWindow.Shell.cs");
        var header = SourceMember(shell, "private void RefreshWorkspaceHeader(");
        Check(header.Contains("workspaceHeaderFiles.IsEnabled = RemoteWorkspaceFeatures.AllowsLocalOnly(workspace);", StringComparison.Ordinal) && header.Contains("RemoteHostBadge(workspace)", StringComparison.Ordinal),
            "the header names the host and its files button is off for a remote workspace");
        Check(SourceMember(shell, "private void RefreshStatusBar(").Contains("status.Text = RemoteWorkspaceFeatures.StatusLine(remote, connection);", StringComparison.Ordinal)
            && Source("MainWindow.cs").Contains("private readonly TextBlock status = new() { TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis,", StringComparison.Ordinal),
            "the status bar names the host and its OS on one line that gives way at the end");
        Check(Source("MainWindow.SessionInfo.cs").Contains("Row(\"hostOS\", workspace?.Remote is null ? null : RemoteWindowsStrings.HostOS(", StringComparison.Ordinal), "session info names the host's OS for a remote workspace only");
        Check(SourceMember(Source("MainWindow.RemoteWorkspaces.cs"), "private void InitializeRemoteWorkspaces(").Contains("DispatcherQueue.TryEnqueue(() => { if (!closing) { RenderSidebar(); RefreshWorkspaceHeader(); RefreshStatusBar(); RefreshRemotePanes();", StringComparison.Ordinal),
            "a connection's change redraws, on the window's thread, everything that names it");
        // Every reading of a pane's CLI goes through PaneRuntime; this PC's runtime is read directly only where a remote pane never comes.
        var direct = Directory.EnumerateFiles(folder, "*.cs").Where(file => !Path.GetFileName(file).Contains("Smoke", StringComparison.Ordinal))
            .SelectMany(file => File.ReadAllLines(file).Where(line => line.Contains("owner.Runtime(", StringComparison.Ordinal)).Select(_ => Path.GetFileName(file))).Order(StringComparer.Ordinal).ToList();
        Check(direct.SequenceEqual(["MainWindow.LoginRecovery.cs", "MainWindow.MobileRemote.cs", "MainWindow.MobileRemote.cs", "MainWindow.StatusLine.cs", "MainWindow.cs"]), "a pane reads its CLI through PaneRuntime: " + string.Join(", ", direct));
        // Nor any other way in a pane's own members: the window's Runtime(…) or its runtime field.
        var bare = new Regex(@"(?<![.\w])Runtime\(|\bowner\.runtime\b", RegexOptions.CultureInvariant); var paneParts = 0; var stray = new List<string>();
        foreach (var file in Directory.EnumerateFiles(folder, "*.cs").Where(file => !Path.GetFileName(file).Contains("Smoke", StringComparison.Ordinal)))
        {
            var text = Code(File.ReadAllText(file));
            for (var at = text.IndexOf("partial class PaneView", StringComparison.Ordinal); at >= 0; at = text.IndexOf("partial class PaneView", at + 1, StringComparison.Ordinal))
            {
                var end = text.IndexOf("\n    }\n", at, StringComparison.Ordinal); var part = text[at..(end < 0 ? text.Length : end)]; paneParts++;
                stray.AddRange(bare.Matches(part).Select(m => Path.GetFileName(file) + ": " + m.Value));
            }
        }
        Check(paneParts > 20 && stray.Count == 0, $"a pane's members read no CLI but through PaneRuntime ({paneParts} parts): " + string.Join(", ", stray));
        Check(Source("MainWindow.cs").Contains("internal ProviderRuntime? PaneRuntime(string provider) => owner.service.RemoteReference(id) is { } remote", StringComparison.Ordinal), "a remote pane's CLI is its host's");
        var window = Source("MainWindow.cs");
        Has(window, "var remoteBlock = workspace.Remote is null ? null : RunBlockReason(runtime);", "the composer reads a remote pane's block from its host");
        Has(window, "canSend = mutationBlock is null && remoteBlock is null && ", "Send waits while the host says the pane cannot run");
        Has(SourceMember(window, "private async Task<bool> AddPane("), "Title = kind == \"shell\" ? Locale.Get(target?.Remote is null ? \"session.title.shell\" : \"remote.pane.commandTitle\")", "a remote workspace's command pane is a remote command");
        Has(Source("MainWindow.Sidebar.cs"), "AutomationProperties.SetAutomationId(host, \"sidebar-remote-host-\" + workspace.Id);", "the sidebar names a remote workspace's host under it");
        var empty = Source("MainWindow.EmptyStates.cs");
        Has(SourceMember(empty, "private void InitializeEmptyOutput("), "if (Session.Kind != \"claude\" && !(Session.Kind == \"shell\" && owner.service.RemoteReference(id) is not null)) return;", "a remote command pane has an empty state");
        Has(empty, "var note = remote is null ? null : RemoteWorkspaceFeatures.PaneNote(pane, remote, owner.service.Remote?.Connection(remote.ConnectionId));", "a remote pane's empty state says where it runs");
        Has(empty, "AutomationProperties.SetAutomationId(line, \"pane-remote-note-\" + id);", "the note carries the Mac's id");
        Has(SourceMember(shell, "private void RefreshStatusBar("), "AutomationProperties.SetAutomationId(status, active?.Remote is null ? StatusBarTextId : StatusRemoteHostId);", "the remote words carry the Mac's status-remote-host id");
        Has(SourceMember(Source("MainWindow.RemoteWorkspaces.cs"), "internal Task OpenRemoteSettings("), "if (SelectSettingsCategory(\"remote\") || dialogOpen) return;", "an open settings window turns to Remote Connection");
        Has(SourceMember(Source("MainWindow.Composer.cs"), "private Task<DelegationRelease> StartFromComposer("), "!service.IsRemoteWorkspace(request.WorkspaceId) && AutomaticLoginOf(request.Provider)", "a remote send never waits for this PC's sign-in");
        Has(SourceMember(Source("MainWindow.Setup.cs"), "private string? SetupItemForMissingCli("), "service.IsRemoteWorkspace(session.WorkspaceId)", "a remote pane is never offered an install here");
        Check(Source("MainWindow.QueuedComposer.cs").Contains("if (RunBlockReason(fresh) is { } blocked)", StringComparison.Ordinal) && !Source("MainWindow.QueuedComposer.cs").Contains("RunBlock.Reason(", StringComparison.Ordinal), "the queue drops what waited by the host's reason");
        Check(SourceMember(Source("MainWindow.UpdateAdmission.cs"), "private string? ManualMutationBlockReason(").Contains("service.IsRemoteWorkspace(pane.WorkspaceId)", StringComparison.Ordinal)
            && Source("MainWindow.AutomaticUpdates.cs").Contains("automaticallyUpdatingProvider == pane.Provider && !service.IsRemoteWorkspace(pane.WorkspaceId);", StringComparison.Ordinal)
            && SourceMember(Source("MainWindow.Composer.cs"), "private async Task<DelegationRelease> StartAdmitted(").Contains("!service.IsRemoteWorkspace(request.WorkspaceId) && AdmissionRefusal(", StringComparison.Ordinal),
            "this PC's CLI updates, plugin changes and sign-ins never hold a remote pane");
        return Task.CompletedTask;
    }

    internal static Task RemotePanesKeepToTheHostsFeatures()
    {
        var local = new Workspace { Path = "C:\\work" };
        var remote = new Workspace { Path = "C:\\far", Remote = new("conn-1", "ws-1", "studio-pc") };
        Check(RemoteWorkspaceFeatures.AddPaneEntries(local).SequenceEqual(AddPaneMenu.Entries()), "a local workspace keeps its menu");
        var entries = RemoteWorkspaceFeatures.AddPaneEntries(remote);
        Check(entries.Contains(AddPaneMenu.Shell) && !entries.Contains(AddPaneMenu.Browser) && !entries.Contains(AddPaneMenu.Files) && Wire.Providers.All(p => entries.Contains(AddPaneMenu.AgentPrefix + p)), "a remote workspace offers agents and commands, never files or a browser");
        Check(RemoteWorkspaceFeatures.AllowsLocalOnly(local) && !RemoteWorkspaceFeatures.AllowsLocalOnly(remote), "local-only features stay local");
        Check(RemoteWorkspaceFeatures.UsesLocalTerminal(new RunSession { Kind = "shell" }, local) && !RemoteWorkspaceFeatures.UsesLocalTerminal(new RunSession { Kind = "shell" }, remote), "a remote command pane is never a local ConPTY terminal");
        var unknown = RemoteWorkspaceFeatures.ProviderRuntime("codex", remote.Remote!, null);
        Check(unknown.Available && unknown.Capabilities.PermissionModes.Contains("onRequest") && unknown.Detail.Contains("studio-pc", StringComparison.Ordinal), "before the host answered, nothing is blocked");
        Check(RemoteWorkspaceFeatures.BlockedReason(new RunSession(), remote.Remote!, null) == Locale.Get("remote.workspace.connectionGone", new Dictionary<string, string> { ["host"] = "studio-pc" }), "a removed connection blocks the pane");
        var info = RemoteWireFixtureVerification.Node("info.json").Deserialize<RemoteWireInfo>(RemoteWire.Json)!;
        var connected = new RemoteConnectionInfo("conn-1", "studio", "100.101.102.103:43137", RemoteConnectionStatus.Connected, "studio-pc", info.Workspaces, info.Runtime);
        Check(RemoteWorkspaceFeatures.ProviderRuntime("claude", remote.Remote!, connected) == info.Runtime.Providers.Single(p => p.Id == "claude"), "once the host answered, its CLI is the pane's");
        Check(RemoteWorkspaceFeatures.BlockedReason(new RunSession { Provider = "codex" }, remote.Remote!, connected) == Locale.Get("remote.host.error.cliMissing", new Dictionary<string, string> { ["name"] = ProviderCatalog.Name("codex") }), "the host's missing CLI blocks the pane");
        Check(RemoteWorkspaceFeatures.BlockedReason(new RunSession { Provider = "claude", Settings = new() { PermissionMode = "onRequest" } }, remote.Remote!, connected) == Locale.Get("remote.workspace.modeUnsupported", new Dictionary<string, string> { ["host"] = "studio-pc" }), "a mode the host's CLI lacks blocks the pane");
        Check(RemoteWorkspaceFeatures.BlockedReason(new RunSession { Provider = "claude" }, remote.Remote!, connected) is null, "the host's CLI in a mode it has runs");
        Check(RemoteWorkspaceFeatures.BlockedReason(new RunSession { Kind = "shell" }, remote.Remote!, connected) is null, "commands never wait for a CLI");
        Check(RemoteWorkspaceFeatures.FirewallPorts(43139, 43137).SequenceEqual([43137, 43139]) && RemoteWorkspaceFeatures.FirewallPorts(null, 43137).SequenceEqual([43137]) && RemoteWorkspaceFeatures.FirewallPorts(null, null).Count == 0 && RemoteWorkspaceFeatures.FirewallPorts(80, null).Count == 0 && RemoteWorkspaceFeatures.FirewallPorts(43137, 43137).SequenceEqual([43137]), "the firewall rule opens the ports in use, nothing else");
        Check(RemoteSecretCopy.Key is { AllowedInHistory: false, Roamable: false } && RemoteSecretCopy.Key.ClearAfter == TimeSpan.FromSeconds(60), "the key stays out of clipboard history and other devices, and goes after a minute");
        Check(RemoteSecretCopy.ShouldClear("k", "k") && !RemoteSecretCopy.ShouldClear("other", "k") && !RemoteSecretCopy.ShouldClear(null, "k"), "only the key itself is cleared");
        return Task.CompletedTask;
    }
}
