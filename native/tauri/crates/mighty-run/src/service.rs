//! The run half of C# `DesktopService` (DesktopService.cs:97-127, 249-276, 299-416): starting a pane's request (`Launch`: the pane
//! retitled, its user entry and running state applied before the runner takes it), every run event applied to the state
//! (`Receive`: `AppSnapshot.Apply`, which runs `ClaudePlanMode.Record`), the routes that let a stop reach a run not started yet, the
//! answers to a run's requests, and the waiting tool-permission requests per pane (`ToolPermissionChanged`).
//!
//! Host-agnostic: no Tauri type. The service owns the state's copy it applies events to; what happened goes out through one
//! channel ([`ServiceEvent`]) in order — each applied event, each permission change, and when to save (at once at a run's end,
//! else on the host's save schedule, C#'s `QueueSave` and `ScheduleSave`).
//!
//! Until their stages: a remote workspace's runs go through a [`RemoteRunner`] (W8); delegation's run ledger, merges and notices
//! (W7), Antigravity's rules (W5), the file originals' pruning (W4), the style's own tool permissions (W6) and the known-session
//! list (W4: [`ServiceEvent::RememberSession`] hands the id to the host) are not here.

use std::collections::HashMap;
use std::path::Path;
use std::sync::{Arc, Mutex};

use indexmap::IndexMap;
use mighty_core::graph::GRAPH_PROVIDERS;
use mighty_core::model::{AppSnapshot, PlanRecord, RunSession, Workspace};
use mighty_core::normalize;
use mighty_core::time::DateTimeOffset;
use mighty_core::wire;
use mighty_platform::BoxFuture;
use tokio::sync::mpsc;

use crate::apply;
use crate::attachments;
use crate::manager::{RunManager, RunManagerOptions};
use crate::model::{RunEvent, StartRunRequest};
use crate::permissions::ToolPermissionRequest;
use crate::phase_models;
use crate::plan_mode::PlanDecision;
use crate::providers::{self, ProviderCatalog};
use crate::questionnaire::UserQuestionAnswer;
use crate::strings;
use crate::timing;
use crate::{Result, RunError, lock};

/// What the service tells its host, in order.
#[derive(Debug, Clone)]
pub enum ServiceEvent {
    /// A run event as applied (C# `RunEventReceived`). `notice_turn_ended`: a notice turn ran before it and no longer does.
    Run { event: Box<RunEvent>, notice_turn_ended: bool },
    /// A request is about to start (C# `RequestStarting`).
    RequestStarting(Box<StartRunRequest>),
    /// A tool-permission request waiting in, or settled by, a local pane (C# `ToolPermissionChanged`): never a run event, never saved.
    ToolPermission(Box<ToolPermissionRequest>),
    /// Save the state now (a run's start and end: C# `QueueSave`).
    SaveNow,
    /// Save the state on the host's schedule (a streaming run: C# `ScheduleSave`).
    SaveSoon,
    /// A session id a pane started or resumed (C# `RememberSession`, the resume list's known sessions).
    RememberSession(String),
}

/// Another computer's runs, for a remote workspace's panes (C# `RemoteClientService`, W8).
pub trait RemoteRunner: Send + Sync {
    fn start(&self, request: StartRunRequest, workspace: Workspace) -> BoxFuture<'_, Result<bool>>;
    fn stop(&self, session_id: String) -> BoxFuture<'_, Result<()>>;
    fn respond_to_tool_permission(&self, session_id: String, request_id: String, allow: bool) -> BoxFuture<'_, Result<()>>;
    fn answer_questions(
        &self,
        session_id: String,
        request_id: String,
        answers: IndexMap<String, UserQuestionAnswer>,
    ) -> BoxFuture<'_, Result<()>>;
    fn answer_plan(&self, session_id: String, request_id: String, decision: PlanDecision) -> BoxFuture<'_, Result<()>>;
    fn is_running(&self, session_id: &str) -> bool;
}

#[derive(Default)]
struct Route {
    started: bool,
    cancelled: bool,
}

struct State {
    snapshot: AppSnapshot,
    routes: HashMap<String, Route>,
    closing: bool,
}

struct Shared {
    state: Mutex<State>,
    events: mpsc::UnboundedSender<ServiceEvent>,
    /// The waiting tool-permission requests of each pane, in the order they came.
    pending: Mutex<HashMap<String, IndexMap<String, ToolPermissionRequest>>>,
}

impl Shared {
    fn send(&self, event: ServiceEvent) {
        let _ = self.events.send(event);
    }

    /// `Receive`: a valid event applied; a run's end saved at once, anything else on the schedule.
    fn receive(&self, value: RunEvent) {
        if !value.valid() {
            return;
        }
        let notice_turn_ended = {
            let mut state = lock(&self.state);
            self.receive_locked(&mut state, &value)
        };
        self.after_receive(value, notice_turn_ended);
    }

    fn receive_locked(&self, state: &mut State, value: &RunEvent) -> bool {
        let before = state.snapshot.session(&value.session_id).map(|s| s.graph_runs().to_vec()).unwrap_or_default();
        state.snapshot = apply::apply(&state.snapshot, value, DateTimeOffset::now());
        let after = state.snapshot.session(&value.session_id).map(|s| s.graph_runs().to_vec()).unwrap_or_default();
        let ended = value.kind == "status" && matches!(value.status.as_deref(), Some("stopped" | "completed" | "error"));
        if ended {
            state.routes.remove(&value.session_id);
            self.send(ServiceEvent::SaveNow);
        } else {
            self.send(ServiceEvent::SaveSoon);
        }
        apply::notice_turn_ended(&before, &after)
    }

    fn after_receive(&self, value: RunEvent, notice_turn_ended: bool) {
        if value.kind == "resume"
            && let Some(resumed) = &value.resume_id
        {
            self.send(ServiceEvent::RememberSession(resumed.clone()));
        }
        self.send(ServiceEvent::Run { event: Box::new(value), notice_turn_ended });
    }

    /// `ReceiveToolPermission`: a request waiting or settled, kept per pane and handed to the host.
    fn tool_permission(&self, value: ToolPermissionRequest) {
        {
            let mut pending = lock(&self.pending);
            let pane = pending.entry(value.run_id.clone()).or_default();
            if value.state == "pending" {
                pane.insert(value.id.clone(), value.clone());
            } else {
                pane.shift_remove(&value.id);
            }
            if pane.is_empty() {
                pending.remove(&value.run_id);
            }
        }
        self.send(ServiceEvent::ToolPermission(Box::new(value)));
    }
}

/// The run half of C# `DesktopService`.
pub struct RunService {
    shared: Arc<Shared>,
    manager: RunManager,
    remote: Option<Arc<dyn RemoteRunner>>,
    admitting: Option<Admitting>,
}

/// How the service is made.
pub struct RunServiceOptions {
    pub plugin_directory: String,
    /// The host can show the approval card (a Claude run then asks it over stdio).
    pub permission_card: bool,
    pub remote: Option<Arc<dyn RemoteRunner>>,
    /// Plugs the graph tracker, the image store, the Agent IO binding and the file originals into the runner.
    pub configure: Option<Configure>,
    /// Called with the pane's id once its route is marked started, just before the runner admits the request, under the service's
    /// lock (a stop asked for meanwhile waits for the admission). The checks' gate on that window; None in the app.
    pub admitting: Option<Admitting>,
}

pub type Configure = Box<dyn FnOnce(&mut RunManagerOptions) + Send>;
pub type Admitting = Arc<dyn Fn(&str) + Send + Sync>;

impl RunService {
    /// A service over `snapshot` (the loaded state), its events on the returned receiver.
    pub fn new(
        snapshot: AppSnapshot,
        providers: Arc<ProviderCatalog>,
        options: RunServiceOptions,
    ) -> (RunService, mpsc::UnboundedReceiver<ServiceEvent>) {
        let (events, receiver) = mpsc::unbounded_channel();
        let shared = Arc::new(Shared {
            state: Mutex::new(State { snapshot, routes: HashMap::new(), closing: false }),
            events,
            pending: Mutex::new(HashMap::new()),
        });
        let resolver = shared.clone();
        let resolve: crate::manager::ResolveWorkspace = Arc::new(move |id: String| {
            let shared = resolver.clone();
            Box::pin(async move { resolve_local(&shared, &id) })
        });
        let receiver_shared = shared.clone();
        let emit: crate::manager::Emit = Arc::new(move |event| receiver_shared.receive(event));
        let mut manager_options = RunManagerOptions::new(&options.plugin_directory, resolve, emit);
        if options.permission_card {
            let permissions = shared.clone();
            manager_options.permission_requested = Some(Arc::new(move |value| permissions.tool_permission(value)));
        }
        if let Some(configure) = options.configure {
            configure(&mut manager_options);
        }
        let manager = RunManager::new(providers, manager_options);
        (RunService { shared, manager, remote: options.remote, admitting: options.admitting }, receiver)
    }

    pub fn manager(&self) -> &RunManager {
        &self.manager
    }

    /// The state as the service has it now.
    pub fn snapshot(&self) -> AppSnapshot {
        lock(&self.shared.state).snapshot.clone()
    }

    /// The state changed elsewhere (the host's own changes): the service applies its events to this from now on.
    pub fn replace(&self, snapshot: AppSnapshot) {
        lock(&self.shared.state).snapshot = snapshot;
    }

    /// `UpdateAsync`: a change normalized as C# normalizes every change, then saved.
    pub fn update(&self, change: impl FnOnce(&AppSnapshot) -> AppSnapshot) -> Result<()> {
        {
            let mut state = lock(&self.shared.state);
            if state.closing {
                return Err(RunError::State("the service is closing".into()));
            }
            let next = normalize::normalize(&change(&state.snapshot), false).map_err(|e| RunError::Invalid(e.to_string()))?;
            state.snapshot = next;
        }
        self.shared.send(ServiceEvent::SaveNow);
        Ok(())
    }

    /// `UpdateDraft` (DesktopService.cs:198): a pane's draft, cleaned to at most 100,000 characters, replaced on that pane alone without
    /// the whole state's normalize and saved on the schedule (a draft streams as it is typed). False when the pane is gone or its draft
    /// is that already.
    pub fn update_draft(&self, session_id: &str, draft: &str) -> Result<bool> {
        {
            let mut state = lock(&self.shared.state);
            if state.closing {
                return Err(RunError::State("the service is closing".into()));
            }
            let bounded = wire::clean(draft, 100_000);
            let Some(at) = state.snapshot.sessions().iter().position(|s| s.id() == session_id) else { return Ok(false) };
            if state.snapshot.sessions()[at].draft.as_deref().unwrap_or("") == bounded {
                return Ok(false);
            }
            let mut next = (*state.snapshot.sessions()[at]).clone();
            next.draft = Some(bounded);
            state.snapshot.sessions_mut()[at] = Arc::new(next);
        }
        self.shared.send(ServiceEvent::SaveSoon);
        Ok(true)
    }

    /// C# `PaneView.ClearToolPermissions` (MainWindow.cs:88): a pane whose run ended (stopped, completed, error) has no request waiting
    /// any more; the window drops them all, whether or not the channel settled each. False when none was waiting.
    pub fn clear_permissions(&self, session_id: &str) -> bool {
        lock(&self.shared.pending).remove(session_id).is_some()
    }

    /// The waiting tool-permission requests of a pane, oldest first.
    pub fn pending_permissions(&self, session_id: &str) -> Vec<ToolPermissionRequest> {
        lock(&self.shared.pending).get(session_id).map(|p| p.values().cloned().collect()).unwrap_or_default()
    }

    fn remote_pane(&self, session_id: &str) -> bool {
        let state = lock(&self.shared.state);
        let snapshot = &state.snapshot;
        snapshot.session(session_id).and_then(|s| snapshot.workspace(s.workspace_id())).is_some_and(|w| w.remote.is_some())
    }

    /// Whether a pane of this computer runs `provider`'s CLI now (`HasActiveProvider`).
    pub fn has_active_provider(&self, provider: &str) -> bool {
        let state = lock(&self.shared.state);
        let snapshot = &state.snapshot;
        snapshot.sessions().iter().any(|s| {
            s.kind() == "claude"
                && s.provider() == provider
                && !snapshot.workspaces().iter().any(|w| w.id() == s.workspace_id() && w.remote.is_some())
                && (s.status() == "running" || state.routes.contains_key(s.id()))
        })
    }

    pub fn is_session_running(&self, id: &str) -> bool {
        self.manager.is_running(id) || self.remote.as_ref().is_some_and(|r| r.is_running(id))
    }

    /// `StartAsync`.
    pub async fn start(&self, value: StartRunRequest) -> Result<Option<()>> {
        self.launch(value, None, false).await
    }

    /// `StartReachedAsync`: returns once the run's process is up with its input open.
    pub async fn start_reached(&self, value: StartRunRequest) -> Result<Option<()>> {
        self.launch(value, None, true).await
    }

    /// `Launch`: the request recorded on its pane and handed to the runner (or the workspace's host). None when it was stopped before
    /// it started.
    pub async fn launch(&self, value: StartRunRequest, title_from: Option<&str>, reach: bool) -> Result<Option<()>> {
        let mut request = value.validate()?;
        let workspace;
        let remote;
        {
            let mut state = lock(&self.shared.state);
            if state.closing {
                return Err(RunError::State("the service is closing".into()));
            }
            let found = state.snapshot.workspace(&request.workspace_id).cloned();
            workspace = found.ok_or_else(|| RunError::Invalid(strings::get("run.error.workspaceNotRegistered")))?;
            remote = workspace.remote.is_some();
            if remote && request.kind != "shell" && providers::is_local_only(&request.provider) {
                return Err(RunError::State(strings::get(&format!("run.blocked.{}Remote", request.provider))));
            }
            let pane = state
                .snapshot
                .sessions()
                .iter()
                .find(|s| s.id() == request.session_id && s.workspace_id() == request.workspace_id && s.kind() == request.kind)
                .cloned()
                .ok_or_else(|| RunError::Invalid(strings::get("run.error.sessionNotInWorkspace")))?;
            // A delegated child runs only in its own worktree, never in its parent's checkout.
            if (pane.parent_session_id.is_some() || pane.working_folder.is_some())
                && !pane.working_folder.as_deref().is_some_and(|f| Path::new(f).is_dir())
            {
                return Err(RunError::State(strings::get("run.blocked.worktreeMissing")));
            }
            request.phase_models = phase_models::normalized(state.snapshot.phase_models.as_ref());
            request.todo_progress = if request.resume_id.is_none() { None } else { pane.todo_progress.clone() };
            request.working_folder = pane.working_folder.clone();
            // C# `DelegationWorktreeRoot(pane)`: the child's worktree as delegation made it, never what the caller says; delegation
            // (W7) keeps no worktree here yet, so none.
            request.delegation_worktree_root = None;
            if state.routes.contains_key(&request.session_id) {
                return Err(RunError::State(strings::get("run.error.alreadyRunning")));
            }
            state.routes.insert(request.session_id.clone(), Route::default());
            let title_input = if request.antigravity_grant.is_some() { "" } else { title_from.unwrap_or(&request.input) };
            let now = DateTimeOffset::now();
            for session in state.snapshot.sessions_mut().iter_mut() {
                if session.id() != request.session_id {
                    continue;
                }
                let mut next: RunSession = requested(session, title_input);
                if request.resume_id.is_none() || session.provider() != request.provider {
                    next.session_usage = None;
                }
                next.provider = Some(request.provider.clone());
                next.current_activity = None;
                next.run_timing = if request.kind == "shell" { None } else { Some(timing::begin(Some(now))) };
                *session = Arc::new(next);
            }
            let mut history = request.input.clone();
            if request.attachment_list().is_some() {
                history.push_str("\n\n");
                history.push_str(&attachments::summary(request.attachment_list()));
            }
            let named = (request.kind == "claude").then_some(request.provider.as_str());
            let asked = RunEvent::log(&request.session_id, "user", &history, named);
            // The run's diagram block is named here, so a send can answer with it before the runner's first event.
            request.graph_run_id =
                (!remote && request.kind != "shell" && GRAPH_PROVIDERS.contains(&request.provider.as_str())).then(wire::new_id);
            state.snapshot = apply::apply(&state.snapshot, &RunEvent::state(&request.session_id, "running", None), now);
            state.snapshot = apply::apply(&state.snapshot, &asked, now);
            if let Some(receipt) = &request.receipt {
                let mut receipt = lock(receipt);
                receipt.entry_id = asked.entry.as_ref().and_then(|e| e.id.clone());
                receipt.graph_run_id = request.graph_run_id.clone();
            }
        }
        self.shared.send(ServiceEvent::SaveNow);
        self.shared.send(ServiceEvent::RequestStarting(Box::new(request.clone())));
        self.shared
            .send(ServiceEvent::Run { event: Box::new(RunEvent::state(&request.session_id, "running", None)), notice_turn_ended: false });
        let session_id = request.session_id.clone();
        let admitted = {
            let mut state = lock(&self.shared.state);
            let cancelled = state.closing || state.routes.get(&session_id).is_none_or(|r| r.cancelled);
            if cancelled {
                let stopped = RunEvent::state(&session_id, "stopped", None);
                let ended = self.shared.receive_locked(&mut state, &stopped);
                drop(state);
                self.shared.after_receive(stopped, ended);
                if request.attachment_list().is_some() {
                    return Err(RunError::Cancelled(strings::get("run.error.attachmentSendCancelled")));
                }
                return Ok(None);
            }
            if let Some(route) = state.routes.get_mut(&session_id) {
                route.started = true;
            }
            if let Some(admitting) = &self.admitting {
                admitting(&session_id);
            }
            // The runner registers the run under this lock, as C# starts `local.StartAsync` inside it: a stop that finds the route
            // started always finds the run too.
            (!remote).then(|| self.manager.admit(request.clone(), reach))
        };
        let started = match admitted {
            Some(admitted) => match admitted {
                Ok(admitted) => admitted.reached().await.map(Some),
                Err(error) => Err(error),
            },
            None => match &self.remote {
                Some(runner) => runner.start(request.clone(), workspace).await.map(|reached| reached.then_some(())),
                None => Err(RunError::State(strings::get("run.error.approvedWorkspaceOnly"))),
            },
        };
        match started {
            Ok(Some(())) => Ok(Some(())),
            Ok(None) => {
                self.shared.receive(RunEvent::state(&session_id, "stopped", None));
                Ok(None)
            }
            Err(RunError::Cancelled(message)) => {
                self.shared.receive(RunEvent::state(&session_id, "stopped", None));
                Err(RunError::Cancelled(message))
            }
            Err(error) => {
                self.shared.receive(RunEvent::log(&session_id, "error", error.message(), None));
                self.shared.receive(RunEvent::state(&session_id, "error", None));
                Err(error)
            }
        }
    }

    /// `StopAsync`: a route not started yet is marked, so its start stops; a started one is stopped by its runner.
    pub async fn stop(&self, id: &str) -> Result<()> {
        {
            let mut state = lock(&self.shared.state);
            let Some(route) = state.routes.get_mut(id) else { return Ok(()) };
            route.cancelled = true;
            if !route.started {
                return Ok(());
            }
        }
        if self.remote_pane(id)
            && let Some(remote) = &self.remote
        {
            return remote.stop(id.to_string()).await;
        }
        self.manager.stop(id).await
    }

    /// `RespondToToolPermission`: 이번만 허용 / 거부, the only way a request is ever answered.
    pub async fn respond_to_tool_permission(&self, session_id: &str, request_id: &str, allow: bool) -> Result<()> {
        if self.remote_pane(session_id)
            && let Some(remote) = &self.remote
        {
            return remote.respond_to_tool_permission(session_id.into(), request_id.into(), allow).await;
        }
        self.manager.respond_to_tool_permission(session_id, request_id, allow)
    }

    /// `AnswerQuestionnaire`.
    pub async fn answer_questionnaire(
        &self,
        session_id: &str,
        request_id: &str,
        answers: &IndexMap<String, UserQuestionAnswer>,
    ) -> Result<()> {
        if self.remote_pane(session_id)
            && let Some(remote) = &self.remote
        {
            return remote.answer_questions(session_id.into(), request_id.into(), answers.clone()).await;
        }
        self.manager.answer_questionnaire(session_id, request_id, answers)
    }

    /// `AnswerPlan`: None for a remote pane (its record comes back as the host's event).
    pub async fn answer_plan(&self, session_id: &str, request_id: &str, decision: &PlanDecision) -> Result<Option<PlanRecord>> {
        if self.remote_pane(session_id)
            && let Some(remote) = &self.remote
        {
            remote.answer_plan(session_id.into(), request_id.into(), decision.clone()).await?;
            return Ok(None);
        }
        self.manager.answer_plan(session_id, request_id, decision).map(Some)
    }

    /// `TrySteerAsync`: a notice handed to the pane's running local Claude turn, recorded as the user's line.
    pub fn try_steer(&self, session_id: &str, text: &str) -> bool {
        if self.remote_pane(session_id) || !self.manager.try_steer(session_id, text) {
            return false;
        }
        self.shared.receive(RunEvent::log(session_id, "user", text, Some("claude")));
        true
    }

    /// `DisposeAsync`'s run part: every run stopped, then one more save.
    pub async fn dispose(&self) {
        lock(&self.shared.state).closing = true;
        self.manager.dispose().await;
        self.shared.send(ServiceEvent::SaveNow);
    }
}

/// `PaneTitle.Requested`: a pane whose title follows its requests takes the new one.
fn requested(session: &RunSession, input: &str) -> RunSession {
    let mut next = session.clone();
    if normalize::follows_requests(session)
        && let Some(title) = normalize::shortened(normalize::ask_question(input))
    {
        next.title = Some(title);
    }
    next
}

/// `ResolveLocal` and `StateStore.ResolveLocalAsync`: a local workspace the state has, its folder there.
fn resolve_local(shared: &Shared, id: &str) -> Result<Workspace> {
    let state = lock(&shared.state);
    let workspace =
        state.snapshot.workspace(id).cloned().ok_or_else(|| RunError::Invalid(strings::get("run.error.localWorkspaceNotRegistered")))?;
    if workspace.remote.is_some() {
        return Err(RunError::State(strings::get("run.error.approvedWorkspaceOnly")));
    }
    if !Path::new(workspace.path()).is_dir() {
        return Err(RunError::Io(strings::get("store.error.workspaceFolderNotFound")));
    }
    Ok(workspace)
}
