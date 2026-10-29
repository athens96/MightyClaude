using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using MightyClaude.Core;

internal static class ModBridgeParityVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static Dictionary<string, object?> Envelope(ModBridge.Connection connection, string kind = "agent.spawn", object? graph = null) => new()
        { ["version"] = 1, ["runId"] = connection.Id, ["claudeSessionId"] = "graph-session", ["event"] = kind, ["sequence"] = 1, ["graph"] = graph };
    private static Dictionary<string, object?> Starting() => new()
        { ["version"] = 1, ["phase"] = "starting", ["parentToolUseId"] = "spawn-tool", ["parentAgentId"] = "parent-agent", ["name"] = "검토", ["agentType"] = "Explore" };
    private static string Json(object value) => JsonSerializer.Serialize(value, Wire.Json);
    private static async Task<HttpStatusCode> Post(HttpClient http, ModBridge.Connection connection, object value, bool graphHeader = true, string? token = null, string? browserHeader = null)
        => await PostRaw(http, connection, Json(value), graphHeader, token, browserHeader);
    private static async Task<HttpStatusCode> PostRaw(HttpClient http, ModBridge.Connection connection, string value, bool graphHeader = true, string? token = null, string? browserHeader = null, bool chunked = false, bool freshConnection = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, connection.Url);
        // Oversized uploads can close Kestrel's connection while it discards
        // unread bytes. Each boundary check gets its own connection.
        if (freshConnection) request.Headers.ConnectionClose = true;
        request.Headers.Add("Authorization", "Bearer " + (token ?? connection.Token));
        if (graphHeader) request.Headers.Add("X-Mighty-Graph", "1");
        if (browserHeader is not null) request.Headers.Add(browserHeader, "https://example.invalid");
        request.Content = chunked ? new ChunkedJson(value) : new StringContent(value, Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request);
        return response.StatusCode;
    }
    // No computed Content-Length: the host must count actual bytes, including
    // whitespace after the JSON object, when Kestrel de-chunks the request.
    private sealed class ChunkedJson : HttpContent
    {
        private readonly byte[] bytes;
        internal ChunkedJson(string value) { bytes = Encoding.UTF8.GetBytes(value); Headers.ContentType = new("application/json"); }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await stream.WriteAsync(bytes.AsMemory(0, Math.Min(1024, bytes.Length)));
            await stream.WriteAsync(bytes.AsMemory(Math.Min(1024, bytes.Length)));
        }
    }
    private sealed class Clock : TimeProvider
    {
        private long ticks = DateTimeOffset.UtcNow.UtcTicks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
        internal void Advance() => Interlocked.Add(ref ticks, TimeSpan.TicksPerSecond);
    }

    internal static async Task GraphLifecycleCarriesBoundedContent()
    {
        var seen = new List<JsonElement>();
        await using var bridge = new ModBridge();
        using var connection = await bridge.RegisterAsync(value => seen.Add(value.Clone()), CancellationToken.None, graphEnabled: true);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var start = Starting(); start["input"] = new string('i', 16_384);
        Check(await Post(http, connection, Envelope(connection, graph: start)) == HttpStatusCode.NoContent, "Graph input at the macOS 16 KiB boundary must reach the run.");
        var running = Starting(); running["phase"] = "running"; running["agentId"] = "child-agent"; running["model"] = "claude-sonnet-4-6";
        Check(await Post(http, connection, Envelope(connection, graph: running)) == HttpStatusCode.NoContent, "Spawned agent identities must reach the run.");
        var finished = new Dictionary<string, object?>(running) { ["phase"] = "completed", ["output"] = new string('r', 32_768) };
        Check(await Post(http, connection, Envelope(connection, "agent.complete", finished)) == HttpStatusCode.NoContent, "Completed agent output at 32 KiB must reach the run.");
        Check(seen.Count == 3 && connection.Received == 3, "Every valid lifecycle event must be delivered exactly once.");
        Check(seen[0].GetProperty("graph").Text("input")?.Length == 16_384 && seen[0].GetProperty("graph").Text("name") == "검토", "Input and Unicode labels must remain intact after HTTP parsing.");
        var end = seen[2].GetProperty("graph");
        Check(seen[2].Text("event") == "agent.complete" && end.Text("phase") == "completed" && end.Text("agentId") == "child-agent" && end.Text("parentAgentId") == "parent-agent" && end.Text("parentToolUseId") == "spawn-tool" && end.Text("output")?.Length == 32_768 && end.Text("model") == "claude-sonnet-4-6", "The callback must retain graph ownership, model and completed output.");
    }

    internal static async Task GraphOptInAuthenticationAndRunIsolation()
    {
        var richSeen = 0; var legacySeen = 0;
        await using var bridge = new ModBridge();
        using var rich = await bridge.RegisterAsync(_ => Interlocked.Increment(ref richSeen), CancellationToken.None, graphEnabled: true);
        using var legacy = await bridge.RegisterAsync(_ => Interlocked.Increment(ref legacySeen), CancellationToken.None);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var graph = Envelope(rich, graph: Starting());
        Check(await Post(http, rich, graph, token: new string('0', 64)) == HttpStatusCode.Unauthorized, "Graph requests require the authenticated run's bearer token.");
        Check(await Post(http, legacy, graph) == HttpStatusCode.BadRequest, "A different run's valid token cannot publish this run's graph.");
        Check(await Post(http, legacy, Envelope(legacy, graph: Starting())) == HttpStatusCode.BadRequest, "A run must explicitly opt into graph observations.");
        Check(await Post(http, rich, graph, graphHeader: false) == HttpStatusCode.BadRequest, "Even small graph requests require the graph header.");
        foreach (var header in new[] { "Origin", "Sec-Fetch-Site" })
            Check(await Post(http, rich, graph, browserHeader: header) == HttpStatusCode.Forbidden, "Browser-origin graph requests must be rejected before delivery.");
        Check(await Post(http, rich, graph) == HttpStatusCode.NoContent, "Valid graph metadata must remain usable after rejected requests.");
        var original = Envelope(legacy, "session.start"); original.Remove("graph");
        Check(await Post(http, legacy, original, graphHeader: false) == HttpStatusCode.NoContent, "Legacy metadata remains supported without graph opt-in.");
        rich.Dispose();
        Check(await Post(http, rich, graph) == HttpStatusCode.Unauthorized, "Disposed run tokens must be revoked.");
        Check(richSeen == 1 && legacySeen == 1, "Rejected requests must never cross run callbacks.");
    }

    internal static async Task MalformedGraphIsRejectedBeforeDelivery()
    {
        var seen = 0;
        await using var bridge = new ModBridge();
        using var connection = await bridge.RegisterAsync(_ => Interlocked.Increment(ref seen), CancellationToken.None, graphEnabled: true);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var valid = Starting(); valid["phase"] = "running"; valid["agentId"] = "child-agent";
        (string Key, object? Value)[] changes =
        [
            ("version", true), ("version", 2), ("version", 1.5), ("phase", "completed"), ("phase", null),
            ("agentId", "child-agent\n"), ("agentId", 123), ("agentId", null), ("agentId", new string('a', 129)), ("parentAgentId", "child-agent"),
            ("parentToolUseId", null), ("parentToolUseId", "path/to/tool"), ("model", "not a model"),
            ("input", new string('i', 16_385)), ("input", string.Concat(Enumerable.Repeat("한", 5462))),
            ("output", new string('o', 32_769)), ("credentials", "never accepted"),
            ("name", "\u001b[31mforged"), ("name", "two\nlines"), ("name", new string('n', 161)),
            ("agentType", new string('t', 161)), ("model", new string('m', 201))
        ];
        foreach (var (key, value) in changes)
        {
            var graph = new Dictionary<string, object?>(valid) { [key] = value };
            Check(await Post(http, connection, Envelope(connection, graph: graph)) == HttpStatusCode.BadRequest, "Malformed graph field was accepted: " + key);
        }
        var noParent = new Dictionary<string, object?>(valid); noParent.Remove("parentToolUseId");
        Check(await Post(http, connection, Envelope(connection, graph: noParent)) == HttpStatusCode.BadRequest, "Spawn must identify its parent tool.");
        Check(await Post(http, connection, Envelope(connection, "agent.complete", new { version = 1, phase = "completed" })) == HttpStatusCode.BadRequest, "Completion must identify the actual agent.");
        var missing = Envelope(connection); missing.Remove("graph");
        Check(await Post(http, connection, missing) == HttpStatusCode.BadRequest, "Agent lifecycle events require graph metadata.");
        Check(await Post(http, connection, Envelope(connection, "session.start", valid)) == HttpStatusCode.BadRequest, "Ordinary events cannot smuggle graph metadata.");
        Check(seen == 0 && connection.Received == 0, "Invalid graph content must not reach any consumer.");
        // The Mac's validModGraph also requires each ID to be clean at 128 bytes on one line: IDs at that boundary still pass.
        var longest = new Dictionary<string, object?>(valid) { ["agentId"] = "a" + new string('b', 127), ["parentAgentId"] = "p" + new string('q', 127), ["parentToolUseId"] = "t" + new string('u', 127) };
        Check(await Post(http, connection, Envelope(connection, graph: longest)) == HttpStatusCode.NoContent && seen == 1, "Clean 128-byte graph identities are accepted, as on the Mac.");
    }

    internal static async Task BodyAllowanceIsLimitedToAuthenticatedGraphRequests()
    {
        var seen = 0;
        await using var bridge = new ModBridge();
        using var connection = await bridge.RegisterAsync(_ => Interlocked.Increment(ref seen), CancellationToken.None, graphEnabled: true);
        using var legacy = await bridge.RegisterAsync(_ => Interlocked.Increment(ref seen), CancellationToken.None);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        Task<HttpStatusCode> Send(ModBridge.Connection target, string body, bool graphHeader = true, string? token = null, bool chunked = false)
            => PostRaw(http, target, body, graphHeader, token, chunked: chunked, freshConnection: true);
        var valid = Json(Envelope(connection, graph: Starting()));
        var graphBody = valid + new string(' ', 65_536 - Encoding.UTF8.GetByteCount(valid));
        Check(await Send(connection, graphBody) == HttpStatusCode.NoContent, "An authenticated graph body can use the 64 KiB limit.");
        Check(await Send(connection, graphBody, chunked: true) == HttpStatusCode.NoContent, "Chunked graph bodies use the same authenticated limit.");
        foreach (var chunked in new[] { false, true })
        {
            Check(IsTooLarge(await Send(connection, graphBody + " ", chunked: chunked)), "Graph body beyond 64 KiB must be rejected.");
            Check(IsTooLarge(await Send(connection, graphBody, graphHeader: false, chunked: chunked)), "The graph header is required for the larger limit.");
            Check(IsTooLarge(await Send(legacy, graphBody, chunked: chunked)), "A graph-disabled run must retain the 16 KiB limit.");
        }
        Check(await Send(connection, graphBody, token: new string('0', 64)) == HttpStatusCode.Unauthorized, "Unauthenticated requests do not obtain the larger allowance.");
        var ordinary = Envelope(connection, "session.start"); ordinary.Remove("graph");
        var small = Json(ordinary); small += new string(' ', 16_384 - Encoding.UTF8.GetByteCount(small));
        Check(await Send(connection, small) == HttpStatusCode.NoContent, "Ordinary telemetry still accepts exactly 16 KiB.");
        Check(await Send(connection, small + " ") == HttpStatusCode.RequestEntityTooLarge, "Graph header cannot enlarge ordinary telemetry's limit.");
        Check(await Send(connection, small + " ", chunked: true) == HttpStatusCode.RequestEntityTooLarge, "Chunked trailing whitespace also counts toward the ordinary limit.");
        Check(seen == 3, "Only the two graph uploads and the bounded ordinary event may enter consumers.");
    }
    private static bool IsTooLarge(HttpStatusCode status) => status is HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge;

    internal static async Task RateLimitRecoversAndLongRunsKeepReceiving()
    {
        var clock = new Clock(); var received = 0;
        await using var bridge = new ModBridge(clock);
        using var connection = await bridge.RegisterAsync(_ => Interlocked.Increment(ref received), CancellationToken.None);
        using var other = await bridge.RegisterAsync(_ => { }, CancellationToken.None);
        using var handler = new SocketsHttpHandler { MaxConnectionsPerServer = 8 };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        var body = Envelope(connection, "session.start"); body.Remove("graph");
        for (var window = 0; window < 35; window++)
        {
            if (window > 0) clock.Advance();
            var codes = await Task.WhenAll(Enumerable.Range(0, 120).Select(_ => Post(http, connection, body, graphHeader: false)));
            Check(codes.All(code => code == HttpStatusCode.NoContent), "Valid events below the per-second limit must remain accepted after 4096 total events.");
            Check(await Post(http, connection, body, graphHeader: false) == HttpStatusCode.TooManyRequests, "The 121st request in each window must be rate limited.");
        }
        var otherBody = Envelope(other, "session.start"); otherBody.Remove("graph");
        Check(await Post(http, other, otherBody, graphHeader: false) == HttpStatusCode.NoContent, "One run's rate limit must not throttle another run.");
        Check(connection.Received == 4200 && received == 4200, "Concurrent callbacks and received counts must stay exact for long runs.");
        clock.Advance(); connection.Received = int.MaxValue;
        Check(await Post(http, connection, body, graphHeader: false) == HttpStatusCode.NoContent && connection.Received == int.MaxValue, "The diagnostic counter saturates without terminating the observation stream.");
    }

    internal static async Task RevokedRunCannotReceiveAnAlreadyAuthenticatedUpload()
    {
        var received = 0;
        await using var bridge = new ModBridge();
        using var connection = await bridge.RegisterAsync(_ => Interlocked.Increment(ref received), CancellationToken.None, graphEnabled: true);
        var uri = new Uri(connection.Url); var bytes = Encoding.UTF8.GetBytes(Json(Envelope(connection, graph: Starting())));
        using var socket = new TcpClient(); await socket.ConnectAsync(uri.Host, uri.Port);
        await using var stream = socket.GetStream();
        var headers = $"POST /events HTTP/1.1\r\nHost: {uri.Host}:{uri.Port}\r\nAuthorization: Bearer {connection.Token}\r\nContent-Type: application/json\r\nX-Mighty-Graph: 1\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(headers)); await stream.WriteAsync(bytes.AsMemory(0, 1)); await stream.FlushAsync();
        await Verification.Until(() => connection.Requests == 1);
        connection.Dispose();
        await stream.WriteAsync(bytes.AsMemory(1)); await stream.FlushAsync();
        using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
        var status = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Check(status?.Contains("410", StringComparison.Ordinal) == true && received == 0 && connection.Received == 0, "Revoking a run while its authenticated body is pending must prevent callback delivery.");
    }
}
