using System.Text;
using System.Text.Json.Nodes;
using MightyClaude.Core;
using Microsoft.Web.WebView2.Core;

namespace MightyClaude.WinUI;

/// <summary>
/// The help capture's delegation demo (MainWindow.HelpCapture.cs), seeded on the first delegation shot as the Mac's capture seeds it
/// (M/HelpCaptureDiagnostics.swift makeDelegationDemo): the delegation workspace, its parent pane, the open children opened beside
/// it the way delegate opens them (a closed parent's child is then linked to that parent, whose pane is not in the snapshot), their
/// canned runs played through the real run path, and then the profile's delegation records saved as the profile's delegation file
/// and loaded into the app's delegation coordinator, so the sidebar's tree, the children's cards and the queued lists show them.
/// </summary>
public sealed partial class MainWindow
{
    private async Task<HelpTarget> OpenHelpDelegation(string screen, HelpDemo demo, CoreWebView2 core)
    {
        if (demo.DelegationProblem is { } problem) throw new InvalidOperationException(problem);
        if (!demo.DelegationSeeded)
        {
            try { await SeedHelpDelegation(demo); demo.DelegationSeeded = true; }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                demo.DelegationProblem = "seeding the delegation demo failed: " + ex.Message;
                throw new InvalidOperationException(demo.DelegationProblem, ex);
            }
        }
        var space = demo.Workspaces[demo.DelegationWorkspace!];
        var id = demo.Pane(screen == "delegation-parent-closed" ? "child-of-closed" : "parent");
        CheckHelpDelegation(screen, id, demo);
        var expanded = service.Snapshot.ExpandedWorkspaceIds;
        await SelectWorkspace(space);
        // The other workspaces' lists are closed, so the tree has the sidebar to itself.
        await service.UpdateAsync(s => s with { ExpandedWorkspaceIds = [space] });
        await SelectLayoutSession(id);
        // The held notices with the parent pane on its own; the tree with the pane's tab selected.
        if (screen == "delegation-held") await ApplyLayoutPreset("focus");
        RenderSidebar(); Render();
        if (!await HelpUntil(core, "!!document.querySelector('[data-session-id=\"" + id + "\"]')", 10)) throw new InvalidOperationException("the delegation pane did not show");
        await Task.Delay(1000);
        return new(Cleanup: async () =>
        {
            if (screen == "delegation-held" && demo.Profile["layouts"]?.AsArray().OfType<JsonObject>().FirstOrDefault(l => (string?)l["workspace"] == demo.DelegationWorkspace) is { } layout)
                await ApplyLayoutPreset((string?)layout["preset"] ?? "tabs");
            await service.UpdateAsync(s => s with { ExpandedWorkspaceIds = expanded });
        });
    }

    private async Task SeedHelpDelegation(HelpDemo demo)
    {
        var directory = options.ProfileDirectory!;
        var profile = demo.Profile;
        var spec = profile["delegation"] as JsonObject ?? throw new InvalidOperationException("the demo has no delegation");
        var space = demo.DelegationWorkspace ?? throw new InvalidOperationException("the delegation demo has no workspace");
        var children = (spec["children"]?.AsArray() ?? []).OfType<JsonObject>().ToList();
        var childRoles = children.Select(c => HelpText(c["role"])).ToHashSet();
        var workspaceSpec = (profile["workspaces"]?.AsArray() ?? []).OfType<JsonObject>().FirstOrDefault(w => (string?)w["id"] == space) ?? throw new InvalidOperationException("the delegation demo has no workspace");

        // The workspace, as the other demo workspaces are made.
        var checkout = Path.Combine(directory, "Projects", (string?)workspaceSpec["directory"] ?? throw new InvalidOperationException("the delegation workspace has no folder"));
        foreach (var file in (workspaceSpec["files"]?.AsArray() ?? []).OfType<JsonObject>())
        {
            var path = Path.Combine(checkout, ((string?)file["path"] ?? "").Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, (string?)file["content"] ?? "");
        }
        var workspace = await service.AddWorkspaceAsync(checkout);
        demo.Workspaces[space] = workspace.Id;
        var panes = (profile["panes"]?.AsArray() ?? []).OfType<JsonObject>().Where(p => (string?)p["workspace"] == space).ToList();
        JsonObject PaneSpec(string role) => panes.FirstOrDefault(p => HelpText(p["role"]) == role) ?? throw new InvalidOperationException("the delegation demo has no " + role + " pane");
        RunSession Filled(RunSession session, JsonObject pane) => session with
        {
            Kind = (string?)pane["kind"] ?? "claude", Provider = (string?)pane["provider"] ?? "claude", Model = (string?)pane["model"] ?? "default",
            Title = (string?)pane["title"] ?? HelpText(pane["role"]), TitleMode = "fixed",
            Settings = new((string?)pane["effort"] ?? "default", (string?)pane["permissionMode"] ?? "manual"), AgentViewMode = (string?)pane["view"] == "mighty" ? "mighty" : null,
        };

        // The parents' panes.
        var parents = new Dictionary<string, string>();
        foreach (var pane in panes.Where(p => !childRoles.Contains(HelpText(p["role"]))))
        {
            var session = Filled(new RunSession { WorkspaceId = workspace.Id }, pane);
            demo.Panes[HelpText(pane["role"])] = session.Id; parents[HelpText(pane["role"])] = session.Id;
            await service.UpdateAsync(s => s with { Sessions = [.. s.Sessions, session], ActiveWorkspaceId = workspace.Id, ActiveSessionId = session.Id });
        }
        foreach (var role in (spec["closedParents"]?.AsArray() ?? []).Select(r => (string?)r ?? "")) parents[role] = Guid.NewGuid().ToString().ToLowerInvariant();
        var opened = children.Select(c => demo.Panes.GetValueOrDefault(HelpText(c["parent"]))).FirstOrDefault(id => id is not null) ?? throw new InvalidOperationException("the delegation demo has no open parent");
        Render();

        // The open children, opened beside their parent as delegate opens them, each working in its own folder; a closed child has no pane.
        var worktrees = new ChildWorktreeMaker(Path.Combine(directory, "worktrees"));
        static ChildState State(JsonObject child) => DelegationNames.Parse<ChildState>((string?)child["state"]) ?? throw new InvalidOperationException("a delegation child has an unknown state");
        foreach (var child in children.Where(c => State(c).IsOpen()))
        {
            var role = HelpText(child["role"]); var id = HelpText(child["id"]);
            var pane = PaneSpec(role);
            var parent = parents.GetValueOrDefault(HelpText(child["parent"])) ?? throw new InvalidOperationException("the delegation demo has no parent for " + role);
            var folder = worktrees.WorktreePath(id);
            Directory.CreateDirectory(folder);
            var beside = demo.Panes.GetValueOrDefault(HelpText(child["parent"])) ?? opened;
            if (!await ((IDelegationHost)service).CreatePane(new(id, beside, (string?)child["mode"] ?? "acceptEdits", folder))) throw new InvalidOperationException("could not open the " + role + " pane");
            demo.Panes[role] = id;
            await service.UpdateAsync(s => s with { Sessions = s.Sessions.Select(p => p.Id == id ? Filled(p, pane) with { ParentSessionId = parent } : p).ToList() });
        }
        Render();

        // Their runs, played by the canned CLI through the real run path, each waited for until its last line is in.
        var scripts = Path.Combine(directory, "help-demo"); Directory.CreateDirectory(scripts);
        var runs = new JsonArray(); var plays = new List<(string Role, string Id, JsonObject Run, string Marker)>();
        foreach (var pane in panes)
        {
            var role = HelpText(pane["role"]);
            if (!demo.Panes.TryGetValue(role, out var id)) continue;
            var index = 0;
            foreach (var run in (pane["runs"]?.AsArray() ?? []).OfType<JsonObject>())
            {
                var marker = Path.Combine(scripts, $"{role}-{index++}.done");
                runs.Add(new JsonObject { ["provider"] = (string?)pane["provider"], ["input"] = (string?)run["input"], ["frames"] = run["frames"]?.DeepClone(), ["marker"] = marker });
                plays.Add((role, id, run, marker));
            }
        }
        var script = Path.Combine(scripts, "delegation-script.json");
        await File.WriteAllTextAsync(script, new JsonObject { ["runs"] = runs }.ToJsonString());
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("the app's own binary is not known");
        service.Providers.Override("claude", new CliCommand(executable, [HelpDemoCli.Argument, script], "2.1.271"));
        var timings = new Dictionary<string, AgentRunTiming>();
        foreach (var (role, id, run, marker) in plays)
        {
            var current = service.Snapshot.Sessions.First(s => s.Id == id);
            var finish = (string?)run["finish"];
            await service.SendAsync(new StartRunRequest(id, current.WorkspaceId, current.Kind, (string?)run["input"] ?? "", [], current.Model, current.Provider, current.Settings, current.ResumeId));
            if (!await HelpWait(() => File.Exists(marker), 30)) throw new InvalidOperationException("the " + role + " pane's run did not play");
            if (finish == "stopped") { await Task.Delay(300); await service.StopAsync(id); }
            if (finish is not null && !await HelpWait(() => service.Snapshot.Sessions.First(s => s.Id == id).Status is not "running" and not "idle", 30))
                throw new InvalidOperationException("the " + role + " pane's run did not end");
            await Task.Delay(400);
            var now = DateTimeOffset.UtcNow;
            if (run["elapsedSeconds"]?.GetValue<double>() is { } seconds) timings[id] = new(now.AddSeconds(-seconds), now, finish is null ? null : now);
        }
        await service.UpdateAsync(s => s with { Sessions = s.Sessions.Select(p => timings.TryGetValue(p.Id, out var timing) ? p with { RunTiming = timing } : p).ToList() });

        // The records, as the Mac's HelpDemoDelegation.file writes them, loaded the way a launch loads them.
        var parentBranch = (string?)spec["parentBranch"] ?? "main"; var baseCommit = (string?)spec["baseCommit"] ?? "";
        const string deliveredRunId = "help-demo-run";
        var records = new List<ChildRecord>(); var merges = new List<MergeRecord>(); var copies = new List<DelegationCopy>();
        foreach (var child in children)
        {
            var id = HelpText(child["id"]); var task = HelpText(child["task"]); var mode = (string?)child["mode"] ?? "acceptEdits";
            var parentId = parents.GetValueOrDefault(HelpText(child["parent"])) ?? throw new InvalidOperationException("the delegation demo has no session for " + HelpText(child["parent"]));
            var head = (string?)child["reportHead"];
            var record = ChildRecord.Make(id, parentId, worktrees.WorktreePath(id), parentBranch, baseCommit, mode, DelegationRequestKey.Make(parentId, deliveredRunId, task, mode),
                reportRevision: child["reportRevision"]?.GetValue<int>() ?? 0, reportHead: head, state: State(child), parentCheckout: checkout);
            copies.Add(DelegationCopy.Of(id, DelegationCopyKind.Task, 0, Encoding.UTF8.GetBytes(task)));
            if (DelegationNames.Parse<MergeKind>((string?)child["merge"]) is { } kind && head is not null)
            {
                merges.Add(new(id, kind, parentBranch, baseCommit, head, head));
                if (record.State == ChildState.Closed) record = record with { ClosedHead = head };
            }
            records.Add(record);
        }
        var time = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        var notices = new List<Notice>();
        foreach (var notice in (spec["notices"]?.AsArray() ?? []).OfType<JsonObject>())
        {
            var child = children.FirstOrDefault(c => HelpText(c["role"]) == HelpText(notice["child"])) ?? throw new InvalidOperationException("the delegation demo has no child " + HelpText(notice["child"]));
            var lane = DelegationNames.Parse<DeliveryLane>((string?)notice["lane"]) ?? throw new InvalidOperationException("a delegation notice has an unknown lane");
            notices.Add(new(HelpText(notice["id"]), HelpText(child["id"]), notice["revision"]?.GetValue<int>() ?? 0,
                DelegationNames.Parse<NoticeKind>((string?)notice["kind"]) ?? throw new InvalidOperationException("a delegation notice has an unknown kind"), lane,
                lane == DeliveryLane.Delivered ? new DeliveryReceipt(time, DeliveryRoute.Queue, deliveredRunId) : null));
        }
        new DelegationFileStore(directory).Save(DelegationFile.Of(records, notices, merges: merges, copies: copies));
        await service.RestartDelegationAsync();
        if (service.Delegation is null) throw new InvalidOperationException("delegation did not load the demo's records");
        var held = notices.Count(n => n.Lane == DeliveryLane.Held);
        if (!await HelpWait(() => service.DelegationChildren.Select(c => c.Id).SequenceEqual(records.Select(r => r.Id)) && parents.Values.Sum(p => service.HeldItems(p).Count) == held, 10))
            throw new InvalidOperationException("the delegation records did not load");

        if (profile["layouts"]?.AsArray().OfType<JsonObject>().FirstOrDefault(l => (string?)l["workspace"] == space) is { } layout)
        {
            await SelectWorkspace(workspace.Id);
            await ApplyLayoutPreset((string?)layout["preset"] ?? "tabs");
            await SelectLayoutSession(demo.Pane(HelpText(layout["active"])));
        }
        RenderSidebar(); Render();
    }

    /// <summary>Checks that the delegation demo shows what the screen is meant to, as the Mac's capture does (checkDelegation).</summary>
    private void CheckHelpDelegation(string screen, string id, HelpDemo demo)
    {
        var spec = demo.Profile["delegation"]!;
        var children = (spec["children"]?.AsArray() ?? []).OfType<JsonObject>().ToList();
        var session = service.Snapshot.Sessions.FirstOrDefault(s => s.Id == id) ?? throw new InvalidOperationException("the pane is missing");
        var tree = service.SidebarTree(service.Snapshot, demo.Workspaces[demo.DelegationWorkspace!]);
        switch (screen)
        {
            case "delegation-tree":
                var expected = children.Where(c => demo.Panes.GetValueOrDefault(HelpText(c["parent"])) == id).Select(c => HelpText(c["id"])).ToList();
                if (expected.Count == 0 || tree.Children.GetValueOrDefault(id)?.Select(c => c.Id).SequenceEqual(expected) != true)
                    throw new InvalidOperationException("the parent's children are not listed under it");
                if (!session.Logs.Any(l => l.Kind == "user" && l.Text.StartsWith("[Mighty Claude notice ", StringComparison.Ordinal))) throw new InvalidOperationException("the parent's transcript has no notice");
                break;
            case "delegation-held":
                if (session.Status != "stopped" || service.HeldItems(id).Count == 0) throw new InvalidOperationException("the stopped parent does not hold its notices");
                break;
            case "delegation-parent-closed":
                if (tree.ParentClosed.SelectMany(n => n.Children).FirstOrDefault(c => c.Id == id) is not { HeldNotices: > 0 } row || service.Snapshot.Sessions.Any(s => s.Id == row.ParentSessionId))
                    throw new InvalidOperationException("the closed parent's child is not listed with its held notices");
                break;
        }
    }
}
