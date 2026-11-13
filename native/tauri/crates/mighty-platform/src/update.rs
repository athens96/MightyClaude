//! Replacing the installed app with a staged update (C# `AppUpdateReplacement` + `--update-helper`, docs/windows-app-update.md;
//! Swift `AppUpdate` + `install.sh`, docs/app-update.md). Checking, downloading and verifying the signed manifest are shared logic;
//! only the swap is the OS's.

use std::path::PathBuf;

use crate::Result;

/// A verified package, unpacked into the updates folder.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct StagedUpdate {
    pub version: String,
    /// The downloaded archive.
    pub package: PathBuf,
    /// The new app inside the staging folder: `MightyCode.exe` (Windows) or `MightyCode.app` (the Mac).
    pub staged: PathBuf,
    /// The package's SHA-256 (lower-case hex) and size, which the Windows helper checks again before the swap.
    pub sha256: String,
    pub size: u64,
}

/// Contract: `hand_off` starts the OS's helper and returns once it runs; the caller then quits. The helper waits for this process
/// to exit (5 min at most), swaps the installed app for `update.staged` by two renames with a rollback (Windows also restores an
/// interrupted install first, re-checks the SHA-256, syncs the uninstall entry's `DisplayVersion` and appends `install.log`), and
/// starts the new app with a fresh user environment when `relaunch`. The 0.4.x helper arguments stay valid (`--update-helper
/// --staged --install --backup --package --sha256 --size --wait-pid --relaunch`).
pub trait UpdateInstaller: Send + Sync {
    /// The installed app this copy would replace, or None when it cannot update itself (a development build, a folder it cannot write).
    fn install_location(&self) -> Option<PathBuf>;
    fn hand_off(&self, update: &StagedUpdate, relaunch: bool) -> Result<()>;
}
