//! What the graph tests share: the fixtures where they live (native/contracts), the comparison every shared vector uses (C#
//! GraphVectorVerification.Difference: keys exactly, numbers within 0.001) and the readers of a case's runs, entries and records; and
//! what the structure and timeline checks share: the fixture runs of C# `MightyStructureVerification` (Run, Agent, Record, Said, Tool),
//! and a style read from its manifest as the frozen style engine reads one, as far as the structure reads it.
#![allow(dead_code)]

use std::collections::HashMap;
use std::path::{Path, PathBuf};

use mighty_core::graph::{GraphResponseRecord, GraphTokenUsage, MightyGraphAgent, MightyGraphRun};
use mighty_core::locale::Locale;
use mighty_core::model::{AgentActivity, LogEntry, PlanRecord};
use mighty_graph::ExecutionGraphNode;
use mighty_graph::structure::{StructureStyle, StylePhase};
use serde_json::{Map, Value, json};

/// native/contracts.
pub fn contracts() -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR")).join("../../../contracts")
}

pub fn read_json(path: &Path) -> Value {
    let text = std::fs::read_to_string(path).unwrap_or_else(|e| panic!("{}: {e}", path.display()));
    serde_json::from_str(&text).unwrap_or_else(|e| panic!("{}: {e}", path.display()))
}

pub fn fixture(name: &str) -> Value {
    read_json(&contracts().join("fixtures").join(name))
}

pub fn fixture_text(name: &str) -> String {
    std::fs::read_to_string(contracts().join("fixtures").join(name)).unwrap_or_else(|e| panic!("{name}: {e}"))
}

pub fn ko() -> Locale {
    Locale::new("ko")
}

/// `GraphVectorVerification.Difference`: the first way `produced` differs from `expected`, keys compared exactly and numbers within 0.001.
pub fn difference(expected: &Value, produced: &Value, path: &str) -> Option<String> {
    match expected {
        Value::Null => (!produced.is_null()).then(|| format!("{path}: expected null, produced {}", show(produced))),
        Value::Bool(flag) => (produced.as_bool() != Some(*flag)).then(|| format!("{path}: expected {flag}, produced {}", show(produced))),
        Value::Number(n) => match produced.as_f64() {
            Some(p) if (n.as_f64().unwrap_or(f64::NAN) - p).abs() <= 0.001 => None,
            _ => Some(format!("{path}: expected {n}, produced {}", show(produced))),
        },
        Value::String(s) => (produced.as_str() != Some(s)).then(|| format!("{path}: expected {s:?}, produced {}", show(produced))),
        Value::Array(items) => {
            let Some(list) = produced.as_array() else { return Some(format!("{path}: expected an array, produced {}", show(produced))) };
            if list.len() != items.len() {
                return Some(format!("{path}: expected {} items, produced {}", items.len(), list.len()));
            }
            items.iter().zip(list).enumerate().find_map(|(i, (e, p))| difference(e, p, &format!("{path}[{i}]")))
        }
        Value::Object(map) => {
            let Some(other) = produced.as_object() else { return Some(format!("{path}: expected an object, produced {}", show(produced))) };
            let mut keys: Vec<&String> = map.keys().collect();
            let mut produced_keys: Vec<&String> = other.keys().collect();
            keys.sort();
            produced_keys.sort();
            if keys != produced_keys {
                return Some(format!("{path}: expected keys {keys:?}, produced {produced_keys:?}"));
            }
            map.iter().find_map(|(k, v)| difference(v, &other[k], &format!("{path}.{k}")))
        }
    }
}

fn show(value: &Value) -> String {
    match value {
        Value::Object(map) => format!("{{{}}}", map.keys().cloned().collect::<Vec<_>>().join(",")),
        Value::Array(items) => format!("[{} items]", items.len()),
        other => other.to_string(),
    }
}

/// Runs every case of a group against the committed expectation (C# `RunGroup`).
pub fn run_group(document: &Value, group: &str, minimum: usize, produce: impl Fn(&Value) -> Value) {
    assert_eq!(document["version"], json!(1), "graph-vectors.json version must be 1");
    let cases = document[group].as_array().unwrap_or_else(|| panic!("missing group {group}"));
    let mut names = std::collections::HashSet::new();
    let mut mismatches = vec![];
    for case in cases {
        let name = case["name"].as_str().unwrap_or_else(|| panic!("a {group} case has no name"));
        assert!(names.insert(name), "duplicate {group} case name {name}");
        let expected = case.get("expected").unwrap_or_else(|| panic!("{group}/{name} has no expected value"));
        if let Some(reason) = difference(expected, &produce(case), &format!("{group}/{name}")) {
            mismatches.push(reason);
        }
    }
    assert!(cases.len() >= minimum, "group {group} has {} cases, needs {minimum}", cases.len());
    assert!(mismatches.is_empty(), "Rust does not reproduce macOS:\n{}", mismatches.join("\n"));
}

// ── JSON shapes ──────────────────────────────────────────────────────────────────────────────────────────────────────────────

pub fn usage_json(usage: Option<GraphTokenUsage>) -> Value {
    match usage {
        None => Value::Null,
        Some(u) => {
            json!({"inputTokens": u.input_tokens, "outputTokens": u.output_tokens, "cacheReadTokens": u.cache_read_tokens, "cacheCreationTokens": u.cache_creation_tokens})
        }
    }
}

pub fn record_json(record: &GraphResponseRecord) -> Value {
    json!({
        "responseId": record.response_id,
        "model": record.model,
        "usage": usage_json(Some(record.usage.unwrap_or_default())),
        "activityIds": record.activity_ids.iter().flatten().cloned().collect::<Vec<_>>(),
        "markedAsConfigured": record.marked_as_configured,
    })
}

/// Timestamps are wall-clock and never part of an expectation.
pub fn entry_json(entry: &LogEntry) -> Value {
    json!({"id": entry.id, "kind": entry.kind, "text": entry.text, "provider": entry.provider})
}

pub fn node_json(node: &ExecutionGraphNode) -> Value {
    json!({
        "id": node.id, "runId": node.run_id, "parentId": node.parent_id, "kind": node.kind, "state": node.state, "title": node.title,
        "input": node.input, "output": node.output, "usage": usage_json(node.usage), "activityGeneration": node.activity_generation,
        "responseRecords": node.response_records.as_ref().map(|r| r.iter().map(record_json).collect::<Vec<_>>()),
        "entries": node.entries.iter().map(entry_json).collect::<Vec<_>>(),
    })
}

fn records_json(records: &Option<Vec<Option<GraphResponseRecord>>>) -> Value {
    match records {
        None => Value::Null,
        Some(list) => Value::Array(list.iter().flatten().map(record_json).collect()),
    }
}

pub fn agent_json(agent: &MightyGraphAgent) -> Value {
    json!({
        "id": agent.id, "parentID": agent.parent_id, "title": agent.title, "input": agent.input, "status": agent.status, "kind": agent.kind,
        "usage": usage_json(agent.usage), "activityGeneration": agent.activity_generation, "responseRecords": records_json(&agent.response_records),
        "entries": agent.entries().iter().map(entry_json).collect::<Vec<_>>(),
    })
}

pub fn run_json(run: &MightyGraphRun) -> Value {
    json!({
        "id": run.id, "input": run.input, "status": run.status, "provider": run.provider, "finalOutput": run.final_output,
        "sourceRunID": run.source_run_id, "usage": usage_json(run.usage), "nodeModelLabel": run.node_model_label,
        "configuredModel": run.configured_model, "responseRecords": records_json(&run.response_records),
        "rootEntries": run.root_entries().iter().map(entry_json).collect::<Vec<_>>(),
        "resultEntries": run.result_entries().iter().map(entry_json).collect::<Vec<_>>(),
        "agents": run.agents().iter().map(agent_json).collect::<Vec<_>>(),
    })
}

// ── reading a case's inputs ──────────────────────────────────────────────────────────────────────────────────────────────────

/// Entries fed in as input carry this fixed timestamp so nothing in an expectation depends on the clock.
pub const FIXED_TIMESTAMP: &str = "2026-01-01T00:00:00Z";

pub fn text<'a>(value: &'a Value, key: &str) -> Option<&'a str> {
    value.get(key).and_then(Value::as_str)
}

pub fn flag(value: &Value, key: &str) -> bool {
    value.get(key) == Some(&Value::Bool(true))
}

pub fn strings(value: Option<&Value>) -> Vec<String> {
    value.and_then(Value::as_array).map(|a| a.iter().filter_map(|v| v.as_str().map(str::to_string)).collect()).unwrap_or_default()
}

pub fn items(value: Option<&Value>) -> Vec<Value> {
    value.and_then(Value::as_array).cloned().unwrap_or_default()
}

fn long(value: &Value, key: &str) -> i64 {
    value.get(key).and_then(Value::as_i64).unwrap_or(0)
}

pub fn usage_in(value: Option<&Value>) -> Option<GraphTokenUsage> {
    let value = value.filter(|v| v.is_object())?;
    Some(GraphTokenUsage {
        input_tokens: long(value, "inputTokens"),
        output_tokens: long(value, "outputTokens"),
        cache_read_tokens: long(value, "cacheReadTokens"),
        cache_creation_tokens: long(value, "cacheCreationTokens"),
    })
}

pub fn record_in(value: &Value) -> GraphResponseRecord {
    GraphResponseRecord {
        response_id: Some(text(value, "responseId").unwrap_or("").into()),
        model: text(value, "model").map(Into::into),
        usage: Some(usage_in(value.get("usage")).unwrap_or_default()),
        activity_ids: Some(strings(value.get("activityIds")).into_iter().map(Some).collect()),
        marked_as_configured: flag(value, "markedAsConfigured"),
    }
}

pub fn records_in(value: Option<&Value>) -> Option<Vec<Option<GraphResponseRecord>>> {
    value.filter(|v| v.is_array()).map(|v| items(Some(v)).iter().map(|r| Some(record_in(r))).collect())
}

pub fn entry_in(value: &Value) -> LogEntry {
    LogEntry {
        id: Some(text(value, "id").unwrap_or("").into()),
        kind: Some(text(value, "kind").unwrap_or("system").into()),
        text: Some(text(value, "text").unwrap_or("").into()),
        timestamp: Some(text(value, "timestamp").unwrap_or(FIXED_TIMESTAMP).into()),
        provider: text(value, "provider").map(Into::into),
        activity: None,
        images: None,
    }
}

pub fn agent_in(value: &Value, locale: &Locale) -> MightyGraphAgent {
    MightyGraphAgent {
        id: Some(text(value, "id").unwrap_or("").into()),
        parent_id: text(value, "parentID").map(Into::into),
        title: Some(text(value, "title").map(str::to_string).unwrap_or_else(|| locale.get("graph.block.agent"))),
        input: Some(text(value, "input").unwrap_or("").into()),
        status: Some(text(value, "status").unwrap_or("running").into()),
        entries: Some(items(value.get("entries")).iter().map(entry_in).collect()),
        kind: text(value, "kind").map(Into::into),
        usage: usage_in(value.get("usage")),
        activity_generation: value.get("activityGeneration").and_then(Value::as_i64).map(|g| g as i32),
        response_records: records_in(value.get("responseRecords")),
        notice_reply: text(value, "noticeReply").map(Into::into),
        ..MightyGraphAgent::default()
    }
}

pub fn run_in(value: &Value, locale: &Locale) -> MightyGraphRun {
    MightyGraphRun {
        id: Some(text(value, "id").unwrap_or("").into()),
        input: Some(text(value, "input").unwrap_or("").into()),
        status: Some(text(value, "status").unwrap_or("running").into()),
        root_entries: Some(items(value.get("rootEntries")).iter().map(entry_in).collect()),
        agents: Some(items(value.get("agents")).iter().map(|a| agent_in(a, locale)).collect()),
        result_entries: Some(vec![]),
        source_run_id: text(value, "sourceRunID").map(Into::into),
        final_output: text(value, "finalOutput").map(Into::into),
        usage: usage_in(value.get("usage")),
        response_records: records_in(value.get("responseRecords")),
        provider: text(value, "provider").map(Into::into),
        node_model_label: text(value, "nodeModelLabel").map(Into::into),
        configured_model: text(value, "configuredModel").map(Into::into),
        ..MightyGraphRun::default()
    }
}

pub fn object(fields: &[(&str, Value)]) -> Value {
    Value::Object(fields.iter().map(|(k, v)| (k.to_string(), v.clone())).collect::<Map<_, _>>())
}

// ── the structure's and the timeline's fixture runs and styles ──────────────────────────────────────────────────────────────

pub const STAMP: &str = "2026-10-12T00:00:00.000Z";

/// The repository root (this crate is native/tauri/crates/mighty-graph).
pub fn repo() -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR")).join("../../../..").canonicalize().expect("repository root")
}

/// `MightyStructureVerification.Run`: a Claude run with the request "do <id>", refreshed as the app refreshes it.
pub fn run(
    id: &str,
    input: Option<&str>,
    status: &str,
    agents: Vec<MightyGraphAgent>,
    final_output: Option<&str>,
    source: Option<&str>,
) -> MightyGraphRun {
    let mut run = MightyGraphRun {
        id: Some(id.into()),
        input: Some(input.map_or_else(|| format!("do {id}"), str::to_string)),
        status: Some(status.into()),
        root_entries: Some(vec![]),
        result_entries: Some(vec![]),
        agents: Some(agents),
        source_run_id: source.map(str::to_string),
        final_output: final_output.map(str::to_string),
        provider: Some("claude".into()),
        ..MightyGraphRun::default()
    };
    mighty_core::normalize::refresh_result(&mut run);
    run
}

/// `Run(id)` with every default.
pub fn done(id: &str) -> MightyGraphRun {
    run(id, None, "completed", vec![], None, None)
}

/// `Run(id, input)`.
pub fn asked(id: &str, input: &str) -> MightyGraphRun {
    run(id, Some(input), "completed", vec![], None, None)
}

/// `Run(id, status: status)`.
pub fn status(id: &str, status: &str) -> MightyGraphRun {
    run(id, None, status, vec![], None, None)
}

/// `MightyStructureVerification.Agent`.
pub fn agent(id: &str, parent: Option<&str>, status: &str, kind: Option<&str>) -> MightyGraphAgent {
    MightyGraphAgent {
        id: Some(id.into()),
        parent_id: parent.map(str::to_string),
        title: Some(format!("agent {id}")),
        input: Some(String::new()),
        status: Some(status.into()),
        entries: Some(vec![]),
        kind: kind.map(str::to_string),
        ..MightyGraphAgent::default()
    }
}

/// `Agent(id)`.
pub fn sub(id: &str) -> MightyGraphAgent {
    agent(id, None, "completed", None)
}

/// A bare `new MightyGraphAgent { … }` (title and input empty, running).
pub fn block(id: &str) -> MightyGraphAgent {
    MightyGraphAgent {
        id: Some(id.into()),
        title: Some(String::new()),
        input: Some(String::new()),
        status: Some("running".into()),
        entries: Some(vec![]),
        ..MightyGraphAgent::default()
    }
}

/// `MightyStructureVerification.Record`.
pub fn record(id: &str, graph_run_id: &str, outcome: &str) -> PlanRecord {
    PlanRecord {
        id: Some(id.into()),
        run_id: Some(format!("channel-{id}")),
        plan: Some("# plan".into()),
        received_at: Some(STAMP.into()),
        decided_at: Some("2026-10-12T00:01:00.000Z".into()),
        outcome: Some(outcome.into()),
        feedback: None,
        plan_truncated: None,
        graph_run_id: Some(graph_run_id.into()),
        launch_override: None,
    }
}

/// A log entry as C# `new LogEntry(id, kind, text, timestamp)` makes one.
pub fn entry(id: &str, kind: &str, text: &str, timestamp: &str) -> LogEntry {
    LogEntry::new(id.into(), kind, text.into(), timestamp.into(), None)
}

/// `MightyStructureVerification.Said`.
pub fn said(id: &str, text: &str) -> LogEntry {
    entry(id, "assistant", text, STAMP)
}

/// An activity as C# `new AgentActivity(id, provider, kind, state, summary, toolName)` makes one.
pub fn activity(id: &str, kind: &str, state: &str, summary: &str, tool: Option<&str>) -> AgentActivity {
    AgentActivity {
        id: id.into(),
        provider: "claude".into(),
        kind: kind.into(),
        state: state.into(),
        summary: summary.into(),
        tool_name: tool.map(str::to_string),
        output: None,
        duration_ms: None,
    }
}

/// `MightyStructureVerification.Tool`.
pub fn tool(id: &str, summary: &str, kind: &str, state: &str) -> LogEntry {
    LogEntry { activity: Some(activity(&format!("act-{id}"), kind, state, summary, None)), ..entry(id, "system", summary, STAMP) }
}

/// `Tool(id, summary)`.
pub fn read(id: &str, summary: &str) -> LogEntry {
    tool(id, summary, "read", "completed")
}

/// A style read from its manifest (C# `StyleManifest` and `StyleEvaluator`), as far as the structure reads it: its phases, its phase
/// rule, its recognition, actions and aliases.
pub struct ManifestStyle {
    id: String,
    phases: Vec<StylePhase>,
    prefixes: Vec<String>,
    lowercase: bool,
    actions: Vec<(String, Option<String>, Option<String>)>,
    aliases: Vec<(Option<String>, Option<String>)>,
    kind: String,
    default: Option<String>,
    map: HashMap<String, String>,
}

fn manifest_text(value: &Value, key: &str) -> Option<String> {
    value.get(key).and_then(Value::as_str).map(str::to_string)
}

impl ManifestStyle {
    pub fn load(path: &Path) -> ManifestStyle {
        let raw = std::fs::read_to_string(path).unwrap_or_else(|e| panic!("{}: {e}", path.display()));
        let root: Value = serde_json::from_str(&raw).expect("a style manifest");
        let list = |key: &str| root.get(key).and_then(Value::as_array).cloned().unwrap_or_default();
        let rule = root.get("rules").and_then(|r| r.get("phase")).cloned().unwrap_or(Value::Null);
        let recognition = root.get("recognition").cloned().unwrap_or(Value::Null);
        ManifestStyle {
            id: manifest_text(&root, "id").expect("an id"),
            phases: list("phases")
                .iter()
                .map(|p| StylePhase {
                    id: manifest_text(p, "id").expect("a phase id"),
                    title: manifest_text(p, "title").expect("a phase title"),
                    order: p.get("order").and_then(Value::as_i64).expect("a phase order") as i32,
                })
                .collect(),
            prefixes: recognition
                .get("prefixes")
                .and_then(Value::as_array)
                .map(|l| l.iter().filter_map(Value::as_str).map(str::to_string).collect())
                .unwrap_or_default(),
            lowercase: recognition.get("lowercase").and_then(Value::as_bool).unwrap_or(false),
            actions: list("actions").iter().map(|a| (manifest_text(a, "id").expect("an action id"), manifest_text(a, "match"), manifest_text(a, "phase"))).collect(),
            aliases: list("aliases").iter().map(|a| (manifest_text(a, "name"), manifest_text(a, "phase"))).collect(),
            kind: manifest_text(&rule, "kind").unwrap_or_else(|| "none".into()),
            default: manifest_text(&rule, "default"),
            map: rule
                .get("map")
                .and_then(Value::as_object)
                .map(|m| m.iter().filter_map(|(k, v)| v.as_str().map(|v| (k.clone(), v.to_string()))).collect())
                .unwrap_or_default(),
        }
    }

    /// `StyleEvaluator.RecognisedName`.
    fn recognised_name(&self, prompt: &str) -> Option<String> {
        let value = prompt.trim();
        let prefix = self.prefixes.iter().find(|p| value.starts_with(p.as_str()))?;
        let name: String = value[prefix.len()..].chars().take_while(|c| !c.is_whitespace()).collect();
        if name.is_empty() {
            return None;
        }
        Some(if self.lowercase { name.to_lowercase() } else { name })
    }
}

impl StructureStyle for ManifestStyle {
    fn id(&self) -> &str {
        &self.id
    }
    fn phases(&self) -> &[StylePhase] {
        &self.phases
    }
    fn plan_state(&self) -> bool {
        self.kind == "planState"
    }
    fn current_phase(&self, plan_stage: Option<&str>) -> Option<&StylePhase> {
        match self.kind.as_str() {
            "none" => None,
            "planState" => {
                let id = self.map.get(plan_stage.unwrap_or("planning"))?;
                self.phases.iter().find(|p| &p.id == id)
            }
            _ => self.phases.iter().find(|p| Some(&p.id) == self.default.as_ref()),
        }
    }
    fn prompt_phase(&self, prompt: &str) -> Option<String> {
        let name = self.recognised_name(prompt);
        let action = self
            .actions
            .iter()
            .find(|(id, _, _)| Some(id) == name.as_ref())
            .or_else(|| self.actions.iter().find(|(_, matched, _)| matched.is_some() && *matched == name));
        match action {
            Some((_, _, phase)) => phase.clone(),
            None => self.aliases.iter().find(|(alias, _)| *alias == name).and_then(|(_, phase)| phase.clone()),
        }
    }
}

/// A bundled style (`native/macos/Sources/MightyCore/Resources/Styles/<id>.json`), as `MightyStructureVerification.Style` reads it.
pub fn style(id: &str) -> ManifestStyle {
    ManifestStyle::load(&repo().join("native/macos/Sources/MightyCore/Resources/Styles").join(format!("{id}.json")))
}

/// A test-only style of `styles/fixtures/` (C# `StyleFixtures`): `loop` (four phases), `deck` (no phases).
pub fn fixture_style(id: &str) -> ManifestStyle {
    ManifestStyle::load(&repo().join("styles/fixtures").join(format!("{id}.json")))
}

/// `MightyStructureVerification.Phase`.
pub fn phase<'a>(style: &'a ManifestStyle, id: &str) -> &'a StylePhase {
    style.phases.iter().find(|p| p.id == id).expect("a phase of the style")
}
