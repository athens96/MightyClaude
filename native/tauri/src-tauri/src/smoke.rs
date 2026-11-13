//! `--smoke-test`: checks the page and the host together without anyone watching. Once the page has called `getLocale` and `ready`
//! and the host has pushed it the snapshot and the frame model, a probe script runs in the page: it waits for the sidebar's pane
//! titles, reads the DOM (pane titles, the selected pane, the theme, tabs, header, status bar), clicks another pane's sidebar row and
//! waits for the page to show it selected, then hands what it saw to the `smoke_probe` command. The host adds whether its own state
//! took the click, the calls it saw and the methods it answered "unimplemented", and writes `smoke-result.json`; with
//! `--smoke-exit` the app then quits, 0 when the smoke passed, else 1. A smoke that has no probe after 90 s fails.

use std::path::PathBuf;
use std::sync::Mutex;
use std::time::{Duration, Instant};

use serde_json::{Value, json};
use tauri::Manager;

use crate::host::{Host, WINDOW};

const TIMEOUT: Duration = Duration::from_secs(90);

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

#[derive(Default)]
struct Progress {
    calls: Vec<String>,
    snapshot_sent: bool,
    shell_sent: bool,
    injected: bool,
    finished: bool,
}

pub struct Smoke {
    out: PathBuf,
    exit: bool,
    started: Instant,
    /// What the load found, for the result.
    load: Value,
    progress: Mutex<Progress>,
}

impl Smoke {
    pub fn new(out: PathBuf, exit: bool, load: Value) -> Smoke {
        Smoke { out, exit, started: Instant::now(), load, progress: Mutex::new(Progress::default()) }
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
        match host.app.get_webview_window(WINDOW) {
            Some(window) => {
                if let Err(error) = window.eval(PROBE) {
                    self.finish(host, json!({"error": format!("the probe did not run: {error}")}));
                }
            }
            None => self.finish(host, json!({"error": "no main window"})),
        }
    }

    /// Writes the result (once) and, with `--smoke-exit`, quits.
    pub fn finish(&self, host: &Host, probe: Value) {
        let calls = {
            let mut progress = self.progress();
            if progress.finished {
                return;
            }
            progress.finished = true;
            progress.calls.clone()
        };
        let (snapshot_sent, shell_sent) = {
            let progress = self.progress();
            (progress.snapshot_sent, progress.shell_sent)
        };
        let (theme, active, workspaces, panes) = {
            let view = host.view();
            let s = &view.snapshot;
            (s.theme().to_string(), s.active_session_id.clone(), s.workspaces.len(), s.sessions.len())
        };
        let count = |key: &str| probe.get(key).and_then(Value::as_u64).unwrap_or(0);
        let text = |key: &str| probe.get(key).and_then(Value::as_str).unwrap_or("");
        let click_target = probe.get("clickTarget").and_then(Value::as_str);
        let host_selected = click_target.map(|target| active.as_deref() == Some(target));
        let up = ["getLocale", "ready"].iter().all(|m| calls.iter().any(|c| c == m));
        let checks = json!({
            "getLocaleAndReady": up,
            "snapshotAndShellSent": snapshot_sent && shell_sent,
            "paneTitles": count("paneTitles") > 0,
            "selectedPane": !text("selectedTitle").is_empty(),
            "theme": text("theme") == theme,
            "clickSelected": click_target.is_none() || probe.get("clickSelected") == Some(&Value::Bool(true)),
            "hostSelected": host_selected.unwrap_or(true),
            "noProbeError": probe.get("error").is_none(),
        });
        let passed = checks.as_object().is_some_and(|c| c.values().all(|v| v == &Value::Bool(true)));
        let result = json!({
            "passed": passed,
            "checks": checks,
            "probe": probe,
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

    /// Fails the smoke when the page never got far enough to be probed.
    pub fn watch(host: std::sync::Arc<Host>) {
        std::thread::spawn(move || {
            std::thread::sleep(TIMEOUT);
            if let Some(smoke) = &host.smoke {
                smoke.finish(&host, json!({"error": format!("no probe after {} s", TIMEOUT.as_secs())}));
            }
        });
    }
}
