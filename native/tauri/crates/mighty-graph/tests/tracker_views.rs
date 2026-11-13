//! The tracker's checks among the views' C# suites: FlowLayoutVerification.TheTrackersFiguresForTheCards and
//! FlowStepsVerification.TheRecordedHeartbeatMakesNoBlock whole, the tracker's half of MightyTimelineVerification's
//! RecordedOrderAndResultsMatchDiagram (its 기록 rows, diagram and toolbar are timeline.rs's) and ATaskNotificationIsATaskNoticeSteer with
//! the toolbar over the tracker's run, and AcpGraphVerification.OpencodeAndHermesPanesDrawTheGraph.

mod common;

use std::collections::HashMap;

use common::*;
use mighty_core::graph::{GraphAgentFigures, MightyGraphAgent};
use mighty_core::locale::Locale;
use mighty_core::time;
use mighty_graph::tracker::{ModGraphMetadata, ModMetadata};
use mighty_graph::{ExecutionGraphNode, labels, node, notice, support, view_model};
use serde_json::{Value, json};

fn figures(started: Option<&str>, ended: Option<&str>) -> Option<GraphAgentFigures> {
    Some(GraphAgentFigures { started_at: started.map(Into::into), ended_at: ended.map(Into::into), ..GraphAgentFigures::default() })
}

fn last_of(emitted: &[ExecutionGraphNode], tool: &str) -> ExecutionGraphNode {
    let id = node::agent_node_id("run", tool);
    emitted.iter().rfind(|n| n.id == id).cloned().unwrap_or_else(|| panic!("no block for {tool}"))
}

#[test]
fn the_trackers_figures_for_the_cards() {
    let (mut tracker, emitted) = recording_tracker("run", Some("request"), "claude", None);
    let latest = |tool: &str| last_of(&emitted.lock().expect("nodes"), tool);
    tracker.consume(&json!({"type": "assistant", "uuid": "m1", "timestamp": "2026-11-10T18:56:44.496Z",
        "message": {"id": "m1", "content": [{"type": "tool_use", "id": "a", "name": "Agent", "input": {"description": "Count", "prompt": "Go"}}]}}));
    same(latest("a").figures, figures(Some("2026-11-10T18:56:44.496Z"), None), "started at its frame's time");
    tracker.consume(&json!({"type": "user", "timestamp": "2026-11-10T18:57:02.812Z",
        "message": {"content": [{"type": "tool_result", "tool_use_id": "a", "content": "3"}]}}));
    same(latest("a").state, "completed".to_string(), "ended");
    same(latest("a").figures, figures(Some("2026-11-10T18:56:44.496Z"), Some("2026-11-10T18:57:02.812Z")), "ended at its frame's time");
    tracker.consume(&json!({"type": "user", "timestamp": "2026-11-10T19:00:00.000Z",
        "message": {"content": [{"type": "tool_result", "tool_use_id": "a", "content": "again"}]}}));
    same(latest("a").figures.and_then(|f| f.ended_at).as_deref(), Some("2026-11-10T18:57:02.812Z"), "a later word does not move its end");
    tracker.consume(&json!({"type": "assistant", "uuid": "m2",
        "message": {"id": "m2", "content": [{"type": "tool_use", "id": "b", "name": "Agent", "input": {"description": "Live", "prompt": "Go"}}]}}));
    let started = time::parse(latest("b").figures.and_then(|f| f.started_at).as_deref()).expect("no start");
    let now = time::parse(Some(&time::now_o())).expect("now");
    check((started.utc_ticks() - now.utc_ticks()).abs() < 60 * 10_000_000, "a frame without a time starts the block now");
    tracker.finish("stopped");
    let b = latest("b");
    check(b.state == "stopped" && time::parse(b.figures.and_then(|f| f.ended_at).as_deref()).is_some(), "the process's end ends it, now");
    check(
        emitted.lock().expect("nodes").iter().filter(|n| n.kind == "main").all(|n| n.figures.is_none()),
        "only a sub-agent block has figures",
    );

    let (mut tools, counted) = recording_tracker("run", Some("request"), "claude", None);
    let calls = |tool: &str| last_of(&counted.lock().expect("nodes"), tool).figures.and_then(|f| f.tool_calls);
    let child = |blocks: Vec<Value>, id: &str, parent: &str| json!({"type": "assistant", "uuid": id, "parent_tool_use_id": parent, "message": {"id": id, "content": blocks}});
    tools.consume(&json!({"type": "assistant", "uuid": "m1",
        "message": {"id": "m1", "content": [{"type": "tool_use", "id": "a", "name": "Agent", "input": {"description": "Count", "prompt": "Go"}}]}}));
    same(calls("a"), None, "none yet");
    let read = child(vec![json!({"type": "tool_use", "id": "r1", "name": "Read", "input": {"file_path": "a.txt"}})], "c1", "a");
    tools.consume(&read);
    same(calls("a"), Some(1), "one");
    tools.consume(&read);
    same(calls("a"), Some(1), "the same message again is the same call");
    tools.consume(&child(
        vec![
            json!({"type": "tool_use", "id": "r2", "name": "Read", "input": {"file_path": "b.txt"}}),
            json!({"type": "tool_use", "id": "r3", "name": "Grep", "input": {"pattern": "x"}}),
        ],
        "c2",
        "a",
    ));
    same(calls("a"), Some(3), "two more");
    tools.consume(&json!({"type": "system", "subtype": "task_progress", "task_id": "t", "tool_use_id": "a", "description": "Reading",
        "usage": {"total_tokens": 100, "tool_uses": 5, "duration_ms": 10}}));
    same(calls("a"), Some(5), "the CLI's larger count");
    tools.consume(&json!({"type": "system", "subtype": "task_progress", "task_id": "t", "tool_use_id": "a", "description": "Reading", "usage": {"tool_uses": 2}}));
    same(calls("a"), Some(5), "not a smaller one");
    tools.consume(&json!({"type": "assistant", "uuid": "m4",
        "message": {"id": "m4", "content": [{"type": "tool_use", "id": "b", "name": "Agent", "input": {"description": "Other", "prompt": "Go"}}]}}));
    tools.consume(&json!({"type": "system", "subtype": "task_progress", "task_id": "u", "tool_use_id": "b", "description": "Reading", "usage": {"tool_uses": 1}}));
    tools.consume(&child(vec![json!({"type": "tool_use", "id": "b-read", "name": "Read", "input": {"file_path": "a.txt"}})], "c3", "b"));
    same(calls("b"), Some(1), "a report before the message is not counted twice");
    tools.consume(&json!({"type": "assistant", "uuid": "m3",
        "message": {"id": "m3", "content": [{"type": "tool_use", "id": "main-read", "name": "Read", "input": {"file_path": "c.txt"}}]}}));
    same(calls("a"), Some(5), "the main session's calls are not a sub-agent's");
}

#[test]
fn the_recorded_heartbeat_makes_no_block() {
    let runs = replay("claude-stream-tool-heartbeat", "recorded", None);
    check(
        recording("claude-stream-tool-heartbeat").iter().any(|line| text(line, "type") == Some("tool_progress")),
        "the recording has the heartbeat",
    );
    check(
        !runs.is_empty() && runs[0].agents().is_empty() && runs[0].status() == "completed",
        &format!("no block: {:?}", runs[0].agents().iter().map(|a| a.title()).collect::<Vec<_>>()),
    );

    // A frame or a Mods spawn under a call the stream showed as another tool is no sub-agent; under an Agent call it is.
    let (mut tracker, nodes) = recording_tracker("run", Some("probe"), "claude", None);
    tracker.consume(&json!({"type": "assistant", "message": {"id": "m1", "role": "assistant",
        "content": [{"type": "tool_use", "id": "read-1", "name": "Read", "input": {"file_path": "/tmp/a"}}]}}));
    tracker.consume(&json!({"type": "assistant", "parent_tool_use_id": "read-1", "message": {"id": "m2", "role": "assistant", "content": [{"type": "text", "text": "x"}]}}));
    tracker.receive_mod(&ModMetadata {
        claude_session_id: "session".into(),
        event: "agent.spawn".into(),
        graph: Some(ModGraphMetadata {
            version: 1,
            phase: "starting".into(),
            parent_tool_use_id: Some("read-1".into()),
            ..ModGraphMetadata::default()
        }),
        ..ModMetadata::default()
    });
    tracker.consume(&json!({"type": "assistant", "message": {"id": "m3", "role": "assistant",
        "content": [{"type": "tool_use", "id": "agent-1", "name": "Agent", "input": {"description": "Map the repo", "prompt": "Map it"}}]}}));
    tracker.consume(&json!({"type": "assistant", "parent_tool_use_id": "agent-1", "message": {"id": "m4", "role": "assistant", "content": [{"type": "text", "text": "mapping"}]}}));
    let nodes = nodes.lock().expect("nodes");
    check(!nodes.iter().any(|n| n.id == node::agent_node_id("run", "read-1")), "nothing under the Read call");
    check(nodes.iter().any(|n| n.id == node::agent_node_id("run", "agent-1") && n.title == "Map the repo"), "the Agent call's block");
}

fn steer_agent(id: &str, input: &str) -> MightyGraphAgent {
    MightyGraphAgent {
        id: Some(id.into()),
        title: Some(String::new()),
        input: Some(input.into()),
        status: Some("completed".into()),
        kind: Some("steer".into()),
        entries: Some(vec![]),
        ..MightyGraphAgent::default()
    }
}

/// The tracker's and the run rules' half of MightyTimelineVerification.RecordedOrderAndResultsMatchDiagram: a delegation notice and a
/// person's steer are named apart, and a run that ends before a steer is answered says so in the block's own words.
#[test]
fn recorded_order_and_results_match_diagram_tracker_part() {
    let ko = Locale::new("ko");
    let notice_steer = steer_agent("notice", "[Mighty Code notice n-1] Your child c-1 reported revision 1.");
    let follow_up = steer_agent("follow", "[Mighty Code follow-up f-1] Your parent sent you this follow-up:");
    let person = steer_agent("person", "Also add tests");
    check(
        support::block_title(&notice_steer, &ko) == "위임 알림"
            && support::block_title(&follow_up, &ko) == "위임 알림"
            && support::block_title(&person, &ko) == "중간 요청",
        "a delegation notice is named 위임 알림, a person's steer 중간 요청",
    );
    check(support::block_kind(&notice_steer) == "steer", "the kind stays steer");
    let (mut tracker, emitted) = recording_tracker("steer-run", Some("root prompt"), "claude", None);
    tracker.steer("n", notice_steer.input());
    tracker.steer("f", follow_up.input());
    tracker.steer("p", person.input());
    tracker.finish("completed");
    let latest: HashMap<String, ExecutionGraphNode> = emitted.lock().expect("nodes").iter().map(|n| (n.id.clone(), n.clone())).collect();
    let unfinished = |id: &str| {
        latest[&node::identifier("steer-run", &format!("steer:{id}"))].entries.last().map(|e| e.text().to_string()).unwrap_or_default()
    };
    check(
        unfinished("n") == ko.get("graph.block.unfinished.delegationNotice")
            && unfinished("f") == ko.get("graph.block.unfinished.delegationNotice")
            && unfinished("p") == ko.get("graph.block.unfinished.steer")
            && ko.get("graph.block.unfinished.delegationNotice") != ko.get("graph.block.unfinished.steer"),
        &format!(
            "an unanswered delegation notice ends with its own words, a person's steer with the 중간 요청 words; got '{}', '{}', '{}'",
            unfinished("n"),
            unfinished("f"),
            unfinished("p")
        ),
    );
}

/// MightyTimelineVerification.ATaskNotificationIsATaskNoticeSteer over the tracker's run (the 기록 kind label is timeline.rs's).
#[test]
fn a_task_notification_is_a_task_notice_steer_tracker_part() {
    let ko = Locale::new("ko");
    let (mut tracker, emitted) = recording_tracker("notice-run", Some("Build project"), "claude", None);
    let notification = |task: &str, tool: &str, status: &str, summary: &str| {
        format!(
            "[SYSTEM NOTIFICATION]\n<task-notification>\n<task-id>{task}</task-id>\n<tool-use-id>{tool}</tool-use-id>\n<status>{status}</status>\n<summary>{summary}</summary>\n</task-notification>"
        )
    };
    let said = |text: &str| json!({"type": "assistant", "message": {"content": [{"type": "text", "text": text}]}});
    let told = |text: &str| json!({"type": "user", "message": {"content": [{"type": "text", "text": text}]}});
    tracker.consume(&json!({"type": "assistant", "message": {"content": [
        {"type": "tool_use", "id": "build-cmd", "name": "Bash", "input": {"command": "npm run build", "description": "Build", "run_in_background": true}},
        {"type": "tool_use", "id": "lint-cmd", "name": "Bash", "input": {"command": "npm run lint", "run_in_background": true}}]}}));
    tracker.consume(&json!({"type": "user", "message": {"content": [
        {"type": "tool_result", "tool_use_id": "build-cmd", "content": "Command running in background with ID: task-b1."},
        {"type": "tool_result", "tool_use_id": "lint-cmd", "content": "could not start", "is_error": true}]}}));
    tracker.consume(&said("Started the build."));
    tracker.consume(&told(&notification("task-b1", "build-cmd", "completed", "Build done")));
    tracker.consume(&said("The build passed."));
    // The same task told again, and a task whose launch had already failed.
    tracker.consume(&told(&notification("task-b1", "build-cmd", "failed", "Build done again")));
    tracker.consume(&told(&notification("task-l1", "lint-cmd", "stopped", "")));
    tracker.finish("completed");
    let latest: HashMap<String, ExecutionGraphNode> = emitted.lock().expect("nodes").iter().map(|n| (n.id.clone(), n.clone())).collect();
    let main = node::main_node_id("notice-run");
    check(!latest.values().any(|n| n.kind == "steer"), "no 작업 알림 steer is made");
    let notice_main = latest[&node::main_node_id("notice-run:notice:1")].clone();
    let build = latest[&format!("notice-run:notice:1:{}", node::agent_node_id("notice-run", "build-cmd"))].clone();
    let lint = latest[&format!("notice-run:notice:1:{}", node::agent_node_id("notice-run", "lint-cmd"))].clone();
    // Named after the blocks it copies: the build by its title, the lint by its command.
    check(
        notice_main.state == "completed"
            && notice_main.output.is_none()
            && notice_main.title == notice::title(&notice::kind(notice::Opener::Work, &ko), &["Build", "npm run lint"], &ko),
        &format!("the absorbed notice run is complete without a result; got '{}'", notice_main.title),
    );
    check(
        notice_main.input.as_deref() == Some("Build · 완료\nnpm run lint · 실패"),
        &format!("the notice run names each finished task and how it ended; got '{:?}'", notice_main.input),
    );
    check(
        build.state == "completed"
            && build.background == Some(true)
            && build.parent_id.as_deref() == Some(notice_main.id.as_str())
            && lint.state == "error",
        "each block is copied once, as it ended",
    );
    let answer = latest[&main].output.clone();
    check(
        answer.is_none() || answer.as_deref() == Some("The build passed."),
        &format!("the request's answer stays its own; got '{answer:?}'"),
    );
    let mut run = tracker.build_run(false).expect("a run");
    check(run.agents().iter().all(|a| a.kind.as_deref() != Some("steer")) && run.notice_run.is_none(), "the request holds no notice block");
    let notices = tracker.build_notice_runs(false);
    check(notices.len() == 1 && notices[0].is_notice() && notices[0].agents().len() == 2, "the notice run is built apart, flagged");
    let saved = steer_agent("saved-notice", &format!("{}Build · 완료\nBuild done", support::TASK_NOTICE_PREFIX));
    check(
        support::block_title(&saved, &ko) == "작업 알림"
            && support::block_kind(&saved) == "steer"
            && support::is_task_notice_block(&saved)
            && support::is_notice(&saved)
            && !support::is_delegation_notice_block(&saved)
            && support::shown_input(&saved) == "Build · 완료\nBuild done",
        "a saved block is a 작업 알림 that shows its line without the prefix",
    );
    let legacy = steer_agent("old-notice", "[Mighty Claude task notice] Build · 완료");
    check(
        support::is_task_notice_block(&legacy) && support::shown_input(&legacy) == "Build · 완료",
        "a block saved before the rename is still a 작업 알림",
    );
    check(
        support::is_delegation_notice_block(&steer_agent("o", "[Mighty Claude notice n-0] Your child c-0 reported revision 1.")),
        "a delegation notice saved before the rename is still one",
    );
    let agents = run.agents.get_or_insert_with(Vec::new);
    agents.push(saved);
    agents.push(steer_agent("person", "Also add tests"));
    let summary = view_model::toolbar_summary(&[run], &ko);
    check(
        summary.contains("중간 요청 1") && !summary.contains("중간 요청 3"),
        &format!("task notices are not counted as a person's steers; got {summary}"),
    );
}

/// AcpGraphVerification.OpencodeAndHermesPanesDrawTheGraph: opencode and Hermes panes draw the graph beside the four, as on the Mac, and
/// their request block is titled by the agent's own name.
#[test]
fn opencode_and_hermes_panes_draw_the_graph() {
    check(support::PROVIDERS == ["claude", "codex", "gemini", "pi", "opencode", "hermes"], "every agent's pane draws the graph");
    for provider in ["opencode", "hermes"] {
        let pane = mighty_core::model::RunSession {
            kind: Some("claude".into()),
            provider: Some(provider.into()),
            ..mighty_core::model::RunSession::default()
        };
        check(
            view_model::supports_mighty(&pane) && view_model::view_mode("claude", provider) == Some("mighty"),
            &format!("{provider} opens in the Mighty view"),
        );
        let (_tracker, nodes) = recording_tracker("r", Some("hi"), provider, None);
        let nodes = nodes.lock().expect("nodes");
        check(
            nodes.len() == 1 && nodes[0].title == labels::provider_label(provider),
            &format!("{provider}'s request block carries its name: {:?}", nodes.iter().map(|n| &n.title).collect::<Vec<_>>()),
        );
    }
}
