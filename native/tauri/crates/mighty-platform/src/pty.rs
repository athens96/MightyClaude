//! Pseudo terminals for the shell and agent-terminal panes (C# `PseudoTerminal`, ConPTY; Swift `PTYAgentTerminalPane`, forkpty).
//! The Mac's shell pane is a Ghostty view that owns its own PTY ([`crate::TerminalSurface`]); this port is for terminals the page
//! draws (xterm.js) and for the Mac's agent terminal.

use std::collections::BTreeMap;
use std::fmt;
use std::path::{Path, PathBuf};
use std::sync::Arc;

use tokio::sync::mpsc;

use crate::{BoxFuture, Result};

/// A terminal grid. Implementations clamp it as C# does: 2–1000 columns, 1–1000 rows.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct PtySize {
    pub columns: u16,
    pub rows: u16,
}

impl PtySize {
    pub fn clamped(self) -> PtySize {
        PtySize { columns: self.columns.clamp(2, 1000), rows: self.rows.clamp(1, 1000) }
    }
}

impl Default for PtySize {
    /// C# `PseudoTerminal.Start`'s 100 × 30.
    fn default() -> PtySize {
        PtySize { columns: 100, rows: 30 }
    }
}

#[derive(Clone)]
pub struct PtyRequest {
    /// A full path to the shell or program.
    pub program: PathBuf,
    pub args: Vec<String>,
    /// An existing absolute folder (the workspace).
    pub cwd: PathBuf,
    /// The child's whole environment (not laid over the app's): the resolved CLI environment. No name may be empty or hold `=`,
    /// no value a NUL (`Invalid`).
    pub env: BTreeMap<String, String>,
    pub size: PtySize,
}

impl fmt::Debug for PtyRequest {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("PtyRequest")
            .field("program", &self.program)
            .field("cwd", &self.cwd)
            .field("size", &self.size)
            .finish_non_exhaustive()
    }
}

/// A running terminal: its output (bytes as the console writes them, UTF-8; the channel ends once the console has closed and its
/// last frame is read) and its control.
pub struct PtySession {
    pub id: u32,
    pub output: mpsc::Receiver<Vec<u8>>,
    pub control: Arc<dyn PtyControl>,
}

impl fmt::Debug for PtySession {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("PtySession").field("id", &self.id).finish_non_exhaustive()
    }
}

/// Input, size and teardown, each independent of the others: a close never blocks the output drain or the caller's thread.
pub trait PtyControl: Send + Sync {
    /// Queues input: UTF-8 text of at most 65,536 UTF-16 units at once, as C# `TryWrite` counts a string's `Length` (30,000 Korean
    /// characters, 90,000 bytes, are taken); false when it is longer, the terminal has closed or its input queue is full.
    fn write(&self, bytes: &[u8]) -> bool;
    fn resize(&self, size: PtySize) -> Result<()>;
    /// The processes running in the terminal now (the shell and everything under it), for "is something still running?"; None once
    /// closed or when the OS gives no list.
    fn process_ids(&self) -> Option<Vec<u32>>;
    fn wait(&self) -> BoxFuture<'_, Result<i32>>;
    /// Ends the shell and its descendants (Windows: the session's kill-on-close job, then `ClosePseudoConsole`), lets the output
    /// drain read the console's final frame, and releases everything. It returns whether or not anyone reads the output: once it
    /// has begun, output that finds the channel full is dropped. What was queued before can still be read, then the output ends.
    /// Calling it again waits for the same close.
    fn close(&self) -> BoxFuture<'_, Result<()>>;
}

/// Text the app puts into a terminal for the user — a style's install command, a command the app wrote itself (C#
/// `TerminalInputPolicy`, TerminalInputPolicy.cs; Swift TerminalInputPolicy.swift:17-34). Whatever its source, a line break means
/// nothing is put in at all, and Enter follows only what was taken whole and only where the app says so.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum TerminalInput {
    Pasted,
    PastedAndRan,
    Refused,
    Failed,
}

impl TerminalInput {
    /// LF, CR, U+2028 or U+2029 anywhere in `text`.
    pub fn has_line_break(text: &str) -> bool {
        text.contains(['\n', '\r', '\u{2028}', '\u{2029}'])
    }

    /// Puts `text` in through `write` (false when the terminal took none of it, as [`PtyControl::write`] answers), then `\r` when
    /// `auto_run`. The refusal's sentence is the locale's `terminal.input.newlineRefused`.
    pub fn apply(text: &str, auto_run: bool, mut write: impl FnMut(&str) -> bool) -> TerminalInput {
        if TerminalInput::has_line_break(text) {
            return TerminalInput::Refused;
        }
        if !write(text) {
            return TerminalInput::Failed;
        }
        if !auto_run {
            return TerminalInput::Pasted;
        }
        if write("\r") { TerminalInput::PastedAndRan } else { TerminalInput::Failed }
    }
}

/// Opens pseudo terminals.
pub trait PtyHost: Send + Sync {
    /// The shell a new terminal runs: Windows PowerShell 5.1 when present, else `%ComSpec%`; on the Mac the user's login shell.
    fn default_shell(&self) -> PathBuf;
    /// The arguments for `shell`: interactive, or running `command` and exiting. Windows sets the console to UTF-8 for this shell
    /// only (never the user's profile or the system); `no_profile` skips the shell's profile.
    fn shell_arguments(&self, shell: &Path, command: Option<&str>, no_profile: bool) -> Vec<String>;
    /// Starts `request` in a new terminal. Its processes die with the session (Windows: a kill-on-close job; the Mac: the session's
    /// process group) and never inherit the app's redirected standard handles.
    fn open(&self, request: PtyRequest) -> BoxFuture<'_, Result<PtySession>>;
}

#[cfg(test)]
mod tests {
    use super::*;

    /// C# "composer parity: text with a line break is not put into a terminal" (ComposerParityVerification.cs:444-463; Swift
    /// TerminalInputPolicyTests.swift:15-37). The locale's refusal sentence is checked with the locales (mighty-core).
    #[test]
    fn terminal_input_refuses_a_line_break() {
        let mut written: Vec<String> = Vec::new();
        let outcome = TerminalInput::apply("npx example-installer", false, |text| {
            written.push(text.into());
            true
        });
        assert!(
            outcome == TerminalInput::Pasted && written == ["npx example-installer"],
            "a command from elsewhere is put in and left for the user to run"
        );
        written.clear();
        let outcome = TerminalInput::apply("claude login", true, |text| {
            written.push(text.into());
            true
        });
        assert!(
            outcome == TerminalInput::PastedAndRan && written == ["claude login", "\r"],
            "only a command the app wrote itself is followed by Enter"
        );
        for separator in ["\n", "\r", "\u{2028}", "\u{2029}"] {
            written.clear();
            let text = format!("rm -rf /{separator}echo done");
            assert!(TerminalInput::has_line_break(&text), "a line break is seen: {separator:?}");
            let outcome = TerminalInput::apply(&text, true, |text| {
                written.push(text.into());
                true
            });
            assert!(outcome == TerminalInput::Refused && written.is_empty(), "a line break stops the whole text: {separator:?}");
        }
        assert!(!TerminalInput::has_line_break("echo one && echo two") && !TerminalInput::has_line_break(""), "one line is one line");
        let mut attempts = 0;
        let outcome = TerminalInput::apply("ls", true, |_| {
            attempts += 1;
            false
        });
        assert!(outcome == TerminalInput::Failed && attempts == 1, "text the terminal did not take is never followed by Enter");
        assert_eq!(
            TerminalInput::apply("ls", true, |text| text != "\r"),
            TerminalInput::Failed,
            "an Enter the terminal did not take is a failure too"
        );
    }
}
