using System.Globalization;
using System.Text.Json;
using MightyCode.Core;

/// <summary>
/// A request's checklist as steps on its spine and in its plan column (Core FlowSteps, macOS FlowStepsTests), and the heartbeat Claude Code sends 30 s
/// into a foreground Bash call, which is no sub-agent (macOS ToolHeartbeatTests; native/contracts/fixtures/claude-stream-tool-heartbeat.jsonl).
/// </summary>
internal static class FlowStepsVerification
{
    private static void Check(bool value, string why) { if (!value) throw new InvalidOperationException(why); }
    private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
    private static DateTimeOffset At(double seconds) => T0.AddSeconds(seconds);
    private static string Stamp(double seconds) => FlowSteps.Stamp(At(seconds));
    private static TodoItem Item(string content, string status, string? id = null) => new(content, status, content + " 중", id);
    private static TodoProgress List(IReadOnlyList<TodoItem> items, string run = "r") => new(items, run);
    private static MightyGraphRun Request(string id = "r", string status = "running", List<LogEntry>? entries = null, List<MightyGraphAgent>? agents = null) =>
        new() { Id = id, Input = "릴리스", Status = status, RootEntries = entries ?? [], Agents = agents ?? [], SourceRunID = id, Provider = "claude" };
    private static LogEntry Tool(string id, string summary, double at) =>
        new(id, "system", summary, Stamp(at), "claude", new AgentActivity(id, "claude", "command", "completed", summary, "Bash"));
    private static void Korean(Action action) => FlowTrackerVerification.InLanguage("ko", action);

    internal static Task EachChangeKeepsTheTimesOfTheStepsItHad()
    {
        var runs = new List<MightyGraphRun> { Request() };
        FlowSteps.Record(List([Item("점검", "in_progress"), Item("노트", "pending")]), runs, null, At(0));
        FlowSteps.Record(List([Item("점검", "completed"), Item("노트", "in_progress")]), runs, null, At(10));
        FlowSteps.Record(List([Item("점검", "completed"), Item("노트", "completed"), Item("CI", "pending")]), runs, null, At(25));
        var steps = runs[0].Steps!;
        Check(steps.Select(s => s.Content).SequenceEqual(["점검", "노트", "CI"]) && steps.Select(s => s.Status).SequenceEqual(["completed", "completed", "pending"]), "the steps and their states");
        Check(steps[0].Started == At(0) && steps[0].Ended == At(10) && steps[1].Started == At(10) && steps[1].Ended == At(25), "each began and ended when the list said so");
        Check(steps[2].StartedAt is null && steps[2].EndedAt is null && steps.Select(s => s.ActiveForm).SequenceEqual(["점검 중", "노트 중", "CI 중"]), "a step not begun, and the active forms");

        // Marked done without going in progress: it began when the one before it ended.
        var skipped = new List<MightyGraphRun> { Request() };
        FlowSteps.Record(List([Item("하나", "in_progress"), Item("둘", "pending")]), skipped, null, At(0));
        FlowSteps.Record(List([Item("하나", "completed"), Item("둘", "pending")]), skipped, null, At(5));
        FlowSteps.Record(List([Item("하나", "completed"), Item("둘", "completed")]), skipped, null, At(9));
        Check(skipped[0].Steps![1].Started == At(5) && skipped[0].Steps![1].Ended == At(9), "a step done at once began at the last end");

        // A task found by its id when its words change.
        var tasks = new List<MightyGraphRun> { Request() };
        FlowSteps.Record(List([Item("빌드", "in_progress", "1")]), tasks, null, At(0));
        FlowSteps.Record(List([Item("빌드와 서명", "in_progress", "1")]), tasks, null, At(4));
        Check(tasks[0].Steps is [{ Content: "빌드와 서명" } only] && only.Started == At(0), "the same task, renamed");

        // The earlier request's done tasks are never this request's steps.
        var earlier = new TodoProgress([Item("이전 일", "completed", "1")], "old");
        var carried = new List<MightyGraphRun> { Request("old", "completed"), Request("r") };
        FlowSteps.Record(List([Item("이전 일", "completed", "1"), Item("새 일", "in_progress", "2")]), carried, earlier, At(0));
        FlowSteps.Record(List([Item("이전 일", "completed", "1"), Item("새 일", "completed", "2")]), carried, earlier, At(3));
        Check(carried[1].Steps!.Select(s => s.Content).SequenceEqual(["새 일"]) && carried[0].Steps is null, "carried over, and stays out");

        var elsewhere = new List<MightyGraphRun> { Request() };
        FlowSteps.Record(List([Item("x", "in_progress")], "nobody"), elsewhere, null, At(0));
        Check(elsewhere[0].Steps is null, "a list of no run of the pane changes nothing");
        return Task.CompletedTask;
    }

    internal static Task ThePaneKeepsTheStepsThroughItsEvents()
    {
        var pane = new RunSession { Id = "s", Kind = "claude", Provider = "claude", Status = "running", GraphRuns = [Request()] };
        var snapshot = new AppSnapshot { Sessions = [pane] }.Apply(new RunEvent("s", "todos", Todos: List([Item("점검", "in_progress")])));
        var after = snapshot.Sessions.Single();
        Check(after.GraphRuns![0].Steps is [{ Status: "in_progress", StartedAt: not null }] && after.TodoProgress?.Items.Count == 1, "the step is on the request and the list on the pane");
        // The run pipeline's graph of the request carries no checklist: the steps stay.
        var fresh = Request();
        snapshot = snapshot.Apply(new RunEvent("s", "graph_run", GraphRun: fresh));
        Check(snapshot.Sessions.Single().GraphRuns![0].Steps?.Count == 1, "a graph run keeps the steps it had");
        // Saved and read back.
        var json = JsonSerializer.Serialize(snapshot.Sessions.Single().GraphRuns![0], Wire.Json);
        Check(json.Contains("\"steps\":[{\"key\":\"text:점검\"", StringComparison.Ordinal) || json.Contains("\"steps\":[{\"key\":\"text:\\uC810\\uAC80\"", StringComparison.Ordinal), "written as steps: " + json[..Math.Min(json.Length, 300)]);
        var back = JsonSerializer.Deserialize<MightyGraphRun>(json, Wire.Json)!;
        Check(back.Steps?.Count == 1 && back.Steps[0] == snapshot.Sessions.Single().GraphRuns![0].Steps![0], "read back");
        return Task.CompletedTask;
    }

    internal static Task AStepHoldsWhatItsSpanHoldsAndItsCardSaysIt()
    {
        var steps = new List<FlowStep> { new("a", "a", null, "completed", Stamp(10), Stamp(20)), new("b", "b", null, "in_progress", Stamp(20.4)), new("c", "c") };
        Check(FlowSteps.StepIndex(At(5), steps) is null && FlowSteps.StepIndex(At(10), steps) == 0 && FlowSteps.StepIndex(At(15), steps) == 0, "inside the first span");
        Check(FlowSteps.StepIndex(At(20), steps) == 1 && FlowSteps.StepIndex(At(99), steps) == 1, "where two meet, the later; from its whole second");
        Check(FlowSteps.NodeSuffix(new("text:릴리스 노트 / 검토", "x")) is var suffix && suffix.StartsWith("step:", StringComparison.Ordinal) && suffix[5..].All(Uri.IsHexDigit)
            && suffix == FlowSteps.NodeSuffix(new("text:릴리스 노트 / 검토", "다른 말")), "a node id never carries the step's words");
        Check(FlowSteps.StableHash("") == "cbf29ce484222325" && FlowSteps.StableHash("a") == "af63dc4c8601ec8c", "FNV-1a 64 as the Mac writes it");

        Korean(() =>
        {
            var run = Request(entries: [Tool("e1", "git status", 1), Tool("e2", "python scripts/release.py check", 3), Tool("e3", "git push", 12)]);
            run.Steps = [new("a", "점검", "점검 중", "completed", Stamp(0), Stamp(10)), new("b", "푸시", "푸시 중", "in_progress", Stamp(10)), new("c", "CI")];
            var runs = new List<MightyGraphRun> { run };
            var done = FlowSteps.Card(0, 0, runs)!;
            Check(done.Title == "1. 점검" && done.Status == "completed" && done.ToolCalls == 2 && done.Activity == "python scripts/release.py check", "a done step's card");
            Check(FlowCard.Elapsed(done, At(99)) == StylePresentation.Elapsed(At(0), At(10)), "its time");
            var now = FlowSteps.Card(1, 0, runs)!;
            Check(now.Running && now.ToolCalls == 1 && now.Activity == "git push", "the step in progress");
            var pending = FlowSteps.Card(2, 0, runs)!;
            Check(pending.IsPending && !pending.Running && pending.StateWord == "예정" && pending.Activity is null && FlowCard.Elapsed(pending, At(99)) is null, "a step not begun");
            Check(FlowSteps.Entries(0, 0, runs).Select(e => e.Id).SequenceEqual(["e1", "e2"]), "what the first step holds");
            var notice = new MightyGraphRun { Id = "r:notice:1", Status = "completed", RootEntries = [Tool("n1", "gh run watch", 14)], SourceRunID = "r:notice:1", Provider = "claude", NoticeRun = true };
            Check(FlowSteps.Entries(1, 0, [run, notice]).Select(e => e.Id).SequenceEqual(["e3", "n1"]), "a notice turn's records are the activity's too");

            // The 실행 card: the newest thing the main session said or did; ended, its answer's first line.
            var said = new LogEntry("s1", "assistant", "먼저 상태를 봅니다.\n**화면을 찍어 보겠습니다.**", Stamp(2), "claude");
            var lead = Request(entries: [Tool("l1", "ls", 1), said]);
            Check(FlowSteps.LeadCard(lead, "실행") is { Activity: "화면을 찍어 보겠습니다.", Prose: true }, "what it last said, as prose");
            lead.RootEntries.Add(Tool("l2", "screencapture -x shot.png", 3));
            Check(FlowSteps.LeadCard(lead, "실행") is { Activity: "screencapture -x shot.png", Prose: false }, "a newer command, as code");
            var answered = Request(status: "completed", entries: [Tool("x1", "ls", 1), Tool("x2", "make", 2)]);
            answered.FinalOutput = "완료했습니다.\n자세한 내용";
            Check(FlowSteps.LeadCard(answered, "실행 · 요청 1") is { Activity: "완료했습니다.", Prose: true, ToolCalls: 2, Running: false }, "ended: its answer");

            // The progress panel: the request in brief, its state, every step and its state.
            var progress = FlowSteps.ProgressOf(0, runs, "요청 27");
            Check(progress.Title == "진행 상황" && progress.Heading == "요청 27" && progress.Count == "1/3" && progress.Running && progress.Status == "running"
                && progress.StateWord == "실행 중", "its head and its request's state");
            Check(progress.Rows.Select(r => r.Text).SequenceEqual(["1. 점검", "2. 푸시 중", "3. CI"])
                && progress.Rows.Select(r => r.State).SequenceEqual([FlowSteps.ProgressState.Done, FlowSteps.ProgressState.Current, FlowSteps.ProgressState.Pending]), "every step and its state");
            var stopped = Request(status: "stopped", entries: run.RootEntries); stopped.Steps = run.Steps;
            var halted = FlowSteps.ProgressOf(0, [stopped], "요청 27");
            Check(halted.Rows[1].State == FlowSteps.ProgressState.Stopped && halted.StateWord == "중단" && !halted.Running, "stopped with a step in progress");
            var longRun = Request();
            longRun.Steps = Enumerable.Range(0, 30).Select(i => new FlowStep(i.ToString(CultureInfo.InvariantCulture), "단계 " + i, null, i < 20 ? "completed" : i == 20 ? "in_progress" : "pending", i <= 20 ? Stamp(i) : null)).ToList();
            var window = FlowSteps.ProgressOf(0, [longRun], "요청 1");
            Check(window.Rows.Count == FlowSteps.ProgressVisibleRows - 1 && window.Rows.Any(r => r.State == FlowSteps.ProgressState.Current) && window.Hidden + window.Rows.Count == 30, "11 steps and a line for the rest");
            var plain = FlowSteps.ProgressOf(0, [Request(status: "completed")], "요청 2");
            Check(plain.Rows.Count == 0 && plain.Count is null && plain.StateWord == "완료" && plain.Request == Request().Input.Split('\n')[0], "no plan: its words and its state, no rows");

            // A card beside a step names it.
            var inStep = Request(agents: [new MightyGraphAgent { Id = "notes", Title = "notes", Input = "노트", Status = "running", Figures = new GraphAgentFigures(Stamp(12)) },
                new MightyGraphAgent { Id = "early", Title = "early", Input = "먼저", Status = "completed", Figures = new GraphAgentFigures(Stamp(-5)) }]);
            inStep.Steps = [new FlowStep("a", "점검", null, "completed", Stamp(0), Stamp(10)), new FlowStep("b", "노트", null, "in_progress", Stamp(10))];
            Check(FlowSteps.StepLabel(inStep.Agents[0], inStep) == "2. 노트" && FlowSteps.StepLabel(inStep.Agents[1], inStep) is null, "the step a card was launched in");
            var layout = MightyGraphLayout.Make([inStep], "", true, new HashSet<string>(), splitsExecution: true, fanOut: new MightyGraphLayout.FanOut());
            Check(layout.Nodes.First(n => n.Id.EndsWith("agent:notes", StringComparison.Ordinal)).Frame is { W: 236, H: 104 }
                && layout.Nodes.First(n => n.Id.EndsWith("agent:early", StringComparison.Ordinal)).Frame is { W: 236, H: 86 } && layout.Nodes.All(n => n.Kind != "outline"),
                "its card has one more line, and the plan is no block");
        });
        return Task.CompletedTask;
    }

    internal static Task TheRecordedHeartbeatMakesNoBlock()
    {
        var runs = FlowTrackerVerification.Replay("claude-stream-tool-heartbeat", "recorded");
        Check(FlowTrackerVerification.Fixture("claude-stream-tool-heartbeat").Any(line => line.TryGetProperty("type", out var type) && type.GetString() == "tool_progress"), "the recording has the heartbeat");
        Check(runs.Count >= 1 && runs[0].Agents.Count == 0 && runs[0].Status == "completed", "no block: " + string.Join(", ", runs[0].Agents.Select(a => a.Title)));

        // A frame or a Mods spawn under a call the stream showed as another tool is no sub-agent; under an Agent call it is.
        var nodes = new List<ExecutionGraphNode>();
        var tracker = new ExecutionGraphTracker("run", "probe", "claude", null, nodes.Add);
        void Send(object value) => tracker.Consume(JsonSerializer.SerializeToElement(value));
        Send(new { type = "assistant", message = new { id = "m1", role = "assistant", content = new object[] { new { type = "tool_use", id = "read-1", name = "Read", input = new { file_path = "/tmp/a" } } } } });
        Send(new { type = "assistant", parent_tool_use_id = "read-1", message = new { id = "m2", role = "assistant", content = new object[] { new { type = "text", text = "x" } } } });
        tracker.ReceiveMod(new ModMetadata("session", "agent.spawn", Graph: new ModGraphMetadata(1, "starting", ParentToolUseId: "read-1")));
        Send(new { type = "assistant", message = new { id = "m3", role = "assistant", content = new object[] { new { type = "tool_use", id = "agent-1", name = "Agent", input = new { description = "Map the repo", prompt = "Map it" } } } } });
        Send(new { type = "assistant", parent_tool_use_id = "agent-1", message = new { id = "m4", role = "assistant", content = new object[] { new { type = "text", text = "mapping" } } } });
        Check(!nodes.Any(n => n.Id == ExecutionGraphSupport.AgentNodeID("run", "read-1")), "nothing under the Read call");
        Check(nodes.Any(n => n.Id == ExecutionGraphSupport.AgentNodeID("run", "agent-1") && n.Title == "Map the repo"), "the Agent call's block");
        return Task.CompletedTask;
    }

    internal static Task ASavedHeartbeatBlockIsDroppedAndSavedStepsAreBounded()
    {
        var heartbeat = new MightyGraphAgent { Id = "graph-bash", Title = "하위 에이전트", Input = "", Status = "completed",
            Entries = [new("graph-bash-answer", "assistant", "Build complete!", "2026-11-11T15:17:41Z", "claude")], Figures = new GraphAgentFigures("2026-11-11T15:17:41Z", "2026-11-11T15:19:02.999Z") };
        var real = new MightyGraphAgent { Id = "graph-agent", Title = "하위 에이전트", Input = "Map the repo", Status = "running" };
        var titled = new MightyGraphAgent { Id = "graph-named", Title = "Map", Input = "", Status = "completed" };
        var run = new MightyGraphRun { Id = "r", Input = "요청", Status = "completed", Agents = [heartbeat, real, titled], Provider = "claude" };
        var budget = 10_000_000;
        Check(MightyGraphSupport.Normalized([run], true, ref budget).Single().Agents.Select(a => a.Id).SequenceEqual(["graph-agent", "graph-named"]), "only the heartbeat's block goes");

        var stepped = new MightyGraphRun { Id = "s", Input = "요청", Status = "completed", Provider = "claude",
            Steps = Enumerable.Range(0, 150).Select(i => new FlowStep("text:" + i, "단계 " + i, null, i == 0 ? "done?" : "completed", "not a time")).ToList() };
        budget = 10_000_000;
        var steps = MightyGraphSupport.Normalized([stepped], true, ref budget).Single().Steps!;
        Check(steps.Count == TodoProgress.MaximumItems && steps[0].Status == "pending" && steps.All(s => s.StartedAt is null), "as many as a list holds, statuses and times checked");
        return Task.CompletedTask;
    }
}
