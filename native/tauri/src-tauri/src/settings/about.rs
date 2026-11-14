//! `about`: 앱 업데이트 and 앱 정보 (C# MainWindow.AppUpdate.cs:110-255, MainWindow.Settings.cs:1031-1045). Owner: W6-A.

use std::sync::Arc;

use mighty_native::ui::{El, Ui};

use crate::host::Host;

/// 앱 업데이트 (App update): the no-key build's version, refusal and disabled button; the backend is W9's (C#
/// `BuildAppUpdateSectionFromState`, MainWindow.AppUpdate.cs:110-255). Stub: W6-A1 fills it; until then the box has no rows.
pub fn app_update(_host: &Arc<Host>, ui: &mut Ui) -> El {
    ui.settings_rows().finish()
}

/// 앱 정보 (App info): the version, the help button, the state folder (C# `BuildAppInfoSection`, MainWindow.Settings.cs:1031-1045).
/// Stub: W6-A1 fills it; until then the box has no rows.
pub fn app_info(_host: &Arc<Host>, ui: &mut Ui) -> El {
    ui.settings_rows().finish()
}
