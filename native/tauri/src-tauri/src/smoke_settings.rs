//! The smoke's settings steps (C# MainWindow.WebShell.Settings.cs:348-473 `RunWebSettingsSmoke`, MainWindow.WebShell.LiveSmoke.cs).
//! Owner: W6-A.
//!
//! - The sidebar's gear opens Settings (`settingsOpened`); every category is clicked in its side list and shows each of
//!   `SettingsGroups`' headings while the host saved it as `SettingsPane` (`settingsCategories`); in every category whose boxes are
//!   W6-A's, each box shows something and the category at least one control (`settingsCategoryControls`; the tools and CLI tabs'
//!   boxes are W6-B's, -C's and -D's, recorded under `settings.categories` and required once they are filled).
//! - The theme picker (`[data-aid=settings-theme]`) switches the theme, the state and the page follow, and back (`themeSwitched`,
//!   `themeRestored`); the footer's ✕ closes Settings and the window holds no dialog (`settingsClosed`).
//! - The terminal history panel over a shell pane holding "fixture answer 2", closed with its ✕ (`terminalHistory`); a model-id-style
//!   dialog: typed, refused with its note, accepted (`modelDialog`).
//! - The live keys (C# `RunWebLiveSmoke`): `settingsSections` (the headings and D's CLI update rows, the auto-update switch flipped and
//!   restored, in memory), `phaseModelsSection`, `phaseModelsActions`, `completionNotification`.

use std::sync::{Arc, Mutex};

use mighty_core::settings_sections::{self, available};
use mighty_core::wire;
use mighty_native::surface::{DialogButton, DialogResult, DialogSpec};
use mighty_native::ui::{Content, El, Local, Locals, Ui};
use mighty_run::phase_routing::Phase;
use mighty_run::phase_section;
use serde_json::{Value, json};

use crate::host::Host;

/// The categories whose boxes are W6-A's: each box must show something and the category a control.
const A_CATEGORIES: [&str; 7] = ["general", "models", "styles", "mobile", "remote", "companion", "about"];

/// The settings step's probe; `__CATEGORIES__` is filled in with each category's id, its boxes' headings and whether its controls are
/// required.
const PROBE: &str = r#"(async () => {
  const until = async (test, ms) => { const end = performance.now() + ms; while (performance.now() < end) { const v = await test(); if (v) return v; await new Promise((r) => setTimeout(r, 50)); } return test(); };
  const invoke = window.__TAURI_INTERNALS__.invoke;
  const host = (action) => invoke('smoke_settings', { action, args: null });
  const report = { categories: {} };
  const form = () => {
    const v = document.querySelector('[data-native-view=settings]');
    if (!v) return null;
    const f = v.querySelector('[data-testid=settings-form]');
    return {
      category: f?.dataset.category ?? null,
      nav: [...v.querySelectorAll('.native-nav-row[data-category]')].map((e) => e.dataset.category),
      sections: [...(f?.querySelectorAll('[data-testid=native-section]') ?? [])].map((s) => ({ title: s.dataset.title ?? null, heading: s.querySelector('.native-section-title')?.textContent ?? '', controls: s.querySelectorAll('[data-native-id]').length, nodes: s.querySelectorAll('.native-node').length })),
    };
  };
  try {
    document.querySelector('[data-testid=sidebar-settings]')?.click();
    report.opened = !!(await until(async () => form() && (await host('state')).settingsOpen, 15000));
    report.nav = form()?.nav ?? [];
    for (const category of __CATEGORIES__) {
      document.querySelector(`[data-native-view=settings] .native-nav-row[data-category="${category.id}"]`)?.click();
      const filled = (f) => !category.controls || (category.titles.every((t) => f.sections.some((s) => s.title === t && s.nodes > 0)) && f.sections.reduce((n, s) => n + s.controls, 0) > 0);
      const shown = await until(() => { const f = form(); return f && f.category === category.id && category.titles.every((t) => f.sections.some((s) => s.title === t && s.heading.startsWith(t))) && filled(f); }, 10000);
      const state = await host('state');
      const f = form();
      report.categories[category.id] = { shown: !!shown, saved: state.settingsPane === category.id, controlsRequired: category.controls, filled: !!f && f.category === category.id && filled(f), form: f };
    }
    // One harmless setting: the theme picker (Settings › General › 화면), switched and switched back; its segments are dark then light.
    document.querySelector('[data-native-view=settings] .native-nav-row[data-category="general"]')?.click();
    await until(() => form()?.category === 'general', 10000);
    const before = (await host('state')).theme === 'light' ? 'light' : 'dark';
    const other = before === 'light' ? 'dark' : 'light';
    const pick = (theme) => { const b = [...document.querySelectorAll('[data-native-view=settings] [data-aid=settings-theme] button[role=radio]')]; const target = b[theme === 'dark' ? 0 : 1]; if (!target) return false; target.click(); return true; };
    const clicked = !!(await until(() => pick(other), 10000));
    report.themeSwitched = clicked && !!(await until(async () => (await host('state')).theme === other && document.documentElement.dataset.theme === other, 10000));
    await until(() => pick(before), 10000);
    report.themeRestored = !!(await until(async () => ((await host('state')).theme === 'light' ? 'light' : 'dark') === before && document.documentElement.dataset.theme === before, 10000));
    document.querySelector('[data-native-view=settings] [data-testid=settings-close]')?.click();
    report.closed = !!(await until(async () => { const s = await host('state'); return !s.settingsOpen && !s.dialogOpen && !document.querySelector('[data-native-view=settings]'); }, 15000));
    const top = () => [...document.querySelectorAll('[data-native-view^=dialog-]')].pop();
    const dialog = () => { const v = top(); return v ? { title: v.querySelector('.dialog-title')?.textContent ?? '', text: v.textContent, kind: v.dataset.dialogKind ?? '', close: !!v.querySelector('[data-testid=native-close]') } : null; };
    // The terminal history panel, closed with its ✕.
    const history = await host('terminalHistory');
    report.historyShown = !!(await until(() => { const d = dialog(); return d && d.title === history.title && d.text.includes('fixture answer 2') && d.kind === 'panel' && d.close; }, 10000));
    top()?.querySelector('[data-testid=native-close]')?.click();
    report.historyClosed = !!(await until(async () => !top() && !(await host('state')).dialogOpen, 10000));
    // A model-id-style dialog: typed, refused with its note, accepted.
    await host('modelDialog');
    const type = (words) => { const f = top()?.querySelector('input.native-field'); if (!f) return false; Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set.call(f, words); f.dispatchEvent(new Event('input', { bubbles: true })); return true; };
    const primary = () => top()?.querySelector('[data-testid=native-button-primary]')?.click();
    await until(() => type('bad model id'), 10000);
    report.typed = !!(await until(async () => (await host('modelDialogState')).text === 'bad model id', 10000));
    primary();
    report.refused = !!(await until(async () => { const s = await host('modelDialogState'); return s.note.length > 0 && (top()?.textContent ?? '').includes(s.note) && s.answer === null; }, 10000));
    type('claude-smoke-model');
    await until(async () => (await host('modelDialogState')).text === 'claude-smoke-model', 10000);
    primary();
    report.accepted = !!(await until(async () => (await host('modelDialogState')).answer === 'primary' && !top(), 10000));
    report.live = await host('live');
  } catch (error) {
    report.error = String(error);
  }
  await invoke('smoke_probe', { probe: report });
})();"#;

/// Starts the step: the probe, with each available category's boxes.
pub fn start(host: &Arc<Host>) -> bool {
    let categories: Vec<Value> = available(&host.locale)
        .into_iter()
        .map(|c| {
            let titles: Vec<String> = crate::settings::groups(&host.locale, c.id).into_iter().map(|g| g.title).collect();
            json!({"id": c.id, "titles": titles, "controls": A_CATEGORIES.contains(&c.id)})
        })
        .collect();
    let Some(smoke) = &host.smoke else { return false };
    host.log.line("smoke: settings steps");
    smoke.eval(host, &PROBE.replace("__CATEGORIES__", &Value::Array(categories).to_string()));
    true
}

/// The step's checks.
pub fn checks(report: &Value) -> Vec<(&'static str, bool)> {
    let yes = |key: &str| report.get(key) == Some(&Value::Bool(true));
    let categories = report.get("categories").and_then(Value::as_object);
    let all = |test: &dyn Fn(&Value) -> bool| categories.is_some_and(|all| !all.is_empty() && all.values().all(test));
    let flag = |c: &Value, key: &str| c.get(key) == Some(&Value::Bool(true));
    let live = report.get("live").cloned().unwrap_or(Value::Null);
    let live_ok = |key: &str| live.get(key).is_some_and(|v| v.get("error").is_none() && v != &Value::Bool(false));
    vec![
        ("settingsOpened", report.get("error").is_none() && yes("opened")),
        ("settingsCategories", all(&|c| flag(c, "shown") && flag(c, "saved"))),
        ("settingsCategoryControls", all(&|c| !flag(c, "controlsRequired") || flag(c, "filled"))),
        ("themeSwitched", yes("themeSwitched")),
        ("themeRestored", yes("themeRestored")),
        ("settingsClosed", yes("closed")),
        ("terminalHistory", yes("historyShown") && yes("historyClosed")),
        ("modelDialog", yes("typed") && yes("refused") && yes("accepted")),
        ("settingsSections", live_ok("settingsSections")),
        ("phaseModelsSection", live.get("phaseModelsSection") == Some(&Value::Bool(true))),
        ("phaseModelsActions", live.pointer("/phaseModelsActions/selectionsAndRegistrationPersisted") == Some(&Value::Bool(true))),
        ("completionNotification", live.pointer("/completionNotification/status").and_then(Value::as_str).is_some()),
    ]
}

/// The model-id-style dialog's state the probe reads.
#[derive(Default)]
struct ModelDialog {
    text: Option<Local<String>>,
    note: Option<Local<String>>,
    answer: Option<&'static str>,
}

static MODEL: Mutex<Option<ModelDialog>> = Mutex::new(None);

fn model() -> std::sync::MutexGuard<'static, Option<ModelDialog>> {
    MODEL.lock().unwrap_or_else(|poisoned| poisoned.into_inner())
}

/// The smoke's model-id-style dialog (C#'s, built in `RunWebSettingsSmoke`): a field headed `composer.model.idHeader` and a note under
/// it; its primary refused, the note set, while the words are no model id (`wire::model`, the composer's rule).
fn open_model_dialog(host: &Arc<Host>) {
    let locale = host.locale;
    let (text, note) = (Local::new(String::new()), Local::new(String::new()));
    let header = locale.get("composer.model.idHeader");
    let (typed, noted) = (text.clone(), note.clone());
    let mut spec = DialogSpec::new(locale.get("composer.model.enterIdTitle"), move |_| {
        let into = typed.clone();
        let field = El::text_box(typed.get()).header(header.clone()).on_text(move |words| into.set(words));
        Content::El(Box::new(El::vstack(vec![field, El::text_block(noted.get())])))
    });
    spec.primary = Some(DialogButton::new(locale.get("composer.model.select")));
    spec.close = Some(locale.get("settings.run.cancelButton"));
    let (checked, refused) = (text.clone(), note.clone());
    spec.accept = Some(Box::new(move |answer| {
        if answer != DialogResult::Primary || wire::model(checked.get().trim()) {
            return true;
        }
        refused.set(locale.get("composer.model.invalidName"));
        false
    }));
    *model() = Some(ModelDialog { text: Some(text), note: Some(note), answer: None });
    let handle = crate::native::start_dialog(host, spec);
    tauri::async_runtime::spawn(async move {
        let answer = match handle.wait().await {
            DialogResult::Primary => "primary",
            DialogResult::Secondary => "secondary",
            DialogResult::None => "none",
        };
        if let Some(dialog) = model().as_mut() {
            dialog.answer = Some(answer);
        }
    });
}

/// The terminal history over a shell pane holding "fixture answer 2" (C#'s smoke pane's record): the panel opens on its own task.
fn open_terminal_history(host: &Arc<Host>) -> Value {
    let pane = mighty_core::model::RunSession {
        id: Some("smoke-history-pane".into()),
        kind: Some("shell".into()),
        title: Some("Smoke shell".into()),
        logs: Some(vec![
            mighty_core::model::LogEntry::new("l1".into(), "output", "fixture answer 1".into(), "2026-11-14T00:00:00Z".into(), None),
            mighty_core::model::LogEntry::new("l2".into(), "output", "fixture answer 2".into(), "2026-11-14T00:00:01Z".into(), None),
        ]),
        ..mighty_core::model::RunSession::default()
    };
    let title = mighty_terminal::history::terminal_history(&pane, &host.locale).title;
    let host = host.clone();
    tauri::async_runtime::spawn(async move { crate::terminal::show_history(&host, &pane).await });
    json!({"title": title})
}

/// The live keys (C# `RunWebLiveSmoke`'s settings steps).
async fn live(host: &Arc<Host>) -> Value {
    let locale = host.locale;
    // settingsSections: the slots' headings as the boxes are built, D's CLI update rows over the fixture, the auto-update switch
    // flipped, read back and restored (in memory: a smoke writes no state).
    let titles: Vec<String> = available(&locale)
        .iter()
        .flat_map(|c| crate::settings::groups(&locale, c.id))
        .map(|g| g.title)
        .filter(|t| settings_sections::windows_titles(&locale).contains(t))
        .collect();
    let mut ordered = settings_sections::windows_titles(&locale);
    ordered.retain(|t| titles.contains(t));
    let (expected, rendered) = crate::updates::section_fixture_statuses(host);
    let sections = settings_sections::smoke_run(
        &locale,
        &ordered,
        &rendered,
        &expected,
        || host.view().snapshot.auto_update_clis,
        |value| {
            let _ = host.view().update(|state| state.auto_update_clis = value);
        },
    );
    let sections = match sections {
        Ok(outcome) => {
            json!({"sections": outcome.sections, "cliUpdateSection": outcome.cli_update_statuses, "toggledTo": outcome.toggled_to, "restored": outcome.restored})
        }
        Err(error) => json!({"error": error}),
    };
    // phaseModelsSection: the box over C#'s fixture.
    let env = crate::native::env(host);
    let mut locals = Locals::default();
    let failed = crate::settings::models::smoke_section(host, &mut Ui::new(&env, &mut locals));
    // phaseModelsActions: a model row, an effort, a registration and its removal, then everything as it was.
    let actions = phase_actions(host);
    let notification = crate::notifications::smoke(host).await;
    json!({
        "settingsSections": sections,
        "phaseModelsSection": failed.is_empty(),
        "phaseModelsSectionFailed": failed,
        "phaseModelsActions": actions,
        "completionNotification": serde_json::from_str::<Value>(&notification.to_json()).unwrap_or(Value::Null),
    })
}

/// C# `RunPhaseModelsActionsSmoke` through the box's own actions (settings/models.rs), in memory.
fn phase_actions(host: &Arc<Host>) -> Value {
    use crate::settings::models;
    let saved = {
        let view = host.view();
        (view.snapshot.phase_models.clone(), view.snapshot.model_defaults.clone())
    };
    let run = || -> Result<(), String> {
        let start = mighty_core::model::PhaseModelsSnapshot {
            claude_main: Some("previous-main".into()),
            codex_review_model: Some("review-unchanged".into()),
            ..mighty_run::phase_models::new_snapshot()
        };
        host.view().update(|state| {
            state.phase_models = Some(start);
            state.model_defaults = None;
        })?;
        models::apply_row(host, "claude", Phase::Execution, "claude-sonnet-fixture")?;
        let config = host.view().snapshot.phase_models.clone().unwrap_or_else(mighty_run::phase_models::new_snapshot);
        if config.claude_main.as_deref() != Some("claude-sonnet-fixture")
            || config.claude_sonnet_alias.as_deref() != Some("claude-sonnet-fixture")
            || config.codex_review_model.as_deref() != Some("review-unchanged")
        {
            return Err(format!("the model row was not saved: {config:?}"));
        }
        models::apply_effort(host, "codex", Phase::Subagents, "xhigh")?;
        if host.view().snapshot.phase_models.as_ref().and_then(|c| c.codex_subagent_effort.clone()).as_deref() != Some("xhigh") {
            return Err("the effort was not saved".into());
        }
        models::register(host, "claude", "registered-phase-fixture", true, &["max".to_string()])?;
        let (claude, codex) = {
            let view = host.view();
            (phase_section::registered_of(&view.snapshot, "claude"), phase_section::registered_of(&view.snapshot, "codex"))
        };
        let levels: Vec<String> =
            claude.first().and_then(|e| e.supported_effort_levels.clone()).unwrap_or_default().into_iter().flatten().collect();
        if claude.len() != 1 || !claude[0].supports_effort || levels != ["max"] || !codex.is_empty() {
            return Err(format!("the registration was not saved: {claude:?}"));
        }
        models::unregister(host, "claude", "registered-phase-fixture")?;
        if !phase_section::registered_of(&host.view().snapshot, "claude").is_empty() {
            return Err("the registration was not removed".into());
        }
        Ok(())
    };
    let outcome = run();
    let _ = host.view().update(|state| {
        state.phase_models = saved.0;
        state.model_defaults = saved.1;
    });
    match outcome {
        Ok(()) => json!({"selectionsAndRegistrationPersisted": true}),
        Err(error) => json!({"selectionsAndRegistrationPersisted": false, "error": error}),
    }
}

/// The step's host side, for its probe: `state` (the theme, the saved category, whether Settings and a dialog are open),
/// `terminalHistory`, `modelDialog` and `modelDialogState`, `live` (the live keys). Smoke only.
#[tauri::command]
pub async fn smoke_settings(action: String, args: Value, host: tauri::State<'_, Arc<Host>>) -> Result<Value, String> {
    let _ = args;
    if host.smoke.is_none() {
        return Err("smoke only".into());
    }
    match action.as_str() {
        "state" => {
            let open = crate::native::is_open(&host, crate::settings::SURFACE);
            let view = host.view();
            Ok(
                json!({"settingsPane": view.snapshot.settings_pane, "theme": view.snapshot.theme(), "settingsOpen": open, "dialogOpen": view.dialog_open}),
            )
        }
        "terminalHistory" => Ok(open_terminal_history(&host)),
        "modelDialog" => {
            open_model_dialog(&host);
            Ok(Value::Null)
        }
        "modelDialogState" => {
            let dialog = model();
            let dialog = dialog.as_ref();
            Ok(json!({
                "text": dialog.and_then(|d| d.text.as_ref()).map(Local::get),
                "note": dialog.and_then(|d| d.note.as_ref()).map(Local::get).unwrap_or_default(),
                "answer": dialog.and_then(|d| d.answer),
            }))
        }
        "live" => Ok(live(&host).await),
        other => Err(format!("unknown smoke action {other}")),
    }
}
