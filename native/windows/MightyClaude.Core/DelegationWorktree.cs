using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace MightyClaude.Core;

/// <summary>
/// A delegated child's own checkout (M/DelegationWorktree.swift:3-29): the flat branch <c>mighty/&lt;session id&gt;</c> at its
/// parent's head, in a git worktree under the worktree root, outside the workspace checkout. TASK.md and REPORT.md sit in
/// <see cref="NotesFolder"/> inside it. That folder ignores itself, so neither file is ever committed or counts as a change,
/// whatever line endings git is set to write.
/// </summary>
/// <param name="WorkingFolder">Where the child works: the worktree, or the same subfolder of it when the workspace is a subfolder of its repository.</param>
/// <param name="ParentBranch">The parent checkout's branch (short name); <paramref name="BaseCommit"/> is its head when the child was made.</param>
public sealed record ChildWorktree(string SessionId, string WorktreePath, string WorkingFolder, string Branch, string ParentBranch, string BaseCommit)
{
    public const string NotesFolder = ".mighty-delegation";
    public string TaskFile => TaskFileIn(WorktreePath);
    public string ReportFile => ReportFileIn(WorktreePath);
    public static string TaskFileIn(string worktreePath) => Path.Combine(worktreePath, NotesFolder, "TASK.md");
    public static string ReportFileIn(string worktreePath) => Path.Combine(worktreePath, NotesFolder, "REPORT.md");
}

/// <summary>The parent side of a child about to be made, read from the workspace (M/DelegationWorktree.swift:31-39).</summary>
/// <param name="Repository">The top folder of the workspace's checkout.</param>
/// <param name="Prefix">The workspace's folder inside it, as git names it: "" or "sub/folder/".</param>
public sealed record ChildWorktreeBase(string Repository, string Prefix, string ParentBranch, string BaseCommit);

public abstract record ChildWorktreeCheck
{
    public sealed record Ready(ChildWorktreeBase Base) : ChildWorktreeCheck;
    /// <summary>Refused before anything was made.</summary>
    public sealed record Refused(DelegationReasonCode Reason) : ChildWorktreeCheck;

    /// <summary>
    /// Whether the folder is a checkout that can host a child worktree: a repository with a branch checked out that has a
    /// commit (M/DelegationWorktree.swift:46-55). Free disk changes from minute to minute, so a low disk still counts as one;
    /// the child's own check refuses it then.
    /// </summary>
    public bool CanHostWorktree => this is Ready or Refused { Reason: DelegationReasonCode.LowDisk };
}

public abstract record ChildWorktreeOutcome
{
    public sealed record Created(ChildWorktree Worktree) : ChildWorktreeOutcome;
    /// <summary>Refused before anything was made.</summary>
    public sealed record Refused(DelegationReasonCode Reason) : ChildWorktreeOutcome;
    /// <summary>Git or the file system failed partway. Whatever was left (a branch, a folder) stays until a human discards the child; the app never removes it on its own.</summary>
    public sealed record Failed(string Message) : ChildWorktreeOutcome;
}

/// <summary>
/// Makes child worktrees under one root (M/DelegationWorktree.swift:57-146): <c>%USERPROFILE%\.mightyclaude\worktrees</c> in the
/// app, a temp folder in tests. A workspace that is not in a git checkout, whose branch has no commit yet, whose HEAD is
/// detached, or whose root volume has under 10 GB free is refused before anything is made.
/// </summary>
/// <param name="freeBytes">Reads the free space of the volume holding a path; null (unknown) counts as too little.</param>
public sealed class ChildWorktreeMaker(string? root = null, Func<string, long?>? freeBytes = null)
{
    public const long MinimumFreeBytes = 10_000_000_000;
    public static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".mightyclaude", "worktrees");

    public string Root { get; } = DelegationPaths.Standardized(root ?? DefaultRoot);
    private readonly Func<string, long?> freeBytes = freeBytes ?? AvailableBytes;

    public string WorktreePath(string sessionId) => Path.Combine(Root, sessionId);

    /// <summary>Checks the workspace and the free space, and makes nothing. Quick, so a delegate call can answer with its refusal.</summary>
    public async Task<ChildWorktreeCheck> Check(string workspace)
    {
        static ChildWorktreeCheck Refused(DelegationReasonCode reason) => new ChildWorktreeCheck.Refused(reason);
        if (!Path.IsPathFullyQualified(workspace) || workspace.Contains('\0')
            || await DelegationGit.Run(["rev-parse", "--is-inside-work-tree", "--show-toplevel", "--show-prefix"], workspace) is not { ExitCode: 0 } place) return Refused(DelegationReasonCode.NotGit);
        var lines = place.Stdout.Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
        if (lines.Length < 2 || lines[0] != "true" || !Path.IsPathFullyQualified(lines[1])) return Refused(DelegationReasonCode.NotGit);
        if (await DelegationGit.Run(["symbolic-ref", "-q", "HEAD"], workspace) is not { } head) return Refused(DelegationReasonCode.NotGit);
        var reference = DelegationGit.Line(head.Stdout);
        if (head.ExitCode != 0 || !reference.StartsWith("refs/heads/", StringComparison.Ordinal) || reference.Length <= 11)
            return Refused(head.ExitCode is 0 or 1 ? DelegationReasonCode.DetachedHead : DelegationReasonCode.NotGit);
        if (await DelegationGit.Run(["rev-parse", "-q", "--verify", "HEAD^{commit}"], workspace) is not { } commit) return Refused(DelegationReasonCode.NotGit);
        var sha = DelegationGit.Line(commit.Stdout);
        if (commit.ExitCode != 0 || !DelegationGit.IsObjectName(sha)) return Refused(DelegationReasonCode.UnbornBranch);
        if (freeBytes(Root) is not { } free || free < MinimumFreeBytes) return Refused(DelegationReasonCode.LowDisk);
        // Git names the folder with forward slashes; the rest of the app reads it the Windows way.
        return new ChildWorktreeCheck.Ready(new(DelegationPaths.Standardized(lines[1]), lines.Length > 2 ? lines[2] : "", reference[11..], sha));
    }

    /// <summary>
    /// Makes the branch <c>mighty/&lt;session id&gt;</c> at the recorded base commit and its worktree, then TASK.md with
    /// <paramref name="task"/> and an empty REPORT.md. Never replaces an existing branch, folder or file.
    /// </summary>
    public async Task<ChildWorktreeOutcome> Make(string sessionId, ChildWorktreeBase start, string task, double timeout = 300)
    {
        static ChildWorktreeOutcome Failed(string message) => new ChildWorktreeOutcome.Failed(message);
        if (!IsSafeSessionId(sessionId)) return Failed("The session id cannot name a branch and a folder.");
        var worktree = WorktreePath(sessionId); var branch = ChildRecord.BranchName(sessionId);
        try { Directory.CreateDirectory(Root); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) { return Failed(ex.Message); }
        if (await DelegationGit.Run(["worktree", "add", "-b", branch, worktree, start.BaseCommit], start.Repository, timeout) is not { } added) return Failed("Git did not finish.");
        if (added.ExitCode != 0) return Failed(DelegationGit.Message(added));
        var notes = Path.Combine(worktree, ChildWorktree.NotesFolder);
        try
        {
            if (Directory.Exists(notes) || File.Exists(notes)) throw new IOException(notes);
            Directory.CreateDirectory(notes);
            // Written byte for byte, never through git: the notes ignore themselves, so no line-ending setting touches them.
            foreach (var (path, bytes) in new[] { (Path.Combine(notes, ".gitignore"), "*\n"u8.ToArray()), (ChildWorktree.TaskFileIn(worktree), Encoding.UTF8.GetBytes(task)), (ChildWorktree.ReportFileIn(worktree), Array.Empty<byte>()) })
            {
                using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read); file.Write(bytes);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) { return Failed(ex.Message); }
        var folder = start.Prefix.Length == 0 ? worktree : DelegationPaths.Standardized(Path.Combine(worktree, start.Prefix));
        return new ChildWorktreeOutcome.Created(new(sessionId, worktree, folder, branch, start.ParentBranch, start.BaseCommit));
    }

    /// <summary><see cref="Check"/> and then <see cref="Make"/>.</summary>
    public async Task<ChildWorktreeOutcome> Create(string sessionId, string workspace, string task) => await Check(workspace) switch
    {
        ChildWorktreeCheck.Ready ready => await Make(sessionId, ready.Base, task),
        ChildWorktreeCheck.Refused refused => new ChildWorktreeOutcome.Refused(refused.Reason),
        _ => throw new InvalidOperationException(),
    };

    /// <summary>
    /// Letters, digits, <c>-</c>, <c>_</c> and inner dots, at most 128: one folder name and a valid branch name part. On Windows a
    /// device name (<c>nul</c>, <c>con</c>, <c>com1</c>…) is no folder name either, with or without an extension.
    /// </summary>
    public static bool IsSafeSessionId(string id) =>
        id.Length is > 0 and <= 128 && id[0] is not ('.' or '-') && id[^1] != '.' && !id.Contains("..", StringComparison.Ordinal) && !id.EndsWith(".lock", StringComparison.Ordinal)
        && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_')
        && !(OperatingSystem.IsWindows() && Regex.IsMatch(id.Split('.')[0], "^(con|prn|aux|nul|com[1-9]|lpt[1-9])$", RegexOptions.IgnoreCase));

    /// <summary>Free bytes this user may take on the volume holding <paramref name="path"/> or its nearest existing folder (the Mac reads the volume's available capacity).</summary>
    public static long? AvailableBytes(string path)
    {
        try
        {
            var place = Path.GetFullPath(path);
            while (!Directory.Exists(place))
            {
                var parent = Path.GetDirectoryName(place);
                if (string.IsNullOrEmpty(parent) || parent == place) return null;
                place = parent;
            }
            // The folder's own volume, which a mount point or a UNC share decides, not its drive letter.
            if (OperatingSystem.IsWindows()) return GetDiskFreeSpaceExW(Path.EndsInDirectorySeparator(place) ? place : place + Path.DirectorySeparatorChar, out var available, out _, out _) ? (long)available : null;
            return Path.GetPathRoot(place) is { Length: > 0 } volume ? new DriveInfo(volume).AvailableFreeSpace : null;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or NotSupportedException) { return null; }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceExW(string directory, out ulong available, out ulong total, out ulong free);
}

public sealed record GitResult(int ExitCode, string Stdout, string Stderr);

/// <summary>
/// Git for delegation (M/DelegationWorktree.swift:148-172): the user's own settings, line endings among them, but no hooks,
/// fsmonitor, auto gc, prompts or inherited GIT_* variables. Every command of a child's life runs through here with the same
/// settings, so a checkout this wrote reads as unchanged to the status this reads. On Windows it also takes paths past 260
/// characters: a child's worktree sits deeper than its parent's checkout may.
/// </summary>
public static class DelegationGit
{
    public const int MaximumBytes = 1_048_576;
    private static string? found;

    /// <summary>git on the PATH, as <see cref="WorkspaceGitInfo.ReadAsync"/> finds it; null when there is none. A relative PATH entry names no place, so the folder a command runs in never decides.</summary>
    public static string? Executable => found ??= (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Select(folder => folder.Trim().Trim('"'))
        .Where(Path.IsPathFullyQualified).Select(folder => Path.Combine(folder, OperatingSystem.IsWindows() ? "git.exe" : "git")).FirstOrDefault(File.Exists);

    /// <summary>
    /// The hooks folder git is pointed at: an absolute folder under the profile (<see cref="UseProfile"/>), never the install
    /// folder, so no repository's hooks run. Windows has no <c>/dev/null</c> folder, and git reads a device name such as
    /// <c>NUL</c> as a relative path. It is checked empty before each run (<see cref="HooksEmpty"/>).
    /// </summary>
    public static string NoHooks { get; private set; } = OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MightyClaudeNative", "git-no-hooks") : "/dev/null";

    /// <summary>Points <see cref="NoHooks"/> into the profile at <paramref name="directory"/>.</summary>
    public static void UseProfile(string directory) { if (OperatingSystem.IsWindows()) NoHooks = Path.Combine(directory, "git-no-hooks"); }

    /// <summary>Whether <see cref="NoHooks"/> is a plain, empty folder, made when it is missing. Git does not run while it is not.</summary>
    public static bool HooksEmpty()
    {
        if (!OperatingSystem.IsWindows()) return true;
        try { var folder = Directory.CreateDirectory(NoHooks); return !folder.Attributes.HasFlag(FileAttributes.ReparsePoint) && !folder.EnumerateFileSystemInfos().Any(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }

    /// <summary>
    /// null when git is missing, could not start, wrote more than 1 MiB or did not finish in time, or the hooks folder is not
    /// empty. <paramref name="input"/> is written to git's standard input before it is closed.
    /// </summary>
    public static async Task<GitResult?> Run(IReadOnlyList<string> arguments, string directory, double timeout = 10, string? input = null)
    {
        if (Executable is not { } executable || !HooksEmpty()) return null;
        try
        {
            string[] settings = ["-c", "core.fsmonitor=false", "-c", "core.hooksPath=" + NoHooks, "-c", "gc.auto=0", .. OperatingSystem.IsWindows() ? new[] { "-c", "core.longpaths=true" } : [], "-C", directory];
            var info = ChildProcess.StartInfo(executable, [.. settings, .. arguments], directory);
            foreach (var key in info.Environment.Keys.Where(key => key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray()) info.Environment.Remove(key);
            info.Environment["GIT_OPTIONAL_LOCKS"] = "0"; info.Environment["GIT_TERMINAL_PROMPT"] = "0"; info.Environment["LC_ALL"] = "C";
            await using var process = ChildProcess.Start(info);
            if (input is not null) { await process.Input.WriteAsync(input).ConfigureAwait(false); await process.Input.FlushAsync().ConfigureAwait(false); }
            process.Input.Close();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(0.01, timeout)));
            // A process git started can keep its output open after git exits: the deadline ends the whole job, so the reads end too.
            using var kill = deadline.Token.Register(process.Kill);
            var total = new int[1]; var over = false;
            async Task<string> Read(StreamReader reader)
            {
                var text = new StringBuilder(); var buffer = new char[8192]; int count;
                while ((count = await reader.ReadAsync(buffer.AsMemory(), deadline.Token).ConfigureAwait(false)) > 0)
                {
                    if (Interlocked.Add(ref total[0], Encoding.UTF8.GetByteCount(buffer, 0, count)) > MaximumBytes) { over = true; process.Kill(); break; }
                    text.Append(buffer, 0, count);
                }
                return text.ToString();
            }
            var stdout = Read(process.Output); var stderr = Read(process.Error);
            try
            {
                var exit = await process.Completion.WaitAsync(deadline.Token).ConfigureAwait(false);
                await Task.WhenAll(stdout, stderr).WaitAsync(deadline.Token).ConfigureAwait(false);
                return over ? null : new(exit, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
            }
            finally
            {
                process.Kill(); deadline.Cancel();
                try { await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); } catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException or TimeoutException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException or Win32Exception or TimeoutException or InvalidOperationException) { return null; }
    }

    public static string Line(string output) => output.Trim();

    public static bool IsObjectName(string value) => value.Length is 40 or 64 && value.All(char.IsAsciiHexDigit);

    public static string Message(GitResult result)
    {
        var text = Line(result.Stderr);
        return text.Length == 0 ? $"Git exited with {result.ExitCode}." : DelegationNames.Prefix(text, 500);
    }
}

/// <summary>
/// Folder paths as delegation compares them on Windows: full, with one kind of separator and no closing one, and without regard
/// to case. Where the Mac resolves a path with realpath, <see cref="Canonical"/> asks Windows for the folder's final name, the
/// way git lists worktrees: short 8.3 names are spelled out and junctions and links followed, as far as the path exists.
/// </summary>
public static class DelegationPaths
{
    public static readonly StringComparison Comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    public static readonly StringComparer Comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>The full path with the platform's separators and no closing one; as it is when it names no path.</summary>
    public static string Standardized(string path)
    {
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { return path; }
    }

    /// <summary><paramref name="path"/> with its links resolved as far as it exists (M/DelegationCleanup.swift:176-189).</summary>
    public static string Canonical(string path)
    {
        var standard = Standardized(path); var existing = standard; var missing = new Stack<string>();
        while (true)
        {
            if (FinalName(existing) is { } resolved) return missing.Aggregate(resolved, Path.Combine);
            var parent = Path.GetDirectoryName(existing);
            if (string.IsNullOrEmpty(parent) || parent == existing) return standard;
            missing.Push(Path.GetFileName(existing)); existing = parent;
        }
    }

    public static bool Same(string first, string second) => string.Equals(first, second, Comparison);

    /// <summary>Whether <paramref name="path"/> lies inside <paramref name="folder"/>, both already standardized or canonical.</summary>
    public static bool IsInside(string path, string folder) =>
        path.StartsWith(Path.EndsInDirectorySeparator(folder) ? folder : folder + Path.DirectorySeparatorChar, Comparison);

    private static string? FinalName(string path)
    {
        if (!OperatingSystem.IsWindows()) return Directory.Exists(path) || File.Exists(path) ? path : null;
        var name = path.Length < 248 || path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path : path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
        // No access asked for, every sharing allowed, and backup semantics so that a folder opens.
        using var handle = CreateFileW(name, 0, 7, 0, 3, 0x02000000, 0);
        if (handle.IsInvalid) return null;
        var buffer = new char[1024]; var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
        if (length > buffer.Length) { buffer = new char[length]; length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0); }
        if (length == 0 || length > buffer.Length) return null;
        var final = new string(buffer, 0, (int)length);
        return final.StartsWith(@"\\?\UNC\", StringComparison.Ordinal) ? @"\\" + final[8..] : final.StartsWith(@"\\?\", StringComparison.Ordinal) ? final[4..] : final;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, [Out] char[] buffer, uint size, uint flags);
}
