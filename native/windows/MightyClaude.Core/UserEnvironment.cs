using System.Runtime.InteropServices;

namespace MightyClaude.Core;

/// <summary>
/// The signed-in user's own environment (USERPROFILE, APPDATA, LOCALAPPDATA, TEMP, PATH, PATHEXT …), as Windows builds it
/// for a new logon (<c>CreateEnvironmentBlock</c> with no inheritance). The update helper runs with only <c>SystemRoot</c>
/// (<see cref="AppUpdateReplacement.HelperEnvironment"/>); without this the app it starts again — and every CLI, shell and
/// tool the app then runs — would inherit that bare environment, and tools that need a home folder (Antigravity's
/// <c>%userprofile% is not defined</c>, gh, Python installers, Unreal) fail.
/// </summary>
public static class UserEnvironment
{
    /// <summary>The variables whose absence marks a bare environment.</summary>
    public static readonly string[] Markers = ["USERPROFILE", "APPDATA", "LOCALAPPDATA"];

    /// <summary>True when any marker is missing or empty in <paramref name="current"/>.</summary>
    public static bool IsBare(Func<string, string?> current) => Markers.Any(name => string.IsNullOrEmpty(current(name)));

    /// <summary>
    /// What to add to <paramref name="current"/> from <paramref name="fresh"/>: each fresh variable that is missing or empty
    /// now. A variable the process already has keeps its value, so a launch with its own settings is never overridden.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>> Missing(Func<string, string?> current, IReadOnlyDictionary<string, string> fresh) =>
        fresh.Where(pair => pair.Key.Length > 0 && pair.Value.Length > 0 && string.IsNullOrEmpty(current(pair.Key))).ToList();

    /// <summary>
    /// A <c>CreateEnvironmentBlock</c> block (<c>NAME=value\0…\0\0</c>) as a case-blind dictionary. Entries without a name
    /// (the drive-letter <c>=C:=C:\</c> ones begin with <c>=</c>) are left out; a later duplicate wins.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Parse(string block)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in block.Split('\0'))
        {
            var equals = entry.IndexOf('=');
            if (equals <= 0) continue;
            values[entry[..equals]] = entry[(equals + 1)..];
        }
        return values;
    }

    /// <summary>The user's fresh environment, or null off Windows or when Windows will not build it.</summary>
    public static IReadOnlyDictionary<string, string>? Fresh()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var token = IntPtr.Zero; var block = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out token)) return null;
            if (!CreateEnvironmentBlock(out block, token, false) || block == IntPtr.Zero) return null;
            // The block is UTF-16 strings, each ended by NUL, the whole ended by an empty one.
            var text = new System.Text.StringBuilder(); var offset = 0;
            while (true)
            {
                var entry = Marshal.PtrToStringUni(block + offset) ?? "";
                if (entry.Length == 0) break;
                text.Append(entry).Append('\0'); offset += (entry.Length + 1) * 2;
            }
            var values = Parse(text.ToString());
            return values.Count == 0 ? null : values;
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or ExternalException) { return null; }
        finally
        {
            if (block != IntPtr.Zero) DestroyEnvironmentBlock(block);
            if (token != IntPtr.Zero) CloseHandle(token);
        }
    }

    /// <summary>
    /// At startup: a process started with a bare environment (by an update helper that predates this fix, or any other
    /// launcher that cleared it) takes the user's missing variables, so what it runs inherits a whole environment.
    /// Returns how many variables were added.
    /// </summary>
    public static int RepairCurrentProcess()
    {
        if (!IsBare(Environment.GetEnvironmentVariable) || Fresh() is not { } fresh) return 0;
        var missing = Missing(Environment.GetEnvironmentVariable, fresh);
        foreach (var (name, value) in missing) Environment.SetEnvironmentVariable(name, value);
        return missing.Count;
    }

    private const uint TokenQuery = 0x0008;
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("userenv.dll", SetLastError = true)] private static extern bool CreateEnvironmentBlock(out IntPtr block, IntPtr token, bool inherit);
    [DllImport("userenv.dll", SetLastError = true)] private static extern bool DestroyEnvironmentBlock(IntPtr block);
}
