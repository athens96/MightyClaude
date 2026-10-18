using System.Text.Json;
using System.Text.Json.Serialization;

namespace MightyClaude.Core;

/// <summary>
/// The runs half (M/Remote/RemoteClientService.swift start, follow, stop, send, dropRuns, shutdown). A run holds the connection entry and
/// revision it started under: whatever it hits later fails that connection only while it is still that one, never a newer one.
/// </summary>
public sealed partial class RemoteClientService
{
    private sealed class Run(string connectionId, Entry entry, Guid revision, RemoteTarget target, string key)
    {
        public readonly string ConnectionId = connectionId;
        public readonly Entry Entry = entry;
        public readonly Guid Revision = revision;
        public readonly RemoteTarget Target = target;
        public readonly string Key = key;
        public readonly CancellationTokenSource Cancel = new();
        /// <summary>
        /// Held while one poll's events reach the pane, and while a stop or a lost connection says the run ended: the pane's last word
        /// always comes after the poll being applied, as the Mac's actor orders them. Taken before <c>gate</c>, never under it.
        /// </summary>
        public readonly object Applying = new();
        /// <summary>Waiting tool requests by id, with the host's run id their answer carries (Read rewrites the request's to the pane's). Under <c>gate</c>.</summary>
        public readonly Dictionary<string, string> HostRunIds = new(StringComparer.Ordinal);
        /// <summary>Set once, under <c>gate</c>, when the host accepted the run.</summary>
        public string? JobId;
        public int Cursor;
        public bool Stopping;
    }

    /// <summary>A poll whose events are read one at a time: one the client cannot read is left out alone, never the whole poll.</summary>
    private sealed record PollBody(int Protocol, int Cursor, int LastCursor, bool Gap, bool Done, [property: JsonConverter(typeof(Items))] IReadOnlyList<JsonElement> Events);

    /// <summary>
    /// The poll's events as they came, each read later on its own with RemoteWire.Json: a key repeated inside one of them refuses that one
    /// alone there (RemoteWire.Json would refuse the whole poll here).
    /// </summary>
    private sealed class Items : JsonConverter<IReadOnlyList<JsonElement>>
    {
        public override IReadOnlyList<JsonElement> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException(Locale.Get("remote.error.badBody"));
            var items = new List<JsonElement>();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                // Bounded by the transport's 2 MiB; a count past 256 is refused after.
                using var item = JsonDocument.ParseValue(ref reader);
                items.Add(item.RootElement.Clone());
            }
            return items;
        }
        public override void Write(Utf8JsonWriter writer, IReadOnlyList<JsonElement> value, JsonSerializerOptions options) => throw new NotSupportedException();
    }

    private static readonly IReadOnlyDictionary<string, string> GraphRunsHeader = new Dictionary<string, string> { [RemoteProtocol.GraphHeader] = RemoteProtocol.GraphRuns };
    /// <summary>The Mac's: 30 s for a start without attachments, 5 s for a stop.</summary>
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(30), StopTimeout = TimeSpan.FromSeconds(5);
    private readonly Dictionary<string, Run> runs = new(StringComparer.Ordinal);
    private readonly RemotePendingPermissions pending = new();

    public bool IsRunning(string paneId) { lock (gate) return runs.ContainsKey(paneId); }

    /// <summary>Sends one request of a remote workspace's pane to the host and follows its output.</summary>
    public async Task StartAsync(StartRunRequest request, Workspace workspace)
    {
        lock (gate) if (closing) throw new InvalidOperationException(Locale.Get("common.appClosing"));
        request = request.Validate();
        Run run; string hostWorkspaceId;
        lock (gate)
        {
            if (closing) throw new InvalidOperationException(Locale.Get("common.appClosing"));
            if (runs.ContainsKey(request.SessionId)) throw new InvalidOperationException(Locale.Get("run.error.paneAlreadyRunning"));
            if (runs.Count >= RemoteProtocol.MaximumRuns) throw new InvalidOperationException(Locale.Get("run.error.concurrentLimit"));
            if (workspace.Id != request.WorkspaceId || workspace.Remote is not { } reference || !connections.TryGetValue(reference.ConnectionId, out var connection)
                || connection.Info.Status != RemoteConnectionStatus.Connected || connection.Target is not { } target || connection.Key is not { } key || !connection.Info.Shared.Any(w => w.Id == reference.WorkspaceId))
                throw new InvalidOperationException(Locale.Get("remote.run.error.notConnected"));
            if (request.Kind == "claude")
            {
                if (connection.Info.Runtime?.Providers?.FirstOrDefault(p => p.Id == request.Provider) is not { Available: true } provider)
                    throw new InvalidOperationException(Locale.Get("remote.host.error.cliMissing", new Dictionary<string, string> { ["name"] = ProviderCatalog.Name(request.Provider) }));
                if (RemoteWorkspaceValidation.CapabilityProblem(request, provider.Capabilities) is { } problem) throw new InvalidOperationException(problem);
            }
            run = new Run(reference.ConnectionId, connection, connection.Revision, target, key); hostWorkspaceId = reference.WorkspaceId;
            runs[request.SessionId] = run;
        }
        try
        {
            var body = JsonSerializer.SerializeToUtf8Bytes(RemoteWireStart.From(request, hostWorkspaceId), RemoteWire.Json);
            if (body.Length > AttachmentSupport.MaximumRequestBytes || request.Attachments is null && body.Length > RemoteProtocol.MaximumRequestBytes) throw new InvalidOperationException(Locale.Get("remote.run.error.tooLarge"));
            await EnsureRouteAsync(run.ConnectionId, run.Entry, run.Revision, run.Target);
            var data = await RemoteTransport.RequestAsync(run.Target, run.Key, HttpMethod.Post, RemoteProtocol.PathPrefix + "runs", body,
                request.Attachments is null ? StartTimeout : RemoteProtocol.UploadTimeout, headers: GraphRunsHeader);
            RemoteWireJob? accepted;
            try { accepted = JsonSerializer.Deserialize<RemoteWireJob>(data, RemoteWire.Json); }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException or InvalidOperationException) { accepted = null; }
            if (accepted is null || accepted.Protocol != RemoteProtocol.Version || !Wire.Identifier(accepted.JobId)) throw new InvalidDataException(Locale.Get("remote.connect.error.notMighty"));
            bool cancelled;
            lock (gate) { run.JobId = accepted.JobId; cancelled = closing || !Listed(request.SessionId, run) || run.Stopping; }
            if (cancelled)
            {
                // Stopped, dropped or closing while the host was accepting it: it stops there too.
                await StopOnHostAsync(run);
                lock (gate) if (Listed(request.SessionId, run)) runs.Remove(request.SessionId);
                throw new OperationCanceledException();
            }
            _ = Task.Run(() => FollowAsync(request.SessionId, run));
        }
        catch (Exception ex)
        {
            lock (gate) if (Listed(request.SessionId, run)) runs.Remove(request.SessionId);
            // The host no longer takes this key (it restarted sharing): the connection is not connected any more.
            if (ex is RemoteHttpFailure { Status: 401 } failure) await FailAsync(run.ConnectionId, failure.Message, run.Entry, run.Revision);
            throw;
        }
    }

    /// <summary>The pane's run is still <paramref name="run"/>. Call under the lock.</summary>
    private bool Listed(string paneId, Run run) => runs.TryGetValue(paneId, out var current) && ReferenceEquals(current, run);
    private bool Current(string paneId, Run run) { lock (gate) return Listed(paneId, run) && !run.Stopping; }

    private async Task FollowAsync(string paneId, Run run)
    {
        var quiet = 0; DateTimeOffset? failingSince = null;
        while (Current(paneId, run))
        {
            // Outside the tunnel the connection is lost when it is still this run's, and never asked again.
            try { await EnsureRouteAsync(run.ConnectionId, run.Entry, run.Revision, run.Target); }
            catch (InvalidOperationException ex) { await EndAsync(paneId, run, ex.Message); return; }
            byte[] data;
            try
            {
                data = await RemoteTransport.RequestAsync(run.Target, run.Key, HttpMethod.Get, $"{RemoteProtocol.PathPrefix}runs/{run.JobId}/events?cursor={run.Cursor}", token: run.Cancel.Token);
                failingSince = null;
            }
            catch (OperationCanceledException) when (run.Cancel.IsCancellationRequested) { return; }
            catch (Exception ex) when (RemoteTransport.Transient(ex) && DateTimeOffset.UtcNow - (failingSince ?? DateTimeOffset.UtcNow) < options.PollRetryWindow)
            {
                // Asked again for a while (ten seconds), well inside the host's heartbeat.
                failingSince ??= DateTimeOffset.UtcNow;
                if (!await Pause(run, TimeSpan.FromSeconds(1))) return;
                continue;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { await LoseAsync(paneId, run, ex.Message); return; }
            if (Read(data, run, paneId) is not { } poll) { await LoseAsync(paneId, run, Locale.Get("remote.run.error.badOutput")); return; }
            lock (run.Applying)
            {
                if (!Current(paneId, run)) return;
                if (poll.Body.Gap) onEvent(RunEvent.Log(paneId, "system", Locale.Get("remote.run.notice.gap")));
                var terminal = false; var skipped = false;
                foreach (var incoming in poll.Items)
                {
                    // Ended from inside a callback on this thread (the lock lets it in): nothing more of this run reaches the pane.
                    lock (gate) if (!Listed(paneId, run)) return;
                    // One event this app cannot use is left out, and said so once; one it reads but does not draw is left out silently.
                    if (incoming is not { Usable: true }) { skipped = true; continue; }
                    if (incoming.Permission is { } permission) { Receive(run, permission, incoming.HostRunId!); continue; }
                    if (incoming.Event is not { } value) continue;
                    onEvent(value);
                    if (value.Type == "status" && value.Status is "completed" or "error" or "stopped") terminal = true;
                }
                if (skipped) onEvent(RunEvent.Log(paneId, "system", Locale.Get("remote.run.notice.skipped")));
                run.Cursor = poll.Body.Cursor;
                if (poll.Body.Done)
                {
                    lock (gate) if (Listed(paneId, run)) runs.Remove(paneId);
                    pending.Forget(paneId);
                    if (!terminal) onEvent(RunEvent.State(paneId, "error"));
                    return;
                }
            }
            // More waits on the host: read it soon (at most ten polls a second per run). Otherwise wait longer while nothing comes.
            if (poll.Body.LastCursor > run.Cursor) { quiet = 0; if (!await Pause(run, TimeSpan.FromMilliseconds(100))) return; continue; }
            quiet = poll.Items.Count == 0 ? Math.Min(quiet + 1, 4) : 0;
            if (!await Pause(run, TimeSpan.FromMilliseconds(250 + 150 * quiet))) return;
        }
    }

    /// <summary>
    /// One poll as the Mac checks it (protocol, cursors, at most 256 events), each event read for the pane; an item that is not even an
    /// event is null (unusable). Null when the poll itself is bad, which loses the connection.
    /// </summary>
    private static (PollBody Body, List<RemoteIncoming?> Items)? Read(byte[] data, Run run, string paneId)
    {
        PollBody? poll;
        try { poll = JsonSerializer.Deserialize<PollBody>(data, RemoteWire.Json); }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException or InvalidOperationException) { return null; }
        if (poll is null || poll.Protocol != RemoteProtocol.Version || poll.Cursor < run.Cursor || poll.LastCursor < poll.Cursor || poll.Events is null || poll.Events.Count > RemoteProtocol.MaximumPollEvents) return null;
        var items = new List<RemoteIncoming?>(poll.Events.Count); var cursor = run.Cursor;
        foreach (var element in poll.Events)
        {
            RemoteWireEvent? item;
            try { item = element.Deserialize<RemoteWireEvent>(RemoteWire.Json); }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException or InvalidOperationException) { item = null; }
            if (item?.Event is null) { items.Add(null); continue; }
            if (item.Cursor <= cursor || item.Cursor > poll.Cursor) return null;
            cursor = item.Cursor;
            items.Add(RemoteWireEvents.Read(item.Event, run.JobId!, paneId, graphRuns: true));
        }
        return (poll, items);
    }

    /// <summary>A tool request reaches the pane, which keeps the newest 32 waiting; older ones are withdrawn. Under <see cref="Run.Applying"/>.</summary>
    private void Receive(Run run, ToolPermissionRequest permission, string hostRunId)
    {
        var withdrawn = pending.Admit(permission);
        lock (gate)
        {
            foreach (var old in withdrawn) run.HostRunIds.Remove(old.Id);
            if (permission.State == "pending") run.HostRunIds[permission.Id] = hostRunId; else run.HostRunIds.Remove(permission.Id);
        }
        foreach (var old in withdrawn) onPermission(old);
        onPermission(permission);
    }

    private static async Task<bool> Pause(Run run, TimeSpan delay)
    {
        try { await Task.Delay(delay, run.Cancel.Token); return true; } catch (OperationCanceledException) { return false; }
    }

    /// <summary>A poll failed for good: the connection is lost while it is still this run's (its runs end then), else this run alone ends.</summary>
    private async Task LoseAsync(string paneId, Run run, string message)
    {
        bool listed; lock (gate) listed = Listed(paneId, run);
        if (listed && !await FailAsync(run.ConnectionId, message, run.Entry, run.Revision)) await EndAsync(paneId, run, message);
    }

    /// <summary>This run alone ends as lost: its connection changed under it, so the failure is not the newer connection's.</summary>
    private async Task EndAsync(string paneId, Run run, string message)
    {
        lock (run.Applying)
        {
            lock (gate) { if (!Listed(paneId, run)) return; runs.Remove(paneId); }
            SayLost(paneId, message);
        }
        await StopOnHostAsync(run);
    }

    /// <summary>The pane's run ended with its connection: the reason, cut as the Mac cuts it, then the error. Under <see cref="Run.Applying"/>.</summary>
    private void SayLost(string paneId, string message)
    {
        pending.Forget(paneId);
        onEvent(RunEvent.Log(paneId, "error", Locale.Get("remote.run.error.connectionLost", new Dictionary<string, string> { ["detail"] = RemoteText.Cut(message) })));
        onEvent(RunEvent.State(paneId, "error"));
    }

    public async Task StopAsync(string paneId)
    {
        Run? run;
        lock (gate) { if (!runs.TryGetValue(paneId, out run) || run.Stopping) return; run.Stopping = true; }
        run.Cancel.Cancel();
        await StopOnHostAsync(run);
        lock (run.Applying)
        {
            lock (gate) { if (!Listed(paneId, run)) return; runs.Remove(paneId); }
            pending.Forget(paneId);
            onEvent(RunEvent.State(paneId, "stopped"));
        }
    }

    private async Task StopOnHostAsync(Run run)
    {
        string? job; lock (gate) job = run.JobId;
        if (job is null) return;
        try
        {
            await EnsureRouteAsync(run.ConnectionId, run.Entry, run.Revision, run.Target);
            await RemoteTransport.RequestAsync(run.Target, run.Key, HttpMethod.Post, $"{RemoteProtocol.PathPrefix}runs/{job}/stop", "{}"u8.ToArray(), StopTimeout);
        }
        // The host stops a run nobody polls within 30 s anyway.
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }

    private async Task<bool> SendAsync<T>(string paneId, string action, Func<Run, T> body)
    {
        Run? run; string job;
        lock (gate)
        {
            if (!runs.TryGetValue(paneId, out run) || run.Stopping || run.JobId is not { } accepted) throw new InvalidOperationException(Locale.Get("run.error.permissionRunGone"));
            job = accepted;
        }
        await EnsureRouteAsync(run.ConnectionId, run.Entry, run.Revision, run.Target);
        var data = await RemoteTransport.RequestAsync(run.Target, run.Key, HttpMethod.Post, $"{RemoteProtocol.PathPrefix}runs/{job}/{action}", JsonSerializer.SerializeToUtf8Bytes(body(run), RemoteWire.Json));
        try { return JsonSerializer.Deserialize<RemoteWireAnswer>(data, RemoteWire.Json)?.Accepted == true; }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException or InvalidOperationException) { return false; }
    }

    /// <summary>The host's run id of a waiting request (a Mac host's need not be the job's); the job's for one the pane does not hold.</summary>
    private string HostRunId(Run run, string requestId) { lock (gate) return run.HostRunIds.GetValueOrDefault(requestId) ?? run.JobId!; }

    /// <summary>Hands a follow-up to the running Claude turn on the host; false when it did not take it.</summary>
    public async Task<bool> SteerAsync(string paneId, string text)
    {
        try { return await SendAsync(paneId, "steer", _ => new RemoteWireSteer(text)); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return false; }
    }
    public Task RespondToPermissionAsync(string paneId, string requestId, bool allow) => SendAsync(paneId, "permission", run => new RemoteWirePermission(HostRunId(run, requestId), requestId, allow));
    public Task AnswerQuestionsAsync(string paneId, string requestId, IReadOnlyDictionary<string, UserQuestionAnswer> answers) => SendAsync(paneId, "questions", run => new RemoteWireQuestions(HostRunId(run, requestId), requestId, answers));
    public Task AnswerPlanAsync(string paneId, string requestId, PlanDecision decision) => SendAsync(paneId, "plan", run => RemoteWirePlan.From(HostRunId(run, requestId), requestId, decision));

    /// <summary>A connection's runs end: replaced or disconnected (<paramref name="message"/> null) as stopped, lost with its reason as an error.</summary>
    private async Task DropRunsAsync(string connectionId, string? message)
    {
        List<(string Pane, Run Run)> active;
        lock (gate)
        {
            active = runs.Where(p => p.Value.ConnectionId == connectionId).Select(p => (p.Key, p.Value)).ToList();
            foreach (var (pane, _) in active) runs.Remove(pane);
        }
        foreach (var (pane, run) in active)
        {
            run.Cancel.Cancel();
            lock (run.Applying)
            {
                if (message is not null) SayLost(pane, message);
                else { pending.Forget(pane); onEvent(RunEvent.State(pane, "stopped")); }
            }
        }
        // The host stops a run it no longer hears from anyway; this is only sooner.
        foreach (var (_, run) in active) await StopOnHostAsync(run);
    }

    /// <summary>The app is closing: nothing starts any more, and every run stops, on the host too (the Mac's shutdown).</summary>
    public async ValueTask DisposeAsync()
    {
        string[] panes; lock (gate) { closing = true; panes = [.. runs.Keys]; }
        foreach (var pane in panes) await StopAsync(pane);
    }
}
