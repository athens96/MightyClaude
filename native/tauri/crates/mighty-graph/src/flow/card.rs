//! A sub-agent, or a command the session runs in the background, as the flow diagram's compact card (C# `FlowCard`,
//! MightyCode.Core/FlowCard.cs): who is working, what it is doing right now and how far it is. Every figure is one the app observed.

use std::collections::HashSet;
use std::sync::LazyLock;

use mighty_core::graph::{MightyGraphAgent, MightyGraphRun};
use mighty_core::locale::Locale;
use mighty_core::time::{self, DateTimeOffset};
use regex::Regex;

use crate::flow::roles::{self, AgentDefinition, FlowIdentity, FlowInk, FlowMark, FlowRole};
use crate::node;
use crate::support;
use crate::usage_format;

pub use crate::flow::roles::{LINE_BYTES, card_line as line, first_line, last_line};

/// `Width`, `Height` (FlowCard.cs:17): the card's size in the diagram; it is never dragged.
pub const WIDTH: f64 = 236.0;
pub const HEIGHT: f64 = 86.0;
/// `StepHeight`: a card beside a request's step, one more line.
pub const STEP_HEIGHT: f64 = 104.0;

/// `MobileMightySupport.Status` (Remote/MobileMighty.cs:135): a status in the five buckets.
pub fn status_bucket(raw: &str) -> &'static str {
    match raw {
        "completed" => "completed",
        "error" | "failed" => "error",
        "stopped" | "cancelled" | "interrupted" => "stopped",
        "waiting" => "waiting",
        _ => "running",
    }
}

/// `IsCompact`: whether the flow draws `agent` as a card; `opened`: the user opened it into its full block.
pub fn is_compact(agent: &MightyGraphAgent, opened: bool) -> bool {
    folds_to_card(agent) && !opened
}

/// `FoldsToCard`: every sub-agent and background command, running or ended, a copy in a notice run too.
pub fn folds_to_card(agent: &MightyGraphAgent) -> bool {
    agent.is_task() || support::is_subagent(agent)
}

/// `FlowCard.Content`: what a card says.
#[derive(Debug, Clone, PartialEq)]
pub struct Content {
    pub title: String,
    pub identity: Option<String>,
    pub activity: Option<String>,
    pub status: String,
    pub state_word: String,
    pub started_at: Option<DateTimeOffset>,
    pub ended_at: Option<DateTimeOffset>,
    pub tool_calls: Option<i32>,
    pub tokens: Option<i64>,
    pub nested: i32,
    pub marks: Vec<FlowMark>,
    pub ink: FlowInk,
    pub summary: String,
    pub is_task: bool,
    pub exit_code: Option<i32>,
    pub is_pending: bool,
    pub prose: bool,
}

impl Content {
    /// `Running`: a step not begun has no time running.
    pub fn running(&self) -> bool {
        !self.is_pending && !support::layout_terminal(&self.status)
    }
}

/// `Of`: the card of `agent`, a block of `run`. `output` holds the last lines a background command wrote.
pub fn of(
    agent: &MightyGraphAgent,
    run: &MightyGraphRun,
    definition: Option<&AgentDefinition>,
    output: &[String],
    locale: &Locale,
) -> Content {
    if let Some(task) = roles::task(agent, locale) {
        return task_content(agent, &task, output, locale);
    }
    let who = roles::subagent(agent, definition, locale);
    let status = status_bucket(agent.status());
    let running = !support::layout_terminal(status);
    let figures = agent.figures.as_ref();
    Content {
        title: support::block_title(agent, locale),
        identity: identity_line_of(who.as_ref(), agent, locale),
        activity: if running { doing(agent) } else { outcome(agent) },
        status: status.into(),
        state_word: state_word(status, locale),
        started_at: figures.and_then(|f| time::parse(f.started_at.as_deref())),
        ended_at: figures.and_then(|f| time::parse(f.ended_at.as_deref())),
        tool_calls: figures.and_then(|f| f.tool_calls),
        tokens: agent.usage.filter(|u| !node::is_empty(u)).map(|u| u.total()),
        nested: nested(agent, run),
        marks: who.as_ref().map(|w| w.marks.clone()).unwrap_or_default(),
        ink: who.as_ref().map_or(roles::ink(FlowRole::Subagent), |w| w.ink),
        summary: who.map(|w| w.summary).unwrap_or_default(),
        is_task: false,
        exit_code: None,
        is_pending: false,
        prose: false,
    }
}

/// `TaskContent`: a background command's card.
fn task_content(agent: &MightyGraphAgent, who: &FlowIdentity, output: &[String], locale: &Locale) -> Content {
    let status = status_bucket(agent.status());
    let command = match line(first_line(support::shown_input(agent))) {
        Some(line) => locale.get_with("graph.card.task", &[("command", &line)]),
        None => roles::role_name(FlowRole::Task, locale),
    };
    let figures = agent.figures.as_ref();
    Content {
        title: support::block_title(agent, locale),
        identity: Some(command),
        activity: last_output(output),
        status: status.into(),
        state_word: state_word(status, locale),
        started_at: figures.and_then(|f| time::parse(f.started_at.as_deref())),
        ended_at: figures.and_then(|f| time::parse(f.ended_at.as_deref())),
        tool_calls: None,
        tokens: None,
        nested: 0,
        marks: who.marks.clone(),
        ink: who.ink,
        summary: who.summary.clone(),
        is_task: true,
        exit_code: figures.and_then(|f| f.exit_code),
        is_pending: false,
        prose: false,
    }
}

static EXITED_LINE: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"^\[exited with code -?[0-9]+\]$").expect("pattern"));

/// `LastOutput`: the last line a background command wrote, without the `[exited with code 0]` line Claude Code adds.
pub fn last_output(lines: &[String]) -> Option<String> {
    for raw in lines.iter().rev() {
        let text = raw.trim_matches([' ', '\t']);
        if text.is_empty() || EXITED_LINE.is_match(text) {
            continue;
        }
        return line(text);
    }
    None
}

/// `IdentityLine(agent, definition)`: "Explore · Haiku 5.5".
pub fn identity_line(agent: &MightyGraphAgent, definition: Option<&AgentDefinition>, locale: &Locale) -> Option<String> {
    identity_line_of(roles::subagent(agent, definition, locale).as_ref(), agent, locale)
}

/// The first model a block's own responses name.
pub fn answered_model(agent: &MightyGraphAgent) -> Option<&str> {
    agent.response_records.iter().flatten().flatten().filter_map(|r| r.model.as_deref()).find(|m| !m.is_empty())
}

pub(crate) fn identity_line_of(who: Option<&FlowIdentity>, agent: &MightyGraphAgent, locale: &Locale) -> Option<String> {
    let model = match answered_model(agent) {
        Some(answered) => Some(usage_format::short_name(answered, None, true)),
        None => usage_format::versioned_node_label(who.and_then(|w| w.model_label.as_deref()), locale),
    };
    let kind = who.and_then(|w| w.kind.as_deref().filter(|_| !w.marks.contains(&FlowMark::Fork))).map(|t| roles::type_name(t).to_string());
    let parts: Vec<String> = [kind, model].into_iter().flatten().collect();
    (!parts.is_empty()).then(|| parts.join(" · "))
}

/// `Doing`: a running agent's latest tool call, else the last line it wrote, else the first line of what it was asked.
pub fn doing(agent: &MightyGraphAgent) -> Option<String> {
    for entry in agent.entries().iter().rev() {
        let Some(activity) = &entry.activity else { continue };
        if activity.kind == "turn" {
            continue;
        }
        let words = if activity.summary.is_empty() { activity.tool_name.as_deref().unwrap_or("") } else { &activity.summary };
        if let Some(line) = line(words) {
            return Some(line);
        }
    }
    for entry in agent.entries().iter().rev() {
        if entry.kind() == "assistant"
            && let Some(line) = line(last_line(entry.text()))
        {
            return Some(line);
        }
    }
    line(first_line(support::shown_input(agent)))
}

/// `Outcome`: the first line of an ended agent's answer, else of the last thing it recorded.
pub fn outcome(agent: &MightyGraphAgent) -> Option<String> {
    if let Some(answer) = agent.entries().iter().rev().find(|e| e.kind() == "assistant" && !e.text().is_empty())
        && let Some(first) = line(first_line(answer.text()))
    {
        return Some(first);
    }
    for entry in agent.entries().iter().rev() {
        if entry.activity.is_none()
            && let Some(line) = line(first_line(entry.text()))
        {
            return Some(line);
        }
    }
    None
}

/// `StateWord`: a status bucket in the diagram's own words.
pub fn state_word(status: &str, locale: &Locale) -> String {
    locale.get(match status {
        "completed" => "graph.state.completed",
        "error" | "failed" => "graph.state.error",
        "stopped" | "cancelled" | "interrupted" => "graph.state.stopped",
        "waiting" => "graph.state.waiting",
        _ => "graph.state.running",
    })
}

/// `Nested`: the sub-agents under `agent`, at any depth.
pub fn nested(agent: &MightyGraphAgent, run: &MightyGraphRun) -> i32 {
    let agents = support::flow_agents(run);
    let mut under: HashSet<&str> = HashSet::from([agent.id()]);
    let mut count = 0;
    let mut grew = true;
    while grew {
        grew = false;
        for candidate in &agents {
            if under.contains(candidate.id()) {
                continue;
            }
            let Some(parent) = candidate.parent_id() else { continue };
            if !under.contains(parent) {
                continue;
            }
            under.insert(candidate.id());
            grew = true;
            if support::is_subagent(candidate) {
                count += 1;
            }
        }
    }
    count
}

/// `FigureParts`: the card's last line before and after its time.
pub fn figure_parts(content: &Content, locale: &Locale) -> (Vec<String>, Vec<String>) {
    let mut before = vec![content.state_word.clone()];
    if content.is_task
        && !content.running()
        && let Some(code) = content.exit_code
    {
        before.push(locale.get_with("graph.card.exitCode", &[("code", &code.to_string())]));
    }
    let mut after = vec![];
    if let Some(tools) = content.tool_calls.filter(|t| *t > 0) {
        after.push(locale.get_with("graph.card.tools", &[("count", &tools.to_string())]));
    }
    if let Some(tokens) = content.tokens {
        after.push(locale.get_with("graph.usage.summary", &[("total", &node::compact(tokens))]));
    }
    (before, after)
}

/// `StylePresentation.Elapsed` (Styles/StylePresentation.cs:80): "12초", "3분 4초", "1시간 2분".
pub fn elapsed_words(start: DateTimeOffset, end: DateTimeOffset, locale: &Locale) -> String {
    let seconds = ((end.utc_ticks() - start.utc_ticks()) / 10_000_000).max(0);
    if seconds < 60 {
        return locale.get_with("styles.state.elapsedSeconds", &[("seconds", &seconds.to_string())]);
    }
    if seconds < 3600 {
        return locale.get_with(
            "styles.state.elapsedMinutes",
            &[("minutes", &(seconds / 60).to_string()), ("seconds", &(seconds % 60).to_string())],
        );
    }
    locale
        .get_with("styles.state.elapsedHours", &[("hours", &(seconds / 3600).to_string()), ("minutes", &(seconds % 3600 / 60).to_string())])
}

/// `Elapsed`: while it runs the time since it started; once it ended, how long it ran.
pub fn elapsed(content: &Content, now: DateTimeOffset, locale: &Locale) -> Option<String> {
    let start = content.started_at?;
    if content.running() {
        return Some(elapsed_words(start, now, locale));
    }
    content.ended_at.filter(|end| end.utc_ticks() >= start.utc_ticks()).map(|end| elapsed_words(start, end, locale))
}

/// `Figures`: the card's last line, its state then the figures it has.
pub fn figures(content: &Content, now: DateTimeOffset, locale: &Locale) -> String {
    let (before, after) = figure_parts(content, locale);
    let mut parts = before;
    if let Some(time) = elapsed(content, now, locale) {
        parts.push(time);
    }
    parts.extend(after);
    parts.join(" · ")
}

/// `CopyNodeID`: the node of `agent`'s newest full copy in a notice run, where a finished background agent's card leads.
pub fn copy_node_id(agent: &MightyGraphAgent, runs: &[MightyGraphRun]) -> Option<String> {
    for run in runs.iter().rev() {
        if !run.is_notice() {
            continue;
        }
        if let Some(copied) = run.agents().iter().rev().find(|c| c.source_block_id.as_deref() == Some(agent.id())) {
            return Some(support::node_id(run, &format!("agent:{}", copied.id())));
        }
    }
    None
}
