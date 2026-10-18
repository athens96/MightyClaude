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
    public const int HeaderLimit = 8_192, DefaultBodyLimit = 512 * 1024, MaximumConnections = 64;
    /// <summary>The request line and at most 33 header lines, as on the Mac.</summary>
    private const int MaximumHeadLines = 34, DrainBytes = 64 * 1024;
    private static readonly TimeSpan DrainTime = TimeSpan.FromSeconds(1);
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly Regex HeaderName = new(@"^[A-Za-z0-9-]+\z", RegexOptions.CultureInvariant);
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
    private int boundPort;

    /// <summary>How long the head and body may take to arrive (the Mac's 15 s accept timer); a body over 512 KiB gets the upload timeout.</summary>
    internal TimeSpan RequestDeadline { get; init; } = RemoteProtocol.RequestTimeout;
    /// <summary>How long the handler and the answer may take once the request is in (the Mac's 60 s processing timer).</summary>
    internal TimeSpan HandlerDeadline { get; init; } = TimeSpan.FromSeconds(60);

    public int Port => Volatile.Read(ref boundPort);

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
                created.Start(MaximumConnections);
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
        while (!stop.IsCancellationRequested)
        {
            Socket socket;
            try { socket = await accepting.AcceptSocketAsync(stop.Token); }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or InvalidOperationException) { return; }
            catch (SocketException) when (!stop.IsCancellationRequested)
            {
                // A connection reset before it was accepted, or a passing shortage: the listener goes on.
                try { await Task.Delay(100, stop.Token); } catch (OperationCanceledException) { return; }
                continue;
            }
            catch (SocketException) { return; }
            try { Admit(socket); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { Abort(socket); }
        }
    }

    /// <summary>Takes the connection or drops it unread: the filter first, then the overall and per-peer caps.</summary>
    private void Admit(Socket socket)
    {
        var endpoint = socket.RemoteEndPoint as IPEndPoint;
        var remote = endpoint is null ? null : endpoint.Address.IsIPv4MappedToIPv6 ? endpoint.Address.MapToIPv4() : endpoint.Address;
        var peer = DirectIncoming.PeerOf(endpoint);
        bool allowed;
        try { allowed = remote is not null && peer is not null && acceptPeer?.Invoke(remote) != false; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { allowed = false; }
        lock (sync)
        {
            allowed = allowed && !closing && open.Count < MaximumConnections && (maximumConnectionsPerPeer is not { } cap || peerConnections.GetValueOrDefault(peer!) < cap);
            if (allowed)
            {
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
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        var stream = new NetworkStream(socket, ownsSocket: false);
        try
        {
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
            // The peer's slot is given back before the peer can see the end of the answer, so its next connection is never refused for this one (as on the Mac).
            lock (sync) { var left = peerConnections.GetValueOrDefault(peer) - 1; if (left > 0) peerConnections[peer] = left; else peerConnections.Remove(peer); }
            try
            {
                socket.Shutdown(SocketShutdown.Send);
                // An answer given without reading the whole request (a refusal, or a head answered alone) is followed by briefly
                // discarding what is still arriving, so the peer reads the answer instead of a reset. Nothing read here is parsed.
                if (drain) await DrainAsync(socket);
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { }
            await stream.DisposeAsync();
            socket.Close();
            lock (sync) open.Remove(socket);
        }
    }

    private async Task DrainAsync(Socket socket)
    {
        using var time = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        time.CancelAfter(DrainTime);
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
        var body = new byte[length];
        Array.Copy(buffer, end + 4, body, 0, already);
        for (var got = already; got < length;)
        {
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
            if (HeaderName.IsMatch(name) && !HasControl(value)) head.Append(name).Append(": ").Append(value).Append("\r\n");
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
