//! Finding requests in a record (C# `HistoryFormat`, `HistoryTurn`, `HistoryScan`, SessionHistory.cs:442-708): which line opens a
//! request the user sent, the user's text of a Codex turn, and the walk back from the record's end that places the retained request.

use std::fs::File;
use std::path::PathBuf;
use std::sync::LazyLock;

use mighty_core::json;
use mighty_core::time::{self, DateTimeOffset};
use regex::Regex;
use serde_json::Value;

use crate::backward::JsonlBackwardReader;
use crate::history::{MAXIMUM_TURN_BYTES, SessionHistoryAnchor, SessionHistoryError};
use crate::{Cancel, antigravity, files};

/// `HistoryFormat(Provider, Thread, Brain)`: `thread` is Codex's thread or Antigravity's conversation; `brain` Antigravity's brain
/// folder, which also holds the request's sub-agents' transcripts.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct HistoryFormat {
    pub provider: String,
    pub thread: Option<String>,
    pub brain: Option<PathBuf>,
}

impl HistoryFormat {
    pub fn claude() -> HistoryFormat {
        HistoryFormat { provider: "claude".into(), thread: None, brain: None }
    }
    pub fn codex(thread: &str) -> HistoryFormat {
        HistoryFormat { provider: "codex".into(), thread: Some(thread.into()), brain: None }
    }
    pub fn antigravity(conversation: &str, brain: PathBuf) -> HistoryFormat {
        HistoryFormat { provider: "gemini".into(), thread: Some(conversation.into()), brain: Some(brain) }
    }
}

/// `HistoryTurn`: one request — where it starts, what the user typed, when, its key, its lines, and (`until`) when the next request was
/// sent, when known: work recorded after it belongs to that request.
#[derive(Debug, Clone)]
pub struct HistoryTurn {
    pub start: i64,
    pub prompt: String,
    pub timestamp: Option<String>,
    pub date: Option<DateTimeOffset>,
    pub key: Option<String>,
    pub lines: Vec<Vec<u8>>,
    pub until: Option<DateTimeOffset>,
}

/// `HistoryScan.Opening`: the request a line opens.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Opening {
    pub prompt: Option<String>,
    pub timestamp: Option<String>,
    pub key: Option<String>,
}

/// `HistoryScan.Result`.
#[derive(Debug)]
pub struct ScanResult {
    pub turns: Vec<HistoryTurn>,
    pub end: i64,
    pub reached_start: bool,
}

/// Requests looked at before the retained one must be placed.
pub const MAXIMUM_UNDECIDED: usize = 16;
const INJECTED_TURN_ORIGINS: [&str; 3] = ["peer", "task_notification", "task-notification"];

/// `HistoryScan.Contains`: a raw byte search, so a line can be rejected before it is parsed.
pub fn contains(line: &[u8], pattern: &str) -> bool {
    memchr::memmem::find(line, pattern.as_bytes()).is_some()
}

/// `HistoryScan.Parse`: the line as a JSON object, or None.
pub fn parse(line: &[u8]) -> Option<Value> {
    serde_json::from_slice::<Value>(line).ok().filter(Value::is_object)
}

/// `MetadataJson.Property`: an object's property, or None.
pub fn prop<'a>(value: Option<&'a Value>, key: &str) -> Option<&'a Value> {
    value.and_then(Value::as_object).and_then(|o| o.get(key))
}

/// `JsonElementExtensions.Text` on a property that may be missing.
pub fn text<'a>(value: Option<&'a Value>, key: &str) -> Option<&'a str> {
    value.and_then(|v| json::text(v, key))
}

pub fn flag(value: Option<&Value>, key: &str) -> bool {
    value.is_some_and(|v| json::flag(v, key))
}

/// The text blocks of a content array (`type == "text"`), joined with line breaks.
pub fn text_blocks(blocks: &[Value]) -> String {
    blocks.iter().filter(|b| json::text(b, "type") == Some("text")).filter_map(|b| json::text(b, "text")).collect::<Vec<_>>().join("\n")
}

/// `HistoryScan.Date`.
pub fn date(timestamp: Option<&str>) -> Option<DateTimeOffset> {
    time::parse(timestamp)
}

/// `HistoryScan.OpeningAt`: the opening read forward from `offset` (where a newer request starts), if it is one.
pub fn opening_at(file: &mut File, offset: i64, format: &HistoryFormat) -> Option<Opening> {
    let mut bytes = vec![0u8; 1_048_576];
    let count = files::read_at(file, &mut bytes, offset);
    let newline = memchr::memchr(b'\n', &bytes[..count])?;
    (count > 0).then(|| opening_of(&bytes[..newline], format)).flatten()
}

/// `HistoryScan.OpeningOf`: the request a line opens, cheaply rejected on raw bytes first (a key in raw JSON, `"toolUseResult":`, can
/// never appear inside a string).
pub fn opening_of(line: &[u8], format: &HistoryFormat) -> Option<Opening> {
    if format.provider == "gemini" {
        return antigravity::opening(line).map(|(prompt, timestamp, key)| Opening { prompt: Some(prompt), timestamp, key });
    }
    if format.provider == "claude" {
        if !contains(line, "\"type\":\"user\"") || contains(line, "\"toolUseResult\":") {
            return None;
        }
        let value = parse(line)?;
        let prompt = claude_prompt(&value)?;
        return Some(Opening {
            prompt: Some(prompt),
            timestamp: json::text(&value, "timestamp").map(str::to_string),
            key: json::text(&value, "uuid").map(str::to_string),
        });
    }
    if !contains(line, "\"task_started\"") {
        return None;
    }
    let codex = parse(line)?;
    if json::text(&codex, "type") != Some("event_msg") {
        return None;
    }
    let payload = prop(Some(&codex), "payload");
    (text(payload, "type") == Some("task_started")).then(|| Opening {
        prompt: None,
        timestamp: json::text(&codex, "timestamp").map(str::to_string),
        key: text(payload, "turn_id").map(str::to_string),
    })
}

/// `HistoryScan.HumanOrigin`: no origin (stream input) or one the user typed; never a notification, a peer agent or any other injected
/// kind.
pub fn human_origin(origin: Option<&Value>) -> bool {
    match origin {
        None | Some(Value::Null) => true,
        Some(origin) => json::text(origin, "kind") == Some("human"),
    }
}

/// `HistoryScan.ClaudePrompt`: a request the user sent — not a tool result, an injected notification or peer message, a meta line, a
/// sub-agent line, a compaction summary, an interruption or a local command's printed output. Interactive `claude` stamps what the user
/// typed with a human origin; `-p` stream input has none.
pub fn claude_prompt(value: &Value) -> Option<String> {
    if json::text(value, "type") != Some("user")
        || json::flag(value, "isMeta")
        || json::flag(value, "isSidechain")
        || json::flag(value, "isCompactSummary")
        || !human_origin(prop(Some(value), "origin"))
        || INJECTED_TURN_ORIGINS.contains(&json::text(value, "turnOrigin").unwrap_or(""))
    {
        return None;
    }
    let message = prop(Some(value), "message").filter(|m| m.is_object())?;
    let text = match prop(Some(message), "content")? {
        Value::String(s) => s.clone(),
        Value::Array(blocks) => {
            if blocks.iter().any(|b| json::text(b, "type") == Some("tool_result")) {
                return None;
            }
            text_blocks(blocks)
        }
        _ => return None,
    };
    let trimmed = text.trim();
    if trimmed.is_empty() || trimmed.starts_with("[Request interrupted by user") || trimmed.starts_with("<local-command-") {
        return None;
    }
    // A slash command the user typed is recorded as tags only.
    if trimmed.starts_with("<command-") { command_prompt(trimmed) } else { Some(trimmed.to_string()) }
}

/// `HistoryScan.CommandPrompt`: a slash command recorded as tags, shown as it was typed.
pub fn command_prompt(text: &str) -> Option<String> {
    let tag = |name: &str| -> Option<String> {
        let open = format!("<{name}>");
        let start = text.find(&open)?;
        let from = start + open.len();
        let end = text[from..].find(&format!("</{name}>"))? + from;
        Some(text[from..end].trim().to_string())
    };
    let mut name = tag("command-name").filter(|n| !n.is_empty())?;
    if !name.starts_with('/') {
        name = format!("/{name}");
    }
    let arguments = tag("command-args").unwrap_or_default();
    Some(if arguments.is_empty() { name } else { format!("{name} {arguments}") })
}

static ANSI: LazyLock<Regex> = LazyLock::new(|| Regex::new("\u{1B}\\[[0-9;?]*[A-Za-z]").expect("the ANSI pattern"));

/// `HistoryScan.LocalCommandOutput`: what a local command (`/model`, `/login`) printed, shown as the command's result, never as a
/// request of its own. None for other text.
pub fn local_command_output(text: &str) -> Option<String> {
    let trimmed = text.trim();
    for tag in ["local-command-stdout", "local-command-stderr"] {
        let Some(mut inner) = trimmed.strip_prefix(&format!("<{tag}>")) else { continue };
        if let Some(cut) = inner.strip_suffix(&format!("</{tag}>")) {
            inner = cut;
        }
        return Some(ANSI.replace_all(inner, "").trim().to_string());
    }
    None
}

/// `HistoryScan.CodexPrompt`: the user's text of a Codex turn, one rule for the history and the session list — the `UserMessage` item
/// (0.153 and later), else the `user_message` event (0.147 and earlier), else the first user message item the model saw that is not
/// injected context.
pub fn codex_prompt<L: AsRef<[u8]>>(lines: &[L]) -> Option<String> {
    let (mut evented, mut response) = (None, None);
    for line in lines {
        let line = line.as_ref();
        if contains(line, "\"UserMessage\"")
            && let Some(text) = codex_user_item(line)
        {
            return Some(text);
        }
        if evented.is_none() && contains(line, "\"user_message\"") {
            evented = codex_user_event(line);
        }
        if response.is_none() && contains(line, "\"role\":\"user\"") {
            response = codex_user_response(line);
        }
    }
    evented.or(response)
}

/// Whether a line can carry a Codex turn's user text at all.
pub fn codex_user_line(line: &[u8]) -> bool {
    contains(line, "\"UserMessage\"") || contains(line, "\"user_message\"") || contains(line, "\"role\":\"user\"")
}

fn codex_payload(line: &[u8], kind: &str) -> Option<Value> {
    let value = parse(line)?;
    if json::text(&value, "type") != Some(kind) {
        return None;
    }
    value.get("payload").filter(|p| p.is_object()).cloned()
}

fn non_empty(text: Option<&str>) -> Option<String> {
    text.map(str::trim).filter(|t| !t.is_empty()).map(str::to_string)
}

fn array(value: Option<&Value>) -> &[Value] {
    value.and_then(Value::as_array).map(Vec::as_slice).unwrap_or(&[])
}

pub fn codex_user_item(line: &[u8]) -> Option<String> {
    let payload = codex_payload(line, "event_msg")?;
    if json::text(&payload, "type") != Some("item_completed") {
        return None;
    }
    let item = payload.get("item");
    if text(item, "type") != Some("UserMessage") {
        return None;
    }
    let joined = array(prop(item, "content")).iter().filter_map(|c| json::text(c, "text")).collect::<Vec<_>>().join("\n");
    non_empty(Some(&joined))
}

pub fn codex_user_event(line: &[u8]) -> Option<String> {
    let payload = codex_payload(line, "event_msg")?;
    (json::text(&payload, "type") == Some("user_message")).then(|| non_empty(json::text(&payload, "message"))).flatten()
}

pub fn codex_user_response(line: &[u8]) -> Option<String> {
    let payload = codex_payload(line, "response_item")?;
    if json::text(&payload, "type") != Some("message") || json::text(&payload, "role") != Some("user") {
        return None;
    }
    let joined = array(payload.get("content"))
        .iter()
        .filter(|c| json::text(c, "type") == Some("input_text"))
        .filter_map(|c| json::text(c, "text"))
        .collect::<Vec<_>>()
        .join("\n");
    let text = non_empty(Some(&joined))?;
    (!text.starts_with('<') && !text.starts_with("# AGENTS.md instructions")).then_some(text)
}

/// `HistoryScan.AnchorIndex`: the undecided request (newest first) the retained one is among — the same text before only a containing
/// one, then the closest send time.
pub fn anchor_index(turns: &[HistoryTurn], anchor: &SessionHistoryAnchor) -> Option<usize> {
    let mut best: Option<(usize, i32, f64)> = None;
    for (index, turn) in turns.iter().enumerate() {
        let Some(quality) = anchor.quality(&turn.prompt, turn.date) else { continue };
        let distance = anchor.distance(turn.date);
        if let Some((_, q, d)) = best
            && (quality < q || (quality == q && distance >= d))
        {
            continue;
        }
        best = Some((index, quality, distance));
    }
    best.map(|(index, _, _)| index)
}

/// `HistoryScan.Run`: walks back from the reader's end. With an anchor, requests at or after the retained one are passed over (they
/// are already on screen) and the chunk starts right above it. Stops after `wanted` requests, or once `maximum_bytes` were read and one
/// request was found.
pub fn run(
    reader: &mut JsonlBackwardReader,
    format: &HistoryFormat,
    anchor: Option<&SessionHistoryAnchor>,
    wanted: usize,
    maximum_bytes: i64,
    cancel: &dyn Cancel,
    newer: Option<DateTimeOffset>,
) -> Result<ScanResult, SessionHistoryError> {
    struct Walk<'a> {
        collected: Vec<HistoryTurn>,
        undecided: Vec<HistoryTurn>,
        past: bool,
        end: i64,
        read: i64,
        wanted: usize,
        maximum_bytes: i64,
        anchor: Option<&'a SessionHistoryAnchor>,
    }
    impl Walk<'_> {
        fn take(&mut self, turn: HistoryTurn) -> bool {
            self.end = turn.start;
            self.collected.push(turn);
            self.collected.len() >= self.wanted || self.read >= self.maximum_bytes
        }
        /// Places the retained request among the undecided ones: what is older than it is history. Without a match, what was written
        /// before it was sent is.
        fn place(&mut self) -> bool {
            self.past = true;
            let Some(anchor) = self.anchor else { return false };
            let start = match anchor_index(&self.undecided, anchor) {
                Some(found) => found + 1,
                None => self.undecided.iter().position(|t| anchor.precedes(t.date)).unwrap_or(self.undecided.len()),
            };
            let older: Vec<HistoryTurn> = self.undecided.drain(..).skip(start).collect();
            for turn in older {
                if self.take(turn) {
                    return true;
                }
            }
            false
        }
        fn full(mut self) -> ScanResult {
            self.collected.reverse();
            ScanResult { turns: self.collected, end: self.end, reached_start: false }
        }
    }
    let mut walk =
        Walk { collected: vec![], undecided: vec![], past: anchor.is_none(), end: reader.end, read: 0, wanted, maximum_bytes, anchor };
    let mut pending: Vec<Vec<u8>> = vec![];
    let mut pending_bytes: i64 = 0;
    let mut newer = newer;
    while let Some((offset, line)) = reader.previous() {
        walk.read += line.len() as i64 + 1;
        let Some(opening) = opening_of(&line, format) else {
            // An oversized request keeps the lines nearest its end; a Codex turn also keeps the lines that carry its user text.
            if pending_bytes + line.len() as i64 > MAXIMUM_TURN_BYTES as i64 && !(format.provider == "codex" && codex_user_line(&line)) {
                continue;
            }
            pending_bytes += line.len() as i64;
            pending.push(line);
            continue;
        };
        if cancel.cancelled() {
            return Err(SessionHistoryError::Cancelled);
        }
        pending.reverse();
        let mut lines = std::mem::take(&mut pending);
        pending_bytes = 0;
        if format.provider == "codex" {
            lines.insert(0, line);
        }
        let prompt = opening.prompt.clone().or_else(|| codex_prompt(&lines));
        let at = date(opening.timestamp.as_deref());
        let until = newer;
        newer = at;
        let Some(prompt) = prompt else {
            walk.end = offset;
            continue;
        };
        let turn = HistoryTurn { start: offset, prompt, timestamp: opening.timestamp, date: at, key: opening.key, lines, until };
        if !walk.past
            && let Some(anchor) = anchor
        {
            walk.end = offset;
            if anchor.below(at) {
                if walk.place() || walk.take(turn) {
                    return Ok(walk.full());
                }
                continue;
            }
            let quality = anchor.quality(&turn.prompt, at);
            if anchor.date.is_some() && at.is_some() {
                // Written after the retained request could have been: on screen.
                if anchor.newer(at) {
                    continue;
                }
                walk.undecided.push(turn);
            } else {
                // Without dates only the text tells; until a candidate shows up the requests are the newer, retained ones.
                if quality.is_none() && walk.undecided.is_empty() {
                    continue;
                }
                walk.undecided.push(turn);
                if quality == Some(2) {
                    if walk.place() {
                        return Ok(walk.full());
                    }
                    continue;
                }
            }
            if walk.undecided.len() >= MAXIMUM_UNDECIDED && walk.place() {
                return Ok(walk.full());
            }
            continue;
        }
        if walk.take(turn) {
            return Ok(walk.full());
        }
    }
    if reader.failed {
        return Err(SessionHistoryError::Unreadable);
    }
    if !walk.past && walk.place() {
        return Ok(walk.full());
    }
    // Lines before the first request belong to no request.
    walk.collected.reverse();
    Ok(ScanResult { turns: walk.collected, end: 0, reached_start: true })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn slash_commands_and_local_output_read_as_typed() {
        assert_eq!(
            command_prompt("<command-name>/review</command-name>\n<command-args>main</command-args>").as_deref(),
            Some("/review main")
        );
        assert_eq!(command_prompt("<command-name>model</command-name>").as_deref(), Some("/model"));
        assert_eq!(command_prompt("<command-args>x</command-args>"), None);
        assert_eq!(
            local_command_output("<local-command-stdout>\u{1b}[1mSet model to opus\u{1b}[22m</local-command-stdout>").as_deref(),
            Some("Set model to opus")
        );
        assert_eq!(local_command_output("plain"), None);
    }
}
