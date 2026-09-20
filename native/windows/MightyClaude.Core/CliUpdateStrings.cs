namespace MightyClaude.Core;

// Korean copy of the CLI update section, mirrored from
// CLIUpdateSettingsView.swift (section, toggle, button, status labels) and
// CLIUpdateService.swift (every detail sentence).
// WinUI reads these constants and never types Korean of its own.
// StringsVerification.CliUpdateStringsMatchMacOS checks every value.
//
// One OS-bound substitution (recorded in docs/windows-cli-update.md):
//   SectionDescription reads "이 PC에 설치된" where macOS reads "이 Mac에 설치된".
// Two Windows-only sentences have no macOS literal because Windows has no
// Homebrew; they are 보류 rows in docs/windows-parity.md, not silent copy.
public static class CliUpdateStrings
{
    // CLIUpdateSettingsView.swift — section, toggle, description, progress, button.
    public const string SectionTitle = "CLI 업데이트";
    public const string AutoUpdateToggle = "앱 시작 시 CLI 자동 업데이트";
    // OS-bound substitution: "이 PC에" replaces "이 Mac에".
    public const string SectionDescription = "이 PC에 설치된 Claude Code·Codex·Gemini CLI를 기존 설치 방식으로 업데이트합니다.";
    public const string ProgressInspecting = "설치 정보 확인 중…";
    public const string ProgressProviderTemplate = "{provider} 업데이트 중…";
    public const string LastRunTemplate = "마지막 실행 {time}";
    public const string UpdateButton = "업데이트 하기";
    public const string UpdatingButton = "업데이트 중…";
    // One result row: "{provider} · {status}" and the version change under it.
    public const string ResultRowTemplate = "{provider} · {status}";
    public const string VersionChangeTemplate = "{before} → {after}";

    // CLIUpdateSettingsView.swift label(_:) — one label per status.
    public const string StatusUpdated = "업데이트 완료";
    public const string StatusCurrent = "변경 없음";
    public const string StatusFailed = "업데이트 실패";
    public const string StatusCancelled = "취소됨";
    public const string StatusBusy = "다른 업데이트 진행 중";
    public const string StatusSkipped = "건너뜀";

    // CLIUpdateService.swift — detail sentences, in the order they appear there.
    public const string DetailClosing = "앱이 종료 중입니다.";
    public const string DetailInspectCancelled = "설치 확인을 취소했습니다.";
    public const string DetailInspectFailed = "CLI 설치 정보를 확인하지 못했습니다.";
    public const string DetailBusy = "다른 CLI를 업데이트하고 있습니다.";
    public const string DetailCancelled = "업데이트를 취소했습니다.";
    public const string DetailUnsupportedProvider = "지원하지 않는 CLI입니다.";
    public const string DetailVersionUnknown = "CLI 버전을 확인하지 못해 업데이트하지 않았습니다.";
    public const string DetailMissing = "설치된 CLI가 없어 건너뜁니다. 새로 설치하지 않습니다.";
    public const string DetailUnknownMethod = "수동 설치 또는 확인할 수 없는 설치 방식입니다. 기존 설치 방법으로 직접 업데이트하세요.";
    public const string DetailNativeClaude = "Claude Code의 기본 업데이트 명령을 사용합니다.";
    public const string DetailNpmPrerelease = "시험판 또는 확인할 수 없는 npm 채널은 자동 변경하지 않습니다. 기존 채널에서 직접 업데이트하세요.";
    public const string DetailNpmRuntimeMissing = "npm 설치는 확인했지만 해당 설치를 업데이트할 Node.js/npm을 찾지 못했습니다.";
    public const string DetailNpmPlan = "기존 npm 설치 위치에서 공식 패키지만 업데이트합니다.";
    public const string DetailFailedExitTemplate = "업데이트 명령이 종료 코드 {code}로 실패했습니다. 설치 권한이나 네트워크 상태를 확인하세요.";
    public const string DetailVersionRecheckFailed = "업데이트 명령은 끝났지만 CLI 버전을 다시 확인하지 못했습니다.";
    public const string DetailUpdated = "CLI를 업데이트했습니다.";
    public const string DetailUnchanged = "업데이트 명령을 완료했습니다. 설치된 버전은 동일합니다.";

    // 보류 (docs/windows-parity.md): Windows has no Homebrew, so these two
    // sentences replace the Homebrew ones. They keep the macOS shape — name the
    // package manager, say only that one package is touched.
    public const string DetailWingetPlan = "설치된 winget의 해당 패키지만 업데이트합니다.";
    public const string DetailWingetRuntimeMissing = "winget 설치이지만 winget 실행 파일을 찾지 못했습니다.";

    /// The macOS label(_:) switch: an unknown status reads 건너뜀.
    public static string StatusLabel(string status) => status switch
    {
        "updated" => StatusUpdated,
        "current" => StatusCurrent,
        "failed" => StatusFailed,
        "cancelled" => StatusCancelled,
        "busy" => StatusBusy,
        _ => StatusSkipped,
    };
}
