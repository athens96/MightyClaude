//! C# `ActivitySupport` (AgentActivity.cs:13-94): an agent's tool rows, their ids, kinds and one-line summaries. The normalize and
//! `Clean` rules are mighty-core's (the state's normalize reads them too).

use mighty_core::model::AgentActivity;
use mighty_core::normalize;
use serde_json::Value;
use sha2::{Digest, Sha256};

use crate::meta;
use crate::strings;

pub const MAXIMUM_MESSAGE_BYTES: usize = 131_072;
pub const MAXIMUM_SUMMARY_BYTES: usize = 1000;
pub const MAXIMUM_OUTPUT_BYTES: usize = 8192;
pub const MAXIMUM_DURATION_MS: f64 = 30.0 * 24.0 * 60.0 * 60.0 * 1000.0;

/// `ActivitySupport.Terminal`.
pub fn terminal(state: &str) -> bool {
    matches!(state, "completed" | "error" | "stopped")
}

/// `ActivitySupport.ValidDuration`.
pub fn valid_duration(value: f64) -> bool {
    value.is_finite() && (0.0..=MAXIMUM_DURATION_MS).contains(&value)
}

/// `ActivitySupport.PrefixUtf8`.
pub fn prefix_utf8(value: &str, maximum_bytes: usize) -> String {
    normalize::prefix_utf8(value, maximum_bytes)
}

/// `ActivitySupport.Clean`.
pub fn clean(value: Option<&str>, maximum_bytes: usize, single_line: bool) -> String {
    normalize::activity_clean(value, maximum_bytes, single_line)
}

/// `ActivitySupport.Normalize`.
pub fn normalized(value: &AgentActivity, restoring: bool) -> Option<AgentActivity> {
    normalize::activity(value, restoring)
}

/// `new AgentActivity(id, provider, kind, state, summary, toolName, output, durationMs)`.
pub fn make(id: &str, provider: &str, kind: &str, state: &str, summary: &str) -> AgentActivity {
    AgentActivity {
        id: id.to_string(),
        provider: provider.to_string(),
        kind: kind.to_string(),
        state: state.to_string(),
        summary: summary.to_string(),
        tool_name: None,
        output: None,
        duration_ms: None,
    }
}

/// `ActivitySupport.DurationLabel`.
pub fn duration_label(value: &AgentActivity) -> Option<String> {
    if value.kind == "turn" || !terminal(&value.state) {
        return None;
    }
    let ms = value.duration_ms.filter(|ms| valid_duration(*ms))?;
    if ms < 1.0 {
        return Some(if ms == 0.0 { "0ms".into() } else { "<1ms".into() });
    }
    if ms < 1000.0 {
        return Some(format!("{}ms", ms as i64));
    }
    if ms < 60000.0 {
        let seconds = format!("{:.1}", (ms / 100.0).floor() / 10.0);
        return Some(strings::get_with("run.activity.durationSeconds", &[("seconds", &seconds)]));
    }
    let seconds = (ms / 1000.0) as i64;
    let minutes = (seconds / 60).to_string();
    Some(if seconds % 60 == 0 {
        strings::get_with("run.activity.durationMinutes", &[("minutes", &minutes)])
    } else {
        strings::get_with("run.activity.durationMinutesSeconds", &[("minutes", &minutes), ("seconds", &(seconds % 60).to_string())])
    })
}

/// `ActivitySupport.Id`: `activity-` and the first 48 hex digits of SHA-256(`ns|key`), lower case.
pub fn id(namespace: &str, key: &str) -> String {
    let digest = Sha256::digest(format!("{namespace}|{key}").as_bytes());
    let hex: String = digest.iter().map(|b| format!("{b:02x}")).collect();
    format!("activity-{}", &hex[..48])
}

/// `ActivitySupport.Kind`.
pub fn kind(tool: &str) -> &'static str {
    let name = tool.to_lowercase();
    match name.as_str() {
        "bash" | "run_shell_command" | "command_execution" | "exec_command" | "shell" | "run_command" | "send_command_input"
        | "command_status" => "command",
        "read" | "read_file" | "read_many_files" | "list_directory" | "ls" | "view_file" | "list_dir" | "read_resource"
        | "list_resources" => "read",
        "edit"
        | "write"
        | "write_file"
        | "replace"
        | "apply_patch"
        | "file_change"
        | "notebookedit"
        | "write_to_file"
        | "replace_file_content"
        | "multi_replace_file_content"
        | "sed_file"
        | "notebook_edit" => "edit",
        "grep" | "glob" | "find" | "search_file_content" | "glob_search" | "grep_search" | "find_by_name" => "search",
        "websearch" | "webfetch" | "web_search" | "google_web_search" | "web_fetch" | "search_web" | "read_url_content" => "web",
        _ if name.starts_with("browser_")
            || matches!(
                name.as_str(),
                "open_browser_url"
                    | "read_browser_page"
                    | "list_browser_pages"
                    | "click_browser_pixel"
                    | "execute_browser_javascript"
                    | "capture_browser_screenshot"
                    | "capture_browser_console_logs"
            ) =>
        {
            "web"
        }
        "agent" | "task" | "spawn_agent" | "delegate_to_agent" | "invoke_subagent" | "define_subagent" | "manage_subagents" => "agent",
        _ => "tool",
    }
}

const SUMMARY_KEYS: [&str; 15] = [
    "command",
    "command_line",
    "file_path",
    "absolute_path",
    "path",
    "pattern",
    "query",
    "url",
    "description",
    "target_file",
    "filename",
    "glob",
    "directory_path",
    "search_path",
    "search_directory",
];

/// `ActivitySupport.Summary`: what a tool call is about, in one line (its command, path, pattern, query, ...), else the tool's name.
pub fn summary(tool: &str, input: Option<&Value>) -> String {
    if let Some(Value::String(text)) = input
        && text.encode_utf16().count() <= 65536
        && let Ok(parsed) = serde_json::from_str::<Value>(text)
    {
        return summary(tool, Some(&parsed));
    }
    if let Some(input) = input {
        for key in SUMMARY_KEYS {
            match meta::prop(input, key) {
                Some(Value::String(selected)) if !selected.is_empty() => return clean(Some(selected), MAXIMUM_SUMMARY_BYTES, true),
                Some(Value::Array(items)) => {
                    let parts: Vec<&str> = items.iter().take(8).filter_map(Value::as_str).collect();
                    if !parts.is_empty() {
                        return clean(Some(&parts.join(" ")), MAXIMUM_SUMMARY_BYTES, true);
                    }
                }
                _ => {}
            }
        }
        if let Some(Value::Array(changes)) = meta::prop(input, "changes") {
            let paths: Vec<String> = changes
                .iter()
                .take(12)
                .filter_map(|c| {
                    let path = meta::text(c, "path")?;
                    Some(match meta::text(c, "kind") {
                        Some(kind) => format!("{kind} {path}"),
                        None => path.to_string(),
                    })
                })
                .collect();
            if !paths.is_empty() {
                return clean(Some(&paths.join(" · ")), MAXIMUM_SUMMARY_BYTES, true);
            }
        }
    }
    clean(Some(tool), MAXIMUM_SUMMARY_BYTES, true)
}

/// `ActivitySupport.Output`: a tool result's text (a string, the text blocks of a content list, a message, or an object's content).
pub fn output(value: Option<&Value>) -> Option<String> {
    let value = value?;
    match value {
        Value::String(text) => Some(clean(Some(text), MAXIMUM_OUTPUT_BYTES, false)),
        Value::Array(blocks) => {
            let texts: Vec<&str> = blocks
                .iter()
                .take(32)
                .filter(|b| meta::text(b, "type") == Some("text"))
                .map(|b| meta::text(b, "text").unwrap_or(""))
                .collect();
            Some(clean(Some(&texts.join("\n")), MAXIMUM_OUTPUT_BYTES, false))
        }
        _ => {
            if let Some(message) = meta::text(value, "message") {
                return Some(clean(Some(message), MAXIMUM_OUTPUT_BYTES, false));
            }
            match meta::prop(value, "content") {
                Some(content) if value.is_object() => output(Some(content)),
                _ => None,
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn ids_kinds_and_summaries() {
        assert_eq!(id("ns", "k").len(), "activity-".len() + 48);
        assert_eq!(kind("Bash"), "command");
        assert_eq!(kind("browser_click"), "web");
        assert_eq!(kind("Unknown"), "tool");
        let input: Value = serde_json::json!({"file_path": "/w/a.md"});
        assert_eq!(summary("Read", Some(&input)), "/w/a.md");
        assert_eq!(summary("Read", Some(&Value::String("{\"pattern\":\"x*\"}".into()))), "x*");
        assert_eq!(summary("Edit", Some(&serde_json::json!({"changes":[{"path":"a","kind":"add"},{"path":"b"}]}))), "add a · b");
        assert_eq!(
            output(Some(&serde_json::json!([{"type":"text","text":"a"},{"type":"image"},{"type":"text","text":"b"}]))).as_deref(),
            Some("a\nb")
        );
        assert_eq!(output(Some(&serde_json::json!({"content":"x"}))).as_deref(), Some("x"));
        assert_eq!(output(None), None);
    }
}
