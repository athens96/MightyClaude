using System.Text.Json;
using System.Text.Json.Serialization;

namespace MightyClaude.Core;

/// Account quota windows; these are independent of an agent's context window.
/// Mirrors AccountUsageWindow in AccountUsageSnapshot.swift.
public sealed record AccountUsageWindow(string Kind, double UsedPercent, string? ResetsAt = null, int? WindowMinutes = null);

/// Memory-only presentation data. Never contains credentials, raw HTTP bodies
/// or transcripts. Mirrors AccountUsageSnapshot in AccountUsageSnapshot.swift,
/// minus the macOS-only "permission" status — Windows has no Keychain dialog.
public sealed record AccountUsageSnapshot
{
    public string Provider { get; init; } = "";
    public string? AccountLabel { get; init; }
    public string? Plan { get; init; }
    public IReadOnlyList<AccountUsageWindow> Windows { get; init; } = [];
    /// The Claude limit-reset (리셋권) rows, one per programme. Empty until the
    /// entitlement read has happened; never carries a grant id or a credential.
    public IReadOnlyList<AccountResetEntitlement> Resets { get; init; } = [];
    public string? FetchedAt { get; init; }
    /// available, unavailable, error, stale or cancelled.
    public string Status { get; init; } = "unavailable";
    public string Detail { get; init; } = AccountUsageStrings.DetailNotCheckedYet;
    /// Transport scheduling hint; never serialized with the presentation data.
    [JsonIgnore] public double? RetryAfterSeconds { get; init; }
}

public enum AccountUsageFailureKind { Unavailable, InvalidResponse, Authentication, RateLimited, Network }

/// The message is copy from AccountUsageStrings only. A credential, a header
/// value or a response body is never placed in it.
public sealed class AccountUsageFailure(AccountUsageFailureKind kind, string detail = "", double retryAfterSeconds = 0)
    : Exception(detail)
{
    public AccountUsageFailureKind Kind { get; } = kind;
    public string Detail { get; } = detail;
    public double RetryAfterSeconds { get; } = retryAfterSeconds;
}

public sealed record AccountUsageHttpRequest(Uri Url, IReadOnlyDictionary<string, string> Headers, TimeSpan Timeout)
{
    /// Always "GET"; exposed so a test handler can assert the fake transport never sees a POST.
    public string Method { get; init; } = "GET";
}
/// `RedirectLocation` is only ever set by a handler that saw a 3xx; the probe
/// refuses it rather than following it.
public sealed record AccountUsageHttpResponse(int Status, string Body, string? RetryAfter = null, string? RedirectLocation = null);
public delegate Task<AccountUsageHttpResponse> AccountUsageHttpHandler(AccountUsageHttpRequest request, CancellationToken cancellation);

public sealed record ClaudeQuotaCredential(string Token, string? Plan);

public static class AccountUsageSupport
{
    public const int MaximumBodyBytes = 1024 * 1024;
    public const double MinimumBackoffSeconds = 60, MaximumBackoffSeconds = 86400, DefaultBackoffSeconds = 300;

    internal static double? Number(JsonElement value, string key) => MetadataJson.Number(value, key);
    /// Control characters stripped, trimmed, capped at 160 — the macOS `text` rule.
    public static string? Text(string? value)
    {
        var clean = Wire.Clean(value, 160).Trim();
        return clean.Length == 0 ? null : clean;
    }
    public static string Timestamp(DateTimeOffset value) => value.ToUniversalTime().ToString("O");
    public static DateTimeOffset? Date(string? value) => AgentRunTiming.Parse(value);
    /// An ISO date, or epoch seconds inside a sane range; anything else is dropped
    /// so a malformed reset never becomes a real-looking date.
    public static string? Reset(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String && Date(value.GetString()) is { } parsed) return Timestamp(parsed);
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var seconds) && double.IsFinite(seconds) && seconds is > 0 and < 32_503_680_000)
            return Timestamp(DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000)));
        return null;
    }
    public static double Backoff(double seconds) =>
        double.IsFinite(seconds) ? Math.Min(MaximumBackoffSeconds, Math.Max(MinimumBackoffSeconds, seconds)) : DefaultBackoffSeconds;
    /// Retry-After as seconds or as an HTTP date, clamped into the back-off range.
    public static double RetryInterval(string? value, DateTimeOffset now)
    {
        if (value is null) return DefaultBackoffSeconds;
        if (double.TryParse(value, out var seconds)) return Backoff(seconds);
        return Date(value) is { } date ? Backoff((date - now).TotalSeconds) : DefaultBackoffSeconds;
    }
    /// The macOS RateLimitWindowLabel, in Korean copy from AccountUsageStrings.
    public static string WindowLabel(string kind)
    {
        var lowered = kind.ToLowerInvariant();
        switch (lowered)
        {
            case "session" or "five_hour" or "5h" or "primary": return AccountUsageStrings.WindowSession;
            case "weekly" or "seven_day" or "7d" or "secondary": return AccountUsageStrings.WindowWeekly;
            case "daily": return AccountUsageStrings.WindowDaily;
            case "monthly": return AccountUsageStrings.WindowMonthly;
            case "spend_limit": return AccountUsageStrings.WindowSpendLimit;
        }
        if (lowered.StartsWith("seven_day_")) return AccountUsageStrings.WindowWeekly + " " + kind[10..];
        if (lowered.StartsWith("five_hour_")) return AccountUsageStrings.WindowSession + " " + kind[10..];
        if (lowered.Length > 1 && lowered[^1] == 'm' && lowered[..^1].All(char.IsAsciiDigit)) return lowered[..^1] + "분";
        if (lowered.Length > 1 && lowered[^1] == 'h' && lowered[..^1].All(char.IsAsciiDigit)) return lowered[..^1] + "시간";
        return kind.Replace('_', ' ');
    }
    public static string Percent(double value) => ((int)Math.Round(value, MidpointRounding.AwayFromZero)).ToString();
}

/// The Claude credentials file the CLI writes under the user's .claude folder.
/// Read only: the file is never written, rotated or moved, and the token lives
/// in memory for one request.
public static class ClaudeCredentialFile
{
    public const int MaximumBytes = 262144;
    public const int MaximumTokenBytes = 16384;
    public const string FileName = ".credentials.json";

    /// `CLAUDE_CONFIG_DIR` wins, otherwise `<home>/.claude`. There is no
    /// Keychain on Windows, so the file is the only source.
    public static string Path(string home, string? configDirectory) =>
        System.IO.Path.Combine(configDirectory is { Length: > 0 } dir ? dir : System.IO.Path.Combine(home, ".claude"), FileName);

    public static ClaudeQuotaCredential? Parse(string json, DateTimeOffset now)
    {
        if (json.Length == 0 || System.Text.Encoding.UTF8.GetByteCount(json) > MaximumBytes) return null;
        JsonElement root;
        try { using var document = JsonDocument.Parse(json); root = document.RootElement.Clone(); }
        catch (JsonException) { return null; }
        var oauth = MetadataJson.Property(root, "claudeAiOauth");
        if (oauth.ValueKind != JsonValueKind.Object) return null;
        var token = oauth.Text("accessToken");
        if (token is not { Length: > 0 } || System.Text.Encoding.UTF8.GetByteCount(token) > MaximumTokenBytes || token != Wire.Clean(token, MaximumTokenBytes)) return null;
        if (AccountUsageSupport.Number(oauth, "expiresAt") is { } expires && expires <= now.ToUnixTimeMilliseconds()) return null;
        var scopes = MetadataJson.Property(oauth, "scopes");
        if (scopes.ValueKind == JsonValueKind.Array && scopes.GetArrayLength() > 0 && !scopes.EnumerateArray().Any(s => s.ValueKind == JsonValueKind.String && s.GetString() == "user:profile")) return null;
        return new ClaudeQuotaCredential(token, AccountUsageSupport.Text(oauth.Text("subscriptionType")));
    }

    /// Size-checks before reading and never touches the file otherwise.
    public static ClaudeQuotaCredential? Read(string home, IReadOnlyDictionary<string, string> environment, DateTimeOffset now)
    {
        environment.TryGetValue("CLAUDE_CONFIG_DIR", out var configured);
        var path = Path(home, configured);
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaximumBytes) return null;
            return Parse(File.ReadAllText(path), now);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}

/// The direct Claude lookup. Only https://api.anthropic.com is ever asked, a
/// redirect is refused, the timeout is 10 seconds and there is one attempt.
public static class ClaudeAccountProbe
{
    public const string Host = "api.anthropic.com";
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    /// Any of these means a custom or non-subscription auth path; the
    /// production endpoint must never see those credentials.
    public static readonly string[] BlockedEnvironment =
    [
        "CLAUDE_CODE_CUSTOM_OAUTH_URL", "CLAUDE_LOCAL_OAUTH_API_BASE", "USE_LOCAL_OAUTH", "USE_STAGING_OAUTH",
        "ANTHROPIC_BASE_URL", "CLAUDE_CODE_OAUTH_TOKEN", "ANTHROPIC_API_KEY",
        "CLAUDE_CODE_USE_BEDROCK", "CLAUDE_CODE_USE_VERTEX", "CLAUDE_CODE_USE_FOUNDRY",
    ];

    /// The entitlement reads get the CLI's own 5 second budget.
    public static readonly TimeSpan ResetTimeout = TimeSpan.FromSeconds(5);

    public static Uri Endpoint(string path, string? query = null)
    {
        var url = new Uri("https://" + Host + "/api/oauth/" + path + (query is { Length: > 0 } ? "?" + query : ""));
        Guard(url);
        return url;
    }
    /// Per-process deadline for reset GETs; survives across ReadAsync calls.
    public static readonly ResetReadDeadlineBox SharedDeadlineBox = new();

    /// HTTPS, exactly api.anthropic.com, and only the four allowed paths.
    /// A usage query with a reset variant must carry skip_spend=1 and no extra
    /// keys; anything else is rejected here before the token ever leaves.
    public static void Guard(Uri url)
    {
        if (url.Scheme != Uri.UriSchemeHttps || url.Host != Host)
            throw new AccountUsageFailure(AccountUsageFailureKind.Network, AccountUsageStrings.DetailRefreshFailed);
        var pq = url.PathAndQuery;
        if (pq is not ("/api/oauth/usage" or "/api/oauth/profile"
            or "/api/oauth/usage?cedar_ember=1&skip_spend=1"
            or "/api/oauth/usage?at_wall=1&skip_spend=1"))
            throw new AccountUsageFailure(AccountUsageFailureKind.Network, AccountUsageStrings.DetailRefreshFailed);
    }

    /// Wraps a handler with the pre-send guard so unsanctioned URLs are
    /// rejected before the transport is ever called.
    public static AccountUsageHttpHandler Guarded(AccountUsageHttpHandler handler) =>
        (request, cancellation) => { Guard(request.Url); return handler(request, cancellation); };

    public static bool CustomAuthentication(IReadOnlyDictionary<string, string> environment) =>
        BlockedEnvironment.Any(key => environment.TryGetValue(key, out var value)
            && value is { Length: > 0 } && value.ToLowerInvariant() is not ("0" or "false"));

    public static void CheckResponse(AccountUsageHttpResponse response, DateTimeOffset now)
    {
        if (response.Status is 401 or 403) throw new AccountUsageFailure(AccountUsageFailureKind.Authentication, AccountUsageStrings.DetailAuthentication);
        if (response.Status == 429) throw new AccountUsageFailure(AccountUsageFailureKind.RateLimited, AccountUsageStrings.DetailRateLimited, AccountUsageSupport.RetryInterval(response.RetryAfter, now));
        // A redirect is refused, never followed: the next hop is not this host.
        if (response.Status is >= 300 and < 400 || response.RedirectLocation is not null)
            throw new AccountUsageFailure(AccountUsageFailureKind.Network, AccountUsageStrings.DetailRefreshFailed);
        if (response.Status is < 200 or >= 300 || System.Text.Encoding.UTF8.GetByteCount(response.Body) > AccountUsageSupport.MaximumBodyBytes)
            throw new AccountUsageFailure(AccountUsageFailureKind.Network, AccountUsageStrings.DetailRefreshFailed);
    }

    public static AccountUsageSnapshot Map(JsonElement body, JsonElement profile, string? plan)
    {
        var windows = new List<AccountUsageWindow>();
        foreach (var (key, kind, minutes) in new[] { ("five_hour", "session", 300), ("seven_day", "weekly", 10080), ("seven_day_sonnet", "seven_day_Sonnet", 10080) })
        {
            var row = MetadataJson.Property(body, key);
            if (row.ValueKind != JsonValueKind.Object || AccountUsageSupport.Number(row, "utilization") is not { } used || used is < 0 or > 100) continue;
            windows.Add(new AccountUsageWindow(kind, used, AccountUsageSupport.Reset(MetadataJson.Property(row, "resets_at")), minutes));
        }
        var account = MetadataJson.Property(profile, "account");
        var organization = MetadataJson.Property(profile, "organization");
        return new AccountUsageSnapshot
        {
            Provider = "claude",
            AccountLabel = AccountUsageSupport.Text(account.Text("email")),
            Plan = AccountUsageSupport.Text(organization.Text("rate_limit_tier")) ?? AccountUsageSupport.Text(plan),
            Windows = windows,
            Status = windows.Count == 0 ? "unavailable" : "available",
            Detail = windows.Count == 0 ? AccountUsageStrings.DetailClaudeNoWindows : AccountUsageStrings.DetailClaude,
        };
    }

    public static async Task<AccountUsageSnapshot> ReadAsync(
        IReadOnlyDictionary<string, string> environment,
        Func<ClaudeQuotaCredential?> load,
        AccountUsageHttpHandler http,
        Func<DateTimeOffset> clock,
        ResetReadDeadlineBox? resetDeadlineBox = null,
        AccountUsageShapeLog? shapeLog = null,
        CancellationToken cancellation = default)
    {
        if (CustomAuthentication(environment)) throw new AccountUsageFailure(AccountUsageFailureKind.Unavailable, AccountUsageStrings.DetailCustomAuthentication);
        var credential = load() ?? throw new AccountUsageFailure(AccountUsageFailureKind.Authentication, AccountUsageStrings.DetailAuthentication);
        // The token exists only inside this call; it is placed in one header
        // dictionary per request and never stored, logged or returned.
        AccountUsageHttpRequest Request(string path, string? query = null, TimeSpan? timeout = null) =>
            new(Endpoint(path, query), new Dictionary<string, string>
            {
                ["Authorization"] = "Bearer " + credential.Token,
                ["Accept"] = "application/json",
                ["anthropic-beta"] = "oauth-2025-04-20",
            }, timeout ?? Timeout);

        var usage = await http(Request("usage"), cancellation);
        CheckResponse(usage, clock());
        JsonElement body;
        try { using var document = JsonDocument.Parse(usage.Body); body = document.RootElement.Clone(); }
        catch (JsonException) { throw new AccountUsageFailure(AccountUsageFailureKind.InvalidResponse, AccountUsageStrings.DetailRefreshFailed); }
        if (body.ValueKind != JsonValueKind.Object) throw new AccountUsageFailure(AccountUsageFailureKind.InvalidResponse, AccountUsageStrings.DetailRefreshFailed);

        var profile = default(JsonElement);
        double? retryAfter = null;
        try
        {
            var reply = await http(Request("profile"), cancellation);
            if (reply.Status == 429) retryAfter = AccountUsageSupport.RetryInterval(reply.RetryAfter, clock());
            else if (reply.Status is >= 200 and < 300 && reply.RedirectLocation is null)
            {
                using var document = JsonDocument.Parse(reply.Body);
                profile = document.RootElement.Clone();
            }
        }
        catch (JsonException) { }
        catch (AccountUsageFailure) { }
        cancellation.ThrowIfCancellationRequested();
        // The 리셋권 read rides the same schedule; a failure of it never changes
        // the base usage windows or the status above.
        var resets = await ClaudeResetEntitlements.ReadAsync(
            query => Request("usage", query, ResetTimeout), http, clock,
            resetDeadlineBox ?? SharedDeadlineBox,
            shapeLog ?? AccountUsageShapeLog.Shared,
            cancellation);
        return Map(body, profile, credential.Plan) with { RetryAfterSeconds = retryAfter, Resets = resets };
    }
}

/// One line-delimited JSON-RPC channel to `codex app-server`. The app never
/// sees Codex credentials; the CLI owns them.
public interface IAccountUsageStdio : IAsyncDisposable
{
    Task WriteLineAsync(string line, CancellationToken cancellation);
    Task<string?> ReadLineAsync(CancellationToken cancellation);
}

/// Asks the installed Codex CLI's own app-server over stdio. It never starts or
/// resumes a thread and never calls a tool.
public static class CodexAccountProbe
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    public static AccountUsageSnapshot Map(JsonElement account, JsonElement limits)
    {
        var info = MetadataJson.Property(account, "account");
        if (info.ValueKind != JsonValueKind.Object || info.Text("type") != "chatgpt")
            throw new AccountUsageFailure(AccountUsageFailureKind.Unavailable, AccountUsageStrings.DetailCodexNeedsChatGPT);
        var buckets = MetadataJson.Property(limits, "rateLimitsByLimitId");
        var primary = MetadataJson.Property(buckets, "codex");
        if (primary.ValueKind != JsonValueKind.Object) primary = MetadataJson.Property(limits, "rateLimits");
        if (primary.ValueKind != JsonValueKind.Object) throw new AccountUsageFailure(AccountUsageFailureKind.InvalidResponse, AccountUsageStrings.DetailRefreshFailed);
        var windows = new List<AccountUsageWindow>();
        foreach (var (key, fallback) in new[] { ("primary", "session"), ("secondary", "weekly") })
        {
            var row = MetadataJson.Property(primary, key);
            if (row.ValueKind != JsonValueKind.Object || AccountUsageSupport.Number(row, "usedPercent") is not { } used || used is < 0 or > 100) continue;
            var raw = AccountUsageSupport.Number(row, "windowDurationMins");
            int? minutes = raw is { } value && value > 0 && value <= 525_600 && Math.Round(value) == value ? (int)value : null;
            // A reported nonstandard period is respected, never relabelled as a week.
            var kind = minutes is null ? fallback : minutes == 10080 ? "weekly" : minutes == 300 ? "session" : minutes + "m";
            windows.Add(new AccountUsageWindow(kind, used, AccountUsageSupport.Reset(MetadataJson.Property(row, "resetsAt")), minutes));
        }
        return new AccountUsageSnapshot
        {
            Provider = "codex",
            AccountLabel = AccountUsageSupport.Text(info.Text("email")),
            Plan = AccountUsageSupport.Text(primary.Text("planType")) ?? AccountUsageSupport.Text(info.Text("planType")),
            Windows = windows,
            Status = windows.Count == 0 ? "unavailable" : "available",
            Detail = windows.Count == 0 ? AccountUsageStrings.DetailCodexNoWindows : AccountUsageStrings.DetailCodex,
        };
    }

    /// initialize → initialized → account/read → account/rateLimits/read.
    public static async Task<AccountUsageSnapshot> ReadAsync(Func<IAccountUsageStdio> open, TimeSpan timeout, CancellationToken cancellation = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(timeout);
        await using var channel = open();
        async Task Send(object message) => await channel.WriteLineAsync(JsonSerializer.Serialize(message, Wire.Json), deadline.Token);

        await Send(new { id = 1, method = "initialize", @params = new { clientInfo = new { name = "mightyclaude_account_usage", version = "0.1.0" } } });
        var expected = 1;
        var account = default(JsonElement);
        var read = 0;
        while (true)
        {
            string? line;
            try { line = await channel.ReadLineAsync(deadline.Token); }
            catch (OperationCanceledException) { throw new AccountUsageFailure(AccountUsageFailureKind.Network, AccountUsageStrings.DetailRefreshFailed); }
            if (line is null) throw new AccountUsageFailure(AccountUsageFailureKind.Network, AccountUsageStrings.DetailRefreshFailed);
            read += line.Length;
            if (read > AccountUsageSupport.MaximumBodyBytes) throw new AccountUsageFailure(AccountUsageFailureKind.InvalidResponse, AccountUsageStrings.DetailRefreshFailed);
            JsonElement message;
            try { using var document = JsonDocument.Parse(line); message = document.RootElement.Clone(); }
            catch (JsonException) { continue; }
            if (MetadataJson.Integer(message, "id") != expected) continue;
            if (MetadataJson.Property(message, "error").ValueKind == JsonValueKind.Object)
            {
                var error = MetadataJson.Property(message, "error");
                var rateLimited = AccountUsageSupport.Number(error, "code") == 429 || (error.Text("message") ?? "").Contains("429");
                throw rateLimited
                    ? new AccountUsageFailure(AccountUsageFailureKind.RateLimited, AccountUsageStrings.DetailRateLimited, AccountUsageSupport.DefaultBackoffSeconds)
                    : new AccountUsageFailure(AccountUsageFailureKind.Network, AccountUsageStrings.DetailRefreshFailed);
            }
            var result = MetadataJson.Property(message, "result");
            if (result.ValueKind != JsonValueKind.Object) throw new AccountUsageFailure(AccountUsageFailureKind.InvalidResponse, AccountUsageStrings.DetailRefreshFailed);
            if (expected == 1)
            {
                await Send(new { method = "initialized" });
                expected = 2;
                await Send(new { id = 2, method = "account/read", @params = new { refreshToken = false } });
            }
            else if (expected == 2)
            {
                account = result;
                if (MetadataJson.Property(result, "account").Text("type") != "chatgpt")
                    throw new AccountUsageFailure(AccountUsageFailureKind.Unavailable, AccountUsageStrings.DetailCodexNeedsChatGPT);
                expected = 3;
                await Send(new { id = 3, method = "account/rateLimits/read" });
            }
            else return Map(account, result);
        }
    }
}

/// Local account reads only. One read at a time per provider, a cache, a next
/// read time and a back-off. A failure keeps the last known value and marks it.
/// Mirrors the macOS actor; the probe and the clock are always injected.
public sealed class AccountUsageService : IAsyncDisposable
{
    private readonly Func<string, CancellationToken, Task<AccountUsageSnapshot>> probe;
    private readonly Func<DateTimeOffset> clock;
    private readonly object gate = new();
    private readonly Dictionary<string, Task<AccountUsageSnapshot>> inFlight = [];
    private readonly Dictionary<string, AccountUsageSnapshot> cache = [];
    private readonly Dictionary<string, DateTimeOffset> nextRead = [];
    private readonly CancellationTokenSource closing = new();
    private bool closed;

    public AccountUsageService(Func<string, CancellationToken, Task<AccountUsageSnapshot>> probe, Func<DateTimeOffset>? clock = null)
    { this.probe = probe; this.clock = clock ?? (() => DateTimeOffset.UtcNow); }

    public AccountUsageSnapshot? Cached(string provider) { lock (gate) return cache.TryGetValue(provider, out var value) ? value : null; }

    public Task<AccountUsageSnapshot> ReadAsync(string provider, bool force = false, CancellationToken cancellation = default)
    {
        var now = clock();
        Task<AccountUsageSnapshot> started;
        lock (gate)
        {
            if (closed || closing.IsCancellationRequested)
                return Task.FromResult(new AccountUsageSnapshot { Provider = provider, Status = "cancelled", Detail = AccountUsageStrings.DetailShutdown });
            if (inFlight.TryGetValue(provider, out var running)) return running;
            // A forced refresh still respects the server cooldown.
            if (nextRead.TryGetValue(provider, out var deadline) && deadline > now && cache.TryGetValue(provider, out var held)) return Task.FromResult(held);
            if (!force && cache.TryGetValue(provider, out var saved) && AccountUsageSupport.Date(saved.FetchedAt) is { } stamp && (now - stamp).TotalSeconds < 60) return Task.FromResult(saved);
            started = RunAsync(provider, now, cancellation);
            inFlight[provider] = started;
        }
        return started;
    }

    private async Task<AccountUsageSnapshot> RunAsync(string provider, DateTimeOffset started, CancellationToken cancellation)
    {
        await Task.Yield();
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, closing.Token);
            var value = (await probe(provider, linked.Token)) with { FetchedAt = AccountUsageSupport.Timestamp(started) };
            lock (gate)
            {
                if (closed) return value;
                cache[provider] = value;
                nextRead[provider] = clock().AddSeconds(value.RetryAfterSeconds is { } retry ? AccountUsageSupport.Backoff(retry) : 5);
                return value;
            }
        }
        catch (Exception error) { return Failed(provider, error); }
        finally { lock (gate) inFlight.Remove(provider); }
    }

    /// A failure keeps the last known windows and marks them stale; a failure
    /// that invalidates the account (auth, unavailable) drops them instead of
    /// showing another account's numbers.
    private AccountUsageSnapshot Failed(string provider, Exception error)
    {
        lock (gate)
        {
            if (closed || closing.IsCancellationRequested || error is OperationCanceledException)
                return new AccountUsageSnapshot { Provider = provider, Status = "cancelled", Detail = AccountUsageStrings.DetailCancelled };
            var delay = AccountUsageSupport.MinimumBackoffSeconds;
            var detail = AccountUsageStrings.DetailRefreshFailed;
            var preserve = true;
            if (error is AccountUsageFailure failure)
                switch (failure.Kind)
                {
                    case AccountUsageFailureKind.Unavailable: detail = failure.Detail; preserve = false; break;
                    case AccountUsageFailureKind.Authentication: detail = AccountUsageStrings.DetailAuthentication; preserve = false; break;
                    case AccountUsageFailureKind.RateLimited: delay = AccountUsageSupport.Backoff(failure.RetryAfterSeconds); detail = AccountUsageStrings.DetailRateLimited; break;
                }
            var held = preserve && cache.TryGetValue(provider, out var saved) ? saved : new AccountUsageSnapshot { Provider = provider };
            var value = held with
            {
                Provider = provider,
                Status = held.Windows.Count == 0 ? preserve ? "error" : "unavailable" : "stale",
                Detail = held.Windows.Count == 0 ? detail : detail + AccountUsageStrings.DetailLastKnownSuffix,
                RetryAfterSeconds = null,
            };
            cache[provider] = value;
            nextRead[provider] = clock().AddSeconds(delay);
            return value;
        }
    }

    /// Closing the app cancels the pending reads and drops every cached value.
    public async ValueTask DisposeAsync()
    {
        Task[] pending;
        lock (gate) { if (closed) return; pending = [.. inFlight.Values]; }
        await closing.CancelAsync();
        foreach (var task in pending) { try { await task; } catch { } }
        lock (gate) { closed = true; inFlight.Clear(); cache.Clear(); nextRead.Clear(); }
        closing.Dispose();
    }
}
