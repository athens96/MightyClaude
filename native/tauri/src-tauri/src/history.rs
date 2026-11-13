//! Session history and resume in the app (C# MainWindow.SessionHistory.cs, MainWindow.ResumeSession.cs and the web shell's
//! `WebResumeRows`, `WebPickResumedSession`, MainWindow.WebShell.Shell.cs:268-317), over mighty-history:
//!
//! - The Mighty view's older requests: each pane keeps a `SessionHistoryState` here (C# `PaneView.graphHistory`, in memory only, never
//!   saved, dropped when the pane closes). [`older`] is `GraphHistoryRuns` and the history block's words for the projection
//!   (`mighty.rs`); [`load_older`] is `LoadOlderGraphHistory`: the next chunk read off the page's thread and handed back.
//! - The resume list's data: `resumeRows{search, all}` answers the page's list sheet from the folder's sessions, read off the page's thread
//!   (again when "모든 세션 보기" changed). The sheets themselves (`resumeChoice`, `resumeList`) are the page-drawn dialogs' flow; it calls
//!   [`look_up`] and [`asks_resume`] for the choice, [`open_resume_list`] before it pushes the list, then [`close_resume_list`] and
//!   [`pick`] with the answered id, and adds the pane shaped by `mighty_history::resumable::apply`.
//! - The known sessions (`known-sessions.json`, C# `DesktopService.KnownSessions` / `RememberSession`): read once, added to when a pane
//!   resumes or is picked; written only when the app may write its profile (`--allow-state-write`).

use std::collections::{HashMap, HashSet};
use std::path::PathBuf;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex, OnceLock};

use mighty_core::graph::MightyGraphRun;
use mighty_core::model::{RunSession, Workspace};
use mighty_graph::view::{MightyPane, WebMightyHistory, shows_history_block};
use mighty_history::resumable::{self, known};
use mighty_history::{
    ResumableSession, ResumableSessionListing, ResumableSessionQuery, SessionHistoryRequest, SessionHistoryState, history,
};
use serde_json::{Value, json};
use tauri::Manager;

use crate::host::{Host, Registry, View, lock};
use crate::methods::{flag, text};

/// One pane's loaded history (C# `graphHistory`, `graphHistoryRetained`, `graphHistoryLoad`).
#[derive(Default)]
struct PaneHistory {
    state: SessionHistoryState,
    retained: Vec<MightyGraphRun>,
    load: Option<Arc<AtomicBool>>,
}

/// The resume list the page shows (C# `webResume`): its agent and workspace, what was read for it and whether every session was asked for.
struct ResumeList {
    serial: u64,
    provider: String,
    workspace: Workspace,
    listing: Option<ResumableSessionListing>,
    all: bool,
}

#[derive(Default)]
pub struct History {
    panes: Mutex<HashMap<String, PaneHistory>>,
    known: Mutex<Option<Vec<String>>>,
    resume: Mutex<Option<ResumeList>>,
    serial: Mutex<u64>,
    /// `PaneView.HistoryHomeOverride`: the smoke reads its own fixture records instead of the user's.
    home_override: Mutex<Option<PathBuf>>,
}

/// The app's one `History` (made on first use).
pub fn of(host: &Host) -> Arc<History> {
    if let Some(history) = host.app.try_state::<Arc<History>>() {
        return history.inner().clone();
    }
    host.app.manage(Arc::new(History::default()));
    host.app.state::<Arc<History>>().inner().clone()
}

/// Whether the app was started with `--allow-state-write` (it may write `known-sessions.json` in the profile).
fn writes_allowed() -> bool {
    static ALLOWED: OnceLock<bool> = OnceLock::new();
    *ALLOWED.get_or_init(|| crate::args::parse(std::env::args().skip(1)).map(|o| o.allow_state_write).unwrap_or(false))
}

impl History {
    /// The smoke's own records (C# `HistoryHomeOverride`).
    pub fn set_home_override(&self, home: Option<PathBuf>) {
        *lock(&self.home_override) = home;
    }

    fn home(&self) -> PathBuf {
        lock(&self.home_override).clone().unwrap_or_else(history::user_home)
    }

    /// `HistoryEnvironment`: the CLI home variables to read records under. The smoke's own home stands alone: the runner's
    /// CLAUDE_CONFIG_DIR or CODEX_HOME must never send it to real records.
    fn environment(&self) -> HashMap<String, String> {
        let mut values = HashMap::new();
        if lock(&self.home_override).is_some() {
            return values;
        }
        for key in ["CLAUDE_CONFIG_DIR", "CODEX_HOME"] {
            if let Ok(value) = std::env::var(key)
                && !value.is_empty()
            {
                values.insert(key.to_string(), value);
            }
        }
        values
    }
}

// ── the Mighty view's older requests ────────────────────────────────────────────────────────────────────────────────────

/// `GraphHistoryRuns(pane, retained)` and the history block (`WebMightyModel`): the loaded runs that still attach above the retained
/// ones (a trim moves the runs it drops into the history; history from another session is not shown), and the block's words while the
/// pane has a record to read. Panes that left the state let their history go (`ForgetGraphHistory`).
pub fn older(host: &Host, view: &View, pane: &RunSession) -> (Vec<MightyGraphRun>, Option<WebMightyHistory>) {
    let history = of(host);
    let mut panes = lock(&history.panes);
    let alive: HashSet<&str> = view.snapshot.sessions().iter().map(|s| s.id()).collect();
    panes.retain(|id, entry| {
        let keep = alive.contains(id.as_str());
        if !keep && let Some(load) = &entry.load {
            load.store(true, Ordering::SeqCst);
        }
        keep
    });
    let retained = pane.graph_runs();
    let entry = panes.entry(pane.id().to_string()).or_default();
    let previous = std::mem::take(&mut entry.retained);
    entry.state.follow(&previous, retained, pane.resume_id.as_deref(), pane.provider());
    entry.retained = retained.to_vec();
    let first = retained.first().map(|r| r.id());
    let connects = entry.state.connects(first, pane.resume_id.as_deref());
    let runs = if connects { entry.state.runs.clone() } else { vec![] };
    let block = shows_history_block(pane.provider(), retained, pane.resume_id.as_deref()).then(|| {
        let fresh = SessionHistoryState::new();
        let state = if connects { &entry.state } else { &fresh };
        WebMightyHistory {
            text: state.block_text(runs.len(), &host.locale),
            acts: state.block_acts(),
            phase: state.phase.name().to_string(),
        }
    });
    (runs, block)
}

/// The older runs a pane holds now (for `mightyImages` and a block's size), without following a trim.
pub fn loaded(host: &Host, pane: &RunSession) -> Vec<MightyGraphRun> {
    let history = of(host);
    let panes = lock(&history.panes);
    match panes.get(pane.id()) {
        Some(entry) if entry.state.connects(pane.graph_runs().first().map(|r| r.id()), pane.resume_id.as_deref()) => {
            entry.state.runs.clone()
        }
        _ => vec![],
    }
}

/// `LocalPath(Workspace)`: the pane's folder on this computer; none for a remote workspace's pane. (A delegated child's runs work in its
/// worktree, `DelegationPanes.RunWorkspace`: W7's.)
fn folder_of(view: &View, pane: &RunSession) -> Option<String> {
    view.snapshot.workspace(pane.workspace_id()).filter(|w| w.remote.is_none()).map(|w| w.path().to_string())
}

/// `LoadOlderGraphHistory`: reads the next older chunk off the page's thread and puts its requests above the ones already shown. A load
/// in flight, or a record whose start is on screen, does nothing; a record replaced under the cursor starts over from its end.
pub fn load_older(host: &Arc<Host>, view: &mut View, pane_id: &str) {
    let Some(pane) = view.snapshot.session(pane_id).cloned() else { return };
    let Some(request) = MightyPane::load_older(&pane, folder_of(view, &pane).as_deref()) else { return };
    let history = of(host);
    let mut template = SessionHistoryRequest::new(&request.provider, &request.resume_id, &request.folder);
    template.environment = history.environment();
    template.home = history.home();
    let (next, generation, cancel) = {
        let mut panes = lock(&history.panes);
        let entry = panes.entry(pane_id.to_string()).or_default();
        let next = entry.state.begin(
            request.first_retained.as_deref(),
            pane.resume_id.as_deref(),
            SessionHistoryState::anchor_for(&pane),
            &template,
        );
        let Some(next) = next else {
            view.mighty.dirty(pane_id);
            return;
        };
        if let Some(previous) = entry.load.take() {
            previous.store(true, Ordering::SeqCst);
        }
        let cancel = Arc::new(AtomicBool::new(false));
        entry.load = Some(cancel.clone());
        (next, entry.state.generation, cancel)
    };
    view.mighty.dirty(pane_id);
    let (host, id, locale) = (host.clone(), pane_id.to_string(), host.locale);
    tauri::async_runtime::spawn_blocking(move || {
        let result = history::load(&next, &locale, cancel.as_ref());
        if cancel.load(Ordering::SeqCst) {
            return;
        }
        let restart = {
            let history = of(&host);
            let mut panes = lock(&history.panes);
            let Some(entry) = panes.get_mut(&id) else { return };
            if entry.load.as_ref().is_some_and(|l| Arc::ptr_eq(l, &cancel)) {
                entry.load = None;
            }
            entry.state.finish(result, generation)
        };
        let mut view = host.view();
        if view.snapshot.session(&id).is_none() {
            return;
        }
        view.mighty.dirty(&id);
        // The record was replaced under the cursor: start over from its end.
        if restart {
            load_older(&host, &mut view, &id);
        }
    });
}

// ── resume ──────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// `KnownSessions`: the ids the app's own panes started or resumed, lowercased.
fn known_sessions(host: &Host) -> HashSet<String> {
    let history = of(host);
    let mut known = lock(&history.known);
    known.get_or_insert_with(|| known::load(&host.data_dir.join(known::FILE_NAME))).iter().map(|id| id.to_lowercase()).collect()
}

/// `RememberSession`: a session id a pane started or resumed is never hidden as an automated run again.
pub fn remember_session(host: &Host, id: &str) {
    let history = of(host);
    let mut known = lock(&history.known);
    let path = host.data_dir.join(known::FILE_NAME);
    let list = known.get_or_insert_with(|| known::load(&path));
    let Some(next) = known::adding(id, list) else { return };
    *list = next.clone();
    if writes_allowed()
        && let Err(error) = known::save(&next, &path)
    {
        host.log.line(&format!("known-sessions.json: {error}"));
    }
}

/// `ResumeQuery(workspace)`: the folder's records under the CLI homes, open panes' sessions left out, the app's own never hidden.
fn query(host: &Host, view: &View, workspace: &Workspace) -> ResumableSessionQuery {
    let history = of(host);
    let mut environment = history.environment();
    // opencode's and Hermes's databases move with these as the CLIs' own do; the smoke's own home stands alone.
    if lock(&history.home_override).is_none() {
        for key in resumable::STORE_VARIABLES {
            if let Ok(value) = std::env::var(key)
                && !value.is_empty()
            {
                environment.insert(key.to_string(), value);
            }
        }
    }
    let mut query = ResumableSessionQuery::new(workspace.path());
    query.environment = environment;
    query.home = history.home();
    query.excluding = resumable::in_use(view.snapshot.sessions().iter().map(|s| s.as_ref()));
    query.known = known_sessions(host);
    query
}

/// `AddAgentPane`'s look-up: whether the folder has a session of this agent to continue (the head of one record only, nested runs
/// hidden, open panes' sessions left out), read off the page's thread. Any failure reading the records means nothing to offer.
#[allow(dead_code, reason = "the add-pane flow (the page-drawn resumeChoice sheet) calls it; that flow is the workspace dialogs' stage")]
pub async fn look_up(host: &Arc<Host>, provider: &str, workspace: &Workspace) -> ResumableSessionListing {
    let mut query = {
        let view = host.view();
        query(host, &view, workspace)
    };
    query.head_only = true;
    query.maximum_sessions = 1;
    let provider = provider.to_string();
    tauri::async_runtime::spawn_blocking(move || resumable::listing_of(&query, &provider, &mighty_history::Never)).await.unwrap_or_default()
}

/// `AddPaneMenu.AsksResume` with the open panes read now (a pane may have taken one while the look-up ran).
#[allow(dead_code, reason = "the add-pane flow (the page-drawn resumeChoice sheet) calls it; that flow is the workspace dialogs' stage")]
pub fn asks_resume(view: &View, provider: &str, listing: &ResumableSessionListing) -> bool {
    let in_use = resumable::in_use(view.snapshot.sessions().iter().map(|s| s.as_ref()));
    resumable::asks_resume(provider, &listing.items, &in_use)
}

/// `WebPickResumedSession`'s start: the list the page is about to show is this agent's for this workspace.
pub fn open_resume_list(host: &Host, provider: &str, workspace: &Workspace) {
    let history = of(host);
    let serial = {
        let mut serial = lock(&history.serial);
        *serial += 1;
        *serial
    };
    *lock(&history.resume) =
        Some(ResumeList { serial, provider: provider.into(), workspace: workspace.clone(), listing: None, all: false });
}

/// The list was answered or closed: what it showed last (`listing = webResume?.Listing; webResume = null`).
pub fn close_resume_list(host: &Host) -> Option<ResumableSessionListing> {
    lock(&of(host).resume).take().and_then(|open| open.listing)
}

/// The pick the page answered from `listing`: the session, remembered as the app's own, or the words why not (taken by another pane
/// while the list was open: never two panes on one session). None for an id the list did not show.
pub fn pick(host: &Host, view: &View, listing: &ResumableSessionListing, session_id: &str) -> Option<Result<ResumableSession, String>> {
    let session = listing.items.iter().find(|i| i.session_id == session_id)?.clone();
    if resumable::in_use(view.snapshot.sessions().iter().map(|s| s.as_ref())).contains(&session.session_id.to_lowercase()) {
        return Some(Err(host.locale.get("resume.error.inUse")));
    }
    remember_session(host, &session.session_id);
    Some(Ok(session))
}

/// `WebResumeRows`: the resume list's rows for the page's search, read again when "모든 세션 보기" changed.
async fn resume_rows(host: Arc<Host>, search: String, all: bool) -> Result<Value, String> {
    let history = of(&host);
    let (serial, provider, workspace, cached, cached_all) = {
        let open = lock(&history.resume);
        let Some(open) = open.as_ref() else { return Ok(Value::Null) };
        (open.serial, open.provider.clone(), open.workspace.clone(), open.listing.clone(), open.all)
    };
    let listing = match cached {
        Some(listing) if cached_all == all => listing,
        _ => {
            let mut query = {
                let view = host.view();
                query(&host, &view, &workspace)
            };
            query.include_automated = all;
            let reading = provider.clone();
            let listing = tauri::async_runtime::spawn_blocking(move || resumable::listing_of(&query, &reading, &mighty_history::Never))
                .await
                .unwrap_or_default();
            let mut open = lock(&history.resume);
            match open.as_mut().filter(|o| o.serial == serial) {
                Some(open) => {
                    open.listing = Some(listing.clone());
                    open.all = all;
                }
                None => return Ok(Value::Null),
            }
            listing
        }
    };
    let locale = host.locale;
    let now = mighty_core::time::DateTimeOffset::now();
    let excluding = {
        let view = host.view();
        resumable::in_use(view.snapshot.sessions().iter().map(|s| s.as_ref()))
    };
    let shown: Vec<ResumableSession> =
        listing.items.iter().filter(|i| !excluding.contains(&i.session_id.to_lowercase())).cloned().collect();
    let items = resumable::filter(&shown, &search);
    let warning = locale.get("resume.recentlyModified");
    let rows: Vec<Value> = items
        .iter()
        .map(|item| {
            let spoken = resumable::details(item, now, &locale);
            let busy = resumable::may_be_running(item, now);
            let suffix = format!(" · {warning}");
            let details = if busy && spoken.ends_with(&suffix) { spoken[..spoken.len() - suffix.len()].to_string() } else { spoken };
            json!({
                "id": item.session_id,
                "provider": item.provider,
                "title": resumable::row_title(item, &locale),
                "untitled": item.title.is_none(),
                "beta": mighty_core::shell::is_beta(&item.provider),
                "details": details,
                "busy": busy,
                "help": item.title.clone().unwrap_or_else(|| item.session_id.clone()),
            })
        })
        .collect();
    let mut result = serde_json::Map::new();
    result.insert("rows".into(), Value::Array(rows.clone()));
    if listing.hidden > 0 && !all {
        result.insert("hidden".into(), json!(locale.get_with("resume.hiddenCount", &[("count", &listing.hidden.to_string())])));
    }
    if rows.is_empty() && search.trim().is_empty() {
        result.insert("empty".into(), json!(locale.get("resume.empty")));
        let label = mighty_core::model_label::provider_label(&provider);
        result.insert("reason".into(), json!(locale.get_with("resume.emptyReason", &[("provider", label)])));
    } else if rows.is_empty() {
        result.insert("empty".into(), json!(locale.get("resume.noMatch")));
    }
    Ok(Value::Object(result))
}

pub fn register(methods: &mut Registry) {
    methods.add("resumeRows", |host: Arc<Host>, args| async move {
        let search = text(&args, "search").unwrap_or("").to_string();
        resume_rows(host, search, flag(&args, "all")).await
    });
    crate::workmap::register(methods);
}
