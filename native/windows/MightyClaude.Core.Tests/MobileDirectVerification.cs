using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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

    private static string Temp() => Directory.CreateTempSubdirectory("mighty-direct-").FullName;
    private static string Query(string link, string name) => Uri.UnescapeDataString(new Uri(link).Query.TrimStart('?').Split('&').Single(part => part.StartsWith(name + "=", StringComparison.Ordinal))[(name.Length + 1)..]);
    private static async Task<byte[]> Receive(WebSocket socket, CancellationToken token)
    {
        using var result = new MemoryStream(); var buffer = new byte[8192]; WebSocketReceiveResult frame;
        do { frame = await socket.ReceiveAsync(buffer, token); if (frame.MessageType == WebSocketMessageType.Close) throw new IOException("closed"); result.Write(buffer, 0, frame.Count); } while (!frame.EndOfMessage);
        return result.ToArray();
    }

    /// <summary>A phone on the direct path: ws://endpoint/ws?v=1, hello, the paired key checked, then encrypted frames.</summary>
    private sealed class DirectPhone(ClientWebSocket socket, RelayCipher cipher) : IAsyncDisposable
    {
        internal static async Task<DirectPhone> ConnectAsync(string endpoint, string pairingUrl, CancellationToken token)
        {
            var socket = new ClientWebSocket();
            try
            {
                await socket.ConnectAsync(new Uri("ws://" + endpoint + "/ws?v=1"), token);
                var secret = RandomNumberGenerator.GetBytes(32); var nonce = RandomNumberGenerator.GetBytes(16);
                await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { type = "hello", v = 1, clientKey = Convert.ToBase64String(RelayCipher.PublicKey(secret)), nonce = Convert.ToBase64String(nonce) }, Wire.Json), WebSocketMessageType.Text, true, token);
                using var ready = JsonDocument.Parse(await Receive(socket, token));
                var server = Convert.FromBase64String(ready.RootElement.Text("serverKey")!);
                Check(Query(pairingUrl, "pk").Replace('-', '+').Replace('_', '/') + "=" == Convert.ToBase64String(server), "the direct path answers with the paired public key");
                return new(socket, new RelayCipher(secret, server, nonce, Convert.FromBase64String(ready.RootElement.Text("nonce")!), false));
            }
            catch { socket.Dispose(); throw; }
        }

        internal Task<JsonDocument> ExchangeAsync(object message, CancellationToken token) => ExchangeRawAsync(JsonSerializer.SerializeToUtf8Bytes(message, Wire.Json), token);

        /// <summary>Seals <paramref name="plaintext"/> as it is, JSON or not, and reads the answer.</summary>
        internal async Task<JsonDocument> ExchangeRawAsync(byte[] plaintext, CancellationToken token)
        {
            await socket.SendAsync(cipher.Seal(plaintext), WebSocketMessageType.Binary, true, token);
            return JsonDocument.Parse(cipher.Open(await Receive(socket, token)));
        }

        public ValueTask DisposeAsync() { socket.Abort(); socket.Dispose(); cipher.Dispose(); return ValueTask.CompletedTask; }
    }

    internal static async Task APhoneConnectsDirectlyOnTheRelaysHandshake()
    {
        var directory = Temp(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var host = new MobileRelayHost(Path.Combine(directory, "identity"), "Fixture", [], (_, path, _, _, _) => Task.FromResult(new MobileReply(200, new { protocol = 1, path }))) { WatchesNetwork = false };
        try
        {
            // The seam is set first, so this machine's own Tailscale address is never bound.
            host.SetDirectAddressSource(() => ["127.0.0.1"]);
            Check(host.DirectStatus.State == "off" && host.DirectEndpoints.Count == 0, "nothing listens while the remote is off");
            Check(JsonSerializer.Deserialize<MobileRemoteSettings>("{\"enabled\":true,\"relayURL\":\"\",\"allowLegacyPhones\":true}", Wire.Json) is { DirectEnabled: true, DirectPort: MobileDirect.DefaultPort }, "settings saved before the direct path read as on, port 43139");
            Check(new MobileRemoteSettings(DirectPort: 80).Normalized.DirectPort == MobileDirect.DefaultPort && new MobileRemoteSettings(true, "not a relay", DirectEnabled: false).Normalized is { Enabled: false, DirectEnabled: false }, "a bad port normalizes to 43139 and a bad relay keeps the direct switch");
            // The relay address is a closed local port: the relay fails and redials the whole time, and the direct phone never notices.
            await host.ApplyAsync(new MobileRemoteSettings(true, "ws://127.0.0.1:9"));
            Check(host.DirectStatus.Listening && host.DirectEndpoints.Count == 1 && host.DirectEndpoints[0].StartsWith("127.0.0.1:", StringComparison.Ordinal), "the seam's address is listened on and advertised: " + string.Join(",", host.DirectEndpoints));
            var endpoint = host.DirectEndpoints[0];
            await using (var phone = await DirectPhone.ConnectAsync(endpoint, host.PairingUrl, timeout.Token))
            {
                using var auth = await phone.ExchangeAsync(new { type = "auth", pairingKey = Query(host.PairingUrl, "key"), clientId = "abcdefghijklmnopqrstuv", clientName = "Fixture Phone" }, timeout.Token);
                Check(auth.RootElement.Text("type") == "auth_ok" && auth.RootElement.Text("deviceToken") is not null, "a phone pairs over the direct path");
                Check(auth.RootElement.GetProperty("direct").EnumerateArray().Select(value => value.GetString()).SequenceEqual(host.DirectEndpoints), "auth_ok carries the direct endpoints");
                using var info = await phone.ExchangeAsync(new { id = "request-1", method = "GET", path = "/m1/info" }, timeout.Token);
                Check(info.RootElement.Text("id") == "request-1" && info.RootElement.GetProperty("status").GetInt32() == 200, "requests are tunnelled as on the relay");
                Check(host.ConnectionIds.Single().StartsWith(MobileDirect.ConnectionPrefix, StringComparison.Ordinal) && host.Devices.Single().Connected && host.Status.Connections == 1, "the phone joins the same table with a direct- id");
            }
            await Verification.Until(() => host.ConnectionIds.Count == 0);
            // A wrong key hears one word, never why.
            await using (var stranger = await DirectPhone.ConnectAsync(endpoint, host.PairingUrl, timeout.Token))
            {
                using var refused = await stranger.ExchangeAsync(new { type = "auth", pairingKey = "wrong-key-0000000000", clientId = "zyxwvutsrqponmlkjihgfe", clientName = "Stranger" }, timeout.Token);
                Check(refused.RootElement.Text("type") == "auth_error" && refused.RootElement.Text("reason") == MobileDirect.GenericRefusal, "a direct refusal is the generic unauthorized");
            }
            // An auth frame that decrypts but is no JSON is an empty one, as on the Mac: the same one word, not a silent close.
            await using (var garbled = await DirectPhone.ConnectAsync(endpoint, host.PairingUrl, timeout.Token))
            {
                using var refused = await garbled.ExchangeRawAsync("not json {"u8.ToArray(), timeout.Token);
                Check(refused.RootElement.Text("type") == "auth_error" && refused.RootElement.Text("reason") == MobileDirect.GenericRefusal, "an auth frame that is no JSON hears the generic unauthorized");
            }
        }
        finally { await host.DisposeAsync(); Directory.Delete(directory, true); }
    }

    internal static async Task OnlyWhoMayGetsInAndRelayRedialsLeaveThemAlone()
    {
        var directory = Temp(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var addresses = new List<string> { "127.0.0.1" };
        var host = new MobileRelayHost(Path.Combine(directory, "identity"), "Fixture", [], (_, path, _, _, _) => Task.FromResult(new MobileReply(200, new { protocol = 1, path })))
        { WatchesNetwork = false, HandshakeDeadline = TimeSpan.FromMilliseconds(600), DirectRedetectInterval = TimeSpan.FromMilliseconds(200) };
        try
        {
            host.SetDirectAddressSource(() => [.. addresses]);
            await host.ApplyAsync(new MobileRemoteSettings(true, "ws://127.0.0.1:9"));
            var endpoint = host.DirectEndpoints.Single();
            int Port() => int.Parse(endpoint[(endpoint.LastIndexOf(':') + 1)..], CultureInfo.InvariantCulture);

            // Two stalled handshakes from one address hold its two slots; a third is reset unread; the deadline frees them.
            using (var first = new TcpClient()) using (var second = new TcpClient())
            {
                await first.ConnectAsync(IPAddress.Loopback, Port(), timeout.Token); await second.ConnectAsync(IPAddress.Loopback, Port(), timeout.Token);
                await Verification.Until(() => host.ConnectionIds.Count == 2);
                using var third = new TcpClient(); await third.ConnectAsync(IPAddress.Loopback, Port(), timeout.Token);
                Check(await ReadsEnd(third, timeout.Token), "a third handshake from the same address is dropped");
                Check(await ReadsEnd(first, timeout.Token) && await ReadsEnd(second, timeout.Token), "a stalled handshake is cut at the deadline");
            }
            await Verification.Until(() => host.ConnectionIds.Count == 0);

            // A paired phone stays through a relay redial and a re-read that finds the same address; the watch notices it go away.
            await using (var phone = await DirectPhone.ConnectAsync(endpoint, host.PairingUrl, timeout.Token))
            {
                using var auth = await phone.ExchangeAsync(new { type = "auth", pairingKey = Query(host.PairingUrl, "key"), clientId = "abcdefghijklmnopqrstuv", clientName = "Fixture Phone" }, timeout.Token);
                Check(auth.RootElement.Text("type") == "auth_ok", "the phone is in");
                await host.ReconnectAsync();
                using var afterRedial = await phone.ExchangeAsync(new { id = "after-redial", method = "GET", path = "/m1/info" }, timeout.Token);
                Check(afterRedial.RootElement.Text("id") == "after-redial", "a relay redial leaves a direct phone connected");
                host.RefreshDirect();
                using var afterRefresh = await phone.ExchangeAsync(new { id = "after-refresh", method = "GET", path = "/m1/info" }, timeout.Token);
                Check(afterRefresh.RootElement.Text("id") == "after-refresh", "a re-read that finds the same address changes nothing");
                addresses.Clear();
                await Verification.Until(() => host.DirectStatus.State == "notFound");
                Check(host.DirectEndpoints.Count == 0 && MobileDirectStatusText.Detail(host.DirectStatus) == Locale.Get("mobileRemote.direct.status.notFound"), "the watch re-reads the addresses on its own");
            }
            addresses.Add("127.0.0.1");
            await Verification.Until(() => host.DirectStatus.Listening);
            endpoint = host.DirectEndpoints.Single();
            await Verification.Until(() => host.ConnectionIds.Count == 0);

            // The direct switch off drops the direct phones and says so.
            await using (var phone = await DirectPhone.ConnectAsync(endpoint, host.PairingUrl, timeout.Token))
            {
                using var auth = await phone.ExchangeAsync(new { type = "auth", pairingKey = Query(host.PairingUrl, "key"), clientId = "bcdefghijklmnopqrstuvw", clientName = "Second Phone" }, timeout.Token);
                Check(auth.RootElement.Text("type") == "auth_ok", "a second phone is in");
                await host.ApplyAsync(new MobileRemoteSettings(true, "ws://127.0.0.1:9", DirectEnabled: false));
                Check(host.DirectStatus.State == "disabled" && host.DirectEndpoints.Count == 0 && MobileDirectStatusText.Detail(host.DirectStatus) == Locale.Get("mobileRemote.direct.status.off"), "the switch off says so");
                await Verification.Until(() => !host.ConnectionIds.Any(MobileDirect.IsDirect));
            }

            // Five refused keys from one address within a minute: its sixth socket is reset unread.
            await host.ApplyAsync(new MobileRemoteSettings(true, "ws://127.0.0.1:9"));
            endpoint = host.DirectEndpoints.Single();
            for (var i = 0; i < MobileDirect.MaximumAuthFailures; i++)
            {
                await using (var stranger = await DirectPhone.ConnectAsync(endpoint, host.PairingUrl, timeout.Token))
                {
                    using var refused = await stranger.ExchangeAsync(new { type = "auth", pairingKey = "wrong-key-0000000000", clientId = "zyxwvutsrqponmlkjihgf" + i.ToString(CultureInfo.InvariantCulture), clientName = "Stranger" }, timeout.Token);
                    Check(refused.RootElement.Text("reason") == MobileDirect.GenericRefusal, "every refusal is the same word");
                }
                await Verification.Until(() => host.ConnectionIds.Count == 0);
            }
            var locked = false;
            try { await using var sixth = await DirectPhone.ConnectAsync(endpoint, host.PairingUrl, timeout.Token); }
            catch (Exception ex) when (ex is WebSocketException or IOException) { locked = true; }
            Check(locked, "an address that keeps failing is shut out");
        }
        finally { await host.DisposeAsync(); Directory.Delete(directory, true); }
    }

    /// <summary>
    /// A relay that sends connected and disconnected notices carrying a direct phone's id (or any direct- id): the relay cannot reach a
    /// phone that came in directly, so the notices are ignored, nothing is dialled for them and the phone keeps answering.
    /// </summary>
    internal static async Task RelayNoticesCarryingADirectIdLeaveItAlone()
    {
        var directory = Temp(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var directId = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dialled = new ConcurrentQueue<string>(); var controls = 0;
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory }); builder.Logging.ClearProviders(); builder.WebHost.UseKestrelCore(); builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));
        await using var app = builder.Build(); app.UseWebSockets();
        app.Run(async context =>
        {
            try
            {
                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                if (context.Request.Query.TryGetValue("connectionId", out var data)) dialled.Enqueue(data.ToString());
                else if (Interlocked.Increment(ref controls) == 1)
                {
                    var id = await directId.Task.WaitAsync(timeout.Token);
                    // The phone's own id closed and opened, a direct- id nobody holds opened, then a relay phone as the marker that all were read.
                    foreach (var (type, connection) in new[] { ("disconnected", id), ("connected", id), ("connected", MobileDirect.ConnectionPrefix + Guid.NewGuid().ToString()), ("connected", "fixture-after-notices") })
                        await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { type, connectionId = connection }, Wire.Json), WebSocketMessageType.Text, true, timeout.Token);
                }
                var buffer = new byte[1024]; while ((await socket.ReceiveAsync(buffer, timeout.Token)).MessageType != WebSocketMessageType.Close) { }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { }
        });
        await app.StartAsync(timeout.Token);
        var relay = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var host = new MobileRelayHost(Path.Combine(directory, "identity"), "Fixture", [], (_, path, _, _, _) => Task.FromResult(new MobileReply(200, new { protocol = 1, path }))) { WatchesNetwork = false };
        try
        {
            host.SetDirectAddressSource(() => ["127.0.0.1"]);
            await host.ApplyAsync(new MobileRemoteSettings(true, relay));
            await Verification.Until(() => host.Status.Connected && Volatile.Read(ref controls) == 1);
            await using var phone = await DirectPhone.ConnectAsync(host.DirectEndpoints.Single(), host.PairingUrl, timeout.Token);
            using var auth = await phone.ExchangeAsync(new { type = "auth", pairingKey = Query(host.PairingUrl, "key"), clientId = "abcdefghijklmnopqrstuv", clientName = "Fixture Phone" }, timeout.Token);
            Check(auth.RootElement.Text("type") == "auth_ok", "the phone is in");
            var id = host.ConnectionIds.Single();
            Check(MobileDirect.IsDirect(id), "the phone holds a direct- id");
            directId.SetResult(id);
            await Verification.Until(() => !dialled.IsEmpty);
            Check(dialled.SequenceEqual(["fixture-after-notices"]), "a connected notice for a direct- id dials nothing: " + string.Join(",", dialled));
            Check(host.ConnectionIds.Contains(id) && host.ConnectionIds.Count(MobileDirect.IsDirect) == 1, "a disconnected notice for the phone's id leaves it in the table, and a connected one adds no other direct- id");
            using var after = await phone.ExchangeAsync(new { id = "after-notices", method = "GET", path = "/m1/info" }, timeout.Token);
            Check(after.RootElement.Text("id") == "after-notices" && after.RootElement.GetProperty("status").GetInt32() == 200, "the direct phone still answers after the relay's notices");
        }
        finally { await host.DisposeAsync(); await app.StopAsync(CancellationToken.None); Directory.Delete(directory, true); }
    }
}
