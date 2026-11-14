//! C# OutputParser.Acp.cs (M/MightyCore/CLIStreamParser.swift acp): an ACP agent (opencode, Hermes) through [`crate::acp::AcpChannel`]
//! — its `session/update`s, the prompt's result and a failed call, as `acp.*` events. Text arrives as deltas and is logged whole when the
//! message, a tool call or the turn ends; thoughts are not copied.

use serde_json::Value;

use super::{Inner, OutputParser};
use crate::acp;
use crate::activity::{self, MAXIMUM_MESSAGE_BYTES, clean};
use crate::failure;
use crate::meta;
use crate::plan_mode::MAXIMUM_TODO_ITEMS;
use crate::strings;

/// The last 8,192 UTF-16 units of `text` (C#'s `text[^8_192..]`), never cutting a pair.
fn tail(text: &str) -> String {
    let units: usize = text.chars().map(char::len_utf16).sum();
    if units <= 8_192 {
        return text.to_string();
    }
    let mut skip = units - 8_192;
    for (at, c) in text.char_indices() {
        if skip == 0 {
            return text[at..].to_string();
        }
        skip = skip.saturating_sub(c.len_utf16());
    }
    String::new()
}

/// The first `units` UTF-16 units of `text` (C#'s `text[..units]`).
fn head(text: &str, units: usize) -> String {
    crate::acp::agents::utf16_prefix(text, units)
}

impl OutputParser {
    /// `Acp`: one `acp.*` event.
    pub(super) fn acp(&self, inner: &mut Inner, root: &Value, kind: Option<&str>) {
        let name = [("name", strings::provider_label(&self.provider))];
        match kind {
            Some("acp.session") => self.resume(inner, meta::text(root, "sessionId")),
            Some("acp.prompt") => {
                inner.acp_reply_tail.clear();
                self.turn(inner, &strings::get_with("run.turn.generating", &name));
            }
            Some("acp.output") => {
                if let Some(output) = meta::text(root, "text") {
                    self.log("output", output);
                }
            }
            Some("acp.update") => {
                let Some(update) = meta::prop(root, "update").filter(|u| u.is_object()) else { return };
                match meta::text(update, "sessionUpdate") {
                    Some("agent_message_chunk") => self.acp_text(inner, update),
                    Some("tool_call" | "tool_call_update") => self.acp_tool(inner, update),
                    Some("plan") => {
                        // A plan's entries as the checklist rows other agents show.
                        let lines: Vec<String> = meta::prop(update, "entries")
                            .and_then(Value::as_array)
                            .into_iter()
                            .flatten()
                            .take(MAXIMUM_TODO_ITEMS)
                            .filter(|entry| entry.is_object() && meta::text(entry, "content").is_some())
                            .map(|entry| {
                                let mark = if meta::text(entry, "status") == Some("completed") { "[x] " } else { "[ ] " };
                                format!("{mark}{}", clean(meta::text(entry, "content"), 400, true))
                            })
                            .collect();
                        if !lines.is_empty() {
                            self.log("system", &lines.join("\n"));
                        }
                    }
                    // Thoughts, usage, commands, modes and options are not transcript lines.
                    _ => {}
                }
            }
            Some("acp.prompt_result") => {
                self.acp_flush_text(inner);
                match meta::text(root, "stopReason") {
                    Some(reason @ ("max_tokens" | "max_turn_requests")) => {
                        self.log("system", &strings::get_with("run.notice.acpStopped", &[("reason", reason)]));
                    }
                    Some("refusal") => self.log("system", &strings::get_with("run.notice.acpRefused", &name)),
                    _ => {}
                }
                inner.authentication_failure = false;
                self.turn(inner, &strings::get_with("run.turn.finishing", &name));
            }
            Some("acp.error") => {
                self.acp_flush_text(inner);
                inner.failed = true;
                let message = meta::text(root, "message").filter(|m| !m.is_empty());
                let data = meta::prop(root, "data");
                // The Python ACP SDK (Hermes) puts an unexpected failure's words in `data.details`.
                let detail = match data {
                    Some(Value::Object(_)) => meta::text_of(data, "message").or_else(|| meta::text_of(data, "details")),
                    Some(Value::String(text)) => Some(text.as_str()),
                    _ => None,
                };
                let mut text = [message, detail].into_iter().flatten().filter(|part| !part.is_empty()).collect::<Vec<_>>().join("\n");
                if self.provider == "hermes" {
                    inner.authentication_failure =
                        meta::flag(root, "signIn") || failure::hermes(&format!("{text}\n{}", inner.acp_reply_tail));
                    // Hermes's gateway names a failed turn only by this word; the reply above said why.
                    if detail == Some("admitted_turn_failed") {
                        text = strings::get("run.error.hermesTurnFailed");
                    } else if message == Some("Internal error")
                        && let Some(detail) = detail.filter(|d| !d.is_empty())
                    {
                        text = detail.to_string();
                    }
                } else {
                    inner.authentication_failure = meta::safe_integer(root, "code") == Some(-32000) || failure::opencode(&text);
                }
                let shown = if text.is_empty() { strings::get_with("run.error.providerRunFailed", &name) } else { head(&text, 32_768) };
                self.log("error", &shown);
            }
            _ => {}
        }
    }

    fn acp_text(&self, inner: &mut Inner, update: &Value) {
        // A sub-agent's own reply (OpenCode 2 marks its child sessions' updates) is not the answer.
        if let Some(Value::Object(marks)) = meta::prop(update, "_meta")
            && marks.contains_key("opencode/child-session")
        {
            return;
        }
        let message = meta::text(update, "messageId");
        if let Some(message) = message
            && inner.acp_message.as_deref().is_some_and(|previous| previous != message)
        {
            self.acp_flush_text(inner);
        }
        if let Some(message) = message {
            inner.acp_message = Some(message.to_string());
        }
        let Some(content) = meta::prop(update, "content").filter(|c| c.is_object()) else { return };
        if meta::text(content, "type") != Some("text") {
            return;
        }
        let Some(mut text) = meta::text(content, "text").filter(|t| !t.is_empty()) else { return };
        // A reply does not begin with the blank lines an agent streams ahead of it.
        if inner.pending.is_empty() {
            text = text.trim_start_matches(['\r', '\n']);
            if text.is_empty() {
                return;
            }
        }
        // Hermes sends a reply it streamed once more, whole, when the turn settles (with a note appended when a hook added one): the whole
        // takes the streamed text's place.
        let shown = inner.pending.trim().to_string();
        let whole = text.trim();
        if shown.len() >= 16 && whole.starts_with(&shown) {
            inner.pending = activity::prefix_utf8(whole, MAXIMUM_MESSAGE_BYTES);
            inner.pending_truncated = whole.len() > MAXIMUM_MESSAGE_BYTES;
            inner.acp_reply_tail = tail(whole);
            return;
        }
        let remaining = MAXIMUM_MESSAGE_BYTES.saturating_sub(inner.pending.len());
        if text.len() > remaining {
            inner.pending_truncated = true;
        }
        let kept = activity::prefix_utf8(text, remaining);
        inner.pending.push_str(&kept);
        inner.acp_reply_tail = tail(&format!("{}{text}", inner.acp_reply_tail));
    }

    fn acp_tool(&self, inner: &mut Inner, update: &Value) {
        self.acp_flush_text(inner);
        let Some(id) = meta::text(update, "toolCallId") else { return };
        let state = match meta::text(update, "status") {
            Some("completed") => "completed",
            Some("failed") => "error",
            _ => "running",
        };
        let known = inner.activities.contains_key(&activity::id(&self.namespace, id));
        let (kind, title) = (meta::text(update, "kind"), meta::text(update, "title"));
        // A settled call that was never announced and says nothing of itself (the close of a permission card's own bubble) is not a row.
        if !known && state != "running" && kind.is_none() && title.is_none() {
            return;
        }
        let mut tool_name = (kind.is_some() || !known)
            .then(|| acp::tool_name(kind, meta::text(update, "name").map(str::to_string).or_else(|| acp::titled_tool(title)).as_deref()));
        // An update that repeats a kind without a name (Hermes's "other") does not rename the row.
        if known && tool_name.as_deref() == Some("Tool") {
            tool_name = None;
        }
        // Hermes sends no `rawInput` for its own tools: the path, shell line or title stands in.
        let input = match meta::prop(update, "rawInput") {
            Some(raw) if !raw.is_null() => Some(acp::display_input(Some(raw))),
            _ if known => None,
            _ => update.as_object().and_then(acp::described_input),
        };
        let output = if state == "running" { None } else { acp_output(update) };
        self.tool(inner, Some(id), state, tool_name.as_deref(), input.as_ref(), output.clone(), None);
        if self.hooks.activity.is_none()
            && state != "running"
            && let Some(output) = output.filter(|o| !o.is_empty())
        {
            self.log("output", &output);
        }
    }

    /// `AcpFlushText`: the reply streamed so far, logged whole; the latest one is also the request's answer in the graph.
    fn acp_flush_text(&self, inner: &mut Inner) {
        let text = inner.pending.trim_end().to_string();
        if !text.is_empty() {
            let key = format!("acp:{}", inner.acp_message.as_deref().unwrap_or(""));
            self.assistant(inner, &text, Some(&key));
            if let Some(answer) = &self.hooks.answer {
                answer(&text);
            }
        }
        if inner.pending_truncated {
            self.log("system", &strings::get("run.notice.messageTruncated"));
        }
        inner.pending.clear();
        inner.pending_truncated = false;
    }
}

/// `OutputParser.AcpOutput`: a settled ACP tool call's result — its text content blocks, a diff's path, or `rawOutput`.
pub fn acp_output(update: &Value) -> Option<String> {
    let mut parts: Vec<String> = Vec::new();
    if let Some(Value::Array(content)) = meta::prop(update, "content") {
        for block in content.iter().take(64).filter(|b| b.is_object()) {
            match meta::text(block, "type") {
                Some("content") => {
                    if let Some(inner) = meta::prop(block, "content").filter(|i| i.is_object())
                        && meta::text(inner, "type") == Some("text")
                        && let Some(text) = meta::text(inner, "text")
                    {
                        parts.push(text.to_string());
                    }
                }
                Some("diff") => {
                    if let Some(path) = meta::text(block, "path") {
                        parts.push(path.to_string());
                    }
                }
                _ => {}
            }
        }
    }
    if parts.is_empty() {
        match meta::prop(update, "rawOutput") {
            Some(Value::String(raw)) => parts.push(raw.clone()),
            Some(raw @ Value::Object(_)) => match meta::text(raw, "output").or_else(|| meta::text(raw, "error")) {
                Some(said) => parts.push(said.to_string()),
                None => return activity::output(Some(raw)),
            },
            _ => {}
        }
    }
    let joined = parts.join("\n");
    (!joined.is_empty()).then_some(joined)
}
