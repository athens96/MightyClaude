using System.Text.Json;
using MightyClaude.Core;

/// <summary>
/// Session history and resume on Windows: scrolling the Mighty diagram past its
/// top reads earlier requests from the CLI's own session record, and "창 추가" →
/// "이어가기…" lists a folder's earlier Claude and Codex sessions with nested
/// Ouroboros runs hidden. Mirrors native/macos/Tests/MightyCoreTests/
/// SessionHistoryTests.swift and ResumableSessionsTests.swift.
/// </summary>
internal static class SessionHistoryVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private static readonly DateTimeOffset Base = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    private static string Stamp(DateTimeOffset date) => date.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
    private static string Json(object value) => JsonSerializer.Serialize(value);

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "mighty-history-" + Guid.NewGuid().ToString("N"));
        public string Home => Path.Combine(Root, "home");
        public string Workspace => Path.Combine(Root, "work", "project");
        public Fixture() { Directory.CreateDirectory(Home); Directory.CreateDirectory(Workspace); }
        public string ClaudeRecord(string id, IEnumerable<string> lines, string? workspace = null, DateTimeOffset? modified = null)
        {
            var folder = Path.Combine(Home, ".claude", "projects", SessionHistory.ClaudeProjectFolder(workspace ?? Workspace));
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, id + ".jsonl");
            File.WriteAllText(path, string.Join("\n", lines) + "\n");
            if (modified is { } at) File.SetLastWriteTimeUtc(path, at.UtcDateTime);
            return path;
        }
        public string CodexRecord(string thread, IEnumerable<string> lines, DateTimeOffset? modified = null)
        {
            var folder = Path.Combine(Home, ".codex", "sessions", "2026", "10", "01");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "rollout-2026-10-01T10-00-00-" + thread + ".jsonl");
            File.WriteAllText(path, string.Join("\n", lines) + "\n");
            if (modified is { } at) File.SetLastWriteTimeUtc(path, at.UtcDateTime);
            return path;
        }
        public void Dispose() { try { Directory.Delete(Root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    private static string ClaudeUser(string text, DateTimeOffset at, string uuid, string cwd, object? extra = null)
    {
        var line = new Dictionary<string, object?> { ["type"] = "user", ["message"] = new { role = "user", content = text }, ["uuid"] = uuid, ["timestamp"] = Stamp(at), ["cwd"] = cwd };
        if (extra is Dictionary<string, object?> more) foreach (var (key, value) in more) line[key] = value;
        return Json(line);
    }
    private static string ClaudeAnswer(string text, DateTimeOffset at, string uuid) =>
        Json(new { type = "assistant", message = new { id = "msg-" + uuid, role = "assistant", model = "claude-opus-4-5", content = new[] { new { type = "text", text } } }, uuid, timestamp = Stamp(at) });

    /// Fifteen requests an hour apart; the pane retains the 13th and later.
    private static List<string> ClaudeConversation(string cwd, int count = 15)
    {
        var lines = new List<string>();
        for (var i = 1; i <= count; i++)
        {
            var at = Base.AddHours(i);
            lines.Add(ClaudeUser("request " + i, at, "u" + i, cwd));
            lines.Add(ClaudeAnswer("answer " + i, at.AddSeconds(5), "a" + i));
        }
        return lines;
    }

    /// <summary>
    /// A request whose own turn ended while its backgrounded command ran reads completed and says it waits on it; the task's
    /// notice and the reply to it are still recorded, the result goes on with that notice turn's answer under a line naming the
    /// notice, an empty notice turn adds nothing, and a replayed record builds the same result
    /// (macOS SessionHistoryTests.aNoticeTurnsAnswerFollowsTheRequestsLiveAndReplayed).
    /// </summary>
    internal static Task ANoticeTurnsAnswerFollowsTheRequestsLiveAndReplayed()
    {
        var emitted = new List<ExecutionGraphNode>();
        var tracker = new ExecutionGraphTracker("wait-run", "ping it", "claude", null, emitted.Add);
        void Frame(object value) { using var json = JsonDocument.Parse(Json(value)); tracker.Consume(json.RootElement.Clone()); }
        const string notice = "<task-notification>\n<task-id>b1</task-id>\n<tool-use-id>ping-cmd</tool-use-id>\n<status>completed</status>\n<summary>ping exited 0</summary>\n</task-notification>";
        var launch = new { type = "assistant", message = new { content = new object[] { new { type = "tool_use", id = "ping-cmd", name = "Bash", input = new { command = "ping -n 30 localhost", description = "ping", run_in_background = true } } } } };
        var started = new { type = "user", message = new { content = new object[] { new { type = "tool_result", tool_use_id = "ping-cmd", content = "Command running in background with ID: b1." } } } };
        Frame(launch); Frame(started);
        Frame(new { type = "assistant", message = new { content = new[] { new { type = "text", text = "Started ping." } } } });
        Frame(new { type = "result", subtype = "success", is_error = false, result = "Started ping." });
        var waiting = tracker.BuildRun(includeRunning: true)!;
        // Its result shows as its own turn ends (the background task runs on in the strip), and it says what it waits on.
        Check(waiting.Status == "completed" && MightyGraphSupport.BackgroundWaiting(waiting) == 1 && MightyGraphLayout.Finished(waiting)
            && MightyGraphBlockModel.RunStateLabel(waiting) == "턴 완료 · 백그라운드 1개 실행 중", "the request reads completed and waits on its task; got " + waiting.Status + " / " + MightyGraphBlockModel.RunStateLabel(waiting));
        Frame(new { type = "user", message = new { content = new[] { new { type = "text", text = notice } } } });
        Frame(new { type = "assistant", message = new { content = new[] { new { type = "text", text = "Ping finished fine." } } } });
        Frame(new { type = "result", subtype = "success", is_error = false, result = "Ping finished fine.", origin = new { kind = "task-notification" } });
        Frame(new { type = "result", subtype = "success", is_error = false, result = " ", origin = new { kind = "task-notification" } });
        tracker.Finish("completed");
        var live = tracker.BuildRun()!;
        var notices = tracker.BuildNoticeRuns();
        Check(!live.Agents.Any(MightyGraphSupport.IsTaskNotice) && notices is [{ NoticeRun: true, Status: "completed", FinalOutput: "Ping finished fine.", Input: "ping · 완료" }],
            "the notice turn is a run of its own whose result is its answer; an empty notice turn adds nothing");
        Check(live.FinalOutput == "Started ping." && MightyGraphSupport.BackgroundWaiting(live) == 0 && MightyGraphBlockModel.RunStateLabel(live) == "완료",
            "the request's result stays its own; got " + live.FinalOutput);
        // An older save's result that goes on with a notice section: the main block keeps the request's own answer, and the
        // result's whole text is not copied into it again.
        var expected = "Started ping." + MightyGraphSupport.NoticeSectionMark + "작업 알림 · ping 이후**\n\nPing finished fine.";
        live.FinalOutput = expected;
        live.RootEntries = [new("a1", "assistant", "Started ping.", Stamp(Base)), new("a2", "assistant", "Ping finished fine.", Stamp(Base))];
        MightyGraphSupport.RefreshResult(live);
        Check(live.ResultEntries.Single().Text == expected && live.RootEntries.Count == 2, "the result block shows the whole result and the main block stays as it was");

        // The transcript after the turn ended still reaches the waiting request's 실행 block.
        var session = new RunSession { Id = "p", Kind = "claude", Provider = "claude", Status = "running", GraphRuns = [waiting], BackgroundWork = new([new("b1", "bash", "ping", Stamp(Base))], TurnEnded: true) };
        var merged = new AppSnapshot { Sessions = [session] }.Apply(new RunEvent("p", "log", new("late", "assistant", "Ping finished fine.", Stamp(Base)))).Sessions.Single();
        Check(merged.GraphRuns![^1].RootEntries.Any(e => e.Id == "late"), "a notice turn's transcript joins the request that waits on its task");

        using var fixture = new Fixture();
        var id = "7d1e2c1a-1111-4222-8333-444455556666";
        var at = Base.AddHours(1);
        fixture.ClaudeRecord(id, [
            ClaudeUser("ping it", at, "u1", fixture.Workspace),
            Json(new { launch.type, launch.message, uuid = "a1", timestamp = Stamp(at.AddSeconds(1)) }),
            Json(new { started.type, started.message, toolUseResult = new { }, uuid = "t1", timestamp = Stamp(at.AddSeconds(2)) }),
            ClaudeAnswer("Started ping.", at.AddSeconds(3), "a2"),
            ClaudeUser(notice, at.AddSeconds(30), "n1", fixture.Workspace, new Dictionary<string, object?> { ["origin"] = new { kind = "task-notification" } }),
            ClaudeAnswer("Ping finished fine.", at.AddSeconds(31), "a3"),
        ]);
        var replayed = SessionHistory.Load(new SessionHistoryRequest("claude", id, fixture.Workspace) { Home = fixture.Home }).Runs;
        Check(replayed.Count == 2 && replayed[0].FinalOutput == "Started ping." && replayed[1] is { NoticeRun: true, FinalOutput: "Ping finished fine." },
            "a replayed record builds the live runs; got " + string.Join(" | ", replayed.Select(r => r.Input + " => " + r.FinalOutput)));
        return Task.CompletedTask;
    }

    /// <summary>
    /// A request handed into the run after its turn ended (run next while only background work is left) is a 중간 요청 whose reply is
    /// the next root answer, and that turn's answer follows the request's in the result under "중간 요청 이후"; the request stays
    /// completed (macOS ExecutionGraphTests.aSteerAfterTheTurnFollowsInTheResult).
    /// </summary>
    internal static Task ASteerAfterTheTurnFollowsInTheResult()
    {
        var emitted = new List<ExecutionGraphNode>();
        var tracker = new ExecutionGraphTracker("steer-after", "ping it", "claude", null, emitted.Add);
        void Frame(object value) { using var json = JsonDocument.Parse(Json(value)); tracker.Consume(json.RootElement.Clone()); }
        tracker.Steer("early", "Also log it");
        Frame(new { type = "assistant", message = new { content = new[] { new { type = "text", text = "Started ping." } } } });
        Frame(new { type = "result", subtype = "success", is_error = false, result = "Started ping." });
        tracker.Steer("late", "What is the time?");
        Frame(new { type = "assistant", message = new { content = new[] { new { type = "text", text = "It is noon." } } } });
        Frame(new { type = "result", subtype = "success", is_error = false, result = "It is noon." });
        tracker.Finish("completed");
        var run = tracker.BuildRun()!;
        var late = run.Agents.Single(a => a.Input == "What is the time?");
        Check(run.Status == "completed" && late.Status == "completed" && late.Entries.Any(e => e.Text == "It is noon."), "the steered turn is a 중간 요청 answered by the next root answer");
        Check(run.FinalOutput == "Started ping." + MightyGraphSupport.NoticeSectionMark + "중간 요청 이후**\n\nIt is noon.", "a steer before the result changes nothing; one after it adds its answer under its own line; got " + run.FinalOutput);
        return Task.CompletedTask;
    }

    /// <summary>
    /// A delegation notice handed in after the request's result opens a 위임 알림 notice run with the answer; the 위임 알림 block stays in
    /// the request and takes the reply; the request's result is untouched and no second run opens
    /// (macOS ExecutionGraphTests.aDelegationNoticeAfterTheTurnOpensItsOwnRun).
    /// </summary>
    internal static Task ADelegationNoticeAfterTheTurnOpensItsOwnRun()
    {
        var emitted = new List<ExecutionGraphNode>();
        var tracker = new ExecutionGraphTracker("deleg-after", "Split the work", "claude", null, emitted.Add);
        void Frame(object value) { using var json = JsonDocument.Parse(Json(value)); tracker.Consume(json.RootElement.Clone()); }
        Frame(new { type = "assistant", message = new { id = "main-1", content = new[] { new { type = "text", text = "Delegated." } } } });
        Frame(new { type = "result", subtype = "success", is_error = false, result = "Delegated." });
        const string noticeText = "[Mighty Claude notice n-1] Your child c-1 reported revision 1. Read the report with child_status; merge it with merge when it is right.";
        tracker.Steer("n-1", noticeText);
        Frame(new { type = "assistant", message = new { id = "main-2", content = new[] { new { type = "text", text = "Merged the child." } } } });
        Frame(new { type = "result", subtype = "success", is_error = false, result = "Merged the child." });
        var latest = new Dictionary<string, ExecutionGraphNode>();
        foreach (var node in emitted) latest[node.Id] = node;
        Check(latest[ExecutionGraphSupport.MainNodeID("deleg-after")].Output == "Delegated.", "the request's result is untouched; got " + latest[ExecutionGraphSupport.MainNodeID("deleg-after")].Output);
        var notice = latest.GetValueOrDefault(ExecutionGraphSupport.MainNodeID(BackgroundNoticeRuns.RunID("deleg-after", 1)))
            ?? throw new InvalidOperationException("the delegation notice opens no run");
        Check(notice.Title == Locale.Get("graph.block.delegationNotice") && notice.Input == noticeText, "the 위임 알림 run names the notice and holds it; got '" + notice.Title + "' / '" + notice.Input + "'");
        Check(notice is { Output: "Merged the child.", State: "completed" }, "the run's result is the answer; got " + notice.State + " / " + notice.Output);
        var steer = latest[ExecutionGraphSupport.Identifier("deleg-after", "steer:n-1")];
        Check(steer.RunId == "deleg-after" && steer.Output == "Merged the child.", "the 위임 알림 block stays in the request and takes the reply");
        // One notice, one run: nothing opened a second.
        Check(!emitted.Any(n => n.RunId == BackgroundNoticeRuns.RunID("deleg-after", 2)), "no second run opens");
        tracker.Finish("completed");
        Check(tracker.BuildNoticeRuns() is [{ NoticeRun: true, NoticeTitle: var title }] && title == Locale.Get("graph.block.delegationNotice"), "the projection carries the run's title");
        return Task.CompletedTask;
    }

    /// <summary>
    /// A delegation notice after the request's result with no room left for its run: its answer follows in the request's result under
    /// 위임 알림, as before notice runs, and never replaces that result (macOS ExecutionGraphTests.aDelegationNoticeAtTheNodeCapFollowsInTheResult).
    /// </summary>
    internal static Task ADelegationNoticeAtTheNodeCapFollowsInTheResult()
    {
        var emitted = new List<ExecutionGraphNode>();
        var tracker = new ExecutionGraphTracker("deleg-full", "Split the work", "claude", null, emitted.Add);
        void Frame(object value) { using var json = JsonDocument.Parse(Json(value)); tracker.Consume(json.RootElement.Clone()); }
        // The request's own blocks fill all nodes but one, which the notice's steer block takes.
        for (var index = 1; index < ExecutionGraphSupport.MaximumNodes - 1; index++) Frame(new { type = "system", subtype = "compact_boundary", uuid = "c" + index });
        Frame(new { type = "result", subtype = "success", is_error = false, result = "Delegated." });
        tracker.Steer("n-1", "[Mighty Claude notice n-1] Your child c-1 reported revision 1.");
        Frame(new { type = "assistant", message = new { id = "main-2", content = new[] { new { type = "text", text = "Merged the child." } } } });
        Frame(new { type = "result", subtype = "success", is_error = false, result = "Merged the child." });
        var latest = new Dictionary<string, ExecutionGraphNode>();
        foreach (var node in emitted) latest[node.Id] = node;
        Check(latest.Count == ExecutionGraphSupport.MaximumNodes && !latest.Values.Any(n => BackgroundNoticeRuns.IsNoticeRun(n.RunId)), "the graph is full and no notice run opened; got " + latest.Count);
        var header = Locale.Get("graph.result.afterSteer", new Dictionary<string, string> { ["block"] = Locale.Get("graph.block.delegationNotice") });
        var output = latest[ExecutionGraphSupport.MainNodeID("deleg-full")].Output;
        Check(output == "Delegated." + MightyGraphSupport.NoticeSectionMark + header + "**\n\nMerged the child.", "the answer follows under 위임 알림 and keeps the result; got " + output);
        Check(latest[ExecutionGraphSupport.Identifier("deleg-full", "steer:n-1")].Output == "Merged the child.", "the 위임 알림 block takes the reply");
        return Task.CompletedTask;
    }

    /// <summary>
    /// A running agent's message turn when message runs have no room: its answer takes the nodes kept for work copies, and at the very cap it
    /// joins the agent's log as Claude's answer. It never becomes a section of the request's result
    /// (macOS ExecutionGraphTests.aMessageAnswerWithNoRoomNeverBecomesASectionOfTheResult).
    /// </summary>
    internal static Task AMessageAnswerWithNoRoomNeverBecomesASectionOfTheResult()
    {
        const string agent = "b2c3d4e5f6a7b8c9d";
        Dictionary<string, ExecutionGraphNode> Replay(int filling)
        {
            var emitted = new List<ExecutionGraphNode>();
            var tracker = new ExecutionGraphTracker("msg-full", "Find the config", "claude", null, emitted.Add);
            void Frame(object value) { using var json = JsonDocument.Parse(Json(value)); tracker.Consume(json.RootElement.Clone()); }
            Frame(new { type = "assistant", message = new { id = "m-1", content = new[] { new { type = "tool_use", id = "toolu_scout", name = "Agent",
                input = new { description = "Find the config", prompt = "Find it.", name = "scout", run_in_background = true } } } } });
            Frame(new { type = "user", message = new { content = new[] { new { type = "tool_result", tool_use_id = "toolu_scout", content = "Async agent launched successfully.\nagentId: " + agent + " (internal ID)" } } } });
            for (var index = 0; index < filling; index++) Frame(new { type = "system", subtype = "compact_boundary", uuid = "c" + index });
            Frame(new { type = "result", subtype = "success", is_error = false, result = "Scouting." });
            Frame(new { type = "user", origin = new { kind = "peer", from = agent, senderTaskId = agent },
                message = new { role = "user", content = "Another Claude session sent a message:\n<agent-message from=\"" + agent + "\">\nFound the config.\n</agent-message>" } });
            Frame(new { type = "assistant", message = new { id = "m-2", content = new[] { new { type = "text", text = "Good." } } } });
            Frame(new { type = "result", subtype = "success", is_error = false, result = "Good.", origin = new { kind = "peer" } });
            var latest = new Dictionary<string, ExecutionGraphNode>();
            foreach (var node in emitted) latest[node.Id] = node;
            return latest;
        }
        var mainID = ExecutionGraphSupport.MainNodeID("msg-full"); var agentID = ExecutionGraphSupport.AgentNodeID("msg-full", "toolu_scout");
        // The main block, the agent and compactions up to the reserve: the message run takes a kept node.
        var reserved = Replay(ExecutionGraphSupport.MaximumNodes - 16 - 2);
        var run = reserved.GetValueOrDefault(ExecutionGraphSupport.MainNodeID(BackgroundNoticeRuns.RunID("msg-full", 1))) ?? throw new InvalidOperationException("the message opens no run");
        Check(run.Title == Locale.Get("graph.background.messageRun") + " · scout" && run.Output == "Good." && run.Input == "Found the config." && reserved[mainID].Output == "Scouting.",
            "the message run takes a kept node; got '" + run.Title + "' / '" + run.Output + "'");
        // Every node taken: the answer joins the agent's log, the request's result stays.
        var full = Replay(ExecutionGraphSupport.MaximumNodes - 2);
        var reply = "**" + Locale.Get("graph.background.reply") + "**\n\nGood.";
        Check(full.Count == ExecutionGraphSupport.MaximumNodes && !full.Values.Any(n => BackgroundNoticeRuns.IsNoticeRun(n.RunId)) && full[mainID].Output == "Scouting."
            && full[agentID].Entries.Any(e => e.Kind == "assistant" && e.Text == reply), "at the very cap the answer joins the agent's log; got " + full[mainID].Output);
        return Task.CompletedTask;
    }

    internal static Task ScrollingUpLoadsTheTenRequestsAboveTheRetainedOneFromAClaudeRecord()
    {
        using var fixture = new Fixture();
        var id = "0b9e2c1a-1111-4222-8333-444455556666";
        fixture.ClaudeRecord(id, ClaudeConversation(fixture.Workspace));
        // A line still being written past the last newline is never read.
        File.AppendAllText(Path.Combine(fixture.Home, ".claude", "projects", SessionHistory.ClaudeProjectFolder(fixture.Workspace), id + ".jsonl"), "{\"type\":\"user\",\"message\":{\"content\":\"half");

        var located = SessionHistory.Locate("claude", id, fixture.Workspace, new Dictionary<string, string>(), fixture.Home);
        Check(located is not null && located.EndsWith(id + ".jsonl", StringComparison.Ordinal), "the Claude record must be found under projects/<escaped cwd>");
        var state = new SessionHistoryState();
        var template = new SessionHistoryRequest("claude", id, fixture.Workspace) { Home = fixture.Home };
        var anchor = new SessionHistoryAnchor("request 13", Base.AddHours(13).AddSeconds(-1));
        var request = state.Begin("run-13", id, anchor, template);
        Check(request is not null && state.Phase == SessionHistoryState.Phases.Loading && request.Anchor == anchor && request.End is null, "the first load starts at the end of the record above the retained request");
        var first = SessionHistory.Load(request!);
        Check(first.Runs.Count == SessionHistory.TurnsPerChunk, "a chunk is ten requests, found " + first.Runs.Count);
        Check(first.Runs.Select(r => r.Input).SequenceEqual(Enumerable.Range(3, 10).Select(i => "request " + i)), "the chunk is requests 3–12, oldest first: " + string.Join(", ", first.Runs.Select(r => r.Input)));
        Check(first.Runs.All(r => r.Id.StartsWith(HistoryReplay.RunPrefix, StringComparison.Ordinal) && r.Status == "completed" && r.Provider == "claude"), "replayed runs are record- runs that completed");
        Check(first.Runs[^1].FinalOutput == "answer 12", "the result is the request's last answer, found " + first.Runs[^1].FinalOutput);
        Check(!first.ReachedStart && first.End > 0, "the record goes on above the chunk");
        Check(!state.Finish(first, null, state.Generation) && state.Phase == SessionHistoryState.Phases.Idle && state.Runs.Count == 10, "the chunk is kept and more can load");

        var next = state.Begin("run-13", id, anchor, template);
        Check(next is not null && next.Anchor is null && next.End == first.End && next.File == first.File, "the next load continues above the first chunk in the same file");
        var second = SessionHistory.Load(next!);
        Check(second.ReachedStart && second.Runs.Select(r => r.Input).SequenceEqual(["request 1", "request 2"]), "the rest of the record reaches its start");
        state.Finish(second, null, state.Generation);
        Check(state.Phase == SessionHistoryState.Phases.Start && state.Runs.Select(r => r.Input).SequenceEqual(Enumerable.Range(1, 12).Select(i => "request " + i)), "older requests go first");
        Check(state.Begin("run-13", id, anchor, template) is null, "nothing more loads once the record's start is on screen");
        Check(state.BlockText(state.Runs.Count) == Locale.Get("graph.history.start") + " · " + Locale.Get("graph.history.loaded", new Dictionary<string, string> { ["count"] = "12" }), "the history block uses the shared locale keys");
        return Task.CompletedTask;
    }

    internal static Task AReplacedRecordStartsOverAndAPaneWithoutASessionHasNone()
    {
        using var fixture = new Fixture();
        var id = "1c9e2c1a-1111-4222-8333-444455556666";
        var path = fixture.ClaudeRecord(id, ClaudeConversation(fixture.Workspace, 3));
        var file = SessionHistory.Identify(path)!;
        try { SessionHistory.Load(new SessionHistoryRequest("claude", id, fixture.Workspace) { Home = fixture.Home, File = file, End = new FileInfo(path).Length + 10 }); Check(false, "a cursor past the end must fail"); }
        catch (SessionHistoryException ex) { Check(ex.Error == SessionHistoryError.Changed, "a shortened record is a changed record"); }
        var state = new SessionHistoryState();
        state.Begin(null, id, null, new SessionHistoryRequest("claude", id, fixture.Workspace));
        var generation = state.Generation;
        Check(state.Finish(null, new SessionHistoryException(SessionHistoryError.Changed), generation) && state.Phase == SessionHistoryState.Phases.Idle && state.Generation == generation + 1, "a replaced record resets the pane's history and loads again");
        Check(!state.Finish(null, new SessionHistoryException(SessionHistoryError.Unreadable), generation), "a result from before the reset is ignored");
        var none = new SessionHistoryState();
        Check(none.Begin(null, null, null, new SessionHistoryRequest("claude", "", fixture.Workspace)) is null && none.Phase == SessionHistoryState.Phases.Unavailable, "a pane that resumes nothing has no record to read");
        Check(none.BlockText(0) == Locale.Get("graph.history.none"), "and says so");
        try { SessionHistory.Load(new SessionHistoryRequest("shell", id, fixture.Workspace) { Home = fixture.Home }); Check(false, "a shell must be unsupported"); }
        catch (SessionHistoryException ex) { Check(ex.Error == SessionHistoryError.Unsupported, "a shell keeps no record the graph reads"); }
        try { SessionHistory.Load(new SessionHistoryRequest("gemini", id, fixture.Workspace) { Home = fixture.Home }); Check(false, "a missing Antigravity transcript must be missing"); }
        catch (SessionHistoryException ex) { Check(ex.Error == SessionHistoryError.Missing, "an Antigravity conversation without its transcript has nothing to read"); }
        return Task.CompletedTask;
    }

    internal static Task OnlyWhatTheUserTypedIsARequest()
    {
        var cwd = "/w";
        JsonElement Line(string json) => JsonDocument.Parse(json).RootElement.Clone();
        Check(HistoryScan.ClaudePrompt(Line(ClaudeUser("fix it", Base, "u", cwd))) == "fix it", "-p stream input has no origin and is a request");
        Check(HistoryScan.ClaudePrompt(Line(ClaudeUser("typed", Base, "u", cwd, new Dictionary<string, object?> { ["origin"] = new { kind = "human" } }))) == "typed", "a human origin is a request");
        Check(HistoryScan.ClaudePrompt(Line(ClaudeUser("note", Base, "u", cwd, new Dictionary<string, object?> { ["origin"] = new { kind = "task-notification" } }))) is null, "an injected notification is not");
        Check(HistoryScan.ClaudePrompt(Line(ClaudeUser("peer", Base, "u", cwd, new Dictionary<string, object?> { ["turnOrigin"] = "peer" }))) is null, "a peer agent's message is not");
        Check(HistoryScan.ClaudePrompt(Line(ClaudeUser("meta", Base, "u", cwd, new Dictionary<string, object?> { ["isMeta"] = true }))) is null, "a meta line is not");
        Check(HistoryScan.ClaudePrompt(Line(ClaudeUser("side", Base, "u", cwd, new Dictionary<string, object?> { ["isSidechain"] = true }))) is null, "a sub-agent line is not");
        Check(HistoryScan.ClaudePrompt(Line(ClaudeUser("[Request interrupted by user]", Base, "u", cwd))) is null, "an interruption is not");
        Check(HistoryScan.ClaudePrompt(Line(ClaudeUser("<local-command-stdout>Set model</local-command-stdout>", Base, "u", cwd))) is null, "a local command's output is not");
        Check(HistoryScan.ClaudePrompt(Line(ClaudeUser("<command-name>/review</command-name>\n<command-args>main</command-args>", Base, "u", cwd))) == "/review main", "a slash command shows as typed");
        Check(HistoryScan.ClaudePrompt(Line(Json(new { type = "user", message = new { content = new[] { new { type = "tool_result", tool_use_id = "t", content = "x" } } } }))) is null, "a tool result is not");
        Check(HistoryScan.LocalCommandOutput("<local-command-stdout>\u001b[1mSet model to opus\u001b[22m</local-command-stdout>") == "Set model to opus", "a local command's output loses its colour codes");
        // Codex: the item beats the event, which beats the response; injected context never titles.
        byte[] B(string json) => System.Text.Encoding.UTF8.GetBytes(json);
        var injected = B(Json(new { type = "response_item", payload = new { type = "message", role = "user", content = new[] { new { type = "input_text", text = "<environment_context>x</environment_context>" } } } }));
        var response = B(Json(new { type = "response_item", payload = new { type = "message", role = "user", content = new[] { new { type = "input_text", text = "from response" } } } }));
        var evented = B(Json(new { type = "event_msg", payload = new { type = "user_message", message = "from event" } }));
        var item = B(Json(new { type = "event_msg", payload = new { type = "item_completed", item = new { id = "i", type = "UserMessage", content = new[] { new { text = "from item" } } } } }));
        Check(HistoryScan.CodexPrompt([injected]) is null, "injected context is not a request");
        Check(HistoryScan.CodexPrompt([injected, response]) == "from response", "the first real user message");
        Check(HistoryScan.CodexPrompt([response, evented]) == "from event", "the event beats the response item");
        Check(HistoryScan.CodexPrompt([response, evented, item]) == "from item", "the UserMessage item beats both");
        return Task.CompletedTask;
    }

    internal static Task CodexRolloutsReplayThroughTheLiveTracker()
    {
        using var fixture = new Fixture();
        var thread = "2d9e2c1a-1111-4222-8333-444455556666";
        var lines = new List<string> { Json(new { timestamp = Stamp(Base), type = "session_meta", payload = new { id = thread, cwd = fixture.Workspace, source = "exec" } }) };
        for (var i = 1; i <= 3; i++)
        {
            var at = Base.AddHours(i);
            lines.Add(Json(new { timestamp = Stamp(at), type = "turn_context", payload = new { model = "gpt-5-codex" } }));
            lines.Add(Json(new { timestamp = Stamp(at), type = "event_msg", payload = new { type = "task_started", turn_id = "turn-" + i } }));
            lines.Add(Json(new { timestamp = Stamp(at), type = "event_msg", payload = new { type = "user_message", message = "codex request " + i } }));
            lines.Add(Json(new { timestamp = Stamp(at.AddSeconds(2)), type = "event_msg", payload = new { type = "item_completed", item = new { id = "cmd-" + i, type = "CommandExecution", command = new[] { "bash", "-lc", "ls" }, aggregated_output = "a.txt", exit_code = 0 } } }));
            lines.Add(Json(new { timestamp = Stamp(at.AddSeconds(3)), type = "event_msg", payload = new { type = "item_completed", item = new { id = "msg-" + i, type = "AgentMessage", content = new[] { new { text = "codex answer " + i } } } } }));
            lines.Add(Json(new { timestamp = Stamp(at.AddSeconds(4)), type = "event_msg", payload = new { type = "task_complete" } }));
        }
        fixture.CodexRecord(thread, lines);
        var chunk = SessionHistory.Load(new SessionHistoryRequest("codex", thread, fixture.Workspace) { Home = fixture.Home });
        Check(chunk.ReachedStart && chunk.Runs.Select(r => r.Input).SequenceEqual(["codex request 1", "codex request 2", "codex request 3"]), "every Codex turn becomes a request: " + string.Join(", ", chunk.Runs.Select(r => r.Input)));
        Check(chunk.Runs.All(r => r.Provider == "codex" && r.Status == "completed" && r.Id.StartsWith("record-turn-", StringComparison.Ordinal)), "Codex runs complete and are keyed by turn");
        Check(chunk.Runs[^1].FinalOutput == "codex answer 3", "the agent message is the result, found " + chunk.Runs[^1].FinalOutput);
        Check(chunk.Runs[0].NodeModelLabel is { Length: > 0 }, "the turn's model labels the request");
        // The newest request is retained on screen: only the two above it load.
        var anchored = SessionHistory.Load(new SessionHistoryRequest("codex", thread, fixture.Workspace) { Home = fixture.Home, Anchor = new SessionHistoryAnchor("codex request 3", Base.AddHours(3)) });
        Check(anchored.Runs.Select(r => r.Input).SequenceEqual(["codex request 1", "codex request 2"]), "the chunk starts right above the retained request");
        return Task.CompletedTask;
    }

    internal static Task LoadedRunsStayBoundedAndFollowATrim()
    {
        MightyGraphRun Run(string id) => new() { Id = id, Input = id, Status = "completed" };
        var state = new SessionHistoryState();
        state.Begin("c", "s", null, new SessionHistoryRequest("claude", "s", "/w"));
        state.Finish(new SessionHistoryChunk([.. Enumerable.Range(0, 120).Select(i => Run("record-" + i))], new SessionHistoryFile("/r", 1), 5, false), null, state.Generation);
        Check(state.Runs.Count == SessionHistoryState.MaximumRuns && state.Phase == SessionHistoryState.Phases.Limit, "a pane keeps at most 100 older requests and then stops loading");
        Check(state.Runs[0].Id == "record-20", "the oldest go first");
        Check(state.BlockText(100).StartsWith(Locale.Get("graph.history.limit"), StringComparison.Ordinal), "the block says the pane shows no more");

        var trimmed = new SessionHistoryState();
        trimmed.Begin("a", "s", null, new SessionHistoryRequest("claude", "s", "/w"));
        trimmed.Finish(new SessionHistoryChunk([Run("record-1")], new SessionHistoryFile("/r", 1), 5, false), null, trimmed.Generation);
        trimmed.Follow([Run("a"), Run("b"), Run("c")], [Run("c")], "s", "claude");
        Check(trimmed.Runs.Select(r => r.Id).SequenceEqual(["record-1", "a", "b"]) && trimmed.AnchorRunID == "c" && trimmed.PinnedRunID == "a", "runs a trim drops move into the history, so nothing is wiped");
        Check(trimmed.Connects("c", "s") && !trimmed.Connects("c", "other"), "history attaches only above its own session");
        trimmed.Reconcile("c", "other");
        Check(trimmed.Runs.Count == 0, "resuming another session drops what no longer connects");
        return Task.CompletedTask;
    }

    internal static Task TheLayoutStacksLoadedRequestsAboveWithoutMovingTheRetainedOne()
    {
        MightyGraphRun Run(string id) => new() { Id = id, Input = id, Status = "completed", FinalOutput = "done", ResultEntries = [new LogEntry(id + "-r", "assistant", "done", Stamp(Base))] };
        var retained = new List<MightyGraphRun> { Run("live-1"), Run("live-2") };
        var plain = MightyGraphLayout.Make(retained, "", false, new HashSet<string>());
        var older = new List<MightyGraphRun> { Run("record-1"), Run("record-2") };
        var stacked = MightyGraphLayout.Make([.. older, .. retained], "", false, new HashSet<string>(), retainedStart: older.Count, history: true);
        GraphRect Frame(MightyGraphLayout layout, string id) => layout.Nodes.First(n => n.Id == id).Frame;
        var request = MightyGraphLayout.NodeID(retained[0], "request");
        Check(Frame(plain, request) == Frame(stacked, request), "loading older requests moves nothing already on screen");
        Check(Frame(stacked, MightyGraphLayout.NodeID(older[1], "request")).MaxY < Frame(stacked, request).Y, "older requests sit above the retained one");
        var history = stacked.Nodes.First();
        Check(history.Id == MightyGraphLayout.HistoryNodeID && history.Kind == "history" && history.Frame.MaxY < stacked.Nodes.Skip(1).Min(n => n.Frame.Y), "the history block is the top of the diagram");
        Check(stacked.OriginY < 0 && stacked.OriginY <= history.Frame.Y - 24, "the canvas starts above the history block");
        Check(plain.OriginY == 0 && !plain.Nodes.Any(n => n.Kind == "history"), "without history nothing changes");
        Check(MightyGraphCamera.IsAuxiliary(MightyGraphLayout.HistoryNodeID), "the camera never aims at the history block");
        Check(MightyGraphCamera.ShowsTop(-(history.Frame.Y - 4) * 1.5, 1.5, history.Frame.Y), "the top shows once the view reaches the history block");
        Check(!MightyGraphCamera.ShowsTop(-(history.Frame.Y + 200) * 1.5, 1.5, history.Frame.Y), "further down it does not");
        return Task.CompletedTask;
    }

    internal static Task ResumePickerHidesNestedOuroborosRunsUnlessEverySessionIsShown()
    {
        using var fixture = new Fixture();
        var now = Base.AddDays(2);
        string Id(int n) => $"{n:x8}-1111-4222-8333-444455556666";
        fixture.ClaudeRecord(Id(1), [ClaudeUser("add a login page", Base, "u1", fixture.Workspace), ClaudeAnswer("done", Base.AddSeconds(3), "a1")], modified: now.AddHours(-1));
        fixture.ClaudeRecord(Id(2), [ClaudeUser("User: plan the seed\n\nAssistant: ok", Base, "u2", fixture.Workspace)], modified: now.AddHours(-2));
        fixture.ClaudeRecord(Id(3), [ClaudeUser("Assistant: evaluate", Base, "u3", fixture.Workspace)], modified: now.AddHours(-3));
        var query = new ResumableSessionQuery(fixture.Workspace) { Home = fixture.Home, Now = now };
        var listing = ResumableSessions.Listing(query, "claude");
        Check(listing.Items.Select(i => i.SessionID).SequenceEqual([Id(1)]) && listing.Hidden == 2, "nested Ouroboros runs are hidden by default: " + string.Join(", ", listing.Items.Select(i => i.Title)) + " hidden " + listing.Hidden);
        Check(listing.Items[0].Title == "add a login page" && listing.Items[0].Requests == 1 && listing.Items[0].Model == "claude-opus-4-5", "the row shows the first request, the request count and the model");
        var all = ResumableSessions.Listing(query with { IncludeAutomated = true }, "claude");
        Check(all.Items.Count == 3 && all.Hidden == 0 && all.Items.Count(i => i.Automated) == 2, "모든 세션 보기 lists them marked as automated");
        Check(ResumableSessions.Details(all.Items[1], now).Contains(Locale.Get("resume.automated"), StringComparison.Ordinal), "an automated row says so");
        var known = ResumableSessions.Listing(query with { Known = new HashSet<string> { Id(2).ToUpperInvariant().ToLowerInvariant() } }, "claude");
        Check(known.Items.Select(i => i.SessionID).SequenceEqual([Id(1), Id(2)]) && known.Hidden == 1, "a session the app's own pane started is never hidden");
        Check(ResumableSessions.AutomatedPrompt("User: x") && ResumableSessions.AutomatedPrompt("Assistant:\nx") && !ResumableSessions.AutomatedPrompt("Users: x") && !ResumableSessions.AutomatedPrompt(null), "the first-prompt rule");
        return Task.CompletedTask;
    }

    internal static Task ResumePickerListsOnlyThisFoldersSessionsNewestFirstMinusOpenPanes()
    {
        using var fixture = new Fixture();
        var now = Base.AddDays(2);
        string Id(int n) => $"{n:x8}-2222-4222-8333-444455556666";
        var other = Path.Combine(fixture.Root, "work", "project-other");
        fixture.ClaudeRecord(Id(1), [ClaudeUser("older", Base, "u1", fixture.Workspace)], modified: now.AddHours(-5));
        fixture.ClaudeRecord(Id(2), [ClaudeUser("newer", Base, "u2", fixture.Workspace)], modified: now.AddMinutes(-1));
        fixture.ClaudeRecord(Id(3), [ClaudeUser("open in a pane", Base, "u3", fixture.Workspace)], modified: now.AddHours(-1));
        // Two paths can escape to one folder name: the record's cwd decides.
        fixture.ClaudeRecord(Id(4), [ClaudeUser("another folder", Base, "u4", other)], workspace: fixture.Workspace, modified: now.AddHours(-1));
        fixture.ClaudeRecord(Id(5), [ClaudeUser("too old", Base, "u5", fixture.Workspace)], modified: now.AddDays(-61));
        fixture.ClaudeRecord("agent-abc", [ClaudeUser("not a session id", Base, "u6", fixture.Workspace)], modified: now);
        var thread = "3d9e2c1a-1111-4222-8333-444455556666";
        var child = "4d9e2c1a-1111-4222-8333-444455556666";
        fixture.CodexRecord(thread, [Json(new { type = "session_meta", payload = new { id = thread, cwd = fixture.Workspace } }), Json(new { type = "event_msg", payload = new { type = "task_started", turn_id = "t" } }), Json(new { type = "event_msg", payload = new { type = "user_message", message = "codex work" } })], modified: now.AddHours(-2));
        fixture.CodexRecord(child, [Json(new { type = "session_meta", payload = new { id = child, cwd = fixture.Workspace, source = new { subagent = "review" } } })], modified: now.AddHours(-1));
        var panes = new[] { new RunSession { Kind = "claude", Provider = "claude", ResumeId = Id(3).ToUpperInvariant() }, new RunSession { Kind = "shell", ResumeId = Id(2) } };
        var query = new ResumableSessionQuery(fixture.Workspace) { Home = fixture.Home, Now = now, Excluding = ResumableSessions.InUse(panes) };
        var claude = ResumableSessions.Listing(query, "claude");
        Check(claude.Items.Select(i => i.Title).SequenceEqual(["newer", "older"]), "this folder's sessions, newest first, minus open panes, other folders, old and non-session records: " + string.Join(", ", claude.Items.Select(i => i.Title)));
        Check(ResumableSessions.MayBeRunning(claude.Items[0], now) && ResumableSessions.Details(claude.Items[0], now).Contains(Locale.Get("resume.recentlyModified"), StringComparison.Ordinal), "a record written in the last two minutes may still be running");
        var codex = ResumableSessions.Listing(query, "codex");
        Check(codex.Items.Select(i => i.SessionID).SequenceEqual([thread]) && codex.Items[0].Title == "codex work", "Codex lists its own thread, never a sub-agent's");
        Check(ResumableSessions.Listing(query, "gemini").Items.Count == 0 && ResumableSessions.OffersResume("claude", "gemini") && ResumableSessions.OffersResume("claude", "codex") && !ResumableSessions.OffersResume("shell", "claude"), "Antigravity asks when agy has a conversation here (none: agy never ran); terminals and browsers never ask");
        Check(ResumableSessions.Listing(query with { HeadOnly = true }, "claude").Items.All(i => i.Requests is null && i.Model is null), "the menu's look-up reads only the head");
        Check(ResumableSessions.Filter(claude.Items, "NEW").Select(i => i.Title).SequenceEqual(["newer"]) && ResumableSessions.Filter(claude.Items, " ").Count == 2, "search keeps titles with every word, ignoring case");
        return Task.CompletedTask;
    }

    internal static Task ResumingContinuesTheSessionInANewPaneAndRemembersIt()
    {
        var item = new ResumableSession("codex", "5d9e2c1a-1111-4222-8333-444455556666", "a very long first request that goes on well past the forty character pane title", Base, 3, "gpt-5-codex", "/r");
        var pane = ResumableSessions.Apply(item, new RunSession { Kind = "claude", Provider = "claude", Title = "Claude", TitleMode = PaneTitle.Fixed });
        Check(pane.Provider == "codex" && pane.ResumeId == item.SessionID && pane.Title == PaneTitle.Shortened(item.Title) && pane.TitleMode == PaneTitle.Automatic, "the new pane resumes the session, titled after its first request, title still automatic");
        Check(ResumableSessions.Apply(item with { Title = null }, new RunSession { Title = "Codex" }).Title == "Codex" && ResumableSessions.RowTitle(item with { Title = null }) == Locale.Get("resume.untitled"), "an untitled session keeps the pane's name");
        Check(ResumableSessions.RelativeTime(Base, Base.AddSeconds(30)) == Locale.Get("resume.time.now")
            && ResumableSessions.RelativeTime(Base, Base.AddMinutes(5)) == Locale.Get("resume.time.minutes", new Dictionary<string, string> { ["count"] = "5" })
            && ResumableSessions.RelativeTime(Base, Base.AddHours(3)) == Locale.Get("resume.time.hours", new Dictionary<string, string> { ["count"] = "3" })
            && ResumableSessions.RelativeTime(Base, Base.AddDays(12)) == Locale.Get("resume.time.days", new Dictionary<string, string> { ["count"] = "12" }), "relative times use the shared locale keys");
        using var fixture = new Fixture();
        var path = Path.Combine(fixture.Root, "state", KnownSessionIDs.FileName);
        Check(KnownSessionIDs.Load(path).Count == 0, "no file, nothing known");
        var ids = KnownSessionIDs.Adding("abc", [])!;
        Check(KnownSessionIDs.Adding("ABC", ids) is null && KnownSessionIDs.Adding("../x", ids) is null, "an id is kept once, and only a valid id");
        var many = Enumerable.Range(0, KnownSessionIDs.Maximum).Select(i => "id" + i).ToList();
        var bounded = KnownSessionIDs.Adding("newest", many)!;
        Check(bounded.Count == KnownSessionIDs.Maximum && bounded[^1] == "newest" && bounded[0] == "id1", "past 2,000 the oldest goes");
        KnownSessionIDs.Save(bounded, path);
        Check(KnownSessionIDs.Load(path).SequenceEqual(bounded), "known-sessions.json reads back");
        Check(ResumableSessions.OneLine("one\n two   three") == "one two three" && ResumableSessions.OneLine(new string('x', 300)).Length == 201, "titles are one line of at most 200 characters");
        return Task.CompletedTask;
    }
}
