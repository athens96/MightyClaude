//! Codex exec JSONL (ExecutionGraphTracker.cs:16-76, 739-857; ExecutionGraphTracker.CodexSessions.cs). Codex has no hook bridge: the root
//! thread's items carry the final message and the turn's usage; `collab_tool_call` items describe sub-agents by thread id with a prompt
//! and a state, not their inner work, which the session files add (`codex_session`).

use std::collections::{BTreeMap, HashMap};

use mighty_core::locale::Locale;
use serde_json::Value;

use super::{CodexSessionAgent, ExecutionGraphTracker, Owner, key, prop, text};
use crate::node::{self, ExecutionGraphNode, MAXIMUM_INPUT_BYTES, MAXIMUM_NODES, MAXIMUM_OUTPUT_BYTES, clean, prefix};

/// One agent's state in a `collab_tool_call` (`CodexCollaborationItem.AgentState(Status, Message?)`).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CodexAgentState {
    pub status: String,
    pub message: Option<String>,
}

/// C# `CodexCollaborationItem`: one Codex `collab_tool_call` item.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CodexCollaborationItem {
    pub id: String,
    pub tool: String,
    pub sender: Option<String>,
    pub receivers: Vec<String>,
    /// By thread, in ordinal order.
    pub states: BTreeMap<String, CodexAgentState>,
    pub prompt: Option<String>,
    pub status: String,
}

/// `Rune.GetUnicodeCategory(rune) is Format` (Cf).
fn is_format(c: char) -> bool {
    matches!(c as u32,
        0x00AD | 0x0600..=0x0605 | 0x061C | 0x06DD | 0x070F | 0x0890..=0x0891 | 0x08E2 | 0x180E | 0x200B..=0x200F | 0x202A..=0x202E
        | 0x2060..=0x2064 | 0x2066..=0x206F | 0xFEFF | 0xFFF9..=0xFFFB | 0x110BD | 0x110CD | 0x13430..=0x1343F | 0x1BCA0..=0x1BCA3
        | 0x1D173..=0x1D17A | 0xE0001 | 0xE0020..=0xE007F)
}

/// `CodexCollaborationItem.Key`: a non-empty thread or call id of at most 512 bytes with no control or format character.
pub fn codex_key(text: Option<&str>) -> Option<&str> {
    let text = text?;
    (!text.is_empty() && text.len() <= 512 && !text.chars().any(|c| c.is_control() || is_format(c))).then_some(text)
}

/// `StringComparer.Ordinal` order of UTF-16 units.
fn ordinal(a: &str, b: &str) -> std::cmp::Ordering {
    a.encode_utf16().cmp(b.encode_utf16())
}

impl CodexCollaborationItem {
    /// `CodexCollaborationItem.Parse`.
    pub fn parse(item: &Value) -> Option<CodexCollaborationItem> {
        let item_ref = Some(item);
        if !matches!(text(item_ref, "type"), Some("collab_tool_call" | "collab_agent_tool_call")) {
            return None;
        }
        let id = codex_key(prop(item_ref, "id").and_then(Value::as_str))?;
        let tool = text(item_ref, "tool").filter(|t| matches!(*t, "spawn_agent" | "send_input" | "wait" | "close_agent"))?;
        let mut receivers: Vec<String> = vec![];
        if let Some(raw) = prop(item_ref, "receiver_thread_ids").and_then(Value::as_array)
            && raw.iter().all(Value::is_string)
        {
            for value in raw.iter().take(MAXIMUM_NODES) {
                if let Some(thread) = codex_key(value.as_str())
                    && !receivers.iter().any(|r| r == thread)
                {
                    receivers.push(thread.into());
                }
            }
        }
        let mut states = BTreeMap::new();
        if let Some(agents) = prop(item_ref, "agents_states").and_then(Value::as_object) {
            let mut names: Vec<&String> = agents.keys().collect();
            names.sort_by(|a, b| ordinal(a, b));
            for name in names.into_iter().take(MAXIMUM_NODES) {
                let value = &agents[name.as_str()];
                let Some(thread) = codex_key(Some(name)) else { continue };
                if !value.is_object() {
                    continue;
                }
                let Some(status) = text(Some(value), "status") else { continue };
                states.insert(
                    thread.to_string(),
                    CodexAgentState {
                        status: prefix(status, 80),
                        message: text(Some(value), "message").map(|m| clean(m, MAXIMUM_OUTPUT_BYTES, false)),
                    },
                );
            }
        }
        let prompt = text(item_ref, "prompt").map(|p| clean(p, MAXIMUM_INPUT_BYTES, false)).filter(|p| !p.is_empty());
        Some(CodexCollaborationItem {
            id: id.into(),
            tool: tool.into(),
            sender: codex_key(prop(item_ref, "sender_thread_id").and_then(Value::as_str)).map(Into::into),
            receivers,
            states,
            prompt,
            status: text(item_ref, "status").unwrap_or("in_progress").into(),
        })
    }

    /// `Threads`: the receivers, then the other agents the states name in ordinal order, at most 128.
    pub fn threads(&self) -> Vec<String> {
        let mut others: Vec<&String> = self.states.keys().filter(|k| !self.receivers.contains(k)).collect();
        others.sort_by(|a, b| ordinal(a, b));
        self.receivers.iter().cloned().chain(others.into_iter().cloned()).take(MAXIMUM_NODES).collect()
    }

    /// `Summary`: the call's one line as a tool row — what it does, its prompt, and the first agents it names.
    pub fn summary(&self, locale: &Locale) -> String {
        let action = locale.get(match self.tool.as_str() {
            "spawn_agent" => "run.agent.spawn",
            "send_input" => "run.agent.sendInput",
            "wait" => "run.agent.wait",
            _ => "run.agent.close",
        });
        let targets = self.threads().iter().take(4).map(|t| prefix(t, 12)).collect::<Vec<_>>().join(", ");
        let parts: Vec<&str> = [Some(action.as_str()), self.prompt.as_deref(), (!targets.is_empty()).then_some(targets.as_str())]
            .into_iter()
            .flatten()
            .collect();
        clean(&parts.join(" · "), node::ACTIVITY_SUMMARY_BYTES, true)
    }

    /// `Output`: each named agent's state and what it said, none while none has reported.
    pub fn output(&self) -> Option<String> {
        let rows: Vec<String> = self
            .threads()
            .iter()
            .filter_map(|thread| {
                let state = self.states.get(thread)?;
                let label = format!("{} · {}", prefix(thread, 12), state.status);
                Some(match &state.message {
                    Some(message) => format!("{label}\n{message}"),
                    None => label,
                })
            })
            .collect();
        (!rows.is_empty()).then(|| clean(&rows.join("\n\n"), node::ACTIVITY_OUTPUT_BYTES, false))
    }
}

/// `CodexAgentState(status)`: a Codex agent's status as a block state.
fn agent_state(status: &str) -> Option<&'static str> {
    Some(match status {
        "pending_init" | "running" => "running",
        "completed" => "completed",
        "interrupted" | "shutdown" => "stopped",
        "errored" | "not_found" => "error",
        _ => return None,
    })
}

/// `ExecutionGraphTracker.CodexUsage`: a turn's usage in Codex's names.
pub fn codex_usage(value: Option<&Value>) -> Option<mighty_core::graph::GraphTokenUsage> {
    node::mapped_usage(
        value,
        &[
            ("input_tokens", "input_tokens"),
            ("output_tokens", "output_tokens"),
            ("cached_input_tokens", "cache_read_input_tokens"),
            ("cache_write_input_tokens", "cache_creation_input_tokens"),
        ],
    )
}

impl ExecutionGraphTracker {
    fn ensure_codex_agent(&mut self, thread: &str) -> Option<String> {
        if let Some(existing) = self.codex_agents.get(thread) {
            return Some(existing.clone());
        }
        if self.codex_agents.len() >= MAXIMUM_NODES || self.nodes.len() >= MAXIMUM_NODES {
            return None;
        }
        let id = node::identifier(&self.run_id, &format!("codex-agent:{thread}"));
        let value = ExecutionGraphNode::new(
            id.clone(),
            self.run_id.clone(),
            Some(self.main_id.clone()),
            "agent",
            "running",
            format!("Codex · {}", prefix(thread, 12)),
        );
        self.insert(value);
        self.codex_agents.insert(thread.into(), id.clone());
        self.alias(thread, &id);
        Some(id)
    }

    pub(super) fn consume_codex(&mut self, value: &Value) {
        let frame = Some(value);
        let Some(kind) = text(frame, "type") else { return };
        if kind == "thread.started"
            && let Some(root) = codex_key(prop(frame, "thread_id").and_then(Value::as_str))
        {
            self.codex_root_thread = Some(root.into());
            let main = self.main_id.clone();
            self.alias(root, &main);
            return;
        }
        if kind == "turn.completed" {
            let Some(usage) = codex_usage(prop(frame, "usage")) else { return };
            // Each turn reports its own usage once; sum turns for the main block.
            self.unnamed_usage += 1;
            let message = format!("turn:{}", self.unnamed_usage);
            let main = self.main_id.clone();
            let model = self.configured_model.clone();
            self.record_usage(usage, &message, &main, model.as_deref(), &[], model.is_some());
            return;
        }
        let item = prop(frame, "item").filter(|i| i.is_object());
        if !matches!(kind, "item.started" | "item.updated" | "item.completed") {
            return;
        }
        let Some(item) = item else { return };
        let Some(item_type) = text(Some(item), "type") else { return };
        if item_type == "agent_message" {
            if kind == "item.completed"
                && let Some(answer) = text(Some(item), "text").filter(|t| !t.is_empty())
            {
                let answer = answer.to_string();
                let main = self.main_id.clone();
                self.update(&main, |n| ExecutionGraphNode { output: Some(answer), ..n });
            }
            return;
        }
        if item_type == "context_compaction" {
            if kind != "item.completed" {
                return;
            }
            self.compactions += 1;
            let key = key(prop(Some(item), "id")).map(str::to_string).unwrap_or_else(|| format!("n{}", self.compactions));
            let main = self.main_id.clone();
            let detail = crate::labels::codex_compaction_summary(&self.locale);
            self.compaction(&key, &main, detail);
            return;
        }
        let Some(call) = CodexCollaborationItem::parse(item) else { return };
        // Completed envelopes may be repeated or followed by a late start; an acknowledged input creates at most one new generation.
        if self.codex_settled_calls.contains(&call.id) {
            return;
        }
        let threads = call.threads();
        if !self.codex_call_generations.contains_key(&call.id) {
            self.codex_observed_call_order.push(call.id.clone());
            if self.codex_observed_call_order.len() > 512 {
                let oldest = self.codex_observed_call_order.remove(0);
                self.codex_call_generations.remove(&oldest);
            }
            let generations: HashMap<String, i32> = threads
                .iter()
                .map(|t| {
                    let generation =
                        self.codex_agents.get(t).and_then(|id| self.nodes.get(id)).and_then(|n| n.activity_generation).unwrap_or(0);
                    (t.clone(), generation)
                })
                .collect();
            self.codex_call_generations.insert(call.id.clone(), generations);
        }
        let completed = kind == "item.completed";
        let succeeded = completed && call.status == "completed";
        if completed {
            self.codex_settled_calls.insert(call.id.clone());
            self.codex_call_order.push(call.id.clone());
            if self.codex_call_order.len() > 512 {
                let oldest = self.codex_call_order.remove(0);
                self.codex_settled_calls.remove(&oldest);
            }
        }
        let owner = match (&call.sender, &self.codex_root_thread) {
            (Some(sender), Some(root)) if sender != root => Owner::agent(sender),
            _ => Owner::node(&self.main_id),
        };
        self.remember_tool(&call.id, owner.clone());
        let root = self.codex_root_thread.clone();
        for thread in threads.iter().filter(|t| Some(*t) != root.as_ref()) {
            let Some(id) = self.ensure_codex_agent(thread) else { continue };
            // A wait already in flight before a new instruction may finish late with the previous assignment's answer: keep its tool row,
            // but do not settle or overwrite the newer child generation.
            let addressed = call.receivers.contains(thread);
            let current_generation = self.nodes.get(&id).and_then(|n| n.activity_generation).unwrap_or(0);
            let stale =
                self.codex_call_generations.get(&call.id).and_then(|g| g.get(thread)).is_some_and(|seen| *seen < current_generation);
            // Every accepted input is a real assignment, even if two sends were in flight together.
            if stale && !(succeeded && call.tool == "send_input" && addressed) {
                continue;
            }
            // Resolve the sender later if a nested spawn precedes its parent; never manufacture another root.
            if call.tool == "spawn_agent" && addressed {
                self.set_parent(&id, &owner);
            }
            let restarting = succeeded && call.tool == "send_input" && addressed;
            if restarting && let Some(previous) = self.nodes.get(&id).and_then(|n| n.output.clone()).filter(|o| !o.is_empty()) {
                let generation = self.nodes[&id].activity_generation.unwrap_or(0);
                let entry = self.entry(node::identifier(&self.run_id, &format!("{id}:answer:{generation}")), "assistant", previous, None);
                self.append(entry, &id);
            }
            let thread_name = thread.clone();
            let call_ref = &call;
            self.update_with(&id, restarting, false, |mut node| {
                if call_ref.tool == "spawn_agent"
                    && addressed
                    && let Some(prompt) = &call_ref.prompt
                    && node.input.is_none()
                {
                    node.title = format!("{} · {}", clean(prompt, 100, true), prefix(&thread_name, 12));
                    node.input = Some(prompt.clone());
                }
                if restarting {
                    node.state = "running".into();
                    node.output = None;
                }
                if !stale && let Some(state) = call_ref.states.get(&thread_name) {
                    if let Some(mapped) = agent_state(&state.status)
                        && !(state.status == "shutdown" && node.state == "completed")
                    {
                        node.state = mapped.into();
                    }
                    if let Some(message) = state.message.as_ref().filter(|m| !m.is_empty()) {
                        node.output = Some(message.clone());
                    }
                }
                // The call's failure reports an operation failure, not a child failure. A successful spawn does not complete its child.
                if addressed && succeeded && call_ref.tool == "close_agent" && !node::terminal(&node.state) {
                    node.state = "stopped".into();
                }
                node
            });
            if restarting && let Some(input) = &call.prompt {
                let entry = self.entry(node::identifier(&self.run_id, &format!("{id}:input:{}", call.id)), "user", input.clone(), None);
                self.append(entry, &id);
            }
        }
    }

    /// `CodexSession(agent)` (ExecutionGraphTracker.CodexSessions.cs): merges only descendants the session watcher already scoped.
    pub fn codex_session(&mut self, agent: &CodexSessionAgent) {
        if self.finished || self.provider != "codex" {
            return;
        }
        let Some(id) = self.ensure_codex_agent(&agent.thread) else { return };
        let owner = if Some(&agent.parent_thread) == self.codex_root_thread.as_ref() {
            Owner::node(&self.main_id)
        } else {
            Owner::agent(&agent.parent_thread)
        };
        self.set_parent(&id, &owner);
        let seen = self.codex_session_turns.get(&agent.thread).copied().unwrap_or(0);
        let old = self.nodes[&id].clone();
        let reopening = seen > 0 && agent.turns > seen && node::terminal(&old.state);
        self.codex_session_turns.insert(agent.thread.clone(), seen.max(agent.turns));
        for record in &agent.usage {
            let message = format!("codex-session:{}:{}", agent.thread, record.response_id.as_deref().unwrap_or(""));
            let ids: Vec<String> = node::activity_ids(record).map(str::to_string).collect();
            self.record_usage(node::record_usage(record), &message, &id, record.model.as_deref(), &ids, false);
        }
        if reopening
            && let Some(previous) = old.output.as_ref().filter(|o| !o.is_empty())
            && !agent.entries.iter().any(|e| e.kind() == "assistant" && e.text.as_ref() == Some(previous))
        {
            let entry = self.entry(
                node::identifier(&self.run_id, &format!("{id}:answer:{}", old.activity_generation.unwrap_or(0))),
                "assistant",
                previous.clone(),
                None,
            );
            self.append(entry, &id);
        }
        self.update_with(&id, reopening, false, |mut value| {
            for entry in &agent.entries {
                if agent.answers.contains(entry.id()) && value.entries.iter().any(|e| e.kind() == "assistant" && e.text == entry.text) {
                    continue;
                }
                match value.entries.iter().position(|e| e.id == entry.id) {
                    None => value.entries.push(entry.clone()),
                    Some(at) => {
                        let timestamp = value.entries[at].timestamp.clone();
                        value.entries[at] = mighty_core::model::LogEntry { timestamp, ..entry.clone() };
                    }
                }
            }
            value.title = agent.title.clone().unwrap_or(value.title);
            value.input = value.input.or(agent.input.clone());
            if reopening || agent.output.is_some() {
                value.output = agent.output.clone();
            }
            value.state = agent.state.clone();
            value
        });
    }
}
