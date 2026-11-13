#![allow(dead_code)]

//! What the structure and timeline checks share: the fixture runs of C# `MightyStructureVerification` (Run, Agent, Record, Said, Tool),
//! and a style read from its manifest as the frozen style engine reads one, as far as the structure reads it.

use std::collections::HashMap;
use std::path::{Path, PathBuf};

use mighty_core::graph::{MightyGraphAgent, MightyGraphRun};
use mighty_core::locale::Locale;
use mighty_core::model::{AgentActivity, LogEntry, PlanRecord};
use mighty_graph::structure::{StructureStyle, StylePhase};
use serde_json::Value;

pub const STAMP: &str = "2026-10-12T00:00:00.000Z";

pub fn ko() -> Locale {
    Locale::new("ko")
}

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

fn text(value: &Value, key: &str) -> Option<String> {
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
            id: text(&root, "id").expect("an id"),
            phases: list("phases")
                .iter()
                .map(|p| StylePhase {
                    id: text(p, "id").expect("a phase id"),
                    title: text(p, "title").expect("a phase title"),
                    order: p.get("order").and_then(Value::as_i64).expect("a phase order") as i32,
                })
                .collect(),
            prefixes: recognition
                .get("prefixes")
                .and_then(Value::as_array)
                .map(|l| l.iter().filter_map(Value::as_str).map(str::to_string).collect())
                .unwrap_or_default(),
            lowercase: recognition.get("lowercase").and_then(Value::as_bool).unwrap_or(false),
            actions: list("actions").iter().map(|a| (text(a, "id").expect("an action id"), text(a, "match"), text(a, "phase"))).collect(),
            aliases: list("aliases").iter().map(|a| (text(a, "name"), text(a, "phase"))).collect(),
            kind: text(&rule, "kind").unwrap_or_else(|| "none".into()),
            default: text(&rule, "default"),
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
