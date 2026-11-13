//! A notice turn is a run of its own (C# BackgroundNoticeRunsVerification.cs; macOS BackgroundNoticeRunsTests): the tracker opens it, the
//! pane keeps it flagged with its own lines, views draw background blocks only there, and it never takes the newest request's place.
//!
//! What is here is what the tracker and the run rules decide. The diagram's layout, camera and blocks, 기록, the pipeline, the phone
//! (MobileMightySupport, MobileStructure, PlanCardSupport) and the pane's own `Apply` of a run event (its transcript routing) are other
//! modules': those assertions are ported with them. The pane is fed here as the run pipeline feeds it, the tracker's runs by id.

mod common;

use std::collections::HashMap;

use common::*;
use mighty_core::graph::{MightyGraphAgent, MightyGraphRun};
use mighty_core::json::Wire;
use mighty_core::locale::Locale;
use mighty_core::model::RunSession;
use mighty_core::wire;
use mighty_graph::tracker::ExecutionGraphTracker;
use mighty_graph::{ExecutionGraphNode, node, notice, support};
use serde_json::{Value, json};

fn ko() -> Locale {
    Locale::new("ko")
}

fn text_frame(value: &str, id: &str) -> Value {
    json!({"type": "assistant", "uuid": id, "message": {"id": id, "content": [{"type": "text", "text": value}]}})
}

fn command(id: &str, command: &str, description: &str) -> Value {
    json!({"type": "assistant", "uuid": format!("call-{id}"), "message": {"id": format!("call-{id}"), "content": [{"type": "tool_use", "id": id, "name": "Bash",
        "input": {"command": command, "description": description, "run_in_background": true}}]}})
}

fn launched(tool: &str, task: &str) -> Value {
    json!({"type": "user", "message": {"content": [{"type": "tool_result", "tool_use_id": tool,
        "content": format!("Command running in background with ID: {task}. Output is being written to: /tmp/{task}.output")}]}})
}

fn notification(task: &str, summary: &str) -> Value {
    json!({"type": "user", "origin": {"kind": "task-notification"}, "message": {"content":
        format!("<task-notification>\n<task-id>{task}</task-id>\n<output-file>/tmp/{task}.output</output-file>\n<status>completed</status>\n<summary>{summary}</summary>\n</task-notification>")}})
}

/// The notification a real stream carries: it names the tool use that started the work beside the task's id.
fn notification_of(task: &str, tool: &str, summary: &str) -> Value {
    json!({"type": "user", "origin": {"kind": "task-notification"}, "message": {"content":
        format!("<task-notification>\n<task-id>{task}</task-id>\n<tool-use-id>{tool}</tool-use-id>\n<output-file>/tmp/{task}.output</output-file>\n<status>completed</status>\n<summary>{summary}</summary>\n</task-notification>")}})
}

/// The live stream's form of the same notification.
fn system_notification(task: &str, tool: &str, summary: &str, status: &str) -> Value {
    json!({"type": "system", "subtype": "task_notification", "task_id": task, "tool_use_id": tool, "status": status,
        "output_file": format!("/tmp/{task}.output"), "summary": summary})
}

fn agent_call(id: &str, name: &str, background: Option<bool>) -> Value {
    let input = match background {
        Some(flag) => json!({"prompt": "survey", "description": name, "run_in_background": flag}),
        None => json!({"prompt": "survey", "description": name}),
    };
    json!({"type": "assistant", "uuid": format!("call-{id}"), "message": {"id": format!("call-{id}"), "content": [{"type": "tool_use", "id": id, "name": "Agent", "input": input}]}})
}

fn agent_ack(tool: &str, agent: &str) -> Value {
    json!({"type": "user", "message": {"content": [{"type": "tool_result", "tool_use_id": tool,
        "content": format!("Async agent launched successfully.\nagentId: {agent} (internal ID)")}]}})
}

fn result(value: &str, notice: bool) -> Value {
    if notice {
        json!({"type": "result", "subtype": "success", "is_error": false, "result": value, "origin": {"kind": "task-notification"}})
    } else {
        json!({"type": "result", "subtype": "success", "is_error": false, "result": value})
    }
}

/// A run as C# `new MightyGraphRun { … }` makes one.
fn graph_run(id: &str, status: &str) -> MightyGraphRun {
    MightyGraphRun {
        id: Some(id.into()),
        input: Some(String::new()),
        status: Some(status.into()),
        root_entries: Some(vec![]),
        agents: Some(vec![]),
        result_entries: Some(vec![]),
        ..MightyGraphRun::default()
    }
}

/// An agent as C# `new MightyGraphAgent { … }` makes one.
fn graph_agent(id: &str, title: &str, status: &str) -> MightyGraphAgent {
    MightyGraphAgent {
        id: Some(id.into()),
        title: Some(title.into()),
        input: Some(String::new()),
        status: Some(status.into()),
        entries: Some(vec![]),
        ..MightyGraphAgent::default()
    }
}

fn agents_mut(run: &mut MightyGraphRun) -> &mut Vec<MightyGraphAgent> {
    run.agents.get_or_insert_with(Vec::new)
}

fn new_tracker(run_id: &str, input: &str) -> ExecutionGraphTracker {
    recording_tracker(run_id, Some(input), "claude", None).0
}

/// The pane's runs as the run pipeline hands them over (`Hand`): the request's run, then the notice runs, each by id.
fn hand(runs: &mut Vec<MightyGraphRun>, tracker: &mut ExecutionGraphTracker) {
    let mut put = |run: MightyGraphRun| match runs.iter().position(|r| r.id == run.id) {
        Some(at) => runs[at] = run,
        None => runs.push(run),
    };
    if let Some(request) = tracker.build_run(true) {
        put(request);
    }
    for notice in tracker.take_notice_runs(true) {
        put(notice);
    }
}

fn pane_of(runs: &[MightyGraphRun], status: &str) -> RunSession {
    RunSession {
        id: Some("p".into()),
        kind: Some("claude".into()),
        provider: Some("claude".into()),
        status: Some(status.into()),
        graph_runs: Some(runs.to_vec()),
        ..RunSession::default()
    }
}

#[test]
fn run_ids_and_the_filter() {
    same(notice::run_id("run-1", 2), "run-1:notice:2".to_string(), "the id names the original and its number");
    check(notice::is_notice_run("run-1:notice:2") && !notice::is_notice_run("run-1"), "a notice run is told by its id");
    check(wire::identifier(&notice::run_id(&wire::new_id(), 12)), "the id is a wire identifier");
    let agents = || {
        let mut bg = graph_agent("bg", "Build", "running");
        bg.kind = Some("task".into());
        bg.background = Some(true);
        let mut child = graph_agent("bg-child", "Inner", "running");
        child.parent_id = Some("bg".into());
        let mut survey = graph_agent("agent", "Survey", "running");
        survey.background = Some(true);
        vec![graph_agent("fg", "Read", "running"), bg, child, survey]
    };
    let mut request = graph_run("r", "running");
    request.agents = Some(agents());
    check(
        notice::shown_agents(&request).iter().map(|a| a.id()).collect::<Vec<_>>() == vec!["fg"],
        "a request draws no background block, nor what hangs under one",
    );
    let mut notice_run = graph_run("r:notice:1", "running");
    notice_run.agents = Some(agents());
    notice_run.notice_run = Some(true);
    check(notice::shown_agents(&notice_run).len() == 4, "a notice run draws them all");
    // The flags survive saving; an older save reads them as absent; the shown count is never saved.
    let mut run = graph_run("r:notice:1", "running");
    run.notice_run = Some(true);
    let mut copy = graph_agent("c", "", "running");
    copy.kind = Some("task".into());
    copy.background = Some(true);
    copy.source_block_id = Some("graph-source".into());
    run.agents = Some(vec![copy]);
    let decoded = MightyGraphRun::read(&serde_json::from_str(&run.to_json()).expect("json")).expect("a run");
    check(
        decoded.notice_run == Some(true) && decoded.agents()[0].source_block_id.as_deref() == Some("graph-source"),
        "the flags survive a save",
    );
    let legacy = MightyGraphRun::read(
        &serde_json::from_str(
            r#"{"id":"r","input":"","status":"completed","rootEntries":[],"agents":[{"id":"a","title":"A","input":"","status":"completed","entries":[]}],"resultEntries":[]}"#,
        )
        .expect("json"),
    )
    .expect("a legacy run");
    check(legacy.notice_run.is_none() && legacy.agents()[0].source_block_id.is_none(), "an older save reads them as absent");
    let mut budget = 1_000_000;
    let normalized = support::normalized(std::slice::from_ref(&run), true, &mut budget, None);
    check(
        normalized[0].notice_run == Some(true) && normalized[0].agents()[0].source_block_id.as_deref() == Some("graph-source"),
        "normalizing keeps them",
    );
    let mut task = ExecutionGraphNode::new("n".into(), "r:notice:1".into(), None, "task", "completed", "T".into());
    task.source_block_id = Some("graph-source".into());
    let read = ExecutionGraphNode::read(&serde_json::from_str(&task.to_json()).expect("json")).expect("a node");
    check(read.source_block_id.as_deref() == Some("graph-source"), "a node keeps its source");
}

/// A request whose background command still runs, a notice run taken into its turn (no reply), and one with a reply.
fn runs(answered: bool, request_status: &str) -> Vec<MightyGraphRun> {
    let task = |id: &str, status: &str| {
        let mut agent = graph_agent(id, "Build", status);
        agent.input = Some("npm run build".into());
        agent.kind = Some("task".into());
        agent.background = Some(true);
        agent.source_block_id = (id != "t").then(|| "t".into());
        agent
    };
    let mut request = graph_run("r", request_status);
    request.input = Some("build it".into());
    request.agents = Some(vec![graph_agent("fg", "Read", "completed"), task("t", "running")]);
    request.final_output = (request_status == "completed").then(|| "Started.".into());
    support::refresh_result(&mut request);
    let mut quiet = graph_run("r:notice:1", "completed");
    quiet.input = Some("Build · 완료".into());
    quiet.agents = Some(vec![task("r:notice:1:t", "completed")]);
    quiet.notice_run = Some(true);
    support::refresh_result(&mut quiet);
    let mut reply = graph_run("r:notice:2", if answered { "completed" } else { "running" });
    reply.input = Some("Build · 완료".into());
    reply.agents = Some(vec![task("r:notice:2:t", "completed")]);
    reply.final_output = answered.then(|| "The build passed.".into());
    reply.notice_run = Some(true);
    support::refresh_result(&mut reply);
    vec![request, quiet, reply]
}

fn node_id(run: &str, suffix: &str) -> String {
    mighty_core::graph::node_id(run, suffix)
}

fn first_result_text(run: &MightyGraphRun) -> Option<String> {
    run.result_entries().first().map(|e| e.text().to_string())
}

/// The run rules of C# TheRunRulesAndTheViews; its diagram, camera, 기록, pipeline and phone digest assertions belong to those modules.
#[test]
fn the_run_rules_and_the_views() {
    let ko = ko();
    let all = runs(true, "completed");
    same(support::newest_request_index(&all), Some(0), "the newest request");
    check(
        !support::quiet_notice(&all[0]) && support::quiet_notice(&all[1]) && !support::quiet_notice(&all[2]),
        "only the absorbed notice run is quiet",
    );
    same(support::follow_run_index(&all), Some(2), "an answered notice run is followed");
    same(support::follow_run_index(&runs(false, "completed")), Some(2), "a running notice run is followed");
    same(support::follow_run_index(&all[..2]), Some(0), "a quiet one never is");
    let mut next = all.clone();
    next.push(graph_run("n", "starting"));
    check(
        support::newest_request_index(&next) == Some(3) && support::follow_run_index(&next) == Some(3),
        "a request after them is the newest again",
    );
    check(
        support::request_ordinals(&next) == vec![1, 1, 1, 2] && support::request_ordinals(&[all[1].clone(), all[0].clone()]) == vec![1, 1],
        "notice runs take no number",
    );
    check(support::newest_request_index(&all[1..2]).is_none(), "notice runs alone hold no request");
    // The request's result shows as its own turn ends; its background command runs on in the strip and it still says it waits on it.
    check(
        all[0].settled() && first_result_text(&all[0]).as_deref() == Some("Started."),
        "the request's result does not wait for hidden work",
    );
    let shown = support::shown_runs(&all, &ko);
    check(
        shown[0].agents().iter().map(|a| a.id()).collect::<Vec<_>>() == vec!["fg"]
            && all[0].agents().len() == 2
            && support::background_waiting(&shown[0]) == 1,
        "views drop the block and keep its count",
    );
    check(!shown[0].to_json().to_lowercase().contains("backgroundwaiting"), "the count is never saved");
    check(support::background_waiting(&support::shown_runs(&runs(true, "running"), &ko)[0]) == 0, "a running request waits on nothing yet");
    same(support::latest_result_id(&all), Some(node_id("r:notice:2", "result")), "the latest result");
    same(support::latest_result_id(&all[..2]), Some(node_id("r", "result")), "never the quiet run's");
    // A failed notice turn keeps its card; a reopened one keeps its earlier reply; a first one still waiting has none.
    let mut failed = graph_run("r:notice:1", "error");
    failed.notice_run = Some(true);
    check(
        !support::quiet_notice(&failed)
            && support::has_result(&failed)
            && support::follow_run_index(&[all[0].clone(), failed.clone()]) == Some(1),
        "a failed notice turn is not quiet",
    );
    let mut reopened = graph_run("r:notice:1", "running");
    reopened.final_output = Some("The build passed.".into());
    reopened.notice_run = Some(true);
    support::refresh_result(&mut reopened);
    check(
        reopened.reopened_with_reply()
            && support::has_result(&reopened)
            && first_result_text(&reopened).as_deref() == Some("The build passed."),
        "a reopened notice run keeps its reply",
    );
    // The reopened turn failed or was stopped: the card keeps the earlier reply; a request's error result still shows none.
    for status in ["error", "stopped"] {
        reopened.status = Some(status.into());
        support::refresh_result(&mut reopened);
        check(
            support::has_result(&reopened) && first_result_text(&reopened).as_deref() == Some("The build passed."),
            &format!("a notice run whose reopened turn ended {status} keeps its reply"),
        );
    }
    let mut failed_request = graph_run("r", "error");
    failed_request.final_output = Some("Started.".into());
    support::refresh_result(&mut failed_request);
    check(failed_request.result_entries().is_empty(), "a request's error result shows none");
    let mut opening = graph_run("r:notice:2", "running");
    opening.notice_run = Some(true);
    support::refresh_result(&mut opening);
    check(!support::has_result(&opening) && opening.result_entries().is_empty(), "a first notice turn still waiting has no card");
}

/// The tracker's half of C# ThePaneKeepsTheNoticeRunApart: the request's run and the notice run as the tracker builds them. The notice
/// turn's lines (the pane's `Apply`) and the phone and strip (MobileMightySupport, PlanCardSupport) are those modules'.
#[test]
fn the_pane_keeps_the_notice_run_apart() {
    let ko = ko();
    let mut tracker = new_tracker("proc-1", "build it");
    let mut pane_runs = vec![];
    for value in [command("build", "npm run build", "Build the app"), launched("build", "b1"), text_frame("Building.", "m1")] {
        tracker.consume(&value);
    }
    hand(&mut pane_runs, &mut tracker);
    tracker.consume(&result("Building.", false));
    hand(&mut pane_runs, &mut tracker);
    let revision = tracker.notice_revision();
    tracker.consume(&notification("b1", "Build done"));
    check(
        tracker.notice_revision() != revision,
        "a notice run's opening moves the revision, so the pipeline hands it over before the turn's lines",
    );
    hand(&mut pane_runs, &mut tracker);
    tracker.consume(&text_frame("The build passed.", "m2"));
    tracker.consume(&result("The build passed.", true));
    hand(&mut pane_runs, &mut tracker);
    check(
        pane_runs.iter().map(|r| r.id()).collect::<Vec<_>>() == vec!["proc-1", "proc-1:notice:1"],
        &format!("the request, then its notice run: {:?}", pane_runs.iter().map(|r| r.id()).collect::<Vec<_>>()),
    );
    let (request, notice) = (&pane_runs[0], &pane_runs[1]);
    check(
        request.notice_run.is_none()
            && request.final_output.as_deref() == Some("Building.")
            && request.agents().len() == 1
            && request.agents()[0].background == Some(true),
        "the request keeps its answer and its block as data",
    );
    check(
        notice.is_notice()
            && notice.status() == "completed"
            && notice.final_output.as_deref() == Some("The build passed.")
            && notice.input() == "Build the app · 완료",
        &format!("the notice run: {} / {:?}", notice.input(), notice.final_output),
    );
    // The projection carries the main block's title, named after the finished work; a request has none.
    check(
        notice.notice_title.as_deref() == Some("백그라운드 작업 완료 · Build the app")
            && request.notice_title.is_none()
            && Some(notice::heading(notice, &ko)) == notice.notice_title,
        &format!("the notice run's title: {:?}", notice.notice_title),
    );
    check(notice.agents().len() == 1, "one copy");
    let copy = &notice.agents()[0];
    check(
        copy.source_block_id == request.agents()[0].id
            && copy.kind.as_deref() == Some("task")
            && copy.background == Some(true)
            && copy.title() == "Build the app"
            && copy.input() == "npm run build"
            && copy.status() == "completed"
            && copy.output_file.as_deref() == Some("/tmp/b1.output"),
        "the copy is the block as it ended",
    );
    check(support::newest_request_run(&pane_runs).map(|r| r.id()) == Some("proc-1"), "the notice run never takes the request's place");
}

/// The tracker's half of C# AbsorbedAndEndedNoticeRunsLeaveTheRequestItsPlace (the lines it routes are the pane's `Apply`).
#[test]
fn absorbed_and_ended_notice_runs_leave_the_request_its_place() {
    let mut tracker = new_tracker("proc-1", "build it");
    for value in [command("build", "npm run build", "Build the app"), launched("build", "b1"), notification("b1", "Build done")] {
        tracker.consume(&value);
    }
    let mut pane_runs = vec![];
    hand(&mut pane_runs, &mut tracker);
    check(
        pane_runs.iter().map(|r| r.id()).collect::<Vec<_>>() == vec!["proc-1", "proc-1:notice:1"]
            && pane_runs[1].status() == "completed"
            && support::quiet_notice(&pane_runs[1]),
        "the absorbed notice run is finished and quiet",
    );
    let pane = RunSession { id: Some("pane".into()), ..pane_of(&pane_runs, "running") };
    check(
        support::newest_request_run(&pane_runs).map(|r| r.id()) == Some("proc-1")
            && support::without_notice_runs(&pane).graph_runs.as_ref().map(Vec::len) == Some(1),
        "the request stays the newest",
    );
    // A process that ends in a notice turn ends its run, which then takes no more lines.
    let mut second = new_tracker("proc-2", "lint it");
    for value in
        [command("lint", "npm run lint", "Lint"), launched("lint", "l1"), result("Linting.", false), notification("l1", "Lint done")]
    {
        second.consume(&value);
    }
    let mut later = vec![];
    hand(&mut later, &mut second);
    same(later[1].status(), "running", "the notice turn runs");
    second.finish("stopped");
    for notice in second.take_notice_runs(false) {
        match later.iter().position(|r| r.id == notice.id) {
            Some(at) => later[at] = notice,
            None => later.push(notice),
        }
    }
    check(later[1].status() == "stopped" && !support::quiet_notice(&later[1]), "the process's end stops the notice run");
    same(support::routed_index(&later), 0, "an ended notice run takes no more lines");
}

#[test]
fn the_heading_is_the_runs_title_or_the_generic_one() {
    let ko = ko();
    same(notice::generic_title(&ko), ko.get("graph.background.noticeRun"), "the generic title");
    let titled = |title: Option<&str>| {
        let mut run = graph_run("r:notice:1", "running");
        run.notice_run = Some(true);
        run.notice_title = title.map(Into::into);
        run
    };
    same(notice::heading(&titled(None), &ko), notice::generic_title(&ko), "an untitled notice run");
    same(notice::heading(&titled(Some("  \n")), &ko), notice::generic_title(&ko), "a blank title");
    same(
        notice::heading(&titled(Some("하위 에이전트 메시지 · scout")), &ko),
        "하위 에이전트 메시지 · scout".to_string(),
        "a titled notice run",
    );
    // A title a view carries reads the same way, trimmed.
    same(notice::heading_of(None, &ko), notice::generic_title(&ko), "no carried title");
    same(notice::heading_of(Some(" \n"), &ko), notice::generic_title(&ko), "a blank carried title");
    same(notice::heading_of(Some(" 위임 알림 \n"), &ko), "위임 알림".to_string(), "a padded carried title");
    let mut run = graph_run("r:notice:1", "completed");
    run.notice_run = Some(true);
    run.notice_title = Some("백그라운드 작업 완료 · Build".into());
    let decoded = MightyGraphRun::read(&serde_json::from_str(&run.to_json()).expect("json")).expect("a run");
    same(decoded.notice_title.as_deref(), Some("백그라운드 작업 완료 · Build"), "the title survives a save");
    let old = MightyGraphRun::read(
        &serde_json::from_str(
            r#"{"id":"r:notice:1","input":"","status":"completed","rootEntries":[],"agents":[],"resultEntries":[],"noticeRun":true}"#,
        )
        .expect("json"),
    )
    .expect("an old run");
    check(old.notice_title.is_none(), "an older save reads no title");
    let mut budget = 1_000_000;
    let mut stray = graph_run("r", "completed");
    stray.notice_title = Some("stray".into());
    let normalized = support::normalized(&[stray, run.clone()], true, &mut budget, None);
    check(
        normalized.iter().map(|r| r.notice_title.clone()).collect::<Vec<_>>() == vec![None, Some("백그라운드 작업 완료 · Build".into())],
        "normalizing keeps a notice run's title and drops a request's",
    );
    check(
        run.clone().notice_title == run.notice_title && support::shown(&run, &ko).notice_title == run.notice_title,
        "a copy and the shown run keep it",
    );
}

/// The awaiting-notice rule of C# ARequestAwaitingNoticesIsTheAwaitingNoticeBlock; the diagram's block, 기록, the pipeline and the phone are
/// those modules'.
#[test]
fn a_request_awaiting_notices_is_the_awaiting_notice_block() {
    let ko = ko();
    let mut all = runs(false, "completed");
    same(notice::original_run_id("r:notice:2"), Some("r"), "a notice run names its request");
    check(notice::original_run_id("r").is_none(), "a request names none");
    let awaiting = notice::awaiting_notice(&all[0], &all, &ko);
    check(
        awaiting.as_ref().is_some_and(|a| {
            a.delivered() == 0
                && a.tasks.len() == 1
                && a.tasks[0].id == "t"
                && a.tasks[0].title == "Build"
                && a.tasks[0].status == "running"
        }),
        "the running command is awaited",
    );
    let awaiting = awaiting.expect("awaiting");
    same(notice::awaited_word(&awaiting.tasks[0], &ko), "끝나면 알림".to_string(), "its row's word");
    same(notice::awaiting_progress(&awaiting, &ko), "알림 0/1".to_string(), "the progress line");
    same(result_title("completed", 0, true, &ko), "알림 대기 중".to_string(), "the title while awaiting");
    same(result_title("completed", 0, false, &ko), "최종 결과".to_string(), "not awaiting is the final result");
    same(result_title("error", 0, true, &ko), ko.get("graph.block.resultError"), "a failed request keeps its title");
    check(
        notice::awaiting_notice(&support::shown_runs(&all, &ko)[0], &[], &ko) == Some(awaiting.clone()),
        "a shown run carries its awaiting",
    );
    // The pane is alive only for the notice: the pet and the header say 알림 대기.
    check(
        notice::awaits_notice(&pane_of(&all, "running"))
            && !notice::awaits_notice(&pane_of(&all, "completed"))
            && notice::state_word(&ko) == "알림 대기",
        "the pane awaits notices",
    );
    // The command ended and its notice turn runs: still awaiting, its notice delivered.
    agents_mut(&mut all[0])[1].status = Some("completed".into());
    let delivered = notice::awaiting_notice(&all[0], &all, &ko);
    check(
        delivered.as_ref().is_some_and(|d| {
            d.delivered() == 1 && notice::awaiting_progress(d, &ko) == "알림 1/1" && notice::awaited_word(&d.tasks[0], &ko) == "알림 전달됨"
        }) && support::background_waiting(&all[0]) == 0,
        "an unanswered notice turn keeps it awaiting",
    );
    // The notice turn answered: the normal 최종 결과.
    all[2].status = Some("completed".into());
    all[2].final_output = Some("The build passed.".into());
    check(
        notice::awaiting_notice(&all[0], &all, &ko).is_none() && !notice::awaits_notice(&pane_of(&all, "running")),
        "answered, it is the final result",
    );
    // Failed or stopped work keeps its word; a failed or stopped request, or a notice run, awaits nothing.
    agents_mut(&mut all[0])[1].status = Some("error".into());
    all[2].status = Some("running".into());
    same(
        notice::awaited_word(&notice::awaiting_notice(&all[0], &all, &ko).expect("awaiting").tasks[0], &ko),
        "실패".to_string(),
        "a failed command's word",
    );
    agents_mut(&mut all[0])[1].status = Some("stopped".into());
    same(
        notice::awaited_word(&notice::awaiting_notice(&all[0], &all, &ko).expect("awaiting").tasks[0], &ko),
        "중단됨".to_string(),
        "a stopped command's word",
    );
    check(notice::awaiting_notice(&all[2], &all, &ko).is_none(), "a notice run awaits nothing");
    for status in ["error", "stopped", "running"] {
        let other = runs(false, status);
        check(notice::awaiting_notice(&other[0], &other, &ko).is_none(), &format!("a {status} request awaits nothing"));
    }
    // A notice run of another request never keeps this one awaiting.
    agents_mut(&mut all[0])[1].status = Some("completed".into());
    let mut foreign = graph_run("other:notice:1", "running");
    foreign.notice_run = Some(true);
    let mut copy = graph_agent("c", "", "running");
    copy.source_block_id = Some("t".into());
    foreign.agents = Some(vec![copy]);
    check(notice::awaiting_notice(&all[0], &[all[0].clone(), foreign], &ko).is_none(), "another request's notice run");
}

fn block(tracker: &ExecutionGraphTracker, tool: &str) -> MightyGraphAgent {
    let id = node::agent_node_id("proc-1", tool);
    let found: Vec<MightyGraphAgent> = tracker.build_run(true).expect("a run").agents().iter().filter(|a| a.id() == id).cloned().collect();
    assert_eq!(found.len(), 1, "one block for {tool}");
    found[0].clone()
}

#[test]
fn a_command_started_in_a_notice_turn_is_settled_by_its_notification() {
    let mut first = new_tracker("proc-1", "survey it");
    // The request starts two sub-agents in the background, answers, and the first one's hand-back opens a notice turn.
    for value in [
        agent_call("a1", "First", Some(true)),
        agent_ack("a1", "ag1"),
        agent_call("a2", "Second", Some(true)),
        agent_ack("a2", "ag2"),
        result("Started two.", false),
    ] {
        first.consume(&value);
    }
    first.consume(&notification_of("ag1", "a1", "First done"));
    // Inside that notice turn it starts a background command, which the turn's end leaves running.
    for value in [
        command("build", "npm run build", "Build the app"),
        launched("build", "bt1"),
        text_frame("Building.", "m2"),
        result("Building.", true),
    ] {
        first.consume(&value);
    }
    same(block(&first, "build").status().to_string(), "running".to_string(), "the command runs after the notice turn");
    // Its own notification arrives later, as a notice turn of its own.
    first.consume(&notification_of("bt1", "build", "Build done"));
    same(block(&first, "build").status().to_string(), "completed".to_string(), "the notification settles the command");
    for value in [text_frame("The build passed.", "m3"), result("The build passed.", true)] {
        first.consume(&value);
    }
    first.finish("completed");
    let run = first.build_run(false).expect("a run");
    let status = |tool: &str| run.agents().iter().find(|a| a.id() == node::agent_node_id("proc-1", tool)).map(|a| a.status().to_string());
    same(status("build").as_deref(), Some("completed"), "the command stays completed when the process ends");
    same(status("a1").as_deref(), Some("completed"), "the first sub-agent");
    same(status("a2").as_deref(), Some("stopped"), "the second one never reported");
    // The live stream carries the end as a system event, no user message: it settles the block and opens the notice run the same way.
    let mut live = new_tracker("proc-1", "build it");
    for value in [command("build", "npm run build", "Build the app"), launched("build", "bt1"), result("Started.", false)] {
        live.consume(&value);
    }
    live.consume(&system_notification("bt1", "build", "Build done", "completed"));
    same(block(&live, "build").status().to_string(), "completed".to_string(), "the system event settles the command");
    live.consume(&text_frame("The build passed.", "m2"));
    live.consume(&result("The build passed.", true));
    live.finish("completed");
    let ended = live.build_run(false).expect("a run");
    let notices = live.take_notice_runs(false);
    same(ended.agents().len(), 1, "one block");
    same(ended.agents()[0].status(), "completed", "it stays completed when the process ends");
    check(
        notices.len() == 1
            && notices[0].agents().len() == 1
            && notices[0].agents()[0].source_block_id == ended.agents()[0].id
            && notices[0].final_output.as_deref() == Some("The build passed."),
        "the notice run holds its copy and Claude's reply",
    );
    // A command whose end the stream names by its task alone settles by the task id the launch gave.
    let mut bare = new_tracker("proc-1", "build it");
    for value in [
        command("build", "npm run build", "Build the app"),
        launched("build", "bt1"),
        system_notification("bt1", "", "Build failed", "failed"),
    ] {
        bare.consume(&value);
    }
    same(block(&bare, "build").status().to_string(), "error".to_string(), "a failed command by its task ID");
}

/// The counts of C# ANoticeRunReadsTheRequestsWaitingCount; the diagram's cards, the pipeline, 기록 and the phone are those modules'.
#[test]
fn a_notice_run_reads_the_requests_waiting_count() {
    let ko = ko();
    let task = |id: &str, status: &str, source: Option<&str>| {
        let mut agent = graph_agent(id, "Job", status);
        agent.kind = Some("task".into());
        agent.background = Some(true);
        agent.source_block_id = source.map(Into::into);
        agent
    };
    let survey = |id: &str, status: &str| {
        let mut agent = graph_agent(id, "Survey", status);
        agent.background = Some(true);
        agent
    };
    let mut request = graph_run("r", "completed");
    request.input = Some("go".into());
    request.final_output = Some("Started.".into());
    request.agents = Some(vec![survey("a1", "completed"), survey("a2", "running"), survey("a3", "running"), task("t1", "running", None)]);
    let mut first = graph_run("r:notice:1", "completed");
    first.notice_run = Some(true);
    first.final_output = Some("First done.".into());
    let mut first_copy = survey("r:notice:1:a1", "completed");
    first_copy.source_block_id = Some("a1".into());
    first.agents = Some(vec![first_copy]);
    let mut second = graph_run("r:notice:2", "completed");
    second.notice_run = Some(true);
    second.final_output = Some("Task step.".into());
    second.agents = Some(vec![task("r:notice:2:t0", "completed", Some("t0"))]);
    let mut next = graph_run("n", "completed");
    next.input = Some("again".into());
    next.final_output = Some("Done.".into());
    next.agents = Some(vec![task("n-t", "running", None)]);
    let mut all = vec![request, first, second, next];
    for run in &mut all {
        support::refresh_result(run);
    }
    // Three unfinished blocks of the request count once for each of its runs; a copy of a finished block adds nothing; the next request counts its own.
    let counts = |all: &[MightyGraphRun]| support::shown_runs(all, &ko).iter().map(support::background_waiting).collect::<Vec<_>>();
    let waiting = counts(&all);
    check(waiting == vec![3, 3, 3, 1], &format!("waiting counts: {waiting:?}"));
    check(
        support::with_background_waiting(&support::shown_runs(&all, &ko)).iter().map(support::background_waiting).collect::<Vec<_>>()
            == vec![3, 3, 3, 1],
        "counting runs already counted changes nothing",
    );
    same(support::follow_run_index(&all[..3]), Some(2), "the newest notice run is followed");
    same(result_title("completed", waiting[2], false, &ko), "현재까지의 결과 · 백그라운드 작업 3개 기다리는 중".to_string(), "the title");
    // The work ends one by one; the notice run goes on reading the count until the last one.
    agents_mut(&mut all[0])[1].status = Some("completed".into());
    agents_mut(&mut all[0])[3].status = Some("completed".into());
    check(counts(&all) == vec![1, 1, 1, 1], "one left");
    agents_mut(&mut all[0])[2].status = Some("stopped".into());
    check(counts(&all) == vec![0, 0, 0, 1], "none left: the final result");
    same(result_title("completed", 0, false, &ko), "최종 결과".to_string(), "the final title");
}

#[test]
fn an_async_launch_result_keeps_the_agent_running() {
    let mut tracker = new_tracker("proc-1", "survey it");
    let structured = json!({"type": "user", "message": {"content": [{"type": "tool_result", "tool_use_id": "a1", "content": "Launched."}]},
        "toolUseResult": {"isAsync": true, "status": "async_launched", "agentId": "ag1"}});
    let textual = json!({"type": "user", "message": {"content": [{"type": "tool_result", "tool_use_id": "a2",
        "content": "Async agent launched successfully.\nagentId: ag2 (internal ID)"}]}});
    let plain =
        json!({"type": "user", "message": {"content": [{"type": "tool_result", "tool_use_id": "a3", "content": "The survey: 3 files."}]}});
    for value in [agent_call("a1", "One", None), structured, agent_call("a2", "Two", None), textual, agent_call("a3", "Three", None), plain]
    {
        tracker.consume(&value);
    }
    let (one, two, three) = (block(&tracker, "a1"), block(&tracker, "a2"), block(&tracker, "a3"));
    check(
        one.status() == "running" && one.background == Some(true) && two.status() == "running" && two.background == Some(true),
        "an async launch result leaves the agent running in the background",
    );
    check(three.status() == "completed" && three.background != Some(true), "an agent that answered is not a background launch");
    // Its hand-back settles it by the agent id the launch named.
    tracker.consume(&notification_of("ag1", "a1", "One done"));
    tracker.consume(&json!({"type": "user", "origin": {"kind": "task-notification"}, "message": {"content":
        "<task-notification>\n<task-id>ag2</task-id>\n<status>completed</status>\n<summary>Two done</summary>\n</task-notification>"}}));
    check(block(&tracker, "a1").status() == "completed" && block(&tracker, "a2").status() == "completed", "the notifications settle them");
}

#[test]
fn a_later_turn_answering_a_held_notification_goes_on_in_its_run() {
    let (mut tracker, emitted) = recording_tracker("proc-1", Some("two commands"), "claude", None);
    let live = |task: &str, tool: &str, run: &str| {
        json!({"type": "system", "subtype": "task_notification", "task_id": task, "tool_use_id": tool, "run_id": run, "status": "completed",
            "output_file": format!("/tmp/{task}.output"), "summary": format!("{task} ended")})
    };
    let answered = |value: &str, run: Option<&str>| match run {
        None => result(value, true),
        Some(run) => json!({"type": "result", "subtype": "success", "is_error": false, "result": value,
            "origin": {"kind": "task-notification", "producer": "session-task", "runId": run}}),
    };
    let nodes =
        || -> HashMap<String, ExecutionGraphNode> { emitted.lock().expect("nodes").iter().map(|n| (n.id.clone(), n.clone())).collect() };
    let notice_runs = || {
        let mut ids: Vec<String> =
            nodes().values().filter(|n| n.kind == "main" && notice::is_notice_run(&n.run_id)).map(|n| n.run_id.clone()).collect();
        ids.sort();
        ids
    };
    let output = |run: &str| nodes().get(&node::main_node_id(run)).and_then(|n| n.output.clone());
    for value in [
        command("one", "sleep 3", "count"),
        launched("one", "b1"),
        command("two", "sleep 2; exit 3", "fail"),
        launched("two", "b2"),
        result("launched.", false),
        live("b2", "two", "r2"),
        live("b1", "one", "r1"),
        text_frame("done.", "m2"),
        answered("done.", Some("r2")),
    ] {
        tracker.consume(&value);
    }
    check(
        notice_runs() == vec!["proc-1:notice:1".to_string()] && output("proc-1:notice:1").as_deref() == Some("done."),
        &format!("both notices joined the first turn's run: {:?}", notice_runs()),
    );
    // The second notification's own turn.
    for value in [text_frame("done.", "m3"), answered("done.", Some("r1"))] {
        tracker.consume(&value);
    }
    check(notice_runs() == vec!["proc-1:notice:1".to_string()], &format!("no empty run: {:?}", notice_runs()));
    same(output("proc-1:notice:1").as_deref(), Some("done.\n\ndone."), "the answer goes on in the run that holds its work");
    same(
        nodes().get(&node::main_node_id("proc-1:notice:1")).map(|n| n.state.clone()).as_deref(),
        Some("completed"),
        "and the run ends as the turn did",
    );
    same(nodes().values().filter(|n| n.run_id == "proc-1:notice:1" && n.source_block_id.is_some()).count(), 2, "both copies");
    // Work no notice run holds, or no task run named: a run of its own, as before.
    for value in [text_frame("Another.", "m4"), answered("Another.", Some("r9")), text_frame("And one.", "m5"), answered("And one.", None)]
    {
        tracker.consume(&value);
    }
    check(
        notice_runs() == vec!["proc-1:notice:1".to_string(), "proc-1:notice:2".to_string(), "proc-1:notice:3".to_string()],
        &format!("an unknown task run and none open runs of their own: {:?}", notice_runs()),
    );
    same(output("proc-1:notice:2").as_deref(), Some("Another."), "an unknown task run's answer");
    same(output("proc-1:notice:3").as_deref(), Some("And one."), "an answer naming no task run");
    same(output("proc-1:notice:1").as_deref(), Some("done.\n\ndone."), "the first run keeps its two answers");
}
