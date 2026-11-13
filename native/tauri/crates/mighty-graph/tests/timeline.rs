//! The Mighty 기록 view against C# `MightyTimelineVerification` (native/windows/MightyCode.Core.Tests/MightyTimelineVerification.cs,
//! registered in Verification.cs:236-237, 291-292, 340). The checks that read the diagram's layout (`MightyGraphLayout.Make`), the
//! toolbar (`MightyGraphBlockModel.ToolbarSummary`) or the graph tracker are left to those modules' ports; each is named where it stood.

mod common;

use std::collections::HashSet;
use std::sync::Arc;

use common::{activity, entry, ko};
use mighty_core::graph::{GraphBlockSize, MightyGraphAgent, MightyGraphRun, WORK_SUFFIX, node_id};
use mighty_core::json::Wire;
use mighty_core::model::{AppSnapshot, LogEntry, RunSession, Workspace};
use mighty_core::shell::Tone;
use mighty_graph::support;
use mighty_graph::timeline::{self, Node, Tally};

/// `MightyTimelineVerification.Run`: a run with the request "request <id>", no provider, refreshed as the app refreshes it.
fn run(id: &str, status: &str, final_output: Option<&str>, agents: Vec<MightyGraphAgent>) -> MightyGraphRun {
    let mut run = MightyGraphRun {
        id: Some(id.into()),
        status: Some(status.into()),
        input: Some(format!("request {id}")),
        final_output: final_output.map(str::to_string),
        root_entries: Some(vec![]),
        result_entries: Some(vec![]),
        agents: Some(agents),
        ..MightyGraphRun::default()
    };
    mighty_core::normalize::refresh_result(&mut run);
    run
}

/// A bare `new MightyGraphAgent { Id = id }`: running, no title or input.
fn agent(id: &str) -> MightyGraphAgent {
    MightyGraphAgent {
        id: Some(id.into()),
        title: Some(String::new()),
        input: Some(String::new()),
        status: Some("running".into()),
        entries: Some(vec![]),
        ..MightyGraphAgent::default()
    }
}

fn with_status(id: &str, status: &str) -> MightyGraphAgent {
    MightyGraphAgent { status: Some(status.into()), ..agent(id) }
}

fn steer(id: &str, input: &str) -> MightyGraphAgent {
    MightyGraphAgent { kind: Some("steer".into()), status: Some("completed".into()), input: Some(input.into()), ..agent(id) }
}

fn groups(runs: &[MightyGraphRun]) -> Vec<timeline::Group> {
    timeline::groups(runs, &ko())
}

#[test]
fn recorded_order_and_results_match_diagram() {
    let ko = ko();
    let step = LogEntry {
        activity: Some(activity("step", "tool", "running", "read 한국어.cs", Some("Read"))),
        ..entry("step", "system", "tool", "2026-10-04T10:00:00Z")
    };
    let agents = vec![
        MightyGraphAgent { title: Some("Inspect".into()), status: Some("completed".into()), ..agent("child") },
        MightyGraphAgent { kind: Some("task".into()), entries: Some(vec![step]), ..agent("task") },
        MightyGraphAgent { kind: Some("question".into()), status: Some("waiting".into()), ..agent("ask") },
    ];
    let runs = [run("old", "completed", Some("done"), vec![]), run("new", "running", None, agents)];
    let made = groups(&runs);
    let rows = &made[1].rows;
    // The request first, its 실행 block, then the agents in the order the graph recorded them.
    assert!(
        made.iter().map(|g| g.ordinal).collect::<Vec<_>>() == [1, 2]
            && rows.iter().map(|r| r.kind.as_str()).collect::<Vec<_>>() == ["main", timeline::EXECUTION_KIND, "agent", "task", "question"],
        "same request and child ordering as Mac and diagram"
    );
    assert!(
        rows.iter().map(|r| r.agent_index).collect::<Vec<_>>() == [None, None, Some(0), Some(1), Some(2)]
            && rows.iter().map(|r| r.is_execution).collect::<Vec<_>>() == [false, true, false, false, false]
            && rows[3].latest.as_deref() == Some("read 한국어.cs")
            && rows[2].latest.is_none(),
        "live activity only on unfinished blocks"
    );
    assert!(
        rows[0].node_id == node_id("new", "request")
            && rows[1].node_id == node_id("new", WORK_SUFFIX)
            && rows[3].node_id == node_id("new", "agent:task"),
        "each row stands for its diagram node"
    );
    // The 실행 block names its request, so each one reads apart from the others.
    assert!(
        rows[0].title == ko.get_with("graph.timeline.requestOrdinal", &[("n", "2")])
            && rows[1].title == ko.get_with("graph.block.executionOf", &[("n", "2")])
            && rows[1].title != made[0].rows[1].title
            && timeline::kind_label(timeline::EXECUTION_KIND, None, &ko) == ko.get("graph.block.execution"),
        "the 실행 row is named for its request"
    );
    // Deferred to the layout's port: "every timeline row shares a real diagram node identity" (MightyGraphLayout.Make, splitsExecution).
    let result = made[0].result.as_ref().expect("a result");
    // Deferred to the layout's port: the result's node is one the diagram lays out.
    assert!(result.text.as_deref() == Some("done") && result.tone == Tone::Done, "result identity and final answer are shared");
    assert!(
        groups(&[run("pending-child", "completed", Some("premature"), vec![agent("still-running")])])[0].result.is_none(),
        "an unfinished child prevents a premature result"
    );
    let failure = groups(&[run("failure", "failed", None, vec![])]).remove(0).result.expect("a result");
    let stop = groups(&[run("stop", "interrupted", None, vec![])]).remove(0).result.expect("a result");
    assert!(failure.tone == Tone::Err && failure.text.is_none() && stop.tone == Tone::Stop, "terminal aliases match diagram result states");
    // While the request awaits the notices of background work it started, the 기록 result head waits, then is done once they are in.
    let shown = |task_status: &str, status: &str| {
        let task = MightyGraphAgent { kind: Some("task".into()), status: Some(task_status.into()), background: Some(true), ..agent("t") };
        support::shown(&run("so-far", status, Some("Built."), vec![task]), &ko)
    };
    let waiting = groups(&[shown("running", "completed")]).remove(0).result.expect("a result");
    let ended = groups(&[shown("completed", "completed")]).remove(0).result.expect("a result");
    let failed = groups(&[shown("running", "error")]).remove(0).result.expect("a result");
    assert!(
        waiting.tone == Tone::Wait && waiting.awaiting && ended.tone == Tone::Done && !ended.awaiting && failed.tone == Tone::Err,
        "the awaiting head waits in 기록"
    );
    // A steer delegation sent is a 위임 알림; one a person sent stays a 중간 요청.
    let notice = steer("notice", "[Mighty Code notice n-1] Your child c-1 reported revision 1.");
    let follow_up = steer("follow", "[Mighty Code follow-up f-1] Your parent sent you this follow-up:");
    let person = steer("person", "Also add tests");
    assert!(
        support::block_title(&notice, &ko) == "위임 알림"
            && support::block_title(&follow_up, &ko) == "위임 알림"
            && support::block_title(&person, &ko) == "중간 요청",
        "a delegation notice is named 위임 알림, a person's steer 중간 요청"
    );
    assert!(
        timeline::kind_label("steer", notice.input.as_deref(), &ko) == "위임 알림"
            && timeline::kind_label("steer", person.input.as_deref(), &ko) == "중간 요청"
            && support::block_kind(&notice) == "steer",
        "the timeline names the kind the same way, and the kind stays steer"
    );
    // Deferred to the view model's port: "the toolbar counts only a person's steers" (MightyGraphBlockModel.ToolbarSummary).
    // Deferred to the tracker's port: "an unanswered delegation notice ends with its own words, a person's steer with the 중간 요청 words"
    // (ExecutionGraphTracker.Steer, Finish).
}

#[test]
fn a_task_notification_is_a_task_notice_steer() {
    let ko = ko();
    // Deferred to the tracker's port: the notification copies its block into one notice run that is complete at once and has no result
    // ("no 작업 알림 steer is made", "the absorbed notice run is complete without a result", "the notice run names each finished task and
    // how it ended", "each block is copied once, as it ended", "the request's answer stays its own", "the request holds no notice block",
    // "the notice run is built apart, flagged") — ExecutionGraphTracker.Consume, Finish, BuildRun, BuildNoticeRuns.
    let saved = MightyGraphAgent {
        kind: Some("steer".into()),
        input: Some(format!("{}Build · 완료\nBuild done", support::TASK_NOTICE_PREFIX)),
        ..agent("saved-notice")
    };
    assert!(
        support::block_title(&saved, &ko) == "작업 알림"
            && support::block_kind(&saved) == "steer"
            && support::is_task_notice_block(&saved)
            && support::is_notice(&saved)
            && !support::is_delegation_notice_block(&saved)
            && support::shown_input(&saved) == "Build · 완료\nBuild done",
        "a saved block is a 작업 알림 that shows its line without the prefix"
    );
    let legacy = MightyGraphAgent {
        kind: Some("steer".into()),
        input: Some("[Mighty Claude task notice] Build · 완료".into()),
        ..agent("old-notice")
    };
    assert!(
        support::is_task_notice_block(&legacy) && support::shown_input(&legacy) == "Build · 완료",
        "a block saved before the rename is still a 작업 알림"
    );
    let old_delegation = MightyGraphAgent {
        kind: Some("steer".into()),
        input: Some("[Mighty Claude notice n-0] Your child c-0 reported revision 1.".into()),
        ..agent("o")
    };
    assert!(support::is_delegation_notice_block(&old_delegation), "a delegation notice saved before the rename is still one");
    assert!(
        timeline::kind_label("steer", saved.input.as_deref(), &ko) == "작업 알림"
            && timeline::kind_label("steer", Some("Also add tests"), &ko) == "중간 요청",
        "the timeline names it the same way"
    );
    // Deferred to the view model's port: "task notices are not counted as a person's steers" (MightyGraphBlockModel.ToolbarSummary).
}

#[test]
fn the_request_row_holds_the_input_and_the_execution_row_the_work() {
    let step = |id: &str, summary: &str| LogEntry {
        activity: Some(activity(id, "tool", "running", summary, Some("Bash"))),
        ..entry(id, "system", "tool", "2026-10-04T10:00:00Z")
    };
    let mut r = run("r", "running", None, vec![agent("a")]);
    r.root_entries = Some(vec![step("s1", "rg render"), step("s2", "sed -n 1,40p view.swift")]);
    let made = groups(&[r]);
    let rows = &made[0].rows;
    // The request: what was asked, in its neutral state, with nothing it did.
    assert!(
        rows[0].kind == "main"
            && rows[0].status == timeline::REQUEST_STATUS
            && rows[0].latest.is_none()
            && rows[0].duration_ms.is_none()
            && !rows[0].is_execution
            && rows[0].agent_index.is_none()
            && rows[0].node == timeline::REQUEST_NODE
            && timeline::REQUEST_NODE == Node { tone: Tone::Idle, ring: false, rail: None },
        "the request row is neutral and holds no work"
    );
    // Its 실행 block: the run's status and steps.
    assert!(
        rows[1].kind == timeline::EXECUTION_KIND
            && rows[1].status == "running"
            && rows[1].latest.as_deref() == Some("sed -n 1,40p view.swift")
            && rows[1].is_execution
            && rows[1].agent_index.is_none()
            && rows[1].node == timeline::node_for("running"),
        "the 실행 row holds the work"
    );
    // A request that has nothing yet still has its 실행 row, waiting.
    let fresh = groups(&[run("n", "running", None, vec![])]).remove(0);
    assert!(
        fresh.rows.iter().map(|r| r.is_execution).collect::<Vec<_>>() == [false, true]
            && fresh.rows[1].status == "running"
            && fresh.rows[1].latest.is_none(),
        "a new request has its 실행 row at once"
    );
    // Only the 실행 block counts: one block, none finished, not the request as a finished block.
    assert_eq!(fresh.tally, Tally { total: 1, settled: 0, running: 1 }, "the tally leaves the request out");
    // A request whose work failed or stopped never reads as a success, nor as the failure.
    for (status, tone) in [("error", Tone::Err), ("stopped", Tone::Stop), ("completed", Tone::Done)] {
        let ended = groups(&[run("e", status, None, vec![])]).remove(0);
        assert!(
            ended.rows[0].node.tone == Tone::Idle
                && ended.rows[1].node.tone == tone
                && ended.tally == Tally { total: 1, settled: 1, running: 0 },
            "the request row stays neutral when its work ends: {status}"
        );
    }
}

#[test]
fn status_rails_counts_and_duration_stay_honest() {
    let ko = ko();
    let agents =
        ["completed", "error", "stopped", "running", "waiting"].iter().enumerate().map(|(i, s)| with_status(&format!("a{i}"), s)).collect();
    let group = groups(&[run("mixed", "running", None, agents)]).remove(0);
    // The 실행 block (running) and the five agents; the request is not a block that settles.
    assert_eq!(group.tally, Tally { total: 6, settled: 3, running: 2 }, "tally counts only blocks present");
    // The request's node is neutral and leaves the track; its 실행 block runs.
    assert!(
        timeline::rail_above(&group.rows, 0).is_none()
            && timeline::rail_above(&group.rows, 1).is_none()
            && timeline::rail_above(&group.rows, 2) == Some(Tone::Run)
            && timeline::rail_above(&group.rows, 3) == Some(Tone::Done)
            && timeline::rail_above(&group.rows, 4).is_none()
            && timeline::rail_above(&group.rows, 9).is_none(),
        "only running and completed nodes light the following rail"
    );
    for status in ["idle", "starting", "queued", "future-state"] {
        let node = timeline::node_for(status);
        assert!(node.tone == Tone::Run && node.ring, "unknown in-flight status remains running: {status}");
    }
    let mut timed = run("timed", "completed", None, vec![]);
    timed.root_entries =
        Some(vec![entry("1", "user", "prompt", "2026-10-04T10:00:00Z"), entry("2", "assistant", "answer", "2026-10-04T10:00:01.234Z")]);
    let rows = groups(std::slice::from_ref(&timed)).remove(0).rows;
    assert!(
        rows.len() == 2 && rows[0].duration_ms.is_none() && rows[1].duration_ms == Some(1234.0),
        "settled block reports measured own record span, on the 실행 row"
    );
    timed.status = Some("running".into());
    assert!(groups(&[timed]).remove(0).rows[1].duration_ms.is_none(), "running blocks cannot claim final duration");
    assert!(
        timeline::duration_label(Some(f64::NAN), &ko).is_none()
            && timeline::duration_label(Some(-1.0), &ko).is_none()
            && timeline::duration_label(Some(999.0), &ko).as_deref() == Some("999ms"),
        "invalid duration never reaches labels"
    );
    assert!(
        !timeline::folds(&"👨‍👩‍👧".repeat(600)) && timeline::folds(&"가".repeat(601)) && timeline::folds("1\n2\n3\n4\n5\n6\n7\n8\n9"),
        "result folding counts graphemes or actual lines, not UTF16 units"
    );
    let made = groups(&[run("older", "running", None, vec![]), run("latest", "running", None, vec![])]);
    let mut flipped = HashSet::new();
    let timeline_runs = vec![run("older", "running", None, vec![]), run("latest", "running", None, vec![])];
    assert!(
        !timeline::group_open(&made[0], &timeline_runs, &flipped) && timeline::group_open(&made[1], &timeline_runs, &flipped),
        "latest group starts open; old group retains its result below collapsed rows"
    );
    flipped.insert("latest".to_string());
    assert!(!timeline::group_open(&made[1], &timeline_runs, &flipped), "manual group toggle is independent of row/result expansion");
}

#[test]
fn saved_mode_is_independent_and_lenient() {
    let pane = RunSession {
        id: Some("s".into()),
        workspace_id: Some("w".into()),
        draft: Some("한글 초안".into()),
        agent_view_mode: Some("mighty".into()),
        model: Some("saved-model".into()),
        graph_result_size: Some(GraphBlockSize { width: 700.0, height: 500.0 }),
        ..RunSession::default()
    };
    let selected = timeline::apply_mode(&pane, "timeline");
    let restored = RunSession::read(&serde_json::from_str(&selected.to_json()).unwrap()).unwrap();
    assert!(
        restored.graph_view_mode.as_deref() == Some("timeline")
            && restored.agent_view_mode.as_deref() == Some("mighty")
            && restored.draft == pane.draft
            && restored.graph_result_size == pane.graph_result_size
            && restored.model == pane.model,
        "persisted timeline changes only graph presentation"
    );
    for value in ["\"future\"", "123", "true", "{}", "[]", "null"] {
        let legacy = RunSession::read(&serde_json::from_str(&format!("{{\"id\":\"s\",\"graphViewMode\":{value}}}")).unwrap()).unwrap();
        assert!(
            legacy.graph_view_mode.is_none() && timeline::mode(&legacy) == "diagram",
            "unknown persisted value defaults to diagram: {value}"
        );
    }
    let shell = RunSession { kind: Some("shell".into()), ..pane.clone() };
    assert!(
        timeline::apply_mode(&pane, "unknown") == pane && timeline::apply_mode(&shell, "timeline").graph_view_mode.is_none(),
        "unsupported modes and non-agent panes are not changed"
    );
    let directory = std::env::temp_dir().to_string_lossy().into_owned();
    let corrupt =
        RunSession { id: Some("sh".into()), kind: Some("shell".into()), graph_view_mode: Some("timeline".into()), ..pane.clone() };
    let state = AppSnapshot {
        workspaces: Some(vec![Workspace { id: Some("w".into()), path: Some(directory), ..Workspace::default() }]),
        sessions: Some(vec![Arc::new(selected), Arc::new(corrupt)]),
        ..AppSnapshot::default()
    };
    let normalized = mighty_core::normalize::normalize(&state, true).unwrap();
    let mode_of = |id: &str| normalized.sessions().iter().find(|s| s.id() == id).and_then(|s| s.graph_view_mode.clone());
    assert!(
        mode_of("s").as_deref() == Some("timeline") && mode_of("sh").is_none(),
        "restore retains agent preference and drops shell-only corruption"
    );
}
