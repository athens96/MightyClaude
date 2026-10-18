namespace MightyClaude.Core;

/// <summary>
/// A remote command pane's text on a Windows host: Windows PowerShell from the system folder Windows reports (never a PATH or
/// current-folder lookup), without the user's profile and non-interactive (a command that prompts, or Get-Credential, fails at once
/// instead of waiting on the host's desktop), one process per command in the shared folder. The Mac host runs the same text in its
/// login shell with no input (M/ProcessRunner.swift start, shell kind).
/// </summary>
public static class RemoteCommandShell
{
    /// <summary>The program and its arguments; the text is the one argument after <c>-Command</c>, so it can never add another.</summary>
    /// <exception cref="ArgumentException"><paramref name="systemFolder"/> is not a full drive path (an empty, relative or drive-relative
    /// one would resolve against the current folder).</exception>
    public static (string Binary, IReadOnlyList<string> Arguments) Launch(string command, string systemFolder)
    {
        if (systemFolder is not { Length: >= 3 } || !char.IsAsciiLetter(systemFolder[0]) || systemFolder[1] != ':' || systemFolder[2] is not ('\\' or '/')
            || systemFolder.IndexOfAny(['"', '\0']) >= 0 || systemFolder.Split('\\', '/').Any(part => part is "." or ".."))
            throw new ArgumentException("The system folder must be a full path.", nameof(systemFolder));
        return (systemFolder.TrimEnd('\\', '/') + @"\WindowsPowerShell\v1.0\powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", command]);
    }

    /// <summary>The command line CreateProcess receives (ChildProcess quotes each argument the same way).</summary>
    public static string CommandLine(string command, string systemFolder)
    {
        var (binary, arguments) = Launch(command, systemFolder);
        return string.Join(" ", new[] { binary }.Concat(arguments).Select(ChildProcess.QuoteWindows));
    }

    /// <summary>Whether the command line fits CreateProcess's limit (32,767 characters with its terminating NUL); a host refuses one that does not.</summary>
    public static bool Fits(string command, string systemFolder) => CommandLine(command, systemFolder).Length < 32767;
}
