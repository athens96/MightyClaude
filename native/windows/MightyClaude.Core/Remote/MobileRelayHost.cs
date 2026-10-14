using System.Collections.Concurrent;
using System.Net.NetworkInformation;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MightyClaude.Core;

public sealed record MobileRemoteSettings(bool Enabled = false, string RelayURL = "", bool AllowLegacyPhones = true)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public MobileRemoteSettings Normalized
    {
        get { var relay = (RelayURL ?? "").Trim(); return relay.Length == 0 ? this with { RelayURL = "" } : MobileRelayHost.NormalizeRelay(relay) is { } valid ? this with { RelayURL = valid } : new(false, "", AllowLegacyPhones); }
    }
}
public sealed record MobileReply(int Status, object Body, Func<Task>? AfterSend = null);
/// <param name="Detail"><c>disabled</c>, <c>connecting</c>, <c>connected</c>, <c>reconnecting</c> or <c>offline</c>.</param>
/// <param name="Error">Why the last control socket ended, while reconnecting: <c>rejected</c>, <c>host-id-taken</c>, <c>closed</c>, <c>unresponsive</c> or <c>connect-failed</c>.</param>
/// <param name="RetrySeconds">How long the host waits before it dials again, while reconnecting.</param>
public sealed record MobileRelayStatus(bool Connected, int Connections, string Detail, string? Error = null, int? RetrySeconds = null);

/// <summary>The line under the relay's state in Settings, as the Mac words it (M/MightyCore/Remote/MobileRemoteService.swift:544-549, 659-669).</summary>
public static class MobileRelayStatusText
{
    public static string? Reason(string? error) => error switch
    {
        "rejected" => Locale.Get("mobileRemote.error.relayRejected"), "host-id-taken" => Locale.Get("mobileRemote.error.hostIdTaken"), "closed" => Locale.Get("mobileRemote.error.relayClosed"),
        "unresponsive" => Locale.Get("settings.mobileRemote.detail.relayUnresponsive"), "connect-failed" => Locale.Get("mobileRemote.error.relayConnectFailed"), _ => null,
    };
    public static string Detail(MobileRelayStatus status) => status.Detail switch
    {
        "disabled" => Locale.Get("mobileRemote.status.off"),
        "connected" => Locale.Get("mobileRemote.status.scanQR"),
        "offline" => Locale.Get("settings.mobileRemote.detail.networkOffline"),
        "reconnecting" => (Reason(status.Error) is { } reason ? reason + " · " : Locale.Get("mobileRemote.status.disconnected") + " ") + Locale.Get("mobileRemote.status.retryIn", new Dictionary<string, string> { ["seconds"] = (status.RetrySeconds ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture) }),
        _ => Locale.Get("mobileRemote.status.connecting"),
    };
}

/// Outbound-only relay host. Every phone must complete the pinned X25519
/// handshake and encrypted device authentication before any m1 request runs.
public sealed class MobileRelayHost : IAsyncDisposable
{
    public const string DefaultRelay = "wss://mightyclaude.duckdns.org";
    private const int MaximumFrame = 1024 * 1024;
    private readonly MobileIdentity identity;
    private readonly string hostName;
    private readonly string[] capabilities;
    private readonly Func<string, string, JsonElement?, string, CancellationToken, Task<MobileReply>> route;
    private readonly SemaphoreSlim lifecycle = new(1);
    private readonly ConcurrentDictionary<string, Connection> clients = [];
    private CancellationTokenSource? lifetime;
    private Task? loop;
    private MobileRemoteSettings settings = new();
    private int generation;
    private bool disposed;
    private long turnRenewTicks;
    private readonly object pathSync = new();
    private RelayNetworkPath? networkPath;
    private RelayLinkPolicy.Window pathWindow = new();
    private RelayLinkPolicy.Pacing pathPacing = new();
    private CancellationTokenSource? pathSettle;
    private bool watching, subscribed;
    /// <summary>Whether Windows' own network reports are followed. Off, only <see cref="NetworkPathChanged"/> and <see cref="ReconnectSoon"/> move the link (the checks).</summary>
    public bool WatchesNetwork { get; init; } = true;
    /// <summary>How often the control socket is pinged and how long a ping may go unanswered before the socket is closed and dialled again.</summary>
    public TimeSpan KeepaliveInterval { get; init; } = RelayLinkPolicy.KeepaliveInterval;
    public TimeSpan KeepaliveTimeout { get; init; } = RelayLinkPolicy.KeepaliveTimeout;
    /// <summary>How long path reports must stay quiet before the link acts on them.</summary>
    public TimeSpan PathSettle { get; init; } = RelayLinkPolicy.PathSettle;
    public Func<string[]>? ExtraCapabilities { get; set; }
    public bool IsTrustedDevice(string device) => identity.Contains(device, false);
    public Func<string, JsonElement, CancellationToken, Task>? ScreenSignal { get; set; }
    public Func<string, Task>? ScreenDisconnected { get; set; }
    public Func<IReadOnlyList<ScreenIceServer>, Task>? TurnCredentials { get; set; }
    public event Action<MobileRelayStatus>? StatusChanged;
    public MobileRelayStatus Status { get; private set; } = new(false, 0, "disabled");
    public IReadOnlyList<MobileDeviceInfo> Devices { get { var connected = clients.Values.Where(c => c.Authenticated).Select(c => c.DeviceId).ToHashSet(); return identity.Devices.Select(d => d with { Connected = connected.Contains(d.Id) }).ToArray(); } }
    public string HostId => identity.HostId;
    public string PairingServerId => identity.ServerId;
    public string PairingKeyForDisplay => identity.PairingKey;
    public string AppVersion { get; init; } = "0.0.0";
    public string PairingUrl => identity.PairingUrl(NormalizeRelay(settings.RelayURL) ?? DefaultRelay, hostName);
    public MobileRelayHost(string directory, string hostName, string[] capabilities, Func<string, string, JsonElement?, string, CancellationToken, Task<MobileReply>> route)
    { identity = new(directory); this.hostName = hostName; this.capabilities = capabilities; this.route = route; }
    public static string? NormalizeRelay(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (value.Length > 256 || value.Any(char.IsWhiteSpace)) return null;
        var input = value.Contains("://", StringComparison.Ordinal) ? value : "wss://" + value;
        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri) || uri.Scheme is not ("ws" or "wss" or "http" or "https") || uri.Host.Length == 0 || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0) return null;
        var builder = new UriBuilder(uri) { Scheme = uri.Scheme is "http" or "ws" ? "ws" : "wss", Path = "", Query = "", Fragment = "" };
        return builder.Uri.GetLeftPart(UriPartial.Authority);
    }
    private Uri SocketUri(string relay, string? connection = null)
    {
        var query = "serverId=" + Uri.EscapeDataString(identity.ServerId) + "&role=server&v=1&hostToken=" + Uri.EscapeDataString(identity.HostToken);
        if (connection is not null) query += "&connectionId=" + Uri.EscapeDataString(connection);
        return new Uri(relay + "/ws?" + query);
    }
    /// <summary>
    /// A socket that pings by itself and is aborted when a ping goes unanswered: the receive loop alone never notices a socket
    /// the network left half-open, while the relay has long dropped this host (M/MightyCore/Remote/MobileRemoteService.swift:582-596).
    /// </summary>
    private ClientWebSocket Socket()
    {
        var socket = new ClientWebSocket(); socket.Options.KeepAliveInterval = KeepaliveInterval; socket.Options.KeepAliveTimeout = KeepaliveTimeout; return socket;
    }
    public async Task ApplyAsync(MobileRemoteSettings value)
    {
        await lifecycle.WaitAsync();
        try { ObjectDisposedException.ThrowIf(disposed, this); await RestartInternal(value.Normalized); }
        finally { lifecycle.Release(); }
    }
    public async Task ReconnectAsync()
    {
        await lifecycle.WaitAsync();
        try { ObjectDisposedException.ThrowIf(disposed, this); await RestartInternal(settings); }
        finally { lifecycle.Release(); }
    }
    private async Task RestartInternal(MobileRemoteSettings value)
    {
        await StopInternal(); settings = value;
        if (!settings.Enabled) { StopWatching(); Publish(false, "disabled"); return; }
        var relay = NormalizeRelay(settings.RelayURL.Length == 0 ? DefaultRelay : settings.RelayURL) ?? throw new ArgumentException("Invalid relay address.");
        lifetime = new(); var current = ++generation; Publish(false, "connecting"); loop = RunAsync(relay, current, lifetime.Token);
        StartWatching();
    }

    // The network path (M/MightyCore/Remote/MobileRemoteService.swift:474-549): watched while the remote is on. A change of
    // network may leave the control socket half-open, so once the reports settle the host dials again; with no network at
    // all it stops dialling and says so, and dials the moment one returns.
    private void StartWatching()
    {
        lock (pathSync) { if (watching) return; watching = true; networkPath = null; pathWindow = new(); pathPacing = new(); if (!WatchesNetwork) return; subscribed = true; }
        NetworkChange.NetworkAddressChanged += AddressChanged; NetworkChange.NetworkAvailabilityChanged += AvailabilityChanged;
        // Reading the adapters takes a while; the remote is often switched on from Settings, on the window's thread.
        _ = Task.Run(Read);
    }
    /// <summary>Reports the path as Windows has it now; one that could not be read is no report at all.</summary>
    private void Read() { if (RelayNetworkPath.Current() is { } path) NetworkPathChanged(path); }
    private void StopWatching()
    {
        bool was;
        lock (pathSync) { watching = false; was = subscribed; subscribed = false; pathSettle?.Cancel(); pathSettle = null; networkPath = null; pathWindow = new(); pathPacing = new(); }
        if (was) { NetworkChange.NetworkAddressChanged -= AddressChanged; NetworkChange.NetworkAvailabilityChanged -= AvailabilityChanged; }
    }
    private void AddressChanged(object? sender, EventArgs e) => Read();
    private void AvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) => Read();
    /// <summary>A path report from Windows or a check. Acted on once reports stop arriving for <see cref="PathSettle"/>.</summary>
    public void NetworkPathChanged(RelayNetworkPath path)
    {
        lock (pathSync) { if (!watching) return; pathWindow.Report(path, networkPath, DateTimeOffset.UtcNow); ScheduleSettle(null); }
    }
    /// <summary>
    /// Dials again once the path settles, whatever it then is (wake from sleep): asleep, the PC missed the relay's pings and
    /// its control socket may be gone on the relay's side while it still looks open here. Never paced, and it still waits for a network.
    /// </summary>
    public void ReconnectSoon()
    {
        lock (pathSync) { if (!watching) return; pathWindow.Force(DateTimeOffset.UtcNow); ScheduleSettle(null); }
    }
    private void ScheduleSettle(TimeSpan? after)
    {
        pathSettle?.Cancel(); var cancel = pathSettle = new();
        var delay = after ?? (pathWindow.Delay(DateTimeOffset.UtcNow) is var wait && wait < PathSettle ? wait : PathSettle);
        // Started off this caller, which holds pathSync: with no delay left the decision (and a redial) would otherwise run under it.
        _ = Task.Run(() => Settle(delay, cancel.Token));
    }
    private async Task Settle(TimeSpan delay, CancellationToken token)
    {
        try { await Task.Delay(delay, token); } catch (OperationCanceledException) { return; }
        RelayLinkPolicy.PathAction action;
        lock (pathSync)
        {
            if (!watching || token.IsCancellationRequested || pathWindow.IsEmpty) return;
            var window = pathWindow; action = RelayLinkPolicy.Action(networkPath, window);
            // A flapping link must not redial on every flap. The window stays open meanwhile, so later reports still count.
            if (action == RelayLinkPolicy.PathAction.Reconnect && !window.Forced && pathPacing.Wait(DateTimeOffset.UtcNow) is var pause && pause > TimeSpan.Zero) { ScheduleSettle(pause); return; }
            pathWindow = new(); if (window.Latest is { } latest) networkPath = latest;
            if (action == RelayLinkPolicy.PathAction.Reconnect && !window.Forced) pathPacing.Record(DateTimeOffset.UtcNow);
        }
        if (action == RelayLinkPolicy.PathAction.None) return;
        await lifecycle.WaitAsync();
        try
        {
            if (disposed || !settings.Enabled) return;
            if (action == RelayLinkPolicy.PathAction.Offline) { await StopInternal(); Publish(false, "offline"); }
            else await RestartInternal(settings);
        }
        catch (ArgumentException) { }
        finally { lifecycle.Release(); }
    }
    public async Task RotateKeyAsync(string? expectedDeviceId = null)
    {
        await lifecycle.WaitAsync();
        try { ObjectDisposedException.ThrowIf(disposed, this); if (expectedDeviceId is not null && !identity.Contains(expectedDeviceId, true)) return; identity.Rotate(); foreach (var client in clients.Values) client.Cancel(); }
        finally { lifecycle.Release(); }
        Republish();
    }
    private async Task StopInternal()
    {
        ++generation; lifetime?.Cancel(); foreach (var client in clients.Values) client.Cancel();
        if (loop is { } running) try { await running; } catch (OperationCanceledException) { }
        await Task.WhenAll(clients.Values.Select(c => c.Completion.Task));
        lifetime?.Dispose(); lifetime = null; loop = null;
    }
    private void Publish(bool connected, string detail, string? error = null, int? retry = null) { Status = new(connected, clients.Values.Count(c => c.Authenticated), detail, error, retry); StatusChanged?.Invoke(Status); }
    /// <summary>The same state again, with the phones counted anew.</summary>
    private void Republish() => Publish(Status.Connected, Status.Detail, Status.Error, Status.RetrySeconds);
    private async Task RunAsync(string relay, int current, CancellationToken token)
    {
        var delay = 1;
        while (!token.IsCancellationRequested && current == generation)
        {
            using var socket = Socket();
            using var renew = CancellationTokenSource.CreateLinkedTokenSource(token); Task? renewal = null; var connected = false;
            try
            {
                await socket.ConnectAsync(SocketUri(relay), token); connected = true; Interlocked.Exchange(ref turnRenewTicks, DateTimeOffset.UtcNow.AddMinutes(45).UtcTicks); delay = 1; Publish(true, "connected");
                await socket.SendAsync(Encoding.UTF8.GetBytes("{\"type\":\"turn-credentials-request\"}"), WebSocketMessageType.Text, true, token);
                renewal = RenewTurn(socket, renew.Token);
                while (!token.IsCancellationRequested)
                {
                    var (type, bytes) = await Receive(socket, token);
                    if (type != WebSocketMessageType.Text) continue;
                    using var doc = JsonDocument.Parse(bytes); var value = doc.RootElement;
                    if (value.Text("type") == "turn-credentials") { await ReadTurnCredentials(value); continue; }
                    var id = value.Text("connectionId");
                    if (id is null || !Regex.IsMatch(id, "^[A-Za-z0-9-]{8,64}$")) continue;
                    if (value.Text("type") == "disconnected") { if (clients.TryGetValue(id, out var prior)) prior.Cancel(); continue; }
                    if (value.Text("type") != "connected" || clients.Count >= 32 || clients.Values.Count(c => !c.Authenticated) >= 4) continue;
                    var connection = new Connection(this, id, SocketUri(relay, id), token);
                    if (clients.TryAdd(id, connection)) _ = Serve(connection);
                }
            }
            catch (Exception ex) when (ex is WebSocketException or IOException or OperationCanceledException or JsonException or InvalidOperationException) { }
            finally { renew.Cancel(); if (renewal is not null) await renewal; var ended = clients.Values.ToArray(); foreach (var client in ended) client.Cancel(); await Task.WhenAll(ended.Select(c => c.Completion.Task)); }
            if (token.IsCancellationRequested || current != generation) break;
            // The relay says why it closed (docs/relay.md); a socket that ended without a word had stopped answering.
            Publish(false, "reconnecting", socket.CloseStatus is { } closed ? (int)closed switch { 4400 => "rejected", 4409 => "host-id-taken", _ => "closed" } : connected ? "unresponsive" : "connect-failed", delay);
            try { await Task.Delay(TimeSpan.FromSeconds(delay), token); } catch (OperationCanceledException) { break; }
            delay = Math.Min(delay * 2, 30);
        }
        Publish(false, "disabled");
    }
    private async Task RenewTurn(ClientWebSocket socket, CancellationToken token)
    {
        try { while (!token.IsCancellationRequested) { await Task.Delay(TimeSpan.FromSeconds(5), token); if (DateTimeOffset.UtcNow.UtcTicks < Interlocked.Read(ref turnRenewTicks)) continue; Interlocked.Exchange(ref turnRenewTicks, DateTimeOffset.UtcNow.AddMinutes(1).UtcTicks); await socket.SendAsync(Encoding.UTF8.GetBytes("{\"type\":\"turn-credentials-request\"}"), WebSocketMessageType.Text, true, token); } }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException) { }
    }
    private async Task ReadTurnCredentials(JsonElement value)
    {
        if (TurnCredentials is null || value.Text("username") is not { Length: > 0 and <= 256 } user || value.Text("password") is not { Length: > 0 and <= 256 } password || !value.TryGetProperty("uris", out var list) || list.ValueKind != JsonValueKind.Array) return;
        var uris = list.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).Where(s => s.Length <= 512 && s is not null && (s.StartsWith("turn:", StringComparison.Ordinal) || s.StartsWith("turns:", StringComparison.Ordinal)) && !s.Any(char.IsWhiteSpace)).Take(8).ToArray();
        if (uris.Length == 0 || !value.TryGetProperty("ttl", out var lifetime) || !lifetime.TryGetInt32(out var seconds) || seconds is < 30 or > 86400) return;
        Interlocked.Exchange(ref turnRenewTicks, DateTimeOffset.UtcNow.AddSeconds(seconds * .75).UtcTicks);
        var stun = uris.Select(s => "stun:" + s[(s.IndexOf(':') + 1)..].Split('?')[0]).Distinct().ToArray();
        await TurnCredentials([new(stun), new(uris, user, password)]);
    }
    public Task SendToDeviceAsync(string device, object value) => Task.WhenAll(clients.Values.Where(c => c.Authenticated && c.DeviceId == device).Select(c => c.Send(value)));
    private async Task Serve(Connection connection)
    {
        try { await connection.Run(); }
        finally { clients.TryRemove(new KeyValuePair<string, Connection>(connection.Id, connection)); connection.Completion.TrySetResult(); if (ScreenDisconnected is not null && connection.Authenticated) { try { await ScreenDisconnected(connection.DeviceId); } catch { } } Republish(); }
    }
    public async Task NotifyAsync(string scope, long revision)
    {
        if (!(scope == "state" || scope.StartsWith("session:", StringComparison.Ordinal))) return;
        await Task.WhenAll(clients.Values.Where(c => c.Authenticated).Select(c => c.Send(new { type = "notify", scope, revision })));
    }
    /// <summary>The largest plaintext one frame to a phone may hold: the relay closes a connection on a frame over 1 MiB, and the seal takes 28 bytes of it.</summary>
    public const int MaximumPlaintext = MaximumFrame - 28;
    private static async Task<(WebSocketMessageType Type, byte[] Bytes)> Receive(ClientWebSocket socket, CancellationToken token)
    {
        using var data = new MemoryStream(); var buffer = new byte[8192]; WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, token);
            if (result.MessageType == WebSocketMessageType.Close) throw new IOException("Relay socket closed.");
            if (data.Length + result.Count > MaximumFrame) throw new IOException("Relay frame exceeded limit.");
            data.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return (result.MessageType, data.ToArray());
    }
    private sealed class Connection
    {
        public string Id { get; }
        public bool Authenticated { get; private set; }
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int cancelled;
        private Task? heartbeat;
        private readonly MobileRelayHost host;
        private readonly Uri uri;
        private readonly CancellationTokenSource cancel;
        private readonly ClientWebSocket socket;
        private readonly SemaphoreSlim writer = new(1), requests = new(8);
        private readonly ConcurrentDictionary<string, Task> inflight = [];
        private RelayCipher? cipher;
        private string device = "";
        public string DeviceId => device;
        public Connection(MobileRelayHost host, string id, Uri uri, CancellationToken token) { this.host = host; Id = id; this.uri = uri; socket = host.Socket(); cancel = CancellationTokenSource.CreateLinkedTokenSource(token); }
        public void Cancel() { if (Interlocked.Exchange(ref cancelled, 1) == 0) { cancel.Cancel(); socket.Abort(); } }
        public async Task Run()
        {
            var token = cancel.Token;
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(10));
                await socket.ConnectAsync(uri, deadline.Token);
                var hello = await Receive(socket, deadline.Token);
                if (hello.Type != WebSocketMessageType.Text || hello.Bytes.Length > 4096) return;
                using var hd = JsonDocument.Parse(hello.Bytes); var h = hd.RootElement;
                if (h.Text("type") != "hello" || !h.TryGetProperty("v", out var v) || !v.TryGetInt32(out var version) || version != 1) return;
                var nonce = RandomNumberGenerator.GetBytes(16);
                cipher = new(host.identity.Secret, Convert.FromBase64String(h.Text("clientKey") ?? ""), Convert.FromBase64String(h.Text("nonce") ?? ""), nonce, true);
                var ready = JsonSerializer.SerializeToUtf8Bytes(new { type = "ready", v = 1, serverKey = host.identity.PublicKey, nonce = Convert.ToBase64String(nonce) }, Wire.Json);
                await socket.SendAsync(ready, WebSocketMessageType.Text, true, deadline.Token);
                var authentication = await Receive(socket, deadline.Token);
                if (authentication.Type != WebSocketMessageType.Binary) return;
                using var ad = JsonDocument.Parse(cipher.Open(authentication.Bytes));
                var auth = host.identity.Authenticate(ad.RootElement, host.settings.AllowLegacyPhones);
                if (auth.DeviceId is null) { await Send(new { type = "auth_error", reason = auth.Error }); return; }
                device = auth.DeviceId; Authenticated = true;
                await Send(new { type = "auth_ok", hostName = host.hostName, hostId = host.identity.HostId, appVersion = host.AppVersion, capabilities = host.capabilities.Concat(host.ExtraCapabilities?.Invoke() ?? []).Distinct().ToArray(), deviceToken = auth.DeviceToken });
                host.Republish();
                heartbeat = Heartbeat(token);
                while (!token.IsCancellationRequested)
                {
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(token); idle.CancelAfter(TimeSpan.FromSeconds(60));
                    var frame = await Receive(socket, idle.Token);
                    if (frame.Type != WebSocketMessageType.Binary) continue;
                    using var doc = JsonDocument.Parse(cipher.Open(frame.Bytes)); var root = doc.RootElement;
                    if (root.Text("type") == "ping") { await Send(new { type = "pong" }); continue; }
                    if (root.Text("type") is { } signalType && signalType.StartsWith("screen-", StringComparison.Ordinal))
                    { if (host.ScreenSignal is not null && frame.Bytes.Length <= 65536 + 28 && host.identity.Contains(device, false)) await host.ScreenSignal(device, root.Clone(), token); continue; }
                    if (root.TryGetProperty("type", out _)) continue;
                    var id = root.Text("id"); var method = root.Text("method"); var path = root.Text("path");
                    if (id is not { Length: > 0 and <= 64 } || method is not ("GET" or "POST") || path is null || !path.StartsWith('/')) continue;
                    if (Encoding.UTF8.GetByteCount(path) > 16384) { await Send(new { id, status = 414, body = new { protocol = 1, error = "Path exceeds limit.", code = "badPath" } }); continue; }
                    if (inflight.ContainsKey(id) || !await requests.WaitAsync(0, token)) { await Send(new { id, status = 429, body = new { protocol = 1, error = "Too many pending requests." } }); continue; }
                    var body = root.TryGetProperty("body", out var b) ? b.Clone() : (JsonElement?)null;
                    // Insert before the asynchronous route can complete and remove itself.
                    var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); inflight[id] = completion.Task;
                    _ = Handle(id, method, path, body, completion);
                }
            }
            catch (Exception ex) when (ex is WebSocketException or IOException or OperationCanceledException or JsonException or FormatException or CryptographicException or Org.BouncyCastle.Crypto.CryptoException or InvalidOperationException or ArgumentException) { }
            finally
            {
                Cancel(); if (heartbeat is not null) await heartbeat; await Task.WhenAll(inflight.Values); cipher?.Dispose(); socket.Dispose(); cancel.Dispose();
            }
        }
        private async Task Heartbeat(CancellationToken token)
        {
            try { while (!token.IsCancellationRequested) { await Task.Delay(TimeSpan.FromSeconds(20), token); await Send(new { type = "ping" }); } }
            catch (OperationCanceledException) { }
        }
        private async Task Handle(string id, string method, string path, JsonElement? body, TaskCompletionSource completion)
        {
            try
            {
                cancel.Token.ThrowIfCancellationRequested();
                if (!host.identity.Contains(device, host.settings.AllowLegacyPhones)) return;
                var reply = await host.route(method, path, body, device, cancel.Token);
                // A reply the relay would close the connection for is refused instead, so the phone is told and keeps its tunnel.
                var frame = JsonSerializer.SerializeToUtf8Bytes(new { id, status = reply.Status, body = reply.Body }, Wire.Json);
                if (frame.Length > MaximumPlaintext) { await Send(new { id, status = 413, body = new { protocol = 1, error = "Response exceeds the mobile display limit." } }); return; }
                await SendBytes(frame);
                if (!cancel.IsCancellationRequested && reply.AfterSend is not null) await reply.AfterSend();
            }
            catch (OperationCanceledException) { }
            catch { await Send(new { id, status = 500, body = new { protocol = 1, error = "The mobile request failed." } }); }
            finally { inflight.TryRemove(id, out _); requests.Release(); completion.TrySetResult(); }
        }
        public Task Send(object value) => SendBytes(JsonSerializer.SerializeToUtf8Bytes(value, Wire.Json));
        private async Task SendBytes(byte[] bytes)
        {
            try
            {
                await writer.WaitAsync(cancel.Token);
                try
                {
                    if (cipher is null) return;
                    if (bytes.Length > MaximumPlaintext) throw new IOException("Relay response exceeds limit.");
                    await socket.SendAsync(cipher.Seal(bytes), WebSocketMessageType.Binary, true, cancel.Token);
                }
                finally { writer.Release(); }
            }
            catch (Exception ex) when (ex is WebSocketException or IOException or OperationCanceledException or ObjectDisposedException) { Cancel(); }
        }
    }
    public async ValueTask DisposeAsync() { await lifecycle.WaitAsync(); try { if (disposed) return; disposed = true; StopWatching(); await StopInternal(); } finally { lifecycle.Release(); } }
}
