//! The pi half of C# `OutputParser` (OutputParser.cs:513-597 `Pi`; M/MightyCore/CLIStreamParser.swift pi): `pi --mode rpc` (pi's
//! docs/rpc.md) writes one JSON object per line, its commands' `response`s and the agent's events. A reply is logged whole, when its
//! text block or message ends; thinking text is not copied. Tools are rows in the activity list as for the others. The turn is open
//! from `agent_start` to `agent_end`, and while a compaction or an automatic retry (which pi announces after `agent_end`) goes on.
//!
//! An extension's dialog goes to [`PiDialogChannel`](crate::pi_dialogs::PiDialogChannel) after the parser's lock is released (C#
//! hands it over under the lock, which its re-entrant monitor allows; the card's row is the parser's own).

use indexmap::IndexMap;
use serde_json::{Value, json};

use super::{Inner, OutputParser};
use crate::activity;
use crate::compaction;
use crate::failure;
use crate::lock;
use crate::meta;
use crate::pi_dialogs::Asking;
use crate::providers::PI_QUESTION_TOOL;
use crate::strings;
use crate::text_json;

/// pi's turn and failure as its stream said them.
#[derive(Default)]
pub(super) struct PiState {
    agent_open: bool,
    compacting: bool,
    retrying: bool,
    ended: bool,
    /// The latest assistant message pi ended with `stopReason: error`, until a retry or a later message answers it; still set at
    /// `Flush`, it fails the run.
    error: Option<String>,
    /// `AuthenticationFailure`: pi's own sign-in failure as its stream said it last.
    pub(super) authentication_failure: bool,
    /// The app's `ask_user` calls that are running: a dialog titled with one's question belongs to it.
    asking: IndexMap<String, (String, bool)>,
}

impl PiState {
    /// `PiTurnOpen`: stdin must stay open.
    pub(super) fn turn_open(&self) -> bool {
        self.agent_open || self.compacting || self.retrying
    }

    /// `PiTurnEnded`: `agent_end` has come.
    pub(super) fn turn_ended(&self) -> bool {
        self.ended
    }

    /// The error no retry answered, once.
    pub(super) fn take_error(&mut self) -> Option<String> {
        self.error.take()
    }
}

/// What a frame leaves for after the parser's lock.
pub(super) enum Later {
    /// An extension's dialog for the card (or, when the card cannot ask it, the answer no pane can give).
    Dialog { root: Value, asking: Option<Asking> },
    /// The `ask_user` call ended: its card has nothing left to answer.
    ToolEnded(String),
}

/// `JsonElement.ToString()`: a string's value, `True`/`False`, nothing for null, any other value's JSON.
fn element_text(value: &Value) -> String {
    match value {
        Value::String(text) => text.clone(),
        Value::Bool(true) => "True".into(),
        Value::Bool(false) => "False".into(),
        Value::Null => String::new(),
        other => other.to_string(),
    }
}

/// `Id(message)`: `pi:` and the message's response id, else its timestamp. None for a message that is no object (C#'s
/// `TryGetProperty` throws there, and the line is skipped as unsupported).
fn message_id(message: Option<&Value>) -> Option<String> {
    let message = message.filter(|m| m.is_object())?;
    let key = match (meta::prop(message, "responseId"), meta::prop(message, "timestamp")) {
        (Some(response), _) if !response.is_null() => element_text(response),
        (_, Some(stamp)) => element_text(stamp),
        _ => "message".into(),
    };
    Some(format!("pi:{key}"))
}

impl OutputParser {
    /// `Pi(root, type)`, under the parser's lock; the dialogs it leaves are for [`OutputParser::pi_later`].
    pub(super) fn pi(&self, inner: &mut Inner, root: &Value, kind: Option<&str>) -> Vec<Later> {
        let mut later = Vec::new();
        let name = [("name", strings::provider_label(&self.provider))];
        let failed_words = || strings::get_with("run.error.providerRunFailed", &name);
        match kind {
            Some("response") => {
                // The get_state asked first names the session this run continues.
                if matches!(meta::prop(root, "success"), Some(Value::Bool(false))) {
                    let rejected = Self::error_text(meta::prop(root, "error"), &failed_words());
                    inner.failed = true;
                    inner.pi.authentication_failure = failure::pi(&rejected);
                    self.log("error", &rejected);
                } else if meta::text(root, "command") == Some("get_state")
                    && let Some(data) = meta::prop(root, "data").filter(|d| d.is_object())
                {
                    self.resume(inner, meta::text(data, "sessionId"));
                }
            }
            Some("agent_start") => {
                // A retry pi announced has started: the turn itself is open now.
                inner.pi.agent_open = true;
                inner.pi.retrying = false;
                inner.pi.ended = false;
                self.turn(inner, &strings::get_with("run.turn.generating", &name));
            }
            Some("agent_end") => {
                inner.pi.agent_open = false;
                inner.pi.ended = true;
                self.turn(inner, &strings::get_with("run.turn.finishing", &name));
            }
            Some("message_update") => {
                if let Some(update) = meta::prop(root, "assistantMessageEvent").filter(|u| u.is_object())
                    && meta::text(update, "type") == Some("text_end")
                    && let Some(text) = meta::text(update, "content")
                {
                    let Some(id) = message_id(meta::prop(root, "message")) else {
                        self.log("system", &strings::get("run.notice.unsupportedRecord"));
                        return later;
                    };
                    let key = format!("{id}:{}", meta::integer(update, "contentIndex").unwrap_or(0));
                    self.assistant(inner, text, Some(&key));
                }
            }
            Some("message_end") => {
                let Some(message) = meta::prop(root, "message").filter(|m| m.is_object()) else { return later };
                if meta::text(message, "role") != Some("assistant") {
                    return later;
                }
                let key = message_id(Some(message)).unwrap_or_default();
                if let Some(Value::Array(blocks)) = meta::prop(message, "content") {
                    for (index, block) in blocks.iter().enumerate() {
                        if meta::text(block, "type") == Some("text")
                            && let Some(text) = meta::text(block, "text")
                        {
                            self.assistant(inner, text, Some(&format!("{key}:{index}")));
                        }
                    }
                }
                inner.pi.error = (meta::text(message, "stopReason") == Some("error"))
                    .then(|| Self::error_text(meta::prop(message, "errorMessage"), &failed_words()));
                inner.pi.authentication_failure = inner.pi.error.as_deref().is_some_and(failure::pi);
            }
            Some(event @ ("tool_execution_start" | "tool_execution_update" | "tool_execution_end")) => {
                let ended = event == "tool_execution_end";
                if meta::text(root, "toolName") == Some(PI_QUESTION_TOOL)
                    && let Some(asker) = meta::text(root, "toolCallId")
                {
                    if ended {
                        inner.pi.asking.shift_remove(asker);
                        later.push(Later::ToolEnded(asker.to_string()));
                    } else if let Some(asked) = meta::prop(root, "args").filter(|a| a.is_object())
                        && let Some(question) = meta::text(asked, "question")
                    {
                        inner.pi.asking.insert(asker.to_string(), (question.trim().to_string(), meta::flag(asked, "multiple")));
                    }
                }
                let output = activity::output(meta::prop(root, if ended { "result" } else { "partialResult" }));
                let state = match (ended, meta::flag(root, "isError")) {
                    (false, _) => "running",
                    (true, true) => "error",
                    (true, false) => "completed",
                };
                let tool = meta::text(root, "toolName").unwrap_or("Tool");
                self.tool(inner, meta::text(root, "toolCallId"), state, Some(tool), meta::prop(root, "args"), output.clone(), None);
                if self.hooks.activity.is_none()
                    && ended
                    && let Some(output) = output.filter(|o| !o.is_empty())
                {
                    self.log("output", &output);
                }
            }
            Some("compaction_start") => inner.pi.compacting = true,
            Some("compaction_end") => {
                inner.pi.compacting = false;
                if let Some(failure) = meta::text(root, "errorMessage") {
                    self.log("system", &format!("{} · {failure}", compaction::title()));
                } else if let Some(result) = meta::prop(root, "result").filter(|r| r.is_object()) {
                    let trigger = if meta::text(root, "reason") == Some("manual") { "manual" } else { "auto" };
                    let metadata = json!({"trigger": trigger, "pre_tokens": meta::integer(result, "tokensBefore")});
                    self.log("system", &format!("{} · {}", compaction::title(), compaction::claude_summary(Some(&metadata))));
                }
            }
            Some("auto_retry_start") => {
                inner.pi.retrying = true;
                inner.pi.error = None;
                inner.pi.authentication_failure = false;
                let attempt = meta::integer(root, "attempt").unwrap_or(0).to_string();
                let maximum = meta::integer(root, "maxAttempts").unwrap_or(0).to_string();
                let error = activity::clean(Some(meta::text(root, "errorMessage").unwrap_or("")), 512, true);
                self.log(
                    "system",
                    &strings::get_with("run.notice.piRetry", &[("attempt", &attempt), ("max", &maximum), ("error", &error)]),
                );
            }
            Some("auto_retry_end") => {
                inner.pi.retrying = false;
                if matches!(meta::prop(root, "success"), Some(Value::Bool(false))) {
                    let last = Self::error_text(meta::prop(root, "finalError"), &failed_words());
                    inner.failed = true;
                    inner.pi.authentication_failure = failure::pi(&last);
                    self.log("error", &last);
                }
            }
            Some("extension_error") => self.log("system", &Self::error_text(meta::prop(root, "error"), "")),
            Some("extension_ui_request") => {
                // A dialog waits for an answer: select, confirm and input are the pane's question card; an editor, or one the card
                // cannot show, gets the answer no pane can give it.
                if !matches!(meta::text(root, "method"), Some("select" | "confirm" | "input" | "editor"))
                    || meta::text(root, "id").is_none()
                {
                    return later;
                }
                let title = meta::text(root, "title").unwrap_or("").trim();
                let asking = inner
                    .pi
                    .asking
                    .iter()
                    .find(|(_, (question, _))| question == title)
                    .map(|(call, (_, multiple))| Asking { tool_call_id: call.clone(), multiple: *multiple });
                later.push(Later::Dialog { root: root.clone(), asking });
            }
            _ => {}
        }
        later
    }

    /// What [`OutputParser::pi`] left, after the parser's lock: each dialog to the card, else cancelled and named; each ended
    /// `ask_user` call's card withdrawn.
    pub(super) fn pi_later(&self, later: Vec<Later>) {
        if later.is_empty() {
            return;
        }
        let dialogs = lock(&self.pi_dialogs).clone();
        for item in later {
            match item {
                Later::ToolEnded(call) => {
                    if let Some(dialogs) = &dialogs {
                        dialogs.tool_ended(&call);
                    }
                }
                Later::Dialog { root, asking } => {
                    if dialogs.as_ref().is_some_and(|dialogs| dialogs.receive(&root, asking)) {
                        continue;
                    }
                    if let Some(reply) = &self.hooks.pi_reply {
                        let id = meta::text(&root, "id").unwrap_or("");
                        reply(&format!("{}\n", text_json::wire(&json!({"type": "extension_ui_response", "id": id, "cancelled": true}))));
                    }
                    let title = activity::clean(Some(meta::text(&root, "title").unwrap_or("")), 256, true);
                    self.log("system", &strings::get_with("run.notice.piDialogDismissed", &[("title", &title)]));
                }
            }
        }
    }
}
