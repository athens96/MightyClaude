using System.Text.Json;
using MightyClaude.Core;
using Microsoft.UI.Text;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace MightyClaude.WinUI;

public sealed partial class MainWindow
{
    /// <summary>
    /// Inline agent pictures in a real transcript: a picture the agent returned (from the
    /// cache, by reference) and a Markdown picture inside the workspace are drawn into the
    /// RichEdit document as pictures after a loading placeholder; an http picture stays a
    /// link and a picture outside the allowed folders is named, not drawn; the saved state
    /// holds the reference, never the bytes.
    /// </summary>
    private async Task<Dictionary<string, object?>> RunAgentImagesSmoke(Workspace workspace)
    {
        var checks = new Dictionary<string, object?>();
        var id = service.Snapshot.Sessions[0].Id;
        var original = service.Snapshot.Sessions[0].Logs;
        var png = await SmokePng(40, 30);
        var oriented = await AgentPictures.Thumbnail(await SmokeExifJpeg());
        Require(oriented.Width == 30 && oriented.Height == 40, "a portrait EXIF JPEG lost its orientation in the transcript thumbnail");
        checks["exifOrientation"] = true;
        var svg = await NativeSvgRaster.Render("<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 80 40'><rect width='80' height='40' fill='red'/></svg>"u8.ToArray(), 20);
        Require(svg.Width == 20 && svg.Height == 10 && AgentImageSupport.Inspect(svg.Png, "image/png") == ("image/png", 20, 10), "bounded native SVG raster has incorrect dimensions");
        checks["svgRaster"] = true;
        var tiff = await AgentPictures.Thumbnail(await SmokeTiff());
        Require(tiff.Width == 40 && tiff.Height == 30, "Windows TIFF raster decoder was not used");
        checks["tiffRaster"] = true;
        var stored = service.Images.Store(png, "image/png", "Read · smoke.png");
        Directory.CreateDirectory(Path.Combine(workspace.Path, "shots"));
        await File.WriteAllBytesAsync(Path.Combine(workspace.Path, "shots", "smoke.png"), await SmokePng(24, 18));
        const string markdown = "![shot](shots/smoke.png)\n\n![web](https://example.com/x.png)\n\n![secret](../outside.png)";
        var image = new LogEntry(Wire.Id(), "image", AgentImageSupport.EntryText([stored], stored.Source), Wire.Now(), "claude", null, [stored]);
        try
        {
            await service.UpdateAsync(s => s with { Sessions = s.Sessions.Select(p => p.Id == id ? p with { Logs = [.. p.Logs, image, new(Wire.Id(), "assistant", markdown, Wire.Now(), "claude")] } : p).ToList() });
            var pane = views[id];
            pane.Refresh();
            Require(pane.Transcript.Text.Contains(Locale.Get("images.loading"), StringComparison.Ordinal), "a picture shows the loading placeholder before its thumbnail");
            checks["loadingPlaceholderFirst"] = true;
            // Markdown picture paths are located off the UI thread (AgentPictures.Locate), so the
            // refused note arrives with that answer, not on the first render.
            await WaitUI(() => pane.Transcript.Text.Count(c => c == '￼') == 2 && pane.Transcript.Text.Contains(Locale.Get("images.refused"), StringComparison.Ordinal));
            pane.Transcript.View.Document.GetText(TextGetOptions.FormatRtf, out var rtf);
            Require(rtf.Contains(@"\pict", StringComparison.Ordinal), "the document holds no picture");
            checks["inlinePicture"] = true;
            checks["markdownPicture"] = true;
            var text = pane.Transcript.Text;
            Require(!text.Contains(Locale.Get("images.loading"), StringComparison.Ordinal), "a placeholder is left after the thumbnails loaded");
            Require(text.Contains(image.Text, StringComparison.Ordinal), "the picture's source is not shown under it");
            checks["sourceCaption"] = true;
            Require(text.Contains(Locale.Get("images.remote"), StringComparison.Ordinal) && text.Contains("https://example.com/x.png", StringComparison.Ordinal), "an http picture is not a plain link");
            checks["remoteLinkOnly"] = true;
            Require(text.Contains(Locale.Get("images.refused"), StringComparison.Ordinal), "a picture outside the workspace is not named as refused");
            checks["outsideRefused"] = true;
            var saved = JsonSerializer.Serialize(service.Snapshot, Wire.Json);
            Require(saved.Contains(stored.Hash, StringComparison.Ordinal) && !saved.Contains(Convert.ToBase64String(png)[..32], StringComparison.Ordinal), "the saved state carries picture bytes");
            checks["referencesOnlySaved"] = true;
        }
        finally
        {
            await service.UpdateAsync(s => s with { Sessions = s.Sessions.Select(p => p.Id == id ? p with { Logs = original } : p).ToList() });
            views[id].Refresh();
        }
        return checks;
    }

    private static async Task<byte[]> SmokeTiff()
    {
        var pixels = new byte[40 * 30 * 4]; for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.TiffEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, 40, 30, 96, 96, pixels); await encoder.FlushAsync();
        var bytes = new byte[stream.Size]; using var reader = new DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync((uint)stream.Size); reader.ReadBytes(bytes);
        Require(AgentImageSupport.Inspect(bytes, "image/tiff") == ("image/tiff", 40, 30), "TIFF header inspection rejected WIC's actual output");
        return bytes;
    }

    private static async Task<byte[]> SmokeExifJpeg()
    {
        var pixels = new byte[40 * 30 * 4];
        for (var i = 0; i < pixels.Length; i += 4) { pixels[i] = 30; pixels[i + 1] = 90; pixels[i + 2] = 180; pixels[i + 3] = 255; }
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, 40, 30, 96, 96, pixels);
        await encoder.FlushAsync();
        var jpeg = new byte[stream.Size]; using var reader = new DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync((uint)stream.Size); reader.ReadBytes(jpeg);
        // EXIF APP1: little-endian TIFF, one SHORT Orientation=6 (90° clockwise).
        // The pixel dimensions stay 40×30; the display dimensions must be 30×40.
        byte[] orientation = [0xff, 0xe1, 0, 34, 69, 120, 105, 102, 0, 0, 73, 73, 42, 0, 8, 0, 0, 0,
            1, 0, 0x12, 0x01, 3, 0, 1, 0, 0, 0, 6, 0, 0, 0, 0, 0, 0, 0];
        Require(jpeg.Length > 2 && jpeg[0] == 0xff && jpeg[1] == 0xd8, "WIC did not encode a JPEG fixture");
        return [.. jpeg.AsSpan(0, 2).ToArray(), .. orientation, .. jpeg.AsSpan(2).ToArray()];
    }

    private static async Task<byte[]> SmokePng(uint width, uint height)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4) { pixels[i] = 40; pixels[i + 1] = 120; pixels[i + 2] = 200; pixels[i + 3] = 255; }
        using var output = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, width, height, 96, 96, pixels);
        await encoder.FlushAsync();
        var bytes = new byte[output.Size];
        using var reader = new DataReader(output.GetInputStreamAt(0));
        await reader.LoadAsync((uint)output.Size);
        reader.ReadBytes(bytes);
        return bytes;
    }

    private sealed partial class PaneView
    {
        internal AgentTranscript Transcript => output;
    }
}
