using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MightyClaude.Core;

/// <summary>One request as the handler sees it. Header names are lower-case; <see cref="RemoteAddress"/> is the peer's canonical spelling.</summary>
public sealed record RemoteHttpRequest(string Method, string Target, IReadOnlyDictionary<string, string> Headers, byte[] Body, string RemoteAddress);

public sealed record RemoteHttpResponse(int Status, byte[] Body, IReadOnlyDictionary<string, string>? Headers = null)
{
    public static RemoteHttpResponse Json(int status, object value, IReadOnlyDictionary<string, string>? headers = null) => new(status, JsonSerializer.SerializeToUtf8Bytes(value, RemoteWire.Json), headers);
}

/// <summary>
/// A bounded HTTP/1.1 endpoint on one address (M/Remote/HTTPServer.swift). Each connection carries one request and closes. A peer is
/// refused at accept, before anything is read, when <c>acceptPeer</c> says no (or throws) or it already holds its share of connections.
/// The head decides how much body is read; 0 means none, and the handler answers the head alone. The listener's own refusals (400, 413,
/// 431) carry no protocol header: they come before the protocol is reached.
/// </summary>
public sealed class RemoteHttpListener(IPAddress address, int port, Func<RemoteHttpRequest, Task<RemoteHttpResponse>> handler,
    Func<RemoteHttpRequest, int>? bodyLimit = null, Func<IPAddress, bool>? acceptPeer = null, int? maximumConnectionsPerPeer = null) : IAsyncDisposable
{
    public const int HeaderLimit = 8_192, DefaultBodyLimit = 512 * 1024;
    /// <summary>Connections open at once across every peer, as on the Mac (not RemoteProtocol.MaximumConnections, the saved connections).</summary>
    public const int MaximumOpenConnections = 64;
    /// <summary>The request line and at most 33 header lines, as on the Mac.</summary>
    private const int MaximumHeadLines = 34, DrainBytes = 64 * 1024, FirstBodyBuffer = 64 * 1024, MaximumAcceptFailures = 50;
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly Regex HeaderName = new(@"^[A-Za-z0-9-]+\z", RegexOptions.CultureInvariant);
    /// <summary>Headers the listener writes itself; a handler's copy is dropped so it can never change how the answer is framed.</summary>
    private static readonly HashSet<string> FramingHeaders = new(["content-length", "connection", "transfer-encoding", "content-type"], StringComparer.OrdinalIgnoreCase);
    private static readonly Regex LengthText = new(@"^[0-9]{1,9}\z", RegexOptions.CultureInvariant);
    private static readonly Dictionary<int, string> Reasons = new()
    {
        [200] = "OK", [202] = "Accepted", [400] = "Bad Request", [401] = "Unauthorized", [403] = "Forbidden", [404] = "Not Found", [409] = "Conflict", [413] = "Payload Too Large",
        [415] = "Unsupported Media Type", [426] = "Upgrade Required", [429] = "Too Many Requests", [431] = "Request Header Fields Too Large", [500] = "Internal Server Error", [503] = "Service Unavailable",
    };
    private readonly object sync = new();
    private readonly CancellationTokenSource stop = new();
    private readonly Dictionary<string, int> peerConnections = new(StringComparer.Ordinal);
    /// <summary>Every admitted connection and the task serving it, until its socket is closed.</summary>
    private readonly Dictionary<Socket, Task> open = [];
    private TcpListener? listener;
    private Task loop = Task.CompletedTask;
    private bool closing;
    private volatile bool acceptEnded;
    private int boundPort;
    /// <summary>Connections holding a slot: admitted and not yet released (a draining one still holds its slot).</summary>
    private int active;

    /// <summary>How long the head and body may take to arrive (the Mac's 15 s accept timer); a body over 512 KiB gets the upload timeout.</summary>
    internal TimeSpan RequestDeadline { get; init; } = RemoteProtocol.RequestTimeout;
    /// <summary>How long the handler and the answer may take once the request is in (the Mac's 60 s processing timer).</summary>
    internal TimeSpan HandlerDeadline { get; init; } = TimeSpan.FromSeconds(60);
    /// <summary>How long what still arrives after an early answer is discarded before the connection is closed (at most 64 KiB of it).</summary>
    internal TimeSpan DrainDeadline { get; init; } = TimeSpan.FromSeconds(1);
    /// <summary>Called after a connection gives back its slot; tests wait on it instead of sleeping.</summary>
    internal Action? SlotReleased { get; init; }

    public int Port => Volatile.Read(ref boundPort);

    /// <summary>True while connections are being accepted: false before Start, after dispose, and once accepting has failed for good
    /// (the host shows sharing as stopped then).</summary>
    public bool IsListening { get { lock (sync) return listener is not null && !closing && !acceptEnded; } }

    /// <summary>Binds the one address (no dual-stack, no shared port) and starts accepting; the port bound. A listener starts once.</summary>
    public int Start()
    {
        lock (sync)
        {
            if (listener is not null || closing) throw new InvalidOperationException(Locale.Get("http.error.alreadyStarted"));
            var created = new TcpListener(address, port);
            try
            {
                if (address.AddressFamily == AddressFamily.InterNetworkV6) created.Server.DualMode = false;
                if (OperatingSystem.IsWindows()) created.ExclusiveAddressUse = true;
                created.Start(MaximumOpenConnections);
            }
            catch { created.Stop(); throw; }
            listener = created;
            boundPort = ((IPEndPoint)created.LocalEndpoint).Port;
            loop = Task.Run(() => AcceptLoopAsync(created));
            return boundPort;
        }
    }

    private async Task AcceptLoopAsync(TcpListener accepting)
    {
        var failures = 0;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                Socket socket;
                try { socket = await accepting.AcceptSocketAsync(stop.Token); }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or InvalidOperationException) { return; }
                catch (SocketException ex) when (!stop.IsCancellationRequested)
                {
                    // A connection reset or aborted while it waited to be accepted says nothing about the listener: the next one is
                    // accepted at once and nothing is counted, so a peer cannot end sharing by resetting connections. Any other error may
                    // be a passing shortage: the listener goes on after a pause, unless it keeps failing.
                    if (!CountsAsAcceptFailure(ex.SocketErrorCode)) continue;
                    if (++failures >= MaximumAcceptFailures) { Fail(accepting, ex); return; }
                    try { await Task.Delay(100, stop.Token); } catch (OperationCanceledException) { return; }
                    continue;
                }
                catch (SocketException) { return; }
                failures = 0;
                try { Admit(socket); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { Abort(socket); }
            }
        }
        catch (Exception ex) when (!stop.IsCancellationRequested && ex is not OutOfMemoryException) { Fail(accepting, ex); }
        finally { acceptEnded = true; }
    }

    /// <summary>Whether an accept error counts toward ending the listener: not when it is one waiting connection's own reset or abort.</summary>
    internal static bool CountsAsAcceptFailure(SocketError code) => code is not (SocketError.ConnectionReset or SocketError.ConnectionAborted);

    /// <summary>Accepting has failed for good: the port is closed instead of left taking connections nobody accepts.</summary>
    private static void Fail(TcpListener accepting, Exception ex)
    {
        System.Diagnostics.Trace.TraceError("remote host: accepting failed: " + ex.GetType().Name);
        try { accepting.Stop(); } catch (Exception stopping) when (stopping is SocketException or ObjectDisposedException) { }
    }

    /// <summary>The peer's address as the filter sees it: an IPv4-mapped address as its IPv4, and no IPv6 scope (the Mac strips it).</summary>
    internal static IPAddress? PeerAddress(IPEndPoint? endpoint)
    {
        if (endpoint is null) return null;
        var address = endpoint.Address.IsIPv4MappedToIPv6 ? endpoint.Address.MapToIPv4() : endpoint.Address;
        return new IPAddress(address.GetAddressBytes());
    }

    /// <summary>Takes the connection or drops it unread: the filter first, then the overall and per-peer caps.</summary>
    private void Admit(Socket socket)
    {
        var endpoint = socket.RemoteEndPoint as IPEndPoint;
        var remote = PeerAddress(endpoint);
        var peer = DirectIncoming.PeerOf(endpoint);
        bool allowed;
        try { allowed = remote is not null && peer is not null && acceptPeer?.Invoke(remote) != false; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { allowed = false; }
        lock (sync)
        {
            allowed = allowed && !closing && active < MaximumOpenConnections && (maximumConnectionsPerPeer is not { } cap || peerConnections.GetValueOrDefault(peer!) < cap);
            if (allowed)
            {
                active++;
                peerConnections[peer!] = peerConnections.GetValueOrDefault(peer!) + 1;
                // The task cannot finish before it is recorded: its last step takes this lock.
                open[socket] = Task.Run(() => ServeAsync(socket, peer!));
            }
        }
        if (!allowed) Abort(socket);
    }

    /// <summary>Drops a connection with a reset, nothing said.</summary>
    private static void Abort(Socket socket)
    {
        try { socket.LingerState = new LingerOption(true, 0); } catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { }
        socket.Close();
    }

    private async Task ServeAsync(Socket socket, string peer)
    {
        var drain = false;
        var released = false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        NetworkStream? stream = null;
        try
        {
            stream = new NetworkStream(socket, ownsSocket: false);
            deadline.CancelAfter(RequestDeadline);
            var (response, unread) = await ReadAndAnswerAsync(stream, peer, deadline);
            if (response is not null) { await WriteAsync(stream, response, deadline.Token); drain = unread; }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A filter, limit or handler that fails ends its own connection only. The type alone is traced: a message could carry request text.
            System.Diagnostics.Trace.TraceError("remote host: a request failed: " + ex.GetType().Name);
        }
        finally
        {
            // A connection that is done gives its slot back before the peer can see the end of the answer, so the peer's next
            // connection is never refused for this one (as on the Mac). A draining one holds its slot until the drain ends: otherwise
            // one peer could keep any number of draining connections open.
            try
            {
                if (!drain) Release();
                try
                {
                    socket.Shutdown(SocketShutdown.Send);
                    // An answer given without reading the whole request (a refusal, or a head answered alone) is followed by briefly
                    // discarding what is still arriving, so the peer reads the answer instead of a reset. Nothing read here is parsed.
                    if (drain) await DrainAsync(socket);
                }
                catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { }
            }
            finally
            {
                // Whatever went wrong above, the slot, the socket and the open entry are always given back.
                try { Release(); }
                finally
                {
                    try { if (stream is not null) await stream.DisposeAsync(); }
                    finally { socket.Close(); lock (sync) open.Remove(socket); }
                }
            }
        }

        void Release()
        {
            if (released) return;
            released = true;
            lock (sync) { active--; var left = peerConnections.GetValueOrDefault(peer) - 1; if (left > 0) peerConnections[peer] = left; else peerConnections.Remove(peer); }
            SlotReleased?.Invoke();
        }
    }

    private async Task DrainAsync(Socket socket)
    {
        using var time = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        time.CancelAfter(DrainDeadline);
        var sink = new byte[8192];
        try { for (var total = 0; total < DrainBytes;) { var read = await socket.ReceiveAsync(sink, SocketFlags.None, time.Token); if (read == 0) break; total += read; } }
        catch (OperationCanceledException) { }
    }

    private static (RemoteHttpResponse, bool) Refuse(int status, string key) => (RemoteHttpResponse.Json(status, new Dictionary<string, string> { ["error"] = Locale.Get(key) }), true);

    private static bool HasControl(string text)
    {
        foreach (var c in text) if (c < ' ' && c != '\t' || c == '\x7f') return true;
        return false;
    }

    /// <summary>Reads the head, asks how much body it may bring, reads that much and no more, and asks the handler. Unread is true when
    /// the answer was given with request bytes possibly still arriving.</summary>
    private async Task<(RemoteHttpResponse? Response, bool Unread)> ReadAndAnswerAsync(NetworkStream stream, string peer, CancellationTokenSource deadline)
    {
        // The head may be HeaderLimit bytes before its blank line, as on the Mac; anything longer without one is 431.
        var buffer = new byte[HeaderLimit + 4]; var filled = 0; var scanned = 0; int end;
        while ((end = HeadEnd(buffer, filled, ref scanned)) < 0)
        {
            if (filled == buffer.Length) return Refuse(431, "http.error.headerTooLarge");
            var read = await stream.ReadAsync(buffer.AsMemory(filled), deadline.Token);
            if (read == 0) return (null, false);
            filled += read;
        }
        string text;
        try { text = StrictUtf8.GetString(buffer, 0, end); }
        catch (DecoderFallbackException) { return Refuse(400, "http.error.badHeaders"); }
        var lines = text.Split("\r\n");
        var start = lines[0].Split(' ');
        if (lines.Length > MaximumHeadLines || start.Length != 3 || start[0] is not ("GET" or "POST") || start[2] != "HTTP/1.1" || !start[1].StartsWith('/') || HasControl(lines[0]))
            return Refuse(400, "http.error.badRequest");
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in lines.Skip(1))
        {
            // No folded lines, and no bare CR, LF or other control character smuggled inside a line.
            var colon = line.IndexOf(':');
            if (colon < 0 || line.StartsWith(' ') || line.StartsWith('\t') || HasControl(line)) return Refuse(400, "http.error.badHeader");
            var raw = line[..colon];
            var name = raw.ToLowerInvariant();
            if (!HeaderName.IsMatch(raw) || headers.ContainsKey(name)) return Refuse(400, "http.error.duplicateHeader");
            headers[name] = line[(colon + 1)..].Trim(' ', '\t');
        }
        if (headers.ContainsKey("transfer-encoding")) return Refuse(400, "http.error.lengthRequired");
        var head = new RemoteHttpRequest(start[0], start[1], headers, [], peer);
        var limit = Math.Min(AttachmentSupport.MaximumRequestBytes, Math.Max(0, bodyLimit?.Invoke(head) ?? DefaultBodyLimit));
        var lengthValue = headers.GetValueOrDefault("content-length") ?? "0";
        if (!LengthText.IsMatch(lengthValue) || !int.TryParse(lengthValue, NumberStyles.None, CultureInfo.InvariantCulture, out var length) || length > limit && limit != 0)
            return Refuse(413, "http.error.badLength");
        // Nothing of the body is read for a head the server refuses it to: the handler answers the head alone (an unauthorized request's 401).
        if (limit == 0 && length > 0) return (await HandleAsync(head, deadline), true);
        if (length > DefaultBodyLimit) deadline.CancelAfter(RemoteProtocol.UploadTimeout);
        var already = filled - (end + 4);
        if (already > length) return Refuse(400, "http.error.oneRequest");
        // The body buffer grows as bytes arrive, up to the length: a head alone never makes the listener hold its whole length.
        var body = new byte[Math.Min(length, FirstBodyBuffer)];
        Array.Copy(buffer, end + 4, body, 0, already);
        for (var got = already; got < length;)
        {
            if (got == body.Length) Array.Resize(ref body, (int)Math.Min(length, body.Length * 2L));
            var read = await stream.ReadAsync(body.AsMemory(got), deadline.Token);
            if (read == 0) return (null, false);
            got += read;
        }
        if (stream.DataAvailable) return Refuse(400, "http.error.oneRequest");
        return (await HandleAsync(head with { Body = body }, deadline), false);
    }

    /// <summary>The arrival deadline covered slow delivery; the handler and the answer get their own, as on the Mac. A handler still
    /// running at it is left to finish on its own and its answer dropped.</summary>
    private async Task<RemoteHttpResponse> HandleAsync(RemoteHttpRequest request, CancellationTokenSource deadline)
    {
        deadline.CancelAfter(HandlerDeadline);
        var task = handler(request);
        try { return await task.WaitAsync(deadline.Token); }
        catch (OperationCanceledException) { _ = task.ContinueWith(static t => t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default); throw; }
    }

    private static int HeadEnd(byte[] buffer, int filled, ref int scanned)
    {
        for (var i = Math.Max(0, scanned - 3); i + 3 < filled; i++) if (buffer[i] == '\r' && buffer[i + 1] == '\n' && buffer[i + 2] == '\r' && buffer[i + 3] == '\n') return i;
        scanned = filled;
        return -1;
    }

    private static async Task WriteAsync(NetworkStream stream, RemoteHttpResponse response, CancellationToken token)
    {
        var head = new StringBuilder($"HTTP/1.1 {response.Status.ToString(CultureInfo.InvariantCulture)} {Reasons.GetValueOrDefault(response.Status, "Response")}\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {response.Body.Length.ToString(CultureInfo.InvariantCulture)}\r\nConnection: close\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\n");
        foreach (var (name, value) in response.Headers ?? new Dictionary<string, string>())
            if (HeaderName.IsMatch(name) && !HasControl(value) && !FramingHeaders.Contains(name)) head.Append(name).Append(": ").Append(value).Append("\r\n");
        head.Append("\r\n");
        await stream.WriteAsync(Encoding.UTF8.GetBytes(head.ToString()), token);
        await stream.WriteAsync(response.Body, token);
        await stream.FlushAsync(token);
    }

    /// <summary>Stops accepting, resets every live connection and waits for their tasks; a handler still running is not waited for.</summary>
    public async ValueTask DisposeAsync()
    {
        TcpListener? accepting;
        lock (sync) { closing = true; accepting = listener; }
        stop.Cancel();
        accepting?.Stop();
        KeyValuePair<Socket, Task>[] live; Task acceptLoop;
        lock (sync) { live = [.. open]; acceptLoop = loop; }
        foreach (var (socket, _) in live) Abort(socket);
        try { await acceptLoop; } catch (Exception ex) when (ex is not OutOfMemoryException) { }
        try { await Task.WhenAll(live.Select(entry => entry.Value)); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }
}
