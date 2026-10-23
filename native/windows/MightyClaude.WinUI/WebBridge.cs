using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MightyClaude.Core;
using Microsoft.UI.Dispatching;
using Microsoft.Web.WebView2.Core;

namespace MightyClaude.WinUI;

/// <summary>
/// The web shell's message protocol (native/windows/web/src/bridge/protocol.ts), JSON with <see cref="Wire.Json"/> over
/// <c>chrome.webview.postMessage</c> and <see cref="CoreWebView2.PostWebMessageAsJson"/>. The page calls
/// <c>{kind:"call", id, method, args}</c> and is answered <c>{kind:"result", id, ok, value?, error?}</c>; the host pushes the
/// whole state once on <c>ready</c> (<c>{kind:"snapshot"}</c>), then <c>{kind:"delta"}</c> with only the panes whose record was
/// replaced, every run event (<c>{kind:"event"}</c>) and every tool-permission change (<c>{kind:"permission"}</c>).
/// Records are replaced, never edited (DesktopService.Snapshot), so a pane is "changed" exactly when its object is not the one
/// sent last: a streamed line costs the one pane that streams, never the whole state.
/// </summary>
internal sealed class WebBridge : IDisposable
{
    /// <summary>A method the page may call: its <c>args</c> (undefined when it sent none) in, a value to serialize back out.</summary>
    internal delegate Task<object?> Handler(JsonElement args);

    private readonly Dictionary<string, Handler> handlers = new(StringComparer.Ordinal);
    private readonly DesktopService service;
    private readonly DispatcherQueue queue;
    /// <summary>Coalesces deltas: the first change starts it and every change until it fires rides along, about one frame.</summary>
    private readonly DispatcherQueueTimer flush;
    /// <summary>
    /// The service raises no event for an <see cref="DesktopService.UpdateAsync"/> made elsewhere (the phone, delegation, a
    /// setting), so the snapshot's reference is looked at a few times a second as well; an unchanged state costs one compare.
    /// </summary>
    private readonly DispatcherQueueTimer poll;
    private CoreWebView2? core;
    private bool ready, disposed;
    private AppSnapshot? sent;
    private readonly Dictionary<string, RunSession> sentSessions = new(StringComparer.Ordinal);
    private List<string> sentOrder = [];
    private string? sentState;

    internal WebBridge(DesktopService service, DispatcherQueue queue)
    {
        this.service = service; this.queue = queue;
        flush = queue.CreateTimer(); flush.Interval = TimeSpan.FromMilliseconds(16); flush.IsRepeating = false; flush.Tick += (_, _) => Flush();
        poll = queue.CreateTimer(); poll.Interval = TimeSpan.FromMilliseconds(100); poll.IsRepeating = true; poll.Tick += (_, _) => { if (!ReferenceEquals(service.Snapshot, sent)) Schedule(); };
        service.RunEventReceived += ReceiveRunEvent; service.ToolPermissionChanged += ReceivePermission;
        Register("ready", _ => { SendSnapshot(); return Task.FromResult<object?>(null); });
    }

    /// <summary>Whether the page asked for the state and has been sent it: the smoke waits for this.</summary>
    internal bool Ready => ready;

    /// <summary>Adds (or replaces) a method; each phase's methods are registered by the window that owns what they change.</summary>
    internal void Register(string method, Handler handler) => handlers[method] = handler;

    internal void Attach(CoreWebView2 view)
    {
        core = view;
        view.WebMessageReceived += Receive;
        // A page loaded again holds nothing: it asks for the whole state again with its own ready.
        view.ContentLoading += (_, _) => { ready = false; poll.Stop(); };
    }

    /// <summary>Runs on whatever thread the service raised it, possibly under its lock: only handed on.</summary>
    private void ReceiveRunEvent(RunEvent value) => queue.TryEnqueue(() => { if (!ready) return; Post(JsonSerializer.Serialize(new { kind = "event", @event = value }, Wire.Json)); Schedule(); });

    private void ReceivePermission(ToolPermissionRequest value) => queue.TryEnqueue(() => { if (ready) Post(JsonSerializer.Serialize(new { kind = "permission", request = value }, Wire.Json)); });

    private async void Receive(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (!WebShell.Trusted(args.Source)) return;
        long id = 0;
        try
        {
            string method; JsonElement callArgs;
            using (var json = JsonDocument.Parse(args.WebMessageAsJson))
            {
                var root = json.RootElement;
                if (root.Text("kind") != "call") return;
                id = root.TryGetProperty("id", out var number) && number.TryGetInt64(out var parsed) ? parsed : 0;
                method = root.Text("method") ?? "";
                callArgs = root.TryGetProperty("args", out var given) ? given.Clone() : default;
            }
            if (!handlers.TryGetValue(method, out var handler)) { Reply(id, false, null, "unknown method: " + method); return; }
            Reply(id, true, await handler(callArgs), null);
        }
        catch (JsonException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Reply(id, false, null, ex.Message); }
        // A call usually changes the state: the page sees it within a frame instead of at the next poll.
        Schedule();
    }

    private void Reply(long id, bool ok, object? value, string? error) => Post(JsonSerializer.Serialize(new { kind = "result", id, ok, value, error }, Wire.Json));

    private void SendSnapshot()
    {
        var current = service.Snapshot;
        using (PerfLog.Measure("web snapshot")) Post(JsonSerializer.Serialize(new { kind = "snapshot", snapshot = current }, Wire.Json));
        Remember(current, current.Sessions.Select(s => s.Id).ToList(), StateJson(current));
        ready = true; poll.Start();
    }

    private void Schedule() { if (ready && !disposed && !flush.IsRunning) flush.Start(); }

    /// <summary>Sends what changed since the last snapshot or delta: replaced panes, removed ids, the order when it moved, the rest of the state when it changed.</summary>
    private void Flush()
    {
        if (!ready || disposed) return;
        var current = service.Snapshot;
        if (ReferenceEquals(current, sent)) return;
        using var perf = PerfLog.Measure("web delta");
        var changed = new List<RunSession>(); var order = new List<string>(current.Sessions.Count);
        foreach (var session in current.Sessions)
        {
            order.Add(session.Id);
            if (!sentSessions.TryGetValue(session.Id, out var previous) || !ReferenceEquals(previous, session)) changed.Add(session);
        }
        var present = order.ToHashSet(StringComparer.Ordinal);
        var removed = sentSessions.Keys.Where(id => !present.Contains(id)).ToList();
        var state = StateJson(current);
        var stateChanged = state != sentState; var orderChanged = !order.SequenceEqual(sentOrder);
        Remember(current, order, state);
        if (changed.Count == 0 && removed.Count == 0 && !stateChanged && !orderChanged) return;
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject(); writer.WriteString("kind", "delta");
            writer.WritePropertyName("sessions"); JsonSerializer.Serialize(writer, changed, Wire.Json);
            writer.WritePropertyName("removed"); JsonSerializer.Serialize(writer, removed, Wire.Json);
            if (orderChanged) { writer.WritePropertyName("order"); JsonSerializer.Serialize(writer, order, Wire.Json); }
            if (stateChanged) { writer.WritePropertyName("state"); writer.WriteRawValue(state, skipInputValidation: true); }
            writer.WriteEndObject();
        }
        Post(Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length));
    }

    private void Remember(AppSnapshot current, List<string> order, string state)
    {
        sent = current; sentOrder = order; sentState = state; sentSessions.Clear();
        foreach (var session in current.Sessions) sentSessions[session.Id] = session;
    }

    /// <summary>Every field of the state but the panes, as the page's <c>AppState</c>; compared as text to tell whether it changed.</summary>
    private static string StateJson(AppSnapshot current)
    {
        var node = JsonSerializer.SerializeToNode(current with { Sessions = [] }, Wire.Json)!.AsObject();
        node.Remove("sessions");
        return node.ToJsonString(Wire.Json);
    }

    private void Post(string json)
    {
        if (core is null || disposed) return;
        try { core.PostWebMessageAsJson(json); }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException) { }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true; ready = false; flush.Stop(); poll.Stop();
        service.RunEventReceived -= ReceiveRunEvent; service.ToolPermissionChanged -= ReceivePermission;
        if (core is not null) core.WebMessageReceived -= Receive;
    }
}
