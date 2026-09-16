using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace MightyClaude.Core;

public sealed class ModBridge : IAsyncDisposable
{
    public sealed class Connection(string id, string token, Action<JsonElement> receive) : IDisposable
    {
        internal DateTimeOffset Window = DateTimeOffset.UtcNow;
        internal int Count;
        public string Id { get; } = id;
        public string Token { get; } = token;
        public string Url { get; internal set; } = "";
        public int Received { get; internal set; }
        internal Action<JsonElement> Receive { get; } = receive;
        internal Action? Release { get; set; }
        public void Dispose() => Release?.Invoke();
    }
    private readonly ConcurrentDictionary<string, Connection> connections = [];
    private readonly SemaphoreSlim gate = new(1);
    private HttpHost? host;
    private bool disposed;
    public async Task<Connection> RegisterAsync(Action<JsonElement> receive, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            host ??= await HttpHost.StartAsync(IPAddress.Loopback, 0, HandleAsync, token, 16384);
            var connection = new Connection(Wire.Id(), Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(), receive) { Url = new Uri(host.Address, "/events").ToString() };
            connection.Release = () => connections.TryRemove(connection.Id, out _); connections[connection.Id] = connection; return connection;
        }
        finally { gate.Release(); }
    }
    private async Task HandleAsync(HttpContext context)
    {
        try
        {
            if (context.Connection.RemoteIpAddress?.Equals(IPAddress.Loopback) != true || context.Request.Method != "POST" || context.Request.Path != "/events" || context.Request.Headers.ContainsKey("Origin") || context.Request.Headers.ContainsKey("sec-fetch-site")) { context.Response.StatusCode = 403; return; }
            var authorization = context.Request.Headers.Authorization.ToString();
            var connection = connections.Values.FirstOrDefault(c => authorization.Length == 71 && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(authorization[7..]), Encoding.ASCII.GetBytes(c.Token)) && authorization.StartsWith("Bearer ", StringComparison.Ordinal));
            if (connection is null) { context.Response.StatusCode = 401; return; }
            lock (connection) { if (DateTimeOffset.UtcNow - connection.Window > TimeSpan.FromSeconds(1)) { connection.Window = DateTimeOffset.UtcNow; connection.Count = 0; } if (++connection.Count > 120 || connection.Received >= 4096) { context.Response.StatusCode = 429; return; } }
            using var body = await HttpHost.ReadJsonAsync(context, 16384); var value = body.RootElement;
            if (!connections.TryGetValue(connection.Id, out var current) || !ReferenceEquals(current, connection)) { context.Response.StatusCode = 410; return; }
            var allowed = new[] { "version", "runId", "claudeSessionId", "event", "turnId", "tool", "durationMs", "reason", "toolUseId", "summary", "output", "isError", "sequence", "agentId", "usage" };
            if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Any(p => !allowed.Contains(p.Name)) || !value.TryGetProperty("version", out var version) || !version.TryGetInt32(out var protocol) || protocol != 1 || value.Text("runId") != connection.Id || !Wire.Identifier(value.Text("claudeSessionId")) || value.Text("event") is not ("session.start" or "turn.start" or "turn.complete" or "tool.call" or "tool.waiting" or "tool.complete" or "session.usage")) { context.Response.StatusCode = 400; return; }
            if (value.TryGetProperty("turnId", out var turn) && !Wire.Identifier(turn.ValueKind == JsonValueKind.String ? turn.GetString() : null) || value.TryGetProperty("tool", out var tool) && (tool.ValueKind != JsonValueKind.String || tool.GetString() is not { Length: > 0 and <= 160 }) || value.Text("event") == "tool.call" && value.Text("tool") is null || value.TryGetProperty("reason", out var reason) && (reason.ValueKind != JsonValueKind.String || reason.GetString() is not ("answer" or "aborted" or "refusal" or "error")) || value.TryGetProperty("durationMs", out var duration) && (!duration.TryGetDouble(out var milliseconds) || !double.IsFinite(milliseconds) || milliseconds < 0 || milliseconds > 1_000_000_000_000)) { context.Response.StatusCode = 400; return; }
            foreach (var (key, bytes, line) in new[] { ("tool", 160, true), ("summary", 1000, true), ("output", 8192, false) })
                if (value.TryGetProperty(key, out var raw) && (raw.ValueKind != JsonValueKind.String || raw.GetString() != ActivitySupport.Clean(raw.GetString(), bytes, line))) { context.Response.StatusCode = 400; return; }
            foreach (var key in new[] { "toolUseId", "agentId" }) if (value.TryGetProperty(key, out var raw) && !Wire.Identifier(raw.ValueKind == JsonValueKind.String ? raw.GetString() : null)) { context.Response.StatusCode = 400; return; }
            if (value.TryGetProperty("sequence", out var sequence) && (sequence.ValueKind != JsonValueKind.Number || !sequence.TryGetInt64(out var number) || number is < 0 or > 9_007_199_254_740_991) || value.TryGetProperty("isError", out var flag) && flag.ValueKind is not (JsonValueKind.True or JsonValueKind.False) || value.Text("event") is "tool.waiting" or "tool.complete" && (value.Text("tool") is null || value.Text("toolUseId") is null)) { context.Response.StatusCode = 400; return; }
            if (value.TryGetProperty("usage", out var usage))
            {
                var clean = usage.Deserialize<SessionUsage>(Wire.Json);
                if (value.Text("event") != "session.usage" || clean?.Provider != "claude" || clean.ProviderSessionId != value.Text("claudeSessionId")) { context.Response.StatusCode = 400; return; }
            }
            else if (value.Text("event") == "session.usage") { context.Response.StatusCode = 400; return; }
            connection.Received++; connection.Receive(value); context.Response.StatusCode = 204;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or InvalidOperationException or OperationCanceledException) { if (!context.Response.HasStarted) context.Response.StatusCode = 400; }
    }
    public async ValueTask DisposeAsync() { await gate.WaitAsync(); try { disposed = true; connections.Clear(); if (host is not null) await host.DisposeAsync(); host = null; } finally { gate.Release(); } }
}
