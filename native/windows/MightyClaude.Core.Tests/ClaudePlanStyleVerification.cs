using System.Reflection;
using System.Text;
using System.Text.Json;
using MightyClaude.Core;

/// <summary>
/// The "claude-plan" bundled style and the v6 vocabulary (docs/mighty-styles.md §1.17), against the same
/// manifest and the same golden the macOS StylesClaudePlanTests read.
/// </summary>
internal static class ClaudePlanStyleVerification
{
    private static void Check(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }

    private static RegisteredStyle Bundled()
    {
        var root = Verification.Temp();
        try { return StyleRegistry.Load(Path.Combine(root, "profile"), Path.Combine(root, "workspace")).Styles.Single(s => s.Id == "claude-plan"); }
        finally { Directory.Delete(root, true); }
    }

    private static string ManifestText(RegisteredStyle style) => Encoding.UTF8.GetString(style.Bytes.Span);

    private static string? Code(string text)
    {
        try { StyleManifestDecoder.Decode(Encoding.UTF8.GetBytes(text)); return null; }
        catch (StyleManifestException e) { return e.Code; }
    }

    private static string? Replaced(string text, string old, string replacement)
    {
        Check(text.Contains(old, StringComparison.Ordinal), "fixture text not found: " + old);
        return Code(text.Replace(old, replacement, StringComparison.Ordinal));
    }

    private static T Korean<T>(Func<T> body)
    {
        var saved = Locale.LanguagePreference; Locale.LanguagePreference = "ko"; Locale.ResetCache();
        try { return body(); }
        finally { Locale.LanguagePreference = saved; Locale.ResetCache(); }
    }

    private static PlanRecord Record(string outcome, string runId = "plan-pane", string? graphRunId = "proc-1", string id = "req-1", string? launchOverride = null) =>
        new(id, runId, "# Plan", "2026-10-06T01:00:00.000Z", "2026-10-06T01:01:00.000Z", outcome, GraphRunId: graphRunId, LaunchOverride: launchOverride);

    private static MightyGraphRun Run(string id, string? source, string status = "completed") => new() { Id = id, Input = "request", Status = status, SourceRunID = source };

    private static RunSession Pane(List<MightyGraphRun>? runs = null, List<PlanRecord>? plans = null, TodoProgress? todos = null, BackgroundWork? work = null) =>
        new() { Id = "plan-pane", WorkspaceId = "ws", Provider = "claude", AgentViewMode = "mighty", MightyStyle = "claude-plan", GraphRuns = runs, PlanHistory = plans, TodoProgress = todos, BackgroundWork = work };

    /// <summary>The golden's fixed run state (M/StyleGoldenSupport.swift stateRunState).</summary>
    private static (TodoProgress Todos, BackgroundWork Work) Fixture()
    {
        var items = Enumerable.Range(1, 7).Select(i => new TodoItem("step " + i, i <= 3 ? "completed" : i == 4 ? "in_progress" : "pending", "doing step " + i)).ToList();
        var work = new BackgroundWork([
            new BackgroundTask("bg-1", "agent", "review the diff", "2026-10-06T01:00:00.000Z"),
            new BackgroundTask("bg-2", "shell", "npm test", "2026-10-06T01:00:05.000Z", "completed", EndedAt: "2026-10-06T01:01:10.000Z"),
        ], TurnEnded: true);
        return (new TodoProgress(items), work);
    }

    internal static Task ManifestAndValidation()
    {
        var style = Bundled(); var evaluator = style.Evaluator; var m = style.Manifest;
        Check(style.Source == "bundled" && style.Runnable && m.Name == "클러드 플랜", "the plan style is a bundled, runnable style");
        Check(string.Join(",", m.Phases.OrderBy(p => p.Order).Select(p => p.Id)) == "plan,approve,execute", "three phases in order");
        Check(evaluator.ReadsPlanState && evaluator.LaunchPermissionMode == "plan" && evaluator.DrawsTasks, "reads the plan stage, launches in plan mode, draws tasks");
        Check(StyleEvaluator.RecognisedName(m, "[계획] 결제") == "계획]" && evaluator.RecognisedAction("[검증] 다시")?.Id == "verify", "both actions are recognised");
        var text = ManifestText(style);
        Check(Replaced(text, "\"map\": { \"planning\": \"plan\", \"awaitingApproval\": \"approve\", \"executing\": \"execute\" }", "\"map\": { \"planning\": \"plan\", \"awaitingApproval\": \"approve\" }") == "E_RULE_INCOMPLETE", "every stage is mapped");
        Check(Replaced(text, "\"executing\": \"execute\" }", "\"executing\": \"execute\", \"idle\": \"plan\" }") == "E_UNKNOWN_REFERENCE", "only the closed stages");
        Check(Replaced(text, "\"executing\": \"execute\" }", "\"executing\": \"run\" }") == "E_UNKNOWN_REFERENCE", "each stage names a phase");
        Check(Replaced(text, "\"kind\": \"planState\"", "\"kind\": \"planStage\"") == "E_UNKNOWN_RULE", "the phase rule kinds are closed");
        Check(Replaced(text, "\"source\": \"todos\",      \"widget\": \"progressBar\"", "\"source\": \"transcript\", \"widget\": \"progressBar\"") == "E_STATE_RUN_STATE", "run-state sources are closed");
        Check(Replaced(text, "\"source\": \"todos\",      \"widget\": \"label\"", "\"source\": \"todos\", \"widget\": \"taskList\"") == "E_STATE_WIDGET", "the checklist is not a task list");
        Check(Replaced(text, "\"source\": \"background\", \"widget\": \"label\"", "\"source\": \"background\", \"widget\": \"list\"") == "E_STATE_WIDGET", "background is not a plain list");
        Check(Replaced(text, "\"runState\": [", "\"files\": [{\"path\": \"a.md\", \"parser\": \"json\", \"widget\": \"taskList\"}], \"runState\": [") == "E_STATE_WIDGET", "a file is never a task list");
        Check(Replaced(text, "\"permissionMode\": \"plan\"", "\"permissionMode\": \"fullAccess\"") == "E_UNKNOWN_RULE", "the launch mode is closed");
        Check(Replaced(text, "\"launch\": { \"permissionMode\": \"plan\" }", "\"launch\": { \"permissionMode\": \"plan\", \"model\": \"opus\" }") == "E_UNKNOWN_FIELD", "the launch block is closed");
        Check(Replaced(text, "\"running\": \"승인한 계획대로 실행 중입니다\"", "\"running\": \"{text} 실행 중\"") == "E_PROMPT_PLACEHOLDER", "a phase's guidance keeps the template");
        Check(Replaced(text, "\"running\": \"승인한 계획대로 실행 중입니다\"", "\"busy\": \"실행 중\"") == "E_UNKNOWN_FIELD", "a phase's lines are closed");
        Check(Replaced(text, "\"approve\": {\n        \"running\": \"계획을 검토하고 승인하세요 · 바꿀", "\"review\": {\n        \"running\": \"계획을 검토하고 승인하세요 · 바꿀") == "E_UNKNOWN_REFERENCE", "a phase's own lines name a real phase");
        var sections = Korean(() => StylePresentation.Approval(style));
        Check(sections.Any(s => s.Id == "launch" && s.Lines[0].Contains("plan", StringComparison.Ordinal)), "the approval card shows how requests start");
        var state = sections.Single(s => s.Id == "state").Lines;
        Check(state.Contains("실행 상태 background · 위젯 taskList") && state.Contains("단계 approve ← 계획 상태 awaitingApproval"), "the approval card lists the run state and the stages");
        return Task.CompletedTask;
    }

    internal static Task StageRules()
    {
        Check(StyleStateEngine.PlanStage(Pane(), false) == "planning", "nothing asked is planning");
        Check(StyleStateEngine.PlanStage(Pane([Run("g1", "proc-1", "running")], [Record(PlanOutcome.Revised)]), true) == "awaitingApproval", "a waiting plan");
        foreach (var outcome in new[] { PlanOutcome.ApprovedAuto, PlanOutcome.ApprovedConfirm })
            Check(StyleStateEngine.PlanStage(Pane([Run("g1", "proc-1")], [Record(outcome)]), false) == "executing", "an approved plan executes: " + outcome);
        foreach (var outcome in new[] { PlanOutcome.Revised, PlanOutcome.Cancelled })
            Check(StyleStateEngine.PlanStage(Pane([Run("g1", "proc-1")], [Record(PlanOutcome.ApprovedAuto, id: "old"), Record(outcome, id: "new")]), false) == "planning", "sent back or cancelled plans again: " + outcome);
        Check(StyleStateEngine.PlanStage(Pane([Run("g1", "proc-1"), Run("g2", "proc-2", "running")], [Record(PlanOutcome.ApprovedAuto)]), false) == "planning", "the next new request plans again");
        Check(StyleStateEngine.PlanStage(Pane([Run("g1", "proc-1")], [Record(PlanOutcome.ApprovedAuto, graphRunId: "g1")]), false) == "executing", "the block id names the run too");
        Check(StyleStateEngine.PlanStage(Pane([Run("g1", "proc-1")], [Record(PlanOutcome.ApprovedAuto, runId: "proc-1", graphRunId: null)]), false) == "executing", "an older Mac record's run id names it too");
        // The new run started but its block is not there yet: the previous approved block is settled.
        var started = Pane([Run("g1", "proc-1")], [Record(PlanOutcome.ApprovedAuto)]) with { Status = "running" };
        Check(StyleStateEngine.PlanStage(started, false) == "planning", "a run whose block has not appeared plans");
        Check(StyleStateEngine.PlanStage(started with { BackgroundWork = new([new("b", "agent", "x", "2026-10-06T01:00:00.000Z")], TurnEnded: true) }, false) == "executing", "a turn that only waits on background work is still the approved request's");
        // A pane that never saved blocks reads its log like the Mac's legacy runs; no block at all reads the last record.
        var legacy = Pane(null, [Record(PlanOutcome.ApprovedAuto)]) with { Logs = [new(Wire.Id(), "user", "x", Wire.Now())] };
        Check(StyleStateEngine.PlanStage(legacy, false) == "planning", "the log's blocks own no record");
        Check(StyleStateEngine.PlanStage(legacy with { Logs = [] }, false) == "executing" && StyleStateEngine.PlanStage(Pane([], [Record(PlanOutcome.ApprovedAuto)]), false) == "executing", "no block reads the last record");
        Check(StyleStateEngine.PlanStage(Pane([Run("g1", "proc-1")], [Record(PlanOutcome.ApprovedAuto, runId: "proc-9", graphRunId: "proc-9")]), false) == "planning", "another request's plan is not this one's");

        var evaluator = Bundled().Evaluator; var m = evaluator.Manifest;
        Check(evaluator.CurrentPhase(["[검증] x"])?.Id == "plan" && evaluator.CurrentPhase([], null, "awaitingApproval")?.Id == "approve" && evaluator.CurrentPhase(["[계획] x"], null, "executing")?.Id == "execute", "the stage, not the history, moves the phase");
        StylePhase Phase(string id) => m.Phases.Single(p => p.Id == id);
        Check(string.Join(",", evaluator.VisibleActions(Phase("plan"), null, false).Select(a => a.Id)) == "new-plan", "plan offers a new plan");
        Check(evaluator.VisibleActions(Phase("approve"), null, true).Length == 0, "approval shows progress, not chips");
        Check(string.Join(",", evaluator.VisibleActions(Phase("execute"), null, false).Select(a => a.Id)) == "new-plan,verify", "after execution: new plan and verify");
        Check(evaluator.Placeholder(Phase("plan"), false) == "무엇을 계획할까요?" && evaluator.Placeholder(Phase("approve"), true).StartsWith("계획을 검토하고 승인하세요", StringComparison.Ordinal), "each phase's own composer line");
        Check(evaluator.Placeholder(Phase("approve"), true, answering: true) == "직접 답하려면 여기에 적고 Enter…", "a question still wins");
        Check(evaluator.Guidance(Phase("execute"), false) == "실행을 마쳤습니다. 새 계획을 세우거나 검증을 요청하세요." && evaluator.Guidance(Phase("execute"), true) == "승인한 계획대로 실행 중입니다", "each phase's own guidance");
        Check(evaluator.Guidance(Phase("approve"), false) == "승인 단계가 끝났습니다. 새 계획을 세우거나 검증을 요청하세요.", "no own line falls back to the style's");
        Check(evaluator.RewriteAction("결제", Phase("plan"), false, false, false) is null, "Enter is advisory");

        var request = new StartRunRequest("plan-pane", "ws", "claude", "결제", []);
        var root = Verification.Temp();
        try
        {
            var bound = StyleRunPermissions.Bind(request, Pane(), Path.Combine(root, "profile"), Path.Combine(root, "workspace"));
            Check(bound.PermissionModeOverride == "plan" && bound.Settings?.PermissionMode != "plan", "every new request starts in plan mode, the pane's mode untouched");
            bound.Validate();
            Check(ProviderCatalog.Arguments(bound, "/plugin") is var args && args.SkipWhile(a => a != "--permission-mode").Skip(1).FirstOrDefault() == "plan", "plan reaches Claude's argv");
            var other = StyleRunPermissions.Bind(request, Pane() with { MightyStyle = "ouroboros" }, Path.Combine(root, "profile"), Path.Combine(root, "workspace"));
            Check(other.PermissionModeOverride is null, "another style leaves the mode alone");
        }
        finally { Directory.Delete(root, true); }
        return Task.CompletedTask;
    }

    internal static Task WidgetsMatchTheGolden()
    {
        var style = Bundled(); var (todos, work) = Fixture();
        using var golden = JsonDocument.Parse(Resource("Styles.golden.claude-plan.panel.json"));
        var withState = golden.RootElement.GetProperty("withState");
        var widgets = Korean(() => StyleStateEngine.RunStateWidgets(style.Manifest, "executing", todos, work));
        var produced = JsonSerializer.Serialize(widgets.Select(StylePresentation.Payload));
        var expected = JsonSerializer.Serialize(withState.GetProperty("widgets"));
        Check(Normalised(produced) == Normalised(expected), "run-state widgets match the shared golden\n" + produced + "\n" + expected);
        foreach (var stage in StyleStateEngine.PlanStages)
        {
            var panel = golden.RootElement.GetProperty("planStage" + char.ToUpperInvariant(stage[0]) + stage[1..]);
            var phase = style.Evaluator.CurrentPhase([], null, stage);
            Check(phase?.Id == panel.GetProperty("phase").GetProperty("id").GetString(), "the phase at " + stage + " matches the golden");
            var running = stage == "awaitingApproval";
            Check(style.Evaluator.Guidance(phase, running) == panel.GetProperty("guidance").GetString(), "the guidance at " + stage + " matches the golden");
            var next = string.Join(",", style.Evaluator.VisibleActions(phase, null, running).Select(a => a.Id));
            Check(next == string.Join(",", panel.GetProperty("next").EnumerateArray().Select(e => e.GetString())), "the chips at " + stage + " match the golden");
        }
        var planning = Korean(() => StyleStateEngine.RunStateWidgets(style.Manifest, "planning", todos, work));
        Check(planning[0].Value == 0 && planning[0].Total == 0 && planning[1].Text == "", "a new plan does not show the last one's progress");
        var reading = StyleStateEngine.Live(null, style.Manifest, Pane([Run("g1", "proc-1")], [Record(PlanOutcome.ApprovedAuto)], todos, work), false);
        Check(reading.PlanStage == "executing" && reading.Widgets.Count == 4 && reading.Widgets[3].Tasks?.Count == 2, "the live reading adds the stage and the four widgets");
        var other = StyleRegistry.Load(Verification.Temp(), Verification.Temp()).Styles.Single(s => s.Id == "superpowers");
        Check(StyleStateEngine.Live(null, other.Manifest, Pane(), false).PlanStage is null, "another style never reads the stage");
        return Task.CompletedTask;
    }

    internal static Task TasksAndBackgroundStrip()
    {
        var (_, work) = Fixture();
        Korean(() =>
        {
            var rows = PlanCardSupport.BackgroundTasks(work);
            Check(rows.Count == 2 && rows[0].Text == "review the diff" && rows[0].Running && rows[0].KindTitle == "에이전트" && rows[1].StatusTitle == "완료", "running first, kind and status in words");
            var start = DateTimeOffset.Parse("2026-10-06T01:00:00.000Z", System.Globalization.CultureInfo.InvariantCulture);
            Check(rows[0].Elapsed(start.AddSeconds(45)) == "45초" && rows[0].Elapsed(start.AddSeconds(192)) == "3분 12초" && rows[1].Elapsed(start.AddHours(5)) == "1분 5초", "elapsed runs to now, or to the end");
            Check(StylePresentation.Elapsed(start, start.AddSeconds(3725)) == "1시간 2분" && StylePresentation.Elapsed(start, start.AddSeconds(-5)) == "0초", "hours, and a clock that went back");
            var odd = StylePresentation.Tasks(new StyleStateWidget("taskList", Tasks: [new StyleTaskItem(" \n", "weird", "nope", "2026-10-06T01:00:00.000Z")]));
            Check(odd[0].Kind == "other" && odd[0].Text == "작업" && odd[0].StatusTitle == "알 수 없음", "closed fallbacks, and the kind for an empty description");
            Check(PlanCardSupport.BackgroundSummary(work) == "턴 완료 · 백그라운드 1개 실행 중" && PlanCardSupport.BackgroundSummary(work with { TurnEnded = false }) == "백그라운드 1개 실행 중", "the folded line");
            return 0;
        });
        var many = new BackgroundWork(Enumerable.Range(0, 12).Select(i => new BackgroundTask("t" + i, "agent", "task " + i, "2026-10-06T01:00:00.000Z")).ToList());
        Check(PlanCardSupport.BackgroundTasks(many).Count == StyleStateEngine.MaximumTaskListItems, "at most eight tasks");
        var running = work with { TurnEnded = false };
        Check(PlanCardSupport.ShowsBackgroundStrip(work, false, false) && !PlanCardSupport.ShowsBackgroundStrip(running, false, false), "outside Mighty only once the turn is over");
        Check(PlanCardSupport.ShowsBackgroundStrip(running, true, false) && !PlanCardSupport.ShowsBackgroundStrip(work, true, true) && !PlanCardSupport.ShowsBackgroundStrip(null, true, false), "Mighty all along, unless the style draws its own");
        var payload = JsonSerializer.Serialize(StylePresentation.Payload(new StyleStateWidget("taskList", Tasks: [new StyleTaskItem("x", "agent", "running", "2026-10-06T01:00:00.000Z")])));
        Check(payload.Contains("\"kind\":\"taskList\"", StringComparison.Ordinal) && !payload.Contains("endedAt", StringComparison.Ordinal), "the task list payload carries only what it has");
        return Task.CompletedTask;
    }

    /// <summary>Keys sorted at every level, as the golden writes them.</summary>
    internal static Task QueueAndStoredMode()
    {
        var running = new BackgroundTask("b", "agent", "x", "2026-10-06T01:00:00.000Z");
        var waiting = new BackgroundWork([running], TurnEnded: true); var busy = new BackgroundWork([running]);
        Check(BackgroundQueuePolicy.ComposerJoins(false, waiting, false) && !BackgroundQueuePolicy.ComposerJoins(false, waiting, true) && BackgroundQueuePolicy.ComposerJoins(true, waiting, true), "the composer joins unless the style plans each request; Ctrl+Enter still steers");
        Check(!BackgroundQueuePolicy.ComposerJoins(false, busy, false) && !BackgroundQueuePolicy.ComposerJoins(false, null, false), "a turn still running is not joined by Enter");
        Check(BackgroundQueuePolicy.PhoneSteers(null, waiting, false) && !BackgroundQueuePolicy.PhoneSteers(null, waiting, true) && !BackgroundQueuePolicy.PhoneSteers("steer", waiting, true) && BackgroundQueuePolicy.PhoneSteers("steer", busy, true) && !BackgroundQueuePolicy.PhoneSteers("queue", null, false), "the phone queues rather than steers in the same case");
        Check(BackgroundQueuePolicy.WaitsOnBackground(waiting, true, 1) && !BackgroundQueuePolicy.WaitsOnBackground(waiting, true, 0) && !BackgroundQueuePolicy.WaitsOnBackground(waiting, false, 1) && !BackgroundQueuePolicy.WaitsOnBackground(busy, true, 1), "the queue says when it waits on background work");
        Check(Korean(() => Locale.Get("queue.waitingOnBackground.windows")) == "백그라운드 작업이 끝나야 시작합니다 · Ctrl+Enter로 끼워 넣기 / 중지", "the notice says Ctrl+Enter");
        Check(BackgroundQueuePolicy.StopKeepsQueue(waiting) && !BackgroundQueuePolicy.StopKeepsQueue(busy) && !BackgroundQueuePolicy.StopKeepsQueue(null), "stopping a turn that is over keeps the queue");

        var queue = new QueuedInputBuffer();
        var item = queue.Add("결제", [], BackgroundQueuePolicy.QueuedOverride(true));
        Check(item.PermissionModeOverride == "plan" && queue.Add("x", []).PermissionModeOverride is null, "a queued item keeps its launch decision");
        Check(queue.Settle("stopped", keepOnStop: true) && queue.Items.Count == 2, "a kept queue survives the stop and runs next");
        Check(!queue.Settle("stopped") && queue.Items.Count == 0, "an ordinary stop still cancels the queue");

        var stored = Pane() with { Settings = new RunSettings(PermissionMode: "plan") };
        var approved = new RunEvent("plan-pane", "plan", Plan: Record(PlanOutcome.ApprovedAuto, launchOverride: "plan"));
        Check(Apply(stored, approved).Settings.PermissionMode == "plan", "a style-launched plan leaves even a plan-stored pane alone");
        Check(Apply(stored, approved with { Plan = Record(PlanOutcome.ApprovedAuto) }).Settings.PermissionMode == "acceptEdits", "without the override the stage-1 behaviour stays");
        Check(ClaudePlanMode.NormalizedHistory([Record(PlanOutcome.ApprovedAuto, launchOverride: "plan"), Record(PlanOutcome.ApprovedAuto, id: "b", launchOverride: "fullAccess")])!.Select(r => r.LaunchOverride).SequenceEqual(["plan", null]), "saved records keep only the plan override");
        return Task.CompletedTask;
    }

    private static RunSession Apply(RunSession session, RunEvent ev) => new AppSnapshot { Sessions = [session] }.Apply(ev).Sessions.Single();

    private static string Normalised(string json)
    {
        static object? Sorted(JsonElement e) => e.ValueKind switch
        {
            JsonValueKind.Object => e.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).ToDictionary(p => p.Name, p => Sorted(p.Value)),
            JsonValueKind.Array => e.EnumerateArray().Select(Sorted).ToArray(),
            JsonValueKind.String => e.GetString(),
            JsonValueKind.Number => e.GetInt64(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
        using var document = JsonDocument.Parse(json);
        return JsonSerializer.Serialize(Sorted(document.RootElement), new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    private static byte[] Resource(string suffix)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var name = assembly.GetManifestResourceNames().Single(n => n.Replace('\\', '/').EndsWith(suffix, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!; using var output = new MemoryStream(); stream.CopyTo(output); return output.ToArray();
    }
}
