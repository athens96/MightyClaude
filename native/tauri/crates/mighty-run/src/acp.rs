//! C# `AcpChannel` (AcpChannel.cs): one Agent Client Protocol run (https://agentclientprotocol.com, M/MightyCore/ACPChannel.swift) —
//! JSON-RPC 2.0 over the agent's stdio, one JSON object per line — for opencode (`opencode acp`) and Hermes (`hermes acp`). The app is
//! the client: `initialize`, then `session/new` (or `session/resume` / `session/load` of the pane's session), the pane's settings as
//! `session/set_config_option`, then one `session/prompt`. What the agent streams (`session/update`) reaches the parser as `acp.*`
//! events (the parser's `acp` part); a `session/request_permission` becomes the pane's approval card, and only the user's single-call
//! answer leaves the channel. A question (an `elicitation/create` form: opencode's `question` tool, Hermes's clarify; or Hermes's
//! clarify as an approval with one `allow_once` option per choice) becomes the pane's question card ([`crate::questionnaire::agent`]).
//! Nothing here is one agent's own: opencode and Hermes differ only in their argv, how the permission mode reaches them and their
//! settings' ids ([`agents`]).
//!
//! C# runs every callback under the channel's re-entrant lock. Here the frames to the agent are written under the lock, in order; every
//! other callback (`consume`, `emit`, `activity`, `warning`, `fail`, `completed`) is queued under it and delivered once it is released,
//! one at a time and in the order C# makes them, by whichever caller finds the queue undelivered — so a callback may answer at once,
//! as C#'s may. A frame nested deeper than serde_json's 128 levels (C# reads 256) is read as a plain line.

use std::collections::{HashMap, HashSet, VecDeque};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};

use indexmap::IndexMap;
use mighty_core::{time, wire};
use serde_json::{Map, Value, json};

use crate::activity::{self, clean};
use crate::permissions::ToolPermissionRequest;
use crate::presentation::strings as copy;
use crate::questionnaire::{UserQuestionAnswer, agent};
use crate::strings;
use crate::text_json;
use crate::{Result, RunError, lock};

pub mod accounts;
pub mod agents;
pub mod elicitation;

use elicitation::AcpElicitationForm;

pub const MAXIMUM_FRAME_BYTES: usize = 32 * 1024 * 1024;
pub const MAXIMUM_INPUT_BYTES: usize = 65_536;
pub const MAXIMUM_PENDING: usize = 16;

/// `ClientCapabilities`: no file system and no terminal, so the agent uses its own tools, and form elicitation, so its questions arrive as
/// `elicitation/create`.
pub fn client_capabilities() -> Value {
    json!({"fs": {"readTextFile": false, "writeTextFile": false}, "terminal": false, "elicitation": {"form": {}}})
}

/// `PromptText`: the prompt's text block — the request (or the attachments' default words), each attached copy that is not a picture,
/// and the words for the agent that follow the user's request (Hermes's clarify note), a blank line apart.
pub fn prompt_text(input: &str, references: &[String], note: Option<&str>) -> String {
    let first = if input.trim().is_empty() { strings::get("attachments.prompt.default") } else { input.to_string() };
    std::iter::once(first).chain(references.iter().cloned()).chain(note.map(str::to_string)).collect::<Vec<_>>().join("\n\n")
}

pub type TextHook = Arc<dyn Fn(&str) + Send + Sync>;
pub type EmitHook = Arc<dyn Fn(ToolPermissionRequest) + Send + Sync>;
pub type ActivityHook = Arc<dyn Fn(&ToolPermissionRequest, &str) + Send + Sync>;
pub type CompletedHook = Arc<dyn Fn() + Send + Sync>;
pub type AdmitHook = Arc<dyn Fn(&Value) -> Option<String> + Send + Sync>;

/// The callbacks a channel reports through (C#'s constructor arguments).
#[derive(Clone)]
pub struct AcpHooks {
    /// One frame, its newline included, to the agent's stdin.
    pub write: TextHook,
    /// One `acp.*` event, as a JSON line, for the parser, the usage reader and the graph.
    pub consume: TextHook,
    /// A card waiting, or settled.
    pub emit: EmitHook,
    /// The tool row's state for a card (the parser owns the row).
    pub activity: ActivityHook,
    pub warning: TextHook,
    /// The channel failed closed (the run stops).
    pub fail: TextHook,
    /// The prompt was answered, or failed: the runner ends the agent's input.
    pub completed: CompletedHook,
    /// Reads the agent's `initialize` answer before a session is opened: a message stops the run there as a missing sign-in (Hermes
    /// names no provider it could use).
    pub admit: Option<AdmitHook>,
}

impl AcpHooks {
    /// Hooks that do nothing but write.
    pub fn writing(write: TextHook) -> AcpHooks {
        AcpHooks {
            write,
            consume: Arc::new(|_| {}),
            emit: Arc::new(|_| {}),
            activity: Arc::new(|_, _| {}),
            warning: Arc::new(|_| {}),
            fail: Arc::new(|_| {}),
            completed: Arc::new(|| {}),
            admit: None,
        }
    }
}

/// What a channel's run is (C#'s other constructor arguments).
#[derive(Debug, Clone, Default)]
pub struct AcpRequest {
    /// The pane (cards carry the pane as their run, as Claude's and Codex's do).
    pub run_id: String,
    pub resume_id: Option<String>,
    pub workspace_path: String,
    /// The prompt's text block ([`prompt_text`]).
    pub prompt_text: String,
    /// Pictures sent as image blocks: (media type, base64).
    pub images: Vec<(String, String)>,
    /// `session/set_config_option` values sent before the prompt, in order (model, effort, mode).
    pub config_values: Vec<(String, String)>,
    /// The agent reads a typed answer in place of a form's choice (Hermes's clarify).
    pub free_choices: bool,
}

/// C# `AcpCancellation`: a turn's `session/cancel` frame behind a lock, so the stop (sent before the process is ended) can read it once.
#[derive(Debug, Default)]
pub struct AcpCancellation(Mutex<Option<String>>);

impl AcpCancellation {
    pub fn set(&self, value: Option<String>) {
        *lock(&self.0) = value.map(|frame| frame + "\n");
    }

    /// `Take`: the frame, once; a second stop does not send it again.
    pub fn take(&self) -> Option<String> {
        lock(&self.0).take()
    }
}

/// How a question's answer travels: an elicitation's `content`, or the option of the picked choice.
struct Question {
    form: Option<AcpElicitationForm>,
    choices: Option<HashMap<String, String>>,
}

struct Pending {
    rpc_id: Value,
    allow_option: Option<String>,
    reject_option: Option<String>,
    display: ToolPermissionRequest,
    asked: Option<Question>,
    /// False for a form without a tool call of its own (Hermes's): no activity row to mark.
    row: bool,
}

/// What a channel tells its owner, after its lock is released.
enum Effect {
    Consume(String),
    Emit(ToolPermissionRequest),
    Activity(ToolPermissionRequest, &'static str),
    Warning(String),
    Fail(String),
    Completed,
}

#[derive(Default)]
struct State {
    pending: IndexMap<String, Pending>,
    seen: HashSet<String>,
    rpc: HashMap<String, String>,
    queued_config: VecDeque<(String, String)>,
    config_options: Vec<Value>,
    sequence: u64,
    can_resume: bool,
    can_load: bool,
    replaying: bool,
    closed: bool,
    started: bool,
    /// The tool call the agent opened last and has not settled: a form that names no call of its own (Hermes's clarify) is its question.
    open_call: Option<String>,
    session_id: Option<String>,
    initialized: bool,
    ready: bool,
    turn_completed: bool,
    failed: bool,
}

/// C# `AcpChannel`.
pub struct AcpChannel {
    request: AcpRequest,
    hooks: AcpHooks,
    state: Mutex<State>,
    queue: Mutex<VecDeque<Effect>>,
    delivering: AtomicBool,
    /// `Cancellation`: the `session/cancel` notification of the turn in flight; an agent whose turn outlives its ACP process (Hermes's
    /// gateway owns it) is told to stop before the process is ended.
    pub cancellation: AcpCancellation,
}

/// Lets the queue be delivered again when a callback unwinds.
struct Delivering<'a>(&'a AtomicBool);

impl Drop for Delivering<'_> {
    fn drop(&mut self) {
        self.0.store(false, Ordering::SeqCst);
    }
}

fn text(node: Option<&Value>) -> Option<&str> {
    node.and_then(Value::as_str)
}

impl AcpChannel {
    pub fn new(request: AcpRequest, hooks: AcpHooks) -> AcpChannel {
        AcpChannel {
            request,
            hooks,
            state: Mutex::new(State::default()),
            queue: Mutex::new(VecDeque::new()),
            delivering: AtomicBool::new(false),
            cancellation: AcpCancellation::default(),
        }
    }

    pub fn session_id(&self) -> Option<String> {
        lock(&self.state).session_id.clone()
    }
    pub fn initialized(&self) -> bool {
        lock(&self.state).initialized
    }
    pub fn ready(&self) -> bool {
        lock(&self.state).ready
    }
    pub fn turn_completed(&self) -> bool {
        lock(&self.state).turn_completed
    }
    pub fn failed(&self) -> bool {
        lock(&self.state).failed
    }

    /// One call: its state change and its frames under the lock, then its callbacks.
    fn with<T>(&self, body: impl FnOnce(&Self, &mut State, &mut Vec<Effect>) -> T) -> T {
        let result = {
            let mut state = lock(&self.state);
            let mut effects = Vec::new();
            let result = body(self, &mut state, &mut effects);
            lock(&self.queue).extend(effects);
            result
        };
        self.deliver();
        result
    }

    fn next_effect(&self) -> Option<Effect> {
        lock(&self.queue).pop_front()
    }

    fn deliver(&self) {
        loop {
            if self.delivering.swap(true, Ordering::SeqCst) {
                return;
            }
            {
                let _delivering = Delivering(&self.delivering);
                while let Some(effect) = self.next_effect() {
                    match effect {
                        Effect::Consume(line) => (self.hooks.consume)(&line),
                        Effect::Emit(value) => (self.hooks.emit)(value),
                        Effect::Activity(value, state) => (self.hooks.activity)(&value, state),
                        Effect::Warning(message) => (self.hooks.warning)(&message),
                        Effect::Fail(message) => (self.hooks.fail)(&message),
                        Effect::Completed => (self.hooks.completed)(),
                    }
                }
            }
            // An effect queued after the last look and before the flag went down is delivered by this caller.
            if lock(&self.queue).is_empty() {
                return;
            }
        }
    }

    /// `Start`: `initialize`.
    pub fn start(&self) {
        self.with(|this, state, _| {
            if state.started || state.closed {
                return;
            }
            state.started = true;
            let parameters = json!({
                "protocolVersion": 1,
                "clientCapabilities": client_capabilities(),
                "clientInfo": {"name": "mighty-code", "title": "Mighty Code", "version": "1.0"},
            });
            this.call(state, "initialize", parameters);
        })
    }

    /// `InitializationTimedOut`.
    pub fn initialization_timed_out(&self) {
        self.with(|this, state, effects| {
            if !state.closed && !state.ready {
                this.fail_closed(state, effects, strings::get("acp.channel.initTimeout"));
            }
        })
    }

    /// `FrameTooLarge`.
    pub fn frame_too_large(&self) {
        self.with(|this, state, effects| this.fail_closed(state, effects, strings::get("acp.channel.frameTooLarge")))
    }

    /// `Flush`: EOF before the prompt's answer is not success.
    pub fn flush(&self) {
        self.with(|this, state, effects| {
            if !state.closed {
                this.fail_closed(state, effects, strings::get("acp.channel.closedEarly"));
            }
        })
    }

    /// `Receive`: one line of the agent's standard output.
    pub fn receive(&self, line: &str) {
        self.with(|this, state, effects| {
            if state.closed {
                return;
            }
            let node = if line.trim_start().starts_with('{') { serde_json::from_str::<Value>(line).ok() } else { None };
            let Some(Value::Object(frame)) = node else {
                // An agent may print a plain line (a warning) before or beside its frames: shown, not fatal.
                let shown = clean(Some(&agents::utf16_prefix(line, 4_096)), 4_096, false);
                if !shown.is_empty() {
                    this.event(effects, json!({"type": "acp.output", "text": shown}));
                }
                return;
            };
            if let Some(method) = text(frame.get("method")) {
                let empty = Map::new();
                let parameters = match frame.get("params") {
                    Some(Value::Object(parameters)) => parameters,
                    _ => &empty,
                };
                match frame.get("id") {
                    Some(id) if !id.is_null() => this.agent_request(state, effects, id, method, parameters),
                    _ => this.notification(state, effects, method, parameters),
                }
                return;
            }
            let Some(called) = key(frame.get("id")).and_then(|key| state.rpc.remove(&key)) else { return };
            if let Some(Value::Object(error)) = frame.get("error") {
                this.failure(state, effects, &called, error);
                return;
            }
            let empty = Map::new();
            let result = match frame.get("result") {
                Some(Value::Object(result)) => result,
                _ => &empty,
            };
            this.answered(state, effects, &called, result);
        })
    }

    fn answered(&self, state: &mut State, effects: &mut Vec<Effect>, method: &str, result: &Map<String, Value>) {
        match method {
            "initialize" => {
                state.initialized = true;
                let capabilities = result.get("agentCapabilities").and_then(Value::as_object);
                state.can_load = matches!(capabilities.and_then(|c| c.get("loadSession")), Some(Value::Bool(true)));
                state.can_resume = capabilities
                    .and_then(|c| c.get("sessionCapabilities"))
                    .and_then(Value::as_object)
                    .and_then(|s| s.get("resume"))
                    .is_some_and(|resume| !resume.is_null());
                if let Some(admit) = &self.hooks.admit
                    && let Some(refusal) = admit(&Value::Object(result.clone()))
                {
                    self.event(effects, json!({"type": "acp.error", "method": method, "code": -32000, "message": refusal, "signIn": true}));
                    state.failed = true;
                    Self::cancel_all_locked(state, effects);
                    effects.push(Effect::Completed);
                    return;
                }
                self.open_session(state);
            }
            "session/new" => match text(result.get("sessionId")).filter(|id| wire::session_identifier(id)) {
                Some(id) => self.session_opened(state, effects, id.to_string(), result),
                None => self.fail_closed(state, effects, strings::get("acp.channel.badSession")),
            },
            "session/resume" | "session/load" => {
                state.replaying = false;
                match self.request.resume_id.clone() {
                    Some(resume) => self.session_opened(state, effects, resume, result),
                    None => self.fail_closed(state, effects, strings::get("acp.channel.badSession")),
                }
            }
            "session/set_config_option" => {
                if let Some(Value::Array(options)) = result.get("configOptions") {
                    state.config_options = options.clone();
                }
                self.next_config(state, effects);
            }
            "session/prompt" => {
                let Some(session) = state.session_id.clone() else { return };
                let mut value = json!({
                    "type": "acp.prompt_result",
                    "sessionId": session,
                    "stopReason": text(result.get("stopReason")).unwrap_or("end_turn"),
                });
                if let Some(usage) = result.get("usage").filter(|u| !u.is_null()) {
                    value["usage"] = usage.clone();
                }
                self.event(effects, value);
                state.turn_completed = true;
                self.cancellation.set(None);
                Self::cancel_all_locked(state, effects);
                effects.push(Effect::Completed);
            }
            _ => {}
        }
    }

    fn open_session(&self, state: &mut State) {
        let mut parameters = json!({"cwd": self.request.workspace_path, "mcpServers": []});
        match &self.request.resume_id {
            Some(resume) if state.can_resume || state.can_load => {
                parameters["sessionId"] = Value::String(resume.clone());
                if state.can_resume {
                    self.call(state, "session/resume", parameters);
                } else {
                    state.replaying = true;
                    self.call(state, "session/load", parameters);
                }
            }
            _ => self.call(state, "session/new", parameters),
        }
    }

    fn session_opened(&self, state: &mut State, effects: &mut Vec<Effect>, id: String, result: &Map<String, Value>) {
        state.session_id = Some(id.clone());
        state.config_options = match result.get("configOptions") {
            Some(Value::Array(options)) => options.clone(),
            _ => Vec::new(),
        };
        let mut value = json!({"type": "acp.session", "sessionId": id});
        if let Some(offered) = result.get("configOptions").filter(|o| !o.is_null()) {
            value["configOptions"] = offered.clone();
        }
        self.event(effects, value);
        state.queued_config = self.request.config_values.iter().cloned().collect();
        self.next_config(state, effects);
    }

    /// `NextConfig`: one setting at a time; one the agent does not offer is named and left out.
    fn next_config(&self, state: &mut State, effects: &mut Vec<Effect>) {
        let Some(session) = state.session_id.clone() else { return };
        while let Some((id, value)) = state.queued_config.pop_front() {
            let option = state.config_options.iter().filter_map(Value::as_object).find(|o| text(o.get("id")) == Some(id.as_str()));
            let send = match option {
                Some(option) => {
                    let values = option_values(option);
                    if !values.is_empty() && !values.contains(&value) {
                        Some(false)
                    } else if text(option.get("currentValue")) == Some(value.as_str()) {
                        None
                    } else {
                        Some(true)
                    }
                }
                None if !state.config_options.is_empty() => Some(false),
                None => Some(true),
            };
            match send {
                None => continue,
                Some(false) => self.unavailable(effects, &id, &value),
                Some(true) => {
                    self.call(state, "session/set_config_option", json!({"sessionId": session, "configId": id, "value": value}));
                    return;
                }
            }
        }
        self.prompt(state, effects, &session);
    }

    fn unavailable(&self, effects: &mut Vec<Effect>, id: &str, value: &str) {
        let shown = clean(Some(value), 120, true);
        effects.push(Effect::Warning(strings::get_with("acp.channel.optionUnavailable", &[("option", id), ("value", &shown)])));
    }

    fn prompt(&self, state: &mut State, effects: &mut Vec<Effect>, session: &str) {
        if state.ready {
            return;
        }
        state.ready = true;
        let mut blocks = vec![json!({"type": "text", "text": self.request.prompt_text})];
        for (media_type, data) in &self.request.images {
            blocks.push(json!({"type": "image", "data": data, "mimeType": media_type}));
        }
        self.event(effects, json!({"type": "acp.prompt", "sessionId": session}));
        let cancel = json!({"jsonrpc": "2.0", "method": "session/cancel", "params": {"sessionId": session}});
        self.cancellation.set(Some(text_json::relaxed(&cancel, false)));
        self.call(state, "session/prompt", json!({"sessionId": session, "prompt": blocks}));
    }

    fn failure(&self, state: &mut State, effects: &mut Vec<Effect>, method: &str, error: &Map<String, Value>) {
        let message = clean(Some(text(error.get("message")).unwrap_or("")), 8_192, false);
        let code = match error.get("code").and_then(Value::as_f64) {
            Some(code) if code.is_finite() && code.abs() < f64::from(i32::MAX) => code.trunc() as i32,
            _ => 0,
        };
        match method {
            "session/resume" | "session/load" => {
                // The session is gone (never answered, deleted): the pane starts a new one.
                state.replaying = false;
                effects.push(Effect::Warning(strings::get("acp.channel.newSession")));
                self.call(state, "session/new", json!({"cwd": self.request.workspace_path, "mcpServers": []}));
            }
            "session/set_config_option" => {
                let said = if message.is_empty() { code.to_string() } else { message };
                effects.push(Effect::Warning(strings::get_with("acp.channel.optionFailed", &[("error", &said)])));
                self.next_config(state, effects);
            }
            _ => {
                // The parser shows the agent's words and reads a lost sign-in from them.
                let mut value = json!({"type": "acp.error", "method": method, "code": code, "message": message});
                if let Some(data) = error.get("data").filter(|d| !d.is_null()) {
                    value["data"] = data.clone();
                }
                self.event(effects, value);
                // The agent answered the prompt with an error: no turn is left to cancel.
                if method == "session/prompt" {
                    self.cancellation.set(None);
                }
                state.failed = true;
                Self::cancel_all_locked(state, effects);
                effects.push(Effect::Completed);
            }
        }
    }

    fn notification(&self, state: &mut State, effects: &mut Vec<Effect>, method: &str, parameters: &Map<String, Value>) {
        // The agent withdrew a request of its own (its turn was cancelled while it asked).
        if method == "$/cancel_request"
            && let Some(withdrawn) = key(parameters.get("requestId"))
        {
            let ids: Vec<String> = state
                .pending
                .iter()
                .filter(|(_, ask)| key(Some(&ask.rpc_id)).as_deref() == Some(withdrawn.as_str()))
                .map(|(id, _)| id.clone())
                .collect();
            for id in ids {
                Self::cancel_pending(state, effects, &id);
            }
            return;
        }
        if method != "session/update" || state.replaying {
            return;
        }
        let Some(session) = state.session_id.clone() else { return };
        if text(parameters.get("sessionId")) != Some(session.as_str()) {
            return;
        }
        let Some(Value::Object(update)) = parameters.get("update") else { return };
        let Some(kind) = text(update.get("sessionUpdate")) else { return };
        let settled = matches!(text(update.get("status")), Some("completed" | "failed"));
        if matches!(kind, "tool_call" | "tool_call_update")
            && let Some(call) = text(update.get("toolCallId")).filter(|c| !c.is_empty() && c.len() <= 256)
        {
            if settled {
                if state.open_call.as_deref() == Some(call) {
                    state.open_call = None;
                }
            } else if kind == "tool_call" {
                state.open_call = Some(call.to_string());
            }
        }
        // A call that settled no longer waits for an answer. A question that goes this way was not answered in time (Hermes stops waiting
        // after `agent.clarify_timeout`): the pane says so.
        if kind == "tool_call_update"
            && settled
            && let Some(settled_call) = text(update.get("toolCallId"))
        {
            let ids: Vec<String> =
                state.pending.iter().filter(|(_, ask)| ask.display.tool_use_id == settled_call).map(|(id, _)| id.clone()).collect();
            for id in ids {
                if let Some(ask) = state.pending.get(&id)
                    && ask.asked.is_some()
                {
                    let question = ask.display.summary.clone();
                    effects.push(Effect::Warning(strings::get_with("acp.channel.questionWithdrawn", &[("question", &question)])));
                }
                Self::cancel_pending(state, effects, &id);
            }
        }
        self.event(effects, json!({"type": "acp.update", "sessionId": session, "update": Value::Object(update.clone())}));
    }

    fn agent_request(&self, state: &mut State, effects: &mut Vec<Effect>, raw_id: &Value, method: &str, parameters: &Map<String, Value>) {
        let Some(request_key) = key(Some(raw_id)) else {
            self.fail_closed(state, effects, strings::get("acp.channel.badRequestId"));
            return;
        };
        if !state.seen.insert(request_key.clone()) {
            self.fail_closed(state, effects, strings::get("acp.channel.duplicateRequest"));
            return;
        }
        if state.seen.len() > 2_048 {
            self.fail_closed(state, effects, strings::get("acp.channel.tooManyRequests"));
            return;
        }
        if method == "elicitation/create" {
            self.elicitation(state, effects, raw_id, &request_key, parameters);
            return;
        }
        // No file system or terminal was offered: anything else is not this client's.
        if method != "session/request_permission" {
            self.send(
                &json!({"jsonrpc": "2.0", "id": raw_id, "error": {"code": -32601, "message": "Method not supported by this client."}}),
            );
            return;
        }
        let call = match (&state.session_id, parameters.get("toolCall")) {
            (Some(session), Some(Value::Object(call))) if text(parameters.get("sessionId")) == Some(session.as_str()) => call,
            _ => {
                self.reply(raw_id, json!({"outcome": {"outcome": "cancelled"}}));
                return;
            }
        };
        let options: Vec<&Map<String, Value>> =
            parameters.get("options").and_then(Value::as_array).into_iter().flatten().filter_map(Value::as_object).take(16).collect();
        let option = |kinds: &[&str]| -> Option<String> {
            for kind in kinds {
                if let Some(found) = options.iter().find(|o| text(o.get("kind")) == Some(kind))
                    && let Some(id) = text(found.get("optionId")).filter(|id| !id.is_empty() && id.len() <= 256)
                {
                    return Some(id.to_string());
                }
            }
            None
        };
        let (allow, reject) = (option(&["allow_once"]), option(&["reject_once", "reject_always"]));
        let allow_once = |o: &&&Map<String, Value>| text(o.get("kind")) == Some("allow_once");
        // Several "allow once" options are a question's choices (Hermes's clarify), not an approval: they are the question card's choices.
        // One the card cannot show is skipped and named instead of answered by guess.
        if options.iter().filter(allow_once).count() > 1 {
            if state.pending.len() < MAXIMUM_PENDING
                && let Some(ask) = self.choice_question(raw_id, &request_key, call, &options, reject.clone())
            {
                let display = ask.display.clone();
                state.pending.insert(display.id.clone(), ask);
                effects.push(Effect::Activity(display.clone(), "waiting"));
                effects.push(Effect::Emit(display));
                return;
            }
            self.answer(raw_id, reject.as_deref());
            let question = clean(Some(text(call.get("title")).unwrap_or("")), 1_000, true);
            let choices: Vec<String> = options
                .iter()
                .filter(allow_once)
                .filter_map(|o| text(o.get("name")))
                .take(8)
                .map(|name| clean(Some(name), 120, true))
                .collect();
            effects.push(Effect::Warning(strings::get_with(
                "acp.channel.questionSkipped",
                &[("question", &question), ("choices", &choices.join(" / "))],
            )));
            return;
        }
        if state.pending.len() >= MAXIMUM_PENDING {
            self.answer(raw_id, reject.as_deref());
            effects.push(Effect::Warning(strings::get("acp.channel.tooManyPending")));
            return;
        }
        let call_id = text(call.get("toolCallId"))
            .filter(|named| !named.is_empty() && named.len() <= 256 && named.chars().all(|ch| ch > ' ' && ch != '\x7f'))
            .map(str::to_string)
            .unwrap_or_else(|| format!("permission-{request_key}"));
        let Some(display) = display_json(&display_input(call.get("rawInput"))).filter(|d| d.len() <= MAXIMUM_INPUT_BYTES) else {
            self.answer(raw_id, reject.as_deref());
            effects.push(Effect::Warning(strings::get("acp.channel.requestTooLarge")));
            return;
        };
        let title = clean(Some(text(call.get("title")).unwrap_or("")), 1_000, true);
        let tool = tool_name(text(call.get("kind")), text(call.get("name")).or_else(|| text(call.get("title"))));
        let summary = if title.is_empty() { strings::get("acp.channel.permissionSummary") } else { title };
        let mut value = ToolPermissionRequest::new(&wire::new_id(), &self.request.run_id, &call_id, &tool, &display, &summary);
        value.reason = allow.is_none().then(|| strings::get("acp.channel.denyOnly"));
        value.can_allow = allow.is_some();
        value.received_at = Some(time::now_o());
        state.pending.insert(
            value.id.clone(),
            Pending { rpc_id: raw_id.clone(), allow_option: allow, reject_option: reject, display: value.clone(), asked: None, row: true },
        );
        effects.push(Effect::Activity(value.clone(), "waiting"));
        effects.push(Effect::Emit(value));
    }

    /// `ChoiceQuestion`: Hermes's clarify as an approval — the tool call's title is the question and each `allow_once` option one choice.
    /// The answer is the picked option; nothing typed can travel.
    fn choice_question(
        &self,
        raw_id: &Value,
        request_key: &str,
        call: &Map<String, Value>,
        options: &[&Map<String, Value>],
        reject: Option<String>,
    ) -> Option<Pending> {
        let asked = call.get("rawInput").and_then(Value::as_object).and_then(|input| text(input.get("question")));
        let question = agent::text(asked.or_else(|| text(call.get("title"))), 8_192, false)?;
        let mut item =
            agent::Item { header: strings::get("questionnaire.header.agent"), question, multi_select: false, options: Vec::new() };
        let mut choices = HashMap::new();
        for option in options.iter().filter(|o| text(o.get("kind")) == Some("allow_once")) {
            let id = text(option.get("optionId")).filter(|id| !id.is_empty() && id.len() <= 256)?;
            let label = agent::text(text(option.get("name")), 1_024, true)?;
            if choices.contains_key(&label) {
                return None;
            }
            choices.insert(label.clone(), id.to_string());
            item.options.push((label, String::new()));
        }
        let call_id = text(call.get("toolCallId"))
            .filter(|named| !named.is_empty() && named.len() <= 256)
            .map(str::to_string)
            .unwrap_or_else(|| format!("question-{request_key}"));
        let mut display = agent::request(&wire::new_id(), &self.request.run_id, &call_id, &[item])?;
        display.received_at = Some(time::now_o());
        Some(Pending {
            rpc_id: raw_id.clone(),
            allow_option: None,
            reject_option: reject,
            display,
            asked: Some(Question { form: None, choices: Some(choices) }),
            row: true,
        })
    }

    /// `Elicitation`: a form the question card asks, or one it cannot (a number, a nested object, more than the card holds), which is
    /// declined and named.
    fn elicitation(
        &self,
        state: &mut State,
        effects: &mut Vec<Effect>,
        raw_id: &Value,
        request_key: &str,
        parameters: &Map<String, Value>,
    ) {
        match &state.session_id {
            Some(session) if text(parameters.get("sessionId")) == Some(session.as_str()) => {}
            _ => {
                self.reply(raw_id, json!({"action": "cancel"}));
                return;
            }
        }
        let call_id = text(parameters.get("toolCallId"))
            .filter(|named| !named.is_empty() && named.len() <= 256)
            .map(str::to_string)
            .or_else(|| state.open_call.clone());
        let form =
            if state.pending.len() >= MAXIMUM_PENDING { None } else { AcpElicitationForm::read(parameters, self.request.free_choices) };
        let fallback = format!("question-{request_key}");
        let display = form
            .as_ref()
            .and_then(|form| agent::request(&wire::new_id(), &self.request.run_id, call_id.as_deref().unwrap_or(&fallback), &form.items));
        let (Some(form), Some(mut value)) = (form, display) else {
            self.reply(raw_id, json!({"action": "decline"}));
            let question = clean(Some(text(parameters.get("message")).unwrap_or("")), 1_000, true);
            effects.push(Effect::Warning(strings::get_with("acp.channel.formDeclined", &[("question", &question)])));
            return;
        };
        value.received_at = Some(time::now_o());
        let row = call_id.is_some();
        state.pending.insert(
            value.id.clone(),
            Pending {
                rpc_id: raw_id.clone(),
                allow_option: None,
                reject_option: None,
                display: value.clone(),
                asked: Some(Question { form: Some(form), choices: None }),
                row,
            },
        );
        if row {
            effects.push(Effect::Activity(value.clone(), "waiting"));
        }
        effects.push(Effect::Emit(value));
    }

    /// `AnswerQuestions`: the question card's answers, sent as the form's content or the picked choice's option. An answer the form
    /// cannot carry leaves the card answerable.
    pub fn answer_questions(&self, request_id: &str, answers: &IndexMap<String, UserQuestionAnswer>) -> Result<()> {
        self.with(|this, state, effects| {
            let gone = || RunError::State(strings::get("permission.error.questionGone"));
            if state.closed {
                return Err(gone());
            }
            let Some(ask) = state.pending.get(request_id) else { return Err(gone()) };
            let Some(asked) = &ask.asked else { return Err(gone()) };
            let checked = agent::answers(&ask.display, answers)?;
            // Checked first: an answer the form cannot carry leaves the card answerable.
            let (content, option) = match &asked.form {
                Some(form) => (Some(form.content(&checked)?), None),
                None => {
                    let option = match checked.values().next() {
                        Some((picked, typed)) if typed.is_empty() && picked.len() == 1 => {
                            asked.choices.as_ref().and_then(|choices| choices.get(&picked[0])).cloned()
                        }
                        _ => None,
                    };
                    (None, Some(option.ok_or_else(|| RunError::Invalid(strings::get("questionnaire.error.choicesOnly")))?))
                }
            };
            let Some(ask) = state.pending.shift_remove(request_id) else { return Err(gone()) };
            match content {
                Some(content) => this.reply(&ask.rpc_id, json!({"action": "accept", "content": content})),
                None => this.answer(&ask.rpc_id, option.as_deref()),
            }
            let mut value = ask.display;
            value.state = "answered".into();
            if ask.row {
                effects.push(Effect::Activity(value.clone(), "running"));
            }
            effects.push(Effect::Emit(value));
            Ok(())
        })
    }

    /// `Respond`: 이번만 허용 / 거부, or a question's Cancel (the form is declined, the clarify approval skipped).
    pub fn respond(&self, request_id: &str, allow: bool) -> Result<()> {
        self.with(|this, state, effects| {
            if state.closed || !state.pending.contains_key(request_id) {
                return Err(RunError::State(copy::already_settled()));
            }
            if allow && state.pending.get(request_id).is_some_and(|ask| ask.allow_option.is_none()) {
                return Err(RunError::State(strings::get("acp.channel.denyOnly")));
            }
            let Some(ask) = state.pending.shift_remove(request_id) else { return Err(RunError::State(copy::already_settled())) };
            if ask.asked.as_ref().is_some_and(|asked| asked.form.is_some()) {
                this.reply(&ask.rpc_id, json!({"action": "decline"}));
            } else {
                this.answer(&ask.rpc_id, if allow { ask.allow_option.as_deref() } else { ask.reject_option.as_deref() });
            }
            let mut value = ask.display;
            value.state = if allow { "allowed" } else { "denied" }.into();
            // A dismissed question's call is settled by the agent's own update, not marked failed here.
            if ask.row {
                effects.push(Effect::Activity(value.clone(), if allow || ask.asked.is_some() { "running" } else { "error" }));
            }
            effects.push(Effect::Emit(value));
            Ok(())
        })
    }

    /// `Answer`: the option the user chose; without one the request is cancelled, which the agent reads as a no.
    fn answer(&self, id: &Value, option_id: Option<&str>) {
        let outcome = match option_id {
            Some(option) => json!({"outcome": "selected", "optionId": option}),
            None => json!({"outcome": "cancelled"}),
        };
        self.reply(id, json!({"outcome": outcome}));
    }

    fn cancel_pending(state: &mut State, effects: &mut Vec<Effect>, id: &str) {
        let Some(ask) = state.pending.shift_remove(id) else { return };
        let mut value = ask.display;
        value.state = "cancelled".into();
        if ask.asked.is_none() {
            effects.push(Effect::Activity(value.clone(), "stopped"));
        }
        effects.push(Effect::Emit(value));
    }

    /// `CancelAll`: every waiting card settles cancelled and the channel takes no more frames.
    pub fn cancel_all(&self) {
        self.with(|_, state, effects| Self::cancel_all_locked(state, effects))
    }

    fn cancel_all_locked(state: &mut State, effects: &mut Vec<Effect>) {
        if state.closed {
            return;
        }
        state.closed = true;
        state.rpc.clear();
        for (_, ask) in std::mem::take(&mut state.pending) {
            let mut value = ask.display;
            value.state = "cancelled".into();
            if ask.row {
                effects.push(Effect::Activity(value.clone(), "stopped"));
            }
            effects.push(Effect::Emit(value));
        }
    }

    fn fail_closed(&self, state: &mut State, effects: &mut Vec<Effect>, message: String) {
        if state.closed {
            return;
        }
        state.failed = true;
        Self::cancel_all_locked(state, effects);
        effects.push(Effect::Fail(message));
    }

    fn call(&self, state: &mut State, method: &str, parameters: Value) {
        state.sequence += 1;
        let id = format!("mighty-{}", state.sequence);
        state.rpc.insert(format!("s:{id}"), method.to_string());
        self.send(&json!({"jsonrpc": "2.0", "id": id, "method": method, "params": parameters}));
    }

    fn reply(&self, id: &Value, result: Value) {
        self.send(&json!({"jsonrpc": "2.0", "id": id, "result": result}));
    }

    fn send(&self, frame: &Value) {
        (self.hooks.write)(&format!("{}\n", text_json::relaxed(frame, false)));
    }

    fn event(&self, effects: &mut Vec<Effect>, value: Value) {
        effects.push(Effect::Consume(text_json::relaxed(&value, false)));
    }
}

/// `Key`: a JSON-RPC id as a key — a string (1–256 bytes) or a whole number; 0 is an id like any other.
pub fn key(raw: Option<&Value>) -> Option<String> {
    match raw? {
        Value::String(text) => (!text.is_empty() && text.len() <= 256).then(|| format!("s:{text}")),
        Value::Number(number) => {
            let value = number.as_f64()?;
            (value.is_finite() && value.round() == value && value.abs() < 1e15).then(|| format!("n:{}", value as i64))
        }
        _ => None,
    }
}

/// `ToolName`: ACP's tool kinds named as the approval card and the activity rows already read them.
pub fn tool_name(kind: Option<&str>, title: Option<&str>) -> String {
    match kind {
        Some("execute") => "Bash".into(),
        Some("edit" | "delete" | "move") => "Edit".into(),
        Some("read") => "Read".into(),
        Some("search") => "Grep".into(),
        Some("fetch") => "WebFetch".into(),
        _ => {
            let name = clean(Some(title.unwrap_or("")), 80, true);
            if name.is_empty() { "Tool".into() } else { name }
        }
    }
}

/// `DisplayInput`: a tool's input with camelCase keys in the snake_case the card and the activity summary read (`filePath` →
/// `file_path`); anything that is not an object is shown under `input`. An input wrapped as `{tool, arguments}` (Hermes's edit approvals)
/// is shown as its arguments.
pub fn display_input(raw: Option<&Value>) -> Value {
    let fields = match raw {
        Some(Value::Object(fields)) => fields,
        None | Some(Value::Null) => return Value::Object(Map::new()),
        Some(other) => return json!({"input": other}),
    };
    let fields = match fields.get("arguments") {
        Some(Value::Object(arguments)) if fields.len() == 2 && text(fields.get("tool")).is_some() => arguments,
        _ => fields,
    };
    let mut result = Map::new();
    for (key, value) in fields.iter().take(64) {
        let mut snake = String::new();
        for character in key.chars() {
            if character.is_uppercase() && !snake.is_empty() && !snake.ends_with('_') {
                snake.push('_');
            }
            snake.extend(character.to_lowercase());
        }
        let name = if snake == "filepath" { "file_path".to_string() } else { snake };
        result.insert(name, value.clone());
    }
    Value::Object(result)
}

/// `DescribedInput`: what a tool call without `rawInput` still says about itself — its first location's path, a shell line its content
/// shows (`$ ls`), or the title's part after `tool: `.
pub fn described_input(call: &Map<String, Value>) -> Option<Value> {
    if let Some(Value::Object(location)) = call.get("locations").and_then(Value::as_array).and_then(|l| l.first())
        && let Some(path) = text(location.get("path")).filter(|p| !p.is_empty())
    {
        return Some(json!({"file_path": path}));
    }
    for block in call.get("content").and_then(Value::as_array).into_iter().flatten().filter_map(Value::as_object).take(4) {
        if let Some(Value::Object(inner)) = block.get("content")
            && let Some(shell) = text(inner.get("text")).and_then(|t| t.strip_prefix("$ "))
        {
            return Some(json!({"command": activity::prefix_utf8(shell, 8_192)}));
        }
    }
    let title = text(call.get("title"))?;
    let rest = &title[title.find(": ")? + 2..];
    (!rest.is_empty()).then(|| json!({"description": rest}))
}

/// `TitledTool`: a title of the form `tool: preview` names its tool before the colon; any other title is the name.
pub fn titled_tool(title: Option<&str>) -> Option<String> {
    let title = title?;
    let Some(colon) = title.find(": ") else { return Some(title.to_string()) };
    let name = &title[..colon];
    let tool = (1..=64).contains(&name.len()) && name.bytes().all(|b| b.is_ascii_alphanumeric() || matches!(b, b'_' | b'.' | b'-'));
    Some(if tool { name } else { title }.to_string())
}

/// `OptionValues`: the values of a `select` option, flat or grouped.
pub fn option_values(option: &Map<String, Value>) -> Vec<String> {
    let mut values = Vec::new();
    for entry in option.get("options").and_then(Value::as_array).into_iter().flatten().filter_map(Value::as_object).take(2_048) {
        if let Some(value) = text(entry.get("value")) {
            values.push(value.to_string());
        }
        for inner in entry.get("options").and_then(Value::as_array).into_iter().flatten().filter_map(Value::as_object).take(2_048) {
            if let Some(grouped) = text(inner.get("value")) {
                values.push(grouped.to_string());
            }
        }
    }
    values
}

/// How deep a value nests (`Utf8JsonWriter`'s `MaxDepth` of 64 refuses deeper).
fn depth(value: &Value) -> usize {
    match value {
        Value::Object(map) => 1 + map.values().map(depth).max().unwrap_or(0),
        Value::Array(items) => 1 + items.iter().map(depth).max().unwrap_or(0),
        _ => 0,
    }
}

/// Keys in C#'s ordinal order (UTF-16 units), at every level.
fn sorted(value: &Value) -> Value {
    match value {
        Value::Object(map) => {
            let mut entries: Vec<(&String, &Value)> = map.iter().collect();
            entries.sort_by(|a, b| a.0.encode_utf16().cmp(b.0.encode_utf16()));
            Value::Object(entries.into_iter().map(|(k, v)| (k.clone(), sorted(v))).collect())
        }
        Value::Array(items) => Value::Array(items.iter().map(sorted).collect()),
        other => other.clone(),
    }
}

/// `DisplayJson`: the card's JSON — indented, keys sorted, format characters (bidirectional overrides and the like) escaped so they
/// cannot hide text. None when it nests too deep to write.
fn display_json(value: &Value) -> Option<String> {
    if depth(value) > 64 {
        return None;
    }
    let text = text_json::relaxed(&sorted(value), true);
    let mut result = String::with_capacity(text.len());
    for character in text.chars() {
        if text_json::is_format(character) {
            let mut units = [0u16; 2];
            for unit in character.encode_utf16(&mut units) {
                result.push_str(&format!("\\u{unit:04x}"));
            }
        } else {
            result.push(character);
        }
    }
    Some(result)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn ids_names_and_inputs() {
        assert_eq!(key(Some(&json!(0))).as_deref(), Some("n:0"));
        assert_eq!(key(Some(&json!("mighty-1"))).as_deref(), Some("s:mighty-1"));
        assert_eq!(key(Some(&json!(1.5))), None);
        assert_eq!(key(Some(&json!(""))), None);
        assert_eq!(tool_name(Some("execute"), None), "Bash");
        assert_eq!(tool_name(Some("other"), Some("tool_search: x")), "tool_search: x");
        assert_eq!(titled_tool(Some("tool_search: x")).as_deref(), Some("tool_search"));
        assert_eq!(titled_tool(Some("a b: x")).as_deref(), Some("a b: x"));
        assert_eq!(display_input(Some(&json!({"filePath": "a", "oldString": "b"}))), json!({"file_path": "a", "old_string": "b"}));
        assert_eq!(display_input(Some(&json!({"tool": "write", "arguments": {"path": "p"}}))), json!({"path": "p"}));
        assert_eq!(display_input(Some(&json!("ls"))), json!({"input": "ls"}));
        assert_eq!(described_input(json!({"title": "terminal: ls"}).as_object().unwrap()), Some(json!({"description": "ls"})));
        assert!(display_json(&json!({"b": 1, "a": "\u{202e}x"})).unwrap().contains("\\u202e"));
    }
}
