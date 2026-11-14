//! `--smoke-test`: checks the page and the host together without anyone watching. Once the page has called `getLocale` and `ready`
//! and the host has pushed it the snapshot and the frame model, a probe script runs in the page: it waits for the sidebar's pane
//! titles, reads the DOM (pane titles, the selected pane, the theme, tabs, header, status bar), clicks another pane's sidebar row and
//! waits for the page to show it selected, then hands what it saw to the `smoke_probe` command.
//!
//! Then the page's steps, in this order ([`STEPS`]), each where its platform has what it checks:
//!
//! - the frame (smoke_frame.rs: the sidebar, a row click, the add menu's terminal entry, tab switches, the columns preset);
//! - where the platform draws browser panes natively, the browser surface (the NativeSlot protocol): the host adds a browser pane in a
//!   workspace of its own to the in-memory state and makes it active; a probe types the address of a local fixture page (served on
//!   127.0.0.1 by this process) into the pane's address field and waits until the native view shows it, checks that the view's real
//!   bounds (read back from the OS) are the slot's in device pixels, has the host push a page dialog and checks that the view hides
//!   under it and shows again once it closes, then the history's address, back, a refused popup and a menu over the view;
//! - where the page draws terminals, the terminal (smoke_terminal.rs: a shell pane's shell started, typed into through the page's xterm
//!   and its marker read back, the footer, the pane's status running and idle again, the pane closed);
//! - the files pane opened from the add menu, an HTML frame, the reference bubble and the picture viewer (smoke_files.rs);
//! - the work map and the resume flow end to end, from the add menu's Claude entry to the resumed pane's older requests (smoke_history.rs);
//! - with git on PATH, a clone through the Git sheet, then a rename sheet that survives a page reload (smoke_frame.rs).
//!
//! With `--smoke-fake-cli` an opencode run's question card answered from the page (smoke_acp.rs) and the run steps follow (smoke_runs.rs:
//! the composer, the permission and plan cards, the Mighty diagram, the progress panel and a stop, against mighty-run's stand-in CLI);
//! with `--smoke-real-claude` one real request to the installed
//! `claude` CLI follows the first probe instead of the page's steps.
//!
//! The host adds whether its own state took the click, the calls it saw and the methods it answered "unimplemented", and writes
//! `smoke-result.json`; with `--smoke-exit` the app then quits, 0 when the smoke passed, else 1. A smoke that has no result in time
//! (330 s, 480 s with the stand-in CLI, 420 s with the real one) fails.

use std::io::{Read, Write};
use std::net::TcpListener;
use std::path::PathBuf;
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use mighty_core::AppSnapshot;
use mighty_platform::SurfaceKind;
use serde_json::{Value, json};
use tauri::Manager;

use crate::host::{Host, WINDOW};
use crate::surface::SurfaceManager;

pub use crate::smoke_runs::RECORDING;

/// Which steps follow the first probe.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Mode {
    /// The page and, where the platform draws browsers natively, the browser surface.
    Page,
    /// Then the run steps against mighty-run's stand-in CLI.
    FakeClaude,
    /// The page, then one real request to the installed `claude` CLI.
    RealClaude,
}

/// Which probe's report comes next.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
enum Stage {
    #[default]
    Main,
    /// The frame's steps (smoke_frame.rs).
    Frame,
    Browser,
    /// The terminal step (smoke_terminal.rs), where the page draws terminals.
    Terminal,
    /// The files pane, an HTML frame, the reference bubble and the picture viewer (smoke_files.rs).
    Files,
    /// The work map and the resume list (smoke_history.rs).
    History,
    /// The Git sheet's clone of a local bare repository (smoke_frame.rs).
    Create,
    /// A rename sheet open while the page loads again: drawn again and answered (smoke_frame.rs).
    Reload,
    /// An opencode run's question card answered from the page (smoke_acp.rs), with the stand-in CLI.
    Acp,
    Runs,
}

/// The steps after the first probe, in their order: each one that can start does (`Smoke::start`), the run steps last.
const STEPS: [Stage; 9] =
    [Stage::Frame, Stage::Browser, Stage::Terminal, Stage::Files, Stage::History, Stage::Create, Stage::Reload, Stage::Acp, Stage::Runs];

const PROBE: &str = r#"(async () => {
  const until = async (test, ms) => { const end = performance.now() + ms; while (performance.now() < end) { const v = test(); if (v) return v; await new Promise((r) => setTimeout(r, 50)); } return test(); };
  const titles = () => [...document.querySelectorAll('.sidebar-pane-title')];
  const report = {};
  try {
    await until(() => titles().length > 0, 15000);
    const rows = [...document.querySelectorAll('[data-testid=sidebar-pane]')];
    const active = document.querySelector('.sidebar-pane.active');
    report.paneTitles = titles().length;
    report.sidebarRows = rows.length;
    report.workspaces = document.querySelectorAll('[data-testid=sidebar-workspace]').length;
    report.selectedTitle = active?.querySelector('.sidebar-pane-title')?.textContent ?? null;
    report.selectedId = active?.dataset.sessionId ?? null;
    report.theme = document.documentElement.dataset.theme ?? null;
    report.language = document.documentElement.lang || null;
    report.tabs = document.querySelectorAll('[data-testid=tab]').length;
    report.groups = document.querySelectorAll('[data-testid=tab-group]').length;
    report.header = document.querySelector('[data-testid=workspace-header]')?.textContent?.trim().slice(0, 200) ?? null;
    report.statusBar = document.querySelector('[data-testid=status-bar]')?.textContent?.trim().slice(0, 200) ?? null;
    const other = rows.find((row) => row.dataset.sessionId !== report.selectedId);
    if (other) {
      const id = other.dataset.sessionId;
      report.clickTarget = id;
      other.click();
      report.clickSelected = !!(await until(() => document.querySelector('.sidebar-pane.active')?.dataset.sessionId === id && document.querySelector('.pane-slot.active:not(.hidden)')?.dataset.paneId === id, 5000));
      report.activeSlot = document.querySelector('.pane-slot.active:not(.hidden)')?.dataset.paneId ?? null;
    }
    // A moment for the calls the newly shown pane makes, so the unimplemented list holds them too.
    await new Promise((r) => setTimeout(r, 1000));
  } catch (error) {
    report.error = String(error);
  }
  await window.__TAURI_INTERNALS__.invoke('smoke_probe', { probe: report });
})();"#;

/// The browser-surface step's probe; `__PANE__` and `__FIXTURE__` are filled in (both plain ASCII, no quotes).
const BROWSER_PROBE: &str = r#"(async () => {
  const until = async (test, ms) => { const end = performance.now() + ms; while (performance.now() < end) { const v = await test(); if (v) return v; await new Promise((r) => setTimeout(r, 50)); } return test(); };
  const invoke = (command, args) => window.__TAURI_INTERNALS__.invoke(command, args);
  const paneId = '__PANE__', fixture = '__FIXTURE__';
  const surface = () => invoke('smoke_surface', { paneId, action: 'inspect' });
  const report = { paneId, fixture };
  try {
    // This pane's slot, looked up again whenever it is measured (the page may draw it anew).
    const ofPane = (selector) => document.querySelector('.pane-slot[data-pane-id="' + paneId + '"] ' + selector);
    const slotNow = () => ofPane('[data-testid=browser-slot]');
    const slot = await until(slotNow, 15000);
    report.slot = !!slot;
    const address = await until(() => { const field = ofPane('[data-testid=browser-address]'); return field && !field.disabled ? field : null; }, 10000);
    report.addressField = !!address;
    // Typed as a person would: the field's value, its input event, then Enter (BrowserView's keyDown sends browserNavigate).
    Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set.call(address, fixture);
    address.dispatchEvent(new Event('input', { bubbles: true }));
    address.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
    const loaded = await until(async () => { const s = await surface(); return s && s.visible && (s.url || '').startsWith(fixture) ? s : null; }, 15000);
    report.loaded = !!loaded;
    report.url = loaded?.url ?? null;
    // The slot in device pixels as the page reports it (src/slot/bounds.ts), against the view's own bounds from the OS.
    await new Promise((r) => setTimeout(r, 300));
    const box = (slotNow() ?? slot).getBoundingClientRect(), ratio = window.devicePixelRatio || 1;
    const x = Math.round(box.left * ratio), y = Math.round(box.top * ratio);
    report.expected = { x, y, width: Math.round((box.left + box.width) * ratio) - x, height: Math.round((box.top + box.height) * ratio) - y };
    const placed = await surface();
    report.actual = placed?.bounds ?? null;
    report.ratio = ratio;
    report.boundsMatch = !!placed && ['x', 'y', 'width', 'height'].every((k) => Math.abs(placed.bounds[k] - report.expected[k]) <= 1);
    await invoke('smoke_surface', { paneId, action: 'dialog' });
    report.dialogShown = !!(await until(() => document.querySelector('[data-testid=dialog]'), 5000));
    report.hiddenUnderDialog = !!(await until(async () => { const s = await surface(); return s && !s.visible; }, 5000));
    await invoke('smoke_surface', { paneId, action: 'dialogClose' });
    report.dialogClosed = !!(await until(() => !document.querySelector('[data-testid=dialog]'), 5000));
    report.shownAfterDialog = !!(await until(async () => { const s = await surface(); return s && s.visible; }, 5000));
    // The address the field shows came back from the host's history as a push.
    const field = () => ofPane('[data-testid=browser-address]')?.value ?? '';
    report.navigated = !!(await until(() => field() === fixture, 10000));
    // A second page, then the page's back button: the host's history and the engine go back to the first.
    const second = fixture.replace('/fixture', '/second'), popup = fixture.replace('/fixture', '/popup');
    const go = (url) => { Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set.call(address, url); address.dispatchEvent(new Event('input', { bubbles: true })); address.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true })); };
    go(second);
    report.onSecond = !!(await until(() => field() === second, 10000));
    const back = await until(() => { const b = ofPane('button.browser-nav'); return b && !b.disabled ? b : null; }, 10000);
    back?.click();
    report.wentBack = report.onSecond && !!(await until(async () => { const s = await surface(); return field() === fixture && s && (s.url || '') === fixture; }, 10000));
    // window.open from a page: refused and counted, no window opens, and the page stays where it is.
    const before = await invoke('smoke_surface', { paneId, action: 'popups' });
    go(popup);
    report.popupPage = !!(await until(() => field() === popup, 10000));
    const after = await until(async () => { const p = await invoke('smoke_surface', { paneId, action: 'popups' }); return p.refused > before.refused ? p : null; }, 10000);
    const stayed = await surface();
    report.popups = after;
    report.popupBlocked = !!after && after.webviews === before.webviews && (stayed?.url || '') === popup;
    // A page menu over the slot (the tab's menu at a point inside it): the view steps aside, and comes back once the menu closes.
    const box2 = (slotNow() ?? slot).getBoundingClientRect();
    report.menuAt = { x: Math.round(box2.left + 20), y: Math.round(box2.top + 20) };
    const handle = document.querySelector('[data-testid=tab][data-tab-id="' + paneId + '"] .tab-handle');
    handle?.dispatchEvent(new MouseEvent('contextmenu', { bubbles: true, cancelable: true, button: 2, clientX: box2.left + 20, clientY: box2.top + 20 }));
    report.menuOpened = !!handle && !!(await until(() => document.querySelector('[data-testid=menu]'), 5000));
    report.steppedAside = report.menuOpened && !!(await until(async () => { const s = await surface(); return s && !s.visible; }, 5000));
    window.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    report.menuClosed = report.steppedAside && !!(await until(async () => { const s = await surface(); return !document.querySelector('[data-testid=menu]') && s && s.visible; }, 5000));
  } catch (error) {
    report.error = String(error);
  }
  await invoke('smoke_probe', { probe: report });
})();"#;

/// The fixture page the browser pane loads.
const FIXTURE: &str = "<!doctype html><html><head><meta charset=utf-8><title>Mighty smoke fixture</title></head>\
<body style=\"margin:0;background:#1f7a52;color:#fff;font:28px system-ui\"><p style=\"padding:24px\">Mighty Code browser surface fixture</p></body></html>";
/// The second page (`/second`), for back.
const SECOND: &str = "<!doctype html><html><head><meta charset=utf-8><title>Mighty smoke second</title></head>\
<body style=\"margin:0;background:#20508f;color:#fff;font:28px system-ui\"><p style=\"padding:24px\">Second page</p></body></html>";
/// A page that asks for a new window as it loads (`/popup`): the host refuses it.
const POPUP: &str = "<!doctype html><html><head><meta charset=utf-8><title>Mighty smoke popup</title></head>\
<body style=\"margin:0;background:#7a1f52;color:#fff;font:28px system-ui\"><p style=\"padding:24px\">Popup page</p>\
<script>window.open('/second', '_blank');</script></body></html>";

/// The ids of the pane and workspace the step adds (UUID-shaped, as the state's ids are).
const PANE: &str = "5a0e0b0b-0000-4000-8000-00000000b10b";
const WORKSPACE: &str = "5a0e0b0b-0000-4000-8000-00000000a0a0";
/// The id of the page dialog the step opens over the browser pane.
const DIALOG: u64 = 990_001;

#[derive(Default)]
struct Progress {
    calls: Vec<String>,
    snapshot_sent: bool,
    shell_sent: bool,
    injected: bool,
    stage: Stage,
    /// The first probe's report, kept while the later steps run.
    main: Option<Value>,
    /// The host's active pane when the first probe reported (its click must have reached the state).
    main_active: Option<String>,
    frame: Option<Value>,
    browser: Option<Value>,
    terminal: Option<Value>,
    files: Option<Value>,
    history: Option<Value>,
    create: Option<Value>,
    /// Where the gitCreate step's clone goes.
    create_location: Option<PathBuf>,
    /// The `gitProgress` pushes the page was sent.
    git_progress: usize,
    reload: Option<Value>,
    acp: Option<Value>,
    /// The reload step's page is loading again: its `ready` brings the probe.
    reload_waiting: bool,
    reload_ready: bool,
    reload_injected: bool,
    /// The name the reload step's rename sheet opened with.
    reload_name: Option<String>,
    finished: bool,
}

impl Progress {
    /// Where `stage`'s report is kept (the run steps' goes to `finish` as it is).
    fn report(&mut self, stage: Stage) -> Option<&mut Option<Value>> {
        match stage {
            Stage::Main => Some(&mut self.main),
            Stage::Frame => Some(&mut self.frame),
            Stage::Browser => Some(&mut self.browser),
            Stage::Terminal => Some(&mut self.terminal),
            Stage::Files => Some(&mut self.files),
            Stage::History => Some(&mut self.history),
            Stage::Create => Some(&mut self.create),
            Stage::Reload => Some(&mut self.reload),
            Stage::Acp => Some(&mut self.acp),
            Stage::Runs => None,
        }
    }
}

pub struct Smoke {
    out: PathBuf,
    exit: bool,
    started: Instant,
    /// What the load found, for the result.
    load: Value,
    mode: Mode,
    /// Where the stand-in CLI records what it was sent (`<record>.answers` …).
    fake_record: Option<PathBuf>,
    progress: Mutex<Progress>,
}

impl Smoke {
    pub fn new(out: PathBuf, exit: bool, load: Value, mode: Mode, fake_record: Option<PathBuf>) -> Smoke {
        Smoke { out, exit, started: Instant::now(), load, mode, fake_record, progress: Mutex::new(Progress::default()) }
    }

    pub fn fake_record(&self) -> Option<&PathBuf> {
        self.fake_record.as_ref()
    }

    fn timeout(&self) -> Duration {
        Duration::from_secs(match self.mode {
            // The frame, browser, gitCreate and reload steps add up to two minutes, the terminal and history steps a minute each.
            Mode::Page => 330,
            Mode::FakeClaude => 480,
            Mode::RealClaude => 420,
        })
    }

    fn progress(&self) -> std::sync::MutexGuard<'_, Progress> {
        self.progress.lock().unwrap_or_else(|poisoned| poisoned.into_inner())
    }

    pub fn called(&self, method: &str) {
        let mut progress = self.progress();
        if !progress.calls.iter().any(|m| m == method) {
            progress.calls.push(method.to_string());
        }
        if method == "ready" && progress.reload_waiting {
            progress.reload_ready = true;
        }
    }

    pub fn pushed(&self, kind: &str) {
        let mut progress = self.progress();
        match kind {
            "snapshot" => progress.snapshot_sent = true,
            "shell" => progress.shell_sent = true,
            "gitProgress" => progress.git_progress += 1,
            _ => {}
        }
    }

    /// Runs the probe once the page is up (see the module comment).
    pub fn check(&self, host: &Host) {
        let reloaded = {
            let mut progress = self.progress();
            let now = progress.reload_ready && !progress.reload_injected;
            if now {
                progress.reload_injected = true;
            }
            now.then(|| progress.reload_name.clone().unwrap_or_default())
        };
        if let Some(name) = reloaded {
            host.log.line("smoke: the page loaded again; probing its dialogs");
            let name = serde_json::to_string(&name).unwrap_or_default();
            self.eval(host, &crate::smoke_frame::RELOAD_PROBE.replace("__NAME__", &name));
            return;
        }
        {
            let mut progress = self.progress();
            let up = ["getLocale", "ready"].iter().all(|m| progress.calls.iter().any(|c| c == m))
                && progress.snapshot_sent
                && progress.shell_sent;
            if !up || progress.injected {
                return;
            }
            progress.injected = true;
        }
        host.log.line("smoke: the page is up; probing");
        self.eval(host, PROBE);
    }

    /// Runs `script` in the page (not `get_webview_window`: once a browser pane has a child webview, the window holds two).
    fn eval(&self, host: &Host, script: &str) {
        match host.app.get_webview(WINDOW) {
            Some(page) => {
                if let Err(error) = page.eval(script) {
                    self.probed(host, json!({"error": format!("the probe did not run: {error}")}));
                }
            }
            None => self.probed(host, json!({"error": "no main window"})),
        }
    }

    /// A probe's report: each one starts the next step that can start ([`STEPS`]); the last one finishes.
    pub fn probed(&self, host: &Host, probe: Value) {
        let stage = {
            let mut progress = self.progress();
            if progress.finished {
                return;
            }
            let stage = progress.stage;
            if let Some(report) = progress.report(stage) {
                *report = Some(probe.clone());
            }
            stage
        };
        if stage == Stage::Main {
            let active = host.view().snapshot.active_session_id.clone();
            self.progress().main_active = active;
        }
        let next = if stage == Stage::Main && probe.get("error").is_some() {
            None
        } else {
            let after = STEPS.iter().position(|step| *step == stage).map_or(0, |at| at + 1);
            STEPS[after..].iter().copied().find(|step| self.start(host, *step))
        };
        match next {
            Some(next) => self.progress().stage = next,
            None => {
                let runs = (stage == Stage::Runs).then_some(probe);
                self.finish(host, runs);
            }
        }
    }

    /// Starts `step` when it can: the page's steps never with the real CLI, each also only where its platform has what it checks.
    fn start(&self, host: &Host, step: Stage) -> bool {
        let page_steps = self.mode != Mode::RealClaude;
        match step {
            Stage::Main => false,
            Stage::Frame => {
                if page_steps {
                    self.eval(host, crate::smoke_frame::FRAME_PROBE);
                }
                page_steps
            }
            Stage::Browser => page_steps && self.start_browser(host),
            Stage::Terminal => page_steps && self.start_terminal(host),
            Stage::Files => page_steps && self.start_files(host),
            Stage::History => page_steps && crate::smoke_history::start(host),
            Stage::Create => page_steps && self.start_create(host),
            Stage::Reload => page_steps && self.start_reload(host),
            Stage::Acp => self.mode == Mode::FakeClaude && crate::smoke_acp::start(host),
            Stage::Runs => self.start_runs(host),
        }
    }

    /// Makes the gitCreate step's bare repository, points the sheets' location at a temporary folder and runs its probe (smoke_frame.rs);
    /// false without git on PATH.
    fn start_create(&self, host: &Host) -> bool {
        if crate::workspace_create::git_executable().is_none() {
            host.log.line("smoke: git is not on PATH; no gitCreate step");
            return false;
        }
        let Some(shared) = host.app.try_state::<Arc<Host>>().map(|h| h.inner().clone()) else { return false };
        host.log.line("smoke: gitCreate step");
        tauri::async_runtime::spawn(async move {
            let Some(smoke) = &shared.smoke else { return };
            match crate::smoke_frame::bare_repository(&shared).await {
                Ok((bare, location)) => {
                    {
                        let mut view = shared.view();
                        let mut next = view.snapshot.clone();
                        next.last_create_location = Some(location.to_string_lossy().into_owned());
                        view.replace(next);
                    }
                    smoke.progress().create_location = Some(location);
                    let workspace = shared.view().snapshot.active_workspace_id.clone().unwrap_or_default();
                    let url = serde_json::to_string(&bare.to_string_lossy()).unwrap_or_default();
                    smoke.eval(&shared, &crate::smoke_frame::CREATE_PROBE.replace("__URL__", &url).replace("__WORKSPACE__", &workspace));
                }
                Err(error) => smoke.probed(&shared, json!({"error": format!("no bare repository: {error}")})),
            }
        });
        true
    }

    /// Starts a rename of the active workspace (its sheet in the page, the flow waiting on its answer), then loads the page again;
    /// the page's `ready` brings the reload probe (`check`). False without an active workspace.
    fn start_reload(&self, host: &Host) -> bool {
        let Some(shared) = host.app.try_state::<Arc<Host>>().map(|h| h.inner().clone()) else { return false };
        let Some((id, name)) = ({
            let view = host.view();
            let state = &view.snapshot;
            state.active_workspace_id.as_deref().and_then(|id| state.workspace(id)).map(|w| (w.id().to_string(), w.name().to_string()))
        }) else {
            return false;
        };
        host.log.line("smoke: reload step");
        self.progress().reload_name = Some(name);
        tauri::async_runtime::spawn(async move {
            let Some(smoke) = &shared.smoke else { return };
            let dialogs = crate::dialogs::of(&shared);
            // The step before may still be ending its own sheet (its answer reaches the host after the page took it away).
            let settled = || dialogs.open_count() == 0 && !shared.view().dialog_open;
            let deadline = Instant::now() + Duration::from_secs(10);
            while !settled() && Instant::now() < deadline {
                tokio::time::sleep(Duration::from_millis(50)).await;
            }
            crate::frame::start_rename_workspace(&shared, id);
            let deadline = Instant::now() + Duration::from_secs(5);
            while dialogs.open_count() == 0 && Instant::now() < deadline {
                tokio::time::sleep(Duration::from_millis(50)).await;
            }
            if dialogs.open_count() == 0 {
                smoke.probed(&shared, json!({"error": "the rename sheet never opened"}));
                return;
            }
            // A moment for the page to draw the sheet before it loads again.
            tokio::time::sleep(Duration::from_millis(500)).await;
            smoke.progress().reload_waiting = true;
            smoke.eval(&shared, "location.reload()");
        });
        true
    }

    /// Adds the run steps' pane and runs their probe; false in the page-only smoke.
    fn start_runs(&self, host: &Host) -> bool {
        let script = match self.mode {
            Mode::Page => return false,
            Mode::FakeClaude => crate::smoke_runs::FAKE_PROBE
                .replace("__TYPED__", crate::smoke_runs::FAKE_REQUEST)
                .replace("__STOP__", crate::smoke_runs::STOP_REQUEST),
            Mode::RealClaude => crate::smoke_runs::REAL_PROBE.replace("__TYPED__", crate::smoke_runs::REAL_REQUEST),
        };
        let pane = match crate::smoke_runs::add_pane(host) {
            Ok(pane) => pane,
            Err(error) => {
                host.log.line(&format!("smoke: the run steps' pane was not added: {error}"));
                return false;
            }
        };
        host.flush();
        host.log.line(&format!("smoke: run steps ({:?})", self.mode));
        self.eval(host, &script.replace("__PANE__", &pane));
        true
    }

    /// Adds the files steps' workspace and panes and runs their probe (smoke_files.rs).
    fn start_files(&self, host: &Host) -> bool {
        let Some(host) = host.app.try_state::<Arc<Host>>().map(|h| h.inner().clone()) else { return false };
        match crate::smoke_files::start(&host) {
            Ok(script) => {
                host.flush();
                host.log.line("smoke: files steps");
                self.eval(&host, &script);
                true
            }
            Err(error) => {
                host.log.line(&format!("smoke: the files steps were not set up: {error}"));
                false
            }
        }
    }

    /// Adds the browser pane and runs the second probe; false where the platform has no native browser surface.
    fn start_browser(&self, host: &Host) -> bool {
        let Some(surfaces) = host.app.try_state::<Arc<SurfaceManager>>() else { return false };
        if surfaces.capabilities()["surfaces"]["browser"] != "native" {
            return false;
        }
        // The browser engine counts as on for this smoke only (C# `ForceBrowserEngineForSmoke`).
        surfaces.force_engine();
        let fixture = match serve_fixture() {
            Ok(fixture) => fixture,
            Err(error) => {
                host.log.line(&format!("smoke: no fixture server: {error}"));
                return false;
            }
        };
        if let Err(error) = add_browser_pane(host) {
            host.log.line(&format!("smoke: the browser pane was not added: {error}"));
            return false;
        }
        host.flush();
        host.log.line(&format!("smoke: browser surface step ({fixture})"));
        self.eval(host, &BROWSER_PROBE.replace("__PANE__", PANE).replace("__FIXTURE__", &fixture));
        true
    }

    /// Adds the terminal step's shell pane and runs its probe; false where the platform draws terminals natively (the Mac's Ghostty).
    fn start_terminal(&self, host: &Host) -> bool {
        let Some(surfaces) = host.app.try_state::<Arc<SurfaceManager>>() else { return false };
        if surfaces.capabilities()["surfaces"]["terminal"] != "web" {
            return false;
        }
        let script = match crate::smoke_terminal::start(host) {
            Ok(script) => script,
            Err(error) => {
                host.log.line(&format!("smoke: the terminal step's pane was not added: {error}"));
                return false;
            }
        };
        host.flush();
        host.log.line("smoke: terminal step");
        self.eval(host, &script);
        true
    }

    /// Writes the result (once) and, with `--smoke-exit`, quits.
    pub fn finish(&self, host: &Host, runs: Option<Value>) {
        let (calls, probe, browser, main_active, terminal, files, history) = {
            let mut progress = self.progress();
            if progress.finished {
                return;
            }
            progress.finished = true;
            let probe = progress.main.clone().unwrap_or_else(|| json!({"error": "no probe report"}));
            (
                progress.calls.clone(),
                probe,
                progress.browser.clone(),
                progress.main_active.clone(),
                progress.terminal.clone(),
                progress.files.clone(),
                progress.history.clone(),
            )
        };
        let (frame, create, create_location, git_progress, reload) = {
            let progress = self.progress();
            (
                progress.frame.clone(),
                progress.create.clone(),
                progress.create_location.clone(),
                progress.git_progress,
                progress.reload.clone(),
            )
        };
        let (snapshot_sent, shell_sent) = {
            let progress = self.progress();
            (progress.snapshot_sent, progress.shell_sent)
        };
        let (theme, active, workspaces, panes) = {
            let view = host.view();
            let s = &view.snapshot;
            (s.theme().to_string(), s.active_session_id.clone(), s.workspaces().len(), s.sessions().len())
        };
        let count = |key: &str| probe.get(key).and_then(Value::as_u64).unwrap_or(0);
        let text = |key: &str| probe.get(key).and_then(Value::as_str).unwrap_or("");
        let click_target = probe.get("clickTarget").and_then(Value::as_str);
        // The later steps select their own panes: the click's pane must have been the host's active one when the first probe reported.
        let host_selected = click_target.map(|target| main_active.as_deref() == Some(target));
        let up = ["getLocale", "ready"].iter().all(|m| calls.iter().any(|c| c == m));
        let mut checks = json!({
            "getLocaleAndReady": up,
            "snapshotAndShellSent": snapshot_sent && shell_sent,
            "paneTitles": count("paneTitles") > 0,
            "selectedPane": !text("selectedTitle").is_empty(),
            "theme": text("theme") == theme,
            "clickSelected": click_target.is_none() || probe.get("clickSelected") == Some(&Value::Bool(true)),
            "hostSelected": host_selected.unwrap_or(true),
            "noProbeError": probe.get("error").is_none(),
        });
        let merge = |checks: &mut Value, more: Value| {
            if let (Some(checks), Some(more)) = (checks.as_object_mut(), more.as_object()) {
                checks.extend(more.clone());
            }
        };
        match &frame {
            Some(frame) => merge(&mut checks, crate::smoke_frame::frame_checks(frame)),
            None if self.mode != Mode::RealClaude => merge(&mut checks, json!({"frameSidebar": false})),
            None => {}
        }
        if let Some(browser) = &browser {
            let yes = |key: &str| browser.get(key) == Some(&Value::Bool(true));
            checks["browserSurface"] = json!(
                browser.get("error").is_none()
                    && yes("loaded")
                    && yes("boundsMatch")
                    && yes("dialogShown")
                    && yes("hiddenUnderDialog")
                    && yes("shownAfterDialog")
            );
            checks["browserNavigated"] = json!(yes("navigated"));
            checks["browserBack"] = json!(yes("onSecond") && yes("wentBack"));
            checks["browserPopupBlocked"] = json!(yes("popupPage") && yes("popupBlocked"));
            checks["browserMenuOver"] = json!(yes("menuOpened") && yes("steppedAside") && yes("menuClosed"));
        }
        if let Some(create) = &create {
            merge(&mut checks, crate::smoke_frame::create_checks(create, host, create_location.as_deref(), git_progress));
        }
        match &reload {
            Some(reload) => merge(&mut checks, crate::smoke_frame::reload_checks(reload)),
            None if self.mode != Mode::RealClaude => merge(&mut checks, json!({"dialogResentAfterReload": false})),
            None => {}
        }
        if let Some(terminal) = &terminal {
            for (name, passed) in crate::smoke_terminal::checks(terminal) {
                checks[name] = json!(passed);
            }
        }
        if self.mode != Mode::RealClaude {
            let empty = json!({"error": "the files steps did not report"});
            for (name, passed) in crate::smoke_files::checks(host, files.as_ref().unwrap_or(&empty)) {
                checks[name] = json!(passed);
            }
            let missing = json!({"error": "the history step did not report"});
            for (name, passed) in crate::smoke_history::checks(history.as_ref().unwrap_or(&missing)) {
                checks[name] = json!(passed);
            }
        }
        match self.mode {
            Mode::Page => {}
            Mode::FakeClaude => {
                let acp = self.progress().acp.clone().unwrap_or_else(|| json!({"error": "the ACP step did not report"}));
                for (name, passed) in crate::smoke_acp::checks(host, &acp) {
                    checks[name] = json!(passed);
                }
                let empty = json!({"error": "the run steps did not report"});
                let r = runs.as_ref().unwrap_or(&empty);
                let yes = |key: &str| r.get(key) == Some(&Value::Bool(true));
                let text = |key: &str| r.get(key).and_then(Value::as_str).unwrap_or("");
                checks["runsNoError"] = json!(r.get("error").is_none());
                checks["composerSend"] = json!(yes("available") && yes("typed") && yes("drafted") && text("outcome") == "started");
                checks["permissionCard"] = json!(yes("permissionShown") && text("permissionTool") == "Bash");
                checks["planCard"] = json!(yes("planShown") && r.get("answers").and_then(Value::as_u64).unwrap_or(0) >= 2);
                checks["mightyDiagram"] = json!(
                    yes("requestBlock")
                        && matches!(text("status"), "completed" | "idle")
                        && !text("resultText").is_empty()
                        && r.get("agentCards").and_then(Value::as_u64).unwrap_or(0) + r.get("blocks").and_then(Value::as_u64).unwrap_or(0)
                            >= 3
                );
                checks["progressSteps"] = json!(
                    yes("stepsDrawn") && text("stepsCount") == "1/3" && text("stepsStates") == "done,current,pending" && yes("stepsHidden")
                );
                checks["stopRun"] = json!(yes("stopPermission") && yes("stopped") && yes("stopCardGone"));
            }
            Mode::RealClaude => {
                let empty = json!({"error": "the real request did not report"});
                let r = runs.as_ref().unwrap_or(&empty);
                let state = r.get("state").cloned().unwrap_or(Value::Null);
                let usage = state.get("usage").filter(|u| u.is_object());
                let tokens = usage.is_some_and(|u| {
                    ["outputTokens", "totalTokens", "inputTokens"].iter().any(|k| u.get(*k).is_some_and(Value::is_number))
                });
                checks["realNoError"] = json!(r.get("error").is_none());
                checks["realSent"] =
                    json!(r.get("available") == Some(&Value::Bool(true)) && r.get("outcome").and_then(Value::as_str) == Some("started"));
                checks["realCompleted"] = json!(matches!(state.get("status").and_then(Value::as_str), Some("completed" | "idle")));
                checks["realResultOk"] = json!(r.get("resultBlock").and_then(Value::as_str).is_some());
                checks["realUsage"] = json!(tokens);
            }
        }
        let passed = checks.as_object().is_some_and(|c| c.values().all(|v| v == &Value::Bool(true)));
        let result = json!({
            "passed": passed,
            "checks": checks,
            "probe": probe,
            "frame": frame,
            "browser": browser,
            "terminal": terminal,
            "files": files,
            "framesServed": crate::smoke_files::served(host),
            "history": history,
            "gitCreate": create,
            "gitProgressPushes": git_progress,
            "reload": reload,
            "acp": self.progress().acp.clone(),
            "runs": runs,
            "mode": format!("{:?}", self.mode),
            "host": {"workspaces": workspaces, "panes": panes, "theme": theme, "activeSessionId": active, "language": host.locale.language()},
            "load": self.load,
            "calls": calls,
            "unimplemented": host.unimplemented_methods(),
            "elapsedMs": self.started.elapsed().as_millis() as u64,
            "dataDir": host.data_dir,
            "version": host.version,
        });
        if let Some(parent) = self.out.parent() {
            let _ = std::fs::create_dir_all(parent);
        }
        if let Err(error) = std::fs::write(&self.out, serde_json::to_vec_pretty(&result).unwrap_or_default()) {
            host.log.line(&format!("smoke: cannot write {}: {error}", self.out.display()));
        }
        host.log.line(&format!("smoke: {} ({})", if passed { "passed" } else { "FAILED" }, self.out.display()));
        crate::smoke_history::remove_fixtures();
        if self.exit {
            host.app.exit(if passed { 0 } else { 1 });
        }
    }

    /// Fails the smoke when it has no result in time.
    pub fn watch(host: Arc<Host>) {
        std::thread::spawn(move || {
            let Some(timeout) = host.smoke.as_ref().map(Smoke::timeout) else { return };
            std::thread::sleep(timeout);
            if let Some(smoke) = &host.smoke {
                let error = json!({"error": format!("no result after {} s", timeout.as_secs())});
                let stage = {
                    let mut progress = smoke.progress();
                    let stage = progress.stage;
                    if let Some(report) = progress.report(stage) {
                        *report = Some(error.clone());
                    }
                    stage
                };
                smoke.finish(&host, (stage == Stage::Runs).then_some(error));
            }
        });
    }
}

/// Serves [`FIXTURE`] on 127.0.0.1 for the rest of the run; answers its address.
fn serve_fixture() -> std::io::Result<String> {
    let listener = TcpListener::bind("127.0.0.1:0")?;
    let address = format!("http://127.0.0.1:{}/fixture", listener.local_addr()?.port());
    std::thread::Builder::new().name("smoke-fixture".into()).spawn(move || {
        for mut stream in listener.incoming().flatten() {
            let _ = stream.set_read_timeout(Some(Duration::from_secs(5)));
            let mut request = [0u8; 4096];
            let read = stream.read(&mut request).unwrap_or(0);
            let line = String::from_utf8_lossy(&request[..read]);
            let page = if line.starts_with("GET /second") {
                SECOND
            } else if line.starts_with("GET /popup") {
                POPUP
            } else {
                FIXTURE
            };
            let response = format!(
                "HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n{page}",
                page.len()
            );
            let _ = stream.write_all(response.as_bytes());
        }
    })?;
    Ok(address)
}

/// A browser pane in a workspace of its own, made the active one (a change of the in-memory state; saved only where the app may
/// write, which a smoke's profile never is unless asked).
fn add_browser_pane(host: &Host) -> Result<(), String> {
    use mighty_core::Wire;
    let mut view = host.view();
    let mut state: Value = serde_json::from_str(&view.snapshot.to_json()).map_err(|e| e.to_string())?;
    let now = chrono::Utc::now().to_rfc3339_opts(chrono::SecondsFormat::Millis, true);
    let workspace = json!({"id": WORKSPACE, "name": "Smoke browser", "path": host.data_dir, "createdAt": now});
    let pane = json!({
        "id": PANE, "workspaceId": WORKSPACE, "title": "Smoke browser", "kind": "browser", "status": "idle", "model": "default",
        "provider": "claude", "settings": {"effort": "default", "permissionMode": "manual", "maxTurns": null, "maxBudgetUsd": null},
        "logs": [], "draft": "", "createdAt": now,
    });
    let list = |state: &mut Value, name: &str, item: Value| match state.get_mut(name).and_then(Value::as_array_mut) {
        Some(items) => {
            items.push(item);
            Ok(())
        }
        None => Err(format!("the state has no {name}")),
    };
    list(&mut state, "workspaces", workspace)?;
    list(&mut state, "sessions", pane)?;
    let mut expanded: Vec<Value> = state.get("expandedWorkspaceIds").and_then(Value::as_array).cloned().unwrap_or_default();
    expanded.push(json!(WORKSPACE));
    state["expandedWorkspaceIds"] = json!(expanded);
    state["activeWorkspaceId"] = json!(WORKSPACE);
    state["activeSessionId"] = json!(PANE);
    let next = AppSnapshot::read(&state).map_err(|e| e.to_string())?;
    // The workspace is the smoke's own: the store never approved it, so it is not normalized away but stays in memory as made.
    view.replace(next);
    Ok(())
}

/// The browser step's view of the native surface, and its page dialog: `inspect` → `{bounds, visible, url}` read back from the
/// OS (null before the view exists); `dialog` / `dialogClose` push a confirm dialog over the window and take it away. Smoke only.
#[tauri::command]
pub async fn smoke_surface(pane_id: String, action: String, host: tauri::State<'_, Arc<Host>>) -> Result<Value, String> {
    if host.smoke.is_none() {
        return Err("smoke only".into());
    }
    let surfaces = host.app.try_state::<Arc<SurfaceManager>>().map(|s| s.inner().clone()).ok_or("no native surfaces")?;
    let push = crate::surface::page_push(host.app.clone());
    match action.as_str() {
        "inspect" => Ok(surfaces.inspect(&pane_id, SurfaceKind::Browser).await.map_or(Value::Null, |state| {
            let b = state.bounds;
            json!({"bounds": {"x": b.x, "y": b.y, "width": b.width, "height": b.height}, "visible": state.visible, "url": state.url})
        })),
        "dialog" => {
            push(json!({"kind": "dialog", "id": DIALOG, "dialog": {
                "type": "confirm", "title": "Smoke", "message": "A page dialog over the browser pane", "close": "Close", "defaultPrimary": false
            }}));
            Ok(Value::Null)
        }
        "dialogClose" => {
            push(json!({"kind": "dialogClose", "id": DIALOG}));
            Ok(Value::Null)
        }
        // The popups the pane's page asked for (all refused) and the webviews the app holds (none more for a popup).
        "popups" => Ok(json!({"refused": surfaces.popups_refused(&pane_id), "webviews": host.app.webviews().len()})),
        // The frame step's view of the host's state (smoke_frame.rs).
        "state" => Ok(crate::smoke_frame::state(&host)),
        // The dialogs whose flows still wait (the reload step's: none once the redrawn sheet was answered).
        "dialogs" => Ok(json!({"open": crate::dialogs::of(&host).open_count(), "dialogOpen": host.view().dialog_open})),
        other => Err(format!("unknown smoke action {other}")),
    }
}
