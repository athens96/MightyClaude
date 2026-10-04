using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using MightyClaude.Core;

namespace MightyClaude.WinUI;

/// Native input never bypasses Windows UIPI. Only public, validated wire keys
/// are mapped, and committed Unicode text uses KEYEVENTF_UNICODE (no IME state
/// changes). The observer reads password-state booleans, never field contents.
internal sealed class WindowsRemoteInput : IDisposable
{
    private static readonly nuint Marker = 0x4D435352;
    private readonly object sync = new();
    private readonly Hook keyboardProc, mouseProc;
    private readonly Thread observer;
    private readonly ManualResetEvent stop = new(false);
    private nint keyboardHook, mouseHook;
    private long lastLocalTicks = DateTimeOffset.UtcNow.UtcTicks, observedTicks;
    private volatile bool protectedFocus = true, disposed;
    private string? dragOwner;
    public event Action? KillRequested;
    public bool Available => keyboardHook != 0 && mouseHook != 0 && !disposed;
    public WindowsRemoteInput()
    {
        keyboardProc = Keyboard; mouseProc = Mouse;
        keyboardHook = SetWindowsHookEx(13, keyboardProc, GetModuleHandle(null), 0);
        mouseHook = SetWindowsHookEx(14, mouseProc, GetModuleHandle(null), 0);
        observer = new Thread(Observe) { IsBackground = true, Name = "Screen privacy observer" }; observer.SetApartmentState(ApartmentState.MTA); observer.Start();
    }
    public ScreenSafety Safety
    {
        get { var secure = protectedFocus || DateTimeOffset.UtcNow.UtcTicks - Interlocked.Read(ref observedTicks) > TimeSpan.FromMilliseconds(750).Ticks; return new(!DefaultDesktop(), secure || !Available, new DateTimeOffset(Interlocked.Read(ref lastLocalTicks), TimeSpan.Zero)); }
    }
    private void Observe()
    {
        IAutomation? automation = null;
        try
        {
            automation = (IAutomation)Activator.CreateInstance(Type.GetTypeFromCLSID(new("FF48DBA4-60EF-4201-AA87-54103EEF594E"), true)!)!;
            while (!stop.WaitOne(150))
            {
                var secure = true; IElement? element = null;
                try { automation.GetFocusedElement(out element); element.GetCurrentPropertyValueEx(30019, true, out var property); secure = property is not false; }
                catch (COMException) { }
                finally { if (element is not null) Marshal.ReleaseComObject(element); }
                protectedFocus = secure; Interlocked.Exchange(ref observedTicks, DateTimeOffset.UtcNow.UtcTicks);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or TypeLoadException) { }
        finally { if (automation is not null) Marshal.ReleaseComObject(automation); }
    }
    private nint Keyboard(int code, nint message, nint pointer)
    {
        if (code >= 0)
        {
            var key = Marshal.PtrToStructure<KeyboardHook>(pointer);
            if (key.Extra != Marker)
            {
                Interlocked.Exchange(ref lastLocalTicks, DateTimeOffset.UtcNow.UtcTicks);
                // A physical Ctrl+Alt+Escape is the global kill switch. Remote
                // input is marked and cannot consume or generate this shortcut.
                if ((message == 0x100 || message == 0x104) && key.Key == 0x1B && (GetAsyncKeyState(0x11) & 0x8000) != 0 && (GetAsyncKeyState(0x12) & 0x8000) != 0) KillRequested?.Invoke();
            }
        }
        return CallNextHookEx(0, code, message, pointer);
    }
    private nint Mouse(int code, nint message, nint pointer)
    {
        if (code >= 0 && Marshal.PtrToStructure<MouseHook>(pointer).Extra != Marker) Interlocked.Exchange(ref lastLocalTicks, DateTimeOffset.UtcNow.UtcTicks);
        return CallNextHookEx(0, code, message, pointer);
    }
    private static bool DefaultDesktop()
    {
        var desktop = OpenInputDesktop(0, false, 1); if (desktop == 0) return false;
        try { var name = new StringBuilder(256); return GetUserObjectInformation(desktop, 2, name, name.Capacity * 2, out _) && name.ToString().Equals("Default", StringComparison.OrdinalIgnoreCase); }
        finally { CloseDesktop(desktop); }
    }
    public void Inject(string owner, JsonElement value, IReadOnlyList<ScreenDisplay> displays, CancellationToken token, Func<bool> current)
    {
        bool Continue() => !token.IsCancellationRequested && current() && !disposed && !protectedFocus && DateTimeOffset.UtcNow.UtcTicks - Interlocked.Read(ref observedTicks) <= TimeSpan.FromMilliseconds(750).Ticks && DateTimeOffset.UtcNow.UtcTicks - Interlocked.Read(ref lastLocalTicks) >= TimeSpan.FromSeconds(2).Ticks;
        if (!Continue()) return;
        var safety = Safety; if (safety.Locked || safety.SecureInput || DateTimeOffset.UtcNow - safety.LastLocalInput < TimeSpan.FromSeconds(2)) return;
        // The platform input queue serializes workers; never hold a state
        // lock across SendInput, which can synchronously call UI-thread hooks.
        {
            if (!Continue()) return;
            var type = value.Text("t");
            if (type is "tap" or "drag" or "scroll")
            {
                var display = displays.FirstOrDefault(d => d.DisplayId == value.GetProperty("displayId").GetInt32()); if (display is null) return;
                var x = display.Left + Math.Clamp(value.GetProperty("x").GetDouble(), 0, 1) * Math.Max(0, display.Width - 1);
                var y = display.Top + Math.Clamp(value.GetProperty("y").GetDouble(), 0, 1) * Math.Max(0, display.Height - 1);
                var width = GetSystemMetrics(78); var height = GetSystemMetrics(79); if (width < 2 || height < 2) return;
                var absoluteX = (int)Math.Round((x - GetSystemMetrics(76)) * 65535 / (width - 1)); var absoluteY = (int)Math.Round((y - GetSystemMetrics(77)) * 65535 / (height - 1));
                Send([MouseEvent(0x8000 | 0x4000 | 1, Math.Clamp(absoluteX, 0, 65535), Math.Clamp(absoluteY, 0, 65535))]);
                if (!Continue()) return;
                if (type == "tap") { var right = value.Text("button") == "right"; Send([MouseEvent(right ? 8u : 2u), MouseEvent(right ? 16u : 4u)]); }
                else if (type == "drag")
                {
                    if (value.Text("phase") == "begin") { bool press; lock (sync) { press = dragOwner is null && Continue(); if (press) dragOwner = owner; } if (press) try { Send([MouseEvent(2)]); } catch { Release(owner); throw; } }
                    else if (value.Text("phase") == "end") Release(owner);
                }
                else
                {
                    var dx = (int)Math.Clamp(value.GetProperty("dx").GetDouble(), -2400, 2400); var dy = (int)Math.Clamp(value.GetProperty("dy").GetDouble(), -2400, 2400);
                    if (dy != 0) Send([MouseEvent(0x0800, data: unchecked((uint)-dy))]); if (dx != 0) Send([MouseEvent(0x1000, data: unchecked((uint)dx))]);
                }
            }
            else if (type == "text")
            {
                if (new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }.Any(k => (GetAsyncKeyState(k) & 0x8000) != 0)) return;
                foreach (var character in value.Text("text")!) { if (!Continue()) break; try { Send([KeyEvent(0, character, 4), KeyEvent(0, character, 4 | 2)]); } catch { Send([KeyEvent(0, character, 4 | 2)], false); throw; } }
            }
            else if (type == "key")
            {
                if (!Continue() || new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }.Any(k => (GetAsyncKeyState(k) & 0x8000) != 0)) return;
                var parts = value.Text("combo")!.Split('+'); var modifiers = parts[..^1].Select(p => p switch { "shift" => (ushort)0x10, "opt" => (ushort)0x12, _ => (ushort)0x11 }).Distinct().ToArray();
                var key = KeyCode(parts[^1]); var extended = key is >= 0x21 and <= 0x2E ? 1u : 0u;
                try { Send(modifiers.Select(k => KeyEvent(k)).Concat([KeyEvent(key, flags: extended), KeyEvent(key, flags: extended | 2)]).Concat(modifiers.Reverse().Select(k => KeyEvent(k, flags: 2))).ToArray()); }
                finally { Send(new[] { KeyEvent(key, flags: extended | 2) }.Concat(modifiers.Reverse().Select(k => KeyEvent(k, flags: 2))).ToArray(), false); }
            }
        }
    }
    public void Release(string owner)
    { lock (sync) { if (dragOwner != owner) return; dragOwner = null; } Send([MouseEvent(4)], false); }
    public void ReleaseAll() { string? owner; lock (sync) owner = dragOwner; if (owner is not null) Release(owner); }
    private static ushort KeyCode(string key) => key.Length == 1 ? (ushort)char.ToUpperInvariant(key[0]) : (ushort)(key switch { "return" => 13, "tab" => 9, "space" => 32, "backspace" => 8, "delete" => 46, "escape" => 27, "left" => 37, "right" => 39, "up" => 38, "down" => 40, "home" => 36, "end" => 35, "pageup" => 33, "pagedown" => 34, _ => throw new ArgumentException("Unsupported key.") });
    private static Input MouseEvent(uint flags, int x = 0, int y = 0, uint data = 0) => new() { Kind = 0, Data = new() { Mouse = new() { X = x, Y = y, Flags = flags, Data = data, Extra = Marker } } };
    private static Input KeyEvent(ushort key, char scan = '\0', uint flags = 0) => new() { Kind = 1, Data = new() { Keyboard = new() { Key = key, Scan = scan, Flags = flags, Extra = Marker } } };
    private static void Send(Input[] values, bool required = true) { if (values.Length > 0 && SendInput((uint)values.Length, values, Marshal.SizeOf<Input>()) != values.Length && required) throw new IOException("Windows could not deliver the remote input."); }
    public void Dispose()
    { lock (sync) { if (disposed) return; disposed = true; } ReleaseAll(); stop.Set(); if (keyboardHook != 0) UnhookWindowsHookEx(keyboardHook); if (mouseHook != 0) UnhookWindowsHookEx(mouseHook); keyboardHook = mouseHook = 0; /* An unresponsive accessibility provider must not hold shutdown. */ }
    private delegate nint Hook(int code, nint message, nint data);
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardHook { public uint Key, Scan, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseHook { public Point Point; public uint Data, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Kind; public InputData Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputData { [FieldOffset(0)] public MouseInput Mouse; [FieldOffset(0)] public KeyboardInput Keyboard; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort Key, Scan; public uint Flags, Time; public nuint Extra; }
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW")] private static extern nint SetWindowsHookEx(int id, Hook callback, nint module, uint thread);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nint message, nint pointer);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll")] private static extern nint OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseDesktop(nint desktop);
    [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetUserObjectInformation(nint handle, int index, StringBuilder information, int length, out int needed);
    // Prefix declarations preserve the official SDK vtable order. Unused slots
    // are never invoked; no control text/name/value API is exposed here.
    [ComImport, Guid("30CBE57D-D9D0-452A-AB13-7AC5AC4825EE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] private interface IAutomation
    {
        void CompareElements(); void CompareRuntimeIds(); void GetRootElement(); void ElementFromHandle(); void ElementFromPoint();
        void GetFocusedElement([MarshalAs(UnmanagedType.Interface)] out IElement element);
    }
    [ComImport, Guid("D22108AA-8AC5-49A5-837B-37BBB3D7591E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)] private interface IElement
    {
        void SetFocus(); void GetRuntimeId(); void FindFirst(); void FindAll(); void FindFirstBuildCache(); void FindAllBuildCache(); void BuildUpdatedCache();
        void GetCurrentPropertyValue(int propertyId, [MarshalAs(UnmanagedType.Struct)] out object value);
        void GetCurrentPropertyValueEx(int propertyId, [MarshalAs(UnmanagedType.Bool)] bool ignoreDefaultValue, [MarshalAs(UnmanagedType.Struct)] out object value);
    }
}
