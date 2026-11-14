//! The Antigravity half of C# `OutputParser` (OutputParser.cs:374-511, 599-614; M/MightyCore/CLIStreamParser.swift antigravity): agy's
//! `--output-format stream-json`. Each line names its `event` and carries the payload under that same key. `init` opens the session,
//! `step_update` streams the turn's steps (the request, the agent's text as deltas, tool calls, sub-agents started, checkpoints) and
//! `result` closes it. What a sub-agent does is not in this stream; the graph reads it from the sub-agent's own transcript.

use serde_json::Value;

use super::{Inner, OutputParser};
use crate::activity::{self, MAXIMUM_MESSAGE_BYTES};
use crate::antigravity::permissions::{self, PermissionRequest};
use crate::antigravity::records;
use crate::lock;
use crate::meta;
use crate::strings;

const STOPPED_STATES: [&str; 3] = ["CANCELED", "CANCELLED", "INTERRUPTED"];

/// `TrailingTrimmed`: agy ends a reply with a newline; a reply's start (an indented code block, say) is kept as written.
pub fn trailing_trimmed(text: &str) -> &str {
    text.trim_end()
}

/// The first `count` UTF-16 units of `text` (C#'s `text[..count]`), never half a pair.
fn first_units(text: &str, count: usize) -> &str {
    let mut units = 0;
    for (at, c) in text.char_indices() {
        if units + c.len_utf16() > count {
            return &text[..at];
        }
        units += c.len_utf16();
    }
    text
}

impl OutputParser {
    fn note_new_conversation(&self, inner: &mut Inner) {
        if inner.new_conversation_noted {
            return;
        }
        inner.new_conversation_noted = true;
        self.log("system", &strings::get("run.notice.antigravityNewConversation"));
    }

    /// `AntigravityStderr`: one complete stderr line of agy (M/MightyCore/CLIStreamParser.swift antigravityStderr) — the conversation it
    /// was asked to continue missing (`warning: conversation "…" not found`) is said once. A lost sign-in there is the runner's to read.
    pub fn antigravity_stderr(&self, line: &str) {
        if self.provider != "gemini" {
            return;
        }
        let lowered = first_units(line, 1024).to_lowercase();
        if lowered.starts_with("warning: conversation") && lowered.contains("not found") {
            let mut inner = lock(&self.inner);
            self.note_new_conversation(&mut inner);
        }
    }

    /// `Antigravity(root, kind)`: one line of agy's stream (`raw` is the line as it came, for an event this app does not read yet).
    pub(super) fn antigravity(&self, inner: &mut Inner, root: &Value, kind: &str, raw: &str) {
        let payload = meta::prop(root, kind);
        let name = [("name", strings::provider_label(&self.provider))];
        match kind {
            "init" => {
                let conversation = meta::text(root, "conversation_id");
                if let Some(requested) = &self.hooks.requested_resume
                    && let Some(started) = conversation.filter(|c| !c.is_empty())
                    && started != requested
                {
                    self.note_new_conversation(inner);
                }
                self.resume(inner, conversation);
                self.turn(inner, &strings::get_with("run.turn.generating", &name));
            }
            "step_update" => {
                let state = payload.and_then(|p| meta::text(p, "state")).unwrap_or("").to_uppercase();
                match payload.and_then(|p| meta::text(p, "step_type")) {
                    Some("agent_response") => {
                        // A long answer arrives as ACTIVE deltas before its DONE step.
                        if let Some(text) = payload.and_then(|p| meta::text(p, "text_delta")).filter(|t| !t.is_empty()) {
                            let remaining = MAXIMUM_MESSAGE_BYTES.saturating_sub(inner.pending.len());
                            if text.len() > remaining {
                                inner.pending_truncated = true;
                            }
                            inner.pending.push_str(&activity::prefix_utf8(text, remaining));
                            inner.antigravity_streamed = true;
                        }
                        if state != "ACTIVE" {
                            self.flush_locked(inner);
                        }
                    }
                    Some("tool") => {
                        self.flush_locked(inner);
                        let Some(payload) = payload else { return };
                        let Some(index) = meta::integer(payload, "step_index") else { return };
                        let info = meta::prop(payload, "tool_info");
                        let tool = info
                            .and_then(|i| meta::text(i, "name"))
                            .or_else(|| meta::text(payload, "tool_name"))
                            .unwrap_or("Tool")
                            .to_string();
                        let failure = match info.and_then(|i| meta::prop(i, "error")) {
                            None | Some(Value::Null) => None,
                            error => Some(Self::error_text(error, &strings::get("run.tool.antigravityFailed"))),
                        };
                        let tool_state = if STOPPED_STATES.contains(&state.as_str()) {
                            "stopped"
                        } else if state == "ERROR" || state == "DONE" && failure.is_some() {
                            "error"
                        } else if state == "DONE" {
                            "completed"
                        } else {
                            "running"
                        };
                        let key = format!("step:{index}");
                        let started = inner.activities.contains_key(&activity::id(&self.namespace, &key));
                        let ended = tool_state != "running";
                        inner.denials.observe(Some(payload));
                        let parameters = records::antigravity_parameters(info.and_then(|i| meta::prop(i, "parameters")));
                        let output = info.and_then(|i| meta::text(i, "output")).map(str::to_string);
                        let shown = if ended { failure.clone().or_else(|| output.clone()) } else { None };
                        self.tool(inner, Some(&key), tool_state, Some(&tool), parameters.as_ref(), shown, None);
                        if self.hooks.activity.is_none() {
                            if !started && !ended {
                                self.log("system", &strings::get_with("run.tool.started", &[("name", first_units(&tool, 160))]));
                            }
                            if ended && let Some(output) = output.filter(|o| !o.is_empty()) {
                                self.log("output", &output);
                            }
                            if let Some(failure) = &failure {
                                self.log("system", failure);
                            }
                        }
                    }
                    Some("subagent") => {
                        // Started sub-agents: one row here, one block each in the graph.
                        self.flush_locked(inner);
                        let Some(payload) = payload else { return };
                        let Some(index) = meta::integer(payload, "step_index") else { return };
                        let specs = meta::prop(payload, "subagent_info").and_then(|i| meta::prop(i, "subagents"));
                        let names: Vec<String> = match specs {
                            Some(Value::Array(specs)) => specs
                                .iter()
                                .take(8)
                                .filter_map(|spec| records::title(meta::text(spec, "role"), meta::text(spec, "type_name")))
                                .collect(),
                            _ => vec![],
                        };
                        let tool = meta::text(payload, "tool_name").unwrap_or("invoke_subagent");
                        let tool_state = if STOPPED_STATES.contains(&state.as_str()) {
                            "stopped"
                        } else if state == "ERROR" {
                            "error"
                        } else if state == "DONE" {
                            "completed"
                        } else {
                            "running"
                        };
                        let summary = if names.is_empty() { tool.to_string() } else { names.join(", ") };
                        self.tool(inner, Some(&format!("step:{index}")), tool_state, Some(tool), None, None, Some(&summary));
                    }
                    _ => {}
                }
            }
            "result" => {
                self.flush_locked(inner);
                self.resume(inner, payload.and_then(|p| meta::text(p, "conversation_id")));
                // The full text of the turn repeats what the deltas already showed.
                if !inner.antigravity_streamed
                    && let Some(response) = payload.and_then(|p| meta::text(p, "response"))
                {
                    let full = trailing_trimmed(response);
                    if !full.is_empty() {
                        self.log("assistant", &activity::prefix_utf8(full, MAXIMUM_MESSAGE_BYTES));
                        if let Some(answer) = &self.hooks.answer {
                            answer(full);
                        }
                    }
                }
                inner.antigravity_streamed = false;
                // What headless mode could not ask about was declined and the turn went on; the pane asks now, with a card.
                let refused = inner.denials.finish(payload);
                let card_hook = self.hooks.antigravity_permission.as_ref().filter(|_| self.provider == "gemini");
                if let Some(hook) = card_hook.filter(|_| !refused.is_empty()) {
                    let card = PermissionRequest::make(&self.namespace, refused);
                    self.log("system", &permissions::transcript_line(&card));
                    hook(card);
                } else {
                    let denied: Vec<&str> = match payload.and_then(|p| meta::prop(p, "denied_actions")) {
                        Some(Value::Array(rows)) => rows
                            .iter()
                            .take(16)
                            .filter_map(|row| meta::text(row, "display_name").or_else(|| meta::text(row, "action")))
                            .collect(),
                        _ => vec![],
                    };
                    if !denied.is_empty() {
                        let actions = activity::clean(Some(&denied.join(", ")), 512, true);
                        self.log("system", &strings::get_with("run.notice.antigravityDenied", &[("actions", &actions)]));
                    }
                }
                let status = payload.and_then(|p| meta::text(p, "status")).unwrap_or("").to_uppercase();
                if matches!(status.as_str(), "ERROR" | "INVALID") || STOPPED_STATES.contains(&status.as_str()) {
                    // A stop the user asked for ends the run as stopped in the runner; agy ending the turn by itself is a failure.
                    inner.failed = true;
                    let fallback = if matches!(status.as_str(), "ERROR" | "INVALID") {
                        strings::get_with("run.error.providerRunFailed", &name)
                    } else {
                        strings::get_with("run.error.antigravityEnded", &[("status", &status)])
                    };
                    self.log("error", &Self::error_text(payload.and_then(|p| meta::prop(p, "error")), &fallback));
                } else if status != "SUCCESS" {
                    // WAITING, RUNNING or a status this app does not know yet.
                    let shown = activity::clean(Some(if status.is_empty() { "?" } else { &status }), 64, true);
                    self.log("system", &strings::get_with("run.notice.antigravityStatus", &[("status", &shown)]));
                }
                self.turn(inner, &strings::get_with("run.turn.finishing", &name));
            }
            // A read-only slash command's own payload; its result carries the same text.
            "command_result" => {}
            // An event this app does not read yet is shown, not dropped.
            _ => self.log("output", first_units(raw.trim(), 32_768)),
        }
    }
}
