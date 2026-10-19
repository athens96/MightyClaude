using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace MightyClaude.Core;

public sealed record RemoteHostOptions
{
    /// <summary>The saved workspaces; only local ones the user picked are ever shared.</summary>
    public required Func<IReadOnlyList<Workspace>> Workspaces { get; init; }
    /// <summary>A registered local workspace whose folder exists (StateStore.ResolveLocalAsync), or why not.</summary>
    public required Func<string, Task<Workspace>> ResolveLocal { get; init; }
    /// <summary>This PC's CLIs as /info reports them, with the app's version and platform "win32".</summary>
    public required Func<Task<RuntimeInfo>> Runtime { get; init; }
    /// <summary>The runner client runs go through: no pane MCP, no delegation, no picture cache.</summary>
    public required Func<RemoteRunnerSinks, IRemoteRunner> CreateRunner { get; init; }
    public required string HostName { get; init; }
    /// <summary>Why this PC cannot start a client's run now (its CLIs or plugins are changing); null when it can.</summary>
    public Func<StartRunRequest, string?> Admission { get; init; } = _ => null;
    /// <summary>Told how many client runs go on this PC whenever that changes: while any does, CLI updates wait.</summary>
    public Action<int> RunsChanged { get; init; } = _ => { };
    /// <summary>Loopback stands in for Tailscale; only tests and the smoke run set it, in code.</summary>
    public bool AllowLoopbackForTests { get; init; }
    public TimeSpan Heartbeat { get; init; } = RemoteProtocol.HeartbeatTimeout;
    /// <summary>The addresses to listen on instead of this PC's Tailscale addresses (checks only); any that is not a Tailscale address
    /// (or loopback, when injected) is still left out.</summary>
    public Func<IReadOnlyList<string>>? Addresses { get; init; }
    public Func<DateTimeOffset> Clock { get; init; } = () => DateTimeOffset.UtcNow;
}

/// <summary>
/// A client polls each of its runs at most ten times a second. Each peer has its own budget of 300 a second; only requests with
/// the key count toward the shared 600, so a device without it cannot crowd the real client out. At most 64 peers are remembered.
/// </summary>
public sealed class RemoteRateLimiter(Func<DateTimeOffset> clock)
{
    private readonly Dictionary<string, (DateTimeOffset Start, int Count)> peers = new(StringComparer.Ordinal);
    private DateTimeOffset window = DateTimeOffset.MinValue; private int keyed;

    public bool AllowPeer(string peer)
    {
        lock (peers)
        {
            var now = clock();
            var entry = peers.TryGetValue(peer, out var known) ? known : (Start: now, Count: 0);
            if (now - entry.Start >= TimeSpan.FromSeconds(1)) entry = (now, 0);
            entry.Count++;
            if (!peers.ContainsKey(peer) && peers.Count >= 64) peers.Clear();
            peers[peer] = entry;
            return entry.Count <= 300;
        }
    }

    public bool AllowKeyed()
    {
        lock (peers)
        {
            var now = clock();
            if (now - window >= TimeSpan.FromSeconds(1)) { window = now; keyed = 0; }
            return ++keyed <= 600;
        }
    }
}

/// <summary>
/// "이 컴퓨터 공유" (M/Remote/RemoteHostService.swift): serves the chosen local workspaces to other Mighty Claude apps on this PC's
/// Tailscale addresses only. Runs go through this PC's own RunManager with its CLIs and sign-ins; their events wait in a per-run buffer
/// the client reads by cursor. A run its client stops polling for the heartbeat is stopped. Sharing never survives the app; stopping
/// it revokes the key, which lives in memory only.
/// </summary>
public sealed partial class RemoteHostService(RemoteHostOptions options) : IAsyncDisposable
{
    private sealed class HostFailure(int status, string message) : Exception(message) { public int Status { get; } = status; }
    private static readonly IReadOnlyDictionary<string, string> VersionHeaders = new Dictionary<string, string> { [RemoteProtocol.Header] = RemoteProtocol.Version.ToString() };
    /// <summary>A target the Mac's URLComponents(string:) reads: URL characters only, no fragment.</summary>
    private static readonly Regex TargetText = new(@"^/[A-Za-z0-9\-._~!$&'()*+,;=:@/%]*(\?[A-Za-z0-9\-._~!$&'()*+,;=:@/?%]*)?\z", RegexOptions.CultureInvariant);
    /// <summary>How long /info waits for this PC's CLI details; the listener would abandon the request at 60 s.</summary>
    private static readonly TimeSpan RuntimeDeadline = TimeSpan.FromSeconds(30);

    private readonly object gate = new();
    /// <summary>Serializes AnnounceRuns, so a count read earlier is never told after one read later.</summary>
    private readonly object announcing = new();
    private readonly string hostId = Wire.Id();
    private readonly RemoteRateLimiter rates = new(options.Clock);
    private readonly List<RemoteHttpListener> servers = [];
    private RemoteHostState state = new();
    private IRemoteRunner? runner;
    private Guid generation = Guid.NewGuid();
    private CancellationTokenSource? lease;
    private bool changing, closed, allowsFullAccess;

    /// <summary>Awaited between making each address's listener and recording it (checks only): a stop let in here must still close what the start opens.</summary>
    internal Func<string, Task>? BeforeBind { get; init; }

    /// <summary>The listeners of the sharing now on, one per address (checks only).</summary>
    internal IReadOnlyList<RemoteHttpListener> Listeners { get { lock (gate) return [.. servers]; } }

    /// <summary>What Settings shows. A listener that has stopped accepting for good ends the whole sharing first, so it never shows as on.</summary>
    public RemoteHostState Current()
    {
        StopIfListenerFailed();
        lock (gate) return state with { ActiveRuns = jobs.Values.Count(j => !j.Done), AllowsFullAccess = allowsFullAccess };
    }

    /// <summary>Whether clients may run commands and full-access agents here; takes effect from the next run they start.</summary>
    public RemoteHostState SetAllowsFullAccess(bool allowed) { lock (gate) allowsFullAccess = allowed; return Current(); }

    /// <summary>What a client may not run here while full access is off: a command pane, or an agent in full access; null when it may.</summary>
    public static string? FullAccessRefusal(StartRunRequest request, bool allowed) =>
        allowed ? null : request.Kind == "shell" ? Locale.Get("remote.host.error.commandsOff") : request.Settings?.PermissionMode == "fullAccess" ? Locale.Get("remote.host.error.fullAccessOff") : null;

    /// <summary>Starts sharing <paramref name="workspaceIds"/> (registered local workspaces) on every Tailscale address this PC has, with a new key.</summary>
    public async Task<RemoteHostState> StartAsync(IReadOnlyList<string> workspaceIds, int port = RemoteProtocol.DefaultPort)
    {
        Guid revision;
        lock (gate)
        {
            if (changing || state.Enabled || servers.Count > 0) throw new InvalidOperationException(Locale.Get("remote.host.error.busy"));
            if (workspaceIds.Count is 0 or > RemoteProtocol.MaximumSharedWorkspaces || workspaceIds.Distinct(StringComparer.Ordinal).Count() != workspaceIds.Count || !workspaceIds.All(Wire.Identifier))
                throw new ArgumentException(Locale.Get("remote.host.error.pickWorkspace"));
            if (!(port is >= 1024 and <= 65535 || options.AllowLoopbackForTests && port == 0)) throw new ArgumentException(Locale.Get("remote.connect.error.badPort"));
            if (closed) throw new InvalidOperationException(Locale.Get("common.appClosing"));
            // Taken before the first wait: a stop meanwhile changes it and ends this start.
            changing = true; revision = generation = Guid.NewGuid();
        }
        var binding = false;
        try
        {
            var saved = options.Workspaces();
            foreach (var id in workspaceIds)
            {
                if (!saved.Any(w => w.Id == id && w.Remote is null)) throw new InvalidOperationException(RemoteWindowsStrings.HostLocalOnly);
                await options.ResolveLocal(id);
            }
            Checked(revision);
            var addresses = ListenAddresses();
            if (addresses.Count == 0) throw new InvalidOperationException(RemoteWindowsStrings.HostNoTailscale);
            string key;
            try { key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_'); }
            catch (CryptographicException) { throw new InvalidOperationException(Locale.Get("remote.host.error.keyFailed")); }
            var created = options.CreateRunner(new(value => Receive(value, revision), value => ReceivePermission(value, revision), (id, node) => ReceiveGraph(id, node, revision), id => ResolveShared(id, revision)));
            bool current;
            lock (gate) { current = revision == generation && !closed; if (current) runner = created; }
            if (!current) { await created.DisposeAsync(); throw new OperationCanceledException(); }
            var bound = port;
            binding = true;
            foreach (var address in addresses)
            {
                // Refused at accept: anything but a Tailscale peer, and a ninth connection from one peer. Windows cannot pin a listener
                // to the tunnel's interface as the Mac does, so only Tailscale addresses are bound and every peer is filtered.
                // Made before the check (its constructor opens no socket).
                var server = new RemoteHttpListener(IPAddress.Parse(address), bound, request => ServeAsync(request, revision), head => BodyLimit(head, key, options.AllowLoopbackForTests),
                    peer => RemotePeerPolicy.Allowed(peer, options.AllowLoopbackForTests), RemoteProtocol.MaximumConnectionsPerPeer);
                // The window a check-then-add in two steps would leave open; the checks hold a stop here.
                if (BeforeBind is { } hook) await hook(address);
                // Recorded under the same lock as the check, so a stop meanwhile always closes it; one refused here was never started.
                bool refused;
                lock (gate) { refused = revision != generation || closed; if (!refused) servers.Add(server); }
                if (refused) { await server.DisposeAsync(); throw new OperationCanceledException(); }
                bound = server.Start();
            }
            binding = false;
            lock (gate)
            {
                if (revision != generation || closed) throw new OperationCanceledException();
                state = new RemoteHostState { Enabled = true, Key = key, Port = bound, WorkspaceIds = [.. workspaceIds], Addresses = addresses.Select(a => new RemoteAddress(a, bound).Display).ToList() };
                allowsFullAccess = false;
                lease = new CancellationTokenSource();
                _ = LeaseLoopAsync(revision, lease.Token);
            }
            return Current();
        }
        catch (Exception ex)
        {
            bool mine; lock (gate) mine = revision == generation;
            // A stop that came in meanwhile already closed what this start had opened.
            if (mine) await StopNowAsync(revision);
            if (ex is OperationCanceledException || binding && !mine) throw new InvalidOperationException(Locale.Get("remote.host.error.startCancelled"));
            if (binding) throw new InvalidOperationException(Locale.Get("remote.host.error.listenFailed", new Dictionary<string, string> { ["error"] = ex.Message }));
            throw;
        }
        finally { lock (gate) changing = false; }
    }

    private void Checked(Guid revision) { lock (gate) if (revision != generation || closed) throw new OperationCanceledException(); }

    /// <summary>This PC's Tailscale addresses (or the injected ones), each once; never any other address.</summary>
    private List<string> ListenAddresses()
    {
        IReadOnlyList<string> found;
        try { found = options.Addresses?.Invoke() ?? (options.AllowLoopbackForTests ? ["127.0.0.1"] : TailscaleAddresses.Current()); }
        catch (AdaptersUnreadableException ex)
        {
            Trace.TraceWarning("remote host: " + ex.GetBaseException().Message);
            throw new InvalidOperationException(RemoteWindowsStrings.HostNoTailscale);
        }
        return found.Where(a => RemotePeerPolicy.Allowed(a, options.AllowLoopbackForTests)).Select(TailscaleAddresses.Canonical).Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>Stops sharing: every remote run here stops and the key is gone for good.</summary>
    public async Task<RemoteHostState> StopAsync() { await StopNowAsync(); return Current(); }

    /// <summary>Ends the sharing (only <paramref name="only"/>'s, when given): the state, key and switch are gone before the first wait.</summary>
    private async Task StopNowAsync(Guid? only = null)
    {
        List<RemoteHttpListener> old; IRemoteRunner? oldRunner; CancellationTokenSource? oldLease; List<CancellationTokenSource> launches;
        lock (gate)
        {
            if (only is { } expected && expected != generation) return;
            generation = Guid.NewGuid(); old = [.. servers]; servers.Clear(); oldRunner = runner; runner = null;
            state = new(); allowsFullAccess = false; oldLease = lease; lease = null;
            launches = jobs.Values.Select(j => j.Cancel).ToList(); jobs.Clear();
        }
        oldLease?.Cancel();
        // A run not handed to the runner yet never is; the runner's own runs end with its disposal below.
        foreach (var launch in launches) launch.Cancel();
        try
        {
            // Every live connection is reset, and a request still being handled answers 503: its generation is gone.
            foreach (var server in old) await server.DisposeAsync();
            if (oldRunner is not null) await oldRunner.DisposeAsync();
        }
        finally { AnnounceRuns(); }
    }

    /// <summary>
    /// Tells the app how many client runs go on now. Serialized and read at the moment it is told, so a stop's count can never land after a
    /// newer one. Never called while holding <c>gate</c>; Task 14's runs announce through this same helper.
    /// </summary>
    private void AnnounceRuns()
    {
        lock (announcing)
        {
            int count; lock (gate) count = jobs.Values.Count(j => !j.Done);
            try { options.RunsChanged(count); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { Trace.TraceError("remote host: telling the app about runs failed: " + ex.GetType().Name); }
        }
    }

    /// <summary>
    /// A listener that stopped accepting for good (RemoteHttpListener.IsListening) ends the whole sharing, so it never shows as on while
    /// an address is dead: the key is revoked and the other addresses close. True when it did.
    /// </summary>
    private bool StopIfListenerFailed()
    {
        Guid revision;
        lock (gate) { if (!state.Enabled || servers.All(s => s.IsListening)) return false; revision = generation; }
        _ = StopNowAsync(revision).ContinueWith(static t => Trace.TraceError("remote host: stopping after a listener failed: " + t.Exception!.GetBaseException().GetType().Name),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return true;
    }

    public async ValueTask DisposeAsync() { lock (gate) closed = true; await StopNowAsync(); }

    /// <summary>
    /// How much body is read, decided from the head before any of it: none without the key (the head alone gets its 401), the larger
    /// attachment body only for an authenticated run request.
    /// </summary>
    public static int BodyLimit(RemoteHttpRequest head, string key, bool allowLoopback)
    {
        if (head.Headers.ContainsKey("origin") || !RemotePeerPolicy.Allowed(head.RemoteAddress, allowLoopback) || head.Headers.GetValueOrDefault(RemoteProtocol.Header) != RemoteProtocol.Version.ToString()
            || !KeyPresented(head.Headers, key)) return 0;
        return head.Method == "POST" && head.Target == RemoteProtocol.PathPrefix + "runs" ? AttachmentSupport.MaximumRequestBytes : RemoteProtocol.MaximumRequestBytes;
    }

    /// <summary>The request's bearer key is the sharing's (at most 256 characters in all, compared in constant time).</summary>
    private static bool KeyPresented(IReadOnlyDictionary<string, string> headers, string key)
    {
        var authorization = headers.GetValueOrDefault("authorization") ?? "";
        return authorization.StartsWith("Bearer ", StringComparison.Ordinal) && authorization.Length <= 256 && RemoteWorkspaceValidation.KeysMatch(authorization[7..], key);
    }

    /// <summary>Every request: sharing on, no browser origin, a Tailscale peer, within the rate limits, the right key (compared in constant
    /// time), the protocol version.</summary>
    private void Authorize(RemoteHttpRequest request, Guid revision)
    {
        string key;
        lock (gate)
        {
            if (!state.Enabled || revision != generation || state.Key is not { } current || !servers.All(s => s.IsListening)) throw new HostFailure(503, Locale.Get("remote.host.error.stopped"));
            key = current;
        }
        if (request.Headers.ContainsKey("origin") || !RemotePeerPolicy.Allowed(request.RemoteAddress, options.AllowLoopbackForTests)) throw new HostFailure(403, Locale.Get("remote.host.error.tailscaleOnly"));
        if (!rates.AllowPeer(request.RemoteAddress)) throw new HostFailure(429, Locale.Get("remote.host.error.tooMany"));
        if (!KeyPresented(request.Headers, key)) throw new HostFailure(401, Locale.Get("remote.host.error.badKey"));
        if (!rates.AllowKeyed()) throw new HostFailure(429, Locale.Get("remote.host.error.tooMany"));
        if (request.Headers.GetValueOrDefault(RemoteProtocol.Header) != RemoteProtocol.Version.ToString()) throw new HostFailure(426, Locale.Get("remote.host.error.version"));
    }

    private static RemoteHttpResponse Reply(int status, object value) => RemoteHttpResponse.Json(status, value, VersionHeaders);
    private static RemoteHttpResponse Failure(int status, string message) => Reply(status, new RemoteWireError(RemoteProtocol.Version, RemoteText.Cut(message, 1000)));

    private async Task<RemoteHttpResponse> ServeAsync(RemoteHttpRequest request, Guid revision)
    {
        try
        {
            Authorize(request, revision);
            // The path is read as written, as the Mac reads it: no dot segment is resolved and no escape decoded.
            var mark = request.Target.IndexOf('?');
            var path = mark < 0 ? request.Target : request.Target[..mark];
            var query = mark < 0 ? "" : request.Target[mark..];
            if (!TargetText.IsMatch(request.Target) || !path.StartsWith(RemoteProtocol.PathPrefix, StringComparison.Ordinal)) throw new HostFailure(400, Locale.Get("remote.host.error.badPath"));
            var route = path[RemoteProtocol.PathPrefix.Length..].Split('/');
            if (request.Method == "GET" && route is ["info"] && query.Length == 0) return await InfoAsync(revision);
            return await ServeRunsAsync(request, route, query, revision);
        }
        catch (HostFailure failure) { return Failure(failure.Status, failure.Message); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return Failure(500, ex.Message); }
    }

    private List<Workspace> SharedWorkspaces()
    {
        IReadOnlyList<string> ids; lock (gate) ids = state.WorkspaceIds;
        return options.Workspaces().Where(w => w.Remote is null && ids.Contains(w.Id) && RemoteWorkspaceValidation.SharedWorkspace(new(w.Id, w.Name, w.Path))).ToList();
    }

    private async Task<RemoteHttpResponse> InfoAsync(Guid revision)
    {
        var shared = SharedWorkspaces();
        RuntimeInfo runtime;
        try { runtime = await options.Runtime().WaitAsync(RuntimeDeadline); }
        catch (TimeoutException) { throw new HostFailure(503, RemoteWindowsStrings.Timeout); }
        lock (gate) if (revision != generation) throw new HostFailure(503, Locale.Get("remote.host.error.stopped"));
        var name = RemoteText.Cut(options.HostName, 120);
        return Reply(200, new RemoteWireInfo(RemoteProtocol.Version, hostId, name.Length == 0 ? "Windows" : name, shared.Select(w => new RemoteSharedWorkspace(w.Id, w.Name, w.Path)).ToList(), runtime));
    }
}
