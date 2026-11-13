//! What a request changed in one file (C# FileChanges.cs: `FileOriginalStore`, `GitHeadFile`, `FileChanges`; M/MightyCore/FileChanges.swift):
//! the source preview's 변경사항 view and the web shell's `referenceChanges`. The base is the file as it was before the run first
//! changed it (`run`, kept by [`FileOriginalStore`] from the run's Edit, Write and MultiEdit results) or git's HEAD (`head`, an
//! untracked file read as empty).

use std::collections::{HashMap, HashSet};
use std::io::Read as _;
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex, MutexGuard};
use std::time::Duration;

use mighty_core::model::RunSession;
use mighty_core::{normalize, wire, workspace_files};
use mighty_platform::{FileHandles, ProcessHost, SpawnRequest};
use serde_json::{Map, Value, json};
use sha2::{Digest, Sha256};

use crate::classify::{self, decode_text};
use crate::diff::{self, Hunk};
use crate::patch::{self, FileEdit};
use crate::reference;

/// C# `FileOriginalStore`'s caps.
pub const MAXIMUM_ORIGINAL_BYTES: usize = 1_048_576;
pub const MAXIMUM_FILES_PER_RUN: usize = 64;
pub const MAXIMUM_BYTES_PER_RUN: u64 = 16 * 1_048_576;
pub const MAXIMUM_BYTES: u64 = 512 * 1_048_576;
pub const MAXIMUM_RUNS_PER_PANE: usize = 20;
pub const MAXIMUM_REFUSED_PER_RUN: usize = 1_024;
pub const FOLDER_NAME: &str = "file-originals";

/// Paths compare ignoring case where the OS's file system does (C# `PathComparer`: OrdinalIgnoreCase on Windows).
fn ignores_case() -> bool {
    std::path::MAIN_SEPARATOR == '\\'
}

fn same_path(a: &str, b: &str) -> bool {
    if ignores_case() { normalize::ordinal_ignore_case(a, b).is_eq() } else { a == b }
}

fn lock<T>(mutex: &Mutex<T>) -> MutexGuard<'_, T> {
    mutex.lock().unwrap_or_else(|poisoned| poisoned.into_inner())
}

/// C# `Hash`: the first 32 hex digits of the value's SHA-256, lower case.
fn hash(value: &str) -> String {
    let digest = Sha256::digest(value.as_bytes());
    digest.iter().map(|b| format!("{b:02x}")).collect::<String>()[..32].to_string()
}

/// C# `FileOriginalStore.SourceRun`: a background notice run's id stands for the run it came from.
pub fn source_run(run_id: &str) -> &str {
    match run_id.find(mighty_graph::notice::MARKER) {
        Some(at) if at > 0 => &run_id[..at],
        _ => run_id,
    }
}

/// .NET's `DateTime.UtcNow.Ticks`: 100 ns since 0001-01-01, so a sequence C# wrote and one written here compare.
fn ticks_now() -> i64 {
    let since = std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap_or_default();
    621_355_968_000_000_000 + (since.as_nanos() / 100) as i64
}

/// C# `FileOriginal`: a file's content before a run first changed it; `created` when the run made it (the content is then "").
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct FileOriginal {
    pub run_id: String,
    pub path: String,
    pub text: String,
    pub created: bool,
}

/// C# `FileOriginalStore.Entry`.
#[derive(Debug, Clone, PartialEq, Eq)]
struct Entry {
    path: String,
    blob: String,
    bytes: u64,
    created: bool,
}

/// C# `FileOriginalStore.Manifest` (`run.json`, written with C#'s default options: these names, nulls written).
#[derive(Debug, Clone, PartialEq, Eq)]
struct Manifest {
    session_id: String,
    run_id: String,
    sequence: i64,
    files: Vec<Entry>,
    root: Option<String>,
    refused: Option<Vec<String>>,
}

impl Manifest {
    fn read(folder: &Path) -> Option<Manifest> {
        let bytes = std::fs::read(folder.join("run.json")).ok()?;
        let value: Value = serde_json::from_slice(&bytes).ok()?;
        if value.get("version").and_then(Value::as_i64) != Some(1) {
            return None;
        }
        let text = |key: &str| value.get(key).and_then(Value::as_str).map(str::to_string);
        let session_id = text("sessionId").filter(|id| wire::identifier(id))?;
        let run_id = text("runId").filter(|id| wire::identifier(id))?;
        let files = value
            .get("files")?
            .as_array()?
            .iter()
            .map(|entry| {
                Some(Entry {
                    path: entry.get("path")?.as_str()?.to_string(),
                    blob: entry.get("blob")?.as_str()?.to_string(),
                    bytes: entry.get("bytes").and_then(Value::as_u64).unwrap_or(0),
                    created: entry.get("created").and_then(Value::as_bool).unwrap_or(false),
                })
            })
            .collect::<Option<Vec<_>>>()?;
        let refused = match value.get("refused") {
            None | Some(Value::Null) => None,
            Some(list) => Some(list.as_array()?.iter().map(|p| p.as_str().map(str::to_string)).collect::<Option<Vec<_>>>()?),
        };
        Some(Manifest {
            session_id,
            run_id,
            sequence: value.get("sequence").and_then(Value::as_i64).unwrap_or(0),
            files,
            root: text("root"),
            refused,
        })
    }

    fn to_json(&self) -> Vec<u8> {
        let files: Vec<Value> =
            self.files.iter().map(|f| json!({"path": f.path, "blob": f.blob, "bytes": f.bytes, "created": f.created})).collect();
        let mut out = Map::new();
        out.insert("version".into(), json!(1));
        out.insert("sessionId".into(), json!(self.session_id));
        out.insert("runId".into(), json!(self.run_id));
        out.insert("sequence".into(), json!(self.sequence));
        out.insert("files".into(), Value::Array(files));
        out.insert("root".into(), json!(self.root));
        out.insert("refused".into(), json!(self.refused));
        serde_json::to_vec(&Value::Object(out)).unwrap_or_default()
    }

    fn refuses(&self, relative: &str) -> bool {
        self.refused.as_deref().unwrap_or_default().iter().any(|p| same_path(p, relative))
    }

    fn entry(&self, relative: &str) -> Option<&Entry> {
        self.files.iter().find(|f| same_path(&f.path, relative))
    }

    fn total_bytes(&self) -> u64 {
        self.files.iter().map(|f| f.bytes).sum()
    }
}

/// C# `Indexed`: a run folder's manifest summary.
#[derive(Debug, Clone)]
struct Indexed {
    session_id: String,
    run_id: String,
    sequence: i64,
    bytes: u64,
    files: usize,
}

impl Indexed {
    fn of(manifest: &Manifest) -> Indexed {
        Indexed {
            session_id: manifest.session_id.clone(),
            run_id: manifest.run_id.clone(),
            sequence: manifest.sequence,
            bytes: manifest.total_bytes(),
            files: manifest.files.len(),
        }
    }
}

type Key = (String, String, String);

#[derive(Default)]
struct Inner {
    /// Paths of a run (session, run, path) already settled — kept or refused — in this process.
    settled: HashSet<Key>,
    /// For an original rebuilt from a patch: the hash of the file as the run's last confirmed edit of it left it.
    following: HashMap<Key, String>,
    /// Every run folder's manifest summary, read once from disk and kept up to date.
    index: Option<HashMap<PathBuf, Indexed>>,
    last_sequence: i64,
}

/// C# `FileOriginalStore`: the contents files had before a Claude run first changed them, from the run's Edit, Write and MultiEdit
/// results, kept apart from the state under `<data>/file-originals/<sha(session)>/<sha(run)>/`: `run.json` and one `<n>.txt` per file
/// (UTF-8), each written to a temporary name and moved over.
///
/// A path is the real path relative to the run's folder (its `root`, recorded: an original is never read under another root). Only a
/// run's first edit of a path counts: an original that is unknown, over 1 MiB, or past the run's 64 files or 16 MiB is recorded as
/// refused, and the path's later edits in that run never pose as its first. An original rebuilt from a patch holds only while each later
/// edit of the path in the run confirms it (the CLI may write the next edit before this one's result is read). Each pane keeps the
/// originals of its newest 20 runs, and the whole store at most 512 MiB, the oldest runs going first.
pub struct FileOriginalStore {
    directory: PathBuf,
    budget: u64,
    handles: Arc<dyn FileHandles>,
    inner: Mutex<Inner>,
}

impl FileOriginalStore {
    /// A store under `directory` (`<data>/file-originals`) with the whole store's `budget` ([`MAXIMUM_BYTES`]; checks pass a smaller
    /// one); the files edited are read through `handles`.
    pub fn new(directory: impl Into<PathBuf>, budget: u64, handles: Arc<dyn FileHandles>) -> FileOriginalStore {
        FileOriginalStore { directory: directory.into(), budget, handles, inner: Mutex::new(Inner::default()) }
    }

    pub fn directory(&self) -> &Path {
        &self.directory
    }

    fn session_folder(&self, session_id: &str) -> PathBuf {
        self.directory.join(hash(session_id))
    }

    fn run_folder(&self, session_id: &str, run_id: &str) -> PathBuf {
        self.session_folder(session_id).join(hash(run_id))
    }

    fn key(session_id: &str, run_id: &str, path: &str) -> Key {
        (session_id.into(), run_id.into(), if ignores_case() { path.to_lowercase() } else { path.into() })
    }

    /// Under the lock: the run folders' summaries, read from disk the first time.
    fn index<'a>(&self, inner: &'a mut Inner) -> &'a mut HashMap<PathBuf, Indexed> {
        if inner.index.is_none() {
            let mut index = HashMap::new();
            let mut last = inner.last_sequence;
            for session in folders(&self.directory) {
                for run in folders(&session) {
                    if let Some(manifest) = Manifest::read(&run) {
                        last = last.max(manifest.sequence);
                        index.insert(run, Indexed::of(&manifest));
                    }
                }
            }
            inner.last_sequence = last;
            inner.index = Some(index);
        }
        inner.index.as_mut().expect("the index was read")
    }

    /// C# `NextSequence`: later than every run's so far, also across restarts (the clock's ticks, never going back).
    fn next_sequence(&self, inner: &mut Inner) -> i64 {
        self.index(inner);
        inner.last_sequence = (inner.last_sequence + 1).max(ticks_now());
        inner.last_sequence
    }

    fn save(&self, inner: &mut Inner, folder: &Path, manifest: &Manifest) -> std::io::Result<()> {
        write_atomically(&folder.join("run.json"), &manifest.to_json())?;
        self.index(inner).insert(folder.to_path_buf(), Indexed::of(manifest));
        Ok(())
    }

    /// The file as it is now, through the files pane's open (C# `FileChanges.Current`): None when over 1 MiB, binary or undecodable.
    pub fn current(&self, relative: &str, root: &Path) -> Option<String> {
        current(relative, root, self.handles.as_ref())
    }

    /// C# `Capture`: what an edit of the run says about `file_path` before it. The path may be absolute or relative to
    /// `workspace_root`; one that does not resolve to an existing file inside it is ignored. A patch is undone against the file as it
    /// is now. True when an original was kept; an edit whose original cannot be kept settles the path as refused.
    pub fn capture(&self, session_id: &str, run_id: &str, workspace_root: &str, file_path: &str, edit: &FileEdit) -> bool {
        if !wire::identifier(session_id) || !wire::identifier(run_id) {
            return false;
        }
        let Some(target) = reference::resolve(file_path, workspace_root, None) else { return false };
        let Some(real_root) = workspace_files::real_path(Path::new(workspace_root)) else { return false };
        let relative = target.relative_path;
        let real_root_text = real_root.to_string_lossy().into_owned();
        let mut inner = lock(&self.inner);
        let key = Self::key(session_id, run_id, &relative);
        if !inner.settled.insert(key.clone()) {
            if let Some(expected) = inner.following.get(&key).cloned() {
                match self.follow(&expected, edit, &relative, &real_root) {
                    Some(next) => {
                        inner.following.insert(key, next);
                    }
                    None => {
                        inner.following.remove(&key);
                        self.withdraw(&mut inner, session_id, run_id, &relative);
                    }
                }
            }
            return false;
        }
        let folder = self.run_folder(session_id, run_id);
        let manifest = match Manifest::read(&folder) {
            Some(manifest) => manifest,
            None => Manifest {
                session_id: session_id.into(),
                run_id: run_id.into(),
                sequence: self.next_sequence(&mut inner),
                files: vec![],
                root: Some(real_root_text.clone()),
                refused: Some(vec![]),
            },
        };
        if manifest.entry(&relative).is_some() || manifest.refuses(&relative) {
            return false;
        }
        if !manifest.root.as_deref().is_some_and(|root| same_path(root, &real_root_text)) {
            return false;
        }
        // The files are read without a byte order mark, so an original is kept without one too.
        let (text, after) = if let Some(given) = &edit.original {
            (Some(patch::without_byte_order_mark(given).to_string()), None)
        } else if edit.created {
            (Some(String::new()), None)
        } else if let Some(hunks) = &edit.patch
            && let Some(current) = self.current(&relative, &real_root)
            && let Some(before) = patch::reverse_apply(&current, hunks)
        {
            (Some(before), Some(hash(&current)))
        } else {
            (None, None)
        };
        let outcome = (|| -> std::io::Result<bool> {
            std::fs::create_dir_all(&folder)?;
            let too_much = |bytes: usize| {
                bytes > MAXIMUM_ORIGINAL_BYTES
                    || manifest.files.len() >= MAXIMUM_FILES_PER_RUN
                    || manifest.total_bytes() + bytes as u64 > MAXIMUM_BYTES_PER_RUN
            };
            let Some(text) = text.filter(|t| !too_much(t.len())) else {
                let refused = manifest.refused.clone().unwrap_or_default();
                if refused.len() < MAXIMUM_REFUSED_PER_RUN {
                    let mut kept = refused;
                    kept.push(relative.clone());
                    self.save(&mut inner, &folder, &Manifest { refused: Some(kept), ..manifest.clone() })?;
                }
                return Ok(false);
            };
            // A withdrawn original leaves a gap in the numbers: the next blob takes the first free one.
            let mut number = manifest.files.len();
            while manifest.files.iter().any(|f| f.blob == blob(number)) {
                number += 1;
            }
            let name = blob(number);
            write_atomically(&folder.join(&name), text.as_bytes())?;
            let mut files = manifest.files.clone();
            files.push(Entry {
                path: relative.clone(),
                blob: name,
                bytes: text.len() as u64,
                created: edit.original.is_none() && edit.created,
            });
            self.save(&mut inner, &folder, &Manifest { files, ..manifest.clone() })?;
            if let Some(after) = after {
                inner.following.insert(key.clone(), after);
            }
            self.evict(&mut inner, Some(&folder));
            Ok(true)
        })();
        outcome.unwrap_or(false)
    }

    /// C# `Follow`: the hash of the file after a later edit of a path whose original was rebuilt from a patch, when the edit confirms
    /// the file the run's last edit left (`expected`); None when it does not or cannot.
    fn follow(&self, expected: &str, edit: &FileEdit, relative: &str, real_root: &Path) -> Option<String> {
        let hunks = edit.patch.as_ref()?;
        if let Some(given) = &edit.original {
            let before = patch::without_byte_order_mark(given);
            return (hash(before) == expected)
                .then(|| patch::reverse_apply(before, &patch::inverted(hunks)).map(|then| hash(&then)))
                .flatten();
        }
        let current = self.current(relative, real_root)?;
        let undone = patch::reverse_apply(&current, hunks)?;
        (hash(&undone) == expected).then(|| hash(&current))
    }

    /// C# `Withdraw`: drops a kept original of the run, settling its path as refused.
    fn withdraw(&self, inner: &mut Inner, session_id: &str, run_id: &str, relative: &str) {
        let folder = self.run_folder(session_id, run_id);
        let Some(manifest) = Manifest::read(&folder) else { return };
        let Some(entry) = manifest.entry(relative).cloned() else { return };
        if safe_blob(&entry.blob) {
            let _ = std::fs::remove_file(folder.join(&entry.blob));
        }
        let mut refused = manifest.refused.clone().unwrap_or_default();
        if refused.len() < MAXIMUM_REFUSED_PER_RUN {
            refused.push(relative.into());
        }
        let files = manifest.files.iter().filter(|f| **f != entry).cloned().collect();
        let _ = self.save(inner, &folder, &Manifest { files, refused: Some(refused), ..manifest });
    }

    /// C# `Evict`: each pane keeps the originals of its newest [`MAXIMUM_RUNS_PER_PANE`] runs and the store at most its budget; the
    /// oldest runs go first, never `keep` (the run just written).
    fn evict(&self, inner: &mut Inner, keep: Option<&Path>) {
        let kept = |folder: &Path| keep.is_some_and(|k| same_path(&k.to_string_lossy(), &folder.to_string_lossy()));
        let mut panes: HashMap<String, Vec<(PathBuf, i64)>> = HashMap::new();
        for (folder, value) in self.index(inner).iter().filter(|(_, v)| v.files > 0) {
            panes.entry(value.session_id.clone()).or_default().push((folder.clone(), value.sequence));
        }
        for (_, mut runs) in panes {
            runs.sort_by_key(|a| std::cmp::Reverse(a.1));
            for (folder, _) in runs.into_iter().skip(MAXIMUM_RUNS_PER_PANE).filter(|(f, _)| !kept(f)) {
                self.drop_folder(inner, &folder);
            }
        }
        let mut all: Vec<(PathBuf, i64, u64)> = self.index(inner).iter().map(|(f, v)| (f.clone(), v.sequence, v.bytes)).collect();
        let mut total: u64 = all.iter().map(|a| a.2).sum();
        all.sort_by_key(|a| a.1);
        for (folder, _, bytes) in all.into_iter().filter(|(f, _, _)| !kept(f)) {
            if total <= self.budget {
                break;
            }
            total -= bytes;
            self.drop_folder(inner, &folder);
        }
    }

    /// C# `Drop`: one run folder removed, with its summary and what this process settled for it.
    fn drop_folder(&self, inner: &mut Inner, folder: &Path) {
        if let Some(gone) = self.index(inner).remove(folder) {
            inner.settled.retain(|k| !(k.0 == gone.session_id && k.1 == gone.run_id));
            inner.following.retain(|k, _| !(k.0 == gone.session_id && k.1 == gone.run_id));
        }
        delete(folder);
    }

    /// C# `Find`: the original of `relative` that `run_id` (a notice run: the run it came from) captured, or with no run the one of
    /// the session's newest run that edited the path; None when that run refused it, it was captured under another root than `root`
    /// (when given), or there is none.
    pub fn find(&self, session_id: &str, run_id: Option<&str>, relative: &str, root: Option<&str>) -> Option<FileOriginal> {
        let run_id = run_id.map(source_run);
        if !wire::identifier(session_id) || run_id.is_some_and(|r| !wire::identifier(r)) {
            return None;
        }
        let real_root = match root {
            Some(root) => Some(workspace_files::real_path(Path::new(root))?.to_string_lossy().into_owned()),
            None => None,
        };
        let mut inner = lock(&self.inner);
        let folders: Vec<PathBuf> = match run_id {
            Some(run) => vec![self.run_folder(session_id, run)],
            None => {
                let mut mine: Vec<(PathBuf, i64)> = self
                    .index(&mut inner)
                    .iter()
                    .filter(|(_, v)| v.session_id == session_id)
                    .map(|(f, v)| (f.clone(), v.sequence))
                    .collect();
                mine.sort_by_key(|a| std::cmp::Reverse(a.1));
                mine.into_iter().map(|(f, _)| f).collect()
            }
        };
        for folder in folders {
            let Some(manifest) = Manifest::read(&folder) else { continue };
            if manifest.session_id != session_id || run_id.is_some_and(|r| manifest.run_id != r) {
                continue;
            }
            if manifest.refuses(relative) {
                return None;
            }
            let Some(entry) = manifest.entry(relative) else { continue };
            if let Some(real_root) = &real_root
                && !manifest.root.as_deref().is_some_and(|kept| same_path(kept, real_root))
            {
                return None;
            }
            if !safe_blob(&entry.blob) {
                return None;
            }
            let path = folder.join(&entry.blob);
            if std::fs::metadata(&path).ok()?.len() > MAXIMUM_ORIGINAL_BYTES as u64 {
                return None;
            }
            let bytes = std::fs::read(&path).ok()?;
            return Some(FileOriginal {
                run_id: manifest.run_id.clone(),
                path: entry.path.clone(),
                text: String::from_utf8_lossy(&bytes).into_owned(),
                created: entry.created,
            });
        }
        None
    }

    /// C# `Paths`: the paths (relative to the run's folder, '/') the run's manifest records as edited, kept or refused; empty when the
    /// run is unknown or was recorded under another root than `root`.
    pub fn paths(&self, session_id: &str, run_id: &str, root: &str) -> Vec<String> {
        let run_id = source_run(run_id);
        let Some(real_root) = workspace_files::real_path(Path::new(root)) else { return vec![] };
        if !wire::identifier(session_id) || !wire::identifier(run_id) {
            return vec![];
        }
        let _guard = lock(&self.inner);
        let Some(manifest) = Manifest::read(&self.run_folder(session_id, run_id)) else { return vec![] };
        if manifest.session_id != session_id
            || manifest.run_id != run_id
            || !manifest.root.as_deref().is_some_and(|kept| same_path(kept, &real_root.to_string_lossy()))
        {
            return vec![];
        }
        let mut out: Vec<String> = Vec::new();
        for path in manifest.files.iter().map(|f| &f.path).chain(manifest.refused.iter().flatten()) {
            if !out.iter().any(|p| same_path(p, path)) {
                out.push(path.clone());
            }
        }
        out
    }

    /// C# `Runs`: the run ids the store holds for a session, newest first.
    pub fn runs(&self, session_id: &str) -> Vec<String> {
        if !wire::identifier(session_id) {
            return vec![];
        }
        let mut inner = lock(&self.inner);
        let mut runs: Vec<(String, i64)> =
            self.index(&mut inner).values().filter(|v| v.session_id == session_id).map(|v| (v.run_id.clone(), v.sequence)).collect();
        runs.sort_by_key(|a| std::cmp::Reverse(a.1));
        runs.into_iter().map(|(r, _)| r).collect()
    }

    /// C# `Retain`: drops the session's runs that are not in `keep` (a notice run keeps the run it came from).
    pub fn retain(&self, session_id: &str, keep: &HashSet<String>) {
        if !wire::identifier(session_id) {
            return;
        }
        let kept: HashSet<&str> = keep.iter().map(|r| source_run(r)).collect();
        let mut inner = lock(&self.inner);
        let gone: Vec<PathBuf> = self
            .index(&mut inner)
            .iter()
            .filter(|(_, v)| v.session_id == session_id && !kept.contains(v.run_id.as_str()))
            .map(|(f, _)| f.clone())
            .collect();
        for folder in gone {
            self.drop_folder(&mut inner, &folder);
        }
        // A folder with no readable manifest is no run.
        for run in folders(&self.session_folder(session_id)) {
            if !self.index(&mut inner).contains_key(&run) {
                delete(&run);
            }
        }
    }

    /// C# `Forget`: everything a pane's runs captured goes.
    pub fn forget(&self, session_id: &str) {
        if !wire::identifier(session_id) {
            return;
        }
        let mut inner = lock(&self.inner);
        self.index(&mut inner).retain(|_, v| v.session_id != session_id);
        delete(&self.session_folder(session_id));
        inner.settled.retain(|k| k.0 != session_id);
        inner.following.retain(|k, _| k.0 != session_id);
    }

    /// C# `Prune`: keeps only the sessions named, each with only the runs named for it (a notice run keeps the run it came from);
    /// everything else in the folder goes, temporary files left by a crash included, and then the budget holds.
    pub fn prune(&self, sessions: &HashMap<String, HashSet<String>>) {
        let mut inner = lock(&self.inner);
        if !self.directory.is_dir() {
            return;
        }
        let known: HashMap<String, &String> = sessions.keys().filter(|id| wire::identifier(id)).map(|id| (hash(id), id)).collect();
        for folder in folders(&self.directory) {
            let name = folder.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default();
            let Some(id) = known.get(&name) else {
                delete(&folder);
                continue;
            };
            let runs: HashSet<&str> = sessions[*id].iter().map(|r| source_run(r)).collect();
            for run in folders(&folder) {
                let keep = Manifest::read(&run).is_some_and(|m| m.session_id == **id && runs.contains(m.run_id.as_str()));
                if !keep {
                    delete(&run);
                }
            }
        }
        // A file that cannot go (open elsewhere, read-only) leaves the rest to go.
        let mut stale: Vec<PathBuf> = std::fs::read_dir(&self.directory)
            .map(|items| items.flatten().map(|i| i.path()).filter(|p| p.is_file()).collect())
            .unwrap_or_default();
        temporary_files(&self.directory, &mut stale);
        for file in stale {
            let _ = std::fs::remove_file(file);
        }
        inner.index = None;
        inner.settled.clear();
        inner.following.clear();
        self.evict(&mut inner, None);
    }

    /// C# `KeptRuns(session)`: the runs a pane's graph keeps, its runs' ids and the ids they were made from.
    pub fn kept_runs(session: &RunSession) -> HashSet<String> {
        session
            .graph_runs()
            .iter()
            .flat_map(|r| [Some(r.id().to_string()), r.source_run_id.clone()])
            .flatten()
            .filter(|id| wire::identifier(id))
            .collect()
    }

    /// C# `KeptRuns(sessions)`: the kept runs of every pane; a pane id that appears twice keeps the runs of both.
    pub fn kept_runs_of<'a>(sessions: impl IntoIterator<Item = &'a RunSession>) -> HashMap<String, HashSet<String>> {
        let mut kept: HashMap<String, HashSet<String>> = HashMap::new();
        for session in sessions {
            kept.entry(session.id().to_string()).or_default().extend(Self::kept_runs(session));
        }
        kept
    }
}

fn blob(number: usize) -> String {
    format!("{number}.txt")
}

fn safe_blob(blob: &str) -> bool {
    !blob.contains('/') && !blob.contains('\\') && !blob.contains("..")
}

/// The folders directly in `folder` (none when it cannot be read).
fn folders(folder: &Path) -> Vec<PathBuf> {
    std::fs::read_dir(folder).map(|items| items.flatten().map(|i| i.path()).filter(|p| p.is_dir()).collect()).unwrap_or_default()
}

fn temporary_files(folder: &Path, out: &mut Vec<PathBuf>) {
    for item in std::fs::read_dir(folder).into_iter().flatten().flatten() {
        let path = item.path();
        if path.is_dir() {
            temporary_files(&path, out);
        } else if path.extension().is_some_and(|e| e.eq_ignore_ascii_case("tmp")) {
            out.push(path);
        }
    }
}

fn delete(folder: &Path) {
    if folder.is_dir() {
        let _ = std::fs::remove_dir_all(folder);
    }
}

/// C# `WriteAtomically`: to a temporary name beside it, then moved over.
fn write_atomically(path: &Path, data: &[u8]) -> std::io::Result<()> {
    let temporary = PathBuf::from(format!("{}.{}.tmp", path.display(), uuid::Uuid::new_v4().simple()));
    let written = std::fs::write(&temporary, data).and_then(|_| std::fs::rename(&temporary, path));
    if temporary.exists() {
        let _ = std::fs::remove_file(&temporary);
    }
    written
}

/// C# `FileChanges.Current`: the file as it is now, through the files pane's open: None when over 1 MiB, binary or undecodable.
pub fn current(relative: &str, root: &Path, handles: &dyn FileHandles) -> Option<String> {
    let opened = crate::open_file(relative, root, handles).ok()?;
    if opened.size > diff::MAXIMUM_SIDE_BYTES as u64 {
        return None;
    }
    let mut data = Vec::new();
    opened.file.take(diff::MAXIMUM_SIDE_BYTES as u64 + 1).read_to_end(&mut data).ok()?;
    if data.len() > diff::MAXIMUM_SIDE_BYTES {
        return None;
    }
    decode_text(&data, false).map(|d| d.text)
}

// ---- git's HEAD ------------------------------------------------------------------------------------------------------------

/// C# `GitHeadFile.Content`: `untracked` when HEAD has no such file (or there is no commit yet), so everything reads as added.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct HeadContent {
    pub data: Vec<u8>,
    pub short_sha: Option<String>,
    pub untracked: bool,
}

/// C# `GitHeadFile.NotInHead`: git's own words (LC_ALL=C) for a path HEAD does not have; any other failure is no base at all.
pub fn not_in_head(error: &str) -> bool {
    error.contains("does not exist in 'HEAD'") || error.contains("exists on disk, but not in 'HEAD'")
}

/// One git command's exit code, its first `limit` bytes (and whether there were more) and the start of its stderr; None when it did
/// not finish in 2 s or did not start.
async fn run_git(
    processes: &dyn ProcessHost,
    binary: &Path,
    root: &str,
    arguments: &[&str],
    limit: usize,
) -> Option<(i32, Vec<u8>, bool, String)> {
    use tokio::io::AsyncReadExt;
    let hooks = if ignores_case() { "core.hooksPath=NUL" } else { "core.hooksPath=/dev/null" };
    let mut all = vec!["--no-optional-locks", "-c", "core.fsmonitor=false", "-c", hooks, "-c", "gc.auto=0", "-C", root];
    all.extend_from_slice(arguments);
    let mut request = SpawnRequest::new(binary.to_path_buf(), all, root);
    for (key, _) in std::env::vars_os() {
        let key = key.to_string_lossy().into_owned();
        if key.to_ascii_uppercase().starts_with("GIT_") {
            request = request.without(key);
        }
    }
    let request = request.env("GIT_OPTIONAL_LOCKS", "0").env("GIT_TERMINAL_PROMPT", "0").env("LC_ALL", "C");
    let mut child = processes.spawn(request).await.ok()?;
    drop(child.stdin.take());
    let control = child.control();
    let (stdout, stderr) = (child.stdout.take(), child.stderr.take());
    async fn bounded(reader: Option<mighty_platform::process::ChildReader>, keep: usize) -> (Vec<u8>, bool) {
        let mut kept = Vec::new();
        let mut over = false;
        let Some(mut reader) = reader else { return (kept, over) };
        // On the heap: the futures that hold it are moved about, and a debug build's stack is small.
        let mut buffer = vec![0u8; 65_536];
        while let Ok(read) = reader.read(&mut buffer).await {
            if read == 0 {
                break;
            }
            let room = keep.saturating_sub(kept.len());
            if read > room {
                over = true;
            }
            kept.extend_from_slice(&buffer[..read.min(room)]);
        }
        (kept, over)
    }
    let waiting = control.clone();
    let outcome = tokio::time::timeout(Duration::from_secs(2), async move {
        let (exit, output, error) = tokio::join!(waiting.wait(), bounded(stdout, limit), bounded(stderr, 65_536));
        (exit, output, error)
    })
    .await;
    control.kill();
    let _ = control.close().await;
    let (exit, (output, over), (error, _)) = outcome.ok()?;
    Some((exit.ok()?, output, over, String::from_utf8_lossy(&error).into_owned()))
}

/// C# `GitHeadFile.ReadAsync`: HEAD's copy of `relative` (relative to the real `root`, '/' separated), read as `WorkspaceGitInfo`
/// reads status (no hooks, no fsmonitor, no optional locks, no GIT_* from the app's environment, output bounded, 2 s per command;
/// only `rev-parse` and `cat-file blob HEAD:./path`, which applies no filters). None when the folder is in no repository, git is
/// missing or fails for any reason but the path not being in HEAD, or the copy is over `maximum_bytes`.
pub async fn git_head(processes: &dyn ProcessHost, root: &str, relative: &str, maximum_bytes: usize) -> Option<HeadContent> {
    if !normalize::fully_qualified(root)
        || !Path::new(root).is_dir()
        || relative.is_empty()
        || relative.contains('\0')
        || relative.contains('\\')
        || relative.split('/').any(|c| matches!(c, "" | "." | ".."))
    {
        return None;
    }
    let name = if ignores_case() { "git.exe" } else { "git" };
    let path = std::env::var_os("PATH")?;
    let binary =
        std::env::split_paths(&path).map(|p| PathBuf::from(p.to_string_lossy().trim_matches('"')).join(name)).find(|p| p.is_file())?;
    let (exit, inside, _, _) = run_git(processes, &binary, root, &["rev-parse", "--is-inside-work-tree"], 64).await?;
    if exit != 0 || String::from_utf8_lossy(&inside).trim() != "true" {
        return None;
    }
    let (exit, head, _, _) = run_git(processes, &binary, root, &["rev-parse", "-q", "--verify", "HEAD^{commit}"], 256).await?;
    let sha = String::from_utf8_lossy(&head).trim().to_string();
    // A repository with no commit yet: nothing is in HEAD.
    if exit != 0 || !(40..=64).contains(&sha.len()) || !sha.bytes().all(|b| b.is_ascii_digit() || (b'a'..=b'f').contains(&b)) {
        return Some(HeadContent { data: vec![], short_sha: None, untracked: true });
    }
    let short = sha[..7].to_string();
    let spec = format!("HEAD:./{relative}");
    let (exit, data, over, error) = run_git(processes, &binary, root, &["cat-file", "blob", &spec], maximum_bytes).await?;
    if over {
        return None;
    }
    if exit == 0 {
        return Some(HeadContent { data, short_sha: Some(short), untracked: false });
    }
    not_in_head(&error).then_some(HeadContent { data: vec![], short_sha: Some(short), untracked: true })
}

// ---- the change set --------------------------------------------------------------------------------------------------------

/// C# `FileChangeSet`: `base` is `run` or `head`; `run_id` names the run of a `run` base, `head_sha` the short commit of a `head` base
/// (None for a repository with no commit yet), `created` a file the run made or git does not know.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ChangeSet {
    pub base: &'static str,
    pub hunks: Vec<Hunk>,
    pub added: usize,
    pub removed: usize,
    pub truncated: bool,
    pub unchanged: bool,
    pub run_id: Option<String>,
    pub head_sha: Option<String>,
    pub created: Option<bool>,
}

impl ChangeSet {
    /// As `Wire.Json` writes it (camelCase, nulls left out).
    pub fn to_json(&self) -> Value {
        let mut out = Map::new();
        out.insert("base".into(), json!(self.base));
        out.insert("hunks".into(), Value::Array(self.hunks.iter().map(Hunk::to_json).collect()));
        out.insert("added".into(), json!(self.added));
        out.insert("removed".into(), json!(self.removed));
        out.insert("truncated".into(), json!(self.truncated));
        out.insert("unchanged".into(), json!(self.unchanged));
        if let Some(run) = &self.run_id {
            out.insert("runId".into(), json!(run));
        }
        if let Some(sha) = &self.head_sha {
            out.insert("headSha".into(), json!(sha));
        }
        if let Some(created) = self.created {
            out.insert("created".into(), json!(created));
        }
        Value::Object(out)
    }
}

/// C# `FileChanges.OnDiskCase`: `relative` with each component as the disk spells it (git compares paths by case, Windows does not, so
/// a link written `src/app.cs` still finds HEAD's `Src/App.cs`). Elsewhere the path is already the disk's.
pub fn on_disk_case(real_root: &Path, relative: &str) -> String {
    if !ignores_case() {
        return relative.to_string();
    }
    let mut current = real_root.to_path_buf();
    let mut parts = Vec::new();
    for part in relative.split('/') {
        let found = std::fs::read_dir(&current).ok().and_then(|items| {
            items.flatten().map(|i| i.file_name().to_string_lossy().into_owned()).find(|n| normalize::ordinal_ignore_case(n, part).is_eq())
        });
        let name = found.unwrap_or_else(|| part.to_string());
        current.push(&name);
        parts.push(name);
    }
    parts.join("/")
}

/// C# `FileChanges.ReadAsync`: the change set of `relative` under `workspace_root` (for a delegated child, its worktree folder); None
/// when there is no diff to show (the path leaves the folder or is no regular file, a side is binary, undecodable or over the caps, or
/// there is no run original and no git base). With `run_id` (a notice run: the run it came from) only that run's original counts;
/// without it the newest run of `session_id` that edited the file. An original captured under another folder never counts.
pub async fn read(
    store: Option<&FileOriginalStore>,
    processes: &dyn ProcessHost,
    handles: &dyn FileHandles,
    workspace_root: &str,
    relative: &str,
    session_id: Option<&str>,
    run_id: Option<&str>,
) -> Option<ChangeSet> {
    let root = Path::new(workspace_root);
    let real = workspace_files::resolve(relative, root)?;
    let real_root = workspace_files::real_path(root)?;
    if real.is_dir() {
        return None;
    }
    let disk_relative = on_disk_case(&real_root, &workspace_files::relative(&real, &real_root)?.replace('\\', "/"));
    let now = current(relative, root, handles)?;
    if let (Some(store), Some(session)) = (store, session_id)
        && let Some(original) = store.find(session, run_id, &disk_relative, Some(workspace_root))
    {
        let changes = diff::compute(&original.text, &now)?;
        return Some(ChangeSet {
            base: "run",
            hunks: changes.hunks,
            added: changes.added,
            removed: changes.removed,
            truncated: changes.truncated,
            unchanged: changes.unchanged,
            run_id: Some(original.run_id),
            head_sha: None,
            created: original.created.then_some(true),
        });
    }
    let head = git_head(processes, &real_root.to_string_lossy(), &disk_relative, diff::MAXIMUM_SIDE_BYTES).await?;
    let decoded = classify::decode_text(&head.data, false)?;
    let changes = diff::compute(&decoded.text, &now)?;
    Some(ChangeSet {
        base: "head",
        hunks: changes.hunks,
        added: changes.added,
        removed: changes.removed,
        truncated: changes.truncated,
        unchanged: changes.unchanged,
        run_id: None,
        head_sha: head.short_sha,
        created: head.untracked.then_some(true),
    })
}
