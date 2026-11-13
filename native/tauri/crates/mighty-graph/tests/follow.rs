//! The diagram's follow mode (C# MightyGraphFollowVerification, MightyCode.Core.Tests/MightyGraphFollowVerification.cs): the newest
//! block's bottom stays right above the composer while the reader is there, and a reader who moved away is left where they are.

mod view_support;

use std::collections::HashSet;

use mighty_core::graph::{MightyGraphAgent, MightyGraphRun, node_id};
use mighty_graph::camera::{self, Anchor, GraphRect, Point, Size};
use mighty_graph::follow::{self, RunChange, Watch};
use mighty_graph::layout::{self, Layout, Options};
use mighty_graph::view_model;
use view_support::*;

const VIEWPORT: Size = Size::new(900.0, 600.0);

fn id(run: &str, suffix: &str) -> String {
    node_id(run, suffix)
}

fn agent_of(id: &str, parent_id: Option<&str>, status: &str) -> MightyGraphAgent {
    let mut a = agent(id, id, "", status);
    a.parent_id = parent_id.map(str::to_string);
    a
}

fn run_of(id: &str, status: &str, agents: Vec<MightyGraphAgent>) -> MightyGraphRun {
    with_agents(run(id, &format!("request {id}"), status), agents)
}

fn make(runs: &[MightyGraphRun], draft: &str, running: bool) -> Layout {
    layout::make(runs, draft, running, &HashSet::new(), &Options { splits_execution: true, ..Options::default() }, &ko())
}

fn frame(layout: &Layout, id: &str) -> GraphRect {
    layout.nodes.iter().find(|n| n.id == id).unwrap().frame
}

fn bottom(frame: GraphRect, camera: Point, zoom: f64) -> f64 {
    camera.y + frame.max_y() * zoom
}

/// "mighty follow: typing a draft never changes what the camera follows" (TypingADraftNeverChangesWhatTheCameraFollows).
#[test]
fn typing_a_draft_never_changes_what_the_camera_follows() {
    let runs = vec![run_of("a", "running", vec![])];
    let (idle, typing) = (make(&runs, "", true), make(&runs, "다음 요청", true));
    check(typing.nodes.iter().any(|n| n.kind == "draft"), "a typed draft has its block");
    check(
        follow::target_id(&typing, &runs) == Some(id("a", layout::WORK_SUFFIX)) && follow::target_id(&idle, &runs) == follow::target_id(&typing, &runs),
        "the 실행 block is followed with or without a draft",
    );
    let done = vec![run_of("a", "completed", vec![])];
    check(
        follow::target_id(&make(&done, "", false), &done) == Some(id("a", "result")) && follow::target_id(&make(&done, "x", false), &done) == Some(id("a", "result")),
        "the result is followed, never the draft under it",
    );
}

/// "mighty follow: the followed block is the newest request's lowest block" (TheFollowedBlockIsTheNewestRequestsLowestBlock).
#[test]
fn the_followed_block_is_the_newest_requests_lowest_block() {
    let running = vec![
        run_of("old", "completed", vec![]),
        run_of("a", "running", vec![agent_of("one", None, "running"), agent_of("deep", Some("one"), "running")]),
    ];
    check(follow::target_id(&make(&running, "", true), &running) == Some(id("a", "agent:deep")), "the deepest sub-agent");
    let finished = vec![
        running[0].clone(),
        run_of("a", "completed", vec![agent_of("one", None, "completed"), agent_of("deep", Some("one"), "completed")]),
    ];
    check(follow::target_id(&make(&finished, "", false), &finished) == Some(id("a", "result")), "then the result");
    let siblings = vec![run_of("a", "running", vec![agent_of("left", None, "running"), agent_of("right", None, "running")])];
    check(follow::target_id(&make(&siblings, "", true), &siblings) == Some(id("a", "agent:right")), "of one row the block added last");
    check(follow::target_id(&make(&[], "", false), &[]).as_deref() == Some(camera::PENDING_NODE_ID), "with no request the draft block");
    let planning = vec![run_of("a", "running", vec![])];
    let options = Options {
        plan_run_id: Some("a".into()),
        plan_records: vec![("a".into(), "earlier".into())],
        splits_execution: true,
        ..Options::default()
    };
    let plan = layout::make(&planning, "", true, &HashSet::new(), &options, &ko());
    check(
        plan.nodes.iter().any(|n| n.kind == "planRecord") && follow::target_id(&plan, &planning) == Some(id("a", layout::PLAN_SUFFIX)),
        "the waiting plan, not the answered one beside the flow",
    );
}

/// "mighty follow: a send puts the new request's bottom just above the composer" (ASendPutsTheNewRequestsBottomJustAboveTheComposer).
#[test]
fn a_send_puts_the_new_requests_bottom_just_above_the_composer() {
    let runs = vec![run_of("old", "completed", vec![]), run_of("new", "running", vec![])];
    let graph = make(&runs, "", true);
    let target = follow::target_id(&graph, &runs);
    check(target == Some(id("new", layout::WORK_SUFFIX)), "a sent request is followed to its 실행 block");
    let work = frame(&graph, &target.unwrap());
    for zoom in [1.0, 0.7] {
        let camera = follow::camera_offset(work, VIEWPORT, zoom, false, false, true);
        check(
            (bottom(work, camera, zoom) - (VIEWPORT.h - follow::MARGIN)).abs() < 0.001 && camera.x == follow::centred_x(VIEWPORT, zoom),
            format!("its bottom 16pt above the composer on the centreline at {zoom}"),
        );
    }
    check(
        follow::camera_offset(work, VIEWPORT, 1.0, true, false, false) == camera::camera_offset(work, VIEWPORT, 1.0, true),
        "any other aim is the shared rule's",
    );
}

/// "mighty follow: a following camera puts each new block right above the composer" (AFollowingCameraPutsEachNewBlockRightAboveTheComposer).
#[test]
fn a_following_camera_puts_each_new_block_right_above_the_composer() {
    let mut watch = Watch::new();
    let work = GraphRect::new(24.0, 200.0, 500.0, 280.0);
    let camera = follow::camera(work, VIEWPORT, 1.0, 50.0, follow::MARGIN);
    watch.start();
    check(watch.observe(Some("work"), Some(work), camera, VIEWPORT, 1.0).is_none(), "placed, nothing moves");
    let agent = GraphRect::new(94.0, 512.0, 360.0, 240.0);
    let moved = watch.observe(Some("agent"), Some(agent), camera, VIEWPORT, 1.0);
    check(
        moved.is_some_and(|to| bottom(agent, to, 1.0) == VIEWPORT.h - follow::MARGIN && to.x == camera.x),
        format!("a new block right above the composer, only up and down: {moved:?}"),
    );
    check(watch.observe(Some("agent"), Some(agent), moved.unwrap(), VIEWPORT, 1.0).is_none() && watch.following(), "and then it stays");
}

/// "mighty follow: a taller composer keeps the followed block above it" (ATallerComposerKeepsTheFollowedBlockAboveIt).
#[test]
fn a_taller_composer_keeps_the_followed_block_above_it() {
    let mut watch = Watch::new();
    let block = GraphRect::new(24.0, 900.0, 500.0, 280.0);
    let camera = follow::camera(block, VIEWPORT, 1.0, 0.0, follow::MARGIN);
    watch.start();
    watch.observe(Some("b"), Some(block), camera, VIEWPORT, 1.0);
    let shorter = Size::new(VIEWPORT.w, VIEWPORT.h - 40.0);
    let kept = watch.observe(Some("b"), Some(block), camera, shorter, 1.0);
    check(
        kept.is_some_and(|k| bottom(block, k, 1.0) == shorter.h - follow::MARGIN && k.x == camera.x),
        "a shorter canvas keeps the block above the composer",
    );
    check(watch.observe(Some("b"), Some(block), kept.unwrap(), VIEWPORT, 1.0) == Some(camera), "and back");
}

/// "mighty follow: a reader who moved away is never pulled back" (AReaderWhoMovedAwayIsNeverPulledBack).
#[test]
fn a_reader_who_moved_away_is_never_pulled_back() {
    let mut watch = Watch::new();
    let block = GraphRect::new(24.0, 900.0, 500.0, 280.0);
    let up = Point::new(0.0, VIEWPORT.h - follow::MARGIN - block.max_y() + 200.0);
    check(!follow::is_at_newest(block, up, VIEWPORT, 1.0), "200pt behind the composer is not at the newest");
    watch.moved(Some("b"), Some(block), up, VIEWPORT, 1.0);
    check(!watch.following(), "a move away stops following");
    let next = GraphRect::new(24.0, 1_212.0, 500.0, 200.0);
    check(
        watch.observe(Some("c"), Some(next), up, VIEWPORT, 1.0).is_none() && watch.observe(Some("c"), Some(next), up, Size::new(900.0, 500.0), 1.0).is_none(),
        "neither a new block nor a new viewport moves the reader",
    );
    check(!follow::is_at_newest(block, Point::new(0.0, -block.max_y() - 50.0), VIEWPORT, 1.0), "past the newest block is not at it");
    check(
        !follow::is_at_newest(block, Point::new(0.0, f64::NAN), VIEWPORT, 1.0)
            && !follow::is_at_newest(block, up, Size::new(0.0, 600.0), 1.0)
            && !follow::is_at_newest(block, up, VIEWPORT, 0.0),
        "broken input is never at the newest",
    );
}

/// "mighty follow: a reader who scrolled up before a layout landed is not followed through it"
/// (AReaderWhoScrolledUpBeforeALayoutLandedIsNotFollowedThroughIt).
#[test]
fn a_reader_who_scrolled_up_before_a_layout_landed_is_not_followed_through_it() {
    let mut watch = Watch::new();
    let viewport = Size::new(1_020.0, 611.0);
    let block = GraphRect::new(24.0, 30_000.0, 360.0, 240.0);
    let aligned = follow::camera(block, viewport, 1.0, 236.0, follow::MARGIN);
    watch.start();
    check(watch.observe(Some("b"), Some(block), aligned, viewport, 1.0).is_none(), "settled");
    let up = Point::new(aligned.x, aligned.y + 120.0);
    watch.moved(Some("b"), Some(block), up, viewport, 1.0);
    check(!watch.following(), "120pt up is away");
    let trimmed = block.offset_by(0.0, -10_112.0);
    check(watch.observe(Some("b"), Some(trimmed), up, viewport, 1.0).is_none(), "a trim does not pull the reader back");
    check(
        watch.observe(Some("late"), Some(GraphRect::new(640.0, trimmed.y, 360.0, 240.0)), up, viewport, 1.0).is_none() && !watch.following(),
        "nor does a new block",
    );
}

/// "mighty follow: a reader who comes back to the newest block follows again" (AReaderWhoComesBackToTheNewestBlockFollowsAgain).
#[test]
fn a_reader_who_comes_back_to_the_newest_block_follows_again() {
    let mut watch = Watch::new();
    let block = GraphRect::new(24.0, 900.0, 500.0, 280.0);
    watch.stop();
    check(!watch.following(), "stopped");
    let near = Point::new(0.0, VIEWPORT.h - follow::MARGIN - block.max_y() + follow::SLACK - 1.0);
    watch.moved(Some("b"), Some(block), near, VIEWPORT, 1.0);
    check(watch.following() && follow::SLACK == 36.0 && follow::MARGIN == 16.0, "back within the slack follows again");
    check(watch.observe(Some("b"), Some(block), near, VIEWPORT, 1.0).is_none(), "the block stays where the reader left it");
    let next = GraphRect::new(24.0, 1_212.0, 500.0, 200.0);
    check(
        watch.observe(Some("c"), Some(next), near, VIEWPORT, 1.0).is_some_and(|m| bottom(next, m, 1.0) == VIEWPORT.h - follow::MARGIN),
        "a new block goes right above the composer",
    );
}

/// "mighty follow: a reader below the newest block keeps their place while it settles" (AReaderBelowTheNewestBlockKeepsTheirPlaceWhileItSettles).
#[test]
fn a_reader_below_the_newest_block_keeps_their_place_while_it_settles() {
    let mut watch = Watch::new();
    let result = GraphRect::new(24.0, 900.0, 500.0, 400.0);
    let camera = Point::new(0.0, 300.0 - result.max_y());
    watch.moved(Some("r"), Some(result), camera, VIEWPORT, 1.0);
    check(watch.following(), "the newest block's bottom on screen is at the newest");
    let measured = GraphRect::new(24.0, 900.0, 500.0, 250.0);
    check(
        watch.observe(Some("r"), Some(measured), camera, VIEWPORT, 1.0).is_some_and(|k| bottom(measured, k, 1.0) == 300.0),
        "a block settling to its height keeps its bottom where it was",
    );
}

/// "mighty follow: a trim above keeps the followed block where it was" (ATrimAboveKeepsTheFollowedBlockWhereItWas).
#[test]
fn a_trim_above_keeps_the_followed_block_where_it_was() {
    let mut watch = Watch::new();
    let block = GraphRect::new(24.0, 9_000.0, 500.0, 280.0);
    let camera = follow::camera(block, VIEWPORT, 0.8, 10.0, follow::MARGIN);
    watch.start();
    watch.observe(Some("b"), Some(block), camera, VIEWPORT, 0.8);
    let trimmed = block.offset_by(0.0, -6_000.0);
    check(
        watch
            .observe(Some("b"), Some(trimmed), camera, VIEWPORT, 0.8)
            .is_some_and(|k| (bottom(trimmed, k, 0.8) - (VIEWPORT.h - follow::MARGIN)).abs() < 0.001),
        "a trim above keeps the followed block above the composer",
    );
}

/// "mighty follow: opening or resizing a block never moves the camera"
/// (OpeningOrResizingABlockNeverMovesTheCameraAndKeepsFollowOnlyAtTheNewest).
#[test]
fn opening_or_resizing_a_block_never_moves_the_camera_and_keeps_follow_only_at_the_newest() {
    let mut watch = Watch::new();
    let block = GraphRect::new(24.0, 900.0, 500.0, 280.0);
    let camera = follow::camera(block, VIEWPORT, 1.0, 0.0, follow::MARGIN);
    watch.start();
    check(watch.observe(Some("b"), Some(block), camera, VIEWPORT, 1.0).is_none(), "settled");
    watch.user_changed_layout();
    check(
        watch.observe(Some("b"), Some(GraphRect::new(24.0, 900.0, 500.0, 300.0)), camera, VIEWPORT, 1.0).is_none() && watch.following(),
        "opened within the slack: nothing moves, still following",
    );
    watch.user_changed_layout();
    check(
        watch.observe(Some("b"), Some(GraphRect::new(24.0, 900.0, 500.0, 700.0)), camera, VIEWPORT, 1.0).is_none() && !watch.following(),
        "opened past the composer: nothing moves, following stops",
    );
    watch.user_changed_layout();
    check(watch.observe(Some("b"), Some(block), camera, VIEWPORT, 1.0).is_none() && !watch.following(), "a layout change never starts following");
}

/// "mighty follow: a block taller than the viewport shows its bottom" (ABlockTallerThanTheViewportShowsItsBottom).
#[test]
fn a_block_taller_than_the_viewport_shows_its_bottom() {
    let tall = GraphRect::new(24.0, 100.0, 500.0, 2_000.0);
    let camera = follow::camera(tall, VIEWPORT, 1.0, 0.0, follow::MARGIN);
    check(bottom(tall, camera, 1.0) == VIEWPORT.h - follow::MARGIN && follow::is_at_newest(tall, camera, VIEWPORT, 1.0), "a tall block shows its bottom");
    check(!follow::is_at_newest(tall, Point::new(0.0, camera.y + 400.0), VIEWPORT, 1.0), "scrolled up inside it the reader is reading");
}

/// "mighty follow: an aim elsewhere stops following until the next send" (AnAimElsewhereStopsFollowingUntilTheNextSend).
#[test]
fn an_aim_elsewhere_stops_following_until_the_next_send() {
    let mut watch = Watch::new();
    watch.stop();
    let block = GraphRect::new(24.0, 900.0, 500.0, 280.0);
    check(watch.observe(Some("b"), Some(block), Point::new(0.0, 0.0), VIEWPORT, 1.0).is_none(), "stopped, nothing follows");
    watch.start();
    check(
        watch
            .observe(Some("b"), Some(block), Point::new(0.0, 0.0), VIEWPORT, 1.0)
            .is_some_and(|p| bottom(block, p, 1.0) == VIEWPORT.h - follow::MARGIN && p.x == 0.0),
        "a send follows again",
    );
}

fn ids(values: &[&str]) -> Vec<String> {
    values.iter().map(|v| v.to_string()).collect()
}

/// "mighty follow: the run-id rules follow a send and re-aim a trim in every view" (TheRunIdRulesFollowASendAndReaimATrimInEveryView).
#[test]
fn the_run_id_rules_follow_a_send_and_reaim_a_trim_in_every_view() {
    let changed = |previous: Option<&[&str]>, current: &[&str], following: bool| {
        follow::runs_changed(previous.map(ids).as_deref(), &ids(current), following)
    };
    check(changed(None, &["a"], false) == RunChange::First && changed(None, &[], true) == RunChange::First, "the first observation follows");
    check(
        changed(Some(&["a", "b"]), &["a", "b"], false) == RunChange::None && changed(Some(&["a", "b"]), &["a", "b"], true) == RunChange::None,
        "the same list asks nothing, so a block revealed after it keeps the camera",
    );
    check(
        changed(Some(&["a"]), &["a", "b"], false) == RunChange::Follow
            && changed(Some(&[]), &["a"], false) == RunChange::Follow
            && changed(Some(&["a", "b"]), &["b", "c"], true) == RunChange::Follow,
        "a new last request is a send",
    );
    check(
        changed(Some(&["a", "b", "c"]), &["b", "c"], false) == RunChange::Trim
            && changed(Some(&["b", "c"]), &["z", "b", "c"], false) == RunChange::Trim
            && changed(Some(&["a"]), &[], false) == RunChange::Trim,
        "any other change is a trim",
    );
    check(
        changed(Some(&["a", "b", "c"]), &["b", "c"], true) == RunChange::None && changed(Some(&["a"]), &[], true) == RunChange::None,
        "a following camera needs no re-aim",
    );
}

/// "mighty follow: older requests loaded above and a zoom keep the reader's place" (OlderRequestsLoadedAboveAndAZoomKeepTheReadersPlace).
#[test]
fn older_requests_loaded_above_and_a_zoom_keep_the_readers_place() {
    let nodes: HashSet<String> = HashSet::from([id("b", "request"), id("c", "request")]);
    check(camera::trim_anchor(&ids(&["b", "c"]), &ids(&["z", "a", "b", "c"]), None, &nodes) == Anchor::Hold, "loading older requests holds");
    check(
        camera::trim_anchor(&ids(&["a", "b", "c"]), &ids(&["b", "c"]), None, &nodes) == Anchor::reaim(&id("c", "request"), true),
        "a trim re-aims at the newest request",
    );
    check(camera::trim_anchor(&ids(&["a", "c"]), &ids(&["x", "b", "c"]), None, &nodes) == Anchor::reaim(&id("c", "request"), true), "a different list re-aims");
    let pan = Point::new(-120.0, -900.0);
    for (from, to) in [(1.0, 1.5), (1.5, 0.5), (0.7, 0.8)] {
        let zoomed = view_model::zoom_anchored(pan, VIEWPORT, from, to);
        let (before_x, before_y) = ((VIEWPORT.w / 2.0 - pan.x) / from, (VIEWPORT.h / 2.0 - pan.y) / from);
        check(
            (before_x * to + zoomed.x - VIEWPORT.w / 2.0).abs() < 0.001 && (before_y * to + zoomed.y - VIEWPORT.h / 2.0).abs() < 0.001,
            format!("the centre keeps its place from {from} to {to}"),
        );
    }
    check(
        view_model::zoom_anchored(pan, VIEWPORT, 0.0, 1.0) == pan && view_model::zoom_anchored(pan, VIEWPORT, 1.0, f64::NAN) == pan,
        "a broken zoom leaves the pan",
    );
}
