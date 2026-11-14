//! The path and environment rules the C# updater and plugin readers read from .NET (`Path.IsPathRooted`, `Path.GetFullPath`,
//! `Path.TrimEndingDirectorySeparator`, `File.ResolveLinkTarget`, the PATH walk, a dictionary of variables that ignores case on
//! Windows), over std's paths. Whether names ignore case follows `mighty_run::os::windows()`, which the host sets from the profile.

use std::collections::BTreeMap;
use std::path::{Component, Path, PathBuf};

/// The executable extensions every candidate is tried with, the bare name last so a fixture without one is still found.
pub const EXTENSIONS: [&str; 4] = [".cmd", ".exe", ".bat", ""];

fn ignores_case() -> bool {
    mighty_run::os::windows()
}

fn same_name(a: &str, b: &str) -> bool {
    if ignores_case() { a.eq_ignore_ascii_case(b) } else { a == b }
}

/// A variable of `environment` (C#'s `OrdinalIgnoreCase` dictionary on Windows).
pub fn variable<'a>(environment: &'a BTreeMap<String, String>, name: &str) -> Option<&'a str> {
    environment.get(name).or_else(|| environment.iter().find(|(key, _)| same_name(key, name)).map(|(_, value)| value)).map(String::as_str)
}

/// `values[name] = value`: one entry per name.
pub fn set_variable(environment: &mut BTreeMap<String, String>, name: &str, value: &str) {
    remove_variable(environment, name);
    environment.insert(name.to_string(), value.to_string());
}

/// `values.Remove(name)`.
pub fn remove_variable(environment: &mut BTreeMap<String, String>, name: &str) {
    environment.retain(|key, _| !same_name(key, name));
}

/// `Path.IsPathRooted`.
pub fn rooted(path: &str) -> bool {
    let path = Path::new(path);
    path.has_root() || path.is_absolute()
}

/// `Path.TrimEndingDirectorySeparator(Path.GetFullPath(path))`: absolute against the working folder, `.` and `..` folded, no
/// separator at the end but a root's. None for a path .NET refuses.
pub fn full_path(path: &str) -> Option<PathBuf> {
    if path.is_empty() || path.contains('\0') {
        return None;
    }
    let absolute = std::path::absolute(path).ok()?;
    let mut folded = PathBuf::new();
    for component in absolute.components() {
        match component {
            Component::ParentDir => {
                folded.pop();
            }
            Component::CurDir => {}
            other => folded.push(other.as_os_str()),
        }
    }
    Some(folded)
}

/// C#'s `Resolve`: the full path, and when it is a link (a file's or a folder's) the final target's full path; the path as it was
/// when either cannot be read.
pub fn resolve(path: &str) -> String {
    let Some(mut current) = full_path(path) else { return path.to_string() };
    for _ in 0..40 {
        let Ok(metadata) = std::fs::symlink_metadata(&current) else { break };
        if !metadata.file_type().is_symlink() {
            break;
        }
        let Ok(target) = std::fs::read_link(&current) else { return path.to_string() };
        let joined = if target.is_absolute() { target } else { current.parent().map(|parent| parent.join(&target)).unwrap_or(target) };
        let Some(next) = full_path(&joined.to_string_lossy()) else { return path.to_string() };
        current = next;
    }
    current.to_string_lossy().into_owned()
}

/// C#'s `Same`: both resolved, without a trailing separator, equal ignoring case (`OrdinalIgnoreCase`, on every OS as in C#).
pub fn same(left: Option<&str>, right: Option<&str>) -> bool {
    let (Some(left), Some(right)) = (left, right) else { return false };
    let trim = |value: String| value.trim_end_matches(['\\', '/']).to_string();
    let (left, right) = (trim(resolve(left)), trim(resolve(right)));
    left.to_lowercase() == right.to_lowercase()
}

/// `Path.GetDirectoryName` as a string.
pub fn parent(path: &str) -> Option<String> {
    Path::new(path).parent().filter(|parent| !parent.as_os_str().is_empty()).map(|parent| parent.to_string_lossy().into_owned())
}

/// `Path.Combine(base, parts…)`.
pub fn join(base: &str, parts: &[&str]) -> String {
    parts.iter().fold(PathBuf::from(base), |path, part| path.join(part)).to_string_lossy().into_owned()
}

/// The PATH entries (at most 64, each once ignoring case, rooted, no NUL) joined with `name` and each of [`EXTENSIONS`].
pub fn path_candidates(environment: &BTreeMap<String, String>, name: &str) -> Vec<String> {
    let separator = mighty_run::os::path_separator();
    let mut seen: Vec<String> = Vec::new();
    let mut candidates = Vec::new();
    for entry in variable(environment, "PATH").unwrap_or("").split(separator).take(64) {
        if entry.is_empty()
            || entry.contains('\0')
            || !rooted(entry)
            || seen.iter().any(|known| known.to_lowercase() == entry.to_lowercase())
        {
            continue;
        }
        seen.push(entry.to_string());
        for extension in EXTENSIONS {
            candidates.push(join(entry, &[&format!("{name}{extension}")]));
        }
    }
    candidates
}

/// `text[..limit]` in UTF-16 units (never through a character).
pub fn utf16_prefix(text: &str, limit: usize) -> String {
    let mut units = 0;
    let mut end = 0;
    for (at, c) in text.char_indices() {
        if units + c.len_utf16() > limit {
            break;
        }
        units += c.len_utf16();
        end = at + c.len_utf8();
    }
    text[..end].to_string()
}

/// `text[^limit..]` in UTF-16 units (never through a character).
pub fn utf16_suffix(text: &str, limit: usize) -> String {
    let mut units = 0;
    let mut start = text.len();
    for (at, c) in text.char_indices().rev() {
        if units + c.len_utf16() > limit {
            break;
        }
        units += c.len_utf16();
        start = at;
    }
    text[start..].to_string()
}
