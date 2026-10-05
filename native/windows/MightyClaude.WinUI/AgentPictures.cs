using MightyClaude.Core;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.UI.Dispatching;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace MightyClaude.WinUI;

/// <summary>
/// Thumbnails for the pictures agents show (macOS AgentImageLibrary): decoded through WIC
/// off the UI thread, at most two at a time, at most 960 pixels on the long side, and kept
/// in memory as the transcript's ready RTF picture groups. Transcripts hold only the
/// picture's key and ask again on every render; finished loads are announced together
/// after 50 ms so each transcript redraws once.
/// </summary>
internal sealed class AgentPictures(AgentImageCache cache, DispatcherQueue dispatcher)
{
    internal enum State { Loading, Ready, Missing, Unsupported }
    internal readonly record struct Picture(State State, string? Rtf = null);

    /// <summary>Thumbnail groups held in memory, least recently drawn dropped first.</summary>
    internal const long MemoryBudget = 96L * 1_048_576;
    private readonly Dictionary<string, (string? Rtf, LinkedListNode<string> Node)> done = [];
    private readonly LinkedList<string> order = new();
    private readonly HashSet<string> loading = [];
    private readonly SemaphoreSlim decodes = new(2, 2);
    private readonly List<WeakReference<AgentTranscript>> listeners = [];
    private long held;
    private bool announcing;

    /// <summary>
    /// Where each Markdown picture is, with its file's size and write time, found off the UI
    /// thread and kept so drawing a transcript never touches the disk. A known entry is looked
    /// at again in the background at most every RecheckInterval; a change redraws.
    /// </summary>
    internal static readonly TimeSpan RecheckInterval = TimeSpan.FromSeconds(2);
    private const int MaximumLocated = 4_096;
    private sealed record Located(AgentImageLocation Location, string? Stamp, DateTime Checked);
    private readonly Dictionary<(string Source, string? Root), Located> located = [];
    private readonly HashSet<(string Source, string? Root)> locating = [];

    internal AgentImageCache Cache => cache;

    internal void Listen(AgentTranscript transcript)
    {
        listeners.RemoveAll(l => !l.TryGetTarget(out _));
        if (!listeners.Any(l => l.TryGetTarget(out var t) && ReferenceEquals(t, transcript))) listeners.Add(new(transcript));
    }

    /// <summary>A picture the agent returned, by its cache reference.</summary>
    internal Picture For(AgentImageRef value)
    {
        return Lookup("ref:" + value.Hash, () => cache.Data(value) ?? throw new FileNotFoundException());
    }

    /// <summary>
    /// A Markdown picture: where it is and what to draw, or null while it is first being
    /// found. data: and http(s) sources are decided on paper at once; a path is located off
    /// the UI thread (AgentImagePaths.Locate asks the disk) and the transcript redraws then.
    /// </summary>
    internal (AgentImageLocation Location, Picture Picture)? Locate(string source, string? root)
    {
        var value = source.Trim();
        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            || Uri.TryCreate(value, UriKind.Absolute, out var url) && url.Scheme is "http" or "https")
        {
            var onPaper = AgentImagePaths.Locate(source, root);
            return (onPaper, For(new Located(onPaper, null, DateTime.UtcNow)));
        }
        var key = (source, root);
        if (!located.TryGetValue(key, out var found)) { Relocate(key); return null; }
        if (DateTime.UtcNow - found.Checked > RecheckInterval) Relocate(key);
        return (found.Location, For(found));
    }

    private void Relocate((string Source, string? Root) key)
    {
        if (!locating.Add(key)) return;
        _ = Task.Run(() =>
        {
            Located result;
            try
            {
                var location = AgentImagePaths.Locate(key.Source, key.Root);
                result = new(location, location is AgentImageLocation.File file ? Stamp(file.Path) : null, DateTime.UtcNow);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { result = new(new AgentImageLocation.Refused(), null, DateTime.UtcNow); }
            dispatcher.TryEnqueue(() =>
            {
                locating.Remove(key);
                var changed = !located.TryGetValue(key, out var old) || old.Location != result.Location || old.Stamp != result.Stamp;
                if (!located.ContainsKey(key) && located.Count >= MaximumLocated) located.Clear();
                located[key] = result;
                if (changed) Announce();
            });
        });
    }

    /// <summary>The file's size and write time (a changed file under the same name is read again), or null when it is gone.</summary>
    private static string? Stamp(string path)
    {
        try { var info = new FileInfo(path); return info.Exists ? info.Length + ":" + info.LastWriteTimeUtc.Ticks : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    private Picture For(Located found)
    {
        switch (found.Location)
        {
            case AgentImageLocation.File file:
                if (found.Stamp is not { } stamp) return new(State.Missing);
                return Lookup("file:" + file.Path + ":" + stamp, () => AgentImagePaths.Read(file.Path, file.Root));
            case AgentImageLocation.Inline inline:
                return Lookup("inline:" + AgentImageSupport.Sha256(System.Text.Encoding.ASCII.GetBytes(inline.Base64)), () => AgentImageSupport.DecodeBase64(inline.Base64));
            default: return new(State.Missing);
        }
    }

    private Picture Lookup(string key, Func<byte[]> read)
    {
        if (done.TryGetValue(key, out var found))
        {
            order.Remove(found.Node); order.AddLast(found.Node);
            return found.Rtf is null ? new(State.Missing) : new(State.Ready, found.Rtf);
        }
        if (loading.Add(key)) _ = Task.Run(() => LoadAsync(key, read));
        return new(State.Loading);
    }

    private async Task LoadAsync(string key, Func<byte[]> read)
    {
        string? rtf = null;
        await decodes.WaitAsync();
        try
        {
            var data = read();
            var isSvg = AgentImageSupport.SvgDocument(data);
            _ = AgentImageSupport.Inspect(data, isSvg ? "image/svg+xml" : "image/png");
            if (isSvg) data = (await NativeSvgRaster.Render(data, AgentImageSupport.ThumbnailPixels)).Png;
            var (encoded, width, height) = await Thumbnail(data);
            rtf = AgentImageRtf.Picture(encoded, width, height, AgentImageRtf.DisplaySize(width, height));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { rtf = null; }
        finally { decodes.Release(); }
        dispatcher.TryEnqueue(() => { loading.Remove(key); Remember(key, rtf); Announce(); });
    }

    private void Remember(string key, string? rtf)
    {
        if (done.Remove(key, out var old)) { order.Remove(old.Node); held -= (old.Rtf?.Length ?? 0) * 2L; }
        done[key] = (rtf, order.AddLast(key)); held += (rtf?.Length ?? 0) * 2L;
        while (held > MemoryBudget && order.First is { } oldest && oldest.Value != key)
        {
            order.RemoveFirst();
            if (done.Remove(oldest.Value, out var dropped)) held -= (dropped.Rtf?.Length ?? 0) * 2L;
        }
    }

    private void Announce()
    {
        if (announcing) return;
        announcing = true;
        _ = Task.Delay(50).ContinueWith(delay => dispatcher.TryEnqueue(() =>
        {
            announcing = false;
            listeners.RemoveAll(l => !l.TryGetTarget(out _));
            foreach (var listener in listeners.ToList()) if (listener.TryGetTarget(out var transcript)) transcript.Redraw();
        }), TaskScheduler.Default);
    }

    /// <summary>
    /// A thumbnail at most 960 pixels on its long side, decoded and encoded through WIC: a
    /// JPEG when every pixel is opaque (a screenshot is a fraction of its PNG size, and the
    /// transcript carries it as hex on every redraw), a PNG when transparency must survive.
    /// </summary>
    internal static async Task<(byte[] Encoded, int Width, int Height)> Thumbnail(byte[] data)
    {
        using var input = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(input.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(data);
            await writer.StoreAsync(); await writer.FlushAsync();
            writer.DetachStream();
        }
        input.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(input);
        uint width = decoder.OrientedPixelWidth, height = decoder.OrientedPixelHeight;
        if (FilePreviewClassifier.PixelCount(width, height) is not { } pixels || pixels > AgentImageSupport.MaximumPixels || Math.Max(width, height) > AgentImageSupport.MaximumSide)
            throw new AgentImageException(AgentImageError.TooManyPixels);
        var scale = Math.Min(1.0, (double)AgentImageSupport.ThumbnailPixels / Math.Max(width, height));
        var thumbnailWidth = (uint)Math.Max(1, Math.Round(width * scale)); var thumbnailHeight = (uint)Math.Max(1, Math.Round(height * scale));
        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)Math.Max(1, Math.Round(decoder.PixelWidth * scale)),
            ScaledHeight = (uint)Math.Max(1, Math.Round(decoder.PixelHeight * scale)),
            InterpolationMode = BitmapInterpolationMode.Fant
        };
        var frame = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, transform, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
        var pixelData = frame.DetachPixelData();
        if ((long)thumbnailWidth * thumbnailHeight * 4 != pixelData.Length) throw new InvalidDataException("Unexpected oriented bitmap dimensions.");
        var opaque = true;
        for (var i = 3; i < pixelData.Length && opaque; i += 4) opaque = pixelData[i] == 255;
        using var output = new InMemoryRandomAccessStream();
        var encoder = opaque
            ? await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, output, new BitmapPropertySet { ["ImageQuality"] = new BitmapTypedValue(0.88, Windows.Foundation.PropertyType.Single) })
            : await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, opaque ? BitmapAlphaMode.Ignore : BitmapAlphaMode.Straight, thumbnailWidth, thumbnailHeight, 96, 96, pixelData);
        await encoder.FlushAsync();
        var bytes = new byte[output.Size];
        using (var reader = new DataReader(output.GetInputStreamAt(0)))
        {
            await reader.LoadAsync((uint)output.Size);
            reader.ReadBytes(bytes);
        }
        return (bytes, (int)thumbnailWidth, (int)thumbnailHeight);
    }
}

/// <summary>
/// The small marks the Mac draws into a transcript line as attachments (M/AgentTranscriptFormat.swift:92-150):
/// a tool call's state as a 14pt filled square with its glyph, and the speaker's own provider mark at 13pt.
/// An RTF line has no shapes, so each is a tiny PNG drawn once per look and device scale (Win2D, off screen)
/// and kept as its ready <c>\pict</c> group. Null when the device cannot draw: the line then shows a glyph.
/// </summary>
internal static class TranscriptMarks
{
    /// <summary>The state square's side and corner radius (M/AgentTranscriptFormat.swift:134-137).</summary>
    internal const double StatusSize = 14, StatusRadius = 4;
    /// <summary>The provider mark's side (M/AgentTranscriptFormat.swift:97).</summary>
    internal const double ProviderSize = 13;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> drawn = new();
    /// <summary>
    /// When a mark last failed to draw (<see cref="Environment.TickCount64"/>). A failure is not kept as the
    /// mark: the next line that needs it tries again. But a device that is gone fails for every mark of every
    /// line, so nothing is tried for <see cref="RetryAfter"/> milliseconds after one.
    /// </summary>
    private static long failedAt = -RetryAfter;
    private const long RetryAfter = 2_000;

    /// <summary>A mark by its key: the one already drawn, or a new drawing, kept only when it worked.</summary>
    private static string? Mark(string key, Func<string?> draw)
    {
        if (drawn.TryGetValue(key, out var mark)) return mark;
        if (Environment.TickCount64 - Interlocked.Read(ref failedAt) < RetryAfter) return null;
        if (draw() is { } made) return drawn.GetOrAdd(key, made);
        Interlocked.Exchange(ref failedAt, Environment.TickCount64);
        return null;
    }

    /// <summary>
    /// ✓ done, ✕ failed, ● still going (amber while it waits), – stopped: the fill of the state with only
    /// its own glyph ink on it (<c>onStatus</c>; <c>onWait</c> on the amber).
    /// </summary>
    internal static string? Status(string state, bool live, DesignPalette palette, double scale, double lift)
    {
        var (fill, ink, glyph) = state switch
        {
            "completed" => (palette.Done, palette.OnStatus, 'c'),
            "error" => (palette.Err, palette.OnStatus, 'x'),
            "waiting" when live => (palette.Wait, palette.OnWait, 'o'),
            "running" when live => (palette.Run, palette.OnStatus, 'o'),
            _ => (palette.Stop, palette.OnStatus, 'm'),
        };
        return Mark($"status:{glyph}:{fill}:{ink}:{Pixels(StatusSize, scale)}:{Pixels(lift, scale)}", () => Draw(StatusSize, scale, lift, session =>
        {
            var paint = DesignBrushes.ToColor(ink);
            session.FillRoundedRectangle(0, 0, (float)StatusSize, (float)StatusSize, (float)StatusRadius, (float)StatusRadius, DesignBrushes.ToColor(fill));
            // The Mac's symbols are 8pt heavy (the dot 5pt), centred in the square.
            using var stroke = new CanvasStrokeStyle { StartCap = CanvasCapStyle.Round, EndCap = CanvasCapStyle.Round, LineJoin = CanvasLineJoin.Round };
            switch (glyph)
            {
                case 'c':
                    using (var builder = new CanvasPathBuilder(session))
                    {
                        builder.BeginFigure(3.8f, 7.3f); builder.AddLine(6.05f, 9.5f); builder.AddLine(10.3f, 4.6f); builder.EndFigure(CanvasFigureLoop.Open);
                        using var check = CanvasGeometry.CreatePath(builder);
                        session.DrawGeometry(check, paint, 1.8f, stroke);
                    }
                    break;
                case 'x': session.DrawLine(4.5f, 4.5f, 9.5f, 9.5f, paint, 1.8f, stroke); session.DrawLine(9.5f, 4.5f, 4.5f, 9.5f, paint, 1.8f, stroke); break;
                case 'o': session.FillCircle(7, 7, 2.6f, paint); break;
                default: session.DrawLine(4.2f, 7, 9.8f, 7, paint, 1.8f, stroke); break;
            }
        }));
    }

    /// <summary>The provider's own mark in its brand colours (Core <see cref="ProviderMark"/>), or null for a provider without one.</summary>
    internal static string? Provider(string? provider, double scale, double lift)
    {
        if (ProviderMark.MarkedProvider(provider) is not { } marked) return null;
        return Mark($"provider:{marked}:{Pixels(ProviderSize, scale)}:{Pixels(lift, scale)}", () => Draw(ProviderSize, scale, lift, session =>
        {
            using var builder = new CanvasPathBuilder(session);
            builder.SetFilledRegionDetermination(CanvasFilledRegionDetermination.Winding);
            foreach (var figure in ProviderMark.Figures(marked))
            {
                builder.BeginFigure((float)figure.X, (float)figure.Y);
                foreach (var segment in figure.Segments)
                {
                    if (segment is GlyphCurve curve) builder.AddCubicBezier(new((float)curve.X1, (float)curve.Y1), new((float)curve.X2, (float)curve.Y2), new((float)curve.X, (float)curve.Y));
                    else builder.AddLine((float)segment.X, (float)segment.Y);
                }
                builder.EndFigure(figure.Closed ? CanvasFigureLoop.Closed : CanvasFigureLoop.Open);
            }
            using var outline = CanvasGeometry.CreatePath(builder);
            var colors = ProviderMark.Colors(marked);
            // The outline sits in its 24-unit box; Gemini's sweep runs from the bottom-left to the top-right corner.
            session.Transform = System.Numerics.Matrix3x2.CreateScale((float)(ProviderSize / ProviderMark.Box)) * session.Transform;
            if (colors.Count == 1) { session.FillGeometry(outline, DesignBrushes.ToColor(new DesignColor(colors[0]))); return; }
            var stops = colors.Select((color, index) => new CanvasGradientStop { Color = DesignBrushes.ToColor(new DesignColor(color)), Position = (float)index / (colors.Count - 1) }).ToArray();
            using var sweep = new CanvasLinearGradientBrush(session, stops) { StartPoint = new(0, (float)ProviderMark.Box), EndPoint = new((float)ProviderMark.Box, 0) };
            session.FillGeometry(outline, sweep);
        }));
    }

    private static int Pixels(double size, double scale) => Math.Max(0, (int)Math.Round(size * Math.Clamp(scale, 1, 4)));

    /// <summary>
    /// A square of <paramref name="size"/> epx drawn at the device's scale, as the <c>\pict</c> group shown at
    /// that size. RichEdit hangs a picture from the top of its line (the paragraph's space before included)
    /// and takes no baseline offset for one, so the square is drawn <paramref name="lift"/> epx down a taller,
    /// clear picture: that puts it where the Mac's attachment bounds do.
    /// </summary>
    private static string? Draw(double size, double scale, double lift, Action<CanvasDrawingSession> paint)
    {
        try
        {
            int pixels = Math.Max(1, Pixels(size, scale)), above = Pixels(Math.Max(0, lift), scale);
            var device = CanvasDevice.GetSharedDevice(forceSoftwareRenderer: true);
            using var target = new CanvasRenderTarget(device, pixels, above + pixels, 96);
            using (var session = target.CreateDrawingSession())
            {
                session.Clear(default(Windows.UI.Color)); // transparent: every channel 0
                session.Antialiasing = CanvasAntialiasing.Antialiased;
                session.Transform = System.Numerics.Matrix3x2.CreateScale((float)(pixels / size)) * System.Numerics.Matrix3x2.CreateTranslation(0, above);
                paint(session);
            }
            // The goal is in twips (15 an epx), so the picture is shown on the device pixels it was drawn with.
            var twips = 15 / Math.Clamp(scale, 1, 4);
            return new System.Text.StringBuilder(@"{\pict\pngblip\picw").Append(pixels).Append(@"\pich").Append(above + pixels)
                .Append(@"\picwgoal").Append((int)Math.Round(pixels * twips)).Append(@"\pichgoal").Append((int)Math.Round((above + pixels) * twips)).Append(' ')
                .Append(Convert.ToHexString(Png(target.GetPixelBytes(), pixels, above + pixels))).Append('}').ToString();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
    }

    /// <summary>A PNG of premultiplied BGRA pixels: straight RGBA, no filter, one zlib stream.</summary>
    private static byte[] Png(byte[] bgra, int width, int height)
    {
        var raw = new byte[(width * 4 + 1) * height];
        for (var y = 0; y < height; y++)
        {
            var row = y * (width * 4 + 1) + 1;
            for (var x = 0; x < width; x++)
            {
                var from = (y * width + x) * 4; var to = row + x * 4; var alpha = bgra[from + 3];
                byte Straight(byte value) => alpha == 0 ? (byte)0 : (byte)Math.Min(255, value * 255 / alpha);
                raw[to] = Straight(bgra[from + 2]); raw[to + 1] = Straight(bgra[from + 1]); raw[to + 2] = Straight(bgra[from]); raw[to + 3] = alpha;
            }
        }
        using var packed = new MemoryStream();
        using (var zlib = new System.IO.Compression.ZLibStream(packed, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true)) zlib.Write(raw);
        using var png = new MemoryStream();
        png.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        var header = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header, width); System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; header[9] = 6; // 8 bits a channel, RGBA
        Chunk(png, "IHDR"u8, header); Chunk(png, "IDAT"u8, packed.ToArray()); Chunk(png, "IEND"u8, []);
        return png.ToArray();
    }

    private static void Chunk(Stream png, ReadOnlySpan<byte> type, byte[] data)
    {
        Span<byte> word = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(word, data.Length); png.Write(word);
        png.Write(type); png.Write(data);
        var crc = uint.MaxValue;
        void Add(ReadOnlySpan<byte> bytes) { foreach (var value in bytes) { crc ^= value; for (var bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? (crc >> 1) ^ PngPolynomial : crc >> 1; } }
        Add(type); Add(data);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(word, ~crc); png.Write(word);
    }

    /// <summary>The reversed CRC-32 polynomial every PNG chunk is checked with.</summary>
    private const uint PngPolynomial = 0xEDB88320u;
}
