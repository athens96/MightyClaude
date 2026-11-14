//! C# ModelLabelVerification `GraphCapsulesAndResumeRowsReadVersions`, its graph half (`ModelUsageFormat` and the canvas's request
//! block); the resume row's half is mighty-history's resumable_sessions.rs. The catalogue is the installed Claude Code's rows of
//! 2026-10-03 as the run engine reads them.

mod view_support;

use std::collections::HashSet;

use mighty_core::graph::GraphTokenUsage;
use mighty_core::model_label::ModelOption;
use mighty_graph::layout::{self, Options};
use mighty_graph::{node, usage_format, view_model};
use serde_json::Value;
use view_support::*;

fn claude_rows() -> Vec<ModelOption> {
    let rows: Vec<Value> = serde_json::from_str(
        r#"[
          {"value": "default", "resolvedModel": "claude-opus-5-5", "displayName": "Default (recommended)"},
          {"value": "opus", "resolvedModel": "claude-opus-5-5", "displayName": "Opus"},
          {"value": "claude-fable-5-1[1m]", "resolvedModel": "claude-fable-5-1", "displayName": "Fable"},
          {"value": "sonnet", "resolvedModel": "claude-sonnet-5-5", "displayName": "Sonnet"},
          {"value": "haiku", "resolvedModel": "claude-haiku-4-5-20251001", "displayName": "Haiku"}
        ]"#,
    )
    .unwrap();
    mighty_run::providers::claude_catalog(&rows).models.iter().map(|m| m.label_option()).collect()
}

#[test]
fn graph_capsules_and_resume_rows_read_versions() {
    mighty_run::strings::set_language("ko");
    let ko = ko();
    let catalog = claude_rows();
    let records = vec![node::response_record(
        "r1",
        Some("claude-opus-5-5"),
        GraphTokenUsage { input_tokens: 1000, output_tokens: 50, ..GraphTokenUsage::default() },
        &["a1".to_string()],
        false,
    )];
    let usage = GraphTokenUsage { input_tokens: 1500, output_tokens: 80, ..GraphTokenUsage::default() };
    check(
        usage_format::block_capsule(Some(usage), &records, None, Some(&catalog), true, &ko).as_deref() == Some("1.6K · Opus 5.5"),
        "versioned capsule",
    );
    check(usage_format::short_name("claude-sonnet-4-5", None, true) == "Sonnet 4.5", "versioned short name");
    // The shared graph vectors keep the catalogue-only name.
    check(usage_format::short_name("claude-sonnet-4-5", None, false) == "claude-sonnet-4-5", "catalogue-only short name");
    let marker = format!(" {}", ko.get("graph.nodeModel.configuredSuffix"));
    // Today's catalogue does not say which version an older run used.
    check(
        usage_format::versioned_node_label(Some(&format!("opus{marker}")), &ko) == Some(format!("Opus{marker}")),
        "configured alias node label",
    );
    check(
        usage_format::block_capsule(Some(usage), &[], Some(&format!("opus{marker}")), Some(&catalog), true, &ko)
            == Some(format!("Opus{marker}")),
        "capsule before a response",
    );
    check(
        usage_format::versioned_node_label(Some(&format!("claude-sonnet-4-5{marker}")), &ko) == Some(format!("Sonnet 4.5{marker}")),
        "configured id node label",
    );
    check(usage_format::versioned_node_label(Some("claude-fable-5-1"), &ko).as_deref() == Some("Fable 5.1"), "reported node label");
    check(usage_format::versioned_node_label(None, &ko).is_none(), "no node label");
    // The canvas blocks use the versioned capsule.
    let mut graph_run = run("run-1", "go", "completed");
    graph_run.provider = Some("claude".into());
    graph_run.usage = Some(usage);
    graph_run.response_records = Some(records.into_iter().map(Some).collect());
    graph_run.final_output = Some("done".into());
    let runs = vec![graph_run];
    let made = layout::make(&runs, "", false, &HashSet::new(), &Options::default(), &ko);
    let blocks = view_model::blocks(&made, &runs, "", "Claude", true, Some(&catalog), None, &ko);
    let request = blocks.iter().find(|b| b.kind == "request").expect("a request block");
    check(request.capsule.as_deref() == Some("1.6K · Opus 5.5"), format!("request block capsule on the canvas: {:?}", request.capsule));
}
