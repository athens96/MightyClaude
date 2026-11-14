//! C# `CodexApprovalChannel` (CodexApprovalChannel.cs): one Codex `app-server` turn over JSON-RPC on the CLI's stdio, for a pane in
//! the `onRequest` permission mode. Approvals are ephemeral, bound to the turn's thread, turn and item, and can only grant the single
//! command or diff the card shows; the CLI stays the owner of its sign-in. What the app-server says is turned into the `exec --json`
//! frames the parser and the graph already read (`Legacy`).
//!
//! As in `ClaudePermissionChannel` (permissions.rs), C#'s callbacks run under its re-entrant lock; here the lock is not re-entrant:
//! the writes to the CLI happen under it, in order, and the other callbacks (`consume`, `emit`, `activity`, `warning`, `fail`,
//! `completed`) run after it is released, in the order C# calls them, so a sink may answer the request it was just handed.

use std::collections::{HashMap, HashSet};
use std::fmt::Write as _;
use std::sync::{Arc, Mutex};

use indexmap::IndexMap;
use mighty_core::wire;
use serde_json::{Map, Value, json};

use crate::activity::clean;
use crate::meta;
use crate::model::StartRunRequest;
use crate::os;
use crate::permissions::{ActivityHook, Emit, Message, ToolPermissionRequest};
use crate::plan_mode::MAXIMUM_TODO_ITEMS;
use crate::presentation::strings as copy;
use crate::text_json;
use crate::{Result, RunError, lock};

/// `CodexApprovalChannel.MaximumFrameBytes`: the longest stdout line an approval run reads.
pub const MAXIMUM_FRAME_BYTES: usize = 32 * 1024 * 1024;
/// `MaximumInputBytes`: the largest card (and the largest file change kept for one).
pub const MAXIMUM_INPUT_BYTES: usize = 65_536;
/// `MaximumPending`.
pub const MAXIMUM_PENDING: usize = 16;

/// The callbacks a channel reports through (C#'s constructor arguments).
#[derive(Clone)]
pub struct CodexHooks {
    /// One JSON-RPC line to the CLI's stdin (a failed write is ignored, as C#'s is).
    pub write: Arc<dyn Fn(&str) + Send + Sync>,
    /// One `exec --json` frame for the parser and the graph (the run's `Consume`).
    pub consume: Arc<dyn Fn(&str) + Send + Sync>,
    /// A request waiting, or settled.
    pub emit: Emit,
    /// The tool row's state for a request (the parser owns the row).
    pub activity: ActivityHook,
    pub warning: Message,
    /// The channel failed closed (the run stops).
    pub fail: Message,
    /// The turn completed: the CLI's stdin ends.
    pub completed: Arc<dyn Fn() + Send + Sync>,
}

enum Effect {
    Consume(String),
    Emit(ToolPermissionRequest),
    Activity(ToolPermissionRequest, String),
    Warning(String),
    Fail(String),
    Completed,
}

struct Pending {
    rpc_id: Value,
    display: ToolPermissionRequest,
}

struct State {
    pending: IndexMap<String, Pending>,
    rpc: HashMap<String, String>,
    files: HashMap<String, Value>,
    seen: HashSet<String>,
    thread_id: Option<String>,
    turn_id: Option<String>,
    sequence: u64,
    closed: bool,
    started: bool,
    ready: bool,
    awaiting_turn: bool,
    usage: Value,
    initialized: bool,
    turn_completed: bool,
    failed: bool,
}

/// C# `CodexApprovalChannel`.
pub struct CodexApprovalChannel {
    request: StartRunRequest,
    workspace: String,
    prompt: String,
    images: Vec<String>,
    hooks: CodexHooks,
    state: Mutex<State>,
}

/// `IdKey`: a JSON-RPC id as a key — a non-empty string of at most 256 bytes, or an integer.
fn id_key(raw: Option<&Value>) -> Option<String> {
    match raw? {
        Value::String(text) if !text.is_empty() && text.len() <= 256 => Some(format!("s:{text}")),
        Value::Number(number) => number.as_i64().map(|n| format!("n:{n}")),
        _ => None,
    }
}

/// `Count`: a count that fits a long, never below zero; 0 otherwise.
fn count(value: &Value, name: &str) -> i64 {
    meta::integer(value, name).map(|n| n.max(0)).unwrap_or(0)
}

/// `CodexApprovalChannel.AbsolutePath`: a full path (also Windows roots, for cross-platform tests); a drive-relative `C:foo` is not.
pub fn absolute_path(value: Option<&str>) -> bool {
    let Some(value) = value.filter(|v| !v.is_empty() && !v.contains('\0')) else { return false };
    let bytes = value.as_bytes();
    value.starts_with('/')
        || value.starts_with("\\\\")
        || value.encode_utf16().count() >= 3 && bytes[0].is_ascii_alphabetic() && bytes[1] == b':' && matches!(bytes[2], b'\\' | b'/')
}

/// `JsonNode.ToJsonString(new JsonSerializerOptions { WriteIndented = true })`: the default encoder (every non-ASCII character and
/// the HTML-sensitive ones as `\uXXXX`, which shows Unicode format characters such as bidi overrides), two spaces a level, lines
/// ended by `Environment.NewLine`.
fn indented(value: &Value) -> String {
    let mut out = String::new();
    write_indented(&mut out, value, 0);
    out
}

fn indent(out: &mut String, level: usize) {
    out.push_str(os::new_line());
    for _ in 0..level {
        out.push_str("  ");
    }
}

fn write_indented(out: &mut String, value: &Value, level: usize) {
    match value {
        Value::Null => out.push_str("null"),
        Value::Bool(b) => out.push_str(if *b { "true" } else { "false" }),
        Value::Number(n) => {
            let _ = write!(out, "{n}");
        }
        Value::String(s) => mighty_core::json::escape_into(out, s),
        Value::Array(items) => {
            out.push('[');
            if items.is_empty() {
                out.push(']');
                return;
            }
            for (index, item) in items.iter().enumerate() {
                if index > 0 {
                    out.push(',');
                }
                indent(out, level + 1);
                write_indented(out, item, level + 1);
            }
            indent(out, level);
            out.push(']');
        }
        Value::Object(map) => {
            out.push('{');
            if map.is_empty() {
                out.push('}');
                return;
            }
            for (index, (key, item)) in map.iter().enumerate() {
                if index > 0 {
                    out.push(',');
                }
                indent(out, level + 1);
                mighty_core::json::escape_into(out, key);
                out.push_str(": ");
                write_indented(out, item, level + 1);
            }
            indent(out, level);
            out.push('}');
        }
    }
}

/// `LegacyItem`: an app-server item as `exec --json` names it, or None for a kind the parser does not read.
fn legacy_item(raw: &Value) -> Option<Value> {
    let mapped = match meta::text(raw, "type")? {
        "agentMessage" => "agent_message",
        "commandExecution" => "command_execution",
        "fileChange" => "file_change",
        "mcpToolCall" => "mcp_tool_call",
        "webSearch" => "web_search",
        "collabAgentToolCall" => "collab_agent_tool_call",
        "contextCompaction" => "context_compaction",
        "imageView" => "image_view",
        "imageGeneration" => "image_generation",
        _ => return None,
    };
    let mut item = raw.as_object()?.clone();
    item.insert("type".into(), Value::String(mapped.into()));
    for (from, to) in [
        ("aggregatedOutput", "aggregated_output"),
        ("exitCode", "exit_code"),
        ("senderThreadId", "sender_thread_id"),
        ("receiverThreadIds", "receiver_thread_ids"),
        ("agentsStates", "agents_states"),
        ("savedPath", "saved_path"),
    ] {
        if let Some(value) = item.shift_remove(from) {
            item.insert(to.into(), value);
        }
    }
    if meta::text(raw, "status") == Some("inProgress") {
        item.insert("status".into(), Value::String("in_progress".into()));
    }
    if mapped == "collab_agent_tool_call"
        && let Some(tool) = meta::text(raw, "tool")
    {
        let named = match tool {
            "spawnAgent" => "spawn_agent",
            "sendInput" => "send_input",
            "closeAgent" => "close_agent",
            other => other,
        };
        item.insert("tool".into(), Value::String(named.into()));
    }
    Some(Value::Object(item))
}

impl CodexApprovalChannel {
    /// `request`: the run's (validated) request; `workspace`: the folder the turn works in; `prompt`: the request's text (with the
    /// attachments' copies named); `images`: the pictures' staged copies, sent as `localImage`.
    pub fn new(request: &StartRunRequest, workspace: &str, prompt: &str, images: Vec<String>, hooks: CodexHooks) -> CodexApprovalChannel {
        CodexApprovalChannel {
            request: request.clone(),
            workspace: workspace.into(),
            prompt: prompt.into(),
            images,
            hooks,
            state: Mutex::new(State {
                pending: IndexMap::new(),
                rpc: HashMap::new(),
                files: HashMap::new(),
                seen: HashSet::new(),
                thread_id: None,
                turn_id: None,
                sequence: 0,
                closed: false,
                started: false,
                ready: false,
                awaiting_turn: false,
                usage: json!({}),
                initialized: false,
                turn_completed: false,
                failed: false,
            }),
        }
    }

    pub fn initialized(&self) -> bool {
        lock(&self.state).initialized
    }

    pub fn turn_completed(&self) -> bool {
        lock(&self.state).turn_completed
    }

    pub fn failed(&self) -> bool {
        lock(&self.state).failed
    }

    fn run(&self, effects: Vec<Effect>) {
        for effect in effects {
            match effect {
                Effect::Consume(frame) => (self.hooks.consume)(&frame),
                Effect::Emit(value) => (self.hooks.emit)(value),
                Effect::Activity(value, state) => (self.hooks.activity)(&value, &state),
                Effect::Warning(message) => (self.hooks.warning)(&message),
                Effect::Fail(message) => (self.hooks.fail)(&message),
                Effect::Completed => (self.hooks.completed)(),
            }
        }
    }

    /// `Start`: the `initialize` call, once.
    pub fn start(&self) {
        let mut state = lock(&self.state);
        if state.started || state.closed {
            return;
        }
        state.started = true;
        let parameters = json!({
            "clientInfo": {"name": "mightyclaude", "title": "Mighty Code", "version": "1.0"},
            "capabilities": {"experimentalApi": false},
        });
        self.call(&mut state, "initialize", parameters);
    }

    /// `InitializationTimedOut`: a turn the CLI never started fails closed.
    pub fn initialization_timed_out(&self) {
        let mut effects = Vec::new();
        {
            let mut state = lock(&self.state);
            if !state.closed && !state.ready {
                self.fail_closed(&mut state, &mut effects, "Codex approval channel initialization timed out.");
            }
        }
        self.run(effects);
    }

    /// `Flush`: the CLI exited; a turn it never completed is a failure.
    pub fn flush(&self) {
        let mut effects = Vec::new();
        {
            let mut state = lock(&self.state);
            if !state.closed {
                self.fail_closed(&mut state, &mut effects, "Codex exited before completing the approval turn.");
            }
        }
        self.run(effects);
    }

    /// `Receive`: one stdout line of the CLI.
    pub fn receive(&self, frame: &str) {
        let mut effects = Vec::new();
        {
            let mut state = lock(&self.state);
            if !state.closed {
                if frame.len() > MAXIMUM_FRAME_BYTES {
                    self.fail_closed(&mut state, &mut effects, "Codex response frame exceeded the size limit.");
                } else {
                    match serde_json::from_str::<Value>(frame) {
                        Ok(root) if root.is_object() => self.receive_frame(&mut state, &mut effects, &root),
                        Ok(_) => self.fail_closed(&mut state, &mut effects, "Invalid Codex response."),
                        Err(_) => self.fail_closed(&mut state, &mut effects, "Invalid Codex response JSON."),
                    }
                }
            }
        }
        self.run(effects);
    }

    fn receive_frame(&self, state: &mut State, effects: &mut Vec<Effect>, root: &Value) {
        if let Some(method) = meta::text(root, "method") {
            let parameters = meta::prop(root, "params").filter(|p| p.is_object());
            let id = meta::prop(root, "id");
            let Some(parameters) = parameters else {
                match id {
                    Some(id) => self.rpc_error(id, "Invalid request parameters."),
                    None => self.fail_closed(state, effects, "Invalid Codex notification."),
                }
                return;
            };
            match id {
                Some(id) => self.server_request(state, effects, id, method, parameters),
                None => self.notification(state, effects, method, parameters),
            }
            return;
        }
        let Some(key) = id_key(meta::prop(root, "id")) else { return };
        let Some(response_method) = state.rpc.remove(&key) else { return };
        let result = meta::prop(root, "result").filter(|r| r.is_object());
        let Some(result) = result.filter(|_| meta::prop(root, "error").is_none()) else {
            let message = format!("Codex {response_method} failed. Check CLI version, authentication and permissions.");
            self.fail_closed(state, effects, &message);
            return;
        };
        let settings = self.request.settings();
        match response_method.as_str() {
            "initialize" => {
                state.initialized = true;
                self.send(&json!({"method": "initialized"}));
                let mut parameters = Map::new();
                parameters.insert("cwd".into(), Value::String(self.workspace.clone()));
                parameters.insert("approvalPolicy".into(), Value::String("on-request".into()));
                parameters.insert("approvalsReviewer".into(), Value::String("user".into()));
                parameters.insert("sandbox".into(), Value::String("workspace-write".into()));
                if self.request.model != "default" {
                    parameters.insert("model".into(), Value::String(self.request.model.clone()));
                }
                match &self.request.resume_id {
                    Some(resume) => {
                        parameters.insert("threadId".into(), Value::String(resume.clone()));
                        parameters.insert("excludeTurns".into(), Value::Bool(true));
                        self.call(state, "thread/resume", Value::Object(parameters));
                    }
                    None => self.call(state, "thread/start", Value::Object(parameters)),
                }
            }
            "thread/start" | "thread/resume" => {
                let thread = meta::text_of(meta::prop(result, "thread"), "id");
                let Some(thread) = thread.filter(|t| wire::identifier(t)) else {
                    self.fail_closed(state, effects, "Codex thread identifier did not match.");
                    return;
                };
                if self.request.resume_id.as_deref().is_some_and(|resume| resume != thread) {
                    self.fail_closed(state, effects, "Codex thread identifier did not match.");
                    return;
                }
                state.thread_id = Some(thread.to_string());
                Self::legacy(effects, &json!({"type": "thread.started", "thread_id": thread}));
                self.start_turn(state, &settings);
            }
            "turn/start" => {
                let turn = meta::text_of(meta::prop(result, "turn"), "id");
                let Some(turn) = turn.filter(|t| wire::identifier(t) && state.turn_id.as_deref().is_none_or(|known| known == *t)) else {
                    self.fail_closed(state, effects, "Codex turn identifier did not match.");
                    return;
                };
                if state.turn_id.is_none() {
                    state.turn_id = Some(turn.to_string());
                    Self::legacy(effects, &json!({"type": "turn.started"}));
                }
                state.awaiting_turn = false;
                state.ready = true;
            }
            _ => {}
        }
    }

    fn start_turn(&self, state: &mut State, settings: &mighty_core::model::RunSettings) {
        let mut input = vec![json!({"type": "text", "text": self.prompt})];
        input.extend(self.images.iter().map(|path| json!({"type": "localImage", "path": path})));
        let roots: Vec<Value> = match &self.request.delegation_worktree_root {
            Some(root) => vec![Value::String(self.workspace.clone()), Value::String(root.clone())],
            None => vec![Value::String(self.workspace.clone())],
        };
        let mut parameters = Map::new();
        parameters.insert("threadId".into(), state.thread_id.clone().map(Value::String).unwrap_or(Value::Null));
        parameters.insert("input".into(), Value::Array(input));
        parameters.insert("cwd".into(), Value::String(self.workspace.clone()));
        parameters.insert("approvalPolicy".into(), Value::String("on-request".into()));
        parameters.insert("approvalsReviewer".into(), Value::String("user".into()));
        parameters.insert(
            "sandboxPolicy".into(),
            json!({"type": "workspaceWrite", "writableRoots": roots, "networkAccess": settings.network_access}),
        );
        if self.request.model != "default" {
            parameters.insert("model".into(), Value::String(self.request.model.clone()));
        }
        if settings.effort != "default" {
            parameters.insert("effort".into(), Value::String(settings.effort.clone()));
        }
        if settings.fast_mode {
            parameters.insert("serviceTier".into(), Value::String("fast".into()));
        }
        state.awaiting_turn = true;
        self.call(state, "turn/start", Value::Object(parameters));
    }

    fn notification(&self, state: &mut State, effects: &mut Vec<Effect>, method: &str, parameters: &Value) {
        let Some(thread) = state.thread_id.clone() else { return };
        if meta::text(parameters, "threadId") != Some(thread.as_str()) {
            return;
        }
        if method == "serverRequest/resolved"
            && let Some(key) = id_key(meta::prop(parameters, "requestId"))
        {
            let resolved: Vec<String> = state
                .pending
                .iter()
                .filter(|(_, p)| id_key(Some(&p.rpc_id)).as_deref() == Some(key.as_str()))
                .map(|(id, _)| id.clone())
                .collect();
            for id in resolved {
                Self::cancel_pending(state, effects, &id);
            }
            return;
        }
        if method == "turn/started"
            && state.awaiting_turn
            && let Some(turn) = meta::text_of(meta::prop(parameters, "turn"), "id").filter(|t| wire::identifier(t))
        {
            if state.turn_id.as_deref().is_some_and(|known| known != turn) {
                self.fail_closed(state, effects, "Codex turn notification did not match.");
                return;
            }
            if state.turn_id.is_none() {
                state.turn_id = Some(turn.to_string());
                Self::legacy(effects, &json!({"type": "turn.started"}));
            }
            return;
        }
        if method == "turn/completed" {
            let result = meta::prop(parameters, "turn");
            let Some(turn) = state.turn_id.clone() else { return };
            if meta::text_of(result, "id") != Some(turn.as_str()) {
                return;
            }
            if meta::text_of(result, "status") != Some("completed") {
                self.fail_closed(state, effects, "Codex turn failed or was interrupted.");
                return;
            }
            Self::legacy(effects, &json!({"type": "turn.completed", "usage": state.usage.clone()}));
            state.turn_completed = true;
            Self::cancel_all_locked(state, effects);
            effects.push(Effect::Completed);
            return;
        }
        let Some(turn) = state.turn_id.clone() else { return };
        if meta::text(parameters, "turnId") != Some(turn.as_str()) {
            return;
        }
        if method == "error" && !matches!(meta::prop(parameters, "willRetry"), Some(Value::Bool(true))) {
            self.fail_closed(state, effects, "Codex turn returned an error.");
            return;
        }
        if method == "thread/tokenUsage/updated"
            && let Some(last) = meta::prop(parameters, "tokenUsage").and_then(|u| meta::prop(u, "last")).filter(|l| l.is_object())
        {
            state.usage = json!({
                "input_tokens": count(last, "inputTokens"),
                "cached_input_tokens": count(last, "cachedInputTokens"),
                "output_tokens": count(last, "outputTokens"),
            });
            return;
        }
        // The turn's plan (update_plan) as exec's todo_list item, so the checklist reads one shape (TodoProgressTracker).
        if method == "turn/plan/updated" {
            if let Some(Value::Array(plan)) = meta::prop(parameters, "plan") {
                let items: Vec<Value> = plan
                    .iter()
                    .filter_map(|step| {
                        let text = meta::text(step, "step")?;
                        Some(json!({"text": text, "completed": meta::text(step, "status") == Some("completed")}))
                    })
                    .take(MAXIMUM_TODO_ITEMS)
                    .collect();
                Self::legacy(effects, &json!({"type": "item.updated", "item": {"id": "turn-plan", "type": "todo_list", "items": items}}));
            }
            return;
        }
        let Some(item) = meta::prop(parameters, "item") else { return };
        let started = method == "item/started";
        if !started && method != "item/completed" {
            return;
        }
        let Some(item_id) = meta::text(item, "id") else { return };
        if !started {
            let ended: Vec<String> =
                state.pending.iter().filter(|(_, p)| p.display.tool_use_id == item_id).map(|(id, _)| id.clone()).collect();
            for id in ended {
                Self::cancel_pending(state, effects, &id);
            }
        }
        if meta::text(item, "type") == Some("fileChange") {
            // C# measures the item's raw text; this is its compact form (escapes and spacing may differ).
            if started && state.files.len() < 128 && item.to_string().len() <= MAXIMUM_INPUT_BYTES {
                state.files.insert(item_id.to_string(), item.clone());
            }
            if !started {
                state.files.remove(item_id);
            }
        }
        if let Some(mapped) = legacy_item(item) {
            Self::legacy(effects, &json!({"type": if started { "item.started" } else { "item.completed" }, "item": mapped}));
        }
    }

    fn server_request(&self, state: &mut State, effects: &mut Vec<Effect>, raw_id: &Value, method: &str, parameters: &Value) {
        let Some(key) = id_key(Some(raw_id)) else {
            self.fail_closed(state, effects, "Invalid Codex request identifier.");
            return;
        };
        if !state.seen.insert(key) {
            self.fail_closed(state, effects, "Duplicate Codex approval request.");
            return;
        }
        if state.seen.len() > 2048 {
            self.fail_closed(state, effects, "Too many Codex approval requests.");
            return;
        }
        let belongs = matches!((&state.thread_id, &state.turn_id), (Some(thread), Some(turn))
            if meta::text(parameters, "threadId") == Some(thread.as_str()) && meta::text(parameters, "turnId") == Some(turn.as_str()));
        if !belongs {
            self.rpc_error(raw_id, "Request does not belong to the active thread and turn.");
            return;
        }
        if method == "item/permissions/requestApproval" {
            self.reply(raw_id, json!({"permissions": {}, "scope": "turn"}));
            return;
        }
        if method == "mcpServer/elicitation/request" {
            self.reply(raw_id, json!({"action": "decline", "content": null, "_meta": null}));
            return;
        }
        let command = method == "item/commandExecution/requestApproval";
        if !command && method != "item/fileChange/requestApproval" {
            self.rpc_error(raw_id, "This host does not support this request.");
            effects.push(Effect::Warning("Unsupported Codex input request was declined.".into()));
            return;
        }
        let item_id = meta::text(parameters, "itemId");
        let Some(item_id) = item_id.filter(|i| wire::identifier(i) && state.pending.len() < MAXIMUM_PENDING) else {
            self.reply(raw_id, json!({"decision": "decline"}));
            return;
        };
        let mut input = parameters.as_object().cloned().unwrap_or_default();
        let mut can_allow;
        if command {
            can_allow = meta::text(parameters, "command").is_some_and(|c| !c.is_empty())
                && absolute_path(meta::text(parameters, "cwd"))
                && (meta::prop(parameters, "kind").is_none() || meta::text(parameters, "kind") == Some("command"));
        } else {
            let changes = state.files.get(item_id).and_then(|file| meta::prop(file, "changes"));
            let list = match changes {
                Some(Value::Array(list)) => Some(list),
                _ => None,
            };
            input.insert("changes".into(), Value::Array(list.cloned().unwrap_or_default()));
            can_allow = list.is_some_and(|list| {
                !list.is_empty()
                    && list.iter().all(|c| {
                        meta::text(c, "path").is_some_and(|p| !p.is_empty())
                            && matches!(meta::prop(c, "diff"), Some(Value::String(_)))
                            && meta::prop(c, "kind").is_some_and(Value::is_object)
                    })
            });
            if !meta::absent_or_null(parameters, "grantRoot") {
                can_allow = false;
            }
        }
        if !meta::absent_or_null(parameters, "availableDecisions") {
            can_allow &= matches!(meta::prop(parameters, "availableDecisions"), Some(Value::Array(decisions))
                if decisions.iter().any(|d| d.as_str() == Some("accept")));
        }
        // The default encoder visibly escapes Unicode format characters, bidi overrides among them.
        let display = indented(&Value::Object(input));
        if display.len() > MAXIMUM_INPUT_BYTES {
            self.reply(raw_id, json!({"decision": "decline"}));
            effects.push(Effect::Warning("Codex approval contents exceeded the complete-display limit.".into()));
            return;
        }
        let (tool, summary) =
            if command { ("command_execution", "Command execution approval") } else { ("file_change", "File change approval") };
        let mut value = ToolPermissionRequest::new(&wire::new_id(), &self.request.session_id, item_id, tool, &display, summary);
        value.reason = Some(if can_allow {
            clean(Some(meta::text(parameters, "reason").unwrap_or("")), 8192, false)
        } else {
            "The full command or diff is unavailable, or this request does not support a one-time approval.".into()
        });
        value.can_allow = can_allow;
        state.pending.insert(value.id.clone(), Pending { rpc_id: raw_id.clone(), display: value.clone() });
        effects.push(Effect::Activity(value.clone(), "waiting".into()));
        effects.push(Effect::Emit(value));
    }

    /// `Respond`: 이번만 허용 / 거부 for one waiting request; only the single displayed command or diff is granted.
    pub fn respond(&self, id: &str, allow: bool) -> Result<()> {
        let mut effects = Vec::new();
        {
            let mut state = lock(&self.state);
            if state.closed || !state.pending.contains_key(id) {
                return Err(RunError::State(copy::already_settled()));
            }
            if allow && !state.pending[id].display.can_allow {
                return Err(RunError::State(copy::cannot_allow()));
            }
            let Some(ask) = state.pending.shift_remove(id) else { return Err(RunError::State(copy::already_settled())) };
            self.reply(&ask.rpc_id, json!({"decision": if allow { "accept" } else { "decline" }}));
            let value = ToolPermissionRequest { state: if allow { "allowed" } else { "denied" }.into(), ..ask.display };
            effects.push(Effect::Activity(value.clone(), if allow { "running" } else { "error" }.into()));
            effects.push(Effect::Emit(value));
        }
        self.run(effects);
        Ok(())
    }

    fn cancel_pending(state: &mut State, effects: &mut Vec<Effect>, id: &str) {
        if let Some(ask) = state.pending.shift_remove(id) {
            let value = ToolPermissionRequest { state: "cancelled".into(), ..ask.display };
            effects.push(Effect::Activity(value.clone(), "stopped".into()));
            effects.push(Effect::Emit(value));
        }
    }

    fn cancel_all_locked(state: &mut State, effects: &mut Vec<Effect>) {
        if state.closed {
            return;
        }
        state.closed = true;
        state.files.clear();
        state.rpc.clear();
        let ids: Vec<String> = state.pending.keys().cloned().collect();
        for id in ids {
            Self::cancel_pending(state, effects, &id);
        }
    }

    /// `CancelAll`: the channel closes and every waiting request settles as cancelled.
    pub fn cancel_all(&self) {
        let mut effects = Vec::new();
        Self::cancel_all_locked(&mut lock(&self.state), &mut effects);
        self.run(effects);
    }

    fn fail_closed(&self, state: &mut State, effects: &mut Vec<Effect>, message: &str) {
        if state.closed {
            return;
        }
        state.failed = true;
        Self::cancel_all_locked(state, effects);
        effects.push(Effect::Fail(message.into()));
    }

    fn call(&self, state: &mut State, method: &str, parameters: Value) {
        state.sequence += 1;
        let id = format!("mighty-{}", state.sequence);
        state.rpc.insert(format!("s:{id}"), method.into());
        self.send(&json!({"id": id, "method": method, "params": parameters}));
    }

    fn reply(&self, id: &Value, result: Value) {
        self.send(&json!({"id": id, "result": result}));
    }

    fn rpc_error(&self, id: &Value, message: &str) {
        self.send(&json!({"id": id, "error": {"code": -32601, "message": message}}));
    }

    fn send(&self, value: &Value) {
        (self.hooks.write)(&format!("{}\n", text_json::wire(value)));
    }

    fn legacy(effects: &mut Vec<Effect>, value: &Value) {
        effects.push(Effect::Consume(text_json::wire(value)));
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_display_is_indented_with_the_default_encoder() {
        let nl = os::new_line();
        assert_eq!(
            indented(&json!({"a": [1, "é"], "b": {}})),
            format!("{{{nl}  \"a\": [{nl}    1,{nl}    \"\\u00E9\"{nl}  ],{nl}  \"b\": {{}}{nl}}}")
        );
        assert_eq!(indented(&json!({"x": "\u{202E}<"})), format!("{{{nl}  \"x\": \"\\u202E\\u003C\"{nl}}}"));
    }
}
