//! The smoke's frame and workspace-create steps (C# `RunWebShellFrameSmoke`, MainWindow.WebShell.Shell.cs:525, and the create sheets of
//! MainWindow.WorkspaceCreate.cs), driven in the page as a person would:
//!
//! - frame: the sidebar lists the workspaces, the active one's panes, the header and the status bar; a click on another pane's row
//!   makes it the active one in the host's state and in the page; the add-pane menu's terminal entry adds a tab to the active group; a
//!   click on a tab and back on the new one switch panes (timed from the click to the frame after the pane shows); the header's layout
//!   button's columns preset draws two groups side by side. Nothing is clicked that opens the OS's dialogs or another window.
//! - gitCreate: a bare repository made in a temporary folder (the fixture's own git, no user configuration), the sheets' location set
//!   to that folder (the in-memory state's last location), then the add menu's "Git으로 생성…": its address typed, the check answering
//!   reachable, the name it suggests, 생성, the clone's progress, and the sheet gone once the workspace is added.

use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::time::Duration;

use mighty_platform::SpawnRequest;
use serde_json::{Value, json};

use crate::host::Host;

/// The frame step's probe.
pub const FRAME_PROBE: &str = r#"(async () => {
  const until = async (test, ms) => { const end = performance.now() + ms; while (performance.now() < end) { const v = await test(); if (v) return v; await new Promise((r) => setTimeout(r, 50)); } return test(); };
  const invoke = (command, args) => window.__TAURI_INTERNALS__.invoke(command, args);
  const host = () => invoke('smoke_surface', { paneId: '', action: 'state' });
  const frame = () => ({
    workspaces: document.querySelectorAll('[data-testid=sidebar-workspace]').length,
    rows: [...document.querySelectorAll('[data-testid=sidebar-pane]')].map((e) => e.dataset.sessionId),
    tabs: [...document.querySelectorAll('[data-testid=tab]')].map((e) => e.dataset.tabId),
    groups: document.querySelectorAll('[data-testid=tab-group]').length,
    splits: document.querySelectorAll('[data-testid=split]').length,
    active: document.querySelector('.pane-slot.active:not(.hidden)')?.dataset.paneId ?? null,
    statusBar: !!document.querySelector('[data-testid=status-bar]'),
    header: !!document.querySelector('[data-testid=workspace-header]'),
  });
  const shows = async (id) => (await host()).active === id && frame().active === id;
  const timeTab = async (id) => {
    window.__tabSwitch = null;
    const tab = document.querySelector('[data-tab-id="' + id + '"] .tab-handle');
    if (!tab) return null;
    const t0 = performance.now();
    tab.click();
    const shown = () => { const slot = document.querySelector('.pane-slot[data-pane-id="' + id + '"]'); return !!slot && !slot.classList.contains('hidden') && !!slot.querySelector('.pane'); };
    const check = () => { if (shown()) requestAnimationFrame(() => { window.__tabSwitch = { frame: performance.now() - t0 }; }); else requestAnimationFrame(check); };
    check();
    const timing = await until(() => window.__tabSwitch, 5000);
    return timing ? Math.round(timing.frame * 10) / 10 : null;
  };
  const report = {};
  try {
    const start = await host();
    report.sidebar = !!(await until(() => { const f = frame(); return f.workspaces >= 1 && f.statusBar && f.header && start.activePanes.every((id) => f.rows.includes(id)); }, 15000));
    report.sidebarFrame = frame();
    // A pane row: another pane of the active workspace becomes the active one.
    const other = start.activePanes.find((id) => id !== start.active);
    if (other) {
      document.querySelector('.sidebar [data-session-id="' + other + '"]')?.click();
      report.rowClick = !!(await until(() => shows(other), 10000));
    } else {
      report.rowClick = 'no other pane';
    }
    // The add-pane menu: its terminal entry adds a tab to the active group.
    const before = await host();
    const tabsBefore = frame().tabs.length;
    document.querySelector('[data-testid=sidebar-workspace][data-workspace-id="' + before.workspace + '"] [data-testid=sidebar-add]')?.click();
    report.addMenu = !!(await until(() => document.querySelector('[data-testid="add-entry-shell"]'), 10000));
    document.querySelector('[data-testid="add-entry-shell"]')?.click();
    report.addedTab = report.addMenu && !!(await until(async () => (await host()).sessions === before.sessions + 1 && frame().tabs.length === tabsBefore + 1, 10000));
    // A tab of the new pane's group and back: each switch the host's and the page's.
    const added = (await host()).active;
    const group = [...document.querySelectorAll('[data-testid=tab-group]')].find((g) => g.querySelector('[data-tab-id="' + added + '"]') && g.querySelectorAll('[data-testid=tab]').length >= 2);
    const sibling = group && [...group.querySelectorAll('[data-testid=tab]')].map((t) => t.dataset.tabId).find((id) => id !== added);
    if (sibling) {
      report.tabSwitchFirstMs = await timeTab(sibling);
      const away = !!(await until(() => shows(sibling), 10000));
      report.tabSwitchMountedMs = await timeTab(added);
      const back = !!(await until(() => shows(added), 10000));
      report.tabClick = away && back;
    } else {
      report.tabClick = false;
    }
    // The columns preset, from the header's layout button: two groups side by side.
    document.querySelector('[data-testid=layout-select]')?.click();
    await until(() => document.querySelector('[data-testid=layout-preset-columns]'), 10000);
    document.querySelector('[data-testid=layout-preset-columns]')?.click();
    report.split = !!(await until(() => { const f = frame(); return f.groups >= 2 && f.splits >= 1; }, 10000));
    report.afterSplit = frame();
  } catch (error) {
    report.error = String(error);
  }
  await invoke('smoke_probe', { probe: report });
})();"#;

/// The gitCreate step's probe; `__URL__` is a JS string literal, `__WORKSPACE__` a plain id.
pub const CREATE_PROBE: &str = r#"(async () => {
  const until = async (test, ms) => { const end = performance.now() + ms; while (performance.now() < end) { const v = await test(); if (v) return v; await new Promise((r) => setTimeout(r, 50)); } return test(); };
  const invoke = (command, args) => window.__TAURI_INTERNALS__.invoke(command, args);
  const type = (field, text) => { Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set.call(field, text); field.dispatchEvent(new Event('input', { bubbles: true })); };
  const url = __URL__, workspace = '__WORKSPACE__';
  const report = { url };
  try {
    document.querySelector('[data-testid=sidebar-workspace][data-workspace-id="' + workspace + '"] [data-testid=sidebar-add]')?.click();
    report.menu = !!(await until(() => document.querySelector('[data-testid="add-entry-fromGit"]'), 10000));
    document.querySelector('[data-testid="add-entry-fromGit"]')?.click();
    const field = await until(() => document.querySelector('[data-testid=workspace-git-url]'), 10000);
    report.sheet = !!field;
    type(field, url);
    report.reachable = !!(await until(() => document.querySelector('[data-testid=workspace-git-probe].tone-text-done'), 30000));
    report.probe = document.querySelector('[data-testid=workspace-git-probe]')?.textContent ?? null;
    report.name = await until(() => document.querySelector('[data-testid=workspace-git-name]')?.value || null, 5000);
    const create = await until(() => { const b = document.querySelector('[data-testid=workspace-git-create]'); return b && !b.disabled ? b : null; }, 10000);
    report.createEnabled = !!create;
    create?.click();
    report.closed = !!(await until(() => !document.querySelector('[data-testid=workspace-git-url]'), 60000));
    report.cloneError = document.querySelector('[data-testid=workspace-git-clone-error]')?.textContent ?? null;
  } catch (error) {
    report.error = String(error);
  }
  await invoke('smoke_probe', { probe: report });
})();"#;

/// The reload step's probe, run in the page loaded again: the rename sheet the host's flow still waits on is drawn again (sent once
/// more at `ready`), with the name it was opened with (`__NAME__`, a JS string literal); Esc answers it, which ends the flow.
pub const RELOAD_PROBE: &str = r#"(async () => {
  const until = async (test, ms) => { const end = performance.now() + ms; while (performance.now() < end) { const v = await test(); if (v) return v; await new Promise((r) => setTimeout(r, 50)); } return test(); };
  const invoke = (command, args) => window.__TAURI_INTERNALS__.invoke(command, args);
  const name = __NAME__;
  const report = {};
  try {
    const field = await until(() => document.querySelector('[data-testid=dialog] [data-testid=rename-field]'), 15000);
    report.redrawn = !!field;
    report.current = field?.value ?? null;
    field?.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    report.dismissed = !!(await until(() => !document.querySelector('[data-testid=dialog]'), 5000));
    report.flowEnded = !!(await until(async () => (await invoke('smoke_surface', { paneId: '', action: 'dialogs' })).open === 0, 5000));
    report.sameName = report.current === name;
  } catch (error) {
    report.error = String(error);
  }
  await invoke('smoke_probe', { probe: report });
})();"#;

/// The reload step's check.
pub fn reload_checks(probe: &Value) -> Value {
    let yes = |key: &str| probe.get(key) == Some(&Value::Bool(true));
    json!({"dialogResentAfterReload": probe.get("error").is_none() && yes("redrawn") && yes("sameName") && yes("dismissed") && yes("flowEnded")})
}

/// What the host holds for the frame probe (`smoke_surface{action:'state'}`): the active pane and workspace, the number of panes and
/// the active workspace's panes.
pub fn state(host: &Host) -> Value {
    let view = host.view();
    let s = &view.snapshot;
    let workspace = s.active_workspace_id.clone();
    let active_panes: Vec<&str> = s.sessions().iter().filter(|p| Some(p.workspace_id()) == workspace.as_deref()).map(|p| p.id()).collect();
    json!({"active": s.active_session_id, "workspace": workspace, "sessions": s.sessions().len(), "activePanes": active_panes})
}

/// The frame step's checks.
pub fn frame_checks(frame: &Value) -> Value {
    let yes = |key: &str| frame.get(key) == Some(&Value::Bool(true));
    json!({
        "frameSidebar": frame.get("error").is_none() && yes("sidebar"),
        // A workspace of one pane has no other row to click.
        "frameRowClick": yes("rowClick") || frame.get("rowClick") == Some(&json!("no other pane")),
        "frameAddMenu": yes("addMenu") && yes("addedTab"),
        "frameTabClick": yes("tabClick"),
        "frameSplit": yes("split"),
    })
}

/// The fixture's own git: no user or system configuration, the app's `GIT_*` variables gone.
fn fixture_git(git: &Path, settings: &Path, folder: &Path, arguments: &[&str]) -> SpawnRequest {
    let mut all = vec!["-C".to_string(), folder.to_string_lossy().into_owned()];
    all.extend(arguments.iter().map(|a| a.to_string()));
    let mut request = SpawnRequest::new(git, all, folder);
    for (key, _) in std::env::vars() {
        if key.to_ascii_uppercase().starts_with("GIT_") {
            request = request.without(key);
        }
    }
    request.env("GIT_CONFIG_GLOBAL", settings.to_string_lossy()).env("GIT_CONFIG_NOSYSTEM", "1").env("GIT_TERMINAL_PROMPT", "0")
}

/// A bare repository with one commit in a new temporary folder: (the bare repository, the folder the clone goes into).
pub async fn bare_repository(host: &Arc<Host>) -> Result<(PathBuf, PathBuf), String> {
    let git = crate::workspace_create::git_executable().ok_or("git is not on PATH")?;
    let root = std::env::temp_dir().join(format!("MightyTauriCreateSmoke_{}", mighty_core::wire::new_id()));
    let (work, location) = (root.join("work"), root.join("clones"));
    for folder in [&work, &location] {
        std::fs::create_dir_all(folder).map_err(|e| e.to_string())?;
    }
    let settings = root.join("fixture.gitconfig");
    std::fs::write(&settings, "").map_err(|e| e.to_string())?;
    std::fs::write(work.join("README.md"), "hello\n").map_err(|e| e.to_string())?;
    let bare = root.join("remote.git");
    let (work_text, bare_text) = (work.to_string_lossy().into_owned(), bare.to_string_lossy().into_owned());
    let steps: [(&Path, Vec<&str>); 4] = [
        (work.as_path(), vec!["init", "-q"]),
        (work.as_path(), vec!["add", "README.md"]),
        (
            work.as_path(),
            vec![
                "-c",
                "user.name=Smoke",
                "-c",
                "user.email=smoke@example.invalid",
                "-c",
                "commit.gpgsign=false",
                "commit",
                "-q",
                "-m",
                "first",
            ],
        ),
        (root.as_path(), vec!["clone", "-q", "--bare", &work_text, &bare_text]),
    ];
    for (folder, arguments) in steps {
        let request = fixture_git(&git, &settings, folder, &arguments);
        let captured = mighty_platform::process::capture(host.processes.as_ref(), request, None, Duration::from_secs(120), 1 << 20)
            .await
            .map_err(|e| e.to_string())?;
        if captured.exit_code != 0 {
            return Err(format!("git {}: {}", arguments.join(" "), String::from_utf8_lossy(&captured.stderr)));
        }
    }
    Ok((bare, location))
}

/// The gitCreate step's checks: the sheet drove the clone, its progress was pushed, and the host listed the clone as a workspace.
pub fn create_checks(probe: &Value, host: &Host, location: Option<&Path>, progress_pushes: usize) -> Value {
    let yes = |key: &str| probe.get(key) == Some(&Value::Bool(true));
    let target = location.map(|l| l.join("remote"));
    let listed = target.as_ref().is_some_and(|target| {
        let view = host.view();
        let path = target.to_string_lossy();
        view.snapshot.workspaces().iter().any(|w| w.path() == path) && view.snapshot.active_workspace_id.is_some()
    });
    let cloned = target.as_ref().is_some_and(|t| t.join("README.md").is_file() && t.join(".git").is_dir());
    json!({
        "gitCreateSheet": probe.get("error").is_none() && yes("menu") && yes("sheet"),
        "gitCreateProbe": yes("reachable") && probe.get("name") == Some(&json!("remote")),
        "gitCreateClone": yes("createEnabled") && yes("closed") && cloned && listed && progress_pushes > 0,
    })
}
