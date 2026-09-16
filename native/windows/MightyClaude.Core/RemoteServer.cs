using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace MightyClaude.Core;

public sealed record WireInfo(int Protocol, string HostId, string HostName, List<Workspace> Workspaces, RuntimeInfo Runtime);
public sealed record WireEvent(long Cursor, RunEvent Event);
public sealed record WirePoll(int Protocol, long Cursor, long LastCursor, bool Gap, bool Done, List<WireEvent> Events);

public sealed class RemoteServer : IAsyncDisposable
{
    private sealed class Job(string id)
    {
        internal readonly string Id = id;
        internal readonly List<WireEvent> Events = [];
        internal long Cursor;
        internal int Bytes;
        internal bool Done, Stopping;
        internal DateTimeOffset LastPoll = DateTimeOffset.UtcNow, Finished;
    }
    private readonly ConcurrentDictionary<string, Job> jobs = [];
    private readonly object admission = new();
    private readonly IRunManager manager;
    private readonly HashSet<string> allowed;
    private readonly Func<List<Workspace>> list;
    private readonly Func<string, Task<Workspace>> resolve;
    private readonly Func<Task<RuntimeInfo>> runtime;
    private readonly string name, id = Wire.Id();
    private readonly bool testLoopback;
    private readonly TimeSpan lease;
    private readonly CancellationTokenSource cancel = new();
    private HttpHost? server;
    private Task? monitor;
    private DateTimeOffset rateAt = DateTimeOffset.UtcNow;
    private int requests;
    private bool disposed;
    public string Token { get; } = RemoteNetwork.Token();
    public string Address => server?.Address.GetLeftPart(UriPartial.Authority) ?? "";
    public int Port => server?.Address.Port ?? 0;
    public int ActiveRuns => jobs.Values.Count(j => !j.Done);
    public RemoteServer(string name, string[] allowed, Func<List<Workspace>> list, Func<string, Task<Workspace>> resolve, Func<Task<RuntimeInfo>> runtime, Func<Action<RunEvent>, IRunManager> factory, bool testLoopback = false, TimeSpan? lease = null)
    { this.name = name; this.allowed = allowed.ToHashSet(); this.list = list; this.resolve = resolve; this.runtime = runtime; this.testLoopback = testLoopback; this.lease = lease ?? TimeSpan.FromSeconds(18); manager = factory(Emit); }
    public async Task StartAsync(IPAddress address, int port)
    {
        if (!RemoteNetwork.Allowed(address, testLoopback)) throw new ArgumentException("Tailscale 주소에만 공유할 수 있습니다.");
        server = await HttpHost.StartAsync(address, port, HandleAsync, cancel.Token, AttachmentSupport.MaximumRequestBytes);
        if (disposed) { await server.DisposeAsync(); throw new ObjectDisposedException(nameof(RemoteServer)); }
        monitor = MonitorAsync();
    }
    private async Task HandleAsync(HttpContext context)
    {
        try
        {
            if (disposed) { await Reply(503, "공유가 종료 중입니다."); return; }
            if (context.Request.Headers.ContainsKey("Origin") || context.Connection.RemoteIpAddress is not { } peer || !RemoteNetwork.Allowed(peer, testLoopback)) { await Reply(403, "Tailscale 앱 연결만 허용합니다."); return; }
            bool limited;
            lock (admission) { if (DateTimeOffset.UtcNow - rateAt > TimeSpan.FromSeconds(1)) { rateAt = DateTimeOffset.UtcNow; requests = 0; } limited = ++requests > 100; }
            if (limited) { await Reply(429, "요청이 너무 많습니다."); return; }
            if (!RemoteNetwork.Authenticate(context.Request.Headers.Authorization.ToString(), Token)) { await Reply(401, "연결 키가 올바르지 않습니다."); return; }
            if (context.Request.Headers[RemoteNetwork.VersionHeader] != "1") { await Reply(426, "원격 프로토콜 버전이 다릅니다."); return; }
            var path = context.Request.Path.Value ?? "";
            if (context.Request.Method == "GET" && path == "/v1/info" && !context.Request.QueryString.HasValue)
            { await HttpHost.ReplyAsync(context, 200, new WireInfo(1, id, name, list().Where(w => w.Remote is null && allowed.Contains(w.Id)).Take(64).ToList(), await runtime())); return; }
            if (context.Request.Method == "POST" && path == "/v1/runs" && !context.Request.QueryString.HasValue)
            {
                using var json = await HttpHost.ReadJsonAsync(context, AttachmentSupport.MaximumRequestBytes, 60);
                var request = json.RootElement.GetProperty("request").Deserialize<StartRunRequest>(Wire.Json)?.Validate() ?? throw new ArgumentException("실행 요청이 없습니다.");
                if (!allowed.Contains(request.WorkspaceId) || !list().Any(w => w.Id == request.WorkspaceId && w.Remote is null)) { await Reply(403, "공유한 로컬 워크스페이스만 실행할 수 있습니다."); return; }
                var workspace = await resolve(request.WorkspaceId);
                if (workspace.Remote is not null || workspace.Id != request.WorkspaceId) { await Reply(403, "로컬 폴더가 아닙니다."); return; }
                Job job;
                lock (admission) { if (disposed || ActiveRuns >= 16) throw new InvalidOperationException("공유가 종료 중이거나 동시 실행 한도에 도달했습니다."); job = new(Wire.Id()); jobs[job.Id] = job; }
                _ = BeginAsync(job, request with { SessionId = job.Id });
                await HttpHost.ReplyAsync(context, 202, new { protocol = 1, jobId = job.Id }); return;
            }
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 4 && parts[0] == "v1" && parts[1] == "runs" && Wire.Identifier(parts[2]) && jobs.TryGetValue(parts[2], out var found))
            {
                if (context.Request.Method == "GET" && parts[3] == "events")
                {
                    if (context.Request.Query.Count != 1 || context.Request.Query["cursor"].Count != 1 || !long.TryParse(context.Request.Query["cursor"], out var cursor) || cursor < 0 || cursor > found.Cursor) { await Reply(400, "출력 위치가 올바르지 않습니다."); return; }
                    WirePoll poll;
                    lock (found)
                    {
                        found.LastPoll = DateTimeOffset.UtcNow;
                        var events = found.Events.Where(e => e.Cursor > cursor).Take(100).Select(e =>
                        {
                            var unsupported = e.Event.Type == "activity" && context.Request.Headers["x-mighty-activity"] != "1" || e.Event.Type == "usage" && context.Request.Headers["x-mighty-usage"] != "1";
                            // Keep cursor continuity for original v1 clients.
                            if (unsupported) return e with { Event = RunEvent.State(e.Event.SessionId, "running") };
                            if (e.Event.Entry is { } entry && context.Request.Headers["x-mighty-activity"] != "1") return e with { Event = e.Event with { Entry = entry with { Activity = null, Text = Wire.Clean(entry.Text, 32768) } } };
                            return e;
                        }).ToList();
                        poll = new(1, events.LastOrDefault()?.Cursor ?? cursor, found.Cursor, (found.Events.FirstOrDefault()?.Cursor ?? 1) > cursor + 1, found.Done, events);
                    }
                    await HttpHost.ReplyAsync(context, 200, poll); return;
                }
                if (context.Request.Method == "POST" && parts[3] == "stop" && !context.Request.QueryString.HasValue) { using var body = await HttpHost.ReadJsonAsync(context); await StopJobAsync(found); await HttpHost.ReplyAsync(context, 200, new { protocol = 1, stopped = true }); return; }
            }
            await Reply(404, "원격 작업 또는 API를 찾을 수 없습니다.");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException or JsonException or InvalidDataException) { if (!context.Response.HasStarted) await Reply(400, Wire.Clean(ex.Message, 300)); }
        catch (OperationCanceledException) { }
        catch (IOException) { if (!context.Response.HasStarted) await Reply(500, "원격 요청을 처리하지 못했습니다."); }
        Task Reply(int status, string error) => HttpHost.ReplyAsync(context, status, new { protocol = 1, error });
    }
    private async Task BeginAsync(Job job, StartRunRequest request)
    {
        try { if (disposed) throw new ObjectDisposedException(nameof(RemoteServer)); await manager.StartAsync(request); }
        catch (Exception ex) { Emit(RunEvent.Log(job.Id, "error", ex.Message)); Emit(RunEvent.State(job.Id, job.Stopping ? "stopped" : "error")); }
    }
    private void Emit(RunEvent value)
    {
        if (!value.Valid() || !jobs.TryGetValue(value.SessionId, out var job)) return;
        lock (job)
        {
            if (job.Done) return;
            var entry = new WireEvent(++job.Cursor, value); job.Events.Add(entry); job.Bytes += JsonSerializer.SerializeToUtf8Bytes(entry, Wire.Json).Length;
            while (job.Events.Count > 256 || job.Bytes > 512 * 1024) { job.Bytes -= JsonSerializer.SerializeToUtf8Bytes(job.Events[0], Wire.Json).Length; job.Events.RemoveAt(0); }
            if (value.Type == "status" && value.Status is "completed" or "error" or "stopped") { job.Done = true; job.Finished = DateTimeOffset.UtcNow; }
        }
    }
    private async Task StopJobAsync(Job job)
    {
        lock (job) { if (job.Done || job.Stopping) return; job.Stopping = true; }
        try { await manager.StopAsync(job.Id); } catch (Exception ex) { Emit(RunEvent.Log(job.Id, "error", ex.Message)); }
        Emit(RunEvent.State(job.Id, "stopped"));
    }
    private async Task MonitorAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        try
        {
            while (await timer.WaitForNextTickAsync(cancel.Token))
            {
                foreach (var job in jobs.Values) if (!job.Done && DateTimeOffset.UtcNow - job.LastPoll > lease) _ = StopJobAsync(job);
                var completed = jobs.Values.Where(j => j.Done).OrderByDescending(j => j.Finished).ToList();
                foreach (var job in completed.Where((j, index) => index >= 32 || DateTimeOffset.UtcNow - j.Finished > TimeSpan.FromMinutes(2))) jobs.TryRemove(job.Id, out _);
            }
        }
        catch (OperationCanceledException) { }
    }
    public async ValueTask DisposeAsync() { if (disposed) return; disposed = true; cancel.Cancel(); if (server is not null) await server.DisposeAsync(); await manager.DisposeAsync(); if (monitor is not null) await monitor; jobs.Clear(); }
}
