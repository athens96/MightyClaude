//! C# `RunManager` (RunManager.cs): runs a pane's request — an agent CLI or a shell command — and turns what it says into the pane's
//! events. A run starts (`Begin`), its process is found, launched with its arguments and environment through the platform's
//! `ProcessHost`, read line by line (`ExecuteAsync`), steered (`TrySteer`), answered (permissions, plans, questions) and stopped
//! (`Stop`, 10 s). Its timers are C#'s: the graph published every second, a Claude run's input closed after 120 s of silence once
//! the request answered and no background task runs (`BackgroundIdleClose`), the permission handshake given 15 s (Claude's, and a
//! Codex `onRequest` run's app-server turn: `CodexApprovalChannel`, whose approvals are the pane's tool-permission requests).
//!
//! pi (`pi --mode rpc`) reads `get_state` and the prompt and keeps its stdin for a steer until its turn ended and it went quiet for
//! a second (`ClosePiWhenIdle`); its extensions' dialogs are the pane's question cards (`PiDialogChannel`), a deadline pi gives one
//! closing its card when it passes.
//!
//! An Antigravity run reads agy's stream through the parser, sends its card before the run's last status (`antigravityPermission`),
//! says a missing conversation once (`AntigravityStderr`) and follows the sub-agents' own transcripts about once a second, as a Codex
//! run's session files are, through the graph's [`GraphSink::read_children`] (C# `ReadChildren`).
//!
//! Until their stages: the ACP agents (opencode, Hermes) are W5 — a run of opencode or Hermes is refused with the reason. A sharing
//! host's PowerShell commands are W8. The Agent IO / delegation binding (W7) plugs in through [`RunBinding`]; the file originals (W4) through
//! [`FileOriginals`].

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
use crate::antigravity::records as antigravity_records;
use crate::attachments::StagedAttachments;
use crate::codex_approval::{self, CodexApprovalChannel, CodexHooks};
use crate::failure;
use crate::graph::{ChildSources, GraphFactory, GraphSink, GraphStart};
use crate::lines::{Lines, Next};
use crate::meta;
use crate::model::{RunEvent, StartRunRequest};
use crate::mods::{Connection, ModBridge, ModMetadata};
use crate::parser::{ImageStore, OutputParser, ParserHooks, maximum_line_bytes};
use crate::permissions::{ChannelHooks, ClaudePermissionChannel, ProviderInput, ToolPermissionRequest};
use crate::phase_models;
use crate::pi_dialogs::{PiDialogChannel, PiDialogHooks};
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

/// Whether pi keeps the record of a session (C# `PiAgent.SessionFile(id, workspacePath, home, environment) is not null`, mighty-history's
/// `pi::session_file`): the session id, the folder the run works in and the CLI's environment.
pub type PiSessions = Arc<dyn Fn(&str, &str, &BTreeMap<String, String>) -> bool + Send + Sync>;

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
    /// pi exits on a session it cannot find (one that never got an answer is not written): a run asked to continue a session this
    /// says pi has no record of starts a new one. None keeps every resume as asked.
    pub pi_sessions: Option<PiSessions>,
    /// agy's settings and the app's ledger the desktop service's Antigravity continuations hold rules in (C# `DesktopService.AntigravityRules`);
    /// the service takes them from here (the runner never reads them). None is the user's own settings without a ledger.
    pub antigravity_rules: Option<crate::antigravity::AntigravityRuleLeases>,
    /// The home an Antigravity run's sub-agents keep their transcripts under (`AntigravityRecords.Brain(home)`); None is the user's
    /// profile (C#'s `Environment.SpecialFolder.UserProfile`). Checks pass a fake one.
    pub antigravity_home: Option<PathBuf>,
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
            pi_sessions: None,
            antigravity_rules: None,
            antigravity_home: None,
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
    /// A Codex run in `onRequest`: the app-server channel its stdio carries.
    codex: Mutex<Option<Arc<CodexApprovalChannel>>>,
    /// A pi run's extension dialogs (its `ask_user` tool among them) as the pane's question card.
    pi_dialogs: Mutex<Option<Arc<PiDialogChannel>>>,
    /// pi's stdin was closed after its turn ended (or never opened): no steer follows.
    pi_input_closed: AtomicBool,
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

    fn codex(&self) -> Option<Arc<CodexApprovalChannel>> {
        lock(&self.codex).clone()
    }

    fn pi_dialogs(&self) -> Option<Arc<PiDialogChannel>> {
        lock(&self.pi_dialogs).clone()
    }

    /// pi's stdin while it is open (C#'s writes under `InputGate`: never after the run's end or the close after its turn).
    fn pi_write(&self, text: &str) {
        let _gate = lock(&self.input_gate);
        if self.finished.load(Ordering::SeqCst) || self.pi_input_closed.load(Ordering::SeqCst) {
            return;
        }
        if let Some(input) = lock(&self.input).as_ref() {
            let _ = input.write(text);
        }
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

/// A run [`RunManager::admit`] registered: what is left of its start (C# `run.Accepted`), awaited when the request waits for its
/// process.
#[must_use]
pub struct Admitted(Option<oneshot::Receiver<std::result::Result<(), RunError>>>);

impl Admitted {
    /// Returns once the run's process is up with its input open, or why it never got that far; at once when the request does not
    /// wait. A run whose task ended without saying (it panicked, or the runtime went away) did not start.
    pub async fn reached(self) -> Result<()> {
        match self.0 {
            Some(accepted) => accepted.await.unwrap_or_else(|_| Err(RunError::State("The run ended before it started.".into()))),
            None => Ok(()),
        }
    }
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
        self.admit(value, awaited)?.reached().await
    }

    /// `Begin`'s synchronous part, as C#'s `StartAsync` runs it before its first await: the request checked, the run registered
    /// (a stop finds it from now on) and its task started. The [`Admitted`] waits for the process when the request asks for that.
    pub fn admit(&self, value: StartRunRequest, awaited: bool) -> Result<Admitted> {
        let request = value.validate()?;
        if request.provider == "codex"
            && request.settings().permission_mode == "onRequest"
            && self.inner.options.permission_requested.is_none()
        {
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
                codex: Mutex::new(None),
                pi_dialogs: Mutex::new(None),
                pi_input_closed: AtomicBool::new(false),
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
        Ok(Admitted(receiver))
    }

    fn run(&self, id: &str) -> Option<Arc<Run>> {
        lock(&self.inner.runs).get(id).cloned()
    }

    /// `RespondToToolPermission`: 이번만 허용 / 거부 for one waiting request of one run.
    pub fn respond_to_tool_permission(&self, session_id: &str, request_id: &str, allow: bool) -> Result<()> {
        let Some(run) = self.run(session_id) else { return Err(RunError::State(copy::already_settled())) };
        if let Some(codex) = run.codex() {
            return codex.respond(request_id, allow);
        }
        if let Some(dialogs) = run.pi_dialogs() {
            return dialogs.respond(request_id, allow);
        }
        match run.channel() {
            Some(channel) => channel.respond(request_id, allow),
            None => Err(RunError::State(copy::already_settled())),
        }
    }

    /// `TrySteerAsync`: a follow-up joins a running Claude turn's open stdin (after the request's result it opens a new turn); pi takes
    /// it as a `steer` command while its turn is open (read after the current tool calls, before the next model call).
    pub fn try_steer(&self, session_id: &str, text: &str) -> bool {
        let Some(run) = self.run(session_id) else { return false };
        if run.request.provider == "pi" {
            let _gate = lock(&run.input_gate);
            let open = lock(&run.parser).as_ref().is_some_and(|parser| parser.pi_turn_open());
            if run.finalizing.load(Ordering::SeqCst)
                || run.finished.load(Ordering::SeqCst)
                || run.cancel.is_cancelled()
                || run.pi_input_closed.load(Ordering::SeqCst)
                || !open
                || lock(&run.child).is_none()
            {
                return false;
            }
            let written = lock(&run.input).as_ref().is_some_and(|input| input.write(&ProviderInput::pi_steer(text)).is_ok());
            if written {
                run.touch();
            }
            return written;
        }
        if run.finalizing.load(Ordering::SeqCst)
            || run.finished.load(Ordering::SeqCst)
            || run.cancel.is_cancelled()
            || run.request.provider != "claude"
        {
            return false;
        }
        let Some(channel) = run.channel() else { return false };
        // The steer opens a new turn: the last turn's result no longer lets the input close, and what the CLI last said of its
        // session is not what it does now. Reset under the channel's lock, right after the write: the steered turn's own result
        // can only be read after it, so it is never lost behind the reset. (C# RunManager.cs TrySteer resets neither.)
        let accepted = channel.try_steer_with(text, || {
            run.turn_result_seen.store(false, Ordering::SeqCst);
            *lock(&run.cli_state) = None;
        });
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

    /// `AnswerQuestionnaire`: Claude's AskUserQuestion, or a pi extension's dialog, answered.
    pub fn answer_questionnaire(
        &self,
        session_id: &str,
        request_id: &str,
        answers: &indexmap::IndexMap<String, UserQuestionAnswer>,
    ) -> Result<()> {
        let gone = || RunError::State(copy::already_settled());
        let run = self.run(session_id).ok_or_else(gone)?;
        if run.finalizing.load(Ordering::SeqCst) || run.finished.load(Ordering::SeqCst) || run.cancel.is_cancelled() {
            return Err(gone());
        }
        if let Some(dialogs) = run.pi_dialogs() {
            return dialogs.answer_questions(request_id, answers);
        }
        if run.request.provider != "claude" {
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

/// What a run's end owes whatever happened to its task (the last steps of C#'s `finally`): a child the task never closed ended, the
/// run off the list, its start answered (Ok unless it failed), and a stop waiting for it told. Dropped when the run's task ends, or
/// while it unwinds.
struct RunEnd {
    inner: Arc<Inner>,
    run: Arc<Run>,
    start_failure: Option<RunError>,
}

impl Drop for RunEnd {
    fn drop(&mut self) {
        let run = &self.run;
        if let Some(child) = lock(&run.child).take() {
            child.kill();
        }
        {
            let mut runs = lock(&self.inner.runs);
            if runs.get(&run.request.session_id).is_some_and(|current| Arc::ptr_eq(current, run)) {
                runs.remove(&run.request.session_id);
            }
        }
        if let Some(sender) = lock(&run.accepted).take() {
            let _ = sender.send(match self.start_failure.take() {
                Some(error) => Err(error),
                None => Ok(()),
            });
        }
        run.done.send_replace(true);
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
    /// Where `ReadChildren` finds the run's sub-agents (set once the run is under way), and whether reading them failed.
    children: Mutex<Children>,
    emit: Emit,
}

/// C#'s `codexHome` (here the CLI's environment the run started with, which says where Codex keeps it), `antigravityBrain` and
/// `watcherDisabled`.
#[derive(Default)]
struct Children {
    environment: Option<BTreeMap<String, String>>,
    antigravity_brain: Option<PathBuf>,
    disabled: bool,
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

    /// The channel failed closed, or a write to the CLI failed: the run ends in an error, said once, and its process is ended.
    fn fail_protocol(run: &Run, emit: &Emit, message: &str) {
        if !run.protocol_failed.swap(true, Ordering::SeqCst) {
            Self::log(run, emit, "error", message);
        }
        run.cancel.cancel();
        if let Some(child) = lock(&run.child).clone() {
            child.kill();
        }
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

    /// The run's task: its body in a task of its own, so a panic in it is caught there; whatever happened, [`RunEnd`] then takes the
    /// run off the list, answers its start and tells a waiting stop.
    async fn run(inner: Arc<Inner>, run: Arc<Run>) {
        let mut end = RunEnd { inner: inner.clone(), run: run.clone(), start_failure: None };
        match tokio::spawn(Self::run_body(inner.clone(), run.clone())).await {
            Ok(start_failure) => end.start_failure = start_failure,
            Err(failed) => {
                let text = match failed.try_into_panic() {
                    Ok(payload) => payload
                        .downcast_ref::<&str>()
                        .map(|text| text.to_string())
                        .or_else(|| payload.downcast_ref::<String>().cloned())
                        .unwrap_or_else(|| "The run failed.".into()),
                    Err(_) => "The run was cancelled.".into(),
                };
                Self::panicked(&inner, &run, &text).await;
                end.start_failure = Some(RunError::State(text));
            }
        }
    }

    /// The run's body panicked (C#'s catch of an unexpected exception): the pane is told why and that the run ended in an error, its
    /// waiting requests settle, its closures let go of it, and its child is closed.
    async fn panicked(inner: &Inner, run: &Run, text: &str) {
        let emit = inner.options.emit.clone();
        run.finalizing.store(true, Ordering::SeqCst);
        run.cancel.cancel();
        if !run.finished.load(Ordering::SeqCst) {
            Self::log(run, &emit, "error", text);
            if run.request.kind != "shell" {
                let summary = strings::get("run.activity.error");
                Self::activity(run, &emit, &activity::make(&run.activity_id, &run.request.provider, "turn", "error", &summary));
            }
            let _node = lock(&run.node_gate);
            run.finished.store(true, Ordering::SeqCst);
            emit(RunEvent::state(&run.request.session_id, "error", None));
        }
        *lock(&run.tracker) = None;
        *lock(&run.turn_reopened) = None;
        *lock(&run.parser) = None;
        let channel = lock(&run.permissions).take();
        if let Some(channel) = channel {
            channel.cancel_all();
        }
        let codex = lock(&run.codex).take();
        if let Some(codex) = codex {
            codex.cancel_all();
        }
        let dialogs = lock(&run.pi_dialogs).take();
        if let Some(dialogs) = dialogs {
            dialogs.cancel_all();
        }
        let child = lock(&run.child).take();
        if let Some(child) = child {
            let _ = child.close().await;
        }
    }

    /// C# `ExecuteAsync`: the run's work and its `finally` but the last steps (those are [`RunEnd`]'s). The start's failure, if any.
    async fn run_body(inner: Arc<Inner>, run: Arc<Run>) -> Option<RunError> {
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
        // pi's dialogs reach the pane's question card where a pane can answer it; a headless run cancels them.
        if provider == "pi"
            && request.kind != "shell"
            && let Some(permission) = inner.options.permission_requested.clone()
        {
            let (write_run, emit_run, timer_run) = (run.clone(), run.clone(), run.clone());
            let row = Arc::downgrade(&parser);
            let (warning_run, warning_emit) = (run.clone(), emit.clone());
            let dialogs = Arc::new(PiDialogChannel::new(
                &request.session_id,
                PiDialogHooks {
                    write: Arc::new(move |text| write_run.pi_write(text)),
                    emit: Arc::new(move |value| {
                        if !emit_run.finished.load(Ordering::SeqCst) {
                            permission(value);
                        }
                    }),
                    activity: Arc::new(move |value, state| {
                        if let Some(parser) = row.upgrade() {
                            parser.permission_activity(value, state);
                        }
                    }),
                    warning: Arc::new(move |message| Exec::log(&warning_run, &warning_emit, "system", message)),
                    // pi answers a dialog with a deadline itself when it passes: the card goes then too.
                    timer: Some(Arc::new(move |id: &str, seconds: f64| {
                        let (run, id) = (timer_run.clone(), id.to_string());
                        tokio::spawn(async move {
                            tokio::time::sleep(Duration::from_secs_f64(seconds.min(86_400.0))).await;
                            if let Some(dialogs) = run.pi_dialogs() {
                                dialogs.timed_out(&id);
                            }
                        });
                    })),
                },
            ));
            *lock(&run.pi_dialogs) = Some(dialogs.clone());
            parser.set_pi_dialogs(Some(dialogs));
        }
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
            children: Mutex::new(Children::default()),
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
        let codex = lock(&run.codex).take();
        if let Some(codex) = codex {
            codex.cancel_all();
        }
        let dialogs = lock(&run.pi_dialogs).take();
        if let Some(dialogs) = dialogs {
            dialogs.cancel_all();
        }
        exec.parser.set_pi_dialogs(None);
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
        start_failure
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
        let answer =
            tracker.clone().map(|tracker| Arc::new(move |text: &str| lock(&tracker).antigravity_answer(text)) as crate::parser::AnswerHook);
        // The card goes out before the run's last status (M/ProcessRunner.swift antigravityPermission).
        let (card_run, card_emit) = (run.clone(), emit.clone());
        let antigravity_permission: crate::parser::AntigravityCardHook = Arc::new(move |card| {
            if !card_run.finished.load(Ordering::SeqCst) && card_run.request.kind == "claude" {
                card_emit(RunEvent {
                    antigravity_permission: Some(card),
                    ..RunEvent::new(&card_run.request.session_id, "antigravityPermission")
                });
            }
        });
        let requested_resume = if run.request.provider == "gemini" { run.request.resume_id.clone() } else { None };
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
            pi_reply: (run.request.provider == "pi").then(|| {
                let reply_run = run.clone();
                Arc::new(move |text: &str| reply_run.pi_write(text)) as crate::parser::PiReplyHook
            }),
            answer,
            antigravity_permission: Some(antigravity_permission),
            requested_resume,
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

    /// `PublishGraph(false)`.
    fn publish_graph(&self) -> bool {
        self.publish_graph_with(false)
    }

    /// `PublishGraph(readChildren)`: the live graph when it changed since the last one sent (the sub-agents' files read first
    /// when asked); false once the run is ending.
    fn publish_graph_with(&self, read_children: bool) -> bool {
        let _gate = lock(&self.graph_gate);
        if self.run.finalizing.load(Ordering::SeqCst) || self.run.finished.load(Ordering::SeqCst) {
            return false;
        }
        if read_children {
            self.read_children(false);
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

    /// `ReadChildren(closing)`: the run's sub-agents read from the CLI's own files through the graph ([`GraphSink::read_children`]):
    /// agy's stream names each sub-agent's conversation but not its work (M/ProcessRunner.swift watchAntigravitySubagents), and Codex's
    /// sub-agents are in its session files. Observation is optional: a file changing under it must never fail the actual CLI turn, so a
    /// failed read stops the reading for the rest of the run.
    fn read_children(&self, closing: bool) {
        let Some(tracker) = &self.tracker else { return };
        let mut children = lock(&self.children);
        if children.disabled {
            return;
        }
        let Some(environment) = &children.environment else { return };
        let sources =
            ChildSources { environment, antigravity_brain: children.antigravity_brain.as_deref(), namespace: &self.run.activity_id };
        let read = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| lock(tracker).read_children(&sources, closing)));
        if read.is_err() {
            children.disabled = true;
        }
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
        self.read_children(true);
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
        // pi's sign-in failure is read from its RPC stream, where a later message or a retry clears it.
        let authentication =
            state == "error" && (run.authentication_failure.load(Ordering::SeqCst) || self.parser.authentication_failure());
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
        let codex_approvals = provider == "codex" && request.settings().permission_mode == "onRequest";
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
        {
            let mut children = lock(&self.children);
            if request.kind != "shell" {
                children.environment = Some(environment.clone());
            }
            if provider == "gemini" {
                let home =
                    inner.options.antigravity_home.clone().or_else(crate::antigravity::permissions::user_profile).unwrap_or_default();
                children.antigravity_brain = Some(antigravity_records::brain(&home));
            }
        }
        if self.tracker.is_some() {
            let exec = self.clone();
            *graph_task = Some(tokio::spawn(async move {
                loop {
                    tokio::select! {
                        _ = tokio::time::sleep(Duration::from_secs(1)) => {}
                        _ = exec.graph_stop.cancelled() => return,
                    }
                    if !exec.publish_graph_with(true) {
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
            if codex_approvals && !providers::supports_codex_approvals(command.version.as_deref()) {
                return Err(Failure::Error("Codex approval requests require stable Codex CLI 0.153.4 or later.".into()));
            }
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
                // Kept before the stop is looked at, so a stop right after the staging still removes the copies.
                input = attachments.input_for(&request);
                *staged = Some(attachments);
                self.cancelled()?;
            }
            if provider == "pi" && staged.is_none() {
                input = ProviderInput::pi_startup(&input, &[]);
            }
            // pi exits on a session it cannot find (one that never got an answer is not written): such a pane starts a new session.
            let mut launch = request.clone();
            if provider == "pi"
                && let (Some(resume), Some(sessions)) = (&request.resume_id, &inner.options.pi_sessions)
                && !sessions(resume, &workspace_path, &environment)
            {
                launch.resume_id = None;
            }
            // An approval run's pictures go in its turn (`localImage`), not as `--image`.
            let mut arguments = match staged.as_ref().filter(|_| !codex_approvals) {
                Some(staged) => staged.arguments_for(&launch, &inner.options.plugin_directory)?,
                None => providers::arguments(&launch, &inner.options.plugin_directory, None)?,
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
                fail: Arc::new(move |message| Exec::fail_protocol(&fail_run, &fail_emit, message)),
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
            // Files staged for Claude made the input a whole stream-json user frame already (`StagedAttachments.InputFor`, the Mac's
            // Attachments.swift `prepare`): it goes as it is; plain text is wrapped in one.
            let prompt = if staged.is_some() { input.clone() } else { ProviderInput::prompt_frame(&input) };
            let channel = ClaudePermissionChannel::new(&request.session_id, &prompt, hooks, &request.settings().permission_mode);
            *lock(&run.permissions) = Some(Arc::new(channel));
        }
        if codex_approvals {
            let images: Vec<String> = staged
                .as_ref()
                .map(|staged| {
                    let pictures = staged.files.iter().filter(|(attachment, _)| attachment.media_type.starts_with("image/"));
                    pictures.map(|(_, path)| path.to_string_lossy().into_owned()).collect()
                })
                .unwrap_or_default();
            let (write_run, write_pipe) = (run.clone(), pipe.clone());
            let consumer = Arc::downgrade(self);
            let emit_run = run.clone();
            let permission = inner.options.permission_requested.clone();
            let parser = self.parser.clone();
            let (warning_run, warning_emit) = (run.clone(), self.emit.clone());
            let (fail_run, fail_emit) = (run.clone(), self.emit.clone());
            let completed_pipe = pipe.clone();
            let hooks = CodexHooks {
                write: Arc::new(move |text| {
                    if !write_run.finished.load(Ordering::SeqCst) {
                        let _ = write_pipe.write(text);
                    }
                }),
                consume: Arc::new(move |line| {
                    if let Some(exec) = consumer.upgrade() {
                        exec.consume(line);
                    }
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
                fail: Arc::new(move |message| Exec::fail_protocol(&fail_run, &fail_emit, message)),
                completed: Arc::new(move || completed_pipe.close()),
            };
            *lock(&run.codex) = Some(Arc::new(CodexApprovalChannel::new(&request, &workspace_path, &input, images, hooks)));
        }
        (self.emit)(RunEvent::state(&request.session_id, "running", None));
        if request.kind != "shell" {
            let summary = strings::get_with("run.activity.running", &[("name", providers::name(&provider))]);
            Self::activity(&run, &self.emit, &activity::make(&run.activity_id, &provider, "turn", "running", &summary));
        }
        if self.tracker.is_some() {
            self.publish_graph();
        }
        let line_limit = if request.kind == "shell" {
            1024 * 1024
        } else if codex_approvals {
            codex_approval::MAXIMUM_FRAME_BYTES
        } else {
            maximum_line_bytes(&provider)
        };
        let output = tokio::spawn(Self::pump_output(self.clone(), child.stdout.take(), line_limit));
        let error = tokio::spawn(Self::pump_error(self.clone(), child.stderr.take()));
        if interactive {
            if let Some(channel) = run.channel() {
                channel.start()?;
            }
            // Closes input once the CLI is silent for BackgroundIdleClose after the request's result with no background task running
            // and the CLI idle (or never saying): a turn it still says runs keeps its input. (C# RunManager.cs's watchdog closes
            // whatever the CLI last said of its session.)
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
                    let state = lock(&exec.run.cli_state).clone();
                    if quiet
                        && stream::closes_input(
                            exec.run.turn_result_seen.load(Ordering::SeqCst),
                            exec.background_running(),
                            state.as_deref(),
                        )
                    {
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
        } else if let Some(codex) = run.codex() {
            // The channel writes the request itself, once its thread and turn are open; a turn not started in 15 s fails closed.
            codex.start();
            let exec = self.clone();
            tokio::spawn(async move {
                tokio::select! {
                    _ = tokio::time::sleep(Duration::from_secs(15)) => {}
                    _ = exec.run.cancel.cancelled() => return,
                }
                if !exec.run.finished.load(Ordering::SeqCst)
                    && let Some(codex) = exec.run.codex()
                {
                    codex.initialization_timed_out();
                }
            });
        } else {
            if request.kind != "shell" {
                let first = if provider == "gemini" { ProviderInput::antigravity_user_message(&input) } else { input.clone() };
                let _ = pipe.write(&first);
            }
            // pi reads get_state and the prompt, then keeps reading for a steer: stdin stays open until its turn ended and went quiet.
            if provider == "pi" {
                tokio::spawn(Self::close_pi_when_idle(self.clone()));
            } else {
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
        let codex = run.codex();
        if let Some(codex) = &codex {
            codex.flush();
        }
        self.close_channel();
        if mod_connection.as_ref().is_some_and(|c| c.received() == 0) {
            Self::log(&run, &self.emit, "system", &strings::get("run.warning.noModEvents"));
        }
        let state = if run.plan_cancelled.load(Ordering::SeqCst) {
            "stopped"
        } else if code == 0
            && !self.parser.failed()
            && !run.protocol_failed.load(Ordering::SeqCst)
            && (!codex_approvals || codex.is_some_and(|c| c.turn_completed()))
        {
            "completed"
        } else {
            "error"
        };
        self.finish(state);
        Ok(())
    }

    /// `ClosePiWhenIdle`: closes a pi run's stdin (pi then exits) once its turn is over and it has been silent for a second:
    /// compaction and an automatic retry come right after `agent_end`.
    async fn close_pi_when_idle(exec: Arc<Exec>) {
        let run = &exec.run;
        while !run.finished.load(Ordering::SeqCst) && !run.cancel.is_cancelled() {
            tokio::time::sleep(Duration::from_millis(250)).await;
            if !exec.parser.pi_turn_ended() || exec.parser.pi_turn_open() || lock(&run.last_output).elapsed() < Duration::from_secs(1) {
                continue;
            }
            let _gate = lock(&run.input_gate);
            run.pi_input_closed.store(true, Ordering::SeqCst);
            if let Some(input) = lock(&run.input).as_ref() {
                input.close();
            }
            return;
        }
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
                    if let Some(codex) = exec.run.codex() {
                        codex.receive(&line);
                        continue;
                    }
                    if let Some(channel) = exec.run.channel()
                        && let Err(error) = channel.receive(&line)
                    {
                        // A failed write to the CLI (the prompt, or an answer) ends this pump and the run in an error, as the exception
                        // that ends C#'s pump ends its run.
                        Self::fail_protocol(&exec.run, &exec.emit, error.message());
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
                    // Only Antigravity reports a lost sign-in on stderr ("Error: authentication required. Run 'agy' to log in, then
                    // retry."), and that the conversation it was asked to continue is gone.
                    if exec.run.request.kind != "shell" && exec.run.request.provider == "gemini" {
                        if failure::antigravity(&line) {
                            exec.run.authentication_failure.store(true, Ordering::SeqCst);
                        }
                        exec.parser.antigravity_stderr(&line);
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
