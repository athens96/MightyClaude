//! C# `RunLogText` (RunLogText.cs): "Copy run log", every entry as `[role] body`, a blank line between entries.

use mighty_core::model::{LogEntry, RunSession};

use crate::strings;

/// Who an entry is from: the user, the agent that answered (the pane's when the entry names none), output, an error, or the app.
pub fn role(entry: &LogEntry, pane_provider: &str) -> String {
    match entry.kind() {
        "user" => strings::get("pane.log.roleUser"),
        "assistant" => strings::provider_label(entry.provider.as_deref().unwrap_or(pane_provider)).to_string(),
        "output" => strings::get("composer.sessionInfo.output"),
        "error" => strings::get("session.state.error"),
        _ => strings::get("pane.log.roleSystem"),
    }
}

pub fn copy(pane: &RunSession) -> String {
    pane.logs().iter().map(|entry| format!("[{}] {}", role(entry, pane.provider()), entry.text())).collect::<Vec<_>>().join("\n\n")
}
