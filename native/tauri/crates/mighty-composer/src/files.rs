//! The workspace path rules the picture and HTML checks read: the shared ones (C# `WorkspaceFiles.RealPath`, `Contains`, `Resolve`,
//! `UncShare`, `FollowsLinkTarget`) are mighty-core's [`workspace_files`](mighty_core::workspace_files), and `OpenFile`
//! (WorkspaceFiles.cs:278), with the open handle checked through the platform's [`FileHandles`] port, is here. The files pane's
//! listing, preview and changes are mighty-files'.

use std::path::{Path, PathBuf};
use std::sync::{Arc, RwLock};

use mighty_platform::FileHandles;
use mighty_platform::unsupported::Unsupported;

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

/// A regular file opened read-only under a root (C# `WorkspaceOpenFile`): read through `file`, never by its path again.
#[derive(Debug)]
pub struct OpenedFile {
    pub file: std::fs::File,
    /// The resolved path it was opened at.
    pub path: PathBuf,
    pub size: u64,
    pub modified: Option<std::time::SystemTime>,
}

/// The handle check every reader of the user's files uses (the host installs the OS's: [`use_file_handles`]); until then the path is
/// walked again after the open, as C# does off Windows.
static HANDLES: RwLock<Option<Arc<dyn FileHandles>>> = RwLock::new(None);

/// The OS's handle check for [`read_under`] (and every [`open_file`] the host makes through [`file_handles`]).
pub fn use_file_handles(handles: Arc<dyn FileHandles>) {
    if let Ok(mut installed) = HANDLES.write() {
        *installed = Some(handles);
    }
}

/// The installed handle check, or none asked ([`Unsupported`]: the path is walked again).
pub fn file_handles() -> Arc<dyn FileHandles> {
    HANDLES.read().ok().and_then(|installed| installed.clone()).unwrap_or_else(|| Arc::new(Unsupported))
}

/// C# `WorkspaceFiles.OpenFile` (WorkspaceFiles.cs:278): the file at `relative` under `root`, opened for reading: resolved under the
/// root, refused when its last part is (now) a link (Missing) or a folder (NotRegularFile), opened sharing read, write and delete as
/// C#'s `FileShare.ReadWrite | FileShare.Delete`, and then checked once more: where the OS can ask the open handle (`handles`,
/// Windows' `GetFinalPathNameByHandleW`) the handle must be a disk file whose final path is inside the root's final path, which closes
/// the gap between the walk and the open; elsewhere the path is walked again, as C# does off Windows.
pub fn open_file(relative: &str, root: &Path, handles: &dyn FileHandles) -> Result<OpenedFile, OpenError> {
    let path = resolve(relative, root).ok_or(OpenError::Missing)?;
    match mighty_core::workspace_files::link_target(&path) {
        Ok(None) => {}
        _ => return Err(OpenError::Missing),
    }
    if path.is_dir() {
        return Err(OpenError::NotRegularFile);
    }
    // std opens with FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE on Windows, as C#'s FileShare does.
    let file = std::fs::File::open(&path).map_err(|e| match e.kind() {
        std::io::ErrorKind::NotFound | std::io::ErrorKind::NotADirectory => OpenError::Missing,
        _ => OpenError::Unreadable,
    })?;
    let meta = file.metadata().map_err(|_| OpenError::Unreadable)?;
    if meta.is_dir() {
        return Err(OpenError::NotRegularFile);
    }
    let real_root = real_path(root).ok_or(OpenError::Unreadable)?;
    if handles.asks_handles() {
        if !handles.is_disk(&file) {
            return Err(OpenError::NotRegularFile);
        }
        let (Some(final_path), Some(final_root)) = (handles.final_path(&file), handles.final_directory_path(&real_root)) else {
            return Err(OpenError::Unreadable);
        };
        if !contains(&final_path, &final_root) {
            return Err(OpenError::Missing);
        }
    } else if !meta.is_file() {
        return Err(OpenError::NotRegularFile);
    } else if !real_path(&path).is_some_and(|again| contains(&again, &real_root)) {
        return Err(OpenError::Missing);
    }
    Ok(OpenedFile { size: meta.len(), modified: meta.modified().ok(), file, path })
}

/// [`open_file`] as its readers use it (the pictures, the HTML previews), with the installed handle check: the file's resolved path and
/// its bytes, at most `limit`.
pub fn read_under(relative: &str, root: &Path, limit: usize) -> Result<(PathBuf, Vec<u8>), OpenError> {
    use std::io::Read as _;
    let opened = open_file(relative, root, file_handles().as_ref())?;
    let mut data = Vec::new();
    opened.file.take(limit as u64).read_to_end(&mut data).map_err(|_| OpenError::Unreadable)?;
    Ok((opened.path, data))
}
