//! The edge a Mighty diagram block in motion draws over its card (C# `MightyGraphActivity`, MightyCode.Core/MightyGraphActivity.cs; macOS
//! `MightyGraphActivityOutline`): a running block's dashed run-blue border marching around it, still with animations off; a waiting
//! block's still amber line; none otherwise. And the running block's activity mark, four capsules rising and falling.

use std::sync::LazyLock;

use serde_json::Value;

/// `Marching`, `Solid`, `Waiting`, `None`: the outline choices.
pub const MARCHING: &str = "marching";
pub const SOLID: &str = "solid";
pub const WAITING: &str = "waiting";
pub const NONE: &str = "none";

/// `Dash`: 9pt dashes, 7pt gaps.
pub const DASH: [f64; 2] = [9.0, 7.0];
/// `Period`: one dash-and-gap period goes by every 1.6 s (in seconds).
pub const PERIOD_SECONDS: f64 = 1.6;
/// `LineWidth`, `CornerRadius`.
pub const LINE_WIDTH: f64 = 2.0;
pub const CORNER_RADIUS: f64 = 12.0;

/// `Outline`: a block's outline from its status, bucketed as macOS `DesignTone(blockStatus:)` does.
pub fn outline(block_status: &str, animations_enabled: bool) -> &'static str {
    match block_status {
        "waiting" => WAITING,
        "completed" | "error" | "failed" | "stopped" | "cancelled" | "interrupted" => NONE,
        _ if animations_enabled => MARCHING,
        _ => SOLID,
    }
}

/// .NET ticks (100 ns) in one period.
const PERIOD_TICKS: i64 = 16_000_000;

/// `Phase`: how far through the cycle the dashes are, `elapsed_ticks` in .NET ticks; always 0 with animations off.
pub fn phase(elapsed_ticks: i64, animations_enabled: bool) -> f64 {
    if !animations_enabled {
        return 0.0;
    }
    let cycle = elapsed_ticks % PERIOD_TICKS;
    (if cycle < 0 { cycle + PERIOD_TICKS } else { cycle }) as f64 / PERIOD_TICKS as f64
}

/// `Perimeter`: the length of a rounded rectangle's outline with circular corners.
pub fn perimeter(width: f64, height: f64, corner_radius: f64) -> f64 {
    let radius = 0f64.max(corner_radius.min((width / 2.0).min(height / 2.0)));
    2.0 * (width + height) - 8.0 * radius + 2.0 * std::f64::consts::PI * radius
}

/// `Math.Round(value, MidpointRounding.AwayFromZero)`.
fn round_away(value: f64) -> f64 {
    value.round()
}

/// `DashFitting`: the dash stretched or squeezed so a whole number of periods fits the outline.
pub fn dash_fitting(perimeter: f64) -> Vec<f64> {
    let period: f64 = DASH.iter().sum();
    if perimeter <= 0.0 {
        return DASH.to_vec();
    }
    let fitted = perimeter / 1f64.max(round_away(perimeter / period));
    DASH.iter().map(|d| d * fitted / period).collect()
}

/// `DashOffset`: one period per cycle; a negative offset walks the dashes forward.
pub fn dash_offset(phase: f64, dash: &[f64]) -> f64 {
    -phase * dash.iter().sum::<f64>()
}

/// `DashForCard`: the outline drawn on a card of this size: the card inset by 1pt, its radius 1pt smaller.
pub fn dash_for_card(width: f64, height: f64, corner_radius: f64) -> Vec<f64> {
    dash_fitting(perimeter(0f64.max(width - LINE_WIDTH), 0f64.max(height - LINE_WIDTH), corner_radius - 1.0))
}

/// `StrokeToken`: run blue, or wait amber while the block waits (design token names).
pub fn stroke_token(outline: &str) -> &'static str {
    if outline == WAITING { "wait" } else { "run" }
}

/// `HaloToken`: the soft halo outside a running block's border; waiting has none.
pub fn halo_token(outline: &str) -> Option<&'static str> {
    matches!(outline, MARCHING | SOLID).then_some("runSoft")
}

static TOKENS: LazyLock<Value> =
    LazyLock::new(|| serde_json::from_str(include_str!("../../../../contracts/fixtures/design-tokens.json")).unwrap_or(Value::Null));

/// A token's colour in one mode, `#RRGGBB` (C# `DesignTokens.Light`/`Dark`, whose values the shared `design-tokens.json` holds).
pub fn token_hex(token: &str, dark: bool) -> Option<String> {
    TOKENS.get(if dark { "dark" } else { "light" })?.get(token)?.as_str().map(str::to_uppercase)
}

/// `StrokeHex`: the outline's colour, the same in both modes.
pub fn stroke_hex(outline: &str) -> String {
    token_hex(stroke_token(outline), false).unwrap_or_default()
}

/// `HaloHex`: the soft run halo, per mode; waiting has none.
pub fn halo_hex(outline: &str, dark: bool) -> Option<String> {
    halo_token(outline).and_then(|t| token_hex(t, dark))
}

/// `Bars`, `BarWidth`, `BarGap`, `BarBoxWidth`, `BarBoxHeight`, `BarTallest`, `BarFramesPerSecond`: the activity mark.
pub const BARS: usize = 4;
pub const BAR_WIDTH: f64 = 3.0;
pub const BAR_GAP: f64 = 2.0;
pub const BAR_BOX_WIDTH: f64 = 18.0;
pub const BAR_BOX_HEIGHT: f64 = 14.0;
pub const BAR_TALLEST: f64 = 12.0;
pub const BAR_FRAMES_PER_SECOND: f64 = 24.0;

/// `BarHeight`: a capsule's height at a phase, 4 to 12, each a fifth of a cycle behind the one before.
pub fn bar_height(index: usize, phase: f64) -> f64 {
    4.0 + 8.0 * (((phase + index as f64 / 5.0) * 2.0 * std::f64::consts::PI).sin() + 1.0) / 2.0
}
