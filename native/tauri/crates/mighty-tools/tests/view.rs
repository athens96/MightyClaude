//! The forms (mighty-tools `view`) read as the page reads them: the setup screen over the smoke's fixed probes (MainWindow.SetupSmoke.cs's
//! rows, ticks, buttons and plan, as the page now sees them), its controls reaching the host's actions through the act table, the
//! confirmation D10, the components box, the toolkit box with its file error, rows and results, and D7. Each is drawn by
//! mighty-native's surfaces exactly as the host pushes it.

mod common;

use std::sync::{Arc, Mutex};

use mighty_core::locale::Locale;
use mighty_core::text_appearance::TextAppearance;
use mighty_native::surface::{DialogSurface, SurfaceModel, Surfaces};
use mighty_native::tokens::Theme;
use mighty_native::ui::{Env, Ui};
use mighty_native::view::{Place, View};
use mighty_run::strings;
use mighty_tools::setup::screen::{self, SetupScreen};
use mighty_tools::toolkit::file::{ToolkitFileEntry, ToolkitInstallSpec};
use mighty_tools::toolkit::runner::{PlanAction, ToolkitPlanItem, ToolkitRunItem, Verdict, presentation};
use mighty_tools::toolkit::store::ToolkitStore;
use mighty_tools::view::{self, SetupActions, SetupExtras, ToolkitActions, ToolkitModel, ToolkitRow};
use serde_json::Value;

fn env() -> Env {
    Env { theme: Theme::Dark, text: TextAppearance::default(), locale: Locale::new("ko") }
}

/// What the controls asked the host to do.
#[derive(Default)]
struct Recorded(Mutex<Vec<String>>);

impl Recorded {
    fn said(&self, what: String) {
        self.0.lock().unwrap().push(what);
    }
    fn all(&self) -> Vec<String> {
        self.0.lock().unwrap().clone()
    }
}

impl SetupActions for Recorded {
    fn toggle(&self, id: &str) {
        self.said(format!("toggle {id}"));
    }
    fn sign_in(&self, provider: &str) {
        self.said(format!("sign in {provider}"));
    }
    fn update(&self) {
        self.said("update".into());
    }
    fn open_page(&self, url: &str) {
        self.said(format!("page {url}"));
    }
    fn recheck(&self) {
        self.said("recheck".into());
    }
    fn install(&self) {
        self.said("install".into());
    }
    fn abandon(&self) {
        self.said("abandon".into());
    }
}

impl ToolkitActions for Recorded {
    fn add(&self) {
        self.said("add".into());
    }
    fn export(&self) {
        self.said("export".into());
    }
    fn import(&self) {
        self.said("import".into());
    }
    fn install(&self) {
        self.said("toolkit install".into());
    }
    fn approve(&self, id: &str) {
        self.said(format!("approve {id}"));
    }
    fn remove(&self, id: &str) {
        self.said(format!("remove {id}"));
    }
}

/// A surface that builds the setup screen from a screen held by the check.
struct SetupModel {
    screen: Arc<Mutex<SetupScreen>>,
    extras: SetupExtras,
    actions: Arc<Recorded>,
    never_show: Arc<Mutex<bool>>,
}

impl SurfaceModel for SetupModel {
    fn view(&mut self, ui: &mut Ui) -> View {
        let list = view::setup_content(ui, &self.screen.lock().unwrap(), &self.extras, self.actions.clone());
        let never = self.never_show.clone();
        view::setup_view(list, *self.never_show.lock().unwrap(), move |on| *never.lock().unwrap() = on)
    }
}

/// A surface that builds a settings box (toned as the settings shell tones a box).
struct BoxModel(Box<dyn FnMut(&mut Ui) -> mighty_native::ui::El + Send>);

impl SurfaceModel for BoxModel {
    fn view(&mut self, ui: &mut Ui) -> View {
        let body = (self.0)(ui);
        View::Companion { title: "box".into(), body: Some(ui.section_body(body)) }
    }
}

/// Every node of a pushed view, depth first.
fn nodes(value: &Value, into: &mut Vec<Value>) {
    match value {
        Value::Object(object) => {
            if object.contains_key("t") {
                into.push(value.clone());
            }
            for child in object.values() {
                nodes(child, into);
            }
        }
        Value::Array(items) => items.iter().for_each(|item| nodes(item, into)),
        _ => {}
    }
}

fn pushed(surfaces: &Surfaces, id: &str) -> Value {
    surfaces.forget_sent();
    serde_json::from_str(&surfaces.render(id, &env()).expect("a push")).unwrap()
}

fn by_aid(view: &Value, aid: &str) -> Option<Value> {
    let mut all = vec![];
    nodes(view, &mut all);
    all.into_iter().find(|node| node.get("aid").and_then(Value::as_str) == Some(aid))
}

fn control_id(node: &Value) -> i64 {
    node["id"].as_i64().unwrap_or_else(|| panic!("a control: {node}"))
}

fn smoke_screen() -> Arc<Mutex<SetupScreen>> {
    let mut screen = SetupScreen::new();
    let catalog = screen.load_catalog().unwrap();
    screen.finish_refresh(screen::smoke_fixed_results(&catalog), None, false, std::time::Instant::now());
    screen.open(Some("agy"));
    screen.preselect_once();
    screen.toggle("gh");
    Arc::new(Mutex::new(screen))
}

/// The setup screen the smoke opens (MainWindow.SetupSmoke.cs): its rows by automation id with Git, the focused Antigravity and
/// the picked gh ticked and glab not, Claude below its minimum offering its update and Tailscale its page, the install button on
/// with a plan, the Mac's tools absent, and the panel's own keys; each control reaches the host's actions.
#[test]
fn the_setup_screen_draws_the_smokes_rows_and_its_controls_reach_the_host() {
    common::korean();
    let actions = Arc::new(Recorded::default());
    let screen = smoke_screen();
    let never_show = Arc::new(Mutex::new(false));
    let surfaces = Surfaces::new();
    let model =
        SetupModel { screen: screen.clone(), extras: SetupExtras::default(), actions: actions.clone(), never_show: never_show.clone() };
    surfaces.open(view::SETUP_SURFACE, Place::Surface, Box::new(model));
    let page = pushed(&surfaces, view::SETUP_SURFACE);
    assert_eq!(page["kind"], "native");
    assert_eq!(page["place"], "surface");
    let v = &page["view"];
    assert_eq!(v["kind"], "setup");
    assert_eq!(v["title"], strings::get("setup.title"));
    assert_eq!(v["subtitle"], strings::get("setup.windows.subtitle"));
    assert_eq!(v["close"], strings::get("setup.button.later"));
    assert_eq!(v["chrome"], serde_json::json!({"kind": "panel", "panel": "setup", "icon": "wrench"}));
    assert!(v["width"] == 620 && v["height"] == 700);
    assert_eq!(v["sections"][0].get("title"), None, "the setup screen's one section has no heading");
    assert_eq!(v["footer"]["t"], "check");
    assert_eq!(v["footer"]["aid"], "setup-never-show");
    assert_eq!(v["footer"]["label"], strings::get("setup.button.neverShow"));
    for id in ["claude", "codex", "agy", "git", "node", "gh", "tailscale", "glab"] {
        assert!(by_aid(v, &format!("setup-row-{id}")).is_some(), "the {id} row");
    }
    for id in ["homebrew", "gstack", "bun"] {
        assert!(by_aid(v, &format!("setup-row-{id}")).is_none(), "the Mac's {id} is not listed");
    }
    let ticked = |id: &str| by_aid(v, &format!("setup-check-{id}")).map(|check| check["on"] == true);
    assert_eq!((ticked("git"), ticked("agy"), ticked("gh"), ticked("glab")), (Some(true), Some(true), Some(true), Some(false)));
    assert_eq!(ticked("codex"), None, "an installed CLI's box is hidden");
    assert_eq!(ticked("node"), Some(false), "a missing Node.js nothing picked needs: its box shows, unticked");
    assert_eq!(ticked("tailscale"), None, "a page is no install: its box is hidden");
    assert!(by_aid(v, "setup-update-claude").is_some() && by_aid(v, "setup-page-tailscale").is_some(), "the update and the page");
    let install = by_aid(v, "setup-install").expect("the install button");
    assert!(install.get("disabled").is_none() && install["accent"] == true, "on with a plan: {install}");
    assert_eq!(by_aid(v, "setup-recheck").unwrap()["label"], strings::get("setup.button.recheck"));
    // Each control's input reaches the host.
    for aid in ["setup-check-glab", "setup-update-claude", "setup-page-tailscale", "setup-recheck", "setup-install"] {
        let node = by_aid(v, aid).unwrap();
        let op = if node["t"] == "check" { "toggle" } else { "invoke" };
        assert!(surfaces.act(view::SETUP_SURFACE, control_id(&node), op, &Value::Null).run(|_| {}), "{aid} takes its input");
    }
    assert_eq!(actions.all(), ["toggle glab", "update", "page https://tailscale.com/download/windows", "recheck", "install"]);
    let never = by_aid(v, "setup-never-show").unwrap();
    surfaces.act(view::SETUP_SURFACE, control_id(&never), "toggle", &Value::Null).run(|_| {});
    assert!(*never_show.lock().unwrap(), "never-show ticks");
    // A running install: the boxes wait, the hint and the ring show; an empty plan turns the install off.
    {
        let mut held = screen.lock().unwrap();
        held.selection.clear();
        held.run = Some(screen::SetupRun {
            items: vec!["gh".into()],
            started: std::env::temp_dir().join("s"),
            done: std::env::temp_dir().join("d"),
            opened: std::time::Instant::now(),
            probed: std::time::Instant::now(),
            finished: false,
            not_started: true,
        });
        held.message = Some("why".into());
    }
    let running = pushed(&surfaces, view::SETUP_SURFACE);
    let v = &running["view"];
    assert_eq!(by_aid(v, "setup-install").unwrap()["disabled"], true);
    assert!(by_aid(v, "setup-run-end").is_some() && by_aid(v, "setup-message").is_some() && by_aid(v, "setup-run-not-started").is_some());
    let mut all = vec![];
    nodes(v, &mut all);
    assert!(all.iter().any(|n| n["t"] == "spinner"), "the ring turns while the install runs");
}

/// The sign-in a signed-out agent CLI offers, and the update that waits while a CLI update runs.
#[test]
fn the_setup_rows_offer_the_sign_in_and_a_waiting_update() {
    common::korean();
    let actions = Arc::new(Recorded::default());
    let extras = SetupExtras { signed_out: ["codex".to_string()].into(), cli_update_running: true, plugin_operations_running: false };
    let surfaces = Surfaces::new();
    let model = SetupModel { screen: smoke_screen(), extras, actions: actions.clone(), never_show: Arc::default() };
    surfaces.open(view::SETUP_SURFACE, Place::Surface, Box::new(model));
    let page = pushed(&surfaces, view::SETUP_SURFACE);
    let v = &page["view"];
    let sign_in = by_aid(v, "setup-signin-codex").expect("a signed-out CLI offers its sign-in");
    assert_eq!(sign_in["label"], strings::get("setup.button.signIn"));
    surfaces.act(view::SETUP_SURFACE, control_id(&sign_in), "invoke", &Value::Null).run(|_| {});
    let update = by_aid(v, "setup-update-claude").unwrap();
    assert!(update["label"] == strings::get("settings.components.inProgressLabel") && update["disabled"] == true, "{update}");
    assert_eq!(actions.all(), ["sign in codex"]);
}

/// D10: the confirmation's title, its preamble and steps by automation id, Run and Cancel with Cancel the default.
#[test]
fn the_setup_confirmation_lists_the_first_lines_and_every_step() {
    common::korean();
    let screen = smoke_screen();
    let confirmation = screen.lock().unwrap().confirmation(&Default::default()).unwrap();
    let surfaces = Surfaces::new();
    surfaces.open("dialog-1", Place::Dialog, Box::new(DialogSurface::new(view::setup_confirmation(&confirmation), |_| {})));
    let page = pushed(&surfaces, "dialog-1");
    let v = &page["view"];
    assert_eq!(v["title"], strings::get("setup.confirm.title"));
    assert_eq!(v["chrome"]["kind"], "sheet");
    let preamble = by_aid(v, "setup-confirm-preamble").expect("the first lines");
    assert!(preamble.to_string().contains("Set-Location -LiteralPath $HOME"));
    for id in ["agy", "git", "gh"] {
        assert!(by_aid(v, &format!("setup-confirm-step-{id}")).is_some(), "the {id} step");
    }
    assert_eq!(
        v["buttons"],
        serde_json::json!([{"id": "close", "label": strings::get("setup.confirm.cancel"), "disabled": false, "accent": true}, {"id": "primary", "label": strings::get("setup.confirm.run"), "disabled": false, "accent": false}])
    );
    assert!(page.to_string().contains("\"maxH\":340"), "the steps scroll past 340");
}

/// The components box: the explanation over the setup list, as two ruled rows.
#[test]
fn the_components_box_hosts_the_setup_list() {
    common::korean();
    let screen = smoke_screen();
    let actions = Arc::new(Recorded::default());
    let surfaces = Surfaces::new();
    let model = BoxModel(Box::new(move |ui| {
        let list = view::setup_content(ui, &screen.lock().unwrap(), &SetupExtras::default(), actions.clone());
        view::components_box(ui, list)
    }));
    surfaces.open("box", Place::Popover, Box::new(model));
    let page = pushed(&surfaces, "box");
    let rows = page["view"]["sections"][0]["node"]["c"].as_array().unwrap().clone();
    assert_eq!(rows.len(), 2);
    assert!(rows.iter().all(|row| row["role"] == "settingsRow"));
    assert_eq!(rows[0]["c"][0]["text"], strings::get("setup.windows.subtitle"));
    assert!(by_aid(&rows[1], "setup-list").is_some() && by_aid(&rows[1], "setup-install").is_some());
}

fn entry(id: &str, install: ToolkitInstallSpec) -> ToolkitFileEntry {
    ToolkitFileEntry::new(id, &format!("{id} tool"), "package", install)
}

/// The toolkit box: the bundled row with its capsule, a user's tool with its approve and remove, the probe's words, the error line
/// only while there is one, the results row once there are results, and the buttons that wait while an install runs.
#[test]
fn the_toolkit_box_draws_its_rows_error_and_results() {
    common::korean();
    let actions = Arc::new(Recorded::default());
    let bundled = ToolkitStore::bundled().remove(0);
    let npm = entry("smoke-npm", ToolkitInstallSpec::Package { manager: "npm".into(), name: "x".into(), executable: None });
    let mut model = ToolkitModel {
        rows: vec![
            ToolkitRow { entry: bundled, approved: false, installed: true },
            ToolkitRow { entry: npm.clone(), approved: false, installed: false },
        ],
        ..ToolkitModel::default()
    };
    let shown = Arc::new(Mutex::new(model.clone()));
    let (state, into) = (shown.clone(), actions.clone());
    let surfaces = Surfaces::new();
    surfaces.open(
        "box",
        Place::Popover,
        Box::new(BoxModel(Box::new(move |ui| view::toolkit_box(ui, &state.lock().unwrap(), into.clone())))),
    );
    let page = pushed(&surfaces, "box");
    let v = &page["view"];
    let rows = v["sections"][0]["node"]["c"].as_array().unwrap().clone();
    assert_eq!(rows.len(), 3, "the hidden error and results rows are dropped: the two tools and the footer");
    assert!(by_aid(v, "toolkit-entry-mighty-styles").unwrap().to_string().contains(&strings::get("settings.toolkit.bundledBadge")));
    assert!(
        by_aid(v, "toolkit-approve-mighty-styles").is_none() && by_aid(v, "toolkit-remove-mighty-styles").is_none(),
        "the bundled tool has no buttons"
    );
    let user = by_aid(v, "toolkit-entry-smoke-npm").unwrap().to_string();
    assert!(
        user.contains(&strings::get("settings.toolkit.needsApproval")) && user.contains(&strings::get("settings.toolkit.statusMissing"))
    );
    for aid in [
        "toolkit-approve-smoke-npm",
        "toolkit-remove-smoke-npm",
        "settings-toolkit-add",
        "settings-toolkit-export",
        "settings-toolkit-import",
        "settings-toolkit-install",
    ] {
        let node = by_aid(v, aid).unwrap_or_else(|| panic!("{aid}"));
        assert!(surfaces.act("box", control_id(&node), "invoke", &Value::Null).run(|_| {}), "{aid}");
    }
    assert_eq!(actions.all(), ["approve smoke-npm", "remove smoke-npm", "add", "export", "import", "toolkit install"]);
    model.error = Some(strings::get("toolkit.error.unreadable"));
    model.running = true;
    model.results = Some(vec![
        ToolkitRunItem { entry_id: "smoke-npm".into(), verdict: Verdict::Installed, steps: vec![] },
        ToolkitRunItem { entry_id: "gone".into(), verdict: Verdict::Failed, steps: vec![] },
    ]);
    *shown.lock().unwrap() = model;
    let page = pushed(&surfaces, "box");
    let v = &page["view"];
    let text = v.to_string();
    assert!(text.contains(&presentation::error_banner(&strings::get("toolkit.error.unreadable"))), "the error line says why");
    let results = by_aid(v, "settings-toolkit-results").expect("the results row").to_string();
    assert!(
        results.contains("smoke-npm tool") && results.contains("gone") && results.contains(&strings::get("settings.toolkit.verdictFailed"))
    );
    let install = by_aid(v, "settings-toolkit-install").unwrap();
    assert!(install["disabled"] == true && install["label"] == strings::get("settings.toolkit.installingButton"), "{install}");
    assert_eq!(by_aid(v, "toolkit-remove-smoke-npm").unwrap()["disabled"], true, "the rows' buttons wait with them");
}

/// D7: the toolkit's confirmation names each tool over every command, a skipped one over its one line.
#[test]
fn the_toolkit_confirmation_names_each_tool_over_its_commands() {
    common::korean();
    let npm = entry("a", ToolkitInstallSpec::Package { manager: "npm".into(), name: "x".into(), executable: None });
    let plan = vec![
        ToolkitPlanItem {
            entry: npm.clone(),
            action: PlanAction::Run,
            commands: vec![vec!["npm".into(), "install".into(), "-g".into(), "x".into()]],
        },
        ToolkitPlanItem { entry: entry("b", npm.install.clone()), action: PlanAction::Skip, commands: vec![] },
    ];
    let surfaces = Surfaces::new();
    surfaces.open(
        "dialog-2",
        Place::Dialog,
        Box::new(DialogSurface::new(view::toolkit_confirmation(presentation::plan_rows(&plan)), |_| {})),
    );
    let page = pushed(&surfaces, "dialog-2");
    let v = &page["view"];
    assert_eq!(v["title"], strings::get("settings.toolkit.confirmTitle"));
    let text = v.to_string();
    assert!(text.contains("npm install -g x") && text.contains("a tool") && text.contains("b tool"));
    assert!(text.contains(&format!("→ {}", strings::get("settings.toolkit.verdictSkipped"))) && text.contains("\"maxH\":300"));
    assert_eq!(v["buttons"][1]["label"], strings::get("settings.toolkit.confirmInstall"));
    assert_eq!(v["buttons"][1]["accent"], false, "no default button");
}
