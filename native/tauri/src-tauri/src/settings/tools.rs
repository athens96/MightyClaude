//! `tools`: 구성 요소 and 내 작업 도구 모음 (C# MainWindow.Settings.cs:253-571, MainWindow.Setup.cs:175-384). Owner: W6-B.

use std::sync::Arc;

use mighty_native::ui::{El, Ui};

use crate::host::Host;

/// 구성 요소 (Components) (C# `BuildComponentsSection`, MainWindow.Settings.cs:288-294 with MainWindow.Setup.cs:175-384). Stub: W6-B
/// fills it; until then the box has no rows.
pub fn components(_host: &Arc<Host>, ui: &mut Ui) -> El {
    ui.settings_rows().finish()
}

/// 내 작업 도구 모음 (Toolkit) (C# `BuildToolkitSection`, MainWindow.Settings.cs:296-571). Stub: W6-B fills it; until then the box has
/// no rows.
pub fn toolkit(_host: &Arc<Host>, ui: &mut Ui) -> El {
    ui.settings_rows().finish()
}
