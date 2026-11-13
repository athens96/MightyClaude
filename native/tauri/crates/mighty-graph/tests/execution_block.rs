//! The diagram's 실행 block (C# MightyGraphExecutionBlockVerification, MightyCode.Core.Tests/MightyGraphExecutionBlockVerification.cs): a
//! request block holds its input alone and the main agent's work sits in a 실행 block right under it.

mod view_support;

use std::collections::{HashMap, HashSet};
use std::sync::Arc;

use indexmap::IndexMap;
use mighty_core::Wire;
use mighty_core::graph::{GraphBlockSize, GraphTokenUsage, MightyGraphAgent, MightyGraphRun, node_id};
use mighty_core::json::JsonOut;
use mighty_core::model::{AppSnapshot, RunSession, Workspace};
use mighty_core::shell::Tone;
use mighty_graph::block_prefs::{self, BlockSizes};
use mighty_graph::camera::{self, Anchor, GraphRect};
use mighty_graph::layout::{self, Layout, Options};
use mighty_graph::view_model::{self, MightyGraphBlock};
use mighty_graph::{activity, node};
use view_support::*;

const ROW_GAP: f64 = 32.0;

fn agent_of(id: &str, parent_id: Option<&str>, status: &str) -> MightyGraphAgent {
    let mut a = agent(id, id, "check", status);
    a.parent_id = parent_id.map(str::to_string);
    a
}

fn run_of(id: &str, status: &str, agents: Vec<MightyGraphAgent>) -> MightyGraphRun {
    let mut r = with_agents(run(id, &format!("request {id}"), status), agents);
    r.final_output = (status == "completed").then(|| format!("result {id}"));
    r
}

fn split(runs: &[MightyGraphRun], running: bool, expanded: &[String], block_sizes: Option<HashMap<String, GraphBlockSize>>, plan_records: Vec<(String, String)>) -> Layout {
    let options = Options { block_sizes, plan_records, splits_execution: true, ..Options::default() };
    layout::make(runs, "", running, &expanded.iter().cloned().collect(), &options, &ko())
}

fn split_plain(runs: &[MightyGraphRun], running: bool) -> Layout {
    split(runs, running, &[], None, vec![])
}

fn shared(runs: &[MightyGraphRun], running: bool) -> Layout {
    layout::make(runs, "", running, &HashSet::new(), &Options::default(), &ko())
}

fn id(run: &str, suffix: &str) -> String {
    node_id(run, suffix)
}

fn frame(layout: &Layout, id: &str) -> GraphRect {
    layout.nodes.iter().find(|n| n.id == id).unwrap_or_else(|| panic!("no node {id}")).frame
}

fn intersects(a: &GraphRect, b: &GraphRect) -> bool {
    a.x < b.max_x() && b.x < a.max_x() && a.y < b.max_y() && b.y < a.max_y()
}

fn blocks(layout: &Layout, runs: &[MightyGraphRun]) -> Vec<MightyGraphBlock> {
    view_model::blocks(layout, runs, "", "Claude", true, None, None, &ko())
}

fn size(w: f64, h: f64) -> GraphBlockSize {
    GraphBlockSize { width: w, height: h }
}

fn same(sizes: Option<&BlockSizes>, expected: &[(String, GraphBlockSize)]) -> bool {
    sizes.is_some_and(|s| s.len() == expected.len() && expected.iter().all(|(id, size)| s.get(id).copied().flatten() == Some(*size)))
}

fn sizes(pairs: &[(String, GraphBlockSize)]) -> BlockSizes {
    pairs.iter().map(|(k, v)| (k.clone(), Some(*v))).collect()
}

/// "mighty execution block: a stable node id" (TheExecutionBlockHasAStableNodeID).
#[test]
fn the_execution_block_has_a_stable_node_id() {
    check(
        layout::WORK_SUFFIX == "execution"
            && node_id("one", layout::WORK_SUFFIX) == "3:one:execution"
            && layout::node_id(&run_of("one", "completed", vec![]), layout::WORK_SUFFIX) == id("one", "execution"),
        "the 실행 block's node id",
    );
    check(!camera::is_auxiliary(&id("one", "execution")), "the 실행 block is not auxiliary");
    let runs = vec![run_of("one", "completed", vec![agent_of("a", None, "completed")])];
    let ids = |l: Layout| l.nodes.into_iter().map(|n| n.id).collect::<Vec<_>>();
    check(
        ids(split_plain(&runs, false)) == ids(split_plain(&runs, false))
            && split_plain(&runs, false).nodes.iter().filter(|n| n.kind == layout::WORK_KIND).map(|n| n.id.clone()).collect::<Vec<_>>()
                == vec![id("one", "execution")],
        "the 실행 block is the same node on every layout",
    );
}

/// "mighty execution block: the request sits over it in one column" (TheRequestSitsOverItsExecutionBlockInOneColumn).
#[test]
fn the_request_sits_over_its_execution_block_in_one_column() {
    let made = split_plain(&[run_of("one", "completed", vec![])], false);
    let kinds: Vec<&str> = made.nodes.iter().map(|n| n.kind.as_str()).collect();
    check(kinds == ["request", layout::WORK_KIND, "result", "draft"], format!("request, 실행, result, draft: {kinds:?}"));
    let (request, work, result) = (frame(&made, &id("one", "request")), frame(&made, &id("one", "execution")), frame(&made, &id("one", "result")));
    check(request.h == layout::request_height(false) && layout::request_height(false) == 140.0 && work.h == 280.0, "the request is short and its 실행 block tall");
    check(
        work.y == request.max_y() + ROW_GAP
            && request.mid_x() == work.mid_x()
            && work.mid_x() == result.mid_x()
            && (work.mid_x() - camera::CENTRE_X).abs() < 0.5
            && result.y == work.max_y() + ROW_GAP,
        "one column on the centreline",
    );
    let edges: Vec<(String, String)> = made.edges.iter().map(|e| (e.source.clone(), e.target.clone())).collect();
    check(
        edges
            == vec![
                (id("one", "request"), id("one", "execution")),
                (id("one", "execution"), id("one", "result")),
                (id("one", "result"), camera::PENDING_NODE_ID.to_string()),
            ]
            && !made.edges[0].joins,
        "request → 실행 → result → draft",
    );
    let least = layout::minimum_block_size(layout::WORK_KIND);
    check(least.w == mighty_core::graph::MINIMUM_WIDTH && least.h == mighty_core::graph::MINIMUM_HEIGHT, "the 실행 block's least is any block's");
    let opened = split(&[run_of("one", "completed", vec![])], false, &[id("one", "request"), id("one", "execution")], None, vec![]);
    check(
        frame(&opened, &id("one", "request")).h == layout::request_height(true)
            && layout::request_height(true) == 280.0
            && frame(&opened, &id("one", "execution")).h == 540.0,
        "opened heights",
    );
    let sized = split(&[run_of("one", "completed", vec![])], false, &[], Some(HashMap::from([(id("one", "request"), size(640.0, 300.0))])), vec![]);
    let request = frame(&sized, &id("one", "request"));
    check(request.w == 640.0 && request.h == 300.0 && frame(&sized, &id("one", "execution")).y == 24.0 + 300.0 + ROW_GAP, "a saved request size");
}

/// "mighty execution block: sub-agents branch from it" (SubAgentsBranchFromTheExecutionBlock).
#[test]
fn sub_agents_branch_from_the_execution_block() {
    let runs = vec![run_of(
        "one",
        "completed",
        vec![agent_of("root", None, "completed"), agent_of("left", Some("root"), "completed"), agent_of("solo", None, "completed")],
    )];
    let made = split_plain(&runs, false);
    let (work, request) = (id("one", "execution"), id("one", "request"));
    let has = |s: &str, t: &str| made.edges.iter().any(|e| e.source == s && e.target == t);
    check(
        has(&work, &id("one", "agent:root")) && has(&work, &id("one", "agent:solo")) && has(&id("one", "agent:root"), &id("one", "agent:left")),
        "sub-agents branch from the 실행 block",
    );
    check(made.edges.iter().filter(|e| e.source == request).map(|e| e.target.clone()).collect::<Vec<_>>() == vec![work.clone()], "the request's only edge");
    let work_frame = frame(&made, &work);
    for a in ["root", "solo"] {
        check(frame(&made, &id("one", &format!("agent:{a}"))).y == work_frame.max_y() + ROW_GAP, format!("the first row of agents is under the 실행 block: {a}"));
    }
    let into_result: HashSet<String> = made.edges.iter().filter(|e| e.target == id("one", "result")).map(|e| e.source.clone()).collect();
    check(into_result == HashSet::from([id("one", "agent:left"), id("one", "agent:solo")]), "the result joins the leaves");
    let taller = split(&runs, false, &[], Some(HashMap::from([(work.clone(), size(500.0, 400.0))])), vec![]);
    check(
        frame(&taller, &id("one", "agent:root")).y == work_frame.y + 400.0 + ROW_GAP && frame(&taller, &request) == frame(&made, &request),
        "a taller 실행 block pushes its agents down and leaves the request",
    );
}

/// "mighty execution block: an empty run still shows it" (AnEmptyRunStillShowsItsExecutionBlock).
#[test]
fn an_empty_run_still_shows_its_execution_block() {
    let fresh = vec![run_of("new", "running", vec![])];
    let made = split_plain(&fresh, true);
    check(
        made.nodes.iter().map(|n| n.kind.as_str()).collect::<Vec<_>>() == ["request", layout::WORK_KIND]
            && made.edges.iter().map(|e| e.target.clone()).collect::<Vec<_>>() == vec![id("new", "execution")],
        "a request and its 실행 block alone",
    );
    let live: Vec<String> = blocks(&made, &fresh).iter().filter(|b| view_model::tone(b) == Tone::Run).map(|b| b.id.clone()).collect();
    check(live == vec![id("new", "execution")], "only the 실행 block is live");
    let done = vec![run_of("done", "completed", vec![])];
    check(!blocks(&split_plain(&done, false), &done).iter().any(|b| view_model::tone(b) == Tone::Run), "a settled request has no live block");
}

/// "mighty execution block: the request block holds its input and it holds the work" (TheRequestBlockHoldsItsInputAndTheExecutionBlockTheWork).
#[test]
fn the_request_block_holds_its_input_and_the_execution_block_the_work() {
    let mut r = run_of("one", "running", vec![agent_of("a", None, "running")]);
    r.root_entries = Some(vec![
        entry("u", "user", r.input(), "2026-10-14T10:00:00Z", None),
        entry("r", "assistant", "Looking.", "2026-10-14T10:00:01Z", None),
    ]);
    r.usage = Some(GraphTokenUsage { input_tokens: 100, output_tokens: 20, ..GraphTokenUsage::default() });
    r.response_records = Some(vec![Some(node::response_record(
        "m1",
        Some("claude-opus-5"),
        GraphTokenUsage { input_tokens: 100, output_tokens: 20, ..GraphTokenUsage::default() },
        &[],
        false,
    ))]);
    let runs = vec![r.clone()];
    let made = blocks(&split_plain(&runs, true), &runs);
    let request = made.iter().find(|b| b.kind == "request").unwrap();
    let work = made.iter().find(|b| b.kind == layout::WORK_KIND).unwrap();
    check(
        request.request == r.input()
            && request.entries.is_empty()
            && request.state.is_empty()
            && request.status.is_empty()
            && request.capsule.is_none()
            && request.capsule_help.is_empty()
            && request.indicator == "none"
            && request.outline == activity::NONE
            && view_model::tone(request) == Tone::Idle,
        "the request block holds its input alone",
    );
    check(request.title == view_model::request_title(1, "Claude", &ko()), "the request block keeps its title");
    check(
        work.request.is_empty()
            && work.entries.iter().map(|e| e.id()).collect::<Vec<_>>() == ["r"]
            && work.status == "running"
            && work.state == view_model::state_label("running", &ko())
            && work.capsule.is_some()
            && work.indicator == "animating"
            && work.outline == activity::MARCHING
            && view_model::tone(work) == Tone::Run,
        "the 실행 block holds the work",
    );
    check(
        work.title == ko().get_with("graph.block.executionOf", &[("n", "1")])
            && work.title != view_model::execution_title(2, &ko())
            && view_model::title_provider(work, "claude").is_none(),
        "the 실행 block names its request and carries no agent mark",
    );
    let shared_blocks = blocks(&shared(&runs, true), &runs);
    let shared_request = shared_blocks.iter().find(|b| b.kind == "request").unwrap();
    check(
        shared_request.entries.len() == 1 && shared_request.status == "running" && shared_request.capsule.is_some(),
        "the shared layout's request block is unchanged",
    );
}

/// "mighty execution block: the camera stands on it while the request runs" (TheCameraStandsOnTheExecutionBlockWhileTheRequestRuns).
#[test]
fn the_camera_stands_on_the_execution_block_while_the_request_runs() {
    let mut running = vec![run_of("one", "completed", vec![]), run_of("two", "running", vec![agent_of("child", None, "running")])];
    check(split_plain(&running, true).newest_request_target_id(&running) == Some(id("two", "execution")), "a running request's 실행 block");
    running[1].status = Some("completed".into());
    check(split_plain(&running, true).newest_request_target_id(&running) == Some(id("two", "execution")), "a request with a child at work");
    running[1].status = Some("running".into());
    let done = vec![run_of("one", "completed", vec![]), run_of("two", "completed", vec![])];
    check(split_plain(&done, false).newest_request_target_id(&done) == Some(id("two", "request")), "a finished request");
    check(
        shared(&running, true).newest_request_target_id(&running) == Some(id("two", "request"))
            && split_plain(&[], false).newest_request_target_id(&[]).is_none(),
        "the shared layout and no request",
    );
    let ids: HashSet<String> = split_plain(&running, true).nodes.into_iter().map(|n| n.id).collect();
    check(camera::reaim_anchor(Some("two"), None, &ids) == Anchor::reaim(&id("two", "request"), true), "a new request aims at its request block");
}

/// "mighty execution block: the request's attachments hang off it" (TheRequestsAttachmentsHangOffItsExecutionBlock).
#[test]
fn the_requests_attachments_hang_off_its_execution_block() {
    let runs = vec![
        run_of("one", "completed", (0..3).map(|i| agent_of(&format!("branch-{i}"), None, "completed")).collect()),
        run_of("two", "completed", vec![]),
    ];
    let plain = split_plain(&runs, false);
    let made = split(&runs, false, &[], None, vec![("one".into(), "p1".into()), ("one".into(), "p2".into())]);
    for node in &plain.nodes {
        check(frame(&made, &node.id) == node.frame, format!("nothing already placed moves: {}", node.id));
    }
    check(made.edges == plain.edges, "attachments add no edge");
    let work = frame(&made, &id("one", "execution"));
    let first = frame(&made, &id("one", &format!("{}p1", layout::PLAN_RECORD_SUFFIX)));
    let second = frame(&made, &id("one", &format!("{}p2", layout::PLAN_RECORD_SUFFIX)));
    check(first.y == work.y && first.x > work.max_x() && second.y > first.max_y() && second.x > work.max_x(), "answered plans stack beside the 실행 block, from its top");
    for attachment in [first, second] {
        for node in made.nodes.iter().filter(|n| n.frame != attachment) {
            check(!intersects(&node.frame, &attachment), format!("an attachment overlaps {}", node.id));
        }
    }
    let shared_layout =
        layout::make(&runs, "", false, &HashSet::new(), &Options { plan_records: vec![("one".into(), "p1".into())], ..Options::default() }, &ko());
    check(
        frame(&shared_layout, &id("one", &format!("{}p1", layout::PLAN_RECORD_SUFFIX))).y == frame(&shared_layout, &id("one", "request")).y,
        "the shared layout hangs them off the request",
    );
}

/// "mighty execution block: without the split the shared layout is unchanged" (WithoutTheSplitTheSharedLayoutIsUnchanged).
#[test]
fn without_the_split_the_shared_layout_is_unchanged() {
    let runs = vec![run_of("one", "completed", vec![agent_of("a", None, "completed")]), run_of("two", "running", vec![])];
    let made = shared(&runs, true);
    check(
        !made.nodes.iter().any(|n| n.kind == layout::WORK_KIND)
            && made.edges.iter().any(|e| e.source == id("one", "request") && e.target == id("one", "agent:a"))
            && frame(&made, &id("one", "request")).h == 280.0,
        "the shared layout has no 실행 block",
    );
    // MightyGraphViewModel.CanvasLayout is MightyGraphLayout.Make with the same options.
    let canvas = layout::make(&runs, "", true, &HashSet::new(), &Options::default(), &ko());
    let split_canvas = split_plain(&runs, true);
    check(
        canvas.nodes.iter().map(|n| (n.id.clone(), n.frame)).collect::<Vec<_>>() == made.nodes.iter().map(|n| (n.id.clone(), n.frame)).collect::<Vec<_>>()
            && split_canvas.nodes.iter().any(|n| n.kind == layout::WORK_KIND),
        "the canvas layout splits only when asked",
    );
}

fn write(session: &RunSession) -> serde_json::Value {
    let mut out = JsonOut::new();
    session.write(&mut out);
    serde_json::from_str(&out.finish()).expect("json")
}

fn clone_through_wire(session: &RunSession) -> RunSession {
    RunSession::read(&write(session)).expect("read")
}

/// "mighty execution block: a size saved before the split moves to it once" (ASizeSavedBeforeTheSplitMovesToTheExecutionBlockOnce).
#[test]
fn a_size_saved_before_the_split_moves_to_the_execution_block_once() {
    let dragged = size(1_100.0, 700.0);
    let own = size(640.0, 400.0);
    let directory = std::env::temp_dir();
    let workspace = Workspace {
        id: Some("workspace".into()),
        name: Some("Graph".into()),
        path: Some(directory.to_string_lossy().into_owned()),
        ..Workspace::default()
    };
    let saved = |session: &RunSession| -> RunSession {
        let snapshot =
            AppSnapshot { workspaces: Some(vec![workspace.clone()]), sessions: Some(vec![Arc::new(session.clone())]), ..AppSnapshot::default() };
        let normalized = mighty_core::normalize::normalize(&snapshot, true).expect("normalize");
        normalized.sessions().first().expect("the pane").as_ref().clone()
    };
    let session = RunSession {
        id: Some("old".into()),
        workspace_id: Some("workspace".into()),
        title: Some("Old".into()),
        graph_runs: Some(vec![run("a", "a", "completed"), run("b", "b", "completed")]),
        graph_block_sizes: Some(sizes(&[(id("a", "request"), dragged), (id("b", "request"), dragged), (id("b", "execution"), own)])),
        ..RunSession::default()
    };
    let mut wire = write(&session);
    check(wire["graphSizesLayout"].as_i64() == Some(block_prefs::SPLIT_LAYOUT as i64), "a pane is saved with its sizes' layout");
    wire.as_object_mut().unwrap().remove("graphSizesLayout");
    let legacy = RunSession::read(&wire).expect("read");
    check(legacy.graph_sizes_layout.is_none(), "a pane saved before the split has no layout marker");
    let migrated = saved(&legacy);
    check(
        same(migrated.graph_block_sizes.as_ref(), &[(id("a", "execution"), dragged), (id("b", "execution"), own)])
            && migrated.graph_sizes_layout == Some(block_prefs::SPLIT_LAYOUT),
        format!("a request size from before the split is its 실행 block's: {:?}", migrated.graph_block_sizes),
    );
    let laid = split(migrated.graph_runs.as_deref().unwrap_or(&[]), false, &[], Some(block_prefs::drawn(migrated.graph_block_sizes.as_ref())), vec![]);
    let request = frame(&laid, &id("a", "request"));
    let execution = frame(&laid, &id("a", "execution"));
    check(
        request.w == camera::REQUEST_WIDTH && request.h == 140.0 && execution.w == 1_100.0 && execution.h == 700.0,
        "the request is short again and the 실행 block takes the size",
    );
    let mut later = migrated.clone();
    let mut later_sizes = migrated.graph_block_sizes.clone().unwrap();
    later_sizes.insert(id("a", "request"), Some(own));
    later.graph_block_sizes = Some(later_sizes);
    for again in [saved(&later), saved(&clone_through_wire(&later))] {
        let s = again.graph_block_sizes.unwrap_or_default();
        check(
            s.get(&id("a", "request")).copied().flatten() == Some(own) && s.get(&id("a", "execution")).copied().flatten() == Some(dragged),
            "the hand-over happens once",
        );
    }
    let fresh = RunSession {
        workspace_id: Some("workspace".into()),
        title: Some("New".into()),
        graph_runs: Some(vec![run("a", "a", "completed")]),
        graph_block_sizes: Some(sizes(&[(id("a", "request"), own)])),
        ..RunSession::default()
    };
    check(
        fresh.graph_sizes_layout == Some(block_prefs::SPLIT_LAYOUT)
            && same(saved(&clone_through_wire(&fresh)).graph_block_sizes.as_ref(), &[(id("a", "request"), own)]),
        "a new pane's request size stays the request's",
    );
    for value in ["\"1\"", "1.5", "true", "{}", "[]", "null"] {
        let read = RunSession::read(&serde_json::from_str(&format!("{{\"id\":\"s\",\"graphSizesLayout\":{value}}}")).unwrap()).expect("read");
        check(read.graph_sizes_layout.is_none(), format!("a damaged layout marker reads as none: {value}"));
    }
    let read = RunSession::read(&serde_json::from_str("{\"id\":\"s\",\"graphSizesLayout\":1}").unwrap()).expect("read");
    check(read.graph_sizes_layout == Some(1), "a saved layout marker is read");
    check(
        block_prefs::splitting_execution(None).is_none()
            && block_prefs::splitting_execution(Some(&IndexMap::new())).is_none()
            && same(
                block_prefs::splitting_execution(Some(&sizes(&[
                    (camera::PENDING_NODE_ID.into(), own),
                    (id("a", "agent:request"), own),
                    (id("a", "result"), own),
                ])))
                .as_ref(),
                &[(camera::PENDING_NODE_ID.into(), own), (id("a", "agent:request"), own), (id("a", "result"), own)],
            ),
        "only a request's size moves",
    );
}

/// "mighty execution block: a request and it share one saved-size place" (ARequestAndItsExecutionBlockShareOneSavedSizePlace).
#[test]
fn a_request_and_its_execution_block_share_one_saved_size_place() {
    let s = size(620.0, 360.0);
    let mut runs: Vec<MightyGraphRun> = (0..block_prefs::MAXIMUM_SIZES).map(|i| run(&format!("run-{i}"), "", "running")).collect();
    let mut values = BlockSizes::new();
    for r in &runs {
        values.insert(id(r.id(), "request"), Some(s));
        values.insert(id(r.id(), "execution"), Some(s));
    }
    let saved = block_prefs::normalize_owned(Some(&values), &runs).unwrap();
    let get = |m: &BlockSizes, k: &str| m.get(k).copied().flatten();
    check(
        block_prefs::MAXIMUM_SIZES == 1_024
            && saved.len() == 2 * block_prefs::MAXIMUM_SIZES
            && get(&saved, &id("run-0", "request")) == Some(s)
            && get(&saved, &id("run-0", "execution")) == Some(s),
        "a request and its 실행 block share one place",
    );
    runs.push(run("run-new", "", "running"));
    values.insert(id("run-new", "request"), Some(s));
    let full = block_prefs::normalize_owned(Some(&values), &runs).unwrap();
    check(
        full.len() == 2 * block_prefs::MAXIMUM_SIZES - 1
            && full.contains_key(&id("run-new", "request"))
            && !full.contains_key(&id("run-0", "request"))
            && !full.contains_key(&id("run-0", "execution")),
        "recent turns are preferred when the places run out",
    );
    let pane = RunSession { graph_block_sizes: Some(saved), ..RunSession::default() };
    let set = block_prefs::set(&pane, &id("run-0", "execution"), Some(&size(700.0, 500.0))).graph_block_sizes.unwrap();
    check(
        set.len() == 2 * block_prefs::MAXIMUM_SIZES
            && get(&set, &id("run-0", "execution")) == Some(size(700.0, 500.0))
            && get(&set, &id("run-0", "request")) == Some(s),
        "saving one size keeps every pair",
    );
}

/// "mighty execution block: its saved size is kept, with the pane's own blocks only" (AnExecutionBlocksSavedSizeIsKept).
#[test]
fn an_execution_blocks_saved_size_is_kept() {
    let s = size(620.0, 360.0);
    let mut live = with_agents(run("live", "", "running"), vec![agent_of("child", None, "running")]);
    let record = id("live", &format!("{}p1", layout::PLAN_RECORD_SUFFIX));
    let values = sizes(&[
        (id("live", "execution"), s),
        (id("gone", "execution"), s),
        (id("live", "agent:child"), s),
        (id("live", "agent:gone"), s),
        (id("live", "result"), s),
        (camera::PENDING_NODE_ID.into(), s),
        (record.clone(), size(360.0, 40.0)),
        ("other".into(), s),
        (id("live", "request"), size(f64::NAN, 100.0)),
    ]);
    check(
        same(
            block_prefs::normalize_owned(Some(&values), std::slice::from_ref(&live)).as_ref(),
            &[
                (id("live", "execution"), s),
                (id("live", "agent:child"), s),
                (camera::PENDING_NODE_ID.into(), s),
                (record, size(360.0, layout::plan_record_height(false))),
            ],
        ),
        "a pane keeps the sizes of its own blocks only",
    );
    live.status = Some("completed".into());
    live.agents.as_mut().unwrap()[0].status = Some("completed".into());
    check(
        block_prefs::normalize_owned(Some(&values), std::slice::from_ref(&live)).unwrap().get(&id("live", "result")).copied().flatten() == Some(s),
        "a settled run keeps its result's size",
    );
    check(
        block_prefs::normalize_owned(None, std::slice::from_ref(&live)).is_none()
            && block_prefs::normalize_owned(Some(&sizes(&[("other".into(), s)])), std::slice::from_ref(&live)).is_none(),
        "nothing owned is nothing saved",
    );
}
