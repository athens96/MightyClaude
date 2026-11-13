//! The other agents' streams: Antigravity's stream-json (ExecutionGraphTracker.Antigravity.cs), pi's RPC events (PiSupport.cs:112-133)
//! and an ACP agent's prompt result (ExecutionGraphTracker.Acp.cs, opencode; Hermes's block carries no tokens).

use std::sync::LazyLock;

use regex::Regex;
use serde_json::Value;

use super::{AntigravitySubagent, CodexSessionAgent, ExecutionGraphTracker, Owner, key, prop, text};
use crate::node::{self, ExecutionGraphNode, MAXIMUM_NODES};
use mighty_core::wire;

static UUID: LazyLock<Regex> =
    LazyLock::new(|| Regex::new(r"\A[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\z").expect("regex"));

/// `AntigravityRecords.ConversationID`: none for anything that is not a conversation id.
pub fn conversation_id(value: Option<&str>) -> Option<&str> {
    value.filter(|v| UUID.is_match(v))
}

/// `AntigravityRecords.Title(role, typeName)`: "role (type)", the one that is there, or none.
pub fn antigravity_title(role: Option<&str>, type_name: Option<&str>) -> Option<String> {
    let name = role.map(str::trim).unwrap_or("");
    let kind = type_name.map(str::trim).unwrap_or("");
    if name.is_empty() {
        return (!kind.is_empty()).then(|| kind.to_string());
    }
    Some(if kind.is_empty() || kind == name { name.to_string() } else { format!("{name} ({kind})") })
}

/// `AntigravityRecords.Usage`: an `agent_response` step's usage in agy's names.
pub fn antigravity_usage(value: Option<&Value>) -> Option<mighty_core::graph::GraphTokenUsage> {
    node::mapped_usage(
        value,
        &[("input_tokens", "input_tokens"), ("output_tokens", "output_tokens"), ("cache_read_tokens", "cache_read_input_tokens")],
    )
}

/// `JsonElement.ToString()` of a member: a string's value, any other value's JSON text, "" for null.
fn element_text(value: &Value) -> String {
    match value {
        Value::String(s) => s.clone(),
        Value::Null => String::new(),
        other => other.to_string(),
    }
}

impl ExecutionGraphTracker {
    /// `ConsumeAcp`: opencode's prompt result carries the turn's tokens.
    pub(super) fn consume_acp(&mut self, value: &Value) {
        let frame = Some(value);
        if text(frame, "type") != Some("acp.prompt_result") {
            return;
        }
        let Some(usage) = prop(frame, "usage").filter(|u| u.is_object()) else { return };
        let names = [
            ("inputTokens", "input_tokens"),
            ("outputTokens", "output_tokens"),
            ("cachedReadTokens", "cache_read_input_tokens"),
            ("cachedWriteTokens", "cache_creation_input_tokens"),
        ];
        if let Some(tokens) = node::mapped_usage(Some(usage), &names) {
            let message = format!("acp:{}", text(frame, "sessionId").unwrap_or("turn"));
            let main = self.main_id.clone();
            self.record_usage(tokens, &message, &main, None, &[], false);
        }
    }

    /// `ConsumePi`: pi has no sub-agents; each assistant message adds its usage and the latest message with text is the answer.
    pub(super) fn consume_pi(&mut self, value: &Value) {
        let frame = Some(value);
        if text(frame, "type") != Some("message_end") {
            return;
        }
        let Some(message) = prop(frame, "message").filter(|m| m.is_object()) else { return };
        if text(Some(message), "role") != Some("assistant") {
            return;
        }
        let object = message.as_object().expect("an object");
        let name = match (object.get("responseId"), object.get("timestamp")) {
            (Some(response), _) if !response.is_null() => element_text(response),
            (_, Some(stamp)) => element_text(stamp),
            _ => "message".into(),
        };
        let message_key = format!("pi:{name}");
        if let Some(usage) = prop(Some(message), "usage").filter(|u| u.is_object()) {
            let names = [
                ("input", "input_tokens"),
                ("output", "output_tokens"),
                ("cacheRead", "cache_read_input_tokens"),
                ("cacheWrite", "cache_creation_input_tokens"),
            ];
            if let Some(tokens) = node::mapped_usage(Some(usage), &names) {
                let model = text(Some(message), "model").filter(|m| wire::model(m));
                let main = self.main_id.clone();
                self.record_usage(tokens, &message_key, &main, model, &[], false);
            }
        }
        let answer = prop(Some(message), "content")
            .and_then(Value::as_array)
            .map(|blocks| {
                blocks
                    .iter()
                    .filter(|b| text(Some(b), "type") == Some("text"))
                    .filter_map(|b| text(Some(b), "text"))
                    .collect::<Vec<_>>()
                    .join("\n")
            })
            .unwrap_or_default();
        if !answer.is_empty() {
            let main = self.main_id.clone();
            self.update(&main, |n| ExecutionGraphNode { output: Some(answer), ..n });
        }
    }

    /// `ConsumeAntigravity`: each `agent_response` step carries its own usage; a `subagent` step names each sub-agent's conversation, role
    /// and prompt.
    pub(super) fn consume_antigravity(&mut self, value: &Value) {
        let frame = Some(value);
        let Some(kind) = text(frame, "event") else { return };
        let payload = prop(frame, kind);
        match kind {
            "init" => {
                if let Some(started) = key(prop(frame, "conversation_id")) {
                    self.antigravity_conversation = Some(started.into());
                    let main = self.main_id.clone();
                    self.alias(started, &main);
                }
                if let Some(model) = text(payload, "model").filter(|m| wire::model(m)) {
                    self.antigravity_model = Some(model.into());
                }
            }
            "step_update" => {
                let index = prop(payload, "step_index").and_then(Value::as_i64).unwrap_or(-1);
                match text(payload, "step_type") {
                    Some("agent_response") => {
                        let Some(usage) = antigravity_usage(prop(payload, "usage")) else { return };
                        let model = self.antigravity_model.clone().or(self.configured_model.clone());
                        let marked = self.antigravity_model.is_none() && self.configured_model.is_some();
                        let main = self.main_id.clone();
                        self.record_usage(usage, &format!("step:{index}"), &main, model.as_deref(), &[], marked);
                    }
                    Some("subagent") => {
                        let failed = text(payload, "state").is_some_and(|s| s.to_uppercase() == "ERROR");
                        let Some(specs) = prop(prop(payload, "subagent_info"), "subagents").and_then(Value::as_array) else { return };
                        for spec in specs.iter().take(MAXIMUM_NODES) {
                            let Some(conversation) = conversation_id(text(Some(spec), "conversation_id")) else { continue };
                            if Some(conversation) == self.antigravity_conversation.as_deref() {
                                continue;
                            }
                            let Some(id) = self.ensure_antigravity_agent(conversation) else { continue };
                            let title = antigravity_title(text(Some(spec), "role"), text(Some(spec), "type_name"));
                            let prompt = text(Some(spec), "initial_prompt").filter(|p| !p.is_empty()).map(str::to_string);
                            if !self.antigravity_subagents.iter().any(|s| s.conversation == conversation) {
                                self.antigravity_subagents.push(AntigravitySubagent {
                                    conversation: conversation.into(),
                                    parent: self.antigravity_conversation.clone().unwrap_or_default(),
                                    title: title.clone(),
                                    prompt: prompt.clone(),
                                });
                            }
                            // The step ends once the sub-agent is started; only a failed start ends the block.
                            self.update(&id, |node| ExecutionGraphNode {
                                title: title.unwrap_or(node.title.clone()),
                                input: node.input.clone().or(prompt),
                                state: if failed { "error".into() } else { node.state.clone() },
                                ..node
                            });
                        }
                    }
                    _ => {}
                }
            }
            _ => {}
        }
    }

    /// `AntigravityAnswer(text)`: the reply the parser just showed for an `agent_response` step (or the result's full response when
    /// nothing was streamed): the latest one is the request's answer. An ACP agent's (opencode, Hermes) streamed reply is its answer too.
    pub fn antigravity_answer(&mut self, answer: &str) {
        if self.finished || !matches!(self.provider.as_str(), "gemini" | "opencode" | "hermes") || answer.is_empty() {
            return;
        }
        let main = self.main_id.clone();
        let answer = answer.to_string();
        self.update(&main, |n| ExecutionGraphNode { output: Some(answer), ..n });
    }

    fn ensure_antigravity_agent(&mut self, conversation: &str) -> Option<String> {
        let lower = conversation.to_ascii_lowercase();
        if let Some(known) = self.antigravity_agents.get(&lower) {
            return Some(known.clone());
        }
        if self.nodes.len() >= MAXIMUM_NODES {
            return None;
        }
        let id = node::identifier(&self.run_id, &format!("agy-agent:{conversation}"));
        let value = ExecutionGraphNode::new(
            id.clone(),
            self.run_id.clone(),
            Some(self.main_id.clone()),
            "agent",
            "running",
            self.locale.get("graph.block.agent"),
        );
        self.insert(value);
        self.antigravity_agents.insert(lower, id.clone());
        self.alias(conversation, &id);
        Some(id)
    }

    /// `AntigravitySession(agent)`: a sub-agent's transcript snapshot. A new instruction on a settled sub-agent is new work; one that
    /// answered while helpers it started were still out goes on with the same assignment when one reports.
    pub fn antigravity_session(&mut self, agent: &CodexSessionAgent) {
        if self.finished || self.provider != "gemini" || Some(agent.thread.as_str()) == self.antigravity_conversation.as_deref() {
            return;
        }
        let Some(id) = self.ensure_antigravity_agent(&agent.thread) else { return };
        let owner = if Some(agent.parent_thread.as_str()) == self.antigravity_conversation.as_deref() {
            Owner::node(&self.main_id)
        } else {
            Owner::agent(&agent.parent_thread)
        };
        self.set_parent(&id, &owner);
        let lower = agent.thread.to_ascii_lowercase();
        let seen = self.antigravity_turns.get(&lower).copied().unwrap_or(0);
        self.antigravity_turns.insert(lower, seen.max(agent.turns));
        let old_state = self.nodes[&id].state.clone();
        let reopening = seen > 0 && agent.turns > seen && node::terminal(&old_state);
        let model = self.antigravity_model.clone().or(self.configured_model.clone());
        let marked = self.antigravity_model.is_none() && self.configured_model.is_some();
        for record in &agent.usage {
            let ids: Vec<String> = node::activity_ids(record).map(str::to_string).collect();
            self.record_usage(node::record_usage(record), record.response_id.as_deref().unwrap_or(""), &id, model.as_deref(), &ids, marked);
        }
        // The previous assignment's answer stays in the block above the new one.
        let current = self.nodes[&id].clone();
        if reopening
            && let Some(previous) = current.output.as_ref().filter(|o| !o.is_empty())
            && !current.entries.iter().any(|e| e.kind() == "assistant" && e.text.as_ref() == Some(previous))
        {
            let entry = self.entry(
                node::identifier(&self.run_id, &format!("{id}:answer:{}", current.activity_generation.unwrap_or(0))),
                "assistant",
                previous.clone(),
                None,
            );
            self.append(entry, &id);
        }
        let continuing = !reopening && agent.state == "running" && self.nodes[&id].state == "completed";
        self.update_with(&id, reopening, continuing, |mut value| {
            for entry in &agent.entries {
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
