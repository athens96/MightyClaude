using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using MightyClaude.Core;

/// <summary>"이 컴퓨터 공유" on Windows (docs/remote-workspaces.md): the listener, the host, and host and client over loopback.</summary>
internal static partial class RemoteHostVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    /// <summary>One raw exchange, for requests HttpClient will not send (no version header, an Origin, a bad head).</summary>
    internal static async Task<string> Raw(int port, string head, byte[]? body = null) => await Raw(port, Encoding.ASCII.GetBytes(head), body);

    internal static async Task<string> Raw(int port, byte[] head, byte[]? body = null)
    {
        using var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, port);
        var stream = client.GetStream();
        try { await stream.WriteAsync(head); if (body is not null) await stream.WriteAsync(body); } catch (IOException) { }
        using var answer = new MemoryStream(); var buffer = new byte[8192];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { int read; while ((read = await stream.ReadAsync(buffer, timeout.Token)) > 0) answer.Write(buffer, 0, read); } catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
        return Encoding.UTF8.GetString(answer.ToArray());
    }

    /// <summary>Opens a connection and sends <paramref name="head"/>, leaving it open; the caller reads or closes it.</summary>
    private static async Task<TcpClient> Open(int port, string head)
    {
        var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, port);
        await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes(head));
        return client;
    }

    /// <summary>Reads until the server ends the connection (or 10 s pass).</summary>
    private static async Task<string> ReadAll(TcpClient client)
    {
        using var answer = new MemoryStream(); var buffer = new byte[8192];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { int read; while ((read = await client.GetStream().ReadAsync(buffer, timeout.Token)) > 0) answer.Write(buffer, 0, read); } catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
        return Encoding.UTF8.GetString(answer.ToArray());
    }

    /// <summary>True when the server ends the connection (end of stream, a reset, or a write refused) within <paramref name="within"/>.</summary>
    private static async Task<bool> ClosedWithin(TcpClient client, TimeSpan within)
    {
        using var timeout = new CancellationTokenSource(within);
        var buffer = new byte[1024];
        try { while (await client.GetStream().ReadAsync(buffer, timeout.Token) > 0) { } return true; }
        catch (OperationCanceledException) { return false; }
        catch (IOException) { return true; }
    }

    internal static async Task ARefusedPeerIsCutOffAtAcceptAndOnePeerHoldsAtMostItsShare()
    {
        var handled = 0;
        await using (var refused = new RemoteHttpListener(IPAddress.Loopback, 0, _ => { Interlocked.Increment(ref handled); return Task.FromResult(RemoteHttpResponse.Json(200, new { })); }, acceptPeer: _ => false))
        {
            var port = refused.Start();
            Check(await Raw(port, "GET /v2/info HTTP/1.1\r\n\r\n") == "" && handled == 0, "a refused peer gets nothing and nothing is handled");
        }
        var asked = new List<IPAddress>();
        await using (var filtered = new RemoteHttpListener(IPAddress.Loopback, 0, _ => Task.FromResult(RemoteHttpResponse.Json(200, new { })), acceptPeer: peer => { lock (asked) asked.Add(peer); return true; }))
        {
            var port = filtered.Start();
            Check((await Raw(port, "GET /v2/info HTTP/1.1\r\n\r\n")).StartsWith("HTTP/1.1 200", StringComparison.Ordinal), "an accepted peer is answered");
            lock (asked) Check(asked.Count == 1 && asked[0].Equals(IPAddress.Loopback), "the filter is asked once with the peer's address");
        }
        {
            var scoped = RemoteHttpListener.PeerAddress(new IPEndPoint(IPAddress.Parse("fe80::5%7"), 1));
            Check(scoped is { ScopeId: 0 } && scoped.ToString() == "fe80::5", "the filter sees an IPv6 peer without its scope");
            Check(RemoteHttpListener.PeerAddress(new IPEndPoint(IPAddress.Parse("::ffff:100.101.102.103"), 1))?.ToString() == "100.101.102.103", "and an IPv4-mapped peer as its IPv4");
        }
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = 0; var bothIn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var capped = new RemoteHttpListener(IPAddress.Loopback, 0, async _ =>
        {
            if (Interlocked.Increment(ref entered) == 2) bothIn.TrySetResult();
            await release.Task; return RemoteHttpResponse.Json(200, new Dictionary<string, bool> { ["ok"] = true });
        }, maximumConnectionsPerPeer: 2);
        var cappedPort = capped.Start();
        var held = new[] { Raw(cappedPort, "GET /a HTTP/1.1\r\n\r\n"), Raw(cappedPort, "GET /b HTTP/1.1\r\n\r\n") };
        Check(await Task.WhenAny(bothIn.Task, Task.Delay(TimeSpan.FromSeconds(10))) == bothIn.Task, "the two first connections reach the handler");
        Check(await Raw(cappedPort, "GET /c HTTP/1.1\r\n\r\n") == "", "a third connection from the same peer is closed at accept");
        Check(entered == 2, "the third is never handled");
        release.SetResult();
        Check((await Task.WhenAll(held)).All(r => r.StartsWith("HTTP/1.1 200", StringComparison.Ordinal)), "the two held ones are answered");
        Check((await Raw(cappedPort, "GET /d HTTP/1.1\r\n\r\n")).StartsWith("HTTP/1.1 200", StringComparison.Ordinal), "a freed slot takes a new connection");
    }

    internal static async Task NoBodyIsReadWithoutLeaveAndTheHeadIsBounded()
    {
        var seenBody = -1;
        await using var server = new RemoteHttpListener(IPAddress.Loopback, 0, request => { seenBody = request.Body.Length; return Task.FromResult(RemoteHttpResponse.Json(request.Headers.ContainsKey("authorization") ? 200 : 401, new { })); },
            bodyLimit: head => head.Headers.ContainsKey("authorization") ? 1024 : 0);
        var port = server.Start();
        Check((await Raw(port, "POST /v2/runs HTTP/1.1\r\nContent-Type: application/json\r\nContent-Length: 100000\r\n\r\n")).StartsWith("HTTP/1.1 401", StringComparison.Ordinal) && seenBody == 0, "the head alone is answered; no body byte is read");
        seenBody = -1;
        Check((await Raw(port, "POST /v2/runs HTTP/1.1\r\nContent-Length: 10\r\n\r\n", Encoding.ASCII.GetBytes("0123456789"))).StartsWith("HTTP/1.1 401", StringComparison.Ordinal) && seenBody == 0, "a body sent without leave never reaches the handler");
        seenBody = -1;
        var oversize = await Raw(port, "POST /v2/x HTTP/1.1\r\nAuthorization: Bearer k\r\nContent-Length: 2048\r\n\r\n", new byte[2048]);
        Check(oversize.StartsWith("HTTP/1.1 413", StringComparison.Ordinal) && seenBody == -1, "over the head's limit is 413, unhandled");
        Check(!oversize.Contains(RemoteProtocol.Header, StringComparison.OrdinalIgnoreCase), "an HTTP-layer refusal carries no version header");
        Check((await Raw(port, "POST /v2/x HTTP/1.1\r\nAuthorization: Bearer k\r\nContent-Length: 999999999\r\n\r\n")).StartsWith("HTTP/1.1 413", StringComparison.Ordinal), "a huge length is refused without waiting for it");
        foreach (var length in new[] { "+2", "2, 2", "0x2", "", "1234567890" })
            Check((await Raw(port, $"POST /v2/x HTTP/1.1\r\nAuthorization: Bearer k\r\nContent-Length: {length}\r\n\r\n{{}}")).StartsWith("HTTP/1.1 413", StringComparison.Ordinal), "a malformed length is refused: " + length);
        Check((await Raw(port, "POST /v2/x HTTP/1.1\r\nAuthorization: Bearer k\r\nContent-Length: 2\r\n\r\n{}")).StartsWith("HTTP/1.1 200", StringComparison.Ordinal) && seenBody == 2, "within it, read whole");
        Check((await Raw(port, "POST /v2/x HTTP/1.1\r\nTransfer-Encoding: chunked\r\n\r\n")).StartsWith("HTTP/1.1 400", StringComparison.Ordinal), "chunked bodies are refused");
        Check((await Raw(port, "POST /v2/x HTTP/1.1\r\nAuthorization: Bearer k\r\nContent-Length: 2\r\nTransfer-Encoding: identity\r\n\r\n{}")).StartsWith("HTTP/1.1 400", StringComparison.Ordinal), "any transfer coding is refused");
        var tooBig = await Raw(port, "GET /v2/x HTTP/1.1\r\nX-Big: " + new string('a', 8300) + "\r\n\r\n");
        Check(tooBig.StartsWith("HTTP/1.1 431", StringComparison.Ordinal) && !tooBig.Contains(RemoteProtocol.Header, StringComparison.OrdinalIgnoreCase), "an oversize head is 431");
        Check((await Raw(port, "GET /v2/x HTTP/1.1\r\nA: 1\r\na: 2\r\n\r\n")).StartsWith("HTTP/1.1 400", StringComparison.Ordinal), "a repeated header is refused");
        Check((await Raw(port, "POST /v2/x HTTP/1.1\r\nAuthorization: Bearer k\r\nContent-Length: 2\r\ncontent-length: 2\r\n\r\n{}")).StartsWith("HTTP/1.1 400", StringComparison.Ordinal), "a repeated length is refused");
        Check((await Raw(port, "GET /v2/x HTTP/1.1\r\nA: 1\r\n folded\r\n\r\n")).StartsWith("HTTP/1.1 400", StringComparison.Ordinal), "a folded line is refused");
        Check((await Raw(port, "GET /v2/x HTTP/1.1\r\nA: 1\nAuthorization: Bearer k\r\n\r\n")).StartsWith("HTTP/1.1 400", StringComparison.Ordinal), "a bare line feed inside a header is refused");
        Check((await Raw(port, "GET /v2/x\rAuthorization: k HTTP/1.1\r\n\r\n")).StartsWith("HTTP/1.1 400", StringComparison.Ordinal), "a bare carriage return in the target is refused");
        Check((await Raw(port, "GET /v2/x HTTP/1.1\r\nBad Name: 1\r\n\r\n")).StartsWith("HTTP/1.1 400", StringComparison.Ordinal), "a header name outside the token set is refused");
        Check((await Raw(port, Encoding.UTF8.GetBytes("GET /v2/x HTTP/1.1\r\nKey: 1\r\n\r\n"))).StartsWith("HTTP/1.1 400", StringComparison.Ordinal), "a non-ASCII header name is refused");
        Check((await Raw(port, [.. Encoding.ASCII.GetBytes("GET /v2/x HTTP/1.1\r\nA: "), 0xff, 0xfe, .. Encoding.ASCII.GetBytes("\r\n\r\n")])).StartsWith("HTTP/1.1 400", StringComparison.Ordinal), "a head that is not UTF-8 is refused");
        Check((await Raw(port, "GET /v2/x HTTP/1.1\r\n" + string.Concat(Enumerable.Range(0, 34).Select(i => $"H{i}: 1\r\n")) + "\r\n")).StartsWith("HTTP/1.1 400", StringComparison.Ordinal), "at most 33 header lines");
        Check((await Raw(port, "GET /v2/x HTTP/1.1\r\n" + string.Concat(Enumerable.Range(0, 32).Select(i => $"H{i}: 1\r\n")) + "Authorization: k\r\n\r\n")).StartsWith("HTTP/1.1 200", StringComparison.Ordinal), "33 header lines are fine");
        Check((await Raw(port, "DELETE /v2/x HTTP/1.1\r\n\r\n")).StartsWith("HTTP/1.1 400", StringComparison.Ordinal), "only GET and POST");
        Check((await Raw(port, "GET /v2/x HTTP/1.0\r\n\r\n")).StartsWith("HTTP/1.1 400", StringComparison.Ordinal), "only HTTP/1.1");
        Check((await Raw(port, "GET http://studio-pc/v2/x HTTP/1.1\r\n\r\n")).StartsWith("HTTP/1.1 400", StringComparison.Ordinal), "only an origin-form target");
        Check((await Raw(port, "POST /v2/x HTTP/1.1\r\nAuthorization: Bearer k\r\nContent-Length: 2\r\n\r\n{}GET / HTTP/1.1\r\n\r\n")).StartsWith("HTTP/1.1 400", StringComparison.Ordinal), "one request per connection");
        Check((await Raw(port, "GET /v2/x HTTP/1.1\r\nAuthorization: Bearer k\r\n\r\nGET / HTTP/1.1\r\n\r\n")).StartsWith("HTTP/1.1 400", StringComparison.Ordinal), "one request per connection, without a body too");
        // Bytes that come in a write of their own after the whole body are still a second request.
        var headSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var pipelinedHandled = false;
        await using (var pipelined = new RemoteHttpListener(IPAddress.Loopback, 0, _ => { pipelinedHandled = true; return Task.FromResult(RemoteHttpResponse.Json(200, new { })); }, bodyLimit: _ => { headSeen.TrySetResult(); return 1024; }))
        {
            using var client = await Open(pipelined.Start(), "POST /v2/x HTTP/1.1\r\nContent-Length: 2\r\n\r\n");
            Check(await Task.WhenAny(headSeen.Task, Task.Delay(TimeSpan.FromSeconds(10))) == headSeen.Task, "the head is read before the body is sent");
            await client.GetStream().WriteAsync("{}GET / HTTP/1.1\r\n\r\n"u8.ToArray());
            Check((await ReadAll(client)).StartsWith("HTTP/1.1 400", StringComparison.Ordinal) && !pipelinedHandled, "a request after the body is refused, unhandled");
        }
        var answer = await Raw(port, "GET /v2/x HTTP/1.1\r\nAuthorization: Bearer k\r\n\r\n");
        Check(answer.Contains("Connection: close", StringComparison.Ordinal) && answer.Contains("Cache-Control: no-store", StringComparison.Ordinal) && answer.Contains("X-Content-Type-Options: nosniff", StringComparison.Ordinal), "every answer closes and is never cached");
    }

    internal static async Task ASlowOrStuckRequestIsCutOffAndStoppingClosesEverything()
    {
        var release = new TaskCompletionSource<RemoteHttpResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = new RemoteHttpListener(IPAddress.Loopback, 0, request =>
        {
            if (request.Target == "/stuck") { entered.TrySetResult(); return release.Task; }
            if (request.Target == "/hang") return new TaskCompletionSource<RemoteHttpResponse>().Task;
            return Task.FromResult(RemoteHttpResponse.Json(200, new Dictionary<string, bool> { ["ok"] = true }, new Dictionary<string, string> { ["X-Ok"] = "1", ["X-Evil"] = "a\r\nSet-Cookie: b" }));
        }, maximumConnectionsPerPeer: 1) { RequestDeadline = TimeSpan.FromMilliseconds(500), HandlerDeadline = TimeSpan.FromMilliseconds(500) };
        try
        {
            Check(!server.IsListening, "not listening before Start");
            var port = server.Start();
            Check(server.IsListening, "listening once started");
            Check(throws(() => server.Start()), "a listener starts once");
            // A head trickled one byte at a time is still cut off at the deadline, and its slot is freed.
            var watch = Stopwatch.StartNew();
            using (var slow = await Open(port, "GET /v2/x HTTP/1.1\r\n"))
            {
                var trickle = Task.Run(async () => { try { for (var i = 0; i < 50; i++) { await slow.GetStream().WriteAsync("X"u8.ToArray()); await Task.Delay(100); } } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { } });
                Check(await ClosedWithin(slow, TimeSpan.FromSeconds(4)), "a head still arriving at the deadline is cut off");
                Check(watch.Elapsed < TimeSpan.FromSeconds(4), "trickling does not extend the deadline");
                await trickle;
            }
            var ok = await Raw(port, "GET /v2/x HTTP/1.1\r\n\r\n");
            Check(ok.StartsWith("HTTP/1.1 200", StringComparison.Ordinal) && ok.Contains("X-Ok: 1\r\n", StringComparison.Ordinal) && !ok.Contains("Set-Cookie", StringComparison.Ordinal), "the slot is freed, and a header with a line break is never written");
            // A handler that never answers holds its connection only until the handler deadline.
            using (var hang = await Open(port, "GET /hang HTTP/1.1\r\n\r\n"))
                Check(await ClosedWithin(hang, TimeSpan.FromSeconds(4)), "a handler that never answers is cut off");
            Check((await Raw(port, "GET /v2/x HTTP/1.1\r\n\r\n")).StartsWith("HTTP/1.1 200", StringComparison.Ordinal), "and its slot is freed");
            // Stopping closes a live connection and takes no new ones.
            using var stuck = await Open(port, "GET /stuck HTTP/1.1\r\n\r\n");
            Check(await Task.WhenAny(entered.Task, Task.Delay(TimeSpan.FromSeconds(10))) == entered.Task, "the stuck request reaches its handler");
            var disposing = server.DisposeAsync().AsTask();
            Check(await ClosedWithin(stuck, TimeSpan.FromSeconds(4)), "stopping closes a live connection");
            Check(await Task.WhenAny(disposing, Task.Delay(TimeSpan.FromSeconds(10))) == disposing, "stopping ends without waiting on the handler");
            release.SetResult(RemoteHttpResponse.Json(200, new { }));
            var refusedAfter = false;
            try { refusedAfter = await Raw(port, "GET /v2/x HTTP/1.1\r\n\r\n") == ""; } catch (SocketException) { refusedAfter = true; }
            Check(refusedAfter, "a stopped listener takes no connection");
            Check(throws(() => server.Start()), "a stopped listener never starts again");
            Check(!server.IsListening, "a stopped listener is not listening");
        }
        finally { await server.DisposeAsync(); }

        static bool throws(Action action) { try { action(); return false; } catch (InvalidOperationException) { return true; } }
    }

    internal static async Task AThrowingFilterOrHandlerNeverStopsTheListener()
    {
        var filterCalls = 0;
        await using var server = new RemoteHttpListener(IPAddress.Loopback, 0, request => request.Target switch
        {
            "/throw" => throw new InvalidOperationException("handler failed"),
            "/fault" => Task.FromException<RemoteHttpResponse>(new InvalidOperationException("handler faulted")),
            _ => Task.FromResult(RemoteHttpResponse.Json(200, new { })),
        }, bodyLimit: head => head.Target == "/limit" ? throw new InvalidOperationException("limit failed") : 1024,
            acceptPeer: _ => Interlocked.Increment(ref filterCalls) == 1 ? throw new InvalidOperationException("filter failed") : true, maximumConnectionsPerPeer: 1);
        var port = server.Start();
        Check(await Raw(port, "GET /v2/x HTTP/1.1\r\n\r\n") == "", "a filter that throws refuses the peer");
        foreach (var target in new[] { "/throw", "/fault", "/limit" })
        {
            Check(await Raw(port, $"GET {target} HTTP/1.1\r\n\r\n") == "", "a failing request ends its connection unanswered: " + target);
            Check((await Raw(port, "GET /v2/x HTTP/1.1\r\n\r\n")).StartsWith("HTTP/1.1 200", StringComparison.Ordinal), "the listener and the peer's slot outlive " + target);
        }
    }

    internal static async Task ADrainingConnectionHoldsItsPeersSlotUntilTheDrainEnds()
    {
        var handled = 0;
        var released = new SemaphoreSlim(0);
        await using var server = new RemoteHttpListener(IPAddress.Loopback, 0, request => { Interlocked.Increment(ref handled); return Task.FromResult(RemoteHttpResponse.Json(401, new { })); },
            bodyLimit: _ => 0, maximumConnectionsPerPeer: 1) { SlotReleased = () => released.Release() };
        var port = server.Start();
        // A keyless POST is answered from its head and then drained; while it drains it still holds the peer's one slot.
        using (var first = await Open(port, "POST /x HTTP/1.1\r\nContent-Length: 5\r\n\r\n"))
        {
            Check((await ReadAll(first)).StartsWith("HTTP/1.1 401", StringComparison.Ordinal), "the head alone is answered");
            Check(await Raw(port, "POST /x HTTP/1.1\r\nContent-Length: 5\r\n\r\n") == "" && handled == 1, "a second connection while the first drains is refused at accept, unhandled");
        }
        Check(await released.WaitAsync(TimeSpan.FromSeconds(10)), "the closed one gives its slot back");
        Check((await Raw(port, "GET /x HTTP/1.1\r\n\r\n")).StartsWith("HTTP/1.1 401", StringComparison.Ordinal) && handled == 2, "and the next one is answered");
        Check(await released.WaitAsync(TimeSpan.FromSeconds(10)), "the answered one gives its slot back");
        // One that keeps sending after its answer is drained for a bounded time only, then closed, and its slot is freed.
        var watch = Stopwatch.StartNew();
        using (var trickler = await Open(port, "POST /x HTTP/1.1\r\nContent-Length: 999\r\n\r\n"))
        {
            Check((await ReadAll(trickler)).StartsWith("HTTP/1.1 401", StringComparison.Ordinal), "the trickler is answered");
            var cutOff = false;
            try { while (watch.Elapsed < TimeSpan.FromSeconds(5)) { await trickler.GetStream().WriteAsync("X"u8.ToArray()); await Task.Delay(50); } }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException) { cutOff = true; }
            Check(cutOff && watch.Elapsed < TimeSpan.FromSeconds(3), "a peer that keeps sending after its answer is closed within about 2 s");
        }
        Check(await released.WaitAsync(TimeSpan.FromSeconds(10)), "the trickler's slot is given back");
        Check((await Raw(port, "GET /x HTTP/1.1\r\n\r\n")).StartsWith("HTTP/1.1 401", StringComparison.Ordinal), "and the peer is answered again");
    }

    internal static async Task AllPeersTogetherHoldAtMostTheOpenConnectionCap()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allIn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = 0;
        await using var server = new RemoteHttpListener(IPAddress.Loopback, 0, async _ =>
        {
            if (Interlocked.Increment(ref entered) == RemoteHttpListener.MaximumOpenConnections) allIn.TrySetResult();
            await release.Task; return RemoteHttpResponse.Json(200, new { });
        });
        var port = server.Start();
        var held = Enumerable.Range(0, RemoteHttpListener.MaximumOpenConnections).Select(i => Raw(port, $"GET /{i} HTTP/1.1\r\n\r\n")).ToArray();
        Check(await Task.WhenAny(allIn.Task, Task.Delay(TimeSpan.FromSeconds(20))) == allIn.Task, "64 connections reach the handler");
        Check(await Raw(port, "GET /over HTTP/1.1\r\n\r\n") == "" && entered == RemoteHttpListener.MaximumOpenConnections, "the 65th is closed at accept, unhandled");
        release.SetResult();
        Check((await Task.WhenAll(held)).All(r => r.StartsWith("HTTP/1.1 200", StringComparison.Ordinal)), "the held ones are answered");
        Check((await Raw(port, "GET /next HTTP/1.1\r\n\r\n")).StartsWith("HTTP/1.1 200", StringComparison.Ordinal), "a freed slot takes a new connection");
    }
}
