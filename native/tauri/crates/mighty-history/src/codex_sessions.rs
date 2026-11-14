//! C# `CodexSessionThread` and `CodexSessionWatcher` (CodexSessionRecords.cs): a Codex run's sub-agents read live from the CLI's own
//! session files (`$CODEX_HOME/sessions/yyyy/MM/dd/rollout-*.jsonl`) while the run goes on, so their work reaches the graph as it
//! happens (`ExecutionGraphTracker.CodexSession`). Only proven descendants of the run's root thread are read, only what they wrote since
//! the run started, never a fork's inherited history; every read is byte-bounded and confined below the resolved sessions folder, and
//! no file is followed through a link.

use std::collections::{HashMap, HashSet, VecDeque};
use std::fs::File;
use std::path::{Path, PathBuf};

use mighty_core::graph::GraphResponseRecord;
use mighty_core::model::{AgentActivity, LogEntry};
use mighty_core::time::{self, DateTimeOffset};
use mighty_core::workspace_files;
use mighty_graph::node::{self, MAXIMUM_ENTRIES, MAXIMUM_INPUT_BYTES, MAXIMUM_OUTPUT_BYTES};
use mighty_graph::tracker::{CodexCollaborationItem, CodexSessionAgent, codex_key, codex_usage};
use mighty_run::activity;
use serde_json::Value;

use crate::files;
use crate::replay;

/// `CodexSessionThread.ThreadID`: a thread id as `Guid.ToString("D")` writes it, or None.
pub fn thread_id(value: Option<&str>) -> Option<String> {
    let value = value?.trim();
    let bytes = value.as_bytes();
    let shaped = bytes.len() == 36
        && bytes.iter().enumerate().all(|(at, b)| if matches!(at, 8 | 13 | 18 | 23) { *b == b'-' } else { b.is_ascii_hexdigit() });
    shaped.then(|| value.to_ascii_lowercase())
}

/// `CodexSessionThread.Meta`: a spawned sub-agent's `session_meta` record.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Meta {
    pub thread: String,
    pub parent: String,
    pub nickname: Option<String>,
    pub path: Option<String>,
    pub start_ordinal: Option<i64>,
    pub forked: bool,
}

fn prop<'a>(value: Option<&'a Value>, key: &str) -> Option<&'a Value> {
    value.and_then(|v| v.as_object()).and_then(|o| o.get(key))
}

fn text<'a>(value: Option<&'a Value>, key: &str) -> Option<&'a str> {
    prop(value, key).and_then(Value::as_str)
}

fn integer(value: Option<&Value>, key: &str) -> Option<i64> {
    prop(value, key).and_then(Value::as_i64)
}

fn absent_or_null(value: Option<&Value>, key: &str) -> bool {
    matches!(prop(value, key), None | Some(Value::Null))
}

/// `CodexSessionThread.Metadata`: the record's sub-agent, when it is one a parent spawned (never the thread itself, never a guardian).
pub fn metadata(record: &Value) -> Option<Meta> {
    let record = Some(record);
    if text(record, "type") != Some("session_meta") {
        return None;
    }
    let p = prop(record, "payload");
    let spawn = prop(prop(prop(p, "source"), "subagent"), "thread_spawn").filter(|s| s.is_object());
    let thread = thread_id(text(p, "id"))?;
    let spawn = spawn?;
    let parent = thread_id(text(p, "parent_thread_id").or_else(|| text(Some(spawn), "parent_thread_id")))?;
    if parent == thread {
        return None;
    }
    Some(Meta {
        nickname: codex_key(text(p, "agent_nickname").or_else(|| text(Some(spawn), "agent_nickname"))).map(str::to_string),
        path: codex_key(text(p, "agent_path").or_else(|| text(Some(spawn), "agent_path"))).map(str::to_string),
        start_ordinal: integer(p, "subagent_history_start_ordinal"),
        forked: !absent_or_null(p, "forked_from_id"),
        thread,
        parent,
    })
}

fn clean(value: Option<&str>, maximum: usize) -> Option<String> {
    Some(activity::clean(value, maximum, false)).filter(|v| !v.is_empty())
}

/// `Stamp`: the record's own time when it reads as one, else now.
fn stamp(record: &Value) -> String {
    match text(Some(record), "timestamp") {
        Some(value) if time::parse(Some(value)).is_some() => value.to_string(),
        _ => time::now_o(),
    }
}

/// `Text(content)`: the message parts' text, never a sealed (`gAAAA…`) body.
fn content_text(content: Option<&Value>) -> Option<String> {
    let Some(Value::Array(parts)) = content else { return None };
    let joined: Vec<&str> = parts
        .iter()
        .take(32)
        .filter(|p| matches!(text(Some(p), "type"), Some("input_text" | "output_text" | "text" | "Text")))
        .filter_map(|p| text(Some(p), "text"))
        .collect();
    let joined = joined.join("\n");
    (!joined.is_empty() && !joined.starts_with("gAAAA")).then_some(joined)
}

/// `Envelope`: a message that is only a header (`Sender: parent` … `Message:` with nothing after it).
fn envelope(text: &str) -> bool {
    let lines: Vec<&str> = text.trim_end_matches('\n').split('\n').collect();
    if lines.len() < 2 || lines.len() > 12 {
        return false;
    }
    let last = lines[lines.len() - 1];
    if !last.contains(':') || !last.split_once(':').is_some_and(|(_, rest)| rest.trim().is_empty()) {
        return false;
    }
    lines.iter().all(|line| {
        let Some(colon) = line.find(':') else { return false };
        let units = line[..colon].encode_utf16().count();
        units > 0
            && units <= 32
            && line.chars().next().is_some_and(char::is_uppercase)
            && line[..colon].chars().all(|c| c.is_alphabetic() || c == ' ')
    })
}

/// C# `CodexSessionThread`: one sub-agent's record as read so far.
pub struct CodexSessionThread {
    pub metadata: Meta,
    namespace: String,
    since: DateTimeOffset,
    state_only: bool,
    own: bool,
    active: bool,
    dirty: bool,
    failed: bool,
    input: Option<String>,
    output: Option<String>,
    model: Option<String>,
    last_answer: Option<String>,
    state: String,
    turns: i32,
    errors: i64,
    title: Option<String>,
    entries: Vec<LogEntry>,
    changed: HashSet<String>,
    answers: HashSet<String>,
    responses: HashSet<String>,
    usage: Vec<GraphResponseRecord>,
}

impl CodexSessionThread {
    pub fn new(metadata: Meta, namespace: &str, started: DateTimeOffset) -> CodexSessionThread {
        let state_only = metadata.forked && metadata.start_ordinal.is_none();
        let last_path = metadata.path.as_deref().and_then(|p| p.split('/').next_back()).map(str::to_string);
        let parts: Vec<String> = [metadata.nickname.clone(), last_path].into_iter().flatten().filter(|v| !v.is_empty()).collect();
        CodexSessionThread {
            title: clean(Some(&parts.join(" · ")), 160),
            since: DateTimeOffset::from_utc_ticks(started.utc_ticks() - 10_000_000),
            state_only,
            own: !state_only,
            active: false,
            dirty: true,
            failed: false,
            input: None,
            output: None,
            model: None,
            last_answer: None,
            state: "running".into(),
            turns: 0,
            errors: 0,
            namespace: namespace.into(),
            metadata,
            entries: vec![],
            changed: HashSet::new(),
            answers: HashSet::new(),
            responses: HashSet::new(),
            usage: vec![],
        }
    }

    fn id(&self, key: &str) -> String {
        node::identifier(&self.namespace, &format!("codex-session:{}:{key}", self.metadata.thread))
    }

    fn upsert(&mut self, mut entry: LogEntry) {
        match self.entries.iter().position(|e| e.id == entry.id) {
            Some(at) => {
                entry.timestamp = self.entries[at].timestamp.clone();
                if self.entries[at] == entry {
                    return;
                }
                self.entries[at] = entry.clone();
            }
            None => self.entries.push(entry.clone()),
        }
        while self.entries.len() > MAXIMUM_ENTRIES {
            let first = self.entries.remove(0);
            self.changed.remove(first.id());
            self.answers.remove(first.id());
        }
        self.changed.insert(entry.id().to_string());
        self.dirty = true;
    }

    fn entry(id: String, kind: &str, text: String, record: &Value) -> LogEntry {
        LogEntry::new(id, kind, text, stamp(record), Some("codex".into()))
    }

    /// `Consume`: one record of the sub-agent's file.
    pub fn consume(&mut self, record: &Value) {
        let r = Some(record);
        let p = prop(r, "payload").filter(|p| p.is_object());
        let (Some(kind_of_record), Some(payload)) = (text(r, "type"), p) else { return };
        let p = Some(payload);
        let kind = text(p, "type");
        if let Some(start) = self.metadata.start_ordinal {
            if integer(r, "ordinal").is_none_or(|ordinal| ordinal < start) {
                return;
            }
        } else if !self.own {
            let settings = kind_of_record == "event_msg" && kind == Some("thread_settings_applied");
            let addressed = kind_of_record == "response_item"
                && kind == Some("agent_message")
                && self.metadata.path.is_some()
                && text(p, "recipient") == self.metadata.path.as_deref();
            if !settings && !addressed {
                return;
            }
            self.own = true;
        }
        if !self.active {
            let begun = time::parse(text(r, "timestamp")).filter(|date| *date >= self.since);
            if kind_of_record != "event_msg" || kind != Some("task_started") || begun.is_none() {
                return;
            }
            self.active = true;
        }
        if self.state_only && (kind_of_record != "event_msg" || !matches!(kind, Some("task_started" | "task_complete" | "turn_aborted"))) {
            return;
        }
        match (kind_of_record, kind) {
            ("turn_context", _) => self.model = clean(codex_key(text(p, "model")), 160),
            ("token_usage_record", _) => {
                if let Some(response) = codex_key(text(p, "response_id"))
                    && self.responses.len() < 4096
                    && !self.responses.contains(response)
                    && let Some(tokens) = codex_usage(prop(p, "usage"))
                {
                    self.responses.insert(response.to_string());
                    self.usage.push(GraphResponseRecord {
                        response_id: Some(response.to_string()),
                        model: self.model.clone(),
                        usage: Some(tokens),
                        activity_ids: Some(vec![]),
                        marked_as_configured: false,
                    });
                    self.dirty = true;
                }
            }
            ("event_msg", Some("task_started")) => {
                if let Some(output) = self.output.clone().filter(|o| !o.is_empty()) {
                    let id = self.id(&format!("answer:{}", self.turns));
                    self.upsert(Self::entry(id.clone(), "assistant", output, record));
                    self.answers.insert(id);
                }
                self.turns += 1;
                self.state = "running".into();
                self.output = None;
                self.last_answer = None;
                self.failed = false;
                self.dirty = true;
            }
            ("event_msg", Some("task_complete")) => {
                self.failed = false;
                if !self.state_only {
                    self.output = clean(text(p, "last_agent_message"), MAXIMUM_OUTPUT_BYTES).or_else(|| self.last_answer.clone());
                }
                self.state = "completed".into();
                self.dirty = true;
            }
            ("event_msg", Some("turn_aborted")) => {
                self.failed = false;
                self.state = "stopped".into();
                self.dirty = true;
            }
            ("event_msg", Some("error")) => {
                if let Some(error) = clean(text(p, "message"), MAXIMUM_OUTPUT_BYTES) {
                    self.failed = true;
                    self.errors += 1;
                    let key = integer(r, "ordinal").unwrap_or(self.errors);
                    let id = self.id(&format!("error:{key}"));
                    self.upsert(Self::entry(id, "error", error, record));
                }
            }
            ("event_msg", Some("item_completed")) => self.item(prop(p, "item"), payload, record),
            ("response_item", Some("agent_message")) => {
                if self.metadata.path.is_some()
                    && text(p, "recipient") == self.metadata.path.as_deref()
                    && let Some(message) = content_text(prop(p, "content"))
                    && !envelope(&message)
                {
                    let cleaned = clean(Some(&message), MAXIMUM_INPUT_BYTES);
                    if self.input.is_none() {
                        self.input = cleaned;
                        self.dirty = true;
                    } else if let (Some(id), Some(cleaned)) = (codex_key(text(p, "id")), cleaned) {
                        let id = self.id(&format!("input:{id}"));
                        self.upsert(Self::entry(id, "user", cleaned, record));
                    }
                }
            }
            ("response_item", Some("message")) => {
                let kinds = prop(prop(p, "internal_chat_message_metadata_passthrough"), "content_item_kinds");
                let typed = match kinds {
                    None => true,
                    Some(Value::Array(list)) => !list.is_empty() && list.iter().all(|k| k.as_str() == Some("user.text")),
                    _ => false,
                };
                if self.input.is_none()
                    && text(p, "role") == Some("user")
                    && !matches!(prop(prop(r, "metadata"), "inherited_user_message"), Some(Value::Bool(true)))
                    && typed
                    && let Some(human) = content_text(prop(p, "content")).filter(|h| !h.starts_with('<'))
                {
                    self.input = clean(Some(&human), MAXIMUM_INPUT_BYTES);
                    self.dirty = true;
                }
            }
            _ => {}
        }
    }

    fn item(&mut self, item: Option<&Value>, payload: &Value, record: &Value) {
        let (Some(id), Some(kind)) = (codex_key(text(item, "id")), text(item, "type")) else { return };
        let id = id.to_string();
        if kind == "AgentMessage" {
            let Some(message) = clean(content_text(prop(item, "content")).as_deref(), MAXIMUM_OUTPUT_BYTES) else { return };
            if text(item, "phase") == Some("final_answer") {
                self.output = Some(message.clone());
                self.last_answer = Some(message);
                self.dirty = true;
            } else {
                let entry_id = self.id(&format!("message:{id}"));
                self.upsert(Self::entry(entry_id, "assistant", message, record));
            }
            return;
        }
        let Some(item_value) = item else { return };
        let mapped = replay::codex_item(item).unwrap_or_else(|| item_value.clone());
        let mut arguments = Some(&mapped);
        let mut result: Option<String> = None;
        let mut bad = matches!(text(item, "status"), Some("failed" | "declined"));
        let tool = match kind {
            "CommandExecution" => {
                result = text(Some(&mapped), "aggregated_output").map(str::to_string);
                "command_execution".to_string()
            }
            "FileChange" => "file_change".to_string(),
            "McpToolCall" => {
                let named: Vec<&str> = [text(item, "server"), text(item, "tool")].into_iter().flatten().collect();
                arguments = prop(item, "arguments");
                let value = prop(item, "result");
                if absent_or_null(item, "error") {
                    result = activity::output(value);
                } else {
                    bad = true;
                    result = activity::output(prop(item, "error"));
                }
                bad |= matches!(prop(value, "isError"), Some(Value::Bool(true)));
                if named.is_empty() { "MCP".to_string() } else { named.join(".") }
            }
            "Extension" => {
                if text(item, "kind") == Some("web.search") {
                    "web_search".to_string()
                } else {
                    clean(text(item, "kind"), 80).unwrap_or_else(|| "Extension".into())
                }
            }
            "ImageView" => "view_image".to_string(),
            "CollabAgentToolCall" => match CodexCollaborationItem::parse(&mapped) {
                Some(call) => call.tool,
                None => return,
            },
            _ => return,
        };
        let duration = match (integer(Some(payload), "started_at_ms"), integer(Some(payload), "completed_at_ms")) {
            (Some(started), Some(ended)) if ended >= started => Some((ended - started) as f64),
            _ => None,
        };
        let state = if text(item, "status") == Some("in_progress") {
            "running"
        } else if bad {
            "error"
        } else {
            "completed"
        };
        let candidate = AgentActivity {
            id: activity::id(&self.namespace, &format!("codex-session:{}:{id}", self.metadata.thread)),
            provider: "codex".into(),
            kind: activity::kind(&tool).into(),
            state: state.into(),
            summary: activity::summary(&tool, arguments),
            tool_name: Some(tool),
            output: result,
            duration_ms: duration,
        };
        if let Some(value) = activity::normalized(&candidate, false) {
            let mut entry = Self::entry(value.id.clone(), "system", value.summary.clone(), record);
            entry.activity = Some(value);
            self.upsert(entry);
        }
    }

    /// `Close`: the run ended; a sub-agent still running after an error failed.
    pub fn close(&mut self) {
        if self.active && self.failed && self.state == "running" {
            self.state = "error".into();
            self.dirty = true;
        }
    }

    /// `Take`: what changed since the last take, once the sub-agent started in this run.
    pub fn take(&mut self) -> Option<CodexSessionAgent> {
        if !self.active || !self.dirty {
            return None;
        }
        let entries: Vec<LogEntry> = self.entries.iter().filter(|e| self.changed.contains(e.id())).cloned().collect();
        let value = CodexSessionAgent {
            thread: self.metadata.thread.clone(),
            parent_thread: self.metadata.parent.clone(),
            title: self.title.clone(),
            input: self.input.clone(),
            entries,
            answers: self.answers.intersection(&self.changed).cloned().collect(),
            output: self.output.clone(),
            state: self.state.clone(),
            turns: self.turns,
            usage: std::mem::take(&mut self.usage),
        };
        self.dirty = false;
        self.changed.clear();
        Some(value)
    }
}

/// `CodexSessionWatcher.Limits`.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Limits {
    pub bytes_per_poll: usize,
    pub final_bytes: usize,
    pub maximum_line_bytes: usize,
    pub maximum_first_line_bytes: usize,
    pub first_lines_per_poll: usize,
    pub maximum_children: usize,
    pub maximum_waiting: usize,
}

impl Default for Limits {
    fn default() -> Limits {
        Limits {
            bytes_per_poll: 4 * 1024 * 1024,
            final_bytes: 32 * 1024 * 1024,
            maximum_line_bytes: 2 * 1024 * 1024,
            maximum_first_line_bytes: 1024 * 1024,
            first_lines_per_poll: 64,
            maximum_children: 128,
            maximum_waiting: 512,
        }
    }
}

struct Child {
    file: File,
    thread: CodexSessionThread,
    offset: i64,
    skipping: bool,
    closed: bool,
}

pub type Clock = Box<dyn Fn() -> DateTimeOffset + Send>;

/// C# `CodexSessionWatcher`: discovers the current run's descendants only.
pub struct CodexSessionWatcher {
    home: PathBuf,
    root: String,
    namespace: String,
    sessions: Option<PathBuf>,
    started: DateTimeOffset,
    now: Clock,
    limits: Limits,
    children: Vec<Child>,
    known: HashSet<String>,
    skipped: HashSet<String>,
    /// Files whose parent is not known yet, by name, in the order they were found: the path and the parent thread.
    waiting: Vec<(String, PathBuf, String)>,
    waiting_order: VecDeque<String>,
    next: usize,
    stopped: bool,
    /// `FirstLineReads`: how many files' first lines were read.
    pub first_line_reads: usize,
}

fn file_name(path: &Path) -> String {
    path.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default()
}

fn find(bytes: &[u8], from: usize, to: usize) -> Option<usize> {
    bytes[from..to].iter().position(|b| *b == b'\n')
}

impl CodexSessionWatcher {
    /// The watcher of `root_thread`'s descendants under `codex_home`, from the run's start; None for a root that is no thread id or
    /// limits beyond C#'s (its constructor throws there).
    pub fn new(
        codex_home: &Path,
        root_thread: &str,
        started: DateTimeOffset,
        namespace: &str,
        clock: Option<Clock>,
        limits: Option<Limits>,
    ) -> Option<CodexSessionWatcher> {
        let root = thread_id(Some(root_thread))?;
        let limits = limits.unwrap_or_default();
        let valid = limits.bytes_per_poll >= 1
            && limits.final_bytes >= 1
            && (1..=2 * 1024 * 1024).contains(&limits.maximum_line_bytes)
            && (1..=1024 * 1024).contains(&limits.maximum_first_line_bytes)
            && (1..=64).contains(&limits.first_lines_per_poll)
            && (1..=128).contains(&limits.maximum_children)
            && (1..=512).contains(&limits.maximum_waiting);
        if !valid {
            return None;
        }
        Some(CodexSessionWatcher {
            home: codex_home.to_path_buf(),
            root,
            namespace: namespace.into(),
            sessions: None,
            started,
            now: clock.unwrap_or_else(|| Box::new(DateTimeOffset::now)),
            limits,
            children: vec![],
            known: HashSet::new(),
            skipped: HashSet::new(),
            waiting: vec![],
            waiting_order: VecDeque::new(),
            next: 0,
            stopped: false,
            first_line_reads: 0,
        })
    }

    /// `Poll`: what changed since the last poll.
    pub fn poll(&mut self) -> Vec<CodexSessionAgent> {
        if self.stopped { vec![] } else { self.read(self.limits.bytes_per_poll, false) }
    }

    /// `Finish`: the last read (a larger budget, every sub-agent closed), after which nothing is read again.
    pub fn finish(&mut self) -> Vec<CodexSessionAgent> {
        if self.stopped {
            return vec![];
        }
        let last = self.read(self.limits.final_bytes, true);
        self.dispose();
        last
    }

    /// `Dispose`: every file let go.
    pub fn dispose(&mut self) {
        self.stopped = true;
        self.children.clear();
    }

    /// `DayFolders`: the run's days (its start's day before, up to today, at most three), newest first, each a real folder.
    fn day_folders(&self) -> Vec<PathBuf> {
        let Some(sessions) = &self.sessions else { return vec![] };
        let local = |at: DateTimeOffset| at.to_chrono().with_timezone(&chrono::Local).date_naive();
        let now = (self.now)();
        let first = local(self.started) - chrono::Days::new(1);
        let last = local(if now > self.started { now } else { self.started });
        let oldest = last - chrono::Days::new(2);
        let mut folders = vec![];
        let mut day = last;
        while day >= first && day >= oldest {
            let mut path = sessions.clone();
            let mut valid = true;
            for part in [day.format("%Y").to_string(), day.format("%m").to_string(), day.format("%d").to_string()] {
                path = path.join(part);
                if !files::real_directory(&path) {
                    valid = false;
                    break;
                }
            }
            if valid {
                folders.push(path);
            }
            day = day - chrono::Days::new(1);
        }
        folders
    }

    /// `Open`: a regular file whose real path lies below the sessions folder.
    fn open(&self, path: &Path) -> Option<File> {
        let sessions = self.sessions.as_ref()?;
        if !files::regular(path) || !workspace_files::real_path(path).is_some_and(|real| workspace_files::contains(&real, sessions)) {
            return None;
        }
        files::open(path)
    }

    /// `Header`: the file's first line as a sub-agent of `thread`, and where its records start; `incomplete` when the line is still
    /// being written.
    fn header(&mut self, file: &mut File, thread: &str, incomplete: &mut bool) -> Option<(Meta, i64)> {
        self.first_line_reads += 1;
        *incomplete = false;
        let length = files::length(file).max(0) as usize;
        let mut bytes = vec![0u8; length.min(self.limits.maximum_first_line_bytes + 1)];
        let count = files::read_at(file, &mut bytes, 0);
        let Some(end) = find(&bytes, 0, count) else {
            *incomplete = count <= self.limits.maximum_first_line_bytes;
            return None;
        };
        let root: Value = serde_json::from_slice(&bytes[..end]).ok()?;
        metadata(&root).filter(|meta| meta.thread == thread).map(|meta| (meta, end as i64 + 1))
    }

    fn waits(&self, name: &str) -> bool {
        self.waiting.iter().any(|(waiting, _, _)| waiting == name)
    }

    fn skip(&mut self, name: &str) {
        if self.skipped.len() >= 8192 {
            self.skipped.clear();
        }
        self.skipped.insert(name.to_string());
    }

    fn candidate(&mut self, path: &Path, thread: &str, recheck: bool, reads: &mut usize) {
        let name = file_name(path);
        if *reads >= self.limits.first_lines_per_poll
            || thread == self.root
            || self.known.contains(thread)
            || self.skipped.contains(&name)
            || !recheck && self.waits(&name)
        {
            return;
        }
        let Some(mut file) = self.open(path) else { return };
        let modified = file.metadata().and_then(|m| m.modified()).map(files::date_of).unwrap_or(DateTimeOffset::from_utc_ticks(0));
        if modified < self.started {
            return;
        }
        *reads += 1;
        let mut incomplete = false;
        let Some((meta, offset)) = self.header(&mut file, thread, &mut incomplete) else {
            if !incomplete {
                self.skip(&name);
            }
            return;
        };
        if meta.parent != self.root && !self.known.contains(&meta.parent) {
            if !self.waits(&name) {
                while self.waiting.len() >= self.limits.maximum_waiting
                    && let Some(old) = self.waiting_order.pop_front()
                {
                    self.waiting.retain(|(waiting, _, _)| *waiting != old);
                    self.skip(&old);
                }
                self.waiting.push((name.clone(), path.to_path_buf(), meta.parent.clone()));
                self.waiting_order.push_back(name);
            }
            return;
        }
        self.waiting.retain(|(waiting, _, _)| *waiting != name);
        if self.children.len() >= self.limits.maximum_children {
            self.skip(&name);
            return;
        }
        self.known.insert(thread.to_string());
        let thread = CodexSessionThread::new(meta, &self.namespace, self.started);
        self.children.push(Child { file, thread, offset, skipping: false, closed: false });
    }

    /// `Discover`: the day folders' rollouts whose first line names a descendant of the root (a grandchild found before its parent
    /// waits until the parent is known).
    fn discover(&mut self) {
        if self.sessions.is_none() {
            let path = self.home.join("sessions");
            self.sessions = Some(workspace_files::real_path(&path).unwrap_or(path));
        }
        if !self.sessions.as_deref().is_some_and(files::real_directory) {
            return;
        }
        let mut reads = 0;
        for folder in self.day_folders() {
            let Ok(entries) = std::fs::read_dir(&folder) else { continue };
            let paths: Vec<PathBuf> =
                entries.flatten().filter(|e| e.file_type().is_ok_and(|t| !t.is_dir())).map(|e| e.path()).take(16_384).collect();
            for path in paths {
                if reads >= self.limits.first_lines_per_poll {
                    break;
                }
                if let Some(thread) = thread_id(files::thread(&file_name(&path)).as_deref()) {
                    self.candidate(&path, &thread, false, &mut reads);
                }
            }
        }
        let mut previous = usize::MAX;
        while previous != self.known.len() && reads < self.limits.first_lines_per_poll {
            previous = self.known.len();
            let ready: Vec<PathBuf> =
                self.waiting.iter().filter(|(_, _, parent)| self.known.contains(parent)).map(|(_, path, _)| path.clone()).collect();
            for path in ready {
                if let Some(thread) = thread_id(files::thread(&file_name(&path)).as_deref()) {
                    self.candidate(&path, &thread, true, &mut reads);
                }
            }
        }
    }

    fn read(&mut self, budget: usize, closing: bool) -> Vec<CodexSessionAgent> {
        self.discover();
        let mut budget = budget;
        let count = self.children.len();
        let first = if count == 0 {
            0
        } else {
            let first = self.next % count;
            self.next += 1;
            first
        };
        let maximum_line = self.limits.maximum_line_bytes;
        for step in 0..count {
            if budget == 0 {
                break;
            }
            let child = &mut self.children[(first + step) % count];
            if child.closed {
                continue;
            }
            if files::length(&child.file) < child.offset {
                child.closed = true;
                continue;
            }
            while budget > 0 {
                let left = (files::length(&child.file) - child.offset).max(0) as usize;
                let capacity = (maximum_line + 1).min(left).min(budget);
                if capacity == 0 {
                    break;
                }
                let mut bytes = vec![0u8; capacity];
                let read = files::read_at(&mut child.file, &mut bytes, child.offset);
                if read == 0 {
                    break;
                }
                budget = budget.saturating_sub(read);
                let mut start = 0;
                if child.skipping {
                    let Some(end) = find(&bytes, 0, read) else {
                        child.offset += read as i64;
                        continue;
                    };
                    start = end + 1;
                    child.skipping = false;
                }
                while start < read {
                    let Some(relative) = find(&bytes, start, read) else { break };
                    if relative > 0
                        && relative <= maximum_line
                        && let Ok(record) = serde_json::from_slice::<Value>(&bytes[start..start + relative])
                    {
                        child.thread.consume(&record);
                    }
                    start += relative + 1;
                }
                if read - start >= maximum_line {
                    child.skipping = true;
                    start = read;
                }
                child.offset += start as i64;
                if start == 0 || read < capacity {
                    break;
                }
            }
        }
        if closing {
            for child in &mut self.children {
                child.thread.close();
            }
        }
        self.children.iter_mut().filter_map(|child| child.thread.take()).collect()
    }
}

/// The run engine's watcher of a Codex run (mighty-graph's `SessionWatcher`, made by [`watcher_factory`]).
struct Watching(CodexSessionWatcher);

impl mighty_graph::run::SessionWatcher for Watching {
    fn poll(&mut self) -> Vec<CodexSessionAgent> {
        self.0.poll()
    }
    fn finish(&mut self) -> Vec<CodexSessionAgent> {
        self.0.finish()
    }
}

/// What the app hands mighty-graph's `TrackerFactory`: a Codex run's watcher under `CliAccountSupport.CodexHome(home, environment)`
/// (`$CODEX_HOME`, else `<home>/.codex`), opened once the run's root thread is known.
pub fn watcher_factory(home: PathBuf) -> mighty_graph::run::CodexWatcherFactory {
    std::sync::Arc::new(move |environment, root, started, namespace| {
        let environment: HashMap<String, String> = environment.iter().map(|(k, v)| (k.clone(), v.clone())).collect();
        let codex_home = crate::history::codex_home(&home, &environment);
        CodexSessionWatcher::new(&codex_home, root, started, namespace, None, None)
            .map(|watcher| Box::new(Watching(watcher)) as Box<dyn mighty_graph::run::SessionWatcher>)
    })
}
