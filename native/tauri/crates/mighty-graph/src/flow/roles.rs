//! Who works in a flow diagram (C# `FlowRole`, `FlowInk`, `FlowMark`, `FlowIdentity`, `FlowRoles`, MightyCode.Core/FlowRoles.cs): the main
//! session, the sub-agents it delegates to, the commands it runs in the background and the advisor. One ink per role.

use std::collections::HashSet;

use mighty_core::graph::{MightyGraphAgent, MightyGraphRun};
use mighty_core::locale::Locale;
use mighty_core::model_label;

use crate::labels;
use crate::node::clean;
use crate::support;

/// `FlowRole`.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum FlowRole {
    /// The pane's own agent: a request's 실행 block.
    Lead,
    /// An agent another agent started.
    Subagent,
    /// A shell command a Claude session runs in the background: a task block.
    Task,
    /// The model a Claude session asks for advice.
    Advisor,
}

/// `FlowInk`: one of the design palette's text-safe inks.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum FlowInk {
    Task,
    Agent,
    Steer,
    Accent,
    Done,
    Err,
    Wait,
    Question,
    Neutral,
}

/// `FlowMark`: a small mark on a sub-agent's header.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum FlowMark {
    Background,
    Fork,
    Worktree,
}

/// `FlowIdentity(Role, Ink, Type, TypeChip, ModelLabel, Effort, Marks, Summary)`: what a flow block's header says about who it is.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct FlowIdentity {
    pub role: FlowRole,
    pub ink: FlowInk,
    pub kind: Option<String>,
    pub type_chip: Option<String>,
    pub model_label: Option<String>,
    pub effort: Option<String>,
    pub marks: Vec<FlowMark>,
    pub summary: String,
}

/// `AgentDefinition(Name, Color, Model)`: a custom sub-agent as its definition file's frontmatter says it.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct AgentDefinition {
    pub name: String,
    pub color: Option<String>,
    pub model: Option<String>,
}

/// `FlowRoles.Ink(role)`.
pub fn ink(role: FlowRole) -> FlowInk {
    match role {
        FlowRole::Lead => FlowInk::Task,
        FlowRole::Subagent => FlowInk::Agent,
        FlowRole::Task => FlowInk::Neutral,
        FlowRole::Advisor => FlowInk::Steer,
    }
}

/// `FlowRoles.Token`: the design token (the page's CSS variable) an ink draws with.
pub fn token(ink: FlowInk) -> &'static str {
    match ink {
        FlowInk::Neutral => "ink2",
        FlowInk::Task => "taskText",
        FlowInk::Agent => "agentText",
        FlowInk::Steer => "steerText",
        FlowInk::Accent => "accent",
        FlowInk::Done => "doneText",
        FlowInk::Err => "errText",
        FlowInk::Wait => "waitText",
        FlowInk::Question => "questionText",
    }
}

/// `FlowRoles.AgentColors`.
pub const AGENT_COLORS: [&str; 8] = ["red", "blue", "green", "yellow", "purple", "orange", "pink", "cyan"];

/// `FlowRoles.Ink(color)`: a custom agent's colour as a palette ink; none for a colour Claude Code does not have.
pub fn color_ink(color: Option<&str>) -> Option<FlowInk> {
    Some(match color?.trim_matches([' ', '\t']).to_lowercase().as_str() {
        "red" => FlowInk::Err,
        "blue" => FlowInk::Accent,
        "green" => FlowInk::Done,
        "yellow" => FlowInk::Wait,
        "purple" => FlowInk::Agent,
        "orange" => FlowInk::Question,
        "pink" => FlowInk::Steer,
        "cyan" => FlowInk::Task,
        _ => return None,
    })
}

/// `FlowRoles.Ink(role, color)`: a custom agent's own colour takes the place of the sub-agent ink.
pub fn role_ink(role: FlowRole, color: Option<&str>) -> FlowInk {
    if role == FlowRole::Subagent { color_ink(color).unwrap_or(ink(role)) } else { ink(role) }
}

/// `FlowRoles.RoleName`.
pub fn role_name(role: FlowRole, locale: &Locale) -> String {
    locale.get(match role {
        FlowRole::Lead => "graph.identity.role.lead",
        FlowRole::Subagent => "graph.block.agent",
        FlowRole::Task => "graph.identity.role.task",
        FlowRole::Advisor => "graph.identity.role.advisor",
    })
}

/// `FlowRoles.Help`.
pub fn help(mark: FlowMark, locale: &Locale) -> String {
    locale.get(match mark {
        FlowMark::Background => "graph.identity.mark.background",
        FlowMark::Fork => "graph.identity.mark.fork",
        FlowMark::Worktree => "graph.identity.mark.worktree",
    })
}

/// `FlowRoles.Word`.
pub fn word(mark: FlowMark, locale: &Locale) -> String {
    locale.get(match mark {
        FlowMark::Background => "graph.identity.word.background",
        FlowMark::Fork => "graph.identity.word.fork",
        FlowMark::Worktree => "graph.identity.word.worktree",
    })
}

/// `FlowRoles.EffortName`: a level as the composer names it; none for "default".
pub fn effort_name(effort: Option<&str>) -> Option<&'static str> {
    Some(match effort? {
        "off" => "Off",
        "minimal" => "Minimal",
        "low" => "Low",
        "medium" => "Medium",
        "high" => "High",
        "xhigh" => "XHigh",
        "max" => "Max",
        _ => return None,
    })
}

/// `FlowRoles.TypeName`: a plugin's agent (`plugin:agent`) by its own name.
pub fn type_name(kind: &str) -> &str {
    match kind.rfind(':') {
        Some(colon) if colon + 1 < kind.len() => &kind[colon + 1..],
        _ => kind,
    }
}

/// `GraphAgentIdentity.IsFork`.
pub fn is_fork(agent: &MightyGraphAgent) -> bool {
    agent.identity.as_ref().and_then(|i| i.kind.as_deref()) == Some("fork")
}

/// `FlowRoles.Marks`: a sub-agent block's marks, in a fixed order.
pub fn marks(agent: &MightyGraphAgent) -> Vec<FlowMark> {
    let mut marks = vec![];
    if agent.background == Some(true) {
        marks.push(FlowMark::Background);
    }
    if is_fork(agent) {
        marks.push(FlowMark::Fork);
    }
    if agent.identity.as_ref().and_then(|i| i.isolation.as_deref()) == Some("worktree") {
        marks.push(FlowMark::Worktree);
    }
    marks
}

/// `FlowRoles.Lead`: the main session's identity on a run's 실행 block.
pub fn lead(run: &MightyGraphRun, locale: &Locale) -> FlowIdentity {
    let effort = effort_name(run.configured_effort.as_deref());
    let mut parts = vec![role_name(FlowRole::Lead, locale)];
    if let Some(effort) = effort {
        parts.push(locale.get_with("composer.effort.menu", &[("effort", effort)]));
    }
    FlowIdentity {
        role: FlowRole::Lead,
        ink: ink(FlowRole::Lead),
        kind: None,
        type_chip: None,
        model_label: None,
        effort: effort.map(str::to_string),
        marks: vec![],
        summary: parts.join(" · "),
    }
}

/// `FlowRoles.Subagent`: a sub-agent block's identity; none for a block that is no sub-agent. `definition` is the custom agent its type
/// names: its colour takes the sub-agent ink's place and its model stands in for a call that asked for none.
pub fn subagent(agent: &MightyGraphAgent, definition: Option<&AgentDefinition>, locale: &Locale) -> Option<FlowIdentity> {
    if !support::is_subagent(agent) {
        return None;
    }
    let identity = agent.identity.as_ref();
    let kind = identity.and_then(|i| i.kind.clone());
    let fork = is_fork(agent);
    let marks = marks(agent);
    let title = support::block_title(agent, locale);
    let chip = kind.as_deref().and_then(|t| {
        let name = type_name(t);
        (!(fork || t == title || name == title)).then(|| name.to_string())
    });
    let requested = identity.and_then(|i| i.requested_model.as_deref()).or(definition.and_then(|d| d.model.as_deref()));
    let resolved = identity.and_then(|i| i.resolved_model.as_deref());
    let model = labels::agent_model_label(resolved, requested, locale);
    let mut parts = vec![role_name(FlowRole::Subagent, locale)];
    if let Some(t) = &kind
        && !fork
    {
        parts.push(t.clone());
    }
    if let Some(resolved) = resolved {
        parts.push(model_label::text(resolved, None, None));
    } else if let Some(requested) = requested.filter(|r| *r != "inherit") {
        parts.push(locale.get_with("graph.identity.requestedModel", &[("model", &model_label::text(requested, None, None))]));
    }
    parts.extend(marks.iter().map(|m| word(*m, locale)));
    Some(FlowIdentity {
        role: FlowRole::Subagent,
        ink: role_ink(FlowRole::Subagent, definition.and_then(|d| d.color.as_deref())),
        kind,
        type_chip: chip,
        model_label: model,
        effort: None,
        marks,
        summary: parts.join(" · "),
    })
}

/// `FlowCard.LineBytes`.
pub const LINE_BYTES: usize = 240;
const BREAKS: [char; 7] = ['\n', '\r', '\u{b}', '\u{c}', '\u{85}', '\u{2028}', '\u{2029}'];

/// `FlowCard.Line`: a card's one line, cleaned; none when nothing is left.
pub fn card_line(raw: &str) -> Option<String> {
    Some(clean(raw, LINE_BYTES, true)).filter(|c| !c.is_empty())
}

/// `FlowCard.FirstLine`: the first line that holds more than spaces and tabs.
pub fn first_line(text: &str) -> &str {
    text.split(BREAKS).find(|l| !l.trim_matches([' ', '\t']).is_empty()).unwrap_or("")
}

/// `FlowCard.LastLine`: the last line that holds more than spaces and tabs.
pub fn last_line(text: &str) -> &str {
    text.split(BREAKS).rfind(|l| !l.trim_matches([' ', '\t']).is_empty()).unwrap_or("")
}

/// `FlowRoles.Task`: a background command's identity, the first line of its command in words; none for a block that is no command.
pub fn task(agent: &MightyGraphAgent, locale: &Locale) -> Option<FlowIdentity> {
    if !agent.is_task() {
        return None;
    }
    let marks = marks(agent);
    let mut parts = vec![role_name(FlowRole::Task, locale)];
    parts.extend(marks.iter().map(|m| word(*m, locale)));
    if let Some(command) = card_line(first_line(support::shown_input(agent))) {
        parts.push(command);
    }
    Some(FlowIdentity {
        role: FlowRole::Task,
        ink: ink(FlowRole::Task),
        kind: None,
        type_chip: None,
        model_label: None,
        effort: None,
        marks,
        summary: parts.join(" · "),
    })
}

/// `FlowRoles.Types`: the types the blocks of `runs` name, for looking their definitions up.
pub fn types(runs: &[MightyGraphRun]) -> HashSet<String> {
    runs.iter().flat_map(|r| support::flow_agents(r)).filter_map(|a| a.identity.as_ref().and_then(|i| i.kind.clone())).collect()
}

// ── header room ──

/// `FlowRoles.HeaderFit(TypeWidth, ShowsEffort, ShowsMarks)`.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct HeaderFit {
    pub type_width: Option<f64>,
    pub shows_effort: bool,
    pub shows_marks: bool,
}

impl HeaderFit {
    pub const NONE: HeaderFit = HeaderFit { type_width: None, shows_effort: false, shows_marks: false };
}

/// `FlowRoles.EstimatedWidth`: a wide (Hangul, kana, Han) character a full em, any other a little over half.
pub fn estimated_width(text: &str, size: f64, monospaced: bool) -> f64 {
    text.chars()
        .map(|c| {
            let v = c as u32;
            let wide = (0x1100..=0x11FF).contains(&v)
                || (0x2E80..=0xA4CF).contains(&v)
                || (0xAC00..=0xD7A3).contains(&v)
                || (0xF900..=0xFAFF).contains(&v)
                || (0xFF00..=0xFFEF).contains(&v);
            size * if wide {
                1.0
            } else if monospaced {
                0.61
            } else {
                0.56
            }
        })
        .sum()
}

/// `DesignMetrics.Spacing.Sm`, `DesignMetrics.Inset.GraphBlockBodyH`.
pub const SPACING_SM: f64 = 6.0;
pub const GRAPH_BLOCK_BODY_H: f64 = 8.0;
pub const TITLE_FLOOR: f64 = 72.0;
pub const TYPE_FLOOR: f64 = 40.0;
pub const TYPE_CEILING: f64 = 110.0;
pub const MARK_WIDTH: f64 = 14.0;
pub const CHIP_PADDING: f64 = 2.0 * SPACING_SM;

/// `FlowRoles.FitHeader`: what fits in a block header `width` wide — the marks, then the effort, then the type chip.
pub fn fit_header(
    width: f64,
    title: &str,
    status: Option<&str>,
    capsule: Option<&str>,
    buttons: i32,
    identity: &FlowIdentity,
) -> HeaderFit {
    let gap = SPACING_SM;
    let mut used = 2.0 * GRAPH_BLOCK_BODY_H + 16.0 + gap;
    used += TITLE_FLOOR.min(estimated_width(title, 12.0, false)) + gap;
    if let Some(status) = status {
        used += 18.0 + gap + estimated_width(status, 10.0, false) + CHIP_PADDING + gap;
    }
    if let Some(capsule) = capsule {
        used += estimated_width(capsule, 10.0, true) + CHIP_PADDING + gap;
    }
    used += buttons as f64 * (16.0 + gap);
    let mut free = width - used;
    let mut fit = HeaderFit::NONE;
    if !identity.marks.is_empty() {
        let marks = identity.marks.len() as f64 * MARK_WIDTH + gap;
        if free >= marks {
            fit.shows_marks = true;
            free -= marks;
        }
    }
    if let Some(effort) = &identity.effort {
        let chip = MARK_WIDTH + estimated_width(effort, 10.0, false) + gap;
        if free >= chip {
            fit.shows_effort = true;
            free -= chip;
        }
    }
    if let Some(kind) = &identity.type_chip
        && free >= TYPE_FLOOR + gap
    {
        let wanted = (estimated_width(kind, 10.0, false) * 1.15 + CHIP_PADDING + 4.0).ceil();
        fit.type_width = Some(TYPE_FLOOR.max(TYPE_CEILING.min((free - gap).min(wanted))));
    }
    fit
}
