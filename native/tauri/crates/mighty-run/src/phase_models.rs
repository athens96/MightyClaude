//! The run's part of C# `PhaseModelPreferences` (PhaseModelPreferences.cs:8-24): Settings › Models' per-phase knobs as a run reads
//! them. The settings rows themselves are W6.

use mighty_core::model::PhaseModelsSnapshot;
use mighty_core::normalize;

use crate::model::{ModelCatalog, StartRunRequest};
use crate::providers;

/// `PhaseModelPreferences.Normalize`.
pub fn normalized(value: Option<&PhaseModelsSnapshot>) -> Option<PhaseModelsSnapshot> {
    normalize::phase_models(value)
}

/// `PhaseModelPreferences.EffectiveModel`: a Claude pane on its default model runs Settings › Models' main model.
pub fn effective_model(request: &StartRunRequest) -> String {
    if request.provider == "claude" && request.model == "default" {
        return normalized(request.phase_models.as_ref()).and_then(|p| p.claude_main).unwrap_or_else(|| "default".into());
    }
    request.model.clone()
}

/// `PhaseModelPreferences.MainEffort`.
pub fn main_effort(request: &StartRunRequest) -> Option<String> {
    let phase = normalized(request.phase_models.as_ref())?;
    match request.provider.as_str() {
        "claude" => phase.claude_main_effort,
        "codex" => phase.codex_main_effort,
        _ => None,
    }
}

/// `PhaseModelPreferences.ResolveEffort`: a pane left on Auto takes the main effort when its model takes that level.
pub fn resolve_effort(request: &StartRunRequest, catalog: &ModelCatalog) -> StartRunRequest {
    let settings = request.settings();
    if settings.effort != "default" {
        return request.clone();
    }
    let Some(effort) = main_effort(request) else { return request.clone() };
    let levels = providers::efforts(&request.provider, &effective_model(request), catalog, Some(&request.registered_models));
    if levels.contains(&effort) {
        let mut resolved = request.clone();
        resolved.settings = Some(mighty_core::model::RunSettings { effort, ..settings });
        return resolved;
    }
    request.clone()
}
