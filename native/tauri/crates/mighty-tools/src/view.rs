//! The setup screen, the 구성 요소 and 내 작업 도구 모음 boxes and their dialogs (D10 the setup install's confirmation, D7 the toolkit
//! install's), built with mighty-native's kit as C# builds them (MainWindow.Setup.cs:175-415 `BuildSetupContent`, `FillSetupContent`,
//! `SetupItemRow`, `SetupAction`, `SetupFooter`, `BuildSetupConfirmation`; MainWindow.WebShell.Settings.cs:218-241 `ShowWebSetup`;
//! MainWindow.Settings.cs:285-501 `BuildComponentsSection`, `BuildToolkitSection`, `FillToolkitList`, `FillToolkitResults`,
//! `RunToolkitInstall`'s confirmation).
//!
//! Builders run on every read of their surface (four times a second): they read only the models they are given (the screen's
//! state, the toolkit's last read, the accounts' statuses) and do no I/O. What a control does is the host's: each takes the
//! [`SetupActions`] or [`ToolkitActions`] the host gives and calls it from its handler.

use std::collections::HashSet;
use std::sync::Arc;

use mighty_core::design_tokens::{Token, metrics};
use mighty_native::kit::{ControlSize, TextStyle};
use mighty_native::surface::{DefaultButton, DialogButton, DialogSpec};
use mighty_native::ui::{Content, El, Grid, Kind, Thickness, Track, Ui, VAlign};
use mighty_native::view::View;
use mighty_run::strings;

use crate::setup::catalog::{SetupCatalog, SetupGroup, SetupItem, SetupPlatform, SetupRecipe};
use crate::setup::probe::{SetupProbeState, SetupRowState, SetupRows};
use crate::setup::screen::{Confirmation, SetupScreen, WINDOW_HEIGHT, WINDOW_WIDTH};
use crate::toolkit::file::{ToolkitEntrySource, ToolkitFileEntry};
use crate::toolkit::runner::{ToolkitRunItem, Verdict, presentation};

/// The setup screen's surface id (the page's `data-native-view`).
pub const SETUP_SURFACE: &str = "setup";

/// Segoe Fluent symbols (copied by code point: `SettingsSymbol` takes them as private-use characters).
mod glyph {
    /// A warning line (`Warning`).
    pub const WARNING: &str = "\u{E7BA}";
    /// The toolkit's rows and the prerequisites (`Toolkit`… `DeveloperTools`).
    pub const TOOL: &str = "\u{E90F}";
    pub const STYLE: &str = "\u{E790}";
    pub const NETWORK: &str = "\u{E774}";
    pub const DEV: &str = "\u{E943}";
    pub const AGENT: &str = "\u{E756}";
    /// A result's verdict: installed, failed, skipped.
    pub const INSTALLED: &str = "\u{EC61}";
    pub const FAILED: &str = "\u{EB90}";
    pub const SKIPPED: &str = "\u{E738}";
}

/// What the setup rows read besides the screen: the agent CLIs signed out (the accounts' statuses, `LoggedIn == false`) and whether
/// a CLI update or a plugin operation runs (the update button waits).
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct SetupExtras {
    pub signed_out: HashSet<String>,
    pub cli_update_running: bool,
    pub plugin_operations_running: bool,
}

/// What the setup screen's controls do (C# `ToggleSetupSelection`, `StartCliSignIn`, the CLI update coordinator's `Start`, the vendor
/// page, `RefreshSetup(force: true)`, `RequestSetupInstall`, `AbandonSetupRun`).
pub trait SetupActions: Send + Sync {
    fn toggle(&self, id: &str);
    fn sign_in(&self, provider: &str);
    fn update(&self);
    fn open_page(&self, url: &str);
    fn recheck(&self);
    fn install(&self);
    fn abandon(&self);
}

fn text(ui: &Ui, words: impl Into<String>, style: TextStyle) -> El {
    ui.settings_text(words, style)
}

/// A ProgressRing turning, `side` square (MinWidth and MinHeight 0).
fn ring(side: f64) -> El {
    El::new(Kind::ProgressRing { active: true }).width(side).height(side)
}

/// C# `SetupAction`: a small push button named and worded by its key, with its automation id.
fn setup_action(ui: &Ui, key: &str, aid: String, prominent: bool, enabled: bool, action: impl Fn() + Send + Sync + 'static) -> El {
    let button = ui.button(strings::get(key), action).enabled(enabled);
    ui.settings_push(button, ControlSize::Small, prominent, false).aid(aid)
}

/// C# `SetupItemRow`: the box (hidden where nothing may be picked), then the name with its mark, capsules and version over the
/// state's word, the detail, what is out of date or unread, why it is ticked, and its own buttons.
fn item_row(ui: &Ui, screen: &SetupScreen, item: &SetupItem, added: bool, extras: &SetupExtras, actions: &Arc<dyn SetupActions>) -> El {
    let state = screen.state_of(item);
    let result = screen.results.get(&item.id);
    let selectable = SetupRows::is_selectable(state, item);
    let focused = screen.focus.as_deref() == Some(item.id.as_str());
    let picked = screen.selection.contains(&item.id);
    let running = screen.running();
    let id = item.id.clone();
    let toggle = actions.clone();
    let check = El::check_box(picked || added, Content::None)
        .enabled(selectable && !added && !running)
        .va(VAlign::Top)
        .opacity(if selectable || picked || added { 1.0 } else { 0.0 })
        .named(SetupCatalog::title(item))
        .aid(format!("setup-check-{}", item.id))
        .on_toggle(move |_| toggle.toggle(&id));
    let mut name = vec![];
    match item.provider() {
        Some(_) => name.push(mighty_native::kit::provider_mark()),
        None => name.push(ui.settings_symbol(
            match item.group {
                SetupGroup::Prerequisite => glyph::TOOL,
                SetupGroup::Style => glyph::STYLE,
                SetupGroup::Network => glyph::NETWORK,
                SetupGroup::Dev => glyph::DEV,
                SetupGroup::Agent => glyph::AGENT,
            },
            12.0,
            Token::Ink,
        )),
    }
    name.push(text(ui, SetupCatalog::title(item), TextStyle::size(13.0).medium()));
    if item.provider().is_some_and(mighty_run::providers::is_beta) {
        name.push(ui.beta_badge());
    }
    if item.required {
        name.push(ui.settings_capsule(
            strings::get("setup.badge.required"),
            ui.brush(Token::Accent),
            ui.brush_at(Token::Accent, 0.15),
            9.0,
            3.0,
        ));
    }
    if let Some(version) = result.and_then(|r| r.shown_version()) {
        name.push(text(ui, version, TextStyle::size(10.0).ink(Token::Ink2).mono()));
    }
    let ink = match state {
        SetupRowState::Installed => Token::DoneText,
        SetupRowState::Missing | SetupRowState::Outdated | SetupRowState::Attention => Token::WaitText,
        SetupRowState::Installing => Token::Accent,
        SetupRowState::Failed => Token::ErrText,
        SetupRowState::Checking => Token::Ink2,
    };
    let mut word = vec![];
    if state == SetupRowState::Installing {
        word.push(ring(10.0));
    }
    word.push(text(ui, strings::get(SetupRows::status_key(state)), TextStyle::size(10.0).ink(ink).medium()));
    let head = El::grid(Grid {
        children: vec![El::stack(true, metrics::SPACING_SM, name), El::stack(true, metrics::SPACING_XS, word).va(VAlign::Top).cell(0, 1)],
        cols: vec![Track::Star(1.0), Track::Auto],
        col_spacing: metrics::SPACING_SM,
        ..Grid::default()
    });
    let mut words = vec![head, text(ui, SetupCatalog::detail(item), TextStyle::size(11.0).ink(Token::Ink2))];
    if let Some(result) = result {
        match (result.state, result.minimum.as_deref()) {
            (SetupProbeState::Outdated, Some(minimum)) => words.push(text(
                ui,
                strings::get_with("setup.outdatedDetail", &[("version", result.version.as_deref().unwrap_or("?")), ("minimum", minimum)]),
                TextStyle::size(11.0).ink(Token::WaitText),
            )),
            (SetupProbeState::Unverified, Some(minimum)) => words.push(text(
                ui,
                strings::get_with("setup.unverifiedDetail", &[("minimum", minimum)]),
                TextStyle::size(11.0).ink(Token::WaitText),
            )),
            _ => {}
        }
    }
    if added {
        words.push(text(ui, strings::get("setup.addedForSelection"), TextStyle::size(10.0).ink(Token::Accent)));
    }
    let mut buttons = vec![];
    // Sign-in once an agent CLI is there and signed out; the CLI update for an old agent CLI; the page for what only a page installs.
    if let Some(provider) = item.provider() {
        if state == SetupRowState::Installed && extras.signed_out.contains(provider) {
            let (sign, provider) = (actions.clone(), provider.to_string());
            buttons.push(setup_action(ui, "setup.button.signIn", format!("setup-signin-{}", item.id), true, true, move || {
                sign.sign_in(&provider)
            }));
        }
        if state == SetupRowState::Outdated {
            let key = if extras.cli_update_running { "settings.components.inProgressLabel" } else { "setup.button.update" };
            let enabled = !extras.cli_update_running && !extras.plugin_operations_running;
            let update = actions.clone();
            buttons.push(setup_action(ui, key, format!("setup-update-{}", item.id), true, enabled, move || update.update()));
        }
    }
    if state != SetupRowState::Installed
        && let Some(SetupRecipe::VendorPage { url }) = item.entry(SetupPlatform::Windows).map(|entry| &entry.install)
    {
        let (open, url) = (actions.clone(), url.to_string());
        buttons
            .push(setup_action(ui, "setup.button.openPage", format!("setup-page-{}", item.id), false, true, move || open.open_page(&url)));
    }
    if !buttons.is_empty() {
        words.push(El::stack(true, metrics::SPACING_SM, buttons).margin(Thickness::new(0.0, metrics::SPACING_XXS, 0.0, 0.0)));
    }
    let side = if focused { metrics::SPACING_XS } else { 0.0 };
    El::grid(Grid {
        children: vec![check, El::stack(false, metrics::SPACING_XXS, words).cell(0, 1)],
        cols: vec![Track::Auto, Track::Star(1.0)],
        col_spacing: metrics::SPACING_SM,
        padding: Thickness::new(side, metrics::SPACING_XXS, side, metrics::SPACING_XXS),
        background: focused.then(|| ui.brush_at(Token::Accent, 0.12)),
        ..Grid::default()
    })
    .aid(format!("setup-row-{}", item.id))
    .keyed(item.id.clone())
}

/// C# `SetupFooter`'s warning line: the mark beside selectable words at most 520 wide.
fn warning(ui: &Ui, words: impl Into<String>, aid: &str) -> El {
    let mut said = text(ui, words, TextStyle::size(11.0).selectable());
    said.max_width = Some(520.0);
    El::stack(true, metrics::SPACING_SM, vec![ui.settings_symbol(glyph::WARNING, 12.0, Token::WaitText), said]).aid(aid)
}

/// C# `SetupFooter`: what is going on, then 다시 확인 and the install button.
fn footer(ui: &Ui, screen: &SetupScreen, plan: &[String], actions: &Arc<dyn SetupActions>) -> El {
    let mut rows = vec![];
    if let Some(message) = &screen.message {
        rows.push(warning(ui, message.clone(), "setup-message"));
    }
    if screen.run.as_ref().is_some_and(|run| run.not_started) {
        let mut hint = warning(ui, strings::get("setup.run.checkTerminal"), "setup-run-not-started");
        let abandon = actions.clone();
        let end = ui
            .settings_push(ui.button(strings::get("setup.run.end"), move || abandon.abandon()), ControlSize::Small, false, false)
            .aid("setup-run-end");
        if let Some(children) = hint.children_mut() {
            children.push(end);
        }
        rows.push(hint);
    }
    let running = screen.running();
    let mut buttons = vec![];
    if running {
        buttons.push(El::stack(
            true,
            metrics::SPACING_SM,
            vec![ring(12.0), text(ui, strings::get("setup.running"), TextStyle::size(11.0).ink(Token::Ink2))],
        ));
    }
    let recheck = actions.clone();
    let check =
        ui.button(strings::get(if screen.probing { "setup.button.checking" } else { "setup.button.recheck" }), move || recheck.recheck());
    buttons.push(ui.settings_push(check.enabled(!screen.probing), ControlSize::Regular, false, false).aid("setup-recheck").cell(0, 1));
    let install = actions.clone();
    let label = strings::get("setup.button.install");
    let button = El::button(label.clone()).named(label).enabled(!plan.is_empty() && !running).on_click(move || install.install());
    buttons.push(ui.settings_push(button, ControlSize::Regular, true, false).aid("setup-install").cell(0, 2));
    rows.push(El::grid(Grid {
        children: buttons,
        cols: vec![Track::Star(1.0), Track::Auto, Track::Auto],
        col_spacing: metrics::SPACING_SM,
        ..Grid::default()
    }));
    El::stack(false, metrics::SPACING_SM, rows)
}

/// C# `BuildSetupContent` with `FillSetupContent`: the groups in their order, each its 11pt heading over its rows, then the footer;
/// the setup window's body and Settings › 구성 요소'.
pub fn setup_content(ui: &Ui, screen: &SetupScreen, extras: &SetupExtras, actions: Arc<dyn SetupActions>) -> El {
    let plan = screen.plan_ids();
    let added: HashSet<&str> = plan.iter().map(String::as_str).filter(|id| !screen.selection.contains(*id)).collect();
    let items = screen.items();
    let mut sections = vec![];
    for group in SetupGroup::ALL {
        let group_items: Vec<&&SetupItem> = items.iter().filter(|item| item.group == group).collect();
        if group_items.is_empty() {
            continue;
        }
        let mut rows = vec![text(ui, strings::get(SetupCatalog::group_title_key(group)), TextStyle::size(11.0).ink(Token::Ink2).medium())];
        for item in group_items {
            rows.push(item_row(ui, screen, item, added.contains(item.id.as_str()), extras, &actions));
        }
        sections.push(El::stack(false, metrics::SPACING_SM, rows));
    }
    sections.push(footer(ui, screen, &plan, &actions));
    El::stack(false, metrics::SPACING_LG, sections).aid("setup-list")
}

/// C# `ShowWebSetup`'s view: the list, then its never-show box (`setup-never-show`, ticked from `never_show`) at the footer's left; the
/// header's ✕ is 다음에 하기.
pub fn setup_view(list: El, never_show: bool, toggle_never_show: impl Fn(bool) + Send + Sync + 'static) -> View {
    let footer = El::check_box(never_show, strings::get("setup.button.neverShow")).aid("setup-never-show").on_toggle(toggle_never_show);
    View::Setup {
        title: strings::get("setup.title"),
        subtitle: strings::get("setup.windows.subtitle"),
        close: strings::get("setup.button.later"),
        body: Some(list),
        footer: Some(footer),
        width: WINDOW_WIDTH,
        height: WINDOW_HEIGHT,
    }
}

/// C# `BuildSetupConfirmation` (D10): the one confirmation before anything runs — what Windows will do, then (at most 340 high)
/// the lines that run first and every step's command exactly as the script has it, in order. Run is the primary, Cancel the
/// default.
pub fn setup_confirmation(confirmation: &Confirmation) -> DialogSpec {
    let (preamble, steps) = (confirmation.preamble.join("\n"), confirmation.steps.clone());
    let mut spec = DialogSpec::new(strings::get("setup.confirm.title"), move |ui: &mut Ui| {
        let mut body = vec![
            El::stack(
                false,
                metrics::SPACING_XS,
                vec![
                    text(ui, strings::get("setup.confirm.preamble"), TextStyle::size(12.0).medium()),
                    text(ui, preamble.clone(), TextStyle::size(11.0).mono().selectable()),
                ],
            )
            .aid("setup-confirm-preamble"),
        ];
        for (index, step) in steps.iter().enumerate() {
            body.push(
                El::stack(
                    false,
                    metrics::SPACING_XS,
                    vec![
                        text(ui, format!("{}. {}", index + 1, step.title), TextStyle::size(12.0).medium()),
                        text(ui, step.body.clone(), TextStyle::size(11.0).mono().selectable()),
                    ],
                )
                .aid(format!("setup-confirm-step-{}", step.item_id)),
            );
        }
        let scroll = El::new(Kind::ScrollViewer { content: El::stack(false, metrics::SPACING_MD, body).into() }).max_height(340.0);
        let content = El::stack(
            false,
            metrics::SPACING_MD,
            vec![text(ui, strings::get("setup.windows.confirm.body"), TextStyle::size(12.0).ink(Token::Ink2)), scroll],
        );
        Content::El(Box::new(content))
    })
    .styled(None, None);
    spec.primary = Some(DialogButton::new(strings::get("setup.confirm.run")));
    spec.close = Some(strings::get("setup.confirm.cancel"));
    spec.default_button = DefaultButton::Close;
    spec
}

/// C# `BuildComponentsSection`: the setup screen's explanation over its list, two rows of the box.
pub fn components_box(ui: &Ui, list: El) -> El {
    let mut rows = ui.settings_rows();
    rows.add(text(ui, strings::get("setup.windows.subtitle"), TextStyle::size(11.0).ink(Token::Ink2)));
    rows.add(list);
    rows.finish()
}

/// One tool of the box as its last read found it.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ToolkitRow {
    pub entry: ToolkitFileEntry,
    /// `store.GetApproval(entry)` is set (never for the bundled entry).
    pub approved: bool,
    /// The file-only probe says it is there.
    pub installed: bool,
}

/// The toolkit box's model: the store's list with each tool's approval and probe, what the file says when it failed, whether an
/// install runs, and the last run's results.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct ToolkitModel {
    pub rows: Vec<ToolkitRow>,
    pub error: Option<String>,
    /// Why the install did nothing in this run of the app (the verification mode, no `--allow-state-write`): a line of its own, not
    /// the file's error.
    pub notice: Option<String>,
    pub running: bool,
    pub results: Option<Vec<ToolkitRunItem>>,
}

/// What the toolkit box's buttons do (C#'s add, export, import, install, approve and remove, each through `ToolkitFileAction` but the
/// install).
pub trait ToolkitActions: Send + Sync {
    fn add(&self);
    fn export(&self);
    fn import(&self);
    fn install(&self);
    fn approve(&self, id: &str);
    fn remove(&self, id: &str);
}

/// C# `FillToolkitList`'s row: the mark, the 13pt medium name and the bundled or approved capsule, the probe's word at the trailing
/// edge; a user's tool its approve (while unapproved) and remove buttons.
fn toolkit_row(ui: &Ui, row: &ToolkitRow, running: bool, actions: &Arc<dyn ToolkitActions>) -> El {
    let entry = &row.entry;
    let bundled = entry.source == ToolkitEntrySource::Bundled;
    let mut name =
        vec![ui.settings_symbol(glyph::TOOL, 12.0, Token::Ink), text(ui, entry.display_name.clone(), TextStyle::size(13.0).medium())];
    if bundled {
        name.push(ui.settings_capsule(
            strings::get("settings.toolkit.bundledBadge"),
            ui.brush(Token::Ink2),
            ui.brush_at(Token::Ink2, 0.15),
            9.0,
            3.0,
        ));
    } else if row.approved {
        name.push(ui.settings_capsule(
            strings::get("settings.toolkit.approvedBadge"),
            ui.brush(Token::DoneText),
            ui.brush(Token::DoneSoft),
            9.0,
            3.0,
        ));
    }
    let (word, ink) = if row.installed {
        ("settings.toolkit.statusInstalled", Token::DoneText)
    } else {
        ("settings.toolkit.statusMissing", Token::WaitText)
    };
    let header = El::grid(Grid {
        children: vec![
            El::stack(true, metrics::SPACING_SM, name),
            text(ui, strings::get(word), TextStyle::size(10.0).ink(ink).medium()).cell(0, 1),
        ],
        cols: vec![Track::Star(1.0), Track::Auto],
        col_spacing: metrics::SPACING_SM,
        ..Grid::default()
    });
    let mut parts = vec![header];
    if !bundled {
        let mut buttons = vec![];
        if !row.approved {
            buttons.push(text(ui, strings::get("settings.toolkit.needsApproval"), TextStyle::size(11.0).ink(Token::Ink2)));
            let (approve, id) = (actions.clone(), entry.id.clone());
            let button =
                El::button(strings::get("settings.toolkit.approveButton")).enabled(!running).on_click(move || approve.approve(&id));
            buttons.push(ui.settings_push(button, ControlSize::Mini, false, false).aid(format!("toolkit-approve-{}", entry.id)));
        }
        let (remove, id) = (actions.clone(), entry.id.clone());
        let button = El::button(strings::get("settings.toolkit.removeButton")).enabled(!running).on_click(move || remove.remove(&id));
        buttons.push(ui.settings_push(button, ControlSize::Mini, false, false).aid(format!("toolkit-remove-{}", entry.id)));
        parts.push(El::stack(true, metrics::SPACING_SM, buttons));
    }
    El::stack(false, metrics::SPACING_XS, parts)
        .margin(Thickness::new(0.0, metrics::SPACING_XXS, 0.0, metrics::SPACING_XXS))
        .aid(format!("toolkit-entry-{}", entry.id))
        .keyed(entry.id.clone())
}

/// C# `FillToolkitResults`: one line per tool with its verdict's mark and word.
fn toolkit_results(ui: &Ui, results: &[ToolkitRunItem], entries: &[ToolkitFileEntry]) -> El {
    let lines = results
        .iter()
        .map(|item| {
            let (key, mark, ink) = match item.verdict {
                Verdict::Installed => ("settings.toolkit.verdictInstalled", glyph::INSTALLED, Token::DoneText),
                Verdict::Failed => ("settings.toolkit.verdictFailed", glyph::FAILED, Token::ErrText),
                Verdict::Skipped => ("settings.toolkit.verdictSkipped", glyph::SKIPPED, Token::Ink2),
            };
            El::grid(Grid {
                children: vec![
                    ui.settings_symbol(mark, 11.0, ink),
                    text(ui, presentation::result_name(item, entries), TextStyle::size(11.0)).cell(0, 1),
                    text(ui, strings::get(key), TextStyle::size(10.0).ink(ink).medium()).cell(0, 2),
                ],
                cols: vec![Track::Auto, Track::Star(1.0), Track::Auto],
                col_spacing: metrics::SPACING_SM,
                ..Grid::default()
            })
        })
        .collect();
    El::stack(false, metrics::SPACING_XXS, lines)
        .margin(Thickness::new(0.0, metrics::SPACING_XS, 0.0, metrics::SPACING_XS))
        .aid("settings-toolkit-results")
}

/// C# `BuildToolkitSection` with `FillToolkitList`: the line that says toolkit.json failed and why (shown only then), one row per
/// tool, the last run's results (shown once there are any), then the explanation beside add, export, import and install. While an
/// install runs every button waits.
pub fn toolkit_box(ui: &Ui, model: &ToolkitModel, actions: Arc<dyn ToolkitActions>) -> El {
    let mut rows = ui.settings_rows();
    let mut mark = ui.settings_symbol(glyph::WARNING, 12.0, Token::WaitText).va(VAlign::Top);
    mark.margin = Thickness::new(0.0, 1.0, 0.0, 0.0);
    let banner = El::grid(Grid {
        children: vec![
            mark,
            text(ui, model.error.as_deref().map(presentation::error_banner).unwrap_or_default(), TextStyle::size(11.0).selectable())
                .cell(0, 1),
        ],
        cols: vec![Track::Auto, Track::Star(1.0)],
        col_spacing: metrics::SPACING_SM,
        ..Grid::default()
    });
    rows.add(banner).visible = model.error.is_some();
    let notice = text(ui, model.notice.clone().unwrap_or_default(), TextStyle::size(11.0).ink(Token::Ink2)).aid("settings-toolkit-notice");
    rows.add(notice).visible = model.notice.is_some();
    for row in &model.rows {
        rows.add(toolkit_row(ui, row, model.running, &actions));
    }
    let entries: Vec<ToolkitFileEntry> = model.rows.iter().map(|row| row.entry.clone()).collect();
    let results = model.results.as_deref().unwrap_or(&[]);
    rows.add(toolkit_results(ui, results, &entries)).visible = !results.is_empty();
    let button = |key: &str, aid: &str, size: ControlSize, prominent: bool, act: Box<dyn Fn() + Send + Sync>| {
        let el = El::button(strings::get(key)).enabled(!model.running).on_click(act);
        ui.settings_push(el, size, prominent, false).aid(aid)
    };
    let (add, export, import, install) = (actions.clone(), actions.clone(), actions.clone(), actions);
    let buttons = vec![
        button("settings.toolkit.addButton", "settings-toolkit-add", ControlSize::Small, false, Box::new(move || add.add())),
        button("settings.toolkit.exportButton", "settings-toolkit-export", ControlSize::Small, false, Box::new(move || export.export())),
        button("settings.toolkit.importButton", "settings-toolkit-import", ControlSize::Small, false, Box::new(move || import.import())),
        button(
            if model.running { "settings.toolkit.installingButton" } else { "settings.toolkit.installButton" },
            "settings-toolkit-install",
            ControlSize::Regular,
            false,
            Box::new(move || install.install()),
        ),
    ];
    rows.add(ui.settings_labeled(
        text(ui, strings::get("settings.toolkit.sectionDescription"), TextStyle::size(11.0).ink(Token::Ink2)),
        El::stack(true, metrics::SPACING_SM, buttons),
        false,
    ));
    rows.finish()
}

/// C# `RunToolkitInstall`'s confirmation (D7): what runs, then (at most 300 high) each tool's name over every command it will run,
/// or over the line that says it is left out.
pub fn toolkit_confirmation(rows: Vec<presentation::PlanRow>) -> DialogSpec {
    let mut spec = DialogSpec::new(strings::get("settings.toolkit.confirmTitle"), move |ui: &mut Ui| {
        let tools = rows
            .iter()
            .map(|row| {
                let mut parts = vec![text(ui, row.name.clone(), TextStyle::size(12.0).medium())];
                for line in &row.lines {
                    let style = TextStyle::size(11.0).ink(if row.skipped { Token::Ink2 } else { Token::Ink }).mono();
                    parts.push(text(ui, line.clone(), if row.skipped { style } else { style.selectable() }));
                }
                El::stack(false, metrics::SPACING_XS, parts).margin(Thickness::new(0.0, metrics::SPACING_XXS, 0.0, metrics::SPACING_XXS))
            })
            .collect();
        let scroll = El::new(Kind::ScrollViewer { content: El::stack(false, metrics::SPACING_SM, tools).into() }).max_height(300.0);
        let content = El::stack(
            false,
            metrics::SPACING_MD,
            vec![text(ui, strings::get("settings.toolkit.confirmDescription"), TextStyle::size(12.0).ink(Token::Ink2)), scroll],
        );
        Content::El(Box::new(content))
    })
    .styled(None, None);
    spec.primary = Some(DialogButton::new(strings::get("settings.toolkit.confirmInstall")));
    spec.close = Some(strings::get("settings.toolkit.cancelButton"));
    spec
}
