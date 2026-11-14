//! The CLI 업데이트 box and the plugin browser, dialog D3 (C# MainWindow.Settings.cs:155-251 `BuildCliUpdateSection`,
//! MainWindow.Plugins.cs:58-469 `ShowPluginBrowser`, :822-866 `PluginRowPanel`), as mighty-native builders: immediate-mode, no I/O,
//! every element, automation id, ink and size as the WinUI builder makes it. The host gives what C# read off its window
//! ([`CliUpdateBox`]) and what the controls do ([`CliUpdateActions`], [`PluginActions`]); the plugin window's own state is the
//! [`ClaudePluginBrowser`] the handlers change directly.
//!
//! The plugin window is the page's panel `plugins` (C#'s `paged` branch): the header's tools are the CLI's version and the reload
//! ([`plugin_tools`]), the footer's status the ring and what is being done ([`plugin_status`]), its action the cancel
//! ([`plugin_actions`]), its body the tabs, the filters, the sentences and the rows ([`plugin_body`]).

use std::sync::Arc;

use mighty_core::design_tokens::{Token, metrics};
use mighty_native::kit::{ControlSize, TextStyle};
use mighty_native::tokens::Brush;
use mighty_native::ui::{Border, Content, El, Grid, HAlign, Kind, Local, StackPanel, Thickness, Track, Ui, VAlign};
use mighty_run::strings::{get, get_with};

use crate::cli_update::{CliUpdateResult, CliUpdateService, strings};
use crate::plugins::browser::{ClaudePluginBrowser, ClaudePluginRow};
use crate::plugins::status;
use crate::plugins::strings::plugin as plugin_strings;
use crate::plugins::support::automation_id;
use crate::plugins::{ClaudePluginSnapshot, PluginAutoUpdateResult};

// ---- the CLI 업데이트 box ----------------------------------------------------------------------------------------------------

/// The automation id prefix of a CLI result row (`ResultRowIdPrefix`): `cli-update-result-<provider>-<status>`.
pub const RESULT_ROW_ID_PREFIX: &str = "cli-update-result-";
/// checkmark.circle.fill, exclamationmark.circle, info.circle in Segoe Fluent Icons.
const DONE_GLYPH: &str = "\u{EC61}";
const FAILED_GLYPH: &str = "\u{E783}";
const NOTE_GLYPH: &str = "\u{E946}";

/// What the box shows (C#: the window's fields `BuildCliUpdateSection` and `RefreshBackgroundUpdates` read).
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct CliUpdateBox {
    /// `AutoUpdateCLIs == true`.
    pub auto_update_clis: bool,
    /// `AutoUpdatePlugins != false`.
    pub auto_update_plugins: bool,
    /// `AnyCliUpdateRunning`: the manual run or the automatic pass.
    pub any_running: bool,
    pub plugin_operation_running: bool,
    /// What the automatic pass updates now, and whether it is that provider's plugins.
    pub automatic_provider: Option<String>,
    pub automatic_plugins: bool,
    /// The last run's finish time, already in the words `lastRunTemplate` takes.
    pub last_run: Option<String>,
    /// The CLI results shown, in order.
    pub results: Vec<CliUpdateResult>,
    /// The automatic plugin updates' results, in `Wire.Providers` order.
    pub plugin_results: Vec<(String, PluginAutoUpdateResult)>,
}

/// What the box's controls do.
#[derive(Clone)]
pub struct CliUpdateActions {
    pub toggle_clis: Arc<dyn Fn(bool) + Send + Sync>,
    pub toggle_plugins: Arc<dyn Fn(bool) + Send + Sync>,
    /// 업데이트 하기: the manual run (the host checks nothing runs, as C#'s Click does).
    pub start: Arc<dyn Fn() + Send + Sync>,
}

impl CliUpdateActions {
    /// Controls that do nothing (a smoke's fixture box).
    pub fn none() -> CliUpdateActions {
        CliUpdateActions { toggle_clis: Arc::new(|_| {}), toggle_plugins: Arc::new(|_| {}), start: Arc::new(|| {}) }
    }
}

/// The words beside the update button: what the automatic pass updates, else the inspection while a run is on, else the last
/// run's time, else nothing.
pub fn progress_text(model: &CliUpdateBox) -> String {
    if let Some(provider) = &model.automatic_provider {
        let key = if model.automatic_plugins {
            "settings.cliUpdate.progressPluginsTemplate"
        } else {
            "settings.cliUpdate.progressProviderTemplate"
        };
        return get_with(key, &[("provider", mighty_run::providers::name(provider))]);
    }
    if model.any_running {
        return strings::progress_inspecting();
    }
    model.last_run.as_ref().map(|time| strings::last_run_template().replace("{time}", time)).unwrap_or_default()
}

/// `Mark(status)`: a run that went through, a failed one, the rest.
fn mark(status: &str) -> (&'static str, Token) {
    match status {
        "updated" | "current" => (DONE_GLYPH, Token::Ink2),
        "failed" => (FAILED_GLYPH, Token::WaitText),
        _ => (NOTE_GLYPH, Token::Ink2),
    }
}

/// `ResultRow`: the mark, the title (and the 베타 capsule of a beta provider), the version line, the detail.
fn result_row(ui: &Ui, status: &str, title: String, provider: Option<&str>, change: Option<String>, detail: &str, id: String) -> El {
    let (glyph, ink) = mark(status);
    let mut head = vec![ui.settings_symbol(glyph, 12.0, ink), ui.settings_text(title, TextStyle::size(11.0).medium())];
    if provider.is_some_and(mighty_run::providers::is_beta) {
        head.push(ui.beta_badge());
    }
    let mut row = vec![El::stack(true, metrics::SPACING_SM, head)];
    if let Some(change) = change {
        row.push(ui.settings_text(change, TextStyle::size(10.0).ink(Token::Ink2).mono()));
    }
    if !detail.is_empty() {
        row.push(ui.settings_text(detail, TextStyle::size(11.0).ink(Token::Ink2).selectable()));
    }
    El::stack(false, metrics::SPACING_XS, row).aid(id)
}

/// `BuildCliUpdateSection` (M/CLIUpdateSettingsView.swift:8-74): the two switches each over its explanation, the progress or
/// last-run words beside the update button, then a row per CLI result and a row per plugin result.
pub fn cli_update_box(ui: &Ui, model: &CliUpdateBox, actions: &CliUpdateActions) -> El {
    let mut rows = ui.settings_rows();
    let toggle_label = strings::auto_update_toggle();
    let toggle = actions.toggle_clis.clone();
    let clis = ui.settings_switch(toggle_label.clone(), model.auto_update_clis, Some("cli-auto-update"), move |on| toggle(on));
    rows.add(ui.settings_labeled(ui.settings_text(toggle_label, TextStyle::default()), clis, false));
    rows.add(ui.settings_text(strings::section_description(), TextStyle::size(11.0).ink(Token::Ink2)));
    let plugins_label = get("settings.cliUpdate.autoUpdatePluginsToggle");
    let toggle = actions.toggle_plugins.clone();
    let plugins = ui.settings_switch(plugins_label.clone(), model.auto_update_plugins, Some("plugin-auto-update"), move |on| toggle(on));
    rows.add(ui.settings_labeled(ui.settings_text(plugins_label, TextStyle::default()), plugins, false));
    rows.add(ui.settings_text(get("settings.cliUpdate.autoUpdatePluginsDescription"), TextStyle::size(11.0).ink(Token::Ink2)));

    // While something runs the words are the progress (primary ink); otherwise the quiet last-run time.
    let progress = ui
        .settings_text(progress_text(model), TextStyle::size(11.0).ink(if model.any_running { Token::Ink } else { Token::Ink2 }))
        .aid("cli-update-progress");
    let label = if model.any_running { strings::updating_button() } else { strings::update_button() };
    let start = actions.start.clone();
    let mut button = ui.button(label, move || start()).enabled(!model.any_running && !model.plugin_operation_running);
    button.name = String::new();
    let button = ui.settings_push(button, ControlSize::Regular, false, false).aid("cli-update-start");
    rows.add(ui.settings_labeled(progress, button, false));

    for result in &model.results {
        let id = format!("{RESULT_ROW_ID_PREFIX}{}-{}", result.provider, result.status);
        let row = result_row(
            ui,
            &result.status,
            CliUpdateService::result_row(result),
            Some(&result.provider),
            CliUpdateService::version_change(result),
            &result.detail,
            id,
        );
        rows.add(row);
    }
    for (provider, result) in &model.plugin_results {
        let status = if result.status == "succeeded" { "updated" } else { result.status.as_str() };
        let title = get_with(
            "settings.cliUpdate.pluginRowTemplate",
            &[("provider", mighty_run::providers::name(provider)), ("status", &strings::status_label(status))],
        );
        rows.add(result_row(ui, status, title, Some(provider), None, &result.detail, format!("plugin-update-result-{provider}")));
    }
    rows.finish()
}

/// Every element of a tree, depth first (the smoke and the checks read the box's rows back off the built tree, as C# reads
/// `SettingsElements`).
pub fn elements(el: &El) -> Vec<&El> {
    let mut all = vec![el];
    for child in children(el) {
        all.extend(elements(child));
    }
    all
}

/// An element's children as WinUI's tree holds them (a panel's, a border's child, a content control's element).
fn children(el: &El) -> Vec<&El> {
    fn content(c: &Content) -> Option<&El> {
        match c {
            Content::El(inner) => Some(inner),
            _ => None,
        }
    }
    match &el.kind {
        Kind::StackPanel(StackPanel { children, .. }) | Kind::Grid(Grid { children, .. }) | Kind::Panel { children } => {
            children.iter().collect()
        }
        Kind::Border(Border { child, .. }) => child.iter().map(|c| c.as_ref()).collect(),
        Kind::Button(button) => content(&button.content).into_iter().collect(),
        Kind::ScrollViewer { content: c } | Kind::ContentControl { content: c } => content(c).into_iter().collect(),
        _ => vec![],
    }
}

/// The statuses of the CLI result rows a built box shows, in order (C# `RunSettingsSectionsSmoke`'s read-back).
pub fn shown_statuses(box_el: &El) -> Vec<String> {
    elements(box_el)
        .into_iter()
        .filter_map(|el| el.aid.as_deref())
        .filter(|aid| aid.starts_with(RESULT_ROW_ID_PREFIX))
        .map(|aid| aid[aid.rfind('-').map_or(0, |at| at + 1)..].to_string())
        .collect()
}

// ---- the plugin browser ------------------------------------------------------------------------------------------------------

/// The plugin sheet's size, the least the page's panel shrinks to, and its marketplace filter's width.
pub const PLUGIN_SHEET_WIDTH: f64 = 760.0;
pub const PLUGIN_SHEET_HEIGHT: f64 = 620.0;
pub const PLUGIN_SHEET_MIN_WIDTH: f64 = 560.0;
pub const PLUGIN_SHEET_MIN_HEIGHT: f64 = 420.0;
const PLUGIN_FILTER_WIDTH: f64 = 230.0;
/// The chosen tab's tint: accent at 0.17.
const PLUGIN_TAB_TINT: f64 = 0.17;
/// `DesignMetrics.Radius.Row`.
const RADIUS_ROW: f64 = 8.0;
/// arrow.clockwise, magnifyingglass, the disclosure's chevrons, checkmark.circle.fill, info.circle.
const RELOAD_GLYPH: &str = "\u{E72C}";
const SEARCH_GLYPH: &str = "\u{E721}";
const FOLDED_GLYPH: &str = "\u{E76C}";
const UNFOLDED_GLYPH: &str = "\u{E70D}";
/// Where the Claude window says to add a marketplace.
pub const MARKETPLACE_HELP_URL: &str = "https://code.claude.com/docs/en/discover-plugins#add-marketplaces";

/// What the plugin window's buttons do beyond its own state (the browser): read the list again, install one plugin, refresh the
/// marketplaces. Each starts its work and returns.
#[derive(Clone)]
pub struct PluginActions {
    pub reload: Arc<dyn Fn() + Send + Sync>,
    pub install: Arc<dyn Fn(String) + Send + Sync>,
    pub refresh: Arc<dyn Fn() + Send + Sync>,
}

/// The window's subtitle: the workspace's name · its path.
pub fn plugin_subtitle(browser: &ClaudePluginBrowser) -> String {
    format!("{} · {}", browser.workspace().name(), browser.workspace().path())
}

fn aid(browser: &ClaudePluginBrowser, part: &str) -> String {
    automation_id(browser.provider(), part)
}

/// A TextBlock in the kit's look with its ink.
fn words(ui: &Ui, text: impl Into<String>, size: f64, ink: Token) -> El {
    ui.settings_text(text, TextStyle::size(size).ink(ink))
}

/// `PluginTab`: a plain button around a radius-7 chip with 12pt words, semibold on the accent tint when chosen.
fn tab(ui: &Ui, browser: &Arc<ClaudePluginBrowser>, value: &'static str, name: String) -> El {
    let chosen = browser.tab() == value;
    let mut label = ui.text_block(browser.tab_label(value), 12.0);
    if let Some(block) = label.text_mut() {
        block.foreground = Some(ui.brush(Token::Ink));
        block.weight = if chosen { 600 } else { 400 };
    }
    let chip = El::border(Border {
        child: Some(Box::new(label)),
        padding: Thickness::new(metrics::SPACING_MD, metrics::SPACING_SM, metrics::SPACING_MD, metrics::SPACING_SM),
        corner_radius: 7.0,
        background: Some(if chosen { ui.brush_at(Token::Accent, PLUGIN_TAB_TINT) } else { ui.env.theme.subtle() }),
        ..Border::default()
    });
    let target = browser.clone();
    let mut button = El::button(chip)
        .named(name)
        .aid(aid(browser, if value == ClaudePluginBrowser::MARKETPLACE_TAB { "tab-marketplace" } else { "tab-installed" }));
    button = button.on_click(move || target.set_tab(value));
    ui.paint_plain_button(&mut button, Brush::TRANSPARENT, Some(Brush::TRANSPARENT), None);
    button
}

/// The reload button: the symbol before its words, disabled while the list is read.
fn reload_button(ui: &Ui, browser: &Arc<ClaudePluginBrowser>, actions: &PluginActions) -> El {
    let reload = actions.reload.clone();
    let button = El::button(ui.settings_glyph_label(RELOAD_GLYPH, plugin_strings::button_reload(), metrics::TYPE_TITLE))
        .named(plugin_strings::button_reload())
        .aid(aid(browser, "reload"))
        .enabled(!browser.loading())
        .on_click(move || reload());
    ui.settings_push(button, ControlSize::Regular, false, false)
}

/// The header's tools: the CLI's version, then the reload.
pub fn plugin_tools(ui: &Ui, browser: &Arc<ClaudePluginBrowser>, actions: &PluginActions) -> El {
    let version = browser.snapshot().and_then(|s| s.cli_version.clone()).unwrap_or_default();
    let version = ui.settings_text(version, TextStyle::size(10.0).ink(Token::Ink2).mono()).va(VAlign::Center);
    El::stack(true, metrics::SPACING_MD, vec![version, reload_button(ui, browser, actions)])
}

/// The footer's status: the ring that turns while the list is read or an operation runs, and what is being done.
pub fn plugin_status(ui: &Ui, browser: &ClaudePluginBrowser) -> El {
    let working = browser.loading() || browser.is_mutating();
    let ring = El::new(Kind::ProgressRing { active: working }).width(16.0).height(16.0).va(VAlign::Center).visible(working);
    let loading = words(ui, plugin_strings::progress_loading(), 11.0, Token::Ink2)
        .aid(aid(browser, "progress"))
        .visible(browser.loading() && !browser.is_mutating());
    let operating = words(ui, browser.progress_label().unwrap_or_default(), 11.0, Token::Ink2)
        .aid(aid(browser, "operation-progress"))
        .visible(browser.is_mutating());
    let doing = El::grid(Grid { children: vec![loading, operating], ..Grid::default() });
    El::stack(true, metrics::SPACING_SM, vec![ring, doing])
}

/// The footer's action: the cancel, shown while a change runs.
pub fn plugin_actions(ui: &Ui, browser: &Arc<ClaudePluginBrowser>) -> El {
    let target = browser.clone();
    let button = El::button(browser.cancel_label())
        .aid(aid(browser, "cancel-operation"))
        .enabled(browser.can_cancel())
        .visible(browser.is_mutating())
        .on_click(move || target.request_cancel());
    ui.settings_push(button, ControlSize::Regular, false, false)
}

/// `PluginRowPanel`: a plugin on the subtle wash — its name and version against its state word, the description, where it comes
/// from, its project path, its errors and notes; a catalog row with its install button at the top of the trailing edge.
fn row_panel(ui: &Ui, browser: &ClaudePluginBrowser, row: &ClaudePluginRow, install: Option<El>) -> El {
    let mut title = vec![];
    let mut name = ui.settings_text(row.name.clone(), TextStyle::size(13.0));
    if let Some(block) = name.text_mut() {
        block.weight = 600;
    }
    title.push(name);
    if let Some(version) = row.version.as_deref().filter(|v| !v.is_empty()) {
        title.push(ui.settings_text(version, TextStyle::size(10.0).ink(Token::Ink2).mono()).cell(0, 1));
    }
    if let Some(state) = row.state.as_deref().filter(|s| !s.is_empty()) {
        let ink = if state == plugin_strings::state_enabled() { Token::DoneText } else { Token::Ink2 };
        title.push(ui.settings_text(state, TextStyle::size(10.0).ink(ink).medium()).ha(HAlign::Right).cell(0, 2));
    }
    let catalog = install.is_some();
    let mut lines = vec![El::grid(Grid {
        children: title,
        cols: vec![Track::Auto, Track::Auto, Track::Star(1.0)],
        col_spacing: metrics::SPACING_SM,
        ..Grid::default()
    })];
    if !row.description.is_empty() {
        lines.push(words(ui, row.description.clone(), 11.0, Token::Ink2));
    }
    lines.push(if catalog {
        ui.settings_tertiary(row.subtitle.clone(), 10.0, false, false)
    } else {
        words(ui, row.subtitle.clone(), 10.0, Token::Ink2)
    });
    if let Some(path) = row.project_path.as_deref().filter(|p| !p.is_empty()) {
        lines.push(ui.settings_tertiary(path, 10.0, true, false).tooltip(path));
    }
    for error in &row.errors {
        lines.push(words(ui, error.clone(), 10.0, Token::WaitText));
    }
    for note in &row.notes {
        lines.push(words(ui, note.clone(), 10.0, Token::Ink2));
    }
    let mut children = vec![El::stack(false, if catalog { 6.0 } else { 7.0 }, lines)];
    if let Some(install) = install {
        children.push(install.va(VAlign::Top).cell(0, 1));
    }
    El::grid(Grid {
        children,
        cols: vec![Track::Star(1.0), Track::Auto],
        col_spacing: metrics::SPACING_MD,
        padding: Thickness::all(metrics::SPACING_MD),
        background: Some(ui.env.theme.subtle()),
        ..Grid::default()
    })
    .aid(aid(browser, &format!("row-{}", row.id)))
    .keyed(row.id.clone())
}

/// The rows of the selected tab (a catalog row with its install button), the wait while the first read runs, or the sentence that
/// stands in for an empty list with the way to add a marketplace.
fn rows(ui: &Ui, browser: &Arc<ClaudePluginBrowser>, actions: &PluginActions) -> Vec<El> {
    let on_marketplace = browser.tab() == ClaudePluginBrowser::MARKETPLACE_TAB;
    let mut list = vec![];
    for row in browser.rows() {
        let install = on_marketplace.then(|| {
            let (id, install) = (row.id.clone(), actions.install.clone());
            let button = El::button(browser.install_button_label(&row.id))
                .aid(aid(browser, &format!("install-{}", row.id)))
                .enabled(browser.can_install(&row.id))
                .on_click(move || install(id.clone()));
            ui.settings_push(button, ControlSize::Regular, false, false)
        });
        list.push(row_panel(ui, browser, &row, install));
    }
    if !list.is_empty() {
        return list;
    }
    if browser.loading() && !browser.is_mutating() {
        let wait = El::stack(
            true,
            metrics::SPACING_SM,
            vec![
                El::new(Kind::ProgressRing { active: true }).width(16.0).height(16.0),
                words(ui, get("dialog.loading"), 12.0, Token::Ink2),
            ],
        )
        .ha(HAlign::Center)
        .margin(Thickness::new(0.0, metrics::SPACING_XL, 0.0, metrics::SPACING_XL))
        .mark("loading");
        return vec![wait];
    }
    let mut empty = vec![words(ui, browser.empty_message(), 12.0, Token::Ink2).ha(HAlign::Center)];
    if browser.shows_marketplace_help_link() {
        let mut link = El::new(Kind::Hyperlink {
            content: plugin_strings::marketplace_help_link().into(),
            uri: Some(MARKETPLACE_HELP_URL.to_string()),
        })
        .ha(HAlign::Center);
        link.name = String::new();
        empty.push(link);
    }
    if let Some(help) = browser.marketplace_help_text() {
        empty.push(words(ui, help, 11.0, Token::Ink2).ha(HAlign::Center).aid(aid(browser, "marketplace-help")));
    }
    vec![El::stack(false, metrics::SPACING_MD, empty).margin(Thickness::new(0.0, metrics::SPACING_XL, 0.0, metrics::SPACING_XL))]
}

/// The window's body (M/ClaudePluginView.swift:184-303): the tabs; the search beside the marketplace filter; on the marketplace tab
/// the scope beside the marketplace refresh with its note and Codex's Git-only sentence; what the read and the last operation said;
/// the command's output behind its disclosure; the note; a rule; the rows.
pub fn plugin_body(ui: &mut Ui, browser: &Arc<ClaudePluginBrowser>, actions: &PluginActions) -> El {
    let diagnostics_open: Local<bool> = ui.local("plugin-diagnostics", || false);
    let on_marketplace = browser.tab() == ClaudePluginBrowser::MARKETPLACE_TAB;

    let tabs = El::grid(Grid {
        children: vec![El::stack(
            true,
            metrics::SPACING_SM,
            vec![
                tab(ui, browser, ClaudePluginBrowser::INSTALLED_TAB, plugin_strings::tab_installed()),
                tab(ui, browser, ClaudePluginBrowser::MARKETPLACE_TAB, plugin_strings::tab_marketplace()),
            ],
        )],
        cols: vec![Track::Star(1.0), Track::Auto],
        ..Grid::default()
    });

    // The search box takes what the 230-wide marketplace filter leaves.
    let target = browser.clone();
    let search = El::text_box(browser.search())
        .placeholder(plugin_strings::search_placeholder())
        .named(plugin_strings::search_placeholder())
        .aid(aid(browser, "search"))
        .va(VAlign::Center)
        .on_text(move |text| target.set_search(&text))
        .cell(0, 1);
    let search_box = El::border(Border {
        child: Some(Box::new(El::grid(Grid {
            children: vec![ui.settings_symbol(SEARCH_GLYPH, 12.0, Token::Ink2), search],
            cols: vec![Track::Auto, Track::Star(1.0)],
            col_spacing: metrics::SPACING_SM,
            ..Grid::default()
        }))),
        padding: Thickness::all(metrics::SPACING_SM),
        corner_radius: RADIUS_ROW,
        background: Some(ui.env.theme.subtle()),
        ..Border::default()
    });
    let names = browser.marketplaces();
    let wanted: Vec<String> = std::iter::once(String::new()).chain(names.iter().cloned()).collect();
    let mut items: Vec<Content> = vec![plugin_strings::filter_all().into()];
    items.extend(names.iter().map(|n| Content::from(n.as_str())));
    let index = wanted.iter().position(|w| *w == browser.marketplace_filter()).unwrap_or(0) as i32;
    let target = browser.clone();
    let filter = ui
        .settings_popup(El::combo_box(index, items), true)
        .named(plugin_strings::tab_marketplace())
        .aid(aid(browser, "marketplace-filter"))
        .ha(HAlign::Stretch)
        .on_select(move |at| {
            let choice = usize::try_from(at).ok().and_then(|at| wanted.get(at)).cloned().unwrap_or_default();
            if target.marketplace_filter() != choice {
                target.set_marketplace_filter(&choice);
            }
        })
        .cell(0, 1);
    let filter_line = El::grid(Grid {
        children: vec![ui.settings_text(plugin_strings::tab_marketplace(), TextStyle::default()), filter],
        cols: vec![Track::Auto, Track::Star(1.0)],
        col_spacing: metrics::SPACING_SM,
        ..Grid::default()
    })
    .width(PLUGIN_FILTER_WIDTH)
    .va(VAlign::Center)
    .cell(0, 1);
    let filters = El::grid(Grid {
        children: vec![search_box, filter_line],
        cols: vec![Track::Star(1.0), Track::Auto],
        col_spacing: metrics::SPACING_MD,
        ..Grid::default()
    });

    // The scope (Claude: three; Codex: one) beside the marketplace refresh, and the scope's note.
    let options = browser.scope_options();
    let scope_index = options.iter().position(|o| o.value == browser.scope()).unwrap_or(0) as i32;
    let values: Vec<String> = options.iter().map(|o| o.value.clone()).collect();
    let target = browser.clone();
    let picker = ui
        .settings_popup(El::combo_box(scope_index, options.iter().map(|o| Content::from(o.label.as_str())).collect()), true)
        .named(plugin_strings::scope_picker_label())
        .aid(aid(browser, "scope-picker"))
        .ha(HAlign::Left)
        .on_select(move |at| {
            if let Some(value) = usize::try_from(at).ok().and_then(|at| values.get(at)) {
                target.set_scope(value);
            }
        });
    let refresh = actions.refresh.clone();
    let refresh_button = ui
        .settings_push(
            El::button(plugin_strings::button_marketplace_refresh())
                .enabled(browser.can_refresh_marketplaces())
                .on_click(move || refresh()),
            ControlSize::Regular,
            false,
            false,
        )
        .aid(aid(browser, "refresh-marketplaces"))
        .cell(0, 1);
    let picker_row = El::grid(Grid {
        children: vec![
            El::stack(
                true,
                metrics::SPACING_SM,
                vec![ui.settings_text(plugin_strings::scope_picker_label(), TextStyle::default()), picker],
            ),
            refresh_button,
        ],
        cols: vec![Track::Star(1.0), Track::Auto],
        col_spacing: metrics::SPACING_MD,
        ..Grid::default()
    })
    .visible(on_marketplace);
    let picker_note = words(ui, browser.scope_note(), 10.0, Token::Ink2).aid(aid(browser, "picker-note")).visible(on_marketplace);
    let unavailable = browser.refresh_unavailable_note();
    let marketplace_unavailable = words(ui, unavailable.clone().unwrap_or_default(), 10.0, Token::Ink2)
        .aid(aid(browser, "marketplace-unavailable"))
        .visible(on_marketplace && unavailable.is_some_and(|n| !n.is_empty()));

    // What the read said: quiet while the list is good and nothing went wrong on the way to it.
    let snapshot: Option<Arc<ClaudePluginSnapshot>> = browser.snapshot();
    let output = snapshot.as_ref().map(|s| s.diagnostic_output.clone()).unwrap_or_default();
    let status_text = browser.status_text().unwrap_or_default();
    let status = ui
        .settings_text(
            status_text.clone(),
            TextStyle::size(11.0).ink(if browser.is_ready() && output.is_empty() { Token::Ink2 } else { Token::WaitText }).selectable(),
        )
        .aid(aid(browser, "load-status"))
        .visible(!status_text.is_empty());

    // What the last operation said: doneText when it succeeded, waitText otherwise.
    let result = browser.last_result();
    let result_text = browser.result_text().unwrap_or_default();
    let succeeded = result.as_ref().is_some_and(|r| r.status == status::SUCCEEDED);
    let ink = if succeeded { Token::DoneText } else { Token::WaitText };
    let mark = El::new(Kind::FontIcon {
        glyph: if succeeded { DONE_GLYPH } else { NOTE_GLYPH }.to_string(),
        size: Some(11.0),
        foreground: Some(ui.brush(ink)),
    })
    .va(VAlign::Top)
    .margin(Thickness::new(0.0, metrics::SPACING_XXS, 0.0, 0.0));
    let result_words =
        ui.settings_text(result_text.clone(), TextStyle::size(11.0).ink(ink).selectable()).aid(aid(browser, "operation-result")).cell(0, 1);
    let result_row = El::grid(Grid {
        children: vec![mark, result_words],
        cols: vec![Track::Auto, Track::Star(1.0)],
        col_spacing: metrics::SPACING_XS,
        ..Grid::default()
    })
    .visible(!result_text.is_empty());

    // The command's own output, 80 high behind its disclosure; closed whenever there is none.
    if output.is_empty() {
        diagnostics_open.set(false);
    }
    let open = diagnostics_open.get();
    let flip = diagnostics_open.clone();
    let disclosure_words = ui.text_block(plugin_strings::diagnostics_disclosure(), 10.0).va(VAlign::Center);
    let disclosure = El::stack(
        true,
        metrics::SPACING_XS,
        vec![El::font_icon(if open { UNFOLDED_GLYPH } else { FOLDED_GLYPH }, Some(8.0)).va(VAlign::Center), disclosure_words],
    );
    let mut toggle = El::button(disclosure)
        .named(plugin_strings::diagnostics_disclosure())
        .aid(aid(browser, "diagnostics"))
        .ha(HAlign::Left)
        .visible(!output.is_empty())
        .on_click(move || flip.update(|open| *open = !*open));
    ui.paint_plain_button(&mut toggle, Brush::TRANSPARENT, Some(ui.brush(Token::Ink)), None);
    let diagnostics_box =
        El::new(Kind::ScrollViewer { content: ui.settings_text(output.clone(), TextStyle::size(10.0).mono().selectable()).into() })
            .height(80.0)
            .visible(open && !output.is_empty());

    let controls = El::stack(
        false,
        metrics::SPACING_MD,
        vec![
            tabs,
            filters,
            picker_row,
            picker_note,
            marketplace_unavailable,
            status,
            result_row,
            toggle,
            diagnostics_box,
            words(ui, browser.footer_note(), 10.0, Token::Ink2),
        ],
    );
    let rule = El::border(Border { background: Some(ui.brush(Token::Line)), ..Border::default() }).height(metrics::STROKE_LINE).cell(1, 0);
    let row_panel = El::new(Kind::StackPanel(StackPanel {
        children: rows(ui, browser, actions),
        spacing: metrics::SPACING_MD,
        padding: Thickness::new(0.0, 0.0, metrics::SPACING_LG, 0.0),
        ..StackPanel::default()
    }));
    let scroller = El::new(Kind::ScrollViewer { content: row_panel.into() }).cell(2, 0);
    El::grid(Grid {
        children: vec![controls, rule, scroller],
        rows: vec![Track::Auto, Track::Auto, Track::Star(1.0)],
        row_spacing: metrics::SPACING_MD,
        ..Grid::default()
    })
}

/// Every automation id a built element tree shows (a hidden element's subtree is left out, as the page never draws it): the read-only
/// guard counts the ones that name a change.
pub fn automation_ids(el: &El) -> Vec<String> {
    if !el.visible {
        return vec![];
    }
    let mut ids: Vec<String> = el.aid.iter().cloned().collect();
    for child in children(el) {
        ids.extend(automation_ids(child));
    }
    ids
}
