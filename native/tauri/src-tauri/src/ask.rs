//! The composer's "send to" switch on a Claude agent pane (C# MainWindow.AskTargets.cs, M/MightyCode/AppStore+AskTargets.swift):
//! another installed CLI asked through oh-my-claudecode's ask skill (mighty-composer `ask_targets`). Which target executables this
//! computer has is read off the page's thread with the slash catalogue, at most every 30 s; whether a pane's catalogue has the skill
//! is noted by each scan (`SlashCatalogCache`). A saved choice that is no longer offered reads as Claude, and the pane says so once.
//! The phone's side of the switch is W8's.

use std::collections::{HashMap, HashSet};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use mighty_composer::ask_targets::{self, AskTarget};
use mighty_composer::slash::{self, SlashCatalogCache, SlashCommand};
use mighty_core::model::{AppSnapshot, RunSession};
use tauri::Manager as _;

use crate::host::{Host, lock};

/// `AskWait`: how long a send with a saved choice not yet known to stand waits for the pane's catalogue and the PATH read.
const ASK_WAIT: Duration = Duration::from_secs(2);

#[derive(Default)]
struct Probe {
    /// `askProbeFinished`: until the PATH was read conclusively once, no saved target is judged missing.
    finished: bool,
    in_flight: bool,
    /// When the PATH was last read successfully, and when the next read may start (30 s after one that worked, 2 s after one that
    /// failed).
    probed_at: Option<Instant>,
    next_at: Option<Instant>,
}

/// The window's part of the switch (C# `installedAskExecutables`, `askProbe*`, `askFallbackNotices`, `slashCatalogs`,
/// `slashScansInFlight`).
#[derive(Default)]
struct Ask {
    installed: Mutex<HashSet<String>>,
    probe: Mutex<Probe>,
    /// A pane's notice that its saved target is gone and its sends go to Claude, until it is dismissed or another target is chosen.
    notices: Mutex<HashMap<String, String>>,
    catalogs: Mutex<SlashCatalogCache>,
    scans: Mutex<HashSet<String>>,
}

fn switch(host: &Host) -> Arc<Ask> {
    if let Some(ask) = host.app.try_state::<Arc<Ask>>() {
        return ask.inner().clone();
    }
    host.app.manage(Arc::new(Ask::default()));
    host.app.state::<Arc<Ask>>().inner().clone()
}

/// `AskCatalogPath`: the folder a pane's catalogue is kept under — its workspace's, none for a remote one.
fn catalog_path(state: &AppSnapshot, pane: &RunSession) -> Option<String> {
    state.workspace(pane.workspace_id()).filter(|w| w.remote.is_none()).map(|w| w.path().to_string())
}

/// `AsksOtherCli`: a Claude agent pane of this computer; a remote workspace's pane runs on another computer, whose CLIs this one
/// cannot see.
fn asks_other_cli(state: &AppSnapshot, pane: &RunSession) -> bool {
    pane.kind() == "claude" && pane.provider() == "claude" && state.workspace(pane.workspace_id()).is_some_and(|w| w.remote.is_none())
}

/// `AskTargetsFor(pane)`: the targets a pane's switch offers, in the menu's order; empty hides it.
pub fn targets_for(host: &Host, state: &AppSnapshot, pane: &RunSession) -> Vec<AskTarget> {
    let ask = switch(host);
    let installed = lock(&ask.installed).clone();
    let remote = state.workspace(pane.workspace_id()).is_none_or(|w| w.remote.is_some());
    let offers = lock(&ask.catalogs).offers_ask(pane.provider(), catalog_path(state, pane).as_deref());
    mighty_composer::composer::ask_targets_for(pane, remote, &installed, offers)
}

/// `AskSendTarget(pane)`: the target a pane's sends go to now; None for Claude.
pub fn send_target(host: &Host, state: &AppSnapshot, pane: &RunSession) -> Option<AskTarget> {
    pane.ask_target.as_ref()?;
    ask_targets::effective(pane.ask_target.as_deref(), &targets_for(host, state, pane))
}

/// `askFallbackNotices[id]`.
pub fn notice(host: &Host, id: &str) -> Option<String> {
    lock(&switch(host).notices).get(id).cloned()
}

/// `AskTargetsKnown`: the PATH read once and the pane's catalogue scanned.
fn known(ask: &Ask, state: &AppSnapshot, pane: &RunSession) -> bool {
    lock(&ask.probe).probed_at.is_some() && lock(&ask.catalogs).offers_ask(pane.provider(), catalog_path(state, pane).as_deref()).is_some()
}

/// `RefreshAskExecutables`: reads which target executables this computer has (its PATH with PATHEXT and the installers' folders, as
/// the setup screen finds a CLI), at most every 30 s.
pub fn refresh_executables(host: &Host) {
    let ask = switch(host);
    {
        let mut probe = lock(&ask.probe);
        if probe.in_flight || probe.next_at.is_some_and(|next| Instant::now() < next) {
            return;
        }
        probe.in_flight = true;
    }
    let Some(environment) = host.runs().map(|runs| runs.catalog.environment()) else {
        lock(&ask.probe).in_flight = false;
        return;
    };
    let Some(shared) = host.app.try_state::<Arc<Host>>().map(|h| h.inner().clone()) else {
        lock(&ask.probe).in_flight = false;
        return;
    };
    tauri::async_runtime::spawn(async move {
        let read = tauri::async_runtime::spawn_blocking(move || {
            let values = environment.current().values;
            ask_targets::ALL
                .iter()
                .map(|t| t.executable)
                .filter(|name| !crate::platform::cli_copies(name, &values).is_empty())
                .map(str::to_string)
                .collect::<HashSet<String>>()
        })
        .await;
        let ask = switch(&shared);
        // A read that failed leaves the PATH unknown: a saved choice is never taken as gone, or sent unwrapped, on its account; the
        // next send or scan reads it again after a short pause.
        let Ok(found) = read else {
            let mut probe = lock(&ask.probe);
            probe.in_flight = false;
            probe.next_at = Some(Instant::now() + Duration::from_secs(2));
            return;
        };
        {
            let mut probe = lock(&ask.probe);
            let now = Instant::now();
            probe.in_flight = false;
            probe.probed_at = Some(now);
            probe.next_at = Some(now + Duration::from_secs(30));
        }
        // This computer's own environment: as the Mac's login shell, it may judge a target gone.
        let (installed, conclusive) = ask_targets::probed(&lock(&ask.installed).clone(), &found, true);
        let changed = {
            let mut current = lock(&ask.installed);
            let changed = *current != installed;
            *current = installed;
            changed
        };
        if changed {
            targets_changed(&shared);
        }
        if !conclusive {
            return;
        }
        lock(&ask.probe).finished = true;
        reconcile(&shared);
    });
}

/// `RefreshAskCatalog(pane, whenStale)`: scans a Claude agent pane's catalogue so its switch can show — when it was never scanned,
/// or with `when_stale` also once it is older than 30 s. The PATH is read again with it.
pub fn refresh_catalog(host: &Host, state: &AppSnapshot, pane: &RunSession, when_stale: bool) {
    if !asks_other_cli(state, pane) {
        return;
    }
    let ask = switch(host);
    let path = catalog_path(state, pane);
    {
        let catalogs = lock(&ask.catalogs);
        let fresh = if when_stale {
            !catalogs.is_stale(pane.provider(), path.as_deref())
        } else {
            catalogs.offers_ask(pane.provider(), path.as_deref()).is_some()
        };
        if fresh {
            return;
        }
    }
    let key = format!("{}|{}", pane.provider(), path.as_deref().unwrap_or(""));
    if !lock(&ask.scans).insert(key.clone()) {
        return;
    }
    // Only a scan that starts reads the PATH again with it.
    refresh_executables(host);
    let Some(shared) = host.app.try_state::<Arc<Host>>().map(|h| h.inner().clone()) else {
        lock(&ask.scans).remove(&key);
        return;
    };
    let provider = pane.provider().to_string();
    let locale = host.locale;
    tauri::async_runtime::spawn(async move {
        let (scan_provider, scan_path) = (provider.clone(), path.clone());
        let scanned = tauri::async_runtime::spawn_blocking(move || {
            let home = crate::composer::dirs_home();
            slash::commands(&scan_provider, scan_path.as_deref().map(std::path::Path::new), &home, &slash::DiskFiles, &locale)
        })
        .await;
        // No catalogue: the switch stays hidden.
        if let Ok(commands) = scanned {
            set_catalog(&shared, &provider, path.as_deref(), commands);
        }
        lock(&switch(&shared).scans).remove(&key);
    });
}

/// `SetSlashCatalog`: keeps a scanned catalogue (the slash palette's too). Whether oh-my-claudecode's ask is in it decides the
/// switch, so a change redraws the composers, and a saved choice is judged again. Never called under the view's lock.
pub fn set_catalog(host: &Host, provider: &str, path: Option<&str>, commands: Vec<SlashCommand>) {
    let ask = switch(host);
    let changed = {
        let mut catalogs = lock(&ask.catalogs);
        let had = catalogs.offers_ask(provider, path);
        catalogs.set(provider, path, commands);
        had != catalogs.offers_ask(provider, path)
    };
    refresh_executables(host);
    if changed {
        targets_changed(host);
    }
    reconcile(host);
}

/// `AskTargetsChanged`: what a switch offers moved — every pane's composer is drawn again.
fn targets_changed(host: &Host) {
    host.panes_changed();
}

/// `ReconcileAskTargets`: a saved target that is no longer offered (oh-my-claudecode or its CLI gone) reads as Claude — the pane says
/// so once and the choice is cleared. Judged only after the PATH was read and the pane's catalogue scanned.
fn reconcile(host: &Host) {
    let ask = switch(host);
    if !lock(&ask.probe).finished {
        return;
    }
    let gone: Vec<(String, String)> = {
        let view = host.view();
        view.snapshot
            .sessions()
            .iter()
            .filter(|pane| pane.ask_target.is_some())
            .filter(|pane| {
                let scanned = asks_other_cli(&view.snapshot, pane)
                    && lock(&ask.catalogs).offers_ask(pane.provider(), catalog_path(&view.snapshot, pane).as_deref()).is_some();
                ask_targets::fell_back(pane.ask_target.as_deref(), &targets_for(host, &view.snapshot, pane), true, scanned)
            })
            .map(|pane| (pane.id().to_string(), pane.ask_target.clone().unwrap_or_default()))
            .collect()
    };
    for (id, saved) in gone {
        let name = ask_targets::ALL.iter().find(|t| t.id == saved).map_or(saved.clone(), |t| t.label.to_string());
        lock(&ask.notices).insert(id.clone(), host.locale.get_with("composer.ask.fellBack", &[("name", &name)]));
        // C# `Act(() => SetAskTarget(id, null))`: what it fails with shows in the banner.
        if let Err(error) = set_target(host, &id, None) {
            host.view().error = Some(error);
        }
    }
}

/// `SetAskTarget`: saves a pane's choice (None: Claude) and redraws its composer. A target the pane does not offer is refused.
pub fn set_target(host: &Host, id: &str, target: Option<String>) -> Result<(), String> {
    let ask = switch(host);
    let mut view = host.view();
    let Some(pane) = view.snapshot.session(id).cloned() else { return Err(host.locale.get("remote.error.paneNotFound")) };
    if let Some(target) = &target {
        if !targets_for(host, &view.snapshot, &pane).iter().any(|t| t.id == target) {
            return Err(host.locale.get("remote.error.badBody"));
        }
        lock(&ask.notices).remove(id);
    }
    if pane.ask_target != target {
        crate::composer::change(&mut view, id, |p| RunSession { ask_target: target.clone(), ..p.clone() })?;
    }
    if let Some(ui) = view.panes.get_mut(id) {
        ui.dirty = true;
    }
    Ok(())
}

/// `DismissAskNotice`: takes a pane's fallback notice down.
pub fn dismiss_notice(host: &Host, id: &str) {
    if lock(&switch(host).notices).remove(id).is_some()
        && let Some(ui) = host.view().panes.get_mut(id)
    {
        ui.dirty = true;
    }
}

/// `AwaitAskTargets`: waits briefly until a pane with a saved choice knows whether it still stands, so a send never goes unwrapped
/// just because its workspace was not scanned yet (M/AppStore+AskTargets.swift awaitAskTargets). Panes on Claude return at once.
pub async fn await_targets(host: &Host, id: &str) {
    let ask = switch(host);
    {
        let view = host.view();
        let Some(pane) = view.snapshot.session(id).filter(|p| p.ask_target.is_some()).cloned() else { return };
        if !asks_other_cli(&view.snapshot, &pane) || known(&ask, &view.snapshot, &pane) {
            return;
        }
        refresh_catalog(host, &view.snapshot, &pane, true);
    }
    refresh_executables(host);
    let deadline = Instant::now() + ASK_WAIT;
    while Instant::now() < deadline {
        let waiting = {
            let view = host.view();
            view.snapshot.session(id).is_some_and(|pane| !known(&ask, &view.snapshot, pane))
        };
        if !waiting {
            return;
        }
        tokio::time::sleep(Duration::from_millis(25)).await;
    }
}

#[cfg(test)]
mod tests {
    use std::sync::Arc;

    use mighty_core::model::{AppSnapshot, RemoteWorkspaceReference, RunSession, Workspace};

    use super::{asks_other_cli, catalog_path};

    #[test]
    fn only_a_local_claude_agent_pane_asks_another_cli_and_its_catalogue_is_its_folders() {
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
        let pane = |workspace: &str, kind: &str, provider: &str| RunSession {
            workspace_id: Some(workspace.into()),
            kind: Some(kind.into()),
            provider: Some(provider.into()),
            ..RunSession::default()
        };
        let state =
            AppSnapshot { workspaces: Some(vec![local, remote]), sessions: Some(Vec::<Arc<RunSession>>::new()), ..AppSnapshot::default() };
        let claude = pane("w", "claude", "claude");
        assert!(asks_other_cli(&state, &claude));
        assert_eq!(catalog_path(&state, &claude).as_deref(), Some("C:\\work"));
        assert!(!asks_other_cli(&state, &pane("w", "claude", "codex")), "another agent has no switch");
        assert!(!asks_other_cli(&state, &pane("w", "shell", "claude")), "a shell pane has none");
        let far = pane("r", "claude", "claude");
        assert!(!asks_other_cli(&state, &far) && catalog_path(&state, &far).is_none(), "a remote workspace's pane runs on its host");
    }
}
