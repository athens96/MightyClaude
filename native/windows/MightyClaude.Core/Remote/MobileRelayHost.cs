using System.Collections.Concurrent;
using System.Net.NetworkInformation;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MightyClaude.Core;

/// <param name="DirectEnabled">The direct path's switch (docs/relay.md, "Tailscale 직접 연결"); on unless turned off.</param>
/// <param name="DirectPort">The direct listener's port, 1024-65535.</param>
public sealed record MobileRemoteSettings(bool Enabled = false, string RelayURL = "", bool AllowLegacyPhones = true, bool DirectEnabled = true, int DirectPort = MobileDirect.DefaultPort)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public MobileRemoteSettings Normalized
    {
        get
        {
            var port = MobileDirect.ValidPort(DirectPort) ? DirectPort : MobileDirect.DefaultPort;
            var relay = (RelayURL ?? "").Trim();
            return relay.Length == 0 ? this with { RelayURL = "", DirectPort = port }
                : MobileRelayHost.NormalizeRelay(relay) is { } valid ? this with { RelayURL = valid, DirectPort = port }
                : this with { Enabled = false, RelayURL = "", DirectPort = port };
        }
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
    /// <summary>
    /// Phones inside the handshake on the relay path (M/MobileRemoteService.swift maximumUnauthenticated). The same number as the
    /// direct path's <see cref="MobileDirect.MaximumUnauthenticated"/> but a separate budget: each path counts only its own.
    /// </summary>
    public const int MaximumRelayUnauthenticated = 4;
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
    /// <summary>How long a phone may take from its socket to auth_ok, on either path (docs/relay.md: 10 s).</summary>
    public TimeSpan HandshakeDeadline { get; init; } = MobileDirect.HandshakeDeadline;
    /// <summary>How often the Tailscale addresses are read again while the direct path is on, on top of every address change Windows reports.</summary>
    public TimeSpan DirectRedetectInterval { get; init; } = MobileDirect.RedetectInterval;
    public Func<string[]>? ExtraCapabilities { get; set; }
    public bool IsTrustedDevice(string device) => identity.Contains(device, false);
    public Func<string, JsonElement, CancellationToken, Task>? ScreenSignal { get; set; }
    public Func<string, Task>? ScreenDisconnected { get; set; }
    public Func<IReadOnlyList<ScreenIceServer>, Task>? TurnCredentials { get; set; }
    public event Action<MobileRelayStatus>? StatusChanged;
    public MobileRelayStatus Status { get; private set; } = new(false, 0, "disabled");
    public IReadOnlyList<MobileDeviceInfo> Devices { get { var connected = clients.Values.Where(c => c.Authenticated).Select(c => c.DeviceId).ToHashSet(); return identity.Devices.Select(d => d with { Connected = connected.Contains(d.Id) }).ToArray(); } }
    public string HostId => identity.HostId;
    internal MobileIdentity Identity => identity;
    /// <summary>The relay this host dials; the push route sits beside it (docs/relay.md, the push section).</summary>
    public string RelayForPush => NormalizeRelay(settings.RelayURL) ?? DefaultRelay;
    public bool Enabled => settings.Enabled;
    public string PairingServerId => identity.ServerId;
    public string PairingKeyForDisplay => identity.PairingKey;
    public string AppVersion { get; init; } = "0.0.0";
    public string PairingUrl => identity.PairingUrl(NormalizeRelay(settings.RelayURL) ?? DefaultRelay, hostName, DirectEndpoints);

    // The direct path (M/MightyCore/Remote/MobileRemoteService.swift "Direct path"): listeners on this PC's Tailscale addresses while
    // the remote and its direct switch are on. Its phones have ids starting with "direct-" and live apart from the relay's: a relay
    // redial, a relay `disconnected` notice and a relay outage never touch them. Guarded by directSync, which also makes every
    // admission to the phone table (relay or direct) one atomic count-check-insert.
    private readonly object directSync = new();
    private MobileDirectListener? directListener;
    private IReadOnlyList<string>? directBoundAddresses;
    private int? directBoundPort;
    private string[] directEndpoints = [];
    private readonly DirectAuthFailures directFailures = new();
    /// <summary>
    /// Test seam: where the direct path reads its addresses instead of this PC's Tailscale adapters (loopback in the checks), and the
    /// port it binds then (0: any free one). Internal and reached only by MightyClaude.Core.Tests (InternalsVisibleTo): neither
    /// Settings nor a phone can set it, so the app only ever binds Tailscale addresses.
    /// </summary>
    private Func<IReadOnlyList<string>>? directAddressSource;
    private int directSourcePort;
    private CancellationTokenSource? directWatch;
    /// <summary>Every watch loop started, awaited by <see cref="DisposeAsync"/> so no pass publishes after it.</summary>
    private Task directWatchLoops = Task.CompletedTask;
    public MobileDirectStatus DirectStatus { get; private set; } = MobileDirectStatus.Off;
    /// <summary>The endpoints the direct path listens on now, as auth_ok, /m1/info and the QR carry them; empty while it is off.</summary>
    public IReadOnlyList<string> DirectEndpoints => Volatile.Read(ref directEndpoints);
    internal IReadOnlyList<string> ConnectionIds => [.. clients.Keys];

    internal void SetDirectAddressSource(Func<IReadOnlyList<string>>? source, int port = 0)
    {
        lock (directSync) { directAddressSource = source; directSourcePort = port; }
        RefreshDirect(force: true);
    }

    /// <summary>
    /// Reads the addresses again and makes the listeners match when they or the port changed; with the remote or the switch off,
    /// stops and drops the direct phones. Listeners whose address and port stay are kept, with their phones. <paramref name="force"/>
    /// rebinds even when nothing seems changed (Settings changed).
    /// </summary>
    public void RefreshDirect(bool force = false)
    {
        // Only a pass that changed something is published (a re-read that found the same addresses keeps the same status object),
        // so the 15 s watch does not redraw Settings or the QR for nothing.
        if (UpdateDirect(force)) Republish();
    }

    /// <summary><see cref="RefreshDirect"/> without the publish, the addresses read on the caller's thread; answers whether <see cref="DirectStatus"/> changed.</summary>
    private bool UpdateDirect(bool force) => ApplyDirect(force, ReadDirect());

    /// <summary>
    /// What one pass read, outside directSync: the source it read from and the addresses, or why they could not be read. Not
    /// <see cref="Wanted"/> when the path was off, and then nothing was read.
    /// </summary>
    private sealed record DirectRead(bool Wanted, Func<IReadOnlyList<string>>? Source, IReadOnlyList<string>? Addresses, Exception? Fault);

    /// <summary>
    /// Reads the addresses outside directSync: reading the adapters takes a while, so it never holds the lock the listeners' accepts
    /// and the relay's admissions wait on, and Settings runs it off the window's thread (<see cref="RestartInternal"/>).
    /// </summary>
    private DirectRead ReadDirect()
    {
        Func<IReadOnlyList<string>>? source; bool wanted;
        lock (directSync) { var current = settings; wanted = !disposed && current.Enabled && current.DirectEnabled; source = directAddressSource; }
        if (!wanted) return new(false, source, null, null);
        try { return new(true, source, source is null ? TailscaleAddresses.Current() : source(), null); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return new(true, source, null, ex); }
    }

    /// <summary>Makes the listeners match what <paramref name="read"/> found, under directSync; answers whether <see cref="DirectStatus"/> changed.</summary>
    private bool ApplyDirect(bool force, DirectRead read)
    {
        MobileDirectStatus before, after;
        Connection[] dropped = [];
        lock (directSync)
        {
            before = DirectStatus;
            var current = settings;
            if (disposed || !current.Enabled || !current.DirectEnabled) dropped = StopDirect(current);
            else
            {
                StartDirectWatch();
                // A read made while the path was still off, or from a source since replaced, says nothing of now: it is left alone,
                // and the pass that turned the path on or replaced the source (or the next watch tick) reads again.
                if (read.Wanted && ReferenceEquals(read.Source, directAddressSource))
                {
                    if (read.Fault is { } fault) ReadFailed(fault);
                    else if (read.Addresses is { } addresses)
                    {
                        var port = read.Source is null ? current.DirectPort : directSourcePort;
                        if (force || directBoundAddresses is not { } was || !was.SequenceEqual(addresses) || directBoundPort != port) BindDirect(addresses, port, read.Source is not null);
                    }
                }
            }
            // A pass that ends where it began keeps the same status object, so nothing is published for it: a failed or partly bound
            // path is bound again on every pass, and the switch-off state is made anew on every one.
            if (SameStatus(before, DirectStatus)) DirectStatus = before;
            after = DirectStatus;
        }
        foreach (var client in dropped) client.Cancel();
        return !ReferenceEquals(before, after);
    }

    private static bool SameStatus(MobileDirectStatus a, MobileDirectStatus b) => a.State == b.State && a.Error == b.Error && a.Endpoints.SequenceEqual(b.Endpoints);

    /// <summary>
    /// The addresses could not be read (under directSync): the path stops listening and says why in words, as when binding fails, and
    /// the next pass (the watch, an address change, Settings) reads them again. The phones already in stay, as when no address is
    /// found. The fault's own text goes to the trace only.
    /// </summary>
    private void ReadFailed(Exception ex)
    {
        System.Diagnostics.Trace.TraceError("mobile direct: the addresses could not be read: " + (ex.InnerException ?? ex).Message);
        directListener?.Stop(); directListener = null; directBoundAddresses = null; directBoundPort = null;
        Volatile.Write(ref directEndpoints, []); DirectStatus = new("failed", [], MobileDirectError.AdaptersUnreadable);
    }

    private void BindDirect(IReadOnlyList<string> addresses, int port, bool seam)
    {
        directBoundAddresses = addresses; directBoundPort = port;
        if (addresses.Count == 0)
        {
            directListener?.Stop(); directListener = null; Volatile.Write(ref directEndpoints, []); DirectStatus = new("notFound", []);
            return;
        }
        directListener ??= new MobileDirectListener(AcceptDirect);
        var (bound, error) = directListener.Update(addresses, port);
        Volatile.Write(ref directEndpoints, [.. MobileDirect.Advertised(bound, allowAnyHost: seam)]);
        // Anything not bound is tried again on the next pass rather than taken as settled.
        if (bound.Count < addresses.Count) directBoundAddresses = null;
        if (bound.Count == 0) { directListener.Stop(); directListener = null; DirectStatus = new("failed", [], error); }
        else DirectStatus = new("listening", Volatile.Read(ref directEndpoints));
    }

    /// <summary>
    /// Stops listening (under directSync) and answers every phone that came in directly, for the caller to cancel once it has let go of
    /// the lock: cancelling runs continuations that may publish. With the listener gone nothing new is admitted in between.
    /// </summary>
    private Connection[] StopDirect(MobileRemoteSettings current)
    {
        var watch = directWatch; directWatch = null; watch?.Cancel();
        directListener?.Stop(); directListener = null;
        directBoundAddresses = null; directBoundPort = null; Volatile.Write(ref directEndpoints, []);
        DirectStatus = !disposed && current.Enabled && !current.DirectEnabled ? new("disabled", []) : MobileDirectStatus.Off;
        return [.. clients.Values.Where(client => client.Direct)];
    }

    /// <summary>Reads the addresses again every <see cref="DirectRedetectInterval"/> while the path is on: Tailscale coming up does not always raise an address change. Under directSync.</summary>
    private void StartDirectWatch()
    {
        if (directWatch is not null) return;
        var watch = directWatch = new CancellationTokenSource();
        var interval = DirectRedetectInterval > TimeSpan.Zero ? DirectRedetectInterval : MobileDirect.RedetectInterval;
        var loop = Task.Run(async () =>
        {
            try
            {
                using var timer = new PeriodicTimer(interval);
                // One pass that fails never ends the watch: it is reported and the next tick tries again.
                while (await timer.WaitForNextTickAsync(watch.Token)) RedetectDirect();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (ex is not OutOfMemoryException) { System.Diagnostics.Trace.TraceError("mobile direct: the watch stopped: " + ex.Message); }
            finally
            {
                // A watch that ended on its own is cleared, so the next pass starts another.
                lock (directSync) { if (directWatch == watch) directWatch = null; }
                watch.Dispose();
            }
        });
        directWatchLoops = directWatchLoops.IsCompleted ? loop : Task.WhenAll(directWatchLoops, loop);
    }

    /// <summary>A re-detection off the caller's path (the watch, an address change): a fault is reported, never thrown into a timer or an event.</summary>
    private void RedetectDirect()
    {
        try { RefreshDirect(); }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException) { System.Diagnostics.Trace.TraceError("mobile direct: re-detection failed: " + ex.Message); }
    }

    /// <summary>
    /// One socket a direct listener accepted, on the listener's accept loop and before a byte of it is read. Admitted ones join the
    /// relay's phone table and run the same <see cref="Connection"/>, held to the same deadline (the upgrade included), auth and request
    /// limits; anything else (a peer outside Tailscale's ranges, one locked out by failed auths, one over the limits) is reset unread
    /// (<see cref="MobileDirect.Admit"/>). The count, the check and the insert happen under directSync together, so two sockets racing
    /// in from one peer cannot both take the last slot.
    /// </summary>
    private void AcceptDirect(DirectIncoming incoming)
    {
        Connection connection;
        lock (directSync)
        {
            var current = settings;
            var pending = clients.Values.Where(client => client.Direct && !client.Authenticated).Select(client => client.Peer ?? "").ToArray();
            var failures = incoming.Peer is { } peer ? directFailures.Recent(peer, DateTimeOffset.UtcNow) : 0;
            if (disposed || directListener is null || !current.Enabled || !current.DirectEnabled
                || !MobileDirect.Admit(incoming.Peer, directAddressSource is not null, clients.Count, pending, failures)) { incoming.Refuse(); return; }
            var id = MobileDirect.ConnectionPrefix + Guid.NewGuid().ToString();
            // The listener slot goes back once the connection has ended, whichever way it ends.
            connection = new Connection(this, id, token => MobileDirect.AcceptWebSocketAsync(incoming.Stream, token), CancellationToken.None, direct: true, peer: incoming.Peer) { Ended = incoming.Dispose };
            clients[id] = connection;
        }
        _ = Serve(connection);
    }

    /// <summary>The relay path's data socket for one connection id, dialled inside the handshake deadline.</summary>
    private Func<CancellationToken, Task<WebSocket?>> Open(Uri uri) => async token =>
    {
        var socket = Socket();
        try { await socket.ConnectAsync(uri, token); return socket; }
        catch { socket.Dispose(); throw; }
    };
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
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var before = settings; var now = value.Normalized;
            await RestartInternal(now, forceDirect: now.Enabled != before.Enabled || now.DirectEnabled != before.DirectEnabled || now.DirectPort != before.DirectPort);
        }
        finally { lifecycle.Release(); }
    }
    public async Task ReconnectAsync()
    {
        await lifecycle.WaitAsync();
        try { ObjectDisposedException.ThrowIf(disposed, this); await RestartInternal(settings); }
        finally { lifecycle.Release(); }
    }
    private async Task RestartInternal(MobileRemoteSettings value, bool forceDirect = false)
    {
        await StopInternal(); settings = value;
        // The direct path follows its own switch, not the relay's state (M/MobileRemoteService.swift:409-424): the relay was just
        // stopped and the direct phones kept. Its new status rides on the publish just below, before the relay dials, so no relay
        // report is ever sent twice for it. Reading the adapters takes a while and Settings applies from the window's thread, so the
        // read runs on the pool, as the relay path's own read does (StartWatching); binding then happens under directSync.
        var read = await Task.Run(ReadDirect);
        ApplyDirect(forceDirect, read);
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
    // An address change may be Tailscale coming up or going down: the relay's path is read and the direct path re-detected.
    private void AddressChanged(object? sender, EventArgs e) { Read(); _ = Task.Run(RedetectDirect); }
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
    /// <summary>Stops the relay link and the relay's phones; the direct phones stay (<see cref="StopDirect"/> is theirs).</summary>
    private async Task StopInternal()
    {
        ++generation; lifetime?.Cancel();
        foreach (var client in clients.Values.Where(client => !client.Direct)) client.Cancel();
        if (loop is { } running) try { await running; } catch (OperationCanceledException) { }
        await Task.WhenAll(clients.Values.Where(client => !client.Direct).Select(client => client.Completion.Task));
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
                    // A direct- id is never the relay's to open or close: the relay cannot reach a phone that came in directly.
                    if (value.Text("type") == "disconnected") { if (!MobileDirect.IsDirect(id) && clients.TryGetValue(id, out var prior)) prior.Cancel(); continue; }
                    if (value.Text("type") != "connected" || MobileDirect.IsDirect(id)) continue;
                    Connection connection; bool added;
                    // The same lock as the direct path's admission: the shared table's count, check and insert happen together.
                    lock (directSync)
                    {
                        if (clients.Count >= MobileDirect.MaximumClients || clients.Values.Count(c => !c.Authenticated && !c.Direct) >= MaximumRelayUnauthenticated) continue;
                        connection = new Connection(this, id, Open(SocketUri(relay, id)), token);
                        added = clients.TryAdd(id, connection);
                    }
                    if (added) _ = Serve(connection);
                }
            }
            catch (Exception ex) when (ex is WebSocketException or IOException or OperationCanceledException or JsonException or InvalidOperationException) { }
            finally { renew.Cancel(); if (renewal is not null) await renewal; var ended = clients.Values.Where(c => !c.Direct).ToArray(); foreach (var client in ended) client.Cancel(); await Task.WhenAll(ended.Select(c => c.Completion.Task)); }
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
    public const int MaximumPlaintext = MobileDirect.MaximumFrameBytes - 28;
    private static async Task<(WebSocketMessageType Type, byte[] Bytes)> Receive(WebSocket socket, CancellationToken token)
    {
        using var data = new MemoryStream(); var buffer = new byte[8192]; WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, token);
            if (result.MessageType == WebSocketMessageType.Close) throw new IOException("Relay socket closed.");
            if (data.Length + result.Count > MobileDirect.MaximumFrameBytes) throw new IOException("Relay frame exceeded limit.");
            data.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return (result.MessageType, data.ToArray());
    }
    private sealed class Connection
    {
        public string Id { get; }
        public bool Authenticated { get; private set; }
        /// <summary>Came in on the direct path: kept through relay redials, refused with one word, its failed auths counted against <see cref="Peer"/>.</summary>
        public bool Direct { get; }
        /// <summary>The direct peer's address; null on the relay path.</summary>
        public string? Peer { get; }
        /// <summary>Runs once the connection has ended and its socket is gone (a direct one gives its listener slot back), whatever ended it.</summary>
        public Action? Ended { get; init; }
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int cancelled;
        private Task? heartbeat;
        private readonly MobileRelayHost host;
        private readonly Func<CancellationToken, Task<WebSocket?>> open;
        private readonly CancellationTokenSource cancel;
        private WebSocket? socket;
        private readonly SemaphoreSlim writer = new(1), requests = new(8);
        private readonly ConcurrentDictionary<string, Task> inflight = [];
        private RelayCipher? cipher;
        private string device = "";
        public string DeviceId => device;
        public Connection(MobileRelayHost host, string id, Func<CancellationToken, Task<WebSocket?>> open, CancellationToken token, bool direct = false, string? peer = null)
        { this.host = host; Id = id; this.open = open; Direct = direct; Peer = peer; cancel = CancellationTokenSource.CreateLinkedTokenSource(token); }
        public void Cancel() { if (Interlocked.Exchange(ref cancelled, 1) == 0) { cancel.Cancel(); Volatile.Read(ref socket)?.Abort(); } }
        public async Task Run()
        {
            var token = cancel.Token;
            try
            {
                // One deadline from the socket to auth_ok: the relay path dials its data socket, the direct path reads the phone's
                // HTTP upgrade, and both then read hello and auth, all on this one token.
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(host.HandshakeDeadline);
                var opened = await open(deadline.Token);
                if (opened is null) return;
                Volatile.Write(ref socket, opened);
                if (Volatile.Read(ref cancelled) == 1) return;
                var hello = await Receive(opened, deadline.Token);
                if (hello.Type != WebSocketMessageType.Text || hello.Bytes.Length > 4096) return;
                using var hd = JsonDocument.Parse(hello.Bytes); var h = hd.RootElement;
                if (h.Text("type") != "hello" || !h.TryGetProperty("v", out var v) || !v.TryGetInt32(out var version) || version != 1) return;
                var nonce = RandomNumberGenerator.GetBytes(16);
                cipher = new(host.identity.Secret, Convert.FromBase64String(h.Text("clientKey") ?? ""), Convert.FromBase64String(h.Text("nonce") ?? ""), nonce, true);
                var ready = JsonSerializer.SerializeToUtf8Bytes(new { type = "ready", v = 1, serverKey = host.identity.PublicKey, nonce = Convert.ToBase64String(nonce) }, Wire.Json);
                await opened.SendAsync(ready, WebSocketMessageType.Text, true, deadline.Token);
                var authentication = await Receive(opened, deadline.Token);
                if (authentication.Type != WebSocketMessageType.Binary) return;
                using var ad = AuthFrame(cipher.Open(authentication.Bytes));
                var auth = host.identity.Authenticate(ad.RootElement, host.settings.AllowLegacyPhones);
                if (auth.DeviceId is null)
                {
                    // A direct refusal is one word, so a tailnet peer learns nothing of which ids or keys this PC knows; the phone then
                    // takes the relay and hears the real reason there (docs/relay.md). Each one counts against the peer's address.
                    if (Direct && Peer is not null) host.directFailures.Record(Peer, DateTimeOffset.UtcNow);
                    await Send(new { type = "auth_error", reason = Direct ? MobileDirect.GenericRefusal : auth.Error });
                    return;
                }
                device = auth.DeviceId; Authenticated = true;
                await Send(new { type = "auth_ok", hostName = host.hostName, hostId = host.identity.HostId, appVersion = host.AppVersion, capabilities = host.capabilities.Concat(host.ExtraCapabilities?.Invoke() ?? []).Distinct().ToArray(), direct = host.DirectEndpoints, deviceToken = auth.DeviceToken });
                host.Republish();
                heartbeat = Heartbeat(token);
                while (!token.IsCancellationRequested)
                {
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(token); idle.CancelAfter(TimeSpan.FromSeconds(60));
                    var frame = await Receive(opened, idle.Token);
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
            catch (Exception ex) when (ex is WebSocketException or IOException or OperationCanceledException or JsonException or FormatException or CryptographicException or Org.BouncyCastle.Crypto.CryptoException or InvalidOperationException or ArgumentException or ObjectDisposedException) { }
            finally
            {
                // Ended runs on every way out, so a direct connection's listener slot is never kept by one that has gone.
                try { Cancel(); if (heartbeat is not null) await heartbeat; await Task.WhenAll(inflight.Values); cipher?.Dispose(); Volatile.Read(ref socket)?.Dispose(); cancel.Dispose(); }
                finally { Ended?.Invoke(); }
            }
        }
        /// <summary>
        /// The decrypted auth frame; one that is not JSON reads as an empty object, so it is refused as malformed (generically and
        /// counted on the direct path) rather than ending the connection unanswered, as on the Mac (decide(frame: [:])).
        /// </summary>
        private static JsonDocument AuthFrame(byte[] plaintext)
        {
            try { return JsonDocument.Parse(plaintext); }
            catch (JsonException) { return JsonDocument.Parse("{}"); }
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
                    if (cipher is null || Volatile.Read(ref socket) is not { } current) return;
                    if (bytes.Length > MaximumPlaintext) throw new IOException("Relay response exceeds limit.");
                    await current.SendAsync(cipher.Seal(bytes), WebSocketMessageType.Binary, true, cancel.Token);
                }
                finally { writer.Release(); }
            }
            catch (Exception ex) when (ex is WebSocketException or IOException or OperationCanceledException or ObjectDisposedException) { Cancel(); }
        }
    }
    public async ValueTask DisposeAsync()
    {
        await lifecycle.WaitAsync();
        try { if (disposed) return; disposed = true; StopWatching(); await StopInternal(); }
        finally { lifecycle.Release(); }
        Connection[] dropped; Task watches;
        lock (directSync) { dropped = StopDirect(settings); watches = directWatchLoops; }
        foreach (var client in dropped) client.Cancel();
        // The watch was cancelled with the path; a pass already running ends before this returns, so none publishes after dispose.
        await watches;
        await Task.WhenAll(clients.Values.Select(client => client.Completion.Task));
    }
}
