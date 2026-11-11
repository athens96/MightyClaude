using System.Text.Json;
using MightyCode.Core;

/// <summary>
/// The role view's diagram, checked against the cases of the macOS suite: FlowLinesTests (the delegate line and the return node, where the layout puts
/// them, the follow camera), MightyGraphFanOutTests (every sub-agent and background command in the fan-out row, a running one a card, what a card draws
/// under it, the tracker's figures for the cards) and FlowTaskTests (background commands in the row, the line, the way back and the status line). A
/// macOS check of what only the Mac computes (its live-edge routes, the advisor's dotted routes, SwiftUI) is left out where noted: the web page draws
/// those (native/windows/web/src/mighty/flow.ts).
/// </summary>
internal static class FlowLayoutVerification
{
    private static void Check(bool value, string why) => FlowFixtures.Check(value, why);
    private static void Same<T>(T actual, T expected, string why) => FlowFixtures.Same(actual, expected, why);
    private static void SameList<T>(IEnumerable<T> actual, IEnumerable<T> expected, string why) => FlowFixtures.SameList(actual, expected, why);
    private static string Node(MightyGraphRun run, string suffix) => FlowFixtures.Node(run, suffix);
    private static MightyGraphLayout.Node? Find(MightyGraphLayout layout, string id) => FlowFixtures.Find(layout, id);
    private static MightyGraphLayout.Node Must(MightyGraphLayout layout, string id) => FlowFixtures.Must(layout, id);
    private static string Agent(MightyGraphRun run, string id) => Node(run, "agent:" + id);
    private static string Line(MightyGraphRun run, string key = FlowDelegation.MainKey) => Node(run, MightyGraphLayout.DelegateSuffix + key);
    private const double LineDrop = MightyGraphLayout.DelegateGap + FlowDelegation.Height + MightyGraphLayout.DelegateRowGap;

    /// <summary>A run as FlowLinesTests and MightyGraphFanOutTests build it: no responses, its result's answer once it completed.</summary>
    private static MightyGraphRun Plain(string id, List<MightyGraphAgent>? agents = null, string status = "running", bool notice = false) => new()
    {
        Id = id, Input = "요청 " + id, Status = status, Agents = agents ?? [], FinalOutput = status == "completed" ? "결과 " + id : null, NoticeRun = notice ? true : null,
    };
    /// <summary>A sub-agent as FlowLinesTests builds it: its identity and, when it answered, its own response's model.</summary>
    private static MightyGraphAgent Sub(string id, string? parent = null, string status = "running", bool background = false, string? kind = null, string? source = null,
        string? requested = null, string? resolved = null, string? answered = null, string? type = null) =>
        FlowFixtures.Agent(id, parent, status, background, kind, source, identity: new GraphAgentIdentity(type, requested, resolved),
            records: answered is null ? null : [FlowFixtures.Answered(id + "-m", answered)]);
    private static MightyGraphLayout PlainLayout(IReadOnlyList<MightyGraphRun> runs, bool running = true) =>
        MightyGraphLayout.Make(MightyGraphSupport.ShownRuns(runs), "", running, new HashSet<string>(), splitsExecution: true);

    // ── FlowLinesTests ──

    internal static Task TheDelegateLineSaysHowManyOnWhichModelAndHowManyRun()
    {
        FlowFixtures.Korean(() =>
        {
            var three = new List<MightyGraphAgent> { Sub("a", answered: "claude-haiku-5-5"), Sub("b", answered: "claude-haiku-5-5"), Sub("c", status: "completed", answered: "claude-haiku-5-5") };
            var line = FlowDelegation.Of(three)!;
            Check(line.Count == 3 && line.Running == 2, "three, two running");
            Same(line.Head, "하위 에이전트 3개에 위임", "the head");
            Same(line.Model, "Haiku 5.5", "the shared model");
            Same(line.Progress, "2/3 실행 중", "how many run");
            Same(line.Text, "하위 에이전트 3개에 위임 · Haiku 5.5 · 2/3 실행 중", "the whole line");
            foreach (var agent in three) agent.Status = "completed";
            var ended = FlowDelegation.Of(three)!;
            Same(ended.Progress, null, "all ended");
            Same(ended.Text, "하위 에이전트 3개에 위임 · Haiku 5.5", "without the count");
            Same(FlowDelegation.Of([Sub("solo", status: "completed")])!.Text, "하위 에이전트 1개에 위임", "one, no model");
            Same(FlowDelegation.Of([]), null, "none");

            Same(FlowDelegation.Of([Sub("a", answered: "claude-haiku-5-5"), Sub("b", answered: "claude-opus-5-5")])!.Model, "모델 2종", "two models");
            Same(FlowDelegation.Of([Sub("a", resolved: "claude-haiku-5-5"), Sub("b", answered: "claude-haiku-5-5")])!.Model, "Haiku 5.5", "the resolved model stands in");
            Same(FlowDelegation.Of([Sub("a", answered: "claude-haiku-5-5"), Sub("b", requested: "haiku")])!.Model, "Haiku 5.5", "an alias of the named model's family");
            Same(FlowDelegation.Of([Sub("a", answered: "claude-haiku-5-5"), Sub("b", requested: "opus")])!.Model, "모델 2종", "an alias of another family");
            Same(FlowDelegation.Of([Sub("a", requested: "haiku"), Sub("b", requested: "haiku")])!.Model, "Haiku · 요청", "nothing named yet");
            Same(FlowDelegation.Of([Sub("a", requested: "haiku"), Sub("b", requested: "sonnet")])!.Model, "모델 2종", "two aliases");
            Same(FlowDelegation.Of([Sub("a", answered: "claude-haiku-5-5"), Sub("b")])!.Model, "Haiku 5.5", "one that says nothing");
            var definitions = new Dictionary<string, AgentDefinition> { ["reviewer"] = new("reviewer", Model: "sonnet"), ["scout"] = new("scout", Model: "inherit") };
            Same(FlowDelegation.Of([Sub("a", type: "reviewer")], definitions)!.Model, "Sonnet · 요청", "a definition's model");
            Same(FlowDelegation.Of([Sub("a", type: "scout")], definitions)!.Model, null, "inherit asks for none");
            Same(FlowDelegation.Of([Sub("a", type: "reviewer")])!.Model, null, "without its definition");
        });

        var agents = new List<MightyGraphAgent> { Sub("first"), Sub("bg", background: true), Sub("nested", "first"), Sub("deep", "nested"), Sub("note", kind: "compact"),
            Sub("task", background: true, kind: "task"), Sub("orphan", "ghost"), Sub("self", "self") };
        var shown = Plain("r", agents).Shown;
        SameList(FlowDelegation.Children(FlowDelegation.MainKey, shown).Select(a => a.Id), ["first", "bg", "task", "orphan", "self"], "the main session's, in launch order");
        SameList(FlowDelegation.Children("first", shown).Select(a => a.Id), ["nested"], "first's");
        SameList(FlowDelegation.Children("nested", shown).Select(a => a.Id), ["deep"], "nested's");
        Same(FlowDelegation.Children("deep", shown).Count, 0, "deep's");
        Same(FlowDelegation.Children(FlowDelegation.MainKey, Plain("r:notice:1", [Sub("copy", status: "completed", background: true, source: "bg")], "completed", true)).Count, 0, "a notice run delegated nothing");

        FlowFixtures.Korean(() =>
        {
            var waiting = FlowReturn.Of(Plain("r", [Sub("a"), Sub("b", status: "completed"), Sub("c", "a")]))!;
            Check(waiting.Total == 3 && waiting.Running == 2 && waiting.Waiting, "two of three run");
            Same(waiting.Text, "하위 에이전트 2/3 실행 중 · 보고를 기다리는 중", "waiting for reports");
            var back = FlowReturn.Of(Plain("r", [Sub("a", status: "completed"), Sub("b", status: "completed"), Sub("c", "a", "completed")]))!;
            Check(!back.Waiting, "all ended");
            Same(back.Text, "메인으로 복귀 · 검토 중", "back to the main session");
            Same(FlowReturn.Of(Plain("r")), null, "no delegation");
            Same(FlowReturn.Of(Plain("r", [Sub("note", kind: "compact"), Sub("ask", kind: "question")])), null, "no sub-agent");
            Same(FlowReturn.Of(Plain("r:notice:1", [Sub("copy", status: "completed", background: true, source: "x")], notice: true)), null, "a notice run");
            Same(FlowReturn.Of(Plain("r", [Sub("bg", background: true)]).Shown)?.Total, 1, "a background sub-agent counts through the shown run");
        });
        return Task.CompletedTask;
    }

    internal static Task TheDelegateLineAndTheReturnNodeStandInTheFlow()
    {
        var request = Plain("r", [Sub("a"), Sub("b", background: true), Sub("c")]);
        var graph = FlowFixtures.Layout([request]);
        // The main session's line hangs from the request: the 실행 card stands where the answer will come.
        var work = Must(graph, Node(request, "request"));
        var line = Must(graph, Line(request));
        Same(line.Kind, MightyGraphLayout.DelegateKind, "a delegate line");
        Check(line.Frame.W == FlowDelegation.Width && line.Frame.H == FlowDelegation.Height, "its size");
        Same(line.Frame.Y, work.Frame.MaxY + 18, "18 under the block");
        Check(Math.Abs(line.Frame.MidX - work.Frame.MidX) < 0.5, "centred under it");
        Check(MightyGraphLayout.IsFixed(line) && !MightyGraphLayout.IsResizable(line) && !MightyGraphLayout.IsAuxiliary(line.Kind) && !line.IsCompact, "a label: fixed, never resized");
        Check(graph.Edges.Any(e => e.Source == work.Id && e.Target == line.Id), "the block's branch goes into it");
        foreach (var child in new[] { "a", "b", "c" })
        {
            var card = Must(graph, Agent(request, child));
            Check(graph.Edges.Any(e => e.Source == line.Id && e.Target == card.Id), "the line fans out to " + child);
            Check(!graph.Edges.Any(e => e.Source == work.Id && e.Target == card.Id), "not the block to " + child);
            Same(card.Frame.Y, line.Frame.MaxY + 26, "the row 26 under the line");
        }
        foreach (var other in graph.Nodes.Where(n => n.Id != line.Id)) Check(!FlowFixtures.Overlap(other.Frame, line.Frame), "nothing overlaps the line: " + other.Id);

        var quiet = FlowFixtures.Layout([Plain("r", [Sub("note", status: "completed", kind: "compact")])]);
        Check(!quiet.Nodes.Any(n => n.Kind is MightyGraphLayout.DelegateKind or MightyGraphLayout.ReturnKind), "no line and no return node without a sub-agent");
        var single = Plain("r", [Sub("a")]);
        SameList(PlainLayout([single]).Nodes.Select(n => n.Kind), ["request", MightyGraphLayout.WorkKind, "agent"], "without a fan-out, as before");
        SameList(MightyGraphLayout.Make([single], "", true, new HashSet<string>()).Nodes.Select(n => n.Kind), ["request", "agent"], "the shared vectors' layout");

        // An opened sub-agent that delegated has its own line.
        var nested = Plain("r", [Sub("lead"), Sub("child", "lead"), Sub("note", "lead", "completed", kind: "compact")]);
        var leadID = Agent(nested, "lead");
        Same(Find(FlowFixtures.Layout([nested]), Line(nested, "lead")), null, "folded: no line");
        var opened = FlowFixtures.Layout([nested], opened: [leadID]);
        var lead = Must(opened, leadID);
        var own = Must(opened, Line(nested, "lead"));
        Same(own.Frame.Y, lead.Frame.MaxY + 18, "under the opened block");
        Check(Math.Abs(own.Frame.MidX - lead.Frame.MidX) < 0.5, "centred under it");
        foreach (var child in new[] { "child", "note" }) Check(opened.Edges.Any(e => e.Source == own.Id && e.Target == Agent(nested, child)), "its row hangs off it: " + child);
        Check(Find(opened, Line(nested)) is not null, "the main line still stands");
        var narrow = FlowFixtures.Layout([nested], opened: [leadID], sizes: new() { [leadID] = new GraphBlockSize(300, 200) });
        var narrowLine = Must(narrow, Line(nested, "lead"));
        foreach (var other in narrow.Nodes.Where(n => n.Id != narrowLine.Id)) Check(!FlowFixtures.Overlap(other.Frame, narrowLine.Frame), "a narrow block holds its line: " + other.Id);

        // The main session's card stands where the result will, until it is there; no return node.
        var running = Plain("r", [Sub("a"), Sub("b")]);
        var flow = FlowFixtures.Layout([running]);
        Same(Find(flow, Node(running, MightyGraphLayout.ReturnSuffix)), null, "no return node");
        var back = Must(flow, Node(running, MightyGraphLayout.WorkSuffix));
        Same(back.Kind, MightyGraphLayout.WorkKind, "the 실행 block");
        Check(back.IsCompact && back.Frame.W == FlowSteps.CardWidth && back.Frame.H == FlowSteps.CardHeight, "as its card");
        Check(MightyGraphLayout.IsFixed(back) && !MightyGraphLayout.IsResizable(back) && !MightyGraphLayout.IsAuxiliary(back.Kind), "fixed");
        var cards = new[] { "a", "b" }.Select(c => Must(flow, Agent(running, c))).ToList();
        foreach (var card in cards) Check(flow.Edges.Any(e => e.Source == card.Id && e.Target == back.Id && e.Joins), "the branches converge into it");
        Same(back.Frame.Y, cards[0].Frame.MaxY + 32, "a row gap under the cards");
        Check(Math.Abs(back.Frame.MidX - Must(flow, Node(running, "request")).Frame.MidX) < 0.5, "on the centreline");
        Same(Find(flow, Node(running, "result")), null, "no result yet");
        running.Status = "completed"; running.FinalOutput = "done";
        foreach (var agent in running.Agents) agent.Status = "completed";
        var done = FlowFixtures.Layout([running], running: false);
        Same(Find(done, Node(running, MightyGraphLayout.WorkSuffix)), null, "the result takes its place");
        var result = Must(done, Node(running, "result"));
        foreach (var child in new[] { "a", "b" }) Check(done.Edges.Any(e => e.Source == Agent(running, child) && e.Target == result.Id && e.Joins), "into the result: " + child);

        // The 알림 대기 block takes it too; a plan waiting for its answer stands there instead; a notice run has neither line nor way back.
        var awaiting = Plain("r", [Sub("bg", background: true)], "completed");
        var awaited = FlowFixtures.Layout([awaiting]);
        Same(Find(awaited, Node(awaiting, MightyGraphLayout.ReturnSuffix)), null, "the 알림 대기 block stands there");
        var awaitingResult = Must(awaited, Node(awaiting, "result"));
        Check(awaited.Edges.Any(e => e.Source == Agent(awaiting, "bg") && e.Target == awaitingResult.Id && e.Joins), "the card leads into it");
        var shownAwaiting = MightyGraphSupport.ShownRuns([awaiting]);
        Check(BackgroundNoticeRuns.AwaitingNotice(shownAwaiting[0], shownAwaiting) is not null, "it awaits the notice");
        var turn = Plain("r", [Sub("bg", background: true)]);
        Check(FlowFixtures.Layout([turn]).Edges.Any(e => e.Source == Agent(turn, "bg") && e.Target == Node(turn, MightyGraphLayout.WorkSuffix)), "while the turn runs, into the main session's card");
        var planned = Plain("r", [Sub("a", status: "completed")]);
        var planLayout = FlowFixtures.Layout([planned], plan: "r");
        Check(Find(planLayout, Node(planned, MightyGraphLayout.ReturnSuffix)) is null && Find(planLayout, Node(planned, MightyGraphLayout.PlanSuffix)) is not null, "the plan stands there");
        var ended = Plain("r", [Sub("bg", status: "completed", background: true)], "completed");
        var notice = Plain("r:notice:1", [Sub("r:notice:1:bg", status: "completed", background: true, source: "bg")], notice: true);
        var both = FlowFixtures.Layout([ended, notice]);
        Check(Find(both, Node(notice, MightyGraphLayout.ReturnSuffix)) is null && Find(both, Line(notice)) is null, "a notice run has neither");
        Check(Find(both, Line(ended)) is not null, "the request keeps its line");
        return Task.CompletedTask;
    }

    internal static Task TheCameraFollowsTheReturnNodeAndThenTheResult()
    {
        (double W, double H) viewport = (900, 600);
        var request = Plain("r", [Sub("a"), Sub("b")]);
        var runs = MightyGraphSupport.ShownRuns([request]);
        var graph = MightyGraphLayout.Make(runs, "", true, new HashSet<string>(), splitsExecution: true, fanOut: new MightyGraphLayout.FanOut());
        var followed = MightyGraphFollow.TargetID(graph, runs);
        Same(followed, Node(request, MightyGraphLayout.WorkSuffix), "the main session's card, never a delegate line");
        var back = Must(graph, followed!);
        var watch = new MightyGraphFollow.Watch();
        var camera = MightyGraphFollow.CameraOffset(back.Frame, viewport, 1, false, false, true);
        watch.Moved(followed, back.Frame, camera, viewport, 1);
        Check(watch.Following, "following");
        Same(watch.Observe(followed, back.Frame, camera, viewport, 1), null, "nothing moved");
        request.Status = "completed"; request.FinalOutput = "done";
        foreach (var agent in request.Agents) agent.Status = "completed";
        var doneRuns = MightyGraphSupport.ShownRuns([request]);
        var done = MightyGraphLayout.Make(doneRuns, "", false, new HashSet<string>(), splitsExecution: true, fanOut: new MightyGraphLayout.FanOut());
        var next = MightyGraphFollow.TargetID(done, doneRuns);
        Same(next, Node(request, "result"), "then the result");
        var result = Must(done, next!);
        var moved = watch.Observe(next, result.Frame, camera, viewport, 1) ?? throw new InvalidOperationException("the camera should move to the result");
        Check(Math.Abs(moved.Y + result.Frame.MaxY - (viewport.H - MightyGraphFollow.Margin)) < 0.001, "its bottom right above the composer");
        Same(moved.X, camera.X, "the reader's x stays");
        var away = new MightyGraphFollow.Watch();
        away.Moved(followed, back.Frame, (camera.X, camera.Y + 2_000), viewport, 1);
        Check(!away.Following, "a reader who scrolled away");
        Same(away.Observe(next, result.Frame, (camera.X, camera.Y + 2_000), viewport, 1), null, "is left there");

        // A re-aim goes to the request; the stranded rule reads frames alone; no two nodes overlap.
        var single = Plain("r", [Sub("a")]);
        var layout = FlowFixtures.Layout([single]);
        var ids = layout.Nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        Same(MightyGraphCamera.ReaimAnchor("r", null, ids), MightyGraphCamera.Anchor.Reaim(Node(single, "request"), true), "a re-aim goes to the request");
        Same(layout.NewestRequestTargetID([single]), Node(single, MightyGraphLayout.WorkSuffix), "the newest request's 실행 block");
        var stranded = new MightyGraphCamera.StrandedWatch();
        var frames = layout.Nodes.Select(n => (n.Id, n.Frame)).ToList();
        Same(stranded.Observe(frames, (0, 0), 1, viewport, "r", true), null, "nobody stranded");
        Same(stranded.Observe(frames, (0, 0), 1, viewport, "r", true), null, "still nobody");
        for (var i = 0; i < layout.Nodes.Count; i++)
            for (var j = i + 1; j < layout.Nodes.Count; j++)
                Check(!FlowFixtures.Overlap(layout.Nodes[i].Frame, layout.Nodes[j].Frame), layout.Nodes[i].Id + " × " + layout.Nodes[j].Id);
        // (macOS theEdgesIntoWhatStillRunsAreLive reads MightyGraphLayout.liveFlowIDs and routes(into:), which the web page draws: flow.ts liveFlowIds.)
        return Task.CompletedTask;
    }

    // ── MightyGraphFanOutTests ──

    private static MightyGraphAgent Fan(string id, string? parent = null, string status = "completed", bool background = false, string? kind = null, string? source = null) =>
        FlowFixtures.Agent(id, parent, status, background, kind, source);

    internal static Task AShownRunCarriesWhatOnlyTheFlowDraws()
    {
        var request = Plain("r", [Fan("fg"), Fan("bg", status: "running", background: true), Fan("bg-child", "bg", "running"), Fan("task", status: "running", background: true, kind: "task"),
            Fan("task-child", "task"), Fan("late")], "completed");
        var shown = request.Shown;
        SameList(shown.Agents.Select(a => a.Id), ["fg", "late"], "every other view draws what it drew");
        SameList(BackgroundNoticeRuns.ShownAgents(request).Select(a => a.Id), ["fg", "late"], "shownAgents");
        SameList(shown.FlowAgents.Select(a => a.Id), ["fg", "late", "bg", "bg-child", "task", "task-child"], "the flow draws them too");
        SameList(shown.FlowAgents.Take(shown.Agents.Count), shown.Agents, "the same blocks at the same indexes first");
        SameList(Enumerable.Range(0, shown.FlowAgents.Count).Select(shown.FlowOrder), [0, 5, 1, 2, 3, 4], "each where it was launched");
        SameList(BackgroundNoticeRuns.FanOut(request), [1, 2, 3, 4], "the fan-out");
        Check(ReferenceEquals(shown.Shown, shown), "shown again, it is the same run");
        SameList(shown.Shown.FlowAgents.Select(a => a.Id), ["fg", "late", "bg", "bg-child", "task", "task-child"], "with the same flow");

        var quiet = Plain("r", [Fan("a"), Fan("b", "a")], "completed");
        SameList(quiet.Shown.FlowAgents, quiet.Agents, "a run that hides nothing");
        Same(quiet.FlowOrder(1), 1, "its order");
        Same(BackgroundNoticeRuns.FanOut(quiet).Count, 0, "no fan-out");
        var notice = Plain("r:notice:1", [Fan("copy", background: true, source: "bg")], "completed", true);
        SameList(notice.Shown.FlowAgents.Select(a => a.Id), ["copy"], "a notice run's own");
        Same(BackgroundNoticeRuns.FanOut(notice).Count, 0, "none for a notice run");
        SameList(Plain("t", [Fan("task", background: true, kind: "task")], "completed").Shown.FlowAgents.Select(a => a.Id), ["task"], "commands alone fan out");

        var status = Plain("r", [Fan("bg", status: "running", background: true)], "completed");
        var runs = MightyGraphSupport.ShownRuns([status]);
        Check(runs[0].Settled && MightyGraphLayout.Finished(runs[0]) && MightyGraphLayout.HasResult(runs[0]), "it settled, with its result");
        Same(MightyGraphSupport.BackgroundWaiting(runs[0]), 1, "waiting on one");
        Check(BackgroundNoticeRuns.AwaitingNotice(runs[0], runs) is not null, "awaiting its notice");
        Same(MightyTimeline.Groups([status])[0].Rows.Count, 2, "the timeline leaves the block out");
        Same(MightyStructure.Clusters(status).Count, 0, "so does the pipeline");
        Same(MobileMightySupport.Blocks(status, 1).Count, 1, "and the phone");
        return Task.CompletedTask;
    }

    internal static Task TheFanOutRowHoldsCardsAndBlocks()
    {
        var mixed = Plain("r", [Fan("fg", status: "running"), Fan("bg", status: "running", background: true)]);
        var before = PlainLayout([mixed]);
        Same(Find(before, Agent(mixed, "bg")), null, "without a fan-out no background block");
        var fg = Must(before, Agent(mixed, "fg"));
        Check(fg.Frame.W == 360 && fg.Frame.H == 240 && !fg.IsCompact && MightyGraphLayout.IsResizable(fg) && !MightyGraphLayout.IsFixed(fg), "the block as before");

        var single = Plain("r", [Fan("bg", status: "running", background: true)]);
        var graph = FlowFixtures.Layout([single]);
        var card = Must(graph, Agent(single, "bg"));
        Check(card.IsCompact && card.Frame.W == FlowCard.Width && card.Frame.H == FlowCard.Height, "a card");
        Check(!MightyGraphLayout.IsResizable(card) && MightyGraphLayout.IsFixed(card), "never resized, fixed");
        var work = Must(graph, Node(single, "request"));
        Check(graph.Edges.Any(e => e.Source == work.Id && e.Target == Line(single)) && graph.Edges.Any(e => e.Source == Line(single) && e.Target == card.Id), "through the line");
        Same(card.Frame.Y, work.Frame.MaxY + LineDrop, "under the line");
        Check(Math.Abs(card.Frame.MidX - work.Frame.MidX) < 0.5, "centred");
        // (The Mac also checks the edge into a running card is live, MightyGraphLayout.liveNodeIDs(fanOut:): the web page's flow.ts liveFlowIds.)

        var finished = Plain("r", [Fan("bg", background: true)], "completed");
        var stays = FlowFixtures.Layout([finished]);
        var ended = Must(stays, Agent(finished, "bg"));
        Check(ended.IsCompact && ended.Frame.W == FlowCard.Width, "a finished background card stays");
        var result = Must(stays, Node(finished, "result"));
        Check(stays.Edges.Any(e => e.Source == ended.Id && e.Target == result.Id && e.Joins), "it leads into the result");
        Same(result.Frame.Y, ended.Frame.MaxY + 32, "a row gap under it");

        var foreground = Plain("r", [Fan("fg", status: "running")]);
        Check(Must(FlowFixtures.Layout([foreground]), Agent(foreground, "fg")).IsCompact, "a running foreground sub-agent is a card");
        foreground.Agents[0].Status = "completed";
        var endedID = Agent(foreground, "fg");
        Check(Must(FlowFixtures.Layout([foreground]), endedID).IsCompact, "and a card once it ends");
        // Opened, it is the block the layout always drew for it, with its saved size and its opened height.
        var block = Must(FlowFixtures.Layout([foreground], opened: [endedID]), endedID);
        Check(!block.IsCompact && MightyGraphLayout.IsResizable(block) && !MightyGraphLayout.IsFixed(block) && block.Frame.W == 360 && block.Frame.H == 240, "its block once opened");
        var saved = Must(FlowFixtures.Layout([foreground], opened: [endedID], sizes: new() { [endedID] = new GraphBlockSize(420, 300) }), endedID);
        Check(saved.Frame.W == 420 && saved.Frame.H == 300, "its saved size is back");
        Same(Must(FlowFixtures.Layout([foreground], opened: [endedID], expanded: [endedID]), endedID).Frame.H, 480d, "its opened height");

        var cardOnly = Plain("r", [Fan("fg", status: "running")]);
        var cardID = Agent(cardOnly, "fg");
        var ignoring = Must(FlowFixtures.Layout([cardOnly], expanded: [cardID], sizes: new() { [cardID] = new GraphBlockSize(420, 300) }), cardID);
        Check(ignoring.Frame.W == FlowCard.Width && ignoring.Frame.H == FlowCard.Height, "a card ignores a saved size and the opened height");

        var pair = Plain("r", [Fan("fg", status: "running"), Fan("bg", status: "running", background: true)]);
        string fgID = Agent(pair, "fg"), bgID = Agent(pair, "bg");
        var openedCard = FlowFixtures.Layout([pair], opened: [bgID], sizes: new() { [bgID] = new GraphBlockSize(400, 260) });
        Check(Must(openedCard, fgID).IsCompact, "the other stays a card");
        var openedBlock = Must(openedCard, bgID);
        Check(!openedBlock.IsCompact && MightyGraphLayout.IsResizable(openedBlock) && openedBlock.Frame.W == 400 && openedBlock.Frame.H == 260, "an opened card is its block, saved size and all");
        Same(Must(openedCard, fgID).Frame.Y, openedBlock.Frame.Y, "sharing the row's top");

        var agents = new List<MightyGraphAgent> { Fan("lead", status: "running"), Fan("child", "lead", "running"), Fan("grand", "child", "running"), Fan("note", "lead", kind: "compact") };
        var deep = Plain("r", agents);
        var folded = FlowFixtures.Layout([deep]);
        Check(Must(folded, Agent(deep, "lead")).IsCompact, "the lead is a card");
        foreach (var hidden in new[] { "child", "grand", "note" }) Same(Find(folded, Agent(deep, hidden)), null, "nothing under the card: " + hidden);
        SameList(folded.Nodes.Select(n => n.Id).Order(StringComparer.Ordinal),
            new[] { Node(deep, "request"), Line(deep), Agent(deep, "lead"), Node(deep, MightyGraphLayout.WorkSuffix) }.Order(StringComparer.Ordinal), "four nodes, no stray root");
        Same(FlowCard.Nested(agents[0], deep), 2, "the card counts them");
        var leadID = Agent(deep, "lead");
        var opened = FlowFixtures.Layout([deep], opened: [leadID]);
        Check(!Must(opened, leadID).IsCompact, "opened");
        var childCard = Must(opened, Agent(deep, "child"));
        Check(childCard.IsCompact && Find(opened, Agent(deep, "grand")) is null && !Must(opened, Agent(deep, "note")).IsCompact, "its running child a card, the compaction a block");
        Check(opened.Edges.Any(e => e.Source == leadID && e.Target == Line(deep, "lead")) && opened.Edges.Any(e => e.Source == Line(deep, "lead") && e.Target == childCard.Id), "its own line");
        Same(opened.Nodes.Count, 7, "seven nodes: no return node, the main session's card instead");

        var order = Plain("r", [Fan("first", status: "running"), Fan("second", status: "running", background: true), Fan("third", status: "running"), Fan("fourth", status: "running", background: true)]);
        var rowLayout = FlowFixtures.Layout([order]);
        var row = new[] { "first", "second", "third", "fourth" }.Select(id => Must(rowLayout, Agent(order, id))).ToList();
        SameList(row.Select(n => n.Frame.X), row.Select(n => n.Frame.X).Order(), "left to right in launch order");
        Same(row.Select(n => n.Frame.Y).Distinct().Count(), 1, "one row");
        for (var i = 1; i < row.Count; i++) Same(row[i].Frame.X - row[i - 1].Frame.MaxX, 20d, "the sibling gap");
        Check(Math.Abs((row[0].Frame.X + row[3].Frame.MaxX) / 2 - Must(rowLayout, Node(order, "request")).Frame.MidX) < 0.5, "centred under the request");
        SameList(MightyGraphSupport.ShownRuns([order])[0].FlowAgents.Select(a => a.Id), ["first", "third", "second", "fourth"], "the visible blocks first");

        var request = Plain("r", [Fan("bg", background: true)], "completed");
        var notice = Plain("r:notice:1", [Fan("r:notice:1:bg", background: true, source: "bg")], "completed", true);
        var copies = FlowFixtures.Layout([request, notice], running: false);
        var copy = Must(copies, Agent(notice, "r:notice:1:bg"));
        Check(copy.IsCompact && copy.Frame.W == FlowCard.Width && copy.Frame.H == FlowCard.Height, "a copy is a card too");
        var openedCopy = Must(FlowFixtures.Layout([request, notice], running: false, opened: [copy.Id]), copy.Id);
        Check(!openedCopy.IsCompact && openedCopy.Frame.W == 360 && openedCopy.Frame.H == 240, "opened, its full block");
        Check(Must(copies, Agent(request, "bg")).IsCompact, "the request keeps its card");
        var shown = MightyGraphSupport.ShownRuns([request, notice]);
        Same(FlowCard.CopyNodeID(shown[0].FlowAgents[0], shown), copy.Id, "which leads to the copy");

        var commands = Plain("r", [Fan("task", status: "running", background: true, kind: "task"), Fan("bg", status: "running", background: true), Fan("fg")]);
        var commandLayout = FlowFixtures.Layout([commands]);
        MightyGraphLayout.Node task = Must(commandLayout, Agent(commands, "task")), bg = Must(commandLayout, Agent(commands, "bg")), fgBlock = Must(commandLayout, Agent(commands, "fg"));
        Check(task.IsCompact && task.Frame.W == FlowCard.Width && bg.IsCompact && fgBlock.IsCompact, "the command and both agents are cards");
        Check(task.Frame.Y == bg.Frame.Y && task.Frame.Y == fgBlock.Frame.Y && task.Frame.MaxX < bg.Frame.X && bg.Frame.MaxX < fgBlock.Frame.X, "one row in launch order");
        Check(Find(commandLayout, Line(commands)) is not null && Find(commandLayout, Node(commands, MightyGraphLayout.WorkSuffix)) is not null, "the line and the main session's card");
        Check(!Must(FlowFixtures.Layout([commands], opened: [task.Id]), task.Id).IsCompact, "opened, its block");
        // (The edges into the running command's and agent's cards are live on the Mac, liveNodeIDs: the web page draws them, flow.ts liveFlowIds.)
        Check(!MightyGraphLayout.Make([commands.Shown], "", true, new HashSet<string>(), splitsExecution: true).Nodes.Any(n => n.Id == task.Id), "without a fan-out it is not there");

        var two = Plain("r", [Fan("one", status: "running", background: true), Fan("two", status: "running", background: true)]);
        var twoRuns = MightyGraphSupport.ShownRuns([two]);
        Same(MightyGraphFollow.TargetID(MightyGraphLayout.Make(twoRuns, "", true, new HashSet<string>(), splitsExecution: true, fanOut: new MightyGraphLayout.FanOut()), twoRuns),
            Node(two, MightyGraphLayout.WorkSuffix), "the main session's card under the cards is the lowest");
        two.Status = "completed"; two.FinalOutput = "launched";
        var endedRuns = MightyGraphSupport.ShownRuns([two]);
        var endedGraph = MightyGraphLayout.Make(endedRuns, "", true, new HashSet<string>(), splitsExecution: true, fanOut: new MightyGraphLayout.FanOut());
        Same(MightyGraphFollow.TargetID(endedGraph, endedRuns), Node(two, "result"), "then the 알림 대기 block");
        Same(endedGraph.NewestRequestTargetID(endedRuns), Node(two, "request"), "the request is the newest request's target");
        Same(MightyGraphCamera.ReaimAnchor(two.Id, null, endedGraph.Nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal)), MightyGraphCamera.Anchor.Reaim(Node(two, "request"), true), "a card is no re-aim target");
        return Task.CompletedTask;
    }

    internal static Task TheTrackersFiguresForTheCards()
    {
        var emitted = new List<ExecutionGraphNode>();
        var tracker = new ExecutionGraphTracker("run", "request", "claude", null, emitted.Add);
        void Send(object value) => tracker.Consume(JsonSerializer.SerializeToElement(value));
        ExecutionGraphNode Latest(string tool) => emitted.Last(n => n.Id == ExecutionGraphSupport.AgentNodeID("run", tool));
        Send(new { type = "assistant", uuid = "m1", timestamp = "2026-11-10T18:56:44.496Z", message = new { id = "m1", content = new object[] { new { type = "tool_use", id = "a", name = "Agent", input = new { description = "Count", prompt = "Go" } } } } });
        Same(Latest("a").Figures, new GraphAgentFigures("2026-11-10T18:56:44.496Z"), "started at its frame's time");
        Send(new { type = "user", timestamp = "2026-11-10T18:57:02.812Z", message = new { content = new object[] { new { type = "tool_result", tool_use_id = "a", content = "3" } } } });
        Same(Latest("a").State, "completed", "ended");
        Same(Latest("a").Figures, new GraphAgentFigures("2026-11-10T18:56:44.496Z", "2026-11-10T18:57:02.812Z"), "ended at its frame's time");
        Send(new { type = "user", timestamp = "2026-11-10T19:00:00.000Z", message = new { content = new object[] { new { type = "tool_result", tool_use_id = "a", content = "again" } } } });
        Same(Latest("a").Figures?.EndedAt, "2026-11-10T18:57:02.812Z", "a later word does not move its end");
        Send(new { type = "assistant", uuid = "m2", message = new { id = "m2", content = new object[] { new { type = "tool_use", id = "b", name = "Agent", input = new { description = "Live", prompt = "Go" } } } } });
        var live = Latest("b").Figures?.Started ?? throw new InvalidOperationException("no start");
        Check(Math.Abs((live - DateTimeOffset.UtcNow).TotalSeconds) < 60, "a frame without a time starts the block now");
        tracker.Finish("stopped");
        Check(Latest("b").State == "stopped" && Latest("b").Figures?.Ended is not null, "the process's end ends it, now");
        Check(emitted.Where(n => n.Kind == "main").All(n => n.Figures is null), "only a sub-agent block has figures");

        var counted = new List<ExecutionGraphNode>();
        var tools = new ExecutionGraphTracker("run", "request", "claude", null, counted.Add);
        void Tell(object value) => tools.Consume(JsonSerializer.SerializeToElement(value));
        int? Calls(string tool) => counted.Last(n => n.Id == ExecutionGraphSupport.AgentNodeID("run", tool)).Figures?.ToolCalls;
        object Child(object[] blocks, string id, string parent) => new { type = "assistant", uuid = id, parent_tool_use_id = parent, message = new { id, content = blocks } };
        Tell(new { type = "assistant", uuid = "m1", message = new { id = "m1", content = new object[] { new { type = "tool_use", id = "a", name = "Agent", input = new { description = "Count", prompt = "Go" } } } } });
        Same(Calls("a"), null, "none yet");
        var read = Child([new { type = "tool_use", id = "r1", name = "Read", input = new { file_path = "a.txt" } }], "c1", "a");
        Tell(read);
        Same(Calls("a"), 1, "one");
        Tell(read);
        Same(Calls("a"), 1, "the same message again is the same call");
        Tell(Child([new { type = "tool_use", id = "r2", name = "Read", input = new { file_path = "b.txt" } }, new { type = "tool_use", id = "r3", name = "Grep", input = new { pattern = "x" } }], "c2", "a"));
        Same(Calls("a"), 3, "two more");
        Tell(new { type = "system", subtype = "task_progress", task_id = "t", tool_use_id = "a", description = "Reading", usage = new { total_tokens = 100, tool_uses = 5, duration_ms = 10 } });
        Same(Calls("a"), 5, "the CLI's larger count");
        Tell(new { type = "system", subtype = "task_progress", task_id = "t", tool_use_id = "a", description = "Reading", usage = new { tool_uses = 2 } });
        Same(Calls("a"), 5, "not a smaller one");
        Tell(new { type = "assistant", uuid = "m4", message = new { id = "m4", content = new object[] { new { type = "tool_use", id = "b", name = "Agent", input = new { description = "Other", prompt = "Go" } } } } });
        Tell(new { type = "system", subtype = "task_progress", task_id = "u", tool_use_id = "b", description = "Reading", usage = new { tool_uses = 1 } });
        Tell(Child([new { type = "tool_use", id = "b-read", name = "Read", input = new { file_path = "a.txt" } }], "c3", "b"));
        Same(Calls("b"), 1, "a report before the message is not counted twice");
        Tell(new { type = "assistant", uuid = "m3", message = new { id = "m3", content = new object[] { new { type = "tool_use", id = "main-read", name = "Read", input = new { file_path = "c.txt" } } } } });
        Same(Calls("a"), 5, "the main session's calls are not a sub-agent's");
        return Task.CompletedTask;
    }

    internal static Task TheRecordedBackgroundAgentIsACardThatLeadsToItsCopy()
    {
        FlowFixtures.Korean(() =>
        {
            var tracker = new ExecutionGraphTracker("recorded", "probe", "claude", null, _ => { });
            var parser = new OutputParser("claude", (_, _) => { }, _ => { }, activityNamespace: "recorded", graphActivity: (value, tool) => tracker.Activity(value, tool));
            var snapshots = new List<List<MightyGraphRun>>();
            foreach (var line in FlowGraphVerification.Lines("claude-stream-subagents"))
            {
                tracker.Consume(JsonDocument.Parse(line).RootElement);
                parser.Parse(line);
                snapshots.Add([.. new[] { tracker.BuildRun(true) }.OfType<MightyGraphRun>(), .. tracker.BuildNoticeRuns(true)]);
            }
            parser.Flush();
            tracker.Finish("completed");
            var runs = new List<MightyGraphRun> { tracker.BuildRun()! }.Concat(tracker.BuildNoticeRuns()).ToList();
            var agents = runs[0].Agents.Where(a => a.IsSubagent).ToList();
            SameList(agents.Select(a => a.Title), ["count a", "count b and c"], "the sub-agents");
            SameList(agents.Select(a => a.Figures?.StartedAt), ["2026-11-10T18:56:31.912Z", "2026-11-10T18:56:44.496Z"], "each started at its call's frame");
            Check(agents.All(a => a.Figures?.Ended is not null), "each ended");
            SameList(agents.Select(a => a.Figures?.ToolCalls), [1, 2], "one call counted once");
            Check(agents.All(a => (a.Usage?.Total ?? 0) > 0), "their tokens");
            var liveCards = snapshots.Select(snapshot =>
            {
                var shownRun = MightyGraphSupport.ShownRuns(snapshot)[0];
                var agent = shownRun.FlowAgents.FirstOrDefault(a => a.Title == "count b and c");
                return agent is { Status: "running" } && agent.Figures?.ToolCalls == 1 ? FlowCard.Of(agent, shownRun) : null;
            }).OfType<FlowCard.Content>().ToList();
            var card = liveCards.LastOrDefault() ?? throw new InvalidOperationException("the background agent never showed as a running card");
            Check(card.Running && card.Identity == "Explore · Haiku 5.5" && card.ToolCalls == 1 && (card.Tokens ?? 0) > 0, "a running card with its figures so far: " + card.Identity);
            SameList(card.Marks, [FlowMark.Background], "its mark");
            Check(card.Activity is { Length: > 0 }, "what it does");
            var shown = MightyGraphSupport.ShownRuns(runs);
            var ended = shown[0].FlowAgents.First(a => a.Title == "count b and c");
            Check(FlowCard.IsCompact(ended, false), "still the request's card");
            Same(FlowCard.Of(ended, shown[0]).Activity, "b.txt: 3", "its answer's first line");
            var graph = MightyGraphLayout.Make(shown, "", false, new HashSet<string>(), splitsExecution: true, fanOut: new MightyGraphLayout.FanOut());
            var copy = FlowCard.CopyNodeID(ended, shown) ?? throw new InvalidOperationException("no copy");
            Check(graph.Nodes.Any(n => n.Id == copy && n.IsCompact), "its copy is a card too");
            Check(graph.Nodes.Any(n => n.Id == Node(shown[0], "agent:" + ended.Id) && n.IsCompact), "its card");
            Check(FlowCard.IsCompact(shown[0].FlowAgents.First(a => a.Title == "count a"), false), "the foreground agent that ended is a card too");
        });
        return Task.CompletedTask;
    }

    // ── FlowTaskTests ──

    private static MightyGraphAgent Command(string id, string command = "python3 scripts/release.py ci", string? title = null, string status = "running", string? parent = null,
        int? started = null, int? ended = null, int? exitCode = null, string? source = null) =>
        FlowFixtures.Agent(id, parent, status, true, "task", source, title, command,
            figures: new GraphAgentFigures(started is { } s ? FlowFixtures.Stamp(s) : null, ended is { } e ? FlowFixtures.Stamp(e) : null, ExitCode: exitCode));
    private static MightyGraphAgent Helper(string id, string status = "running", bool background = false) =>
        FlowFixtures.Agent(id, status: status, background: background, records: [new GraphResponseRecord(id + "-m", "claude-haiku-5-5", new GraphTokenUsage(1), [])]);

    internal static Task BackgroundCommandsStandInTheRow()
    {
        var runs = FlowGraphVerification.ReplayParsed("claude-stream-background-commands", "recorded");
        var shown = MightyGraphSupport.ShownRuns(runs);
        Same(shown[0].Agents.Count, 0, "every other view leaves them out");
        SameList(shown[0].FlowAgents.Select(a => a.Title), ["count to three", "fail on purpose"], "the role view has both, in launch order");
        FlowFixtures.Korean(() =>
        {
            var cards = shown[0].FlowAgents.Select(a => FlowCard.Of(a, shown[0])).ToList();
            SameList(cards.Select(c => c.Identity), ["작업 · for i in 1 2 3; do echo line $i; sleep 1; done", "작업 · sleep 2; echo about to fail; exit 3"], "their command lines");
            SameList(cards.Select(c => FlowCard.Figures(c, DateTimeOffset.UtcNow)), ["완료 · 종료 코드 0 · 3초", "실패 · 종료 코드 3 · 2초"], "their exit codes and times");
            Check(cards.All(c => c.IsTask && c.Ink == FlowInk.Neutral && c.Marks.SequenceEqual([FlowMark.Background])), "the neutral ink and the background mark");
            Same(FlowCard.Of(shown[0].FlowAgents[0], shown[0], output: ["line 1", "line 2", "line 3", "", "[exited with code 0]"]).Activity, "line 3", "the last line it wrote");
            Same(FlowDelegation.Of(FlowDelegation.Children(FlowDelegation.MainKey, shown[0]))?.Text, "작업 2개 실행", "the line");
            Same(FlowReturn.Of(shown[0])?.Text, "메인으로 복귀 · 검토 중", "the way back");
            var graph = MightyGraphLayout.Make(shown, "", false, new HashSet<string>(), splitsExecution: true, fanOut: new MightyGraphLayout.FanOut());
            Check(shown[0].FlowAgents.All(a => graph.Nodes.FirstOrDefault(n => n.Id == Node(shown[0], "agent:" + a.Id))?.IsCompact == true), "both cards");
            Check(FlowCard.CopyNodeID(shown[0].FlowAgents[0], shown) is not null, "an ended command's card leads to its copy");

            var command = "python3 scripts/release.py ci\n--commit";
            var running = Command("ci", command, "CI 대기", started: 0);
            var request = FlowFixtures.Run("r", agents: [running]);
            var card = FlowCard.Of(running, request, output: ["native-macos 진행 중 · native-windows 진행 중"]);
            Same(card.Title, "CI 대기", "its description");
            Same(card.Identity, "작업 · python3 scripts/release.py ci", "its command's first line");
            Same(card.Activity, "native-macos 진행 중 · native-windows 진행 중", "its last line");
            Check(card.Running && card.StateWord == "실행 중", "running");
            Same(FlowCard.Figures(card, FlowFixtures.Date(138)), "실행 중 · 2분 18초", "its time");
            Same(card.Summary, "작업 · 백그라운드 · python3 scripts/release.py ci", "in one line");
            Check(card.Nested == 0 && card.ToolCalls is null && card.Tokens is null, "no tools, no tokens");
            Same(FlowCard.Of(running, request).Activity, null, "nothing written yet");
            Same(FlowCard.Figures(FlowCard.Of(Command("ci", command, status: "completed", started: 0, ended: 840, exitCode: 0), request), FlowFixtures.Date(9_999)), "완료 · 종료 코드 0 · 14분 0초", "ended");
            Same(FlowCard.Figures(FlowCard.Of(Command("ci", command, status: "error", started: 0, ended: 75, exitCode: 1), request), FlowFixtures.Date(9_999)), "실패 · 종료 코드 1 · 1분 15초", "failed");
            Same(FlowCard.Figures(FlowCard.Of(Command("x", status: "stopped", started: 0, ended: 5), request), FlowFixtures.Date(9)), "중단 · 5초", "no exit code named");
            Same(FlowCard.Figures(FlowCard.Of(Command("x", status: "completed", exitCode: 0), request), FlowFixtures.Date(9)), "완료 · 종료 코드 0", "an end the app never saw");
            var bare = new MightyGraphAgent { Id = "b", Title = "", Input = "", Status = "running", Kind = "task", Background = true };
            Same(FlowCard.Of(bare, request).Identity, "작업", "a block without a command");
            Same(FlowCard.Of(bare, request).Title, "백그라운드 작업", "and without a description");

            var who = FlowRoles.Task(Command("t", "eas build -p android --profile preview\nsecond line"))!;
            Check(who.Role == FlowRole.Task && who.Ink == FlowInk.Neutral && who.Marks.SequenceEqual([FlowMark.Background]), "a command's identity");
            Same(who.Summary, "작업 · 백그라운드 · eas build -p android --profile preview", "in one line");
            Check(who.TypeChip is null && who.ModelLabel is null && who.Effort is null, "no chip, model or effort");
            Same(FlowRoles.Task(Helper("a")), null, "a sub-agent is no command");
            Same(FlowRoles.RoleName(FlowRole.Task), "작업", "its role's name");

            var mixed = FlowDelegation.Of([Helper("release-notes", "completed", true), Command("ci"), Command("apk")])!;
            Same(mixed.Text, "3개 병렬 · 하위 에이전트 1 · 작업 2 · 2/3 실행 중", "both kinds");
            Check(mixed.Head == "3개 병렬" && mixed.Model is null && mixed.Progress == "2/3 실행 중", "its head, no model");
            SameList(mixed.Parts, [new FlowDelegation.Part(FlowRole.Subagent, "하위 에이전트 1"), new FlowDelegation.Part(FlowRole.Task, "작업 2")], "how many of each");
            Check(mixed.Count == 3 && mixed.Running == 2, "counts");
            Same(FlowDelegation.Of([Command("ci"), Command("apk", status: "completed")])?.Text, "작업 2개 실행 · 1/2 실행 중", "commands alone");
            Same(FlowDelegation.Of([Command("ci", status: "completed")])?.Text, "작업 1개 실행", "one ended command");
            Same(FlowDelegation.Of([Command("ci")])?.Parts.Count, 0, "no parts");
            Same(FlowDelegation.Of([Helper("a"), Helper("b", "completed")])?.Text, "하위 에이전트 2개에 위임 · Haiku 5.5 · 1/2 실행 중", "sub-agents alone");

            var waiting = FlowReturn.Of(FlowFixtures.Run("r", agents: [Helper("notes", "completed", true), Command("ci"), Command("apk")]).Shown)!;
            Same(waiting.Text, "병렬 2/3 실행 중 · 보고를 기다리는 중", "commands among them");
            Check(waiting.Total == 3 && waiting.Running == 2 && waiting.Subagents == 1 && waiting.SubagentsRunning == 0 && waiting.Tasks == 2 && waiting.TasksRunning == 2, "its counts");
            Same(FlowReturn.Of(FlowFixtures.Run("r", agents: [Command("dev")]).Shown)?.Text, "병렬 1/1 실행 중 · 보고를 기다리는 중", "commands alone");
            Same(FlowReturn.Of(FlowFixtures.Run("r", agents: [Helper("notes", "completed", true), Command("ci", status: "completed")]).Shown)?.Text, "메인으로 복귀 · 검토 중", "all ended");
            Same(FlowReturn.Of(FlowFixtures.Run("r", agents: [Helper("a"), Helper("b", "completed")]))?.Text, "하위 에이전트 1/2 실행 중 · 보고를 기다리는 중", "sub-agents alone");
            Same(FlowReturn.Of(FlowFixtures.Run("r:notice:1", agents: [Command("c", status: "completed", source: "ci")], notice: true)), null, "a notice run");

            var release = MightyGraphSupport.ShownRuns([FlowFixtures.Run("r", agents: [Helper("notes", "completed", true), Command("ci"), Command("apk")])]);
            var status = FlowLog.StatusOf(release)!;
            Same(status.Text, "Sonnet 5.5 실행 중 · 작업 2개 실행 중", "the mock's moment");
            SameList(status.Parts.Select(p => p.Role), [FlowRole.Lead, FlowRole.Task], "in their roles' inks");
            Same(FlowLog.StatusOf(MightyGraphSupport.ShownRuns([FlowFixtures.Run("r", agents: [Helper("notes", background: true), Command("ci"), Command("apk", status: "completed")])]))?.Text,
                "Sonnet 5.5 실행 중 · 하위 에이전트 1/1 실행 중 · 작업 1개 실행 중", "side by side");
            Same(FlowLog.StatusOf(MightyGraphSupport.ShownRuns([FlowFixtures.Run("r", agents: [Helper("notes", "completed", true), Command("ci", status: "completed")])]))?.Text,
                "Sonnet 5.5 실행 중 · 하위 에이전트 1개 끝남", "ended sub-agents once nothing runs");
            Same(FlowLog.StatusOf(MightyGraphSupport.ShownRuns([FlowFixtures.Run("r", "completed", [Command("ci")], output: "launched")]))?.Text,
                "Sonnet 5.5 알림 대기 · 작업 1개 실행 중", "awaiting the commands' notices");

            var mock = FlowFixtures.Run("r", agents: [Helper("notes", "completed", true), Command("ci", title: "CI 대기"), Command("apk", title: "폰 APK 빌드")]);
            var mockGraph = FlowFixtures.Layout([mock]);
            var row = new[] { "agent:notes", "agent:ci", "agent:apk" }.Select(s => Must(mockGraph, Node(mock, s))).ToList();
            Check(row.All(n => n.IsCompact) && row.Select(n => n.Frame.Y).Distinct().Count() == 1, "three cards in one row");
            Check(row[0].Frame.MaxX < row[1].Frame.X && row[1].Frame.MaxX < row[2].Frame.X, "left to right");
            var mockLine = Must(mockGraph, Line(mock));
            SameList(mockGraph.Edges.Where(e => e.Source == mockLine.Id).Select(e => e.Target), row.Select(n => n.Id), "under one line");
            var mockBack = Must(mockGraph, Node(mock, MightyGraphLayout.WorkSuffix));
            SameList(mockGraph.Edges.Where(e => e.Target == mockBack.Id).Select(e => e.Source).Order(StringComparer.Ordinal), row.Select(n => n.Id).Order(StringComparer.Ordinal), "into the main session's card");
            // (Which of these edges are live is the web page's: flow.ts liveFlowIds, the Mac's liveFlowIDs.)
            Same(MightyGraphSupport.ShownRuns([mock])[0].Agents.Count, 0, "no command block for the other views");
            Check(!PlainLayout([mock]).Nodes.Any(n => n.Id == row[1].Id), "nor without the fan-out");
        });

        var scout = Helper("scout");
        var nested = FlowFixtures.Run("r", agents: [scout, Command("watch", parent: "scout")]);
        var nestedRuns = MightyGraphSupport.ShownRuns([nested]);
        SameList(FlowDelegation.Children(FlowDelegation.MainKey, nestedRuns[0]).Select(a => a.Id), ["scout"], "the sub-agent under the main session");
        SameList(FlowDelegation.Children("scout", nestedRuns[0]).Select(a => a.Id), ["watch"], "the command under the sub-agent");
        string scoutID = Agent(nested, "scout"), watchID = Agent(nested, "watch");
        Same(Find(FlowFixtures.Layout([nested]), watchID), null, "the card folds it");
        Check(Must(FlowFixtures.Layout([nested], opened: [scoutID]), watchID).IsCompact, "opened, its row holds the command's card");

        FlowTrackerVerification.InLanguage("en", () =>
        {
            Same(FlowDelegation.Of([Helper("a", "completed"), Command("ci"), Command("apk")])!.Text, "3 in parallel · sub-agents 1 · tasks 2 · 2/3 running", "in English");
            Same(FlowDelegation.Of([Command("ci")])?.Text, "Running 1 task · 1/1 running", "one command in English");
        });
        Same(FlowCard.LastOutput([]), null, "nothing written");
        Same(FlowCard.LastOutput(["one", "two"]), "two", "the last line");
        Same(FlowCard.LastOutput(["one", "two", "", "   ", "[exited with code 0]"]), "two", "not the exit trailer");
        Same(FlowCard.LastOutput(["[exited with code -1]"]), null, "the trailer alone");
        Same(FlowCard.LastOutput(["  indented  "]), "indented", "trimmed");
        Same(FlowCard.LastOutput(["[exited with code soon]"]), "[exited with code soon]", "only Claude Code's own trailer");
        Check(System.Text.Encoding.UTF8.GetByteCount(FlowCard.LastOutput([new string('x', 500)]) ?? "") <= 240, "bounded");
        return Task.CompletedTask;
    }
}
