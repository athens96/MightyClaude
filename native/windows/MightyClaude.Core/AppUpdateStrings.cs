namespace MightyClaude.Core;

// Korean copy of the app update section, mirrored from
// AppUpdateSettingsView.swift (section, labels, toggle, status, buttons).
// WinUI reads these constants and never types Korean of its own.
// StringsVerification.AppUpdateStringsMatchMacOS checks every value.
//
// OS-bound differences (recorded in docs/windows-app-update.md):
//   Windows rule 1: a build without a public key shows NoPublicKeyNotice and
//   disables the section entirely; macOS shows SignatureUnverified (orange) but
//   still allows the check. NoPublicKeyNotice has no macOS literal and is a
//   보류 row in docs/windows-parity.md.
public static class AppUpdateStrings
{
    // AppUpdateSettingsView.swift — section heading and current version label.
    public const string SectionTitle = "앱 업데이트";
    public const string CurrentVersionLabel = "현재 버전";

    // AppUpdateSettingsView.swift — URL field placeholder and hints.
    public const string ManifestUrlPlaceholder = "업데이트 정보 주소 (https://…/latest.json)";
    public const string BuiltInAddressTemplate = "빌드에 포함된 주소를 사용합니다: {address}";
    public const string ManifestUrlHint = "Cloudflare에 올린 latest.json의 https 주소를 입력하세요. 비워 두면 빌드에 포함된 주소를 씁니다.";

    // AppUpdateSettingsView.swift — automatic check toggle.
    public const string AutoCheckToggle = "앱 시작 시 하루 한 번 새 버전 확인";

    // AppUpdateSettingsView.swift — public key status line (shown when key present).
    public const string SignatureVerified = "서명 검증: 이 빌드에 포함된 공개 키로 서명된 업데이트 정보만 받습니다.";

    // AppUpdateSettingsView.swift — status descriptions.
    public const string NotCheckedYet = "아직 확인하지 않았습니다.";
    public const string LastCheckedTemplate = "마지막 확인 {time}";
    public const string Checking = "새 버전 확인 중…";
    public const string UpToDate = "최신 버전입니다.";
    public const string AvailableTemplate = "새 버전 {version} 이 있습니다.";
    public const string DownloadingTemplate = "{percent}% 받는 중…";
    public const string StagingProgress = "패키지를 풀고 확인하는 중…";
    public const string ReadyTemplate = "{version} 설치 준비 완료 · 설치하면 앱이 종료된 뒤 교체되고 다시 실행됩니다.";
    public const string Installing = "앱을 종료하고 교체하는 중…";

    // AppUpdateSettingsView.swift — action buttons.
    public const string CheckButton = "업데이트 확인";
    public const string DownloadButton = "다운로드";
    public const string CancelButton = "취소";
    public const string InstallButton = "설치하고 다시 실행";
    public const string InProgressButton = "진행 중…";

    // 보류 (docs/windows-parity.md): Windows rule 1 — no public key means no
    // update check at all; macOS shows SignatureUnverified (orange) and still
    // allows the check. This sentence replaces the entire section on Windows.
    public const string NoPublicKeyNotice = "이 빌드에는 업데이트 공개 키가 없어 업데이트 확인을 지원하지 않습니다.";
}
