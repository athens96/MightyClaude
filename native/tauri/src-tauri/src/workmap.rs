//! The work map in the app (C# MainWindow.WorkMap.cs, docs/work-map.md): one pane per workspace, drawn by the page alone, its model from
//! mighty-workmap's `WorkMapService` (the same one the phone will read through its router).
//!
//! - `openWorkMap{workspaceId}` opens the workspace's `workmap:<id>` pane, placed as the files pane is (C# `OpenSlimPane`): beside the
//!   current pane, or a tab when there is no room for a split; it is never saved. A refusal shows in the error banner, as C#'s `Act`.
//! - `workMapState{workspaceId}` → the model (`WorkMap.Json`: nulls kept); `workMapSummarize{workspaceId, key}` starts a version's summary
//!   (one at a time per workspace; busy is an error the page shows) and the model is pushed again when it ends (`workmap`).
//! - `workMapOpenFile{workspaceId, path}` opens the workspace's files pane and pushes `files{paneId, path}`, which the files pane takes as
//!   "show and preview this file".
//!
//! The summaries are kept per workspace in `<profile>/work-map` when the app may write its profile, else in this machine's local folder
//! for the profile (as the picture cache is).

use std::path::PathBuf;
use std::sync::Arc;

use mighty_core::layout;
use mighty_core::model::{AppSnapshot, PaneLayoutNode, RunSession, Workspace};
use mighty_core::shell::{effective_layout, layout_mode};
use mighty_workmap::service::Finder;
use mighty_workmap::summary::{self, Runner};
use mighty_workmap::{Git, WorkMapService, WorkMapStart, pane};
use serde_json::{Value, json};
use tauri::Manager;

use crate::host::{Host, Registry, View};
use crate::methods::text;

/// The app's one `WorkMapService`, made on first use once the run engine is up (it finds Claude through the engine's catalogue).
pub fn service(host: &Arc<Host>) -> Result<Arc<WorkMapService>, String> {
    if let Some(service) = host.app.try_state::<Arc<WorkMapService>>() {
        return Ok(service.inner().clone());
    }
    let runs = host.engine()?;
    let allowed = crate::args::parse(std::env::args().skip(1)).map(|o| o.allow_state_write).unwrap_or(false);
    let local = local_folder(host);
    let store = if allowed { host.data_dir.join("work-map") } else { local.join("work-map") };
    let git = Git::new(host.processes.clone(), local.join("git-no-hooks"));
    let state = host.clone();
    let catalog = runs.catalog.clone();
    let finder: Finder = Arc::new(move || {
        let catalog = catalog.clone();
        Box::pin(async move { catalog.find("claude").await })
    });
    let (processes, environment) = (host.processes.clone(), runs.catalog.environment());
    let runner: Runner = Arc::new(move |command, prompt, timeout| {
        let (processes, environment) = (processes.clone(), environment.clone());
        Box::pin(async move {
            let env = mighty_run::providers::claude_headless_environment(environment.as_ref());
            summary::run(processes.as_ref(), &command, &env, &prompt, timeout).await
        })
    });
    let service = WorkMapService::new(
        store,
        git,
        Arc::new(move || state.view().snapshot.clone()),
        host.data_dir.join("file-originals"),
        finder,
        runner,
        host.locale,
    );
    // A summary that ends (done or failed) is pushed to the page.
    let pushing = Arc::downgrade(host);
    service.on_changed(Arc::new(move |workspace_id: &str| {
        if let Some(host) = pushing.upgrade() {
            let workspace_id = workspace_id.to_string();
            tauri::async_runtime::spawn(async move { push(&host, &workspace_id).await });
        }
    }));
    host.app.manage(service);
    Ok(host.app.state::<Arc<WorkMapService>>().inner().clone())
}

/// This machine's own folder for the profile, as the picture cache's (`<local>/<kind>/<profile key>`).
fn local_folder(host: &Host) -> PathBuf {
    let name: String =
        host.data_dir.to_string_lossy().chars().map(|c| if c.is_ascii_alphanumeric() || c == '-' { c } else { '_' }).collect();
    let name = if name.len() > 80 { name[name.len() - 80..].to_string() } else { name };
    let base = std::env::var_os("LOCALAPPDATA").map(PathBuf::from).unwrap_or_else(std::env::temp_dir);
    base.join("dev.mightycode.tauri").join("work-map").join(name)
}

fn workspace_of(view: &View, args: &Value) -> Option<Workspace> {
    text(args, "workspaceId").and_then(|id| view.snapshot.workspace(id)).cloned()
}

/// `PushWorkMap`: the workspace's model to its pane.
async fn push(host: &Arc<Host>, workspace_id: &str) {
    let Some(workspace) = host.view().snapshot.workspace(workspace_id).cloned() else { return };
    let Ok(service) = service(host) else { return };
    let model = service.model(&workspace).await.to_value();
    let message = json!({"kind": "workmap", "paneId": pane::pane_id(workspace_id), "state": model});
    let view = host.view();
    host.push(&view, "workmap", message.to_string());
}

// ── the slim pane (C# `OpenSlimPane`) ───────────────────────────────────────────────────────────────────────────────────

fn save_layout(state: &mut AppSnapshot, workspace: &str, node: PaneLayoutNode) {
    state.pane_layouts.get_or_insert_with(Default::default).insert(workspace.to_string(), Some(node));
}

fn save_selection(state: &mut AppSnapshot, workspace: &str, id: &str) {
    state.pane_layout_active_session_ids.get_or_insert_with(Default::default).insert(workspace.to_string(), Some(id.to_string()));
}

fn save_mode(state: &mut AppSnapshot, workspace: &str, mode: &str) {
    state.pane_layout_modes.get_or_insert_with(Default::default).insert(workspace.to_string(), Some(mode.to_string()));
}

/// `FilePaneKind.Place`: the pane left of the current one's group (or of the first group holding another pane), or a tab in it when a
/// split has no room.
fn place(tree: &PaneLayoutNode, pane_id: &str, current: Option<&str>) -> PaneLayoutNode {
    let groups = layout::groups(tree);
    let target = groups
        .iter()
        .find(|g| current.is_some_and(|c| c != pane_id && g.session_ids().any(|id| id == c)))
        .or_else(|| groups.iter().find(|g| g.session_ids().any(|id| id != pane_id)));
    let Some(target) = target else { return tree.clone() };
    let split = layout::move_tab(tree, pane_id, target.id(), "left", None);
    if layout::groups(&split).iter().any(|g| g.session_ids().collect::<Vec<_>>() == [pane_id]) {
        return split;
    }
    layout::move_tab(tree, pane_id, target.id(), "center", None)
}

/// `SelectLayoutSession`: the pane's group shows it, and it becomes the active pane of its workspace, which becomes the active one.
fn select(view: &mut View, id: &str) -> Result<(), String> {
    view.shows_dashboard = false;
    let Some(workspace) = view.snapshot.session(id).map(|s| s.workspace_id().to_string()) else { return Ok(()) };
    let node = effective_layout(&view.snapshot, &workspace, &mut view.layout_defaults).map(|n| layout::select(&n, id));
    view.update(|state| {
        if let Some(node) = node {
            save_layout(state, &workspace, node);
        }
        save_selection(state, &workspace, id);
        state.active_workspace_id = Some(workspace.clone());
        state.active_session_id = Some(id.to_string());
    })
}

/// `OpenSlimPane`: shows the workspace's one pane of `kind` (`files`, `workmap`): selects it when it is open, otherwise adds it beside the
/// current pane. Never saved, so it does not come back after a restart. The error is the banner's words.
pub fn open_slim_pane(host: &Host, view: &mut View, workspace_id: Option<&str>, kind: &str, title_key: &str) -> Result<String, String> {
    let locale = host.locale;
    let state = view.snapshot.clone();
    let workspace = workspace_id
        .or(state.active_workspace_id.as_deref())
        .and_then(|id| state.workspace(id))
        .cloned()
        .ok_or_else(|| locale.get("window.error.addWorkspaceFirst"))?;
    // A remote workspace's folder is on its host: no such pane reads it here.
    if workspace.remote.is_some() {
        return Err(locale.get("remote.windows.workspace.paneUnavailable"));
    }
    let id = format!("{kind}:{}", workspace.id());
    if state.session(&id).is_some() {
        select(view, &id)?;
        return Ok(id);
    }
    if state.sessions().len() >= layout::MAXIMUM_SESSIONS {
        return Err(locale.get("files.error.noRoom"));
    }
    let current = if state.active_workspace_id.as_deref() == Some(workspace.id()) {
        state.active_session_id.clone()
    } else {
        state.pane_layout_active_session_ids.as_ref().and_then(|m| m.get(workspace.id())).cloned().flatten()
    };
    let pane = RunSession {
        id: Some(id.clone()),
        workspace_id: Some(workspace.id().to_string()),
        kind: Some(kind.to_string()),
        title: Some(locale.get(title_key)),
        ..RunSession::default()
    };
    // The pane joins the tree before it becomes the active one, as a new pane does (AddToLayout).
    let mut joined = state.clone();
    joined.sessions_mut().push(Arc::new(pane.clone()));
    let tree = effective_layout(&joined, workspace.id(), &mut view.layout_defaults);
    let placed = tree.map(|tree| layout::select(&place(&tree, &id, current.as_deref()), &id));
    let focus = layout_mode(&joined, workspace.id()) == "focus";
    let workspace_id = workspace.id().to_string();
    view.shows_dashboard = false;
    view.update(|state| {
        if state.session(&id).is_none() {
            state.sessions_mut().push(Arc::new(pane));
        }
        state.active_workspace_id = Some(workspace_id.clone());
        state.active_session_id = Some(id.clone());
        if let Some(placed) = placed {
            let split = placed.kind() == "split";
            save_layout(state, &workspace_id, placed);
            if !focus {
                save_mode(state, &workspace_id, if split { "custom" } else { "tabs" });
            }
        }
        save_selection(state, &workspace_id, &id);
    })?;
    Ok(id)
}

/// `OpenWorkMapPane`.
pub fn open_work_map(host: &Host, view: &mut View, workspace_id: Option<&str>) -> Result<String, String> {
    open_slim_pane(host, view, workspace_id, pane::KIND, "workMap.title")
}

pub fn register(methods: &mut Registry) {
    methods.add("openWorkMap", |host: Arc<Host>, args| async move {
        let mut view = host.view();
        if !view.dialog_open {
            view.shows_dashboard = false;
            if let Err(error) = open_work_map(&host, &mut view, text(&args, "workspaceId")) {
                view.error = Some(error);
            }
        }
        Ok(Value::Null)
    });
    methods.add("workMapState", |host: Arc<Host>, args| async move {
        let Some(workspace) = workspace_of(&host.view(), &args) else { return Err("Workspace not found.".into()) };
        let service = service(&host)?;
        Ok(service.model(&workspace).await.to_value())
    });
    methods.add("workMapSummarize", |host: Arc<Host>, args| async move {
        let Some(workspace) = workspace_of(&host.view(), &args) else { return Err("Workspace not found.".into()) };
        let service = service(&host)?;
        match service.start(&workspace, text(&args, "key").unwrap_or("")).await {
            WorkMapStart::Busy => Err(host.locale.get("workMap.error.busy")),
            // The page asks again with the summary marked as running.
            started => Ok(Value::Bool(started == WorkMapStart::Started)),
        }
    });
    // A changed file opens in the workspace's files pane, shown and previewed there.
    methods.add("workMapOpenFile", |host: Arc<Host>, args| async move {
        let path = text(&args, "path").unwrap_or("").replace('\\', "/");
        let (pane_id, root) = {
            let mut view = host.view();
            let Some(workspace) = workspace_of(&view, &args) else { return Err("Workspace not found.".into()) };
            if view.dialog_open || path.is_empty() || workspace.remote.is_some() {
                return Ok(Value::Bool(false));
            }
            let root = workspace.path().to_string();
            if mighty_core::workspace_files::resolve(&path, std::path::Path::new(&root)).is_none() {
                return Ok(Value::Bool(false));
            }
            match open_slim_pane(&host, &mut view, Some(workspace.id()), "files", "files.pane.title") {
                Ok(id) => (id, root),
                Err(error) => {
                    view.error = Some(error);
                    return Ok(Value::Bool(false));
                }
            }
        };
        let _ = root;
        let message = json!({"kind": "files", "paneId": pane_id, "path": path}).to_string();
        host.flush();
        {
            let view = host.view();
            host.push(&view, "files", message.clone());
        }
        // A files pane opened just now mounts after the flush: it hears the path once more when it is up.
        let again = host.clone();
        tauri::async_runtime::spawn(async move {
            tokio::time::sleep(std::time::Duration::from_millis(400)).await;
            let view = again.view();
            again.push(&view, "files", message);
        });
        Ok(Value::Bool(true))
    });
}
