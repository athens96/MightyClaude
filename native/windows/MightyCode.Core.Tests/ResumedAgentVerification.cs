using System.Text.Json;
using MightyCode.Core;

/// <summary>
/// A background agent Claude resumes with SendMessage is background work of the pane like any other: the pane waits on it, the queue holds,
/// the strip lists it, and the run's input stays open until it ends (macOS ResumedAgentTests, ResumedAgentRunnerTests). Claude Code 2.1.322
/// lists and starts the resumed run under the agent's first task id with a run id of its own, and ends it as it ends any task. The recording is
/// the shared native/contracts/fixtures/claude-stream-resumed-agent.jsonl: the request launches one background agent, which ends before the
/// request's own result (frames 0–17); the notice turn its end opens resumes it (18–27), and its second end opens the next notice turn (28–35);
/// a second user message on stdin resumes it once more from an ordinary turn (36–52).
/// </summary>
internal static class ResumedAgentVerification
{
    private static void Check(bool value, string why) { if (!value) throw new InvalidOperationException(why); }

    /// <summary>The recording read as RunManager reads a Claude run's frames: the graph tracker, the background tasks and the request's own
    /// result, and at each settle point (a top-level result or idle) whether the runner closes the input (ClaudeStream.ClosesInput).</summary>
    private sealed class Replay
    {
        internal readonly FlowTrackerVerification.Pane Graph = new("recorded");
        internal readonly BackgroundTaskTracker Background = new(() => DateTimeOffset.FromUnixTimeSeconds(1_800_000_000));
        internal readonly List<bool> Closes = [];
        private readonly List<string> lines = FlowGraphVerification.Lines("claude-stream-resumed-agent");
        private int next;
        private bool turnResultSeen;
        private string? sessionState;
        internal void Feed(int through)
        {
            while (next <= through)
            {
                var line = lines[next++];
                var frame = JsonDocument.Parse(line).RootElement.Clone();
                Graph.Send(frame);
                if (ClaudeStream.SessionState(frame) is { } state) sessionState = state;
                Background.Consume(frame);
                if (frame.Text("type") == "result" && !ClaudeStream.IsNotificationResult(frame) && MetadataJson.Property(frame, "parent_tool_use_id").ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
                    Background.TurnEnded();
                if (!ClaudeStream.IsSettlePoint(line)) continue;
                if (ClaudeStream.IsTurnResult(line)) turnResultSeen = true;
                Closes.Add(ClaudeStream.ClosesInput(turnResultSeen, Background.Work.Running.Count > 0, sessionState));
            }
        }
        internal BackgroundWork Work => Background.Work;
        internal bool Busy => sessionState is not (null or "idle");
        internal List<MightyGraphRun> Runs => Graph.Runs();
    }

    internal static Task TheRecordingIsTheResumeAsClaudeCodeReportsIt()
    {
        var frames = FlowGraphVerification.Lines("claude-stream-resumed-agent").Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToList();
        Check(frames.Count == 53, "the recording has its 53 frames");
        var listed = MetadataJson.Property(frames[23], "tasks")[0];
        Check(frames[23].Text("subtype") == "background_tasks_changed" && listed.Text("task_id") == "agent1" && listed.Text("run_id") == "taskrun-2", "the resumed run is listed under the first task id and a run of its own");
        Check(frames[24] is var start && start.Text("subtype") == "task_started" && start.Text("task_id") == "agent1" && start.Text("run_id") == "taskrun-2" && start.Text("tool_use_id") == "toolu_3"
            && start.Text("description") == "Count one to three", "the resumed run starts naming the SendMessage call, with the first description");
        Check(frames[30] is var end && end.Text("subtype") == "task_notification" && end.Text("run_id") == "taskrun-2" && end.Text("tool_use_id") == "toolu_3", "its own notification ends it");
        Check(MetadataJson.Property(frames[34], "origin").Text("runId") == "taskrun-2", "its notice turn names the run");
        var idle = frames.Select((frame, index) => (frame, index)).Where(f => ClaudeStream.SessionState(f.frame) == "idle").Select(f => f.index).ToList();
        Check(idle.SequenceEqual([35, 52]), "idle comes only after the resumed agent's notice turn: " + string.Join(",", idle));
        return Task.CompletedTask;
    }

    /// <summary>The user's case: a notice turn resumes the agent its own notice was about, and Claude ends that turn while the agent works.</summary>
    internal static Task AnAgentResumedFromANoticeTurnKeepsThePaneWaitingUntilItsNotification()
    {
        FlowFixtures.Korean(() =>
        {
            var replay = new Replay();
            // The request's own turn: the agent ended before its result and its notice turn is still to come, so the CLI is busy and the
            // input stays open (closed, Claude Code would stop the resumed agent 10 minutes on).
            replay.Feed(17);
            Check(replay.Work is { TurnEnded: true, WaitingOnBackground: false } work17 && work17.Tasks.Single().Status == "completed", "the request's turn left nothing running");
            Check(replay.Closes.SequenceEqual([false]) && replay.Busy, "the request's result leaves the input open while the CLI is busy");
            // The notice turn resumes it: listed again under a new run, then started by the SendMessage call.
            replay.Feed(24);
            var task = replay.Work.Tasks.Single();
            Check(task is { Id: "agent1", Status: "running", ToolUseId: "toolu_3", EndedAt: null, Summary: null, Description: "Count one to three", Kind: "agent" }, "the resumed run runs again: " + task);
            // The notice turn's result: the agent still runs, so the input stays open and the pane waits on it.
            replay.Feed(27);
            var work = replay.Work;
            Check(replay.Closes.SequenceEqual([false, false]) && work.Running.Count == 1, "the notice turn's result leaves the input open");
            Check(work.WaitingOnBackground && PlanCardSupport.BackgroundStatus(work) == "턴 완료 · 백그라운드 1개 실행 중", "the pane waits on the resumed agent");
            var runs = replay.Runs;
            Check(BackgroundQueuePolicy.WaitsOnBackground(work, 1) && BackgroundQueuePolicy.SteersRunNext(work, true, false, 1, false) && BackgroundQueuePolicy.StopKeepsQueue(work), "the queue holds, 바로 실행 steers and Stop keeps the queue");
            var pane = new QueueRunNow.Pane(true, false, ComposerSecondSend.Queue, false, work.WaitingOnBackground, 0, MightyGraphSupport.NoticeTurnRunning(runs));
            Check(QueueRunNow.Decide(null, "next", 0, pane) == QueueRunNowPath.Steer, "바로 실행 takes the row into the process");
            var request = MightyGraphSupport.NewestRequestRun(runs)!;
            Check(PlanCardSupport.ShowsBackgroundStrip(work, false, false) && PlanCardSupport.BackgroundCounts(work) == "끝남 0개 · 작업 중 1개", "the strip shows it running");
            var rows = PlanCardSupport.BackgroundStripRows(work, request);
            Check(rows.Count == 1 && rows[0] is { Id: "agent1", Run: false, Command: "Reply with exactly: four five six. Use no tools." } && rows[0].Task.Running, "the strip's row is the resumed sub-agent, opened to its message");
            Check(PlanCardSupport.MobileBackgroundWork(work, request)?.Select(w => w.Kind).SequenceEqual(["agent"]) == true, "the phone lists it");
            // Its block is the request's, as every block a notice turn makes is (a notice run holds only copies).
            var resumed = request.Agents.Single(a => a.Title == "Count four to six");
            Check(resumed is { Background: true, Status: "running", SourceBlockId: null } && resumed.Id == ExecutionGraphSupport.AgentNodeID("recorded", "toolu_3"), "the resumed block is the request's");
            Check(runs.Where(r => r.NoticeRun == true).All(r => r.Agents.All(a => a.SourceBlockId is not null)), "notice runs hold only copies");
            Check(BackgroundNoticeRuns.AwaitingNotice(request, runs)?.Tasks.Select(t => t.Status).SequenceEqual(["completed", "running"]) == true, "the request awaits its notice");
            Check(BackgroundNoticeRuns.AwaitsNotice(new RunSession { Id = "p", Kind = "claude", Provider = "claude", Status = "running", GraphRuns = runs }), "the pet and the header read 알림 대기");
            var shown = MightyGraphSupport.ShownRuns(runs);
            Check(shown[0].FlowAgents.Select(a => a.Title).SequenceEqual(["Count one to three", "Count four to six"]) && shown[0].FlowAgents.Select(a => a.Status).SequenceEqual(["completed", "running"]), "the role view draws it as a running card");
            Check(FlowLog.StatusOf(runs) is { } status && status.Lead.EndsWith("알림 대기", StringComparison.Ordinal) && status.Subagents == "하위 에이전트 1/2 실행 중", "the log strip's status line counts it");
            // Its end: the run's own notification ends it and its copy opens the next notice run.
            replay.Feed(30);
            work = replay.Work;
            Check(work.Tasks.Single() is { Status: "completed", EndedAt: not null } && !work.WaitingOnBackground && PlanCardSupport.BackgroundCounts(work) == "끝남 1개 · 작업 중 0개", "its notification ends it");
            var ended = replay.Runs;
            Check(ended[0].Agents.Single(a => a.Id == resumed.Id).Status == "completed" && ended[^1] is { NoticeRun: true } last && last.Agents.Select(a => a.SourceBlockId).SequenceEqual([resumed.Id]), "its copy opens the next notice run");
            // That notice turn's result, then idle: only then does the input close.
            replay.Feed(35);
            Check(replay.Closes.SequenceEqual([false, false, false, true]), "the input closes at idle: " + string.Join(",", replay.Closes));
        });
        return Task.CompletedTask;
    }

    /// <summary>The same agent resumed from an ordinary turn: 바로 실행's steer reopens the turn, so the pane waits on background work only once
    /// that turn's own result came, until the agent's notification.</summary>
    internal static Task AnAgentResumedFromASteeredTurnWaitsOnceThatTurnEnds()
    {
        var replay = new Replay();
        replay.Feed(35);
        // The second message reached the process as a steer does (RunManager.TrySteerAsync: TurnReopened, then the tracker's steer block).
        replay.Background.Reopen();
        replay.Graph.Tracker.Steer("second", "Call the SendMessage tool exactly once to continue the same agent again.");
        replay.Feed(40);
        Check(replay.Work is { TurnEnded: false, WaitingOnBackground: false } open && open.Tasks.Single() is { Status: "running", ToolUseId: "toolu_4" }, "the steered turn is open while the agent runs again");
        replay.Feed(43);
        Check(replay.Work is { TurnEnded: true, WaitingOnBackground: true } && replay.Closes[^1] == false, "after that turn's result the pane waits on the agent");
        replay.Feed(47);
        Check(replay.Work is { WaitingOnBackground: false } done && done.Tasks.Single().Status == "completed", "its notification ends it");
        replay.Feed(52);
        Check(replay.Closes[^1], "idle closes the input");
        return Task.CompletedTask;
    }

    /// <summary>The run's input as RunManager keeps it: open after the request's result while the CLI says it is not idle, so a notice turn that
    /// comes after a result with nothing running can resume an agent and still take new input; closed once it is idle.</summary>
    internal static async Task TheRunnerKeepsTheInputOpenForAResumedAgent()
    {
        var directory = Verification.Temp();
        try
        {
            var workspace = new Workspace { Path = directory };
            var plugin = Path.Combine(directory, "plugin"); Directory.CreateDirectory(Path.Combine(plugin, ".claude-plugin"));
            await File.WriteAllTextAsync(Path.Combine(plugin, ".claude-plugin", "plugin.json"), "{}");
            var record = Path.Combine(directory, "resumed");
            await using var catalog = new ProviderCatalog((_, _) => Task.FromResult<CliCommand?>(Verification.Self("--fake-cli", "claude", record)));
            var events = new List<RunEvent>();
            RunEvent[] Snapshot() { lock (events) return [.. events]; }
            var manager = new RunManager(_ => Task.FromResult(workspace), catalog, plugin, ev => { lock (events) events.Add(ev); }, _ => { });
            await using (manager)
            {
                await manager.StartAsync(new("resumed-pane", workspace.Id, "claude", "resumed agent fixture", []));
                // After the notice turn's result the resumed agent runs, the pane waits on it, and new input still reaches the process.
                await Verification.Until(() => Snapshot().LastOrDefault(e => e.Type == "background")?.Background is { WaitingOnBackground: true } work && work.Tasks[0].ToolUseId == "toolu_2", 20000);
                Check(manager.IsRunning("resumed-pane"), "the run waits for the resumed agent");
                Check(await manager.TrySteerAsync("resumed-pane", "and one more thing"), "new input still joins the open process");
                await Verification.Until(() => !manager.IsRunning("resumed-pane"), 20000);
            }
            var all = Snapshot();
            Check(all.Last(e => e.Type == "status").Status == "completed", "the run ends completed: " + all.Last(e => e.Type == "status").Status);
            Check((await File.ReadAllTextAsync(record + ".follow")).Contains("and one more thing", StringComparison.Ordinal) && !File.Exists(record + ".extra"), "the steered text reached the CLI before its input closed");
            Check(all.Last(e => e.Type == "background").Background is { WaitingOnBackground: false } last && last.Tasks.Single().Status == "completed", "the resumed run's notification ends it");
        }
        finally { try { Directory.Delete(directory, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}
