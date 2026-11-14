//! Session history and resume (docs/tauri-conversion.md W4, core catalog subsystem 31): the older requests of a pane read back from
//! the CLI's own session record and replayed through the live graph's tracker (C# `SessionHistory`, `HistoryScan`, `HistoryReplay`,
//! `SessionHistoryState`, MightyCode.Core/SessionHistory.cs), and the earlier sessions of a workspace folder a new pane can continue (C#
//! `ResumableSessions`, `KnownSessionIDs`, MightyCode.Core/ResumableSessions.cs) for all six CLIs: Claude's and Codex's JSONL records,
//! Antigravity's transcripts and `conversation_summaries.db`, pi's session files, opencode's `opencode.db` and Hermes's `state.db`.
//!
//! Everything here only reads; the databases are opened read-only. Synchronous and file-bound: the host calls it off the page's thread.
//! No Tauri and no OS code (a Windows rule — path comparison ignoring case — reads mighty-run's `os::windows`).

pub mod acp;
pub mod antigravity;
pub mod backward;
pub mod codex_sessions;
pub mod files;
pub mod history;
pub mod pi;
pub mod replay;
pub mod resumable;
pub mod scan;
pub mod sqlite;
pub mod state;

pub use history::{SessionHistoryAnchor, SessionHistoryChunk, SessionHistoryError, SessionHistoryFile, SessionHistoryRequest};
pub use resumable::{ResumableSession, ResumableSessionListing, ResumableSessionQuery};
pub use state::{Phase, SessionHistoryState};

/// Whether this OS compares paths ignoring case and takes `\` as a separator (C# `OperatingSystem.IsWindows()` in these rules).
pub fn windows_paths() -> bool {
    mighty_run::os::windows()
}

/// A cancellation the readers look at between requests (C#'s `CancellationToken`).
pub trait Cancel {
    fn cancelled(&self) -> bool;
}

/// Never cancelled.
pub struct Never;

impl Cancel for Never {
    fn cancelled(&self) -> bool {
        false
    }
}

impl Cancel for std::sync::atomic::AtomicBool {
    fn cancelled(&self) -> bool {
        self.load(std::sync::atomic::Ordering::SeqCst)
    }
}
