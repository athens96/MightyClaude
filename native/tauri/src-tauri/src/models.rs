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
                let busy = s.status() == "running" || view.panes.get(s.id()).is_some_and(|u| u.view.starting);
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
    let current = models.refresh.wait_current(&key, None).await;
    if current {
        let prior = lock(&models.prior).remove(id);
        let value = models.refresh.value(&key);
        let same_provider = host.view().snapshot.session(id).is_some_and(|s| s.provider() == key.provider);
        if let Some(value) = value
            && same_provider
        {
            reconcile(host, id, prior.as_ref(), &value.model_catalog);
        }
    }
    set_checking(host, id, false);
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
    use std::sync::Arc;

    use mighty_core::model::{AppSnapshot, RemoteWorkspaceReference, RunSession, Workspace};

    use super::context_of;

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
