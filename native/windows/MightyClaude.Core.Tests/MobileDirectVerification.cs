using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MightyClaude.Core;

// The phone's direct path to a Windows host (docs/relay.md, "Tailscale 직접 연결"; spec
// docs/superpowers/specs/2026-10-17-windows-catch-up-design.md section 2). Loopback stands in for Tailscale only through
// the host's internal SetDirectAddressSource seam; nothing here runs netsh or binds a real Tailscale address.
internal static class MobileDirectVerification
{
    private static void Check(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }

    internal static Task EndpointsUpgradeAdmissionAndStatus()
    {
        // Endpoints: host:port, IPv6 in brackets; only Tailscale addresses, each once, at most eight.
        Check(MobileDirect.Endpoint("100.101.102.103", 43139) == "100.101.102.103:43139" && MobileDirect.Endpoint("fd7a:115c:a1e0::1", 43139) == "[fd7a:115c:a1e0::1]:43139", "endpoints are host:port, IPv6 in brackets");
        Check(MobileDirect.Advertised([("100.101.102.103", 43139), ("fd7a:115c:a1e0::1", 43139), ("192.168.0.5", 43139), ("100.101.102.103", 43139)])
            .SequenceEqual(["100.101.102.103:43139", "[fd7a:115c:a1e0::1]:43139"]), "only Tailscale endpoints, each once");
        Check(MobileDirect.Advertised([("127.0.0.1", 50000)], allowAnyHost: true).SequenceEqual(["127.0.0.1:50000"]) && MobileDirect.Advertised([("127.0.0.1", 50000)]).Count == 0, "loopback is advertised only through the seam");
        Check(MobileDirect.Advertised(Enumerable.Range(1, 12).Select(i => ("100.101.0." + i.ToString(CultureInfo.InvariantCulture), 43139))).Count == MobileDirect.MaximumEndpoints, "at most eight endpoints");
        Check(MobileDirect.ValidPort(1024) && MobileDirect.ValidPort(65535) && !MobileDirect.ValidPort(1023) && !MobileDirect.ValidPort(65536) && !MobileDirect.ValidPort(0), "ports are 1024-65535");
        Check(MobileDirect.IsDirect("direct-0f8fad5b-d9cb-469f-a165-70867728950e") && !MobileDirect.IsDirect("0f8fad5b-d9cb-469f-a165-70867728950e"), "direct ids start with direct-");
        Check(MobileDirect.DefaultPort == 43139 && MobileDirect.ConnectionLimit == 36 && MobileDirect.MaximumFrameBytes == 1024 * 1024 && MobileDirect.HandshakeDeadline == TimeSpan.FromSeconds(10)
            && MobileDirect.AuthFailureWindow == TimeSpan.FromSeconds(60) && MobileDirect.RedetectInterval == TimeSpan.FromSeconds(15) && MobileDirect.GenericRefusal == "unauthorized", "the Mac's limits");

        // The phone's upgrade (RFC 6455 section 1.3's sample key): GET /ws, HTTP/1.1, websocket, version 13, a 16-byte key.
        Check(MobileDirect.AcceptKey("dGhlIHNhbXBsZSBub25jZQ==") == "s3pPLMBiTxaQ9kYGzzhZRbK+xOo=", "the accept key");
        const string request = "GET /ws?v=1 HTTP/1.1\r\nHost: 100.101.102.103:43139\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\n\r\n";
        Check(MobileDirect.UpgradeResponse(request) == "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: s3pPLMBiTxaQ9kYGzzhZRbK+xOo=\r\n\r\n", "a phone's upgrade is accepted");
        Check(MobileDirect.UpgradeResponse(request.Replace("Connection: Upgrade", "Connection: keep-alive, Upgrade").Replace("Upgrade: websocket", "upgrade: WebSocket")) is not null, "header names and tokens are case-insensitive");
        foreach (var bad in new[]
                 {
                     request.Replace("GET /ws?v=1", "GET /other"), request.Replace("GET", "POST"), request.Replace("HTTP/1.1", "HTTP/1.0"),
                     request.Replace("Sec-WebSocket-Version: 13", "Sec-WebSocket-Version: 8"), request.Replace("Upgrade: websocket\r\n", ""),
                     request.Replace("dGhlIHNhbXBsZSBub25jZQ==", "c2hvcnQ="), request.Replace("Connection: Upgrade", "Connection: close"),
                 })
            Check(MobileDirect.UpgradeResponse(bad) is null, "a refused upgrade: " + bad.Split("\r\n")[0]);

        // Admission at accept, before anything is read.
        const string tailnet = "100.101.102.103";
        Check(MobileDirect.Admit(tailnet, false, 0, [], 0), "a tailnet peer gets a slot");
        Check(!MobileDirect.Admit("192.168.0.5", false, 0, [], 0) && !MobileDirect.Admit("127.0.0.1", false, 0, [], 0) && !MobileDirect.Admit(null, false, 0, [], 0), "a peer outside the ranges is dropped");
        Check(MobileDirect.Admit("127.0.0.1", true, 0, [], 0), "loopback only through the seam");
        Check(!MobileDirect.Admit(tailnet, false, MobileDirect.MaximumClients, [], 0), "a full phone table admits none");
        Check(!MobileDirect.Admit(tailnet, false, 4, ["100.64.0.1", "100.64.0.2", "100.64.0.3", "100.64.0.4"], 0), "four handshakes at once");
        Check(!MobileDirect.Admit(tailnet, false, 2, [tailnet, tailnet], 0) && MobileDirect.Admit(tailnet, false, 2, [tailnet, "100.64.0.1"], 0), "two handshakes per address");
        Check(!MobileDirect.Admit(tailnet, false, 0, [], MobileDirect.MaximumAuthFailures) && MobileDirect.Admit(tailnet, false, 0, [], MobileDirect.MaximumAuthFailures - 1), "five failed auths shut the address out");

        // Failed auths per address inside 60 s.
        var failures = new DirectAuthFailures(); var start = DateTimeOffset.UnixEpoch;
        for (var i = 0; i < 5; i++) failures.Record(tailnet, start.AddSeconds(i));
        Check(failures.Recent(tailnet, start.AddSeconds(10)) == 5 && failures.Recent("100.64.0.1", start) == 0, "failures count per address");
        Check(failures.Recent(tailnet, start.AddSeconds(62)) == 2, "a failure 60 s old no longer counts");
        Check(failures.Recent(tailnet, start.AddSeconds(70)) == 0 && failures.Peers == 0, "an address with no recent failure is forgotten");
        for (var i = 0; i < 256; i++) failures.Record("100.65." + (i / 256).ToString(CultureInfo.InvariantCulture) + "." + (i % 256).ToString(CultureInfo.InvariantCulture), start);
        failures.Record("100.66.0.1", start.AddSeconds(61));
        Check(failures.Peers == 1, "a full table drops the addresses whose failures are all old");

        // The status line says what the Mac's says.
        Check(MobileDirectStatusText.Detail(MobileDirectStatus.Off) == "", "off says nothing");
        Check(MobileDirectStatusText.Detail(new("disabled", [])) == Locale.Get("mobileRemote.direct.status.off"), "the switch off");
        Check(MobileDirectStatusText.Detail(new("notFound", [])) == Locale.Get("mobileRemote.direct.status.notFound"), "no Tailscale");
        Check(MobileDirectStatusText.Detail(new("listening", ["100.101.102.103:43139", "[fd7a:115c:a1e0::1]:43139"]))
            == Locale.Get("mobileRemote.direct.status.listeningTemplate", new Dictionary<string, string> { ["addresses"] = "100.101.102.103:43139, [fd7a:115c:a1e0::1]:43139" }), "the endpoints listened on");
        Check(MobileDirectStatusText.Detail(new("failed", [], "in use")) == Locale.Get("mobileRemote.direct.status.failedTemplate", new Dictionary<string, string> { ["error"] = "in use" }), "why binding failed");
        Check(new MobileDirectStatus("listening", ["x"]).Listening && !MobileDirectStatus.Off.Listening, "Listening is computed");
        Check(JsonSerializer.Serialize(MobileDirectStatus.Off, Wire.Json) == "{\"state\":\"off\",\"endpoints\":[]}", "the computed property is never serialized");
        return Task.CompletedTask;
    }

    internal static async Task TheListenerHoldsAtMost36AndUpgradesAPhone()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var delivered = new ConcurrentQueue<DirectIncoming>();
        var listener = new MobileDirectListener(delivered.Enqueue);
        try
        {
            var (bound, error) = listener.Update(["127.0.0.1"], 0);
            Check(error is null && bound.Count == 1 && bound[0].Host == "127.0.0.1" && bound[0].Port > 0, "one listener on the address asked for");
            var port = bound[0].Port;
            Check(listener.Update(["127.0.0.1"], 0).Bound.Single().Port == port, "an address already listened on keeps its listener");

            // A phone's upgrade, then frames both ways.
            using (var phone = new ClientWebSocket())
            {
                var connecting = phone.ConnectAsync(new Uri("ws://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + "/ws?v=1"), timeout.Token);
                await Verification.Until(() => !delivered.IsEmpty);
                delivered.TryDequeue(out var incoming);
                Check(incoming!.Peer == "127.0.0.1", "the peer is read from the socket");
                using var server = await MobileDirect.AcceptWebSocketAsync(incoming.Stream, timeout.Token) ?? throw new InvalidOperationException("the phone's upgrade was refused");
                await connecting;
                await phone.SendAsync("hello"u8.ToArray(), WebSocketMessageType.Text, true, timeout.Token);
                var buffer = new byte[16]; var got = await server.ReceiveAsync(buffer, timeout.Token);
                Check(got.MessageType == WebSocketMessageType.Text && Encoding.UTF8.GetString(buffer, 0, got.Count) == "hello", "frames pass after the upgrade");
                await server.SendAsync("ready"u8.ToArray(), WebSocketMessageType.Text, true, timeout.Token);
                got = await phone.ReceiveAsync(buffer, timeout.Token);
                Check(got.MessageType == WebSocketMessageType.Text && Encoding.UTF8.GetString(buffer, 0, got.Count) == "ready", "frames pass back to the phone");
                incoming.Dispose();
                incoming.Dispose(); incoming.Refuse();
            }
            await Verification.Until(() => listener.LiveConnections == 0);
            Check(listener.LiveConnections == 0, "a connection disposed twice and then refused gives its slot back once");

            // A request that is no phone's upgrade gets no 101: the caller refuses it unanswered and its slot comes back.
            using (var stranger = new TcpClient())
            {
                await stranger.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
                await stranger.GetStream().WriteAsync("GET /other HTTP/1.1\r\nHost: x\r\n\r\n"u8.ToArray(), timeout.Token);
                await Verification.Until(() => !delivered.IsEmpty);
                delivered.TryDequeue(out var incoming);
                Check(await MobileDirect.AcceptWebSocketAsync(incoming!.Stream, timeout.Token) is null, "a refused upgrade gives no socket");
                incoming.Refuse(); incoming.Refuse(); incoming.Dispose();
                Check(await ReadsEnd(stranger, timeout.Token), "a refused upgrade is answered with nothing");
                await Verification.Until(() => listener.LiveConnections == 0);
            }

            // A phone that hangs up before anything starts on its connection still gives its slot back once.
            using (var early = new TcpClient())
            {
                await early.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
                await Verification.Until(() => !delivered.IsEmpty);
                early.Dispose();
                delivered.TryDequeue(out var incoming);
                WebSocket? none = null;
                try { none = await MobileDirect.AcceptWebSocketAsync(incoming!.Stream, timeout.Token); }
                catch (IOException) { }
                Check(none is null, "a closed connection gives no socket");
                incoming!.Dispose(); incoming.Dispose();
                await Verification.Until(() => listener.LiveConnections == 0);
            }

            // 36 held at once; the 37th is reset unread and gives its slot straight back.
            var held = new List<TcpClient>();
            try
            {
                for (var i = 0; i < MobileDirect.ConnectionLimit; i++) { var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token); held.Add(client); }
                await Verification.Until(() => listener.LiveConnections == MobileDirect.ConnectionLimit);
                using var extra = new TcpClient(); await extra.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
                Check(await ReadsEnd(extra, timeout.Token), "the 37th connection is dropped");
                Check(listener.LiveConnections == MobileDirect.ConnectionLimit, "a dropped connection gives its slot back");
                Check(delivered.Count == MobileDirect.ConnectionLimit, "the 37th connection is never delivered");
            }
            finally { foreach (var client in held) client.Dispose(); while (delivered.TryDequeue(out var item)) item.Dispose(); }
            await Verification.Until(() => listener.LiveConnections == 0);

            // A handler that throws has its connection refused, and its slot comes back.
            var throwing = new MobileDirectListener(_ => throw new InvalidOperationException("handler"));
            try
            {
                var throwingPort = throwing.Update(["127.0.0.1"], 0).Bound.Single().Port;
                using var dropped = new TcpClient(); await dropped.ConnectAsync(IPAddress.Loopback, throwingPort, timeout.Token);
                Check(await ReadsEnd(dropped, timeout.Token), "a handler's failure drops the connection");
                await Verification.Until(() => throwing.LiveConnections == 0);
            }
            finally { throwing.Stop(); }

            // A port already taken is reported, not thrown.
            using var taken = new TcpListener(IPAddress.Loopback, 0); taken.Start(); var busy = ((IPEndPoint)taken.LocalEndpoint).Port;
            var second = new MobileDirectListener(_ => { });
            var failed = second.Update(["127.0.0.1"], busy); second.Stop();
            Check(failed.Bound.Count == 0 && failed.Error is { Length: > 0 }, "a taken port is a bind error");

            // Stop closes every listener.
            listener.Stop();
            Check(listener.Update(["127.0.0.1"], 0).Bound.Count == 0, "a stopped listener binds nothing again");
            var refused = false;
            using var after = new TcpClient();
            try { await after.ConnectAsync(IPAddress.Loopback, port, timeout.Token); refused = await ReadsEnd(after, timeout.Token); }
            catch (SocketException) { refused = true; }
            Check(refused, "a stopped listener takes no phone");
        }
        finally { listener.Stop(); }
    }

    internal static async Task TheUpgradeAndThePeerAreReadStrictly()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        const string request = "GET /ws?v=1 HTTP/1.1\r\nHost: 100.101.102.103:43139\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\n\r\n";
        // Nothing is written back to a refused request.
        foreach (var (bytes, reason) in new[]
                 {
                     (Encoding.ASCII.GetBytes(request.Replace("GET /ws?v=1", "GET /other")), "another path"),
                     (Encoding.ASCII.GetBytes(request + "x"), "bytes after the blank line"),
                     (Encoding.ASCII.GetBytes(request[..^2]), "a head that never ends"),
                     (Encoding.ASCII.GetBytes("GET /ws?v=1 HTTP/1.1\r\nX: " + new string('a', MobileDirect.MaximumRequestHeadBytes) + "\r\n\r\n"), "a head over 8 KiB"),
                     (Array.Empty<byte>(), "nothing at all"),
                 })
        {
            using var stream = new ReadOnlyThenRecording(bytes);
            Check(await MobileDirect.AcceptWebSocketAsync(stream, timeout.Token) is null && stream.Written == 0, "a refused upgrade: " + reason);
        }
        using (var good = new ReadOnlyThenRecording(Encoding.ASCII.GetBytes(request)))
        {
            using var socket = await MobileDirect.AcceptWebSocketAsync(good, timeout.Token);
            Check(socket is { State: WebSocketState.Open } && Encoding.ASCII.GetString(good.WrittenBytes).Contains("Sec-WebSocket-Accept: s3pPLMBiTxaQ9kYGzzhZRbK+xOo=\r\n\r\n"), "a good upgrade is answered with a 101");
        }

        // One spelling per peer, so the per-address limits never split across forms of one address.
        Check(DirectIncoming.PeerOf(new IPEndPoint(IPAddress.Parse("::ffff:100.101.102.103"), 1)) == "100.101.102.103", "an IPv4-mapped peer is its IPv4");
        Check(DirectIncoming.PeerOf(new IPEndPoint(IPAddress.Parse("FD7A:115C:A1E0::1"), 1)) == "fd7a:115c:a1e0::1", "an IPv6 peer is lowercase");
        Check(DirectIncoming.PeerOf(new IPEndPoint(IPAddress.Parse("fd7a:115c:a1e0:0:0:0:0:1%7"), 1)) == "fd7a:115c:a1e0::1", "an IPv6 peer has no zone and is compressed");
        Check(DirectIncoming.PeerOf(new IPEndPoint(IPAddress.Parse("100.101.102.103"), 1)) == "100.101.102.103", "an IPv4 peer stays");
        Check(DirectIncoming.PeerOf(new DnsEndPoint("example.invalid", 1)) is null && DirectIncoming.PeerOf(null) is null, "no IP, no peer");
    }

    /// <summary>Reads the given bytes, then the end; records what is written.</summary>
    private sealed class ReadOnlyThenRecording(byte[] input) : Stream
    {
        private readonly MemoryStream reading = new(input), writing = new();
        public int Written => (int)writing.Length;
        public byte[] WrittenBytes => writing.ToArray();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => reading.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => writing.Write(buffer, offset, count);
        protected override void Dispose(bool disposing) { if (disposing) { reading.Dispose(); writing.Dispose(); } base.Dispose(disposing); }
    }

    /// <summary>Whether the server ended the connection (a reset or a close) within three seconds.</summary>
    private static async Task<bool> ReadsEnd(TcpClient client, CancellationToken token)
    {
        try
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(token); wait.CancelAfter(TimeSpan.FromSeconds(3));
            return await client.GetStream().ReadAsync(new byte[1], wait.Token) == 0;
        }
        catch (IOException) { return true; }
        catch (OperationCanceledException) { return false; }
    }
}
