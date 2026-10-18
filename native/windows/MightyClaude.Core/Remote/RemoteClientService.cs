using System.Text.Json;

namespace MightyClaude.Core;

public sealed record RemoteClientOptions
{
    /// <summary>Loopback stands in for Tailscale; only tests and the smoke run set it, in code.</summary>
    public bool AllowLoopbackForTests { get; init; }
    /// <summary>Whether the route to a host's IP still leaves from this PC's Tailscale tunnel; asked before every request. Replaces the real check only when set.</summary>
    public Func<string, bool>? RouteCheck { get; init; }
    /// <summary>This PC's Tailscale addresses (empty while Tailscale is off); tests replace it.</summary>
    public Func<IReadOnlyList<string>>? LocalAddresses { get; init; }
    public TimeSpan PollRetryWindow { get; init; } = RemoteProtocol.PollRetryWindow;
}

/// <summary>
/// "다른 컴퓨터 연결" (M/Remote/RemoteClientService.swift): saved connections to other computers' Mighty Claude, the remote workspaces
/// opened through them, and the runs of their panes. A run's events are the host runner's own, read by cursor and applied as
/// the pane's; polling a run is also its heartbeat.
/// </summary>
public sealed partial class RemoteClientService : IAsyncDisposable
{
    private sealed class Entry(RemoteConnectionInfo info)
    {
        public RemoteConnectionInfo Info = info;
        public string? Key;
        public RemoteTarget? Target;
        /// <summary>Saved with a key in the vault that was not read yet (read on first use).</summary>
        public bool KeyPending;
        public Guid Revision = Guid.NewGuid();
    }

    private readonly StateStore store;
    private readonly string dataDirectory;
    private readonly IRemoteKeyVault keys;
    private readonly Action<RunEvent> onEvent;
    private readonly Action<ToolPermissionRequest> onPermission;
    private readonly RemoteClientOptions options;
    private readonly Func<string, bool> routeCheck;
    private readonly object gate = new();
    /// <summary>One write of remote-connections.json at a time, each with the list as it is when its turn comes.</summary>
    private readonly SemaphoreSlim writes = new(1, 1);
    private readonly Dictionary<string, Entry> connections = new(StringComparer.Ordinal);
    private readonly List<string> order = [];
    private readonly HashSet<string> connecting = new(StringComparer.Ordinal);
    private bool loaded, closing;

    public event Action? Changed;

    public RemoteClientService(StateStore store, string dataDirectory, IRemoteKeyVault keys, Action<RunEvent> onEvent, Action<ToolPermissionRequest> onPermission, RemoteClientOptions? options = null)
    {
        this.store = store; this.dataDirectory = dataDirectory; this.keys = keys; this.onEvent = onEvent; this.onPermission = onPermission;
        this.options = options ?? new();
        routeCheck = this.options.RouteCheck ?? (this.options.AllowLoopbackForTests ? _ => true : RemoteRoute.ThroughTailscale);
    }

    public IReadOnlyList<RemoteConnectionInfo> List() { LoadSaved(); lock (gate) return order.Select(id => connections[id].Info).ToList(); }
    public RemoteConnectionInfo? Connection(string id) { LoadSaved(); lock (gate) return connections.TryGetValue(id, out var c) ? c.Info : null; }

    /// <summary>Connects with a new address and key, or replaces the key of the saved connection at the same address (a host that restarted sharing has a new one).</summary>
    public async Task<RemoteConnectionInfo> ConnectAsync(string name, string address, string key)
    {
        LoadSaved();
        var label = (name ?? "").Trim(); var trimmed = (key ?? "").Trim();
        if (label.Length > 120 || !RemoteWorkspaceValidation.Key(trimmed)) throw new ArgumentException(Locale.Get("remote.connect.error.badKey"));
        var parsed = RemoteAddress.Parse(address);
        Entry? existing;
        lock (gate)
        {
            if (closing) throw new InvalidOperationException(Locale.Get("common.appClosing"));
            existing = connections.Values.FirstOrDefault(c => c.Info.Address == parsed.Display);
            if (existing is null && connections.Count >= RemoteProtocol.MaximumConnections) throw TooMany();
            if (!connecting.Add(parsed.Display)) throw new InvalidOperationException(Locale.Get("remote.connect.error.busy"));
        }
        try
        {
            var (target, info) = await ReachAsync(parsed, trimmed);
            lock (gate) if (closing) throw new InvalidOperationException(Locale.Get("common.appClosing"));
            var id = existing?.Info.Id ?? Wire.Id();
            if (existing is not null) await DropRunsAsync(id, null);
            // A vault that cannot keep the key leaves it in memory for this launch; an older key saved under this id is never read back instead.
            var saved = keys.Save(id, trimmed);
            if (!saved) keys.Delete(id);
            var view = new RemoteConnectionInfo(id, label.Length > 0 ? label : existing?.Info.Name ?? info.HostName, parsed.Display, RemoteConnectionStatus.Connected, info.HostName, info.Workspaces, info.Runtime,
                saved ? RemoteWindowsStrings.ConnectedSaved : RemoteWindowsStrings.ConnectedMemory);
            lock (gate)
            {
                // Another connect may have filled the list while this one waited on the host.
                if (closing || !connections.ContainsKey(id) && connections.Count >= RemoteProtocol.MaximumConnections)
                {
                    if (existing is null) keys.Delete(id);
                    throw closing ? new InvalidOperationException(Locale.Get("common.appClosing")) : TooMany();
                }
                connections[id] = new Entry(view) { Key = trimmed, Target = target };
                if (!order.Contains(id)) order.Add(id);
            }
            await WriteSavedAsync(); Changed?.Invoke();
            return view;
        }
        finally { lock (gate) connecting.Remove(parsed.Display); }
    }

    /// <summary>Reconnects a saved connection; its key is read from the vault here the first time, never at launch.</summary>
    public async Task<RemoteConnectionInfo> RefreshAsync(string id)
    {
        LoadSaved();
        Entry current; Guid revision; string? key;
        lock (gate)
        {
            if (closing) throw new InvalidOperationException(Locale.Get("common.appClosing"));
            if (!connections.TryGetValue(id, out current!)) throw new InvalidOperationException(Locale.Get("remote.connect.error.unknown"));
            if (current.Key is null && current.KeyPending) { current.Key = keys.Load(id); current.KeyPending = false; }
            key = current.Key;
            if (key is not null && !connecting.Add(current.Info.Address)) throw new InvalidOperationException(Locale.Get("remote.connect.error.busy"));
            if (key is null) current.Info = current.Info with { Status = RemoteConnectionStatus.Disconnected, Detail = Locale.Get("remote.connect.error.keyMissing") };
            revision = current.Revision;
        }
        if (key is null) { Changed?.Invoke(); throw new InvalidOperationException(Locale.Get("remote.connect.error.keyMissing")); }
        try
        {
            var (target, info) = await ReachAsync(RemoteAddress.Parse(current.Info.Address), key);
            RemoteConnectionInfo result;
            lock (gate)
            {
                if (closing || current.Revision != revision) throw new InvalidOperationException(Locale.Get("remote.connect.error.changed"));
                current.Target = target;
                result = current.Info = current.Info with { Status = RemoteConnectionStatus.Connected, HostName = info.HostName, Workspaces = info.Workspaces, Runtime = info.Runtime, Detail = Locale.Get("remote.connect.detail.connected") };
            }
            Changed?.Invoke();
            return result;
        }
        catch (Exception ex)
        {
            bool same; lock (gate) same = current.Revision == revision;
            if (same) await FailAsync(id, ex.Message);
            throw;
        }
        finally { lock (gate) connecting.Remove(current.Info.Address); }
    }

    /// <summary>Connected now, reconnecting first when it is not: a remote pane's send does this.</summary>
    public async Task<RemoteConnectionInfo> EnsureConnectedAsync(string id)
    {
        LoadSaved();
        // Two panes sending at once after launch share one connect.
        var deadline = DateTimeOffset.UtcNow + RemoteProtocol.RequestTimeout + TimeSpan.FromSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            bool waiting; lock (gate) waiting = connections.TryGetValue(id, out var c) && connecting.Contains(c.Info.Address);
            if (!waiting) break;
            await Task.Delay(50);
        }
        RemoteConnectionInfo? info = null; RemoteTarget? target = null;
        lock (gate) if (connections.TryGetValue(id, out var c)) { info = c.Info; target = c.Target; }
        if (info is { Status: RemoteConnectionStatus.Connected } && target is not null) { await EnsureRouteAsync(id, target); return info; }
        return await RefreshAsync(id);
    }

    /// <summary>Stops this connection's runs and keeps it saved; a refresh connects again.</summary>
    public async Task DisconnectAsync(string id)
    {
        await DropRunsAsync(id, null);
        lock (gate)
            if (connections.TryGetValue(id, out var c)) { c.Revision = Guid.NewGuid(); c.Target = null; c.Info = c.Info with { Status = RemoteConnectionStatus.Disconnected, Detail = Locale.Get("remote.connect.detail.disconnected") }; }
        Changed?.Invoke();
    }

    /// <summary>Forgets the connection and its key. Its workspaces stay listed until removed.</summary>
    public async Task RemoveAsync(string id)
    {
        await DisconnectAsync(id);
        lock (gate) { connections.Remove(id); order.Remove(id); }
        keys.Delete(id);
        await WriteSavedAsync(); Changed?.Invoke();
    }

    /// <summary>Lists the host's shared workspace <paramref name="hostWorkspaceId"/> in this app as a remote workspace.</summary>
    public Workspace OpenWorkspace(string connectionId, string hostWorkspaceId)
    {
        RemoteConnectionInfo info;
        lock (gate)
        {
            if (!connections.TryGetValue(connectionId, out var c) || c.Info.Status != RemoteConnectionStatus.Connected) throw new InvalidOperationException(Locale.Get("remote.workspace.error.notShared"));
            info = c.Info;
        }
        var shared = info.Shared.FirstOrDefault(w => w.Id == hostWorkspaceId);
        if (shared is null || !RemoteWorkspaceValidation.SharedWorkspace(shared)) throw new InvalidOperationException(Locale.Get("remote.workspace.error.notShared"));
        return store.ApproveRemote(connectionId, shared.Id, shared.Name, shared.Path, info.HostName ?? info.Name);
    }

    private static InvalidOperationException TooMany() =>
        new(Locale.Get("remote.connect.error.tooMany", new Dictionary<string, string> { ["count"] = RemoteProtocol.MaximumConnections.ToString(System.Globalization.CultureInfo.InvariantCulture) }));

    /// <summary>The route check, where one that throws is no tunnel: never a passing failure, never a request.</summary>
    private bool Routed(string ip)
    {
        try { return routeCheck(ip); }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning("remote route check: " + ex.GetType().Name);
            return false;
        }
    }

    private async Task<(RemoteTarget Target, RemoteWireInfo Info)> ReachAsync(RemoteAddress address, string key)
    {
        // As the Mac: while testing (loopback injected) this PC's own Tailscale is not asked, unless a check gives its addresses.
        if (options.LocalAddresses is not null || !options.AllowLoopbackForTests)
        {
            IReadOnlyList<string> local;
            try { local = options.LocalAddresses?.Invoke() ?? TailscaleAddresses.Current(); }
            catch (AdaptersUnreadableException) { local = []; }
            if (local.Count == 0) throw new InvalidOperationException(RemoteWindowsStrings.LocalTailscaleOff);
        }
        var target = await RemoteTransport.ResolveAsync(address, options.AllowLoopbackForTests);
        // The peer must be reached through this PC's own tunnel: a carrier-grade NAT network can route 100.64/10 over Wi-Fi.
        if (!Routed(target.Ip)) throw new InvalidOperationException(Locale.Get("remote.connect.error.notViaTailscale"));
        var data = await RemoteTransport.RequestAsync(target, key, HttpMethod.Get, RemoteProtocol.PathPrefix + "info");
        RemoteWireInfo? info;
        try { info = JsonSerializer.Deserialize<RemoteWireInfo>(data, RemoteWire.Json); }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException or InvalidOperationException) { throw new InvalidDataException(Locale.Get("remote.connect.error.notMighty")); }
        RemoteWorkspaceValidation.Info(info);
        return (target, info!);
    }

    /// <summary>Before each request to the host: the route still goes through the tunnel, or the connection is lost (never retried).</summary>
    private async Task EnsureRouteAsync(string connectionId, RemoteTarget target)
    {
        if (Routed(target.Ip)) return;
        await FailAsync(connectionId, Locale.Get("remote.connect.error.notViaTailscale"));
        throw new InvalidOperationException(Locale.Get("remote.connect.error.notViaTailscale"));
    }

    /// <summary>The network or the host failed: the connection shows the error and its runs end in error.</summary>
    private async Task FailAsync(string id, string message)
    {
        lock (gate)
            if (connections.TryGetValue(id, out var c)) { c.Revision = Guid.NewGuid(); c.Target = null; c.Info = c.Info with { Status = RemoteConnectionStatus.Disconnected, Detail = Prefix(message) }; }
        await DropRunsAsync(id, message);
        Changed?.Invoke();
    }

    /// <summary>At most 300 UTF-16 units, never ending inside a surrogate pair.</summary>
    private static string Prefix(string message)
    {
        if (message.Length <= 300) return message;
        var cut = message[..300];
        return char.IsHighSurrogate(cut[^1]) ? cut[..^1] : cut;
    }

    /// <summary>
    /// The saved connections, read once on first use: RemoteConnectionFile.Read keeps the file's first 16 entries whose id is good and
    /// whose address parses to one no earlier entry has (the Mac's loadSaved); the address is parsed again here so a list built any
    /// other way still holds one connection per shown address. Keys stay in the vault until a connection is used.
    /// </summary>
    private void LoadSaved()
    {
        lock (gate)
        {
            if (loaded) return;
            loaded = true;
            foreach (var item in RemoteConnectionFile.Read(dataDirectory))
            {
                RemoteAddress address;
                try { address = RemoteAddress.Parse(item.Address); } catch (ArgumentException) { continue; }
                if (connections.ContainsKey(item.Id) || connections.Values.Any(c => c.Info.Address == address.Display)) continue;
                connections[item.Id] = new Entry(new(item.Id, item.Name, address.Display, Detail: Locale.Get("remote.connect.detail.saved"))) { KeyPending = true };
                order.Add(item.Id);
            }
        }
    }

    /// <summary>Writes id, name and address of every connection, never a key. Writes go one at a time, each taking the list when its turn comes, so the last one always holds the latest list.</summary>
    private async Task WriteSavedAsync()
    {
        await writes.WaitAsync();
        try
        {
            List<RemoteSavedConnection> saved; lock (gate) saved = order.Select(id => new RemoteSavedConnection(id, connections[id].Info.Name, connections[id].Info.Address)).ToList();
            // The connection still works for this launch when the file cannot be written; only its listing is not kept.
            try { await RemoteConnectionFile.WriteAsync(dataDirectory, saved); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        finally { writes.Release(); }
    }
}
