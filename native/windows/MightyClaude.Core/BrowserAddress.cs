namespace MightyClaude.Core;

public static class BrowserAddress
{
    // Mirrors macOS BrowserAddress.resolve: trim, empty → null, keep ://, else https://.
    public static Uri? Resolve(string? text)
    {
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0) return null;
        if (trimmed.Contains("://"))
            return Uri.TryCreate(trimmed, UriKind.Absolute, out var u) ? u : null;
        return new Uri("https://" + trimmed);
    }
}
