//! C# `RecordFiles` (SessionHistory.cs:207-353): record files are opened as regular files only, never through a link, and read in
//! bounded pieces.

use std::fs::File;
use std::io::{Read, Seek, SeekFrom};
use std::path::{Path, PathBuf};
use std::sync::LazyLock;
use std::time::SystemTime;

use mighty_core::time::{self, DateTimeOffset};
use regex::Regex;

static ROLLOUT_NAME: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(
        r"^rollout-\d{4}-\d{2}-\d{2}T\d{2}-\d{2}-\d{2}-(?<thread>[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12})\.jsonl\z",
    )
    .expect("the rollout name")
});

/// A rollout file's thread id (`rollout-YYYY-MM-DDTHH-MM-SS-<uuid>.jsonl`).
pub fn thread(file_name: &str) -> Option<String> {
    ROLLOUT_NAME.captures(file_name).and_then(|c| c.name("thread")).map(|m| m.as_str().to_string())
}

/// A file that exists and is no link (C# also refuses any other reparse point; a symbolic link and a junction are what this sees).
pub fn regular(path: &Path) -> bool {
    std::fs::symlink_metadata(path).is_ok_and(|m| m.file_type().is_file() && !m.file_type().is_symlink())
}

/// A folder that exists and is no link.
pub fn real_directory(path: &Path) -> bool {
    std::fs::symlink_metadata(path).is_ok_and(|m| m.file_type().is_dir() && !m.file_type().is_symlink())
}

/// `RecordFiles.Open`: a regular file opened for reading, the CLI still free to write, rename or delete it.
pub fn open(path: &Path) -> Option<File> {
    if !regular(path) {
        return None;
    }
    File::open(path).ok()
}

/// A `SystemTime` as .NET's `DateTimeOffset` in UTC.
pub fn date_of(value: SystemTime) -> DateTimeOffset {
    DateTimeOffset::from_utc_ticks(time::ticks_of(chrono::DateTime::<chrono::Utc>::from(value)))
}

/// `File.GetLastWriteTimeUtc`.
pub fn modified(path: &Path) -> DateTimeOffset {
    std::fs::metadata(path).and_then(|m| m.modified()).map(date_of).unwrap_or(DateTimeOffset::from_utc_ticks(0))
}

/// `File.GetCreationTimeUtc(path).Ticks`: the file's identity besides its path (an atomic replace or a rotation creates a new file).
pub fn created_ticks(path: &Path) -> i64 {
    std::fs::metadata(path).and_then(|m| m.created()).map(|t| date_of(t).utc_ticks()).unwrap_or(0)
}

/// `$CODEX_HOME/sessions` with its own link resolved once; nothing below it may be a link.
pub fn sessions(codex_home: &Path) -> Option<PathBuf> {
    let mut path = codex_home.join("sessions");
    match std::fs::symlink_metadata(&path) {
        Ok(meta) if meta.file_type().is_symlink() => {
            path = mighty_core::workspace_files::real_path(&path).unwrap_or(path);
        }
        Ok(_) => {}
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => {}
        Err(_) => return None,
    }
    real_directory(&path).then_some(path)
}

fn file_name(path: &Path) -> String {
    path.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default()
}

fn numbered_folders(path: &Path) -> Vec<PathBuf> {
    let Ok(entries) = std::fs::read_dir(path) else { return vec![] };
    let mut folders: Vec<PathBuf> = entries
        .flatten()
        .map(|e| e.path())
        .filter(|d| {
            let name = file_name(d);
            !name.is_empty() && !name.starts_with('.') && name.chars().all(|c| c.is_ascii_digit()) && real_directory(d)
        })
        .collect();
    folders.sort_by_key(|f| std::cmp::Reverse(file_name(f)));
    folders
}

/// Every day folder, newest first, at most 4 000; links are not followed.
pub fn day_folders(sessions: &Path) -> Vec<PathBuf> {
    let mut days = vec![];
    for year in numbered_folders(sessions) {
        for month in numbered_folders(&year) {
            for day in numbered_folders(&month) {
                days.push(day);
                if days.len() >= 4_000 {
                    return days;
                }
            }
        }
    }
    days
}

/// The files directly in a folder (`Directory.EnumerateFiles`: anything that is no folder).
fn listed_files(folder: &Path) -> Vec<PathBuf> {
    let Ok(entries) = std::fs::read_dir(folder) else { return vec![] };
    entries.flatten().filter(|e| e.file_type().is_ok_and(|t| !t.is_dir())).map(|e| e.path()).collect()
}

/// One rollout file of a day folder.
#[derive(Debug, Clone)]
pub struct Rollout {
    pub path: PathBuf,
    pub thread: String,
    pub modified: DateTimeOffset,
}

/// Rollout files in one day folder that are regular files, not links.
pub fn rollouts(folder: &Path) -> Vec<Rollout> {
    listed_files(folder)
        .into_iter()
        .filter_map(|path| {
            let thread = thread(&file_name(&path))?;
            regular(&path).then(|| Rollout { modified: modified(&path), path, thread })
        })
        .collect()
}

/// Regular files (not links) with the extension directly in the folder, with when each was written.
pub fn files(folder: &Path, extension: &str) -> Vec<(PathBuf, DateTimeOffset)> {
    listed_files(folder)
        .into_iter()
        .filter(|p| {
            let name = file_name(p);
            p.extension().is_some_and(|e| format!(".{}", e.to_string_lossy()) == extension) && !name.starts_with('.') && regular(p)
        })
        .map(|p| {
            let at = modified(&p);
            (p, at)
        })
        .collect()
}

/// The file's length now.
pub fn length(file: &File) -> i64 {
    file.metadata().map(|m| m.len() as i64).unwrap_or(0)
}

/// `RecordFiles.Read`: as many bytes as fit from `offset`, fewer at the end or after a failed read.
pub fn read_at(file: &mut File, buffer: &mut [u8], offset: i64) -> usize {
    if file.seek(SeekFrom::Start(offset.max(0) as u64)).is_err() {
        return 0;
    }
    let mut total = 0;
    while total < buffer.len() {
        match file.read(&mut buffer[total..]) {
            Ok(0) | Err(_) => break,
            Ok(read) => total += read,
        }
    }
    total
}

/// `RecordFiles.ForEachLine`: visits the complete lines of the first `limit` bytes in order until `visit` returns false. True when
/// every line of the file was visited; a line still being written is never visited.
pub fn for_each_line(file: &mut File, size: i64, limit: i64, mut visit: impl FnMut(&[u8]) -> bool) -> bool {
    const BLOCK: usize = 64 * 1_024;
    let total = size.min(limit.max(0));
    let mut pending: Vec<u8> = vec![];
    let mut offset: i64 = 0;
    let mut chunk = vec![0u8; BLOCK];
    while offset < total {
        let wanted = (BLOCK as i64).min(total - offset) as usize;
        let read = read_at(file, &mut chunk[..wanted], offset);
        if read == 0 {
            return false;
        }
        offset += read as i64;
        let mut start = 0;
        for i in 0..read {
            if chunk[i] != b'\n' {
                continue;
            }
            if !pending.is_empty() {
                pending.extend_from_slice(&chunk[start..i]);
                let line = std::mem::take(&mut pending);
                if !visit(&line) {
                    return false;
                }
            } else if i > start && !visit(&chunk[start..i]) {
                return false;
            }
            start = i + 1;
        }
        if start < read {
            pending.extend_from_slice(&chunk[start..read]);
        }
    }
    offset == size && pending.is_empty()
}

/// `RecordFiles.TailValue`: the newest value `pick` finds in the record's last lines (the last 64 KiB, then 256 KiB).
pub fn tail_value(file: &mut File, size: i64, pick: impl Fn(&[u8]) -> Option<String>) -> Option<String> {
    for window in [64 * 1_024i64, 256 * 1_024] {
        let start = (size - window).max(0);
        let mut data = vec![0u8; (size - start) as usize];
        if read_at(file, &mut data, start) != data.len() {
            return None;
        }
        let mut lines: Vec<&[u8]> = vec![];
        let (mut from, mut first) = (0, true);
        for i in 0..data.len() {
            if data[i] != b'\n' {
                continue;
            }
            // The window's first piece is the end of a longer line.
            if i > from && !(first && start > 0) {
                lines.push(&data[from..i]);
            }
            first = false;
            from = i + 1;
        }
        for line in lines.iter().rev() {
            if let Some(value) = pick(line) {
                return Some(value);
            }
        }
        if start == 0 {
            break;
        }
    }
    None
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn rollout_names_carry_their_thread() {
        assert_eq!(
            thread("rollout-2026-10-01T10-00-00-2d9e2c1a-1111-4222-8333-444455556666.jsonl").as_deref(),
            Some("2d9e2c1a-1111-4222-8333-444455556666")
        );
        assert_eq!(thread("rollout-2026-10-01T10-00-00-2d9e2c1a.jsonl"), None);
        assert_eq!(thread("rollout-2026-10-01T10-00-00-2d9e2c1a-1111-4222-8333-444455556666.jsonl.bak"), None);
    }
}
