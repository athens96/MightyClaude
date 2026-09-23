namespace MightyClaude.Core;

/// What the 리셋권 smoke records. The count of non-GET requests the fake
/// transport saw is part of the contract because no POST exists in this app at
/// all: the entitlement surface is read with GET and nothing is written back.
public sealed record AccountResetSmokeResult(
    bool Passed,
    int FakeTransportPostCount,
    string CedarEmberState,
    string JuniperTideState,
    IReadOnlyList<string> Requests,
    IReadOnlyList<string> Lines,
    /// The rows themselves, so the GUI smoke can draw them with the real builder.
    IReadOnlyList<AccountResetRow> Rows);

/// The 리셋권 smoke, shared by the WinUI smoke check and by the Core check that
/// proves it. The probe is driven with an injected fixture clock and a fake
/// handler that only ever sees GET, so the rows render from fixture data
/// irrespective of the direct-lookup switch and without writing any setting.
/// cedar_ember renders available, juniper_tide renders unknown.
/// Mirrors AccountResetSmoke.swift.
public static class ClaudeResetSmoke
{
    /// Binary-derived shape (CLI 2.1.280 decoder field names); the values are a
    /// fixture. The grant's window closes well after the fixture clock.
    public const string CedarAvailableBody = """
    {"cedar_ember":{"eligible":true,"in_experiment":true,"ineligible_reason":null,"next_grant_id":"grant-fixture","grants":[{"id":"grant-fixture","resets_total":3,"resets_left":2,"starts_at":"2026-09-01T00:00:00Z","ends_at":"2026-12-31T00:00:00Z","usable_now":true,"use_requires_limit":false,"paused":false,"percent_used":33.3}],"at_limit":false,"cooldown_until":null,"exhausted":[]}}
    """;
    private const string UsageBody = """{"five_hour":{"utilization":12.5,"resets_at":"2026-09-23T20:00:00Z"}}""";
    /// A made-up sign-in value; it is not a real token and never leaves the fixture.
    public const string FixtureToken = "smoke-fixture-not-a-real-token";

    /// Every comparison in the run reads this instant, never the wall clock.
    public static DateTimeOffset FixtureClock =>
        DateTimeOffset.Parse("2026-09-23T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

    /// The expected GET allow-list for the run, in order: the base usage read,
    /// the profile read and the two entitlement variants, each with skip_spend=1.
    public static readonly string[] ExpectedRequests =
    [
        "GET /api/oauth/usage",
        "GET /api/oauth/profile",
        "GET /api/oauth/usage?cedar_ember=1&skip_spend=1",
        "GET /api/oauth/usage?at_wall=1&skip_spend=1",
    ];

    public static async Task<AccountResetSmokeResult> RunAsync(CancellationToken cancellation = default)
    {
        var requests = new List<string>();
        var fakeTransportPostCount = 0;
        AccountUsageHttpHandler http = (request, _) =>
        {
            ClaudeAccountProbe.Guard(request.Url);
            // Method + path + the exact ordered query, compared as PathAndQuery.
            requests.Add(request.Method + " " + request.Url.PathAndQuery);
            if (request.Method != "GET") fakeTransportPostCount++;
            return Task.FromResult(request.Url.Query switch
            {
                "?cedar_ember=1&skip_spend=1" => new AccountUsageHttpResponse(200, CedarAvailableBody),
                // A 5xx on an entitlement GET leaves that row unknown and never
                // disturbs the base usage windows.
                "?at_wall=1&skip_spend=1" => new AccountUsageHttpResponse(503, ""),
                _ => new AccountUsageHttpResponse(200, request.Url.AbsolutePath.EndsWith("profile") ? "{}" : UsageBody),
            });
        };
        var snapshot = await ClaudeAccountProbe.ReadAsync(new Dictionary<string, string>(),
            () => new ClaudeQuotaCredential(FixtureToken, "max"), http, () => FixtureClock, cancellation: cancellation);
        // directLookupEnabled is true because this service is injected: the rows
        // render without the switch, and the switch itself is never touched.
        var rows = ClaudeResetEntitlements.Rows(snapshot, true);
        var cedar = rows.FirstOrDefault(r => r.Program == ResetProgram.CedarEmber)?.State ?? ResetState.Unknown;
        var juniper = rows.FirstOrDefault(r => r.Program == ResetProgram.JuniperTide)?.State ?? ResetState.Unknown;
        var lines = rows.Select(r => r.Text).ToList();
        var resolved = lines.Count == 2 && lines.TrueForAll(l => l.Length > 0 && !l.StartsWith("usage.reset.", StringComparison.Ordinal));
        var passed = fakeTransportPostCount == 0
            && cedar == ResetState.Available
            && juniper == ResetState.Unknown
            && requests.SequenceEqual(ExpectedRequests)
            && resolved;
        return new AccountResetSmokeResult(passed, fakeTransportPostCount, cedar, juniper, requests, lines, rows);
    }
}
