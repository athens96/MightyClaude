using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MightyClaude.Core;

public sealed record RunAttachment(string Id, string Name, string MediaType, [property: JsonConverter(typeof(AttachmentBase64Converter))] string DataBase64);

// Base64 is carried in JSON, never inserted into HTML. Encode its ASCII alphabet
// directly so '+' does not expand sixfold and defeat the negotiated body limit.
public sealed class AttachmentBase64Converter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetString() ?? throw new JsonException("첨부 데이터가 없습니다.");
    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(JsonEncodedText.Encode(value, System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping));
}

public static class AttachmentSupport
{
    public const int MaximumCount = 8;
    public const int MaximumFileBytes = 5 * 1024 * 1024;
    public const int MaximumTotalBytes = 8 * 1024 * 1024;
    public const int MaximumRequestBytes = 12 * 1024 * 1024;
    private static readonly string[] MediaTypes = ["image/png", "image/jpeg", "image/gif", "image/webp", "application/pdf", "text/plain", "application/octet-stream"];
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static RunAttachment Make(string name, byte[] data)
    {
        if (data.Length > MaximumFileBytes) throw new ArgumentException("파일 하나는 5MiB 이하여야 합니다.");
        var basename = name.Replace('\\', '/').Split('/').LastOrDefault() ?? "attachment";
        basename = new string(basename.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (basename.Length > 180) { basename = basename[..180]; if (char.IsHighSurrogate(basename[^1])) basename = basename[..^1]; }
        if (basename is "" or "." or "..") basename = "attachment";
        return new(Wire.Id(), basename, DetectMediaType(data), Convert.ToBase64String(data));
    }
    public static string DetectMediaType(ReadOnlySpan<byte> data)
    {
        if (data.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return "image/png";
        if (data.StartsWith(new byte[] { 255, 216, 255 })) return "image/jpeg";
        if (data.StartsWith("GIF87a"u8) || data.StartsWith("GIF89a"u8)) return "image/gif";
        if (data.Length >= 12 && data[..4].SequenceEqual("RIFF"u8) && data.Slice(8, 4).SequenceEqual("WEBP"u8)) return "image/webp";
        if (data.StartsWith("%PDF-"u8)) return "application/pdf";
        try { var text = StrictUtf8.GetString(data); if (!text.Any(c => char.IsControl(c) && c is not ('\t' or '\r' or '\n'))) return "text/plain"; } catch (DecoderFallbackException) { }
        return "application/octet-stream";
    }
    public static byte[] Decode(RunAttachment attachment)
    {
        if (attachment is null || !Wire.Identifier(attachment.Id) || attachment.Name is not { Length: > 0 and <= 180 } || attachment.Name is "." or ".." || attachment.Name.Any(c => c is '/' or '\\' || char.IsControl(c)) || !MediaTypes.Contains(attachment.MediaType) || attachment.DataBase64 is null || attachment.DataBase64.Length > ((MaximumFileBytes + 2) / 3) * 4) throw new ArgumentException("첨부 파일의 형식이나 크기가 올바르지 않습니다.");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(attachment.DataBase64); } catch (FormatException) { throw new ArgumentException("첨부 파일의 데이터가 올바르지 않습니다."); }
        if (bytes.Length > MaximumFileBytes || Convert.ToBase64String(bytes) != attachment.DataBase64) throw new ArgumentException("첨부 파일의 데이터나 크기가 올바르지 않습니다.");
        if (DetectMediaType(bytes) != attachment.MediaType) throw new ArgumentException("파일 형식과 첨부 데이터가 일치하지 않습니다.");
        return bytes;
    }
    public static IReadOnlyList<RunAttachment>? Validate(IReadOnlyList<RunAttachment>? attachments)
    {
        if (attachments is null || attachments.Count == 0) return null;
        if (attachments.Count > MaximumCount) throw new ArgumentException("첨부 파일은 최대 8개까지 가능합니다.");
        var copied = attachments.ToArray(); var ids = new HashSet<string>(); long total = 0;
        foreach (var attachment in copied)
        {
            total += Decode(attachment).Length;
            if (!ids.Add(attachment.Id) || total > MaximumTotalBytes) throw new ArgumentException("첨부 ID는 고유해야 하며 전체 크기는 8MiB 이하여야 합니다.");
        }
        return Array.AsReadOnly(copied);
    }
    public static string Summary(IReadOnlyList<RunAttachment>? attachments) => attachments is not { Count: > 0 } ? "" : "첨부: " + string.Join(", ", attachments.Select(a => $"{Wire.Clean(a.Name, 180)} ({DecodedLength(a):N0} bytes)"));
    public static int DecodedLength(RunAttachment attachment) => attachment.DataBase64.Length / 4 * 3 - (attachment.DataBase64.EndsWith("==", StringComparison.Ordinal) ? 2 : attachment.DataBase64.EndsWith('=') ? 1 : 0);
    internal static string Extension(RunAttachment attachment)
    {
        var known = attachment.MediaType switch { "image/png" => ".png", "image/jpeg" => ".jpg", "image/gif" => ".gif", "image/webp" => ".webp", "application/pdf" => ".pdf", _ => null };
        if (known is not null) return known;
        var suffix = Path.GetExtension(attachment.Name).ToLowerInvariant();
        return suffix.Length is >= 2 and <= 13 && suffix[1..].All(char.IsAsciiLetterOrDigit) ? suffix : attachment.MediaType == "text/plain" ? ".txt" : ".bin";
    }
}

public sealed class StagedAttachments : IAsyncDisposable
{
    public string DirectoryPath { get; }
    public IReadOnlyList<(RunAttachment Attachment, string Path)> Files { get; }
    private StagedAttachments(string directory, IReadOnlyList<(RunAttachment, string)> files) { DirectoryPath = directory; Files = files; }
    public static async Task<StagedAttachments> CreateAsync(IReadOnlyList<RunAttachment> attachments, CancellationToken token = default)
    {
        var validated = AttachmentSupport.Validate(attachments) ?? throw new ArgumentException("첨부 파일이 없습니다.");
        var directory = Directory.CreateTempSubdirectory("mighty-attachments-"); var files = new List<(RunAttachment, string)>();
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var security = new DirectorySecurity(); security.SetAccessRuleProtection(true, false);
                security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                directory.SetAccessControl(security);
            }
            else File.SetUnixFileMode(directory.FullName, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            for (var index = 0; index < validated.Count; index++)
            {
                token.ThrowIfCancellationRequested(); var attachment = validated[index]; var path = Path.Combine(directory.FullName, $"{index}-{Guid.NewGuid():N}{AttachmentSupport.Extension(attachment)}");
                var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                await using var stream = new FileStream(path, options);
                await stream.WriteAsync(AttachmentSupport.Decode(attachment), token); files.Add((attachment, path));
            }
            token.ThrowIfCancellationRequested(); return new(directory.FullName, files.AsReadOnly());
        }
        catch { directory.Delete(true); throw; }
    }
    public string InputFor(StartRunRequest request)
    {
        var prompt = string.IsNullOrWhiteSpace(request.Input) ? "첨부 파일을 확인해 주세요." : request.Input;
        var generic = Files.Where(f => request.Provider != "claude" || !f.Attachment.MediaType.StartsWith("image/", StringComparison.Ordinal) && f.Attachment.MediaType != "application/pdf").ToArray();
        if (generic.Length > 0)
        {
            var references = generic.Select(f => request.Provider == "gemini" ? (OperatingSystem.IsWindows() ? "@\"" + f.Path + "\"" : "@" + EscapeGeminiPath(f.Path)) + "\n" + JsonSerializer.Serialize(f.Attachment.Name, Wire.Json) + ": " + JsonSerializer.Serialize(f.Path, Wire.Json) : JsonSerializer.Serialize(f.Attachment.Name, Wire.Json) + ": " + JsonSerializer.Serialize(f.Path, Wire.Json));
            prompt += "\n\n첨부 파일 (이번 실행에만 제공된 사본):\n" + string.Join("\n", references);
        }
        if (request.Provider != "claude") return prompt;
        var content = new List<object> { new { type = "text", text = prompt } };
        foreach (var file in Files.Where(f => f.Attachment.MediaType.StartsWith("image/", StringComparison.Ordinal) || f.Attachment.MediaType == "application/pdf")) content.Add(new { type = file.Attachment.MediaType == "application/pdf" ? "document" : "image", source = new { type = "base64", media_type = file.Attachment.MediaType, data = file.Attachment.DataBase64 } });
        return JsonSerializer.Serialize(new { type = "user", message = new { role = "user", content }, parent_tool_use_id = (string?)null }, new JsonSerializerOptions(Wire.Json) { DefaultIgnoreCondition = JsonIgnoreCondition.Never, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n";
    }
    public List<string> ArgumentsFor(StartRunRequest request, string pluginDirectory)
    {
        var args = ProviderCatalog.Arguments(request, pluginDirectory);
        if (request.Provider == "claude")
        {
            args.AddRange(["--input-format", "stream-json"]);
            if (Files.Any(f => !f.Attachment.MediaType.StartsWith("image/", StringComparison.Ordinal) && f.Attachment.MediaType != "application/pdf")) args.AddRange(["--add-dir", DirectoryPath]);
        }
        else if (request.Provider == "codex")
        {
            foreach (var file in Files.Where(f => f.Attachment.MediaType.StartsWith("image/", StringComparison.Ordinal))) args.InsertRange(args.Count - 1, ["--image", file.Path]);
        }
        else args.AddRange(["--include-directories", DirectoryPath]);
        return args;
    }
    private static string EscapeGeminiPath(string path) => string.Concat(path.Select(c => char.IsWhiteSpace(c) || c is '(' or ')' or '[' or ']' or '{' or '}' or '\\' or ',' or ';' or '!' or '?' or '"' or '\'' ? "\\" + c : c.ToString()));
    public ValueTask DisposeAsync() { if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, true); return ValueTask.CompletedTask; }
}
