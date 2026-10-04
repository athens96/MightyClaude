using MightyClaude.Core;
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
