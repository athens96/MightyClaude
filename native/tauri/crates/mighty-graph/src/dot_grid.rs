//! The dots under the Mighty diagram (C# `MightyGraphDotGrid`, MightyCode.Core/MightyGraphDotGrid.cs; macOS M/MightyGraphView.swift:995-1015):
//! one `line`-coloured dot every 18pt × zoom on the `page` surface, laid from the camera's offset, none once the step is under 6pt.

/// `Spacing`: the step between two dots at 100%.
pub const SPACING: f64 = 18.0;
/// `MinimumStep`: below this step no dots are drawn.
pub const MINIMUM_STEP: f64 = 6.0;
/// `MinimumRadius`.
pub const MINIMUM_RADIUS: f64 = 0.6;

/// `Step`: the step between two dots at a zoom.
pub fn step(zoom: f64) -> f64 {
    SPACING * zoom
}

/// `Shown`: whether the grid is drawn at a zoom.
pub fn shown(zoom: f64) -> bool {
    step(zoom) >= MINIMUM_STEP
}

/// `Radius`: the zoom itself, never under the minimum.
pub fn radius(zoom: f64) -> f64 {
    MINIMUM_RADIUS.max(zoom)
}

/// `First`: the first dot centre at or after 0 along one axis, a value in [0, step).
pub fn first(offset: f64, step: f64) -> f64 {
    if step.is_nan() || step <= 0.0 || !offset.is_finite() {
        return 0.0;
    }
    let first = offset % step;
    if first < 0.0 { first + step } else { first }
}
