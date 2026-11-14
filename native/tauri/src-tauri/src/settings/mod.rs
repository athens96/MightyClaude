//! Settings in the page (C# MainWindow.WebShell.Settings.cs `ShowWebSettingsAsync`, `SelectWebSettingsCategory`, `CloseWebSettings`;
//! MainWindow.Settings.cs:61-77 `SettingsGroups`; MainWindow.SettingsNavigation.cs `SettingsCategoryGlyph`), `openSettings` and
//! `openHelp` (MainWindow.Help.cs `OpenHelp`). Owner: W6-A.
//!
//! The surface `settings` (place `surface`): the available categories with their symbols, the one chosen — the saved `SettingsPane`
//! when it is available, else the first —, and that category's boxes in the Mac's order, each the rows its builder makes (every box
//! has its own file, by its owner) toned as `BuildSectionContainer` tones them. It holds the window's dialog while it is open (C#
//! `dialogOpen`) and refuses to open while another sheet is up. A nav saves `SettingsPane` (kept in memory; written only with
//! `--allow-state-write`); the CLI tab reads the account statuses again, except in a smoke.

pub mod about;
pub mod app_update;
pub mod cli_accounts;
pub mod cli_update;
pub mod general;
pub mod later;
pub mod models;
pub mod tools;

use std::sync::Arc;

use mighty_core::design_tokens::metrics;
use mighty_core::help_site;
use mighty_core::locale::Locale;
use mighty_core::settings_sections::{self as sections, available};
use mighty_native::surface::{Next, SurfaceModel};
use mighty_native::ui::{El, Ui};
use mighty_native::view::{NavItem, Place, Section, View};
use serde_json::Value;

use crate::frame::Sheet;
use crate::host::{Host, Registry};

/// The surface's id (the page's `data-native-view`).
pub const SURFACE: &str = "settings";

/// A box's builder: its rows from the host's models, no I/O (it runs on every read).
pub type Builder = fn(&Arc<Host>, &mut Ui) -> El;

/// One box of a category (C# `SettingsGroup`): its heading, its builder, whether the 베타 capsule follows the heading.
pub struct Group {
    pub title: String,
    pub build: Builder,
    pub beta: bool,
}

/// `SettingsGroups(category)`: each category's boxes in the Mac's order. A registered slot keeps the heading Core gives it; the boxes
/// the Mac draws inside a slot's tab (agent links, the toolkit, screen view) take theirs from the catalogue.
pub fn groups(locale: &Locale, category: &str) -> Vec<Group> {
    let slot = |id: &str, build: Builder| Group { title: sections::title_of(locale, id).unwrap_or_default(), build, beta: false };
    let titled = |key: &str, build: Builder, beta: bool| Group { title: locale.get(key), build, beta };
    match category {
        "general" => vec![
            slot(sections::DISPLAY, general::display),
            titled("settings.text.sectionTitle", general::text, false),
            titled("agentTerminal.urlOpen.settingTitle", general::agent_links, false),
        ],
        "models" => vec![slot(sections::PHASE_MODELS, models::phase_models)],
        "styles" => vec![slot(sections::STYLES, later::styles)],
        "tools" => vec![slot(sections::COMPONENTS, tools::components), titled("settings.toolkit.sectionTitle", tools::toolkit, false)],
        "cli" => vec![
            slot(sections::PROVIDERS, cli_accounts::providers),
            slot(sections::CLI_ACCOUNTS, cli_accounts::accounts),
            slot(sections::CLAUDE_MODS, cli_accounts::claude_mods),
            slot(sections::CLI_UPDATE, cli_update::cli_update),
        ],
        "mobile" => vec![
            slot(sections::MOBILE_REMOTE, later::mobile_remote),
            titled("settings.screenShare.sectionTitle", later::screen_share, true),
        ],
        "remote" => vec![slot(sections::REMOTE_WORKSPACES, later::remote_workspaces)],
        "companion" => vec![slot(sections::COMPANION, later::companion)],
        "about" => vec![slot(sections::APP_UPDATE, about::app_update), slot(sections::APP_INFO, about::app_info)],
        _ => vec![],
    }
}

/// `SettingsCategoryGlyph`: the side list's Segoe Fluent symbol per category (the page draws its own Lucide icons for the known ones).
pub fn glyph(id: &str) -> &'static str {
    match id {
        "remote" => "\u{E968}",
        "models" => "\u{E950}",
        "styles" => "\u{E790}",
        "tools" => "\u{E7B8}",
        "cli" => "\u{E756}",
        "mobile" => "\u{E8EA}",
        "about" => "\u{E946}",
        _ => "\u{E713}",
    }
}

/// The CLI tab's account statuses read again (C# `RefreshVisibleSettingsAccountsAsync`), on a task of its own so the accounts' read
/// never runs under the surface's lock; never in a smoke.
fn refresh_accounts(host: &Arc<Host>, category: &str) {
    if category != "cli" || host.smoke.is_some() {
        return;
    }
    let host = host.clone();
    tauri::async_runtime::spawn(async move { crate::accounts::refresh(&host) });
}

/// The settings surface's model.
struct SettingsSurface {
    host: Arc<Host>,
    category: String,
    /// The window's dialog, held while Settings is open.
    _sheet: Sheet,
    /// Told when Settings closes (`openSettings` answers then).
    closed: Option<tokio::sync::oneshot::Sender<()>>,
}

impl Drop for SettingsSurface {
    fn drop(&mut self) {
        if let Some(closed) = self.closed.take() {
            let _ = closed.send(());
        }
    }
}

impl SurfaceModel for SettingsSurface {
    fn view(&mut self, ui: &mut Ui) -> View {
        let locale = ui.locale();
        let nav = available(&locale)
            .into_iter()
            .map(|c| NavItem { id: c.id.to_string(), title: c.title, glyph: glyph(c.id).to_string() })
            .collect();
        let sections = groups(&locale, &self.category)
            .into_iter()
            .map(|group| {
                let body = (group.build)(&self.host, ui);
                Section { title: Some(group.title), beta: group.beta, node: Some(ui.section_body(body)) }
            })
            .collect();
        View::Settings {
            title: locale.get("settings.title"),
            close: locale.get("settings.closeButton"),
            nav,
            selected: self.category.clone(),
            sections,
            width: metrics::SETTINGS_WIDTH,
            height: metrics::SETTINGS_HEIGHT,
        }
    }

    /// Any button closes Settings.
    fn button(&mut self, _which: Option<&str>) -> Next {
        Next::Close
    }

    /// `surface.Navigate`: nothing for the category shown or one not available; else that category's boxes, and `SettingsPane`
    /// saved (C#'s `Act(UpdateAsync)`: the banner cleared first, a failure shown there).
    fn navigate(&mut self, to: &str) -> bool {
        if to == self.category || !available(&self.host.locale).iter().any(|c| c.id == to) {
            return false;
        }
        self.category = to.to_string();
        refresh_accounts(&self.host, to);
        let mut view = self.host.view();
        view.error = None;
        if let Err(error) = view.update(|state| state.settings_pane = Some(to.to_string())) {
            view.error = Some(error);
        }
        true
    }
}

/// `ShowWebSettingsAsync` up to its wait: none when Settings is open already or another sheet holds the window.
fn start(host: &Arc<Host>) -> Option<tokio::sync::oneshot::Receiver<()>> {
    if crate::native::is_open(host, SURFACE) {
        return None;
    }
    let sheet = Sheet::begin(host)?;
    // The text box's font lists, read once per run before any builder runs (C# `InstalledFamilies`).
    general::read_families(host);
    // omc's and Ouroboros's settings for the phase models box (C# `LoadTools`; the fixture in a smoke).
    models::read_tools(host);
    let saved = host.view().snapshot.settings_pane.clone();
    let categories = available(&host.locale);
    let category = saved.filter(|pane| categories.iter().any(|c| c.id == pane)).unwrap_or_else(|| categories[0].id.to_string());
    refresh_accounts(host, &category);
    let (closed, done) = tokio::sync::oneshot::channel();
    crate::native::open(
        host,
        SURFACE,
        Place::Surface,
        Box::new(SettingsSurface { host: host.clone(), category, _sheet: sheet, closed: Some(closed) }),
    );
    Some(done)
}

/// `settings::open`: Settings opened (when it can be) and, when `category` is given, brought to that category (C# `OpenSettings`
/// then `SelectSettingsCategory`).
#[allow(dead_code, reason = "a seam W6-A1 (the slash palette) and W6-C calls")]
pub fn open(host: &Arc<Host>, category: Option<&str>) {
    let _ = start(host);
    if let Some(category) = category {
        select(host, category);
    }
}

/// `SelectWebSettingsCategory`: true when Settings is open (and now on `id`).
pub fn select(host: &Arc<Host>, id: &str) -> bool {
    if !crate::native::is_open(host, SURFACE) {
        return false;
    }
    if crate::native::navigate(host, SURFACE, id) {
        crate::native::render(host, SURFACE);
    }
    true
}

/// `CloseWebSettings`.
#[allow(dead_code, reason = "a seam W6-A1 and W6-D calls")]
pub fn close(host: &Arc<Host>) {
    crate::native::close(host, SURFACE);
}

/// C# `OpenHelp` (the sidebar's help symbol, F1, Settings › 앱 정보): the guide's folder in the language the app shows, in the user's
/// browser. A smoke opens nothing (the browser would outlive it): the address is logged instead.
pub fn open_help(host: &Host) -> Result<(), String> {
    let address = help_site::current(&host.locale);
    if host.smoke.is_some() {
        host.log.line(&format!("smoke: help not opened: {address}"));
        return Ok(());
    }
    crate::platform::open_url(&address)
}

pub fn register(methods: &mut Registry) {
    // C# `openSettings`: `if (!dialogOpen) await OpenSettings()`, which answers once Settings closed.
    methods.add("openSettings", |host: Arc<Host>, _| async move {
        if host.view().dialog_open {
            return Ok(Value::Null);
        }
        crate::frame::act(&host, async {
            if let Some(closed) = start(&host) {
                let _ = closed.await;
            }
            Ok(())
        })
        .await
    });
    methods.add("openHelp", |host: Arc<Host>, _| async move {
        open_help(&host)?;
        Ok(Value::Null)
    });
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Every available category has its boxes, each box a heading, and the slots' headings are SettingsSections' (C#
    /// `SettingsGroups`).
    #[test]
    fn every_category_has_its_boxes_in_the_macs_order() {
        let locale = Locale::new("ko");
        let titles = |category: &str| groups(&locale, category).into_iter().map(|g| g.title).collect::<Vec<_>>();
        for category in available(&locale) {
            let boxes = groups(&locale, category.id);
            assert!(!boxes.is_empty() && boxes.iter().all(|g| !g.title.is_empty()), "{}", category.id);
        }
        assert_eq!(
            titles("general"),
            vec!["화면".to_string(), locale.get("settings.text.sectionTitle"), locale.get("agentTerminal.urlOpen.settingTitle")]
        );
        assert_eq!(
            titles("cli"),
            vec![
                "이 PC의 CLI".to_string(),
                locale.get("settings.cliAccounts.sectionTitle"),
                "Claude Mods".to_string(),
                locale.get("settings.cliUpdate.sectionTitle")
            ]
        );
        assert!(groups(&locale, "mobile")[1].beta, "screen view carries the 베타 capsule");
        assert_eq!(titles("about"), vec![locale.get("settings.appUpdate.sectionTitle"), "앱 정보".to_string()]);
        assert!(groups(&locale, "elsewhere").is_empty());
    }

    #[test]
    fn the_glyphs_are_segoe_fluents_code_points() {
        assert_eq!(glyph("general"), "\u{E713}");
        assert_eq!(glyph("companion"), "\u{E713}", "the paw is a path in WinUI; the list's glyph falls back to the gear");
        assert_eq!(glyph("remote"), "\u{E968}");
    }
}
