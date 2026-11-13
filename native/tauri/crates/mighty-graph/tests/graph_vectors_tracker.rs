//! native/contracts/graph-vectors.json, the groups that drive the tracker and the run rules (C# GraphVectorVerification.cs: ClaudeStream,
//! CodexStream, Mods, Bounds, Capsule, Files). The file is macOS truth; every case runs with the Korean locale, so the committed display
//! strings are the ko copy the port reads from locales/ko.json. (Layout and Camera: tests/graph_vectors_layout.rs.)

mod common;

use common::*;
use mighty_core::graph::GraphTokenUsage;
use mighty_core::model::AgentActivity;
use mighty_core::model_label::ModelOption;
use mighty_graph::result_files::{self, StdFiles};
use mighty_graph::tracker::{ModGraphMetadata, ModMetadata};
use mighty_graph::usage_format::{self, GraphChildBlock};
use mighty_graph::{labels, node, support};
use serde_json::{Value, json};

fn document() -> Value {
    read_json(&contracts().join("graph-vectors.json"))
}

fn activity_in(value: &Value) -> AgentActivity {
    AgentActivity {
        id: text(value, "id").unwrap_or("").into(),
        provider: text(value, "provider").unwrap_or("claude").into(),
        kind: text(value, "kind").unwrap_or("tool").into(),
        state: text(value, "state").unwrap_or("completed").into(),
        summary: text(value, "summary").unwrap_or("").into(),
        tool_name: text(value, "toolName").map(Into::into),
        output: text(value, "output").map(Into::into),
        duration_ms: value.get("durationMs").and_then(Value::as_f64),
    }
}

fn mod_in(value: &Value) -> ModMetadata {
    let graph = value.get("graph").filter(|g| g.is_object()).map(|raw| ModGraphMetadata {
        version: raw.get("version").and_then(Value::as_i64).map_or(1, |v| v as i32),
        phase: text(raw, "phase").unwrap_or("starting").into(),
        agent_id: text(raw, "agentId").map(Into::into),
        parent_agent_id: text(raw, "parentAgentId").map(Into::into),
        parent_tool_use_id: text(raw, "parentToolUseId").map(Into::into),
        name: text(raw, "name").map(Into::into),
        agent_type: text(raw, "agentType").map(Into::into),
        model: text(raw, "model").map(Into::into),
        input: text(raw, "input").map(Into::into),
        output: text(raw, "output").map(Into::into),
    });
    ModMetadata {
        claude_session_id: text(value, "claudeSessionId").unwrap_or("session").into(),
        event: text(value, "event").unwrap_or("agent.spawn").into(),
        tool: text(value, "tool").map(Into::into),
        tool_use_id: text(value, "toolUseId").map(Into::into),
        agent_id: text(value, "agentId").map(Into::into),
        graph,
    }
}

fn result_title(status: &str, waiting: i32) -> String {
    mighty_graph::view_model::result_title(status, waiting, false, &ko())
}

/// claudeStream, codexStream and mods all drive the tracker: a run identity plus an ordered list of steps.
fn run_tracker(value: &Value) -> Value {
    let (mut tracker, emitted) = recording_tracker(
        text(value, "runId").unwrap_or("run-1"),
        text(value, "input"),
        text(value, "provider").unwrap_or("claude"),
        text(value, "configuredModel"),
    );
    for step in items(value.get("steps")) {
        match text(&step, "kind") {
            Some("frame") => tracker.consume(&step["value"]),
            Some("mod") => tracker.receive_mod(&mod_in(&step["value"])),
            Some("steer") => tracker.steer(text(&step, "id").unwrap_or(""), text(&step, "text").unwrap_or("")),
            Some("activity") => {
                tracker.activity(&activity_in(&step["value"]), text(&step, "toolId"));
            }
            Some("finish") => tracker.finish(text(&step, "state").unwrap_or("completed")),
            _ => {}
        }
    }
    let nodes: Vec<Value> = latest(&emitted.lock().expect("emitted")).iter().map(node_json).collect();
    let mut result = json!({ "nodes": nodes });
    // A case marked `waiting` also pins what the request's result card reads: the background work its run still waits on.
    if flag(value, "waiting")
        && let Some(run) = tracker.build_run(true)
    {
        let waiting = support::background_waiting(&run);
        result["backgroundWaiting"] = json!(waiting);
        result["resultTitle"] = json!(result_title(run.status(), waiting));
    }
    result
}

fn run_bounds(value: &Value) -> Value {
    let ko = ko();
    let runs: Vec<_> = items(value.get("runs")).iter().map(|r| run_in(r, &ko)).collect();
    if text(value, "op") == Some("boundedLiveHistory") {
        return json!({"runs": support::bounded_live_history(&runs).iter().map(run_json).collect::<Vec<_>>(), "budget": null});
    }
    let mut budget = value.get("budget").and_then(Value::as_i64).unwrap_or(support::LIVE_HISTORY_LIMIT);
    let result = support::normalized(&runs, flag(value, "restoring"), &mut budget, text(value, "provider"));
    json!({"runs": result.iter().map(run_json).collect::<Vec<_>>(), "budget": budget})
}

fn catalog_in(value: Option<&Value>) -> Vec<ModelOption> {
    items(value)
        .iter()
        .map(|o| ModelOption {
            value: text(o, "value").unwrap_or("").into(),
            display_name: text(o, "displayName").unwrap_or("").into(),
            resolved_model: text(o, "resolvedModel").map(Into::into),
        })
        .collect()
}

fn run_capsule(value: &Value) -> Value {
    let ko = ko();
    let args = &value["args"];
    let catalog = catalog_in(args.get("catalog"));
    let records: Vec<_> = items(args.get("records")).iter().map(record_in).collect();
    let usage = usage_in(args.get("usage"));
    match text(value, "fn") {
        Some("blockCapsule") => {
            json!(usage_format::block_capsule(usage, &records, text(args, "nodeModelLabel"), Some(&catalog), false, &ko))
        }
        Some("blockCapsuleHelp") => json!(usage_format::block_capsule_help(&records, Some(&catalog), false, &ko)),
        Some("activitySuffix") => {
            let child = args.get("childBlock").filter(|c| c.is_object()).map(|raw| GraphChildBlock {
                usage: usage_in(raw.get("usage")),
                records: items(raw.get("records")).iter().map(record_in).collect(),
            });
            json!(usage_format::activity_suffix(
                text(args, "activityId").unwrap_or(""),
                &records,
                child.as_ref(),
                Some(&catalog),
                false,
                &ko
            ))
        }
        Some("callerAttribution") => json!(usage_format::caller_attribution(text(args, "activityId").unwrap_or(""), &records)),
        Some("blockModels") => Value::Array(
            usage_format::block_models(&records).into_iter().map(|(m, u)| json!({"model": m, "usage": usage_json(Some(u))})).collect(),
        ),
        Some("shortName") => json!(usage_format::short_name(text(args, "modelId").unwrap_or(""), Some(&catalog), false)),
        Some("nodeModelLabel") => {
            json!(labels::node_model_label(text(args, "cliReportedModel"), Some(text(args, "configuredModel").unwrap_or("default")), &ko))
        }
        Some("blockTitle") => json!(support::block_title(&agent_in(&args["agent"], &ko), &ko)),
        Some("blockKind") => json!(support::block_kind(&agent_in(&args["agent"], &ko))),
        Some("usageSummary") => {
            let usage: GraphTokenUsage = usage.unwrap_or_default();
            json!({"summary": node::summary(&usage, &ko), "detail": node::detail(&usage, &ko), "compact": node::compact(usage.total())})
        }
        Some("childBlocks") => {
            let records = args.get("records").filter(|r| r.is_array()).map(|r| items(Some(r)).iter().map(record_in).collect::<Vec<_>>());
            let agents: Vec<_> = items(args.get("agents")).iter().map(|a| agent_in(a, &ko)).collect();
            let map = usage_format::child_blocks(records.as_deref(), &agents, text(args, "runId").unwrap_or(""));
            let mut keys: Vec<&String> = map.keys().collect();
            keys.sort_by(|a, b| a.encode_utf16().cmp(b.encode_utf16()));
            Value::Array(
                keys.into_iter()
                    .map(|k| {
                        json!({"activityId": k, "usage": usage_json(map[k].usage), "records": map[k].records.iter().map(record_json).collect::<Vec<_>>()})
                    })
                    .collect(),
            )
        }
        _ => Value::Null,
    }
}

/// Materializes the listed workspace tree in a temporary root, runs the result-file rule over the case's texts, and returns the
/// relative paths.
fn run_result_files(value: &Value) -> Value {
    let root = std::env::temp_dir().join(format!("graph-vectors-{}", mighty_core::wire::new_id()));
    std::fs::create_dir_all(&root).expect("a temporary root");
    for relative in strings(value.get("tree")) {
        let file = root.join(relative.replace('/', std::path::MAIN_SEPARATOR_STR));
        std::fs::create_dir_all(file.parent().expect("a folder")).expect("its folder");
        std::fs::write(&file, "fixture\n").expect("the file");
    }
    let files = result_files::files_in(&strings(value.get("texts")), root.to_str(), &StdFiles { ignores_case: cfg!(windows) });
    let _ = std::fs::remove_dir_all(&root);
    json!({"paths": files.iter().map(|f| f.path.clone()).collect::<Vec<_>>(), "lines": files.iter().map(|f| f.line).collect::<Vec<_>>()})
}

#[test]
fn graph_vectors_match_macos_claude_stream() {
    run_group(&document(), "claudeStream", 6, run_tracker);
}

#[test]
fn graph_vectors_match_macos_codex_stream() {
    run_group(&document(), "codexStream", 4, run_tracker);
}

#[test]
fn graph_vectors_match_macos_mods_events() {
    run_group(&document(), "mods", 3, run_tracker);
}

#[test]
fn graph_history_bounds_and_restore_match_macos() {
    run_group(&document(), "bounds", 3, run_bounds);
}

#[test]
fn graph_capsule_text_matches_macos() {
    run_group(&document(), "capsule", 8, run_capsule);
}

#[test]
fn graph_result_files_match_macos() {
    run_group(&document(), "resultFiles", 4, run_result_files);
}
