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
