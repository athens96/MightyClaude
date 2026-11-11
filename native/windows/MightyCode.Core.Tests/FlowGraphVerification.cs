using System.Text.Json;
using MightyCode.Core;

/// <summary>
/// The role view's session log, the advisor's band and the words the web page gets, checked against the cases of the macOS suite: FlowLogTests (the
/// last events of the newest activity with their time, who and what, the status line), FlowAdvisorTests (the band's words, where the layout puts it and
/// its links, what it gives the log) and the page's copy of all of it (Core FlowCopy). The shared recordings are replayed through the stream parser and
/// the tracker as a run feeds them (RunManager), so the sub-agents' tool rows reach their blocks as they do in the app.
/// </summary>
internal static class FlowGraphVerification
{
    private static void Check(bool value, string why) => FlowFixtures.Check(value, why);
    private static void Same<T>(T actual, T expected, string why) => FlowFixtures.Same(actual, expected, why);
    private static void SameList<T>(IEnumerable<T> actual, IEnumerable<T> expected, string why) => FlowFixtures.SameList(actual, expected, why);
    private static string Node(MightyGraphRun run, string suffix) => FlowFixtures.Node(run, suffix);
    private static MightyGraphLayout.Node Must(MightyGraphLayout layout, string id) => FlowFixtures.Must(layout, id);
    private static string Stamp(int seconds) => FlowFixtures.Stamp(seconds);
    private static DateTimeOffset Date(int seconds) => FlowFixtures.Date(seconds);

    /// <summary>A shared recording's lines (native/contracts/fixtures/&lt;name&gt;.jsonl).</summary>
    internal static List<string> Lines(string name)
    {
        var resource = "MightyCode.Core.Tests." + name + ".jsonl";
        using var stream = typeof(FlowGraphVerification).Assembly.GetManifestResourceStream(resource) ?? throw new InvalidOperationException(resource + " is not embedded");
        using var reader = new StreamReader(stream);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line) if (line.Trim().Length > 0) lines.Add(line);
        return lines;
    }

    /// <summary>A recording replayed as a run reads it: each frame to the tracker, then to the stream parser, whose tool rows the tracker routes to their
    /// blocks; the pane's runs as the run pipeline hands them over once the process ended.</summary>
    internal static List<MightyGraphRun> ReplayParsed(string fixture, string runID)
    {
        var tracker = new ExecutionGraphTracker(runID, "probe", "claude", null, _ => { });
        var parser = new OutputParser("claude", (_, _) => { }, _ => { }, activityNamespace: runID, graphActivity: (value, tool) => tracker.Activity(value, tool));
        foreach (var line in Lines(fixture))
        {
            tracker.Consume(JsonDocument.Parse(line).RootElement);
            parser.Parse(line);
        }
        parser.Flush();
        tracker.Finish("completed");
        return [.. new[] { tracker.BuildRun() }.OfType<MightyGraphRun>(), .. tracker.BuildNoticeRuns()];
    }

    private static MightyGraphAgent Agent(string id, string? parent = null, string status = "running", bool background = false, string? kind = null, string? source = null,
        List<LogEntry>? entries = null, int? started = null, int? ended = null, string? type = null, string? resolved = null) =>
        FlowFixtures.Agent(id, parent, status, background, kind, source, entries: entries, identity: new GraphAgentIdentity(type, null, resolved),
            figures: new GraphAgentFigures(started is { } s ? Stamp(s) : null, ended is { } e ? Stamp(e) : null));
    private static List<string> Words(IEnumerable<FlowLog.Row> rows) => rows.Select(r => r.Who + " | " + r.What).ToList();

    // ── FlowLogTests ──

    internal static Task TheLogsRowsAreWhatTheAppSawHappen()
    {
        FlowFixtures.Korean(() =>
        {
            var root = new List<LogEntry> { FlowFixtures.Said("t1", "Looking around.", 1), FlowFixtures.Tool("r1", "Sources/A.swift 읽기", 2), FlowFixtures.Tool("spawn", "Agent: scout", 3, "Agent"),
                FlowFixtures.Tool("turn", "응답 생성", 4, "", "turn"), FlowFixtures.Tool("ask", "어느 쪽으로 할까요?", 9, "AskUserQuestion", state: "waiting"),
                FlowFixtures.Said("c1", ContextCompaction.Title + " · 자동 정리 · 84,120 → 21,300 토큰", 10, "system"), FlowFixtures.Said("e1", "오류 줄", 11, "error") };
            var scout = Agent("scout", status: "completed", entries: [FlowFixtures.Tool("s1", "grep tracker", 5, "Grep"), FlowFixtures.Said("s2", "Found three.", 6), FlowFixtures.Tool("s3", "B.swift 읽기", 7)],
                started: 3, ended: 8, type: "Explore", resolved: "claude-haiku-5-5");
            var request = FlowFixtures.Run("r", root: root, agents: [scout]);
            var rows = FlowLog.Rows([request], 20);
            SameList(Words(rows), ["Sonnet 5.5 | Sources/A.swift 읽기", "scout | 시작 · Explore · Haiku 5.5", "scout | grep tracker", "scout | B.swift 읽기", "scout | 완료",
                "Sonnet 5.5 | 질문 · 어느 쪽으로 할까요?", "Sonnet 5.5 | 컨텍스트 정리 · 자동 정리 · 84,120 → 21,300 토큰"], "the rows");
            SameList(rows.Select(r => r.Time), new[] { 2, 3, 5, 7, 8, 9, 10 }.Select(Date), "their times");
            Check(rows[0].Actor == FlowLog.Actor.Lead && rows[0].Ink == FlowRoles.Ink(FlowRole.Lead), "the lead in its ink");
            Same(rows[0].NodeID, Node(request, MightyGraphLayout.WorkSuffix), "leading to the 실행 block");
            Check(rows[1].Actor == FlowLog.Actor.Subagent("scout") && rows[1].Ink == FlowInk.Agent, "the sub-agent in its ink");
            Same(rows[1].NodeID, Node(request, "agent:scout"), "leading to its block");
            Check(rows[1].Actor.Role == FlowRole.Subagent && rows[0].Actor.Role == FlowRole.Lead, "the actors' roles");
            Same(rows.Select(r => r.Id).Distinct().Count(), rows.Count, "an id each");
        });

        Same(FlowLog.LeadWords(FlowFixtures.Said("t", "Just words.")), null, "a reply");
        Same(FlowLog.LeadWords(FlowFixtures.Tool("turn", "응답 생성", 0, "", "turn")), null, "the lifecycle row");
        Same(FlowLog.LeadWords(FlowFixtures.Tool("a", "Agent: scout", 0, "Agent")), null, "an Agent call");
        Same(FlowLog.LeadWords(FlowFixtures.Tool("a", "Task: scout", 0, "Task")), null, "a Task call");
        Same(FlowLog.LeadWords(FlowFixtures.Said("e", "오류", 0, "error")), null, "an error line");
        Same(FlowLog.LeadWords(FlowFixtures.Said("s", "다른 시스템 줄", 0, "system")), null, "another system line");
        Same(FlowLog.LeadWords(FlowFixtures.Tool("w", "", 0, "WebFetch")), "WebFetch", "a call without a summary");
        var longest = FlowLog.LeadWords(FlowFixtures.Tool("l", new string('가', 400) + "\nnext"));
        Check(System.Text.Encoding.UTF8.GetByteCount(longest ?? "") <= FlowLog.LineBytes && longest?.Contains('\n') == false, "one bounded line");

        var busy = Agent("busy", entries: Enumerable.Range(0, 40).Select(i => FlowFixtures.Tool("b" + i, "grep " + i, i * 2 + 1)).ToList(), started: 0);
        var many = FlowFixtures.Run("r", root: Enumerable.Range(0, 40).Select(i => FlowFixtures.Tool("r" + i, "read " + i, i * 2)).ToList(), agents: [busy]);
        var last = FlowLog.Rows([many]);
        Same(last.Count, FlowLog.VisibleRows, "the last five");
        SameList(last.Select(r => r.What), ["grep 37", "read 38", "grep 38", "read 39", "grep 39"], "oldest first");
        SameList(last.Select(r => r.Time), last.Select(r => r.Time).Order(), "in time");
        SameList(FlowLog.Rows([many], 2).Select(r => r.What), ["read 39", "grep 39"], "a smaller limit");
        Same(FlowLog.Rows([many], 0).Count, 0, "no room");
        Same(FlowLog.Rows([]).Count, 0, "no run");

        var second = FlowFixtures.Run("r", root: [FlowFixtures.Tool("a", "first", 5), FlowFixtures.Tool("b", "second", 5), FlowFixtures.Tool("c", "third", 5)]);
        SameList(FlowLog.Rows([second]).Select(r => r.What), ["first", "second", "third"], "one second keeps the recorded order");
        second.RootEntries[1] = second.RootEntries[1] with { Timestamp = "not a time" };
        SameList(FlowLog.Rows([second]).Select(r => r.What), ["first", "third"], "a time that cannot be read is no row");

        FlowFixtures.Korean(() =>
        {
            var old = FlowFixtures.Run("old", "completed", root: [FlowFixtures.Tool("o1", "old read", 1), FlowFixtures.Said("o2", "old answer", 2)], output: "old answer");
            var bg = Agent("bg", status: "completed", background: true, entries: [FlowFixtures.Tool("g1", "c.txt 읽기", 13)], started: 12, ended: 15);
            var request = FlowFixtures.Run("new", "completed", [bg], [FlowFixtures.Tool("n1", "new read", 11), FlowFixtures.Said("n2", "launched", 14)], "launched");
            MightyGraphAgent Copy() => Agent("new:notice:1:bg", status: "completed", background: true, source: "bg", started: 12, ended: 15);
            MightyGraphRun Notice(string status = "completed", bool rooted = true, string? output = "done") =>
                FlowFixtures.Run("new:notice:1", status, [Copy()], rooted ? [FlowFixtures.Said("x1", "done", 16)] : [], output, true, "백그라운드 작업 완료 · bg");
            var notice = Notice();
            var runs = MightyGraphSupport.ShownRuns([old, request, notice]);
            Same(FlowLog.Activity(runs), (1, 3), "the newest request and the notice runs after it");
            var rows = FlowLog.Rows(runs, 20);
            SameList(Words(rows), ["Sonnet 5.5 | new read", "bg | 시작", "bg | c.txt 읽기", "Sonnet 5.5 | 완료", "bg | 완료", "Sonnet 5.5 | 알림 받음 · 백그라운드 작업 완료 · bg", "Sonnet 5.5 | 완료"], "the activity's rows");
            SameList(rows.Select(r => r.Time), new[] { 11, 12, 13, 14, 15, 16, 16 }.Select(Date), "their times");
            Same(rows[5].NodeID, Node(notice, "request"), "the notice leads to its run's request block");
            Same(rows[6].NodeID, Node(notice, MightyGraphLayout.WorkSuffix), "its end to its 실행 block");
            var answering = FlowLog.Rows(MightyGraphSupport.ShownRuns([old, request, Notice("running", false, null)]), 20);
            Check(answering.Last().What == "알림 받음 · 백그라운드 작업 완료 · bg" && answering.Last().Time == Date(15), "a notice that recorded nothing yet came when its work ended");
            Same(FlowLog.Rows(MightyGraphSupport.ShownRuns([old, request, Notice(output: null)]), 20).Last().What, "알림 받음 · 백그라운드 작업 완료 · bg", "taken into a running turn: no end");
            Same(FlowLog.Rows(MightyGraphSupport.ShownRuns([old, request, Notice("error", output: null)]), 20).Last().What, "실패", "one that failed says so");
            Same(FlowLog.Activity([notice]), (0, 1), "a pane with only a notice run");
        });

        var plain = Agent("plain", status: "completed", entries: [FlowFixtures.Tool("p1", "read", 3)]);
        var custom = Agent("custom", entries: [FlowFixtures.Tool("c1", "grep", 4)], type: "reviewer");
        var nested = Agent("nested", "custom", entries: [FlowFixtures.Tool("d1", "deep", 5)]);
        var task = Agent("task", background: true, kind: "task", entries: [FlowFixtures.Said("t1", "launched", 6, "system")]);
        var inked = FlowLog.Rows([FlowFixtures.Run("r", agents: [plain, custom, nested, task])], 20, new Dictionary<string, AgentDefinition> { ["reviewer"] = new("reviewer", "green") });
        SameList(inked.Select(r => r.What), ["read", "grep", "deep"], "no start or end without figures, no command");
        SameList(inked.Select(r => r.Ink), [FlowInk.Agent, FlowInk.Done, FlowInk.Agent], "a custom agent's colour");
        SameList(inked.Select(r => r.Who), ["plain", "custom", "nested"], "by their names");


        FlowFixtures.Korean(() =>
        {
            Same(FlowLog.LeadName(FlowFixtures.Run("r")), "Sonnet 5.5", "the model that answered");
            var configured = FlowFixtures.Run("r", model: null);
            configured.NodeModelLabel = "opus · 설정";
            Same(FlowLog.LeadName(configured), "Opus · 설정", "the configured model");
            Same(FlowLog.LeadName(FlowFixtures.Run("r", model: null)), "Claude", "the agent's name");
            Same(FlowLog.LeadName(FlowFixtures.Run("r", model: null, provider: "codex")), "Codex", "another agent's");
        });

        var inner = Agent("inner", "outer", entries: [FlowFixtures.Tool("i1", "grep", 4, "Grep")], started: 3);
        var outer = Agent("outer", entries: [FlowFixtures.Tool("o1", "ls", 2, "Bash")], started: 1);
        var targetRun = FlowFixtures.Run("r", root: [FlowFixtures.Tool("m1", "A.swift 읽기", 0)], agents: [outer, inner]);
        var targetRows = FlowLog.Rows([targetRun], 20);
        var innerRow = targetRows.First(r => r.Id.EndsWith(":i1", StringComparison.Ordinal));
        var leadRow = targetRows.First(r => r.Actor == FlowLog.Actor.Lead);
        string workNode = Node(targetRun, MightyGraphLayout.WorkSuffix), outerNode = Node(targetRun, "agent:outer"), innerNode = Node(targetRun, "agent:inner");
        Same(innerRow.NodeID, innerNode, "its block");
        Same(FlowLog.Target(innerRow, [targetRun], _ => true), innerNode, "its own when drawn");
        Same(FlowLog.Target(leadRow, [targetRun], _ => true), workNode, "the lead's");
        Same(FlowLog.Target(innerRow, [targetRun], id => id != innerNode), outerNode, "the card above it");
        Same(FlowLog.Target(innerRow, [targetRun], id => id == workNode), workNode, "else the main session");
        Same(FlowLog.Target(innerRow, [targetRun], _ => false), null, "nothing drawn");
        Same(FlowLog.Target(leadRow, [targetRun], _ => false), null, "nothing drawn for the lead");
        var loop = FlowFixtures.Run("r", agents: [Agent("self", "self", started: 1)]);
        Same(FlowLog.Target(FlowLog.Rows([loop], 20).First(), [loop], _ => false), null, "a block that is its own parent ends the walk");
        var folded = FlowFixtures.Layout([targetRun]).Nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        Same(FlowLog.Target(innerRow, [targetRun], folded.Contains), outerNode, "the layout the pane draws: the folded card");
        Same(FlowLog.Target(leadRow, [targetRun], folded.Contains), workNode, "and the 실행 block");
        var opened = FlowFixtures.Layout([targetRun], opened: [outerNode]).Nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        Same(FlowLog.Target(innerRow, [targetRun], opened.Contains), innerNode, "opened, its own");

        var moment = DateTimeOffset.FromUnixTimeSeconds(1_794_336_483);
        Same(FlowLog.Clock(moment, TimeZoneInfo.Utc), "18:48:03", "in UTC");
        Same(FlowLog.Clock(moment, TimeZoneInfo.CreateCustomTimeZone("seoul", TimeSpan.FromHours(9), "Seoul", "Seoul")), "03:48:03", "nine hours east");
        return Task.CompletedTask;
    }

    internal static Task TheStatusLineSaysWhoIsDoingWhatNow()
    {
        FlowFixtures.Korean(() =>
        {
            var agents = new List<MightyGraphAgent> { Agent("a"), Agent("b", status: "completed"), Agent("c", background: true) };
            var running = FlowLog.StatusOf([FlowFixtures.Run("r", agents: agents)])!;
            Same(running.Lead, "Sonnet 5.5 실행 중", "the main session");
            Same(running.Subagents, "하위 에이전트 2/3 실행 중", "the sub-agents");
            Same(running.Text, "Sonnet 5.5 실행 중 · 하위 에이전트 2/3 실행 중", "the line");
            SameList(running.Parts, [new FlowLog.Part(FlowRole.Lead, "Sonnet 5.5 실행 중"), new FlowLog.Part(FlowRole.Subagent, "하위 에이전트 2/3 실행 중")], "its parts");
            var ended = new List<MightyGraphAgent> { Agent("a", status: "completed"), Agent("b", status: "completed") };
            Same(FlowLog.StatusOf([FlowFixtures.Run("r", "completed", ended, output: "done")])!.Text, "Sonnet 5.5 완료 · 하위 에이전트 2개 끝남", "every sub-agent ended");
            Same(FlowLog.StatusOf([FlowFixtures.Run("r", "error")])!.Text, "Sonnet 5.5 실패", "failed");
            Same(FlowLog.StatusOf([]), null, "no request");

            var awaiting = MightyGraphSupport.ShownRuns([FlowFixtures.Run("r", "completed", [Agent("bg", background: true)], output: "launched")]);
            Same(FlowLog.StatusOf(awaiting)!.Text, "Sonnet 5.5 알림 대기 · 하위 에이전트 1/1 실행 중", "awaiting its notice");
            MightyGraphRun Request() => FlowFixtures.Run("r", "completed", [Agent("bg", status: "completed", background: true)], output: "launched");
            MightyGraphRun Answer(string status, string? output = null, string? model = "claude-haiku-5-5") =>
                FlowFixtures.Run("r:notice:1", status, [Agent("r:notice:1:bg", status: "completed", background: true, source: "bg")], output: output, notice: true, title: "백그라운드 작업 완료 · bg", model: model);
            Same(FlowLog.StatusOf(MightyGraphSupport.ShownRuns([Request(), Answer("running")]))!.Text, "Haiku 5.5 실행 중 · 하위 에이전트 1개 끝남", "a notice turn is the main session at work");
            Same(FlowLog.StatusOf(MightyGraphSupport.ShownRuns([Request(), Answer("completed", "done")]))!.Text, "Haiku 5.5 완료 · 하위 에이전트 1개 끝남", "answered");
            Same(FlowLog.StatusOf(MightyGraphSupport.ShownRuns([Request(), Answer("running", model: null)]))!.Lead, "Sonnet 5.5 실행 중", "named as its request until it answers");
        });
        return Task.CompletedTask;
    }

    internal static Task TheRecordedRequestLogsItsSubagentsWork()
    {
        FlowFixtures.Korean(() =>
        {
            var runs = MightyGraphSupport.ShownRuns(ReplayParsed("claude-stream-subagents", "recorded"));
            var rows = FlowLog.Rows(runs, 40);
            var starts = rows.Where(r => r.Id.EndsWith(":start", StringComparison.Ordinal)).ToList();
            SameList(starts.Select(r => r.Who), ["count a", "count b and c"], "both started");
            SameList(starts.Select(r => r.What), ["시작 · Explore · Haiku 5.5", "시작 · Explore · Haiku 5.5"], "as their cards' second line");
            SameList(starts.Select(r => r.Time), new[] { "2026-11-10T18:56:31.912Z", "2026-11-10T18:56:44.496Z" }.Select(t => DateTimeOffset.Parse(t, System.Globalization.CultureInfo.InvariantCulture)), "at the times the CLI's frames name");
            SameList(rows.Where(r => r.Id.EndsWith(":end", StringComparison.Ordinal) && r.Actor != FlowLog.Actor.Lead).Select(r => r.Who), ["count a", "count b and c"], "both ended");
            var background = runs[0].FlowAgents.First(a => a.Title == "count b and c").Id;
            Same(rows.Count(r => r.Actor == FlowLog.Actor.Subagent(background) && !r.Id.EndsWith(":start", StringComparison.Ordinal) && !r.Id.EndsWith(":end", StringComparison.Ordinal)), 2, "the background agent's two reads");
            Check(rows.Any(r => r.What == "알림 받음 · 백그라운드 작업 완료 · count b and c"), "its end came in as a notice");
            Same(FlowLog.StatusOf(runs)!.Text, "Haiku 5.5 완료 · 하위 에이전트 2개 끝남", "the status once it is over");
        });

        // The pane remembers the strip folded, written only when folded.
        var pane = JsonSerializer.Deserialize<RunSession>("""{"id":"p","workspaceId":"w","title":"Claude"}""", Wire.Json)!;
        Same(pane.FlowLogCollapsed, null, "an older save is open");
        Check(!JsonSerializer.Serialize(pane, Wire.Json).Contains("flowLogCollapsed"), "an open strip writes nothing");
        var folded = FlowCopy.Collapse(pane, true);
        Same(JsonSerializer.Deserialize<RunSession>(JsonSerializer.Serialize(folded, Wire.Json), Wire.Json)!.FlowLogCollapsed, true, "a folded one is kept");
        Same(FlowCopy.Collapse(folded, false).FlowLogCollapsed, null, "opened again");
        Same(JsonSerializer.Deserialize<RunSession>("""{"id":"p","workspaceId":"w","title":"Claude","flowLogCollapsed":false}""", Wire.Json)!.FlowLogCollapsed, null, "false is open");
        Same(JsonSerializer.Deserialize<RunSession>("""{"id":"p","workspaceId":"w","title":"Claude","flowLogCollapsed":"yes"}""", Wire.Json)!.FlowLogCollapsed, null, "damage is open");
        return Task.CompletedTask;
    }

    // ── FlowAdvisorTests ──

    private static GraphAdvisorCall Call(string id, string? caller = null, int? at = null, int? until = null, string state = "done", string? text = null, string? code = null,
        bool before = false, bool followed = false) =>
        new(id, caller, at is { } s ? Stamp(s) : null, until is { } e ? Stamp(e) : null, state, state == "done" && text is null ? true : null, text, code, before ? true : null, followed ? true : null);
    private static MightyGraphRun Advised(string id, string status = "running", List<MightyGraphAgent>? agents = null, List<GraphAdvisorCall>? calls = null, string? model = null,
        GraphTokenUsage? usage = null, string? configured = null, int? total = null, bool notice = false, string? output = null, string provider = "claude") =>
        FlowFixtures.Run(id, status, agents, output: output, notice: notice, provider: provider, configuredAdvisor: configured,
            advisor: calls is null && model is null && usage is null ? null : new GraphAdvisor(model, usage, calls ?? [], total));
    private static MightyGraphAgent Caller(string id, string? parent = null, string status = "running", bool background = false, string? type = null) =>
        FlowFixtures.Agent(id, parent, status, background, identity: new GraphAgentIdentity(type));

    internal static Task TheWebPagesCopyIsTheRoleViewsWords()
    {
        FlowFixtures.Korean(() =>
        {
            var scout = FlowFixtures.Agent("scout", identity: new GraphAgentIdentity("Explore", "haiku"), figures: new GraphAgentFigures(Stamp(1)));
            var done = FlowFixtures.Agent("done", status: "completed", entries: [FlowFixtures.Said("d1", "finished")]);
            var build = FlowFixtures.Agent("ci", background: true, kind: "task", title: "build", input: "npm run build", figures: new GraphAgentFigures(Stamp(0)));
            var request = FlowFixtures.Run("r", agents: [scout, done, build], effort: "high");
            var runs = new List<MightyGraphRun> { request };
            var layout = FlowFixtures.Layout(runs);
            var blocks = FlowCopy.Blocks(layout, runs, true, output: id => id == "ci" ? ["compiling", "[exited with code 0]"] : []);
            // The 실행 card stands where the answer will come: the lead with its effort, and its card.
            var workCopy = blocks[Node(request, MightyGraphLayout.WorkSuffix)];
            var work = workCopy.Role!;
            Check(work.Name == "lead" && work.Ink == "taskText" && work.Effort == "High" && work.EffortHelp == "사고 강도 · High", "the 실행 block is the lead with its effort");
            Check(workCopy.Card is { Title: "실행 · 요청 1", Identity: "Sonnet 5.5", Running: true, Ink: "taskText" }, "and the 실행 card");
            var scoutCopy = blocks[Node(request, "agent:scout")];
            Check(scoutCopy.Role is { Name: "subagent", Ink: "agentText", TypeChip: "Explore" }, "a running sub-agent's identity");
            var card = scoutCopy.Card!;
            Check(card.Title == "scout" && card.Identity == "Explore · Haiku · 요청" && card.Running && card.Status == "running" && !card.Prose && !card.Pending, "its card's words");
            SameList(card.Before, ["실행 중"], "before its time");
            Check(card.StartedAt == Date(1).ToUnixTimeMilliseconds() && card.EndedAt is null, "its start for the page's clock");
            Check(card.CopyNodeId is null && card.ActivateHelp == "블록으로 펼치기" && card.Ink == "agentText" && !card.IsTask, "it opens in place");
            var doneCopy = blocks[Node(request, "agent:done")];
            Check(doneCopy.Role is { Name: "subagent" } && doneCopy.Card is { Running: false, Status: "completed" }, "an ended foreground sub-agent is a card too");
            var task = blocks[Node(request, "agent:ci")];
            Check(task.Role is { Name: "task", Ink: "ink2" } && task.Role.Marks.Count == 1 && task.Role.Marks[0].Name == "background" && task.Role.Marks[0].Help == "백그라운드에서 실행", "a command's identity");
            Check(task.Card is { IsTask: true, Identity: "작업 · npm run build", Activity: "compiling", Ink: "ink2" }, "its card");
            var line = blocks[Node(request, MightyGraphLayout.DelegateSuffix + FlowDelegation.MainKey)].Line!;
            Same(line.Head, "3개 병렬", "the line's head");
            SameList(line.Parts.Select(p => p.Text + "/" + p.Ink), ["하위 에이전트 2/agentText", "작업 1/ink2"], "each kind in its ink");
            Check(line.Model is null && line.Progress == "2/3 실행 중" && line.Text == "3개 병렬 · 하위 에이전트 2 · 작업 1 · 2/3 실행 중", "the rest of it");
            Check(!blocks.ContainsKey(Node(request, MightyGraphLayout.ReturnSuffix)), "no return node: the 실행 card stands there");

            // A request with steps: a step's card and what it holds, and the plan column.
            var stepped = FlowFixtures.Run("s", root: [FlowFixtures.Tool("e1", "git status", 2), FlowFixtures.Tool("e2", "git push", 12)]);
            stepped.Steps = [new FlowStep("text:점검", "점검", "점검 중", "completed", Stamp(0), Stamp(10)), new FlowStep("text:푸시", "푸시", "푸시하는 중", "in_progress", Stamp(10)),
                new FlowStep("text:CI", "CI")];
            var steppedRuns = new List<MightyGraphRun> { stepped };
            var steppedCopy = FlowCopy.Blocks(FlowFixtures.Layout(steppedRuns), steppedRuns, true);
            var first = steppedCopy[Node(stepped, FlowSteps.NodeSuffix(stepped.Steps[0]))];
            Check(first.Card is { Title: "1. 점검", Status: "completed", Activity: "git status", Prose: false, Pending: false }, "a done step's card");
            Check(first.Step is { Title: "1. 점검", Status: "completed" } && first.Step.EntryIds.SequenceEqual(["e1"]), "and what it holds");
            Check(!steppedCopy.ContainsKey(Node(stepped, FlowSteps.NodeSuffix(stepped.Steps[2]))), "a step not begun has no card");
            var progress = FlowCopy.ProgressOf(steppedRuns)[stepped.Id];
            Check(progress.Title == "진행 상황" && progress.Heading == "요청 1" && progress.Count == "1/3" && progress.Running && progress.Hidden == 0 && progress.More is null
                && progress.RequestNodeId == Node(stepped, "request"), "the progress panel's head and its request");
            SameList(progress.Rows.Select(r => r.Text + "/" + r.State), ["1. 점검/done", "2. 푸시하는 중/current", "3. CI/pending"], "every step and its state");
            Check(progress.Rows[0].NodeId == Node(stepped, FlowSteps.NodeSuffix(stepped.Steps[0])) && progress.Rows[2].NodeId is null && progress.Rows[0].EndedAt == Date(10).ToUnixTimeMilliseconds(), "where a row leads, and its times");

            var plainLayout = MightyGraphLayout.Make(MightyGraphSupport.ShownRuns(runs), "", true, new HashSet<string>(), splitsExecution: true);
            var plainCopy = FlowCopy.Blocks(plainLayout, runs, false);
            Check(plainCopy[Node(request, MightyGraphLayout.WorkSuffix)] is { Role.Name: "lead", Card: null }, "without the role view the lead still wears its ink, with no card");
            Check(plainCopy[Node(request, "agent:scout")] is { Role.Name: "subagent", Card: null }, "and a sub-agent its own, with no card");
            Check(!plainCopy.Values.Any(b => b.Line is not null || b.Back is not null) && !plainCopy.ContainsKey(Node(request, "agent:ci")), "no line, way back or command block");

            var log = FlowCopy.LogOf(runs, false)!;
            Same(log.StatusText, "Sonnet 5.5 실행 중 · 하위 에이전트 1/2 실행 중 · 작업 1개 실행 중", "the status line");
            SameList(log.Status.Select(p => p.Ink), ["taskText", "agentText", "ink2"], "each part in its role's ink");
            SameList(log.Rows.Select(r => r.Who + " | " + r.What), ["scout | 시작 · Explore · Haiku · 요청"], "the rows");
            Check(log.Rows[0].Ink == "agentText" && log.Rows[0].NodeId == Node(request, "agent:scout") && log.Rows.All(r => r.Clock.Length == 8) && !log.Collapsed, "their inks, blocks and clocks");
            var collapsed = FlowCopy.LogOf(runs, true)!;
            Check(collapsed.Collapsed && collapsed.Rows.Count == 0 && collapsed.StatusText == log.StatusText, "folded: the status line alone");
            Same(FlowCopy.LogOf([], false), null, "no request, no strip");
        });
        return Task.CompletedTask;
    }
}
