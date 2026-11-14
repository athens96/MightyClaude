//! `cli`: 이 PC의 CLI, CLI 계정 and Claude Mods (C# MainWindow.Settings.cs:145-153, :573-1026). Owner: W6-C.

use std::sync::Arc;

use mighty_native::ui::{El, Ui};

use crate::host::Host;

/// 이 PC의 CLI (Providers) (C# `BuildProvidersSection`, MainWindow.Settings.cs:576-621). Stub: W6-C fills it; until then the box has no
/// rows.
pub fn providers(_host: &Arc<Host>, ui: &mut Ui) -> El {
    ui.settings_rows().finish()
}

/// CLI 계정 (CLI accounts) (C# `BuildCliAccountsSectionFromState`, MainWindow.Settings.cs:628-1026). Stub: W6-C fills it; until then
/// the box has no rows.
pub fn accounts(_host: &Arc<Host>, ui: &mut Ui) -> El {
    ui.settings_rows().finish()
}

/// Claude Mods (C# `BuildClaudeModsSection`, MainWindow.Settings.cs:145-153). Stub: W6-C fills it; until then the box has no rows.
pub fn claude_mods(_host: &Arc<Host>, ui: &mut Ui) -> El {
    ui.settings_rows().finish()
}
