//! The plugin window's copy (C# `PluginStrings`, `CodexPluginStrings`), read from the shared locale files: the Korean values are the
//! macOS literals of ClaudePluginView.swift, ClaudePluginService.swift and CodexPluginService.swift. The screen never types copy of
//! its own. Each module's `all()` lists every field by its C# name (what the strings checks read instead of reflection).
//!
//! OS-bound substitutions (docs/windows-plugins.md): `plugin::scope_note_user` and `codex::footer_note` read "이 PC의" where macOS
//! reads "이 Mac의".

macro_rules! copy {
    ($($field:literal $name:ident => $value:expr),* $(,)?) => {
        $(
            #[doc = concat!("C# `", $field, "`.")]
            pub fn $name() -> String {
                $value
            }
        )*

        /// Every field's value by its C# name, in the class's order.
        pub fn all() -> Vec<(&'static str, String)> {
            vec![$(($field, $name())),*]
        }
    };
}

/// C# `PluginStrings`: everything both providers say.
pub mod plugin {
    use mighty_run::strings::get;

    copy! {
        "TitleTemplate" title_template => get("plugins.titleTemplate"),
        "TabInstalled" tab_installed => get("settings.toolkit.verdictInstalled"),
        "TabMarketplace" tab_marketplace => get("plugins.tab.marketplace"),
        // tab(_:value:count:) draws "\(title) \(count)".
        "TabCountTemplate" tab_count_template => "{title} {count}".to_string(),
        "SearchPlaceholder" search_placeholder => get("plugins.searchPlaceholder"),
        "FilterAll" filter_all => get("plugins.filterAll"),
        "ButtonReload" button_reload => get("plugins.button.reload"),
        "ButtonClose" button_close => get("settings.closeButton"),
        "DiagnosticsDisclosure" diagnostics_disclosure => get("plugins.diagnosticsDisclosure"),
        "MarketplaceHelpLink" marketplace_help_link => get("plugins.marketplaceHelpLink"),
        "DirectInstall" direct_install => get("plugins.directInstall"),
        "SubtitleTemplate" subtitle_template => "{left} · {right}".to_string(),
        "ScopeLocal" scope_local => get("plugins.scope.local"),
        "ScopeProject" scope_project => get("plugins.scope.project"),
        "ScopeUser" scope_user => get("plugins.scope.user"),
        "ScopeManaged" scope_managed => get("plugins.scope.managed"),
        "StateEnabled" state_enabled => get("plugins.state.enabled"),
        "StateDisabled" state_disabled => get("plugins.state.disabled"),
        "StateUnknown" state_unknown => get("plugins.state.unknown"),
        "NoDescription" no_description => get("plugins.noDescription"),
        "EmptyLoading" empty_loading => get("plugins.empty.loading"),
        "EmptyFailed" empty_failed => get("plugins.empty.failed"),
        "EmptyFiltered" empty_filtered => get("plugins.empty.filtered"),
        "EmptyInstalled" empty_installed => get("plugins.empty.installed"),
        "EmptyAvailable" empty_available => get("plugins.empty.available"),
        "ProgressLoading" progress_loading => get("plugins.progress.loading"),
        "FooterNote" footer_note => get("plugins.claude.footerNote"),
        "DetailReady" detail_ready => get("plugins.claude.detailReady"),
        "DetailNoMarketplaces" detail_no_marketplaces => get("plugins.claude.detailNoMarketplaces"),
        "DetailCancelled" detail_cancelled => get("plugins.detail.cancelled"),
        "DetailFailed" detail_failed => get("plugins.detail.failed"),
        "DetailInvalidWorkspace" detail_invalid_workspace => get("plugins.detail.invalidWorkspace"),
        "DetailMissingWorkspace" detail_missing_workspace => get("plugins.detail.missingWorkspace"),
        "DetailMissingCli" detail_missing_cli => get("plugins.claude.detailMissingCli"),
        "DetailUnknownVersion" detail_unknown_version => get("plugins.claude.detailUnknownVersion"),
        "DetailUnsupported" detail_unsupported => get("plugins.claude.detailUnsupported"),
        "DetailListingFailed" detail_listing_failed => get("plugins.claude.detailListingFailed"),
        "DetailMarketplacesFailed" detail_marketplaces_failed => get("plugins.detail.marketplacesFailed"),
        "DetailMalformed" detail_malformed => get("plugins.detail.malformed"),
        "DetailIncomplete" detail_incomplete => get("plugins.detail.incomplete"),
        "ScopePickerLabel" scope_picker_label => get("plugins.scopePicker.label"),
        "ScopeLocalOption" scope_local_option => get("plugins.scopePicker.local"),
        "ScopeProjectOption" scope_project_option => get("plugins.scopePicker.project"),
        "ScopeUserOption" scope_user_option => get("plugins.scopePicker.user"),
        "ScopeNoteLocal" scope_note_local => get("plugins.scopeNote.local"),
        "ScopeNoteProject" scope_note_project => get("plugins.scopeNote.project"),
        "ScopeNoteUser" scope_note_user => get("plugins.scopeNote.user"),
        "ButtonInstall" button_install => get("settings.toolkit.installButton"),
        "ButtonInstalling" button_installing => get("settings.toolkit.installingButton"),
        "ButtonMarketplaceRefresh" button_marketplace_refresh => get("plugins.button.marketplaceRefresh"),
        "ButtonCancelOperation" button_cancel_operation => get("plugins.button.cancelOperation"),
        "ButtonCancelling" button_cancelling => get("plugins.button.cancelling"),
        "ProgressInstalling" progress_installing => get("plugins.progress.installing"),
        "ProgressRefreshing" progress_refreshing => get("plugins.progress.refreshing"),
        "ProgressCancelling" progress_cancelling => get("plugins.progress.cancelling"),
        "NoRefreshableMarketplaces" no_refreshable_marketplaces => get("plugins.noRefreshableMarketplaces"),
        "SelectPluginAndScopeAgain" select_plugin_and_scope_again => get("plugins.selectPluginAndScopeAgain"),
        "LoadListFirst" load_list_first => get("plugins.loadListFirst"),
        "MarketplacesRefreshedTemplate" marketplaces_refreshed_template => get("plugins.marketplacesRefreshed"),
        "OperationBusy" operation_busy => get("plugins.operation.busy"),
        "OperationCancelled" operation_cancelled => get("plugins.operation.cancelled"),
        "OperationCancelledByUser" operation_cancelled_by_user => get("plugins.operation.cancelledByUser"),
        "InstallBadIdOrScope" install_bad_id_or_scope => get("plugins.install.badIdOrScope"),
        "InstallNotFound" install_not_found => get("plugins.claude.installNotFound"),
        "InstallSkipped" install_skipped => get("plugins.claude.installSkipped"),
        "InstallCommandRequired" install_command_required => get("plugins.claude.installCommandRequired"),
        "InstallUnconfirmed" install_unconfirmed => get("plugins.install.unconfirmed"),
        "InstallFailed" install_failed => get("plugins.install.failed"),
        "InstallSucceeded" install_succeeded => get("plugins.claude.installSucceeded"),
        "MarketplaceBadName" marketplace_bad_name => get("plugins.marketplace.badName"),
        "MarketplaceNotRegistered" marketplace_not_registered => get("plugins.marketplace.notRegistered"),
        "MarketplaceRefreshFailed" marketplace_refresh_failed => get("plugins.marketplace.refreshFailed"),
        "MarketplaceRefreshSucceeded" marketplace_refresh_succeeded => get("plugins.marketplace.refreshSucceeded"),
    }
}

/// C# `CodexPluginStrings`: only the sentences Codex words differently; the shared ones stay in [`plugin`].
pub mod codex {
    use mighty_run::strings::get;

    copy! {
        "DetailReady" detail_ready => get("plugins.codex.detailReady"),
        "DetailNoMarketplaces" detail_no_marketplaces => get("plugins.codex.detailNoMarketplaces"),
        "DetailRestrictedSuffix" detail_restricted_suffix => get("plugins.codex.detailRestrictedSuffix"),
        "DetailWarningSuffix" detail_warning_suffix => get("plugins.codex.detailWarningSuffix"),
        "DetailMissingCli" detail_missing_cli => get("plugins.codex.detailMissingCli"),
        "DetailUnknownVersion" detail_unknown_version => get("plugins.codex.detailUnknownVersion"),
        "DetailUnsupported" detail_unsupported => get("plugins.codex.detailUnsupported"),
        "DetailListingFailed" detail_listing_failed => get("plugins.codex.detailListingFailed"),
        "FooterNote" footer_note => get("plugins.codex.footerNote"),
        "MarketplaceHelp" marketplace_help => get("plugins.codex.marketplaceHelp"),
        "InstallSkipped" install_skipped => get("plugins.codex.installSkipped"),
        "InstallNotFound" install_not_found => get("plugins.codex.installNotFound"),
        "InstallSucceeded" install_succeeded => get("plugins.codex.installSucceeded"),
        "InstallVerifyFailed" install_verify_failed => get("plugins.codex.installVerifyFailed"),
        "OperationFailed" operation_failed => get("plugins.codex.operationFailed"),
        "MarketplaceNotGit" marketplace_not_git => get("plugins.codex.marketplaceNotGit"),
        "MarketplaceRefreshUnconfirmed" marketplace_refresh_unconfirmed => get("plugins.codex.marketplaceRefreshUnconfirmed"),
        "NoGitMarketplaces" no_git_marketplaces => get("plugins.codex.noGitMarketplaces"),
        "RefreshGitOnly" refresh_git_only => get("plugins.codex.refreshGitOnly"),
    }
}

/// The automatic update's sentences (C# `PluginAutoUpdateResult`, `Locale.Get("pluginAutoUpdate.*")`).
pub mod auto_update {
    use mighty_run::strings::{get, get_with};

    pub fn busy() -> String {
        get("pluginAutoUpdate.busy")
    }
    pub fn cancelled() -> String {
        get("pluginAutoUpdate.cancelled")
    }
    pub fn failed() -> String {
        get("pluginAutoUpdate.failed")
    }
    pub fn none() -> String {
        get("pluginAutoUpdate.none")
    }
    pub fn no_git_marketplaces() -> String {
        get("pluginAutoUpdate.noGitMarketplaces")
    }
    pub fn marketplaces_upgraded() -> String {
        get("pluginAutoUpdate.marketplacesUpgraded")
    }
    /// `PluginAutoUpdateResult.Count`: a `{count}` template.
    pub fn count(key: &str, count: usize) -> String {
        get_with(key, &[("count", &count.to_string())])
    }
    /// `PluginAutoUpdateResult.Names`: a `{plugins}` template, the names joined with ", ".
    pub fn names(key: &str, names: &[String]) -> String {
        get_with(key, &[("plugins", &names.join(", "))])
    }
}
