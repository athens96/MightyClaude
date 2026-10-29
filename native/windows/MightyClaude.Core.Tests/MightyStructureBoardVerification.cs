using MightyClaude.Core;
using static MightyStructureVerification;
using Board = MightyClaude.Core.MightyStructure.Board;
using Page = MightyClaude.Core.MightyStructureFollow.Page;
using Scroll = MightyClaude.Core.MightyStructureFollow.Scroll;
using Step = MightyClaude.Core.MightyStructure.Step;
using Wheel = MightyClaude.Core.MightyStructureWheel;

/// <summary>
/// The structure view's kanban boards and its wheel against the checks of the Mac's MightyStructureBoardTests and
/// MightyStructureWheelTests: one board per request of a free pane, one per pipeline of a styled pane, their header steps and
/// columns, the navigation between them, and the wheel and trackpad gestures that are the user's own move.
/// </summary>
internal static class MightyStructureBoardVerification
{
    private static void Check(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }

    private static IReadOnlyList<Board> Boards(IReadOnlyList<MightyGraphRun> runs, StyleEvaluator? style = null, string? phase = null, bool startingNew = false, IReadOnlyList<PlanRecord>? planHistory = null) =>
        MightyStructure.Boards(MightyStructure.Make(runs, style, phase is null || style is null ? null : Phase(style, phase), startingNew, planHistory ?? []));
    private static string Ids(IEnumerable<Board> boards) => string.Join(",", boards.Select(b => b.Id));
    private static string Runs(IEnumerable<Board> boards) => string.Join("|", boards.Select(b => string.Join(",", b.RunIds)));
    private static string Cards(MightyStructure.BoardColumn column) => string.Join(",", column.Cards.Select(c => c.Id));
    private static string Steps(Board board) => string.Join(",", board.Steps.Select(s => MightyStructure.Word(s.Step)));
    private static string Counts(Board board) => string.Join(",", board.Columns.Select(c => c.Count));

    internal static Task AFreePaneHasOneBoardPerRequest()
    {
        var runs = new[]
        {
            Run("r1", "\n  첫 요청의 첫 줄\n둘째 줄", final: "끝났습니다"),
            Run("r2", status: "running", agents: [Agent("a", status: "running"), Agent("b", "a", "running"), Agent("c")]),
            Run("r3", status: "error"),
        };
        var boards = Boards(runs);
        Check(Ids(boards) == "r1,r2,r3" && boards.Select(b => b.Ordinal).SequenceEqual([1, 2, 3]), "one board per request, numbered: " + Ids(boards));
        Check(boards.All(b => b.Kind == MightyStructure.BoardKind.Request && b.RunIds.SequenceEqual([b.Id])), "a free board holds its one request");
        Check(boards[0].Headline == "첫 요청의 첫 줄", "a board's headline is its request's first line with words: " + boards[0].Headline);
        Check(boards.Select(b => b.Status).SequenceEqual(["completed", "running", "error"]), "a free board's status is its request's");
        // The header: 요청 → 실행 → 결과, as a free row's steps.
        Check(boards[0].Steps.Select(s => s.Id).SequenceEqual(["request", "execution", "result"]) && boards[0].Steps.All(s => s.Title is null), "a free board's steps are request, run and result, named by the view");
        Check(string.Join("|", boards.Select(Steps)) == "done,done,done|done,current,pending|done,error,error", "a free board's steps stand as its row's: " + string.Join("|", boards.Select(Steps)));
        Check(boards.Select(b => b.CurrentStepIndex).SequenceEqual(new int?[] { 2, 1, 2 }), "the current step, else the last that is over");
        // The columns: the request, then the 실행 card and one card per sub-agent, then the result.
        var running = boards[1];
        Check(running.Columns.Select(c => c.Id).SequenceEqual(["request", "execution", "result"]) && running.Columns.Select(c => c.Step).SequenceEqual(running.Steps), "one column per step");
        var nodes = MightyStructure.Clusters(runs[1]).SelectMany(c => c.Nodes).ToList();
        Check(Cards(running.Columns[0]) == "request:r2" && Cards(running.Columns[1]) == string.Join(",", new[] { "execution:r2" }.Concat(nodes.Select(n => "agent:" + n.NodeId))) && Cards(running.Columns[2]) == "result:r2",
            "the request, the run and its sub-agents, the result: " + string.Join("|", running.Columns.Select(Cards)));
        Check(Counts(running) == "1,4,0" && Counts(boards[0]) == "1,1,1", "a free column counts its cards, the result once there is one");
        // A nested sub-agent names the block it runs under; a root names none.
        var agents = running.Columns[1].Cards.Where(c => c.Kind == MightyStructure.CardKind.Agent).ToList();
        Check(agents.All(c => c.Item.RunId == "r2") && agents.Select(c => c.Parent).SequenceEqual([null, nodes[0].Title, null]), "a nested sub-agent names its parent block");

        // A notice run is no request: its board says what work it reports on, an older save the generic heading (macOS aNoticeRunsBoardIsHeadedByItsTitle).
        var notice = Run("r1:notice:1", "Build the app · done", final: "Built."); notice.NoticeRun = true; notice.NoticeTitle = "Background work finished · Build the app";
        var older = Run("r1:notice:2", "Build the app · done", final: "Built."); older.NoticeRun = true;
        var headed = Boards([Run("r1", "Build it", final: "Started."), notice, older]);
        Check(headed.Select(b => b.Headline).SequenceEqual(["Build it", "Background work finished · Build the app", BackgroundNoticeRuns.GenericTitle]),
            "a notice run's board is headed by its title: " + string.Join(" | ", headed.Select(b => b.Headline)));
        return Task.CompletedTask;
    }

    internal static Task AStyledPaneSplitsIntoPipelinesWhereThePhaseGoesBack()
    {
        var style = Style("ouroboros");
        var runs = new[]
        {
            Run("r1", "그냥 시작해줘"),            // goal (the default): the first request starts a pipeline
            Run("r2", "ooo interview 할 일 앱"),   // interview
            Run("r3", "좀 더 물어봐"),             // interview (the previous run's)
            Run("r4", "ooo seed"),               // seed
            Run("r5", "ooo pm 다시"),              // interview: back, a new pipeline
            Run("r6", "이어서", "running"),         // interview
        };
        Check(MightyStructure.PhaseIDs(runs, style).SequenceEqual(["goal", "interview", "interview", "seed", "interview", "interview"]), "the requests' phases");
        var boards = Boards(runs, style);
        Check(Ids(boards) == "r1,r5" && Runs(boards) == "r1,r2,r3,r4|r5,r6", "a pipeline ends where the phase goes back: " + Runs(boards));
        Check(boards.All(b => b.Kind == MightyStructure.BoardKind.Pipeline) && boards.Select(b => b.Ordinal).SequenceEqual([1, 2]) && boards[0].Headline == "그냥 시작해줘", "pipelines are numbered and headed by their first request");
        // Any request still running makes the pipeline running.
        Check(boards.Select(b => b.Status).SequenceEqual(["completed", "running"]), "a pipeline runs while any of its requests does");
        // An older pipeline that is over: every phase it reached is done, the rest pending.
        var phases = style.Manifest.Phases.OrderBy(p => p.Order).ToList();
        Check(boards[0].Steps.Select(s => s.Id).SequenceEqual(phases.Select(p => p.Id)) && boards[0].Steps.Select(s => s.Title).SequenceEqual(phases.Select(p => p.Title)), "a pipeline's steps are the style's phases");
        Check(Steps(boards[0]) == "done,done,done,pending,pending,pending", "an older pipeline that is over: " + Steps(boards[0]));
        // The newest pipeline stands where the pane does.
        Check(Steps(boards[1]) == "done,current,pending,pending,pending,pending" && boards[1].CurrentStepIndex == 1, "the newest pipeline stands where the pane does: " + Steps(boards[1]));
        // A single request is a pipeline of its own.
        Check(Runs(Boards([runs[0]], style)) == "r1" && Boards([], style).Count == 0, "a single request is a pipeline; no request, no board");
        return Task.CompletedTask;
    }

    internal static Task TheNewestPipelineTakesThePanesPhaseOnlyWhenItLiesAhead()
    {
        var style = Style("ouroboros");
        var runs = new[] { Run("r1", "ooo interview 시작"), Run("r2", "ooo seed") };
        // The pane moved on to 실행: the newest request takes it.
        var moved = Boards(runs, style, "run");
        Check(Runs(moved) == "r1,r2" && Steps(moved[0]) == "done,done,done,current,pending,pending", "the pane moved on: " + Steps(moved[0]));
        // Starting anew at 목표: that phase is the next request's, so the pipeline stands at its last request.
        var fresh = Boards(runs, style, "goal", startingNew: true);
        Check(Runs(fresh) == "r1,r2" && Steps(fresh[0]) == "done,done,current,pending,pending,pending", "starting anew: " + Steps(fresh[0]));
        // An older pipeline whose last request still runs stays current there.
        var busy = Boards([Run("a", "ooo seed", "running"), Run("b", "ooo interview")], style);
        Check(Runs(busy) == "a|b" && Steps(busy[0]) == "done,done,current,pending,pending,pending", "an older pipeline still running: " + Steps(busy[0]));
        return Task.CompletedTask;
    }

    internal static Task ClaudePlanStagesSplitWhenARequestPlansAgain()
    {
        var style = Style("claude-plan");
        var runs = new[] { Run("r1", "로그인 화면 계획 세워줘", source: "s1"), Run("r2", "테스트 돌려줘", source: "s2"), Run("r3", "이제 다음 화면", source: "s3"), Run("r4", "[계획] 새 기능", source: "s4") };
        var history = new[] { Record("p1", "s1", PlanOutcome.ApprovedAuto), Record("p3", "s3", PlanOutcome.ApprovedConfirm) };
        Check(MightyStructure.PhaseIDs(runs, style, planHistory: history).SequenceEqual(["execute", "plan", "execute", "plan"]), "the plan stages");
        var boards = Boards(runs, style, "plan", planHistory: history);
        Check(Runs(boards) == "r1|r2,r3|r4" && Ids(boards) == "r1,r2,r4", "a request that plans again starts a pipeline: " + Runs(boards));
        Check(string.Join("|", boards[1].Columns.Select(Cards)) == "summary:r2||summary:r3" && Counts(boards[1]) == "1,0,1", "a styled column holds its requests: " + string.Join("|", boards[1].Columns.Select(Cards)));
        Check(Steps(boards[1]) == "done,done,done" && Steps(boards[2]) == "current,pending,pending", "an older pipeline is done, the newest stands at the pane's phase");
        return Task.CompletedTask;
    }

    internal static Task AStyledColumnHoldsItsRequestsWithTheirSubAgentsUnderEach()
    {
        var style = Style("ouroboros");
        var runs = new[] { Run("r1", "ooo interview 시작", agents: [Agent("x"), Agent("y", "x")]), Run("r2", "더 물어봐"), Run("r3", "ooo seed", "running") };
        var board = Boards(runs, style)[0];
        var nodes = MightyStructure.Clusters(runs[0]).SelectMany(c => c.Nodes).Select(n => "agent:" + n.NodeId);
        Check(string.Join("|", board.Columns.Select(Cards)) == "|" + string.Join(",", new[] { "summary:r1" }.Concat(nodes).Append("summary:r2")) + "|summary:r3|||", "each request's card with its sub-agents under it: " + string.Join("|", board.Columns.Select(Cards)));
        Check(Counts(board) == "0,2,1,0,0,0" && Steps(board) == "done,done,current,pending,pending,pending", "a styled column counts its requests");
        return Task.CompletedTask;
    }

    internal static Task BoardIdsHoldAsRunsArriveAndOldOnesAreTrimmed()
    {
        var runs = new List<MightyGraphRun> { Run("r1"), Run("r2") };
        Check(Ids(Boards(runs)) == "r1,r2", "free boards are named by their request");
        runs.Add(Run("r3", status: "running"));
        Check(Ids(Boards(runs)) == "r1,r2,r3" && Ids(Boards(runs.Skip(1).ToList())) == "r2,r3", "a new request adds a board; a trim drops one");
        var style = Style("ouroboros");
        var styled = new List<MightyGraphRun> { Run("s1", "ooo interview 시작"), Run("s2", "ooo seed"), Run("s3", "ooo interview 다시") };
        Check(Ids(Boards(styled, style)) == "s1,s3", "pipelines are named by their first request");
        // A request that keeps the newest pipeline going joins it; a new pipeline is appended.
        styled.Add(Run("s4", "ooo seed"));
        Check(Ids(Boards(styled, style)) == "s1,s3" && string.Join(",", Boards(styled, style)[^1].RunIds) == "s3,s4", "a request that goes on joins the newest pipeline");
        styled.Add(Run("s5", "ooo interview 또"));
        Check(Ids(Boards(styled, style)) == "s1,s3,s5", "a new pipeline is appended");
        // Trims drop the oldest requests: the newer pipelines keep their ids, and the oldest one, its first request gone, is named by its next.
        Check(Ids(Boards(styled.Skip(1).ToList(), style)) == "s2,s3,s5", "a trimmed pipeline is named by its next request");
        return Task.CompletedTask;
    }

    internal static Task TheViewMovesOnWithTheNewestBoardUntilTheUserPicksAnOlderOne()
    {
        var runs = new List<MightyGraphRun> { Run("r1"), Run("r2"), Run("r3", status: "running") };
        IReadOnlyList<Board> Now() => Boards(runs);
        var nav = new MightyStructure.Navigation();
        Check(nav.Index([]) is null && nav.Index(Now()) == 2 && nav.OnNewest(Now()), "the view opens on the newest board");
        // A new request while on the newest board: the view moves to it.
        runs.Add(Run("r4", status: "running"));
        Check(nav.Index(Now()) == 3 && !nav.HasUnseen(Now()), "a new request on the newest board is shown");
        // Back one: the older board is kept by id as new ones arrive, and they are unseen.
        nav.Step(-1, Now());
        Check(nav.Chosen == "r3" && !nav.HasUnseen(Now()), "back one keeps the older board");
        runs.Add(Run("r5", status: "running"));
        Check(nav.Index(Now()) == 2 && nav.HasUnseen(Now()), "a new request leaves the older board shown and is unseen");
        // Moving between older boards keeps the badge until the newest is shown.
        nav.Select("r1", Now());
        Check(nav.Index(Now()) == 0 && nav.HasUnseen(Now()), "moving between older boards keeps the badge");
        nav.Step(-1, Now());
        Check(nav.Index(Now()) == 0, "stepping stops at the oldest board");
        nav.Select("missing", Now());
        Check(nav.Index(Now()) == 0, "an unknown board moves nothing");
        nav.ShowNewest(Now());
        Check(nav.Chosen is null && nav.Index(Now()) == 4 && !nav.HasUnseen(Now()), "the badge shows the newest board");
        nav.Step(1, Now());
        Check(nav.Index(Now()) == 4, "stepping stops at the newest board");
        runs.Add(Run("r6", status: "running"));
        Check(nav.Index(Now()) == 5, "the newest board moves on again");
        // Stepping onto the newest board follows new ones again.
        nav.Step(-2, Now()); nav.Step(1, Now()); nav.Step(1, Now());
        Check(nav.Chosen is null, "stepping onto the newest board follows new ones again");
        return Task.CompletedTask;
    }

    internal static Task APickedBoardIsFoundAgainAfterItsFirstRequestIsTrimmed()
    {
        var style = Style("ouroboros");
        var runs = new[] { Run("s1", "ooo interview 시작"), Run("s2", "ooo seed"), Run("s3", "ooo interview 다시"), Run("s4", "ooo interview 또", "running") };
        var nav = new MightyStructure.Navigation();
        nav.Select("s1", Boards(runs, style));
        Check(nav.Index(Boards(runs, style)) == 0, "the oldest pipeline is picked");
        var trimmed = Boards(runs.Skip(1).ToList(), style);
        Check(Ids(trimmed) == "s2,s3" && nav.Index(trimmed) == 0, "a picked pipeline is found by a request it held");
        // A picked board trimmed away entirely: the oldest board left is shown.
        Check(nav.Index(Boards(runs.Skip(2).ToList(), style)) == 0, "a picked board trimmed away shows the oldest");
        return Task.CompletedTask;
    }

    internal static Task TheWheelMovesTheOriginAndStaysOnItsAxis()
    {
        var minimum = (X: 0d, Y: 0d); var maximum = (X: 240d, Y: 1_800d);
        Check(Wheel.StageOf(0, 0) == Wheel.Stage.Step && Wheel.StageOf(32, 0) == Wheel.Stage.Began && Wheel.StageOf(1, 0) == Wheel.Stage.Began && Wheel.StageOf(4, 0) == Wheel.Stage.Changed && Wheel.StageOf(2, 0) == Wheel.Stage.Changed,
            "a wheel step, a touch and a move");
        Check(Wheel.StageOf(8, 0) == Wheel.Stage.Ended && Wheel.StageOf(16, 0) == Wheel.Stage.Cancelled && Wheel.StageOf(0, 1) == Wheel.Stage.Momentum && Wheel.StageOf(0, 4) == Wheel.Stage.Momentum
            && Wheel.StageOf(0, 8) == Wheel.Stage.MomentumEnded && Wheel.StageOf(0, 16) == Wheel.Stage.MomentumEnded, "a lift, a cancel and the momentum");
        // Fingers moving down (a positive y) show what is above; moving right shows the left.
        var start = (X: 100d, Y: 900d);
        Check(Wheel.Origin(start, (0, 120), null, minimum, maximum) == (100, 780) && Wheel.Origin(start, (-60, 0), null, minimum, maximum) == (160, 900) && Wheel.Origin(start, (30, -40), null, minimum, maximum) == (70, 940), "a wheel moves the origin against its delta");
        Check(Wheel.AxisOf((-6, 12)) == Wheel.Axis.Vertical && Wheel.AxisOf((-12, 6)) == Wheel.Axis.Horizontal && Wheel.AxisOf((0, 0)) is null, "a movement's axis is its larger part");
        Check(Wheel.Origin(start, (-6, 12), Wheel.Axis.Vertical, minimum, maximum) == (100, 888) && Wheel.Origin(start, (-12, 6), Wheel.Axis.Horizontal, minimum, maximum) == (112, 900), "a gesture keeps to its axis");
        Check(Wheel.Origin((10, 20), (50, 100), null, minimum, maximum) == (0, 0) && Wheel.Origin((230, 1_790), (-50, -100), null, minimum, maximum) == maximum, "a wheel stops at the edges");
        Check(Wheel.Origin((5, 5), (10, 10), null, (10, double.NaN), (0, 100)) == (5, 5), "a range that is not valid keeps the origin");
        // A code block that scrolls sideways, at its left edge.
        var codeMaximum = (X: 400d, Y: 0d);
        var right = Wheel.CanMove((0, 0), (-20, 0), Wheel.Axis.Horizontal, (0, 0), codeMaximum);
        var left = Wheel.CanMove((0, 0), (20, 0), Wheel.Axis.Horizontal, (0, 0), codeMaximum);
        var down = Wheel.CanMove((0, 0), (0, -20), Wheel.Axis.Vertical, (0, 0), codeMaximum);
        Check(right && !left && !down, "an inner scroll view can move only where it has room");
        // Innermost first, the page last: the code block takes a sideways scroll it can make, and the page takes the rest; a page at its edge still takes it.
        Check(Wheel.Taker([right, true]) == 0 && Wheel.Taker([left, true]) == 1 && Wheel.Taker([down, true]) == 1 && Wheel.Taker([false, false]) == 1 && Wheel.Taker([false]) == 0, "the innermost scroll view that can move takes the wheel, else the page");
        return Task.CompletedTask;
    }

    internal static Task AGestureIsDecidedOnceAndIsTheUsersUntilItSettles()
    {
        var wheel = new Wheel.Tracker();
        Check(wheel.Feed(Wheel.Stage.Began, true, (0, 0), 10) == Wheel.Tracker.Action.Hold && wheel.Axis is null, "fingers on the page hold the event");
        Check(wheel.Feed(Wheel.Stage.Changed, false, (-3, 12), 10.02) == Wheel.Tracker.Action.Choose && wheel.Axis == Wheel.Axis.Vertical, "the first movement chooses the taker and the axis");
        Check(wheel.Feed(Wheel.Stage.Changed, false, (-8, 4), 10.04) == Wheel.Tracker.Action.Move && wheel.Axis == Wheel.Axis.Vertical, "the gesture keeps its taker and its axis");
        Check(wheel.Feed(Wheel.Stage.Ended, false, (0, 0), 10.06) == Wheel.Tracker.Action.Hold && wheel.Feed(Wheel.Stage.Momentum, false, (0, 9), 10.1) == Wheel.Tracker.Action.Move
            && wheel.Feed(Wheel.Stage.MomentumEnded, false, (0, 0), 10.5) == Wheel.Tracker.Action.Hold, "the momentum is the gesture's");
        Check(wheel.Feed(Wheel.Stage.Momentum, false, (0, 9), 11) == Wheel.Tracker.Action.Pass, "momentum after the gesture is over is not this page's");

        wheel = new();
        Check(wheel.Feed(Wheel.Stage.Began, false, (0, 0), 10) == Wheel.Tracker.Action.Pass && wheel.Feed(Wheel.Stage.Changed, true, (0, 12), 10.02) == Wheel.Tracker.Action.Pass
            && wheel.Feed(Wheel.Stage.Ended, true, (0, 0), 10.04) == Wheel.Tracker.Action.Pass && wheel.Feed(Wheel.Stage.Momentum, true, (0, 8), 10.06) == Wheel.Tracker.Action.Pass && !wheel.UserScrolling(10.06),
            "a gesture that began elsewhere is never this page's");
        wheel = new();
        Check(wheel.Feed(Wheel.Stage.Began, true, (-14, 2), 10) == Wheel.Tracker.Action.Choose && wheel.Axis == Wheel.Axis.Horizontal && wheel.Feed(Wheel.Stage.Changed, true, (-14, 2), 10.02) == Wheel.Tracker.Action.Move, "a gesture that moves at once chooses at its start");
        wheel = new();
        Check(wheel.Feed(Wheel.Stage.Step, true, (0, -30), 10) == Wheel.Tracker.Action.Choose && wheel.Axis is null && wheel.Feed(Wheel.Stage.Step, true, (0, -30), 10.05) == Wheel.Tracker.Action.Choose
            && wheel.Feed(Wheel.Stage.Step, false, (0, -30), 10.1) == Wheel.Tracker.Action.Pass, "a mouse wheel step is decided on its own");
        Check(wheel.Feed(Wheel.Stage.Momentum, true, (0, 8), 10.15) == Wheel.Tracker.Action.Pass, "a step ends any gesture before it");
        wheel = new();
        wheel.Feed(Wheel.Stage.Began, true, (0, 0), 10); wheel.Feed(Wheel.Stage.Changed, true, (0, 12), 10.02); wheel.Feed(Wheel.Stage.Ended, true, (0, 0), 10.04);
        Check(wheel.Feed(Wheel.Stage.Began, false, (0, 0), 12) == Wheel.Tracker.Action.Pass && wheel.Feed(Wheel.Stage.Momentum, false, (0, 8), 12.2) == Wheel.Tracker.Action.Pass, "a new gesture elsewhere ends one this page still held");
        wheel = new();
        wheel.Feed(Wheel.Stage.Began, true, (0, 0), 10); wheel.Feed(Wheel.Stage.Changed, true, (0, 12), 10.02); wheel.Feed(Wheel.Stage.Ended, true, (0, 0), 10.04);
        Check(wheel.Feed(Wheel.Stage.Momentum, false, (0, 8), 10.04 + Wheel.SettleGrace + 0.2) == Wheel.Tracker.Action.Pass && wheel.Feed(Wheel.Stage.MomentumEnded, false, (0, 0), 10.04 + Wheel.SettleGrace + 0.4) == Wheel.Tracker.Action.Pass,
            "momentum long after a gesture ended is not this page's");

        // The user's scroll: from the gesture's start until just after its last event.
        wheel = new();
        Check(!wheel.UserScrolling(10), "no gesture, no scroll of the user's");
        wheel.Feed(Wheel.Stage.Began, true, (0, 0), 10);
        Check(wheel.UserScrolling(10.5), "fingers resting before they move are already the user's");
        wheel.Feed(Wheel.Stage.Changed, true, (0, 12), 10.6); wheel.Feed(Wheel.Stage.Ended, true, (0, 0), 10.7);
        Check(wheel.UserScrolling(10.7 + Wheel.SettleGrace - 0.01), "between the lift and the momentum");
        wheel.Feed(Wheel.Stage.Momentum, true, (0, 9), 10.8);
        Check(wheel.UserScrolling(11.4), "during the momentum");
        wheel.Feed(Wheel.Stage.MomentumEnded, true, (0, 0), 11.5);
        Check(wheel.UserScrolling(11.5 + Wheel.SettleGrace - 0.01) && !wheel.UserScrolling(11.5 + Wheel.SettleGrace + 0.01), "the moment after the momentum ends");
        wheel = new(); wheel.Feed(Wheel.Stage.Step, true, (0, -30), 10);
        Check(wheel.UserScrolling(10 + Wheel.SettleGrace - 0.01) && !wheel.UserScrolling(10 + Wheel.SettleGrace + 0.01), "a mouse wheel step is the user's for a moment");
        wheel = new(); wheel.Feed(Wheel.Stage.Began, true, (0, 0), 10); wheel.Feed(Wheel.Stage.Changed, true, (0, 12), 10.1);
        Check(wheel.UserScrolling(10.1 + Wheel.StaleAfter - 0.01) && !wheel.UserScrolling(10.1 + Wheel.StaleAfter + 0.01), "a gesture whose end never came goes stale");

        var idle = new Wheel.Tracker();
        Check(!Wheel.UserMoving(false, false, 0, idle, 100) && Wheel.UserMoving(true, false, 0, idle, 100) && Wheel.UserMoving(false, true, 0, idle, 100)
            && Wheel.UserMoving(false, false, 100 - MightyStructurePan.ReleaseGrace + 0.01, idle, 100), "the user moves the page by scroll, drag or the moment after one");
        wheel = new(); wheel.Feed(Wheel.Stage.Began, true, (0, 0), 99.9);
        Check(Wheel.UserMoving(false, false, 0, wheel, 100), "a wheel gesture is the user's move");

        // Symptom: a trackpad scroll never reached the system's scroll phase, so a page that grew under it was read as a layout and followed back.
        static Page At(double origin, double document) => new(origin, 600, document, "b", document - 8);
        var watch = new MightyStructureFollow.PageWatch();
        Check(watch.Observe(At(1_400, 2_000), false) is null && watch.Following, "the page follows at its end");
        wheel = new(); wheel.Feed(Wheel.Stage.Began, true, (0, 0), 10); wheel.Feed(Wheel.Stage.Changed, true, (0, 20), 10.02);
        var moving = Wheel.UserMoving(false, false, 0, wheel, 10.03);
        Check(moving && watch.Observe(At(1_380, 2_300), moving) is null && !watch.Following, "follow never scrolls while a wheel gesture is going");
        var blind = new MightyStructureFollow.PageWatch(); blind.Observe(At(1_400, 2_000), false);
        Check(blind.Observe(At(1_380, 2_300), false) == Scroll.Bottom, "without the wheel known, the same report pulled the reader back down");
        return Task.CompletedTask;
    }

    internal static Task AVerticalWheelMovesTheColumnAndASidewaysOneThePage()
    {
        // Under the pointer: the column, then the page; each confined to its own axis.
        var page = MightyStructurePan.Confined((0, 0), (240, 0), (0, 0), Wheel.Axis.Horizontal);
        var column = MightyStructurePan.Confined((0, 0), (0, 1_800), (0, 900), Wheel.Axis.Vertical);
        int Takers((double X, double Y) delta, Wheel.Axis? axis, (double X, double Y) columnAt) =>
            Wheel.Taker([Wheel.CanMove(columnAt, delta, axis, column.Minimum, column.Maximum), Wheel.CanMove((0, 0), delta, axis, page.Minimum, page.Maximum)]);
        Check(Takers((-2, 30), Wheel.Axis.Vertical, (0, 900)) == 0 && Takers((-15, 1), Wheel.Axis.Horizontal, (0, 900)) == 1, "a vertical scroll moves the column, a sideways one the page");
        // A mouse wheel's sideways step has no axis: the column cannot move that way, the page takes it.
        Check(Takers((-20, 0), null, (0, 900)) == 1, "a sideways wheel step goes to the page");
        // A column at its top shows nothing more above: the page takes the scroll and, moving sideways only, moves nothing.
        Check(Takers((0, 30), Wheel.Axis.Vertical, (0, 0)) == 1 && Wheel.Origin((0, 0), (0, 30), Wheel.Axis.Vertical, page.Minimum, page.Maximum) == (0, 0), "a column at its top passes the scroll to a page that does not move");
        return Task.CompletedTask;
    }
}
