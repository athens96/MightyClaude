//! C# `RunManager` (RunManager.cs): runs a pane's request — an agent CLI or a shell command — and turns what it says into the pane's
//! events. A run starts (`Begin`), its process is found, launched with its arguments and environment through the platform's
//! `ProcessHost`, read line by line (`ExecuteAsync`), steered (`TrySteer`), answered (permissions, plans, questions) and stopped
//! (`Stop`, 10 s). Its timers are C#'s: the graph published every second, a Claude run's input closed after 120 s of silence once
//! the request answered and no background task runs (`BackgroundIdleClose`), the permission handshake given 15 s.
//!
//! Until their stages: Codex's approval channel (`onRequest`), the ACP agents (opencode, Hermes) and pi's dialogs are W5 — a run
//! of opencode or Hermes, or a Codex run in `onRequest`, is refused with the reason; pi and Antigravity launch and are read through
//! the parser's `ProviderStream` once W5 plugs it in. A sharing host's PowerShell commands are W8. The Agent IO / delegation
//! binding (W7) plugs in through [`RunBinding`]; the file originals (W4) through [`FileOriginals`].

use std::collections::{BTreeMap, HashMap};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use mighty_core::graph::GRAPH_PROVIDERS;
use mighty_core::model::{AgentActivity, LogEntry, Workspace};
use mighty_core::{time, wire};
use mighty_platform::process::{ChildReader, ChildWriter};
use mighty_platform::{BoxFuture, ChildControl};
use serde_json::Value;
use tokio::io::AsyncWriteExt;
use tokio::sync::{mpsc, oneshot, watch};

use crate::activity;
use crate::attachments::StagedAttachments;
use crate::failure;
use crate::graph::{GraphFactory, GraphSink, GraphStart};
use crate::lines::{Lines, Next};
use crate::meta;
use crate::model::{RunEvent, StartRunRequest};
use crate::mods::{Connection, ModBridge, ModMetadata};
use crate::parser::{ImageStore, OutputParser, ParserHooks, maximum_line_bytes};
use crate::permissions::{ChannelHooks, ClaudePermissionChannel, ProviderInput, ToolPermissionRequest};
use crate::phase_models;
use crate::plan_mode::{BackgroundTaskTracker, PlanDecision, TodoProgressTracker, work};
use crate::presentation::strings as copy;
use crate::providers::{self, ProviderCatalog};
use crate::questionnaire::UserQuestionAnswer;
use crate::stream;
use crate::strings;
use crate::{Result, RunError, lock};

pub type Emit = Arc<dyn Fn(RunEvent) + Send + Sync>;
pub type PermissionSink = Arc<dyn Fn(ToolPermissionRequest) + Send + Sync>;
pub type ResolveWorkspace = Arc<dyn Fn(String) -> BoxFuture<'static, Result<Workspace>> + Send + Sync>;
pub type GraphNodes = Arc<dyn Fn(&str, Value) + Send + Sync>;

/// What a pane's Agent IO and delegation binding (C# `AgentIOBinding`, W7) adds to a Claude or Codex run.
pub struct RunBinding {
    pub environment: BTreeMap<String, String>,
    pub arguments: Vec<String>,
    /// Every line the run logs or reads passes it (the pane's token never reaches the transcript).
    pub redact: Arc<dyn Fn(&str) -> String + Send + Sync>,
}

pub type BindingFactory = Arc<dyn Fn(&StartRunRequest, &Workspace) -> Option<RunBinding> + Send + Sync>;

/// Where a Claude run's file originals are kept (C# `FileOriginalStore.Capture`, W4): the pane, the run's graph id, the folder the
/// run works in, and what an Edit, Write or MultiEdit result said (the parser's `FileOriginalHook` arguments).
pub type FileOriginals = Arc<dyn Fn(&str, &str, &str, &str, &str, &Value) + Send + Sync>;

/// `RunManager`'s constructor arguments and settable properties.
#[derive(Clone)]
pub struct RunManagerOptions {
    pub plugin_directory: String,
    pub resolve_workspace: ResolveWorkspace,
    pub emit: Emit,
    /// Set only by a host that can show the approval card: a Claude run then launches with host prompts over stdio.
    pub permission_requested: Option<PermissionSink>,
    pub images: Option<Arc<dyn ImageStore>>,
    pub graph: Option<Arc<dyn GraphFactory>>,
    /// Every graph node of a run as it changes (a sharing host's "graph" events); never after the run's last status.
    pub graph_nodes: Option<GraphNodes>,
    /// After the request's result, how long a silent CLI with no background task may keep stdin (C#: 120 s).
    pub background_idle_close: Duration,
    pub binding: Option<BindingFactory>,
    pub file_originals: Option<FileOriginals>,
}

impl RunManagerOptions {
    pub fn new(plugin_directory: &str, resolve_workspace: ResolveWorkspace, emit: Emit) -> RunManagerOptions {
        RunManagerOptions {
            plugin_directory: plugin_directory.into(),
            resolve_workspace,
            emit,
            permission_requested: None,
            images: None,
            graph: None,
            graph_nodes: None,
            background_idle_close: Duration::from_secs(120),
            binding: None,
            file_originals: None,
        }
    }
}

/// C# `CancellationTokenSource` of one run.
struct Cancel(watch::Sender<bool>);

impl Cancel {
    fn new() -> Cancel {
        Cancel(watch::channel(false).0)
    }
    fn cancel(&self) {
        self.0.send_replace(true);
    }
    fn is_cancelled(&self) -> bool {
        *self.0.borrow()
    }
    async fn cancelled(&self) {
        let mut receiver = self.0.subscribe();
        let _ = receiver.wait_for(|cancelled| *cancelled).await;
    }
}

/// A child's stdin as C# writes it (`Write`, `Flush`, `Close`), in order, from any thread: writes are queued to a task that owns
/// the pipe. A write after the close, or after the pipe broke, fails.
struct InputPipe {
    sender: Mutex<Option<mpsc::UnboundedSender<Vec<u8>>>>,
    broken: Arc<AtomicBool>,
}

impl InputPipe {
    fn new(stdin: Option<ChildWriter>) -> InputPipe {
        let (sender, mut receiver) = mpsc::unbounded_channel::<Vec<u8>>();
        let broken = Arc::new(AtomicBool::new(false));
        let flag = broken.clone();
        tokio::spawn(async move {
            let Some(mut stdin) = stdin else { return };
            while let Some(bytes) = receiver.recv().await {
                if stdin.write_all(&bytes).await.is_err() || stdin.flush().await.is_err() {
                    flag.store(true, Ordering::SeqCst);
                    break;
                }
            }
            let _ = stdin.shutdown().await;
        });
        InputPipe { sender: Mutex::new(Some(sender)), broken }
    }

    fn write(&self, text: &str) -> std::io::Result<()> {
        if self.broken.load(Ordering::SeqCst) {
            return Err(std::io::ErrorKind::BrokenPipe.into());
        }
        match lock(&self.sender).as_ref() {
            Some(sender) => sender.send(text.as_bytes().to_vec()).map_err(|_| std::io::ErrorKind::BrokenPipe.into()),
            None => Err(std::io::ErrorKind::BrokenPipe.into()),
        }
    }

    /// `Input.Close()`: what was written still reaches the child, then its stdin ends.
    fn close(&self) {
        lock(&self.sender).take();
    }
}

type SharedSink = Arc<Mutex<Box<dyn GraphSink>>>;
type Redact = Arc<dyn Fn(&str) -> String + Send + Sync>;

/// One run (C# `RunManager.Run`).
struct Run {
    request: StartRunRequest,
    cancel: Cancel,
    accepted: Mutex<Option<oneshot::Sender<std::result::Result<(), RunError>>>>,
    child: Mutex<Option<Arc<dyn ChildControl>>>,
    input: Mutex<Option<Arc<InputPipe>>>,
    input_gate: Mutex<()>,
    permissions: Mutex<Option<Arc<ClaudePermissionChannel>>>,
    parser: Mutex<Option<Arc<OutputParser>>>,
    tracker: Mutex<Option<SharedSink>>,
    redact: Mutex<Option<Redact>>,
    turn_reopened: Mutex<Option<Arc<dyn Fn() + Send + Sync>>>,
    /// The folder the run works in, once resolved.
    workspace_root: Mutex<Option<String>>,
    bytes: AtomicUsize,
    truncated: AtomicBool,
    activity_id: String,
    activity_bytes: AtomicUsize,
    finalizing: AtomicBool,
    finished: AtomicBool,
    plan_cancelled: AtomicBool,
    turn_result_seen: AtomicBool,
    protocol_failed: AtomicBool,
    authentication_failure: AtomicBool,
    authentication_failure_text: Mutex<Option<String>>,
    cli_state: Mutex<Option<String>>,
    last_output: Mutex<Instant>,
    node_gate: Mutex<()>,
    done: watch::Sender<bool>,
}

impl Run {
    fn redacted(&self, text: &str) -> String {
        match lock(&self.redact).as_ref() {
            Some(redact) => redact(text),
            None => text.to_string(),
        }
    }

    fn touch(&self) {
        *lock(&self.last_output) = Instant::now();
    }

    fn channel(&self) -> Option<Arc<ClaudePermissionChannel>> {
        lock(&self.permissions).clone()
    }
}

struct Inner {
    options: RunManagerOptions,
    providers: Arc<ProviderCatalog>,
    runs: Mutex<HashMap<String, Arc<Run>>>,
    disposed: Mutex<bool>,
    bridge: ModBridge,
}

/// C# `RunManager`.
#[derive(Clone)]
pub struct RunManager {
    inner: Arc<Inner>,
}

/// UTF-16 chunks of at most `units`, a pair cut in two read as two U+FFFD as C#'s `Substring` and `Clean` make it.
fn utf16_chunks(text: &str, units: usize) -> Vec<String> {
    let mut chunks = Vec::new();
    let mut current = String::new();
    let mut length = 0;
    for c in text.chars() {
        let width = c.len_utf16();
        if length + width > units {
            if width == 2 && length + 1 == units {
                current.push('\u{FFFD}');
                chunks.push(std::mem::take(&mut current));
                current.push('\u{FFFD}');
                length = 1;
                continue;
            }
            chunks.push(std::mem::take(&mut current));
            length = 0;
        }
        current.push(c);
        length += width;
    }
    if !current.is_empty() {
        chunks.push(current);
    }
    chunks
}

impl RunManager {
    pub fn new(providers: Arc<ProviderCatalog>, options: RunManagerOptions) -> RunManager {
        RunManager {
            inner: Arc::new(Inner {
                options,
                providers,
                runs: Mutex::new(HashMap::new()),
                disposed: Mutex::new(false),
                bridge: ModBridge::new(),
            }),
        }
    }

    pub fn providers(&self) -> Arc<ProviderCatalog> {
        self.inner.providers.clone()
    }

    pub fn is_running(&self, id: &str) -> bool {
        lock(&self.inner.runs).contains_key(id)
    }

    /// `StartAsync`: the run begins; a request with attachments returns once its process is up (or why it never got there).
    pub async fn start(&self, request: StartRunRequest) -> Result<()> {
        self.begin(request, false).await
    }

    /// `StartReachedAsync`: returns once the run's process is up with its input open, or why it never got that far.
    pub async fn start_reached(&self, request: StartRunRequest) -> Result<()> {
        self.begin(request, true).await
    }

    async fn begin(&self, value: StartRunRequest, awaited: bool) -> Result<()> {
        let request = value.validate()?;
        if request.provider == "codex" && request.settings().permission_mode == "onRequest" {
            // CodexApprovalChannel is W5: until then a run that needs it has no surface to answer it, as C# says without one.
            return Err(RunError::State("Codex approval requests require a local approval surface.".into()));
        }
        let receiver;
        {
            let disposed = lock(&self.inner.disposed);
            if *disposed {
                return Err(RunError::State("the run manager is closed".into()));
            }
            let mut runs = lock(&self.inner.runs);
            if runs.len() >= 16 {
                return Err(RunError::State(strings::get("run.error.tooManyConcurrent")));
            }
            if runs.contains_key(&request.session_id) {
                return Err(RunError::State(strings::get("run.error.alreadyRunning")));
            }
            let waits = awaited || request.attachment_list().is_some();
            let (sender, accepted) = oneshot::channel();
            receiver = waits.then_some(accepted);
            let run = Arc::new(Run {
                request: request.clone(),
                cancel: Cancel::new(),
                accepted: Mutex::new(waits.then_some(sender)),
                child: Mutex::new(None),
                input: Mutex::new(None),
                input_gate: Mutex::new(()),
                permissions: Mutex::new(None),
                parser: Mutex::new(None),
                tracker: Mutex::new(None),
                redact: Mutex::new(None),
                turn_reopened: Mutex::new(None),
                workspace_root: Mutex::new(None),
                bytes: AtomicUsize::new(0),
                truncated: AtomicBool::new(false),
                activity_id: wire::new_id(),
                activity_bytes: AtomicUsize::new(0),
                finalizing: AtomicBool::new(false),
                finished: AtomicBool::new(false),
                plan_cancelled: AtomicBool::new(false),
                turn_result_seen: AtomicBool::new(false),
                protocol_failed: AtomicBool::new(false),
                authentication_failure: AtomicBool::new(false),
                authentication_failure_text: Mutex::new(None),
                cli_state: Mutex::new(None),
                last_output: Mutex::new(Instant::now()),
                node_gate: Mutex::new(()),
                done: watch::channel(false).0,
            });
            runs.insert(request.session_id.clone(), run.clone());
            let inner = self.inner.clone();
            tokio::spawn(async move {
                Exec::run(inner, run).await;
            });
        }
        match receiver {
            Some(accepted) => accepted.await.unwrap_or(Ok(())),
            None => Ok(()),
        }
    }

    fn run(&self, id: &str) -> Option<Arc<Run>> {
        lock(&self.inner.runs).get(id).cloned()
    }

    /// `RespondToToolPermission`: 이번만 허용 / 거부 for one waiting request of one run.
    pub fn respond_to_tool_permission(&self, session_id: &str, request_id: &str, allow: bool) -> Result<()> {
        let Some(run) = self.run(session_id) else { return Err(RunError::State(copy::already_settled())) };
        match run.channel() {
            Some(channel) => channel.respond(request_id, allow),
            None => Err(RunError::State(copy::already_settled())),
        }
    }

    /// `TrySteerAsync`: a follow-up joins a running Claude turn's open stdin (after the request's result it opens a new turn).
    pub fn try_steer(&self, session_id: &str, text: &str) -> bool {
        let Some(run) = self.run(session_id) else { return false };
        if run.finalizing.load(Ordering::SeqCst)
            || run.finished.load(Ordering::SeqCst)
            || run.cancel.is_cancelled()
            || run.request.provider != "claude"
        {
            return false;
        }
        let Some(channel) = run.channel() else { return false };
        let accepted = channel.try_steer(text);
        if accepted {
            run.touch();
            let reopened = lock(&run.turn_reopened).clone();
            if let Some(reopened) = reopened {
                reopened();
            }
            let tracker = lock(&run.tracker).clone();
            if let Some(tracker) = tracker {
                lock(&tracker).steer(&wire::new_id(), text);
            }
        }
        accepted
    }

    /// `AnswerPlan`: one pending ExitPlanMode request answered. A cancel is marked first, so an exit racing it still ends stopped.
    pub fn answer_plan(&self, session_id: &str, request_id: &str, decision: &PlanDecision) -> Result<mighty_core::model::PlanRecord> {
        let settled = || RunError::State(strings::get("plan.error.settled"));
        let run = self.run(session_id).ok_or_else(settled)?;
        if run.finalizing.load(Ordering::SeqCst)
            || run.finished.load(Ordering::SeqCst)
            || run.cancel.is_cancelled()
            || run.request.provider != "claude"
        {
            return Err(settled());
        }
        let channel = run.channel().ok_or_else(settled)?;
        let cancelling = decision.kind == "cancel";
        let parser = lock(&run.parser).clone();
        if cancelling {
            run.plan_cancelled.store(true, Ordering::SeqCst);
            if let Some(parser) = &parser {
                parser.suppress_interrupted_result.store(true, Ordering::SeqCst);
            }
        }
        let answer = channel.answer_plan(request_id, decision);
        if answer.is_err() && cancelling {
            run.plan_cancelled.store(false, Ordering::SeqCst);
            if let Some(parser) = &parser {
                parser.suppress_interrupted_result.store(false, Ordering::SeqCst);
            }
        }
        answer
    }

    /// `AnswerQuestionnaire`: Claude's AskUserQuestion answered.
    pub fn answer_questionnaire(
        &self,
        session_id: &str,
        request_id: &str,
        answers: &indexmap::IndexMap<String, UserQuestionAnswer>,
    ) -> Result<()> {
        let gone = || RunError::State(copy::already_settled());
        let run = self.run(session_id).ok_or_else(gone)?;
        if run.finalizing.load(Ordering::SeqCst)
            || run.finished.load(Ordering::SeqCst)
            || run.cancel.is_cancelled()
            || run.request.provider != "claude"
        {
            return Err(gone());
        }
        run.channel().ok_or_else(gone)?.answer_questions(request_id, answers)
    }

    /// `StopAsync`: the run is cancelled and its process ended; waits up to 10 s for its end.
    pub async fn stop(&self, session_id: &str) -> Result<()> {
        if !wire::identifier(session_id) {
            return Err(RunError::Invalid(strings::get("run.error.invalidId")));
        }
        let Some(run) = self.run(session_id) else { return Ok(()) };
        run.cancel.cancel();
        let child = lock(&run.child).clone();
        if let Some(child) = &child {
            child.kill();
        }
        let mut done = run.done.subscribe();
        if tokio::time::timeout(Duration::from_secs(10), done.wait_for(|d| *d)).await.is_err() {
            if let Some(child) = lock(&run.child).clone() {
                child.kill();
            }
            return Err(RunError::TimedOut(strings::get("run.error.stopTimeout")));
        }
        Ok(())
    }

    /// `DisposeAsync`: every run stopped, the Mods bridge closed.
    pub async fn dispose(&self) {
        let active: Vec<String> = {
            *lock(&self.inner.disposed) = true;
            lock(&self.inner.runs).keys().cloned().collect()
        };
        for id in active {
            let _ = self.stop(&id).await;
        }
        self.inner.bridge.dispose().await;
    }
}

/// What one run's task works with (the closures of C#'s `ExecuteAsync`).
struct Exec {
    inner: Arc<Inner>,
    run: Arc<Run>,
    parser: Arc<OutputParser>,
    tracker: Option<SharedSink>,
    graph_dirty: Arc<AtomicBool>,
    graph_gate: Mutex<()>,
    graph_stop: Cancel,
    todos: Mutex<TodoProgressTracker>,
    background: Mutex<BackgroundTaskTracker>,
    plan_gate: Mutex<()>,
    todo_run_id: Option<String>,
    emit: Emit,
}

enum Failure {
    Cancelled,
    Error(String),
}

impl From<RunError> for Failure {
    fn from(error: RunError) -> Failure {
        match error {
            RunError::Cancelled(_) => Failure::Cancelled,
            other => Failure::Error(other.message().to_string()),
        }
    }
}

impl Exec {
    /// `RunManager.Log`: one transcript line of the run (an answer whole, anything else in pieces of 8192), output bounded to 2 MiB.
    fn log(run: &Run, emit: &Emit, kind: &str, text: &str) {
        if run.finished.load(Ordering::SeqCst) || text.is_empty() {
            return;
        }
        let text = run.redacted(text);
        let provider = run.request.provider.as_str();
        if kind == "error" && provider == "gemini" {
            run.authentication_failure.store(failure::antigravity(&text), Ordering::SeqCst);
        } else if kind == "error" && failure::matches(provider, &text) {
            run.authentication_failure.store(true, Ordering::SeqCst);
        }
        if matches!(kind, "output" | "assistant") {
            if run.truncated.load(Ordering::SeqCst) {
                return;
            }
            if run.bytes.fetch_add(text.len(), Ordering::SeqCst) + text.len() > 2 * 1024 * 1024 {
                run.truncated.store(true, Ordering::SeqCst);
                emit(RunEvent::log(&run.request.session_id, "system", &strings::get("run.error.outputTruncated"), Some(provider)));
                return;
            }
        }
        if kind == "assistant" {
            emit(RunEvent::log(&run.request.session_id, kind, &text, Some(provider)));
            return;
        }
        let named = if run.request.kind == "shell" { None } else { Some(provider) };
        for chunk in utf16_chunks(&text, 8192) {
            emit(RunEvent::log(&run.request.session_id, kind, &chunk, named));
        }
    }

    /// `RunManager.Activity`: a tool row (as a transcript line too) or the turn's state; outputs bounded to 2 MiB a run.
    fn activity(run: &Run, emit: &Emit, value: &AgentActivity) {
        if run.finished.load(Ordering::SeqCst) {
            return;
        }
        let Some(mut value) = activity::normalized(value, false) else { return };
        if let Some(output) = &value.output
            && run.activity_bytes.fetch_add(output.len(), Ordering::SeqCst) + output.len() > 2 * 1024 * 1024
        {
            value.output = None;
        }
        if value.kind != "turn" {
            let mut entry = LogEntry::new(value.id.clone(), "system", value.summary.clone(), time::now_o(), Some(value.provider.clone()));
            entry.activity = Some(value.clone());
            emit(RunEvent::entry(&run.request.session_id, entry));
        }
        emit(RunEvent { activity: Some(value), ..RunEvent::new(&run.request.session_id, "activity") });
    }

    /// `HandOver`: one graph node to the host, unless the run's last status is already out.
    fn hand_over(inner: &Inner, run: &Run, node: Value) {
        let _gate = lock(&run.node_gate);
        if run.finished.load(Ordering::SeqCst) {
            return;
        }
        if let Some(nodes) = &inner.options.graph_nodes {
            nodes(&run.request.session_id, node);
        }
    }

    async fn run(inner: Arc<Inner>, run: Arc<Run>) {
        let emit = inner.options.emit.clone();
        let request = run.request.clone();
        let provider = request.provider.clone();
        let graph_dirty = Arc::new(AtomicBool::new(false));
        let tracker: Option<SharedSink> =
            match (&inner.options.graph, request.kind != "shell" && GRAPH_PROVIDERS.contains(&provider.as_str())) {
                (Some(factory), true) => {
                    let (dirty, owner, run_ref) = (graph_dirty.clone(), inner.clone(), run.clone());
                    let run_id = request.graph_run_id.clone().filter(|id| wire::identifier(id)).unwrap_or_else(wire::new_id);
                    let sink = factory.start(GraphStart {
                        run_id,
                        input: request.input.clone(),
                        provider: provider.clone(),
                        model: phase_models::effective_model(&request),
                        effort: request.settings.as_ref().map(|s| s.effort.clone()),
                        node: Arc::new(move |node| {
                            dirty.store(true, Ordering::SeqCst);
                            Exec::hand_over(&owner, &run_ref, node);
                        }),
                    });
                    Some(Arc::new(Mutex::new(sink)))
                }
                _ => None,
            };
        *lock(&run.tracker) = tracker.clone();
        let parser = Arc::new(OutputParser::new(&provider, &run.activity_id, Self::parser_hooks(&inner, &run, tracker.clone())));
        *lock(&run.parser) = Some(parser.clone());
        let todo_run_id = match &tracker {
            Some(tracker) => Some(lock(tracker).run_id()),
            None => request.graph_run_id.clone().filter(|id| wire::identifier(id)),
        };
        let exec = Arc::new(Exec {
            inner: inner.clone(),
            run: run.clone(),
            parser,
            tracker,
            graph_dirty,
            graph_gate: Mutex::new(()),
            graph_stop: Cancel::new(),
            todos: Mutex::new(TodoProgressTracker::new(request.todo_progress.as_ref())),
            background: Mutex::new(BackgroundTaskTracker::new(None)),
            plan_gate: Mutex::new(()),
            todo_run_id,
            emit: emit.clone(),
        });
        let reopened = exec.clone();
        *lock(&run.turn_reopened) = Some(Arc::new(move || {
            let _gate = lock(&reopened.plan_gate);
            if !reopened.run.finished.load(Ordering::SeqCst)
                && let Some(work) = lock(&reopened.background).reopen()
            {
                (reopened.emit)(RunEvent { background: Some(work), ..RunEvent::new(&reopened.run.request.session_id, "background") });
            }
        }));
        let mut mod_connection: Option<Arc<Connection>> = None;
        let mut staged: Option<StagedAttachments> = None;
        let mut graph_task: Option<tokio::task::JoinHandle<()>> = None;
        let outcome = exec.execute(&mut mod_connection, &mut staged, &mut graph_task).await;
        let start_failure = match outcome {
            Ok(()) => None,
            Err(Failure::Cancelled) => {
                exec.finish(if run.protocol_failed.load(Ordering::SeqCst) { "error" } else { "stopped" });
                Some(RunError::Cancelled(strings::get("run.activity.stopped")))
            }
            Err(Failure::Error(message)) => {
                Self::log(&run, &emit, "error", &message);
                exec.finish(if run.cancel.is_cancelled() && !run.protocol_failed.load(Ordering::SeqCst) { "stopped" } else { "error" });
                Some(RunError::State(message))
            }
        };
        // finally
        exec.graph_stop.cancel();
        if let Some(task) = graph_task {
            let _ = task.await;
        }
        *lock(&run.tracker) = None;
        // The run's closures hold the run: let them go with it.
        *lock(&run.turn_reopened) = None;
        *lock(&run.parser) = None;
        let channel = lock(&run.permissions).take();
        if let Some(channel) = channel {
            channel.cancel_all();
        }
        if let Some(connection) = mod_connection {
            connection.dispose();
        }
        let child = lock(&run.child).take();
        if let Some(child) = child {
            let _ = child.close().await;
        }
        if let Some(staged) = staged
            && let Err(error) = staged.dispose()
        {
            let key = if error.kind() == std::io::ErrorKind::PermissionDenied {
                "run.error.attachmentCleanupForbidden"
            } else {
                "run.error.attachmentCleanupFailed"
            };
            Self::log(&run, &emit, "error", &strings::get(key));
        }
        {
            let mut runs = lock(&inner.runs);
            if runs.get(&request.session_id).is_some_and(|current| Arc::ptr_eq(current, &run)) {
                runs.remove(&request.session_id);
            }
        }
        if let Some(sender) = lock(&run.accepted).take() {
            let _ = sender.send(match start_failure {
                Some(error) => Err(error),
                None => Ok(()),
            });
        }
        run.done.send_replace(true);
    }

    fn parser_hooks(inner: &Arc<Inner>, run: &Arc<Run>, tracker: Option<SharedSink>) -> ParserHooks {
        let emit = inner.options.emit.clone();
        let (log_run, log_emit) = (run.clone(), emit.clone());
        let (resume_run, resume_emit) = (run.clone(), emit.clone());
        let (activity_run, activity_emit) = (run.clone(), emit.clone());
        let (usage_run, usage_emit) = (run.clone(), emit.clone());
        let (entry_run, entry_emit) = (run.clone(), emit.clone());
        let file_original: Option<crate::parser::FileOriginalHook> = match (&inner.options.file_originals, &tracker) {
            (Some(originals), Some(tracker)) if run.request.provider == "claude" && run.request.kind == "claude" => {
                let (originals, owner) = (originals.clone(), run.clone());
                let graph_run_id = lock(tracker).run_id();
                Some(Arc::new(move |path: &str, tool: &str, result: &Value| {
                    let root = lock(&owner.workspace_root).clone();
                    if let Some(root) = root {
                        originals(&owner.request.session_id, &graph_run_id, &root, path, tool, result);
                    }
                }))
            }
            _ => None,
        };
        let (graph_activity, graph_images) = (tracker.clone(), tracker);
        ParserHooks {
            log: Some(Arc::new(move |kind, text| Exec::log(&log_run, &log_emit, kind, text))),
            resume: Some(Arc::new(move |id| {
                resume_emit(RunEvent { resume_id: Some(id.to_string()), ..RunEvent::new(&resume_run.request.session_id, "resume") })
            })),
            activity: Some(Arc::new(move |value| Exec::activity(&activity_run, &activity_emit, &value))),
            usage: Some(Arc::new(move |value| {
                if !usage_run.finalizing.load(Ordering::SeqCst) {
                    usage_emit(RunEvent { usage: Some(value), ..RunEvent::new(&usage_run.request.session_id, "usage") });
                }
            })),
            image_entry: Some(Arc::new(move |entry| {
                if !entry_run.finished.load(Ordering::SeqCst) {
                    entry_emit(RunEvent::entry(&entry_run.request.session_id, entry));
                }
            })),
            graph_activity: graph_activity.map(|tracker| {
                Arc::new(move |value: &AgentActivity, tool: Option<&str>| lock(&tracker).activity(value, tool))
                    as crate::parser::GraphActivityHook
            }),
            graph_images: graph_images.map(|tracker| {
                Arc::new(move |entry: &LogEntry, tool: Option<&str>, parent: Option<&str>| lock(&tracker).images(entry, tool, parent))
                    as crate::parser::GraphImagesHook
            }),
            images: if run.request.kind == "shell" { None } else { inner.options.images.clone() },
            file_original,
        }
    }

    fn cancelled(&self) -> std::result::Result<(), Failure> {
        if self.run.cancel.is_cancelled() { Err(Failure::Cancelled) } else { Ok(()) }
    }

    /// `ReadPlanMode`: the checklist, the background tasks and the turn's end of one frame.
    fn read_plan_mode(&self, frame: &Value) {
        let _gate = lock(&self.plan_gate);
        if self.run.finished.load(Ordering::SeqCst) {
            return;
        }
        let session = &self.run.request.session_id;
        if let Some(progress) = lock(&self.todos).consume(frame) {
            let progress = mighty_core::model::TodoProgress { run_id: self.todo_run_id.clone(), ..progress };
            (self.emit)(RunEvent { todos: Some(progress), ..RunEvent::new(session, "todos") });
        }
        if self.run.request.provider != "claude" {
            return;
        }
        let mut background = lock(&self.background);
        if let Some(work) = background.consume(frame) {
            (self.emit)(RunEvent { background: Some(work), ..RunEvent::new(session, "background") });
        }
        if meta::text(frame, "type") == Some("result")
            && !stream::is_notification_result(frame)
            && meta::top_level(frame)
            && let Some(ended) = background.turn_ended()
        {
            (self.emit)(RunEvent { background: Some(ended), ..RunEvent::new(session, "background") });
        }
    }

    fn background_running(&self) -> bool {
        let _gate = lock(&self.plan_gate);
        !work::running(&lock(&self.background).work()).is_empty()
    }

    /// `PublishGraph`: the live graph when it changed since the last one sent; false once the run is ending.
    fn publish_graph(&self) -> bool {
        let _gate = lock(&self.graph_gate);
        if self.run.finalizing.load(Ordering::SeqCst) || self.run.finished.load(Ordering::SeqCst) {
            return false;
        }
        if self.graph_dirty.swap(false, Ordering::SeqCst)
            && let Some(tracker) = &self.tracker
        {
            let mut sink = lock(tracker);
            if let Some(live) = sink.build_run(true) {
                (self.emit)(RunEvent { graph_run: Some(live), ..RunEvent::new(&self.run.request.session_id, "graph_run") });
                for notice in sink.take_notice_runs(true) {
                    (self.emit)(RunEvent { graph_run: Some(notice), ..RunEvent::new(&self.run.request.session_id, "graph_run") });
                }
            }
        }
        true
    }

    /// `Finish`: the run's last events — background work that never reported an end, the rows still open, the turn, the graph,
    /// and its status (with "authentication" when an error is a lost sign-in).
    fn finish(&self, state: &str) {
        let run = &self.run;
        let request = &run.request;
        run.finalizing.store(true, Ordering::SeqCst);
        self.graph_stop.cancel();
        if request.provider == "claude" && request.kind != "shell" {
            let _gate = lock(&self.plan_gate);
            if !run.finished.load(Ordering::SeqCst)
                && let Some(last) = lock(&self.background).finish()
            {
                (self.emit)(RunEvent { background: Some(last), ..RunEvent::new(&request.session_id, "background") });
            }
        }
        let _gate = lock(&self.graph_gate);
        self.parser.flush();
        self.parser.finish_activities(state == "stopped");
        if request.kind != "shell" {
            let summary = match state {
                "completed" => strings::get("run.activity.completed"),
                "stopped" => strings::get("run.activity.stopped"),
                _ => strings::get("run.activity.error"),
            };
            Self::activity(run, &self.emit, &activity::make(&run.activity_id, &request.provider, "turn", state, &summary));
        }
        if let Some(tracker) = &self.tracker {
            let mut sink = lock(tracker);
            sink.finish(state);
            if let Some(graph_run) = sink.build_run(false) {
                (self.emit)(RunEvent { graph_run: Some(graph_run), ..RunEvent::new(&request.session_id, "graph_run") });
            }
            for notice in sink.take_notice_runs(false) {
                (self.emit)(RunEvent { graph_run: Some(notice), ..RunEvent::new(&request.session_id, "graph_run") });
            }
        }
        let _node = lock(&run.node_gate);
        run.finished.store(true, Ordering::SeqCst);
        let authentication = state == "error" && run.authentication_failure.load(Ordering::SeqCst);
        (self.emit)(RunEvent::state(&request.session_id, state, authentication.then_some("authentication")));
    }

    /// `CloseChannel`: every waiting request settles and the CLI's stdin ends.
    fn close_channel(&self) {
        let channel = {
            let _gate = lock(&self.run.input_gate);
            let Some(channel) = lock(&self.run.permissions).take() else { return };
            if let Some(input) = lock(&self.run.input).as_ref() {
                input.close();
            }
            channel
        };
        channel.cancel_all();
    }

    /// `SettleInput`: at a settle point the input closes when the request answered, nothing runs in the background and the CLI is
    /// idle (or never says).
    fn settle_input(&self) {
        let state = lock(&self.run.cli_state).clone();
        if stream::closes_input(self.run.turn_result_seen.load(Ordering::SeqCst), self.background_running(), state.as_deref()) {
            self.close_channel();
        }
    }

    /// `Consume`: one frame for the graph, the plan mode trackers, the sign-in signals and the parser.
    fn consume(&self, line: &str) {
        let line = self.run.redacted(line);
        let request = &self.run.request;
        let mut shown: Option<String> = None;
        if let Ok(frame) = serde_json::from_str::<Value>(&line) {
            shown = if request.provider == "claude" { stream::present_failure(&frame) } else { None };
            let presented = shown.as_deref().and_then(|s| serde_json::from_str::<Value>(s).ok());
            if let Some(tracker) = &self.tracker {
                let (before, after) = {
                    let mut sink = lock(tracker);
                    let before = sink.notice_revision();
                    sink.frame(&request.provider, presented.as_ref().unwrap_or(&frame));
                    (before, sink.notice_revision())
                };
                // A notice turn's run reaches the pane before the turn's lines, which it then takes.
                if before != after {
                    self.publish_graph();
                }
            }
            match request.provider.as_str() {
                "claude" => {
                    if let Some(state) = stream::session_state(&frame) {
                        *lock(&self.run.cli_state) = Some(state);
                    }
                    self.read_plan_mode(&frame);
                    // As on the Mac, only the run's own turn decides: never a sub-agent's frame or a background notification's result.
                    if !stream::is_notification_result(&frame) && meta::top_level(&frame) {
                        let kind = meta::text(&frame, "type");
                        if kind == Some("result") {
                            let text = lock(&self.run.authentication_failure_text).clone();
                            let lost = failure::claude(&frame) || failure::claude_repeating(&frame, text.as_deref());
                            self.run.authentication_failure.store(lost, Ordering::SeqCst);
                        } else if failure::claude(&frame) {
                            self.run.authentication_failure.store(true, Ordering::SeqCst);
                        }
                        if kind == Some("result") || kind == Some("assistant") && meta::text(&frame, "error").is_some_and(|e| !e.is_empty())
                        {
                            *lock(&self.run.authentication_failure_text) = failure::claude_failure_text(&frame);
                        }
                    }
                }
                "opencode" | "hermes" => self.read_plan_mode(&frame),
                "codex" => {
                    self.read_plan_mode(&frame);
                    match meta::text(&frame, "type") {
                        Some("turn.completed") => self.run.authentication_failure.store(false, Ordering::SeqCst),
                        Some(kind @ ("turn.failed" | "error")) => {
                            let message = meta::text(&frame, "message")
                                .or_else(|| meta::text_of(meta::prop(&frame, "error"), "message"))
                                .unwrap_or("");
                            if kind == "turn.failed" {
                                self.run.authentication_failure.store(failure::codex(message), Ordering::SeqCst);
                            } else if failure::codex(message) {
                                self.run.authentication_failure.store(true, Ordering::SeqCst);
                            }
                        }
                        _ => {}
                    }
                }
                _ => {}
            }
        }
        self.parser.parse(shown.as_deref().unwrap_or(&line));
    }

    /// The Mods bridge's callback: the event to the graph and the parser while the run is current.
    fn receive_mod(&self, value: &Value) {
        if self.run.finalizing.load(Ordering::SeqCst) || self.run.cancel.is_cancelled() {
            return;
        }
        let current = lock(&self.inner.runs).get(&self.run.request.session_id).is_some_and(|r| Arc::ptr_eq(r, &self.run));
        if !current {
            return;
        }
        if let Some(tracker) = &self.tracker
            && let Some(event) = ModMetadata::read(value)
        {
            lock(tracker).mod_event(&event);
        }
        self.parser.receive_mod(value);
    }

    async fn execute(
        self: &Arc<Self>,
        mod_connection: &mut Option<Arc<Connection>>,
        staged: &mut Option<StagedAttachments>,
        graph_task: &mut Option<tokio::task::JoinHandle<()>>,
    ) -> std::result::Result<(), Failure> {
        let run = self.run.clone();
        let inner = self.inner.clone();
        let mut request = run.request.clone();
        let provider = request.provider.clone();
        // The request's diagram block appears the moment the run starts, before the workspace, the CLI or the environment is found.
        if self.tracker.is_some() {
            self.graph_dirty.store(true, Ordering::SeqCst);
            self.publish_graph();
        }
        let mut workspace = (inner.options.resolve_workspace)(request.workspace_id.clone()).await?;
        self.cancelled()?;
        if workspace.remote.is_some() {
            return Err(Failure::Error(strings::get("run.error.approvedWorkspaceOnly")));
        }
        if let Some(folder) = &request.working_folder {
            if !Path::new(folder).is_dir() {
                return Err(Failure::Error(strings::get("run.blocked.worktreeMissing")));
            }
            workspace.path = Some(folder.clone());
        }
        let workspace_path = workspace.path().to_string();
        *lock(&self.parser.image_root) = Some(workspace_path.clone());
        // A Claude run's Edit, Write and MultiEdit results name each file's content before the run changed it (FileChanges, W4).
        *lock(&run.workspace_root) = Some(workspace_path.clone());
        let providers = inner.providers.clone();
        tokio::select! {
            _ = providers.refresh_environment(true) => {}
            _ = run.cancel.cancelled() => return Err(Failure::Cancelled),
        }
        let mut environment = providers.quiet_environment();
        if self.tracker.is_some() {
            let exec = self.clone();
            *graph_task = Some(tokio::spawn(async move {
                loop {
                    tokio::select! {
                        _ = tokio::time::sleep(Duration::from_secs(1)) => {}
                        _ = exec.graph_stop.cancelled() => return,
                    }
                    if !exec.publish_graph() {
                        return;
                    }
                }
            }));
        }
        let mut interactive = false;
        let mut input = request.input.clone();
        let launch: mighty_platform::SpawnRequest = if request.kind == "shell" {
            providers.processes().shell_request(&request.input, Path::new(&workspace_path), &environment)
        } else {
            if providers::is_acp(&provider) {
                // AcpChannel (opencode acp, hermes acp) is W5.
                return Err(Failure::Error(format!(
                    "{} runs through the Agent Client Protocol, which this build does not run yet.",
                    providers::name(&provider)
                )));
            }
            let command = tokio::select! {
                command = providers.find(&provider) => command,
                _ = run.cancel.cancelled() => return Err(Failure::Cancelled),
            };
            let command = command
                .ok_or_else(|| Failure::Error(strings::get_with("run.error.cliNotInstalled", &[("name", providers::name(&provider))])))?;
            self.cancelled()?;
            let settings = request.settings();
            if settings.effort != "default" || phase_models::main_effort(&request).is_some() {
                let runtime = providers.get_runtime(false, Some(&workspace_path)).await;
                self.cancelled()?;
                if let Some(catalog) = runtime.providers.iter().find(|p| p.id == provider).map(|p| p.model_catalog.clone()) {
                    request = phase_models::resolve_effort(&request, &catalog);
                    let effort = request.settings().effort;
                    if let Some(tracker) = &self.tracker {
                        lock(tracker).note_effort(&effort);
                    }
                    if let Some(refusal) = providers::effort_refusal(
                        &provider,
                        &phase_models::effective_model(&request),
                        &effort,
                        &catalog,
                        Some(&request.registered_models),
                    ) {
                        return Err(Failure::Error(refusal));
                    }
                }
            }
            if provider == "claude" {
                if !providers::supports_mods(command.version.as_deref()) {
                    return Err(Failure::Error(strings::get("run.error.modsVersionRequired")));
                }
                let plugin = PathBuf::from(&inner.options.plugin_directory).join(".claude-plugin").join("plugin.json");
                if !plugin.is_file() {
                    return Err(Failure::Error(strings::get("run.error.modsResourceMissing")));
                }
                let exec = Arc::downgrade(self);
                let connection = inner
                    .bridge
                    .register(
                        Box::new(move |value| {
                            if let Some(exec) = exec.upgrade() {
                                exec.receive_mod(value);
                            }
                        }),
                        true,
                    )
                    .await?;
                environment.insert("CLAUDE_CODE_ENABLE_FUNCTION_HOOKS".into(), "1".into());
                environment.insert("MIGHTY_CLAUDE_BRIDGE_URL".into(), connection.url.clone());
                environment.insert("MIGHTY_CLAUDE_BRIDGE_TOKEN".into(), connection.token.clone());
                environment.insert("MIGHTY_CLAUDE_RUN_ID".into(), connection.id.clone());
                for key in ["MIGHTY_CLAUDE_ACTIVITY", "MIGHTY_CLAUDE_USAGE", "MIGHTY_CLAUDE_GRAPH"] {
                    environment.insert(key.into(), "1".into());
                }
                let effort = request.settings().effort;
                if effort != "default" {
                    environment.insert("CLAUDE_CODE_EFFORT_LEVEL".into(), effort);
                }
                *mod_connection = Some(connection);
            }
            if let Some(files) = request.attachment_list() {
                let files = files.to_vec();
                let attachments = tokio::task::spawn_blocking(move || StagedAttachments::create(&files))
                    .await
                    .map_err(|e| Failure::Error(e.to_string()))??;
                self.cancelled()?;
                input = attachments.input_for(&request);
                *staged = Some(attachments);
            }
            if provider == "pi" && staged.is_none() {
                input = ProviderInput::pi_startup(&input, &[]);
            }
            let mut arguments = match staged.as_ref() {
                Some(staged) => staged.arguments_for(&request, &inner.options.plugin_directory)?,
                None => providers::arguments(&request, &inner.options.plugin_directory, None)?,
            };
            if matches!(provider.as_str(), "claude" | "codex")
                && let Some(factory) = &inner.options.binding
                && let Some(binding) = factory(&request, &workspace)
            {
                *lock(&run.redact) = Some(binding.redact.clone());
                environment.extend(binding.environment);
                arguments.extend(binding.arguments);
            }
            // Host prompts over stdio only where the approval card exists.
            interactive = inner.options.permission_requested.is_some() && provider == "claude";
            if interactive {
                ProviderInput::host_prompts(&mut arguments);
                // The CLI's authoritative "turn over, no background agent left" signal.
                environment.insert("CLAUDE_CODE_EMIT_SESSION_STATE_EVENTS".into(), "1".into());
            }
            let mut all = command.prefix.clone();
            all.extend(arguments);
            providers
                .processes()
                .cli_request(Path::new(&command.binary), &all, Path::new(&workspace_path), &environment)
                .map_err(|e| Failure::Error(e.to_string()))?
        };
        self.cancelled()?;
        let mut child = providers.processes().spawn(launch).await.map_err(|e| Failure::Error(e.to_string()))?;
        let control = child.control();
        *lock(&run.child) = Some(control.clone());
        self.cancelled()?;
        let pipe = Arc::new(InputPipe::new(child.stdin.take()));
        *lock(&run.input) = Some(pipe.clone());
        if interactive {
            // Fail closed: the prompt itself is written only after a successful handshake, and a failure stops the run.
            let (write_run, write_pipe) = (run.clone(), pipe.clone());
            let emit_run = run.clone();
            let permission = inner.options.permission_requested.clone();
            let parser = self.parser.clone();
            let (warning_run, warning_emit) = (run.clone(), self.emit.clone());
            let (fail_run, fail_emit) = (run.clone(), self.emit.clone());
            let (plan_run, plan_emit) = (run.clone(), self.emit.clone());
            let graph_run_id = self.tracker.as_ref().map(|t| lock(t).run_id());
            let launch_override = request.permission_mode_override.clone();
            let hooks = ChannelHooks {
                write: Arc::new(move |text| {
                    if write_run.finished.load(Ordering::SeqCst) || write_run.finalizing.load(Ordering::SeqCst) {
                        return Err(std::io::Error::other("The Claude input channel is closed."));
                    }
                    write_pipe.write(text)
                }),
                emit: Arc::new(move |value| {
                    if !emit_run.finished.load(Ordering::SeqCst)
                        && let Some(sink) = &permission
                    {
                        sink(value);
                    }
                }),
                activity: Arc::new(move |value, state| parser.permission_activity(value, state)),
                warning: Arc::new(move |message| Exec::log(&warning_run, &warning_emit, "system", message)),
                fail: Arc::new(move |message| {
                    fail_run.protocol_failed.store(true, Ordering::SeqCst);
                    Exec::log(&fail_run, &fail_emit, "error", message);
                    fail_run.cancel.cancel();
                    if let Some(child) = lock(&fail_run.child).clone() {
                        child.kill();
                    }
                }),
                plan: Arc::new(move |record| {
                    if plan_run.finished.load(Ordering::SeqCst) {
                        return;
                    }
                    let record = mighty_core::model::PlanRecord {
                        graph_run_id: graph_run_id.clone().or(record.graph_run_id.clone()),
                        launch_override: launch_override.clone(),
                        ..record
                    };
                    plan_emit(RunEvent { plan: Some(record), ..RunEvent::new(&plan_run.request.session_id, "plan") });
                }),
                clock: Arc::new(mighty_core::time::DateTimeOffset::now),
            };
            let channel = ClaudePermissionChannel::new(
                &request.session_id,
                &ProviderInput::prompt_frame(&input),
                hooks,
                &request.settings().permission_mode,
            );
            *lock(&run.permissions) = Some(Arc::new(channel));
        }
        (self.emit)(RunEvent::state(&request.session_id, "running", None));
        if request.kind != "shell" {
            let summary = strings::get_with("run.activity.running", &[("name", providers::name(&provider))]);
            Self::activity(&run, &self.emit, &activity::make(&run.activity_id, &provider, "turn", "running", &summary));
        }
        if self.tracker.is_some() {
            self.publish_graph();
        }
        let line_limit = if request.kind == "shell" { 1024 * 1024 } else { maximum_line_bytes(&provider) };
        let output = tokio::spawn(Self::pump_output(self.clone(), child.stdout.take(), line_limit));
        let error = tokio::spawn(Self::pump_error(self.clone(), child.stderr.take()));
        if interactive {
            if let Some(channel) = run.channel() {
                channel.start()?;
            }
            // Closes input once the CLI is silent for BackgroundIdleClose after the request's result with no background task running.
            let idle = inner.options.background_idle_close;
            let exec = self.clone();
            tokio::spawn(async move {
                let step = Duration::from_millis((idle.as_millis() as u64 / 4).clamp(50, 1000));
                while exec.run.channel().is_some() && !exec.run.finished.load(Ordering::SeqCst) && !exec.run.cancel.is_cancelled() {
                    tokio::select! {
                        _ = tokio::time::sleep(step) => {}
                        _ = exec.run.cancel.cancelled() => return,
                    }
                    let quiet = lock(&exec.run.last_output).elapsed() >= idle;
                    if exec.run.turn_result_seen.load(Ordering::SeqCst) && !exec.background_running() && quiet {
                        exec.close_channel();
                        return;
                    }
                }
            });
            let exec = self.clone();
            tokio::spawn(async move {
                tokio::select! {
                    _ = tokio::time::sleep(Duration::from_secs(15)) => {}
                    _ = exec.run.cancel.cancelled() => return,
                }
                if !exec.run.finished.load(Ordering::SeqCst)
                    && let Some(channel) = exec.run.channel()
                {
                    channel.initialization_timed_out();
                }
            });
        } else {
            if request.kind != "shell" {
                let first = if provider == "gemini" { ProviderInput::antigravity_user_message(&input) } else { input.clone() };
                let _ = pipe.write(&first);
            }
            // pi reads get_state and the prompt, then keeps reading for a steer (its close after the turn is W5's pi stream).
            if provider != "pi" {
                pipe.close();
            }
        }
        if let Some(sender) = lock(&run.accepted).take() {
            let _ = sender.send(Ok(()));
        }
        let code = tokio::select! {
            code = control.wait() => code.map_err(|e| Failure::Error(e.to_string()))?,
            _ = run.cancel.cancelled() => return Err(Failure::Cancelled),
        };
        self.cancelled()?;
        // Close descendants that inherited stdout after their parent exited.
        control.kill();
        let pumps = async {
            let _ = output.await;
            let _ = error.await;
        };
        tokio::select! {
            ended = tokio::time::timeout(Duration::from_secs(3), pumps) => {
                if ended.is_err() {
                    return Err(Failure::Error("The operation has timed out.".into()));
                }
            }
            _ = run.cancel.cancelled() => return Err(Failure::Cancelled),
        }
        self.parser.flush();
        self.close_channel();
        if mod_connection.as_ref().is_some_and(|c| c.received() == 0) {
            Self::log(&run, &self.emit, "system", &strings::get("run.warning.noModEvents"));
        }
        let state = if run.plan_cancelled.load(Ordering::SeqCst) {
            "stopped"
        } else if code == 0 && !self.parser.failed() && !run.protocol_failed.load(Ordering::SeqCst) {
            "completed"
        } else {
            "error"
        };
        self.finish(state);
        Ok(())
    }

    async fn pump_output(exec: Arc<Exec>, reader: Option<ChildReader>, limit: usize) {
        let mut lines = Lines::new(reader, limit);
        let request = &exec.run.request;
        loop {
            match lines.next().await {
                Next::End => break,
                Next::TooLong => {
                    if request.kind != "shell" && !exec.run.finalizing.load(Ordering::SeqCst) {
                        Self::log(&exec.run, &exec.emit, "system", &strings::get("run.notice.lineTooLong"));
                    }
                }
                Next::Line(line) => {
                    if exec.run.finalizing.load(Ordering::SeqCst) {
                        continue;
                    }
                    if request.kind == "shell" {
                        Self::log(&exec.run, &exec.emit, "output", &line);
                        continue;
                    }
                    if let Some(channel) = exec.run.channel()
                        && channel.receive(&line).is_err()
                    {
                        // A failed write to the CLI ends this pump, as the exception ends C#'s.
                        break;
                    }
                    if request.provider == "gemini" {
                        if !line.trim_start().starts_with('{') {
                            if failure::antigravity(&line) {
                                exec.run.authentication_failure.store(true, Ordering::SeqCst);
                            }
                        } else if failure::antigravity_succeeded(&line) {
                            exec.run.authentication_failure.store(false, Ordering::SeqCst);
                        }
                    }
                    exec.consume(&line);
                    // EOF only after the CLI's own turn result; background tasks still running keep it open longer.
                    exec.run.touch();
                    if exec.run.channel().is_some() && stream::is_settle_point(&line) {
                        if stream::is_turn_result(&line) {
                            exec.run.turn_result_seen.store(true, Ordering::SeqCst);
                        }
                        exec.settle_input();
                    }
                }
            }
        }
    }

    async fn pump_error(exec: Arc<Exec>, reader: Option<ChildReader>) {
        let mut lines = Lines::new(reader, 1024 * 1024);
        loop {
            match lines.next().await {
                Next::End => break,
                Next::TooLong => {}
                Next::Line(line) => {
                    // Only Antigravity reports a lost sign-in on stderr.
                    if exec.run.request.kind != "shell" && exec.run.request.provider == "gemini" && failure::antigravity(&line) {
                        exec.run.authentication_failure.store(true, Ordering::SeqCst);
                    }
                    Self::log(&exec.run, &exec.emit, "output", &line);
                }
            }
        }
    }
}

impl From<Failure> for RunError {
    fn from(failure: Failure) -> RunError {
        match failure {
            Failure::Cancelled => RunError::Cancelled(String::new()),
            Failure::Error(message) => RunError::State(message),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn chunks_cut_as_csharp_substring_cuts() {
        assert_eq!(utf16_chunks("abcdef", 4), ["abcd", "ef"]);
        assert_eq!(utf16_chunks("abc😀d", 4), ["abc\u{FFFD}", "\u{FFFD}d"]);
        assert_eq!(utf16_chunks("ab😀d", 4), ["ab😀", "d"]);
        assert!(utf16_chunks("", 4).is_empty());
    }
}
