using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace MightyClaude.Core;

/// <summary>
/// The read-only files pane of docs/file-pane.md, ported from macOS
/// MightyCore/WorkspaceFiles.swift. It is held in the pane list as a
/// RunSession of its own kind that runs nothing, is never saved and never
/// comes back after a restart.
/// </summary>
public static class FilePaneKind
{
    public const string Kind = "files";

    /// <summary>macOS ⇧⌘E with ⌘ mapped to Ctrl.</summary>
    public const string Shortcut = "Ctrl+Shift+E";

    /// <summary>One files pane per workspace, so its id follows from the workspace id.</summary>
    public static string PaneId(string workspaceId) => Kind + ":" + workspaceId;
    public static bool IsFilePane(string? kind) => kind == Kind;

    /// <summary>
    /// What is written to disk: everything but files panes — not in the pane list, the layout
    /// trees or any selection (a restart then selects the workspace's first remaining pane).
    /// </summary>
    public static AppSnapshot Stored(AppSnapshot snapshot)
    {
        var files = snapshot.Sessions.Where(s => IsFilePane(s.Kind) || AgentIOPaneKind.IsAgentIO(s.Kind)).Select(s => s.Id).ToHashSet();
        if (files.Count == 0) return snapshot;
        var sessions = snapshot.Sessions.Where(s => !files.Contains(s.Id)).ToList();
        Dictionary<string, PaneLayoutNode>? layouts = null;
        if (snapshot.PaneLayouts is not null)
        {
            layouts = [];
            foreach (var (workspace, node) in snapshot.PaneLayouts)
                if (PaneLayout.Normalize(node, sessions.Where(s => s.WorkspaceId == workspace).Select(s => s.Id)) is { } kept) layouts[workspace] = kept;
        }
        return snapshot with
        {
            Sessions = sessions,
            PaneLayouts = layouts,
            ActiveSessionId = files.Contains(snapshot.ActiveSessionId ?? "") ? null : snapshot.ActiveSessionId,
            PaneLayoutActiveSessionIds = snapshot.PaneLayoutActiveSessionIds?.Where(pair => !files.Contains(pair.Value)).ToDictionary(pair => pair.Key, pair => pair.Value),
        };
    }

    /// <summary>
    /// Where a newly added files pane goes: left of the group holding the current pane
    /// (as on macOS), or a tab in that group when the layout has no room for a split.
    /// </summary>
    public static PaneLayoutNode Place(PaneLayoutNode tree, string paneId, string? currentSessionId)
    {
        var groups = PaneLayout.Groups(tree).ToList();
        var target = groups.FirstOrDefault(g => currentSessionId is not null && currentSessionId != paneId && g.SessionIds.Contains(currentSessionId))
            ?? groups.FirstOrDefault(g => g.SessionIds.Any(id => id != paneId));
        if (target is null) return tree;
        var split = PaneLayout.Move(tree, paneId, target.Id, "left");
        if (PaneLayout.Groups(split).Any(g => g.SessionIds.Count == 1 && g.SessionIds[0] == paneId)) return split;
        return PaneLayout.Move(tree, paneId, target.Id, "center");
    }
}

/// <summary>One row of the workspace tree. RelativePath uses '/' under the root as the user sees it.</summary>
public sealed record WorkspaceFileEntry(string Name, string RelativePath, bool IsDirectory, bool IsSymlink = false)
{
    /// <summary>Heavy or generated folders stay collapsed until the user opens them.</summary>
    public bool IsNoise => IsDirectory && WorkspaceFiles.IsNoiseFolder(Name);
}

public sealed record WorkspaceDirectoryListing(IReadOnlyList<WorkspaceFileEntry> Entries, bool Truncated);

public enum WorkspaceFileError { OutsideRoot, NotDirectory, Unreadable }
public enum WorkspaceFileOpenError { Missing, NotRegularFile, Unreadable }

public sealed class WorkspaceFileException(WorkspaceFileError error) : IOException(error.ToString())
{
    public WorkspaceFileError Error { get; } = error;
}

public sealed class WorkspaceFileOpenException(WorkspaceFileOpenError error) : IOException(error.ToString())
{
    public WorkspaceFileOpenError Error { get; } = error;
}

/// <summary>A regular file opened read-only under the root; read through Stream, never by path again.</summary>
public sealed record WorkspaceOpenFile(FileStream Stream, string Path, long Size, DateTimeOffset? Modified) : IDisposable
{
    public void Dispose() => Stream.Dispose();
}

/// <summary>
/// Read-only access to the files under a workspace root. Every path is resolved through
/// symlinks and junctions and refused unless its real path is the root or inside it, so
/// neither a ".." nor a link can reach other files.
/// </summary>
public static class WorkspaceFiles
{
    public static readonly IReadOnlySet<string> NoiseFolders = new HashSet<string>(StringComparer.Ordinal) { ".git", "node_modules", ".build", "build", "dist", "DerivedData", ".next", "Pods", ".venv", "__pycache__" };
    public const int MaximumEntriesPerFolder = 5_000;
    /// <summary>Names read from one folder before the rest are left unread and the listing is marked truncated.</summary>
    public const int MaximumEnumeratedNames = 20_000;
    public const int MaximumPathBytes = 4_096;
    private const int MaximumLinkHops = 40;
    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static bool IsNoiseFolder(string name) => NoiseFolders.Contains(name);

    /// <summary>Folders first, then files; each group in Finder/Explorer natural order ("file2" before "file10").</summary>
    public static IReadOnlyList<WorkspaceFileEntry> Sorted(IEnumerable<WorkspaceFileEntry> entries) =>
        entries.OrderBy(e => e.IsDirectory ? 0 : 1).ThenBy(e => e.Name, NaturalComparer.Instance).ThenBy(e => e.Name, StringComparer.Ordinal).ToList();

    /// <summary>Case-insensitive comparison in which digit runs compare by value.</summary>
    public sealed class NaturalComparer : IComparer<string>
    {
        public static readonly NaturalComparer Instance = new();
        public int Compare(string? x, string? y)
        {
            x ??= ""; y ??= "";
            int i = 0, j = 0;
            while (i < x.Length && j < y.Length)
            {
                if (char.IsAsciiDigit(x[i]) && char.IsAsciiDigit(y[j]))
                {
                    int si = i, sj = j;
                    while (i < x.Length && char.IsAsciiDigit(x[i])) i++;
                    while (j < y.Length && char.IsAsciiDigit(y[j])) j++;
                    var a = x[si..i].TrimStart('0'); var b = y[sj..j].TrimStart('0');
                    if (a.Length != b.Length) return a.Length.CompareTo(b.Length);
                    var digits = string.CompareOrdinal(a, b); if (digits != 0) return digits;
                    continue;
                }
                var order = string.Compare(x[i].ToString(), y[j].ToString(), StringComparison.OrdinalIgnoreCase);
                if (order != 0) return order;
                i++; j++;
            }
            return (x.Length - i).CompareTo(y.Length - j);
        }
    }

    /// <summary>
    /// The real path of <paramref name="path"/>: ".." collapsed first, then every symlink or
    /// junction on the way replaced by its target. Missing components stay as they are.
    /// Null when links loop.
    /// </summary>
    public static string? RealPath(string path)
    {
        var full = Path.GetFullPath(path);
        for (var hops = 0; ; )
        {
            var root = Path.GetPathRoot(full) ?? "";
            var parts = full[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
            var current = root; string? restart = null;
            for (var index = 0; index < parts.Length; index++)
            {
                var next = Path.Combine(current, parts[index]);
                if (!TryLinkTarget(next, out var target)) return null;
                if (target is not null)
                {
                    if (++hops > MaximumLinkHops) return null;
                    var resolved = Path.GetFullPath(target, current);
                    // Decided on paper, before anything reads the target: a link never leads off this machine.
                    if (!FollowsLinkTarget(resolved, path)) return null;
                    restart = Path.Combine(new[] { resolved }.Concat(parts[(index + 1)..]).ToArray());
                    break;
                }
                current = next;
            }
            if (restart is null) return current;
            full = Path.GetFullPath(restart);
        }
    }

    /// <summary>
    /// Whether RealPath may go on to a link's target (already made absolute) reached from
    /// <paramref name="start"/>. A UNC target (<c>\\server\share</c>, <c>//server/share</c>,
    /// <c>\\?\UNC\…</c>, <c>\\.\UNC\…</c>) is followed only when the start is on that same
    /// share, so a link inside a local workspace can never make the app reach another machine
    /// (and hand it the user's credentials) just by looking at the link.
    /// </summary>
    public static bool FollowsLinkTarget(string target, string start) =>
        UncShare(target) is not { } share || string.Equals(UncShare(start), share, StringComparison.OrdinalIgnoreCase);

    /// <summary>"server\share" of a UNC path, or null for any other path (a device path to a local volume included).</summary>
    public static string? UncShare(string path)
    {
        var p = path.Replace('/', '\\');
        string rest;
        if (p.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) || p.StartsWith(@"\\.\UNC\", StringComparison.OrdinalIgnoreCase)) rest = p[8..];
        else if (p.StartsWith(@"\\?\", StringComparison.Ordinal) || p.StartsWith(@"\\.\", StringComparison.Ordinal)) return null;
        else if (p.StartsWith(@"\\", StringComparison.Ordinal)) rest = p[2..];
        else return null;
        var parts = rest.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch { 0 => "", 1 => parts[0], _ => parts[0] + "\\" + parts[1] };
    }

    /// <summary>
    /// The link's target (null for a plain item or a missing one). False when the link data
    /// could not be read: the path is then refused rather than taken for a plain folder.
    /// </summary>
    private static bool TryLinkTarget(string path, out string? target)
    {
        target = null;
        try { target = new FileInfo(path).LinkTarget; return true; }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    /// <summary>Whether <paramref name="realPath"/> (already resolved) is the real root or inside it.</summary>
    public static bool Contains(string realPath, string realRoot)
    {
        var root = Path.TrimEndingDirectorySeparator(realRoot);
        var path = Path.TrimEndingDirectorySeparator(realPath);
        if (string.Equals(path, root, PathComparison)) return true;
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, PathComparison);
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    /// <summary>
    /// The real path of an existing item at <paramref name="relativePath"/> under the root
    /// ("" is the root itself), or null when it is missing or its real path leaves the root.
    /// Absolute paths, drive letters, NUL and Windows stream names (':') are refused.
    /// </summary>
    public static string? Resolve(string relativePath, string root)
    {
        if (relativePath.Contains('\0') || relativePath.Contains(':') || Encoding.UTF8.GetByteCount(relativePath) > MaximumPathBytes) return null;
        if (relativePath.StartsWith('/') || relativePath.StartsWith('\\') || Path.IsPathRooted(relativePath)) return null;
        if (!Path.IsPathFullyQualified(root) || RealPath(root) is not { } realRoot) return null;
        var candidate = relativePath.Length == 0 ? realRoot : RealPath(Path.Combine(realRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        return candidate is not null && Contains(candidate, realRoot) && Exists(candidate) ? candidate : null;
    }

    /// <summary>
    /// The folder's children, sorted, hidden files included. Children whose real path leaves
    /// the root (and dangling links) are left out. Only the first
    /// <paramref name="enumerationLimit"/> names on disk are looked at.
    /// </summary>
    public static WorkspaceDirectoryListing List(string relativePath, string root, int enumerationLimit = MaximumEnumeratedNames)
    {
        var directory = Resolve(relativePath, root) ?? throw new WorkspaceFileException(WorkspaceFileError.OutsideRoot);
        if (!Directory.Exists(directory)) throw new WorkspaceFileException(WorkspaceFileError.NotDirectory);
        var realRoot = RealPath(root)!;
        var names = new List<string>(); var unread = false;
        try
        {
            var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false, ReturnSpecialDirectories = false };
            foreach (var path in Directory.EnumerateFileSystemEntries(directory, "*", options))
            {
                if (names.Count == enumerationLimit) { unread = true; break; }
                names.Add(Path.GetFileName(path));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new WorkspaceFileException(WorkspaceFileError.Unreadable); }
        var entries = new List<WorkspaceFileEntry>();
        foreach (var name in names.Where(n => n.Length > 0 && n != "." && n != ".."))
        {
            var item = Path.Combine(directory, name);
            if (!TryLinkTarget(item, out var linkTarget)) continue;
            var link = linkTarget is not null;
            var real = link ? RealPath(item) : item;
            if (real is null || !Contains(real, realRoot) || !Exists(real)) continue;
            entries.Add(new(name, relativePath.Length == 0 ? name : relativePath + "/" + name, Directory.Exists(real), link));
        }
        var ordered = Sorted(entries);
        return new(ordered.Take(MaximumEntriesPerFolder).ToList(), unread || ordered.Count > MaximumEntriesPerFolder);
    }

    /// <summary>
    /// Opens the file at <paramref name="relativePath"/> for reading: resolved under the root,
    /// refused when its last component is (now) a link, required to be a regular file, and on
    /// Windows its final path (from the open handle) checked against the root once more.
    /// </summary>
    public static WorkspaceOpenFile OpenFile(string relativePath, string root)
    {
        var path = Resolve(relativePath, root) ?? throw new WorkspaceFileOpenException(WorkspaceFileOpenError.Missing);
        if (!TryLinkTarget(path, out var swapped) || swapped is not null) throw new WorkspaceFileOpenException(WorkspaceFileOpenError.Missing);
        if (Directory.Exists(path)) throw new WorkspaceFileOpenException(WorkspaceFileOpenError.NotRegularFile);
        FileStream stream;
        try { stream = new FileStream(path, new FileStreamOptions { Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.ReadWrite | FileShare.Delete }); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { throw new WorkspaceFileOpenException(WorkspaceFileOpenError.Missing); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) { throw new WorkspaceFileOpenException(WorkspaceFileOpenError.Unreadable); }
        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (WindowsFileHandles.FileType(stream.SafeFileHandle) != WindowsFileHandles.FileTypeDisk) throw new WorkspaceFileOpenException(WorkspaceFileOpenError.NotRegularFile);
                var final = WindowsFileHandles.FinalPath(stream.SafeFileHandle);
                var canonicalRoot = WindowsFileHandles.FinalDirectoryPath(RealPath(root)!);
                if (final is null || canonicalRoot is null) throw new WorkspaceFileOpenException(WorkspaceFileOpenError.Unreadable);
                if (!Contains(final, canonicalRoot)) throw new WorkspaceFileOpenException(WorkspaceFileOpenError.Missing);
            }
            else if (RealPath(path) is not { } again || !Contains(again, RealPath(root)!)) throw new WorkspaceFileOpenException(WorkspaceFileOpenError.Missing);
            var info = new FileInfo(path);
            return new(stream, path, stream.Length, info.Exists ? new DateTimeOffset(info.LastWriteTimeUtc) : null);
        }
        catch { stream.Dispose(); throw; }
    }

    /// <summary>Win32 handle queries: the kind of an open handle and the path it really points at.</summary>
    private static class WindowsFileHandles
    {
        internal const uint FileTypeDisk = 1;
        [DllImport("kernel32.dll", SetLastError = true)] private static extern uint GetFileType(SafeFileHandle handle);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, char[] buffer, uint length, uint flags);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

        internal static uint FileType(SafeFileHandle handle) => GetFileType(handle);

        internal static string? FinalPath(SafeFileHandle handle)
        {
            var buffer = new char[32768];
            var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
            if (length == 0 || length >= buffer.Length) return null;
            var value = new string(buffer, 0, (int)length);
            if (value.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) return @"\\" + value[8..];
            return value.StartsWith(@"\\?\", StringComparison.Ordinal) ? value[4..] : value;
        }

        /// <summary>The final path of a folder, opened with backup semantics so a directory handle exists.</summary>
        internal static string? FinalDirectoryPath(string path)
        {
            const uint shareAll = 7, openExisting = 3, backupSemantics = 0x02000000;
            using var handle = CreateFileW(path, 0, shareAll, 0, openExisting, backupSemantics, 0);
            return handle.IsInvalid ? null : FinalPath(handle);
        }
    }
}

/// <summary>The text encodings the preview reads, in the order macOS tries them.</summary>
public enum TextEncodingKind { Utf8, Utf8Bom, Utf16LE, Utf16BE, Utf32LE, Utf32BE, Cp949 }

public static class TextEncodings
{
    static TextEncodings() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>Shown in the preview header; the same names as macOS.</summary>
    public static string DisplayName(TextEncodingKind kind) => kind switch
    {
        TextEncodingKind.Utf8 => "UTF-8",
        TextEncodingKind.Utf8Bom => "UTF-8 BOM",
        TextEncodingKind.Utf16LE => "UTF-16 LE",
        TextEncodingKind.Utf16BE => "UTF-16 BE",
        TextEncodingKind.Utf32LE => "UTF-32 LE",
        TextEncodingKind.Utf32BE => "UTF-32 BE",
        _ => "CP949 (EUC-KR)",
    };

    public static byte[] ByteOrderMark(TextEncodingKind kind) => kind switch
    {
        TextEncodingKind.Utf8Bom => [0xEF, 0xBB, 0xBF],
        TextEncodingKind.Utf16LE => [0xFF, 0xFE],
        TextEncodingKind.Utf16BE => [0xFE, 0xFF],
        TextEncodingKind.Utf32LE => [0xFF, 0xFE, 0x00, 0x00],
        TextEncodingKind.Utf32BE => [0x00, 0x00, 0xFE, 0xFF],
        _ => [],
    };

    /// <summary>A decoder that throws on bytes it cannot read.</summary>
    public static Encoding Strict(TextEncodingKind kind) => kind switch
    {
        TextEncodingKind.Utf8 or TextEncodingKind.Utf8Bom => new UTF8Encoding(false, true),
        TextEncodingKind.Utf16LE => new UnicodeEncoding(false, false, true),
        TextEncodingKind.Utf16BE => new UnicodeEncoding(true, false, true),
        TextEncodingKind.Utf32LE => new UTF32Encoding(false, false, true),
        TextEncodingKind.Utf32BE => new UTF32Encoding(true, false, true),
        _ => Encoding.GetEncoding(949, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback),
    };

    /// <summary>Bytes a cut sample may leave of a character at its end.</summary>
    internal static int TailSlack(TextEncodingKind kind) => kind == TextEncodingKind.Cp949 ? 1 : 3;

    /// <summary>The encoding named by the data's byte order mark (UTF-32 before UTF-16, whose LE mark it starts with).</summary>
    public static TextEncodingKind? FromByteOrderMark(ReadOnlySpan<byte> data)
    {
        foreach (var kind in new[] { TextEncodingKind.Utf32LE, TextEncodingKind.Utf32BE, TextEncodingKind.Utf8Bom, TextEncodingKind.Utf16LE, TextEncodingKind.Utf16BE })
            if (data.StartsWith(ByteOrderMark(kind))) return kind;
        return null;
    }
}

/// <summary>The source languages the preview highlights; Plain is text without highlighting.</summary>
public enum SourceLanguage { Swift, C, JavaScript, Python, Kotlin, Java, Go, Rust, Shell, Json, Yaml, Toml, Xml, Html, Css, Sql, Gradle, Dockerfile, Makefile, Plain }

public enum FilePreviewKindTag { Markdown, Source, Image, Unsupported }

public sealed record FilePreviewKind(FilePreviewKindTag Tag, SourceLanguage Language = SourceLanguage.Plain)
{
    public static readonly FilePreviewKind Markdown = new(FilePreviewKindTag.Markdown);
    public static readonly FilePreviewKind Image = new(FilePreviewKindTag.Image);
    public static readonly FilePreviewKind Unsupported = new(FilePreviewKindTag.Unsupported);
    public static FilePreviewKind Source(SourceLanguage language) => new(FilePreviewKindTag.Source, language);
}

public sealed record DecodedText(string Text, TextEncodingKind Encoding);

/// <summary>
/// Decides how a file is previewed: by name first, then by sniffing its first bytes. A text
/// kind still needs text bytes, so a binary .plist or a mislabelled file is unsupported.
/// </summary>
public static class FilePreviewClassifier
{
    public const int MaximumTextBytes = 1_048_576;
    public const long MaximumImageBytes = 50L * 1_048_576;
    public const int MaximumFitPixels = 4_096;
    public const long MaximumFullPixels = 100_000_000;
    public const long MaximumDecodePixels = 250_000_000;
    public const double MaximumVectorPoints = 10_000_000;
    public const double MaximumZoomPoints = 16_384;
    public const int SniffBytes = 8_192;
    /// <summary>Markdown above this many bytes is shown as source.</summary>
    public const int MaximumMarkdownRenderBytes = 131_072;

    private static readonly HashSet<string> MarkdownExtensions = ["md", "markdown", "mdx"];
    private static readonly HashSet<string> ImageExtensions = ["png", "jpg", "jpeg", "gif", "webp", "heic", "heif", "tif", "tiff", "bmp", "ico", "svg", "pdf"];
    private static readonly Dictionary<string, SourceLanguage> SourceExtensions = new()
    {
        ["swift"] = SourceLanguage.Swift,
        ["c"] = SourceLanguage.C, ["h"] = SourceLanguage.C, ["cc"] = SourceLanguage.C, ["cpp"] = SourceLanguage.C, ["cxx"] = SourceLanguage.C, ["hpp"] = SourceLanguage.C, ["hh"] = SourceLanguage.C, ["m"] = SourceLanguage.C, ["mm"] = SourceLanguage.C,
        ["js"] = SourceLanguage.JavaScript, ["jsx"] = SourceLanguage.JavaScript, ["mjs"] = SourceLanguage.JavaScript, ["cjs"] = SourceLanguage.JavaScript, ["ts"] = SourceLanguage.JavaScript, ["tsx"] = SourceLanguage.JavaScript, ["mts"] = SourceLanguage.JavaScript, ["cts"] = SourceLanguage.JavaScript,
        ["py"] = SourceLanguage.Python, ["pyi"] = SourceLanguage.Python,
        ["kt"] = SourceLanguage.Kotlin, ["kts"] = SourceLanguage.Kotlin, ["java"] = SourceLanguage.Java, ["go"] = SourceLanguage.Go, ["rs"] = SourceLanguage.Rust,
        ["sh"] = SourceLanguage.Shell, ["bash"] = SourceLanguage.Shell, ["zsh"] = SourceLanguage.Shell, ["fish"] = SourceLanguage.Shell,
        ["json"] = SourceLanguage.Json, ["jsonc"] = SourceLanguage.Json, ["yaml"] = SourceLanguage.Yaml, ["yml"] = SourceLanguage.Yaml, ["toml"] = SourceLanguage.Toml,
        ["xml"] = SourceLanguage.Xml, ["plist"] = SourceLanguage.Xml, ["xib"] = SourceLanguage.Xml, ["storyboard"] = SourceLanguage.Xml, ["entitlements"] = SourceLanguage.Xml,
        ["html"] = SourceLanguage.Html, ["htm"] = SourceLanguage.Html, ["css"] = SourceLanguage.Css, ["scss"] = SourceLanguage.Css, ["less"] = SourceLanguage.Css, ["sql"] = SourceLanguage.Sql, ["gradle"] = SourceLanguage.Gradle,
        ["txt"] = SourceLanguage.Plain, ["log"] = SourceLanguage.Plain, ["csv"] = SourceLanguage.Plain, ["tsv"] = SourceLanguage.Plain, ["ini"] = SourceLanguage.Plain, ["cfg"] = SourceLanguage.Plain, ["conf"] = SourceLanguage.Plain, ["properties"] = SourceLanguage.Plain, ["env"] = SourceLanguage.Shell,
    };

    /// <summary>The lower-cased extension; a leading dot (".env") is not one.</summary>
    public static string FileExtension(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot <= 0 ? "" : name[(dot + 1)..].ToLowerInvariant();
    }

    /// <summary>The kind from the file name alone, or null when only the bytes can tell.</summary>
    public static FilePreviewKind? KindForName(string name)
    {
        var ext = FileExtension(name);
        if (MarkdownExtensions.Contains(ext)) return FilePreviewKind.Markdown;
        if (ImageExtensions.Contains(ext)) return FilePreviewKind.Image;
        var lower = name.ToLowerInvariant();
        if (lower == "dockerfile" || lower.StartsWith("dockerfile.", StringComparison.Ordinal) || ext == "dockerfile") return FilePreviewKind.Source(SourceLanguage.Dockerfile);
        if (lower is "makefile" or "gnumakefile" || ext == "mk") return FilePreviewKind.Source(SourceLanguage.Makefile);
        if (lower == ".env" || lower.StartsWith(".env.", StringComparison.Ordinal)) return FilePreviewKind.Source(SourceLanguage.Shell);
        return SourceExtensions.TryGetValue(ext, out var language) ? FilePreviewKind.Source(language) : null;
    }

    /// <summary>The preview kind for <paramref name="name"/> given its first bytes (up to SniffBytes).</summary>
    public static FilePreviewKind Classify(string name, ReadOnlySpan<byte> head) => KindForName(name) switch
    {
        { Tag: FilePreviewKindTag.Markdown } => LooksLikeText(head) ? FilePreviewKind.Markdown : FilePreviewKind.Unsupported,
        { Tag: FilePreviewKindTag.Image } => FilePreviewKind.Image,
        { Tag: FilePreviewKindTag.Source } kind => LooksLikeText(head) ? kind : FilePreviewKind.Unsupported,
        _ => LooksLikeText(head) ? FilePreviewKind.Source(SourceLanguage.Plain) : FilePreviewKind.Unsupported,
    };

    /// <summary>Text in one of the encodings. Without a UTF-16/32 byte order mark a NUL byte means binary.</summary>
    public static bool LooksLikeText(ReadOnlySpan<byte> data) => DecodeText(data, sample: true) is not null;

    /// <summary>
    /// The text and its encoding: the byte order mark's encoding, else UTF-8, else CP949; null
    /// when none decodes cleanly (or a NUL shows binary). With sample the data may end inside a character.
    /// </summary>
    public static DecodedText? DecodeText(ReadOnlySpan<byte> data, bool sample)
    {
        if (TextEncodings.FromByteOrderMark(data) is { } marked)
            return Decode(data[TextEncodings.ByteOrderMark(marked).Length..], marked, sample) is { } text ? new(text, marked) : null;
        if (data.Contains((byte)0)) return null;
        foreach (var kind in new[] { TextEncodingKind.Utf8, TextEncodingKind.Cp949 })
            if (Decode(data, kind, sample) is { } text) return new(text, kind);
        return null;
    }

    private static string? Decode(ReadOnlySpan<byte> data, TextEncodingKind kind, bool sample)
    {
        var encoding = TextEncodings.Strict(kind);
        for (var drop = 0; drop <= (sample ? Math.Min(TextEncodings.TailSlack(kind), data.Length) : 0); drop++)
        {
            try { return encoding.GetString(data[..^drop]); }
            catch (DecoderFallbackException) { }
            catch (ArgumentException) { }
        }
        return null;
    }

    /// <summary>Decodes whatever is there, replacing bad sequences.</summary>
    private static string DecodeLossily(ReadOnlySpan<byte> data, TextEncodingKind kind) => kind switch
    {
        TextEncodingKind.Utf16LE => new UnicodeEncoding(false, false).GetString(data[..(data.Length & ~1)]),
        TextEncodingKind.Utf16BE => new UnicodeEncoding(true, false).GetString(data[..(data.Length & ~1)]),
        TextEncodingKind.Utf32LE => new UTF32Encoding(false, false).GetString(data[..(data.Length & ~3)]),
        TextEncodingKind.Utf32BE => new UTF32Encoding(true, false).GetString(data[..(data.Length & ~3)]),
        _ => new UTF8Encoding(false, false).GetString(data),
    };

    public sealed record TextRead(string Text, bool Truncated, TextEncodingKind Encoding);

    /// <summary>
    /// The first <paramref name="maximumBytes"/> of a text file read from its start, in the
    /// encoding DecodeText finds. Bytes that no longer decode become replacement characters.
    /// </summary>
    public static TextRead ReadText(Stream stream, int maximumBytes = MaximumTextBytes)
    {
        var data = ReadPrefix(stream, maximumBytes + 1);
        var truncated = data.Length > maximumBytes;
        var body = truncated ? data.AsSpan(0, maximumBytes) : data.AsSpan();
        if (DecodeText(body, sample: true) is { } decoded) return new(decoded.Text, truncated, decoded.Encoding);
        var fallback = TextEncodings.FromByteOrderMark(body) ?? TextEncodingKind.Utf8;
        return new(DecodeLossily(body[TextEncodings.ByteOrderMark(fallback).Length..], fallback), truncated, fallback);
    }

    /// <summary>The first SniffBytes of a file, read from its start.</summary>
    public static byte[] ReadHead(Stream stream) => ReadPrefix(stream, SniffBytes);

    public static byte[] ReadPrefix(Stream stream, int count, CancellationToken cancellation = default)
    {
        stream.Seek(0, SeekOrigin.Begin);
        var buffer = new byte[Math.Max(0, (int)Math.Min(count, Math.Max(0, stream.Length) + 1))];
        var read = 0;
        while (read < buffer.Length)
        {
            cancellation.ThrowIfCancellationRequested();
            var chunk = stream.Read(buffer, read, Math.Min(buffer.Length - read, 1 << 20));
            if (chunk == 0) break;
            read += chunk;
        }
        return read == buffer.Length ? buffer : buffer[..read];
    }

    /// <summary>width × height, or null when either is not positive or the product overflows.</summary>
    public static long? PixelCount(long width, long height) => width > 0 && height > 0 && width <= long.MaxValue / height ? width * height : null;

    /// <summary>Whether an svg's or pdf's size in points may be drawn: finite, positive and at most MaximumVectorPoints.</summary>
    public static bool IsDrawable(double width, double height) =>
        double.IsFinite(width) && double.IsFinite(height) && width > 0 && height > 0 && width <= MaximumVectorPoints && height <= MaximumVectorPoints;

    /// <summary>
    /// An svg's size in CSS pixels from its root element: width and height in absolute units
    /// (px, pt, pc, in, cm, mm or none), a missing one taken from the viewBox; null when neither
    /// says (percentages, em or no root element), so the caller can apply IsDrawable as for pdf.
    /// </summary>
    public static (double Width, double Height)? SvgSize(ReadOnlySpan<byte> data)
    {
        if (DecodeText(data[..Math.Min(data.Length, 65_536)], sample: true) is not { } decoded) return null;
        var tag = Regex.Match(decoded.Text, @"<svg\b[^>]*>", RegexOptions.IgnoreCase);
        if (!tag.Success) return null;
        double? Attribute(string name)
        {
            var match = Regex.Match(tag.Value, @"\s" + name + @"\s*=\s*([""'])\s*([^""']*?)\s*\1", RegexOptions.IgnoreCase);
            return match.Success ? SvgLength(match.Groups[2].Value) : null;
        }
        var width = Attribute("width"); var height = Attribute("height");
        if (width is { } w && height is { } h) return (w, h);
        var box = Regex.Match(tag.Value, @"\sviewBox\s*=\s*([""'])([^""']*)\1", RegexOptions.IgnoreCase);
        if (!box.Success) return null;
        var parts = box.Groups[2].Value.Split([' ', ',', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4 || !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var boxWidth)
            || !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var boxHeight)) return null;
        return (width ?? boxWidth, height ?? boxHeight);
    }

    private static double? SvgLength(string value)
    {
        var match = Regex.Match(value, @"^([+-]?(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?)\s*(px|pt|pc|in|cm|mm)?$", RegexOptions.IgnoreCase);
        if (!match.Success || !double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return null;
        return number * match.Groups[2].Value.ToLowerInvariant() switch { "pt" => 96.0 / 72, "pc" => 16, "in" => 96, "cm" => 96 / 2.54, "mm" => 96 / 25.4, _ => 1 };
    }

    /// <summary>
    /// Whether drawing this svg could read anything but its own bytes — the same deliberately
    /// conservative rule as macOS: any href/src value or CSS url( that is neither a #fragment
    /// nor a non-svg data: URL, any @import, image-set(, xml:base, &lt;!ENTITY, DOCTYPE with an
    /// identifier or internal subset, CSS backslash escape, unreadable declared encoding or
    /// bytes that do not decode as text all count as external.
    /// </summary>
    public static bool SvgLoadsExternalContent(ReadOnlySpan<byte> data)
    {
        if (DecodeText(data, sample: false) is not { } decoded) return true;
        var text = Encoding.UTF8.GetBytes(SvgReferences.DecodingCharacterReferences(decoded.Text).ToLowerInvariant());
        if (Array.IndexOf(text, (byte)'\\') >= 0) return true;
        foreach (var token in new[] { "<!entity", "@import", "image-set(", "xml:base" })
            if (SvgReferences.Find(token, text).Count > 0) return true;
        foreach (var start in SvgReferences.Find("<!doctype", text))
        {
            var end = Array.IndexOf(text, (byte)'>', start);
            if (end < 0) return true;
            var doctype = text[start..end];
            if (new[] { "system", "public", "[" }.Any(word => SvgReferences.Find(word, doctype).Count > 0)) return true;
        }
        if (!SvgReferences.ReadableDeclaration(text, decoded.Encoding)) return true;
        foreach (var name in new[] { "href", "src", "srcset" })
        {
            foreach (var start in SvgReferences.Find(name, text))
            {
                var index = SvgReferences.SkipSpace(text, start + name.Length);
                if (index >= text.Length || text[index] != (byte)'=') continue;
                if (!SvgReferences.IsLocal(text, index + 1)) return true;
            }
        }
        foreach (var start in SvgReferences.Find("url(", text))
            if (!SvgReferences.IsLocal(text, start + 4)) return true;
        return false;
    }
}

/// <summary>The byte-level pieces of FilePreviewClassifier.SvgLoadsExternalContent.</summary>
internal static class SvgReferences
{
    internal static List<int> Find(string needle, byte[] text)
    {
        var pattern = Encoding.UTF8.GetBytes(needle); var found = new List<int>();
        if (pattern.Length == 0) return found;
        for (var offset = 0; offset <= text.Length - pattern.Length;)
        {
            var hit = text.AsSpan(offset).IndexOf(pattern);
            if (hit < 0) break;
            found.Add(offset + hit); offset += hit + 1;
        }
        return found;
    }

    internal static bool IsSpace(byte value) => value is 0x20 or 0x09 or 0x0A or 0x0D or 0x0C;

    internal static int SkipSpace(byte[] text, int start)
    {
        var index = start;
        while (index < text.Length && IsSpace(text[index])) index++;
        return index;
    }

    /// <summary>Whether the reference at <paramref name="start"/> stays inside the document: #fragment or a non-svg/xml/html data: URL.</summary>
    internal static bool IsLocal(byte[] text, int start)
    {
        var index = SkipSpace(text, start);
        if (index < text.Length && text[index] is (byte)'"' or (byte)'\'') index = SkipSpace(text, index + 1);
        if (index >= text.Length) return false;
        if (text[index] == (byte)'#') return true;
        var scheme = "data:"u8;
        if (text.Length - index <= scheme.Length || !text.AsSpan(index, scheme.Length).SequenceEqual(scheme)) return false;
        var restStart = index + scheme.Length;
        var rest = text.AsSpan(restStart, Math.Min(256, text.Length - restStart));
        var comma = rest.IndexOf((byte)',');
        if (comma < 0) return false;
        var media = rest[..comma].ToArray().Where(b => !IsSpace(b)).ToArray();
        return new[] { "svg", "xml", "html" }.All(word => Find(word, media).Count == 0);
    }

    /// <summary>An XML declaration's encoding is one whose markup is plain ASCII bytes (or the byte order mark's own).</summary>
    internal static bool ReadableDeclaration(byte[] text, TextEncodingKind bom)
    {
        var start = SkipSpace(text, 0);
        var opening = "<?xml"u8;
        if (text.Length - start < opening.Length || !text.AsSpan(start, opening.Length).SequenceEqual(opening)) return true;
        var declaration = text.AsSpan(start, Math.Min(512, text.Length - start)).ToArray();
        var closes = Find("?>", declaration);
        if (closes.Count == 0) return false;
        var head = declaration[..closes[0]];
        var keys = Find("encoding", head);
        if (keys.Count == 0) return true;
        var index = SkipSpace(head, keys[0] + 8);
        if (index >= head.Length || head[index] != (byte)'=') return false;
        index = SkipSpace(head, index + 1);
        if (index >= head.Length || head[index] is not ((byte)'"' or (byte)'\'')) return false;
        var quote = head[index];
        var end = Array.IndexOf(head, quote, index + 1);
        if (end < 0) return false;
        var name = Encoding.UTF8.GetString(head, index + 1, end - index - 1);
        var readable = new HashSet<string> { "utf-8", "utf8", "us-ascii", "ascii", "iso-8859-1", "latin1" };
        if (bom is TextEncodingKind.Utf16LE or TextEncodingKind.Utf16BE) readable.UnionWith(["utf-16", "utf-16le", "utf-16be"]);
        if (bom is TextEncodingKind.Utf32LE or TextEncodingKind.Utf32BE) readable.UnionWith(["utf-32", "utf-32le", "utf-32be"]);
        return readable.Contains(name);
    }

    /// <summary>The text with &amp;#NN;, &amp;#xHH; and the five predefined entities replaced, as the XML parser reads them.</summary>
    internal static string DecodingCharacterReferences(string text)
    {
        if (!text.Contains('&')) return text;
        var runes = text.EnumerateRunes().ToArray();
        var output = new StringBuilder();
        for (var index = 0; index < runes.Length;)
        {
            if (runes[index].Value == '&' && Reference(runes, index + 1) is { } found) { output.Append(found.Value.ToString()); index = found.Next; }
            else { output.Append(runes[index].ToString()); index++; }
        }
        return output.ToString();
    }

    private static (Rune Value, int Next)? Reference(Rune[] runes, int start)
    {
        var index = start;
        if (index < runes.Length && runes[index].Value == '#')
        {
            index++;
            var radix = 10;
            if (index < runes.Length && runes[index].Value is 'x' or 'X') { radix = 16; index++; }
            long value = 0; var digits = 0;
            while (index < runes.Length && HexValue(runes[index].Value) is { } digit && digit < radix)
            {
                value = Math.Min(value * radix + digit, 0x11_0000); digits++; index++;
            }
            if (digits == 0 || index >= runes.Length || runes[index].Value != ';' || !Rune.IsValid((int)value)) return null;
            return (new Rune((int)value), index + 1);
        }
        var name = new StringBuilder();
        while (index < runes.Length && name.Length < 5 && Rune.IsLetter(runes[index])) { name.Append(runes[index].ToString()); index++; }
        if (index >= runes.Length || runes[index].Value != ';') return null;
        return name.ToString().ToLowerInvariant() switch
        {
            "amp" => (new Rune('&'), index + 1),
            "lt" => (new Rune('<'), index + 1),
            "gt" => (new Rune('>'), index + 1),
            "quot" => (new Rune('"'), index + 1),
            "apos" => (new Rune('\''), index + 1),
            _ => null,
        };
    }

    private static int? HexValue(int value) => value switch
    {
        >= '0' and <= '9' => value - '0',
        >= 'a' and <= 'f' => value - 'a' + 10,
        >= 'A' and <= 'F' => value - 'A' + 10,
        _ => null,
    };
}

/// <summary>A coloured span in a source preview, in UTF-16 offsets.</summary>
public sealed record SourceToken(SourceTokenKind Kind, int Location, int Length);
public enum SourceTokenKind { Keyword, String, Comment, Number }

/// <summary>Where lines start in a text: \n, \r, \r\n (one break), U+2028 or U+2029 — as macOS counts them.</summary>
public static class SourceLines
{
    /// <summary>Longer lines make the source view wrap instead of scrolling sideways.</summary>
    public const int WrapThreshold = 5_000;

    public static (List<int> Starts, int Longest) Scan(string text)
    {
        var starts = new List<int> { 0 }; int longest = 0, offset = 0, lineStart = 0; char previous = '\0';
        foreach (var unit in text)
        {
            offset++;
            if (unit == '\n' && previous == '\r') { starts[^1] = offset; lineStart = offset; }
            else if (unit is '\n' or '\r' or '\u2028' or '\u2029') { longest = Math.Max(longest, offset - 1 - lineStart); starts.Add(offset); lineStart = offset; }
            previous = unit;
        }
        return (starts, Math.Max(longest, offset - lineStart));
    }
}

/// <summary>
/// The single-pass highlighter of macOS SourceHighlighter.swift: comments, strings, numbers
/// and keywords. It never fails. Only the first MaximumUnits UTF-16 units are scanned.
/// </summary>
public static class SourceHighlighter
{
    public const int MaximumUnits = 400_000;

    private sealed record Rules(string[] LineComments, (string Open, string Close)? BlockComment, char[] Quotes, char[] MultilineQuotes, bool TripleQuotes, HashSet<string> Keywords, bool CaseInsensitive = false);

    public static List<SourceToken> Tokens(string text, SourceLanguage language, int maximumUnits = MaximumUnits)
    {
        var result = new List<SourceToken>();
        if (language == SourceLanguage.Plain) return result;
        var rules = RulesFor(language);
        var units = text.Length > maximumUnits ? text[..maximumUnits] : text;
        var count = units.Length;
        var index = 0;
        bool Matches(string pattern, int position) => position + pattern.Length <= count && string.CompareOrdinal(units, position, pattern, 0, pattern.Length) == 0;
        int? Find(string pattern, int position) { var hit = units.IndexOf(pattern, position, StringComparison.Ordinal); return hit < 0 ? null : hit; }
        int LineEnd(int position) { var hit = units.IndexOf('\n', position); return hit < 0 ? count : hit; }
        void Add(SourceTokenKind kind, int start, int end) { if (end > start) result.Add(new(kind, start, end - start)); }

        while (index < count)
        {
            var unit = units[index];
            if (rules.BlockComment is { } block && Matches(block.Open, index))
            {
                var end = Find(block.Close, index + block.Open.Length) is { } close ? close + block.Close.Length : count;
                Add(SourceTokenKind.Comment, index, end); index = end; continue;
            }
            var commented = false;
            foreach (var marker in rules.LineComments)
            {
                if (!Matches(marker, index)) continue;
                // "#" starts a comment only at a word boundary ("$#", "a#b" do not).
                if (marker == "#" && index > 0 && !IsSpace(units[index - 1])) break;
                var end = LineEnd(index);
                Add(SourceTokenKind.Comment, index, end); index = end; commented = true; break;
            }
            if (commented) continue;
            if (rules.TripleQuotes && unit == '"' && Matches("\"\"\"", index))
            {
                var end = Find("\"\"\"", index + 3) is { } close ? close + 3 : count;
                Add(SourceTokenKind.String, index, end); index = end; continue;
            }
            if (rules.Quotes.Contains(unit))
            {
                var cursor = index + 1;
                while (cursor < count)
                {
                    var current = units[cursor];
                    if (current == '\\') { cursor += 2; continue; }
                    if (current == unit) { cursor++; break; }
                    if (current == '\n' && !rules.MultilineQuotes.Contains(unit)) break;
                    cursor++;
                }
                var end = Math.Min(cursor, count);
                Add(SourceTokenKind.String, index, end); index = end; continue;
            }
            if (char.IsAsciiDigit(unit) && (index == 0 || !IsIdentifier(units[index - 1])))
            {
                var cursor = index + 1;
                while (cursor < count && (IsIdentifier(units[cursor]) || units[cursor] == '.' && cursor + 1 < count && char.IsAsciiDigit(units[cursor + 1]))) cursor++;
                Add(SourceTokenKind.Number, index, cursor); index = cursor; continue;
            }
            if (IsIdentifierStart(unit))
            {
                var cursor = index + 1;
                while (cursor < count && IsIdentifier(units[cursor])) cursor++;
                var word = units[index..cursor];
                if (rules.Keywords.Contains(rules.CaseInsensitive ? word.ToLowerInvariant() : word)) Add(SourceTokenKind.Keyword, index, cursor);
                index = cursor; continue;
            }
            index++;
        }
        return result;
    }

    private static bool IsIdentifierStart(char unit) => unit is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '_';
    private static bool IsIdentifier(char unit) => IsIdentifierStart(unit) || char.IsAsciiDigit(unit);
    private static bool IsSpace(char unit) => unit is ' ' or '\t' or '\n' or '\r';
    private static HashSet<string> Words(string text) => text.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);

    private static Rules RulesFor(SourceLanguage language)
    {
        string[] slash = ["//"]; (string, string) cBlock = ("/*", "*/"); char[] both = ['"', '\''];
        switch (language)
        {
            case SourceLanguage.Swift:
                return new(slash, cBlock, ['"'], [], true, Words("actor as associatedtype async await break case catch class continue default defer deinit do else enum extension fallthrough false fileprivate final for func guard if import in init inout internal is lazy let mutating nil nonisolated open operator override private protocol public repeat rethrows return self Self some static struct subscript super switch throw throws true try typealias var weak where while any"));
            case SourceLanguage.C:
                return new(slash, cBlock, both, [], false, Words("auto bool break case char class const constexpr continue default delete do double else enum explicit extern false float for friend goto if inline int long namespace new nullptr operator private protected public register return short signed sizeof static struct switch template this throw true try typedef typename union unsigned using virtual void volatile while NULL nil YES NO self super id"));
            case SourceLanguage.JavaScript:
                return new(slash, cBlock, ['"', '\'', '`'], ['`'], false, Words("abstract as async await break case catch class const continue debugger declare default delete do else enum export extends false finally for from function get if implements import in instanceof interface keyof let new null of private protected public readonly return set static super switch this throw true try type typeof undefined var void while yield"));
            case SourceLanguage.Python:
                return new(["#"], null, both, [], true, Words("False None True and as assert async await break class continue def del elif else except finally for from global if import in is lambda nonlocal not or pass raise return self try while with yield match case"));
            case SourceLanguage.Kotlin or SourceLanguage.Gradle:
                return new(slash, cBlock, both, [], true, Words("abstract annotation as break by catch class companion const constructor continue data do else enum false final finally for fun if import in init inline interface internal is lateinit null object open operator override package private protected public return sealed super suspend this throw true try typealias val var when while def apply plugins dependencies implementation"));
            case SourceLanguage.Java:
                return new(slash, cBlock, both, [], true, Words("abstract boolean break byte case catch char class const continue default do double else enum extends false final finally float for if implements import instanceof int interface long native new null package private protected public record return short static super switch synchronized this throw throws transient true try var void volatile while"));
            case SourceLanguage.Go:
                return new(slash, cBlock, ['"', '\'', '`'], ['`'], false, Words("break case chan const continue default defer else fallthrough false for func go goto if import interface iota map nil package range return select struct switch true type var"));
            case SourceLanguage.Rust:
                // '\'' is also a lifetime marker, so only double-quoted strings.
                return new(slash, cBlock, ['"'], ['"'], false, Words("as async await break const continue crate dyn else enum extern false fn for if impl in let loop match mod move mut pub ref return self Self static struct super trait true type unsafe use where while"));
            case SourceLanguage.Shell or SourceLanguage.Dockerfile or SourceLanguage.Makefile:
                var keywords = Words("if then else elif fi for while until do done case esac in function return export local readonly source echo exit set unset");
                if (language == SourceLanguage.Dockerfile) keywords.UnionWith(Words("from run cmd label expose env add copy entrypoint volume user workdir arg onbuild stopsignal healthcheck shell as"));
                if (language == SourceLanguage.Makefile) keywords.UnionWith(Words("ifeq ifneq ifdef ifndef endif include define endef override"));
                return new(["#"], null, both, both, false, keywords, language == SourceLanguage.Dockerfile);
            case SourceLanguage.Json:
                return new([], null, ['"'], [], false, Words("true false null"));
            case SourceLanguage.Yaml:
                return new(["#"], null, both, [], false, Words("true false null yes no on off"));
            case SourceLanguage.Toml:
                return new(["#"], null, both, [], true, Words("true false"));
            case SourceLanguage.Xml or SourceLanguage.Html:
                return new([], ("<!--", "-->"), ['"'], [], false, []);
            case SourceLanguage.Css:
                return new([], cBlock, both, [], false, Words("important inherit initial unset none auto"));
            case SourceLanguage.Sql:
                return new(["--"], cBlock, ['\'', '"'], [], false, Words("add all alter and as asc begin between by case check column commit constraint create database default delete desc distinct drop else end exists foreign from group having if in index inner insert into is join key left like limit not null on or order outer primary references right rollback select set table then union unique update values view when where with"), true);
            default:
                return new([], null, [], [], false, []);
        }
    }
}

/// <summary>
/// The workspace's tree as the pane shows it — the logic of macOS FilePaneModel without the
/// view: loaded listings, opened folders, captions, and the name filter that searches only
/// the root and the opened folders (never the disk), capped at MaximumFilterResults.
/// </summary>
public sealed class FilePaneTree
{
    public const int MaximumFilterResults = 2_000;
    public sealed record Row(WorkspaceFileEntry Entry, int Depth, bool IsExpanded, string? Caption);

    public Dictionary<string, IReadOnlyList<WorkspaceFileEntry>> Children { get; } = [];
    public HashSet<string> Truncated { get; } = [];
    public Dictionary<string, string> FolderErrors { get; } = [];
    public HashSet<string> Expanded { get; } = [];
    public string? SelectedPath { get; set; }
    /// <summary>The file last previewed, shown again when a closed pane reopens.</summary>
    public string? PreviewPath { get; set; }
    public string Filter { get; set; } = "";
    public bool IsFiltering => FilterNeedle.Length > 0;
    private string FilterNeedle => Filter.Trim();

    /// <summary>Records one folder's listing (or the reason it could not be read).</summary>
    public void Apply(string path, WorkspaceDirectoryListing? listing, WorkspaceFileError? error = null)
    {
        if (listing is not null)
        {
            Children[path] = listing.Entries; FolderErrors.Remove(path);
            if (listing.Truncated) Truncated.Add(path); else Truncated.Remove(path);
        }
        else
        {
            Children[path] = [];
            FolderErrors[path] = error == WorkspaceFileError.OutsideRoot ? Locale.Get("files.preview.missing") : Locale.Get("files.tree.unreadable");
        }
    }

    /// <summary>Loads one folder through WorkspaceFiles and applies it.</summary>
    public void Load(string path, string root)
    {
        try { Apply(path, WorkspaceFiles.List(path, root)); }
        catch (WorkspaceFileException ex) { Apply(path, null, ex.Error); }
    }

    /// <summary>Refresh: the root and the opened folders are read again; listings of closed folders are dropped.</summary>
    public IReadOnlyList<string> RefreshTargets()
    {
        foreach (var stale in Children.Keys.Where(path => path.Length > 0 && !Expanded.Contains(path)).ToList()) { Children.Remove(stale); Truncated.Remove(stale); FolderErrors.Remove(stale); }
        return [.. new[] { "" }.Concat(Expanded.OrderBy(p => p, StringComparer.Ordinal))];
    }

    public (IReadOnlyList<Row> Rows, bool HitCap) Rows()
    {
        var needle = FilterNeedle;
        if (needle.Length > 0)
        {
            var found = new List<Row>();
            foreach (var path in new[] { "" }.Concat(Expanded.OrderBy(p => p, StringComparer.Ordinal)))
                foreach (var entry in Children.GetValueOrDefault(path) ?? [])
                {
                    if (!entry.Name.Contains(needle, StringComparison.CurrentCultureIgnoreCase)) continue;
                    if (found.Count == MaximumFilterResults) return (found, true);
                    found.Add(new(entry, 0, false, null));
                }
            return (found, false);
        }
        var rows = new List<Row>();
        void Visit(string path, int depth)
        {
            foreach (var entry in Children.GetValueOrDefault(path) ?? [])
            {
                var open = entry.IsDirectory && Expanded.Contains(entry.RelativePath);
                rows.Add(new(entry, depth, open, open ? Caption(entry.RelativePath) : null));
                if (open && depth < 64) Visit(entry.RelativePath, depth + 1);
            }
        }
        Visit("", 0);
        return (rows, false);
    }

    public string? Caption(string path)
    {
        if (FolderErrors.TryGetValue(path, out var error)) return error;
        if (Children.TryGetValue(path, out var children) && children.Count == 0) return Locale.Get("files.tree.empty");
        if (Truncated.Contains(path)) return Locale.Get("files.tree.truncated", new Dictionary<string, string> { ["count"] = WorkspaceFiles.MaximumEntriesPerFolder.ToString() });
        return null;
    }

    /// <summary>
    /// Opening a folder found by the filter clears the filter and opens the folder and every
    /// folder above it, so it shows in the tree (macOS Return/→ on a filter result).
    /// </summary>
    public void Reveal(string path)
    {
        Filter = "";
        var parts = path.Split('/');
        for (var index = 1; index <= parts.Length; index++) Expanded.Add(string.Join('/', parts[..index]));
        SelectedPath = path;
    }
}

/// <summary>What the preview shows for one file, read off the UI thread.</summary>
public sealed record FilePreviewData(
    string Name, string RelativePath, long Size, DateTimeOffset? Modified, FilePreviewKind Kind,
    string? Text = null, TextEncodingKind? Encoding = null, bool Truncated = false,
    bool MarkdownRenderable = false, byte[]? ImageBytes = null, string? Reason = null)
{
    /// <summary>The preview could not be read at all: the "missing" or "failed" message.</summary>
    public string? Failure { get; init; }
}

/// <summary>
/// How much the Windows files pane draws at once. WinUI makes an element for every tree row
/// and a text run for every coloured span, without virtualization, so a huge expanded tree
/// or a long file is cut here and a note says so.
/// </summary>
public static class FilePaneDrawing
{
    public const int MaximumTreeRows = 2_000;
    /// <summary>256 KB of UTF-16 units; the reader itself stops at 1 MB.</summary>
    public const int MaximumSourceUnits = 262_144;

    /// <summary>The first MaximumTreeRows rows, and whether any were left out.</summary>
    public static (IReadOnlyList<T> Rows, bool Capped) TreeRows<T>(IReadOnlyList<T> rows) =>
        rows.Count <= MaximumTreeRows ? (rows, false) : (rows.Take(MaximumTreeRows).ToList(), true);

    /// <summary>
    /// The text drawn: all of it when short; otherwise up to the last line break before the
    /// cap (so no line is cut), or the cap itself when one line is longer — never between the
    /// halves of a surrogate pair.
    /// </summary>
    public static (string Text, bool Capped) SourceText(string text)
    {
        if (text.Length <= MaximumSourceUnits) return (text, false);
        var cut = text.LastIndexOf('\n', MaximumSourceUnits - 1);
        if (cut > 0) return (text[..cut].TrimEnd('\r'), true);
        cut = MaximumSourceUnits;
        if (char.IsHighSurrogate(text[cut - 1])) cut--;
        return (text[..cut], true);
    }
}

public static class FilePreviewLoader
{
    /// <summary>
    /// Reads one file for the preview: classified from its first bytes; text read up to 1 MB
    /// in the encoding found; markdown over 128 KB marked as source only; images and svg kept
    /// as bytes under 50 MB, svg refused when it can load outside content.
    /// </summary>
    public static FilePreviewData Load(string root, string relativePath, CancellationToken cancellation = default)
    {
        var name = relativePath.Split('/')[^1];
        WorkspaceOpenFile file;
        try { file = WorkspaceFiles.OpenFile(relativePath, root); }
        catch (WorkspaceFileOpenException ex)
        {
            return ex.Error switch
            {
                WorkspaceFileOpenError.Missing => new(name, relativePath, 0, null, FilePreviewKind.Unsupported) { Failure = Locale.Get("files.preview.missing") },
                WorkspaceFileOpenError.NotRegularFile => new(name, relativePath, 0, null, FilePreviewKind.Unsupported),
                _ => new(name, relativePath, 0, null, FilePreviewKind.Unsupported) { Failure = Locale.Get("files.preview.failed") },
            };
        }
        using (file)
        {
            try
            {
                cancellation.ThrowIfCancellationRequested();
                var kind = FilePreviewClassifier.Classify(name, FilePreviewClassifier.ReadHead(file.Stream));
                cancellation.ThrowIfCancellationRequested();
                switch (kind.Tag)
                {
                    case FilePreviewKindTag.Image:
                        if (file.Size > FilePreviewClassifier.MaximumImageBytes)
                            return new(name, relativePath, file.Size, file.Modified, FilePreviewKind.Unsupported, Reason: Locale.Get("files.preview.tooLargeImage"));
                        var bytes = FilePreviewClassifier.ReadPrefix(file.Stream, (int)FilePreviewClassifier.MaximumImageBytes, cancellation);
                        if (FilePreviewClassifier.FileExtension(name) == "svg" && FilePreviewClassifier.SvgLoadsExternalContent(bytes))
                            return new(name, relativePath, file.Size, file.Modified, FilePreviewKind.Unsupported, Reason: Locale.Get("files.preview.svgExternal"));
                        return new(name, relativePath, file.Size, file.Modified, kind, ImageBytes: bytes);
                    case FilePreviewKindTag.Source or FilePreviewKindTag.Markdown:
                        var text = FilePreviewClassifier.ReadText(file.Stream);
                        return new(name, relativePath, file.Size, file.Modified, kind, text.Text, text.Encoding, text.Truncated,
                            MarkdownRenderable: kind.Tag == FilePreviewKindTag.Markdown && file.Size <= FilePreviewClassifier.MaximumMarkdownRenderBytes);
                    default:
                        return new(name, relativePath, file.Size, file.Modified, FilePreviewKind.Unsupported);
                }
            }
            catch (IOException) { return new(name, relativePath, file.Size, file.Modified, FilePreviewKind.Unsupported) { Failure = Locale.Get("files.preview.failed") }; }
        }
    }
}
