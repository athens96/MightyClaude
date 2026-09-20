namespace MightyClaude.Core;

// Korean copy of the CLI accounts section, mirrored from
// CLIAccountsSettingsView.swift (section, buttons, status notes, confirmation),
// CLIAccountStatus.summary (summary labels), and CLIAccountService (detail sentences).
// WinUI reads these constants and never types Korean of its own.
// StringsVerification.CliAccountStringsMatchMacOS checks every value.
//
// One OS-bound substitution (recorded in docs/windows-cli-accounts.md):
//   SectionDescription reads "외부 터미널 창에서" where macOS reads "터미널 실행 창에서".
//   Sign-in opens an external Windows terminal window, not an in-app terminal run pane.
public static class CliAccountStrings
{
    // CLIAccountsSettingsView.swift — section title and description.
    public const string SectionTitle = "CLI 계정";
    // OS-bound substitution: "외부 터미널 창에서" replaces "터미널 실행 창에서".
    public const string SectionDescription = "로그인은 외부 터미널 창에서 진행됩니다. 앱이 명령을 실행해 두면 CLI가 브라우저를 엽니다. 다른 계정으로 바꿀 때는 브라우저에서 원하는 계정을 고르세요. 바꾼 계정은 다음 요청부터 적용됩니다.";

    // Per-provider row: loading indicator, pending note, not-installed label.
    public const string StatusChecking = "확인 중…";
    public const string StatusPending = "로그인 터미널을 열었습니다. 브라우저에서 로그인을 마치면 여기에 반영됩니다.";
    public const string StatusNotInstalled = "미설치";

    // Buttons.
    public const string ButtonCancelWait = "대기 취소";
    public const string ButtonChange = "계정 변경";
    public const string ButtonLogout = "로그아웃";
    public const string ButtonLogin = "로그인";
    public const string ButtonLoginClaude = "Claude 구독으로 로그인";
    public const string ButtonLoginConsole = "Anthropic Console(API 과금)로 로그인";
    public const string ButtonCancel = "취소";
    public const string RefreshTooltip = "상태 다시 확인";

    // Confirmation dialog.
    public const string ConfirmLogoutTitleTemplate = "{provider} 에서 로그아웃할까요?";
    public const string ConfirmChangeTitleTemplate = "{provider} 계정을 바꿀까요?";
    public const string ConfirmMessageTemplate = "{provider} CLI에 저장된 로그인 정보를 지웁니다. 터미널에서 직접 실행하는 {provider}에도 같이 적용됩니다.";

    // CLIAccountStatus.summary — three summary labels.
    public const string SummarySignedOut = "로그인되지 않음";
    public const string SummaryUnknown = "상태를 확인하지 못했습니다.";
    public const string SummarySignedIn = "로그인됨";

    // CLIAccountService — detail sentences for each failure / edge case.
    public const string DetailClaudeParseError = "Claude 로그인 상태를 읽지 못했습니다.";
    public const string DetailCodexParseError = "Codex 로그인 상태를 읽지 못했습니다.";
    public const string DetailGeminiNotInstalled = "Gemini CLI가 설치되어 있지 않습니다.";
    public const string DetailNotInstalledTemplate = "{provider} CLI가 설치되어 있지 않습니다.";
    public const string DetailUnsupportedProvider = "지원하지 않는 실행기입니다.";
    public const string DetailGeminiApiKeyPresent = "GEMINI_API_KEY 환경 변수로 인증합니다. 바꾸려면 그 값을 바꾸거나 Gemini의 /auth에서 방식을 바꾸세요.";
    public const string DetailGeminiApiKeyAbsent = "GEMINI_API_KEY 환경 변수를 확인하세요.";
    public const string DetailVertexPresent = "Google Cloud 자격 증명으로 인증합니다. gcloud에서 계정을 바꾸세요.";
    public const string DetailVertexAbsent = "Vertex AI 자격 증명을 확인하지 못했습니다.";
    public const string DetailClaudeTimeout = "Claude 상태 확인이 제한 시간 안에 끝나지 않았습니다.";
    public const string DetailClaudeUnknown = "이 Claude CLI에서 로그인 상태를 읽지 못했습니다. CLI를 업데이트해 보세요.";
    public const string DetailRunFailed = "상태 명령을 실행하지 못했습니다.";
    public const string DetailGeminiLogoutFailedTemplate = "Gemini 로그아웃에 실패했습니다: {reason}";
}
