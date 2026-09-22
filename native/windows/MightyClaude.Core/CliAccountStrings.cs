namespace MightyClaude.Core;

// CLI accounts section strings. Values are read from the shared locale files via
// Locale.Get() so macOS and Windows always show the same copy.
// StringsVerification.CliAccountStringsMatchMacOS checks every value.
//
// OS-bound differences:
//   SectionDescription uses the Windows variant ("외부 터미널 창에서").
public static class CliAccountStrings
{
    public static readonly string SectionTitle = Locale.Get("settings.cliAccounts.sectionTitle");
    // OS-bound substitution: "외부 터미널 창에서" instead of "터미널 실행 창에서".
    public static readonly string SectionDescription = Locale.Get("settings.cliAccounts.sectionDescriptionWindows");

    public static readonly string StatusChecking = Locale.Get("settings.cliAccounts.statusChecking");
    public static readonly string StatusPending = Locale.Get("settings.cliAccounts.statusPending");
    public static readonly string StatusNotInstalled = Locale.Get("settings.cliAccounts.statusNotInstalled");

    public static readonly string ButtonCancelWait = Locale.Get("settings.cliAccounts.buttonCancelWait");
    public static readonly string ButtonChange = Locale.Get("settings.cliAccounts.buttonChange");
    public static readonly string ButtonLogout = Locale.Get("settings.cliAccounts.buttonLogout");
    public static readonly string ButtonLogin = Locale.Get("settings.cliAccounts.buttonLogin");
    public static readonly string ButtonLoginClaude = Locale.Get("settings.cliAccounts.buttonLoginClaude");
    public static readonly string ButtonLoginConsole = Locale.Get("settings.cliAccounts.buttonLoginConsole");
    public static readonly string ButtonCancel = Locale.Get("settings.cliAccounts.buttonCancel");
    public static readonly string RefreshTooltip = Locale.Get("settings.cliAccounts.refreshTooltip");

    public static readonly string ConfirmLogoutTitleTemplate = Locale.Get("settings.cliAccounts.confirmLogoutTitleTemplate");
    public static readonly string ConfirmChangeTitleTemplate = Locale.Get("settings.cliAccounts.confirmChangeTitleTemplate");
    public static readonly string ConfirmMessageTemplate = Locale.Get("settings.cliAccounts.confirmMessageTemplate");

    public static readonly string SummarySignedOut = Locale.Get("settings.cliAccounts.summarySignedOut");
    public static readonly string SummaryUnknown = Locale.Get("settings.cliAccounts.summaryUnknown");
    public static readonly string SummarySignedIn = Locale.Get("settings.cliAccounts.summarySignedIn");

    public static readonly string DetailClaudeParseError = Locale.Get("settings.cliAccounts.detailClaudeParseError");
    public static readonly string DetailCodexParseError = Locale.Get("settings.cliAccounts.detailCodexParseError");
    public static readonly string DetailGeminiNotInstalled = Locale.Get("settings.cliAccounts.detailGeminiNotInstalled");
    public static readonly string DetailNotInstalledTemplate = Locale.Get("settings.cliAccounts.detailNotInstalledTemplate");
    public static readonly string DetailUnsupportedProvider = Locale.Get("settings.cliAccounts.detailUnsupportedProvider");
    public static readonly string DetailGeminiApiKeyPresent = Locale.Get("settings.cliAccounts.detailGeminiApiKeyPresent");
    public static readonly string DetailGeminiApiKeyAbsent = Locale.Get("settings.cliAccounts.detailGeminiApiKeyAbsent");
    public static readonly string DetailVertexPresent = Locale.Get("settings.cliAccounts.detailVertexPresent");
    public static readonly string DetailVertexAbsent = Locale.Get("settings.cliAccounts.detailVertexAbsent");
    public static readonly string DetailClaudeTimeout = Locale.Get("settings.cliAccounts.detailClaudeTimeout");
    public static readonly string DetailClaudeUnknown = Locale.Get("settings.cliAccounts.detailClaudeUnknown");
    public static readonly string DetailRunFailed = Locale.Get("settings.cliAccounts.detailRunFailed");
    public static readonly string DetailGeminiLogoutFailedTemplate = Locale.Get("settings.cliAccounts.detailGeminiLogoutFailedTemplate");
}
