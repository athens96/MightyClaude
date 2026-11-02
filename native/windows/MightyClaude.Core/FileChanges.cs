using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MightyClaude.Core;

/// <summary>
/// What a request changed in one file (the source preview's 변경사항 view, the phone's <c>changes</c>, the web shell's
/// <c>referenceChanges</c>): <c>Base</c> is <c>run</c> (the file as it was before the run first changed it) or <c>head</c>
/// (git's HEAD, an untracked file read as empty). <c>RunId</c> names the run of a <c>run</c> base, <c>HeadSha</c> the short
/// commit of a <c>head</c> base (null for a repository with no commit yet), <c>Created</c> a file the run made or git does not know.
/// </summary>
public sealed record FileChangeSet(string Base, IReadOnlyList<FileDiffHunk> Hunks, int Added, int Removed, bool Truncated, bool Unchanged)
{
    public string? RunId { get; init; }
    public string? HeadSha { get; init; }
    public bool? Created { get; init; }
}

/// <summary>A file's content before a run first changed it; <c>Created</c> when the run made it (the content is then "").</summary>
public sealed record FileOriginal(string RunId, string Path, string Text, bool Created);

/// <summary>
/// The contents files had before a Claude run first changed them (M/MightyCore/FileChanges.swift FileOriginalStore), from the
/// <c>tool_use_result</c> of the run's Edit, Write and MultiEdit results. Kept apart from the state file, under
/// <c>&lt;data&gt;/file-originals/&lt;sha(session)&gt;/&lt;sha(run)&gt;/</c>: <c>run.json</c> (<see cref="Manifest"/>) and one
/// <c>&lt;n&gt;.txt</c> per file (UTF-8), each written to a temporary name and moved over. A path is the workspace-relative
/// real path (inside the run's folder). Only a run's first original of a path counts, at most 1 MiB each, 64 files and
/// 16 MiB per run; one past a cap is refused, and later originals of that path in that run are refused too. Only runs the
/// pane's graph keeps are kept (<see cref="Retain"/>), a closed pane's go (<see cref="Forget"/>), and a start drops whatever
/// the saved state no longer names (<see cref="Prune"/>).
/// </summary>
public sealed class FileOriginalStore
{
    public const int MaximumOriginalBytes = 1_048_576;
    public const int MaximumFilesPerRun = 64;
    public const long MaximumBytesPerRun = 16L * 1_048_576;
    public const string FolderName = "file-originals";

    public sealed record Entry([property: JsonPropertyName("path")] string Path, [property: JsonPropertyName("blob")] string Blob, [property: JsonPropertyName("bytes")] long Bytes, [property: JsonPropertyName("created")] bool Created);
    public sealed record Manifest([property: JsonPropertyName("version")] int Version, [property: JsonPropertyName("sessionId")] string SessionId, [property: JsonPropertyName("runId")] string RunId,
        [property: JsonPropertyName("sequence")] long Sequence, [property: JsonPropertyName("files")] List<Entry> Files);

    private readonly object sync = new();
    private readonly string directory;
    /// <summary>Paths of a run (session, run, path) that were refused or already taken: a later original of them never counts.</summary>
    private readonly HashSet<(string, string, string)> settled = [];
    private long lastSequence;
    public string DirectoryPath => directory;

    public FileOriginalStore(string directory) { this.directory = directory; }

    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..32].ToLowerInvariant();
    private string SessionFolder(string sessionId) => Path.Combine(directory, Hash(sessionId));
    private string RunFolder(string sessionId, string runId) => Path.Combine(SessionFolder(sessionId), Hash(runId));

    private static Manifest? ReadManifest(string folder)
    {
        try
        {
            var value = JsonSerializer.Deserialize<Manifest>(File.ReadAllBytes(Path.Combine(folder, "run.json")));
            return value is { Version: 1, Files: not null } && Wire.Identifier(value.SessionId) && Wire.Identifier(value.RunId) ? value : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { return null; }
    }

    private static void WriteAtomically(string path, byte[] data)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temporary, data); File.Move(temporary, path, true); }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    /// <summary>
    /// Records <paramref name="filePath"/>'s content before the run changed it (null: the run created it). The path may be
    /// absolute or relative to <paramref name="workspaceRoot"/>; one that does not resolve to an existing file inside it is
    /// refused. True when this original was kept.
    /// </summary>
    public bool Capture(string sessionId, string runId, string workspaceRoot, string filePath, string? original)
    {
        if (!Wire.Identifier(sessionId) || !Wire.Identifier(runId)) return false;
        if (ReferencePreview.Resolve(filePath, workspaceRoot) is not { } target) return false;
        var relative = target.RelativePath;
        var key = (sessionId, runId, OperatingSystem.IsWindows() ? relative.ToLowerInvariant() : relative);
        lock (sync)
        {
            if (!settled.Add(key)) return false;
            var text = original ?? "";
            var bytes = Encoding.UTF8.GetBytes(text);
            if (bytes.Length > MaximumOriginalBytes) return false;
            try
            {
                var folder = RunFolder(sessionId, runId);
                var manifest = ReadManifest(folder) ?? new(1, sessionId, runId, NextSequence(), []);
                if (manifest.Files.Any(f => PathComparer.Equals(f.Path, relative))) return false;
                if (manifest.Files.Count >= MaximumFilesPerRun || manifest.Files.Sum(f => f.Bytes) + bytes.Length > MaximumBytesPerRun) return false;
                Directory.CreateDirectory(folder);
                var blob = manifest.Files.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".txt";
                WriteAtomically(Path.Combine(folder, blob), bytes);
                var next = manifest with { Files = [.. manifest.Files, new(relative, blob, bytes.Length, original is null)] };
                WriteAtomically(Path.Combine(folder, "run.json"), JsonSerializer.SerializeToUtf8Bytes(next));
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        }
    }

    /// <summary>A sequence later than every run's so far, also across restarts (the clock's ticks, never going back).</summary>
    private long NextSequence() => lastSequence = Math.Max(lastSequence + 1, DateTime.UtcNow.Ticks);

    /// <summary>
    /// The original of <paramref name="relativePath"/> that <paramref name="runId"/> captured, or with no run the newest run of
    /// the session that captured it; null when there is none.
    /// </summary>
    public FileOriginal? Find(string sessionId, string? runId, string relativePath)
    {
        if (!Wire.Identifier(sessionId) || runId is not null && !Wire.Identifier(runId)) return null;
        lock (sync)
        {
            IEnumerable<string> folders;
            if (runId is not null) folders = [RunFolder(sessionId, runId)];
            else
            {
                var session = SessionFolder(sessionId);
                try { folders = Directory.Exists(session) ? Directory.GetDirectories(session) : []; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
            }
            var candidates = folders.Select(ReadManifest).OfType<Manifest>()
                .Where(m => m.SessionId == sessionId && (runId is null || m.RunId == runId))
                .OrderByDescending(m => m.Sequence);
            foreach (var manifest in candidates)
            {
                if (manifest.Files.FirstOrDefault(f => PathComparer.Equals(f.Path, relativePath)) is not { } entry) continue;
                if (entry.Blob.Contains('/') || entry.Blob.Contains('\\') || entry.Blob.Contains("..")) return null;
                try
                {
                    var path = Path.Combine(RunFolder(sessionId, manifest.RunId), entry.Blob);
                    if (new FileInfo(path).Length > MaximumOriginalBytes) return null;
                    return new(manifest.RunId, entry.Path, Encoding.UTF8.GetString(File.ReadAllBytes(path)), entry.Created);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
            }
            return null;
        }
    }

    /// <summary>The run ids the store holds for a session, newest first.</summary>
    public IReadOnlyList<string> Runs(string sessionId)
    {
        if (!Wire.Identifier(sessionId)) return [];
        lock (sync)
        {
            var session = SessionFolder(sessionId);
            try { return Directory.Exists(session) ? Directory.GetDirectories(session).Select(ReadManifest).OfType<Manifest>().OrderByDescending(m => m.Sequence).Select(m => m.RunId).ToList() : []; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
        }
    }

    /// <summary>Drops the session's runs that are not in <paramref name="keep"/>.</summary>
    public void Retain(string sessionId, IReadOnlySet<string> keep)
    {
        if (!Wire.Identifier(sessionId)) return;
        lock (sync)
        {
            var session = SessionFolder(sessionId);
            try
            {
                if (!Directory.Exists(session)) return;
                foreach (var folder in Directory.GetDirectories(session))
                    if (ReadManifest(folder) is not { } manifest || !keep.Contains(manifest.RunId)) Delete(folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Drops everything a pane's runs captured.</summary>
    public void Forget(string sessionId)
    {
        if (!Wire.Identifier(sessionId)) return;
        lock (sync) { Delete(SessionFolder(sessionId)); settled.RemoveWhere(k => k.Item1 == sessionId); }
    }

    /// <summary>Keeps only the sessions named, each with only the runs named for it; everything else in the folder goes.</summary>
    public void Prune(IReadOnlyDictionary<string, IReadOnlySet<string>> sessions)
    {
        lock (sync)
        {
            try
            {
                if (!Directory.Exists(directory)) return;
                var known = sessions.Keys.Where(Wire.Identifier).ToDictionary(Hash, id => id);
                foreach (var folder in Directory.GetDirectories(directory))
                {
                    if (!known.TryGetValue(Path.GetFileName(folder), out var id)) { Delete(folder); continue; }
                    foreach (var run in Directory.GetDirectories(folder))
                        if (ReadManifest(run) is not { } manifest || manifest.SessionId != id || !sessions[id].Contains(manifest.RunId)) Delete(run);
                }
                foreach (var file in Directory.GetFiles(directory)) File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>The runs a pane's graph keeps: its runs' ids and the ids they were made from.</summary>
    public static IReadOnlySet<string> KeptRuns(RunSession session) =>
        (session.GraphRuns ?? []).SelectMany(r => new[] { r.Id, r.SourceRunID }).OfType<string>().Where(Wire.Identifier).ToHashSet(StringComparer.Ordinal);

    private static void Delete(string folder)
    {
        try { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>
/// A file's content at git's HEAD (M/MightyCore/FileChanges.swift GitHeadFile), read the way <see cref="WorkspaceGitInfo"/>
/// reads status: no hooks, no fsmonitor, no optional locks, no GIT_* from the app's environment, output bounded, 2 seconds
/// at most per command. Only reads: <c>rev-parse</c> and <c>cat-file blob HEAD:./path</c>, which applies no filters.
/// </summary>
public static class GitHeadFile
{
    /// <summary><c>Untracked</c>: HEAD has no such file (or there is no commit yet), so everything reads as added.</summary>
    public sealed record Content(byte[] Data, string? ShortSha, bool Untracked);

    private static ProcessStartInfo Command(string binary, string root, IEnumerable<string> arguments)
    {
        var info = ChildProcess.StartInfo(binary, new[] { "--no-optional-locks", "-c", "core.fsmonitor=false", "-c", "core.hooksPath=" + (OperatingSystem.IsWindows() ? "NUL" : "/dev/null"), "-c", "gc.auto=0", "-C", root }.Concat(arguments), root);
        foreach (var key in info.Environment.Keys.Where(key => key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray()) info.Environment.Remove(key);
        info.Environment["GIT_OPTIONAL_LOCKS"] = "0"; info.Environment["GIT_TERMINAL_PROMPT"] = "0"; info.Environment["LC_ALL"] = "C";
        return info;
    }

    private static string? Binary()
    {
        var name = OperatingSystem.IsWindows() ? "git.exe" : "git";
        return (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => Path.Combine(p.Trim('"'), name)).FirstOrDefault(File.Exists);
    }

    /// <summary>The command's exit code and its first <paramref name="limit"/> bytes, plus whether there were more; null when it did not finish in time.</summary>
    private static async Task<(int Exit, byte[] Output, bool Over)?> Run(string binary, string root, string[] arguments, int limit, CancellationToken cancellation)
    {
        await using var process = ChildProcess.Start(Command(binary, root, arguments)); process.Input.Close();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromSeconds(2));
        var stdout = ReadBounded(process.Output.BaseStream, limit, timeout.Token); var stderr = ReadBounded(process.Error.BaseStream, 65_536, timeout.Token);
        try
        {
            var exit = await process.Completion.WaitAsync(timeout.Token);
            var (data, over) = await stdout; await stderr;
            return (exit, data, over);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { return null; }
        finally
        {
            process.Kill(); timeout.Cancel();
            try { await Task.WhenAll(stdout, stderr); } catch (OperationCanceledException) { }
        }
    }

    private static async Task<(byte[] Data, bool Over)> ReadBounded(Stream stream, int limit, CancellationToken token)
    {
        var result = new MemoryStream(); var buffer = new byte[65_536]; var over = false;
        while (await stream.ReadAsync(buffer.AsMemory(), token) is var count && count > 0)
        {
            var room = limit - (int)result.Length;
            if (count > room) over = true;
            if (room > 0) result.Write(buffer, 0, Math.Min(count, room));
        }
        return (result.ToArray(), over);
    }

    /// <summary>
    /// HEAD's copy of <paramref name="relativePath"/> (relative to the real <paramref name="root"/>, '/' separated): null when
    /// the folder is in no repository, git is missing or fails, or the copy is over <paramref name="maximumBytes"/>.
    /// </summary>
    public static async Task<Content?> ReadAsync(string root, string relativePath, int maximumBytes = FileDiff.MaximumSideBytes, CancellationToken cancellation = default)
    {
        if (!Path.IsPathFullyQualified(root) || !Directory.Exists(root) || relativePath.Length == 0 || relativePath.Contains('\0') || relativePath.Contains('\\') || relativePath.Split('/').Any(c => c is "" or "." or "..")) return null;
        if (Binary() is not { } binary) return null;
        try
        {
            if (await Run(binary, root, ["rev-parse", "--is-inside-work-tree"], 64, cancellation) is not { Exit: 0 } inside || Encoding.UTF8.GetString(inside.Output).Trim() != "true") return null;
            var head = await Run(binary, root, ["rev-parse", "-q", "--verify", "HEAD^{commit}"], 256, cancellation);
            if (head is null) return null;
            var sha = Encoding.ASCII.GetString(head.Value.Output).Trim();
            // A repository with no commit yet: nothing is in HEAD.
            if (head.Value.Exit != 0 || !System.Text.RegularExpressions.Regex.IsMatch(sha, "^[0-9a-f]{40,64}$")) return new([], null, true);
            var blob = await Run(binary, root, ["cat-file", "blob", "HEAD:./" + relativePath], maximumBytes, cancellation);
            if (blob is null || blob.Value.Over) return null;
            return blob.Value.Exit == 0 ? new(blob.Value.Output, sha[..7], false) : new([], sha[..7], true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
    }
}

/// <summary>
/// <see cref="ReadAsync"/>: what a request changed in a file, against the run's own original when the store has one, else
/// against git's HEAD (M/MightyCore/FileChanges.swift FileChanges.read).
/// </summary>
public static class FileChanges
{
    /// <summary>
    /// The change set of <paramref name="relativePath"/> under <paramref name="workspaceRoot"/>, null when there is no diff to
    /// show (the path leaves the workspace or is no regular file, a side is binary, undecodable or over the caps, or there is
    /// no run original and no git base). With <paramref name="runId"/> only that run's original counts; without it the newest
    /// run of <paramref name="sessionId"/> that captured the file.
    /// </summary>
    public static async Task<FileChangeSet?> ReadAsync(FileOriginalStore? store, string workspaceRoot, string relativePath, string? sessionId = null, string? runId = null, CancellationToken cancellation = default)
    {
        if (WorkspaceFiles.Resolve(relativePath, workspaceRoot) is not { } real || WorkspaceFiles.RealPath(workspaceRoot) is not { } realRoot || Directory.Exists(real)) return null;
        var relative = Path.GetRelativePath(realRoot, real).Replace('\\', '/');
        var current = await Task.Run(() => Current(relativePath, workspaceRoot), cancellation);
        if (current is null) return null;
        if (store is not null && sessionId is not null && store.Find(sessionId, runId, relative) is { } original)
            return FileDiff.Compute(original.Text, current) is { } diff
                ? new FileChangeSet("run", diff.Hunks, diff.Added, diff.Removed, diff.Truncated, diff.Unchanged) { RunId = original.RunId, Created = original.Created ? true : null }
                : null;
        if (await GitHeadFile.ReadAsync(realRoot, relative, FileDiff.MaximumSideBytes, cancellation) is not { } head) return null;
        if (FilePreviewClassifier.DecodeText(head.Data, sample: false) is not { } decoded) return null;
        return FileDiff.Compute(decoded.Text, current) is { } headDiff
            ? new FileChangeSet("head", headDiff.Hunks, headDiff.Added, headDiff.Removed, headDiff.Truncated, headDiff.Unchanged) { HeadSha = head.ShortSha, Created = head.Untracked ? true : null }
            : null;
    }

    /// <summary>The file as it is now, through the files pane's open (<see cref="WorkspaceFiles.OpenFile"/>): null when over 1 MiB, binary or undecodable.</summary>
    private static string? Current(string relativePath, string root)
    {
        try
        {
            using var file = WorkspaceFiles.OpenFile(relativePath, root);
            if (file.Size > FileDiff.MaximumSideBytes) return null;
            var data = FilePreviewClassifier.ReadPrefix(file.Stream, FileDiff.MaximumSideBytes + 1);
            return data.Length > FileDiff.MaximumSideBytes ? null : FilePreviewClassifier.DecodeText(data, sample: false)?.Text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}
