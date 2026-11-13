//! A shell pane's "previous command history" (C# `ShowTerminalHistory`, MainWindow.WorkflowParity.cs:10-23): the pane's saved
//! transcript, read-only, in a panel dialog (`DialogChrome { Panel = "terminalHistory", Icon = "square-terminal" }`). The dialog is a
//! native form (host-contract D14), which W6 draws as a NativeNode view; this is its content.

use mighty_core::locale::Locale;
use mighty_core::model::RunSession;

/// What the dialog shows.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct TerminalHistory {
    pub title: String,
    pub help: String,
    /// Each saved entry as `[kind] text`, a blank line apart; the empty note when there is none.
    pub text: String,
    /// The close button's words.
    pub close: String,
    /// The dialog's chrome: its panel's name and icon.
    pub panel: &'static str,
    pub icon: &'static str,
}

pub fn terminal_history(pane: &RunSession, locale: &Locale) -> TerminalHistory {
    let logs = pane.logs();
    let text = if logs.is_empty() {
        locale.get("terminal.history.empty")
    } else {
        logs.iter().map(|log| format!("[{}] {}", log.kind(), log.text())).collect::<Vec<_>>().join("\n\n")
    };
    TerminalHistory {
        title: locale.get("terminal.history.title"),
        help: locale.get("terminal.history.help"),
        text,
        close: locale.get("settings.closeButton"),
        panel: "terminalHistory",
        icon: "square-terminal",
    }
}
