//! The workspace path rules the picture and HTML checks read (C# `WorkspaceFiles.RealPath`, `Contains`, `Resolve`, `OpenFile`,
//! `UncShare`, `FollowsLinkTarget`; WorkspaceFiles.cs:149-302), through `std::fs` and `std::path` so each OS follows its own
//! path rules as .NET does. The files pane's full port (listing, natural order, the handle re-check through
//! `GetFinalPathNameByHandleW`) is W4's; this keeps only what pictures and HTML previews need.

use std::path::{Component, Path, PathBuf};

/// `WorkspaceFiles.MaximumPathBytes`.
pub const MAXIMUM_PATH_BYTES: usize = 4_096;
const MAXIMUM_LINK_HOPS: usize = 40;

/// Whether paths compare ignoring case (C# `PathComparison`: OrdinalIgnoreCase on Windows).
fn ignore_case() -> bool {
    std::path::MAIN_SEPARATOR == '\\'
}

fn same(a: &str, b: &str) -> bool {
    if ignore_case() { crate::slash::ordinal_ignore_case(a, b).is_eq() } else { a == b }
}

/// `Path.GetFullPath`: absolute, `.` and `..` collapsed (missing parts kept).
pub fn full_path(path: &Path) -> Option<PathBuf> {
    let absolute = std::path::absolute(path).ok()?;
    let mut out = PathBuf::new();
    for component in absolute.components() {
        match component {
            Component::Prefix(p) => out.push(p.as_os_str()),
            Component::RootDir => out.push(Component::RootDir.as_os_str()),
            Component::CurDir => {}
            Component::ParentDir => {
                if out.parent().is_some() {
                    out.pop();
                }
            }
            Component::Normal(part) => out.push(part),
        }
    }
    Some(out)
}

/// `"server\share"` of a UNC path, or None for any other path (C# `WorkspaceFiles.UncShare`).
pub fn unc_share(path: &str) -> Option<String> {
    let p = path.replace('/', "\\");
    let upper = p.to_ascii_uppercase();
    let rest = if upper.starts_with(r"\\?\UNC\") || upper.starts_with(r"\\.\UNC\") {
        &p[8..]
    } else if p.starts_with(r"\\?\") || p.starts_with(r"\\.\") {
        return None;
    } else {
        p.strip_prefix(r"\\")?
    };
    let parts: Vec<&str> = rest.split('\\').filter(|s| !s.is_empty()).collect();
    Some(match parts.len() {
        0 => String::new(),
        1 => parts[0].to_string(),
        _ => format!("{}\\{}", parts[0], parts[1]),
    })
}

/// C# `WorkspaceFiles.FollowsLinkTarget`: a UNC target is followed only from the same share.
pub fn follows_link_target(target: &str, start: &str) -> bool {
    match unc_share(target) {
        None => true,
        Some(share) => unc_share(start).is_some_and(|s| s.eq_ignore_ascii_case(&share)),
    }
}

/// The link's target, None for a plain or missing item; Err when the link could not be read (the path is then refused).
fn link_target(path: &Path) -> Result<Option<PathBuf>, ()> {
    match std::fs::symlink_metadata(path) {
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => Ok(None),
        Err(_) => {
            // A missing parent folder reads as not found on Windows and as NotADirectory elsewhere: both are "missing" to C#.
            if path.parent().is_some_and(|p| !p.exists()) { Ok(None) } else { Err(()) }
        }
        Ok(meta) if meta.file_type().is_symlink() => std::fs::read_link(path).map(Some).map_err(|_| ()),
        Ok(_) => Ok(None),
    }
}

/// C# `WorkspaceFiles.RealPath` (WorkspaceFiles.cs:149): `..` collapsed first, then every link on the way replaced by its target;
/// missing parts stay as they are; None when links loop or one could not be read or leads off this machine.
pub fn real_path(path: &Path) -> Option<PathBuf> {
    let start = path.to_string_lossy().into_owned();
    let mut full = full_path(path)?;
    let mut hops = 0;
    loop {
        let components: Vec<Component> = full.components().collect();
        let mut current = PathBuf::new();
        let mut restart: Option<PathBuf> = None;
        for (index, component) in components.iter().enumerate() {
            match component {
                Component::Prefix(_) | Component::RootDir => {
                    current.push(component.as_os_str());
                    continue;
                }
                _ => {}
            }
            let next = current.join(component.as_os_str());
            match link_target(&next) {
                Err(()) => return None,
                Ok(Some(target)) => {
                    hops += 1;
                    if hops > MAXIMUM_LINK_HOPS {
                        return None;
                    }
                    let resolved = full_path(&current.join(&target))?;
                    if !follows_link_target(&resolved.to_string_lossy(), &start) {
                        return None;
                    }
                    let mut again = resolved;
                    for rest in &components[index + 1..] {
                        again.push(rest.as_os_str());
                    }
                    restart = Some(again);
                    break;
                }
                Ok(None) => current = next,
            }
        }
        match restart {
            None => return Some(current),
            Some(again) => full = full_path(&again)?,
        }
    }
}

fn trim_separator(value: &str) -> &str {
    // `Path.TrimEndingDirectorySeparator` leaves a root ("C:\", "/") alone.
    let trimmed = value.trim_end_matches(['/', std::path::MAIN_SEPARATOR]);
    if trimmed.is_empty() || trimmed.ends_with(':') { value } else { trimmed }
}

/// C# `WorkspaceFiles.Contains` (WorkspaceFiles.cs:213): whether a resolved path is the root or inside it.
pub fn contains(real_path: &Path, real_root: &Path) -> bool {
    let root_text = real_root.to_string_lossy();
    let path_text = real_path.to_string_lossy();
    let root = trim_separator(&root_text);
    let path = trim_separator(&path_text);
    if same(path, root) {
        return true;
    }
    let prefix = if root.ends_with(std::path::MAIN_SEPARATOR) { root.to_string() } else { format!("{root}{}", std::path::MAIN_SEPARATOR) };
    path.len() >= prefix.len() && path.is_char_boundary(prefix.len()) && same(&path[..prefix.len()], &prefix)
}

/// C# `Path.IsPathFullyQualified` for the OS the app runs on.
pub fn fully_qualified(path: &str) -> bool {
    mighty_core::normalize::fully_qualified(path)
}

/// C# `WorkspaceFiles.Resolve` (WorkspaceFiles.cs:229): the real path of an existing item under the root, or None.
pub fn resolve(relative: &str, root: &Path) -> Option<PathBuf> {
    if relative.contains('\0') || relative.contains(':') || relative.len() > MAXIMUM_PATH_BYTES {
        return None;
    }
    if relative.starts_with('/') || relative.starts_with('\\') || Path::new(relative).has_root() {
        return None;
    }
    if !fully_qualified(&root.to_string_lossy()) {
        return None;
    }
    let real_root = real_path(root)?;
    let candidate = if relative.is_empty() {
        Some(real_root.clone())
    } else {
        real_path(&real_root.join(relative.replace('/', std::path::MAIN_SEPARATOR_STR)))
    }?;
    (contains(&candidate, &real_root) && candidate.exists()).then_some(candidate)
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
    match link_target(&path) {
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
