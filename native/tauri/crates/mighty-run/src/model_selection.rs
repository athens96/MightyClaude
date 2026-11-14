//! C# ModelSelection.cs and ClaudeModelReset.cs: a pane's model and effort kept right against a newly read model list
//! (`ModelSelectionSupport`, M/MightyCore/ModelSelectionSupport.swift), the reads of each context's list one probe at a time
//! (`ModelCatalogRefresh`, M/LocalModelCatalogRefresh.swift), and Settings › CLI accounts' "모델 초기화·다시 불러오기"
//! (`ClaudeModelReset`, M/AppStore+ClaudeModelReset.swift).

use std::collections::HashMap;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, LazyLock, Mutex};
use std::time::Duration;

use mighty_core::model::{AppSnapshot, LogEntry, RunSession, RunSettings, Workspace};
use mighty_core::time::DateTimeOffset;
use mighty_core::{time, wire};
use mighty_platform::BoxFuture;
use regex::Regex;
use tokio::sync::watch;

use crate::model::{ModelCatalog, ModelOption, ProviderRuntime};
use crate::{apply, lock, providers, strings};

// ── ModelSelectionSupport ───────────────────────────────────────────────────────────────────────────────────────────────

/// C# `ModelSelectionResolution(Model, Effort)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ModelSelectionResolution {
    pub model: String,
    pub effort: String,
}

impl ModelSelectionResolution {
    pub fn new(model: &str, effort: &str) -> ModelSelectionResolution {
        ModelSelectionResolution { model: model.to_string(), effort: effort.to_string() }
    }
}

static OFFICIAL_CLAUDE: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(
        r"^(?:(?:opus|sonnet|fable)\[1m\]|claude-(?:(?:opus|sonnet|haiku|fable)-[0-9]+(?:[-.][0-9]+)*|[0-9]+(?:-[0-9]+)*-(?:opus|sonnet|haiku|fable)(?:-[0-9]+)*)(?:\[1m\])?)$",
    )
    .expect("pattern")
});

/// `ModelSelectionSupport.IsOfficialClaudeModel`: a model name Claude Code itself knows — a row of the fallback list, an alias with
/// `[1m]`, or a `claude-…` id (M/MightyCore/Models.swift:694-696).
pub fn is_official_claude_model(value: &str) -> bool {
    providers::fallback("claude").models.iter().any(|m| m.value == value) || OFFICIAL_CLAUDE.is_match(value)
}

fn resolved_is(option: &ModelOption, model: &str) -> bool {
    option.resolved_model.as_deref() == Some(model)
}

/// `ModelSelectionSupport.Reconcile`: the model and effort a pane keeps under a newly read list. Only a list the CLI itself gave on
/// its latest read can retire a model it offered before; a custom id survives a missing or inconclusive reading, and a kept older
/// list (ReadFailure set: the newest read failed) changes nothing.
pub fn reconcile(
    model: &str,
    effort: &str,
    previous: Option<&ModelCatalog>,
    refreshed: &ModelCatalog,
    provider: Option<&str>,
) -> ModelSelectionResolution {
    let original = ModelSelectionResolution::new(model, effort);
    if !refreshed.is_fresh() {
        return original;
    }
    let prior = previous.and_then(|p| p.models.iter().find(|m| m.value == model || resolved_is(m, model)));
    let direct = refreshed
        .models
        .iter()
        .find(|m| m.value == model)
        .or_else(|| refreshed.models.iter().find(|m| m.value != "default" && resolved_is(m, model)));
    // Bedrock's list can hold concrete regional ids while the CLI still takes the stable aliases: a missing row must not change
    // their model family, or freeze an alias to what it last stood for.
    let stable_alias = provider == Some("claude") && !model.starts_with("claude-") && is_official_claude_model(model);
    if stable_alias && direct.is_none() {
        return original;
    }
    let matched = direct.or_else(|| {
        let resolved = prior?.resolved_model.as_deref()?;
        refreshed.models.iter().find(|m| m.value != "default" && (m.value == resolved || resolved_is(m, resolved)))
    });
    let Some(matched) = matched else {
        let retired_official = provider == Some("claude") && model.starts_with("claude-") && is_official_claude_model(model);
        return if prior.is_none() && !retired_official { original } else { ModelSelectionResolution::new("default", "default") };
    };
    let unsupported = matched.supports_effort == Some(false)
        || matched.supported_effort_levels.as_ref().is_some_and(|levels| !levels.iter().any(|l| l == effort));
    ModelSelectionResolution {
        model: matched.value.clone(),
        effort: if effort != "default" && unsupported { "default".into() } else { effort.to_string() },
    }
}

/// `ModelSelectionSupport.Adjusted`: the pane with its model and effort put right against a newly read list, and the transcript line
/// that says what changed (M/AppStore+ModelCatalogs.swift:47-63); None when the list leaves the choice as it is. A pane that continues
/// a conversation and falls back to the default names the model the default stands for: with none named, the CLI would restore the
/// conversation's retired model.
pub fn adjusted(pane: &RunSession, previous: Option<&ModelCatalog>, refreshed: &ModelCatalog) -> Option<RunSession> {
    let settings = pane.settings.clone().unwrap_or_default();
    let resolution = reconcile(pane.model(), &settings.effort, previous, refreshed, Some(pane.provider()));
    let mut model = resolution.model.clone();
    if pane.model() != "default"
        && model == "default"
        && pane.resume_id.is_some()
        && let Some(stands) = refreshed
            .models
            .iter()
            .find(|m| m.value == "default")
            .and_then(|m| m.resolved_model.as_deref())
            .filter(|s| !s.is_empty() && *s != "default")
    {
        model = stands.to_string();
    }
    if model == pane.model() && resolution.effort == settings.effort {
        return None;
    }
    let line = strings::get_with(
        "composer.model.catalogAdjusted",
        &[("fromModel", pane.model()), ("toModel", &model), ("fromEffort", &settings.effort), ("toEffort", &resolution.effort)],
    );
    let mut logs = pane.logs().to_vec();
    logs.push(LogEntry::new(wire::new_id(), "system", line, time::now_o(), Some(pane.provider().to_string())));
    Some(RunSession {
        model: Some(model),
        settings: Some(RunSettings { effort: resolution.effort, ..settings }),
        logs: Some(apply::trimmed(logs)),
        ..pane.clone()
    })
}

// ── ModelCatalogRefresh ─────────────────────────────────────────────────────────────────────────────────────────────────

/// C# `ModelContext(WorkspaceId, Path, Provider)`: one provider's model list in one workspace folder.
#[derive(Debug, Clone, PartialEq, Eq, Hash)]
pub struct ModelContext {
    pub workspace_id: String,
    pub path: String,
    pub provider: String,
}

impl ModelContext {
    pub fn new(workspace_id: &str, path: &str, provider: &str) -> ModelContext {
        ModelContext { workspace_id: workspace_id.into(), path: path.into(), provider: provider.into() }
    }
}

/// `ModelCatalogRefresh.ForcedLifetime`: a list the CLI gave answers a forced request (a pane selected) for 2 s.
pub const FORCED_LIFETIME: Duration = Duration::from_secs(2);
/// `ModelCatalogRefresh.Lifetime`: and any other (a run about to start) for 60 s.
pub const LIFETIME: Duration = Duration::from_secs(60);
/// `ModelCatalogRefresh.FailedLifetime`: a failed reading, or a fallback or kept one, is not asked again by a run for 15 s.
pub const FAILED_LIFETIME: Duration = Duration::from_secs(15);
/// `ModelCatalogRefresh.PreRunWait`: the longest a run waits for its list before it starts with the one in hand (a read's own
/// limit is 7 s).
pub const PRE_RUN_WAIT: Duration = Duration::from_secs(3);
/// `ModelCatalogRefresh.Settle`: one gesture can ask twice (a selection and the focus it moves): a probe waits this long so they
/// share it.
pub const SETTLE: Duration = Duration::from_millis(100);

/// The loader (`Func<ModelContext, Task<ProviderRuntime>>`): an Err is a reading that threw.
pub type Loader = Arc<dyn Fn(ModelContext) -> BoxFuture<'static, Result<ProviderRuntime, String>> + Send + Sync>;
/// `changed`: hears every new list with the one before it.
pub type Changed = Arc<dyn Fn(&ModelContext, Option<&ProviderRuntime>, &ProviderRuntime) + Send + Sync>;
/// `clock`.
pub type Clock = Arc<dyn Fn() -> DateTimeOffset + Send + Sync>;

/// The answer to a request: the list in hand at once, or the probe's when it ends (C#'s `Task<ProviderRuntime?>`; two requests that
/// share a probe answer with the same one, [`ModelRequest::same`]).
#[derive(Clone)]
pub struct ModelRequest {
    probe: Option<u64>,
    result: watch::Receiver<Option<Option<ProviderRuntime>>>,
}

impl ModelRequest {
    fn ready(value: Option<ProviderRuntime>) -> ModelRequest {
        let (_, result) = watch::channel(Some(value));
        ModelRequest { probe: None, result }
    }

    /// The list, or None once stopped or when the reading failed.
    pub async fn wait(mut self) -> Option<ProviderRuntime> {
        match self.result.wait_for(Option::is_some).await {
            Ok(value) => value.clone().flatten(),
            Err(_) => None,
        }
    }

    /// `ReferenceEquals`: both wait on the same probe.
    pub fn same(&self, other: &ModelRequest) -> bool {
        self.probe.is_some() && self.probe == other.probe
    }

    /// `IsCompleted`.
    pub fn is_completed(&self) -> bool {
        self.result.borrow().is_some()
    }
}

struct ProbeSlot {
    id: u64,
    result: watch::Receiver<Option<Option<ProviderRuntime>>>,
}

#[derive(Default)]
struct RefreshEntry {
    revision: u64,
    loaded_revision: Option<u64>,
    value: Option<ProviderRuntime>,
    /// `LoadedAt` (None: `MinValue`).
    loaded_at: Option<DateTimeOffset>,
    /// `FailedAt` (None: `MinValue`).
    failed_at: Option<DateTimeOffset>,
    probe: Option<ProbeSlot>,
}

#[derive(Default)]
struct RefreshState {
    entries: HashMap<ModelContext, RefreshEntry>,
    stopped: bool,
}

struct RefreshInner {
    loader: Loader,
    changed: Changed,
    clock: Option<Clock>,
    runtime: tokio::runtime::Handle,
    state: Mutex<RefreshState>,
    probes: AtomicU64,
}

/// C# `ModelCatalogRefresh`: reads each context's model list one probe at a time. Requests made while a probe runs share it; an
/// invalidating request during a probe makes it read again before it answers, so it never answers with the list from before; and a
/// list the CLI gave is used again for 2 s by a forced request (a pane selected) and for 60 s by any other (a run about to start). A
/// reading that failed, or gave only the fallback list or a kept older one (offline, signed out), is not asked again by a run about
/// to start for [`FAILED_LIFETIME`], so each send does not wait for the CLI to fail anew; a selection asks again.
#[derive(Clone)]
pub struct ModelCatalogRefresh {
    inner: Arc<RefreshInner>,
}

fn older_than(now: DateTimeOffset, at: Option<DateTimeOffset>, lifetime: Duration) -> bool {
    at.is_none_or(|at| (now.utc_ticks() - at.utc_ticks()) as i128 >= lifetime.as_nanos() as i128 / 100)
}

impl ModelCatalogRefresh {
    /// The probes run on `runtime`.
    pub fn new(runtime: tokio::runtime::Handle, loader: Loader, changed: Changed, clock: Option<Clock>) -> ModelCatalogRefresh {
        ModelCatalogRefresh {
            inner: Arc::new(RefreshInner {
                loader,
                changed,
                clock,
                runtime,
                state: Mutex::new(RefreshState::default()),
                probes: AtomicU64::new(0),
            }),
        }
    }

    fn now(&self) -> DateTimeOffset {
        self.inner.clock.as_ref().map_or_else(DateTimeOffset::now, |clock| clock())
    }

    /// `Value(key)`.
    pub fn value(&self, key: &ModelContext) -> Option<ProviderRuntime> {
        lock(&self.inner.state).entries.get(key).and_then(|e| e.value.clone())
    }

    /// `IsCurrent(key)`: the list in hand is the newest asked for — no probe runs and nothing invalidated it since it was read.
    pub fn is_current(&self, key: &ModelContext) -> bool {
        lock(&self.inner.state).entries.get(key).is_some_and(|e| e.probe.is_none() && e.loaded_revision == Some(e.revision))
    }

    /// `Request(key, force, invalidate)`: the context's list — the one in hand while it is fresh, else the running probe's, else a
    /// new probe's. With `invalidate` the list in hand is known to be another agent's or account's: it is not used, and a running
    /// probe reads again. None once stopped, or when the reading failed.
    pub fn request(&self, key: &ModelContext, force: bool, invalidate: bool) -> ModelRequest {
        let now = self.now();
        let mut state = lock(&self.inner.state);
        if state.stopped {
            return ModelRequest::ready(None);
        }
        let entry = state.entries.entry(key.clone()).or_default();
        if invalidate {
            entry.revision += 1;
            entry.loaded_at = None;
        }
        if let Some(probe) = &entry.probe {
            return ModelRequest { probe: Some(probe.id), result: probe.result.clone() };
        }
        if let Some(value) = &entry.value {
            let lifetime = match (value.model_catalog.is_fresh(), force) {
                (true, true) => FORCED_LIFETIME,
                (true, false) => LIFETIME,
                (false, true) => Duration::ZERO,
                (false, false) => FAILED_LIFETIME,
            };
            if !older_than(now, entry.loaded_at, lifetime) {
                return ModelRequest::ready(Some(value.clone()));
            }
        }
        if !force && !invalidate && !older_than(now, entry.failed_at, FAILED_LIFETIME) {
            return ModelRequest::ready(None);
        }
        let id = self.inner.probes.fetch_add(1, Ordering::Relaxed) + 1;
        let (sender, result) = watch::channel(None);
        entry.probe = Some(ProbeSlot { id, result: result.clone() });
        let refresh = self.clone();
        let context = key.clone();
        self.inner.runtime.spawn(async move { refresh.probe(context, id, sender).await });
        ModelRequest { probe: Some(id), result }
    }

    /// `WaitCurrent(key, limit)`: waits for the list a run should start with (M/AppStore+ModelCatalogs.swift:66-76), but no longer than
    /// `limit` ([`PRE_RUN_WAIT`]): true when the newest list is in hand, false when it failed, was stopped or took too long, and the run
    /// goes on with what is in hand.
    pub async fn wait_current(&self, key: &ModelContext, limit: Option<Duration>) -> bool {
        let deadline = tokio::time::sleep(limit.unwrap_or(PRE_RUN_WAIT));
        tokio::pin!(deadline);
        loop {
            let request = self.request(key, false, false);
            let value = tokio::select! {
                biased;
                value = request.wait() => value,
                _ = &mut deadline => return false,
            };
            if value.is_none() {
                return false;
            }
            if self.is_current(key) {
                return true;
            }
        }
    }

    /// `Same(a, b)`: whether two readings say the same, compared by what they hold.
    pub fn same(a: Option<&ProviderRuntime>, b: Option<&ProviderRuntime>) -> bool {
        matches!((a, b), (Some(a), Some(b)) if a == b)
    }

    /// `Store(key, value)`: a list read another way (the whole runtime at once) is the context's newest: it is kept and reported like
    /// a probe's.
    pub fn store(&self, key: &ModelContext, value: ProviderRuntime) {
        let now = self.now();
        let previous = {
            let mut state = lock(&self.inner.state);
            if state.stopped {
                return;
            }
            let entry = state.entries.entry(key.clone()).or_default();
            let previous = entry.value.replace(value.clone());
            entry.loaded_at = Some(now);
            if entry.probe.is_none() {
                entry.loaded_revision = Some(entry.revision);
            }
            previous
        };
        (self.inner.changed)(key, previous.as_ref(), &value);
    }

    /// `Invalidate(provider)`: the lists in hand are old (M/LocalModelCatalogRefresh.swift invalidate) — of `provider` in every
    /// workspace, or of every provider. A sign-in or sign-out changed what that CLI lists (M/AppStore+ModelCatalogs.swift
    /// modelsAfterAccountChange), so none is used again, a running probe reads again before it answers, and the next request reads
    /// anew, a run's included.
    pub fn invalidate(&self, provider: Option<&str>) {
        let mut state = lock(&self.inner.state);
        for (key, entry) in state.entries.iter_mut() {
            if provider.is_none_or(|p| key.provider == p) {
                entry.revision += 1;
                entry.loaded_at = None;
                entry.failed_at = None;
            }
        }
    }

    /// `Shutdown()`.
    pub fn shutdown(&self) {
        let mut state = lock(&self.inner.state);
        state.stopped = true;
        state.entries.clear();
    }

    /// `ProbeAsync`: never finished by the time `request` kept it (it waits [`SETTLE`] first); reads until no invalidation overtook the
    /// reading, then keeps and reports it. A reading that failed answers None and holds runs off for [`FAILED_LIFETIME`].
    async fn probe(&self, key: ModelContext, id: u64, sender: watch::Sender<Option<Option<ProviderRuntime>>>) {
        tokio::time::sleep(SETTLE).await;
        // Dropped before `sender`, so a request woken by the probe's end never finds it still kept.
        let mut end = ProbeEnd { refresh: self, key: &key, id, ended: false };
        let result = loop {
            let revision = {
                let state = lock(&self.inner.state);
                match state.entries.get(&key) {
                    Some(entry) if !state.stopped => entry.revision,
                    _ => break None,
                }
            };
            let read = (self.inner.loader)(key.clone()).await;
            let now = self.now();
            match read {
                Err(_) => {
                    if let Some(entry) = lock(&self.inner.state).entries.get_mut(&key) {
                        entry.failed_at = Some(now);
                    }
                    break None;
                }
                Ok(value) => {
                    let previous = {
                        let mut state = lock(&self.inner.state);
                        if state.stopped {
                            break None;
                        }
                        let Some(entry) = state.entries.get_mut(&key) else { break None };
                        if revision != entry.revision {
                            continue;
                        }
                        let previous = entry.value.replace(value.clone());
                        entry.loaded_at = Some(now);
                        entry.loaded_revision = Some(revision);
                        entry.probe = None;
                        previous
                    };
                    (self.inner.changed)(&key, previous.as_ref(), &value);
                    break Some(value);
                }
            }
        };
        end.ended = true;
        drop(end);
        let _ = sender.send(Some(result));
    }
}

/// `ProbeAsync`'s `finally` (and its `catch` for a panic, ModelSelection.cs:171-172): however the probe ends — a read or `changed` that
/// panicked included — it is forgotten as `request` kept it (a probe that `changed` starts is not forgotten by this one's end), and one
/// that never reached its answer counts as a failed reading, so runs do not ask again for [`FAILED_LIFETIME`]. Its waiters then answer
/// None (the sender went with it).
struct ProbeEnd<'a> {
    refresh: &'a ModelCatalogRefresh,
    key: &'a ModelContext,
    id: u64,
    /// The probe reached its answer.
    ended: bool,
}

impl Drop for ProbeEnd<'_> {
    fn drop(&mut self) {
        let failed = (!self.ended).then(|| self.refresh.now());
        if let Some(entry) = lock(&self.refresh.inner.state).entries.get_mut(self.key) {
            if let Some(now) = failed {
                entry.failed_at = Some(now);
            }
            if entry.probe.as_ref().is_some_and(|p| p.id == self.id) {
                entry.probe = None;
            }
        }
    }
}

// ── ClaudeModelReset ────────────────────────────────────────────────────────────────────────────────────────────────────

/// `ClaudeModelReset.Concurrency`: how many lists are read at once.
pub const RESET_CONCURRENCY: usize = 3;
/// `ClaudeModelReset.ReadTimeout`: how long one read may take before it counts as timed out.
pub const RESET_READ_TIMEOUT: Duration = Duration::from_secs(7);

/// A cancellation a read looks at (C#'s `CancellationToken`): cancelled when its source is, or its deadline passed. It holds its
/// source, so it never reads as cancelled only because the source went away.
#[derive(Clone)]
pub struct Cancellation(Arc<watch::Sender<bool>>);

impl Cancellation {
    /// One that never ends (`CancellationToken.None`).
    pub fn none() -> Cancellation {
        CancellationSource::default().token()
    }

    pub fn is_cancelled(&self) -> bool {
        *self.0.borrow()
    }

    /// Ends when it is cancelled.
    pub async fn cancelled(&self) {
        let mut receiver = self.0.subscribe();
        let _ = receiver.wait_for(|cancelled| *cancelled).await;
    }
}

/// C#'s `CancellationTokenSource`.
pub struct CancellationSource(Arc<watch::Sender<bool>>);

impl Default for CancellationSource {
    fn default() -> Self {
        CancellationSource(Arc::new(watch::channel(false).0))
    }
}

impl CancellationSource {
    pub fn token(&self) -> Cancellation {
        Cancellation(self.0.clone())
    }
    pub fn cancel(&self) {
        self.0.send_replace(true);
    }
}

/// A reset that ended because its window closed (`OperationCanceledException`).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct ResetCancelled;

/// One workspace's read: the list its CLI gives, given a token that ends with the reset or after its time.
pub type ResetRead = Arc<dyn Fn(Workspace, Cancellation) -> BoxFuture<'static, ModelCatalog> + Send + Sync>;

/// `ClaudeModelReset.Apply`: the state with every Claude pane on the default model and effort; conversations are kept.
pub fn reset_apply(state: &AppSnapshot) -> AppSnapshot {
    let mut next = state.clone();
    for pane in next.sessions_mut() {
        if pane.kind() == "claude" && pane.provider() == "claude" {
            let settings = pane.settings.clone().unwrap_or_default();
            *pane = Arc::new(RunSession {
                model: Some("default".into()),
                settings: Some(RunSettings { effort: "default".into(), ..settings }),
                ..(**pane).clone()
            });
        }
    }
    next
}

/// `ClaudeModelReset.ReloadAsync`: reads every workspace's list, since each folder can have its own CLI settings, and returns the
/// result sentence. Each read starts on a task of its own (a CLI never starts on the caller's thread), at most [`RESET_CONCURRENCY`]
/// at a time, and is given a token that ends with `cancellation` or after `timeout` ([`RESET_READ_TIMEOUT`]). A list the CLI did
/// not give on this read counts as not read: as timed out when its time ran out, else as a sign-in or setup the CLI still needs. A
/// cancelled reset ends with [`ResetCancelled`].
pub async fn reset_reload(
    workspaces: &[Workspace],
    read: ResetRead,
    cancellation: Cancellation,
    timeout: Option<Duration>,
) -> Result<String, ResetCancelled> {
    let gate = Arc::new(tokio::sync::Semaphore::new(RESET_CONCURRENCY));
    let timeout = timeout.unwrap_or(RESET_READ_TIMEOUT);
    let mut reads = Vec::new();
    for workspace in workspaces.iter().cloned() {
        let (gate, read, cancellation) = (gate.clone(), read.clone(), cancellation.clone());
        reads.push(async move {
            let permit = tokio::select! {
                permit = gate.acquire_owned() => permit.map_err(|_| ResetCancelled)?,
                _ = cancellation.cancelled() => return Err(ResetCancelled),
            };
            if cancellation.is_cancelled() {
                return Err(ResetCancelled);
            }
            // The read's token: the reset's, or its own time running out.
            let deadline = CancellationSource::default();
            let token = deadline.token();
            let watched = cancellation.clone();
            let ends = tokio::spawn(async move {
                tokio::select! {
                    _ = tokio::time::sleep(timeout) => {}
                    _ = watched.cancelled() => {}
                }
                deadline.cancel();
            });
            let catalog = tokio::spawn((read)(workspace, token.clone())).await;
            let timed_out = token.is_cancelled();
            ends.abort();
            drop(permit);
            if cancellation.is_cancelled() {
                return Err(ResetCancelled);
            }
            let fresh = catalog.as_ref().is_ok_and(ModelCatalog::is_fresh);
            // A kept older list is not a reload.
            Ok((fresh, !fresh && timed_out))
        });
    }
    let mut results = Vec::new();
    for outcome in futures_join(reads).await {
        results.push(outcome?);
    }
    let failed = results.iter().filter(|(read, timed_out)| !read && !timed_out).count();
    let timed_out = results.iter().filter(|(_, timed_out)| *timed_out).count();
    Ok(reset_result(workspaces.len(), failed, timed_out))
}

/// `Task.WhenAll` over futures that each run on their own.
async fn futures_join<T: Send + 'static>(futures: Vec<impl std::future::Future<Output = T> + Send + 'static>) -> Vec<T> {
    let handles: Vec<_> = futures.into_iter().map(tokio::spawn).collect();
    let mut results = Vec::with_capacity(handles.len());
    for handle in handles {
        match handle.await {
            Ok(value) => results.push(value),
            Err(error) => std::panic::resume_unwind(error.into_panic()),
        }
    }
    results
}

/// `ClaudeModelReset.Result`: the sentence for `failed` lists the CLI would not give and `timed_out` it did not give in time, of
/// `workspaces`.
pub fn reset_result(workspaces: usize, failed: usize, timed_out: usize) -> String {
    if workspaces == 0 {
        return strings::get("settings.cliAccounts.modelReset.cacheCleared");
    }
    if failed == 0 && timed_out == 0 {
        return strings::get_with("settings.cliAccounts.modelReset.done", &[("count", &workspaces.to_string())]);
    }
    let mut said = Vec::new();
    if failed > 0 {
        said.push(strings::get_with("settings.cliAccounts.modelReset.partial", &[("count", &failed.to_string())]));
    }
    if timed_out > 0 {
        said.push(strings::get_with("settings.cliAccounts.modelReset.timedOut", &[("count", &timed_out.to_string())]));
    }
    said.join(" ")
}
