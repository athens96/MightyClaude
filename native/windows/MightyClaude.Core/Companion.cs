using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace MightyClaude.Core;

public sealed record CompanionPreferences(bool Enabled = true, bool ShowsTask = true, bool Notifications = true,
    bool ReducedMotion = false, string SelectedPet = "mighty-raccoon", double? BubbleWidth = null, double? BubbleHeight = null,
    int? Left = null, int? Top = null)
{
    public static CompanionPreferences Load(string profile)
    {
        try
        {
            var bytes = StyleFiles.Read(profile, "companion-settings.json", 65_536); var value = bytes is null ? new() : JsonSerializer.Deserialize<CompanionPreferences>(bytes, Wire.Json) ?? new CompanionPreferences();
            return value with { BubbleWidth = value.BubbleWidth is { } w && double.IsFinite(w) ? CompanionBubbleLayout.Width(w) : null,
                BubbleHeight = CompanionBubbleLayout.Height(value.BubbleHeight) };
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or ArgumentException) { return new(); }
    }
    public void Save(string profile)
    {
        Directory.CreateDirectory(profile);
        var destination = Path.Combine(profile, "companion-settings.json");
        if (File.Exists(destination) && (File.GetAttributes(destination) & FileAttributes.ReparsePoint) != 0) throw new IOException("Companion settings cannot be a link.");
        var temp = Path.Combine(profile, ".companion-" + Guid.NewGuid().ToString("N"));
        try { using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) JsonSerializer.Serialize(file, this, Wire.Json); File.Move(temp, destination, true); }
        finally { try { File.Delete(temp); } catch (IOException) { } }
    }
}

public sealed record CompanionPet(string Id, string Name, string Source, int Width, int Height, byte[] Image)
{
    public const int MaximumBytes = 20 * 1024 * 1024;
    public static readonly IReadOnlyList<int> FrameCounts = Array.AsReadOnly(new[] { 6, 8, 8, 4, 5, 8, 6, 6, 6 });
    private static readonly double[][] Durations = [
        [.28, .11, .11, .14, .14, .32], [.12, .12, .12, .12, .12, .12, .12, .22], [.12, .12, .12, .12, .12, .12, .12, .22],
        [.14, .14, .14, .28], [.14, .14, .14, .14, .28], [.14, .14, .14, .14, .14, .14, .14, .24],
        [.15, .15, .15, .15, .15, .26], [.12, .12, .12, .12, .12, .22], [.15, .15, .15, .15, .15, .28]];
    public static int Frame(int row, double elapsed, bool reducedMotion)
    {
        if (reducedMotion || !double.IsFinite(elapsed)) return 0;
        var timing = Durations[Math.Clamp(row, 0, 8)]; var phase = Math.Max(0, elapsed) % timing.Sum();
        for (var i = 0; i < timing.Length; i++) { if (phase < timing[i]) return i; phase -= timing[i]; }
        return 0;
    }
    public static CompanionPet Load(string selected, string id)
    {
        selected = Path.GetFullPath(selected); var manifest = Directory.Exists(selected) ? Path.Combine(selected, "pet.json") : selected;
        var source = selected; var name = Path.GetFileNameWithoutExtension(selected); int? version = null;
        if (Path.GetExtension(manifest).Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            var folder = Path.GetDirectoryName(manifest)!;
            var bytes = StyleFiles.Read(folder, Path.GetFileName(manifest), 65_536) ?? throw Invalid("manifest");
            using var doc = JsonDocument.Parse(bytes); var value = doc.RootElement;
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("spritesheetPath", out var path) || path.ValueKind != JsonValueKind.String
                || !StyleManifestDecoder.SafeRelativePath(path.GetString()!)) throw Invalid("manifest path");
            source = WorkspaceFiles.Resolve(path.GetString()!, folder) ?? throw Invalid("manifest path");
            if (value.TryGetProperty("displayName", out var title) && title.ValueKind == JsonValueKind.String) name = Wire.Clean(title.GetString(), 80);
            if (value.TryGetProperty("spriteVersionNumber", out var v)) { if (!v.TryGetInt32(out var n)) throw Invalid("version"); version = n; }
        }
        if (Path.GetExtension(source).ToLowerInvariant() is not (".png" or ".webp")) throw Invalid("format");
        var image = StyleFiles.Read(Path.GetDirectoryName(source)!, Path.GetFileName(source), MaximumBytes) ?? throw Invalid("image");
        var shape = InspectImage(image);
        if (shape.Width != 1536 || shape.Height is not (1872 or 2288 or 2496) || !shape.Alpha) throw Invalid("dimensions or transparency");
        if (version is not null && (version switch { 1 => 1872, 2 => 2288, 3 => 2496, _ => 0 }) != shape.Height) throw Invalid("version");
        return new(id, string.IsNullOrWhiteSpace(name) ? "Pet" : name, source, shape.Width, shape.Height, image);
    }
    public static IReadOnlyList<CompanionPet> Catalog(string bundled, string profile, string? codexHome = null)
    {
        var result = new List<CompanionPet>();
        void Add(string path, string id) { try { result.Add(Load(path, id) with { Image = [] }); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or JsonException or InvalidOperationException or OverflowException) { } }
        Add(Path.Combine(bundled, "mighty-raccoon"), "mighty-raccoon");
        codexHome ??= Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        foreach (var (root, prefix) in new[] { (Path.Combine(profile, "pets"), "local:"), (Path.Combine(codexHome, "pets"), "codex:") })
        {
            try { foreach (var directory in Directory.EnumerateDirectories(root).Where(d => !Path.GetFileName(d).StartsWith('.')).Order(StringComparer.Ordinal).Take(40)) Add(directory, prefix + Path.GetFileName(directory)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return result;
    }
    // The GUI fully decodes and checks alpha before calling Install. Writing the
    // validated snapshot prevents the source changing between preview and copy.
    public CompanionPet Install(string profile)
    {
        var root = Path.Combine(profile, "pets"); Directory.CreateDirectory(root);
        var realProfile = WorkspaceFiles.RealPath(profile); var realRoot = WorkspaceFiles.RealPath(root);
        if (realProfile is null || realRoot is null || !WorkspaceFiles.Contains(realRoot, realProfile) || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) throw Invalid("destination");
        var id = Guid.NewGuid().ToString("N"); var destination = Path.Combine(realRoot, id); Directory.CreateDirectory(destination);
        try
        {
            var filename = "spritesheet" + Path.GetExtension(Source).ToLowerInvariant();
            using (var stream = new FileStream(Path.Combine(destination, filename), FileMode.CreateNew, FileAccess.Write, FileShare.None)) stream.Write(Image);
            using (var stream = new FileStream(Path.Combine(destination, "pet.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
                JsonSerializer.Serialize(stream, new { id, displayName = Name, spritesheetPath = filename }, Wire.Json);
            return Load(destination, "local:" + id);
        }
        catch { try { Directory.Delete(destination, true); } catch (IOException) { } throw; }
    }
    private static IOException Invalid(string reason) => new("Invalid Codex pet: " + reason + ". Expected transparent PNG/WebP, 1536 × 1872/2288/2496, at most 20 MiB.");

    // Header bounds are checked before invoking any platform image decoder. This
    // is metadata validation; the GUI must still fully decode the selected image.
    public static (int Width, int Height, bool Alpha) InspectImage(ReadOnlySpan<byte> image)
    {
        if (image.Length is < 25 or > MaximumBytes) throw Invalid("size");
        if (image[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
        {
            if (image.Length < 33 || !image.Slice(12, 4).SequenceEqual("IHDR"u8) || BinaryPrimitives.ReadUInt32BigEndian(image[8..]) != 13) throw Invalid("PNG header");
            int width = checked((int)BinaryPrimitives.ReadUInt32BigEndian(image[16..])), height = checked((int)BinaryPrimitives.ReadUInt32BigEndian(image[20..]));
            bool alpha = image[25] is 4 or 6;
            for (var offset = 8; offset <= image.Length - 12;)
            {
                var length = BinaryPrimitives.ReadUInt32BigEndian(image[offset..]); if (length > image.Length - offset - 12) throw Invalid("PNG chunk");
                if (image.Slice(offset + 4, 4).SequenceEqual("tRNS"u8)) alpha = true;
                offset += checked((int)length + 12);
            }
            return (width, height, alpha);
        }
        if (!image[..4].SequenceEqual("RIFF"u8) || !image.Slice(8, 4).SequenceEqual("WEBP"u8) || BinaryPrimitives.ReadUInt32LittleEndian(image[4..]) != image.Length - 8) throw Invalid("WebP header");
        for (var offset = 12; offset <= image.Length - 8;)
        {
            var length = BinaryPrimitives.ReadUInt32LittleEndian(image[(offset + 4)..]); if (length > image.Length - offset - 8) throw Invalid("WebP chunk");
            var data = image.Slice(offset + 8, (int)length);
            if (image.Slice(offset, 4).SequenceEqual("VP8X"u8) && data.Length >= 10)
                return (1 + data[4] + (data[5] << 8) + (data[6] << 16), 1 + data[7] + (data[8] << 8) + (data[9] << 16), (data[0] & 16) != 0);
            if (image.Slice(offset, 4).SequenceEqual("VP8L"u8) && data.Length >= 5 && data[0] == 0x2f)
            {
                var bits = BinaryPrimitives.ReadUInt32LittleEndian(data[1..]); return ((int)(bits & 0x3fff) + 1, (int)((bits >> 14) & 0x3fff) + 1, (bits & (1u << 28)) != 0);
            }
            offset += checked((int)length + 8 + ((int)length & 1));
        }
        throw Invalid("WebP transparency");
    }
}

public static class CompanionAnimation
{
    public static int Priority(string status) => status switch { "waiting" => 5, "running" or "starting" or "queued" => 4, "error" => 3, "completed" => 2, _ => 1 };
    public static int Row(string status, AgentActivity? activity, bool celebrating = false)
    {
        if (status == "waiting") return 6;
        if (status is "error" or "failed" or "stopped" or "cancelled") return 5;
        if (status == "completed") return celebrating ? 4 : 0;
        if (status is not ("running" or "starting" or "queued")) return 0;
        if (activity?.State == "waiting") return 6;
        if (activity?.State == "error") return 5;
        if (activity?.Kind is "read" or "search" or "web" or "review") return 8;
        if (activity?.Kind is "edit" or "write" or "command" or "thinking" or "agent" or "turn") return 7;
        var summary = activity?.Summary.ToLowerInvariant() ?? "";
        return new[] { "read", "search", "review", "inspect", "\uC77D", "\uAC80\uC0C9", "\uAC80\uD1A0", "\uCC3E\uB294" }.Any(summary.Contains) ? 8 : 7;
    }
}

public static class CompanionCarousel
{
    public static string? Shown(string? pinned, IReadOnlyList<string> active, string? fallback) => pinned is not null && active.Contains(pinned) ? pinned : fallback;
    public static string? Step(string? shown, IReadOnlyList<string> active, int offset)
    {
        if (active.Count <= 1 || offset == 0) return null;
        var index = active.ToList().FindIndex(id => id == shown);
        return index < 0 ? offset > 0 ? active[0] : active[^1] : active[(int)(((long)index + offset) % active.Count + active.Count) % active.Count];
    }
    public static int? Position(string? shown, IReadOnlyList<string> active) { var index = active.ToList().FindIndex(id => id == shown); return index < 0 ? null : index + 1; }
}
