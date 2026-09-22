namespace MightyClaude.Core;

// CLI update section strings. Values are read from the shared locale files via
// Locale.Get() so macOS and Windows always show the same copy.
// StringsVerification.CliUpdateStringsMatchMacOS checks every value.
//
// OS-bound differences:
//   SectionDescription uses the Windows variant ("이 PC에 설치된").
//   DetailWingetPlan / DetailWingetRuntimeMissing are Windows-only.
public static class CliUpdateStrings
{
    public static readonly string SectionTitle = Locale.Get("settings.cliUpdate.sectionTitle");
    public static readonly string AutoUpdateToggle = Locale.Get("settings.cliUpdate.autoUpdateToggle");
    // OS-bound substitution: "이 PC에" instead of "이 Mac에".
    public static readonly string SectionDescription = Locale.Get("settings.cliUpdate.sectionDescriptionWindows");
    public static readonly string ProgressInspecting = Locale.Get("settings.cliUpdate.progressInspecting");
    public static readonly string ProgressProviderTemplate = Locale.Get("settings.cliUpdate.progressProviderTemplate");
    public static readonly string LastRunTemplate = Locale.Get("settings.cliUpdate.lastRunTemplate");
    public static readonly string UpdateButton = Locale.Get("settings.cliUpdate.updateButton");
    public static readonly string UpdatingButton = Locale.Get("settings.cliUpdate.updatingButton");
    public static readonly string ResultRowTemplate = Locale.Get("settings.cliUpdate.resultRowTemplate");
    public static readonly string VersionChangeTemplate = Locale.Get("settings.cliUpdate.versionChangeTemplate");

    public static readonly string StatusUpdated = Locale.Get("settings.cliUpdate.statusUpdated");
    public static readonly string StatusCurrent = Locale.Get("settings.cliUpdate.statusCurrent");
    public static readonly string StatusFailed = Locale.Get("settings.cliUpdate.statusFailed");
    public static readonly string StatusCancelled = Locale.Get("settings.cliUpdate.statusCancelled");
    public static readonly string StatusBusy = Locale.Get("settings.cliUpdate.statusBusy");
    public static readonly string StatusSkipped = Locale.Get("settings.cliUpdate.statusSkipped");

    public static readonly string DetailClosing = Locale.Get("settings.cliUpdate.detailClosing");
    public static readonly string DetailInspectCancelled = Locale.Get("settings.cliUpdate.detailInspectCancelled");
    public static readonly string DetailInspectFailed = Locale.Get("settings.cliUpdate.detailInspectFailed");
    public static readonly string DetailBusy = Locale.Get("settings.cliUpdate.detailBusy");
    public static readonly string DetailCancelled = Locale.Get("settings.cliUpdate.detailCancelled");
    public static readonly string DetailUnsupportedProvider = Locale.Get("settings.cliUpdate.detailUnsupportedProvider");
    public static readonly string DetailVersionUnknown = Locale.Get("settings.cliUpdate.detailVersionUnknown");
    public static readonly string DetailMissing = Locale.Get("settings.cliUpdate.detailMissing");
    public static readonly string DetailUnknownMethod = Locale.Get("settings.cliUpdate.detailUnknownMethod");
    public static readonly string DetailNativeClaude = Locale.Get("settings.cliUpdate.detailNativeClaude");
    public static readonly string DetailNpmPrerelease = Locale.Get("settings.cliUpdate.detailNpmPrerelease");
    public static readonly string DetailNpmRuntimeMissing = Locale.Get("settings.cliUpdate.detailNpmRuntimeMissing");
    public static readonly string DetailNpmPlan = Locale.Get("settings.cliUpdate.detailNpmPlan");
    public static readonly string DetailFailedExitTemplate = Locale.Get("settings.cliUpdate.detailFailedExitTemplate");
    public static readonly string DetailVersionRecheckFailed = Locale.Get("settings.cliUpdate.detailVersionRecheckFailed");
    public static readonly string DetailUpdated = Locale.Get("settings.cliUpdate.detailUpdated");
    public static readonly string DetailUnchanged = Locale.Get("settings.cliUpdate.detailUnchanged");

    // Windows-only: no Homebrew on Windows.
    public static readonly string DetailWingetPlan = Locale.Get("settings.cliUpdate.detailWingetPlan");
    public static readonly string DetailWingetRuntimeMissing = Locale.Get("settings.cliUpdate.detailWingetRuntimeMissing");

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
