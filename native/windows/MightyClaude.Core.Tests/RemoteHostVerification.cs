using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using MightyClaude.Core;

/// <summary>"이 컴퓨터 공유" on Windows (docs/remote-workspaces.md): the listener, the host, and host and client over loopback.</summary>
internal static partial class RemoteHostVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    /// <summary>One raw exchange, for requests HttpClient will not send (no version header, an Origin, a bad head).</summary>
    internal static async Task<string> Raw(int port, string head, byte[]? body = null, IPAddress? address = null) => await Raw(port, Encoding.ASCII.GetBytes(head), body, address);

    internal static async Task<string> Raw(int port, byte[] head, byte[]? body = null, IPAddress? address = null)
    {
        using var client = new TcpClient((address ?? IPAddress.Loopback).AddressFamily);
        NetworkStream stream;
        // A peer cut off at accept may be reset while it connects or before it takes its stream: it received nothing.
        try { await client.ConnectAsync(address ?? IPAddress.Loopback, port); stream = client.GetStream(); }
        catch (Exception ex) when (CutOff(ex)) { return ""; }
        try { await stream.WriteAsync(head); if (body is not null) await stream.WriteAsync(body); } catch (IOException) { }
        using var answer = new MemoryStream(); var buffer = new byte[8192];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { int read; while ((read = await stream.ReadAsync(buffer, timeout.Token)) > 0) answer.Write(buffer, 0, read); } catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
        return Encoding.UTF8.GetString(answer.ToArray());
    }

    /// <summary>Opens a connection and sends <paramref name="head"/>, leaving it open; the caller reads or closes it.</summary>
    private static async Task<TcpClient> Open(int port, string head)
    {
        // Every connection opened here must be accepted: a reset while connecting fails the check instead of reading as "closed",
        // which would let the deadline and close-on-stop checks pass without testing anything (only Raw expects a cut-off at accept).
        var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, port);
        await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes(head));
        return client;
    }

    /// <summary>The server reset or aborted the connection, or it was no longer connected when its stream was asked for.</summary>
    private static bool CutOff(Exception ex) => ex is SocketException { SocketErrorCode: SocketError.ConnectionReset or SocketError.ConnectionAborted } or InvalidOperationException;

    /// <summary>Reads until the server ends the connection (or 10 s pass).</summary>
    private static async Task<string> ReadAll(TcpClient client)
    {
        using var answer = new MemoryStream(); var buffer = new byte[8192];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        // The stream is taken once: after the peer's reset a second GetStream throws InvalidOperationException (not connected).
        try { var stream = client.GetStream(); int read; while ((read = await stream.ReadAsync(buffer, timeout.Token)) > 0) answer.Write(buffer, 0, read); }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidOperationException) { }
        return Encoding.UTF8.GetString(answer.ToArray());
    }

    /// <summary>True when the server ends the connection (end of stream, a reset, or a write refused) within <paramref name="within"/>.</summary>
    private static async Task<bool> ClosedWithin(TcpClient client, TimeSpan within)
    {
        using var timeout = new CancellationTokenSource(within);
        var buffer = new byte[1024];
        try { var stream = client.GetStream(); while (await stream.ReadAsync(buffer, timeout.Token) > 0) { } return true; }
        catch (OperationCanceledException) { return false; }
        // A socket already reset reads as closed: GetStream on it throws InvalidOperationException.
        catch (Exception ex) when (ex is IOException or InvalidOperationException) { return true; }
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
        // A body larger than the first buffer, of no power-of-two length, arrives whole and exact.
        byte[]? grown = null;
        await using (var large = new RemoteHttpListener(IPAddress.Loopback, 0, request => { grown = request.Body; return Task.FromResult(RemoteHttpResponse.Json(200, new { })); }))
        {
            var sent = new byte[200_003]; for (var i = 0; i < sent.Length; i++) sent[i] = (byte)(i * 31 + i / 251);
            Check((await Raw(large.Start(), $"POST /v2/x HTTP/1.1\r\nContent-Length: {sent.Length}\r\n\r\n", sent)).StartsWith("HTTP/1.1 200", StringComparison.Ordinal), "a 200 KiB body is taken within the default limit");
            Check(grown is not null && grown.AsSpan().SequenceEqual(sent), "the handler sees the exact length and bytes");
        }
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
            if (request.Target == "/framing") return Task.FromResult(RemoteHttpResponse.Json(200, new { }, new Dictionary<string, string> { ["Content-Length"] = "0", ["connection"] = "keep-alive", ["Content-Type"] = "text/html", ["Transfer-Encoding"] = "chunked" }));
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
                var slowStream = slow.GetStream();
                var trickle = Task.Run(async () => { try { for (var i = 0; i < 50; i++) { await slowStream.WriteAsync("X"u8.ToArray()); await Task.Delay(100); } } catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { } });
                Check(await ClosedWithin(slow, TimeSpan.FromSeconds(4)), "a head still arriving at the deadline is cut off");
                Check(watch.Elapsed < TimeSpan.FromSeconds(4), "trickling does not extend the deadline");
                await trickle;
            }
            var ok = await Raw(port, "GET /v2/x HTTP/1.1\r\n\r\n");
            Check(ok.StartsWith("HTTP/1.1 200", StringComparison.Ordinal) && ok.Contains("X-Ok: 1\r\n", StringComparison.Ordinal) && !ok.Contains("Set-Cookie", StringComparison.Ordinal), "the slot is freed, and a header with a line break is never written");
            var framed = await Raw(port, "GET /framing HTTP/1.1\r\n\r\n");
            Check(framed.StartsWith("HTTP/1.1 200", StringComparison.Ordinal) && framed.EndsWith("\r\n\r\n{}", StringComparison.Ordinal), "the answer is framed by its own length");
            Check(framed.Split("\r\n").Count(line => line.StartsWith("content-length:", StringComparison.OrdinalIgnoreCase)) == 1 && framed.Contains("\r\nContent-Length: 2\r\n", StringComparison.Ordinal), "exactly one, correct Content-Length");
            Check(framed.Contains("\r\nConnection: close\r\n", StringComparison.Ordinal) && !framed.Contains("keep-alive", StringComparison.OrdinalIgnoreCase) && !framed.Contains("chunked", StringComparison.OrdinalIgnoreCase) && !framed.Contains("text/html", StringComparison.Ordinal), "a handler cannot change the framing headers");
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
        // A waiting connection's own reset or abort never counts toward ending the listener; an error of the listener itself does.
        Check(!RemoteHttpListener.CountsAsAcceptFailure(SocketError.ConnectionReset) && !RemoteHttpListener.CountsAsAcceptFailure(SocketError.ConnectionAborted), "a reset or aborted connection never counts");
        foreach (var code in new[] { SocketError.NetworkDown, SocketError.AddressNotAvailable, SocketError.TooManyOpenSockets, SocketError.NoBufferSpaceAvailable, SocketError.NetworkUnreachable })
            Check(RemoteHttpListener.CountsAsAcceptFailure(code), "an error of the listener counts: " + code);
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
            bodyLimit: _ => 0, maximumConnectionsPerPeer: 1) { SlotReleased = () => released.Release(), DrainDeadline = TimeSpan.FromSeconds(5) };
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
        // One that sends more than 64 KiB at once after its answer is closed once that much is discarded, long before the drain deadline.
        using (var flooder = await Open(port, "POST /x HTTP/1.1\r\nContent-Length: 999999\r\n\r\n"))
        {
            Check((await ReadAll(flooder)).StartsWith("HTTP/1.1 401", StringComparison.Ordinal), "the flooder is answered");
            try { var flood = flooder.GetStream(); await flood.WriteAsync(new byte[80 * 1024]); } catch (Exception ex) when (ex is IOException or SocketException or InvalidOperationException) { }
            Check(await released.WaitAsync(TimeSpan.FromSeconds(3)), "past 64 KiB the drain ends and the slot is given back before its deadline");
        }
        Check((await Raw(port, "GET /x HTTP/1.1\r\n\r\n")).StartsWith("HTTP/1.1 401", StringComparison.Ordinal), "and the peer is answered again");
        Check(await released.WaitAsync(TimeSpan.FromSeconds(10)), "that one gives its slot back too");
        // One that keeps sending after its answer is drained for the default deadline only, then closed, and its slot is freed.
        await using var bounded = new RemoteHttpListener(IPAddress.Loopback, 0, _ => Task.FromResult(RemoteHttpResponse.Json(401, new { })), bodyLimit: _ => 0, maximumConnectionsPerPeer: 1) { SlotReleased = () => released.Release() };
        port = bounded.Start();
        var watch = Stopwatch.StartNew();
        using (var trickler = await Open(port, "POST /x HTTP/1.1\r\nContent-Length: 999\r\n\r\n"))
        {
            Check((await ReadAll(trickler)).StartsWith("HTTP/1.1 401", StringComparison.Ordinal), "the trickler is answered");
            var cutOff = false;
            try { var trickled = trickler.GetStream(); while (watch.Elapsed < TimeSpan.FromSeconds(5)) { await trickled.WriteAsync("X"u8.ToArray()); await Task.Delay(50); } }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or InvalidOperationException) { cutOff = true; }
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

    /// <summary>A command line split the way the C runtime powershell.exe starts with splits it (CommandLineToArgvW after the program name).</summary>
    private static List<string> Argv(string line)
    {
        var result = new List<string>(); var at = 0;
        while (true)
        {
            while (at < line.Length && line[at] is ' ' or '\t') at++;
            if (at >= line.Length) return result;
            var value = new StringBuilder(); var quoted = false;
            while (at < line.Length && (quoted || line[at] is not (' ' or '\t')))
            {
                var slashes = 0;
                while (at < line.Length && line[at] == '\\') { slashes++; at++; }
                if (at < line.Length && line[at] == '"')
                {
                    value.Append('\\', slashes / 2);
                    if (slashes % 2 == 1) value.Append('"');
                    else if (quoted && at + 1 < line.Length && line[at + 1] == '"') { value.Append('"'); at++; }
                    else quoted = !quoted;
                    at++;
                }
                else { value.Append('\\', slashes); if (at < line.Length && (quoted || line[at] is not (' ' or '\t'))) value.Append(line[at++]); }
            }
            result.Add(value.ToString());
        }
    }

    internal static async Task CommandsRunInPowerShellFromTheSystemFolderAndGraphNodesReachTheHost()
    {
        var (binary, arguments) = RemoteCommandShell.Launch("Get-Location; echo \"a b\"", @"C:\Windows\System32\");
        Check(binary == @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe" && arguments.SequenceEqual(new[] { "-NoProfile", "-NonInteractive", "-Command", "Get-Location; echo \"a b\"" }), "powershell.exe -NoProfile -NonInteractive -Command <text>, from the system folder");
        var line = RemoteCommandShell.CommandLine("echo \"a b\"", @"C:\Windows\System32");
        Check(line.Contains(" -NoProfile -NonInteractive -Command ", StringComparison.Ordinal) && line.EndsWith(ChildProcess.QuoteWindows("echo \"a b\""), StringComparison.Ordinal), "the text is one quoted argument: " + line);
        // A peer's text never becomes more than the one argument after -Command, whatever quotes and backslashes it holds.
        foreach (var text in new[] { "a\" -EncodedCommand ZQBjAGgAbwA=", "ends in a slash\\", "\\\" -File x.ps1", "x\\\\\" & calc \"", "\"\"", " lead and trail ", "tab\there", "%PATH% & ^ | < > ;", "-", "'single' `tick` $(sub)" })
        {
            var argv = Argv(RemoteCommandShell.CommandLine(text, @"C:\Windows\System32"));
            Check(argv.SequenceEqual(new[] { @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", "-NoProfile", "-NonInteractive", "-Command", text }), "one argument for " + text + ": " + string.Join(" | ", argv));
        }
        // CreateProcess takes at most 32,767 characters with its NUL: a host can tell which commands fit before it starts one.
        var room = 32766 - RemoteCommandShell.CommandLine("", @"C:\Windows\System32").Length + 2;
        Check(RemoteCommandShell.Fits("echo x", @"C:\Windows\System32") && RemoteCommandShell.CommandLine(new string('a', room), @"C:\Windows\System32").Length == 32766
            && RemoteCommandShell.Fits(new string('a', room), @"C:\Windows\System32") && !RemoteCommandShell.Fits(new string('a', room + 1), @"C:\Windows\System32"), "a command line of 32,766 characters fits and one more does not");
        // Only the system folder Windows reports: never a relative or drive-relative one, which would resolve against the current folder.
        foreach (var folder in new[] { "", "System32", @"\Windows\System32", "C:Windows", @"..\System32", "C:\\Win\"dows" })
        {
            var refused = false;
            try { RemoteCommandShell.Launch("echo x", folder); } catch (ArgumentException) { refused = true; }
            Check(refused, "a system folder that is not a full path is refused: " + folder);
        }

        var directory = Verification.Temp();
        try
        {
            var workspace = new Workspace { Path = directory };
            var plugin = Path.Combine(directory, "plugin"); Directory.CreateDirectory(Path.Combine(plugin, ".claude-plugin")); await File.WriteAllTextAsync(Path.Combine(plugin, ".claude-plugin", "plugin.json"), "{}");
            var events = new ConcurrentQueue<RunEvent>(); var nodes = new ConcurrentQueue<(string Session, ExecutionGraphNode Node)>();
            await using var catalog = new ProviderCatalog((_, _) => Task.FromResult<CliCommand?>(Verification.Self("--fake-cli", "codex", Path.Combine(directory, "codex"))));
            await using var runner = new RunManager(_ => Task.FromResult(workspace), catalog, plugin, events.Enqueue, _ => { }) { PowerShellCommands = true, GraphNodes = (id, node) => nodes.Enqueue((id, node)) };
            IRemoteRunner remote = runner;
            await remote.StartAsync(new StartRunRequest("job-a", workspace.Id, "claude", "hello", [], Provider: "codex"));
            await Verification.Until(() => events.Any(e => e.SessionId == "job-a" && e.Status is "completed" or "error"), 20000);
            Check(nodes.Any(n => n.Session == "job-a" && n.Node.Id.Length > 0), "the run's graph nodes reach the host as they change");
            Check(nodes.All(n => n.Session == "job-a"), "every node names its run");
            // On Windows the command also shows that a program it names is never looked for in the shared folder first.
            await remote.StartAsync(new StartRunRequest("job-b", workspace.Id, "shell", OperatingSystem.IsWindows() ? "echo remote-ok; echo \"nocd=$env:NoDefaultCurrentDirectoryInExePath\"" : "echo remote-ok", []));
            await Verification.Until(() => events.Any(e => e.SessionId == "job-b" && e.Status is "completed" or "error"), 20000);
            Check(events.Any(e => e.SessionId == "job-b" && e.Entry?.Text.Contains("remote-ok", StringComparison.Ordinal) == true), "a command runs (PowerShell on Windows, /bin/sh elsewhere)");
            if (OperatingSystem.IsWindows()) Check(events.Any(e => e.SessionId == "job-b" && e.Entry?.Text.Contains("nocd=1", StringComparison.Ordinal) == true), "the command runs with NoDefaultCurrentDirectoryInExePath=1");
            Check(!nodes.Any(n => n.Session == "job-b"), "a command pane has no graph");

            // A remote run works in the shared folder alone: a worktree folder is never taken from the host's caller.
            var elsewhere = Directory.CreateDirectory(Path.Combine(directory, "elsewhere")).FullName;
            var refused = false;
            try { await remote.StartAsync(new StartRunRequest("job-c", workspace.Id, "claude", "hello", [], Provider: "codex") { WorkingFolder = elsewhere }); } catch (ArgumentException) { refused = true; }
            Check(refused && !runner.IsRunning("job-c") && !events.Any(e => e.SessionId == "job-c"), "a remote run with a working folder of its own is refused before it starts");
            // A grant Validate accepts on a Claude run (ToolSearch), so only the remote guard can refuse it; the same request without it starts.
            var granted = new StartRunRequest("job-d", workspace.Id, "claude", "hello", []) { StyleAutoAllow = ["ToolSearch"] };
            Check(granted.Validate().StyleAutoAllow is { Count: 1 }, "the grant itself is one a local style may give");
            refused = false;
            try { await remote.StartAsync(granted); } catch (ArgumentException) { refused = true; }
            Check(refused && !runner.IsRunning("job-d") && !events.Any(e => e.SessionId == "job-d"), "a remote run never brings a local style's tool permissions");
            await remote.StartAsync(granted with { SessionId = "job-f", StyleAutoAllow = null });
            await Verification.Until(() => events.Any(e => e.SessionId == "job-f"), 20000);
            await remote.StopAsync("job-f");

            // A host callback that throws loses its node, never the run's last status.
            var thrown = 0;
            await using var throwing = new RunManager(_ => Task.FromResult(workspace), catalog, plugin, events.Enqueue, _ => { }) { GraphNodes = (_, _) => { Interlocked.Increment(ref thrown); throw new InvalidOperationException("host failed"); } };
            await ((IRemoteRunner)throwing).StartAsync(new StartRunRequest("job-e", workspace.Id, "claude", "hello", [], Provider: "codex"));
            await Verification.Until(() => events.Any(e => e.SessionId == "job-e" && e.Status is "completed" or "error"), 20000);
            Check(thrown > 0 && events.Any(e => e.SessionId == "job-e" && e.Status == events.Where(x => x.SessionId == "job-a" && x.Status is "completed" or "error").Last().Status), "the run ends as it would have: " + string.Join(",", events.Where(e => e.SessionId == "job-e" && e.Status is not null).Select(e => e.Status)));
        }
        finally { Directory.Delete(directory, true); }
    }

    internal static string Key => RemoteClientVerification.Key;

    /// <summary>A scripted runner for the host checks: "hold" waits for a stop, "ask" asks one tool request, "graph" sends a node and a graph run,
    /// "flood" and "bulk" send more than the host keeps (in events and in bytes).</summary>
    internal sealed class FakeRemoteRunner(RemoteRunnerSinks sinks) : IRemoteRunner
    {
        internal readonly ConcurrentQueue<StartRunRequest> Started = new();
        internal readonly ConcurrentQueue<string> Stopped = new();
        internal readonly ConcurrentQueue<(string Session, string Request, bool Allow)> Answers = new();
        private readonly ConcurrentDictionary<string, bool> waiting = new();
        public Task StartAsync(StartRunRequest request)
        {
            Started.Enqueue(request); var id = request.SessionId;
            sinks.Event(RunEvent.State(id, "running"));
            sinks.Event(RunEvent.Log(id, "output", "echo:" + request.Input));
            switch (request.Input)
            {
                case "hold": waiting[id] = true; break;
                case "ask": waiting[id] = true; sinks.Permission(new ToolPermissionRequest("perm-1", id, "toolu_1", "Bash", "{\"command\":\"ls\"}", "ls", ReceivedAt: "2026-10-17T01:02:05.000Z")); break;
                case "graph":
                    sinks.GraphNode(id, new ExecutionGraphNode("node-1", "run-1", null, "main", "completed", "Request"));
                    var run = RemoteWireFixtureVerification.Node("graph-run-event.json")["event"]!["graphRun"].Deserialize<MightyGraphRun>(Wire.Json)!;
                    sinks.Event(new RunEvent(id, "graph_run", GraphRun: run));
                    sinks.Event(RunEvent.State(id, "completed")); break;
                case "flood":
                    for (var i = 0; i < 5000; i++) sinks.Event(RunEvent.Log(id, "output", "line " + i));
                    sinks.Event(RunEvent.State(id, "completed")); break;
                case "bulk":
                    for (var i = 0; i < 200; i++) sinks.Event(RunEvent.Log(id, "output", new string((char)('a' + i % 26), 30_000)));
                    sinks.Event(RunEvent.State(id, "completed")); break;
                default: sinks.Event(RunEvent.State(id, "completed")); break;
            }
            return Task.CompletedTask;
        }
        public Task StopAsync(string id) { Stopped.Enqueue(id); if (waiting.TryRemove(id, out _)) sinks.Event(RunEvent.State(id, "stopped")); return Task.CompletedTask; }
        public void RespondToToolPermission(string id, string requestId, bool allow)
        {
            Answers.Enqueue((id, requestId, allow));
            sinks.Permission(new ToolPermissionRequest(requestId, id, "toolu_1", "Bash", "{}", "ls", State: allow ? "allowed" : "denied"));
            if (waiting.TryRemove(id, out _)) sinks.Event(RunEvent.State(id, "completed"));
        }
        public void AnswerQuestionnaire(string id, string requestId, IReadOnlyDictionary<string, UserQuestionAnswer> answers) => Answers.Enqueue((id, requestId, true));
        public PlanRecord AnswerPlan(string id, string requestId, PlanDecision decision) { Answers.Enqueue((id, requestId, decision.Kind != "cancel")); return new(requestId, id, "plan", Wire.Now(), Wire.Now(), decision.Outcome); }
        internal volatile bool Disposed;
        public ValueTask DisposeAsync() { Disposed = true; foreach (var id in waiting.Keys) _ = StopAsync(id); return ValueTask.CompletedTask; }
    }

    internal sealed class HostFixture : IAsyncDisposable
    {
        internal required string Directory; internal required StateStore Store; internal required Workspace Shared, Private, Remote;
        internal required RemoteHostService Host; internal FakeRemoteRunner? Runner;
        public async ValueTask DisposeAsync() { await Host.DisposeAsync(); System.IO.Directory.Delete(Directory, true); }
    }

    internal static async Task<HostFixture> MakeHost(TimeSpan? heartbeat = null, Func<StartRunRequest, string?>? admission = null, Func<HostFixture, RemoteRunnerSinks, IRemoteRunner>? runner = null,
        Func<IReadOnlyList<string>>? addresses = null, Func<DateTimeOffset>? clock = null, Func<string, Task>? beforeBind = null, Action<int>? runsChanged = null,
        Func<CancellationToken, Task>? beforeLaunch = null, bool? windowsHost = null, string runtime = "info.json")
    {
        var directory = Verification.Temp(); var store = new StateStore(Path.Combine(directory, "host")); await store.LoadAsync();
        System.IO.Directory.CreateDirectory(Path.Combine(directory, "project")); System.IO.Directory.CreateDirectory(Path.Combine(directory, "private"));
        var shared = store.ApproveLocal(Path.Combine(directory, "project")); var hidden = store.ApproveLocal(Path.Combine(directory, "private"));
        var remote = store.ApproveRemote("conn-x", "ws-x", "far", "C:\\far", "other");
        await store.SaveAsync(new AppSnapshot { Workspaces = [shared, hidden, remote] });
        HostFixture fixture = null!;
        var host = new RemoteHostService(new RemoteHostOptions
        {
            Workspaces = () => store.Snapshot.Workspaces, ResolveLocal = store.ResolveLocalAsync,
            Runtime = () => Task.FromResult(RemoteWireFixtureVerification.Node(runtime)["runtime"].Deserialize<RuntimeInfo>(Wire.Json)!),
            CreateRunner = sinks => runner?.Invoke(fixture, sinks) ?? (fixture.Runner = new FakeRemoteRunner(sinks)),
            HostName = "studio-pc", AllowLoopbackForTests = true, Heartbeat = heartbeat ?? RemoteProtocol.HeartbeatTimeout, Admission = admission ?? (_ => null),
            Addresses = addresses, Clock = clock ?? (() => DateTimeOffset.UtcNow), RunsChanged = runsChanged ?? (_ => { }), WindowsHost = windowsHost ?? OperatingSystem.IsWindows(),
        }) { BeforeBind = beforeBind, BeforeLaunch = beforeLaunch };
        return fixture = new HostFixture { Directory = directory, Store = store, Shared = shared, Private = hidden, Remote = remote, Host = host };
    }

    internal static Task OnlyAKeyedRunRequestGetsTheAttachmentBody()
    {
        var key = new string('k', 43);
        RemoteHttpRequest Head(string target = "/v2/runs", Dictionary<string, string>? extra = null, string peer = "100.90.1.1")
        {
            var headers = new Dictionary<string, string> { ["authorization"] = "Bearer " + key, ["x-mighty-remote-version"] = "2" };
            foreach (var (name, value) in extra ?? []) headers[name] = value;
            return new("POST", target, headers, [], peer);
        }
        Check(RemoteHostService.BodyLimit(Head(), key, false) == AttachmentSupport.MaximumRequestBytes, "a keyed run request may carry attachments");
        Check(RemoteHostService.BodyLimit(Head(extra: new() { ["authorization"] = "Bearer " + new string('x', 43) }), key, false) == 0, "a wrong key reads nothing");
        Check(RemoteHostService.BodyLimit(new("POST", "/v2/runs", new Dictionary<string, string> { ["x-mighty-remote-version"] = "2" }, [], "100.90.1.1"), key, false) == 0, "no key reads nothing");
        Check(RemoteHostService.BodyLimit(Head(extra: new() { ["authorization"] = "Basic " + key }), key, false) == 0, "only a bearer key counts");
        Check(RemoteHostService.BodyLimit(Head(extra: new() { ["x-mighty-remote-version"] = "1" }), key, false) == 0, "an old protocol reads nothing");
        Check(RemoteHostService.BodyLimit(Head(extra: new() { ["origin"] = "https://example.com" }), key, false) == 0, "a browser page reads nothing");
        Check(RemoteHostService.BodyLimit(Head("/v2/runs/x/steer"), key, false) == RemoteProtocol.MaximumRequestBytes, "other routes get 512 KiB");
        Check(RemoteHostService.BodyLimit(Head("/v2/runs?x=1"), key, false) == RemoteProtocol.MaximumRequestBytes, "only the bare run route gets the attachment body");
        Check(RemoteHostService.BodyLimit(Head(peer: "192.168.1.5"), key, false) == 0 && RemoteHostService.BodyLimit(Head(peer: "127.0.0.1"), key, false) == 0, "non-Tailscale peers read nothing");
        Check(RemoteHostService.BodyLimit(Head(peer: "127.0.0.1"), key, true) == AttachmentSupport.MaximumRequestBytes, "loopback only when injected");
        var shell = new StartRunRequest("j", "w", "shell", "dir", []);
        var full = new StartRunRequest("j", "w", "claude", "hi", [], Settings: new(PermissionMode: "fullAccess"));
        var edits = new StartRunRequest("j", "w", "claude", "hi", [], Settings: new(PermissionMode: "acceptEdits"));
        Check(RemoteHostService.FullAccessRefusal(shell, false) == Locale.Get("remote.host.error.commandsOff") && RemoteHostService.FullAccessRefusal(full, false) == Locale.Get("remote.host.error.fullAccessOff"), "commands and full access wait for the switch");
        Check(RemoteHostService.FullAccessRefusal(full with { Provider = "codex" }, false, windowsHost: false) == Locale.Get("remote.host.error.fullAccessOff"), "full access waits for the switch whatever the agent");
        Check(RemoteHostService.FullAccessRefusal(edits, false) is null && RemoteHostService.FullAccessRefusal(shell, true) is null && RemoteHostService.FullAccessRefusal(full with { Provider = "codex" }, true) is null, "the switch allows them; other modes never needed it");
        // A Windows host cannot confirm it confines Codex or Antigravity as Seatbelt does on a Mac: without the switch they are refused in every mode.
        var fullAccessOff = Locale.Get("remote.host.error.fullAccessOff");
        foreach (var (provider, mode) in new[] { ("codex", "manual"), ("codex", "acceptEdits"), ("codex", "onRequest"), ("gemini", "manual"), ("gemini", "acceptEdits") })
        {
            var agent = edits with { Provider = provider, Settings = new(PermissionMode: mode) };
            Check(RemoteHostService.FullAccessRefusal(agent, false, windowsHost: true) == RemoteWindowsStrings.HostAgentNeedsFullAccess(provider), $"a Windows host refuses a {provider} {mode} run while the switch is off, saying only the switch helps");
            Check(RemoteHostService.FullAccessRefusal(agent, true, windowsHost: true) is null, $"the switch lets a {provider} {mode} run start on a Windows host");
            Check(RemoteHostService.FullAccessRefusal(agent, false, windowsHost: false) is null, $"a Mac host confines a {provider} {mode} run, so it needs no switch");
        }
        Check(RemoteHostService.FullAccessRefusal(edits, false, windowsHost: true) is null && RemoteHostService.FullAccessRefusal(edits with { Settings = new() }, false, windowsHost: true) is null, "Claude keeps its modes on a Windows host: it asks through permission requests");
        Check(RemoteHostService.FullAccessRefusal(full, false, windowsHost: true) == fullAccessOff && RemoteHostService.FullAccessRefusal(shell, false, windowsHost: true) == Locale.Get("remote.host.error.commandsOff")
            && RemoteHostService.FullAccessRefusal(full, true, windowsHost: true) is null && RemoteHostService.FullAccessRefusal(shell, true, windowsHost: true) is null, "full access and commands wait for the switch on a Windows host too");
        Check(RemoteHostService.FullAccessRefusal(full with { Provider = "codex" }, false, windowsHost: true) == RemoteWindowsStrings.HostAgentNeedsFullAccess("codex"), "a Codex full-access run on a Windows host gets the same words: no mode works without the switch");
        Check(RemoteHostService.FullAccessRefusal(edits with { Provider = "codex" }, false) == (OperatingSystem.IsWindows() ? RemoteWindowsStrings.HostAgentNeedsFullAccess("codex") : null), "without a setting the host's own OS decides");
        var now = DateTimeOffset.UnixEpoch; var limiter = new RemoteRateLimiter(() => now);
        Check(Enumerable.Range(0, 300).All(_ => limiter.AllowPeer("100.90.1.1")) && !limiter.AllowPeer("100.90.1.1") && limiter.AllowPeer("100.90.1.2"), "300 a second per peer, each peer its own budget");
        Check(Enumerable.Range(0, 600).All(_ => limiter.AllowKeyed()) && !limiter.AllowKeyed(), "600 keyed requests a second in all");
        now += TimeSpan.FromSeconds(1);
        Check(limiter.AllowPeer("100.90.1.1") && limiter.AllowKeyed(), "the next second starts afresh");
        return Task.CompletedTask;
    }

    internal static async Task SharingStartsWithANewKeyAndStopRevokesIt()
    {
        await using var fixture = await MakeHost();
        var host = fixture.Host;
        await RemoteClientVerification.RejectAsync(() => host.StartAsync([]), "nothing picked", ex => ex.Message == Locale.Get("remote.host.error.pickWorkspace"));
        await RemoteClientVerification.RejectAsync(() => host.StartAsync([fixture.Remote.Id]), "a remote workspace is never shared", ex => ex.Message == RemoteWindowsStrings.HostLocalOnly);
        await RemoteClientVerification.RejectAsync(() => host.StartAsync([fixture.Shared.Id], 80), "ports below 1024 are refused", ex => ex.Message == Locale.Get("remote.connect.error.badPort"));
        var state = await host.StartAsync([fixture.Shared.Id], 0);
        Check(state is { Enabled: true, AllowsFullAccess: false, Port: > 0 } && RemoteWorkspaceValidation.Key(state.Key) && state.Addresses.SequenceEqual(["127.0.0.1:" + state.Port]), "sharing on loopback (injected), a fresh key, full access off");
        await RemoteClientVerification.RejectAsync(() => host.StartAsync([fixture.Shared.Id], 0), "one sharing at a time", ex => ex.Message == Locale.Get("remote.host.error.busy"));
        var target = RemoteClientVerification.Loopback(state.Port!.Value);
        var info = JsonSerializer.Deserialize<RemoteWireInfo>(await RemoteTransport.RequestAsync(target, state.Key!, HttpMethod.Get, "/v2/info"), Wire.Json)!;
        RemoteWorkspaceValidation.Info(info);
        Check(info is { Protocol: 2, HostName: "studio-pc" } && info.Workspaces.Single().Id == fixture.Shared.Id && info.Runtime.Platform == "win32", "only the shared workspace, and the host's OS");
        var port = state.Port.Value; var key = state.Key!;
        string Get(string target, string? authorization = "Bearer ", string extra = "") =>
            $"GET {target} HTTP/1.1\r\n" + (authorization is null ? "" : "Authorization: " + authorization + (authorization == "Bearer " ? key : "") + "\r\n") + "x-mighty-remote-version: 2\r\n" + extra + "\r\n";
        var plain = await Raw(port, Get("/v2/info"));
        Check(plain.StartsWith("HTTP/1.1 200", StringComparison.Ordinal) && plain.Contains("x-mighty-remote-version: 2", StringComparison.Ordinal) && !plain.Contains(key, StringComparison.Ordinal), "/info never carries the key");
        await RemoteClientVerification.RejectAsync(() => RemoteTransport.RequestAsync(target, new string('x', 43), HttpMethod.Get, "/v2/info"), "a wrong key is 401", ex => ex is RemoteHttpFailure { Status: 401 });
        var keyless = await Raw(port, Get("/v2/info", authorization: null));
        Check(keyless.StartsWith("HTTP/1.1 401", StringComparison.Ordinal) && keyless.Contains("x-mighty-remote-version: 2", StringComparison.Ordinal), "no key is 401, with the version: " + keyless);
        Check((await Raw(port, Get("/v2/nothing", authorization: null))).StartsWith("HTTP/1.1 401", StringComparison.Ordinal), "no route is found without the key");
        // Were the body read, the answer would wait for its 400 000 bytes until the request deadline.
        var unread = await Raw(port, Get("/v2/runs", authorization: "Bearer " + new string('x', 43), extra: "Content-Type: application/json\r\nContent-Length: 400000\r\n").Replace("GET ", "POST ", StringComparison.Ordinal));
        Check(unread.StartsWith("HTTP/1.1 401", StringComparison.Ordinal), "a wrong key is answered from the head alone: " + unread);
        Check((await Raw(port, $"GET /v2/info HTTP/1.1\r\nAuthorization: Bearer {key}\r\nx-mighty-remote-version: 1\r\n\r\n")).StartsWith("HTTP/1.1 426", StringComparison.Ordinal), "an old protocol is 426");
        var browser = await Raw(port, Get("/v2/info", extra: "Origin: https://example.com\r\n"));
        Check(browser.StartsWith("HTTP/1.1 403", StringComparison.Ordinal) && browser.Contains("x-mighty-remote-version: 2", StringComparison.Ordinal), "a browser page is 403, even with the key");
        var answer = await Raw(port, Get("/v2/nothing"));
        Check(answer.StartsWith("HTTP/1.1 404", StringComparison.Ordinal) && answer.Contains("x-mighty-remote-version: 2", StringComparison.Ordinal), "every answer carries the version");
        Check((await Raw(port, Get("/v2/info?x=1"))).StartsWith("HTTP/1.1 404", StringComparison.Ordinal) && (await Raw(port, Get("/v2/runs/../info"))).StartsWith("HTTP/1.1 404", StringComparison.Ordinal), "/info takes no query, and a dot segment is never resolved");
        Check(host.SetAllowsFullAccess(true).AllowsFullAccess, "the switch turns on while sharing");
        // A request still arriving when sharing stops is cut off with it.
        using var pending = await Open(port, "GET /v2/info HTTP/1.1\r\n");
        var stopped = await host.StopAsync();
        Check(!stopped.Enabled && stopped.Key is null && !stopped.AllowsFullAccess, "stopping forgets the key and the switch");
        Check(await ClosedWithin(pending, TimeSpan.FromSeconds(5)), "a connection open when sharing stops is closed");
        await RemoteClientVerification.RejectAsync(() => RemoteTransport.RequestAsync(target, state.Key!, HttpMethod.Get, "/v2/info"), "nothing answers after a stop");
        var again = await host.StartAsync([fixture.Shared.Id], 0);
        Check(again.Key != state.Key && !again.AllowsFullAccess, "a new sharing has a new key and the switch off");
        var old = await Raw(again.Port!.Value, $"GET /v2/info HTTP/1.1\r\nAuthorization: Bearer {key}\r\nx-mighty-remote-version: 2\r\n\r\n");
        Check(old.StartsWith("HTTP/1.1 401", StringComparison.Ordinal), "the old key is refused by the new sharing");
        await host.DisposeAsync();
        await RemoteClientVerification.RejectAsync(() => host.StartAsync([fixture.Shared.Id], 0), "no sharing after shutdown", ex => ex.Message == Locale.Get("common.appClosing"));
    }

    internal static async Task AFloodIsRefusedAndTheRefusalCarriesTheVersion()
    {
        var now = DateTimeOffset.UnixEpoch;
        await using var fixture = await MakeHost(clock: () => now);
        var state = await fixture.Host.StartAsync([fixture.Shared.Id], 0);
        var port = state.Port!.Value;
        var keyed = $"GET /v2/info HTTP/1.1\r\nAuthorization: Bearer {state.Key}\r\nx-mighty-remote-version: 2\r\n\r\n";
        // Requests without the key count toward the peer's own budget too.
        for (var i = 0; i < 300; i++) Check((await Raw(port, "GET /v2/info HTTP/1.1\r\nx-mighty-remote-version: 2\r\n\r\n")).StartsWith("HTTP/1.1 401", StringComparison.Ordinal), "request " + i + " is answered");
        var refused = await Raw(port, keyed);
        Check(refused.StartsWith("HTTP/1.1 429", StringComparison.Ordinal) && refused.Contains("x-mighty-remote-version: 2", StringComparison.Ordinal), "the 301st request in a second is 429, with the version: " + refused);
        now += TimeSpan.FromSeconds(1);
        Check((await Raw(port, keyed)).StartsWith("HTTP/1.1 200", StringComparison.Ordinal), "the next second is answered");
    }

    internal static async Task AListenerThatStopsAcceptingTurnsSharingOff()
    {
        await using var fixture = await MakeHost(addresses: () => ["127.0.0.1", "::1"]);
        var state = await fixture.Host.StartAsync([fixture.Shared.Id], 0);
        var port = state.Port!.Value;
        var keyed = $"GET /v2/info HTTP/1.1\r\nAuthorization: Bearer {state.Key}\r\nx-mighty-remote-version: 2\r\n\r\n";
        Check(state.Addresses.SequenceEqual(["127.0.0.1:" + port, "[::1]:" + port]) && fixture.Host.Listeners.Count == 2 && fixture.Host.Listeners.All(l => l.IsListening), "one listener per address, on one port");
        Check((await Raw(port, keyed, address: IPAddress.IPv6Loopback)).StartsWith("HTTP/1.1 200", StringComparison.Ordinal), "both addresses answer");
        // Stands in for accepting that failed for good on one address.
        await fixture.Host.Listeners[0].DisposeAsync();
        var other = await Raw(port, keyed, address: IPAddress.IPv6Loopback);
        Check(other.StartsWith("HTTP/1.1 503", StringComparison.Ordinal) && other.Contains("x-mighty-remote-version: 2", StringComparison.Ordinal), "the other address stops answering too: " + other);
        var now = fixture.Host.Current();
        Check(now is { Enabled: false, Key: null } && fixture.Host.Listeners.Count == 0, "sharing shows as off, its key gone and every listener closed");
        Check((await fixture.Host.StartAsync([fixture.Shared.Id], 0)).Key != state.Key, "sharing starts again with a new key");
        await fixture.Host.Listeners[1].DisposeAsync();
        Check(fixture.Host.Current() is { Enabled: false, Key: null }, "Current alone notices a listener that stopped");
    }

    internal static async Task AStopWhileAStartBindsClosesWhatItOpened()
    {
        var cancelled = Locale.Get("remote.host.error.startCancelled");
        // A stop while the start is about to bind an address: the listener it then makes is never left open.
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (var fixture = await MakeHost(beforeBind: async _ => { entered.TrySetResult(); await release.Task; }))
        {
            var starting = fixture.Host.StartAsync([fixture.Shared.Id], 0);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await fixture.Host.StopAsync();
            release.TrySetResult();
            await RemoteClientVerification.RejectAsync(() => starting, "a start a stop overtook is cancelled", ex => ex.Message == cancelled);
            Check(fixture.Host.Listeners.Count == 0 && fixture.Host.Current() is { Enabled: false, Key: null } && fixture.Runner is { Disposed: true }, "nothing it opened stays open: " + fixture.Host.Listeners.Count);
        }
    }

    internal static async Task AStopWhileAStartMakesItsRunnerDisposesThatRunner()
    {
        var cancelled = Locale.Get("remote.host.error.startCancelled");
        // A stop while the start makes its runner: that runner is put away, never kept.
        FakeRemoteRunner? made = null;
        await using (var fixture = await MakeHost(runner: (f, sinks) => { _ = f.Host.StopAsync(); return made = new FakeRemoteRunner(sinks); }))
        {
            await RemoteClientVerification.RejectAsync(() => fixture.Host.StartAsync([fixture.Shared.Id], 0), "a start a stop overtook is cancelled", ex => ex.Message == cancelled);
            Check(made is { Disposed: true } && fixture.Host.Listeners.Count == 0 && fixture.Host.Current() is { Enabled: false, Key: null }, "the runner made after the stop is disposed");
        }
    }

    internal static async Task TheRunCountIsToldOutsideTheHostsLockAndAFailingListenerOfItChangesNothing()
    {
        var told = new ConcurrentQueue<int>(); var gateHeld = false; HostFixture? holder = null;
        await using var fixture = await MakeHost(runsChanged: count =>
        {
            told.Enqueue(count);
            // Another thread asks the host: it would wait for ever if this were told under the host's lock.
            if (holder is { } f && !Task.Run(() => f.Host.Current()).Wait(TimeSpan.FromSeconds(5))) gateHeld = true;
            throw new InvalidOperationException("the app failed to take the count");
        });
        holder = fixture;
        var state = await fixture.Host.StartAsync([fixture.Shared.Id], 0);
        var stopped = await fixture.Host.StopAsync();
        Check(!stopped.Enabled && fixture.Host.Listeners.Count == 0 && fixture.Runner is { Disposed: true }, "a failing callback never keeps sharing on");
        Check(!told.IsEmpty && told.All(c => c == 0) && !gateHeld, "the count is told, outside the lock: " + string.Join(",", told));
        await RemoteClientVerification.RejectAsync(() => RemoteTransport.RequestAsync(RemoteClientVerification.Loopback(state.Port!.Value), state.Key!, HttpMethod.Get, "/v2/info"), "nothing answers after the stop");
        Check((await fixture.Host.StartAsync([fixture.Shared.Id], 0)).Enabled, "and sharing starts again");
    }

    internal static async Task SharingRefusesWhatItCannotServe()
    {
        var noTailscale = RemoteWindowsStrings.HostNoTailscale;
        await using (var lan = await MakeHost(addresses: () => ["192.168.1.5", "0.0.0.0"]))
            await RemoteClientVerification.RejectAsync(() => lan.Host.StartAsync([lan.Shared.Id], 0), "no address but Tailscale's is ever bound", ex => ex.Message == noTailscale);
        await using (var unreadable = await MakeHost(addresses: () => throw new AdaptersUnreadableException(new InvalidOperationException("adapters"))))
            await RemoteClientVerification.RejectAsync(() => unreadable.Host.StartAsync([unreadable.Shared.Id], 0), "adapters that cannot be read are no Tailscale", ex => ex.Message == noTailscale);
        await using var fixture = await MakeHost();
        var taken = new TcpListener(IPAddress.Loopback, 0); taken.Start();
        try
        {
            var busyPort = ((IPEndPoint)taken.LocalEndpoint).Port;
            var prefix = Locale.Get("remote.host.error.listenFailed", new Dictionary<string, string> { ["error"] = "\u0001" }).Split('\u0001')[0];
            await RemoteClientVerification.RejectAsync(() => fixture.Host.StartAsync([fixture.Shared.Id], busyPort), "a port in use is a listen failure", ex => ex.Message.StartsWith(prefix, StringComparison.Ordinal) && ex.Message.Length > prefix.Length);
            Check(fixture.Host.Listeners.Count == 0 && fixture.Host.Current() is { Enabled: false, Key: null } && fixture.Runner is { Disposed: true }, "a failed start leaves nothing open");
        }
        finally { taken.Stop(); }
        Check(fixture.Host.SetAllowsFullAccess(true).AllowsFullAccess, "the switch can be set while sharing is off");
        var state = await fixture.Host.StartAsync([fixture.Shared.Id], 0);
        Check(state is { Enabled: true, AllowsFullAccess: false }, "sharing always starts with the switch off");
        var port = state.Port!.Value;
        string Keyed(string target) => $"GET {target} HTTP/1.1\r\nAuthorization: Bearer {state.Key}\r\nx-mighty-remote-version: 2\r\n\r\n";
        foreach (var target in new[] { "/v2\\info", "/v2/info#x" })
        {
            var answer = await Raw(port, Keyed(target));
            Check(answer.StartsWith("HTTP/1.1 400", StringComparison.Ordinal) && answer.Contains("x-mighty-remote-version: 2", StringComparison.Ordinal), "a target the Mac cannot read is 400: " + target);
        }
        var accented = await Raw(port, Encoding.UTF8.GetBytes(Keyed("/v2/infö")));
        Check(accented.StartsWith("HTTP/1.1 400", StringComparison.Ordinal) && accented.Contains("x-mighty-remote-version: 2", StringComparison.Ordinal), "a non-ASCII target is 400");
        Check((await Raw(port, Keyed("http://x/v2/info"))).StartsWith("HTTP/1.1 400", StringComparison.Ordinal), "an absolute-form target is refused (by the listener, before the protocol)");
        Check((await Raw(port, Keyed("/v2/info"))).StartsWith("HTTP/1.1 200", StringComparison.Ordinal), "and the plain target is answered");
    }
}
