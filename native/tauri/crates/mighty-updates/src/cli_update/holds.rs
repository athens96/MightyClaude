//! What a running update holds, and the automatic update pass (C# MainWindow.AutomaticUpdates.cs, MainWindow.UpdateAdmission.cs;
//! M/AppStore.swift `backgroundUpdateHolds`, `manualUpdateHolds`, `runBlockedReason`). In C# these are the window's own fields and
//! methods; here they are pure functions of what the host knows ([`UpdateActivity`], [`ProviderUse`]) and a pass over a host trait
//! ([`AutomaticUpdateHost`]), so the composer's window rules, the cards, the phone and the settings box all read one answer.
//!
//! - An automatic update of a provider's CLI (or its plugins) holds that provider's local panes: their sends wait in the queue.
//! - A manual CLI update holds its provider's local panes; a manual plugin change holds Claude's and Codex's.
//! - A remote pane runs on its host: this PC's updates never hold it.

use std::collections::BTreeMap;
use std::sync::{Mutex, MutexGuard};
use std::time::Duration;

use chrono::{DateTime, Utc};
use mighty_core::model::{RunSession, Workspace};
use mighty_core::wire;
use mighty_platform::BoxFuture;
use mighty_run::strings::{get, get_with};

use super::schedule::CliAutoUpdateSchedule;
use super::service::CliUpdateResult;
use crate::plugins::PluginAutoUpdateResult;
use crate::runner::CancellationToken;

/// Whether the host may run automatic updates at all. They mutate the user's machine (`claude update`, `npm i -g`, plugin updates)
/// and a C# app on the same PC may be running the same ones, so until W9 they run only with `--allow-state-write`, and never in a
/// smoke (C# skips them under `--smoke-test` too).
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub struct AutomaticUpdatePolicy {
    pub smoke: bool,
    pub allow_state_write: bool,
}

impl AutomaticUpdatePolicy {
    pub fn permits(&self) -> bool {
        !self.smoke && self.allow_state_write
    }
}

/// A pane as the holds read it: an agent pane (`kind` "claude") of `provider`, in a remote workspace or not.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct HoldPane<'a> {
    pub kind: &'a str,
    pub provider: &'a str,
    pub remote: bool,
}

impl<'a> HoldPane<'a> {
    pub fn of(session: &'a RunSession, remote: bool) -> HoldPane<'a> {
        HoldPane { kind: session.kind(), provider: session.provider(), remote }
    }

    fn local_agent(&self) -> bool {
        self.kind == "claude" && !self.remote
    }
}

/// What runs on this PC now: the automatic pass's fields, the manual run (`CliUpdateCoordinator::is_updating`) and the provider it
/// is updating, and whether a plugin operation runs (`PluginOperations::is_running`).
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct UpdateActivity {
    pub automatic_running: bool,
    pub automatic_provider: Option<String>,
    pub automatic_plugins: bool,
    pub manual_running: bool,
    pub manual_provider: Option<String>,
    pub plugin_operation_running: bool,
}

impl UpdateActivity {
    /// C# `AnyCliUpdateRunning`.
    pub fn any_cli_update_running(&self) -> bool {
        self.manual_running || self.automatic_running
    }

    /// C# `BackgroundUpdateHolds`: an automatic update of the pane's CLI holds its sends; never a remote pane's, whose CLI is its
    /// host's.
    pub fn background_update_holds(&self, pane: &HoldPane) -> bool {
        pane.local_agent() && self.automatic_running && self.automatic_provider.as_deref() == Some(pane.provider)
    }

    /// The sentence a held pane shows (MainWindow.Delegation.cs:108, `settings.cliUpdate.backgroundUpdateQueued`).
    pub fn background_update_note(&self, pane: &HoldPane) -> Option<String> {
        self.background_update_holds(pane).then(|| {
            get_with("settings.cliUpdate.backgroundUpdateQueued", &[("provider", mighty_run::strings::provider_label(pane.provider))])
        })
    }

    /// C# `ManualUpdateReason`: the CLI update or plugin change the user started on this PC that holds the pane's runs. A plugin
    /// change holds Claude and Codex, unless it is the automatic pass's own plugin update.
    pub fn manual_update_reason(&self, pane: &HoldPane) -> Option<String> {
        if !pane.local_agent() {
            return None;
        }
        if self.manual_provider.as_deref() == Some(pane.provider) {
            return Some(get_with("windows.updates.manualProvider", &[("provider", mighty_run::providers::name(pane.provider))]));
        }
        (matches!(pane.provider, "claude" | "codex")
            && self.plugin_operation_running
            && !(self.automatic_running && self.automatic_plugins))
            .then(|| get("plugins.operation.busy"))
    }

    /// C# `ManualMutationBlockReason`: a child still recording its last run's end starts nothing yet, else
    /// [`Self::manual_update_reason`].
    pub fn manual_mutation_block_reason(&self, pane: &HoldPane, recording_end: bool) -> Option<String> {
        if !pane.local_agent() {
            return None;
        }
        if recording_end {
            return Some(get("delegation.child.recordingEnd"));
        }
        self.manual_update_reason(pane)
    }

    /// C# `UpdateManualProvider`'s admission: a manual update of `provider` waits for nothing; it is refused (busy) while the
    /// provider is in use, an account changes, or a plugin operation runs.
    pub fn manual_update_refusal(&self, provider: &str, usage: &ProviderUse) -> Option<CliUpdateResult> {
        (usage.account_change || self.plugin_operation_running || usage.running)
            .then(|| CliUpdateResult::new(provider, "busy").with_detail(super::strings::detail_busy()))
    }

    /// C# `PluginMutationBlockReason`: why the plugin window may not install or refresh now. `workspace_current` is whether the
    /// window's workspace is still the state's (same id, path and remote) and the app is not closing; `local` whether it has a local
    /// folder.
    pub fn plugin_mutation_block_reason(
        &self,
        provider: &str,
        workspace_current: bool,
        local: bool,
        usage: &ProviderUse,
    ) -> Option<String> {
        if !workspace_current {
            return Some(get("windows.updates.workspaceChanged"));
        }
        if !local {
            return Some(get("remote.workspace.pluginsOnHost"));
        }
        if self.any_cli_update_running() {
            return Some(get("loginRecovery.updating"));
        }
        if self.plugin_operation_running {
            return Some(get("plugins.operation.busy"));
        }
        usage.running.then(|| get_with("windows.updates.providerRunning", &[("provider", mighty_run::providers::name(provider))]))
    }
}

/// How a provider is in use on this PC (C# `ReadyForAutomaticUpdate`'s inputs).
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct ProviderUse {
    /// A run of it is active here (`HasActiveProvider`), another computer's runs use this PC's CLIs (`remoteHostRuns > 0`), or one
    /// of its panes is starting.
    pub running: bool,
    /// A sign-in, a sign-out or a sign-in waited on in a terminal (`loginBusy`, `accountChanges`, `accountLoginWaits`).
    pub account_change: bool,
    /// One of its panes holds queued input.
    pub queued: bool,
    /// Its last status or log event.
    pub last_active: Option<DateTime<Utc>>,
}

/// C# `ReadyForAutomaticUpdate`: nothing running, starting or queued, and idle for the full 3 minutes.
pub fn ready_for_automatic_update(usage: &ProviderUse, now: DateTime<Utc>) -> bool {
    CliAutoUpdateSchedule::idle_long_enough(usage.running || usage.account_change, usage.queued, usage.last_active, now)
}

/// What the automatic pass asks of the host.
pub trait AutomaticUpdateHost: Send + Sync {
    /// The saved `AutoUpdateCLIs` and `AutoUpdatePlugins` (read again before each provider: a switch turned off stops the pass).
    fn preferences(&self) -> (Option<bool>, Option<bool>);
    /// [`ready_for_automatic_update`] for the provider, now.
    fn ready(&self, provider: &str) -> bool;
    /// `CliUpdateService::update`.
    fn update_cli(&self, provider: &str, cancel: CancellationToken) -> BoxFuture<'_, CliUpdateResult>;
    /// `PluginOperations::is_running`.
    fn plugin_operation_running(&self) -> bool;
    /// `PluginOperations::update_installed` with the user's home folder.
    fn update_plugins(
        &self,
        provider: &str,
        home: &Workspace,
        budget: Duration,
        cancel: CancellationToken,
    ) -> BoxFuture<'_, PluginAutoUpdateResult>;
    /// The holds or the results changed (C# `NotifyAutomaticUpdates`: the panes' composers, the settings box, the phone).
    fn changed(&self);
    /// A provider's plugins were updated (C#: its slash catalogue is cleared).
    fn plugins_updated(&self, provider: &str);
    /// The pass ended; `completed` when it was not cancelled (C#: the runtime is read again, then the sign-ins waiting on an
    /// update are resent).
    fn pass_finished(&self, completed: bool);
}

/// The automatic pass's own fields (C# `automaticUpdateRunning`, `automaticallyUpdatingProvider`, `automaticallyUpdatingPlugins`,
/// `automaticUpdateFinishedAt`, `lastCliUpdateResults`, `pluginUpdateResults`).
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct AutomaticState {
    pub running: bool,
    pub provider: Option<String>,
    pub plugins: bool,
    pub finished_at: Option<DateTime<Utc>>,
    pub cli_results: Vec<CliUpdateResult>,
    pub plugin_results: BTreeMap<String, PluginAutoUpdateResult>,
}

/// The automatic CLI and plugin updates: the schedule, the pass, and what it is doing now.
#[derive(Default)]
pub struct AutomaticUpdates {
    schedule: Mutex<CliAutoUpdateSchedule>,
    state: Mutex<AutomaticState>,
}

/// What [`AutomaticUpdates::check`] needs to know of the rest of the app.
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub struct CheckGate {
    pub policy: AutomaticUpdatePolicy,
    pub closing: bool,
    /// The manual run (`CliUpdateCoordinator::is_updating`).
    pub manual_running: bool,
    pub plugin_operation_running: bool,
}

impl AutomaticUpdates {
    pub fn new() -> AutomaticUpdates {
        AutomaticUpdates::default()
    }

    fn schedule(&self) -> MutexGuard<'_, CliAutoUpdateSchedule> {
        self.schedule.lock().unwrap_or_else(|poisoned| poisoned.into_inner())
    }

    fn state(&self) -> MutexGuard<'_, AutomaticState> {
        self.state.lock().unwrap_or_else(|poisoned| poisoned.into_inner())
    }

    pub fn snapshot(&self) -> AutomaticState {
        self.state().clone()
    }

    /// The manual results become the last results the pass merges into (C#: the coordinator's results are `lastCliUpdateResults`).
    pub fn set_cli_results(&self, results: Vec<CliUpdateResult>) {
        self.state().cli_results = results;
    }

    /// [`UpdateActivity`] with this pass's fields and the rest the host gives.
    pub fn activity(&self, manual_running: bool, manual_provider: Option<&str>, plugin_operation_running: bool) -> UpdateActivity {
        let state = self.state();
        UpdateActivity {
            automatic_running: state.running,
            automatic_provider: state.provider.clone(),
            automatic_plugins: state.plugins,
            manual_running,
            manual_provider: manual_provider.map(str::to_string),
            plugin_operation_running,
        }
    }

    /// C# `CheckAutomaticUpdates` (each [`CliAutoUpdateSchedule::TICK`]): the providers a pass starts for now — all of them when a
    /// pass is due, else the deferred ones that are ready — or none while the policy forbids it, the app closes, any CLI update or
    /// plugin operation runs, or both switches are off.
    pub fn check(&self, now: DateTime<Utc>, gate: CheckGate, host: &dyn AutomaticUpdateHost) -> Vec<String> {
        if !gate.policy.permits() || gate.closing || gate.manual_running || gate.plugin_operation_running || self.state().running {
            return Vec::new();
        }
        let (clis, plugins) = host.preferences();
        let enabled = clis == Some(true) || plugins != Some(false);
        let mut schedule = self.schedule();
        if schedule.is_due(now, enabled) {
            schedule.pass_started(now);
            return wire::PROVIDERS.iter().map(|p| p.to_string()).collect();
        }
        schedule.due_retries(enabled, |provider| host.ready(provider))
    }

    /// C# `RunAutomaticUpdates`: each provider's CLI in turn while `AutoUpdateCLIs` stays a JSON true (one not ready is deferred and
    /// retried later), then Claude's and Codex's plugins while `AutoUpdatePlugins` is not false, within
    /// [`CliAutoUpdateSchedule::PLUGIN_BUDGET`] all together. `home` is the user's home folder.
    pub async fn run(&self, host: &dyn AutomaticUpdateHost, providers: &[String], home: &Workspace, cancel: &CancellationToken) {
        {
            let mut state = self.state();
            state.running = true;
            state.finished_at = None;
        }
        host.changed();
        let completed = self.pass(host, providers, home, cancel).await;
        {
            let mut state = self.state();
            state.running = false;
            state.provider = None;
            state.plugins = false;
            state.finished_at = Some(Utc::now());
        }
        host.changed();
        host.pass_finished(completed);
    }

    async fn pass(&self, host: &dyn AutomaticUpdateHost, providers: &[String], home: &Workspace, cancel: &CancellationToken) -> bool {
        let deferred = || get("settings.cliUpdate.detailDeferred");
        let mut results: BTreeMap<String, CliUpdateResult> =
            self.state().cli_results.iter().map(|result| (result.provider.clone(), result.clone())).collect();
        for provider in providers {
            if cancel.is_cancelled() {
                return false;
            }
            if host.preferences().0 != Some(true) {
                break;
            }
            if !host.ready(provider) {
                self.schedule().skipped_busy(provider);
                results.insert(provider.clone(), CliUpdateResult::new(provider, "skipped").with_detail(deferred()));
            } else {
                self.set_provider(Some(provider), false);
                host.changed();
                let result = host.update_cli(provider, cancel.clone()).await;
                results.insert(provider.clone(), result);
                self.schedule().updated(provider);
                self.set_provider(None, false);
                host.changed();
            }
            self.state().cli_results = wire::PROVIDERS.iter().filter_map(|p| results.get(*p).cloned()).collect();
            host.changed();
        }
        let began = std::time::Instant::now();
        for provider in providers.iter().filter(|p| matches!(p.as_str(), "claude" | "codex")) {
            if cancel.is_cancelled() {
                return false;
            }
            if host.preferences().1 == Some(false) {
                break;
            }
            let remaining = CliAutoUpdateSchedule::PLUGIN_BUDGET.saturating_sub(began.elapsed());
            if !host.ready(provider) || remaining.is_zero() {
                self.schedule().skipped_busy(provider);
                self.state().plugin_results.insert(provider.clone(), PluginAutoUpdateResult::new("skipped", deferred()));
                continue;
            }
            if host.plugin_operation_running() {
                self.state().plugin_results.insert(provider.clone(), PluginAutoUpdateResult::busy());
                continue;
            }
            self.set_provider(Some(provider), true);
            host.changed();
            let result = host.update_plugins(provider, home, remaining, cancel.clone()).await;
            self.state().plugin_results.insert(provider.clone(), result);
            // C# leaves `automaticallyUpdatingPlugins` set until the pass ends.
            self.set_provider(None, true);
            host.changed();
            host.plugins_updated(provider);
        }
        !cancel.is_cancelled()
    }

    fn set_provider(&self, provider: Option<&str>, plugins: bool) {
        let mut state = self.state();
        state.provider = provider.map(str::to_string);
        state.plugins = plugins;
    }
}
