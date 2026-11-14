//! C# `PhaseModelRouting` (PhaseModelRouting.cs, macOS PhaseModelRouting.swift): which single-model knob of Claude, Codex, omc and
//! Ouroboros each phase row of Settings › 페이즈별 모델 sets, and how a row reads when its knobs agree (one value) or not (mixed).

use indexmap::IndexMap;
use mighty_core::model::PhaseModelsSnapshot;

/// C# `Phase`, in the order the screen shows the rows.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum Phase {
    Planning,
    Execution,
    Review,
    Subagents,
}

impl Phase {
    /// `phase.ToString().ToLowerInvariant()`: the rows' automation ids.
    pub fn id(self) -> &'static str {
        match self {
            Phase::Planning => "planning",
            Phase::Execution => "execution",
            Phase::Review => "review",
            Phase::Subagents => "subagents",
        }
    }
}

/// C# `RowState`: one value (`Uniform`) or `Mixed`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum RowState {
    Uniform(String),
    Mixed,
}

impl RowState {
    pub fn is_uniform(&self) -> bool {
        matches!(self, RowState::Uniform(_))
    }
    pub fn value(&self) -> Option<&str> {
        match self {
            RowState::Uniform(value) => Some(value),
            RowState::Mixed => None,
        }
    }
}

/// A knob of the snapshot as C# reads its non-null string (a missing one is the initializer's "default").
pub(crate) fn knob(value: &Option<String>) -> &str {
    value.as_deref().unwrap_or("default")
}

/// `OmcAgentPhase`.
pub fn omc_agent_phase(key: &str) -> Option<Phase> {
    match key {
        "planner" | "architect" | "critic" => Some(Phase::Planning),
        "executor" => Some(Phase::Execution),
        "codeReviewer" | "verifier" => Some(Phase::Review),
        _ => None,
    }
}

/// `OmcPhaseKeys`.
pub fn omc_phase_keys(phase: Phase) -> &'static [&'static str] {
    match phase {
        Phase::Planning => &["planner", "architect", "critic"],
        Phase::Execution => &["executor"],
        Phase::Review => &["codeReviewer", "verifier"],
        Phase::Subagents => &[],
    }
}

/// `OuroborosKeyPhase`.
pub fn ouroboros_key_phase(key: &str) -> Option<Phase> {
    match key {
        "clarification.default_model" => Some(Phase::Planning),
        "evaluation.semantic_model" | "consensus.judge_model" | "llm.qa_model" => Some(Phase::Review),
        _ => None,
    }
}

/// `OuroborosPhaseKeys`.
pub fn ouroboros_phase_keys(phase: Phase) -> &'static [&'static str] {
    match phase {
        Phase::Planning => &["clarification.default_model"],
        Phase::Review => &["evaluation.semantic_model", "consensus.judge_model", "llm.qa_model"],
        _ => &[],
    }
}

/// `ApplyClaudeRow`.
pub fn apply_claude_row(phase: Phase, value: &str, config: &PhaseModelsSnapshot) -> PhaseModelsSnapshot {
    let mut next = config.clone();
    match phase {
        Phase::Planning => next.claude_opus_alias = Some(value.into()),
        Phase::Execution => {
            next.claude_main = Some(value.into());
            next.claude_sonnet_alias = Some(value.into());
        }
        Phase::Subagents => next.claude_subagent_default = Some(value.into()),
        Phase::Review => {}
    }
    next
}

/// `ApplyCodexRow`.
pub fn apply_codex_row(phase: Phase, value: &str, config: &PhaseModelsSnapshot) -> PhaseModelsSnapshot {
    let mut next = config.clone();
    match phase {
        Phase::Review => next.codex_review_model = Some(value.into()),
        Phase::Subagents => next.codex_subagent_default = Some(value.into()),
        Phase::Planning | Phase::Execution => {}
    }
    next
}

/// `ApplyOmcRow`: every agent of the phase takes the value (one the map does not have is added).
pub fn apply_omc_row(phase: Phase, value: &str, agents: &IndexMap<String, String>) -> IndexMap<String, String> {
    let mut updated = agents.clone();
    for key in omc_phase_keys(phase) {
        updated.insert(key.to_string(), value.to_string());
    }
    updated
}

/// `ApplyOuroborosRow`.
pub fn apply_ouroboros_row(phase: Phase, value: &str, keys: &IndexMap<String, String>) -> IndexMap<String, String> {
    let mut updated = keys.clone();
    for key in ouroboros_phase_keys(phase) {
        updated.insert(key.to_string(), value.to_string());
    }
    updated
}

/// `ClaudeRowState`: None for a phase with no single-model Claude knob (review).
pub fn claude_row_state(phase: Phase, config: &PhaseModelsSnapshot) -> Option<RowState> {
    match phase {
        Phase::Planning => Some(row_state_from(&[knob(&config.claude_opus_alias)])),
        Phase::Execution => Some(row_state_from(&[knob(&config.claude_main), knob(&config.claude_sonnet_alias)])),
        Phase::Subagents => Some(row_state_from(&[knob(&config.claude_subagent_default)])),
        Phase::Review => None,
    }
}

/// `CodexRowState`: None for planning and execution.
pub fn codex_row_state(phase: Phase, config: &PhaseModelsSnapshot) -> Option<RowState> {
    match phase {
        Phase::Review => Some(row_state_from(&[knob(&config.codex_review_model)])),
        Phase::Subagents => Some(row_state_from(&[knob(&config.codex_subagent_default)])),
        Phase::Planning | Phase::Execution => None,
    }
}

/// `OmcRowState`: None when omc is not installed or has none of the phase's agents.
pub fn omc_row_state(phase: Phase, agents: Option<&IndexMap<String, String>>) -> Option<RowState> {
    let agents = agents?;
    let values: Vec<&str> = omc_phase_keys(phase).iter().filter_map(|k| agents.get(*k).map(String::as_str)).collect();
    (!values.is_empty()).then(|| row_state_from(&values))
}

/// `OuroborosRowState`.
pub fn ouroboros_row_state(phase: Phase, keys: Option<&IndexMap<String, String>>) -> Option<RowState> {
    let keys = keys?;
    let values: Vec<&str> = ouroboros_phase_keys(phase).iter().filter_map(|k| keys.get(*k).map(String::as_str)).collect();
    (!values.is_empty()).then(|| row_state_from(&values))
}

/// `RowStateFrom`.
fn row_state_from(values: &[&str]) -> RowState {
    let mut unique: Vec<&str> = Vec::new();
    for value in values {
        if !unique.contains(value) {
            unique.push(value);
        }
    }
    if unique.len() == 1 { RowState::Uniform(unique[0].to_string()) } else { RowState::Mixed }
}
