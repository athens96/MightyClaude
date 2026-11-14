//! `cli`: CLI 업데이트 (C# MainWindow.Settings.cs:155-251, MainWindow.AutomaticUpdates.cs). Owner: W6-D.

use std::sync::Arc;

use mighty_native::ui::{El, Ui};

use crate::host::Host;

/// CLI 업데이트 (CLI update) (C# `BuildCliUpdateSectionFromState`, MainWindow.Settings.cs:155-251). Stub: W6-D fills it; until then the
/// box has no rows.
pub fn cli_update(_host: &Arc<Host>, ui: &mut Ui) -> El {
    ui.settings_rows().finish()
}
