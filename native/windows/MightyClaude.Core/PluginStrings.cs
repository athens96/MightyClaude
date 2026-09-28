namespace MightyClaude.Core;

// Copy of the Claude plugin list, mirrored from
// ClaudePluginView.swift (title, tabs, filter, rows, buttons and empty
// copy) and ClaudePluginService.swift (the status sentences). Values are read
// from the shared locale files via Locale.Get; the Korean values are the
// macOS literals. WinUI reads these properties and never types Korean of its own.
// ClaudePluginVerification.claudePluginStringsMatchMacOS checks every value.
//
// Install buttons, the scope picker and the marketplace refresh belong to the
// marketplace feature and have no copy here. Reading changes nothing.
public static class PluginStrings
{
    // ClaudePluginView.swift — window header and tabs.
    public static string TitleTemplate => Locale.Get("plugins.titleTemplate");
    public static string TabInstalled => Locale.Get("settings.toolkit.verdictInstalled");
    public static string TabMarketplace => Locale.Get("plugins.tab.marketplace");
    // tab(_:value:count:) draws "\(title) \(count)".
    public static string TabCountTemplate => "{title} {count}";

    // Search field and marketplace filter. "전체" is the filter's empty tag.
    public static string SearchPlaceholder => Locale.Get("plugins.searchPlaceholder");
    public static string FilterAll => Locale.Get("plugins.filterAll");

    // Buttons.
    public static string ButtonReload => Locale.Get("plugins.button.reload");
    public static string ButtonClose => Locale.Get("settings.closeButton");
    public static string DiagnosticsDisclosure => Locale.Get("plugins.diagnosticsDisclosure");
    public static string MarketplaceHelpLink => Locale.Get("plugins.marketplaceHelpLink");

    // Installed row: subtitle is "{marketplace} · {scope}" with this fallback,
    // and the enabled badge.
    public static string DirectInstall => Locale.Get("plugins.directInstall");
    public static string SubtitleTemplate => "{left} · {right}";
    public static string ScopeLocal => Locale.Get("plugins.scope.local");
    public static string ScopeProject => Locale.Get("plugins.scope.project");
    public static string ScopeUser => Locale.Get("plugins.scope.user");
    public static string ScopeManaged => Locale.Get("plugins.scope.managed");
    public static string StateEnabled => Locale.Get("plugins.state.enabled");
    public static string StateDisabled => Locale.Get("plugins.state.disabled");
    public static string StateUnknown => Locale.Get("plugins.state.unknown");

    // Catalog row: subtitle is "{marketplace} · {sourceKind}".
    public static string NoDescription => Locale.Get("plugins.noDescription");

    // Empty list and progress copy.
    public static string EmptyLoading => Locale.Get("plugins.empty.loading");
    public static string EmptyFailed => Locale.Get("plugins.empty.failed");
    public static string EmptyFiltered => Locale.Get("plugins.empty.filtered");
    public static string EmptyInstalled => Locale.Get("plugins.empty.installed");
    public static string EmptyAvailable => Locale.Get("plugins.empty.available");
    public static string ProgressLoading => Locale.Get("plugins.progress.loading");
    public static string FooterNote => Locale.Get("plugins.claude.footerNote");

    // ClaudePluginService.swift — one sentence per status.
    public static string DetailReady => Locale.Get("plugins.claude.detailReady");
    public static string DetailNoMarketplaces => Locale.Get("plugins.claude.detailNoMarketplaces");
    public static string DetailCancelled => Locale.Get("plugins.detail.cancelled");
    public static string DetailFailed => Locale.Get("plugins.detail.failed");
    public static string DetailInvalidWorkspace => Locale.Get("plugins.detail.invalidWorkspace");
    public static string DetailMissingWorkspace => Locale.Get("plugins.detail.missingWorkspace");
    public static string DetailMissingCli => Locale.Get("plugins.claude.detailMissingCli");
    public static string DetailUnknownVersion => Locale.Get("plugins.claude.detailUnknownVersion");
    public static string DetailUnsupported => Locale.Get("plugins.claude.detailUnsupported");
    public static string DetailListingFailed => Locale.Get("plugins.claude.detailListingFailed");
    public static string DetailMarketplacesFailed => Locale.Get("plugins.detail.marketplacesFailed");
    public static string DetailMalformed => Locale.Get("plugins.detail.malformed");
    public static string DetailIncomplete => Locale.Get("plugins.detail.incomplete");

    // ---- Install and marketplace refresh controls (ClaudePluginView.swift) ----
    // The scope picker, the per-plugin install button, 마켓플레이스 새로고침, the
    // progress line and the cancel button, word for word as macOS writes them.
    public static string ScopePickerLabel => Locale.Get("plugins.scopePicker.label");
    public static string ScopeLocalOption => Locale.Get("plugins.scopePicker.local");
    public static string ScopeProjectOption => Locale.Get("plugins.scopePicker.project");
    public static string ScopeUserOption => Locale.Get("plugins.scopePicker.user");
    public static string ScopeNoteLocal => Locale.Get("plugins.scopeNote.local");
    public static string ScopeNoteProject => Locale.Get("plugins.scopeNote.project");
    // OS-bound substitution, recorded in docs/windows-plugins.md:
    // macOS reads "이 Mac의 모든 프로젝트에서 ...".
    public static string ScopeNoteUser => Locale.Get("plugins.scopeNote.user");
    public static string ButtonInstall => Locale.Get("settings.toolkit.installButton");
    public static string ButtonInstalling => Locale.Get("settings.toolkit.installingButton");
    public static string ButtonMarketplaceRefresh => Locale.Get("plugins.button.marketplaceRefresh");
    public static string ButtonCancelOperation => Locale.Get("plugins.button.cancelOperation");
    public static string ButtonCancelling => Locale.Get("plugins.button.cancelling");
    public static string ProgressInstalling => Locale.Get("plugins.progress.installing");
    public static string ProgressRefreshing => Locale.Get("plugins.progress.refreshing");
    public static string ProgressCancelling => Locale.Get("plugins.progress.cancelling");
    public static string NoRefreshableMarketplaces => Locale.Get("plugins.noRefreshableMarketplaces");
    public static string SelectPluginAndScopeAgain => Locale.Get("plugins.selectPluginAndScopeAgain");
    public static string LoadListFirst => Locale.Get("plugins.loadListFirst");
    public static string MarketplacesRefreshedTemplate => Locale.Get("plugins.marketplacesRefreshed");

    // ---- Operation results (ClaudePluginService.swift install / refreshMarketplace) ----
    public static string OperationBusy => Locale.Get("plugins.operation.busy");
    public static string OperationCancelled => Locale.Get("plugins.operation.cancelled");
    public static string OperationCancelledByUser => Locale.Get("plugins.operation.cancelledByUser");
    public static string InstallBadIdOrScope => Locale.Get("plugins.install.badIdOrScope");
    public static string InstallNotFound => Locale.Get("plugins.claude.installNotFound");
    public static string InstallSkipped => Locale.Get("plugins.claude.installSkipped");
    public static string InstallCommandRequired => Locale.Get("plugins.claude.installCommandRequired");
    public static string InstallUnconfirmed => Locale.Get("plugins.install.unconfirmed");
    public static string InstallFailed => Locale.Get("plugins.install.failed");
    public static string InstallSucceeded => Locale.Get("plugins.claude.installSucceeded");
    public static string MarketplaceBadName => Locale.Get("plugins.marketplace.badName");
    public static string MarketplaceNotRegistered => Locale.Get("plugins.marketplace.notRegistered");
    public static string MarketplaceRefreshFailed => Locale.Get("plugins.marketplace.refreshFailed");
    public static string MarketplaceRefreshSucceeded => Locale.Get("plugins.marketplace.refreshSucceeded");
}
