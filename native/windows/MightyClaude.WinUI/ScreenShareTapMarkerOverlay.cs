using System.Runtime.InteropServices;
using MightyClaude.Core;
using Microsoft.UI.Dispatching;

namespace MightyClaude.WinUI;

/// Captured with the desktop, but never participates in pointer or keyboard
/// input. Matches the Mac latency marker: 48 points, visible for half a second.
internal sealed class ScreenShareTapMarkerOverlay(DispatcherQueue dispatcher) : IDisposable
{
    private MarkerWindow? marker;
    private long generation;
    private volatile bool disposed;
    public Task<bool> ShowAsync(ScreenDisplay display, double normalizedX, double normalizedY, string nonce)
    {
        if (disposed || !double.IsFinite(normalizedX) || !double.IsFinite(normalizedY) || normalizedX is < 0 or > 1 || normalizedY is < 0 or > 1
            || display.Width <= 0 || display.Height <= 0 || nonce.Length is < 1 or > 32 || !nonce.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-')) return Task.FromResult(false);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ticket = Interlocked.Increment(ref generation);
        if (!dispatcher.TryEnqueue(async () =>
        {
            if (disposed || ticket != Interlocked.Read(ref generation)) { completion.TrySetResult(false); return; }
            try
            {
                marker?.Dispose();
                var x = display.Left + (int)Math.Round(normalizedX * (display.Width - 1)); var y = display.Top + (int)Math.Round(normalizedY * (display.Height - 1));
                marker = new MarkerWindow(x, y); completion.TrySetResult(true);
                await Task.Delay(500);
                dispatcher.TryEnqueue(() => { if (ticket == Interlocked.Read(ref generation)) { marker?.Dispose(); marker = null; } });
            }
            catch { marker?.Dispose(); marker = null; completion.TrySetResult(false); }
        })) completion.TrySetResult(false);
        return completion.Task;
    }
    public void Clear()
    {
        var ticket = Interlocked.Increment(ref generation);
        void Close() { if (ticket != Interlocked.Read(ref generation)) return; marker?.Dispose(); marker = null; }
        if (dispatcher.HasThreadAccess) Close(); else dispatcher.TryEnqueue(Close);
    }
    public void Dispose() { disposed = true; Clear(); }
    internal static bool SmokeNoFocus()
    {
        var foreground = MarkerWindow.Foreground; using var marker = new MarkerWindow(100, 100);
        return marker.ClickThrough && MarkerWindow.Foreground == foreground;
    }
    private sealed class MarkerWindow : IDisposable
    {
        private readonly Procedure procedure;
        private readonly string name = "MightyScreenMarker-" + Guid.NewGuid().ToString("N");
        private readonly nint instance = GetModuleHandle(null);
        private nint hwnd;
        internal static nint Foreground => GetForegroundWindow();
        internal bool ClickThrough => (GetWindowLongPtr(hwnd, -20).ToInt64() & (0x08000000 | 0x00000020)) == (0x08000000 | 0x00000020);
        internal MarkerWindow(int centerX, int centerY)
        {
            procedure = (handle, message, w, l) => message switch { 0x0084 => -1, 0x0021 => 3, _ => DefWindowProc(handle, message, w, l) };
            var cls = new WindowClass { Size = (uint)Marshal.SizeOf<WindowClass>(), Procedure = Marshal.GetFunctionPointerForDelegate(procedure), Instance = instance, ClassName = name };
            if (RegisterClassEx(ref cls) == 0) throw new System.ComponentModel.Win32Exception();
            hwnd = CreateWindowEx(0x080800a0, name, "Mighty Claude tap marker", 0x80000000, centerX - 24, centerY - 24, 48, 48, 0, 0, instance, 0);
            if (hwnd == 0) { UnregisterClass(name, instance); throw new System.ComponentModel.Win32Exception(); }
            try
            {
                var scale = Math.Clamp(GetDpiForWindow(hwnd) / 96.0, 1, 4); var side = (int)Math.Ceiling(48 * scale);
                var pixels = new byte[side * side * 4];
                for (var y = 0; y < side; y++) for (var x = 0; x < side; x++)
                {
                    var dx = (x + .5) / scale - 24; var dy = (y + .5) / scale - 24; var radius = Math.Sqrt(dx * dx + dy * dy);
                    byte red = 0, green = 0, blue = 0; double alpha;
                    if (radius > 18 && radius <= 24) alpha = Math.Clamp(Math.Min(radius - 18, 24 - radius) * scale, 0, 1);
                    else if (radius > 14 && radius <= 18) { red = green = blue = 255; alpha = Math.Clamp(Math.Min(radius - 14, 18 - radius) * scale, 0, 1); }
                    else if (radius <= 7.56) { red = blue = 255; alpha = Math.Clamp((7.56 - radius) * scale, 0, 1); }
                    else continue;
                    var offset = (y * side + x) * 4; pixels[offset] = (byte)(blue * alpha); pixels[offset + 1] = (byte)(green * alpha); pixels[offset + 2] = (byte)(red * alpha); pixels[offset + 3] = (byte)(255 * alpha);
                }
                var dc = CreateCompatibleDC(0); if (dc == 0) throw new System.ComponentModel.Win32Exception();
                nint bitmap = 0, old = 0;
                try
                {
                    var info = new BitmapInfo { Size = 40, Width = side, Height = -side, Planes = 1, BitCount = 32 };
                    bitmap = CreateDIBSection(dc, ref info, 0, out var bits, 0, 0); if (bitmap == 0) throw new System.ComponentModel.Win32Exception();
                    old = SelectObject(dc, bitmap); Marshal.Copy(pixels, 0, bits, pixels.Length);
                    var position = new Point { X = centerX - side / 2, Y = centerY - side / 2 }; var size = new Point { X = side, Y = side }; var source = new Point(); var blend = new Blend { Alpha = 255, Format = 1 };
                    if (!UpdateLayeredWindow(hwnd, 0, ref position, ref size, dc, ref source, 0, ref blend, 2)) throw new System.ComponentModel.Win32Exception();
                    SetWindowPos(hwnd, -1, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0040);
                }
                finally { if (old != 0) SelectObject(dc, old); if (bitmap != 0) DeleteObject(bitmap); DeleteDC(dc); }
            }
            catch { Dispose(); throw; }
        }
        public void Dispose() { if (hwnd != 0) { DestroyWindow(hwnd); hwnd = 0; } UnregisterClass(name, instance); }
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate nint Procedure(nint hwnd, uint message, nuint wParam, nint lParam);
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WindowClass { public uint Size, Style; public nint Procedure; public int ClassExtra, WindowExtra; public nint Instance, Icon, Cursor, Background; public string? Menu; public string ClassName; public nint SmallIcon; }
        [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo { public uint Size; public int Width, Height; public ushort Planes, BitCount; public uint Compression, SizeImage; public int XPixels, YPixels; public uint Used, Important, Color; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct Blend { public byte Operation, Flags, Alpha, Format; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassEx(ref WindowClass value);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool UnregisterClass(string name, nint instance);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateWindowEx(uint extended, string cls, string title, uint style, int x, int y, int width, int height, nint owner, nint menu, nint instance, nint data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint DefWindowProc(nint hwnd, uint message, nuint wParam, nint lParam);
        [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
        [DllImport("user32.dll")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
        [DllImport("user32.dll")] private static extern bool DestroyWindow(nint hwnd);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")] private static extern bool UpdateLayeredWindow(nint hwnd, nint screen, ref Point destination, ref Point size, nint dc, ref Point source, uint key, ref Blend blend, uint flags);
        [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
        [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);
        [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint value);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
    }
}
