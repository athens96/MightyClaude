//! The smoke's terminal step (C# MainWindow.WebShell.Panes.cs `RunWebPanesSmoke`, its terminal part), where the page draws the
//! terminal (Windows: xterm.js over ConPTY): the host adds a shell pane in a workspace of its own (a temporary folder, in memory)
//! and lets it start a shell; the probe waits for the shell (idle at its prompt), types a command through the page's xterm that
//! writes a marker in two halves and waits for the joined marker in the terminal's buffer, reads the footer, types a command that
//! runs a while (ping) and waits for the pane to read running and then idle again, and closes the pane (its shell ends).

use std::path::PathBuf;
use std::sync::Arc;

use mighty_core::{AppSnapshot, Wire, wire};
use serde_json::{Value, json};

use crate::host::Host;
use crate::terminal::terminals;

/// The pane and workspace the step adds (UUID-shaped, as the state's ids are).
pub const PANE: &str = "5a0e0b0b-0000-4000-8000-0000000007e1";
const WORKSPACE: &str = "5a0e0b0b-0000-4000-8000-0000000007e2";

/// The probe; `__PANE__` and `__MARKER__` are plain ASCII, `__TYPED__` and `__BUSY__` JSON string literals.
pub const PROBE: &str = r#"(async () => {
  const until = async (test, ms) => { const end = performance.now() + ms; while (performance.now() < end) { const v = await test(); if (v) return v; await new Promise((r) => setTimeout(r, 100)); } return test(); };
  const invoke = (command, args) => window.__TAURI_INTERNALS__.invoke(command, args);
  const paneId = '__PANE__', marker = '__MARKER__', typed = __TYPED__, busy = __BUSY__;
  const state = () => invoke('smoke_terminal', { paneId, action: 'state' });
  const slot = () => document.querySelector('.pane-slot:not(.hidden)[data-pane-id="' + paneId + '"]');
  const buffer = () => window.mightyPanes?.terminalText?.(paneId) ?? '';
  const report = { paneId };
  try {
    report.pane = !!(await until(() => slot()?.querySelector('[data-testid=terminal-pane]'), 15000));
    report.started = !!(await until(async () => (await state()).running, 30000));
    report.idle = !!(await until(async () => (await state()).status === 'idle', 15000));
    report.typed = !!(await until(() => window.mightyPanes?.terminalInput?.(paneId, typed) === true, 15000));
    report.echoed = !!(await until(() => buffer().includes(marker), 20000));
    report.footer = slot()?.querySelector('[data-testid=terminal-footer]')?.textContent ?? null;
    report.notice = slot()?.querySelector('[data-testid=terminal-notice]')?.textContent ?? null;
    const sent = window.mightyPanes?.terminalInput?.(paneId, busy) === true;
    report.running = sent && !!(await until(async () => (await state()).status === 'running', 15000));
    report.idleAgain = report.running && !!(await until(async () => (await state()).status === 'idle', 30000));
    if (!report.echoed || !report.running) {
      report.text = buffer().slice(-600);
      report.outputs = window.mightyPanes?.terminalOutputs?.() ?? null;
      report.host = await state();
    }
    report.closed = await invoke('smoke_terminal', { paneId, action: 'close' });
  } catch (error) {
    report.error = String(error);
  }
  await invoke('smoke_probe', { probe: report });
})();"#;

/// The step's workspace folder: a new temporary folder.
fn workspace_folder() -> std::io::Result<PathBuf> {
    let folder = std::env::temp_dir().join(format!("mighty-tauri-smoke-terminal-{}", std::process::id()));
    std::fs::create_dir_all(&folder)?;
    Ok(folder)
}

/// Adds the shell pane (made the active one, in memory as the other steps' panes are), lets it start a shell, and gives the probe
/// with its marker and commands filled in.
pub fn start(host: &Host) -> Result<String, String> {
    let terminals = terminals(host)?;
    let folder = workspace_folder().map_err(|e| e.to_string())?;
    terminals.smoke_allow(PANE);
    {
        let mut view = host.view();
        let mut state: Value = serde_json::from_str(&view.snapshot.to_json()).map_err(|e| e.to_string())?;
        let now = chrono::Utc::now().to_rfc3339_opts(chrono::SecondsFormat::Millis, true);
        let workspace = json!({"id": WORKSPACE, "name": "Smoke terminal", "path": folder, "createdAt": now});
        let pane = json!({
            "id": PANE, "workspaceId": WORKSPACE, "title": "Web smoke terminal", "kind": "shell", "status": "idle", "model": "default",
            "provider": "claude", "settings": {"effort": "default", "permissionMode": "manual", "maxTurns": null, "maxBudgetUsd": null},
            "logs": [], "draft": "", "createdAt": now,
        });
        for (name, item) in [("workspaces", workspace), ("sessions", pane)] {
            state.get_mut(name).and_then(Value::as_array_mut).ok_or_else(|| format!("the state has no {name}"))?.push(item);
        }
        let mut expanded: Vec<Value> = state.get("expandedWorkspaceIds").and_then(Value::as_array).cloned().unwrap_or_default();
        expanded.push(json!(WORKSPACE));
        state["expandedWorkspaceIds"] = json!(expanded);
        state["activeWorkspaceId"] = json!(WORKSPACE);
        state["activeSessionId"] = json!(PANE);
        view.replace(AppSnapshot::read(&state).map_err(|e| e.to_string())?);
    }
    // A marker the terminal must render back through the pseudo console, typed in two halves so the echo of the typing alone never
    // holds it whole.
    let marker = format!("MIGHTY_WEB_{}", wire::new_id().replace('-', ""));
    let (head, tail) = marker.split_at(12);
    let powershell = terminals.default_shell().file_stem().is_some_and(|stem| stem.eq_ignore_ascii_case("powershell"));
    let (typed, busy) = if powershell {
        (format!("[Console]::WriteLine(('{head}'+'{tail}'))\r"), "ping -n 6 127.0.0.1 > $null\r".to_string())
    } else {
        (
            format!("set MIGHTY_SMOKE={head}\rset MIGHTY_SMOKE=%MIGHTY_SMOKE%{tail}\recho %MIGHTY_SMOKE%\r"),
            "ping -n 6 127.0.0.1 >nul\r".to_string(),
        )
    };
    Ok(PROBE
        .replace("__PANE__", PANE)
        .replace("__MARKER__", &marker)
        .replace("__TYPED__", &serde_json::to_string(&typed).unwrap_or_default())
        .replace("__BUSY__", &serde_json::to_string(&busy).unwrap_or_default()))
}

/// The step's checks from its probe's report.
pub fn checks(report: &Value) -> Vec<(&'static str, bool)> {
    let yes = |key: &str| report.get(key) == Some(&Value::Bool(true));
    let footer = report.get("footer").and_then(Value::as_str).unwrap_or("");
    vec![
        ("terminalNoError", report.get("error").is_none()),
        ("terminalStarted", yes("pane") && yes("started") && yes("idle")),
        ("terminalEcho", yes("typed") && yes("echoed")),
        // The engine and the grid ("ConPTY · powershell … 120×30").
        ("terminalFooter", footer.contains("ConPTY") && footer.contains('×')),
        ("terminalStatus", yes("running") && yes("idleAgain")),
        ("terminalClosed", yes("closed")),
    ]
}

/// What the probe asks the host (smoke only): `state` of the step's pane, or `close` it (its shell ends; the pane leaves the state).
#[tauri::command]
pub async fn smoke_terminal(pane_id: String, action: String, host: tauri::State<'_, Arc<Host>>) -> Result<Value, String> {
    if host.smoke.is_none() {
        return Err("smoke only".into());
    }
    let terminals = terminals(&host)?;
    match action.as_str() {
        "state" => Ok(terminals.smoke_state(&host, &pane_id)),
        "close" => {
            let closed = terminals.smoke_close(&pane_id).await;
            {
                let mut view = host.view();
                let mut next = view.snapshot.clone();
                next.sessions_mut().retain(|s| s.id() != pane_id);
                view.replace(next);
            }
            host.schedule();
            if let Ok(folder) = workspace_folder() {
                let _ = std::fs::remove_dir_all(folder);
            }
            Ok(json!(closed))
        }
        other => Err(format!("unknown smoke action {other}")),
    }
}
