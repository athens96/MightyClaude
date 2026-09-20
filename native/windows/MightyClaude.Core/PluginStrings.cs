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

    // ---- Install and marketplace refresh controls (ClaudePluginView.swift) ----
    // The scope picker, the per-plugin install button, 마켓플레이스 새로고침, the
    // progress line and the cancel button, word for word as macOS writes them.
    public const string ScopePickerLabel = "설치 범위";
    public const string ScopeLocalOption = "로컬 · 이 워크스페이스, 나만";
    public const string ScopeProjectOption = "프로젝트 · 팀과 공유";
    public const string ScopeUserOption = "사용자 · 모든 프로젝트";
    public const string ScopeNoteLocal = "현재 워크스페이스에만 적용하며 팀의 공유 설정은 바꾸지 않습니다.";
    public const string ScopeNoteProject = "프로젝트 설정에 기록해 팀과 같은 플러그인을 사용합니다.";
    // OS-bound substitution, recorded in docs/windows-plugins.md:
    // macOS reads "이 Mac의 모든 프로젝트에서 ...".
    public const string ScopeNoteUser = "이 PC의 모든 프로젝트에서 사용하는 사용자 설정에 설치합니다.";
    public const string ButtonInstall = "설치";
    public const string ButtonInstalling = "설치 중…";
    public const string ButtonMarketplaceRefresh = "마켓플레이스 새로고침";
    public const string ButtonCancelOperation = "작업 취소";
    public const string ButtonCancelling = "취소 중…";
    public const string ProgressInstalling = "플러그인 설치 중…";
    public const string ProgressRefreshing = "마켓플레이스 갱신 중…";
    public const string ProgressCancelling = "작업을 취소하는 중…";
    public const string NoRefreshableMarketplaces = "새로고침할 등록된 마켓플레이스가 없습니다.";
    public const string SelectPluginAndScopeAgain = "목록에서 플러그인과 설치 범위를 다시 선택하세요.";
    public const string LoadListFirst = "플러그인 목록을 먼저 불러오세요.";
    public const string MarketplacesRefreshedTemplate = "{count}개 마켓플레이스의 목록을 갱신했습니다.";

    // ---- Operation results (ClaudePluginService.swift install / refreshMarketplace) ----
    public const string OperationBusy = "다른 플러그인 작업이 진행 중입니다.";
    public const string OperationCancelled = "플러그인 작업을 취소했습니다.";
    public const string OperationCancelledByUser = "작업을 취소했습니다. 이미 반영된 변경이 있을 수 있어 목록을 다시 확인합니다.";
    public const string InstallBadIdOrScope = "플러그인 이름 또는 설치 범위가 올바르지 않습니다.";
    public const string InstallNotFound = "현재 등록된 마켓플레이스 목록에서 이 플러그인을 찾지 못했습니다. 목록을 다시 확인하세요.";
    public const string InstallSkipped = "선택한 범위에 이미 설치되어 있습니다. 비활성 상태라면 Claude CLI에서 활성화하세요.";
    public const string InstallCommandRequired = "이 플러그인은 추가 명령 실행 동의가 필요합니다. Claude CLI에서 표시된 명령을 확인한 뒤 설치하세요. 앱은 자동 승인하지 않습니다.";
    public const string InstallUnconfirmed = "설치 결과를 확인하지 못했습니다. 목록을 다시 읽어 설치 상태를 확인하세요.";
    public const string InstallFailed = "플러그인 설치에 실패했습니다. 네트워크·권한·조직 정책을 확인하세요.";
    public const string InstallSucceeded = "플러그인을 설치했습니다. 다음 Claude 실행부터 적용됩니다.";
    public const string MarketplaceBadName = "마켓플레이스 이름이 올바르지 않습니다.";
    public const string MarketplaceNotRegistered = "등록되지 않은 마켓플레이스입니다. 기존 등록 목록에서 선택하세요.";
    public const string MarketplaceRefreshFailed = "마켓플레이스 새로고침에 실패했습니다. 네트워크 상태와 접근 권한을 확인하세요.";
    public const string MarketplaceRefreshSucceeded = "선택한 마켓플레이스 목록을 새로고침했습니다.";
}
