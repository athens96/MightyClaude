//! The status line per pane: refresher, runner, trust (C# MainWindow.StatusLine.cs, MainWindow.WebShell.Cards.cs:599-624). Owner: W6-B.
//!
//! The seams the other parts call, with their final signatures. Each stub answers the resting value (no status line drawn) until W6-B
//! fills it.

use std::sync::Arc;

use mighty_composer::cards::StatusLineInput;
use mighty_core::model::RunSession;

use crate::host::Host;

/// `status_line::set_enabled`: C# `SetStatusLineEnabled` — Settings › 화면's switch and the composer's toggle: the setting saved, each
/// pane's refresher started or stopped. Stub: W6-B fills it; until then it does nothing (the composer's toggle saves the setting).
#[allow(dead_code, reason = "a seam W6-A1 (Settings and the composer's toggle) calls")]
pub fn set_enabled(_host: &Arc<Host>, _on: bool) {}

/// `status_line::input`: what the pane's status line card shows (C# `RenderStatusLine`'s state), for `CardInputs.status_line`. Called
/// while the cards are made, the state locked: it reads the status line's own state, never `host.view()`. Stub: W6-B fills it; until
/// then none.
pub fn input(_host: &Host, _pane: &RunSession) -> Option<StatusLineInput> {
    None
}
