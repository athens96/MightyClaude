//! The tracker as the run engine uses it (C# `RunManager.ExecuteAsync` holding a `new ExecutionGraphTracker(runID, input, provider,
//! model, onNode, effort)`): mighty-run's [`GraphFactory`] and [`GraphSink`] over [`ExecutionGraphTracker`]. mighty-run names the seam
//! and never this crate; this crate reads mighty-run and nothing reads this crate back, so the two stay free of a cycle. The host hands
//! `RunManagerOptions.graph` a [`TrackerFactory`]; the runner then publishes each request's graph run every second and at its end, with
//! the notice runs its background work opened, as C#'s does. A Codex run's sub-agents come live from Codex's session files through
//! the [`SessionWatcher`] the factory's [`CodexWatcherFactory`] opens (mighty-history's `codex_sessions`, which reads the files), an
//! Antigravity run's from their own transcripts through [`AntigravitySubagentWatcher`].

use std::collections::BTreeMap;
use std::path::Path;
use std::sync::Arc;

use mighty_core::graph::MightyGraphRun;
use mighty_core::json::Wire;
use mighty_core::locale::Locale;
use mighty_core::model::{AgentActivity, LogEntry};
use mighty_core::time::DateTimeOffset;
use mighty_run::graph::{ChildRead, ChildSources, ChildrenFound, GraphFactory, GraphSink, GraphStart};
use serde_json::Value;

use crate::antigravity::{AntigravitySubagentWatcher, Limits};
use crate::tracker::{CodexSessionAgent, ModGraphMetadata, ModMetadata};
use crate::{ExecutionGraphNode, ExecutionGraphTracker};

/// A run's sub-agents as the CLI's own files tell them (C# `CodexSessionWatcher`): what changed since the last poll; at the end what is
/// left, after which it reads nothing more.
pub trait SessionWatcher: Send {
    fn poll(&mut self) -> Vec<CodexSessionAgent>;
    fn finish(&mut self) -> Vec<CodexSessionAgent>;
}

/// Opens a Codex run's [`SessionWatcher`] (C# `new CodexSessionWatcher(codexHome, root, startedAt, tracker.RunID)`): the CLI's
/// environment, the run's root thread, when the run started and the graph run's id. None when there is nothing to watch.
pub type CodexWatcherFactory =
    Arc<dyn Fn(&BTreeMap<String, String>, &str, DateTimeOffset, &str) -> Option<Box<dyn SessionWatcher>> + Send + Sync>;

/// Makes one tracker per run. `locale`: the language the blocks are worded in; None reads the run engine's (`mighty_run::strings`) when
/// each run starts, as C#'s tracker reads `Locale` when it words something. `codex_sessions`: where a Codex run's sub-agents are read
/// from; None leaves them to what the stream says.
#[derive(Clone, Default)]
pub struct TrackerFactory {
    pub locale: Option<Locale>,
    pub codex_sessions: Option<CodexWatcherFactory>,
}

impl TrackerFactory {
    pub fn new() -> TrackerFactory {
        TrackerFactory::default()
    }
}

/// A node as the remote wire's `graph` event carries it (`Wire.Json`).
pub fn node_value(node: &ExecutionGraphNode) -> Value {
    serde_json::from_str(&node.to_json()).unwrap_or(Value::Null)
}

/// mighty-run's reading of a Mods event as the tracker takes it (the same C# record, read once by the runner).
pub fn mod_metadata(event: &mighty_run::mods::ModMetadata) -> ModMetadata {
    ModMetadata {
        claude_session_id: event.claude_session_id.clone(),
        event: event.event.clone(),
        tool: event.tool.clone(),
        tool_use_id: event.tool_use_id.clone(),
        agent_id: event.agent_id.clone(),
        graph: event.graph.as_ref().map(|g| ModGraphMetadata {
            version: g.version,
            phase: g.phase.clone(),
            agent_id: g.agent_id.clone(),
            parent_agent_id: g.parent_agent_id.clone(),
            parent_tool_use_id: g.parent_tool_use_id.clone(),
            name: g.name.clone(),
            agent_type: g.agent_type.clone(),
            model: g.model.clone(),
            input: g.input.clone(),
            output: g.output.clone(),
        }),
    }
}

impl GraphFactory for TrackerFactory {
    fn start(&self, start: GraphStart) -> Box<dyn GraphSink> {
        let locale = self.locale.unwrap_or_else(mighty_run::strings::locale);
        let node = start.node.clone();
        let tracker = ExecutionGraphTracker::new(
            &start.run_id,
            Some(&start.input),
            &start.provider,
            Some(&start.model),
            Box::new(move |changed: &ExecutionGraphNode| node(node_value(changed))),
            start.effort.as_deref(),
            None,
            locale,
        );
        Box::new(TrackerSink::new(tracker, self.codex_sessions.clone()))
    }
}

/// One run's tracker behind the runner's calls, with the watchers of its sub-agents' files once there are some: a Codex run's session
/// files once the stream named its root thread, an Antigravity run's sub-agents' transcripts once the stream named one.
pub struct TrackerSink {
    pub tracker: ExecutionGraphTracker,
    /// When the run started (C#'s `startedAt`): nothing older is read.
    started: DateTimeOffset,
    codex_sessions: Option<CodexWatcherFactory>,
    watcher: Option<Box<dyn SessionWatcher>>,
    antigravity: Option<AntigravitySubagentWatcher>,
    /// A read is out with the watcher: none other is handed out (nor a watcher opened afresh) until it is merged back.
    reading: bool,
}

impl TrackerSink {
    /// `codex_sessions`: where a Codex run's sub-agents are read from; None leaves them to what the stream says.
    pub fn new(tracker: ExecutionGraphTracker, codex_sessions: Option<CodexWatcherFactory>) -> TrackerSink {
        TrackerSink { tracker, started: DateTimeOffset::now(), codex_sessions, watcher: None, antigravity: None, reading: false }
    }

    /// The Codex half of C#'s `ReadChildren`: the watcher, opened once the stream named the root thread (the opening goes with the read,
    /// as it may look at the disk), goes with the read; each sub-agent it read is merged on the way back (`CodexSession`).
    fn codex_children(&mut self, environment: &BTreeMap<String, String>, closing: bool) -> Option<ChildRead> {
        if let Some(mut watcher) = self.watcher.take() {
            return Some(Box::new(move || {
                let children = if closing { watcher.finish() } else { watcher.poll() };
                Box::new(Found::Codex(Some(watcher), children)) as ChildrenFound
            }));
        }
        let (open, root) = (self.codex_sessions.clone()?, self.tracker.codex_root_thread()?.to_string());
        let (environment, started, run_id) = (environment.clone(), self.started, self.tracker.run_id().to_string());
        Some(Box::new(move || {
            let mut watcher = open(&environment, &root, started, &run_id);
            let children = watcher.as_mut().map(|w| if closing { w.finish() } else { w.poll() }).unwrap_or_default();
            Box::new(Found::Codex(watcher, children)) as ChildrenFound
        }))
    }

    /// The Antigravity half of C#'s `ReadChildren`: the watcher made once the stream named a sub-agent (the run's own conversation never
    /// followed) goes with the read and the sub-agents known now; each snapshot it read is handed to `AntigravitySession` on the way
    /// back.
    fn antigravity_children(&mut self, brain: &Path, namespace: &str, closing: bool) -> Option<ChildRead> {
        let known = self.tracker.antigravity_subagents().to_vec();
        if self.antigravity.is_none() && !known.is_empty() {
            let root = self.tracker.antigravity_conversation().map(str::to_string);
            self.antigravity = Some(AntigravitySubagentWatcher::new(brain, namespace, root.as_deref(), None, Limits::default()));
        }
        let mut watcher = self.antigravity.take()?;
        Some(Box::new(move || {
            let children = if closing { watcher.finish(&known) } else { watcher.poll(&known) };
            Box::new(Found::Antigravity(watcher, children)) as ChildrenFound
        }))
    }
}

/// What a [`TrackerSink`]'s read found: the watcher it took along, given back, and the sub-agents it read.
enum Found {
    Codex(Option<Box<dyn SessionWatcher>>, Vec<CodexSessionAgent>),
    Antigravity(AntigravitySubagentWatcher, Vec<CodexSessionAgent>),
}

impl GraphSink for TrackerSink {
    fn run_id(&self) -> String {
        self.tracker.run_id().to_string()
    }
    fn frame(&mut self, _provider: &str, frame: &Value) {
        self.tracker.consume(frame);
    }
    fn notice_revision(&self) -> u64 {
        u64::try_from(self.tracker.notice_revision()).unwrap_or(0)
    }
    fn activity(&mut self, activity: &AgentActivity, tool_id: Option<&str>) -> bool {
        self.tracker.activity(activity, tool_id)
    }
    fn images(&mut self, entry: &LogEntry, tool_id: Option<&str>, parent_tool_id: Option<&str>) -> bool {
        self.tracker.images(entry.clone(), tool_id, parent_tool_id)
    }
    fn mod_event(&mut self, event: &mighty_run::mods::ModMetadata) {
        self.tracker.receive_mod(&mod_metadata(event));
    }
    fn steer(&mut self, id: &str, text: &str) {
        self.tracker.steer(id, text);
    }
    fn note_effort(&mut self, effort: &str) {
        self.tracker.note_effort(Some(effort));
    }
    fn finish(&mut self, state: &str) {
        self.tracker.finish(state);
    }
    fn build_run(&mut self, include_running: bool) -> Option<MightyGraphRun> {
        self.tracker.build_run(include_running)
    }
    fn take_notice_runs(&mut self, include_running: bool) -> Vec<MightyGraphRun> {
        self.tracker.take_notice_runs(include_running)
    }
    fn antigravity_answer(&mut self, text: &str) {
        self.tracker.antigravity_answer(text);
    }
    /// C#'s `ReadChildren`: an Antigravity run's sub-agents from their own transcripts under its `brain`, a Codex run's from its session
    /// files — read by the runner without this sink's lock.
    fn read_children(&mut self, sources: &ChildSources<'_>, closing: bool) -> Option<ChildRead> {
        if self.reading {
            return None;
        }
        let read = match self.tracker.provider() {
            "gemini" => self.antigravity_children(sources.antigravity_brain?, sources.namespace, closing),
            "codex" => self.codex_children(sources.environment, closing),
            _ => None,
        };
        self.reading = read.is_some();
        read
    }
    /// The read's sub-agents merged into the graph (`CodexSession`, `AntigravitySession`), its watcher kept for the next read.
    fn merge_children(&mut self, found: ChildrenFound) {
        self.reading = false;
        let Ok(found) = found.downcast::<Found>() else { return };
        match *found {
            Found::Codex(watcher, children) => {
                self.watcher = watcher;
                for child in &children {
                    self.tracker.codex_session(child);
                }
            }
            Found::Antigravity(watcher, children) => {
                self.antigravity = Some(watcher);
                for child in &children {
                    self.tracker.antigravity_session(child);
                }
            }
        }
    }
}
