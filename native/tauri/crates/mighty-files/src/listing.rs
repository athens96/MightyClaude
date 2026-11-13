//! A workspace folder's children as the files pane lists them (C# `WorkspaceFiles.List`, `Sorted`, `NaturalComparer`, `IsNoiseFolder`;
//! WorkspaceFiles.cs:100-271): hidden files shown, links followed only inside the root (a link out of it, or a dangling one, is left
//! out), folders first, then Finder/Explorer natural order, at most 5,000 entries of the first 20,000 names read.

use std::cmp::Ordering;
use std::path::Path;

use mighty_core::workspace_files::{contains, link_target, real_path, resolve};

use crate::utf16;

/// C# `WorkspaceFiles.NoiseFolders`: heavy or generated folders, which stay collapsed until the user opens them.
pub const NOISE_FOLDERS: [&str; 10] =
    [".git", "node_modules", ".build", "build", "dist", "DerivedData", ".next", "Pods", ".venv", "__pycache__"];
/// C# `WorkspaceFiles.MaximumEntriesPerFolder`.
pub const MAXIMUM_ENTRIES_PER_FOLDER: usize = 5_000;
/// C# `WorkspaceFiles.MaximumEnumeratedNames`: names read from one folder before the rest are left unread (the listing is then truncated).
pub const MAXIMUM_ENUMERATED_NAMES: usize = 20_000;

/// C# `WorkspaceFiles.IsNoiseFolder`.
pub fn is_noise_folder(name: &str) -> bool {
    NOISE_FOLDERS.contains(&name)
}

/// One row of the workspace tree (C# `WorkspaceFileEntry`); `relative_path` uses '/' under the root as the user sees it.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct FileEntry {
    pub name: String,
    pub relative_path: String,
    pub is_directory: bool,
    pub is_symlink: bool,
}

impl FileEntry {
    pub fn new(name: &str, relative_path: &str, is_directory: bool) -> FileEntry {
        FileEntry { name: name.into(), relative_path: relative_path.into(), is_directory, is_symlink: false }
    }

    /// C# `IsNoise`: a heavy or generated folder.
    pub fn is_noise(&self) -> bool {
        self.is_directory && is_noise_folder(&self.name)
    }
}

/// C# `WorkspaceDirectoryListing`.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct Listing {
    pub entries: Vec<FileEntry>,
    pub truncated: bool,
}

/// C# `WorkspaceFileError`.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ListError {
    OutsideRoot,
    NotDirectory,
    Unreadable,
}

/// C# `NaturalComparer`: case-insensitive (each UTF-16 unit as `OrdinalIgnoreCase` compares it), digit runs by value ("file2" before
/// "file10"), leading zeros aside; at the end the shorter rest first.
pub fn natural(x: &str, y: &str) -> Ordering {
    let (x, y) = (utf16::units(x), utf16::units(y));
    let digit = |u: u16| (b'0' as u16..=b'9' as u16).contains(&u);
    let (mut i, mut j) = (0, 0);
    while i < x.len() && j < y.len() {
        if digit(x[i]) && digit(y[j]) {
            let (si, sj) = (i, j);
            while i < x.len() && digit(x[i]) {
                i += 1;
            }
            while j < y.len() && digit(y[j]) {
                j += 1;
            }
            let trim = |run: &[u16]| -> Vec<u16> { run.iter().copied().skip_while(|u| *u == b'0' as u16).collect() };
            let (a, b) = (trim(&x[si..i]), trim(&y[sj..j]));
            if a.len() != b.len() {
                return a.len().cmp(&b.len());
            }
            match a.cmp(&b) {
                Ordering::Equal => continue,
                order => return order,
            }
        }
        match utf16::upper_unit(x[i]).cmp(&utf16::upper_unit(y[j])) {
            Ordering::Equal => {}
            order => return order,
        }
        i += 1;
        j += 1;
    }
    (x.len() - i).cmp(&(y.len() - j))
}

/// C# `WorkspaceFiles.Sorted`: folders first, then files; each group in natural order, then ordinal.
pub fn sorted(mut entries: Vec<FileEntry>) -> Vec<FileEntry> {
    entries.sort_by(|a, b| {
        (!a.is_directory).cmp(&!b.is_directory).then_with(|| natural(&a.name, &b.name)).then_with(|| utf16::ordinal(&a.name, &b.name))
    });
    entries
}

/// C# `WorkspaceFiles.List`: the folder's children, sorted, hidden files included; children whose real path leaves the root (and
/// dangling links) are left out. Only the first `enumeration_limit` names on disk are looked at.
pub fn list(relative: &str, root: &Path, enumeration_limit: usize) -> Result<Listing, ListError> {
    let directory = resolve(relative, root).ok_or(ListError::OutsideRoot)?;
    if !directory.is_dir() {
        return Err(ListError::NotDirectory);
    }
    let real_root = real_path(root).ok_or(ListError::OutsideRoot)?;
    let mut names = Vec::new();
    let mut unread = false;
    let reader = std::fs::read_dir(&directory).map_err(|_| ListError::Unreadable)?;
    for item in reader {
        let item = item.map_err(|_| ListError::Unreadable)?;
        if names.len() == enumeration_limit {
            unread = true;
            break;
        }
        names.push(item.file_name().to_string_lossy().into_owned());
    }
    let mut entries = Vec::new();
    for name in names.into_iter().filter(|n| !n.is_empty() && n != "." && n != "..") {
        let item = directory.join(&name);
        let Ok(target) = link_target(&item) else { continue };
        let link = target.is_some();
        let real = if link { real_path(&item) } else { Some(item) };
        let Some(real) = real.filter(|r| contains(r, &real_root) && r.exists()) else { continue };
        let relative_path = if relative.is_empty() { name.clone() } else { format!("{relative}/{name}") };
        entries.push(FileEntry { is_directory: real.is_dir(), name, relative_path, is_symlink: link });
    }
    let mut ordered = sorted(entries);
    let truncated = unread || ordered.len() > MAXIMUM_ENTRIES_PER_FOLDER;
    ordered.truncate(MAXIMUM_ENTRIES_PER_FOLDER);
    Ok(Listing { entries: ordered, truncated })
}
