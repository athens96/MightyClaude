namespace MightyClaude.Core;

// Read on demand so the stored language preference applies after initialization.
// Korean values retain the macOS copy and documented Windows-specific wording.
public static class AccountUsageStrings
{
    public static string Title => Locale.Get("windows.accountUsage.title");
    public static string ChipsTooltip => Locale.Get("windows.accountUsage.chipsTooltip");
    public static string RefreshTooltip => Locale.Get("windows.accountUsage.refreshTooltip");
    public static string RefreshAccessibilityLabel => Locale.Get("windows.accountUsage.refreshAccessibilityLabel");
    public static string ChipBeforeFirstRun => Locale.Get("windows.accountUsage.chipBeforeFirstRun");
    public static string ChipChecking => Locale.Get("windows.accountUsage.chipChecking");
    public static string ChipEmpty => Locale.Get("windows.accountUsage.chipEmpty");
    public static string ToggleLabel => Locale.Get("windows.accountUsage.toggleLabel");
    public static string ToggleDescription => Locale.Get("windows.accountUsage.toggleDescription");
    public static string SharedLimitsNote => Locale.Get("windows.accountUsage.sharedLimitsNote");
    public static string UsedPercentTemplate => Locale.Get("windows.accountUsage.usedPercentTemplate");
    public static string ResetTemplate => Locale.Get("windows.accountUsage.resetTemplate");
    public static string CheckedAtTemplate => Locale.Get("windows.accountUsage.checkedAtTemplate");
    public static string LastKnownPrefix => Locale.Get("windows.accountUsage.lastKnownPrefix");
    public static string WindowFiveHourSuffix => Locale.Get("windows.accountUsage.windowFiveHourSuffix");
    public static string WindowSevenDaySuffix => Locale.Get("windows.accountUsage.windowSevenDaySuffix");
    public static string ClaudeBeforeFirstRunNote => Locale.Get("windows.accountUsage.claudeBeforeFirstRunNote");
    public static string CardChecking => Locale.Get("windows.accountUsage.cardChecking");
    public static string CardNotCheckedYet => Locale.Get("windows.accountUsage.cardNotCheckedYet");
    public static string WindowSession => Locale.Get("windows.accountUsage.windowSession");
    public static string WindowWeekly => Locale.Get("windows.accountUsage.windowWeekly");
    public static string WindowDaily => Locale.Get("windows.accountUsage.windowDaily");
    public static string WindowMonthly => Locale.Get("windows.accountUsage.windowMonthly");
    public static string WindowSpendLimit => Locale.Get("windows.accountUsage.windowSpendLimit");
    public static string DetailNotCheckedYet => Locale.Get("windows.accountUsage.detailNotCheckedYet");
    public static string DetailShutdown => Locale.Get("windows.accountUsage.detailShutdown");
    public static string DetailCancelled => Locale.Get("windows.accountUsage.detailCancelled");
    public static string DetailRefreshFailed => Locale.Get("windows.accountUsage.detailRefreshFailed");
    public static string DetailAuthentication => Locale.Get("windows.accountUsage.detailAuthentication");
    public static string DetailRateLimited => Locale.Get("windows.accountUsage.detailRateLimited");
    public static string DetailLastKnownSuffix => Locale.Get("windows.accountUsage.detailLastKnownSuffix");
    public static string DetailGeminiUnavailable => Locale.Get("windows.accountUsage.detailGeminiUnavailable");
    public static string DetailUnsupportedProvider => Locale.Get("windows.accountUsage.detailUnsupportedProvider");
    public static string DetailCodexNotInstalled => Locale.Get("windows.accountUsage.detailCodexNotInstalled");
    public static string DetailCodexNeedsChatGPT => Locale.Get("windows.accountUsage.detailCodexNeedsChatGPT");
    public static string DetailCustomAuthentication => Locale.Get("windows.accountUsage.detailCustomAuthentication");
    public static string DetailCodexNoWindows => Locale.Get("windows.accountUsage.detailCodexNoWindows");
    public static string DetailCodex => Locale.Get("windows.accountUsage.detailCodex");
    public static string DetailClaudeNoWindows => Locale.Get("windows.accountUsage.detailClaudeNoWindows");
    public static string DetailClaude => Locale.Get("windows.accountUsage.detailClaude");
    public static string DetailSessionReportedStale => Locale.Get("windows.accountUsage.detailSessionReportedStale");
    public static string DetailSessionReported => Locale.Get("windows.accountUsage.detailSessionReported");
    public static string ResetTitle => Locale.Get("usage.reset.title");
    public static string ResetLink => Locale.Get("usage.reset.link");
    public static string ResetProgramCedarEmber => Locale.Get("usage.reset.program.cedarEmber");
    public static string ResetProgramJuniperTide => Locale.Get("usage.reset.program.juniperTide");
    public static string ResetAvailableCedarEmber => Locale.Get("usage.reset.available.cedarEmber");
    public static string ResetAvailableJuniperTide => Locale.Get("usage.reset.available.juniperTide");
    public static string ResetHeld => Locale.Get("usage.reset.held");
    public static string ResetCooldownCedarEmber => Locale.Get("usage.reset.cooldown.cedarEmber");
    public static string ResetCooldownJuniperTide => Locale.Get("usage.reset.cooldown.juniperTide");
    public static string ResetExhausted => Locale.Get("usage.reset.exhausted");
    public static string ResetNone => Locale.Get("usage.reset.none");
    public static string ResetIneligible => Locale.Get("usage.reset.ineligible");
    public static string ResetUnknown => Locale.Get("usage.reset.unknown");
}
