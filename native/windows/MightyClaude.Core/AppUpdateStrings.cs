namespace MightyClaude.Core;

// App update section strings. Values are read from the shared locale files via
// Locale.Get() so macOS and Windows always show the same copy.
// StringsVerification.AppUpdateStringsMatchMacOS checks every value.
//
// OS-bound differences:
//   ManifestUrlHint uses the Windows variant (includes "leave blank" note).
//   NoPublicKeyNotice uses the Windows variant (explains missing public key).
public static class AppUpdateStrings
{
    public static readonly string SectionTitle = Locale.Get("settings.appUpdate.sectionTitle");
    public static readonly string CurrentVersionLabel = Locale.Get("settings.appUpdate.currentVersionLabel");

    public static readonly string ManifestUrlPlaceholder = Locale.Get("settings.appUpdate.manifestUrlPlaceholder");
    public static readonly string BuiltInAddressTemplate = Locale.Get("settings.appUpdate.builtInAddressTemplate");
    public static readonly string ManifestUrlHint = Locale.Get("settings.appUpdate.manifestUrlHintWindows");

    public static readonly string AutoCheckToggle = Locale.Get("settings.appUpdate.autoCheckToggle");

    public static readonly string SignatureVerified = Locale.Get("settings.appUpdate.signatureVerified");

    public static readonly string NotCheckedYet = Locale.Get("settings.appUpdate.notCheckedYet");
    public static readonly string LastCheckedTemplate = Locale.Get("settings.appUpdate.lastCheckedTemplate");
    public static readonly string Checking = Locale.Get("settings.appUpdate.checking");
    public static readonly string UpToDate = Locale.Get("settings.appUpdate.upToDate");
    public static readonly string AvailableTemplate = Locale.Get("settings.appUpdate.availableTemplate");
    public static readonly string DownloadingTemplate = Locale.Get("settings.appUpdate.downloadingTemplate");
    public static readonly string StagingProgress = Locale.Get("settings.appUpdate.stagingProgress");
    public static readonly string ReadyTemplate = Locale.Get("settings.appUpdate.readyTemplate");
    public static readonly string Installing = Locale.Get("settings.appUpdate.installing");

    public static readonly string CheckButton = Locale.Get("settings.appUpdate.checkButton");
    public static readonly string DownloadButton = Locale.Get("settings.appUpdate.downloadButton");
    public static readonly string CancelButton = Locale.Get("settings.appUpdate.cancelButton");
    public static readonly string InstallButton = Locale.Get("settings.appUpdate.installButton");
    public static readonly string InProgressButton = Locale.Get("settings.appUpdate.inProgressButton");

    // Windows-only: replaces the section when no public key is present.
    public static readonly string NoPublicKeyNotice = Locale.Get("settings.appUpdate.noPublicKeyNoticeWindows");
}
