//! What the work map reads of the file originals (C# `FileOriginalStore.Paths`, FileChanges.cs:285): the paths a run's manifest
//! (`file-originals/<hash of the pane>/<hash of the run>/run.json`) records as edited, kept or refused. Read-only; the store itself (what
//! a run's edits capture) is the files pane's.

use std::path::{Path, PathBuf};

use mighty_core::wire;
use serde_json::Value;
use sha2::{Digest, Sha256};

/// `FileOriginalStore.Hash`: the first 32 hex digits of the text's SHA-256.
fn hash(value: &str) -> String {
    Sha256::digest(value.as_bytes()).iter().take(16).map(|b| format!("{b:02x}")).collect()
}

/// `FileOriginalStore.SourceRun`: a background notice run's id stands for the run it came from.
pub fn source_run(run_id: &str) -> &str {
    match run_id.find(mighty_graph::notice::MARKER) {
        Some(at) if at > 0 => &run_id[..at],
        _ => run_id,
    }
}

fn same_path(a: &str, b: &str) -> bool {
    if mighty_run::os::windows() { mighty_core::normalize::ordinal_ignore_case(a, b).is_eq() } else { a == b }
}

/// The file originals of one profile (`<data>/file-originals`).
#[derive(Debug, Clone)]
pub struct FileOriginals {
    directory: PathBuf,
}

impl FileOriginals {
    pub fn new(directory: PathBuf) -> FileOriginals {
        FileOriginals { directory }
    }

    /// `Paths`: the paths (relative to the run's folder, '/') the run's manifest records as edited, kept or refused; empty when the run is
    /// unknown or was recorded under another root than `root`.
    pub fn paths(&self, session_id: &str, run_id: &str, root: &str) -> Vec<String> {
        let run_id = source_run(run_id);
        if !wire::identifier(session_id) || !wire::identifier(run_id) {
            return vec![];
        }
        let Some(real_root) = mighty_core::workspace_files::real_path(Path::new(root)).map(|p| p.to_string_lossy().into_owned()) else {
            return vec![];
        };
        let manifest = self.directory.join(hash(session_id)).join(hash(run_id)).join("run.json");
        let Ok(bytes) = std::fs::read(manifest) else { return vec![] };
        let Ok(value) = serde_json::from_slice::<Value>(&bytes) else { return vec![] };
        let text = |name: &str| value.get(name).and_then(Value::as_str);
        let valid = value.get("version").and_then(Value::as_i64) == Some(1)
            && value.get("files").is_some_and(Value::is_array)
            && text("sessionId").is_some_and(wire::identifier)
            && text("runId").is_some_and(wire::identifier);
        if !valid || text("sessionId") != Some(session_id) || text("runId") != Some(run_id) {
            return vec![];
        }
        if !text("root").is_some_and(|kept| same_path(kept, &real_root)) {
            return vec![];
        }
        let files = value["files"].as_array().into_iter().flatten().filter_map(|f| f.get("path").and_then(Value::as_str));
        let refused = value.get("refused").and_then(Value::as_array).into_iter().flatten().filter_map(Value::as_str);
        let mut paths: Vec<String> = vec![];
        for path in files.chain(refused) {
            if !paths.iter().any(|known| same_path(known, path)) {
                paths.push(path.to_string());
            }
        }
        paths
    }
}

/// The run folder a manifest of (`session_id`, `run_id`) lies in, for the checks that write one as the store does.
pub fn run_folder(directory: &Path, session_id: &str, run_id: &str) -> PathBuf {
    directory.join(hash(session_id)).join(hash(run_id))
}
