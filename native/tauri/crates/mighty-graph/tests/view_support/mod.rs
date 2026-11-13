//! What the views' tests share: the shared fixtures, the C# models' defaults as builders, and the graph vectors' comparator
//! (GraphVectorVerification.Difference: numbers within 0.001, objects with exactly the expected keys).

#![allow(dead_code, reason = "each test file uses part of it")]

use std::path::{Path, PathBuf};

use mighty_core::graph::{GraphAgentFigures, GraphAgentIdentity, MightyGraphAgent, MightyGraphRun};
use mighty_core::locale::Locale;
use mighty_core::model::{AgentActivity, LogEntry};
use serde_json::Value;

pub fn ko() -> Locale {
    Locale::new("ko")
}

/// native/contracts.
pub fn contracts() -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR")).join("../../../contracts")
}

pub fn read_json(path: &Path) -> Value {
    let text = std::fs::read_to_string(path).unwrap_or_else(|e| panic!("{}: {e}", path.display()));
    serde_json::from_str(&text).unwrap_or_else(|e| panic!("{}: {e}", path.display()))
}

/// C# `Check(value, message)`.
#[track_caller]
pub fn check(value: bool, message: impl AsRef<str>) {
    assert!(value, "{}", message.as_ref());
}

/// C# `new MightyGraphRun { Id, Input, Status }` with every list empty.
pub fn run(id: &str, input: &str, status: &str) -> MightyGraphRun {
    MightyGraphRun {
        id: Some(id.into()),
        input: Some(input.into()),
        status: Some(status.into()),
        root_entries: Some(vec![]),
        agents: Some(vec![]),
        result_entries: Some(vec![]),
        ..MightyGraphRun::default()
    }
}

/// C# `new MightyGraphAgent { Id, Title, Input, Status }`.
pub fn agent(id: &str, title: &str, input: &str, status: &str) -> MightyGraphAgent {
    MightyGraphAgent {
        id: Some(id.into()),
        title: Some(title.into()),
        input: Some(input.into()),
        status: Some(status.into()),
        entries: Some(vec![]),
        ..MightyGraphAgent::default()
    }
}

pub fn with_agents(mut run: MightyGraphRun, agents: Vec<MightyGraphAgent>) -> MightyGraphRun {
    run.agents = Some(agents);
    run
}

pub fn with_final(mut run: MightyGraphRun, output: &str) -> MightyGraphRun {
    run.final_output = Some(output.into());
    run
}

pub fn parent(mut agent: MightyGraphAgent, parent: &str) -> MightyGraphAgent {
    agent.parent_id = Some(parent.into());
    agent
}

pub fn kind(mut agent: MightyGraphAgent, kind: &str) -> MightyGraphAgent {
    agent.kind = Some(kind.into());
    agent
}

pub fn background(mut agent: MightyGraphAgent) -> MightyGraphAgent {
    agent.background = Some(true);
    agent
}

pub fn identity(kind: Option<&str>, requested: Option<&str>, resolved: Option<&str>, isolation: Option<&str>) -> GraphAgentIdentity {
    GraphAgentIdentity {
        kind: kind.map(str::to_string),
        requested_model: requested.map(str::to_string),
        resolved_model: resolved.map(str::to_string),
        isolation: isolation.map(str::to_string),
    }
}

pub fn figures(started: Option<&str>, ended: Option<&str>, tools: Option<i32>, exit: Option<i32>) -> GraphAgentFigures {
    GraphAgentFigures {
        started_at: started.map(str::to_string),
        ended_at: ended.map(str::to_string),
        tool_calls: tools,
        exit_code: exit,
    }
}

/// C# `new LogEntry(id, kind, text, timestamp, provider, activity)`.
pub fn entry(id: &str, kind: &str, text: &str, timestamp: &str, provider: Option<&str>) -> LogEntry {
    LogEntry::new(id.into(), kind, text.into(), timestamp.into(), provider.map(str::to_string))
}

pub fn with_activity(mut entry: LogEntry, activity: AgentActivity) -> LogEntry {
    entry.activity = Some(activity);
    entry
}

pub fn activity(id: &str, provider: &str, kind: &str, state: &str, summary: &str, tool: Option<&str>) -> AgentActivity {
    AgentActivity {
        id: id.into(),
        provider: provider.into(),
        kind: kind.into(),
        state: state.into(),
        summary: summary.into(),
        tool_name: tool.map(str::to_string),
        output: None,
        duration_ms: None,
    }
}

/// GraphVectorVerification.Difference: the first place `produced` differs from the committed expectation.
pub fn difference(expected: &Value, produced: &Value, path: &str) -> Option<String> {
    match expected {
        Value::Null => (!produced.is_null()).then(|| format!("{path}: expected null, produced {produced}")),
        Value::Bool(flag) => (produced.as_bool() != Some(*flag)).then(|| format!("{path}: expected {flag}, produced {produced}")),
        Value::Number(number) => match produced.as_f64() {
            Some(value) if (number.as_f64().unwrap_or(f64::NAN) - value).abs() <= 0.001 => None,
            _ => Some(format!("{path}: expected {number}, produced {produced}")),
        },
        Value::String(text) => (produced.as_str() != Some(text)).then(|| format!("{path}: expected {text:?}, produced {produced}")),
        Value::Array(items) => {
            let Some(list) = produced.as_array() else { return Some(format!("{path}: expected an array, produced {produced}")) };
            if list.len() != items.len() {
                return Some(format!("{path}: expected {} items, produced {}", items.len(), list.len()));
            }
            items.iter().zip(list).enumerate().find_map(|(i, (e, p))| difference(e, p, &format!("{path}[{i}]")))
        }
        Value::Object(map) => {
            let Some(made) = produced.as_object() else { return Some(format!("{path}: expected an object, produced {produced}")) };
            let mut keys: Vec<&String> = map.keys().collect();
            let mut have: Vec<&String> = made.keys().collect();
            keys.sort();
            have.sort();
            if keys != have {
                return Some(format!("{path}: expected keys {keys:?}, produced {have:?}"));
            }
            map.iter().find_map(|(k, v)| difference(v, &made[k], &format!("{path}.{k}")))
        }
    }
}
