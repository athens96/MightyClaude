//! C# `SessionHistory` and its records (SessionHistory.cs:10-204): where a pane's CLI keeps its session record — Claude's
//! `<config>/projects/<escaped cwd>/<session>.jsonl`, Codex's `sessions/YYYY/MM/DD/rollout-…-<thread>.jsonl`, Antigravity's
//! `brain/<conversation>/.system_generated/logs/transcript_full.jsonl` — and the next older chunk of it, replayed into graph runs.

use std::collections::HashMap;
use std::path::{Path, PathBuf};

use chrono::{Datelike, Local, TimeZone};
use mighty_core::graph::MightyGraphRun;
use mighty_core::locale::Locale;
use mighty_core::time::DateTimeOffset;
use mighty_core::wire;

use crate::backward::JsonlBackwardReader;
use crate::scan::{self, HistoryFormat};
use crate::{Cancel, antigravity, files, replay, resumable};

/// `SessionHistory.Providers`: the agents whose record is replayed into the graph (pi's records are listed for resuming, not replayed).
pub const PROVIDERS: [&str; 3] = ["claude", "codex", "gemini"];
pub const TURNS_PER_CHUNK: usize = 10;
pub const MAXIMUM_CHUNK_BYTES: i64 = 48 * 1_048_576;
/// A single record line larger than this is skipped rather than held in memory.
pub const MAXIMUM_LINE_BYTES: usize = 16 * 1_048_576;
/// One request's lines kept for replay; past it only the prompt survives.
pub const MAXIMUM_TURN_BYTES: usize = 24 * 1_048_576;
/// Text a replayed request may keep, as the saved profile bounds a run.
pub const RUN_BUDGET: i64 = 1_048_576;

/// `SessionHistoryFile(Path, Created)`: the record and its identity (the file's creation time stands in for an inode: an atomic replace or
/// a rotation creates a new file).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SessionHistoryFile {
    pub path: PathBuf,
    pub created: i64,
}

/// `SessionHistoryAnchor(Text, Date)`: the retained request the loaded history continues above, and when it was sent.
#[derive(Debug, Clone, PartialEq)]
pub struct SessionHistoryAnchor {
    pub text: String,
    pub date: Option<DateTimeOffset>,
}

const TICKS_PER_SECOND: i64 = 10_000_000;

fn seconds(a: DateTimeOffset, b: DateTimeOffset) -> f64 {
    (a.utc_ticks() - b.utc_ticks()) as f64 / TICKS_PER_SECOND as f64
}

impl SessionHistoryAnchor {
    pub fn new(text: &str, date: Option<DateTimeOffset>) -> SessionHistoryAnchor {
        SessionHistoryAnchor { text: text.to_string(), date }
    }

    /// `Typed`: what the user typed (the transcript adds one "첨부: " line per attachment).
    pub fn typed(text: &str) -> String {
        const MARKER: &str = "\u{CCA8}\u{BD80}: ";
        let typed = if text.starts_with(MARKER) { "" } else { text.split(&format!("\n\n{MARKER}")).next().unwrap_or("") };
        typed.trim().to_string()
    }

    /// `Quality`: 2 when the record prompt is the retained text, 1 when it only contains it, None otherwise. With both dates known it
    /// must also be written around the same time (the CLI records it a moment after the app sent it).
    pub fn quality(&self, prompt: &str, date: Option<DateTimeOffset>) -> Option<i32> {
        let typed = Self::typed(&self.text);
        let prompt = prompt.trim();
        if typed.is_empty() || !(prompt == typed || prompt.contains(&typed)) {
            return None;
        }
        if let (Some(anchor), Some(at)) = (self.date, date) {
            let difference = seconds(at, anchor);
            if !(-600.0..=120.0).contains(&difference) {
                return None;
            }
        }
        Some(if prompt == typed { 2 } else { 1 })
    }

    pub fn distance(&self, date: Option<DateTimeOffset>) -> f64 {
        match (self.date, date) {
            (Some(anchor), Some(at)) => seconds(at, anchor).abs(),
            _ => 0.0,
        }
    }

    /// Written after any prompt that could match: a newer, retained request.
    pub fn newer(&self, date: Option<DateTimeOffset>) -> bool {
        matches!((self.date, date), (Some(anchor), Some(at)) if seconds(at, anchor) > 120.0)
    }

    /// Written before any prompt that could still match: the search is over.
    pub fn below(&self, date: Option<DateTimeOffset>) -> bool {
        matches!((self.date, date), (Some(anchor), Some(at)) if seconds(at, anchor) < -600.0)
    }

    /// Written before the retained request was sent: older history even unmatched.
    pub fn precedes(&self, date: Option<DateTimeOffset>) -> bool {
        matches!((self.date, date), (Some(anchor), Some(at)) if seconds(at, anchor) < -1.0)
    }
}

/// `SessionHistoryRequest`: what to read. `file` and `end` continue an earlier chunk (None locates the record and reads from its end);
/// `anchor` only for the first chunk (newer requests than it are already shown).
#[derive(Debug, Clone)]
pub struct SessionHistoryRequest {
    pub provider: String,
    pub resume_id: String,
    pub workspace_path: String,
    pub environment: HashMap<String, String>,
    pub home: PathBuf,
    pub file: Option<SessionHistoryFile>,
    pub end: Option<i64>,
    pub anchor: Option<SessionHistoryAnchor>,
    pub turns: usize,
    /// Bytes read per chunk once at least one request was found.
    pub maximum_bytes: i64,
}

impl SessionHistoryRequest {
    pub fn new(provider: &str, resume_id: &str, workspace_path: &str) -> SessionHistoryRequest {
        SessionHistoryRequest {
            provider: provider.into(),
            resume_id: resume_id.into(),
            workspace_path: workspace_path.into(),
            environment: HashMap::new(),
            home: user_home(),
            file: None,
            end: None,
            anchor: None,
            turns: TURNS_PER_CHUNK,
            maximum_bytes: MAXIMUM_CHUNK_BYTES,
        }
    }

    pub fn home(mut self, home: impl Into<PathBuf>) -> SessionHistoryRequest {
        self.home = home.into();
        self
    }
}

/// `Environment.GetFolderPath(SpecialFolder.UserProfile)`.
pub fn user_home() -> PathBuf {
    std::env::var_os(if crate::windows_paths() { "USERPROFILE" } else { "HOME" }).map(PathBuf::from).unwrap_or_default()
}

/// `SessionHistoryChunk(Runs, File, End, ReachedStart)`: oldest first, each built by the live graph's own tracker.
#[derive(Debug, Clone)]
pub struct SessionHistoryChunk {
    pub runs: Vec<MightyGraphRun>,
    pub file: SessionHistoryFile,
    pub end: i64,
    pub reached_start: bool,
}

/// `SessionHistoryError`, and a load the caller cancelled (C#'s `OperationCanceledException`).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SessionHistoryError {
    Unsupported,
    Missing,
    Changed,
    Unreadable,
    Cancelled,
}

impl std::fmt::Display for SessionHistoryError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "{self:?}")
    }
}

impl std::error::Error for SessionHistoryError {}

// ── locating the record ─────────────────────────────────────────────────────────────────────────────────────────────────

/// `ClaudeProjectFolder`: Claude names a project folder after its working directory with every character other than an ASCII letter or
/// digit replaced by "-" (one per Unicode scalar).
pub fn claude_project_folder(path: &str) -> String {
    path.chars().map(|c| if c.is_ascii_alphanumeric() { c } else { '-' }).collect()
}

/// `ClaudeConfig`: `$CLAUDE_CONFIG_DIR`, else `<home>/.claude`.
pub fn claude_config(home: &Path, environment: &HashMap<String, String>) -> PathBuf {
    match environment.get("CLAUDE_CONFIG_DIR").filter(|v| !v.is_empty()) {
        Some(configured) => PathBuf::from(configured),
        None => home.join(".claude"),
    }
}

/// `CliAccountSupport.CodexHome`: `$CODEX_HOME`, else `<home>/.codex`.
pub fn codex_home(home: &Path, environment: &HashMap<String, String>) -> PathBuf {
    match environment.get("CODEX_HOME").filter(|v| !v.is_empty()) {
        Some(configured) => PathBuf::from(configured),
        None => home.join(".codex"),
    }
}

/// `SessionHistory.Locate`: the record of a session, or None.
pub fn locate(
    provider: &str,
    resume_id: &str,
    workspace_path: &str,
    environment: &HashMap<String, String>,
    home: &Path,
) -> Option<PathBuf> {
    if !wire::identifier(resume_id) {
        return None;
    }
    match provider {
        "claude" => {
            let projects = claude_config(home, environment).join("projects");
            let name = format!("{resume_id}.jsonl");
            for path in resumable::workspace_paths(workspace_path) {
                let candidate = projects.join(claude_project_folder(&path)).join(&name);
                if files::regular(&candidate) {
                    return Some(candidate);
                }
            }
            // A long path is shortened by the CLI; the session id alone still finds it.
            let entries = std::fs::read_dir(&projects).ok()?;
            for entry in entries.flatten().filter(|e| e.file_type().is_ok_and(|t| t.is_dir())).take(8_192) {
                let candidate = entry.path().join(&name);
                if files::regular(&candidate) {
                    return Some(candidate);
                }
            }
            None
        }
        "codex" => files::sessions(&codex_home(home, environment)).and_then(|sessions| codex_rollout(&sessions, resume_id)),
        "gemini" => antigravity::transcript(&antigravity::brain(home), resume_id).filter(|t| files::regular(t)),
        _ => None,
    }
}

/// `UuidV7Date`: Codex thread ids are UUIDv7, whose first 48 bits are the creation time (in milliseconds), which names the day folder.
pub fn uuid_v7_date(value: &str) -> Option<DateTimeOffset> {
    let hex: String = value.chars().filter(|c| *c != '-').collect();
    if !hex.is_ascii() || hex.len() != 32 || hex.as_bytes()[12] != b'7' {
        return None;
    }
    let milliseconds = i64::from_str_radix(&hex[..12], 16).ok()?;
    Some(DateTimeOffset::from_utc_ticks(mighty_core::time::ticks_of(chrono::DateTime::from_timestamp_millis(milliseconds)?)))
}

fn codex_rollout(sessions: &Path, thread: &str) -> Option<PathBuf> {
    let find = |folder: &Path| files::rollouts(folder).into_iter().find(|r| r.thread.eq_ignore_ascii_case(thread)).map(|r| r.path);
    if let Some(created) = uuid_v7_date(thread) {
        // Codex files a thread under the local date it was created; the day before covers a zone change.
        let utc = created.to_chrono();
        let day = Local.from_utc_datetime(&utc.naive_utc()).date_naive();
        for date in [day.succ_opt(), Some(day), day.pred_opt()].into_iter().flatten() {
            let folder =
                sessions.join(format!("{:04}", date.year())).join(format!("{:02}", date.month())).join(format!("{:02}", date.day()));
            if files::real_directory(&folder)
                && let Some(found) = find(&folder)
            {
                return Some(found);
            }
        }
    }
    files::day_folders(sessions).iter().find_map(|folder| find(folder))
}

/// `SessionHistory.Identify`: the record's identity, for a request that should read this file only.
pub fn identify(path: &Path) -> Option<SessionHistoryFile> {
    files::regular(path).then(|| SessionHistoryFile { path: path.to_path_buf(), created: files::created_ticks(path) })
}

// ── loading a chunk ─────────────────────────────────────────────────────────────────────────────────────────────────────

/// `SessionHistory.Load`: the next older chunk. Never reads the whole record into memory; a cancellation stops between requests.
pub fn load(request: &SessionHistoryRequest, locale: &Locale, cancel: &dyn Cancel) -> Result<SessionHistoryChunk, SessionHistoryError> {
    if !PROVIDERS.contains(&request.provider.as_str()) {
        return Err(SessionHistoryError::Unsupported);
    }
    let path = match &request.file {
        Some(file) => file.path.clone(),
        None => locate(&request.provider, &request.resume_id, &request.workspace_path, &request.environment, &request.home)
            .ok_or(SessionHistoryError::Missing)?,
    };
    let mut handle =
        files::open(&path).ok_or(if request.file.is_none() { SessionHistoryError::Missing } else { SessionHistoryError::Changed })?;
    let file = SessionHistoryFile { path: path.clone(), created: files::created_ticks(&path) };
    if request.file.as_ref().is_some_and(|known| *known != file) {
        return Err(SessionHistoryError::Changed);
    }
    let size = files::length(&handle);
    let end = request.end.unwrap_or(size);
    if end > size || end < 0 {
        return Err(SessionHistoryError::Changed);
    }
    let name = path.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default();
    let format = match request.provider.as_str() {
        "codex" => HistoryFormat::codex(&files::thread(&name).unwrap_or_else(|| request.resume_id.clone())),
        "gemini" => HistoryFormat::antigravity(&request.resume_id, antigravity::brain_of(&path)),
        _ => HistoryFormat::claude(),
    };
    // Where the chunk ends a newer request starts: its time ends the oldest one read here (Antigravity's sub-agents may get work from
    // later requests).
    let mut newer = None;
    if format.provider == "gemini"
        && let Some(chunk_end) = request.end
    {
        newer = scan::date(scan::opening_at(&mut handle, chunk_end, &format).and_then(|o| o.timestamp).as_deref());
    }
    let mut reader = JsonlBackwardReader::new(&mut handle, end, MAXIMUM_LINE_BYTES);
    let anchor = if request.end.is_none() { request.anchor.as_ref() } else { None };
    let scanned = scan::run(&mut reader, &format, anchor, request.turns.max(1), request.maximum_bytes, cancel, newer)?;
    let mut runs = vec![];
    // Sub-agents' transcripts the chunk's requests read, together.
    let mut child_bytes = replay::ANTIGRAVITY_CHILD_BYTES.min(request.maximum_bytes.max(0));
    for turn in &scanned.turns {
        if cancel.cancelled() {
            return Err(SessionHistoryError::Cancelled);
        }
        runs.extend(replay::runs(turn, &format, &mut child_bytes, locale));
    }
    Ok(SessionHistoryChunk { runs, file, end: scanned.end, reached_start: scanned.reached_start })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn claude_names_a_project_folder_after_its_path() {
        assert_eq!(claude_project_folder("C:\\Users\\me\\work"), "C--Users-me-work");
        assert_eq!(claude_project_folder("/home/한글/x"), "-home----x");
    }

    #[test]
    fn a_codex_thread_says_when_it_was_made() {
        let date = uuid_v7_date("0197c1d2-7f3a-7c4e-9b1a-5d2e8f6a4b30").expect("a v7 id");
        assert!(date.to_o().starts_with("2025-"), "{}", date.to_o());
        assert!(uuid_v7_date("2d9e2c1a-1111-4222-8333-444455556666").is_none(), "a v4 id has no date");
    }

    #[test]
    fn the_anchor_matches_text_then_time() {
        let at = mighty_core::time::parse(Some("2026-10-01T10:00:00Z"));
        let anchor = SessionHistoryAnchor::new("fix it\n\n\u{CCA8}\u{BD80}: a.png", at);
        assert_eq!(anchor.quality("fix it", at), Some(2));
        assert_eq!(anchor.quality("please fix it now", at), Some(1));
        assert_eq!(anchor.quality("fix it", mighty_core::time::parse(Some("2026-10-01T10:03:00Z"))), None);
        assert!(anchor.below(mighty_core::time::parse(Some("2026-10-01T09:49:00Z"))));
        assert!(anchor.precedes(mighty_core::time::parse(Some("2026-10-01T09:59:58Z"))));
        assert!(anchor.newer(mighty_core::time::parse(Some("2026-10-01T10:02:01Z"))));
    }
}
