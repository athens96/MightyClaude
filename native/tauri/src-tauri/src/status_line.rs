//! The status line per pane: refresher, runner, trust (C# MainWindow.StatusLine.cs:12-309, MainWindow.WebShell.Cards.cs:599-624).
//! Owner: W6-B.
//!
//! Each Claude agent pane with a view, in a workspace on this PC, while the status line is on (C# `StatusLineEligible`), has a
//! mighty-tools `StatusLineRefresher`: the settings read fresh for each run (the workspace's, then the user's), a workspace command
//! run only once the user allowed it for that workspace (the user's runs meanwhile), one command at a time, at most every 2 s. The
//! status line's clock (every 500 ms) follows the state: a pane that became eligible gets its refresher and a reading; one that is
//! not any longer loses it (its command let go before another starts); a pane whose record changed asks again (C#'s `Refresh`); and
//! outside a smoke each pane shown in the active workspace's layout asks by itself every 10 s while its run goes on and every 60 s at
//! rest (`TickInterval`; C# ticks on-screen panes only). What the card shows is what was drawn last (C# `webStatusLine`): the
//! refresher's state as it changes, the trust answer's at once. The user's settings are `<home>\.claude`'s, as C# reads them (never
//! the CLI's `CLAUDE_CONFIG_DIR`).
//!
//! A smoke never reads the user's own settings: its home is the folder its step gives ([`smoke_home`]), else none.
//!
//! Locks: the status lines' lock is taken after the state's (the cards read it with the state locked), never before.

use std::collections::{HashMap, HashSet};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, Mutex, MutexGuard};
use std::time::{Duration, Instant};

use mighty_composer::cards::StatusLineInput;
use mighty_core::model::{AppSnapshot, RunSession};
use mighty_tools::status_line::refresher::{self, StatusLineRefresher};
use mighty_tools::status_line::{StatusLineConfig, StatusLineContext, StatusLineResult, card, config, run, trust};
use tauri::Manager;

use crate::host::{Host, View};

/// The clock's pace.
const CLOCK: Duration = Duration::from_millis(500);

/// What the card shows of a pane (C# `RenderStatusLine`'s arguments).
#[derive(Clone, Default)]
struct Shown {
    config: Option<StatusLineConfig>,
    untrusted: Option<StatusLineConfig>,
    result: Option<StatusLineResult>,
}

/// One eligible pane's status line.
struct Line {
    refresher: Arc<StatusLineRefresher>,
    workspace_id: String,
    folder: String,
    shown: Shown,
    /// The pane's record the last reading was asked for.
    seen: Arc<RunSession>,
    asked_at: Instant,
}

#[derive(Default)]
struct StatusLines {
    lines: Mutex<HashMap<String, Line>>,
    /// Refreshers closed whose command may still run: a new one for the same pane waits for it (C# `statusLineStopping`).
    stopping: Mutex<HashMap<String, Arc<StatusLineRefresher>>>,
    clock: AtomicBool,
    /// Turned each time the switch is set (set_enabled): a pass that read the state before makes nothing new.
    switched: AtomicU64,
    /// A smoke's home folder (its step's), in place of the user's.
    smoke_home: Mutex<Option<PathBuf>>,
}

fn lock<T>(mutex: &Mutex<T>) -> MutexGuard<'_, T> {
    mutex.lock().unwrap_or_else(|poisoned| poisoned.into_inner())
}

fn of(host: &Host) -> Arc<StatusLines> {
    if let Some(lines) = host.app.try_state::<Arc<StatusLines>>() {
        return lines.inner().clone();
    }
    host.app.manage(Arc::new(StatusLines::default()));
    host.app.state::<Arc<StatusLines>>().inner().clone()
}

/// The home the settings are read from: the user's, or in a smoke its step's folder (none before it gives one).
fn home(host: &Host) -> Option<PathBuf> {
    if host.smoke.is_some() { lock(&of(host).smoke_home).clone() } else { Some(mighty_history::history::user_home()) }
}

/// C# `LocalPath(workspace)`: the folder of a workspace on this PC.
fn local_path(snapshot: &AppSnapshot, workspace_id: &str) -> Option<String> {
    snapshot.workspace(workspace_id).filter(|w| w.remote.is_none()).map(|w| w.path().to_string())
}

/// C# `BuildStatusLineContext`: the pane's session, folder, model, usage and settings as the command reads them.
fn context(host: &Host, pane: &RunSession, folder: &str) -> StatusLineContext {
    let runtime = host.runs().and_then(|runs| runs.runtime(pane.provider()));
    let catalog =
        host.runs().map(|runs| runs.catalog_of(pane.provider())).unwrap_or_else(|| mighty_run::providers::fallback(pane.provider()));
    let (model_id, model_name) = card::model_label(pane, &catalog, &host.locale);
    let elapsed = pane.run_timing.as_ref().map(|timing| (mighty_run::timing::elapsed(timing, None) * 1000.0).max(0.0) as i64);
    let home = home(host).unwrap_or_default();
    // C# passes no environment (MainWindow.StatusLine.cs:135): always `<home>\.claude`, never the CLI's CLAUDE_CONFIG_DIR.
    let config_dir = config::config_dir(Some(&home), None).unwrap_or_else(|| home.join(".claude"));
    card::context(
        pane,
        &card::ContextInputs {
            folder: Some(folder),
            model_id: &model_id,
            model_name: &model_name,
            version: runtime.as_ref().and_then(|r| r.version.as_deref()),
            elapsed_ms: elapsed,
            config_dir: &config_dir,
        },
    )
}

/// C# `InitRefresher`: the pane's refresher, its settings read from the workspace's folder and the home, the trusted commands from
/// the state, its commands run through the host's `ProcessHost`; a change draws the card again.
fn refresher(host: &Arc<Host>, pane: &str, workspace_id: &str, folder: &str) -> Arc<StatusLineRefresher> {
    let (reader, folder_path) = (host.clone(), PathBuf::from(folder));
    // The settings files read where the run task stands, as blocking work the runtime moves its other tasks away from; no environment,
    // as C# reads them (MainWindow.StatusLine.cs:64): `<home>\.claude`, never CLAUDE_CONFIG_DIR.
    let discover: refresher::Discover = Arc::new(move || {
        let home = home(&reader);
        tokio::task::block_in_place(|| config::discover(Some(&folder_path), home.as_deref(), None))
    });
    let state = host.clone();
    let trusted: refresher::Trusted = Arc::new(move || state.view().snapshot.trusted_status_lines.clone());
    let processes = host.processes.clone();
    let runner: refresher::Runner = Arc::new(move |config, context, cancel| {
        let processes = processes.clone();
        Box::pin(async move { run::run(processes.as_ref(), &config, &context, cfg!(windows), run::DEFAULT_TIMEOUT, cancel).await })
    });
    let refresher = StatusLineRefresher::new(discover, trusted, workspace_id, runner, None, tauri::async_runtime::handle().inner().clone());
    let (drawer, id, weak) = (host.clone(), pane.to_string(), Arc::downgrade(&refresher));
    refresher.on_change(Arc::new(move || {
        let Some(refresher) = weak.upgrade() else { return };
        {
            let lines = of(&drawer);
            let mut lines = lock(&lines.lines);
            let Some(line) = lines.get_mut(&id).filter(|line| Arc::ptr_eq(&line.refresher, &refresher)) else { return };
            line.shown = Shown { config: refresher.config(), untrusted: refresher.untrusted(), result: refresher.result() };
        }
        drawer.panes_changed();
    }));
    refresher
}

/// The pane views eligible for a status line, each `(id, record, workspace, folder)`, and the panes shown in the active workspace's
/// layout (each tab group's selected pane).
#[allow(clippy::type_complexity)]
fn eligible_panes(host: &Host) -> (Vec<(String, Arc<RunSession>, String, String)>, HashSet<String>) {
    let mut view = host.view();
    let View { snapshot, layout_defaults, panes, .. } = &mut *view;
    let eligible = snapshot
        .sessions()
        .iter()
        .filter(|pane| panes.contains_key(pane.id()))
        .filter_map(|pane| {
            let folder = local_path(snapshot, pane.workspace_id());
            card::eligible(snapshot, pane, folder.as_deref())
                .then(|| (pane.id().to_string(), pane.clone(), pane.workspace_id().to_string(), folder.unwrap_or_default()))
        })
        .collect();
    let active = snapshot.active_workspace_id.clone().unwrap_or_default();
    let shown = mighty_core::shell::effective_layout(snapshot, &active, layout_defaults)
        .map(|root| {
            mighty_core::layout::groups(&root)
                .into_iter()
                .filter_map(|group| group.selected_session_id.clone().or_else(|| group.session_ids().next().map(str::to_string)))
                .collect()
        })
        .unwrap_or_default();
    (eligible, shown)
}

/// One pass of the clock (C#'s `RequestStatusLineRefresh` for every pane view, and its tick).
fn pass(host: &Arc<Host>) {
    let lines = of(host);
    let switched = lines.switched.load(Ordering::SeqCst);
    let (eligible, shown) = eligible_panes(host);
    let mut closed = false;
    let mut asks = vec![];
    {
        let mut map = lock(&lines.lines);
        // The switch was turned since the panes were read (set_enabled): this pass makes nothing new from what it read then, and
        // the pass set_enabled runs reads the state again (the state's lock comes before this one, so it is not read here).
        let stale = lines.switched.load(Ordering::SeqCst) != switched;
        let mut stopping = lock(&lines.stopping);
        stopping.retain(|_, refresher| !refresher.is_stopped());
        map.retain(|id, line| {
            let keep =
                eligible.iter().any(|(pane, _, workspace, folder)| pane == id && *workspace == line.workspace_id && *folder == line.folder);
            if !keep {
                line.refresher.close();
                stopping.insert(id.clone(), line.refresher.clone());
                closed = true;
            }
            keep
        });
        let now = Instant::now();
        for (id, pane, workspace, folder) in &eligible {
            match map.get_mut(id) {
                Some(line) => {
                    // The timed reading only for a pane shown in the active workspace's layout (C# ticks on-screen panes only).
                    let running = pane.status() == "running";
                    let ticked = host.smoke.is_none()
                        && shown.contains(id)
                        && now.duration_since(line.asked_at) >= refresher::tick_interval(running);
                    if ticked || !Arc::ptr_eq(&line.seen, pane) {
                        line.seen = pane.clone();
                        line.asked_at = now;
                        asks.push((line.refresher.clone(), pane.clone(), folder.clone(), false));
                    }
                }
                None if stale || stopping.contains_key(id) => {}
                None => {
                    let refresher = refresher(host, id, workspace, folder);
                    asks.push((refresher.clone(), pane.clone(), folder.clone(), true));
                    let line = Line {
                        refresher,
                        workspace_id: workspace.clone(),
                        folder: folder.clone(),
                        shown: Shown::default(),
                        seen: pane.clone(),
                        asked_at: now,
                    };
                    map.insert(id.clone(), line);
                }
            }
        }
    }
    for (refresher, pane, folder, force) in asks {
        refresher.request_refresh(context(host, &pane, &folder), force);
    }
    if closed {
        host.panes_changed();
    }
}

/// The clock, started once.
fn start(host: &Host) {
    let lines = of(host);
    if lines.clock.swap(true, Ordering::SeqCst) {
        return;
    }
    let Some(host) = host.app.try_state::<Arc<Host>>().map(|host| host.inner().clone()) else {
        lines.clock.store(false, Ordering::SeqCst);
        return;
    };
    std::thread::Builder::new()
        .name("status-line-clock".into())
        .spawn(move || {
            while !host.closing() {
                std::thread::sleep(CLOCK);
                pass(&host);
            }
            for line in lock(&of(&host).lines).values() {
                line.refresher.close();
            }
        })
        .expect("the status line clock");
}

/// `status_line::set_enabled`: C# `SetStatusLineEnabled` — Settings › 화면's switch and the composer's toggle: the setting saved, each
/// pane's refresher started (a reading asked for at once) or stopped.
#[allow(dead_code, reason = "a seam W6-A1 (Settings and the composer's toggle) calls")]
pub fn set_enabled(host: &Arc<Host>, on: bool) {
    let saved = host.view().update(|state| state.status_line_enabled = on);
    of(host).switched.fetch_add(1, Ordering::SeqCst);
    if let Err(error) = saved {
        host.log.line(&format!("status line: the setting was not saved: {error}"));
    }
    start(host);
    pass(host);
    host.panes_changed();
}

/// `statusLineTrust{paneId, allow}`: C# `WebStatusLineTrust` — Allow records the workspace command's fingerprint in the state (an
/// edited command asks again), draws the card without the question at once and reads the line again; Not now clears the question.
/// Nothing without a question waiting.
pub fn trust_answer(host: &Arc<Host>, pane: &str, allow: bool) -> Result<(), String> {
    let lines = of(host);
    let (untrusted, refresher, workspace, folder) = {
        let map = lock(&lines.lines);
        let Some(line) = map.get(pane) else { return Ok(()) };
        let Some(untrusted) = line.shown.untrusted.clone() else { return Ok(()) };
        (untrusted, line.refresher.clone(), line.workspace_id.clone(), line.folder.clone())
    };
    if allow {
        host.view().update(|state| trust::trust(state, &untrusted, &workspace))?;
        if let Some(line) = lock(&lines.lines).get_mut(pane) {
            line.shown = Shown { config: Some(untrusted), untrusted: None, result: None };
        }
        let session = host.view().snapshot.session(pane).cloned();
        if let Some(session) = session {
            refresher.request_refresh(context(host, &session, &folder), true);
        }
    } else {
        refresher.dismiss();
        if let Some(line) = lock(&lines.lines).get_mut(pane) {
            line.shown.untrusted = None;
        }
    }
    host.panes_changed();
    Ok(())
}

/// `status_line::input`: what the pane's status line card shows (C# `RenderStatusLine`'s state), for `CardInputs.status_line`. Called
/// while the cards are made, the state locked: it reads the status line's own state, never `host.view()`. The first call starts the
/// clock.
pub fn input(host: &Host, pane: &RunSession) -> Option<StatusLineInput> {
    start(host);
    let lines = of(host);
    let map = lock(&lines.lines);
    let shown = &map.get(pane.id())?.shown;
    Some(card::input(shown.config.as_ref(), shown.untrusted.as_ref(), shown.result.as_ref()))
}

/// The smoke's home folder (its step's), or none again; the panes read their settings anew.
pub fn smoke_home(host: &Arc<Host>, folder: Option<&Path>) {
    *lock(&of(host).smoke_home) = folder.map(Path::to_path_buf);
    start(host);
}

/// What the smoke reads of a pane's status line: the command waiting for trust, and the rows' words.
pub fn smoke_state(host: &Host, pane: &str) -> serde_json::Value {
    let lines = of(host);
    let map = lock(&lines.lines);
    let shown = map.get(pane).map(|line| line.shown.clone()).unwrap_or_default();
    let rows: Vec<String> = shown
        .result
        .as_ref()
        .map(|result| result.lines.iter().map(|line| line.iter().map(|segment| segment.text.as_str()).collect()).collect())
        .unwrap_or_default();
    serde_json::json!({
        "line": map.contains_key(pane),
        "config": shown.config.as_ref().map(|c| c.command.clone()),
        "untrusted": shown.untrusted.as_ref().map(|c| c.command.clone()),
        "rows": rows,
        "error": shown.result.as_ref().and_then(|r| r.error_text.clone()),
    })
}

/// The smoke's pane read again at once (its step edited the workspace's command).
pub fn smoke_refresh(host: &Arc<Host>, pane: &str) {
    let line = lock(&of(host).lines).get(pane).map(|line| (line.refresher.clone(), line.folder.clone()));
    let session = host.view().snapshot.session(pane).cloned();
    if let (Some((refresher, folder)), Some(session)) = (line, session) {
        refresher.request_refresh(context(host, &session, &folder), true);
    }
}
