//! `models`: 페이즈별 모델 (C# MainWindow.PhaseModels.cs, MainWindow.PhaseModelsSmoke.cs). Owner: W6-A.

use std::sync::Arc;

use mighty_native::ui::{El, Ui};

use crate::host::Host;

/// 페이즈별 모델 (Phase models): each phase's model and its draft (C# `BuildPhaseModelsSection`, MainWindow.PhaseModels.cs:23-240).
/// Stub: W6-A1 fills it; until then the box has no rows.
pub fn phase_models(_host: &Arc<Host>, ui: &mut Ui) -> El {
    ui.settings_rows().finish()
}
