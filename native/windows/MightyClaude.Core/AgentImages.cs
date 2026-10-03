using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MightyClaude.Core;

/// <summary>
/// A picture an agent showed, kept by reference (macOS AgentImageRef, same JSON field
/// names). The bytes live in the app's image cache under their SHA-256; transcripts and
/// saved state carry only this small record, never the picture itself.
/// </summary>
public sealed record AgentImageRef(string Hash, string MediaType, int Width, int Height, int Bytes, string Source, string? Path = null);

public enum AgentImageError { Empty, TooLarge, UnsupportedType, Undecodable, ExternalSvg, TooManyPixels, InvalidEncoding }

public sealed class AgentImageException(AgentImageError error) : Exception(error.ToString())
{
    public AgentImageError Error { get; } = error;
}

/// <summary>A picture checked and written to the cache, not yet attributed to a tool.</summary>
public sealed record AgentImagePrepared(string Hash, string MediaType, int Width, int Height, int Bytes)
{
    public AgentImageRef Ref(string source, string? path = null)
    {
        var value = new AgentImageRef(Hash, MediaType, Width, Height, Bytes, source, path);
        return AgentImageSupport.Normalized(value) ?? value;
    }
}

/// <summary>
/// The macOS AgentImageSupport rules: caps, media types, base64, the payload walk and the
/// entry text. Windows reads a bitmap's size from its header instead of ImageIO, and keeps
/// the formats WIC decodes out of the box (png, jpeg, gif, webp, bmp) plus svg.
/// </summary>
public static class AgentImageSupport
{
    /// <summary>Larger decoded pictures are refused, never written.</summary>
    public const int MaximumImageBytes = 20 * 1_048_576;
    /// <summary>The cache on disk; least recently used pictures go first above it.</summary>
    public const int MaximumCacheBytes = 200 * 1_048_576;
    /// <summary>One run stores at most this many pictures; later ones are noted once.</summary>
    public const int MaximumImagesPerRun = 64;
    public const int MaximumImagesPerEntry = 16;
    /// <summary>Thumbnails are decoded at most this many pixels on the long side: a 480 pt picture at 2x.</summary>
    public const int ThumbnailPixels = 960;
    public const long MaximumPixels = 64_000_000;
    public const int MaximumSide = 8_000;
    /// <summary>
    /// One stream-json line may carry a whole picture in base64, so an agent's standard
    /// output reads lines up to this many characters (macOS: 64 MiB per line, 32 MiB for
    /// a Codex frame). Error output keeps the 1 MiB line.
    /// </summary>
    public const int MaximumLineCharacters = 32 * 1_048_576;
    internal const int MaximumSourceBytes = 240;

    /// <summary>The picture formats kept, with the file extension each is stored under.</summary>
    public static readonly IReadOnlyDictionary<string, string> Extensions = new Dictionary<string, string>
    {
        ["image/png"] = "png", ["image/jpeg"] = "jpg", ["image/gif"] = "gif", ["image/webp"] = "webp", ["image/heic"] = "heic",
        ["image/heif"] = "heif", ["image/tiff"] = "tiff", ["image/bmp"] = "bmp", ["image/svg+xml"] = "svg",
    };

    /// <summary>A recognised picture media type, or null.</summary>
    public static string? MediaType(string? value)
    {
        var lower = (value ?? "").Trim().ToLowerInvariant();
        var canonical = lower switch { "image/jpg" or "image/pjpeg" => "image/jpeg", "image/x-png" => "image/png", "image/svg" => "image/svg+xml", _ => lower };
        return Extensions.ContainsKey(canonical) ? canonical : null;
    }

    /// <summary>The media type a picture file name implies, or null for anything else.</summary>
    public static string? MediaTypeForFileName(string name)
    {
        var extension = FilePreviewClassifier.FileExtension(System.IO.Path.GetFileName(name));
        if (extension == "jpeg") return "image/jpeg";
        if (extension == "tif") return "image/tiff";
        return Extensions.FirstOrDefault(pair => pair.Value == extension).Key;
    }

    public static string Sha256(ReadOnlySpan<byte> data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    /// <summary>
    /// What the bytes really are: a bitmap within the pixel and side caps, or an svg
    /// document that references nothing outside itself. The declared type only decides
    /// between the two; a bitmap is stored as what its header says it is.
    /// </summary>
    public static (string MediaType, int Width, int Height) Inspect(ReadOnlySpan<byte> data, string declared, long maximumPixels = MaximumPixels, int maximumSide = MaximumSide)
    {
        if (data.IsEmpty) throw new AgentImageException(AgentImageError.Empty);
        if (data.Length > MaximumImageBytes) throw new AgentImageException(AgentImageError.TooLarge);
        var type = MediaType(declared) ?? throw new AgentImageException(AgentImageError.UnsupportedType);
        if (type == "image/svg+xml")
        {
            if (!SvgDocument(data)) throw new AgentImageException(AgentImageError.Undecodable);
            if (FilePreviewClassifier.SvgLoadsExternalContent(data)) throw new AgentImageException(AgentImageError.ExternalSvg);
            return ("image/svg+xml", 0, 0);
        }
        var (actual, width, height) = BitmapHeader(data);
        if (actual is null) throw new AgentImageException(AgentImageError.Undecodable);
        if (actual == "") throw new AgentImageException(AgentImageError.UnsupportedType);
        if (FilePreviewClassifier.PixelCount(width, height) is not { } pixels) throw new AgentImageException(AgentImageError.Undecodable);
        if (pixels > maximumPixels || Math.Max(width, height) > maximumSide) throw new AgentImageException(AgentImageError.TooManyPixels);
        return (actual, (int)width, (int)height);
    }

    /// <summary>
    /// The type and size a bitmap's header declares. Null type: not a picture this reads;
    /// "": a picture format Windows does not decode without an extra codec (tiff, heic).
    /// </summary>
    internal static (string? Type, long Width, long Height) BitmapHeader(ReadOnlySpan<byte> d)
    {
        if (d.Length >= 24 && d[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }) && d.Slice(12, 4).SequenceEqual("IHDR"u8))
            return ("image/png", BinaryPrimitives.ReadUInt32BigEndian(d.Slice(16, 4)), BinaryPrimitives.ReadUInt32BigEndian(d.Slice(20, 4)));
        if (d.Length >= 10 && (d[..6].SequenceEqual("GIF87a"u8) || d[..6].SequenceEqual("GIF89a"u8)))
            return ("image/gif", BinaryPrimitives.ReadUInt16LittleEndian(d.Slice(6, 2)), BinaryPrimitives.ReadUInt16LittleEndian(d.Slice(8, 2)));
        if (d.Length >= 26 && d[0] == (byte)'B' && d[1] == (byte)'M')
        {
            var header = BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(14, 4));
            if (header == 12) return ("image/bmp", BinaryPrimitives.ReadUInt16LittleEndian(d.Slice(18, 2)), BinaryPrimitives.ReadUInt16LittleEndian(d.Slice(20, 2)));
            if (header >= 40) return ("image/bmp", Math.Abs((long)BinaryPrimitives.ReadInt32LittleEndian(d.Slice(18, 4))), Math.Abs((long)BinaryPrimitives.ReadInt32LittleEndian(d.Slice(22, 4))));
            return (null, 0, 0);
        }
        if (d.Length >= 30 && d[..4].SequenceEqual("RIFF"u8) && d.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            var chunk = d.Slice(12, 4);
            if (chunk.SequenceEqual("VP8 "u8) && d[23] == 0x9D && d[24] == 0x01 && d[25] == 0x2A)
                return ("image/webp", BinaryPrimitives.ReadUInt16LittleEndian(d.Slice(26, 2)) & 0x3FFF, BinaryPrimitives.ReadUInt16LittleEndian(d.Slice(28, 2)) & 0x3FFF);
            if (chunk.SequenceEqual("VP8L"u8) && d[20] == 0x2F)
            {
                var bits = BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(21, 4));
                return ("image/webp", (bits & 0x3FFF) + 1, ((bits >> 14) & 0x3FFF) + 1);
            }
            if (chunk.SequenceEqual("VP8X"u8))
                return ("image/webp", (d[24] | d[25] << 8 | d[26] << 16) + 1L, (d[27] | d[28] << 8 | d[29] << 16) + 1L);
            return (null, 0, 0);
        }
        if (d.Length >= 4 && d[0] == 0xFF && d[1] == 0xD8)
        {
            var at = 2;
            while (at + 4 <= d.Length)
            {
                if (d[at] != 0xFF) return (null, 0, 0);
                var marker = d[at + 1];
                if (marker == 0xFF) { at++; continue; }
                if (marker is 0x01 or (>= 0xD0 and <= 0xD7)) { at += 2; continue; }
                var length = BinaryPrimitives.ReadUInt16BigEndian(d.Slice(at + 2, 2));
                if (length < 2) return (null, 0, 0);
                if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
                    return at + 9 <= d.Length ? ("image/jpeg", BinaryPrimitives.ReadUInt16BigEndian(d.Slice(at + 7, 2)), BinaryPrimitives.ReadUInt16BigEndian(d.Slice(at + 5, 2))) : (null, 0, 0);
                if (marker is 0xD9 or 0xDA) return (null, 0, 0);
                at += 2 + length;
            }
            return (null, 0, 0);
        }
        if (d.Length >= 4 && (d[..4].SequenceEqual(new byte[] { 0x49, 0x49, 0x2A, 0x00 }) || d[..4].SequenceEqual(new byte[] { 0x4D, 0x4D, 0x00, 0x2A }))) return ("", 0, 0);
        if (d.Length >= 12 && d.Slice(4, 4).SequenceEqual("ftyp"u8) && (d.Slice(8, 4).SequenceEqual("heic"u8) || d.Slice(8, 4).SequenceEqual("heix"u8) || d.Slice(8, 4).SequenceEqual("mif1"u8) || d.Slice(8, 4).SequenceEqual("msf1"u8))) return ("", 0, 0);
        return (null, 0, 0);
    }

    /// <summary>
    /// Whether the text is an svg document: after an optional byte order mark, XML
    /// declaration, processing instructions, comments and doctype, the first element is
    /// <c>&lt;svg</c>. Anything else declared as svg is refused.
    /// </summary>
    public static bool SvgDocument(ReadOnlySpan<byte> data)
    {
        if (FilePreviewClassifier.DecodeText(data[..Math.Min(data.Length, 65_536)], sample: true) is not { } decoded) return false;
        var rest = decoded.Text.ToLowerInvariant().AsSpan();
        while (true)
        {
            rest = rest.TrimStart().TrimStart('﻿').TrimStart();
            if (rest.StartsWith("<?")) { var end = rest.IndexOf("?>"); if (end < 0) return false; rest = rest[(end + 2)..]; }
            else if (rest.StartsWith("<!--")) { var end = rest.IndexOf("-->"); if (end < 0) return false; rest = rest[(end + 3)..]; }
            else if (rest.StartsWith("<!doctype")) { var end = rest.IndexOf('>'); if (end < 0) return false; rest = rest[(end + 1)..]; }
            else break;
        }
        if (!rest.StartsWith("<svg")) return false;
        return rest.Length == 4 || char.IsWhiteSpace(rest[4]) || rest[4] is '>' or '/';
    }

    /// <summary>A saved or received record, or null when any field is out of shape.</summary>
    public static AgentImageRef? Normalized(AgentImageRef? value)
    {
        if (value is null || value.Hash is not { Length: 64 } hash || !hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')
            || MediaType(value.MediaType) is not { } type || value.Width is < 0 or > 1_000_000 || value.Height is < 0 or > 1_000_000
            || value.Bytes is < 1 or > MaximumImageBytes) return null;
        var path = value.Path is { } p && (p.StartsWith('/') || System.IO.Path.IsPathFullyQualified(p)) && Encoding.UTF8.GetByteCount(p) <= WorkspaceFiles.MaximumPathBytes && !p.Contains('\0') ? p : null;
        return value with { MediaType = type, Source = ActivitySupport.Clean(value.Source, MaximumSourceBytes, true), Path = path };
    }

    public static List<AgentImageRef>? Normalized(IEnumerable<AgentImageRef?>? values)
    {
        if (values is null) return null;
        var result = values.Take(MaximumImagesPerEntry).Select(Normalized).OfType<AgentImageRef>().ToList();
        return result.Count == 0 ? null : result;
    }

    /// <summary>The text an image entry carries; any reader without picture support shows it instead.</summary>
    public static string EntryText(IReadOnlyCollection<AgentImageRef> refs, string source)
    {
        var label = ActivitySupport.Clean(source, MaximumSourceBytes, true);
        return refs.Count == 1
            ? Locale.Get("images.entry.one", new Dictionary<string, string> { ["source"] = label })
            : Locale.Get("images.entry.many", new Dictionary<string, string> { ["count"] = refs.Count.ToString(CultureInfo.InvariantCulture), ["source"] = label });
    }

    /// <summary>The system line a refused picture leaves (macOS CLIStreamParser.imageFailure).</summary>
    public static string FailureText(AgentImageError error) => error switch
    {
        AgentImageError.TooLarge => Locale.Get("images.failed.tooLarge", new Dictionary<string, string> { ["limit"] = (MaximumImageBytes / 1_048_576).ToString(CultureInfo.InvariantCulture) }),
        AgentImageError.ExternalSvg => Locale.Get("images.failed.externalSVG"),
        AgentImageError.TooManyPixels => Locale.Get("images.failed.tooManyPixels"),
        AgentImageError.UnsupportedType => Locale.Get("images.failed.unsupported"),
        _ => Locale.Get("images.failed.undecodable"),
    };

    /// <summary>
    /// Base64 picture payloads in a tool result or message content: Claude's
    /// <c>{"type":"image","source":{"type":"base64",…}}</c> blocks and MCP's
    /// <c>{"type":"image","data":…,"mimeType":…}</c> blocks, in order, also inside a
    /// <c>content</c> array (Codex <c>mcp_tool_call</c> results). Text is never read.
    /// </summary>
    public static List<(string MediaType, string Base64)> Payloads(JsonElement value, int depth = 0)
    {
        var result = new List<(string, string)>();
        if (depth >= 4) return result;
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (value.Text("type") == "image")
            {
                var source = MetadataJson.Property(value, "source");
                if (source.Text("type") == "base64" && source.Text("data") is { } data && source.Text("media_type") is { } type) result.Add((type, data));
                else if (value.Text("data") is { } inline && (value.Text("mimeType") ?? value.Text("mime_type") ?? value.Text("media_type")) is { } inlineType) result.Add((inlineType, inline));
                return result;
            }
            return Payloads(MetadataJson.Property(value, "content"), depth + 1);
        }
        if (value.ValueKind != JsonValueKind.Array) return result;
        foreach (var block in value.EnumerateArray().Take(64)) result.AddRange(Payloads(block, depth + 1));
        return result;
    }

    /// <summary>Strict base64 (whitespace allowed), decoded only within the size cap.</summary>
    public static byte[] DecodeBase64(string text)
    {
        if (text.Length > (MaximumImageBytes / 3 + 2) * 4 + 4_096) throw new AgentImageException(AgentImageError.TooLarge);
        var compact = text.Any(char.IsWhiteSpace) ? string.Concat(text.Where(c => !char.IsWhiteSpace(c))) : text;
        var buffer = new byte[compact.Length / 4 * 3 + 3];
        if (!Convert.TryFromBase64String(compact, buffer, out var written) || written == 0) throw new AgentImageException(AgentImageError.InvalidEncoding);
        if (written > MaximumImageBytes) throw new AgentImageException(AgentImageError.TooLarge);
        return buffer[..written];
    }
}

/// <summary>
/// The content-addressed picture store under the app's data folder (macOS
/// AgentImageCache): files are named <c>&lt;sha256&gt;.&lt;ext&gt;</c>; reading one marks it
/// recently used, and writing past the cap removes the least recently used others.
/// </summary>
public sealed class AgentImageCache(string directory, long maximumBytes = AgentImageSupport.MaximumCacheBytes)
{
    private readonly object sync = new();
    public string Directory { get; } = directory;
    public long MaximumBytes { get; } = maximumBytes;

    internal string? FilePath(string hash, string mediaType)
    {
        if (hash is not { Length: 64 } || !hash.All(Uri.IsHexDigit) || AgentImageSupport.MediaType(mediaType) is not { } type) return null;
        return System.IO.Path.Combine(Directory, hash.ToLowerInvariant() + "." + AgentImageSupport.Extensions[type]);
    }

    /// <summary>Validates, then writes the picture once under its hash. A file already there whose bytes no longer match is written again.</summary>
    public AgentImagePrepared Prepare(byte[] data, string mediaType)
    {
        var inspected = AgentImageSupport.Inspect(data, mediaType);
        var hash = AgentImageSupport.Sha256(data);
        var path = FilePath(hash, inspected.MediaType) ?? throw new AgentImageException(AgentImageError.UnsupportedType);
        lock (sync)
        {
            System.IO.Directory.CreateDirectory(Directory);
            if (Intact(path, data.Length, hash)) Touch(path);
            else
            {
                var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllBytes(temporary, data);
                File.Move(temporary, path, overwrite: true);
                Evict(path);
            }
        }
        return new(hash, inspected.MediaType, inspected.Width, inspected.Height, data.Length);
    }

    public AgentImagePrepared Prepare(string base64, string mediaType) => Prepare(AgentImageSupport.DecodeBase64(base64), mediaType);
    public AgentImageRef Store(byte[] data, string mediaType, string source, string? path = null) => Prepare(data, mediaType).Ref(source, path);

    private static bool Intact(string path, int bytes, string hash)
    {
        try { var info = new FileInfo(path); return info.Exists && info.Length == bytes && AgentImageSupport.Sha256(File.ReadAllBytes(path)) == hash; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>The cached file, marked as just used; null once it was evicted or removed.</summary>
    public string? PathFor(AgentImageRef value)
    {
        if (FilePath(value.Hash, value.MediaType) is not { } path) return null;
        lock (sync) { if (!File.Exists(path)) return null; Touch(path); return path; }
    }

    /// <summary>The cached bytes, re-checked against the hash they are named by.</summary>
    public byte[]? Data(AgentImageRef value)
    {
        if (PathFor(value) is not { } path) return null;
        try
        {
            var info = new FileInfo(path);
            if (info.Length > AgentImageSupport.MaximumImageBytes) return null;
            var data = File.ReadAllBytes(path);
            return AgentImageSupport.Sha256(data) == value.Hash.ToLowerInvariant() ? data : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public long TotalBytes { get { lock (sync) return Entries().Sum(e => e.Length); } }

    private static void Touch(string path) { try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }

    private List<FileInfo> Entries()
    {
        try { return new DirectoryInfo(Directory).EnumerateFiles().Where(f => !f.Name.StartsWith('.') && !f.Name.EndsWith(".tmp", StringComparison.Ordinal)).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private void Evict(string kept)
    {
        var files = Entries().OrderBy(f => f.LastWriteTimeUtc).ToList();
        var total = files.Sum(f => f.Length);
        foreach (var oldest in files)
        {
            if (total <= MaximumBytes) break;
            if (string.Equals(oldest.FullName, System.IO.Path.GetFullPath(kept), StringComparison.OrdinalIgnoreCase)) continue;
            var length = oldest.Length;
            try { oldest.Delete(); total -= length; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}

/// <summary>Where a picture an agent named may be read from (macOS AgentImageLocation).</summary>
public abstract record AgentImageLocation
{
    /// <summary>An existing picture file whose real path is inside <paramref name="Root"/>.</summary>
    public sealed record File(string Path, string Root) : AgentImageLocation;
    /// <summary>A <c>data:image/…;base64,</c> URI, still encoded.</summary>
    public sealed record Inline(string MediaType, string Base64) : AgentImageLocation;
    /// <summary>An http(s) address. It is shown as a link and never fetched.</summary>
    public sealed record Remote(Uri Url) : AgentImageLocation;
    /// <summary>Anything else: outside the allowed folders, not a picture, malformed.</summary>
    public sealed record Refused : AgentImageLocation;
}

/// <summary>
/// The path rule for pictures named in agent text (Markdown images) or by a tool item that
/// reports only a path: the file must be a picture whose real path, links resolved, lies
/// inside the workspace folder or the system temporary folder. Relative paths resolve
/// against the workspace only.
/// </summary>
public static class AgentImagePaths
{
    /// <summary>The temporary folders an agent's tools write screenshots to.</summary>
    public static readonly IReadOnlyList<string> TemporaryRoots = BuildTemporaryRoots();

    private static List<string> BuildTemporaryRoots()
    {
        var roots = new List<string> { System.IO.Path.GetTempPath() };
        if (!OperatingSystem.IsWindows() && WorkspaceFiles.RealPath("/tmp") is { } tmp && !roots.Any(r => WorkspaceFiles.RealPath(r) == tmp)) roots.Add("/tmp");
        return roots;
    }

    public static AgentImageLocation Locate(string reference, string? workspaceRoot, IReadOnlyList<string>? temporaryRoots = null)
    {
        var value = reference.Trim();
        if (value.Length == 0 || value.Contains('\0')) return new AgentImageLocation.Refused();
        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return Inline(value);
        if (Uri.TryCreate(value, UriKind.Absolute, out var url) && url.Scheme is "http" or "https")
            return string.IsNullOrEmpty(url.Host) ? new AgentImageLocation.Refused() : new AgentImageLocation.Remote(url);
        // A path is tried as written, then percent-decoded: Markdown hands over
        // `/tmp/%EC%8A%A4….png` for a Korean or spaced file name.
        var candidates = new List<string>();
        if (Uri.TryCreate(value, UriKind.Absolute, out var fileUrl) && fileUrl.IsFile && value.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) candidates.Add(fileUrl.LocalPath);
        else if (Uri.TryCreate(value, UriKind.Absolute, out var other) && !IsAbsolutePath(value) && other.Scheme.Length > 1) return new AgentImageLocation.Refused();
        else
        {
            candidates.Add(value);
            string decoded; try { decoded = Uri.UnescapeDataString(value); } catch (UriFormatException) { decoded = value; }
            if (decoded != value) candidates.Add(decoded);
        }
        foreach (var path in candidates)
        {
            if (path.StartsWith('~') || path.Contains('\0') || AgentImageSupport.MediaTypeForFileName(path) is null) continue;
            var absolute = IsAbsolutePath(path);
            var roots = (workspaceRoot is null ? [] : new[] { workspaceRoot }).Concat(absolute ? temporaryRoots ?? TemporaryRoots : []);
            foreach (var root in roots)
            {
                if (WorkspaceFiles.RealPath(root) is not { } realRoot) continue;
                string? real;
                try
                {
                    // On paper first: a path that is not inside the root as written (a UNC
                    // share, another drive, `..` out of it) is refused before anything on
                    // disk or on the network is asked about it.
                    var full = System.IO.Path.GetFullPath(absolute ? path : System.IO.Path.Combine(realRoot, path.Replace('/', System.IO.Path.DirectorySeparatorChar)));
                    if (!WorkspaceFiles.Contains(full, realRoot) && !WorkspaceFiles.Contains(full, System.IO.Path.GetFullPath(root))) continue;
                    real = WorkspaceFiles.RealPath(full);
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
                if (real is null || !WorkspaceFiles.Contains(real, realRoot) || !System.IO.File.Exists(real) || AgentImageSupport.MediaTypeForFileName(real) is null) continue;
                return new AgentImageLocation.File(real, realRoot);
            }
        }
        return new AgentImageLocation.Refused();
    }

    private static bool IsAbsolutePath(string path) => path.StartsWith('/') || System.IO.Path.IsPathFullyQualified(path);

    private static AgentImageLocation Inline(string value)
    {
        var comma = value.IndexOf(',');
        if (comma < 0) return new AgentImageLocation.Refused();
        var parts = value[5..comma].ToLowerInvariant().Split(';').Select(p => p.Trim()).ToArray();
        if (parts[^1] != "base64" || AgentImageSupport.MediaType(parts[0]) is not { } type) return new AgentImageLocation.Refused();
        return new AgentImageLocation.Inline(type, value[(comma + 1)..]);
    }

    /// <summary>Reads a located file through the workspace safe-open (no final link, regular file, handle path re-checked against the root).</summary>
    public static byte[] Read(string path, string root)
    {
        var realRoot = WorkspaceFiles.RealPath(root) ?? throw new WorkspaceFileOpenException(WorkspaceFileOpenError.Missing);
        using var file = WorkspaceFiles.OpenFile(System.IO.Path.GetRelativePath(realRoot, path).Replace(System.IO.Path.DirectorySeparatorChar, '/'), realRoot);
        if (file.Size > AgentImageSupport.MaximumImageBytes) throw new AgentImageException(AgentImageError.TooLarge);
        var data = FilePreviewClassifier.ReadPrefix(file.Stream, AgentImageSupport.MaximumImageBytes + 1);
        if (data.Length > AgentImageSupport.MaximumImageBytes) throw new AgentImageException(AgentImageError.TooLarge);
        return data;
    }
}

/// <summary><c>![alt](target)</c> images in agent Markdown, outside fenced code blocks.</summary>
public sealed record AgentMarkdownImage(string Alt, string Source);

public static class AgentMarkdownImages
{
    public const int MaximumImages = 32;
    public static readonly Regex Pattern = new(@"!\[([^\]\n]{0,500})\]\(\s*(?:<([^>\n]+)>|([^)\s]+))(?:\s+(?:""[^""\n]*""|'[^'\n]*'))?\s*\)", RegexOptions.Compiled, TimeSpan.FromMilliseconds(150));

    public static List<AgentMarkdownImage> Extract(string text)
    {
        var result = new List<AgentMarkdownImage>();
        if (!text.Contains("![", StringComparison.Ordinal)) return result;
        var fenced = false; var outside = new StringBuilder();
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.TrimStart(' ');
            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal)) { fenced = !fenced; outside.Append('\n'); continue; }
            outside.Append(fenced ? "\n" : line + "\n");
        }
        foreach (Match match in Pattern.Matches(outside.ToString()))
        {
            var source = match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value;
            if (source.Length == 0) continue;
            result.Add(new(match.Groups[1].Value, source));
            if (result.Count == MaximumImages) break;
        }
        return result;
    }
}

/// <summary>
/// The transcript is one RichEdit document, so a picture is an RTF <c>\pict</c> group holding
/// a PNG thumbnail. It is drawn at most 480 × 640 (the macOS transcript thumbnail), never
/// larger than the picture itself, aspect kept.
/// </summary>
public static class AgentImageRtf
{
    public const int MaximumWidth = 480, MaximumHeight = 640;

    /// <summary>The size a picture of <paramref name="width"/> × <paramref name="height"/> pixels is drawn at, in device-independent pixels.</summary>
    public static (int Width, int Height) DisplaySize(int width, int height, int maximumWidth = MaximumWidth, int maximumHeight = MaximumHeight)
    {
        if (width <= 0 || height <= 0) return (0, 0);
        var scale = Math.Min(1.0, Math.Min((double)maximumWidth / width, (double)maximumHeight / height));
        return (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
    }

    /// <summary>
    /// A <c>\pict</c> group for a PNG (<c>\pngblip</c>) or, for an opaque thumbnail, a JPEG
    /// (<c>\jpegblip</c>) of <paramref name="pixelWidth"/> × <paramref name="pixelHeight"/> pixels drawn at <paramref name="display"/>.
    /// </summary>
    public static string Picture(ReadOnlySpan<byte> png, int pixelWidth, int pixelHeight, (int Width, int Height) display)
    {
        // 1 DIP = 15 twips at 96 DPI.
        var body = new StringBuilder(png.Length * 2 + 128);
        body.Append(png.Length >= 2 && png[0] == 0xFF && png[1] == 0xD8 ? @"{\pict\jpegblip\picw" : @"{\pict\pngblip\picw").Append(pixelWidth).Append(@"\pich").Append(pixelHeight)
            .Append(@"\picwgoal").Append(display.Width * 15).Append(@"\pichgoal").Append(display.Height * 15).Append(' ');
        for (var i = 0; i < png.Length; i++)
        {
            if (i > 0 && i % 64 == 0) body.Append('\n');
            body.Append(png[i].ToString("x2", CultureInfo.InvariantCulture));
        }
        return body.Append('}').ToString();
    }
}
