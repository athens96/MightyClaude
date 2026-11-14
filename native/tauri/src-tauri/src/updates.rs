//! CLI updates, automatic updates and their holds (C# MainWindow.AutomaticUpdates.cs, MainWindow.UpdateAdmission.cs, the
//! coordinator wiring of MainWindow.cs:76-85). Owner: W6-D.
//!
//! One [`Updates`] per host, made the first time a seam is asked once the run engine is attached: mighty-updates'
//! `CliUpdateService` over the platform's processes, the manual `CliUpdateCoordinator` (its updater is C#'s `UpdateManualProvider`),
//! the `AutomaticUpdates` pass with its 60 s tick, and the app's one `PluginOperations`. The holds the composer, the cards and the
//! phone read come from here, each read with the state locked (never `host.view()` there).
//!
//! W6's guard (docs/tauri-conversion.md §8): an automatic update runs only with `--allow-state-write` and never in a smoke
//! ([`AutomaticUpdatePolicy`]); a manual CLI update outside a smoke also needs the flag (`Host::may_change_machine`), else each CLI
//! reads skipped with the verification-mode word. A C# app on the same PC may run the same updates.

use std::collections::HashMap;
use std::sync::{Arc, Mutex, MutexGuard};
use std::time::{Duration, Instant};

use chrono::{DateTime, Local, Utc};
use mighty_core::model::{AppSnapshot, RunSession, Workspace};
use mighty_core::wire;
use mighty_native::ui::{Env, Locals, Ui};
use mighty_platform::BoxFuture;
use mighty_run::RunEvent;
use mighty_updates::cli_update::coordinator::Updater;
use mighty_updates::cli_update::holds::{
    AutomaticUpdateHost, AutomaticUpdatePolicy, AutomaticUpdates, CheckGate, HoldPane, ProviderUse, QueuedPane, UpdateActivity,
    ready_for_automatic_update,
};
use mighty_updates::cli_update::schedule::CliAutoUpdateSchedule;
use mighty_updates::cli_update::{CliUpdateCoordinator, CliUpdateResult, CliUpdateService, strings};
use mighty_updates::plugins::{PluginAutoUpdateResult, PluginOperations};
use mighty_updates::runner::{CancellationToken, HostCliRunner};
use mighty_updates::view::{self, CliUpdateActions, CliUpdateBox};
use tauri::{AppHandle, Manager};

use crate::host::{Host, View};

/// The manual updater's stand-in in a smoke (C# `smokeCliUpdater`).
pub type SmokeUpdater = Arc<dyn Fn(String, CancellationToken) -> BoxFuture<'static, CliUpdateResult> + Send + Sync>;

#[derive(Default)]
struct State {
    /// C# `manuallyUpdatingProvider`.
    manual_provider: Option<String>,
    /// C# `providerLastActive`.
    last_active: HashMap<String, DateTime<Utc>>,
    smoke_updater: Option<SmokeUpdater>,
    /// C# `lastManualUpdateRefresh`.
    last_manual_refresh: Option<DateTime<Utc>>,
    /// When each pane's queue was last asked to drain (a notice comes many times a pass: one drain at a time per pane).
    draining: HashMap<String, Instant>,
    /// The smoke's stand-in for the drain: told the pane instead of starting its queued request.
    drain_hook: Option<DrainHook>,
}

/// The smoke's stand-in for a queue's drain.
pub type DrainHook = Arc<dyn Fn(&str) + Send + Sync>;

/// The host's updates.
pub struct Updates {
    pub service: Arc<CliUpdateService>,
    pub coordinator: Arc<CliUpdateCoordinator>,
    pub automatic: Arc<AutomaticUpdates>,
    /// The app's own operations object (C# `pluginOperations`): the shared runner every plugin read and mutation goes through and
    /// the app-wide one-at-a-time gate. Never replaced, not even by a smoke (which hands the window a fake reader instead).
    pub operations: Arc<PluginOperations>,
    state: Mutex<State>,
    /// Cancelled once the app quits (C# `automaticUpdateClosing`).
    closing: CancellationToken,
}

fn lock<T>(mutex: &Mutex<T>) -> MutexGuard<'_, T> {
    mutex.lock().unwrap_or_else(|poisoned| poisoned.into_inner())
}

/// The host as its handlers share it.
fn shared(app: &AppHandle) -> Option<Arc<Host>> {
    app.try_state::<Arc<Host>>().map(|host| host.inner().clone())
}

/// The user's profile folder, where the automatic plugin update runs and the native Claude install lives.
fn home() -> String {
    std::env::var("USERPROFILE").or_else(|_| std::env::var("HOME")).unwrap_or_default()
}

/// The host's [`Updates`], made (and its tick started) the first time once the run engine is attached; none before. Cheap and safe
/// anywhere, the state locked or in a builder: making it reads nothing (the CLI environment is read when an update is planned, on a
/// blocking thread) and starts no process. main.rs makes it as the run engine attaches.
pub fn of(host: &Host) -> Option<Arc<Updates>> {
    if let Some(updates) = host.app.try_state::<Arc<Updates>>() {
        return Some(updates.inner().clone());
    }
    let runs = host.runs()?;
    let environment = runs.catalog.environment();
    let runner = Arc::new(HostCliRunner::new(host.processes.clone()));
    let read = environment.clone();
    let service = Arc::new(CliUpdateService::with_environment_source(runner, Arc::new(move || read.current().values), home()));
    let app = host.app.clone();
    let updates = Arc::new(Updates {
        service,
        coordinator: CliUpdateCoordinator::new(manual_updater(app.clone())),
        automatic: Arc::new(AutomaticUpdates::new()),
        operations: Arc::new(PluginOperations::installed(host.processes.clone(), environment)),
        state: Mutex::new(State::default()),
        closing: CancellationToken::new(),
    });
    if host.app.manage(updates.clone()) {
        let listener = app.clone();
        updates.coordinator.on_state_changed(move || coordinator_changed(&listener));
        start_tick(app);
    }
    host.app.try_state::<Arc<Updates>>().map(|updates| updates.inner().clone())
}

/// How long quitting waits for a running update to stop.
const SHUTDOWN_WAIT: Duration = Duration::from_secs(3);

/// `updates::shutdown`: quitting (C# `ShutdownAutomaticUpdates`, the coordinator's and the service's `ShutdownAsync`): the automatic
/// pass cancelled, the manual run and the service refusing new work and cancelled, the running installer stopped with everything it
/// started — waited for at most [`SHUTDOWN_WAIT`]. Called from main.rs's `RunEvent::Exit`, outside the async runtime; nothing when
/// the updates were never made.
pub fn shutdown(host: &Host) {
    let Some(updates) = host.app.try_state::<Arc<Updates>>().map(|updates| updates.inner().clone()) else { return };
    updates.closing.cancel();
    tauri::async_runtime::block_on(async move {
        let stop = async {
            updates.coordinator.shutdown().await;
            updates.service.shutdown().await;
        };
        let _ = tokio::time::timeout(SHUTDOWN_WAIT, stop).await;
    });
}

impl Updates {
    fn state(&self) -> MutexGuard<'_, State> {
        lock(&self.state)
    }

    /// What runs on this PC now ([`UpdateActivity`]).
    pub fn activity(&self) -> UpdateActivity {
        let manual = self.state().manual_provider.clone();
        self.automatic.activity(self.coordinator.is_updating(), manual.as_deref(), self.operations.is_running())
    }

    /// The smoke's stand-in for a queue's drain (None: the composer's drain).
    pub fn set_drain_hook(&self, hook: Option<DrainHook>) {
        let mut state = self.state();
        state.drain_hook = hook;
        state.draining.clear();
    }

    /// The smoke's stand-in for the manual updater (None: the real service).
    pub fn set_smoke_updater(&self, updater: Option<SmokeUpdater>) {
        self.state().smoke_updater = updater;
    }

    /// C# `lastCliUpdateResults` as the box shows it: the manual run's while it runs and has some, else the last ones.
    pub fn shown_results(&self) -> Vec<CliUpdateResult> {
        let live = self.coordinator.results();
        if self.coordinator.is_updating() && !live.is_empty() { live } else { self.automatic.snapshot().cli_results }
    }
}

/// Whether the pane's workspace is on another computer (C# `service.IsRemoteWorkspace`).
fn remote(snapshot: &AppSnapshot, pane: &RunSession) -> bool {
    snapshot.workspace(pane.workspace_id()).is_some_and(|w| w.remote().is_some())
}

/// `updates::any_running`: C# `AnyCliUpdateRunning` — a manual or automatic CLI update is under way (C's `RequireIdleAccount`).
#[allow(dead_code, reason = "a seam W6-C (RequireIdleAccount) calls")]
pub fn any_running(host: &Host) -> bool {
    of(host).is_some_and(|updates| updates.activity().any_cli_update_running())
}

/// `updates::start_manual`: C# `UpdateCliTools` — the manual update of every CLI (the setup screen's update row, Settings' button),
/// unless a CLI update or a plugin operation runs.
pub fn start_manual(host: &Arc<Host>) {
    let Some(updates) = of(host) else { return };
    if !updates.activity().any_cli_update_running() && !updates.operations.is_running() {
        updates.coordinator.start();
    }
}

/// `updates::holds`: C# `BackgroundUpdateHolds(pane)` — an automatic update of the pane's CLI holds its sends. Called with the state
/// locked: it reads the updates' own state and `snapshot`, never `host.view()`.
pub fn holds(host: &Host, snapshot: &AppSnapshot, pane: &RunSession) -> bool {
    of(host).is_some_and(|updates| updates.activity().background_update_holds(&HoldPane::of(pane, remote(snapshot, pane))))
}

/// `updates::manual_block`: C# `ManualMutationBlockReason(pane)` — why a manual update or plugin change refuses the pane's sends.
/// Called with the state locked, as [`holds`]. (A delegated child still recording its last run's end is W7's; until then none is.)
pub fn manual_block(host: &Host, snapshot: &AppSnapshot, pane: &RunSession) -> Option<String> {
    of(host)?.activity().manual_mutation_block_reason(&HoldPane::of(pane, remote(snapshot, pane)), false)
}

/// `updates::manual_update`: C# `ManualUpdateReason(pane)` — the manual update of the pane's CLI (or a plugin change) under way.
/// Called with the state locked, as [`holds`].
pub fn manual_update(host: &Host, snapshot: &AppSnapshot, pane: &RunSession) -> Option<String> {
    of(host)?.activity().manual_update_reason(&HoldPane::of(pane, remote(snapshot, pane)))
}

/// C# `SettingsSectionsSmoke.FixtureResults`: four fixture results, not a real run.
pub fn fixture_results() -> Vec<CliUpdateResult> {
    vec![
        CliUpdateResult::new("claude", "updated")
            .with_versions(Some("2.1.270"), Some("2.1.271"))
            .with_method("native")
            .with_detail(strings::detail_updated()),
        CliUpdateResult::new("codex", "current")
            .with_versions(Some("0.51.0"), Some("0.51.0"))
            .with_method("npm")
            .with_detail(strings::detail_unchanged()),
        CliUpdateResult::new("gemini", "skipped").with_method("missing").with_detail(strings::detail_missing()),
        CliUpdateResult::new("claude", "failed")
            .with_versions(Some("2.1.270"), None)
            .with_method("native")
            .with_detail(strings::detail_failed_exit(1)),
    ]
}

/// `updates::section_fixture_statuses`: for the `settingsSections` smoke (C# `RunSettingsSectionsSmoke`), the CLI update box built
/// over the fixture results: the fixture's statuses and the statuses the built box shows, in order.
#[allow(dead_code, reason = "a seam W6-A1 (the settingsSections smoke) calls")]
pub fn section_fixture_statuses(host: &Arc<Host>) -> (Vec<String>, Vec<String>) {
    let results = fixture_results();
    let expected = results.iter().map(|r| r.status.clone()).collect();
    let env = crate::native::env(host);
    let mut locals = Locals::default();
    let model = CliUpdateBox { results, ..box_model(host) };
    let built = view::cli_update_box(&Ui::new(&env, &mut locals), &model, &CliUpdateActions::none());
    (expected, view::shown_statuses(&built))
}

/// A run event (C# `RecordProviderActivity`, the first thing the window does with one): a status or log line of an agent pane
/// marks its provider active now.
pub fn on_run_event(host: &Arc<Host>, event: &RunEvent) {
    if !matches!(event.kind.as_str(), "status" | "log") {
        return;
    }
    let provider = {
        let view = host.view();
        view.snapshot.session(&event.session_id).filter(|p| p.kind() == "claude").map(|p| p.provider().to_string())
    };
    if let (Some(provider), Some(updates)) = (provider, of(host)) {
        updates.state().last_active.insert(provider, Utc::now());
    }
}

/// C# `ReadyForAutomaticUpdate`'s inputs for `provider`: its runs here (and another computer's on this PC, W8: none yet), a sign-in,
/// sign-out or terminal wait of it (W6-C's `accounts::busy`: C# `loginBusy`, `accountChanges`, `accountLoginWaits`), its panes
/// starting or holding queued input, its last activity. Locks the state: never call it with the state locked ([`provider_use_in`]).
pub fn provider_use(host: &Host, provider: &str) -> ProviderUse {
    let view = host.view();
    provider_use_in(host, &view, provider)
}

/// [`provider_use`] read under a state guard the caller holds, so an admission decided from it holds until the guard drops (C#
/// decides on the window's one dispatcher). Order: the state, then the accounts' and the updates' own locks, as [`holds`].
pub fn provider_use_in(host: &Host, view: &View, provider: &str) -> ProviderUse {
    let active = host.runs().is_some_and(|runs| runs.service.has_active_provider(provider));
    let panes: Vec<_> = view
        .panes
        .iter()
        .filter_map(|(id, ui)| view.snapshot.session(id).filter(|p| p.kind() == "claude" && p.provider() == provider).map(|p| (p, ui)))
        .collect();
    let starting = panes.iter().any(|(_, ui)| ui.view.starting || ui.view.queue_starting);
    let queued = panes.iter().any(|(pane, ui)| pane.status() != "error" && !ui.queue.items().is_empty());
    let last_active = of(host).and_then(|updates| updates.state().last_active.get(provider).copied());
    ProviderUse { running: active || starting, account_change: crate::accounts::busy(host, provider), queued, last_active }
}

/// How soon a pane's queue is asked to drain again (the drain itself waits for the pane to be free).
const DRAIN_AGAIN: Duration = Duration::from_secs(2);

/// The panes whose queued requests drain now ([`UpdateActivity::drains`]), read under the state guard.
fn draining(view: &View, activity: &UpdateActivity) -> Vec<String> {
    let rows: Vec<(String, RunSession, bool, usize)> = view
        .panes
        .iter()
        .filter_map(|(id, ui)| {
            let pane = view.snapshot.session(id)?;
            Some((id.clone(), (**pane).clone(), ui.view.starting || ui.view.queue_starting, ui.queue.items().len()))
        })
        .collect();
    let panes: Vec<QueuedPane> = rows
        .iter()
        .map(|(id, pane, starting, queued)| QueuedPane {
            id,
            pane: HoldPane::of(pane, remote(&view.snapshot, pane)),
            status: pane.status(),
            starting: *starting,
            queued: *queued,
        })
        .collect();
    activity.drains(&panes).into_iter().map(str::to_string).collect()
}

/// C# `NotifyAutomaticUpdates`: every pane's composer looked at again — each `BackgroundUpdateChanged`: a pane with queued requests
/// that nothing holds any more drains them —, the CLI 업데이트 box drawn again (the phone's notice is W8).
pub fn notify(host: &Arc<Host>) {
    crate::panes::mark_all(host);
    if let Some(updates) = of(host)
        && !host.closing()
    {
        let ready = {
            let view = host.view();
            draining(&view, &updates.activity())
        };
        let (hook, due) = {
            let mut state = updates.state();
            let now = Instant::now();
            let due: Vec<String> =
                ready.into_iter().filter(|id| state.draining.get(id).is_none_or(|at| now.duration_since(*at) >= DRAIN_AGAIN)).collect();
            for id in &due {
                state.draining.insert(id.clone(), now);
            }
            (state.drain_hook.clone(), due)
        };
        for id in due {
            match &hook {
                Some(hook) => hook(&id),
                None => crate::composer::drain_queue(host.clone(), id),
            }
        }
    }
    if crate::native::is_open(host, crate::settings::SURFACE) {
        crate::native::touch(host, crate::settings::SURFACE);
    }
}

/// C# `RefreshRuntime` after an update: every CLI looked for again and read (outside a smoke), the panes reading the new versions.
async fn refresh_runtime(host: &Arc<Host>) {
    if host.smoke.is_some() || host.closing() {
        return;
    }
    let Some(runs) = host.runs().cloned() else { return };
    let folder = {
        let view = host.view();
        view.snapshot
            .active_workspace_id
            .as_deref()
            .and_then(|id| view.snapshot.workspace(id))
            .filter(|w| w.remote().is_none())
            .map(|w| w.path().to_string())
    };
    let info = runs.catalog.get_runtime(true, folder.as_deref()).await;
    for provider in info.providers {
        runs.keep(provider);
    }
    host.panes_changed();
}

/// C# `UpdateManualProvider`: refused while the provider is in use or a plugin operation runs; in a smoke only its stand-in runs;
/// outside one only with `--allow-state-write` (W6). The provider is held while its update runs.
fn manual_updater(app: AppHandle) -> Updater {
    Arc::new(move |provider: String, token: CancellationToken| {
        let app = app.clone();
        Box::pin(async move {
            let host = shared(&app)?;
            let updates = of(&host)?;
            if token.is_cancelled() || host.closing() {
                return None;
            }
            let stand_in = updates.state().smoke_updater.clone();
            if host.smoke.is_some() && stand_in.is_none() {
                return Some(CliUpdateResult::new(&provider, "skipped").with_detail(strings::detail_inspect_failed()));
            }
            if stand_in.is_none() && !host.may_change_machine() {
                let word = mighty_run::strings::get("windows.notifications.statusVerificationMode");
                return Some(CliUpdateResult::new(&provider, "skipped").with_detail(word));
            }
            // Admitted and held in one decision under the state's lock, so no composer start slips in between (C#: one dispatcher).
            let held = {
                let view = host.view();
                let usage = provider_use_in(&host, &view, &provider);
                if let Some(refused) = updates.activity().manual_update_refusal(&provider, &usage) {
                    return Some(refused);
                }
                updates.state().manual_provider = Some(provider.clone());
                ManualHold(updates.clone())
            };
            notify(&host);
            let result = match stand_in {
                Some(stand_in) => stand_in(provider.clone(), token.clone()).await,
                None => updates.service.update(&provider, &token).await,
            };
            drop(held);
            if !host.closing() {
                notify(&host);
            }
            Some(result)
        })
    })
}

/// The manual update's hold of its provider, released however the update ends (C#'s `finally`).
struct ManualHold(Arc<Updates>);

impl Drop for ManualHold {
    fn drop(&mut self) {
        self.0.state().manual_provider = None;
    }
}

/// The coordinator's `StateChanged` (MainWindow.cs:78-85): the box drawn again; once the run ended its results are the last ones
/// and `ManualUpdateFinished` runs.
fn coordinator_changed(app: &AppHandle) {
    let Some(host) = shared(app) else { return };
    let Some(updates) = of(&host) else { return };
    if crate::native::is_open(&host, crate::settings::SURFACE) {
        crate::native::touch(&host, crate::settings::SURFACE);
    }
    if updates.coordinator.is_updating() {
        return;
    }
    let results = updates.coordinator.results();
    if !results.is_empty() {
        updates.automatic.set_cli_results(results);
    }
    if !host.closing() {
        tauri::async_runtime::spawn(async move { manual_update_finished(&host, &updates).await });
    }
}

/// C# `ManualUpdateFinished`: once per finished run, the runtime read again (outside a smoke) and the sign-ins held for the update
/// resent.
async fn manual_update_finished(host: &Arc<Host>, updates: &Updates) {
    let Some(finished) = updates.coordinator.finished_at() else { return };
    {
        let mut state = updates.state();
        if host.closing() || state.last_manual_refresh == Some(finished) {
            return;
        }
        state.last_manual_refresh = Some(finished);
    }
    refresh_runtime(host).await;
    crate::accounts::resend_after_update(host);
}

/// The automatic pass's view of the app.
struct Pass {
    app: AppHandle,
}

impl Pass {
    fn host(&self) -> Option<Arc<Host>> {
        shared(&self.app)
    }
}

impl AutomaticUpdateHost for Pass {
    fn preferences(&self) -> (Option<bool>, Option<bool>) {
        match self.host() {
            Some(host) => {
                let view = host.view();
                (view.snapshot.auto_update_clis, view.snapshot.auto_update_plugins)
            }
            None => (None, Some(false)),
        }
    }

    fn ready(&self, provider: &str) -> bool {
        self.host().is_some_and(|host| ready_for_automatic_update(&provider_use(&host, provider), Utc::now()))
    }

    /// Decided and held under one state guard (the state, then the updates' own lock), as the manual update is.
    fn admit(&self, provider: &str, plugins: bool) -> bool {
        let Some(host) = self.host() else { return false };
        let Some(updates) = of(&host) else { return false };
        let view = host.view();
        let ready = ready_for_automatic_update(&provider_use_in(&host, &view, provider), Utc::now());
        if ready {
            updates.automatic.hold(provider, plugins);
        }
        ready
    }

    fn update_cli(&self, provider: &str, cancel: CancellationToken) -> BoxFuture<'_, CliUpdateResult> {
        let provider = provider.to_string();
        Box::pin(async move {
            match self.host().as_deref().and_then(of) {
                Some(updates) => updates.service.update(&provider, &cancel).await,
                None => CliUpdateResult::new(&provider, "cancelled").with_detail(strings::detail_closing()),
            }
        })
    }

    fn plugin_operation_running(&self) -> bool {
        self.host().as_deref().and_then(of).is_some_and(|updates| updates.operations.is_running())
    }

    fn update_plugins(
        &self,
        provider: &str,
        home: &Workspace,
        budget: Duration,
        cancel: CancellationToken,
    ) -> BoxFuture<'_, PluginAutoUpdateResult> {
        let (provider, home) = (provider.to_string(), home.clone());
        Box::pin(async move {
            match self.host().as_deref().and_then(of) {
                Some(updates) => updates.operations.update_installed(&provider, &home, budget, &cancel, None).await,
                None => PluginAutoUpdateResult::cancelled(),
            }
        })
    }

    fn changed(&self) {
        if let Some(host) = self.host() {
            notify(&host);
        }
    }

    /// C# clears the provider's slash catalogue; the host scans it on each request, so nothing is kept to clear.
    fn plugins_updated(&self, _provider: &str) {}

    fn pass_finished(&self, completed: bool) {
        let Some(host) = self.host() else { return };
        if host.closing() {
            return;
        }
        tauri::async_runtime::spawn(async move {
            if completed {
                refresh_runtime(&host).await;
            }
            crate::accounts::resend_after_update(&host);
        });
    }
}

/// C# `BeginAutomaticUpdates`: the check now and every [`CliAutoUpdateSchedule::TICK`], each pass run to its end before the next
/// check; the pass cancelled when the app quits. Nothing runs while [`AutomaticUpdatePolicy`] forbids it.
fn start_tick(app: AppHandle) {
    let watcher = app.clone();
    tauri::async_runtime::spawn(async move {
        loop {
            tokio::time::sleep(Duration::from_millis(500)).await;
            let Some(host) = shared(&watcher) else { continue };
            if host.closing() {
                if let Some(updates) = of(&host) {
                    updates.closing.cancel();
                }
                return;
            }
        }
    });
    tauri::async_runtime::spawn(async move {
        let pass = Pass { app: app.clone() };
        loop {
            if let Some(host) = shared(&app)
                && let Some(updates) = of(&host)
            {
                if host.closing() {
                    return;
                }
                let gate = CheckGate {
                    policy: AutomaticUpdatePolicy { smoke: host.smoke.is_some(), allow_state_write: host.writes() },
                    closing: host.closing(),
                    manual_running: updates.coordinator.is_updating(),
                    plugin_operation_running: updates.operations.is_running(),
                };
                let providers = updates.automatic.check(Utc::now(), gate, &pass);
                if !providers.is_empty() {
                    let home = Workspace { name: Some("Home".into()), path: Some(home()), ..Workspace::default() };
                    updates.automatic.run(&pass, &providers, &home, &updates.closing).await;
                }
            }
            tokio::time::sleep(CliAutoUpdateSchedule::TICK).await;
        }
    });
}

/// What the CLI 업데이트 box shows now (C#: the window's fields `RefreshBackgroundUpdates` reads). Locks the state.
pub fn box_model(host: &Host) -> CliUpdateBox {
    let (clis, plugins) = {
        let view = host.view();
        (view.snapshot.auto_update_clis == Some(true), view.snapshot.auto_update_plugins != Some(false))
    };
    let Some(updates) = of(host) else {
        return CliUpdateBox { auto_update_clis: clis, auto_update_plugins: plugins, ..CliUpdateBox::default() };
    };
    let activity = updates.activity();
    let automatic = updates.automatic.snapshot();
    let finished = match (automatic.finished_at, updates.coordinator.finished_at()) {
        (Some(auto), Some(manual)) => Some(auto.max(manual)),
        (auto, manual) => auto.or(manual),
    };
    let plugin_results =
        wire::PROVIDERS.iter().filter_map(|p| automatic.plugin_results.get(*p).map(|r| (p.to_string(), r.clone()))).collect();
    CliUpdateBox {
        auto_update_clis: clis,
        auto_update_plugins: plugins,
        any_running: activity.any_cli_update_running(),
        plugin_operation_running: activity.plugin_operation_running,
        automatic_provider: automatic.provider.clone(),
        automatic_plugins: automatic.plugins,
        // C# `finished.ToLocalTime().ToString("t")`: the short time; here the 24-hour clock (the OS's culture pattern is not read).
        last_run: finished.map(|at| at.with_timezone(&Local).format("%H:%M").to_string()),
        results: updates.shown_results(),
        plugin_results,
    }
}

/// What the box's controls do: the switches write the snapshot's fields (saved only with `--allow-state-write`, as every change),
/// the button starts the manual run.
pub fn box_actions(host: &Arc<Host>) -> CliUpdateActions {
    let write = |host: &Arc<Host>, change: fn(&mut AppSnapshot, bool)| -> Arc<dyn Fn(bool) + Send + Sync> {
        let host = host.clone();
        Arc::new(move |on| {
            let mut view = host.view();
            view.error = None;
            if let Err(error) = view.update(|state| change(state, on)) {
                view.error = Some(error);
            }
        })
    };
    let start = host.clone();
    CliUpdateActions {
        toggle_clis: write(host, |state, on| state.auto_update_clis = Some(on)),
        toggle_plugins: write(host, |state, on| state.auto_update_plugins = Some(on)),
        start: Arc::new(move || start_manual(&start)),
    }
}

/// The box built now (settings/cli_update.rs).
pub fn build_box(host: &Arc<Host>, ui: &Ui) -> mighty_native::ui::El {
    view::cli_update_box(ui, &box_model(host), &box_actions(host))
}

/// The box with an environment of its own, for a check that reads a built box (the smoke).
pub fn built_box(host: &Arc<Host>, model: &CliUpdateBox, actions: &CliUpdateActions) -> mighty_native::ui::El {
    let env: Env = crate::native::env(host);
    let mut locals = Locals::default();
    view::cli_update_box(&Ui::new(&env, &mut locals), model, actions)
}
