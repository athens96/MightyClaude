using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MightyCode.Core;

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
/// run's Edit, Write and MultiEdit results (<see cref="FileEdit"/>). Kept apart from the state file, under
/// <c>&lt;data&gt;/file-originals/&lt;sha(session)&gt;/&lt;sha(run)&gt;/</c>: <c>run.json</c> (<see cref="Manifest"/>) and one
/// <c>&lt;n&gt;.txt</c> per file (UTF-8), each written to a temporary name and moved over.
/// <para>
/// A path is the real path relative to the run's folder (its <c>root</c>, recorded: an original is never read under another
/// root). Only a run's first edit of a path counts: an original that is unknown (no text, no patch that fits the file), over
/// 1 MiB, or past the run's 64 files or 16 MiB is recorded as refused, and the path's later edits in that run never pose as
/// its first. An original rebuilt from a patch holds only while each later edit of the path in the run confirms it: the CLI
/// may write the next edit before this one's result is read, and undoing a patch on that file keeps the next edit in. So the
/// file the run's last edit left is remembered (its hash, in this process), each later edit's patch undone on the file now
/// must give it (or its <c>originalFile</c> must be it), and an edit that does not drops the original as refused.
/// Each pane keeps the originals of its newest 20 runs, and the whole store at most 512 MiB, the oldest runs going
/// first. Only runs the pane's graph keeps are kept (<see cref="Retain"/>), a closed pane's go (<see cref="Forget"/>), and a
/// start drops whatever the saved state no longer names (<see cref="Prune"/>).
/// </para>
/// </summary>
public sealed class FileOriginalStore
{
    public const int MaximumOriginalBytes = 1_048_576;
    public const int MaximumFilesPerRun = 64;
    public const long MaximumBytesPerRun = 16L * 1_048_576;
    public const long MaximumBytes = 512L * 1_048_576;
    public const int MaximumRunsPerPane = 20;
    public const int MaximumRefusedPerRun = 1_024;
    public const string FolderName = "file-originals";

    public sealed record Entry([property: JsonPropertyName("path")] string Path, [property: JsonPropertyName("blob")] string Blob, [property: JsonPropertyName("bytes")] long Bytes, [property: JsonPropertyName("created")] bool Created);
    public sealed record Manifest([property: JsonPropertyName("version")] int Version, [property: JsonPropertyName("sessionId")] string SessionId, [property: JsonPropertyName("runId")] string RunId,
        [property: JsonPropertyName("sequence")] long Sequence, [property: JsonPropertyName("files")] List<Entry> Files)
    {
        /// <summary>The real path of the run's folder its paths are relative to.</summary>
        [JsonPropertyName("root")] public string? Root { get; init; }
        /// <summary>Paths the run edited whose original is not kept.</summary>
        [JsonPropertyName("refused")] public List<string>? Refused { get; init; }
    }
    private sealed record Indexed(string SessionId, string RunId, long Sequence, long Bytes, int Files);

    private readonly object sync = new();
    private readonly string directory;
    /// <summary>Paths of a run (session, run, path) already settled — kept or refused — in this process.</summary>
    private readonly HashSet<(string, string, string)> settled = [];
    /// <summary>For an original rebuilt from a patch: the hash of the file as the run's last confirmed edit of it left it.</summary>
    private readonly Dictionary<(string, string, string), string> following = [];
    /// <summary>Every run folder's manifest summary, read once from disk and kept up to date.</summary>
    private Dictionary<string, Indexed>? index;
    private long lastSequence;
    public string DirectoryPath => directory;

    private readonly long budget;
    /// <param name="budget">The store's whole size (<see cref="MaximumBytes"/>; checks pass a smaller one).</param>
    public FileOriginalStore(string directory, long budget = MaximumBytes) { this.directory = directory; this.budget = budget; }

    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..32].ToLowerInvariant();
    private string SessionFolder(string sessionId) => Path.Combine(directory, Hash(sessionId));
    private string RunFolder(string sessionId, string runId) => Path.Combine(SessionFolder(sessionId), Hash(runId));
    private static (string, string, string) Key(string sessionId, string runId, string path) => (sessionId, runId, OperatingSystem.IsWindows() ? path.ToLowerInvariant() : path);
    /// <summary>A background notice run's id (<see cref="BackgroundNoticeRuns.RunID"/>) stands for the run it came from.</summary>
    public static string SourceRun(string runId) => runId.IndexOf(BackgroundNoticeRuns.Marker, StringComparison.Ordinal) is var at and > 0 ? runId[..at] : runId;

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

    /// <summary>Under <see cref="sync"/>: the run folders' summaries, read from disk the first time.</summary>
    private Dictionary<string, Indexed> Index()
    {
        if (index is not null) return index;
        index = new(PathComparer);
        try
        {
            if (Directory.Exists(directory))
                foreach (var session in Directory.GetDirectories(directory))
                    foreach (var run in Directory.GetDirectories(session))
                        if (ReadManifest(run) is { } manifest) { index[run] = Summary(manifest); lastSequence = Math.Max(lastSequence, manifest.Sequence); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return index;
    }
    private static Indexed Summary(Manifest manifest) => new(manifest.SessionId, manifest.RunId, manifest.Sequence, manifest.Files.Sum(f => f.Bytes), manifest.Files.Count);

    /// <summary>Records what the run's edit says about <paramref name="filePath"/> before it (the convenience form: text, or null for a created file).</summary>
    public bool Capture(string sessionId, string runId, string workspaceRoot, string filePath, string? original) =>
        Capture(sessionId, runId, workspaceRoot, filePath, original is null ? new FileEdit(null, true, null) : new FileEdit(original, false, null));

    /// <summary>
    /// Records what an edit of the run says about <paramref name="filePath"/> before it. The path may be absolute or relative
    /// to <paramref name="workspaceRoot"/>; one that does not resolve to an existing file inside it is ignored. A patch is
    /// undone against the file as it is now (<see cref="FilePatch.ReverseApply"/>). True when an original was kept; an edit
    /// whose original cannot be kept settles the path as refused.
    /// </summary>
    public bool Capture(string sessionId, string runId, string workspaceRoot, string filePath, FileEdit edit)
    {
        if (!Wire.Identifier(sessionId) || !Wire.Identifier(runId)) return false;
        if (ReferencePreview.Resolve(filePath, workspaceRoot) is not { } target || WorkspaceFiles.RealPath(workspaceRoot) is not { } realRoot) return false;
        var relative = target.RelativePath;
        lock (sync)
        {
            var key = Key(sessionId, runId, relative);
            if (!settled.Add(key))
            {
                if (following.TryGetValue(key, out var expected))
                {
                    if (Follow(expected, edit, relative, realRoot) is { } next) following[key] = next;
                    else { following.Remove(key); Withdraw(sessionId, runId, relative); }
                }
                return false;
            }
            try
            {
                var folder = RunFolder(sessionId, runId);
                var manifest = ReadManifest(folder) ?? new(1, sessionId, runId, NextSequence(), []) { Root = realRoot, Refused = [] };
                if (manifest.Files.Any(f => PathComparer.Equals(f.Path, relative)) || (manifest.Refused ?? []).Contains(relative, PathComparer)) return false;
                if (manifest.Root is not { } root || !PathComparer.Equals(root, realRoot)) return false;
                // The files are read without a byte order mark, so an original is kept without one too.
                string? text, after = null;
                if (edit.Original is { } given) text = FilePatch.WithoutByteOrderMark(given);
                else if (edit.Created) text = "";
                else if (edit.Patch is { } patch && FileChanges.Current(relative, realRoot) is { } current && FilePatch.ReverseApply(current, patch) is { } before) { text = before; after = Hash(current); }
                else text = null;
                var bytes = text is null ? null : Encoding.UTF8.GetBytes(text);
                Directory.CreateDirectory(folder);
                if (bytes is null || bytes.Length > MaximumOriginalBytes || manifest.Files.Count >= MaximumFilesPerRun || manifest.Files.Sum(f => f.Bytes) + bytes.Length > MaximumBytesPerRun)
                {
                    var refused = manifest.Refused ?? [];
                    if (refused.Count < MaximumRefusedPerRun) Save(folder, manifest with { Refused = [.. refused, relative] });
                    return false;
                }
                // A withdrawn original leaves a gap in the numbers: the next blob takes the first free one.
                var number = manifest.Files.Count;
                while (manifest.Files.Any(f => f.Blob == Blob(number))) number++;
                var blob = Blob(number);
                WriteAtomically(Path.Combine(folder, blob), bytes);
                Save(folder, manifest with { Files = [.. manifest.Files, new(relative, blob, bytes.Length, edit.Original is null && edit.Created)] });
                if (after is not null) following[key] = after;
                Evict(folder);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        }
    }

    private static string Blob(int number) => number.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".txt";

    /// <summary>
    /// The hash of the file after a later edit of a path whose original was rebuilt from a patch, when the edit confirms the
    /// file the run's last edit left (<paramref name="expected"/>); null when it does not or cannot.
    /// </summary>
    private static string? Follow(string expected, FileEdit edit, string relative, string realRoot)
    {
        if (edit.Patch is not { } patch) return null;
        if (edit.Original is { } given)
        {
            var before = FilePatch.WithoutByteOrderMark(given);
            return Hash(before) == expected && FilePatch.ReverseApply(before, FilePatch.Inverted(patch)) is { } then ? Hash(then) : null;
        }
        return FileChanges.Current(relative, realRoot) is { } current && FilePatch.ReverseApply(current, patch) is { } undone && Hash(undone) == expected ? Hash(current) : null;
    }

    /// <summary>Under <see cref="sync"/>: drops a kept original of the run, settling its path as refused.</summary>
    private void Withdraw(string sessionId, string runId, string relative)
    {
        var folder = RunFolder(sessionId, runId);
        try
        {
            if (ReadManifest(folder) is not { } manifest || manifest.Files.FirstOrDefault(f => PathComparer.Equals(f.Path, relative)) is not { } entry) return;
            if (!entry.Blob.Contains('/') && !entry.Blob.Contains('\\') &&!entry.Blob.Contains("..")) File.Delete(Path.Combine(folder, entry.Blob));
            var refused = manifest.Refused ?? [];
            Save(folder, manifest with { Files = manifest.Files.Where(f => f != entry).ToList(), Refused = refused.Count < MaximumRefusedPerRun ? [.. refused, relative] : refused });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private void Save(string folder, Manifest manifest)
    {
        WriteAtomically(Path.Combine(folder, "run.json"), JsonSerializer.SerializeToUtf8Bytes(manifest));
        Index()[folder] = Summary(manifest);
    }

    /// <summary>
    /// Under <see cref="sync"/>: each pane keeps the originals of its newest <see cref="MaximumRunsPerPane"/> runs and the store
    /// at most <see cref="MaximumBytes"/>; the oldest runs go first, never <paramref name="keep"/> (the run just written).
    /// </summary>
    private void Evict(string? keep)
    {
        var all = Index();
        foreach (var pane in all.Where(e => e.Value.Files > 0).GroupBy(e => e.Value.SessionId))
            foreach (var old in pane.OrderByDescending(e => e.Value.Sequence).Skip(MaximumRunsPerPane).Where(e => !PathComparer.Equals(e.Key, keep ?? "")).ToList()) Drop(old.Key);
        var total = all.Values.Sum(v => v.Bytes);
        foreach (var old in all.OrderBy(e => e.Value.Sequence).Where(e => !PathComparer.Equals(e.Key, keep ?? "")).ToList())
        {
            if (total <= budget) break;
            total -= old.Value.Bytes; Drop(old.Key);
        }
    }

    /// <summary>Under <see cref="sync"/>: removes one run folder, its summary and what this process settled for it.</summary>
    private void Drop(string folder)
    {
        if (Index().Remove(folder, out var gone))
        {
            settled.RemoveWhere(k => k.Item1 == gone.SessionId && k.Item2 == gone.RunId);
            foreach (var key in following.Keys.Where(k => k.Item1 == gone.SessionId && k.Item2 == gone.RunId).ToList()) following.Remove(key);
        }
        Delete(folder);
    }

    /// <summary>A sequence later than every run's so far, also across restarts (the clock's ticks, never going back).</summary>
    private long NextSequence() { Index(); return lastSequence = Math.Max(lastSequence + 1, DateTime.UtcNow.Ticks); }

    /// <summary>
    /// The original of <paramref name="relativePath"/> that <paramref name="runId"/> (a notice run: the run it came from)
    /// captured, or with no run the one of the session's newest run that edited the path; null when that run refused it, it was
    /// captured under another root than <paramref name="root"/> (when given), or there is none.
    /// </summary>
    public FileOriginal? Find(string sessionId, string? runId, string relativePath, string? root = null)
    {
        if (runId is not null) runId = SourceRun(runId);
        if (!Wire.Identifier(sessionId) || runId is not null && !Wire.Identifier(runId)) return null;
        var realRoot = root is null ? null : WorkspaceFiles.RealPath(root);
        if (root is not null && realRoot is null) return null;
        lock (sync)
        {
            IEnumerable<string> folders = runId is not null ? [RunFolder(sessionId, runId)]
                : Index().Where(e => e.Value.SessionId == sessionId).OrderByDescending(e => e.Value.Sequence).Select(e => e.Key).ToList();
            foreach (var folder in folders)
            {
                if (ReadManifest(folder) is not { } manifest || manifest.SessionId != sessionId || runId is not null && manifest.RunId != runId) continue;
                if ((manifest.Refused ?? []).Contains(relativePath, PathComparer)) return null;
                if (manifest.Files.FirstOrDefault(f => PathComparer.Equals(f.Path, relativePath)) is not { } entry) continue;
                if (realRoot is not null && (manifest.Root is not { } kept || !PathComparer.Equals(kept, realRoot))) return null;
                if (entry.Blob.Contains('/') || entry.Blob.Contains('\\') || entry.Blob.Contains("..")) return null;
                try
                {
                    var path = Path.Combine(folder, entry.Blob);
                    if (new FileInfo(path).Length > MaximumOriginalBytes) return null;
                    return new(manifest.RunId, entry.Path, Encoding.UTF8.GetString(File.ReadAllBytes(path)), entry.Created);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
            }
            return null;
        }
    }

    /// <summary>The paths (relative to the run's folder, '/') the run's manifest records as edited, kept or refused; empty when the run is unknown or was recorded under another root than <paramref name="root"/>.</summary>
    public IReadOnlyList<string> Paths(string sessionId, string runId, string root)
    {
        runId = SourceRun(runId);
        if (!Wire.Identifier(sessionId) || !Wire.Identifier(runId) || WorkspaceFiles.RealPath(root) is not { } realRoot) return [];
        lock (sync)
        {
            if (ReadManifest(RunFolder(sessionId, runId)) is not { } manifest || manifest.SessionId != sessionId || manifest.RunId != runId
                || manifest.Root is not { } kept || !PathComparer.Equals(kept, realRoot)) return [];
            return manifest.Files.Select(f => f.Path).Concat(manifest.Refused ?? []).Distinct(PathComparer).ToList();
        }
    }

    /// <summary>The run ids the store holds for a session, newest first.</summary>
    public IReadOnlyList<string> Runs(string sessionId)
    {
        if (!Wire.Identifier(sessionId)) return [];
        lock (sync) return Index().Values.Where(v => v.SessionId == sessionId).OrderByDescending(v => v.Sequence).Select(v => v.RunId).ToList();
    }

    /// <summary>Drops the session's runs that are not in <paramref name="keep"/> (a notice run keeps the run it came from).</summary>
    public void Retain(string sessionId, IReadOnlySet<string> keep)
    {
        if (!Wire.Identifier(sessionId)) return;
        var kept = keep.Select(SourceRun).ToHashSet(StringComparer.Ordinal);
        lock (sync)
        {
            foreach (var run in Index().Where(e => e.Value.SessionId == sessionId && !kept.Contains(e.Value.RunId)).Select(e => e.Key).ToList()) Drop(run);
            // A folder with no readable manifest is no run.
            try { if (Directory.Exists(SessionFolder(sessionId))) foreach (var run in Directory.GetDirectories(SessionFolder(sessionId))) if (!Index().ContainsKey(run)) Delete(run); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Drops everything a pane's runs captured.</summary>
    public void Forget(string sessionId)
    {
        if (!Wire.Identifier(sessionId)) return;
        lock (sync)
        {
            foreach (var run in Index().Where(e => e.Value.SessionId == sessionId).Select(e => e.Key).ToList()) Index().Remove(run);
            Delete(SessionFolder(sessionId)); settled.RemoveWhere(k => k.Item1 == sessionId);
            foreach (var key in following.Keys.Where(k => k.Item1 == sessionId).ToList()) following.Remove(key);
        }
    }

    /// <summary>
    /// Keeps only the sessions named, each with only the runs named for it (a notice run keeps the run it came from); everything
    /// else in the folder goes, temporary files left by a crash included, and then the budget holds.
    /// </summary>
    public void Prune(IReadOnlyDictionary<string, IReadOnlySet<string>> sessions)
    {
        lock (sync)
        {
            try
            {
                if (!Directory.Exists(directory)) return;
                var known = new Dictionary<string, string>();
                foreach (var id in sessions.Keys.Where(Wire.Identifier)) known[Hash(id)] = id;
                foreach (var folder in Directory.GetDirectories(directory))
                {
                    if (!known.TryGetValue(Path.GetFileName(folder), out var id)) { Delete(folder); continue; }
                    var runs = sessions[id].Select(SourceRun).ToHashSet(StringComparer.Ordinal);
                    foreach (var run in Directory.GetDirectories(folder))
                        if (ReadManifest(run) is not { } manifest || manifest.SessionId != id || !runs.Contains(manifest.RunId)) Delete(run);
                }
                // A file that cannot go (open elsewhere, read-only) leaves the rest to go.
                foreach (var file in Directory.GetFiles(directory).Concat(Directory.EnumerateFiles(directory, "*.tmp", SearchOption.AllDirectories)).ToList())
                    try { File.Delete(file); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            index = null; settled.Clear(); following.Clear();
            Evict(null);
        }
    }

    /// <summary>The runs a pane's graph keeps: its runs' ids and the ids they were made from.</summary>
    public static IReadOnlySet<string> KeptRuns(RunSession session) =>
        (session.GraphRuns ?? []).SelectMany(r => new[] { r.Id, r.SourceRunID }).OfType<string>().Where(Wire.Identifier).ToHashSet(StringComparer.Ordinal);

    /// <summary>The kept runs of every pane; a pane id that appears twice keeps the runs of both.</summary>
    public static IReadOnlyDictionary<string, IReadOnlySet<string>> KeptRuns(IEnumerable<RunSession> sessions)
    {
        var kept = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var session in sessions)
        {
            if (!kept.TryGetValue(session.Id, out var runs)) kept[session.Id] = runs = new(StringComparer.Ordinal);
            runs.UnionWith(KeptRuns(session));
        }
        return kept.ToDictionary(p => p.Key, p => (IReadOnlySet<string>)p.Value);
    }

    private static void Delete(string folder)
    {
        try { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>
/// A file's content at git's HEAD (M/MightyCore/FileChanges.swift GitHeadFile), read the way <see cref="WorkspaceGitInfo"/>
/// reads status: no hooks, no fsmonitor, no signature programs, no optional locks, no GIT_* from the app's environment, output
/// bounded, 2 seconds at most per command. Only reads: <c>rev-parse</c> and <c>cat-file blob HEAD:./path</c>, which applies no
/// filters. A partial clone's missing blob is never fetched (GIT_NO_LAZY_FETCH, and <c>protocol.allow=never</c> for a git older
/// than 2.44 that does not know it): the read fails, and the file has no base.
/// </summary>
public static class GitHeadFile
{
    /// <summary><c>Untracked</c>: HEAD has no such file (or there is no commit yet), so everything reads as added.</summary>
    public sealed record Content(byte[] Data, string? ShortSha, bool Untracked);

    internal static ProcessStartInfo Command(string binary, string root, IEnumerable<string> arguments)
    {
        var info = ChildProcess.StartInfo(binary, ["--no-optional-locks", "-c", "core.fsmonitor=false", "-c", "core.hooksPath=" + (OperatingSystem.IsWindows() ? "NUL" : "/dev/null"), "-c", "gc.auto=0", "-c", "protocol.allow=never", .. DelegationGit.NoSignaturePrograms, "-C", root, .. arguments], root);
        foreach (var key in info.Environment.Keys.Where(key => key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray()) info.Environment.Remove(key);
        info.Environment["GIT_OPTIONAL_LOCKS"] = "0"; info.Environment["GIT_TERMINAL_PROMPT"] = "0"; info.Environment["GIT_NO_LAZY_FETCH"] = "1"; info.Environment["LC_ALL"] = "C";
        return info;
    }

    private static string? Binary()
    {
        var name = OperatingSystem.IsWindows() ? "git.exe" : "git";
        return (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => Path.Combine(p.Trim('"'), name)).FirstOrDefault(File.Exists);
    }

    /// <summary>The command's exit code, its first <paramref name="limit"/> bytes (and whether there were more) and the start of its stderr; null when it did not finish in time.</summary>
    private static async Task<(int Exit, byte[] Output, bool Over, string Error)?> Run(string binary, string root, string[] arguments, int limit, CancellationToken cancellation)
    {
        await using var process = ChildProcess.Start(Command(binary, root, arguments)); process.Input.Close();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromSeconds(2));
        var stdout = ReadBounded(process.Output.BaseStream, limit, timeout.Token); var stderr = ReadBounded(process.Error.BaseStream, 65_536, timeout.Token);
        try
        {
            var exit = await process.Completion.WaitAsync(timeout.Token);
            var (data, over) = await stdout; var (error, _) = await stderr;
            return (exit, data, over, Encoding.UTF8.GetString(error));
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

    /// <summary>git's own words (LC_ALL=C) for a path HEAD does not have; any other failure is no base at all.</summary>
    public static bool NotInHead(string error) =>
        error.Contains("does not exist in 'HEAD'", StringComparison.Ordinal) || error.Contains("exists on disk, but not in 'HEAD'", StringComparison.Ordinal);

    /// <summary>
    /// HEAD's copy of <paramref name="relativePath"/> (relative to the real <paramref name="root"/>, '/' separated): null when
    /// the folder is in no repository, git is missing or fails for any reason but the path not being in HEAD, or the copy is
    /// over <paramref name="maximumBytes"/>.
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
            if (blob is not { } read || read.Over) return null;
            if (read.Exit == 0) return new(read.Output, sha[..7], false);
            return NotInHead(read.Error) ? new([], sha[..7], true) : null;
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
    /// The change set of <paramref name="relativePath"/> under <paramref name="workspaceRoot"/> (for a delegated child, its
    /// worktree folder), null when there is no diff to show (the path leaves the folder or is no regular file, a side is
    /// binary, undecodable or over the caps, or there is no run original and no git base). With <paramref name="runId"/> (a
    /// notice run: the run it came from) only that run's original counts; without it the newest run of
    /// <paramref name="sessionId"/> that edited the file. An original captured under another folder never counts.
    /// </summary>
    public static async Task<FileChangeSet?> ReadAsync(FileOriginalStore? store, string workspaceRoot, string relativePath, string? sessionId = null, string? runId = null, CancellationToken cancellation = default)
    {
        if (WorkspaceFiles.Resolve(relativePath, workspaceRoot) is not { } real || WorkspaceFiles.RealPath(workspaceRoot) is not { } realRoot || Directory.Exists(real)) return null;
        var relative = OnDiskCase(realRoot, Path.GetRelativePath(realRoot, real).Replace('\\', '/'));
        var current = Current(relativePath, workspaceRoot);
        if (current is null) return null;
        if (store is not null && sessionId is not null && store.Find(sessionId, runId, relative, workspaceRoot) is { } original)
            return FileDiff.Compute(original.Text, current) is { } diff
                ? new FileChangeSet("run", diff.Hunks, diff.Added, diff.Removed, diff.Truncated, diff.Unchanged) { RunId = original.RunId, Created = original.Created ? true : null }
                : null;
        if (await GitHeadFile.ReadAsync(realRoot, relative, FileDiff.MaximumSideBytes, cancellation).ConfigureAwait(false) is not { } head) return null;
        if (FilePreviewClassifier.DecodeText(head.Data, sample: false) is not { } decoded) return null;
        return FileDiff.Compute(decoded.Text, current) is { } headDiff
            ? new FileChangeSet("head", headDiff.Hunks, headDiff.Added, headDiff.Removed, headDiff.Truncated, headDiff.Unchanged) { HeadSha = head.ShortSha, Created = head.Untracked ? true : null }
            : null;
    }

    /// <summary>
    /// <paramref name="relative"/> with each component as the disk spells it: git compares paths by case, Windows does not, so
    /// a link written <c>src/app.cs</c> still finds HEAD's <c>Src/App.cs</c>. Elsewhere the path is already the disk's.
    /// </summary>
    public static string OnDiskCase(string realRoot, string relative)
    {
        if (!OperatingSystem.IsWindows()) return relative;
        var current = realRoot; var parts = new List<string>();
        foreach (var part in relative.Split('/'))
        {
            string? found = null;
            try { found = new DirectoryInfo(current).EnumerateFileSystemInfos(part).Select(i => i.Name).FirstOrDefault(n => string.Equals(n, part, StringComparison.OrdinalIgnoreCase)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
            parts.Add(found ?? part); current = Path.Combine(current, found ?? part);
        }
        return string.Join('/', parts);
    }

    /// <summary>The file as it is now, through the files pane's open (<see cref="WorkspaceFiles.OpenFile"/>): null when over 1 MiB, binary or undecodable.</summary>
    public static string? Current(string relativePath, string root)
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
