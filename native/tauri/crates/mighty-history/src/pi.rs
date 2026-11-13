//! Where pi (`@mariozechner/pi-coding-agent`) keeps its session records (C# `PiAgent`, PiSupport.cs:10-80): `<agent dir>/sessions/
//! --<path>--/<timestamp>_<id>.jsonl`. Read-only.

use std::collections::{HashMap, HashSet};
use std::path::{Path, PathBuf};

use mighty_core::{normalize, wire};

use crate::{files, resumable};

fn absolute(path: &str) -> bool {
    path.starts_with('/') || normalize::fully_qualified(path)
}

/// `PiAgent.Directory`: `$PI_CODING_AGENT_DIR` (when absolute), else `~/.pi/agent`.
pub fn directory(home: &Path, environment: &HashMap<String, String>) -> PathBuf {
    match environment.get("PI_CODING_AGENT_DIR").filter(|g| !g.is_empty() && absolute(g)) {
        Some(given) => PathBuf::from(given),
        None => home.join(".pi").join("agent"),
    }
}

/// `PiAgent.SessionFolder`: the folder of `sessions/` a working directory's sessions are kept in, `--<path with / \ : turned into ->--`.
pub fn session_folder(path: &str) -> String {
    let trimmed = path.trim_start_matches(['/', '\\']);
    format!("--{}--", trimmed.chars().map(|c| if matches!(c, '/' | '\\' | ':') { '-' } else { c }).collect::<String>())
}

/// `PiAgent.SessionFolders`: `$PI_CODING_AGENT_SESSION_DIR` as it is, else one folder per path under `<agent dir>/sessions`.
pub fn session_folders(workspace_paths: &[String], home: &Path, environment: &HashMap<String, String>) -> Vec<PathBuf> {
    if let Some(given) = environment.get("PI_CODING_AGENT_SESSION_DIR").filter(|g| !g.is_empty() && absolute(g)) {
        return vec![PathBuf::from(given)];
    }
    let sessions = directory(home, environment).join("sessions");
    let mut paths: Vec<&String> = workspace_paths.iter().collect();
    paths.sort_by(|a, b| normalize::ordinal(a, b));
    let mut seen = HashSet::new();
    paths.into_iter().map(|p| session_folder(p)).filter(|name| seen.insert(name.clone())).map(|name| sessions.join(name)).collect()
}

/// `PiAgent.SessionFile`: the record of session `id`, `<timestamp>_<id>.jsonl` in one of the workspace's folders.
pub fn session_file(id: &str, workspace_path: &str, home: &Path, environment: &HashMap<String, String>) -> Option<PathBuf> {
    if !wire::identifier(id) {
        return None;
    }
    let suffix = format!("_{id}.jsonl");
    for folder in session_folders(&resumable::workspace_paths(workspace_path), home, environment) {
        for (path, _) in files::files(&folder, ".jsonl") {
            if path.file_name().is_some_and(|n| n.to_string_lossy().ends_with(&suffix)) {
                return Some(path);
            }
        }
    }
    None
}

/// `PiAgent.SessionID`: a session's id from its file name (`2024-12-03T14-00-00-000Z_<uuid>.jsonl`).
pub fn session_id(file_name: &str) -> Option<String> {
    let stem = file_name.strip_suffix(".jsonl")?;
    let underscore = stem.find('_')?;
    let id = &stem[underscore + 1..];
    wire::identifier(id).then(|| id.to_string())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn folders_and_ids_read_as_pi_writes_them() {
        assert_eq!(session_folder("/Users/dev/my project"), "--Users-dev-my project--");
        assert_eq!(session_folder(r"C:\Users\dev"), "--C--Users-dev--");
        assert_eq!(
            session_id("2026-11-10T09-30-00-000Z_0197c1d2-7f3a-7c4e-9b1a-5d2e8f6a4b30.jsonl").as_deref(),
            Some("0197c1d2-7f3a-7c4e-9b1a-5d2e8f6a4b30")
        );
        assert_eq!(session_id("notes.txt"), None);
    }
}
