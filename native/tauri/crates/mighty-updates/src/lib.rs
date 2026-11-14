//! Mighty Code's CLI updates, automatic updates and plugins (W6-D), with no Tauri dependency and no OS code: CLI updates with
//! their coordinators, schedule and holds (`cli_update`), Claude and Codex plugins (`plugins`) and their forms (`view`). Every
//! child process goes through one [`runner::CliRunner`], whose app implementation starts it through the platform's `ProcessHost`.
//!
//! The automatic updates (CLI and plugin) are a mutation of the user's machine a C# app on the same PC may be running too: the host
//! keeps them off until W9 unless `--allow-state-write` is given, and always in a smoke ([`cli_update::holds::AutomaticUpdatePolicy`]).

pub mod cli_update;
pub mod paths;
pub mod plugins;
pub mod runner;
pub mod view;

pub use runner::{CancellationToken, CliCall, CliRunResult, CliRunner, HostCliRunner, RunError};
