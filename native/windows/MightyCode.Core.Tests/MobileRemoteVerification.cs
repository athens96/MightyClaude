using System.Net;
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
using MightyCode.Core;

internal static class MobileRemoteVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value, Wire.Json);
    private static string Temp() => Directory.CreateTempSubdirectory("mighty-mobile-").FullName;
    private static void Reject(Action action, string message)
    { try { action(); } catch (Exception ex) when (ex is CryptographicException or Org.BouncyCastle.Crypto.CryptoException or ArgumentException or ObjectDisposedException or MobileRequestException) { return; } throw new InvalidOperationException(message); }
    internal static Task SharedCipherVectors()
    {
        using var resource = typeof(MobileRemoteVerification).Assembly.GetManifestResourceStream("MightyCode.Core.Tests.RelayVectors.json")!;
        using var document = JsonDocument.Parse(resource); var root = document.RootElement;
        byte[] Bytes(string key) => Convert.FromBase64String(root.GetProperty(key).GetString()!);
        Check(RelayCipher.PublicKey(Bytes("hostSecretKeyB64")).SequenceEqual(Bytes("hostPublicKeyB64")), "host X25519 public key matches Mac/mobile vector");
        using var host = new RelayCipher(Bytes("hostSecretKeyB64"), Bytes("clientPublicKeyB64"), Bytes("clientNonceB64"), Bytes("serverNonceB64"), true);
        using var client = new RelayCipher(Bytes("clientSecretKeyB64"), Bytes("hostPublicKeyB64"), Bytes("clientNonceB64"), Bytes("serverNonceB64"), false);
        for (var i = 0; i < 2; i++)
        {
            var cPlain = Encoding.UTF8.GetBytes(root.GetProperty("clientToHostPlaintexts")[i].GetString()!); var cFrame = client.Seal(cPlain);
            Check(Convert.ToBase64String(cFrame) == root.GetProperty("clientToHostFramesB64")[i].GetString(), "client AEAD bytes match shared golden vector");
            var tampered = cFrame.ToArray(); tampered[^1] ^= 1; Reject(() => host.Open(tampered), "forged tag accepted");
            Check(host.Open(cFrame).SequenceEqual(cPlain), "bad authentication does not advance receive counter"); Reject(() => host.Open(cFrame), "replay accepted");
            var hPlain = Encoding.UTF8.GetBytes(root.GetProperty("hostToClientPlaintexts")[i].GetString()!); var hFrame = host.Seal(hPlain);
            Check(Convert.ToBase64String(hFrame) == root.GetProperty("hostToClientFramesB64")[i].GetString(), "host AEAD bytes match shared golden vector"); Check(client.Open(hFrame).SequenceEqual(hPlain), "phone opens host ciphertext");
        }
        host.Dispose(); Reject(() => host.Seal([]), "disposed key reused");
        return Task.CompletedTask;
    }
    private static string Query(string link, string name) => Uri.UnescapeDataString(new Uri(link).Query.TrimStart('?').Split('&').Single(p => p.StartsWith(name + "=", StringComparison.Ordinal))[(name.Length + 1)..]);
    internal static Task DeviceTrustAndRotation()
    {
        var directory = Temp();
        try
        {
            var identity = new MobileIdentity(directory); var key = Query(identity.PairingUrl(MobileRelayHost.DefaultRelay, "Fixture"), "key"); var id = "abcdefghijklmnopqrstuv";
            Check(identity.Authenticate(Json(new { type = "auth", pairingKey = key, clientId = "bad" }), true).Error == "malformed", "malformed client id cannot downgrade to key-only");
            Check(identity.Authenticate(Json(new { type = "auth", pairingKey = key }), false).Error == "legacy-refused", "legacy clients can be disabled");
            var joined = identity.Authenticate(Json(new { type = "auth", pairingKey = key, clientId = id, clientName = "Phone" }), false);
            Check(joined.DeviceId == id && joined.DeviceToken is not null, "first pairing issues device token");
            Check(!File.ReadAllText(Path.Combine(directory, "mobile-identity.json")).Contains(joined.DeviceToken!), "only device token hash is persisted");
            // The phone scanned the QR again (the relay moved, its pairing was deleted): the key and the same id re-pair it, the row
            // keeps its first-seen time and loses its push handle, the new token works and the old one no longer does.
            var firstSeen = identity.Devices.Single(d => d.Id == id).FirstSeen;
            Check(identity.SetPush(id, new string('h', 43), MobilePushKinds.All.ToArray()) == MobilePushRegistrationResult.Saved && identity.PushDevices().Count == 1, "paired phone registers a push handle");
            var again = identity.Authenticate(Json(new { type = "auth", pairingKey = key, clientId = id, clientName = "Galaxy" }), false);
            Check(again.Error is null && again.DeviceId == id && again.DeviceToken is { } renewed && renewed != joined.DeviceToken, "pairing again with the key issues the same row a new token");
            Check(identity.Devices.Count == 1 && identity.Devices[0].FirstSeen == firstSeen && identity.Devices[0].Name == "Galaxy", "re-pairing keeps one row with its first-seen time and the new name");
            Check(identity.PushDevices().Count == 0, "re-pairing drops the push registration");
            Check(identity.Authenticate(Json(new { type = "auth", clientId = id, deviceToken = joined.DeviceToken }), false).Error == "device-revoked", "the token from before re-pairing stops working");
            Check(identity.Authenticate(Json(new { type = "auth", pairingKey = "not-the-key-not-the-key-not-the-key-not-the", clientId = id }), false).Error == "pairing-key", "re-pairing without the key is refused");
            joined = again;
            var reopened = new MobileIdentity(directory);
            Check(reopened.Authenticate(Json(new { type = "auth", clientId = id, deviceToken = joined.DeviceToken }), false).DeviceId == id, "returning device authenticates after app restart");
            reopened.Rotate(); Check(reopened.Devices.Count == 0, "key rotation erases every device credential");
            Check(reopened.Authenticate(Json(new { type = "auth", clientId = id, deviceToken = joined.DeviceToken }), false).Error == "device-revoked", "rotated device token is rejected");
            Check(reopened.Authenticate(Json(new { type = "auth", pairingKey = key, clientId = id }), false).Error == "pairing-key", "old QR key is rejected");
            var fresh = Query(reopened.PairingUrl(MobileRelayHost.DefaultRelay, "Fixture"), "key");
            for (var n = 0; n < 8; n++) Check(reopened.Authenticate(Json(new { type = "auth", pairingKey = fresh, clientId = n.ToString().PadLeft(22, 'a') }), false).Error is null, "registration below rate limit succeeds");
            Check(reopened.Authenticate(Json(new { type = "auth", pairingKey = fresh, clientId = "zzzzzzzzzzzzzzzzzzzzzz" }), false).Error == "device-limit", "new-device rate limit is enforced");
            Check(MobileRelayHost.NormalizeRelay("https://relay.example/path?token=x") == "wss://relay.example", "relay origin does not retain path/query");
            Check(MobileRelayHost.NormalizeRelay("wss://user:password@relay.example") is null, "relay URL credentials rejected");
            // The default relay is Cloudflare's; the Oracle relay earlier versions shipped, typed into the field or left there, falls back to it.
            Check(MobileRelayHost.DefaultRelay == "wss://mighty-relay.athens96.workers.dev", "default relay is the Cloudflare relay");
            Check(MobileRelayHost.EffectiveRelay("") == MobileRelayHost.DefaultRelay && MobileRelayHost.EffectiveRelay("not a url") == MobileRelayHost.DefaultRelay, "empty or invalid relay field uses the default");
            Check(MobileRelayHost.EffectiveRelay("relay.example") == "wss://relay.example", "user relay field wins over the default");
            Check(MobileRelayHost.EffectiveRelay("wss://mightyclaude.duckdns.org", "wss://default.example") == "wss://default.example", "retired default relay falls back to the default");
            Check(MobileRelayHost.EffectiveRelay("MightyClaude.DuckDNS.org", "wss://default.example") == "wss://default.example", "retired default relay matches without case after normalizing");
            Check(MobileRelayHost.EffectiveRelay("", "") is null, "no usable relay is null");
        }
        finally { Directory.Delete(directory, true); }
        return Task.CompletedTask;
    }
    internal static Task UploadOwnershipAndClaims()
    {
        var directory = Temp();
        try
        {
            using var store = new MobileUploads(directory); var ticket = store.Begin("pane", "phone-a", "../sample.txt", 4);
            Reject(() => store.Append(ticket.UploadId, "phone-b", 0, [65,66,67,68]), "other phone appended upload");
            Reject(() => store.Append(ticket.UploadId, "phone-a", 1, [65,66,67,68]), "out-of-order chunk accepted");
            Check(store.Append(ticket.UploadId, "phone-a", 0, [65,66,67,68]) == 4, "valid chunk appended"); store.Complete(ticket.UploadId, "phone-a");
            Reject(() => store.Claim([ticket.UploadId], "other-pane", "phone-a"), "cross-pane upload consumed");
            var claim = store.Claim([ticket.UploadId], "pane", "phone-a"); Reject(() => store.Claim([ticket.UploadId], "pane", "phone-a"), "concurrent double submit accepted");
            store.Finish(claim, false); var again = store.Claim([ticket.UploadId], "pane", "phone-a"); Check(Encoding.UTF8.GetString(AttachmentSupport.Decode(again.Attachments[0])) == "ABCD", "failed submit leaves attachment available");
            store.Finish(again, true); Reject(() => store.Claim([ticket.UploadId], "pane", "phone-a"), "spent upload replayed");
            Check(!Directory.EnumerateFiles(directory).Any(), "consumed file handle deletes staging bytes");
        }
        finally { Directory.Delete(directory, true); }
        return Task.CompletedTask;
    }
    internal static async Task DesktopRouteContract()
    {
        var directory = Temp();
        try
        {
            var workspacePath = Path.Combine(directory, "workspace"); Directory.CreateDirectory(workspacePath); File.WriteAllText(Path.Combine(workspacePath, "hello.txt"), "안녕하세요");
            await using var service = new DesktopService(Path.Combine(directory, "state"), null, directory); await service.InitializeAsync(); var workspace = await service.AddWorkspaceAsync(workspacePath);
            var pane = new RunSession { Id = "pane", WorkspaceId = workspace.Id, Logs = [new("first", "user", "hello", Wire.Now()), new("last", "assistant", "hi", Wire.Now())] };
            await service.UpdateAsync(s => s with { Sessions = [pane] }); var actions = new List<MobileDesktopAction>();
            using var router = new MobileDesktopRouter(service, Path.Combine(directory, "mobile"), "Fixture", (action, ct) => { ct.ThrowIfCancellationRequested(); actions.Add(action); return Task.FromResult<object?>("started"); }, _ => new([]));
            async Task<MobileReply> Route(string method, string path, object? value = null) => await router.RouteAsync(method, path, value is null ? null : Json(value), "phone", CancellationToken.None);
            var state = await Route("GET", "/m1/state"); Check(state.Status == 200 && Json(state.Body).GetProperty("sessions")[0].Text("id") == pane.Id, "phone receives desktop sessions");
            var history = Json((await Route("GET", "/m1/sessions/pane/entries?before=last&limit=1")).Body); Check(history.GetProperty("entries")[0].Text("id") == "first", "history pagination matches contract");
            Check((await Route("GET", "/m1/state?wait=11")).Status == 400, "unbounded poll rejected");
            Check((await Route("POST", "/m1/sessions/pane/permission", new { requestId = "stale", runId = "pane", allow = true })).Status == 409, "stale permissions fail closed");
            Check((await Route("POST", "/m1/sessions/pane/plan", new { requestId = "stale", runId = "pane", decision = "approveAutoEdit" })).Status == 409, "a stale plan answer fails closed");
            var unknown = await Route("POST", "/m1/sessions/pane/plan", new { requestId = "stale", runId = "pane", decision = "allow" });
            Check(unknown.Status == 400 && Json(unknown.Body).Text("error") == Locale.Get("plan.error.invalidDecision"), "an unknown plan decision is refused");
            var badId = await Route("POST", "/m1/sessions/pane/plan", new { requestId = "../x", runId = "pane", decision = "cancel" });
            Check(badId.Status == 400 && Json(badId.Body).Text("error") == Locale.Get("plan.error.invalidRequest"), "a malformed plan request id is refused");
            Check((await Route("POST", "/m1/sessions/pane/plan", new { requestId = "stale", runId = "pane", decision = "revise", feedback = " \n " })).Status == 400, "an empty change request never reaches the pane");
            Check((await Route("POST", "/m1/sessions/pane/submit", new { text = "hello", mode = "invented" })).Status == 400 && actions.Count == 0, "invalid submit mode never reaches composer");
            Check((await Route("POST", "/m1/sessions/pane/submit", new { text = "한글 request", mode = "queue" })).Status == 202 && actions.Single().Body.Text("text") == "한글 request", "valid submit routes complete UTF-8 text");
            Check((await Route("POST", $"/m1/workspaces/{workspace.Id}/sessions", new { kind = "shell" })).Status == 409, "phone cannot start local terminal");
            // The later agents: each is listed by name, so a phone offers it for a new pane, and the create route makes a pane of each.
            foreach (var provider in new[] { "pi", "opencode", "hermes" })
                Check(MobileDesktopRouter.Capabilities.Contains(provider) && (await Route("POST", $"/m1/workspaces/{workspace.Id}/sessions", new { kind = "claude", provider })).Status == 201
                    && actions[^1].Action == "create" && actions[^1].Body.Text("provider") == provider, "the phone may create a pane of " + provider);
            Check((await Route("POST", $"/m1/workspaces/{workspace.Id}/sessions", new { kind = "claude", provider = "rovo" })).Status == 400, "an agent the host does not run is refused");
            Check((await Route("GET", $"/m1/workspaces/{workspace.Id}/file?path=hello.txt")).Status == 200, "workspace text is previewed");
            Check((await Route("GET", $"/m1/workspaces/{workspace.Id}/file?path=..%2Foutside.txt")).Status == 403, "workspace escape is rejected");
            Check((await Route("GET", "/m1/screen-share/status")).Status == 503 && !MobileDesktopRouter.Capabilities.Contains("screenShare"), "unsupported capture is not advertised");
        }
        finally { Directory.Delete(directory, true); }
    }
    internal static async Task EncryptedRelayRoundTrip()
    {
        var directory = Temp(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var controlConnections = 0;
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory }); builder.Logging.ClearProviders(); builder.WebHost.UseKestrelCore(); builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));
        await using var app = builder.Build(); app.UseWebSockets(); MobileRelayHost? host = null;
        app.Run(async context =>
        {
            try
            {
                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                if (!context.Request.Query.ContainsKey("connectionId"))
                {
                    if (Interlocked.Increment(ref controlConnections) == 1)
                        await socket.SendAsync(Encoding.UTF8.GetBytes("{\"type\":\"connected\",\"connectionId\":\"fixture-connection\"}"), WebSocketMessageType.Text, true, timeout.Token);
                    else reconnected.TrySetResult();
                    var controlBuffer = new byte[1024]; while ((await socket.ReceiveAsync(controlBuffer, timeout.Token)).MessageType != WebSocketMessageType.Close) { } return;
                }
                var secret = RandomNumberGenerator.GetBytes(32); var nonce = RandomNumberGenerator.GetBytes(16);
                await Send(socket, Json(new { type = "hello", v = 1, clientKey = Convert.ToBase64String(RelayCipher.PublicKey(secret)), nonce = Convert.ToBase64String(nonce) }), timeout.Token);
                using var ready = JsonDocument.Parse(await Receive(socket, timeout.Token)); var server = Convert.FromBase64String(ready.RootElement.Text("serverKey")!);
                Check(Query(host!.PairingUrl, "pk").Replace('-', '+').Replace('_', '/') + "=" == Convert.ToBase64String(server), "relay host key matches QR pin");
                using var cipher = new RelayCipher(secret, server, nonce, Convert.FromBase64String(ready.RootElement.Text("nonce")!), false);
                async Task Encrypted(object message) => await socket.SendAsync(cipher.Seal(JsonSerializer.SerializeToUtf8Bytes(message, Wire.Json)), WebSocketMessageType.Binary, true, timeout.Token);
                await Encrypted(new { type = "auth", pairingKey = Query(host.PairingUrl, "key"), clientId = "abcdefghijklmnopqrstuv", clientName = "Fixture Phone" });
                using var auth = JsonDocument.Parse(cipher.Open(await Receive(socket, timeout.Token))); Check(auth.RootElement.Text("type") == "auth_ok" && auth.RootElement.Text("deviceToken") is not null, "phone authenticates over encrypted relay");
                // Screen share is paused (ScreenShareAvailability): a host built with the defaults leaves it out of auth_ok even when asked to add it.
                Check(auth.RootElement.GetProperty("capabilities").EnumerateArray().Any(c => c.GetString() == "screenShare") == ScreenShareAvailability.Enabled, "auth_ok follows the screen-share switch");
                await Encrypted(new { id = "request-1", method = "GET", path = "/m1/info" });
                using var response = JsonDocument.Parse(cipher.Open(await Receive(socket, timeout.Token))); Check(response.RootElement.Text("id") == "request-1" && response.RootElement.GetProperty("status").GetInt32() == 200, "encrypted request response is correlated");
                completed.TrySetResult(); var buffer = new byte[1024]; await socket.ReceiveAsync(buffer, timeout.Token);
            }
            catch (Exception ex) { if (!completed.Task.IsCompleted) completed.TrySetException(ex); }
        });
        try
        {
            await app.StartAsync(timeout.Token); var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            host = new MobileRelayHost(Path.Combine(directory, "identity"), "Fixture", [], (method, path, body, device, ct) => Task.FromResult(new MobileReply(200, new { protocol = 1, ok = true }))) { WatchesNetwork = false, ExtraCapabilities = () => ["screenShare"] };
            await host.ApplyAsync(new(true, address, false, DirectEnabled: false)); await completed.Task.WaitAsync(timeout.Token); Check(host.Devices.Count == 1, "device registered through live WebSocket handshake");
            var link = host.PairingUrl; var serverId = host.PairingServerId;
            Check(host.PairingKeyForDisplay == Query(link, "key"), "manual pairing key matches QR key");
            await host.ReconnectAsync(); await reconnected.Task.WaitAsync(timeout.Token);
            Check(host.Devices.Count == 1 && host.PairingUrl == link && host.PairingServerId == serverId && controlConnections == 2, "reconnect replaces relay socket while retaining pairing and trusted devices");
            await host.RotateKeyAsync(); Check(host.Devices.Count == 0, "live key rotation clears device registry");
            var rotated = host.PairingUrl; await host.RotateKeyAsync("abcdefghijklmnopqrstuv"); Check(host.PairingUrl == rotated, "stale device revoke cannot rotate a newer pairing key");
            await host.DisposeAsync(); host = null;
        }
        finally { if (host is not null) await host.DisposeAsync(); await app.StopAsync(CancellationToken.None); Directory.Delete(directory, true); }
    }
    /// <summary>
    /// Screen share's one switch (M/ScreenShareEngineTests.swift aHostWithScreenShareSwitchedOffNeitherAdvertisesNorRoutesIt): with it off
    /// a router handed the screen routes and the capability neither advertises nor routes it; the screen-share checks opt in.
    /// </summary>
    internal static async Task ScreenShareSwitch()
    {
        var directory = Temp();
        try
        {
            await using var service = new DesktopService(Path.Combine(directory, "state"), null, directory); await service.InitializeAsync();
            var routed = 0;
            Task<MobileReply> Screen(string method, string path, JsonElement? body, string device, CancellationToken token) { routed++; return Task.FromResult(new MobileReply(200, new { screenShare = new { } })); }
            async Task<(bool Advertised, int State, int Session)> Probe(MobileDesktopRouter router)
            {
                router.ExtraCapabilities = () => ["screenShare"]; router.ScreenRoute = Screen;
                var info = Json((await router.RouteAsync("GET", "/m1/info", null, "phone", CancellationToken.None)).Body);
                var advertised = info.GetProperty("capabilities").EnumerateArray().Any(c => c.GetString() == "screenShare");
                var state = (await router.RouteAsync("GET", "/m1/screen-share/state", null, "phone", CancellationToken.None)).Status;
                var session = (await router.RouteAsync("POST", "/m1/screen-share/sessions", Json(new { mode = "view" }), "phone", CancellationToken.None)).Status;
                return (advertised, state, session);
            }
            using (var off = new MobileDesktopRouter(service, Path.Combine(directory, "off"), "Fixture", (_, _) => Task.FromResult<object?>(null), _ => new([])) { ScreenShareAvailable = false })
            {
                var probe = await Probe(off);
                Check(!probe.Advertised && probe.State == 503 && probe.Session == 503 && routed == 0, "screen share switched off is neither advertised nor routed");
            }
            using (var on = new MobileDesktopRouter(service, Path.Combine(directory, "on"), "Fixture", (_, _) => Task.FromResult<object?>(null), _ => new([])) { ScreenShareAvailable = true })
            {
                var probe = await Probe(on);
                Check(probe.Advertised && probe.State == 200 && probe.Session == 200 && routed == 2, "screen share switched on is advertised and routed");
            }
            using (var standard = new MobileDesktopRouter(service, Path.Combine(directory, "standard"), "Fixture", (_, _) => Task.FromResult<object?>(null), _ => new([])))
                Check((await Probe(standard)).Advertised == ScreenShareAvailability.Enabled, "a router built with the defaults follows the one switch");
            Check(ScreenShareAvailability.Extra(() => ["screenShare", "other"], false).SequenceEqual(["other"]) && ScreenShareAvailability.Extra(() => ["screenShare"], true).SequenceEqual(["screenShare"]) && ScreenShareAvailability.Extra(null, true).Length == 0, "the host's extra capabilities drop screenShare only while it is off");
        }
        finally { Directory.Delete(directory, true); }
    }
    private static async Task Send(WebSocket socket, JsonElement value, CancellationToken token) => await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(value, Wire.Json), WebSocketMessageType.Text, true, token);
    internal static async Task DisposedHostCannotRestart()
    {
        var directory = Temp();
        try
        {
            await using var host = new MobileRelayHost(directory, "Fixture", [], (_, _, _, _, _) => Task.FromResult(new MobileReply(200, new { ok = true }))) { WatchesNetwork = false };
            var pairing = host.PairingUrl;
            await host.DisposeAsync(); await host.DisposeAsync();
            foreach (var operation in new Func<Task>[] { host.ReconnectAsync, () => host.ApplyAsync(new()), () => host.RotateKeyAsync() })
            {
                var rejected = false; try { await operation(); } catch (ObjectDisposedException) { rejected = true; }
                Check(rejected, "disposed relay host must reject reconnect, apply and key rotation");
            }
            Check(!host.Status.Connected && host.Devices.Count == 0 && host.PairingUrl == pairing, "shutdown retains identity and cannot reopen sockets");
        }
        finally { Directory.Delete(directory, true); }
    }
    private static async Task<byte[]> Receive(WebSocket socket, CancellationToken token)
    {
        using var result = new MemoryStream(); var buffer = new byte[8192]; WebSocketReceiveResult frame;
        do { frame = await socket.ReceiveAsync(buffer, token); if (frame.MessageType == WebSocketMessageType.Close) throw new IOException("fixture closed"); result.Write(buffer, 0, frame.Count); } while (!frame.EndOfMessage); return result.ToArray();
    }
}
