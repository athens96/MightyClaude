//! The per-pane status line loop (C# `IStatusLineClock`, `StatusLineRefresher`, StatusLineRefresher.cs; the Mac's
//! AppStore+StatusLine.swift `refreshStatusLine(for:force:)`): debounce, a generation counter, a re-run when a refresh was asked for
//! while one ran, nothing after the pane closed, and never two commands at once for one pane. A workspace command never runs before
//! the user allows it; the user-level command runs meanwhile.
//!
//! Runs are tasks on the tokio runtime the refresher was made on; the runner gets a [`Cancel`] that fires when the pane closes, and
//! [`StatusLineRefresher::when_stopped`] ends only once the command running then has let its child go.

use std::sync::{Arc, Mutex, MutexGuard};
use std::time::{Duration, Instant};

use mighty_core::model::StringMap;
use mighty_platform::BoxFuture;
use tokio::sync::watch;

use crate::status_line::config::{StatusLineConfig, StatusLineDiscovery};
use crate::status_line::payload::StatusLineContext;
use crate::status_line::run::{Cancel, Cancelled, StatusLineResult};
use crate::status_line::trust;

/// The least interval between runs (the Mac's `statusLineMinimumInterval`).
pub const MINIMUM_INTERVAL: Duration = Duration::from_secs(2);

/// How often a pane asks again by itself, so elapsed time and repository figures stay fresh: every 10 s while its run goes on and
/// every 60 s at rest.
pub fn tick_interval(running: bool) -> Duration {
    Duration::from_secs(if running { 10 } else { 60 })
}

/// A swappable clock, so the checks move time without waiting (C# `IStatusLineClock`).
pub trait StatusLineClock: Send + Sync {
    fn now(&self) -> Instant;
    fn delay(&self, delay: Duration) -> BoxFuture<'static, ()>;
}

/// The real clock.
pub struct SystemClock;

impl StatusLineClock for SystemClock {
    fn now(&self) -> Instant {
        Instant::now()
    }
    fn delay(&self, delay: Duration) -> BoxFuture<'static, ()> {
        Box::pin(tokio::time::sleep(delay))
    }
}

/// The two levels of settings, read fresh for each run.
pub type Discover = Arc<dyn Fn() -> StatusLineDiscovery + Send + Sync>;
/// The state's `trustedStatusLines` as it stands now.
pub type Trusted = Arc<dyn Fn() -> Option<StringMap> + Send + Sync>;
/// Runs a command (normally [`crate::status_line::run::run`] over the platform's `ProcessHost`).
pub type Runner =
    Arc<dyn Fn(StatusLineConfig, StatusLineContext, Cancel) -> BoxFuture<'static, Result<StatusLineResult, Cancelled>> + Send + Sync>;

#[derive(Default)]
struct State {
    config: Option<StatusLineConfig>,
    untrusted: Option<StatusLineConfig>,
    result: Option<StatusLineResult>,
    updated_at: Option<Instant>,
    running: bool,
    pending: bool,
    generation: u64,
    closed: bool,
    pending_context: Option<StatusLineContext>,
}

/// C# `StatusLineRefresher`.
pub struct StatusLineRefresher {
    discover: Discover,
    trusted: Trusted,
    workspace_id: String,
    runner: Runner,
    clock: Arc<dyn StatusLineClock>,
    runtime: tokio::runtime::Handle,
    state: Mutex<State>,
    shutdown: watch::Sender<bool>,
    stopped: watch::Sender<bool>,
    changed: Mutex<Option<Arc<dyn Fn() + Send + Sync>>>,
}

impl StatusLineRefresher {
    /// A refresher whose runs are tasks on `runtime`.
    pub fn new(
        discover: Discover,
        trusted: Trusted,
        workspace_id: &str,
        runner: Runner,
        clock: Option<Arc<dyn StatusLineClock>>,
        runtime: tokio::runtime::Handle,
    ) -> Arc<StatusLineRefresher> {
        Arc::new(StatusLineRefresher {
            discover,
            trusted,
            workspace_id: workspace_id.to_string(),
            runner,
            clock: clock.unwrap_or_else(|| Arc::new(SystemClock)),
            runtime,
            state: Mutex::new(State::default()),
            shutdown: watch::channel(false).0,
            stopped: watch::channel(false).0,
            changed: Mutex::new(None),
        })
    }

    fn state(&self) -> MutexGuard<'_, State> {
        self.state.lock().unwrap_or_else(|poisoned| poisoned.into_inner())
    }

    /// The command that was resolved and run (None when gated or without a configuration).
    pub fn config(&self) -> Option<StatusLineConfig> {
        self.state().config.clone()
    }

    /// A workspace command found but not yet allowed by the user.
    pub fn untrusted(&self) -> Option<StatusLineConfig> {
        self.state().untrusted.clone()
    }

    /// The last output from the command.
    pub fn result(&self) -> Option<StatusLineResult> {
        self.state().result.clone()
    }

    /// Called when the configuration, the trust question or the result changes (C#'s `StateChanged`), off the caller's thread.
    pub fn on_change(&self, handler: Arc<dyn Fn() + Send + Sync>) {
        *self.changed.lock().unwrap_or_else(|poisoned| poisoned.into_inner()) = Some(handler);
    }

    fn notify(&self) {
        let handler = self.changed.lock().unwrap_or_else(|poisoned| poisoned.into_inner()).clone();
        if let Some(handler) = handler {
            handler();
        }
    }

    /// Asks for a refresh. While a run goes on the request is kept and served when it ends; a request soon after the last run waits
    /// out the least interval (unless `force`); nothing after the pane closed.
    pub fn request_refresh(self: &Arc<Self>, context: StatusLineContext, force: bool) {
        let mut start = None;
        let mut debounce = None;
        {
            let mut state = self.state();
            if state.closed {
                return;
            }
            if state.running {
                state.pending = true;
                state.pending_context = Some(context);
                return;
            }
            if !force && let Some(at) = state.updated_at {
                let elapsed = self.clock.now().saturating_duration_since(at);
                if elapsed < MINIMUM_INTERVAL {
                    if state.pending {
                        // A debounce is already waiting.
                        return;
                    }
                    state.pending = true;
                    state.pending_context = Some(context.clone());
                    debounce = Some(MINIMUM_INTERVAL - elapsed);
                }
            }
            if debounce.is_none() {
                state.running = true;
                state.pending = false;
                state.pending_context = None;
                state.generation += 1;
                start = Some(state.generation);
            }
        }
        if let Some(delay) = debounce {
            let this = self.clone();
            self.runtime.spawn(async move { this.debounce(context, delay).await });
        } else if let Some(generation) = start {
            let this = self.clone();
            self.runtime.spawn(async move { this.run(context, generation).await });
        }
    }

    /// Clears the trust question without running anything.
    pub fn dismiss(&self) {
        self.state().untrusted = None;
        self.notify();
    }

    /// Marks the pane closed: the run in flight is discarded and later requests are ignored.
    pub fn close(&self) {
        {
            let mut state = self.state();
            if state.closed {
                return;
            }
            state.closed = true;
            state.generation += 1;
            if !state.running {
                self.stopped.send_replace(true);
            }
        }
        self.shutdown.send_replace(true);
    }

    /// Ends after [`close`](Self::close), once the command running then has released its child.
    pub async fn when_stopped(&self) {
        let mut stopped = self.stopped.subscribe();
        let _ = stopped.wait_for(|done| *done).await;
    }

    pub fn is_stopped(&self) -> bool {
        *self.stopped.borrow()
    }

    fn cancel(&self) -> Cancel {
        Cancel::new(self.shutdown.subscribe())
    }

    async fn debounce(self: Arc<Self>, context: StatusLineContext, delay: Duration) {
        let mut cancel = self.cancel();
        tokio::select! {
            _ = self.clock.delay(delay) => {}
            _ = cancel.cancelled() => return,
        }
        let context = {
            let mut state = self.state();
            if state.closed || !state.pending {
                return;
            }
            state.pending = false;
            state.pending_context.take().unwrap_or(context)
        };
        self.request_refresh(context, true);
    }

    async fn run(self: Arc<Self>, context: StatusLineContext, generation: u64) {
        let cancel = self.cancel();
        let outcome = async {
            if cancel.is_cancelled() {
                return Err(Cancelled);
            }
            let discovery = (self.discover)();
            let trusted = (self.trusted)();
            let (config, untrusted) = trust::resolve_trusted(&discovery, trusted.as_ref(), &self.workspace_id);
            let mut result = None;
            if let Some(config) = &config {
                let user = discovery.user.as_ref();
                let ctx = StatusLineContext {
                    output_style: config.output_style.clone().or_else(|| user.and_then(|u| u.output_style.clone())),
                    thinking_enabled: config.thinking_enabled.or_else(|| user.and_then(|u| u.thinking_enabled)),
                    ..context.clone()
                };
                if cancel.is_cancelled() {
                    return Err(Cancelled);
                }
                result = Some((self.runner)(config.clone(), ctx, cancel.clone()).await?);
            }
            Ok((config, untrusted, result))
        }
        .await;
        match outcome {
            Ok((config, untrusted, result)) => self.complete(generation, config, untrusted, result, context),
            Err(Cancelled) => self.complete(generation, None, None, None, context),
        }
    }

    fn complete(
        self: &Arc<Self>,
        generation: u64,
        config: Option<StatusLineConfig>,
        untrusted: Option<StatusLineConfig>,
        result: Option<StatusLineResult>,
        context: StatusLineContext,
    ) {
        let (rerun, pending) = {
            let mut state = self.state();
            if state.closed {
                state.running = false;
                self.stopped.send_replace(true);
                return;
            }
            if generation != state.generation {
                return;
            }
            state.config = config;
            state.untrusted = untrusted;
            state.result = result;
            state.updated_at = Some(self.clock.now());
            state.running = false;
            let rerun = state.pending;
            state.pending = false;
            (rerun, state.pending_context.take())
        };
        self.notify();
        if rerun {
            if self.state().closed {
                return;
            }
            self.request_refresh(pending.unwrap_or(context), false);
        }
    }
}
