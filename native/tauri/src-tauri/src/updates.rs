//! CLI updates, automatic updates and their holds (C# MainWindow.AutomaticUpdates.cs, MainWindow.UpdateAdmission.cs). Owner: W6-D.
//!
//! The seams the other parts call, with their final signatures. Each stub answers the resting value (nothing updating, nothing held)
//! until W6-D fills it.

use std::sync::Arc;

use mighty_core::model::{AppSnapshot, RunSession};
use mighty_run::RunEvent;

use crate::host::Host;

/// `updates::any_running`: C# `AnyCliUpdateRunning` — a manual or automatic CLI update is under way (C's `RequireIdleAccount`). Stub:
/// W6-D fills it; until then none.
#[allow(dead_code, reason = "a seam W6-C (RequireIdleAccount) calls")]
pub fn any_running(_host: &Host) -> bool {
    false
}

/// `updates::start_manual`: C# `UpdateCliTools` — the manual update of every CLI (the setup screen's update row, Settings' button).
/// Stub: W6-D fills it; until then nothing starts.
#[allow(dead_code, reason = "a seam W6-B (the setup screen's update row) calls")]
pub fn start_manual(_host: &Arc<Host>) {}

/// `updates::holds`: C# `BackgroundUpdateHolds(pane)` — an automatic update of the pane's CLI holds its sends. Called while the composer
/// and the cards are made, the state locked: it reads the updates' own state and `snapshot`, never `host.view()`. Stub: W6-D fills it;
/// until then nothing holds a pane.
pub fn holds(_host: &Host, _snapshot: &AppSnapshot, _pane: &RunSession) -> bool {
    false
}

/// `updates::manual_block`: C# `ManualMutationBlockReason(pane)` — why a manual update or plugin change refuses the pane's sends.
/// Called with the state locked, as [`holds`]. Stub: W6-D fills it; until then none.
pub fn manual_block(_host: &Host, _snapshot: &AppSnapshot, _pane: &RunSession) -> Option<String> {
    None
}

/// `updates::manual_update`: C# `ManualUpdateReason(pane)` — the manual update of the pane's CLI under way. Called with the state
/// locked, as [`holds`]. Stub: W6-D fills it; until then none.
pub fn manual_update(_host: &Host, _snapshot: &AppSnapshot, _pane: &RunSession) -> Option<String> {
    None
}

/// `updates::section_fixture_statuses`: for the `settingsSections` smoke (C# `RunSettingsSectionsSmoke`), the CLI update box built
/// over `SettingsSectionsSmoke.FixtureResults`: the fixture's statuses and the statuses the built box shows, in order. Stub: W6-D fills
/// it; until then neither (the check then compares two empty lists).
#[allow(dead_code, reason = "a seam W6-A1 (the settingsSections smoke) calls")]
pub fn section_fixture_statuses(_host: &Arc<Host>) -> (Vec<String>, Vec<String>) {
    (vec![], vec![])
}

/// A run event (C# `RecordProviderActivity`, the first thing the window does with one). Stub: W6-D fills it; until then nothing is
/// recorded.
pub fn on_run_event(_host: &Arc<Host>, _event: &RunEvent) {}
