using System.Text.Json;
using MightyClaude.Core;

internal static class MightyTimelineVerification
{
    private static void Check(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
    private static MightyGraphRun Run(string id, string status = "running", string? final = null, params MightyGraphAgent[] agents)
    {
        var run = new MightyGraphRun { Id = id, Status = status, Input = "request " + id, FinalOutput = final, Agents = [.. agents] };
        MightyGraphSupport.RefreshResult(run); return run;
    }
    internal static Task RecordedOrderAndResultsMatchDiagram()
    {
        var agents = new[] { new MightyGraphAgent { Id = "child", Title = "Inspect", Status = "completed" }, new MightyGraphAgent { Id = "task", Kind = "task", Status = "running", Entries = [new("step", "system", "tool", "2026-10-04T10:00:00Z", Activity: new("step", "claude", "tool", "running", "read 한국어.cs", "Read"))] }, new MightyGraphAgent { Id = "ask", Kind = "question", Status = "waiting" } };
        var runs = new[] { Run("old", "completed", "done"), Run("new", agents: agents) };
        var groups = MightyTimeline.Groups(runs); var rows = groups[1].Rows;
        // The request first, its 실행 block, then the agents in the order the graph recorded them.
        Check(groups.Select(g => g.Ordinal).SequenceEqual([1, 2]) && rows.Select(r => r.Kind).SequenceEqual(["main", MightyTimeline.ExecutionKind, "agent", "task", "question"]), "same request and child ordering as Mac and diagram");
        Check(rows.Select(r => r.AgentIndex).SequenceEqual(new int?[] { null, null, 0, 1, 2 }) && rows.Select(r => r.IsExecution).SequenceEqual([false, true, false, false, false]) && rows[3].Latest == "read 한국어.cs" && rows[2].Latest is null, "live activity only on unfinished blocks");
        Check(rows[0].NodeId == MightyGraphBlockSize.NodeId("new", "request") && rows[1].NodeId == MightyGraphBlockSize.NodeId("new", MightyGraphLayout.WorkSuffix) && rows[3].NodeId == MightyGraphBlockSize.NodeId("new", "agent:task"), "each row stands for its diagram node");
        // The 실행 block names its request, so each one reads apart from the others.
        Check(rows[0].Title == Locale.Get("graph.timeline.requestOrdinal", new Dictionary<string, string> { ["n"] = "2" }) && rows[1].Title == Locale.Get("graph.block.executionOf", new Dictionary<string, string> { ["n"] = "2" }) && rows[1].Title != groups[0].Rows[1].Title
            && MightyTimeline.KindLabel(MightyTimeline.ExecutionKind) == Locale.Get("graph.block.execution"), "the 실행 row is named for its request");
        var diagram = MightyGraphLayout.Make(runs, "", true, new HashSet<string>(), splitsExecution: true);
        Check(groups.SelectMany(g => g.Rows).All(r => diagram.Nodes.Any(n => n.Id == r.NodeId)), "every timeline row shares a real diagram node identity");
        Check(groups[0].Result is { Text: "done", Tone: DesignTone.Done } result && diagram.Nodes.Any(n => n.Id == result.NodeId), "result identity and final answer are shared");
        Check(MightyTimeline.Groups([Run("pending-child", "completed", "premature", new MightyGraphAgent { Id = "still-running" })])[0].Result is null, "an unfinished child prevents a premature result");
        Check(MightyTimeline.Groups([Run("failure", "failed")])[0].Result is { Tone: DesignTone.Err, Text: null } && MightyTimeline.Groups([Run("stop", "interrupted")])[0].Result?.Tone == DesignTone.Stop, "terminal aliases match diagram result states");
        // A steer delegation sent is a 위임 알림; one a person sent stays a 중간 요청, and only those are counted in the toolbar.
        var notice = new MightyGraphAgent { Id = "notice", Kind = "steer", Status = "completed", Input = "[Mighty Claude notice n-1] Your child c-1 reported revision 1." };
        var followUp = new MightyGraphAgent { Id = "follow", Kind = "steer", Status = "completed", Input = "[Mighty Claude follow-up f-1] Your parent sent you this follow-up:" };
        var person = new MightyGraphAgent { Id = "person", Kind = "steer", Status = "completed", Input = "Also add tests" };
        Check(MightyGraphSupport.BlockTitle(notice) == "위임 알림" && MightyGraphSupport.BlockTitle(followUp) == "위임 알림" && MightyGraphSupport.BlockTitle(person) == "중간 요청", "a delegation notice is named 위임 알림, a person's steer 중간 요청");
        Check(MightyTimeline.KindLabel("steer", notice.Input) == "위임 알림" && MightyTimeline.KindLabel("steer", person.Input) == "중간 요청" && MightyGraphSupport.BlockKind(notice) == "steer", "the timeline names the kind the same way, and the kind stays steer");
        var steered = MightyGraphBlockModel.ToolbarSummary([Run("steered", "completed", "done", notice, followUp, person)]);
        Check(steered.Contains("중간 요청 1", StringComparison.Ordinal) && !steered.Contains("중간 요청 3", StringComparison.Ordinal), "the toolbar counts only a person's steers; got " + steered);
        // A run that ends before a steer is answered says so in the block's words: a delegation notice's or a person's.
        var emitted = new Dictionary<string, ExecutionGraphNode>();
        var tracker = new ExecutionGraphTracker("steer-run", "root prompt", "claude", null, node => emitted[node.Id] = node);
        tracker.Steer("n", notice.Input!); tracker.Steer("f", followUp.Input!); tracker.Steer("p", person.Input!);
        tracker.Finish("completed");
        string Unfinished(string id) => emitted[ExecutionGraphSupport.Identifier("steer-run", "steer:" + id)].Entries[^1].Text;
        Check(Unfinished("n") == Locale.Get("graph.block.unfinished.delegationNotice") && Unfinished("f") == Locale.Get("graph.block.unfinished.delegationNotice")
            && Unfinished("p") == Locale.Get("graph.block.unfinished.steer") && Locale.Get("graph.block.unfinished.delegationNotice") != Locale.Get("graph.block.unfinished.steer"),
            $"an unanswered delegation notice ends with its own words, a person's steer with the 중간 요청 words; got '{Unfinished("n")}', '{Unfinished("f")}', '{Unfinished("p")}'");
        return Task.CompletedTask;
    }
    /// macOS MightyTimelineTests.theRequestRowHoldsTheInputAndTheExecutionRowTheWork.
    internal static Task TheRequestRowHoldsTheInputAndTheExecutionRowTheWork()
    {
        static LogEntry Step(string id, string summary) => new(id, "system", "tool", "2026-10-04T10:00:00Z", Activity: new(id, "claude", "tool", "running", summary, "Bash"));
        var run = Run("r", agents: new MightyGraphAgent { Id = "a", Status = "running" });
        run.RootEntries = [Step("s1", "rg render"), Step("s2", "sed -n 1,40p view.swift")];
        var rows = MightyTimeline.Groups([run])[0].Rows;
        // The request: what was asked, in its neutral state, with nothing it did.
        Check(rows[0] is { Kind: "main", Status: MightyTimeline.RequestStatus, Latest: null, DurationMs: null, IsExecution: false, AgentIndex: null }
            && rows[0].Node == MightyTimeline.RequestNode && MightyTimeline.RequestNode == new MightyTimeline.Node(DesignTone.Idle, false, null), "the request row is neutral and holds no work");
        // Its 실행 block: the run's status and steps.
        Check(rows[1] is { Kind: MightyTimeline.ExecutionKind, Status: "running", Latest: "sed -n 1,40p view.swift", IsExecution: true, AgentIndex: null } && rows[1].Node == MightyTimeline.NodeFor("running"), "the 실행 row holds the work");
        // A request that has nothing yet still has its 실행 row, waiting.
        var fresh = MightyTimeline.Groups([Run("n")])[0];
        Check(fresh.Rows.Select(r => r.IsExecution).SequenceEqual([false, true]) && fresh.Rows[1] is { Status: "running", Latest: null }, "a new request has its 실행 row at once");
        // Only the 실행 block counts: one block, none finished, not the request as a finished block.
        Check(fresh.Tally == new MightyTimeline.Tally(1, 0, 1), "the tally leaves the request out");
        // A request whose work failed or stopped never reads as a success, nor as the failure.
        foreach (var (status, tone) in new[] { ("error", DesignTone.Err), ("stopped", DesignTone.Stop), ("completed", DesignTone.Done) })
        {
            var ended = MightyTimeline.Groups([Run("e", status)])[0];
            Check(ended.Rows[0].Node.Tone == DesignTone.Idle && ended.Rows[1].Node.Tone == tone && ended.Tally == new MightyTimeline.Tally(1, 1, 0), "the request row stays neutral when its work ends: " + status);
        }
        return Task.CompletedTask;
    }
    internal static Task StatusRailsCountsAndDurationStayHonest()
    {
        var run = Run("mixed", agents: new[] { "completed", "error", "stopped", "running", "waiting" }.Select((s, i) => new MightyGraphAgent { Id = "a" + i, Status = s }).ToArray());
        var group = MightyTimeline.Groups([run])[0];
        // The 실행 block (running) and the five agents; the request is not a block that settles.
        Check(group.Tally == new MightyTimeline.Tally(6, 3, 2), "tally counts only blocks present");
        // The request's node is neutral and leaves the track; its 실행 block runs.
        Check(MightyTimeline.RailAbove(group.Rows, 0) is null && MightyTimeline.RailAbove(group.Rows, 1) is null && MightyTimeline.RailAbove(group.Rows, 2) == DesignTone.Run && MightyTimeline.RailAbove(group.Rows, 3) == DesignTone.Done && MightyTimeline.RailAbove(group.Rows, 4) is null && MightyTimeline.RailAbove(group.Rows, 9) is null, "only running and completed nodes light the following rail");
        foreach (var status in new[] { "idle", "starting", "queued", "future-state" }) Check(MightyTimeline.NodeFor(status) is { Tone: DesignTone.Run, Ring: true }, "unknown in-flight status remains running: " + status);
        var timed = Run("timed", "completed"); timed.RootEntries = [new("1", "user", "prompt", "2026-10-04T10:00:00Z"), new("2", "assistant", "answer", "2026-10-04T10:00:01.234Z")];
        Check(MightyTimeline.Groups([timed])[0].Rows is [{ DurationMs: null }, { DurationMs: 1234 }], "settled block reports measured own record span, on the 실행 row");
        timed.Status = "running"; Check(MightyTimeline.Groups([timed])[0].Rows[1].DurationMs is null, "running blocks cannot claim final duration");
        Check(MightyTimeline.DurationLabel(double.NaN) is null && MightyTimeline.DurationLabel(-1) is null && MightyTimeline.DurationLabel(999) == "999ms", "invalid duration never reaches labels");
        Check(!MightyTimeline.Folds(string.Concat(Enumerable.Repeat("👨‍👩‍👧", 600))) && MightyTimeline.Folds(string.Concat(Enumerable.Repeat("가", 601))) && MightyTimeline.Folds("1\n2\n3\n4\n5\n6\n7\n8\n9"), "result folding counts graphemes or actual lines, not UTF16 units");
        var groups = MightyTimeline.Groups([Run("older"), Run("latest")]); var flipped = new HashSet<string>();
        Check(!MightyTimeline.GroupOpen(groups[0], 2, flipped) && MightyTimeline.GroupOpen(groups[1], 2, flipped), "latest group starts open; old group retains its result below collapsed rows");
        flipped.Add("latest"); Check(!MightyTimeline.GroupOpen(groups[1], 2, flipped), "manual group toggle is independent of row/result expansion");
        return Task.CompletedTask;
    }
    internal static Task SavedModeIsIndependentAndLenient()
    {
        var pane = new RunSession { Id = "s", WorkspaceId = "w", Draft = "한글 초안", AgentViewMode = "mighty", Model = "saved-model", GraphResultSize = new(700, 500) };
        var selected = MightyTimeline.ApplyMode(pane, "timeline"); var restored = Wire.Clone(selected);
        Check(restored.GraphViewMode == "timeline" && restored.AgentViewMode == "mighty" && restored.Draft == pane.Draft && restored.GraphResultSize == pane.GraphResultSize && restored.Model == pane.Model, "persisted timeline changes only graph presentation");
        foreach (var value in new[] { "\"future\"", "123", "true", "{}", "[]", "null" })
        {
            var legacy = JsonSerializer.Deserialize<RunSession>("{\"id\":\"s\",\"graphViewMode\":" + value + "}", Wire.Json)!;
            Check(legacy.GraphViewMode is null && MightyTimeline.Mode(legacy) == "diagram", "unknown persisted value defaults to diagram: " + value);
        }
        Check(MightyTimeline.ApplyMode(pane, "unknown") == pane && MightyTimeline.ApplyMode(pane with { Kind = "shell" }, "timeline").GraphViewMode is null, "unsupported modes and non-agent panes are not changed");
        var directory = Verification.Temp();
        try
        {
            var state = StateStore.Normalize(new AppSnapshot { Workspaces = [new Workspace { Id = "w", Path = directory }], Sessions = [selected, pane with { Id = "sh", Kind = "shell", GraphViewMode = "timeline" }] }, true);
            Check(state.Sessions.Single(s => s.Id == "s").GraphViewMode == "timeline" && state.Sessions.Single(s => s.Id == "sh").GraphViewMode is null, "restore retains agent preference and drops shell-only corruption");
        }
        finally { Directory.Delete(directory, true); }
        return Task.CompletedTask;
    }
}
