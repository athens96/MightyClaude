using System.Globalization;
using System.Text.Json.Serialization;

namespace MightyClaude.Core;

/// One chip in the bottom status bar. `Warning` marks a window at 90% or more.
public sealed record AccountUsageChip(string Provider, string Text, bool Warning);
public sealed record AccountUsageWindowRow(string Label, string Used, double Fraction, string? Reset, bool Warning);
/// One provider card in the popover.
public sealed record AccountUsageCard(string Provider, string Title, string? Account,
    IReadOnlyList<AccountUsageWindowRow> Windows, string? Detail, string? CheckedAt, string? Note);

/// What the GUI smoke run records under the key "accountUsage" after driving
/// the real chips and popover with fixture data. It carries counts and copy
/// only — never an account label, a plan or anything read from a credential.
public sealed record AccountUsageSmokeOutcome
{
    public const string ResultKey = "accountUsage";
    [JsonPropertyName("chips")] public int Chips { get; init; }
    [JsonPropertyName("cards")] public int Cards { get; init; }
    [JsonPropertyName("windowRows")] public int WindowRows { get; init; }
    [JsonPropertyName("directLookupDefaultOff")] public bool DirectLookupDefaultOff { get; init; }
    [JsonPropertyName("claudeChip")] public string ClaudeChip { get; init; } = "";
    [JsonPropertyName("geminiNote")] public string GeminiNote { get; init; } = "";
    [JsonPropertyName("restored")] public bool Restored { get; init; }
}

/// The account usage state behind the status bar. All of it lives in Core so a
/// Mac-side check can drive it; WinUI only renders these rows and forwards the
/// click, the refresh and the switch.
public sealed class AccountUsageStatus : IAsyncDisposable
{
    private static readonly CultureInfo Korean = CultureInfo.GetCultureInfo("ko-KR");
    private readonly AccountUsageService service;
    private readonly Func<DateTimeOffset> clock;
    private readonly Dictionary<string, AccountUsageSnapshot> snapshots = [];
    private readonly object gate = new();
    private bool stopped;

    public AccountUsageStatus(AccountUsageService service, Func<DateTimeOffset>? clock = null)
    { this.service = service; this.clock = clock ?? (() => DateTimeOffset.UtcNow); }

    public IReadOnlyList<string> Providers { get; private set; } = [];
    public bool Refreshing { get; private set; }
    /// The saved switch. Off out of the box, so nothing is looked up directly.
    public bool DirectClaudeLookupEnabled { get; private set; }

    public AccountUsageSnapshot? Snapshot(string provider) { lock (gate) return snapshots.TryGetValue(provider, out var value) ? value : null; }

    /// The providers with a local AI pane, in the macOS provider order. A remote
    /// workspace never borrows this PC's account, and a shell pane has none.
    public static IReadOnlyList<string> LocalProviders(AppSnapshot snapshot)
    {
        var local = snapshot.Workspaces.Where(w => w.Remote is null).Select(w => w.Id).ToHashSet();
        return snapshot.Sessions
            .Where(s => s.Kind != "shell" && local.Contains(s.WorkspaceId) && Wire.Providers.Contains(s.Provider))
            .Select(s => s.Provider).Distinct()
            .OrderBy(p => Array.IndexOf(Wire.Providers, p)).ToList();
    }

    /// The limits the CLI reported during a run. This is what Claude shows while
    /// the direct lookup is off, and it is newer than any polled read.
    public static IReadOnlyDictionary<string, AccountUsageSnapshot> SessionReported(AppSnapshot snapshot, DateTimeOffset now)
    {
        var local = snapshot.Workspaces.Where(w => w.Remote is null).Select(w => w.Id).ToHashSet();
        var found = new Dictionary<string, AccountUsageSnapshot>();
        foreach (var session in snapshot.Sessions.Where(s => local.Contains(s.WorkspaceId)))
        {
            if (SessionUsageSupport.Normalize(session.SessionUsage) is not { } usage || usage.Provider != session.Provider || usage.RateLimits is not { Count: > 0 } limits) continue;
            var windows = limits.Where(r => r.PercentUsed is not null)
                .Select(r => new AccountUsageWindow(r.Kind, r.PercentUsed!.Value, r.ResetsAt)).ToList();
            if (windows.Count == 0 || AccountUsageSupport.Date(usage.RateLimitsUpdatedAt) is not { } stamp) continue;
            if (found.TryGetValue(session.Provider, out var existing) && AccountUsageSupport.Date(existing.FetchedAt) is { } older && older >= stamp) continue;
            var stale = (now - stamp).TotalSeconds > 300;
            found[session.Provider] = new AccountUsageSnapshot
            {
                Provider = session.Provider, Windows = windows, FetchedAt = usage.RateLimitsUpdatedAt,
                Status = stale ? "stale" : "available",
                Detail = stale ? AccountUsageStrings.DetailSessionReportedStale : AccountUsageStrings.DetailSessionReported,
            };
        }
        return found;
    }

    /// Called on app start and on every state change — a run event, a pane
    /// added, a workspace switched. Returns true when the rows changed.
    public bool Update(AppSnapshot snapshot)
    {
        var now = clock();
        var providers = LocalProviders(snapshot);
        var changed = !providers.SequenceEqual(Providers);
        Providers = providers;
        DirectClaudeLookupEnabled = snapshot.ClaudeDirectUsageLookupEnabled;
        lock (gate)
        {
            foreach (var (provider, reported) in SessionReported(snapshot, now))
            {
                if (snapshots.TryGetValue(provider, out var held) && AccountUsageSupport.Date(held.FetchedAt) is { } older
                    && AccountUsageSupport.Date(reported.FetchedAt) is { } fresh && older >= fresh) continue;
                if (snapshots.TryGetValue(provider, out var previous) && previous == reported) continue;
                snapshots[provider] = reported;
                changed = true;
            }
        }
        return changed;
    }

    /// Only the providers that may be asked right now. Claude is absent until
    /// the user switches the direct lookup on.
    public IReadOnlyList<string> Targets() =>
        Providers.Where(p => p != "claude" || DirectClaudeLookupEnabled).ToList();

    /// One read per provider, off the UI thread, cancelled when the app closes.
    public async Task RefreshAsync(bool force = false, CancellationToken cancellation = default)
    {
        var targets = Targets();
        if (stopped || targets.Count == 0 || Refreshing) return;
        Refreshing = true;
        try
        {
            foreach (var provider in targets)
            {
                if (stopped || cancellation.IsCancellationRequested) break;
                var value = await service.ReadAsync(provider, force, cancellation);
                lock (gate)
                {
                    // A value a running session reported is never overwritten by an older read.
                    if (snapshots.TryGetValue(provider, out var held) && held.Windows.Count > 0
                        && AccountUsageSupport.Date(held.FetchedAt) is { } older
                        && AccountUsageSupport.Date(value.FetchedAt) is { } fresh && older > fresh) continue;
                    snapshots[provider] = value;
                }
            }
        }
        finally { Refreshing = false; }
    }

    /// Turning the switch off drops a value that only the direct lookup could
    /// have produced, so nothing looked up stays on screen.
    public void SetDirectClaudeLookup(bool enabled)
    {
        DirectClaudeLookupEnabled = enabled;
        if (enabled) return;
        lock (gate)
            if (snapshots.TryGetValue("claude", out var held) && held.Detail is not (AccountUsageStrings.DetailSessionReported or AccountUsageStrings.DetailSessionReportedStale))
                snapshots.Remove("claude");
    }

    /// The two windows worth a chip: the session window, then the weekly one.
    public static IReadOnlyList<AccountUsageWindow> Leading(IReadOnlyList<AccountUsageWindow> windows) =>
        windows.Where(w => w.Kind != "spend_limit").OrderBy(w => Rank(w.Kind)).Take(2).ToList();
    private static int Rank(string kind) => AccountUsageSupport.WindowLabel(kind) switch
    { AccountUsageStrings.WindowSession => 0, AccountUsageStrings.WindowWeekly => 1, _ => 2 };

    public IReadOnlyList<AccountUsageChip> Chips() => Providers.Select(provider =>
    {
        var usage = Snapshot(provider);
        if (usage is { Windows.Count: > 0 })
        {
            var leading = Leading(usage.Windows);
            return new AccountUsageChip(provider,
                string.Join(" ", leading.Select(w => AccountUsageSupport.WindowLabel(w.Kind) + " " + AccountUsageSupport.Percent(w.UsedPercent) + "%")),
                leading.Any(w => w.UsedPercent >= 90));
        }
        if (provider == "claude" && !DirectClaudeLookupEnabled) return new AccountUsageChip(provider, AccountUsageStrings.ChipBeforeFirstRun, false);
        return new AccountUsageChip(provider, Refreshing ? AccountUsageStrings.ChipChecking : AccountUsageStrings.ChipEmpty, false);
    }).ToList();

    public IReadOnlyList<AccountUsageCard> Cards() => Providers.Select(provider =>
    {
        var usage = Snapshot(provider);
        var title = ProviderCatalog.Name(provider);
        if (usage is null)
            return new AccountUsageCard(provider, title, null, [], null, null,
                provider == "claude" && !DirectClaudeLookupEnabled ? AccountUsageStrings.ClaudeBeforeFirstRunNote
                    : Refreshing ? AccountUsageStrings.CardChecking : AccountUsageStrings.CardNotCheckedYet);
        var rows = usage.Windows.Select(w => new AccountUsageWindowRow(
            AccountUsageSupport.WindowLabel(w.Kind) + (w.WindowMinutes == 300 ? AccountUsageStrings.WindowFiveHourSuffix : w.WindowMinutes == 10080 ? AccountUsageStrings.WindowSevenDaySuffix : ""),
            AccountUsageStrings.UsedPercentTemplate.Replace("{percent}", AccountUsageSupport.Percent(w.UsedPercent)),
            Math.Clamp(w.UsedPercent / 100, 0, 1),
            AccountUsageSupport.Date(w.ResetsAt) is { } reset ? AccountUsageStrings.ResetTemplate.Replace("{date}", reset.ToLocalTime().ToString("g", Korean)) : null,
            w.UsedPercent >= 90)).ToList();
        var account = new[] { usage.AccountLabel, usage.Plan }.Where(v => v is { Length: > 0 }).ToArray();
        var checkedAt = AccountUsageSupport.Date(usage.FetchedAt) is { } stamp
            ? (usage.Status is "error" or "stale" ? AccountUsageStrings.LastKnownPrefix : "")
              + AccountUsageStrings.CheckedAtTemplate.Replace("{time}", stamp.ToLocalTime().ToString("t", Korean))
            : null;
        return new AccountUsageCard(provider, title, account.Length > 0 ? string.Join(" · ", account) : null, rows,
            usage.Status != "available" || rows.Count == 0 ? usage.Detail : null, checkedAt, null);
    }).ToList();

    public async ValueTask DisposeAsync()
    {
        stopped = true;
        await service.DisposeAsync();
        lock (gate) snapshots.Clear();
    }
}
