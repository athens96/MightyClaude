//! The toolkit, 내 작업 도구 모음 (C# `ToolkitFileReader`, `ToolkitStore`, `ToolkitProbe`, `ToolkitRunner`, `ToolkitPresentation`).
//! `toolkit.json` beside the state file is shared with the Mac byte for byte: entries this OS does not run (brew, repoScript) are
//! kept in the file untouched and never shown or planned.

pub mod file;
pub mod probe;
pub mod runner;
pub mod store;

pub use file::{ToolkitApproval, ToolkitEntrySource, ToolkitFile, ToolkitFileEntry, ToolkitInstallSpec};
pub use probe::{ProbeResult, ToolkitProbeContext};
pub use runner::{
    CommandOutput, PlanAction, ProcessToolkitExecutor, StepOutcome, ToolkitExecutor, ToolkitPlanItem, ToolkitRunItem, ToolkitRunner,
    ToolkitStepResult, Verdict,
};
pub use store::ToolkitStore;

use std::path::PathBuf;

use mighty_platform::Paths;

/// Where `toolkit.json` is kept (C# `StateDirectory`, the folder of the state file, which follows `--profile`): the profile's own with
/// `--allow-state-write`, else this machine's local folder for the profile (`%LOCALAPPDATA%\dev.mightycode.tauri\toolkit\<profile>`),
/// so the dev build never writes the toolkit the installed C# app reads (W6 defaults).
pub fn store_folder(paths: &dyn Paths, writes: bool) -> PathBuf {
    if writes { paths.data_dir().to_path_buf() } else { paths.local_dir().join("toolkit").join(paths.profile_key()) }
}
