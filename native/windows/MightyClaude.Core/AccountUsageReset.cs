using System.Text.Json;

namespace MightyClaude.Core;

/// The Claude limit-reset (리셋권) entitlement, read only. Mirrors
/// AccountResetEntitlement.swift — the same two programmes, the same seven
/// states, the same decision lists and the same shared usage.reset.* keys.
/// The app never claims, spends or writes one; the only action is the link.
public static class ResetProgram
{
    public const string CedarEmber = "cedar_ember";
    public const string JuniperTide = "juniper_tide";
    public static readonly string[] All = [CedarEmber, JuniperTide];

    /// The one query variant that carries this programme, `skip_spend=1` included.
    public static string Query(string program) =>
        program == CedarEmber ? "cedar_ember=1&skip_spend=1" : "at_wall=1&skip_spend=1";
    public static string LabelKey(string program) =>
        program == CedarEmber ? "usage.reset.program.cedarEmber" : "usage.reset.program.juniperTide";
}

/// Seven states. Precedence, highest first:
/// available > held > cooldown > exhausted > none > ineligible > unknown.
public static class ResetState
{
    public const string Available = "available", Held = "held", Cooldown = "cooldown",
        Exhausted = "exhausted", None = "none", Ineligible = "ineligible", Unknown = "unknown";
    /// Highest first, so the order itself records the precedence.
    public static readonly string[] All = [Available, Held, Cooldown, Exhausted, None, Ineligible, Unknown];
}

/// One 리셋권 row. Carries no credential, no grant id and no account identifier.
public sealed record AccountResetEntitlement(string Program, string State)
{
    /// cedar_ember: the selected grant's resets_left, shown as "남은 리셋권 N회".
    public int? RemainingCount { get; init; }
    /// cedar_ember: the selected grant's ends_at, shown as "{만료일}까지".
    public string? ExpiresAt { get; init; }
    /// juniper_tide: next_available_at, and cedar_ember: cooldown_until.
    public string? NextAvailableAt { get; init; }
    /// juniper_tide: weekly_resets_at, carried beside the next time.
    public string? WeeklyResetsAt { get; init; }
    /// juniper_tide: resets_per_week, shown as "주 N회".
    public int? ResetsPerWeek { get; init; }

    /// Exactly one shared key per programme and state. The available state
    /// renders the per-programme line; every other state renders the per-state
    /// sentence. `unknown` blames this app's connection, never Anthropic policy.
    public string CopyKey => State switch
    {
        ResetState.Available => Program == ResetProgram.CedarEmber ? "usage.reset.available.cedarEmber" : "usage.reset.available.juniperTide",
        ResetState.Cooldown => Program == ResetProgram.CedarEmber ? "usage.reset.cooldown.cedarEmber" : "usage.reset.cooldown.juniperTide",
        ResetState.Held => "usage.reset.held",
        ResetState.Exhausted => "usage.reset.exhausted",
        ResetState.None => "usage.reset.none",
        ResetState.Ineligible => "usage.reset.ineligible",
        _ => "usage.reset.unknown",
    };
}

/// The rendered row: the programme's own name and its one sentence, both from
/// the shared keys. WinUI draws these and nothing of its own.
public sealed record AccountResetRow(string Program, string State, string Label, string Text);

/// The decision lists, evaluated top to bottom, first match wins. Every time
/// comparison reads the caller's clock, never DateTimeOffset.Now.
public static class ClaudeResetEntitlements
{
    /// The link, live in every one of the seven states.
    public const string LinkKey = "usage.reset.link";
    public const string LinkTarget = "https://claude.ai/settings/usage";
    public const string TitleKey = "usage.reset.title";
    public static string LinkLabel => Locale.Get(LinkKey);
    public static string Title => Locale.Get(TitleKey);

    public static AccountResetEntitlement Unknown(string program) => new(program, ResetState.Unknown);
    public static AccountResetEntitlement Ineligible(string program) => new(program, ResetState.Ineligible);

    private static bool? Flag(JsonElement block, string key)
    {
        var value = MetadataJson.Property(block, key);
        return value.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null };
    }
    private static bool Present(JsonElement block, string key) => MetadataJson.Property(block, key).ValueKind != JsonValueKind.Undefined;
    private static int? Whole(JsonElement block, string key) =>
        AccountUsageSupport.Number(block, key) is { } value && value >= 0 && value <= 1_000_000 && Math.Round(value) == value ? (int)value : null;
    private static DateTimeOffset? Instant(JsonElement block, string key)
    {
        var value = MetadataJson.Property(block, key);
        return value.ValueKind == JsonValueKind.String ? AccountUsageSupport.Date(value.GetString()) : null;
    }
    /// `ineligible_reason` marks the state; its value is never matched literally.
    private static bool ReasonPresent(JsonElement block)
    {
        var value = MetadataJson.Property(block, "ineligible_reason");
        return value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 };
    }

    /// cedar_ember: granted resets. Field names are the CLI 2.1.280 decoder's.
    public static AccountResetEntitlement CedarEmber(JsonElement body, DateTimeOffset now)
    {
        // (1) a parse or HTTP failure, a non-JSON body, a wrong-typed field or
        //     a missing block — this app cannot tell, so it says so.
        if (body.ValueKind != JsonValueKind.Object) return Unknown(ResetProgram.CedarEmber);
        var block = MetadataJson.Property(body, ResetProgram.CedarEmber);
        if (block.ValueKind != JsonValueKind.Object) return Unknown(ResetProgram.CedarEmber);
        if (Present(block, "eligible") && Flag(block, "eligible") is null) return Unknown(ResetProgram.CedarEmber);
        var grants = MetadataJson.Property(block, "grants");
        if (Present(block, "grants") && grants.ValueKind != JsonValueKind.Array) return Unknown(ResetProgram.CedarEmber);
        var selected = MetadataJson.Property(block, "next_grant_id");
        if (Present(block, "next_grant_id") && selected.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) return Unknown(ResetProgram.CedarEmber);
        // (2) the server says this account is not in the programme. cedar_ember
        //     reports `eligible`; `in_experiment` is accepted too because
        //     juniper_tide spells the same fact that way.
        if (Flag(block, "eligible") == false || Flag(block, "in_experiment") == false || ReasonPresent(block)) return Ineligible(ResetProgram.CedarEmber);
        // (3) no grant is selected, or the selected id matches nothing.
        var id = selected.ValueKind == JsonValueKind.String ? selected.GetString() : null;
        JsonElement grant = default;
        if (id is { Length: > 0 } && grants.ValueKind == JsonValueKind.Array)
            foreach (var candidate in grants.EnumerateArray())
                if (candidate.ValueKind == JsonValueKind.Object && candidate.Text("id") == id) { grant = candidate; break; }
        if (grant.ValueKind != JsonValueKind.Object) return new AccountResetEntitlement(ResetProgram.CedarEmber, ResetState.None);

        var left = Whole(grant, "resets_left");
        var ends = Instant(grant, "ends_at");
        var expiry = ends is { } stamp ? AccountUsageSupport.Timestamp(stamp) : null;
        // (4) a paused grant, or one whose window already closed.
        if (Flag(grant, "paused") == true) return new AccountResetEntitlement(ResetProgram.CedarEmber, ResetState.None);
        if (ends is { } closes && closes <= now) return new AccountResetEntitlement(ResetProgram.CedarEmber, ResetState.None);
        // (5) usable right now.
        if (Flag(grant, "usable_now") == true)
            return new AccountResetEntitlement(ResetProgram.CedarEmber, ResetState.Available) { RemainingCount = left, ExpiresAt = expiry };
        // (6) held back until the account actually reaches its limit.
        if (Flag(grant, "use_requires_limit") == true && Flag(block, "at_limit") != true)
            return new AccountResetEntitlement(ResetProgram.CedarEmber, ResetState.Held) { RemainingCount = left, ExpiresAt = expiry };
        // (7) still cooling down from the last use.
        if (Instant(block, "cooldown_until") is { } cooldown && cooldown > now)
            return new AccountResetEntitlement(ResetProgram.CedarEmber, ResetState.Cooldown)
            { RemainingCount = left, ExpiresAt = expiry, NextAvailableAt = AccountUsageSupport.Timestamp(cooldown) };
        // (8) nothing left in this period — `exhausted` is a list or a flag.
        var exhausted = MetadataJson.Property(block, "exhausted");
        var spent = (exhausted.ValueKind == JsonValueKind.Array && exhausted.GetArrayLength() > 0) || exhausted.ValueKind == JsonValueKind.True;
        if (spent || left == 0)
            return new AccountResetEntitlement(ResetProgram.CedarEmber, ResetState.Exhausted) { RemainingCount = left, ExpiresAt = expiry };
        // (9) otherwise there is nothing to offer.
        return new AccountResetEntitlement(ResetProgram.CedarEmber, ResetState.None) { RemainingCount = left, ExpiresAt = expiry };
    }

    /// juniper_tide: the reset offered when the account is at the wall.
    public static AccountResetEntitlement JuniperTide(JsonElement body, DateTimeOffset now)
    {
        // (1) a parse or HTTP failure, or a missing block.
        if (body.ValueKind != JsonValueKind.Object) return Unknown(ResetProgram.JuniperTide);
        var block = MetadataJson.Property(body, ResetProgram.JuniperTide);
        if (block.ValueKind != JsonValueKind.Object) return Unknown(ResetProgram.JuniperTide);
        if (Present(block, "in_experiment") && Flag(block, "in_experiment") is null) return Unknown(ResetProgram.JuniperTide);
        if (Present(block, "available") && Flag(block, "available") is null) return Unknown(ResetProgram.JuniperTide);
        // (2) not in the experiment, or the server named a reason.
        if (Flag(block, "in_experiment") == false || ReasonPresent(block)) return Ineligible(ResetProgram.JuniperTide);
        var perWeek = Whole(block, "resets_per_week");
        var weekly = Instant(block, "weekly_resets_at") is { } w ? AccountUsageSupport.Timestamp(w) : null;
        // (3) available right now.
        if (Flag(block, "available") == true)
            return new AccountResetEntitlement(ResetProgram.JuniperTide, ResetState.Available) { WeeklyResetsAt = weekly, ResetsPerWeek = perWeek };
        // (4) a next time still ahead of the injected clock.
        if (Instant(block, "next_available_at") is { } next && next > now)
            return new AccountResetEntitlement(ResetProgram.JuniperTide, ResetState.Cooldown)
            { NextAvailableAt = AccountUsageSupport.Timestamp(next), WeeklyResetsAt = weekly, ResetsPerWeek = perWeek };
        // (5) the account gets none this week.
        if (perWeek == 0) return new AccountResetEntitlement(ResetProgram.JuniperTide, ResetState.None) { WeeklyResetsAt = weekly, ResetsPerWeek = perWeek };
        // (6) otherwise this period's resets are spent.
        return new AccountResetEntitlement(ResetProgram.JuniperTide, ResetState.Exhausted) { WeeklyResetsAt = weekly, ResetsPerWeek = perWeek };
    }

    /// One GET per programme, the two query variants the installed CLI uses,
    /// on the same schedule as the base usage read. A 404 or a 200 whose body
    /// carries no reset field means the account is not in that programme;
    /// anything else this app could not read stays unknown. A failure here
    /// never changes the base usage windows or status.
    public static async Task<IReadOnlyList<AccountResetEntitlement>> ReadAsync(
        Func<string, AccountUsageHttpRequest> request, AccountUsageHttpHandler http,
        Func<DateTimeOffset> clock, CancellationToken cancellation = default)
    {
        var rows = new List<AccountResetEntitlement>();
        foreach (var program in ResetProgram.All)
        {
            var row = Unknown(program);
            try
            {
                var reply = await http(request(ResetProgram.Query(program)), cancellation);
                if (reply.Status == 404) row = Ineligible(program);
                else if (reply.Status is >= 200 and < 300 && reply.RedirectLocation is null
                         && System.Text.Encoding.UTF8.GetByteCount(reply.Body) <= AccountUsageSupport.MaximumBodyBytes)
                {
                    using var document = JsonDocument.Parse(reply.Body);
                    var body = document.RootElement.Clone();
                    // A body without the block at all is an account outside the
                    // programme, not a gap in what this app can see.
                    row = body.ValueKind == JsonValueKind.Object && MetadataJson.Property(body, program).ValueKind == JsonValueKind.Undefined
                        ? Ineligible(program)
                        : program == ResetProgram.CedarEmber ? CedarEmber(body, clock()) : JuniperTide(body, clock());
                }
                // A 429 or a 5xx leaves the row unknown and the base usage untouched.
            }
            catch (JsonException) { }
            catch (AccountUsageFailure) { }
            rows.Add(row);
        }
        return rows;
    }

    /// The one sentence for a row, from the shared key. The granted line
    /// carries the remaining count and the expiry; the at-wall cooldown line
    /// carries the next time and the weekly count.
    public static string Line(AccountResetEntitlement row)
    {
        if (row is { State: ResetState.Available, Program: ResetProgram.CedarEmber })
            return Locale.Get(row.CopyKey, new Dictionary<string, string>
            { ["count"] = (row.RemainingCount ?? 0).ToString(), ["expiry"] = Day(row.ExpiresAt) });
        if (row is { State: ResetState.Cooldown, Program: ResetProgram.CedarEmber })
            return Locale.Get(row.CopyKey, new Dictionary<string, string> { ["time"] = Moment(row.NextAvailableAt) });
        if (row is { State: ResetState.Cooldown, Program: ResetProgram.JuniperTide })
            return Locale.Get(row.CopyKey, new Dictionary<string, string>
            { ["time"] = Moment(row.NextAvailableAt), ["count"] = (row.ResetsPerWeek ?? 0).ToString() });
        return Locale.Get(row.CopyKey);
    }

    public static AccountResetRow Render(AccountResetEntitlement row) =>
        new(row.Program, row.State, Locale.Get(ResetProgram.LabelKey(row.Program)), Line(row));

    private static readonly System.Globalization.CultureInfo Korean = System.Globalization.CultureInfo.GetCultureInfo("ko-KR");
    private static string Day(string? value) => AccountUsageSupport.Date(value) is { } date ? date.ToLocalTime().ToString("d", Korean) : "";
    private static string Moment(string? value) => AccountUsageSupport.Date(value) is { } date ? date.ToLocalTime().ToString("t", Korean) : "";

    /// The rows the GUI draws. The decision to hide them lives here in Core,
    /// not in a window: while the direct-lookup switch is off there is nothing
    /// to draw at all, and with it on but nothing read yet every programme
    /// reads unknown.
    public static IReadOnlyList<AccountResetRow> Rows(AccountUsageSnapshot? snapshot, bool directLookupEnabled)
    {
        if (!directLookupEnabled) return [];
        var read = snapshot?.Resets ?? [];
        return ResetProgram.All
            .Select(program => Render(read.FirstOrDefault(r => r.Program == program) ?? Unknown(program)))
            .ToList();
    }
}
