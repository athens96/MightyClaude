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
}
