using System.Buffers.Text;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Parameters;
using BcChaCha = Org.BouncyCastle.Crypto.Modes.ChaCha20Poly1305;

namespace MightyClaude.Core;

/// <summary>The push kinds, as the wire spells them (M/MightyCore/Remote/MobilePush.swift MobilePushKind).</summary>
public static class MobilePushKinds
{
    public const string Completed = "completed", Failed = "failed", Permission = "permission", Question = "question", Plan = "plan", Signin = "signin";
    public static readonly string[] All = [Completed, Failed, Permission, Question, Plan, Signin];
}

/// <summary>The buttons a permission push carries (MobilePushButtons); the phone draws them, the host still decides.</summary>
public static class MobilePushButtonKinds
{
    public const string Claude = "claude", Antigravity = "antigravity", AntigravityOnce = "antigravityOnce", None = "none";
}

/// <summary>
/// The seal on a push, the Mac's MobilePushSeal (docs/mobile-remote.md "푸시 알림"): key = HKDF-SHA256(ikm: the 32 raw bytes of
/// SHA-256(UTF-8 device token) — the stored TokenHash —, salt: UTF-8 clientId, info: "mighty-push-v1", 32 bytes); envelope =
/// base64url(0x01 ‖ nonce (12) ‖ ChaCha20-Poly1305 ciphertext ‖ tag (16)), AAD "mighty-push-v1:" + handle.
/// </summary>
public static class MobilePushSeal
{
    public const string Info = "mighty-push-v1";
    public const byte Version = 1;
    public const int MaximumPlaintext = 2048, MaximumEnvelope = 3072;
    /// <summary>The key, or null when the hash is not 64 ASCII hex digits (either case).</summary>
    public static byte[]? Key(string tokenHash, string clientId)
        => Regex.IsMatch(tokenHash, "^[0-9a-fA-F]{64}\\z") ? HKDF.DeriveKey(HashAlgorithmName.SHA256, Convert.FromHexString(tokenHash), 32, Encoding.UTF8.GetBytes(clientId), Encoding.UTF8.GetBytes(Info)) : null;
    private static byte[] Aad(string handle) => Encoding.UTF8.GetBytes(Info + ":" + handle);
    public static string Seal(byte[] plaintext, byte[] key, string handle, byte[]? nonce = null)
    {
        if (plaintext.Length > MaximumPlaintext) throw new ArgumentException("Push message too long.", nameof(plaintext));
        nonce ??= RandomNumberGenerator.GetBytes(12);
        var cipher = new BcChaCha();
        cipher.Init(true, new AeadParameters(new KeyParameter(key), 128, nonce, Aad(handle)));
        var output = new byte[cipher.GetOutputSize(plaintext.Length)];
        var length = cipher.ProcessBytes(plaintext, 0, plaintext.Length, output, 0);
        length += cipher.DoFinal(output, length);
        var envelope = new byte[13 + length];
        envelope[0] = Version; nonce.CopyTo(envelope, 1); Array.Copy(output, 0, envelope, 13, length);
        return Base64Url.EncodeToString(envelope);
    }
    public static byte[]? Open(string envelope, byte[] key, string handle)
    {
        if (envelope.Length > MaximumEnvelope || !Regex.IsMatch(envelope, "^[A-Za-z0-9_-]*\\z")) return null;
        byte[] bytes;
        try { bytes = Base64Url.DecodeFromChars(envelope); } catch (FormatException) { return null; }
        if (bytes.Length < 1 + 12 + 16 || bytes[0] != Version) return null;
        try
        {
            var cipher = new BcChaCha();
            cipher.Init(false, new AeadParameters(new KeyParameter(key), 128, bytes[1..13], Aad(handle)));
            var output = new byte[cipher.GetOutputSize(bytes.Length - 13)];
            var length = cipher.ProcessBytes(bytes, 13, bytes.Length - 13, output, 0);
            length += cipher.DoFinal(output, length);
            return output[..length];
        }
        catch (InvalidCipherTextException) { return null; }
    }
}

public sealed record MobilePushRequest(string Id, string RunId, string Kind, string Summary, string Buttons)
{
    /// <summary>A pending card as the phone sees it, read as a push (M/MobilePushRequest.init(_ permission:)).</summary>
    public static MobilePushRequest From(MobilePermission permission)
    {
        if (permission.Antigravity is { } card)
        {
            var ruled = card.Actions.Where(a => a.Rule is not null).ToArray();
            var buttons = !permission.CanAllow || ruled.Length == 0 ? MobilePushButtonKinds.None : ruled.Any(a => a.OnceOnly == true) ? MobilePushButtonKinds.AntigravityOnce : MobilePushButtonKinds.Antigravity;
            return new(permission.Id, permission.RunId, MobilePushKinds.Permission, permission.Summary, buttons);
        }
        if (permission.Questionnaire is not null || permission.ToolName == "AskUserQuestion") return new(permission.Id, permission.RunId, MobilePushKinds.Question, permission.Summary, MobilePushButtonKinds.None);
        if (permission.Plan is not null || permission.ToolName == ClaudePlanMode.ToolName) return new(permission.Id, permission.RunId, MobilePushKinds.Plan, permission.Summary, MobilePushButtonKinds.None);
        return new(permission.Id, permission.RunId, MobilePushKinds.Permission, permission.Summary, permission.CanAllow ? MobilePushButtonKinds.Claude : MobilePushButtonKinds.None);
    }
}

/// <summary>One pane as the planner sees it; Status is the pane's own run status (running, completed, error, stopped, …), not the phone's display status.</summary>
public sealed record MobilePushPane(string SessionId, string Workspace, string Title, string Status, string? Preview, IReadOnlyList<MobilePushRequest> Requests, bool SignInLost);
public sealed record MobilePushPermission(string RequestId, string RunId, string Buttons);

/// <summary>The plaintext inside a push envelope.</summary>
public sealed record MobilePushMessage(string Id, string Kind, string HostId, string HostName, string Workspace, string Title, string Summary, string SessionId, MobilePushPermission? Permission, string At)
{
    public int V { get; init; } = 1;

    /// <summary>
    /// UTF-8 JSON within MaximumPlaintext; the summary goes first when it does not fit. Null means it does not fit even without
    /// the summary: skip this push and log its Id only. The limit is measured on the bytes the Mac's JSONEncoder writes.
    /// </summary>
    public byte[]? EncodedForPush()
    {
        var data = Encode(this);
        if (data.Length <= MobilePushSeal.MaximumPlaintext) return data;
        data = Encode(this with { Summary = "" });
        return data.Length <= MobilePushSeal.MaximumPlaintext ? data : null;
    }

    /// <summary>Printing shows only ids and kinds: never what the notification says.</summary>
    public override string ToString() => $"MobilePushMessage {{ Id = {Id}, Kind = {Kind}, SessionId = {SessionId} }}";

    /// <summary>
    /// The JSON Swift's JSONEncoder writes for this message, keys in declaration order: '"', '\', '/' and the C0 controls are
    /// escaped (\b \f \n \r \t short, the rest \u00xx), everything else — DEL, U+2028, emoji — goes out as UTF-8.
    /// System.Text.Json escapes otherwise (emoji as surrogate pairs, DEL, never '/'), so its byte count would not be the Mac's.
    /// </summary>
    private static byte[] Encode(MobilePushMessage message)
    {
        var json = new StringBuilder("{\"v\":").Append(message.V.ToString(CultureInfo.InvariantCulture));
        void Field(string name, string value, bool first = false)
        {
            json.Append(first ? "\"" : ",\"").Append(name).Append("\":\"");
            foreach (var c in value)
                json.Append(c switch
                {
                    '"' => "\\\"", '\\' => "\\\\", '/' => "\\/", '\b' => "\\b", '\f' => "\\f", '\n' => "\\n", '\r' => "\\r", '\t' => "\\t",
                    < ' ' => "\\u" + ((int)c).ToString("x4", CultureInfo.InvariantCulture),
                    _ => c.ToString(),
                });
            json.Append('"');
        }
        Field("id", message.Id); Field("kind", message.Kind); Field("hostId", message.HostId); Field("hostName", message.HostName);
        Field("workspace", message.Workspace); Field("title", message.Title); Field("summary", message.Summary); Field("sessionId", message.SessionId);
        if (message.Permission is { } permission)
        {
            json.Append(",\"permission\":{");
            Field("requestId", permission.RequestId, first: true); Field("runId", permission.RunId); Field("buttons", permission.Buttons);
            json.Append('}');
        }
        Field("at", message.At);
        return Encoding.UTF8.GetBytes(json.Append('}').ToString());
    }
}

/// <summary>
/// Decides what a host pushes from what its panes show now and what they showed last time (M/MobilePushPlanner; the rules are in
/// docs/mobile-remote.md "푸시 알림"). Not thread-safe: one caller observes at a time.
/// </summary>
public sealed class MobilePushPlanner
{
    public const int SentMemory = 512;
    /// <summary>How many sessions' run and sign-in numbers are kept, absent panes oldest first out.</summary>
    public const int CountMemory = 512;
    private sealed record Memory(string Status, HashSet<string> Requests, bool SignInLost);
    /// <summary>Kept across observations a pane misses, so its ids keep counting up instead of repeating sent ones.</summary>
    private sealed class Count { public int Runs, Signins; public long Seen; }
    private bool baseline;
    private Dictionary<string, Memory> memory = [];
    private readonly Dictionary<string, Count> counts = [];
    private long observation;
    private readonly Queue<string> sent = new();
    private readonly HashSet<string> sentSet = [];

    /// <summary>
    /// Starts over from the next observation, which only sets the baseline: what changed while nothing was watched is not pushed.
    /// Run and sign-in numbers and the sent ids are kept, so an id already pushed is never reused.
    /// </summary>
    public void Reset() { baseline = false; memory = []; }

    public IReadOnlyList<MobilePushMessage> Observe(IReadOnlyList<MobilePushPane> panes, string hostId, string hostName, string at)
    {
        var messages = new List<MobilePushMessage>(); var next = new Dictionary<string, Memory>();
        observation++;
        foreach (var pane in panes)
        {
            memory.TryGetValue(pane.SessionId, out var previous);
            if (!counts.TryGetValue(pane.SessionId, out var count)) counts[pane.SessionId] = count = new Count();
            count.Seen = observation;
            if (baseline)
            {
                foreach (var request in pane.Requests)
                {
                    var id = RequestId(request);
                    if (previous?.Requests.Contains(id) == true) continue;
                    Emit(messages, pane, request.Kind, id, request.Summary, request.Kind == MobilePushKinds.Permission ? new(request.Id, request.RunId, request.Buttons) : null, hostId, hostName, at);
                }
                if (previous?.Status == "running" && pane.Requests.Count == 0 && (pane.Status == "completed" || pane.Status == "error"))
                {
                    count.Runs++;
                    var kind = pane.Status == "completed" ? MobilePushKinds.Completed : MobilePushKinds.Failed;
                    Emit(messages, pane, kind, $"run:{pane.SessionId}:{count.Runs}:{kind}", pane.Preview ?? "", null, hostId, hostName, at);
                }
                if (pane.SignInLost && previous?.SignInLost != true)
                {
                    count.Signins++;
                    Emit(messages, pane, MobilePushKinds.Signin, $"signin:{pane.SessionId}:{count.Signins}", "", null, hostId, hostName, at);
                }
            }
            next[pane.SessionId] = new(pane.Status, pane.Requests.Select(RequestId).ToHashSet(), pane.SignInLost);
        }
        if (counts.Count > CountMemory)
            foreach (var sessionId in counts.Where(entry => entry.Value.Seen != observation).OrderBy(entry => entry.Value.Seen).Take(counts.Count - CountMemory).Select(entry => entry.Key).ToArray())
                counts.Remove(sessionId);
        memory = next; baseline = true;
        return messages;
    }

    private static string RequestId(MobilePushRequest request) => $"request:{request.RunId}:{request.Id}";

    private void Emit(List<MobilePushMessage> messages, MobilePushPane pane, string kind, string id, string summary, MobilePushPermission? permission, string hostId, string hostName, string at)
    {
        if (!sentSet.Add(id)) return;
        sent.Enqueue(id);
        if (sent.Count > SentMemory) sentSet.Remove(sent.Dequeue());
        messages.Add(new(id, kind, hostId, Clip(hostName, 80), Clip(pane.Workspace, 80), Clip(pane.Title, 80), Clip(OneLine(summary), 200), pane.SessionId, permission, at));
    }

    /// <summary>The first <paramref name="limit"/> Unicode scalars.</summary>
    internal static string Clip(string text, int limit) => string.Concat(text.EnumerateRunes().Take(limit).Select(rune => rune.ToString()));

    /// <summary>Line breaks become spaces, then spaces and tabs (Swift's .whitespaces: Zs and U+0009) are trimmed from both ends.</summary>
    internal static string OneLine(string text)
    {
        var line = text.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');
        static bool Blank(char c) => c == '\t' || char.GetUnicodeCategory(c) == UnicodeCategory.SpaceSeparator;
        int start = 0, end = line.Length;
        while (start < end && Blank(line[start])) start++;
        while (end > start && Blank(line[end - 1])) end--;
        return line[start..end];
    }
}

public sealed record MobilePushDevice(string DeviceId, string Handle, string TokenHash, IReadOnlySet<string> Kinds)
{
    /// <summary>Printing shows only the device id and its kinds: never the handle or the token hash.</summary>
    public override string ToString() => $"MobilePushDevice {{ DeviceId = {DeviceId}, Kinds = [{string.Join(", ", Kinds.Order(StringComparer.Ordinal))}] }}";
}

/// <summary>"This phone has this session on screen", until <c>Until</c>.</summary>
public sealed record MobilePushViewing(string SessionId, DateTimeOffset Until);

public static class MobilePushRecipients
{
    public static readonly TimeSpan ViewingWindow = TimeSpan.FromSeconds(45);
    public static IReadOnlyList<MobilePushDevice> Targets(MobilePushMessage message, IEnumerable<MobilePushDevice> devices, IReadOnlyDictionary<string, MobilePushViewing> viewing, DateTimeOffset now)
        => devices.Where(d => d.Kinds.Contains(message.Kind) && !(viewing.TryGetValue(d.DeviceId, out var seen) && seen.SessionId == message.SessionId && seen.Until > now)).ToArray();
}

public static class MobilePushWire
{
    /// <summary>A relay push handle: 32 random bytes, base64url without padding.</summary>
    public static bool ValidHandle(string value) => Regex.IsMatch(value, "^[A-Za-z0-9_-]{43}\\z");
    /// <summary>The kinds this host knows, in its own order; a newer phone's unknown names are left out, not refused.</summary>
    public static IReadOnlyList<string> Kinds(IEnumerable<string> values) { var wanted = values.ToHashSet(); return MobilePushKinds.All.Where(wanted.Contains).ToArray(); }
}

public static class MobilePushRelay
{
    /// <summary>The relay's push route next to the WebSocket the host already dials.</summary>
    public static Uri? SendUri(string relay)
    {
        if (!Uri.TryCreate(relay, UriKind.Absolute, out var uri) || uri.Host.Length == 0) return null;
        var scheme = uri.Scheme.ToLowerInvariant() switch { "wss" or "https" => "https", "ws" or "http" => "http", _ => null };
        if (scheme is null) return null;
        return new UriBuilder(scheme, uri.Host, uri.IsDefaultPort ? -1 : uri.Port, "/push/v1/send").Uri;
    }
}

public enum MobilePushSendStatus { Sent, Gone, Retry, Failed }

/// <summary>The relay's answer. Gone: the relay no longer knows the handle, it is another host's, or FCM dropped the token.</summary>
public sealed record MobilePushSendOutcome(MobilePushSendStatus Status, TimeSpan? RetryAfter = null, int Code = 0)
{
    public static MobilePushSendOutcome FromStatus(int status, TimeSpan? retryAfter) => status switch
    {
        >= 200 and <= 299 => new(MobilePushSendStatus.Sent, null, status),
        403 or 404 or 410 => new(MobilePushSendStatus.Gone, null, status),
        429 or 500 or 502 or 504 => new(MobilePushSendStatus.Retry, retryAfter, status),
        _ => new(MobilePushSendStatus.Failed, null, status),
    };
}

/// <summary><c>POST /push/v1/send</c> on the relay: the payload is the sealed envelope, never plaintext.</summary>
public sealed record MobilePushSendRequest(string ServerId, string Handle, string Payload)
{
    /// <summary>Printing shows neither the handle nor the envelope.</summary>
    public override string ToString() => $"MobilePushSendRequest {{ Payload = {Payload.Length} characters }}";
}

public interface IMobilePushTransport
{
    Task<MobilePushSendOutcome> SendAsync(MobilePushSendRequest request, Uri url, string hostToken, CancellationToken token);
}

/// <summary>
/// Sends over HTTP. A relay that answers 3xx is not followed: the bearer token and the envelope go to the relay the user set and
/// nowhere else, and the 3xx itself comes back as Failed.
/// </summary>
public sealed class MobilePushHttpTransport : IMobilePushTransport
{
    /// <summary>The longest Retry-After taken from the relay, which clamps its own to the same.</summary>
    public static readonly TimeSpan MaximumRetryAfter = TimeSpan.FromHours(1);
    private static readonly HttpClient Client = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(15) };

    public async Task<MobilePushSendOutcome> SendAsync(MobilePushSendRequest request, Uri url, string hostToken, CancellationToken token)
    {
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(request, Wire.Json)) };
            message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", hostToken);
            using var response = await Client.SendAsync(message, token);
            var header = response.Headers.NonValidated.TryGetValues("Retry-After", out var values) ? values.ToString() : null;
            return MobilePushSendOutcome.FromStatus((int)response.StatusCode, RetryAfter(header));
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            Trace.TraceInformation("mobile push: relay unreachable (" + ex.GetType().Name + ")");
            return new(MobilePushSendStatus.Retry);
        }
    }

    /// <summary>Seconds only (an HTTP date counts as absent), finite, not negative, at most <see cref="MaximumRetryAfter"/>.</summary>
    internal static TimeSpan? RetryAfter(string? header)
    {
        if (header is null || !double.TryParse(header.Trim(' ', '\t'), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) || !double.IsFinite(seconds) || seconds < 0) return null;
        return TimeSpan.FromSeconds(Math.Min(seconds, MaximumRetryAfter.TotalSeconds));
    }
}
