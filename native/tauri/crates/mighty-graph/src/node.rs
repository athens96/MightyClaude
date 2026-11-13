//! One block of a run's execution graph as the tracker emits it (C# `ExecutionGraphNode`, MightyCode.Core/ExecutionGraph.cs:255), the
//! rules every node goes through (`ExecutionGraphSupport`, ExecutionGraph.cs:318) and what the views read of a block's tokens
//! (`GraphTokenUsage.Parse`, `Compact`, `Summary`, `Detail`, ExecutionGraph.cs:41-74).

use std::collections::HashSet;

use mighty_core::graph::{GraphAdvisor, GraphAgentFigures, GraphAgentIdentity, GraphResponseRecord, GraphTokenUsage};
use mighty_core::locale::Locale;
use mighty_core::model::{AgentActivity, LogEntry};
use mighty_core::normalize::{self, activity_clean};
use mighty_core::{time, wire};
use serde_json::Value;
use sha2::{Digest, Sha256};

/// `ExecutionGraphSupport.MaximumActivityGeneration`.
pub const MAXIMUM_ACTIVITY_GENERATION: i32 = 1_000_000;
/// `ExecutionGraphSupport.MaximumNodes`.
pub const MAXIMUM_NODES: usize = 128;
/// `ExecutionGraphSupport.MaximumEntries`.
pub const MAXIMUM_ENTRIES: usize = 80;
pub const MAXIMUM_INPUT_BYTES: usize = 16 * 1024;
pub const MAXIMUM_OUTPUT_BYTES: usize = 32 * 1024;
pub const MAXIMUM_NODE_BYTES: i64 = 64 * 1024;
/// `ExecutionGraphSupport.Kinds`.
pub const KINDS: [&str; 6] = ["main", "agent", "task", "steer", "compact", "question"];
/// `ExecutionGraphSupport.EntryKinds`.
pub const ENTRY_KINDS: [&str; 6] = ["user", "assistant", "system", "output", "error", "image"];
/// `ActivitySupport.States`.
pub const ACTIVITY_STATES: [&str; 5] = ["running", "waiting", "completed", "error", "stopped"];
/// `MightyStructure.Newlines`: what a one-line path may not hold.
pub const NEWLINES: [char; 7] = ['\n', '\r', '\u{b}', '\u{c}', '\u{85}', '\u{2028}', '\u{2029}'];
/// `ActivitySupport.MaximumSummaryBytes`, `MaximumOutputBytes`.
pub const ACTIVITY_SUMMARY_BYTES: usize = 1000;
pub const ACTIVITY_OUTPUT_BYTES: usize = 8192;

/// A request-scoped node snapshot. A missing input or output means the engine did not report it.
#[derive(Debug, Clone)]
pub struct ExecutionGraphNode {
    pub id: String,
    pub run_id: String,
    pub parent_id: Option<String>,
    pub kind: String,
    pub state: String,
    pub title: String,
    pub input: Option<String>,
    pub output: Option<String>,
    pub entries: Vec<LogEntry>,
    pub updated_at: String,
    pub usage: Option<GraphTokenUsage>,
    pub activity_generation: Option<i32>,
    pub response_records: Option<Vec<GraphResponseRecord>>,
    pub background: Option<bool>,
    pub output_file: Option<String>,
    pub notice_reply: Option<String>,
    pub source_block_id: Option<String>,
    pub identity: Option<GraphAgentIdentity>,
    pub figures: Option<GraphAgentFigures>,
    pub advisor: Option<GraphAdvisor>,
}

impl ExecutionGraphNode {
    /// The tracker's constructor (Swift's memberwise init): updated now.
    pub fn new(id: String, run_id: String, parent_id: Option<String>, kind: &str, state: &str, title: String) -> Self {
        ExecutionGraphNode {
            id,
            run_id,
            parent_id,
            kind: kind.into(),
            state: state.into(),
            title,
            input: None,
            output: None,
            entries: vec![],
            updated_at: time::now_o(),
            usage: None,
            activity_generation: None,
            response_records: None,
            background: None,
            output_file: None,
            notice_reply: None,
            source_block_id: None,
            identity: None,
            figures: None,
            advisor: None,
        }
    }
}

/// C#'s structural `Equals`: every field but `UpdatedAt`.
impl PartialEq for ExecutionGraphNode {
    fn eq(&self, o: &Self) -> bool {
        self.id == o.id
            && self.run_id == o.run_id
            && self.parent_id == o.parent_id
            && self.kind == o.kind
            && self.state == o.state
            && self.title == o.title
            && self.input == o.input
            && self.output == o.output
            && self.usage == o.usage
            && self.activity_generation == o.activity_generation
            && self.background == o.background
            && self.output_file == o.output_file
            && self.notice_reply == o.notice_reply
            && self.source_block_id == o.source_block_id
            && self.identity == o.identity
            && self.figures == o.figures
            && self.advisor == o.advisor
            && self.response_records == o.response_records
            && self.entries == o.entries
    }
}

/// `ExecutionGraphSupport.NormalizedGeneration`.
pub fn normalized_generation(value: Option<i32>) -> Option<i32> {
    value.filter(|n| (0..=MAXIMUM_ACTIVITY_GENERATION).contains(n))
}

/// `ExecutionGraphSupport.MainNodeID`.
pub fn main_node_id(run_id: &str) -> String {
    identifier(run_id, "main")
}

/// `ExecutionGraphSupport.AgentNodeID`.
pub fn agent_node_id(run_id: &str, tool_use_id: &str) -> String {
    identifier(run_id, &format!("tool:{tool_use_id}"))
}

/// `ExecutionGraphSupport.Identifier`: `graph-` and the first 48 hex digits of SHA-256(`<run>|<key>`).
pub fn identifier(run_id: &str, key: &str) -> String {
    let digest = Sha256::digest(format!("{run_id}|{key}").as_bytes());
    let hex: String = digest.iter().map(|b| format!("{b:02x}")).collect();
    format!("graph-{}", &hex[..48])
}

/// `ExecutionGraphSupport.Terminal`.
pub fn terminal(state: &str) -> bool {
    matches!(state, "completed" | "error" | "stopped")
}

/// `ExecutionGraphSupport.Prefix`: the first `count` Unicode scalars, as Swift's `String.prefix` takes them.
pub fn prefix(value: &str, count: usize) -> String {
    value.chars().take(count).collect()
}

fn bytes(value: Option<&str>) -> i64 {
    value.map_or(0, |v| v.len() as i64)
}

/// `ActivitySupport.Clean(text, bytes, singleLine)`.
pub fn clean(text: &str, maximum_bytes: usize, single_line: bool) -> String {
    activity_clean(Some(text), maximum_bytes, single_line)
}

fn activity_bytes(value: &AgentActivity) -> i64 {
    bytes(Some(&value.id))
        + bytes(Some(&value.kind))
        + bytes(Some(&value.state))
        + bytes(Some(&value.provider))
        + bytes(Some(&value.summary))
        + bytes(value.tool_name.as_deref())
        + bytes(value.output.as_deref())
}

/// `ExecutionGraphSupport.Normalized(node, restoring)`: shared by the wire boundary and the saved history; the byte budget includes the
/// prompt, the output and every entry's metadata.
pub fn normalized(node: &ExecutionGraphNode, restoring: bool) -> Option<ExecutionGraphNode> {
    if !wire::identifier(&node.id) || !wire::identifier(&node.run_id) {
        return None;
    }
    if let Some(parent) = &node.parent_id
        && (!wire::identifier(parent) || *parent == node.id)
    {
        return None;
    }
    if !KINDS.contains(&node.kind.as_str()) || !ACTIVITY_STATES.contains(&node.state.as_str()) {
        return None;
    }
    if node.updated_at.len() > 80 || time::parse(Some(&node.updated_at)).is_none() {
        return None;
    }
    let mut result = ExecutionGraphNode {
        parent_id: if node.kind == "main" { None } else { node.parent_id.clone() },
        title: clean(&node.title, 160, true),
        input: node.input.as_deref().map(|i| clean(i, MAXIMUM_INPUT_BYTES, false)),
        output: node.output.as_deref().map(|o| clean(o, MAXIMUM_OUTPUT_BYTES, false)),
        usage: node.usage.and_then(|u| u.normalized()),
        activity_generation: normalized_generation(node.activity_generation),
        background: (node.background == Some(true)).then_some(true),
        output_file: node.output_file.clone().filter(|f| f.len() <= 4096 && !f.contains(NEWLINES)),
        notice_reply: node.notice_reply.as_deref().map(|r| clean(r, MAXIMUM_OUTPUT_BYTES, false)),
        source_block_id: node.source_block_id.clone().filter(|s| wire::identifier(s)),
        // Only a sub-agent block has a type, a model of its own and an isolation; a sub-agent's or a task's its figures; a run's main
        // block its consultations of the advisor (macOS).
        identity: if node.kind == "agent" { node.identity.as_ref().and_then(normalize::identity) } else { None },
        figures: if matches!(node.kind.as_str(), "agent" | "task") { node.figures.as_ref().and_then(normalize::figures) } else { None },
        advisor: if node.kind == "main" { node.advisor.as_ref().and_then(|a| normalize::advisor(a, restoring)) } else { None },
        entries: vec![],
        ..node.clone()
    };
    if restoring && !terminal(&result.state) {
        result.state = "stopped".into();
    }
    if node.kind == "main" {
        return Some(result);
    }
    let mut remaining = MAXIMUM_NODE_BYTES
        - bytes(Some(&result.title))
        - bytes(result.input.as_deref())
        - bytes(result.output.as_deref())
        - bytes(result.output_file.as_deref())
        - bytes(result.notice_reply.as_deref())
        - bytes(result.source_block_id.as_deref())
        - result.identity.as_ref().map_or(0, normalize::identity_bytes);
    let mut seen = HashSet::new();
    let mut entries = vec![];
    let start = node.entries.len().saturating_sub(MAXIMUM_ENTRIES);
    for original in node.entries[start..].iter().rev() {
        let id = original.id();
        if !wire::identifier(id) || !seen.insert(id.to_string()) || !ENTRY_KINDS.contains(&original.kind()) {
            continue;
        }
        if let Some(provider) = &original.provider
            && !wire::PROVIDERS.contains(&provider.as_str())
        {
            continue;
        }
        let timestamp = original.timestamp.as_deref().unwrap_or("");
        if timestamp.len() > 80 || time::parse(Some(timestamp)).is_none() || remaining <= 256 {
            continue;
        }
        let mut entry = LogEntry {
            activity: original.activity.as_ref().and_then(|a| normalize::activity(a, restoring)),
            images: if original.kind() == "image" { normalize::images(original.images.as_ref()) } else { None },
            ..original.clone()
        };
        // An entry past the budget is kept without its activity and with its text clipped, as on the Mac (ExecutionGraph.swift).
        let metadata = bytes(entry.id.as_deref())
            + bytes(entry.timestamp.as_deref())
            + bytes(entry.kind.as_deref())
            + bytes(entry.provider.as_deref())
            + normalize::approximate_bytes(entry.images.as_ref());
        let mut activity = entry.activity.as_ref().map_or(0, activity_bytes);
        if metadata + activity >= remaining {
            entry.activity = None;
            activity = 0;
        }
        let available = (remaining - metadata - activity).max(0) as usize;
        entry.text = Some(clean(entry.text(), MAXIMUM_OUTPUT_BYTES.min(available), false));
        remaining -= metadata + activity + bytes(entry.text.as_deref());
        entries.push(entry);
    }
    entries.reverse();
    result.entries = entries;
    Some(result)
}

// ── tokens ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// `GraphTokenUsage + GraphTokenUsage`.
pub fn add(a: GraphTokenUsage, b: GraphTokenUsage) -> GraphTokenUsage {
    GraphTokenUsage {
        input_tokens: a.input_tokens.wrapping_add(b.input_tokens),
        output_tokens: a.output_tokens.wrapping_add(b.output_tokens),
        cache_read_tokens: a.cache_read_tokens.wrapping_add(b.cache_read_tokens),
        cache_creation_tokens: a.cache_creation_tokens.wrapping_add(b.cache_creation_tokens),
    }
}

/// `GraphTokenUsage - GraphTokenUsage`: each counter at least 0.
pub fn subtract(a: GraphTokenUsage, b: GraphTokenUsage) -> GraphTokenUsage {
    GraphTokenUsage {
        input_tokens: (a.input_tokens - b.input_tokens).max(0),
        output_tokens: (a.output_tokens - b.output_tokens).max(0),
        cache_read_tokens: (a.cache_read_tokens - b.cache_read_tokens).max(0),
        cache_creation_tokens: (a.cache_creation_tokens - b.cache_creation_tokens).max(0),
    }
}

/// `GraphTokenUsage.IsEmpty`.
pub fn is_empty(usage: &GraphTokenUsage) -> bool {
    usage.total() == 0
}

/// `GraphTokenUsage.Parse`: the `usage` object of a CLI stream message. Missing counters are zero; a missing or empty object is no
/// observation at all.
pub fn parse_usage(value: Option<&Value>) -> Option<GraphTokenUsage> {
    let object = value?.as_object()?;
    let count = |key: &str| -> i64 {
        match object.get(key) {
            Some(Value::Number(n)) => match n.as_i64() {
                Some(integer) => integer,
                None => n
                    .as_f64()
                    .filter(|f| f.is_finite() && *f >= 0.0 && *f <= GraphTokenUsage::MAXIMUM_TOKENS as f64)
                    .map_or(0, |f| f as i64),
            },
            _ => 0,
        }
    };
    GraphTokenUsage {
        input_tokens: count("input_tokens"),
        output_tokens: count("output_tokens"),
        cache_read_tokens: count("cache_read_input_tokens"),
        cache_creation_tokens: count("cache_creation_input_tokens"),
    }
    .normalized()
}

/// The usage object `GraphTokenUsage.Parse` reads, from another CLI's names (`source` → `target`; Codex, ACP, pi).
pub fn mapped_usage(value: Option<&Value>, names: &[(&str, &str)]) -> Option<GraphTokenUsage> {
    let object = value?.as_object()?;
    let mut mapped = serde_json::Map::new();
    for (source, target) in names {
        if let Some(number @ Value::Number(_)) = object.get(*source) {
            mapped.insert((*target).into(), number.clone());
        }
    }
    parse_usage(Some(&Value::Object(mapped)))
}

/// `GraphTokenUsage.Compact`: 999, 1.2K, 12K, 1.23M, 12.3M (.NET's `F` formats round the exact value, ties to even, as Rust's do).
pub fn compact(tokens: i64) -> String {
    if tokens < 1_000 {
        return tokens.to_string();
    }
    if tokens < 1_000_000 {
        let value = tokens as f64 / 1_000.0;
        return if tokens < 10_000 { format!("{value:.1}K") } else { format!("{value:.0}K") };
    }
    let value = tokens as f64 / 1_000_000.0;
    if tokens < 10_000_000 { format!("{value:.2}M") } else { format!("{value:.1}M") }
}

/// `GraphTokenUsage.Summary`.
pub fn summary(usage: &GraphTokenUsage, locale: &Locale) -> String {
    locale.get_with("graph.usage.summary", &[("total", &compact(usage.total()))])
}

/// `GraphTokenUsage.Detail`.
pub fn detail(usage: &GraphTokenUsage, locale: &Locale) -> String {
    let mut parts = vec![
        locale.get_with("graph.usage.input", &[("value", &compact(usage.input_tokens))]),
        locale.get_with("graph.usage.output", &[("value", &compact(usage.output_tokens))]),
    ];
    if usage.cache_read_tokens > 0 {
        parts.push(locale.get_with("graph.usage.cacheRead", &[("value", &compact(usage.cache_read_tokens))]));
    }
    if usage.cache_creation_tokens > 0 {
        parts.push(locale.get_with("graph.usage.cacheCreation", &[("value", &compact(usage.cache_creation_tokens))]));
    }
    format!("{} · {}", parts.join(" · "), locale.get_with("graph.usage.total", &[("value", &compact(usage.total()))]))
}

/// A response record as the tracker makes one (every part present).
pub fn response_record(
    response_id: &str,
    model: Option<&str>,
    usage: GraphTokenUsage,
    activity_ids: &[String],
    marked: bool,
) -> GraphResponseRecord {
    GraphResponseRecord {
        response_id: Some(response_id.into()),
        model: model.map(str::to_string),
        usage: Some(usage),
        activity_ids: Some(activity_ids.iter().cloned().map(Some).collect()),
        marked_as_configured: marked,
    }
}

/// A record's activity ids, nulls left out.
pub fn activity_ids(record: &GraphResponseRecord) -> impl Iterator<Item = &str> {
    record.activity_ids.iter().flatten().flatten().map(String::as_str)
}

/// A record's usage, none read as zero.
pub fn record_usage(record: &GraphResponseRecord) -> GraphTokenUsage {
    record.usage.unwrap_or_default()
}

/// A log entry as C# `new LogEntry(id, kind, text, timestamp, provider, activity)` makes one.
pub fn entry(id: String, kind: &str, text: String, timestamp: String, provider: Option<&str>, activity: Option<AgentActivity>) -> LogEntry {
    LogEntry {
        id: Some(id),
        kind: Some(kind.into()),
        text: Some(text),
        timestamp: Some(timestamp),
        provider: provider.map(str::to_string),
        activity,
        images: None,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn compact_matches_dotnet() {
        assert_eq!(compact(999), "999");
        assert_eq!(compact(1_250), "1.2K");
        assert_eq!(compact(1_350), "1.4K");
        assert_eq!(compact(12_500), "12K");
        assert_eq!(compact(1_234_567), "1.23M");
        assert_eq!(compact(12_340_000), "12.3M");
    }

    #[test]
    fn identifiers_are_hashed() {
        let id = main_node_id("run-1");
        assert!(id.starts_with("graph-") && id.len() == 54);
        assert_ne!(id, agent_node_id("run-1", "main"));
    }
}
