//! Everything the plugin window shows, decided here and proven on the Mac (C# ClaudePluginPresentation.cs: `ClaudePluginBrowser`,
//! `ClaudePluginRow`, `PluginScopeOption`, the smoke outcomes). The window draws these rows and forwards the tab, the search, the
//! marketplace filter, the scope, reload, install, 마켓플레이스 새로고침 and cancel. The only two changes it can ask for are the two
//! the macOS app has: install one plugin and refresh one marketplace.
//!
//! The browser is shared between the window's builder and the operation it runs (C#'s UI thread): its state sits behind a lock that
//! is never held across an await, so the window reads progress while an install runs.

use std::sync::{Arc, Mutex, MutexGuard};

use mighty_core::model::Workspace;

use super::model::{ClaudePluginOperationResult, ClaudePluginSnapshot, PluginReader, status};
use super::strings::{codex as codex_strings, plugin as strings};
use super::support;
use crate::cli_update::CliUpdateService;
use crate::runner::CancellationToken;

/// One entry of the scope picker (ClaudePluginView's 설치 범위 picker).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PluginScopeOption {
    pub value: String,
    pub label: String,
}

/// One drawn row, installed or available; the subtitle is already composed, so the screen never joins copy of its own.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct ClaudePluginRow {
    pub id: String,
    pub name: String,
    pub description: String,
    pub subtitle: String,
    pub version: Option<String>,
    /// 활성 / 비활성 / 상태 미확인 on an installed row, None on a catalog row.
    pub state: Option<String>,
    pub project_path: Option<String>,
    pub errors: Vec<String>,
    pub notes: Vec<String>,
}

struct State {
    tab: String,
    search: String,
    marketplace_filter: String,
    snapshot: Option<Arc<ClaudePluginSnapshot>>,
    loading: bool,
    scope: String,
    phase: &'static str,
    active_plugin_id: Option<String>,
    is_cancelling: bool,
    last_result: Option<ClaudePluginOperationResult>,
    operation_cancel: Option<CancellationToken>,
}

pub struct ClaudePluginBrowser {
    provider: String,
    workspace: Workspace,
    state: Mutex<State>,
}

impl ClaudePluginBrowser {
    pub const INSTALLED_TAB: &'static str = "installed";
    pub const MARKETPLACE_TAB: &'static str = "marketplace";
    pub const CODEX_PROVIDER: &'static str = "codex";
    pub const PHASE_IDLE: &'static str = "idle";
    pub const PHASE_LOADING: &'static str = "loading";
    pub const PHASE_INSTALLING: &'static str = "installing";
    pub const PHASE_REFRESHING: &'static str = "refreshing";

    pub fn new(provider: &str, workspace: Workspace) -> ClaudePluginBrowser {
        let scope = if provider == Self::CODEX_PROVIDER { "user" } else { "local" };
        ClaudePluginBrowser {
            provider: provider.into(),
            workspace,
            state: Mutex::new(State {
                tab: Self::INSTALLED_TAB.into(),
                search: String::new(),
                marketplace_filter: String::new(),
                snapshot: None,
                loading: false,
                scope: scope.into(),
                phase: Self::PHASE_IDLE,
                active_plugin_id: None,
                is_cancelling: false,
                last_result: None,
                operation_cancel: None,
            }),
        }
    }

    fn lock(&self) -> MutexGuard<'_, State> {
        self.state.lock().unwrap_or_else(|poisoned| poisoned.into_inner())
    }

    pub fn provider(&self) -> &str {
        &self.provider
    }

    pub fn workspace(&self) -> &Workspace {
        &self.workspace
    }

    fn is_codex(&self) -> bool {
        self.provider == Self::CODEX_PROVIDER
    }

    // ---- the window's own state -------------------------------------------------------------------------------------------

    pub fn tab(&self) -> String {
        self.lock().tab.clone()
    }
    pub fn set_tab(&self, tab: &str) {
        self.lock().tab = tab.into();
    }
    pub fn search(&self) -> String {
        self.lock().search.clone()
    }
    pub fn set_search(&self, search: &str) {
        self.lock().search = search.into();
    }
    pub fn marketplace_filter(&self) -> String {
        self.lock().marketplace_filter.clone()
    }
    pub fn set_marketplace_filter(&self, filter: &str) {
        self.lock().marketplace_filter = filter.into();
    }
    pub fn snapshot(&self) -> Option<Arc<ClaudePluginSnapshot>> {
        self.lock().snapshot.clone()
    }
    pub fn loading(&self) -> bool {
        self.lock().loading
    }
    /// The scope the install uses: Claude offers the three macOS scopes and starts on 로컬; Codex installs at user level only.
    pub fn scope(&self) -> String {
        self.lock().scope.clone()
    }
    pub fn set_scope(&self, scope: &str) {
        self.lock().scope = scope.into();
    }
    pub fn phase(&self) -> &'static str {
        self.lock().phase
    }
    pub fn active_plugin_id(&self) -> Option<String> {
        self.lock().active_plugin_id.clone()
    }
    pub fn is_cancelling(&self) -> bool {
        self.lock().is_cancelling
    }
    /// The result line under the list. Never saved anywhere.
    pub fn last_result(&self) -> Option<ClaudePluginOperationResult> {
        self.lock().last_result.clone()
    }

    /// ClaudePluginView.providerLabel: the same header for both providers.
    pub fn title(&self) -> String {
        strings::title_template().replace("{provider}", CliUpdateService::provider_label(&self.provider))
    }

    pub fn is_ready(&self) -> bool {
        self.lock().snapshot.as_ref().is_some_and(|s| s.status == status::READY)
    }

    /// Which installed scopes this provider's window lists: every scope Claude's CLI reports; Codex plugins are user level only.
    pub fn supported_scopes(&self) -> &'static [&'static str] {
        if self.is_codex() { &["user"] } else { &["local", "project", "user", "managed", "session"] }
    }

    /// The sentence under the list (ClaudePluginView's footer).
    pub fn footer_note(&self) -> String {
        if self.is_codex() { codex_strings::footer_note() } else { strings::footer_note() }
    }

    // ---- the install and the marketplace refresh (AppStore+Plugins.swift) ------------------------------------------------

    /// Claude's three scopes; Codex's only one.
    pub fn install_scopes(&self) -> &'static [&'static str] {
        if self.is_codex() { &["user"] } else { &["local", "project", "user"] }
    }

    pub fn scope_options(&self) -> Vec<PluginScopeOption> {
        self.install_scopes()
            .iter()
            .map(|scope| PluginScopeOption {
                value: scope.to_string(),
                label: match *scope {
                    "local" => strings::scope_local_option(),
                    "project" => strings::scope_project_option(),
                    _ => strings::scope_user_option(),
                },
            })
            .collect()
    }

    /// The sentence under the picker explaining the selected scope.
    pub fn scope_note(&self) -> String {
        match self.scope().as_str() {
            "project" => strings::scope_note_project(),
            "user" => strings::scope_note_user(),
            _ => strings::scope_note_local(),
        }
    }

    pub fn is_busy(&self) -> bool {
        let state = self.lock();
        state.phase != Self::PHASE_IDLE || state.is_cancelling
    }

    pub fn is_mutating(&self) -> bool {
        let state = self.lock();
        matches!(state.phase, Self::PHASE_INSTALLING | Self::PHASE_REFRESHING) || state.is_cancelling
    }

    /// 닫기 is disabled while a change runs, as on macOS.
    pub fn can_close(&self) -> bool {
        !self.is_mutating()
    }

    pub fn can_cancel(&self) -> bool {
        self.is_mutating() && !self.is_cancelling()
    }

    pub fn progress_label(&self) -> Option<String> {
        if !self.is_busy() {
            return None;
        }
        if self.is_cancelling() {
            return Some(strings::progress_cancelling());
        }
        Some(match self.phase() {
            Self::PHASE_INSTALLING => strings::progress_installing(),
            Self::PHASE_REFRESHING => strings::progress_refreshing(),
            _ => strings::progress_loading(),
        })
    }

    pub fn cancel_label(&self) -> String {
        if self.is_cancelling() { strings::button_cancelling() } else { strings::button_cancel_operation() }
    }

    /// The registered sources the filter selects; Codex can upgrade Git-backed ones only.
    pub fn refreshable_marketplaces(&self) -> Vec<String> {
        let state = self.lock();
        let Some(snapshot) = &state.snapshot else { return Vec::new() };
        snapshot
            .marketplaces
            .iter()
            .filter(|m| {
                (!self.is_codex() || m.source_kind == "git") && (state.marketplace_filter.is_empty() || m.name == state.marketplace_filter)
            })
            .map(|m| m.name.clone())
            .collect()
    }

    /// The Codex sentence saying only registered Git sources can be refreshed.
    pub fn refresh_unavailable_note(&self) -> Option<String> {
        (self.is_codex() && self.refreshable_marketplaces().is_empty()).then(codex_strings::refresh_git_only)
    }

    pub fn installed_in_selected_scope(&self, plugin_id: &str) -> bool {
        let state = self.lock();
        let Some(snapshot) = &state.snapshot else { return false };
        let scope = state.scope.as_str();
        snapshot.installed.iter().any(|p| {
            p.plugin_id == plugin_id
                && p.scope == scope
                && (scope == "user" || p.project_path.as_deref().is_none_or(|path| support::applies(path, self.workspace.path())))
        })
    }

    pub fn install_button_label(&self, plugin_id: &str) -> String {
        if self.active_plugin_id().as_deref() == Some(plugin_id) {
            strings::button_installing()
        } else if self.installed_in_selected_scope(plugin_id) {
            strings::tab_installed()
        } else {
            strings::button_install()
        }
    }

    pub fn can_install(&self, plugin_id: &str) -> bool {
        !self.installed_in_selected_scope(plugin_id) && !self.is_busy() && self.is_ready()
    }

    pub fn can_refresh_marketplaces(&self) -> bool {
        !self.is_busy() && self.is_ready() && !self.refreshable_marketplaces().is_empty()
    }

    /// A result line no operation of the window gave (C# `ShowResult(block, false)`: why a change could not start).
    pub fn show_result(&self, result: ClaudePluginOperationResult) {
        self.lock().last_result = Some(result);
    }

    /// The result line.
    pub fn result_text(&self) -> Option<String> {
        self.lock().last_result.as_ref().map(|r| r.detail.clone())
    }

    /// The bounded CLI output behind 명령 실행 상세: Claude shows the operation's, else a failed read's; Codex both.
    pub fn diagnostic_text(&self) -> String {
        let state = self.lock();
        let snapshot =
            state.snapshot.as_ref().filter(|s| s.status != status::READY).map(|s| s.diagnostic_output.clone()).unwrap_or_default();
        let operation = state.last_result.as_ref().map(|r| r.output.clone()).unwrap_or_default();
        if !self.is_codex() {
            return if operation.is_empty() { snapshot } else { operation };
        }
        [operation, snapshot].into_iter().filter(|t| !t.is_empty()).collect::<Vec<_>>().join("\n\n")
    }

    /// The window's cancel button: the running operation stops and the list is read again, because a change may already have
    /// reached the CLI.
    pub fn request_cancel(&self) {
        let cancel = {
            let mut state = self.lock();
            let mutating = matches!(state.phase, Self::PHASE_INSTALLING | Self::PHASE_REFRESHING) || state.is_cancelling;
            if !mutating || state.is_cancelling {
                return;
            }
            state.is_cancelling = true;
            state.operation_cancel.clone()
        };
        if let Some(cancel) = cancel {
            cancel.cancel();
        }
    }

    /// Install one plugin the catalog just offered, in the selected scope: the argument assembly, the run and the reading of the
    /// result tied together.
    pub async fn install(
        &self,
        gateway: &Arc<dyn PluginReader>,
        plugin_id: &str,
        cancel: &CancellationToken,
    ) -> ClaudePluginOperationResult {
        if let Some(refusal) = self.refuse() {
            return self.remember(refusal);
        }
        let offered = self.lock().snapshot.as_ref().is_some_and(|s| s.available.iter().any(|p| p.id == plugin_id));
        let scope = self.scope();
        if !self.install_scopes().contains(&scope.as_str()) || !offered {
            return self.remember(ClaudePluginOperationResult::new(status::FAILED, strings::select_plugin_and_scope_again()));
        }
        if self.installed_in_selected_scope(plugin_id) {
            let detail = if self.is_codex() { codex_strings::install_skipped() } else { strings::install_skipped() };
            return self.remember(ClaudePluginOperationResult::new(status::SKIPPED, detail));
        }
        let token = self.begin(Self::PHASE_INSTALLING, Some(plugin_id), cancel);
        let result = gateway.install(plugin_id, &scope, &self.workspace, token).await;
        self.finish(gateway, result, cancel).await
    }

    /// Refresh the registered marketplaces the filter selects, one at a time, stopping at the first that does not succeed.
    pub async fn refresh_marketplaces(&self, gateway: &Arc<dyn PluginReader>, cancel: &CancellationToken) -> ClaudePluginOperationResult {
        if let Some(refusal) = self.refuse() {
            return self.remember(refusal);
        }
        let names = self.refreshable_marketplaces();
        if names.is_empty() {
            let detail = if self.is_codex() { codex_strings::no_git_marketplaces() } else { strings::no_refreshable_marketplaces() };
            return self.remember(ClaudePluginOperationResult::new(status::SKIPPED, detail));
        }
        let token = self.begin(Self::PHASE_REFRESHING, None, cancel);
        let mut failed = None;
        for name in &names {
            if token.is_cancelled() {
                failed = Some(ClaudePluginOperationResult::new(status::CANCELLED, strings::operation_cancelled()));
                break;
            }
            let one = gateway.refresh_marketplace(name, &self.workspace, token.clone()).await;
            if one.status != status::SUCCEEDED {
                failed = Some(one);
                break;
            }
        }
        let result = failed.unwrap_or_else(|| {
            ClaudePluginOperationResult::new(
                status::SUCCEEDED,
                strings::marketplaces_refreshed_template().replace("{count}", &names.len().to_string()),
            )
        });
        self.finish(gateway, result, cancel).await
    }

    fn refuse(&self) -> Option<ClaudePluginOperationResult> {
        if self.is_busy() {
            return Some(ClaudePluginOperationResult::new(status::BUSY, strings::operation_busy()));
        }
        if !self.is_ready() {
            return Some(ClaudePluginOperationResult::new(status::FAILED, strings::load_list_first()));
        }
        None
    }

    fn remember(&self, result: ClaudePluginOperationResult) -> ClaudePluginOperationResult {
        self.lock().last_result = Some(result.clone());
        result
    }

    fn begin(&self, phase: &'static str, active_plugin_id: Option<&str>, cancel: &CancellationToken) -> CancellationToken {
        let token = cancel.child_token();
        let mut state = self.lock();
        state.phase = phase;
        state.active_plugin_id = active_plugin_id.map(str::to_string);
        state.last_result = None;
        state.operation_cancel = Some(token.clone());
        token
    }

    /// A cancel the user asked for says so, and the list is read again because a change may already have reached the CLI.
    async fn finish(
        &self,
        gateway: &Arc<dyn PluginReader>,
        mut result: ClaudePluginOperationResult,
        cancel: &CancellationToken,
    ) -> ClaudePluginOperationResult {
        {
            let mut state = self.lock();
            state.operation_cancel = None;
            if state.is_cancelling && result.status == status::CANCELLED {
                result.detail = strings::operation_cancelled_by_user();
            }
            state.last_result = Some(result.clone());
            state.phase = Self::PHASE_IDLE;
            state.active_plugin_id = None;
            state.is_cancelling = false;
        }
        let snapshot = gateway.snapshot(&self.workspace, cancel.clone()).await;
        self.apply(snapshot);
        result
    }

    pub fn begin_load(&self) {
        let mut state = self.lock();
        state.loading = true;
        if state.phase == Self::PHASE_IDLE {
            state.phase = Self::PHASE_LOADING;
        }
    }

    /// The one place a finished read reaches the screen. A filter the new list no longer offers falls back to 전체, as on macOS.
    pub fn apply(&self, snapshot: impl Into<Arc<ClaudePluginSnapshot>>) {
        let snapshot = snapshot.into();
        let marketplaces = marketplaces_of(Some(&snapshot));
        let mut state = self.lock();
        state.snapshot = Some(snapshot);
        state.loading = false;
        if state.phase == Self::PHASE_LOADING {
            state.phase = Self::PHASE_IDLE;
        }
        if !state.marketplace_filter.is_empty() && !marketplaces.contains(&state.marketplace_filter) {
            state.marketplace_filter.clear();
        }
    }

    /// The tab's count is what the tab lists: Claude counts every scope its CLI reports, Codex never a non-user row.
    pub fn installed_count(&self) -> usize {
        let scopes = self.supported_scopes();
        self.lock().snapshot.as_ref().map_or(0, |s| s.installed.iter().filter(|p| scopes.contains(&p.scope.as_str())).count())
    }

    pub fn available_count(&self) -> usize {
        self.lock().snapshot.as_ref().map_or(0, |s| s.available.len())
    }

    pub fn tab_label(&self, tab: &str) -> String {
        let marketplace = tab == Self::MARKETPLACE_TAB;
        strings::tab_count_template()
            .replace("{title}", &if marketplace { strings::tab_marketplace() } else { strings::tab_installed() })
            .replace("{count}", &if marketplace { self.available_count() } else { self.installed_count() }.to_string())
    }

    /// Every marketplace name the list mentions, registered or referenced, once each, in ordinal order.
    pub fn marketplaces(&self) -> Vec<String> {
        marketplaces_of(self.lock().snapshot.as_ref())
    }

    fn matches(&self, name: &str, description: &str, marketplace: Option<&str>) -> bool {
        let state = self.lock();
        let query = state.search.trim();
        if !state.marketplace_filter.is_empty() && Some(state.marketplace_filter.as_str()) != marketplace {
            return false;
        }
        query.is_empty() || contains_ignoring_case(name, query) || contains_ignoring_case(description, query)
    }

    pub fn scope_label(scope: &str) -> String {
        match scope {
            "local" => strings::scope_local(),
            "project" => strings::scope_project(),
            "user" => strings::scope_user(),
            "managed" => strings::scope_managed(),
            _ => scope.to_string(),
        }
    }

    fn subtitle(left: &str, right: &str) -> String {
        strings::subtitle_template().replace("{left}", left).replace("{right}", right)
    }

    /// The rows of the selected tab, filtered and ordered as macOS orders them.
    pub fn rows(&self) -> Vec<ClaudePluginRow> {
        if self.tab() == Self::MARKETPLACE_TAB { self.available_rows() } else { self.installed_rows() }
    }

    pub fn installed_rows(&self) -> Vec<ClaudePluginRow> {
        let Some(snapshot) = self.snapshot() else { return Vec::new() };
        let scopes = self.supported_scopes();
        let mut plugins: Vec<_> = snapshot
            .installed
            .iter()
            .filter(|p| scopes.contains(&p.scope.as_str()))
            .filter(|p| self.matches(&p.name, &p.description, p.marketplace.as_deref()))
            .collect();
        plugins.sort_by(|a, b| a.name.cmp(&b.name).then_with(|| a.scope.cmp(&b.scope)).then_with(|| a.id().cmp(&b.id())));
        plugins
            .into_iter()
            .map(|p| ClaudePluginRow {
                id: p.id(),
                name: p.name.clone(),
                description: p.description.clone(),
                subtitle: Self::subtitle(
                    p.marketplace.as_deref().filter(|m| !m.is_empty()).map_or_else(strings::direct_install, str::to_string).as_str(),
                    &Self::scope_label(&p.scope),
                ),
                version: p.version.clone(),
                state: Some(match p.enabled {
                    None => strings::state_unknown(),
                    Some(true) => strings::state_enabled(),
                    Some(false) => strings::state_disabled(),
                }),
                project_path: p.project_path.clone(),
                errors: p.errors.clone(),
                notes: p.notes.clone(),
            })
            .collect()
    }

    pub fn available_rows(&self) -> Vec<ClaudePluginRow> {
        let Some(snapshot) = self.snapshot() else { return Vec::new() };
        let mut plugins: Vec<_> =
            snapshot.available.iter().filter(|p| self.matches(&p.name, &p.description, Some(&p.marketplace))).collect();
        plugins.sort_by(|a, b| a.name.cmp(&b.name).then_with(|| a.marketplace.cmp(&b.marketplace)));
        plugins
            .into_iter()
            .map(|p| ClaudePluginRow {
                id: p.id.clone(),
                name: p.name.clone(),
                description: if p.description.is_empty() { strings::no_description() } else { p.description.clone() },
                subtitle: Self::subtitle(&p.marketplace, &p.source_kind),
                version: p.version.clone(),
                ..ClaudePluginRow::default()
            })
            .collect()
    }

    /// What stands in for an empty list (ClaudePluginView.emptyMessage). macOS keeps the same copy for both providers and puts the
    /// Claude link or the Codex sentence under it.
    pub fn empty_message(&self) -> String {
        if self.loading() {
            return strings::empty_loading();
        }
        if !self.is_ready() {
            return strings::empty_failed();
        }
        if !self.search().trim().is_empty() || !self.marketplace_filter().is_empty() {
            return strings::empty_filtered();
        }
        if self.tab() == Self::MARKETPLACE_TAB { strings::empty_available() } else { strings::empty_installed() }
    }

    /// The sentence under the filters: the snapshot's detail when the read did not end ready (or always, for Codex, when it has
    /// one); nothing while a Claude list is good.
    pub fn status_text(&self) -> Option<String> {
        let snapshot = self.snapshot()?;
        (snapshot.status != status::READY || (self.is_codex() && !snapshot.detail.is_empty())).then(|| snapshot.detail.clone())
    }

    /// Shown when the marketplace tab is empty because nothing is registered.
    pub fn shows_marketplace_help(&self) -> bool {
        self.is_ready() && self.tab() == Self::MARKETPLACE_TAB && self.snapshot().is_some_and(|s| s.marketplaces.is_empty())
    }

    /// macOS puts the 마켓플레이스 추가 방법 link there for Claude…
    pub fn shows_marketplace_help_link(&self) -> bool {
        self.shows_marketplace_help() && !self.is_codex()
    }

    /// …and a sentence telling the user to register one in the CLI for Codex.
    pub fn marketplace_help_text(&self) -> Option<String> {
        (self.shows_marketplace_help() && self.is_codex()).then(codex_strings::marketplace_help)
    }
}

fn marketplaces_of(snapshot: Option<&Arc<ClaudePluginSnapshot>>) -> Vec<String> {
    let Some(snapshot) = snapshot else { return Vec::new() };
    let mut names: Vec<String> = snapshot
        .marketplaces
        .iter()
        .map(|m| m.name.clone())
        .chain(snapshot.available.iter().map(|p| p.marketplace.clone()))
        .chain(snapshot.installed.iter().filter_map(|p| p.marketplace.clone()).filter(|n| !n.is_empty()))
        .collect();
    names.sort();
    names.dedup();
    names
}

/// `Contains(query, OrdinalIgnoreCase)`.
fn contains_ignoring_case(text: &str, query: &str) -> bool {
    text.to_lowercase().contains(&query.to_lowercase())
}

/// What the smoke records under "pluginMarketplace" after driving the plugin window with a fake reader through install, cancel and
/// refresh (C# `PluginMarketplaceSmokeOutcome`). No real claude or codex process starts.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct PluginMarketplaceSmokeOutcome {
    pub claude_scope_options: usize,
    pub codex_scope_options: usize,
    pub install_result: String,
    pub cancel_result: String,
    pub refresh_result: String,
    pub restored: bool,
}

impl PluginMarketplaceSmokeOutcome {
    pub const RESULT_KEY: &'static str = "pluginMarketplace";

    pub fn to_json(&self) -> serde_json::Value {
        serde_json::json!({
            "claudeScopeOptions": self.claude_scope_options,
            "codexScopeOptions": self.codex_scope_options,
            "installResult": self.install_result,
            "cancelResult": self.cancel_result,
            "refreshResult": self.refresh_result,
            "restored": self.restored,
        })
    }
}

/// What the smoke records under "claudePluginList" after driving the plugin window with a fixture snapshot (C#
/// `ClaudePluginSmokeOutcome`). No CLI starts.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct ClaudePluginSmokeOutcome {
    pub title: String,
    pub installed_tab: String,
    pub marketplace_tab: String,
    pub installed_rows: usize,
    pub available_rows: usize,
    pub filtered_rows: usize,
    pub searched_rows: usize,
    pub installed_subtitle: String,
    pub available_subtitle: String,
    pub reloaded_from_status: String,
    pub reads: usize,
    pub mutating_controls: usize,
    pub restored: bool,
}

impl ClaudePluginSmokeOutcome {
    pub const RESULT_KEY: &'static str = "claudePluginList";

    pub fn to_json(&self) -> serde_json::Value {
        serde_json::json!({
            "title": self.title,
            "installedTab": self.installed_tab,
            "marketplaceTab": self.marketplace_tab,
            "installedRows": self.installed_rows,
            "availableRows": self.available_rows,
            "filteredRows": self.filtered_rows,
            "searchedRows": self.searched_rows,
            "installedSubtitle": self.installed_subtitle,
            "availableSubtitle": self.available_subtitle,
            "reloadedFromStatus": self.reloaded_from_status,
            "reads": self.reads,
            "mutatingControls": self.mutating_controls,
            "restored": self.restored,
        })
    }
}

/// What the smoke records under "codexPluginList" (C# `CodexPluginSmokeOutcome`). No CLI starts.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct CodexPluginSmokeOutcome {
    pub title: String,
    pub installed_tab: String,
    pub marketplace_tab: String,
    pub installed_rows: usize,
    pub available_rows: usize,
    pub filtered_rows: usize,
    pub searched_rows: usize,
    pub installed_subtitle: String,
    pub footer_note: String,
    pub ready_status: String,
    pub no_marketplace_help: String,
    pub unsupported_status: String,
    pub reads: usize,
    pub mutating_controls: usize,
    pub restored: bool,
}

impl CodexPluginSmokeOutcome {
    pub const RESULT_KEY: &'static str = "codexPluginList";

    pub fn to_json(&self) -> serde_json::Value {
        serde_json::json!({
            "title": self.title,
            "installedTab": self.installed_tab,
            "marketplaceTab": self.marketplace_tab,
            "installedRows": self.installed_rows,
            "availableRows": self.available_rows,
            "filteredRows": self.filtered_rows,
            "searchedRows": self.searched_rows,
            "installedSubtitle": self.installed_subtitle,
            "footerNote": self.footer_note,
            "readyStatus": self.ready_status,
            "noMarketplaceHelp": self.no_marketplace_help,
            "unsupportedStatus": self.unsupported_status,
            "reads": self.reads,
            "mutatingControls": self.mutating_controls,
            "restored": self.restored,
        })
    }
}
