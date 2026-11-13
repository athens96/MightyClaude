//! C# FlowStepsVerification: a request's checklist as steps on its run (FlowSteps.Record, RecordSteps, StepIndex, NodeSuffix,
//! StableHash). The progress panel, the step cards, the layout and the heartbeat replay read mighty-graph's tracker and views and are
//! ported with them.

mod common;

use std::sync::Arc;

use chrono::TimeZone;
use mighty_core::graph::{FlowStep, MightyGraphRun};
use mighty_core::json::Wire;
use mighty_core::model::{AppSnapshot, RunSession, TodoItem, TodoProgress};
use mighty_core::time::{self, DateTimeOffset, ticks_of};
use mighty_run::flow_steps::{self, SUFFIX};
use mighty_run::model::RunEvent;
use mighty_run::plan_mode::{self, TodoProgressTracker, todos};

fn at(seconds: f64) -> DateTimeOffset {
    let t0 = ticks_of(chrono::Utc.timestamp_opt(1_800_000_000, 0).unwrap());
    DateTimeOffset::from_utc_ticks(t0 + (seconds * 10_000_000.0) as i64)
}

fn stamp(seconds: f64) -> String {
    flow_steps::stamp(at(seconds))
}

fn item(content: &str, status: &str, id: Option<&str>) -> TodoItem {
    plan_mode::todo_item(content, status, Some(&format!("{content} 중")), id)
}

fn list(items: Vec<TodoItem>, run: &str) -> TodoProgress {
    TodoProgress { items: Some(items), run_id: Some(run.into()) }
}

fn request(id: &str, status: &str) -> MightyGraphRun {
    MightyGraphRun {
        id: Some(id.into()),
        input: Some("릴리스".into()),
        status: Some(status.into()),
        root_entries: Some(vec![]),
        agents: Some(vec![]),
        source_run_id: Some(id.into()),
        provider: Some("claude".into()),
        ..MightyGraphRun::default()
    }
}

fn started(step: &FlowStep) -> Option<DateTimeOffset> {
    time::parse(step.started_at.as_deref())
}

fn ended(step: &FlowStep) -> Option<DateTimeOffset> {
    time::parse(step.ended_at.as_deref())
}

fn step(key: &str, content: &str, status: Option<&str>, started: Option<f64>, ended: Option<f64>) -> FlowStep {
    FlowStep {
        key: Some(key.into()),
        content: Some(content.into()),
        active_form: None,
        status: status.map(str::to_string),
        started_at: started.map(stamp),
        ended_at: ended.map(stamp),
    }
}

#[test]
fn each_change_keeps_the_times_of_the_steps_it_had() {
    common::korean();
    let mut runs = vec![request("r", "running")];
    flow_steps::record(&list(vec![item("점검", "in_progress", None), item("노트", "pending", None)], "r"), &mut runs, None, at(0.0));
    flow_steps::record(&list(vec![item("점검", "completed", None), item("노트", "in_progress", None)], "r"), &mut runs, None, at(10.0));
    flow_steps::record(
        &list(vec![item("점검", "completed", None), item("노트", "completed", None), item("CI", "pending", None)], "r"),
        &mut runs,
        None,
        at(25.0),
    );
    let steps = runs[0].steps.clone().unwrap();
    assert!(
        steps.iter().map(|s| s.content.clone().unwrap()).collect::<Vec<_>>() == ["점검", "노트", "CI"]
            && steps.iter().map(|s| s.status.clone().unwrap()).collect::<Vec<_>>() == ["completed", "completed", "pending"],
        "the steps and their states"
    );
    assert!(
        started(&steps[0]) == Some(at(0.0))
            && ended(&steps[0]) == Some(at(10.0))
            && started(&steps[1]) == Some(at(10.0))
            && ended(&steps[1]) == Some(at(25.0)),
        "each began and ended when the list said so"
    );
    assert!(
        steps[2].started_at.is_none()
            && steps[2].ended_at.is_none()
            && steps.iter().map(|s| s.active_form.clone().unwrap()).collect::<Vec<_>>() == ["점검 중", "노트 중", "CI 중"],
        "a step not begun, and the active forms"
    );

    // Marked done without going in progress: it began when the one before it ended.
    let mut skipped = vec![request("r", "running")];
    flow_steps::record(&list(vec![item("하나", "in_progress", None), item("둘", "pending", None)], "r"), &mut skipped, None, at(0.0));
    flow_steps::record(&list(vec![item("하나", "completed", None), item("둘", "pending", None)], "r"), &mut skipped, None, at(5.0));
    flow_steps::record(&list(vec![item("하나", "completed", None), item("둘", "completed", None)], "r"), &mut skipped, None, at(9.0));
    let second = &skipped[0].steps.as_ref().unwrap()[1];
    assert!(started(second) == Some(at(5.0)) && ended(second) == Some(at(9.0)), "a step done at once began at the last end");

    // A task found by its id when its words change.
    let mut tasks = vec![request("r", "running")];
    flow_steps::record(&list(vec![item("빌드", "in_progress", Some("1"))], "r"), &mut tasks, None, at(0.0));
    flow_steps::record(&list(vec![item("빌드와 서명", "in_progress", Some("1"))], "r"), &mut tasks, None, at(4.0));
    let only = tasks[0].steps.clone().unwrap();
    assert!(
        only.len() == 1 && only[0].content.as_deref() == Some("빌드와 서명") && started(&only[0]) == Some(at(0.0)),
        "the same task, renamed"
    );

    // The earlier request's done tasks are never this request's steps.
    let earlier = list(vec![item("이전 일", "completed", Some("1"))], "old");
    let mut carried = vec![request("old", "completed"), request("r", "running")];
    flow_steps::record(
        &list(vec![item("이전 일", "completed", Some("1")), item("새 일", "in_progress", Some("2"))], "r"),
        &mut carried,
        Some(&earlier),
        at(0.0),
    );
    flow_steps::record(
        &list(vec![item("이전 일", "completed", Some("1")), item("새 일", "completed", Some("2"))], "r"),
        &mut carried,
        Some(&earlier),
        at(3.0),
    );
    assert!(
        carried[1].steps.as_ref().unwrap().iter().map(|s| s.content.clone().unwrap()).collect::<Vec<_>>() == ["새 일"]
            && carried[0].steps.is_none(),
        "carried over, and stays out"
    );

    let mut elsewhere = vec![request("r", "running")];
    flow_steps::record(&list(vec![item("x", "in_progress", None)], "nobody"), &mut elsewhere, None, at(0.0));
    assert!(elsewhere[0].steps.is_none(), "a list of no run of the pane changes nothing");
}

#[test]
fn the_pane_keeps_the_steps_through_its_events() {
    common::korean();
    let pane = RunSession {
        id: Some("s".into()),
        kind: Some("claude".into()),
        provider: Some("claude".into()),
        status: Some("running".into()),
        graph_runs: Some(vec![request("r", "running")]),
        ..RunSession::default()
    };
    let apply = |snapshot: &AppSnapshot, event: &RunEvent| mighty_run::apply::apply(snapshot, event, DateTimeOffset::now());
    let mut event = RunEvent::new("s", "todos");
    event.todos = Some(list(vec![item("점검", "in_progress", None)], "r"));
    let mut snapshot = apply(&AppSnapshot { sessions: Some(vec![Arc::new(pane)]), ..AppSnapshot::default() }, &event);
    let after = snapshot.sessions.as_ref().unwrap()[0].clone();
    let steps = after.graph_runs.as_ref().unwrap()[0].steps.clone().unwrap_or_default();
    assert!(
        steps.len() == 1
            && steps[0].status.as_deref() == Some("in_progress")
            && steps[0].started_at.is_some()
            && todos::items(after.todo_progress.as_ref().unwrap()).len() == 1,
        "the step is on the request and the list on the pane"
    );
    // The run pipeline's graph of the request carries no checklist: the steps stay.
    let mut fresh = RunEvent::new("s", "graph_run");
    fresh.graph_run = Some(request("r", "running"));
    snapshot = apply(&snapshot, &fresh);
    let run = snapshot.sessions.as_ref().unwrap()[0].graph_runs.as_ref().unwrap()[0].clone();
    assert_eq!(run.steps.as_ref().map(Vec::len), Some(1), "a graph run keeps the steps it had");
    // Saved and read back.
    let json = run.to_json();
    assert!(
        json.contains("\"steps\":[{\"key\":\"text:점검\"") || json.contains("\"steps\":[{\"key\":\"text:\\uC810\\uAC80\""),
        "written as steps: {}",
        &json[..json.len().min(300)]
    );
    let back = MightyGraphRun::read(&common::json(&json)).unwrap();
    assert!(back.steps.as_ref().is_some_and(|s| s.len() == 1 && s[0] == run.steps.as_ref().unwrap()[0]), "read back");
}

/// The StepIndex, NodeSuffix and StableHash half (the cards are the views').
#[test]
fn a_step_holds_what_its_span_holds_and_its_card_says_it() {
    let steps = vec![
        step("a", "a", Some("completed"), Some(10.0), Some(20.0)),
        step("b", "b", Some("in_progress"), Some(20.4), None),
        step("c", "c", None, None, None),
    ];
    assert!(
        flow_steps::step_index(at(5.0), &steps).is_none()
            && flow_steps::step_index(at(10.0), &steps) == Some(0)
            && flow_steps::step_index(at(15.0), &steps) == Some(0),
        "inside the first span"
    );
    assert!(
        flow_steps::step_index(at(20.0), &steps) == Some(1) && flow_steps::step_index(at(99.0), &steps) == Some(1),
        "where two meet, the later; from its whole second"
    );
    let words = |content: &str| step("text:릴리스 노트 / 검토", content, None, None, None);
    let suffix = flow_steps::node_suffix(&words("x"));
    assert!(
        suffix.starts_with(SUFFIX)
            && suffix[SUFFIX.len()..].chars().all(|c| c.is_ascii_hexdigit())
            && suffix == flow_steps::node_suffix(&words("다른 말")),
        "a node id never carries the step's words"
    );
    assert!(
        flow_steps::stable_hash("") == "cbf29ce484222325" && flow_steps::stable_hash("a") == "af63dc4c8601ec8c",
        "FNV-1a 64 as the Mac writes it"
    );
}

/// An ACP agent's plan update (opencode, Hermes) is its checklist and so the request's steps; a pane of any agent keeps them.
#[test]
fn an_acp_agents_plan_is_the_requests_steps() {
    common::korean();
    let frame = common::json(
        r#"{"type":"acp.update","update":{"sessionUpdate":"plan","entries":[{"content":"Read the README","status":"completed","priority":"high"},{"content":"Summarize it","status":"in_progress","priority":"medium"},{"content":"Answer","status":"odd","priority":"low"}]}}"#,
    );
    let progress = TodoProgressTracker::new(None).consume(&frame).expect("the plan reads as a checklist");
    assert!(
        todos::items(&progress).iter().map(|i| i.content.clone().unwrap()).collect::<Vec<_>>()
            == ["Read the README", "Summarize it", "Answer"]
            && todos::items(&progress).iter().map(|i| i.status.clone().unwrap()).collect::<Vec<_>>()
                == ["completed", "in_progress", "pending"],
        "the plan's entries and their statuses"
    );
    let pane = RunSession {
        id: Some("s".into()),
        kind: Some("claude".into()),
        provider: Some("opencode".into()),
        graph_runs: Some(vec![MightyGraphRun {
            id: Some("r".into()),
            input: Some("요약".into()),
            status: Some("running".into()),
            source_run_id: Some("r".into()),
            provider: Some("opencode".into()),
            ..MightyGraphRun::default()
        }]),
        ..RunSession::default()
    };
    let mut event = RunEvent::new("s", "todos");
    event.todos = Some(TodoProgress { run_id: Some("r".into()), ..progress });
    let recorded = flow_steps::record_steps(pane, &event, at(1.0));
    let statuses: Vec<String> =
        recorded.graph_runs.unwrap()[0].steps.clone().unwrap_or_default().into_iter().map(|s| s.status.unwrap_or_default()).collect();
    assert_eq!(statuses, ["completed", "in_progress", "pending"], "an opencode pane keeps the steps on its request");
}
