using System.Text.Json;
using MightyClaude.Core;

/// <summary>The Windows client of remote workspaces (docs/remote-workspaces.md): saved connections, keys, addresses, transport, runs.</summary>
internal static partial class RemoteClientVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static Task Reject(Func<Task> action, string message, Func<Exception, bool>? expected = null) => RejectAsync(action, message, expected);
    internal static async Task RejectAsync(Func<Task> action, string message, Func<Exception, bool>? expected = null)
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
            {
                // A key file that cannot be removed (here a folder in its place; on Windows also a file another program holds open) is
                // tried again for about a second, then left with one trace line that names only the exception's type.
                var stuck = Path.Combine(directory, DpapiRemoteKeyVault.FolderName, DpapiRemoteKeyVault.FileName("conn:stuck"));
                Directory.CreateDirectory(Path.Combine(stuck, "inside"));
                var traced = new TraceLines(); System.Diagnostics.Trace.Listeners.Add(traced);
                var took = System.Diagnostics.Stopwatch.StartNew();
                try { dpapi.Delete("conn:stuck"); }
                finally { System.Diagnostics.Trace.Listeners.Remove(traced); }
                var lines = traced.Lines.Where(l => l.Contains("remote key delete", StringComparison.Ordinal)).ToList();
                Check(took.Elapsed >= TimeSpan.FromMilliseconds(800), "a delete that fails is tried again for about a second: " + took.ElapsedMilliseconds + " ms");
                Check(lines.Count == 1 && System.Text.RegularExpressions.Regex.IsMatch(lines[0], @"remote key delete: \w+Exception\s*$") && !lines[0].Contains(directory, StringComparison.Ordinal) && !lines[0].Contains("conn", StringComparison.Ordinal),
                    "and is traced once by its exception type alone: " + string.Join(" | ", lines));
                Directory.Delete(stuck, true);
            }
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
        /// <summary>When it matches an event, the caller's thread waits there (after the event is recorded) until <see cref="Release"/>.</summary>
        internal Func<RunEvent, bool>? Hold;
        internal readonly TaskCompletionSource Held = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly ManualResetEventSlim Release = new();
        /// <summary>Called on the caller's thread after each event is recorded.</summary>
        internal Action<RunEvent>? After;
        internal void Event(RunEvent value)
        {
            lock (events) events.Add(value);
            if (Hold?.Invoke(value) == true) { Held.TrySetResult(); Release.Wait(TimeSpan.FromSeconds(10)); }
            After?.Invoke(value);
        }
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

    /// <summary>
    /// A saved-connections file held open by another program at start (an antivirus scan) is read again later, never taken as empty: nothing
    /// is listed while it is held, no write of the list replaces it meanwhile, and once it is free its connections come back, the window is
    /// told, and a later write keeps them.
    /// </summary>
    internal static async Task AConnectionsFileHeldOpenIsReadLaterAndNeverWrittenOver()
    {
        var (store, directory) = await ClientStore(); var seen = new Collected();
        try
        {
            await RemoteConnectionFile.WriteAsync(directory, [new("conn-1", "studio", "100.101.102.103:43137"), new("conn-2", "mac", "[fd7a:115c:a1e0::5]:43137")]);
            var path = Path.Combine(directory, RemoteConnectionFile.FileName); var before = await File.ReadAllBytesAsync(path);
            await using var client = new RemoteClientService(store, directory, new MemoryRemoteKeyVault(), seen.Event, seen.Permission);
            var changed = 0; client.Changed += () => Interlocked.Increment(ref changed);
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Check(!RemoteConnectionFile.TryRead(directory, out _), "a file held open cannot be read now");
                Check(client.List().Count == 0 && client.Connection("conn-1") is null, "while it is held, nothing is listed");
                await client.RemoveAsync("conn-9"); // writes the list
            }
            Check((await File.ReadAllBytesAsync(path)).SequenceEqual(before), "no write replaced the file while it was unread");
            var heldChanges = Volatile.Read(ref changed);
            await Verification.Until(() => client.List().Select(c => c.Id).SequenceEqual(["conn-1", "conn-2"]));
            Check(Volatile.Read(ref changed) == heldChanges + 1, "the window is told once when the connections come back: " + (changed - heldChanges));
            await client.RemoveAsync("conn-9");
            Check(RemoteConnectionFile.Read(directory).Select(c => c.Id).SequenceEqual(["conn-1", "conn-2"]), "a write after the late read keeps them on disk");
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>
    /// A connect while the saved-connections file is held open waits for it to be read, as the Mac's actor reads it before any connect:
    /// an address the file holds keeps the file's id (so its workspaces still find it), a new address is saved beside the file's entries,
    /// and a file held past the wait refuses the connect and is left as it was.
    /// </summary>
    internal static async Task AConnectWaitsForAHeldConnectionsFile()
    {
        await using var host = new RemoteFakeHost(request => request.Target == "/v2/info" ? request.Headers["authorization"] == "Bearer " + Key ? RemoteFakeHost.Fixture("info.json") : RemoteFakeHost.Fixture("error.json", 401) : new(404, "{}"));
        var address = "127.0.0.1:" + host.Port; var seen = new Collected(); var directories = new List<string>();
        async Task<(RemoteClientService Client, string Path)> Client(IEnumerable<RemoteSavedConnection> saved, TimeSpan? wait = null)
        {
            var (store, directory) = await ClientStore(); directories.Add(directory);
            await RemoteConnectionFile.WriteAsync(directory, saved);
            var options = new RemoteClientOptions { AllowLoopbackForTests = true, SavedReadWait = wait ?? TimeSpan.FromSeconds(5) };
            return (new RemoteClientService(store, directory, new MemoryRemoteKeyVault(), seen.Event, seen.Permission, options), Path.Combine(directory, RemoteConnectionFile.FileName));
        }
        try
        {
            // (b) The address the file holds keeps the file's id.
            var (known, knownPath) = await Client([new("conn-old", "studio", address), new("conn-2", "mac", "[fd7a:115c:a1e0::5]:43137")]);
            await using (known)
            {
                Task<RemoteConnectionInfo> again;
                using (new FileStream(knownPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    again = known.ConnectAsync("", address, Key);
                    await Task.Delay(400);
                    Check(!again.IsCompleted, "a connect waits while the saved file is held");
                }
                var info = await again;
                Check(info.Id == "conn-old" && known.List().Select(c => c.Id).SequenceEqual(["conn-old", "conn-2"]), "the file's id stands for its address: " + info.Id);
                Check(RemoteConnectionFile.Read(Path.GetDirectoryName(knownPath)!).Select(c => c.Id).SequenceEqual(["conn-old", "conn-2"]), "the file keeps the old id, nothing added for it");
            }
            // (a) A new address is saved with the file's entries, in the same launch.
            var (fresh, freshPath) = await Client([new("conn-2", "mac", "[fd7a:115c:a1e0::5]:43137")]);
            await using (fresh)
            {
                Task<RemoteConnectionInfo> added;
                using (new FileStream(freshPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    added = fresh.ConnectAsync("", address, Key);
                    await Task.Delay(400);
                    Check(!added.IsCompleted, "a new connect waits while the saved file is held");
                }
                var info = await added;
                Check(RemoteConnectionFile.Read(Path.GetDirectoryName(freshPath)!).Select(c => c.Id).SequenceEqual(["conn-2", info.Id]), "the new connection is saved beside the file's");
            }
            // A file held past the wait refuses the connect and stays as it was.
            var (held, heldPath) = await Client([new("conn-2", "mac", "[fd7a:115c:a1e0::5]:43137")], TimeSpan.FromMilliseconds(300));
            await using (held)
            {
                var before = await File.ReadAllBytesAsync(heldPath);
                using (new FileStream(heldPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    await Reject(() => held.ConnectAsync("", address, Key), "a connect is refused while the file stays held", ex => ex.Message == RemoteWindowsStrings.SavedUnreadable);
                Check((await File.ReadAllBytesAsync(heldPath)).SequenceEqual(before), "the refused connect left the file as it was");
                await Verification.Until(() => held.List().Select(c => c.Id).SequenceEqual(["conn-2"]));
            }
        }
        finally { foreach (var directory in directories) Directory.Delete(directory, true); }
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

    /// <summary>Collects trace lines (a check adds it to Trace.Listeners and removes it again).</summary>
    private sealed class TraceLines : System.Diagnostics.TraceListener
    {
        private readonly List<string> lines = []; private string partial = "";
        internal string[] Lines { get { lock (lines) return [.. lines]; } }
        public override void Write(string? message) { lock (lines) partial += message; }
        public override void WriteLine(string? message) { lock (lines) { lines.Add(partial + message); partial = ""; } }
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

    // Runs (M/Remote/RemoteClientService.swift start, follow, stop, send, dropRuns, shutdown).

    /// <summary>Serves /v2/info and then whatever the check queues for job-1's polls; <see cref="Hook"/> answers first when it gives a reply.</summary>
    internal sealed class ScriptedHost : IAsyncDisposable
    {
        internal readonly System.Collections.Concurrent.ConcurrentQueue<RemoteFakeHost.Reply> Polls = new();
        internal RemoteFakeHost.Reply? Start;
        internal Func<RemoteFakeHost.Request, Task<RemoteFakeHost.Reply>?>? Hook;
        internal readonly RemoteFakeHost Host;
        internal ScriptedHost() => Host = new RemoteFakeHost(request => Hook?.Invoke(request) ?? Task.FromResult(Script(request)));
        private RemoteFakeHost.Reply Script(RemoteFakeHost.Request request) => request.Target switch
        {
            "/v2/info" => RemoteFakeHost.Fixture("info.json"),
            "/v2/runs" => Start ?? RemoteFakeHost.Fixture("start-accepted.json", 202),
            // Nothing queued: an empty poll at the client's own cursor, so the run just keeps waiting.
            var t when t.StartsWith("/v2/runs/job-1/events", StringComparison.Ordinal) => Polls.TryDequeue(out var reply) ? reply : Empty(t[(t.IndexOf("cursor=", StringComparison.Ordinal) + 7)..]),
            _ => RemoteFakeHost.Fixture("answer.json"),
        };
        internal static RemoteFakeHost.Reply Empty(string cursor) => new(200, "{\"protocol\":2,\"cursor\":" + cursor + ",\"lastCursor\":" + cursor + ",\"gap\":false,\"done\":false,\"events\":[]}");
        internal RemoteFakeHost.Request[] Seen(string prefix) => Host.Seen.Where(r => r.Target.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
        internal int PollCount => Seen("/v2/runs/job-1/events").Length;
        public ValueTask DisposeAsync() => Host.DisposeAsync();
    }

    internal static async Task<(RemoteClientService Client, Workspace Workspace, Collected Seen, string Directory)> Connected(ScriptedHost scripted, RemoteClientOptions? options = null)
    {
        var (store, directory) = await ClientStore(); var seen = new Collected();
        var client = new RemoteClientService(store, directory, new MemoryRemoteKeyVault(), seen.Event, seen.Permission, options ?? new() { AllowLoopbackForTests = true });
        var info = await client.ConnectAsync("", "127.0.0.1:" + scripted.Host.Port, Key);
        return (client, client.OpenWorkspace(info.Id, "ws-project"), seen, directory);
    }

    private static string Poll(int cursor, bool done, params string[] events) =>
        "{\"protocol\":2,\"cursor\":" + cursor + ",\"lastCursor\":" + cursor + ",\"gap\":false,\"done\":" + (done ? "true" : "false") + ",\"events\":[" + string.Join(",", events) + "]}";
    private static string LogEvent(int cursor, string text) =>
        "{\"cursor\":" + cursor + ",\"event\":{\"sessionId\":\"job-1\",\"type\":\"log\",\"entry\":{\"id\":\"log-" + cursor + "\",\"kind\":\"output\",\"text\":\"" + text + "\",\"timestamp\":\"2026-10-17T01:04:00.000Z\"}}}";
    private static string Lost(string detail) => Locale.Get("remote.run.error.connectionLost", new Dictionary<string, string> { ["detail"] = detail });

    internal static async Task ARunPostsTheMacsRequestAndAppliesTheHostsEventsAsThePanes()
    {
        await using var scripted = new ScriptedHost(); scripted.Polls.Enqueue(RemoteFakeHost.Fixture("poll.json"));
        var (client, workspace, seen, directory) = await Connected(scripted);
        try
        {
            var fixture = RemoteWireFixtureVerification.Node("start.json").Deserialize<RemoteWireStart>(RemoteWire.Json)!;
            var pane = fixture.ToRequest("pane-1", workspace.Id);
            await client.StartAsync(pane, workspace);
            await Verification.Until(() => seen.Events.Any(e => e.Type == "status" && e.Status == "completed"));
            var posted = scripted.Seen("/v2/runs").First(r => r.Method == "POST");
            Check(RemoteWireFixtureVerification.Same(System.Text.Json.Nodes.JsonNode.Parse(posted.Body), RemoteWireFixtureVerification.Node("start.json")), "the body is the Mac's start sample: " + System.Text.Encoding.UTF8.GetString(posted.Body));
            Check(posted.Headers.GetValueOrDefault("x-mighty-remote-graph") == "run", "a Windows client asks for graph runs");
            Check(seen.Events.All(e => e.SessionId == "pane-1") && seen.Events.Select(e => e.Type).SequenceEqual(["status", "resume", "log", "activity", "usage", "plan", "todos", "background", "status"]), "every usable event became the pane's, the Mac graph node left out: " + string.Join(",", seen.Events.Select(e => e.Type)));
            Check(seen.Permissions.Single() is { RunId: "pane-1", Id: "perm-1", InputJson: "{\"command\":\"ls\"}" }, "the tool request reached the pane");
            // The pane's last status comes first and the run ends right after it, as a local run ends.
            await Verification.Until(() => !client.IsRunning("pane-1"));
        }
        finally { await client.DisposeAsync(); Directory.Delete(directory, true); }
    }

    internal static async Task AnswersGoToTheHostRunAndAStopReachesTheHost()
    {
        await using var scripted = new ScriptedHost();
        var permission = RemoteWireFixtureVerification.Node("poll.json")["events"]![5]!.DeepClone();
        scripted.Polls.Enqueue(new(200, "{\"protocol\":2,\"cursor\":6,\"lastCursor\":6,\"gap\":false,\"done\":false,\"events\":[" + permission.ToJsonString() + "]}"));
        var (client, workspace, seen, directory) = await Connected(scripted);
        try
        {
            await client.StartAsync(new StartRunRequest("pane-1", workspace.Id, "claude", "hi", []), workspace);
            await Verification.Until(() => seen.Permissions.Length == 1);
            await client.RespondToPermissionAsync("pane-1", "perm-1", true);
            var answer = scripted.Seen("/v2/runs/job-1/permission").Single();
            Check(RemoteWireFixtureVerification.Same(System.Text.Json.Nodes.JsonNode.Parse(answer.Body), RemoteWireFixtureVerification.Node("permission.json")), "the answer is the Mac's sample, with the host's run id");
            await client.AnswerPlanAsync("pane-1", "perm-3", PlanDecision.Revise("Keep the tests"));
            Check(RemoteWireFixtureVerification.Same(System.Text.Json.Nodes.JsonNode.Parse(scripted.Seen("/v2/runs/job-1/plan").Single().Body), RemoteWireFixtureVerification.Node("plan-revised.json")), "a plan answer is the Mac's sample");
            await client.StopAsync("pane-1");
            Check(scripted.Seen("/v2/runs/job-1/stop").Single().Body is var stop && System.Text.Encoding.UTF8.GetString(stop) == "{}", "a stop reaches the host, with a body");
            Check(seen.Events.Last() is { Type: "status", Status: "stopped" } && !client.IsRunning("pane-1"), "a stop ends the pane");
        }
        finally { await client.DisposeAsync(); Directory.Delete(directory, true); }
    }

    internal static async Task OneUnusableEventIsSkippedAndAGapIsSaid()
    {
        await using var scripted = new ScriptedHost(); scripted.Polls.Enqueue(RemoteFakeHost.Fixture("poll-malformed.json"));
        var (client, workspace, seen, directory) = await Connected(scripted);
        try
        {
            await client.StartAsync(new StartRunRequest("pane-1", workspace.Id, "shell", "dir", []), workspace);
            await Verification.Until(() => !client.IsRunning("pane-1"));
            Check(seen.Events.Count(e => e.Entry?.Text == Locale.Get("remote.run.notice.skipped")) == 1 && seen.Events.Any(e => e.Entry?.Text == "ok") && seen.Events.Any(e => e.Status == "completed"), "the bad one is skipped and said once; the good ones arrive");
            Check(client.Connection(workspace.Remote!.ConnectionId)?.Status == RemoteConnectionStatus.Connected, "a skipped event keeps the connection");
            scripted.Polls.Enqueue(RemoteFakeHost.Fixture("poll-gap.json"));
            await client.StartAsync(new StartRunRequest("pane-2", workspace.Id, "claude", "hi", []), workspace);
            await Verification.Until(() => !client.IsRunning("pane-2"));
            Check(seen.Events.Any(e => e.SessionId == "pane-2" && e.Entry?.Text == Locale.Get("remote.run.notice.gap")) && seen.Events.Last(e => e.SessionId == "pane-2") is { Status: "error", Reason: "authentication" }, "a gap is said, and a lost sign-in reads as Windows' own");
        }
        finally { await client.DisposeAsync(); Directory.Delete(directory, true); }
    }

    internal static async Task ABusyHostIsAskedAgainForTenSecondsAndThenTheConnectionIsLost()
    {
        await using var scripted = new ScriptedHost();
        for (var i = 0; i < 2; i++) scripted.Polls.Enqueue(new(503, "{\"protocol\":2,\"error\":\"busy\"}"));
        scripted.Polls.Enqueue(RemoteFakeHost.Fixture("poll-malformed.json"));
        var (client, workspace, seen, directory) = await Connected(scripted, new() { AllowLoopbackForTests = true, PollRetryWindow = TimeSpan.FromSeconds(5) });
        try
        {
            await client.StartAsync(new StartRunRequest("pane-1", workspace.Id, "shell", "dir", []), workspace);
            await Verification.Until(() => seen.Events.Any(e => e.Status == "completed"), 10000);
            Check(!seen.Events.Any(e => e.Status == "error"), "two busy answers are ridden out");
        }
        finally { await client.DisposeAsync(); Directory.Delete(directory, true); }

        await using var down = new ScriptedHost();
        for (var i = 0; i < 50; i++) down.Polls.Enqueue(new(503, "{\"protocol\":2,\"error\":\"busy\"}"));
        var (lost, place, events, folder) = await Connected(down, new() { AllowLoopbackForTests = true, PollRetryWindow = TimeSpan.FromMilliseconds(1500) });
        try
        {
            await lost.StartAsync(new StartRunRequest("pane-1", place.Id, "shell", "dir", []), place);
            await Verification.Until(() => events.Events.Any(e => e.Status == "error"), 10000);
            Check(events.Events.Any(e => e.Entry?.Kind == "error" && e.Entry.Text == Lost("busy")), "the pane says the connection was lost, with the host's reason");
            Check(lost.Connection(place.Remote!.ConnectionId)?.Status == RemoteConnectionStatus.Disconnected, "past the window the connection is lost");
        }
        finally { await lost.DisposeAsync(); Directory.Delete(folder, true); }
    }

    internal static async Task UnsupportedRunsAndARefusedKeyNeverRun()
    {
        await using var scripted = new ScriptedHost();
        var (client, workspace, seen, directory) = await Connected(scripted);
        try
        {
            await Reject(() => client.StartAsync(new StartRunRequest("pane-1", workspace.Id, "claude", "hi", [], Settings: new(PermissionMode: "acceptEdits", FastMode: false, NetworkAccess: false)) with { Provider = "codex" }, workspace), "the host's missing Codex is refused locally", ex => ex.Message == Locale.Get("remote.host.error.cliMissing", new Dictionary<string, string> { ["name"] = ProviderCatalog.Name("codex") }));
            Check(scripted.Seen("/v2/runs").Length == 0, "nothing was posted");
            scripted.Start = RemoteFakeHost.Fixture("error.json", 401);
            await Reject(() => client.StartAsync(new StartRunRequest("pane-1", workspace.Id, "shell", "dir", []), workspace), "a host that no longer takes the key refuses", ex => ex is RemoteHttpFailure { Status: 401 });
            Check(client.Connection(workspace.Remote!.ConnectionId)?.Status == RemoteConnectionStatus.Disconnected && !client.IsRunning("pane-1"), "a 401 marks the connection lost");
            await Reject(() => client.StartAsync(new StartRunRequest("pane-1", workspace.Id, "shell", "dir", []), workspace), "a lost connection runs nothing", ex => ex.Message == Locale.Get("remote.run.error.notConnected"));
        }
        finally { await client.DisposeAsync(); Directory.Delete(directory, true); }
    }

    /// <summary>The Mac fails the whole poll on one event it cannot decode; Windows leaves out that one event alone, whatever is wrong with it.</summary>
    internal static async Task AMalformedEventIsDroppedOnItsOwn()
    {
        await using var scripted = new ScriptedHost();
        scripted.Polls.Enqueue(new(200, Poll(5, true,
            LogEvent(1, "ok"),
            "{\"cursor\":2,\"event\":\"not an event\"}",
            "{\"cursor\":3,\"event\":{\"sessionId\":\"job-1\",\"type\":\"log\",\"type\":\"status\",\"status\":\"error\"}}",
            "\"not an item\"", "null", "{\"event\":{\"sessionId\":\"job-1\",\"type\":\"status\",\"status\":\"error\"}}",
            "{\"cursor\":5,\"event\":{\"sessionId\":\"job-1\",\"type\":\"status\",\"status\":\"completed\"}}")));
        var (client, workspace, seen, directory) = await Connected(scripted);
        try
        {
            await client.StartAsync(new StartRunRequest("pane-1", workspace.Id, "shell", "dir", []), workspace);
            await Verification.Until(() => !client.IsRunning("pane-1"));
            Check(seen.Events.Select(e => e.Entry?.Text ?? e.Status).SequenceEqual(["ok", "completed", Locale.Get("remote.run.notice.skipped")]), "the good events arrive and the bad ones are said once: " + string.Join(" | ", seen.Events.Select(e => e.Entry?.Text ?? e.Status)));
            Check(client.Connection(workspace.Remote!.ConnectionId)?.Status == RemoteConnectionStatus.Connected, "and the connection stays");
            // A finished run whose last status never came ends in error, as on the Mac.
            scripted.Polls.Enqueue(new(200, Poll(1, true, LogEvent(1, "partial"))));
            await client.StartAsync(new StartRunRequest("pane-2", workspace.Id, "shell", "dir", []), workspace);
            await Verification.Until(() => !client.IsRunning("pane-2"));
            Check(seen.Events.Where(e => e.SessionId == "pane-2").Select(e => e.Entry?.Text ?? e.Status).SequenceEqual(["partial", "error"]), "a done poll without a last status ends the pane in error");
        }
        finally { await client.DisposeAsync(); Directory.Delete(directory, true); }
    }

    /// <summary>Holds job-1's next poll at the host until released, and says when it arrived.</summary>
    private sealed class HeldPoll
    {
        internal readonly TaskCompletionSource Arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<RemoteFakeHost.Reply> Released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task<RemoteFakeHost.Reply>? Answer(RemoteFakeHost.Request request)
        {
            if (!request.Target.StartsWith("/v2/runs/job-1/events", StringComparison.Ordinal) || Arrived.Task.IsCompleted) return null;
            Arrived.TrySetResult(); return Released.Task;
        }
    }

    internal static async Task ARouteOutsideTheTunnelEndsTheConnectionAndAStaleRunNeverFailsANewerOne()
    {
        var notVia = Locale.Get("remote.connect.error.notViaTailscale");
        await using var scripted = new ScriptedHost();
        var tunnel = true; var block = 0; var arrived = new ManualResetEventSlim(); var released = new ManualResetEventSlim();
        var options = new RemoteClientOptions
        {
            AllowLoopbackForTests = true,
            RouteCheck = _ => { if (Interlocked.Exchange(ref block, 0) == 1) { arrived.Set(); released.Wait(TimeSpan.FromSeconds(10)); return false; } return Volatile.Read(ref tunnel); },
        };
        var (client, workspace, seen, directory) = await Connected(scripted, options);
        var id = workspace.Remote!.ConnectionId;
        try
        {
            // A poll: the route is asked before each one, and outside the tunnel the connection is lost at once, never asked again.
            await client.StartAsync(new StartRunRequest("pane-1", workspace.Id, "shell", "dir", []), workspace);
            await Verification.Until(() => scripted.PollCount > 0);
            Volatile.Write(ref tunnel, false);
            await Verification.Until(() => !client.IsRunning("pane-1"));
            Check(client.Connection(id) is { Status: RemoteConnectionStatus.Disconnected, Detail: var detail } && detail == notVia, "the poll's route check lost the connection: " + client.Connection(id)?.Detail);
            Check(seen.Events.Where(e => e.SessionId == "pane-1").TakeLast(2).Select(e => e.Entry?.Text ?? e.Status).SequenceEqual([Lost(notVia), "error"]), "and the pane says why");

            // A start: nothing is posted.
            Volatile.Write(ref tunnel, true); await client.RefreshAsync(id); Volatile.Write(ref tunnel, false);
            var posts = scripted.Seen("/v2/runs").Count(r => r.Target == "/v2/runs");
            await Reject(() => client.StartAsync(new StartRunRequest("pane-2", workspace.Id, "shell", "dir", []), workspace), "a start outside the tunnel", ex => ex.Message == notVia);
            Check(scripted.Seen("/v2/runs").Count(r => r.Target == "/v2/runs") == posts && client.Connection(id)?.Status == RemoteConnectionStatus.Disconnected && !client.IsRunning("pane-2"), "posted nothing and lost the connection");

            // An answer and a stop, while the run's poll waits on the host: neither is sent.
            foreach (var action in new[] { "permission", "stop" })
            {
                Volatile.Write(ref tunnel, true); await client.RefreshAsync(id);
                var held = new HeldPoll(); scripted.Hook = held.Answer;
                await client.StartAsync(new StartRunRequest("pane-3", workspace.Id, "shell", "dir", []), workspace);
                await held.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Volatile.Write(ref tunnel, false);
                if (action == "permission") await Reject(() => client.RespondToPermissionAsync("pane-3", "perm-1", true), "an answer outside the tunnel", ex => ex.Message == notVia);
                else await client.StopAsync("pane-3");
                held.Released.TrySetResult(ScriptedHost.Empty("0")); scripted.Hook = null;
                Check(scripted.Seen("/v2/runs/job-1/" + action).Length == 0 && client.Connection(id)?.Detail == notVia && !client.IsRunning("pane-3"), "the " + action + " was not sent and the connection is lost");
            }

            // A stale run: its route check fails after its connection was disconnected and connected again. The newer connection stands.
            Volatile.Write(ref tunnel, true); await client.RefreshAsync(id);
            await client.StartAsync(new StartRunRequest("pane-4", workspace.Id, "shell", "dir", []), workspace);
            await Verification.Until(() => scripted.PollCount > 0 && client.IsRunning("pane-4"));
            Interlocked.Exchange(ref block, 1);
            Check(arrived.Wait(TimeSpan.FromSeconds(10)), "the run's route check was asked");
            await client.DisconnectAsync(id);
            await client.RefreshAsync(id);
            released.Set();
            await client.FollowsEnded().WaitAsync(TimeSpan.FromSeconds(10));
            Check(client.Connection(id)?.Status == RemoteConnectionStatus.Connected, "the stale run's route failure left the newer connection alone: " + client.Connection(id)?.Detail);
        }
        finally { released.Set(); await client.DisposeAsync(); Directory.Delete(directory, true); }
    }

    internal static async Task OnlyAPassingFailureIsAskedAgain()
    {
        await using var scripted = new ScriptedHost();
        scripted.Polls.Enqueue(new(500, "{\"protocol\":2,\"error\":\"broken\"}"));
        scripted.Polls.Enqueue(RemoteFakeHost.Fixture("poll-malformed.json"));
        var (client, workspace, seen, directory) = await Connected(scripted);
        try
        {
            await client.StartAsync(new StartRunRequest("pane-1", workspace.Id, "shell", "dir", []), workspace);
            await Verification.Until(() => seen.Events.Any(e => e.Status is "error" or "completed"));
            Check(seen.Events.Any(e => e.Entry?.Text == Lost("broken")) && seen.Events.Last().Status == "error" && scripted.PollCount == 1, "a host error is not asked again: the connection is lost at once");
            Check(client.Connection(workspace.Remote!.ConnectionId)?.Status == RemoteConnectionStatus.Disconnected, "and is shown lost");
        }
        finally { await client.DisposeAsync(); Directory.Delete(directory, true); }
    }

    internal static async Task ALostConnectionEndsItsRunsWithItsReasonCut()
    {
        const string emoji = "\U0001F600";
        var error = new string('a', 299) + emoji + new string('b', 20); var expected = new string('a', 299) + emoji;
        await using var scripted = new ScriptedHost();
        var adaptersFail = false;
        var (client, workspace, seen, directory) = await Connected(scripted, new() { AllowLoopbackForTests = true, LocalAddresses = () => Volatile.Read(ref adaptersFail) ? throw new InvalidOperationException(error) : ["100.101.102.103"] });
        try
        {
            await client.StartAsync(new StartRunRequest("pane-1", workspace.Id, "shell", "dir", []), workspace);
            await client.StartAsync(new StartRunRequest("pane-2", workspace.Id, "shell", "dir", []), workspace);
            Volatile.Write(ref adaptersFail, true);
            await Reject(() => client.RefreshAsync(workspace.Remote!.ConnectionId), "a refresh that fails with a long message", ex => ex.Message == error);
            foreach (var pane in new[] { "pane-1", "pane-2" })
                Check(seen.Events.Where(e => e.SessionId == pane).TakeLast(2).Select(e => e.Entry?.Text ?? e.Status).SequenceEqual([Lost(expected), "error"]) && !client.IsRunning(pane), pane + " ended with the reason cut at whole characters");
            await Verification.Until(() => scripted.Seen("/v2/runs/job-1/stop").Length == 2);
        }
        finally { await client.DisposeAsync(); Directory.Delete(directory, true); }
    }

    internal static async Task ClosingTheAppStopsItsRunsOnTheHost()
    {
        await using var scripted = new ScriptedHost();
        var (client, workspace, seen, directory) = await Connected(scripted);
        try
        {
            await client.StartAsync(new StartRunRequest("pane-1", workspace.Id, "shell", "dir", []), workspace);
            await client.DisposeAsync();
            Check(scripted.Seen("/v2/runs/job-1/stop").Length == 1 && seen.Events.Last() is { SessionId: "pane-1", Status: "stopped" } && !client.IsRunning("pane-1"), "closing stopped the run on the host and in the pane");
            await Reject(() => client.StartAsync(new StartRunRequest("pane-2", workspace.Id, "shell", "dir", []), workspace), "nothing starts after closing", ex => ex.Message == Locale.Get("common.appClosing"));
        }
        finally { Directory.Delete(directory, true); }
    }

    internal static async Task ARemotePaneKeepsTheNewestRequestsAndAnswersWithTheHostsRunId()
    {
        await using var scripted = new ScriptedHost();
        var requests = Enumerable.Range(0, 40).Select(i => "{\"cursor\":" + (i + 1) + ",\"event\":{\"sessionId\":\"job-1\",\"type\":\"permission\",\"permission\":{\"id\":\"p" + i + "\",\"runId\":\"host-" + i
            + "\",\"toolUseId\":\"t" + i + "\",\"toolName\":\"Bash\",\"inputJSON\":\"{}\",\"summary\":\"ls\",\"state\":\"pending\",\"canAllow\":true,\"canAnswerQuestions\":false,\"canAnswerPlan\":false}}}").ToArray();
        scripted.Polls.Enqueue(new(200, Poll(40, false, requests)));
        var (client, workspace, seen, directory) = await Connected(scripted);
        try
        {
            await client.StartAsync(new StartRunRequest("pane-1", workspace.Id, "claude", "hi", []), workspace);
            await Verification.Until(() => seen.Permissions.Count(p => p.State == "pending") == 40);
            var withdrawn = seen.Permissions.Where(p => p.State == "cancelled").Select(p => p.Id).ToArray();
            Check(withdrawn.SequenceEqual(Enumerable.Range(0, 8).Select(i => "p" + i)) && seen.Permissions.All(p => p.RunId == "pane-1"), "the newest 32 wait and the 8 older ones are withdrawn: " + string.Join(",", withdrawn));
            await client.RespondToPermissionAsync("pane-1", "p39", false);
            Check(System.Text.Json.Nodes.JsonNode.Parse(scripted.Seen("/v2/runs/job-1/permission").Single().Body)?["runId"]?.GetValue<string>() == "host-39", "an answer carries the host's run id of that request");
        }
        finally { await client.DisposeAsync(); Directory.Delete(directory, true); }
    }

    internal static async Task AStopWaitsForThePollBeingAppliedAndIsThePanesLastWord()
    {
        await using var scripted = new ScriptedHost();
        scripted.Polls.Enqueue(new(200, Poll(3, false, LogEvent(1, "first"), LogEvent(2, "second"), LogEvent(3, "third"))));
        var (client, workspace, seen, directory) = await Connected(scripted);
        seen.Hold = e => e.Entry?.Text == "first";
        try
        {
            await client.StartAsync(new StartRunRequest("pane-1", workspace.Id, "shell", "dir", []), workspace);
            await seen.Held.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var stop = client.StopAsync("pane-1");
            await Task.WhenAny(stop, Task.Delay(500));
            Check(!stop.IsCompleted, "the stop waits while the poll is being applied");
            seen.Release.Set();
            await stop.WaitAsync(TimeSpan.FromSeconds(10));
            Check(seen.Events.Select(e => e.Entry?.Text ?? e.Status).SequenceEqual(["first", "second", "third", "stopped"]), "the poll's events, then the stop: " + string.Join(",", seen.Events.Select(e => e.Entry?.Text ?? e.Status)));
        }
        finally { seen.Release.Set(); await client.DisposeAsync(); Directory.Delete(directory, true); }
    }

    /// <summary>
    /// While a disconnect, a remove or a new key's connect is stopping the connection's runs on the host, the connection is already not
    /// connected: a start in that window is refused, and no run polls with the old key after it.
    /// </summary>
    internal static async Task NoRunStartsOrOutlivesItsConnectionWhileItsStopsAreOnTheirWay()
    {
        foreach (var leave in new[] { "disconnect", "remove", "replace" })
        {
            var stopArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var stopReleased = new TaskCompletionSource<RemoteFakeHost.Reply>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var scripted = new ScriptedHost();
            scripted.Hook = request =>
            {
                if (request.Target != "/v2/runs/job-1/stop" || stopArrived.Task.IsCompleted) return null;
                stopArrived.TrySetResult(); return stopReleased.Task;
            };
            var (client, workspace, seen, directory) = await Connected(scripted);
            var id = workspace.Remote!.ConnectionId;
            try
            {
                await client.StartAsync(new StartRunRequest("pane-1", workspace.Id, "shell", "dir", []), workspace);
                Task leaving = leave switch
                {
                    "disconnect" => client.DisconnectAsync(id),
                    "remove" => client.RemoveAsync(id),
                    _ => client.ConnectAsync("", "127.0.0.1:" + scripted.Host.Port, new string('n', 43)),
                };
                await stopArrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await Reject(() => client.StartAsync(new StartRunRequest("pane-2", workspace.Id, "shell", "dir", []), workspace), leave + ": a start while the stops are on their way", ex => ex.Message == Locale.Get("remote.run.error.notConnected"));
                stopReleased.TrySetResult(RemoteFakeHost.Fixture("answer.json"));
                await leaving;
                await client.FollowsEnded().WaitAsync(TimeSpan.FromSeconds(10));
                var polls = scripted.PollCount;
                Check(scripted.Seen("/v2/runs").Count(r => r.Target == "/v2/runs") == 1 && !client.IsRunning("pane-1") && !client.IsRunning("pane-2"), leave + ": only the first run was posted, and none runs");
                Check(seen.Events.Where(e => e.SessionId == "pane-1").Last().Status == "stopped" && !seen.Events.Any(e => e.SessionId == "pane-2"), leave + ": the first pane stopped, the second never began");
                Check(leave == "replace" ? client.Connection(id)?.Status == RemoteConnectionStatus.Connected : leave == "remove" ? client.Connection(id) is null : client.Connection(id)?.Status == RemoteConnectionStatus.Disconnected, leave + ": the connection is as it was left");
                Check(scripted.PollCount == polls, leave + ": nothing polls afterwards");
            }
            finally { stopReleased.TrySetResult(RemoteFakeHost.Fixture("answer.json")); await client.DisposeAsync(); Directory.Delete(directory, true); }
        }
    }

    /// <summary>A pane callback that throws ends that run as lost (never a pane left running with nobody polling); the connection stays.</summary>
    internal static async Task ACallbackThatThrowsEndsTheRun()
    {
        await using var scripted = new ScriptedHost();
        scripted.Polls.Enqueue(new(200, Poll(2, false, LogEvent(1, "boom"), LogEvent(2, "after"))));
        var (client, workspace, seen, directory) = await Connected(scripted);
        seen.After = e => { if (e.Entry?.Text == "boom") throw new InvalidOperationException("the pane broke"); };
        try
        {
            await client.StartAsync(new StartRunRequest("pane-1", workspace.Id, "shell", "dir", []), workspace);
            await Verification.Until(() => !client.IsRunning("pane-1"));
            await client.FollowsEnded().WaitAsync(TimeSpan.FromSeconds(10));
            Check(seen.Events.Select(e => e.Entry?.Text ?? e.Status).SequenceEqual(["boom", Lost("the pane broke"), "error"]), "the run ended at the failing callback: " + string.Join(",", seen.Events.Select(e => e.Entry?.Text ?? e.Status)));
            Check(client.Connection(workspace.Remote!.ConnectionId)?.Status == RemoteConnectionStatus.Connected, "and the connection stays");
        }
        finally { await client.DisposeAsync(); Directory.Delete(directory, true); }
    }

    /// <summary>A callback that disconnects while a poll is being applied (the lock lets its own thread in): nothing of that run follows its end.</summary>
    internal static async Task ADisconnectFromInsideAnEventEndsThePollThere()
    {
        await using var scripted = new ScriptedHost();
        scripted.Polls.Enqueue(new(200, Poll(3, false, LogEvent(1, "first"), LogEvent(2, "second"), LogEvent(3, "third"))));
        var (client, workspace, seen, directory) = await Connected(scripted);
        Task? disconnect = null;
        seen.After = e => { if (e.Entry?.Text == "first") disconnect = client.DisconnectAsync(workspace.Remote!.ConnectionId); };
        try
        {
            await client.StartAsync(new StartRunRequest("pane-1", workspace.Id, "shell", "dir", []), workspace);
            await Verification.Until(() => disconnect is not null && !client.IsRunning("pane-1"));
            await disconnect!;
            await client.FollowsEnded().WaitAsync(TimeSpan.FromSeconds(10));
            Check(seen.Events.Select(e => e.Entry?.Text ?? e.Status).SequenceEqual(["first", "stopped"]), "the run ended at the disconnect: " + string.Join(",", seen.Events.Select(e => e.Entry?.Text ?? e.Status)));
        }
        finally { await client.DisposeAsync(); Directory.Delete(directory, true); }
    }

    /// <summary>
    /// A disconnect while a run's poll waits on the host: the connection and its runs end in one step, so the pane's last word is the
    /// disconnect's "stopped", never a "connection changed" error from the poll, and the poll's late answer reaches nobody.
    /// </summary>
    internal static async Task ADisconnectWhileAPollIsInFlightEndsThePaneStopped()
    {
        await using var scripted = new ScriptedHost();
        var (client, workspace, seen, directory) = await Connected(scripted);
        var held = new HeldPoll(); scripted.Hook = held.Answer;
        try
        {
            await client.StartAsync(new StartRunRequest("pane-1", workspace.Id, "shell", "dir", []), workspace);
            await held.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await client.DisconnectAsync(workspace.Remote!.ConnectionId).WaitAsync(TimeSpan.FromSeconds(10));
            held.Released.TrySetResult(new(200, Poll(1, false, LogEvent(1, "late"))));
            await client.FollowsEnded().WaitAsync(TimeSpan.FromSeconds(10));
            var words = seen.Events.Where(e => e.SessionId == "pane-1").Select(e => e.Entry?.Text ?? e.Status).ToList();
            Check(words.SequenceEqual(["stopped"]), "the pane's only last word is stopped: " + string.Join(",", words));
            Check(!client.IsRunning("pane-1") && client.Connection(workspace.Remote!.ConnectionId)?.Status == RemoteConnectionStatus.Disconnected, "the run and the connection ended");
        }
        finally { held.Released.TrySetResult(ScriptedHost.Empty("0")); scripted.Hook = null; await client.DisposeAsync(); Directory.Delete(directory, true); }
    }

    /// <summary>
    /// A pane callback that throws while a remove ends the connection's runs: every other pane still gets its last word, and the
    /// connection and its key are still forgotten.
    /// </summary>
    internal static async Task APaneThatThrowsDuringARemoveLeavesTheOthersTheirLastWordAndTheKeyGoes()
    {
        await using var scripted = new ScriptedHost();
        var (store, directory) = await ClientStore(); var seen = new Collected(); var vault = new MemoryRemoteKeyVault();
        var client = new RemoteClientService(store, directory, vault, seen.Event, seen.Permission, new() { AllowLoopbackForTests = true });
        try
        {
            var info = await client.ConnectAsync("", "127.0.0.1:" + scripted.Host.Port, Key);
            var workspace = client.OpenWorkspace(info.Id, "ws-project");
            await client.StartAsync(new StartRunRequest("pane-1", workspace.Id, "shell", "dir", []), workspace);
            await client.StartAsync(new StartRunRequest("pane-2", workspace.Id, "shell", "dir", []), workspace);
            Check(vault.Load(info.Id) == Key, "the key is in the vault before the remove");
            // The first pane told it stopped throws, whichever it is.
            var thrown = 0;
            seen.After = e => { if (e.Status == "stopped" && Interlocked.Exchange(ref thrown, 1) == 0) throw new InvalidOperationException("the pane broke"); };
            await client.RemoveAsync(info.Id);
            await client.FollowsEnded().WaitAsync(TimeSpan.FromSeconds(10));
            Check(thrown == 1, "a pane's callback threw");
            foreach (var pane in new[] { "pane-1", "pane-2" })
                Check(seen.Events.LastOrDefault(e => e.SessionId == pane)?.Status == "stopped" && !client.IsRunning(pane), pane + " got its last word: " + string.Join(",", seen.Events.Where(e => e.SessionId == pane).Select(e => e.Entry?.Text ?? e.Status)));
            Check(client.Connection(info.Id) is null && client.List().Count == 0 && vault.Load(info.Id) is null, "the connection and its key are gone");
        }
        finally { await client.DisposeAsync(); Directory.Delete(directory, true); }
    }
}
