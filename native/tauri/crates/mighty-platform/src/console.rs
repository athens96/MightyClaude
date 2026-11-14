//! The port that opens a command in a terminal window of its own, one that outlives the app (C# `CliAccountTerminal.LaunchPlan` with
//! `Process.Start`, CliAccounts.cs:579-; the setup screen's install, MainWindow.Setup.cs:440-470). Owner: W6-A.
//!
//! The sign-in of a CLI (its own login command) and the setup screen's install script run there: the app never types into it and
//! never reads it. Unlike every child of [`crate::ProcessHost`], the window is not the app's to end: it stays open when the app quits.

use std::collections::BTreeMap;
use std::path::PathBuf;

use crate::{BoxFuture, Result};

/// A command to open in a terminal window. Its `Debug` names the environment's variables, never their values (a CLI environment can
/// hold credentials).
#[derive(Clone, PartialEq, Eq, Default)]
pub struct TerminalLaunch {
    /// The command and its arguments, handed on as a list: no command line is ever made from text.
    pub argv: Vec<String>,
    /// The command's full path when it is known (it replaces the name; a `.cmd` shim runs through `cmd /c`).
    pub resolved: Option<String>,
    /// The folder it starts in.
    pub directory: Option<PathBuf>,
    /// The terminal tab's title, where the terminal has one.
    pub title: Option<String>,
    /// The whole environment it gets (the CLI environment, with what the caller adds).
    pub environment: BTreeMap<String, String>,
    /// False: the system's own console window even where a terminal app is installed (C#: Bedrock's setup).
    pub prefer_terminal_app: bool,
}

impl std::fmt::Debug for TerminalLaunch {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("TerminalLaunch")
            .field("argv", &self.argv)
            .field("resolved", &self.resolved)
            .field("directory", &self.directory)
            .field("title", &self.title)
            .field("environment", &self.environment.keys().collect::<Vec<_>>())
            .field("prefer_terminal_app", &self.prefer_terminal_app)
            .finish()
    }
}

/// A window opened.
pub trait TerminalWindow: Send + Sync {
    /// The process started holds the command itself (the console host), so its end is the command's; false when a terminal app took
    /// the command into a tab of its own and the process started ends at once (C#'s `opened(plan.Executable == ConsoleHost)`).
    fn holds_command(&self) -> bool;
    /// The exit code of the process started, once it ended (C# `WaitForExitAsync`).
    fn wait(&self) -> BoxFuture<'_, Result<i32>>;
}

/// Contract: `open` starts the window and answers at once, never waiting for the command; the process it starts is not the app's
/// child in any way that would end it with the app (no kill-on-close job, no inherited handles). It fails without opening anything
/// when the command is empty or cannot start.
pub trait TerminalLauncher: Send + Sync {
    fn open(&self, launch: TerminalLaunch) -> Result<Box<dyn TerminalWindow>>;
}

#[cfg(test)]
mod tests {
    use super::*;

    /// A launch's `Debug` names the variables and never prints their values.
    #[test]
    fn debug_names_the_environment_without_its_values() {
        let launch = TerminalLaunch {
            argv: vec!["claude".into(), "/login".into()],
            environment: BTreeMap::from([
                ("ANTHROPIC_API_KEY".to_string(), "sk-secret-value".to_string()),
                ("PATH".to_string(), "C:\\bin".to_string()),
            ]),
            ..TerminalLaunch::default()
        };
        let text = format!("{launch:?}");
        assert!(text.contains("ANTHROPIC_API_KEY") && text.contains("PATH") && text.contains("/login"));
        assert!(!text.contains("sk-secret-value") && !text.contains("C:\\\\bin"), "{text}");
    }
}
