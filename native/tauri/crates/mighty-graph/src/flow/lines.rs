//! The delegate line on the branch from a block to the sub-agents it handed work to (C# `FlowDelegation`, MightyCode.Core/FlowLines.cs:9)
//! and the node at a request's result position before its result is there (`FlowReturn`, FlowLines.cs:108).

use std::collections::{HashMap, HashSet};

use mighty_core::graph::{MightyGraphAgent, MightyGraphRun};
use mighty_core::locale::Locale;
use mighty_core::model_label;

use crate::flow::card::answered_model;
use crate::flow::roles::{AgentDefinition, FlowRole};
use crate::support;
use crate::usage_format;

/// `FlowDelegation.Width`, `Height`: the line's node, as wide as its longest wording needs, one line tall.
pub const DELEGATE_WIDTH: f64 = 340.0;
pub const DELEGATE_HEIGHT: f64 = 24.0;
/// `FlowDelegation.MainKey`: the key of the block every request starts from, its 실행 block.
pub const MAIN_KEY: &str = "main";

/// `FlowDelegation.Part(Role, Text)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Part {
    pub role: FlowRole,
    pub text: String,
}

/// `FlowDelegation.Line(Count, Running, Head, Parts, Model, Progress)`: what the line says.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Line {
    pub count: usize,
    pub running: usize,
    pub head: String,
    pub parts: Vec<Part>,
    pub model: Option<String>,
    pub progress: Option<String>,
}

impl Line {
    /// `Text`: the whole line, as a screen reader reads it.
    pub fn text(&self) -> String {
        std::iter::once(self.head.clone())
            .chain(self.parts.iter().map(|p| p.text.clone()))
            .chain(self.model.iter().cloned())
            .chain(self.progress.iter().cloned())
            .collect::<Vec<_>>()
            .join(" · ")
    }
}

/// `FlowDelegation.Children`: the sub-agents block `key` of `run` delegated to and the commands it runs in the background, in launch order.
pub fn children<'a>(key: &str, run: &'a MightyGraphRun) -> Vec<&'a MightyGraphAgent> {
    if run.is_notice() {
        return vec![];
    }
    let agents = support::flow_agents(run);
    let ids: HashSet<&str> = agents.iter().map(|a| a.id()).collect();
    let mut order: Vec<usize> = (0..agents.len()).collect();
    order.sort_by_key(|&i| support::flow_order(run, i));
    order
        .into_iter()
        .map(|i| agents[i])
        .filter(|agent| {
            if !(support::is_subagent(agent) || agent.is_task()) || agent.source_block_id.is_some() {
                return false;
            }
            // The layout's own rule for what hangs off the main block: no parent, an unknown one, or itself.
            if key == MAIN_KEY {
                return agent.parent_id().is_none_or(|p| !ids.contains(p) || p == agent.id());
            }
            agent.parent_id() == Some(key) && agent.id() != key
        })
        .collect()
}

/// `FlowDelegation.Of`: the line for `children`, none when there are none.
pub fn line_of(children: &[&MightyGraphAgent], definitions: &HashMap<String, AgentDefinition>, locale: &Locale) -> Option<Line> {
    if children.is_empty() {
        return None;
    }
    let count = children.len();
    let running = children.iter().filter(|c| !support::layout_terminal(c.status())).count();
    let progress = (running > 0)
        .then(|| locale.get_with("graph.delegate.running", &[("running", &running.to_string()), ("total", &count.to_string())]));
    let tasks = children.iter().filter(|c| c.is_task()).count();
    let agents = count - tasks;
    if tasks == 0 {
        let head = if count == 1 {
            locale.get("graph.delegate.one")
        } else {
            locale.get_with("graph.delegate.many", &[("count", &count.to_string())])
        };
        return Some(Line { count, running, head, parts: vec![], model: model(children, definitions, locale), progress });
    }
    if agents == 0 {
        let head = if tasks == 1 {
            locale.get("graph.delegate.task")
        } else {
            locale.get_with("graph.delegate.tasks", &[("count", &tasks.to_string())])
        };
        return Some(Line { count, running, head, parts: vec![], model: None, progress });
    }
    Some(Line {
        count,
        running,
        head: locale.get_with("graph.delegate.parallel", &[("count", &count.to_string())]),
        parts: vec![
            Part { role: FlowRole::Subagent, text: locale.get_with("graph.delegate.partAgents", &[("count", &agents.to_string())]) },
            Part { role: FlowRole::Task, text: locale.get_with("graph.delegate.partTasks", &[("count", &tasks.to_string())]) },
        ],
        model: None,
        progress,
    })
}

/// `FlowDelegation.Model`: the model the sub-agents share, or how many they are on.
pub fn model(children: &[&MightyGraphAgent], definitions: &HashMap<String, AgentDefinition>, locale: &Locale) -> Option<String> {
    let mut named: Vec<String> = vec![];
    let mut asked: Vec<String> = vec![];
    for agent in children {
        let identity = agent.identity.as_ref();
        if let Some(id) = answered_model(agent).or(identity.and_then(|i| i.resolved_model.as_deref())) {
            let label = usage_format::short_name(id, None, true);
            if !named.contains(&label) {
                named.push(label);
            }
        } else if let Some(alias) = identity
            .and_then(|i| i.requested_model.as_deref())
            .or_else(|| identity.and_then(|i| i.kind.as_deref()).and_then(|t| definitions.get(t)).and_then(|d| d.model.as_deref()))
            .filter(|a| *a != "inherit")
        {
            let label = model_label::text(alias, None, None);
            if !asked.contains(&label) {
                asked.push(label);
            }
        }
    }
    let unmatched: Vec<&String> =
        asked.iter().filter(|family| !named.iter().any(|m| m == *family || m.starts_with(&format!("{family} ")))).collect();
    match named.len() + unmatched.len() {
        0 => None,
        1 => Some(named.first().cloned().unwrap_or_else(|| format!("{} {}", unmatched[0], locale.get("graph.nodeModel.requestedSuffix")))),
        count => Some(locale.get_with("graph.delegate.models", &[("count", &count.to_string())])),
    }
}

/// `FlowReturn.Width`, `Height`.
pub const RETURN_WIDTH: f64 = 340.0;
pub const RETURN_HEIGHT: f64 = 34.0;

/// `FlowReturn.State`: the run's sub-agents and its background commands, and how many have not ended.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ReturnState {
    pub total: usize,
    pub running: usize,
    pub subagents: usize,
    pub subagents_running: usize,
    pub tasks: usize,
    pub tasks_running: usize,
    pub text: String,
}

impl ReturnState {
    /// `Waiting`.
    pub fn waiting(&self) -> bool {
        self.running > 0
    }
}

/// `FlowReturn.Of`: the state of `run`'s way back; none for a run that delegated nothing and for a notice run.
pub fn return_of(run: &MightyGraphRun, locale: &Locale) -> Option<ReturnState> {
    if run.is_notice() {
        return None;
    }
    let blocks: Vec<&MightyGraphAgent> =
        support::flow_agents(run).into_iter().filter(|a| (support::is_subagent(a) || a.is_task()) && a.source_block_id.is_none()).collect();
    if blocks.is_empty() {
        return None;
    }
    let live: Vec<&&MightyGraphAgent> = blocks.iter().filter(|a| !support::layout_terminal(a.status())).collect();
    let tasks = blocks.iter().filter(|a| a.is_task()).count();
    let tasks_running = live.iter().filter(|a| a.is_task()).count();
    let counts = [("running", live.len().to_string()), ("total", blocks.len().to_string())];
    let counts: Vec<(&str, &str)> = counts.iter().map(|(k, v)| (*k, v.as_str())).collect();
    let text = if live.is_empty() {
        locale.get("graph.return.reviewing")
    } else if tasks > 0 {
        locale.get_with("graph.return.parallel", &counts)
    } else {
        locale.get_with("graph.return.waiting", &counts)
    };
    Some(ReturnState {
        total: blocks.len(),
        running: live.len(),
        subagents: blocks.len() - tasks,
        subagents_running: live.len() - tasks_running,
        tasks,
        tasks_running,
        text,
    })
}
