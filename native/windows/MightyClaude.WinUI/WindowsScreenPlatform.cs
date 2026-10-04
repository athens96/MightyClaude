using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using MightyClaude.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Windows.ApplicationModel.DataTransfer;

namespace MightyClaude.WinUI;

/// The WebView uses only a bundled page in an isolated profile. The mobile
/// relay carries authenticated signaling; WebRTC DTLS-SRTP carries the video
/// and SCTP input channel. No screenshot, SDP or clipboard payload touches disk.
internal sealed class WindowsScreenPlatform : IScreenSharePlatform, IAsyncDisposable
{
    private const string Origin = "https://screen.mightyclaude.invalid/";
    private readonly DispatcherQueue dispatcher;
    private readonly WebView2 browser = new() { Width = 1, Height = 1, Opacity = 0, IsHitTestVisible = false, IsTabStop = false, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    private readonly WindowsScreenCapture capture = new();
    private readonly WindowsRemoteInput input = new();
    private readonly ScreenShareTapMarkerOverlay markers;
    private readonly ConcurrentDictionary<long, TaskCompletionSource> commands = [];
    private readonly Dictionary<string, ScreenLiveSession> peers = [];
    private readonly object sync = new();
    private readonly SemaphoreSlim lifecycle = new(1), inputQueue = new(1);
    private int pendingInputs;
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource lifetime = new();
    private ScreenRegion region = new();
    private int display;
    private CancellationTokenSource? captureCancellation;
    private bool captureRunning;
    private long sequence, generation;
    private bool initialized, disposed;
    internal ScreenShareHub? Hub { get; set; }
    internal event Action? KillRequested;
    public bool Available => initialized && !disposed && WindowsScreenCapture.Supported && input.Available;
    public IReadOnlyList<ScreenDisplay> Displays => capture.Displays;
    public ScreenSafety Safety => input.Safety;
    internal WindowsScreenPlatform(DispatcherQueue dispatcher, Panel host)
    { this.dispatcher = dispatcher; markers = new(dispatcher); host.Children.Add(browser); capture.DisplayLost += () => _ = StopFor("display-gone"); input.KillRequested += () => { Halt(); KillRequested?.Invoke(); }; }
    internal async Task InitializeAsync(string directory)
    {
        var environment = await CoreWebView2Environment.CreateWithOptionsAsync("", Path.Combine(directory, "screen-webview"), new());
        await browser.EnsureCoreWebView2Async(environment); if (disposed) return;
        var core = browser.CoreWebView2; var settings = core.Settings;
        settings.AreHostObjectsAllowed = false; settings.AreDevToolsEnabled = false; settings.AreDefaultContextMenusEnabled = false; settings.AreBrowserAcceleratorKeysEnabled = false; settings.AreDefaultScriptDialogsEnabled = false; settings.IsPasswordAutosaveEnabled = false; settings.IsGeneralAutofillEnabled = false; settings.IsStatusBarEnabled = false;
        core.SetVirtualHostNameToFolderMapping("screen.mightyclaude.invalid", Path.Combine(AppContext.BaseDirectory, "Assets", "ScreenShare"), CoreWebView2HostResourceAccessKind.Deny);
        core.NavigationStarting += (_, e) => e.Cancel = e.Uri != Origin + "index.html";
        core.FrameNavigationStarting += (_, e) => e.Cancel = true;
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.DownloadStarting += (_, e) => e.Cancel = true;
        core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
        core.WebResourceRequested += (_, e) => { if (e.Request.Method != "GET" || e.Request.Uri is not (Origin + "index.html" or Origin + "screen.js")) e.Response = environment.CreateWebResourceResponse(null, 403, "Forbidden", ""); };
        core.ProcessFailed += (_, _) => { initialized = false; Halt(); _ = StopFor("peer-left"); };
        core.WebMessageReceived += Message;
        core.Navigate(Origin + "index.html"); await ready.Task.WaitAsync(TimeSpan.FromSeconds(20)); initialized = true;
    }
    private async void Message(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        try
        {
            if (disposed || args.Source != Origin + "index.html" || Encoding.UTF8.GetByteCount(args.WebMessageAsJson) > 131072) return;
            using var doc = JsonDocument.Parse(args.WebMessageAsJson); var value = doc.RootElement;
            if (value.Text("type") == "ready") { if (value.TryGetProperty("h264", out var h264) && h264.ValueKind == JsonValueKind.True) ready.TrySetResult(); else ready.TrySetException(new NotSupportedException("H264 encoding is unavailable.")); return; }
            if (value.Text("type") == "ack" && value.TryGetProperty("id", out var id) && id.TryGetInt64(out var number) && commands.TryRemove(number, out var pending))
            { if (value.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True) pending.TrySetResult(); else pending.TrySetException(new IOException("The screen transport operation failed.")); return; }
            var session = value.Text("sessionId"); lock (sync) if (session is null || !peers.ContainsKey(session)) return;
            if (Hub is null) return;
            if (value.Text("type") == "signal" && value.TryGetProperty("signal", out var signal) && signal.Text("sessionId") == session) await Hub.PeerSignal(session, signal.Clone());
            else if (value.Text("type") == "data" && value.Text("data") is { } data) await Hub.InputAsync(session, data);
            else if (value.Text("type") == "state") { if (value.Text("state") == "connected") Hub.PeerConnected(session); else if (value.Text("state") is "closed" or "failed") await Hub.PeerFailedAsync(session); }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }
    private Task OnUI(Func<Task> action, CancellationToken token = default)
    {
        if (dispatcher.HasThreadAccess) { token.ThrowIfCancellationRequested(); return action(); }
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var registration = token.Register(() => complete.TrySetCanceled(token));
        if (!dispatcher.TryEnqueue(async () => { try { token.ThrowIfCancellationRequested(); await action(); complete.TrySetResult(); } catch (OperationCanceledException) { complete.TrySetCanceled(); } catch (Exception ex) { complete.TrySetException(ex); } finally { registration.Dispose(); } })) { registration.Dispose(); complete.TrySetCanceled(); }
        return complete.Task;
    }
    private async Task Command(object message, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested(); ObjectDisposedException.ThrowIf(disposed, this);
        var id = Interlocked.Increment(ref sequence); var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); commands[id] = completion;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        try
        {
            var value = JsonSerializer.SerializeToNode(message, Wire.Json)!.AsObject(); value["id"] = id;
            await OnUI(() => { if (disposed) throw new OperationCanceledException(); browser.CoreWebView2.PostWebMessageAsJson(value.ToJsonString(Wire.Json)); return Task.CompletedTask; }, linked.Token);
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(8), linked.Token);
        }
        catch (TimeoutException) { throw new IOException("Screen transport did not respond."); }
        finally { commands.TryRemove(id, out _); }
    }
    public async Task StartPeerAsync(ScreenLiveSession session, IReadOnlyList<ScreenIceServer> ice, CancellationToken token)
    {
        var ticket = Interlocked.Read(ref generation);
        await lifecycle.WaitAsync(token);
        try
        {
            if (ticket != Interlocked.Read(ref generation)) throw new OperationCanceledException();
            if (!Available || Safety.Locked || Safety.SecureInput) throw new IOException("Screen capture is unavailable.");
            lock (sync) { peers.Add(session.SessionId, session); display = session.DisplayId; }
            try { await Command(new { type = "start", session, iceServers = ice }, token); token.ThrowIfCancellationRequested(); if (ticket != Interlocked.Read(ref generation)) throw new OperationCanceledException(); if (captureRunning) capture.Configure(Quality, region); else await StartCapture(token, ticket); }
            catch { lock (sync) peers.Remove(session.SessionId); await Command(new { type = "stop", sessionId = session.SessionId }, CancellationToken.None); throw; }
        }
        finally { lifecycle.Release(); }
    }
    private ScreenQuality Quality { get { lock (sync) { var values = peers.Values.Select(p => p.Quality).ToArray(); return values.Length == 0 ? ScreenQuality.For(false) : new(values.Max(q => q.Width), values.Max(q => q.Height), values.Max(q => q.Fps), values.Max(q => q.MaxBitrateKbps)); } } }
    private async Task StartCapture(CancellationToken token, long ticket)
    {
        CancellationTokenSource next; CancellationTokenSource? prior;
        lock (sync)
        {
            token.ThrowIfCancellationRequested();
            if (ticket != Interlocked.Read(ref generation) || peers.Count == 0 || disposed) throw new OperationCanceledException();
            next = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); prior = captureCancellation; captureCancellation = next; captureRunning = false;
        }
        // Only startup belongs to the requesting phone. After successful start,
        // detaching this registration leaves capture shared by the live peers.
        using var requestedCancellation = token.Register(next.Cancel);
        try
        {
            await capture.StopAsync(); prior?.Dispose(); next.Token.ThrowIfCancellationRequested();
            if (ticket != Interlocked.Read(ref generation)) throw new OperationCanceledException();
            await capture.StartAsync(display, Quality, region, Deliver, next.Token);
            next.Token.ThrowIfCancellationRequested();
            lock (sync) { if (ticket != Interlocked.Read(ref generation) || peers.Count == 0 || disposed) throw new OperationCanceledException(); captureRunning = true; }
        }
        catch
        {
            capture.Halt(); await capture.StopAsync();
            lock (sync) { if (ReferenceEquals(captureCancellation, next)) captureCancellation = null; captureRunning = false; }
            next.Dispose(); throw;
        }
    }
    private async Task Deliver(ScreenFrame frame)
    {
        var ticket = Interlocked.Read(ref generation); var safety = Safety;
        if (disposed || safety.Locked || safety.SecureInput) { Halt(); _ = StopFor(safety.Locked ? "lock-screen" : "secure-input"); return; }
        try { await OnUI(async () => { if (ticket != Interlocked.Read(ref generation) || disposed) return; var current = Safety; if (current.Locked || current.SecureInput) return; await Command(new { type = "frame", frame }, lifetime.Token); }, lifetime.Token); }
        catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
    }
    public Task ReceiveSignalAsync(string sessionId, JsonElement signal, CancellationToken token) => Command(new { type = "signal", sessionId, signal }, token);
    public async Task StopPeerAsync(string sessionId)
    {
        await lifecycle.WaitAsync();
        try
        {
            bool empty; lock (sync) { peers.Remove(sessionId); empty = peers.Count == 0; } await ReleaseButtonsAsync(sessionId);
            if (empty) { capture.Halt(); Interlocked.Increment(ref generation); captureRunning = false; }
            if (!disposed && initialized) await Command(new { type = "stop", sessionId });
            if (empty) await capture.StopAsync(); else capture.Configure(Quality, region);
        }
        finally { lifecycle.Release(); }
    }
    public void Halt()
    {
        Interlocked.Increment(ref generation); lock (sync) { captureCancellation?.Cancel(); captureRunning = false; } capture.Halt(); _ = ReleaseAllInputs(); markers.Clear();
        _ = OnUI(() => { if (!disposed && browser.CoreWebView2 is { } core) core.PostWebMessageAsJson("{\"type\":\"halt\",\"id\":0}"); return Task.CompletedTask; });
    }
    private async Task StopFor(string reason) { Halt(); if (Hub is { } hub) try { await hub.KillAllAsync(reason); } catch (OperationCanceledException) { } }
    public async Task SetDisplayAsync(int displayId, CancellationToken token)
    {
        var ticket = Interlocked.Read(ref generation);
        if (!Displays.Any(d => d.DisplayId == displayId)) return;
        await lifecycle.WaitAsync(token);
        try { if (ticket != Interlocked.Read(ref generation)) throw new OperationCanceledException(); display = displayId; region = new(); lock (sync) foreach (var id in peers.Keys.ToArray()) peers[id] = peers[id] with { DisplayId = displayId }; await Command(new { type = "display", displayId }, token); await StartCapture(token, ticket); }
        finally { lifecycle.Release(); }
    }
    public void SetRegion(ScreenRegion value) { region = value; capture.Configure(Quality, region); }
    public async Task InjectAsync(string sessionId, JsonElement value, CancellationToken token)
    {
        if (Interlocked.Increment(ref pendingInputs) > 128) { Interlocked.Decrement(ref pendingInputs); return; }
        var ticket = Interlocked.Read(ref generation);
        try
        {
            // Data-channel messages arrive in order. Serialize their workers so
            // committed text/shortcuts cannot reorder while keeping the UI pump
            // free to receive the physical kill switch and local input hooks.
            await inputQueue.WaitAsync(token);
            try { var displays = Displays; await Task.Run(() => input.Inject(sessionId, value, displays, token, () => ticket == Interlocked.Read(ref generation) && Hub?.InputPaused != true), token); }
            finally { inputQueue.Release(); }
        }
        finally { Interlocked.Decrement(ref pendingInputs); }
    }
    public async Task<bool> ShowMarkerAsync(string sessionId, JsonElement value, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); var safety = Safety; if (safety.Locked || safety.SecureInput) return false;
        var display = Displays.FirstOrDefault(d => d.DisplayId == value.GetProperty("displayId").GetInt32());
        return display is not null && await markers.ShowAsync(display, value.GetProperty("x").GetDouble(), value.GetProperty("y").GetDouble(), value.Text("marker")!);
    }
    public async Task ReleaseButtonsAsync(string sessionId)
    { await inputQueue.WaitAsync(); try { await Task.Run(() => input.Release(sessionId)); } finally { inputQueue.Release(); } }
    private async Task ReleaseAllInputs()
    { await inputQueue.WaitAsync(); try { await Task.Run(input.ReleaseAll); } finally { inputQueue.Release(); } }
    public async Task<string?> ReadClipboardAsync(CancellationToken token)
    {
        string? text = null;
        await OnUI(async () =>
        {
            var content = Clipboard.GetContent();
            // Password managers commonly advertise one of these exclusion/
            // concealed formats. The host never attempts to fetch that value.
            if (content.AvailableFormats.Any(f => f.Contains("concealed", StringComparison.OrdinalIgnoreCase) || f.Contains("password", StringComparison.OrdinalIgnoreCase) || f.Equals("ExcludeClipboardContentFromMonitorProcessing", StringComparison.OrdinalIgnoreCase))) return;
            if (!content.Contains(StandardDataFormats.Text)) { text = ""; return; }
            var result = await content.GetTextAsync(); token.ThrowIfCancellationRequested(); if (Encoding.UTF8.GetByteCount(result) <= ScreenClipboard.MaximumBytes) text = result;
        }, token); return text;
    }
    public Task WriteClipboardAsync(string text, CancellationToken token) => OnUI(() => { var safety = Safety; if (Hub?.InputPaused == true || Encoding.UTF8.GetByteCount(text) > ScreenClipboard.MaximumBytes || safety.Locked || safety.SecureInput || DateTimeOffset.UtcNow - safety.LastLocalInput < TimeSpan.FromSeconds(2)) return Task.CompletedTask; var content = new DataPackage(); content.SetText(text); Clipboard.SetContent(content); return Task.CompletedTask; }, token);
    public Task SendDataAsync(string sessionId, object message, CancellationToken token) => Command(new { type = "data", sessionId, message }, token);
    public async ValueTask DisposeAsync()
    {
        if (disposed) return; Halt(); lifetime.Cancel(); await capture.DisposeAsync(); await inputQueue.WaitAsync(); try { await Task.Run(input.Dispose); } finally { inputQueue.Release(); } markers.Dispose(); disposed = true;
        foreach (var completion in commands.Values) completion.TrySetCanceled(); commands.Clear();
        await OnUI(() => { browser.Close(); if (browser.Parent is Panel host) host.Children.Remove(browser); return Task.CompletedTask; }); lifetime.Dispose();
    }
}
