//! The setup screen against C#'s capture (plan W6 §1.8 item 5): `mighty-native/tests/golden/setup.json` is the screen as C#'s web
//! smoke first pushed it over its fixed probe results (every Windows item missing, nothing probed yet: dark, Korean), normalized by
//! `parity` (`tools/parity/native.ps1`, `native-diff --write-cs`). The same screen is drawn here and compared byte for byte.

use std::sync::Arc;

use mighty_native::parity;
use mighty_native::surface::{SurfaceModel, Surfaces};
use mighty_native::ui::{Env, Ui};
use mighty_native::view::{Place, View};
use mighty_tools::setup::catalog::SetupPlatform;
use mighty_tools::setup::probe::SetupProbeResult;
use mighty_tools::setup::screen::SetupScreen;
use mighty_tools::view::{self, SetupActions, SetupExtras};
use serde_json::{Value, json};

const SETUP: &str = include_str!("../../mighty-native/tests/golden/setup.json");

struct Nothing;

impl SetupActions for Nothing {
    fn toggle(&self, _: &str) {}
    fn sign_in(&self, _: &str) {}
    fn update(&self) {}
    fn open_page(&self, _: &str) {}
    fn recheck(&self) {}
    fn install(&self) {}
    fn abandon(&self) {}
}

/// The setup surface as the host holds it (setup.rs `SetupSurface`): the list, its never-show box unticked.
struct Screen(SetupScreen);

impl SurfaceModel for Screen {
    fn view(&mut self, ui: &mut Ui) -> View {
        view::setup_view(view::setup_content(ui, &self.0, &SetupExtras::default(), Arc::new(Nothing)), false, |_| {})
    }
}

#[test]
fn the_setup_screen_over_fixed_results_matches_cs() {
    mighty_run::strings::set_language("ko");
    // C# RunWebSettingsSmoke's setup branch: the catalog read, every Windows item's result fixed as missing, what was read and
    // picked forgotten; the screen opened before its first probe ran.
    let mut screen = SetupScreen::new();
    let catalog = screen.load_catalog().expect("the bundled catalog");
    screen.fixed_results = Some(
        catalog
            .items
            .iter()
            .filter(|item| item.entry(SetupPlatform::Windows).is_some())
            .map(|item| (item.id.clone(), SetupProbeResult::missing()))
            .collect(),
    );
    let surfaces = Surfaces::new();
    surfaces.open("setup", Place::Surface, Box::new(Screen(screen)));
    let mut push: Value = serde_json::from_str(&surfaces.render("setup", &Env::default()).expect("a push")).unwrap();
    push["id"] = json!("setup");
    assert_eq!(parity::text(&parity::normalize(&push, &[])), SETUP.trim_end());
}
