using System.Globalization;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace MightyClaude.Core;

/// <summary>
/// The direct phone path (docs/relay.md, "Tailscale 직접 연결"; M/MightyCore/Remote/MobileDirect.swift): a phone on the same tailnet
/// opens its data socket straight to this PC instead of to the relay, at <c>ws://&lt;endpoint&gt;/ws?v=1</c>. Everything after the
/// upgrade is the relay path unchanged: the same hello/ready handshake, ChaCha20-Poly1305 frames and auth.
/// </summary>
public static class MobileDirect
{
    /// <summary>The listener's port unless Settings changes it.</summary>
    public const int DefaultPort = 43139;
    /// <summary>The frame limit on both paths: the relay's own, kept for the direct socket that replaces it.</summary>
    public const int MaximumFrameBytes = 1024 * 1024;
    /// <summary>Phones inside the handshake on the direct path, counted apart from the relay's.</summary>
    public const int MaximumUnauthenticated = 4;
    /// <summary>Of those, at most this many from one remote address.</summary>
    public const int MaximumUnauthenticatedPerPeer = 2;
    /// <summary>The phone table, shared with the relay path (docs/relay.md: 32 per host).</summary>
    public const int MaximumClients = 32;
    /// <summary>Connections the listeners hold together: four in their handshake and the 32 phones.</summary>
    public const int ConnectionLimit = MaximumUnauthenticated + MaximumClients;
    /// <summary>Failed authentications one address may have inside <see cref="AuthFailureWindow"/> before its new sockets are reset unread.</summary>
    public const int MaximumAuthFailures = 5;
    public static readonly TimeSpan AuthFailureWindow = TimeSpan.FromSeconds(60);
    /// <summary>From the socket to auth_ok, the HTTP upgrade included.</summary>
    public static readonly TimeSpan HandshakeDeadline = TimeSpan.FromSeconds(10);
    /// <summary>How often the addresses are read again while the path is on, on top of every address change Windows reports.</summary>
    public static readonly TimeSpan RedetectInterval = TimeSpan.FromSeconds(15);
    /// <summary>At most this many endpoints travel in a QR, an auth_ok or /m1/info.</summary>
    public const int MaximumEndpoints = 8;
    /// <summary>The phone's upgrade request, headers included.</summary>
    public const int MaximumRequestHeadBytes = 8192;
    public const string ConnectionPrefix = "direct-";
    /// <summary>A direct refusal says one word, so a tailnet peer learns nothing of which ids or keys this PC knows.</summary>
    public const string GenericRefusal = "unauthorized";
    private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    public static bool ValidPort(int port) => port is >= 1024 and <= 65535;
    public static bool IsDirect(string connectionId) => connectionId.StartsWith(ConnectionPrefix, StringComparison.Ordinal);

    /// <summary><c>host:port</c>, an IPv6 literal in brackets.</summary>
    public static string Endpoint(string host, int port) => (host.Contains(':') ? "[" + host + "]" : host) + ":" + port.ToString(CultureInfo.InvariantCulture);

    /// <summary>What the host advertises: the bound addresses with their ports, Tailscale addresses only (any host through the seam), each once, at most eight.</summary>
    public static IReadOnlyList<string> Advertised(IEnumerable<(string Host, int Port)> bound, bool allowAnyHost = false)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return [.. bound.Where(entry => allowAnyHost || TailscaleAddresses.Contains(entry.Host)).Select(entry => Endpoint(entry.Host, entry.Port)).Where(seen.Add).Take(MaximumEndpoints)];
    }

    public static string AcceptKey(string key) => Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + WebSocketGuid)));

    /// <summary>
    /// The 101 for a phone's upgrade request head (up to and with its blank line), or null: <c>GET /ws</c> (its query is not read)
    /// over HTTP/1.1, <c>Upgrade: websocket</c>, <c>Connection</c> carrying <c>Upgrade</c>, version 13 and a 16-byte key. Nothing
    /// else is negotiated: no subprotocol, no extension.
    /// </summary>
    public static string? UpgradeResponse(string head)
    {
        var lines = head.Split("\r\n");
        var request = lines[0].Split(' ');
        if (request.Length != 3 || request[0] != "GET" || request[2] != "HTTP/1.1" || request[1].Split('?', 2)[0] != "/ws") return null;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1).Where(line => line.Length > 0))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) return null;
            headers.TryAdd(line[..colon].Trim(), line[(colon + 1)..].Trim());
        }
        if (!headers.TryGetValue("Upgrade", out var upgrade) || !upgrade.Equals("websocket", StringComparison.OrdinalIgnoreCase)) return null;
        if (!headers.TryGetValue("Connection", out var connection) || !connection.Split(',').Any(token => token.Trim().Equals("Upgrade", StringComparison.OrdinalIgnoreCase))) return null;
        if (!headers.TryGetValue("Sec-WebSocket-Version", out var version) || version != "13") return null;
        if (!headers.TryGetValue("Sec-WebSocket-Key", out var key) || !IsKey(key)) return null;
        return "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: " + AcceptKey(key) + "\r\n\r\n";
    }

    private static bool IsKey(string key)
    {
        try { return Convert.FromBase64String(key).Length == 16; }
        catch (FormatException) { return false; }
    }

    /// <summary>
    /// Reads the phone's upgrade from <paramref name="stream"/> (at most 8 KiB, nothing after its blank line: a client speaks only after
    /// the 101), answers it and returns the server WebSocket; null for anything else, the caller then closes the connection unanswered.
    /// </summary>
    public static async Task<WebSocket?> AcceptWebSocketAsync(Stream stream, CancellationToken token)
    {
        var buffer = new byte[MaximumRequestHeadBytes]; var length = 0;
        while (true)
        {
            if (length == buffer.Length) return null;
            var read = await stream.ReadAsync(buffer.AsMemory(length), token);
            if (read == 0) return null;
            length += read;
            var end = buffer.AsSpan(0, length).IndexOf("\r\n\r\n"u8);
            if (end < 0) continue;
            if (end + 4 != length) return null;
            var response = UpgradeResponse(Encoding.ASCII.GetString(buffer, 0, end + 4));
            if (response is null) return null;
            await stream.WriteAsync(Encoding.ASCII.GetBytes(response), token);
            // Pings are answered by the stack; the app's own 20 s ping and 60 s idle limit keep the session honest, as on the relay.
            return WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true, KeepAliveInterval = TimeSpan.Zero });
        }
    }

    /// <summary>
    /// Whether a socket the listeners just accepted may start its handshake (M/MobileRemoteService.swift acceptDirect): a peer in
    /// Tailscale's ranges (any peer only through the seam), room in the phone table, fewer than four handshakes on the direct path
    /// and two from this address, and fewer than five failed auths from it in the last minute.
    /// </summary>
    public static bool Admit(string? peer, bool allowAnyPeer, int clients, IReadOnlyCollection<string> pendingPeers, int recentFailures) =>
        peer is { Length: > 0 } && (allowAnyPeer || TailscaleAddresses.Contains(peer)) && clients < MaximumClients
        && pendingPeers.Count < MaximumUnauthenticated && pendingPeers.Count(pending => pending == peer) < MaximumUnauthenticatedPerPeer
        && recentFailures < MaximumAuthFailures;
}

/// <summary>Failed direct authentications per remote address, kept for <see cref="MobileDirect.AuthFailureWindow"/>; at most 256 addresses unless all are recent.</summary>
public sealed class DirectAuthFailures
{
    private const int MaximumPeers = 256;
    private readonly object sync = new();
    private readonly Dictionary<string, List<DateTimeOffset>> failures = new(StringComparer.Ordinal);

    public int Peers { get { lock (sync) return failures.Count; } }

    public int Recent(string peer, DateTimeOffset now)
    {
        lock (sync)
        {
            if (!failures.TryGetValue(peer, out var times)) return 0;
            times.RemoveAll(time => now - time >= MobileDirect.AuthFailureWindow);
            if (times.Count == 0) failures.Remove(peer);
            return times.Count;
        }
    }

    public void Record(string peer, DateTimeOffset now)
    {
        lock (sync)
        {
            if (!failures.ContainsKey(peer) && failures.Count >= MaximumPeers)
                foreach (var old in failures.Where(entry => entry.Value.All(time => now - time >= MobileDirect.AuthFailureWindow)).Select(entry => entry.Key).ToArray()) failures.Remove(old);
            if (!failures.TryGetValue(peer, out var times)) failures[peer] = times = [];
            times.Add(now);
        }
    }
}

/// <summary>
/// What the direct path is doing. <c>off</c>: the remote is off; <c>disabled</c>: its direct switch is; <c>notFound</c>: no Tailscale
/// address; <c>listening</c>: on <see cref="Endpoints"/>; <c>failed</c>: no address could be bound, <see cref="Error"/> says why as one
/// of <see cref="MobileDirectError"/>'s codes, never Windows' own text.
/// </summary>
public sealed record MobileDirectStatus(string State, IReadOnlyList<string> Endpoints, string? Error = null)
{
    public static readonly MobileDirectStatus Off = new("off", []);
    [JsonIgnore] public bool Listening => State == "listening";
}

/// <summary>Why the direct path could not listen (<see cref="MobileDirectStatus.Error"/>): codes only, each with its own words (<see cref="MobileDirectStrings.Error"/>).</summary>
public static class MobileDirectError
{
    /// <summary>Another program holds the port on that address.</summary>
    public const string AddressInUse = "addressInUse";
    /// <summary>Windows refused the port (a reserved range, or another program's exclusive use).</summary>
    public const string AccessDenied = "accessDenied";
    /// <summary>Any other reason binding failed.</summary>
    public const string BindFailed = "bindFailed";
    /// <summary>This PC's network adapters could not be read.</summary>
    public const string AdaptersUnreadable = "adaptersUnreadable";

    /// <summary>The code for a listener that could not start; the socket's own text goes to the trace only.</summary>
    public static string Of(System.Net.Sockets.SocketException ex) => ex.SocketErrorCode switch
    {
        System.Net.Sockets.SocketError.AddressAlreadyInUse => AddressInUse,
        System.Net.Sockets.SocketError.AccessDenied => AccessDenied,
        _ => BindFailed,
    };
}

/// <summary>The line under the direct switch, in the Mac's words (mobileRemote.direct.status.*), with the Windows words for why it could not listen.</summary>
public static class MobileDirectStatusText
{
    public static string Detail(MobileDirectStatus status) => status.State switch
    {
        "disabled" => Locale.Get("mobileRemote.direct.status.off"),
        "notFound" => Locale.Get("mobileRemote.direct.status.notFound"),
        "listening" => Locale.Get("mobileRemote.direct.status.listeningTemplate", new Dictionary<string, string> { ["addresses"] = string.Join(", ", status.Endpoints) }),
        "failed" => Locale.Get("mobileRemote.direct.status.failedTemplate", new Dictionary<string, string> { ["error"] = MobileDirectStrings.Error(status.Error) }),
        _ => "",
    };
}
