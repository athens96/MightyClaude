namespace MightyClaude.Core;

// Copy that belongs to the Codex plugin list only, mirrored from
// CodexPluginService.swift (the status sentences) and the Codex branches of
// ClaudePluginView.swift (the footer and the empty-marketplace sentence).
// Values are read from the shared locale files via Locale.Get.
//
// Everything both providers say - the window title template, the tabs, the
// search placeholder, the filter, the buttons, the row labels, the empty copy
// and the shared status sentences - stays in PluginStrings and is reused
// unchanged, so the Claude list keeps behaving exactly as before.
// CodexPluginVerification.StringsMatchMacOS checks every value here.
//
// One OS-bound substitution (recorded in docs/windows-plugins.md):
//   FooterNote reads "이 PC의 Codex 설치 목록" where macOS reads "이 Mac의".
//   Windows has no Mac; the sentence names the computer the app runs on.
//
// Install, marketplace add and marketplace upgrade belong to the marketplace
// feature and have no copy here. Reading changes nothing.
public static class CodexPluginStrings
{
    // CodexPluginService.parseSnapshot - the two ready sentences.
    public static string DetailReady => Locale.Get("plugins.codex.detailReady");
    public static string DetailNoMarketplaces => Locale.Get("plugins.codex.detailNoMarketplaces");

    // Appended to a ready detail; "{count}" is the number of rows left out.
    public static string DetailRestrictedSuffix => Locale.Get("plugins.codex.detailRestrictedSuffix");
    // CodexPluginService.readSnapshot - appended when the CLI wrote to stderr.
    public static string DetailWarningSuffix => Locale.Get("plugins.codex.detailWarningSuffix");

    // CodexPluginService.command / readSnapshot - one sentence per failure.
    public static string DetailMissingCli => Locale.Get("plugins.codex.detailMissingCli");
    public static string DetailUnknownVersion => Locale.Get("plugins.codex.detailUnknownVersion");
    public static string DetailUnsupported => Locale.Get("plugins.codex.detailUnsupported");
    public static string DetailListingFailed => Locale.Get("plugins.codex.detailListingFailed");

    // ClaudePluginView.swift - the Codex branches of the footer and of the
    // empty marketplace tab. Claude keeps its own footer and its help link.
    // OS-bound substitution: "이 PC의" replaces "이 Mac의".
    public static string FooterNote => Locale.Get("plugins.codex.footerNote");
    public static string MarketplaceHelp => Locale.Get("plugins.codex.marketplaceHelp");

    // ---- Install and marketplace upgrade (CodexPluginService.swift) ----
    // Only the sentences Codex words differently live here. The shared ones
    // (OperationBusy, OperationCancelled, InstallBadIdOrScope, InstallUnconfirmed,
    // MarketplaceBadName, MarketplaceNotRegistered, MarketplaceRefreshSucceeded)
    // are read from PluginStrings and never repeated.
    public static string InstallSkipped => Locale.Get("plugins.codex.installSkipped");
    public static string InstallNotFound => Locale.Get("plugins.codex.installNotFound");
    public static string InstallSucceeded => Locale.Get("plugins.codex.installSucceeded");
    public static string InstallVerifyFailed => Locale.Get("plugins.codex.installVerifyFailed");
    public static string OperationFailed => Locale.Get("plugins.codex.operationFailed");
    public static string MarketplaceNotGit => Locale.Get("plugins.codex.marketplaceNotGit");
    public static string MarketplaceRefreshUnconfirmed => Locale.Get("plugins.codex.marketplaceRefreshUnconfirmed");
    public static string NoGitMarketplaces => Locale.Get("plugins.codex.noGitMarketplaces");
    public static string RefreshGitOnly => Locale.Get("plugins.codex.refreshGitOnly");
}
