using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MightyClaude.Core;

internal static class ScreenShareVerification
{
    private const string Phone = "abcdefghijklmnopqrstuv";
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value, Wire.Json);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed class Platform : IScreenSharePlatform
    {
        public bool Available { get; set; } = true;
        public IReadOnlyList<ScreenDisplay> Displays => [new(1, 1920, 1080, true), new(2, 1280, 1024, false, -1280, 0)];
        public ScreenSafety Safety { get; set; } = new(false, false, DateTimeOffset.UtcNow.AddMinutes(-1));
        internal readonly List<string> Started = [], Stopped = [], Inputs = [], Released = [];
        internal string Clipboard = "";
        internal int Halts;
        public Task StartPeerAsync(ScreenLiveSession session, IReadOnlyList<ScreenIceServer> ice, CancellationToken token) { token.ThrowIfCancellationRequested(); Started.Add(session.SessionId); return Task.CompletedTask; }
        public Task ReceiveSignalAsync(string id, JsonElement signal, CancellationToken token) => Task.CompletedTask;
        public Task StopPeerAsync(string id) { Stopped.Add(id); return Task.CompletedTask; }
        public void Halt() => Halts++;
        public Task SetDisplayAsync(int id, CancellationToken token) => Task.CompletedTask;
        public void SetRegion(ScreenRegion region) { }
        public Task InjectAsync(string id, JsonElement value, CancellationToken token) { Inputs.Add(value.Text("t")!); return Task.CompletedTask; }
        public Task<bool> ShowMarkerAsync(string id, JsonElement value, CancellationToken token) => Task.FromResult(true);
        public Task ReleaseButtonsAsync(string id) { Released.Add(id); return Task.CompletedTask; }
        public Task<string?> ReadClipboardAsync(CancellationToken token) => Task.FromResult<string?>(Clipboard);
        public Task WriteClipboardAsync(string text, CancellationToken token) { Clipboard = text; return Task.CompletedTask; }
        public Task SendDataAsync(string id, object value, CancellationToken token) => Task.CompletedTask;
    }
    private static byte[] Public(ECDsa key) { var q = key.ExportParameters(false).Q; return [4, .. q.X!, .. q.Y!]; }
    private static async Task<string> Start(ScreenShareHub hub, string phone = Phone, string mode = "view", string? signature = null)
    {
        var reply = await hub.RouteAsync("POST", "sessions", Json(new { mode, network = "wifi", displayId = 1, decodes = new[] { "H264" }, controlSignatureB64 = signature }), phone, default);
        Check(reply.Status == 200, "authorized session accepted"); var id = Json(reply.Body).Text("sessionId")!;
        await reply.AfterSend!(); hub.PeerConnected(id); return id;
    }
    internal static Task PolicyAndWireBoundaries()
    {
        Check(!ScreenSharePolicy.GrantAllows(new(Phone), "view"), "fresh phone has no screen grant");
        Check(ScreenSharePolicy.GrantAllows(new(Phone, true, "control"), "view"), "control includes viewing");
        Check(!ScreenSharePolicy.GrantAllows(new(Phone, true, "view"), "control"), "view does not include input");
        Check(ScreenControlWire.Parse("{\"t\":\"tap\",\"displayId\":1,\"x\":1e999,\"y\":0,\"button\":\"left\"}") is null, "non-finite coordinate rejected");
        Check(ScreenControlWire.Parse(Json(new { t = "key", combo = "cmd+cmd+c" }).GetRawText()) is null, "duplicate modifiers rejected");
        Check(ScreenControlWire.Parse(Json(new { t = "key", combo = "win+r" }).GetRawText()) is null, "OS keycodes and unknown modifiers rejected");
        Check(ScreenControlWire.Parse(Json(new { t = "text", text = new string('한', 1366) }).GetRawText()) is null, "text limit measured in UTF-8 bytes");
        Check(ScreenControlWire.Parse(Json(new { t = "text", text = "한글 😀" }).GetRawText()) is not null, "committed Unicode text accepted");
        Check(ScreenRegion.Parse(Json(new { x = -.1, y = .9, width = .5, height = .2 })) == new ScreenRegion(0, .9, .4, .09999999999999998), "zoom clips to display bounds");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256); var publicKey = Public(key); var challenge = RandomNumberGenerator.GetBytes(40);
        var signature = key.SignData(challenge, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        Check(ScreenSharePolicy.Verify(challenge, signature, publicKey), "P-256 DER matches mobile Secure Enclave/Keystore protocol"); signature[^1] ^= 1;
        Check(!ScreenSharePolicy.Verify(challenge, signature, publicKey), "altered signature refused");
        Check(!ScreenSharePolicy.ValidKey(new byte[65]), "invalid curve point refused"); return Task.CompletedTask;
    }
    internal static Task ClipboardBoundsAndReplay()
    {
        var now = DateTimeOffset.UtcNow; var text = string.Concat(Enumerable.Repeat("한글 👨‍👩‍👧‍👦\n", 4000)); var receiver = new ScreenClipboard(); string? decoded = null;
        foreach (var frame in ScreenClipboard.Encode(text)) { var node = JsonSerializer.SerializeToNode(frame, Wire.Json)!; node["dir"] = "to-mac"; decoded = receiver.Receive(JsonSerializer.SerializeToElement(node), now); }
        Check(decoded == text, "bounded zstd clipboard round trips full Unicode");
        var raw = new { t = "clipboard", dir = "to-mac", id = "a", enc = "raw", bytes = 2, seq = 0, total = 2, data = "YQ==" };
        Check(receiver.Receive(Json(raw), now) is null, "partial clipboard is not published");
        Check(receiver.Receive(Json(raw with { seq = 1, data = "Yg==" }), now.AddSeconds(31)) is null, "expired assembly refused");
        Check(receiver.Receive(Json(raw), now) is null, "new transfer starts");
        Check(receiver.Receive(Json(raw with { seq = 1, bytes = 1 }), now) is null, "metadata change invalidates assembly");
        Check(receiver.Receive(Json(raw with { seq = 1, data = "Yg==" }), now) is null, "invalidated transfer cannot resume");
        var compressed = JsonSerializer.SerializeToNode(ScreenClipboard.Encode(new string('x', 50000))[0], Wire.Json)!; compressed["dir"] = "to-mac"; compressed["bytes"] = 1;
        Check(receiver.Receive(JsonSerializer.SerializeToElement(compressed), now) is null, "zstd expansion cannot exceed declared output bound");
        Check(Json(ScreenClipboard.Encode(null)[0]).GetProperty("concealed").GetBoolean(), "concealed source emits no plaintext");
        Check(ScreenClipboard.Encode(new string('x', ScreenClipboard.MaximumBytes + 1)).Count == 0, "oversize clipboard never leaves host"); return Task.CompletedTask;
    }
    internal static async Task EnrollmentChallengeAndResponseOrdering()
    {
        var directory = Directory.CreateTempSubdirectory("mighty-screen-").FullName; var platform = new Platform();
        try
        {
            await using var hub = new ScreenShareHub(directory, platform, (_, _) => Task.CompletedTask, (_, _, _) => Task.FromResult(true), p => p == Phone);
            Check((await hub.RouteAsync("POST", "sessions", Json(new { mode = "view", network = "wifi", displayId = 1 }), Phone, default)).Status == 403, "pairing alone never authorizes pixels");
            await hub.SetGrantAsync(Phone, true, "control"); using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256); var pub = Convert.ToBase64String(Public(key));
            Check((await hub.RouteAsync("POST", "control-key", Json(new { publicKeyB64 = pub }), Phone, default)).Status == 200, "host confirms new control key");
            using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            Check((await hub.RouteAsync("POST", "control-key", Json(new { publicKeyB64 = Convert.ToBase64String(Public(other)) }), Phone, default)).Status == 409, "phone cannot silently replace enrolled identity");
            var state = Json((await hub.RouteAsync("GET", "state", null, Phone, default)).Body).GetProperty("screenShare");
            var challenge = Convert.FromBase64String(state.Text("controlChallengeB64")!); var signature = Convert.ToBase64String(key.SignData(challenge, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
            var request = Json(new { mode = "control", network = "wifi", displayId = 1, controlSignatureB64 = signature });
            var reply = await hub.RouteAsync("POST", "sessions", request, Phone, default); Check(reply.Status == 200 && platform.Started.Count == 0, "peer offer waits until m1 response has been sent"); await reply.AfterSend!();
            Check(platform.Started.Count == 1, "response completion starts peer exactly once"); await hub.KillAllAsync();
            Check((await hub.RouteAsync("POST", "sessions", request, Phone, default)).Status == 403, "consumed biometric challenge cannot be replayed");
            await hub.RemoveControlKeyAsync(Phone); Check(hub.Grants.Single().ControlKeyPublic is null, "remove key preserves grant but requires local enrollment again");
        }
        finally { Directory.Delete(directory, true); }
    }
    internal static async Task RevocationAndInputSafety()
    {
        var directory = Directory.CreateTempSubdirectory("mighty-screen-").FullName; var platform = new Platform(); var trusted = true;
        try
        {
            await using var hub = new ScreenShareHub(directory, platform, (_, _) => Task.CompletedTask, (_, _, _) => Task.FromResult(true), _ => trusted);
            await hub.SetGrantAsync(Phone, true, "control"); using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            await hub.RouteAsync("POST", "control-key", Json(new { publicKeyB64 = Convert.ToBase64String(Public(key)) }), Phone, default);
            var state = Json((await hub.RouteAsync("GET", "state", null, Phone, default)).Body).GetProperty("screenShare");
            var signature = Convert.ToBase64String(key.SignData(Convert.FromBase64String(state.Text("controlChallengeB64")!), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
            var id = await Start(hub, mode: "control", signature: signature); var input = Json(new { t = "text", text = "한글" }).GetRawText();
            await hub.InputAsync(id, input); Check(platform.Inputs.Count == 1, "approved control delivers input");
            platform.Safety = platform.Safety with { LastLocalInput = DateTimeOffset.UtcNow }; await hub.InputAsync(id, input); Check(platform.Inputs.Count == 1, "physical input pauses remote control");
            await hub.InputAsync(id, Json(new { t = "drag", phase = "end", displayId = 1, x = .1, y = .1 }).GetRawText()); Check(platform.Released.Contains(id), "drag release is honored even during a safety pause");
            platform.Safety = platform.Safety with { LastLocalInput = DateTimeOffset.UtcNow.AddMinutes(-1) }; trusted = false; await hub.InputAsync(id, input); Check(platform.Inputs.Count == 1, "revoked relay identity immediately blocks input"); trusted = true;
            await hub.SetGrantAsync(Phone, true, "view"); Check(hub.Sessions.Count == 0 && platform.Stopped.Contains(id), "grant downgrade cancels control before returning");
            var view = await Start(hub); await hub.InputAsync(view, input); Check(platform.Inputs.Count == 1, "view-only data channel cannot inject");
            platform.Safety = platform.Safety with { SecureInput = true };
            await Task.Delay(600); Check(hub.Sessions.Count == 0 && platform.Halts > 0, "password focus closes pixel gate and kills live peers within one second");
        }
        finally { Directory.Delete(directory, true); }
    }
    internal static async Task EnrollmentRaceAndSessionLimits()
    {
        var directory = Directory.CreateTempSubdirectory("mighty-screen-").FullName; var platform = new Platform(); var approval = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); var clock = DateTimeOffset.UtcNow;
        try
        {
            await using var hub = new ScreenShareHub(directory, platform, (_, _) => Task.CompletedTask, (_, _, _) => approval.Task, _ => true, () => clock);
            await hub.SetGrantAsync(Phone, true, "control"); using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var enrollment = hub.RouteAsync("POST", "control-key", Json(new { publicKeyB64 = Convert.ToBase64String(Public(key)) }), Phone, default);
            await hub.SetGrantAsync(Phone, false, "none"); approval.SetResult(true); Check((await enrollment).Status == 403 && hub.Grants.Single().ControlKeyPublic is null, "consent response cannot resurrect a revoked grant");
            foreach (var phone in new[] { Phone, "bbbbbbbbbbbbbbbbbbbbbb", "cccccccccccccccccccccc" }) await hub.SetGrantAsync(phone, true, "view");
            var first = await Start(hub); await Start(hub, "bbbbbbbbbbbbbbbbbbbbbb");
            Check((await hub.RouteAsync("POST", "sessions", Json(new { mode = "view", network = "wifi", displayId = 1 }), "cccccccccccccccccccccc", default)).Status == 403, "third concurrent viewer refused");
            await hub.SignalAsync(Phone, Json(new { type = "screen-background", sessionId = first, background = true }), default); clock = clock.AddSeconds(31); await Task.Delay(400);
            Check(hub.Sessions.Count == 1 && platform.Stopped.Contains(first), "backgrounded phone stops after thirty seconds"); await hub.ResetAsync("rekey-pairing"); Check(hub.Grants.Count == 0 && hub.Sessions.Count == 0, "pairing rotation clears screen trust and active peers");
        }
        finally { Directory.Delete(directory, true); }
    }
    internal static async Task TeardownIgnoresBrokenRelayAndPersistence()
    {
        var directory = Directory.CreateTempSubdirectory("mighty-screen-").FullName; var platform = new Platform(); var broken = false; var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "screen-devices.json"), "[null,{\"deviceId\":\"" + Phone + "\",\"allowed\":true,\"grant\":\"control\",\"controlKeyPublic\":\"malformed\"}]");
            await using var hub = new ScreenShareHub(directory, platform, (_, message) => broken && Json(message).Text("type") is "screen-kill" or "screen-session-end" ? blocked.Task : Task.CompletedTask, (_, _, _) => Task.FromResult(true), _ => true);
            Check(hub.Grants.Count == 0, "malformed persisted identity never enables screen access");
            await hub.SetGrantAsync(Phone, true, "view"); var id = await Start(hub); broken = true;
            var revoke = hub.SetGrantAsync(Phone, false, "none"); await Task.Delay(60);
            Check(platform.Stopped.Contains(id) && hub.Sessions.Count == 0, "native pixels close before blocked relay notification completes"); await revoke.WaitAsync(TimeSpan.FromSeconds(2));
            await hub.SetGrantAsync(Phone, true, "view"); platform.Safety = new(false, false, DateTimeOffset.UtcNow.AddMinutes(-1)); await Start(hub);
            platform.Safety = platform.Safety with { SecureInput = true }; await Task.Delay(1500);
            Check(hub.Sessions.Count == 0, "privacy watcher survives blocked relay notification");
            platform.Safety = platform.Safety with { SecureInput = false }; var again = await Start(hub); platform.Safety = platform.Safety with { Locked = true }; await Task.Delay(1500);
            Check(platform.Stopped.Contains(again) && hub.Sessions.Count == 0, "privacy watcher continues enforcing after earlier teardown failure"); blocked.TrySetException(new IOException("fixture disconnected"));
        }
        finally { blocked.TrySetResult(); Directory.Delete(directory, true); }
        directory = Directory.CreateTempSubdirectory("mighty-screen-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "screen-devices.json"), "invalid json");
            await using var hub = new ScreenShareHub(directory, new Platform(), (_, _) => Task.CompletedTask, (_, _, _) => Task.FromResult(false), _ => true);
            Check(hub.Grants.Count == 0, "corrupted persistence starts with no grants");
        }
        finally { Directory.Delete(directory, true); }
    }

}
