using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace MightyClaude.Core;

/// <summary>Local Git status only; never contacts remotes, runs hooks or refreshes the index.</summary>
public sealed record WorkspaceGitInfo(string Branch, string? Revision, bool IsDirty, int? Ahead, int? Behind)
{
    public string Label => Branch == "(detached)" ? "HEAD · " + (Revision is { Length: >= 7 } ? Revision[..7] : "—") : Branch;
    public string Badge => "⑂ " + Label + (IsDirty ? " ●" : "") + (Ahead is > 0 ? " ↑" + Ahead : "") + (Behind is > 0 ? " ↓" + Behind : "");

    public static WorkspaceGitInfo? Parse(string output)
    {
        string? branch = null, revision = null; int? ahead = null, behind = null; var dirty = false;
        foreach (var line in output.Split('\n'))
        {
            var clean = line.TrimEnd('\r');
            if (clean.StartsWith("# branch.head ", StringComparison.Ordinal)) branch = clean[14..];
            else if (clean.StartsWith("# branch.oid ", StringComparison.Ordinal) && Regex.IsMatch(clean[13..], "^[a-fA-F0-9]{7,64}$")) revision = clean[13..];
            else if (clean.StartsWith("# branch.ab ", StringComparison.Ordinal))
            {
                var match = Regex.Match(clean, @"^# branch.ab \+(\d+) -(\d+)$");
                if (match.Success && int.TryParse(match.Groups[1].Value, out var a) && int.TryParse(match.Groups[2].Value, out var b)) { ahead = a; behind = b; }
            }
            else if (new[] { "1 ", "2 ", "u ", "? " }.Any(prefix => clean.StartsWith(prefix, StringComparison.Ordinal))) dirty = true;
        }
        return string.IsNullOrEmpty(branch) || Encoding.UTF8.GetByteCount(branch) > 4096 || branch.Any(char.IsControl)
            ? null : new(branch, revision, dirty, ahead, behind);
    }

    public static ProcessStartInfo Command(string binary, string path)
    {
        var info = ChildProcess.StartInfo(binary, ["--no-optional-locks", "-c", "core.fsmonitor=false", "-c", "core.hooksPath=" + (OperatingSystem.IsWindows() ? "NUL" : "/dev/null"), "-c", "core.untrackedCache=false", "-c", "gc.auto=0", "-C", path, "status", "--porcelain=v2", "--branch", "--untracked-files=normal"], path);
        foreach (var key in info.Environment.Keys.Where(key => key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray()) info.Environment.Remove(key);
        info.Environment["GIT_OPTIONAL_LOCKS"] = "0"; info.Environment["GIT_TERMINAL_PROMPT"] = "0"; info.Environment["LC_ALL"] = "C";
        return info;
    }

    public static async Task<WorkspaceGitInfo?> ReadAsync(string path, CancellationToken cancellation = default)
    {
        if (!Path.IsPathFullyQualified(path) || !Directory.Exists(path)) return null;
        var name = OperatingSystem.IsWindows() ? "git.exe" : "git";
        var binary = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => Path.Combine(p.Trim('"'), name)).FirstOrDefault(File.Exists);
        if (binary is null) return null;
        try
        {
            await using var process = ChildProcess.Start(Command(binary, path)); process.Input.Close();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromSeconds(2));
            var stdout = ReadBounded(process.Output, timeout.Token); var stderr = ReadBounded(process.Error, timeout.Token);
            try
            {
                var exit = await process.Completion.WaitAsync(timeout.Token);
                var output = await stdout; await stderr;
                return exit == 0 ? Parse(output) : null;
            }
            finally
            {
                process.Kill(); timeout.Cancel();
                try { await Task.WhenAll(stdout, stderr); } catch (OperationCanceledException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or System.ComponentModel.Win32Exception) { return null; }
    }

    private static async Task<string> ReadBounded(TextReader reader, CancellationToken token)
    {
        var result = new StringBuilder(); var buffer = new char[4096];
        while (await reader.ReadAsync(buffer.AsMemory(), token) is var count && count > 0)
            if (result.Length < 262_144) result.Append(buffer, 0, Math.Min(count, 262_144 - result.Length));
        return result.ToString();
    }
}
