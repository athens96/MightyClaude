using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace MightyClaude.Core;

/// <summary>One step of a clone: <c>starting</c>, <c>receiving</c>, <c>resolving</c> or <c>checkout</c>, git's percent, and the whole clone's share done (0–1).</summary>
public sealed record CloneProgress(string Phase, int Percent, double Fraction);

/// <summary>
/// The rules for adding a workspace from a new folder or a Git address (docs/superpowers/specs/2026-10-26-workspace-create-design.md),
/// ported line for line from macOS MightyCore WorkspaceCreate.swift and held to native/contracts/fixtures/workspace-create.json.
/// Name problems and Git error kinds are the fixture's camelCase names: <c>empty</c>, <c>badCharacter</c>, <c>reserved</c>,
/// <c>trailingDotOrSpace</c>, <c>dots</c>, <c>tooLong</c>; <c>gitMissing</c>, <c>invalidURL</c>, <c>auth</c>, <c>notFound</c>,
/// <c>network</c>, <c>other</c>.
/// </summary>
public static partial class WorkspaceCreate
{
    private static readonly string[] Schemes = ["https://", "http://", "ssh://", "git://", "file://"];

    [GeneratedRegex(@"^[^/\s@]+@[^/\s:]+:\S+$")] private static partial Regex ScpAddress();
    [GeneratedRegex(@"^[A-Za-z]:[\\/]")] private static partial Regex DrivePath();
    [GeneratedRegex(@"\d{1,3}(?=%)")] private static partial Regex Percent();

    private static string? SchemeOf(string url) => Schemes.FirstOrDefault(scheme => url.StartsWith(scheme, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether <paramref name="raw"/> reads as a Git address: a known scheme with more after it, scp-style <c>user@host:path</c>, or a local or UNC path.</summary>
    public static bool LooksLikeGitUrl(string raw)
    {
        var url = raw.Trim();
        if (url.Length == 0 || url.Any(char.IsWhiteSpace)) return false;
        if (SchemeOf(url) is { } scheme) return url.Length > scheme.Length;
        if (ScpAddress().IsMatch(url)) return true;
        if (url.StartsWith('/') || url.StartsWith(@"\\", StringComparison.Ordinal)) return true;
        return DrivePath().IsMatch(url);
    }

    /// <summary>The folder name an address suggests: its last path piece without <c>.git</c>; null when it is no Git address or names no repository.</summary>
    public static string? RepoName(string raw)
    {
        if (!LooksLikeGitUrl(raw)) return null;
        var url = raw.Trim();
        var cut = url.IndexOfAny(new[] { '?', '#' });
        if (cut >= 0) url = url[..cut];
        while (url.EndsWith('/') || url.EndsWith('\\')) url = url[..^1];
        if (url.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) url = url[..^4];
        var afterScheme = SchemeOf(url) is { } scheme ? url[scheme.Length..] : null;
        var slash = afterScheme?.IndexOf('/') ?? -1;
        var path = afterScheme is null ? url : slash >= 0 ? afterScheme[slash..] : "";
        var separators = afterScheme is null ? new[] { '/', '\\', ':' } : new[] { '/', '\\' };
        var name = path.Split(separators, StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
        return name.Length == 0 ? null : name;
    }

    private static readonly HashSet<string> ReservedNames = ["CON", "PRN", "AUX", "NUL", .. Enumerable.Range(1, 9).Select(n => "COM" + n), .. Enumerable.Range(1, 9).Select(n => "LPT" + n)];

    /// <summary>Why <paramref name="raw"/> cannot name a new folder, or null when it can. <paramref name="windows"/> adds Windows' own rules.</summary>
    public static string? CheckName(string raw, bool windows = true)
    {
        var name = raw.Trim();
        if (name.Length == 0) return "empty";
        if (name is "." or "..") return "dots";
        if (Encoding.UTF8.GetByteCount(name) > 255) return "tooLong";
        if (name.Any(c => c is '/' or '\\' or ':' || char.IsControl(c))) return "badCharacter";
        if (!windows) return null;
        if (name.Any(c => c is '<' or '>' or '"' or '|' or '?' or '*')) return "badCharacter";
        var baseName = name.Split('.', 2)[0].ToUpperInvariant();
        if (ReservedNames.Contains(baseName)) return "reserved";
        if (name.EndsWith('.') || name.EndsWith(' ')) return "trailingDotOrSpace";
        return null;
    }

    private static readonly (string Kind, string[] Needles)[] ErrorTable =
    [
        ("auth", ["authentication failed", "could not read username", "could not read password", "permission denied (publickey", "access denied", "error: 401", "error: 403"]),
        ("notFound", ["not found", "error: 404", "does not appear to be a git repository"]),
        ("network", ["could not resolve host", "could not resolve hostname", "connection refused", "timed out", "network is unreachable", "failed to connect", "connection reset"]),
        ("invalidURL", ["is not supported", "not a valid", "invalid url"]),
    ];

    /// <summary>What git's error text says went wrong: <c>auth</c>, <c>notFound</c>, <c>network</c>, <c>invalidURL</c> or <c>other</c>.</summary>
    public static string ErrorKind(string stderr) =>
        ErrorTable.FirstOrDefault(row => row.Needles.Any(needle => stderr.Contains(needle, StringComparison.OrdinalIgnoreCase))).Kind ?? "other";

    /// <summary>The clone's progress a line of <c>git clone --progress</c> stderr gives, or null for any other line.</summary>
    public static CloneProgress? Progress(string raw)
    {
        var line = raw.Trim();
        CloneProgress? Make(string phase, double start, double span)
        {
            if (Percent().Match(line) is not { Success: true } match) return null;
            var percent = Math.Min(100, int.TryParse(match.Value, out var value) ? value : 0);
            return new(phase, percent, Math.Round(start + span * percent / 100, 3, MidpointRounding.AwayFromZero));
        }
        if (line.StartsWith("Receiving objects:", StringComparison.Ordinal)) return Make("receiving", 0, 0.7);
        if (line.StartsWith("Resolving deltas:", StringComparison.Ordinal)) return Make("resolving", 0.7, 0.2);
        if (line.StartsWith("Updating files:", StringComparison.Ordinal) || line.StartsWith("Checking out files:", StringComparison.Ordinal)) return Make("checkout", 0.9, 0.1);
        if (line.StartsWith("Cloning into", StringComparison.Ordinal) || line.StartsWith("remote:", StringComparison.Ordinal)) return new("starting", 0, 0);
        return null;
    }

    /// <summary>What the create sheets say about a folder name that cannot be used (<see cref="CheckName"/>'s answer).</summary>
    public static string NameMessage(string problem) => problem switch
    {
        "empty" => Locale.Get("workspace.name.error.empty"),
        "badCharacter" => Locale.Get("workspace.name.error.badCharacter"),
        "reserved" => Locale.Get("workspace.name.error.reserved"),
        "trailingDotOrSpace" => Locale.Get("workspace.name.error.trailingDotOrSpace"),
        "dots" => Locale.Get("workspace.name.error.dots"),
        "tooLong" => Locale.Get("workspace.name.error.tooLong"),
        "exists" => Locale.Get("workspace.name.error.exists"),
        _ => throw new ArgumentOutOfRangeException(nameof(problem), problem, null),
    };

    /// <summary>What the Git sheet says about a failed check or clone; <paramref name="detail"/> is git's own text, shown for <c>other</c>.</summary>
    public static string ErrorMessage(string kind, string? detail) => kind switch
    {
        "gitMissing" => Locale.Get("workspace.git.error.gitMissing"),
        "invalidURL" => Locale.Get("workspace.git.error.invalidURL"),
        "auth" => Locale.Get("workspace.git.error.auth"),
        "notFound" => Locale.Get("workspace.git.error.notFound"),
        "network" => Locale.Get("workspace.git.error.network"),
        _ => Locale.Get("workspace.git.error.other", new Dictionary<string, string> { ["message"] = detail ?? "" }),
    };

    /// <summary>The clone's progress line.</summary>
    public static string ProgressMessage(CloneProgress progress)
    {
        var percent = new Dictionary<string, string> { ["percent"] = progress.Percent.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        return progress.Phase switch
        {
            "receiving" => Locale.Get("workspace.git.progress.receiving", percent),
            "resolving" => Locale.Get("workspace.git.progress.resolving", percent),
            "checkout" => Locale.Get("workspace.git.progress.checkout", percent),
            _ => Locale.Get("workspace.git.progress.starting"),
        };
    }

    /// <summary>The new workspace's folder: <paramref name="name"/>, trimmed, in <paramref name="location"/>.</summary>
    public static string Target(string location, string name) => Path.Combine(location, name.Trim());

    /// <summary>Where the create sheets start: the last location used if it is still a folder, else the active workspace's parent folder, else home.</summary>
    public static string DefaultLocation(string? last, string? activeWorkspacePath, string home)
    {
        if (last is not null && Directory.Exists(last)) return last;
        if (activeWorkspacePath is not null && Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(activeWorkspacePath)) is { Length: > 0 } parent && Directory.Exists(parent)) return parent;
        return home;
    }
}

/// <summary>What <see cref="GitTool.MakeFolderAsync"/> did: <c>made</c>, <c>madeButGitInitFailed</c> (the folder is there, git init failed) or <c>failed</c> (no folder), with the reason.</summary>
public sealed record MakeFolderResult(string Outcome, string? Message);

/// <summary>Whether this computer reaches an address; otherwise the error kind (as <see cref="WorkspaceCreate.ErrorKind"/>, or <c>invalidURL</c>/<c>gitMissing</c>) and git's text.</summary>
public sealed record ProbeResult(bool Reachable, string? Kind, string? Message);

/// <summary>
/// Git for making workspaces (macOS MightyCore WorkspaceCreateGit.swift): the user's own git, configuration and credentials,
/// never a prompt or a window.
/// </summary>
public static class GitTool
{
    public static string? Executable => DelegationGit.Executable;

    private const int KeptErrorChars = 65_536;
    private static readonly object SshCommandGate = new();
    private static (bool Known, string? Value) sshCommand;

    /// <summary>
    /// No prompt can appear: a credential helper or ssh-agent answers, or git fails at once. An empty GIT_ASKPASS switches git's
    /// askpass off (it would otherwise fall back to core.askPass and SSH_ASKPASS and could open a window); ChildProcess writes it
    /// into the child's environment block as <c>GIT_ASKPASS=</c>, which git reads as set and empty. GCM_INTERACTIVE=never keeps
    /// Git Credential Manager from opening its sign-in window. BatchMode ssh is used only when the user has no ssh command of
    /// their own (GIT_SSH_COMMAND, GIT_SSH, such as plink, or core.sshCommand).
    /// </summary>
    public static async Task ApplyEnvironmentAsync(ProcessStartInfo info)
    {
        foreach (var key in new[] { "GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE" }) info.Environment.Remove(key);
        info.Environment["GIT_TERMINAL_PROMPT"] = "0"; info.Environment["GCM_INTERACTIVE"] = "never"; info.Environment["LC_ALL"] = "C";
        info.Environment["GIT_ASKPASS"] = ""; info.Environment["SSH_ASKPASS_REQUIRE"] = "never";
        if (!info.Environment.ContainsKey("GIT_SSH_COMMAND") && !info.Environment.ContainsKey("GIT_SSH") && await SshCommandAsync().ConfigureAwait(false) is null)
            info.Environment["GIT_SSH_COMMAND"] = "ssh -o BatchMode=yes";
    }

    /// <summary>
    /// The user's own core.sshCommand. Only a lookup that finished (set, or exit 1: not set) is kept for the app's run; one that
    /// timed out or failed is tried again on the next probe or clone, so a slow first start of git never overrides the user's ssh.
    /// </summary>
    private static async Task<string?> SshCommandAsync()
    {
        lock (SshCommandGate) { if (sshCommand.Known) return sshCommand.Value; }
        var (finished, value) = await ReadSshCommandAsync().ConfigureAwait(false);
        if (finished) { lock (SshCommandGate) { sshCommand = (true, value); } }
        return value;
    }

    /// <summary>
    /// Reads core.sshCommand (system or global configuration; run from home, so no workspace's repository is read):
    /// <c>Finished</c> is false when git could not run, timed out or gave an answer that is neither a value nor "not set".
    /// </summary>
    private static async Task<(bool Finished, string? Value)> ReadSshCommandAsync()
    {
        if (Executable is not { } executable) return (false, null);
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var info = ChildProcess.StartInfo(executable, ["config", "--get", "core.sshCommand"], Directory.Exists(home) ? home : Path.GetTempPath());
            foreach (var key in new[] { "GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE" }) info.Environment.Remove(key);
            info.Environment["GIT_TERMINAL_PROMPT"] = "0"; info.Environment["LC_ALL"] = "C";
            var process = ChildProcess.Start(info);
            try
            {
                try { process.Input.Close(); } catch (IOException) { }
                var output = process.Output.ReadToEndAsync(); _ = Drain(process.Error);
                var exit = await process.Completion.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                var value = DelegationGit.Line(await output.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false));
                if (exit == 0 && value.Length > 0) return (true, value);
                return (exit == 1, null);
            }
            finally { await DisposeQuietly(process).ConfigureAwait(false); }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return (false, null); }
    }

    /// <summary>
    /// Makes the folder at <paramref name="path"/>, which must not exist yet while its parent does, and runs <c>git init</c> in it
    /// when <paramref name="gitInit"/> is set.
    /// </summary>
    public static async Task<MakeFolderResult> MakeFolderAsync(string path, bool gitInit)
    {
        if (File.Exists(path) || Directory.Exists(path)) return new("failed", Locale.Get("workspace.name.error.exists"));
        if (Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path)) is not { Length: > 0 } parent || !Directory.Exists(parent)) return new("failed", Locale.Get("workspace.location.error.missing"));
        try { Directory.CreateDirectory(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return new("failed", ex.Message); }
        if (!gitInit) return new("made", null);
        if (Executable is not { } executable) return new("madeButGitInitFailed", Locale.Get("workspace.git.error.gitMissing"));
        try
        {
            var result = await RunAsync(executable, ["init", "-q"], path, 20, CancellationToken.None).ConfigureAwait(false);
            return result.ExitCode == 0 ? new("made", null) : new("madeButGitInitFailed", DelegationGit.Message(result));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return new("madeButGitInitFailed", ex.Message); }
    }

    /// <summary>
    /// Whether this computer's own credentials reach <paramref name="raw"/>: <c>git ls-remote -- &lt;url&gt; HEAD</c> (one line, so a
    /// repository with many branches is no slower), at most 20 seconds.
    /// Throws <see cref="OperationCanceledException"/> when <paramref name="token"/> is cancelled; git is stopped then.
    /// </summary>
    public static async Task<ProbeResult> ProbeAsync(string raw, CancellationToken token = default)
    {
        var url = raw.Trim();
        if (!WorkspaceCreate.LooksLikeGitUrl(url)) return new(false, "invalidURL", null);
        if (Executable is not { } executable) return new(false, "gitMissing", null);
        try
        {
            var result = await RunAsync(executable, ["ls-remote", "--", url, "HEAD"], Path.GetTempPath(), 20, token).ConfigureAwait(false);
            return result.ExitCode == 0 ? new(true, null, null) : new(false, WorkspaceCreate.ErrorKind(result.Stderr), DelegationGit.Message(result));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (TimeoutException ex) { return new(false, "network", ex.Message); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return new(false, "other", ex.Message); }
    }

    /// <summary>
    /// Runs git with <see cref="ApplyEnvironmentAsync"/>: standard input closed, standard output read and dropped, the last
    /// 64 KiB of standard error kept, both read while git runs so a long ref list never blocks it. Past <paramref name="seconds"/>
    /// git and every process it started are killed and <see cref="TimeoutException"/> is thrown.
    /// </summary>
    private static async Task<GitResult> RunAsync(string executable, IReadOnlyList<string> arguments, string directory, double seconds, CancellationToken token)
    {
        var info = ChildProcess.StartInfo(executable, arguments, directory);
        await ApplyEnvironmentAsync(info).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        var process = ChildProcess.Start(info);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(seconds));
            using var kill = deadline.Token.Register(() => process.Kill());
            try { process.Input.Close(); } catch (IOException) { }
            var output = Drain(process.Output); var errors = Tail(process.Error);
            int exit;
            try { exit = await process.Completion.WaitAsync(deadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                token.ThrowIfCancellationRequested();
                throw new TimeoutException(Locale.Get("process.error.timeout"));
            }
            // A process git started can keep the pipes open after git exits: ending the job ends the reads too.
            process.Kill();
            try { await Task.WhenAll(output, errors).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); } catch (TimeoutException) { }
            return new(exit, "", errors.IsCompletedSuccessfully ? errors.Result : "");
        }
        finally { await DisposeQuietly(process).ConfigureAwait(false); }
    }

    internal static async Task Drain(StreamReader reader)
    {
        var buffer = new char[8192];
        try { while (await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false) > 0) { } }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    private static async Task<string> Tail(StreamReader reader)
    {
        var text = new StringBuilder(); var buffer = new char[8192];
        try
        {
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
            {
                text.Append(buffer, 0, count);
                if (text.Length > KeptErrorChars) text.Remove(0, text.Length - KeptErrorChars);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        return text.ToString();
    }

    /// <summary>Ends the child's job and closes its handles; a descendant slow to end does not make it fail.</summary>
    internal static async Task DisposeQuietly(ChildProcess process)
    {
        try { await process.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) when (ex is TimeoutException or IOException or Win32Exception or InvalidOperationException or ObjectDisposedException) { }
    }

    /// <summary>
    /// Removes a folder a cancelled or failed clone left. Git marks its pack and object files read-only, which Windows does not
    /// delete, and Windows may still hold a handle for a moment after git's processes end: five tries, 200 ms apart. Links inside
    /// are removed, never followed.
    /// </summary>
    internal static async Task DeleteFolderAsync(string path)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (!Directory.Exists(path)) return;
                foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                    if (entry.Attributes.HasFlag(FileAttributes.ReadOnly)) entry.Attributes &= ~FileAttributes.ReadOnly;
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            await Task.Delay(200).ConfigureAwait(false);
        }
    }
}

/// <summary>What a <see cref="GitCloneJob"/> ended with: <c>done</c>, <c>cancelled</c> or <c>failed</c> with an error kind and git's last line.</summary>
public sealed record CloneResult(string Outcome, string? Kind, string? Message);

/// <summary>
/// <c>git clone --progress</c> into a folder that must not exist yet (macOS MightyCore GitCloneJob): progress from its stderr,
/// <see cref="Cancel"/> stops it with every process it started (the job object), and a cancelled or failed clone leaves no folder.
/// </summary>
public sealed class GitCloneJob
{
    private readonly string url, target;
    private readonly Action<CloneProgress> progress;
    private readonly object gate = new();
    private ChildProcess? child;
    private bool cancelled;

    /// <summary><paramref name="progress"/> is called on a reader thread, not the UI thread: a UI must dispatch to its own thread.</summary>
    public GitCloneJob(string url, string target, Action<CloneProgress> progress)
    {
        this.url = url.Trim(); this.target = target; this.progress = progress;
    }

    /// <summary>Stops the clone, or keeps it from starting; <see cref="RunAsync"/> then removes what it wrote and gives <c>cancelled</c>.</summary>
    public void Cancel()
    {
        // Killed under the lock: RunAsync forgets the child under it before closing the child's handles.
        lock (gate) { cancelled = true; child?.Kill(); }
    }

    private bool IsCancelled { get { lock (gate) return cancelled; } }

    private static CloneResult Failed(string kind, string? message) => new("failed", kind, message);

    public async Task<CloneResult> RunAsync()
    {
        if (IsCancelled) return new("cancelled", null, null);
        if (!WorkspaceCreate.LooksLikeGitUrl(url)) return Failed("invalidURL", null);
        if (GitTool.Executable is not { } executable) return Failed("gitMissing", null);
        // Refused before anything is touched, so a folder that was there already is never removed below.
        if (File.Exists(target) || Directory.Exists(target)) return Failed("other", Locale.Get("workspace.name.error.exists"));
        if (Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(target)) is not { Length: > 0 } parent || !Directory.Exists(parent)) return Failed("other", Locale.Get("workspace.location.error.missing"));
        var info = ChildProcess.StartInfo(executable, ["clone", "--progress", "--", url, target], parent);
        await GitTool.ApplyEnvironmentAsync(info).ConfigureAwait(false);
        ChildProcess process;
        // Git never ran, so there is nothing of its to remove.
        try { process = ChildProcess.Start(info); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return Failed("other", ex.Message); }
        lock (gate) { child = process; if (cancelled) process.Kill(); }
        var exit = -1; var text = "";
        try
        {
            try { process.Input.Close(); } catch (IOException) { }
            var output = GitTool.Drain(process.Output); var errors = ReadErrors(process.Error);
            try { exit = await process.Completion.ConfigureAwait(false); }
            catch (Win32Exception) { exit = -1; }
            process.Kill();
            try { await Task.WhenAll(output, errors).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch (TimeoutException) { }
            if (errors.IsCompletedSuccessfully) text = errors.Result;
        }
        finally
        {
            lock (gate) child = null;
            await GitTool.DisposeQuietly(process).ConfigureAwait(false);
        }
        if (exit == 0 && !IsCancelled) return new("done", null, null);
        await GitTool.DeleteFolderAsync(target).ConfigureAwait(false);
        if (IsCancelled) return new("cancelled", null, null);
        var cut = text.LastIndexOf('\n');
        return Failed(WorkspaceCreate.ErrorKind(text), cut < 0 ? text : text[(cut + 1)..]);
    }

    /// <summary>
    /// git's stderr split on <c>\r</c> and <c>\n</c> (git redraws its progress line with <c>\r</c>), each line's progress passed on;
    /// the last 64 KiB of text kept, trimmed.
    /// </summary>
    private async Task<string> ReadErrors(StreamReader reader)
    {
        var line = new StringBuilder(); var all = new StringBuilder(); var buffer = new char[4096];
        void EndLine()
        {
            if (line.Length == 0) return;
            var text = line.ToString(); line.Clear();
            all.Append(text).Append('\n');
            if (all.Length > 65_536) all.Remove(0, all.Length - 32_768);
            if (WorkspaceCreate.Progress(text) is not { } step) return;
            // A failing callback must not stop the reads: git would block on a full pipe.
            try { progress(step); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
        }
        try
        {
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
                for (var index = 0; index < count; index++)
                {
                    var c = buffer[index];
                    if (c is '\r' or '\n') EndLine(); else line.Append(c);
                }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        return (all.ToString() + line).Trim();
    }
}
