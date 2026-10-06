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
        return !ExternalDenial(value) && (new[] { "please run /login", "oauth token has expired", "oauth token revoked", "invalid api key" }.Any(value.Contains)
            || value.Contains("401") && value.Contains("authentication_error"));
    }
    public static bool Codex(string text)
    {
        var value = Bounded(text);
        return !ExternalDenial(value) && !value.TrimStart().StartsWith("reconnecting", StringComparison.Ordinal)
            && (value.Contains("401") && value.Contains("unauthorized") || new[] { "not logged in", "please log in", "please login", "log in again", "sign in again", "refresh token" }.Any(value.Contains));
    }
    /// <summary>
    /// One line of Gemini CLI's own console output (stderr, or a stdout line that is not JSON) that says its sign-in is
    /// gone (M/CLILoginRecovery.swift geminiConsole(line:)). Only the CLI's own sign-in messages count, since this output
    /// also carries API error dumps that quote other text: a stale Google sign-in prints the OAuth consent prompt
    /// ("Opening authentication page in your browser") and then "Error authenticating: FatalCancellationError:
    /// Authentication cancelled by user."; no method chosen prints "Please set an Auth method" (or "No authentication
    /// method selected."); a suppressed browser prints "Manual authorization is required".
    /// </summary>
    public static bool GeminiConsole(string line)
    {
        var value = Bounded(line);
        return new[] { "please set an auth method", "no authentication method selected", "opening authentication page in your browser", "manual authorization is required" }.Any(value.Contains)
            || value.Contains("error authenticating") && (value.Contains("fatalauthenticationerror") || value.Contains("authentication cancelled by user"));
    }
    /// <summary>
    /// The message of a failed Gemini error/result event, never model or tool text (M/CLILoginRecovery.swift gemini(text:)):
    /// the console messages above, or a token rejected mid-run (OAuth invalid_grant, "Token has been expired or revoked",
    /// the API's 401 UNAUTHENTICATED "Request had invalid authentication credentials"). An invalid API key is not matched,
    /// and Vertex or a key is ruled out by SignInCanFix.
    /// </summary>
    public static bool Gemini(string text)
    {
        var value = Bounded(text);
        return GeminiConsole(text)
            || new[] { "authentication consent could not be obtained", "invalid_grant", "token has been expired or revoked", "request had invalid authentication credentials" }.Any(value.Contains)
            || value.Contains("401") && value.Contains("unauthenticated");
    }
    /// <summary>A Gemini stream-json line that is the run's successful result: it clears an earlier sign-in signal, as on the Mac.</summary>
    public static bool GeminiSucceeded(string line)
    {
        if (!line.Contains("\"result\"", StringComparison.Ordinal) || !line.TrimStart().StartsWith('{')) return false;
        try { using var json = JsonDocument.Parse(line); var root = json.RootElement; return root.ValueKind == JsonValueKind.Object && root.Text("type") == "result" && root.Text("status") == "success"; }
        catch (JsonException) { return false; }
    }
    /// <summary>Whether a run's failure text is its provider's lost sign-in.</summary>
    public static bool Matches(string provider, string text) => provider switch
    {
        "claude" => Claude(text),
        "codex" => Codex(text),
        "gemini" => Gemini(text),
        _ => false,
    };
    public static bool Claude(JsonElement value)
    {
        var type = value.Text("type");
        if (type == "assistant" && value.Text("error") is { Length: > 0 } error)
        {
            var texts = value.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object && message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array
                ? content.EnumerateArray().Where(block => block.Text("type") == "text").Select(block => block.Text("text") ?? "").ToArray() : [];
            return !texts.Any(text => ExternalDenial(Bounded(text))) && (error == "authentication_failed" || texts.Any(Claude));
        }
        if (type != "result" || !(value.TryGetProperty("is_error", out var failed) && failed.ValueKind == JsonValueKind.True || value.Text("subtype")?.StartsWith("error", StringComparison.Ordinal) == true)) return false;
        var errors = value.TryGetProperty("errors", out var items) && items.ValueKind == JsonValueKind.Array
            ? items.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToList() : [];
        if (value.Text("result") is { } result) errors.Add(result);
        return !errors.Any(text => ExternalDenial(Bounded(text))) && errors.Any(Claude);
    }
    /// <summary>A Gemini method other than the Google sign-in (an API key, Vertex AI, Compute ADC, …) is not renewed by signing in.</summary>
    public static bool SignInCanFix(CliAccountStatus status) => Providers.Contains(status.Provider) && status.Installed && status.AccessVerified != false
        && !(status.Provider == "codex" && status.MethodId == CliAccountMethod.CodexApiKey)
        && !(status.Provider == "gemini" && status.MethodId is not null && status.MethodId != CliAccountMethod.GeminiGoogle);
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

/// <summary>
/// Gemini has no sign-in command: its sign-in is the interactive CLI itself ("Login with Google"), so it runs in a
/// terminal the user sees (M/CLILoginRecovery.swift CLIGeminiLogin). A dropped sign-in usually leaves oauth_creds.json
/// on disk, so a new sign-in is the file appearing or being rewritten after the terminal opened. Only the file's
/// modification time is read, never its contents.
/// </summary>
public static class CliGeminiLogin
{
    public static string CredentialsPath(string home) => Path.Combine(home, ".gemini", "oauth_creds.json");
    /// <summary>When the credentials file was last written; null while it does not exist.</summary>
    public static DateTimeOffset? CredentialsStamp(string home)
    {
        try { var file = new FileInfo(CredentialsPath(home)); return file.Exists ? new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }
    /// <summary>Signed in since baseline (the stamp when the terminal opened): the file is newer, or appeared, and the status reads as a Google sign-in.</summary>
    public static bool SignedIn(DateTimeOffset? baseline, DateTimeOffset? stamp, CliAccountStatus status) =>
        stamp is { } value && (baseline is not { } before || value > before)
        && status.Provider == "gemini" && status.LoggedIn == true && CliAuthFailure.SignInCanFix(status);
    /// <summary>
    /// Waits for a sign-in in the terminal. The stamp is checked every tick; the status is read only once the stamp
    /// moved (then at most every interval until it confirms), and once more when the terminal closed (Exited) or the
    /// limit passed (TimedOut). Cancellation throws.
    /// </summary>
    public static async Task<CliLoginWaitResult> RunAsync(DateTimeOffset? baseline, Func<DateTimeOffset?> stamp,
        Func<CancellationToken, Task<CliAccountStatus>> status, Func<bool> open, CancellationToken cancellation = default,
        TimeSpan? interval = null, TimeSpan? limit = null, TimeSpan? tick = null)
    {
        var watch = Stopwatch.StartNew(); TimeSpan? lastRead = null;
        var polling = interval ?? TimeSpan.FromSeconds(3); var timeout = limit ?? TimeSpan.FromMinutes(10);
        while (true)
        {
            await Task.Delay(tick ?? TimeSpan.FromSeconds(1), cancellation);
            var active = open(); var expired = watch.Elapsed >= timeout;
            var moved = stamp() is { } current && (baseline is not { } before || current > before);
            var final = !active || expired;
            if (!final && !(moved && (lastRead is not { } read || watch.Elapsed - read >= polling))) continue;
            lastRead = watch.Elapsed;
            var value = await status(cancellation); cancellation.ThrowIfCancellationRequested();
            if (SignedIn(baseline, stamp(), value)) return new(CliLoginOutcome.LoggedIn, value);
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
