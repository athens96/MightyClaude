using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MightyClaude.Core;

public sealed record ScreenIceServer(string[] Urls, string? Username = null, string? Credential = null);
public sealed record ScreenSafety(bool Locked, bool SecureInput, DateTimeOffset LastLocalInput);
public interface IScreenSharePlatform
{
    bool Available { get; }
    IReadOnlyList<ScreenDisplay> Displays { get; }
    ScreenSafety Safety { get; }
    Task StartPeerAsync(ScreenLiveSession session, IReadOnlyList<ScreenIceServer> ice, CancellationToken token);
    Task ReceiveSignalAsync(string sessionId, JsonElement signal, CancellationToken token);
    Task StopPeerAsync(string sessionId);
    void Halt();
    Task SetDisplayAsync(int displayId, CancellationToken token);
    void SetRegion(ScreenRegion region);
    Task InjectAsync(string sessionId, JsonElement input, CancellationToken token);
    Task ReleaseButtonsAsync(string sessionId);
    Task<bool> ShowMarkerAsync(string sessionId, JsonElement input, CancellationToken token);
    Task<string?> ReadClipboardAsync(CancellationToken token);
    Task WriteClipboardAsync(string text, CancellationToken token);
    Task SendDataAsync(string sessionId, object message, CancellationToken token);
}

/// Host-authoritative screen consent, biometric key enrollment, session limits
/// and input policy. No input contents, SDP, ICE passwords or clipboard bytes
/// enter persisted session history or application logs.
public sealed class ScreenShareHub : IAsyncDisposable
{
    private sealed class Session(ScreenLiveSession value)
    {
        internal ScreenLiveSession Value = value;
        internal readonly CancellationTokenSource Cancel = new();
        internal DateTimeOffset Activity = value.StartedAt;
        internal DateTimeOffset? Background;
        internal bool Connected;
        internal ScreenClipboard Clipboard = new();
    }
    private sealed record Challenge(byte[] Bytes, DateTimeOffset Expires);
    private readonly object sync = new();
    private readonly string path;
    private readonly IScreenSharePlatform platform;
    private readonly Func<string, object, Task> send;
    private readonly Func<string, string, CancellationToken, Task<bool>> confirm;
    private readonly Func<string, bool> trustedDevice;
    private readonly Func<DateTimeOffset> now;
    private Dictionary<string, ScreenDeviceGrant> grants = [];
    private readonly Dictionary<string, Session> sessions = [];
    private readonly Dictionary<string, Challenge> challenges = [];
    private readonly Dictionary<string, DateTimeOffset> refusedKeys = [];
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task watcher;
    private bool confirming, disposed;
    public bool InputPaused => Volatile.Read(ref confirming);
    public event Action? Changed;
    public IReadOnlyList<ScreenIceServer> IceServers { get; private set; } = [];
    public IReadOnlyList<ScreenLiveSession> Sessions { get { lock (sync) return sessions.Values.Select(s => s.Value).ToArray(); } }
    public IReadOnlyList<ScreenDeviceGrant> Grants { get { lock (sync) return grants.Values.ToArray(); } }
    public ScreenShareHub(string directory, IScreenSharePlatform platform, Func<string, object, Task> send, Func<string, string, CancellationToken, Task<bool>> confirm, Func<string, bool> trustedDevice, Func<DateTimeOffset>? now = null)
    {
        MobileIdentity.SecureDirectory(directory); path = Path.Combine(directory, "screen-devices.json"); this.platform = platform; this.send = send; this.confirm = confirm; this.trustedDevice = trustedDevice; this.now = now ?? (() => DateTimeOffset.UtcNow);
        if (File.Exists(path))
        {
            if (new FileInfo(path).Length > 65536 || File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Invalid screen sharing settings.");
            try
            {
                var values = JsonSerializer.Deserialize<ScreenDeviceGrant?[]>(File.ReadAllText(path), Wire.Json) ?? [];
                grants = values.Where(g => g is not null && MobileIdentity.ClientId(g.DeviceId) && g.Grant is "none" or "view" or "control" && (g.ControlKeyPublic is null || g.Grant == "control" && ValidStoredKey(g.ControlKeyPublic))).Select(g => g!).Take(32).DistinctBy(g => g.DeviceId).ToDictionary(g => g.DeviceId);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { grants = []; }
        }
        watcher = Watch();
    }
    private static bool ValidStoredKey(string text) { try { return text.Length <= 128 && ScreenSharePolicy.ValidKey(Convert.FromBase64String(text)); } catch (FormatException) { return false; } }
    public async Task SetGrantAsync(string deviceId, bool allowed, string grant)
    {
        string[] stopped;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!trustedDevice(deviceId) || !MobileIdentity.ClientId(deviceId) || grant is not ("none" or "view" or "control")) throw new ArgumentException("Invalid device grant.");
            var prior = grants.GetValueOrDefault(deviceId); var next = new ScreenDeviceGrant(deviceId, allowed, allowed ? grant : "none", allowed && grant == "control" && prior?.Grant == "control" ? prior.ControlKeyPublic : null);
            var copy = new Dictionary<string, ScreenDeviceGrant>(grants) { [deviceId] = next }; Save(copy); challenges.Remove(deviceId);
            stopped = sessions.Values.Where(s => s.Value.DeviceId == deviceId && !ScreenSharePolicy.GrantAllows(next, s.Value.Mode)).Select(s => s.Value.SessionId).ToArray();
        }
        foreach (var id in stopped) await Stop(id, allowed ? "grant-downgrade" : "revoked", true);
        try { await send(deviceId, new { type = "screen-grant", allowed, grant = allowed ? grant : "none", displays = DisplaysWire(), iceServers = IceServers }).WaitAsync(TimeSpan.FromSeconds(1)); } catch { } Changed?.Invoke();
    }
    public async Task RemoveControlKeyAsync(string device)
    {
        string[] ids; lock (sync) { if (!grants.TryGetValue(device, out var prior)) return; var copy = new Dictionary<string, ScreenDeviceGrant>(grants) { [device] = prior with { ControlKeyPublic = null } }; Save(copy); challenges.Remove(device); ids = sessions.Values.Where(s => s.Value.DeviceId == device && s.Value.Mode == "control").Select(s => s.Value.SessionId).ToArray(); }
        foreach (var id in ids) await Stop(id, "revoked", true); Changed?.Invoke();
    }
    public async Task ResetAsync(string reason)
    {
        lock (sync) { Save([]); challenges.Clear(); refusedKeys.Clear(); }
        await KillAllAsync(reason);
    }
    private void Save(Dictionary<string, ScreenDeviceGrant> next)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N");
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temporary, options)) { JsonSerializer.Serialize(stream, next.Values.ToArray(), Wire.Json); stream.Flush(true); }
            File.Move(temporary, path, true); grants = next;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private object[] DisplaysWire() => platform.Displays.Select(d => (object)new { d.DisplayId, d.Width, d.Height, d.Main }).ToArray();
    private ScreenDeviceGrant Require(string device, string mode)
    {
        if (device == "legacy" || !MobileIdentity.ClientId(device)) throw new ScreenShareException("legacy-client");
        if (!trustedDevice(device) || grants.GetValueOrDefault(device) is not { Allowed: true } grant) throw new ScreenShareException("device-not-allowed");
        if (!ScreenSharePolicy.GrantAllows(grant, mode)) throw new ScreenShareException("insufficient-grant"); return grant;
    }
    private string? ChallengeFor(string device, bool fresh)
    {
        if (grants.GetValueOrDefault(device) is not { Allowed: true, Grant: "control" }) return null;
        if (!fresh && challenges.TryGetValue(device, out var prior) && prior.Expires - now() > TimeSpan.FromSeconds(30)) return Convert.ToBase64String(prior.Bytes);
        var bytes = Encoding.UTF8.GetBytes("screen-control-challenge:" + Wire.Id() + ":" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)));
        challenges[device] = new(bytes, now().AddMinutes(2)); return Convert.ToBase64String(bytes);
    }
    public async Task<MobileReply> RouteAsync(string method, string leaf, JsonElement? body, string device, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            if (!platform.Available) return new(503, new { error = new { reason = "screen-permission" } });
            if (method == "GET" && leaf == "state")
            {
                lock (sync)
                {
                    var grant = trustedDevice(device) ? grants.GetValueOrDefault(device) : null;
                    var key = grant?.ControlKeyPublic is { } text ? Convert.FromBase64String(text) : null;
                    return new(200, new { screenShare = new { allowed = grant?.Allowed ?? false, grant = grant?.Grant ?? "none", isBeta = true, displays = DisplaysWire(), controlChallengeB64 = ChallengeFor(device, true), controlKeyFingerprint = key is null ? null : ScreenSharePolicy.Fingerprint(key), iceServers = IceServers, idleTimeoutSeconds = grant?.Grant == "control" ? 600 : 1800, tapMarker = true } });
                }
            }
            if (body is not { ValueKind: JsonValueKind.Object } value || Encoding.UTF8.GetByteCount(value.GetRawText()) > 65536) throw new ScreenShareException("bad-request", 400);
            if (method == "POST" && leaf == "control-key") return await Enroll(device, value, token);
            if (method == "POST" && leaf == "sessions")
            {
                Session session;
                lock (sync)
                {
                    var mode = value.Text("mode"); if (mode is not ("view" or "control") || value.Text("network") is not ("wifi" or "cellular") || !value.TryGetProperty("displayId", out var requested) || requested.ValueKind != JsonValueKind.Number || !requested.TryGetInt32(out var displayId)) throw new ScreenShareException("bad-request", 400);
                    var grant = Require(device, mode); var safety = platform.Safety;
                    if (safety.Locked || safety.SecureInput || confirming) throw new ScreenShareException(safety.Locked ? "lock-screen" : "secure-input");
                    if (mode == "control")
                    {
                        challenges.Remove(device, out var challenge);
                        if (challenge is null || challenge.Expires < now() || grant.ControlKeyPublic is null || value.Text("controlSignatureB64") is not { } signature || !ScreenSharePolicy.Verify(challenge.Bytes, Convert.FromBase64String(signature), Convert.FromBase64String(grant.ControlKeyPublic))) throw new ScreenShareException("control-signature");
                    }
                    if (!ScreenSharePolicy.CanJoin(mode, sessions.Values.Select(s => s.Value)) || sessions.Values.Any(s => s.Value.DeviceId == device)) throw new ScreenShareException("concurrency-limit");
                    var display = platform.Displays.FirstOrDefault(d => d.DisplayId == displayId) ?? platform.Displays.FirstOrDefault(d => d.Main) ?? throw new ScreenShareException("screen-permission");
                    if (sessions.Values.FirstOrDefault() is { } first && first.Value.DisplayId != display.DisplayId) display = platform.Displays.First(d => d.DisplayId == first.Value.DisplayId);
                    var decodes = value.TryGetProperty("decodes", out var codecs) && codecs.ValueKind == JsonValueKind.Array ? codecs.EnumerateArray().Where(c => c.ValueKind == JsonValueKind.String).Select(c => c.GetString()).ToArray() : ["H264"];
                    if (!decodes.Contains("H264")) throw new ScreenShareException("bad-request", 400);
                    session = new(new(Wire.Id(), device, mode, display.DisplayId, "H264", ScreenQuality.For(value.Text("network") == "cellular"), now())); sessions.Add(session.Value.SessionId, session);
                }
                Changed?.Invoke();
                return new(200, new { session.Value.SessionId, session.Value.Mode, session.Value.DisplayId, session.Value.Codec, session.Value.Quality }, async () =>
                {
                    try { session.Cancel.Token.ThrowIfCancellationRequested(); await platform.StartPeerAsync(session.Value, IceServers, session.Cancel.Token); }
                    catch { await Stop(session.Value.SessionId, "peer-left", false); }
                });
            }
            return new(404, new { error = new { reason = "not-found" } });
        }
        catch (ScreenShareException ex) { return new(ex.Status, new { error = new { reason = ex.Reason } }); }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException or InvalidOperationException) { return new(400, new { error = new { reason = "bad-request" } }); }
    }
    private async Task<MobileReply> Enroll(string device, JsonElement body, CancellationToken token)
    {
        var key = Convert.FromBase64String(body.Text("publicKeyB64") ?? ""); if (!ScreenSharePolicy.ValidKey(key)) throw new ScreenShareException("bad-request", 400);
        var fingerprint = ScreenSharePolicy.Fingerprint(key); ScreenDeviceGrant before;
        lock (sync)
        {
            before = Require(device, "control");
            if (before.ControlKeyPublic is { } prior) { if (!Convert.FromBase64String(prior).SequenceEqual(key)) throw new ScreenShareException("control-key-present", 409); return new(200, new { fingerprint }); }
            if (confirming) throw new ScreenShareException("control-key-pending", 409);
            if (refusedKeys.TryGetValue(device, out var refused) && now() - refused < TimeSpan.FromSeconds(60)) throw new ScreenShareException("control-key-not-confirmed");
            confirming = true;
        }
        try
        {
            var approved = await confirm(device, fingerprint, token); token.ThrowIfCancellationRequested();
            lock (sync)
            {
                var current = Require(device, "control"); if (current != before) throw new ScreenShareException("control-key-not-confirmed");
                if (!approved) { refusedKeys[device] = now(); throw new ScreenShareException("control-key-not-confirmed"); }
                var copy = new Dictionary<string, ScreenDeviceGrant>(grants) { [device] = current with { ControlKeyPublic = Convert.ToBase64String(key) } }; Save(copy);
            }
            Changed?.Invoke(); return new(200, new { fingerprint });
        }
        finally { lock (sync) confirming = false; }
    }
    public async Task SignalAsync(string device, JsonElement signal, CancellationToken token)
    {
        if (Encoding.UTF8.GetByteCount(signal.GetRawText()) > 65536 || signal.Text("sessionId") is not { } id) return;
        Session session; lock (sync) { if (!sessions.TryGetValue(id, out session!) || session.Value.DeviceId != device) return; }
        switch (signal.Text("type"))
        {
            case "screen-session-end" when signal.Text("reason") is "user-stop" or "background" or "peer-failed": await Stop(id, "peer-left", false); break;
            case "screen-background" when signal.TryGetProperty("background", out var background) && background.ValueKind is JsonValueKind.True or JsonValueKind.False: lock (sync) session.Background = background.GetBoolean() ? now() : null; break;
            case "screen-answer" when signal.Text("sdp") is { Length: > 0 }: await platform.ReceiveSignalAsync(id, signal, token); break;
            case "screen-ice" when signal.Text("candidate") is { Length: <= 4096 }: await platform.ReceiveSignalAsync(id, signal, token); break;
        }
    }
    public Task PeerFailedAsync(string sessionId) => Stop(sessionId, "peer-left", false);
    public void PeerConnected(string sessionId) { lock (sync) if (sessions.TryGetValue(sessionId, out var session)) { session.Connected = true; session.Activity = now(); } }
    public async Task PeerSignal(string sessionId, object signal)
    { ScreenLiveSession? session; lock (sync) session = sessions.GetValueOrDefault(sessionId)?.Value; if (session is not null) await send(session.DeviceId, signal); }
    private bool MayControl(Session session)
    {
        var safety = platform.Safety;
        lock (sync) return sessions.ContainsKey(session.Value.SessionId) && session.Value.Mode == "control" && ScreenSharePolicy.GrantAllows(grants.GetValueOrDefault(session.Value.DeviceId), "control") && trustedDevice(session.Value.DeviceId) && !confirming && !safety.Locked && !safety.SecureInput && now() - safety.LastLocalInput >= TimeSpan.FromSeconds(2);
    }
    public async Task InputAsync(string sessionId, string input)
    {
        if (ScreenControlWire.Parse(input) is not { } value) return; Session session;
        lock (sync) { if (!sessions.TryGetValue(sessionId, out session!)) return; session.Activity = now(); }
        var type = value.Text("t"); var token = session.Cancel.Token;
        try
        {
            if (type == "drag" && value.Text("phase") == "end") { await platform.ReleaseButtonsAsync(sessionId); return; }
            if (type is "zoom" or "display")
            {
                lock (sync) if (!trustedDevice(session.Value.DeviceId) || !ScreenSharePolicy.GrantAllows(grants.GetValueOrDefault(session.Value.DeviceId), session.Value.Mode) || confirming || platform.Safety.Locked || platform.Safety.SecureInput || session.Value.Mode != "control" && sessions.Count != 1) return;
                if (type == "zoom") platform.SetRegion(ScreenRegion.Parse(value.GetProperty("region"))!);
                else { var displayId = value.GetProperty("displayId").GetInt32(); if (!platform.Displays.Any(d => d.DisplayId == displayId)) return; await platform.SetDisplayAsync(displayId, token); lock (sync) foreach (var active in sessions.Values) active.Value = active.Value with { DisplayId = displayId }; Changed?.Invoke(); }
                return;
            }
            if (!MayControl(session)) { if (type == "tap" && value.Text("marker") is { } marker) await platform.SendDataAsync(sessionId, new { t = "marker", id = marker, shown = false }, token); return; }
            if (type == "clipboard")
            {
                if (session.Clipboard.Receive(value, now()) is { } text && MayControl(session)) await platform.WriteClipboardAsync(text, token); return;
            }
            if (type == "clipboard-request")
            {
                var text = await platform.ReadClipboardAsync(token); if (!MayControl(session)) return;
                foreach (var chunk in ScreenClipboard.Encode(text)) { if (!MayControl(session)) return; await platform.SendDataAsync(sessionId, chunk, token); } return;
            }
            await platform.InjectAsync(sessionId, value, token);
            if (type == "tap" && value.Text("marker") is { } requestedMarker) { var shown = MayControl(session) && await platform.ShowMarkerAsync(sessionId, value, token); await platform.SendDataAsync(sessionId, new { t = "marker", id = requestedMarker, shown }, token); }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidOperationException) { }
    }
    public async Task DeviceDisconnected(string device)
    { string[] ids; lock (sync) ids = sessions.Values.Where(s => s.Value.DeviceId == device).Select(s => s.Value.SessionId).ToArray(); foreach (var id in ids) await Stop(id, "peer-left", false); }
    public async Task KillAllAsync(string reason = "kill-switch")
    {
        Session[] stopped; lock (sync) { stopped = sessions.Values.ToArray(); sessions.Clear(); foreach (var session in stopped) session.Cancel.Cancel(); }
        try { platform.Halt(); } catch { /* Continue closing each peer even if the native gate failed. */ }
        await Task.WhenAll(stopped.Select(session => CloseSession(session, reason, true))); Changed?.Invoke();
    }
    private async Task Stop(string id, string reason, bool killed)
    {
        Session? session; lock (sync) { if (!sessions.Remove(id, out session)) return; session.Cancel.Cancel(); }
        await CloseSession(session, reason, killed); Changed?.Invoke();
    }
    private async Task CloseSession(Session session, string reason, bool killed)
    {
        var id = session.Value.SessionId;
        // A revoked phone may deliberately stop reading its relay socket.
        // Pixel teardown must finish before best-effort notification can wait.
        Task stopPeer; try { stopPeer = platform.StopPeerAsync(id); } catch { stopPeer = Task.FromException(new IOException("Screen peer teardown failed.")); }
        Task release; try { release = platform.ReleaseButtonsAsync(id); } catch { release = Task.CompletedTask; }
        try { await Task.WhenAll(stopPeer, release).WaitAsync(TimeSpan.FromSeconds(1)); }
        catch { try { platform.Halt(); } catch { } }
        try { await send(session.Value.DeviceId, new { type = killed ? "screen-kill" : "screen-session-end", sessionId = id, reason }).WaitAsync(TimeSpan.FromSeconds(1)); } catch { }
    }
    public Task BroadcastSceneAsync(string phase)
    {
        if (phase is not ("preroll" or "motion" or "still" or "done")) throw new ArgumentException("Unknown measurement phase.");
        return Task.WhenAll(Sessions.Select(async session => { try { await platform.SendDataAsync(session.SessionId, new { t = "scene", phase }, lifetime.Token).WaitAsync(TimeSpan.FromSeconds(2)); } catch { } }));
    }
    public async Task SetIceServersAsync(IReadOnlyList<ScreenIceServer> servers)
    {
        lock (sync) IceServers = servers;
        await Task.WhenAll(Sessions.Select(async session =>
        {
            try { await send(session.DeviceId, new { type = "screen-grant", sessionId = session.SessionId, allowed = true, grant = session.Mode, iceServers = servers }).WaitAsync(TimeSpan.FromSeconds(2)); await platform.ReceiveSignalAsync(session.SessionId, JsonSerializer.SerializeToElement(new { type = "host-ice-restart", iceServers = servers }, Wire.Json), lifetime.Token).WaitAsync(TimeSpan.FromSeconds(8)); }
            catch { await Stop(session.SessionId, "peer-left", false); }
        }));
    }
    private async Task Watch()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                await Task.Delay(250, lifetime.Token);
                try
                {
                var safety = platform.Safety;
                if (safety.Locked || safety.SecureInput) { if (Sessions.Count > 0) await KillAllAsync(safety.Locked ? "lock-screen" : "secure-input"); continue; }
                (string Id, string Reason)[] expired;
                lock (sync) expired = sessions.Values.Select(s => (s.Value.SessionId, Reason: s.Background is { } background && now() - background > TimeSpan.FromSeconds(30) ? "background" : !s.Connected && now() - s.Value.StartedAt > TimeSpan.FromSeconds(30) ? "peer-left" : now() - s.Activity > TimeSpan.FromSeconds(s.Value.Mode == "control" ? 600 : 1800) ? "idle-timeout" : null)).Where(p => p.Reason is not null).Select(p => (p.SessionId, p.Reason!)).ToArray();
                foreach (var value in expired) await Stop(value.Id, value.Reason, false);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException) { try { await KillAllAsync("secure-input"); } catch { } }
            }
        }
        catch (OperationCanceledException) { }
    }
    public async ValueTask DisposeAsync() { lock (sync) disposed = true; lifetime.Cancel(); await KillAllAsync(); try { await watcher; } catch (OperationCanceledException) { } lifetime.Dispose(); }
}
