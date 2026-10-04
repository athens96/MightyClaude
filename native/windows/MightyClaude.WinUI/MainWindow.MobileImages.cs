using MightyClaude.Core;
using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace MightyClaude.WinUI;

public sealed partial class MainWindow
{
    private readonly SemaphoreSlim mobileImageDecodes = new(2, 2);

    // WIC/PDF decode uses no XAML object and may run on the relay worker. The
    // file bytes have already passed the workspace handle/containment reader.
    private async Task<MobileImagePreview?> MakeMobileImagePreview(FilePreviewData preview, CancellationToken cancellation)
    {
        if (preview.ImageBytes is not { Length: > 0 and <= 50 * 1_048_576 } bytes || preview.Kind.Tag != FilePreviewKindTag.Image) return null;
        await mobileImageDecodes.WaitAsync(cancellation);
        try
        {
            return await Task.Run(async () =>
            {
                cancellation.ThrowIfCancellationRequested();
                var extension = FilePreviewClassifier.FileExtension(preview.Name);
                var imageBytes = extension == "svg" ? (await NativeSvgRaster.Render(bytes, 1600, cancellation)).Png : bytes;
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
                    if (document.PageCount == 0) return null;
                    using var page = document.GetPage(0);
                    if (!FilePreviewClassifier.IsDrawable(page.Size.Width, page.Size.Height)) return null;
                    var scale = Math.Min(2, 1600.0 / Math.Max(page.Size.Width, page.Size.Height));
                    cancellation.ThrowIfCancellationRequested();
                    await page.RenderToStreamAsync(rendered, new PdfPageRenderOptions { DestinationWidth = (uint)Math.Max(1, page.Size.Width * scale), DestinationHeight = (uint)Math.Max(1, page.Size.Height * scale) });
                    rendered.Seek(0); raster = rendered;
                }
                var decoder = await BitmapDecoder.CreateAsync(raster);
                var width = decoder.OrientedPixelWidth; var height = decoder.OrientedPixelHeight;
                if (FilePreviewClassifier.PixelCount(width, height) is not { } pixels || pixels > 64_000_000 || width > 32_768 || height > 32_768) return null;
                var edge = 1600;
                for (var attempt = 0; attempt < 5; attempt++, edge /= 2)
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
                    if ((long)targetWidth * targetHeight * 4 != data.Length) return null;
                    var opaque = true; for (var i = 3; i < data.Length && opaque; i += 4) opaque = data[i] == 255;
                    using var output = new InMemoryRandomAccessStream();
                    var encoder = opaque
                        ? await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, output, new BitmapPropertySet { ["ImageQuality"] = new BitmapTypedValue(.82f, Windows.Foundation.PropertyType.Single) })
                        : await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
                    encoder.SetPixelData(BitmapPixelFormat.Bgra8, opaque ? BitmapAlphaMode.Ignore : BitmapAlphaMode.Straight, targetWidth, targetHeight, 96, 96, data);
                    await encoder.FlushAsync();
                    cancellation.ThrowIfCancellationRequested();
                    if (output.Size > 512 * 1024) continue;
                    var thumbnail = new byte[checked((int)output.Size)];
                    using var reader = new DataReader(output.GetInputStreamAt(0)); await reader.LoadAsync((uint)thumbnail.Length); reader.ReadBytes(thumbnail);
                    return new MobileImagePreview(opaque ? "image/jpeg" : "image/png", Convert.ToBase64String(thumbnail), (int)width, (int)height, (int)targetWidth, (int)targetHeight);
                }
                return null;
            }, cancellation);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
        finally { mobileImageDecodes.Release(); }
    }
}
