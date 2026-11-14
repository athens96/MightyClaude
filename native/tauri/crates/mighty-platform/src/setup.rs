//! The setup screen's install folder and run (C# `SetupScriptFiles`, SetupPlan.cs:215-333): a folder only this user can touch, the
//! script's start and end markers that are real folders and never links, the leftovers of earlier runs removed without following a
//! link, whether the app runs elevated, and whether the script's PowerShell still runs. W6-B.
//!
//! The fake ([`FakeSetupFiles`]) and `Unsupported`'s answers live here beside the port, so the parts that add ports in parallel
//! (W6-A's console, fonts and http) never edit the same lines of `fake.rs` and `unsupported.rs`.

use std::collections::HashSet;
use std::path::Path;
use std::sync::Mutex;
use std::sync::atomic::{AtomicBool, Ordering};

use crate::unsupported::Unsupported;
use crate::{PlatformError, Result};

/// Contract:
/// - `prepare_folder` creates `path` (and its parent folders) for this user alone when nothing is there, then checks it: a real
///   folder (no junction, link or other reparse point) owned by this user. On Windows it puts the protected user-only access list
///   back on every call (C# `PrepareDirectory`: full control for this user, nothing inherited, this user the owner). `Err` when a
///   check fails or the OS refused.
/// - `is_plain_folder` is true only for a real folder: a junction or link there does not count (C# `MarkerExists`).
/// - `remove_leftovers` removes everything inside `folder`; a junction or link is removed itself, never followed; what cannot be
///   removed is left (C# `RemoveLeftovers`).
/// - `is_elevated`: the app runs with an administrator's token (C# `IsElevated`).
/// - `is_power_shell_running`: the process `id` is alive and is Windows PowerShell (C# `IsPowerShellRunning`).
/// - `system_folder`: the system folder Windows reports (C# `Environment.GetFolderPath(SpecialFolder.System)`, which no environment
///   variable moves), where the script's PowerShell is taken from; None when the OS names none.
pub trait SetupFiles: Send + Sync {
    fn prepare_folder(&self, path: &Path) -> Result<()>;
    fn is_plain_folder(&self, path: &Path) -> bool;
    fn remove_leftovers(&self, folder: &Path);
    fn is_elevated(&self) -> bool;
    fn is_power_shell_running(&self, id: u32) -> bool;
    fn system_folder(&self) -> Option<String>;
}

/// The Mac has no setup install of this kind (its setup screen runs in a terminal pane, the Mac stage's): nothing is prepared, no
/// marker counts and nothing runs.
impl SetupFiles for Unsupported {
    fn prepare_folder(&self, _: &Path) -> Result<()> {
        Err(PlatformError::unsupported("SetupFiles", "prepare_folder"))
    }
    fn is_plain_folder(&self, _: &Path) -> bool {
        false
    }
    fn remove_leftovers(&self, _: &Path) {}
    fn is_elevated(&self) -> bool {
        false
    }
    fn is_power_shell_running(&self, _: u32) -> bool {
        false
    }
    fn system_folder(&self) -> Option<String> {
        None
    }
}

/// The checks' setup files: real folders on the disk read through `std` (a symbolic link is a link), an elevation the check sets,
/// and the PowerShell process ids it says are running. No access list is written.
#[derive(Default)]
pub struct FakeSetupFiles {
    elevated: AtomicBool,
    running: Mutex<HashSet<u32>>,
    prepared: Mutex<Vec<std::path::PathBuf>>,
    system: Mutex<Option<String>>,
}

impl FakeSetupFiles {
    pub fn set_elevated(&self, elevated: bool) {
        self.elevated.store(elevated, Ordering::SeqCst);
    }
    /// Says the PowerShell with `id` runs (or no longer does).
    pub fn set_running(&self, id: u32, running: bool) {
        let mut set = self.running.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
        if running {
            set.insert(id);
        } else {
            set.remove(&id);
        }
    }
    /// The system folder the fake reports (none by default).
    pub fn set_system_folder(&self, folder: Option<&str>) {
        *self.system.lock().unwrap_or_else(|poisoned| poisoned.into_inner()) = folder.map(str::to_string);
    }
    /// Every folder `prepare_folder` was asked for, in order.
    pub fn prepared(&self) -> Vec<std::path::PathBuf> {
        self.prepared.lock().unwrap_or_else(|poisoned| poisoned.into_inner()).clone()
    }
}

fn plain_folder(path: &Path) -> bool {
    std::fs::symlink_metadata(path).is_ok_and(|meta| meta.is_dir() && !meta.file_type().is_symlink())
}

impl SetupFiles for FakeSetupFiles {
    fn prepare_folder(&self, path: &Path) -> Result<()> {
        self.prepared.lock().unwrap_or_else(|poisoned| poisoned.into_inner()).push(path.to_path_buf());
        if let Some(parent) = path.parent().filter(|parent| !parent.as_os_str().is_empty()) {
            std::fs::create_dir_all(parent)?;
        }
        if std::fs::symlink_metadata(path).is_err() {
            std::fs::create_dir(path)?;
        }
        if !path.is_dir() {
            return Err(PlatformError::Refused(format!("setup folder is not a folder: {}", path.display())));
        }
        if !plain_folder(path) {
            return Err(PlatformError::Refused(format!("setup folder is not a plain folder: {}", path.display())));
        }
        Ok(())
    }
    fn is_plain_folder(&self, path: &Path) -> bool {
        plain_folder(path)
    }
    fn remove_leftovers(&self, folder: &Path) {
        let Ok(entries) = std::fs::read_dir(folder) else { return };
        for entry in entries.flatten() {
            let path = entry.path();
            let Ok(meta) = std::fs::symlink_metadata(&path) else { continue };
            let _ = if meta.file_type().is_symlink() {
                // A directory link is removed as a folder on Windows and unlinked elsewhere; either way only the link goes.
                std::fs::remove_file(&path).or_else(|_| std::fs::remove_dir(&path))
            } else if meta.is_dir() {
                std::fs::remove_dir_all(&path)
            } else {
                std::fs::remove_file(&path)
            };
        }
    }
    fn is_elevated(&self) -> bool {
        self.elevated.load(Ordering::SeqCst)
    }
    fn is_power_shell_running(&self, id: u32) -> bool {
        self.running.lock().unwrap_or_else(|poisoned| poisoned.into_inner()).contains(&id)
    }
    fn system_folder(&self) -> Option<String> {
        self.system.lock().unwrap_or_else(|poisoned| poisoned.into_inner()).clone()
    }
}
