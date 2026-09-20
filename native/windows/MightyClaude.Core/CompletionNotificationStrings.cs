namespace MightyClaude.Core;

// Korean copy of completion notification UI, mirrored from AgentCompanion.swift
// (title and body) and AgentCompanionViews.swift (toggle, status texts, button).
// WinUI reads these constants and never types Korean of its own.
// StringsVerification checks every value against its macOS literal.
// One OS-bound substitution (see docs/windows-completion-notification.md):
//   ToggleLabel reads "Windows" where macOS reads "Mac".
public static class CompletionNotificationStrings
{
    // AgentCompanion.swift CompletionNotifications.send
    public const string NotificationTitle = "MightyClaude · 작업 완료";
    public const string NotificationBodyTemplate = "{title}의 작업이 완료되었습니다.";

    // AgentCompanionViews.swift Toggle / status texts / settings button.
    // OS-bound substitution: "Windows" replaces "Mac" in ToggleLabel.
    public const string ToggleLabel = "작업 완료 시 Windows 알림";
    public const string StatusAllowed = "허용됨";
    public const string StatusDenied = "시스템 설정에서 알림을 허용하세요";
    public const string StatusNeedPermission = "권한 필요";
    public const string StatusVerificationMode = "검증 모드";
    public const string SettingsButton = "알림 설정";
}
