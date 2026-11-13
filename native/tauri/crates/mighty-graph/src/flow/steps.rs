//! A request's steps in the role view of a Claude pane (C# `FlowSteps`, MightyCode.Core/FlowSteps.cs:30): the checklist Claude keeps
//! while it works, each item that began a card on the request's spine with what it launched beside it, the progress panel, and the
//! recording of a checklist change onto its request's run. The step record itself is `mighty_core::graph::FlowStep`.

use std::cmp::Reverse;
use std::collections::{HashMap, HashSet};

use mighty_core::graph::{FlowStep, MightyGraphAgent, MightyGraphRun};
use mighty_core::locale::Locale;
use mighty_core::model::{LogEntry, RunSession, TodoItem, TodoProgress};
use mighty_core::time::{self, DateTimeOffset};

use crate::camera::REQUEST_WIDTH;
use crate::flow::card::{self, Content};
use crate::flow::log;
use crate::flow::roles::{self, FlowRole};
use crate::notice;
use crate::support;

/// `TaskTools`: Claude Code's task tools, which `--allowedTools` must name (FlowSteps.cs:37).
pub const TASK_TOOLS: [&str; 4] = ["TaskCreate", "TaskUpdate", "TaskList", "TaskGet"];

/// `PlanInstruction` (FlowSteps.cs:39): what a Claude run is told on top of its system prompt.
pub const PLAN_INSTRUCTION: &str = "Mighty Code draws your task list as this request's plan. Before any other tool call, create the task list of the steps you will take with TaskCreate (load it with ToolSearch first if it is not loaded yet; use TodoWrite only where the task tools are unavailable): one task per step, in the user's language, even when there are only one or two steps. Then mark each task in_progress with TaskUpdate when you start it and completed as soon as it is done, and add or remove tasks when the plan changes. Only a reply that needs no tool call at all needs no task list.";

/// `Suffix`: a step block's node suffix, `step:` and a hash of its key.
pub const SUFFIX: &str = "step:";
/// `SideGap`: from a spine block's trailing edge to the column of what it launched.
pub const SIDE_GAP: f64 = 44.0;
/// `CardWidth`, `CardHeight`: the 실행 card and a step's card.
pub const CARD_WIDTH: f64 = REQUEST_WIDTH;
pub const CARD_HEIGHT: f64 = card::HEIGHT;

/// A step's key, content, status (C#'s non-null record fields).
pub fn key(step: &FlowStep) -> &str {
    step.key.as_deref().unwrap_or("")
}
pub fn content(step: &FlowStep) -> &str {
    step.content.as_deref().unwrap_or("")
}
pub fn status(step: &FlowStep) -> &str {
    step.status.as_deref().unwrap_or("pending")
}
/// `FlowStep.IsPending`.
pub fn is_pending(step: &FlowStep) -> bool {
    status(step) == "pending"
}
/// `FlowStep.Started`, `Ended`.
pub fn started(step: &FlowStep) -> Option<DateTimeOffset> {
    time::parse(step.started_at.as_deref())
}
pub fn ended(step: &FlowStep) -> Option<DateTimeOffset> {
    time::parse(step.ended_at.as_deref())
}

/// `Key(TodoItem)`: `id:<id>`, else `text:<content>`.
pub fn item_key(item: &TodoItem) -> String {
    match &item.id {
        Some(id) => format!("id:{id}"),
        None => format!("text:{}", item.content.as_deref().unwrap_or("")),
    }
}

/// `NodeSuffix`.
pub fn node_suffix(step: &FlowStep) -> String {
    format!("{SUFFIX}{}", stable_hash(key(step)))
}

/// `StableHash`: FNV-1a, 64 bits, in lowercase hex without leading zeros.
pub fn stable_hash(text: &str) -> String {
    let mut hash: u64 = 0xcbf29ce484222325;
    for b in text.as_bytes() {
        hash ^= *b as u64;
        hash = hash.wrapping_mul(0x00000100000001b3);
    }
    format!("{hash:x}")
}

const UNIX_EPOCH_MS: i64 = 62_135_596_800_000;

/// `DateTimeOffset.ToUnixTimeMilliseconds`.
pub fn unix_ms(time: &DateTimeOffset) -> i64 {
    time.utc_ticks().div_euclid(10_000) - UNIX_EPOCH_MS
}

/// `Stamp`: `yyyy-MM-dd'T'HH:mm:ss.fff'Z'` in UTC.
pub fn stamp(date: &DateTimeOffset) -> String {
    date.to_chrono().format("%Y-%m-%dT%H:%M:%S%.3fZ").to_string()
}

/// `Steps`: the steps a run draws, never a notice run's.
pub fn steps(run: &MightyGraphRun) -> &[FlowStep] {
    if run.is_notice() { &[] } else { run.steps.as_deref().unwrap_or(&[]) }
}

// ── the progress panel ──

/// `ProgressWidth`, `ProgressRowHeight`, `ProgressVisibleRows`, `ProgressBackgroundRows`.
pub const PROGRESS_WIDTH: f64 = 280.0;
pub const PROGRESS_ROW_HEIGHT: f64 = 24.0;
pub const PROGRESS_VISIBLE_ROWS: usize = 12;
pub const PROGRESS_BACKGROUND_ROWS: usize = 4;

/// `ProgressState`.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ProgressState {
    Done,
    Current,
    Pending,
    Stopped,
    Failed,
}

impl ProgressState {
    /// `ToString().ToLowerInvariant()`.
    pub fn name(self) -> &'static str {
        match self {
            ProgressState::Done => "done",
            ProgressState::Current => "current",
            ProgressState::Pending => "pending",
            ProgressState::Stopped => "stopped",
            ProgressState::Failed => "failed",
        }
    }
}

/// `ProgressRow(Index, Id, Text, State, StartedAt, EndedAt)`.
#[derive(Debug, Clone, PartialEq)]
pub struct ProgressRow {
    pub index: usize,
    pub id: String,
    pub text: String,
    pub state: ProgressState,
    pub started_at: Option<DateTimeOffset>,
    pub ended_at: Option<DateTimeOffset>,
}

/// `ProgressBackground(Id, Text, Agent, State, StartedAt, EndedAt)`.
#[derive(Debug, Clone, PartialEq)]
pub struct ProgressBackground {
    pub id: String,
    pub text: String,
    pub agent: bool,
    pub state: ProgressState,
    pub started_at: Option<DateTimeOffset>,
    pub ended_at: Option<DateTimeOffset>,
}

/// `Progress`: what the progress panel says.
#[derive(Debug, Clone, PartialEq)]
pub struct Progress {
    pub title: String,
    pub heading: String,
    pub request: String,
    pub state_word: String,
    pub status: String,
    pub count: Option<String>,
    pub rows: Vec<ProgressRow>,
    pub hidden: usize,
    pub running: bool,
    pub background: Vec<ProgressBackground>,
    pub background_hidden: usize,
}

fn figure_time(agent: &MightyGraphAgent, end: bool) -> Option<DateTimeOffset> {
    let figures = agent.figures.as_ref()?;
    time::parse(if end { figures.ended_at.as_deref() } else { figures.started_at.as_deref() })
}

/// `BackgroundOf`: the background work request `run` started, running first in launch order, then the ended ones, newest end first.
pub fn background_of(run: &MightyGraphRun, locale: &Locale) -> (Vec<ProgressBackground>, usize) {
    let rows: Vec<ProgressBackground> = support::flow_agents(run)
        .into_iter()
        .filter(|a| a.background == Some(true) && a.source_block_id.is_none() && (a.is_task() || a.is_background_agent()))
        .map(|agent| {
            let state = match card::status_bucket(agent.status()) {
                "completed" => ProgressState::Done,
                "error" => ProgressState::Failed,
                "stopped" => ProgressState::Stopped,
                _ if support::layout_terminal(agent.status()) => ProgressState::Stopped,
                _ => ProgressState::Current,
            };
            ProgressBackground {
                id: agent.id().into(),
                text: support::block_title(agent, locale),
                agent: agent.is_background_agent(),
                state,
                started_at: figure_time(agent, false),
                ended_at: figure_time(agent, true),
            }
        })
        .collect();
    let mut ordered: Vec<(usize, ProgressBackground)> = rows.into_iter().enumerate().collect();
    ordered.sort_by_key(|(offset, row)| {
        let current = row.state == ProgressState::Current;
        (
            if current { 0 } else { 1 },
            if current { *offset } else { 0 },
            Reverse(if current { i64::MIN } else { row.ended_at.map_or(i64::MIN, |e| e.utc_ticks()) }),
            Reverse(if current { 0 } else { *offset }),
        )
    });
    let total = ordered.len();
    let shown: Vec<ProgressBackground> = ordered.into_iter().take(PROGRESS_BACKGROUND_ROWS).map(|(_, r)| r).collect();
    let hidden = total - shown.len();
    (shown, hidden)
}

/// `ProgressOf`: the progress of request `runs[index]`, headed `heading` ("요청 27").
pub fn progress_of(index: usize, runs: &[MightyGraphRun], heading: &str, locale: &Locale) -> Progress {
    let request = &runs[index];
    let all = steps(request);
    let activity = activity_runs(index, runs);
    let awaiting = notice::awaiting_notice(request, &activity, locale).is_some();
    let running = activity.iter().any(|r| !support::layout_terminal(r.status())) || awaiting;
    let rows: Vec<ProgressRow> = all
        .iter()
        .enumerate()
        .map(|(offset, step)| {
            let state = match status(step) {
                "completed" => ProgressState::Done,
                "in_progress" => match state_of(step, &activity, locale).as_str() {
                    "running" => ProgressState::Current,
                    "error" => ProgressState::Failed,
                    "completed" => ProgressState::Done,
                    _ => ProgressState::Stopped,
                },
                _ => ProgressState::Pending,
            };
            let words = if state == ProgressState::Current { step.active_form.as_deref().unwrap_or(content(step)) } else { content(step) };
            let end = ended(step)
                .or_else(|| if matches!(state, ProgressState::Stopped | ProgressState::Failed) { activity_end(index, runs) } else { None });
            ProgressRow {
                index: offset,
                id: key(step).into(),
                text: title(offset, words, locale),
                state,
                started_at: started(step),
                ended_at: end,
            }
        })
        .collect();
    let room = if rows.len() <= PROGRESS_VISIBLE_ROWS { PROGRESS_VISIBLE_ROWS } else { PROGRESS_VISIBLE_ROWS - 1 };
    let focus = rows
        .iter()
        .position(|r| r.state == ProgressState::Current)
        .or_else(|| rows.iter().rposition(|r| r.state == ProgressState::Done))
        .unwrap_or(0) as isize;
    let start = if rows.len() <= room { 0 } else { (focus - room as isize / 3).min(rows.len() as isize - room as isize).max(0) as usize };
    let shown: Vec<ProgressRow> = rows.iter().skip(start).take(room).cloned().collect();
    let done = rows.iter().filter(|r| r.state == ProgressState::Done).count();
    let status = if running { "running".to_string() } else { card::status_bucket(request.status()).to_string() };
    let (background, background_hidden) = background_of(request, locale);
    Progress {
        title: locale.get("graph.progress.title"),
        heading: heading.into(),
        request: card::line(card::first_line(request.input())).unwrap_or_default(),
        state_word: if awaiting { notice::state_word(locale) } else { card::state_word(&status, locale) },
        status: if awaiting { "waiting".into() } else { status },
        count: (!rows.is_empty())
            .then(|| locale.get_with("graph.steps.count", &[("done", &done.to_string()), ("total", &rows.len().to_string())])),
        hidden: rows.len() - shown.len(),
        rows: shown,
        running,
        background,
        background_hidden,
    }
}

/// `StepLabel`: the step a block launched from the main session belongs to, as its card says it ("2. 노트 작성").
pub fn step_label(agent: &MightyGraphAgent, run: &MightyGraphRun, locale: &Locale) -> Option<String> {
    if let Some(parent) = agent.parent_id()
        && parent != agent.id()
        && support::flow_agents(run).iter().any(|a| a.id() == parent)
    {
        return None;
    }
    let begun: Vec<(usize, &FlowStep)> = steps(run).iter().enumerate().filter(|(_, s)| !is_pending(s)).collect();
    if begun.is_empty() {
        return None;
    }
    let spine: Vec<FlowStep> = begun.iter().map(|(_, s)| (*s).clone()).collect();
    let at = slot(agent, &spine);
    if at == 0 {
        return None;
    }
    let (index, chosen) = begun[at - 1];
    Some(title(index, content(chosen), locale))
}

fn title(index: usize, text: &str, locale: &Locale) -> String {
    locale.get_with("graph.step.title", &[("n", &(index + 1).to_string()), ("text", text)])
}

// ── recording ──

/// `Record`: a checklist change applied to the request run it names. Each item keeps the times of the step it was; an item that goes in
/// progress starts now, one that is done ends now. Items `previous` already had done are the earlier request's, never this one's steps.
pub fn record(progress: &TodoProgress, runs: &mut [MightyGraphRun], previous: Option<&TodoProgress>, now: DateTimeOffset) {
    let Some(run_id) = progress.run_id.as_deref() else { return };
    let Some(index) = runs.iter().rposition(|r| !r.is_notice() && (r.source_run_id.as_deref() == Some(run_id) || r.id() == run_id)) else {
        return;
    };
    let mut run = runs[index].clone();
    if run.steps.is_none() {
        let carried: Vec<String> = match previous {
            Some(before) if before.run_id.as_deref() != Some(run_id) => {
                before.items.iter().flatten().filter(|i| i.status.as_deref() == Some("completed")).map(item_key).collect()
            }
            _ => vec![],
        };
        run.steps_carried = (!carried.is_empty()).then_some(carried);
    }
    let skip: HashSet<String> = run.steps_carried.iter().flatten().cloned().collect();
    let mut old: HashMap<String, FlowStep> = HashMap::new();
    for step in run.steps.iter().flatten() {
        old.entry(key(step).to_string()).or_insert_with(|| step.clone());
    }
    let now_stamp = stamp(&now);
    // The latest a step ended before this change: where a step marked done without going in progress began.
    let mut ends: Vec<String> = run.steps.iter().flatten().filter_map(|s| s.ended_at.clone()).collect();
    ends.sort_by_key(|e| time::parse(Some(e)).map_or(i64::MIN, |t| t.utc_ticks()));
    let mut last_end = ends.pop();
    let first_root = run.root_entries().first().and_then(|e| e.timestamp.clone());
    let mut next = vec![];
    for item in progress.items.iter().flatten() {
        let item_key = item_key(item);
        if skip.contains(&item_key) {
            continue;
        }
        let mut step = old.get(&item_key).cloned().unwrap_or_else(|| FlowStep {
            key: Some(item_key.clone()),
            content: item.content.clone(),
            active_form: None,
            status: Some("pending".into()),
            started_at: None,
            ended_at: None,
        });
        step.content = Some(item.content.clone().unwrap_or_default());
        step.active_form = item.active_form.clone();
        let item_status = item.status.as_deref().unwrap_or("pending");
        match item_status {
            "in_progress" => {
                if step.started_at.is_none() || status(&step) == "pending" {
                    step.started_at = Some(now_stamp.clone());
                }
                step.ended_at = None;
            }
            "completed" => {
                if step.started_at.is_none() {
                    step.started_at = Some(last_end.clone().or_else(|| first_root.clone()).unwrap_or_else(|| now_stamp.clone()));
                }
                if step.ended_at.is_none() {
                    step.ended_at = Some(now_stamp.clone());
                    last_end = Some(now_stamp.clone());
                }
            }
            _ => {
                step.started_at = None;
                step.ended_at = None;
            }
        }
        step.status = Some(item_status.into());
        next.push(step);
    }
    run.steps = Some(next);
    runs[index] = run;
}

/// `RecordSteps`: a checklist change of the main session's (a `todos` event's checklist), kept on the request it belongs to. Read before
/// the pane's list is replaced with it. The caller passes the event's checklist only for a `todos` event.
pub fn record_steps(session: &RunSession, todos: Option<&TodoProgress>, now: DateTimeOffset) -> Option<RunSession> {
    if session.kind() != "claude" {
        return None;
    }
    let progress = mighty_core::normalize::todo_progress(todos)?;
    let live = session.graph_runs.as_ref().filter(|r| !r.is_empty())?;
    let mut runs = live.clone();
    record(&progress, &mut runs, session.todo_progress.as_ref(), now);
    let mut next = session.clone();
    next.graph_runs = Some(runs);
    Some(next)
}

// ── what a step holds ──

/// `StepIndex`: the step whose span holds `time`, the one that started last when spans meet.
pub fn step_index(time: &DateTimeOffset, steps: &[FlowStep]) -> Option<usize> {
    let mut best: Option<(usize, DateTimeOffset)> = None;
    for (index, step) in steps.iter().enumerate() {
        let Some(start) = started(step) else { continue };
        if (unix_ms(&start) as f64 / 1000.0).floor() > unix_ms(time) as f64 / 1000.0 {
            continue;
        }
        if let Some(end) = ended(step)
            && end.utc_ticks() < time.utc_ticks()
        {
            continue;
        }
        if best.is_none_or(|(_, b)| start.utc_ticks() >= b.utc_ticks()) {
            best = Some((index, start));
        }
    }
    best.map(|(i, _)| i)
}

/// `Slot`: where on the spine a block launched from the main session stands, 0 beside the request, k beside the k-th step.
pub fn slot(agent: &MightyGraphAgent, steps: &[FlowStep]) -> usize {
    figure_time(agent, false).and_then(|start| step_index(&start, steps)).map_or(0, |i| i + 1)
}

/// `ActivityRuns`: the request `runs[index]` and the notice runs that answer for its work.
pub fn activity_runs(index: usize, runs: &[MightyGraphRun]) -> Vec<MightyGraphRun> {
    let request = &runs[index];
    let Some(source) = request.source_run_id.as_deref() else { return vec![request.clone()] };
    std::iter::once(request.clone())
        .chain(
            runs.iter()
                .filter(|r| r.is_notice() && r.source_run_id.as_deref().is_some_and(|id| notice::original_run_id(id) == Some(source)))
                .cloned(),
        )
        .collect()
}

/// `Entries`: what the main session recorded during `step` of `runs[index]`, in the request and the notice turns after it.
pub fn entries(step: usize, index: usize, runs: &[MightyGraphRun]) -> Vec<LogEntry> {
    let all = steps(&runs[index]);
    if step >= all.len() {
        return vec![];
    }
    activity_runs(index, runs)
        .iter()
        .flat_map(|r| r.root_entries().to_vec())
        .filter(|entry| time::parse(entry.timestamp.as_deref()).is_some_and(|t| step_index(&t, all) == Some(step)))
        .collect()
}

// ── cards ──

/// `Latest`: the newest thing the main session did, as a card's line: what it last said (prose), or the tool call it made after that.
pub fn latest(entries: &[LogEntry]) -> Option<(String, bool)> {
    for entry in entries.iter().rev() {
        if let Some(activity) = &entry.activity {
            if activity.kind == "turn" {
                continue;
            }
            let words = if activity.summary.is_empty() { activity.tool_name.as_deref().unwrap_or("") } else { &activity.summary };
            if let Some(line) = card::line(words) {
                return Some((line, false));
            }
        } else if entry.kind() == "assistant"
            && let Some(said) = prose(card::last_line(entry.text()))
        {
            return Some((said, true));
        }
    }
    None
}

/// `Prose`: a line of Claude's reply without its markdown marks.
pub fn prose(raw: &str) -> Option<String> {
    let text = raw.replace("**", "").replace('`', "");
    let start = text.find(|c: char| !"#>-* ".contains(c)).unwrap_or(text.len());
    card::line(&text[start..])
}

fn calls(entries: &[LogEntry]) -> i32 {
    entries.iter().filter(|e| e.activity.as_ref().is_some_and(|a| a.kind != "turn")).count() as i32
}

/// `Card`: a step as its card.
pub fn card(step: usize, index: usize, runs: &[MightyGraphRun], locale: &Locale) -> Option<Content> {
    let request = &runs[index];
    let all = steps(request);
    let item = all.get(step)?;
    let held = entries(step, index, runs);
    let activity_runs = activity_runs(index, runs);
    let state = state_of(item, &activity_runs, locale);
    let pending = is_pending(item);
    let running = !support::layout_terminal(&state) && !pending;
    let newest =
        latest(&held).or_else(|| if running { item.active_form.as_deref().and_then(card::line).map(|line| (line, true)) } else { None });
    let activity = if pending { None } else { newest };
    let end = ended(item).or_else(|| if support::layout_terminal(&state) && !pending { activity_end(index, runs) } else { None });
    Some(Content {
        title: title(step, content(item), locale),
        identity: Some(log::lead_name(request, None, locale)),
        activity: activity.as_ref().map(|(t, _)| t.clone()),
        status: if pending { "pending".into() } else { state.clone() },
        state_word: if pending { locale.get("graph.state.pending") } else { card::state_word(&state, locale) },
        started_at: started(item),
        ended_at: end,
        tool_calls: Some(calls(&held)),
        tokens: None,
        nested: 0,
        marks: vec![],
        ink: roles::ink(FlowRole::Lead),
        summary: content(item).into(),
        is_task: false,
        exit_code: None,
        is_pending: pending,
        prose: activity.is_some_and(|(_, p)| p),
    })
}

/// `State`: the state a step's card shows: done as the list says; in progress, the state of its activity.
pub fn state_of(step: &FlowStep, activity: &[MightyGraphRun], locale: &Locale) -> String {
    if status(step) == "completed" {
        return "completed".into();
    }
    if activity.iter().any(|r| !support::layout_terminal(r.status())) {
        return "running".into();
    }
    if let Some(first) = activity.first()
        && notice::awaiting_notice(first, activity, locale).is_some()
    {
        return "running".into();
    }
    card::status_bucket(activity.last().map_or("completed", |r| r.status())).into()
}

/// `ActivityEnd`: when the activity's last turn ended, the last thing it recorded.
pub fn activity_end(index: usize, runs: &[MightyGraphRun]) -> Option<DateTimeOffset> {
    let mut best: Option<DateTimeOffset> = None;
    for run in activity_runs(index, runs) {
        let last = run.result_entries().last().or(run.root_entries().last());
        if let Some(time) = last.and_then(|e| time::parse(e.timestamp.as_deref()))
            && best.is_none_or(|b| time.utc_ticks() > b.utc_ticks())
        {
            best = Some(time);
        }
    }
    best
}

/// `LeadCard`: the 실행 block as its card: the main session's model, what it is doing (or answered), its state, time, tool calls and tokens.
pub fn lead_card(run: &MightyGraphRun, title: &str, locale: &Locale) -> Content {
    let status = card::status_bucket(run.status());
    let running = !support::layout_terminal(status);
    let current = steps(run)
        .iter()
        .find(|s| status_of(s) == "in_progress")
        .and_then(|step| card::line(step.active_form.as_deref().unwrap_or(content(step))));
    let answer = run.final_output().and_then(|output| prose(card::first_line(output))).map(|first| (first, true));
    let line =
        if running { latest(run.root_entries()).or(current.map(|c| (c, true))) } else { answer.or_else(|| latest(run.root_entries())) };
    let started = run.root_entries().first().and_then(|e| time::parse(e.timestamp.as_deref()));
    let ended = if running {
        None
    } else {
        run.result_entries().last().or(run.root_entries().last()).and_then(|e| time::parse(e.timestamp.as_deref()))
    };
    Content {
        title: title.into(),
        identity: Some(log::lead_name(run, None, locale)),
        activity: line.as_ref().map(|(t, _)| t.clone()),
        status: status.into(),
        state_word: card::state_word(status, locale),
        started_at: started,
        ended_at: ended,
        tool_calls: Some(calls(run.root_entries())),
        tokens: run.usage.filter(|u| u.total() != 0).map(|u| u.total()),
        nested: 0,
        marks: vec![],
        ink: roles::ink(FlowRole::Lead),
        summary: log::lead_name(run, None, locale),
        is_task: false,
        exit_code: None,
        is_pending: false,
        prose: line.is_some_and(|(_, p)| p),
    }
}

fn status_of(step: &FlowStep) -> &str {
    status(step)
}
