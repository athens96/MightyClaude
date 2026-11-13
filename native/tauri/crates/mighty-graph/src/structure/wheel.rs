//! Wheel and trackpad scrolling of the structure view (C# `MightyStructureWheel`, MightyCode.Core/MightyStructureWheel.cs; macOS
//! MightyStructureWheel.swift): a trackpad gesture is decided once, at its first movement, by the innermost scroll view under the pointer
//! that can still move that way; a mouse wheel's steps are decided one by one. Either is the user's own move.

use super::pan::{self, Point};

/// `SettleGrace`: after a wheel step, or a gesture's last event, the page is still the user's this long, in seconds.
pub const SETTLE_GRACE: f64 = 0.3;
/// `StaleAfter`: a gesture that sent nothing for this long, in seconds, is over.
pub const STALE_AFTER: f64 = 1.0;

/// `MightyStructureWheel.Stage`: where a wheel event is in its gesture.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum Stage {
    Step,
    Began,
    Changed,
    Ended,
    Cancelled,
    Momentum,
    MomentumEnded,
}

/// `StageOf`: the stage from AppKit's phase raw values (began 1, stationary 2, changed 4, ended 8, cancelled 16, may begin 32).
pub fn stage_of(phase: u32, momentum_phase: u32) -> Stage {
    if momentum_phase != 0 {
        return if momentum_phase & (8 | 16) != 0 { Stage::MomentumEnded } else { Stage::Momentum };
    }
    if phase == 0 {
        Stage::Step
    } else if phase & 16 != 0 {
        Stage::Cancelled
    } else if phase & 8 != 0 {
        Stage::Ended
    } else if phase & (1 | 32) != 0 {
        Stage::Began
    } else {
        Stage::Changed
    }
}

/// `MightyStructureWheel.Axis`.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum Axis {
    Horizontal,
    Vertical,
}

/// `AxisOf`: the axis a movement is mostly along; none for no movement.
pub fn axis_of(delta: Point) -> Option<Axis> {
    if delta.0 == 0.0 && delta.1 == 0.0 {
        None
    } else if delta.0.abs() > delta.1.abs() {
        Some(Axis::Horizontal)
    } else {
        Some(Axis::Vertical)
    }
}

/// `Origin`: a scroll view's origin after a wheel movement of `delta` (a positive y shows what is above); only `axis` moves when there
/// is one, the origin stops at the edges, and an axis with no valid range keeps its origin.
pub fn origin(current: Point, delta: Point, axis: Option<Axis>, minimum: Point, maximum: Point) -> Point {
    let dx = if axis == Some(Axis::Vertical) { 0.0 } else { delta.0 };
    let dy = if axis == Some(Axis::Horizontal) { 0.0 } else { delta.1 };
    (pan::clamp(current.0 - dx, minimum.0, maximum.0, current.0), pan::clamp(current.1 - dy, minimum.1, maximum.1, current.1))
}

/// `CanMove`: the scroll view can still move the way `delta` asks.
pub fn can_move(current: Point, delta: Point, axis: Option<Axis>, minimum: Point, maximum: Point) -> bool {
    let next = origin(current, delta, axis, minimum, maximum);
    (next.0 - current.0).abs() > 0.5 || (next.1 - current.1).abs() > 0.5
}

/// `Taker`: which scroll view under the pointer takes a movement, innermost first and the page last.
pub fn taker(can_move: &[bool]) -> usize {
    can_move.iter().position(|c| *c).unwrap_or(can_move.len().saturating_sub(1))
}

/// `UserMoving`: the page is moving under the user's hand — a reported scroll, a drag or the moment after it, or a wheel.
pub fn user_moving(scroll_phase: bool, panning: bool, pan_released_at: f64, wheel: &Tracker, now: f64) -> bool {
    scroll_phase || panning || now - pan_released_at < pan::RELEASE_GRACE || wheel.user_scrolling(now)
}

/// `Tracker.Action`: what the page does with a wheel event.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum Action {
    /// Not this page's.
    Pass,
    /// This page's, with nothing to move.
    Hold,
    /// This page's first movement: choose the scroll view that takes it and move it.
    Choose,
    /// Move the scroll view the gesture chose.
    Move,
}

/// `MightyStructureWheel.Tracker`: a page's wheel gestures, one at a time.
#[derive(Debug, Clone)]
pub struct Tracker {
    owned: bool,
    chosen: bool,
    live: bool,
    last_event: f64,
    axis: Option<Axis>,
}

impl Default for Tracker {
    fn default() -> Self {
        Tracker { owned: false, chosen: false, live: false, last_event: f64::NEG_INFINITY, axis: None }
    }
}

impl Tracker {
    pub fn new() -> Tracker {
        Tracker::default()
    }

    /// `Axis`: the axis the current gesture keeps; none for a wheel step, or before it moved.
    pub fn axis(&self) -> Option<Axis> {
        self.axis
    }

    /// `Feed` (MightyStructureWheel.cs:111): a wheel event at `now` seconds; `over_page`: it landed on this page.
    pub fn feed(&mut self, stage: Stage, over_page: bool, delta: Point, now: f64) -> Action {
        let moves = delta.0 != 0.0 || delta.1 != 0.0;
        match stage {
            Stage::Step => {
                self.owned = false;
                self.chosen = false;
                self.live = false;
                self.axis = None;
                if !over_page {
                    return Action::Pass;
                }
                self.last_event = now;
                if moves { Action::Choose } else { Action::Hold }
            }
            Stage::Began => {
                self.owned = over_page;
                self.chosen = false;
                self.live = over_page;
                self.axis = None;
                if !self.owned {
                    return Action::Pass;
                }
                self.last_event = now;
                if !moves {
                    return Action::Hold;
                }
                self.chosen = true;
                self.axis = axis_of(delta);
                Action::Choose
            }
            _ => {
                // Momentum starts right after the fingers lift: a gesture that ended without it is over.
                if self.owned && !self.live && now - self.last_event >= SETTLE_GRACE {
                    self.owned = false;
                    self.chosen = false;
                }
                if !self.owned {
                    return Action::Pass;
                }
                self.last_event = now;
                self.live = matches!(stage, Stage::Changed | Stage::Momentum);
                let action = if !moves {
                    Action::Hold
                } else if self.chosen {
                    Action::Move
                } else {
                    Action::Choose
                };
                if action == Action::Choose {
                    self.chosen = true;
                    self.axis = axis_of(delta);
                }
                if matches!(stage, Stage::Cancelled | Stage::MomentumEnded) {
                    self.owned = false;
                    self.chosen = false;
                }
                action
            }
        }
    }

    /// `UserScrolling`: a gesture that is still going, or the moment after its last event.
    pub fn user_scrolling(&self, now: f64) -> bool {
        let since = now - self.last_event;
        (self.live && since < STALE_AFTER) || since < SETTLE_GRACE
    }
}
