//! The execution graph tracker (C# `ExecutionGraphTracker`, MightyCode.Core/ExecutionGraphTracker.cs:81 and its partial files): it
//! observes one run's public CLI and Mods events and emits node snapshots. It never reads another session's transcript or guesses a
//! child's prompt.
//!
//! The run engine calls it as `RunManager` does (RunManager.cs:168-273, 339, 419-422, 715): [`ExecutionGraphTracker::consume`] each
//! stream frame, [`activity`](ExecutionGraphTracker::activity) and [`images`](ExecutionGraphTracker::images) from the parser,
//! [`antigravity_answer`](ExecutionGraphTracker::antigravity_answer) with a streamed reply, [`receive_mod`](ExecutionGraphTracker::receive_mod)
//! with a Mods event, [`steer`](ExecutionGraphTracker::steer) when a steer was accepted, [`codex_session`](ExecutionGraphTracker::codex_session)
//! and [`antigravity_session`](ExecutionGraphTracker::antigravity_session) with the sub-agents' transcripts, [`finish`](ExecutionGraphTracker::finish)
//! when the process ended, and [`build_run`](ExecutionGraphTracker::build_run) / [`take_notice_runs`](ExecutionGraphTracker::take_notice_runs)
//! to publish. C# locks every call; here every call takes `&mut self` and a caller that shares a tracker between threads keeps it in a
//! `Mutex`. Each node goes out through the `emit` callback the moment it changes.
//!
//! The modules: `claude` (stream-json and Mods), `codex` (exec JSONL, `CodexCollaborationItem`, `CodexSession`), `others` (Antigravity,
//! pi, ACP), `advisor` (the advisor's consultations), `notices` (notice runs, `SettleTasks`, `SettleHandbacks`).

mod advisor;
mod claude;
mod codex;
mod notices;
mod others;

use std::collections::{HashMap, HashSet};

use indexmap::IndexMap;
use mighty_core::graph::{GraphAgentFigures, GraphAgentIdentity, GraphResponseRecord, GraphTokenUsage, MightyGraphAgent, MightyGraphRun};
use mighty_core::locale::Locale;
use mighty_core::model::{AgentActivity, LogEntry};
use mighty_core::{normalize, time, wire};
use serde_json::Value;

pub use codex::{CodexAgentState, CodexCollaborationItem, codex_usage};

use crate::labels;
use crate::node::{self, ExecutionGraphNode, MAXIMUM_ACTIVITY_GENERATION, MAXIMUM_ENTRIES, MAXIMUM_NODES};
use crate::notice::Opener;
use crate::support;

/// C# `ModGraphMetadata(Version, Phase, AgentId?, ParentAgentId?, ParentToolUseId?, Name?, AgentType?, Model?, Input?, Output?)`: the
/// graph part of one Mods event.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct ModGraphMetadata {
    pub version: i32,
    pub phase: String,
    pub agent_id: Option<String>,
    pub parent_agent_id: Option<String>,
    pub parent_tool_use_id: Option<String>,
    pub name: Option<String>,
    pub agent_type: Option<String>,
    pub model: Option<String>,
    pub input: Option<String>,
    pub output: Option<String>,
}

/// C# `ModMetadata(ClaudeSessionId, Event, Tool?, ToolUseId?, AgentId?, Graph?)`: the fields of one Mods event the graph reads.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct ModMetadata {
    pub claude_session_id: String,
    pub event: String,
    pub tool: Option<String>,
    pub tool_use_id: Option<String>,
    pub agent_id: Option<String>,
    pub graph: Option<ModGraphMetadata>,
}

impl ModMetadata {
    /// `RunManager.FeedMod`: a Mods event as the mighty-bridge hook posts it, or none without a session or an event.
    pub fn from_event(root: &Value) -> Option<ModMetadata> {
        let session = text(Some(root), "claudeSessionId")?;
        let event = text(Some(root), "event")?;
        let graph =
            prop(Some(root), "graph").filter(|g| g.is_object()).filter(|g| safe_integer(prop(Some(g), "version")) == Some(1)).map(|g| {
                let g = Some(g);
                ModGraphMetadata {
                    version: 1,
                    phase: text(g, "phase").unwrap_or("running").into(),
                    agent_id: text(g, "agentId").map(Into::into),
                    parent_agent_id: text(g, "parentAgentId").map(Into::into),
                    parent_tool_use_id: text(g, "parentToolUseId").map(Into::into),
                    name: text(g, "name").or(text(g, "agentType")).map(Into::into),
                    agent_type: text(g, "agentType").map(Into::into),
                    model: text(g, "model").map(Into::into),
                    input: text(g, "input").map(Into::into),
                    output: text(g, "output").map(Into::into),
                }
            });
        Some(ModMetadata {
            claude_session_id: session.into(),
            event: event.into(),
            tool: text(Some(root), "tool").map(Into::into),
            tool_use_id: text(Some(root), "toolUseId").map(Into::into),
            agent_id: text(Some(root), "agentId").map(Into::into),
            graph,
        })
    }
}

/// C# `CodexSessionAgent(Thread, ParentThread, Title?, Input?, Entries, Answers, Output?, State, Turns, Usage)`: a sub-agent's
/// transcript snapshot (Codex's session files, Antigravity's sub-agent transcripts), read by the run engine's watcher.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct CodexSessionAgent {
    pub thread: String,
    pub parent_thread: String,
    pub title: Option<String>,
    pub input: Option<String>,
    pub entries: Vec<LogEntry>,
    pub answers: HashSet<String>,
    pub output: Option<String>,
    pub state: String,
    pub turns: i32,
    pub usage: Vec<GraphResponseRecord>,
}

/// C# `AntigravitySubagent(Conversation, Parent, Title?, Prompt?)`: a sub-agent the stream named, for the runner's watcher.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct AntigravitySubagent {
    pub conversation: String,
    pub parent: String,
    pub title: Option<String>,
    pub prompt: Option<String>,
}

/// `Owner(IsAgent, Id)`: a node by its id, or a CLI agent by its own id (its block found through `aliases`).
#[derive(Debug, Clone, PartialEq, Eq)]
struct Owner {
    is_agent: bool,
    id: String,
}

impl Owner {
    fn node(id: &str) -> Owner {
        Owner { is_agent: false, id: id.into() }
    }
    fn agent(id: &str) -> Owner {
        Owner { is_agent: true, id: id.into() }
    }
}

#[derive(Debug, Clone, Default)]
struct PendingAgent {
    metadata: Option<ModGraphMetadata>,
    activities: Vec<AgentActivity>,
}

/// `SentMessage(Owner, To?, Summary?, Message?)`: a `SendMessage` call waiting for its result.
#[derive(Debug, Clone)]
struct SentMessage {
    owner: Owner,
    to: Option<String>,
    summary: Option<String>,
    message: Option<String>,
}

/// `NoticeOpening(Opener, Names, Lines, Senders)`: what opened a notice run and what it names while it holds no copy.
#[derive(Debug, Clone)]
struct NoticeOpening {
    opener: Opener,
    names: Vec<String>,
    lines: Vec<String>,
    senders: Vec<String>,
}

impl NoticeOpening {
    fn empty() -> NoticeOpening {
        NoticeOpening { opener: Opener::Work, names: vec![], lines: vec![], senders: vec![] }
    }
}

/// `HeldLine(Line?, Name?, Agent?)`: a root message's line no run took yet.
#[derive(Debug, Clone, PartialEq, Eq)]
struct HeldLine {
    line: Option<String>,
    name: Option<String>,
    agent: Option<String>,
}

/// `NoticeLineLimit`, `MessageRunLimit`, `NoticeHeadroom`.
const NOTICE_LINE_LIMIT: usize = 16;
const MESSAGE_RUN_LIMIT: usize = 8;
const NOTICE_HEADROOM: usize = 16;

/// Where a node goes out the moment it changes (C#'s `Action<ExecutionGraphNode> emit`).
pub type Emit = Box<dyn FnMut(&ExecutionGraphNode) + Send>;

/// C# `ExecutionGraphTracker`.
pub struct ExecutionGraphTracker {
    run_id: String,
    main_id: String,
    provider: String,
    configured_model: Option<String>,
    locale: Locale,
    emit: Emit,
    // Codex
    codex_agents: HashMap<String, String>,
    codex_root_thread: Option<String>,
    codex_settled_calls: HashSet<String>,
    codex_call_order: Vec<String>,
    codex_call_generations: HashMap<String, HashMap<String, i32>>,
    codex_observed_call_order: Vec<String>,
    codex_session_turns: HashMap<String, i32>,
    // the graph
    nodes: HashMap<String, ExecutionGraphNode>,
    order: Vec<String>,
    aliases: HashMap<String, String>,
    pending_parents: IndexMap<String, String>,
    pending_agents: IndexMap<String, PendingAgent>,
    tool_owners: HashMap<String, Owner>,
    tool_order: Vec<String>,
    activity_owners: HashMap<String, Owner>,
    activity_order: Vec<String>,
    background_tools: HashSet<String>,
    task_aliases: HashMap<String, String>,
    agent_names: IndexMap<String, String>,
    agent_summaries: HashMap<String, String>,
    sent_messages: HashMap<String, SentMessage>,
    tool_names: HashMap<String, String>,
    message_usage: HashMap<String, GraphTokenUsage>,
    message_model: HashMap<String, String>,
    message_activity_ids: HashMap<String, Vec<String>>,
    message_configured: HashMap<String, bool>,
    node_response_order: HashMap<String, Vec<String>>,
    pending_steers: Vec<String>,
    turn_ended: bool,
    steered_turn: Option<String>,
    notice_index: usize,
    notice_turn: Option<String>,
    absorbed_run: Option<String>,
    merge_turn: Option<String>,
    merge_restore: Option<String>,
    notice_copies: HashMap<String, Vec<String>>,
    notice_runs: Vec<String>,
    copied_in: HashMap<String, String>,
    task_runs: HashMap<String, String>,
    notice_openers: HashMap<String, NoticeOpening>,
    turn_lines: Vec<HeldLine>,
    message_run_count: usize,
    latest_message_run: Option<String>,
    message_agents: HashMap<String, String>,
    notice_revision: i32,
    changed_notice_runs: HashSet<String>,
    message_order: Vec<String>,
    unnamed_usage: usize,
    compactions: usize,
    finished: bool,
    frame_time: Option<String>,
    counted_tools: HashSet<String>,
    counted_order: Vec<String>,
    seen_tools: HashMap<String, i32>,
    reported_tools: HashMap<String, i32>,
    // the advisor
    advisor_owners: HashMap<String, String>,
    advisor_order: Vec<String>,
    changed_files: HashSet<String>,
    advisor_message_usage: HashMap<String, GraphTokenUsage>,
    advisor_message_order: Vec<String>,
    advisor_message_sum: HashMap<String, GraphTokenUsage>,
    advisor_result_sum: HashMap<String, GraphTokenUsage>,
    configured_effort: Option<String>,
    configured_advisor: Option<String>,
    // Antigravity (case-insensitive keys: lower-cased)
    antigravity_agents: HashMap<String, String>,
    antigravity_turns: HashMap<String, i32>,
    antigravity_subagents: Vec<AntigravitySubagent>,
    antigravity_conversation: Option<String>,
    antigravity_model: Option<String>,
}

impl ExecutionGraphTracker {
    /// `new ExecutionGraphTracker(runID, input, provider, configuredModel, emit, configuredEffort, configuredAdvisor)`
    /// (ExecutionGraphTracker.cs:226): the request's main block goes out at once, titled by the agent's own name.
    #[allow(clippy::too_many_arguments)]
    pub fn new(
        run_id: &str,
        input: Option<&str>,
        provider: &str,
        configured_model: Option<&str>,
        emit: Emit,
        configured_effort: Option<&str>,
        configured_advisor: Option<&str>,
        locale: Locale,
    ) -> ExecutionGraphTracker {
        let main_id = node::main_node_id(run_id);
        let mut tracker = ExecutionGraphTracker {
            run_id: run_id.into(),
            main_id: main_id.clone(),
            provider: provider.into(),
            configured_model: configured_model.map(Into::into),
            locale,
            emit,
            codex_agents: HashMap::new(),
            codex_root_thread: None,
            codex_settled_calls: HashSet::new(),
            codex_call_order: vec![],
            codex_call_generations: HashMap::new(),
            codex_observed_call_order: vec![],
            codex_session_turns: HashMap::new(),
            nodes: HashMap::new(),
            order: vec![],
            aliases: HashMap::new(),
            pending_parents: IndexMap::new(),
            pending_agents: IndexMap::new(),
            tool_owners: HashMap::new(),
            tool_order: vec![],
            activity_owners: HashMap::new(),
            activity_order: vec![],
            background_tools: HashSet::new(),
            task_aliases: HashMap::new(),
            agent_names: IndexMap::new(),
            agent_summaries: HashMap::new(),
            sent_messages: HashMap::new(),
            tool_names: HashMap::new(),
            message_usage: HashMap::new(),
            message_model: HashMap::new(),
            message_activity_ids: HashMap::new(),
            message_configured: HashMap::new(),
            node_response_order: HashMap::new(),
            pending_steers: vec![],
            turn_ended: false,
            steered_turn: None,
            notice_index: 0,
            notice_turn: None,
            absorbed_run: None,
            merge_turn: None,
            merge_restore: None,
            notice_copies: HashMap::new(),
            notice_runs: vec![],
            copied_in: HashMap::new(),
            task_runs: HashMap::new(),
            notice_openers: HashMap::new(),
            turn_lines: vec![],
            message_run_count: 0,
            latest_message_run: None,
            message_agents: HashMap::new(),
            notice_revision: 0,
            changed_notice_runs: HashSet::new(),
            message_order: vec![],
            unnamed_usage: 0,
            compactions: 0,
            finished: false,
            frame_time: None,
            counted_tools: HashSet::new(),
            counted_order: vec![],
            seen_tools: HashMap::new(),
            reported_tools: HashMap::new(),
            advisor_owners: HashMap::new(),
            advisor_order: vec![],
            changed_files: HashSet::new(),
            advisor_message_usage: HashMap::new(),
            advisor_message_order: vec![],
            advisor_message_sum: HashMap::new(),
            advisor_result_sum: HashMap::new(),
            configured_effort: configured_effort.filter(|e| normalize::valid_effort(e)).map(Into::into),
            configured_advisor: configured_advisor.filter(|m| provider == "claude" && wire::model(m)).map(Into::into),
            antigravity_agents: HashMap::new(),
            antigravity_turns: HashMap::new(),
            antigravity_subagents: vec![],
            antigravity_conversation: None,
            antigravity_model: None,
        };
        let mut main =
            ExecutionGraphNode::new(main_id.clone(), run_id.into(), None, "main", "running", labels::provider_label(provider).into());
        main.input = input.map(Into::into);
        if let Some(normalized) = node::normalized(&main, false) {
            tracker.insert(normalized);
        }
        tracker
    }

    /// `RunID`.
    pub fn run_id(&self) -> &str {
        &self.run_id
    }

    /// `Provider`: "claude" observes stream-json plus Mods; "codex" exec JSONL; "gemini" Antigravity's stream-json; "pi" its RPC events;
    /// "opencode" and "hermes" their ACP channel's events.
    pub fn provider(&self) -> &str {
        &self.provider
    }

    /// `CodexRootThread`.
    pub fn codex_root_thread(&self) -> Option<&str> {
        self.codex_root_thread.as_deref()
    }

    /// `AntigravityConversation`: the Antigravity conversation this run continues or started.
    pub fn antigravity_conversation(&self) -> Option<&str> {
        self.antigravity_conversation.as_deref()
    }

    /// `AntigravitySubagents`: sub-agents the stream named, oldest first, for the runner's watcher.
    pub fn antigravity_subagents(&self) -> &[AntigravitySubagent] {
        &self.antigravity_subagents
    }

    /// `NoticeRevision`: moves each time a notice run opens or a notice turn reopens one; the caller hands the runs over before the
    /// turn's lines.
    pub fn notice_revision(&self) -> i32 {
        self.notice_revision
    }

    /// `NoteEffort`: the effort the request starts with, when it is not the pane's own; a word that is no effort changes nothing.
    pub fn note_effort(&mut self, effort: Option<&str>) {
        if let Some(effort) = effort.filter(|e| normalize::valid_effort(e)) {
            self.configured_effort = Some(effort.into());
        }
    }

    /// `NoteAdvisor`: the advisor the request starts with once Claude's settings were read; none when none is known to be on.
    pub fn note_advisor(&mut self, model: Option<&str>) {
        self.configured_advisor = model.filter(|m| self.provider == "claude" && wire::model(m)).map(Into::into);
    }

    /// `Consume`: one stream frame, observed before the stream parser makes its tool rows, so a child's rows take the same route as its
    /// text. A Claude frame's own `timestamp` stands for "now" while it is read.
    pub fn consume(&mut self, value: &Value) {
        self.frame_time = if self.provider == "claude" && value.is_object() { frame_time(value) } else { None };
        self.consume_frame(value);
        self.frame_time = None;
    }

    fn consume_frame(&mut self, value: &Value) {
        if self.finished || !value.is_object() {
            return;
        }
        match self.provider.as_str() {
            "codex" => self.consume_codex(value),
            "gemini" => self.consume_antigravity(value),
            "pi" => self.consume_pi(value),
            "opencode" => self.consume_acp(value),
            // Hermes reports its session's running token counts, not the request's: the block carries none.
            "hermes" => {}
            _ => self.consume_claude(value),
        }
    }

    // ── the node store ────────────────────────────────────────────────────────────────────────────────────────────────

    /// Every node goes out as it changes; a notice run's also marks that run for `take_notice_runs`.
    fn send(&mut self, node: &ExecutionGraphNode) {
        if node.run_id != self.run_id {
            self.changed_notice_runs.insert(node.run_id.clone());
        }
        (self.emit)(node);
    }

    /// A new node: kept, ordered and emitted.
    fn insert(&mut self, node: ExecutionGraphNode) {
        self.order.push(node.id.clone());
        self.nodes.insert(node.id.clone(), node.clone());
        self.send(&node);
    }

    fn node(&self, id: &str) -> Option<&ExecutionGraphNode> {
        self.nodes.get(id)
    }

    fn kind_of(&self, id: &str) -> Option<&str> {
        self.nodes.get(id).map(|n| n.kind.as_str())
    }

    /// The time of the frame being read, else now.
    fn now(&self) -> String {
        self.frame_time.clone().unwrap_or_else(time::now_o)
    }

    /// `Update(id, change, reopening, continuing)` (ExecutionGraphTracker.cs:375). Late starts and stdout fallbacks never resurrect a final
    /// snapshot; a Claude sub-agent's or command's block keeps when it was seen to end. `continuing`: an Antigravity sub-agent that answered
    /// while helpers it started were still out goes on with the same assignment when one reports.
    fn update_with(&mut self, id: &str, reopening: bool, continuing: bool, change: impl FnOnce(ExecutionGraphNode) -> ExecutionGraphNode) {
        if self.finished {
            return;
        }
        let Some(previous) = self.nodes.get(id).cloned() else { return };
        let mut value = change(previous.clone());
        let provider = self.provider.as_str();
        let restart = reopening
            && matches!(provider, "codex" | "gemini")
            && previous.kind == "agent"
            && previous.activity_generation.unwrap_or(0) < MAXIMUM_ACTIVITY_GENERATION;
        if restart {
            value.activity_generation = Some(previous.activity_generation.unwrap_or(0) + 1);
        }
        if !restart && matches!(previous.state.as_str(), "error" | "stopped") && value.state != previous.state {
            value.state = previous.state.clone();
            value.output = previous.output.clone().or(value.output);
        } else if !restart
            && node::terminal(&previous.state)
            && !node::terminal(&value.state)
            && !(continuing
                && provider == "gemini"
                && previous.kind == "agent"
                && previous.state == "completed"
                && value.state == "running")
        {
            value.state = previous.state.clone();
        }
        if provider == "claude"
            && matches!(value.kind.as_str(), "agent" | "task")
            && !node::terminal(&previous.state)
            && node::terminal(&value.state)
            && value.figures.as_ref().and_then(|f| f.ended_at.as_ref()).is_none()
        {
            let mut figures = value.figures.take().unwrap_or_default();
            figures.ended_at = Some(self.now());
            value.figures = Some(figures);
        }
        let Some(mut normalized) = node::normalized(&value, false) else { return };
        if normalized == previous {
            return;
        }
        normalized.updated_at = time::now_o();
        self.nodes.insert(id.to_string(), normalized.clone());
        self.send(&normalized);
    }

    fn update(&mut self, id: &str, change: impl FnOnce(ExecutionGraphNode) -> ExecutionGraphNode) {
        self.update_with(id, false, false, change);
    }

    /// `Append`: an entry by its id, a repeated one keeping its first timestamp.
    fn append(&mut self, entry: LogEntry, id: &str) {
        self.update(id, |mut value| {
            match value.entries.iter().position(|e| e.id == entry.id) {
                Some(at) => {
                    let timestamp = value.entries[at].timestamp.clone();
                    value.entries[at] = LogEntry { timestamp, ..entry };
                }
                None => value.entries.push(entry),
            }
            value
        });
    }

    /// `Entry(id, kind, text, activity)`: stamped now, in the run's provider.
    fn entry(&self, id: String, kind: &str, text: String, activity: Option<AgentActivity>) -> LogEntry {
        node::entry(id, kind, text, time::now_o(), Some(&self.provider), activity)
    }

    fn append_activity(&mut self, value: &AgentActivity, id: &str) {
        let entry = self.entry(value.id.clone(), "system", value.summary.clone(), Some(value.clone()));
        self.append(entry, id);
    }

    fn ensure_agent(&mut self, tool_id: &str) -> Option<String> {
        let title = self.locale.get("graph.block.agent");
        self.ensure_node(tool_id, "agent", title)
    }

    /// `EnsureNode`: a Claude sub-agent's or background command's block keeps when it was first seen, for its card's elapsed time.
    fn ensure_node(&mut self, tool_id: &str, kind: &str, title: String) -> Option<String> {
        let id = node::agent_node_id(&self.run_id, tool_id);
        if self.nodes.contains_key(&id) {
            return Some(id);
        }
        if self.nodes.len() >= MAXIMUM_NODES {
            return None;
        }
        let mut value = ExecutionGraphNode::new(id.clone(), self.run_id.clone(), None, kind, "running", title);
        if matches!(kind, "agent" | "task") && self.provider == "claude" {
            value.figures = Some(GraphAgentFigures { started_at: Some(self.now()), ..GraphAgentFigures::default() });
        }
        self.insert(value);
        Some(id)
    }

    fn remember_tool(&mut self, tool: &str, owner: Owner) {
        if !self.tool_owners.contains_key(tool) {
            self.tool_order.push(tool.into());
            if self.tool_order.len() > 512 {
                let oldest = self.tool_order.remove(0);
                self.tool_owners.remove(&oldest);
            }
        }
        // A child owner is more specific than a delayed unscoped Mod event.
        let main = Owner::node(&self.main_id);
        if let Some(old) = self.tool_owners.get(tool)
            && *old != main
            && owner == main
        {
            return;
        }
        self.tool_owners.insert(tool.into(), owner);
    }

    fn node_id_of(&self, owner: &Owner) -> Option<String> {
        if !owner.is_agent { Some(owner.id.clone()) } else { self.aliases.get(&owner.id).cloned() }
    }

    fn set_parent(&mut self, id: &str, owner: &Owner) {
        match self.node_id_of(owner) {
            Some(parent) if parent != id => {
                if self.provider == "codex" {
                    let mut ancestor = Some(parent.clone());
                    let mut seen = HashSet::new();
                    while let Some(at) = ancestor {
                        if !seen.insert(at.clone()) {
                            break;
                        }
                        if at == id {
                            self.pending_parents.shift_remove(id);
                            return;
                        }
                        ancestor = self.nodes.get(&at).and_then(|n| n.parent_id.clone());
                    }
                }
                self.pending_parents.shift_remove(id);
                self.update(id, |n| ExecutionGraphNode { parent_id: Some(parent), ..n });
            }
            _ => {
                if owner.is_agent {
                    self.pending_parents.insert(id.into(), owner.id.clone());
                }
            }
        }
    }

    fn alias(&mut self, agent: &str, id: &str) {
        let existing = self.aliases.get(agent).cloned();
        if existing.as_deref().is_some_and(|e| e != id) {
            return;
        }
        if existing.is_none() && self.aliases.len() >= 512 {
            return;
        }
        self.aliases.insert(agent.into(), id.into());
        let children: Vec<String> = self.pending_parents.iter().filter(|(_, v)| v.as_str() == agent).map(|(k, _)| k.clone()).collect();
        for child in children {
            self.set_parent(&child, &Owner::node(id));
        }
        if let Some(pending) = self.pending_agents.shift_remove(agent) {
            for activity in &pending.activities {
                self.append_activity(activity, id);
            }
            if let Some(metadata) = &pending.metadata {
                self.apply(metadata, id);
            }
        }
    }

    /// `Compaction`: the CLI summarized its context; the block sits under the request, or for Claude under the sub-agent that compacted,
    /// and is complete on arrival.
    fn compaction(&mut self, key: &str, owner: &str, detail: String) {
        if self.finished || self.nodes.len() >= MAXIMUM_NODES {
            return;
        }
        let id = node::identifier(&self.run_id, &format!("compact:{key}"));
        if self.nodes.contains_key(&id) {
            return;
        }
        let parent = if self.nodes.contains_key(owner) { owner.to_string() } else { self.main_id.clone() };
        let mut value =
            ExecutionGraphNode::new(id, self.run_id.clone(), Some(parent), "compact", "completed", labels::compaction_title(&self.locale));
        value.input = Some(detail);
        if let Some(normalized) = node::normalized(&value, false) {
            self.insert(normalized);
        }
    }

    // ── activity rows and pictures ────────────────────────────────────────────────────────────────────────────────────

    /// `Activity(value, toolId)`: true for a known child owner, including an agent whose spawn envelope is still in flight; such rows
    /// never leak into the main log (the parser leaves the row to the graph then).
    pub fn activity(&mut self, value: &AgentActivity, tool_id: Option<&str>) -> bool {
        if self.finished {
            return false;
        }
        let owner = tool_id
            .and_then(|t| self.tool_owners.get(t).cloned())
            .or_else(|| self.activity_owners.get(&value.id).cloned())
            .unwrap_or_else(|| Owner::node(&self.main_id));
        if !self.activity_owners.contains_key(&value.id) {
            self.activity_order.push(value.id.clone());
            if self.activity_order.len() > 512 {
                let oldest = self.activity_order.remove(0);
                self.activity_owners.remove(&oldest);
            }
        }
        self.activity_owners.insert(value.id.clone(), owner.clone());
        if let Some(id) = self.node_id_of(&owner) {
            if id == self.main_id {
                return false;
            }
            self.append_activity(value, &id);
        } else if owner.is_agent && (self.pending_agents.contains_key(&owner.id) || self.pending_agents.len() < MAXIMUM_NODES) {
            let mut pending = self.pending_agents.get(&owner.id).cloned().unwrap_or_default();
            match pending.activities.iter().position(|a| a.id == value.id) {
                Some(at) => pending.activities[at] = value.clone(),
                None => {
                    pending.activities.push(value.clone());
                    let start = pending.activities.len().saturating_sub(MAXIMUM_ENTRIES);
                    pending.activities.drain(..start);
                }
            }
            let bounded = self.bounded_pending(pending, &owner.id);
            self.pending_agents.insert(owner.id.clone(), bounded);
        }
        true
    }

    /// `Images(entry, toolId, parentToolId)`: a child's tool or message pictures belong to its graph block. The spawning tool is the
    /// fallback when the tool's owner was never seen; a child not yet bound to a block never sends its pictures to the main log.
    pub fn images(&mut self, entry: LogEntry, tool_id: Option<&str>, parent_tool_id: Option<&str>) -> bool {
        if self.finished {
            return false;
        }
        if let Some(owner) = tool_id.and_then(|t| self.tool_owners.get(t).cloned())
            && owner != Owner::node(&self.main_id)
        {
            let Some(id) = self.node_id_of(&owner) else { return true };
            if id != self.main_id {
                self.append(entry, &id);
                return true;
            }
        }
        if let Some(parent) = parent_tool_id.and_then(key_str) {
            let child = node::agent_node_id(&self.run_id, parent);
            if self.nodes.contains_key(&child) {
                self.append(entry, &child);
            }
            return true;
        }
        false
    }

    fn bounded_pending(&self, pending: PendingAgent, agent: &str) -> PendingAgent {
        let metadata = pending.metadata;
        let title = metadata
            .as_ref()
            .and_then(|m| m.name.clone().or_else(|| m.agent_type.clone()))
            .unwrap_or_else(|| self.locale.get("graph.block.agent"));
        let mut temporary = ExecutionGraphNode::new(
            node::identifier(&self.run_id, &format!("pending:{agent}")),
            self.run_id.clone(),
            None,
            "agent",
            "running",
            title,
        );
        temporary.input = metadata.as_ref().and_then(|m| m.input.clone());
        temporary.output = metadata.as_ref().and_then(|m| m.output.clone());
        temporary.entries =
            pending.activities.iter().map(|a| self.entry(a.id.clone(), "system", a.summary.clone(), Some(a.clone()))).collect();
        let Some(bounded) = node::normalized(&temporary, false) else { return PendingAgent::default() };
        PendingAgent {
            activities: bounded.entries.iter().filter_map(|e| e.activity.clone()).collect(),
            metadata: metadata.map(|m| ModGraphMetadata {
                version: 1,
                phase: m.phase,
                agent_id: m.agent_id.as_deref().and_then(key_str).map(Into::into),
                parent_agent_id: m.parent_agent_id.as_deref().and_then(key_str).map(Into::into),
                parent_tool_use_id: m.parent_tool_use_id.as_deref().and_then(key_str).map(Into::into),
                name: Some(bounded.title.clone()),
                agent_type: None,
                model: None,
                input: bounded.input.clone(),
                output: bounded.output.clone(),
            }),
        }
    }

    // ── the end ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// `Finish(state)`: the process ended. A dropped spawn's observed results are kept; every unfinished block ends, saying it did not
    /// finish (process success does not prove an unfinished background agent succeeded).
    pub fn finish(&mut self, state: &str) {
        if self.finished || !node::terminal(state) {
            return;
        }
        let mut pending: Vec<(String, PendingAgent)> = self.pending_agents.drain(..).collect();
        pending.sort_by(|a, b| normalize::ordinal(&a.0, &b.0));
        for (agent, pending) in pending {
            if self.nodes.len() >= MAXIMUM_NODES {
                break;
            }
            let id = node::identifier(&self.run_id, &format!("agent:{agent}"));
            let value =
                ExecutionGraphNode::new(id.clone(), self.run_id.clone(), None, "agent", "running", self.locale.get("graph.block.agent"));
            self.insert(value);
            self.aliases.insert(agent.clone(), id.clone());
            for activity in &pending.activities {
                self.append_activity(activity, &id);
            }
            if let Some(metadata) = &pending.metadata {
                self.apply(metadata, &id);
            }
        }
        self.pending_agents.clear();
        self.stop_advisor_calls();
        let parents: Vec<(String, String)> = self.pending_parents.iter().map(|(k, v)| (k.clone(), v.clone())).collect();
        for (id, agent) in parents {
            if let Some(parent) = self.aliases.get(&agent).cloned()
                && id != parent
            {
                self.update(&id, |n| ExecutionGraphNode { parent_id: Some(parent), ..n });
            }
        }
        for id in self.order.clone() {
            if id == self.main_id {
                continue;
            }
            let Some(value) = self.nodes.get(&id) else { continue };
            if node::terminal(&value.state) {
                continue;
            }
            // A notice turn the process ended before its answer ends with the request (macOS).
            if value.kind == "main" {
                self.update(&id, |n| ExecutionGraphNode { state: state.into(), ..n });
                continue;
            }
            let key = match value.kind.as_str() {
                "task" => "graph.block.unfinished.task",
                // A notice or follow-up delegation handed into the turn is a 위임 알림, a task's notification a 작업 알림.
                "steer" if support::is_delegation_notice(value.input.as_deref()) => "graph.block.unfinished.delegationNotice",
                "steer" if support::is_task_notice(value.input.as_deref()) => "graph.block.unfinished.taskNotice",
                "steer" => "graph.block.unfinished.steer",
                "question" => "graph.block.unfinished.question",
                _ => "graph.block.unfinished.agent",
            };
            let entry = self.entry(node::identifier(&self.run_id, &format!("{id}:unfinished")), "system", self.locale.get(key), None);
            let ended = if state == "error" { "error" } else { "stopped" };
            self.update(&id, |mut n| {
                n.state = ended.into();
                n.entries.push(entry);
                n
            });
        }
        let main = self.main_id.clone();
        self.update(&main, |n| ExecutionGraphNode { state: state.into(), ..n });
        self.finished = true;
    }

    // ── the runs ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// `BuildRun(includeRunning)`: the request's own graph as one run, from the latest snapshot of every node. Live snapshots are opt-in;
    /// `finish` settles unfinished blocks.
    pub fn build_run(&self, include_running: bool) -> Option<MightyGraphRun> {
        self.build(&self.run_id, include_running)
    }

    /// `BuildNoticeRuns(includeRunning)`: the notice runs the process's background work opened, oldest first.
    pub fn build_notice_runs(&self, include_running: bool) -> Vec<MightyGraphRun> {
        self.notice_runs.iter().filter_map(|run| self.build(run, include_running)).collect()
    }

    /// `TakeNoticeRuns(includeRunning)`: the notice runs that changed since the last call.
    pub fn take_notice_runs(&mut self, include_running: bool) -> Vec<MightyGraphRun> {
        if self.changed_notice_runs.is_empty() || (!self.finished && !include_running) {
            return vec![];
        }
        let built = self
            .notice_runs
            .iter()
            .filter(|r| self.changed_notice_runs.contains(*r))
            .filter_map(|run| self.build(run, include_running))
            .collect();
        self.changed_notice_runs.clear();
        built
    }

    fn build(&self, id: &str, include_running: bool) -> Option<MightyGraphRun> {
        let main_of = node::main_node_id(id);
        if !self.finished && !include_running {
            return None;
        }
        let main = self.nodes.get(&main_of)?;
        let notice = id != self.run_id;
        let records = |r: &Option<Vec<GraphResponseRecord>>| r.as_ref().map(|list| list.iter().cloned().map(Some).collect());
        let mut run = MightyGraphRun {
            id: Some(id.into()),
            input: Some(main.input.clone().unwrap_or_default()),
            status: Some(main.state.clone()),
            root_entries: Some(vec![]),
            agents: Some(vec![]),
            result_entries: Some(vec![]),
            final_output: main.output.clone().filter(|o| !o.is_empty()),
            usage: main.usage,
            response_records: records(&main.response_records),
            provider: Some(self.provider.clone()),
            source_run_id: Some(id.into()),
            configured_model: self.configured_model.clone(),
            node_model_label: labels::node_model_label(None, self.configured_model.as_deref(), &self.locale),
            notice_run: notice.then_some(true),
            notice_title: (notice && !main.title.is_empty()).then(|| main.title.clone()),
            configured_effort: self.configured_effort.clone(),
            configured_advisor: self.configured_advisor.clone(),
            advisor: main.advisor.clone(),
            ..MightyGraphRun::default()
        };
        let mut agents = vec![];
        for node_id in self.order.iter().filter(|v| **v != main_of) {
            let Some(node) = self.nodes.get(node_id) else { continue };
            if node.run_id != id {
                continue;
            }
            let mut entries = node.entries.clone();
            if let Some(output) = node.output.as_deref().filter(|o| !o.is_empty()) {
                // Codex and Antigravity children take new assignments: one answer per generation.
                let answer_id = if matches!(self.provider.as_str(), "codex" | "gemini") {
                    node::identifier(&self.run_id, &format!("{}:answer:{}", node.id, node.activity_generation.unwrap_or(0)))
                } else {
                    format!("{}-answer", node.id)
                };
                let has_answer = entries.iter().any(|e| {
                    if self.provider == "codex" {
                        e.id.as_deref() == Some(&answer_id)
                    } else {
                        e.id.as_deref() == Some(&answer_id) || (e.kind() == "assistant" && e.text.as_deref() == Some(output))
                    }
                });
                if !has_answer {
                    entries.push(node::entry(answer_id, "assistant", output.into(), time::now_o(), Some(&self.provider), None));
                }
            }
            if agents.len() < 128 {
                agents.push(MightyGraphAgent {
                    id: Some(node.id.clone()),
                    parent_id: node.parent_id.clone().filter(|p| *p != main_of),
                    title: Some(node.title.clone()),
                    input: Some(node.input.clone().unwrap_or_default()),
                    status: Some(node.state.clone()),
                    entries: Some(entries),
                    kind: matches!(node.kind.as_str(), "task" | "steer" | "compact" | "question").then(|| node.kind.clone()),
                    usage: node.usage,
                    activity_generation: node.activity_generation,
                    response_records: records(&node.response_records),
                    background: node.background,
                    output_file: node.output_file.clone(),
                    notice_reply: node.notice_reply.clone(),
                    source_block_id: node.source_block_id.clone(),
                    identity: node.identity.clone(),
                    figures: node.figures.clone(),
                });
            }
        }
        run.agents = Some(agents);
        normalize::apply_provider(&mut run, &self.provider);
        normalize::refresh_result(&mut run);
        Some(run)
    }

    // ── usage ────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// `RecordUsage`: one message comes as several events that all carry its usage, so a block adds each message once and keeps its
    /// latest figure; on first observation the model and the tool calls are recorded, and later events add the calls they name.
    fn record_usage(
        &mut self,
        usage: GraphTokenUsage,
        message: &str,
        node_id: &str,
        model: Option<&str>,
        activity_ids: &[String],
        marked: bool,
    ) {
        if !self.nodes.contains_key(node_id) {
            return;
        }
        let previous = self.message_usage.get(message).copied();
        let mut named = false;
        if previous.is_some() && !activity_ids.is_empty() {
            let mut ids = self.message_activity_ids.get(message).cloned().unwrap_or_default();
            for id in activity_ids {
                if !ids.contains(id) {
                    ids.push(id.clone());
                    named = true;
                }
            }
            if named {
                self.message_activity_ids.insert(message.into(), ids);
            }
        }
        if previous == Some(usage) && !named {
            return;
        }
        if previous.is_none() {
            self.message_order.push(message.into());
            if self.message_order.len() > 1_024 {
                let oldest = self.message_order.remove(0);
                self.message_usage.remove(&oldest);
            }
            if let Some(model) = model {
                self.message_model.insert(message.into(), model.into());
            }
            self.message_activity_ids.insert(message.into(), activity_ids.to_vec());
            self.message_configured.insert(message.into(), marked);
            self.node_response_order.entry(node_id.into()).or_default().push(message.into());
        }
        self.message_usage.insert(message.into(), usage);
        let records = self.build_response_records(node_id);
        let before = previous.unwrap_or_default();
        self.update(node_id, |n| ExecutionGraphNode {
            usage: Some(node::add(node::subtract(n.usage.unwrap_or_default(), before), usage)),
            response_records: Some(records),
            ..n
        });
    }

    fn build_response_records(&self, node_id: &str) -> Vec<GraphResponseRecord> {
        let Some(responses) = self.node_response_order.get(node_id) else { return vec![] };
        responses
            .iter()
            .filter_map(|id| {
                let usage = *self.message_usage.get(id)?;
                Some(node::response_record(
                    id,
                    self.message_model.get(id).map(String::as_str),
                    usage,
                    self.message_activity_ids.get(id).map_or(&[][..], Vec::as_slice),
                    self.message_configured.get(id).copied().unwrap_or(false),
                ))
            })
            .collect()
    }

    /// `Identify`: what the CLI said about who an agent block is, added to what the block holds. `replacing`: the Agent call's own words,
    /// which a repeated event restates; any other source only fills what is still unsaid. The model that runs is the CLI's latest word.
    fn identify(
        mut node: ExecutionGraphNode,
        kind: Option<&str>,
        requested: Option<&str>,
        resolved: Option<&str>,
        isolation: Option<&str>,
        replacing: bool,
    ) -> ExecutionGraphNode {
        let mut identity = node.identity.take().unwrap_or_default();
        let value = |incoming: Option<&str>, current: Option<String>| -> Option<String> {
            match incoming.filter(|i| !i.is_empty()) {
                None => current,
                Some(incoming) if replacing || current.is_none() => Some(incoming.into()),
                Some(_) => current,
            }
        };
        identity.kind = value(kind, identity.kind);
        identity.requested_model = value(requested, identity.requested_model);
        if let Some(resolved) = resolved.filter(|r| !r.is_empty()) {
            identity.resolved_model = Some(resolved.into());
        }
        identity.isolation = value(isolation, identity.isolation);
        node.identity = (identity != GraphAgentIdentity::default()).then_some(identity);
        node
    }

    /// `Apply`: a Mods event's graph metadata on its block.
    fn apply(&mut self, metadata: &ModGraphMetadata, id: &str) {
        let agent_title = self.locale.get("graph.block.agent");
        let metadata = metadata.clone();
        self.update(id, |mut node| {
            // Mods names a block the stream has not titled; the call's own description, once read, stays its title.
            if let Some(title) = metadata.name.clone().or(metadata.agent_type.clone()).filter(|t| !t.is_empty())
                && (node.title.is_empty() || node.title == agent_title)
            {
                node.title = title;
            }
            if metadata.input.is_some() {
                node.input = metadata.input.clone();
            }
            if metadata.output.is_some() {
                node.output = metadata.output.clone();
            }
            let state = if metadata.phase == "starting" { "running" } else { metadata.phase.as_str() };
            if node::ACTIVITY_STATES.contains(&state) {
                node.state = state.into();
            }
            // Mods names the agent's type, the model the call asked for (while it starts) and the one the CLI resolved (once it runs).
            if node.kind == "agent" {
                let ran = matches!(metadata.phase.as_str(), "running" | "completed");
                node = Self::identify(
                    node,
                    metadata.agent_type.as_deref(),
                    if metadata.phase == "starting" { metadata.model.as_deref() } else { None },
                    if ran { metadata.model.as_deref() } else { None },
                    None,
                    false,
                );
            }
            node
        });
    }

    /// `ReceiveMod`: a Mods event.
    pub fn receive_mod(&mut self, value: &ModMetadata) {
        if self.finished {
            return;
        }
        if let Some(tool) = value.tool_use_id.as_deref().and_then(key_str) {
            if let Some(agent) = value.agent_id.as_deref().and_then(key_str) {
                self.remember_tool(tool, Owner::agent(agent));
            } else if matches!(value.event.as_str(), "tool.call" | "tool.complete") {
                let main = Owner::node(&self.main_id);
                self.remember_tool(tool, main);
            }
        }
        let Some(metadata) = value.graph.as_ref().filter(|g| g.version == 1) else { return };
        let id = if let Some(tool_id) = metadata.parent_tool_use_id.as_deref().and_then(key_str) {
            if self.tool_names.get(tool_id).is_some_and(|name| !agent_tool(Some(name))) {
                return;
            }
            let id = self.ensure_agent(tool_id);
            if let Some(id) = &id {
                let parent = match metadata.parent_agent_id.as_deref().and_then(key_str) {
                    Some(parent) => Owner::agent(parent),
                    None => Owner::node(&self.main_id),
                };
                self.set_parent(id, &parent);
                // The Agent call belongs to its parent, never to the spawned child.
                self.remember_tool(tool_id, parent);
                if let Some(agent) = metadata.agent_id.as_deref().and_then(key_str) {
                    self.alias(agent, id);
                }
            }
            id
        } else {
            metadata.agent_id.as_deref().and_then(key_str).and_then(|a| self.aliases.get(a).cloned())
        };
        if let Some(id) = id {
            self.apply(metadata, &id);
        } else if let Some(agent) = metadata.agent_id.as_deref().and_then(key_str)
            && (self.pending_agents.contains_key(agent) || self.pending_agents.len() < MAXIMUM_NODES)
        {
            let mut pending = self.pending_agents.get(agent).cloned().unwrap_or_default();
            // Keep a final observation when a late starting event arrives.
            let previous = pending.metadata.as_ref().map(|m| m.phase.clone());
            match previous.as_deref() {
                Some(p @ ("error" | "stopped")) => {
                    if metadata.phase == p {
                        pending.metadata = Some(metadata.clone());
                    }
                }
                _ => {
                    if pending.metadata.is_none() || !node::terminal(previous.as_deref().unwrap_or("")) || node::terminal(&metadata.phase) {
                        pending.metadata = Some(metadata.clone());
                    }
                }
            }
            let bounded = self.bounded_pending(pending, agent);
            self.pending_agents.insert(agent.into(), bounded);
        }
    }

    // ── tool calls of a sub-agent ─────────────────────────────────────────────────────────────────────────────────────

    /// `CountTool`: one more tool call of a sub-agent block, counted once however often its message is sent.
    fn count_tool(&mut self, tool: &str, node_id: &str) {
        if self.provider != "claude" || self.kind_of(node_id) != Some("agent") || !self.counted_tools.insert(tool.into()) {
            return;
        }
        self.counted_order.push(tool.into());
        if self.counted_order.len() > 4_096 {
            let oldest = self.counted_order.remove(0);
            self.counted_tools.remove(&oldest);
        }
        *self.seen_tools.entry(node_id.into()).or_default() += 1;
        self.publish_tools(node_id);
    }

    /// `PublishTools`: the block's tool calls, what its messages showed or what the CLI reported, whichever is more.
    fn publish_tools(&mut self, node_id: &str) {
        let count = GraphAgentFigures_MAXIMUM_TOOL_CALLS
            .min(self.seen_tools.get(node_id).copied().unwrap_or(0).max(self.reported_tools.get(node_id).copied().unwrap_or(0)));
        if count <= 0 || Some(count) == self.node(node_id).and_then(|n| n.figures.as_ref()).and_then(|f| f.tool_calls) {
            return;
        }
        self.update(node_id, |mut value| {
            let mut figures = value.figures.take().unwrap_or_default();
            figures.tool_calls = Some(count);
            value.figures = Some(figures);
            value
        });
    }

    /// `ReportTools`: the CLI's own count of an agent's tool calls (`usage.tool_uses`), taken when it is more than was counted.
    fn report_tools(&mut self, frame: &Value) {
        let Some(tool) = key(prop(Some(frame), "tool_use_id")) else { return };
        let Some(count) = prop(prop(Some(frame), "usage"), "tool_uses").and_then(Value::as_i64).filter(|c| *c > 0 && *c <= i32::MAX as i64)
        else {
            return;
        };
        let count = count as i32;
        let node_id = node::agent_node_id(&self.run_id, tool);
        if self.provider != "claude"
            || self.kind_of(&node_id) != Some("agent")
            || count <= self.reported_tools.get(&node_id).copied().unwrap_or(0)
        {
            return;
        }
        self.reported_tools.insert(node_id.clone(), count);
        self.publish_tools(&node_id);
    }
}

/// `GraphAgentFigures.MaximumToolCalls`.
#[allow(non_upper_case_globals)]
const GraphAgentFigures_MAXIMUM_TOOL_CALLS: i32 = 1_000_000;

// ── JSON as `MetadataJson` and the tracker's own helpers read it ─────────────────────────────────────────────────────────────

/// `MetadataJson.Property`: a member of an object, none otherwise.
fn prop<'a>(value: Option<&'a Value>, key: &str) -> Option<&'a Value> {
    value?.as_object()?.get(key)
}

/// `JsonElement.Text(property)`: a string member.
fn text<'a>(value: Option<&'a Value>, key: &str) -> Option<&'a str> {
    prop(value, key)?.as_str()
}

/// `IsTrue`: the member is JSON `true`.
fn is_true(value: Option<&Value>, key: &str) -> bool {
    prop(value, key) == Some(&Value::Bool(true))
}

/// `MetadataJson.SafeInteger`: an integral number within JavaScript's safe range.
fn safe_integer(value: Option<&Value>) -> Option<i64> {
    let n = value?.as_f64().filter(|f| f.is_finite())?;
    (n.trunc() == n && (-9_007_199_254_740_991.0..=9_007_199_254_740_991.0).contains(&n)).then_some(n as i64)
}

/// `Key(JsonElement)`: a non-empty string of at most 512 UTF-8 bytes.
fn key(value: Option<&Value>) -> Option<&str> {
    value?.as_str().and_then(key_str)
}

fn key_str(value: &str) -> Option<&str> {
    (!value.is_empty() && value.len() <= 512).then_some(value)
}

/// `Objects`: the array's elements when every one is an object.
fn objects(value: Option<&Value>) -> Option<&Vec<Value>> {
    let list = value?.as_array()?;
    list.iter().all(Value::is_object).then_some(list)
}

/// `Text(JsonElement)`: a string, or the text blocks of a content array joined by new lines.
fn content_text(value: Option<&Value>) -> Option<String> {
    if let Some(text) = value.and_then(Value::as_str) {
        return Some(text.into());
    }
    let blocks = objects(value)?;
    let values: Vec<&str> = blocks.iter().filter(|b| text(Some(b), "type") == Some("text")).filter_map(|b| text(Some(b), "text")).collect();
    (!values.is_empty()).then(|| values.join("\n"))
}

/// `AgentTool`: an Agent (or Task) call.
fn agent_tool(name: Option<&str>) -> bool {
    name.is_some_and(|n| matches!(n.to_lowercase().as_str(), "agent" | "task"))
}

fn frame_time(value: &Value) -> Option<String> {
    normalize::graph_time(text(Some(value), "timestamp"))
}

/// `ClaudeStream.IsNotificationResult`: a `result` a task notification or a sub-agent's report opened, not the request's.
pub fn is_notification_result(value: &Value) -> bool {
    text(Some(value), "type") == Some("result")
        && text(prop(Some(value), "origin"), "kind").is_some_and(|k| matches!(k, "task-notification" | "task_notification" | "peer"))
}

/// `ExecutionGraphTracker.QuestionSummary`: the header and the readable body of an AskUserQuestion input.
pub fn question_summary(input: Option<&Value>, locale: &Locale) -> (Option<String>, String) {
    let questions = objects(prop(input, "questions")).map(Vec::as_slice).unwrap_or(&[]);
    let header = questions.first().and_then(|q| text(Some(q), "header"));
    let title = header.filter(|h| !h.is_empty()).map(|h| format!("{}{h}", locale.get("graph.block.questionPrefix")));
    let body = questions
        .iter()
        .map(|question| {
            let question_text = text(Some(question), "question").unwrap_or("");
            let options: Vec<&str> = objects(prop(Some(question), "options"))
                .map(|o| o.iter().filter_map(|o| text(Some(o), "label")).collect())
                .unwrap_or_default();
            if options.is_empty() {
                question_text.to_string()
            } else {
                format!("{question_text}\n{}", options.iter().map(|o| format!("  ○ {o}")).collect::<Vec<_>>().join("\n"))
            }
        })
        .collect::<Vec<_>>()
        .join("\n\n");
    (title, body)
}
