//! The update holds and the automatic pass (C# MainWindow.AutomaticUpdates.cs, MainWindow.UpdateAdmission.cs), which C# checks only
//! in its `automaticUpdates` smoke (MainWindow.AutomaticUpdatesSmoke.cs). These are that smoke's Core assertions, Rust only, over a
//! fake host: an update holds only its own provider's local panes (`sameProviderLocalAndMobileHeld`, the local half; the phone half
//! is W8), queued input neither starts under a hold nor admits another idle update, a manual update refuses and blocks only its
//! provider (`manualUpdateRefusesWithoutConsumingDrafts`), a manual plugin change blocks Claude and Codex
//! (`manualPluginMutationBlocksBothProviders`), and the pass's order, deferrals, switches and cancel. Keeping drafts and queues is
//! the composer's (phase 2 smoke). Also the W6 guard: no automatic update without `--allow-state-write`, and none in a smoke.

mod common;

use std::sync::{Arc, Mutex};
use std::time::Duration;

use chrono::{TimeDelta, Utc};
use common::*;
use mighty_core::model::Workspace;
use mighty_core::wire::PROVIDERS;
use mighty_platform::BoxFuture;
use mighty_updates::cli_update::holds::{
    AutomaticUpdateHost, AutomaticUpdatePolicy, AutomaticUpdates, CheckGate, HoldPane, ProviderUse, QueuedPane, UpdateActivity,
    ready_for_automatic_update,
};
use mighty_updates::cli_update::{CliUpdateResult, strings};
use mighty_updates::plugins::PluginAutoUpdateResult;
use mighty_updates::runner::CancellationToken;

fn pane<'a>(provider: &'a str) -> HoldPane<'a> {
    HoldPane { kind: "claude", provider, remote: false }
}

fn get(key: &str) -> String {
    mighty_run::strings::get(key)
}

const ALLOWED: AutomaticUpdatePolicy = AutomaticUpdatePolicy { smoke: false, allow_state_write: true };

#[test]
fn every_automatic_update_stays_off_without_allow_state_write_and_in_a_smoke() {
    korean();
    assert!(!AutomaticUpdatePolicy::default().permits(), "a plain run updates nothing by itself");
    assert!(!AutomaticUpdatePolicy { smoke: true, allow_state_write: true }.permits(), "a smoke never updates");
    assert!(ALLOWED.permits(), "--allow-state-write lets the automatic updates run");

    let updates = AutomaticUpdates::new();
    let host = FakeHost::new((Some(true), None));
    let now = Utc::now();
    assert!(updates.check(now, CheckGate::default(), &host).is_empty(), "a due pass does not start without the flag");
    assert!(
        updates
            .check(now, CheckGate { policy: AutomaticUpdatePolicy { smoke: true, allow_state_write: true }, ..CheckGate::default() }, &host)
            .is_empty()
    );
    for blocked in [
        CheckGate { policy: ALLOWED, closing: true, ..CheckGate::default() },
        CheckGate { policy: ALLOWED, manual_running: true, ..CheckGate::default() },
        CheckGate { policy: ALLOWED, plugin_operation_running: true, ..CheckGate::default() },
    ] {
        assert!(
            updates.check(now, blocked, &host).is_empty(),
            "no pass beside a closing app, a manual update or a plugin operation: {blocked:?}"
        );
    }
    let due = updates.check(now, CheckGate { policy: ALLOWED, ..CheckGate::default() }, &host);
    assert_eq!(due, PROVIDERS, "a due pass covers every provider");
    assert!(
        updates.check(now + TimeDelta::minutes(1), CheckGate { policy: ALLOWED, ..CheckGate::default() }, &host).is_empty(),
        "not each tick"
    );

    let off = FakeHost::new((Some(false), Some(false)));
    assert!(
        AutomaticUpdates::new().check(now, CheckGate { policy: ALLOWED, ..CheckGate::default() }, &off).is_empty(),
        "both switches off: nothing"
    );
    let plugins_only = FakeHost::new((None, None));
    assert_eq!(
        AutomaticUpdates::new().check(now, CheckGate { policy: ALLOWED, ..CheckGate::default() }, &plugins_only),
        PROVIDERS,
        "plugin updates default on, so a pass is due with the CLI switch unset"
    );
}

/// `sameProviderLocalAndMobileHeld` (the local half): an automatic update holds only its own provider's local agent panes.
#[test]
fn an_update_holds_only_its_own_provider() {
    korean();
    let activity = UpdateActivity { automatic_running: true, automatic_provider: Some("claude".into()), ..UpdateActivity::default() };
    assert!(activity.background_update_holds(&pane("claude")), "an update holds its own provider");
    assert!(!activity.background_update_holds(&pane("codex")), "an update must hold only its own provider");
    assert!(!activity.background_update_holds(&HoldPane { remote: true, ..pane("claude") }), "a remote pane's CLI is its host's");
    assert!(!activity.background_update_holds(&HoldPane { kind: "shell", ..pane("claude") }), "a shell pane runs no CLI");
    assert_eq!(
        activity.background_update_note(&pane("claude")),
        Some(mighty_run::strings::get_with("settings.cliUpdate.backgroundUpdateQueued", &[("provider", "Claude")])),
        "the held pane says why its send waits"
    );
    let released = UpdateActivity { automatic_running: false, ..activity };
    assert!(!released.background_update_holds(&pane("claude")), "a finished update holds nothing");
}

fn queued_pane<'a>(id: &'a str, provider: &'a str, status: &'a str, starting: bool, queued: usize) -> QueuedPane<'a> {
    QueuedPane { id, pane: pane(provider), status, starting, queued }
}

/// `releasedQueuePreservesOrder`'s Core (C# `BackgroundUpdateChanged`, `StartNextQueuedInput`): queued requests do not drain while an
/// update holds their pane, a manual update or a plugin change blocks it; released, every free pane with a queue drains, whatever
/// started the hold; and queued input admits no automatic update of its provider (an idle provider is ready after three quiet
/// minutes).
#[test]
fn queued_requests_neither_start_under_a_hold_nor_admit_another_idle_update() {
    korean();
    let panes = [
        queued_pane("claude-1", "claude", "completed", false, 2),
        queued_pane("codex-1", "codex", "idle", false, 1),
        queued_pane("claude-empty", "claude", "idle", false, 0),
        queued_pane("claude-running", "claude", "running", false, 1),
        queued_pane("claude-error", "claude", "error", false, 1),
        queued_pane("claude-starting", "claude", "idle", true, 1),
    ];
    let automatic = UpdateActivity { automatic_running: true, automatic_provider: Some("claude".into()), ..UpdateActivity::default() };
    assert_eq!(automatic.drains(&panes), ["codex-1"], "Claude's queue waits for Claude's update; Codex's does not");
    let manual = UpdateActivity { manual_running: true, manual_provider: Some("claude".into()), ..UpdateActivity::default() };
    assert_eq!(manual.drains(&panes), ["codex-1"], "a manual update holds the queue too");
    let plugins = UpdateActivity { plugin_operation_running: true, ..UpdateActivity::default() };
    assert!(plugins.drains(&panes).is_empty(), "a plugin change holds Claude's and Codex's queues");
    let released = UpdateActivity::default();
    assert_eq!(released.drains(&panes), ["claude-1", "codex-1"], "released: every free pane with a queue drains, nothing else");
    let remote = [QueuedPane { pane: HoldPane { remote: true, ..pane("claude") }, ..panes[0] }];
    assert_eq!(automatic.drains(&remote), ["claude-1"], "a remote pane's CLI is its host's: this PC's update holds nothing there");

    let now = Utc::now();
    let idle = ProviderUse { last_active: Some(now - TimeDelta::minutes(3)), ..ProviderUse::default() };
    assert!(ready_for_automatic_update(&idle, now), "three idle minutes admit an update");
    assert!(!ready_for_automatic_update(&ProviderUse { queued: true, ..idle.clone() }, now), "queued input admits no update");
    assert!(!ready_for_automatic_update(&ProviderUse { running: true, ..idle.clone() }, now), "a running or starting pane admits none");
    assert!(!ready_for_automatic_update(&ProviderUse { account_change: true, ..idle.clone() }, now), "nor a sign-in in progress");
    assert!(
        !ready_for_automatic_update(&ProviderUse { last_active: Some(now - TimeDelta::seconds(179)), ..idle }, now),
        "nor a recent run"
    );
    assert!(ready_for_automatic_update(&ProviderUse::default(), now), "a provider never used is idle");
}

/// `manualUpdateRefusesWithoutConsumingDrafts`'s Core: a manual CLI update blocks only its provider's local panes, and is itself
/// refused (busy) while that provider is in use.
#[test]
fn a_manual_update_refuses_and_blocks_only_its_provider() {
    korean();
    let activity = UpdateActivity { manual_running: true, manual_provider: Some("claude".into()), ..UpdateActivity::default() };
    let reason = mighty_run::strings::get_with("windows.updates.manualProvider", &[("provider", mighty_run::providers::name("claude"))]);
    assert_eq!(
        activity.manual_mutation_block_reason(&pane("claude"), false),
        Some(reason.clone()),
        "the manual update blocks its provider"
    );
    assert_eq!(activity.manual_mutation_block_reason(&pane("codex"), false), None, "and only its provider");
    assert_eq!(activity.manual_mutation_block_reason(&HoldPane { remote: true, ..pane("claude") }, false), None, "never a remote pane");
    assert_eq!(activity.manual_update_reason(&pane("claude")), Some(reason), "바로 실행 reads it as held for an update");
    assert_eq!(
        UpdateActivity::default().manual_mutation_block_reason(&pane("codex"), true),
        Some(get("delegation.child.recordingEnd")),
        "a child still recording its last run's end starts nothing yet"
    );
    assert!(activity.any_cli_update_running(), "a manual run is a CLI update");

    let idle = UpdateActivity::default();
    assert_eq!(idle.manual_update_refusal("claude", &ProviderUse::default()), None, "an idle provider may be updated");
    for busy in [ProviderUse { running: true, ..ProviderUse::default() }, ProviderUse { account_change: true, ..ProviderUse::default() }] {
        let refused = idle.manual_update_refusal("claude", &busy).expect("refused");
        assert!(refused.status == "busy" && refused.detail == strings::detail_busy(), "a provider in use is refused: {busy:?}");
    }
    let plugins = UpdateActivity { plugin_operation_running: true, ..UpdateActivity::default() };
    assert!(plugins.manual_update_refusal("gemini", &ProviderUse::default()).is_some(), "no CLI update beside a plugin operation");
}

/// `manualPluginMutationBlocksBothProviders`: a manual plugin change blocks Claude and Codex only — but never the automatic pass's
/// own plugin update.
#[test]
fn a_manual_plugin_mutation_blocks_both_providers() {
    korean();
    let activity = UpdateActivity { plugin_operation_running: true, ..UpdateActivity::default() };
    let busy = Some(get("plugins.operation.busy"));
    assert_eq!(activity.manual_mutation_block_reason(&pane("claude"), false), busy, "Claude is held");
    assert_eq!(activity.manual_mutation_block_reason(&pane("codex"), false), busy, "Codex is held");
    assert_eq!(activity.manual_mutation_block_reason(&pane("gemini"), false), None, "Antigravity has no plugins to wait for");
    let automatic = UpdateActivity { automatic_running: true, automatic_plugins: true, ..activity };
    assert_eq!(automatic.manual_update_reason(&pane("claude")), None, "the automatic pass's own plugin update holds through its own hold");
}

#[test]
fn the_plugin_window_says_why_it_may_not_change_anything() {
    korean();
    let idle = UpdateActivity::default();
    let quiet = ProviderUse::default();
    assert_eq!(idle.plugin_mutation_block_reason("claude", true, true, &quiet), None, "nothing in the way");
    assert_eq!(idle.plugin_mutation_block_reason("claude", false, true, &quiet), Some(get("windows.updates.workspaceChanged")));
    assert_eq!(idle.plugin_mutation_block_reason("claude", true, false, &quiet), Some(get("remote.workspace.pluginsOnHost")));
    let updating = UpdateActivity { automatic_running: true, ..UpdateActivity::default() };
    assert_eq!(updating.plugin_mutation_block_reason("claude", true, true, &quiet), Some(get("loginRecovery.updating")));
    let operating = UpdateActivity { plugin_operation_running: true, ..UpdateActivity::default() };
    assert_eq!(operating.plugin_mutation_block_reason("codex", true, true, &quiet), Some(get("plugins.operation.busy")));
    assert_eq!(
        idle.plugin_mutation_block_reason("codex", true, true, &ProviderUse { running: true, ..quiet }),
        Some(mighty_run::strings::get_with("windows.updates.providerRunning", &[("provider", mighty_run::providers::name("codex"))]))
    );
}

/// What the pass asked of the host, and what the host answers.
struct FakeHost {
    preferences: Mutex<(Option<bool>, Option<bool>)>,
    not_ready: Mutex<Vec<String>>,
    plugin_running: Mutex<bool>,
    log: Mutex<Vec<String>>,
    changes: Mutex<usize>,
    finished: Mutex<Option<bool>>,
    /// Turns the CLI switch off after this provider's update.
    switch_off_after: Mutex<Option<String>>,
    /// Holds a CLI update until the token is cancelled.
    hang: Mutex<bool>,
    updates: Arc<AutomaticUpdates>,
    seen_holds: Mutex<Vec<(String, Option<String>, bool)>>,
}

impl FakeHost {
    fn new(preferences: (Option<bool>, Option<bool>)) -> FakeHost {
        Self::with(preferences, Arc::new(AutomaticUpdates::new()))
    }

    fn with(preferences: (Option<bool>, Option<bool>), updates: Arc<AutomaticUpdates>) -> FakeHost {
        FakeHost {
            preferences: Mutex::new(preferences),
            not_ready: Mutex::new(Vec::new()),
            plugin_running: Mutex::new(false),
            log: Mutex::new(Vec::new()),
            changes: Mutex::new(0),
            finished: Mutex::new(None),
            switch_off_after: Mutex::new(None),
            hang: Mutex::new(false),
            updates,
            seen_holds: Mutex::new(Vec::new()),
        }
    }

    fn log(&self) -> Vec<String> {
        self.log.lock().unwrap().clone()
    }
}

impl AutomaticUpdateHost for FakeHost {
    fn preferences(&self) -> (Option<bool>, Option<bool>) {
        *self.preferences.lock().unwrap()
    }
    fn ready(&self, provider: &str) -> bool {
        !self.not_ready.lock().unwrap().iter().any(|p| p == provider)
    }
    fn update_cli(&self, provider: &str, cancel: CancellationToken) -> BoxFuture<'_, CliUpdateResult> {
        let provider = provider.to_string();
        Box::pin(async move {
            let state = self.updates.snapshot();
            self.seen_holds.lock().unwrap().push((format!("cli:{provider}"), state.provider.clone(), state.plugins));
            self.log.lock().unwrap().push(format!("cli:{provider}"));
            if *self.hang.lock().unwrap() {
                cancel.cancelled().await;
                return CliUpdateResult::new(&provider, "cancelled").with_detail(strings::detail_cancelled());
            }
            if self.switch_off_after.lock().unwrap().as_deref() == Some(provider.as_str()) {
                self.preferences.lock().unwrap().0 = Some(false);
            }
            CliUpdateResult::new(&provider, "updated")
        })
    }
    fn plugin_operation_running(&self) -> bool {
        *self.plugin_running.lock().unwrap()
    }
    fn update_plugins(
        &self,
        provider: &str,
        home: &Workspace,
        budget: Duration,
        _cancel: CancellationToken,
    ) -> BoxFuture<'_, PluginAutoUpdateResult> {
        let (provider, home) = (provider.to_string(), home.path().to_string());
        Box::pin(async move {
            let state = self.updates.snapshot();
            self.seen_holds.lock().unwrap().push((format!("plugins:{provider}"), state.provider.clone(), state.plugins));
            assert!(budget > Duration::ZERO && budget <= Duration::from_secs(300), "the plugins get what is left of the 5-minute budget");
            self.log.lock().unwrap().push(format!("plugins:{provider}:{home}"));
            PluginAutoUpdateResult::new("succeeded", "")
        })
    }
    fn changed(&self) {
        *self.changes.lock().unwrap() += 1;
    }
    fn plugins_updated(&self, provider: &str) {
        self.log.lock().unwrap().push(format!("catalog:{provider}"));
    }
    fn pass_finished(&self, completed: bool) {
        *self.finished.lock().unwrap() = Some(completed);
    }
}

fn all() -> Vec<String> {
    PROVIDERS.iter().map(|p| p.to_string()).collect()
}

#[tokio::test]
async fn the_automatic_pass_updates_in_order_holds_each_provider_and_defers_the_busy() {
    korean();
    let updates = Arc::new(AutomaticUpdates::new());
    let host = FakeHost::with((Some(true), None), updates.clone());
    host.not_ready.lock().unwrap().push("codex".into());
    let home = workspace("/home/someone");
    let providers = updates.check(Utc::now(), CheckGate { policy: ALLOWED, ..CheckGate::default() }, &host);
    assert_eq!(providers, all(), "the first tick starts a pass over every provider");
    updates.run(&host, &providers, &home, &CancellationToken::new()).await;

    let expected: Vec<String> =
        ["cli:claude", "cli:gemini", "cli:pi", "cli:opencode", "cli:hermes", "plugins:claude:/home/someone", "catalog:claude"]
            .iter()
            .map(|s| s.to_string())
            .collect();
    assert_eq!(host.log(), expected, "every ready CLI in order, then Claude's plugins; busy Codex is left alone");
    for (call, provider, plugins) in host.seen_holds.lock().unwrap().iter() {
        let (kind, name) = call.split_once(':').unwrap();
        assert_eq!(provider.as_deref(), Some(name.split(':').next().unwrap()), "{call} holds its own provider while it runs");
        assert_eq!(*plugins, kind == "plugins", "{call} says whether it updates plugins");
    }
    let state = updates.snapshot();
    assert!(!state.running && state.provider.is_none() && state.finished_at.is_some(), "the pass ended and records when");
    assert_eq!(state.cli_results.iter().map(|r| r.provider.as_str()).collect::<Vec<_>>(), PROVIDERS, "a result per provider, in order");
    let codex = state.cli_results.iter().find(|r| r.provider == "codex").unwrap();
    assert!(
        codex.status == "skipped" && codex.detail == get("settings.cliUpdate.detailDeferred"),
        "busy Codex is deferred with its sentence"
    );
    assert_eq!(state.plugin_results["codex"].status, "skipped", "and its plugins wait too");
    assert_eq!(state.plugin_results["claude"].status, "succeeded", "Claude's plugins were updated");
    assert_eq!(*host.finished.lock().unwrap(), Some(true), "the host reads the runtime again and resends waiting sign-ins");
    assert!(*host.changes.lock().unwrap() >= 4, "the holds were announced as they changed");

    // The deferred provider is retried once it is ready, without waiting for the next six hours.
    host.not_ready.lock().unwrap().clear();
    let retry = updates.check(Utc::now(), CheckGate { policy: ALLOWED, ..CheckGate::default() }, &host);
    assert_eq!(retry, ["codex"], "only the deferred provider retries");
    assert!(updates.check(Utc::now(), CheckGate { policy: ALLOWED, ..CheckGate::default() }, &host).is_empty(), "once");
}

#[tokio::test]
async fn the_pass_stops_when_a_switch_turns_off() {
    korean();
    let updates = Arc::new(AutomaticUpdates::new());
    let host = FakeHost::with((Some(true), Some(false)), updates.clone());
    *host.switch_off_after.lock().unwrap() = Some("codex".into());
    updates.run(&host, &all(), &workspace("/home/someone"), &CancellationToken::new()).await;
    assert_eq!(host.log(), ["cli:claude", "cli:codex"], "the CLI switch turned off stops the CLIs; plugins off updates no plugin");

    let plugins_only = Arc::new(AutomaticUpdates::new());
    let host = FakeHost::with((None, None), plugins_only.clone());
    *host.plugin_running.lock().unwrap() = true;
    plugins_only.run(&host, &all(), &workspace("/home/someone"), &CancellationToken::new()).await;
    assert!(host.log().is_empty(), "the CLI switch unset updates no CLI, and a running plugin operation is not joined");
    let state = plugins_only.snapshot();
    assert!(state.plugin_results["claude"].status == "busy" && state.plugin_results["codex"].status == "busy", "they read busy");
}

#[tokio::test(flavor = "multi_thread")]
async fn a_cancelled_pass_ends_at_once_and_holds_nothing() {
    korean();
    let updates = Arc::new(AutomaticUpdates::new());
    let host = Arc::new(FakeHost::with((Some(true), None), updates.clone()));
    *host.hang.lock().unwrap() = true;
    let cancel = CancellationToken::new();
    let running = {
        let (updates, host, cancel) = (updates.clone(), host.clone(), cancel.clone());
        tokio::spawn(async move { updates.run(host.as_ref(), &all(), &workspace("/home/someone"), &cancel).await })
    };
    until(|| updates.snapshot().provider.as_deref() == Some("claude"), 5000, "Claude's update runs").await;
    let activity = updates.activity(false, None, false);
    assert!(activity.background_update_holds(&pane("claude")) && !activity.background_update_holds(&pane("codex")), "it holds Claude only");
    cancel.cancel();
    running.await.unwrap();
    assert_eq!(host.log(), ["cli:claude"], "nothing after the cancel");
    let state = updates.snapshot();
    assert!(!state.running && state.provider.is_none(), "a cancelled pass holds nothing");
    assert!(!updates.activity(false, None, false).background_update_holds(&pane("claude")), "the hold is released");
    assert_eq!(*host.finished.lock().unwrap(), Some(false), "the host knows it did not complete");
}

/// C#'s `finally`: a pass whose task is dropped half-way (the app quitting, a panic) holds nothing afterwards and tells the host.
#[tokio::test(flavor = "multi_thread")]
async fn a_dropped_pass_holds_nothing() {
    korean();
    let updates = Arc::new(AutomaticUpdates::new());
    let host = Arc::new(FakeHost::with((Some(true), None), updates.clone()));
    *host.hang.lock().unwrap() = true;
    let running = {
        let (updates, host) = (updates.clone(), host.clone());
        tokio::spawn(async move { updates.run(host.as_ref(), &all(), &workspace("/home/someone"), &CancellationToken::new()).await })
    };
    until(|| updates.snapshot().provider.as_deref() == Some("claude"), 5000, "Claude's update runs").await;
    let changes = *host.changes.lock().unwrap();
    running.abort();
    let _ = running.await;
    let state = updates.snapshot();
    assert!(!state.running && state.provider.is_none() && state.finished_at.is_some(), "the dropped pass is at rest: {state:?}");
    assert!(!updates.activity(false, None, false).background_update_holds(&pane("claude")), "and holds no pane");
    assert!(*host.changes.lock().unwrap() > changes, "the host was told the holds changed");
    assert_eq!(*host.finished.lock().unwrap(), None, "a dropped pass did not finish");
}
