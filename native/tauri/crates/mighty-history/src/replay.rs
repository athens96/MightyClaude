//! C# `HistoryReplay` (SessionHistory.cs:716-982): one recorded request fed through a fresh `ExecutionGraphTracker`, the one a live run
//! uses, and built into its graph run and the notice runs its background work opened.

use mighty_core::graph::MightyGraphRun;
use mighty_core::json;
use mighty_core::locale::Locale;
use mighty_core::model::LogEntry;
use mighty_core::time::{self, DateTimeOffset};
use mighty_core::wire;
use mighty_graph::ExecutionGraphTracker;
use serde_json::{Map, Value, json};

use crate::antigravity;
use crate::history::RUN_BUDGET;
use crate::scan::{self, HistoryFormat, HistoryTurn, prop, text, text_blocks};

/// Ids of runs read from the record start with this, so they never meet a retained run's id.
pub const RUN_PREFIX: &str = "record-";

/// Sub-agents' transcripts one chunk's requests may read together.
pub const ANTIGRAVITY_CHILD_BYTES: i64 = 32 * 1_048_576;

/// `HistoryReplay.RunID`.
pub fn run_id(turn: &HistoryTurn) -> String {
    match &turn.key {
        Some(key) if wire::identifier(&format!("{RUN_PREFIX}{key}")) => format!("{RUN_PREFIX}{key}"),
        _ => format!("{RUN_PREFIX}o{}", turn.start),
    }
}

const SECOND: i64 = 10_000_000;

/// `HistoryReplay.Runs`: the request, then the notice runs its background work opened, as live. `child_bytes` is what reading Antigravity
/// sub-agents' transcripts may still spend.
pub fn runs(turn: &HistoryTurn, format: &HistoryFormat, child_bytes: &mut i64, locale: &Locale) -> Vec<MightyGraphRun> {
    let provider = format.provider.as_str();
    let model = if provider == "codex" { codex_model(&turn.lines) } else { None };
    let id = run_id(turn);
    let mut tracker =
        ExecutionGraphTracker::new(&id, Some(&turn.prompt), provider, model.as_deref(), Box::new(|_| {}), None, None, *locale);
    let mut latest = turn.date;
    let status = {
        let mut at = |value: &Value| {
            if let Some(date) = scan::date(json::text(value, "timestamp")) {
                latest = Some(date);
            }
        };
        match provider {
            "codex" => codex(&turn.lines, format.thread.as_deref().unwrap_or(""), &mut tracker, &mut at),
            "gemini" => {
                let mut stamped: Option<DateTimeOffset> = None;
                let status = antigravity_replay(turn, format, &mut tracker, child_bytes, &mut |date| stamped = Some(date), locale);
                if stamped.is_some() {
                    latest = stamped;
                }
                status
            }
            _ => claude(&turn.lines, &mut tracker, &mut at),
        }
    };
    tracker.finish(status);
    let Some(request) = tracker.build_run(false).filter(|r| r.id() == id) else { return vec![] };
    let mut result = vec![];
    for mut run in std::iter::once(request).chain(tracker.build_notice_runs(false)) {
        if run.node_model_label.is_none()
            && let Some(model) = &model
        {
            run.node_model_label = mighty_graph::labels::node_model_label(Some(model), Some("default"), locale);
        }
        // Blocks the tracker made carry the replay's own clock; the request happened when its record says.
        if let Some(settled) = latest {
            let stamp = DateTimeOffset::from_utc_ticks(settled.utc_ticks()).to_o();
            let settle = |entries: &mut Option<Vec<LogEntry>>| {
                for entry in entries.iter_mut().flatten() {
                    if time::parse(entry.timestamp.as_deref()).is_some_and(|date| date.utc_ticks() > settled.utc_ticks() + SECOND) {
                        entry.timestamp = Some(stamp.clone());
                    }
                }
            };
            settle(&mut run.root_entries);
            settle(&mut run.result_entries);
            for agent in run.agents.iter_mut().flatten() {
                settle(&mut agent.entries);
            }
        }
        let mut budget = RUN_BUDGET;
        if let Some(normalized) = mighty_graph::support::normalized(&[run], true, &mut budget, Some(provider)).into_iter().next() {
            result.push(normalized);
        }
    }
    result
}

/// The text of an `attachment.prompt` (a string or text blocks).
fn prompt_text(prompt: Option<&Value>) -> Option<String> {
    match prompt? {
        Value::String(s) => Some(s.clone()),
        Value::Array(blocks) => Some(text_blocks(blocks)),
        _ => None,
    }
}

/// `HistoryReplay.Steer`: a message the user typed while the request ran, recorded as a queued command — shown as the live graph's
/// steer block. Injected kinds are not.
pub fn steer(value: &Value) -> Option<(Option<String>, String)> {
    let attachment = prop(Some(value), "attachment");
    if text(attachment, "type") != Some("queued_command")
        || text(attachment, "commandMode") != Some("prompt")
        || scan::flag(attachment, "isMeta")
        || !scan::human_origin(prop(attachment, "origin"))
    {
        return None;
    }
    let text = prompt_text(prop(attachment, "prompt"))?.trim().to_string();
    (!text.is_empty() && !text.starts_with('<')).then(|| (json::text(value, "uuid").map(str::to_string), text))
}

/// `HistoryReplay.AgentMessage`: a sub-agent's message (its final report, or one it sent while running) taken into a running turn,
/// recorded as a queued command; the live stream shows it to the tracker as a user message.
pub fn agent_message(value: &Value) -> Option<Value> {
    let attachment = prop(Some(value), "attachment");
    let origin = prop(attachment, "origin");
    if text(attachment, "type") != Some("queued_command") || text(origin, "kind") != Some("peer") {
        return None;
    }
    let text = prompt_text(prop(attachment, "prompt"))?;
    if !text.contains(mighty_graph::handback::OPENING) {
        return None;
    }
    Some(json!({"type": "user", "origin": origin.cloned().unwrap_or(Value::Null), "message": {"role": "user", "content": text}}))
}

/// A Claude record line is the stream-json event it was printed as, minus the `result`s, which are rebuilt from the answers: the
/// request's own before the first task notification that opened a turn, then each notice turn's, as live.
fn claude(lines: &[Vec<u8>], tracker: &mut ExecutionGraphTracker, at: &mut dyn FnMut(&Value)) -> &'static str {
    let mut answer: Option<String> = None;
    let (mut interrupted, mut failed, mut answered) = (false, false, false);
    let result = |tracker: &mut ExecutionGraphTracker, answer: &mut Option<String>, answered: &mut bool| {
        let mut value = json!({"type": "result", "subtype": "success", "is_error": false, "result": answer.clone().unwrap_or_default()});
        if *answered {
            value["origin"] = json!({"kind": "task-notification"});
        }
        tracker.consume(&value);
        *answered = true;
        *answer = None;
    };
    for (index, line) in lines.iter().enumerate() {
        let Some(value) = scan::parse(line) else { continue };
        let kind = json::text(&value, "type").unwrap_or("");
        if !matches!(kind, "assistant" | "user" | "system" | "attachment") || json::flag(&value, "isSidechain") {
            continue;
        }
        at(&value);
        let mut consumed = value.clone();
        match kind {
            "attachment" => {
                if let Some((id, text)) = steer(&value) {
                    tracker.steer(&id.unwrap_or_else(|| format!("line-{index}")), &text);
                } else if let Some(message) = agent_message(&value) {
                    tracker.consume(&message);
                }
                continue;
            }
            "system" => {
                if json::text(&value, "subtype") != Some("compact_boundary") {
                    continue;
                }
                if value.get("compact_metadata").is_none()
                    && let Some(metadata) = value.get("compactMetadata")
                    && let Some(object) = consumed.as_object_mut()
                {
                    object.insert("compact_metadata".into(), metadata.clone());
                }
            }
            "user" => {
                if json::flag(&value, "isCompactSummary") {
                    continue;
                }
                let Some(message) = prop(Some(&value), "message").filter(|m| m.is_object()) else { continue };
                let words = match prop(Some(message), "content") {
                    Some(Value::String(s)) => Some(s.clone()),
                    Some(Value::Array(blocks)) => Some(text_blocks(blocks)),
                    _ => None,
                };
                // A sub-agent's message (its final report, or one it sent while running) is a meta line, and like a task notification
                // it opens a turn of its own.
                let peer_message = text(prop(Some(&value), "origin"), "kind") == Some("peer")
                    && words.as_deref().is_some_and(|t| t.contains(mighty_graph::handback::OPENING));
                if json::flag(&value, "isMeta") && !peer_message {
                    continue;
                }
                if words.as_deref().is_some_and(|t| t.starts_with("[Request interrupted by user")) {
                    interrupted = true;
                    continue;
                }
                // A local command's printed output is that command's result.
                if let Some(output) = words.as_deref().and_then(scan::local_command_output) {
                    if !output.is_empty() {
                        answer = Some(output);
                    }
                    continue;
                }
                // A task notification opens a turn of its own: the turn before it ended with its answer.
                if peer_message || words.as_deref().is_some_and(|t| t.contains("<task-notification>")) {
                    result(tracker, &mut answer, &mut answered);
                }
            }
            _ => {
                if json::flag(&value, "isApiErrorMessage") {
                    failed = true;
                }
                if let Some(Value::Array(blocks)) = prop(prop(Some(&value), "message"), "content") {
                    let reply = text_blocks(blocks);
                    if !reply.is_empty() {
                        answer = Some(reply);
                    }
                }
            }
        }
        tracker.consume(&consumed);
    }
    if answer.is_some() {
        result(tracker, &mut answer, &mut answered);
    }
    if interrupted {
        return "stopped";
    }
    if failed { "error" } else { "completed" }
}

/// `HistoryReplay.CodexModel`: the model a turn's `turn_context` names.
pub fn codex_model<L: AsRef<[u8]>>(lines: &[L]) -> Option<String> {
    for line in lines.iter().map(AsRef::as_ref).filter(|l| scan::contains(l, "\"type\":\"turn_context\"")) {
        if let Some(value) = scan::parse(line)
            && let Some(model) = text(prop(Some(&value), "payload"), "model").filter(|m| wire::model(m))
        {
            return Some(model.to_string());
        }
    }
    None
}

/// `HistoryReplay.CodexItem`: a rollout records `codex exec --json` items under other names; each is mapped back to the exec shape the
/// tracker reads.
pub fn codex_item(item: Option<&Value>) -> Option<Value> {
    let item = item?;
    let kind = json::text(item, "type")?;
    let id = json::text(item, "id")?;
    let status = match json::text(item, "status") {
        None | Some("completed") => "completed",
        Some("in_progress") => "in_progress",
        Some(_) => "failed",
    };
    let renamed = |name: &str, with_status: bool| {
        let mut node = item.clone();
        if let Some(object) = node.as_object_mut() {
            object.insert("type".into(), json!(name));
            if with_status {
                object.insert("status".into(), json!(status));
            }
        }
        node
    };
    match kind {
        "AgentMessage" => {
            let text = item
                .get("content")
                .and_then(Value::as_array)
                .map(|c| c.iter().filter_map(|b| json::text(b, "text")).collect::<Vec<_>>().join("\n"))
                .unwrap_or_default();
            (!text.is_empty()).then(|| json!({"id": id, "type": "agent_message", "text": text}))
        }
        "CommandExecution" => {
            let command = match item.get("command") {
                Some(Value::Array(parts)) => {
                    let parts: Vec<String> =
                        parts.iter().map(|p| p.as_str().map(str::to_string).unwrap_or_else(|| p.to_string())).collect();
                    if parts.len() == 3 && (parts[1] == "-lc" || parts[1] == "-c") { parts[2].clone() } else { parts.join(" ") }
                }
                _ => json::text(item, "command").unwrap_or("").to_string(),
            };
            let mut mapped = Map::new();
            mapped.insert("id".into(), json!(id));
            mapped.insert("type".into(), json!("command_execution"));
            mapped.insert("command".into(), json!(command));
            mapped.insert("status".into(), json!(status));
            if let Some(output) = json::text(item, "aggregated_output").or_else(|| json::text(item, "stdout")) {
                mapped.insert("aggregated_output".into(), json!(output));
            }
            if let Some(code) = item.get("exit_code").filter(|c| c.is_number()) {
                mapped.insert("exit_code".into(), code.clone());
            }
            Some(Value::Object(mapped))
        }
        "FileChange" => {
            let changes: Vec<Value> = match item.get("changes") {
                Some(Value::Object(all)) => {
                    let mut names: Vec<(&String, &Value)> = all.iter().collect();
                    names.sort_by(|a, b| mighty_core::normalize::ordinal(a.0, b.0));
                    names
                        .into_iter()
                        .take(64)
                        .map(|(path, change)| json!({"path": path, "kind": json::text(change, "type").unwrap_or("update")}))
                        .collect()
                }
                _ => vec![],
            };
            Some(json!({"id": id, "type": "file_change", "changes": changes, "status": status}))
        }
        "McpToolCall" => Some(renamed("mcp_tool_call", true)),
        "Extension" => (json::text(item, "kind") == Some("web.search"))
            .then(|| json!({"id": id, "type": "web_search", "query": json::text(item, "query").unwrap_or(""), "status": status})),
        "CollabAgentToolCall" => Some(renamed("collab_tool_call", false)),
        "ContextCompaction" => Some(json!({"id": id, "type": "context_compaction"})),
        _ => None,
    }
}

fn codex(lines: &[Vec<u8>], thread: &str, tracker: &mut ExecutionGraphTracker, at: &mut dyn FnMut(&Value)) -> &'static str {
    let mut usage: Map<String, Value> = Map::new();
    let (mut completed, mut aborted, mut failed) = (false, false, false);
    tracker.consume(&json!({"type": "thread.started", "thread_id": thread}));
    for line in lines {
        let Some(value) = scan::parse(line) else { continue };
        let Some(kind) = json::text(&value, "type") else { continue };
        let Some(payload) = value.get("payload").filter(|p| p.is_object()) else { continue };
        at(&value);
        match (kind, json::text(payload, "type")) {
            ("event_msg", Some("task_started")) => tracker.consume(&json!({"type": "turn.started"})),
            ("event_msg", Some("item_completed")) => {
                if let Some(mapped) = codex_item(payload.get("item")) {
                    tracker.consume(&json!({"type": "item.completed", "item": mapped}));
                }
            }
            ("token_usage_record", _) => {
                if let Some(Value::Object(counts)) = payload.get("usage") {
                    for (name, count) in counts {
                        if let Some(number) = count.as_i64().filter(|n| *n >= 0) {
                            let sum = usage.get(name).and_then(Value::as_i64).unwrap_or(0) + number;
                            usage.insert(name.clone(), json!(sum));
                        }
                    }
                }
            }
            ("event_msg", Some("error")) => {
                if let Some(message) = json::text(payload, "message").filter(|m| !m.is_empty()) {
                    failed = true;
                    tracker.consume(&json!({"type": "error", "message": message}));
                }
            }
            ("event_msg", Some("task_complete")) => completed = true,
            ("event_msg", Some("turn_aborted")) => aborted = true,
            _ => {}
        }
    }
    if completed || !usage.is_empty() {
        tracker.consume(&json!({"type": "turn.completed", "usage": Value::Object(usage)}));
    }
    if aborted {
        return "stopped";
    }
    if completed {
        return "completed";
    }
    if failed { "error" } else { "stopped" }
}

/// An Antigravity transcript step mapped back to the stream-json event agy printed for it (`AntigravityRecords.StreamEvents`) and fed to
/// the tracker; each answer step's text is the request's answer as the live parser hands it on (`tracker.AntigravityAnswer`). Each step's
/// event carries that step's time; the result the last one's.
///
/// Not yet as C# (W5, with Antigravity's live parser and sub-agent watcher): the tool rows the parser makes of each tool step, and the
/// sub-agents' own transcripts read up to the next request (`AntigravitySubagentWatcher`); the sub-agents' blocks are there, from the
/// stream's `subagent` steps, without their work.
fn antigravity_replay(
    turn: &HistoryTurn,
    format: &HistoryFormat,
    tracker: &mut ExecutionGraphTracker,
    child_bytes: &mut i64,
    at: &mut dyn FnMut(DateTimeOffset),
    locale: &Locale,
) -> &'static str {
    let _ = child_bytes;
    let steps: Vec<Value> = turn.lines.iter().filter_map(|l| scan::parse(l)).collect();
    let conversation = format.thread.as_deref().unwrap_or("");
    let (events, status) = antigravity::stream_events(&steps, conversation, locale);
    let mut stamps = 0;
    for value in &events {
        if json::text(value, "event") == Some("step_update")
            && let Some(index) = value.get("step_update").and_then(|s| json::integer(s, "step_index")).filter(|i| *i >= 0)
        {
            while stamps < steps.len() {
                let step = &steps[stamps];
                stamps += 1;
                if let Some(date) = antigravity::date(step) {
                    at(date);
                }
                if json::integer(step, "step_index") == Some(index) {
                    break;
                }
            }
        }
        tracker.consume(value);
        if let Some(update) = value.get("step_update")
            && json::text(update, "step_type") == Some("agent_response")
            && let Some(answer) = json::text(update, "text_delta").map(str::trim_end).filter(|t| !t.is_empty())
        {
            tracker.antigravity_answer(answer);
        }
    }
    status
}
