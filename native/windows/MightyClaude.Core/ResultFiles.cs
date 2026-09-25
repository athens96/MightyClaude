using System.Text;
using System.Text.RegularExpressions;

namespace MightyClaude.Core;

/// <summary>
/// Port of macOS ReferenceLinkSupport: the file paths a final result names.
/// Detection is purely textual; resolution only ever yields an existing regular
/// file inside the given workspace root, so a model-written path cannot reach
/// other files.
/// </summary>
public static class ResultFiles
{
    public const int MaximumResultFiles = 100;
    public const int MaximumResultTextBytes = 512 * 1024;
    public const int MaximumResultTexts = 200;
    public const int MaximumPathBytes = 1_024;
    public const int MaximumMatchesPerText = 200;

    /// A path needs at least one directory segment and an ASCII extension, so
    /// version numbers, "and/or" and bare file names are left alone.
    private static readonly Regex PathPattern = new(
        @"(?<![A-Za-z0-9_/.:@~-])((?:\.{1,2}/|/)?(?:[\p{L}\p{N}_.@-]+/)+[\p{L}\p{N}_.@-]+\.[A-Za-z0-9]{1,8})(?::(\d{1,6}))?(?![A-Za-z0-9_/])",
        RegexOptions.Compiled);
    private static readonly Regex WebPattern = new(@"https?://[^\s<>""'`)\]]+", RegexOptions.Compiled);
    /// Inline Markdown link: the destination is a reference, its label is not.
    private static readonly Regex MarkdownLink = new(@"\[(?<label>[^\]\[]*)\]\(\s*<?(?<dest>[^)\s<>]*)>?(?:\s+(?:""[^""]*""|'[^']*'|\([^)]*\)))?\s*\)", RegexOptions.Compiled);

    public sealed record PathMatch(string Path, int? Line);
    public sealed record ResultFile(string Path, int? Line);

    /// <summary>Plain-text path references, web addresses excluded.</summary>
    public static List<PathMatch> Matches(string text)
    {
        var taken = new List<(int Start, int End)>();
        foreach (Match web in WebPattern.Matches(text).Take(MaximumMatchesPerText))
        {
            var end = web.Index + web.Length;
            while (end - web.Index > 1 && ".,;:!?".Contains(text[end - 1])) end -= 1;
            taken.Add((web.Index, end));
        }
        var result = new List<PathMatch>();
        foreach (Match match in PathPattern.Matches(text).Take(MaximumMatchesPerText))
        {
            var start = match.Index; var end = match.Index + match.Length;
            if (taken.Any(range => Math.Min(range.End, end) > Math.Max(range.Start, start))) continue;
            var path = match.Groups[1].Value;
            if (Encoding.UTF8.GetByteCount(path) > MaximumPathBytes) continue;
            int? line = match.Groups[2].Success && int.TryParse(match.Groups[2].Value, out var number) ? number : null;
            result.Add(new PathMatch(path, line));
        }
        return result;
    }

    /// <summary>A relative or file destination from Markdown link syntax, as a path string.</summary>
    public static string? LocalPath(string destination)
    {
        if (destination.Length == 0) return null;
        var scheme = Regex.Match(destination, @"^([A-Za-z][A-Za-z0-9+.-]*):");
        if (scheme.Success && !scheme.Groups[1].Value.Equals("file", StringComparison.OrdinalIgnoreCase)) return null;
        var value = destination;
        if (scheme.Success)
        {
            if (!value.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) return null;
            value = value["file://".Length..];
            var slash = value.IndexOf('/');
            if (slash < 0) return null;
            if (slash > 0) return null; // a host is not a local path
            value = value[slash..];
        }
        string clean;
        try { clean = Uri.UnescapeDataString(value); } catch (UriFormatException) { clean = value; }
        if (clean.Length == 0 || clean.Contains("://", StringComparison.Ordinal) || clean.Contains('\0')) return null;
        return Encoding.UTF8.GetByteCount(clean) <= MaximumPathBytes ? clean : null;
    }

    /// <summary>nil unless the reference is an existing regular file inside root.</summary>
    public static string? Resolve(string path, string? root)
    {
        if (root is null || path.Length == 0 || path.Contains('\0') || Encoding.UTF8.GetByteCount(path) > MaximumPathBytes) return null;
        var basePath = RealPath(root);
        if (basePath is null) return null;
        string candidate;
        try { candidate = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(basePath, path)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        candidate = RealPath(Path.GetDirectoryName(candidate)) is { } directory
            ? Path.Combine(directory, Path.GetFileName(candidate)) : candidate;
        var prefix = basePath.EndsWith(Path.DirectorySeparatorChar) ? basePath : basePath + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var info = new FileInfo(candidate);
        return info.Exists && (info.Attributes & FileAttributes.Directory) == 0 ? candidate : null;
    }

    /// The directory's own path with every symlink resolved (macOS
    /// standardizedFileURL.resolvingSymlinksInPath for the workspace root).
    private static string? RealPath(string? directory)
    {
        if (directory is null) return null;
        try
        {
            var full = Path.GetFullPath(directory);
            var info = new DirectoryInfo(full);
            while (info.ResolveLinkTarget(true) is DirectoryInfo target) { if (target.FullName == info.FullName) break; info = target; }
            return info.FullName.TrimEnd(Path.DirectorySeparatorChar) is { Length: > 0 } trimmed ? trimmed : info.FullName;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException) { return null; }
    }

    /// <summary>
    /// The workspace files a set of final-result texts name, in order, without
    /// duplicates, each inside the workspace. Markdown link destinations count as
    /// references; their label text does not.
    /// </summary>
    public static List<ResultFile> In(IEnumerable<string> texts, string? root)
    {
        var basePath = root is null ? null : RealPath(root);
        if (basePath is null) return [];
        var prefix = basePath.EndsWith(Path.DirectorySeparatorChar) ? basePath : basePath + Path.DirectorySeparatorChar;
        var files = new List<ResultFile>();
        var seen = new HashSet<string>();
        var remainingBytes = MaximumResultTextBytes;
        var remainingCandidates = 1_000;

        void Add(string path, int? line)
        {
            if (remainingCandidates <= 0 || files.Count >= MaximumResultFiles) return;
            remainingCandidates -= 1;
            var resolved = Resolve(path, basePath);
            // Markdown destinations can carry the same :line convention as plain
            // transcript paths. A real filename containing ':' wins.
            if (resolved is null)
            {
                var colon = path.LastIndexOf(':');
                if (colon >= 0)
                {
                    var suffix = path[(colon + 1)..];
                    if (suffix.Length is > 0 and <= 6 && suffix.All(char.IsAsciiDigit) && int.TryParse(suffix, out var number) && number > 0)
                    { resolved = Resolve(path[..colon], basePath); line = number; }
                }
            }
            if (resolved is null || !seen.Add(resolved)) return;
            files.Add(new ResultFile(resolved[prefix.Length..].Replace('\\', '/'), line is > 0 ? line : null));
        }
        void Plain(string text)
        {
            foreach (var match in Matches(text))
            {
                Add(match.Path, match.Line);
                if (remainingCandidates == 0 || files.Count == MaximumResultFiles) break;
            }
        }
        foreach (var source in texts.Take(MaximumResultTexts))
        {
            if (remainingBytes <= 0 || remainingCandidates <= 0 || files.Count >= MaximumResultFiles) break;
            var text = ActivitySupport.PrefixUtf8(source, remainingBytes);
            remainingBytes -= Encoding.UTF8.GetByteCount(text);
            var at = 0;
            foreach (Match link in MarkdownLink.Matches(text))
            {
                if (remainingCandidates <= 0 || files.Count >= MaximumResultFiles) break;
                if (link.Index > at) Plain(text[at..link.Index]);
                if (LocalPath(link.Groups["dest"].Value) is { } destination) Add(destination, null);
                at = link.Index + link.Length;
            }
            if (at < text.Length) Plain(text[at..]);
        }
        return files;
    }
}
