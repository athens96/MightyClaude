//! `cli`: CLI 업데이트 (C# MainWindow.Settings.cs:155-251, MainWindow.AutomaticUpdates.cs). Owner: W6-D.

use std::sync::Arc;

use mighty_native::ui::{El, Ui};

use crate::host::Host;

/// CLI 업데이트 (CLI update) (C# `BuildCliUpdateSectionFromState`, MainWindow.Settings.cs:155-251): the two switches over their
/// explanations, the progress or last-run words beside 업데이트 하기, then a row per CLI result and per plugin result, built from the
/// host's updates (updates.rs) and drawn again as they change.
pub fn cli_update(host: &Arc<Host>, ui: &mut Ui) -> El {
    crate::updates::build_box(host, ui)
}
