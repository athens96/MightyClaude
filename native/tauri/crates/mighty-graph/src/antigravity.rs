//! An Antigravity run's sub-agents read from their own transcripts (C# AntigravityRecords.cs:267-515, M/MightyCore/AntigravityRecords.swift
//! AntigravityChildRecord, AntigravitySubagentWatcher): agy's stream names each sub-agent's conversation but not its work, so the runner
//! (and the history's replay) reads each sub-agent's `transcript_full.jsonl` as it grows and hands the tracker a snapshot of what changed
//! (`ExecutionGraphTracker.AntigravitySession`). The records' helpers are mighty-run's (`mighty_run::antigravity::records`).

use std::collections::HashSet;
use std::fs::File;
use std::io::{Read, Seek, SeekFrom};
use std::path::{Path, PathBuf};

use mighty_core::graph::GraphResponseRecord;
use mighty_core::model::{AgentActivity, LogEntry};
use mighty_core::time::{self, DateTimeOffset};
use mighty_run::activity;
use mighty_run::antigravity::records::{self, Outcome};
use serde_json::Value;

use crate::node::{MAXIMUM_ENTRIES, MAXIMUM_NODES};
use crate::tracker::others::antigravity_usage;
use crate::tracker::{AntigravitySubagent, CodexSessionAgent};

fn text<'a>(value: &'a Value, key: &str) -> Option<&'a str> {
    value.get(key).and_then(Value::as_str)
}

fn integer(value: &Value, key: &str) -> Option<i64> {
    mighty_core::json::integer(value, key)
}

/// C# `AntigravityChildRecord`: one Antigravity sub-agent's own conversation, read as it grows — its parent's instructions, its messages
/// and tool calls, what it sent back (`send_message`) and its usage. Snapshots carry only the entries changed since the previous one, as
/// Codex's session records do. Tool results are shown for commands and failures only; file contents are never copied. A sub-agent that
/// started helpers of its own is not done while any of them has not reported back: their messages continue its work.
pub struct AntigravityChildRecord {
    pub subagent: AntigravitySubagent,
    namespace: String,
    entries: Vec<LogEntry>,
    changed: HashSet<String>,
    usage: Vec<GraphResponseRecord>,
    dirty: bool,
    input: Option<String>,
    output: Option<String>,
    state: String,
    turns: i32,
    /// Tool calls waiting for their result step, oldest first.
    calls: Vec<(String, String, Option<Value>)>,
    answer: Option<String>,
    message: Option<String>,
    /// Sub-agents this one started that were not handed out yet; those it started; those that have not reported back.
    spawned: Vec<AntigravitySubagent>,
    started: Vec<String>,
    waiting: HashSet<String>,
}

impl AntigravityChildRecord {
    pub fn new(subagent: AntigravitySubagent, namespace: &str) -> AntigravityChildRecord {
        AntigravityChildRecord {
            input: subagent.prompt.clone(),
            subagent,
            namespace: namespace.into(),
            entries: vec![],
            changed: HashSet::new(),
            usage: vec![],
            dirty: false,
            output: None,
            state: "running".into(),
            turns: 0,
            calls: vec![],
            answer: None,
            message: None,
            spawned: vec![],
            started: vec![],
            waiting: HashSet::new(),
        }
    }

    /// `TakeSpawned`: the helpers it started since the last call.
    pub fn take_spawned(&mut self) -> Vec<AntigravitySubagent> {
        std::mem::take(&mut self.spawned)
    }

    /// `Take`: what changed since the last snapshot, or None.
    pub fn take(&mut self) -> Option<CodexSessionAgent> {
        if !self.dirty {
            return None;
        }
        self.dirty = false;
        let value = CodexSessionAgent {
            thread: self.subagent.conversation.clone(),
            parent_thread: self.subagent.parent.clone(),
            title: self.subagent.title.clone(),
            input: self.input.clone().filter(|i| !i.is_empty()),
            entries: self.entries.iter().filter(|e| self.changed.contains(e.id())).cloned().collect(),
            answers: HashSet::new(),
            output: self.output.clone(),
            state: self.state.clone(),
            turns: self.turns,
            usage: std::mem::take(&mut self.usage),
        };
        self.changed.clear();
        Some(value)
    }

    fn key(&self, suffix: &str) -> String {
        activity::id(&self.namespace, &format!("agy:{}:{suffix}", self.subagent.conversation))
    }

    fn upsert(&mut self, mut entry: LogEntry) {
        if let Some(index) = self.entries.iter().position(|e| e.id == entry.id) {
            entry.timestamp = self.entries[index].timestamp.clone();
            if self.entries[index] == entry {
                return;
            }
            self.entries[index] = entry.clone();
        } else {
            self.entries.push(entry.clone());
            while self.entries.len() > MAXIMUM_ENTRIES {
                let first = self.entries.remove(0);
                self.changed.remove(first.id());
            }
        }
        self.changed.insert(entry.id().to_string());
        self.dirty = true;
    }

    fn activity(&mut self, id: &str, tool: &str, args: Option<&Value>, state: &str, stamp: &str, output: Option<String>) {
        let candidate = AgentActivity {
            id: id.into(),
            provider: "gemini".into(),
            kind: activity::kind(tool).into(),
            state: state.into(),
            summary: records::summary(tool, args),
            tool_name: Some(tool.into()),
            output,
            duration_ms: None,
        };
        let Some(value) = activity::normalized(&candidate, false) else { return };
        let mut entry = LogEntry::new(id.into(), "system", value.summary.clone(), stamp.into(), Some("gemini".into()));
        entry.activity = Some(value);
        self.upsert(entry);
    }

    fn entry(&mut self, key: &str, kind: &str, text: String, stamp: &str) {
        let id = self.key(key);
        self.upsert(LogEntry::new(id, kind, text, stamp.into(), Some("gemini".into())));
    }

    /// `Consume`: one transcript step.
    pub fn consume(&mut self, step: &Value) {
        let (Some(kind), Some(index)) = (text(step, "type"), integer(step, "step_index")) else { return };
        let stamp = match text(step, "created_at") {
            Some(written) if time::parse(Some(written)).is_some() => written.to_string(),
            _ => time::now_o(),
        };
        let outcome = records::outcome_of(text(step, "status"));
        let before = (self.state.clone(), self.output.clone(), self.turns, self.input.clone());
        let key = index.to_string();
        let content = text(step, "content");
        match kind {
            "SYSTEM_MESSAGE" if records::message(content, &self.subagent.parent).is_none() => {
                // A helper it started reported back: the same assignment goes on.
                let Some(reported) = self.started.iter().find(|helper| records::message(content, helper).is_some()).cloned() else {
                    return;
                };
                self.waiting.remove(&reported.to_lowercase());
                if self.state == "completed" {
                    self.state = "running".into();
                }
            }
            "SYSTEM_MESSAGE" | "USER_INPUT" => {
                // Each instruction from the parent (or the user) is a new assignment.
                let said = if kind == "USER_INPUT" { records::request(content) } else { records::message(content, &self.subagent.parent) };
                let Some(said) = said else { return };
                self.turns += 1;
                if self.turns == 1 && self.input.as_deref().is_none_or(str::is_empty) {
                    self.input = Some(said);
                } else if self.turns > 1 {
                    self.entry(&key, "user", said, &stamp);
                }
                self.answer = None;
                self.message = None;
                self.state = "running".into();
            }
            "PLANNER_RESPONSE" => {
                if let Some(said) = content.map(str::trim).filter(|t| !t.is_empty()) {
                    self.entry(&key, "assistant", said.to_string(), &stamp);
                    self.answer = Some(said.to_string());
                }
                let mut ids = vec![];
                let calls: Vec<Value> =
                    step.get("tool_calls").and_then(Value::as_array).map(|c| c.iter().take(64).cloned().collect()).unwrap_or_default();
                for (position, call) in calls.iter().enumerate() {
                    let Some(name) = text(call, "name").filter(|n| !n.is_empty()) else { continue };
                    let args = call.get("args").cloned();
                    let id = self.key(&format!("{key}:{position}"));
                    self.activity(&id, name, args.as_ref(), "running", &stamp, None);
                    self.calls.push((id.clone(), name.to_string(), args.clone()));
                    ids.push(id);
                    // What it sends its parent is its answer to the assignment.
                    if name == "send_message"
                        && args.as_ref().and_then(|a| text(a, "Recipient")).is_some_and(|r| r.eq_ignore_ascii_case(&self.subagent.parent))
                        && let Some(sent) = args.as_ref().and_then(|a| text(a, "Message")).map(str::trim).filter(|m| !m.is_empty())
                    {
                        self.message = Some(sent.to_string());
                    }
                }
                if let Some(tokens) = antigravity_usage(Some(step)) {
                    self.usage.push(GraphResponseRecord {
                        response_id: Some(format!("agy:{}:{key}", self.subagent.conversation)),
                        model: None,
                        usage: Some(tokens),
                        activity_ids: Some(ids.iter().cloned().map(Some).collect()),
                        marked_as_configured: false,
                    });
                    self.dirty = true;
                }
                match outcome {
                    Outcome::Failed => {
                        self.state = "error".into();
                        if let Some(error) = records::error_text(step.get("error")) {
                            self.entry(&format!("{key}:error"), "error", error, &stamp);
                        }
                    }
                    Outcome::Stopped => self.state = "stopped".into(),
                    // An answer while its helpers still work ("waiting for …") is not the end.
                    Outcome::Done => {
                        self.state = if ids.is_empty() && self.waiting.is_empty() { "completed" } else { "running" }.into();
                    }
                }
            }
            "GENERIC" => {
                if self.calls.is_empty() {
                    return;
                }
                let (entry, name, args) = self.calls.remove(0);
                let result = match outcome {
                    Outcome::Failed => records::error_text(step.get("error")).or_else(|| records::tool_text(content)),
                    Outcome::Done if activity::kind(&name) == "command" => records::tool_text(content),
                    _ => None,
                };
                let state = match outcome {
                    Outcome::Done => "completed",
                    Outcome::Failed => "error",
                    Outcome::Stopped => "stopped",
                };
                self.activity(&entry, &name, args.as_ref(), state, &stamp, result);
                if name == "invoke_subagent" && outcome == Outcome::Done {
                    let specs: Vec<Value> =
                        args.as_ref().and_then(|a| a.get("Subagents")).and_then(Value::as_array).cloned().unwrap_or_default();
                    for (offset, id) in records::spawned_conversations(content).into_iter().enumerate() {
                        let spec = specs.get(offset);
                        if !self.started.contains(&id) {
                            self.started.push(id.clone());
                        }
                        self.waiting.insert(id.to_lowercase());
                        self.spawned.push(AntigravitySubagent {
                            conversation: id,
                            parent: self.subagent.conversation.clone(),
                            title: records::title(spec.and_then(|s| text(s, "Role")), spec.and_then(|s| text(s, "TypeName"))),
                            prompt: spec.and_then(|s| text(s, "Prompt")).map(str::to_string),
                        });
                    }
                }
            }
            _ => return,
        }
        self.output = self.message.clone().or_else(|| if self.state == "completed" { self.answer.clone() } else { None });
        if before != (self.state.clone(), self.output.clone(), self.turns, self.input.clone()) {
            self.dirty = true;
        }
    }
}

/// `AntigravitySubagentWatcher.Limits`: what each read may spend.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Limits {
    pub bytes_per_poll: i64,
    pub final_bytes: i64,
    pub maximum_line_bytes: usize,
    pub maximum_children: usize,
}

impl Default for Limits {
    fn default() -> Self {
        Limits {
            bytes_per_poll: 4 * 1024 * 1024,
            final_bytes: 32 * 1024 * 1024,
            maximum_line_bytes: 2 * 1024 * 1024,
            maximum_children: MAXIMUM_NODES,
        }
    }
}

struct Child {
    path: PathBuf,
    record: AntigravityChildRecord,
    offset: i64,
    skipping: bool,
    /// The file shrank; transcripts only grow, so it is not read again.
    closed: bool,
}

/// A file that exists and is no link nor any other reparse point, opened for reading with agy still free to write it (C#
/// `RecordFiles.Open`, mighty-history's `files::open`).
fn open_regular(path: &Path) -> Option<File> {
    let meta = std::fs::symlink_metadata(path).ok()?;
    #[cfg(windows)]
    {
        use std::os::windows::fs::MetadataExt;
        const FILE_ATTRIBUTE_REPARSE_POINT: u32 = 0x400;
        if meta.file_attributes() & FILE_ATTRIBUTE_REPARSE_POINT != 0 {
            return None;
        }
    }
    if !meta.file_type().is_file() {
        return None;
    }
    File::open(path).ok()
}

/// `RecordFiles.Read`: as many bytes as fit from `offset`, fewer at the end or after a failed read.
fn read_at(file: &mut File, buffer: &mut [u8], offset: i64) -> usize {
    if file.seek(SeekFrom::Start(offset.max(0) as u64)).is_err() {
        return 0;
    }
    let mut total = 0;
    while total < buffer.len() {
        match file.read(&mut buffer[total..]) {
            Ok(0) | Err(_) => break,
            Ok(read) => total += read,
        }
    }
    total
}

/// C# `AntigravitySubagentWatcher`: follows an Antigravity run's sub-agents through their own transcripts. Every read is bounded.
pub struct AntigravitySubagentWatcher {
    brain: PathBuf,
    namespace: String,
    limits: Limits,
    children: Vec<Child>,
    known: HashSet<String>,
    next: usize,
    stopped: bool,
    /// Steps written after this are not read (a recorded request's end: later requests may give the same sub-agent more work).
    until: Option<DateTimeOffset>,
    pub bytes_read: i64,
}

impl AntigravitySubagentWatcher {
    /// `root`: the run's own conversation, never followed as a sub-agent.
    pub fn new(
        brain: &Path,
        namespace: &str,
        root: Option<&str>,
        until: Option<DateTimeOffset>,
        limits: Limits,
    ) -> AntigravitySubagentWatcher {
        let mut known = HashSet::new();
        if let Some(root) = root {
            known.insert(root.to_lowercase());
        }
        AntigravitySubagentWatcher {
            brain: brain.to_path_buf(),
            namespace: namespace.into(),
            limits,
            children: vec![],
            known,
            next: 0,
            stopped: false,
            until,
            bytes_read: 0,
        }
    }

    /// `Poll`: changed sub-agents while the run is active.
    pub fn poll(&mut self, subagents: &[AntigravitySubagent]) -> Vec<CodexSessionAgent> {
        if self.stopped {
            return vec![];
        }
        self.read(subagents, self.limits.bytes_per_poll)
    }

    /// `Finish`: one last, larger read after the run ended; later polls read nothing.
    pub fn finish(&mut self, subagents: &[AntigravitySubagent]) -> Vec<CodexSessionAgent> {
        if self.stopped {
            return vec![];
        }
        let read = self.read(subagents, self.limits.final_bytes);
        self.stopped = true;
        read
    }

    fn adopt(&mut self, subagents: Vec<AntigravitySubagent>) {
        for subagent in subagents {
            if self.children.len() >= self.limits.maximum_children {
                break;
            }
            let Some(path) = records::transcript(&self.brain, &subagent.conversation) else { continue };
            if !self.known.insert(subagent.conversation.to_lowercase()) {
                continue;
            }
            self.children.push(Child {
                path,
                record: AntigravityChildRecord::new(subagent, &self.namespace),
                offset: 0,
                skipping: false,
                closed: false,
            });
        }
    }

    fn read(&mut self, subagents: &[AntigravitySubagent], mut budget: i64) -> Vec<CodexSessionAgent> {
        self.adopt(subagents.to_vec());
        // Start one child later each time so a busy child cannot starve the rest; children a child starts are read in the same pass.
        let existing = self.children.len();
        let first = if existing == 0 { 0 } else { self.next % existing };
        self.next = first + 1;
        let mut order: Vec<usize> = (0..existing).map(|i| (first + i) % existing).collect();
        let maximum_line = self.limits.maximum_line_bytes;
        let mut position = 0;
        while position < order.len() && budget > 0 {
            let at = order[position];
            position += 1;
            let until = self.until;
            let child = &mut self.children[at];
            if child.closed {
                continue;
            }
            let Some(mut handle) = open_regular(&child.path) else { continue };
            let Ok(size) = handle.metadata().map(|m| m.len() as i64) else { continue };
            if size < child.offset {
                child.closed = true;
                continue;
            }
            while budget > 0 && child.offset < size {
                let capacity = (maximum_line as i64 + 1).min(size - child.offset).min(budget) as usize;
                let mut bytes = vec![0u8; capacity];
                let count = read_at(&mut handle, &mut bytes, child.offset);
                if count == 0 {
                    break;
                }
                budget -= count as i64;
                self.bytes_read += count as i64;
                let mut start = 0;
                if child.skipping {
                    let Some(end) = bytes[..count].iter().position(|b| *b == b'\n') else {
                        child.offset += count as i64;
                        continue;
                    };
                    start = end + 1;
                    child.skipping = false;
                }
                while start < count && !child.closed {
                    let Some(relative) = bytes[start..count].iter().position(|b| *b == b'\n') else { break };
                    if relative > 0
                        && relative <= maximum_line
                        && let Some(step) = serde_json::from_slice::<Value>(&bytes[start..start + relative]).ok().filter(Value::is_object)
                    {
                        // Transcripts are written in order: past the end, nothing more belongs here.
                        if let (Some(limit), Some(date)) = (until, records::date(&step))
                            && date.utc_ticks() > limit.utc_ticks()
                        {
                            child.closed = true;
                            break;
                        }
                        child.record.consume(&step);
                    }
                    start += relative + 1;
                }
                if count - start >= maximum_line {
                    child.skipping = true;
                    start = count;
                }
                child.offset += start as i64;
                if child.closed || start == 0 || count < capacity {
                    break;
                }
            }
            let spawned = child.record.take_spawned();
            let before = self.children.len();
            self.adopt(spawned);
            order.extend(before..self.children.len());
        }
        self.children.iter_mut().filter_map(|c| c.record.take()).collect()
    }
}
