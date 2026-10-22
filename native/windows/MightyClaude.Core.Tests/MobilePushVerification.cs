using System.Net;
using System.Net.Sockets;
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
}
