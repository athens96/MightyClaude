//! The workspace path rules the picture and HTML checks read: the shared ones (C# `WorkspaceFiles.RealPath`, `Contains`, `Resolve`,
//! `UncShare`, `FollowsLinkTarget`) are mighty-core's [`workspace_files`](mighty_core::workspace_files), and `OpenFile`
//! (WorkspaceFiles.cs:278) as pictures and HTML previews read files is here. The files pane's full port (listing, natural order) is
//! W4's.

use std::path::{Path, PathBuf};

pub use mighty_core::workspace_files::{MAXIMUM_PATH_BYTES, contains, follows_link_target, full_path, real_path, resolve, unc_share};

/// C# `Path.IsPathFullyQualified` for the OS the app runs on.
pub fn fully_qualified(path: &str) -> bool {
    mighty_core::normalize::fully_qualified(path)
}

/// Why a file could not be opened (C# `WorkspaceFileOpenError`).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum OpenError {
    Missing,
    NotRegularFile,
    Unreadable,
}

/// C# `WorkspaceFiles.OpenFile` (WorkspaceFiles.cs:278) as its readers use it: the file at `relative` under `root`, refused when its
/// last part is (now) a link, not a regular file, or its real path left the root by the time it was opened; its bytes, at most `limit`.
pub fn read_under(relative: &str, root: &Path, limit: usize) -> Result<(PathBuf, Vec<u8>), OpenError> {
    use std::io::Read as _;
    let path = resolve(relative, root).ok_or(OpenError::Missing)?;
    match mighty_core::workspace_files::link_target(&path) {
        Ok(None) => {}
        _ => return Err(OpenError::Missing),
    }
    if path.is_dir() {
        return Err(OpenError::NotRegularFile);
    }
    let file = std::fs::File::open(&path)
        .map_err(|e| if e.kind() == std::io::ErrorKind::NotFound { OpenError::Missing } else { OpenError::Unreadable })?;
    let meta = file.metadata().map_err(|_| OpenError::Unreadable)?;
    if !meta.is_file() {
        return Err(OpenError::NotRegularFile);
    }
    let real_root = real_path(root).ok_or(OpenError::Unreadable)?;
    if !real_path(&path).is_some_and(|again| contains(&again, &real_root)) {
        return Err(OpenError::Missing);
    }
    let mut data = Vec::new();
    file.take(limit as u64).read_to_end(&mut data).map_err(|_| OpenError::Unreadable)?;
    Ok((path, data))
}
