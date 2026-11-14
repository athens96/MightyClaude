//! CLI updates (C# `CliUpdateService`, `CliUpdateCoordinator`, `CliAutoUpdateSchedule`, `CliUpdateStrings`, the update holds).
//!
//! - [`service`]: updates one installed, recognised CLI through its own installer (native, winget, npm, pi, opencode, Hermes, agy).
//! - [`coordinator`]: the 업데이트 하기 run over every provider in order (macOS `AppStore` `isUpdatingCLIs`).
//! - [`schedule`]: when an automatic pass is due, idle long enough, and the deferred retries.
//! - [`holds`]: what a running update holds (C# MainWindow.AutomaticUpdates.cs, MainWindow.UpdateAdmission.cs) and the automatic
//!   pass itself, over a host trait.
//! - [`strings`]: the section's copy.

pub mod coordinator;
pub mod holds;
pub mod schedule;
pub mod service;
pub mod strings;

pub use coordinator::CliUpdateCoordinator;
pub use schedule::CliAutoUpdateSchedule;
pub use service::{CliUpdateInstallation, CliUpdateResult, CliUpdateService};
