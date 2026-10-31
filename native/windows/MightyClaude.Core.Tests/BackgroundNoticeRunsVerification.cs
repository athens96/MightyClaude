using System.Text.Json;
using MightyClaude.Core;

/// <summary>
/// A notice turn is a run of its own (BackgroundNoticeRuns, macOS BackgroundNoticeRunsTests): the tracker opens it, the pane keeps
/// it flagged with its own lines, views draw background blocks only there, and it never takes the newest request's place.
/// </summary>
internal static class BackgroundNoticeRunsVerification
{
    private static void Check(bool value, string why) { if (!value) throw new InvalidOperationException(why); }
    private static void Same<T>(T actual, T expected, string why) { if (!EqualityComparer<T>.Default.Equals(actual, expected)) throw new InvalidOperationException($"{why}: expected <{expected}> but was <{actual}>"); }

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
    private static JsonElement Text(string value, string id) => Json(new { type = "assistant", uuid = id, message = new { id, content = new[] { new { type = "text", text = value } } } });
    private static JsonElement Command(string id, string command, string description) =>
        Json(new { type = "assistant", uuid = "call-" + id, message = new { id = "call-" + id, content = new object[] { new { type = "tool_use", id, name = "Bash", input = new { command, description, run_in_background = true } } } } });
    private static JsonElement Launched(string tool, string task) =>
        Json(new { type = "user", message = new { content = new object[] { new { type = "tool_result", tool_use_id = tool, content = $"Command running in background with ID: {task}. Output is being written to: /tmp/{task}.output" } } } });
    private static JsonElement Notification(string task, string summary) =>
        Json(new { type = "user", origin = new { kind = "task-notification" }, message = new { content = $"<task-notification>\n<task-id>{task}</task-id>\n<output-file>/tmp/{task}.output</output-file>\n<status>completed</status>\n<summary>{summary}</summary>\n</task-notification>" } });
    private static JsonElement Result(string value, bool notice = false) => notice
        ? Json(new { type = "result", subtype = "success", is_error = false, result = value, origin = new { kind = "task-notification" } })
        : Json(new { type = "result", subtype = "success", is_error = false, result = value });

    private static void InKorean(Action action)
    {
        var saved = Locale.LanguagePreference;
        try { Locale.LanguagePreference = "ko"; Locale.ResetCache(); action(); }
        finally { Locale.LanguagePreference = saved; Locale.ResetCache(); }
    }

    /// <summary>The pane as the run pipeline feeds it: the request's run and the notice runs the tracker built, then the lines.</summary>
    private static RunSession Hand(RunSession pane, ExecutionGraphTracker tracker)
    {
        if (tracker.BuildRun(includeRunning: true) is { } request) pane = pane.Apply(new RunEvent(pane.Id, "graph_run", GraphRun: request));
        foreach (var notice in tracker.TakeNoticeRuns(includeRunning: true)) pane = pane.Apply(new RunEvent(pane.Id, "graph_run", GraphRun: notice));
        return pane;
    }
    private static RunSession Line(RunSession pane, string id, string text) => pane.Apply(new RunEvent(pane.Id, "log", new LogEntry(id, "assistant", text, Wire.Now(), "claude")));

    internal static Task RunIDsAndTheFilter()
    {
        Same(BackgroundNoticeRuns.RunID("run-1", 2), "run-1:notice:2", "the id names the original and its number");
        Check(BackgroundNoticeRuns.IsNoticeRun("run-1:notice:2") && !BackgroundNoticeRuns.IsNoticeRun("run-1"), "a notice run is told by its id");
        Check(Wire.Identifier(BackgroundNoticeRuns.RunID(Guid.NewGuid().ToString(), 12)), "the id is a wire identifier");
        List<MightyGraphAgent> Agents() =>
        [
            new() { Id = "fg", Title = "Read" }, new() { Id = "bg", Title = "Build", Kind = "task", Background = true },
            new() { Id = "bg-child", ParentID = "bg", Title = "Inner" }, new() { Id = "agent", Title = "Survey", Background = true },
        ];
        Check(BackgroundNoticeRuns.ShownAgents(new MightyGraphRun { Id = "r", Agents = Agents() }).Select(a => a.Id).SequenceEqual(["fg"]), "a request draws no background block, nor what hangs under one");
        Check(BackgroundNoticeRuns.ShownAgents(new MightyGraphRun { Id = "r:notice:1", Agents = Agents(), NoticeRun = true }).Count == 4, "a notice run draws them all");
        // The flags survive saving; an older save reads them as absent; the shown count is never saved.
        var run = new MightyGraphRun { Id = "r:notice:1", NoticeRun = true, Agents = [new() { Id = "c", Kind = "task", Background = true, SourceBlockId = "graph-source" }] };
        var decoded = JsonSerializer.Deserialize<MightyGraphRun>(JsonSerializer.Serialize(run, Wire.Json), Wire.Json)!;
        Check(decoded.NoticeRun == true && decoded.Agents[0].SourceBlockId == "graph-source", "the flags survive a save");
        var legacy = JsonSerializer.Deserialize<MightyGraphRun>("""{"id":"r","input":"","status":"completed","rootEntries":[],"agents":[{"id":"a","title":"A","input":"","status":"completed","entries":[]}],"resultEntries":[]}""", Wire.Json)!;
        Check(legacy.NoticeRun is null && legacy.Agents[0].SourceBlockId is null, "an older save reads them as absent");
        var budget = 1_000_000;
        Check(MightyGraphSupport.Normalized([run], true, ref budget)[0] is { NoticeRun: true } normalized && normalized.Agents[0].SourceBlockId == "graph-source", "normalizing keeps them");
        var node = new ExecutionGraphNode("n", "r:notice:1", null, "task", "completed", "T") { SourceBlockId = "graph-source" };
        Check(JsonSerializer.Deserialize<ExecutionGraphNode>(JsonSerializer.Serialize(node, Wire.Json), Wire.Json)!.SourceBlockId == "graph-source", "a node keeps its source");
        return Task.CompletedTask;
    }

    /// <summary>A request whose background command still runs, a notice run taken into its turn (no reply), and one with a reply.</summary>
    private static List<MightyGraphRun> Runs(bool answered = true, string requestStatus = "completed")
    {
        MightyGraphAgent Task(string id, string status) => new() { Id = id, Title = "Build", Input = "npm run build", Status = status, Kind = "task", Background = true, SourceBlockId = id == "t" ? null : "t" };
        var request = new MightyGraphRun { Id = "r", Input = "build it", Status = requestStatus, Agents = [new() { Id = "fg", Title = "Read", Status = "completed" }, Task("t", "running")], FinalOutput = requestStatus == "completed" ? "Started." : null };
        MightyGraphSupport.RefreshResult(request);
        var quiet = new MightyGraphRun { Id = "r:notice:1", Input = "Build · 완료", Status = "completed", Agents = [Task("r:notice:1:t", "completed")], NoticeRun = true };
        MightyGraphSupport.RefreshResult(quiet);
        var reply = new MightyGraphRun { Id = "r:notice:2", Input = "Build · 완료", Status = answered ? "completed" : "running", Agents = [Task("r:notice:2:t", "completed")], FinalOutput = answered ? "The build passed." : null, NoticeRun = true };
        MightyGraphSupport.RefreshResult(reply);
        return [request, quiet, reply];
    }
    private static string Node(string run, string suffix) => MightyGraphBlockSize.NodeId(run, suffix);

    internal static Task TheRunRulesAndTheViews()
    {
        var all = Runs();
        Same(MightyGraphSupport.NewestRequestIndex(all), 0, "the newest request");
        Check(!all[0].QuietNotice && all[1].QuietNotice && !all[2].QuietNotice, "only the absorbed notice run is quiet");
        Same(MightyGraphSupport.FollowRunIndex(all), 2, "an answered notice run is followed");
        Same(MightyGraphSupport.FollowRunIndex(Runs(false)), 2, "a running notice run is followed");
        Same(MightyGraphSupport.FollowRunIndex(all.Take(2).ToList()), 0, "a quiet one never is");
        List<MightyGraphRun> next = [.. all, new() { Id = "n", Status = "starting" }];
        Check(MightyGraphSupport.NewestRequestIndex(next) == 3 && MightyGraphSupport.FollowRunIndex(next) == 3, "a request after them is the newest again");
        Check(MightyGraphSupport.RequestOrdinals(next).SequenceEqual([1, 1, 1, 2]) && MightyGraphSupport.RequestOrdinals([all[1], all[0]]).SequenceEqual([1, 1]), "notice runs take no number");
        Check(MightyGraphSupport.NewestRequestIndex([all[1]]) is null, "notice runs alone hold no request");
        // The request's result shows as its own turn ends; its background command runs on in the strip and it still says it waits on it.
        Check(all[0].Settled && all[0].ResultEntries.FirstOrDefault()?.Text == "Started.", "the request's result does not wait for hidden work");
        var shown = MightyGraphSupport.ShownRuns(all);
        Check(shown[0].Agents.Select(a => a.Id).SequenceEqual(["fg"]) && all[0].Agents.Count == 2 && MightyGraphSupport.BackgroundWaiting(shown[0]) == 1, "views drop the block and keep its count");
        Check(!JsonSerializer.Serialize(shown[0], Wire.Json).Contains("BackgroundWaiting", StringComparison.OrdinalIgnoreCase), "the count is never saved");
        Check(MightyGraphSupport.BackgroundWaiting(MightyGraphSupport.ShownRuns(Runs(requestStatus: "running"))[0]) == 0, "a running request waits on nothing yet");
        // The diagram: the quiet run has no result card, the answered one does, and the latest result is never the empty card.
        var layout = MightyGraphLayout.Make(all, "", false, new HashSet<string>(), splitsExecution: true);
        var ids = layout.Nodes.Select(n => n.Id).ToHashSet();
        Check(ids.Contains(Node("r", "result")) && !ids.Contains(Node("r", "agent:t")), "the request draws its result, not its background block");
        Check(!ids.Contains(Node("r:notice:1", "result")) && ids.Contains(Node("r:notice:1", "agent:r:notice:1:t")) && ids.Contains(Node("r:notice:2", "result")), "a quiet notice run has its copy and no result card");
        Same(MightyGraphLayout.LatestResultID(all), Node("r:notice:2", "result"), "the latest result");
        Same(MightyGraphLayout.LatestResultID(all.Take(2).ToList()), Node("r", "result"), "never the quiet run's");
        Check(MightyGraphLayout.FilesPanelOpen(all.Take(2).ToList(), "r"), "the request's files panel stays open beside its card");
        Same(MightyGraphFollow.TargetID(layout, all), Node("r:notice:2", "result"), "a following camera moves on to the answered notice run");
        var running = Runs(requestStatus: "running").Take(2).ToList();
        var live = MightyGraphLayout.Make(running, "", true, new HashSet<string>(), splitsExecution: true);
        Same(MightyGraphFollow.TargetID(live, running), Node("r", "agent:fg"), "a quiet notice run leaves the camera on the request");
        Same(live.NewestRequestTargetID(running), Node("r", MightyGraphLayout.WorkSuffix), "the newest request's target");
        // 기록 numbers the request alone, titles notice runs as such and opens the newest request and answered notice runs after it.
        var groups = MightyTimeline.Groups(all);
        Check(groups.Select(g => g.Ordinal).SequenceEqual([1, 1, 1]) && groups.Select(g => g.NoticeRun).SequenceEqual([false, true, true]), "기록's numbers and flags");
        Check(groups[1].Result is null && groups[2].Result?.Text == "The build passed.", "기록 gives the quiet run no result card");
        Check(groups[0].Rows.Select(r => r.NodeId).SequenceEqual([Node("r", "request"), Node("r", MightyGraphLayout.WorkSuffix), Node("r", "agent:fg")]), "the request's rows leave its background block out");
        // A notice run saved without a title of its own (an older save) is headed generically.
        Same(groups[1].Rows[0].Title, BackgroundNoticeRuns.GenericTitle, "a notice run's request row");
        Check(groups[1].NoticeTitle is null && groups[0].NoticeTitle is null, "neither a request nor an untitled notice run carries a title");
        Same(groups[1].Rows[1].Title, Locale.Get("graph.block.execution"), "a notice run's 실행 row has no request number");
        var none = new HashSet<string>();
        Check(MightyTimeline.GroupOpen(groups[0], all, none) && !MightyTimeline.GroupOpen(groups[1], all, none) && MightyTimeline.GroupOpen(groups[2], all, none), "기록 opens the request and the answered notice run");
        // The pipeline: numbered alike, the request's background block left out, never moving on to a quiet board.
        var rows = MightyStructure.Make(all, null, null).Rows!;
        Check(rows.Select(r => r.Item.Ordinal).SequenceEqual([1, 1, 1]) && rows.Select(r => r.Item.NoticeRun).SequenceEqual([false, true, true]), "pipeline cards' numbers and flags");
        Check(rows[0].Item.Clusters.SelectMany(c => c.Nodes).Select(n => n.AgentId).SequenceEqual(["fg"]) && rows[1].Item.Clusters.SelectMany(c => c.Nodes).Select(n => n.AgentId).SequenceEqual(["r:notice:1:t"]), "pipeline clusters");
        var boards = MightyStructure.Boards(MightyStructure.Make(all.Take(2).ToList(), null, null));
        Check(boards.Select(b => b.Quiet).SequenceEqual([false, true]), "the quiet board is marked");
        var navigation = new MightyStructure.Navigation();
        Check(navigation.Index(boards) == 0 && navigation.OnNewest(boards), "the view stays on the request's board");
        navigation.Step(1, boards);
        Check(navigation.Index(boards) == 1 && !navigation.OnNewest(boards), "the quiet board can be stepped to");
        navigation.ShowNewest(boards);
        Same(navigation.Index(boards), 0, "the newest board is the request's");
        // A failed notice turn keeps its card; a reopened one keeps its earlier reply; a first one still waiting has none.
        var failed = new MightyGraphRun { Id = "r:notice:1", Status = "error", NoticeRun = true };
        Check(!failed.QuietNotice && MightyGraphLayout.HasResult(failed) && MightyGraphSupport.FollowRunIndex([all[0], failed]) == 1, "a failed notice turn is not quiet");
        var reopened = new MightyGraphRun { Id = "r:notice:1", Status = "running", FinalOutput = "The build passed.", NoticeRun = true };
        MightyGraphSupport.RefreshResult(reopened);
        Check(reopened.ReopenedWithReply && MightyGraphLayout.HasResult(reopened) && reopened.ResultEntries.FirstOrDefault()?.Text == "The build passed.", "a reopened notice run keeps its reply");
        // The reopened turn failed or was stopped: the card keeps the earlier reply; a request's error result still shows none.
        foreach (var status in new[] { "error", "stopped" })
        {
            reopened.Status = status;
            MightyGraphSupport.RefreshResult(reopened);
            Check(MightyGraphLayout.HasResult(reopened) && reopened.ResultEntries.FirstOrDefault()?.Text == "The build passed.", $"a notice run whose reopened turn ended {status} keeps its reply");
        }
        var failedRequest = new MightyGraphRun { Id = "r", Status = "error", FinalOutput = "Started." };
        MightyGraphSupport.RefreshResult(failedRequest);
        Check(failedRequest.ResultEntries.Count == 0, "a request's error result shows none");
        var opening = new MightyGraphRun { Id = "r:notice:2", Status = "running", NoticeRun = true };
        MightyGraphSupport.RefreshResult(opening);
        Check(!MightyGraphLayout.HasResult(opening) && opening.ResultEntries.Count == 0, "a first notice turn still waiting has no card");
        // The phone draws no background block in the request that started it: its steps wake no long poll there, the notice run's do.
        var digested = Runs()[0];
        var before = MobileMightySupport.Digest([digested]);
        digested.Agents[1].Entries.Add(new LogEntry("b1", "assistant", "compiling", "2026-10-28T00:00:00Z", "claude"));
        Same(MobileMightySupport.Digest([digested]), before, "a request's hidden background block does not move the digest");
        // Its end moves the request's waiting count (현재까지의 결과 → 최종 결과), which the phone draws.
        digested.Agents[1].Status = "completed";
        var ended = MobileMightySupport.Digest([digested]);
        Check(ended != before, "the end of the request's background work moves the digest");
        digested.Agents.Add(new() { Id = "fg2", Title = "Grep", Status = "running" });
        Check(MobileMightySupport.Digest([digested]) != ended, "a new block the request draws moves the digest");
        var noticeRun = Runs()[2];
        var noticeBefore = MobileMightySupport.Digest([noticeRun]);
        noticeRun.Agents[0].Entries.Add(new LogEntry("c1", "assistant", "done", "2026-10-28T00:00:00Z", "claude"));
        Check(MobileMightySupport.Digest([noticeRun]) != noticeBefore, "a notice run's background block moves the digest");
        return Task.CompletedTask;
    }

    /// <summary>The run pipeline hands the pane the request's run and the notice run; the notice turn's lines are its own, a request
    /// keeps its own answer, and the phone gets the notice run flagged with its reply and no request number.</summary>
    internal static Task ThePaneKeepsTheNoticeRunApart() { InKorean(PaneKeepsTheNoticeRunApart); return Task.CompletedTask; }
    private static void PaneKeepsTheNoticeRunApart()
    {
        var pane = new RunSession { Id = "pane", Kind = "claude", Provider = "claude", Status = "running", Logs = [new("ask", "user", "build it", Wire.Now(), "claude")] };
        var tracker = new ExecutionGraphTracker("proc-1", "build it", "claude", null, _ => { });
        foreach (var value in new[] { Command("build", "npm run build", "Build the app"), Launched("build", "b1"), Text("Building.", "m1") }) tracker.Consume(value);
        pane = Hand(pane, tracker);
        pane = Line(pane, "line-1", "Building.");
        tracker.Consume(Result("Building."));
        pane = Hand(pane, tracker) with { BackgroundWork = new BackgroundWork([new("b1", "shell", "Build the app", Wire.Now(), "running", "build")], TurnEnded: true) };
        var revision = tracker.NoticeRevision;
        tracker.Consume(Notification("b1", "Build done"));
        Check(tracker.NoticeRevision != revision, "a notice run's opening moves the revision, so the pipeline hands it over before the turn's lines");
        pane = Hand(pane, tracker);
        pane = Line(pane, "line-2", "The build passed.");
        tracker.Consume(Text("The build passed.", "m2"));
        tracker.Consume(Result("The build passed.", notice: true));
        pane = Hand(pane, tracker);
        var runs = pane.GraphRuns!;
        Check(runs.Select(r => r.Id).SequenceEqual(["proc-1", "proc-1:notice:1"]), "the request, then its notice run: " + string.Join(",", runs.Select(r => r.Id)));
        var (request, notice) = (runs[0], runs[1]);
        Check(request.NoticeRun is null && request.FinalOutput == "Building." && request.Agents.Count == 1 && request.Agents[0].Background == true, "the request keeps its answer and its block as data");
        Check(notice is { NoticeRun: true, Status: "completed", FinalOutput: "The build passed." } && notice.Input == "Build the app · 완료", "the notice run: " + notice.Input + " / " + notice.FinalOutput);
        // The projection carries the main block's title, named after the finished work; a request has none.
        Check(notice.NoticeTitle == "백그라운드 작업 완료 · Build the app" && request.NoticeTitle is null && BackgroundNoticeRuns.Heading(notice) == notice.NoticeTitle, "the notice run's title: " + notice.NoticeTitle);
        var copy = notice.Agents.Single();
        Check(copy.SourceBlockId == request.Agents[0].Id && copy is { Kind: "task", Background: true, Title: "Build the app", Input: "npm run build", Status: "completed", OutputFile: "/tmp/b1.output" }, "the copy is the block as it ended");
        Check(notice.RootEntries.Any(e => e.Text == "The build passed.") && !request.RootEntries.Any(e => e.Text == "The build passed."), "the notice turn's lines are its own");
        Check(MightyGraphSupport.NewestRequestRun(runs)?.Id == "proc-1", "the notice run never takes the request's place");
        // The phone: the notice run travels flagged, titled after the work in its title and its main block, with its reply; the request
        // keeps its number and its result, and waits on nothing any more.
        var mobile = MobileMightySupport.Runs(runs);
        Check(mobile[0].NoticeRun is null && mobile[1].NoticeRun == true && mobile[1].Blocks[0].Title == notice.NoticeTitle && mobile[1].Title == notice.NoticeTitle, "the phone's notice run");
        Check(mobile[0].BackgroundWaiting is null && mobile[1].BackgroundWaiting is null, "nothing waits once the build ended");
        Check(mobile[0].Blocks.Count == 1 && mobile[1].Blocks.Count == 2, "the request's background block travels only in the notice run");
        Check(mobile[0].Result == "Building." && mobile[1].Result == "The build passed.", "the newest request and the notice run after it carry their results");
        Check(JsonSerializer.Serialize(mobile, Wire.Json).Contains("\"noticeRun\":true", StringComparison.Ordinal), "the flag reaches the wire");
        // The strip and the phone's backgroundWork: a running task meets its block, with its command and output lines, never its path.
        var work = new BackgroundWork([new("b2", "shell", "Lint", Wire.Now(), "running", "lint"), new("o1", "other", "Something", Wire.Now(), "running")], TurnEnded: true);
        var linted = new MightyGraphRun { Id = "proc-2", SourceRunID = "proc-2", Agents = [new() { Id = ExecutionGraphSupport.AgentNodeID("proc-2", "lint"), Title = "Lint", Input = "npm run lint", Status = "running", Kind = "task", Background = true, OutputFile = "/tmp/b2.output" }] };
        var output = new Dictionary<string, IReadOnlyList<string>> { [linted.Agents[0].Id] = ["1", "2", "3", "4", "5", "6", "7"] };
        var rows = PlanCardSupport.BackgroundStripRows(work, linted, output);
        var row = rows.First(r => r.Id == "b2");
        Check(row is { Run: true, Command: "npm run lint", Opens: true } && row.TailLines!.SequenceEqual(["2", "3", "4", "5", "6", "7"]) && row.Tail == "7", "a strip row opens to its command and last six lines");
        var phone = PlanCardSupport.MobileBackgroundWork(work, linted, output)!;
        Check(phone.Count == 1 && phone[0] is { Id: "b2", Kind: "run", Status: "running", Command: "npm run lint" } && phone[0].OutputTail!.Count == 6, "the phone gets the running command, its lines, and no other kind");
        Check(!JsonSerializer.Serialize(phone, Wire.Json).Contains("/tmp/", StringComparison.Ordinal), "no path travels");
        Check(PlanCardSupport.MobileBackgroundWork(new BackgroundWork([new("b3", "shell", "Done", Wire.Now(), "completed")]), linted) is null, "nothing running: no backgroundWork");
        Check(PlanCardSupport.MobileBackgroundDone(work) == 0 && PlanCardSupport.MobileBackgroundDone(new BackgroundWork([new("b3", "shell", "Done", Wire.Now(), "completed")], TurnEnded: true)) == 1,
            "the phone's backgroundDone: 0 while all runs, the finished count once some ended");
    }

    /// <summary>A notice run without a title of its own (an older save) reads the generic heading, one with a title reads it; the title is
    /// saved with the run, an older save reads none, and a request run keeps none through normalizing
    /// (macOS BackgroundNoticeRunsTests.theHeadingIsTheRunsTitleOrTheGenericOne, theNoticeTitleSurvivesCodingAndOldSavesReadNil).</summary>
    internal static Task TheHeadingIsTheRunsTitleOrTheGenericOne() { InKorean(HeadingAndSaves); return Task.CompletedTask; }
    private static void HeadingAndSaves()
    {
        Same(BackgroundNoticeRuns.GenericTitle, Locale.Get("graph.background.noticeRun"), "the generic title");
        Same(BackgroundNoticeRuns.Heading(new MightyGraphRun { Id = "r:notice:1", NoticeRun = true }), BackgroundNoticeRuns.GenericTitle, "an untitled notice run");
        Same(BackgroundNoticeRuns.Heading(new MightyGraphRun { Id = "r:notice:1", NoticeRun = true, NoticeTitle = "  \n" }), BackgroundNoticeRuns.GenericTitle, "a blank title");
        Same(BackgroundNoticeRuns.Heading(new MightyGraphRun { Id = "r:notice:1", NoticeRun = true, NoticeTitle = "하위 에이전트 메시지 · scout" }), "하위 에이전트 메시지 · scout", "a titled notice run");
        // A title a view carries reads the same way, trimmed.
        Same(BackgroundNoticeRuns.Heading((string?)null), BackgroundNoticeRuns.GenericTitle, "no carried title");
        Same(BackgroundNoticeRuns.Heading(" \n"), BackgroundNoticeRuns.GenericTitle, "a blank carried title");
        Same(BackgroundNoticeRuns.Heading(" 위임 알림 \n"), "위임 알림", "a padded carried title");
        var run = new MightyGraphRun { Id = "r:notice:1", Status = "completed", NoticeRun = true, NoticeTitle = "백그라운드 작업 완료 · Build" };
        var decoded = JsonSerializer.Deserialize<MightyGraphRun>(JsonSerializer.Serialize(run, Wire.Json), Wire.Json)!;
        Same(decoded.NoticeTitle, "백그라운드 작업 완료 · Build", "the title survives a save");
        var old = JsonSerializer.Deserialize<MightyGraphRun>("""{"id":"r:notice:1","input":"","status":"completed","rootEntries":[],"agents":[],"resultEntries":[],"noticeRun":true}""", Wire.Json)!;
        Check(old.NoticeTitle is null, "an older save reads no title");
        var budget = 1_000_000;
        var normalized = MightyGraphSupport.Normalized([new MightyGraphRun { Id = "r", Status = "completed", NoticeTitle = "stray" }, run], true, ref budget);
        Check(normalized.Select(r => r.NoticeTitle).SequenceEqual([null, "백그라운드 작업 완료 · Build"]), "normalizing keeps a notice run's title and drops a request's");
        Check(run.Copy().NoticeTitle == run.NoticeTitle && run.Shown.NoticeTitle == run.NoticeTitle, "a copy and the shown run keep it");
    }

    /// <summary>A request whose background command still runs, or ended with its notice turn not answered yet, awaits notices: its result is the
    /// 알림 대기 block (titled 알림 대기 중, waiting, with the command's row and "알림 n/m") in the diagram, 기록, the pipeline and on the phone, and the
    /// pane reads 알림 대기; once the notice turn is answered it is 최종 결과 again. A failed or stopped request awaits nothing. The phone still gets the
    /// running count for older phones (macOS BackgroundNoticeRunsTests.aRequestAwaitingNoticesIsTheAwaitingNoticeBlock, MobileRemoteExtensionTests,
    /// MobileRemoteStructureTests.anItemCarriesTheWaitingCountAndANoticeRunsTitle).</summary>
    internal static Task ARequestAwaitingNoticesIsTheAwaitingNoticeBlock() { InKorean(AwaitingNotices); return Task.CompletedTask; }
    private static void AwaitingNotices()
    {
        var all = Runs(answered: false);
        Same(BackgroundNoticeRuns.OriginalRunID("r:notice:2"), "r", "a notice run names its request"); Check(BackgroundNoticeRuns.OriginalRunID("r") is null, "a request names none");
        var awaiting = BackgroundNoticeRuns.AwaitingNotice(all[0], all);
        Check(awaiting is { Delivered: 0 } && awaiting.Tasks.Single() is { Id: "t", Title: "Build", Status: "running" }, "the running command is awaited");
        Same(awaiting!.Tasks[0].Word, "끝나면 알림", "its row's word"); Same(awaiting.Progress, "알림 0/1", "the progress line");
        Same(MightyGraphBlockModel.ResultTitle("completed", true), "알림 대기 중", "the title while awaiting");
        Same(MightyGraphBlockModel.ResultTitle("completed"), "최종 결과", "not awaiting is the final result");
        Same(MightyGraphBlockModel.ResultTitle("error", true), Locale.Get("graph.block.resultError"), "a failed request keeps its title");
        // The diagram's result block is the 알림 대기 block; the shown run carries what it read of the notice runs.
        MightyGraphBlock Card()
        {
            var layout = MightyGraphLayout.Make(all, "", false, new HashSet<string>(), splitsExecution: true);
            return MightyGraphBlockModel.Blocks(layout, all, "", "Claude", true).First(b => b.Id == Node("r", "result"));
        }
        var card = Card();
        Check(card is { Kind: "result", Title: "알림 대기 중", Status: "waiting", Outline: MightyGraphActivity.Marching } && card.Awaiting == awaiting && card.State == "알림 대기"
            && MightyGraphBlockModel.Tone(card) == DesignTone.Wait, "the diagram's 알림 대기 block: " + card);
        Check(BackgroundNoticeRuns.AwaitingNotice(MightyGraphSupport.ShownRuns(all)[0], []) == awaiting, "a shown run carries its awaiting");
        Check(MightyTimeline.Groups(all)[0].Result is { Awaiting: true, Tone: DesignTone.Wait }, "기록's result head waits");
        Check(MightyStructure.Make(all, null, null).Rows!.Select(r => r.Item.Awaiting).SequenceEqual([true, false, false]), "the pipeline card awaits");
        DesignTone? ResultColumn() { var board = MightyStructure.Boards(MightyStructure.Make(all, null, null))[0]; return MightyStructure.Tone.OfCard(board.Columns[2].Cards[0], board.Columns[2]); }
        Same(ResultColumn(), DesignTone.Wait, "the pipeline's 결과 waits");
        // The phone: the block's rows, and the running count for an older phone.
        var mobile = MobileMightySupport.Runs(all);
        Check(mobile[0].AwaitingNotice is { Delivered: 0, Total: 1 } wire && wire.Tasks.Single() == new MobileAwaitedTask("Build", "running") && mobile[0].BackgroundWaiting == 1
            && mobile[1].AwaitingNotice is null && mobile[2].AwaitingNotice is null, "the phone gets the 알림 대기 block");
        Check(JsonSerializer.Serialize(mobile[0], Wire.Json).Contains("\"awaitingNotice\":{\"delivered\":0,\"total\":1,\"tasks\":[{\"title\":\"Build\",\"status\":\"running\"}]}", StringComparison.Ordinal)
            && !JsonSerializer.Serialize(mobile[1], Wire.Json).Contains("awaitingNotice", StringComparison.Ordinal), "the wire form: " + JsonSerializer.Serialize(mobile[0], Wire.Json));
        var structure = MobileStructure.Make(MightyStructure.Make(all, null, null), all.Count)!.Rows!.Select(r => r.Item).ToList();
        Check(structure.Select(i => i.AwaitingNotice).SequenceEqual([true, (bool?)null, null]) && structure.Select(i => i.BackgroundWaiting).SequenceEqual([1, (int?)null, null]), "the phone's pipeline awaits");
        // The pane is alive only for the notice: the pet and the header say 알림 대기.
        var pane = new RunSession { Id = "p", Kind = "claude", Provider = "claude", Status = "running", GraphRuns = all };
        Check(BackgroundNoticeRuns.AwaitsNotice(pane) && !BackgroundNoticeRuns.AwaitsNotice(pane with { Status = "completed" }) && BackgroundNoticeRuns.StateWord == "알림 대기", "the pane awaits notices");
        // The command ended and its notice turn runs: still awaiting, its notice delivered.
        var digest = MobileMightySupport.Digest(all);
        all[0].Agents[1].Status = "completed";
        Check(BackgroundNoticeRuns.AwaitingNotice(all[0], all) is { Delivered: 1, Progress: "알림 1/1" } delivered && delivered.Tasks[0].Word == "알림 전달됨"
            && MightyGraphSupport.BackgroundWaiting(all[0]) == 0 && Card().Title == "알림 대기 중", "an unanswered notice turn keeps it awaiting");
        Check(MobileMightySupport.Runs(all)[0] is { BackgroundWaiting: null, AwaitingNotice.Delivered: 1 } && MobileMightySupport.Digest(all) != digest, "the phone sees the notice delivered");
        // The notice turn answered: the normal 최종 결과.
        digest = MobileMightySupport.Digest(all);
        all[2].Status = "completed"; all[2].FinalOutput = "The build passed.";
        Check(BackgroundNoticeRuns.AwaitingNotice(all[0], all) is null && Card() is { Title: "최종 결과", Status: "completed", Awaiting: null, Outline: MightyGraphActivity.None }
            && MightyTimeline.Groups(all)[0].Result is { Awaiting: false, Tone: DesignTone.Done } && !BackgroundNoticeRuns.AwaitsNotice(pane) && ResultColumn() == DesignTone.Done, "answered, it is the final result");
        Check(MobileMightySupport.Runs(all)[0].AwaitingNotice is null && MobileMightySupport.Digest(all) != digest, "the phone's block goes and its digest moves");
        // Failed or stopped work keeps its word; a failed or stopped request, or a notice run, awaits nothing.
        all[0].Agents[1].Status = "error"; all[2].Status = "running";
        Same(BackgroundNoticeRuns.AwaitingNotice(all[0], all)!.Tasks[0].Word, "실패", "a failed command's word");
        all[0].Agents[1].Status = "stopped";
        Same(BackgroundNoticeRuns.AwaitingNotice(all[0], all)!.Tasks[0].Word, "중단됨", "a stopped command's word");
        Check(BackgroundNoticeRuns.AwaitingNotice(all[2], all) is null, "a notice run awaits nothing");
        foreach (var status in new[] { "error", "stopped", "running" })
        {
            var other = Runs(answered: false, requestStatus: status);
            Check(BackgroundNoticeRuns.AwaitingNotice(other[0], other) is null, "a " + status + " request awaits nothing");
        }
        // A notice run of another request never keeps this one awaiting.
        all[0].Agents[1].Status = "completed";
        Check(BackgroundNoticeRuns.AwaitingNotice(all[0], [all[0], new MightyGraphRun { Id = "other:notice:1", Status = "running", NoticeRun = true, Agents = [new() { Id = "c", SourceBlockId = "t" }] }]) is null, "another request's notice run");
        // A notice run's title moves the digest with no new record.
        var notice = new MightyGraphRun { Id = "r:notice:1", Status = "running", NoticeRun = true };
        var untitled = MobileMightySupport.Digest([notice]);
        notice.NoticeTitle = "하위 에이전트 메시지 · scout";
        Check(MobileMightySupport.Digest([notice]) != untitled, "a notice run's title moves the digest");
        // An untitled notice run (an older save) is headed generically on the phone; a titled one carries its own.
        var named = Runs(); var old = MobileMightySupport.Runs(named);
        Check(old[1].Title == BackgroundNoticeRuns.GenericTitle && old[1].Blocks[0].Title == BackgroundNoticeRuns.GenericTitle, "an untitled notice run on the phone");
        named[2].NoticeTitle = "백그라운드 작업 완료 · npm run build";
        var titled = MobileMightySupport.Runs(named);
        Check(titled[2].Title == "백그라운드 작업 완료 · npm run build" && titled[2].Blocks[0].Title == "백그라운드 작업 완료 · npm run build", "a titled notice run on the phone");
        var items = MobileStructure.Make(MightyStructure.Make(named, null, null), named.Count, title: _ => "style name")!.Rows!.Select(r => r.Item).ToList();
        Check(items.Select(i => i.Title).SequenceEqual(["style name", null, "백그라운드 작업 완료 · npm run build"]), "the phone's pipeline titles: " + string.Join(" | ", items.Select(i => i.Title)));
        // A pane without a saved graph digests as its legacy runs do.
        var legacy = new RunSession { Id = "legacy", Kind = "claude", Provider = "claude", Status = "idle", Logs = [new("u1", "user", "hi", Wire.Now(), "claude"), new("a1", "assistant", "hello", Wire.Now(), "claude")] };
        Same(MobileMightySupport.Digest(legacy), MobileMightySupport.Digest(MobileMightySupport.LegacyRuns(legacy)), "the legacy digest hashes what the run digest does");
    }

    /// <summary>A notice taken into the request's own turn: its finished notice run comes last while the request still runs, yet the
    /// request stays the newest, and its later lines are its own. A process ending in a notice turn ends that run.</summary>
    internal static Task AbsorbedAndEndedNoticeRunsLeaveTheRequestItsPlace() { InKorean(AbsorbedAndEnded); return Task.CompletedTask; }
    private static void AbsorbedAndEnded()
    {
        var pane = new RunSession { Id = "pane", Kind = "claude", Provider = "claude", Status = "running", Logs = [new("ask", "user", "build it", Wire.Now(), "claude")] };
        var tracker = new ExecutionGraphTracker("proc-1", "build it", "claude", null, _ => { });
        foreach (var value in new[] { Command("build", "npm run build", "Build the app"), Launched("build", "b1"), Notification("b1", "Build done") }) tracker.Consume(value);
        pane = Hand(pane, tracker);
        var runs = pane.GraphRuns!;
        Check(runs.Select(r => r.Id).SequenceEqual(["proc-1", "proc-1:notice:1"]) && runs[1].Status == "completed" && runs[1].QuietNotice, "the absorbed notice run is finished and quiet");
        Check(MightyGraphSupport.NewestRequestRun(runs)?.Id == "proc-1" && MightyGraphSupport.WithoutNoticeRuns(pane).GraphRuns!.Count == 1, "the request stays the newest");
        pane = Line(pane, "line-1", "Built and done.");
        Check(pane.GraphRuns![0].RootEntries.Any(e => e.Text == "Built and done.") && pane.GraphRuns[1].RootEntries.Count == 0, "the request's later lines are its own");
        // A process that ends in a notice turn ends its run, which then takes no more lines.
        var second = new ExecutionGraphTracker("proc-2", "lint it", "claude", null, _ => { });
        foreach (var value in new[] { Command("lint", "npm run lint", "Lint"), Launched("lint", "l1"), Result("Linting."), Notification("l1", "Lint done") }) second.Consume(value);
        var later = Hand(new RunSession { Id = "p2", Kind = "claude", Provider = "claude", Status = "running" }, second);
        Same(later.GraphRuns![1].Status, "running", "the notice turn runs");
        later = Line(later, "l-1", "Linting the rest.");
        Check(later.GraphRuns![1].RootEntries.Any(e => e.Text == "Linting the rest."), "a running notice turn takes the lines");
        second.Finish("stopped");
        foreach (var notice in second.TakeNoticeRuns()) later = later.Apply(new RunEvent(later.Id, "graph_run", GraphRun: notice));
        Check(later.GraphRuns![1] is { Status: "stopped", QuietNotice: false }, "the process's end stops the notice run");
        Same(MightyGraphSupport.RoutedIndex(later.GraphRuns!), 0, "an ended notice run takes no more lines");
    }
}
