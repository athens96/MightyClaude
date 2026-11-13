//! Claude's stream-json (ExecutionGraphTracker.cs:255-737, 895-1023): the request's main block, its sub-agents (Agent calls and the
//! frames under them), background commands, questions, steers, compactions and what their results and notifications say.

use std::sync::LazyLock;

use chrono::DateTime;
use regex::Regex;
use serde_json::Value;

use super::{
    ExecutionGraphTracker, Owner, SentMessage, agent_tool, content_text, is_notification_result, is_true, key, objects, prop,
    question_summary, text,
};
use crate::handback;
use crate::node::{self, ExecutionGraphNode, MAXIMUM_NODES};
use crate::notice::Opener;
use crate::support;

static TASK_LAUNCH: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"background with ID: (?<task>[A-Za-z0-9_-]+)").expect("regex"));
static AGENT_LAUNCH: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"agentId: (?<agent>[A-Za-z0-9_-]+)").expect("regex"));
/// Where a background run writes its output: the rest of the line, as a path may hold spaces.
static OUTPUT_LAUNCH: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"Output is being written to: (?<path>[^\r\n]+)").expect("regex"));
const ASYNC_LAUNCH_TEXT: &str = "Async agent launched successfully.";

impl ExecutionGraphTracker {
    /// `Steer(id, text)`: a message the user sent while the turn ran. Claude reads it between tool calls; the block under main shows it
    /// until the next root-level answer, which is taken as the reply. After the request's result it starts a new turn in the same process.
    pub fn steer(&mut self, id: &str, text: &str) {
        let before = self.pending_steers.len();
        self.steer_block(id, text);
        if !self.turn_ended || self.steered_turn.is_some() || self.pending_steers.len() == before {
            return;
        }
        // A delegation notice's answer is its own 위임 알림 run's; the block stays under the request and takes the reply.
        if support::is_delegation_notice(Some(text)) {
            self.join_turn(Opener::Delegation, None, Some(text), None);
            // No room for its run: its answer follows in the request's result under 위임 알림, as before notice runs.
            if self.notice_turn.is_none() && self.merge_turn.is_none() {
                self.steered_turn = Some(self.locale.get("graph.block.delegationNotice"));
            }
        } else {
            self.steered_turn = Some(self.locale.get("graph.block.steer"));
        }
    }

    fn steer_block(&mut self, id: &str, text: &str) {
        if self.finished || self.provider != "claude" || self.nodes.len() >= MAXIMUM_NODES {
            return;
        }
        let node_id = node::identifier(&self.run_id, &format!("steer:{id}"));
        if self.nodes.contains_key(&node_id) {
            return;
        }
        let mut value = ExecutionGraphNode::new(
            node_id.clone(),
            self.run_id.clone(),
            Some(self.main_id.clone()),
            "steer",
            "running",
            self.locale.get("graph.block.steer"),
        );
        value.input = Some(text.into());
        let Some(normalized) = node::normalized(&value, false) else { return };
        self.insert(normalized);
        self.pending_steers.push(node_id);
    }

    pub(super) fn consume_claude(&mut self, value: &Value) {
        let frame = Some(value);
        // A running tool's heartbeat (sent 30 s into a foreground Bash call) names that call as its parent: it is no sub-agent's message.
        if text(frame, "type") == Some("tool_progress") {
            return;
        }
        let parent_tool = key(prop(frame, "parent_tool_use_id")).map(str::to_string);
        // Only an Agent (or Task) call has frames of its own: one under a call the stream showed as another tool is no sub-agent.
        if let Some(parent) = &parent_tool
            && self.tool_names.get(parent).is_some_and(|name| !agent_tool(Some(name)))
        {
            return;
        }
        if let Some(parent) = &parent_tool {
            self.ensure_agent(parent);
        }
        // Over-cap child events are omitted, never reclassified as main text.
        let current = match &parent_tool {
            None => self.main_id.clone(),
            Some(parent) => node::agent_node_id(&self.run_id, parent),
        };
        let root = parent_tool.is_none();
        let owner = Owner::node(&current);
        let kind = text(frame, "type");
        let message = prop(frame, "message").filter(|m| m.is_object());
        if kind == Some("assistant")
            && let Some(message) = message
        {
            let activity_ids: Vec<String> = objects(prop(Some(message), "content"))
                .map(|blocks| {
                    blocks
                        .iter()
                        .filter(|b| text(Some(b), "type") == Some("tool_use"))
                        .filter_map(|b| key(prop(Some(b), "id")).map(str::to_string))
                        .collect()
                })
                .unwrap_or_default();
            if let Some(usage) = node::parse_usage(prop(Some(message), "usage")) {
                self.unnamed_usage += 1;
                let name = key(prop(Some(message), "id"))
                    .or_else(|| key(prop(frame, "uuid")))
                    .map(str::to_string)
                    .unwrap_or_else(|| format!("unnamed:{}", self.unnamed_usage));
                self.record_usage(usage, &name, &current, text(Some(message), "model"), &activity_ids, false);
            }
        }
        if kind == Some("system") && text(frame, "subtype") == Some("compact_boundary") {
            self.compactions += 1;
            let key = key(prop(frame, "uuid")).map(str::to_string).unwrap_or_else(|| format!("n{}", self.compactions));
            let detail = crate::labels::claude_compaction_summary(prop(frame, "compact_metadata"), &self.locale);
            self.compaction(&key, &current, detail);
            return;
        }
        if kind == Some("result") && root {
            self.advisor_turn_ended(value);
        }
        if kind == Some("assistant")
            && let Some(message) = message
            && let Some(blocks) = objects(prop(Some(message), "content"))
        {
            self.advisor_noted(value, message, root);
            // The advisor is a server tool: its call and its result are blocks of the caller's own message and make no tool block. In the
            // order they stand, so a tool call after a consultation says the work went on.
            for block in blocks {
                match text(Some(block), "type") {
                    Some("server_tool_use") => {
                        if text(Some(block), "name") == Some("advisor")
                            && let Some(call) = key(prop(Some(block), "id"))
                        {
                            self.advisor_asked(call, &current, root);
                        }
                    }
                    Some("advisor_tool_result") => {
                        if let Some(answered) = key(prop(Some(block), "tool_use_id")) {
                            self.advisor_answered(answered, prop(Some(block), "content"));
                        }
                    }
                    Some("tool_use") => self.advisor_worked(&current, root, text(Some(block), "name")),
                    _ => {}
                }
            }
            let content = prop(Some(message), "content");
            // A preamble beside a tool_use is not the reply; wait for a text-only answer.
            if root
                && !self.pending_steers.is_empty()
                && !blocks.iter().any(|b| text(Some(b), "type") == Some("tool_use"))
                && let Some(reply) = content_text(content).filter(|r| !r.is_empty())
            {
                let answered = std::mem::take(&mut self.pending_steers);
                for id in answered {
                    let reply = reply.clone();
                    self.update(&id, |n| ExecutionGraphNode { state: "completed".into(), output: Some(reply), ..n });
                }
            }
            if !root
                && current != self.main_id
                && let Some(text_value) = content_text(content).filter(|t| !t.is_empty())
            {
                let message_key = key(prop(frame, "uuid"))
                    .or_else(|| key(prop(Some(message), "id")))
                    .map(str::to_string)
                    .unwrap_or_else(|| node::identifier(&self.run_id, &text_value));
                let entry =
                    self.entry(node::identifier(&self.run_id, &format!("{current}:message:{message_key}")), "assistant", text_value, None);
                self.append(entry, &current);
            }
            for block in blocks.iter().filter(|b| text(Some(b), "type") == Some("tool_use")) {
                let Some(tool_id) = key(prop(Some(block), "id")).map(str::to_string) else { continue };
                self.remember_tool(&tool_id, owner.clone());
                let tool_name = text(Some(block), "name");
                if let Some(name) = tool_name
                    && (self.tool_names.contains_key(&tool_id) || self.tool_names.len() < 4096)
                {
                    self.tool_names.insert(tool_id.clone(), name.into());
                }
                if !root && current != self.main_id {
                    self.count_tool(&tool_id, &current);
                }
                let input = prop(Some(block), "input").filter(|i| i.is_object());
                let background = is_true(input, "run_in_background");
                if agent_tool(tool_name) {
                    let Some(child) = self.ensure_agent(&tool_id) else { continue };
                    self.set_parent(&child, &owner);
                    if background {
                        self.background_tools.insert(tool_id.clone());
                    }
                    if let Some(agent_name) = text(input, "name").filter(|n| !n.is_empty() && n.len() <= 512)
                        && (self.agent_names.contains_key(agent_name) || self.agent_names.len() < 512)
                    {
                        self.agent_names.insert(agent_name.into(), child.clone());
                    }
                    self.update(&child, |mut node| {
                        if background {
                            node.background = Some(true);
                        }
                        if let Some(prompt) = text(input, "prompt") {
                            node.input = Some(prompt.into());
                        }
                        // The first key present wins, as in the macOS `??` chain.
                        let named = ["name", "description", "subagent_type"].iter().find_map(|k| prop(input, k));
                        if let Some(title) = named.and_then(Value::as_str).filter(|t| !t.is_empty()) {
                            node.title = title.into();
                        }
                        // Who it is, as the call says it: its type, the model it asks for and its isolation.
                        Self::identify(node, text(input, "subagent_type"), text(input, "model"), None, text(input, "isolation"), true)
                    });
                } else if tool_name == Some("AskUserQuestion")
                    && let Some(question) = self.ensure_node(&tool_id, "question", self.locale.get("graph.block.question"))
                {
                    // Each question to the user is its own block; the answer settles it.
                    self.set_parent(&question, &owner);
                    let (title, body) = question_summary(input, &self.locale);
                    self.update(&question, |node| ExecutionGraphNode {
                        state: "waiting".into(),
                        title: title.unwrap_or(node.title.clone()),
                        input: Some(body),
                        ..node
                    });
                } else if background && let Some(task) = self.ensure_node(&tool_id, "task", self.locale.get("graph.block.task")) {
                    // A backgrounded command outlives its tool result: its own child block, settled by the engine's task notification.
                    self.set_parent(&task, &owner);
                    self.background_tools.insert(tool_id.clone());
                    self.update(&task, |mut node| {
                        node.background = Some(true);
                        let command = text(input, "command");
                        if let Some(command) = command {
                            node.input = Some(command.into());
                        }
                        if let Some(title) = text(input, "description").filter(|t| !t.is_empty()) {
                            node.title = title.into();
                        } else if let Some(command) = command.filter(|c| !c.is_empty()) {
                            node.title = command.into();
                        }
                        node
                    });
                } else if tool_name == Some("SendMessage") && (self.sent_messages.contains_key(&tool_id) || self.sent_messages.len() < 512)
                {
                    // A message to a background agent that already ended resumes it in the background. No block yet: only the result
                    // says so (resume_agent); a message delivered to a running agent adds no work.
                    self.sent_messages.insert(
                        tool_id.clone(),
                        SentMessage {
                            owner: owner.clone(),
                            to: key(prop(input, "to")).map(Into::into),
                            summary: text(input, "summary").map(Into::into),
                            message: text(input, "message").map(Into::into),
                        },
                    );
                }
            }
        } else if kind == Some("user")
            && let Some(message) = message
        {
            let blocks = objects(prop(Some(message), "content")).cloned().unwrap_or_default();
            for block in blocks.iter().filter(|b| text(Some(b), "type") == Some("tool_result")) {
                let Some(tool_id) = key(prop(Some(block), "tool_use_id")).map(str::to_string) else { continue };
                self.remember_tool(&tool_id, owner.clone());
                if let Some(sent) = self.sent_messages.remove(&tool_id) {
                    self.resume_agent(&tool_id, &sent, block, value);
                    continue;
                }
                let child = node::agent_node_id(&self.run_id, &tool_id);
                let Some(node_kind) = self.kind_of(&child).map(str::to_string) else { continue };
                let output = content_text(prop(Some(block), "content"));
                let error = is_true(Some(block), "is_error");
                if node_kind == "agent" {
                    self.identify_launch(&child, value);
                }
                if node_kind == "task" {
                    self.acknowledge_task(&child, block);
                    continue;
                }
                if node_kind == "question" {
                    self.update(&child, |n| ExecutionGraphNode { state: if error { "stopped" } else { "completed" }.into(), output, ..n });
                    continue;
                }
                // Background Agent tool results acknowledge launch; they are not the agent's answer. Claude Code runs a sub-agent in the
                // background by default: a call without run_in_background says so in its result.
                if node_kind == "agent" && !self.background_tools.contains(&tool_id) && !error && async_launch(block, value) {
                    self.background_tools.insert(tool_id.clone());
                    self.update(&child, |n| ExecutionGraphNode { background: Some(true), ..n });
                }
                if self.background_tools.contains(&tool_id) {
                    self.remember_agent_id(&child, block, value);
                    continue;
                }
                self.update(&child, |n| {
                    if node::terminal(&n.state) {
                        n
                    } else {
                        ExecutionGraphNode {
                            state: if error { "error" } else { "completed" }.into(),
                            output: output.as_deref().map(report),
                            ..n
                        }
                    }
                });
            }
            // Task completion arrives as an injected user message, not a tool event; so does a sub-agent's final report. Either settles
            // its block and copies it into a notice run; only a root-level one opens a notice turn.
            if let Some(content) = content_text(prop(Some(message), "content")) {
                if content.contains("<task-notification>") {
                    self.settle_tasks(&content, root);
                }
                if content.contains(handback::OPENING) {
                    self.settle_handbacks(&content, prop(frame, "origin"), root);
                }
            }
        } else if kind == Some("system")
            && text(frame, "subtype") == Some("task_notification")
            && let Some(notified) = key(prop(frame, "task_id"))
        {
            // The live stream carries a task's end as this event, not as the injected user message: the same notification.
            self.report_tools(value);
            let summary = text(frame, "summary");
            let tool = key(prop(frame, "tool_use_id"));
            let task_run = key(prop(frame, "run_id"));
            let file = text(frame, "output_file");
            let mut body = format!("<task-notification><task-id>{notified}</task-id>");
            if let Some(tool) = tool {
                body.push_str(&format!("<tool-use-id>{tool}</tool-use-id>"));
            }
            if let Some(run) = task_run {
                body.push_str(&format!("<run-id>{run}</run-id>"));
            }
            if let Some(file) = file.filter(|f| !f.is_empty()) {
                body.push_str(&format!("<output-file>{file}</output-file>"));
            }
            body.push_str(&format!("<status>{}</status>", text(frame, "status").unwrap_or("completed")));
            if let Some(summary) = summary.filter(|s| !s.is_empty()) {
                body.push_str(&format!("<summary>{summary}</summary>"));
            }
            body.push_str("</task-notification>");
            self.settle_tasks(&body, root);
        } else if kind == Some("system") && text(frame, "subtype") == Some("task_progress") {
            self.report_tools(value);
        } else if kind == Some("system")
            && text(frame, "subtype") == Some("task_updated")
            && let Some(updated) = key(prop(frame, "task_id"))
            && let Some(milliseconds) = prop(prop(frame, "patch"), "end_time").filter(|v| v.is_number()).and_then(Value::as_f64)
        {
            // A background command's end as the CLI timed it (milliseconds since 1970), just before its notification.
            let seconds = milliseconds / 1_000.0;
            if let Some(id) = self.task_aliases.get(updated).cloned()
                && self.node(&id).is_some_and(|n| n.kind == "task" && n.figures.as_ref().and_then(|f| f.ended_at.as_ref()).is_none())
                && seconds.is_finite()
                && (0.0..=4_102_444_800.0).contains(&seconds)
                && let Some(at) = DateTime::from_timestamp_millis((seconds * 1_000.0).round_ties_even() as i64)
            {
                let time = at.format("%Y-%m-%dT%H:%M:%S%.3fZ").to_string();
                self.update(&id, |mut node| {
                    let mut figures = node.figures.take().unwrap_or_default();
                    figures.ended_at = Some(time);
                    node.figures = Some(figures);
                    node
                });
            }
        } else if kind == Some("system")
            && text(frame, "subtype") == Some("task_started")
            && let Some(started) = key(prop(frame, "tool_use_id"))
        {
            // The CLI names the type it started the agent as, which a call without `subagent_type` leaves unsaid.
            let child = node::agent_node_id(&self.run_id, started);
            if self.kind_of(&child) == Some("agent") {
                let kind = text(frame, "subagent_type");
                self.update(&child, |node| Self::identify(node, kind, None, None, None, false));
            }
        } else if kind == Some("result")
            && (is_notification_result(value) || (root && (self.notice_turn.is_some() || self.merge_turn.is_some())))
        {
            if !root {
                return;
            }
            self.notice_result(value);
        } else if kind == Some("result") {
            let failed = is_true(frame, "is_error") || text(frame, "subtype").unwrap_or("").starts_with("error");
            // The request's own turn ended: it reads completed while background tasks it started still run.
            if root && let Some(steered) = self.steered_turn.clone() {
                // A turn a steer opened after the request's result: its answer follows in the result, as a notice turn's does.
                self.clear_notice_turn();
                if let Some(answer) = text(frame, "result").map(str::trim).filter(|a| !a.is_empty()) {
                    let header = self.locale.get_with("graph.result.afterSteer", &[("block", &steered)]);
                    let main = self.main_id.clone();
                    self.update(&main, |n| ExecutionGraphNode {
                        output: Some(support::with_notice_section(n.output.as_deref(), &header, answer)),
                        ..n
                    });
                }
            } else if root {
                // The lines its turn held are the request's own answer's, not a notice run's.
                self.absorbed_run = None;
                self.turn_lines.clear();
                self.turn_ended = true;
                let main = self.main_id.clone();
                let result = text(frame, "result").map(str::to_string);
                self.update(&main, |n| ExecutionGraphNode {
                    output: result,
                    state: if failed { n.state.clone() } else { "completed".into() },
                    ..n
                });
            } else if current != self.main_id {
                let result = text(frame, "result").map(str::to_string);
                self.update(&current, |n| ExecutionGraphNode {
                    output: result,
                    state: if failed { "error" } else { "completed" }.into(),
                    ..n
                });
            }
        }
    }

    /// `AcknowledgeTask`: the launch acknowledgement names the engine's task id, kept so a notification without a tool-use id can still
    /// settle the block, and where the run writes its output.
    fn acknowledge_task(&mut self, id: &str, block: &Value) {
        let content = content_text(prop(Some(block), "content")).unwrap_or_default();
        if is_true(Some(block), "is_error") {
            let output = (!content.is_empty()).then(|| content.clone());
            self.update(id, |n| ExecutionGraphNode { state: "error".into(), output, ..n });
            return;
        }
        if let Some(found) = TASK_LAUNCH.captures(&content)
            && self.task_aliases.len() < 512
        {
            self.task_aliases.insert(found["task"].to_string(), id.into());
        }
        // Where the run writes its output; a notification's <output-file> may move it.
        if let Some(found) = OUTPUT_LAUNCH.captures(&content) {
            let mut path = found["path"].trim();
            if let Some(stripped) = path.strip_suffix('.') {
                path = stripped;
            }
            if !path.is_empty() {
                let path = path.to_string();
                self.update(id, |n| ExecutionGraphNode { output_file: Some(path), ..n });
            }
        }
        if !content.is_empty() {
            let entry = self.entry(node::identifier(&self.run_id, &format!("{id}:launch")), "system", content, None);
            self.append(entry, id);
        }
    }

    /// `RememberAgentID`: a background Agent's launch result names its agent id, which its notification's `task-id` and its hand-back's
    /// `from` carry.
    fn remember_agent_id(&mut self, id: &str, block: &Value, frame: &Value) {
        let from_text = content_text(prop(Some(block), "content")).and_then(|t| AGENT_LAUNCH.captures(&t).map(|c| c["agent"].to_string()));
        let Some(agent) = from_text.or_else(|| key(prop(structured(frame), "agentId")).map(str::to_string)) else { return };
        if self.task_aliases.contains_key(&agent) || self.task_aliases.len() < 512 {
            self.task_aliases.insert(agent, id.into());
        }
    }

    /// `IdentifyLaunch`: an Agent call's result names the model the CLI resolved, the agent's type and an isolated agent's worktree.
    fn identify_launch(&mut self, id: &str, frame: &Value) {
        let Some(result) = structured(frame).filter(|r| r.is_object()) else { return };
        let worktree = text(Some(result), "worktreePath").is_some_and(|w| !w.is_empty());
        let kind = text(Some(result), "agentType");
        let resolved = text(Some(result), "resolvedModel");
        self.update(id, |node| Self::identify(node, kind, None, resolved, worktree.then_some("worktree"), false));
    }

    /// `ResumeAgent`: a SendMessage that resumed a background agent that had ended is new background work of this request: a background
    /// agent block under the sender, settled by that agent's notification or hand-back as an Agent launch is.
    fn resume_agent(&mut self, tool_id: &str, sent: &SentMessage, block: &Value, frame: &Value) {
        if is_true(Some(block), "is_error") {
            return;
        }
        let Some(agent) = resumed_agent(block, frame) else { return };
        let Some(child) = self.ensure_agent(tool_id) else { return };
        self.set_parent(&child, &sent.owner);
        self.background_tools.insert(tool_id.into());
        let earlier = self.task_aliases.get(&agent).filter(|old| **old != child).and_then(|old| self.nodes.get(old)).cloned();
        if self.task_aliases.contains_key(&agent) || self.task_aliases.len() < 512 {
            self.task_aliases.insert(agent.clone(), child.clone());
        }
        if let Some(earlier) = &earlier {
            let names: Vec<String> = self.agent_names.iter().filter(|(_, v)| **v == earlier.id).map(|(k, _)| k.clone()).collect();
            for name in names {
                self.agent_names.insert(name, child.clone());
            }
        }
        if let Some(to) = &sent.to
            && *to != agent
            && (self.agent_names.contains_key(to) || self.agent_names.len() < 512)
        {
            self.agent_names.insert(to.clone(), child.clone());
        }
        if let Some(routed) = self.aliases.get(&agent).cloned()
            && routed != child
            && self.nodes.get(&routed).is_some_and(|r| node::terminal(&r.state))
        {
            self.aliases.insert(agent.clone(), child.clone());
        }
        let summary = sent.summary.clone();
        let to = sent.to.clone();
        let message = sent.message.clone();
        self.update(&child, |node| {
            // Without a summary the agent's earlier block in this run names it better than an id.
            let title = summary
                .filter(|s| !s.is_empty())
                .or_else(|| earlier.as_ref().map(|e| e.title.clone()).filter(|t| !t.is_empty()))
                .or(to)
                .unwrap_or(node.title.clone());
            ExecutionGraphNode {
                background: Some(true),
                title,
                input: message.or(node.input.clone()),
                // The same agent goes on: it is who its earlier block in this run was.
                identity: node.identity.clone().or_else(|| earlier.as_ref().and_then(|e| e.identity.clone())),
                ..node
            }
        });
    }
}

/// `Structured`: the live stream's `tool_use_result`, a transcript's `toolUseResult`.
fn structured(frame: &Value) -> Option<&Value> {
    prop(Some(frame), "tool_use_result").filter(|v| v.is_object()).or_else(|| prop(Some(frame), "toolUseResult"))
}

/// `AsyncLaunch`: an Agent call's result acknowledging an asynchronous launch.
fn async_launch(block: &Value, frame: &Value) -> bool {
    if let Some(result) = structured(frame).filter(|r| r.is_object())
        && (text(Some(result), "status") == Some("async_launched") || is_true(Some(result), "isAsync"))
    {
        return true;
    }
    content_text(prop(Some(block), "content")).is_some_and(|t| t.trim_start().starts_with(ASYNC_LAUNCH_TEXT))
}

/// `ResumedAgent`: the agent a SendMessage result says it resumed: the result's JSON text, else the event's `toolUseResult`.
fn resumed_agent(block: &Value, frame: &Value) -> Option<String> {
    let mut result = content_text(prop(Some(block), "content"))
        .filter(|t| t.trim_start().starts_with('{'))
        .and_then(|t| serde_json::from_str::<Value>(&t).ok())
        .filter(Value::is_object);
    if result.is_none() {
        result = prop(Some(frame), "toolUseResult").cloned();
    }
    let result = result.filter(Value::is_object)?;
    is_true(Some(&result), "success").then(|| key(prop(Some(&result), "resumedAgentId")).map(str::to_string)).flatten()
}

/// `Report(raw)` (ExecutionGraphTracker.cs:956): a foreground sub-agent's result as Claude Code hands it to its caller. The report alone
/// when it stands under the hand-back frame; the result as it came otherwise.
pub fn report(raw: &str) -> String {
    let text = raw.replace("\r\n", "\n");
    let Some(frame) = text.find(handback::FRAME) else { return raw.into() };
    if frame > 0
        && !text[..frame]
            .chars()
            .next_back()
            .is_some_and(|c| matches!(c, '\n' | '\r' | '\u{2028}' | '\u{2029}' | '\u{85}' | '\u{b}' | '\u{c}'))
    {
        return raw.into();
    }
    let from = frame + handback::FRAME.len();
    let Some(marker) = text[from..].find(handback::MARKER).map(|at| at + from) else { return raw.into() };
    let after = marker + handback::MARKER.len();
    let Some(line_end) = text[after..].find('\n').map(|at| at + after) else { return raw.into() };
    let mut lines = vec![];
    for line in text[line_end + 1..].split('\n') {
        if let Some(rest) = line.strip_prefix("  ") {
            lines.push(rest);
        } else if line.chars().all(char::is_whitespace) {
            lines.push("");
        } else {
            break;
        }
    }
    let report = lines.join("\n");
    let report = report.trim();
    if report.is_empty() { raw.into() } else { report.into() }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_report_is_cut_out_of_its_frame() {
        let raw = "notes\n[Subagent hand-back] x The report follows:\n  one\n\n  two\nagentId: a1";
        assert_eq!(report(raw), "one\n\ntwo");
        assert_eq!(report("plain"), "plain");
        assert_eq!(report("x[Subagent hand-back] The report follows:\n  a"), "x[Subagent hand-back] The report follows:\n  a");
    }
}
