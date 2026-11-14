//! The smoke's settings steps (C# MainWindow.WebShell.Settings.cs:348-473 `RunWebSettingsSmoke`, MainWindow.WebShell.LiveSmoke.cs).
//! Owner: W6-A.
//!
//! - The sidebar's gear opens Settings (`settingsOpened`); every category is clicked in its side list and shows each of
//!   `SettingsGroups`' headings while the host saved it as `SettingsPane` (`settingsCategories`); in every category each box shows
//!   something and the category at least one control (`settingsCategoryControls`).
//! - The theme picker (`[data-aid=settings-theme]`) switches the theme, the state and the page follow, and back (`themeSwitched`,
//!   `themeRestored`); the footer's ✕ closes Settings and the window holds no dialog (`settingsClosed`).
//! - The terminal history panel over a shell pane holding "fixture answer 2", closed with its ✕ (`terminalHistory`); a model-id-style
//!   dialog: typed, refused with its note, accepted (`modelDialog`).
//! - The live keys (C# `RunWebLiveSmoke`): `settingsSections` (the headings the page showed, in its order, and D's CLI update rows, the
//!   auto-update switch flipped and restored, in memory), `phaseModelsSection`, `phaseModelsActions` (the rendered controls clicked),
//!   `completionNotification` (skipped as unsupported while the app may not change the machine, else sent).
//! - `localeKeyLeakScan` (C# `RunSettingsLeakScan`), run by smoke.rs as the smoke finishes, after every other step: every W6 box's
//!   words as the page would get them (each category's boxes and the setup list), at least 50, none a raw locale key.

use std::sync::{Arc, Mutex};

use mighty_core::settings_sections::{self, available};
use mighty_core::wire;
use mighty_native::surface::{DialogButton, DialogResult, DialogSpec, SurfaceModel, Surfaces};
use mighty_native::ui::{Content, El, Local, Locals, Ui};
use mighty_native::view::{Place, Section, View};
use mighty_run::model::{ModelCatalog, ModelOption};
use mighty_run::phase_routing::Phase;
use mighty_run::phase_section;
use serde_json::{Value, json};

use crate::host::Host;

/// The settings step's probe; `__CATEGORIES__` is filled in with each category's id, its boxes' headings and whether its controls are
/// required (every category's are).
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
      sections: [...(f?.querySelectorAll('[data-testid=native-section]') ?? [])].map((s) => ({ title: s.dataset.title ?? null, heading: s.querySelector('.native-section-title')?.textContent ?? '', words: s.querySelector('.native-section-title')?.childNodes[0]?.textContent ?? '', controls: s.querySelectorAll('[data-native-id]').length, nodes: s.querySelectorAll('.native-node').length })),
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
    // The headings as the page showed them, each category's in its own order (the beta badge left out).
    const headings = Object.values(report.categories).map((c) => (c.form?.sections ?? []).map((s) => s.words).filter((w) => w));
    report.live = await invoke('smoke_settings', { action: 'live', args: { headings } });
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
            json!({"id": c.id, "titles": titles, "controls": true})
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
        ("phaseModelsActions", live.pointer("/phaseModelsActions/selectionsAndRegistration") == Some(&Value::Bool(true))),
        ("completionNotification", {
            let outcome = live.get("completionNotification");
            let field = |value: Option<&Value>, key: &str| value.and_then(|v| v.get(key)).cloned();
            let expected = outcome.and_then(|o| o.get("expected"));
            expected.is_some()
                && field(outcome, "status") == field(expected, "status")
                && field(expected, "reason").is_none_or(|reason| field(outcome, "reason") == Some(reason))
        }),
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
    let (text, note) = (Local::new(String::new()), Local::new(String::new()));
    let spec = model_dialog(host.locale, text.clone(), note.clone());
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

/// The model-id-style dialog over its words `text` and its note `note` (no I/O).
fn model_dialog(locale: mighty_core::locale::Locale, text: Local<String>, note: Local<String>) -> DialogSpec {
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
    spec
}

/// The terminal history over a shell pane holding "fixture request 2" and "fixture answer 2" (C#'s smoke pane's last record, as its
/// panel shows it): the panel opens on its own task.
fn open_terminal_history(host: &Arc<Host>) -> Value {
    let pane = mighty_core::model::RunSession {
        id: Some("smoke-history-pane".into()),
        kind: Some("shell".into()),
        title: Some("Smoke shell".into()),
        logs: Some(vec![
            mighty_core::model::LogEntry::new("l1".into(), "user", "fixture request 2".into(), "2026-11-14T00:00:00Z".into(), None),
            mighty_core::model::LogEntry::new("l2".into(), "assistant", "fixture answer 2".into(), "2026-11-14T00:00:01Z".into(), None),
        ]),
        ..mighty_core::model::RunSession::default()
    };
    let title = mighty_terminal::history::terminal_history(&pane, &host.locale).title;
    let host = host.clone();
    tauri::async_runtime::spawn(async move { crate::terminal::show_history(&host, &pane).await });
    json!({"title": title})
}

/// The registered headings as the page showed them (C# reads them off the tree it built): when every category showed its boxes'
/// headings in `SettingsGroups`' order (`groups`, the page's categories in turn) and every registered heading once, the registered
/// list itself (C# builds it as one list; the page spreads it over categories); else the registered headings the page showed, in its
/// order, which `smoke_run` then refuses with them in its words.
fn shown_titles(registered: &[String], groups: &[Vec<String>], headings: &[Vec<String>]) -> Vec<String> {
    let shown: Vec<String> = headings.iter().flatten().filter(|t| registered.contains(t)).cloned().collect();
    let each_once = registered.iter().all(|r| shown.iter().filter(|t| *t == r).count() == 1);
    if each_once && groups == headings { registered.to_vec() } else { shown }
}

/// The live keys (C# `RunWebLiveSmoke`'s settings steps); `headings` are each category's box headings as the page showed them.
async fn live(host: &Arc<Host>, headings: &[Vec<String>]) -> Value {
    let locale = host.locale;
    // settingsSections: the registered slots' headings as the page showed them (`shown_titles`), D's CLI update rows over the
    // fixture, the auto-update switch flipped, read back and restored (in memory: a smoke writes no state).
    let registered = settings_sections::windows_titles(&locale);
    let groups: Vec<Vec<String>> =
        available(&locale).iter().map(|c| crate::settings::groups(&locale, c.id).into_iter().map(|g| g.title).collect()).collect();
    let shown = shown_titles(&registered, &groups, headings);
    let (expected, rendered) = crate::updates::section_fixture_statuses(host);
    let sections = settings_sections::smoke_run(
        &locale,
        &shown,
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
    let actions = phase_actions(host).await;
    // completionNotification: what the run must record is known before it runs (C#'s CI records the skip; a send that fails fails).
    let expected = if host.may_change_machine() {
        json!({"status": "sent"})
    } else {
        json!({"status": "skipped", "reason": mighty_core::notifications::UNSUPPORTED_REASON})
    };
    let mut notification = match crate::notifications::smoke(host).await {
        Ok(outcome) => serde_json::from_str::<Value>(&outcome.to_json()).unwrap_or(Value::Null),
        Err(error) => json!({"status": "failed", "error": error}),
    };
    if let Some(fields) = notification.as_object_mut() {
        fields.insert("expected".into(), expected);
    }
    json!({
        "settingsSections": sections,
        "phaseModelsSection": failed.is_empty(),
        "phaseModelsSectionFailed": failed,
        "phaseModelsActions": actions,
        "completionNotification": notification,
    })
}

/// The models box as Settings shows it, on a surface of the smoke's own: its rendered controls are what the smoke clicks.
struct ModelsBox(Arc<Host>);

impl SurfaceModel for ModelsBox {
    fn view(&mut self, ui: &mut Ui) -> View {
        let body = crate::settings::models::phase_models(&self.0, ui);
        View::Settings {
            title: String::new(),
            close: String::new(),
            nav: vec![],
            selected: "models".into(),
            sections: vec![Section { title: None, beta: false, node: Some(ui.section_body(body)) }],
            width: 1.0,
            height: 1.0,
        }
    }
}

/// Every object in `value`, depth first.
fn objects<'a>(value: &'a Value, out: &mut Vec<&'a Value>) {
    match value {
        Value::Object(map) => {
            out.push(value);
            map.values().for_each(|v| objects(v, out));
        }
        Value::Array(items) => items.iter().for_each(|v| objects(v, out)),
        _ => {}
    }
}

/// The smoke's hands on [`ModelsBox`]: it reads the box as the page would get it and sends inputs to the controls it shows, by their
/// automation ids, as `nativeAction` does.
struct Hands {
    surfaces: Surfaces,
    env: mighty_native::ui::Env,
}

impl Hands {
    const VIEW: &'static str = "smoke-phase-models";

    fn new(host: &Arc<Host>) -> Hands {
        let surfaces = Surfaces::new();
        surfaces.open(Self::VIEW, Place::Surface, Box::new(ModelsBox(host.clone())));
        Hands { surfaces, env: crate::native::env(host) }
    }

    /// The box read now, as its push.
    fn read(&self) -> Value {
        self.surfaces.forget_sent();
        self.surfaces.render(Self::VIEW, &self.env).and_then(|push| serde_json::from_str(&push).ok()).unwrap_or(Value::Null)
    }

    /// The node shown with automation id `aid`.
    fn node(&self, aid: &str) -> Option<Value> {
        let read = self.read();
        let mut all = vec![];
        objects(&read, &mut all);
        all.into_iter().find(|n| n.get("aid").and_then(Value::as_str) == Some(aid)).cloned()
    }

    /// The selected item's words of the pop-up `aid`.
    fn selected(&self, aid: &str) -> Option<String> {
        let node = self.node(aid)?;
        Some(node["items"][node["index"].as_u64()? as usize].as_str()?.to_string())
    }

    /// The words shown under `aid` (none when it is not shown).
    fn words(&self, aid: &str) -> Option<String> {
        let node = self.node(aid)?;
        let mut all = vec![];
        objects(&node, &mut all);
        Some(all.iter().filter_map(|n| n.get("text").and_then(Value::as_str)).collect::<Vec<_>>().join(" "))
    }

    /// `op` with `value` on the first control in the node `aid` (the node itself or one inside it), run as `nativeAction` runs it.
    fn act(&self, aid: &str, op: &str, value: Value) -> Result<(), String> {
        let node = self.node(aid).ok_or_else(|| format!("{aid} is not shown"))?;
        let mut all = vec![];
        objects(&node, &mut all);
        let id = all.iter().find_map(|n| n.get("id").and_then(Value::as_i64)).ok_or_else(|| format!("{aid} has no control"))?;
        if self.surfaces.act(Self::VIEW, id, op, &value).run(|_| {}) { Ok(()) } else { Err(format!("{aid} refused {op}")) }
    }
}

/// Polls `test` every 50 ms for up to 10 s.
async fn until(mut test: impl FnMut() -> bool) -> bool {
    for _ in 0..200 {
        if test() {
            return true;
        }
        tokio::time::sleep(std::time::Duration::from_millis(50)).await;
    }
    test()
}

/// The smoke's catalogs (C#'s `claudeCatalog` and `codexCatalog`): Claude's Sonnet alias resolving to a concrete version with its
/// effort levels, a Codex model with two.
fn fixture_catalogs() -> std::collections::HashMap<String, ModelCatalog> {
    let option = |value: &str, name: &str, resolved: Option<&str>, levels: Option<&[&str]>| ModelOption {
        resolved_model: resolved.map(str::to_string),
        supports_effort: resolved.map(|_| true),
        supported_effort_levels: levels.map(|l| l.iter().map(|s| s.to_string()).collect()),
        ..ModelOption::new(value, name, "")
    };
    let claude = ModelCatalog::new(
        "fixture",
        vec![
            option("default", "Default", None, None),
            option("sonnet", "Sonnet", Some("claude-sonnet-fixture"), Some(&["low", "high", "max"])),
        ],
        "",
    );
    let codex = ModelCatalog::new(
        "fixture",
        vec![option("default", "Default", None, None), option("codex-fixture", "Codex fixture", None, Some(&["high", "xhigh"]))],
        "",
    );
    [("claude".to_string(), claude), ("codex".to_string(), codex)].into_iter().collect()
}

/// C# `CheckPhaseModelsSettingsActions`: the rendered box's own controls clicked — Claude's execution model, Codex's sub-agent effort,
/// a registration typed, its effort and level switched, added and deleted — each change checked in the state and in the box read
/// again; then the state, the tools, the drafts and the catalogs as they were. In memory (a smoke writes no state).
async fn phase_actions(host: &Arc<Host>) -> Value {
    use crate::settings::models;
    let saved = {
        let view = host.view();
        (view.snapshot.phase_models.clone(), view.snapshot.model_defaults.clone())
    };
    let catalogs = fixture_catalogs();
    let models_before = models::swap_for_smoke(catalogs.clone());
    let outcome = drive_phase_models(host, &catalogs).await;
    models::restore(models_before);
    let _ = host.view().update(|state| {
        state.phase_models = saved.0;
        state.model_defaults = saved.1;
    });
    match outcome {
        Ok(()) => json!({"selectionsAndRegistration": true}),
        Err(error) => json!({"selectionsAndRegistration": false, "error": error}),
    }
}

async fn drive_phase_models(host: &Arc<Host>, catalogs: &std::collections::HashMap<String, ModelCatalog>) -> Result<(), String> {
    let start = mighty_core::model::PhaseModelsSnapshot {
        claude_main: Some("previous-main".into()),
        codex_review_model: Some("review-unchanged".into()),
        ..mighty_run::phase_models::new_snapshot()
    };
    host.view().update(|state| {
        state.phase_models = Some(start.clone());
        state.model_defaults = None;
    })?;
    let hands = Hands::new(host);
    let config = || host.view().snapshot.phase_models.clone().unwrap_or_else(mighty_run::phase_models::new_snapshot);
    // The pickers' options as the box builds them, to pick a row by its value.
    let form = phase_section::form(&start, &phase_section::smoke_fixture_tools(), &|p| catalogs[p].clone(), &|_| vec![]);
    let picker = |provider: &str, phase: Phase, model: bool| {
        let rows = &form.providers.iter().find(|f| f.provider == provider)?.phases;
        let row = rows.iter().find(|r| r.phase == phase)?;
        if model { row.model.clone() } else { row.effort.clone() }
    };
    let option = |picker: &phase_section::PhasePicker, value: &str| {
        picker.options.iter().position(|o| o.value == value).map(|at| (at, picker.options[at].label.clone()))
    };

    // Claude's execution row: the CLI alias and the concrete version both offered; the version picked.
    let execution = picker("claude", Phase::Execution, true).ok_or("no model picker for Claude's execution")?;
    if option(&execution, "sonnet").is_none() {
        return Err("the CLI alias is not offered".into());
    }
    let (at, label) = option(&execution, "claude-sonnet-fixture").ok_or("the concrete version is not offered")?;
    hands.act("phaseModels-model-claude-execution", "select", json!(at))?;
    let aid = "phaseModels-model-claude-execution";
    if !until(|| config().claude_main.as_deref() == Some("claude-sonnet-fixture") && hands.selected(aid).as_deref() == Some(&label)).await {
        return Err(format!("the model row was not saved and shown: {:?}, {:?}", config(), hands.selected(aid)));
    }
    let saved = config();
    if saved.claude_sonnet_alias.as_deref() != Some("claude-sonnet-fixture")
        || saved.codex_review_model.as_deref() != Some("review-unchanged")
    {
        return Err(format!("the Claude row must save its matching knobs and keep Codex's: {saved:?}"));
    }

    // Codex's sub-agent effort.
    let effort = picker("codex", Phase::Subagents, false).ok_or("no effort picker for Codex's sub-agents")?;
    let (at, label) = option(&effort, "xhigh").ok_or("xhigh is not offered")?;
    let aid = "phaseModels-effort-codex-subagents";
    hands.act(aid, "select", json!(at))?;
    if !until(|| config().codex_subagent_effort.as_deref() == Some("xhigh") && hands.selected(aid).as_deref() == Some(&label)).await {
        return Err(format!("the effort was not saved and shown: {:?}", hands.selected(aid)));
    }

    // A registration: typed, its effort switched on (the levels then shown), max chosen, added.
    hands.act("phaseModels-add-claude", "text", json!("registered-phase-fixture"))?;
    hands.act("phaseModels-addEffort-claude", "toggle", Value::Null)?;
    hands.act("phaseModels-addLevel-claude-max", "toggle", Value::Null)?;
    hands.act("phaseModels-addButton-claude", "invoke", Value::Null)?;
    let row = "phaseModels-registered-claude-registered-phase-fixture";
    let registered = |provider: &str| phase_section::registered_of(&host.view().snapshot, provider);
    if !until(|| registered("claude").iter().any(|e| e.name.as_deref() == Some("registered-phase-fixture")) && hands.node(row).is_some())
        .await
    {
        let refusal = hands.words("phaseModels-error-claude").unwrap_or_default();
        return Err(format!("the add did not save the model and show its row: {refusal}"));
    }
    let claude = registered("claude");
    let levels: Vec<String> =
        claude.first().and_then(|e| e.supported_effort_levels.clone()).unwrap_or_default().into_iter().flatten().collect();
    if claude.len() != 1 || !claude[0].supports_effort || levels != ["max"] || !registered("codex").is_empty() {
        return Err(format!("the registration must keep its provider and capabilities: {claude:?}"));
    }
    if hands.node("phaseModels-add-claude").and_then(|n| n.get("text").cloned()).is_some_and(|t| t != "") {
        return Err("the add field kept its words after the add".into());
    }

    // Its delete button.
    hands.act(row, "invoke", Value::Null)?;
    if !until(|| registered("claude").is_empty() && hands.node(row).is_none()).await {
        return Err("the delete did not remove the model and its row".into());
    }
    Ok(())
}

/// Every W6 box at once, for the leak scan: each available category's boxes (`SettingsGroups`) and the setup screen's list, as
/// Settings would show them now.
struct EveryBox(Arc<Host>);

impl SurfaceModel for EveryBox {
    fn view(&mut self, ui: &mut Ui) -> View {
        let locale = ui.locale();
        let mut sections: Vec<Section> = available(&locale)
            .into_iter()
            .flat_map(|c| crate::settings::groups(&locale, c.id))
            .map(|group| {
                let body = (group.build)(&self.0, ui);
                Section { title: Some(group.title), beta: group.beta, node: Some(ui.section_body(body)) }
            })
            .collect();
        let setup = crate::setup::list(&self.0, ui);
        sections.push(Section { title: Some(locale.get("setup.title")), beta: false, node: Some(ui.section_body(setup)) });
        View::Settings {
            title: locale.get("settings.title"),
            close: locale.get("settings.closeButton"),
            nav: vec![],
            selected: String::new(),
            sections,
            width: 1.0,
            height: 1.0,
        }
    }
}

/// The words a push shows (C# `CollectVisibleStrings`: a TextBlock's text and runs, a TextBox's text, a control's words, a tooltip;
/// here also a header, a placeholder and a pop-up's items), depth first.
fn visible_strings(value: &Value, out: &mut Vec<String>) {
    const WORDS: [&str; 11] =
        ["text", "label", "help", "header", "placeholder", "title", "subtitle", "close", "message", "tooltip", "value"];
    match value {
        Value::Object(map) => {
            for (key, value) in map {
                match value {
                    Value::String(words) if WORDS.contains(&key.as_str()) && !words.is_empty() => out.push(words.clone()),
                    Value::Array(items) if key == "items" || key == "options" => {
                        for item in items {
                            match item {
                                Value::String(words) if !words.is_empty() => out.push(words.clone()),
                                other => visible_strings(other, out),
                            }
                        }
                    }
                    other => visible_strings(other, out),
                }
            }
        }
        Value::Array(items) => items.iter().for_each(|v| visible_strings(v, out)),
        _ => {}
    }
}

/// C# `RunSettingsLeakScan`, the smoke's last step: every W6 box's words as the page would get them, none of them a raw locale key
/// (`LocaleKeyLeak.Detect`). `{scanned, leaks}`, as C# reports it.
pub fn leak_scan(host: &Arc<Host>) -> Value {
    const VIEW: &str = "smoke-leak-scan";
    let surfaces = Surfaces::new();
    surfaces.open(VIEW, Place::Surface, Box::new(EveryBox(host.clone())));
    let push = surfaces.render(VIEW, &crate::native::env(host)).and_then(|push| serde_json::from_str::<Value>(&push).ok());
    let mut strings = vec![];
    if let Some(push) = &push {
        visible_strings(push, &mut strings);
    }
    let keys = mighty_core::locale::keys();
    let leaks = mighty_core::locale::key_leaks(strings.iter().map(String::as_str), &keys);
    json!({"scanned": strings.len(), "leaks": leaks})
}

/// The leak scan's check: at least 50 strings read, none a key.
pub fn leak_scan_passed(scan: &Value) -> bool {
    scan.get("scanned").and_then(Value::as_u64).unwrap_or(0) >= 50 && scan.get("leaks").and_then(Value::as_array).is_some_and(Vec::is_empty)
}

/// The step's host side, for its probe: `state` (the theme, the saved category, whether Settings and a dialog are open),
/// `terminalHistory`, `modelDialog` and `modelDialogState`, `live` (the live keys, over `args.headings`). Smoke only.
#[tauri::command]
pub async fn smoke_settings(action: String, args: Value, host: tauri::State<'_, Arc<Host>>) -> Result<Value, String> {
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
        "live" => {
            let words = |list: &Value| -> Vec<String> {
                list.as_array().into_iter().flatten().filter_map(Value::as_str).map(str::to_string).collect()
            };
            let headings: Vec<Vec<String>> = args.get("headings").and_then(Value::as_array).into_iter().flatten().map(words).collect();
            Ok(live(&host, &headings).await)
        }
        other => Err(format!("unknown smoke action {other}")),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Review L7: the settingsSections order is read off the page's headings: categories showing their boxes in `SettingsGroups`'
    /// order pass (Windows' CLI tab puts CLI Update last, as C# does); a heading dropped, shown twice or out of its category's order
    /// fails `smoke_run`.
    /// The leak scan reads the words a push shows (texts, labels, headers, placeholders, a pop-up's items) and not its ids, inks or
    /// automation ids; a key among them fails it, as do fewer than 50 strings.
    #[test]
    fn the_leak_scan_reads_the_shown_words() {
        let push = json!({"kind": "settings", "title": "설정", "sections": [{"title": "화면", "node": {"t": "stack", "c": [
            {"t": "text", "text": "settings.text.sectionTitle", "ink": "#FFFFFF"},
            {"t": "combo", "id": 3, "aid": "settings-theme", "items": ["다크", "라이트"], "header": "테마"},
            {"t": "field", "placeholder": "모델 ID", "text": ""}
        ]}}]});
        let mut strings = vec![];
        visible_strings(&push, &mut strings);
        assert_eq!(strings, ["설정", "화면", "settings.text.sectionTitle", "다크", "라이트", "테마", "모델 ID"]);
        let keys = mighty_core::locale::keys();
        let leaks = mighty_core::locale::key_leaks(strings.iter().map(String::as_str), &keys);
        assert_eq!(leaks, ["settings.text.sectionTitle"]);
        assert!(!leak_scan_passed(&json!({"scanned": 60, "leaks": leaks})));
        assert!(!leak_scan_passed(&json!({"scanned": 49, "leaks": []})));
        assert!(leak_scan_passed(&json!({"scanned": 50, "leaks": []})));
    }

    /// The model-id-style dialog against C#'s capture (`mighty-native/tests/golden/model-dialog.json`, plan W6 §1.8): as the page
    /// first got it, the field empty.
    #[test]
    fn the_model_dialog_matches_cs() {
        let spec = model_dialog(mighty_core::locale::Locale::new("ko"), Local::new(String::new()), Local::new(String::new()));
        let golden = include_str!("../../crates/mighty-native/tests/golden/model-dialog.json");
        assert_eq!(crate::native::golden_dialog(spec), golden.trim_end());
    }

    #[test]
    fn the_section_headings_come_from_the_page() {
        let locale = mighty_core::locale::Locale::new("en");
        let registered = settings_sections::windows_titles(&locale);
        let groups: Vec<Vec<String>> =
            available(&locale).iter().map(|c| crate::settings::groups(&locale, c.id).into_iter().map(|g| g.title).collect()).collect();
        let check = |headings: Vec<Vec<String>>| {
            let shown = shown_titles(&registered, &groups, &headings);
            let switch = std::cell::Cell::new(None);
            settings_sections::smoke_run(&locale, &shown, &[], &[], || switch.get(), |value| switch.set(value)).is_ok()
        };
        assert!(check(groups.clone()), "the page as Settings builds it");
        let mut dropped = groups.clone();
        dropped.last_mut().unwrap().pop();
        assert!(!check(dropped), "a heading dropped");
        let mut swapped = groups.clone();
        let cli = swapped.iter_mut().find(|g| g.len() == 4).expect("the CLI tab's four boxes");
        cli.swap(0, 3);
        assert!(!check(swapped), "out of its category's order");
        let mut twice = groups.clone();
        twice[0].push(registered[1].clone());
        assert!(!check(twice), "a heading shown twice");
    }
}
