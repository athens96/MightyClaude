//! C# `CliUpdateCoordinator`: one CLI update run at a time over every provider in order — what macOS keeps in the `AppStore`
//! (`isUpdatingCLIs`, `cliUpdateFinishedAt`, `cliUpdateResults`). The updater is injected, so checks and the smoke use a fake.

use std::sync::{Arc, Mutex, MutexGuard};

use chrono::{DateTime, Utc};
use mighty_core::model::AppSnapshot;
use mighty_core::wire;
use mighty_platform::BoxFuture;
use tokio::sync::watch;

use super::service::CliUpdateResult;
use super::strings;
use crate::runner::CancellationToken;

/// Updates one provider. None is a run that ended by an exception (C#: the updater threw, a cancel among them): the run stops there
/// without a result for that provider.
pub type Updater = Arc<dyn Fn(String, CancellationToken) -> BoxFuture<'static, Option<CliUpdateResult>> + Send + Sync>;

type Listener = Arc<dyn Fn() + Send + Sync>;

#[derive(Default)]
struct State {
    is_updating: bool,
    finished_at: Option<DateTime<Utc>>,
    results: Vec<CliUpdateResult>,
    cancel: Option<CancellationToken>,
    running: Option<watch::Receiver<bool>>,
    automatic_attempted: bool,
    closing: bool,
}

pub struct CliUpdateCoordinator {
    updater: Updater,
    state: Mutex<State>,
    listeners: Mutex<Vec<Listener>>,
}

impl CliUpdateCoordinator {
    pub fn new(updater: Updater) -> Arc<CliUpdateCoordinator> {
        Arc::new(CliUpdateCoordinator { updater, state: Mutex::new(State::default()), listeners: Mutex::new(Vec::new()) })
    }

    fn lock(&self) -> MutexGuard<'_, State> {
        self.state.lock().unwrap_or_else(|poisoned| poisoned.into_inner())
    }

    /// C#'s `StateChanged` event: called after every change, outside the coordinator's lock, on whichever task made it.
    pub fn on_state_changed(&self, listener: impl Fn() + Send + Sync + 'static) {
        self.listeners.lock().unwrap_or_else(|poisoned| poisoned.into_inner()).push(Arc::new(listener));
    }

    fn changed(&self) {
        let listeners = self.listeners.lock().unwrap_or_else(|poisoned| poisoned.into_inner()).clone();
        for listener in listeners {
            listener();
        }
    }

    pub fn is_updating(&self) -> bool {
        self.lock().is_updating
    }

    pub fn finished_at(&self) -> Option<DateTime<Utc>> {
        self.lock().finished_at
    }

    pub fn results(&self) -> Vec<CliUpdateResult> {
        self.lock().results.clone()
    }

    /// The macOS button: 업데이트 중… while a run is on, 업데이트 하기 otherwise (CLIUpdateSettingsView.swift:26).
    pub fn button_label(&self) -> String {
        if self.is_updating() { strings::updating_button() } else { strings::update_button() }
    }

    /// The button is disabled while a run is on, so a second start is refused before it is even attempted.
    pub fn can_start(&self) -> bool {
        !self.is_updating()
    }

    /// Starts one run over every provider in order, on a task of its own (call it inside a Tokio runtime). False when already
    /// running or closing.
    pub fn start(self: &Arc<Self>) -> bool {
        let (token, done) = {
            let mut state = self.lock();
            if state.closing || state.is_updating {
                return false;
            }
            let token = CancellationToken::new();
            let (done, receiver) = watch::channel(false);
            state.cancel = Some(token.clone());
            state.running = Some(receiver);
            state.is_updating = true;
            state.finished_at = None;
            state.results = Vec::new();
            (token, done)
        };
        self.changed();
        let coordinator = self.clone();
        tokio::spawn(async move {
            coordinator.run(token).await;
            done.send_replace(true);
        });
        true
    }

    /// Starts one automatic run when `AutoUpdateCLIs` is a JSON true and this is the first call. The host never calls it in a smoke,
    /// nor (until W9) without `--allow-state-write` ([`super::holds::AutomaticUpdatePolicy`]).
    pub fn begin_automatic_if_needed(self: &Arc<Self>, snapshot: &AppSnapshot) {
        {
            let mut state = self.lock();
            if state.automatic_attempted || state.closing || snapshot.auto_update_clis != Some(true) {
                return;
            }
            state.automatic_attempted = true;
        }
        self.start();
    }

    /// Cancels the running update and waits for it to stop cleanly.
    pub async fn cancel(&self) {
        let running = {
            let state = self.lock();
            if let Some(token) = &state.cancel {
                token.cancel();
            }
            state.running.clone()
        };
        if let Some(mut running) = running {
            let _ = running.wait_for(|done| *done).await;
        }
    }

    /// Refuses all new work and cancels any current run.
    pub async fn shutdown(&self) {
        self.lock().closing = true;
        self.cancel().await;
    }

    async fn run(&self, token: CancellationToken) {
        let mut all = Vec::new();
        for provider in wire::PROVIDERS {
            if token.is_cancelled() {
                break;
            }
            let Some(result) = (self.updater)(provider.to_string(), token.clone()).await else { break };
            all.push(result);
            self.lock().results = all.clone();
            self.changed();
        }
        {
            let mut state = self.lock();
            state.is_updating = false;
            state.finished_at = Some(Utc::now());
            state.cancel = None;
        }
        self.changed();
    }
}
