//! C# `ClaudeStream` (OutputParser.cs:617-703): what Claude Code's stream-json frames say about the turn, the session and the run's
//! input.

use serde_json::{Map, Value};

use crate::failure;
use crate::meta;

const NOTIFICATION_ORIGINS: [&str; 3] = ["task-notification", "task_notification", "peer"];

/// `ClaudeStream.IsNotificationResult`: a `result` a background task's report or a sub-agent's hand-back opened (its
/// `origin.kind`), not the request's own.
pub fn is_notification_result(value: &Value) -> bool {
    meta::text(value, "type") == Some("result")
        && meta::text_of(meta::prop(value, "origin"), "kind").is_some_and(|kind| NOTIFICATION_ORIGINS.contains(&kind))
}

/// `ClaudeStream.PresentFailure`: a frame whose confirmed failure is a Bedrock policy denial, written again with those words
/// replaced by guidance; None for every other frame.
pub fn present_failure(value: &Value) -> Option<String> {
    let mut shown: Option<Map<String, Value>> = None;
    let original = value.as_object()?;
    let kind = meta::text(value, "type");
    if kind == Some("result") && (meta::flag(value, "is_error") || meta::text(value, "subtype").is_some_and(|s| s.starts_with("error"))) {
        if let Some(Value::Array(errors)) = meta::prop(value, "errors")
            && errors.iter().all(Value::is_string)
        {
            let texts: Vec<&str> = errors.iter().filter_map(Value::as_str).collect();
            let guided: Vec<Option<String>> = texts.iter().map(|t| failure::bedrock_guidance(t)).collect();
            if guided.iter().any(Option::is_some) {
                let replaced: Vec<Value> = texts
                    .iter()
                    .zip(&guided)
                    .map(|(text, guide)| Value::String(guide.clone().unwrap_or_else(|| text.to_string())))
                    .collect();
                shown.get_or_insert_with(|| original.clone()).insert("errors".into(), Value::Array(replaced));
            }
        }
        if let Some(result) = meta::text(value, "result")
            && let Some(guidance) = failure::bedrock_guidance(result)
        {
            shown.get_or_insert_with(|| original.clone()).insert("result".into(), Value::String(guidance));
        }
    } else if kind == Some("assistant") && meta::text(value, "error").is_some_and(|e| !e.is_empty()) {
        let content = meta::prop(value, "message").and_then(|m| meta::prop(m, "content"));
        let Some(Value::Array(blocks)) = content else { return None };
        if blocks.iter().any(|block| !block.is_object()) {
            return None;
        }
        for (index, block) in blocks.iter().enumerate() {
            if meta::text(block, "type") == Some("text")
                && let Some(text) = meta::text(block, "text")
                && let Some(guidance) = failure::bedrock_guidance(text)
            {
                let frame = shown.get_or_insert_with(|| original.clone());
                if let Some(slot) = frame.get_mut("message").and_then(|m| m.get_mut("content")).and_then(|c| c.get_mut(index)) {
                    slot["text"] = Value::String(guidance);
                }
            }
        }
    }
    // `JsonNode.ToJsonString()`: the default encoder, compact.
    shown.map(|frame| crate::text_json::wire(&Value::Object(frame)))
}

fn parse_object(line: &str) -> Option<Value> {
    serde_json::from_str::<Value>(line).ok().filter(Value::is_object)
}

/// `ClaudeStream.IsSettlePoint`: a top-level `result` (the request's or a task notification's) or `session_state_changed: idle`.
pub fn is_settle_point(line: &str) -> bool {
    let Some(root) = parse_object(line) else { return false };
    if !meta::top_level(&root) {
        return false;
    }
    meta::text(&root, "type") == Some("result")
        || meta::text(&root, "type") == Some("system")
            && meta::text(&root, "subtype") == Some("session_state_changed")
            && meta::text(&root, "state") == Some("idle")
}

/// `ClaudeStream.SessionState`: idle, running or requires_action of a top-level `session_state_changed` frame.
pub fn session_state(frame: &Value) -> Option<String> {
    (frame.is_object()
        && meta::text(frame, "type") == Some("system")
        && meta::text(frame, "subtype") == Some("session_state_changed")
        && meta::top_level(frame))
    .then(|| meta::text(frame, "state"))
    .flatten()
    .filter(|state| !state.is_empty() && state.encode_utf16().count() <= 64)
    .map(str::to_string)
}

/// `ClaudeStream.ClosesInput`: the request answered, no background task runs, and the CLI said it is idle (or never says).
pub fn closes_input(turn_result_seen: bool, background_running: bool, session_state: Option<&str>) -> bool {
    turn_result_seen && !background_running && matches!(session_state, None | Some("idle"))
}

/// `ClaudeStream.IsTurnResult`: the CLI's own end-of-turn result, not a task notification's, not a sub-agent's.
pub fn is_turn_result(line: &str) -> bool {
    let Some(root) = parse_object(line) else { return false };
    meta::text(&root, "type") == Some("result") && !is_notification_result(&root) && meta::top_level(&root)
}
