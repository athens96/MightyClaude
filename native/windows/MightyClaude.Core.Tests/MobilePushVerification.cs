using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MightyClaude.Core;

/// <summary>
/// Push notifications, held to the Mac's shared fixture (native/contracts/fixtures/push.json;
/// M/Tests/MightyCoreTests/MobilePushTests.swift).
/// </summary>
internal static class MobilePushVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static readonly Lazy<JsonNode> Fixture = new(() =>
    {
        using var stream = typeof(MobilePushVerification).Assembly.GetManifestResourceStream("MightyClaude.Core.Tests.Push.json") ?? throw new InvalidOperationException("push.json is not embedded");
        return JsonNode.Parse(stream)!;
    });
    private static string S(JsonNode node, string key) => node[key]!.GetValue<string>();

    internal static Task SealFollowsTheFixture()
    {
        var seal = Fixture.Value["seal"]!;
        var key = MobilePushSeal.Key(S(seal, "tokenHash"), S(seal, "clientId")) ?? throw new InvalidOperationException("no key");
        Check(Convert.ToBase64String(key) == S(seal, "keyB64"), "the key is the Mac's");
        Check(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(S(seal, "deviceToken")))).ToLowerInvariant() == S(seal, "tokenHash"), "the token hash is SHA-256 of the token");
        Check(MobilePushSeal.Seal(Encoding.UTF8.GetBytes(S(seal, "plaintext")), key, S(seal, "handle"), Convert.FromBase64String(S(seal, "nonceB64"))) == S(seal, "envelope"), "the envelope is the Mac's");
        Check(MobilePushSeal.Open(S(seal, "envelope"), key, S(seal, "handle")) is { } plain && Encoding.UTF8.GetString(plain) == S(seal, "plaintext"), "the envelope opens");
        foreach (var tampered in seal["tampered"]!.AsArray()) Check(MobilePushSeal.Open(tampered!.GetValue<string>(), key, S(seal, "handle")) is null, "a tampered envelope does not open");
        Check(MobilePushSeal.Open(S(seal, "envelope"), key, S(seal, "otherHandle")) is null, "another handle does not open it");
        Check(MobilePushSeal.Open(new string('A', 3_073), key, S(seal, "handle")) is null, "an envelope over 3072 characters does not open");
        Check(MobilePushSeal.Key("not hex", S(seal, "clientId")) is null, "a bad hash makes no key");
        Check(MobilePushSeal.Key("+f" + S(seal, "tokenHash")[2..], S(seal, "clientId")) is null, "a signed digit is not hex");
        Check(MobilePushSeal.Key(S(seal, "tokenHash").ToUpperInvariant(), S(seal, "clientId")) is { } upper && upper.SequenceEqual(key), "uppercase hex makes the same key");
        var refused = false;
        try { MobilePushSeal.Seal(new byte[2049], key, S(seal, "handle")); } catch (ArgumentException) { refused = true; }
        Check(refused, "a message over 2048 bytes is not sealed");
        Check(MobilePushSeal.Seal(new byte[2048], key, S(seal, "handle")).Length <= MobilePushSeal.MaximumEnvelope, "the longest message fits the envelope limit");
        // The fixture's plaintext is what the Mac encodes for its message: the same bytes here.
        var message = JsonSerializer.Deserialize<MobilePushMessage>(S(seal, "plaintext"), Wire.Json)!;
        Check(message.EncodedForPush() is { } encoded && Encoding.UTF8.GetString(encoded) == S(seal, "plaintext"), "the plaintext is encoded as the Mac encodes it");
        return Task.CompletedTask;
    }

    internal static Task PlannerFollowsTheFixture()
    {
        var planner = Fixture.Value["planner"]!;
        var subject = new MobilePushPlanner();
        foreach (var step in planner["steps"]!.AsArray())
        {
            var note = S(step!, "note");
            var panes = step!["panes"].Deserialize<List<MobilePushPane>>(Wire.Json)!;
            var sent = subject.Observe(panes, S(planner, "hostId"), S(planner, "hostName"), S(planner, "at"));
            var expected = step["expect"]!.AsArray();
            Check(sent.Count == expected.Count, note + ": count " + sent.Count);
            for (var i = 0; i < sent.Count; i++)
            {
                var message = sent[i]; var want = expected[i]!;
                Check(message.V == 1 && message.HostId == S(planner, "hostId") && message.HostName == S(planner, "hostName") && message.At == S(planner, "at"), note + ": constants");
                Check(message.Id == S(want, "id") && message.Kind == S(want, "kind") && message.SessionId == S(want, "sessionId"), note + ": id " + message.Id);
                Check(message.Workspace == S(want, "workspace") && message.Title == S(want, "title") && message.Summary == S(want, "summary"), note + ": text");
                var permission = want["permission"];
                Check(permission is null ? message.Permission is null : message.Permission == new MobilePushPermission(S(permission, "requestId"), S(permission, "runId"), S(permission, "buttons")), note + ": permission");
                var encoded = message.EncodedForPush() ?? throw new InvalidOperationException(note + ": does not encode");
                Check(encoded.Length <= MobilePushSeal.MaximumPlaintext, note + ": encoded " + encoded.Length);
                var pushed = JsonNode.Parse(encoded)!;
                Check(S(pushed, "summary") == (want["pushSummary"]?.GetValue<string>() ?? S(want, "summary")) && S(pushed, "id") == S(want, "id") && S(pushed, "title") == S(want, "title"), note + ": pushed");
            }
        }
        return Task.CompletedTask;
    }

    /// <summary>The bytes the Mac's JSONEncoder writes (its length checked against Swift's own, 227): "/" escaped,
    /// \n \r \t \b \f short, other controls as \u00XX, everything else (DEL, U+2028, emoji, Hangul, &lt;&amp;) as UTF-8.</summary>
    internal static Task EncodingMatchesTheMacsJsonEncoder()
    {
        var message = new MobilePushMessage("i", MobilePushKinds.Permission, "h", "H", "a/b", "\u0001\n\"\\\t\b\f\r", "\U0001F600\u007f\u2028가<&", "s", new("r", "u", MobilePushButtonKinds.Claude), "t");
        const string expected = "{\"v\":1,\"id\":\"i\",\"kind\":\"permission\",\"hostId\":\"h\",\"hostName\":\"H\",\"workspace\":\"a\\/b\",\"title\":\"\\u0001\\n\\\"\\\\\\t\\b\\f\\r\",\"summary\":\"\U0001F600\u007f\u2028가<&\",\"sessionId\":\"s\",\"permission\":{\"requestId\":\"r\",\"runId\":\"u\",\"buttons\":\"claude\"},\"at\":\"t\"}";
        var encoded = message.EncodedForPush() ?? throw new InvalidOperationException("does not encode");
        Check(Encoding.UTF8.GetString(encoded) == expected, "the Mac's escaping: " + Encoding.UTF8.GetString(encoded));
        Check(encoded.Length == 227, "Swift's length: " + encoded.Length);
        Check(Encoding.UTF8.GetString(new MobilePushMessage("i", MobilePushKinds.Completed, "h", "H", "w", "t", "", "s", null, "t").EncodedForPush()!) == "{\"v\":1,\"id\":\"i\",\"kind\":\"completed\",\"hostId\":\"h\",\"hostName\":\"H\",\"workspace\":\"w\",\"title\":\"t\",\"summary\":\"\",\"sessionId\":\"s\",\"at\":\"t\"}", "no permission, no key");
        return Task.CompletedTask;
    }

    internal static Task RequestsTakeTheirButtonsFromTheCard()
    {
        MobilePermission Card(string tool, bool canAllow = true, string? plan = null, MobileAntigravityCard? agy = null) => new("r", "u", tool, "t", null, [], "s", canAllow, null, plan, null, agy);
        Check(MobilePushRequest.From(Card("Bash")).Buttons == MobilePushButtonKinds.Claude, "a tool permission gets allow and deny");
        Check(MobilePushRequest.From(Card("Bash", false)).Buttons == MobilePushButtonKinds.None, "a deny-only request gets no buttons");
        Check(MobilePushRequest.From(Card("AskUserQuestion")).Kind == MobilePushKinds.Question, "a question");
        Check(MobilePushRequest.From(Card(ClaudePlanMode.ToolName, plan: "p")).Kind == MobilePushKinds.Plan, "a plan");
        var once = new MobileAntigravityCard([new MobileAntigravityAction("command", "command", "RunCommand", "rm x", "command(rm x)", true)]);
        var always = new MobileAntigravityCard([new MobileAntigravityAction("command", "command", "RunCommand", "ls", "command(ls)")]);
        var ruleless = new MobileAntigravityCard([new MobileAntigravityAction("other", "mcp", "Mcp")]);
        Check(MobilePushRequest.From(Card(MobileAntigravityCard.ToolName, agy: once)).Buttons == MobilePushButtonKinds.AntigravityOnce, "once only");
        Check(MobilePushRequest.From(Card(MobileAntigravityCard.ToolName, agy: always)).Buttons == MobilePushButtonKinds.Antigravity, "once or always");
        Check(MobilePushRequest.From(Card(MobileAntigravityCard.ToolName, false, agy: ruleless)).Buttons == MobilePushButtonKinds.None, "no rule, no buttons");
        Check(MobilePushRequest.From(Card(MobileAntigravityCard.ToolName, agy: once)).Kind == MobilePushKinds.Permission, "an Antigravity card is a permission");
        return Task.CompletedTask;
    }

    /// <summary>Starting over (phone remote access switched back on) replays nothing that changed meanwhile,
    /// and a pane's run numbers keep counting so an id already sent is not reused.</summary>
    internal static Task StartingOverReplaysNothingAndKeepsCounting()
    {
        var planner = new MobilePushPlanner();
        var pane = new MobilePushPane("p", "w", "t", "running", null, [], false);
        planner.Observe([pane], "h", "n", "a");
        Check(planner.Observe([pane with { Status = "completed" }], "h", "n", "a").Select(m => m.Id).SequenceEqual(["run:p:1:completed"]), "the first completion");
        planner.Reset();
        Check(planner.Observe([pane with { Requests = [new("x", "r", MobilePushKinds.Permission, "", MobilePushButtonKinds.Claude)] }], "h", "n", "a").Count == 0, "the first observation after a reset is the baseline");
        Check(planner.Observe([pane with { Status = "completed" }], "h", "n", "a").Select(m => m.Id).SequenceEqual(["run:p:2:completed"]), "the count goes on");
        return Task.CompletedTask;
    }

    /// <summary>Printing shows only ids and kinds: never the handle, the token hash, the envelope or what the notification says.</summary>
    internal static Task PrintingShowsNoHandleTokenHashOrContent()
    {
        var device = new MobilePushDevice("a", "handle-secret", new string('f', 64), new HashSet<string> { MobilePushKinds.Permission });
        var message = new MobilePushMessage("request:u:r", MobilePushKinds.Permission, "h", "host-secret", "workspace-secret", "title-secret", "summary-secret", "pane-1", null, "t");
        var send = new MobilePushSendRequest("server-1", "handle-secret", "envelope-secret");
        foreach (var text in new[] { device.ToString(), $"{device}", string.Join(",", new[] { device }) })
            Check(!text.Contains("handle-secret") && !text.Contains(device.TokenHash) && text.Contains('a'), "device: " + text);
        foreach (var text in new[] { message.ToString(), $"{message}" })
            Check(!new[] { "host-secret", "workspace-secret", "title-secret", "summary-secret" }.Any(text.Contains) && text.Contains("request:u:r"), "message: " + text);
        Check(!send.ToString().Contains("handle-secret") && !send.ToString().Contains("envelope-secret"), "send: " + send);
        return Task.CompletedTask;
    }

    internal static Task RecipientsAndRelayAnswers()
    {
        var message = new MobilePushMessage("x", MobilePushKinds.Completed, "h", "H", "w", "t", "", "pane-1", null, "t");
        var all = new MobilePushDevice("a", "ha", new string('0', 64), MobilePushKinds.All.ToHashSet());
        var quiet = new MobilePushDevice("b", "hb", new string('0', 64), new HashSet<string> { MobilePushKinds.Permission });
        var now = DateTimeOffset.FromUnixTimeSeconds(1000);
        Check(MobilePushRecipients.Targets(message, [all, quiet], new Dictionary<string, MobilePushViewing>(), now).Select(d => d.DeviceId).SequenceEqual(["a"]), "only the kinds a phone turned on");
        Check(MobilePushRecipients.Targets(message, [all], new Dictionary<string, MobilePushViewing> { ["a"] = new("pane-1", now.AddSeconds(10)) }, now).Count == 0, "not the session it is viewing");
        Check(MobilePushRecipients.Targets(message, [all], new Dictionary<string, MobilePushViewing> { ["a"] = new("pane-2", now.AddSeconds(10)) }, now).Count == 1, "another session it is viewing does not hold");
        Check(MobilePushRecipients.Targets(message, [all], new Dictionary<string, MobilePushViewing> { ["a"] = new("pane-1", now.AddSeconds(-1)) }, now).Count == 1, "an expired view does not hold");
        Check(MobilePushWire.ValidHandle(new string('A', 43)) && !MobilePushWire.ValidHandle(new string('A', 42)) && !MobilePushWire.ValidHandle(new string('A', 42) + "="), "a handle is 43 base64url characters");
        Check(MobilePushWire.Kinds(["signin", "later", "completed"]).SequenceEqual([MobilePushKinds.Completed, MobilePushKinds.Signin]), "known kinds in the host's order");
        Check(MobilePushRelay.SendUri("wss://relay.example.invalid")?.ToString() == "https://relay.example.invalid/push/v1/send", "wss → https");
        Check(MobilePushRelay.SendUri("ws://127.0.0.1:8787")?.ToString() == "http://127.0.0.1:8787/push/v1/send", "ws → http");
        Check(MobilePushRelay.SendUri("wss://relay.example.invalid/ws?x=1")?.ToString() == "https://relay.example.invalid/push/v1/send", "the path and query are replaced");
        Check(MobilePushRelay.SendUri("ftp://relay.example.invalid") is null, "other schemes are refused");
        Check(MobilePushSendOutcome.FromStatus(202, null).Status == MobilePushSendStatus.Sent, "202");
        Check(new[] { 403, 404, 410 }.All(s => MobilePushSendOutcome.FromStatus(s, null).Status == MobilePushSendStatus.Gone), "gone");
        Check(MobilePushSendOutcome.FromStatus(429, TimeSpan.FromSeconds(5)) == new MobilePushSendOutcome(MobilePushSendStatus.Retry, TimeSpan.FromSeconds(5), 429), "429 retries");
        Check(MobilePushSendOutcome.FromStatus(502, null).Status == MobilePushSendStatus.Retry, "502 retries");
        Check(MobilePushSendOutcome.FromStatus(503, null).Status == MobilePushSendStatus.Failed, "503 is not retried");
        Check(MobilePushSendOutcome.FromStatus(401, null) == new MobilePushSendOutcome(MobilePushSendStatus.Failed, null, 401), "401 fails");
        Check(MobilePushHttpTransport.RetryAfter("5") == TimeSpan.FromSeconds(5) && MobilePushHttpTransport.RetryAfter(" 12 ") == TimeSpan.FromSeconds(12), "seconds");
        Check(MobilePushHttpTransport.RetryAfter("999999") == TimeSpan.FromHours(1), "at most an hour");
        foreach (var bad in new[] { null, "", "-1", "nan", "inf", "Wed, 21 Oct 2026 07:28:00 GMT" }) Check(MobilePushHttpTransport.RetryAfter(bad) is null, "not seconds: " + bad);
        var wide = new string('가', 80);
        var subject = new MobilePushPlanner();
        subject.Observe([], "h", "H", "t");
        var pane = new MobilePushPane("p", wide, wide, "running", null, [new("r", "u", MobilePushKinds.Permission, string.Concat(Enumerable.Repeat("😀", 300)), MobilePushButtonKinds.Claude)], false);
        var wideMessage = subject.Observe([pane], "h", wide, "t").Single();
        Check(wideMessage.Summary.EnumerateRunes().Count() == 200, "the summary is clipped to 200 scalars");
        Check(wideMessage.EncodedForPush() is { Length: <= MobilePushSeal.MaximumPlaintext } fits && JsonNode.Parse(fits)!["summary"]!.GetValue<string>() == wideMessage.Summary, "a wide message fits with its summary");
        return Task.CompletedTask;
    }

    /// <summary>A relay that answers 3xx is refused, not followed: the bearer token and the envelope go nowhere else.</summary>
    internal static async Task ARedirectIsAFailureAndIsNeverFollowed()
    {
        using var elsewhere = new TcpListener(IPAddress.Loopback, 0);
        using var redirecting = new TcpListener(IPAddress.Loopback, 0);
        elsewhere.Start(); redirecting.Start();
        var elsewherePort = ((IPEndPoint)elsewhere.LocalEndpoint).Port;
        var hits = 0;
        _ = Task.Run(async () => { try { using var client = await elsewhere.AcceptTcpClientAsync(); Interlocked.Increment(ref hits); } catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { } });
        var answer = Task.Run(async () =>
        {
            using var client = await redirecting.AcceptTcpClientAsync();
            var stream = client.GetStream();
            var buffer = new byte[16_384]; var read = new StringBuilder();
            while (!read.ToString().Contains("\r\n\r\n")) { var count = await stream.ReadAsync(buffer); if (count == 0) break; read.Append(Encoding.ASCII.GetString(buffer, 0, count)); }
            var reply = $"HTTP/1.1 307 Temporary Redirect\r\nLocation: http://127.0.0.1:{elsewherePort}/push/v1/send\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(reply));
        });
        var url = new Uri($"http://127.0.0.1:{((IPEndPoint)redirecting.LocalEndpoint).Port}/push/v1/send");
        var outcome = await new MobilePushHttpTransport().SendAsync(new MobilePushSendRequest("s", "h", "p"), url, "t", CancellationToken.None);
        await answer;
        Check(outcome == new MobilePushSendOutcome(MobilePushSendStatus.Failed, null, 307), "a redirect fails: " + outcome.Status + " " + outcome.Code);
        await Task.Delay(100);
        Check(Volatile.Read(ref hits) == 0, "the redirect is not followed");
    }

    /// <summary>A host with one phone paired the ordinary way; returns the phone's id and token.</summary>
    internal static (MobileRelayHost Host, string ClientId, string Token) PairedHost(string directory)
    {
        var host = new MobileRelayHost(Path.Combine(directory, "mobile"), "Test PC", MobileDesktopRouter.Capabilities, (_, _, _, _, _) => Task.FromResult(new MobileReply(404, new { })));
        const string clientId = "AAECAwQFBgcICQoLDA0ODw";
        var auth = JsonSerializer.SerializeToElement(new { type = "auth", pairingKey = host.Identity.PairingKey, clientId, clientName = "Phone" });
        var result = host.Identity.Authenticate(auth, false);
        return (host, clientId, result.DeviceToken ?? throw new InvalidOperationException("no token: " + result.Error));
    }

    internal static async Task RoutesRegisterAHandleAndAView()
    {
        var directory = Verification.Temp();
        await using var service = new DesktopService(Path.Combine(directory, "state"), null, directory);
        await service.InitializeAsync();
        var (host, clientId, token) = PairedHost(directory);
        await using var _ = host;
        using var push = new MobilePushHub(service, host, "Test PC");
        using var router = new MobileDesktopRouter(service, Path.Combine(directory, "router"), "Test PC", (_, _) => Task.FromResult<object?>("started"), _ => new([])) { Push = push };
        Task<MobileReply> Route(string path, object body, string device) => router.RouteAsync("POST", path, JsonSerializer.SerializeToElement(body), device, CancellationToken.None);
        var handle = new string('h', 43);
        Check((await Route("/m1/push/register", new { handle, kinds = new[] { "permission", "completed", "future-kind" } }, clientId)).Status == 200, "a phone with a token registers");
        var devices = host.Identity.PushDevices();
        Check(devices.Count == 1 && devices[0].Handle == handle && devices[0].Kinds.SetEquals(["permission", "completed"]), "the handle and the known kinds are kept");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
        Check(devices[0].TokenHash == hash, "the key material is the token hash");
        Check((await Route("/m1/push/register", new { handle = "short", kinds = Array.Empty<string>() }, clientId)).Status == 400, "a bad handle is refused");
        Check((await Route("/m1/push/register", new { handle, kinds = Array.Empty<string>() }, "legacy")).Status == 409, "a phone without a token is refused");
        Check((await Route("/m1/push/viewing", new { sessionId = "bad id!" }, clientId)).Status == 404, "a malformed pane id");
        Check((await Route("/m1/push/viewing", new { sessionId = (string?)null }, clientId)).Status == 200, "null clears the view");
        Check((await Route("/m1/push/unregister", new { }, clientId)).Status == 200 && host.Identity.PushDevices().Count == 0, "unregister forgets the handle");
        Check(MobileDesktopRouter.Capabilities.Contains("push"), "the host advertises push");
    }

    /// <summary>
    /// What the Mac settled in review (M/Tests/MightyCoreTests/MobileRemoteTests.swift push tests): the refusals carry the shared
    /// words, a failed unregister is reported with the handle kept, a stale "gone" leaves a newer handle alone, a view holds 45 s,
    /// a rotated key forgets every view, and a handle the stored row cannot use is dropped when the file is read.
    /// </summary>
    internal static async Task RegistrationFollowsTheMacsRules()
    {
        var directory = Verification.Temp();
        await using var service = new DesktopService(Path.Combine(directory, "state"), null, directory);
        await service.InitializeAsync();
        var (host, clientId, _) = PairedHost(directory);
        await using var _host = host;
        var now = DateTimeOffset.Parse("2026-10-23T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        using var push = new MobilePushHub(service, host, "Test PC") { Clock = () => now };
        using var router = new MobileDesktopRouter(service, Path.Combine(directory, "router"), "Test PC", (_, _) => Task.FromResult<object?>("started"), _ => new([])) { Push = push };
        Task<MobileReply> Route(string path, object body, string device) => router.RouteAsync("POST", path, JsonSerializer.SerializeToElement(body), device, CancellationToken.None);
        static string Said(MobileReply reply) => JsonSerializer.SerializeToElement(reply.Body, Wire.Json).Text("error") ?? "";
        var handle = new string('h', 43); var newer = new string('n', 43);
        var bad = await Route("/m1/push/register", new { handle = "short", kinds = Array.Empty<string>() }, clientId);
        Check(bad.Status == 400 && Said(bad) == Locale.Get("remote.error.badPushHandle"), "a bad handle is refused in the shared words");
        var noKinds = await Route("/m1/push/register", new { handle }, clientId);
        Check(noKinds.Status == 400 && Said(noKinds) == Locale.Get("remote.error.badBody"), "a register without kinds is refused in the shared words, as on the Mac");
        var legacy = await Route("/m1/push/register", new { handle, kinds = Array.Empty<string>() }, "legacy");
        Check(legacy.Status == 409 && Said(legacy) == Locale.Get("remote.error.pushNeedsToken"), "a token-less phone is refused in the shared words");
        Check((await Route("/m1/push/register", new { handle, kinds = Array.Empty<string>() }, "ZZZZZZZZZZZZZZZZZZZZZZ")).Status == 409, "an unknown phone is refused");
        Check((await Route("/m1/push/register", new { handle = handle + "\n", kinds = Array.Empty<string>() }, clientId)).Status == 400, "a trailing newline is not a handle");
        var pane = await Route("/m1/push/viewing", new { sessionId = "bad id!" }, clientId);
        Check(pane.Status == 404 && Said(pane) == Locale.Get("remote.error.paneNotFound"), "a malformed pane id is not found");
        Check((await Route("/m1/push/register", new { handle, kinds = new[] { "completed" } }, clientId)).Status == 200, "registered");
        host.Identity.ClearPush(clientId, newer);
        Check(host.Identity.PushDevices().Single().Handle == handle, "a gone handle that is not the stored one clears nothing");

        Check((await Route("/m1/push/viewing", new { sessionId = "pane-1" }, clientId)).Status == 200, "a view");
        Check(push.Viewing.TryGetValue(clientId, out var seen) && seen == new MobilePushViewing("pane-1", now + TimeSpan.FromSeconds(45)), "the view holds 45 s from the clock");
        Check((await Route("/m1/push/viewing", new { }, clientId)).Status == 200 && push.Viewing.Count == 0, "a body without sessionId clears the view");
        Check((await Route("/m1/push/viewing", new { sessionId = "pane-2", deviceId = "b3RoZXItcGhvbmUtMDAwMA" }, clientId)).Status == 200 && push.Viewing.Keys.SequenceEqual([clientId]), "the view is the authenticated phone's, never a body field's");

        if (!OperatingSystem.IsWindows())
        {
            var folder = Path.Combine(directory, "mobile");
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            try
            {
                var failed = await Route("/m1/push/unregister", new { }, clientId);
                Check(failed.Status == 503 && Said(failed) == Locale.Get("mobileRemote.device.saveFailed"), "an unregister that cannot be saved is reported: " + failed.Status);
                Check(host.Identity.PushDevices().Single().Handle == handle && push.Viewing.ContainsKey(clientId), "and the handle and the view are kept");
                var unsaved = await Route("/m1/push/register", new { handle = newer, kinds = new[] { "completed" } }, clientId);
                Check(unsaved.Status == 503 && Said(unsaved) == Locale.Get("mobileRemote.device.saveFailed"), "a register that cannot be saved is reported");
                Check(host.Identity.PushDevices().Single().Handle == handle, "and the old handle is kept");
            }
            finally { File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
        }
        Check((await Route("/m1/push/unregister", new { }, clientId)).Status == 200 && host.Identity.PushDevices().Count == 0 && push.Viewing.Count == 0, "unregister forgets the handle and the view");
        Check((await Route("/m1/push/unregister", new { }, clientId)).Status == 200, "nothing left to clear is not a failure");

        Check((await Route("/m1/push/register", new { handle, kinds = new[] { "completed" } }, clientId)).Status == 200, "registered again");
        Check((await Route("/m1/push/viewing", new { sessionId = "pane-1" }, clientId)).Status == 200 && push.Viewing.Count == 1, "a view again");
        await host.RotateKeyAsync(clientId);
        Check(push.Viewing.Count == 0 && host.Identity.PushDevices().Count == 0, "a revoke (a rotated key) forgets every view and handle");

        // Reading the file drops a handle the row cannot use and keeps the phone.
        var other = Verification.Temp();
        var (again, phone, _) = PairedHost(other);
        await using (again)
        {
            Check(again.Identity.SetPush(phone, handle, ["completed"]) == MobilePushRegistrationResult.Saved, "saved");
            var file = Path.Combine(other, "mobile", "mobile-identity.json");
            var json = JsonNode.Parse(File.ReadAllText(file))!;
            json["devices"]![0]!["push"]!["handle"] = "not a handle";
            File.WriteAllText(file, json.ToJsonString());
            var read = new MobileIdentity(Path.Combine(other, "mobile"));
            Check(read.PushDevices().Count == 0 && read.Contains(phone, false), "a malformed handle is dropped, the phone kept");
        }
    }

    private sealed class RecordingTransport : IMobilePushTransport
    {
        private readonly object gate = new();
        public readonly List<(MobilePushSendRequest Request, Uri Url, string HostToken)> Sent = [];
        public readonly Queue<MobilePushSendOutcome> Outcomes = new();
        /// <summary>When set, answers instead of <see cref="Outcomes"/> (after the send is recorded).</summary>
        public Func<MobilePushSendRequest, CancellationToken, Task<MobilePushSendOutcome>>? Answer;
        public Task<MobilePushSendOutcome> SendAsync(MobilePushSendRequest request, Uri url, string hostToken, CancellationToken token)
        {
            lock (gate)
            {
                Sent.Add((request, url, hostToken));
                if (Answer is { } answer) return answer(request, token);
                return Task.FromResult(Outcomes.Count > 0 ? Outcomes.Dequeue() : new MobilePushSendOutcome(MobilePushSendStatus.Sent));
            }
        }
        public int Count { get { lock (gate) return Sent.Count; } }
    }

    internal static async Task ANewPermissionIsSealedAndSentAndAGoneHandleForgotten()
    {
        var directory = Verification.Temp();
        await using var service = new DesktopService(Path.Combine(directory, "state"), null, directory);
        await service.InitializeAsync();
        var (host, clientId, token) = PairedHost(directory);
        await using var _ = host;
        var transport = new RecordingTransport();
        using var push = new MobilePushHub(service, host, "Test PC") { Transport = transport, RetryDelays = [TimeSpan.Zero, TimeSpan.Zero], Relay = () => "wss://relay.example.invalid", Clock = () => DateTimeOffset.FromUnixTimeSeconds(1000), RemoteOn = () => true };
        var handle = new string('h', 43);
        Check(push.Register(clientId, handle, [MobilePushKinds.Permission]) == MobilePushRegistrationResult.Saved, "registered");
        var idle = new MobilePushPane("pane-1", "demo", "Claude 1", "running", null, [], false);
        await Task.WhenAll(push.Push([idle]));
        var asking = idle with { Requests = [new("req-1", "run-1", MobilePushKinds.Permission, "git status", MobilePushButtonKinds.Claude)] };
        await Task.WhenAll(push.Push([asking]));
        Check(transport.Sent.Count == 1, "one push: " + transport.Sent.Count);
        var (request, url, hostToken) = transport.Sent[0];
        Check(url.ToString() == "https://relay.example.invalid/push/v1/send", "to the relay's push route");
        Check(request.ServerId == Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(hostToken))).ToLowerInvariant(), "serverId is SHA-256 of the host token");
        var key = MobilePushSeal.Key(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant(), clientId)!;
        var plain = MobilePushSeal.Open(request.Payload, key, handle) ?? throw new InvalidOperationException("the phone could not open it");
        using var message = JsonDocument.Parse(plain);
        Check(message.RootElement.GetProperty("id").GetString() == "request:run-1:req-1" && message.RootElement.GetProperty("v").GetInt32() == 1, "the message");
        Check(message.RootElement.GetProperty("at").GetString() == "1970-01-01T00:16:40Z", "the Mac's timestamp form");
        Check(message.RootElement.GetProperty("permission").GetProperty("buttons").GetString() == "claude", "the buttons");

        push.View(clientId, "pane-1");
        await Task.WhenAll(push.Push([asking with { Requests = [.. asking.Requests, new("req-2", "run-1", MobilePushKinds.Permission, "", MobilePushButtonKinds.Claude)] }]));
        Check(transport.Sent.Count == 1, "nothing for the session the phone is viewing");
        push.View(clientId, null);

        transport.Outcomes.Enqueue(new(MobilePushSendStatus.Retry)); transport.Outcomes.Enqueue(new(MobilePushSendStatus.Retry)); transport.Outcomes.Enqueue(new(MobilePushSendStatus.Gone, null, 410));
        await Task.WhenAll(push.Push([asking with { Requests = [.. asking.Requests, new("req-3", "run-1", MobilePushKinds.Permission, "", MobilePushButtonKinds.Claude)] }]));
        Check(transport.Sent.Count == 4 && host.Identity.PushDevices().Count == 0, "two retries, then a gone handle is forgotten");
    }

    /// <summary>
    /// The panes as the planner sees them (M/Tests/MightyCoreTests/MobilePushTests.swift project): no files panes, no remote
    /// workspaces, pending requests minus the style's own then the Antigravity card, a confirmed lost sign-in, the raw status
    /// and the newest non-activity line cut to 400 scalars.
    /// </summary>
    internal static Task TheProjectionFollowsTheMac()
    {
        var local = new Workspace { Id = "ws-1", Name = "demo" };
        var shadow = new Workspace { Id = "ws-1", Name = "second" };
        var remote = new Workspace { Id = "ws-r", Name = "away", Remote = new("c", "w", "other PC") };
        var wide = string.Concat(Enumerable.Repeat("😀", 450));
        var asking = new RunSession
        {
            Id = "pane-a", WorkspaceId = "ws-1", Title = "Claude 1", Status = "running",
            Logs = [new("1", "assistant", "older", "t"), new("2", "assistant", wide, "t"), new("3", "assistant", "ran a tool", "t", Activity: new("a", "claude", "tool", "completed", "s")), new("4", "assistant", "", "t")],
        };
        var signedOut = new RunSession { Id = "pane-b", WorkspaceId = "ws-1", Title = "Claude 2", Status = "error" };
        var files = new RunSession { Id = "pane-f", WorkspaceId = "ws-1", Kind = "files" };
        var away = new RunSession { Id = "pane-r", WorkspaceId = "ws-r" };
        ToolPermissionRequest Request(string id, string state = "pending") => new(id, "pane-a", "tu-" + id, "Bash", "{\"command\":\"ls\"}", "ls " + id, State: state);
        var permissions = new Dictionary<string, IReadOnlyList<ToolPermissionRequest>> { ["pane-a"] = [Request("styled"), Request("asked"), Request("done", "allowed")], ["pane-r"] = [Request("far")] };
        var card = new AntigravityPermissionRequest("agy-1", "run-agy", [new("command", "RunCommand", "RunCommand", "ls", "command(ls)")]);
        IReadOnlyList<MobilePushPane> Project() => MobilePushPane.Project([asking, signedOut, files, away], [local, shadow, remote], permissions,
            (session, request) => session == "pane-a" && request.Id == "styled", id => id == "pane-a" ? card : null, id => id == "pane-b");
        var panes = Project();
        Check(panes.Select(p => p.SessionId).SequenceEqual(["pane-a", "pane-b"]), "only the panes a phone can see: " + string.Join(",", panes.Select(p => p.SessionId)));
        var a = panes[0];
        Check(a.Workspace == "demo" && a.Title == "Claude 1" && a.Status == "running", "the first workspace of an id names it, and a pane waiting on a request is still running");
        Check(a.Requests.Select(r => r.Id).SequenceEqual(["asked", "agy-1"]) && a.Requests[0].Buttons == MobilePushButtonKinds.Claude && a.Requests[1].Kind == MobilePushKinds.Permission && a.Requests[1].Buttons == MobilePushButtonKinds.Antigravity,
            "the pending requests minus the style's own, then the Antigravity card: " + string.Join(",", a.Requests.Select(r => r.Id)));
        Check(a.Preview == string.Concat(Enumerable.Repeat("😀", 400)) && a.Preview.EnumerateRunes().Count() == MobilePushPane.PreviewLimit, "the newest line that is not a tool activity, cut to 400 scalars");
        Check(!a.SignInLost && panes[1].SignInLost && panes[1].Preview is null && panes[1].Workspace == "demo", "a confirmed lost sign-in; no line, no preview");
        Check(Project().SequenceEqual(panes), "the same panes again are equal");
        permissions["pane-a"] = [Request("styled"), Request("asked"), Request("next")];
        Check(!Project().SequenceEqual(panes), "a new request makes them differ");
        return Task.CompletedTask;
    }

    /// <summary>
    /// The Mac's hand-off rules: nothing while phone remote access is off, the first hand-off after it comes on only sets the
    /// baseline (what happened meanwhile is not pushed), and the ids already sent are never sent again.
    /// </summary>
    internal static async Task NothingGoesOutWhileOffAndSwitchingOnPushesNothingOld()
    {
        var directory = Verification.Temp();
        await using var service = new DesktopService(Path.Combine(directory, "state"), null, directory);
        await service.InitializeAsync();
        var (host, clientId, _) = PairedHost(directory);
        await using var _host = host;
        var transport = new RecordingTransport(); var on = false;
        using var push = new MobilePushHub(service, host, "Test PC") { Transport = transport, RetryDelays = [], Relay = () => "wss://relay.example.invalid", RemoteOn = () => on };
        Check(push.Register(clientId, new string('h', 43), [MobilePushKinds.Permission, MobilePushKinds.Completed]) == MobilePushRegistrationResult.Saved, "registered");
        MobilePushPane Pane(params string[] requests) => new("pane-1", "demo", "Claude 1", "running", null, requests.Select(id => new MobilePushRequest(id, "run-1", MobilePushKinds.Permission, "", MobilePushButtonKinds.Claude)).ToArray(), false);
        async Task<string[]> Hand(MobilePushPane pane) { var before = transport.Count; await Task.WhenAll(push.Push([pane])); return transport.Sent.Skip(before).Select(s => s.Request.Payload).ToArray(); }
        Check(push.Observe().Count == 0 && (await Hand(Pane())).Length == 0 && (await Hand(Pane("r1"))).Length == 0, "nothing while off");
        on = true;
        Check((await Hand(Pane("r1", "r2"))).Length == 0, "the first hand-off after switching on only sets the baseline");
        Check((await Hand(Pane("r1", "r2", "r3"))).Length == 1, "then a new request is pushed");
        Check((await Hand(Pane("r1", "r2", "r3"))).Length == 0, "the same panes again decide nothing");
        on = false;
        Check((await Hand(Pane("r1", "r2", "r3", "r4"))).Length == 0, "off again: nothing");
        on = true;
        Check((await Hand(Pane("r1", "r2", "r3", "r4", "r5"))).Length == 0, "on again: a new baseline, r4 and r5 came while off");
        Check((await Hand(Pane("r3", "r6"))).Length == 1 && (await Hand(Pane("r3"))).Length == 0 && (await Hand(Pane("r3", "r6"))).Length == 0, "an id already sent is never sent again");
        push.Dispose();
        Check(push.Observe().Count == 0 && (await Hand(Pane("r7"))).Length == 0 && (await Hand(Pane("r7", "r8"))).Length == 0, "nothing after shutdown");
    }

    /// <summary>
    /// Sending (M/MobileRemoteService deliver): two retries at 2 s and 8 s, a longer Retry-After wins up to 30 s, an exception is a
    /// retry, a refusal ends it, "gone" forgets only the handle sent to, and switching off or closing stops it without a retry.
    /// </summary>
    internal static async Task DeliveriesRetryAndStopAsTheMacDoes()
    {
        var directory = Verification.Temp();
        await using var service = new DesktopService(Path.Combine(directory, "state"), null, directory);
        await service.InitializeAsync();
        var (host, clientId, _) = PairedHost(directory);
        await using var _host = host;
        var transport = new RecordingTransport(); var on = true; var waits = new List<TimeSpan>();
        using var push = new MobilePushHub(service, host, "Test PC") { Transport = transport, Relay = () => "wss://relay.example.invalid", RemoteOn = () => on, Wait = (wait, _) => { lock (waits) waits.Add(wait); return Task.CompletedTask; } };
        var handle = new string('h', 43); var newer = new string('n', 43);
        Check(push.Register(clientId, handle, [MobilePushKinds.Permission]) == MobilePushRegistrationResult.Saved, "registered");
        var serial = 0;
        async Task Ask() { serial++; await Task.WhenAll(push.Push([new("pane-1", "demo", "Claude 1", "running", null, [new("r" + serial, "run-1", MobilePushKinds.Permission, "", MobilePushButtonKinds.Claude)], false)])); }
        await Task.WhenAll(push.Push([new("pane-1", "demo", "Claude 1", "running", null, [], false)]));

        transport.Outcomes.Enqueue(new(MobilePushSendStatus.Retry, TimeSpan.FromSeconds(5))); transport.Outcomes.Enqueue(new(MobilePushSendStatus.Retry, MobilePushHttpTransport.MaximumRetryAfter));
        await Ask();
        Check(transport.Count == 3 && waits.SequenceEqual([TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30)]), "a longer Retry-After wins, at most 30 s: " + string.Join(",", waits));
        waits.Clear(); transport.Outcomes.Enqueue(new(MobilePushSendStatus.Retry, TimeSpan.FromSeconds(1))); transport.Outcomes.Enqueue(new(MobilePushSendStatus.Retry)); transport.Outcomes.Enqueue(new(MobilePushSendStatus.Retry));
        await Ask();
        Check(transport.Count == 6 && waits.SequenceEqual([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(8)]), "2 s, 8 s, then it gives up: " + string.Join(",", waits));
        transport.Outcomes.Enqueue(new(MobilePushSendStatus.Failed, null, 400));
        await Ask();
        Check(transport.Count == 7 && host.Identity.PushDevices().Count == 1, "a refusal is not retried and forgets nothing");
        var thrown = 0;
        transport.Answer = (_, _) => Interlocked.Increment(ref thrown) == 1 ? throw new InvalidOperationException("boom") : Task.FromResult(new MobilePushSendOutcome(MobilePushSendStatus.Sent));
        await Ask();
        Check(transport.Count == 9, "an exception is tried again");
        // The phone registered a newer handle while the old one was on its way: "gone" for the old one leaves the new one.
        transport.Answer = (_, _) => { push.Register(clientId, newer, [MobilePushKinds.Permission]); return Task.FromResult(new MobilePushSendOutcome(MobilePushSendStatus.Gone, null, 404)); };
        await Ask();
        Check(host.Identity.PushDevices().Single().Handle == newer, "only the handle that was sent to is forgotten");
        // Switched off between two attempts: no more.
        waits.Clear(); transport.Answer = (_, _) => { on = false; return Task.FromResult(new MobilePushSendOutcome(MobilePushSendStatus.Retry)); };
        var before = transport.Count;
        await Ask();
        Check(transport.Count == before + 1 && waits.Count == 0, "switched off meanwhile, nothing is tried again");
        on = true;
        // Closing while a send is under way: it is cancelled, and no retry is planned.
        var entered = new TaskCompletionSource();
        transport.Answer = async (_, token) => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return new MobilePushSendOutcome(MobilePushSendStatus.Sent); };
        before = transport.Count;
        serial++; var sending = push.Push([new("pane-1", "demo", "Claude 1", "running", null, [new("r" + serial, "run-1", MobilePushKinds.Permission, "", MobilePushButtonKinds.Claude)], false)]);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        push.Dispose();
        await Task.WhenAll(sending).WaitAsync(TimeSpan.FromSeconds(10));
        Check(transport.Count == before + 1 && waits.Count == 0 && sending.All(t => t.IsCompletedSuccessfully), "closing ends the send without a retry");
    }

    /// <summary>The caller's cancellation (the app closing) is thrown back, not turned into a retry.</summary>
    internal static async Task TheCallersCancellationIsNotARetry()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var thrown = false;
        try { await new MobilePushHttpTransport().SendAsync(new("s", "h", "p"), new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/push/v1/send"), "t", cancel.Token); }
        catch (OperationCanceledException) { thrown = true; }
        Check(thrown, "a cancelled send throws");
    }

    /// <summary>
    /// The WinUI observes on its 250 ms tick: a pane's lost sign-in and a finished run reach the planner through Observe, the panes
    /// are read again only when something moved, and the hub lets go of everything it subscribed to when disposed.
    /// </summary>
    internal static async Task ObservingReadsThePanesAndDisposingLetsGo()
    {
        var directory = Verification.Temp();
        await using var service = new DesktopService(Path.Combine(directory, "state"), null, directory);
        await service.InitializeAsync();
        var (host, clientId, token) = PairedHost(directory);
        await using var _host = host;
        static int Handlers(object target, string name) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic) is { } field ? (field.GetValue(target) as Delegate)?.GetInvocationList().Length ?? 0 : -1;
        string[] events = ["ToolPermissionChanged", "RunEventReceived", "AntigravityCardChanged"];
        var before = events.Select(name => Handlers(service, name)).Append(Handlers(host.Identity, "Rotated")).ToArray();
        Check(before.All(count => count >= 0), "the events are fields: " + string.Join(",", before));
        var transport = new RecordingTransport(); var lost = new HashSet<string>();
        var push = new MobilePushHub(service, host, "Test PC") { Transport = transport, RetryDelays = [], Relay = () => "wss://relay.example.invalid", RemoteOn = () => true, SignInLost = lost.Contains };
        Check(events.Select(name => Handlers(service, name)).Append(Handlers(host.Identity, "Rotated")).Zip(before).All(pair => pair.First == pair.Second + 1), "subscribed to all four");
        try
        {
            Check(push.Register(clientId, new string('h', 43), [MobilePushKinds.Signin, MobilePushKinds.Completed]) == MobilePushRegistrationResult.Saved, "registered");
            var workspace = await service.AddWorkspaceAsync(directory);
            var pane = new RunSession { Id = "pane-1", WorkspaceId = workspace.Id, Title = "Claude 1", Status = "running", Logs = [new("1", "assistant", "all done", "t")] };
            await service.UpdateAsync(s => s with { Sessions = [pane] });
            async Task<string[]> Observe()
            {
                var count = transport.Count; await Task.WhenAll(push.Observe());
                var key = MobilePushSeal.Key(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant(), clientId)!;
                return transport.Sent.Skip(count).Select(s => JsonDocument.Parse(MobilePushSeal.Open(s.Request.Payload, key, s.Request.Handle)!).RootElement.GetProperty("id").GetString()!).ToArray();
            }
            Check((await Observe()).Length == 0, "the first observation is the baseline");
            lost.Add("pane-1");
            Check((await Observe()).SequenceEqual(["signin:pane-1:1"]), "a lost sign-in is seen without any other news");
            await service.UpdateAsync(s => s with { Sessions = [pane with { Status = "completed" }] });
            Check((await Observe()).Length == 0, "nothing reported, nothing read");
            push.Changed();
            var finished = await Observe();
            Check(finished.SequenceEqual(["run:pane-1:1:completed"]), "a finished run once something moved: " + string.Join(",", finished));
        }
        finally { push.Dispose(); }
        Check(events.Select(name => Handlers(service, name)).Append(Handlers(host.Identity, "Rotated")).SequenceEqual(before), "disposing lets go of all four");
    }

    /// <summary>The service's own run events, as a runner reports them (DesktopService.Receive applies, then raises RunEventReceived).</summary>
    private static void Report(DesktopService service, RunEvent value)
        => typeof(DesktopService).GetMethod("Receive", BindingFlags.Instance | BindingFlags.NonPublic, [typeof(RunEvent)])!.Invoke(service, [value]);

    /// <summary>
    /// A run that starts and ends between two ticks (a start that fails at once, a queued follow-up, a delegated child) still gets
    /// its push; a shell's command does too, through the WinUI's Began/Changed; only status events make the panes be read again;
    /// and a request whose pane is gone is let go.
    /// </summary>
    internal static async Task ARunBetweenTwoTicksIsStillPushed()
    {
        var directory = Verification.Temp();
        await using var service = new DesktopService(Path.Combine(directory, "state"), null, directory);
        await service.InitializeAsync();
        var (host, clientId, token) = PairedHost(directory);
        await using var _host = host;
        var transport = new RecordingTransport();
        using var push = new MobilePushHub(service, host, "Test PC") { Transport = transport, RetryDelays = [], Relay = () => "wss://relay.example.invalid", RemoteOn = () => true };
        Check(push.Register(clientId, new string('h', 43), [MobilePushKinds.Completed, MobilePushKinds.Failed]) == MobilePushRegistrationResult.Saved, "registered");
        var workspace = await service.AddWorkspaceAsync(directory);
        var pane = new RunSession { Id = "pane-1", WorkspaceId = workspace.Id, Title = "Claude 1", Logs = [new("1", "assistant", "done", "t")] };
        var shell = new RunSession { Id = "shell-1", WorkspaceId = workspace.Id, Title = "Terminal", Kind = "shell" };
        await service.UpdateAsync(s => s with { Sessions = [pane, shell] });
        var key = MobilePushSeal.Key(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant(), clientId)!;
        async Task<string[]> Observe()
        {
            var count = transport.Count; await Task.WhenAll(push.Observe());
            return transport.Sent.Skip(count).Select(s => JsonDocument.Parse(MobilePushSeal.Open(s.Request.Payload, key, s.Request.Handle)!).RootElement.GetProperty("id").GetString()!).ToArray();
        }
        Check((await Observe()).Length == 0, "the baseline");
        Report(service, RunEvent.State("pane-1", "running")); Report(service, RunEvent.State("pane-1", "completed"));
        var completed = await Observe();
        Check(completed.SequenceEqual(["run:pane-1:1:completed"]), "running then completed between two ticks: " + string.Join(",", completed));
        Report(service, RunEvent.State("pane-1", "running")); Report(service, RunEvent.Log("pane-1", "error", "could not start")); Report(service, RunEvent.State("pane-1", "error"));
        var failed = await Observe();
        Check(failed.SequenceEqual(["run:pane-1:2:failed"]), "running then error between two ticks: " + string.Join(",", failed));
        Report(service, RunEvent.State("pane-1", "running"));
        Check((await Observe()).Length == 0, "a run seen running");
        Report(service, RunEvent.State("pane-1", "completed"));
        Check((await Observe()).SequenceEqual(["run:pane-1:3:completed"]), "and seen ending: one push, not two");

        Check(service.SetShellStatus("shell-1", "running"), "the shell runs a command"); push.Began("shell-1");
        Check(service.SetShellStatus("shell-1", "completed"), "the shell exits"); push.Changed();
        var exited = await Observe();
        Check(exited.SequenceEqual(["run:shell-1:1:completed"]), "a shell's run between two ticks: " + string.Join(",", exited));

        // Output alone does not make the panes be read again: the change below is seen only once a status event comes.
        await service.UpdateAsync(s => s with { Sessions = s.Sessions.Select(p => p.Id == "pane-1" ? p with { Status = "running" } : p).ToList() });
        Report(service, RunEvent.Log("pane-1", "assistant", "streaming"));
        Check((await Observe()).Length == 0, "output alone sends nothing");
        await service.UpdateAsync(s => s with { Sessions = s.Sessions.Select(p => p.Id == "pane-1" ? p with { Status = "completed" } : p).ToList() });
        push.Changed();
        Check((await Observe()).Length == 0, "output did not make the panes be read while the pane was running");

        // A pending request whose pane is no longer there is let go at the next read.
        var raise = (Action<ToolPermissionRequest>)typeof(DesktopService).GetField("ToolPermissionChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
        raise(new("req-gone", "pane-gone", "tu", "Bash", "{}", "ls"));
        raise(new("req-here", "pane-1", "tu", "Bash", "{}", "ls"));
        await Observe();
        var kept = (List<ToolPermissionRequest>)typeof(MobilePushHub).GetField("permissions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(push)!;
        lock (kept) Check(kept.Select(p => p.Id).SequenceEqual(["req-here"]), "only the requests of panes still there: " + string.Join(",", kept.Select(p => p.Id)));
    }
}
