//! The smoke's settings steps (C# MainWindow.WebShell.Settings.cs:348-473 `RunWebSettingsSmoke`, MainWindow.WebShell.LiveSmoke.cs).
//! Owner: W6-A.
//!
//! The shell (W6-A0): the sidebar's gear opens Settings in the page (`settingsOpened`); every category is clicked in its side list and
//! the page shows it with each of `SettingsGroups`' headings while the host saved it as `SettingsPane` (`settingsCategories`, details
//! under `settings.categories`); the footer's ✕ closes it and the window holds no dialog (`settingsClosed`). Then a model-id-style
//! dialog drawn by the page from the host's spec: the words typed reach the host's field, a refused primary keeps it open with the
//! note, accepted words close it as primary (`modelDialog`). The boxes' own checks (each box shows its rows and a control, the theme
//! switched and restored) and the live keys come with the boxes (W6-A1).

use std::sync::{Arc, Mutex};

use mighty_core::settings_sections::available;
use mighty_core::wire;
use mighty_native::surface::{DialogButton, DialogResult, DialogSpec};
use mighty_native::ui::{Content, El, Local};
use serde_json::{Value, json};

use crate::host::Host;

/// The settings step's probe; `__CATEGORIES__` is filled in with each category's id and its boxes' headings.
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
      const shown = await until(() => { const f = form(); return f && f.category === category.id && category.titles.every((t) => f.sections.some((s) => s.title === t && s.heading.startsWith(t))); }, 10000);
      const state = await host('state');
      report.categories[category.id] = { shown: !!shown, saved: state.settingsPane === category.id, form: form() };
    }
    document.querySelector('[data-native-view=settings] [data-testid=settings-close]')?.click();
    report.closed = !!(await until(async () => { const s = await host('state'); return !s.settingsOpen && !s.dialogOpen && !document.querySelector('[data-native-view=settings]'); }, 15000));
    // A model-id-style dialog: typed, refused with its note, accepted.
    await host('modelDialog');
    const top = () => [...document.querySelectorAll('[data-native-view^=dialog-]')].pop();
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
  } catch (error) {
    report.error = String(error);
  }
  await invoke('smoke_probe', { probe: report });
})();"#;

/// Starts the step: the probe, with each available category's boxes.
pub fn start(host: &Arc<Host>) -> bool {
    let categories: Vec<Value> = available(&host.locale)
        .into_iter()
        .map(
            |c| json!({"id": c.id, "titles": crate::settings::groups(&host.locale, c.id).into_iter().map(|g| g.title).collect::<Vec<_>>()}),
        )
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
    let all_shown = categories.is_some_and(|all| {
        !all.is_empty() && all.values().all(|c| c.get("shown") == Some(&Value::Bool(true)) && c.get("saved") == Some(&Value::Bool(true)))
    });
    vec![
        ("settingsOpened", report.get("error").is_none() && yes("opened")),
        ("settingsCategories", all_shown),
        ("settingsClosed", yes("closed")),
        ("modelDialog", yes("typed") && yes("refused") && yes("accepted")),
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

/// The step's host side, for its probe: `state` (the saved category, whether Settings and a dialog are open), `modelDialog` (opens
/// the dialog), `modelDialogState` (its field's words, its note, its answer). Smoke only.
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
            Ok(json!({"settingsPane": view.snapshot.settings_pane, "settingsOpen": open, "dialogOpen": view.dialog_open}))
        }
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
        other => Err(format!("unknown smoke action {other}")),
    }
}
