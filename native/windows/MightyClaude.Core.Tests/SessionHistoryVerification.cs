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
        try { SessionHistory.Load(new SessionHistoryRequest("gemini", id, fixture.Workspace) { Home = fixture.Home }); Check(false, "gemini must be unsupported"); }
        catch (SessionHistoryException ex) { Check(ex.Error == SessionHistoryError.Unsupported, "the app does not read Gemini records"); }
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
        Check(ResumableSessions.Listing(query, "gemini").Items.Count == 0 && !ResumableSessions.OffersResume("claude", "gemini") && ResumableSessions.OffersResume("claude", "codex") && !ResumableSessions.OffersResume("shell", "claude"), "Gemini, terminals and browsers never ask");
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
