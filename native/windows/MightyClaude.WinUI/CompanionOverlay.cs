using System.ComponentModel;
using System.Runtime.InteropServices;
using MightyClaude.Core;

namespace MightyClaude.WinUI;

internal sealed record CompanionOverlayButton(string Id, string Label);
internal sealed record CompanionOverlayCard(string Key, string Title, string Subtitle, string Body, string? Page,
    IReadOnlyList<CompanionOverlayButton> Buttons, bool Light = false);

/// A layered HWND instead of a focusable XAML window. WS_EX_NOACTIVATE plus
/// MA_NOACTIVATE keep IME ownership with the user's current application even
/// while the pet is dragged or its approval buttons are clicked.
internal sealed class CompanionOverlay : IDisposable
{
    private const uint ExStyle = 0x08000000 | 0x00080000 | 0x00000080; // NOACTIVATE, LAYERED, TOOLWINDOW
    private readonly WindowProc procedure;
    private readonly string className = "MightyCompanion-" + Guid.NewGuid().ToString("N");
    private readonly nint instance = GetModuleHandle(null);
    private nint window, dc, bitmap, oldBitmap, bits;
    private int pixelWidth, pixelHeight, left, top;
    private double scale = 1;
    private byte[] pixels = [];
    private byte[] bubblePixels = [];
    private bool cardDirty = true;
    private int lastRow = -1, lastColumn = -1;
    private byte[] atlas = [];
    private CompanionOverlayCard? card;
    private readonly List<(Rect Bounds, string Id)> hitButtons = [];
    private bool mouseDown, dragged, disposed, showBubble;
    private Point dragStart, startOrigin;
    private string? pressedAction, pressedKey;
    private int width, bubbleHeight, height, startWidth, startHeight;
    internal event Action<string, string>? Action;
    internal event Action<int, int>? Moved;
    internal event Action<int, int>? Resized;
    internal nint Handle => window;
    internal bool NonActivating => (GetWindowLongPtr(window, -20).ToInt64() & 0x08000000) != 0;
    internal bool SmokeClick(string id)
    {
        var hit = hitButtons.FirstOrDefault(item => item.Id == id); if (hit.Id is null) return false;
        var x = (int)((hit.Bounds.Left + hit.Bounds.Right) * scale / 2); var y = (int)((hit.Bounds.Top + hit.Bounds.Bottom) * scale / 2);
        var point = (nint)((y << 16) | x); SendMessage(window, 0x0201, 1, point); SendMessage(window, 0x0202, 0, point); return true;
    }
    internal static nint ForegroundWindow => GetForegroundWindow();

    internal CompanionOverlay(CompanionPreferences preferences)
    {
        width = (int)Math.Clamp(preferences.BubbleWidth is { } w && double.IsFinite(w) ? w : 360, 300, 560);
        bubbleHeight = (int)Math.Clamp(preferences.BubbleHeight is { } h && double.IsFinite(h) ? h : 280, 220, 500); height = bubbleHeight + 152;
        procedure = WndProc;
        var cls = new WindowClass { Size = (uint)Marshal.SizeOf<WindowClass>(), Procedure = Marshal.GetFunctionPointerForDelegate(procedure), Instance = instance, ClassName = className, Cursor = LoadCursor(0, 32512) };
        if (RegisterClassEx(ref cls) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        window = CreateWindowEx(ExStyle, className, "Mighty Claude Companion", 0x80000000, 0, 0, width, height, 0, 0, instance, 0);
        if (window == 0) { UnregisterClass(className, instance); throw new Win32Exception(Marshal.GetLastWin32Error()); }
        try
        {
            scale = Math.Clamp(GetDpiForWindow(window) / 96.0, 1, 4);
            dc = CreateCompatibleDC(0); if (dc == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            Allocate(); var work = WorkArea(new Point { X = preferences.Left ?? 0, Y = preferences.Top ?? 0 });
            left = preferences.Left ?? work.Right - pixelWidth - 24; top = preferences.Top ?? work.Bottom - pixelHeight - 24;
            ClampPosition();
        }
        catch { Dispose(); throw; }
    }
    internal void SetAtlas(byte[] premultipliedBgra) { atlas = premultipliedBgra; lastRow = -1; }
    internal void SetCard(CompanionOverlayCard? value, bool visible)
    {
        var shown = visible && value is not null;
        if (showBubble != shown || card?.Key != value?.Key || card?.Title != value?.Title || card?.Subtitle != value?.Subtitle
            || card?.Body != value?.Body || card?.Page != value?.Page || card?.Light != value?.Light || !(card?.Buttons ?? []).SequenceEqual(value?.Buttons ?? [])) cardDirty = true;
        card = value; showBubble = shown;
    }
    internal void Show(bool visible) { if (window != 0) ShowWindow(window, visible ? 8 : 0); }
    private void Allocate()
    {
        if (bitmap != 0) { SelectObject(dc, oldBitmap); DeleteObject(bitmap); bitmap = 0; }
        pixelWidth = (int)Math.Ceiling(width * scale); pixelHeight = (int)Math.Ceiling(height * scale); pixels = new byte[pixelWidth * pixelHeight * 4]; bubblePixels = new byte[pixels.Length]; cardDirty = true;
        var info = new BitmapInfo { Size = 40, Width = pixelWidth, Height = -pixelHeight, Planes = 1, BitCount = 32, SizeImage = (uint)pixels.Length };
        bitmap = CreateDIBSection(dc, ref info, 0, out bits, 0, 0); if (bitmap == 0) throw new Win32Exception(Marshal.GetLastWin32Error()); oldBitmap = SelectObject(dc, bitmap);
    }
    internal void Draw(int row, int column)
    {
        if (disposed || atlas.Length == 0) return;
        if (!cardDirty && lastRow == row && lastColumn == column) return;
        lastRow = row; lastColumn = column;
        if (cardDirty) { Array.Clear(pixels); hitButtons.Clear(); }
        else Buffer.BlockCopy(bubblePixels, 0, pixels, 0, pixels.Length);
        if (cardDirty && showBubble && card is { } current)
        {
            Rounded(0, 0, width, bubbleHeight, 18, current.Light ? (byte)247 : (byte)30, current.Light ? (byte)248 : (byte)33, current.Light ? (byte)251 : (byte)40);
            Marshal.Copy(pixels, 0, bits, pixels.Length);
            Text(current.Title, 16, 12, width - 32, 26, 15, true, current.Light ? 0x00302219u : 0x00f2efed);
            Text(current.Subtitle, 16, 41, width - 32, 23, 11, false, current.Light ? 0x006d6259u : 0x00b9b3ae);
            var buttonsHeight = current.Buttons.Count * 29;
            Text(current.Body, 16, 70, width - 32, Math.Max(30, bubbleHeight - 88 - buttonsHeight - (current.Page is null ? 0 : 27)), 12, false, current.Light ? 0x00302219u : 0x00f2efed);
            int y = bubbleHeight - 10 - buttonsHeight;
            if (current.Page is not null)
            {
                var ink = current.Light ? 0x00302219u : 0x00f2efed;
                Text("‹", 16, y - 26, 30, 22, 16, true, ink); Text(current.Page, 52, y - 24, width - 104, 22, 11, false, ink); Text("›", width - 44, y - 26, 30, 22, 16, true, ink);
                hitButtons.Add((new(8, y - 28, 48, y), "previous")); hitButtons.Add((new(width - 48, y - 28, width - 8, y), "next"));
            }
            foreach (var button in current.Buttons)
            {
                Text(button.Label, 20, y, width - 40, 27, 12, false, current.Light ? 0x00a65a16u : 0x00ffca92); hitButtons.Add((new(12, y, width - 12, y + 27), button.Id)); y += 29;
            }
            Text("◢", width - 20, bubbleHeight - 20, 16, 16, 10, false, 0x008f8880);
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            // GDI text writes RGB with alpha zero. Restore alpha only inside the
            // rounded card, leaving the transparent exterior click-through.
            for (var py = 0; py < (int)(bubbleHeight * scale); py++) for (var px = 0; px < pixelWidth; px++)
                if (InsideRounded(px / scale, py / scale, width, bubbleHeight, 18)) pixels[(py * pixelWidth + px) * 4 + 3] = 255;
        }
        if (cardDirty) { Buffer.BlockCopy(pixels, 0, bubblePixels, 0, pixels.Length); cardDirty = false; }
        var spriteSize = 128; int x0 = width - spriteSize - 8, y0 = bubbleHeight + 3, outWidth = (int)(spriteSize * scale), outHeight = (int)(spriteSize * 208.0 / 192 * scale);
        row = Math.Clamp(row, 0, 8); column = Math.Clamp(column, 0, CompanionPet.FrameCounts[row] - 1);
        for (var y = 0; y < outHeight; y++) for (var x = 0; x < outWidth; x++)
        {
            var src = (((row * 208 + y * 208 / outHeight) * 1536) + column * 192 + x * 192 / outWidth) * 4;
            var dst = (((int)(y0 * scale) + y) * pixelWidth + (int)(x0 * scale) + x) * 4;
            if (src + 3 >= atlas.Length || dst + 3 >= pixels.Length) continue;
            var alpha = atlas[src + 3]; if (alpha == 0) continue;
            for (var c = 0; c < 4; c++) pixels[dst + c] = (byte)Math.Min(255, atlas[src + c] + pixels[dst + c] * (255 - alpha) / 255);
        }
        Marshal.Copy(pixels, 0, bits, pixels.Length);
        var destination = new Point { X = left, Y = top }; var source = new Point(); var size = new Point { X = pixelWidth, Y = pixelHeight }; var blend = new Blend { Operation = 0, SourceAlpha = 255, Format = 1 };
        if (!UpdateLayeredWindow(window, 0, ref destination, ref size, dc, ref source, 0, ref blend, 2)) throw new Win32Exception(Marshal.GetLastWin32Error());
        SetWindowPos(window, -1, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010); // topmost, no activation/move/resize
    }
    private void Rounded(int x, int y, int w, int h, int radius, byte red, byte green, byte blue)
    {
        for (var py = (int)(y * scale); py < (y + h) * scale; py++) for (var px = (int)(x * scale); px < (x + w) * scale; px++)
        {
            if (!InsideRounded(px / scale - x, py / scale - y, w, h, radius)) continue;
            var i = (py * pixelWidth + px) * 4; pixels[i] = blue; pixels[i + 1] = green; pixels[i + 2] = red; pixels[i + 3] = 255;
        }
    }
    private static bool InsideRounded(double x, double y, int w, int h, int radius)
    {
        var dx = Math.Max(radius - x, Math.Max(0, x - (w - radius))); var dy = Math.Max(radius - y, Math.Max(0, y - (h - radius))); return dx * dx + dy * dy <= radius * radius;
    }
    private void Text(string value, int x, int y, int w, int h, int size, bool bold, uint color = 0x00f2efed)
    {
        var font = CreateFont(-(int)(size * scale), 0, 0, 0, bold ? 600 : 400, 0, 0, 0, 1, 0, 0, 4, 0, "Segoe UI");
        var old = SelectObject(dc, font);
        try { SetBkMode(dc, 1); SetTextColor(dc, color); var rect = new Rect((int)(x * scale), (int)(y * scale), (int)((x + w) * scale), (int)((y + h) * scale)); DrawText(dc, value, value.Length, ref rect, 0x0010 | 0x0800 | 0x8000); }
        finally { SelectObject(dc, old); DeleteObject(font); }
    }
    private nint WndProc(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        // Do not allow any managed exception to cross the native callback.
        try
        {
            switch (message)
            {
                case 0x0021: return 3; // WM_MOUSEACTIVATE / MA_NOACTIVATE
                case 0x0201:
                    GetCursorPos(out dragStart); startOrigin = new() { X = left, Y = top }; mouseDown = true; dragged = false;
                    startWidth = width; startHeight = bubbleHeight;
                    var p = new Point { X = (short)(lParam.ToInt64() & 65535), Y = (short)((lParam.ToInt64() >> 16) & 65535) };
                    pressedAction = Hit(p); pressedKey = card?.Key ?? ""; SetCapture(hwnd); return 0;
                case 0x0200 when mouseDown:
                    GetCursorPos(out var move); var dx = move.X - dragStart.X; var dy = move.Y - dragStart.Y;
                    if (pressedAction == "toggle" && (dragged || Math.Abs(dx) + Math.Abs(dy) > 5))
                    { dragged = true; left = startOrigin.X + dx; top = startOrigin.Y + dy; SetWindowPos(hwnd, -1, left, top, 0, 0, 0x0001 | 0x0010); }
                    else if (pressedAction == "resize")
                    {
                        var nextWidth = Math.Clamp(startWidth + (int)(dx / scale), 300, 560); var nextHeight = Math.Clamp(startHeight + (int)(dy / scale), 220, 500);
                        if (nextWidth != width || nextHeight != bubbleHeight) { dragged = true; width = nextWidth; bubbleHeight = nextHeight; height = bubbleHeight + 152; Allocate(); }
                    }
                    return 0;
                case 0x0202 when mouseDown:
                    var completedAction = pressedAction; var completedKey = pressedKey; mouseDown = false; ReleaseCapture();
                    if (dragged) { ClampPosition(); Moved?.Invoke(left, top); if (completedAction == "resize") Resized?.Invoke(width, bubbleHeight); }
                    else { var release = new Point { X = (short)(lParam.ToInt64() & 65535), Y = (short)((lParam.ToInt64() >> 16) & 65535) }; if (completedAction is { } action && action == Hit(release) && completedKey == (card?.Key ?? "")) Action?.Invoke(completedKey!, action); }
                    pressedAction = null; return 0;
                case 0x0215: mouseDown = false; pressedAction = null; return 0; // capture lost
                case 0x020e: Action?.Invoke(card?.Key ?? "", unchecked((short)(wParam >> 16)) < 0 ? "next" : "previous"); return 0;
                case 0x007e: ClampPosition(); return 0; // monitor configuration changed
                case 0x02e0:
                    scale = Math.Clamp((wParam & 65535) / 96.0, 1, 4); Allocate(); ClampPosition(); return 0;
            }
        }
        catch { mouseDown = false; }
        return DefWindowProc(hwnd, message, wParam, lParam);
    }
    private string Hit(Point p)
    {
        double x = p.X / scale, y = p.Y / scale;
        if (showBubble && y < bubbleHeight) { if (x > width - 20 && y > bubbleHeight - 20) return "resize"; foreach (var (bounds, id) in hitButtons) if (x >= bounds.Left && x < bounds.Right && y >= bounds.Top && y < bounds.Bottom) return id; return "open"; }
        return "toggle";
    }
    private static Rect WorkArea(Point point)
    {
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() }; var monitor = MonitorFromPoint(point, 2);
        return GetMonitorInfo(monitor, ref info) ? info.Work : new Rect(0, 0, 1920, 1080);
    }
    private void ClampPosition()
    {
        var work = WorkArea(new Point { X = left + pixelWidth - 64, Y = top + pixelHeight - 64 });
        left = Math.Clamp(left, work.Left, Math.Max(work.Left, work.Right - pixelWidth)); top = Math.Clamp(top, work.Top, Math.Max(work.Top, work.Bottom - pixelHeight));
        if (window != 0) SetWindowPos(window, -1, left, top, pixelWidth, pixelHeight, 0x0010);
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        if (window != 0) { DestroyWindow(window); window = 0; }
        if (bitmap != 0) { SelectObject(dc, oldBitmap); DeleteObject(bitmap); bitmap = 0; }
        if (dc != 0) { DeleteDC(dc); dc = 0; }
        UnregisterClass(className, instance); atlas = []; pixels = []; bubblePixels = [];
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate nint WindowProc(nint hwnd, uint message, nuint wParam, nint lParam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WindowClass { public uint Size, Style; public nint Procedure; public int ClassExtra, WindowExtra; public nint Instance, Icon, Cursor, Background; public string? MenuName; public string ClassName; public nint SmallIcon; }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect(int left, int top, int right, int bottom) { public int Left = left, Top = top, Right = right, Bottom = bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public uint Size; public Rect Monitor, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo { public uint Size; public int Width, Height; public ushort Planes, BitCount; public uint Compression, SizeImage; public int XPixels, YPixels; public uint ColorsUsed, ColorsImportant, Color; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct Blend { public byte Operation, Flags, SourceAlpha, Format; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WindowClass cls);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool UnregisterClass(string name, nint instance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowEx(uint extended, string cls, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint DefWindowProc(nint hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("user32.dll")] private static extern nint LoadCursor(nint instance, int id);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern nint SetCapture(nint hwnd);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint SendMessage(nint hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(Point point, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UpdateLayeredWindow(nint hwnd, nint screenDc, ref Point destination, ref Point size, nint sourceDc, ref Point source, uint key, ref Blend blend, uint flags);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint item);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint item);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateFont(int height, int width, int escape, int orient, int weight, uint italic, uint underline, uint strike, uint charset, uint output, uint clip, uint quality, uint pitch, string name);
    [DllImport("gdi32.dll")] private static extern int SetBkMode(nint dc, int mode);
    [DllImport("gdi32.dll")] private static extern uint SetTextColor(nint dc, uint color);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int DrawText(nint dc, string text, int count, ref Rect rect, uint format);
}
