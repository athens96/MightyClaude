using System.Buffers;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MightyClaude.Core;

/// <summary>
/// The host's runs (M/Remote/RemoteHostService.swift startRun … prune): a client's run goes through this PC's runner in a shared folder,
/// its events wait in a bounded buffer the client reads by cursor, and a run nobody polls for the heartbeat is stopped.
/// </summary>
public sealed partial class RemoteHostService
{
    /// <summary>One client run. Everything that changes is read and written under <c>gate</c>.</summary>
    private sealed class Job(bool graphRuns, DateTimeOffset now)
    {
        /// <summary>Each event as it goes out, written once (so a run holds its bytes, never a larger object tree), with what it costs a poll.</summary>
        public readonly Queue<(int Cursor, byte[] Event, int Size)> Events = new();
        public int Cursor, Bytes;
        public DateTimeOffset LastPoll = now;
        public bool Done, Stopping;
        public DateTimeOffset? FinishedAt;
        /// <summary>The run's tool requests still waiting: an answer (permission, questions, plan) reaches only one of these.</summary>
        public readonly HashSet<string> Pending = new(StringComparer.Ordinal);
        /// <summary>A Windows client asked for graph_run events instead of graph nodes.</summary>
        public readonly bool GraphRuns = graphRuns;
        /// <summary>Cancelled when the run stops or sharing ends, so a launch not begun yet never begins.</summary>
        public readonly CancellationTokenSource Cancel = new();
    }

    private readonly Dictionary<string, Job> jobs = new(StringComparer.Ordinal);
    private static readonly Regex CursorQuery = new(@"^\?cursor=([0-9]{1,12})\z", RegexOptions.CultureInvariant);
    /// <summary>What <c>{"cursor":N,"event":…}</c> adds around an event in a poll, at most.</summary>
    private const int EventFrameBytes = 32;
    /// <summary>More waiting requests than any runner keeps (its channels hold 16); one past this is never answerable from a client.</summary>
    private const int MaximumPendingPerRun = 64;

    private static string ContentType(RemoteHttpRequest request) => (request.Headers.GetValueOrDefault("content-type") ?? "").Split(';')[0].Trim();
    private bool HasJob(string id) { lock (gate) return jobs.ContainsKey(id); }
    /// <summary>Under <c>gate</c>: <paramref name="job"/> is still the run <paramref name="id"/> of this sharing, and not ended.</summary>
    private bool Open(string id, Job job, Guid revision) => revision == generation && jobs.TryGetValue(id, out var current) && ReferenceEquals(current, job) && !job.Done;

    /// <summary>A body read as strictly as the Mac reads it (RemoteWire.Json); anything else is a bad request.</summary>
    private static T Decode<T>(byte[] body) where T : class
    {
        try { return JsonSerializer.Deserialize<T>(body, RemoteWire.Json) ?? throw new JsonException(); }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException or InvalidOperationException) { throw new HostFailure(400, Locale.Get("remote.host.error.badRequest")); }
    }

    private async Task<RemoteHttpResponse> ServeRunsAsync(RemoteHttpRequest request, string[] route, string query, Guid revision)
    {
        if (request.Method == "POST" && route is ["runs"] && query.Length == 0) return await StartRunAsync(request, revision);
        if (route is not ["runs", var id, var action] || !Wire.Identifier(id) || !HasJob(id)) throw new HostFailure(404, Locale.Get("remote.host.error.runGone"));
        if (request.Method == "GET" && action == "events") return Events(id, query);
        if (request.Method != "POST" || query.Length != 0) throw new HostFailure(404, Locale.Get("remote.host.error.badPath"));
        // As on the Mac, a stop reads no body: whatever came with it (this app's client sends {}) is left alone.
        if (action == "stop") { await StopJobAsync(id); return Reply(200, new RemoteWireAnswer(RemoteProtocol.Version, true)); }
        if (ContentType(request) != "application/json") throw new HostFailure(415, Locale.Get("remote.host.error.jsonOnly"));
        IRemoteRunner active;
        lock (gate) { if (runner is null || !jobs.TryGetValue(id, out var job) || job.Done) throw new HostFailure(409, Locale.Get("remote.host.error.runGone")); active = runner; }
        if (request.Body.Length == 0) throw new HostFailure(400, Locale.Get("remote.host.error.badRequest"));
        try
        {
            switch (action)
            {
                case "steer":
                    var steer = Decode<RemoteWireSteer>(request.Body);
                    if (string.IsNullOrWhiteSpace(steer.Text) || Encoding.UTF8.GetByteCount(steer.Text) > 400_000 || steer.Text.Contains('\0')) throw new HostFailure(400, Locale.Get("validation.run.badInput"));
                    return Reply(200, new RemoteWireAnswer(RemoteProtocol.Version, await active.TrySteerAsync(id, steer.Text)));
                case "permission":
                    var permission = Decode<RemoteWirePermission>(request.Body);
                    Answerable(id, permission.RunId, permission.RequestId, ToolPermissionStrings.AlreadySettled);
                    active.RespondToToolPermission(id, permission.RequestId, permission.Allow); break;
                case "questions":
                    var questions = Decode<RemoteWireQuestions>(request.Body);
                    Answerable(id, questions.RunId, questions.RequestId, ToolPermissionStrings.AlreadySettled);
                    active.AnswerQuestionnaire(id, questions.RequestId, questions.Answers); break;
                case "plan":
                    var plan = Decode<RemoteWirePlan>(request.Body);
                    var decision = plan.Decision() ?? throw new HostFailure(400, Locale.Get("remote.host.error.badRequest"));
                    Answerable(id, plan.RunId, plan.RequestId, Locale.Get("plan.error.settled"));
                    active.AnswerPlan(id, plan.RequestId, decision); break;
                default: throw new HostFailure(404, Locale.Get("remote.host.error.badPath"));
            }
        }
        // What the runner refuses (settled, gone, not its kind) is a conflict, as on the Mac.
        catch (Exception ex) when (ex is not (HostFailure or OutOfMemoryException)) { throw new HostFailure(409, ex.Message); }
        return Reply(200, new RemoteWireAnswer(RemoteProtocol.Version, true));
    }

    /// <summary>
    /// An answer names this run (the run id its requests carry is the job's) and one of its requests still waiting; any other request,
    /// another job's included, is refused before the runner hears of it.
    /// </summary>
    private void Answerable(string id, string runId, string requestId, string settled)
    {
        if (requestId is not { Length: > 0 and <= 256 }) throw new HostFailure(400, Locale.Get("remote.host.error.badRequest"));
        lock (gate) if (runId != id || !jobs.TryGetValue(id, out var job) || job.Done || !job.Pending.Contains(requestId)) throw new HostFailure(409, settled);
    }

    private async Task<RemoteHttpResponse> StartRunAsync(RemoteHttpRequest request, Guid revision)
    {
        if (ContentType(request) != "application/json") throw new HostFailure(415, Locale.Get("remote.host.error.jsonOnly"));
        if (request.Body.Length == 0) throw new HostFailure(400, Locale.Get("remote.host.error.badRequest"));
        var wire = Decode<RemoteWireStart>(request.Body);
        // The listener read up to 12 MiB for this route; past 512 KiB only a run that brings attachments.
        if (request.Body.Length > RemoteProtocol.MaximumRequestBytes && wire.Request.Attachments is null) throw new HostFailure(413, Locale.Get("remote.run.error.tooLarge"));
        if (wire.RegisteredModels.Take(64).Any(m => !Wire.Model(m.Name)) || wire.PhaseModels is { } phase && !RemoteWorkspaceValidation.PhaseModels(phase))
            throw new HostFailure(400, Locale.Get("remote.host.error.badRequest"));
        var id = Wire.Id();
        StartRunRequest run;
        try { run = wire.ToRequest(id, wire.Request.WorkspaceId).Validate(); } catch (ArgumentException ex) { throw new HostFailure(400, ex.Message); }
        // Only a shared workspace's own folder is ever the working folder; the client sends an id, never a path.
        var workspace = SharedWorkspaces().FirstOrDefault(w => w.Id == run.WorkspaceId) ?? throw new HostFailure(403, Locale.Get("remote.host.error.notShared"));
        Workspace folder;
        try { folder = await options.ResolveLocal(workspace.Id); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { throw new HostFailure(403, ex.Message); }
        // As M/ProcessRunner.start checks before every run: this workspace, local, an absolute path to a folder that exists, the one shared.
        if (folder.Id != run.WorkspaceId || folder.Remote is not null || !Path.IsPathFullyQualified(folder.Path) || !Directory.Exists(folder.Path) || folder.Path != workspace.Path)
            throw new HostFailure(403, Locale.Get("remote.host.error.notShared"));
        bool allowed; lock (gate) allowed = allowsFullAccess;
        if (FullAccessRefusal(run, allowed) is { } refusal) throw new HostFailure(403, refusal);
        // A Windows command line holds 32 767 characters: a longer command is refused here, never cut.
        if (run.Kind == "shell" && !CommandFits(run.Input)) throw new HostFailure(400, RemoteWindowsStrings.HostCommandTooLong);
        // This PC's own CLI update, plugin change or model reload holds client runs too.
        if (options.Admission(run) is { } reason) throw new HostFailure(503, reason);
        if (run.Kind == "claude")
        {
            // What this PC cannot run is refused here, before anything starts.
            RuntimeInfo runtime;
            try { runtime = await options.Runtime().WaitAsync(RuntimeDeadline); }
            catch (TimeoutException) { throw new HostFailure(503, RemoteWindowsStrings.Timeout); }
            if (runtime.Providers?.FirstOrDefault(p => p.Id == run.Provider) is not { Available: true } provider)
                throw new HostFailure(400, Locale.Get("remote.host.error.cliMissing", new Dictionary<string, string> { ["name"] = ProviderCatalog.Name(run.Provider) }));
            if (RemoteWorkspaceValidation.CapabilityProblem(run, provider.Capabilities) is { } problem) throw new HostFailure(400, problem);
        }
        Job job; IRemoteRunner active;
        lock (gate)
        {
            if (revision != generation || runner is null) throw new HostFailure(503, Locale.Get("remote.host.error.stopped"));
            if (jobs.Values.Count(j => !j.Done) >= RemoteProtocol.MaximumRuns) throw new HostFailure(429, Locale.Get("remote.host.error.runLimit", new Dictionary<string, string> { ["count"] = RemoteProtocol.MaximumRuns.ToString() }));
            Prune();
            jobs[id] = job = new Job(request.Headers.GetValueOrDefault(RemoteProtocol.GraphHeader) == RemoteProtocol.GraphRuns, options.Clock());
            active = runner;
        }
        AnnounceRuns();
        _ = Task.Run(() => LaunchAsync(run, job, active, revision), job.Cancel.Token);
        return Reply(202, new RemoteWireJob(RemoteProtocol.Version, id));
    }

    /// <summary>Whether the command fits the command line its PowerShell gets (RemoteCommandShell.Fits). Off Windows (the checks run on a
    /// Mac) Windows' usual system folder stands in; a system folder the runner cannot use is left for the runner to report.</summary>
    private static bool CommandFits(string command)
    {
        var system = OperatingSystem.IsWindows() ? Environment.GetFolderPath(Environment.SpecialFolder.System) : @"C:\Windows\System32";
        try { return RemoteCommandShell.Fits(command, system); }
        catch (ArgumentException) { return true; }
    }

    /// <summary>
    /// Hands the run to the runner. A runner may refuse it at once (thrown) or fail it later through its own error and status events;
    /// the first is turned into those events here. A stop that came while the runner took the run stops it once it is there.
    /// </summary>
    private async Task LaunchAsync(StartRunRequest run, Job job, IRemoteRunner active, Guid revision)
    {
        var id = run.SessionId;
        lock (gate) if (!Open(id, job, revision) || job.Stopping) return;
        try { await active.StartAsync(run); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            bool stopping;
            lock (gate) { if (!Open(id, job, revision)) return; stopping = job.Stopping; }
            Receive(RunEvent.Log(id, "error", ex.Message), revision);
            Receive(RunEvent.State(id, stopping ? "stopped" : "error"), revision);
            return;
        }
        bool late; lock (gate) late = job.Stopping || revision != generation || !ReferenceEquals(jobs.GetValueOrDefault(id), job);
        if (!late) return;
        try { await active.StopAsync(id); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Trace.TraceWarning("remote host: stopping a run that started late: " + ex.GetType().Name); }
    }

    // The runner's callbacks run on its threads, a graph node and the last status under its per-run lock: each takes the host's lock
    // briefly, never calls into the runner, never waits on anything that does, and never throws.

    private void Receive(RunEvent value, Guid revision)
    {
        try
        {
            if (Following(value.SessionId, revision) is not { } graphRuns) return;
            Append(value.SessionId, RemoteWireEvents.FromRunEvent(value, value.SessionId, graphRuns), revision, value.Type == "status" && value.Status is "completed" or "error" or "stopped");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Trace.TraceWarning("remote host: a run event was left out: " + ex.GetType().Name); }
    }

    private void ReceivePermission(ToolPermissionRequest value, Guid revision)
    {
        try
        {
            lock (gate)
            {
                if (revision != generation || !jobs.TryGetValue(value.RunId, out var job) || job.Done) return;
                // Waiting before the client can see it, so its answer is never refused for coming too soon.
                if (value.State != "pending") job.Pending.Remove(value.Id);
                else if (job.Pending.Count < MaximumPendingPerRun) job.Pending.Add(value.Id);
            }
            Append(value.RunId, RemoteWireEvents.FromPermission(value, value.RunId), revision, false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Trace.TraceWarning("remote host: a tool request was left out: " + ex.GetType().Name); }
    }

    private void ReceiveGraph(string sessionId, ExecutionGraphNode node, Guid revision)
    {
        try
        {
            // A Windows client that asked for graph runs gets those instead of nodes.
            if (Following(sessionId, revision) is false) Append(sessionId, RemoteWireEvents.FromGraphNode(node, sessionId), revision, false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Trace.TraceWarning("remote host: a graph node was left out: " + ex.GetType().Name); }
    }

    /// <summary>Whether the job's client asked for graph runs; null when no open job of this sharing has the id.</summary>
    private bool? Following(string id, Guid revision) { lock (gate) return revision == generation && jobs.TryGetValue(id, out var job) && !job.Done ? job.GraphRuns : null; }

    /// <summary>
    /// One event into the job's buffer: the newest 4096 within 4 MiB are kept, one too large for any poll is never kept (its cursor shows
    /// as a gap), and nothing after the run's last status. Null is an event the Mac could not read, left out.
    /// </summary>
    private void Append(string jobId, JsonObject? wire, Guid revision, bool ended)
    {
        var data = wire is null ? null : JsonSerializer.SerializeToUtf8Bytes(wire, RemoteWire.Json);
        lock (gate)
        {
            if (revision != generation || !jobs.TryGetValue(jobId, out var job) || job.Done) return;
            if (data is not null)
            {
                job.Cursor++;
                var size = data.Length + EventFrameBytes;
                if (size <= RemoteProtocol.MaximumPollBytes - 4096)
                {
                    job.Events.Enqueue((job.Cursor, data, size)); job.Bytes += size;
                    while (job.Events.Count > RemoteProtocol.MaximumBufferedEvents || job.Bytes > RemoteProtocol.MaximumBufferedBytes) job.Bytes -= job.Events.Dequeue().Size;
                }
            }
            if (!ended) return;
            job.Done = true; job.FinishedAt = options.Clock(); job.Pending.Clear();
        }
        // Told from the pool: this may run on the runner's thread under its per-run lock, where nothing may wait.
        ThreadPool.QueueUserWorkItem(_ => AnnounceRuns());
    }

    private RemoteHttpResponse Events(string id, string query)
    {
        var match = CursorQuery.Match(query);
        lock (gate)
        {
            if (!jobs.TryGetValue(id, out var job) || !match.Success || !long.TryParse(match.Groups[1].ValueSpan, out var requested) || requested > job.Cursor) throw new HostFailure(400, Locale.Get("remote.host.error.badCursor"));
            var cursor = (int)requested;
            job.LastPoll = options.Clock();
            var selected = new List<(int Cursor, byte[] Event, int Size)>(); var bytes = 0;
            foreach (var item in job.Events)
            {
                if (item.Cursor <= cursor) continue;
                if (selected.Count >= RemoteProtocol.MaximumPollEvents || bytes + item.Size > RemoteProtocol.MaximumPollBytes) break;
                selected.Add(item); bytes += item.Size;
            }
            // A cursor skipped between the request's and what follows: the buffer dropped it (over its bounds, or one event too large).
            var expected = cursor + 1; var gap = false;
            foreach (var item in selected) { if (item.Cursor != expected) gap = true; expected = item.Cursor + 1; }
            // Nothing kept after the cursor although the run went on: all of it was dropped.
            if (selected.Count == 0 && job.Cursor > cursor) gap = true;
            var last = selected.Count > 0 ? selected[^1].Cursor : gap ? job.Cursor : cursor;
            return new RemoteHttpResponse(200, PollBody(last, job.Cursor, gap, job.Done && last >= job.Cursor, selected), VersionHeaders);
        }
    }

    /// <summary>A RemoteWirePoll with each event's bytes as they were written when it came in.</summary>
    private static byte[] PollBody(int cursor, int lastCursor, bool gap, bool done, List<(int Cursor, byte[] Event, int Size)> events)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("protocol", RemoteProtocol.Version); writer.WriteNumber("cursor", cursor); writer.WriteNumber("lastCursor", lastCursor);
            writer.WriteBoolean("gap", gap); writer.WriteBoolean("done", done);
            writer.WriteStartArray("events");
            foreach (var item in events)
            {
                writer.WriteStartObject(); writer.WriteNumber("cursor", item.Cursor);
                writer.WritePropertyName("event"); writer.WriteRawValue(item.Event, skipInputValidation: true);
                writer.WriteEndObject();
            }
            writer.WriteEndArray(); writer.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Stops one run (the client's stop, or the heartbeat): the runner ends its process, and the run ends stopped if it said nothing.</summary>
    private async Task StopJobAsync(string id)
    {
        IRemoteRunner? active; Guid revision; Job? job;
        lock (gate)
        {
            if (!jobs.TryGetValue(id, out job) || job.Done || job.Stopping) return;
            job.Stopping = true; job.Cancel.Cancel(); active = runner; revision = generation;
        }
        try { if (active is not null) await active.StopAsync(id); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Trace.TraceWarning("remote host: stopping a run: " + ex.GetType().Name); }
        bool open; lock (gate) open = Open(id, job, revision);
        if (open) Receive(RunEvent.State(id, "stopped"), revision);
    }

    /// <summary>The heartbeat, once a second: a listener that stopped for good ends the sharing, and a run whose client stopped polling is stopped.</summary>
    private async Task LeaseLoopAsync(Guid revision, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), token);
                try
                {
                    if (StopIfListenerFailed()) return;
                    string[] orphaned;
                    lock (gate)
                    {
                        if (revision != generation) return;
                        var now = options.Clock();
                        orphaned = jobs.Where(p => !p.Value.Done && !p.Value.Stopping && now - p.Value.LastPoll >= options.Heartbeat).Select(p => p.Key).ToArray();
                    }
                    foreach (var id in orphaned) await StopJobAsync(id);
                    lock (gate) if (revision == generation) Prune();
                }
                // One bad turn never ends the heartbeat: a run nobody polls must still stop.
                catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException)) { Trace.TraceError("remote host: heartbeat: " + ex.GetType().Name); }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Under <c>gate</c>: finished runs older than two minutes go, and the oldest finished ones while there are 32 or more.</summary>
    private void Prune()
    {
        var now = options.Clock();
        foreach (var id in jobs.Where(p => p.Value.Done && now - (p.Value.FinishedAt ?? now) > TimeSpan.FromMinutes(2)).Select(p => p.Key).ToList()) jobs.Remove(id);
        while (jobs.Count >= 2 * RemoteProtocol.MaximumRuns && jobs.Where(p => p.Value.Done).OrderBy(p => p.Value.FinishedAt).Select(p => p.Key).FirstOrDefault() is { } oldest) jobs.Remove(oldest);
    }
}
