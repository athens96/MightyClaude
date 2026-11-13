//! The flow's session log (C# `FlowLog`, MightyCode.Core/FlowLog.cs): the last few things that happened in the pane's newest activity,
//! each with its time, who did it and what it was, under one status line. Every row is something the app saw happen at a time it recorded.

use std::collections::HashMap;

use chrono::{FixedOffset, Local, TimeZone};
use mighty_core::graph::MightyGraphRun;
use mighty_core::locale::Locale;
use mighty_core::model::LogEntry;
use mighty_core::time::{self, DateTimeOffset};

use crate::flow::card;
use crate::flow::lines;
use crate::flow::roles::{self, AgentDefinition, FlowInk, FlowRole};
use crate::flow::steps;
use crate::labels;
use crate::notice;
use crate::support;
use crate::usage_format;

/// `Actor(Role, AgentId)`: who a row is about.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Actor {
    pub role: FlowRole,
    pub agent_id: Option<String>,
}

/// `Row(Id, Time, Actor, Who, Ink, What, NodeID)`.
#[derive(Debug, Clone, PartialEq)]
pub struct Row {
    pub id: String,
    pub time: DateTimeOffset,
    pub actor: Actor,
    pub who: String,
    pub ink: FlowInk,
    pub what: String,
    pub node_id: String,
}

/// `VisibleRows`: the rows the strip shows when it is open.
pub const VISIBLE_ROWS: usize = 5;
const LINE_BYTES: usize = 240;

fn line(raw: &str) -> Option<String> {
    Some(crate::node::clean(raw, LINE_BYTES, true)).filter(|c| !c.is_empty())
}

/// `Activity`: the runs of the pane's newest activity, its newest request and the notice runs after it.
pub fn activity(runs: &[MightyGraphRun]) -> (usize, usize) {
    if runs.is_empty() { (0, 0) } else { (support::newest_request_index(runs).unwrap_or(runs.len() - 1), runs.len()) }
}

/// A candidate row before its time is read: the stamp it was recorded with, and the row without its time.
struct Candidate {
    stamp: String,
    id: String,
    actor: Actor,
    who: String,
    ink: FlowInk,
    what: String,
    node_id: String,
}

/// `Rows`: the last `limit` rows of the newest activity, oldest first.
pub fn rows(runs: &[MightyGraphRun], limit: usize, definitions: &HashMap<String, AgentDefinition>, locale: &Locale) -> Vec<Row> {
    if limit == 0 {
        return vec![];
    }
    let mut candidates: Vec<Candidate> = vec![];
    let (start, end) = activity(runs);
    for index in start..end {
        let run = &runs[index];
        let lead_node = support::node_id(run, crate::layout::WORK_SUFFIX);
        // A notice turn is the same session answering: named as the request is when it has no answer of its own yet.
        let lead_name = lead_name(run, Some(&runs[start]), locale);
        let lead = |id: String, stamp: &str, what: String, node: Option<String>| Candidate {
            stamp: stamp.into(),
            id,
            actor: Actor { role: FlowRole::Lead, agent_id: None },
            who: lead_name.clone(),
            ink: roles::ink(FlowRole::Lead),
            what,
            node_id: node.unwrap_or_else(|| lead_node.clone()),
        };
        if run.is_notice() {
            // A notice came in: at the first thing its turn recorded, else when the work it reports on ended.
            let arrived = run.root_entries().first().and_then(|e| e.timestamp.clone()).or_else(|| {
                run.agents()
                    .iter()
                    .filter_map(|a| a.figures.as_ref().and_then(|f| f.ended_at.clone()))
                    .max_by(|a, b| mighty_core::normalize::ordinal(a, b))
            });
            if let Some(arrived) = arrived
                && let Some(text) = line(&locale.get_with("graph.log.notice", &[("title", &notice::heading(run, locale))]))
            {
                candidates.push(lead(format!("{}:notice", run.id()), &arrived, text, Some(support::node_id(run, "request"))));
            }
        }
        // A request's own record in one of its steps is that step's (FlowSteps).
        let all = steps::steps(run);
        let node_of = |entry: &LogEntry| -> String {
            if !all.is_empty()
                && let Some(t) = time::parse(entry.timestamp.as_deref())
                && let Some(step) = steps::step_index(&t, all)
            {
                return support::node_id(run, &steps::node_suffix(&all[step]));
            }
            lead_node.clone()
        };
        let mut latest = vec![];
        for entry in run.root_entries().iter().rev() {
            if latest.len() >= limit {
                break;
            }
            let Some(what) = lead_words(entry, locale) else { continue };
            latest.push(lead(format!("{}:{}", run.id(), entry.id()), entry.timestamp.as_deref().unwrap_or(""), what, Some(node_of(entry))));
        }
        latest.reverse();
        candidates.extend(latest);
        // The turn's own end: at its result, which stands at its last record. A quiet notice has no end.
        if support::terminal(run.status())
            && !support::quiet_notice(run)
            && let Some(ended) = run
                .result_entries()
                .first()
                .and_then(|e| e.timestamp.clone())
                .or_else(|| run.root_entries().last().and_then(|e| e.timestamp.clone()))
        {
            candidates.push(lead(format!("{}:end", run.id()), &ended, card::state_word(card::status_bucket(run.status()), locale), None));
        }
        for agent in support::flow_agents(run) {
            if !support::is_subagent(agent) || agent.source_block_id.is_some() {
                continue;
            }
            let node = support::node_id(run, &format!("agent:{}", agent.id()));
            let name = support::block_title(agent, locale);
            let definition = agent.identity.as_ref().and_then(|i| i.kind.as_deref()).and_then(|t| definitions.get(t));
            let ink = roles::role_ink(FlowRole::Subagent, definition.and_then(|d| d.color.as_deref()));
            let note = |id: String, stamp: &str, what: String| Candidate {
                stamp: stamp.into(),
                id,
                actor: Actor { role: FlowRole::Subagent, agent_id: Some(agent.id().into()) },
                who: name.clone(),
                ink,
                what,
                node_id: node.clone(),
            };
            if let Some(started) = agent.figures.as_ref().and_then(|f| f.started_at.as_deref()) {
                let identity = card::identity_line(agent, definition, locale);
                let words: Vec<String> = [Some(locale.get("graph.log.started")), identity].into_iter().flatten().collect();
                candidates.push(note(format!("{node}:start"), started, words.join(" · ")));
            }
            let mut calls = vec![];
            for entry in agent.entries().iter().rev() {
                if calls.len() >= limit {
                    break;
                }
                let Some(activity) = &entry.activity else { continue };
                if activity.kind == "turn" {
                    continue;
                }
                let words = if activity.summary.is_empty() { activity.tool_name.as_deref().unwrap_or("") } else { &activity.summary };
                let Some(what) = line(words) else { continue };
                calls.push(note(format!("{node}:{}", entry.id()), entry.timestamp.as_deref().unwrap_or(""), what));
            }
            calls.reverse();
            candidates.extend(calls);
            if support::terminal(agent.status())
                && let Some(finished) = agent.figures.as_ref().and_then(|f| f.ended_at.as_deref())
            {
                candidates.push(note(format!("{node}:end"), finished, card::state_word(card::status_bucket(agent.status()), locale)));
            }
        }
    }
    // Each in the order it was read, so two things of one second stay as they were recorded.
    let mut timed: Vec<(DateTimeOffset, usize, Candidate)> =
        candidates.into_iter().enumerate().filter_map(|(order, c)| time::parse(Some(&c.stamp)).map(|t| (t, order, c))).collect();
    timed.sort_by(|a, b| a.0.utc_ticks().cmp(&b.0.utc_ticks()).then(a.1.cmp(&b.1)));
    let skip = timed.len().saturating_sub(limit);
    timed
        .into_iter()
        .skip(skip)
        .map(|(time, _, c)| Row { id: c.id, time, actor: c.actor, who: c.who, ink: c.ink, what: c.what, node_id: c.node_id })
        .collect()
}

/// `Target`: the diagram block a row's click shows: its own, else the nearest drawn block above it, else its request's main session.
pub fn target(row: &Row, runs: &[MightyGraphRun], drawn: impl Fn(&str) -> bool) -> Option<String> {
    if drawn(&row.node_id) {
        return Some(row.node_id.clone());
    }
    let (start, end) = activity(runs);
    for index in (start..end).rev() {
        let run = &runs[index];
        // The role view draws the 실행 block only until the answer is there: then the main session is its result.
        if row.node_id == support::node_id(run, crate::layout::WORK_SUFFIX) {
            return ["result", "request"].iter().map(|s| support::node_id(run, s)).find(|id| drawn(id));
        }
        if let Some(node) = drawn_node(&row.node_id, run, &drawn) {
            return Some(node);
        }
        if support::flow_agents(run).iter().any(|a| support::node_id(run, &format!("agent:{}", a.id())) == row.node_id) {
            return None;
        }
    }
    None
}

/// `DrawnNode`: the block of `run` that stands for `node_id` on the diagram.
pub fn drawn_node(node_id: &str, run: &MightyGraphRun, drawn: impl Fn(&str) -> bool) -> Option<String> {
    if drawn(node_id) {
        return Some(node_id.into());
    }
    let agents = support::flow_agents(run);
    let own = agents.iter().find(|a| support::node_id(run, &format!("agent:{}", a.id())) == node_id)?;
    let mut above = own.parent_id().map(str::to_string);
    let mut depth = 0;
    while let Some(id) = above {
        if depth >= 64 {
            break;
        }
        depth += 1;
        let Some(parent) = agents.iter().find(|a| a.id() == id) else { break };
        let node = support::node_id(run, &format!("agent:{}", parent.id()));
        if drawn(&node) {
            return Some(node);
        }
        above = parent.parent_id().map(str::to_string);
    }
    [crate::layout::WORK_SUFFIX, "result", "request"].iter().map(|s| support::node_id(run, s)).find(|id| drawn(id))
}

/// `LeadWords`: what a record of the main session's says in the log.
pub fn lead_words(entry: &LogEntry, locale: &Locale) -> Option<String> {
    if let Some(activity) = &entry.activity {
        if activity.kind == "turn" {
            return None;
        }
        let tool = activity.tool_name.as_deref().unwrap_or("");
        if matches!(tool.to_lowercase().as_str(), "agent" | "task") {
            return None;
        }
        let words = if activity.summary.is_empty() { tool } else { &activity.summary };
        return line(&if tool == "AskUserQuestion" {
            locale.get_with("graph.log.question", &[("text", words)])
        } else {
            words.to_string()
        });
    }
    if entry.kind() != "system" || !entry.text().starts_with(&labels::compaction_title(locale)) {
        return None;
    }
    line(entry.text())
}

/// `LeadName`: the main session in words: the model that answered last, else the model the request was set to, else the agent's name.
pub fn lead_name(run: &MightyGraphRun, request: Option<&MightyGraphRun>, locale: &Locale) -> String {
    for candidate in std::iter::once(run).chain(request) {
        if let Some(model) =
            candidate.response_records.iter().flatten().flatten().rev().filter_map(|r| r.model.as_deref()).find(|m| !m.is_empty())
        {
            return usage_format::short_name(model, None, true);
        }
    }
    if let Some(label) =
        usage_format::versioned_node_label(run.node_model_label.as_deref().or(request.and_then(|r| r.node_model_label.as_deref())), locale)
    {
        return label;
    }
    labels::provider_label(run.provider().unwrap_or("claude")).to_string()
}

/// `Clock`: a row's time as the strip writes it, hours, minutes and seconds in the reader's own time zone (`zone`, else the local one).
pub fn clock(date: &DateTimeOffset, zone: Option<FixedOffset>) -> String {
    let utc = date.to_chrono();
    match zone {
        Some(offset) => offset.from_utc_datetime(&utc.naive_utc()).format("%H:%M:%S").to_string(),
        None => Local.from_utc_datetime(&utc.naive_utc()).format("%H:%M:%S").to_string(),
    }
}

// ── status ──

/// `Part(Role, Text)`: one part of the status line with the role whose ink it takes.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Part {
    pub role: FlowRole,
    pub text: String,
}

/// `Status(Lead, Subagents, Tasks)`: the strip's header, who is doing what right now.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Status {
    pub lead: String,
    pub subagents: Option<String>,
    pub tasks: Option<String>,
}

impl Status {
    /// `Parts`.
    pub fn parts(&self) -> Vec<Part> {
        let mut parts = vec![Part { role: FlowRole::Lead, text: self.lead.clone() }];
        if let Some(s) = &self.subagents {
            parts.push(Part { role: FlowRole::Subagent, text: s.clone() });
        }
        if let Some(t) = &self.tasks {
            parts.push(Part { role: FlowRole::Task, text: t.clone() });
        }
        parts
    }
    /// `Text`.
    pub fn text(&self) -> String {
        self.parts().into_iter().map(|p| p.text).collect::<Vec<_>>().join(" · ")
    }
}

/// `StatusOf`: the status of the pane's newest activity, none for a pane without a request.
pub fn status_of(runs: &[MightyGraphRun], locale: &Locale) -> Option<Status> {
    let newest = support::newest_request_index(runs)?;
    let request = &runs[newest];
    let followed = &runs[support::follow_run_index(runs).unwrap_or(newest)];
    let state = if !support::terminal(followed.status()) {
        card::state_word(card::status_bucket(followed.status()), locale)
    } else if notice::awaiting_notice(request, runs, locale).is_some() {
        notice::state_word(locale)
    } else {
        card::state_word(card::status_bucket(request.status()), locale)
    };
    let mut subagents = None;
    let mut tasks = None;
    if let Some(back) = lines::return_of(request, locale) {
        if back.subagents_running > 0 {
            subagents = Some(locale.get_with(
                "graph.log.subagentsRunning",
                &[("running", &back.subagents_running.to_string()), ("total", &back.subagents.to_string())],
            ));
        } else if back.subagents > 0 && back.tasks_running == 0 {
            subagents = Some(locale.get_with("graph.log.subagentsDone", &[("total", &back.subagents.to_string())]));
        }
        if back.tasks_running > 0 {
            tasks = Some(locale.get_with("graph.log.tasksRunning", &[("count", &back.tasks_running.to_string())]));
        }
    }
    Some(Status { lead: format!("{} {state}", lead_name(followed, Some(request), locale)), subagents, tasks })
}
