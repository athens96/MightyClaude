//! `models`: 페이즈별 모델 (C# MainWindow.PhaseModels.cs:23-240 `BuildPhaseModelsSection`, `FillPhaseModelsSection`, `PhasePicker`,
//! `PhasePair`, `AddRegisteredModels`, `SavePhaseModelEdit`; MainWindow.PhaseModelsSmoke.cs), drawn from mighty-run's typed
//! `phase_section::form`. Owner: W6-A.
//!
//! omc's and Ouroboros's settings (`PhaseModelTools`) are read again as Settings opens and as the models category is chosen, outside
//! any lock (C# `LoadTools` on every build; a smoke uses the fixture tools and reads nothing); a change to them is written back only when the app may write the user's settings (`--allow-state-write`, never a smoke),
//! else kept for this run, as C#'s smoke keeps them. Every model saved went through `wire::model` (`phase_models::apply_row`).

use std::collections::HashMap;
use std::sync::{Arc, Mutex};

use mighty_core::design_tokens::{Token, metrics};
use mighty_core::model::PhaseModelsSnapshot;
use mighty_native::kit::{ControlSize, TextStyle};
use mighty_native::ui::{Border, Content, El, Grid, Kind, TextBlock, Thickness, Track, Ui, VAlign};
use mighty_run::model::ModelCatalog;
use mighty_run::phase_models;
use mighty_run::phase_routing::Phase;
use mighty_run::phase_section::{self, MIXED_SENTINEL, PhaseModelEdit, PhaseModelTools, PhasePicker};
use mighty_run::providers;

use crate::host::Host;

/// `PairSelectWidth`, `PhaseNameField`.
const PAIR_WIDTH: f64 = 160.0;
const NAME_FIELD: f64 = 276.0;

/// A provider's registration draft (C# `phaseRegistrationDrafts`), kept across rebuilds and cleared by a successful add.
#[derive(Debug, Clone, Default, PartialEq)]
struct Draft {
    name: String,
    supports_effort: bool,
    levels: Vec<String>,
    error: Option<String>,
}

#[derive(Default)]
pub struct Models {
    tools: Option<PhaseModelTools>,
    drafts: HashMap<String, Draft>,
    /// The smoke's catalogs, in place of the providers' (C#'s smoke replaces the runtime's catalogs).
    catalogs: Option<HashMap<String, ModelCatalog>>,
}

static MODELS: Mutex<Option<Models>> = Mutex::new(None);

fn with<T>(f: impl FnOnce(&mut Models) -> T) -> T {
    let mut models = MODELS.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
    f(models.get_or_insert_with(Models::default))
}

/// The user's home, where omc's and Ouroboros's settings live.
fn home() -> Option<std::path::PathBuf> {
    std::env::var_os("USERPROFILE").or_else(|| std::env::var_os("HOME")).map(std::path::PathBuf::from)
}

/// `LoadTools`: read again each time (the smoke's fixture tools in a smoke, nothing read). Never under a lock: the files are read first.
pub fn read_tools(host: &Host) {
    let tools = if host.smoke.is_some() {
        phase_section::smoke_fixture_tools()
    } else {
        home().map(|home| phase_section::load_tools(&home)).unwrap_or_default()
    };
    with(|m| m.tools = Some(tools));
}

/// The tools read again on a task of its own (the models category chosen while the surface's lock is held), then Settings read again.
pub fn reload_tools(host: &Arc<Host>) {
    let host = host.clone();
    tauri::async_runtime::spawn(async move {
        let reader = host.clone();
        if tauri::async_runtime::spawn_blocking(move || read_tools(&reader)).await.is_ok() {
            crate::native::touch(&host, super::SURFACE);
        }
    });
}

/// The smoke's models: its fixture tools, no drafts and `catalogs` in place of the providers'; what was there is given back to
/// [`restore`] afterwards.
pub fn swap_for_smoke(catalogs: HashMap<String, ModelCatalog>) -> Models {
    let smoke = Models { tools: Some(phase_section::smoke_fixture_tools()), drafts: HashMap::new(), catalogs: Some(catalogs) };
    let mut models = MODELS.lock().unwrap_or_else(|poisoned| poisoned.into_inner());
    models.replace(smoke).unwrap_or_default()
}

/// The models as they were before [`swap_for_smoke`].
pub fn restore(saved: Models) {
    *MODELS.lock().unwrap_or_else(|poisoned| poisoned.into_inner()) = Some(saved);
}

fn tools() -> PhaseModelTools {
    with(|m| m.tools.clone().unwrap_or_default())
}

fn catalog(host: &Host, provider: &str) -> ModelCatalog {
    if let Some(catalog) = with(|m| m.catalogs.as_ref().and_then(|c| c.get(provider).cloned())) {
        return catalog;
    }
    host.runs().map(|r| r.catalog_of(provider)).unwrap_or_else(|| providers::fallback(provider))
}

/// `SavePhaseModelEdit` with the edit made from the state as the save finds it (C# `UpdateAsync(s => ...)`: two picks at once never
/// undo each other): the phase models saved, then omc's and Ouroboros's settings written (only when the app may write the user's
/// settings, else kept for this run).
fn save_edit(
    host: &Arc<Host>,
    make: impl FnOnce(&PhaseModelsSnapshot, &PhaseModelTools) -> Result<PhaseModelEdit, String>,
) -> Result<(), String> {
    let before = tools();
    let mut made = None;
    let mut refused = None;
    host.view().update(|state| {
        let config = state.phase_models.clone().unwrap_or_else(phase_models::new_snapshot);
        match make(&config, &before) {
            Ok(edit) => {
                state.phase_models = Some(edit.config.clone());
                made = Some(edit);
            }
            Err(error) => refused = Some(error),
        }
    })?;
    if let Some(error) = refused {
        return Err(error);
    }
    let Some(edit) = made else { return Ok(()) };
    let after = match home().filter(|_| host.may_change_machine()) {
        Some(home) => phase_section::save_tools(&before, &edit, &home),
        None => PhaseModelTools { omc_agents: edit.omc_agents.clone(), ouroboros_keys: edit.ouroboros_keys.clone(), ..before },
    };
    with(|m| m.tools = Some(after));
    Ok(())
}

fn config(host: &Host) -> PhaseModelsSnapshot {
    host.view().snapshot.phase_models.clone().unwrap_or_else(phase_models::new_snapshot)
}

/// `ApplyProviderPhaseRow`.
pub fn apply_row(host: &Arc<Host>, provider: &str, phase: Phase, value: &str) -> Result<(), String> {
    save_edit(host, |config, tools| phase_models::apply_row(provider, phase, value, config, tools).map_err(|e| e.to_string()))
}

/// `ApplyPhaseEffort`, from the state as the save finds it.
pub fn apply_effort(host: &Arc<Host>, provider: &str, phase: Phase, value: &str) -> Result<(), String> {
    let mut refused = None;
    host.view().update(|state| {
        let config = state.phase_models.clone().unwrap_or_else(phase_models::new_snapshot);
        match phase_models::set_effort(&config, provider, phase, value) {
            Ok(next) => state.phase_models = Some(next),
            Err(error) => refused = Some(error.to_string()),
        }
    })?;
    refused.map_or(Ok(()), Err)
}

/// `ApplyPhaseModelKnob`.
pub fn apply_knob(host: &Arc<Host>, knob: &str, value: &str) -> Result<(), String> {
    save_edit(host, |config, tools| Ok(phase_section::apply_knob(knob, value, config, tools)))
}

/// `Add()` + `UpdatePhaseRegistered`: the draft registered (its words why not shown under it), every pane's composer read again.
pub fn register(host: &Arc<Host>, provider: &str, name: &str, supports: bool, levels: &[String]) -> Result<(), String> {
    let levels: Vec<&str> = levels.iter().map(String::as_str).collect();
    let mut registered = Ok(());
    host.view().update(|state| match phase_section::register(state, provider, name, supports, &levels) {
        Ok(next) => state.model_defaults = next.model_defaults,
        Err(error) => registered = Err(error),
    })?;
    match registered {
        Ok(()) => {
            with(|m| m.drafts.insert(provider.to_string(), Draft::default()));
            host.panes_changed();
            Ok(())
        }
        Err(error) => {
            let words = match error {
                mighty_run::RunError::Invalid(words) => words,
                other => other.to_string(),
            };
            with(|m| m.drafts.entry(provider.to_string()).or_default().error = Some(words.clone()));
            Err(words)
        }
    }
}

/// The delete button.
pub fn unregister(host: &Arc<Host>, provider: &str, name: &str) -> Result<(), String> {
    host.view().update(|state| state.model_defaults = phase_section::unregister(state, provider, name).model_defaults)?;
    host.panes_changed();
    Ok(())
}

/// The box from the saved phase models and the tools read.
pub fn phase_models(host: &Arc<Host>, ui: &mut Ui) -> El {
    let config = config(host);
    build(host, ui, &config, &tools())
}

/// `FillPhaseModelsSection` over `config` and `tools` (the smoke builds it over its fixture too).
pub fn build(host: &Arc<Host>, ui: &mut Ui, config: &PhaseModelsSnapshot, tools: &PhaseModelTools) -> El {
    // Only the registered models are needed: they alone are cloned (not the whole state, four times a second).
    let entries: HashMap<&str, Vec<_>> = {
        let view = host.view();
        ["claude", "codex"].iter().map(|p| (*p, phase_section::registered_of(&view.snapshot, p))).collect()
    };
    let catalogs: HashMap<&str, ModelCatalog> = ["claude", "codex"].iter().map(|p| (*p, catalog(host, p))).collect();
    let form = phase_section::form(config, tools, &|p| catalogs.get(p).cloned().unwrap_or_else(|| providers::fallback(p)), &|p| {
        entries.get(p).cloned().unwrap_or_default()
    });
    let mut rows = ui.settings_rows();
    rows.add(ui.settings_text(form.description.clone(), TextStyle::size(11.0).ink(Token::Ink2)));
    rows.add(ui.settings_text(form.effort_note.clone(), TextStyle::size(11.0).ink(Token::Ink2)));
    for provider in &form.providers {
        let p = provider.provider.clone();
        let mut header = vec![ui.settings_text(provider.label.clone(), TextStyle::size(12.0).medium())];
        if provider.beta {
            header.push(ui.beta_badge());
        }
        rows.add(El::stack(true, metrics::SPACING_SM, header).aid(format!("phaseModels-provider-{p}")));
        for row in &provider.phases {
            let model = match &row.model {
                Some(picker) => {
                    let (to, provider, phase) = (host.clone(), p.clone(), row.phase);
                    pair(
                        ui,
                        combo(ui, picker, move |value| {
                            let (host, provider) = (to.clone(), provider.clone());
                            crate::native::act(&to, async move { apply_row(&host, &provider, phase, &value) });
                        }),
                    )
                }
                None => ui.settings_text(row.pane_model.clone().unwrap_or_default(), TextStyle::size(12.0).ink(Token::Ink2)),
            };
            let effort = match &row.effort {
                Some(picker) => {
                    let (to, provider, phase) = (host.clone(), p.clone(), row.phase);
                    pair(
                        ui,
                        combo(ui, picker, move |value| {
                            let (host, provider) = (to.clone(), provider.clone());
                            crate::native::act(&to, async move { apply_effort(&host, &provider, phase, &value) });
                        }),
                    )
                }
                None => ui.settings_tertiary(row.effort_unsupported.clone().unwrap_or_default(), 12.0, false, false),
            };
            let pickers = El::stack(true, metrics::SPACING_SM, vec![model, effort]);
            rows.add(ui.settings_labeled(ui.settings_text(row.title.clone(), TextStyle::default()), pickers, false));
        }
        for (knob, picker) in &provider.knobs {
            let (to, id) = (host.clone(), knob.knob_id.clone());
            let control = combo(ui, picker, move |value| {
                let (host, id) = (to.clone(), id.clone());
                crate::native::act(&to, async move { apply_knob(&host, &id, &value) });
            });
            rows.add(ui.settings_labeled(
                ui.settings_text(knob.label.clone(), TextStyle::default()),
                ui.settings_popup_frame(control),
                false,
            ));
        }
        rows.add(registered(host, ui, provider));
    }
    if let Some(error) = &form.error {
        rows.add(ui.settings_text(error.clone(), TextStyle::size(11.0).ink(Token::ErrText)));
    }
    rows.finish()
}

/// `PhasePicker`: an inline pop-up 12 after its label, its items the picker's options, the chosen one selected; a pick that is not the
/// mixed sentinel runs `pick`.
fn combo(ui: &Ui, picker: &PhasePicker, pick: impl Fn(String) + Send + Sync + 'static) -> El {
    let items: Vec<Content> = picker.options.iter().map(|o| Content::from(o.label.clone())).collect();
    let index = picker.options.iter().position(|o| o.value == picker.current).unwrap_or(0) as i32;
    let values: Vec<String> = picker.options.iter().map(|o| o.value.clone()).collect();
    ui.settings_popup(El::combo_box(index, items), false)
        .margin(Thickness::new(metrics::SPACING_LG, 0.0, 0.0, 0.0))
        .named(picker.label.clone())
        .aid(picker.id.clone())
        .on_select(move |at| {
            if let Some(value) = values.get(at as usize).filter(|v| *v != MIXED_SENTINEL) {
                pick(value.clone());
            }
        })
}

/// `PhasePair`: the pop-up with its chip, 160 wide, without its own margin.
fn pair(ui: &Ui, combo: El) -> El {
    ui.settings_popup_frame(combo.margin(Thickness::ZERO)).width(PAIR_WIDTH)
}

/// `AddRegisteredModels`: the registered models with their delete buttons, then the add row, the effort switch and levels, and the
/// draft's error.
fn registered(host: &Arc<Host>, ui: &mut Ui, provider: &phase_section::ProviderForm) -> El {
    let p = provider.provider.clone();
    let draft = with(|m| m.drafts.entry(p.clone()).or_default().clone());
    let mut block = vec![ui.settings_text(provider.registered_title.clone(), TextStyle::size(11.0).medium())];
    for entry in &provider.registered {
        let mut words = vec![ui.settings_text(entry.name.clone(), TextStyle::size(11.0).mono())];
        if let Some(label) = &entry.supports_effort {
            words.push(ui.settings_text(label.clone(), TextStyle::size(10.0).ink(Token::Ink2)));
        }
        let (to, provider_id, name) = (host.clone(), p.clone(), entry.name.clone());
        let delete = ui
            .settings_push(
                ui.button(provider.delete_button.clone(), move || {
                    let (host, provider_id, name) = (to.clone(), provider_id.clone(), name.clone());
                    crate::native::act(&to, async move { unregister(&host, &provider_id, &name) });
                }),
                ControlSize::Small,
                false,
                true,
            )
            .cell(0, 1);
        block.push(
            El::grid(Grid {
                children: vec![El::stack(false, metrics::SPACING_XXS, words).va(VAlign::Center), delete],
                cols: vec![Track::Star(1.0), Track::Auto],
                col_spacing: metrics::SPACING_SM,
                ..Grid::default()
            })
            .aid(format!("phaseModels-registered-{p}-{}", entry.name))
            .keyed(entry.name.clone()),
        );
    }
    // The words typed live in the draft (C# `phaseRegistrationDrafts`), so they outlive a category switch; a successful add clears them.
    let provider_id = p.clone();
    let field = ui
        .settings_field(El::text_box(draft.name.clone()), 11.0)
        .named(provider.add_label.clone())
        .aid(format!("phaseModels-add-{p}"))
        .on_text(move |words| with(|m| m.drafts.entry(provider_id.clone()).or_default().name = words))
        .cell(0, 1);
    let (to, provider_id) = (host.clone(), p.clone());
    let add = ui
        .settings_push(
            ui.button(provider.add_button.clone(), move || {
                let draft = with(|m| m.drafts.entry(provider_id.clone()).or_default().clone());
                let (host, provider_id) = (to.clone(), provider_id.clone());
                crate::native::act(&to, async move {
                    // The words show under the add row (the draft's error); the banner stays clear, as C#'s Add catches them.
                    let _ = register(&host, &provider_id, &draft.name, draft.supports_effort, &draft.levels);
                    Ok(())
                });
            }),
            ControlSize::Small,
            false,
            false,
        )
        .aid(format!("phaseModels-addButton-{p}"))
        .cell(0, 2);
    let add_row = El::grid(Grid {
        children: vec![ui.settings_text(provider.add_label.clone(), TextStyle::default()), field.width(NAME_FIELD), add],
        cols: vec![Track::Star(1.0), Track::Pixel(NAME_FIELD), Track::Auto],
        col_spacing: metrics::SPACING_SM,
        ..Grid::default()
    });
    let provider_id = p.clone();
    let supports = ui.settings_switch(
        provider.supports_effort_label.clone(),
        draft.supports_effort,
        Some(&format!("phaseModels-addEffort-{p}")),
        move |on| {
            with(|m| {
                let draft = m.drafts.entry(provider_id.clone()).or_default();
                draft.supports_effort = on;
                if !on {
                    draft.levels.clear();
                }
            })
        },
    );
    let supports_row =
        ui.settings_labeled(ui.settings_text(provider.supports_effort_label.clone(), TextStyle::size(11.0)), supports, false);
    let chips = provider
        .effort_levels
        .iter()
        .map(|level| {
            let on = draft.levels.contains(level);
            let mut words = El::new(Kind::TextBlock(TextBlock {
                text: level.clone(),
                size: Some(9.0),
                weight: 400,
                foreground: Some(ui.brush(Token::OnAccent)),
                ..TextBlock::default()
            }));
            words.va = VAlign::Center;
            let face = El::border(Border {
                child: Some(Box::new(words)),
                padding: Thickness::new(metrics::SPACING_XS, 0.0, metrics::SPACING_XS, 0.0),
                corner_radius: 3.5,
                background: Some(ui.brush(Token::Accent)),
                ..Border::default()
            })
            .height(14.0);
            let (provider_id, level_id) = (p.clone(), level.clone());
            El::new(Kind::ToggleButton { on, content: face.into() })
                .opacity(if on { 1.0 } else { 0.4 })
                .named(level.clone())
                .aid(format!("phaseModels-addLevel-{p}-{level}"))
                .on_toggle(move |on| {
                    with(|m| {
                        let draft = m.drafts.entry(provider_id.clone()).or_default();
                        draft.levels.retain(|l| *l != level_id);
                        if on {
                            draft.levels.push(level_id.clone());
                            let order: Vec<&str> = mighty_core::wire::EFFORTS.to_vec();
                            draft.levels.sort_by_key(|l| order.iter().position(|o| o == l));
                        }
                    })
                })
        })
        .collect();
    let levels = El::stack(
        false,
        metrics::SPACING_XXS,
        vec![
            ui.settings_text(provider.effort_levels_label.clone(), TextStyle::size(10.0).ink(Token::Ink2)),
            El::stack(true, metrics::SPACING_XS, chips),
        ],
    )
    .visible(draft.supports_effort);
    block.push(El::stack(false, metrics::SPACING_XS, vec![add_row, supports_row, levels]).margin(Thickness::new(0.0, -2.0, 0.0, 0.0)));
    let error = draft.error.clone().unwrap_or_default();
    block.push(
        ui.settings_text(error.clone(), TextStyle::size(11.0).ink(Token::ErrText))
            .aid(format!("phaseModels-error-{p}"))
            .visible(!error.is_empty()),
    );
    El::stack(false, metrics::SPACING_SM, block)
}

/// The smoke's `phaseModelsSection` (C# `RunPhaseModelsSectionSmoke`): the box over `{claudeMain: "fixture-main", codexSubagentEffort:
/// "xhigh"}` and the fixture tools shows both providers, Claude's execution as mixed, Codex's sub-agent effort as xhigh, no model
/// picker for Codex's planning and execution, and each provider's add field with the five effort chips. The failed checks' names, if any.
pub fn smoke_section(host: &Arc<Host>, ui: &mut Ui) -> Vec<&'static str> {
    let config = PhaseModelsSnapshot {
        claude_main: Some("fixture-main".into()),
        codex_subagent_effort: Some("xhigh".into()),
        ..phase_models::new_snapshot()
    };
    let el = build(host, ui, &config, &phase_section::smoke_fixture_tools());
    let node: serde_json::Value = serde_json::from_str(&mighty_native::reader::render(&el, ui.env)).unwrap_or_default();
    let mut found: Vec<&serde_json::Value> = vec![];
    fn walk<'a>(value: &'a serde_json::Value, out: &mut Vec<&'a serde_json::Value>) {
        match value {
            serde_json::Value::Object(map) => {
                out.push(value);
                map.values().for_each(|v| walk(v, out));
            }
            serde_json::Value::Array(items) => items.iter().for_each(|v| walk(v, out)),
            _ => {}
        }
    }
    walk(&node, &mut found);
    let aid = |n: &&serde_json::Value| n.get("aid").and_then(|a| a.as_str()).unwrap_or("").to_string();
    let by = |id: &str| found.iter().find(|n| aid(n) == id).copied();
    let selected = |id: &str| by(id).and_then(|n| Some(n["items"][n["index"].as_u64()? as usize].as_str()?.to_string()));
    let mut failed = vec![];
    if found.iter().filter(|n| aid(n).starts_with("phaseModels-provider-")).count() != 2 {
        failed.push("providers");
    }
    if selected("phaseModels-model-claude-execution") != Some(phase_section::mixed_label()) {
        failed.push("claudeExecutionMixed");
    }
    if selected("phaseModels-effort-codex-subagents").as_deref() != Some("xhigh") {
        failed.push("codexSubagentEffort");
    }
    if by("phaseModels-model-codex-execution").is_some() || by("phaseModels-model-codex-planning").is_some() {
        failed.push("codexPaneModel");
    }
    // The chips sit in the levels panel, hidden until the effort switch is on: they are counted in the tree built, as C# counts them in
    // its visual tree (the page reads them once shown).
    let mut aids = vec![];
    tree_aids(&el, &mut aids);
    for p in ["claude", "codex"] {
        let chips = aids.iter().filter(|a| a.starts_with(&format!("phaseModels-addLevel-{p}-"))).count();
        if by(&format!("phaseModels-add-{p}")).is_none() || chips != mighty_core::wire::EFFORTS.len() {
            failed.push("addRow");
        }
    }
    failed
}

/// Every automation id in the tree built, shown or not.
fn tree_aids(el: &El, out: &mut Vec<String>) {
    if let Some(aid) = &el.aid {
        out.push(aid.clone());
    }
    match &el.kind {
        Kind::Grid(Grid { children, .. }) | Kind::StackPanel(mighty_native::ui::StackPanel { children, .. }) | Kind::Panel { children } => {
            children.iter().for_each(|child| tree_aids(child, out))
        }
        Kind::Border(Border { child: Some(child), .. }) => tree_aids(child, out),
        _ => {}
    }
}
