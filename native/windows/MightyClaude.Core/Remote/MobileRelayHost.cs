using System.Collections.Concurrent;
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
public sealed record MobileRelayStatus(bool Connected, int Connections, string Detail);

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
    public Func<string[]>? ExtraCapabilities { get; set; }
    public bool IsTrustedDevice(string device) => identity.Contains(device, false);
    public Func<string, JsonElement, CancellationToken, Task>? ScreenSignal { get; set; }
    public Func<string, Task>? ScreenDisconnected { get; set; }
    public Func<IReadOnlyList<ScreenIceServer>, Task>? TurnCredentials { get; set; }
    public event Action<MobileRelayStatus>? StatusChanged;
    public MobileRelayStatus Status { get; private set; } = new(false, 0, "disabled");
    public IReadOnlyList<MobileDeviceInfo> Devices => identity.Devices;
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
    private static ClientWebSocket Socket()
    {
        var socket = new ClientWebSocket(); socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20); socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(10); return socket;
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
        if (!settings.Enabled) { Publish(false, "disabled"); return; }
        var relay = NormalizeRelay(settings.RelayURL.Length == 0 ? DefaultRelay : settings.RelayURL) ?? throw new ArgumentException("Invalid relay address.");
        lifetime = new(); var current = ++generation; loop = RunAsync(relay, current, lifetime.Token);
    }
    public async Task RotateKeyAsync(string? expectedDeviceId = null)
    {
        await lifecycle.WaitAsync();
        try { ObjectDisposedException.ThrowIf(disposed, this); if (expectedDeviceId is not null && !identity.Contains(expectedDeviceId, true)) return; identity.Rotate(); foreach (var client in clients.Values) client.Cancel(); }
        finally { lifecycle.Release(); }
        Publish(Status.Connected, Status.Detail);
    }
    private async Task StopInternal()
    {
        ++generation; lifetime?.Cancel(); foreach (var client in clients.Values) client.Cancel();
        if (loop is { } running) try { await running; } catch (OperationCanceledException) { }
        await Task.WhenAll(clients.Values.Select(c => c.Completion.Task));
        lifetime?.Dispose(); lifetime = null; loop = null;
    }
    private void Publish(bool connected, string detail) { Status = new(connected, clients.Values.Count(c => c.Authenticated), detail); StatusChanged?.Invoke(Status); }
    private async Task RunAsync(string relay, int current, CancellationToken token)
    {
        var delay = 1;
        while (!token.IsCancellationRequested && current == generation)
        {
            using var socket = Socket();
            using var renew = CancellationTokenSource.CreateLinkedTokenSource(token); Task? renewal = null;
            try
            {
                await socket.ConnectAsync(SocketUri(relay), token); Interlocked.Exchange(ref turnRenewTicks, DateTimeOffset.UtcNow.AddMinutes(45).UtcTicks); delay = 1; Publish(true, "connected");
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
            Publish(false, "reconnecting");
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
        finally { clients.TryRemove(new KeyValuePair<string, Connection>(connection.Id, connection)); connection.Completion.TrySetResult(); if (ScreenDisconnected is not null && connection.Authenticated) { try { await ScreenDisconnected(connection.DeviceId); } catch { } } Publish(Status.Connected, Status.Detail); }
    }
    public async Task NotifyAsync(string scope, long revision)
    {
        if (!(scope == "state" || scope.StartsWith("session:", StringComparison.Ordinal))) return;
        await Task.WhenAll(clients.Values.Where(c => c.Authenticated).Select(c => c.Send(new { type = "notify", scope, revision })));
    }
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
        private readonly ClientWebSocket socket = Socket();
        private readonly SemaphoreSlim writer = new(1), requests = new(8);
        private readonly ConcurrentDictionary<string, Task> inflight = [];
        private RelayCipher? cipher;
        private string device = "";
        public string DeviceId => device;
        public Connection(MobileRelayHost host, string id, Uri uri, CancellationToken token) { this.host = host; Id = id; this.uri = uri; cancel = CancellationTokenSource.CreateLinkedTokenSource(token); }
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
                host.Publish(host.Status.Connected, host.Status.Detail);
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
                    if (Encoding.UTF8.GetByteCount(path) > 16384) { await Send(new { id, status = 414, body = new { protocol = 1, error = "Path exceeds limit." } }); continue; }
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
                await Send(new { id, status = reply.Status, body = reply.Body });
                if (!cancel.IsCancellationRequested && reply.AfterSend is not null) await reply.AfterSend();
            }
            catch (OperationCanceledException) { }
            catch { await Send(new { id, status = 500, body = new { protocol = 1, error = "The mobile request failed." } }); }
            finally { inflight.TryRemove(id, out _); requests.Release(); completion.TrySetResult(); }
        }
        public async Task Send(object value)
        {
            try
            {
                await writer.WaitAsync(cancel.Token);
                try
                {
                    if (cipher is null) return;
                    var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Wire.Json);
                    if (bytes.Length > MaximumFrame - 28) throw new IOException("Relay response exceeds limit.");
                    await socket.SendAsync(cipher.Seal(bytes), WebSocketMessageType.Binary, true, cancel.Token);
                }
                finally { writer.Release(); }
            }
            catch (Exception ex) when (ex is WebSocketException or IOException or OperationCanceledException or ObjectDisposedException) { Cancel(); }
        }
    }
    public async ValueTask DisposeAsync() { await lifecycle.WaitAsync(); try { if (disposed) return; disposed = true; await StopInternal(); } finally { lifecycle.Release(); } }
}
