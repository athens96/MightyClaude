//! The work map in the app (C# MainWindow.WorkMap.cs, docs/work-map.md): one pane per workspace, drawn by the page alone, its model from
//! mighty-workmap's `WorkMapService` (the same one the phone will read through its router).
//!
//! - `openWorkMap{workspaceId}` opens the workspace's `workmap:<id>` pane, placed as the files pane is (C# `OpenSlimPane`, files.rs's
//!   `open_slim_pane`): beside the current pane, or a tab when there is no room for a split; it is never saved. A refusal shows in the
//!   error banner, as C#'s `Act`.
//! - `workMapState{workspaceId}` → the model (`WorkMap.Json`: nulls kept); `workMapSummarize{workspaceId, key}` starts a version's summary
//!   (one at a time per workspace; busy is an error the page shows) and the model is pushed again when it ends (`workmap`).
//! - `workMapOpenFile{workspaceId, path}` opens the file in the workspace's files pane, revealed, selected and previewed there (files.rs's
//!   `open_file_in_pane`), and pushes `files{paneId, path}`.
//!
//! The summaries are kept per workspace in `<profile>/work-map` when the app may write its profile, else in this machine's local folder
//! for the profile (as the picture cache is). The requests' edited files are read from the files pane's file originals (files.rs).

use std::path::PathBuf;
use std::sync::Arc;

use mighty_core::model::Workspace;
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
    // The file originals the files pane keeps (files.rs: the profile's with --allow-state-write, else this machine's own).
    let originals = host
        .app
        .try_state::<Arc<crate::files::Files>>()
        .map_or_else(|| host.data_dir.join("file-originals"), |files| files.originals.directory().to_path_buf());
    let service = WorkMapService::new(store, git, Arc::new(move || state.view().snapshot.clone()), originals, finder, runner, host.locale);
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

/// `OpenWorkMapPane`: the workspace's work map pane, opened as the files pane is (files.rs's `open_slim_pane`, C# `OpenSlimPane`).
pub fn open_work_map(host: &Host, view: &mut View, workspace_id: Option<&str>) -> Result<(), String> {
    crate::files::open_slim_pane(view, &host.locale, workspace_id, pane::KIND, "workMap.title")
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
    // A changed file opens in the workspace's files pane, revealed, selected and previewed there (files.rs).
    methods.add("workMapOpenFile", |host: Arc<Host>, args| async move {
        let Some(workspace) = workspace_of(&host.view(), &args) else { return Err("Workspace not found.".into()) };
        crate::files::open_file_in_pane(&host, workspace.id(), text(&args, "path").unwrap_or("")).await.map(Value::Bool)
    });
}
