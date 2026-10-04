using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using MightyClaude.Core;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Storage.Streams;

namespace MightyClaude.WinUI;

/// Windows Graphics Capture feeds the encrypted screen-sharing transport. This
/// class never grants access: its owner must approve the device and visible local
/// session first. The OS capture border remains enabled. No frames touch disk.
internal sealed class WindowsScreenCapture : IAsyncDisposable
{
    private readonly object sync = new();
    private readonly SemaphoreSlim lifecycle = new(1, 1), processing = new(1, 1);
    private CanvasDevice? device;
    private GraphicsCaptureItem? item;
    private Direct3D11CaptureFramePool? pool;
    private GraphicsCaptureSession? session;
    private CancellationTokenRegistration cancellation;
    private Func<ScreenFrame, Task>? deliver;
    private ScreenQuality quality = ScreenQuality.For(false);
    private ScreenRegion region = new();
    private SizeInt32 poolSize;
    private bool active, disposed;
    private long generation, lastFrame, lastOverview;
    private int lostNotified;
    public event Action? DisplayLost;
    public static bool Supported { get { try { return GraphicsCaptureSession.IsSupported(); } catch { return false; } } }
    public IReadOnlyList<ScreenDisplay> Displays => EnumerateMonitors().Select(m => m.Display).ToArray();

    public async Task StartAsync(int displayId, ScreenQuality requestedQuality, ScreenRegion requestedRegion, Func<ScreenFrame, Task> frame, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(frame); Validate(requestedQuality, requestedRegion);
        long ticket; lock (sync) { ObjectDisposedException.ThrowIf(disposed, this); active = false; ticket = ++generation; }
        await lifecycle.WaitAsync(token);
        try
        {
            await DrainAndDispose(); token.ThrowIfCancellationRequested();
            lock (sync) if (ticket != generation || disposed) throw new OperationCanceledException("Screen capture request was withdrawn.");
            if (!Supported) throw new NotSupportedException("Windows Graphics Capture is unavailable on this device.");
            var monitor = EnumerateMonitors().FirstOrDefault(m => m.Display.DisplayId == displayId) ?? throw new ArgumentException("The selected display is unavailable.", nameof(displayId));
            var nextDevice = new CanvasDevice(); GraphicsCaptureItem? nextItem = null; Direct3D11CaptureFramePool? nextPool = null; GraphicsCaptureSession? nextSession = null;
            var transferred = false;
            try
            {
                nextItem = CaptureItem(monitor.Handle); CheckSize(nextItem.Size);
                nextPool = Direct3D11CaptureFramePool.CreateFreeThreaded(nextDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, nextItem.Size);
                nextSession = nextPool.CreateCaptureSession(nextItem);
                lock (sync)
                {
                    token.ThrowIfCancellationRequested();
                    if (ticket != generation || disposed) throw new OperationCanceledException("Screen capture request was withdrawn.");
                    device = nextDevice; item = nextItem; pool = nextPool; session = nextSession; poolSize = item.Size;
                    transferred = true;
                    quality = requestedQuality; region = requestedRegion; deliver = frame; lastFrame = 0; lastOverview = 0; lostNotified = 0; active = true;
                    pool.FrameArrived += OnFrameArrived; item.Closed += OnClosed;
                    // Keep the default visible capture border. Removing it would
                    // require separate OS consent and is deliberately not requested.
                    session.StartCapture();
                }
                cancellation = token.Register(() => { Halt(); _ = Task.Run(StopAsync); });
                nextDevice = null!; nextItem = null; nextPool = null; nextSession = null;
            }
            catch
            {
                Halt(); await DrainAndDispose(); throw;
            }
            finally { if (!transferred) { nextSession?.Dispose(); nextPool?.Dispose(); nextDevice?.Dispose(); } }
        }
        finally { lifecycle.Release(); }
    }
    public void Configure(ScreenQuality requestedQuality, ScreenRegion requestedRegion)
    {
        Validate(requestedQuality, requestedRegion);
        lock (sync) { quality = requestedQuality; region = requestedRegion; generation++; lastFrame = 0; lastOverview = 0; }
    }
    /// Revocation closes the generation gate synchronously, before any async
    /// teardown. An in-flight encoder cannot begin a callback after this returns.
    public void Halt() { lock (sync) { active = false; generation++; deliver = null; } }
    public async Task StopAsync()
    {
        Halt(); await lifecycle.WaitAsync();
        try { await DrainAndDispose(); } finally { lifecycle.Release(); }
    }
    private async Task DrainAndDispose()
    {
        cancellation.Dispose(); cancellation = default;
        await processing.WaitAsync();
        try
        {
            lock (sync)
            {
                if (pool is not null) pool.FrameArrived -= OnFrameArrived;
                if (item is not null) item.Closed -= OnClosed;
                session?.Dispose(); session = null; pool?.Dispose(); pool = null; device?.Dispose(); device = null; item = null; deliver = null;
            }
        }
        finally { processing.Release(); }
    }
    private void OnClosed(GraphicsCaptureItem sender, object args) => Lost();
    private void Lost()
    {
        Halt();
        if (Interlocked.Exchange(ref lostNotified, 1) == 0) { try { DisplayLost?.Invoke(); } catch { } }
    }
    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        // A single encoder/callback owns a frame. If it is busy, queued frames
        // remain bounded by the two-buffer pool and the next event uses the latest.
        if (!processing.Wait(0)) return;
        _ = ProcessFrame(sender);
    }
    private async Task ProcessFrame(Direct3D11CaptureFramePool sender)
    {
        Direct3D11CaptureFrame? frame = null;
        try
        {
            ScreenQuality snapshotQuality; ScreenRegion snapshotRegion; CanvasDevice canvas; long ticket; bool overview;
            lock (sync)
            {
                if (!active || pool != sender || device is null) return;
                snapshotQuality = quality; snapshotRegion = region; canvas = device; ticket = generation;
                var now = Stopwatch.GetTimestamp();
                if (lastFrame != 0 && Stopwatch.GetElapsedTime(lastFrame, now).TotalSeconds < 1.0 / snapshotQuality.Fps) { using var skipped = sender.TryGetNextFrame(); return; }
                lastFrame = now; overview = snapshotRegion != new ScreenRegion() && (lastOverview == 0 || Stopwatch.GetElapsedTime(lastOverview, now).TotalSeconds >= .5);
                if (overview) lastOverview = now;
            }
            frame = sender.TryGetNextFrame(); if (frame is null) return;
            CheckSize(frame.ContentSize);
            if (frame.ContentSize.Width != poolSize.Width || frame.ContentSize.Height != poolSize.Height)
            {
                var resized = frame.ContentSize; var completedFrame = frame; frame = null; completedFrame.Dispose();
                lock (sync) if (active && ticket == generation && pool == sender) { sender.Recreate(canvas, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, resized); poolSize = resized; }
                return;
            }
            using var bitmap = CanvasBitmap.CreateFromDirect3D11Surface(canvas, frame.Surface);
            var source = Crop(frame.ContentSize, snapshotRegion);
            var dimensions = Fit(source.Width, source.Height, snapshotQuality.Width, snapshotQuality.Height);
            var main = await Encode(canvas, bitmap, source, dimensions.Width, dimensions.Height, .76f);
            string? overviewData = null;
            if (overview)
            {
                var full = Fit(frame.ContentSize.Width, frame.ContentSize.Height, 640, 360);
                overviewData = Convert.ToBase64String(await Encode(canvas, bitmap, new(0, 0, frame.ContentSize.Width, frame.ContentSize.Height), full.Width, full.Height, .56f));
            }
            var payload = new ScreenFrame(Convert.ToBase64String(main), "image/jpeg", dimensions.Width, dimensions.Height, overviewData);
            Task? pending;
            lock (sync)
            {
                if (!active || generation != ticket || pool != sender || deliver is null) return;
                // Invocation occurs under the same gate as Halt. The transport
                // callback must also observe its own revoked session generation.
                pending = deliver(payload);
            }
            await pending;
        }
        catch (Exception) { Lost(); }
        finally
        {
            try { frame?.Dispose(); }
            catch (Exception) { Lost(); }
            finally { processing.Release(); }
        }
    }
    private static void Validate(ScreenQuality q, ScreenRegion r)
    {
        if (q.Width is < 64 or > 3840 || q.Height is < 64 or > 2160 || q.Fps is < 1 or > 30 || q.MaxBitrateKbps is < 100 or > 20000) throw new ArgumentOutOfRangeException(nameof(q));
        if (!double.IsFinite(r.X) || !double.IsFinite(r.Y) || !double.IsFinite(r.Width) || !double.IsFinite(r.Height)
            || r.X < 0 || r.Y < 0 || r.Width < .02 || r.Height < .02 || r.X + r.Width > 1.000001 || r.Y + r.Height > 1.000001) throw new ArgumentOutOfRangeException(nameof(r));
    }
    private static void CheckSize(SizeInt32 size)
    {
        if (size.Width <= 0 || size.Height <= 0 || size.Width > 16384 || size.Height > 16384 || (long)size.Width * size.Height > 67_108_864) throw new IOException("Unsupported capture dimensions.");
    }
    private static Rect Crop(SizeInt32 size, ScreenRegion region)
    {
        var x = Math.Clamp(Math.Floor(region.X * size.Width), 0, size.Width - 1); var y = Math.Clamp(Math.Floor(region.Y * size.Height), 0, size.Height - 1);
        return new(x, y, Math.Clamp(Math.Ceiling(region.Width * size.Width), 1, size.Width - x), Math.Clamp(Math.Ceiling(region.Height * size.Height), 1, size.Height - y));
    }
    private static SizeInt32 Fit(double width, double height, int maximumWidth, int maximumHeight)
    {
        var scale = Math.Min(1, Math.Min(maximumWidth / width, maximumHeight / height));
        return new() { Width = Math.Max(1, (int)Math.Floor(width * scale)), Height = Math.Max(1, (int)Math.Floor(height * scale)) };
    }
    private static async Task<byte[]> Encode(CanvasDevice canvas, CanvasBitmap source, Rect sourceRect, int width, int height, float jpegQuality)
    {
        using var target = new CanvasRenderTarget(canvas, width, height, 96);
        using (var drawing = target.CreateDrawingSession()) { drawing.Clear(Windows.UI.Color.FromArgb(255, 0, 0, 0)); drawing.DrawImage(source, new Rect(0, 0, width, height), sourceRect); }
        using var stream = new InMemoryRandomAccessStream(); await target.SaveAsync(stream, CanvasBitmapFileFormat.Jpeg, jpegQuality);
        if (stream.Size is 0 or > 8_388_608) throw new IOException("Encoded screen frame exceeds the transport limit.");
        var bytes = new byte[(int)stream.Size]; using var reader = new DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync((uint)bytes.Length); reader.ReadBytes(bytes); return bytes;
    }
    public async ValueTask DisposeAsync()
    {
        lock (sync) { if (disposed) return; disposed = true; }
        await StopAsync();
        // A late native event may still enter OnFrameArrived. Keep the tiny gates
        // alive so it can observe the stopped state instead of throwing in native code.
    }
    internal static async Task<Dictionary<string, object?>> SmokeAsync()
    {
        // Exercise the actual Windows GPU/WIC bridge with synthetic pixels. The
        // unattended CI smoke must never capture the runner's desktop contents.
        using var canvas = new CanvasDevice(true);
        using var source = new CanvasRenderTarget(canvas, 100, 80, 96);
        using (var drawing = source.CreateDrawingSession())
        {
            drawing.Clear(Windows.UI.Color.FromArgb(255, 0, 0, 255)); drawing.FillRectangle(0, 0, 50, 80, Windows.UI.Color.FromArgb(255, 255, 0, 0));
        }
        var crop = Crop(new SizeInt32 { Width = 100, Height = 80 }, new ScreenRegion(0, 0, .5, 1)); var size = Fit(crop.Width, crop.Height, 50, 40);
        var encoded = await Encode(canvas, source, crop, size.Width, size.Height, .9f);
        using var stream = new InMemoryRandomAccessStream(); using (var writer = new DataWriter(stream.GetOutputStreamAt(0))) { writer.WriteBytes(encoded); await writer.StoreAsync(); writer.DetachStream(); } stream.Seek(0);
        var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
        var pixels = (await decoder.GetPixelDataAsync(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Ignore,
            new Windows.Graphics.Imaging.BitmapTransform(), Windows.Graphics.Imaging.ExifOrientationMode.IgnoreExifOrientation, Windows.Graphics.Imaging.ColorManagementMode.DoNotColorManage)).DetachPixelData();
        var center = ((size.Height / 2) * size.Width + size.Width / 2) * 4;
        if (decoder.PixelWidth != 25 || decoder.PixelHeight != 40 || pixels[center + 2] < 200 || pixels[center] > 50) throw new InvalidOperationException("Screen capture crop/JPEG bridge did not preserve the selected source region.");
        var displays = EnumerateMonitors();
        if (displays.Count == 0 || displays.Any(m => m.Display.Width <= 0 || m.Display.Height <= 0) || displays.Select(m => m.Display.DisplayId).Distinct().Count() != displays.Count) throw new InvalidOperationException("Screen display enumeration failed.");
        return new() { ["syntheticGpuCropEncoded"] = true, ["displayBoundsEnumerated"] = true, ["captureSupported"] = Supported, ["desktopCaptured"] = false };
    }
    private sealed record Monitor(nint Handle, ScreenDisplay Display);
    private static IReadOnlyList<Monitor> EnumerateMonitors()
    {
        var result = new List<Monitor>();
        MonitorProc callback = (nint handle, nint dc, ref NativeRect rect, nint data) =>
        {
            var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>(), Device = "" };
            if (GetMonitorInfo(handle, ref info))
            {
                var hash = SHA256.HashData(Encoding.Unicode.GetBytes(info.Device)); var id = (int)(BinaryPrimitives.ReadUInt32LittleEndian(hash) & 0x7fffffff);
                result.Add(new(handle, new(id, info.Monitor.Right - info.Monitor.Left, info.Monitor.Bottom - info.Monitor.Top, (info.Flags & 1) != 0, info.Monitor.Left, info.Monitor.Top)));
            }
            return true;
        };
        if (!EnumDisplayMonitors(0, 0, callback, 0)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return result.OrderByDescending(m => m.Display.Main).ThenBy(m => m.Display.DisplayId).ToArray();
    }
    private static GraphicsCaptureItem CaptureItem(nint monitor)
    {
        // IGraphicsCaptureItemInterop is an IUnknown interface (monitor method
        // is vtable slot 4), not an IInspectable interface with six base slots.
        nint text = 0, factory = 0, result = 0;
        try
        {
            const string name = "Windows.Graphics.Capture.GraphicsCaptureItem";
            Marshal.ThrowExceptionForHR(WindowsCreateString(name, name.Length, out text));
            var iid = new Guid("3628e81b-3cac-4c60-b7f4-23ce0e0c3356"); Marshal.ThrowExceptionForHR(RoGetActivationFactory(text, ref iid, out factory));
            var method = Marshal.GetDelegateForFunctionPointer<CreateForMonitor>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(factory), 4 * IntPtr.Size));
            var itemInterface = new Guid("79c3f95b-31f7-4ec2-a464-632ef5d30760"); Marshal.ThrowExceptionForHR(method(factory, monitor, ref itemInterface, out result));
            return WinRT.MarshalInterface<GraphicsCaptureItem>.FromAbi(result);
        }
        finally { if (result != 0) Marshal.Release(result); if (factory != 0) Marshal.Release(factory); if (text != 0) WindowsDeleteString(text); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct MonitorInfo { public uint Size; public NativeRect Monitor, Work; public uint Flags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device; }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate bool MonitorProc(nint monitor, nint dc, ref NativeRect rect, nint data);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateForMonitor(nint self, nint monitor, ref Guid iid, out nint result);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorProc callback, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("combase.dll", CharSet = CharSet.Unicode)] private static extern int WindowsCreateString(string text, int length, out nint value);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(nint value);
    [DllImport("combase.dll")] private static extern int RoGetActivationFactory(nint name, ref Guid iid, out nint result);
}
