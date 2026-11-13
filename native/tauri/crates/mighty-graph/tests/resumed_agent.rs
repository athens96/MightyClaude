//! A background agent Claude resumes with SendMessage is background work of the pane like any other (C# ResumedAgentVerification.cs; macOS
//! ResumedAgentTests). The recording is the shared native/contracts/fixtures/claude-stream-resumed-agent.jsonl: the request launches one
//! background agent, which ends before the request's own result (frames 0–17); the notice turn its end opens resumes it (18–27), and its
//! second end opens the next notice turn (28–35); a second user message on stdin resumes it once more from an ordinary turn (36–52).
//!
//! Only what the graph tracker decides is here: the background task tracker, the input's close, the queue, the strip and the session log
//! strip are the run engine's and the role view's (C# BackgroundTaskTracker, ClaudeStream.ClosesInput, BackgroundQueuePolicy, QueueRunNow,
//! PlanCardSupport, FlowLog).

mod common;

use common::*;
use mighty_core::graph::MightyGraphRun;
use mighty_core::locale::Locale;
use mighty_core::model::RunSession;
use mighty_graph::{node, notice, support};
use serde_json::Value;

/// `ClaudeStream.SessionState(frame)` (OutputParser.cs:679), the run engine's: a top-level `session_state_changed` frame's state.
fn session_state(frame: &Value) -> Option<&str> {
    let top = frame.get("parent_tool_use_id").is_none_or(Value::is_null);
    (text(frame, "type") == Some("system") && text(frame, "subtype") == Some("session_state_changed") && top)
        .then(|| text(frame, "state"))
        .flatten()
        .filter(|s| !s.is_empty() && s.encode_utf16().count() <= 64)
}

#[test]
fn the_recording_is_the_resume_as_claude_code_reports_it() {
    let frames = recording("claude-stream-resumed-agent");
    check(frames.len() == 53, "the recording has its 53 frames");
    let listed = &frames[23]["tasks"][0];
    check(
        text(&frames[23], "subtype") == Some("background_tasks_changed")
            && text(listed, "task_id") == Some("agent1")
            && text(listed, "run_id") == Some("taskrun-2"),
        "the resumed run is listed under the first task id and a run of its own",
    );
    let start = &frames[24];
    check(
        text(start, "subtype") == Some("task_started")
            && text(start, "task_id") == Some("agent1")
            && text(start, "run_id") == Some("taskrun-2")
            && text(start, "tool_use_id") == Some("toolu_3")
            && text(start, "description") == Some("Count one to three"),
        "the resumed run starts naming the SendMessage call, with the first description",
    );
    let end = &frames[30];
    check(
        text(end, "subtype") == Some("task_notification")
            && text(end, "run_id") == Some("taskrun-2")
            && text(end, "tool_use_id") == Some("toolu_3"),
        "its own notification ends it",
    );
    check(text(&frames[34]["origin"], "runId") == Some("taskrun-2"), "its notice turn names the run");
    let idle: Vec<usize> = frames.iter().enumerate().filter(|(_, f)| session_state(f) == Some("idle")).map(|(i, _)| i).collect();
    check(idle == vec![35, 52], &format!("idle comes only after the resumed agent's notice turn: {idle:?}"));
}

/// The graph's half of the user's case (ResumedAgentVerification.AnAgentResumedFromANoticeTurnKeepsThePaneWaitingUntilItsNotification): a
/// notice turn resumes the agent its own notice was about, and Claude ends that turn while the agent works.
#[test]
fn an_agent_resumed_from_a_notice_turn_keeps_the_pane_waiting_until_its_notification() {
    let ko = Locale::new("ko");
    let frames = recording("claude-stream-resumed-agent");
    let mut graph = Pane::with("recorded", None, None, ko);
    for frame in &frames[..=27] {
        graph.send(frame.clone());
    }
    let runs = graph.runs();
    let request = support::newest_request_run(&runs).expect("a request").clone();
    // Its block is the request's, as every block a notice turn makes is (a notice run holds only copies).
    let resumed = request.agents().iter().filter(|a| a.title() == "Count four to six").cloned().collect::<Vec<_>>();
    check(resumed.len() == 1, "one resumed block");
    let resumed = resumed[0].clone();
    check(
        resumed.background == Some(true)
            && resumed.status() == "running"
            && resumed.source_block_id.is_none()
            && resumed.id() == node::agent_node_id("recorded", "toolu_3"),
        "the resumed block is the request's",
    );
    check(
        runs.iter().filter(|r| r.is_notice()).all(|r| r.agents().iter().all(|a| a.source_block_id.is_some())),
        "notice runs hold only copies",
    );
    check(
        notice::awaiting_notice(&request, &runs, &ko).map(|a| a.tasks.iter().map(|t| t.status.clone()).collect::<Vec<_>>())
            == Some(vec!["completed".into(), "running".into()]),
        "the request awaits its notice",
    );
    let pane = RunSession {
        id: Some("p".into()),
        kind: Some("claude".into()),
        provider: Some("claude".into()),
        status: Some("running".into()),
        graph_runs: Some(runs.clone()),
        ..RunSession::default()
    };
    check(notice::awaits_notice(&pane), "the pet and the header read 알림 대기");
    let shown = support::shown_runs(&runs, &ko);
    let flow: Vec<_> = support::flow_agents(&shown[0]);
    check(
        flow.iter().map(|a| a.title()).collect::<Vec<_>>() == vec!["Count one to three", "Count four to six"]
            && flow.iter().map(|a| a.status()).collect::<Vec<_>>() == vec!["completed", "running"],
        "the role view draws it as a running card",
    );
    // Its end: the run's own notification ends it and its copy opens the next notice run.
    for frame in &frames[28..=30] {
        graph.send(frame.clone());
    }
    let ended: Vec<MightyGraphRun> = graph.runs();
    let last = ended.last().expect("a run");
    check(
        ended[0].agents().iter().find(|a| a.id() == resumed.id()).is_some_and(|a| a.status() == "completed")
            && last.is_notice()
            && last.agents().iter().map(|a| a.source_block_id.clone()).collect::<Vec<_>>() == vec![resumed.id.clone()],
        "its copy opens the next notice run",
    );
}
