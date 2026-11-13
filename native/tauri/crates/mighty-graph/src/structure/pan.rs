//! Dragging the structure view's empty space moves its page (C# `MightyStructurePan`, MightyCode.Core/MightyStructurePan.cs; macOS
//! MightyStructurePan.swift): the grabbed point stays under the pointer, and the page stops at its edges as a scroll does.

use super::wheel::Axis;

/// A point or an origin, (x, y).
pub type Point = (f64, f64);

/// `ClickSlop`: points a press moves before it is a drag.
pub const CLICK_SLOP: f64 = 3.0;
/// `ReleaseGrace`: after a release the page is still the user's for this long, in seconds.
pub const RELEASE_GRACE: f64 = 0.25;

/// `MightyStructurePan.Target`: what a press lands on.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum Target {
    /// The page: its background, the gaps, a card's header and padding, or a text that cannot be selected.
    Page,
    /// Selectable text or a native control: it takes its own drag.
    TextOrControl,
    /// A scroll view inside the page.
    InnerScroll,
    /// Not this page.
    Outside,
}

/// `MayPan`: an unmodified press on the page itself, on a page that can move at all.
pub fn may_pan(target: Target, modified: bool, scrollable: bool) -> bool {
    target == Target::Page && !modified && scrollable
}

/// `IsDrag`: the press moved far enough from where it went down.
pub fn is_drag(start: Point, point: Point) -> bool {
    (point.0 - start.0).abs().max((point.1 - start.1).abs()) >= CLICK_SLOP
}

/// `Scrollable`: the page can move along either axis between these origins.
pub fn scrollable(minimum: Point, maximum: Point) -> bool {
    maximum.0 - minimum.0 > 0.5 || maximum.1 - minimum.1 > 0.5
}

/// `Origin`: the page's origin for the pointer at `cursor` after it went down at `press` with the page at `start`, clamped to the edges;
/// an axis that cannot move keeps the origin it had.
pub fn origin(start: Point, press: Point, cursor: Point, minimum: Point, maximum: Point) -> Point {
    (
        clamp(start.0 - (cursor.0 - press.0), minimum.0, maximum.0, start.0),
        clamp(start.1 - (cursor.1 - press.1), minimum.1, maximum.1, start.1),
    )
}

/// `Confined`: the origins a scroll view of the board may take along `axis` only, the other held where it is (`current`).
pub fn confined(minimum: Point, maximum: Point, current: Point, axis: Axis) -> (Point, Point) {
    match axis {
        Axis::Horizontal => ((minimum.0, current.1), (maximum.0, current.1)),
        Axis::Vertical => ((current.0, minimum.1), (current.0, maximum.1)),
    }
}

/// `Clamp`: `value` within `low..=high`; a range that is not valid keeps `current`.
pub(crate) fn clamp(value: f64, low: f64, high: f64, current: f64) -> f64 {
    if value.is_finite() && low.is_finite() && high.is_finite() && low <= high { value.max(low).min(high) } else { current }
}
