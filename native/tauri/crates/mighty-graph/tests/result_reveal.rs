//! A new Mighty result shows right above the composer (C# ResultRevealVerification, MightyCode.Core.Tests/ResultRevealVerification.cs): the
//! newest result card is as tall as its content up to its cap, and the camera scrolls once when a request of the pane finishes.

mod view_support;

use std::collections::HashSet;

use mighty_core::graph::{GraphBlockSize, MightyGraphRun, node_id};
use mighty_graph::camera::{self, GraphRect, ResultReveal, RunProgress, Size};
use mighty_graph::layout::{self, Layout, Options, ResizePhase};
use view_support::*;

fn run_of(id: &str, status: &str) -> MightyGraphRun {
    let mut r = run(id, &format!("요청 {id}"), status);
    r.final_output = (status == "completed").then(|| format!("결과 {id}"));
    r
}

fn done(id: &str) -> MightyGraphRun {
    run_of(id, "completed")
}

fn result_id(run: &str) -> String {
    node_id(run, "result")
}

fn frame(layout: &Layout, id: &str) -> Option<GraphRect> {
    layout.node(id).map(|n| n.frame)
}

fn size_of(layout: &Layout, id: &str) -> Option<Size> {
    frame(layout, id).map(|f| Size::new(f.w, f.h))
}

fn canvas(
    runs: &[MightyGraphRun],
    viewport: Option<Size>,
    content: Option<f64>,
    saved: Option<GraphBlockSize>,
    zoom: Option<f64>,
) -> Layout {
    let options = Options { viewport, zoom, shared_result_size: saved, result_content_height: content, ..Options::default() };
    layout::make(runs, "", false, &HashSet::new(), &options, &ko())
}

fn near(a: f64, b: f64) -> bool {
    (a - b).abs() < 0.001
}

fn size(w: f64, h: f64) -> GraphBlockSize {
    GraphBlockSize { width: w, height: h }
}

/// "mighty result reveal: the newest result is as tall as its content up to its cap" (TheNewestResultIsAsTallAsItsContentUpToItsCap).
#[test]
fn the_newest_result_is_as_tall_as_its_content_up_to_its_cap() {
    check(
        layout::result_size(Size::new(1_100.0, 800.0), Some(260.0)) == Size::new(1_100.0, 260.0),
        "a short answer shrinks below the saved size",
    );
    check(layout::result_size(Size::new(700.0, 350.0), Some(2_000.0)) == Size::new(700.0, 350.0), "a long answer stops at the saved size");
    check(
        layout::result_size(Size::new(700.0, 350.0), Some(60.0)).h == layout::MINIMUM_RESULT_HEIGHT,
        "a tiny answer keeps the minimum height",
    );
    check(
        layout::result_size(Size::new(700.0, 350.0), Some(0.0)).h == layout::MINIMUM_RESULT_HEIGHT,
        "an empty answer keeps the minimum height",
    );
    for broken in [None, Some(f64::NAN), Some(f64::INFINITY), Some(-5.0)] {
        check(
            layout::result_size(Size::new(700.0, 350.0), broken) == Size::new(700.0, 350.0),
            format!("unmeasured or broken content takes the cap: {broken:?}"),
        );
    }
    check(
        layout::result_size(Size::new(700.0, 900.0), Some(300.2)).h == 301.0,
        "fractional content rounds up so the last line is never clipped",
    );
    check(layout::MINIMUM_RESULT_HEIGHT == mighty_core::graph::MINIMUM_HEIGHT, "the result minimum is the block minimum");
}

/// "mighty result reveal: the saved size is a maximum for every new result" (TheSavedSizeIsAMaximumForEveryNewResult).
#[test]
fn the_saved_size_is_a_maximum_for_every_new_result() {
    let viewport = Some(Size::new(1_200.0, 800.0));
    let saved = Some(size(900.0, 600.0));
    check(
        size_of(&canvas(&[done("one")], viewport, Some(180.0), saved, None), &result_id("one")) == Some(Size::new(900.0, 180.0)),
        "a short answer is as tall as its content at the saved width",
    );
    check(
        size_of(&canvas(&[done("one"), done("two")], viewport, Some(1_500.0), saved, None), &result_id("two"))
            == Some(Size::new(900.0, 600.0)),
        "a long answer is the saved size, scrolling inside",
    );
    let fitted = canvas(&[done("one")], viewport, Some(240.0), None, None);
    check(
        size_of(&fitted, &result_id("one")) == Some(Size::new(1_152.0, 240.0)) && fitted.fitted_result_id == Some(result_id("one")),
        "with nothing saved the window fit is the maximum",
    );
    check(
        size_of(&canvas(&[done("one")], viewport, Some(5_000.0), None, None), &result_id("one")) == Some(Size::new(1_152.0, 752.0)),
        "a long answer stops at the window fit",
    );
}

/// "mighty result reveal: the cap is the visible pane" (TheCapIsTheVisiblePane).
#[test]
fn the_cap_is_the_visible_pane() {
    let saved = Some(size(900.0, 700.0));
    check(
        size_of(&canvas(&[done("one")], Some(Size::new(600.0, 400.0)), Some(200.0), saved, Some(1.0)), &result_id("one"))
            == Some(Size::new(552.0, 200.0)),
        "a short answer fits under the pane-bound cap",
    );
    check(
        size_of(&canvas(&[done("one")], Some(Size::new(600.0, 400.0)), Some(5_000.0), saved, Some(1.0)), &result_id("one"))
            == Some(Size::new(552.0, 352.0)),
        "a long answer stops at the visible pane",
    );
    let zoomed = size_of(&canvas(&[done("one")], Some(Size::new(900.0, 700.0)), Some(5_000.0), saved, Some(1.5)), &result_id("one"));
    check(
        zoomed.is_some_and(|z| near(z.w, 568.0) && near(z.h, 652.0 / 1.5)),
        format!("zoomed in the cap is the pane at that zoom: {zoomed:?}"),
    );
}

/// "mighty result reveal: shrinking to content never writes the saved size" (ShrinkingToContentNeverWritesTheSavedSize).
#[test]
fn shrinking_to_content_never_writes_the_saved_size() {
    let saved = size(900.0, 700.0);
    let viewport = Some(Size::new(1_200.0, 800.0));
    let shown = size_of(&canvas(&[done("one")], viewport, Some(180.0), Some(saved), Some(1.0)), &result_id("one")).unwrap();
    check(shown == Size::new(900.0, 180.0), "the card shows its content height");
    let made = canvas(&[done("one")], viewport, Some(180.0), Some(saved), Some(1.0));
    let release = |dragged: Size| {
        let (horizontal, vertical) = layout::result_drag_sides(shown, dragged);
        layout::result_drag(dragged, horizontal, vertical, ResizePhase::Finished, Some(&saved), made.result_limit, made.result_window_fit)
    };
    check(release(shown).1 == Some(saved), "a click on the grip saves the saved size, not the shrunk one");
    check(release(Size::new(1_000.0, 180.0)).1 == Some(size(1_000.0, 700.0)), "a width-only drag keeps the maximum height");
    check(release(Size::new(900.0, 320.0)).1 == Some(size(900.0, 320.0)), "a height drag saves what it was released at");
    check(layout::result_drag_sides(Size::new(900.0, 180.0), Size::new(900.2, 180.3)) == (false, false), "sub-pixel jitter moves no side");
    check(
        layout::result_drag(
            Size::new(900.0, 180.0),
            true,
            true,
            ResizePhase::Cancelled,
            Some(&saved),
            made.result_limit,
            made.result_window_fit,
        ) == (None, None),
        "a cancelled drag saves nothing",
    );
    check(saved == size(900.0, 700.0), "laying out a shrunk card leaves the saved size alone");
}

/// "mighty result reveal: a shorter result moves nothing above it and pulls the draft up" (AShorterResultMovesNothingAboveItAndPullsTheDraftUp).
#[test]
fn a_shorter_result_moves_nothing_above_it_and_pulls_the_draft_up() {
    let viewport = Some(Size::new(1_200.0, 800.0));
    let full = canvas(&[done("one")], viewport, None, None, None);
    let fitted = canvas(&[done("one")], viewport, Some(200.0), None, None);
    let request = node_id("one", "request");
    check(frame(&full, &request) == frame(&fitted, &request), "the request above does not move");
    check(frame(&full, &result_id("one")).unwrap().y == frame(&fitted, &result_id("one")).unwrap().y, "the result's top does not move");
    let lift = frame(&full, camera::PENDING_NODE_ID).unwrap().y - frame(&fitted, camera::PENDING_NODE_ID).unwrap().y;
    check(near(lift, 752.0 - 200.0), format!("the draft below moves up by what the result gave back: {lift}"));
    let two = canvas(&[done("one"), done("two")], viewport, Some(160.0), None, None);
    check(size_of(&two, &result_id("one")) == Some(Size::new(500.0, 200.0)), "an older result keeps its default size");
    check(
        size_of(&canvas(&[done("one")], None, Some(160.0), None, None), &result_id("one")) == Some(Size::new(500.0, 200.0)),
        "no viewport, no measurement",
    );
}

/// "mighty result reveal: a new result sits right above the composer" (ANewResultSitsRightAboveTheComposer).
#[test]
fn a_new_result_sits_right_above_the_composer() {
    let viewport = Size::new(900.0, 700.0);
    let card = GraphRect::new(24.0, 1_800.0, 500.0, 300.0);
    for zoom in [0.5, 1.0, 1.5] {
        let offset = camera::reveal_offset(card, viewport, zoom);
        check(
            near(offset.y + card.max_y() * zoom, viewport.h - 16.0),
            format!("the card's bottom lands 16pt above the composer at {zoom}"),
        );
        check(offset.x == camera::camera_offset(card, viewport, zoom, true).x, "it is centred as every re-aim centres it");
    }
    let tall = GraphRect::new(24.0, 1_800.0, 500.0, 900.0);
    let top = camera::reveal_offset(tall, viewport, 1.0);
    check(
        top == camera::camera_offset(tall, viewport, 1.0, true) && near(top.y + tall.y, 16.0),
        "a card taller than the viewport shows its top",
    );
    let snug = GraphRect::new(24.0, 400.0, 500.0, 668.0);
    check(
        near(camera::reveal_offset(snug, viewport, 1.0).y + snug.y, 16.0),
        "exactly the viewport less both margins: top and bottom agree",
    );
    let small = GraphRect::new(24.0, 600.0, 500.0, 200.0);
    check(
        camera::camera_offset_aligned(small, viewport, 1.0, true, true) == camera::reveal_offset(small, viewport, 1.0),
        "align-bottom wins",
    );
    check(
        camera::camera_offset_aligned(small, viewport, 1.0, true, false) == camera::camera_offset(small, viewport, 1.0, true),
        "without it align-top holds",
    );
}

fn progress(pairs: &[(&str, bool)]) -> Vec<RunProgress> {
    pairs.iter().map(|(id, finished)| RunProgress::new(id, *finished)).collect()
}

fn change(reveal: &mut ResultReveal, previous: &[(&str, bool)], current: &[(&str, bool)], measured: bool) -> Option<String> {
    reveal.runs_changed(&progress(previous), &progress(current), result_id, |_| measured)
}

/// "mighty result reveal: a request that finishes while watched is revealed once" (ARequestThatFinishesWhileWatchedIsRevealedOnce).
#[test]
fn a_request_that_finishes_while_watched_is_revealed_once() {
    let mut reveal = ResultReveal::new();
    reveal.pane_running(false, true);
    check(
        change(&mut reveal, &[], &[("one", true)], true) == Some(result_id("one"))
            && reveal.holding_id() == Some(result_id("one").as_str()),
        "the pane's own finished request is revealed",
    );
    reveal.pane_running(true, false);
    check(change(&mut reveal, &[("one", true)], &[("one", true)], true).is_none(), "redraws with the same runs reveal nothing again");
    reveal.pane_running(false, true);
    check(reveal.holding_id().is_none(), "a new request ends the hold");
    check(
        change(&mut reveal, &[("one", true)], &[("one", true), ("two", true)], true) == Some(result_id("two")),
        "the next result is revealed",
    );
    let mut mac = ResultReveal::new();
    check(
        change(&mut mac, &[("one", false)], &[("one", true)], true) == Some(result_id("one")),
        "a run seen unfinished then finished is revealed",
    );
    check(change(&mut mac, &[("one", true)], &[("one", true), ("two", false)], true).is_none(), "a request starting is not a result");
}

/// "mighty result reveal: runs a pane opens or hydrates with are never revealed" (RunsAPaneOpensOrHydratesWithAreNeverRevealed).
#[test]
fn runs_a_pane_opens_or_hydrates_with_are_never_revealed() {
    let mut reveal = ResultReveal::new();
    check(change(&mut reveal, &[], &[("one", true), ("two", true)], true).is_none(), "launch or restore reveals nothing");
    check(
        change(&mut reveal, &[("one", true)], &[("one", true), ("two", true)], true).is_none(),
        "a run that appeared finished with nothing in flight",
    );
    check(change(&mut reveal, &[], &[], true).is_none() && reveal.holding_id().is_none(), "nothing held");
    reveal.pane_running(false, true);
    check(change(&mut reveal, &[("two", true)], &[("one", true), ("two", true)], true).is_none(), "history loaded above is never revealed");
    let mut back = ResultReveal::new();
    check(change(&mut back, &[("one", true), ("two", true)], &[("one", true), ("two", false)], true).is_none(), "a resumed newest run");
    check(
        change(&mut back, &[("one", true), ("two", true)], &[("one", true)], true).is_none() && back.holding_id().is_none(),
        "a removed newest run",
    );
    check(
        ResultReveal::finished_run_id(&progress(&[("a", false)]), &progress(&[("a", true)]), false) == Some("a".into())
            && ResultReveal::finished_run_id(&progress(&[]), &progress(&[("a", true)]), false).is_none()
            && ResultReveal::finished_run_id(&progress(&[]), &progress(&[("a", true)]), true) == Some("a".into()),
        "the run that just finished",
    );
}

/// "mighty result reveal: an unmeasured card is held until its height arrives" (AnUnmeasuredCardIsHeldUntilItsHeightArrives).
#[test]
fn an_unmeasured_card_is_held_until_its_height_arrives() {
    let mut reveal = ResultReveal::new();
    reveal.pane_running(false, true);
    check(change(&mut reveal, &[], &[("one", true)], false).is_none(), "no camera move before the card is measured");
    check(reveal.holding_id() == Some(result_id("one").as_str()) && reveal.awaiting_measure(), "held, awaiting its height");
    check(reveal.viewport_changed().is_none(), "a resize does not place a card still awaiting its height");
    check(reveal.content_measured(&result_id("one")) == Some(result_id("one")) && !reveal.awaiting_measure(), "its height places it");
    check(reveal.measure_timed_out(&result_id("one")).is_none(), "a late timeout does nothing");
    check(reveal.viewport_changed() == Some(result_id("one")), "a resize places it again");
    check(reveal.content_measured(&result_id("zero")).is_none(), "another card's height does nothing");
    let mut never = ResultReveal::new();
    never.pane_running(false, true);
    let _ = change(&mut never, &[], &[("one", true)], false);
    check(
        never.measure_timed_out(&result_id("zero")).is_none()
            && never.measure_timed_out(&result_id("one")) == Some(result_id("one"))
            && never.measure_timed_out(&result_id("one")).is_none(),
        "the timeout places it once",
    );
    check(never.content_measured(&result_id("one")) == Some(result_id("one")), "its measurement places it again");
}

/// "mighty result reveal: the user's own scroll, zoom or draft ends the hold" (TheUsersOwnScrollZoomOrDraftEndsTheHold).
#[test]
fn the_users_own_scroll_zoom_or_draft_ends_the_hold() {
    let mut reveal = ResultReveal::new();
    reveal.pane_running(false, true);
    let _ = change(&mut reveal, &[], &[("one", true)], false);
    reveal.cancel();
    check(reveal.holding_id().is_none() && !reveal.awaiting_measure(), "a scroll, drag or zoom ends the hold");
    check(
        reveal.content_measured(&result_id("one")).is_none()
            && reveal.measure_timed_out(&result_id("one")).is_none()
            && reveal.viewport_changed().is_none(),
        "nothing places it after",
    );
    reveal.pane_running(false, true);
    check(
        change(&mut reveal, &[("one", true)], &[("one", true), ("two", true)], true) == Some(result_id("two")),
        "a later result is revealed again",
    );
    reveal.draft_changed(false, true);
    check(reveal.holding_id() == Some(result_id("two").as_str()), "clearing the draft keeps the hold");
    reveal.draft_changed(true, true);
    check(reveal.holding_id() == Some(result_id("two").as_str()), "an empty draft keeps the hold");
    reveal.draft_changed(true, false);
    check(reveal.holding_id().is_none(), "typing a draft ends the hold");
}
