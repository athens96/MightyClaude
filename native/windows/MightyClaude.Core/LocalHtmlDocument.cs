namespace MightyClaude.Core;

/// <summary>A rendered document can read only bounded assets in its own directory.</summary>
public sealed class LocalHtmlDocument
{
    public const long MaximumAssetBytes = 8 * 1024 * 1024;
    public const string ContentPolicy = "default-src 'none'; script-src 'self' 'unsafe-inline' 'unsafe-eval'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; font-src 'self' data:; connect-src 'none'; frame-src 'none'; object-src 'none'; base-uri 'none'; form-action 'none'";
    public string Host { get; } = "preview-" + Guid.NewGuid().ToString("N") + ".invalid";
    public string DirectoryPath { get; }
    public string Address { get; }
    public LocalHtmlDocument(string root, string relativePath)
    {
        var path = WorkspaceFiles.Resolve(relativePath, root);
        if (path is null || !File.Exists(path) || Path.GetExtension(path).ToLowerInvariant() is not (".html" or ".htm")) throw new ArgumentException("A workspace HTML document is required.");
        DirectoryPath = Path.GetDirectoryName(path)!;
        Address = "https://" + Host + "/" + Uri.EscapeDataString(Path.GetFileName(path));
    }
    public bool Allows(string address) => Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.Host == Host && uri.IsDefaultPort && uri.UserInfo.Length == 0;
    public sealed record Resource(byte[] Bytes, string MediaType);
    public Resource? Read(string address, string method)
    {
        if (!Allows(address) || method != "GET") return null;
        try
        {
            var path = Uri.UnescapeDataString(new Uri(address).AbsolutePath.TrimStart('/'));
            if (path.Contains('\\') || path.Split('/').Any(segment => segment is ".." or ".")) return null;
            using var opened = WorkspaceFiles.OpenFile(path, DirectoryPath);
            var input = opened.Stream;
            if (input.Length > MaximumAssetBytes) return null;
            var bytes = new byte[(int)input.Length]; input.ReadExactly(bytes);
            var media = Path.GetExtension(opened.Path).ToLowerInvariant() switch
            {
                ".html" or ".htm" => "text/html; charset=utf-8", ".css" => "text/css; charset=utf-8", ".js" or ".mjs" => "text/javascript; charset=utf-8",
                ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif", ".webp" => "image/webp", ".svg" => "image/svg+xml",
                ".woff" => "font/woff", ".woff2" => "font/woff2", ".ttf" => "font/ttf", ".ico" => "image/x-icon", _ => "text/plain; charset=utf-8",
            };
            return new(bytes, media);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }
}
