//! `general`: 화면, 글자, 에이전트 링크 (C# MainWindow.Settings.cs:83-142, MainWindow.TextAppearance.cs:113-202). Owner: W6-A.

use std::sync::Arc;

use mighty_native::ui::{El, Ui};

use crate::host::Host;

/// 화면 (Display): the theme and language pickers, the language note, the status line, browser and delegation switches (C#
/// `BuildDisplaySection`, MainWindow.Settings.cs:83-124). Stub: W6-A1 fills it; until then the box has no rows.
pub fn display(_host: &Arc<Host>, ui: &mut Ui) -> El {
    ui.settings_rows().finish()
}

/// 글자 (Text): the size, the interface, conversation and code fonts, the standard button (C# `BuildTextSection`,
/// MainWindow.TextAppearance.cs:113-202). Stub: W6-A1 fills it; until then the box has no rows.
pub fn text(_host: &Arc<Host>, ui: &mut Ui) -> El {
    ui.settings_rows().finish()
}

/// 에이전트 링크 (Agent links): what an agent's web-open prompt does (C# `BuildAgentLinksSection`, MainWindow.Settings.cs:127-142).
/// Stub: W6-A1 fills it; until then the box has no rows.
pub fn agent_links(_host: &Arc<Host>, ui: &mut Ui) -> El {
    ui.settings_rows().finish()
}
