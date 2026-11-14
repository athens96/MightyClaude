//! The CLI tab's boxes against C#'s capture (plan W6 §1.8 item 5): `mighty-native/tests/golden/cli.json` is C#'s Settings › CLI as
//! its web smoke first pushed it (dark, Korean, the runtime and the account statuses not read yet), normalized by `parity`
//! (`tools/parity/native.ps1`, `native-diff --write-cs`). Each box is drawn here as Settings draws it in a smoke and compared byte for
//! byte with its section of the capture.

use std::sync::Arc;

use mighty_accounts::accounts::CliLoginOption;
use mighty_accounts::view::{self, AccountActions, AccountRow, AccountsModel, ProvidersModel};
use mighty_core::locale::Locale;
use mighty_core::settings_sections::{self as sections};
use mighty_native::parity;
use mighty_native::surface::{SurfaceModel, Surfaces};
use mighty_native::ui::{El, Env, Ui};
use mighty_native::view::{Place, Section, View};
use serde_json::Value;

const CLI: &str = include_str!("../../mighty-native/tests/golden/cli.json");

/// One box as Settings' surface holds it: its heading over `ui.section_body` of what `build` draws.
struct OneBox {
    title: String,
    build: Box<dyn FnMut(&mut Ui) -> El + Send>,
}

impl SurfaceModel for OneBox {
    fn view(&mut self, ui: &mut Ui) -> View {
        let body = (self.build)(ui);
        View::Settings {
            title: String::new(),
            close: String::new(),
            nav: vec![],
            selected: String::new(),
            sections: vec![Section { title: Some(self.title.clone()), beta: false, node: Some(ui.section_body(body)) }],
            width: 1.0,
            height: 1.0,
        }
    }
}

/// The box drawn in C#'s capture's context (dark, the standard text, Korean), normalized as the golden is.
/// The capture is Windows', so the boxes are drawn as on Windows on every OS (the Bedrock reset button is Windows-only); this test
/// binary's every test compares with that capture.
fn drawn(title: String, build: impl FnMut(&mut Ui) -> El + Send + 'static) -> String {
    mighty_run::os::set_windows(true);
    mighty_run::strings::set_language("ko");
    let surfaces = Surfaces::new();
    surfaces.open("settings", Place::Surface, Box::new(OneBox { title, build: Box::new(build) }));
    let push: Value = serde_json::from_str(&surfaces.render("settings", &Env::default()).expect("a push")).unwrap();
    parity::section(&push, 0).unwrap()
}

fn golden(index: usize) -> String {
    parity::section(&serde_json::from_str(CLI).unwrap(), index).unwrap()
}

struct Nothing;

impl AccountActions for Nothing {
    fn change(&self, _: &str) {}
    fn logout(&self, _: &str) {}
    fn sign_in(&self, _: &str, _: CliLoginOption) {}
    fn cancel_wait(&self, _: &str) {}
    fn reset_models(&self) {}
    fn reset_bedrock(&self) {}
    fn refresh(&self, _: &str) {}
    fn auto_login(&self, _: bool) {}
}

#[test]
fn the_providers_box_matches_cs_before_the_runtime_is_read() {
    let locale = Locale::new("ko");
    let title = sections::title_of(&locale, sections::PROVIDERS).unwrap();
    let model = ProvidersModel { runtime: vec![], checking: false };
    assert_eq!(drawn(title, move |ui| view::providers(ui, &model, || {})), golden(0));
}

#[test]
fn the_accounts_box_matches_cs_while_every_status_is_checking() {
    let locale = Locale::new("ko");
    let title = sections::title_of(&locale, sections::CLI_ACCOUNTS).unwrap();
    let rows = AccountsModel::from_statuses(&Default::default())
        .into_iter()
        .map(|status| AccountRow { status, pending: false, message: None, working: false })
        .collect();
    let model = AccountsModel { rows, reading: false, auto_login: true };
    let actions: Arc<dyn AccountActions> = Arc::new(Nothing);
    assert_eq!(drawn(title, move |ui| view::accounts(ui, &model, actions.clone())), golden(1));
}

#[test]
fn the_claude_mods_box_matches_cs_before_the_runtime_is_read() {
    let locale = Locale::new("ko");
    let title = sections::title_of(&locale, sections::CLAUDE_MODS).unwrap();
    assert_eq!(drawn(title, |ui| view::claude_mods(ui, None)), golden(2));
}
