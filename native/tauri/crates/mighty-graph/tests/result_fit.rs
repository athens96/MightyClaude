//! The Mighty result box fits the agent pane (C# ResultFitVerification, MightyCode.Core.Tests/ResultFitVerification.cs): the newest result
//! card shrinks with the pane and grows back to the size the user dragged it to; a drag stops at the pane edge.

mod view_support;

use std::collections::{HashMap, HashSet};
use std::sync::Arc;

use mighty_core::Wire;
use mighty_core::graph::{GraphBlockSize, MightyGraphRun, node_id};
use mighty_core::json::JsonOut;
use mighty_core::model::{AppSnapshot, RunSession, Workspace};
use mighty_graph::camera::{self, Anchor, GraphRect, Size};
use mighty_graph::layout::{self, Layout, Options, ResizePhase};
use mighty_graph::view_model;
use view_support::*;

fn run_of(id: &str) -> MightyGraphRun {
    with_final(run(id, &format!("요청 {id}"), "completed"), &format!("결과 {id}"))
}

fn result_id(run: &str) -> String {
    node_id(run, "result")
}

/// `MightyGraphViewModel.CanvasLayout([Run("one")], "", false, {}, files ? "one" : null, viewport, zoom, saved)`.
fn canvas(viewport: Size, zoom: Option<f64>, saved: Option<GraphBlockSize>, files: bool) -> Layout {
    let options = Options {
        result_files_run_id: files.then(|| "one".to_string()),
        viewport: Some(viewport),
        zoom,
        shared_result_size: saved,
        ..Options::default()
    };
    layout::make(&[run_of("one")], "", false, &HashSet::new(), &options, &ko())
}

fn latest(layout: &Layout) -> Size {
    let frame = layout.node(&result_id("one")).unwrap().frame;
    Size::new(frame.w, frame.h)
}

fn near(a: f64, b: f64) -> bool {
    (a - b).abs() < 0.001
}

const NARROW: Size = Size::new(600.0, 400.0);
const BIG_SAVED: GraphBlockSize = GraphBlockSize { width: 900.0, height: 700.0 };

fn size(w: f64, h: f64) -> GraphBlockSize {
    GraphBlockSize { width: w, height: h }
}

/// "mighty result box: a saved size larger than the pane is kept within it" (ASavedSizeLargerThanThePaneIsKeptWithinIt).
#[test]
fn a_saved_size_larger_than_the_pane_is_kept_within_it() {
    check(
        latest(&canvas(NARROW, Some(1.0), Some(BIG_SAVED), false)) == Size::new(552.0, 352.0),
        "a saved size larger than the pane is the pane less its margins",
    );
    check(
        latest(&canvas(Size::new(1_200.0, 400.0), Some(1.0), Some(BIG_SAVED), false)) == Size::new(900.0, 352.0),
        "only the side the pane bounds shrinks",
    );
    check(latest(&canvas(NARROW, None, Some(BIG_SAVED), false)) == Size::new(900.0, 700.0), "without a zoom the saved size stays whole");
}

/// "mighty result box shrinks with the agent pane and grows back to its saved size" (TheCardShrinksWithThePaneAndGrowsBackToTheSavedSize).
#[test]
fn the_card_shrinks_with_the_pane_and_grows_back_to_the_saved_size() {
    let saved = size(900.0, 600.0);
    let wide = Size::new(1_200.0, 800.0);
    check(latest(&canvas(wide, Some(1.0), Some(saved), false)) == Size::new(900.0, 600.0), "a roomy pane shows the saved size");
    check(
        latest(&canvas(Size::new(500.0, 300.0), Some(1.0), Some(saved), false)) == Size::new(452.0, 252.0),
        "a small pane shrinks the card",
    );
    check(
        latest(&canvas(Size::new(800.0, 500.0), Some(1.0), Some(saved), false)) == Size::new(752.0, 452.0),
        "a medium pane shrinks it less",
    );
    check(latest(&canvas(wide, Some(1.0), Some(saved), false)) == Size::new(900.0, 600.0), "the card grows back to its saved size");
    check(
        latest(&canvas(Size::new(420.0, 230.0), Some(1.0), None, false)) == Size::new(372.0, 182.0),
        "the window fit shrinks with a narrow pane",
    );
    check(latest(&canvas(wide, Some(1.0), None, false)) == Size::new(1_152.0, 752.0), "the window fit fills a wide pane");
}

/// "mighty result box: the pane limit is in diagram coordinates at the zoom" (TheLimitIsInDiagramCoordinatesAtTheZoom).
#[test]
fn the_limit_is_in_diagram_coordinates_at_the_zoom() {
    let pane = Size::new(900.0, 700.0);
    let saved = size(1_100.0, 900.0);
    check(latest(&canvas(pane, Some(0.5), Some(saved), false)) == Size::new(1_100.0, 900.0), "zoomed out, the saved size fits");
    let zoomed_in = latest(&canvas(pane, Some(1.5), Some(saved), false));
    check(near(zoomed_in.w, 568.0) && near(zoomed_in.h, 652.0 / 1.5), "zoomed in, (900 − 48) / 1.5 × (700 − 48) / 1.5");
    check(latest(&canvas(pane, Some(0.5), None, false)) == Size::new(852.0, 652.0), "zoomed out, the window fit is the 100% fit");
    check(latest(&canvas(pane, Some(1.5), None, false)) == zoomed_in, "zoomed in, the window fit is drawn inside the pane too");
    for zoom in [0.5, 1.0, 1.5] {
        for s in [Some(saved), None] {
            let card = latest(&canvas(pane, Some(zoom), s, false));
            check(
                card.w * zoom <= pane.w - 48.0 + 0.001 && card.h * zoom <= pane.h - 48.0 + 0.001,
                format!("the card stays inside the pane at {zoom}"),
            );
        }
    }
}

/// "mighty result box: a tiny pane keeps the block minimum" (ATinyPaneKeepsTheBlockMinimum).
#[test]
fn a_tiny_pane_keeps_the_block_minimum() {
    let minimum = Size::new(mighty_core::graph::MINIMUM_WIDTH, mighty_core::graph::MINIMUM_HEIGHT);
    check(layout::result_viewport_limit(Size::new(200.0, 100.0), 1.0, false) == minimum, "a tiny pane keeps the block minimum");
    check(layout::result_viewport_limit(Size::new(400.0, 220.0), 1.5, false) == minimum, "a tiny zoomed pane keeps the block minimum");
    check(latest(&canvas(Size::new(200.0, 100.0), Some(1.0), Some(BIG_SAVED), false)) == minimum, "the card keeps the block minimum");
    check(
        latest(&canvas(Size::new(1_000.0, 600.0), Some(1.0), Some(BIG_SAVED), true)).w == 1_000.0 - 48.0 - 336.0,
        "the files panel takes its width",
    );
    check(
        latest(&canvas(Size::new(600.0, 600.0), Some(1.0), Some(BIG_SAVED), true)).w == mighty_core::graph::MINIMUM_WIDTH,
        "down to the minimum",
    );
    for zoom in [0.0, -1.0, f64::NAN, f64::INFINITY] {
        check(
            layout::result_viewport_limit(Size::new(600.0, 400.0), zoom, false) == Size::new(552.0, 352.0),
            "a broken zoom counts as 100%",
        );
    }
}

fn drag(
    dragged: Size,
    horizontal: bool,
    vertical: bool,
    phase: ResizePhase,
    on: &Layout,
    saved: Option<GraphBlockSize>,
) -> (Option<GraphBlockSize>, Option<GraphBlockSize>) {
    layout::result_drag(dragged, horizontal, vertical, phase, saved.as_ref(), on.result_limit, on.result_window_fit)
}

/// "mighty result box: the layout carries the limits a drag uses" (TheLayoutCarriesTheLimitsADragUses).
#[test]
fn the_layout_carries_the_limits_a_drag_uses() {
    let graph = canvas(NARROW, Some(1.0), Some(BIG_SAVED), false);
    check(
        graph.result_limit == Some(Size::new(552.0, 352.0)) && graph.result_window_fit == Some(Size::new(552.0, 352.0)),
        "the layout carries the limit and the fit",
    );
    let wide = canvas(Size::new(1_200.0, 800.0), Some(1.5), Some(BIG_SAVED), true);
    check(
        wide.result_limit == Some(layout::result_viewport_limit(Size::new(1_200.0, 800.0), 1.5, true)),
        "the limit counts the files panel and zoom",
    );
    check(wide.result_window_fit == Some(layout::result_fit_size(Size::new(1_200.0, 800.0), true)), "the fit counts the files panel");
    check(canvas(NARROW, None, Some(BIG_SAVED), false).result_limit.is_none(), "no zoom, no limit");
}

/// "mighty result box: a live drag follows the cursor within the pane only" (ALiveDragFollowsTheCursorWithinThePaneOnly).
#[test]
fn a_live_drag_follows_the_cursor_within_the_pane_only() {
    let graph = canvas(NARROW, Some(1.0), Some(BIG_SAVED), false);
    let live = ResizePhase::Live;
    check(
        drag(Size::new(480.0, 300.0), true, true, live, &graph, Some(BIG_SAVED)).0 == Some(size(480.0, 300.0)),
        "inside the pane: the dragged size",
    );
    check(
        drag(Size::new(1_000.0, 300.0), true, true, live, &graph, Some(BIG_SAVED)).0 == Some(size(552.0, 300.0)),
        "past it: stops at the pane",
    );
    let pushed = drag(Size::new(1_000.0, 900.0), true, true, live, &graph, Some(BIG_SAVED));
    check(pushed.0 == Some(size(552.0, 352.0)) && pushed.1.is_none(), "a live drag saves nothing");
    check(latest(&canvas(NARROW, Some(1.0), pushed.0, false)) == Size::new(552.0, 352.0), "the live size laid out is the card shown");
    check(
        layout::result_drag(Size::new(1_000.0, 900.0), true, true, live, None, None, None).0 == Some(size(1_000.0, 900.0)),
        "without a limit the cursor is followed",
    );
}

/// "mighty result box: releasing saves only the sides moved inside the pane" (ReleasingSavesOnlyTheSidesMovedInsideThePane).
#[test]
fn releasing_saves_only_the_sides_moved_inside_the_pane() {
    let finished = ResizePhase::Finished;
    let graph = canvas(NARROW, Some(1.0), Some(BIG_SAVED), false);
    let released = drag(Size::new(552.0, 300.0), true, true, finished, &graph, Some(BIG_SAVED));
    check(released.0.is_none() && released.1 == Some(size(900.0, 300.0)), "dx = 0 at the pane edge keeps the saved width");
    check(
        latest(&canvas(Size::new(1_200.0, 800.0), Some(1.0), released.1, false)).w == 900.0,
        "grown back, the card is the saved width again",
    );
    check(
        drag(Size::new(1_000.0, 900.0), true, true, finished, &graph, Some(BIG_SAVED)).1 == Some(BIG_SAVED),
        "pushing past the pane keeps the saved size",
    );
    let small = size(400.0, 250.0);
    check(
        drag(Size::new(800.0, 600.0), true, true, finished, &canvas(NARROW, Some(1.0), Some(small), false), Some(small)).1
            == Some(size(552.0, 352.0)),
        "a smaller saved size pushed past the pane saves the pane's size",
    );
    check(
        drag(Size::new(900.0, 600.0), true, true, finished, &canvas(Size::new(420.0, 230.0), Some(1.0), None, false), None).1
            == Some(size(500.0, 200.0)),
        "nothing saved and a narrow pane: the fit, not the shrunk size",
    );
    check(
        drag(Size::new(500.0, 300.0), true, true, finished, &graph, Some(BIG_SAVED)).1 == Some(size(500.0, 300.0)),
        "released inside the pane",
    );
    check(
        drag(Size::new(500.0, 352.0), true, false, finished, &graph, Some(BIG_SAVED)).1 == Some(size(500.0, 700.0)),
        "a right-side drag keeps the saved height",
    );
    check(
        drag(Size::new(552.0, 300.0), false, true, finished, &graph, Some(BIG_SAVED)).1 == Some(size(900.0, 300.0)),
        "a bottom drag keeps the saved width",
    );
    let zoomed = canvas(Size::new(900.0, 700.0), Some(1.5), Some(BIG_SAVED), false);
    check(
        drag(Size::new(568.0, 300.0), true, true, finished, &zoomed, Some(BIG_SAVED)).1 == Some(size(900.0, 300.0)),
        "zoomed in, the limit is 568 wide",
    );
    check(
        drag(Size::new(520.0, 300.0), true, true, finished, &zoomed, Some(BIG_SAVED)).1 == Some(size(520.0, 300.0)),
        "zoomed in, inside the limit",
    );
    check(
        drag(Size::new(900.0, 180.0), true, true, ResizePhase::Cancelled, &graph, Some(BIG_SAVED)) == (None, None),
        "a cancelled drag saves nothing",
    );
    check(
        layout::result_drag(Size::new(100.0, 5_000.0), true, true, finished, None, None, Some(Size::new(1_152.0, 752.0))).1
            == Some(size(300.0, 1_200.0)),
        "saves are clamped to the block bounds",
    );
}

/// "mighty result box: a saved card the pane bounds follows the pane on resize" (ASavedCardThePaneBoundsFollowsThePaneOnResize).
#[test]
fn a_saved_card_the_pane_bounds_follows_the_pane_on_resize() {
    let bound = canvas(NARROW, Some(1.0), Some(BIG_SAVED), false);
    check(
        bound.fitted_result_id.is_none() && bound.viewport_bound_result_id == Some(result_id("one")),
        "a bounded saved card follows the pane",
    );
    let frames: HashMap<String, GraphRect> = bound.nodes.iter().map(|n| (n.id.clone(), n.frame)).collect();
    let anchor = camera::resize_anchor(
        bound.fitted_result_id.as_deref().or(bound.viewport_bound_result_id.as_deref()),
        Some(&node_id("one", "request")),
        false,
        &frames,
    );
    check(anchor == Anchor::reaim(&result_id("one"), true), "resizing keeps a bounded card's top in view");
    check(
        canvas(Size::new(1_200.0, 800.0), Some(1.0), Some(BIG_SAVED), false).viewport_bound_result_id.is_none(),
        "a card within the pane is not bounded",
    );
    check(
        canvas(Size::new(1_200.0, 400.0), Some(1.0), Some(BIG_SAVED), false).viewport_bound_result_id == Some(result_id("one")),
        "bounded in one direction is enough",
    );
    check(canvas(NARROW, None, Some(BIG_SAVED), false).viewport_bound_result_id.is_none(), "no zoom never bounds");
    let fitted = canvas(Size::new(900.0, 700.0), Some(1.5), None, false);
    check(
        fitted.fitted_result_id == Some(result_id("one")) && fitted.viewport_bound_result_id == Some(result_id("one")),
        "a bounded fitted card is reported both ways",
    );
}

/// "mighty result box: the saved size is kept under the macOS field" (TheSavedSizeIsKeptUnderTheMacField): written, read back and
/// normalized as a load does (the load's disk part is mighty-core's store's).
#[test]
fn the_saved_size_is_kept_under_the_mac_field() {
    check(
        view_model::LOCALE_KEY_RESULT_FIT_TO_WINDOW == "graph.result.fitToWindow"
            && ko().get(view_model::LOCALE_KEY_RESULT_FIT_TO_WINDOW) == "창에 맞추기",
        "the fit button is graph.result.fitToWindow",
    );
    let workspace = Workspace { path: Some(std::env::temp_dir().to_string_lossy().into_owned()), ..Workspace::default() };
    let session = RunSession {
        workspace_id: workspace.id.clone(),
        provider: Some("claude".into()),
        agent_view_mode: Some("mighty".into()),
        graph_result_size: Some(size(5_000.0, 700.0)),
        ..RunSession::default()
    };
    let shell = RunSession {
        workspace_id: workspace.id.clone(),
        kind: Some("shell".into()),
        graph_result_size: Some(size(900.0, 700.0)),
        ..RunSession::default()
    };
    let plain = RunSession { workspace_id: workspace.id.clone(), provider: Some("claude".into()), ..RunSession::default() };
    let snapshot = AppSnapshot {
        version: 1,
        workspaces: Some(vec![workspace]),
        sessions: Some(vec![Arc::new(session.clone()), Arc::new(shell.clone()), Arc::new(plain.clone())]),
        ..AppSnapshot::default()
    };
    let mut out = JsonOut::new();
    snapshot.write(&mut out);
    let encoded: serde_json::Value = serde_json::from_str(&out.finish()).unwrap();
    let saved = &encoded["sessions"][0]["graphResultSize"];
    check(saved["width"].as_f64() == Some(5_000.0) && saved["height"].as_f64() == Some(700.0), "graphResultSize is {width,height}");
    check(encoded["sessions"][2].get("graphResultSize").is_none(), "no saved size writes nothing");
    let loaded = mighty_core::normalize::normalize(&AppSnapshot::read(&encoded).unwrap(), true).unwrap();
    let find = |id: &str| loaded.sessions().iter().find(|s| s.id() == id).unwrap().graph_result_size;
    check(find(session.id()) == Some(size(1_400.0, 700.0)), "the saved size survives clamped");
    check(find(shell.id()).is_none(), "a shell pane keeps no result size");
    check(find(plain.id()).is_none(), "no saved size loads as none");
}
