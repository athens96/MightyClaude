//! native/contracts/graph-vectors.json's `layout` and `camera` groups (C# GraphVectorVerification.Layout and .Camera,
//! MightyCode.Core.Tests/GraphVectorVerification.cs:397-398): every case's macOS expectation reproduced, numbers within 0.001.

mod view_support;

use std::collections::{HashMap, HashSet};

use mighty_core::graph::{GraphResponseRecord, GraphTokenUsage, MightyGraphAgent, MightyGraphRun};
use mighty_core::model::LogEntry;
use mighty_graph::camera::{self, Anchor, GraphRect, Point, Size};
use mighty_graph::layout::{self, Options};
use serde_json::{Value, json};
use view_support::*;

const FIXED_TIMESTAMP: &str = "2026-01-01T00:00:00Z";

fn document() -> Value {
    read_json(&contracts().join("graph-vectors.json"))
}

fn text<'a>(value: &'a Value, key: &str) -> Option<&'a str> {
    value.get(key).and_then(Value::as_str)
}

fn long(value: &Value, key: &str) -> i64 {
    value.get(key).and_then(Value::as_i64).unwrap_or(0)
}

fn double(value: &Value, key: &str) -> Option<f64> {
    value.get(key).and_then(Value::as_f64)
}

fn flag(value: &Value, key: &str) -> bool {
    value.get(key).and_then(Value::as_bool) == Some(true)
}

fn strings(value: Option<&Value>) -> Vec<String> {
    value.and_then(Value::as_array).map(|a| a.iter().filter_map(|v| v.as_str().map(str::to_string)).collect()).unwrap_or_default()
}

fn items(value: Option<&Value>) -> Vec<Value> {
    value.and_then(Value::as_array).cloned().unwrap_or_default()
}

fn usage_in(value: Option<&Value>) -> Option<GraphTokenUsage> {
    let value = value.filter(|v| v.is_object())?;
    Some(GraphTokenUsage {
        input_tokens: long(value, "inputTokens"),
        output_tokens: long(value, "outputTokens"),
        cache_read_tokens: long(value, "cacheReadTokens"),
        cache_creation_tokens: long(value, "cacheCreationTokens"),
    })
}

fn record_in(value: &Value) -> GraphResponseRecord {
    GraphResponseRecord {
        response_id: Some(text(value, "responseId").unwrap_or("").into()),
        model: text(value, "model").map(str::to_string),
        usage: Some(usage_in(value.get("usage")).unwrap_or_default()),
        activity_ids: Some(strings(value.get("activityIds")).into_iter().map(Some).collect()),
        marked_as_configured: flag(value, "markedAsConfigured"),
    }
}

fn records_in(value: Option<&Value>) -> Option<Vec<Option<GraphResponseRecord>>> {
    value.filter(|v| v.is_array()).map(|v| items(Some(v)).iter().map(|r| Some(record_in(r))).collect())
}

fn entry_in(value: &Value) -> LogEntry {
    entry(
        text(value, "id").unwrap_or(""),
        text(value, "kind").unwrap_or("system"),
        text(value, "text").unwrap_or(""),
        text(value, "timestamp").unwrap_or(FIXED_TIMESTAMP),
        text(value, "provider"),
    )
}

fn agent_in(value: &Value) -> MightyGraphAgent {
    MightyGraphAgent {
        id: Some(text(value, "id").unwrap_or("").into()),
        parent_id: text(value, "parentID").map(str::to_string),
        title: Some(text(value, "title").map_or_else(|| ko().get("graph.block.agent"), str::to_string)),
        input: Some(text(value, "input").unwrap_or("").into()),
        status: Some(text(value, "status").unwrap_or("running").into()),
        entries: Some(items(value.get("entries")).iter().map(entry_in).collect()),
        kind: text(value, "kind").map(str::to_string),
        usage: usage_in(value.get("usage")),
        activity_generation: value.get("activityGeneration").and_then(Value::as_i64).map(|g| g as i32),
        response_records: records_in(value.get("responseRecords")),
        notice_reply: text(value, "noticeReply").map(str::to_string),
        ..MightyGraphAgent::default()
    }
}

fn run_in(value: &Value) -> MightyGraphRun {
    MightyGraphRun {
        id: Some(text(value, "id").unwrap_or("").into()),
        input: Some(text(value, "input").unwrap_or("").into()),
        status: Some(text(value, "status").unwrap_or("running").into()),
        root_entries: Some(items(value.get("rootEntries")).iter().map(entry_in).collect()),
        agents: Some(items(value.get("agents")).iter().map(agent_in).collect()),
        result_entries: Some(vec![]),
        source_run_id: text(value, "sourceRunID").map(str::to_string),
        final_output: text(value, "finalOutput").map(str::to_string),
        usage: usage_in(value.get("usage")),
        response_records: records_in(value.get("responseRecords")),
        provider: text(value, "provider").map(str::to_string),
        node_model_label: text(value, "nodeModelLabel").map(str::to_string),
        configured_model: text(value, "configuredModel").map(str::to_string),
        ..MightyGraphRun::default()
    }
}

fn rect_json(rect: &GraphRect) -> Value {
    json!({ "x": rect.x, "y": rect.y, "w": rect.w, "h": rect.h })
}

fn anchor_json(anchor: &Anchor) -> Value {
    match anchor {
        Anchor::Hold => json!({ "kind": "hold" }),
        Anchor::Reaim { node_id, align_top } => json!({ "kind": "reaim", "nodeID": node_id, "alignTop": align_top }),
    }
}

fn run_group(group: &str, minimum: usize, produce: impl Fn(&Value) -> Value) {
    let document = document();
    check(document.get("version").and_then(Value::as_i64) == Some(1), "graph-vectors.json version must be 1");
    let cases = document.get(group).and_then(Value::as_array).unwrap_or_else(|| panic!("missing group {group}"));
    let mut names = HashSet::new();
    let mut mismatches = vec![];
    for case in cases {
        let name = text(case, "name").unwrap_or_else(|| panic!("a {group} case has no name"));
        check(names.insert(name.to_string()), format!("duplicate {group} case name {name}"));
        let expected = case.get("expected").unwrap_or_else(|| panic!("{group}/{name} has no expected value"));
        if let Some(reason) = difference(expected, &produce(case), &format!("{group}/{name}")) {
            mismatches.push(reason);
        }
    }
    check(cases.len() >= minimum, format!("group {group} has {} cases, needs {minimum}", cases.len()));
    check(mismatches.is_empty(), format!("Windows does not reproduce macOS:\n{}", mismatches.join("\n")));
}

fn run_layout(value: &Value) -> Value {
    let runs: Vec<MightyGraphRun> = items(value.get("runs")).iter().map(run_in).collect();
    let viewport = value.get("viewport").filter(|v| v.is_object()).map(|v| Size::new(double(v, "w").unwrap_or(0.0), double(v, "h").unwrap_or(0.0)));
    let options = Options {
        result_files_run_id: text(value, "resultFilesRunID").map(str::to_string),
        viewport,
        plan_run_id: text(value, "planRunID").map(str::to_string),
        plan_records: items(value.get("planRecords"))
            .iter()
            .map(|r| (text(r, "runID").unwrap_or("").to_string(), text(r, "recordID").unwrap_or("").to_string()))
            .collect(),
        ..Options::default()
    };
    let expanded: HashSet<String> = strings(value.get("expanded")).into_iter().collect();
    let made = layout::make(&runs, text(value, "draft").unwrap_or(""), flag(value, "running"), &expanded, &options, &ko());
    json!({
        "nodes": made.nodes.iter().map(|n| json!({ "id": n.id, "kind": n.kind, "frame": rect_json(&n.frame) })).collect::<Vec<_>>(),
        "edges": made.edges.iter().map(|e| json!({ "source": e.source, "target": e.target, "joins": e.joins })).collect::<Vec<_>>(),
        "size": { "w": made.size.w, "h": made.size.h },
        "originX": made.origin_x,
        "fittedResultID": made.fitted_result_id,
    })
}

fn point(args: &Value, key: &str) -> Option<Point> {
    args.get(key).filter(|v| v.is_object()).map(|v| Point::new(double(v, "x").unwrap_or(0.0), double(v, "y").unwrap_or(0.0)))
}

fn size_of(args: &Value, key: &str) -> Size {
    args.get(key).filter(|v| v.is_object()).map_or(Size::default(), |v| Size::new(double(v, "w").unwrap_or(0.0), double(v, "h").unwrap_or(0.0)))
}

fn rect_of(value: &Value) -> GraphRect {
    GraphRect::new(double(value, "x").unwrap_or(0.0), double(value, "y").unwrap_or(0.0), double(value, "w").unwrap_or(0.0), double(value, "h").unwrap_or(0.0))
}

fn run_camera(value: &Value) -> Value {
    let args = value.get("args").cloned().unwrap_or(Value::Null);
    let ids = |key: &str| -> HashSet<String> { strings(args.get(key)).into_iter().collect() };
    match text(value, "fn") {
        Some("trimAnchor") => anchor_json(&camera::trim_anchor(
            &strings(args.get("previousRunIDs")),
            &strings(args.get("runIDs")),
            text(&args, "selectedNodeID"),
            &ids("layoutNodeIDs"),
        )),
        Some("reaimAnchor") => anchor_json(&camera::reaim_anchor(text(&args, "newestRunID"), text(&args, "selectedNodeID"), &ids("layoutNodeIDs"))),
        Some("resizeAnchor") => {
            let frames: HashMap<String, GraphRect> = args
                .get("frames")
                .and_then(Value::as_object)
                .map(|o| o.iter().filter(|(_, v)| v.is_object()).map(|(k, v)| (k.clone(), rect_of(v))).collect())
                .unwrap_or_default();
            anchor_json(&camera::resize_anchor(text(&args, "fittedResultID"), text(&args, "targetID"), flag(&args, "targetAlignTop"), &frames))
        }
        Some("cameraOffset") => {
            let frame = rect_of(args.get("frame").unwrap_or(&Value::Null));
            let offset = camera::camera_offset(frame, size_of(&args, "viewport"), double(&args, "zoom").unwrap_or(1.0), flag(&args, "alignTop"));
            json!({ "x": offset.x, "y": offset.y })
        }
        Some("lostFrameIndex") => {
            let previous: Vec<GraphRect> = items(args.get("previousFrames")).iter().map(rect_of).collect();
            let current: Vec<GraphRect> = items(args.get("currentFrames")).iter().map(rect_of).collect();
            let index = camera::lost_frame_index(
                &previous,
                &current,
                point(&args, "camera").unwrap_or_default(),
                size_of(&args, "viewport"),
                double(&args, "zoom").unwrap_or(1.0),
            );
            json!({ "index": index, "stranded": index.is_some() })
        }
        Some("admittedCamera") => match camera::admitted_camera(
            text(&args, "targetToken"),
            text(&args, "consumedToken"),
            point(&args, "targetCamera"),
            point(&args, "current").unwrap_or_default(),
            point(&args, "requested").unwrap_or_default(),
        ) {
            Some(p) => json!({ "x": p.x, "y": p.y }),
            None => Value::Null,
        },
        Some("trimToken") => json!(camera::trim_token(long(&args, "sequence") as i32, text(&args, "nodeID").unwrap_or(""))),
        Some("originX") => {
            let leading = double(&args, "leadingMinX").unwrap_or(0.0);
            let trailing = double(&args, "trailingMaxX").unwrap_or(0.0);
            json!({ "originX": camera::origin_x(leading), "canvasWidth": camera::canvas_width(leading, trailing) })
        }
        _ => Value::Null,
    }
}

/// "graph layout matches macOS frames" (GraphVectorVerification.Layout).
#[test]
fn layout() {
    run_group("layout", 10, run_layout);
}

/// "graph camera anchors match macOS" (GraphVectorVerification.Camera).
#[test]
fn camera() {
    run_group("camera", 6, run_camera);
}
