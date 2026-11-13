//! The role view's words for the web page (C# `FlowCopy`, MightyCode.Core/FlowCopy.cs; the page's native/windows/web/src/mighty/flow.ts):
//! who each block is, a card's lines, the 실행 card, a step's card and what it holds, the delegate line, the return node, the progress panel
//! and the session log, read with the very rules the Mac draws them by. Inks are design token names. Each record writes itself as the page
//! reads it (`Wire.Json`: camelCase, nulls left out).

use std::collections::HashMap;

use indexmap::IndexMap;
use mighty_core::graph::{MightyGraphAgent, MightyGraphRun};
use mighty_core::locale::Locale;
use mighty_core::model::RunSession;
use serde_json::{Map, Value, json};

use crate::flow::card::{self, Content};
use crate::flow::lines;
use crate::flow::log;
use crate::flow::roles::{self, AgentDefinition, FlowIdentity, FlowInk, FlowMark, FlowRole};
use crate::flow::steps::{self, ProgressState};
use crate::layout::{self, Layout};
use crate::notice;
use crate::support;
use crate::usage_format;

/// `Mark(Name, Help)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Mark {
    pub name: String,
    pub help: String,
}

/// `Role(Name, Ink, Type, TypeChip, Effort, EffortHelp, Marks, Summary, ModelLabel)`: who a block is.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Role {
    pub name: String,
    pub ink: String,
    pub kind: Option<String>,
    pub type_chip: Option<String>,
    pub effort: Option<String>,
    pub effort_help: Option<String>,
    pub marks: Vec<Mark>,
    pub summary: String,
    pub model_label: Option<String>,
}

/// `Card`: a compact card. `before`/`after` are the figures on either side of its time; times are milliseconds since 1970.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Card {
    pub title: String,
    pub identity: Option<String>,
    pub activity: Option<String>,
    pub status: String,
    pub state_word: String,
    pub before: Vec<String>,
    pub after: Vec<String>,
    pub started_at: Option<i64>,
    pub ended_at: Option<i64>,
    pub running: bool,
    pub nested: i32,
    pub nested_help: Option<String>,
    pub marks: Vec<Mark>,
    pub ink: String,
    pub summary: String,
    pub is_task: bool,
    pub copy_node_id: Option<String>,
    pub activate_help: String,
    pub prose: bool,
    pub pending: bool,
    pub step: Option<String>,
    pub step_help: Option<String>,
}

/// `Part(Text, Ink)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Part {
    pub text: String,
    pub ink: String,
}

/// `Line(Head, Parts, Model, Progress, Text)`: the delegate line.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Line {
    pub head: String,
    pub parts: Vec<Part>,
    pub model: Option<String>,
    pub progress: Option<String>,
    pub text: String,
}

/// `Back(Text, Waiting)`: the return node.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Back {
    pub text: String,
    pub waiting: bool,
}

/// `StepCopy(Title, StateWord, Status, EntryIds)`: a step opened into its block.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct StepCopy {
    pub title: String,
    pub state_word: String,
    pub status: String,
    pub entry_ids: Vec<String>,
}

/// `Block(Role, Card, Line, Back, Step)`: a block's role-view copy, each part only where it belongs.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct Block {
    pub role: Option<Role>,
    pub card: Option<Card>,
    pub line: Option<Line>,
    pub back: Option<Back>,
    pub step: Option<StepCopy>,
}

/// `ProgressRow(Index, NodeId, Text, State, StartedAt, EndedAt)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ProgressRow {
    pub index: usize,
    pub node_id: Option<String>,
    pub text: String,
    pub state: String,
    pub started_at: Option<i64>,
    pub ended_at: Option<i64>,
}

/// `ProgressBackgroundRow(Id, Text, Agent, State, StartedAt, EndedAt)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ProgressBackgroundRow {
    pub id: String,
    pub text: String,
    pub agent: bool,
    pub state: String,
    pub started_at: Option<i64>,
    pub ended_at: Option<i64>,
}

/// `ProgressCopy`: a request's progress panel.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ProgressCopy {
    pub title: String,
    pub heading: String,
    pub request: String,
    pub state_word: String,
    pub status: String,
    pub count: Option<String>,
    pub rows: Vec<ProgressRow>,
    pub hidden: usize,
    pub more: Option<String>,
    pub running: bool,
    pub request_node_id: String,
    pub request_help: String,
    pub background: Option<Vec<ProgressBackgroundRow>>,
    pub background_hidden: usize,
}

/// `LogRow(Id, Clock, Who, Ink, What, NodeId)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct LogRow {
    pub id: String,
    pub clock: String,
    pub who: String,
    pub ink: String,
    pub what: String,
    pub node_id: String,
}

/// `Log(Status, StatusText, Rows, Collapsed)`: the session log strip.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Log {
    pub status: Vec<Part>,
    pub status_text: String,
    pub rows: Vec<LogRow>,
    pub collapsed: bool,
}

fn mark_name(mark: FlowMark) -> &'static str {
    match mark {
        FlowMark::Background => "background",
        FlowMark::Fork => "fork",
        FlowMark::Worktree => "worktree",
    }
}

fn marks(marks: &[FlowMark], locale: &Locale) -> Vec<Mark> {
    marks.iter().map(|m| Mark { name: mark_name(*m).into(), help: roles::help(*m, locale) }).collect()
}

fn ink(ink: FlowInk) -> String {
    roles::token(ink).into()
}

fn role_ink(role: FlowRole) -> String {
    roles::token(roles::ink(role)).into()
}

fn role_name(role: FlowRole) -> &'static str {
    match role {
        FlowRole::Lead => "lead",
        FlowRole::Subagent => "subagent",
        FlowRole::Task => "task",
        FlowRole::Advisor => "advisor",
    }
}

fn role_of(who: &FlowIdentity, locale: &Locale) -> Role {
    Role {
        name: role_name(who.role).into(),
        ink: ink(who.ink),
        kind: who.kind.clone(),
        type_chip: who.type_chip.clone(),
        effort: who.effort.clone(),
        effort_help: who.effort.as_ref().map(|e| locale.get_with("composer.effort.menu", &[("effort", e)])),
        marks: marks(&who.marks, locale),
        summary: who.summary.clone(),
        model_label: usage_format::versioned_node_label(who.model_label.as_deref(), locale),
    }
}

/// `Locate`: the run and suffix a node id names.
fn locate<'a>(node_id: &str, runs: &'a [MightyGraphRun]) -> Option<(&'a MightyGraphRun, usize, String)> {
    runs.iter().enumerate().find_map(|(i, run)| {
        let prefix = support::node_id(run, "");
        node_id.strip_prefix(prefix.as_str()).map(|suffix| (run, i, suffix.to_string()))
    })
}

fn card_of(content: &Content, copy: Option<String>, activate_key: &str, locale: &Locale) -> Card {
    let (before, after) = card::figure_parts(content, locale);
    Card {
        title: content.title.clone(),
        identity: content.identity.clone(),
        activity: content.activity.clone(),
        status: content.status.clone(),
        state_word: content.state_word.clone(),
        before,
        after,
        started_at: content.started_at.as_ref().map(steps::unix_ms),
        ended_at: content.ended_at.as_ref().map(steps::unix_ms),
        running: content.running(),
        nested: content.nested,
        nested_help: (content.nested > 0).then(|| locale.get_with("graph.card.nested", &[("count", &content.nested.to_string())])),
        marks: marks(&content.marks, locale),
        ink: ink(content.ink),
        summary: content.summary.clone(),
        is_task: content.is_task,
        copy_node_id: copy,
        activate_help: locale.get(activate_key),
        prose: content.prose,
        pending: content.is_pending,
        step: None,
        step_help: None,
    }
}

fn definition<'a>(agent: &MightyGraphAgent, definitions: &'a HashMap<String, AgentDefinition>) -> Option<&'a AgentDefinition> {
    agent.identity.as_ref().and_then(|i| i.kind.as_deref()).and_then(|t| definitions.get(t))
}

/// `Blocks`: the role-view copy of every node of `layout` that has one. `fan_out`: the pane draws the role view (a Claude pane).
/// `output` gives a background command's last lines by its block id.
pub fn blocks(
    layout: &Layout,
    runs: &[MightyGraphRun],
    fan_out: bool,
    definitions: &HashMap<String, AgentDefinition>,
    output: &dyn Fn(&str) -> Vec<String>,
    locale: &Locale,
) -> IndexMap<String, Block> {
    let shown = support::shown_runs(runs, locale);
    let shown: &[MightyGraphRun] = &shown;
    let ordinals = support::request_ordinals(shown);
    let mut result = IndexMap::new();
    for node in &layout.nodes {
        let Some((run, index, suffix)) = locate(&node.id, shown) else { continue };
        match node.kind.as_str() {
            layout::WORK_KIND => {
                // The role view's 실행 card: who the main session is, what it is doing or answered, its figures.
                let lead = fan_out.then(|| {
                    let title = notice::execution_title(run.is_notice(), ordinals[index], locale);
                    card_of(&steps::lead_card(run, &title, locale), None, "graph.card.open", locale)
                });
                result.insert(
                    node.id.clone(),
                    Block { role: Some(role_of(&roles::lead(run, locale), locale)), card: lead, ..Block::default() },
                );
            }
            layout::STEP_KIND if fan_out && suffix.starts_with(steps::SUFFIX) => {
                let all = steps::steps(run);
                let Some(step) = all.iter().position(|s| steps::node_suffix(s) == suffix) else { continue };
                let Some(content) = steps::card(step, index, shown, locale) else { continue };
                let held = steps::entries(step, index, shown).iter().map(|e| e.id().to_string()).collect();
                let step_copy = StepCopy {
                    title: content.title.clone(),
                    state_word: content.state_word.clone(),
                    status: content.status.clone(),
                    entry_ids: held,
                };
                result.insert(
                    node.id.clone(),
                    Block { card: Some(card_of(&content, None, "graph.card.open", locale)), step: Some(step_copy), ..Block::default() },
                );
            }
            "agent" if suffix.starts_with("agent:") => {
                let Some(agent) = support::flow_agents(run).into_iter().find(|a| format!("agent:{}", a.id()) == suffix) else { continue };
                let definition = definition(agent, definitions);
                let who = roles::subagent(agent, definition, locale).or_else(|| if fan_out { roles::task(agent, locale) } else { None });
                let mut card_copy = None;
                if fan_out && card::folds_to_card(agent) {
                    let lines = if agent.is_task() { output(agent.id()) } else { vec![] };
                    let content = card::of(agent, run, definition, &lines, locale);
                    let copy = if agent.background == Some(true) && support::layout_terminal(agent.status()) {
                        card::copy_node_id(agent, shown)
                    } else {
                        None
                    };
                    let key = if copy.is_none() { "graph.card.open" } else { "graph.card.showCopy" };
                    let mut made = card_of(&content, copy, key, locale);
                    // A card launched in one of the request's steps names it.
                    if let Some(step) = steps::step_label(agent, run, locale) {
                        made.step_help = Some(locale.get_with("graph.card.inStep", &[("step", &step)]));
                        made.step = Some(step);
                    }
                    card_copy = Some(made);
                }
                if who.is_some() || card_copy.is_some() {
                    result.insert(node.id.clone(), Block { role: who.map(|w| role_of(&w, locale)), card: card_copy, ..Block::default() });
                }
            }
            layout::DELEGATE_KIND if fan_out && suffix.starts_with(layout::DELEGATE_SUFFIX) => {
                let children = lines::children(&suffix[layout::DELEGATE_SUFFIX.len()..], run);
                if let Some(line) = lines::line_of(&children, definitions, locale) {
                    let parts = line.parts.iter().map(|p| Part { text: p.text.clone(), ink: role_ink(p.role) }).collect();
                    let copy = Line {
                        head: line.head.clone(),
                        parts,
                        model: line.model.clone(),
                        progress: line.progress.clone(),
                        text: line.text(),
                    };
                    result.insert(node.id.clone(), Block { line: Some(copy), ..Block::default() });
                }
            }
            layout::RETURN_KIND if fan_out => {
                if let Some(state) = lines::return_of(run, locale) {
                    let waiting = state.waiting();
                    result.insert(node.id.clone(), Block { back: Some(Back { text: state.text, waiting }), ..Block::default() });
                }
            }
            _ => {}
        }
    }
    result
}

/// `ProgressOf`: the progress panel's copy of every request of a pane (no notice run), by its run id.
pub fn progress_of(runs: &[MightyGraphRun], locale: &Locale) -> IndexMap<String, ProgressCopy> {
    let shown = support::shown_runs(runs, locale);
    let shown: &[MightyGraphRun] = &shown;
    let ordinals = support::request_ordinals(shown);
    let mut result = IndexMap::new();
    for (index, run) in shown.iter().enumerate() {
        if run.is_notice() {
            continue;
        }
        let heading = locale.get_with("graph.timeline.requestOrdinal", &[("n", &ordinals[index].to_string())]);
        let progress = steps::progress_of(index, shown, &heading, locale);
        let all = steps::steps(run);
        let rows = progress
            .rows
            .iter()
            .map(|row| ProgressRow {
                index: row.index,
                node_id: (row.state != ProgressState::Pending).then(|| support::node_id(run, &steps::node_suffix(&all[row.index]))),
                text: row.text.clone(),
                state: row.state.name().into(),
                started_at: row.started_at.as_ref().map(steps::unix_ms),
                ended_at: row.ended_at.as_ref().map(steps::unix_ms),
            })
            .collect();
        let more = (progress.hidden > 0).then(|| locale.get_with("graph.progress.more", &[("count", &progress.hidden.to_string())]));
        let background = (!progress.background.is_empty()).then(|| {
            progress
                .background
                .iter()
                .map(|item| ProgressBackgroundRow {
                    id: item.id.clone(),
                    text: item.text.clone(),
                    agent: item.agent,
                    state: item.state.name().into(),
                    started_at: item.started_at.as_ref().map(steps::unix_ms),
                    ended_at: item.ended_at.as_ref().map(steps::unix_ms),
                })
                .collect()
        });
        result.insert(
            run.id().to_string(),
            ProgressCopy {
                title: progress.title,
                heading: progress.heading,
                request: progress.request,
                state_word: progress.state_word,
                status: progress.status,
                count: progress.count,
                rows,
                hidden: progress.hidden,
                more,
                running: progress.running,
                request_node_id: support::node_id(run, "request"),
                request_help: locale.get("graph.progress.showRequest"),
                background,
                background_hidden: progress.background_hidden,
            },
        );
    }
    result
}

/// `LogOf`: the session log strip of a pane's runs, none for a pane without a request. `collapsed`: the pane folded it, which reads no rows.
/// `zone` is the reader's time zone (none: the local one).
pub fn log_of(
    runs: &[MightyGraphRun],
    collapsed: bool,
    definitions: &HashMap<String, AgentDefinition>,
    zone: Option<chrono::FixedOffset>,
    locale: &Locale,
) -> Option<Log> {
    let shown = support::shown_runs(runs, locale);
    let status = log::status_of(&shown, locale)?;
    let rows = if collapsed {
        vec![]
    } else {
        log::rows(&shown, log::VISIBLE_ROWS, definitions, locale)
            .into_iter()
            .map(|row| LogRow {
                id: row.id,
                clock: log::clock(&row.time, zone),
                who: row.who,
                ink: ink(row.ink),
                what: row.what,
                node_id: row.node_id,
            })
            .collect()
    };
    Some(Log {
        status: status.parts().into_iter().map(|p| Part { text: p.text, ink: role_ink(p.role) }).collect(),
        status_text: status.text(),
        rows,
        collapsed,
    })
}

/// `Collapse`: the pane with its log strip folded or open (`FlowLogCollapsed`, written only when folded).
pub fn collapse(pane: &RunSession, collapsed: bool) -> RunSession {
    let mut next = pane.clone();
    next.flow_log_collapsed = collapsed.then_some(true);
    next
}

// ── JSON as the page reads it (`Wire.Json`: camelCase, nulls left out) ──

fn put(map: &mut Map<String, Value>, key: &str, value: impl Into<Value>) {
    let value = value.into();
    if !value.is_null() {
        map.insert(key.into(), value);
    }
}

fn opt<T: Into<Value>>(value: Option<T>) -> Value {
    value.map_or(Value::Null, Into::into)
}

impl Mark {
    pub fn to_json(&self) -> Value {
        json!({ "name": self.name, "help": self.help })
    }
}

impl Part {
    pub fn to_json(&self) -> Value {
        json!({ "text": self.text, "ink": self.ink })
    }
}

impl Role {
    pub fn to_json(&self) -> Value {
        let mut m = Map::new();
        put(&mut m, "name", self.name.clone());
        put(&mut m, "ink", self.ink.clone());
        put(&mut m, "type", opt(self.kind.clone()));
        put(&mut m, "typeChip", opt(self.type_chip.clone()));
        put(&mut m, "effort", opt(self.effort.clone()));
        put(&mut m, "effortHelp", opt(self.effort_help.clone()));
        put(&mut m, "marks", Value::Array(self.marks.iter().map(Mark::to_json).collect()));
        put(&mut m, "summary", self.summary.clone());
        put(&mut m, "modelLabel", opt(self.model_label.clone()));
        Value::Object(m)
    }
}

impl Card {
    pub fn to_json(&self) -> Value {
        let mut m = Map::new();
        put(&mut m, "title", self.title.clone());
        put(&mut m, "identity", opt(self.identity.clone()));
        put(&mut m, "activity", opt(self.activity.clone()));
        put(&mut m, "status", self.status.clone());
        put(&mut m, "stateWord", self.state_word.clone());
        put(&mut m, "before", self.before.clone());
        put(&mut m, "after", self.after.clone());
        put(&mut m, "startedAt", opt(self.started_at));
        put(&mut m, "endedAt", opt(self.ended_at));
        put(&mut m, "running", self.running);
        put(&mut m, "nested", self.nested);
        put(&mut m, "nestedHelp", opt(self.nested_help.clone()));
        put(&mut m, "marks", Value::Array(self.marks.iter().map(Mark::to_json).collect()));
        put(&mut m, "ink", self.ink.clone());
        put(&mut m, "summary", self.summary.clone());
        put(&mut m, "isTask", self.is_task);
        put(&mut m, "copyNodeId", opt(self.copy_node_id.clone()));
        put(&mut m, "activateHelp", self.activate_help.clone());
        put(&mut m, "prose", self.prose);
        put(&mut m, "pending", self.pending);
        put(&mut m, "step", opt(self.step.clone()));
        put(&mut m, "stepHelp", opt(self.step_help.clone()));
        Value::Object(m)
    }
}

impl Line {
    pub fn to_json(&self) -> Value {
        let mut m = Map::new();
        put(&mut m, "head", self.head.clone());
        put(&mut m, "parts", Value::Array(self.parts.iter().map(Part::to_json).collect()));
        put(&mut m, "model", opt(self.model.clone()));
        put(&mut m, "progress", opt(self.progress.clone()));
        put(&mut m, "text", self.text.clone());
        Value::Object(m)
    }
}

impl Block {
    pub fn to_json(&self) -> Value {
        let mut m = Map::new();
        put(&mut m, "role", self.role.as_ref().map_or(Value::Null, Role::to_json));
        put(&mut m, "card", self.card.as_ref().map_or(Value::Null, Card::to_json));
        put(&mut m, "line", self.line.as_ref().map_or(Value::Null, Line::to_json));
        put(&mut m, "back", self.back.as_ref().map_or(Value::Null, |b| json!({ "text": b.text, "waiting": b.waiting })));
        put(
            &mut m,
            "step",
            self.step.as_ref().map_or(
                Value::Null,
                |s| json!({ "title": s.title, "stateWord": s.state_word, "status": s.status, "entryIds": s.entry_ids }),
            ),
        );
        Value::Object(m)
    }
}

impl ProgressCopy {
    pub fn to_json(&self) -> Value {
        let mut m = Map::new();
        put(&mut m, "title", self.title.clone());
        put(&mut m, "heading", self.heading.clone());
        put(&mut m, "request", self.request.clone());
        put(&mut m, "stateWord", self.state_word.clone());
        put(&mut m, "status", self.status.clone());
        put(&mut m, "count", opt(self.count.clone()));
        let rows = self
            .rows
            .iter()
            .map(|r| {
                let mut row = Map::new();
                put(&mut row, "index", r.index);
                put(&mut row, "nodeId", opt(r.node_id.clone()));
                put(&mut row, "text", r.text.clone());
                put(&mut row, "state", r.state.clone());
                put(&mut row, "startedAt", opt(r.started_at));
                put(&mut row, "endedAt", opt(r.ended_at));
                Value::Object(row)
            })
            .collect();
        put(&mut m, "rows", Value::Array(rows));
        put(&mut m, "hidden", self.hidden);
        put(&mut m, "more", opt(self.more.clone()));
        put(&mut m, "running", self.running);
        put(&mut m, "requestNodeId", self.request_node_id.clone());
        put(&mut m, "requestHelp", self.request_help.clone());
        let background = self.background.as_ref().map(|rows| {
            Value::Array(
                rows.iter()
                    .map(|r| {
                        let mut row = Map::new();
                        put(&mut row, "id", r.id.clone());
                        put(&mut row, "text", r.text.clone());
                        put(&mut row, "agent", r.agent);
                        put(&mut row, "state", r.state.clone());
                        put(&mut row, "startedAt", opt(r.started_at));
                        put(&mut row, "endedAt", opt(r.ended_at));
                        Value::Object(row)
                    })
                    .collect(),
            )
        });
        put(&mut m, "background", opt(background));
        put(&mut m, "backgroundHidden", self.background_hidden);
        Value::Object(m)
    }
}

impl Log {
    pub fn to_json(&self) -> Value {
        let rows: Vec<Value> = self
            .rows
            .iter()
            .map(|r| json!({ "id": r.id, "clock": r.clock, "who": r.who, "ink": r.ink, "what": r.what, "nodeId": r.node_id }))
            .collect();
        json!({
            "status": self.status.iter().map(Part::to_json).collect::<Vec<_>>(),
            "statusText": self.status_text,
            "rows": rows,
            "collapsed": self.collapsed,
        })
    }
}
