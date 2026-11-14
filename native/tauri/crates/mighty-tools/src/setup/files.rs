//! The folder the install script and its markers live in (C# `SetupScriptFiles`, SetupPlan.cs:215-333), held to what only this
//! user can touch through the [`SetupFiles`] port: a real folder (no junction or link), owned by this user, with an access list of
//! this user alone. The script is created new under a random name, and a name that is already there is refused rather than followed.

use std::io::Write;
use std::path::{Path, PathBuf};

use mighty_platform::SetupFiles;

/// `%LOCALAPPDATA%\MightyCode\setup`: machine-local, never roamed. A `--profile` run keeps it in its profile.
pub fn folder(profile_directory: Option<&Path>, local_app_data: &Path) -> PathBuf {
    match profile_directory.filter(|profile| !profile.as_os_str().is_empty()) {
        Some(profile) => profile.join("setup"),
        None => local_app_data.join("MightyCode").join("setup"),
    }
}

/// C# `PrepareDirectory`: creates `path` for this user alone when it is not there, then checks it.
pub fn prepare_directory(files: &dyn SetupFiles, path: &Path) -> Result<(), String> {
    files.prepare_folder(path).map_err(|error| error.to_string())
}

/// Writes `text` to a new file with a random name in `folder`, as UTF-8 with its mark, which Windows PowerShell needs to read
/// anything but ASCII. A name that is already there fails the write.
pub fn write_script(text: &str, folder: &Path) -> std::io::Result<PathBuf> {
    let path = folder.join(format!("install-{}.ps1", uuid::Uuid::new_v4().simple()));
    let mut file = std::fs::OpenOptions::new().write(true).create_new(true).open(&path)?;
    file.write_all(&[0xEF, 0xBB, 0xBF])?;
    file.write_all(text.as_bytes())?;
    Ok(path)
}

/// Fresh marker paths in `folder`, which nothing has made yet: `(started, done)`.
pub fn markers(folder: &Path) -> (PathBuf, PathBuf) {
    let id = uuid::Uuid::new_v4().simple().to_string();
    (folder.join(format!("started-{id}")), folder.join(format!("done-{id}")))
}

/// True only for a real folder at `path`: a junction or link there does not count.
pub fn marker_exists(files: &dyn SetupFiles, path: &Path) -> bool {
    files.is_plain_folder(path)
}

/// The script's PowerShell, as it wrote its process ID into the start marker; None until it has.
pub fn runner_process_id(files: &dyn SetupFiles, started_marker: &Path) -> Option<u32> {
    if !marker_exists(files, started_marker) {
        return None;
    }
    let file = started_marker.join("pid");
    let meta = std::fs::symlink_metadata(&file).ok()?;
    if !meta.is_file() || meta.file_type().is_symlink() || meta.len() > 32 {
        return None;
    }
    let text = std::fs::read(&file).ok()?;
    let text = String::from_utf8_lossy(&text);
    let id = crate::setup::windows::parse_int(text.trim())?;
    u32::try_from(id).ok().filter(|id| *id > 0)
}

/// Whether the run is over: the script made its end marker, or it started and its PowerShell is gone without making it (the window
/// was closed; a Windows Terminal tab leaves the app no process of its own to wait for). `is_running` stands in for the port's
/// process check (the checks').
pub fn run_ended(files: &dyn SetupFiles, started_marker: &Path, done_marker: &Path, is_running: Option<&dyn Fn(u32) -> bool>) -> bool {
    if marker_exists(files, done_marker) {
        return true;
    }
    match runner_process_id(files, started_marker) {
        Some(id) => !match is_running {
            Some(running) => running(id),
            None => files.is_power_shell_running(id),
        },
        None => false,
    }
}

/// Removes what earlier runs left in `folder`; a junction or link is removed itself, never followed.
pub fn remove_leftovers(files: &dyn SetupFiles, folder: &Path) {
    if !folder.is_dir() {
        return;
    }
    files.remove_leftovers(folder);
}
