using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Microsoft.Win32;
using MightyCode.Core;

namespace MightyCode.WinUI;

/// <summary>
/// After the app from before the rename updated itself in place (docs/rename-identifiers.md), the install folder keeps its old
/// name and now holds MightyCode.exe, while the Start menu and Desktop shortcuts and the uninstall entry still name
/// MightyClaude.exe. This points them at the new exe, as the setup program would on a fresh install. Best effort: it never throws.
/// </summary>
internal static class RenamedInstall
{
    private const string OldExecutable = "MightyClaude.exe", NewExecutable = "MightyCode.exe", DisplayName = "Mighty Code";

    internal static void Refresh(string installDirectory)
    {
        if (!OperatingSystem.IsWindows()) return;
        var install = installDirectory.TrimEnd('\\', '/');
        var oldExe = Path.Combine(install, OldExecutable);
        var newExe = Path.Combine(install, NewExecutable);
        if (!File.Exists(newExe)) return;
        foreach (var folder in new[] { Environment.GetFolderPath(Environment.SpecialFolder.Programs), Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory) })
            foreach (var name in new[] { "Mighty Claude.lnk", "Mighty Code.lnk" })
                try { Recreate(Path.Combine(folder, name), Path.Combine(folder, DisplayName + ".lnk"), oldExe, newExe, install); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException) { }
        try { UpdateUninstallEntry(install, newExe); }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException) { }
    }

    private static void Recreate(string existing, string target, string oldExe, string newExe, string install)
    {
        if (!File.Exists(existing) || !string.Equals(TargetOf(existing), oldExe, StringComparison.OrdinalIgnoreCase)) return;
        var link = (IShellLinkW)new ShellLink();
        link.SetPath(newExe); link.SetWorkingDirectory(install); link.SetIconLocation(newExe, 0); link.SetDescription(DisplayName);
        ((IPersistFile)link).Save(target, false);
        if (!string.Equals(existing, target, StringComparison.OrdinalIgnoreCase)) File.Delete(existing);
    }

    private static string TargetOf(string shortcut)
    {
        var link = (IShellLinkW)new ShellLink();
        ((IPersistFile)link).Load(shortcut, 0);
        var path = new StringBuilder(1024);
        link.GetPath(path, path.Capacity, IntPtr.Zero, 0);
        return path.ToString();
    }

    /// <summary>The setup's uninstall entry (InstallerRegistration.UninstallKey), when it is for this folder and still shows the old exe as its icon.</summary>
    private static void UpdateUninstallEntry(string install, string newExe)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var key = Registry.CurrentUser.OpenSubKey(InstallerRegistration.UninstallKey, writable: true);
        if (key is null || !string.Equals((key.GetValue("InstallLocation") as string)?.TrimEnd('\\', '/'), install, StringComparison.OrdinalIgnoreCase)) return;
        if (key.GetValue("DisplayIcon") is not string icon || !icon.Contains(OldExecutable, StringComparison.OrdinalIgnoreCase)) return;
        key.SetValue("DisplayIcon", newExe, RegistryValueKind.String);
        key.SetValue("DisplayName", DisplayName, RegistryValueKind.String);
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int count, IntPtr data, uint flags);
        void GetIDList(out IntPtr list);
        void SetIDList(IntPtr list);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder text, int count);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string text);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int count);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int count);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int show);
        void SetShowCmd(int show);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder icon, int count, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string icon, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr window, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }
}
