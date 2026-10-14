using MightyClaude.Core;
using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace MightyClaude.WinUI;

public sealed partial class MainWindow
{
    // WIC/PDF decode uses no XAML object and may run on the relay worker. The
    // file bytes have already passed the workspace handle/containment reader, and
    // the router lets one decode run at a time (MobilePreviewSlot). The thumbnail's
    // long side steps down the Mac's ladder from 2,048 px until it fits 512 KiB
    // (M/MightyCore/Remote/MobileWorkspaceFiles.swift:93-100).
    private async Task<MobileImageResult> MakeMobileImagePreview(string name, byte[] bytes, CancellationToken cancellation)
    {
        if (bytes.Length == 0) return new(null, "undecodable");
        try
        {
            return await Task.Run(async () =>
            {
                cancellation.ThrowIfCancellationRequested();
                var extension = FilePreviewClassifier.FileExtension(name);
                var imageBytes = extension == "svg" ? (await NativeSvgRaster.Render(bytes, MobileWorkspaceFiles.MaximumThumbnailPixels, cancellation)).Png : bytes;
                using var source = new InMemoryRandomAccessStream();
                using (var writer = new DataWriter(source.GetOutputStreamAt(0)))
                {
                    writer.WriteBytes(imageBytes); await writer.StoreAsync(); await writer.FlushAsync(); writer.DetachStream();
                }
                source.Seek(0);
                using var rendered = new InMemoryRandomAccessStream();
                IRandomAccessStream raster = source;
                if (extension == "pdf")
                {
                    var document = await PdfDocument.LoadFromStreamAsync(source);
                    if (document.PageCount == 0) return new MobileImageResult(null, "undecodable");
                    using var page = document.GetPage(0);
                    if (!FilePreviewClassifier.IsDrawable(page.Size.Width, page.Size.Height)) return new(null, "undecodable");
                    var scale = Math.Min(2, (double)MobileWorkspaceFiles.MaximumThumbnailPixels / Math.Max(page.Size.Width, page.Size.Height));
                    cancellation.ThrowIfCancellationRequested();
                    await page.RenderToStreamAsync(rendered, new PdfPageRenderOptions { DestinationWidth = (uint)Math.Max(1, page.Size.Width * scale), DestinationHeight = (uint)Math.Max(1, page.Size.Height * scale) });
                    rendered.Seek(0); raster = rendered;
                }
                var decoder = await BitmapDecoder.CreateAsync(raster);
                var width = decoder.OrientedPixelWidth; var height = decoder.OrientedPixelHeight;
                if (FilePreviewClassifier.PixelCount(width, height) is not { } pixels) return new(null, "undecodable");
                if (pixels > 64_000_000 || width > 32_768 || height > 32_768) return new(null, "tooLarge");
                foreach (var edge in MobileWorkspaceFiles.ThumbnailLadder)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var scale = Math.Min(1.0, (double)edge / Math.Max(width, height));
                    // BitmapTransform uses un-oriented dimensions; the decoder then
                    // applies EXIF orientation, so portrait images retain their ratio.
                    var rawWidth = (uint)Math.Max(1, Math.Round(decoder.PixelWidth * scale));
                    var rawHeight = (uint)Math.Max(1, Math.Round(decoder.PixelHeight * scale));
                    var targetWidth = (uint)Math.Max(1, Math.Round(width * scale));
                    var targetHeight = (uint)Math.Max(1, Math.Round(height * scale));
                    var transform = new BitmapTransform { ScaledWidth = rawWidth, ScaledHeight = rawHeight, InterpolationMode = BitmapInterpolationMode.Fant };
                    var frame = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, transform, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
                    var data = frame.DetachPixelData();
                    if ((long)targetWidth * targetHeight * 4 != data.Length) return new(null, "undecodable");
                    var opaque = true; for (var i = 3; i < data.Length && opaque; i += 4) opaque = data[i] == 255;
                    using var output = new InMemoryRandomAccessStream();
                    var encoder = opaque
                        ? await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, output, new BitmapPropertySet { ["ImageQuality"] = new BitmapTypedValue(.82f, Windows.Foundation.PropertyType.Single) })
                        : await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
                    encoder.SetPixelData(BitmapPixelFormat.Bgra8, opaque ? BitmapAlphaMode.Ignore : BitmapAlphaMode.Straight, targetWidth, targetHeight, 96, 96, data);
                    await encoder.FlushAsync();
                    cancellation.ThrowIfCancellationRequested();
                    if (output.Size > MobileWorkspaceFiles.MaximumThumbnailBytes) continue;
                    var thumbnail = new byte[checked((int)output.Size)];
                    using var reader = new DataReader(output.GetInputStreamAt(0)); await reader.LoadAsync((uint)thumbnail.Length); reader.ReadBytes(thumbnail);
                    return new(new MobileImagePreview(opaque ? "image/jpeg" : "image/png", Convert.ToBase64String(thumbnail), (int)width, (int)height, (int)targetWidth, (int)targetHeight));
                }
                return new(null, "tooLarge");
            }, cancellation);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return new(null, "undecodable"); }
    }
}
