//! Later stages' boxes, host-absent as in C# (MainWindow.Styles, MobileRemote, ScreenShare, RemoteWorkspaces, Companion). Owner: W6-A.

use std::sync::Arc;

use mighty_native::ui::{El, Ui};

use crate::host::Host;

/// 스타일 (Styles): register and rescan, the description, the rows from W9's backend (C# `BuildStylesSection`, MainWindow.Styles.cs:18-139).
/// Stub: W6-A1 fills it; until then the box has no rows.
pub fn styles(_host: &Arc<Host>, ui: &mut Ui) -> El {
    ui.settings_rows().finish()
}

/// 모바일 원격 (Mobile remote) as C# draws it with no host: the switches and fields disabled, no QR (C# `BuildMobileRemoteSection`,
/// MainWindow.MobileRemote.cs:93-456). Stub: W6-A1 fills it; until then the box has no rows.
pub fn mobile_remote(_host: &Arc<Host>, ui: &mut Ui) -> El {
    ui.settings_rows().finish()
}

/// 화면 보기·조작 (Screen share, beta): the paused line (C# `BuildScreenShareSection`, MainWindow.ScreenShare.cs:65-185). Stub: W6-A1
/// fills it; until then the box has no rows.
pub fn screen_share(_host: &Arc<Host>, ui: &mut Ui) -> El {
    ui.settings_rows().finish()
}

/// 원격 작업 공간 (Remote workspaces), not sharing, with no host (C# `BuildRemoteWorkspacesSection`, MainWindow.RemoteWorkspaces.cs:180-560).
/// Stub: W6-A1 fills it; until then the box has no rows.
pub fn remote_workspaces(_host: &Arc<Host>, ui: &mut Ui) -> El {
    ui.settings_rows().finish()
}

/// 컴패니언 (Companion): the completion notification switch and its permission row; the pets are W8's (C# `BuildCompanionSection`,
/// MainWindow.Companion.cs:510-578). Stub: W6-A1 fills it; until then the box has no rows.
pub fn companion(_host: &Arc<Host>, ui: &mut Ui) -> El {
    ui.settings_rows().finish()
}
