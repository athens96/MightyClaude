//! C# `ClaudePlanMode.cs`: Claude Code's plan mode read from the stream the app already runs — the ExitPlanMode answer
//! (`PlanDecision`, `PlanOutcome`, `ClaudePlanMode.Response`), the pane's plan history, the execution checklist
//! (`TodoProgressTracker`) and the background tasks (`BackgroundTaskTracker`), and `Record`, which applies them to a pane.
//! native/contracts/fixtures/claude-plan-mode.json is the shared oracle.

use std::collections::HashMap;
use std::sync::OnceLock;

use mighty_core::model::{BackgroundTask, BackgroundWork, PlanRecord, RunSession, RunSettings, TodoItem, TodoProgress};
use mighty_core::time::DateTimeOffset;
use mighty_core::{normalize, wire};
use regex::Regex;
use serde_json::{Value, json};

use crate::activity::{clean, prefix_utf8};
use crate::meta;
use crate::model::RunEvent;
use crate::strings;
use crate::{Result, RunError};

pub const TOOL_NAME: &str = "ExitPlanMode";
pub const MAXIMUM_FEEDBACK_BYTES: usize = 16_384;
pub const MAXIMUM_HISTORY: usize = 10;
pub const MAXIMUM_STORED_PLAN_BYTES: usize = 32_768;
pub const MAXIMUM_STORED_FEEDBACK_BYTES: usize = 4_096;
pub const MAXIMUM_HISTORY_BYTES: usize = 98_304;
const SUMMARY_LINES: usize = 8;
const SUMMARY_BYTES: usize = 1_024;
const SUMMARY_FEEDBACK_BYTES: usize = 512;

pub const REVISE_MESSAGE: &str = "The user reviewed your plan and wants changes before any code is written. Stay in plan mode, revise the plan, and call ExitPlanMode again. The user said:\n";
pub const CANCEL_MESSAGE: &str =
    "The user cancelled this plan in Mighty Code. Do not make any changes; stop and wait for the user's next request.";
pub const TOO_LONG_MESSAGE: &str =
    "The plan is longer than Mighty Code can show in full, so it cannot be approved. Shorten the plan and call ExitPlanMode again.";

/// How the user answered a plan (C# `PlanDecision`).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PlanDecision {
    pub kind: String,
    pub feedback: Option<String>,
}

impl PlanDecision {
    /// Allow, and the session continues in acceptEdits.
    pub fn approve_auto_edit() -> PlanDecision {
        PlanDecision { kind: "approveAutoEdit".into(), feedback: None }
    }
    /// Allow, and the session continues in the CLI's default (the app's manual).
    pub fn approve_confirm_each() -> PlanDecision {
        PlanDecision { kind: "approveConfirmEach".into(), feedback: None }
    }
    /// Deny and interrupt the turn.
    pub fn cancel() -> PlanDecision {
        PlanDecision { kind: "cancel".into(), feedback: None }
    }
    /// Deny with the user's feedback; Claude stays in plan mode and plans again.
    pub fn revise(feedback: impl Into<String>) -> PlanDecision {
        PlanDecision { kind: "revise".into(), feedback: Some(feedback.into()) }
    }
    /// `PlanDecision.Outcome`.
    pub fn outcome(&self) -> &'static str {
        match self.kind.as_str() {
            "approveAutoEdit" => outcome::APPROVED_AUTO,
            "approveConfirmEach" => outcome::APPROVED_CONFIRM,
            "revise" => outcome::REVISED,
            _ => outcome::CANCELLED,
        }
    }
}

/// C# `PlanOutcome`.
pub mod outcome {
    pub const APPROVED_AUTO: &str = "approvedAuto";
    pub const APPROVED_CONFIRM: &str = "approvedConfirm";
    pub const REVISED: &str = "revised";
    pub const CANCELLED: &str = "cancelled";
    pub const ALL: [&str; 4] = [APPROVED_AUTO, APPROVED_CONFIRM, REVISED, CANCELLED];

    /// The pane's own permission mode after this answer; None leaves it alone.
    pub fn pane_mode(outcome: &str) -> Option<&'static str> {
        match outcome {
            APPROVED_AUTO => Some("acceptEdits"),
            APPROVED_CONFIRM => Some("manual"),
            _ => None,
        }
    }

    /// The mode Claude Code itself switches to (setMode) for this answer alone.
    pub fn cli_mode(outcome: &str) -> Option<&'static str> {
        match outcome {
            APPROVED_AUTO => Some("acceptEdits"),
            APPROVED_CONFIRM => Some("default"),
            _ => None,
        }
    }

    /// The CLI mode a pane's stored permission mode launches with; None for plan (or anything else).
    pub fn pane_cli_mode(pane_mode: Option<&str>) -> Option<&'static str> {
        match pane_mode {
            Some("manual") => Some("default"),
            Some("acceptEdits") => Some("acceptEdits"),
            Some("auto") => Some("auto"),
            Some("fullAccess") => Some("bypassPermissions"),
            _ => None,
        }
    }

    fn rank(mode: &str) -> i32 {
        match mode {
            "acceptEdits" => 1,
            "auto" => 2,
            "bypassPermissions" => 3,
            _ => 0,
        }
    }

    /// `PlanOutcome.ApprovedCliMode`: the button's choice, never below the mode the pane itself runs in.
    pub fn approved_cli_mode(decision: &super::PlanDecision, pane_mode: Option<&str>) -> Option<&'static str> {
        let chosen = cli_mode(decision.outcome())?;
        match pane_cli_mode(pane_mode) {
            Some(own) if rank(own) > rank(chosen) => Some(own),
            _ => Some(chosen),
        }
    }
}

/// A plan waiting for the user's answer (C# `PlanApprovalRequest`).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PlanApprovalRequest {
    pub session_id: String,
    pub run_id: String,
    pub request_id: String,
    pub tool_use_id: String,
    pub plan: String,
    pub received_at: String,
}

impl PlanApprovalRequest {
    /// The pending ExitPlanMode request of one pane, or None for any other request.
    pub fn from(session_id: &str, permission: &crate::permissions::ToolPermissionRequest) -> Option<PlanApprovalRequest> {
        if !permission.can_answer_plan || permission.state != "pending" {
            return None;
        }
        let plan = permission.plan()?;
        Some(PlanApprovalRequest {
            session_id: session_id.to_string(),
            run_id: permission.run_id.clone(),
            request_id: permission.id.clone(),
            tool_use_id: permission.tool_use_id.clone(),
            plan,
            received_at: permission.received_at.clone().unwrap_or_else(|| timestamp(DateTimeOffset::now())),
        })
    }
}

/// `ClaudePlanMode.Timestamp`: ISO 8601 with milliseconds in UTC, as macOS writes them.
pub fn timestamp(value: DateTimeOffset) -> String {
    value.to_chrono().format("%Y-%m-%dT%H:%M:%S%.3fZ").to_string()
}

/// `ClaudePlanMode.Plan(inputJson)`: the Markdown plan of an ExitPlanMode input.
pub fn plan_text(input_json: &str) -> Option<String> {
    if !input_json.trim_start().starts_with('{') {
        return None;
    }
    let value: Value = serde_json::from_str(input_json).ok()?;
    plan(&value)
}

/// `ClaudePlanMode.Plan(JsonElement)`.
pub fn plan(input: &Value) -> Option<String> {
    meta::text(input, "plan").filter(|text| !text.trim().is_empty()).map(str::to_string)
}

/// `ClaudePlanMode.Response`: the `response` body of the control_response answering an ExitPlanMode request.
pub fn response(decision: &PlanDecision, tool_use_id: &str, pane_mode: Option<&str>) -> Result<Value> {
    Ok(match decision.kind.as_str() {
        "approveAutoEdit" | "approveConfirmEach" => json!({
            "behavior": "allow",
            "updatedInput": {},
            "updatedPermissions": [{"type": "setMode", "mode": outcome::approved_cli_mode(decision, pane_mode), "destination": "session"}],
            "toolUseID": tool_use_id,
        }),
        "revise" => json!({
            "behavior": "deny",
            "message": format!("{REVISE_MESSAGE}{}", validated_feedback(decision.feedback.as_deref())?),
            "toolUseID": tool_use_id,
        }),
        _ => json!({"behavior": "deny", "message": CANCEL_MESSAGE, "interrupt": true, "toolUseID": tool_use_id}),
    })
}

/// `ClaudePlanMode.ValidatedFeedback`: trimmed, not empty and bounded.
pub fn validated_feedback(feedback: Option<&str>) -> Result<String> {
    let text = feedback.unwrap_or("").trim();
    if text.is_empty() {
        return Err(RunError::Invalid(strings::get("plan.error.emptyFeedback")));
    }
    if text.len() > MAXIMUM_FEEDBACK_BYTES {
        return Err(RunError::Invalid(strings::get("plan.error.feedbackTooLong")));
    }
    Ok(text.to_string())
}

/// `new PlanRecord(id, runId, plan, receivedAt, decidedAt, outcome, feedback)`.
pub fn plan_record(
    id: &str,
    run_id: &str,
    plan: &str,
    received_at: &str,
    decided_at: &str,
    outcome: &str,
    feedback: Option<&str>,
) -> PlanRecord {
    PlanRecord {
        id: Some(id.into()),
        run_id: Some(run_id.into()),
        plan: Some(plan.into()),
        received_at: Some(received_at.into()),
        decided_at: Some(decided_at.into()),
        outcome: Some(outcome.into()),
        feedback: feedback.map(str::to_string),
        plan_truncated: None,
        graph_run_id: None,
        launch_override: None,
    }
}

/// `ClaudePlanMode.Record(requestId, runId, plan, receivedAt, decidedAt, decision)`.
pub fn record(request_id: &str, run_id: &str, plan: &str, received_at: &str, decided_at: &str, decision: &PlanDecision) -> PlanRecord {
    let feedback = (decision.kind == "revise").then(|| decision.feedback.as_deref().unwrap_or("").trim().to_string());
    bounded(plan_record(request_id, run_id, plan, received_at, decided_at, decision.outcome(), feedback.as_deref()))
}

fn utf16(text: &str) -> usize {
    text.encode_utf16().count()
}

/// `ClaudePlanMode.Bounded`.
pub fn bounded(record: PlanRecord) -> PlanRecord {
    let original = record.plan.clone().unwrap_or_default();
    let plan = prefix_utf8(&original, MAXIMUM_STORED_PLAN_BYTES);
    let feedback = record.feedback.as_deref().map(|f| prefix_utf8(f, MAXIMUM_STORED_FEEDBACK_BYTES)).filter(|f| !f.is_empty());
    PlanRecord {
        plan_truncated: if utf16(&plan) < utf16(&original) { Some(true) } else { record.plan_truncated },
        plan: Some(plan),
        feedback,
        ..record
    }
}

fn cost(record: &PlanRecord) -> usize {
    record.plan.as_deref().map_or(0, str::len) + record.feedback.as_deref().map_or(0, str::len)
}

/// `ClaudePlanMode.Summarized`: the first lines of a plan, bounded.
pub fn summarized(plan: &str) -> String {
    prefix_utf8(&plan.split('\n').take(SUMMARY_LINES).collect::<Vec<_>>().join("\n"), SUMMARY_BYTES)
}

/// `ClaudePlanMode.Budgeted`: newest first, whole records while they fit; older ones keep a summary.
pub fn budgeted(mut list: Vec<PlanRecord>) -> Vec<PlanRecord> {
    let mut used = 0;
    for i in (0..list.len()).rev() {
        let mut record = list[i].clone();
        if used + cost(&record) > MAXIMUM_HISTORY_BYTES {
            let plan = record.plan.clone().unwrap_or_default();
            let summary = summarized(&plan);
            if summary.len() < plan.len() {
                record.plan = Some(summary);
                record.plan_truncated = Some(true);
            }
            if let Some(feedback) = &record.feedback {
                record.feedback = Some(prefix_utf8(feedback, SUMMARY_FEEDBACK_BYTES));
            }
            list[i] = record.clone();
        }
        used += cost(&record);
    }
    list
}

/// `ClaudePlanMode.Appending`: the pane's history with `record` added (a repeat of the same request replaces it), newest last.
pub fn appending(record: &PlanRecord, history: Option<&Vec<PlanRecord>>) -> Vec<PlanRecord> {
    let mut list: Vec<PlanRecord> =
        history.into_iter().flatten().filter(|r| r.id != record.id || r.run_id != record.run_id).cloned().collect();
    list.push(bounded(record.clone()));
    let skip = list.len().saturating_sub(MAXIMUM_HISTORY);
    budgeted(list.into_iter().skip(skip).collect())
}

/// `ClaudePlanMode.NormalizedHistory`.
pub fn normalized_history(history: Option<&Vec<PlanRecord>>) -> Option<Vec<PlanRecord>> {
    normalize::plan_history(history)
}

/// `TodoProgress.Normalized`.
pub fn normalized_todos(value: Option<&TodoProgress>) -> Option<TodoProgress> {
    normalize::todo_progress(value)
}

/// `BackgroundWork.Normalized`.
pub fn normalized_background(value: Option<&BackgroundWork>, restoring: bool) -> Option<BackgroundWork> {
    normalize::background_work(value, restoring)
}

/// `ClaudePlanMode.Record(RunSession, RunEvent)`: plan answers, execution progress and background work applied to the pane. An
/// approved plan also sets the pane's own permission mode — only a pane the user put in plan mode, and never for a run a plan-mode
/// style started in plan.
pub fn apply(session: RunSession, event: &RunEvent) -> RunSession {
    if session.kind() != "claude" || session.provider() != "claude" && !(session.provider() == "codex" && event.kind == "todos") {
        return session;
    }
    match event.kind.as_str() {
        "plan" => {
            let Some(record) = &event.plan else { return session };
            let history = appending(record, session.plan_history.as_ref());
            let settings = session.settings.clone().unwrap_or_default();
            let settings = match outcome::pane_mode(record.outcome.as_deref().unwrap_or("")) {
                Some(mode) if settings.permission_mode == "plan" && record.launch_override.is_none() => {
                    RunSettings { permission_mode: mode.into(), ..settings }
                }
                _ => settings,
            };
            RunSession { plan_history: Some(history), settings: Some(settings), ..session }
        }
        "todos" => RunSession { todo_progress: normalized_todos(event.todos.as_ref()), ..session },
        "background" => RunSession { background_work: normalized_background(event.background.as_ref(), false), ..session },
        "status" if event.status.as_deref() == Some("running") => RunSession { background_work: None, ..session },
        _ => session,
    }
}

// ── the checklist ───────────────────────────────────────────────────────────────────────────────────────────────────────

pub const MAXIMUM_TODO_ITEMS: usize = 100;

/// `new TodoItem(content, status, activeForm, id)`.
pub fn todo_item(content: &str, status: &str, active_form: Option<&str>, id: Option<&str>) -> TodoItem {
    TodoItem {
        content: Some(content.into()),
        status: Some(status.into()),
        active_form: active_form.map(str::to_string),
        id: id.map(str::to_string),
    }
}

/// `TodoProgress.Total`, `Completed`, `Current`, `CurrentText`, `SameAs`.
pub mod todos {
    use mighty_core::model::{TodoItem, TodoProgress};

    pub fn items(progress: &TodoProgress) -> &[TodoItem] {
        progress.items.as_deref().unwrap_or(&[])
    }
    pub fn total(progress: &TodoProgress) -> usize {
        items(progress).len()
    }
    pub fn completed(progress: &TodoProgress) -> usize {
        items(progress).iter().filter(|i| i.status.as_deref() == Some("completed")).count()
    }
    pub fn current(progress: &TodoProgress) -> Option<&TodoItem> {
        items(progress).iter().find(|i| i.status.as_deref() == Some("in_progress"))
    }
    pub fn current_text(progress: &TodoProgress) -> Option<String> {
        current(progress).map(|item| item.active_form.clone().or_else(|| item.content.clone()).unwrap_or_default())
    }
    pub fn same_as(a: &TodoProgress, b: Option<&TodoProgress>) -> bool {
        b.is_some_and(|b| items(a) == items(b))
    }
}

/// C# `TodoProgressTracker`: the main agent's checklist as Claude Code's own reader takes it — the task tools (TaskCreate, confirmed by
/// its "Task #N created" result, and TaskUpdate) when there are any, else the last TodoWrite list; Codex's todo_list item; an ACP
/// agent's plan.
#[derive(Debug, Clone, Default)]
pub struct TodoProgressTracker {
    tasks: Vec<TodoItem>,
    creates: Vec<(String, TodoItem)>,
    todo_write: Option<Vec<TodoItem>>,
    emitted: bool,
    pub progress: Option<TodoProgress>,
}

fn field(input: &Value, keys: &[&str]) -> Option<String> {
    keys.iter().find_map(|key| scalar_text(meta::prop(input, key)).filter(|v| !v.is_empty()))
}

/// `Text(JsonElement)`: a string, or a number's literal.
fn scalar_text(value: Option<&Value>) -> Option<String> {
    match value? {
        Value::String(s) => Some(s.clone()),
        Value::Number(n) => Some(n.to_string()),
        _ => None,
    }
}

/// `TodoProgressTracker.CreatedId`: "Task #3 created successfully: …".
pub fn created_id(output: Option<&str>) -> Option<String> {
    static PATTERN: OnceLock<Regex> = OnceLock::new();
    let pattern = PATTERN.get_or_init(|| Regex::new(r"^Task #(\S+) created successfully").expect("pattern"));
    pattern.captures(output?).map(|c| c[1].to_string())
}

impl TodoProgressTracker {
    /// `progress`: the pane's saved checklist, for a run that resumes it.
    pub fn new(progress: Option<&TodoProgress>) -> TodoProgressTracker {
        let mut tracker = TodoProgressTracker::default();
        let Some(progress) = progress else { return tracker };
        let items = todos::items(progress);
        if items.is_empty() {
            return tracker;
        }
        if items.iter().any(|i| i.id.is_some()) {
            tracker.tasks.extend(items.iter().filter(|i| i.id.is_some()).cloned());
        } else {
            tracker.todo_write = Some(items.to_vec());
        }
        tracker.progress = Some(progress.clone());
        tracker
    }

    /// One frame; the new progress when it changed. Sub-agent frames never touch the main checklist.
    pub fn consume(&mut self, frame: &Value) -> Option<TodoProgress> {
        if !meta::top_level(frame) {
            return None;
        }
        let mut changed = false;
        let blocks = meta::prop(frame, "message").and_then(|m| meta::prop(m, "content"));
        let kind = meta::text(frame, "type");
        if kind == Some("acp.update") {
            let update = meta::prop(frame, "update");
            let planned = update.and_then(|u| meta::prop(u, "entries"));
            let (Some(Value::Array(entries)), Some("plan")) = (planned, meta::text_of(update, "sessionUpdate")) else { return None };
            let list: Vec<TodoItem> = entries
                .iter()
                .take(MAXIMUM_TODO_ITEMS)
                .filter(|e| e.is_object() && !clean(meta::text(e, "content"), 1024, true).is_empty())
                .map(|e| {
                    let status =
                        meta::text(e, "status").filter(|s| matches!(*s, "pending" | "in_progress" | "completed")).unwrap_or("pending");
                    todo_item(meta::text(e, "content").unwrap_or(""), status, None, None)
                })
                .collect();
            if list.is_empty() {
                self.todo_write = Some(list);
                return None;
            }
            self.todo_write = Some(list);
            changed = true;
        } else if matches!(kind, Some("item.started" | "item.updated" | "item.completed")) {
            let item = meta::prop(frame, "item");
            let (Some("todo_list"), Some(Value::Array(entries))) = (meta::text_of(item, "type"), item.and_then(|i| meta::prop(i, "items")))
            else {
                return None;
            };
            let mut current = false;
            self.todo_write = Some(
                entries
                    .iter()
                    .filter(|t| !clean(meta::text(t, "text"), 1024, true).is_empty())
                    .map(|t| {
                        let text = meta::text(t, "text").unwrap_or("");
                        if meta::flag(t, "completed") {
                            return todo_item(text, "completed", None, None);
                        }
                        let status = if current { "pending" } else { "in_progress" };
                        current = true;
                        todo_item(text, status, None, None)
                    })
                    .collect(),
            );
            changed = true;
        } else {
            let Some(Value::Array(blocks)) = blocks else { return None };
            if kind == Some("assistant") {
                for block in blocks.iter().filter(|b| meta::text(b, "type") == Some("tool_use")) {
                    let input = meta::prop(block, "input");
                    let (Some(id), Some(input)) = (meta::text(block, "id").filter(|i| !i.is_empty()), input.filter(|i| i.is_object()))
                    else {
                        continue;
                    };
                    match meta::text(block, "name") {
                        Some("TodoWrite") => {
                            let Some(Value::Array(list)) = meta::prop(input, "todos") else { continue };
                            self.todo_write = Some(
                                list.iter()
                                    .filter_map(|t| {
                                        let content = meta::text(t, "content")?;
                                        Some(todo_item(
                                            content,
                                            meta::text(t, "status").unwrap_or("pending"),
                                            meta::text(t, "activeForm"),
                                            None,
                                        ))
                                    })
                                    .collect(),
                            );
                            changed = true;
                        }
                        Some("TaskCreate") => {
                            let Some(subject) = field(input, &["subject", "title", "name"]) else { continue };
                            if self.creates.len() >= 256 {
                                continue;
                            }
                            let active = field(input, &["activeForm", "active_form"]).unwrap_or_else(|| subject.clone());
                            self.creates.push((id.to_string(), todo_item(&subject, "pending", Some(&active), None)));
                            changed = true;
                        }
                        Some("TaskUpdate") => {
                            let Some(task_id) = field(input, &["taskId", "id", "task_id"]) else { continue };
                            let status = meta::text(input, "status");
                            let subject = meta::text(input, "subject");
                            let active = field(input, &["activeForm", "active_form"]);
                            let index = self.tasks.iter().position(|t| t.id.as_deref() == Some(task_id.as_str()));
                            if status == Some("deleted") {
                                self.tasks.retain(|t| t.id.as_deref() != Some(task_id.as_str()));
                            } else if let Some(index) = index {
                                let task = &mut self.tasks[index];
                                if let Some(subject) = subject {
                                    task.content = Some(subject.to_string());
                                }
                                if let Some(active) = active {
                                    task.active_form = Some(active);
                                }
                                if let Some(status) = status {
                                    task.status = Some(status.to_string());
                                }
                            } else if self.tasks.len() < 256 {
                                let content = subject.map(str::to_string).unwrap_or_else(|| task_id.clone());
                                let active = active.or_else(|| subject.map(str::to_string)).unwrap_or_else(|| task_id.clone());
                                self.tasks.push(todo_item(&content, status.unwrap_or("pending"), Some(&active), Some(&task_id)));
                            }
                            changed = true;
                        }
                        _ => {}
                    }
                }
            } else if kind == Some("user") {
                for block in blocks.iter().filter(|b| meta::text(b, "type") == Some("tool_result")) {
                    let Some(id) = meta::text(block, "tool_use_id") else { continue };
                    let Some(index) = self.creates.iter().position(|(tool, _)| tool == id) else { continue };
                    if meta::flag(block, "is_error") {
                        self.creates.remove(index);
                        changed = true;
                        continue;
                    }
                    let structured = meta::prop(frame, "tool_use_result").and_then(|r| meta::prop(r, "task"));
                    let task_id = scalar_text(structured.and_then(|s| meta::prop(s, "id")))
                        .or_else(|| created_id(crate::activity::output(meta::prop(block, "content")).as_deref()));
                    let Some(task_id) = task_id else { continue };
                    let (_, item) = self.creates.remove(index);
                    let item = TodoItem { id: Some(task_id.clone()), ..item };
                    if !self.tasks.iter().any(|t| t.id.as_deref() == Some(task_id.as_str())) && self.tasks.len() < 256 {
                        self.tasks.push(item);
                    }
                    changed = true;
                }
            }
        }
        if !changed {
            return None;
        }
        let items: Vec<TodoItem> = self.tasks.iter().cloned().chain(self.creates.iter().map(|(_, item)| item.clone())).collect();
        let list = if items.is_empty() { self.todo_write.clone().unwrap_or_default() } else { items };
        let next = normalized_todos(Some(&TodoProgress { items: Some(list), run_id: None }))?;
        if self.emitted && todos::same_as(&next, self.progress.as_ref()) {
            return None;
        }
        self.emitted = true;
        self.progress = Some(next.clone());
        Some(next)
    }
}

// ── background work ─────────────────────────────────────────────────────────────────────────────────────────────────────

pub const MAXIMUM_BACKGROUND_TASKS: usize = 32;

/// `BackgroundTaskTracker.ValidId`.
pub fn valid_task_id(value: Option<&str>) -> bool {
    value.is_some_and(|v| !v.is_empty() && v.len() <= 128 && !v.chars().any(|c| c.is_control() || c == ' '))
}

/// `BackgroundTaskTracker.KindOf`.
pub fn kind_of(kind: Option<&str>) -> &'static str {
    match kind {
        Some("local_agent" | "remote_agent" | "local_workflow" | "in_process_teammate") => "agent",
        Some("local_bash") => "shell",
        _ => "other",
    }
}

/// `BackgroundTaskTracker.StatusOf`.
pub fn status_of(value: Option<&str>) -> Option<&'static str> {
    match value {
        Some("running" | "pending") => Some("running"),
        Some("completed") => Some("completed"),
        Some("failed" | "error") => Some("failed"),
        Some("killed" | "stopped" | "cancelled") => Some("stopped"),
        _ => None,
    }
}

/// `BackgroundWork.Running`, `WaitingOnBackground`, `SameAs`.
pub mod work {
    use mighty_core::model::{BackgroundTask, BackgroundWork};

    pub fn tasks(work: &BackgroundWork) -> &[BackgroundTask] {
        work.tasks.as_deref().unwrap_or(&[])
    }
    pub fn running(work: &BackgroundWork) -> Vec<&BackgroundTask> {
        tasks(work).iter().filter(|t| t.status.as_deref() == Some("running")).collect()
    }
    pub fn waiting_on_background(work: &BackgroundWork) -> bool {
        work.turn_ended && tasks(work).iter().any(|t| t.status.as_deref() == Some("running"))
    }
    pub fn same_as(a: &BackgroundWork, b: Option<&BackgroundWork>) -> bool {
        b.is_some_and(|b| a.turn_ended == b.turn_ended && tasks(a) == tasks(b))
    }
}

/// `new BackgroundTask(id, kind, description, startedAt, status, toolUseId, summary, endedAt)`.
pub fn background_task(id: &str, kind: &str, description: &str, started_at: &str) -> BackgroundTask {
    BackgroundTask {
        id: Some(id.into()),
        kind: Some(kind.into()),
        description: Some(description.into()),
        started_at: Some(started_at.into()),
        status: Some("running".into()),
        tool_use_id: None,
        summary: None,
        ended_at: None,
    }
}

pub type Clock = Box<dyn Fn() -> DateTimeOffset + Send>;

/// C# `BackgroundTaskTracker`: Claude Code's `system` task events. A task registered in the foreground stays out until it moves to the
/// background; a task that ended and starts again under a run of its own runs again.
pub struct BackgroundTaskTracker {
    clock: Clock,
    tasks: Vec<BackgroundTask>,
    hidden: HashMap<String, BackgroundTask>,
    runs: HashMap<String, String>,
    turn_ended: bool,
}

impl Default for BackgroundTaskTracker {
    fn default() -> Self {
        BackgroundTaskTracker::new(None)
    }
}

impl BackgroundTaskTracker {
    pub fn new(clock: Option<Clock>) -> BackgroundTaskTracker {
        BackgroundTaskTracker {
            clock: clock.unwrap_or_else(|| Box::new(DateTimeOffset::now)),
            tasks: vec![],
            hidden: HashMap::new(),
            runs: HashMap::new(),
            turn_ended: false,
        }
    }

    pub fn work(&self) -> BackgroundWork {
        BackgroundWork { tasks: Some(self.tasks.clone()), turn_ended: self.turn_ended }
    }

    fn now(&self) -> String {
        timestamp((self.clock)())
    }

    fn description(value: Option<&str>) -> String {
        clean(Some(value.unwrap_or("")), 1024, true)
    }

    /// `BackgroundTaskTracker.RunId`.
    pub fn run_id(frame: &Value) -> Option<String> {
        meta::text(frame, "run_id").filter(|r| valid_task_id(Some(r))).map(str::to_string)
    }

    fn index(&self, id: &str) -> Option<usize> {
        self.tasks.iter().position(|t| t.id.as_deref() == Some(id))
    }

    /// One frame; the new state when the visible tasks changed.
    pub fn consume(&mut self, frame: &Value) -> Option<BackgroundWork> {
        if meta::text(frame, "type") != Some("system") {
            return None;
        }
        let subtype = meta::text(frame, "subtype")?;
        let before = self.work();
        match subtype {
            "task_started" => self.task_started(frame),
            "task_updated" => self.task_updated(frame),
            "task_progress" => {
                if let (Some(id), Some(description)) = (meta::text(frame, "task_id"), meta::text(frame, "description"))
                    && self.current(id, frame)
                {
                    let description = Self::description(Some(description));
                    self.update(id, |t| t.description = Some(description));
                }
            }
            "task_notification" => {
                if let Some(id) = meta::text(frame, "task_id") {
                    self.hidden.remove(id);
                    if self.current(id, frame) {
                        let now = self.now();
                        let status = status_of(meta::text(frame, "status")).unwrap_or("completed");
                        let summary = clean(Some(meta::text(frame, "summary").unwrap_or("")), 4096, false);
                        self.update(id, |t| {
                            // Only the report that ends the task may set its summary.
                            if t.status.as_deref() != Some("running") {
                                return;
                            }
                            finish(t, status, &now);
                            if !summary.is_empty() {
                                t.summary = Some(summary);
                            }
                        });
                    }
                }
            }
            "background_tasks_changed" => {
                if let Some(Value::Array(live)) = meta::prop(frame, "tasks") {
                    for row in live {
                        let Some(listed) = meta::text(row, "task_id").filter(|l| valid_task_id(Some(l))) else { continue };
                        if meta::flag(row, "ambient") {
                            continue;
                        }
                        let listed_run = Self::run_id(row);
                        if let Some(at) = self.index(listed) {
                            if self.tasks[at].status.as_deref() != Some("running")
                                && let Some(listed_run) = &listed_run
                                && self.runs.get(listed).is_some_and(|ended| ended != listed_run)
                            {
                                self.restart(at, Some(listed_run.clone()), meta::text(row, "description"));
                            }
                            continue;
                        }
                        let task = match self.hidden.remove(listed) {
                            Some(known) => known,
                            None => background_task(
                                listed,
                                kind_of(meta::text(row, "task_type")),
                                &Self::description(meta::text(row, "description")),
                                &self.now(),
                            ),
                        };
                        self.insert(task, listed_run);
                    }
                }
            }
            _ => {}
        }
        let after = self.work();
        if work::same_as(&after, Some(&before)) { None } else { Some(after) }
    }

    fn task_started(&mut self, frame: &Value) {
        let Some(id) = meta::text(frame, "task_id").filter(|i| valid_task_id(Some(i))) else { return };
        if meta::flag(frame, "ambient") || self.hidden.contains_key(id) {
            return;
        }
        let run = Self::run_id(frame);
        let foreground = matches!(meta::prop(frame, "is_backgrounded"), Some(Value::Bool(false)));
        if let Some(mut existing) = self.index(id) {
            // A task that ended starts again in the background: a new run, unless the start names the very run that ended.
            if self.tasks[existing].status.as_deref() != Some("running")
                && !foreground
                && (run.is_none() || run.as_deref() != self.runs.get(id).map(String::as_str))
            {
                existing = self.restart(existing, run.clone(), meta::text(frame, "description"));
            }
            let known = &mut self.tasks[existing];
            if known.tool_use_id.is_none() && wire::is_identifier(meta::text(frame, "tool_use_id")) {
                known.tool_use_id = meta::text(frame, "tool_use_id").map(str::to_string);
            }
            if let Some(kind) = meta::text(frame, "task_type") {
                known.kind = Some(kind_of(Some(kind)).into());
            }
            if known.description.as_deref().unwrap_or("").is_empty()
                && let Some(description) = meta::text(frame, "description")
            {
                known.description = Some(Self::description(Some(description)));
            }
            if let Some(run) = run {
                self.runs.entry(id.to_string()).or_insert(run);
            }
            return;
        }
        let mut task =
            background_task(id, kind_of(meta::text(frame, "task_type")), &Self::description(meta::text(frame, "description")), &self.now());
        task.tool_use_id = meta::text(frame, "tool_use_id").filter(|t| wire::identifier(t)).map(str::to_string);
        if foreground {
            if self.hidden.len() < 256 {
                self.hidden.insert(id.to_string(), task);
            }
        } else {
            self.insert(task, run);
        }
    }

    fn task_updated(&mut self, frame: &Value) {
        let Some(updated) = meta::text(frame, "task_id") else { return };
        let Some(patch) = meta::prop(frame, "patch").filter(|p| p.is_object()) else { return };
        if meta::flag(patch, "is_backgrounded")
            && let Some(moved) = self.hidden.remove(updated)
        {
            self.insert(moved, Self::run_id(frame));
        }
        if let Some(mut waiting) = self.hidden.get(updated).cloned() {
            // Still in the foreground: keep its state for a later move.
            if let Some(description) = meta::text(patch, "description") {
                waiting.description = Some(Self::description(Some(description)));
            }
            match status_of(meta::text(patch, "status")) {
                Some(status) if status != "running" => {
                    self.hidden.remove(updated);
                }
                _ => {
                    self.hidden.insert(updated.to_string(), waiting);
                }
            }
            return;
        }
        if !self.current(updated, frame) {
            return;
        }
        let now = self.now();
        let description = meta::text(patch, "description").map(|d| Self::description(Some(d)));
        let status = status_of(meta::text(patch, "status"));
        let error = meta::text(patch, "error").map(|e| clean(Some(e), 4096, false));
        self.update(updated, |t| {
            if let Some(description) = description {
                t.description = Some(description);
            }
            if let Some(status) = status {
                finish(t, status, &now);
            }
            if t.status.as_deref() == Some("failed")
                && t.summary.is_none()
                && let Some(error) = error
            {
                t.summary = Some(error);
            }
        });
    }

    /// The request's own result arrived (not a task notification's).
    pub fn turn_ended(&mut self) -> Option<BackgroundWork> {
        if self.turn_ended {
            return None;
        }
        self.turn_ended = true;
        (!self.tasks.is_empty()).then(|| self.work())
    }

    /// A new turn started in the same process (a steer after the request's result).
    pub fn reopen(&mut self) -> Option<BackgroundWork> {
        if !self.turn_ended {
            return None;
        }
        self.turn_ended = false;
        (!self.tasks.is_empty()).then(|| self.work())
    }

    /// The process ended: a task without an end report is unknown.
    pub fn finish(&mut self) -> Option<BackgroundWork> {
        let now = self.now();
        let mut changed = false;
        for task in &mut self.tasks {
            if task.status.as_deref() == Some("running") {
                finish(task, "unknown", &now);
                changed = true;
            }
        }
        changed.then(|| self.work())
    }

    fn insert(&mut self, task: BackgroundTask, run: Option<String>) {
        let id = task.id.clone().unwrap_or_default();
        self.tasks.push(task);
        if let Some(run) = run {
            self.runs.insert(id, run);
        }
        while self.tasks.len() > MAXIMUM_BACKGROUND_TASKS {
            let at = self.tasks.iter().position(|t| t.status.as_deref() != Some("running")).unwrap_or(0);
            if let Some(id) = &self.tasks[at].id {
                self.runs.remove(id);
            }
            self.tasks.remove(at);
        }
    }

    fn update(&mut self, id: &str, change: impl FnOnce(&mut BackgroundTask)) {
        if let Some(index) = self.index(id) {
            change(&mut self.tasks[index]);
        }
    }

    /// Whether `frame` reports on task `id`'s latest run.
    fn current(&self, id: &str, frame: &Value) -> bool {
        match (Self::run_id(frame), self.runs.get(id)) {
            (Some(run), Some(latest)) => &run == latest,
            _ => true,
        }
    }

    /// The listed task at `index` runs again: a new run, started now, last in start order, with no end, summary or tool call yet.
    fn restart(&mut self, index: usize, run: Option<String>, description: Option<&str>) -> usize {
        let mut task = self.tasks.remove(index);
        let text = description.map(|d| Self::description(Some(d))).unwrap_or_default();
        task.status = Some("running".into());
        task.started_at = Some(self.now());
        task.ended_at = None;
        task.summary = None;
        task.tool_use_id = None;
        if !text.is_empty() {
            task.description = Some(text);
        }
        let id = task.id.clone().unwrap_or_default();
        match run {
            None => {
                self.runs.remove(&id);
            }
            Some(run) => {
                self.runs.insert(id, run);
            }
        }
        self.tasks.push(task);
        self.tasks.len() - 1
    }
}

/// `BackgroundTaskTracker.Finish(task, status)`: a settled task stays settled until it starts again.
fn finish(task: &mut BackgroundTask, status: &str, now: &str) {
    if task.status.as_deref() != Some("running") || status == "running" {
        return;
    }
    task.status = Some(status.into());
    task.ended_at = Some(now.into());
}
