namespace MightyClaude.Core;

// Korean copy of the account usage chips and popover, mirrored from
// StatusBarUsage.swift (chips, popover, toggle, footnote) and
// AccountUsageService.swift (every detail sentence) plus
// RateLimitWindowLabel (the window names).
// WinUI reads these constants and never types Korean of its own.
// StringsVerification.AccountUsageStringsMatchMacOS checks every value.
//
// Two OS-bound substitutions (recorded in docs/windows-account-usage.md).
// Windows has no Keychain, so no permission dialog and no permission state
// exist here:
//   ToggleLabel reads "Claude 한도를 직접 조회" where macOS reads
//   "Claude 한도를 Keychain으로 직접 조회".
//   ToggleDescription reads "끄면 앱이 Anthropic에 직접 조회하지 않습니다. …"
//   where macOS reads "끄면 Keychain 승인창이 열리지 않습니다. …".
//   SharedLimitsNote drops the macOS Keychain sentence for the Windows one.
public static class AccountUsageStrings
{
    // StatusBarUsageView / StatusBarUsageDetails — title, tooltips, refresh.
    public const string Title = "계정 사용 한도";
    public const string ChipsTooltip = "계정 사용 한도 · 클릭해 상세 보기";
    public const string RefreshTooltip = "계정 한도 다시 확인";
    public const string RefreshAccessibilityLabel = "계정 한도 새로고침";

    // Chip states before a value exists.
    public const string ChipBeforeFirstRun = "실행 후 표시";
    public const string ChipChecking = "확인 중";
    public const string ChipEmpty = "—";

    // The direct Claude lookup switch. OS-bound substitutions, see the header.
    public const string ToggleLabel = "Claude 한도를 직접 조회";
    public const string ToggleDescription = "끄면 앱이 Anthropic에 직접 조회하지 않습니다. Claude 실행 때 CLI가 보고하는 한도만 표시합니다.";
    public const string SharedLimitsNote = "계정 한도는 같은 계정을 사용하는 앱·세션에서 공유됩니다. 자동 조회는 직접 조회를 켜기 전에는 일어나지 않습니다.";

    // Popover rows.
    public const string UsedPercentTemplate = "{percent}% 사용";
    public const string ResetTemplate = "초기화 {date}";
    public const string CheckedAtTemplate = "{time} 확인";
    public const string LastKnownPrefix = "마지막 확인값 · ";
    public const string WindowFiveHourSuffix = " (5시간)";
    public const string WindowSevenDaySuffix = " (7일)";
    public const string ClaudeBeforeFirstRunNote = "Claude를 한 번 실행하면 CLI가 보고한 세션·주간 한도가 여기에 표시됩니다.";
    public const string CardChecking = "계정 사용 한도를 확인하고 있습니다…";
    public const string CardNotCheckedYet = "아직 확인하지 않았습니다.";

    // RateLimitWindowLabel — the window names.
    public const string WindowSession = "세션";
    public const string WindowWeekly = "주간";
    public const string WindowDaily = "일간";
    public const string WindowMonthly = "월간";
    public const string WindowSpendLimit = "지출 한도";

    // AccountUsageService — detail sentences.
    public const string DetailNotCheckedYet = "계정 사용량을 아직 확인하지 않았습니다.";
    public const string DetailShutdown = "계정 조회를 종료했습니다.";
    public const string DetailCancelled = "계정 조회를 취소했습니다.";
    public const string DetailRefreshFailed = "계정 사용량을 갱신하지 못했습니다. 잠시 후 다시 확인하세요.";
    public const string DetailAuthentication = "CLI 로그인을 다시 확인하세요. 계정 한도 조회 권한이 없거나 로그인이 만료되었습니다.";
    public const string DetailRateLimited = "조회가 제한되었습니다. 잠시 후 자동으로 다시 확인합니다.";
    public const string DetailLastKnownSuffix = " 마지막으로 확인한 값입니다.";
    public const string DetailGeminiUnavailable = "Gemini CLI는 이 연결 방식에서 계정 한도를 제공하지 않습니다. CLI의 /stats에서 확인하세요.";
    public const string DetailUnsupportedProvider = "지원하지 않는 계정입니다.";
    public const string DetailCodexNotInstalled = "Codex CLI를 설치하고 로그인하세요.";
    public const string DetailCodexNeedsChatGPT = "ChatGPT로 Codex CLI에 로그인하면 계정 한도를 확인할 수 있습니다.";
    public const string DetailCustomAuthentication = "사용자 지정 인증의 계정 한도는 CLI에서 확인하세요.";
    public const string DetailCodexNoWindows = "이 계정에서 사용량 한도 창을 제공하지 않습니다.";
    public const string DetailCodex = "Codex 계정 한도";
    public const string DetailClaudeNoWindows = "이 Claude 계정에서 구독 한도를 제공하지 않습니다.";
    public const string DetailClaude = "Claude 계정 한도";
    public const string DetailSessionReportedStale = "세션에서 마지막으로 받은 계정 한도입니다.";
    public const string DetailSessionReported = "실행 중인 세션에서 받은 계정 한도입니다.";

    // 리셋권 (limit reset) entitlement rows — shared usage.reset.* locale keys.
    public const string ResetTitle = "리셋권";
    public const string ResetLink = "claude.ai에서 리셋";
    public const string ResetProgramCedarEmber = "지급된 리셋권";
    public const string ResetProgramJuniperTide = "한도 도달 리셋";
    public const string ResetAvailableCedarEmber = "남은 리셋권 {count}회 · {expiry}까지";
    public const string ResetAvailableJuniperTide = "지금 리셋 가능";
    public const string ResetHeld = "리셋권은 한도에 도달했을 때 쓸 수 있습니다.";
    public const string ResetCooldownCedarEmber = "{time}부터 다시 쓸 수 있습니다.";
    public const string ResetCooldownJuniperTide = "{time}부터 가능 · 주 {count}회";
    public const string ResetExhausted = "이번 기간의 리셋권을 모두 썼습니다.";
    public const string ResetNone = "현재 사용 가능한 리셋권이 없습니다.";
    public const string ResetIneligible = "이 계정에는 현재 리셋권이 제공되지 않습니다.";
    public const string ResetUnknown = "이 앱의 연결 방식에서는 리셋권 정보를 제공하지 않습니다. claude.ai 설정 > 사용량에서 확인하세요.";
}
