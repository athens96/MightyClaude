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
        Check(groups.Select(g => g.Ordinal).SequenceEqual([1, 2]) && rows.Select(r => r.Kind).SequenceEqual(["main", "agent", "task", "question"]), "same request and child ordering as Mac and diagram");
        Check(rows.Select(r => r.AgentIndex).SequenceEqual(new int?[] { null, 0, 1, 2 }) && rows[2].Latest == "read 한국어.cs" && rows[1].Latest is null, "live activity only on unfinished blocks");
        var diagram = MightyGraphLayout.Make(runs, "", true, new HashSet<string>());
        Check(groups.SelectMany(g => g.Rows).All(r => diagram.Nodes.Any(n => n.Id == r.NodeId)), "every timeline row shares a real diagram node identity");
        Check(groups[0].Result is { Text: "done", Tone: DesignTone.Done } result && diagram.Nodes.Any(n => n.Id == result.NodeId), "result identity and final answer are shared");
        Check(MightyTimeline.Groups([Run("pending-child", "completed", "premature", new MightyGraphAgent { Id = "still-running" })])[0].Result is null, "an unfinished child prevents a premature result");
        Check(MightyTimeline.Groups([Run("failure", "failed")])[0].Result is { Tone: DesignTone.Err, Text: null } && MightyTimeline.Groups([Run("stop", "interrupted")])[0].Result?.Tone == DesignTone.Stop, "terminal aliases match diagram result states");
        return Task.CompletedTask;
    }
    internal static Task StatusRailsCountsAndDurationStayHonest()
    {
        var run = Run("mixed", agents: new[] { "completed", "error", "stopped", "running", "waiting" }.Select((s, i) => new MightyGraphAgent { Id = "a" + i, Status = s }).ToArray());
        var group = MightyTimeline.Groups([run])[0];
        Check(group.Tally == new MightyTimeline.Tally(6, 3, 2), "tally counts only blocks present");
        Check(MightyTimeline.RailAbove(group.Rows, 0) is null && MightyTimeline.RailAbove(group.Rows, 1) == DesignTone.Run && MightyTimeline.RailAbove(group.Rows, 2) == DesignTone.Done && MightyTimeline.RailAbove(group.Rows, 3) is null, "only running and completed nodes light the following rail");
        foreach (var status in new[] { "idle", "starting", "queued", "future-state" }) Check(MightyTimeline.NodeFor(status) is { Tone: DesignTone.Run, Ring: true }, "unknown in-flight status remains running: " + status);
        var timed = Run("timed", "completed"); timed.RootEntries = [new("1", "user", "prompt", "2026-10-04T10:00:00Z"), new("2", "assistant", "answer", "2026-10-04T10:00:01.234Z")];
        Check(MightyTimeline.Groups([timed])[0].Rows[0].DurationMs == 1234, "settled block reports measured own record span");
        timed.Status = "running"; Check(MightyTimeline.Groups([timed])[0].Rows[0].DurationMs is null, "running blocks cannot claim final duration");
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
