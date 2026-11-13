//! Mighty Code's terminals (W4-A), with no Tauri dependency and no OS code: every terminal opens through mighty-platform's
//! [`mighty_platform::PtyHost`] (Windows: ConPTY), every clipboard read and write goes through its [`mighty_platform::Clipboard`].
//!
//! - [`pane`]: a terminal pane's lifecycle (C# MainWindow.Terminal.cs and the terminal half of MainWindow.WebShell.Panes.cs): the
//!   page's `terminalMessage`s, the shell started, its output buffered and flushed, its exit, the restart, the shell's activity and
//!   the footer; an agent's terminal pane shows its owner's commands.
//! - [`agent`]: an agent pane's terminal (C# `AgentTerminal`): its commands, their output for the agent, for readers with their own
//!   cursor and for the terminal pane, Ctrl+C and the stop.
//! - [`prompt`]: a command waiting for input as a card (C# TerminalPrompt.cs).
//! - [`shell_blocks`]: a transcript's shell block runs (C# ShellBlockRuns.cs, ShellBlockScan.cs).
//! - [`activity`]: a shell pane's status from its terminal (C# `ShellActivity.NextStatus`, `DesktopService.SetShellStatus`).
//! - [`history`]: the shell pane's history dialog's content (C# `ShowTerminalHistory`; the native dialog is W6's).
//! - [`cleaner`]: escape sequences removed from a stream (C# `TerminalTextCleaner`); [`text`]: UTF-16 lengths and UTF-8 decoding.

pub mod activity;
pub mod agent;
pub mod cleaner;
pub mod history;
pub mod pane;
pub mod prompt;
pub mod shell_blocks;
pub mod text;

pub use agent::{AgentTerminal, AgentTerminalError, AgentTerminalResult};
pub use cleaner::TerminalTextCleaner;
pub use pane::{PaneEvents, PaneKind, PaneOptions, Ports, TerminalFont, TerminalFooter, TerminalMessage, TerminalPane};
pub use prompt::{PromptRead, PromptTerminal, TerminalPrompt, TerminalPromptAnswer, TerminalPromptMonitor};
