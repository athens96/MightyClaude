using System.Runtime.InteropServices;

namespace MightyClaude.Core;

/// <summary>
/// Whether a shell pane is running a command (M/MightyCore/ShellActivity.swift). The Mac reads the
/// terminal's foreground process group; Windows has none, so a command is running while a process of the
/// shell's job object other than the shell is attached to the shell's own console: a program started in a
/// window of its own, a GUI program and a detached one are not occupying the terminal. It is decided over a
/// snapshot of the job, so it holds without a terminal.
/// </summary>
public static class ShellActivity
{
    /// <summary>One process of the shell's job: its id and the console it is attached to as Windows records it (0 when none could be read).</summary>
    public readonly record struct Entry(int Id, long Console);

    /// <summary>True when a process of the job other than the shell shares the shell's console; never when the shell's console is unknown.</summary>
    public static bool IsBusy(int shell, IReadOnlyCollection<Entry> job)
    {
        var console = job.FirstOrDefault(p => p.Id == shell).Console;
        return console != 0 && job.Any(p => p.Id != shell && p.Console == console);
    }

    /// <summary>
    /// The status a shell pane moves to after a reading, or null to leave it: a pane that has completed or
    /// failed keeps that, a reading that could not be taken (<paramref name="busy"/> null) changes nothing,
    /// and only a different status is reported.
    /// </summary>
    public static string? NextStatus(string current, bool? busy)
    {
        if (busy is not { } running || current is "completed" or "error") return null;
        var next = running ? "running" : "idle";
        return next == current ? null : next;
    }
}

/// <summary>
/// Reads, from Windows, what <see cref="ShellActivity"/> decides on (M/MightyCore/ShellProcessProbe.swift): the
/// processes in a terminal's job object and the console each is attached to. The console is the kernel's own record
/// of it (<c>ProcessConsoleHostProcess</c>, which System Informer reads too): the console host's id for a process on
/// a console, something else for one without, so it tells a process on the terminal from one in a window of its own,
/// a GUI program or a detached one, which an image's subsystem cannot. Every read fails safe: null means "not known
/// now", never "idle".
/// </summary>
public static class ShellProcessProbe
{
    /// <summary>Whether the terminal's shell runs a command now; null when its job or its console could not be read.</summary>
    public static bool? IsBusy(PseudoTerminal terminal)
    {
        if (terminal.JobProcessIds() is not { } ids || Console(terminal.ProcessId) is not { } shell) return null;
        return ShellActivity.IsBusy(terminal.ProcessId, [new(terminal.ProcessId, shell), .. ids.Where(id => id != terminal.ProcessId).Select(id => new ShellActivity.Entry(id, Console(id) ?? 0))]);
    }

    /// <summary>The console a process is attached to, as the kernel records it; null when it is gone or may not be asked.</summary>
    public static long? Console(int processId)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var process = Native.OpenProcess(Native.QueryLimitedInformation, false, (uint)processId);
        if (process == 0) return null;
        try { return Native.NtQueryInformationProcess(process, Native.ProcessConsoleHostProcess, out var value, (uint)nint.Size, out _) == 0 && value != 0 ? value : null; }
        finally { ChildProcess.Native.CloseHandle(process); }
    }

    private static class Native
    {
        /// <summary>PROCESS_QUERY_LIMITED_INFORMATION, and the ProcessConsoleHostProcess information class.</summary>
        internal const uint QueryLimitedInformation = 0x1000;
        internal const int ProcessConsoleHostProcess = 49;
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);
        [DllImport("ntdll.dll")] internal static extern int NtQueryInformationProcess(nint process, int informationClass, out nint value, uint length, out uint returned);
    }
}
