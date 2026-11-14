//! C# `ResumableSessions` and `KnownSessionIDs` (ResumableSessions.cs): the earlier Claude, Codex, Antigravity, pi, opencode and Hermes
//! sessions recorded for a workspace folder that a new pane can continue (`--resume` / `exec resume` / `--conversation` / `--session` /
//! ACP's `session/resume`), read-only and bounded, and the ids the app's own panes started or resumed.

use std::collections::{HashMap, HashSet};
use std::fs::File;
use std::path::{Path, PathBuf};

use mighty_core::json;
use mighty_core::locale::Locale;
use mighty_core::model::RunSession;
use mighty_core::time::DateTimeOffset;
use mighty_core::wire;
use serde_json::Value;
use unicode_normalization::UnicodeNormalization;
use unicode_segmentation::UnicodeSegmentation;

use crate::history::{claude_config, claude_project_folder, codex_home, user_home};
use crate::scan::{self, HistoryFormat, prop};
use crate::{Cancel, acp, antigravity, files, pi, replay};

/// `ResumableSession(Provider, SessionID, Title, Modified, Requests, Model, Path, Automated)`.
#[derive(Debug, Clone, PartialEq)]
pub struct ResumableSession {
    pub provider: String,
    pub session_id: String,
    pub title: Option<String>,
    pub modified: DateTimeOffset,
    pub requests: Option<i32>,
    pub model: Option<String>,
    pub path: PathBuf,
    pub automated: bool,
}

impl ResumableSession {
    /// `Id`: `provider:session`.
    pub fn id(&self) -> String {
        format!("{}:{}", self.provider, self.session_id)
    }
}

/// `ResumableSessionListing(Items, Hidden)`: what to show, and how many automated sessions were left out.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct ResumableSessionListing {
    pub items: Vec<ResumableSession>,
    pub hidden: usize,
}

/// `ResumableSessionQuery`.
#[derive(Debug, Clone)]
pub struct ResumableSessionQuery {
    pub workspace_path: String,
    pub environment: HashMap<String, String>,
    pub home: PathBuf,
    /// Session ids open panes already use (any provider, lowercased).
    pub excluding: HashSet<String>,
    /// Session ids the app's own panes started or resumed (lowercased): never hidden.
    pub known: HashSet<String>,
    /// Lists automated sessions too ("모든 세션 보기").
    pub include_automated: bool,
    pub now: DateTimeOffset,
    /// Seconds.
    pub maximum_age_seconds: i64,
    /// Records read for a row per provider, newest first; excluded and hidden ones use no slot.
    pub maximum_candidates: usize,
    /// Records looked at per provider at all, hidden ones included.
    pub maximum_scanned: usize,
    pub maximum_sessions: usize,
    /// Reads only what decides whether a record is listed: no request count, no model from the record's end ("창 추가"'s yes/no look-up).
    pub head_only: bool,
}

impl ResumableSessionQuery {
    pub fn new(workspace_path: &str) -> ResumableSessionQuery {
        ResumableSessionQuery {
            workspace_path: workspace_path.into(),
            environment: HashMap::new(),
            home: user_home(),
            excluding: HashSet::new(),
            known: HashSet::new(),
            include_automated: false,
            now: DateTimeOffset::now(),
            maximum_age_seconds: MAXIMUM_AGE_SECONDS,
            maximum_candidates: MAXIMUM_CANDIDATES,
            maximum_scanned: MAXIMUM_SCANNED,
            maximum_sessions: MAXIMUM_SESSIONS,
            head_only: false,
        }
    }

    fn oldest(&self) -> DateTimeOffset {
        DateTimeOffset::from_utc_ticks(self.now.utc_ticks() - self.maximum_age_seconds * 10_000_000)
    }
}

pub const PROVIDERS: [&str; 6] = ["claude", "codex", "gemini", "pi", "opencode", "hermes"];
/// The variables the opencode and Hermes lists find their databases by: a session list's environment carries them besides the history's.
pub const STORE_VARIABLES: [&str; 5] = ["XDG_DATA_HOME", "OPENCODE_DB", "HERMES_HOME", "HERMES_DATA_DIR_SUFFIX", "LOCALAPPDATA"];
pub const MAXIMUM_AGE_SECONDS: i64 = 60 * 86_400;
pub const MAXIMUM_CANDIDATES: usize = 600;
pub const MAXIMUM_SCANNED: usize = 6_000;
pub const MAXIMUM_SESSIONS: usize = 200;
/// A record written this recently may belong to a CLI still running (seconds).
pub const BUSY_SECONDS: i64 = 120;
const HEAD_BYTES: i64 = 2 * 1_048_576;
const META_BYTES: i64 = 512 * 1_024;
const COUNT_BYTES: i64 = 1_048_576;
const COUNT_BUDGET: i64 = 96 * 1_048_576;
const TITLE_CHARACTERS: usize = 200;

fn uuid(id: &str) -> bool {
    let b = id.as_bytes();
    b.len() == 36 && b.iter().enumerate().all(|(i, c)| if matches!(i, 8 | 13 | 18 | 23) { *c == b'-' } else { c.is_ascii_hexdigit() })
}

/// `OffersResume`: whether the menu asks "새로 시작 / 이어가기…" for this provider at all.
pub fn offers_resume(kind: &str, provider: &str) -> bool {
    kind == "claude" && PROVIDERS.contains(&provider)
}

/// `AddPaneMenu.MayAskResume`: only a pick from the menu, and only for an agent whose sessions can be continued; Ctrl+N starts at once.
pub fn may_ask_resume(provider: &str, from_menu: bool) -> bool {
    from_menu && offers_resume("claude", provider)
}

/// `AddPaneMenu.QuietLookUp`: how long the look-up runs before the choice sheet shows in its checking state (milliseconds).
pub const QUIET_LOOK_UP_MS: u64 = 300;

/// `AddPaneMenu.AsksResume`: whether the look-up found something to ask about — a session of this agent that no open pane continues.
/// `in_use` is read again when the look-up ends, since a pane may have taken one while it ran.
pub fn asks_resume<'a>(provider: &str, sessions: &[ResumableSession], in_use: impl IntoIterator<Item = &'a String>) -> bool {
    if !offers_resume("claude", provider) {
        return false;
    }
    let used: HashSet<String> = in_use.into_iter().map(|id| id.to_lowercase()).collect();
    sessions.iter().any(|s| s.provider == provider && !used.contains(&s.session_id.to_lowercase()))
}

/// `Listing`: every provider's sessions, newest first.
pub fn listing(query: &ResumableSessionQuery, cancel: &dyn Cancel) -> ResumableSessionListing {
    let mut budget = COUNT_BUDGET;
    let parts = [
        claude(query, &mut budget, cancel),
        codex(query, &mut budget, cancel),
        antigravity_listing(query, &mut budget, cancel),
        pi_listing(query, &mut budget, cancel),
        opencode(query, cancel),
        hermes(query, cancel),
    ];
    let hidden = parts.iter().map(|p| p.hidden).sum();
    let mut items: Vec<ResumableSession> = parts.into_iter().flat_map(|p| p.items).collect();
    items.sort_by(|a, b| b.modified.utc_ticks().cmp(&a.modified.utc_ticks()).then_with(|| a.id().cmp(&b.id())));
    ResumableSessionListing { items, hidden }
}

/// `Listing(query, provider)`: one agent's sessions alone; the other agents' records are not read.
pub fn listing_of(query: &ResumableSessionQuery, provider: &str, cancel: &dyn Cancel) -> ResumableSessionListing {
    let mut budget = COUNT_BUDGET;
    match provider {
        "claude" => claude(query, &mut budget, cancel),
        "codex" => codex(query, &mut budget, cancel),
        "gemini" => antigravity_listing(query, &mut budget, cancel),
        "pi" => pi_listing(query, &mut budget, cancel),
        "opencode" => opencode(query, cancel),
        "hermes" => hermes(query, cancel),
        _ => ResumableSessionListing::default(),
    }
}

/// `AutomatedPrompt`: a nested non-interactive run (an Ouroboros step's `claude --print`) sends a flattened transcript as its first
/// prompt ("User: …" / "Assistant: …"); a person types no such thing.
pub fn automated_prompt(prompt: Option<&str>) -> bool {
    prompt.is_some_and(|p| {
        p.starts_with("User: ") || p.starts_with("Assistant: ") || p.starts_with("User:\n") || p.starts_with("Assistant:\n")
    })
}

/// `MayBeRunning`: the record was written so recently that a CLI elsewhere may still run the session.
pub fn may_be_running(item: &ResumableSession, now: DateTimeOffset) -> bool {
    now.utc_ticks() - item.modified.utc_ticks() < BUSY_SECONDS * 10_000_000
}

/// `InUse`: ids of sessions open panes continue, lowercased.
pub fn in_use<'a>(sessions: impl IntoIterator<Item = &'a RunSession>) -> HashSet<String> {
    sessions.into_iter().filter(|s| s.kind() == "claude").filter_map(|s| s.resume_id.as_deref()).map(str::to_lowercase).collect()
}

/// Folds a text as `CompareOptions.IgnoreCase | IgnoreWidth | IgnoreNonSpace` compares it: compatibility forms (full-width letters, …)
/// read as their plain ones, combining marks dropped, letters lowered, and what is left composed again, so a Hangul syllable stays one
/// letter (decomposed, "하" would be found inside "한").
fn folded(text: &str) -> String {
    text.nfkd().filter(|c| !unicode_normalization::char::is_combining_mark(*c)).flat_map(char::to_lowercase).nfc().collect()
}

/// `Filter`: the titles containing every word of the query, ignoring case, width and accents.
pub fn filter(items: &[ResumableSession], query: &str) -> Vec<ResumableSession> {
    let words: Vec<String> = query.split_whitespace().map(folded).collect();
    if words.is_empty() {
        return items.to_vec();
    }
    items
        .iter()
        .filter(|item| {
            let title = folded(item.title.as_deref().unwrap_or(""));
            words.iter().all(|word| title.contains(word.as_str()))
        })
        .cloned()
        .collect()
}

/// `Apply`: makes a freshly added agent pane continue the session — it resumes that session and is titled after its first request. The
/// title stays automatic, so the next request retitles the pane as usual.
pub fn apply(item: &ResumableSession, session: &RunSession) -> RunSession {
    RunSession {
        provider: Some(item.provider.clone()),
        resume_id: Some(item.session_id.clone()),
        title: item.title.as_deref().and_then(mighty_core::normalize::shortened).or_else(|| session.title.clone()),
        title_mode: Some("auto".into()),
        ..session.clone()
    }
}

/// `RelativeTime`: "방금", "5분 전", "3시간 전", "12일 전".
pub fn relative_time(date: DateTimeOffset, now: DateTimeOffset, locale: &Locale) -> String {
    let seconds = ((now.utc_ticks() - date.utc_ticks()) as f64 / 10_000_000.0).max(0.0);
    let count = |value: f64| (value as i64).to_string();
    if seconds < 60.0 {
        locale.get("resume.time.now")
    } else if seconds < 3_600.0 {
        locale.get_with("resume.time.minutes", &[("count", &count(seconds / 60.0))])
    } else if seconds < 86_400.0 {
        locale.get_with("resume.time.hours", &[("count", &count(seconds / 3_600.0))])
    } else {
        locale.get_with("resume.time.days", &[("count", &count(seconds / 86_400.0))])
    }
}

/// `Details`: the row's second line — when, how many requests, which model, and the "자동 실행 기록" and "방금 수정됨" notes.
pub fn details(item: &ResumableSession, now: DateTimeOffset, locale: &Locale) -> String {
    let mut parts = vec![relative_time(item.modified, now, locale)];
    if let Some(requests) = item.requests {
        parts.push(locale.get_with("resume.requests", &[("count", &requests.to_string())]));
    }
    if let Some(model) = item.model.as_deref().filter(|m| !m.is_empty()) {
        parts.push(mighty_core::model_label::text(model, None, None));
    }
    if item.automated {
        parts.push(locale.get("resume.automated"));
    }
    if may_be_running(item, now) {
        parts.push(locale.get("resume.recentlyModified"));
    }
    parts.join(" · ")
}

/// `RowTitle`.
pub fn row_title(item: &ResumableSession, locale: &Locale) -> String {
    item.title.clone().unwrap_or_else(|| locale.get("resume.untitled"))
}

/// `OneLine`: a title of one line and at most 200 text elements ("…" when cut).
pub fn one_line(text: &str) -> String {
    // A pasted prompt can be very long; only its start can show.
    let limit = TITLE_CHARACTERS * 8;
    let mut units = 0;
    let mut end = text.len();
    for (at, c) in text.char_indices() {
        if units + c.len_utf16() > limit {
            end = at;
            break;
        }
        units += c.len_utf16();
    }
    let start = &text[..end];
    let collapsed = start.split_whitespace().collect::<Vec<_>>().join(" ");
    let elements: Vec<(usize, &str)> = collapsed.grapheme_indices(true).collect();
    let cut = elements.len() > TITLE_CHARACTERS || start.len() < text.len();
    if !cut {
        return collapsed;
    }
    let kept = if elements.len() > TITLE_CHARACTERS { &collapsed[..elements[TITLE_CHARACTERS].0] } else { collapsed.as_str() };
    format!("{kept}…")
}

// ── the workspace folder ────────────────────────────────────────────────────────────────────────────────────────────────

fn normalized(path: &str) -> String {
    let windows = crate::windows_paths();
    let root_length = root_of(path).len();
    let mut path = path.to_string();
    while path.len() > root_length.max(1) && (path.ends_with('/') || (windows && path.ends_with('\\'))) {
        path.pop();
    }
    path
}

/// `Path.GetPathRoot`: `C:\`, `\\server\share`, `/`, or nothing.
fn root_of(path: &str) -> String {
    Path::new(path)
        .components()
        .take_while(|c| matches!(c, std::path::Component::Prefix(_) | std::path::Component::RootDir))
        .map(|c| c.as_os_str().to_string_lossy().into_owned())
        .collect()
}

fn resolved(path: &str) -> Option<String> {
    let full = mighty_core::workspace_files::full_path(Path::new(path))?;
    let meta = std::fs::symlink_metadata(&full);
    match meta {
        Ok(meta) if meta.file_type().is_symlink() => {
            mighty_core::workspace_files::real_path(&full).map(|p| p.to_string_lossy().into_owned())
        }
        _ => Some(full.to_string_lossy().into_owned()),
    }
}

fn same_path(a: &str, b: &str) -> bool {
    if crate::windows_paths() { mighty_core::normalize::ordinal_ignore_case(a, b).is_eq() } else { a == b }
}

/// `WorkspacePaths`: the workspace path as given and resolved, without a trailing separator.
pub fn workspace_paths(path: &str) -> Vec<String> {
    let mut paths = vec![normalized(path)];
    if let Some(real) = resolved(path) {
        let real = normalized(&real);
        if !paths.iter().any(|p| same_path(p, &real)) {
            paths.push(real);
        }
    }
    paths
}

fn matches(cwd: &str, paths: &[String]) -> bool {
    let plain = normalized(cwd);
    if paths.iter().any(|p| same_path(p, &plain)) {
        return true;
    }
    !on_another_share(cwd, paths)
        && resolved(cwd).is_some_and(|real| {
            let real = normalized(&real);
            paths.iter().any(|p| same_path(p, &real))
        })
}

/// A share or device path (`\\server\share\…`, `\\?\…`) the workspace does not lie on: it is never resolved, since asking a share that
/// is offline can take seconds for each record (and no link on it leads back to a local workspace the listing would show).
fn on_another_share(cwd: &str, paths: &[String]) -> bool {
    let shared = |path: &str| path.starts_with("\\\\") || path.starts_with("//");
    crate::windows_paths() && shared(cwd) && !paths.iter().any(|p| shared(p) && same_path(&root_of(p), &root_of(cwd)))
}

/// The workspace folder a record must name (`WorkspacePaths`), each record's folder decided once a listing: records share a few
/// folders, and resolving one touches the disk.
struct Folder {
    paths: Vec<String>,
    seen: HashMap<String, bool>,
}

impl Folder {
    fn new(workspace_path: &str) -> Folder {
        Folder { paths: workspace_paths(workspace_path), seen: HashMap::new() }
    }

    fn matches(&mut self, cwd: &str) -> bool {
        if let Some(here) = self.seen.get(cwd) {
            return *here;
        }
        let here = matches(cwd, &self.paths);
        self.seen.insert(cwd.to_string(), here);
        here
    }
}

struct Candidate {
    path: PathBuf,
    id: String,
    modified: DateTimeOffset,
}

/// `Recent`: within the age limit, newest first, at most the scan limit.
fn recent(mut candidates: Vec<Candidate>, query: &ResumableSessionQuery) -> Vec<Candidate> {
    let oldest = query.oldest();
    candidates.retain(|c| c.modified.utc_ticks() >= oldest.utc_ticks());
    candidates
        .sort_by(|a, b| b.modified.utc_ticks().cmp(&a.modified.utc_ticks()).then_with(|| mighty_core::normalize::ordinal(&a.id, &b.id)));
    candidates.truncate(query.maximum_scanned);
    candidates
}

enum Outcome {
    Listed(ResumableSession),
    Hidden,
    Skipped,
}

fn count(file: &mut File, size: i64, head_only: bool, budget: &mut i64, opens: &mut dyn FnMut(&[u8]) -> bool) -> Option<i32> {
    if head_only || size > COUNT_BYTES || *budget < size {
        return None;
    }
    *budget -= size;
    let mut count = 0;
    let complete = files::for_each_line(file, size, size, |line| {
        if opens(line) {
            count += 1;
        }
        true
    });
    complete.then_some(count)
}

/// One provider's walk over its candidates (the loop each C# lister shares).
fn walk(
    candidates: Vec<Candidate>,
    query: &ResumableSessionQuery,
    cancel: &dyn Cancel,
    dedupe: bool,
    mut read: impl FnMut(&Candidate, bool) -> Outcome,
) -> ResumableSessionListing {
    let mut listing = ResumableSessionListing::default();
    let mut read_count = 0;
    let mut seen = HashSet::new();
    for candidate in recent(candidates, query) {
        if listing.items.len() >= query.maximum_sessions || read_count >= query.maximum_candidates || cancel.cancelled() {
            break;
        }
        let lowered = candidate.id.to_lowercase();
        if query.excluding.contains(&lowered) || (dedupe && seen.contains(&lowered)) {
            continue;
        }
        match read(&candidate, query.known.contains(&lowered)) {
            Outcome::Listed(item) => {
                read_count += 1;
                seen.insert(lowered);
                listing.items.push(item);
            }
            Outcome::Hidden => {
                seen.insert(lowered);
                listing.hidden += 1;
            }
            Outcome::Skipped => read_count += 1,
        }
    }
    listing
}

// ── Claude ──────────────────────────────────────────────────────────────────────────────────────────────────────────────

fn claude(query: &ResumableSessionQuery, budget: &mut i64, cancel: &dyn Cancel) -> ResumableSessionListing {
    let projects = claude_config(&query.home, &query.environment).join("projects");
    let mut folder = Folder::new(&query.workspace_path);
    let mut folders: Vec<String> = vec![];
    for path in &folder.paths {
        let folder = claude_project_folder(path);
        if !folders.contains(&folder) {
            folders.push(folder);
        }
    }
    let mut candidates = vec![];
    let mut seen = HashSet::new();
    for folder in &folders {
        for (path, modified) in files::files(&projects.join(folder), ".jsonl") {
            let id = path.file_stem().map(|s| s.to_string_lossy().into_owned()).unwrap_or_default();
            if !uuid(&id) || !seen.insert(id.to_lowercase()) {
                continue;
            }
            candidates.push(Candidate { path, id, modified });
        }
    }
    walk(candidates, query, cancel, false, |candidate, known| {
        claude_session(&candidate.path, &candidate.id, candidate.modified, &mut folder, known, query.include_automated, query.head_only, budget)
    })
}

/// The head alone decides whether a record is listed: its first working folder and its first request. Only a listed record is read
/// further.
#[allow(clippy::too_many_arguments)]
fn claude_session(
    path: &Path,
    id: &str,
    modified: DateTimeOffset,
    folder: &mut Folder,
    known: bool,
    include_automated: bool,
    head_only: bool,
    budget: &mut i64,
) -> Outcome {
    let Some(mut file) = files::open(path) else { return Outcome::Skipped };
    let size = files::length(&file);
    if size <= 0 {
        return Outcome::Skipped;
    }
    let (mut title, mut cwd_checked, mut foreign) = (None::<String>, false, false);
    let head_complete = files::for_each_line(&mut file, size, HEAD_BYTES, |line| {
        if !cwd_checked
            && scan::contains(line, "\"cwd\":")
            && let Some(value) = scan::parse(line)
            && let Some(cwd) = json::text(&value, "cwd")
        {
            cwd_checked = true;
            if json::flag(&value, "isSidechain") || !folder.matches(cwd) {
                foreign = true;
                return false;
            }
        }
        if title.is_none()
            && let Some(opening) = scan::opening_of(line, &HistoryFormat::claude())
        {
            title = opening.prompt;
        }
        title.is_none() || !cwd_checked
    });
    // A finished record without a single request has nothing to continue.
    if foreign || (title.is_none() && head_complete) {
        return Outcome::Skipped;
    }
    let automated = !known && automated_prompt(title.as_deref());
    if automated && !include_automated {
        return Outcome::Hidden;
    }
    let requests = count(&mut file, size, head_only, budget, &mut |line| scan::opening_of(line, &HistoryFormat::claude()).is_some());
    let model = if head_only { None } else { files::tail_value(&mut file, size, claude_model) };
    Outcome::Listed(ResumableSession {
        provider: "claude".into(),
        session_id: id.into(),
        title: title.as_deref().map(one_line),
        modified,
        requests,
        model,
        path: path.to_path_buf(),
        automated,
    })
}

fn claude_model(line: &[u8]) -> Option<String> {
    if !scan::contains(line, "\"type\":\"assistant\"") || !scan::contains(line, "\"model\":") {
        return None;
    }
    let value = scan::parse(line)?;
    if json::text(&value, "type") != Some("assistant") || json::flag(&value, "isSidechain") {
        return None;
    }
    let model = scan::text(prop(Some(&value), "message"), "model")?;
    (!model.starts_with('<') && wire::model(model)).then(|| model.to_string())
}

// ── Codex ───────────────────────────────────────────────────────────────────────────────────────────────────────────────

fn codex(query: &ResumableSessionQuery, budget: &mut i64, cancel: &dyn Cancel) -> ResumableSessionListing {
    let Some(sessions) = files::sessions(&codex_home(&query.home, &query.environment)) else { return ResumableSessionListing::default() };
    let mut folder = Folder::new(&query.workspace_path);
    let candidates = files::day_folders(&sessions)
        .iter()
        .flat_map(|day| files::rollouts(day))
        .map(|r| Candidate { path: r.path, id: r.thread, modified: r.modified })
        .filter(|c| wire::identifier(&c.id))
        .collect();
    walk(candidates, query, cancel, true, |candidate, known| {
        codex_session(&candidate.path, &candidate.id, candidate.modified, &mut folder, known, query.include_automated, query.head_only, budget)
    })
}

#[allow(clippy::too_many_arguments)]
fn codex_session(
    path: &Path,
    id: &str,
    modified: DateTimeOffset,
    folder: &mut Folder,
    known: bool,
    include_automated: bool,
    head_only: bool,
    budget: &mut i64,
) -> Outcome {
    let Some(mut file) = files::open(path) else { return Outcome::Skipped };
    let size = files::length(&file);
    if size <= 0 {
        return Outcome::Skipped;
    }
    // The first line alone decides; most of a long line is instructions.
    let mut first: Option<Vec<u8>> = None;
    files::for_each_line(&mut file, size, META_BYTES, |line| {
        first = Some(line.to_vec());
        false
    });
    let Some(meta) = first.as_deref().and_then(scan::parse) else { return Outcome::Skipped };
    if json::text(&meta, "type") != Some("session_meta") {
        return Outcome::Skipped;
    }
    let payload = prop(Some(&meta), "payload");
    let Some(cwd) = scan::text(payload, "cwd") else { return Outcome::Skipped };
    if !folder.matches(cwd) {
        return Outcome::Skipped;
    }
    if prop(payload, "source").and_then(Value::as_object).is_some_and(|s| s.contains_key("subagent")) {
        return Outcome::Skipped;
    }
    // The first turn's lines that can carry its user text, read until the turn's text is certain or the next turn starts.
    let (mut title, mut head_model, mut turn, mut in_turn) = (None::<String>, None::<String>, Vec::<Vec<u8>>::new(), false);
    let format = HistoryFormat::codex(id);
    files::for_each_line(&mut file, size, HEAD_BYTES, |line| {
        if head_model.is_none() {
            head_model = replay::codex_model(&[line]);
        }
        if scan::opening_of(line, &format).is_some() {
            if in_turn && let Some(prompt) = scan::codex_prompt(&turn) {
                title = Some(prompt);
                return false;
            }
            in_turn = true;
            turn.clear();
            return true;
        }
        if !scan::codex_user_line(line) {
            return true;
        }
        turn.push(line.to_vec());
        // The item or the event is the turn's own text; nothing can beat it.
        if scan::codex_user_item(line).is_some() || scan::codex_user_event(line).is_some() {
            title = scan::codex_prompt(&turn);
            return false;
        }
        true
    });
    if title.is_none() {
        title = scan::codex_prompt(&turn);
    }
    let automated = !known && automated_prompt(title.as_deref());
    if automated && !include_automated {
        return Outcome::Hidden;
    }
    let requests = count(&mut file, size, head_only, budget, &mut |line| {
        if head_model.is_none() {
            head_model = replay::codex_model(&[line]);
        }
        scan::opening_of(line, &format).is_some()
    });
    let model = if head_only { head_model } else { files::tail_value(&mut file, size, |line| replay::codex_model(&[line])).or(head_model) };
    Outcome::Listed(ResumableSession {
        provider: "codex".into(),
        session_id: id.into(),
        title: title.as_deref().map(one_line),
        modified,
        requests,
        model,
        path: path.to_path_buf(),
        automated,
    })
}

// ── Antigravity ─────────────────────────────────────────────────────────────────────────────────────────────────────────

/// `conversation_summaries.db` rows whose folders include the workspace, without sub-agents' conversations, that still have their
/// transcript. The title is the first request typed, as for the others; agy's own summary title stands in when none was read.
fn antigravity_listing(query: &ResumableSessionQuery, budget: &mut i64, cancel: &dyn Cancel) -> ResumableSessionListing {
    let Some(rows) = antigravity::summaries(&antigravity::database(&query.home), 20_000) else { return ResumableSessionListing::default() };
    let mut folder = Folder::new(&query.workspace_path);
    let brain = antigravity::brain(&query.home);
    let mut candidates = vec![];
    let mut titles: HashMap<String, String> = HashMap::new();
    let mut seen = HashSet::new();
    for row in rows {
        if !row.parent.is_empty() {
            continue;
        }
        let here = row.paths.iter().any(|p| folder.matches(p));
        if !here || !seen.insert(row.conversation.to_lowercase()) {
            continue;
        }
        let Some(transcript) = antigravity::has_transcript(&brain, &row.conversation) else { continue };
        candidates.push(Candidate { modified: files::modified(&transcript), path: transcript, id: row.conversation.clone() });
        if !row.title.is_empty() {
            titles.insert(row.conversation.clone(), row.title);
        }
    }
    walk(candidates, query, cancel, false, |candidate, known| {
        antigravity_session(
            &candidate.path,
            &candidate.id,
            candidate.modified,
            titles.get(&candidate.id).cloned(),
            known,
            query.include_automated,
            query.head_only,
            budget,
        )
    })
}

#[allow(clippy::too_many_arguments)]
fn antigravity_session(
    path: &Path,
    id: &str,
    modified: DateTimeOffset,
    summary: Option<String>,
    known: bool,
    include_automated: bool,
    head_only: bool,
    budget: &mut i64,
) -> Outcome {
    let Some(mut file) = files::open(path) else { return Outcome::Skipped };
    let size = files::length(&file);
    if size <= 0 {
        return Outcome::Skipped;
    }
    let mut title: Option<String> = None;
    let head_complete = files::for_each_line(&mut file, size, HEAD_BYTES, |line| {
        title = antigravity::opening(line).map(|o| o.0);
        title.is_none()
    });
    // A conversation without a single request has nothing to continue.
    if title.is_none() && head_complete {
        return Outcome::Skipped;
    }
    let automated = !known && automated_prompt(title.as_deref());
    if automated && !include_automated {
        return Outcome::Hidden;
    }
    let requests = count(&mut file, size, head_only, budget, &mut |line| antigravity::opening(line).is_some());
    let shown = title.or(summary);
    Outcome::Listed(ResumableSession {
        provider: "gemini".into(),
        session_id: id.into(),
        title: shown.as_deref().map(one_line),
        modified,
        requests,
        model: None,
        path: path.to_path_buf(),
        automated,
    })
}

// ── pi ──────────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// `<agent dir>/sessions/--<path>--/<timestamp>_<id>.jsonl` of the workspace folder: the first line is the header (`type: session`, `id`,
/// `cwd`); a request is a `message` entry whose message has the `user` role.
fn pi_listing(query: &ResumableSessionQuery, budget: &mut i64, cancel: &dyn Cancel) -> ResumableSessionListing {
    let mut workspace = Folder::new(&query.workspace_path);
    let mut candidates = vec![];
    let mut seen = HashSet::new();
    for folder in pi::session_folders(&workspace.paths, &query.home, &query.environment) {
        for (path, modified) in files::files(&folder, ".jsonl") {
            let name = path.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default();
            let Some(id) = pi::session_id(&name) else { continue };
            if !seen.insert(id.to_lowercase()) {
                continue;
            }
            candidates.push(Candidate { path, id, modified });
        }
    }
    walk(candidates, query, cancel, false, |candidate, known| {
        pi_session(&candidate.path, &candidate.id, candidate.modified, &mut workspace, known, query.include_automated, query.head_only, budget)
    })
}

#[allow(clippy::too_many_arguments)]
fn pi_session(
    path: &Path,
    id: &str,
    modified: DateTimeOffset,
    folder: &mut Folder,
    known: bool,
    include_automated: bool,
    head_only: bool,
    budget: &mut i64,
) -> Outcome {
    let Some(mut file) = files::open(path) else { return Outcome::Skipped };
    let size = files::length(&file);
    if size <= 0 {
        return Outcome::Skipped;
    }
    let (mut title, mut header_checked, mut foreign) = (None::<String>, false, false);
    let head_complete = files::for_each_line(&mut file, size, HEAD_BYTES, |line| {
        if !header_checked {
            header_checked = true;
            let header = scan::parse(line);
            let good = header
                .as_ref()
                .is_some_and(|h| json::text(h, "type") == Some("session") && json::text(h, "cwd").is_some_and(|cwd| folder.matches(cwd)));
            if !good {
                foreign = true;
                return false;
            }
            return true;
        }
        title = pi_prompt(line);
        title.is_none()
    });
    // A session without a single request has nothing to continue.
    if foreign || (title.is_none() && head_complete) {
        return Outcome::Skipped;
    }
    let automated = !known && automated_prompt(title.as_deref());
    if automated && !include_automated {
        return Outcome::Hidden;
    }
    let requests = count(&mut file, size, head_only, budget, &mut |line| pi_prompt(line).is_some());
    let model = if head_only { None } else { files::tail_value(&mut file, size, pi_model) };
    Outcome::Listed(ResumableSession {
        provider: "pi".into(),
        session_id: id.into(),
        title: title.as_deref().map(one_line),
        modified,
        requests,
        model,
        path: path.to_path_buf(),
        automated,
    })
}

/// The text of a pi user request line; None for any other line.
fn pi_prompt(line: &[u8]) -> Option<String> {
    if !scan::contains(line, "\"role\":\"user\"") {
        return None;
    }
    let value = scan::parse(line)?;
    if json::text(&value, "type") != Some("message") {
        return None;
    }
    let message = prop(Some(&value), "message");
    if scan::text(message, "role") != Some("user") {
        return None;
    }
    let text = match prop(message, "content") {
        Some(Value::String(s)) => s.clone(),
        Some(Value::Array(blocks)) => scan::text_blocks(blocks),
        _ => return None,
    };
    let prompt = text.trim();
    (!prompt.is_empty()).then(|| prompt.to_string())
}

fn pi_model(line: &[u8]) -> Option<String> {
    if !scan::contains(line, "\"role\":\"assistant\"") {
        return None;
    }
    let value = scan::parse(line)?;
    if json::text(&value, "type") != Some("message") {
        return None;
    }
    let model = scan::text(prop(Some(&value), "message"), "model")?;
    wire::model(model).then(|| model.to_string())
}

// ── opencode and Hermes ─────────────────────────────────────────────────────────────────────────────────────────────────

/// The root sessions OpenCode 2's `opencode.db` keeps for the workspace folder, newest first: the first user message is the title, else
/// opencode's own unless it is still its placeholder; the request count is the user messages, and a session without one is left out.
fn opencode(query: &ResumableSessionQuery, cancel: &dyn Cancel) -> ResumableSessionListing {
    let Some(database) = acp::opencode_database(&query.environment, &query.home) else { return ResumableSessionListing::default() };
    let rows = acp::opencode_rows(
        &database,
        &workspace_paths(&query.workspace_path),
        query.oldest(),
        query.maximum_scanned.min(2_000),
        !query.head_only,
    );
    listed("opencode", &database, rows, query, cancel)
}

/// The conversations Hermes's ACP server lists for the workspace folder (`state.db`), newest first; a compressed conversation once,
/// under its first row's id, which ACP resumes.
fn hermes(query: &ResumableSessionQuery, cancel: &dyn Cancel) -> ResumableSessionListing {
    let database = acp::hermes_state_database(&query.environment, Some(&query.home));
    // Conversations share a few folders: each is decided once.
    let mut folder = Folder::new(&query.workspace_path);
    let mut here = |cwd: &str| folder.matches(cwd);
    let rows = acp::hermes_rows(&database, &mut here, query.oldest(), query.maximum_scanned.min(2_000), !query.head_only, cancel);
    listed("hermes", &database, rows, query, cancel)
}

/// `Listed`: a database's rows as the list's — open panes' sessions and ones without a request left out, automated ones hidden unless
/// asked for.
fn listed(
    provider: &str,
    database: &Path,
    rows: Option<Vec<acp::Row>>,
    query: &ResumableSessionQuery,
    cancel: &dyn Cancel,
) -> ResumableSessionListing {
    let mut listing = ResumableSessionListing::default();
    for row in rows.unwrap_or_default() {
        if listing.items.len() >= query.maximum_sessions || cancel.cancelled() {
            break;
        }
        let lowered = row.id.to_lowercase();
        if !wire::session_identifier(&row.id) || query.excluding.contains(&lowered) || row.requests == Some(0) {
            continue;
        }
        let title = row.prompt.clone().or_else(|| (!row.title.is_empty()).then(|| row.title.clone()));
        let automated = !query.known.contains(&lowered) && automated_prompt(row.prompt.as_deref());
        if automated && !query.include_automated {
            listing.hidden += 1;
            continue;
        }
        listing.items.push(ResumableSession {
            provider: provider.into(),
            session_id: row.id,
            title: title.as_deref().map(one_line),
            modified: row.updated,
            requests: row.requests,
            model: row.model,
            path: database.to_path_buf(),
            automated,
        });
    }
    listing
}

// ── the known ids ───────────────────────────────────────────────────────────────────────────────────────────────────────

/// `KnownSessionIDs`: session ids the app's own panes started or resumed, oldest first, kept in `known-sessions.json` in the app's data
/// folder; the session list never hides them as automated runs.
pub mod known {
    use std::path::Path;

    use mighty_core::wire;

    pub const MAXIMUM: usize = 2_000;
    const MAXIMUM_FILE_BYTES: u64 = 256 * 1_024;
    pub const FILE_NAME: &str = "known-sessions.json";

    fn last(ids: Vec<String>) -> Vec<String> {
        let skip = ids.len().saturating_sub(MAXIMUM);
        ids.into_iter().skip(skip).collect()
    }

    pub fn load(path: &Path) -> Vec<String> {
        let Ok(meta) = std::fs::symlink_metadata(path) else { return vec![] };
        if !meta.is_file() || meta.file_type().is_symlink() || meta.len() > MAXIMUM_FILE_BYTES {
            return vec![];
        }
        let Ok(bytes) = std::fs::read(path) else { return vec![] };
        let Ok(ids) = serde_json::from_slice::<Vec<String>>(&bytes) else { return vec![] };
        last(ids.into_iter().filter(|id| wire::session_identifier(id)).collect())
    }

    /// `Adding`: the list with the id added as its newest entry, dropping the oldest past the limit; None when nothing changes.
    pub fn adding(id: &str, ids: &[String]) -> Option<Vec<String>> {
        if !wire::session_identifier(id)
            || ids.iter().any(|known| known.eq_ignore_ascii_case(id) || known.to_lowercase() == id.to_lowercase())
        {
            return None;
        }
        let mut next = ids.to_vec();
        next.push(id.to_string());
        Some(last(next))
    }

    /// The list written whole to a temporary file beside `path` and moved over it. A temporary already there — left by a save that
    /// stopped, or put there as a link — goes first, and the new one is created, never opened through a link.
    pub fn save(ids: &[String], path: &Path) -> std::io::Result<()> {
        use std::io::Write as _;
        if let Some(parent) = path.parent() {
            std::fs::create_dir_all(parent)?;
        }
        let temporary = path.with_extension("json.tmp");
        if std::fs::symlink_metadata(&temporary).is_ok() {
            // A link to a folder is removed as a folder on Windows.
            std::fs::remove_file(&temporary).or_else(|_| std::fs::remove_dir(&temporary))?;
        }
        let kept: Vec<&String> = ids.iter().skip(ids.len().saturating_sub(MAXIMUM)).collect();
        let mut file = std::fs::OpenOptions::new().write(true).create_new(true).open(&temporary)?;
        file.write_all(&serde_json::to_vec(&kept).unwrap_or_default())?;
        drop(file);
        std::fs::rename(&temporary, path)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn titles_are_one_line() {
        assert_eq!(one_line("one\n two   three"), "one two three");
        assert_eq!(one_line(&"x".repeat(300)).chars().count(), 201);
    }

    /// A record's folder is decided once a listing, and a share the workspace is not on is never resolved (W4 review C11).
    #[test]
    fn a_folder_is_decided_once_and_another_share_never_resolved() {
        let workspace = if crate::windows_paths() { "C:\\work" } else { "/work" };
        let mut folder = Folder::new(workspace);
        let inside = if crate::windows_paths() { "C:\\work\\" } else { "/work/" };
        assert!(folder.matches(inside) && folder.matches(inside), "the workspace folder, with a trailing separator");
        assert_eq!(folder.seen.len(), 1, "decided once");
        if crate::windows_paths() {
            let paths = vec!["C:\\work".to_string()];
            assert!(on_another_share("\\\\offline\\share\\work", &paths), "a share the workspace is not on");
            assert!(on_another_share("\\\\?\\UNC\\offline\\share", &paths), "a device path");
            assert!(!on_another_share("D:\\work", &paths), "a local folder is resolved as before");
            let shared = vec!["\\\\server\\share\\work".to_string()];
            assert!(!on_another_share("\\\\SERVER\\share\\other", &shared), "the share the workspace lies on");
            assert!(!folder.matches("\\\\offline\\share\\work"), "not the workspace, and never asked");
        }
    }

    #[test]
    fn search_ignores_case_width_and_accents() {
        let item = |title: &str| ResumableSession {
            provider: "claude".into(),
            session_id: "s".into(),
            title: Some(title.into()),
            modified: DateTimeOffset::from_utc_ticks(0),
            requests: None,
            model: None,
            path: PathBuf::new(),
            automated: false,
        };
        let items = vec![item("newer"), item("older"), item("Ｃａｆé login")];
        assert_eq!(filter(&items, "NEW").len(), 1);
        assert_eq!(filter(&items, " ").len(), 3);
        assert_eq!(filter(&items, "cafe LOGIN").len(), 1);
        // A Hangul syllable is one letter: "하" is not inside "한글", "한" and "글" are (W4 review C9).
        let korean = vec![item("한글 정리"), item("하루 계획")];
        assert_eq!(filter(&korean, "하").iter().map(|i| i.title.clone().unwrap()).collect::<Vec<_>>(), ["하루 계획"]);
        assert_eq!(filter(&korean, "한").len(), 1);
        assert_eq!(filter(&korean, "글 정").len(), 1);
    }
}
