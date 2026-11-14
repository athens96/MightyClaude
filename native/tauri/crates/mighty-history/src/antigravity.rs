//! What Antigravity CLI (`agy`) keeps on disk that the history and the session list read (C# `AntigravityRecords`,
//! AntigravityRecords.cs:25-265): each conversation's `brain/<id>/.system_generated/logs/transcript_full.jsonl` (one line per step:
//! `step_index`, `source`, `type` USER_INPUT / PLANNER_RESPONSE / GENERIC / SYSTEM_MESSAGE, `status`, `created_at`, `content`,
//! `tool_calls`, token counts, `error`) and `conversation_summaries.db`, which lists the conversations with their folders and, for
//! sub-agents, their parent. Read-only.

use std::path::{Path, PathBuf};

use mighty_core::json;
use mighty_core::locale::Locale;
use serde_json::{Map, Value, json};

use crate::scan::{self, contains, text};
use crate::{files, sqlite};

/// The records' helpers the live parser and the sub-agent watcher read too (mighty-run's `antigravity::records`, C#
/// `AntigravityRecords`): the paths, the steps' parts and their outcome.
pub use mighty_run::antigravity::records::{
    Outcome, brain, brain_of, conversation_id, database, date, error_text, folder, message, outcome_of, parameters, request,
    spawned_conversations, stream_state, title, tool_text, transcript,
};

/// `Opening`: the request a USER_INPUT line opens, with when it was written and its key ("step<index>"). Lines without the word are
/// rejected on their raw bytes first; however agy spaces its JSON, a line that has it is parsed.
pub fn opening(line: &[u8]) -> Option<(String, Option<String>, Option<String>)> {
    if !contains(line, "USER_INPUT") {
        return None;
    }
    let value = scan::parse(line)?;
    if json::text(&value, "type") != Some("USER_INPUT") || json::text(&value, "source") == Some("SYSTEM") {
        return None;
    }
    let prompt = request(json::text(&value, "content"))?;
    let key = json::integer(&value, "step_index").map(|index| format!("step{index}"));
    Some((prompt, json::text(&value, "created_at").map(str::to_string), key))
}

fn step(fields: Map<String, Value>) -> Value {
    json!({"event": "step_update", "step_update": Value::Object(fields)})
}

fn calls(step: &Value) -> Vec<Value> {
    step.get("tool_calls").and_then(Value::as_array).map(|c| c.iter().take(64).cloned().collect()).unwrap_or_default()
}

/// `StreamEvents`: one recorded request's steps (after its USER_INPUT) as the stream-json events agy printed for them, so a replay goes
/// through the live graph. The request completed when its last step is an answer without tool calls, failed when that answer failed, and
/// was cut short when it was cancelled or ended on a tool step (one that was declined, say: the failed tool shows as such).
pub fn stream_events(steps: &[Value], conversation: &str, locale: &Locale) -> (Vec<Value>, &'static str) {
    let mut events = vec![json!({"event": "init", "conversation_id": conversation, "init": {}})];
    let mut calls_open: Vec<(String, Value)> = vec![];
    let mut unanswered: i64 = 0;
    let open = |call: &(String, Value), unanswered: &mut i64| -> Value {
        *unanswered -= 1;
        let mut fields = Map::new();
        fields.insert("step_index".into(), json!(*unanswered));
        fields.insert("state".into(), json!("ACTIVE"));
        fields.insert("step_type".into(), json!("tool"));
        fields.insert("tool_name".into(), json!(call.0));
        fields.insert("tool_info".into(), json!({"name": call.0, "parameters": parameters(Some(&call.1))}));
        step(fields)
    };
    let mut last: Option<&Value> = None;
    for value in steps {
        let (Some(kind), Some(index)) = (json::text(value, "type"), json::integer(value, "step_index")) else { continue };
        let outcome = outcome_of(json::text(value, "status"));
        let state = stream_state(outcome);
        match kind {
            "PLANNER_RESPONSE" => {
                // Calls the model moved past without a result step stay open.
                for call in std::mem::take(&mut calls_open) {
                    events.push(open(&call, &mut unanswered));
                }
                let mut fields = Map::new();
                fields.insert("step_index".into(), json!(index));
                fields.insert("state".into(), json!(state));
                fields.insert("step_type".into(), json!("agent_response"));
                if let Some(text) = json::text(value, "content").filter(|t| !t.is_empty()) {
                    fields.insert("text_delta".into(), json!(text));
                }
                let mut usage = Map::new();
                for key in ["input_tokens", "output_tokens", "cache_read_tokens"] {
                    if let Some(number) = value.get(key).filter(|v| v.is_number()) {
                        usage.insert(key.into(), number.clone());
                    }
                }
                if !usage.is_empty() {
                    fields.insert("usage".into(), Value::Object(usage));
                }
                events.push(step(fields));
                for call in calls(value) {
                    if let Some(name) = json::text(&call, "name").filter(|n| !n.is_empty()) {
                        calls_open.push((name.to_string(), call.get("args").cloned().unwrap_or(Value::Null)));
                    }
                }
            }
            "GENERIC" => {
                if calls_open.is_empty() {
                    continue;
                }
                let (name, args) = calls_open.remove(0);
                let mut fields = Map::new();
                fields.insert("step_index".into(), json!(index));
                fields.insert("state".into(), json!(state));
                if name == "invoke_subagent" {
                    let specs: Vec<Value> = args.get("Subagents").and_then(Value::as_array).cloned().unwrap_or_default();
                    let subagents: Vec<Value> = spawned_conversations(json::text(value, "content"))
                        .into_iter()
                        .enumerate()
                        .map(|(offset, id)| {
                            let spec = specs.get(offset);
                            json!({
                                "conversation_id": id,
                                "role": text(spec, "Role").unwrap_or(""),
                                "type_name": text(spec, "TypeName").unwrap_or(""),
                                "initial_prompt": text(spec, "Prompt").unwrap_or(""),
                            })
                        })
                        .collect();
                    fields.insert("step_type".into(), json!("subagent"));
                    fields.insert("tool_name".into(), json!(name));
                    fields.insert("subagent_info".into(), json!({"subagents": subagents}));
                } else {
                    let mut info = Map::new();
                    info.insert("name".into(), json!(name));
                    info.insert("parameters".into(), parameters(Some(&args)));
                    if outcome == Outcome::Failed {
                        let message =
                            error_text(value.get("error")).or_else(|| tool_text(json::text(value, "content"))).unwrap_or_default();
                        info.insert("error".into(), json!({"message": message}));
                    } else if outcome == Outcome::Done
                        && mighty_run::activity::kind(&name) == "command"
                        && let Some(output) = tool_text(json::text(value, "content"))
                    {
                        // Command output is shown as the live run shows it; other results (file contents) are not copied.
                        info.insert("output".into(), json!(output));
                    }
                    fields.insert("step_type".into(), json!("tool"));
                    fields.insert("tool_name".into(), json!(name));
                    fields.insert("tool_info".into(), Value::Object(info));
                }
                events.push(step(fields));
            }
            _ => continue,
        }
        last = Some(value);
    }
    for call in std::mem::take(&mut calls_open) {
        events.push(open(&call, &mut unanswered));
    }
    let Some(last) = last.filter(|l| json::text(l, "type") == Some("PLANNER_RESPONSE")) else { return (events, "stopped") };
    match outcome_of(json::text(last, "status")) {
        Outcome::Stopped => return (events, "stopped"),
        Outcome::Failed => {
            let message = error_text(last.get("error")).unwrap_or_else(|| {
                locale.get_with("run.error.providerRunFailed", &[("name", mighty_core::model_label::provider_label("gemini"))])
            });
            events.push(
                json!({"event": "result", "result": {"conversation_id": conversation, "status": "ERROR", "error": {"message": message}}}),
            );
            return (events, "error");
        }
        Outcome::Done => {}
    }
    if !calls(last).is_empty() {
        return (events, "stopped");
    }
    events.push(json!({"event": "result", "result": {"conversation_id": conversation, "status": "SUCCESS"}}));
    (events, "completed")
}

/// `ConversationSummary(Conversation, Title, Paths, Parent)`: `paths` are the folders the conversation works in (the workspace and any
/// `--add-dir`); `parent` is set for a sub-agent's conversation.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ConversationSummary {
    pub conversation: String,
    pub title: String,
    pub paths: Vec<String>,
    pub parent: String,
}

/// A `file:` URI's local path (`Uri.IsFile` and `LocalPath`), None for any other URI.
pub fn file_uri_path(uri: &str) -> Option<String> {
    let rest = uri.get(..5).filter(|s| s.eq_ignore_ascii_case("file:")).map(|_| &uri[5..])?;
    let rest = rest.strip_prefix("//")?;
    let (host, path) = match rest.find('/') {
        Some(at) => (&rest[..at], &rest[at..]),
        None => (rest, ""),
    };
    let decoded = percent_encoding::percent_decode_str(path).decode_utf8().ok()?.into_owned();
    if crate::windows_paths() {
        if !host.is_empty() && !host.eq_ignore_ascii_case("localhost") {
            // A UNC share: \\host\share\...
            return Some(format!("\\\\{host}{}", decoded.replace('/', "\\")));
        }
        // file:///C:/x is C:\x
        let trimmed = decoded.strip_prefix('/').unwrap_or(&decoded);
        Some(trimmed.replace('/', "\\"))
    } else {
        (host.is_empty() || host.eq_ignore_ascii_case("localhost")).then_some(decoded)
    }
}

/// `Summaries`: every conversation `conversation_summaries.db` lists, read-only, newest rows first; None when the database is missing or
/// unreadable.
pub fn summaries(database: &Path, limit: usize) -> Option<Vec<ConversationSummary>> {
    let sql = format!(
        "SELECT conversation_id, title, workspace_uris, parent_conversation_id FROM conversation_summaries ORDER BY rowid DESC LIMIT {limit}"
    );
    let rows = sqlite::read_only(database, &sql, 4, &[])?;
    let mut list = vec![];
    for row in rows {
        let Some(id) = conversation_id(row[0].as_deref()) else { continue };
        let mut paths = vec![];
        if let Ok(Value::Array(items)) = serde_json::from_str::<Value>(row[2].as_deref().unwrap_or("[]")) {
            for item in items.iter().take(32) {
                if let Some(path) = item.as_str().and_then(file_uri_path) {
                    paths.push(path);
                }
            }
        }
        list.push(ConversationSummary {
            conversation: id.to_string(),
            title: row[1].clone().unwrap_or_default(),
            paths,
            parent: row[3].clone().unwrap_or_default(),
        });
    }
    Some(list)
}

/// Whether the transcript of `conversation` is there (a regular file).
pub fn has_transcript(brain: &Path, conversation: &str) -> Option<PathBuf> {
    transcript(brain, conversation).filter(|t| files::regular(t))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn record_helpers() {
        assert_eq!(
            request(Some("<USER_REQUEST>\n  hello  \n</USER_REQUEST>\n<ADDITIONAL_METADATA>x</ADDITIONAL_METADATA>")).as_deref(),
            Some("hello")
        );
        let message_text = |sender: &str, text: &str| {
            format!(
                "The following is a <SYSTEM_MESSAGE> not actually sent by the user.\n\n<SYSTEM_MESSAGE>\n[Message] timestamp=2026-01-02T03:00:00Z sender={sender} priority=MESSAGE_PRIORITY_HIGH content={text}\n</SYSTEM_MESSAGE>"
            )
        };
        let parent = "aaaaaaaa-0000-4000-8000-000000000001";
        assert_eq!(message(Some(&message_text(parent, "Do it\nnow")), parent).as_deref(), Some("Do it\nnow"));
        assert_eq!(message(Some(&message_text("system", "[Notice]")), parent), None);
        assert_eq!(title(Some(""), Some("research")).as_deref(), Some("research"));
        assert_eq!(title(None, None), None);
        assert_eq!(
            opening(
                br#"{"step_index": 0, "type": "USER_INPUT", "source": "USER_EXPLICIT", "content": "<USER_REQUEST>\nHi\n</USER_REQUEST>"}"#
            )
            .map(|o| o.0)
            .as_deref(),
            Some("Hi")
        );
        assert!(transcript(Path::new("brain"), "../escape").is_none(), "no path leaves the brain folder");
    }

    #[test]
    fn file_uris_read_as_local_paths() {
        if crate::windows_paths() {
            assert_eq!(file_uri_path("file:///C:/work/demo%20project/").as_deref(), Some("C:\\work\\demo project\\"));
        } else {
            assert_eq!(file_uri_path("file:///work/demo%20project/").as_deref(), Some("/work/demo project/"));
        }
        assert_eq!(file_uri_path("https://example.com/"), None);
    }
}
