//! The smoke's history step (W4-C): the C# web smoke's work map steps (MainWindow.WebShell.Panes.cs:510-528 `workMapOverview`,
//! `workMapVersions`, `workMapChips`, `workMapButton`) and a resume-list step.
//!
//! The host makes a fixture repository in a temporary folder (one tag, one commit past it, as C#'s `SmokeRepository`), adds it as a
//! workspace of its own (in memory) and makes it active, and writes a fixture home whose Claude records belong to that folder (three
//! requests, and one nested run the list hides); the history reads that home only (C#'s `HistoryHomeOverride`). The probe then:
//!
//! - clicks the header's work map button once the folder reads as a repository (`workMapButton`), waits for the overview's cells
//!   (`workMapOverview`) and one card per version with a summarize button (`workMapVersions`), switches the Mac chip off and on
//!   (`workMapChips`);
//! - has the host open the resume list over the folder (the page-drawn `resumeList` sheet, its rows from `resumeRows`): the session is
//!   listed with its first request and the nested run counted as hidden, a search with no match says so, "모든 세션 보기" lists both
//!   (`resumeList`); then the host takes the first row as the pick, adds a pane that continues the session and the page shows its record's
//!   requests above in the Mighty diagram, read back from the record (`resumeLoaded`).

use std::path::{Path, PathBuf};
use std::sync::Arc;

use mighty_core::{AppSnapshot, Wire};
use mighty_history::history::claude_project_folder;
use mighty_workmap::{Git, pane};
use serde_json::{Value, json};
use tauri::Manager;

use crate::host::Host;

/// The workspace and the panes the step adds (UUID-shaped, as the state's ids are).
pub const WORKSPACE: &str = "5a0e0b0b-0000-4000-8000-00000000c4a0";
const RESUMED_PANE: &str = "5a0e0b0b-0000-4000-8000-00000000c4a1";
/// The fixture sessions: one the user ran, one nested run the list hides.
const SESSION: &str = "5a0e0b0b-0000-4000-8000-00000000e501";
const NESTED: &str = "5a0e0b0b-0000-4000-8000-00000000e502";
/// The resume list's page dialog.
const DIALOG: u64 = 990_101;

const PROBE: &str = r#"(async () => {
  const until = async (test, ms) => { const end = performance.now() + ms; while (performance.now() < end) { const v = await test(); if (v) return v; await new Promise((r) => setTimeout(r, 50)); } return test(); };
  const invoke = (command, args) => window.__TAURI_INTERNALS__.invoke(command, args);
  const step = (name, extra) => invoke('smoke_history', { step: name, ...(extra ?? {}) });
  const mapId = '__MAP__', paneId = '__PANE__';
  const slot = (id) => document.querySelector('.pane-slot[data-pane-id="' + id + '"]');
  const map = (s) => slot(mapId)?.querySelector(s);
  const report = {};
  try {
    // The work map: the header's button, once the folder reads as a repository.
    const button = await until(() => { const b = document.querySelector('[data-testid=open-workmap]'); return b && !b.disabled ? b : null; }, 20000);
    report.buttonEnabled = !!button;
    if (button) button.click(); else await step('openMap');
    report.mapOverview = !!(await until(() => map('[data-testid=workmap-overview] .workmap-cell') && !slot(mapId).classList.contains('hidden'), 20000));
    report.mapVersions = report.mapOverview && !!(await until(() => slot(mapId).querySelectorAll('[data-testid=workmap-version]').length === 2 && map('[data-testid=workmap-summarize]'), 15000));
    report.versionCount = slot(mapId)?.querySelectorAll('[data-testid=workmap-version]').length ?? 0;
    if (report.mapVersions) map('[data-lane=mac]').click();
    const off = report.mapVersions && !!(await until(() => map('[data-lane=mac]')?.getAttribute('aria-pressed') === 'false', 5000));
    if (off) map('[data-lane=mac]').click();
    report.mapChips = off && !!(await until(() => map('[data-lane=mac]')?.getAttribute('aria-pressed') === 'true', 5000));
    report.mapButton = report.buttonEnabled && report.mapOverview;
    // The resume list: the host opens it over the folder; its rows come from resumeRows.
    await step('resumeList');
    const rows = () => [...document.querySelectorAll('[data-testid=resume-rows] .resume-row')];
    report.rowsShown = !!(await until(() => rows().length === 1, 15000));
    report.rowText = rows()[0]?.textContent ?? null;
    report.hiddenText = document.querySelector('.resume-footer .quiet')?.textContent ?? null;
    const search = document.querySelector('.resume-search input');
    const type = (text) => { Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set.call(search, text); search.dispatchEvent(new Event('input', { bubbles: true })); };
    type('no such words anywhere');
    report.noMatch = !!(await until(() => rows().length === 0 && (document.querySelector('.resume-empty')?.textContent ?? '').length > 0, 10000));
    type('');
    await until(() => rows().length === 1, 10000);
    document.querySelector('.resume-footer input[type=checkbox]')?.click();
    report.allShown = !!(await until(() => rows().length === 2, 15000));
    // The pick: the host takes the listed session, closes the sheet and adds a pane that continues it.
    const picked = await step('resume');
    report.picked = picked;
    const blocks = () => [...(slot(paneId)?.querySelectorAll('[data-testid=mighty-diagram] .mighty-block[data-kind=request]') ?? [])];
    const history = () => slot(paneId)?.querySelector('[data-testid=mighty-diagram] .mighty-history');
    report.historyBlock = !!(await until(() => history(), 20000));
    let loaded = await until(() => blocks().length >= 3, 6000);
    if (!loaded) { history()?.querySelector('button')?.click(); loaded = await until(() => blocks().length >= 3, 15000); }
    report.loadedRequests = blocks().length;
    // A block's id is "<length of the run id>:<run id>:<suffix>"; runs read back from the record are "record-…".
    report.loadedFromRecord = blocks().filter((b) => ((b.dataset.blockId ?? '').split(':')[1] ?? '').startsWith('record-')).length;
    report.historyText = history()?.textContent ?? null;
  } catch (error) {
    report.error = String(error);
  }
  await invoke('smoke_probe', { probe: report });
})();"#;

fn line(value: Value) -> String {
    value.to_string()
}

/// The fixture home: the session's record (three requests) and a nested run's, both of `folder`.
fn write_records(home: &Path, folder: &str) -> std::io::Result<()> {
    let projects = home.join(".claude").join("projects").join(claude_project_folder(folder));
    std::fs::create_dir_all(&projects)?;
    let stamp = |minute: u32| format!("2026-11-14T01:{minute:02}:00.000Z");
    let mut lines = vec![];
    for i in 1..=3u32 {
        lines.push(line(json!({"type": "user", "message": {"role": "user", "content": format!("smoke resume request {i}")},
            "uuid": format!("u{i}"), "timestamp": stamp(i * 2), "cwd": folder})));
        lines
            .push(line(json!({"type": "assistant", "message": {"id": format!("msg-{i}"), "role": "assistant", "model": "claude-opus-4-5",
            "content": [{"type": "text", "text": format!("smoke answer {i}")}]}, "uuid": format!("a{i}"), "timestamp": stamp(i * 2 + 1)})));
    }
    std::fs::write(projects.join(format!("{SESSION}.jsonl")), lines.join("\n") + "\n")?;
    let nested = line(json!({"type": "user", "message": {"role": "user", "content": "User: plan the seed\n\nAssistant: ok"},
        "uuid": "n1", "timestamp": stamp(1), "cwd": folder}));
    std::fs::write(projects.join(format!("{NESTED}.jsonl")), nested + "\n")
}

/// C# `SmokeRepository`: a tag and one commit past it (`(windows) smoke first`, `v0.0.1`, `(mac) smoke second`).
async fn repository(git: &Git, folder: &Path) -> bool {
    std::fs::create_dir_all(folder.join("smoke-folder")).is_ok()
        && std::fs::write(folder.join("smoke-folder").join("hello.ts"), "const smokeMarker = 42; // web files smoke\n").is_ok()
        && run(git, folder, &["init", "-q"]).await
        && run(git, folder, &["add", "--", "smoke-folder"]).await
        && run(git, folder, &["commit", "-q", "-m", "(windows) smoke first"]).await
        && run(git, folder, &["tag", "v0.0.1"]).await
        && run(git, folder, &["commit", "-q", "--allow-empty", "-m", "(mac) smoke second"]).await
}

async fn run(git: &Git, folder: &Path, arguments: &[&str]) -> bool {
    let mut all = vec!["-c", "user.name=Smoke", "-c", "user.email=smoke@example.com", "-c", "commit.gpgsign=false"];
    all.extend_from_slice(arguments);
    git.run(&all, folder, 30.0).await.is_some_and(|r| r.exit_code == 0)
}

fn folder() -> PathBuf {
    std::env::temp_dir().join(format!("mighty-tauri-smoke-history-{}", std::process::id()))
}

/// The workspace of the step, made the active one (in memory, as the other steps' are).
fn add_workspace(host: &Host, folder: &Path) -> Result<(), String> {
    let mut view = host.view();
    let mut state: Value = serde_json::from_str(&view.snapshot.to_json()).map_err(|e| e.to_string())?;
    let now = chrono::Utc::now().to_rfc3339_opts(chrono::SecondsFormat::Millis, true);
    let workspace = json!({"id": WORKSPACE, "name": "Smoke history", "path": folder, "createdAt": now});
    state.get_mut("workspaces").and_then(Value::as_array_mut).ok_or("the state has no workspaces")?.push(workspace);
    let mut expanded: Vec<Value> = state.get("expandedWorkspaceIds").and_then(Value::as_array).cloned().unwrap_or_default();
    expanded.push(json!(WORKSPACE));
    state["expandedWorkspaceIds"] = json!(expanded);
    state["activeWorkspaceId"] = json!(WORKSPACE);
    state["activeSessionId"] = Value::Null;
    view.replace(AppSnapshot::read(&state).map_err(|e| e.to_string())?);
    Ok(())
}

/// Starts the step: fixtures made off the page's thread, then the probe. False when there is no run engine to read Claude's catalogue.
pub fn start(host: &Host) -> bool {
    let Some(host) = host.app.try_state::<Arc<Host>>().map(|h| h.inner().clone()) else { return false };
    let folder = folder();
    let home = folder.with_extension("home");
    tauri::async_runtime::spawn(async move {
        let git = Git::new(host.processes.clone(), folder.with_extension("hooks"));
        let made = std::fs::create_dir_all(&folder).is_ok() && repository(&git, &folder).await;
        host.log.line(&format!("smoke: history step ({}, repository {})", folder.display(), if made { "made" } else { "NOT made" }));
        let folder_text = folder.to_string_lossy().into_owned();
        if let Err(error) = write_records(&home, &folder_text) {
            host.log.line(&format!("smoke: the resume records were not written: {error}"));
        }
        crate::history::of(&host).set_home_override(Some(home));
        if let Err(error) = add_workspace(&host, &folder) {
            host.log.line(&format!("smoke: the history workspace was not added: {error}"));
        }
        host.flush();
        let script = PROBE.replace("__MAP__", &pane::pane_id(WORKSPACE)).replace("__PANE__", RESUMED_PANE);
        match host.app.get_webview(crate::host::WINDOW) {
            Some(page) => {
                if let Err(error) = page.eval(&script) {
                    host.log.line(&format!("smoke: the history probe did not run: {error}"));
                }
            }
            None => host.log.line("smoke: no main window for the history probe"),
        }
    });
    true
}

/// The history step's checks from its probe's report.
pub fn checks(report: &Value) -> Vec<(&'static str, bool)> {
    let yes = |key: &str| report.get(key) == Some(&Value::Bool(true));
    let text = |key: &str| report.get(key).and_then(Value::as_str).unwrap_or("");
    vec![
        ("historyNoError", report.get("error").is_none()),
        ("workMapOverview", yes("mapOverview")),
        ("workMapVersions", yes("mapVersions")),
        ("workMapChips", yes("mapChips")),
        ("workMapButton", yes("mapButton")),
        (
            "resumeList",
            yes("rowsShown")
                && text("rowText").contains("smoke resume request 1")
                && !text("hiddenText").is_empty()
                && yes("noMatch")
                && yes("allShown"),
        ),
        (
            "resumeLoaded",
            report.get("picked").and_then(|p| p.get("resumeId")).and_then(Value::as_str) == Some(SESSION)
                && yes("historyBlock")
                && report.get("loadedFromRecord").and_then(Value::as_u64).unwrap_or(0) >= 3,
        ),
    ]
}

/// What the history probe asks the host (smoke only): `openMap` (the work map pane, when the header button never enabled), `resumeList`
/// (the list sheet over the step's workspace), `resume` (the list's first session picked: the sheet closed, a pane that continues it added
/// and shown in the Mighty diagram).
#[tauri::command]
pub fn smoke_history(step: String, host: tauri::State<'_, Arc<Host>>) -> Result<Value, String> {
    if host.smoke.is_none() {
        return Err("smoke only".into());
    }
    match step.as_str() {
        "openMap" => {
            let mut view = host.view();
            crate::workmap::open_work_map(&host, &mut view, Some(WORKSPACE)).map(Value::from)
        }
        "resumeList" => {
            let workspace = host.view().snapshot.workspace(WORKSPACE).cloned().ok_or("no history workspace")?;
            crate::history::open_resume_list(&host, "claude", &workspace);
            let dialog =
                json!({"type": "resumeList", "provider": "claude", "title": host.locale.get("resume.title"), "path": workspace.path()});
            let view = host.view();
            host.push(&view, "dialog", json!({"kind": "dialog", "id": DIALOG, "dialog": dialog}).to_string());
            Ok(Value::Null)
        }
        "resume" => {
            let listing = crate::history::close_resume_list(&host).ok_or("the list read nothing")?;
            {
                let view = host.view();
                host.push(&view, "dialogClose", json!({"kind": "dialogClose", "id": DIALOG}).to_string());
            }
            let first =
                listing.items.iter().find(|i| i.session_id == SESSION).map(|i| i.session_id.clone()).ok_or("the session was not listed")?;
            let mut view = host.view();
            let session = crate::history::pick(&host, &view, &listing, &first).ok_or("no such row")??;
            // AddPane("claude", provider, shape: Apply): a Claude pane of the workspace shown in the Mighty diagram.
            let template = mighty_core::model::RunSession {
                id: Some(RESUMED_PANE.into()),
                workspace_id: Some(WORKSPACE.into()),
                title: Some("Claude".into()),
                agent_view_mode: Some("mighty".into()),
                graph_view_mode: Some("diagram".into()),
                ..mighty_core::model::RunSession::default()
            };
            let pane = mighty_history::resumable::apply(&session, &template);
            let mut state = view.snapshot.clone();
            state.sessions_mut().push(Arc::new(pane.clone()));
            state.active_workspace_id = Some(WORKSPACE.into());
            state.active_session_id = Some(RESUMED_PANE.into());
            view.replace(state);
            drop(view);
            host.flush();
            Ok(json!({"resumeId": pane.resume_id, "title": pane.title, "provider": pane.provider}))
        }
        other => Err(format!("unknown smoke step {other}")),
    }
}
