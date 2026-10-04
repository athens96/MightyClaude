namespace MightyClaude.Core;

// Read on demand so the stored language preference applies after initialization.
// Korean values retain the macOS copy and documented Windows-specific wording.
public static class CompletionNotificationStrings
{
    public static string NotificationTitle => Locale.Get("windows.notifications.notificationTitle");
    public static string NotificationBodyTemplate => Locale.Get("windows.notifications.notificationBodyTemplate");
    public static string ToggleLabel => Locale.Get("windows.notifications.toggleLabel");
    public static string StatusAllowed => Locale.Get("windows.notifications.statusAllowed");
    public static string StatusDenied => Locale.Get("windows.notifications.statusDenied");
    public static string StatusNeedPermission => Locale.Get("windows.notifications.statusNeedPermission");
    public static string StatusVerificationMode => Locale.Get("windows.notifications.statusVerificationMode");
    public static string SettingsButton => Locale.Get("windows.notifications.settingsButton");
}
