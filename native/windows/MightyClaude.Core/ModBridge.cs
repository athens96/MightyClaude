using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace MightyClaude.Core;

public sealed class ModBridge : IAsyncDisposable
{
    private const int MetadataBodyLimit = 16_384, GraphBodyLimit = 65_536, RequestsPerSecond = 120;
    private static readonly HashSet<string> MetadataKeys = new(StringComparer.Ordinal)
        { "version", "runId", "claudeSessionId", "event", "turnId", "tool", "durationMs", "reason", "toolUseId", "summary", "output", "isError", "sequence", "agentId", "usage", "graph" };
    private static readonly HashSet<string> GraphKeys = new(StringComparer.Ordinal)
        { "version", "phase", "agentId", "parentAgentId", "parentToolUseId", "name", "agentType", "model", "input", "output" };

    public sealed class Connection(string id, string token, Action<JsonElement> receive) : IDisposable
    {
        internal DateTimeOffset Window;
        internal int Count;
        private int received;
        public string Id { get; } = id;
        public string Token { get; } = token;
        public string Url { get; internal set; } = "";
        public int Received { get { lock (this) return received; } internal set { lock (this) received = value; } }
        internal bool GraphEnabled { get; init; }
        internal Action<JsonElement> Receive { get; } = receive;
        internal Action? Release { get; set; }
        // Dispatch shares this lock. Once Dispose returns, even an authenticated
        // request that was still reading its body cannot reach the previous run.
        public void Dispose()
        {
            lock (this) { var release = Release; Release = null; release?.Invoke(); }
        }
    }

    private readonly ConcurrentDictionary<string, Connection> connections = [];
    private readonly SemaphoreSlim gate = new(1);
    private readonly TimeProvider clock;
    private HttpHost? host;
    private bool disposed;

    public ModBridge() : this(TimeProvider.System) { }
    internal ModBridge(TimeProvider clock) => this.clock = clock;

    public async Task<Connection> RegisterAsync(Action<JsonElement> receive, CancellationToken token, bool graphEnabled = false)
    {
        await gate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            host ??= await HttpHost.StartAsync(IPAddress.Loopback, 0, HandleAsync, token, MetadataBodyLimit);
            var connection = new Connection(Wire.Id(), Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(), receive)
                { Url = new Uri(host.Address, "/events").ToString(), GraphEnabled = graphEnabled, Window = clock.GetUtcNow() };
            connection.Release = () => connections.TryRemove(connection.Id, out _);
            connections[connection.Id] = connection;
            return connection;
        }
        finally { gate.Release(); }
    }

    private async Task HandleAsync(HttpContext context)
    {
        try
        {
            if (context.Connection.RemoteIpAddress?.Equals(IPAddress.Loopback) != true || context.Request.Method != "POST" || context.Request.Path != "/events" || context.Request.QueryString.HasValue || context.Request.Headers.ContainsKey("Origin") || context.Request.Headers.ContainsKey("sec-fetch-site"))
            { context.Response.StatusCode = 403; return; }
            var authorization = context.Request.Headers.Authorization.ToString();
            var connection = authorization.Length == 71 && authorization.StartsWith("Bearer ", StringComparison.Ordinal)
                ? connections.Values.FirstOrDefault(c => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(authorization[7..]), Encoding.ASCII.GetBytes(c.Token))) : null;
            if (connection is null) { context.Response.StatusCode = 401; return; }

            var graphRequest = connection.GraphEnabled && context.Request.Headers["X-Mighty-Graph"] == "1";
            // Kestrel keeps its 16 KiB default for all other requests. Its SDK
            // contract permits an override only before the body has been read.
            // HTTP/1 chunk framing counts toward Kestrel's transport allowance;
            // keep a finite framing budget while the reader below still caps
            // the decoded JSON body at exactly 64 KiB.
            if (graphRequest && context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            {
                var chunked = context.Request.Headers.TransferEncoding.ToString().Split(',').Any(value => value.Trim().Equals("chunked", StringComparison.OrdinalIgnoreCase));
                limit.MaxRequestBodySize = GraphBodyLimit + (chunked ? MetadataBodyLimit : 0);
            }
            lock (connection)
            {
                var now = clock.GetUtcNow();
                if (now - connection.Window >= TimeSpan.FromSeconds(1)) { connection.Window = now; connection.Count = 0; }
                if (connection.Count >= RequestsPerSecond) { context.Response.StatusCode = 429; return; }
                connection.Count++;
            }

            var payload = await HttpHost.ReadJsonWithLengthAsync(context, graphRequest ? GraphBodyLimit : MetadataBodyLimit);
            using var body = payload.Document;
            var value = body.RootElement;
            if (!IsCurrent(connection)) { context.Response.StatusCode = 410; return; }
            // A graph header does not expand ordinary telemetry. Count the full
            // body, including whitespace and chunked transfers, not GetRawText.
            if (payload.Length > MetadataBodyLimit && value.ValueKind == JsonValueKind.Object && value.Text("event") is not ("agent.spawn" or "agent.complete"))
            { context.Response.StatusCode = 413; return; }
            if (!ValidMetadata(value, connection.Id, graphRequest)) { context.Response.StatusCode = 400; return; }

            lock (connection)
            {
                if (!IsCurrent(connection)) { context.Response.StatusCode = 410; return; }
                // Long runs continue after 4096 events; only the rolling rate
                // limit bounds traffic. The diagnostic counter cannot overflow.
                if (connection.Received < int.MaxValue) connection.Received++;
                connection.Receive(value);
            }
            context.Response.StatusCode = 204;
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        { if (!context.Response.HasStarted) context.Response.StatusCode = 413; }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or InvalidOperationException or OperationCanceledException)
        { if (!context.Response.HasStarted) context.Response.StatusCode = 400; }
    }

    private bool IsCurrent(Connection connection) => connections.TryGetValue(connection.Id, out var current) && ReferenceEquals(current, connection);

    private static bool ValidMetadata(JsonElement value, string runId, bool graphRequest)
    {
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Any(p => !MetadataKeys.Contains(p.Name)) ||
            !VersionOne(value) || value.Text("runId") != runId || !Wire.Identifier(value.Text("claudeSessionId"))) return false;
        var kind = value.Text("event");
        if (kind is not ("session.start" or "turn.start" or "turn.complete" or "tool.call" or "tool.waiting" or "tool.complete" or "session.usage" or "agent.spawn" or "agent.complete")) return false;
        foreach (var key in new[] { "turnId", "toolUseId", "agentId" })
            if (value.TryGetProperty(key, out var raw) && !Wire.Identifier(raw.ValueKind == JsonValueKind.String ? raw.GetString() : null)) return false;
        foreach (var (key, bytes, line) in new[] { ("tool", 160, true), ("summary", ActivitySupport.MaximumSummaryBytes, true), ("output", ActivitySupport.MaximumOutputBytes, false) })
            if (value.TryGetProperty(key, out var raw) && !CleanText(raw, bytes, line)) return false;
        if (value.TryGetProperty("reason", out var reason) && (reason.ValueKind != JsonValueKind.String || reason.GetString() is not ("answer" or "aborted" or "refusal" or "error"))) return false;
        if (value.TryGetProperty("durationMs", out var duration) && (duration.ValueKind != JsonValueKind.Number || !duration.TryGetDouble(out var milliseconds) || !double.IsFinite(milliseconds) || milliseconds < 0 || milliseconds > 1_000_000_000_000)) return false;
        if (value.TryGetProperty("sequence", out var sequence) && (sequence.ValueKind != JsonValueKind.Number || !sequence.TryGetDouble(out var number) || !double.IsFinite(number) || Math.Truncate(number) != number || number is < 0 or > 9_007_199_254_740_991)) return false;
        if (value.TryGetProperty("isError", out var flag) && flag.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
        if (kind.StartsWith("tool.", StringComparison.Ordinal) && string.IsNullOrEmpty(value.Text("tool"))) return false;
        if (kind is "tool.waiting" or "tool.complete" && value.Text("toolUseId") is null) return false;
        if (value.TryGetProperty("usage", out var usage))
        {
            var clean = usage.Deserialize<SessionUsage>(Wire.Json);
            if (kind != "session.usage" || clean?.Provider != "claude" || clean.ProviderSessionId != value.Text("claudeSessionId")) return false;
        }
        else if (kind == "session.usage") return false;

        if (value.TryGetProperty("graph", out var graph))
        {
            if (!graphRequest || kind is not ("agent.spawn" or "agent.complete") || !ValidGraph(graph, kind)) return false;
        }
        else if (kind is "agent.spawn" or "agent.complete") return false;
        return true;
    }

    private static bool ValidGraph(JsonElement graph, string kind)
    {
        if (graph.ValueKind != JsonValueKind.Object || graph.EnumerateObject().Any(p => !GraphKeys.Contains(p.Name)) || !VersionOne(graph)) return false;
        var phase = graph.Text("phase");
        if (kind == "agent.spawn" ? phase is not ("starting" or "running" or "error" or "stopped") : phase is not ("completed" or "error" or "stopped")) return false;
        foreach (var key in new[] { "agentId", "parentAgentId", "parentToolUseId" })
            if (graph.TryGetProperty(key, out var raw) && raw.ValueKind != JsonValueKind.Null && !Wire.Identifier(raw.ValueKind == JsonValueKind.String ? raw.GetString() : null)) return false;
        var agentId = graph.Text("agentId");
        if (agentId is not null && agentId == graph.Text("parentAgentId")) return false;
        if (kind == "agent.spawn" && graph.Text("parentToolUseId") is null) return false;
        if ((kind == "agent.complete" || phase == "running") && agentId is null) return false;
        foreach (var (key, bytes, line) in new[] { ("name", 160, true), ("agentType", 160, true), ("model", 200, true), ("input", 16_384, false), ("output", 32_768, false) })
            if (graph.TryGetProperty(key, out var raw) && raw.ValueKind != JsonValueKind.Null && !CleanText(raw, bytes, line)) return false;
        return graph.Text("model") is not { } model || Wire.Model(model);
    }

    private static bool VersionOne(JsonElement value) => value.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.Number && version.TryGetDouble(out var number) && number == 1;
    private static bool CleanText(JsonElement value, int maximum, bool singleLine) => value.ValueKind == JsonValueKind.String && value.GetString() == ActivitySupport.Clean(value.GetString(), maximum, singleLine);

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try
        {
            disposed = true;
            foreach (var connection in connections.Values) connection.Dispose();
            if (host is not null) await host.DisposeAsync();
            host = null;
        }
        finally { gate.Release(); }
    }
}
