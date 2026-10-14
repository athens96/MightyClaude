using System.Text.Json;
using System.Text.Json.Nodes;
using MightyClaude.Core;

/// The diagram's 실행 block (MightyGraphLayout.Make splitsExecution; macOS MightyGraphExecutionBlockTests): a
/// request block holds its input alone, and the main agent's work sits in a 실행 block right under it, with the
/// sub-agents and the request's attachments hanging off it.
internal static class MightyGraphExecutionBlockVerification
{
    private const double RowGap = 32;
    private static void Check(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
    private static MightyGraphAgent Agent(string id, string? parent = null, string status = "completed") =>
        new() { Id = id, ParentID = parent, Title = id, Input = "check", Status = status };
    private static MightyGraphRun Run(string id, string status = "completed", params MightyGraphAgent[] agents) =>
        new() { Id = id, Input = "request " + id, Status = status, Agents = [.. agents], FinalOutput = status == "completed" ? "result " + id : null };
    private static MightyGraphLayout Split(IReadOnlyList<MightyGraphRun> runs, bool running = false, IEnumerable<string>? expanded = null,
        Dictionary<string, GraphBlockSize>? blockSizes = null, IReadOnlyList<(string RunID, string RecordID)>? planRecords = null) =>
        MightyGraphLayout.Make(runs, "", running, new HashSet<string>(expanded ?? []), blockSizes: blockSizes, planRecords: planRecords, splitsExecution: true);
    private static MightyGraphLayout Shared(IReadOnlyList<MightyGraphRun> runs, bool running = false) => MightyGraphLayout.Make(runs, "", running, new HashSet<string>());
    private static string Id(string run, string suffix) => MightyGraphBlockSize.NodeId(run, suffix);
    private static GraphRect Frame(MightyGraphLayout layout, string id) => layout.Nodes.Single(n => n.Id == id).Frame;
    private static bool Same(IReadOnlyDictionary<string, GraphBlockSize>? sizes, params (string Id, GraphBlockSize Size)[] expected) =>
        sizes is not null && sizes.Count == expected.Length && expected.All(e => sizes.GetValueOrDefault(e.Id) == e.Size);
    private static bool Intersects(GraphRect a, GraphRect b) => a.X < b.MaxX && b.X < a.MaxX && a.Y < b.MaxY && b.Y < a.MaxY;

    internal static Task TheExecutionBlockHasAStableNodeID()
    {
        Check(MightyGraphLayout.WorkSuffix == "execution" && MightyGraphBlockSize.NodeId("one", MightyGraphLayout.WorkSuffix) == "3:one:execution"
            && MightyGraphLayout.NodeID(Run("one"), MightyGraphLayout.WorkSuffix) == Id("one", "execution"), "the 실행 block's node id");
        // Not an attachment: the camera may stand on it.
        Check(!MightyGraphCamera.IsAuxiliary(Id("one", "execution")), "the 실행 block is not auxiliary");
        // Laid out twice, it is the same node in the same place.
        var runs = new[] { Run("one", agents: Agent("a")) };
        Check(Split(runs).Nodes.Select(n => n.Id).SequenceEqual(Split(runs).Nodes.Select(n => n.Id)) && Split(runs).Nodes.Single(n => n.Kind == MightyGraphLayout.WorkKind).Id == Id("one", "execution"),
            "the 실행 block is the same node on every layout");
        return Task.CompletedTask;
    }

    internal static Task TheRequestSitsOverItsExecutionBlockInOneColumn()
    {
        var layout = Split([Run("one")]);
        // Then the next request's draft, as under any finished request.
        Check(layout.Nodes.Select(n => n.Kind).SequenceEqual(["request", MightyGraphLayout.WorkKind, "result", "draft"]), "request, 실행, result, draft: " + string.Join(",", layout.Nodes.Select(n => n.Kind)));
        var (request, work, result) = (Frame(layout, Id("one", "request")), Frame(layout, Id("one", "execution")), Frame(layout, Id("one", "result")));
        // The request holds its input alone, so it is the short block; the work is the tall one.
        Check(request.H == MightyGraphLayout.RequestHeight(false) && MightyGraphLayout.RequestHeight(false) == 140 && work.H == 280, "the request is short and its 실행 block tall");
        Check(work.Y == request.MaxY + RowGap && request.MidX == work.MidX && work.MidX == result.MidX && Math.Abs(work.MidX - MightyGraphCamera.CentreX) < 0.5 && result.Y == work.MaxY + RowGap, "one column on the centreline");
        Check(layout.Edges.Select(e => (e.Source, e.Target)).SequenceEqual([(Id("one", "request"), Id("one", "execution")), (Id("one", "execution"), Id("one", "result")), (Id("one", "result"), MightyGraphCamera.PendingNodeID)])
            && !layout.Edges[0].Joins, "request → 실행 → result → draft");
        // Both are blocks a drag resizes and opening grows.
        Check(MightyGraphLayout.MinimumBlockSize(MightyGraphLayout.WorkKind) == (MightyGraphBlockSize.MinimumWidth, MightyGraphBlockSize.MinimumHeight), "the 실행 block's least is any block's");
        var opened = Split([Run("one")], expanded: [Id("one", "request"), Id("one", "execution")]);
        Check(Frame(opened, Id("one", "request")).H == MightyGraphLayout.RequestHeight(true) && MightyGraphLayout.RequestHeight(true) == 280 && Frame(opened, Id("one", "execution")).H == 540, "opened heights");
        // A saved size wins, and a taller request pushes its 실행 block down.
        var sized = Split([Run("one")], blockSizes: new() { [Id("one", "request")] = new(640, 300) });
        Check(Frame(sized, Id("one", "request")) is { W: 640, H: 300 } && Frame(sized, Id("one", "execution")).Y == 24 + 300 + RowGap, "a saved request size");
        return Task.CompletedTask;
    }

    internal static Task SubAgentsBranchFromTheExecutionBlock()
    {
        var runs = new[] { Run("one", agents: [Agent("root"), Agent("left", "root"), Agent("solo")]) };
        var layout = Split(runs);
        var (work, request) = (Id("one", "execution"), Id("one", "request"));
        Check(layout.Edges.Any(e => e.Source == work && e.Target == Id("one", "agent:root")) && layout.Edges.Any(e => e.Source == work && e.Target == Id("one", "agent:solo"))
            && layout.Edges.Any(e => e.Source == Id("one", "agent:root") && e.Target == Id("one", "agent:left")), "sub-agents branch from the 실행 block");
        // The request's only edge is into its 실행 block.
        Check(layout.Edges.Where(e => e.Source == request).Select(e => e.Target).SequenceEqual([work]), "the request's only edge");
        var workFrame = Frame(layout, work);
        foreach (var agent in new[] { "root", "solo" }) Check(Frame(layout, Id("one", "agent:" + agent)).Y == workFrame.MaxY + RowGap, "the first row of agents is under the 실행 block: " + agent);
        // The result joins the leaves, not the 실행 block.
        Check(layout.Edges.Where(e => e.Target == Id("one", "result")).Select(e => e.Source).ToHashSet().SetEquals([Id("one", "agent:left"), Id("one", "agent:solo")]), "the result joins the leaves");
        // Resizing the 실행 block moves the agents under it.
        var taller = Split(runs, blockSizes: new() { [work] = new(500, 400) });
        Check(Frame(taller, Id("one", "agent:root")).Y == workFrame.Y + 400 + RowGap && Frame(taller, request) == Frame(layout, request), "a taller 실행 block pushes its agents down and leaves the request");
        return Task.CompletedTask;
    }

    internal static Task AnEmptyRunStillShowsItsExecutionBlock()
    {
        // Just sent: no record yet, no agents, nothing finished.
        var fresh = new[] { Run("new", "running") };
        var layout = Split(fresh, running: true);
        Check(layout.Nodes.Select(n => n.Kind).SequenceEqual(["request", MightyGraphLayout.WorkKind]) && layout.Edges.Select(e => e.Target).SequenceEqual([Id("new", "execution")]), "a request and its 실행 block alone");
        // Its edge runs blue while it works, as a running block's does: the 실행 block carries the run's tone, the request none.
        var blocks = MightyGraphBlockModel.Blocks(layout, fresh, "", "Claude", true);
        Check(blocks.Where(b => MightyGraphBlockModel.Tone(b) == DesignTone.Run).Select(b => b.Id).SequenceEqual([Id("new", "execution")]), "only the 실행 block is live");
        // Settled, it is no longer live.
        var done = new[] { Run("done") };
        Check(!MightyGraphBlockModel.Blocks(Split(done), done, "", "Claude", true).Any(b => MightyGraphBlockModel.Tone(b) == DesignTone.Run), "a settled request has no live block");
        return Task.CompletedTask;
    }

    internal static Task TheRequestBlockHoldsItsInputAndTheExecutionBlockTheWork()
    {
        var run = Run("one", "running", Agent("a", status: "running"));
        run.RootEntries = [new("u", "user", run.Input, "2026-10-14T10:00:00Z"), new("r", "assistant", "Looking.", "2026-10-14T10:00:01Z")];
        run.Usage = new(100, 20); run.ResponseRecords = [new("m1", "claude-opus-5", new(100, 20), [])];
        var blocks = MightyGraphBlockModel.Blocks(Split([run], running: true), [run], "", "Claude", true);
        var request = blocks.Single(b => b.Kind == "request"); var work = blocks.Single(b => b.Kind == MightyGraphLayout.WorkKind);
        // The request: what was asked, with no status of its own, nothing it did and no usage.
        Check(request.Request == run.Input && request.Entries.Count == 0 && request.State.Length == 0 && request.Status.Length == 0 && request.Capsule is null && request.CapsuleHelp.Length == 0
            && request.Indicator == "none" && request.Outline == MightyGraphActivity.None && MightyGraphBlockModel.Tone(request) == DesignTone.Idle, "the request block holds its input alone");
        Check(request.Title == MightyGraphBlockModel.RequestTitle(1, "Claude"), "the request block keeps its title");
        // Its 실행 block: the run's records, status and tokens, no request text, and a title that names its request.
        Check(work.Request.Length == 0 && work.Entries.Select(e => e.Id).SequenceEqual(["r"]) && work.Status == "running" && work.State == MightyGraphBlockModel.StateLabel("running") && work.Capsule is not null
            && work.Indicator == "animating" && work.Outline == MightyGraphActivity.Marching && MightyGraphBlockModel.Tone(work) == DesignTone.Run, "the 실행 block holds the work");
        Check(work.Title == Locale.Get("graph.block.executionOf", new Dictionary<string, string> { ["n"] = "1" }) && work.Title != MightyGraphBlockModel.ExecutionTitle(2) && MightyGraphBlockModel.TitleProvider(work, "claude") is null,
            "the 실행 block names its request and carries no agent mark");
        // Without the split the request block holds both, as before.
        var shared = MightyGraphBlockModel.Blocks(Shared([run], running: true), [run], "", "Claude", true).Single(b => b.Kind == "request");
        Check(shared.Entries.Count == 1 && shared.Status == "running" && shared.Capsule is not null, "the shared layout's request block is unchanged");
        return Task.CompletedTask;
    }

    internal static Task TheCameraStandsOnTheExecutionBlockWhileTheRequestRuns()
    {
        var running = new[] { Run("one"), Run("two", "running", Agent("child", status: "running")) };
        Check(Split(running, running: true).NewestRequestTargetID(running) == Id("two", "execution"), "a running request's 실행 block");
        // A run whose own status ended but whose child still works is still running.
        running[1].Status = "completed";
        Check(Split(running, running: true).NewestRequestTargetID(running) == Id("two", "execution"), "a request with a child at work");
        running[1].Status = "running";
        // Finished, the request is where it starts.
        var done = new[] { Run("one"), Run("two") };
        Check(Split(done).NewestRequestTargetID(done) == Id("two", "request"), "a finished request");
        // Without a 실행 block (the shared layout) it is the request, and nothing without a request.
        Check(Shared(running, running: true).NewestRequestTargetID(running) == Id("two", "request") && Split([]).NewestRequestTargetID([]) is null, "the shared layout and no request");
        // A new request still aims at its request block, top first, with the 실행 block right below it.
        Check(MightyGraphCamera.ReaimAnchor("two", null, Split(running, running: true).Nodes.Select(n => n.Id).ToHashSet()).Equals(MightyGraphCamera.Anchor.Reaim(Id("two", "request"), true)), "a new request aims at its request block");
        return Task.CompletedTask;
    }

    internal static Task TheRequestsAttachmentsHangOffItsExecutionBlock()
    {
        var runs = new[] { Run("one", agents: [.. Enumerable.Range(0, 3).Select(i => Agent("branch-" + i))]), Run("two") };
        var plain = Split(runs);
        var layout = Split(runs, planRecords: [("one", "p1"), ("one", "p2")]);
        // Attachments only: the flow is where it was.
        foreach (var node in plain.Nodes) Check(Frame(layout, node.Id) == node.Frame, "nothing already placed moves: " + node.Id);
        Check(layout.Edges.SequenceEqual(plain.Edges), "attachments add no edge");
        var work = Frame(layout, Id("one", "execution"));
        var first = Frame(layout, Id("one", MightyGraphLayout.PlanRecordSuffix + "p1")); var second = Frame(layout, Id("one", MightyGraphLayout.PlanRecordSuffix + "p2"));
        Check(first.Y == work.Y && first.X > work.MaxX && second.Y > first.MaxY && second.X > work.MaxX, "answered plans stack beside the 실행 block, from its top");
        foreach (var attachment in new[] { first, second })
            foreach (var node in layout.Nodes.Where(n => n.Frame != attachment)) Check(!Intersects(node.Frame, attachment), "an attachment overlaps " + node.Id);
        // Without the split they hang off the request, as before.
        var shared = MightyGraphLayout.Make(runs, "", false, new HashSet<string>(), planRecords: [("one", "p1")]);
        Check(Frame(shared, Id("one", MightyGraphLayout.PlanRecordSuffix + "p1")).Y == Frame(shared, Id("one", "request")).Y, "the shared layout hangs them off the request");
        return Task.CompletedTask;
    }

    internal static Task WithoutTheSplitTheSharedLayoutIsUnchanged()
    {
        var runs = new[] { Run("one", agents: Agent("a")), Run("two", "running") };
        var shared = Shared(runs, running: true);
        Check(!shared.Nodes.Any(n => n.Kind == MightyGraphLayout.WorkKind) && shared.Edges.Any(e => e.Source == Id("one", "request") && e.Target == Id("one", "agent:a"))
            && Frame(shared, Id("one", "request")).H == 280, "the shared layout has no 실행 block");
        Check(MightyGraphViewModel.CanvasLayout(runs, "", true, new HashSet<string>()).Nodes.Select(n => (n.Id, n.Frame)).SequenceEqual(shared.Nodes.Select(n => (n.Id, n.Frame)))
            && MightyGraphViewModel.CanvasLayout(runs, "", true, new HashSet<string>(), splitsExecution: true).Nodes.Any(n => n.Kind == MightyGraphLayout.WorkKind), "the canvas layout splits only when asked");
        return Task.CompletedTask;
    }

    internal static Task ASizeSavedBeforeTheSplitMovesToTheExecutionBlockOnce()
    {
        var dragged = new GraphBlockSize(1_100, 700); var own = new GraphBlockSize(640, 400);
        var directory = Verification.Temp();
        try
        {
            var workspace = new Workspace { Id = "workspace", Name = "Graph", Path = directory };
            RunSession Saved(RunSession session) => StateStore.Normalize(new AppSnapshot { Workspaces = [workspace], Sessions = [session] }, true).Sessions.Single();
            // A pane saved before the split: no layout marker, a request block dragged large.
            var session = new RunSession
            {
                Id = "old", WorkspaceId = workspace.Id, Title = "Old",
                GraphRuns = [new() { Id = "a", Input = "a", Status = "completed" }, new() { Id = "b", Input = "b", Status = "completed" }],
                GraphBlockSizes = new() { [Id("a", "request")] = dragged, [Id("b", "request")] = dragged, [Id("b", "execution")] = own },
            };
            var wire = JsonNode.Parse(JsonSerializer.Serialize(session, Wire.Json))!.AsObject();
            Check(wire["graphSizesLayout"]?.GetValue<int>() == GraphBlockPreferences.SplitLayout, "a pane is saved with its sizes' layout");
            wire.Remove("graphSizesLayout");
            var legacy = JsonSerializer.Deserialize<RunSession>(wire.ToJsonString(), Wire.Json)!;
            Check(legacy.GraphSizesLayout is null, "a pane saved before the split has no layout marker");
            var migrated = Saved(legacy);
            // The request goes back to its own short size; its old size is the 실행 block's, unless that block already had one.
            Check(Same(migrated.GraphBlockSizes, (Id("a", "execution"), dragged), (Id("b", "execution"), own)) && migrated.GraphSizesLayout == GraphBlockPreferences.SplitLayout,
                "a request size from before the split is its 실행 block's: " + JsonSerializer.Serialize(migrated.GraphBlockSizes, Wire.Json));
            var laid = Split(migrated.GraphRuns ?? [], blockSizes: migrated.GraphBlockSizes);
            Check(Frame(laid, Id("a", "request")) is { W: MightyGraphCamera.RequestWidth, H: 140 } && Frame(laid, Id("a", "execution")) is { W: 1_100, H: 700 }, "the request is short again and the 실행 block takes the size");
            // Once: a request size dragged after the split stays the request's, through a save and a restart.
            var later = migrated with { GraphBlockSizes = new(migrated.GraphBlockSizes!) { [Id("a", "request")] = own } };
            foreach (var again in new[] { Saved(later), Saved(Wire.Clone(later)) })
                Check(again.GraphBlockSizes?[Id("a", "request")] == own && again.GraphBlockSizes?[Id("a", "execution")] == dragged, "the hand-over happens once");
            // A new pane is in the current layout from the start, and keeps that through a save.
            var fresh = new RunSession { WorkspaceId = workspace.Id, Title = "New", GraphRuns = [new() { Id = "a", Input = "a", Status = "completed" }], GraphBlockSizes = new() { [Id("a", "request")] = own } };
            Check(fresh.GraphSizesLayout == GraphBlockPreferences.SplitLayout && Same(Saved(Wire.Clone(fresh)).GraphBlockSizes, (Id("a", "request"), own)), "a new pane's request size stays the request's");
            // A damaged marker reads as none, and never fails the pane.
            foreach (var value in new[] { "\"1\"", "1.5", "true", "{}", "[]", "null" })
                Check(JsonSerializer.Deserialize<RunSession>("{\"id\":\"s\",\"graphSizesLayout\":" + value + "}", Wire.Json)!.GraphSizesLayout is null, "a damaged layout marker reads as none: " + value);
            Check(JsonSerializer.Deserialize<RunSession>("{\"id\":\"s\",\"graphSizesLayout\":1}", Wire.Json)!.GraphSizesLayout == 1, "a saved layout marker is read");
        }
        finally { Directory.Delete(directory, true); }
        // The move itself: nothing to move is nothing, and a size that is not a request's stays where it is.
        Check(GraphBlockPreferences.SplittingExecution(null) is null && GraphBlockPreferences.SplittingExecution(new Dictionary<string, GraphBlockSize>()) is null
            && Same(GraphBlockPreferences.SplittingExecution(new Dictionary<string, GraphBlockSize> { [MightyGraphCamera.PendingNodeID] = own, [Id("a", "agent:request")] = own, [Id("a", "result")] = own }),
                (MightyGraphCamera.PendingNodeID, own), (Id("a", "agent:request"), own), (Id("a", "result"), own)), "only a request's size moves");
        return Task.CompletedTask;
    }

    internal static Task ARequestAndItsExecutionBlockShareOneSavedSizePlace()
    {
        var size = new GraphBlockSize(620, 360);
        var runs = Enumerable.Range(0, GraphBlockPreferences.MaximumSizes).Select(i => new MightyGraphRun { Id = "run-" + i }).ToList();
        var values = new Dictionary<string, GraphBlockSize>();
        foreach (var run in runs) { values[Id(run.Id, "request")] = size; values[Id(run.Id, "execution")] = size; }
        var saved = GraphBlockPreferences.Normalize(values, runs);
        // Every request keeps both sizes: the pair spends one place, not two.
        Check(GraphBlockPreferences.MaximumSizes == 1_024 && saved?.Count == 2 * GraphBlockPreferences.MaximumSizes && saved[Id("run-0", "request")] == size && saved[Id("run-0", "execution")] == size, "a request and its 실행 block share one place");
        // One request more than there are places: the oldest one's sizes go, the newest stay.
        runs.Add(new() { Id = "run-new" }); values[Id("run-new", "request")] = size;
        var full = GraphBlockPreferences.Normalize(values, runs);
        Check(full?.Count == 2 * GraphBlockPreferences.MaximumSizes - 1 && full.ContainsKey(Id("run-new", "request")) && !full.ContainsKey(Id("run-0", "request")) && !full.ContainsKey(Id("run-0", "execution")), "recent turns are preferred when the places run out");
        // A drag saved through the pane keeps the pair as well.
        var pane = new RunSession { GraphBlockSizes = saved };
        Check(GraphBlockPreferences.Set(pane, Id("run-0", "execution"), new(700, 500)).GraphBlockSizes is { Count: 2 * GraphBlockPreferences.MaximumSizes } set && set[Id("run-0", "execution")] == new GraphBlockSize(700, 500) && set[Id("run-0", "request")] == size,
            "saving one size keeps every pair");
        return Task.CompletedTask;
    }

    internal static Task AnExecutionBlocksSavedSizeIsKept()
    {
        var size = new GraphBlockSize(620, 360);
        var live = new MightyGraphRun { Id = "live", Status = "running", Agents = [Agent("child", status: "running")] };
        var record = Id("live", MightyGraphLayout.PlanRecordSuffix + "p1");
        var values = new Dictionary<string, GraphBlockSize>
        {
            [Id("live", "execution")] = size, [Id("gone", "execution")] = size, [Id("live", "agent:child")] = size, [Id("live", "agent:gone")] = size,
            [Id("live", "result")] = size, [MightyGraphCamera.PendingNodeID] = size, [record] = new(360, 40), ["other"] = size, [Id("live", "request")] = new(double.NaN, 100),
        };
        // Only nodes the session owns: its requests, their 실행 blocks, agents, answered plans and the draft; a result once its run settled.
        Check(Same(GraphBlockPreferences.Normalize(values, [live]), (Id("live", "execution"), size), (Id("live", "agent:child"), size), (MightyGraphCamera.PendingNodeID, size), (record, new(360, MightyGraphLayout.PlanRecordHeight(false)))),
            "a pane keeps the sizes of its own blocks only");
        live.Status = "completed"; live.Agents[0].Status = "completed";
        Check(GraphBlockPreferences.Normalize(values, [live])?[Id("live", "result")] == size, "a settled run keeps its result's size");
        Check(GraphBlockPreferences.Normalize(null, [live]) is null && GraphBlockPreferences.Normalize(new Dictionary<string, GraphBlockSize> { ["other"] = size }, [live]) is null, "nothing owned is nothing saved");
        return Task.CompletedTask;
    }
}
