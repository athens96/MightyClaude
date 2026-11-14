//! The model menu's "모델 ID 입력…" dialog (C# `PaneView.CustomModel`, MainWindow.cs:986) as data: W6's NativeNode form draws it from
//! [`dialog`] and hands what was typed to [`accept`]; the host then changes the pane's model with it (composer.rs `change_model`).

use mighty_core::locale::Locale;
use mighty_core::model::RunSession;
use mighty_core::wire;

/// The dialog's words and its field's first value.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CustomModelDialog {
    pub title: String,
    /// The field's header.
    pub header: String,
    /// The pane's model now.
    pub value: String,
    pub select: String,
    pub cancel: String,
}

/// The dialog for `pane`; None while the pane runs (C# returns at once).
pub fn dialog(pane: &RunSession, l: &Locale) -> Option<CustomModelDialog> {
    (pane.status() != "running").then(|| CustomModelDialog {
        title: l.get("composer.model.enterIdTitle"),
        header: l.get("composer.model.idHeader"),
        value: pane.model().to_string(),
        select: l.get("composer.model.select"),
        cancel: l.get("settings.run.cancelButton"),
    })
}

/// `Valid()`: the trimmed model name the select button takes, or the words the dialog shows under its field (the dialog stays open).
pub fn accept(text: &str, l: &Locale) -> Result<String, String> {
    let trimmed = text.trim();
    if wire::model(trimmed) { Ok(trimmed.to_string()) } else { Err(l.get("composer.model.invalidName")) }
}
