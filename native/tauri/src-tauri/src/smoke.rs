//! `--smoke-test`: checks the page and the host together without anyone watching. Once the page has called `getLocale` and `ready`
//! and the host has pushed it the snapshot and the frame model, a probe script runs in the page: it waits for the sidebar's pane
//! titles, reads the DOM (pane titles, the selected pane, the theme, tabs, header, status bar), clicks another pane's sidebar row and
//! waits for the page to show it selected, then hands what it saw to the `smoke_probe` command.
//!
//! Then, where the platform draws browser panes natively, the browser-surface step (the NativeSlot protocol): the host adds a
//! browser pane in a workspace of its own to the in-memory state and makes it active; a second probe types the address of a local
//! fixture page (served on 127.0.0.1 by this process) into the pane's address field and waits until the native view shows it,
//! checks that the view's real bounds (read back from the OS) are the slot's in device pixels, has the host push a page dialog and
//! checks that the view hides under it, closes the dialog and checks that the view shows again.
//!
//! Then, where the page draws terminals, the terminal step (smoke_terminal.rs: a shell pane's shell started, typed into through the
//! page's xterm and its marker read back, the footer, the pane's status running and idle again, the pane closed).
//!
//! With `--smoke-fake-cli` the run steps follow (smoke_runs.rs: the composer, the permission and plan cards, the Mighty diagram, the
//! progress panel and a stop, against mighty-run's stand-in CLI); with `--smoke-real-claude` one real request to the installed
//! `claude` CLI follows the first probe instead of the browser step.
//!
//! The host adds whether its own state took the click, the calls it saw and the methods it answered "unimplemented", and writes
//! `smoke-result.json`; with `--smoke-exit` the app then quits, 0 when the smoke passed, else 1. A smoke that has no result in time
//! (150 s, 300 s with the stand-in CLI, 420 s with the real one) fails.

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
    Browser,
    /// The terminal step (smoke_terminal.rs), where the page draws terminals.
    Terminal,
    Runs,
}

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
    const slot = await until(() => document.querySelector('[data-testid=browser-slot]'), 15000);
    report.slot = !!slot;
    const address = await until(() => { const field = document.querySelector('[data-testid=browser-address]'); return field && !field.disabled ? field : null; }, 10000);
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
    const box = slot.getBoundingClientRect(), ratio = window.devicePixelRatio || 1;
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
  } catch (error) {
    report.error = String(error);
  }
  await invoke('smoke_probe', { probe: report });
})();"#;

/// The fixture page the browser pane loads.
const FIXTURE: &str = "<!doctype html><html><head><meta charset=utf-8><title>Mighty smoke fixture</title></head>\
<body style=\"margin:0;background:#1f7a52;color:#fff;font:28px system-ui\"><p style=\"padding:24px\">Mighty Code browser surface fixture</p></body></html>";

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
    browser: Option<Value>,
    terminal: Option<Value>,
    finished: bool,
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
            Mode::Page => 150,
            Mode::FakeClaude => 300,
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
    }

    pub fn pushed(&self, kind: &str) {
        let mut progress = self.progress();
        match kind {
            "snapshot" => progress.snapshot_sent = true,
            "shell" => progress.shell_sent = true,
            _ => {}
        }
    }

    /// Runs the probe once the page is up (see the module comment).
    pub fn check(&self, host: &Host) {
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

    /// A probe's report: the first one starts the browser step, the browser step's the run steps; the last one finishes.
    pub fn probed(&self, host: &Host, probe: Value) {
        let stage = {
            let mut progress = self.progress();
            if progress.finished {
                return;
            }
            match progress.stage {
                Stage::Main => progress.main = Some(probe.clone()),
                Stage::Browser => progress.browser = Some(probe.clone()),
                Stage::Terminal => progress.terminal = Some(probe.clone()),
                Stage::Runs => {}
            }
            progress.stage
        };
        if stage == Stage::Main {
            let active = host.view().snapshot.active_session_id.clone();
            self.progress().main_active = active;
        }
        let next = match stage {
            Stage::Main if probe.get("error").is_some() => None,
            Stage::Main if self.mode != Mode::RealClaude && self.start_browser(host) => Some(Stage::Browser),
            Stage::Main | Stage::Browser if self.mode != Mode::RealClaude && self.start_terminal(host) => Some(Stage::Terminal),
            Stage::Main | Stage::Browser | Stage::Terminal if self.start_runs(host) => Some(Stage::Runs),
            _ => None,
        };
        match next {
            Some(next) => self.progress().stage = next,
            None => {
                let runs = (stage == Stage::Runs).then_some(probe);
                self.finish(host, runs);
            }
        }
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

    /// Adds the browser pane and runs the second probe; false where the platform has no native browser surface.
    fn start_browser(&self, host: &Host) -> bool {
        let Some(surfaces) = host.app.try_state::<Arc<SurfaceManager>>() else { return false };
        if surfaces.capabilities()["surfaces"]["browser"] != "native" {
            return false;
        }
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
        let (calls, probe, browser, main_active, terminal) = {
            let mut progress = self.progress();
            if progress.finished {
                return;
            }
            progress.finished = true;
            let probe = progress.main.clone().unwrap_or_else(|| json!({"error": "no probe report"}));
            (progress.calls.clone(), probe, progress.browser.clone(), progress.main_active.clone(), progress.terminal.clone())
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
        }
        if let Some(terminal) = &terminal {
            for (name, passed) in crate::smoke_terminal::checks(terminal) {
                checks[name] = json!(passed);
            }
        }
        match self.mode {
            Mode::Page => {}
            Mode::FakeClaude => {
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
            "browser": browser,
            "terminal": terminal,
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
                    match progress.stage {
                        Stage::Main => progress.main = Some(error.clone()),
                        Stage::Browser => progress.browser = Some(error.clone()),
                        Stage::Terminal => progress.terminal = Some(error.clone()),
                        Stage::Runs => {}
                    }
                    progress.stage
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
            let _ = stream.read(&mut request);
            let response = format!(
                "HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n{FIXTURE}",
                FIXTURE.len()
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
        other => Err(format!("unknown smoke action {other}")),
    }
}
