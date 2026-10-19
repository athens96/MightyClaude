using System.Collections.Concurrent;
using System.Text.Json;
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
            try { await found.Workspace(shared.Id); } catch (InvalidOperationException) { refused = true; }
            Check(refused, "once sharing stops, its runner finds nothing");
            Check(counts.LastOrDefault() == 0, "the host tells the app how many client runs go on");
        }
        finally { Directory.Delete(directory, true); }
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
