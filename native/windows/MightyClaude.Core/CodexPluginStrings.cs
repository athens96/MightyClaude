namespace MightyClaude.Core;

// Korean copy that belongs to the Codex plugin list only, mirrored from
// CodexPluginService.swift (the status sentences) and the Codex branches of
// ClaudePluginView.swift (the footer and the empty-marketplace sentence).
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
    public const string DetailReady = "Codex 사용자 설치 목록과 현재 작업 폴더의 설정을 반영한 목록입니다. 이미 실행 중인 세션의 로드 상태와 다를 수 있습니다.";
    public const string DetailNoMarketplaces = "등록된 마켓플레이스가 없습니다. Codex CLI에서 plugin marketplace add로 등록한 뒤 목록을 다시 읽으세요.";

    // Appended to a ready detail; "{count}" is the number of rows left out.
    public const string DetailRestrictedSuffix = " 설치 정책으로 설치할 수 없는 항목 {count}개는 제외했습니다.";
    // CodexPluginService.readSnapshot - appended when the CLI wrote to stderr.
    public const string DetailWarningSuffix = " CLI 경고가 있습니다. 일부 목록이 최신 상태가 아닐 수 있으니 진단 출력을 확인하세요.";

    // CodexPluginService.command / readSnapshot - one sentence per failure.
    public const string DetailMissingCli = "Codex CLI가 설치되어 있지 않습니다. 먼저 CLI를 설치하세요.";
    public const string DetailUnknownVersion = "설치된 Codex CLI 버전을 확인하지 못했습니다.";
    public const string DetailUnsupported = "설치된 Codex CLI가 필요한 JSON 플러그인 명령을 지원하지 않습니다. 최신 Codex CLI로 업데이트하세요.";
    public const string DetailListingFailed = "Codex CLI에서 플러그인 목록을 읽지 못했습니다.";

    // ClaudePluginView.swift - the Codex branches of the footer and of the
    // empty marketplace tab. Claude keeps its own footer and its help link.
    // OS-bound substitution: "이 PC의" replaces "이 Mac의".
    public const string FooterNote = "이 PC의 Codex 설치 목록과 마켓플레이스 목록입니다. 설치 후 새 Codex 세션을 시작하세요.";
    public const string MarketplaceHelp = "Codex CLI에서 마켓플레이스를 등록한 뒤 목록을 새로고침하세요.";

    // ---- Install and marketplace upgrade (CodexPluginService.swift) ----
    // Only the sentences Codex words differently live here. The shared ones
    // (OperationBusy, OperationCancelled, InstallBadIdOrScope, InstallUnconfirmed,
    // MarketplaceBadName, MarketplaceNotRegistered, MarketplaceRefreshSucceeded,
    // DetailRemote) are read from PluginStrings and never repeated.
    public const string InstallSkipped = "이미 사용자 범위에 설치되어 있습니다. 비활성 상태라면 Codex에서 활성화하세요.";
    public const string InstallNotFound = "현재 설치 가능한 마켓플레이스 목록에서 이 플러그인을 찾지 못했습니다. 목록과 설치 정책을 다시 확인하세요.";
    public const string InstallSucceeded = "플러그인을 사용자 범위에 설치했습니다. 새 Codex 세션부터 적용됩니다. 연결이 필요한 앱은 Codex에서 인증하세요.";
    public const string InstallVerifyFailed = "CLI가 설치 결과를 반환했지만 설치 목록에서 확인하지 못했습니다. 목록을 다시 읽으세요.";
    public const string OperationFailed = "플러그인 작업에 실패했거나 결과 형식이 올바르지 않습니다. 네트워크·권한·조직 정책을 확인하세요.";
    public const string MarketplaceNotGit = "이 마켓플레이스는 Git 소스가 아니어서 Codex CLI로 갱신할 수 없습니다. 목록을 다시 읽으면 현재 상태를 확인할 수 있습니다.";
    public const string MarketplaceRefreshUnconfirmed = "마켓플레이스 새로고침을 확인하지 못했습니다. CLI 진단 출력을 확인하세요.";
    public const string NoGitMarketplaces = "갱신할 Git 마켓플레이스가 없습니다. 로컬·기본 제공 마켓플레이스는 목록 새로고침으로 확인하세요.";
    public const string RefreshGitOnly = "마켓플레이스 갱신은 등록된 Git 소스만 지원합니다. 다른 소스는 목록 새로고침으로 확인하세요.";
}
