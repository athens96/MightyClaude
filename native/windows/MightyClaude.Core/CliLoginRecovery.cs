using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MightyClaude.Core;

/// Only failed provider events qualify; ordinary assistant text is never a login signal.
public static class CliAuthFailure
{
    public static bool IsAuthenticationFailure(string text) => Claude(text) || Codex(text);
    /// <summary>The providers whose lost sign-in raises the card and may start a sign-in.</summary>
    public static readonly IReadOnlyList<string> Providers = ["claude", "codex", "gemini"];
    private static string Bounded(string text) => text[..Math.Min(text.Length, 16_384)].ToLowerInvariant();
    private static bool ExternalDenial(string text) => text.Contains("bedrock") || text.Contains("service control policy") || text.Contains("explicit deny");
    public static bool Claude(string text)
    {
        var value = Bounded(text);
        return !ExternalDenial(value) && (new[] { "please run /login", "oauth token has expired", "oauth token revoked", "oauth session expired", "invalid api key" }.Any(value.Contains)
            || value.Contains("401") && value.Contains("authentication_error"));
    }
    public static bool Codex(string text)
    {
        var value = Bounded(text);
        return !ExternalDenial(value) && !value.TrimStart().StartsWith("reconnecting", StringComparison.Ordinal)
            && (value.Contains("401") && value.Contains("unauthorized") || new[] { "not logged in", "please log in", "please login", "log in again", "sign in again", "refresh token" }.Any(value.Contains));
    }
    /// <summary>
    /// Antigravity CLI's own sign-in failures, in its own words, on stderr, a plain stdout line or a failed result
    /// (M/CLILoginRecovery.swift antigravity(text:)): "Error: authentication required. Run 'agy' to log in, then retry." and
    /// "Print mode: auth error: …". A sign-in that timed out, or a proxy's "407 Proxy Authentication Required", is not a lost sign-in.
    /// </summary>
    public static bool Antigravity(string text)
    {
        var value = Bounded(text);
        return value.Contains("authentication required. run ", StringComparison.Ordinal) || value.Contains("print mode: auth error", StringComparison.Ordinal);
    }
    /// <summary>An Antigravity stream-json line that is the run's successful result: it clears an earlier sign-in signal, as on the Mac.</summary>
    public static bool AntigravitySucceeded(string line)
    {
        if (!line.Contains("\"result\"", StringComparison.Ordinal) || !line.TrimStart().StartsWith('{')) return false;
        try
        {
            using var json = JsonDocument.Parse(line); var root = json.RootElement;
            return root.ValueKind == JsonValueKind.Object && root.Text("event") == "result" && MetadataJson.Property(root, "result").Text("status")?.ToUpperInvariant() == "SUCCESS";
        }
        catch (JsonException) { return false; }
    }
    /// <summary>Whether a run's failure text is its provider's lost sign-in.</summary>
    public static bool Matches(string provider, string text) => provider switch
    {
        "claude" => Claude(text),
        "codex" => Codex(text),
        "gemini" => Antigravity(text),
        _ => false,
    };
    public static bool Claude(JsonElement value)
    {
        var type = value.Text("type");
        if (type == "assistant" && value.Text("error") is { Length: > 0 } error)
        {
            var texts = AssistantTexts(value);
            return !texts.Any(text => ExternalDenial(Bounded(text))) && (error == "authentication_failed" || texts.Any(Claude));
        }
        if (!IsErrorResult(value)) return false;
        var errors = ResultTexts(value);
        return !errors.Any(text => ExternalDenial(Bounded(text))) && errors.Any(Claude);
    }
    /// <summary>The text of a Claude assistant frame the CLI marked as a lost sign-in (authentication_failed); null otherwise.</summary>
    public static string? ClaudeFailureText(JsonElement value) =>
        value.Text("type") == "assistant" && value.Text("error") == "authentication_failed" && Claude(value) ? string.Join("\n", AssistantTexts(value)).Trim() : null;
    /// <summary>
    /// An error result repeating <paramref name="repeating"/>, the turn's message the CLI marked as a lost sign-in
    /// (M/CLILoginRecovery.swift claude(result:repeating:)): the CLI copies that message into the result, so the result
    /// is the same lost sign-in whatever its words, which change between versions.
    /// </summary>
    public static bool Claude(JsonElement value, string? repeating)
    {
        if (string.IsNullOrEmpty(repeating) || !IsErrorResult(value)) return false;
        var texts = ResultTexts(value);
        return !texts.Any(text => ExternalDenial(Bounded(text))) && texts.Any(text => text.Trim() == repeating);
    }
    private static bool IsErrorResult(JsonElement value) => value.Text("type") == "result"
        && (value.TryGetProperty("is_error", out var failed) && failed.ValueKind == JsonValueKind.True || value.Text("subtype")?.StartsWith("error", StringComparison.Ordinal) == true);
    private static string[] AssistantTexts(JsonElement value) =>
        value.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object && message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array
            ? content.EnumerateArray().Where(block => block.Text("type") == "text").Select(block => block.Text("text") ?? "").ToArray() : [];
    private static List<string> ResultTexts(JsonElement value)
    {
        var texts = value.TryGetProperty("errors", out var items) && items.ValueKind == JsonValueKind.Array
            ? items.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToList() : [];
        if (value.Text("result") is { } result) texts.Add(result);
        return texts;
    }
    /// <summary>
    /// Whether signing in again can fix a run that failed with a sign-in error (M/CLILoginRecovery.swift signInCanFix): a Codex
    /// API-key login cannot. Antigravity's status is a live call: only one that reads signed out asks for a sign-in; unknown
    /// (offline, a timeout) does not open one.
    /// </summary>
    public static bool SignInCanFix(CliAccountStatus status) => Providers.Contains(status.Provider) && status.Installed && status.AccessVerified != false
        && !(status.Provider == "codex" && status.MethodId == CliAccountMethod.CodexApiKey)
        && (status.Provider != "gemini" || status.LoggedIn == false);
}

/// Whether a lost sign-in starts its provider's background sign-in by itself (M/CLILoginRecovery.swift
/// CLIAutoLoginGate). One per provider: none while one already runs, and none for Cooldown after one
/// failed, was cancelled or succeeded, so neither a refused sign-in nor a request failing again right
/// after approval reopens the browser in a loop. A request login recovery itself resent, or one sent
/// before the last sign-in, only raises the card. The card's button is never held by this.
public sealed class CliAutoLoginGate
{
    public static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(2);
    private readonly Dictionary<string, DateTimeOffset> held = [], signedIn = [];
    public bool ShouldStart(string provider, bool enabled, CliAccountStatus status, bool loginActive, DateTimeOffset now, bool resent = false, DateTimeOffset? sentAt = null) =>
        enabled && !loginActive && !resent && CliAuthFailure.Providers.Contains(provider) && status.Provider == provider && CliAuthFailure.SignInCanFix(status)
        && !(sentAt is { } sent && signedIn.TryGetValue(provider, out var confirmed) && sent < confirmed)
        && (!held.TryGetValue(provider, out var at) || now - at >= Cooldown);
    /// <summary>A sign-in of the provider failed, timed out or was cancelled.</summary>
    public void Stopped(string provider, DateTimeOffset now) => held[provider] = now;
    /// <summary>Signed in again (in the background, a terminal or Settings): automatic starts wait out the cooldown.</summary>
    public void Succeeded(string provider, DateTimeOffset now) { held[provider] = now; signedIn[provider] = now; }
}

public sealed record CliLoginOutput(Uri? Url = null, bool AsksForCode = false);

/// The bounded output exists in memory only. It never enters run logs or snapshots.
public sealed class CliLoginOutputParser
{
    private readonly StringBuilder raw = new();
    private int bytes;
    public CliLoginOutput Current { get; private set; } = new();
    public const int MaximumBytes = 65_536;
    private static readonly string[] AuthHosts = ["claude.ai", "claude.com", "console.anthropic.com", "auth.openai.com", "chatgpt.com"];
    private static readonly Regex Links = new("https://[^\\s\"'<>`\\x00-\\x1F\\x7F]+", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex Hyperlinks = new("\u001b\\]8;[^;\u0007\u001b]*;([^\u0007\u001b]*)(?:\u0007|\u001b\\\\)", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex Escapes = new("\u001b(?:\\[[0-?]*[ -/]*[@-~]|\\][^\u0007\u001b]*(?:\u0007|\u001b\\\\)|[@-Z\\\\-_])", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    public void Append(string value)
    {
        foreach (var rune in value.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > MaximumBytes) break;
            raw.Append(rune.ToString()); bytes += rune.Utf8SequenceLength;
        }
        Parse(false);
    }
    public void Finish() => Parse(true);
    public void Clear() { raw.Clear(); bytes = 0; Current = new(); }
    public static bool IsAuthUrl(Uri? url) => url is not null && AuthHosts.Any(host => url.Host.Equals(host, StringComparison.OrdinalIgnoreCase) || url.Host.EndsWith("." + host, StringComparison.OrdinalIgnoreCase));
    private void Parse(bool final)
    {
        var text = Escapes.Replace(Hyperlinks.Replace(raw.ToString(), " $1 "), "").Replace('\r', '\n');
        var url = Current.Url;
        if (!IsAuthUrl(url))
        {
            Uri? first = null;
            foreach (Match match in Links.Matches(text))
            {
                if (!final && match.Index + match.Length == text.Length) continue;
                var link = match.Value.TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '}', '>');
                if (Encoding.UTF8.GetByteCount(link) > 8192 || !Uri.TryCreate(link, UriKind.Absolute, out var candidate) || candidate.Scheme != "https" || candidate.Host.Length == 0 || candidate.UserInfo.Length > 0) continue;
                first ??= candidate;
                if (IsAuthUrl(candidate)) { first = candidate; break; }
            }
            url = first ?? url;
        }
        var asks = Current.AsksForCode || text.Split('\n').Any(line => line.Contains("paste", StringComparison.OrdinalIgnoreCase) && line.Contains("code", StringComparison.OrdinalIgnoreCase));
        Current = new(url, asks);
    }
}

/// <summary>Resent: login recovery itself sent the request again after a sign-in. SentAt: when its run started.</summary>
public sealed record CliLoginRetry(StartRunRequest Request, long Generation, bool Resent = false, DateTimeOffset? SentAt = null);

/// A new submission invalidates both its old retry and any pending status check.
public sealed class CliLoginRetryBook
{
    private long serial;
    private readonly Dictionary<string, long> generations = [];
    private readonly Dictionary<string, CliLoginRetry> inflight = [];
    private readonly Dictionary<string, CliLoginRetry> retries = [];
    private readonly HashSet<string> resending = [];
    public IReadOnlyDictionary<string, CliLoginRetry> Requests => retries;
    /// <summary>The next request of this pane is login recovery's own resend (it is reported back later through Sent).</summary>
    public void ExpectResend(string session) => resending.Add(session);
    public void CancelResend(string session) => resending.Remove(session);
    public long Sent(StartRunRequest request, DateTimeOffset? now = null)
    {
        var generation = ++serial; generations[request.SessionId] = generation;
        retries.Remove(request.SessionId); inflight.Remove(request.SessionId);
        var resent = resending.Remove(request.SessionId);
        if (request.Kind == "claude" && CliAuthFailure.Providers.Contains(request.Provider))
            inflight[request.SessionId] = new(request with { Attachments = request.Attachments?.ToArray() }, generation, resent, now ?? DateTimeOffset.UtcNow);
        return generation;
    }
    public CliLoginRetry? Settled(string session) => inflight.Remove(session, out var retry) ? retry : null;
    public bool IsCurrent(CliLoginRetry retry) => generations.GetValueOrDefault(retry.Request.SessionId) == retry.Generation;
    public bool Remember(CliLoginRetry retry)
    {
        if (!IsCurrent(retry)) return false;
        retries[retry.Request.SessionId] = retry; return true;
    }
    public CliLoginRetry? Take(string session) => retries.Remove(session, out var retry) && IsCurrent(retry) ? retry : null;
    public void Drop(string session) { retries.Remove(session); inflight.Remove(session); generations.Remove(session); resending.Remove(session); }
    public void Clear() { retries.Clear(); inflight.Clear(); generations.Clear(); resending.Clear(); }
}

public enum CliLoginOutcome { LoggedIn, Exited, TimedOut }
public sealed record CliLoginWaitResult(CliLoginOutcome Outcome, CliAccountStatus Status);
public static class CliLoginWait
{
    public static async Task<CliLoginWaitResult> RunAsync(Func<bool> running, Func<int?> exitCode,
        Func<CancellationToken, Task<CliAccountStatus>> status, CancellationToken cancellation = default,
        TimeSpan? interval = null, TimeSpan? limit = null, TimeSpan? tick = null)
    {
        var watch = Stopwatch.StartNew(); var wasSignedOut = (await status(cancellation)).LoggedIn == false; var lastCheck = watch.Elapsed;
        var polling = interval ?? TimeSpan.FromSeconds(3); var timeout = limit ?? TimeSpan.FromMinutes(10);
        while (true)
        {
            await Task.Delay(tick ?? TimeSpan.FromMilliseconds(250), cancellation);
            var active = running(); var expired = watch.Elapsed >= timeout;
            if (!wasSignedOut && active && !expired) continue;
            if (wasSignedOut && active && !expired && watch.Elapsed - lastCheck < polling) continue;
            lastCheck = watch.Elapsed; var value = await status(cancellation); cancellation.ThrowIfCancellationRequested();
            var signedIn = value.LoggedIn == true && CliAuthFailure.SignInCanFix(value);
            if (signedIn && (wasSignedOut || !active && exitCode() == 0)) return new(CliLoginOutcome.LoggedIn, value);
            if (!active) return new(CliLoginOutcome.Exited, value);
            if (expired) return new(CliLoginOutcome.TimedOut, value);
        }
    }
}

/// <summary>How the wait on a terminal sign-in ended. StartedSignedOut: the account read as signed out when the wait began.</summary>
public sealed record CliTerminalLoginResult(CliLoginOutcome Outcome, CliAccountStatus Status, bool StartedSignedOut);

/// <summary>
/// The wait after a sign-in was started in a terminal the app cannot read (M/AppStore+CLIAccounts.swift:9-10, 95-124): the
/// account's status is read every 5 s for up to 10 minutes, and the wait ends once it reads as signed in. The wait cannot see
/// the terminal's command end, so only a status that turned from signed out to signed in may send the waiting requests again:
/// signed out is the status held before the terminal opened (<c>before</c>, M/AppStore+CLIAccounts.swift:98), or the first
/// reading when that one says so, since a first reading taken while the sign-in starts may time out and read as unknown.
/// A terminal whose window is known to have closed (<c>closed</c>) ends the wait with one last reading. Cancellation throws.
/// </summary>
public static class CliTerminalLogin
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5), PollLimit = TimeSpan.FromMinutes(10);
    public static bool SignedIn(CliAccountStatus status) => status.LoggedIn == true && status.AccessVerified != false;
    public static bool Resends(CliTerminalLoginResult result) => result.StartedSignedOut && result.Outcome == CliLoginOutcome.LoggedIn;
    public static async Task<CliTerminalLoginResult> RunAsync(Func<CancellationToken, Task<CliAccountStatus>> status, CancellationToken cancellation = default,
        Task? closed = null, TimeSpan? interval = null, TimeSpan? limit = null, CliAccountStatus? before = null)
    {
        var watch = Stopwatch.StartNew(); var polling = interval ?? PollInterval; var timeout = limit ?? PollLimit;
        var first = await status(cancellation); cancellation.ThrowIfCancellationRequested();
        var startedSignedOut = before?.LoggedIn == false || first.LoggedIn == false;
        if (SignedIn(first)) return new(CliLoginOutcome.LoggedIn, first, before?.LoggedIn == false);
        while (true)
        {
            var pause = Task.Delay(polling, cancellation);
            if (closed is null) await pause; else { await Task.WhenAny(pause, closed); cancellation.ThrowIfCancellationRequested(); }
            var value = await status(cancellation); cancellation.ThrowIfCancellationRequested();
            if (SignedIn(value)) return new(CliLoginOutcome.LoggedIn, value, startedSignedOut);
            if (closed is { IsCompleted: true }) return new(CliLoginOutcome.Exited, value, startedSignedOut);
            if (watch.Elapsed >= timeout) return new(CliLoginOutcome.TimedOut, value, startedSignedOut);
        }
    }
}

/// <summary>
/// Antigravity has no sign-in command: its sign-in is the interactive CLI itself (the browser opens on its first run), so it
/// runs in a terminal the user sees (M/CLILoginRecovery.swift CLIAntigravityLogin). Its credentials live in the system's
/// credential store, so the sign-in is seen by asking its status (<see cref="CliAccountSupport.AntigravityStatus"/>) every interval.
/// </summary>
public static class CliAntigravityLogin
{
    /// <summary>
    /// Waits for a sign-in in the terminal: signed in once the status reads so; once more when the terminal closed (Exited)
    /// or the limit passed (TimedOut). The terminal being open is checked every tick. Cancellation throws.
    /// </summary>
    public static async Task<CliLoginWaitResult> RunAsync(Func<CancellationToken, Task<CliAccountStatus>> status, Func<bool> open, CancellationToken cancellation = default,
        TimeSpan? interval = null, TimeSpan? limit = null, TimeSpan? tick = null)
    {
        var watch = Stopwatch.StartNew(); var lastRead = TimeSpan.Zero;
        var polling = interval ?? TimeSpan.FromSeconds(3); var timeout = limit ?? TimeSpan.FromMinutes(10);
        while (true)
        {
            await Task.Delay(tick ?? TimeSpan.FromSeconds(1), cancellation);
            var active = open(); var expired = watch.Elapsed >= timeout;
            if (active && !expired && watch.Elapsed - lastRead < polling) continue;
            lastRead = watch.Elapsed;
            var value = await status(cancellation); cancellation.ThrowIfCancellationRequested();
            if (value.Provider == "gemini" && value.LoggedIn == true) return new(CliLoginOutcome.LoggedIn, value);
            if (!active) return new(CliLoginOutcome.Exited, value);
            if (expired) return new(CliLoginOutcome.TimedOut, value);
        }
    }
}

/// Hidden ConPTY preserves the CLI's browser/code login flow without persisting its output.
public sealed class CliBackgroundLogin : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly CliLoginOutputParser parser = new();
    private readonly PseudoTerminal terminal;
    public event Action<CliLoginOutput>? Changed;
    public CliLoginOutput Output { get { lock (gate) return parser.Current; } }
    public bool IsRunning => !terminal.Completion.IsCompleted;
    public int? ExitCode => terminal.Completion.IsCompletedSuccessfully ? terminal.Completion.Result : null;
    public Task<int> Completion => terminal.Completion;
    public CliBackgroundLogin(CliCommand command, IReadOnlyList<string> arguments, string directory)
    {
        terminal = PseudoTerminal.Start(directory, Receive, columns: 1000, executable: command.Binary, arguments: [.. command.Prefix, .. arguments], environment: CliEnvironment.Current());
    }
    private void Receive(string text)
    {
        CliLoginOutput before, after;
        lock (gate) { before = parser.Current; parser.Append(text); after = parser.Current; }
        if (before != after) Changed?.Invoke(after);
    }
    public void FinishOutput()
    {
        CliLoginOutput value;
        lock (gate) { parser.Finish(); value = parser.Current; }
        Changed?.Invoke(value);
    }
    public bool SendCode(string code)
    {
        var clean = code.Trim();
        if (clean.Length == 0 || Encoding.UTF8.GetByteCount(clean) > 4096 || clean.Any(char.IsControl)) return false;
        return terminal.TryWrite(clean + "\r");
    }
    public async ValueTask DisposeAsync() { await terminal.DisposeAsync(); lock (gate) parser.Clear(); }
}
