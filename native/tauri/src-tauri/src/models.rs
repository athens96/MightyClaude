//! Each pane's model list keeps itself right (C# MainWindow.ModelRefresh.cs, M/AppStore+ModelCatalogs.swift): a selected pane's
//! list is read again, a run waits for the list as the CLI has it now, and a model or effort the list no longer has gives way with a
//! line in the transcript. When to read is mighty-run's rule (`ModelCatalogRefresh`); what changes is `ModelSelectionSupport`'s.
//! A newer list becomes the provider's runtime the composer shows (runs.rs `Runs::keep`). A smoke run asks no CLI and reconciles
//! nothing, as C#'s.
//!
//! One difference: C# reads a selected pane's list again only while the runtime in hand is its folder's (a workspace selected
//! reads the whole runtime anew); the run engine here reads the CLIs once at start (runs.rs) and keeps one runtime, so a selected
//! pane's list is read whatever its folder.

use std::collections::HashMap;
use std::sync::{Arc, Mutex};

use mighty_core::model::AppSnapshot;
use mighty_run::model::{ModelCatalog, ProviderRuntime};
use mighty_run::model_selection::{self, Changed, Loader, ModelCatalogRefresh, ModelContext};
use tauri::Manager as _;

use crate::host::{Host, lock};
use crate::panes;

/// A pane's model and effort.
type Choice = (String, String);

/// The window's part of the model lists (C# `modelRefreshes`, `modelPriorCatalogs`, `modelRefreshSelections`,
/// `modelsSelectedSession`).
struct Models {
    refresh: ModelCatalogRefresh,
    /// The list a pane had before a newer one came while it ran; it is put right with both before its next request.
    prior: Mutex<HashMap<String, ModelCatalog>>,
    /// Each pane's model and effort when a reading began: a choice made meanwhile is the user's and stays.
    selections: Mutex<HashMap<ModelContext, HashMap<String, Choice>>>,
    selected: Mutex<Option<String>>,
}

/// `ModelRefreshes`, made on first use once the run engine is there; None in a smoke.
fn models(host: &Host) -> Option<Arc<Models>> {
    if host.smoke.is_some() {
        return None;
    }
    if let Some(models) = host.app.try_state::<Arc<Models>>() {
        return Some(models.inner().clone());
    }
    let runs = host.runs()?.clone();
    let weak = Arc::downgrade(host.app.try_state::<Arc<Host>>()?.inner());
    let loader: Loader = {
        let weak = weak.clone();
        Arc::new(move |key: ModelContext| {
            let (weak, runs) = (weak.clone(), runs.clone());
            Box::pin(async move {
                if let Some(host) = weak.upgrade() {
                    remember_selections(&host, &key);
                }
                Ok(runs.catalog.refresh_provider(&key.provider, Some(&key.path), false).await)
            })
        })
    };
    let changed: Changed = Arc::new(move |key: &ModelContext, previous: Option<&ProviderRuntime>, refreshed: &ProviderRuntime| {
        if let Some(host) = weak.upgrade() {
            accept(&host, key, previous, refreshed);
        }
    });
    let refresh = ModelCatalogRefresh::new(tauri::async_runtime::handle().inner().clone(), loader, changed, None);
    host.app.manage(Arc::new(Models {
        refresh,
        prior: Mutex::new(HashMap::new()),
        selections: Mutex::new(HashMap::new()),
        selected: Mutex::new(None),
    }));
    Some(host.app.state::<Arc<Models>>().inner().clone())
}

/// `modelRefreshes?.Shutdown()` at quit (C# `QuitAsync`, MainWindow.cs:132): no list is read or taken in from here on, so no pane is
/// reconciled, and saved, after the last save. Makes nothing when no list was ever asked for.
pub fn shutdown(host: &Host) {
    if let Some(models) = host.app.try_state::<Arc<Models>>() {
        models.refresh.shutdown();
    }
}

/// `ModelContextOf`: an agent pane's provider in its workspace's folder on this computer; a remote workspace's models are its host's.
fn context_of(state: &AppSnapshot, id: &str) -> Option<ModelContext> {
    let session = state.session(id).filter(|s| s.kind() == "claude")?;
    let workspace = state.workspace(session.workspace_id()).filter(|w| w.remote.is_none())?;
    Some(ModelContext::new(workspace.id.as_deref().unwrap_or(""), workspace.path(), session.provider()))
}

/// `RememberModelSelections`.
fn remember_selections(host: &Host, key: &ModelContext) {
    let Some(models) = models(host) else { return };
    let chosen: HashMap<String, Choice> = host
        .view()
        .snapshot
        .sessions()
        .iter()
        .filter(|s| s.workspace_id() == key.workspace_id && s.provider() == key.provider)
        .map(|s| (s.id().to_string(), (s.model().to_string(), s.settings.clone().unwrap_or_default().effort)))
        .collect();
    lock(&models.selections).insert(key.clone(), chosen);
}

/// `RefreshModels(sessionId, invalidate)`: reads a pane's model list again; `invalidate` when the one in hand is known to be another
/// agent's or account's (M/AppStore+ModelCatalogs.swift:10-19).
pub fn refresh(host: &Host, state: &AppSnapshot, id: &str, invalidate: bool) {
    let Some(models) = models(host) else { return };
    if let Some(key) = context_of(state, id) {
        drop(models.refresh.request(&key, true, invalidate));
    }
}

/// `ModelsAfterAccountChange(provider)` (MainWindow.ModelRefresh.cs:39, W6-C): a sign-in or sign-out changed what that CLI lists, so
/// its lists in hand are old in every workspace (`ModelCatalogRefresh.Invalidate`): none is used again and the next request reads anew.
pub fn after_account_change(host: &Host, provider: &str) {
    if let Some(models) = models(host) {
        models.refresh.invalidate(Some(provider));
    }
}

/// `RefreshPaneModels` → `RefreshRuntime` → `ReloadProviderModels` (MainWindow.WorkflowParity.cs:27, MainWindow.Settings.cs:955-972):
/// the model menu's refresh row, and a new conversation, read the pane's list again now, a reading already under way reading again
/// first; the list read is its folder's newest, so a run about to start within the minute takes it rather than an older one, and the
/// panes are reconciled with it (`ModelRefreshes.Store` → `AcceptModels`). C# reads every provider of the active workspace; the run
/// engine here reads the pane's provider in its folder (the note above). A remote workspace's pane and a smoke read nothing.
pub async fn reload(host: &Host, id: &str) {
    let Some(models) = models(host) else { return };
    let Some(key) = context_of(&host.view().snapshot, id) else { return };
    read_again(&models.refresh, &key).await;
}

/// The reading [`reload`] waits for: forced, and the list in hand not used (`invalidate`), however new it is.
async fn read_again(refresh: &ModelCatalogRefresh, key: &ModelContext) -> Option<ProviderRuntime> {
    refresh.request(key, true, true).wait().await
}

/// `RefreshSelectedPaneModels`: selecting a pane reads its list again, however it was selected (M/AppStore.swift:546, 565). Looked
/// at by the pane clock (panes.rs).
pub fn follow_selection(host: &Host) {
    let Some(models) = models(host) else { return };
    let (selected, key) = {
        let view = host.view();
        let selected = view.snapshot.active_session_id.clone();
        let key = selected.as_deref().and_then(|id| context_of(&view.snapshot, id));
        (selected, key)
    };
    {
        let mut seen = lock(&models.selected);
        if *seen == selected {
            return;
        }
        seen.clone_from(&selected);
    }
    if let Some(key) = key {
        drop(models.refresh.request(&key, true, false));
    }
}

/// `AcceptModels`: a newly read list (M/AppStore+ModelCatalogs.swift:32-45) becomes the provider's list on screen, and every idle pane
/// of that folder and provider is reconciled with it. A pane that runs, or is about to, is left alone until its next request; so is a
/// choice made while the list was being read.
fn accept(host: &Host, key: &ModelContext, previous: Option<&ProviderRuntime>, refreshed: &ProviderRuntime) {
    let (Some(models), Some(runs)) = (models(host), host.runs().cloned()) else { return };
    let panes: Vec<(String, bool, (String, String))> = {
        let view = host.view();
        if !view.snapshot.workspaces().iter().any(|w| w.id.as_deref() == Some(key.workspace_id.as_str()) && w.path() == key.path) {
            return;
        }
        view.snapshot
            .sessions()
            .iter()
            .filter(|s| s.workspace_id() == key.workspace_id && s.provider() == key.provider && s.kind() == "claude")
            .map(|s| {
                let busy = s.status() == "running" || update_starting(view.panes.get(s.id()));
                (s.id().to_string(), busy, (s.model().to_string(), s.settings.clone().unwrap_or_default().effort))
            })
            .collect()
    };
    // Every reading is a new object: the panes are drawn again only when it says something new.
    if !ModelCatalogRefresh::same(runs.runtime(&key.provider).as_ref(), Some(refreshed)) {
        runs.keep(refreshed.clone());
        host.panes_changed();
    }
    let chosen = lock(&models.selections).get(key).cloned();
    for (id, busy, now) in panes {
        if busy {
            if let Some(previous) = previous {
                lock(&models.prior).entry(id).or_insert_with(|| previous.model_catalog.clone());
            }
            continue;
        }
        if chosen.as_ref().and_then(|c| c.get(&id)) != Some(&now) {
            continue;
        }
        reconcile(host, &id, previous.map(|p| &p.model_catalog), &refreshed.model_catalog);
    }
}

/// `UpdateStarting` (C# `starting || queueStarting`): the pane's send, or its queue's start, is under way.
fn update_starting(ui: Option<&panes::PaneUi>) -> bool {
    ui.is_some_and(|u| u.view.starting || u.view.queue_starting)
}

/// `ReconcileModel`: one pane's model and effort put right against a list, said in its transcript (`ModelSelectionSupport.Adjusted`);
/// the save follows.
fn reconcile(host: &Host, id: &str, previous: Option<&ModelCatalog>, refreshed: &ModelCatalog) {
    let mut view = host.view();
    let Some(session) = view.snapshot.session(id).cloned() else { return };
    if model_selection::adjusted(&session, previous, refreshed).is_none() {
        return;
    }
    let _ = crate::composer::change(&mut view, id, |pane| {
        if pane.status() == "running" {
            pane.clone()
        } else {
            model_selection::adjusted(pane, previous, refreshed).unwrap_or_else(|| pane.clone())
        }
    });
}

/// `PrepareModels` (the pane view's and the window's): before a run starts its pane waits for the model list as the CLI has it now,
/// read again when the one in hand is older than a minute (M/AppStore+ModelCatalogs.swift:66-76), the editor saying it checks the
/// models meanwhile; then the pane is put right with it, and with the list it had before when a newer one came while it ran. A list
/// that cannot be read, or not within `ModelCatalogRefresh.PreRunWait`, holds nothing up.
pub async fn prepare(host: &Host, id: &str) {
    let Some(models) = models(host) else { return };
    let Some(key) = context_of(&host.view().snapshot, id) else { return };
    set_checking(host, id, true);
    if let Some(value) = list_for_run(&models.refresh, &key).await {
        let prior = lock(&models.prior).remove(id);
        let same_provider = host.view().snapshot.session(id).is_some_and(|s| s.provider() == key.provider);
        if same_provider {
            reconcile(host, id, prior.as_ref(), &value.model_catalog);
        }
    }
    set_checking(host, id, false);
}

/// The list a run about to start is put right with (`WaitCurrent`, then `Value`): the newest, once in hand; None when it could not
/// be read in time.
async fn list_for_run(refresh: &ModelCatalogRefresh, key: &ModelContext) -> Option<ProviderRuntime> {
    if refresh.wait_current(key, None).await { refresh.value(key) } else { None }
}

/// `checkingModels`: the editor's placeholder says the models are being checked.
fn set_checking(host: &Host, id: &str, checking: bool) {
    let mut view = host.view();
    if let Ok(ui) = panes::ui(&mut view, id) {
        ui.view.checking_models = checking;
        ui.dirty = true;
    }
}

#[cfg(test)]
mod tests {
    use std::sync::atomic::{AtomicUsize, Ordering};
    use std::sync::{Arc, Mutex};

    use mighty_core::model::{AppSnapshot, RemoteWorkspaceReference, RunSession, RunSettings, Workspace};
    use mighty_run::model::{ModelCatalog, ModelOption, ProviderRuntime};
    use mighty_run::model_selection::{self, Changed, Loader, ModelCatalogRefresh, ModelContext};
    use mighty_run::providers;

    use super::{context_of, list_for_run, read_again, update_starting};
    use crate::panes::PaneUi;

    /// A new list leaves a pane alone while its send or its queue's start is under way (`UpdateStarting`), until its next request.
    #[test]
    fn a_pane_whose_queue_is_starting_is_left_to_its_next_request() {
        let mut ui = PaneUi::default();
        assert!(!update_starting(Some(&ui)) && !update_starting(None), "an idle pane, or one without a view, is reconciled");
        ui.view.queue_starting = true;
        assert!(update_starting(Some(&ui)), "the queue's start under way");
        ui.view.queue_starting = false;
        ui.view.starting = true;
        assert!(update_starting(Some(&ui)), "a send under way");
    }

    fn codex(models: Vec<ModelOption>) -> ProviderRuntime {
        ProviderRuntime {
            id: "codex".into(),
            name: providers::name("codex").into(),
            available: true,
            version: Some("0.130.0".into()),
            detail: String::new(),
            model_catalog: ModelCatalog::new("cli", models, ""),
            capabilities: providers::capabilities("codex", None),
        }
    }

    fn model(value: &str, supports_effort: Option<bool>, levels: Option<&[&str]>) -> ModelOption {
        ModelOption {
            supports_effort,
            supported_effort_levels: levels.map(|l| l.iter().map(|s| s.to_string()).collect()),
            ..ModelOption::new(value, value, "")
        }
    }

    fn pane(model: &str, effort: &str) -> RunSession {
        RunSession {
            id: Some("p".into()),
            workspace_id: Some("w".into()),
            kind: Some("claude".into()),
            provider: Some("codex".into()),
            model: Some(model.into()),
            settings: Some(RunSettings { effort: effort.into(), ..RunSettings::default() }),
            ..RunSession::default()
        }
    }

    /// The model menu's refresh row reads the list into the model lists' own book (C# `RefreshPaneModels` → `ModelRefreshes.Store` →
    /// `AcceptModels`): a model and effort picked from the list it read are not put back by the next send, which would otherwise be
    /// reconciled against the list a run read less than a minute before; and the panes are reconciled with the new list.
    #[tokio::test]
    async fn a_model_picked_from_the_refreshed_list_survives_the_next_send() {
        // The CLI listed m1, and m2 without effort levels; it now lists m2 with them and has dropped m1.
        let before = codex(vec![model("default", None, None), model("m1", None, None), model("m2", Some(false), None)]);
        let after = codex(vec![model("default", None, None), model("m2", Some(true), Some(&["low", "high"]))]);
        let reads = Arc::new(AtomicUsize::new(0));
        let loader: Loader = {
            let (reads, before, after) = (reads.clone(), before.clone(), after.clone());
            Arc::new(move |_: ModelContext| {
                let value = if reads.fetch_add(1, Ordering::SeqCst) == 0 { before.clone() } else { after.clone() };
                Box::pin(async move { Ok(value) })
            })
        };
        // Each reading reported: the list before it, and it.
        type Reported = Vec<(Option<ProviderRuntime>, ProviderRuntime)>;
        let reported: Arc<Mutex<Reported>> = Arc::default();
        let changed: Changed = {
            let reported = reported.clone();
            Arc::new(move |_: &ModelContext, previous: Option<&ProviderRuntime>, refreshed: &ProviderRuntime| {
                reported.lock().unwrap().push((previous.cloned(), refreshed.clone()));
            })
        };
        let refresh = ModelCatalogRefresh::new(tokio::runtime::Handle::current(), loader, changed, None);
        let key = ModelContext::new("w", "C:\\work", "codex");

        // A send a moment ago read the list as it was.
        assert_eq!(list_for_run(&refresh, &key).await.as_ref(), Some(&before));
        // The refresh row reads it again at once, however new the one in hand.
        assert_eq!(read_again(&refresh, &key).await.as_ref(), Some(&after));
        assert_eq!(reads.load(Ordering::SeqCst), 2, "read again, not answered with the list in hand");
        // `AcceptModels` hears it with the list before: an idle pane on the model the CLI dropped gives way.
        let (previous, refreshed) = reported.lock().unwrap().last().cloned().expect("the new list is reported");
        assert_eq!(previous.as_ref(), Some(&before));
        let dropped = model_selection::adjusted(&pane("m1", "default"), Some(&before.model_catalog), &refreshed.model_catalog);
        assert_eq!(dropped.as_ref().map(|p| p.model()), Some("default"), "a pane on a dropped model is reconciled");

        // The user picks m2 at high from the new list; the next send takes the list the row read, without reading again, and keeps it.
        let picked = pane("m2", "high");
        let list = list_for_run(&refresh, &key).await.expect("the newest list is in hand");
        assert_eq!(reads.load(Ordering::SeqCst), 2);
        assert!(model_selection::adjusted(&picked, None, &list.model_catalog).is_none(), "the pick stands");
        // Against the list the send read before (what the row's reading used to leave in the book), the effort was put back.
        let reset = model_selection::adjusted(&picked, None, &before.model_catalog).expect("the older list resets the effort");
        assert_eq!(reset.settings.as_ref().map(|s| s.effort.as_str()), Some("default"));
        refresh.shutdown();
    }

    #[test]
    fn a_pane_reads_its_own_folders_list_and_a_remote_workspace_none() {
        let local = Workspace { id: Some("w".into()), path: Some("C:\\work".into()), ..Workspace::default() };
        let remote = Workspace {
            id: Some("r".into()),
            path: Some("/Users/someone/project".into()),
            remote: Some(RemoteWorkspaceReference {
                connection_id: Some("conn".into()),
                workspace_id: Some("host-ws".into()),
                host_name: Some("Host".into()),
                version: 2,
            }),
            ..Workspace::default()
        };
        let pane = |id: &str, workspace: &str, kind: &str, provider: &str| {
            Arc::new(RunSession {
                id: Some(id.into()),
                workspace_id: Some(workspace.into()),
                kind: Some(kind.into()),
                provider: Some(provider.into()),
                ..RunSession::default()
            })
        };
        let state = AppSnapshot {
            workspaces: Some(vec![local, remote]),
            sessions: Some(vec![pane("a", "w", "claude", "codex"), pane("b", "r", "claude", "claude"), pane("c", "w", "shell", "claude")]),
            ..AppSnapshot::default()
        };
        let key = context_of(&state, "a").expect("a local agent pane has a list");
        assert_eq!((key.workspace_id.as_str(), key.path.as_str(), key.provider.as_str()), ("w", "C:\\work", "codex"));
        assert!(context_of(&state, "b").is_none(), "a remote workspace's models are its host's");
        assert!(context_of(&state, "c").is_none(), "a shell pane has no models");
        assert!(context_of(&state, "missing").is_none());
    }
}
