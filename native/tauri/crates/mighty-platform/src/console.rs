//! The port that opens a command in a terminal window of its own, one that outlives the app (C# `CliAccountTerminal.LaunchPlan` with
//! `Process.Start`, CliAccounts.cs:579-; the setup screen's install, MainWindow.Setup.cs:440-470). Owner: W6-A.
//!
//! The sign-in of a CLI (its own login command) and the setup screen's install script run there: the app never types into it and
//! never reads it. Unlike every child of [`crate::ProcessHost`], the window is not the app's to end: it stays open when the app quits.

use std::collections::BTreeMap;
use std::path::PathBuf;

use crate::{BoxFuture, Result};

/// A command to open in a terminal window.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
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
