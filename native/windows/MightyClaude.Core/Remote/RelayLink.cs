using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace MightyClaude.Core;

/// <summary>
/// What the control socket cares about in the PC's network: whether there is one at all, and which interfaces and routers
/// carry it (M/MightyCore/Remote/RelayLink.swift:8-25). A Wi-Fi switch, a VPN coming up or a cable pulled changes one of these.
/// </summary>
public sealed record RelayNetworkPath(bool Satisfied, IReadOnlyList<string> Interfaces, IReadOnlyList<string> Gateways)
{
    public bool Equals(RelayNetworkPath? other) => other is not null && Satisfied == other.Satisfied && Interfaces.SequenceEqual(other.Interfaces) && Gateways.SequenceEqual(other.Gateways);
    public override int GetHashCode() => HashCode.Combine(Satisfied, Interfaces.Count, Gateways.Count);

    /// <summary>The path as Windows reports it now (<see cref="From"/>); null when the adapters could not be read, which is no reason to redial.</summary>
    public static RelayNetworkPath? Current()
    {
        try
        {
            var up = NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up)
                .Select(n => (n.Id, n.NetworkInterfaceType, (IReadOnlyList<IPAddress>)n.GetIPProperties().GatewayAddresses.Select(g => g.Address).ToArray())).ToArray();
            return From(up, NetworkInterface.GetIsNetworkAvailable());
        }
        catch (Exception ex) when (ex is NetworkInformationException or InvalidOperationException or PlatformNotSupportedException) { return null; }
    }
    /// <summary>
    /// The adapters that can carry the socket out, as the Mac's path names the interfaces its default route may use: those with a
    /// default gateway, loopback and tunnels (Teredo, 6to4) aside. A Hyper-V, WSL or Docker switch has no gateway on the host's
    /// side, so starting one does not cost the phones their tunnels; a VPN that takes the default route does change the path.
    /// </summary>
    public static RelayNetworkPath From(IEnumerable<(string Id, NetworkInterfaceType Type, IReadOnlyList<IPAddress> Gateways)> up, bool available)
    {
        static bool Gateway(IPAddress address) => address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6 && !address.Equals(IPAddress.Any) && !address.Equals(IPAddress.IPv6Any);
        var carrying = up.Where(n => n.Type is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) && n.Gateways.Any(Gateway)).ToArray();
        var gateways = carrying.SelectMany(n => n.Gateways).Where(Gateway).Select(g => g.ToString()).Distinct().Order(StringComparer.Ordinal).ToArray();
        return new(carrying.Length > 0 && available, carrying.Select(n => n.Id).Distinct().Order(StringComparer.Ordinal).ToArray(), gateways);
    }
}

/// <summary>
/// When the host drops its control socket and dials the relay again (M/MightyCore/Remote/RelayLink.swift:31-129). The relay
/// forgets a host whose socket stops answering its pings, but the host's side of a connection the network pulled out from
/// under it never errors on its own, so these decisions are made here rather than left to the receive loop.
/// </summary>
public static class RelayLinkPolicy
{
    /// <summary>How often a connected control socket is pinged, and how long a ping may go unanswered: a dead socket is noticed at worst 20 + 10 = 30 s after it died.</summary>
    public static readonly TimeSpan KeepaliveInterval = TimeSpan.FromSeconds(20), KeepaliveTimeout = TimeSpan.FromSeconds(10);
    /// <summary>How long path reports must stay quiet before one decision is made: a Wi-Fi switch reports several paths within a second, and each must not redial.</summary>
    public static readonly TimeSpan PathSettle = TimeSpan.FromSeconds(1.5);
    /// <summary>A stream of reports that never goes quiet still settles after this long.</summary>
    public static readonly TimeSpan PathSettleMaxWait = TimeSpan.FromSeconds(5);
    /// <summary>Path-triggered redials are at least this far apart, the gap doubling while they keep coming (a flapping link) up to the maximum.</summary>
    public static readonly TimeSpan RestartGapMin = TimeSpan.FromSeconds(5), RestartGapMax = TimeSpan.FromSeconds(30);

    public enum PathAction { None, Offline, Reconnect }

    /// <summary>
    /// Everything reported since the last decision, not only the last report: a Wi-Fi switch between two routers on the same
    /// 192.168.x.1 reports wifi → nothing → wifi, and the settled path then equals the old one although the socket under it is gone.
    /// </summary>
    public sealed class Window
    {
        /// <summary>The latest report, or null when only a wake opened the window.</summary>
        public RelayNetworkPath? Latest { get; private set; }
        /// <summary>Some report differed from the settled path or had no network.</summary>
        public bool Disturbed { get; private set; }
        /// <summary>A wake asked for a redial whatever the path does.</summary>
        public bool Forced { get; private set; }
        public DateTimeOffset? OpenedAt { get; private set; }
        public bool IsEmpty => Latest is null && !Forced;
        public void Report(RelayNetworkPath path, RelayNetworkPath? settled, DateTimeOffset now)
        {
            OpenedAt ??= now;
            if (settled is not null && (path != settled || !path.Satisfied)) Disturbed = true;
            Latest = path;
        }
        public void Force(DateTimeOffset now) { OpenedAt ??= now; Forced = true; }
        /// <summary>How long to wait before deciding: the settle delay, cut short so the window never stays open longer than <see cref="PathSettleMaxWait"/>.</summary>
        public TimeSpan Delay(DateTimeOffset now)
        {
            if (OpenedAt is not { } opened) return PathSettle;
            var left = opened + PathSettleMaxWait - now;
            return left < TimeSpan.Zero ? TimeSpan.Zero : left < PathSettle ? left : PathSettle;
        }
    }

    /// <summary>
    /// The minimum gap between path-triggered redials. A redial more than the current gap plus the maximum after the last one
    /// starts over at the minimum; one sooner doubles the gap.
    /// </summary>
    public sealed class Pacing
    {
        private DateTimeOffset? last;
        private int streak;
        public TimeSpan Gap => TimeSpan.FromSeconds(Math.Min(RestartGapMax.TotalSeconds, RestartGapMin.TotalSeconds * Math.Pow(2, streak)));
        /// <summary>How long a redial wanted now has to wait.</summary>
        public TimeSpan Wait(DateTimeOffset now) => last is { } at && at + Gap - now is var left && left > TimeSpan.Zero ? left : TimeSpan.Zero;
        public void Record(DateTimeOffset now)
        {
            streak = last is { } at && now - at < Gap + RestartGapMax ? streak + 1 : 0;
            last = now;
        }
    }

    /// <summary>
    /// <paramref name="old"/> is the path the current connection was made on (null before the first report),
    /// <paramref name="next"/> the settled one (null when only a wake fired). No network reads as offline rather than as a reason
    /// to redial; any other difference, or a disturbed window, means the old socket may be half-open, so it is replaced.
    /// </summary>
    public static PathAction Action(RelayNetworkPath? old, RelayNetworkPath? next, bool forced, bool disturbed = false)
    {
        if (next is null) return forced ? PathAction.Reconnect : PathAction.None;
        if (!next.Satisfied) return old?.Satisfied == false && !forced ? PathAction.None : PathAction.Offline;
        if (forced || disturbed) return PathAction.Reconnect;
        return old is null || old == next ? PathAction.None : PathAction.Reconnect;
    }
    public static PathAction Action(RelayNetworkPath? old, Window window) => Action(old, window.Latest, window.Forced, window.Disturbed);
}
