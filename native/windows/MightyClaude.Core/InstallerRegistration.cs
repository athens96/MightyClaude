using System.Security;
using Microsoft.Win32;

namespace MightyClaude.Core;

/// <summary>
/// The uninstall entry the setup program writes (native/windows/installer/MightyClaude.iss), seen from the app. Setup records
/// the version it installed; the app later replaces its own folder when it updates (docs/windows-app-update.md), so Settings ›
/// Apps would keep showing the setup's version. At each start the app writes its own version into that entry, and only when
/// the entry names the folder this app is running from: a copy unpacked from the ZIP elsewhere leaves it alone.
/// </summary>
public static class InstallerRegistration
{
    /// <summary>The setup's AppId with Inno Setup's suffix; it must match MightyClaude.iss.</summary>
    public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{F2F99AEF-FB35-4476-A3EE-49453FA0527F}_is1";

    /// <summary>
    /// The version to write, or null when nothing should change: no entry, an entry for another folder, or the same version.
    /// Folders compare without case and without a trailing separator, as Inno Setup writes InstallLocation with one.
    /// </summary>
    public static string? VersionToWrite(string? installLocation, string? displayVersion, string appDirectory, string version)
    {
        if (string.IsNullOrWhiteSpace(installLocation) || string.IsNullOrWhiteSpace(version)) return null;
        // Both separators, not Path's: the Mac runs these checks on Windows paths too.
        static string Folder(string path) => path.Trim().TrimEnd('\\', '/');
        if (!string.Equals(Folder(installLocation), Folder(appDirectory), StringComparison.OrdinalIgnoreCase)) return null;
        return string.Equals(displayVersion, version, StringComparison.Ordinal) ? null : version;
    }

    /// <summary>Brings the entry's DisplayVersion to this app's version. True when it wrote one; never throws.</summary>
    public static bool SyncDisplayVersion(string appDirectory, string version)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallKey, writable: true);
            if (key is null) return false;
            if (VersionToWrite(key.GetValue("InstallLocation") as string, key.GetValue("DisplayVersion") as string, appDirectory, version) is not { } write) return false;
            key.SetValue("DisplayVersion", write, RegistryValueKind.String);
            return true;
        }
        catch (Exception ex) when (ex is SecurityException or IOException or UnauthorizedAccessException) { return false; }
    }
}
