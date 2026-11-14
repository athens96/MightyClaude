//! The CLI tab's boxes and dialogs D8 and D9 as the page reads them (C# MainWindow.Settings.cs:145-153, :576-1026), rendered
//! headlessly through mighty-native's reader and surfaces: each box's rows and their automation ids, what a button does, the dialogs'
//! kind, title and buttons. With them the Core half of AcpAgentAccountVerification `TheWindowsAccountsSectionListsEveryProvider`
//! (Verification.cs:1400): the section lists every provider in the provider order and a terminal sign-in's row says what it asks.

mod common;

use std::sync::{Arc, Mutex};

use mighty_accounts::accounts::{self, CliAccountStatus, CliLoginOption, copy};
use mighty_accounts::view::{self, AccountActions, AccountRow, AccountsModel, ProvidersModel};
use mighty_core::locale::Locale;
use mighty_core::text_appearance::TextAppearance;
use mighty_native::surface::{DialogSurface, Next, SurfaceModel, Surfaces};
use mighty_native::tokens::Theme;
use mighty_native::ui::{El, Env, Ui};
use mighty_native::view::{Place, Section, View};
use mighty_run::model::ProviderRuntime;
use mighty_run::strings;
use serde_json::Value;

fn env() -> Env {
    Env { theme: Theme::Dark, text: TextAppearance::default(), locale: Locale::new("ko") }
}

/// A settings surface showing one box.
struct OneBox(Box<dyn FnMut(&mut Ui) -> El + Send>);

impl SurfaceModel for OneBox {
    fn view(&mut self, ui: &mut Ui) -> View {
        let body = (self.0)(ui);
        View::Settings {
            title: "t".into(),
            close: "x".into(),
            nav: vec![],
            selected: "cli".into(),
            sections: vec![Section { title: Some("box".into()), beta: false, node: Some(ui.section_body(body)) }],
            width: 860.0,
            height: 640.0,
        }
    }
    fn button(&mut self, _which: Option<&str>) -> Next {
        Next::Close
    }
}

/// The box rendered as the page reads it.
fn render(build: impl FnMut(&mut Ui) -> El + Send + 'static) -> (Surfaces, Value) {
    let surfaces = Surfaces::new();
    surfaces.open("settings", Place::Surface, Box::new(OneBox(Box::new(build))));
    let pushed = surfaces.render("settings", &env()).expect("a push");
    let view: Value = serde_json::from_str(&pushed).unwrap();
    (surfaces, view["view"]["sections"][0]["node"].clone())
}

/// Every node under `node`, depth first.
fn nodes(node: &Value) -> Vec<Value> {
    let mut all = vec![node.clone()];
    for key in ["c", "items"] {
        if let Some(Value::Array(children)) = node.get(key) {
            for child in children {
                all.extend(nodes(child));
            }
        }
    }
    all
}

fn by_aid(node: &Value, aid: &str) -> Option<Value> {
    nodes(node).into_iter().find(|n| n.get("aid").and_then(Value::as_str) == Some(aid))
}

fn texts(node: &Value) -> Vec<String> {
    nodes(node).into_iter().filter_map(|n| n.get("text").and_then(Value::as_str).map(str::to_string)).collect()
}

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

impl AccountActions for Recorded {
    fn change(&self, provider: &str) {
        self.said(format!("change {provider}"));
    }
    fn logout(&self, provider: &str) {
        self.said(format!("logout {provider}"));
    }
    fn sign_in(&self, provider: &str, option: CliLoginOption) {
        self.said(format!("signIn {provider} {option:?}"));
    }
    fn cancel_wait(&self, provider: &str) {
        self.said(format!("cancelWait {provider}"));
    }
    fn reset_models(&self) {
        self.said("resetModels".into());
    }
    fn reset_bedrock(&self) {
        self.said("resetBedrock".into());
    }
    fn refresh(&self, provider: &str) {
        self.said(format!("refresh {provider}"));
    }
    fn auto_login(&self, on: bool) {
        self.said(format!("autoLogin {on}"));
    }
}

fn rows(statuses: Vec<CliAccountStatus>) -> Vec<AccountRow> {
    statuses.into_iter().map(|status| AccountRow { status, pending: false, message: None, working: false }).collect()
}

/// The smoke's fixture statuses (C# `CliAccountSmoke.FixtureStatuses`) in the box: each row's status line, the signed-in row's change
/// and sign-out, the signed-out row's sign-in, the missing CLI's word, the switch; a button's press reaches its action.
#[test]
fn the_accounts_box_draws_each_status_and_its_buttons() {
    common::korean();
    let actions = Arc::new(Recorded::default());
    let model = AccountsModel { rows: rows(accounts::smoke_fixture_statuses()), reading: false, auto_login: true };
    let (shared, kept) = (actions.clone() as Arc<dyn AccountActions>, model.clone());
    let (surfaces, node) = render(move |ui| view::accounts(ui, &kept, shared.clone()));
    let shown = texts(&node);
    assert!(shown.iter().any(|t| t.contains("me@example.com")), "the signed-in summary");
    assert!(shown.contains(&copy::summary_signed_out()), "the signed-out summary");
    assert!(shown.contains(&copy::status_not_installed()), "the missing CLI's word");
    assert!(shown.contains(&copy::section_description()), "the section's explanation");
    for aid in [
        "cli-account-row-claude",
        "cli-account-change-claude",
        "cli-account-reset-models-claude",
        "cli-account-bedrock-claude",
        "cli-account-reset-bedrock-claude",
        "cli-account-login-codex",
        "cli-account-refresh-codex",
        "cli-auto-login",
    ] {
        assert!(by_aid(&node, aid).is_some(), "{aid}");
    }
    assert_eq!(
        by_aid(&node, "cli-account-status-claude").and_then(|n| n["text"].as_str().map(str::to_string)),
        Some(model.rows[0].status.summary()),
        "the status line"
    );
    assert!(by_aid(&node, "cli-account-change-gemini").is_none(), "Antigravity cannot be signed out here");
    let id = |aid: &str| by_aid(&node, aid).and_then(|n| n["id"].as_i64()).unwrap_or_else(|| panic!("{aid} has no id"));
    assert!(surfaces.act("settings", id("cli-account-change-claude"), "invoke", &Value::Null).run(|_| {}), "a press");
    assert!(surfaces.act("settings", id("cli-account-login-codex"), "invoke", &Value::Null).run(|_| {}), "a press");
    assert!(surfaces.act("settings", id("cli-account-refresh-codex"), "invoke", &Value::Null).run(|_| {}), "a press");
    assert!(surfaces.act("settings", id("cli-auto-login"), "toggle", &Value::Bool(false)).run(|_| {}), "the switch");
    assert_eq!(actions.all(), ["change claude", "signIn codex Account", "refresh codex", "autoLogin false"]);
}

/// A row at work turns its ring and holds its buttons; a waited-on sign-in shows its line and "cancel waiting" (never held); a
/// message shows under the status with its warning mark; a status still being read turns the ring.
#[test]
fn a_rows_state_shows_in_its_line_and_its_buttons() {
    common::korean();
    let actions: Arc<dyn AccountActions> = Arc::new(Recorded::default());
    let signed_in = CliAccountStatus {
        logged_in: Some(true),
        account: Some("me@example.com".into()),
        method_id: Some(accounts::method::CODEX_CHATGPT.into()),
        ..CliAccountStatus::new("codex")
    };
    let model = AccountsModel {
        rows: vec![
            AccountRow { status: signed_in.clone(), pending: false, message: None, working: true },
            AccountRow { status: CliAccountStatus::new("pi"), pending: true, message: accounts::login_hint("pi"), working: true },
            AccountRow {
                status: CliAccountStatus { detail: copy::status_checking(), ..CliAccountStatus::new("hermes") },
                pending: false,
                message: None,
                working: false,
            },
        ],
        reading: false,
        auto_login: false,
    };
    let (_, node) = render(move |ui| view::accounts(ui, &model, actions.clone()));
    let change = by_aid(&node, "cli-account-change-codex").unwrap();
    assert_eq!(change["disabled"], Value::Bool(true), "a working row's buttons wait");
    let cancel = by_aid(&node, "cli-account-cancel-wait-pi").unwrap();
    assert!(cancel.get("disabled").is_none(), "cancel waiting never waits");
    assert!(texts(&node).contains(&copy::status_pending()), "the pending line");
    let message = by_aid(&node, "cli-account-message-pi").expect("the message");
    assert!(texts(&message).iter().any(|t| t.contains("/login")), "pi's terminal asks for /login: {message}");
    let spinners = nodes(&node).into_iter().filter(|n| n["t"] == "spinner").count();
    assert_eq!(spinners, 3, "the working rows and the one still being read turn their ring");
}

/// AcpAgentAccountVerification.cs:390 `TheWindowsAccountsSectionListsEveryProvider`: a row per provider of the app, in its order, and
/// what a terminal sign-in asks in that row (C#'s `CliAccountSupport.LoginHint(provider)` beside the row's sign-in).
#[test]
fn the_windows_accounts_section_lists_every_provider_in_its_rows() {
    common::korean();
    let statuses = AccountsModel::from_statuses(&Default::default());
    assert!(statuses.iter().map(|s| s.provider.as_str()).eq(mighty_core::wire::PROVIDERS), "every provider, checking");
    let model = AccountsModel { rows: rows(statuses), reading: true, auto_login: true };
    let actions: Arc<dyn AccountActions> = Arc::new(Recorded::default());
    let (_, node) = render(move |ui| view::accounts(ui, &model, actions.clone()));
    let order: Vec<String> = nodes(&node)
        .into_iter()
        .filter_map(|n| n.get("aid").and_then(Value::as_str).and_then(|a| a.strip_prefix(view::ACCOUNT_ROW_ID_PREFIX)).map(str::to_string))
        .collect();
    assert!(order.iter().map(String::as_str).eq(mighty_core::wire::PROVIDERS), "{order:?}");
    for provider in ["pi", "opencode", "hermes"] {
        assert!(accounts::login_hint(provider).is_some(), "{provider} says what its terminal asks");
    }
}

fn runtime(id: &str, available: bool, version: Option<&str>, detail: &str) -> ProviderRuntime {
    ProviderRuntime {
        id: id.into(),
        name: mighty_run::providers::name(id).into(),
        available,
        version: version.map(str::to_string),
        detail: detail.into(),
        model_catalog: mighty_run::providers::fallback(id),
        capabilities: mighty_run::providers::capabilities(id, version),
    }
}

/// `BuildProvidersSection`: before the runtime is read every row says it is checking; then ready or needs setup, the version in mono,
/// the detail; the recheck button runs its action and waits while it checks.
#[test]
fn the_providers_box_says_each_clis_state() {
    common::korean();
    let pressed = Arc::new(Mutex::new(0));
    let (_, before) = render(|ui| view::providers(ui, &ProvidersModel::default(), || {}));
    let checking = strings::get("settings.components.statusChecking");
    assert_eq!(texts(&before).iter().filter(|t| **t == checking).count(), mighty_core::wire::PROVIDERS.len(), "every row checks");
    let model = ProvidersModel {
        runtime: vec![runtime("claude", true, Some("2.1.300"), "ready"), runtime("codex", false, None, "missing")],
        checking: false,
    };
    let count = pressed.clone();
    let (surfaces, after) = render(move |ui| {
        let count = count.clone();
        view::providers(ui, &model, move || *count.lock().unwrap() += 1)
    });
    let shown = texts(&after);
    assert!(
        shown.contains(&strings::get("settings.providers.statusReady"))
            && shown.contains(&strings::get("settings.providers.statusNeedsSetup"))
    );
    let version = nodes(&after).into_iter().find(|n| n["text"] == "2.1.300").expect("the version");
    assert_eq!(version["mono"], Value::Bool(true), "the version in the code font");
    for id in mighty_core::wire::PROVIDERS {
        assert!(by_aid(&after, &format!("settings-provider-{id}")).is_some(), "{id}'s row");
    }
    let check = by_aid(&after, "settings-providers-check").unwrap();
    assert!(surfaces.act("settings", check["id"].as_i64().unwrap(), "invoke", &Value::Null).run(|_| {}) && *pressed.lock().unwrap() == 1);
    let (_, busy) = render(|ui| view::providers(ui, &ProvidersModel { runtime: vec![], checking: true }, || {}));
    assert_eq!(by_aid(&busy, "settings-providers-check").unwrap()["disabled"], Value::Bool(true), "it waits while it checks");
}

/// `BuildClaudeModsSection`: the checking sentence until Claude's runtime is read; then its detail and the minimum version.
#[test]
fn the_claude_mods_box_says_its_state_and_minimum_version() {
    common::korean();
    let (_, before) = render(|ui| view::claude_mods(ui, None));
    assert_eq!(texts(&before), [strings::get("window.status.checkingRuntime")]);
    let claude = runtime("claude", true, Some("2.1.300"), "Mods ready");
    let (_, after) = render(move |ui| view::claude_mods(ui, Some(&claude)));
    let shown = texts(&after);
    assert!(shown.contains(&"Mods ready".to_string()) && shown.contains(&view::MODS_MINIMUM_VERSION.to_string()));
    assert!(shown.contains(&strings::get("settings.claudeMods.compatLabel")));
}

fn dialog(spec: mighty_native::surface::DialogSpec) -> Value {
    let surfaces = Surfaces::new();
    surfaces.open("dialog-1", Place::Dialog, Box::new(DialogSurface::new(spec, |_| {})));
    serde_json::from_str::<Value>(&surfaces.render("dialog-1", &env()).unwrap()).unwrap()["view"].clone()
}

/// D8 and D9: alerts with the provider in the title, the sentence, the primary button and cancel.
#[test]
fn the_sign_out_and_bedrock_dialogs_are_alerts() {
    common::korean();
    let d8 = dialog(view::sign_out_dialog("claude", false));
    assert_eq!(d8["chrome"]["kind"], "alert");
    assert_eq!(d8["title"], copy::confirm_logout_title_template().replace("{provider}", "Claude"));
    assert_eq!(d8["sections"][0]["node"]["text"], copy::confirm_message_template().replace("{provider}", "Claude"));
    let labels: Vec<&str> = d8["buttons"].as_array().unwrap().iter().filter_map(|b| b["label"].as_str()).collect();
    assert_eq!(labels, [copy::button_cancel(), copy::button_logout()]);
    let change = dialog(view::sign_out_dialog("codex", true));
    assert_eq!(change["title"], copy::confirm_change_title_template().replace("{provider}", "Codex"));
    assert_eq!(change["buttons"][1]["label"], strings::get("settings.cliAccounts.macReloginButton"));
    let d9 = dialog(view::reset_bedrock_dialog());
    assert!(d9["chrome"]["kind"] == "alert" && d9["title"] == strings::get("settings.cliAccounts.resetBedrockButton"));
    assert_eq!(d9["sections"][0]["node"]["text"], strings::get("settings.cliAccounts.resetBedrockConfirm"));
}
