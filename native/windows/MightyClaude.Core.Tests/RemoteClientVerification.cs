using System.Text.Json;
using MightyClaude.Core;

/// <summary>The Windows client of remote workspaces (docs/remote-workspaces.md): saved connections, keys, addresses, transport, runs.</summary>
internal static partial class RemoteClientVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task Reject(Func<Task> action, string message, Func<Exception, bool>? expected = null)
    {
        try { await action(); }
        catch (Exception ex) { if (expected is null || expected(ex)) return; throw new InvalidOperationException(message + " (threw " + ex.GetType().Name + ": " + ex.Message + ")"); }
        throw new InvalidOperationException(message);
    }
    internal static readonly string Key = new('k', 43);

    internal static async Task SavedConnectionsKeepNoKey()
    {
        var directory = Verification.Temp();
        try
        {
            await RemoteConnectionFile.WriteAsync(directory, [new("conn-1", "studio", "100.101.102.103:43137"), new("conn-2", "mac", "[fd7a:115c:a1e0::5]:43137")]);
            var text = await File.ReadAllTextAsync(Path.Combine(directory, RemoteConnectionFile.FileName));
            Check(text.Contains("\"version\":2", StringComparison.Ordinal) && !text.Contains("key", StringComparison.OrdinalIgnoreCase), "the file carries the version and no key: " + text);
            Check(RemoteConnectionFile.Read(directory).Select(c => c.Id).SequenceEqual(["conn-1", "conn-2"]), "read back in order");
            if (!OperatingSystem.IsWindows()) Check(File.GetUnixFileMode(Path.Combine(directory, RemoteConnectionFile.FileName)) == (UnixFileMode.UserRead | UnixFileMode.UserWrite), "the file is user-only");
            await File.WriteAllTextAsync(Path.Combine(directory, RemoteConnectionFile.FileName), "{\"version\":1,\"connections\":[{\"id\":\"old\",\"name\":\"x\",\"address\":\"100.1.2.3\"}]}");
            Check(RemoteConnectionFile.Read(directory).Count == 0, "a version 1 file (before 2026-09-28) is not read");
            await File.WriteAllTextAsync(Path.Combine(directory, RemoteConnectionFile.FileName), "{\"version\":2,\"connections\":[{\"id\":\"bad id\",\"name\":\"x\",\"address\":\"a\"},{\"id\":\"ok\",\"name\":\"y\",\"address\":\"b\"},{\"id\":\"ok\",\"name\":\"z\",\"address\":\"c\"}]}");
            Check(RemoteConnectionFile.Read(directory).Select(c => c.Name).SequenceEqual(["y"]), "bad and repeated ids are skipped");
            await File.WriteAllTextAsync(Path.Combine(directory, RemoteConnectionFile.FileName), "{\"version\":2,\"connections\":[null,{\"id\":\"no-address\",\"name\":\"x\"},{\"id\":\"ok\",\"name\":null,\"address\":\"b\",\"key\":\"" + Key + "\"}]}");
            var kept = RemoteConnectionFile.Read(directory);
            Check(kept.Count == 1 && kept[0] is { Id: "ok", Name: "", Address: "b:43137" }, "a null entry or one without an address is skipped alone, and a stray member is dropped");
            await RemoteConnectionFile.WriteAsync(directory, kept);
            Check(!(await File.ReadAllTextAsync(Path.Combine(directory, RemoteConnectionFile.FileName))).Contains(Key, StringComparison.Ordinal), "a key a hand edit left in the file is gone after the next write");
            await File.WriteAllTextAsync(Path.Combine(directory, RemoteConnectionFile.FileName), "{\"version\":2,\"connections\":[{\"id\":\"ok\",\"name\":\"x\",\"address\":\"b\"}");
            Check(RemoteConnectionFile.Read(directory).Count == 0, "a damaged file reads as no connections");
            await File.WriteAllBytesAsync(Path.Combine(directory, RemoteConnectionFile.FileName), new byte[256 * 1024 + 1]);
            Check(RemoteConnectionFile.Read(directory).Count == 0, "an oversize file is not read");
            Check(RemoteConnectionFile.Read(Path.Combine(directory, "missing")).Count == 0, "no file, no connections");

            var name = new string('a', 120) + "\U0001F600";
            await RemoteConnectionFile.WriteAsync(directory, [new("emoji", name, "b"), .. Enumerable.Range(0, 20).Select(i => new RemoteSavedConnection("c" + i, "n", "h" + i)), new("bad id", "x", "b")]);
            var back = RemoteConnectionFile.Read(directory);
            Check(back.Count == RemoteProtocol.MaximumConnections && back[0].Name == new string('a', 120), "a long name is cut at 120 characters as the Mac counts them and reads back, at most 16 connections are kept: " + back.Count);
            Check(!(await File.ReadAllTextAsync(Path.Combine(directory, RemoteConnectionFile.FileName))).Contains("bad id", StringComparison.Ordinal), "an entry the next read would drop is never written");
        }
        finally { Directory.Delete(directory, true); }
    }

    internal static Task KeysStayInTheVault()
    {
        var directory = Verification.Temp();
        try
        {
            var memory = new MemoryRemoteKeyVault();
            Check(memory.Save("conn-1", Key) && memory.Load("conn-1") == Key, "the memory vault keeps a key");
            Check(!memory.ToString().Contains(Key, StringComparison.Ordinal), "the memory vault never prints its keys");
            memory.Delete("conn-1"); Check(memory.Load("conn-1") is null, "and forgets it");
            var dpapi = new DpapiRemoteKeyVault(directory);
            Check(DpapiRemoteKeyVault.FileName("conn:1") is { Length: 68 } hashed && hashed.EndsWith(".bin", StringComparison.Ordinal) && hashed[..64].All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') && hashed != DpapiRemoteKeyVault.FileName("conn:2"), "a key file is named by the id's SHA-256 alone");
            Check(!DpapiRemoteKeyVault.Entropy("conn:1").AsSpan().SequenceEqual(DpapiRemoteKeyVault.Entropy("conn:2")), "each connection's blob is bound to its id");
            if (OperatingSystem.IsWindows())
            {
                Check(dpapi.Save("conn:1", Key), "DPAPI saves for this Windows user");
                var file = Directory.GetFiles(Path.Combine(directory, DpapiRemoteKeyVault.FolderName)).Single();
                Check(!File.ReadAllText(file).Contains(Key, StringComparison.Ordinal) && !Path.GetFileName(file).Contains("conn", StringComparison.Ordinal), "the file is ciphertext under a hashed name");
                Check(new DpapiRemoteKeyVault(directory).Load("conn:1") == Key, "a new vault reads it back");
                var foreign = Path.Combine(Path.GetDirectoryName(file)!, DpapiRemoteKeyVault.FileName("conn:2"));
                File.Copy(file, foreign); Check(dpapi.Load("conn:2") is null, "another connection's blob does not decrypt as this one's");
                File.Delete(foreign);
                File.WriteAllBytes(file, [1, 2, 3]); Check(dpapi.Load("conn:1") is null, "a damaged file reads as no key");
                File.WriteAllBytes(file, new byte[16_385]); Check(dpapi.Load("conn:1") is null, "an oversize file reads as no key");
                dpapi.Delete("conn:1"); Check(!File.Exists(file), "delete removes the file");
                Check(!dpapi.Save("conn:1", "short") && !File.Exists(file), "a key of the wrong shape is never written");
            }
            else Check(!dpapi.Save("conn-1", Key) && dpapi.Load("conn-1") is null && !Directory.Exists(Path.Combine(directory, "remote-keys")), "without DPAPI nothing is written and the key stays in memory");
            Check(!dpapi.ToString().Contains(directory, StringComparison.Ordinal), "the vault prints only its type");
            Check(!memory.Save("conn-1", "short"), "a key of the wrong shape is never saved");
        }
        finally { Directory.Delete(directory, true); }
        return Task.CompletedTask;
    }

    internal static Task AddressesParseWithTheDefaultPort()
    {
        Check(RemoteAddress.Parse("100.101.102.103") == new RemoteAddress("100.101.102.103", 43137), "a bare IP takes 43137");
        Check(RemoteAddress.Parse(" http://100.1.2.3:5000/ ").Display == "100.1.2.3:5000", "http:// and a trailing slash are dropped");
        Check(RemoteAddress.Parse("Studio-PC.tail1234.ts.net").Display == "studio-pc.tail1234.ts.net:43137", "MagicDNS names are lower-cased");
        Check(RemoteAddress.Parse("studio-pc:43200").Display == "studio-pc:43200", "a name with a port");
        Check(RemoteAddress.Parse("[fd7a:115c:a1e0::1]:43200").Display == "[fd7a:115c:a1e0::1]:43200", "bracketed IPv6 with a port");
        Check(RemoteAddress.Parse("fd7a:115c:a1e0:0::1").Display == "[fd7a:115c:a1e0::1]:43137", "IPv6 is canonical");
        Check(RemoteAddress.Parse("\t \u00a0HTTP://studio-pc\r\n\u0085\u2028").Display == "studio-pc:43137", "whitespace and newlines around the address are trimmed");
        Check(RemoteAddress.Parse("studio-pc:+5000").Port == 5000 && RemoteAddress.Parse("[fd7a::1]:+43200").Port == 43200, "a port reads as Swift's Int reads it: a leading + is taken");
        Check(JsonSerializer.Serialize(RemoteAddress.Parse("studio-pc"), Wire.Json) is var json && !json.Contains("display", StringComparison.OrdinalIgnoreCase), "Display is never serialized: " + json);
        foreach (var bad in new[] { "", "https://100.1.2.3", "ftp://x", "user@100.1.2.3", "100.1.2.3/path", "100.1.2.3?k=v", "100.1.2.3#x", "100.1.2.3:80", "100.1.2.3:99999", "100.1.2.3:abc", "[fd7a::1", "-host", "a..b", "host name", "fe80::1%en0", new string('a', 400),
                                    "studio\u0007pc", "studio-pc\u0000", "100.1.2.3:", "100.1.2.3:+", "100.1.2.3: 5000", "[fd7a::1]x", "[studio:pc]", "studio_pc", "\u0007\u0008",
                                    "\u0007studio-pc", "studio-pc\u001b", "\u009bstudio-pc", "studio-pc:-1", "studio-pc:99999999999" })
        {
            try { RemoteAddress.Parse(bad); throw new InvalidOperationException("accepted " + bad); } catch (ArgumentException) { }
        }
        foreach (var (input, words) in new[] { ("100.1.2.3:80", "badPort"), ("studio-pc:-1", "badPort"), ("[fd7a::1]:-0", "badPort"), ("studio-pc:99999999999", "badAddress"), ("user@x", "badAddress"), ("\u0007studio-pc", "badAddress") })
        {
            var refused = false;
            try { RemoteAddress.Parse(input); }
            catch (ArgumentException ex) { refused = true; Check(ex.Message == Locale.Get("remote.connect.error." + words), input + " is refused with the " + words + " words: " + ex.Message); }
            if (!refused) Check(false, input + " was accepted");
        }
        return Task.CompletedTask;
    }

    internal static async Task SavedAddressesReadAsTheMacReadsThem()
    {
        var directory = Verification.Temp();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, RemoteConnectionFile.FileName), "{\"version\":2,\"connections\":["
                + "{\"id\":\"a\",\"name\":\"a\",\"address\":\"Studio-PC\"},"
                + "{\"id\":\"b\",\"name\":\"b\",\"address\":\"http://studio-pc:43137/\"},"
                + "{\"id\":\"c\",\"name\":\"c\",\"address\":\"user@100.1.2.3\"},"
                + "{\"id\":\"d\",\"name\":\"d\",\"address\":\"fd7a:115c:a1e0:0::5\"},"
                + "{\"id\":\"e\",\"name\":\"e\",\"address\":\"[fd7a:115c:a1e0::5]:43137\"},"
                + "{\"id\":\"f\",\"name\":\"f\",\"address\":\"100.101.102.103:80\"},"
                + "{\"id\":\"g\",\"name\":\"g\",\"address\":\"100.101.102.103\"}]}");
            var read = RemoteConnectionFile.Read(directory);
            Check(read.Select(c => c.Id + "=" + c.Address).SequenceEqual(["a=studio-pc:43137", "d=[fd7a:115c:a1e0::5]:43137", "g=100.101.102.103:43137"]),
                "saved addresses are parsed, kept in their shown form, and one that does not parse or repeats an earlier one is dropped: " + string.Join(", ", read.Select(c => c.Id + "=" + c.Address)));
            await File.WriteAllTextAsync(Path.Combine(directory, RemoteConnectionFile.FileName), "{\"version\":2,\"connections\":["
                + string.Join(",", Enumerable.Range(0, 16).Select(i => "{\"id\":\"" + (i < 2 ? "bad id" : "p" + i) + "\",\"name\":\"n\",\"address\":\"h" + i + "\"}"))
                + ",{\"id\":\"late\",\"name\":\"n\",\"address\":\"late\"}]}");
            var first = RemoteConnectionFile.Read(directory);
            Check(first.Count == 14 && first[0].Id == "p2" && first.All(c => c.Id != "late"), "only the file's first 16 entries are read, as on the Mac, so a 17th never fills a dropped one's place: " + first.Count);
            await RemoteConnectionFile.WriteAsync(directory, [new("bad id", "x", "h0"), new("bad id", "x", "h1"), .. Enumerable.Range(2, 16).Select(i => new RemoteSavedConnection("p" + i, "n", "h" + i))]);
            Check(RemoteConnectionFile.Read(directory).Count == RemoteProtocol.MaximumConnections, "a write keeps 16 good entries, whatever it skipped first");
            await RemoteConnectionFile.WriteAsync(directory, [new("x", "x", "100.101.102.103"), new("y", "y", "100.101.102.103:43137"), new("z", "z", "not an address/")]);
            Check(RemoteConnectionFile.Read(directory).Select(c => c.Id + "=" + c.Address).SequenceEqual(["x=100.101.102.103:43137"]), "a repeated or unparsable address is never written");
        }
        finally { Directory.Delete(directory, true); }
    }

    internal static Task OnlyTailscalePeersAndLoopbackWhenInjected()
    {
        Check(!RemotePeerPolicy.Allowed("127.0.0.1") && !RemotePeerPolicy.Allowed("::1"), "loopback is never a peer by default");
        Check(RemotePeerPolicy.Allowed("127.0.0.1", allowLoopback: true) && RemotePeerPolicy.Allowed("::1", allowLoopback: true) && !RemotePeerPolicy.Allowed("192.168.0.2", allowLoopback: true), "loopback only when injected");
        Check(RemotePeerPolicy.Allowed("100.90.1.1") && RemotePeerPolicy.Allowed("fd7a:115c:a1e0::5%17") && !RemotePeerPolicy.Allowed("fe80::1%17"), "the address decides, not its scope");
        Check(RemotePeerPolicy.Allowed(System.Net.IPAddress.Parse("::ffff:100.90.1.1")) && !RemotePeerPolicy.Allowed(System.Net.IPAddress.Parse("10.0.0.1")), "IPv4-mapped peers read as IPv4");
        Check(!RemotePeerPolicy.Allowed("100.63.255.255") && !RemotePeerPolicy.Allowed("100.128.0.1") && !RemotePeerPolicy.Allowed("fd7a:115c:a1e1::1") && !RemotePeerPolicy.Allowed("192.168.0.2") && !RemotePeerPolicy.Allowed("studio-pc") && !RemotePeerPolicy.Allowed(""), "everything outside Tailscale is refused");
        foreach (var loose in new[] { "127.1", "2130706433", "0x7f.1", "0177.0.0.1", "::ffff:127.0.0.1", "0:0:0:0:0:0:0:1", "127.0.0.256", "127.0.0.1.1" })
            Check(!RemotePeerPolicy.Allowed(loose, allowLoopback: true), "only the Mac's loopback spellings count when injected: " + loose);
        Check(RemotePeerPolicy.Allowed("127.9.8.7", allowLoopback: true) && RemotePeerPolicy.Allowed("[::1]", allowLoopback: true) && RemotePeerPolicy.Allowed("::1%1", allowLoopback: true), "any 127/8 address, and ::1 in brackets or with a scope");
        Check(!RemotePeerPolicy.Allowed(System.Net.IPAddress.Loopback) && RemotePeerPolicy.Allowed(System.Net.IPAddress.IPv6Loopback, allowLoopback: true) && RemotePeerPolicy.Allowed(System.Net.IPAddress.Parse("::ffff:127.0.0.1"), allowLoopback: true), "loopback as an IPAddress, also only when injected");
        Check(RemoteRoute.SourceAddressToward("127.0.0.1") == "127.0.0.1", "the route to loopback leaves from loopback, with nothing sent");
        Check(!RemoteRoute.ThroughTailscale("127.0.0.1") && RemoteRoute.SourceAddressToward("not an ip") is null, "loopback is not the tunnel; garbage has no route");
        Check(!RemoteRoute.ThroughTailscale("127.0.0.1", () => throw new AdaptersUnreadableException(new System.Net.NetworkInformation.NetworkInformationException())), "adapters that cannot be read are no tunnel");
        Check(!RemoteRoute.ThroughTailscale("127.0.0.1", () => ["100.101.102.103", "fd7a:115c:a1e0::5"]), "a source address that is not one of this PC's Tailscale addresses is no tunnel");
        Check(RemoteRoute.ThroughTailscale("127.0.0.1", () => ["100.101.102.103", "127.0.0.1"]), "a source address among this PC's Tailscale addresses is the tunnel");
        Check(!RemoteRoute.ThroughTailscale("not an ip", () => ["127.0.0.1"]), "no route, no tunnel");
        Check(RemoteRoute.SourceAddressToward("studio-pc") is null && RemoteRoute.SourceAddressToward("") is null && !RemoteRoute.ThroughTailscale("not an ip"), "a name is never resolved here: only an IP has a route");
        return Task.CompletedTask;
    }

    internal static RemoteTarget Loopback(int port) => new(new RemoteAddress("127.0.0.1", port), "127.0.0.1", port, allowLoopback: true);

    internal static async Task OneAuthenticatedRequestFollowsNoRedirectAndReadsAtMostTwoMebibytes()
    {
        await using var elsewhere = new RemoteFakeHost(_ => RemoteFakeHost.Fixture("info.json"));
        await using var host = new RemoteFakeHost(request => request.Target switch
        {
            "/v2/info" => RemoteFakeHost.Fixture("info.json"),
            "/v2/moved" => new(302, "{}", new Dictionary<string, string> { ["Location"] = $"http://127.0.0.1:{elsewhere.Port}/v2/info" }),
            "/v2/plain" => new(200, "{}", VersionHeader: false),
            "/v2/huge" => new(200, "\"" + new string('x', RemoteProtocol.MaximumResponseBytes) + "\""),
            "/v2/refused" => RemoteFakeHost.Fixture("error.json", 401),
            "/v2/busy" => new(503, "{\"protocol\":2,\"error\":\"busy\"}"),
            "/v2/slow" => new(200, "{}", Delay: TimeSpan.FromSeconds(3)),
            "/v2/huge-unsized" => new(200, "\"" + new string('x', RemoteProtocol.MaximumResponseBytes) + "\"", OmitLength: true),
            "/v2/stalled" => new(200, "{}", BodyDelay: TimeSpan.FromSeconds(3)),
            "/v2/twice" => new(200, "{}", new Dictionary<string, string> { ["X-Mighty-Remote-Version"] = "2" }),
            "/v2/lone" => new(401, "{\"protocol\":2,\"error\":\"\\uD83D\"}"),
            _ => new(404, "{\"protocol\":2,\"error\":\"no\"}"),
        });
        var target = Loopback(host.Port);
        var info = await RemoteTransport.RequestAsync(target, Key, HttpMethod.Get, "/v2/info");
        Check(RemoteWireFixtureVerification.Same(System.Text.Json.Nodes.JsonNode.Parse(info), RemoteWireFixtureVerification.Node("info.json")), "the body arrives whole");
        var seen = host.Seen.Last();
        Check(seen.Headers["authorization"] == "Bearer " + Key && seen.Headers["x-mighty-remote-version"] == "2" && !seen.Headers.ContainsKey("origin") && !seen.Headers.ContainsKey("cookie"), "the key rides in Authorization only, with the version header");
        Check(!seen.Target.Contains(Key, StringComparison.Ordinal), "the key is never in the URL");
        await Reject(() => RemoteTransport.RequestAsync(target, Key, HttpMethod.Get, "/v2/moved"), "a redirect is an error", ex => ex is InvalidDataException);
        Check(elsewhere.Seen.IsEmpty, "a redirect is never followed");
        await Reject(() => RemoteTransport.RequestAsync(target, Key, HttpMethod.Get, "/v2/plain"), "an answer without the version header is not Mighty's", ex => ex is InvalidDataException && ex.Message == Locale.Get("remote.connect.error.notMighty"));
        await Reject(() => RemoteTransport.RequestAsync(target, Key, HttpMethod.Get, "/v2/huge"), "over 2 MiB is refused", ex => ex.Message == Locale.Get("remote.connect.error.responseTooLarge"));
        await Reject(() => RemoteTransport.RequestAsync(target, Key, HttpMethod.Get, "/v2/huge-unsized"), "over 2 MiB with no length is refused while reading", ex => ex is InvalidDataException && ex.Message == Locale.Get("remote.connect.error.responseTooLarge"));
        await Reject(() => RemoteTransport.RequestAsync(target, Key, HttpMethod.Get, "/v2/twice"), "a repeated version header is not Mighty's", ex => ex is InvalidDataException && ex.Message == Locale.Get("remote.connect.error.notMighty"));
        await Reject(() => RemoteTransport.RequestAsync(target, Key, HttpMethod.Get, "/v2/refused"), "an error status carries the host's message", ex => ex is RemoteHttpFailure { Status: 401, Transient: false } f && f.Message == "The key does not match.");
        await Reject(() => RemoteTransport.RequestAsync(target, Key, HttpMethod.Get, "/v2/busy"), "503 is a passing failure", ex => RemoteTransport.Transient(ex));
        await Reject(() => RemoteTransport.RequestAsync(target, Key, HttpMethod.Get, "/v2/lone"), "an error text that is a lone surrogate escape reads as the status, as the Mac's try? does",
            ex => ex is RemoteHttpFailure { Status: 401 } f && f.Message == Locale.Get("remote.connect.error.unexpectedStatus", new Dictionary<string, string> { ["status"] = "401" }));
        await Reject(() => RemoteTransport.RequestAsync(target, Key, HttpMethod.Get, "/v2/slow", timeout: TimeSpan.FromMilliseconds(400)), "a slow host times out", ex => ex is TimeoutException && RemoteTransport.Transient(ex) && ex.Message == RemoteWindowsStrings.Timeout);
        await Reject(() => RemoteTransport.RequestAsync(target, Key, HttpMethod.Get, "/v2/stalled", timeout: TimeSpan.FromMilliseconds(400)), "a body that stalls after the head times out", ex => ex is TimeoutException && ex.Message == RemoteWindowsStrings.Timeout);
        _ = await RemoteTransport.RequestAsync(target, Key, HttpMethod.Get, "/v2/info", headers: new Dictionary<string, string> { [RemoteProtocol.GraphHeader] = RemoteProtocol.GraphRuns });
        Check(host.Seen.Last().Headers[RemoteProtocol.GraphHeader] == RemoteProtocol.GraphRuns, "the graph opt-in header goes out");
        var before = host.Seen.Count;
        await Reject(() => RemoteTransport.RequestAsync(target, Key, HttpMethod.Get, "/v2/info", headers: new Dictionary<string, string> { ["Origin"] = "http://example.invalid" }), "only allow-listed extra headers", ex => ex is ArgumentException);
        await Reject(() => RemoteTransport.RequestAsync(target, Key, HttpMethod.Get, "/v2/info", headers: new Dictionary<string, string> { ["Authorization"] = "Bearer " + Key }), "the transport's own headers are never overridden", ex => ex is ArgumentException);
        await Reject(() => RemoteTransport.RequestAsync(new RemoteTarget(new("127.0.0.1", host.Port), "127.0.0.1", host.Port), Key, HttpMethod.Get, "/v2/info"), "a loopback target without the injected flag is refused", ex => ex.Message == Locale.Get("remote.connect.error.notTailscale"));
        await Reject(() => RemoteTransport.RequestAsync(new RemoteTarget(new("192.168.1.5", 43137), "192.168.1.5", 43137, allowLoopback: true), Key, HttpMethod.Get, "/v2/info"), "a hand-built LAN target never gets the key", ex => ex.Message == Locale.Get("remote.connect.error.notTailscale"));
        await Reject(() => RemoteTransport.RequestAsync(target, "short", HttpMethod.Get, "/v2/info"), "a malformed key is refused before sending");
        await Reject(() => RemoteTransport.RequestAsync(target, Key, HttpMethod.Get, "/v1/info"), "only /v2/ paths");
        await Reject(() => RemoteTransport.RequestAsync(target, Key, HttpMethod.Get, "/v2/info?x=<script>"), "only plain path characters");
        await Reject(() => RemoteTransport.RequestAsync(target, Key, HttpMethod.Get, "/v2/../v1/info"), "a dot segment never leaves /v2/", ex => ex.Message == Locale.Get("remote.connect.error.badRequest"));
        Check(host.Seen.Count == before, "nothing refused locally reaches the host");
        Check(!RemoteTransport.Transient(new InvalidDataException("x")) && !RemoteTransport.Transient(new RemoteHttpFailure(400, "x")), "bad answers are not passing failures");
    }

    internal static async Task HostsResolveOnlyToTailscale()
    {
        var injected = await RemoteTransport.ResolveAsync(new("127.0.0.1", 43137), allowLoopback: true);
        Check(injected.Ip == "127.0.0.1" && injected.AllowLoopback, "loopback when injected, and the target keeps the flag");
        Check(!(await RemoteTransport.ResolveAsync(new("100.101.102.103", 43137), allowLoopback: false)).AllowLoopback, "a target resolved without the flag never gains it");
        await Reject(() => RemoteTransport.ResolveAsync(new(new string('a', 300), 43137), allowLoopback: false), "a name the resolver refuses reads as unresolved, never as a .NET message", ex => ex is InvalidOperationException && ex.Message == Locale.Get("remote.connect.error.unresolved"));
        Check((await RemoteTransport.ResolveAsync(new("localhost", 43137), allowLoopback: true)).Ip == "127.0.0.1", "a name resolves once, IPv4 first");
        Check((await RemoteTransport.ResolveAsync(new("100.101.102.103", 43137), allowLoopback: false)).Ip == "100.101.102.103", "a Tailscale literal needs no lookup");
        await Reject(() => RemoteTransport.ResolveAsync(new("127.0.0.1", 43137), allowLoopback: false), "loopback is not Tailscale", ex => ex.Message == Locale.Get("remote.connect.error.notTailscale"));
        await Reject(() => RemoteTransport.ResolveAsync(new("192.168.1.5", 43137), allowLoopback: true), "a LAN address is refused", ex => ex.Message == Locale.Get("remote.connect.error.notTailscale"));
    }

    internal sealed class Collected
    {
        private readonly List<RunEvent> events = []; private readonly List<ToolPermissionRequest> permissions = [];
        internal void Event(RunEvent value) { lock (events) events.Add(value); }
        internal void Permission(ToolPermissionRequest value) { lock (events) permissions.Add(value); }
        internal RunEvent[] Events { get { lock (events) return [.. events]; } }
        internal ToolPermissionRequest[] Permissions { get { lock (events) return [.. permissions]; } }
    }

    internal static async Task<(StateStore Store, string Directory)> ClientStore()
    {
        var directory = Verification.Temp(); var store = new StateStore(directory); await store.LoadAsync(); return (store, directory);
    }

    internal static async Task ConnectionsAreSavedWithoutTheirKeyAndReadItOnFirstUse()
    {
        await using var host = new RemoteFakeHost(request => request.Target == "/v2/info" ? request.Headers["authorization"] == "Bearer " + Key ? RemoteFakeHost.Fixture("info.json") : RemoteFakeHost.Fixture("error.json", 401) : new(404, "{}"));
        var (store, directory) = await ClientStore(); var vault = new MemoryRemoteKeyVault(); var seen = new Collected();
        try
        {
            var options = new RemoteClientOptions { AllowLoopbackForTests = true };
            await using var client = new RemoteClientService(store, directory, vault, seen.Event, seen.Permission, options);
            var info = await client.ConnectAsync("", "127.0.0.1:" + host.Port, Key);
            Check(info is { Status: RemoteConnectionStatus.Connected, Name: "studio-pc", HostName: "studio-pc", Runtime.Platform: "win32" } && info.Shared.Count == 2, "connected with the host's name, OS and shared workspaces");
            Check(info.Detail == RemoteWindowsStrings.ConnectedSaved && vault.Load(info.Id) == Key, "the key went to the vault");
            var file = await File.ReadAllTextAsync(Path.Combine(directory, RemoteConnectionFile.FileName));
            Check(!file.Contains(Key, StringComparison.Ordinal) && file.Contains("127.0.0.1:" + host.Port, StringComparison.Ordinal), "the file names the address, never the key");
            // A new key for the same address replaces the saved one and keeps the connection.
            Check((await client.ConnectAsync("Studio", "127.0.0.1:" + host.Port, Key)).Id == info.Id && client.List().Count == 1, "same address, same connection");
            await Reject(() => client.ConnectAsync("", "127.0.0.1:" + host.Port, "short"), "a malformed key is refused", ex => ex.Message == Locale.Get("remote.connect.error.badKey"));
            await Reject(() => client.ConnectAsync("", "127.0.0.1:" + host.Port, new string('x', 43)), "a wrong key is the host's 401", ex => ex is RemoteHttpFailure { Status: 401 });
            Check(client.List().Count == 1, "a refused connect saves nothing");

            var workspace = client.OpenWorkspace(info.Id, "ws-project");
            Check(workspace.Remote is { ConnectionId: var c, WorkspaceId: "ws-project", HostName: "studio-pc" } && c == info.Id && workspace.Path == "C:\\work\\project", "the host's workspace is listed as remote");
            Check(client.OpenWorkspace(info.Id, "ws-project").Id == workspace.Id, "opening it again lists the same one");
            try { client.OpenWorkspace(info.Id, "not-shared"); throw new InvalidOperationException("an unshared workspace opened"); } catch (InvalidOperationException ex) when (ex.Message == Locale.Get("remote.workspace.error.notShared")) { }

            await client.DisconnectAsync(info.Id);
            Check(client.Connection(info.Id)?.Status == RemoteConnectionStatus.Disconnected, "disconnect keeps the connection, not connected");
            Check((await client.EnsureConnectedAsync(info.Id)).Status == RemoteConnectionStatus.Connected, "a send reconnects");

            // After a restart the connection is saved, its key unread until first use.
            await using var later = new RemoteClientService(store, directory, vault, seen.Event, seen.Permission, options);
            Check(later.List().Single() is { Status: RemoteConnectionStatus.Saved } saved && saved.Detail == Locale.Get("remote.connect.detail.saved"), "listed as saved after a restart");
            Check((await later.RefreshAsync(info.Id)).Status == RemoteConnectionStatus.Connected, "refresh reads the key from the vault and connects");
            await later.RemoveAsync(info.Id);
            Check(later.List().Count == 0 && vault.Load(info.Id) is null && RemoteConnectionFile.Read(directory).Count == 0, "remove forgets the connection and its key");
        }
        finally { Directory.Delete(directory, true); }
    }

    internal static async Task NoConnectionLeavesThisPCsTunnel()
    {
        await using var host = new RemoteFakeHost(_ => RemoteFakeHost.Fixture("info.json"));
        var (store, directory) = await ClientStore(); var seen = new Collected();
        try
        {
            var tunnel = true;
            await using var client = new RemoteClientService(store, directory, new MemoryRemoteKeyVault(), seen.Event, seen.Permission, new() { AllowLoopbackForTests = true, RouteCheck = _ => tunnel });
            var info = await client.ConnectAsync("", "127.0.0.1:" + host.Port, Key);
            tunnel = false; var before = host.Seen.Count;
            await Reject(() => client.EnsureConnectedAsync(info.Id), "a route outside the tunnel ends the connection", ex => ex.Message == Locale.Get("remote.connect.error.notViaTailscale"));
            Check(client.Connection(info.Id)?.Status == RemoteConnectionStatus.Disconnected && host.Seen.Count == before, "disconnected, and nothing was sent");
            await Reject(() => client.RefreshAsync(info.Id), "refresh refuses the same route", ex => ex.Message == Locale.Get("remote.connect.error.notViaTailscale"));
            Check(host.Seen.Count == before, "still nothing sent");
            await using var offline = new RemoteClientService(store, directory, new MemoryRemoteKeyVault(), seen.Event, seen.Permission, new() { LocalAddresses = () => [] });
            await Reject(() => offline.ConnectAsync("", "100.101.102.103", Key), "no Tailscale on this PC, no connect", ex => ex.Message == RemoteWindowsStrings.LocalTailscaleOff);

            // A route check that throws is no tunnel either, never a passing failure, and nothing is sent.
            await using var broken = new RemoteClientService(store, directory, new MemoryRemoteKeyVault(), seen.Event, seen.Permission, new() { AllowLoopbackForTests = true, RouteCheck = _ => throw new AdaptersUnreadableException(new IOException("x")) });
            await Reject(() => broken.ConnectAsync("", "127.0.0.1:" + host.Port, Key), "a route check that throws refuses the connect", ex => ex.Message == Locale.Get("remote.connect.error.notViaTailscale") && !RemoteTransport.Transient(ex));
            Check(host.Seen.Count == before, "and nothing was sent");
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>A vault that cannot keep anything beyond this launch (DPAPI refused): Save is false and Delete is counted.</summary>
    private sealed class RefusingVault : IRemoteKeyVault
    {
        internal readonly List<string> Deleted = [];
        public string? Load(string id) => null;
        public bool Save(string id, string key) => false;
        public void Delete(string id) { lock (Deleted) Deleted.Add(id); }
    }

    internal static async Task TheHostIsReadStrictlyAndAKeyTheVaultRefusesStaysInMemory()
    {
        var renamed = RemoteWireFixtureVerification.Node("info.json").ToJsonString().Replace("\"hostName\"", "\"HostName\"", StringComparison.Ordinal);
        await using var loose = new RemoteFakeHost(_ => new(200, renamed));
        await using var host = new RemoteFakeHost(_ => RemoteFakeHost.Fixture("info.json"));
        var (store, directory) = await ClientStore(); var seen = new Collected();
        try
        {
            var options = new RemoteClientOptions { AllowLoopbackForTests = true };
            var refusing = new RefusingVault();
            await using var client = new RemoteClientService(store, directory, refusing, seen.Event, seen.Permission, options);
            await Reject(() => client.ConnectAsync("", "127.0.0.1:" + loose.Port, Key), "an /info whose member names differ only in case is not Mighty's", ex => ex.Message == Locale.Get("remote.connect.error.notMighty"));
            Check(client.List().Count == 0, "and nothing is saved");

            var info = await client.ConnectAsync("", "127.0.0.1:" + host.Port, Key);
            Check(info.Detail == RemoteWindowsStrings.ConnectedMemory && refusing.Deleted.Count == 0, "a key the vault refuses is kept for this launch only, and any key it kept before stays, as on the Mac");
            var file = await File.ReadAllTextAsync(Path.Combine(directory, RemoteConnectionFile.FileName));
            Check(!file.Contains(Key, StringComparison.Ordinal), "the file never holds the key, even when the vault refused it");
            await client.DisconnectAsync(info.Id);
            Check((await client.EnsureConnectedAsync(info.Id)).Status == RemoteConnectionStatus.Connected, "the key in memory reconnects in this launch");
            await using var later = new RemoteClientService(store, directory, refusing, seen.Event, seen.Permission, options);
            await Reject(() => later.RefreshAsync(info.Id), "after a restart the key is gone", ex => ex.Message == Locale.Get("remote.connect.error.keyMissing"));
            Check(later.Connection(info.Id)?.Status == RemoteConnectionStatus.Disconnected, "and the connection says so");

            // A hand edit that repeats an address, or names none that parses, lists one connection per address.
            await File.WriteAllTextAsync(Path.Combine(directory, RemoteConnectionFile.FileName), "{\"version\":2,\"connections\":[{\"id\":\"a\",\"name\":\"a\",\"address\":\"Studio-PC\"},{\"id\":\"b\",\"name\":\"b\",\"address\":\"studio-pc:43137\"},{\"id\":\"c\",\"name\":\"c\",\"address\":\"user@x\"}]}");
            await using var edited = new RemoteClientService(store, directory, new MemoryRemoteKeyVault(), seen.Event, seen.Permission, options);
            Check(edited.List().Select(c => c.Id + "=" + c.Address).SequenceEqual(["a=studio-pc:43137"]), "saved connections load parsed and without repeats");
        }
        finally { Directory.Delete(directory, true); }
    }

    internal static async Task TextIsCutAtWholeCharacters()
    {
        const string emoji = "\U0001F600", family = "\U0001F469‍\U0001F469‍\U0001F467";
        Check(RemoteText.Cut(new string('a', 299) + emoji + "bbb") == new string('a', 299) + emoji, "an emoji across units 299 and 300 is the 300th character, kept whole");
        Check(RemoteText.Cut(new string('a', 299) + family + "b") == new string('a', 299) + family && RemoteText.Count(new string('a', 119) + family) == 120, "a joined emoji is one character, as the Mac counts it");
        Check(RemoteText.Cut("abc\uD83D") == "abc" && RemoteText.Cut("\uDE00x\uD83D") == "x" && RemoteText.Cut("ok") == "ok" && RemoteText.Cut(null) == "", "a lone surrogate is dropped at any length");
        Check(RemoteText.Cut("abc", 0) == "" && RemoteText.Cut("abc", -1) == "" && RemoteText.Cut("e\u0301x", 1) == "e\u0301" && RemoteText.Cut("a\uDE00\uD83D\uDE00b", 2) == "a\U0001F600", "no characters for max 0 or less; a combining mark stays with its letter; a pair after a lone low surrogate is one character");
        Check(RemoteText.Count(new string('a', 500), 121) == 121 && RemoteText.Count("a\uD83Db") == 2, "a count stops at its limit and skips lone surrogates");

        var error = new string('a', 299) + emoji + new string('b', 20); var expected = new string('a', 299) + emoji;
        var refuse = false;
        await using var host = new RemoteFakeHost(_ => refuse ? new(401, JsonSerializer.Serialize(new { protocol = 2, error })) : RemoteFakeHost.Fixture("info.json"));
        refuse = true;
        await Reject(() => RemoteTransport.RequestAsync(Loopback(host.Port), Key, HttpMethod.Get, "/v2/info"), "the host's error is cut at 300 characters", ex => ex is RemoteHttpFailure { Status: 401 } f && f.Message == expected);
        refuse = false;
        var (store, directory) = await ClientStore(); var seen = new Collected();
        try
        {
            var options = new RemoteClientOptions { AllowLoopbackForTests = true };
            await using var client = new RemoteClientService(store, directory, new MemoryRemoteKeyVault(), seen.Event, seen.Permission, options);
            var info = await client.ConnectAsync(new string('a', 119) + family, "127.0.0.1:" + host.Port, Key);
            Check(info.Name == new string('a', 119) + family, "a name of 120 characters is taken whole");
            await Reject(() => client.ConnectAsync(new string('a', 120) + family, "127.0.0.1:" + host.Port, Key), "a name of 121 characters is refused", ex => ex.Message == Locale.Get("remote.connect.error.badKey"));
            Check((await client.ConnectAsync("Stu\u0007dio\uD83D", "127.0.0.1:" + host.Port, Key)).Name == "Studio", "a name is cleaned as the file cleans it");
            await using (var later = new RemoteClientService(store, directory, new MemoryRemoteKeyVault(), seen.Event, seen.Permission, options))
                Check(later.List().Single().Name == "Studio", "and reads the same after a restart");
            refuse = true;
            await Reject(() => client.RefreshAsync(info.Id), "a refused refresh", ex => ex is RemoteHttpFailure);
            Check(client.Connection(info.Id) is { Status: RemoteConnectionStatus.Disconnected, Detail: var detail } && detail == expected, "the connection's error is cut at whole characters too");
            // A failure whose own message was never cut (here a check's LocalAddresses that throws it whole) is cut by the connection itself.
            refuse = false; var adaptersFail = false;
            var watched = new RemoteClientOptions { AllowLoopbackForTests = true, LocalAddresses = () => adaptersFail ? throw new InvalidOperationException(error) : ["100.101.102.103"] };
            await using var uncut = new RemoteClientService(store, directory, new MemoryRemoteKeyVault(), seen.Event, seen.Permission, watched);
            var second = await uncut.ConnectAsync("", "127.0.0.1:" + host.Port, Key);
            adaptersFail = true;
            await Reject(() => uncut.RefreshAsync(second.Id), "a refresh that fails with a long message", ex => ex.Message == error);
            Check(uncut.Connection(second.Id)?.Detail == expected, "the connection cuts a long message at whole characters: " + uncut.Connection(second.Id)?.Detail?.Length);
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>Holds a host's /v2/info until released, once closed, and says when one arrived.</summary>
    private sealed class Gate(bool closed = false)
    {
        private volatile bool closed = closed;
        internal readonly TaskCompletionSource Arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Close() => closed = true;
        internal Task WaitArrived() => Arrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        internal async Task<RemoteFakeHost.Reply> Answer(RemoteFakeHost.Request request)
        {
            if (closed && request.Target == "/v2/info") { Arrived.TrySetResult(); await Released.Task; }
            return RemoteFakeHost.Fixture("info.json");
        }
    }

    /// <summary>A memory vault that lists the ids it holds a key for.</summary>
    private sealed class ListingVault : IRemoteKeyVault
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> keys = new(StringComparer.Ordinal);
        internal HashSet<string> Ids => [.. keys.Keys];
        public string? Load(string id) => keys.TryGetValue(id, out var key) ? key : null;
        public bool Save(string id, string key) { if (!RemoteWorkspaceValidation.Key(key)) return false; keys[id] = key; return true; }
        public void Delete(string id) => keys.TryRemove(id, out _);
    }

    /// <summary>A listing vault whose Delete, once held, waits until released and says when it started.</summary>
    private sealed class HeldDeleteVault : IRemoteKeyVault
    {
        private readonly ListingVault inner = new();
        private volatile bool hold;
        internal readonly ManualResetEventSlim Entered = new(), Released = new();
        internal HashSet<string> Ids => inner.Ids;
        internal void Hold() => hold = true;
        public string? Load(string id) => inner.Load(id);
        public bool Save(string id, string key) => inner.Save(id, key);
        public void Delete(string id) { if (hold) { Entered.Set(); Released.Wait(TimeSpan.FromSeconds(10)); } inner.Delete(id); }
    }

    private static async Task<object> Outcome<T>(Task<T> task) { try { return (await task)!; } catch (Exception ex) { return ex; } }

    internal static async Task OverlappingChangesKeepTheNewerState()
    {
        var options = new RemoteClientOptions { AllowLoopbackForTests = true }; var seen = new Collected();
        // (a) Two connects that both passed the first count with 15 saved: one is the 16th, the other is refused and keeps no key.
        {
            var (store, directory) = await ClientStore();
            try
            {
                await RemoteConnectionFile.WriteAsync(directory, Enumerable.Range(0, 15).Select(i => new RemoteSavedConnection("saved-" + i, "n", "h" + i)));
                Gate first = new(closed: true), second = new(closed: true);
                await using var one = new RemoteFakeHost(first.Answer);
                await using var two = new RemoteFakeHost(second.Answer);
                var vault = new ListingVault();
                await using var client = new RemoteClientService(store, directory, vault, seen.Event, seen.Permission, options);
                var a = Outcome(client.ConnectAsync("", "127.0.0.1:" + one.Port, Key)); var b = Outcome(client.ConnectAsync("", "127.0.0.1:" + two.Port, Key));
                await first.WaitArrived(); await second.WaitArrived();
                first.Released.SetResult(); second.Released.SetResult();
                var outcomes = await Task.WhenAll(a, b);
                var connected = outcomes.OfType<RemoteConnectionInfo>().ToList();
                var tooMany = Locale.Get("remote.connect.error.tooMany", new Dictionary<string, string> { ["count"] = "16" });
                Check(connected.Count == 1 && outcomes.OfType<Exception>().Single().Message == tooMany, "exactly one connect is the 16th and the other is refused as too many: " + string.Join(", ", outcomes.Select(o => o is Exception e ? e.Message : "connected")));
                Check(client.List().Count == RemoteProtocol.MaximumConnections && RemoteConnectionFile.Read(directory).Count == RemoteProtocol.MaximumConnections, "16 listed and 16 saved");
                Check(vault.Ids.SetEquals([connected[0].Id]), "the refused connect left no key in the vault");
            }
            finally { Directory.Delete(directory, true); }
        }
        // (b) A disconnect while a refresh waits on the host: the refresh is refused as changed and the disconnect stands.
        {
            var (store, directory) = await ClientStore();
            try
            {
                var gate = new Gate();
                await using var host = new RemoteFakeHost(gate.Answer);
                await using var client = new RemoteClientService(store, directory, new ListingVault(), seen.Event, seen.Permission, options);
                var info = await client.ConnectAsync("", "127.0.0.1:" + host.Port, Key);
                gate.Close();
                var refresh = client.RefreshAsync(info.Id);
                await gate.WaitArrived();
                await client.DisconnectAsync(info.Id);
                gate.Released.SetResult();
                await Reject(() => refresh, "a refresh that outlived a disconnect is refused", ex => ex.Message == Locale.Get("remote.connect.error.changed"));
                Check(client.Connection(info.Id) is { Status: RemoteConnectionStatus.Disconnected, Detail: var detail } && detail == Locale.Get("remote.connect.detail.disconnected"),
                    "the connection stays disconnected with the disconnect's words, never failed over: " + client.Connection(info.Id)?.Detail);
            }
            finally { Directory.Delete(directory, true); }
        }
        // (c) A remove while a new key's connect waits on the host: the connect lands after it and lists the connection again (as on the Mac), with its key.
        {
            var (store, directory) = await ClientStore();
            try
            {
                var gate = new Gate(); var vault = new ListingVault(); var newer = new string('m', 43);
                await using var host = new RemoteFakeHost(gate.Answer);
                await using var client = new RemoteClientService(store, directory, vault, seen.Event, seen.Permission, options);
                var info = await client.ConnectAsync("", "127.0.0.1:" + host.Port, Key);
                gate.Close();
                var again = client.ConnectAsync("", "127.0.0.1:" + host.Port, newer);
                await gate.WaitArrived();
                await client.RemoveAsync(info.Id);
                Check(client.List().Count == 0 && vault.Ids.Count == 0, "removed, key and all");
                gate.Released.SetResult();
                var back = await again;
                Check(back.Id == info.Id && client.List().Single().Id == info.Id && RemoteConnectionFile.Read(directory).Single().Id == info.Id, "the connect that landed after the remove lists the connection again");
                Check(vault.Load(info.Id) == newer && vault.Ids.SetEquals([info.Id]), "with its new key in the vault, and no key for anything unlisted");
            }
            finally { Directory.Delete(directory, true); }
        }
        // (e) A remove holding its key delete while a connect for the same address comes back: the connect's save waits for the
        // delete, so the connection is listed again with its new key, never with the key deleted after it was saved.
        {
            var (store, directory) = await ClientStore();
            try
            {
                var gate = new Gate(); var vault = new HeldDeleteVault(); var newer = new string('n', 43);
                await using var host = new RemoteFakeHost(gate.Answer);
                await using var client = new RemoteClientService(store, directory, vault, seen.Event, seen.Permission, options);
                var info = await client.ConnectAsync("", "127.0.0.1:" + host.Port, Key);
                gate.Close();
                var again = client.ConnectAsync("", "127.0.0.1:" + host.Port, newer);
                await gate.WaitArrived();
                vault.Hold();
                var remove = client.RemoveAsync(info.Id);
                Check(vault.Entered.Wait(TimeSpan.FromSeconds(10)), "the remove is deleting the key");
                gate.Released.SetResult();
                Check(await Task.WhenAny(again, Task.Delay(500)) != again, "the connect does not finish while the remove's delete is held");
                vault.Released.Set();
                await remove;
                var back = await again;
                Check(back.Id == info.Id && client.List().Single().Id == info.Id && RemoteConnectionFile.Read(directory).Single().Id == info.Id, "the connect after the remove lists the connection again");
                Check(vault.Load(info.Id) == newer && vault.Ids.SetEquals([info.Id]), "and its new key is in the vault: " + (vault.Load(info.Id) is null ? "none" : "another"));
            }
            finally { Directory.Delete(directory, true); }
        }
        // (d) A disconnect while a send's route check runs: the route failure is refused as changed and the disconnect stands.
        {
            var (store, directory) = await ClientStore();
            try
            {
                await using var host = new RemoteFakeHost(_ => RemoteFakeHost.Fixture("info.json"));
                var hold = false; var arrived = new ManualResetEventSlim(); var released = new ManualResetEventSlim();
                var routed = new RemoteClientOptions { AllowLoopbackForTests = true, RouteCheck = _ => { if (!Volatile.Read(ref hold)) return true; arrived.Set(); released.Wait(TimeSpan.FromSeconds(10)); return false; } };
                await using var client = new RemoteClientService(store, directory, new ListingVault(), seen.Event, seen.Permission, routed);
                var info = await client.ConnectAsync("", "127.0.0.1:" + host.Port, Key);
                Volatile.Write(ref hold, true);
                var send = Task.Run(() => client.EnsureConnectedAsync(info.Id));
                Check(arrived.Wait(TimeSpan.FromSeconds(10)), "the route check was asked");
                await client.DisconnectAsync(info.Id);
                released.Set();
                await Reject(() => send, "a route failure that outlived a disconnect is refused as changed", ex => ex.Message == Locale.Get("remote.connect.error.changed"));
                Check(client.Connection(info.Id)?.Detail == Locale.Get("remote.connect.detail.disconnected"), "and the disconnect's words stand: " + client.Connection(info.Id)?.Detail);
            }
            finally { Directory.Delete(directory, true); }
        }
    }
}
