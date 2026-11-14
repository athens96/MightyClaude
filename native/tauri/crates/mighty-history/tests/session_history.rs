//! C# SessionHistoryVerification.cs (Verification.cs:1296-1309, "session history: …" and "resume picker …"), mirroring the Mac's
//! SessionHistoryTests and ResumableSessionsTests: scrolling the Mighty diagram past its top reads earlier requests from the CLI's own
//! session record, and "창 추가" → "이어가기…" lists a folder's earlier sessions with nested Ouroboros runs hidden.

mod common;

use std::collections::HashSet;
use std::sync::{Arc, Mutex};

use common::*;
use mighty_core::graph::MightyGraphRun;
use mighty_core::locale::Locale;
use mighty_core::model::{AppSnapshot, BackgroundTask, BackgroundWork, LogEntry, RunSession};
use mighty_graph::layout::{self, Options};
use mighty_graph::{ExecutionGraphNode, ExecutionGraphTracker, camera, node, notice, support, view_model};
use mighty_history::history::{
    self, SessionHistoryAnchor, SessionHistoryChunk, SessionHistoryError, SessionHistoryFile, SessionHistoryRequest,
};
use mighty_history::resumable::{self, ResumableSession, ResumableSessionQuery, known};
use mighty_history::scan::{self, HistoryFormat};
use mighty_history::state::{MAXIMUM_RUNS, Phase, SessionHistoryState};
use mighty_history::{Never, replay};
use serde_json::{Value, json};

type Emitted = Arc<Mutex<Vec<ExecutionGraphNode>>>;

fn tracker(run_id: &str, input: &str) -> (ExecutionGraphTracker, Emitted) {
    let emitted: Emitted = Arc::default();
    let sink = emitted.clone();
    let tracker = ExecutionGraphTracker::new(
        run_id,
        Some(input),
        "claude",
        None,
        Box::new(move |n: &ExecutionGraphNode| sink.lock().unwrap().push(n.clone())),
        None,
        None,
        ko(),
    );
    (tracker, emitted)
}

fn latest(emitted: &Emitted) -> std::collections::HashMap<String, ExecutionGraphNode> {
    emitted.lock().unwrap().iter().map(|n| (n.id.clone(), n.clone())).collect()
}

fn load(request: &SessionHistoryRequest) -> Result<SessionHistoryChunk, SessionHistoryError> {
    history::load(request, &ko(), &Never)
}

fn inputs(runs: &[MightyGraphRun]) -> Vec<String> {
    runs.iter().map(|r| r.input().to_string()).collect()
}

/// SessionHistoryVerification.cs:74 `ANoticeTurnsAnswerFollowsTheRequestsLiveAndReplayed`: a request whose own turn ended while its
/// backgrounded command ran reads completed and says it waits on it; the task's notice and the reply to it are a notice run of their
/// own; an empty notice turn adds nothing; a replayed record builds the same runs.
#[test]
fn session_history_a_notice_turns_answer_follows_the_requests_live_and_replayed() {
    let locale = ko();
    let (mut tracker, _) = tracker("wait-run", "ping it");
    let notice = "<task-notification>\n<task-id>b1</task-id>\n<tool-use-id>ping-cmd</tool-use-id>\n<status>completed</status>\n<summary>ping exited 0</summary>\n</task-notification>";
    let launch = json!({"type": "assistant", "message": {"content": [{"type": "tool_use", "id": "ping-cmd", "name": "Bash",
        "input": {"command": "ping -n 30 localhost", "description": "ping", "run_in_background": true}}]}});
    let started = json!({"type": "user", "message": {"content": [{"type": "tool_result", "tool_use_id": "ping-cmd", "content": "Command running in background with ID: b1."}]}});
    tracker.consume(&launch);
    tracker.consume(&started);
    tracker.consume(&json!({"type": "assistant", "message": {"content": [{"type": "text", "text": "Started ping."}]}}));
    tracker.consume(&json!({"type": "result", "subtype": "success", "is_error": false, "result": "Started ping."}));
    let waiting = tracker.build_run(true).expect("a run");
    let label = view_model::run_state_label(&waiting, &locale);
    check(
        waiting.status() == "completed"
            && support::background_waiting(&waiting) == 1
            && support::finished(&waiting)
            && label == "턴 완료 · 백그라운드 1개 실행 중",
        &format!("the request reads completed and waits on its task; got {} / {label}", waiting.status()),
    );
    tracker.consume(&json!({"type": "user", "message": {"content": [{"type": "text", "text": notice}]}}));
    tracker.consume(&json!({"type": "assistant", "message": {"content": [{"type": "text", "text": "Ping finished fine."}]}}));
    tracker.consume(&json!({"type": "result", "subtype": "success", "is_error": false, "result": "Ping finished fine.", "origin": {"kind": "task-notification"}}));
    tracker.consume(
        &json!({"type": "result", "subtype": "success", "is_error": false, "result": " ", "origin": {"kind": "task-notification"}}),
    );
    tracker.finish("completed");
    let mut live = tracker.build_run(false).expect("the request");
    let notices = tracker.build_notice_runs(false);
    check(
        !live.agents().iter().any(support::is_task_notice_block)
            && notices.len() == 1
            && notices[0].is_notice()
            && notices[0].status() == "completed"
            && notices[0].final_output() == Some("Ping finished fine.")
            && notices[0].input() == "ping · 완료",
        "the notice turn is a run of its own whose result is its answer; an empty notice turn adds nothing",
    );
    check(
        live.final_output() == Some("Started ping.")
            && support::background_waiting(&live) == 0
            && view_model::run_state_label(&live, &locale) == "완료",
        &format!("the request's result stays its own; got {:?}", live.final_output()),
    );
    // An older save's result that goes on with a notice section: the main block keeps the request's own answer.
    let expected = format!("Started ping.{}작업 알림 · ping 이후**\n\nPing finished fine.", support::NOTICE_SECTION_MARK);
    live.final_output = Some(expected.clone());
    live.root_entries = Some(vec![
        LogEntry::new("a1".into(), "assistant", "Started ping.".into(), stamp(base()), None),
        LogEntry::new("a2".into(), "assistant", "Ping finished fine.".into(), stamp(base()), None),
    ]);
    mighty_core::normalize::refresh_result(&mut live);
    check(
        live.result_entries().len() == 1 && live.result_entries()[0].text() == expected && live.root_entries().len() == 2,
        "the result block shows the whole result and the main block stays as it was",
    );

    // The transcript after the turn ended still reaches the waiting request's 실행 block.
    let task = BackgroundTask {
        id: Some("b1".into()),
        kind: Some("bash".into()),
        description: Some("ping".into()),
        started_at: Some(stamp(base())),
        status: Some("running".into()),
        tool_use_id: None,
        summary: None,
        ended_at: None,
    };
    let session = RunSession {
        id: Some("p".into()),
        kind: Some("claude".into()),
        provider: Some("claude".into()),
        status: Some("running".into()),
        graph_runs: Some(vec![waiting.clone()]),
        background_work: Some(BackgroundWork { tasks: Some(vec![task]), turn_ended: true }),
        ..RunSession::default()
    };
    let snapshot = AppSnapshot { sessions: Some(vec![Arc::new(session)]), ..AppSnapshot::default() };
    let event = mighty_run::model::RunEvent {
        entry: Some(LogEntry::new("late".into(), "assistant", "Ping finished fine.".into(), stamp(base()), None)),
        ..mighty_run::model::RunEvent::new("p", "log")
    };
    let merged = mighty_run::apply::apply(&snapshot, &event, base());
    let pane = merged.session("p").expect("the pane");
    check(
        pane.graph_runs().last().is_some_and(|r| r.root_entries().iter().any(|e| e.id() == "late")),
        "a notice turn's transcript joins the request that waits on its task",
    );

    let fixture = Fixture::new("notice-replay");
    let id = "7d1e2c1a-1111-4222-8333-444455556666";
    let date = plus_hours(base(), 1.0);
    let cwd = fixture.workspace_text();
    let mut launch_line = launch.clone();
    launch_line["uuid"] = json!("a1");
    launch_line["timestamp"] = json!(stamp(plus_seconds(date, 1.0)));
    let mut started_line = started.clone();
    started_line["toolUseResult"] = json!({});
    started_line["uuid"] = json!("t1");
    started_line["timestamp"] = json!(stamp(plus_seconds(date, 2.0)));
    fixture.claude_record(
        id,
        &[
            claude_user("ping it", date, "u1", &cwd, Value::Null),
            launch_line.to_string(),
            started_line.to_string(),
            claude_answer("Started ping.", plus_seconds(date, 3.0), "a2"),
            claude_user(notice, plus_seconds(date, 30.0), "n1", &cwd, json!({"origin": {"kind": "task-notification"}})),
            claude_answer("Ping finished fine.", plus_seconds(date, 31.0), "a3"),
        ],
        None,
        None,
    );
    let replayed = load(&SessionHistoryRequest::new("claude", id, &cwd).home(fixture.home())).expect("the record").runs;
    check(
        replayed.len() == 2
            && replayed[0].final_output() == Some("Started ping.")
            && replayed[1].is_notice()
            && replayed[1].final_output() == Some("Ping finished fine."),
        &format!(
            "a replayed record builds the live runs; got {:?}",
            replayed.iter().map(|r| (r.input(), r.final_output())).collect::<Vec<_>>()
        ),
    );
}

/// SessionHistoryVerification.cs:135 `ASteerAfterTheTurnFollowsInTheResult`.
#[test]
fn session_history_a_steer_after_the_turn_follows_in_the_result() {
    let (mut tracker, _) = tracker("steer-after", "ping it");
    tracker.steer("early", "Also log it");
    tracker.consume(&json!({"type": "assistant", "message": {"content": [{"type": "text", "text": "Started ping."}]}}));
    tracker.consume(&json!({"type": "result", "subtype": "success", "is_error": false, "result": "Started ping."}));
    tracker.steer("late", "What is the time?");
    tracker.consume(&json!({"type": "assistant", "message": {"content": [{"type": "text", "text": "It is noon."}]}}));
    tracker.consume(&json!({"type": "result", "subtype": "success", "is_error": false, "result": "It is noon."}));
    tracker.finish("completed");
    let run = tracker.build_run(false).expect("a run");
    let late = run.agents().iter().find(|a| a.input() == "What is the time?").expect("the late steer");
    check(
        run.status() == "completed" && late.status() == "completed" && late.entries().iter().any(|e| e.text() == "It is noon."),
        "the steered turn is a 중간 요청 answered by the next root answer",
    );
    let expected = format!("Started ping.{}중간 요청 이후**\n\nIt is noon.", support::NOTICE_SECTION_MARK);
    check(
        run.final_output() == Some(expected.as_str()),
        &format!("one after the result adds its answer under its own line; got {:?}", run.final_output()),
    );
}

/// SessionHistoryVerification.cs:159 `ADelegationNoticeAfterTheTurnOpensItsOwnRun`.
#[test]
fn session_history_a_delegation_notice_after_the_turn_opens_its_own_run() {
    let locale = ko();
    let (mut tracker, emitted) = tracker("deleg-after", "Split the work");
    tracker.consume(&json!({"type": "assistant", "message": {"id": "main-1", "content": [{"type": "text", "text": "Delegated."}]}}));
    tracker.consume(&json!({"type": "result", "subtype": "success", "is_error": false, "result": "Delegated."}));
    let notice_text = "[Mighty Code notice n-1] Your child c-1 reported revision 1. Read the report with child_status; merge it with merge when it is right.";
    tracker.steer("n-1", notice_text);
    tracker.consume(&json!({"type": "assistant", "message": {"id": "main-2", "content": [{"type": "text", "text": "Merged the child."}]}}));
    tracker.consume(&json!({"type": "result", "subtype": "success", "is_error": false, "result": "Merged the child."}));
    let nodes = latest(&emitted);
    check(nodes[&node::main_node_id("deleg-after")].output.as_deref() == Some("Delegated."), "the request's result is untouched");
    let notice = nodes.get(&node::main_node_id(&notice::run_id("deleg-after", 1))).expect("the delegation notice opens a run");
    check(
        notice.title == locale.get("graph.block.delegationNotice") && notice.input.as_deref() == Some(notice_text),
        &format!("the 위임 알림 run names the notice and holds it; got '{}' / '{:?}'", notice.title, notice.input),
    );
    check(notice.output.as_deref() == Some("Merged the child.") && notice.state == "completed", "the run's result is the answer");
    let steer = &nodes[&node::identifier("deleg-after", "steer:n-1")];
    check(
        steer.run_id == "deleg-after" && steer.output.as_deref() == Some("Merged the child."),
        "the 위임 알림 block stays in the request and takes the reply",
    );
    check(!emitted.lock().unwrap().iter().any(|n| n.run_id == notice::run_id("deleg-after", 2)), "no second run opens");
    tracker.finish("completed");
    let runs = tracker.build_notice_runs(false);
    check(
        runs.len() == 1
            && runs[0].is_notice()
            && runs[0].notice_title.as_deref() == Some(locale.get("graph.block.delegationNotice").as_str()),
        "the projection carries the run's title",
    );
}

/// SessionHistoryVerification.cs:190 `ADelegationNoticeAtTheNodeCapFollowsInTheResult`.
#[test]
fn session_history_a_delegation_notice_at_the_node_cap_follows_in_the_result() {
    let locale = ko();
    let (mut tracker, emitted) = tracker("deleg-full", "Split the work");
    for index in 1..node::MAXIMUM_NODES - 1 {
        tracker.consume(&json!({"type": "system", "subtype": "compact_boundary", "uuid": format!("c{index}")}));
    }
    tracker.consume(&json!({"type": "result", "subtype": "success", "is_error": false, "result": "Delegated."}));
    tracker.steer("n-1", "[Mighty Code notice n-1] Your child c-1 reported revision 1.");
    tracker.consume(&json!({"type": "assistant", "message": {"id": "main-2", "content": [{"type": "text", "text": "Merged the child."}]}}));
    tracker.consume(&json!({"type": "result", "subtype": "success", "is_error": false, "result": "Merged the child."}));
    let nodes = latest(&emitted);
    check(
        nodes.len() == node::MAXIMUM_NODES && !nodes.values().any(|n| notice::is_notice_run(&n.run_id)),
        &format!("the graph is full and no notice run opened; got {}", nodes.len()),
    );
    let header = locale.get_with("graph.result.afterSteer", &[("block", &locale.get("graph.block.delegationNotice"))]);
    let output = nodes[&node::main_node_id("deleg-full")].output.clone();
    check(
        output.as_deref() == Some(format!("Delegated.{}{header}**\n\nMerged the child.", support::NOTICE_SECTION_MARK).as_str()),
        &format!("the answer follows under 위임 알림 and keeps the result; got {output:?}"),
    );
    check(
        nodes[&node::identifier("deleg-full", "steer:n-1")].output.as_deref() == Some("Merged the child."),
        "the 위임 알림 block takes the reply",
    );
}

/// SessionHistoryVerification.cs:216 `AMessageAnswerWithNoRoomNeverBecomesASectionOfTheResult`.
#[test]
fn session_history_a_message_answer_with_no_room_never_becomes_a_section_of_the_result() {
    let locale = ko();
    let agent = "b2c3d4e5f6a7b8c9d";
    let replay = |filling: usize| {
        let (mut tracker, emitted) = tracker("msg-full", "Find the config");
        tracker.consume(
            &json!({"type": "assistant", "message": {"id": "m-1", "content": [{"type": "tool_use", "id": "toolu_scout", "name": "Agent",
            "input": {"description": "Find the config", "prompt": "Find it.", "name": "scout", "run_in_background": true}}]}}),
        );
        tracker.consume(&json!({"type": "user", "message": {"content": [{"type": "tool_result", "tool_use_id": "toolu_scout",
            "content": format!("Async agent launched successfully.\nagentId: {agent} (internal ID)")}]}}));
        for index in 0..filling {
            tracker.consume(&json!({"type": "system", "subtype": "compact_boundary", "uuid": format!("c{index}")}));
        }
        tracker.consume(&json!({"type": "result", "subtype": "success", "is_error": false, "result": "Scouting."}));
        tracker.consume(&json!({"type": "user", "origin": {"kind": "peer", "from": agent, "senderTaskId": agent},
            "message": {"role": "user", "content": format!("Another Claude session sent a message:\n<agent-message from=\"{agent}\">\nFound the config.\n</agent-message>")}}));
        tracker.consume(&json!({"type": "assistant", "message": {"id": "m-2", "content": [{"type": "text", "text": "Good."}]}}));
        tracker.consume(&json!({"type": "result", "subtype": "success", "is_error": false, "result": "Good.", "origin": {"kind": "peer"}}));
        latest(&emitted)
    };
    let (main, agent_id) = (node::main_node_id("msg-full"), node::agent_node_id("msg-full", "toolu_scout"));
    // The main block, the agent and compactions up to the reserve: the message run takes a kept node.
    let reserved = replay(node::MAXIMUM_NODES - 16 - 2);
    let run = reserved.get(&node::main_node_id(&notice::run_id("msg-full", 1))).expect("the message opens a run");
    check(
        run.title == format!("{} · scout", locale.get("graph.background.messageRun"))
            && run.output.as_deref() == Some("Good.")
            && run.input.as_deref() == Some("Found the config.")
            && reserved[&main].output.as_deref() == Some("Scouting."),
        &format!("the message run takes a kept node; got '{}' / '{:?}'", run.title, run.output),
    );
    // Every node taken: the answer joins the agent's log, the request's result stays.
    let full = replay(node::MAXIMUM_NODES - 2);
    let reply = format!("**{}**\n\nGood.", locale.get("graph.background.reply"));
    check(
        full.len() == node::MAXIMUM_NODES
            && !full.values().any(|n| notice::is_notice_run(&n.run_id))
            && full[&main].output.as_deref() == Some("Scouting.")
            && full[&agent_id].entries.iter().any(|e| e.kind() == "assistant" && e.text() == reply),
        &format!("at the very cap the answer joins the agent's log; got {:?}", full[&main].output),
    );
}

/// SessionHistoryVerification.cs:251 `ScrollingUpLoadsTheTenRequestsAboveTheRetainedOneFromAClaudeRecord`.
#[test]
fn session_history_scrolling_up_loads_the_ten_requests_above_the_retained_one_from_a_claude_record() {
    let fixture = Fixture::new("scroll");
    let id = "0b9e2c1a-1111-4222-8333-444455556666";
    let path = fixture.claude_record(id, &claude_conversation(&fixture.workspace_text(), 15), None, None);
    // A line still being written past the last newline is never read.
    let mut text = std::fs::read(&path).unwrap();
    text.extend_from_slice(b"{\"type\":\"user\",\"message\":{\"content\":\"half");
    std::fs::write(&path, text).unwrap();

    let located = history::locate("claude", id, &fixture.workspace_text(), &Default::default(), &fixture.home());
    check(
        located.is_some_and(|p| p.to_string_lossy().ends_with(&format!("{id}.jsonl"))),
        "the Claude record is found under projects/<escaped cwd>",
    );
    let mut state = SessionHistoryState::new();
    let template = SessionHistoryRequest::new("claude", id, &fixture.workspace_text()).home(fixture.home());
    let anchor = SessionHistoryAnchor::new("request 13", Some(plus_seconds(plus_hours(base(), 13.0), -1.0)));
    let request = state.begin(Some("run-13"), Some(id), Some(anchor.clone()), &template).expect("a request");
    check(
        state.phase == Phase::Loading && request.anchor.as_ref() == Some(&anchor) && request.end.is_none(),
        "the first load starts at the end above the retained request",
    );
    let first = load(&request).expect("a chunk");
    check(first.runs.len() == history::TURNS_PER_CHUNK, &format!("a chunk is ten requests, found {}", first.runs.len()));
    check(
        inputs(&first.runs) == (3..=12).map(|i| format!("request {i}")).collect::<Vec<_>>(),
        &format!("requests 3-12, oldest first: {:?}", inputs(&first.runs)),
    );
    check(
        first.runs.iter().all(|r| r.id().starts_with(replay::RUN_PREFIX) && r.status() == "completed" && r.provider() == Some("claude")),
        "replayed runs are record- runs that completed",
    );
    check(first.runs.last().and_then(|r| r.final_output()) == Some("answer 12"), "the result is the request's last answer");
    check(!first.reached_start && first.end > 0, "the record goes on above the chunk");
    let (first_end, first_file) = (first.end, first.file.clone());
    let generation = state.generation;
    check(
        !state.finish(Ok(first), generation) && state.phase == Phase::Idle && state.runs.len() == 10,
        "the chunk is kept and more can load",
    );

    let next = state.begin(Some("run-13"), Some(id), Some(anchor.clone()), &template).expect("the next request");
    check(
        next.anchor.is_none() && next.end == Some(first_end) && next.file == Some(first_file),
        "the next load continues above the first chunk in the same file",
    );
    let second = load(&next).expect("the rest");
    check(second.reached_start && inputs(&second.runs) == ["request 1", "request 2"], "the rest of the record reaches its start");
    let generation = state.generation;
    state.finish(Ok(second), generation);
    check(
        state.phase == Phase::Start && inputs(&state.runs) == (1..=12).map(|i| format!("request {i}")).collect::<Vec<_>>(),
        "older requests go first",
    );
    check(
        state.begin(Some("run-13"), Some(id), Some(anchor), &template).is_none(),
        "nothing more loads once the record's start is on screen",
    );
    let locale = ko();
    check(
        state.block_text(state.runs.len(), &locale)
            == format!("{} · {}", locale.get("graph.history.start"), locale.get_with("graph.history.loaded", &[("count", "12")])),
        "the history block uses the shared locale keys",
    );
}

/// SessionHistoryVerification.cs:285 `AReplacedRecordStartsOverAndAPaneWithoutASessionHasNone`.
#[test]
fn session_history_a_replaced_record_starts_over_and_a_pane_without_a_session_has_none() {
    let fixture = Fixture::new("replaced");
    let id = "1c9e2c1a-1111-4222-8333-444455556666";
    let path = fixture.claude_record(id, &claude_conversation(&fixture.workspace_text(), 3), None, None);
    let file = history::identify(&path).expect("the record");
    let mut past = SessionHistoryRequest::new("claude", id, &fixture.workspace_text()).home(fixture.home());
    past.file = Some(file);
    past.end = Some(std::fs::metadata(&path).unwrap().len() as i64 + 10);
    check(load(&past).err() == Some(SessionHistoryError::Changed), "a shortened record is a changed record");
    let mut state = SessionHistoryState::new();
    state.begin(None, Some(id), None, &SessionHistoryRequest::new("claude", id, &fixture.workspace_text()));
    let generation = state.generation;
    check(
        state.finish(Err(SessionHistoryError::Changed), generation) && state.phase == Phase::Idle && state.generation == generation + 1,
        "a replaced record resets the pane's history and loads again",
    );
    check(!state.finish(Err(SessionHistoryError::Unreadable), generation), "a result from before the reset is ignored");
    let mut none = SessionHistoryState::new();
    check(
        none.begin(None, None, None, &SessionHistoryRequest::new("claude", "", &fixture.workspace_text())).is_none()
            && none.phase == Phase::Unavailable,
        "a pane that resumes nothing has no record to read",
    );
    check(none.block_text(0, &ko()) == ko().get("graph.history.none"), "and says so");
    check(
        load(&SessionHistoryRequest::new("shell", id, &fixture.workspace_text()).home(fixture.home())).err()
            == Some(SessionHistoryError::Unsupported),
        "a shell keeps no record the graph reads",
    );
    check(
        load(&SessionHistoryRequest::new("gemini", id, &fixture.workspace_text()).home(fixture.home())).err()
            == Some(SessionHistoryError::Missing),
        "an Antigravity conversation without its transcript has nothing to read",
    );
}

/// SessionHistoryVerification.cs:308 `OnlyWhatTheUserTypedIsARequest`.
#[test]
fn session_history_only_what_the_user_typed_is_a_request() {
    let cwd = "/w";
    let line = |text: String| -> Value { serde_json::from_str(&text).unwrap() };
    let prompt = |text: &str, extra: Value| scan::claude_prompt(&line(claude_user(text, base(), "u", cwd, extra)));
    check(prompt("fix it", Value::Null).as_deref() == Some("fix it"), "-p stream input has no origin and is a request");
    check(prompt("typed", json!({"origin": {"kind": "human"}})).as_deref() == Some("typed"), "a human origin is a request");
    check(prompt("note", json!({"origin": {"kind": "task-notification"}})).is_none(), "an injected notification is not");
    check(prompt("peer", json!({"turnOrigin": "peer"})).is_none(), "a peer agent's message is not");
    check(prompt("meta", json!({"isMeta": true})).is_none(), "a meta line is not");
    check(prompt("side", json!({"isSidechain": true})).is_none(), "a sub-agent line is not");
    check(prompt("[Request interrupted by user]", Value::Null).is_none(), "an interruption is not");
    check(prompt("<local-command-stdout>Set model</local-command-stdout>", Value::Null).is_none(), "a local command's output is not");
    check(
        prompt("<command-name>/review</command-name>\n<command-args>main</command-args>", Value::Null).as_deref() == Some("/review main"),
        "a slash command shows as typed",
    );
    check(
        scan::claude_prompt(
            &json!({"type": "user", "message": {"content": [{"type": "tool_result", "tool_use_id": "t", "content": "x"}]}}),
        )
        .is_none(),
        "a tool result is not",
    );
    check(
        scan::local_command_output("<local-command-stdout>\u{1b}[1mSet model to opus\u{1b}[22m</local-command-stdout>").as_deref()
            == Some("Set model to opus"),
        "a local command's output loses its colour codes",
    );
    // Codex: the item beats the event, which beats the response; injected context never titles.
    let bytes = |value: Value| value.to_string().into_bytes();
    let injected = bytes(
        json!({"type": "response_item", "payload": {"type": "message", "role": "user", "content": [{"type": "input_text", "text": "<environment_context>x</environment_context>"}]}}),
    );
    let response = bytes(
        json!({"type": "response_item", "payload": {"type": "message", "role": "user", "content": [{"type": "input_text", "text": "from response"}]}}),
    );
    let evented = bytes(json!({"type": "event_msg", "payload": {"type": "user_message", "message": "from event"}}));
    let item = bytes(
        json!({"type": "event_msg", "payload": {"type": "item_completed", "item": {"id": "i", "type": "UserMessage", "content": [{"text": "from item"}]}}}),
    );
    check(scan::codex_prompt(std::slice::from_ref(&injected)).is_none(), "injected context is not a request");
    check(scan::codex_prompt(&[injected.clone(), response.clone()]).as_deref() == Some("from response"), "the first real user message");
    check(scan::codex_prompt(&[response.clone(), evented.clone()]).as_deref() == Some("from event"), "the event beats the response item");
    check(scan::codex_prompt(&[response, evented, item]).as_deref() == Some("from item"), "the UserMessage item beats both");
}

/// SessionHistoryVerification.cs:336 `CodexRolloutsReplayThroughTheLiveTracker`.
#[test]
fn session_history_codex_rollouts_replay_through_the_live_tracker() {
    let fixture = Fixture::new("codex");
    let thread = "2d9e2c1a-1111-4222-8333-444455556666";
    let mut lines = vec![json!({"timestamp": stamp(base()), "type": "session_meta", "payload": {"id": thread, "cwd": fixture.workspace_text(), "source": "exec"}}).to_string()];
    for i in 1..=3 {
        let date = plus_hours(base(), i as f64);
        lines.push(json!({"timestamp": stamp(date), "type": "turn_context", "payload": {"model": "gpt-5-codex"}}).to_string());
        lines.push(
            json!({"timestamp": stamp(date), "type": "event_msg", "payload": {"type": "task_started", "turn_id": format!("turn-{i}")}})
                .to_string(),
        );
        lines.push(json!({"timestamp": stamp(date), "type": "event_msg", "payload": {"type": "user_message", "message": format!("codex request {i}")}}).to_string());
        lines.push(json!({"timestamp": stamp(plus_seconds(date, 2.0)), "type": "event_msg", "payload": {"type": "item_completed", "item": {"id": format!("cmd-{i}"),
            "type": "CommandExecution", "command": ["bash", "-lc", "ls"], "aggregated_output": "a.txt", "exit_code": 0}}}).to_string());
        lines.push(json!({"timestamp": stamp(plus_seconds(date, 3.0)), "type": "event_msg", "payload": {"type": "item_completed", "item": {"id": format!("msg-{i}"),
            "type": "AgentMessage", "content": [{"text": format!("codex answer {i}")}]}}}).to_string());
        lines.push(
            json!({"timestamp": stamp(plus_seconds(date, 4.0)), "type": "event_msg", "payload": {"type": "task_complete"}}).to_string(),
        );
    }
    fixture.codex_record(thread, &lines, None);
    let chunk = load(&SessionHistoryRequest::new("codex", thread, &fixture.workspace_text()).home(fixture.home())).expect("the rollout");
    check(
        chunk.reached_start && inputs(&chunk.runs) == ["codex request 1", "codex request 2", "codex request 3"],
        &format!("every turn is a request: {:?}", inputs(&chunk.runs)),
    );
    check(
        chunk.runs.iter().all(|r| r.provider() == Some("codex") && r.status() == "completed" && r.id().starts_with("record-turn-")),
        "Codex runs complete and are keyed by turn",
    );
    check(chunk.runs.last().and_then(|r| r.final_output()) == Some("codex answer 3"), "the agent message is the result");
    check(chunk.runs[0].node_model_label.as_deref().is_some_and(|l| !l.is_empty()), "the turn's model labels the request");
    // The newest request is retained on screen: only the two above it load.
    let mut anchored = SessionHistoryRequest::new("codex", thread, &fixture.workspace_text()).home(fixture.home());
    anchored.anchor = Some(SessionHistoryAnchor::new("codex request 3", Some(plus_hours(base(), 3.0))));
    check(
        inputs(&load(&anchored).expect("a chunk").runs) == ["codex request 1", "codex request 2"],
        "the chunk starts right above the retained request",
    );
}

fn run(id: &str) -> MightyGraphRun {
    MightyGraphRun { id: Some(id.into()), input: Some(id.into()), status: Some("completed".into()), ..MightyGraphRun::default() }
}

/// SessionHistoryVerification.cs:363 `LoadedRunsStayBoundedAndFollowATrim`.
#[test]
fn session_history_loaded_runs_stay_bounded_and_follow_a_trim() {
    let file = SessionHistoryFile { path: "/r".into(), created: 1 };
    let mut state = SessionHistoryState::new();
    state.begin(Some("c"), Some("s"), None, &SessionHistoryRequest::new("claude", "s", "/w"));
    let generation = state.generation;
    state.finish(
        Ok(SessionHistoryChunk {
            runs: (0..120).map(|i| run(&format!("record-{i}"))).collect(),
            file: file.clone(),
            end: 5,
            reached_start: false,
        }),
        generation,
    );
    check(
        state.runs.len() == MAXIMUM_RUNS && state.phase == Phase::Limit,
        "a pane keeps at most 100 older requests and then stops loading",
    );
    check(state.runs[0].id() == "record-20", "the oldest go first");
    check(state.block_text(100, &ko()).starts_with(&ko().get("graph.history.limit")), "the block says the pane shows no more");

    let mut trimmed = SessionHistoryState::new();
    trimmed.begin(Some("a"), Some("s"), None, &SessionHistoryRequest::new("claude", "s", "/w"));
    let generation = trimmed.generation;
    trimmed.finish(Ok(SessionHistoryChunk { runs: vec![run("record-1")], file, end: 5, reached_start: false }), generation);
    trimmed.follow(&[run("a"), run("b"), run("c")], &[run("c")], Some("s"), "claude");
    check(
        trimmed.runs.iter().map(|r| r.id()).collect::<Vec<_>>() == ["record-1", "a", "b"]
            && trimmed.anchor_run_id.as_deref() == Some("c")
            && trimmed.pinned_run_id.as_deref() == Some("a"),
        "runs a trim drops move into the history, so nothing is wiped",
    );
    check(
        trimmed.connects(Some("c"), Some("s")) && !trimmed.connects(Some("c"), Some("other")),
        "history attaches only above its own session",
    );
    trimmed.reconcile(Some("c"), Some("other"));
    check(trimmed.runs.is_empty(), "resuming another session drops what no longer connects");
}

/// Every projection of a pane takes its loaded runs (W4 review C2): they are shared, never copied run by run, a pane record whose
/// first run did not change leaves them as they are, and a change made while a projection still holds the list leaves that list whole.
#[test]
fn session_history_a_projection_takes_the_loaded_runs_without_copying_them() {
    let file = SessionHistoryFile { path: "/r".into(), created: 1 };
    let mut state = SessionHistoryState::new();
    state.begin(Some("c"), Some("s"), None, &SessionHistoryRequest::new("claude", "s", "/w"));
    let generation = state.generation;
    state.finish(Ok(SessionHistoryChunk { runs: vec![run("record-1"), run("record-2")], file, end: 5, reached_start: false }), generation);
    let shown = state.runs.clone();
    check(Arc::ptr_eq(&shown, &state.runs), "a projection shares the list");
    state.follow(&[run("c")], &[run("c")], Some("s"), "claude");
    check(Arc::ptr_eq(&shown, &state.runs), "the same first run: nothing followed, nothing copied");
    state.follow(&[run("a"), run("c")], &[run("c")], Some("s"), "claude");
    check(inputs(&shown) == ["record-1", "record-2"], "the list a projection holds is not changed under it");
    drop(shown);
    let again = state.runs.clone();
    check(Arc::strong_count(&again) == 2, "only the state and this projection hold the runs");
}

/// SessionHistoryVerification.cs:384 `TheLayoutStacksLoadedRequestsAboveWithoutMovingTheRetainedOne`.
#[test]
fn session_history_the_layout_stacks_loaded_requests_above_without_moving_the_retained_one() {
    let locale = ko();
    let done = |id: &str| MightyGraphRun {
        final_output: Some("done".into()),
        result_entries: Some(vec![LogEntry::new(format!("{id}-r"), "assistant", "done".into(), stamp(base()), None)]),
        ..run(id)
    };
    let retained = vec![done("live-1"), done("live-2")];
    let expanded = HashSet::new();
    let plain = layout::make(&retained, "", false, &expanded, &Options::default(), &locale);
    let older = [done("record-1"), done("record-2")];
    let all: Vec<MightyGraphRun> = older.iter().chain(&retained).cloned().collect();
    let stacked =
        layout::make(&all, "", false, &expanded, &Options { retained_start: older.len(), history: true, ..Options::default() }, &locale);
    let frame = |layout: &layout::Layout, id: &str| layout.node(id).expect(id).frame;
    let request = support::node_id(&retained[0], "request");
    check(frame(&plain, &request) == frame(&stacked, &request), "loading older requests moves nothing already on screen");
    check(
        frame(&stacked, &support::node_id(&older[1], "request")).max_y() < frame(&stacked, &request).y,
        "older requests sit above the retained one",
    );
    let history = &stacked.nodes[0];
    let lowest = stacked.nodes[1..].iter().map(|n| n.frame.y).fold(f64::INFINITY, f64::min);
    check(
        history.id == layout::HISTORY_NODE_ID && history.kind == "history" && history.frame.max_y() < lowest,
        "the history block is the top of the diagram",
    );
    check(stacked.origin_y < 0.0 && stacked.origin_y <= history.frame.y - 24.0, "the canvas starts above the history block");
    check(plain.origin_y == 0.0 && !plain.nodes.iter().any(|n| n.kind == "history"), "without history nothing changes");
    check(camera::is_auxiliary(layout::HISTORY_NODE_ID), "the camera never aims at the history block");
    check(
        camera::shows_top(-(history.frame.y - 4.0) * 1.5, 1.5, history.frame.y, 8.0),
        "the top shows once the view reaches the history block",
    );
    check(!camera::shows_top(-(history.frame.y + 200.0) * 1.5, 1.5, history.frame.y, 8.0), "further down it does not");
}

fn query(fixture: &Fixture, now: mighty_core::time::DateTimeOffset) -> ResumableSessionQuery {
    let mut query = ResumableSessionQuery::new(&fixture.workspace_text());
    query.home = fixture.home();
    query.now = now;
    query
}

fn listing(query: &ResumableSessionQuery, provider: &str) -> mighty_history::ResumableSessionListing {
    resumable::listing_of(query, provider, &Never)
}

/// SessionHistoryVerification.cs:405 `ResumePickerHidesNestedOuroborosRunsUnlessEverySessionIsShown`.
#[test]
fn resume_picker_hides_nested_ouroboros_runs_unless_every_session_is_shown() {
    let fixture = Fixture::new("nested");
    let now = plus_days(base(), 2.0);
    let id = |n: u32| format!("{n:08x}-1111-4222-8333-444455556666");
    let cwd = fixture.workspace_text();
    fixture.claude_record(
        &id(1),
        &[claude_user("add a login page", base(), "u1", &cwd, Value::Null), claude_answer("done", plus_seconds(base(), 3.0), "a1")],
        None,
        Some(plus_hours(now, -1.0)),
    );
    fixture.claude_record(
        &id(2),
        &[claude_user("User: plan the seed\n\nAssistant: ok", base(), "u2", &cwd, Value::Null)],
        None,
        Some(plus_hours(now, -2.0)),
    );
    fixture.claude_record(
        &id(3),
        &[claude_user("Assistant: evaluate", base(), "u3", &cwd, Value::Null)],
        None,
        Some(plus_hours(now, -3.0)),
    );
    let query = query(&fixture, now);
    let found = listing(&query, "claude");
    check(
        found.items.iter().map(|i| i.session_id.clone()).collect::<Vec<_>>() == [id(1)] && found.hidden == 2,
        "nested Ouroboros runs are hidden by default",
    );
    check(
        found.items[0].title.as_deref() == Some("add a login page")
            && found.items[0].requests == Some(1)
            && found.items[0].model.as_deref() == Some("claude-opus-4-5"),
        "the row shows the first request, the request count and the model",
    );
    let all = listing(&ResumableSessionQuery { include_automated: true, ..query.clone() }, "claude");
    check(
        all.items.len() == 3 && all.hidden == 0 && all.items.iter().filter(|i| i.automated).count() == 2,
        "모든 세션 보기 lists them marked as automated",
    );
    check(resumable::details(&all.items[1], now, &ko()).contains(&ko().get("resume.automated")), "an automated row says so");
    let known = listing(&ResumableSessionQuery { known: HashSet::from([id(2).to_lowercase()]), ..query.clone() }, "claude");
    check(
        known.items.iter().map(|i| i.session_id.clone()).collect::<Vec<_>>() == [id(1), id(2)] && known.hidden == 1,
        "a session the app's own pane started is never hidden",
    );
    check(
        resumable::automated_prompt(Some("User: x"))
            && resumable::automated_prompt(Some("Assistant:\nx"))
            && !resumable::automated_prompt(Some("Users: x"))
            && !resumable::automated_prompt(None),
        "the first-prompt rule",
    );
}

/// SessionHistoryVerification.cs:426 `ResumePickerListsOnlyThisFoldersSessionsNewestFirstMinusOpenPanes`.
#[test]
fn resume_picker_lists_only_this_folders_sessions_newest_first_minus_open_panes() {
    let fixture = Fixture::new("folder");
    let now = plus_days(base(), 2.0);
    let id = |n: u32| format!("{n:08x}-2222-4222-8333-444455556666");
    let cwd = fixture.workspace_text();
    let other = fixture.root.join("work").join("project-other").to_string_lossy().into_owned();
    fixture.claude_record(&id(1), &[claude_user("older", base(), "u1", &cwd, Value::Null)], None, Some(plus_hours(now, -5.0)));
    fixture.claude_record(&id(2), &[claude_user("newer", base(), "u2", &cwd, Value::Null)], None, Some(plus_seconds(now, -60.0)));
    fixture.claude_record(&id(3), &[claude_user("open in a pane", base(), "u3", &cwd, Value::Null)], None, Some(plus_hours(now, -1.0)));
    // Two paths can escape to one folder name: the record's cwd decides.
    fixture.claude_record(
        &id(4),
        &[claude_user("another folder", base(), "u4", &other, Value::Null)],
        Some(&cwd),
        Some(plus_hours(now, -1.0)),
    );
    fixture.claude_record(&id(5), &[claude_user("too old", base(), "u5", &cwd, Value::Null)], None, Some(plus_days(now, -61.0)));
    fixture.claude_record("agent-abc", &[claude_user("not a session id", base(), "u6", &cwd, Value::Null)], None, Some(now));
    let thread = "3d9e2c1a-1111-4222-8333-444455556666";
    let child = "4d9e2c1a-1111-4222-8333-444455556666";
    fixture.codex_record(
        thread,
        &[
            json!({"type": "session_meta", "payload": {"id": thread, "cwd": cwd}}).to_string(),
            json!({"type": "event_msg", "payload": {"type": "task_started", "turn_id": "t"}}).to_string(),
            json!({"type": "event_msg", "payload": {"type": "user_message", "message": "codex work"}}).to_string(),
        ],
        Some(plus_hours(now, -2.0)),
    );
    fixture.codex_record(
        child,
        &[json!({"type": "session_meta", "payload": {"id": child, "cwd": cwd, "source": {"subagent": "review"}}}).to_string()],
        Some(plus_hours(now, -1.0)),
    );
    let panes = [
        RunSession {
            kind: Some("claude".into()),
            provider: Some("claude".into()),
            resume_id: Some(id(3).to_uppercase()),
            ..RunSession::default()
        },
        RunSession { kind: Some("shell".into()), resume_id: Some(id(2)), ..RunSession::default() },
    ];
    let query = ResumableSessionQuery { excluding: resumable::in_use(panes.iter()), ..query(&fixture, now) };
    let claude = listing(&query, "claude");
    check(
        claude.items.iter().map(|i| i.title.clone().unwrap_or_default()).collect::<Vec<_>>() == ["newer", "older"],
        &format!("this folder's sessions, newest first, minus open panes, other folders, old and non-session records: {:?}", claude.items),
    );
    check(
        resumable::may_be_running(&claude.items[0], now)
            && resumable::details(&claude.items[0], now, &ko()).contains(&ko().get("resume.recentlyModified")),
        "a record written in the last two minutes may still be running",
    );
    let codex = listing(&query, "codex");
    check(
        codex.items.iter().map(|i| i.session_id.as_str()).collect::<Vec<_>>() == [thread]
            && codex.items[0].title.as_deref() == Some("codex work"),
        "Codex lists its own thread, never a sub-agent's",
    );
    check(
        listing(&query, "gemini").items.is_empty()
            && resumable::offers_resume("claude", "gemini")
            && resumable::offers_resume("claude", "codex")
            && !resumable::offers_resume("shell", "claude"),
        "Antigravity asks when agy has a conversation here (none: agy never ran); terminals and browsers never ask",
    );
    check(
        listing(&ResumableSessionQuery { head_only: true, ..query.clone() }, "claude")
            .items
            .iter()
            .all(|i| i.requests.is_none() && i.model.is_none()),
        "the menu's look-up reads only the head",
    );
    check(
        resumable::filter(&claude.items, "NEW").iter().map(|i| i.title.clone().unwrap_or_default()).collect::<Vec<_>>() == ["newer"]
            && resumable::filter(&claude.items, " ").len() == 2,
        "search keeps titles with every word, ignoring case",
    );
}

/// SessionHistoryVerification.cs:456 `ResumingContinuesTheSessionInANewPaneAndRemembersIt`.
#[test]
fn resume_picker_continues_the_session_in_a_new_pane_and_remembers_it() {
    let locale: Locale = ko();
    let item = ResumableSession {
        provider: "codex".into(),
        session_id: "5d9e2c1a-1111-4222-8333-444455556666".into(),
        title: Some("a very long first request that goes on well past the forty character pane title".into()),
        modified: base(),
        requests: Some(3),
        model: Some("gpt-5-codex".into()),
        path: "/r".into(),
        automated: false,
    };
    let pane =
        resumable::apply(&item, &RunSession { title: Some("Claude".into()), title_mode: Some("fixed".into()), ..RunSession::default() });
    check(
        pane.provider() == "codex"
            && pane.resume_id.as_deref() == Some(item.session_id.as_str())
            && pane.title == mighty_core::normalize::shortened(item.title.as_deref().unwrap())
            && pane.title_mode.as_deref() == Some("auto"),
        "the new pane resumes the session, titled after its first request, title still automatic",
    );
    let untitled = ResumableSession { title: None, ..item.clone() };
    check(
        resumable::apply(&untitled, &RunSession { title: Some("Codex".into()), ..RunSession::default() }).title.as_deref() == Some("Codex")
            && resumable::row_title(&untitled, &locale) == locale.get("resume.untitled"),
        "an untitled session keeps the pane's name",
    );
    check(
        resumable::relative_time(base(), plus_seconds(base(), 30.0), &locale) == locale.get("resume.time.now")
            && resumable::relative_time(base(), plus_seconds(base(), 300.0), &locale)
                == locale.get_with("resume.time.minutes", &[("count", "5")])
            && resumable::relative_time(base(), plus_hours(base(), 3.0), &locale)
                == locale.get_with("resume.time.hours", &[("count", "3")])
            && resumable::relative_time(base(), plus_days(base(), 12.0), &locale)
                == locale.get_with("resume.time.days", &[("count", "12")]),
        "relative times use the shared locale keys",
    );
    let root = temp("known");
    let path = root.join("state").join(known::FILE_NAME);
    check(known::load(&path).is_empty(), "no file, nothing known");
    let ids = known::adding("abc", &[]).expect("a new id");
    check(known::adding("ABC", &ids).is_none() && known::adding("../x", &ids).is_none(), "an id is kept once, and only a valid id");
    let many: Vec<String> = (0..known::MAXIMUM).map(|i| format!("id{i}")).collect();
    let bounded = known::adding("newest", &many).expect("added");
    check(
        bounded.len() == known::MAXIMUM && bounded.last().map(String::as_str) == Some("newest") && bounded[0] == "id1",
        "past 2,000 the oldest goes",
    );
    known::save(&bounded, &path).expect("saved");
    check(known::load(&path) == bounded, "known-sessions.json reads back");
    check(
        resumable::one_line("one\n two   three") == "one two three" && resumable::one_line(&"x".repeat(300)).chars().count() == 201,
        "titles are one line of at most 200 characters",
    );
    let _ = std::fs::remove_dir_all(root);
    let _ = HistoryFormat::claude();
}
