//! C# ClaudePlanModeVerification over the fixture macOS ClaudePlanModeTests reads (native/contracts/fixtures/claude-plan-mode.json):
//! both clients recognise, answer and track Claude's plan mode alike. The two checks over a real child are in runner.rs.

mod common;

use std::sync::{Arc, Mutex};

use chrono::TimeZone;
use mighty_core::json::Wire;
use mighty_core::model::{AppSnapshot, BackgroundWork, PlanRecord, RunSession, RunSettings, TodoItem, TodoProgress, Workspace};
use mighty_core::time::{DateTimeOffset, ticks_of};
use mighty_run::model::{RunEvent, StartRunRequest};
use mighty_run::permissions::{ChannelHooks, ClaudePermissionChannel, ToolPermissionRequest};
use mighty_run::plan_mode::{
    self, BackgroundTaskTracker, MAXIMUM_HISTORY, MAXIMUM_HISTORY_BYTES, MAXIMUM_STORED_PLAN_BYTES, PlanApprovalRequest, PlanDecision,
    TOO_LONG_MESSAGE, TodoProgressTracker, outcome, todos, work,
};
use serde_json::Value;

const FIXED_STAMP: &str = "2027-01-15T08:00:00.000Z";

fn fixed() -> DateTimeOffset {
    DateTimeOffset::from_utc_ticks(ticks_of(chrono::Utc.timestamp_opt(1_800_000_000, 0).unwrap()))
}

fn fixture() -> Value {
    common::fixture_json("claude-plan-mode.json")
}

fn decision(row: &Value) -> PlanDecision {
    match row["decision"].as_str() {
        Some("approveAutoEdit") => PlanDecision::approve_auto_edit(),
        Some("approveConfirmEach") => PlanDecision::approve_confirm_each(),
        Some("revise") => PlanDecision::revise(row["feedback"].as_str().unwrap()),
        _ => PlanDecision::cancel(),
    }
}

fn optional<'a>(row: &'a Value, key: &str) -> Option<&'a str> {
    row.get(key).and_then(Value::as_str)
}

#[derive(Default)]
struct Lists {
    writes: Vec<String>,
    warnings: Vec<String>,
    displays: Vec<ToolPermissionRequest>,
    records: Vec<PlanRecord>,
}

struct Channel {
    lists: Arc<Mutex<Lists>>,
    value: ClaudePermissionChannel,
}

impl Channel {
    fn new(pane_mode: &str) -> Channel {
        common::korean();
        let lists = Arc::new(Mutex::new(Lists::default()));
        let (w, e, warn, p) = (lists.clone(), lists.clone(), lists.clone(), lists.clone());
        let hooks = ChannelHooks {
            write: Arc::new(move |text| {
                w.lock().unwrap().writes.push(text.to_string());
                Ok(())
            }),
            emit: Arc::new(move |value| e.lock().unwrap().displays.push(value)),
            activity: Arc::new(|_, _| {}),
            warning: Arc::new(move |message| warn.lock().unwrap().warnings.push(message.to_string())),
            fail: Arc::new(|_| {}),
            plan: Arc::new(move |record| p.lock().unwrap().records.push(record)),
            clock: Arc::new(fixed),
        };
        Channel { lists, value: ClaudePermissionChannel::new("run", "", hooks, pane_mode) }
    }

    fn writes(&self) -> Vec<String> {
        self.lists.lock().unwrap().writes.clone()
    }
    fn displays(&self) -> Vec<ToolPermissionRequest> {
        self.lists.lock().unwrap().displays.clone()
    }
    fn records(&self) -> Vec<PlanRecord> {
        self.lists.lock().unwrap().records.clone()
    }
    fn warnings(&self) -> Vec<String> {
        self.lists.lock().unwrap().warnings.clone()
    }

    fn last_response(&self) -> Value {
        let document = common::json(self.writes().last().unwrap());
        assert!(
            document["type"] == "control_response" && document["response"]["subtype"] == "success",
            "plan answers are successful control responses"
        );
        document["response"]["response"].clone()
    }
}

fn plan(id: &str, plan: Option<&str>) -> String {
    serde_json::json!({
        "type": "control_request",
        "request_id": id,
        "request": {
            "subtype": "can_use_tool",
            "tool_name": "ExitPlanMode",
            "tool_use_id": format!("toolu_{id}"),
            "input": {"plan": plan.map(str::to_string).unwrap_or_else(|| format!("Plan {id}"))},
            "requires_user_interaction": true,
        },
    })
    .to_string()
}

#[test]
fn shared_requests() {
    let rows = fixture()["planRequests"].as_array().unwrap().clone();
    assert!(rows.len() >= 3, "shared fixture was not loaded");
    for row in rows {
        let channel = Channel::new("plan");
        channel.value.receive(&row["request"].to_string()).unwrap();
        let (expected, name) = (&row["expected"], row["name"].as_str().unwrap());
        let displays = channel.displays();
        assert_eq!(displays.len(), 1, "{name}");
        let display = &displays[0];
        assert!(
            display.can_answer_plan == expected["canAnswerPlan"].as_bool().unwrap()
                && display.can_allow == expected["canAllow"].as_bool().unwrap(),
            "flags differ from Mac: {name}"
        );
        assert_eq!(display.plan().as_deref(), optional(expected, "plan"), "plan text differs from Mac: {name}");
        assert_eq!(display.received_at.as_deref(), Some(FIXED_STAMP), "received time is the channel's clock");
        let approval = PlanApprovalRequest::from("pane-1", display);
        assert_eq!(approval.is_none(), !display.can_answer_plan, "only an answerable plan is a plan approval");
        if let Some(approval) = approval {
            assert_eq!(
                approval,
                PlanApprovalRequest {
                    session_id: "pane-1".into(),
                    run_id: "run".into(),
                    request_id: display.id.clone(),
                    tool_use_id: display.tool_use_id.clone(),
                    plan: display.plan().unwrap(),
                    received_at: FIXED_STAMP.into(),
                },
                "plan approval holds the pane, request, plan and time"
            );
        }
    }
}

#[test]
fn shared_responses() {
    common::korean();
    let rows = fixture()["planResponses"].as_array().unwrap().clone();
    assert!(rows.len() >= 10, "shared fixture was not loaded");
    for row in rows {
        let name = row["name"].as_str().unwrap();
        let value =
            plan_mode::response(&decision(&row), row["toolUseId"].as_str().unwrap(), Some(optional(&row, "paneMode").unwrap_or("plan")));
        if row.get("error").and_then(Value::as_bool) == Some(true) {
            assert!(value.is_err(), "expected a refusal: {name}");
            continue;
        }
        assert_eq!(value.unwrap(), row["expected"], "response differs from Mac: {name}");
    }
}

#[test]
fn channel_answers_once_and_keeps_every_plan() {
    let channel = Channel::new("plan");
    channel.value.receive(&plan("p1", Some("# Plan\n- one"))).unwrap();
    let displays = channel.displays();
    assert!(
        displays.len() == 1 && displays[0].can_answer_plan && !displays[0].can_allow && displays[0].reason.is_none(),
        "a plan is answerable as a plan (no separate-screen notice), never by the generic allow"
    );
    assert!(channel.value.respond("p1", true).is_err());
    assert!(channel.value.answer_plan("p1", &PlanDecision::revise("  ")).is_err());
    assert!(channel.writes().is_empty() && channel.records().is_empty(), "an empty revise leaves the plan answerable and writes nothing");
    let record = channel.value.answer_plan("p1", &PlanDecision::approve_auto_edit()).unwrap();
    let body = channel.last_response();
    assert!(body["behavior"] == "allow" && body["updatedInput"].as_object().unwrap().is_empty(), "approval echoes no injected plan");
    assert!(
        body["updatedPermissions"][0]["mode"] == "acceptEdits" && body["updatedPermissions"][0]["destination"] == "session",
        "approval switches the session mode only"
    );
    assert!(
        record.outcome.as_deref() == Some(outcome::APPROVED_AUTO)
            && record.plan.as_deref() == Some("# Plan\n- one")
            && record.id.as_deref() == Some("p1")
            && record.run_id.as_deref() == Some("run")
            && channel.records() == [record.clone()]
            && channel.displays().last().unwrap().state == "allowed",
        "approval is recorded once"
    );
    assert!(channel.value.answer_plan("p1", &PlanDecision::cancel()).is_err());

    channel.value.receive(&plan("r1", None)).unwrap();
    channel.value.answer_plan("r1", &PlanDecision::revise(" Split step 2 ")).unwrap();
    let last = channel.records().last().cloned().unwrap();
    assert!(
        channel.last_response()["message"].as_str().unwrap().ends_with("The user said:\nSplit step 2")
            && last.outcome.as_deref() == Some(outcome::REVISED)
            && last.feedback.as_deref() == Some("Split step 2"),
        "revise sends and keeps the feedback"
    );
    channel.value.receive(&plan("r2", None)).unwrap();
    channel.value.answer_plan("r2", &PlanDecision::cancel()).unwrap();
    assert!(
        channel.last_response()["interrupt"] == true
            && channel.records().last().unwrap().outcome.as_deref() == Some(outcome::CANCELLED)
            && channel.displays().last().unwrap().state == "denied",
        "cancel interrupts"
    );
    channel.value.receive(&plan("r3", None)).unwrap();
    channel.value.receive(r#"{"type":"control_cancel_request","request_id":"r3"}"#).unwrap();
    channel.value.receive(&plan("r4", None)).unwrap();
    channel.value.cancel_all();
    let records = channel.records();
    assert!(
        records.iter().map(|r| r.id.clone().unwrap()).collect::<Vec<_>>() == ["p1", "r1", "r2", "r3", "r4"]
            && records[3..].iter().all(|r| r.outcome.as_deref() == Some(outcome::CANCELLED)),
        "withdrawn and unanswered plans are kept as cancelled"
    );

    let bash = Channel::new("plan");
    bash.value
        .receive(r#"{"type":"control_request","request_id":"b1","request":{"subtype":"can_use_tool","tool_name":"Bash","tool_use_id":"toolu_b1","input":{"command":"ls"}}}"#)
        .unwrap();
    assert!(bash.value.answer_plan("b1", &PlanDecision::approve_auto_edit()).is_err());
    // A generic deny (an older card, the phone) still keeps the plan, as cancelled.
    bash.value.receive(&plan("p2", Some("# Two"))).unwrap();
    bash.value.respond("p2", false).unwrap();
    bash.value.respond("b1", false).unwrap();
    let records = bash.records();
    assert!(
        records.len() == 1
            && records[0].id.as_deref() == Some("p2")
            && records[0].outcome.as_deref() == Some(outcome::CANCELLED)
            && records[0].plan.as_deref() == Some("# Two"),
        "a generic deny of a plan is kept as cancelled"
    );

    let big = Channel::new("plan");
    big.value.receive(&plan("big", Some(&"x".repeat(70_000)))).unwrap();
    assert!(
        big.last_response()["behavior"] == "deny" && big.last_response()["message"] == TOO_LONG_MESSAGE,
        "a plan too long to show is sent back"
    );
    assert!(big.displays().is_empty() && big.warnings() == [mighty_run::strings::get("plan.warning.tooLong")], "the user is told why");
    let records = big.records();
    assert!(
        records.len() == 1 && records[0].outcome.as_deref() == Some(outcome::CANCELLED) && records[0].plan_truncated == Some(true),
        "the too-long plan is still kept, bounded, as cancelled"
    );
}

fn item(row: &Value) -> TodoItem {
    plan_mode::todo_item(
        row["content"].as_str().unwrap(),
        row["status"].as_str().unwrap(),
        optional(row, "activeForm"),
        optional(row, "id"),
    )
}

#[test]
fn shared_checklists() {
    let rows = fixture()["todos"].as_array().unwrap().clone();
    assert!(rows.len() >= 4, "shared fixture was not loaded");
    for row in rows {
        let saved =
            row.get("saved").map(|items| TodoProgress { items: Some(items.as_array().unwrap().iter().map(item).collect()), run_id: None });
        let mut tracker = TodoProgressTracker::new(saved.as_ref());
        let name = row["name"].as_str().unwrap();
        for frame in row["frames"].as_array().unwrap() {
            tracker.consume(frame);
        }
        let expected = &row["expected"];
        let progress = tracker.progress.clone().unwrap_or_else(|| panic!("no progress: {name}"));
        assert!(
            todos::total(&progress) as u64 == expected["total"].as_u64().unwrap()
                && todos::completed(&progress) as u64 == expected["completed"].as_u64().unwrap()
                && todos::current_text(&progress).as_deref() == optional(expected, "current"),
            "counts differ from Mac: {name}"
        );
        let items: Vec<TodoItem> = expected["items"].as_array().unwrap().iter().map(item).collect();
        assert_eq!(todos::items(&progress), items.as_slice(), "items differ from Mac: {name}");
    }
}

#[test]
fn shared_background_runs() {
    let rows = fixture()["background"].as_array().unwrap().clone();
    assert!(rows.len() >= 3, "shared fixture was not loaded");
    for row in rows {
        let mut tracker = BackgroundTaskTracker::new(Some(Box::new(fixed)));
        let name = row["name"].as_str().unwrap();
        for step in row["steps"].as_array().unwrap() {
            match optional(step, "step") {
                Some("turnEnded") => {
                    tracker.turn_ended();
                }
                Some("finish") => {
                    tracker.finish();
                }
                _ => {
                    tracker.consume(step);
                }
            }
        }
        let expected = &row["expected"];
        let work = tracker.work();
        assert_eq!(work.turn_ended, expected["turnEnded"].as_bool().unwrap(), "turn end differs from Mac: {name}");
        let tasks = work::tasks(&work);
        let wanted = expected["tasks"].as_array().unwrap();
        assert_eq!(tasks.len(), wanted.len(), "task count differs from Mac: {name}");
        for (task, want) in tasks.iter().zip(wanted) {
            let id = task.id.clone().unwrap_or_default();
            assert!(
                task.id.as_deref() == optional(want, "id")
                    && task.tool_use_id.as_deref() == optional(want, "toolUseId")
                    && task.kind.as_deref() == optional(want, "kind")
                    && task.description.as_deref() == optional(want, "description"),
                "task identity differs from Mac: {name} {id}"
            );
            assert!(
                task.status.as_deref() == optional(want, "status")
                    && task.summary.as_deref() == optional(want, "summary")
                    && task.ended_at.is_some() == want["ended"].as_bool().unwrap()
                    && task.started_at.as_deref() == Some(FIXED_STAMP),
                "task state differs from Mac: {name} {id}"
            );
        }
    }
}

#[test]
fn approval_never_lowers_the_pane_mode() {
    let mode = |d: PlanDecision, pane: &str| outcome::approved_cli_mode(&d, Some(pane));
    assert!(
        mode(PlanDecision::approve_confirm_each(), "plan") == Some("default")
            && mode(PlanDecision::approve_auto_edit(), "plan") == Some("acceptEdits"),
        "a plan pane takes the button"
    );
    assert!(
        mode(PlanDecision::approve_confirm_each(), "manual") == Some("default")
            && mode(PlanDecision::approve_auto_edit(), "manual") == Some("acceptEdits"),
        "a manual pane takes the higher"
    );
    assert!(
        mode(PlanDecision::approve_confirm_each(), "acceptEdits") == Some("acceptEdits")
            && mode(PlanDecision::approve_auto_edit(), "auto") == Some("auto")
            && mode(PlanDecision::approve_confirm_each(), "fullAccess") == Some("bypassPermissions"),
        "approval never lowers the pane's mode"
    );
    assert!(
        mode(PlanDecision::revise("x"), "fullAccess").is_none() && mode(PlanDecision::cancel(), "plan").is_none(),
        "only approvals switch modes"
    );
    let channel = Channel::new("auto");
    channel.value.receive(&plan("a1", None)).unwrap();
    channel.value.answer_plan("a1", &PlanDecision::approve_confirm_each()).unwrap();
    assert_eq!(channel.last_response()["updatedPermissions"][0]["mode"], "auto", "the channel uses the pane's stored mode");
}

#[test]
fn per_run_plan_override() {
    common::korean();
    let mut request = StartRunRequest::new("pane", "ws", "claude", "plan it")
        .with_settings(RunSettings { permission_mode: "manual".into(), ..RunSettings::default() });
    request.permission_mode_override = Some("plan".into());
    let args = mighty_run::providers::arguments(&request, "/tmp/plugin", None).unwrap();
    assert!(
        common::after(&args, "--permission-mode") == "plan" && request.settings().permission_mode == "manual",
        "the override launches this request in plan"
    );
    // "the override is never serialized": the Rust request has no serializer; the remote reads only the C# wire fields (service.rs).
    let mut auto = request.clone();
    auto.permission_mode_override = Some("auto".into());
    assert!(auto.validate().is_err());
    let mut codex = StartRunRequest::new("pane", "ws", "claude", "x").with_provider("codex");
    codex.permission_mode_override = Some("plan".into());
    assert!(codex.validate().is_err());
}

#[test]
fn history_keeps_new_plans_whole_within_its_budget() {
    let lines = (1..=400).map(|i| format!("Step {i}: {}", "x".repeat(60))).collect::<Vec<_>>().join("\n");
    let mut history: Option<Vec<PlanRecord>> = None;
    for i in 0..10 {
        let record = plan_mode::plan_record(
            &format!("h{i}"),
            "run",
            &lines,
            "2026-10-06T01:00:00.000Z",
            "2026-10-06T01:01:00.000Z",
            outcome::REVISED,
            Some(&"f".repeat(3_000)),
        );
        history = Some(plan_mode::appending(&record, history.as_ref()));
    }
    let history = history.unwrap();
    let bytes: usize = history.iter().map(|r| r.plan.as_deref().unwrap().len() + r.feedback.as_deref().unwrap_or("").len()).sum();
    assert!(history.len() == 10 && bytes <= MAXIMUM_HISTORY_BYTES + 10 * 1_536, "history stays within its budget");
    assert!(history[8..].iter().all(|r| r.plan.as_deref().unwrap().encode_utf16().count() > 1_024), "the newest plans stay whole");
    let oldest = &history[0];
    assert!(
        oldest.plan_truncated == Some(true)
            && oldest.plan.as_deref().unwrap().split('\n').count() == 8
            && oldest.plan.as_deref().unwrap().starts_with("Step 1:")
            && oldest.feedback.as_deref().unwrap().encode_utf16().count() <= 512,
        "older plans keep their first lines"
    );
    assert_eq!(plan_mode::normalized_history(Some(&history)).unwrap(), history, "normalizing keeps a budgeted history as is");
}

/// `AppSnapshot.Apply`: the public snapshot path is how the app applies events.
fn apply(session: &RunSession, event: &RunEvent) -> RunSession {
    let snapshot = AppSnapshot { sessions: Some(vec![Arc::new(session.clone())]), ..AppSnapshot::default() };
    let applied = mighty_run::apply::apply(&snapshot, event, DateTimeOffset::now());
    applied.sessions.unwrap()[0].as_ref().clone()
}

fn settings(mode: &str) -> Option<RunSettings> {
    Some(RunSettings { permission_mode: mode.into(), ..RunSettings::default() })
}

fn todo_progress(items: Vec<TodoItem>, run_id: Option<&str>) -> TodoProgress {
    TodoProgress { items: Some(items), run_id: run_id.map(str::to_string) }
}

#[test]
fn pane_keeps_the_plan_mode_and_history() {
    common::korean();
    let mut session =
        RunSession { id: Some("pane".into()), workspace_id: Some("ws".into()), settings: settings("plan"), ..RunSession::default() };
    let record = |id: &str, outcome: &str, plan: &str| {
        let mut event = RunEvent::new("pane", "plan");
        let feedback = (outcome == plan_mode::outcome::REVISED).then_some("more");
        event.plan =
            Some(plan_mode::plan_record(id, "run", plan, "2026-10-06T01:00:00.000Z", "2026-10-06T01:01:00.000Z", outcome, feedback));
        event
    };
    let mode = |s: &RunSession| s.settings.as_ref().unwrap().permission_mode.clone();
    session = apply(&session, &record("a", outcome::REVISED, "# Plan"));
    assert!(mode(&session) == "plan" && session.plan_history.as_ref().unwrap().len() == 1, "revise keeps plan mode");
    session = apply(&session, &record("b", outcome::APPROVED_AUTO, "# Plan"));
    assert_eq!(mode(&session), "acceptEdits", "auto-edit approval saves acceptEdits");
    session = apply(&session, &record("c", outcome::APPROVED_CONFIRM, &"가".repeat(20_000)));
    let last = session.plan_history.as_ref().unwrap().last().unwrap().clone();
    assert!(
        mode(&session) == "acceptEdits" && last.plan_truncated == Some(true) && last.plan.unwrap().len() <= MAXIMUM_STORED_PLAN_BYTES,
        "a pane not stored in plan keeps its mode; the stored plan is bounded"
    );
    session.settings = settings("plan");
    session = apply(&session, &record("c2", outcome::APPROVED_CONFIRM, "# Plan"));
    assert_eq!(mode(&session), "manual", "confirm-each from a plan pane saves manual");
    for i in 0..12 {
        session = apply(&session, &record(&format!("n{i}"), outcome::CANCELLED, "# Plan"));
    }
    let history = session.plan_history.clone().unwrap();
    assert!(
        history.len() == MAXIMUM_HISTORY && history.last().unwrap().id.as_deref() == Some("n11") && mode(&session) == "manual",
        "history is capped, newest last"
    );
    let codex =
        RunSession { id: Some("codex".into()), workspace_id: Some("ws".into()), provider: Some("codex".into()), ..RunSession::default() };
    let codex = apply(&codex, &record("x", outcome::APPROVED_AUTO, "# Plan"));
    assert!(codex.plan_history.is_none() && mode(&codex) == "manual", "only Claude panes keep plans");
    let mut todos_event = RunEvent::new("codex", "todos");
    todos_event.todos = Some(todo_progress(vec![plan_mode::todo_item("a", "in_progress", None, None)], Some("run-1")));
    let codex = apply(&codex, &todos_event);
    let progress = codex.todo_progress.clone().unwrap();
    assert!(
        todos::total(&progress) == 1 && progress.run_id.as_deref() == Some("run-1"),
        "a Codex pane keeps its checklist and the request it belongs to"
    );
    let pending = || vec![plan_mode::todo_item("a", "pending", None, None)];
    assert!(
        plan_mode::normalized_todos(Some(&todo_progress(pending(), Some("not an id!")))).unwrap().run_id.is_none()
            && todos::same_as(&todo_progress(pending(), Some("r1")), Some(&todo_progress(pending(), Some("r2")))),
        "an invalid run id is dropped, and the list alone decides sameness"
    );
    // A resumed run that sends the saved list again takes it over once (the host names it this run); again, nothing.
    let saved = todo_progress(vec![plan_mode::todo_item("Step", "in_progress", None, None)], Some("old-run"));
    let mut resumed = TodoProgressTracker::new(Some(&saved));
    let same = common::json(
        r#"{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t1","name":"TodoWrite","input":{"todos":[{"content":"Step","status":"in_progress"}]}}]}}"#,
    );
    assert!(
        resumed.consume(&same).is_some_and(|p| todos::total(&p) == 1) && resumed.consume(&same).is_none(),
        "a resumed run's first list goes out even when it is the saved one, then only changes do"
    );
    let mut background = RunEvent::new("pane", "background");
    background.background = Some(BackgroundWork {
        tasks: Some(vec![plan_mode::background_task("t", "agent", "d", "2026-10-06T01:00:00.000Z")]),
        turn_ended: false,
    });
    session = apply(&session, &background);
    assert_eq!(work::running(session.background_work.as_ref().unwrap()).len(), 1, "background work is kept");
    session = apply(&session, &RunEvent::state("pane", "running", None));
    assert!(session.background_work.is_none(), "a new run starts without the previous run's background work");
    let mut weird = RunEvent::new("pane", "todos");
    weird.todos = Some(todo_progress(vec![plan_mode::todo_item("a", "weird", None, None)], None));
    session = apply(&session, &weird);
    assert_eq!(
        todos::items(session.todo_progress.as_ref().unwrap())[0].status.as_deref(),
        Some("pending"),
        "unknown checklist states load as pending"
    );
    let mut valid_plan = RunEvent::new("pane", "plan");
    valid_plan.plan = Some(plan_mode::plan_record("p", "r", "x", "t", "t", outcome::REVISED, None));
    let mut empty_todos = RunEvent::new("pane", "todos");
    empty_todos.todos = Some(todo_progress(vec![], None));
    assert!(valid_plan.valid() && !RunEvent::new("pane", "plan").valid() && empty_todos.valid(), "plan-mode events are valid wire events");
}

#[test]
fn saved_state_round_trips_and_old_snapshots_load() {
    common::korean();
    const OLD: &str = r#"{"id":"pane","workspaceId":"ws","title":"Claude","kind":"claude","provider":"claude","model":"default","settings":{"effort":"default","permissionMode":"plan"},"status":"completed","logs":[],"createdAt":"2026-10-01T00:00:00Z"}"#;
    let read = |text: &str| RunSession::read(&common::json(text)).unwrap();
    let decoded = read(OLD);
    assert!(
        decoded.plan_history.is_none() && decoded.todo_progress.is_none() && decoded.background_work.is_none(),
        "old snapshots load without plan state"
    );
    let session = RunSession {
        plan_history: Some(vec![plan_mode::plan_record(
            "p",
            "r",
            "# P",
            "2026-10-06T01:00:00.000Z",
            "2026-10-06T01:01:00.000Z",
            outcome::APPROVED_CONFIRM,
            None,
        )]),
        todo_progress: Some(todo_progress(vec![plan_mode::todo_item("a", "completed", None, Some("1"))], Some("graph-run-1"))),
        background_work: Some(BackgroundWork {
            tasks: Some(vec![plan_mode::background_task("t", "shell", "d", "2026-10-06T01:00:00.000Z")]),
            turn_ended: true,
        }),
        ..decoded.clone()
    };
    let json = session.to_json();
    assert!(
        json.contains("\"planHistory\"")
            && json.contains("\"todoProgress\":{\"items\"")
            && json.contains("\"backgroundWork\"")
            && !json.contains("\"total\""),
        "the Mac field names are written"
    );
    let again = read(&json);
    assert!(
        again.plan_history == session.plan_history
            && todos::same_as(again.todo_progress.as_ref().unwrap(), session.todo_progress.as_ref())
            && again.todo_progress.as_ref().unwrap().run_id.as_deref() == Some("graph-run-1")
            && json.contains("\"runId\":\"graph-run-1\"")
            && work::same_as(again.background_work.as_ref().unwrap(), session.background_work.as_ref()),
        "plan state round-trips"
    );
    let damaged = read(&OLD.replace("\"logs\":[]", "\"logs\":[],\"planHistory\":7,\"backgroundWork\":\"x\",\"todoProgress\":[1]"));
    assert!(
        damaged.id.as_deref() == Some("pane")
            && damaged.plan_history.is_none()
            && damaged.background_work.is_none()
            && damaged.todo_progress.is_none(),
        "damage in plan state keeps the conversation"
    );
    let partly = read(&OLD.replace(
        "\"logs\":[]",
        r##""logs":[],"planHistory":[{"id":1},{"id":"ok","runId":"r","plan":"# P","receivedAt":"2026-10-06T01:00:00.000Z","decidedAt":"2026-10-06T01:01:00.000Z","outcome":"revised"},{"id":"bad","outcome":"unknown"}]"##,
    ));
    let kept: Vec<String> =
        plan_mode::normalized_history(partly.plan_history.as_ref()).unwrap().into_iter().map(|r| r.id.unwrap()).collect();
    assert_eq!(kept, ["ok"], "only the damaged plan records are dropped");
    let folder = common::temp("plan-state");
    let workspace = Workspace {
        id: Some("ws".into()),
        name: Some("Repo".into()),
        path: Some(folder.to_string_lossy().into_owned()),
        ..Workspace::default()
    };
    let running = RunSession {
        status: Some("running".into()),
        background_work: Some(BackgroundWork {
            tasks: Some(vec![plan_mode::background_task("t", "agent", "d", "2026-10-06T01:00:00.000Z")]),
            turn_ended: false,
        }),
        ..session.clone()
    };
    let normalize = |s: RunSession, restoring: bool| {
        let snapshot =
            AppSnapshot { workspaces: Some(vec![workspace.clone()]), sessions: Some(vec![Arc::new(s)]), ..AppSnapshot::default() };
        mighty_core::normalize::normalize(&snapshot, restoring).unwrap().sessions.unwrap()[0].as_ref().clone()
    };
    let restored = normalize(running.clone(), true);
    assert!(
        work::tasks(restored.background_work.as_ref().unwrap())[0].status.as_deref() == Some("unknown")
            && restored.plan_history.as_ref().unwrap().len() == 1
            && todos::completed(restored.todo_progress.as_ref().unwrap()) == 1,
        "restoring ends what was still running as unknown"
    );
    let saved = normalize(running, false);
    assert_eq!(
        work::tasks(saved.background_work.as_ref().unwrap())[0].status.as_deref(),
        Some("running"),
        "saving a running pane keeps its live tasks"
    );
    let shell = normalize(RunSession { id: Some("sh".into()), kind: Some("shell".into()), ..session }, true);
    assert!(
        shell.plan_history.is_none() && shell.todo_progress.is_none() && shell.background_work.is_none(),
        "a shell pane keeps no plan state"
    );
    let _ = std::fs::remove_dir_all(folder);
}
