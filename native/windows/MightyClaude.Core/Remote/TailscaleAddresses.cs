using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace MightyClaude.Core;

/// <summary>
/// This PC's Tailscale addresses (M/MightyCore/Remote/TailscaleAddresses.swift), read from its adapters without the Tailscale CLI.
/// Tailscale gives every device an IPv4 address in 100.64.0.0/10 and an IPv6 address in fd7a:115c:a1e0::/48. An adapter counts
/// as Tailscale's only when it carries an address in that IPv6 range, and then its addresses in both ranges count: a carrier-grade
/// NAT can hand Wi-Fi a 100.64/10 address and other VPNs (Cloudflare WARP's 100.96.0.0/12) put one on their own adapter. Unlike the
/// Mac, the adapter's name is not part of the rule: Windows lets the user rename an adapter, and the Wintun adapter's type is no
/// named NetworkInterfaceType. The cases are native/contracts/fixtures/tailscale-addresses.json, which both apps read.
/// </summary>
public static class TailscaleAddresses
{
    private static readonly byte[] Prefix = [0xfd, 0x7a, 0x11, 0x5c, 0xa1, 0xe0];

    /// <summary>Whether <paramref name="address"/> (an IPv4 or IPv6 literal, optionally in brackets) is in one of Tailscale's two ranges. An IPv4-mapped IPv6 address counts as its IPv4; a <c>%scope</c> never counts.</summary>
    public static bool Contains(string address)
    {
        var value = address.Trim('[', ']').ToLowerInvariant();
        if (!value.Contains(':')) return IPv4(value) is { } v4 && InCgnat(v4[0], v4[1]);
        if (value.Contains('%') || !IPAddress.TryParse(value, out var ip) || ip.AddressFamily != AddressFamily.InterNetworkV6) return false;
        if (ip.IsIPv4MappedToIPv6) { var mapped = ip.MapToIPv4().GetAddressBytes(); return InCgnat(mapped[0], mapped[1]); }
        return ip.GetAddressBytes().AsSpan(0, 6).SequenceEqual(Prefix);
    }

    /// <summary>fd7a:115c:a1e0::/48, which only Tailscale hands out (no <c>%scope</c>).</summary>
    public static bool InTailscaleIPv6Range(string address)
    {
        var value = address.Trim('[', ']').ToLowerInvariant();
        return value.Contains(':') && !value.Contains('%') && IPAddress.TryParse(value, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6
            && !ip.IsIPv4MappedToIPv6 && ip.GetAddressBytes().AsSpan(0, 6).SequenceEqual(Prefix);
    }

    /// <summary>The Tailscale addresses among <paramref name="interfaces"/> (adapter name and address, as read). Pure, so the rule is checked without this PC's adapters.</summary>
    public static IReadOnlyList<string> Select(IEnumerable<(string InterfaceName, string Address)> interfaces) => [.. Owned(interfaces).Select(entry => entry.Address)];

    /// <summary><see cref="Select"/> with each address's adapter: each address once in its canonical spelling, IPv4 first, each family sorted ordinally.</summary>
    public static IReadOnlyList<(string InterfaceName, string Address)> Owned(IEnumerable<(string InterfaceName, string Address)> interfaces)
    {
        var read = interfaces.Select(entry => (InterfaceName: entry.InterfaceName, Address: Unscoped(entry.Address))).ToArray();
        var tailnet = read.Where(entry => InTailscaleIPv6Range(entry.Address)).Select(entry => entry.InterfaceName).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var found = read.Where(entry => tailnet.Contains(entry.InterfaceName) && Contains(entry.Address))
            .Select(entry => (InterfaceName: entry.InterfaceName, Address: Canonical(entry.Address)))
            .Where(entry => seen.Add(entry.Address)).ToArray();
        return [.. found.Where(entry => !entry.Address.Contains(':')).OrderBy(entry => entry.Address, StringComparer.Ordinal),
                .. found.Where(entry => entry.Address.Contains(':')).OrderBy(entry => entry.Address, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Every up adapter's unicast addresses, as Windows reports them. One adapter that cannot be read is skipped and the others kept;
    /// when the list itself or every adapter on it cannot be read, <see cref="AdaptersUnreadableException"/>.
    /// </summary>
    public static IReadOnlyList<(string InterfaceName, string Address)> InterfaceAddresses()
    {
        NetworkInterface[] adapters;
        try { adapters = NetworkInterface.GetAllNetworkInterfaces(); }
        catch (Exception ex) when (Unreadable(ex)) { throw new AdaptersUnreadableException(ex); }
        return Gather(adapters.Select(adapter => (Func<IReadOnlyList<(string InterfaceName, string Address)>>)(() => adapter.OperationalStatus != OperationalStatus.Up ? []
            : [.. adapter.GetIPProperties().UnicastAddresses.Select(unicast => (InterfaceName: adapter.Name, Address: unicast.Address.ToString()))])));
    }

    /// <summary>
    /// Reads each adapter in turn (<see cref="InterfaceAddresses"/>; pure, so the checks give it adapters that fail): one that cannot
    /// be read is skipped, and its error kept for the trace only; when there were adapters and none of them could be read,
    /// <see cref="AdaptersUnreadableException"/>.
    /// </summary>
    internal static IReadOnlyList<(string InterfaceName, string Address)> Gather(IEnumerable<Func<IReadOnlyList<(string InterfaceName, string Address)>>> adapters)
    {
        var found = new List<(string InterfaceName, string Address)>(); var read = 0; Exception? first = null;
        foreach (var adapter in adapters)
        {
            try { found.AddRange(adapter()); read++; }
            catch (Exception ex) when (Unreadable(ex))
            {
                first ??= ex;
                System.Diagnostics.Trace.TraceWarning("tailscale addresses: an adapter could not be read: " + ex.Message);
            }
        }
        if (read == 0 && first is not null) throw new AdaptersUnreadableException(first);
        return found;
    }

    private static bool Unreadable(Exception ex) => ex is NetworkInformationException or PlatformNotSupportedException or InvalidOperationException;

    /// <summary>The addresses this PC has on Tailscale now; empty while Tailscale is off; <see cref="AdaptersUnreadableException"/> when no adapter could be read.</summary>
    public static IReadOnlyList<string> Current() => Select(InterfaceAddresses());

    /// <summary>One spelling per address: no brackets or <c>%scope</c>, lowercase, IPv6 compressed.</summary>
    public static string Canonical(string address)
    {
        var value = Unscoped(address).Trim('[', ']').ToLowerInvariant();
        return value.Contains(':') && IPAddress.TryParse(value, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6 ? ip.ToString() : value;
    }

    private static string Unscoped(string address) => address.Split('%', 2)[0];
    private static bool InCgnat(byte first, byte second) => first == 100 && second is >= 64 and <= 127;

    /// <summary>Four dotted decimal parts, 0-255, no leading zeros: the strict form (IPAddress.TryParse also takes "100.64.0").</summary>
    private static byte[]? IPv4(string value)
    {
        var parts = value.Split('.');
        if (parts.Length != 4) return null;
        var bytes = new byte[4];
        for (var i = 0; i < 4; i++)
        {
            var part = parts[i];
            if (part.Length is 0 or > 3 || !part.All(char.IsAsciiDigit) || part.Length > 1 && part[0] == '0') return null;
            var number = int.Parse(part, NumberStyles.None, CultureInfo.InvariantCulture);
            if (number > 255) return null;
            bytes[i] = (byte)number;
        }
        return bytes;
    }
}

/// <summary>This PC's network adapters could not be read at all (<see cref="TailscaleAddresses.InterfaceAddresses"/>): the list, or every adapter on it. Its inner exception's text goes to the trace only.</summary>
public sealed class AdaptersUnreadableException(Exception inner) : Exception("This PC's network adapters could not be read.", inner);
