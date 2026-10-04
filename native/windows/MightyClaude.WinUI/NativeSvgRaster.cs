using System.Numerics;
using MightyClaude.Core;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Svg;
using Windows.Storage.Streams;

namespace MightyClaude.WinUI;

/// Shared native SVG raster path for RichEdit, the image viewer and mobile.
internal static class NativeSvgRaster
{
    internal sealed record Result(byte[] Png, int Width, int Height, double OriginalWidth, double OriginalHeight);
    private static readonly SemaphoreSlim slots = new(2, 2);
    internal static async Task<Result> Render(byte[] bytes, int maximumEdge, CancellationToken cancellation = default)
    {
        maximumEdge = Math.Clamp(maximumEdge, 1, 4096);
        await slots.WaitAsync(cancellation);
        try
        {
            return await Task.Run(async () =>
            {
                cancellation.ThrowIfCancellationRequested();
                var source = SafeSvgDocument.Parse(bytes);
                var scale = Math.Min(1, maximumEdge / Math.Max(source.Width, source.Height));
                var width = (int)Math.Max(1, Math.Round(source.Width * scale));
                var height = (int)Math.Max(1, Math.Round(source.Height * scale));
                var device = CanvasDevice.GetSharedDevice(forceSoftwareRenderer: true);
                if (!CanvasSvgDocument.IsSupported(device)) throw new NotSupportedException("This Windows device cannot rasterize SVG.");
                using var svg = CanvasSvgDocument.LoadFromXml(device, source.Xml);
                using var target = new CanvasRenderTarget(device, width, height, 96);
                using (var draw = target.CreateDrawingSession())
                {
                    draw.Clear(default(Windows.UI.Color)); // transparent black: every channel 0
                    draw.Transform = Matrix3x2.CreateScale((float)(width / source.Width), (float)(height / source.Height));
                    draw.DrawSvg(svg, new Windows.Foundation.Size(source.Width, source.Height));
                }
                cancellation.ThrowIfCancellationRequested();
                using var stream = new InMemoryRandomAccessStream(); await target.SaveAsync(stream, CanvasBitmapFileFormat.Png);
                if (stream.Size > AgentImageSupport.MaximumImageBytes) throw new AgentImageException(AgentImageError.TooLarge);
                var png = new byte[checked((int)stream.Size)]; using var reader = new DataReader(stream.GetInputStreamAt(0));
                await reader.LoadAsync((uint)png.Length); reader.ReadBytes(png); cancellation.ThrowIfCancellationRequested();
                return new Result(png, width, height, source.Width, source.Height);
            }, cancellation);
        }
        finally { slots.Release(); }
    }
}
