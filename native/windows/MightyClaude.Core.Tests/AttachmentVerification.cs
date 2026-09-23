using System.Text;
using System.Text.Json;
using MightyClaude.Core;

internal static class AttachmentVerification
{
    internal static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScLbtAAAAABJRU5ErkJggg==");
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Reject(Action action) { try { action(); } catch (ArgumentException) { return; } throw new InvalidOperationException("Invalid attachment was accepted."); }
    internal static async Task Run()
    {
        var image = AttachmentSupport.Make("../picture.txt", Png); var text = AttachmentSupport.Make("source😀.cs", Encoding.UTF8.GetBytes("\uFEFFa\u200Db")); var pdf = AttachmentSupport.Make("file.pdf", "%PDF-1.7\nfixture"u8.ToArray());
        Check(image.Name == "picture.txt" && image.MediaType == "image/png", "Image type must come from bytes."); Check(text.MediaType == "text/plain", "BOM and ZWJ text must match Swift sniffing.");
        var longName = AttachmentSupport.Make(new string('a', 179) + "😀.txt", "text"u8.ToArray()); Check(longName.Name.Length <= 180 && !char.IsSurrogate(longName.Name[^1]), "Name truncation split a surrogate pair.");
        var request = new StartRunRequest("attachment-run", "workspace", "claude", "", [], Provider: "codex", Attachments: [image, text, pdf]).Validate();
        var restored = Wire.Clone(request); Check(restored.Attachments!.SequenceEqual(request.Attachments!), "Attachment bytes/Unicode changed on wire.");
        var plusBytes = Enumerable.Range(0, 600000).Select(i => new byte[] { 251, 239, 190 }[i % 3]).ToArray(); var plusAttachment = AttachmentSupport.Make("binary.bin", plusBytes);
        Check(JsonSerializer.SerializeToUtf8Bytes(request with { Attachments = [plusAttachment] }, Wire.Json).Length < 850000, "Base64 escaping inflated the negotiated upload limit.");
        using (var json = JsonDocument.Parse(JsonSerializer.Serialize(request with { Input = "text", Attachments = [] }, Wire.Json))) Check(!json.RootElement.TryGetProperty("attachments", out _), "Empty attachments must be omitted for legacy hosts.");
        Check(!JsonSerializer.Deserialize<ProviderCapabilities>("""{"effort":true,"permissionModes":["manual"],"maxTurns":false,"maxBudgetUsd":false,"resume":true}""", Wire.Json)!.Attachments, "Legacy host attachment capability must be false.");
        Reject(() => (request with { Attachments = null }).Validate()); Reject(() => (request with { Kind = "shell" }).Validate());
        Reject(() => AttachmentSupport.Decode(image with { Name = "../escape" })); Reject(() => AttachmentSupport.Decode(image with { Name = "bad\0name" }));
        Reject(() => AttachmentSupport.Decode(image with { DataBase64 = image.DataBase64 + "\n" })); Reject(() => AttachmentSupport.Decode(image with { MediaType = "image/jpeg" })); Reject(() => AttachmentSupport.Decode(image with { MediaType = "application/octet-stream" }));
        Reject(() => AttachmentSupport.Validate([image, image])); Reject(() => AttachmentSupport.Validate(Enumerable.Range(0, 9).Select(_ => image with { Id = Wire.Id() }).ToArray()));
        Reject(() => AttachmentSupport.Make("big", new byte[AttachmentSupport.MaximumFileBytes + 1]));
        var large = AttachmentSupport.Make("large.bin", new byte[5 * 1024 * 1024]); Reject(() => AttachmentSupport.Validate([large, large with { Id = Wire.Id() }]));
        string directory;
        await using (var staged = await StagedAttachments.CreateAsync(request.Attachments!))
        {
            directory = staged.DirectoryPath; Check(staged.Files.All(f => Path.GetDirectoryName(f.Path) == directory && !Path.GetFileName(f.Path).Contains(f.Attachment.Name)), "Untrusted file names reached staging paths.");
            Check(staged.Files[0].Path.EndsWith(".png") && staged.Files[1].Path.EndsWith(".cs"), "Staged extensions are wrong.");
            Check((await File.ReadAllBytesAsync(staged.Files[0].Path)).SequenceEqual(Png), "Staged image changed.");
            if (!OperatingSystem.IsWindows()) Check(File.GetUnixFileMode(directory) == (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute), "Staging directory is not private.");
            foreach (var resume in new string?[] { null, "resume-id" })
            {
                var codex = request with { ResumeId = resume }; var args = staged.ArgumentsFor(codex, "/plugin"); Check(args.Contains("--image") && args[^1] == "-" && !args.Contains("--add-dir"), "Codex image/resume mapping widened permissions or omitted stdin.");
            }
            var claude = request with { Provider = "claude" }; using var input = JsonDocument.Parse(staged.InputFor(claude)); var content = input.RootElement.GetProperty("message").GetProperty("content"); Check(content.EnumerateArray().Any(c => c.GetProperty("type").GetString() == "image") && content.EnumerateArray().Any(c => c.GetProperty("type").GetString() == "document"), "Claude multimodal blocks are missing."); Check(staged.ArgumentsFor(claude, "/plugin").Contains("--input-format"), "Claude needs stream JSON input.");
            var gemini = request with { Provider = "gemini" }; Check(staged.ArgumentsFor(gemini, "/plugin").Contains("--include-directories") && staged.InputFor(gemini).Contains('@'), "Gemini file references are missing.");
        }
        Check(!Directory.Exists(directory), "Staging directory was not removed.");
        using var cancel = new CancellationTokenSource(); cancel.Cancel(); try { await StagedAttachments.CreateAsync(request.Attachments!, cancel.Token); throw new InvalidOperationException("Cancelled staging succeeded."); } catch (OperationCanceledException) { }
    }
}
