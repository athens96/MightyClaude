using System.Text.Json;
using MightyClaude.Core;

// native/contracts/fixtures/tailscale-addresses.json is the address rule the Mac (RemoteWorkspaceAddressFixtureTests) checks
// too; Windows reads each case's `win32` answer.
internal static class TailscaleAddressesVerification
{
    private static void Check(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }

    internal static Task SharedFixture()
    {
        using var stream = typeof(TailscaleAddressesVerification).Assembly.GetManifestResourceStream("MightyClaude.Core.Tests.TailscaleAddresses.json")!;
        using var fixture = JsonDocument.Parse(stream); var root = fixture.RootElement;
        foreach (var address in root.GetProperty("contains").GetProperty("accepted").EnumerateArray())
            Check(TailscaleAddresses.Contains(address.GetString()!), "in Tailscale's ranges: " + address.GetString());
        foreach (var address in root.GetProperty("contains").GetProperty("rejected").EnumerateArray())
            Check(!TailscaleAddresses.Contains(address.GetString()!), "outside Tailscale's ranges: " + address.GetString());
        var cases = root.GetProperty("cases").EnumerateArray().ToArray();
        Check(cases.Length >= 12, "the shared fixture was not loaded");
        foreach (var item in cases)
        {
            var read = item.GetProperty("interfaces").EnumerateArray()
                .SelectMany(adapter => adapter.GetProperty("addresses").EnumerateArray().Select(address => (InterfaceName: adapter.GetProperty("name").GetString()!, Address: address.GetString()!)))
                .ToArray();
            var expected = item.GetProperty("expected").GetProperty("win32").EnumerateArray().Select(address => address.GetString()!).ToArray();
            var actual = TailscaleAddresses.Select(read);
            Check(actual.SequenceEqual(expected), item.GetProperty("name").GetString() + ": got [" + string.Join(", ", actual) + "]");
        }
        return Task.CompletedTask;
    }

    // The Mac (TailscaleAddresses.swift contains/owned) takes an IPv4-mapped address in Tailscale's CGNAT range as its IPv4 and
    // then spells it the way inet_ntop does (still mapped, so listed with the IPv6 addresses). Windows does the same.
    internal static Task AMappedAddressCountsAsItsIPv4()
    {
        Check(TailscaleAddresses.Contains("::ffff:100.100.1.1") && TailscaleAddresses.Contains("[::FFFF:100.100.1.1]") && TailscaleAddresses.Contains("::ffff:6464:101"), "a mapped CGNAT address is Tailscale's");
        Check(!TailscaleAddresses.Contains("::ffff:100.128.0.1") && !TailscaleAddresses.Contains("::ffff:192.168.0.5") && !TailscaleAddresses.Contains("::ffff:100.100.1.1%7"), "a mapped address outside the range, or scoped, is not");
        Check(!TailscaleAddresses.InTailscaleIPv6Range("::ffff:100.100.1.1"), "a mapped address does not make an adapter Tailscale's");
        var onTunnel = TailscaleAddresses.Select([("utun4", "fd7a:115c:a1e0::5"), ("utun4", "::ffff:100.100.1.1"), ("utun4", "100.101.1.2")]);
        Check(onTunnel.SequenceEqual(["100.101.1.2", "::ffff:100.100.1.1", "fd7a:115c:a1e0::5"]), "mapped address on a tunnel: got [" + string.Join(", ", onTunnel) + "]");
        var alone = TailscaleAddresses.Select([("utun4", "::ffff:100.100.1.1")]);
        Check(alone.Count == 0, "a mapped address alone does not make an adapter Tailscale's");
        return Task.CompletedTask;
    }

    internal static Task DetectionOffersOnlyTailscaleAddresses()
    {
        var read = TailscaleAddresses.InterfaceAddresses();
        foreach (var (name, address) in TailscaleAddresses.Owned(read))
            Check(TailscaleAddresses.Contains(address) && read.Any(entry => entry.InterfaceName == name && TailscaleAddresses.InTailscaleIPv6Range(TailscaleAddresses.Canonical(entry.Address))),
                "detection offered " + address + " on " + name);
        Check(TailscaleAddresses.Current().All(TailscaleAddresses.Contains), "whatever this machine has, only Tailscale addresses come back");
        Check(TailscaleAddresses.Canonical("FD7A:115C:A1E0:0::5%12") == "fd7a:115c:a1e0::5" && TailscaleAddresses.Canonical("[fd7a:115c:a1e0::5]") == "fd7a:115c:a1e0::5"
            && TailscaleAddresses.Canonical("100.101.1.2") == "100.101.1.2", "one spelling per address");
        return Task.CompletedTask;
    }
}
