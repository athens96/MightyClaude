using System.Runtime.InteropServices;

namespace MightyCode.Core;

/// <summary>
/// Which files "open with the default app" (the files pane, a reference's bubble and preview) refuses. Windows' launcher
/// (<c>Launcher.LaunchFileAsync</c>) refuses nothing for a desktop app: called from one it ran a <c>.js</c> through WScript and a
/// <c>.jar</c> through javaw. A file whose type can run code is never handed to it: one PATHEXT names, one
/// <c>AssocIsDangerous</c> counts (.exe, .lnk, .js, .ps1, …), one whose own handler loads code it is given (.xll, .jar, .py and the
/// rest of <see cref="CodeTypes"/>), and one whose handler, as <c>AssocQueryString</c> resolves it, is an interpreter or a
/// launcher (<see cref="Launchers"/>) or a DLL that rundll32 runs (.application, .themepack).
/// </summary>
public static class WindowsDefaultApp
{
    /// <summary>
    /// Types whose handler runs or loads what the file holds, or opens a place the file names, whatever their handler on this machine
    /// is called: Java archives, ClickOnce and browser-hosted WPF applications, Excel add-ins (native code), troubleshooting packs,
    /// Remote Desktop connections, themes, Python scripts, and Explorer's search and library locations (which may name another
    /// machine's share).
    /// </summary>
    private static readonly HashSet<string> CodeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jar", ".application", ".xbap", ".xll", ".diagcab", ".rdp", ".themepack", ".search-ms", ".searchconnector-ms", ".library-ms", ".py", ".pyw",
    };

    /// <summary>Handlers that run the file they are given as a program, a script or a remote session.</summary>
    private static readonly HashSet<string> Launchers = new(StringComparer.OrdinalIgnoreCase)
    {
        "java", "javaw", "py", "pyw", "python", "pythonw", "node", "wscript", "cscript", "mshta", "rundll32", "msdt", "powershell", "pwsh", "cmd", "presentationhost", "dfsvc", "mstsc",
    };

    /// <summary>Whether <paramref name="path"/>'s type can run code when its default app opens it (Windows only; elsewhere false).</summary>
    public static bool RunsCode(string path)
    {
        var extension = Path.GetExtension(path);
        if (extension.Length < 2 || !OperatingSystem.IsWindows()) return false;
        if (CodeTypes.Contains(extension) || (Environment.GetEnvironmentVariable("PATHEXT") ?? WindowsCommand.DefaultPathExt).Split(';', StringSplitOptions.TrimEntries).Any(listed => listed.Equals(extension, StringComparison.OrdinalIgnoreCase))) return true;
        return AssocIsDangerous(extension) || Handler(extension) is { } handler && LaunchesCode(handler);
    }

    /// <summary>Whether a handler (the executable <c>AssocQueryString</c> names) runs what it opens: a <see cref="Launchers"/> name, any python, or a DLL.</summary>
    internal static bool LaunchesCode(string handler)
    {
        var name = Path.GetFileNameWithoutExtension(handler.Trim().Trim('"'));
        return Launchers.Contains(name) || name.StartsWith("python", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(handler.Trim().Trim('"')).Equals(".dll", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The executable that opens <paramref name="extension"/>'s files; null when Windows names none.</summary>
    private static string? Handler(string extension)
    {
        const uint NoTruncate = 0x20, IgnoreUnknown = 0x400; const int Executable = 2;
        var buffer = new char[4096]; var length = (uint)buffer.Length;
        return AssocQueryStringW(NoTruncate | IgnoreUnknown, Executable, extension, null, buffer, ref length) == 0 && length > 1 ? new string(buffer, 0, (int)length - 1) : null;
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AssocIsDangerous(string extension);
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)] private static extern int AssocQueryStringW(uint flags, int kind, string association, string? extra, [Out] char[] output, ref uint length);
}
