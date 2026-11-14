//! What Antigravity CLI (`agy`) keeps on disk, as the parser, the sub-agent watcher and the history read it (C# `AntigravityRecords`,
//! AntigravityRecords.cs:25-140). Each conversation has `%USERPROFILE%\.gemini\antigravity-cli\brain\<conversation id>\.system_generated\
//! logs\transcript_full.jsonl`: one line per step (`step_index`, `source`, `type` USER_INPUT / PLANNER_RESPONSE / GENERIC / SYSTEM_MESSAGE,
//! `status`, `created_at`, `content`, `tool_calls`, token counts, `error`). A PLANNER_RESPONSE's tool calls are answered by the GENERIC
//! steps that follow it, in order. A sub-agent is a conversation of its own whose instructions arrive as SYSTEM_MESSAGE steps from its
//! parent. Read-only.
//!
//! The history's own parts (`Opening`, `StreamEvents`, `Summaries`) are mighty-history's; the watcher (`AntigravityChildRecord`,
//! `AntigravitySubagentWatcher`) is mighty-graph's.

use std::path::{Path, PathBuf};
use std::sync::LazyLock;

use mighty_core::time::{self, DateTimeOffset};
use regex::Regex;
use serde_json::{Map, Value};

use crate::activity;
use crate::meta;

static UUID: LazyLock<Regex> =
    LazyLock::new(|| Regex::new(r"\A[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\z").expect("regex"));
static SPAWNED: LazyLock<Regex> = LazyLock::new(|| Regex::new(r#""conversationId":\s*"(?<id>[0-9A-Fa-f-]{36})""#).expect("regex"));

/// `Folder(home)`: `%USERPROFILE%\.gemini\antigravity-cli`.
pub fn folder(home: &Path) -> PathBuf {
    home.join(".gemini").join("antigravity-cli")
}

/// `Brain(home)`.
pub fn brain(home: &Path) -> PathBuf {
    folder(home).join("brain")
}

/// `Database(home)`.
pub fn database(home: &Path) -> PathBuf {
    folder(home).join("conversation_summaries.db")
}

/// `ConversationID`: None for anything that is not a conversation id.
pub fn conversation_id(value: Option<&str>) -> Option<&str> {
    value.filter(|v| UUID.is_match(v))
}

/// `Transcript`: None for anything that is not a conversation id, so no path leaves the brain folder.
pub fn transcript(brain: &Path, conversation: &str) -> Option<PathBuf> {
    conversation_id(Some(conversation))?;
    Some(brain.join(conversation).join(".system_generated").join("logs").join("transcript_full.jsonl"))
}

/// `BrainOf`: the brain folder a transcript lies in.
pub fn brain_of(transcript: &Path) -> PathBuf {
    let mut path = transcript.to_path_buf();
    for _ in 0..4 {
        if let Some(parent) = path.parent() {
            path = parent.to_path_buf();
        }
    }
    path
}

/// `Request`: what the user typed, the `<USER_REQUEST>` part of a USER_INPUT step.
pub fn request(content: Option<&str>) -> Option<String> {
    let content = content?;
    let mut text = content;
    if let Some(start) = content.find("<USER_REQUEST>") {
        let from = start + "<USER_REQUEST>".len();
        let end = content[from..].find("</USER_REQUEST>").map_or(content.len(), |e| e + from);
        text = &content[from..end];
    }
    let trimmed = text.trim();
    (!trimmed.is_empty()).then(|| trimmed.to_string())
}

/// `Message`: the text of a `[Message] … sender=<id> … content=…` system message from `sender`.
pub fn message(content: Option<&str>, sender: &str) -> Option<String> {
    let content = content?;
    let needle = format!("sender={sender} ");
    // `IndexOf(…, OrdinalIgnoreCase)`: the ids and the marker are ASCII.
    let start = (0..=content.len().checked_sub(needle.len())?)
        .find(|at| content.as_bytes()[*at..*at + needle.len()].eq_ignore_ascii_case(needle.as_bytes()))?;
    let after = start + 8 + sender.len();
    let body = content.get(after..)?.find(" content=")? + after;
    let mut text = &content[body + " content=".len()..];
    if let Some(end) = text.rfind("</SYSTEM_MESSAGE>") {
        text = &text[..end];
    }
    let trimmed = text.trim();
    (!trimmed.is_empty()).then(|| trimmed.to_string())
}

/// The first 65,536 UTF-16 units of `content` (C#'s `content[..65_536]`), a pair cut in two read as U+FFFD.
fn first_units(content: &str) -> String {
    if content.len() <= 65_536 {
        return content.to_string();
    }
    let units: Vec<u16> = content.encode_utf16().take(65_537).collect();
    if units.len() <= 65_536 { content.to_string() } else { String::from_utf16_lossy(&units[..65_536]) }
}

/// `ToolText`: a tool step's text without agy's "Created At:" / "Completed At:" header.
pub fn tool_text(content: Option<&str>) -> Option<String> {
    let cut = first_units(content?);
    let mut lines: Vec<&str> = cut.split('\n').collect();
    while let Some(first) = lines.first() {
        if first.starts_with("Created At: ") || first.starts_with("Completed At: ") || first.trim().is_empty() {
            lines.remove(0);
        } else {
            break;
        }
    }
    let text = lines.join("\n").trim().to_string();
    (!text.is_empty()).then_some(text)
}

/// `Outcome`: DONE (or none) went through, ERROR failed, anything else (CANCELED, INTERRUPTED, …) was cut short.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Outcome {
    Done,
    Failed,
    Stopped,
}

/// `OutcomeOf`.
pub fn outcome_of(status: Option<&str>) -> Outcome {
    match status.unwrap_or("DONE").to_uppercase().as_str() {
        "DONE" => Outcome::Done,
        "ERROR" => Outcome::Failed,
        _ => Outcome::Stopped,
    }
}

/// `StreamState`: the stream state the live parser reads for that status.
pub fn stream_state(outcome: Outcome) -> &'static str {
    match outcome {
        Outcome::Done => "DONE",
        Outcome::Failed => "ERROR",
        Outcome::Stopped => "CANCELED",
    }
}

/// `Date`: when a step was written, None when it does not say.
pub fn date(step: &Value) -> Option<DateTimeOffset> {
    time::parse(meta::text(step, "created_at"))
}

/// `ErrorText`: an error as a string, or its `message`; None when empty.
pub fn error_text(value: Option<&Value>) -> Option<String> {
    let text = match value {
        Some(Value::String(s)) => Some(s.as_str()),
        Some(other) => meta::text(other, "message"),
        None => None,
    };
    text.filter(|t| !t.is_empty()).map(str::to_string)
}

/// `Title(role, typeName)`: "File Reader (research)", as agy names a sub-agent's messages.
pub fn title(role: Option<&str>, type_name: Option<&str>) -> Option<String> {
    let name = role.map(str::trim).unwrap_or("");
    let kind = type_name.map(str::trim).unwrap_or("");
    if name.is_empty() {
        return (!kind.is_empty()).then(|| kind.to_string());
    }
    Some(if kind.is_empty() || kind == name { name.to_string() } else { format!("{name} ({kind})") })
}

/// `Parameters`: a recorded tool call's arguments as the live stream's parameters (no `toolAction` / `toolSummary`).
pub fn parameters(args: Option<&Value>) -> Value {
    let mut result = Map::new();
    if let Some(Value::Object(fields)) = args {
        for (name, value) in fields {
            if name != "toolAction" && name != "toolSummary" {
                result.insert(name.clone(), value.clone());
            }
        }
    }
    Value::Object(result)
}

/// `OutputParser.AntigravityParameters`: agy names its tool parameters in PascalCase (`AbsolutePath`, `CommandLine`); the activity
/// summary reads snake_case. None for anything but an object (C#'s `default` element).
pub fn antigravity_parameters(value: Option<&Value>) -> Option<Value> {
    let Some(Value::Object(fields)) = value else { return None };
    let mut result = Map::new();
    for (name, field) in fields.iter().take(64) {
        let mut snake = String::new();
        for character in name.chars() {
            if character.is_uppercase() && !snake.is_empty() && !snake.ends_with('_') {
                snake.push('_');
            }
            snake.extend(character.to_lowercase());
        }
        result.insert(snake, field.clone());
    }
    Some(Value::Object(result))
}

/// `Summary`: a recorded tool call's one-line summary — what the live stream's parameters say, else agy's own `toolSummary`.
pub fn summary(tool: &str, args: Option<&Value>) -> String {
    let parameters = antigravity_parameters(Some(&parameters(args)));
    let summary = activity::summary(tool, parameters.as_ref());
    if summary == activity::clean(Some(tool), activity::MAXIMUM_SUMMARY_BYTES, true)
        && let Some(described) = args.and_then(|a| meta::text(a, "toolSummary")).filter(|d| !d.is_empty())
    {
        return activity::clean(Some(described), activity::MAXIMUM_SUMMARY_BYTES, true);
    }
    summary
}

/// `SpawnedConversations`: the conversation ids an `invoke_subagent` result step names.
pub fn spawned_conversations(content: Option<&str>) -> Vec<String> {
    let Some(content) = content else { return vec![] };
    let cut = first_units(content);
    SPAWNED.captures_iter(&cut).filter_map(|c| c.name("id")).filter_map(|m| conversation_id(Some(m.as_str())).map(str::to_string)).collect()
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    #[test]
    fn pascal_case_parameters_read_as_snake_case() {
        let read = antigravity_parameters(Some(&json!({"TargetFile": "/a", "SearchPath": "/b", "already_snake": 1}))).unwrap();
        let mut names: Vec<&String> = read.as_object().unwrap().keys().collect();
        names.sort();
        assert_eq!(names, ["already_snake", "search_path", "target_file"]);
        assert_eq!(antigravity_parameters(Some(&json!("x"))), None);
    }

    #[test]
    fn a_recorded_call_is_summed_up_by_its_parameters_else_agys_summary() {
        let args = json!({"AbsolutePath": "/w/a.txt", "toolAction": "Reading", "toolSummary": "Read a file"});
        assert_eq!(summary("view_file", Some(&args)), "/w/a.txt");
        let args = json!({"Recipient": "x", "toolSummary": "Summary of send_message"});
        assert_eq!(summary("send_message", Some(&args)), "Summary of send_message");
        assert_eq!(summary("send_message", None), "send_message");
    }

    #[test]
    fn a_long_tool_text_is_cut_at_its_first_65536_units() {
        let long = format!("Created At: x\n{}", "a".repeat(70_000));
        assert_eq!(tool_text(Some(&long)).map(|t| t.len()), Some(65_536 - "Created At: x\n".len()));
        assert_eq!(tool_text(Some("Created At: x\nCompleted At: y\n\n")), None);
    }
}
