//! C# `OutputParser` (OutputParser.cs:10-615): an agent's stream as the pane's transcript — its answers, its tool rows with their
//! durations, its resume id, its usage, the pictures it showed.
//!
//! Claude's stream-json, Codex's `exec --json` and pi's RPC (`pi`, OutputParser.cs:513-597) are read here. The ACP agents' frames
//! and Antigravity's stream are W5's: a [`ProviderStream`] plugs each in, and without one their frames are left unread. Codex's `collab_tool_call` rows need the graph
//! tracker's `CodexCollaborationItem` (mighty-graph) and its `image_view` pictures the image paths (W4): both go to the hooks.
//!
//! Every callback runs under the parser's lock, as C#'s do; none may call back into the parser.

use std::collections::{HashMap, HashSet, VecDeque};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};
use std::time::Instant;

use mighty_core::model::{AgentActivity, AgentImageRef, LogEntry, SessionUsage};
use mighty_core::{normalize, time};
use serde_json::Value;
use sha2::{Digest, Sha256};

use crate::activity::{self, MAXIMUM_MESSAGE_BYTES, terminal, valid_duration};
use crate::compaction;
use crate::lock;
use crate::meta;
use crate::permissions::ToolPermissionRequest;
use crate::pi_dialogs::PiDialogChannel;
use crate::stream;
use crate::strings;
use crate::usage::SessionUsageTracker;

mod pi;

/// Why a picture was refused (C# `AgentImageError`).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ImageError {
    Empty,
    TooLarge,
    UnsupportedType,
    Undecodable,
    ExternalSvg,
    TooManyPixels,
    InvalidEncoding,
}

/// `AgentImageSupport.FailureText`.
pub fn image_failure_text(error: ImageError) -> String {
    match error {
        ImageError::TooLarge => strings::get_with("images.failed.tooLarge", &[("limit", "20")]),
        ImageError::ExternalSvg => strings::get("images.failed.externalSVG"),
        ImageError::TooManyPixels => strings::get("images.failed.tooManyPixels"),
        ImageError::UnsupportedType => strings::get("images.failed.unsupported"),
        _ => strings::get("images.failed.undecodable"),
    }
}

/// Where the pictures an agent shows are kept (C# `AgentImageCache`, the images subsystem): `Prepare(base64, mediaType)` and its
/// `Ref(source, readPath)` in one call. Without one, pictures are left out, as C# leaves them out without a cache.
pub trait ImageStore: Send + Sync {
    fn prepare(&self, base64: &str, media_type: &str, source: &str, read_path: Option<&str>) -> Result<AgentImageRef, ImageError>;
}

pub const MAXIMUM_IMAGES_PER_RUN: usize = 64;
pub const MAXIMUM_IMAGES_PER_ENTRY: usize = 16;

/// `AgentImageSupport.Payloads`: base64 pictures in a tool result or message content, in order.
pub fn image_payloads(value: Option<&Value>, depth: usize) -> Vec<(String, String)> {
    let mut result = Vec::new();
    let Some(value) = value else { return result };
    if depth >= 4 {
        return result;
    }
    if value.is_object() {
        if meta::text(value, "type") == Some("image") {
            let source = meta::prop(value, "source");
            if meta::text_of(source, "type") == Some("base64")
                && let (Some(data), Some(kind)) = (meta::text_of(source, "data"), meta::text_of(source, "media_type"))
            {
                result.push((kind.to_string(), data.to_string()));
            } else if let Some(inline) = meta::text(value, "data")
                && let Some(kind) =
                    meta::text(value, "mimeType").or_else(|| meta::text(value, "mime_type")).or_else(|| meta::text(value, "media_type"))
            {
                result.push((kind.to_string(), inline.to_string()));
            }
            return result;
        }
        return image_payloads(meta::prop(value, "content"), depth + 1);
    }
    if let Value::Array(items) = value {
        for block in items.iter().take(64) {
            result.extend(image_payloads(Some(block), depth + 1));
        }
    }
    result
}

/// `AgentImageSupport.EntryText`.
pub fn image_entry_text(count: usize, source: &str) -> String {
    let label = activity::clean(Some(source), 240, true);
    if count == 1 {
        strings::get_with("images.entry.one", &[("source", &label)])
    } else {
        strings::get_with("images.entry.many", &[("count", &count.to_string()), ("source", &label)])
    }
}

/// The frames of an agent this parser does not read itself (the ACP agents, Antigravity: W5), handed the parser's sinks.
pub trait ProviderStream: Send {
    fn frame(&mut self, root: &Value, sinks: &mut StreamSinks<'_>);
}

/// What a [`ProviderStream`] may do with a frame: log a line, name the resume id, report a turn or a tool.
pub struct StreamSinks<'a> {
    pub log: &'a (dyn Fn(&str, &str) + Send + Sync),
    pub resume: &'a mut dyn FnMut(&str),
    pub failed: &'a mut bool,
}

pub type LogHook = Arc<dyn Fn(&str, &str) + Send + Sync>;
pub type ResumeHook = Arc<dyn Fn(&str) + Send + Sync>;
pub type ActivityHook = Arc<dyn Fn(AgentActivity) + Send + Sync>;
pub type UsageHook = Arc<dyn Fn(SessionUsage) + Send + Sync>;
pub type EntryHook = Arc<dyn Fn(LogEntry) + Send + Sync>;
pub type GraphActivityHook = Arc<dyn Fn(&AgentActivity, Option<&str>) -> bool + Send + Sync>;
pub type GraphImagesHook = Arc<dyn Fn(&LogEntry, Option<&str>, Option<&str>) -> bool + Send + Sync>;
/// An Edit, Write or MultiEdit result the run read (`FileOriginal`): the path as the tool named it, the tool, and the line's
/// `tool_use_result`, for the file originals (FileChanges, W4) to make their `FileEdit` of.
pub type FileOriginalHook = Arc<dyn Fn(&str, &str, &Value) + Send + Sync>;
/// `piReply`: one JSON line (newline included) to pi's stdin, the answer to a dialog no pane can show.
pub type PiReplyHook = Arc<dyn Fn(&str) + Send + Sync>;

/// The parser's callbacks (C#'s constructor arguments).
#[derive(Clone, Default)]
pub struct ParserHooks {
    pub log: Option<LogHook>,
    pub resume: Option<ResumeHook>,
    pub activity: Option<ActivityHook>,
    pub usage: Option<UsageHook>,
    pub image_entry: Option<EntryHook>,
    pub graph_activity: Option<GraphActivityHook>,
    pub graph_images: Option<GraphImagesHook>,
    pub images: Option<Arc<dyn ImageStore>>,
    pub file_original: Option<FileOriginalHook>,
    /// pi only (`piReply`).
    pub pi_reply: Option<PiReplyHook>,
}

struct Inner {
    usage: SessionUsageTracker,
    seen: HashSet<String>,
    seen_order: VecDeque<String>,
    activities: HashMap<String, AgentActivity>,
    starts: HashMap<String, f64>,
    sequences: HashMap<String, i64>,
    permission_states: HashMap<String, String>,
    activity_order: VecDeque<String>,
    pending: String,
    pending_truncated: bool,
    last_resume: Option<String>,
    last_turn: Option<AgentActivity>,
    assistant_seen: bool,
    closed: bool,
    failed: bool,
    stored_images: usize,
    image_limit_noted: bool,
    other: Option<Box<dyn ProviderStream>>,
    pi: pi::PiState,
}

/// C# `OutputParser`.
pub struct OutputParser {
    provider: String,
    namespace: String,
    hooks: ParserHooks,
    origin: Instant,
    clock: Option<Arc<dyn Fn() -> f64 + Send + Sync>>,
    inner: Mutex<Inner>,
    /// A plan the user cancelled interrupts the turn; that interruption is not a failure.
    pub suppress_interrupted_result: AtomicBool,
    /// The workspace folder a picture named only by path must lie in.
    pub image_root: Mutex<Option<String>>,
    /// `PiDialogs`: pi's dialogs as the pane's question card; without it every dialog is cancelled.
    pi_dialogs: Mutex<Option<Arc<PiDialogChannel>>>,
}

/// `OutputParser.MaximumLineBytes`: 64 MiB (a line may carry whole pictures), 8 MiB for Antigravity.
pub fn maximum_line_bytes(provider: &str) -> usize {
    (if provider == "gemini" { 8 } else { 64 }) * 1_048_576
}

impl OutputParser {
    /// `namespace`: the run's activity id (`ActivityId`), which names its tool rows' ids and its turn's.
    pub fn new(provider: &str, namespace: &str, hooks: ParserHooks) -> OutputParser {
        let usage_hook = hooks.usage.clone();
        let usage =
            SessionUsageTracker::new(provider, usage_hook.map(|hook| Box::new(move |value| hook(value)) as crate::usage::UsageCallback));
        OutputParser {
            provider: provider.into(),
            namespace: namespace.into(),
            hooks,
            origin: Instant::now(),
            clock: None,
            inner: Mutex::new(Inner {
                usage,
                seen: HashSet::new(),
                seen_order: VecDeque::new(),
                activities: HashMap::new(),
                starts: HashMap::new(),
                sequences: HashMap::new(),
                permission_states: HashMap::new(),
                activity_order: VecDeque::new(),
                pending: String::new(),
                pending_truncated: false,
                last_resume: None,
                last_turn: None,
                assistant_seen: false,
                closed: false,
                failed: false,
                stored_images: 0,
                image_limit_noted: false,
                other: None,
                pi: pi::PiState::default(),
            }),
            suppress_interrupted_result: AtomicBool::new(false),
            image_root: Mutex::new(None),
            pi_dialogs: Mutex::new(None),
        }
    }

    /// The milliseconds tool durations are measured on (C#'s `activityClock`; by default since the parser was made).
    pub fn with_clock(mut self, clock: Arc<dyn Fn() -> f64 + Send + Sync>) -> OutputParser {
        self.clock = Some(clock);
        self
    }

    /// Plugs in the reader of a provider this parser does not read itself.
    pub fn set_provider_stream(&self, stream: Box<dyn ProviderStream>) {
        lock(&self.inner).other = Some(stream);
    }

    pub fn failed(&self) -> bool {
        lock(&self.inner).failed
    }

    /// `PiTurnOpen`: pi's turn is still going (its stdin stays open).
    pub fn pi_turn_open(&self) -> bool {
        lock(&self.inner).pi.turn_open()
    }

    /// `PiTurnEnded`: pi's `agent_end` has come (stdin closes after the quiet that follows it).
    pub fn pi_turn_ended(&self) -> bool {
        lock(&self.inner).pi.turn_ended()
    }

    /// `AuthenticationFailure`: pi's own sign-in failure as its RPC stream said it last (a rejected command, an assistant message
    /// that ended in such an error or a retry that gave up on one); a later message, or a retry starting, clears it.
    pub fn authentication_failure(&self) -> bool {
        lock(&self.inner).pi.authentication_failure
    }

    /// Sets `PiDialogs`.
    pub fn set_pi_dialogs(&self, dialogs: Option<Arc<PiDialogChannel>>) {
        *lock(&self.pi_dialogs) = dialogs;
    }

    fn now(&self) -> f64 {
        match &self.clock {
            Some(clock) => clock(),
            None => self.origin.elapsed().as_secs_f64() * 1000.0,
        }
    }

    fn log(&self, kind: &str, text: &str) {
        if let Some(log) = &self.hooks.log {
            log(kind, text);
        }
    }

    fn resume(&self, inner: &mut Inner, value: Option<&str>) {
        let Some(value) = value.filter(|v| mighty_core::wire::session_identifier(v)) else { return };
        if inner.last_resume.as_deref() == Some(value) {
            return;
        }
        inner.last_resume = Some(value.to_string());
        if let Some(resume) = &self.hooks.resume {
            resume(value);
        }
    }

    fn assistant(&self, inner: &mut Inner, text: &str, key: Option<&str>) {
        if text.is_empty() {
            return;
        }
        let digest: String = Sha256::digest(text.as_bytes()).iter().map(|b| format!("{b:02X}")).collect();
        let key = format!("{}:{digest}", key.unwrap_or("message"));
        if !inner.seen.insert(key.clone()) {
            return;
        }
        inner.seen_order.push_back(key);
        if inner.seen_order.len() > 512
            && let Some(expired) = inner.seen_order.pop_front()
        {
            inner.seen.remove(&expired);
        }
        inner.assistant_seen = true;
        self.log("assistant", &activity::prefix_utf8(text, MAXIMUM_MESSAGE_BYTES));
    }

    fn turn(&self, inner: &mut Inner, summary: &str) {
        let value = activity::make(&self.namespace, &self.provider, "turn", "running", summary);
        if inner.last_turn.as_ref() == Some(&value) {
            return;
        }
        inner.last_turn = Some(value.clone());
        if let Some(activity) = &self.hooks.activity {
            activity(value);
        }
    }

    fn tool_activity(&self, value: AgentActivity, tool_id: Option<&str>) {
        if self.hooks.graph_activity.as_ref().is_some_and(|graph| graph(&value, tool_id)) {
            return;
        }
        if let Some(activity) = &self.hooks.activity {
            activity(value);
        }
    }

    #[allow(clippy::too_many_arguments)]
    fn tool(
        &self,
        inner: &mut Inner,
        raw_id: Option<&str>,
        state: &str,
        name: Option<&str>,
        input: Option<&Value>,
        output: Option<String>,
        summary: Option<&str>,
    ) {
        let Some(raw_id) = raw_id.filter(|r| !r.is_empty() && r.len() <= 512) else { return };
        let id = activity::id(&self.namespace, raw_id);
        let previous = inner.activities.get(&id).cloned();
        if previous.as_ref().is_some_and(|p| p.state == "waiting")
            && state == "running"
            && inner.permission_states.get(&id).map(String::as_str) == Some("waiting")
        {
            return;
        }
        if previous.as_ref().is_some_and(|p| terminal(&p.state) && !terminal(state) || p.state == "error" && state == "completed") {
            return;
        }
        let tool =
            name.map(str::to_string).or_else(|| previous.as_ref().and_then(|p| p.tool_name.clone())).unwrap_or_else(|| "Tool".into());
        let kept_summary = if matches!(input, None | Some(Value::Null)) { previous.as_ref().map(|p| p.summary.clone()) } else { None };
        let selected = summary.map(str::to_string).or(kept_summary).unwrap_or_else(|| activity::summary(&tool, input));
        let mut duration = None;
        if terminal(state) {
            duration = previous.as_ref().and_then(|p| p.duration_ms);
            if let Some(start) = inner.starts.remove(&id)
                && duration.is_none()
            {
                let ms = self.now() - start;
                if valid_duration(ms) {
                    duration = Some(ms);
                }
            }
        } else if !inner.starts.contains_key(&id) {
            let now = self.now();
            if now.is_finite() && now >= 0.0 {
                inner.starts.insert(id.clone(), now);
            }
        }
        let candidate = AgentActivity {
            id: id.clone(),
            provider: self.provider.clone(),
            kind: activity::kind(&tool).into(),
            state: state.into(),
            summary: selected,
            tool_name: Some(tool.clone()),
            output: output.or_else(|| previous.as_ref().and_then(|p| p.output.clone())),
            duration_ms: duration,
        };
        let Some(value) = normalize::activity(&candidate, false) else { return };
        if previous.as_ref() == Some(&value) {
            return;
        }
        if previous.is_none() {
            inner.activity_order.push_back(id.clone());
            if inner.activity_order.len() > 512
                && let Some(expired) = inner.activity_order.pop_front()
            {
                inner.activities.remove(&expired);
                inner.starts.remove(&expired);
                inner.sequences.remove(&expired);
                inner.permission_states.remove(&expired);
            }
        }
        inner.activities.insert(id, value.clone());
        self.tool_activity(value, Some(raw_id));
    }

    /// `PermissionActivity`: once the stdio approval is seen it owns the waiting state of that tool call's row.
    pub fn permission_activity(&self, request: &ToolPermissionRequest, state: &str) {
        let mut inner = lock(&self.inner);
        if inner.closed || request.tool_use_id.is_empty() || request.tool_use_id.len() > 512 {
            return;
        }
        inner.permission_states.insert(activity::id(&self.namespace, &request.tool_use_id), state.into());
        self.tool(&mut inner, Some(&request.tool_use_id), state, Some(&request.tool_name), None, None, Some(&request.summary));
    }

    /// `ReceiveMod`: a Mods event of the run (usage, turns and tool rows); only the root stream owns the resume id.
    pub fn receive_mod(&self, root: &Value) {
        let mut inner = lock(&self.inner);
        if self.provider != "claude" || inner.closed {
            return;
        }
        inner.usage.consume_mod(root);
        let kind = meta::text(root, "event");
        let claude = [("name", "Claude")];
        if kind == Some("turn.start") && meta::text(root, "agentId").is_none() {
            self.turn(&mut inner, &strings::get_with("run.turn.generating", &claude));
            return;
        }
        if kind == Some("turn.complete") {
            if meta::text(root, "agentId").is_none() {
                self.turn(&mut inner, &strings::get_with("run.turn.finishing", &claude));
            }
            return;
        }
        if !matches!(kind, Some("tool.call" | "tool.waiting" | "tool.complete")) {
            return;
        }
        let (Some(raw_id), Some(name)) = (meta::text(root, "toolUseId"), meta::text(root, "tool")) else { return };
        let id = activity::id(&self.namespace, raw_id);
        if let Some(sequence) = meta::safe_integer(root, "sequence") {
            if inner.sequences.get(&id).is_some_and(|old| *old >= sequence) {
                return;
            }
            inner.sequences.insert(id.clone(), sequence);
        }
        let summary = meta::text(root, "summary");
        match kind {
            Some("tool.call") => {
                let state = if name == "AskUserQuestion" { "waiting" } else { "running" };
                self.tool(&mut inner, Some(raw_id), state, Some(name), None, None, summary);
            }
            // The stdio approval stays authoritative after an answer, even if its best-effort Mod arrives later.
            Some("tool.waiting") if !inner.permission_states.contains_key(&id) => {
                self.tool(&mut inner, Some(raw_id), "waiting", Some(name), None, None, summary)
            }
            Some("tool.complete") => {
                let state = if meta::flag(root, "isError") { "error" } else { "completed" };
                self.tool(&mut inner, Some(raw_id), state, Some(name), None, meta::text(root, "output").map(str::to_string), summary);
            }
            _ => {}
        }
    }

    /// `FinishActivities`: the run ended; every row still running or waiting ends stopped (or with an error).
    pub fn finish_activities(&self, stopped: bool) {
        let mut inner = lock(&self.inner);
        inner.closed = true;
        let order: Vec<String> = inner.activity_order.iter().cloned().collect();
        for id in order {
            let Some(previous) = inner.activities.get(&id).cloned() else { continue };
            if !matches!(previous.state.as_str(), "running" | "waiting") {
                continue;
            }
            let mut duration = previous.duration_ms;
            if let Some(start) = inner.starts.remove(&id)
                && valid_duration(self.now() - start)
            {
                duration = Some(self.now() - start);
            }
            let value = AgentActivity {
                state: if stopped { "stopped" } else { "error" }.into(),
                output: previous.output.clone().or_else(|| Some(strings::get("run.tool.endedBeforeResult"))),
                duration_ms: duration,
                ..previous
            };
            inner.activities.insert(id, value.clone());
            self.tool_activity(value, None);
        }
    }

    /// `Parse`: one stdout line.
    pub fn parse(&self, line: &str) {
        if line.trim().is_empty() {
            return;
        }
        let mut inner = lock(&self.inner);
        if inner.closed {
            return;
        }
        let Ok(root) = serde_json::from_str::<Value>(line) else {
            drop(inner);
            self.log("output", line);
            return;
        };
        let kind = meta::text(&root, "type");
        inner.usage.consume(&root);
        match self.provider.as_str() {
            "claude" => self.claude(&mut inner, &root, kind),
            "codex" => self.codex(&mut inner, &root, kind),
            "pi" => {
                let later = self.pi(&mut inner, &root, kind);
                drop(inner);
                self.pi_later(later);
            }
            _ => {
                if let Some(mut other) = inner.other.take() {
                    let mut resumed: Vec<String> = Vec::new();
                    let mut failed = inner.failed;
                    let log = |kind: &str, text: &str| self.log(kind, text);
                    let mut resume = |id: &str| resumed.push(id.to_string());
                    other.frame(&root, &mut StreamSinks { log: &log, resume: &mut resume, failed: &mut failed });
                    inner.failed = failed;
                    inner.other = Some(other);
                    for id in resumed {
                        self.resume(&mut inner, Some(&id));
                    }
                }
            }
        }
    }

    fn error_text(value: Option<&Value>, fallback: &str) -> String {
        match value {
            Some(Value::String(text)) => text.clone(),
            Some(value) => meta::text(value, "message").unwrap_or(fallback).to_string(),
            None => fallback.to_string(),
        }
    }

    fn claude(&self, inner: &mut Inner, root: &Value, kind: Option<&str>) {
        let child = !meta::top_level(root);
        let parent_tool_id = meta::text(root, "parent_tool_use_id");
        if !child {
            self.resume(inner, meta::text(root, "session_id"));
        }
        let message = meta::prop(root, "message");
        let blocks = message.and_then(|m| meta::prop(m, "content")).and_then(Value::as_array);
        let claude = [("name", "Claude")];
        if kind == Some("assistant")
            && let Some(blocks) = blocks
        {
            let key = meta::text(root, "uuid").or_else(|| meta::text_of(message, "id"));
            if !child {
                let text: Vec<&str> =
                    blocks.iter().filter(|b| meta::text(b, "type") == Some("text")).map(|b| meta::text(b, "text").unwrap_or("")).collect();
                self.assistant(inner, &text.join("\n"), key);
            }
            for block in blocks.iter().filter(|b| meta::text(b, "type") == Some("tool_use")) {
                let name = meta::text(block, "name");
                let state = if name == Some("AskUserQuestion") { "waiting" } else { "running" };
                self.tool(inner, meta::text(block, "id"), state, name, meta::prop(block, "input"), None, None);
            }
            let payloads: Vec<(String, String)> =
                blocks.iter().filter(|b| meta::text(b, "type") == Some("image")).flat_map(|b| image_payloads(Some(b), 0)).collect();
            self.images(inner, payloads, key.unwrap_or("message"), None, child, parent_tool_id);
        }
        if kind == Some("user")
            && let Some(blocks) = blocks
        {
            if self.hooks.file_original.is_some() {
                self.edited_original(inner, root, blocks);
            }
            for block in blocks.iter().filter(|b| meta::text(b, "type") == Some("tool_result")) {
                let state = if meta::flag(block, "is_error") { "error" } else { "completed" };
                let tool_id = meta::text(block, "tool_use_id");
                self.tool(inner, tool_id, state, None, None, activity::output(meta::prop(block, "content")), None);
                if let Some(tool_id) = tool_id.filter(|t| !t.is_empty() && t.len() <= 512) {
                    let payloads = image_payloads(meta::prop(block, "content"), 0);
                    self.images(inner, payloads, tool_id, Some(tool_id), child, parent_tool_id);
                }
            }
        }
        let subtype = meta::text(root, "subtype");
        if kind == Some("system") && subtype == Some("permission_denied") {
            let output = Self::error_text(meta::prop(root, "message"), &strings::get("run.tool.permissionDenied"));
            self.tool(inner, meta::text(root, "tool_use_id"), "error", meta::text(root, "tool_name"), None, Some(output), None);
        }
        // The CLI summarized its own context mid-turn: the transcript says so.
        if kind == Some("system") && subtype == Some("compact_boundary") && !child {
            self.log("system", &format!("{} · {}", compaction::title(), compaction::claude_summary(meta::prop(root, "compact_metadata"))));
        }
        if kind == Some("result") && !child && !stream::is_notification_result(root) {
            if self.suppress_interrupted_result.load(Ordering::SeqCst) && subtype == Some("error_during_execution") {
            } else if meta::flag(root, "is_error") || subtype.is_some_and(|s| s.starts_with("error")) {
                inner.failed = true;
                let text = match meta::prop(root, "errors") {
                    Some(Value::Array(errors)) => {
                        let fallback = strings::get("run.activity.error");
                        errors.iter().map(|e| Self::error_text(Some(e), &fallback)).collect::<Vec<_>>().join("\n")
                    }
                    _ => meta::text(root, "result")
                        .map(str::to_string)
                        .unwrap_or_else(|| strings::get_with("run.error.providerRun", &claude)),
                };
                self.log("error", &text);
            } else if !inner.assistant_seen {
                let result = meta::text(root, "result").unwrap_or("").to_string();
                self.assistant(inner, &result, None);
            }
            self.turn(inner, &strings::get_with("run.turn.finishing", &claude));
        }
    }

    fn codex(&self, inner: &mut Inner, root: &Value, kind: Option<&str>) {
        let codex = [("name", "Codex")];
        if kind == Some("thread.started") {
            self.resume(inner, meta::text(root, "thread_id"));
        }
        if matches!(kind, Some("turn.started" | "turn.completed")) {
            let key = if kind == Some("turn.started") { "run.turn.generating" } else { "run.turn.finishing" };
            self.turn(inner, &strings::get_with(key, &codex));
        }
        if matches!(kind, Some("item.started" | "item.updated" | "item.completed"))
            && let Some(item) = meta::prop(root, "item").filter(|i| i.is_object())
        {
            let ended = kind == Some("item.completed");
            let state = if meta::text(item, "status") == Some("failed") {
                "error"
            } else if ended {
                "completed"
            } else {
                "running"
            };
            match meta::text(item, "type") {
                Some("agent_message") => {
                    if ended {
                        let key = mighty_core::wire::clean(meta::text(item, "id").unwrap_or(""), 128);
                        self.assistant(inner, meta::text(item, "text").unwrap_or(""), Some(&key));
                    }
                }
                Some("command_execution") => {
                    let output = if ended { meta::text(item, "aggregated_output").map(str::to_string) } else { None };
                    self.tool(inner, meta::text(item, "id"), state, Some("command_execution"), Some(item), output, None);
                    if self.hooks.activity.is_none()
                        && ended
                        && let Some(output) = meta::text(item, "aggregated_output").filter(|o| !o.is_empty())
                    {
                        self.log("output", output);
                    }
                }
                Some(kind @ ("file_change" | "web_search")) => {
                    self.tool(inner, meta::text(item, "id"), state, Some(kind), Some(item), None, None)
                }
                Some("context_compaction") => {
                    if ended {
                        self.log("system", &format!("{} · {}", compaction::title(), compaction::codex_summary()));
                    }
                }
                Some("mcp_tool_call") => {
                    let name = [meta::text(item, "server"), meta::text(item, "tool")]
                        .into_iter()
                        .flatten()
                        .filter(|v| !v.is_empty())
                        .collect::<Vec<_>>()
                        .join(".");
                    let result = if meta::prop(item, "error").is_some() { meta::prop(item, "error") } else { meta::prop(item, "result") };
                    let name = if name.is_empty() { "MCP".to_string() } else { name };
                    self.tool(
                        inner,
                        meta::text(item, "id"),
                        state,
                        Some(&name),
                        meta::prop(item, "arguments"),
                        activity::output(result),
                        None,
                    );
                    if ended && let Some(id) = meta::text(item, "id").filter(|i| !i.is_empty() && i.len() <= 512) {
                        let payloads = image_payloads(meta::prop(item, "result"), 0);
                        self.images(inner, payloads, id, Some(id), false, None);
                    }
                }
                Some("image_generation") => {
                    if ended
                        && let Some(id) = meta::text(item, "id").filter(|i| !i.is_empty() && i.len() <= 512)
                        && let Some(result) = meta::text(item, "result").filter(|r| !r.is_empty())
                    {
                        self.images(inner, vec![("image/png".into(), result.into())], id, None, false, None);
                    }
                }
                Some("error") => {
                    let text = meta::text(item, "message")
                        .map(str::to_string)
                        .unwrap_or_else(|| strings::get_with("run.error.providerTask", &codex));
                    self.log("error", &text);
                }
                _ => {}
            }
        }
        if matches!(kind, Some("turn.failed" | "error")) {
            inner.failed = true;
            let fallback = strings::get_with("run.error.providerRun", &codex);
            let text = match meta::prop(root, "error") {
                Some(error) => Self::error_text(Some(error), &fallback),
                None => meta::text(root, "message").map(str::to_string).unwrap_or(fallback),
            };
            self.log("error", &text);
        }
    }

    /// `EditedOriginal`: an Edit, Write or MultiEdit result that did not fail, for the file originals.
    fn edited_original(&self, inner: &Inner, root: &Value, blocks: &[Value]) {
        let result = meta::prop(root, "tool_use_result")
            .filter(|r| r.is_object())
            .or_else(|| meta::prop(root, "toolUseResult").filter(|r| r.is_object()));
        let Some(result) = result else { return };
        let Some(path) = meta::text(result, "filePath").filter(|p| !p.is_empty()) else { return };
        let tool = blocks.iter().find_map(|block| {
            if meta::text(block, "type") != Some("tool_result") || meta::flag(block, "is_error") {
                return None;
            }
            let id = meta::text(block, "tool_use_id").filter(|i| !i.is_empty() && i.len() <= 512)?;
            let call = inner.activities.get(&activity::id(&self.namespace, id))?;
            call.tool_name.as_deref().filter(|t| matches!(*t, "Edit" | "Write" | "MultiEdit")).map(str::to_string)
        });
        if let (Some(tool), Some(hook)) = (tool, &self.hooks.file_original) {
            hook(path, &tool, result);
        }
    }

    fn images(
        &self,
        inner: &mut Inner,
        payloads: Vec<(String, String)>,
        key: &str,
        tool_id: Option<&str>,
        child: bool,
        parent_tool_id: Option<&str>,
    ) {
        let Some(store) = self.hooks.images.clone() else { return };
        if payloads.is_empty() {
            return;
        }
        let previous = tool_id.and_then(|t| inner.activities.get(&activity::id(&self.namespace, t))).cloned();
        let mut parts: Vec<String> = Vec::new();
        for part in
            [previous.as_ref().and_then(|p| p.tool_name.clone()), previous.as_ref().map(|p| p.summary.clone())].into_iter().flatten()
        {
            if !part.is_empty() && !parts.contains(&part) {
                parts.push(part);
            }
        }
        let source = parts.join(" · ");
        let read_path = previous
            .as_ref()
            .filter(|p| matches!(p.tool_name.as_deref().map(str::to_lowercase).as_deref(), Some("read" | "read_file")))
            .and_then(|p| (p.summary.starts_with('/') || normalize::fully_qualified(&p.summary)).then(|| p.summary.clone()));
        let label = if source.is_empty() { strings::get("images.source.agent") } else { source.clone() };
        let mut refs = Vec::new();
        let mut failure = None;
        for (media, base64) in payloads.iter().take(MAXIMUM_IMAGES_PER_ENTRY) {
            if inner.stored_images >= MAXIMUM_IMAGES_PER_RUN {
                if !inner.image_limit_noted && !child {
                    inner.image_limit_noted = true;
                    self.log("system", &strings::get_with("images.tooMany", &[("count", &MAXIMUM_IMAGES_PER_RUN.to_string())]));
                }
                break;
            }
            match store.prepare(base64, media, &label, read_path.as_deref()) {
                Ok(value) => {
                    refs.push(value);
                    inner.stored_images += 1;
                }
                Err(error) => failure = Some(error),
            }
        }
        if let Some(error) = failure
            && !child
        {
            self.log("system", &image_failure_text(error));
        }
        if refs.is_empty() {
            return;
        }
        let hashes: String = refs.iter().map(|r| r.hash.clone().unwrap_or_default()).collect();
        let entry = LogEntry {
            id: Some(activity::id(&self.namespace, &format!("image:{key}:{hashes}"))),
            kind: Some("image".into()),
            text: Some(image_entry_text(refs.len(), &label)),
            timestamp: Some(time::now_o()),
            provider: Some(self.provider.clone()),
            activity: None,
            images: Some(refs.into_iter().map(Some).collect()),
        };
        if self.hooks.graph_images.as_ref().is_some_and(|graph| graph(&entry, tool_id, parent_tool_id)) {
            return;
        }
        if let Some(hook) = &self.hooks.image_entry {
            hook(entry);
        }
    }

    /// `Flush`: a streamed reply still held (Antigravity's deltas) goes out.
    pub fn flush(&self) {
        let mut inner = lock(&self.inner);
        let text = inner.pending.trim_end().to_string();
        if !text.is_empty() {
            self.log("assistant", &text);
        }
        if inner.pending_truncated {
            self.log("system", &strings::get("run.notice.messageTruncated"));
        }
        inner.pending.clear();
        inner.pending_truncated = false;
        // An error pi gave up on is the run's failure; one a retry answered was cleared.
        if let Some(error) = inner.pi.take_error() {
            inner.failed = true;
            self.log("error", &error);
        }
    }
}
