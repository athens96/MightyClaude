//! The saved profile carries the graph under the macOS field names (C# GraphVectorVerification.SessionFields): a snapshot without them loads
//! unchanged, and a restart restores running blocks as stopped. C# writes the file and loads it from disk; here the same bytes go through
//! mighty-core's `read_state`, the load without the disk (the file's own writing and swapping is `store`'s, tested there).

mod common;

use std::collections::HashSet;
use std::sync::Arc;

use common::*;
use mighty_core::graph::{GraphResponseRecord, GraphTokenUsage, MightyGraphAgent, MightyGraphRun};
use mighty_core::json::Wire;
use mighty_core::locale::Locale;
use mighty_core::model::{AppSnapshot, LogEntry, RunSession, Workspace};
use mighty_core::{normalize, store};
use serde_json::Value;

fn usage(input: i64, output: i64) -> Option<GraphTokenUsage> {
    Some(GraphTokenUsage { input_tokens: input, output_tokens: output, ..GraphTokenUsage::default() })
}

fn record(id: &str, model: &str, input: i64, output: i64, tool: &str) -> Option<GraphResponseRecord> {
    Some(GraphResponseRecord {
        response_id: Some(id.into()),
        model: Some(model.into()),
        usage: usage(input, output),
        activity_ids: Some(vec![Some(tool.into())]),
        marked_as_configured: false,
    })
}

fn names(value: &Value) -> HashSet<String> {
    value.as_object().map(|o| o.keys().cloned().collect()).unwrap_or_default()
}

fn load(text: &str) -> AppSnapshot {
    store::read_state(text.as_bytes(), &Locale::new("ko")).unwrap_or_else(|e| panic!("the state loads: {e:?}"))
}

#[test]
fn graph_session_fields_share_macos_names() {
    let ko = Locale::new("ko");
    let workspace = Workspace { path: Some(std::env::temp_dir().to_string_lossy().into_owned()), ..Workspace::default() };
    let run = MightyGraphRun {
        id: Some("request-one".into()),
        input: Some("Refactor".into()),
        status: Some("running".into()),
        provider: Some("claude".into()),
        configured_model: Some("claude-opus-5".into()),
        node_model_label: Some(format!("claude-opus-5 {}", ko.get("graph.nodeModel.configuredSuffix"))),
        source_run_id: Some("process-one".into()),
        usage: usage(100, 20),
        final_output: Some("partial".into()),
        response_records: Some(vec![record("m1", "claude-opus-5", 100, 20, "tool-1")]),
        root_entries: Some(vec![]),
        result_entries: Some(vec![]),
        agents: Some(vec![
            MightyGraphAgent {
                id: Some("graph-child".into()),
                title: Some("Reader".into()),
                input: Some("Read".into()),
                status: Some("running".into()),
                entries: Some(vec![]),
                usage: usage(10, 2),
                activity_generation: Some(1),
                ..MightyGraphAgent::default()
            },
            // Every optional field populated, so the save names them all.
            MightyGraphAgent {
                id: Some("graph-grandchild".into()),
                parent_id: Some("graph-child".into()),
                title: Some("Build".into()),
                input: Some("npm run build".into()),
                status: Some("running".into()),
                kind: Some("task".into()),
                usage: usage(5, 1),
                activity_generation: Some(2),
                response_records: Some(vec![record("m2", "claude-opus-5", 5, 1, "tool-2")]),
                entries: Some(vec![LogEntry::new(
                    "graph-grandchild-launch".into(),
                    "system",
                    "started".into(),
                    "2026-01-01T00:00:00Z".into(),
                    Some("claude".into()),
                )]),
                ..MightyGraphAgent::default()
            },
        ]),
        ..MightyGraphRun::default()
    };
    let session = RunSession {
        workspace_id: workspace.id.clone(),
        provider: Some("claude".into()),
        agent_view_mode: Some("mighty".into()),
        graph_runs: Some(vec![run]),
        ..RunSession::default()
    };
    let snapshot = AppSnapshot {
        version: 1,
        workspaces: Some(vec![workspace.clone()]),
        sessions: Some(vec![Arc::new(session)]),
        ..AppSnapshot::default()
    };
    let encoded = snapshot.to_json();
    let document: Value = serde_json::from_str(&encoded).expect("json");
    let saved = &document["sessions"][0];
    check(document["version"].as_i64() == Some(1), "AppSnapshot version must stay 1");
    check(text(saved, "agentViewMode") == Some("mighty"), "RunSession must serialize agentViewMode");
    let runs = saved.get("graphRuns").and_then(Value::as_array);
    check(runs.is_some_and(|r| r.len() == 1), "RunSession must serialize graphRuns");
    check(saved.get("graphBlockSizes").is_none(), "graphBlockSizes is per-block resize, which is out of scope");
    check(saved.get("graphResultSize").is_none(), "graphResultSize is written only once the newest result is dragged");
    let run_json = &runs.expect("runs")[0];
    let save_names = names(run_json);
    for name in [
        "id",
        "input",
        "status",
        "rootEntries",
        "agents",
        "resultEntries",
        "sourceRunID",
        "finalOutput",
        "usage",
        "responseRecords",
        "provider",
        "nodeModelLabel",
        "configuredModel",
    ] {
        check(save_names.contains(name), &format!("graphRuns must use the macOS MightyGraphRun name {name}"));
    }
    let agent_names = names(&run_json["agents"][1]);
    for name in ["id", "parentID", "title", "input", "status", "entries", "kind", "usage", "activityGeneration", "responseRecords"] {
        check(agent_names.contains(name), &format!("graphRuns agents must use the macOS MightyGraphAgent name {name}"));
    }
    let usage_names = names(&run_json["usage"]);
    for name in ["inputTokens", "outputTokens", "cacheReadTokens", "cacheCreationTokens"] {
        check(usage_names.contains(name), &format!("usage must use the macOS GraphTokenUsage name {name}"));
    }
    let record_names = names(&run_json["responseRecords"][0]);
    for name in ["responseId", "model", "usage", "activityIds", "markedAsConfigured"] {
        check(record_names.contains(name), &format!("responseRecords must use the macOS GraphResponseRecord name {name}"));
    }

    let loaded = load(&encoded);
    let restored = &loaded.sessions()[0];
    check(restored.agent_view_mode.as_deref() == Some("mighty"), "agentViewMode must survive a restart");
    let graph = &restored.graph_runs()[0];
    check(
        graph.status() == "stopped" && graph.agents().iter().all(|a| a.status() == "stopped"),
        "restoring turns running graph blocks into stopped",
    );
    check(
        graph.agents().len() == 2
            && graph.agents()[1].parent_id.as_deref() == Some("graph-child")
            && graph.agents()[1].kind.as_deref() == Some("task"),
        "the child tree itself must survive",
    );
    check(
        graph.id() == "request-one" && graph.source_run_id.as_deref() == Some("process-one") && graph.usage.map(|u| u.total()) == Some(120),
        "the saved graph itself must survive",
    );

    // A live update (any UpdateAsync) is not a restart: a running request stays running.
    let live = normalize::normalize(&snapshot, false).expect("normalized");
    let live_run = &live.sessions()[0].graph_runs()[0];
    check(
        live_run.status() == "running" && live_run.agents().iter().all(|a| a.status() == "running"),
        "a live update must not stop a running graph block",
    );

    // Whatever was saved, an agent pane loads in the Mighty view (a saved "default" included).
    for raw in ["\"grid\"", "\"default\"", "12", "null", "true"] {
        let mut node: Value = serde_json::from_str(&encoded).expect("json");
        node["sessions"][0]["agentViewMode"] = serde_json::from_str(raw).expect("a raw value");
        let loaded = load(&node.to_string());
        check(loaded.sessions()[0].agent_view_mode.as_deref() == Some("mighty"), &format!("agentViewMode {raw} must load as mighty"));
    }

    // A snapshot written before the two fields existed loads unchanged.
    let mut legacy: Value = serde_json::from_str(&encoded).expect("json");
    let pane = legacy["sessions"][0].as_object_mut().expect("a pane");
    pane.remove("agentViewMode");
    pane.remove("graphRuns");
    let loaded = load(&legacy.to_string());
    let restored = &loaded.sessions()[0];
    check(
        restored.agent_view_mode.as_deref() == Some("mighty") && restored.graph_runs.is_none(),
        "a snapshot without the fields loads in the Mighty view",
    );
}
