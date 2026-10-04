using System.Buffers.Binary;
using System.Text.Json;
using MightyClaude.Core;

internal static class CompanionVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    internal static Task AnimationAndCarousel()
    {
        Check(CompanionPet.Frame(0, .29, false) == 1 && CompanionPet.Frame(0, 100, true) == 0, "durations/reduced motion");
        for (var row = 0; row < 9; row++) for (var tick = 0; tick < 100; tick++) Check(CompanionPet.Frame(row, tick / 30.0, false) < CompanionPet.FrameCounts[row], "frame bounds");
        Check(CompanionAnimation.Row("running", new("r", "claude", "read", "running", "read")) == 8, "review animation");
        Check(CompanionAnimation.Row("running", new("r", "claude", "command", "waiting", "blocked")) == 6, "waiting priority");
        Check(CompanionAnimation.Row("completed", null, true) == 4 && CompanionAnimation.Row("error", null) == 5, "completion/failure animation");
        string[] active = ["a", "b", "c"];
        Check(CompanionCarousel.Shown("b", active, "a") == "b" && CompanionCarousel.Shown("gone", active, "a") == "a", "pin clears after completion");
        Check(CompanionCarousel.Step("c", active, 1) == "a" && CompanionCarousel.Step("a", active, -1) == "c" && CompanionCarousel.Step(null, active, -1) == "c", "wrapping pages");
        Check(CompanionCarousel.Step("a", ["a"], 1) is null && CompanionCarousel.Position("b", active) == 2, "page visibility");
        return Task.CompletedTask;
    }
    internal static Task CatalogBoundaries()
    {
        var root = Verification.Temp(); Directory.CreateDirectory(root);
        try
        {
            // Header-only fixture intentionally verifies metadata, not platform decoding.
            var png = new byte[33]; new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(png, 0); BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(8), 13); "IHDR"u8.CopyTo(png.AsSpan(12));
            BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(16), 1536); BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(20), 1872); png[24] = 8; png[25] = 6;
            var source = Path.Combine(root, "source"); Directory.CreateDirectory(source); File.WriteAllBytes(Path.Combine(source, "sheet.png"), png);
            void Manifest(string path, int version = 1) => File.WriteAllText(Path.Combine(source, "pet.json"), JsonSerializer.Serialize(new { displayName = "한글 펫", spritesheetPath = path, spriteVersionNumber = version }));
            Manifest("sheet.png"); var pet = CompanionPet.Load(source, "test"); Check(pet.Width == 1536 && pet.Height == 1872 && pet.Name == "한글 펫", "valid manifest metadata");
            foreach (var path in new[] { "../outside.png", "/tmp/outside.png", "C:\\outside.png", "\\\\server\\image.png", "sheet.png:stream" })
            {
                Manifest(path); try { CompanionPet.Load(source, "test"); throw new InvalidOperationException("escaped pet path accepted: " + path); } catch (IOException) { }
            }
            Manifest("sheet.png", 3); try { CompanionPet.Load(source, "test"); throw new InvalidOperationException("wrong version accepted"); } catch (IOException) { }
            Manifest("sheet.png"); png[25] = 2; File.WriteAllBytes(Path.Combine(source, "sheet.png"), png);
            try { CompanionPet.Load(source, "test"); throw new InvalidOperationException("opaque sheet accepted"); } catch (IOException) { }
            var profile = Path.Combine(root, "profile"); Directory.CreateDirectory(profile);
            var prefs = new CompanionPreferences(ReducedMotion: true, SelectedPet: "local:pet", Left: -100, Top: 200); prefs.Save(profile); Check(CompanionPreferences.Load(profile) == prefs, "preference roundtrip");
            var installed = pet.Install(profile); Check(installed.Id.StartsWith("local:", StringComparison.Ordinal) && installed.Image.SequenceEqual(pet.Image), "import uses validated byte snapshot");
            var catalog = CompanionPet.Catalog(Path.Combine(root, "missing"), profile, Path.Combine(root, "no-codex")); Check(catalog.Count == 1 && catalog[0].Image.Length == 0, "catalog does not retain all atlases");
            if (!OperatingSystem.IsWindows())
            {
                var linked = Path.Combine(root, "linked"); Directory.CreateDirectory(linked); Directory.CreateSymbolicLink(Path.Combine(linked, "pets"), Path.Combine(profile, "pets"));
                try { pet.Install(linked); throw new InvalidOperationException("linked import destination accepted"); } catch (IOException) { }
            }
        }
        finally { Directory.Delete(root, true); }
        return Task.CompletedTask;
    }
}
