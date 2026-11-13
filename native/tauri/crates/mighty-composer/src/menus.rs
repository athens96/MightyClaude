//! The composer's menus and run settings as the page reads them: `composerMenu` rows (C# `WebMenu` over `ModelMenu`,
//! `ChoiceMenu(EffortChoices)`, `ChoiceMenu(PermissionChoices)`, `OptionsMenu`, `PaneActionsMenu`; MainWindow.WebShell.Composer.cs:381-430,
//! MainWindow.cs:846-939), the row a `composerMenuChoose` path runs, `runSettings` (`WebRunSettings`, :459), Apply's checks
//! (`RunSettingsFrom`, MainWindow.RunSettings.cs:211; `WebApplyRunSettings`, :486), and the slash palette's rows for the page
//! (`WebSlashCatalog`, :355, with `ArgumentChoices`, MainWindow.SlashPalette.cs:103).

use mighty_core::locale::Locale;
use mighty_core::model::{RunSession, RunSettings};
use mighty_core::{model_label, normalize, wire};
use serde::Serialize;

use crate::runtime::{self, CatalogModel, ModelCatalog, ModelListNotice, ProviderCapabilities, ProviderRuntime};
use crate::slash::{self, SlashArgument, SlashCommand};

/// What choosing a row does (the `Func<Task>` the WinUI row carries in its `Tag`); the host runs it.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum MenuAction {
    /// `ChangeProvider(value)`.
    ChangeProvider(String),
    /// `RefreshPaneModels`.
    RefreshModels,
    /// `ChangeModel(value)`.
    ChangeModel(String),
    /// `CustomModel`: the dialog for a model id typed by hand.
    CustomModel,
    /// `ChangeSettings(s => s with { Effort })`.
    SetEffort(String),
    /// `ChangeSettings(s => s with { PermissionMode, NetworkAccess })`, the network switch kept only where Codex may use it.
    SetPermission(String),
    /// Codex Fast on or off.
    ToggleFast,
    /// `ShowRunSettings`: the page draws the run settings itself.
    RunSettings,
    /// `RenameSession`.
    Rename,
    /// `OpenPluginBrowser(provider)`.
    OpenPlugins(String),
    /// `ResetConversation`.
    NewConversation,
    /// A style menu row (the style engine's, W9).
    Style(String),
}

/// One row of a menu, as `WebMenu` reads it (MainWindow.WebShell.Composer.cs:402).
#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(tag = "kind", rename_all = "camelCase")]
pub enum MenuRow {
    Submenu {
        text: String,
        enabled: bool,
        items: Vec<MenuRow>,
    },
    Choice {
        text: String,
        enabled: bool,
        checked: bool,
        #[serde(skip_serializing_if = "Option::is_none")]
        help: Option<String>,
        #[serde(skip)]
        action: Option<MenuAction>,
    },
    Note {
        text: String,
        help: String,
    },
    Header {
        text: String,
    },
    RunSettings {
        text: String,
        #[serde(skip)]
        action: Option<MenuAction>,
    },
    Action {
        text: String,
        enabled: bool,
        #[serde(skip_serializing_if = "Option::is_none")]
        help: Option<String>,
        #[serde(skip)]
        action: Option<MenuAction>,
    },
    Separator,
}

fn choice(text: String, action: MenuAction, checked: bool, help: Option<String>) -> MenuRow {
    MenuRow::Choice { text, enabled: true, checked, help, action: Some(action) }
}

fn item(text: String, action: MenuAction) -> MenuRow {
    MenuRow::Action { text, enabled: true, help: None, action: Some(action) }
}

/// What the menus read of the pane and the window.
pub struct MenuInputs<'a> {
    pub locale: Locale,
    pub pane: &'a RunSession,
    pub runtime: Option<&'a ProviderRuntime>,
    /// `ProviderCatalog.Efforts(...)` for the pane's model.
    pub efforts: &'a [String],
    /// `service.IsRemoteSession(pane.Id)`.
    pub remote_session: bool,
    /// `refreshingPaneModels`.
    pub refreshing_models: bool,
    /// The style menu's rows (`stylePicker.Flyout`), from the style engine (W9); None: the menu has only "CLI".
    pub style_rows: Option<Vec<MenuRow>>,
}

impl MenuInputs<'_> {
    fn catalog(&self) -> ModelCatalog {
        self.runtime.map(|r| r.catalog.clone()).unwrap_or_else(|| ModelCatalog::fallback(self.pane.provider(), &self.locale))
    }
    fn capabilities(&self) -> ProviderCapabilities {
        self.runtime.map(|r| r.capabilities.clone()).unwrap_or_else(|| ProviderCapabilities::fallback(self.pane.provider()))
    }
    fn settings(&self) -> RunSettings {
        self.pane.settings.clone().unwrap_or_default()
    }
}

/// C# `ModelLabel.PickerOptions` (ModelLabel.cs:103): the picker's rows with their labels; a saved model the list lacks marked.
pub fn picker_options(pane: &RunSession, catalog: &ModelCatalog, l: &Locale) -> Vec<CatalogModel> {
    let hint = model_label::reported_model(pane);
    let options = catalog.options();
    let mut rows: Vec<CatalogModel> = catalog
        .models
        .iter()
        .zip(&options)
        .map(|(m, o)| CatalogModel { display_name: model_label::option(o, if m.value == pane.model() { hint } else { None }), ..m.clone() })
        .collect();
    if !rows.iter().any(|o| o.value == "default") {
        rows.insert(
            0,
            CatalogModel {
                value: "default".into(),
                display_name: l.get("composer.model.cliDefault"),
                description: String::new(),
                resolved_model: None,
            },
        );
    }
    if !rows.iter().any(|o| o.value == pane.model()) {
        rows.push(CatalogModel {
            value: pane.model().into(),
            display_name: format!("{} · {}", model_label::selection(pane, &options, l), l.get("composer.model.saved")),
            description: String::new(),
            resolved_model: None,
        });
    }
    rows
}

/// `ModelMenu` (MainWindow.cs:858).
fn model_menu(i: &MenuInputs, catalog: &ModelCatalog) -> Vec<MenuRow> {
    let l = &i.locale;
    let pane = i.pane;
    let mut rows = vec![MenuRow::Header { text: l.get("composer.label.runner") }];
    for value in wire::PROVIDERS.iter().filter(|p| !runtime::is_local_only(p) || !i.remote_session) {
        rows.push(choice(
            runtime::beta_label(value, runtime::provider_label(value), l),
            MenuAction::ChangeProvider(value.to_string()),
            pane.provider() == *value,
            None,
        ));
    }
    rows.push(MenuRow::Separator);
    rows.push(MenuRow::Action {
        text: l.get(if i.refreshing_models { "composer.model.refreshing" } else { "composer.model.refresh" }),
        enabled: !i.refreshing_models,
        help: None,
        action: Some(MenuAction::RefreshModels),
    });
    if let Some(ModelListNotice { line, help }) = &catalog.notice {
        rows.push(MenuRow::Note { text: line.clone(), help: help.clone() });
    }
    rows.push(MenuRow::Separator);
    rows.push(MenuRow::Header { text: l.get("composer.label.model") });
    for row in picker_options(pane, catalog, l) {
        let help = (!row.description.is_empty()).then(|| row.description.clone());
        rows.push(choice(row.display_name, MenuAction::ChangeModel(row.value.clone()), pane.model() == row.value, help));
    }
    if pane.provider() != "gemini" {
        rows.push(MenuRow::Separator);
        rows.push(item(l.get("composer.model.enterIdMenu"), MenuAction::CustomModel));
    }
    rows
}

/// `EffortChoices` (MainWindow.cs:876): Auto, then the levels the model takes.
fn effort_choices(i: &MenuInputs) -> Vec<MenuRow> {
    let l = &i.locale;
    let effort = i.settings().effort;
    std::iter::once("default".to_string())
        .chain(i.efforts.iter().cloned())
        .map(|value| {
            let help = (value == "default").then(|| l.get("composer.effort.auto"));
            choice(runtime::effort_label(&value, l), MenuAction::SetEffort(value.clone()), effort == value, help)
        })
        .collect()
}

/// `PermissionChoices` (MainWindow.cs:880): the modes this runner takes, in the Mac's order.
fn permission_choices(i: &MenuInputs, caps: &ProviderCapabilities) -> Vec<MenuRow> {
    let l = &i.locale;
    let provider = i.pane.provider();
    let mode = i.settings().permission_mode;
    let offered: Vec<&String> =
        caps.permission_modes.iter().filter(|m| normalize::permission_modes(provider).contains(&m.as_str())).collect();
    ["plan", "manual", "acceptEdits", "auto", "onRequest", "fullAccess"]
        .into_iter()
        .filter(|m| offered.iter().any(|o| o.as_str() == *m))
        .map(|m| {
            choice(
                runtime::permission_label(provider, m, l),
                MenuAction::SetPermission(m.into()),
                mode == m,
                Some(runtime::permission_help(provider, m, l)),
            )
        })
        .collect()
}

/// `OptionsMenu` (MainWindow.cs:891): effort and permission as submenus, Fast for Codex, then the run settings.
fn options_menu(i: &MenuInputs, caps: &ProviderCapabilities) -> Vec<MenuRow> {
    let l = &i.locale;
    let pane = i.pane;
    let settings = i.settings();
    let mut rows = Vec::new();
    if caps.effort || settings.effort != "default" {
        rows.push(MenuRow::Submenu {
            text: format!("{} · {}", l.get("composer.effort.label"), runtime::effort_pill_label(&settings.effort, l)),
            enabled: !i.efforts.is_empty() || settings.effort != "default",
            items: effort_choices(i),
        });
    }
    rows.push(MenuRow::Submenu {
        text: format!(
            "{} · {}",
            l.get("composer.label.permission"),
            runtime::permission_label(pane.provider(), &settings.permission_mode, l)
        ),
        enabled: true,
        items: permission_choices(i, caps),
    });
    if pane.provider() == "codex" && (caps.fast_mode || settings.fast_mode) {
        rows.push(MenuRow::Choice {
            text: l.get(if settings.fast_mode { "composer.fast.on" } else { "composer.fast.off" }),
            enabled: caps.fast_mode || settings.fast_mode,
            checked: settings.fast_mode,
            help: Some(l.get("composer.fast.tooltip")),
            action: Some(MenuAction::ToggleFast),
        });
    }
    rows.push(MenuRow::Separator);
    rows.push(MenuRow::RunSettings { text: l.get("composer.runSettings.more"), action: Some(MenuAction::RunSettings) });
    rows
}

/// `CliUpdateService.ProviderLabel`, as the plugin window's title names the CLI.
fn plugin_provider_label(provider: &str) -> &'static str {
    match provider {
        "codex" => "Codex",
        _ => "Claude",
    }
}

/// `PaneActionsMenu` (MainWindow.cs:918): rename, the plugin window, a new conversation. The rows' enablement is set when the menu
/// opens, which the page's read never does, so they read as enabled.
fn pane_actions_menu(i: &MenuInputs) -> Vec<MenuRow> {
    let l = &i.locale;
    let pane = i.pane;
    // RenameStrings.MenuEntry (RenameStrings.cs:7), PluginStrings.TitleTemplate (PluginStrings.cs:15).
    let mut rows = vec![item(l.get("menu.rename"), MenuAction::Rename)];
    if pane.kind() == "claude" && matches!(pane.provider(), "claude" | "codex") {
        rows.push(item(
            l.get("plugins.titleTemplate").replace("{provider}", plugin_provider_label(pane.provider())),
            MenuAction::OpenPlugins(pane.provider().into()),
        ));
    }
    if pane.kind() == "claude" {
        rows.push(MenuRow::Separator);
        rows.push(item(l.get("composer.newConversation"), MenuAction::NewConversation));
    }
    rows
}

/// C# `WebBuildMenu` + `WebMenu` (MainWindow.WebShell.Composer.cs:381, 399): the rows of the menu a pill opens; Err ("Unknown
/// menu.") for any other name.
pub fn composer_menu(name: &str, i: &MenuInputs) -> Result<Vec<MenuRow>, String> {
    let caps = i.capabilities();
    Ok(match name {
        "model" => model_menu(i, &i.catalog()),
        "effort" => effort_choices(i),
        "permission" => permission_choices(i, &caps),
        "options" => options_menu(i, &caps),
        "actions" => pane_actions_menu(i),
        "style" => i
            .style_rows
            .clone()
            .unwrap_or_else(|| vec![MenuRow::Choice { text: "CLI".into(), enabled: true, checked: true, help: None, action: None }]),
        _ => return Err("Unknown menu.".into()),
    })
}

/// C# `WebMenuChoose` (MainWindow.WebShell.Composer.cs:418): the action of the row at `path` (indices, a submenu's row after its
/// own); Err ("Unknown menu row.") for a path off the menu, None for a row that is disabled or does nothing.
pub fn menu_action(rows: &[MenuRow], path: &[i64]) -> Result<Option<MenuAction>, String> {
    let mut items = rows;
    let mut row: Option<&MenuRow> = None;
    for &index in path {
        if index < 0 || index as usize >= items.len() {
            return Err("Unknown menu row.".into());
        }
        let at = &items[index as usize];
        row = Some(at);
        items = match at {
            MenuRow::Submenu { items, .. } => items,
            _ => &[],
        };
    }
    Ok(match row {
        Some(MenuRow::Choice { enabled: true, action, .. } | MenuRow::Action { enabled: true, action, .. }) => action.clone(),
        Some(MenuRow::RunSettings { action, .. }) => action.clone(),
        _ => None,
    })
}

// ── run settings ────────────────────────────────────────────────────────────────────────────────────────────────────────

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct RunSettingsValues {
    pub fast_mode: bool,
    pub web_search: String,
    pub network_access: bool,
    pub max_turns: String,
    pub max_budget_usd: String,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
pub struct SearchDescriptions {
    pub default: String,
    pub disabled: String,
    pub cached: String,
    pub live: String,
}

/// `WebRunSettings` (MainWindow.WebShell.Composer.cs:459): what the run settings popover shows and the values it starts from.
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct RunSettingsInfo {
    pub provider: String,
    pub beta: bool,
    pub model: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub model_description: Option<String>,
    pub model_source: String,
    pub permission: String,
    pub permission_description: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub approval: Option<String>,
    pub fast_supported: bool,
    pub web_search: bool,
    pub network_access: bool,
    pub network_allowed: bool,
    pub network_note: String,
    pub max_turns: bool,
    pub max_budget_usd: bool,
    pub values: RunSettingsValues,
    pub search_descriptions: SearchDescriptions,
    pub held: bool,
}

/// C# `WebRunSettings` (MainWindow.WebShell.Composer.cs:459).
pub fn run_settings_info(i: &MenuInputs) -> RunSettingsInfo {
    let l = &i.locale;
    let pane = i.pane;
    let provider = pane.provider();
    let caps = i.capabilities();
    let catalog = i.catalog();
    let settings = i.settings();
    let mode = settings.permission_mode.as_str();
    let allowed = matches!(mode, "acceptEdits" | "onRequest");
    RunSettingsInfo {
        provider: runtime::provider_label(provider).into(),
        beta: runtime::is_beta(provider),
        model: model_label::selection(pane, &catalog.options(), l),
        model_description: catalog.find(pane.model()).map(|m| m.description.clone()).filter(|d| !d.is_empty()),
        model_source: l.get(if catalog.source == "cli" { "settings.run.modelSourceCli" } else { "settings.run.modelSourceDefault" }),
        permission: runtime::permission_label(provider, mode, l),
        permission_description: runtime::permission_description(provider, mode, l),
        approval: (mode != "fullAccess").then(|| {
            l.get(if provider == "claude" || provider == "codex" && mode == "onRequest" {
                "settings.run.approvalInApp"
            } else {
                "settings.run.approvalUnsupported"
            })
        }),
        fast_supported: caps.fast_mode,
        web_search: caps.web_search,
        network_access: caps.network_access,
        network_allowed: allowed,
        network_note: l.get(if mode == "fullAccess" {
            "settings.run.shellNetworkFullAccess"
        } else if allowed {
            "settings.run.shellNetworkAcceptEdits"
        } else {
            "settings.run.shellNetworkLocked"
        }),
        max_turns: caps.max_turns,
        max_budget_usd: caps.max_budget_usd,
        values: RunSettingsValues {
            fast_mode: settings.fast_mode,
            web_search: settings.web_search.clone(),
            network_access: settings.network_access,
            max_turns: settings.max_turns.map(|t| t.to_string()).unwrap_or_default(),
            max_budget_usd: settings.max_budget_usd.map(|b| format!("{b}")).unwrap_or_default(),
        },
        search_descriptions: SearchDescriptions {
            default: runtime::search_description("default", l),
            disabled: runtime::search_description("disabled", l),
            cached: runtime::search_description("cached", l),
            live: runtime::search_description("live", l),
        },
        held: pane.status() == "running",
    }
}

/// What `applyRunSettings` sends (`{fastMode, webSearch, networkAccess, maxTurns, maxBudgetUsd}`), the limits as typed.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct RunSettingsForm {
    pub fast_mode: bool,
    pub web_search: String,
    pub network_access: bool,
    pub max_turns: Option<String>,
    pub max_budget_usd: Option<String>,
}

/// C# `RunSettingsFrom` (MainWindow.RunSettings.cs:211): the settings an Apply saves, or why it refuses. A network switch only where
/// the mode allows it, max turns 1-1000, a budget in (0, 10000]; then `validate` (the run engine's `StartRunRequest.Validate`, W2).
pub fn run_settings_from(
    pane: &RunSession,
    form: &RunSettingsForm,
    validate: &dyn Fn(&RunSettings) -> Result<(), String>,
    l: &Locale,
) -> Result<RunSettings, String> {
    let current = pane.settings.clone().unwrap_or_default();
    let mut setting = RunSettings {
        fast_mode: form.fast_mode,
        web_search: form.web_search.clone(),
        network_access: matches!(current.permission_mode.as_str(), "acceptEdits" | "onRequest") && form.network_access,
        max_turns: None,
        max_budget_usd: None,
        ..current
    };
    let (turns, budget) = (form.max_turns.as_deref(), form.max_budget_usd.as_deref());
    if let Some(wanted) = turns.map(crate::slash::dotnet_trim).filter(|t| !t.is_empty()) {
        // NumberStyles.None: digits only.
        match wanted.parse::<i32>().ok().filter(|_| wanted.bytes().all(|b| b.is_ascii_digit())).filter(|c| (1..=1000).contains(c)) {
            Some(count) => setting.max_turns = Some(count),
            None => return Err(l.get("settings.run.maxTurnsError")),
        }
    }
    if let Some(cap) = budget.map(crate::slash::dotnet_trim).filter(|b| !b.is_empty()) {
        // NumberStyles.AllowDecimalPoint: digits with one optional point, no sign, no exponent.
        let shaped = cap.bytes().all(|b| b.is_ascii_digit() || b == b'.') && cap.bytes().filter(|b| *b == b'.').count() <= 1 && cap != ".";
        match cap.parse::<f64>().ok().filter(|a| shaped && a.is_finite() && *a > 0.0 && *a <= 10_000.0) {
            Some(amount) => setting.max_budget_usd = Some(amount),
            None => return Err(l.get("settings.run.maxBudgetError")),
        }
    }
    validate(&setting)?;
    Ok(setting)
}

/// C# `WebApplyRunSettings` (MainWindow.WebShell.Composer.cs:486): the refusal's words, or the settings to save. `alive`: the pane is
/// still there; `locked`: a run is starting or running, or a background update holds the pane.
pub fn apply_run_settings(
    pane: &RunSession,
    alive: bool,
    locked: bool,
    form: &RunSettingsForm,
    validate: &dyn Fn(&RunSettings) -> Result<(), String>,
    l: &Locale,
) -> Result<RunSettings, String> {
    if !alive {
        return Err(l.get("remote.error.paneNotFound"));
    }
    if locked {
        return Err(l.get("phone.session.settingsLocked"));
    }
    run_settings_from(pane, form, validate, l)
}

// ── the slash palette's rows for the page ───────────────────────────────────────────────────────────────────────────────

/// `WebSlashRow` (MainWindow.WebShell.Composer.cs:352).
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct SlashRow {
    pub invocation: String,
    pub description: String,
    pub source: String,
    pub origin: &'static str,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub action: Option<&'static str>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub argument: Option<&'static str>,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub action_arg: Option<String>,
}

impl SlashRow {
    pub fn of(c: &SlashCommand, l: &Locale) -> Self {
        SlashRow {
            invocation: c.invocation.clone(),
            description: slash::palette::description(c, l),
            source: c.source.clone(),
            origin: c.origin.name(),
            action: c.action.map(|a| a.name()),
            argument: c.argument.map(|a| a.name()),
            action_arg: c.action_arg.clone(),
        }
    }
}

/// `WebSlashCatalog`'s answer: the built-ins, the scanned catalogue, and each argument built-in's choices.
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
pub struct SlashCatalog {
    pub builtins: Vec<SlashRow>,
    pub scanned: Vec<SlashRow>,
    pub choices: indexmap_like::OrderedMap,
}

/// A JSON object that keeps its keys in insertion order (C# `Dictionary` writes them so).
pub mod indexmap_like {
    use serde::ser::SerializeMap;

    #[derive(Debug, Clone, PartialEq, Eq, Default)]
    pub struct OrderedMap(pub Vec<(String, Vec<super::SlashRow>)>);

    impl serde::Serialize for OrderedMap {
        fn serialize<S: serde::Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {
            let mut map = serializer.serialize_map(Some(self.0.len()))?;
            for (key, rows) in &self.0 {
                map.serialize_entry(key, rows)?;
            }
            map.end()
        }
    }
}

/// `ArgumentChoices` (MainWindow.SlashPalette.cs:103): the models or permission modes a built-in's argument completes into.
pub fn argument_choices(argument: SlashArgument, command: &str, i: &MenuInputs) -> Vec<SlashCommand> {
    let l = &i.locale;
    let pane = i.pane;
    if argument == SlashArgument::Model {
        let options: Vec<(String, String)> = picker_options(pane, &i.catalog(), l).into_iter().map(|m| (m.value, m.display_name)).collect();
        return slash::palette::model_choices(command, &options, pane.model(), l);
    }
    let caps = i.capabilities();
    let modes: Vec<String> =
        caps.permission_modes.iter().filter(|m| normalize::permission_modes(pane.provider()).contains(&m.as_str())).cloned().collect();
    let label = |mode: &str| runtime::permission_label(pane.provider(), mode, l);
    slash::palette::permission_choices(command, &modes, &i.settings().permission_mode, &label, l)
}

/// `WebSlashCatalog` (MainWindow.WebShell.Composer.cs:355): `scanned` is the cache's catalogue for the pane (`SlashCatalogCache.Get`).
pub fn slash_catalog(i: &MenuInputs, scanned: &[SlashCommand]) -> SlashCatalog {
    let l = &i.locale;
    if i.pane.kind() == "shell" {
        return SlashCatalog { builtins: vec![], scanned: vec![], choices: Default::default() };
    }
    let builtins = slash::palette::builtins(i.pane.provider(), l);
    let choices = builtins
        .iter()
        .filter_map(|b| {
            b.argument.map(|a| (b.invocation.clone(), argument_choices(a, &b.invocation, i).iter().map(|c| SlashRow::of(c, l)).collect()))
        })
        .collect();
    SlashCatalog {
        builtins: builtins.iter().map(|c| SlashRow::of(c, l)).collect(),
        scanned: scanned.iter().map(|c| SlashRow::of(c, l)).collect(),
        choices: indexmap_like::OrderedMap(choices),
    }
}
