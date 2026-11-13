//! The structure view's kanban boards and its wheel against C# `MightyStructureBoardVerification`
//! (native/windows/MightyCode.Core.Tests/MightyStructureBoardVerification.cs, registered in Verification.cs:357-367): one board per
//! request of a free pane, one per pipeline of a styled pane, their steps and columns, the navigation between them, and the wheel.

mod common;

use common::*;
use mighty_core::graph::MightyGraphRun;
use mighty_core::model::PlanRecord;
use mighty_graph::notice;
use mighty_graph::structure::board::{self, Board, BoardColumn, BoardKind, CardKind, Navigation};
use mighty_graph::structure::follow::{Page, PageWatch, Scroll};
use mighty_graph::structure::wheel::{self, Action, Axis, Stage, Tracker};
use mighty_graph::structure::{self, Inputs, StructureStyle, pan, word};

fn boards(
    runs: &[MightyGraphRun],
    style: Option<&ManifestStyle>,
    current: Option<&str>,
    starting_new: bool,
    history: &[PlanRecord],
) -> Vec<Board> {
    let inputs = Inputs {
        style: style.map(|s| s as &dyn StructureStyle),
        current_phase: current.and_then(|id| style.map(|s| phase(s, id))),
        starting_new,
        plan_history: Some(history),
        pending_plan_run_id: None,
    };
    board::boards(&structure::make(runs, &inputs, None, &ko()), &ko())
}

fn free(runs: &[MightyGraphRun]) -> Vec<Board> {
    boards(runs, None, None, false, &[])
}

fn styled(runs: &[MightyGraphRun], style: &ManifestStyle) -> Vec<Board> {
    boards(runs, Some(style), None, false, &[])
}

fn ids(boards: &[Board]) -> String {
    boards.iter().map(|b| b.id.as_str()).collect::<Vec<_>>().join(",")
}

fn runs_of(boards: &[Board]) -> String {
    boards.iter().map(|b| b.run_ids().join(",")).collect::<Vec<_>>().join("|")
}

fn cards(column: &BoardColumn) -> String {
    column.cards.iter().map(|c| c.id()).collect::<Vec<_>>().join(",")
}

fn steps(board: &Board) -> String {
    board.steps.iter().map(|s| word(s.step)).collect::<Vec<_>>().join(",")
}

fn counts(board: &Board) -> String {
    board.columns.iter().map(|c| c.count.to_string()).collect::<Vec<_>>().join(",")
}

#[test]
fn a_free_pane_has_one_board_per_request() {
    let runs = [
        run("r1", Some("\n  첫 요청의 첫 줄\n둘째 줄"), "completed", vec![], Some("끝났습니다"), None),
        run("r2", None, "running", vec![agent("a", None, "running", None), agent("b", Some("a"), "running", None), sub("c")], None, None),
        status("r3", "error"),
    ];
    let made = free(&runs);
    assert!(
        ids(&made) == "r1,r2,r3" && made.iter().map(|b| b.ordinal).collect::<Vec<_>>() == [1, 2, 3],
        "one board per request, numbered: {}",
        ids(&made)
    );
    assert!(made.iter().all(|b| b.kind == BoardKind::Request && b.run_ids() == [b.id.clone()]), "a free board holds its one request");
    assert_eq!(made[0].headline.as_deref(), Some("첫 요청의 첫 줄"), "a board's headline is its request's first line with words");
    assert_eq!(
        made.iter().map(|b| b.status.as_str()).collect::<Vec<_>>(),
        ["completed", "running", "error"],
        "a free board's status is its request's"
    );
    // The header: 요청 → 실행 → 결과, as a free row's steps.
    assert!(
        made[0].steps.iter().map(|s| s.id.as_str()).collect::<Vec<_>>() == ["request", "execution", "result"]
            && made[0].steps.iter().all(|s| s.title.is_none()),
        "a free board's steps are request, run and result, named by the view"
    );
    let all = made.iter().map(steps).collect::<Vec<_>>().join("|");
    assert_eq!(all, "done,done,done|done,current,pending|done,error,error", "a free board's steps stand as its row's");
    assert_eq!(
        made.iter().map(Board::current_step_index).collect::<Vec<_>>(),
        [Some(2), Some(1), Some(2)],
        "the current step, else the last that is over"
    );
    // The columns: the request, then the 실행 card and one card per sub-agent, then the result.
    let running = &made[1];
    assert!(
        running.columns.iter().map(|c| c.id()).collect::<Vec<_>>() == ["request", "execution", "result"]
            && running.columns.iter().map(|c| c.step.clone()).collect::<Vec<_>>() == running.steps,
        "one column per step"
    );
    let nodes: Vec<_> = structure::clusters(&runs[1], &ko()).into_iter().flat_map(|c| c.nodes).collect();
    let mut expected = vec!["execution:r2".to_string()];
    expected.extend(nodes.iter().map(|n| format!("agent:{}", n.node_id)));
    assert!(
        cards(&running.columns[0]) == "request:r2"
            && cards(&running.columns[1]) == expected.join(",")
            && cards(&running.columns[2]) == "result:r2",
        "the request, the run and its sub-agents, the result: {}",
        running.columns.iter().map(cards).collect::<Vec<_>>().join("|")
    );
    assert!(counts(running) == "1,4,0" && counts(&made[0]) == "1,1,1", "a free column counts its cards, the result once there is one");
    // A nested sub-agent names the block it runs under; a root names none.
    let agents: Vec<_> = running.columns[1].cards.iter().filter(|c| c.kind == CardKind::Agent).collect();
    assert!(
        agents.iter().all(|c| c.item.run_id == "r2")
            && agents.iter().map(|c| c.parent.clone()).collect::<Vec<_>>() == [None, Some(nodes[0].title.clone()), None],
        "a nested sub-agent names its parent block"
    );

    // A notice run is no request: its board says what work it reports on, an older save the generic heading.
    let mut titled = run("r1:notice:1", Some("Build the app · done"), "completed", vec![], Some("Built."), None);
    titled.notice_run = Some(true);
    titled.notice_title = Some("Background work finished · Build the app".into());
    let mut older = run("r1:notice:2", Some("Build the app · done"), "completed", vec![], Some("Built."), None);
    older.notice_run = Some(true);
    let headed = free(&[run("r1", Some("Build it"), "completed", vec![], Some("Started."), None), titled, older]);
    let generic = notice::generic_title(&ko());
    assert_eq!(
        headed.iter().map(|b| b.headline.clone().unwrap_or_default()).collect::<Vec<_>>(),
        ["Build it", "Background work finished · Build the app", generic.as_str()],
        "a notice run's board is headed by its title"
    );
}

#[test]
fn a_styled_pane_splits_into_pipelines_where_the_phase_goes_back() {
    let flow = style("ouroboros");
    let runs = [
        asked("r1", "그냥 시작해줘"),          // goal (the default): the first request starts a pipeline
        asked("r2", "ooo interview 할 일 앱"), // interview
        asked("r3", "좀 더 물어봐"),           // interview (the previous run's)
        asked("r4", "ooo seed"),               // seed
        asked("r5", "ooo pm 다시"),            // interview: back, a new pipeline
        run("r6", Some("이어서"), "running", vec![], None, None), // interview
    ];
    assert_eq!(
        structure::phase_ids(&runs, &flow, None, None, None),
        ["goal", "interview", "interview", "seed", "interview", "interview"],
        "the requests' phases"
    );
    let made = styled(&runs, &flow);
    assert!(
        ids(&made) == "r1,r5" && runs_of(&made) == "r1,r2,r3,r4|r5,r6",
        "a pipeline ends where the phase goes back: {}",
        runs_of(&made)
    );
    assert!(
        made.iter().all(|b| b.kind == BoardKind::Pipeline)
            && made.iter().map(|b| b.ordinal).collect::<Vec<_>>() == [1, 2]
            && made[0].headline.as_deref() == Some("그냥 시작해줘"),
        "pipelines are numbered and headed by their first request"
    );
    // Any request still running makes the pipeline running.
    assert_eq!(
        made.iter().map(|b| b.status.as_str()).collect::<Vec<_>>(),
        ["completed", "running"],
        "a pipeline runs while any of its requests does"
    );
    // An older pipeline that is over: every phase it reached is done, the rest pending.
    let mut phases = flow.phases().to_vec();
    phases.sort_by_key(|p| p.order);
    assert!(
        made[0].steps.iter().map(|s| s.id.clone()).collect::<Vec<_>>() == phases.iter().map(|p| p.id.clone()).collect::<Vec<_>>()
            && made[0].steps.iter().map(|s| s.title.clone()).collect::<Vec<_>>()
                == phases.iter().map(|p| Some(p.title.clone())).collect::<Vec<_>>(),
        "a pipeline's steps are the style's phases"
    );
    assert_eq!(steps(&made[0]), "done,done,done,pending,pending,pending", "an older pipeline that is over");
    // The newest pipeline stands where the pane does.
    assert!(
        steps(&made[1]) == "done,current,pending,pending,pending,pending" && made[1].current_step_index() == Some(1),
        "the newest pipeline stands where the pane does: {}",
        steps(&made[1])
    );
    // A single request is a pipeline of its own.
    assert!(
        runs_of(&styled(&runs[..1], &flow)) == "r1" && styled(&[], &flow).is_empty(),
        "a single request is a pipeline; no request, no board"
    );
}

#[test]
fn the_newest_pipeline_takes_the_panes_phase_only_when_it_lies_ahead() {
    let flow = style("ouroboros");
    let runs = [asked("r1", "ooo interview 시작"), asked("r2", "ooo seed")];
    // The pane moved on to 실행: the newest request takes it.
    let moved = boards(&runs, Some(&flow), Some("run"), false, &[]);
    assert!(
        runs_of(&moved) == "r1,r2" && steps(&moved[0]) == "done,done,done,current,pending,pending",
        "the pane moved on: {}",
        steps(&moved[0])
    );
    // Starting anew at 목표: that phase is the next request's, so the pipeline stands at its last request.
    let fresh = boards(&runs, Some(&flow), Some("goal"), true, &[]);
    assert!(
        runs_of(&fresh) == "r1,r2" && steps(&fresh[0]) == "done,done,current,pending,pending,pending",
        "starting anew: {}",
        steps(&fresh[0])
    );
    // An older pipeline whose last request still runs stays current there.
    let busy = styled(&[run("a", Some("ooo seed"), "running", vec![], None, None), asked("b", "ooo interview")], &flow);
    assert!(
        runs_of(&busy) == "a|b" && steps(&busy[0]) == "done,done,current,pending,pending,pending",
        "an older pipeline still running: {}",
        steps(&busy[0])
    );
}

#[test]
fn claude_plan_stages_split_when_a_request_plans_again() {
    let plan = style("claude-plan");
    let sourced = |id: &str, input: &str, source: &str| run(id, Some(input), "completed", vec![], None, Some(source));
    let runs = [
        sourced("r1", "로그인 화면 계획 세워줘", "s1"),
        sourced("r2", "테스트 돌려줘", "s2"),
        sourced("r3", "이제 다음 화면", "s3"),
        sourced("r4", "[계획] 새 기능", "s4"),
    ];
    let history = [record("p1", "s1", "approvedAuto"), record("p3", "s3", "approvedConfirm")];
    assert_eq!(structure::phase_ids(&runs, &plan, None, Some(&history), None), ["execute", "plan", "execute", "plan"], "the plan stages");
    let made = boards(&runs, Some(&plan), Some("plan"), false, &history);
    assert!(
        runs_of(&made) == "r1|r2,r3|r4" && ids(&made) == "r1,r2,r4",
        "a request that plans again starts a pipeline: {}",
        runs_of(&made)
    );
    let columns = made[1].columns.iter().map(cards).collect::<Vec<_>>().join("|");
    assert!(columns == "summary:r2||summary:r3" && counts(&made[1]) == "1,0,1", "a styled column holds its requests: {columns}");
    assert!(
        steps(&made[1]) == "done,done,done" && steps(&made[2]) == "current,pending,pending",
        "an older pipeline is done, the newest stands at the pane's phase"
    );
}

#[test]
fn a_styled_column_holds_its_requests_with_their_sub_agents_under_each() {
    let flow = style("ouroboros");
    let runs = [
        run("r1", Some("ooo interview 시작"), "completed", vec![sub("x"), agent("y", Some("x"), "completed", None)], None, None),
        asked("r2", "더 물어봐"),
        run("r3", Some("ooo seed"), "running", vec![], None, None),
    ];
    let made = &styled(&runs, &flow)[0];
    let mut expected = vec!["summary:r1".to_string()];
    expected.extend(structure::clusters(&runs[0], &ko()).into_iter().flat_map(|c| c.nodes).map(|n| format!("agent:{}", n.node_id)));
    expected.push("summary:r2".into());
    let columns = made.columns.iter().map(cards).collect::<Vec<_>>().join("|");
    assert_eq!(columns, format!("|{}|summary:r3|||", expected.join(",")), "each request's card with its sub-agents under it");
    assert!(
        counts(made) == "0,2,1,0,0,0" && steps(made) == "done,done,current,pending,pending,pending",
        "a styled column counts its requests"
    );
}

#[test]
fn board_ids_hold_as_runs_arrive_and_old_ones_are_trimmed() {
    let mut runs = vec![done("r1"), done("r2")];
    assert_eq!(ids(&free(&runs)), "r1,r2", "free boards are named by their request");
    runs.push(status("r3", "running"));
    assert!(ids(&free(&runs)) == "r1,r2,r3" && ids(&free(&runs[1..])) == "r2,r3", "a new request adds a board; a trim drops one");
    let flow = style("ouroboros");
    let mut styled_runs = vec![asked("s1", "ooo interview 시작"), asked("s2", "ooo seed"), asked("s3", "ooo interview 다시")];
    assert_eq!(ids(&styled(&styled_runs, &flow)), "s1,s3", "pipelines are named by their first request");
    // A request that keeps the newest pipeline going joins it; a new pipeline is appended.
    styled_runs.push(asked("s4", "ooo seed"));
    let joined = styled(&styled_runs, &flow);
    assert!(
        ids(&joined) == "s1,s3" && joined.last().unwrap().run_ids().join(",") == "s3,s4",
        "a request that goes on joins the newest pipeline"
    );
    styled_runs.push(asked("s5", "ooo interview 또"));
    assert_eq!(ids(&styled(&styled_runs, &flow)), "s1,s3,s5", "a new pipeline is appended");
    // Trims drop the oldest requests: the newer pipelines keep their ids, and the oldest one is named by its next request.
    assert_eq!(ids(&styled(&styled_runs[1..], &flow)), "s2,s3,s5", "a trimmed pipeline is named by its next request");
}

#[test]
fn the_view_moves_on_with_the_newest_board_until_the_user_picks_an_older_one() {
    let mut runs = vec![done("r1"), done("r2"), status("r3", "running")];
    let mut nav = Navigation::new();
    assert!(
        nav.index(&[]).is_none() && nav.index(&free(&runs)) == Some(2) && nav.on_newest(&free(&runs)),
        "the view opens on the newest board"
    );
    // A new request while on the newest board: the view moves to it.
    runs.push(status("r4", "running"));
    assert!(nav.index(&free(&runs)) == Some(3) && !nav.has_unseen(&free(&runs)), "a new request on the newest board is shown");
    // Back one: the older board is kept by id as new ones arrive, and they are unseen.
    nav.step(-1, &free(&runs));
    assert!(nav.chosen() == Some("r3") && !nav.has_unseen(&free(&runs)), "back one keeps the older board");
    runs.push(status("r5", "running"));
    assert!(nav.index(&free(&runs)) == Some(2) && nav.has_unseen(&free(&runs)), "a new request leaves the older board shown and is unseen");
    // Moving between older boards keeps the badge until the newest is shown.
    nav.select("r1", &free(&runs));
    assert!(nav.index(&free(&runs)) == Some(0) && nav.has_unseen(&free(&runs)), "moving between older boards keeps the badge");
    nav.step(-1, &free(&runs));
    assert_eq!(nav.index(&free(&runs)), Some(0), "stepping stops at the oldest board");
    nav.select("missing", &free(&runs));
    assert_eq!(nav.index(&free(&runs)), Some(0), "an unknown board moves nothing");
    nav.show_newest(&free(&runs));
    assert!(
        nav.chosen().is_none() && nav.index(&free(&runs)) == Some(4) && !nav.has_unseen(&free(&runs)),
        "the badge shows the newest board"
    );
    nav.step(1, &free(&runs));
    assert_eq!(nav.index(&free(&runs)), Some(4), "stepping stops at the newest board");
    runs.push(status("r6", "running"));
    assert_eq!(nav.index(&free(&runs)), Some(5), "the newest board moves on again");
    // Stepping onto the newest board follows new ones again.
    nav.step(-2, &free(&runs));
    nav.step(1, &free(&runs));
    nav.step(1, &free(&runs));
    assert!(nav.chosen().is_none(), "stepping onto the newest board follows new ones again");
}

#[test]
fn a_picked_board_is_found_again_after_its_first_request_is_trimmed() {
    let flow = style("ouroboros");
    let runs = [
        asked("s1", "ooo interview 시작"),
        asked("s2", "ooo seed"),
        asked("s3", "ooo interview 다시"),
        run("s4", Some("ooo interview 또"), "running", vec![], None, None),
    ];
    let mut nav = Navigation::new();
    nav.select("s1", &styled(&runs, &flow));
    assert_eq!(nav.index(&styled(&runs, &flow)), Some(0), "the oldest pipeline is picked");
    let trimmed = styled(&runs[1..], &flow);
    assert!(ids(&trimmed) == "s2,s3" && nav.index(&trimmed) == Some(0), "a picked pipeline is found by a request it held");
    // A picked board trimmed away entirely: the oldest board left is shown.
    assert_eq!(nav.index(&styled(&runs[2..], &flow)), Some(0), "a picked board trimmed away shows the oldest");
}

#[test]
fn the_wheel_moves_the_origin_and_stays_on_its_axis() {
    let minimum = (0.0, 0.0);
    let maximum = (240.0, 1_800.0);
    assert!(
        wheel::stage_of(0, 0) == Stage::Step
            && wheel::stage_of(32, 0) == Stage::Began
            && wheel::stage_of(1, 0) == Stage::Began
            && wheel::stage_of(4, 0) == Stage::Changed
            && wheel::stage_of(2, 0) == Stage::Changed,
        "a wheel step, a touch and a move"
    );
    assert!(
        wheel::stage_of(8, 0) == Stage::Ended
            && wheel::stage_of(16, 0) == Stage::Cancelled
            && wheel::stage_of(0, 1) == Stage::Momentum
            && wheel::stage_of(0, 4) == Stage::Momentum
            && wheel::stage_of(0, 8) == Stage::MomentumEnded
            && wheel::stage_of(0, 16) == Stage::MomentumEnded,
        "a lift, a cancel and the momentum"
    );
    // Fingers moving down (a positive y) show what is above; moving right shows the left.
    let start = (100.0, 900.0);
    assert!(
        wheel::origin(start, (0.0, 120.0), None, minimum, maximum) == (100.0, 780.0)
            && wheel::origin(start, (-60.0, 0.0), None, minimum, maximum) == (160.0, 900.0)
            && wheel::origin(start, (30.0, -40.0), None, minimum, maximum) == (70.0, 940.0),
        "a wheel moves the origin against its delta"
    );
    assert!(
        wheel::axis_of((-6.0, 12.0)) == Some(Axis::Vertical)
            && wheel::axis_of((-12.0, 6.0)) == Some(Axis::Horizontal)
            && wheel::axis_of((0.0, 0.0)).is_none(),
        "a movement's axis is its larger part"
    );
    assert!(
        wheel::origin(start, (-6.0, 12.0), Some(Axis::Vertical), minimum, maximum) == (100.0, 888.0)
            && wheel::origin(start, (-12.0, 6.0), Some(Axis::Horizontal), minimum, maximum) == (112.0, 900.0),
        "a gesture keeps to its axis"
    );
    assert!(
        wheel::origin((10.0, 20.0), (50.0, 100.0), None, minimum, maximum) == (0.0, 0.0)
            && wheel::origin((230.0, 1_790.0), (-50.0, -100.0), None, minimum, maximum) == maximum,
        "a wheel stops at the edges"
    );
    assert_eq!(
        wheel::origin((5.0, 5.0), (10.0, 10.0), None, (10.0, f64::NAN), (0.0, 100.0)),
        (5.0, 5.0),
        "a range that is not valid keeps the origin"
    );
    // A code block that scrolls sideways, at its left edge.
    let code_maximum = (400.0, 0.0);
    let right = wheel::can_move((0.0, 0.0), (-20.0, 0.0), Some(Axis::Horizontal), (0.0, 0.0), code_maximum);
    let left = wheel::can_move((0.0, 0.0), (20.0, 0.0), Some(Axis::Horizontal), (0.0, 0.0), code_maximum);
    let down = wheel::can_move((0.0, 0.0), (0.0, -20.0), Some(Axis::Vertical), (0.0, 0.0), code_maximum);
    assert!(right && !left && !down, "an inner scroll view can move only where it has room");
    // Innermost first, the page last: the code block takes a sideways scroll it can make, and the page takes the rest.
    assert!(
        wheel::taker(&[right, true]) == 0
            && wheel::taker(&[left, true]) == 1
            && wheel::taker(&[down, true]) == 1
            && wheel::taker(&[false, false]) == 1
            && wheel::taker(&[false]) == 0,
        "the innermost scroll view that can move takes the wheel, else the page"
    );
}

#[test]
fn a_gesture_is_decided_once_and_is_the_users_until_it_settles() {
    let mut w = Tracker::new();
    assert!(w.feed(Stage::Began, true, (0.0, 0.0), 10.0) == Action::Hold && w.axis().is_none(), "fingers on the page hold the event");
    assert!(
        w.feed(Stage::Changed, false, (-3.0, 12.0), 10.02) == Action::Choose && w.axis() == Some(Axis::Vertical),
        "the first movement chooses the taker and the axis"
    );
    assert!(
        w.feed(Stage::Changed, false, (-8.0, 4.0), 10.04) == Action::Move && w.axis() == Some(Axis::Vertical),
        "the gesture keeps its taker and its axis"
    );
    assert!(
        w.feed(Stage::Ended, false, (0.0, 0.0), 10.06) == Action::Hold
            && w.feed(Stage::Momentum, false, (0.0, 9.0), 10.1) == Action::Move
            && w.feed(Stage::MomentumEnded, false, (0.0, 0.0), 10.5) == Action::Hold,
        "the momentum is the gesture's"
    );
    assert_eq!(w.feed(Stage::Momentum, false, (0.0, 9.0), 11.0), Action::Pass, "momentum after the gesture is over is not this page's");

    w = Tracker::new();
    assert!(
        w.feed(Stage::Began, false, (0.0, 0.0), 10.0) == Action::Pass
            && w.feed(Stage::Changed, true, (0.0, 12.0), 10.02) == Action::Pass
            && w.feed(Stage::Ended, true, (0.0, 0.0), 10.04) == Action::Pass
            && w.feed(Stage::Momentum, true, (0.0, 8.0), 10.06) == Action::Pass
            && !w.user_scrolling(10.06),
        "a gesture that began elsewhere is never this page's"
    );
    w = Tracker::new();
    assert!(
        w.feed(Stage::Began, true, (-14.0, 2.0), 10.0) == Action::Choose
            && w.axis() == Some(Axis::Horizontal)
            && w.feed(Stage::Changed, true, (-14.0, 2.0), 10.02) == Action::Move,
        "a gesture that moves at once chooses at its start"
    );
    w = Tracker::new();
    assert!(
        w.feed(Stage::Step, true, (0.0, -30.0), 10.0) == Action::Choose
            && w.axis().is_none()
            && w.feed(Stage::Step, true, (0.0, -30.0), 10.05) == Action::Choose
            && w.feed(Stage::Step, false, (0.0, -30.0), 10.1) == Action::Pass,
        "a mouse wheel step is decided on its own"
    );
    assert_eq!(w.feed(Stage::Momentum, true, (0.0, 8.0), 10.15), Action::Pass, "a step ends any gesture before it");
    w = Tracker::new();
    w.feed(Stage::Began, true, (0.0, 0.0), 10.0);
    w.feed(Stage::Changed, true, (0.0, 12.0), 10.02);
    w.feed(Stage::Ended, true, (0.0, 0.0), 10.04);
    assert!(
        w.feed(Stage::Began, false, (0.0, 0.0), 12.0) == Action::Pass && w.feed(Stage::Momentum, false, (0.0, 8.0), 12.2) == Action::Pass,
        "a new gesture elsewhere ends one this page still held"
    );
    w = Tracker::new();
    w.feed(Stage::Began, true, (0.0, 0.0), 10.0);
    w.feed(Stage::Changed, true, (0.0, 12.0), 10.02);
    w.feed(Stage::Ended, true, (0.0, 0.0), 10.04);
    assert!(
        w.feed(Stage::Momentum, false, (0.0, 8.0), 10.04 + wheel::SETTLE_GRACE + 0.2) == Action::Pass
            && w.feed(Stage::MomentumEnded, false, (0.0, 0.0), 10.04 + wheel::SETTLE_GRACE + 0.4) == Action::Pass,
        "momentum long after a gesture ended is not this page's"
    );

    // The user's scroll: from the gesture's start until just after its last event.
    w = Tracker::new();
    assert!(!w.user_scrolling(10.0), "no gesture, no scroll of the user's");
    w.feed(Stage::Began, true, (0.0, 0.0), 10.0);
    assert!(w.user_scrolling(10.5), "fingers resting before they move are already the user's");
    w.feed(Stage::Changed, true, (0.0, 12.0), 10.6);
    w.feed(Stage::Ended, true, (0.0, 0.0), 10.7);
    assert!(w.user_scrolling(10.7 + wheel::SETTLE_GRACE - 0.01), "between the lift and the momentum");
    w.feed(Stage::Momentum, true, (0.0, 9.0), 10.8);
    assert!(w.user_scrolling(11.4), "during the momentum");
    w.feed(Stage::MomentumEnded, true, (0.0, 0.0), 11.5);
    assert!(
        w.user_scrolling(11.5 + wheel::SETTLE_GRACE - 0.01) && !w.user_scrolling(11.5 + wheel::SETTLE_GRACE + 0.01),
        "the moment after the momentum ends"
    );
    w = Tracker::new();
    w.feed(Stage::Step, true, (0.0, -30.0), 10.0);
    assert!(
        w.user_scrolling(10.0 + wheel::SETTLE_GRACE - 0.01) && !w.user_scrolling(10.0 + wheel::SETTLE_GRACE + 0.01),
        "a mouse wheel step is the user's for a moment"
    );
    w = Tracker::new();
    w.feed(Stage::Began, true, (0.0, 0.0), 10.0);
    w.feed(Stage::Changed, true, (0.0, 12.0), 10.1);
    assert!(
        w.user_scrolling(10.1 + wheel::STALE_AFTER - 0.01) && !w.user_scrolling(10.1 + wheel::STALE_AFTER + 0.01),
        "a gesture whose end never came goes stale"
    );

    let idle = Tracker::new();
    assert!(
        !wheel::user_moving(false, false, 0.0, &idle, 100.0)
            && wheel::user_moving(true, false, 0.0, &idle, 100.0)
            && wheel::user_moving(false, true, 0.0, &idle, 100.0)
            && wheel::user_moving(false, false, 100.0 - pan::RELEASE_GRACE + 0.01, &idle, 100.0),
        "the user moves the page by scroll, drag or the moment after one"
    );
    w = Tracker::new();
    w.feed(Stage::Began, true, (0.0, 0.0), 99.9);
    assert!(wheel::user_moving(false, false, 0.0, &w, 100.0), "a wheel gesture is the user's move");

    // Symptom: a trackpad scroll never reached the system's scroll phase, so a page that grew under it was read as a layout and followed back.
    let at = |origin: f64, document: f64| Page::new(origin, 600.0, document, Some("b"), Some(document - 8.0));
    let mut watch = PageWatch::new();
    assert!(watch.observe(&at(1_400.0, 2_000.0), false).is_none() && watch.following(), "the page follows at its end");
    w = Tracker::new();
    w.feed(Stage::Began, true, (0.0, 0.0), 10.0);
    w.feed(Stage::Changed, true, (0.0, 20.0), 10.02);
    let moving = wheel::user_moving(false, false, 0.0, &w, 10.03);
    assert!(
        moving && watch.observe(&at(1_380.0, 2_300.0), moving).is_none() && !watch.following(),
        "follow never scrolls while a wheel gesture is going"
    );
    let mut blind = PageWatch::new();
    blind.observe(&at(1_400.0, 2_000.0), false);
    assert_eq!(
        blind.observe(&at(1_380.0, 2_300.0), false),
        Some(Scroll::BOTTOM),
        "without the wheel known, the same report pulled the reader back down"
    );
}

#[test]
fn a_vertical_wheel_moves_the_column_and_a_sideways_one_the_page() {
    // Under the pointer: the column, then the page; each confined to its own axis.
    let page = pan::confined((0.0, 0.0), (240.0, 0.0), (0.0, 0.0), Axis::Horizontal);
    let column = pan::confined((0.0, 0.0), (0.0, 1_800.0), (0.0, 900.0), Axis::Vertical);
    let takers = |delta: (f64, f64), axis: Option<Axis>, column_at: (f64, f64)| {
        wheel::taker(&[
            wheel::can_move(column_at, delta, axis, column.0, column.1),
            wheel::can_move((0.0, 0.0), delta, axis, page.0, page.1),
        ])
    };
    assert!(
        takers((-2.0, 30.0), Some(Axis::Vertical), (0.0, 900.0)) == 0 && takers((-15.0, 1.0), Some(Axis::Horizontal), (0.0, 900.0)) == 1,
        "a vertical scroll moves the column, a sideways one the page"
    );
    // A mouse wheel's sideways step has no axis: the column cannot move that way, the page takes it.
    assert_eq!(takers((-20.0, 0.0), None, (0.0, 900.0)), 1, "a sideways wheel step goes to the page");
    // A column at its top shows nothing more above: the page takes the scroll and, moving sideways only, moves nothing.
    assert!(
        takers((0.0, 30.0), Some(Axis::Vertical), (0.0, 0.0)) == 1
            && wheel::origin((0.0, 0.0), (0.0, 30.0), Some(Axis::Vertical), page.0, page.1) == (0.0, 0.0),
        "a column at its top passes the scroll to a page that does not move"
    );
}
