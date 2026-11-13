//! The diagram's own interaction rules (C# MightyGraphInteractionVerification, MightyCode.Core.Tests/MightyGraphInteractionVerification.cs):
//! a block resizes from every side the user grabs, the layout that takes every block off screen is noticed, and the reference bubble's size.

mod view_support;

use mighty_core::Wire;
use mighty_core::model::AppSnapshot;
use mighty_graph::camera::{GraphRect, Loss, PENDING_NODE_ID, Point, Size, StrandedWatch};
use mighty_graph::layout::{self, resize};
use resize::Edges;
use view_support::*;

const FRAME: GraphRect = GraphRect::new(100.0, 100.0, 300.0, 200.0);

/// "mighty interaction: a border's band reaches further outside than inside" (TheBandReachesFurtherOutsideThanInside).
#[test]
fn the_band_reaches_further_outside_than_inside() {
    let at = |x: f64, y: f64| resize::at(Point::new(x, y), FRAME, 5.0, 2.0);
    check(at(97.0, 200.0) == Edges::LEFT && at(403.0, 200.0) == Edges::RIGHT && at(250.0, 101.0) == Edges::TOP && at(250.0, 298.0) == Edges::BOTTOM, "each border has its band");
    check(at(101.0, 101.0) == (Edges::LEFT | Edges::TOP), "near a corner that is both sides");
    check(at(104.0, 200.0) == Edges::NONE && at(250.0, 200.0) == Edges::NONE && at(90.0, 200.0) == Edges::NONE, "past the band nothing resizes");
    check(resize::band(1.0) == 6.0 && resize::band(0.5) == 4.0 && resize::band(1.5) == 9.0 && resize::BAND_INSIDE == 2.0, "the band's reach at each zoom");
    check(resize::handle_side(1.0) == 22.0 && resize::handle_side(0.5) == 16.0 && resize::handle_side(1.5) == 33.0, "the corner handle's side at each zoom");
}

/// "mighty interaction: every side grows away from the box and honours zoom and bounds" (EverySideGrowsAwayFromTheBoxAndHonoursZoomAndBounds).
#[test]
fn every_side_grows_away_from_the_box_and_honours_zoom_and_bounds() {
    let (minimum, maximum, initial) = (Size::new(300.0, 140.0), Size::new(1400.0, 1200.0), Size::new(360.0, 240.0));
    check(Edges::BOTTOM_RIGHT.size(initial, Point::new(90.0, 60.0), 1.5, minimum, maximum) == Size::new(420.0, 280.0), "the corner handle at 150%");
    check(Edges::LEFT.size(initial, Point::new(-40.0, 99.0), 1.0, minimum, maximum) == Size::new(400.0, 240.0), "the left side grows leftwards and leaves the height");
    check(Edges::TOP.size(initial, Point::new(99.0, -30.0), 1.0, minimum, maximum) == Size::new(360.0, 270.0), "the top side grows upwards and leaves the width");
    check((Edges::LEFT | Edges::TOP).size(initial, Point::new(4000.0, 4000.0), 1.0, minimum, maximum) == minimum, "never below the least");
    check(Edges::RIGHT.size(initial, Point::new(9000.0, 0.0), 1.0, minimum, maximum).w == 1400.0, "never above the most");
    check(Edges::BOTTOM.size(initial, Point::new(0.0, 50.0), f64::NAN, minimum, maximum) == Size::new(360.0, 290.0), "a broken zoom is 100%");
    check(
        Edges::BOTTOM_RIGHT.horizontal() && Edges::BOTTOM_RIGHT.vertical() && Edges::LEFT.horizontal() && !Edges::LEFT.vertical() && !Edges::TOP.horizontal(),
        "which way each side moves",
    );
}

/// "mighty interaction: the corner opposite the dragged sides stays put" (TheCornerOppositeTheDraggedSidesStaysPut).
#[test]
fn the_corner_opposite_the_dragged_sides_stays_put() {
    check(
        Edges::BOTTOM_RIGHT.pinned_unit() == Point::new(0.0, 0.0)
            && Edges::LEFT.pinned_unit() == Point::new(1.0, 0.0)
            && (Edges::LEFT | Edges::TOP).pinned_unit() == Point::new(1.0, 1.0),
        "the pinned corner",
    );
    let (zoom, camera) = (0.8, Point::new(40.0, -60.0));
    let unit = Edges::LEFT.pinned_unit();
    let anchor = Point::new(camera.x + (FRAME.x + unit.x * FRAME.w) * zoom, camera.y + (FRAME.y + unit.y * FRAME.h) * zoom);
    let recentred = GraphRect::new(50.0, 130.0, 400.0, 200.0);
    let pinned = resize::pinned_camera(anchor, unit, recentred, zoom);
    check(
        (pinned.x + recentred.max_x() * zoom - anchor.x).abs() < 0.001 && (pinned.y + recentred.y * zoom - anchor.y).abs() < 0.001,
        "the right edge and the top keep their place on screen",
    );
    let same = resize::pinned_camera(anchor, unit, FRAME, zoom);
    check((same.x - camera.x).abs() < 0.001 && (same.y - camera.y).abs() < 0.001, "a block that did not move leaves the camera");
}

/// "mighty interaction: a press resizes by the handle, then by a band" (APressResizesByTheHandleThenByABand).
#[test]
fn a_press_resizes_by_the_handle_then_by_a_band() {
    let block = vec![("block".to_string(), FRAME)];
    let frames = vec![("block".to_string(), FRAME), ("under".to_string(), GraphRect::new(380.0, 280.0, 300.0, 200.0))];
    let target = |x: f64, y: f64, frames: &[(String, GraphRect)], zoom: f64| resize::target(Point::new(x, y), frames, zoom);
    check(target(395.0, 295.0, &block, 1.0).is_some_and(|t| t.0 == "block" && t.2 == Edges::BOTTOM_RIGHT), "the corner handle is the right and the bottom");
    check(target(250.0, 304.0, &block, 1.0).is_some_and(|t| t.0 == "block" && t.2 == Edges::BOTTOM), "6pt under the border is the bottom band");
    check(target(250.0, 308.0, &block, 1.0).is_none() && target(250.0, 200.0, &block, 1.0).is_none(), "past the band, and inside the block, nothing resizes");
    check(target(381.0, 281.0, &frames, 1.0).is_some_and(|t| t.0 == "under" && t.2 == (Edges::LEFT | Edges::TOP)), "the block drawn last wins");
    check(
        target(372.0, 272.0, &block, 1.5).is_some_and(|t| t.2 == Edges::BOTTOM_RIGHT) && target(372.0, 272.0, &block, 1.0).is_none(),
        "the handle grows with the zoom",
    );
    check(
        ["request", layout::WORK_KIND, "agent", "result", "draft", "plan", "planRecord"].iter().all(|k| layout::is_resizable_kind(k))
            && !["resultFiles", "history", layout::EXECUTION_KIND, layout::IMAGES_KIND].iter().any(|k| layout::is_resizable_kind(k)),
        "which blocks resize",
    );
}

fn nodes(list: &[(&str, GraphRect)]) -> Vec<(String, GraphRect)> {
    list.iter().map(|(id, f)| (id.to_string(), *f)).collect()
}

/// "mighty interaction: the stranded watch judges only layout moves under a still camera" (TheStrandedWatchJudgesOnlyLayoutMovesUnderAStillCamera).
#[test]
fn the_stranded_watch_judges_only_layout_moves_under_a_still_camera() {
    let viewport = Size::new(800.0, 600.0);
    let on_screen = GraphRect::new(120.0, 240.0, 500.0, 280.0);
    let gone = GraphRect::new(120.0, 9_000.0, 500.0, 280.0);
    let draft = (PENDING_NODE_ID, GraphRect::new(120.0, 600.0, 500.0, 140.0));
    let observe = |watch: &mut StrandedWatch, list: &[(&str, GraphRect)], camera_y: f64, zoom: f64, newest: Option<&str>, settled: bool| {
        watch.observe(&nodes(list), Point::new(-100.0, camera_y), zoom, viewport, newest, settled).is_some()
    };
    let plain = |watch: &mut StrandedWatch, list: &[(&str, GraphRect)]| observe(watch, list, -200.0, 1.0, Some("one"), true);
    let unsettled = |watch: &mut StrandedWatch, list: &[(&str, GraphRect)]| observe(watch, list, -200.0, 1.0, Some("one"), false);
    let mut watch = StrandedWatch::new();
    check(!plain(&mut watch, &[("a", on_screen)]) && plain(&mut watch, &[("a", gone)]) && !plain(&mut watch, &[("a", gone)]), "judged once");
    let mut watch = StrandedWatch::new();
    plain(&mut watch, &[("a", on_screen)]);
    check(!unsettled(&mut watch, &[("a", gone)]) && watch.is_withholding(), "an unsettled pass is withheld");
    check(plain(&mut watch, &[("a", gone)]) && !watch.is_withholding(), "and judged once settled");
    let mut watch = StrandedWatch::new();
    plain(&mut watch, &[("a", on_screen)]);
    unsettled(&mut watch, &[("a", gone)]);
    check(!unsettled(&mut watch, &[("a", on_screen)]) && !watch.is_withholding(), "back at the baseline there is nothing to judge");
    let mut watch = StrandedWatch::new();
    plain(&mut watch, &[("a", on_screen)]);
    check(
        !observe(&mut watch, &[("a", gone)], -260.0, 1.0, Some("one"), true) && !observe(&mut watch, &[("a", gone)], -260.0, 1.0, Some("one"), true),
        "a moved camera is its mover's",
    );
    let mut watch = StrandedWatch::new();
    plain(&mut watch, &[("a", on_screen)]);
    check(!observe(&mut watch, &[("a", gone)], -200.0, 0.9, Some("one"), true), "so is a zoom");
    let mut watch = StrandedWatch::new();
    plain(&mut watch, &[("a", on_screen)]);
    check(!observe(&mut watch, &[("a", gone), ("b", gone.offset_by(0.0, 400.0))], -200.0, 1.0, Some("two"), true), "a new request has its own rule");
    let mut watch = StrandedWatch::new();
    plain(&mut watch, &[("a", gone), draft]);
    check(!plain(&mut watch, &[("a", gone)]) && !plain(&mut watch, &[("a", gone), draft]), "the draft block is never lost");
    let mut watch = StrandedWatch::new();
    plain(&mut watch, &[("a", gone), draft]);
    check(!plain(&mut watch, &[("a", gone.offset_by(0.0, 40.0))]), "nor what the user was looking at");
}

/// "mighty interaction: a loss names the card the user saw most of" (ALossNamesTheCardTheUserSawMostOf).
#[test]
fn a_loss_names_the_card_the_user_saw_most_of() {
    let (big, edge, away) =
        (GraphRect::new(120.0, 240.0, 500.0, 480.0), GraphRect::new(640.0, 700.0, 360.0, 240.0), GraphRect::new(120.0, 9_000.0, 500.0, 280.0));
    let mut watch = StrandedWatch::new();
    let viewport = Size::new(800.0, 600.0);
    watch.observe(&nodes(&[("edge", edge), ("big", big)]), Point::new(-100.0, -200.0), 1.0, viewport, Some("one"), true);
    let loss = watch.observe(&nodes(&[("edge", away), ("big", away.offset_by(0.0, 600.0))]), Point::new(-100.0, -200.0), 1.0, viewport, Some("one"), true);
    check(
        loss == Some(Loss { looked_at_node_id: "big".into(), newest_run_id: Some("one".into()) }),
        format!("the loss names the card seen most: {loss:?}"),
    );
}

/// "mighty interaction: the reference bubble's size is kept for every pane" (TheReferenceBubblesSizeIsKeptForEveryPane): the reading and
/// the normalize. The save and load through the state file are mighty-core's store's (`StateStore`), not this crate's.
#[test]
fn the_reference_bubbles_size_is_kept_for_every_pane() {
    let legacy = AppSnapshot::read(
        &serde_json::from_str(r#"{"version":1,"workspaces":[],"sessions":[],"layout":"grid","theme":"dark","sidebarWidth":300}"#).unwrap(),
    )
    .expect("read");
    check(legacy.reference_bubble_width.is_none() && legacy.reference_bubble_height.is_none(), "old state opens the bubble at its default size");
    let bubble = mighty_core::normalize::bubble_size;
    check(bubble(Some(640.0), Some(480.0)) == (Some(640.0), Some(480.0)) && bubble(Some(120.0), Some(90.0)) == (Some(300.0), Some(200.0)), "a saved size is kept, never under its least");
    check(
        bubble(Some(f64::NAN), Some(0.0)) == (None, None)
            && bubble(Some(-5.0), Some(f64::INFINITY)) == (None, None)
            && bubble(Some(500.0), None) == (Some(500.0), None),
        "a broken or absent side is none",
    );
    let normalized = mighty_core::normalize::normalize(
        &AppSnapshot { reference_bubble_width: Some(10.0), reference_bubble_height: Some(f64::NAN), ..AppSnapshot::default() },
        false,
    )
    .expect("normalize");
    check(normalized.reference_bubble_width == Some(300.0) && normalized.reference_bubble_height.is_none(), "normalize keeps the size within its least");
}
