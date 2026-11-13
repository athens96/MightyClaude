//! The two C# AgentNoticeVerification checks that read a written Claude record back with `SessionHistory.Load` (AgentNoticeVerification.cs
//! :280-296 `Replay`, :308 `ARecordedHandBackTurnSettlesTheAgentAsLive`, :338 `AQueuedHandBackSettlesTheAgentByItsName`), left for this
//! stage by mighty-graph (DEFERRED.md); its shared cases are mighty-graph's tests/agent_notices.rs. macOS AgentNoticeTests holds the same.

mod common;

use common::*;
use mighty_core::graph::{MightyGraphAgent, MightyGraphRun};
use mighty_history::Never;
use mighty_history::history::{self, SessionHistoryRequest};
use serde_json::{Value, json};

const REPORT: &str = "## Docs survey\n\n- help/board.md mentions the board view.";

fn stamp_at(seconds: f64) -> String {
    stamp(plus_seconds(at("2026-09-01T00:00:00Z"), seconds))
}

fn handback(sender: &str) -> String {
    let report: Vec<String> = REPORT.split('\n').map(|line| format!("  {line}")).collect();
    format!(
        "<agent-message from=\"{sender}\">\n[Subagent hand-back] The text below is the final report of a subagent. The report follows:\n{}\n</agent-message>",
        report.join("\n")
    )
}

fn user(uuid: &str, seconds: f64, content: Value, extra: &[(&str, Value)]) -> Value {
    let mut line = json!({"type": "user", "uuid": uuid, "timestamp": stamp_at(seconds), "message": {"role": "user", "content": content}});
    for (key, value) in extra {
        line[*key] = value.clone();
    }
    line
}

fn agent_call(name: &str) -> Value {
    json!({"type": "assistant", "uuid": "a1", "timestamp": stamp_at(1.0), "message": {"id": "m1", "role": "assistant", "content": [{"type": "tool_use",
        "id": "toolu_FAKEagent0001", "name": "Agent", "input": {"description": "Survey the docs", "name": name, "prompt": "Survey the docs.", "run_in_background": true}}]}})
}

fn launch(agent: &str) -> Value {
    user(
        "t1",
        2.0,
        json!([{"type": "tool_result", "tool_use_id": "toolu_FAKEagent0001", "content": [{"type": "text",
            "text": format!("Async agent launched successfully.\nagentId: {agent} (internal ID)\nThe agent is working in the background.")}]}]),
        &[("toolUseResult", json!({}))],
    )
}

fn answer(uuid: &str, text: &str, seconds: f64) -> Value {
    json!({"type": "assistant", "uuid": uuid, "timestamp": stamp_at(seconds), "message": {"id": format!("m-{uuid}"), "role": "assistant", "content": [{"type": "text", "text": text}]}})
}

/// `Replay`: the lines written as a Claude record of a workspace and read back.
fn replay(lines: &[Value]) -> Vec<MightyGraphRun> {
    let fixture = Fixture::new("agent-notices");
    let id = "5b0c1d2e-3333-4444-8555-666677778888";
    let written: Vec<String> = lines.iter().map(Value::to_string).collect();
    fixture.claude_record(id, &written, None, None);
    let runs = history::load(&SessionHistoryRequest::new("claude", id, &fixture.workspace_text()).home(fixture.home()), &ko(), &Never)
        .expect("the record")
        .runs;
    assert!(!runs.is_empty(), "the record replays no request");
    runs
}

fn answer_entry(agent: &MightyGraphAgent) -> Option<String> {
    agent.entries().iter().rev().find(|e| e.id().ends_with("-answer")).map(|e| e.text().to_string())
}

fn check_settled(run: &MightyGraphRun, why: &str) {
    let agent = run.agents().iter().find(|a| a.kind().is_none()).unwrap_or_else(|| panic!("{why}: no agent block"));
    assert_eq!(agent.status(), "completed", "{why}: the agent's state");
    assert_eq!(answer_entry(agent).as_deref(), Some(REPORT), "{why}: the agent's result is its report");
    assert_eq!(run.agents().iter().filter(|a| mighty_graph::support::is_task_notice_block(a)).count(), 0, "{why}: no 작업 알림");
}

/// AgentNoticeVerification.cs:308 `ARecordedHandBackTurnSettlesTheAgentAsLive`: a hand-back recorded as a meta line opens a notice turn,
/// as live; the block takes the report, and that turn's answer is the result of a notice run of its own; the notification after it
/// answers in the same run.
#[test]
fn agent_notices_a_recorded_hand_back_turn_settles_the_agent_as_live() {
    let agent = "a1b2c3d4e5f6a7b8c";
    let notice = format!(
        "<task-notification>\n<task-id>{agent}</task-id>\n<tool-use-id>toolu_FAKEagent0001</tool-use-id>\n<status>completed</status>\n<summary>Agent \"Survey the docs\" finished</summary>\n</task-notification>"
    );
    let runs = replay(&[
        user("u1", 0.0, json!("survey the docs"), &[]),
        agent_call("docs-surveyor"),
        launch(agent),
        answer("a2", "Started the survey.", 3.0),
        user(
            "h1",
            30.0,
            json!(format!(
                "Another Claude session sent a message:\n{}\n\nThat other Claude session is an agent of this session.",
                handback(agent)
            )),
            &[("isMeta", json!(true)), ("origin", json!({"kind": "peer", "from": agent, "senderTaskId": agent}))],
        ),
        answer("a3", "The survey found one page.", 31.0),
        user("n1", 32.0, json!(notice), &[("origin", json!({"kind": "task-notification"}))]),
        answer("a4", "Already handled.", 33.0),
    ]);
    assert_eq!(runs.len(), 2, "the request and one notice run");
    let run = &runs[0];
    check_settled(run, "a recorded hand-back turn");
    assert_eq!(run.final_output(), Some("Started the survey."), "the request keeps its own answer");
    assert!(run.notice_run.is_none(), "the request is no notice run");
    let notice_run = &runs[1];
    assert!(notice_run.is_notice() && notice_run.status() == "completed", "the hand-back turn is a notice run of its own");
    assert_eq!(
        notice_run.final_output(),
        Some("The survey found one page.\n\nAlready handled."),
        "the notification after it answers in the same run"
    );
    let copy = notice_run.agents().first().expect("the notice run has a copy");
    let block = run.agents().iter().find(|a| a.kind().is_none()).expect("the block");
    assert!(copy.source_block_id.as_deref() == Some(block.id()) && copy.background == Some(true), "the copy names its block");
    assert_eq!(answer_entry(copy).as_deref(), Some(REPORT), "the copy's result is the report");
}

/// AgentNoticeVerification.cs:338 `AQueuedHandBackSettlesTheAgentByItsName`: a hand-back taken into the running turn is recorded as a
/// queued command; it settles the block by the call's name, and its copy goes to a finished notice run without a result.
#[test]
fn agent_notices_a_queued_hand_back_settles_the_agent_by_its_name() {
    let runs = replay(&[
        user("u1", 0.0, json!("survey the docs"), &[]),
        agent_call("docs-surveyor"),
        launch("a2b3c4d5e6f7a8b9c"),
        json!({"type": "attachment", "uuid": "q1", "timestamp": stamp_at(10.0), "isMeta": true, "attachment": {
            "type": "queued_command", "commandMode": "prompt", "isMeta": true, "origin": {"kind": "peer", "from": "docs-surveyor"}, "prompt": handback("docs-surveyor")}}),
        answer("a2", "The survey is done.", 11.0),
    ]);
    assert_eq!(runs.len(), 2, "the request and one notice run");
    check_settled(&runs[0], "a queued hand-back");
    assert_eq!(runs[0].final_output(), Some("The survey is done."), "the request's result");
    assert!(
        runs[1].is_notice() && runs[1].status() == "completed" && runs[1].final_output().is_none() && runs[1].agents().len() == 1,
        "the absorbed notice run is finished, without a result"
    );
}
