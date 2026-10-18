using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace MightyClaude.Core;

/// <summary>
/// A host address as the user typed it (M/MightyCore/Remote/RemoteTransport.swift RemoteAddress): a Tailscale IP or a MagicDNS name,
/// an optional port (43137 when left out) and an optional http://. Anything else (another scheme, a path, a user, a query, a scope,
/// a character a host name cannot hold) is refused.
/// </summary>
public sealed partial record RemoteAddress(string Host, int Port)
{
    /// <summary>host:port, IPv6 in brackets: what is saved and shown.</summary>
    [JsonIgnore] public string Display => (Host.Contains(':') ? "[" + Host + "]" : Host) + ":" + Port.ToString(CultureInfo.InvariantCulture);

    public const int MaximumInput = 300;

    /// <summary>Reads <paramref name="input"/>; <see cref="ArgumentException"/> with remote.connect.error.badAddress or .badPort when it is no host address.</summary>
    public static RemoteAddress Parse(string input)
    {
        static ArgumentException Bad() => new(Locale.Get("remote.connect.error.badAddress"));
        var raw = input ?? "";
        if (raw.Contains('\0')) throw Bad();
        // Whitespace and newlines around the address are trimmed (char.IsWhiteSpace is the Mac's .whitespacesAndNewlines); a control
        // character anywhere is refused below, never stripped.
        var text = raw.Trim();
        if (text.Length is 0 or > MaximumInput) throw Bad();
        if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) text = text[7..];
        if (text.EndsWith('/')) text = text[..^1];
        if (text.IndexOfAny(['/', '@', '?', '#', '%']) >= 0 || text.Contains("://", StringComparison.Ordinal)) throw Bad();
        var host = text; var port = RemoteProtocol.DefaultPort;
        if (text.StartsWith('['))
        {
            var close = text.IndexOf(']');
            if (close < 0) throw Bad();
            host = text[1..close]; var rest = text[(close + 1)..];
            if (rest.Length > 0 && (!rest.StartsWith(':') || !ReadPort(rest[1..], out port))) throw Bad();
        }
        else if (text.Count(c => c == ':') == 1)
        {
            var colon = text.IndexOf(':'); host = text[..colon];
            if (!ReadPort(text[(colon + 1)..], out port)) throw Bad();
        }
        host = host.ToLowerInvariant();
        if (port is < 1024 or > 65535) throw new ArgumentException(Locale.Get("remote.connect.error.badPort"));
        if (host.Length is 0 or > 253 || !HostCharacters().IsMatch(host) || host.StartsWith('-') || host.StartsWith('.') || host.Contains("..", StringComparison.Ordinal)) throw Bad();
        // A colon is only ever an IPv6 literal's.
        if (host.Contains(':'))
        {
            if (!IPAddress.TryParse(host, out var ip) || ip.AddressFamily != AddressFamily.InterNetworkV6) throw Bad();
            host = TailscaleAddresses.Canonical(host);
        }
        return new(host, port);
    }

    /// <summary>As Swift's Int(_:) reads a port: ASCII digits after an optional + or -, so "+5000" is 5000 and "-1" is a port out of range (badPort), not a bad address.</summary>
    private static bool ReadPort(string value, out int port) => int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out port);

    [GeneratedRegex(@"^[a-z0-9.:-]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex HostCharacters();
}

/// <summary>
/// Which peers the remote-workspace host answers and the client connects to (M/MightyCore/Remote/RemoteTransport.swift
/// RemotePeerPolicy): Tailscale addresses only. Loopback is allowed only where a test (or the smoke run) injects allowLoopback in
/// code; no setting, connection field or IPC reaches it.
/// </summary>
public static class RemotePeerPolicy
{
    public static bool Allowed(string address, bool allowLoopback = false)
    {
        // A peer's address as the network stack reports it may carry its interface (fd7a:…%17); the address alone decides.
        var unscoped = (address ?? "").Split('%', 2)[0];
        if (TailscaleAddresses.Contains(unscoped)) return true;
        if (!allowLoopback) return false;
        // The spellings the Mac's inet_pton check takes: four dotted decimal parts (127.1, 2130706433, 0x7f.1 and 0177.0.0.1 are not)
        // or ::1 exactly (::ffff:127.0.0.1 is not).
        var value = unscoped.Trim('[', ']').ToLowerInvariant();
        return TailscaleAddresses.IPv4(value) is { } v4 ? v4[0] == 127 : value == "::1";
    }

    /// <summary>A peer as the socket reports it: an IPv4-mapped IPv6 address is its IPv4 (so a dual-mode socket's ::ffff:127.0.0.1 is 127.0.0.1).</summary>
    public static bool Allowed(IPAddress address, bool allowLoopback = false) =>
        Allowed((address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString(), allowLoopback);
}

/// <summary>
/// Whether a host is reached through this PC's Tailscale tunnel (M/MightyCore/Remote/RemoteClientService.swift
/// routedThroughTailscale). A carrier-grade NAT network (or another VPN) can route 100.64/10 over Wi-Fi, outside WireGuard, and
/// routes change while the app runs, so the client asks before every request and ends the connection with
/// remote.connect.error.notViaTailscale (never a passing failure) when the answer is no.
/// </summary>
public static class RemoteRoute
{
    /// <summary>
    /// The local address the routing table picks toward <paramref name="ip"/> (an IP literal, optionally in brackets; a name is never
    /// resolved here), in its canonical spelling: a UDP socket is connected, which only picks the route, and nothing is sent. Null when
    /// <paramref name="ip"/> is no IP or there is no route.
    /// </summary>
    public static string? SourceAddressToward(string ip)
    {
        if (!IPAddress.TryParse((ip ?? "").Trim('[', ']'), out var target)) return null;
        try
        {
            using var socket = new Socket(target.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(new IPEndPoint(target, 9));
            return socket.LocalEndPoint is IPEndPoint local
                ? TailscaleAddresses.Canonical((local.Address.IsIPv4MappedToIPv6 ? local.Address.MapToIPv4() : local.Address).ToString())
                : null;
        }
        catch (Exception ex) when (ex is SocketException or PlatformNotSupportedException or NotSupportedException) { return null; }
    }

    /// <summary>The route toward <paramref name="ip"/> leaves from one of this PC's Tailscale addresses. No route, or adapters that cannot be read, is a no.</summary>
    public static bool ThroughTailscale(string ip) => ThroughTailscale(ip, TailscaleAddresses.Current);

    /// <summary><see cref="ThroughTailscale(string)"/> with this PC's Tailscale addresses read by <paramref name="current"/>, so the checks give it lists and failures.</summary>
    internal static bool ThroughTailscale(string ip, Func<IReadOnlyList<string>> current)
    {
        if (SourceAddressToward(ip) is not { } source) return false;
        try { return current().Contains(source, StringComparer.Ordinal); }
        catch (AdaptersUnreadableException ex)
        {
            System.Diagnostics.Trace.TraceWarning("remote route: " + ex.GetBaseException().Message);
            return false;
        }
    }
}
