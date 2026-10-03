using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using MightyClaude.Core;

/// <summary>
/// Pictures agents show, inline in the Windows transcript: kept from stream-json / exec
/// JSON as references in a content-addressed cache, Markdown pictures under the path
/// rule, saved state with references only, and the RTF picture the transcript draws.
/// Mirrors native/macos/Tests/MightyCoreTests/AgentImageTests.swift.
/// </summary>
internal static class AgentImageVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    /// <summary>A real PNG of <paramref name="width"/> × <paramref name="height"/> pixels in one colour.</summary>
    internal static byte[] Png(int width = 8, int height = 6, byte red = 255)
    {
        static uint Crc(ReadOnlySpan<byte> bytes)
        {
            var crc = 0xFFFF_FFFFu;
            foreach (var b in bytes) { crc ^= b; for (var k = 0; k < 8; k++) crc = (crc & 1) == 1 ? crc >> 1 ^ 0xEDB8_8320u : crc >> 1; }
            return ~crc;
        }
        static void Chunk(MemoryStream output, string type, byte[] body)
        {
            var length = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(length, (uint)body.Length); output.Write(length);
            var typed = Encoding.ASCII.GetBytes(type).Concat(body).ToArray(); output.Write(typed);
            var crc = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(crc, Crc(typed)); output.Write(crc);
        }
        var header = new byte[13]; BinaryPrimitives.WriteUInt32BigEndian(header, (uint)width); BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)height); header[8] = 8; header[9] = 2;
        var raw = new MemoryStream();
        for (var y = 0; y < height; y++) { raw.WriteByte(0); for (var x = 0; x < width; x++) { raw.WriteByte(red); raw.WriteByte(102); raw.WriteByte(51); } }
        var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true)) zlib.Write(raw.ToArray());
        var png = new MemoryStream(); png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        Chunk(png, "IHDR", header); Chunk(png, "IDAT", compressed.ToArray()); Chunk(png, "IEND", []);
        return png.ToArray();
    }

    /// <summary>A PNG whose header claims <paramref name="width"/> × <paramref name="height"/> pixels with no pixels behind it.</summary>
    private static byte[] PngHeader(uint width, uint height)
    {
        var bytes = Png(1, 1);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16), width); BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20), height);
        return bytes;
    }

    private static string Line(object value) => JsonSerializer.Serialize(value);
    private static object ClaudeImage(byte[] data, string type = "image/png") => new { type = "image", source = new { type = "base64", media_type = type, data = Convert.ToBase64String(data) } };

    private static (OutputParser Parser, List<LogEntry> Entries, List<(string Kind, string Text)> Logs, List<AgentActivity> Activities) Parser(string provider, AgentImageCache? cache, string? root = null)
    {
        var entries = new List<LogEntry>(); var logs = new List<(string, string)>(); var activities = new List<AgentActivity>();
        var parser = new OutputParser(provider, (kind, text) => logs.Add((kind, text)), _ => { }, activities.Add, activityNamespace: "run-images", images: cache, imageEntry: entries.Add) { ImageRoot = root };
        return (parser, entries, logs, activities);
    }

    internal static Task ClaudeReadAndMcpScreenshotResultsBecomeImageEntriesWithoutBase64()
    {
        var directory = Verification.Temp();
        try
        {
            var cache = new AgentImageCache(Path.Combine(directory, "image-cache"));
            var (parser, entries, logs, activities) = Parser("claude", cache);
            var shot = Png(12, 9); var screenshot = Png(20, 10, 40);
            parser.Parse(Line(new { type = "assistant", uuid = "a1", message = new { id = "m1", content = new object[] {
                new { type = "tool_use", id = "read-1", name = "Read", input = new { file_path = "/w/shot.png" } },
                new { type = "tool_use", id = "mcp-1", name = "mcp__playwright__browser_take_screenshot", input = new { } } } } }));
            // Claude also echoes the file in `tool_use_result`; only the content block counts.
            parser.Parse(Line(new { type = "user", tool_use_result = new { type = "image", file = new { base64 = Convert.ToBase64String(shot) } }, message = new { content = new object[] {
                new { type = "tool_result", tool_use_id = "read-1", content = new[] { ClaudeImage(shot) } } } } }));
            parser.Parse(Line(new { type = "user", message = new { content = new object[] {
                new { type = "tool_result", tool_use_id = "mcp-1", content = new object[] { new { type = "text", text = "Took a screenshot" }, ClaudeImage(screenshot) } } } } }));
            parser.Flush();

            Check(entries.Count == 2, "a Read picture and a screenshot make two entries, got " + entries.Count);
            var read = entries[0];
            Check(read.Kind == "image" && read.Provider == "claude" && Wire.Identifier(read.Id), "an image entry from Claude");
            var readRef = read.Images!.Single();
            Check(readRef.Hash == AgentImageSupport.Sha256(shot) && readRef.MediaType == "image/png" && readRef.Width == 12 && readRef.Height == 9 && readRef.Bytes == shot.Length, "the reference names the picture");
            Check(readRef.Path == "/w/shot.png" && readRef.Source.Contains("Read", StringComparison.Ordinal), "a Read picture keeps its file and tool");
            Check(cache.Data(readRef)!.SequenceEqual(shot), "the cache holds the bytes");
            Check(read.Text == Locale.Get("images.entry.one", new Dictionary<string, string> { ["source"] = readRef.Source }), "the entry text is images.entry.one");
            var mcp = entries[1].Images!.Single();
            Check(mcp.Source.Contains("mcp__playwright__browser_take_screenshot", StringComparison.Ordinal) && mcp.Width == 20 && mcp.Path is null, "a screenshot names its tool");
            // Neither the entries nor the tool rows carry the picture's bytes.
            var encoded = JsonSerializer.Serialize(entries, Wire.Json) + JsonSerializer.Serialize(activities, Wire.Json);
            Check(!encoded.Contains(Convert.ToBase64String(shot)[..40], StringComparison.Ordinal) && !encoded.Contains(Convert.ToBase64String(screenshot)[..40], StringComparison.Ordinal), "no base64 leaves the parser");
            Check(activities.Last(a => a.ToolName == "mcp__playwright__browser_take_screenshot").Output == "Took a screenshot", "the tool row keeps only the text");
            Check(logs.Count == 0, "nothing else is logged");
            // A message's own image block, and the same picture again, are kept once on disk.
            parser.Parse(Line(new { type = "assistant", uuid = "a2", message = new { id = "m2", content = new[] { ClaudeImage(shot) } } }));
            Check(entries.Count == 3 && entries[2].Images!.Single().Hash == readRef.Hash && entries[2].Text == Locale.Get("images.entry.one", new Dictionary<string, string> { ["source"] = Locale.Get("images.source.agent") }), "a message picture is the agent's");
            Check(Directory.GetFiles(cache.Directory).Length == 2, "one file per distinct picture");
        }
        finally { Directory.Delete(directory, true); }
        return Task.CompletedTask;
    }

    internal static Task WithoutACacheOrForASubAgentPicturesStayOut()
    {
        var (plain, entries, _, _) = Parser("claude", null);
        plain.Parse(Line(new { type = "user", message = new { content = new object[] { new { type = "tool_result", tool_use_id = "t", content = new[] { ClaudeImage(Png()) } } } } }));
        Check(entries.Count == 0, "without a cache pictures are left out as before");
        var directory = Verification.Temp();
        try
        {
            var (parser, childEntries, logs, _) = Parser("claude", new AgentImageCache(Path.Combine(directory, "c")));
            parser.Parse(Line(new { type = "user", parent_tool_use_id = "agent-1", message = new { content = new object[] { new { type = "tool_result", tool_use_id = "read-1", content = new[] { ClaudeImage(Png()) } } } } }));
            Check(childEntries.Count == 0 && logs.Count == 0, "a sub-agent's pictures stay out of the request's transcript");
        }
        finally { Directory.Delete(directory, true); }
        return Task.CompletedTask;
    }

    internal static Task RefusedPicturesLeaveANoticeAndNoEntry()
    {
        var directory = Verification.Temp();
        try
        {
            var cache = new AgentImageCache(Path.Combine(directory, "c"));
            var (parser, entries, logs, _) = Parser("claude", cache);
            var svg = Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"><image href=\"file:///etc/passwd\"/></svg>");
            parser.Parse(Line(new { type = "user", message = new { content = new object[] { new { type = "tool_result", tool_use_id = "svg", content = new[] { ClaudeImage(svg, "image/svg+xml") } } } } }));
            parser.Parse(Line(new { type = "user", message = new { content = new object[] { new { type = "tool_result", tool_use_id = "text", content = new[] { ClaudeImage("not a picture"u8.ToArray()) } } } } }));
            Check(entries.Count == 0, "refused pictures make no entry");
            Check(logs.Select(l => l.Text).SequenceEqual(new[] { Locale.Get("images.failed.externalSVG"), Locale.Get("images.failed.undecodable") }) && logs.All(l => l.Kind == "system"), "each refusal leaves one system line");
            Check(cache.TotalBytes == 0, "nothing refused is written");
        }
        finally { Directory.Delete(directory, true); }
        return Task.CompletedTask;
    }

    internal static Task CodexMcpResultsImageViewAndImageGenerationBecomeEntries()
    {
        var directory = Verification.Temp();
        try
        {
            var workspace = Path.Combine(directory, "workspace"); Directory.CreateDirectory(workspace);
            var viewed = Png(5, 4, 10); File.WriteAllBytes(Path.Combine(workspace, "viewed.png"), viewed);
            var cache = new AgentImageCache(Path.Combine(directory, "c"));
            var (parser, entries, logs, _) = Parser("codex", cache, workspace);
            var shot = Png(7, 3, 90); var generated = Png(9, 9, 200);
            parser.Parse(Line(new { type = "item.completed", item = new { id = "mcp-1", type = "mcp_tool_call", server = "playwright", tool = "browser_take_screenshot", status = "completed", arguments = new { }, result = new { content = new object[] { new { type = "text", text = "ok" }, new { type = "image", data = Convert.ToBase64String(shot), mimeType = "image/png" } } } } }));
            parser.Parse(Line(new { type = "item.completed", item = new { id = "view-1", type = "image_view", path = Path.Combine(workspace, "viewed.png") } }));
            parser.Parse(Line(new { type = "item.completed", item = new { id = "gen-1", type = "image_generation", result = Convert.ToBase64String(generated) } }));
            parser.Parse(Line(new { type = "item.completed", item = new { id = "view-2", type = "image_view", path = "/definitely/not/here.png" } }));
            Check(entries.Count == 3, "MCP, image_view and image_generation each make an entry, got " + entries.Count);
            Check(entries[0].Images!.Single().Hash == AgentImageSupport.Sha256(shot) && entries[0].Images![0].Source.Contains("playwright.browser_take_screenshot", StringComparison.Ordinal), "the MCP picture names its tool");
            var view = entries[1].Images!.Single();
            Check(view.Hash == AgentImageSupport.Sha256(viewed) && view.Source == "viewed.png" && view.Path is not null && view.Path.EndsWith("viewed.png", StringComparison.Ordinal), "image_view reads the file inside the workspace");
            Check(entries[2].Images!.Single().Hash == AgentImageSupport.Sha256(generated), "image_generation keeps its base64 result");
            Check(logs.Single().Text == Locale.Get("images.failed.outside", new Dictionary<string, string> { ["path"] = "/definitely/not/here.png" }), "a path outside the allowed folders leaves a notice");
            Check(entries.All(e => e.Provider == "codex"), "Codex entries");
        }
        finally { Directory.Delete(directory, true); }
        return Task.CompletedTask;
    }

    internal static Task HeadersGiveTypeAndSizeAndCapsRefuseOversizePictures()
    {
        Check(AgentImageSupport.Inspect(Png(12, 9), "image/jpeg") == ("image/png", 12, 9), "a bitmap is stored as what its header says");
        var gif = "GIF89a"u8.ToArray().Concat(new byte[] { 3, 0, 2, 0, 0, 0, 0 }).ToArray();
        Check(AgentImageSupport.Inspect(gif, "image/gif") == ("image/gif", 3, 2), "gif size");
        var bmp = new byte[54]; bmp[0] = (byte)'B'; bmp[1] = (byte)'M'; BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(14), 40); BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(18), 6); BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(22), -4);
        Check(AgentImageSupport.Inspect(bmp, "image/bmp") == ("image/bmp", 6, 4), "bmp size, top-down rows included");
        var webp = new byte[30]; "RIFF"u8.CopyTo(webp); "WEBP"u8.CopyTo(webp.AsSpan(8)); "VP8X"u8.CopyTo(webp.AsSpan(12)); webp[24] = 99; webp[27] = 49;
        Check(AgentImageSupport.Inspect(webp, "image/webp") == ("image/webp", 100, 50), "webp canvas size");
        var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x04, 0x00, 0x00, 0xFF, 0xC0, 0x00, 0x11, 0x08, 0x00, 0x20, 0x00, 0x30, 0x03 };
        Check(AgentImageSupport.Inspect(jpeg, "image/jpeg") == ("image/jpeg", 48, 32), "jpeg frame size");
        static AgentImageError? Refusal(byte[] data, string type) { try { AgentImageSupport.Inspect(data, type); return null; } catch (AgentImageException ex) { return ex.Error; } }
        Check(Refusal(PngHeader(8_001, 10), "image/png") == AgentImageError.TooManyPixels, "a side over 8,000 pixels");
        Check(Refusal(PngHeader(8_000, 8_001), "image/png") == AgentImageError.TooManyPixels, "over 64 MP");
        Check(Refusal(PngHeader(8_000, 8_000), "image/png") is null, "exactly 64 MP is kept");
        Check(Refusal(new byte[AgentImageSupport.MaximumImageBytes + 1], "image/png") == AgentImageError.TooLarge, "over 20 MB");
        Check(Refusal([], "image/png") == AgentImageError.Empty, "empty");
        Check(Refusal(Png(), "application/pdf") == AgentImageError.UnsupportedType, "a pdf is not a picture");
        Check(Refusal("II*\0rest"u8.ToArray(), "image/tiff") == AgentImageError.UnsupportedType, "tiff needs a codec Windows lacks");
        Check(Refusal("<html/>"u8.ToArray(), "image/svg+xml") == AgentImageError.Undecodable, "an svg must be an svg document");
        Check(AgentImageSupport.Inspect("<?xml version=\"1.0\"?><!-- c --><!DOCTYPE svg><svg xmlns=\"http://www.w3.org/2000/svg\"/>"u8.ToArray(), "image/svg") == ("image/svg+xml", 0, 0), "an svg after its prologue");
        Check(AgentImageSupport.MediaType("image/JPG") == "image/jpeg" && AgentImageSupport.MediaType("image/x-png") == "image/png" && AgentImageSupport.MediaType("text/plain") is null, "media type aliases");
        Check(AgentImageSupport.MediaTypeForFileName("a/b/Shot.JPEG") == "image/jpeg" && AgentImageSupport.MediaTypeForFileName("x.tif") == "image/tiff" && AgentImageSupport.MediaTypeForFileName("x.txt") is null, "media type by name");
        try { AgentImageSupport.DecodeBase64("@@@"); Check(false, "bad base64 must throw"); } catch (AgentImageException ex) { Check(ex.Error == AgentImageError.InvalidEncoding, "bad base64"); }
        Check(AgentImageSupport.DecodeBase64(" aGVs\nbG8= ").SequenceEqual("hello"u8.ToArray()), "base64 with whitespace");
        return Task.CompletedTask;
    }

    internal static Task TheCacheWritesOnceByHashRewritesADamagedFileAndEvictsTheLeastRecentlyUsed()
    {
        var directory = Verification.Temp();
        try
        {
            var first = Png(4, 4, 1); var second = Png(4, 4, 2); var third = Png(4, 4, 3);
            var cache = new AgentImageCache(Path.Combine(directory, "c"), maximumBytes: first.Length + second.Length + third.Length - 1);
            var a = cache.Store(first, "image/png", "a"); var path = cache.PathFor(a)!;
            Check(Path.GetFileName(path) == a.Hash + ".png", "named by hash and type");
            File.WriteAllBytes(path, "damaged"u8.ToArray());
            Check(cache.Data(a) is null, "damaged bytes are not trusted");
            cache.Store(first, "image/png", "a");
            Check(cache.Data(a)!.SequenceEqual(first), "storing again rewrites a damaged file");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-10));
            var b = cache.Store(second, "image/png", "b");
            File.SetLastWriteTimeUtc(cache.PathFor(b)!, DateTime.UtcNow.AddMinutes(-5));
            Check(cache.Data(a) is not null, "reading marks a used");
            var c = cache.Store(third, "image/png", "c");
            Check(cache.PathFor(b) is null && cache.PathFor(a) is not null && cache.PathFor(c) is not null, "over the cap the least recently used goes first");
            Check(cache.TotalBytes <= cache.MaximumBytes, "the cache stays under its cap");
        }
        finally { Directory.Delete(directory, true); }
        return Task.CompletedTask;
    }

    internal static Task MarkdownPicturesOutsideCodeFencesFollowThePathRule()
    {
        var pictures = AgentMarkdownImages.Extract("See ![shot](shots/a.png) and ![](<my file.png> \"title\")\n```\n![no](code.png)\n```\n![remote](https://example.com/x.png)");
        Check(pictures.Select(p => p.Source).SequenceEqual(new[] { "shots/a.png", "my file.png", "https://example.com/x.png" }) && pictures[0].Alt == "shot", "pictures outside fenced code, in order");
        Check(AgentMarkdownImages.Extract("no pictures here").Count == 0, "no pictures");
        var directory = Verification.Temp();
        try
        {
            var workspace = Path.Combine(directory, "workspace"); var temporary = Path.Combine(directory, "temporary"); var outside = Path.Combine(directory, "outside");
            foreach (var folder in new[] { workspace, Path.Combine(workspace, "shots"), temporary, outside }) Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(workspace, "shots", "a.png"), Png()); File.WriteAllBytes(Path.Combine(temporary, "스크린샷 1.png"), Png()); File.WriteAllBytes(Path.Combine(outside, "secret.png"), Png());
            File.WriteAllText(Path.Combine(workspace, "notes.txt"), "text");
            string[] roots = [temporary];
            AgentImageLocation Locate(string value) => AgentImagePaths.Locate(value, workspace, roots);
            Check(Locate("shots/a.png") is AgentImageLocation.File { } inside && inside.Path.EndsWith("a.png", StringComparison.Ordinal), "a relative picture inside the workspace");
            Check(Locate(Path.Combine(temporary, "스크린샷 1.png")) is AgentImageLocation.File, "an absolute picture in the temporary folder");
            Check(Locate(Path.Combine(temporary, Uri.EscapeDataString("스크린샷 1.png"))) is AgentImageLocation.File, "a percent-encoded Korean and spaced name");
            Check(Locate(Path.Combine(outside, "secret.png")) is AgentImageLocation.Refused, "outside both folders");
            Check(Locate("../outside/secret.png") is AgentImageLocation.Refused, "'..' leaving the workspace");
            Check(Locate(@"\\evil.invalid\share\a.png") is AgentImageLocation.Refused && Locate("//evil.invalid/share/a.png") is AgentImageLocation.Refused && Locate("file://evil.invalid/share/a.png") is AgentImageLocation.Refused, "a network share is refused before it is looked up");
            Check(Locate("~/secret.png") is AgentImageLocation.Refused && Locate("notes.txt") is AgentImageLocation.Refused && Locate("shots/missing.png") is AgentImageLocation.Refused, "home, non-pictures and missing files");
            Check(Locate(Path.Combine(temporary, "x.png")) is AgentImageLocation.Refused, "a missing file in the temporary folder");
            try
            {
                File.CreateSymbolicLink(Path.Combine(workspace, "link.png"), Path.Combine(outside, "secret.png"));
                Check(Locate("link.png") is AgentImageLocation.Refused, "a link pointing outside");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Windows without the link privilege: the rule is the same RealPath check */ }
            Check(Locate("https://example.com/x.png") is AgentImageLocation.Remote { Url.Host: "example.com" }, "http(s) is a link, never fetched");
            Check(Locate("data:image/png;base64,AAAA") is AgentImageLocation.Inline { MediaType: "image/png", Base64: "AAAA" }, "a data URI picture");
            Check(Locate("data:text/plain;base64,AAAA") is AgentImageLocation.Refused && Locate("javascript:alert(1)") is AgentImageLocation.Refused, "other URIs are refused");
            var read = AgentImagePaths.Read(((AgentImageLocation.File)Locate("shots/a.png")).Path, workspace);
            Check(read.SequenceEqual(Png()), "the safe open reads the file");
        }
        finally { Directory.Delete(directory, true); }
        return Task.CompletedTask;
    }

    internal static async Task ImageEntriesSurviveSavedStateWithReferencesOnly()
    {
        var hash = AgentImageSupport.Sha256(Png());
        var good = new AgentImageRef(hash, "image/png", 8, 6, Png().Length, "Read · /w/a.png", "/w/a.png");
        var workspace = new Workspace { Path = Path.GetTempPath() };
        var session = new RunSession { WorkspaceId = workspace.Id, Provider = "claude", Logs = [
            new("image-1", "image", "그림 · Read", Wire.Now(), "claude", null, [good, good with { Hash = "XYZ" }]),
            new("image-2", "image", "그림 · broken", Wire.Now(), "claude", null, [good with { Bytes = 0 }]),
            new("text-1", "assistant", "done", Wire.Now(), "claude", null, [good]),
        ] };
        var encoded = JsonSerializer.Serialize(new AppSnapshot { Version = 1, Workspaces = [workspace], Sessions = [session] }, Wire.Json);
        using (var document = JsonDocument.Parse(encoded))
        {
            var saved = document.RootElement.GetProperty("sessions")[0].GetProperty("logs")[0].GetProperty("images")[0];
            Check(new[] { "hash", "mediaType", "width", "height", "bytes", "source", "path" }.All(name => saved.TryGetProperty(name, out _)), "references keep the macOS field names");
        }
        var directory = Verification.Temp();
        try
        {
            await StateStore.AtomicWriteAsync(Path.Combine(directory, "workspace-state.json"), Encoding.UTF8.GetBytes(encoded));
            var logs = (await new StateStore(directory).LoadAsync()).Sessions.Single(s => s.Id == session.Id).Logs;
            Check(logs.Single(l => l.Id == "image-1").Images!.Single() == good, "a valid reference survives, a malformed one is dropped");
            Check(logs.Single(l => l.Id == "image-2") is { Kind: "image", Images: null }, "an entry whose pictures are all malformed keeps its text");
            Check(logs.Single(l => l.Id == "text-1").Images is null, "only image entries carry pictures");
        }
        finally { Directory.Delete(directory, true); }
        var entry = new LogEntry("image-3", "image", "그림 · Read", Wire.Now(), "claude", null, [good]);
        Check(new RunEvent("s1", "log", entry).Valid() && !new RunEvent("s1", "log", entry with { Images = null }).Valid(), "an image event needs a valid reference");
        var applied = new AppSnapshot { Sessions = [new RunSession { Id = "s1" }] }.Apply(new RunEvent("s1", "log", entry));
        Check(applied.Sessions[0].Logs.Single().Images!.Single() == good, "the transcript keeps the image entry");
    }

    internal static Task TheTranscriptDrawsAPngThumbnailAtMost480By640()
    {
        Check(AgentImageRtf.DisplaySize(1920, 1080) == (480, 270), "a wide screenshot fits 480 wide");
        Check(AgentImageRtf.DisplaySize(600, 1600) == (240, 640), "a tall picture fits 640 high");
        Check(AgentImageRtf.DisplaySize(40, 30) == (40, 30), "a small picture is never enlarged");
        Check(AgentImageRtf.DisplaySize(0, 30) == (0, 0), "an svg without a size draws nothing");
        var png = Png(2, 2);
        var picture = AgentImageRtf.Picture(png, 2, 2, (40, 30));
        Check(picture.StartsWith(@"{\pict\pngblip\picw2\pich2\picwgoal600\pichgoal450 ", StringComparison.Ordinal) && picture.EndsWith('}'), "a pngblip group sized in twips");
        var hex = string.Concat(picture[(picture.IndexOf(' ') + 1)..^1].Where(c => c != '\n'));
        Check(Convert.FromHexString(hex).SequenceEqual(png), "the group carries the PNG bytes in hex");
        Check(AgentImageRtf.Picture(Png(40, 40), 40, 40, (40, 40)).Split('\n').Skip(1).All(line => line.Length <= 129), "hex lines stay short");
        Check(AgentImageRtf.Picture(new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 }, 1, 1, (1, 1)).StartsWith(@"{\pict\jpegblip\picw1\pich1\picwgoal15\pichgoal15 ffd8ffd9", StringComparison.Ordinal), "an opaque thumbnail goes in as a jpegblip");
        return Task.CompletedTask;
    }

    internal static async Task ALineCarryingAPictureOverOneMiBReachesTheParser()
    {
        var big = new string('a', 3 * 1024 * 1024);
        var text = "short\n" + big + "\nafter\n";
        async Task<List<string>> Read(int? limit)
        {
            using var reader = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(text)));
            var lines = new List<string>();
            await foreach (var line in limit is { } value ? OutputParser.LinesAsync(reader, default, value) : OutputParser.LinesAsync(reader)) lines.Add(line);
            return lines;
        }
        var kept = await Read(AgentImageSupport.MaximumLineCharacters);
        Check(kept.Count == 3 && kept[1].Length == big.Length && kept[2] == "after", "an agent's 3 MiB line arrives whole");
        var dropped = await Read(null);
        Check(dropped.SequenceEqual(new[] { "short", "after" }), "other output keeps the 1 MiB line");
    }
}
