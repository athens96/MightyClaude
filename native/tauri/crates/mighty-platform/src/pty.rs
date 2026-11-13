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
    /// Queues input (at most 64 KiB at once); false when the terminal has closed or its input queue is full.
    fn write(&self, bytes: &[u8]) -> bool;
    fn resize(&self, size: PtySize) -> Result<()>;
    /// The processes running in the terminal now (the shell and everything under it), for "is something still running?"; None once
    /// closed or when the OS gives no list.
    fn process_ids(&self) -> Option<Vec<u32>>;
    fn wait(&self) -> BoxFuture<'_, Result<i32>>;
    /// Ends the shell and its descendants (Windows: the session's kill-on-close job, then `ClosePseudoConsole`), lets the output
    /// drain read the console's final frame, and releases everything. Calling it again waits for the same close.
    fn close(&self) -> BoxFuture<'_, Result<()>>;
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
