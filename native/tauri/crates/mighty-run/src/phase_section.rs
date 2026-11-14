//! C# `PhaseModelSection` (PhaseModelSection.cs): what Settings › 페이즈별 모델 draws and how its rows read and write — the four
//! phase rows (planning, execution, review, sub-agents), each setting every single-model knob of Claude, Codex, omc and Ouroboros the
//! phase owns (mixed when they differ), and the tools' detail rows, each one knob. omc's block shows only while installed_plugins.json
//! shows omc installed, Ouroboros's only while `~/.ouroboros/config.yaml` is there.
//!
//! The screen itself is W6's NativeNode form; [`form`] is its typed data (C# `FillPhaseModelsSection`'s rows), and the actions it calls
//! are [`crate::phase_models::apply_row`], [`crate::phase_models::set_effort`], [`apply_knob`] with [`save_tools`], and
//! [`register`] / [`unregister`].

use std::path::Path;

use indexmap::IndexMap;
use mighty_core::model::{AppSnapshot, ModelDefaultsConfig, PhaseModelsSnapshot, ProviderModeDefaults, RegisteredModelEntry};
use mighty_core::wire;

use crate::model::ModelCatalog;
use crate::model_settings::{ModelSettingsFileStore, OmcAgentCatalog};
use crate::phase_models::{self, PhaseModelVersion};
use crate::phase_routing::{self, Phase, RowState, knob};
use crate::{providers, strings};

/// `MixedSentinel`: the value a mixed row shows; never one the user can choose.
pub const MIXED_SENTINEL: &str = "__mixed__";

pub const CLAUDE_TOOL: &str = "claude";
pub const CODEX_TOOL: &str = "codex";
pub const OMC_TOOL: &str = "omc";
pub const OUROBOROS_TOOL: &str = "ouroboros";

/// `Phases`: the rows in the Mac's order.
pub const PHASES: [Phase; 4] = [Phase::Planning, Phase::Execution, Phase::Review, Phase::Subagents];

/// C# `PhaseModelTools(OmcAgents, OuroborosKeys, Error, OmcDefaults)`: the two outside tools' values now (None: not installed), what
/// could not be read, and the frontmatter defaults omc's agents name.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct PhaseModelTools {
    pub omc_agents: Option<IndexMap<String, String>>,
    pub ouroboros_keys: Option<IndexMap<String, String>>,
    pub error: Option<String>,
    pub omc_defaults: Option<IndexMap<String, String>>,
}

/// C# `PhaseModelEdit(Config, OmcAgents, OuroborosKeys)`: the new values one change made.
#[derive(Debug, Clone, PartialEq)]
pub struct PhaseModelEdit {
    pub config: PhaseModelsSnapshot,
    pub omc_agents: Option<IndexMap<String, String>>,
    pub ouroboros_keys: Option<IndexMap<String, String>>,
}

/// C# `PhaseModelSummaryRow(Phase, Label, Value, Mixed)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PhaseModelSummaryRow {
    pub phase: Phase,
    pub label: String,
    pub value: String,
    pub mixed: bool,
}

/// C# `PhaseModelKnobRow(KnobId, Label, Value, IsEffort)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PhaseModelKnobRow {
    pub knob_id: String,
    pub label: String,
    pub value: String,
    pub is_effort: bool,
}

/// C# `PhaseModelToolBlock(Tool, Label, Knobs)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PhaseModelToolBlock {
    pub tool: String,
    pub label: String,
    pub knobs: Vec<PhaseModelKnobRow>,
}

/// `SectionTitle`.
pub fn section_title() -> String {
    strings::get("settings.phaseModels.sectionTitle")
}
/// `Description`.
pub fn description() -> String {
    strings::get("settings.phaseModels.description")
}
/// `DefaultOption`.
pub fn default_option() -> String {
    strings::get("settings.phaseModels.defaultOption")
}
/// `MixedLabel`.
pub fn mixed_label() -> String {
    strings::get("settings.phaseModels.mixed")
}

/// `PhaseLabel`.
pub fn phase_label(phase: Phase) -> String {
    strings::get(match phase {
        Phase::Planning => "settings.phaseModels.phase.planning",
        Phase::Execution => "settings.phaseModels.phase.execution",
        Phase::Review => "settings.phaseModels.phase.review",
        Phase::Subagents => "settings.phaseModels.phase.subagents",
    })
}

/// `ToolLabel`.
pub fn tool_label(tool: &str) -> String {
    match tool {
        OMC_TOOL => strings::get("settings.phaseModels.tool.omc"),
        OUROBOROS_TOOL => strings::get("settings.phaseModels.tool.ouroboros"),
        _ => providers::name(tool).to_string(),
    }
}

/// `FileError(path)`.
pub fn file_error(path: &Path) -> String {
    strings::get_with("settings.phaseModels.fileError", &[("path", &path.display().to_string())])
}

/// `LoadTools(home)`: the two outside tools' values now — omc's agents from its install with the values config.jsonc gives (a
/// frontmatter model is a shown default only), Ouroboros's `*_model` keys. What cannot be read says so in `error`; the file is left.
pub fn load_tools(home: &Path) -> PhaseModelTools {
    let store = ModelSettingsFileStore::new(home);
    let defaults = OmcAgentCatalog::new(home).scan();
    let mut error = None;
    let omc = defaults.as_ref().map(|defaults| {
        let mut omc: IndexMap<String, String> = defaults.keys().map(|k| (k.clone(), "default".to_string())).collect();
        match store.load_omc_agents() {
            Ok(saved) => {
                for (key, model) in saved.unwrap_or_default() {
                    if let Some(value) = omc.get_mut(&key) {
                        *value = model;
                    }
                }
            }
            Err(_) => error = Some(file_error(&store.omc_config_path())),
        }
        omc
    });
    let ouroboros = match store.load_ouroboros_keys() {
        Ok(keys) => keys,
        Err(_) => {
            error.get_or_insert_with(|| file_error(&store.ouroboros_config_path()));
            None
        }
    };
    PhaseModelTools { omc_agents: omc, ouroboros_keys: ouroboros, error, omc_defaults: defaults }
}

/// `SmokeFixtureTools`: the values the smoke shows (both tools as if installed) without reading or writing any user's file.
pub fn smoke_fixture_tools() -> PhaseModelTools {
    let map = |pairs: &[(&str, &str)]| pairs.iter().map(|(k, v)| (k.to_string(), v.to_string())).collect::<IndexMap<_, _>>();
    PhaseModelTools {
        omc_agents: Some(map(&[("planner", "default"), ("executor", "default"), ("codeReviewer", "default"), ("verifier", "default")])),
        ouroboros_keys: Some(map(&[
            ("clarification.default_model", "default"),
            ("evaluation.semantic_model", "default"),
            ("consensus.judge_model", "default"),
            ("llm.qa_model", "default"),
        ])),
        error: None,
        omc_defaults: Some(map(&[("planner", "opus"), ("executor", "sonnet"), ("codeReviewer", "opus"), ("verifier", "sonnet")])),
    }
}

/// `OmcAgentLabel`: an omc detail row's name, with the default model its install names.
pub fn omc_agent_label(key: &str, defaults: Option<&IndexMap<String, String>>) -> String {
    match defaults.and_then(|d| d.get(key)).filter(|m| *m != "default") {
        Some(model) => strings::get_with("settings.phaseModels.omcAgentDefault", &[("agent", key), ("model", model)]),
        None => key.to_string(),
    }
}

/// `SummaryRows`: the four phase rows — the one value their knobs share, else [`MIXED_SENTINEL`].
pub fn summary_rows(config: &PhaseModelsSnapshot, tools: &PhaseModelTools) -> Vec<PhaseModelSummaryRow> {
    PHASES
        .iter()
        .map(|phase| {
            let state = summary_row_state(*phase, config, tools);
            PhaseModelSummaryRow {
                phase: *phase,
                label: phase_label(*phase),
                value: state.as_ref().and_then(RowState::value).unwrap_or(MIXED_SENTINEL).to_string(),
                mixed: state.is_some_and(|s| !s.is_uniform()),
            }
        })
        .collect()
}

/// `SummaryRowState`: every single-model knob of the four tools the phase owns as one state.
pub fn summary_row_state(phase: Phase, config: &PhaseModelsSnapshot, tools: &PhaseModelTools) -> Option<RowState> {
    let states: Vec<RowState> = [
        phase_routing::claude_row_state(phase, config),
        phase_routing::codex_row_state(phase, config),
        phase_routing::omc_row_state(phase, tools.omc_agents.as_ref()),
        phase_routing::ouroboros_row_state(phase, tools.ouroboros_keys.as_ref()),
    ]
    .into_iter()
    .flatten()
    .collect();
    if states.is_empty() {
        return None;
    }
    if states.iter().any(|s| !s.is_uniform()) {
        return Some(RowState::Mixed);
    }
    let mut values: Vec<&str> = Vec::new();
    for value in states.iter().filter_map(RowState::value) {
        if !values.contains(&value) {
            values.push(value);
        }
    }
    Some(if values.len() == 1 { RowState::Uniform(values[0].to_string()) } else { RowState::Mixed })
}

fn knob_row(id: &str, label_key: &str, value: &Option<String>, is_effort: bool) -> PhaseModelKnobRow {
    PhaseModelKnobRow { knob_id: id.into(), label: strings::get(label_key), value: knob(value).to_string(), is_effort }
}

/// `ToolBlocks`: each tool's detail rows; a tool that is not installed is left out.
pub fn tool_blocks(config: &PhaseModelsSnapshot, tools: &PhaseModelTools) -> Vec<PhaseModelToolBlock> {
    let mut blocks = vec![
        PhaseModelToolBlock {
            tool: CLAUDE_TOOL.into(),
            label: tool_label(CLAUDE_TOOL),
            knobs: vec![
                knob_row("claude.claudeMain", "settings.phaseModels.knob.claudeMain", &config.claude_main, false),
                knob_row("claude.claudeOpusAlias", "settings.phaseModels.knob.claudeOpusAlias", &config.claude_opus_alias, false),
                knob_row("claude.claudeSonnetAlias", "settings.phaseModels.knob.claudeSonnetAlias", &config.claude_sonnet_alias, false),
                knob_row("claude.claudeHaikuAlias", "settings.phaseModels.knob.claudeHaikuAlias", &config.claude_haiku_alias, false),
                knob_row(
                    "claude.claudeSubagentDefault",
                    "settings.phaseModels.knob.claudeSubagent",
                    &config.claude_subagent_default,
                    false,
                ),
            ],
        },
        PhaseModelToolBlock {
            tool: CODEX_TOOL.into(),
            label: tool_label(CODEX_TOOL),
            knobs: vec![
                knob_row("codex.codexReviewModel", "settings.phaseModels.knob.codexReview", &config.codex_review_model, false),
                knob_row("codex.codexSubagentDefault", "settings.phaseModels.knob.codexSubagent", &config.codex_subagent_default, false),
                knob_row(
                    "codex.codexPlanModeReasoningEffort",
                    "settings.phaseModels.knob.codexPlanEffort",
                    &config.codex_plan_mode_reasoning_effort,
                    true,
                ),
            ],
        },
    ];
    let sorted = |map: &IndexMap<String, String>| {
        let mut pairs: Vec<(String, String)> = map.iter().map(|(k, v)| (k.clone(), v.clone())).collect();
        pairs.sort_by(|a, b| a.0.cmp(&b.0));
        pairs
    };
    if let Some(agents) = &tools.omc_agents {
        blocks.push(PhaseModelToolBlock {
            tool: OMC_TOOL.into(),
            label: tool_label(OMC_TOOL),
            knobs: sorted(agents)
                .into_iter()
                .map(|(key, value)| PhaseModelKnobRow {
                    knob_id: format!("omc.agent.{key}"),
                    label: omc_agent_label(&key, tools.omc_defaults.as_ref()),
                    value,
                    is_effort: false,
                })
                .collect(),
        });
    }
    if let Some(keys) = &tools.ouroboros_keys {
        blocks.push(PhaseModelToolBlock {
            tool: OUROBOROS_TOOL.into(),
            label: tool_label(OUROBOROS_TOOL),
            knobs: sorted(keys)
                .into_iter()
                .map(|(key, value)| PhaseModelKnobRow { knob_id: format!("ouroboros.{key}"), label: key, value, is_effort: false })
                .collect(),
        });
    }
    blocks
}

/// `KeepKnown`: an agent the install does not have, or a key the file does not have, is never made.
fn keep_known(updated: IndexMap<String, String>, known: &IndexMap<String, String>) -> IndexMap<String, String> {
    updated.into_iter().filter(|(k, _)| known.contains_key(k)).collect()
}

/// `ApplyPhaseRow`: one phase row applied to all four tools; a tool that is not installed is left alone.
pub fn apply_phase_row(phase: Phase, value: &str, config: &PhaseModelsSnapshot, tools: &PhaseModelTools) -> PhaseModelEdit {
    let updated = phase_routing::apply_codex_row(phase, value, &phase_routing::apply_claude_row(phase, value, config));
    PhaseModelEdit {
        config: updated,
        omc_agents: tools.omc_agents.as_ref().map(|a| keep_known(phase_routing::apply_omc_row(phase, value, a), a)),
        ouroboros_keys: tools.ouroboros_keys.as_ref().map(|k| keep_known(phase_routing::apply_ouroboros_row(phase, value, k), k)),
    }
}

/// `ApplyKnob`: one detail row changed; `knob_id` is the one [`tool_blocks`] gave.
pub fn apply_knob(knob_id: &str, value: &str, config: &PhaseModelsSnapshot, tools: &PhaseModelTools) -> PhaseModelEdit {
    if let Some(agent) = knob_id.strip_prefix("omc.agent.") {
        let Some(agents) = &tools.omc_agents else {
            return PhaseModelEdit { config: config.clone(), omc_agents: None, ouroboros_keys: tools.ouroboros_keys.clone() };
        };
        let mut agents = agents.clone();
        agents.insert(agent.to_string(), value.to_string());
        return PhaseModelEdit { config: config.clone(), omc_agents: Some(agents), ouroboros_keys: tools.ouroboros_keys.clone() };
    }
    if let Some(key) = knob_id.strip_prefix("ouroboros.") {
        let Some(keys) = &tools.ouroboros_keys else {
            return PhaseModelEdit { config: config.clone(), omc_agents: tools.omc_agents.clone(), ouroboros_keys: None };
        };
        let mut keys = keys.clone();
        keys.insert(key.to_string(), value.to_string());
        return PhaseModelEdit { config: config.clone(), omc_agents: tools.omc_agents.clone(), ouroboros_keys: Some(keys) };
    }
    let mut updated = config.clone();
    let value = Some(value.to_string());
    match knob_id {
        "claude.claudeMain" => updated.claude_main = value,
        "claude.claudeOpusAlias" => updated.claude_opus_alias = value,
        "claude.claudeSonnetAlias" => updated.claude_sonnet_alias = value,
        "claude.claudeHaikuAlias" => updated.claude_haiku_alias = value,
        "claude.claudeSubagentDefault" => updated.claude_subagent_default = value,
        "codex.codexReviewModel" => updated.codex_review_model = value,
        "codex.codexSubagentDefault" => updated.codex_subagent_default = value,
        "codex.codexPlanModeReasoningEffort" => updated.codex_plan_mode_reasoning_effort = value,
        _ => {}
    }
    PhaseModelEdit { config: updated, omc_agents: tools.omc_agents.clone(), ouroboros_keys: tools.ouroboros_keys.clone() }
}

/// `SaveTools(before, edit, home)`: only the outside values this change really changed are written, then both files read again. A
/// file that cannot be written says so in `error` and is left as it was.
pub fn save_tools(before: &PhaseModelTools, edit: &PhaseModelEdit, home: &Path) -> PhaseModelTools {
    let store = ModelSettingsFileStore::new(home);
    let mut error = None;
    let agents = changed(before.omc_agents.as_ref(), edit.omc_agents.as_ref());
    if !agents.is_empty() && store.save_omc_agents(&agents).is_err() {
        error = Some(file_error(&store.omc_config_path()));
    }
    let keys = changed(before.ouroboros_keys.as_ref(), edit.ouroboros_keys.as_ref());
    if !keys.is_empty() && store.save_ouroboros_keys(&keys).is_err() {
        error.get_or_insert_with(|| file_error(&store.ouroboros_config_path()));
    }
    let reloaded = load_tools(home);
    PhaseModelTools { error: error.or(reloaded.error.clone()), ..reloaded }
}

/// `Changed(before, after)`: the keys of `after` whose value differs from `before`'s.
pub fn changed(before: Option<&IndexMap<String, String>>, after: Option<&IndexMap<String, String>>) -> IndexMap<String, String> {
    let Some(after) = after else { return IndexMap::new() };
    after.iter().filter(|(k, v)| before.is_none_or(|b| b.get(*k) != Some(*v))).map(|(k, v)| (k.clone(), v.clone())).collect()
}

// ── the screen's data (W6 draws it) ─────────────────────────────────────────────────────────────────────────────────────

/// A pop-up of the section (C# `PhasePicker`): "기본값" first, "혼합" while the value is the mixed sentinel, then the other values
/// once each; `current` is the value shown.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PhasePicker {
    pub id: String,
    pub label: String,
    pub current: String,
    pub options: Vec<PhaseModelVersion>,
}

/// `PhasePicker(label, values, current, id, action)`'s rows.
pub fn picker(id: &str, label: &str, values: &[PhaseModelVersion], current: &str) -> PhasePicker {
    let mut options = vec![PhaseModelVersion { value: "default".into(), label: default_option() }];
    if current == MIXED_SENTINEL {
        options.push(PhaseModelVersion { value: MIXED_SENTINEL.into(), label: mixed_label() });
    }
    for value in values.iter().filter(|v| v.value != "default" && v.value != MIXED_SENTINEL) {
        if !options.iter().any(|o| o.value == value.value) {
            options.push(value.clone());
        }
    }
    PhasePicker { id: id.into(), label: label.into(), current: current.into(), options }
}

/// A provider's phase row: its name, the model picker (or the words that the pane's model is used) and the effort picker (or the
/// words that the phase has no effort).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PhaseRowForm {
    pub phase: Phase,
    pub title: String,
    pub model: Option<PhasePicker>,
    /// `settings.phaseModels.paneModel` when there is no model picker.
    pub pane_model: Option<String>,
    pub effort: Option<PhasePicker>,
    /// `settings.phaseModels.effortUnsupported` when there is no effort picker.
    pub effort_unsupported: Option<String>,
}

/// A registered name's row.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RegisteredRow {
    pub name: String,
    /// `settings.phaseModels.supportsEffortLabel` when it takes an effort.
    pub supports_effort: Option<String>,
}

/// A provider's block: its name and beta badge, a row per phase (review left out for Claude), a picker per alias knob, the
/// registered names and the add row's words.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ProviderForm {
    pub provider: String,
    pub label: String,
    pub beta: bool,
    pub phases: Vec<PhaseRowForm>,
    pub knobs: Vec<(PhaseModelKnobRow, PhasePicker)>,
    pub registered_title: String,
    pub registered: Vec<RegisteredRow>,
    pub delete_button: String,
    pub add_label: String,
    pub add_button: String,
    pub supports_effort_label: String,
    pub effort_levels_label: String,
    pub effort_levels: Vec<String>,
}

/// C# `FillPhaseModelsSection` (M/PhaseModelSettingsView.swift:9-67) as data: the two explanations, a block per provider, and the
/// error of a file that could not be read.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PhaseModelsForm {
    pub title: String,
    pub description: String,
    pub effort_note: String,
    pub providers: Vec<ProviderForm>,
    pub error: Option<String>,
}

/// The section's rows for `config` and `tools`; `catalog` is each provider's list in hand (`PhaseCatalog`), `registered` its
/// registered names (`PhaseRegistered`).
pub fn form(
    config: &PhaseModelsSnapshot,
    tools: &PhaseModelTools,
    catalog: &dyn Fn(&str) -> ModelCatalog,
    registered: &dyn Fn(&str) -> Vec<RegisteredModelEntry>,
) -> PhaseModelsForm {
    let effort_word = strings::get("composer.label.effort");
    let blocks = tool_blocks(config, tools);
    let providers_form = ["claude", "codex"]
        .iter()
        .map(|provider| {
            let catalog = catalog(provider);
            let registered = registered(provider);
            let model_picker =
                |id: &str, label: &str, current: &str| picker(id, label, &phase_models::versions(&catalog, &registered, current), current);
            let phases = PHASES
                .iter()
                .filter(|p| *provider != "claude" || **p != Phase::Review)
                .map(|phase| {
                    let state = if *provider == "claude" {
                        phase_routing::claude_row_state(*phase, config)
                    } else {
                        phase_routing::codex_row_state(*phase, config)
                    };
                    let title = phase_label(*phase);
                    let model = state.as_ref().map(|state| {
                        model_picker(
                            &format!("phaseModels-model-{provider}-{}", phase.id()),
                            &title,
                            state.value().unwrap_or(MIXED_SENTINEL),
                        )
                    });
                    let effort = match (*provider, phase) {
                        ("claude", Phase::Execution) => Some(config.claude_main_effort.clone().unwrap_or_else(|| "default".into())),
                        ("codex", Phase::Planning) => Some(knob(&config.codex_plan_mode_reasoning_effort).to_string()),
                        ("codex", Phase::Execution) => Some(config.codex_main_effort.clone().unwrap_or_else(|| "default".into())),
                        ("codex", Phase::Subagents) => Some(config.codex_subagent_effort.clone().unwrap_or_else(|| "default".into())),
                        _ => None,
                    };
                    let effort_picker = effort.as_ref().map(|effort| {
                        let levels: Vec<PhaseModelVersion> = phase_models::efforts(provider, &catalog, effort)
                            .into_iter()
                            .map(|v| PhaseModelVersion { value: v.clone(), label: v })
                            .collect();
                        let mut effort_picker = picker(&format!("phaseModels-effort-{provider}-{}", phase.id()), &title, &levels, effort);
                        effort_picker.label = format!("{title} · {effort_word}");
                        effort_picker
                    });
                    PhaseRowForm {
                        phase: *phase,
                        pane_model: model.is_none().then(|| strings::get("settings.phaseModels.paneModel")),
                        effort_unsupported: effort.is_none().then(|| strings::get("settings.phaseModels.effortUnsupported")),
                        title,
                        model,
                        effort: effort_picker,
                    }
                })
                .collect();
            let knobs = blocks
                .iter()
                .find(|b| b.tool == *provider)
                .map(|b| b.knobs.iter().filter(|k| !k.is_effort).cloned().collect::<Vec<_>>())
                .unwrap_or_default()
                .into_iter()
                .map(|knob| {
                    let knob_picker = model_picker(&format!("phase-models-knob-{}", knob.knob_id), &knob.label, &knob.value);
                    (knob, knob_picker)
                })
                .collect();
            ProviderForm {
                provider: provider.to_string(),
                label: strings::provider_label(provider).to_string(),
                beta: providers::is_beta(provider),
                phases,
                knobs,
                registered_title: strings::get("settings.phaseModels.registeredTitle"),
                registered: registered
                    .iter()
                    .map(|e| RegisteredRow {
                        name: e.name.clone().unwrap_or_default(),
                        supports_effort: e.supports_effort.then(|| strings::get("settings.phaseModels.supportsEffortLabel")),
                    })
                    .collect(),
                delete_button: strings::get("settings.phaseModels.deleteButton"),
                add_label: strings::get("settings.phaseModels.addPlaceholder"),
                add_button: strings::get("settings.phaseModels.addButton"),
                supports_effort_label: strings::get("settings.phaseModels.supportsEffortLabel"),
                effort_levels_label: strings::get("settings.phaseModels.effortLevelsLabel"),
                effort_levels: wire::EFFORTS.iter().map(|s| s.to_string()).collect(),
            }
        })
        .collect();
    PhaseModelsForm {
        title: section_title(),
        description: description(),
        effort_note: strings::get("settings.phaseModels.effortNote"),
        providers: providers_form,
        error: tools.error.clone(),
    }
}

/// `PhaseRegistered(provider)`: the app's registered names of a provider (Codex's, else Claude's).
pub fn registered_of(state: &AppSnapshot, provider: &str) -> Vec<RegisteredModelEntry> {
    let defaults = state.model_defaults.as_ref();
    let side = defaults.and_then(|d| if provider == "codex" { d.codex.as_ref() } else { d.claude.as_ref() });
    side.and_then(|s| s.registered_models.as_ref()).map(|list| list.iter().flatten().cloned().collect()).unwrap_or_default()
}

/// `UpdatePhaseRegistered`: the state with a provider's registered names changed.
fn update_registered(
    state: &AppSnapshot,
    provider: &str,
    change: impl FnOnce(Vec<RegisteredModelEntry>) -> Vec<RegisteredModelEntry>,
) -> AppSnapshot {
    let mut next = state.clone();
    let mut defaults = next
        .model_defaults
        .clone()
        .unwrap_or(ModelDefaultsConfig { claude: Some(ProviderModeDefaults::default()), codex: Some(ProviderModeDefaults::default()) });
    let side = if provider == "codex" { &mut defaults.codex } else { &mut defaults.claude };
    let mut current = side.clone().unwrap_or_default();
    let entries = change(current.registered_models.iter().flatten().flatten().cloned().collect());
    current.registered_models = Some(entries.into_iter().map(Some).collect());
    *side = Some(current);
    next.model_defaults = Some(defaults);
    next
}

/// The add row (`Add()`): a name registered for a provider ([`crate::phase_models::registration`]'s refusal as the error the row
/// shows), the state with it.
pub fn register(state: &AppSnapshot, provider: &str, name: &str, supports_effort: bool, levels: &[&str]) -> crate::Result<AppSnapshot> {
    let entry = phase_models::registration(name, &registered_of(state, provider), supports_effort, levels)?;
    Ok(update_registered(state, provider, |entries| {
        if entries.iter().any(|e| e.name == entry.name) {
            entries
        } else {
            let mut entries = entries;
            entries.push(entry);
            entries
        }
    }))
}

/// A registered name's delete button: the state without it.
pub fn unregister(state: &AppSnapshot, provider: &str, name: &str) -> AppSnapshot {
    update_registered(state, provider, |entries| entries.into_iter().filter(|e| e.name.as_deref() != Some(name)).collect())
}
