//! The setup screen, `openSetup`, `installCli`, the badge (C# MainWindow.Setup.cs, MainWindow.WebShell.Settings.cs:218-241). Owner: W6-B.
//!
//! The seams the other parts call, with their final signatures. Each stub answers the resting value (no screen, no badge, nothing to
//! install) until W6-B fills it.

use std::sync::Arc;

use mighty_core::model::{AppSnapshot, RunSession};

use crate::host::Host;

/// What the frame model shows of the setup (C# `setupBadge` and `SetupNeeded`, the shell's `status.setup` and `setupNeeded`).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct ShellFlags {
    pub badge: bool,
    pub needed: bool,
}

/// `setup::open`: C# `OpenSetup(focus)` and `ShowWebSetup` — the setup screen (the surface `setup`, place `surface`), the item `focus`
/// brought forward; drawn again when it is open already. `openSetup` and the composer's `installCli` call it. Stub: W6-B fills it;
/// until then nothing opens.
pub fn open(_host: &Arc<Host>, _focus: Option<&str>) {}

/// `setup::shell_flags`: the setup badge and whether setup is needed, for the frame model. Called while the frame model is made, the
/// state locked: it reads the setup's own state and `snapshot`, never `host.view()`. Stub: W6-B fills it; until then no badge and no
/// banner.
pub fn shell_flags(_host: &Host, _snapshot: &AppSnapshot) -> ShellFlags {
    ShellFlags::default()
}

/// `setup::item_for_missing_cli`: C# `SetupItemForMissingCli` — the setup item that installs an agent pane's missing CLI, for the
/// blocked line's install button (`WindowRules.setup_item_for_missing_cli`). Called with the state locked, as [`shell_flags`]. Stub:
/// W6-B fills it; until then none.
pub fn item_for_missing_cli(_host: &Host, _snapshot: &AppSnapshot, _pane: &RunSession) -> Option<String> {
    None
}
