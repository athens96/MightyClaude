using System.Text.Json;
using MightyClaude.Core;
using Microsoft.UI.Text;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace MightyClaude.WinUI;

public sealed partial class MainWindow
{
    /// <summary>
    /// The pictures' decoding with Windows' own codecs, as the conversation's thumbnails use it: a portrait EXIF JPEG keeps its
    /// orientation (40×30 pixels shown 30×40), and a TIFF goes through the WIC decoder. The SVG raster is the web smoke's pictures
    /// step; the conversation itself is the page's.
    /// </summary>
    private static async Task<Dictionary<string, object?>> RunPictureDecodeSmoke()
    {
        var oriented = await AgentPictures.Thumbnail(await SmokeExifJpeg());
        Require(oriented.Width == 30 && oriented.Height == 40, "a portrait EXIF JPEG lost its orientation in the transcript thumbnail");
        var tiff = await AgentPictures.Thumbnail(await SmokeTiff());
        Require(tiff.Width == 40 && tiff.Height == 30, "Windows TIFF raster decoder was not used");
        return new() { ["exifOrientation"] = true, ["tiffRaster"] = true };
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


    private sealed partial class PaneView
    {
    }
}
