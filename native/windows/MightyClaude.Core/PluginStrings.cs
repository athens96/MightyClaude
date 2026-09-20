namespace MightyClaude.Core;

// Korean copy of the Claude plugin list, mirrored from
// ClaudePluginView.swift (title, tabs, filter, rows, buttons, empty and remote
// copy) and ClaudePluginService.swift (the status sentences).
// WinUI reads these constants and never types Korean of its own.
// ClaudePluginVerification.claudePluginStringsMatchMacOS checks every value.
//
// One OS-bound substitution (recorded in docs/windows-plugins.md):
//   DetailRemote reads "이 PC의 설치는" where macOS reads "이 Mac의 설치는".
//   Windows has no Mac; the sentence names the computer the app runs on.
//
// Install buttons, the scope picker and the marketplace refresh belong to the
// marketplace feature and have no copy here. Reading changes nothing.
public static class PluginStrings
{
    // ClaudePluginView.swift — window header and tabs.
    public const string TitleTemplate = "{provider} 플러그인";
    public const string TabInstalled = "설치됨";
    public const string TabMarketplace = "마켓플레이스";
    // tab(_:value:count:) draws "\(title) \(count)".
    public const string TabCountTemplate = "{title} {count}";

    // Search field and marketplace filter. "전체" is the filter's empty tag.
    public const string SearchPlaceholder = "이름 또는 설명 검색";
    public const string FilterAll = "전체";

    // Buttons.
    public const string ButtonReload = "목록 새로고침";
    public const string ButtonClose = "닫기";
    public const string DiagnosticsDisclosure = "명령 실행 상세";
    public const string MarketplaceHelpLink = "마켓플레이스 추가 방법";

    // Remote workspace: the two sentences the browser shows instead of a list.
    public const string RemoteTitle = "원격 워크스페이스에서는 관리할 수 없습니다.";
    public const string RemoteNote = "원격 컴퓨터의 MightyClaude에서 플러그인을 관리하세요.";

    // Installed row: subtitle is "{marketplace} · {scope}" with this fallback,
    // and the enabled badge.
    public const string DirectInstall = "직접 설치";
    public const string SubtitleTemplate = "{left} · {right}";
    public const string ScopeLocal = "로컬 · 나만";
    public const string ScopeProject = "프로젝트 · 공유";
    public const string ScopeUser = "사용자 · 전체";
    public const string ScopeManaged = "관리자 관리";
    public const string StateEnabled = "활성";
    public const string StateDisabled = "비활성";
    public const string StateUnknown = "상태 미확인";

    // Catalog row: subtitle is "{marketplace} · {sourceKind}".
    public const string NoDescription = "설명이 제공되지 않았습니다.";

    // Empty list and progress copy.
    public const string EmptyLoading = "플러그인 목록을 불러오는 중…";
    public const string EmptyFailed = "목록을 불러오지 못했습니다. 목록 새로고침으로 다시 확인하세요.";
    public const string EmptyFiltered = "검색 조건에 맞는 플러그인이 없습니다.";
    public const string EmptyInstalled = "설치된 플러그인이 없습니다.";
    public const string EmptyAvailable = "등록된 마켓플레이스에서 제공한 플러그인이 없습니다.";
    public const string ProgressLoading = "목록을 불러오는 중…";
    public const string FooterNote = "현재 폴더의 설정과 저장된 목록입니다. 새 설치는 다음 Claude 실행부터 적용됩니다.";

    // ClaudePluginService.swift — one sentence per status.
    public const string DetailReady = "현재 작업 폴더의 CLI 설정과 등록된 마켓플레이스의 캐시 목록입니다. 이미 실행 중인 세션의 로드 상태와 다를 수 있습니다.";
    public const string DetailNoMarketplaces = "등록된 마켓플레이스가 없습니다. Claude CLI에서 marketplace add로 등록한 뒤 목록을 다시 읽으세요.";
    // OS-bound substitution: "이 PC의 설치는" replaces "이 Mac의 설치는".
    public const string DetailRemote = "원격 워크스페이스의 플러그인은 해당 호스트에서 관리하세요. 이 PC의 설치는 변경하지 않습니다.";
    public const string DetailCancelled = "플러그인 조회를 취소했습니다.";
    public const string DetailFailed = "플러그인 목록을 읽지 못했습니다.";
    public const string DetailInvalidWorkspace = "로컬 작업 폴더가 올바르지 않습니다.";
    public const string DetailMissingWorkspace = "작업 폴더를 찾지 못했습니다.";
    public const string DetailMissingCli = "Claude CLI가 설치되어 있지 않습니다. 먼저 CLI를 설치하세요.";
    public const string DetailUnknownVersion = "설치된 Claude CLI 버전을 확인하지 못했습니다.";
    public const string DetailUnsupported = "이 플러그인 관리 화면은 JSON 설치 결과를 지원하는 Claude Code 2.1.268 이상이 필요합니다.";
    public const string DetailListingFailed = "Claude CLI에서 플러그인 목록을 읽지 못했습니다.";
    public const string DetailMarketplacesFailed = "등록된 마켓플레이스 목록을 읽지 못했습니다.";
    public const string DetailMalformed = "플러그인 목록 형식 또는 크기가 올바르지 않습니다. 빈 목록으로 처리하지 않았습니다.";
    public const string DetailIncomplete = "플러그인 작업을 완료하지 못했습니다. 실행 시간·출력 한도 또는 CLI 접근 상태를 확인하세요.";
}
