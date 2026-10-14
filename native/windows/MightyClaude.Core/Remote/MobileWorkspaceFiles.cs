using System.Globalization;
using System.Text;
using System.Text.Json;

namespace MightyClaude.Core;

/// <summary>One row of a phone's folder listing (<c>GET /m1/workspaces/{id}/files</c>). Size (files only) and date are the item's own.</summary>
public sealed record MobileFileEntry(string Name, string RelativePath, string Kind, long? Size, string? Modified, bool Noise);
public sealed record MobileFileListing(int Protocol, string WorkspaceId, string Path, IReadOnlyList<MobileFileEntry> Entries, bool Truncated);

/// <summary>
/// One file's preview (<c>GET /m1/workspaces/{id}/file</c>). Which optional fields are present follows <c>Type</c>: text for
/// <c>source</c>/<c>markdown</c>, pixels for <c>image</c>, a <c>Reason</c> for <c>unsupported</c>.
/// </summary>
public sealed record MobileFilePreview(int Protocol, string WorkspaceId, string Path, string Name, long Size, string? Modified, string Type)
{
    public string? Language { get; init; }
    public string? Encoding { get; init; }
    public string? Text { get; init; }
    public bool? Truncated { get; init; }
    public int? LineCount { get; init; }
    public string? Mime { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public int? ThumbnailWidth { get; init; }
    public int? ThumbnailHeight { get; init; }
    public string? Data { get; init; }
    /// <summary><c>binary</c>, <c>notRegularFile</c>, <c>tooLarge</c> or <c>undecodable</c>.</summary>
    public string? Reason { get; init; }
}

/// <summary>
/// The phone's read-only view of a workspace folder (M/MightyCore/Remote/MobileWorkspaceFiles.swift): the files pane's listing
/// and preview cut to what fits one relay frame. Containment is exactly the pane's: every path goes through
/// <see cref="WorkspaceFiles.Resolve"/> and <see cref="WorkspaceFiles.OpenFile"/>. A refusal carries a <c>code</c> beside its
/// message, which the phone words itself.
/// </summary>
public static class MobileWorkspaceFiles
{
    public const int MaximumEntries = 2_000;
    /// <summary>Text read from a file for the phone.</summary>
    public const int MaximumTextBytes = 512 * 1_024;
    /// <summary>The encoded reply body: text and entries are cut further when escaping would take them over.</summary>
    public const int MaximumReplyBytes = 768 * 1_024;
    public const int MaximumThumbnailPixels = 2_048, MaximumThumbnailBytes = 512 * 1_024;
    /// <summary>Long sides tried, largest first, until the thumbnail fits its bytes.</summary>
    public static readonly int[] ThumbnailLadder = [2_048, 1_600, 1_280, 1_024, 768, 512, 256];

    public static MobileRequestException WorkspaceNotFound => new(404, Locale.Get("remote.error.workspaceNotFound"), "workspaceNotFound");
    public static MobileRequestException NotFound => new(404, Locale.Get("remote.files.notFound"), "notFound");
    public static MobileRequestException OutsideWorkspace => new(403, Locale.Get("remote.files.outsideWorkspace"), "outsideWorkspace");
    public static MobileRequestException NotReadable => new(403, Locale.Get("remote.files.notReadable"), "notReadable");
    public static MobileRequestException NotDirectory => new(400, Locale.Get("phone.files.error.notDirectory"), "notDirectory");
    public static MobileRequestException BadPath => new(400, Locale.Get("common.badPath"), "badPath");
    /// <summary>A newer preview from the same phone came in while this one waited for its turn.</summary>
    public static MobileRequestException Superseded => new(409, Locale.Get("remote.files.superseded"), "superseded");

    private static string? Stamp(DateTimeOffset? date) => date?.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>
    /// The relative path a phone asked for: "" (the root) only where allowed, no absolute path, no <c>..</c>, no empty or
    /// <c>.</c> component, no NUL — and on Windows no backslash or drive or stream name either.
    /// </summary>
    public static string ValidatedPath(string? raw, bool allowRoot)
    {
        var path = raw ?? "";
        if (path.Length == 0) return allowRoot ? "" : throw BadPath;
        if (path.Contains('\0') || Encoding.UTF8.GetByteCount(path) > WorkspaceFiles.MaximumPathBytes) throw BadPath;
        var components = path.Split('/');
        if (path.StartsWith('/') || path.Contains('\\') || path.Contains(':') || components.Contains("..")) throw OutsideWorkspace;
        if (components.Any(c => c.Length == 0 || c == ".")) throw BadPath;
        return path;
    }

    /// <summary>
    /// A path the pane's resolver refused. Only an item directly inside a folder that itself resolves inside the root is looked
    /// at: it exists (a link out of the root, or a dangling one) or it does not. Under a folder that does not resolve inside the
    /// root every path is <c>notFound</c> alike, so the answer never tells whether something exists out there.
    /// </summary>
    public static MobileRequestException Refusal(string relativePath, string root)
    {
        var slash = relativePath.LastIndexOf('/'); var parent = slash < 0 ? "" : relativePath[..slash]; var name = relativePath[(slash + 1)..];
        if (name.Length == 0 || WorkspaceFiles.Resolve(parent, root) is not { } folder) return NotFound;
        try { File.GetAttributes(Path.Combine(folder, name)); return OutsideWorkspace; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return NotFound; }
    }

    public static MobileFileListing Listing(string workspaceId, string path, string root)
    {
        WorkspaceDirectoryListing listing;
        try { listing = WorkspaceFiles.List(path, root); }
        catch (WorkspaceFileException ex) { throw ex.Error switch { WorkspaceFileError.OutsideRoot => Refusal(path, root), WorkspaceFileError.NotDirectory => NotDirectory, _ => NotReadable }; }
        var realRoot = WorkspaceFiles.RealPath(root);
        return new(1, workspaceId, path, listing.Entries.Take(MaximumEntries).Select(entry => Entry(entry, root, realRoot)).ToArray(), listing.Truncated || listing.Entries.Count > MaximumEntries);
    }
    /// <summary>Size and date are never read through a link: a link's are read at the real path the resolver checked against the root.</summary>
    private static MobileFileEntry Entry(WorkspaceFileEntry entry, string root, string? realRoot)
    {
        var kind = (entry.IsSymlink ? "symlink-" : "") + (entry.IsDirectory ? "folder" : "file");
        var target = entry.IsSymlink ? WorkspaceFiles.Resolve(entry.RelativePath, root) : realRoot is null ? null : Path.Combine(realRoot, entry.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        try
        {
            if (target is not null)
            {
                FileSystemInfo info = entry.IsDirectory ? new DirectoryInfo(target) : new FileInfo(target);
                if (info.Exists) return new(entry.Name, entry.RelativePath, kind, entry.IsDirectory ? null : ((FileInfo)info).Length, Stamp(new DateTimeOffset(info.LastWriteTimeUtc)), entry.IsNoise);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        return new(entry.Name, entry.RelativePath, kind, null, null, entry.IsNoise);
    }

    private static int Bytes(object value) => JsonSerializer.SerializeToUtf8Bytes(value, Wire.Json).Length;
    /// <summary>The listing under <see cref="MaximumReplyBytes"/>, dropping entries from the end (and saying so) when long paths would take it over.</summary>
    public static MobileFileListing Fitted(MobileFileListing listing)
    {
        while (Bytes(listing) > MaximumReplyBytes && listing.Entries.Count > 0)
            listing = listing with { Entries = listing.Entries.Take(listing.Entries.Count - Math.Max(1, listing.Entries.Count / 4)).ToArray(), Truncated = true };
        return listing;
    }
    /// <summary>The preview under <see cref="MaximumReplyBytes"/>: text that escaping would take over is cut by a quarter at a time (and marked truncated).</summary>
    public static MobileFilePreview Fitted(MobileFilePreview preview)
    {
        while (Bytes(preview) > MaximumReplyBytes && preview.Text is { Length: > 0 } text)
        {
            var cut = text.Length * 3 / 4; if (cut > 0 && char.IsHighSurrogate(text[cut - 1])) cut--;
            var kept = text[..cut]; preview = preview with { Text = kept, Truncated = true, LineCount = SourceLines.Scan(kept).Starts.Count };
        }
        return preview;
    }

    /// <summary>A preview read as far as it goes without decoding an image: an image's file is left open, unread, for the caller to decode once it holds the preview slot.</summary>
    public sealed record Stage(MobileFilePreview Preview, WorkspaceOpenFile? Image) : IDisposable { public void Dispose() => Image?.Dispose(); }

    /// <summary>Opens and classifies the file; text is read here (up to <see cref="MaximumTextBytes"/>), an image is not.</summary>
    public static Stage Prepare(string workspaceId, string path, string root)
    {
        var name = path.Split('/')[^1]; WorkspaceOpenFile file;
        try { file = WorkspaceFiles.OpenFile(path, root); }
        catch (WorkspaceFileOpenException ex) when (ex.Error == WorkspaceFileOpenError.Missing) { throw Refusal(path, root); }
        catch (WorkspaceFileOpenException ex) when (ex.Error == WorkspaceFileOpenError.NotRegularFile)
        {
            // A folder or a device: its details only, as the desktop shows it.
            if (WorkspaceFiles.Resolve(path, root) is not { } real) throw Refusal(path, root);
            DateTimeOffset? modified = null; long size = 0;
            try { if (Directory.Exists(real)) modified = new DateTimeOffset(Directory.GetLastWriteTimeUtc(real)); else { var info = new FileInfo(real); size = info.Length; modified = new DateTimeOffset(info.LastWriteTimeUtc); } }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            return new(new(1, workspaceId, path, name, size, Stamp(modified), "unsupported") { Reason = "notRegularFile" }, null);
        }
        catch (WorkspaceFileOpenException) { throw NotReadable; }
        var kept = false;
        try
        {
            var preview = new MobileFilePreview(1, workspaceId, path, name, file.Size, Stamp(file.Modified), "unsupported");
            var kind = FilePreviewClassifier.Classify(name, FilePreviewClassifier.ReadHead(file.Stream));
            switch (kind.Tag)
            {
                case FilePreviewKindTag.Image:
                    if (file.Size > FilePreviewClassifier.MaximumImageBytes) return new(preview with { Reason = "tooLarge" }, null);
                    kept = true; return new(preview, file);
                case FilePreviewKindTag.Markdown or FilePreviewKindTag.Source:
                    var read = FilePreviewClassifier.ReadText(file.Stream, MaximumTextBytes);
                    return new(preview with { Type = kind.Tag == FilePreviewKindTag.Markdown ? "markdown" : "source", Language = kind.Tag == FilePreviewKindTag.Source ? kind.Language.ToString().ToLowerInvariant() : null,
                        Encoding = TextEncodings.DisplayName(read.Encoding), Text = read.Text, Truncated = read.Truncated, LineCount = SourceLines.Scan(read.Text).Starts.Count }, null);
                default: return new(preview with { Reason = "binary" }, null);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw NotReadable; }
        finally { if (!kept) file.Dispose(); }
    }

    /// <summary>The bytes of the image <see cref="Prepare"/> left open; null when it grew past the image limit meanwhile.</summary>
    public static byte[]? ImageBytes(WorkspaceOpenFile file, CancellationToken token)
    {
        try { var data = FilePreviewClassifier.ReadPrefix(file.Stream, checked((int)FilePreviewClassifier.MaximumImageBytes + 1), token); return data.Length > FilePreviewClassifier.MaximumImageBytes ? null : data; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw NotReadable; }
    }
}

/// <summary>
/// The image previews' own turn, one at a time (M/MightyCore/Remote/MobileRemoteService.swift:826-843). A newer preview from
/// the same phone takes an older one's place in the queue: the phone has moved on, so the older one is dropped rather than
/// decoded for nobody.
/// </summary>
public sealed class MobilePreviewSlot
{
    private readonly object sync = new();
    private readonly List<(string Device, TaskCompletionSource<bool> Turn)> waiters = [];
    private bool taken;
    public int Waiting { get { lock (sync) return waiters.Count; } }

    /// <summary>True when the caller holds the slot and must <see cref="Release"/> it; false when a newer preview from the same phone replaced this one.</summary>
    public async Task<bool> Acquire(string device, CancellationToken token)
    {
        TaskCompletionSource<bool> turn;
        lock (sync)
        {
            token.ThrowIfCancellationRequested();
            if (!taken) { taken = true; return true; }
            var older = waiters.FindIndex(w => w.Device == device);
            if (older >= 0) { waiters[older].Turn.TrySetResult(false); waiters.RemoveAt(older); }
            turn = new(TaskCreationOptions.RunContinuationsAsynchronously); waiters.Add((device, turn));
        }
        using var registration = token.Register(() => { bool waiting; lock (sync) waiting = waiters.RemoveAll(w => ReferenceEquals(w.Turn, turn)) > 0; if (waiting) turn.TrySetCanceled(token); });
        return await turn.Task;
    }
    public void Release()
    {
        lock (sync)
        {
            if (waiters.Count == 0) { taken = false; return; }
            var next = waiters[0]; waiters.RemoveAt(0); next.Turn.TrySetResult(true);
        }
    }
}
