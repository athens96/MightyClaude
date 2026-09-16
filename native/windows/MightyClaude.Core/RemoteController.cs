using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace MightyClaude.Core;

public sealed class RemoteController : IAsyncDisposable
{
    private sealed class Connection(RemoteConnectionInfo info, string? token) { internal RemoteConnectionInfo Info = info; internal string? Token = token; internal PinnedRemote? Pinned; }
    private sealed class Run(string id, string connectionId)
    {
        internal readonly string Id = id, ConnectionId = connectionId;
        internal readonly CancellationTokenSource Cancel = new();
        internal string? JobId;
        internal PinnedRemote? Target;
        internal string? Token;
        internal Task Work = Task.CompletedTask;
        internal TaskCompletionSource? Accepted;
        internal bool Terminal;
    }
    private sealed record SavedConnection(string Id, string Name, string Address, string? EncryptedToken);
    private sealed record SavedConnections(int Version, List<SavedConnection> Connections);
    private readonly string directory;
    private readonly string? legacyDirectory;
    private readonly Func<List<Workspace>> list;
    private readonly Func<string, Task<Workspace>> resolve;
    private readonly Func<Task<RuntimeInfo>> runtime;
    private readonly Func<Action<RunEvent>, IRunManager> factory;
    private readonly Action<RunEvent> emit;
    private readonly ISecretProtector? protector;
    private readonly bool testLoopback;
    private readonly Func<CancellationToken, Task<TailscaleInfo>> discover;
    private readonly SemaphoreSlim gate = new(1);
    private readonly ConcurrentDictionary<string, Connection> connections = [];
    private readonly ConcurrentDictionary<string, Run> runs = [];
    private readonly object runLifecycle = new();
    private readonly CancellationTokenSource closing = new();
    private RemoteServer? host;
    private string[] shared = [];
    private bool loaded, disposed;
    private TailscaleInfo tailscale = new(false, [], null, "Tailscale 상태를 확인하세요.");
    private DateTimeOffset discovered;
    public RemoteController(string directory, Func<List<Workspace>> list, Func<string, Task<Workspace>> resolve, Func<Task<RuntimeInfo>> runtime, Func<Action<RunEvent>, IRunManager> factory, Action<RunEvent> emit, ISecretProtector? protector = null, bool testLoopback = false, Func<CancellationToken, Task<TailscaleInfo>>? discover = null, string? legacyDirectory = null)
    { this.directory = directory; this.legacyDirectory = legacyDirectory; this.list = list; this.resolve = resolve; this.runtime = runtime; this.factory = factory; this.emit = emit; this.protector = protector; this.testLoopback = testLoopback; this.discover = discover ?? RemoteNetwork.DiscoverTailscaleAsync; }
    public bool IsRemoteRun(string id) => runs.ContainsKey(id);
    private async Task LoadAsync()
    {
        if (loaded) return; loaded = true;
        var path = Path.Combine(directory, "remote-connections.json"); var importing = !File.Exists(path) && legacyDirectory is not null;
        if (importing) path = Path.Combine(legacyDirectory!, "remote-connections.json");
        if (!File.Exists(path) || new FileInfo(path).Length > 512 * 1024) return;
        try
        {
            var saved = JsonSerializer.Deserialize<SavedConnections>(await File.ReadAllTextAsync(path), Wire.Json);
            if (saved?.Version != 1) return;
            foreach (var row in (saved.Connections ?? []).Take(32))
            {
                if (!Wire.Identifier(row.Id)) continue;
                try
                {
                    var address = RemoteNetwork.Parse(row.Address).GetLeftPart(UriPartial.Authority); string? token = null;
                    if (!importing && protector is not null && row.EncryptedToken is { Length: < 32768 } encoded) { try { token = protector.Unprotect(Convert.FromBase64String(encoded)); } catch (Exception ex) when (ex is CryptographicException or FormatException or System.ComponentModel.Win32Exception) { } }
                    connections[row.Id] = new(new(row.Id, Wire.Clean(row.Name, 120), address, "disconnected", Detail: RemoteNetwork.ValidToken(token) ? "저장된 연결입니다. 새로고침하면 연결합니다." : "연결 키를 다시 입력하세요."), RemoteNetwork.ValidToken(token) ? token : null);
                }
                catch (ArgumentException) { }
            }
            if (importing) await SaveAsync(); // Preserve connection IDs; Electron ciphertext is never decrypted or copied.
        }
        catch (Exception ex) when (ex is IOException or JsonException) { }
    }
    private async Task SaveAsync()
    {
        var rows = new List<SavedConnection>();
        foreach (var connection in connections.Values.Take(32))
        {
            string? encrypted = null;
            if (protector is not null && connection.Token is { } token) { try { encrypted = Convert.ToBase64String(protector.Protect(token)); } catch (Exception ex) when (ex is CryptographicException or System.ComponentModel.Win32Exception or PlatformNotSupportedException) { connection.Info = connection.Info with { Detail = "키 암호화를 사용할 수 없어 이번 실행 중에만 연결 키를 보관합니다." }; } }
            rows.Add(new(connection.Info.Id, connection.Info.Name, connection.Info.Address, encrypted));
        }
        await StateStore.AtomicWriteAsync(Path.Combine(directory, "remote-connections.json"), JsonSerializer.SerializeToUtf8Bytes(new SavedConnections(1, rows), Wire.Json));
    }
    private async Task DiscoverAsync(bool force = false)
    {
        if (!force && DateTimeOffset.UtcNow - discovered < TimeSpan.FromSeconds(5)) return;
        tailscale = testLoopback ? new(true, ["127.0.0.1"], "Loopback fixture", "테스트 전용 loopback") : await discover(closing.Token);
        discovered = DateTimeOffset.UtcNow;
    }
    private RemoteState State() => Wire.Clone(new RemoteState(tailscale, host is null ? new(false, Detail: "공유 꺼짐") : new(true, host.Address, host.Token, host.Port, shared, host.ActiveRuns, "연결 키 소유자는 선택한 폴더에서 CLI와 shell을 실행할 수 있습니다."), connections.Values.Select(c => c.Info).OrderBy(c => c.Name).ToList()));
    public async Task<RemoteState> GetStateAsync()
    {
        await gate.WaitAsync(closing.Token); try { await LoadAsync(); await DiscoverAsync(); return State(); } finally { gate.Release(); }
    }
    public async Task<RemoteState> StartSharingAsync(ShareRequest request)
    {
        if (request.WorkspaceIds is not { Length: > 0 and <= 64 } || request.WorkspaceIds.Any(id => !Wire.Identifier(id)) || request.WorkspaceIds.Distinct().Count() != request.WorkspaceIds.Length || request.Port is < 1 or > 65535 && !(testLoopback && request.Port == 0)) throw new ArgumentException("공유 폴더와 포트가 올바르지 않습니다.");
        await gate.WaitAsync(closing.Token);
        try
        {
            await LoadAsync(); await DiscoverAsync(true);
            if (!tailscale.Available || tailscale.Addresses.Length == 0) throw new InvalidOperationException(tailscale.Detail);
            foreach (var id in request.WorkspaceIds) { if (!list().Any(w => w.Id == id && w.Remote is null)) throw new ArgumentException("등록한 로컬 워크스페이스만 공유할 수 있습니다."); await resolve(id); }
            if (host is not null) await host.DisposeAsync(); host = null;
            var server = new RemoteServer(tailscale.DeviceName ?? Environment.MachineName, request.WorkspaceIds, list, resolve, runtime, factory, testLoopback);
            try { await server.StartAsync(IPAddress.Parse(tailscale.Addresses.FirstOrDefault(ip => IPAddress.Parse(ip).AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) ?? tailscale.Addresses[0]), request.Port ?? RemoteNetwork.DefaultPort); closing.Token.ThrowIfCancellationRequested(); host = server; shared = request.WorkspaceIds; }
            catch { await server.DisposeAsync(); throw; }
            return State();
        }
        finally { gate.Release(); }
    }
    public async Task<RemoteState> StopSharingAsync()
    {
        await gate.WaitAsync(closing.Token); try { if (host is not null) await host.DisposeAsync(); host = null; shared = []; return State(); } finally { gate.Release(); }
    }
    public async Task<RemoteState> ConnectAsync(ConnectRemoteRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 120 || !RemoteNetwork.ValidToken(request.Token)) throw new ArgumentException("연결 이름과 256비트 연결 키를 확인하세요.");
        var address = RemoteNetwork.Parse(request.Address).GetLeftPart(UriPartial.Authority);
        await gate.WaitAsync(closing.Token);
        try
        {
            await LoadAsync(); await DiscoverAsync(true);
            var pinned = await RemoteNetwork.PinAsync(address, tailscale, testLoopback, token: closing.Token);
            var info = await ReadInfoAsync(pinned, request.Token, closing.Token); closing.Token.ThrowIfCancellationRequested();
            var connection = connections.Values.FirstOrDefault(c => c.Info.Address == address);
            if (connection is null && connections.Count >= 32) throw new InvalidOperationException("연결은 32개까지 저장할 수 있습니다.");
            var id = connection?.Info.Id ?? Wire.Id();
            if (connection is not null) await StopConnectionRunsAsync(id);
            connection = new(new(id, Wire.Clean(request.Name, 120), address, "connected", info.HostId, info.HostName, info.Workspaces, info.Runtime, protector is null ? "연결됨 · 키는 앱 종료 시 삭제됩니다." : "연결됨"), request.Token) { Pinned = pinned };
            connections[id] = connection; await SaveAsync(); return State();
        }
        finally { gate.Release(); }
    }
    public async Task<RemoteState> RefreshAsync(string id)
    {
        await gate.WaitAsync(closing.Token);
        try
        {
            await LoadAsync(); var connection = GetConnection(id);
            if (connection.Token is null) throw new InvalidOperationException("현재 연결 키를 다시 입력하세요.");
            try
            {
                await DiscoverAsync(true); var pinned = await RemoteNetwork.PinAsync(connection.Info.Address, tailscale, testLoopback, token: closing.Token);
                var info = await ReadInfoAsync(pinned, connection.Token, closing.Token); closing.Token.ThrowIfCancellationRequested();
                connection.Pinned = pinned; connection.Info = connection.Info with { Status = "connected", HostId = info.HostId, HostName = info.HostName, Workspaces = info.Workspaces, Runtime = info.Runtime, Detail = "연결됨" };
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or OperationCanceledException or System.Net.Http.HttpRequestException or System.Net.Sockets.SocketException) { connection.Info = connection.Info with { Status = "error", Detail = Wire.Clean(ex.Message, 400) }; await StopConnectionRunsAsync(id); }
            return State();
        }
        finally { gate.Release(); }
    }
    public async Task<RemoteState> DisconnectAsync(string id)
    {
        await gate.WaitAsync(closing.Token); try { await LoadAsync(); var connection = GetConnection(id); connection.Info = connection.Info with { Status = "disconnected", Detail = "연결 해제됨. 새로고침하면 다시 연결합니다." }; await StopConnectionRunsAsync(id); return State(); } finally { gate.Release(); }
    }
    private Connection GetConnection(string id) => Wire.Identifier(id) && connections.TryGetValue(id, out var c) ? c : throw new ArgumentException("저장된 원격 연결이 없습니다.");
    public Workspace GetRemoteWorkspace(string connectionId, string workspaceId)
    {
        var connection = GetConnection(connectionId);
        if (connection.Info.Status != "connected") throw new InvalidOperationException("원격 연결을 먼저 새로고침하세요.");
        return connection.Info.Workspaces?.FirstOrDefault(w => w.Id == workspaceId) ?? throw new ArgumentException("호스트가 공유한 워크스페이스가 아닙니다.");
    }
    public Task StartRunAsync(StartRunRequest request, Workspace workspace)
    {
        request = request.Validate();
        if (workspace.Remote is not { } remote || request.WorkspaceId != workspace.Id) throw new ArgumentException("원격 워크스페이스가 필요합니다.");
        if (request.Kind == "claude" && ProviderCatalog.RemoteSettingsProblem(request.Settings!, GetConnection(remote.ConnectionId).Info.Runtime?.Providers?.FirstOrDefault(p => p.Id == request.Provider)?.Capabilities) is { } problem) throw new InvalidOperationException(problem);
        if (request.Attachments is { Count: > 0 } && GetConnection(remote.ConnectionId).Info.Runtime?.Providers?.FirstOrDefault(p => p.Id == request.Provider)?.Capabilities.Attachments != true) throw new InvalidOperationException("원격 호스트가 첨부 파일을 지원하지 않습니다. 호스트 앱을 업데이트하세요.");
        lock (runLifecycle)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var run = new Run(request.SessionId, remote.ConnectionId) { Accepted = request.Attachments is { Count: > 0 } ? new(TaskCreationOptions.RunContinuationsAsynchronously) : null };
            if (runs.Count >= 16 || !runs.TryAdd(run.Id, run)) throw new InvalidOperationException("이미 실행 중이거나 동시 실행 한도에 도달했습니다.");
            run.Work = RunAsync(run, request, remote); return run.Accepted?.Task ?? Task.CompletedTask;
        }
    }
    private async Task RunAsync(Run run, StartRunRequest request, RemoteReference remote)
    {
        try
        {
            var connection = GetConnection(run.ConnectionId); GetRemoteWorkspace(run.ConnectionId, remote.WorkspaceId);
            if (connection.Pinned is null || connection.Token is null) throw new InvalidOperationException("원격 연결을 먼저 새로고침하세요.");
            run.Target = connection.Pinned; run.Token = connection.Token; run.Cancel.Token.ThrowIfCancellationRequested();
            using (var start = await RemoteNetwork.RequestAsync(run.Target, run.Token, HttpMethod.Post, "/v1/runs", new { request = request with { WorkspaceId = remote.WorkspaceId } }, run.Cancel.Token, timeoutSeconds: request.Attachments is { Count: > 0 } ? 60 : 10))
            { run.JobId = start.RootElement.Text("jobId"); if (!Wire.Identifier(run.JobId)) throw new InvalidDataException("원격 작업 ID가 올바르지 않습니다."); }
            run.Cancel.Token.ThrowIfCancellationRequested(); run.Accepted?.TrySetResult(); long cursor = 0;
            while (true)
            {
                using var json = await RemoteNetwork.RequestAsync(run.Target, run.Token, HttpMethod.Get, $"/v1/runs/{run.JobId}/events?cursor={cursor}", cancellation: run.Cancel.Token);
                var poll = json.RootElement.Deserialize<WirePoll>(Wire.Json) ?? throw new InvalidDataException("원격 출력 응답이 없습니다.");
                if (poll.Events is null || poll.Events.Count > 100 || poll.Cursor != (poll.Events.LastOrDefault()?.Cursor ?? cursor) || poll.Cursor < cursor || poll.LastCursor < poll.Cursor || poll.Events.Any(e => e.Cursor <= cursor || e.Cursor > poll.Cursor || e.Event is null || e.Event.SessionId != run.JobId || !e.Event.Valid()) || poll.Events.Select(e => e.Cursor).Distinct().Count() != poll.Events.Count || !poll.Events.Select(e => e.Cursor).SequenceEqual(poll.Events.Select(e => e.Cursor).Order())) throw new InvalidDataException("원격 출력 위치가 올바르지 않습니다.");
                if (poll.Gap) emit(RunEvent.Log(run.Id, "system", "호스트의 출력 보관 한도를 넘어 일부 이전 출력을 생략했습니다."));
                foreach (var item in poll.Events) { var value = item.Event with { SessionId = run.Id }; if (value.Type == "status" && value.Status is "completed" or "error" or "stopped") run.Terminal = true; emit(value); }
                cursor = poll.Cursor;
                if (poll.Done && cursor >= poll.LastCursor) { if (!run.Terminal) throw new InvalidDataException("원격 종료 상태를 확인하지 못했습니다."); break; }
                await Task.Delay(poll.LastCursor > cursor ? 20 : 500, run.Cancel.Token);
            }
        }
        catch (OperationCanceledException ex) { run.Accepted?.TrySetException(ex); if (!run.Terminal) emit(RunEvent.State(run.Id, "stopped")); }
        catch (Exception ex)
        {
            run.Accepted?.TrySetException(ex);
            if (connections.TryGetValue(run.ConnectionId, out var connection)) connection.Info = connection.Info with { Status = "error", Detail = Wire.Clean(ex.Message, 400) };
            emit(RunEvent.Log(run.Id, "error", "원격 연결: " + ex.Message)); if (!run.Terminal) emit(RunEvent.State(run.Id, "error"));
        }
        finally { if (!run.Terminal) await BestEffortStopAsync(run); runs.TryRemove(new KeyValuePair<string, Run>(run.Id, run)); }
    }
    private static async Task BestEffortStopAsync(Run run)
    {
        if (run.Target is null || run.Token is null || run.JobId is null) return;
        try { using var stop = await RemoteNetwork.RequestAsync(run.Target, run.Token, HttpMethod.Post, $"/v1/runs/{run.JobId}/stop", new { }, timeoutSeconds: 4); } catch (Exception ex) when (ex is IOException or OperationCanceledException or HttpRequestException or System.Net.Sockets.SocketException) { }
    }
    public async Task StopRunAsync(string id)
    {
        if (!runs.TryGetValue(id, out var run)) return; run.Cancel.Cancel(); await BestEffortStopAsync(run);
        try { await run.Work.WaitAsync(TimeSpan.FromSeconds(6)); } catch (TimeoutException) { }
    }
    private Task StopConnectionRunsAsync(string id) => Task.WhenAll(runs.Values.Where(r => r.ConnectionId == id).Select(r => StopRunAsync(r.Id)));
    private static async Task<WireInfo> ReadInfoAsync(PinnedRemote target, string token, CancellationToken cancellation)
    {
        using var json = await RemoteNetwork.RequestAsync(target, token, HttpMethod.Get, "/v1/info", cancellation: cancellation);
        var info = json.RootElement.Deserialize<WireInfo>(Wire.Json) ?? throw new InvalidDataException("호스트 정보가 없습니다.");
        if (!Wire.Identifier(info.HostId) || info.Workspaces is null || info.Workspaces.Count > 64 || info.Workspaces.Any(w => !StateStore.ValidWorkspace(w) || w.Remote is not null) || info.Workspaces.DistinctBy(w => w.Id).Count() != info.Workspaces.Count || info.Runtime is null || info.Runtime.Platform is not ("win32" or "darwin" or "linux" or "browser")) throw new InvalidDataException("호스트 정보가 올바르지 않습니다.");
        var providers = (info.Runtime.Providers ?? []).Where(p => p is not null && Wire.Providers.Contains(p.Id) && p.ModelCatalog is not null && p.Capabilities is not null).DistinctBy(p => p.Id).Take(3).Select(p => p with { Name = Wire.Clean(p.Name, 80), Detail = Wire.Clean(p.Detail, 2000), ModelCatalog = p.ModelCatalog with { Models = (p.ModelCatalog.Models ?? []).Where(m => m is not null && Wire.Model(m.Value)).DistinctBy(m => m.Value).Take(128).Select(m => m with { DisplayName = Wire.Clean(m.DisplayName, 160), Description = Wire.Clean(m.Description, 2400), SupportedEffortLevels = m.SupportedEffortLevels?.Where(Wire.Efforts.Contains).ToArray() }).ToList() } }).ToList();
        return info with { HostName = Wire.Clean(info.HostName, 120), Workspaces = info.Workspaces.Select(w => w with { Name = Wire.Clean(w.Name, 120) }).ToList(), Runtime = info.Runtime with { Providers = providers } };
    }
    public async ValueTask DisposeAsync()
    {
        lock (runLifecycle) { if (disposed) return; disposed = true; closing.Cancel(); }
        await gate.WaitAsync(); try { if (host is not null) await host.DisposeAsync(); host = null; } finally { gate.Release(); }
        await Task.WhenAll(runs.Keys.Select(StopRunAsync)); connections.Clear();
    }
}
