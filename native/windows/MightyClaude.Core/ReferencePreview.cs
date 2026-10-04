using System.Text.RegularExpressions;

namespace MightyClaude.Core;

/// <summary>One resolution policy shared by transcript clicks and the final-result file list.</summary>
public static class ReferencePreview
{
    public sealed record Target(string RelativePath, int? Line);

    private static readonly Regex ReferencePattern = new(@"(?<![\p{L}\p{N}_])((?:[A-Za-z]:[\\/]|\.{1,2}[\\/]|/)?(?:[\p{L}\p{N}_.@-]+[\\/])*[\p{L}\p{N}_.@-]+\.[A-Za-z0-9]{1,12})(?::(\d{1,6}))?", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    /// <summary>The file-like text under a click; resolution still checks the workspace boundary.</summary>
    public static (string Path, int? Line)? TextTargetAt(string text, int position)
    {
        if (position < 0 || position >= text.Length) return null;
        // Only the clicked line is inspected; a huge transcript cannot turn a tap into a long scan.
        var start = text.LastIndexOfAny(['\r', '\n'], position); start = start < 0 ? 0 : start + 1;
        var end = text.IndexOfAny(['\r', '\n'], position); if (end < 0) end = text.Length;
        if (end - start > 16_384) return null;
        var line = text[start..end];
        try
        {
            // Markdown destinations are shown in parentheses by the transcript renderer;
            // keep spaces in those names instead of treating only the last word as a path.
            var open = line.LastIndexOf('(', position - start);
            var close = line.IndexOf(')', position - start);
            if (open >= 0 && close > open && position - start > open)
            {
                var candidate = line[(open + 1)..close];
                if (!candidate.Contains("://", StringComparison.Ordinal) && Regex.IsMatch(candidate, @"^[^()<>\r\n]+\.[A-Za-z0-9]{1,12}(?::\d{1,6})?$", RegexOptions.None, TimeSpan.FromMilliseconds(100)))
                {
                    var colon = candidate.LastIndexOf(':');
                    if (colon > 1 && int.TryParse(candidate[(colon + 1)..], out var number)) return (candidate[..colon], number);
                    return (candidate, null);
                }
            }
            foreach (Match match in ReferencePattern.Matches(line))
            {
                if (position - start < match.Index || position - start >= match.Index + match.Length) continue;
                // Never interpret a URL's suffix as a local file.
                var tokenStart = line.LastIndexOfAny([' ', '\t'], match.Index); tokenStart = tokenStart < 0 ? 0 : tokenStart + 1;
                var tokenEnd = line.IndexOfAny([' ', '\t'], match.Index); if (tokenEnd < 0) tokenEnd = line.Length;
                if (line[tokenStart..tokenEnd].Contains("://", StringComparison.Ordinal)) return null;
                return (match.Groups[1].Value, match.Groups[2].Success && int.TryParse(match.Groups[2].Value, out var n) ? n : null);
            }
        }
        catch (RegexMatchTimeoutException) { }
        return null;
    }

    public static Target? Resolve(string path, string root, int? line = null)
    {
        if (ResultFiles.LocalPath(path) is not { } local) return null;
        if (line is null && local.LastIndexOf(':') is var colon && colon > 1 && int.TryParse(local[(colon + 1)..], out var parsed) && parsed > 0)
        { line = parsed; local = local[..colon]; }
        var resolved = ResultFiles.Resolve(local, root);
        if (resolved is null || WorkspaceFiles.RealPath(root) is not { } realRoot) return null;
        return new(Path.GetRelativePath(realRoot, resolved).Replace('\\', '/'), line is > 0 ? line : null);
    }

    public static FilePreviewData Load(Target target, string root, CancellationToken cancellation = default) =>
        FilePreviewLoader.Load(root, target.RelativePath, cancellation);
}
