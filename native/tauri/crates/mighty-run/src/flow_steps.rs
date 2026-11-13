//! C# `FlowSteps` (FlowSteps.cs), the part a run records: a request's checklist kept on its graph run as steps with the times the app
//! saw each start and end, the instruction and tools that make Claude keep one. The progress panel and the step cards are the graph
//! views' (mighty-graph).

use std::collections::{HashMap, HashSet};

use mighty_core::graph::{FlowStep, MightyGraphRun};
use mighty_core::model::{RunSession, TodoItem, TodoProgress};
use mighty_core::time::{self, DateTimeOffset};

use crate::model::RunEvent;
use crate::plan_mode;

/// Claude Code's task tools: run as the app runs it, it loads them only when `--allowedTools` names them (2.1.322).
pub const TASK_TOOLS: [&str; 4] = ["TaskCreate", "TaskUpdate", "TaskList", "TaskGet"];

/// What a Claude run is told on top of its system prompt (`--append-system-prompt`), the Mac's same words.
pub const PLAN_INSTRUCTION: &str = "Mighty Code draws your task list as this request's plan. Before any other tool call, create the task list of the steps you will take with TaskCreate (load it with ToolSearch first if it is not loaded yet; use TodoWrite only where the task tools are unavailable): one task per step, in the user's language, even when there are only one or two steps. Then mark each task in_progress with TaskUpdate when you start it and completed as soon as it is done, and add or remove tasks when the plan changes. Only a reply that needs no tool call at all needs no task list.";

/// A step block's node suffix.
pub const SUFFIX: &str = "step:";

/// `FlowSteps.Key`.
pub fn key(item: &TodoItem) -> String {
    match &item.id {
        Some(id) => format!("id:{id}"),
        None => format!("text:{}", item.content.as_deref().unwrap_or("")),
    }
}

/// `FlowSteps.StableHash`: FNV-1a, 64 bits, lower-case hex without leading zeros.
pub fn stable_hash(text: &str) -> String {
    let mut hash: u64 = 0xcbf29ce484222325;
    for byte in text.as_bytes() {
        hash ^= *byte as u64;
        hash = hash.wrapping_mul(0x00000100000001b3);
    }
    format!("{hash:x}")
}

/// `FlowSteps.NodeSuffix`.
pub fn node_suffix(step: &FlowStep) -> String {
    format!("{SUFFIX}{}", stable_hash(step.key.as_deref().unwrap_or("")))
}

/// `FlowSteps.Stamp`: UTC with milliseconds.
pub fn stamp(date: DateTimeOffset) -> String {
    plan_mode::timestamp(date)
}

/// `FlowSteps.Steps`: the steps a run draws (a notice run's are its request's).
pub fn steps(run: &MightyGraphRun) -> &[FlowStep] {
    if run.is_notice() { &[] } else { run.steps.as_deref().unwrap_or(&[]) }
}

/// `FlowSteps.Record`: a checklist change applied to the request's run that sent it.
pub fn record(progress: &TodoProgress, runs: &mut [MightyGraphRun], previous: Option<&TodoProgress>, now: DateTimeOffset) {
    let Some(run_id) = progress.run_id.as_deref() else { return };
    let Some(index) =
        runs.iter().rposition(|r| !r.is_notice() && (r.source_run_id.as_deref() == Some(run_id) || r.id.as_deref() == Some(run_id)))
    else {
        return;
    };
    let mut run = runs[index].clone();
    if run.steps.is_none() {
        let carried: Vec<String> = match previous {
            Some(before) if before.run_id.as_deref() != Some(run_id) => {
                plan_mode::todos::items(before).iter().filter(|i| i.status.as_deref() == Some("completed")).map(key).collect()
            }
            _ => vec![],
        };
        run.steps_carried = (!carried.is_empty()).then_some(carried);
    }
    let skip: HashSet<String> = run.steps_carried.iter().flatten().cloned().collect();
    let mut old: HashMap<String, FlowStep> = HashMap::new();
    for step in run.steps.iter().flatten() {
        old.entry(step.key.clone().unwrap_or_default()).or_insert_with(|| step.clone());
    }
    let now_text = stamp(now);
    // The latest a step ended before this change: where a step marked done without going in progress began.
    let mut ends: Vec<&String> = run.steps.iter().flatten().filter_map(|s| s.ended_at.as_ref()).collect();
    ends.sort_by_key(|e| time::parse(Some(e)).map(|t| t.utc_ticks()).unwrap_or(i64::MIN));
    let mut last_end: Option<String> = ends.last().map(|e| e.to_string());
    let first_root = run.root_entries().first().and_then(|e| e.timestamp.clone());
    let mut next = Vec::new();
    for item in plan_mode::todos::items(progress) {
        let key = key(item);
        if skip.contains(&key) {
            continue;
        }
        let mut step = old.get(&key).cloned().unwrap_or_else(|| FlowStep {
            key: Some(key.clone()),
            content: item.content.clone(),
            active_form: None,
            status: Some("pending".into()),
            started_at: None,
            ended_at: None,
        });
        step.content = item.content.clone();
        step.active_form = item.active_form.clone();
        match item.status.as_deref() {
            Some("in_progress") => {
                if step.started_at.is_none() || step.status.as_deref() == Some("pending") {
                    step.started_at = Some(now_text.clone());
                }
                step.ended_at = None;
            }
            Some("completed") => {
                if step.started_at.is_none() {
                    step.started_at = Some(last_end.clone().or_else(|| first_root.clone()).unwrap_or_else(|| now_text.clone()));
                }
                if step.ended_at.is_none() {
                    step.ended_at = Some(now_text.clone());
                    last_end = Some(now_text.clone());
                }
            }
            _ => {
                step.started_at = None;
                step.ended_at = None;
            }
        }
        step.status = item.status.clone();
        next.push(step);
    }
    run.steps = Some(next);
    runs[index] = run;
}

/// `FlowSteps.RecordSteps`: a checklist change of the main session's, kept on the request it belongs to. Read before the pane's
/// list is replaced with it.
pub fn record_steps(session: RunSession, event: &RunEvent, now: DateTimeOffset) -> RunSession {
    if event.kind != "todos" || session.kind() != "claude" {
        return session;
    }
    let Some(progress) = plan_mode::normalized_todos(event.todos.as_ref()) else { return session };
    let Some(live) = session.graph_runs.as_ref().filter(|r| !r.is_empty()) else { return session };
    let mut runs = live.clone();
    record(&progress, &mut runs, session.todo_progress.as_ref(), now);
    RunSession { graph_runs: Some(runs), ..session }
}

/// `FlowSteps.StepIndex`: the step whose span holds `time` (the one that started last when spans meet); None between steps. A
/// step's start counts from its whole second, as the records' times do.
pub fn step_index(at: DateTimeOffset, steps: &[FlowStep]) -> Option<usize> {
    let millis = |t: &DateTimeOffset| (t.utc_ticks() - 621_355_968_000_000_000).div_euclid(10_000);
    let mut best: Option<(usize, DateTimeOffset)> = None;
    for (index, step) in steps.iter().enumerate() {
        let Some(start) = time::parse(step.started_at.as_deref()) else { continue };
        if (millis(&start) as f64 / 1000.0).floor() > millis(&at) as f64 / 1000.0 {
            continue;
        }
        if let Some(end) = time::parse(step.ended_at.as_deref())
            && end < at
        {
            continue;
        }
        if best.is_none_or(|(_, b)| start >= b) {
            best = Some((index, start));
        }
    }
    best.map(|(index, _)| index)
}
