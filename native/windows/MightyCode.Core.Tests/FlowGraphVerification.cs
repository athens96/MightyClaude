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
            Check(rows[1].Actor.Role == FlowRole.Subagent && rows[0].Actor.Role == FlowRole.Lead && FlowLog.Actor.Advisor.Role == FlowRole.Advisor, "the actors' roles");
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

        var advised = FlowFixtures.Run("r", root: [FlowFixtures.Tool("a", "read", 5)]);
        var work = Node(advised, MightyGraphLayout.WorkSuffix);
        var noted = FlowLog.Rows([advised], advisor: [new("adv-1", Stamp(3), "Opus 5.5", "검토 중", work), new("adv-2", Stamp(7), "Opus 5.5", "조언 전달", work), new("adv-3", "never", "Opus 5.5", "lost", work)]);
        SameList(Words(noted), ["Opus 5.5 | 검토 중", "Sonnet 5.5 | read", "Opus 5.5 | 조언 전달"], "the advisor's notes");
        SameList(noted.Select(r => r.Actor), [FlowLog.Actor.Advisor, FlowLog.Actor.Lead, FlowLog.Actor.Advisor], "their actors");
        Check(noted[0].Ink == FlowRoles.Ink(FlowRole.Advisor) && noted[0].NodeID == work, "in the advisor's ink");

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
            Same(running.Advisor, null, "no advisor");
            Same(running.Text, "Sonnet 5.5 실행 중 · 하위 에이전트 2/3 실행 중", "the line");
            SameList(running.Parts, [new FlowLog.Part(FlowRole.Lead, "Sonnet 5.5 실행 중"), new FlowLog.Part(FlowRole.Subagent, "하위 에이전트 2/3 실행 중")], "its parts");
            SameList(FlowLog.StatusOf([FlowFixtures.Run("r", agents: agents)], FlowLog.AdvisorState.OnCall)!.Parts.Select(p => p.Role), [FlowRole.Lead, FlowRole.Subagent, FlowRole.Advisor], "with the advisor");
            SameList(FlowLog.StatusOf([FlowFixtures.Run("r")], FlowLog.AdvisorState.Consulting)!.Parts.Select(p => p.Role), [FlowRole.Lead, FlowRole.Advisor], "without sub-agents");
            Same(FlowLog.StatusOf([FlowFixtures.Run("r", agents: agents)], FlowLog.AdvisorState.OnCall)!.Text, "Sonnet 5.5 실행 중 · 하위 에이전트 2/3 실행 중 · 조언자 대기", "on call");
            Same(FlowLog.StatusOf([FlowFixtures.Run("r")], FlowLog.AdvisorState.Consulting)!.Text, "Sonnet 5.5 실행 중 · 조언자 검토 중", "at work");
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

    internal static Task TheAdvisorsBandSaysWhatTheStreamShowed()
    {
        FlowFixtures.Korean(() =>
        {
            Same(FlowAdvisor.Of(Advised("plain")), null, "no advisor, no band");
            Check(!FlowAdvisor.Shows(Advised("plain")), "nothing shows");
            var waiting = FlowAdvisor.Of(Advised("on", configured: "opus"))!;
            Same(waiting.Header, "조언자 · Opus · 요청 · 대기 중", "on, never asked");
            Check(waiting.Rows.Count == 0 && waiting.Calls == 0 && waiting.Totals == "호출 0" && waiting.Note is null, "no row");
            Same(waiting.Empty, "아직 조언을 구하지 않았습니다", "not asked yet");
            var unused = FlowAdvisor.Of(Advised("ended", "completed", configured: "opus", output: "done"))!;
            Check(unused.Header == "조언자 · Opus · 요청" && unused.Empty == "조언을 구하지 않았습니다", "never asked once it ended");
            var used = FlowAdvisor.Of(Advised("used", calls: [Call("srvtoolu_a", at: 1, state: "consulting")]))!;
            Check(used.Header == "조언자 · 검토 중" && used.Empty is null, "seen at work");
            Same(FlowAdvisor.Of(Advised("cost", "completed", model: "claude-opus-5-5", usage: new GraphTokenUsage(900)))!.Header, "조언자 · Opus 5.5", "only the cost");
            Same(FlowAdvisor.Of(Advised("r:notice:1", configured: "opus", notice: true)), null, "a notice run that never asked");
            Same(FlowAdvisor.Of(Advised("r:notice:1", calls: [Call("srvtoolu_n", at: 1, until: 2)], configured: "opus", notice: true))!.Header, "조언자 · Opus · 요청 · 대기 중", "one whose turn asked");
            Same(FlowAdvisor.Of(Advised("codex", configured: "opus", provider: "codex")), null, "another agent");

            var runs = MightyGraphSupport.ShownRuns(ReplayParsed("claude-stream-advisor", "recorded"));
            var band = FlowAdvisor.Of(runs[0])!;
            Same(band.Header, "조언자 · Opus 5.5", "the recording's advisor");
            Check(band.Advising is null && band.Calls == 1 && band.Earlier == 0, "over, one call");
            var row = band.Rows[0];
            Check(row.Role == FlowRole.Lead && row.Caller == "메인" && row.Ink == FlowRoles.Ink(FlowRole.Lead), "the main session asked");
            Same(row.CallerNodeID, Node(runs[0], MightyGraphLayout.WorkSuffix), "its block");
            Same(row.Position, FlowAdvisor.Position.LastStep, "nothing but the answer followed");
            Same(row.Line, "답 직전 마지막 단계 · 조언 전달 · 내용 비표시", "its line");
            Same(row.Text, null, "no text");
            Same(row.Started, DateTimeOffset.Parse("2026-11-10T18:16:51.658Z", System.Globalization.CultureInfo.InvariantCulture), "when it asked");
            Check(Math.Abs((row.Seconds ?? 0) - 7.882) < 0.001, "how long the reply took");
            Same(band.Totals, "호출 1 · 읽은 토큰 34K", "the totals");
            Same(band.Note, "조언은 암호화되어 Claude에게만 전달됩니다. 내용은 표시되지 않습니다.", "the note");
            var graph = MightyGraphLayout.Make(runs, "", false, new HashSet<string>(), splitsExecution: true, fanOut: new MightyGraphLayout.FanOut());
            var bandNode = Must(graph, Node(runs[0], FlowAdvisor.Suffix));
            var work = Must(graph, Node(runs[0], MightyGraphLayout.WorkSuffix));
            Same(bandNode.Frame.MaxX + FlowAdvisor.Gap, work.Frame.X, "left of the request's column");
            SameList(graph.AdvisorLinks, [new MightyGraphLayout.AdvisorLink(bandNode.Id, work.Id)], "one dotted link");
            var rows = FlowLog.Rows(runs, 10, advisor: FlowAdvisor.Notes(runs));
            SameList(Words(rows), ["조언자 · Opus 5.5 | 메인의 요청으로 검토 시작", "조언자 · Opus 5.5 | 조언 전달 · 내용 비표시", "Sonnet 5.5 | 완료"], "the log");
            Check(rows.Take(2).All(r => r.Actor == FlowLog.Actor.Advisor && r.Ink == FlowRoles.Ink(FlowRole.Advisor) && r.NodeID == bandNode.Id), "the advisor's rows lead to the band");
            Same(FlowLog.StatusOf(runs, FlowAdvisor.Status(runs))!.Text, "Sonnet 5.5 완료 · 조언자 호출 1", "the status");
            var drawn = graph.Nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
            Same(FlowLog.Target(rows[0], runs, drawn.Contains), bandNode.Id, "a row goes to the band");
        });

        GraphAdvisorCall early = Call("a", before: true, followed: true), middle = Call("b", followed: true), lastStep = Call("c"), earlyLast = Call("d", before: true);
        SameList(new[] { early, middle, lastStep, earlyLast }.Select(c => FlowAdvisor.PositionOf(c, false)), [FlowAdvisor.Position.BeforeChange, FlowAdvisor.Position.During, FlowAdvisor.Position.During, FlowAdvisor.Position.BeforeChange], "while the caller works");
        SameList(new[] { early, middle, lastStep, earlyLast }.Select(c => FlowAdvisor.PositionOf(c, true)), [FlowAdvisor.Position.BeforeChange, FlowAdvisor.Position.During, FlowAdvisor.Position.LastStep, FlowAdvisor.Position.LastStep], "once it answered");
        var positions = new[] { FlowAdvisor.Position.BeforeChange, FlowAdvisor.Position.During, FlowAdvisor.Position.LastStep };
        FlowFixtures.Korean(() => SameList(positions.Select(FlowAdvisor.Words), ["파일 변경 전", "작업 중", "답 직전 마지막 단계"], "in Korean"));
        FlowTrackerVerification.InLanguage("en", () => SameList(positions.Select(FlowAdvisor.Words), ["before any file change", "during the work", "last step before the answer"], "in English"));

        FlowFixtures.Korean(() =>
        {
            var scout = Caller("scout", status: "completed", type: "reviewer");
            var calls = new List<GraphAdvisorCall> { Call("c1", at: 1, until: 9, before: true, followed: true), Call("c2", "scout", 10, 12, text: "Check the cache key first."),
                Call("c3", "busy", 13, state: "consulting"), Call("c4", at: 14, until: 15, state: "declined", followed: true), Call("c5", at: 16, until: 17, state: "error", code: "overloaded", followed: true),
                Call("c6", at: 18, until: 19, state: "error", followed: true), Call("c7", at: 20, until: 21, state: "stopped") };
            var request = Advised("r", agents: [scout, Caller("busy")], calls: calls, model: "claude-opus-5-5", usage: new GraphTokenUsage(1_200, 80, 300));
            var band = FlowAdvisor.Of(request, new Dictionary<string, AgentDefinition> { ["reviewer"] = new("reviewer", "green") })!;
            Same(band.Header, "조언자 · Opus 5.5 · 검토 중", "at work");
            SameList(band.Rows.Select(r => r.Caller), ["메인", "scout", "busy", "메인", "메인", "메인", "메인"], "who asked");
            SameList(band.Rows.Select(r => r.Role), [FlowRole.Lead, FlowRole.Subagent, FlowRole.Subagent, FlowRole.Lead, FlowRole.Lead, FlowRole.Lead, FlowRole.Lead], "their roles");
            var lead = FlowRoles.Ink(FlowRole.Lead);
            SameList(band.Rows.Select(r => r.Ink), [lead, FlowInk.Done, FlowInk.Agent, lead, lead, lead, lead], "a custom agent's colour, as on its block");
            Same(band.Rows[1].CallerNodeID, Node(request, "agent:scout"), "its block");
            SameList(band.Rows.Select(r => r.Line), ["파일 변경 전 · 조언 전달 · 내용 비표시", "답 직전 마지막 단계 · 조언 전달", "작업 중 · 검토 중", "작업 중 · 조언자가 답하지 않음",
                "작업 중 · 오류 · overloaded", "작업 중 · 오류", "작업 중 · 결과 전에 중단"], "where and how each ended");
            SameList(band.Rows.Select(r => r.Text), [null, "Check the cache key first.", null, null, null, null, null], "the advice only as the CLI gave it");
            SameList(band.Rows.Select(r => r.Seconds), [8d, 2d, null, 1d, 1d, 1d, 1d], "how long each reply took");
            SameList(band.Rows.Select(r => r.Started), new[] { 1, 10, 13, 14, 16, 18, 20 }.Select(s => (DateTimeOffset?)Date(s)), "when each asked");
            Check(band.HidesText && band.Totals == "호출 7 · 읽은 토큰 1.5K", "the note stands; what it read is all its input");
            var orphan = FlowAdvisor.Of(Advised("o", calls: [Call("x", "gone", 1, 2)]))!;
            Check(orphan.Rows[0].Caller == "메인" && orphan.Rows[0].CallerNodeID.EndsWith(MightyGraphLayout.WorkSuffix, StringComparison.Ordinal), "a caller the run no longer holds");
            Same(FlowAdvisor.Of(Advised("t", calls: [Call("y", text: "Do it.")]))!.Note, null, "no note without a hidden reply");
        });

        var done = Call("a", at: 1, until: 2);
        Same(FlowAdvisor.Of(Advised("r", calls: [done]))!.Advising, FlowAdvisor.State.OnCall, "on call while the run goes on");
        Same(FlowAdvisor.Of(Advised("r", "completed", calls: [done]))!.Advising, null, "nothing once it ended");
        Same(FlowAdvisor.Of(Advised("r", "error", calls: [done]))!.Advising, null, "nor once it failed");
        var background = MightyGraphSupport.ShownRuns([Advised("r", "completed", [Caller("bg", background: true)], [done], output: "launched")]);
        Same(FlowAdvisor.Of(background[0])!.Advising, FlowAdvisor.State.OnCall, "a background sub-agent may still ask");
        Same(FlowAdvisor.Of(Advised("r", "completed", calls: [Call("b", at: 3, state: "consulting")]))!.Advising, FlowAdvisor.State.Consulting, "at work");

        var six = Enumerable.Range(0, 6).Select(i => Call("c" + i, at: i, until: i + 1, text: "ok")).ToList();
        var rowsBand = FlowAdvisor.Of(Advised("r", calls: six))!;
        const double chrome = FlowAdvisor.HeaderHeight + FlowAdvisor.FooterHeight;
        var all = FlowAdvisor.Visible(rowsBand, chrome + 6 * FlowAdvisor.RowHeight);
        Check(all.Rows.Count == 6 && all.Earlier == 0, "room for all");
        var fewer = FlowAdvisor.Visible(rowsBand, chrome + 5 * FlowAdvisor.RowHeight);
        SameList(fewer.Rows.Select(r => r.Id), ["c2", "c3", "c4", "c5"], "one short: the line that counts takes room");
        Same(fewer.Earlier, 2, "two before");
        var hidden = FlowAdvisor.Of(Advised("r", calls: six.Select(c => c with { Text = null }).ToList()))!;
        Same(FlowAdvisor.Visible(hidden, chrome + 6 * FlowAdvisor.RowHeight).Rows.Count, 4, "the note takes its room first");
        Same(FlowAdvisor.Visible(hidden, chrome + FlowAdvisor.NoteHeight + 6 * FlowAdvisor.RowHeight).Rows.Count, 6, "with room for the note");
        var trimmed = FlowAdvisor.Of(Advised("r", calls: six, total: 40))!;
        Check(trimmed.Calls == 40 && trimmed.Earlier == 34, "consultations no longer kept");
        var shown = FlowAdvisor.Visible(trimmed, chrome + 6 * FlowAdvisor.RowHeight);
        Check(shown.Rows.Count == 5 && shown.Earlier == 35, "counted with the ones that do not fit");
        Same(FlowAdvisor.Visible(rowsBand, 10).Rows.Count, 0, "no room at all");
        Same(FlowAdvisor.Visible(hidden, FlowAdvisor.MinimumHeight).Rows.Count, 1, "the least band holds one row and its note");
        return Task.CompletedTask;
    }

    internal static Task TheAdvisorsBandStandsBesideItsRun()
    {
        var advised = Advised("r", agents: [Caller("a"), Caller("b"), Caller("c")], calls: [Call("c1", at: 1, until: 2), Call("c2", "b", 3, state: "consulting")], configured: "opus");
        var graph = FlowFixtures.Layout([advised]);
        var band = Must(graph, Node(advised, FlowAdvisor.Suffix));
        var work = Must(graph, Node(advised, MightyGraphLayout.WorkSuffix));
        var own = graph.Nodes.Where(n => n.Id != band.Id).ToList();
        Same(band.Kind, MightyGraphLayout.AdvisorKind, "the advisor's band");
        Same(band.Frame.W, FlowAdvisor.Width, "its width");
        Same(band.Frame.Y, work.Frame.Y, "from the main session's block's top");
        Same(band.Frame.MaxY, own.Max(n => n.Frame.MaxY), "to the run's last block");
        var beside = own.Where(n => n.Frame.Y < band.Frame.MaxY && n.Frame.MaxY > band.Frame.Y).ToList();
        Same(band.Frame.MaxX + FlowAdvisor.Gap, beside.Min(n => n.Frame.X), "left of every block it shares rows with");
        Check(!own.Any(n => FlowFixtures.Overlap(n.Frame, band.Frame)), "over none of them");
        Check(graph.OriginX <= band.Frame.X - MightyGraphCamera.Margin, "the canvas reaches it");
        Check(graph.OriginX + graph.Size.W >= own.Max(n => n.Frame.MaxX) + MightyGraphCamera.Margin, "and still the other side");
        Check(MightyGraphLayout.IsFixed(band) && !MightyGraphLayout.IsResizable(band) && !MightyGraphLayout.IsAuxiliary(band.Kind), "a label beside the flow");
        Check(MightyGraphFollow.TargetID(graph, [advised]) != band.Id, "never what the camera keeps");
        var plain = Advised("r", agents: [Caller("a"), Caller("b"), Caller("c")]);
        var before = FlowFixtures.Layout([plain]);
        SameList(before.Nodes.Select(n => n.Id), own.Select(n => n.Id), "the same blocks");
        SameList(before.Nodes.Select(n => n.Frame), own.Select(n => n.Frame), "in the same places");
        SameList(before.Edges, graph.Edges, "the same edges");
        Same(before.AdvisorLinks.Count, 0, "no links");
        var flat = FlowFixtures.Layout([advised], fanOut: false);
        Check(!flat.Nodes.Any(n => n.Kind == MightyGraphLayout.AdvisorKind) && flat.AdvisorLinks.Count == 0, "no band without the role view");

        var asking = Advised("r", agents: [Caller("a"), Caller("b"), Caller("c")],
            calls: [Call("c1", at: 1, until: 2), Call("c2", "a", 3, 4), Call("c3", "c", 5, 6), Call("c4", "c", 7, state: "consulting"), Call("c5", at: 8, until: 9)]);
        var linked = FlowFixtures.Layout([asking]);
        SameList(linked.AdvisorLinks.Select(l => l.Target), [Node(asking, MightyGraphLayout.WorkSuffix), Node(asking, "agent:a"), Node(asking, "agent:c")], "one link for each caller, in order");
        // (macOS aDottedLinkGoesToEachBlockThatAskedAndPassesOverNoBlock also checks MightyGraphLayout.advisorRoutes, which the web page draws: flow.ts advisorRoute.)
        var deep = Advised("d", agents: [Caller("outer"), Caller("inner", "outer")], calls: [Call("n1", "inner", 1, 2), Call("n2", "outer", 3, 4)]);
        var folded = FlowFixtures.Layout([deep]);
        Check(FlowFixtures.Find(folded, Node(deep, "agent:inner")) is null, "the inner block is folded");
        SameList(folded.AdvisorLinks.Select(l => l.Target), [Node(deep, "agent:outer")], "its link goes to the card above it, once");
        var opened = FlowFixtures.Layout([deep], opened: [Node(deep, "agent:outer")]);
        SameList(opened.AdvisorLinks.Select(l => l.Target), [Node(deep, "agent:inner"), Node(deep, "agent:outer")], "opened, each its own");

        var first = Advised("one", "completed", [Caller("a", status: "completed"), Caller("b", status: "completed")], [Call("c1", at: 1, until: 2)], output: "done");
        var second = Advised("two", calls: Enumerable.Range(0, 12).Select(i => Call("d" + i, at: 10 + i, until: 11 + i)).ToList(), configured: "opus");
        var third = Advised("three");
        var three = FlowFixtures.Layout([first, second, third]);
        var bands = three.Nodes.Where(n => n.Kind == MightyGraphLayout.AdvisorKind).ToList();
        SameList(bands.Select(n => n.Id), [Node(first, FlowAdvisor.Suffix), Node(second, FlowAdvisor.Suffix)], "a band for each advised run");
        Check(!FlowFixtures.Overlap(bands[0].Frame, bands[1].Frame), "they never overlap");
        foreach (var each in bands) Check(!three.Nodes.Any(n => n.Id != each.Id && FlowFixtures.Overlap(n.Frame, each.Frame)), "nothing covers a band");
        var secondWork = Must(three, Node(second, MightyGraphLayout.WorkSuffix));
        Check(bands[1].Frame.Y == secondWork.Frame.Y && bands[1].Frame.H == secondWork.Frame.H, "as tall as its run's column");
        var secondBand = FlowAdvisor.Of(second)!;
        var visible = FlowAdvisor.Visible(secondBand, bands[1].Frame.H);
        Check(visible.Rows.Count < 12 && visible.Rows.Count + visible.Earlier == 12 && visible.Rows[^1].Id == "d11", "the rows it has no room for are counted");

        FlowFixtures.Korean(() =>
        {
            var live = Advised("r", agents: [Caller("scout")], calls: [Call("c1", at: 2, until: 5), Call("c2", "scout", 6, state: "consulting")], model: "claude-opus-5-5");
            var bandID = Node(live, FlowAdvisor.Suffix);
            var notes = FlowAdvisor.Notes([live]);
            SameList(notes.Select(n => n.What), ["메인의 요청으로 검토 시작", "조언 전달 · 내용 비표시", "scout의 요청으로 검토 시작"], "a consultation's start and end");
            SameList(notes.Select(n => n.Time), [Stamp(2), Stamp(5), Stamp(6)], "at their times");
            Check(notes.All(n => n.Who == "조언자 · Opus 5.5" && n.NodeID == bandID), "by the advisor, leading to the band");
            Same(notes.Select(n => n.Id).Distinct().Count(), 3, "an id each");
            Same(FlowAdvisor.Status([live]), FlowLog.AdvisorState.Consulting, "at work");
            Same(FlowLog.StatusOf([live], FlowAdvisor.Status([live]))!.Text, "Sonnet 5.5 실행 중 · 하위 에이전트 1/1 실행 중 · 조언자 검토 중", "the status line");
            Same(FlowAdvisor.Status([Advised("r", calls: [Call("c1", at: 2, until: 5)])]), FlowLog.AdvisorState.OnCall, "on call");
            Same(FlowAdvisor.Status([Advised("r", configured: "opus")]), FlowLog.AdvisorState.OnCall, "on call, never asked");
            Same(FlowLog.StatusOf([Advised("r", configured: "opus")], FlowLog.AdvisorState.OnCall)!.Text, "Sonnet 5.5 실행 중 · 조언자 대기", "in words");
            Same(FlowAdvisor.Status([Advised("r", "completed", calls: [Call("c1", at: 2, until: 5), Call("c2", at: 6, until: 7)], output: "done")]), FlowLog.AdvisorState.Consulted(2), "how often, once over");
            Same(FlowAdvisor.Status([Advised("r", "completed", configured: "opus", output: "done")]), null, "never asked");
            Check(FlowAdvisor.Status([Advised("r")]) is null && FlowAdvisor.Status([]) is null, "no advisor");
            var awaiting = MightyGraphSupport.ShownRuns([Advised("r", "completed", [Caller("bg", background: true)], configured: "opus", output: "launched")]);
            Same(FlowAdvisor.Status(awaiting), FlowLog.AdvisorState.OnCall, "awaiting a notice");
            var old = Advised("old", "completed", calls: [Call("o1", at: 1, until: 2)], output: "done");
            Check(FlowAdvisor.Status([old, Advised("new")]) is null && FlowAdvisor.Notes([old, Advised("new")]).Count == 0, "only the newest activity");
            var many = Advised("m", calls: Enumerable.Range(0, 20).Select(i => Call("k" + i, at: i, until: i)).ToList());
            var node = Node(many, FlowAdvisor.Suffix);
            SameList(FlowAdvisor.Notes([many], 3).Select(n => n.Id), new[] { "k17", "k18", "k19" }.SelectMany(id => new[] { node + ":" + id + ":start", node + ":" + id + ":end" }), "only each run's last few");
        });
        return Task.CompletedTask;
    }

    // ── The page's copy (Core FlowCopy) ──

    internal static Task TheWebPagesCopyIsTheRoleViewsWords()
    {
        FlowFixtures.Korean(() =>
        {
            var scout = FlowFixtures.Agent("scout", identity: new GraphAgentIdentity("Explore", "haiku"), figures: new GraphAgentFigures(Stamp(1)));
            var done = FlowFixtures.Agent("done", status: "completed", entries: [FlowFixtures.Said("d1", "finished")]);
            var build = FlowFixtures.Agent("ci", background: true, kind: "task", title: "build", input: "npm run build", figures: new GraphAgentFigures(Stamp(0)));
            var request = FlowFixtures.Run("r", agents: [scout, done, build], effort: "high", configuredAdvisor: "opus",
                advisor: new GraphAdvisor(Calls: [Call("srvtoolu_1", at: 2, until: 10)]));
            var runs = new List<MightyGraphRun> { request };
            var layout = FlowFixtures.Layout(runs);
            var blocks = FlowCopy.Blocks(layout, runs, true, output: id => id == "ci" ? ["compiling", "[exited with code 0]"] : []);
            var work = blocks[Node(request, MightyGraphLayout.WorkSuffix)].Role!;
            Check(work.Name == "lead" && work.Ink == "taskText" && work.Effort == "High" && work.EffortHelp == "사고 강도 · High", "the 실행 block is the lead with its effort");
            var scoutCopy = blocks[Node(request, "agent:scout")];
            Check(scoutCopy.Role is { Name: "subagent", Ink: "agentText", TypeChip: "Explore" }, "a running sub-agent's identity");
            var card = scoutCopy.Card!;
            Check(card.Title == "scout" && card.Identity == "Explore · Haiku · 요청" && card.Running && card.Status == "running", "its card's words");
            SameList(card.Before, ["실행 중"], "before its time");
            Check(card.StartedAt == Date(1).ToUnixTimeMilliseconds() && card.EndedAt is null, "its start for the page's clock");
            Check(card.CopyNodeId is null && card.ActivateHelp == "블록으로 펼치기" && card.Ink == "agentText" && !card.IsTask, "it opens in place");
            var doneCopy = blocks[Node(request, "agent:done")];
            Check(doneCopy.Role is { Name: "subagent" } && doneCopy.Card is null, "an ended foreground sub-agent is its block, no card");
            var task = blocks[Node(request, "agent:ci")];
            Check(task.Role is { Name: "task", Ink: "ink2" } && task.Role.Marks.Count == 1 && task.Role.Marks[0].Name == "background" && task.Role.Marks[0].Help == "백그라운드에서 실행", "a command's identity");
            Check(task.Card is { IsTask: true, Identity: "작업 · npm run build", Activity: "compiling", Ink: "ink2" }, "its card");
            var line = blocks[Node(request, MightyGraphLayout.DelegateSuffix + FlowDelegation.MainKey)].Line!;
            Same(line.Head, "3개 병렬", "the line's head");
            SameList(line.Parts.Select(p => p.Text + "/" + p.Ink), ["하위 에이전트 2/agentText", "작업 1/ink2"], "each kind in its ink");
            Check(line.Model is null && line.Progress == "2/3 실행 중" && line.Text == "3개 병렬 · 하위 에이전트 2 · 작업 1 · 2/3 실행 중", "the rest of it");
            var back = blocks[Node(request, MightyGraphLayout.ReturnSuffix)].Back!;
            Check(back.Text == "병렬 2/3 실행 중 · 보고를 기다리는 중" && back.Waiting, "the way back");
            var band = blocks[Node(request, FlowAdvisor.Suffix)].Advisor!;
            Check(band.Header == "조언자 · Opus · 요청 · 대기 중" && band.State == "대기 중" && !band.Consulting && band.Calls == 1 && band.Totals == "호출 1", "the band's head and totals");
            Check(band.Rows.Count == 1 && band.Rows[0].Caller == "메인" && band.Rows[0].Ink == "taskText" && band.Rows[0].CallerNodeId == Node(request, MightyGraphLayout.WorkSuffix), "its row");
            Check(band.Rows[0].Line == "파일 변경 전 · 조언 전달 · 내용 비표시" || band.Rows[0].Line == "작업 중 · 조언 전달 · 내용 비표시", "where and how: " + band.Rows[0].Line);
            Check(band.Rows[0].When.EndsWith(" · 8초", StringComparison.Ordinal) && band.Rows[0].Help.Contains("조언을 구한 블록 보기", StringComparison.Ordinal), "when and how long; what a click does");
            Check(band.Note == "조언은 암호화되어 Claude에게만 전달됩니다. 내용은 표시되지 않습니다." && band.HidesText && band.Empty is null, "the note");

            var plainLayout = MightyGraphLayout.Make(MightyGraphSupport.ShownRuns(runs), "", true, new HashSet<string>(), splitsExecution: true);
            var plainCopy = FlowCopy.Blocks(plainLayout, runs, false);
            Check(plainCopy[Node(request, MightyGraphLayout.WorkSuffix)].Role is { Name: "lead" }, "without the role view the lead still wears its ink");
            Check(plainCopy[Node(request, "agent:scout")] is { Role.Name: "subagent", Card: null }, "and a sub-agent its own, with no card");
            Check(!plainCopy.Values.Any(b => b.Line is not null || b.Back is not null || b.Advisor is not null) && !plainCopy.ContainsKey(Node(request, "agent:ci")), "no line, way back, band or command block");

            var log = FlowCopy.LogOf(runs, false)!;
            Same(log.StatusText, "Sonnet 5.5 실행 중 · 하위 에이전트 1/2 실행 중 · 작업 1개 실행 중 · 조언자 대기", "the status line");
            SameList(log.Status.Select(p => p.Ink), ["taskText", "agentText", "ink2", "steerText"], "each part in its role's ink");
            SameList(log.Rows.Select(r => r.Who + " | " + r.What), ["scout | 시작 · Explore · Haiku · 요청", "조언자 · Opus · 요청 | 메인의 요청으로 검토 시작", "조언자 · Opus · 요청 | 조언 전달 · 내용 비표시"], "the rows");
            Check(log.Rows[0].Ink == "agentText" && log.Rows[1].Ink == "steerText" && log.Rows[0].NodeId == Node(request, "agent:scout") && log.Rows.All(r => r.Clock.Length == 8) && !log.Collapsed, "their inks, blocks and clocks");
            var collapsed = FlowCopy.LogOf(runs, true)!;
            Check(collapsed.Collapsed && collapsed.Rows.Count == 0 && collapsed.StatusText == log.StatusText, "folded: the status line alone");
            Same(FlowCopy.LogOf([], false), null, "no request, no strip");
        });
        return Task.CompletedTask;
    }
}
