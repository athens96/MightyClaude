using System.Text.Json;
using System.Text.Json.Nodes;
using MightyClaude.Core;

/// <summary>The same fixture macOS ClaudePlanModeTests reads: both clients recognise, answer and track Claude's plan mode alike.</summary>
internal static class ClaudePlanModeVerification
{
    private static readonly DateTimeOffset Fixed = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
    private const string FixedStamp = "2027-01-15T08:00:00.000Z";
    private static void Check(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
    private static JsonElement Fixture()
    {
        using var stream = typeof(ClaudePlanModeVerification).Assembly.GetManifestResourceStream("MightyClaude.Core.Tests.ClaudePlanMode.json")!;
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.Clone();
    }
    private static PlanDecision Decision(JsonElement row) => row.GetProperty("decision").GetString() switch
    {
        "approveAutoEdit" => PlanDecision.ApproveAutoEdit,
        "approveConfirmEach" => PlanDecision.ApproveConfirmEach,
        "revise" => PlanDecision.Revise(row.GetProperty("feedback").GetString()!),
        _ => PlanDecision.Cancel,
    };
    private static string? Optional(JsonElement row, string key) => row.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private sealed class Channel
    {
        internal readonly List<string> Writes = [], Warnings = [];
        internal readonly List<ToolPermissionRequest> Displays = [];
        internal readonly List<PlanRecord> Records = [];
        internal readonly ClaudePermissionChannel Value;
        internal Channel() => Value = new ClaudePermissionChannel("run", "", Writes.Add, Displays.Add, (_, _) => { }, Warnings.Add, _ => { }, Records.Add, () => Fixed);
        internal JsonElement LastResponse()
        {
            using var document = JsonDocument.Parse(Writes[^1]);
            var outer = document.RootElement.GetProperty("response");
            Check(document.RootElement.GetProperty("type").GetString() == "control_response" && outer.GetProperty("subtype").GetString() == "success", "plan answers are successful control responses");
            return outer.GetProperty("response").Clone();
        }
    }
    private static string Plan(string id, string? plan = null) => JsonSerializer.Serialize(new { type = "control_request", request_id = id, request = new { subtype = "can_use_tool", tool_name = "ExitPlanMode", tool_use_id = "toolu_" + id, input = new { plan = plan ?? "Plan " + id }, requires_user_interaction = true } });

    internal static Task SharedRequests()
    {
        var rows = Fixture().GetProperty("planRequests").EnumerateArray().ToArray();
        Check(rows.Length >= 3, "shared fixture was not loaded");
        foreach (var row in rows)
        {
            var channel = new Channel();
            channel.Value.Receive(row.GetProperty("request").GetRawText());
            var expected = row.GetProperty("expected"); var name = row.GetProperty("name").GetString();
            var display = channel.Displays.Single();
            Check(display.CanAnswerPlan == expected.GetProperty("canAnswerPlan").GetBoolean() && display.CanAllow == expected.GetProperty("canAllow").GetBoolean(), "flags differ from Mac: " + name);
            Check(display.Plan == Optional(expected, "plan"), "plan text differs from Mac: " + name);
            Check(display.ReceivedAt == FixedStamp, "received time is the channel's clock");
            var approval = PlanApprovalRequest.From("pane-1", display);
            Check(approval is null != display.CanAnswerPlan, "only an answerable plan is a plan approval");
            if (approval is not null) Check(approval == new PlanApprovalRequest("pane-1", "run", display.Id, display.ToolUseId, display.Plan!, FixedStamp), "plan approval holds the pane, request, plan and time");
        }
        return Task.CompletedTask;
    }

    internal static Task SharedResponses()
    {
        var rows = Fixture().GetProperty("planResponses").EnumerateArray().ToArray();
        Check(rows.Length >= 10, "shared fixture was not loaded");
        foreach (var row in rows)
        {
            var name = row.GetProperty("name").GetString();
            if (row.TryGetProperty("error", out var error) && error.GetBoolean())
            {
                try { ClaudePlanMode.Response(Decision(row), row.GetProperty("toolUseId").GetString()!, Optional(row, "paneMode") ?? "plan"); }
                catch (ArgumentException) { continue; }
                throw new InvalidOperationException("expected a refusal: " + name);
            }
            var value = ClaudePlanMode.Response(Decision(row), row.GetProperty("toolUseId").GetString()!, Optional(row, "paneMode") ?? "plan");
            Check(JsonNode.DeepEquals(JsonNode.Parse(JsonSerializer.Serialize(value, Wire.Json)), JsonNode.Parse(row.GetProperty("expected").GetRawText())), "response differs from Mac: " + name);
        }
        return Task.CompletedTask;
    }

    internal static Task ChannelAnswersOnceAndKeepsEveryPlan()
    {
        var channel = new Channel();
        channel.Value.Receive(Plan("p1", "# Plan\n- one"));
        Check(channel.Displays.Single() is { CanAnswerPlan: true, CanAllow: false, Reason: null }, "a plan is answerable as a plan (no separate-screen notice), never by the generic allow");
        Reject(() => channel.Value.Respond("p1", true));
        Reject(() => channel.Value.AnswerPlan("p1", PlanDecision.Revise("  ")));
        Check(channel.Writes.Count == 0 && channel.Records.Count == 0, "an empty revise leaves the plan answerable and writes nothing");
        var record = channel.Value.AnswerPlan("p1", PlanDecision.ApproveAutoEdit);
        var body = channel.LastResponse();
        Check(body.GetProperty("behavior").GetString() == "allow" && body.GetProperty("updatedInput").EnumerateObject().Any() == false, "approval echoes no injected plan");
        Check(body.GetProperty("updatedPermissions")[0].GetProperty("mode").GetString() == "acceptEdits" && body.GetProperty("updatedPermissions")[0].GetProperty("destination").GetString() == "session", "approval switches the session mode only");
        Check(record is { Outcome: PlanOutcome.ApprovedAuto, Plan: "# Plan\n- one", Id: "p1", RunId: "run" } && channel.Records.Single() == record && channel.Displays[^1].State == "allowed", "approval is recorded once");
        Reject(() => channel.Value.AnswerPlan("p1", PlanDecision.Cancel));

        channel.Value.Receive(Plan("r1"));
        channel.Value.AnswerPlan("r1", PlanDecision.Revise(" Split step 2 "));
        Check(channel.LastResponse().GetProperty("message").GetString()!.EndsWith("The user said:\nSplit step 2", StringComparison.Ordinal) && channel.Records[^1] is { Outcome: PlanOutcome.Revised, Feedback: "Split step 2" }, "revise sends and keeps the feedback");
        channel.Value.Receive(Plan("r2"));
        channel.Value.AnswerPlan("r2", PlanDecision.Cancel);
        Check(channel.LastResponse().GetProperty("interrupt").GetBoolean() && channel.Records[^1].Outcome == PlanOutcome.Cancelled && channel.Displays[^1].State == "denied", "cancel interrupts");
        channel.Value.Receive(Plan("r3"));
        channel.Value.Receive("""{"type":"control_cancel_request","request_id":"r3"}""");
        channel.Value.Receive(Plan("r4"));
        channel.Value.CancelAll();
        Check(channel.Records.Select(r => r.Id).SequenceEqual(["p1", "r1", "r2", "r3", "r4"]) && channel.Records.TakeLast(2).All(r => r.Outcome == PlanOutcome.Cancelled), "withdrawn and unanswered plans are kept as cancelled");

        var bash = new Channel();
        bash.Value.Receive("""{"type":"control_request","request_id":"b1","request":{"subtype":"can_use_tool","tool_name":"Bash","tool_use_id":"toolu_b1","input":{"command":"ls"}}}""");
        Reject(() => bash.Value.AnswerPlan("b1", PlanDecision.ApproveAutoEdit));
        // A generic deny (an older card, the phone) still keeps the plan, as cancelled.
        bash.Value.Receive(Plan("p2", "# Two"));
        bash.Value.Respond("p2", false);
        bash.Value.Respond("b1", false);
        Check(bash.Records.Single() is { Id: "p2", Outcome: PlanOutcome.Cancelled, Plan: "# Two" }, "a generic deny of a plan is kept as cancelled");

        var big = new Channel();
        big.Value.Receive(Plan("big", new string('x', 70_000)));
        Check(big.LastResponse().GetProperty("behavior").GetString() == "deny" && big.LastResponse().GetProperty("message").GetString() == ClaudePlanMode.TooLongMessage, "a plan too long to show is sent back");
        Check(big.Displays.Count == 0 && big.Warnings.SequenceEqual([Locale.Get("plan.warning.tooLong")]), "the user is told why");
        Check(big.Records.Single() is { Outcome: PlanOutcome.Cancelled, PlanTruncated: true }, "the too-long plan is still kept, bounded, as cancelled");
        return Task.CompletedTask;
    }

    internal static Task SharedChecklists()
    {
        var rows = Fixture().GetProperty("todos").EnumerateArray().ToArray();
        Check(rows.Length >= 4, "shared fixture was not loaded");
        foreach (var row in rows)
        {
            var saved = row.TryGetProperty("saved", out var rows2) ? new TodoProgress(rows2.EnumerateArray().Select(i => new TodoItem(i.GetProperty("content").GetString()!, i.GetProperty("status").GetString()!, Optional(i, "activeForm"), Optional(i, "id"))).ToList()) : null;
            var tracker = new TodoProgressTracker(saved); var name = row.GetProperty("name").GetString();
            foreach (var frame in row.GetProperty("frames").EnumerateArray()) tracker.Consume(frame);
            var expected = row.GetProperty("expected"); var progress = tracker.Progress ?? throw new InvalidOperationException("no progress: " + name);
            Check(progress.Total == expected.GetProperty("total").GetInt32() && progress.Completed == expected.GetProperty("completed").GetInt32() && progress.CurrentText == Optional(expected, "current"), "counts differ from Mac: " + name);
            var items = expected.GetProperty("items").EnumerateArray().Select(i => new TodoItem(i.GetProperty("content").GetString()!, i.GetProperty("status").GetString()!, Optional(i, "activeForm"), Optional(i, "id")));
            Check(progress.Items.SequenceEqual(items), "items differ from Mac: " + name);
        }
        return Task.CompletedTask;
    }

    internal static Task SharedBackgroundRuns()
    {
        var rows = Fixture().GetProperty("background").EnumerateArray().ToArray();
        Check(rows.Length >= 3, "shared fixture was not loaded");
        foreach (var row in rows)
        {
            var tracker = new BackgroundTaskTracker(() => Fixed); var name = row.GetProperty("name").GetString();
            foreach (var step in row.GetProperty("steps").EnumerateArray())
            {
                switch (Optional(step, "step"))
                {
                    case "turnEnded": tracker.TurnEnded(); break;
                    case "finish": tracker.Finish(); break;
                    default: tracker.Consume(step); break;
                }
            }
            var expected = row.GetProperty("expected"); var work = tracker.Work;
            Check(work.TurnEnded == expected.GetProperty("turnEnded").GetBoolean(), "turn end differs from Mac: " + name);
            var tasks = expected.GetProperty("tasks").EnumerateArray().ToArray();
            Check(work.Tasks.Count == tasks.Length, "task count differs from Mac: " + name);
            foreach (var (task, want) in work.Tasks.Zip(tasks))
            {
                Check(task.Id == Optional(want, "id") && task.ToolUseId == Optional(want, "toolUseId") && task.Kind == Optional(want, "kind") && task.Description == Optional(want, "description"), "task identity differs from Mac: " + name + " " + task.Id);
                Check(task.Status == Optional(want, "status") && task.Summary == Optional(want, "summary") && task.EndedAt is not null == want.GetProperty("ended").GetBoolean() && task.StartedAt == FixedStamp, "task state differs from Mac: " + name + " " + task.Id);
            }
        }
        return Task.CompletedTask;
    }

    internal static Task ApprovalNeverLowersThePaneMode()
    {
        string? Mode(PlanDecision d, string pane) => PlanOutcome.ApprovedCliMode(d, pane);
        Check(Mode(PlanDecision.ApproveConfirmEach, "plan") == "default" && Mode(PlanDecision.ApproveAutoEdit, "plan") == "acceptEdits", "a plan pane takes the button");
        Check(Mode(PlanDecision.ApproveConfirmEach, "manual") == "default" && Mode(PlanDecision.ApproveAutoEdit, "manual") == "acceptEdits", "a manual pane takes the higher");
        Check(Mode(PlanDecision.ApproveConfirmEach, "acceptEdits") == "acceptEdits" && Mode(PlanDecision.ApproveAutoEdit, "auto") == "auto" && Mode(PlanDecision.ApproveConfirmEach, "fullAccess") == "bypassPermissions", "approval never lowers the pane's mode");
        Check(Mode(PlanDecision.Revise("x"), "fullAccess") is null && Mode(PlanDecision.Cancel, "plan") is null, "only approvals switch modes");
        var writes = new List<string>();
        var channel = new ClaudePermissionChannel("run", "", writes.Add, _ => { }, (_, _) => { }, _ => { }, _ => { }, paneMode: "auto");
        channel.Receive(Plan("a1"));
        channel.AnswerPlan("a1", PlanDecision.ApproveConfirmEach);
        using var document = JsonDocument.Parse(writes[^1]);
        Check(document.RootElement.GetProperty("response").GetProperty("response").GetProperty("updatedPermissions")[0].GetProperty("mode").GetString() == "auto", "the channel uses the pane's stored mode");
        return Task.CompletedTask;
    }

    internal static Task PerRunPlanOverride()
    {
        var request = new StartRunRequest("pane", "ws", "claude", "plan it", [], Settings: new(PermissionMode: "manual")) { PermissionModeOverride = "plan" };
        var args = ProviderCatalog.Arguments(request, "/tmp/plugin");
        Check(args[args.IndexOf("--permission-mode") + 1] == "plan" && request.Settings!.PermissionMode == "manual", "the override launches this request in plan");
        Check(!JsonSerializer.Serialize(request, Wire.Json).Contains("verride", StringComparison.Ordinal), "the override is never serialized");
        Reject(() => (request with { PermissionModeOverride = "auto" }).Validate());
        Reject(() => new StartRunRequest("pane", "ws", "claude", "x", [], Provider: "codex") { PermissionModeOverride = "plan" }.Validate());
        return Task.CompletedTask;
    }

    internal static Task HistoryKeepsNewPlansWholeWithinItsBudget()
    {
        var lines = string.Join('\n', Enumerable.Range(1, 400).Select(i => $"Step {i}: " + new string('x', 60)));
        List<PlanRecord>? history = null;
        for (var i = 0; i < 10; i++) history = ClaudePlanMode.Appending(new PlanRecord("h" + i, "run", lines, "2026-10-06T01:00:00.000Z", "2026-10-06T01:01:00.000Z", PlanOutcome.Revised, new string('f', 3_000)), history);
        var bytes = history!.Sum(r => System.Text.Encoding.UTF8.GetByteCount(r.Plan) + System.Text.Encoding.UTF8.GetByteCount(r.Feedback ?? ""));
        Check(history!.Count == 10 && bytes <= ClaudePlanMode.MaximumHistoryBytes + 10 * 1_536, "history stays within its budget");
        Check(history.TakeLast(2).All(r => r.Plan.Length > 1_024), "the newest plans stay whole");
        Check(history[0] is { PlanTruncated: true } oldest && oldest.Plan.Split('\n').Length == 8 && oldest.Plan.StartsWith("Step 1:", StringComparison.Ordinal) && oldest.Feedback!.Length <= 512, "older plans keep their first lines");
        Check(ClaudePlanMode.NormalizedHistory(history)!.SequenceEqual(history), "normalizing keeps a budgeted history as is");
        return Task.CompletedTask;
    }

    // RunSession.Apply is internal; the public snapshot path is how the app applies events.
    private static RunSession Apply(this RunSession session, RunEvent ev) => new AppSnapshot { Sessions = [session] }.Apply(ev).Sessions.Single();

    internal static Task PaneKeepsThePlanModeAndHistory()
    {
        var session = new RunSession { Id = "pane", WorkspaceId = "ws", Settings = new(PermissionMode: "plan") };
        RunEvent Record(string id, string outcome, string plan = "# Plan") => new("pane", "plan", Plan: new PlanRecord(id, "run", plan, "2026-10-06T01:00:00.000Z", "2026-10-06T01:01:00.000Z", outcome, outcome == PlanOutcome.Revised ? "more" : null));
        session = session.Apply(Record("a", PlanOutcome.Revised));
        Check(session.Settings.PermissionMode == "plan" && session.PlanHistory!.Count == 1, "revise keeps plan mode");
        session = session.Apply(Record("b", PlanOutcome.ApprovedAuto));
        Check(session.Settings.PermissionMode == "acceptEdits", "auto-edit approval saves acceptEdits");
        session = session.Apply(Record("c", PlanOutcome.ApprovedConfirm, new string('가', 20_000)));
        Check(session.Settings.PermissionMode == "acceptEdits" && session.PlanHistory![^1].PlanTruncated == true && System.Text.Encoding.UTF8.GetByteCount(session.PlanHistory[^1].Plan) <= ClaudePlanMode.MaximumStoredPlanBytes, "a pane not stored in plan keeps its mode; the stored plan is bounded");
        session = session with { Settings = session.Settings with { PermissionMode = "plan" } };
        session = session.Apply(Record("c2", PlanOutcome.ApprovedConfirm));
        Check(session.Settings.PermissionMode == "manual", "confirm-each from a plan pane saves manual");
        for (var i = 0; i < 12; i++) session = session.Apply(Record("n" + i, PlanOutcome.Cancelled));
        Check(session.PlanHistory!.Count == ClaudePlanMode.MaximumHistory && session.PlanHistory[^1].Id == "n11" && session.Settings.PermissionMode == "manual", "history is capped, newest last");
        var codex = new RunSession { Id = "codex", WorkspaceId = "ws", Provider = "codex" }.Apply(Record("x", PlanOutcome.ApprovedAuto));
        Check(codex.PlanHistory is null && codex.Settings.PermissionMode == "manual", "only Claude panes keep plans");
        session = session.Apply(new("pane", "background", Background: new([new BackgroundTask("t", "agent", "d", "2026-10-06T01:00:00.000Z")])));
        Check(session.BackgroundWork!.Running.Count == 1, "background work is kept");
        session = session.Apply(RunEvent.State("pane", "running"));
        Check(session.BackgroundWork is null, "a new run starts without the previous run's background work");
        session = session.Apply(new("pane", "todos", Todos: new([new TodoItem("a", "weird")])));
        Check(session.TodoProgress!.Items[0].Status == "pending", "unknown checklist states load as pending");
        Check(new RunEvent("pane", "plan", Plan: new PlanRecord("p", "r", "x", "t", "t", PlanOutcome.Revised)).Valid() && !new RunEvent("pane", "plan").Valid() && new RunEvent("pane", "todos", Todos: new([])).Valid(), "plan-mode events are valid wire events");
        return Task.CompletedTask;
    }

    internal static Task SavedStateRoundTripsAndOldSnapshotsLoad()
    {
        const string old = """{"id":"pane","workspaceId":"ws","title":"Claude","kind":"claude","provider":"claude","model":"default","settings":{"effort":"default","permissionMode":"plan"},"status":"completed","logs":[],"createdAt":"2026-10-01T00:00:00Z"}""";
        var decoded = JsonSerializer.Deserialize<RunSession>(old, Wire.Json)!;
        Check(decoded.PlanHistory is null && decoded.TodoProgress is null && decoded.BackgroundWork is null, "old snapshots load without plan state");
        var session = decoded with
        {
            PlanHistory = [new PlanRecord("p", "r", "# P", "2026-10-06T01:00:00.000Z", "2026-10-06T01:01:00.000Z", PlanOutcome.ApprovedConfirm)],
            TodoProgress = new([new TodoItem("a", "completed", Id: "1")]),
            BackgroundWork = new([new BackgroundTask("t", "shell", "d", "2026-10-06T01:00:00.000Z")], true),
        };
        var json = JsonSerializer.Serialize(session, Wire.Json);
        Check(json.Contains("\"planHistory\"", StringComparison.Ordinal) && json.Contains("\"todoProgress\":{\"items\"", StringComparison.Ordinal) && json.Contains("\"backgroundWork\"", StringComparison.Ordinal) && !json.Contains("\"total\"", StringComparison.Ordinal), "the Mac field names are written");
        var again = JsonSerializer.Deserialize<RunSession>(json, Wire.Json)!;
        Check(again.PlanHistory!.SequenceEqual(session.PlanHistory) && again.TodoProgress!.SameAs(session.TodoProgress) && again.BackgroundWork!.SameAs(session.BackgroundWork), "plan state round-trips");
        var damaged = JsonSerializer.Deserialize<RunSession>(old.Replace("\"logs\":[]", "\"logs\":[],\"planHistory\":7,\"backgroundWork\":\"x\",\"todoProgress\":[1]", StringComparison.Ordinal), Wire.Json)!;
        Check(damaged.Id == "pane" && damaged.PlanHistory is null && damaged.BackgroundWork is null && damaged.TodoProgress is null, "damage in plan state keeps the conversation");
        var partly = JsonSerializer.Deserialize<RunSession>(old.Replace("\"logs\":[]", "\"logs\":[],\"planHistory\":[{\"id\":1},{\"id\":\"ok\",\"runId\":\"r\",\"plan\":\"# P\",\"receivedAt\":\"2026-10-06T01:00:00.000Z\",\"decidedAt\":\"2026-10-06T01:01:00.000Z\",\"outcome\":\"revised\"},{\"id\":\"bad\",\"outcome\":\"unknown\"}]", StringComparison.Ordinal), Wire.Json)!;
        Check(ClaudePlanMode.NormalizedHistory(partly.PlanHistory)!.Select(r => r.Id).SequenceEqual(["ok"]), "only the damaged plan records are dropped");
        var workspace = new Workspace { Id = "ws", Name = "Repo", Path = Path.GetTempPath() };
        var running = session with { Status = "running", BackgroundWork = new([new BackgroundTask("t", "agent", "d", "2026-10-06T01:00:00.000Z")]) };
        var restored = StateStore.Normalize(new AppSnapshot { Workspaces = [workspace], Sessions = [running] }, true).Sessions.Single();
        Check(restored.BackgroundWork!.Tasks[0].Status == "unknown" && restored.PlanHistory!.Count == 1 && restored.TodoProgress!.Completed == 1, "restoring ends what was still running as unknown");
        var saved = StateStore.Normalize(new AppSnapshot { Workspaces = [workspace], Sessions = [running] }, false).Sessions.Single();
        Check(saved.BackgroundWork!.Tasks[0].Status == "running", "saving a running pane keeps its live tasks");
        var shell = StateStore.Normalize(new AppSnapshot { Workspaces = [workspace], Sessions = [session with { Id = "sh", Kind = "shell" }] }, true).Sessions.Single();
        Check(shell.PlanHistory is null && shell.TodoProgress is null && shell.BackgroundWork is null, "a shell pane keeps no plan state");
        return Task.CompletedTask;
    }

    internal static async Task RunnerAnswersAPlanAndReportsProgress()
    {
        var directory = Verification.Temp();
        try
        {
            var workspace = new Workspace { Path = directory };
            var plugin = Path.Combine(directory, "plugin"); Directory.CreateDirectory(Path.Combine(plugin, ".claude-plugin"));
            await File.WriteAllTextAsync(Path.Combine(plugin, ".claude-plugin", "plugin.json"), "{}");
            foreach (var (decision, final, quiet) in new[] { (PlanDecision.ApproveAutoEdit, "completed", false), (PlanDecision.Cancel, "stopped", false), (PlanDecision.ApproveConfirmEach, "completed", true) })
            {
                var record = Path.Combine(directory, "plan-" + decision.Kind);
                if (quiet) await File.WriteAllTextAsync(record + ".quiet", "");
                await using var catalog = new ProviderCatalog((_, _) => Task.FromResult<CliCommand?>(Verification.Self("--fake-cli", "claude", record)));
                var events = new List<RunEvent>(); var seen = new List<ToolPermissionRequest>();
                RunManager? manager = null;
                manager = new RunManager(_ => Task.FromResult(workspace), catalog, plugin, ev => { lock (events) events.Add(ev); }, value =>
                {
                    lock (events) seen.Add(value);
                    if (PlanApprovalRequest.From("plan-pane", value) is { } approval) manager!.AnswerPlan("plan-pane", approval.RequestId, decision);
                }) { BackgroundIdleClose = TimeSpan.FromMilliseconds(500) };
                RunEvent[] Snapshot() { lock (events) return [.. events]; }
                await using (manager)
                {
                    // The pane is stored in manual; this request alone starts in plan (a guided style's override).
                    await manager.StartAsync(new("plan-pane", workspace.Id, "claude", "plan fixture", [], Settings: new(PermissionMode: "manual")) { PermissionModeOverride = "plan" });
                    if (decision == PlanDecision.ApproveAutoEdit)
                    {
                        // The turn is over but its background agent runs: input stays open and new text joins.
                        await Verification.Until(() => Snapshot().Any(e => e.Background?.WaitingOnBackground == true), 20000);
                        Check(manager.IsRunning("plan-pane"), "the run waits for its background agent");
                        Check(await manager.TrySteerAsync("plan-pane", "and one more thing"), "new input joins the open process");
                    }
                    await Verification.Until(() => !manager.IsRunning("plan-pane"), 20000);
                    Check(!await manager.TrySteerAsync("plan-pane", "too late"), "a finished run takes no input");
                }
                var all = Snapshot();
                var launched = JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(record + ".args"), Wire.Json)!;
                Check(launched[Array.IndexOf(launched, "--permission-mode") + 1] == "plan", "the override launches in plan");
                Check(await File.ReadAllTextAsync(record + ".env") == "1", "the CLI is asked for its session state events");
                var answer = JsonDocument.Parse(await File.ReadAllTextAsync(record + ".decision")).RootElement.GetProperty("response").GetProperty("response");
                Check(seen[0] is { CanAnswerPlan: true, CanAllow: false, Plan: "# Plan\n1. Do it" }, "the plan reached the host as a plan");
                Check(all.Where(e => e.Type == "plan").Select(e => e.Plan!.Outcome).SequenceEqual([decision.Outcome]), "the answer is recorded once");
                var graphRun = all.LastOrDefault(e => e.Type == "graph_run")?.GraphRun;
                Check(graphRun is not null && all.Single(e => e.Type == "plan").Plan!.GraphRunId == graphRun.SourceRunID, "the record names its request's graph run");
                Check(all.Last(e => e.Type == "status").Status == final, "the run ends " + final);
                if (decision == PlanDecision.Cancel)
                {
                    Check(answer.GetProperty("interrupt").GetBoolean() && !all.Any(e => e.Entry?.Kind == "error"), "cancel interrupts the turn without an error line");
                    continue;
                }
                if (quiet) continue;
                Check(answer.GetProperty("updatedPermissions")[0].GetProperty("mode").GetString() == "acceptEdits" && !answer.GetProperty("updatedInput").EnumerateObject().Any(), "approval switches the session to acceptEdits");
                Check((await File.ReadAllTextAsync(record + ".follow")).Contains("and one more thing", StringComparison.Ordinal) && !File.Exists(record + ".extra"), "the steered text reached the CLI before it closed");
                Check(all.Last(e => e.Type == "todos").Todos!.CurrentText == "Doing it", "the checklist is reported");
                var backgrounds = all.Where(e => e.Type == "background").Select(e => e.Background!).ToArray();
                Check(backgrounds[^1].Tasks[0] is { Status: "completed", Summary: "All good" } && !backgrounds[^1].WaitingOnBackground, "its notification settles it");
                var snapshot = new AppSnapshot { Workspaces = [workspace], Sessions = [new RunSession { Id = "plan-pane", WorkspaceId = workspace.Id, Settings = new(PermissionMode: "manual") }] };
                foreach (var ev in all) snapshot = snapshot.Apply(ev);
                Check(snapshot.Sessions[0] is { Settings.PermissionMode: "manual", PlanHistory.Count: 1, TodoProgress.Total: 1 }, "a pane not stored in plan keeps its mode");
            }
        }
        finally { try { Directory.Delete(directory, true); } catch (IOException) { } }
    }

    internal static async Task StoppingAPaneWaitingOnBackgroundWorkEndsEverything()
    {
        var directory = Verification.Temp();
        try
        {
            var workspace = new Workspace { Path = directory };
            var plugin = Path.Combine(directory, "plugin"); Directory.CreateDirectory(Path.Combine(plugin, ".claude-plugin"));
            await File.WriteAllTextAsync(Path.Combine(plugin, ".claude-plugin", "plugin.json"), "{}");
            var record = Path.Combine(directory, "stop");
            await using var catalog = new ProviderCatalog((_, _) => Task.FromResult<CliCommand?>(Verification.Self("--fake-cli", "claude", record)));
            var events = new List<RunEvent>();
            RunEvent[] Snapshot() { lock (events) return [.. events]; }
            var manager = new RunManager(_ => Task.FromResult(workspace), catalog, plugin, ev => { lock (events) events.Add(ev); }, _ => { });
            await using (manager)
            {
                await manager.StartAsync(new("stop-pane", workspace.Id, "claude", "background stop fixture", []));
                await Verification.Until(() => Snapshot().Any(e => e.Background?.WaitingOnBackground == true), 20000);
                Check(manager.IsRunning("stop-pane"), "the run waits for its background shell");
                await manager.StopAsync("stop-pane");
                await Verification.Until(() => !manager.IsRunning("stop-pane"), 20000);
            }
            var all = Snapshot();
            Check(all.Last(e => e.Type == "status").Status == "stopped", "the stopped pane ends stopped");
            Check(all.Last(e => e.Type == "background").Background!.Tasks[0].Status == "unknown", "the task that never reported an end is unknown");
            var pid = int.Parse(await File.ReadAllTextAsync(record + ".pid"), System.Globalization.CultureInfo.InvariantCulture);
            static bool Gone(int id)
            {
                try { using var process = System.Diagnostics.Process.GetProcessById(id); return process.HasExited; }
                catch (ArgumentException) { return true; }
                catch (InvalidOperationException) { return true; }
            }
            await Verification.Until(() => Gone(pid), 10000);
            Check(Gone(pid), "the CLI process is gone");
        }
        finally { try { Directory.Delete(directory, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    private static void Reject(Action action)
    {
        try { action(); } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return; }
        throw new InvalidOperationException("Expected the answer to be refused");
    }
}
