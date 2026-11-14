//! The CLI 업데이트 box and the plugin window as the page reads them (mighty-updates' `view` through mighty-native's reader): the
//! automation ids, rows and words C#'s builders give, the controls' handlers, and the read-only guard over what is drawn — the
//! Core-level part of `WindowIsARealWinUISurfaceWiredIntoTheApp` and `WindowIsWiredIntoTheRunningAppAndTheSmokeRun` (the running
//! window is the smoke's, smoke_updates.rs), and of `RunSettingsSectionsSmoke`'s fixture rows.

mod common;

use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};

use common::*;
use mighty_native::surface::{SurfaceModel, Surfaces};
use mighty_native::ui::{Content, El, Env, Locals, Ui};
use mighty_native::view::{ChromeView, Place, View};
use mighty_updates::cli_update::{CliUpdateResult, strings};
use mighty_updates::plugins::smoke::{claude_snapshot, codex_snapshot};
use mighty_updates::plugins::strings::{codex as codex_strings, plugin as plugin_strings};
use mighty_updates::plugins::{
    ClaudePluginBrowser, ClaudePluginOperationResult, ClaudePluginSnapshot, PluginAutoUpdateResult, status, support,
};
use mighty_updates::view::{self, CliUpdateActions, CliUpdateBox, PluginActions};
use serde_json::Value;

fn env() -> Env {
    Env { locale: mighty_core::locale::Locale::new("ko"), ..Env::default() }
}

/// A surface whose dialog is what `build` makes.
struct Built(Box<dyn FnMut(&mut Ui) -> El + Send>);

impl SurfaceModel for Built {
    fn view(&mut self, ui: &mut Ui) -> View {
        let body = (self.0)(ui);
        View::Dialog {
            title: "t".into(),
            chrome: ChromeView { kind: "panel", ..ChromeView::default() },
            content: Content::El(Box::new(body)),
            buttons: vec![],
            width: None,
            height: None,
        }
    }
}

/// Every node of a read tree, depth first.
fn nodes(value: &Value) -> Vec<&Value> {
    let mut all = vec![value];
    for key in ["c", "header"] {
        match value.get(key) {
            Some(Value::Array(children)) => children.iter().for_each(|c| all.extend(nodes(c))),
            Some(child @ Value::Object(_)) => all.extend(nodes(child)),
            _ => {}
        }
    }
    all
}

fn read(surfaces: &Surfaces) -> Value {
    surfaces.forget_sent();
    let push: Value = serde_json::from_str(&surfaces.render("d", &env()).unwrap_or_else(|| panic!("nothing pushed"))).unwrap();
    push["view"]["sections"][0]["node"].clone()
}

fn by_aid<'a>(tree: &'a Value, aid: &str) -> Option<&'a Value> {
    nodes(tree).into_iter().find(|n| n.get("aid").and_then(Value::as_str) == Some(aid))
}

fn aids(tree: &Value) -> Vec<String> {
    nodes(tree).into_iter().filter_map(|n| n.get("aid").and_then(Value::as_str)).map(str::to_string).collect()
}

/// The control inside a node marked with `aid` (its own id, or its first descendant's).
fn control_id(tree: &Value, aid: &str) -> i64 {
    let node = by_aid(tree, aid).unwrap_or_else(|| panic!("no {aid}"));
    nodes(node).into_iter().find_map(|n| n.get("id").and_then(Value::as_i64)).unwrap_or_else(|| panic!("{aid} has no control"))
}

fn act(surfaces: &Surfaces, id: i64, op: &str, value: Value) -> bool {
    surfaces.act("d", id, op, &value).run(|_| {})
}

fn texts(tree: &Value) -> Vec<String> {
    nodes(tree).into_iter().filter(|n| n["t"] == "text").filter_map(|n| n["text"].as_str()).map(str::to_string).collect()
}

#[test]
fn the_cli_update_box_draws_c_sharps_rows_and_its_controls_reach_the_host() {
    korean();
    let toggled = Arc::new(Mutex::new(Vec::<(&str, bool)>::new()));
    let started = Arc::new(AtomicBool::new(false));
    let (clis, plugins, start) = (toggled.clone(), toggled.clone(), started.clone());
    let actions = CliUpdateActions {
        toggle_clis: Arc::new(move |on| clis.lock().unwrap().push(("clis", on))),
        toggle_plugins: Arc::new(move |on| plugins.lock().unwrap().push(("plugins", on))),
        start: Arc::new(move || start.store(true, Ordering::SeqCst)),
    };
    let model = CliUpdateBox {
        auto_update_clis: false,
        auto_update_plugins: true,
        last_run: Some("15:04".into()),
        results: vec![
            CliUpdateResult::new("claude", "updated")
                .with_versions(Some("2.1.270"), Some("2.1.271"))
                .with_detail(strings::detail_updated()),
            CliUpdateResult::new("codex", "failed").with_detail(strings::detail_failed_exit(3)),
        ],
        plugin_results: vec![("claude".into(), PluginAutoUpdateResult::new("succeeded", "detail"))],
        ..CliUpdateBox::default()
    };
    let surfaces = Surfaces::new();
    let (m, a) = (model.clone(), actions.clone());
    surfaces.open("d", Place::Dialog, Box::new(Built(Box::new(move |ui| view::cli_update_box(ui, &m, &a)))));
    let tree = read(&surfaces);
    let ids = aids(&tree);
    for aid in [
        "cli-auto-update",
        "plugin-auto-update",
        "cli-update-progress",
        "cli-update-start",
        "cli-update-result-claude-updated",
        "cli-update-result-codex-failed",
        "plugin-update-result-claude",
    ] {
        assert!(ids.iter().any(|i| i == aid), "{aid} in {ids:?}");
    }
    assert_eq!(by_aid(&tree, "cli-auto-update").unwrap()["on"], false, "the CLI switch is a JSON true only");
    assert_eq!(by_aid(&tree, "plugin-auto-update").unwrap()["on"], true, "plugin updates default on");
    assert_eq!(by_aid(&tree, "cli-update-progress").unwrap()["text"], strings::last_run_template().replace("{time}", "15:04"));
    let words = texts(&tree);
    assert!(
        words.contains(&format!("Claude · {}", strings::status_updated())) && words.contains(&"2.1.270 → 2.1.271".to_string()),
        "{words:?}"
    );
    assert!(words.contains(&format!("Codex · {}", strings::status_failed())), "the failed row");
    assert!(
        words.iter().any(|w| w.starts_with("Claude")
            && w.contains(&strings::status_updated())
            && w != &format!("Claude · {}", strings::status_updated())),
        "the plugin row reads updated"
    );

    assert!(act(&surfaces, control_id(&tree, "cli-auto-update"), "toggle", Value::Null));
    assert!(act(&surfaces, control_id(&tree, "plugin-auto-update"), "toggle", Value::Null));
    assert!(act(&surfaces, control_id(&tree, "cli-update-start"), "invoke", Value::Null));
    assert_eq!(*toggled.lock().unwrap(), vec![("clis", true), ("plugins", false)], "each switch writes its own preference");
    assert!(started.load(Ordering::SeqCst), "업데이트 하기 starts the manual run");

    // While a run is on: the button reads 업데이트 중… and refuses, the words are the progress.
    let running = CliUpdateBox { any_running: true, automatic_provider: Some("codex".into()), ..model };
    let surfaces = Surfaces::new();
    surfaces.open("d", Place::Dialog, Box::new(Built(Box::new(move |ui| view::cli_update_box(ui, &running, &CliUpdateActions::none())))));
    let tree = read(&surfaces);
    let button = by_aid(&tree, "cli-update-start").unwrap();
    assert_eq!((button["label"].as_str(), button.get("disabled")), (Some(strings::updating_button().as_str()), Some(&Value::Bool(true))));
    assert_eq!(
        by_aid(&tree, "cli-update-progress").unwrap()["text"],
        mighty_run::strings::get_with("settings.cliUpdate.progressProviderTemplate", &[("provider", mighty_run::providers::name("codex"))])
    );
}

/// `RunSettingsSectionsSmoke`'s read-back: the box built over the fixture results shows each, in order.
#[test]
fn the_box_shows_every_fixture_status_in_order() {
    korean();
    let results = vec![
        CliUpdateResult::new("claude", "updated"),
        CliUpdateResult::new("codex", "current"),
        CliUpdateResult::new("gemini", "skipped"),
        CliUpdateResult::new("claude", "failed"),
    ];
    let model = CliUpdateBox { results, ..CliUpdateBox::default() };
    let (env, mut locals) = (env(), Locals::default());
    let built = view::cli_update_box(&Ui::new(&env, &mut locals), &model, &CliUpdateActions::none());
    assert_eq!(view::shown_statuses(&built), ["updated", "current", "skipped", "failed"]);
}

struct Window {
    surfaces: Surfaces,
    browser: Arc<ClaudePluginBrowser>,
    installs: Arc<Mutex<Vec<String>>>,
    reloads: Arc<Mutex<usize>>,
    refreshes: Arc<Mutex<usize>>,
}

fn window(provider: &str, snapshot: ClaudePluginSnapshot) -> Window {
    let browser = Arc::new(ClaudePluginBrowser::new(provider, workspace(&text(&temp("view-plugins")))));
    browser.apply(snapshot);
    let (installs, reloads, refreshes) = (Arc::new(Mutex::new(vec![])), Arc::new(Mutex::new(0)), Arc::new(Mutex::new(0)));
    let (i, r, f) = (installs.clone(), reloads.clone(), refreshes.clone());
    let actions = PluginActions {
        reload: Arc::new(move || *r.lock().unwrap() += 1),
        install: Arc::new(move |id| i.lock().unwrap().push(id)),
        refresh: Arc::new(move || *f.lock().unwrap() += 1),
    };
    let surfaces = Surfaces::new();
    let b = browser.clone();
    surfaces.open(
        "d",
        Place::Dialog,
        Box::new(Built(Box::new(move |ui| {
            // The body, then the header's tools, the footer's status and action, as the panel's parts.
            let parts = vec![
                view::plugin_body(ui, &b, &actions),
                view::plugin_tools(ui, &b, &actions),
                view::plugin_status(ui, &b),
                view::plugin_actions(ui, &b),
            ];
            El::vstack(parts)
        }))),
    );
    Window { surfaces, browser, installs, reloads, refreshes }
}

fn rows(tree: &Value, provider: &str) -> usize {
    aids(tree).iter().filter(|a| a.starts_with(&support::automation_id(provider, "row-"))).count()
}

#[test]
fn the_claude_window_draws_tabs_filters_rows_and_only_the_macs_changes() {
    korean();
    let w = window("claude", claude_snapshot());
    let tree = read(&w.surfaces);
    let id = |part: &str| support::automation_id("claude", part);
    assert_eq!(by_aid(&tree, &id("tab-installed")).map(|n| texts(n).concat()), Some(format!("{} 2", plugin_strings::tab_installed())));
    assert_eq!(rows(&tree, "claude"), 2, "the installed rows");
    assert!(texts(&tree).contains(&plugin_strings::footer_note()), "Claude's footer");
    assert!(texts(&tree).contains(&"2.1.271 (smoke fixture)".to_string()), "the CLI's version among the tools");
    assert!(by_aid(&tree, &id("scope-picker")).is_none(), "the scope belongs to the marketplace tab");
    assert!(by_aid(&tree, &id("cancel-operation")).is_none(), "no cancel while nothing runs");

    // The marketplace tab: an install button per row, the scope picker and the refresh.
    assert!(act(&w.surfaces, control_id(&tree, &id("tab-marketplace")), "invoke", Value::Null));
    assert_eq!(w.browser.tab(), ClaudePluginBrowser::MARKETPLACE_TAB);
    let tree = read(&w.surfaces);
    assert_eq!(rows(&tree, "claude"), 3);
    let changing: Vec<String> = aids(&tree).into_iter().filter(|a| support::names_a_change(a, "claude")).collect();
    assert_eq!(changing.len(), 5, "scope-picker, refresh-marketplaces and three installs: {changing:?}");
    assert!(changing.iter().all(|a| support::names_allowed_change(a, "claude")), "only the Mac's changes");
    let scope = by_aid(&tree, &id("scope-picker")).unwrap();
    assert_eq!(scope["items"].as_array().map(Vec::len), Some(3), "Claude's three scopes");

    // The filter and the search narrow the rows; the scope picker sets the scope.
    let filter = control_id(&tree, &id("marketplace-filter"));
    assert_eq!(by_aid(&tree, &id("marketplace-filter")).unwrap()["items"][0], plugin_strings::filter_all());
    assert!(act(&w.surfaces, filter, "select", Value::from(1)));
    assert_eq!(w.browser.marketplace_filter(), "other");
    assert_eq!(rows(&read(&w.surfaces), "claude"), 1);
    let tree = read(&w.surfaces);
    assert!(act(&w.surfaces, control_id(&tree, &id("search")), "text", Value::from("no-such-plugin-zz")));
    let tree = read(&w.surfaces);
    assert_eq!(rows(&tree, "claude"), 0);
    assert!(texts(&tree).contains(&plugin_strings::empty_filtered()));
    assert!(act(&w.surfaces, control_id(&tree, &id("search")), "text", Value::from("")));
    assert!(act(&w.surfaces, control_id(&tree, &id("marketplace-filter")), "select", Value::from(0)));
    let tree = read(&w.surfaces);
    assert!(act(&w.surfaces, control_id(&tree, &id("scope-picker")), "select", Value::from(2)));
    assert_eq!(w.browser.scope(), "user");

    // Install, refresh and reload reach the host's actions with the row's id.
    let tree = read(&w.surfaces);
    assert!(act(&w.surfaces, control_id(&tree, &id("install-docs@sample")), "invoke", Value::Null));
    assert!(act(&w.surfaces, control_id(&tree, &id("refresh-marketplaces")), "invoke", Value::Null));
    assert!(act(&w.surfaces, control_id(&tree, &id("reload")), "invoke", Value::Null));
    assert_eq!(
        (w.installs.lock().unwrap().clone(), *w.refreshes.lock().unwrap(), *w.reloads.lock().unwrap()),
        (vec!["docs@sample".to_string()], 1, 1)
    );
    assert_eq!(
        by_aid(&tree, &id("install-fmt@sample")).unwrap()["label"],
        plugin_strings::tab_installed(),
        "fmt is installed for every project"
    );
}

#[test]
fn a_read_that_failed_says_so_and_keeps_its_output_behind_the_disclosure() {
    korean();
    let failed = ClaudePluginSnapshot {
        diagnostic_output: "boom".into(),
        ..ClaudePluginSnapshot::of(status::FAILED, plugin_strings::detail_listing_failed())
    };
    let w = window("claude", failed);
    let id = |part: &str| support::automation_id("claude", part);
    let tree = read(&w.surfaces);
    assert_eq!(by_aid(&tree, &id("load-status")).unwrap()["text"], plugin_strings::detail_listing_failed());
    assert!(texts(&tree).contains(&plugin_strings::empty_failed()), "the reload copy stands in for the rows");
    assert!(!texts(&tree).contains(&"boom".to_string()), "the output waits behind its disclosure");
    assert!(act(&w.surfaces, control_id(&tree, &id("diagnostics")), "invoke", Value::Null));
    assert!(texts(&read(&w.surfaces)).contains(&"boom".to_string()), "opened, the output shows");
}

#[test]
fn the_codex_window_has_its_footer_one_scope_and_the_git_only_sentence() {
    korean();
    let w = window("codex", codex_snapshot());
    let id = |part: &str| support::automation_id("codex", part);
    let tree = read(&w.surfaces);
    assert_eq!(rows(&tree, "codex"), 1, "the project row is never drawn under Codex");
    assert!(texts(&tree).contains(&codex_strings::footer_note()));
    assert_eq!(by_aid(&tree, &id("load-status")).unwrap()["text"], codex_strings::detail_ready(), "Codex keeps its ready sentence");
    w.browser.set_tab(ClaudePluginBrowser::MARKETPLACE_TAB);
    let tree = read(&w.surfaces);
    let changing: Vec<String> = aids(&tree).into_iter().filter(|a| support::names_a_change(a, "codex")).collect();
    assert_eq!(changing.len(), 4, "{changing:?}");
    assert_eq!(by_aid(&tree, &id("scope-picker")).unwrap()["items"].as_array().map(Vec::len), Some(1));
    w.browser.apply(ClaudePluginSnapshot::of(status::READY, codex_strings::detail_no_marketplaces()));
    let tree = read(&w.surfaces);
    assert_eq!(by_aid(&tree, &id("marketplace-help")).unwrap()["text"], codex_strings::marketplace_help());
    assert_eq!(by_aid(&tree, &id("marketplace-unavailable")).unwrap()["text"], codex_strings::refresh_git_only());
}

#[test]
fn a_running_change_shows_its_progress_and_cancel_and_its_result_after() {
    korean();
    let w = window("claude", claude_snapshot());
    let id = |part: &str| support::automation_id("claude", part);
    w.browser.begin_load();
    let tree = read(&w.surfaces);
    assert!(nodes(&tree).iter().any(|n| n["t"] == "spinner"), "the ring turns while the list is read");
    assert_eq!(by_aid(&tree, &id("progress")).unwrap()["text"], plugin_strings::progress_loading());
    assert_eq!(by_aid(&tree, &id("reload")).and_then(|n| n.get("disabled")), Some(&Value::Bool(true)), "no reload while one runs");
    w.browser.apply(claude_snapshot());
    w.browser.show_result(ClaudePluginOperationResult::new(status::SUCCEEDED, plugin_strings::install_succeeded()));
    let tree = read(&w.surfaces);
    assert_eq!(by_aid(&tree, &id("operation-result")).unwrap()["text"], plugin_strings::install_succeeded());
    assert!(by_aid(&tree, &id("progress")).is_none() && by_aid(&tree, &id("operation-progress")).is_none(), "idle: no progress");
}
