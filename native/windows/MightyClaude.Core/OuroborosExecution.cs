using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MightyClaude.Core;

/// <summary>
/// A background Ouroboros execution an agent started from a pane (macOS OuroborosExecutionLink). It outlives the
/// request that started it, so the graph keeps a live block for it after that request settled. Only the four start
/// tools' results are read. <paramref name="RunID"/> is the graph run whose tool result named the execution;
/// <paramref name="DashboardURL"/> is loopback only: anything else in the text is dropped.
/// </summary>
public sealed record OuroborosExecutionLink(string RunID, string Tool, string? JobID = null, string? SessionID = null, string? ExecutionID = null, Uri? DashboardURL = null, string Timestamp = "")
{
    /// One block per execution; a start that named only a job keys on the job.
    public string Key => ExecutionID ?? "job-" + (JobID ?? "");
}

public static class OuroborosExecutionLinks
{
    /// The style manifest's <c>job.open</c> tools.
    public static readonly IReadOnlySet<string> StartTools = new HashSet<string>(StringComparer.Ordinal)
    {
        "ouroboros_start_execute_seed", "ouroboros_start_auto", "ouroboros_start_evolve_step", "ouroboros_start_ralph",
    };

    /// Claude names an MCP tool <c>mcp__&lt;server&gt;__&lt;tool&gt;</c>, Codex <c>&lt;server&gt;.&lt;tool&gt;</c>.
    internal static string ShortName(string wireName)
    {
        var name = wireName;
        if (name.LastIndexOf("__", StringComparison.Ordinal) is >= 0 and var underscores) name = name[(underscores + 2)..];
        if (name.LastIndexOf('.') is >= 0 and var dot) name = name[(dot + 1)..];
        return name;
    }

    private static readonly Regex IdPattern = new(@"\A[A-Za-z0-9][A-Za-z0-9_:-]{0,199}\z", RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    /// <summary>
    /// Ids go into URLs (as a query item) and a process argument of their own: letters, digits, <c>_</c>, <c>-</c> and
    /// <c>:</c> (an evolve generation is <c>evolve:&lt;lineage&gt;:generation:&lt;n&gt;</c>) only, never led by <c>-</c> or
    /// <c>:</c>, so none can read as an option or a path.
    /// </summary>
    public static bool ValidID(string value) => IdPattern.IsMatch(value);

    /// What a start prints while it has no id yet (<c>Execution ID: None</c> from an evolve step whose generation is claimed later).
    private static readonly HashSet<string> Placeholders = new(StringComparer.OrdinalIgnoreCase) { "pending", "none", "null" };

    /// <summary>
    /// The labelled lines of one start tool's result text. A placeholder and anything that is not a strict id count
    /// as absent.
    /// </summary>
    public static OuroborosExecutionLink? Parse(string text, string tool, string runID, string timestamp = "")
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in text.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries).Take(400))
        {
            var line = raw.Trim(' ', '\t');
            var colon = line.IndexOf(':');
            if (colon < 0) continue;
            var label = line[..colon].Trim(' ', '\t').ToLowerInvariant();
            var value = line[(colon + 1)..].Trim(' ', '\t');
            if (value.Length == 0 || value.Contains(' ') || values.ContainsKey(label)) continue;
            values[label] = value;
        }
        string? Id(params string[] labels) => labels.Select(label => values.GetValueOrDefault(label)).OfType<string>().FirstOrDefault(value => !Placeholders.Contains(value) && ValidID(value));
        var dashboard = values.GetValueOrDefault("live dashboard") is { } address && Uri.TryCreate(address, UriKind.Absolute, out var url) && OuroborosDashboard.Endpoint(url) is not null ? url : null;
        // A run URL names the execution even when the text still says pending.
        var runParameter = dashboard is null ? null : QueryItem(dashboard, "run");
        var execution = Id("execution id") ?? (runParameter is not null && ValidID(runParameter) && !Placeholders.Contains(runParameter) ? runParameter : null);
        var job = Id("job id", "job_id");
        if (execution is null && job is null) return null;
        return new(runID, tool, job, Id("session id", "auto_session_id"), execution, dashboard, timestamp);
    }

    /// The decoded value of a query item of a dashboard address, null when it has none.
    public static string? QueryItem(Uri url, string name)
    {
        foreach (var item in url.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = item.IndexOf('=');
            if (Uri.UnescapeDataString(equals < 0 ? item : item[..equals]) == name) return equals < 0 ? "" : Uri.UnescapeDataString(item[(equals + 1)..]);
        }
        return null;
    }

    /// <summary>
    /// Every execution a start tool reported in these runs, once each, on the first run that reported it. A failed
    /// start named nothing that runs.
    /// </summary>
    public static List<OuroborosExecutionLink> Extract(IEnumerable<MightyGraphRun> runs)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var links = new List<OuroborosExecutionLink>();
        // Called on every graph draw: the cheap name test runs first.
        void Scan(IEnumerable<LogEntry> entries, string runID)
        {
            foreach (var entry in entries)
            {
                if (entry.Activity is not { State: "completed", ToolName: { } tool, Output: { } output } || !tool.Contains("ouroboros_start_", StringComparison.Ordinal)) continue;
                var name = ShortName(tool);
                if (StartTools.Contains(name) && Parse(output, name, runID, entry.Timestamp) is { } link && seen.Add(link.Key)) links.Add(link);
            }
        }
        foreach (var run in runs)
        {
            Scan(run.RootEntries, run.Id);
            foreach (var agent in run.Agents) Scan(agent.Entries, run.Id);
        }
        return links;
    }
}

// ── dashboard payloads ────────────────────────────────────────────────────────

public enum OuroborosExecutionStatus { Running, Paused, Completed, Failed, Cancelled, Unknown }
public enum OuroborosACStatus { Pending, Executing, Completed, Failed }
public sealed record OuroborosACItem(string Id, string Title, OuroborosACStatus Status, int Depth = 0);

/// One row of <c>/api/runs</c>. Every field but the id may be missing.
public sealed record OuroborosRunSummary(string ExecutionID, OuroborosExecutionStatus Status, string? Goal = null, string? Phase = null, string? Activity = null,
    int? Completed = null, int? Total = null, int? Pending = null, int? Executing = null, int? Failed = null);

/// The Kanban board one <c>/events</c> frame carries.
public sealed record OuroborosBoard(string? Goal, string? Phase, string? Activity, int? Completed, int? Total, IReadOnlyList<OuroborosACItem> Items)
{
    public int Count(OuroborosACStatus status) => Items.Count(item => item.Status == status);
}

public sealed record OuroborosDashboardEndpoint(string Host, int Port)
{
    /// <paramref name="path"/> starts with "/"; <paramref name="run"/> is only set when it is a strict id.
    public Uri Url(string path, string? run = null) =>
        new("http://" + (Host.Contains(':') ? "[" + Host + "]" : Host) + ":" + Port.ToString(System.Globalization.CultureInfo.InvariantCulture) + path + (run is null ? "" : "?run=" + Uri.EscapeDataString(run)));
}

/// <summary>What a graph block shows for one execution (macOS OuroborosExecutionSnapshot).</summary>
/// <param name="Items">Null until a board was read; the list is only read while it is open.</param>
public sealed record OuroborosExecutionSnapshot(OuroborosExecutionSnapshot.Sources Source, OuroborosExecutionStatus Status = OuroborosExecutionStatus.Unknown, string? Goal = null, string? Phase = null,
    string? Activity = null, int? Completed = null, int? Total = null, int? Pending = null, int? Executing = null, int? Failed = null, IReadOnlyList<OuroborosACItem>? Items = null, Uri? DashboardURL = null)
{
    public enum Sources
    {
        /// The dashboard answered.
        Live,
        /// Nothing answered on loopback.
        Unreachable,
        /// The dashboard answered but could not read its runs (a non-200 such as 503). It is up: never started again for this.
        Unreadable,
        /// The start named only a job: there is no execution to look up yet.
        NoExecution,
    }

    public bool Equals(OuroborosExecutionSnapshot? other) => other is not null && Source == other.Source && Status == other.Status && Goal == other.Goal && Phase == other.Phase && Activity == other.Activity
        && Completed == other.Completed && Total == other.Total && Pending == other.Pending && Executing == other.Executing && Failed == other.Failed && DashboardURL == other.DashboardURL
        && (Items is null ? other.Items is null : other.Items is not null && Items.SequenceEqual(other.Items));
    public override int GetHashCode() => HashCode.Combine(Source, Status, Goal, Completed, Total, Items?.Count);

    /// <summary>
    /// The summary owns status and counts; a board fills what it lacks and alone supplies the AC list. Neither is a
    /// contract, so either may be null.
    /// </summary>
    public static OuroborosExecutionSnapshot Merge(OuroborosRunSummary? summary, OuroborosBoard? board, IReadOnlyList<OuroborosACItem>? previousItems, Uri? dashboardURL)
    {
        static string? Text(string? value) => value is null ? null : ActivitySupport.Clean(value, 600, singleLine: true) is { Length: > 0 } clean ? clean : null;
        return new(Sources.Live, summary?.Status ?? (board is null ? OuroborosExecutionStatus.Unknown : SettledStatus(board)),
            Text(summary?.Goal ?? board?.Goal), Text(summary?.Phase ?? board?.Phase), Text(summary?.Activity ?? board?.Activity),
            summary?.Completed ?? board?.Completed ?? board?.Count(OuroborosACStatus.Completed),
            summary?.Total ?? (board is null ? null : board.Total is > 0 and var total ? total : board.Items.Count),
            summary?.Pending ?? board?.Count(OuroborosACStatus.Pending), summary?.Executing ?? board?.Count(OuroborosACStatus.Executing),
            summary?.Failed ?? board?.Count(OuroborosACStatus.Failed), board?.Items ?? previousItems, dashboardURL);
    }

    /// <summary>
    /// A run <c>/api/runs</c> no longer lists (it keeps only the newest) has no status but its board: with nothing
    /// pending or executing and every AC settled, it failed when any AC failed and completed otherwise (the
    /// dashboard's own fallback). Anything else stays unknown.
    /// </summary>
    internal static OuroborosExecutionStatus SettledStatus(OuroborosBoard board)
    {
        var (completed, failed) = (board.Count(OuroborosACStatus.Completed), board.Count(OuroborosACStatus.Failed));
        if (board.Count(OuroborosACStatus.Pending) != 0 || board.Count(OuroborosACStatus.Executing) != 0 || completed + failed <= 0 || completed + failed < (board.Total ?? 0)) return OuroborosExecutionStatus.Unknown;
        return failed > 0 ? OuroborosExecutionStatus.Failed : OuroborosExecutionStatus.Completed;
    }
}

public static class OuroborosDashboard
{
    public static OuroborosExecutionStatus Status(string? wire) => wire is not null && Enum.TryParse<OuroborosExecutionStatus>(wire, ignoreCase: true, out var status) && Enum.IsDefined(status) && !int.TryParse(wire, out _) ? status : OuroborosExecutionStatus.Unknown;
    public static bool IsTerminal(this OuroborosExecutionStatus status) => status is OuroborosExecutionStatus.Completed or OuroborosExecutionStatus.Failed or OuroborosExecutionStatus.Cancelled;

    /// <summary>
    /// The activity mark's own vocabulary for an execution's block (macOS MightyGraphOuroborosCard <c>indicatorStatus</c>):
    /// a block nothing was read for yet is starting, one whose status cannot be told is unknown.
    /// </summary>
    public static string BlockStatus(OuroborosExecutionSnapshot? snapshot) => snapshot?.Status switch
    {
        OuroborosExecutionStatus.Running => "running", OuroborosExecutionStatus.Paused => "waiting", OuroborosExecutionStatus.Completed => "completed",
        OuroborosExecutionStatus.Failed => "error", OuroborosExecutionStatus.Cancelled => "stopped", null => "starting", _ => "unknown",
    };

    public static bool IsLoopback(string host) => host.ToLowerInvariant() is "127.0.0.1" or "localhost" or "::1" or "[::1]";

    /// <summary>
    /// A <c>Live Dashboard:</c> URL is agent-influenced text: only plain http on a loopback host with a real port is
    /// ever connected to.
    /// </summary>
    public static OuroborosDashboardEndpoint? Endpoint(Uri url)
    {
        if (!url.IsAbsoluteUri || url.Scheme != "http" || url.UserInfo.Length > 0 || !NamesPort(url)) return null;
        var host = url.Host;
        if (!IsLoopback(host) || url.Port is < 1 or > 65_535) return null;
        return new(host.StartsWith('[') ? host[1..^1] : host, url.Port);
    }

    /// A port written in the address itself: the scheme's own default does not count as one.
    private static bool NamesPort(Uri url)
    {
        var text = url.OriginalString;
        var start = text.IndexOf("://", StringComparison.Ordinal);
        if (start < 0) return false;
        var end = text.IndexOfAny(['/', '?', '#'], start + 3);
        var authority = end < 0 ? text[(start + 3)..] : text[(start + 3)..end];
        var colon = authority.LastIndexOf(':');
        return colon > authority.LastIndexOf(']') && colon < authority.Length - 1 && authority[(colon + 1)..].All(char.IsAsciiDigit);
    }

    /// <summary>
    /// <c>~/.ouroboros/dashboard.json</c>. A daemon bound to every interface is still reached on loopback, as its
    /// own health probe does.
    /// </summary>
    public static OuroborosDashboardEndpoint? Endpoint(ReadOnlySpan<byte> stateJson)
    {
        try
        {
            var reader = new Utf8JsonReader(stateJson);
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("port", out var portValue) || portValue.ValueKind != JsonValueKind.Number || !portValue.TryGetInt32(out var port) || port is < 1 or > 65_535) return null;
            var raw = root.TryGetProperty("host", out var hostValue) && hostValue.ValueKind == JsonValueKind.String ? hostValue.GetString()! : "127.0.0.1";
            var host = raw is "" or "0.0.0.0" ? "127.0.0.1" : raw;
            return IsLoopback(host) ? new(host, port) : null;
        }
        catch (JsonException) { return null; }
    }

    public static Uri RunsURL(OuroborosDashboardEndpoint endpoint) => endpoint.Url("/api/runs");
    public static Uri? EventsURL(OuroborosDashboardEndpoint endpoint, string executionID) => OuroborosExecutionLinks.ValidID(executionID) ? endpoint.Url("/events", executionID) : null;
    /// The page the "open dashboard" button shows: the run's board, or the run picker when the start named no execution.
    public static Uri PageURL(OuroborosDashboardEndpoint endpoint, string? executionID) => endpoint.Url("/", executionID is not null && OuroborosExecutionLinks.ValidID(executionID) ? executionID : null);

    private static int? Count(JsonElement owner, string name)
    {
        if (owner.ValueKind != JsonValueKind.Object || !owner.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number)) return null;
        return double.IsFinite(number) && number >= 0 && number <= 1_000_000 ? (int)number : null;
    }
    private static string? String(JsonElement owner, string name) =>
        owner.ValueKind == JsonValueKind.Object && owner.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text[..Math.Min(text.Length, 2_000)] : null;

    /// <c>/api/runs</c> keyed by execution id; null when the body is not that shape.
    public static Dictionary<string, OuroborosRunSummary>? Summaries(ReadOnlySpan<byte> data)
    {
        try
        {
            var reader = new Utf8JsonReader(data);
            using var document = JsonDocument.ParseValue(ref reader);
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("runs", out var runs) || runs.ValueKind != JsonValueKind.Array) return null;
            var result = new Dictionary<string, OuroborosRunSummary>(StringComparer.Ordinal);
            foreach (var run in runs.EnumerateArray().Take(500))
            {
                if (run.ValueKind != JsonValueKind.Object || !run.TryGetProperty("execution_id", out var idValue) || idValue.ValueKind != JsonValueKind.String || idValue.GetString() is not { } id
                    || !OuroborosExecutionLinks.ValidID(id) || result.ContainsKey(id)) continue;
                result[id] = new(id, Status(run.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String ? status.GetString() : null),
                    String(run, "goal"), String(run, "phase"), String(run, "activity"), Count(run, "completed_count"), Count(run, "total_count"),
                    Count(run, "pending_count"), Count(run, "executing_count"), Count(run, "failed_count"));
            }
            return result;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// Complete SSE events' <c>data</c> in order. Comments and other fields are skipped; a trailing event without its
    /// blank line is not complete yet.
    /// </summary>
    public static List<string> SseData(string text)
    {
        var events = text.Replace("\r\n", "\n").Replace('\r', '\n').Split("\n\n");
        var result = new List<string>();
        foreach (var block in events.Take(events.Length - 1))
        {
            var lines = block.Split('\n').Where(line => line.StartsWith("data:", StringComparison.Ordinal)).Select(line => line[5..] is { } value && value.StartsWith(' ') ? value[1..] : line[5..]).ToList();
            if (lines.Count > 0) result.Add(string.Join("\n", lines));
        }
        return result;
    }

    /// <summary>
    /// One board frame. Cards the reducer put in no known column are kept as executing, as the dashboard itself
    /// shows them.
    /// </summary>
    public static OuroborosBoard? Board(ReadOnlySpan<byte> data)
    {
        try
        {
            var reader = new Utf8JsonReader(data);
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("columns", out var columns) || columns.ValueKind != JsonValueKind.Object) return null;
            var meta = root.TryGetProperty("meta", out var metaValue) ? metaValue : default;
            var items = new List<(int Index, OuroborosACItem Item)>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var column in columns.EnumerateObject())
            {
                var status = column.Name switch { "pending" => OuroborosACStatus.Pending, "completed" => OuroborosACStatus.Completed, "failed" => OuroborosACStatus.Failed, _ => OuroborosACStatus.Executing };
                if (column.Value.ValueKind != JsonValueKind.Array) continue;
                foreach (var card in column.Value.EnumerateArray().Take(1_000))
                {
                    if (String(card, "id") is not { } id || !ids.Add(id)) continue;
                    var title = ActivitySupport.Clean(String(card, "title") ?? id, 400, singleLine: true);
                    items.Add((Count(card, "ac_index") ?? int.MaxValue, new(id, title.Length == 0 ? id : title, status, Math.Min(4, Count(card, "depth") ?? 0))));
                }
            }
            var ordered = items.OrderBy(entry => entry.Index).ThenBy(entry => entry.Item.Depth).ThenBy(entry => entry.Item.Id, StringComparer.Ordinal).Select(entry => entry.Item).ToList();
            return new(String(meta, "goal"), String(meta, "phase"), String(meta, "activity"), Count(meta, "completed"), Count(meta, "total"), ordered);
        }
        catch (JsonException) { return null; }
    }
}

// ── polling ───────────────────────────────────────────────────────────────────

/// <summary>
/// When one execution is read next (macOS OuroborosExecutionPoll). Pure: the caller supplies the clock and what the
/// last read saw.
/// </summary>
public sealed class OuroborosExecutionPoll(DateTimeOffset now, bool stale)
{
    public static readonly TimeSpan FastInterval = TimeSpan.FromSeconds(3), SlowInterval = TimeSpan.FromSeconds(15);
    /// No change for this long moves to the slow interval.
    public static readonly TimeSpan QuietAfter = TimeSpan.FromSeconds(60);
    /// A start older than this is history: the daemon is not started for it and, when nothing answers, it is read once and left.
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(12);
    /// This many reads in a row that could not tell the status (a run the dashboard neither lists nor has a settled
    /// board for, or no answer) move to <see cref="UnknownInterval"/>; <see cref="UnknownStopAfter"/> of them stop the block.
    public const int UnknownBackoffAfter = 10, UnknownStopAfter = 40;
    public static readonly TimeSpan UnknownInterval = TimeSpan.FromSeconds(180);

    public DateTimeOffset? NextAt { get; private set; } = now;
    public DateTimeOffset LastChange { get; private set; } = now;
    public OuroborosExecutionSnapshot? Last { get; private set; }
    /// Reads in a row whose status was unknown.
    public int UnknownReads { get; private set; }
    public bool Stale { get; } = stale;

    public bool Stopped => NextAt is null;
    public bool IsDue(DateTimeOffset at) => NextAt is { } next && next <= at;

    /// <summary>
    /// A terminal status ends polling; so does a job-only start (nothing to look up), a stale start that nothing
    /// answers for, and a long run of reads that never told the status.
    /// </summary>
    public void Record(OuroborosExecutionSnapshot snapshot, DateTimeOffset at)
    {
        if (!snapshot.Equals(Last)) { LastChange = at; Last = snapshot; }
        UnknownReads = snapshot.Status == OuroborosExecutionStatus.Unknown ? UnknownReads + 1 : 0;
        if (snapshot.Status.IsTerminal() || snapshot.Source == OuroborosExecutionSnapshot.Sources.NoExecution || Stale && snapshot.Status == OuroborosExecutionStatus.Unknown || UnknownReads >= UnknownStopAfter)
        {
            NextAt = null;
            return;
        }
        NextAt = at + (UnknownReads >= UnknownBackoffAfter ? UnknownInterval : at - LastChange >= QuietAfter ? SlowInterval : FastInterval);
    }

    /// Whether this block may ask for the daemon at all: a live execution still being read, never history. How often
    /// is <see cref="OuroborosDaemonStarts"/>'.
    public bool MayEnsureDaemon => !Stale && !Stopped && Last?.Status.IsTerminal() != true;
    /// One more read for a stopped or waiting block, such as an AC list opened with nothing read yet;
    /// <see cref="Record"/> stops it again if it was over.
    public void Wake(DateTimeOffset at) => NextAt = at;

    private static readonly Regex InternetDateTime = new(@"\A\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:\d{2})\z", RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    /// A start with no readable time (an ISO 8601 date and time with its zone) is history.
    public static bool IsStale(string timestamp, DateTimeOffset now) =>
        !InternetDateTime.IsMatch(timestamp) || !DateTimeOffset.TryParse(timestamp, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var date) || now - date > StaleAfter;
}

/// <summary>
/// Daemon starts the graph made on its own, process-wide and keyed by execution id (macOS OuroborosDaemonStarts), so
/// a graph that leaves the screen and comes back cannot start it again sooner.
/// </summary>
public sealed class OuroborosDaemonStarts
{
    public static readonly OuroborosDaemonStarts Shared = new();
    public static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(300);
    public const int Maximum = 3;
    private readonly Dictionary<string, (DateTimeOffset At, int Count)> starts = [];

    /// Takes one start for the execution when its last was at least <see cref="Cooldown"/> ago and it has had fewer
    /// than <see cref="Maximum"/>; false otherwise.
    public bool Claim(string executionID, DateTimeOffset now)
    {
        lock (starts)
        {
            var known = starts.TryGetValue(executionID, out var previous);
            if (known && (previous.Count >= Maximum || now - previous.At < Cooldown)) return false;
            starts[executionID] = (now, (known ? previous.Count : 0) + 1);
            return true;
        }
    }
}

// ── client ────────────────────────────────────────────────────────────────────

/// GETs against the loopback dashboard. Checks inject a fake.
public interface IOuroborosDashboardTransport
{
    /// Status and body, or null when nothing answered in time.
    Task<(int Status, byte[] Body)?> GetAsync(Uri url, TimeSpan timeout, CancellationToken cancellation);
    /// Read an SSE response until it has been quiet for <paramref name="quiet"/> after its first event, no event came
    /// within <paramref name="firstEvent"/>, <paramref name="deadline"/> passed or <paramref name="maximumBytes"/>
    /// arrived; then close it.
    Task<byte[]?> ReadEventsAsync(Uri url, TimeSpan firstEvent, TimeSpan quiet, TimeSpan deadline, int maximumBytes, CancellationToken cancellation);
}

/// <summary>What <c>/api/runs</c> said (macOS OuroborosRunsAnswer).</summary>
/// <param name="Answered">False: nothing answered on loopback, the daemon is not up.</param>
/// <param name="Status">The HTTP status of an answer.</param>
/// <param name="Summaries">The runs; null with an answer is a dashboard that is up and cannot read its runs (a non-200 such as 503, or another body): starting it again cannot help.</param>
public sealed record OuroborosRunsAnswer(bool Answered, int Status = 0, IReadOnlyDictionary<string, OuroborosRunSummary>? Summaries = null)
{
    public static readonly OuroborosRunsAnswer NoAnswer = new(false);
}

public sealed class OuroborosDashboardClient
{
    /// <summary>One daemon start: a fixed executable, argument array, environment and working directory, and no shell.</summary>
    public sealed record Launch(string Executable, IReadOnlyList<string> Arguments, IReadOnlyDictionary<string, string> Environment, string WorkingDirectory);

    /// Board streams one tick reads at once.
    public const int BoardConcurrency = 3;
    /// A stream that sent no event by then has nothing to show yet.
    public static readonly TimeSpan FirstEventDeadline = TimeSpan.FromSeconds(2);
    /// What is read of the daemon's own record, of <c>/api/runs</c> and of one board stream at the most.
    public const int MaximumStateBytes = 64 * 1024, MaximumBoardBytes = 8 * 1024 * 1024;
    /// <summary>
    /// The daemon runs from a directory no agent can write: <c>python -m</c> would otherwise put the working directory
    /// first on <c>sys.path</c>, and the detached daemon inherits it.
    /// </summary>
    public static string DaemonDirectory => System.Environment.SystemDirectory is { Length: > 0 } system ? system : Path.GetPathRoot(System.Environment.CurrentDirectory) ?? "/";

    private readonly IOuroborosDashboardTransport transport;
    private readonly string home;
    private readonly Func<Launch, Task<bool>> launcher;
    public string StatePath { get; }
    public IReadOnlyList<string> PythonCandidates { get; }

    public OuroborosDashboardClient(IOuroborosDashboardTransport? transport = null, string? home = null, Func<Launch, Task<bool>>? launcher = null, IReadOnlyList<string>? pythonCandidates = null)
    {
        this.transport = transport ?? new LoopbackDashboardTransport();
        this.home = home ?? System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        this.launcher = launcher ?? Capture;
        StatePath = Path.Combine(this.home, ".ouroboros", "dashboard.json");
        PythonCandidates = pythonCandidates ?? ToolPythons(this.home, OperatingSystem.IsWindows(), System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData));
    }

    /// <summary>
    /// The tool environment Ouroboros is installed into (uv, then pipx). On Windows uv keeps its tools under the
    /// roaming application data and pipx under the user's folder, each with the interpreter in <c>Scripts</c>.
    /// </summary>
    public static IReadOnlyList<string> ToolPythons(string home, bool windows, string? applicationData) => windows
        ? [.. new[] { applicationData is { Length: > 0 } ? Path.Combine(applicationData, "uv", "tools") : null, Path.Combine(home, "pipx", "venvs"), Path.Combine(home, ".local", "pipx", "venvs") }
            .OfType<string>().Select(tools => Path.Combine(tools, "ouroboros-ai", "Scripts", "python.exe"))]
        : [Path.Combine(home, ".local", "share", "uv", "tools", "ouroboros-ai", "bin", "python"), Path.Combine(home, ".local", "pipx", "venvs", "ouroboros-ai", "bin", "python")];

    private static async Task<bool> Capture(Launch launch)
    {
        try
        {
            // Nothing is piped: the daemon the start leaves behind would inherit a pipe, hold it open past the start and fail
            // its writes once nothing read it. Without a window, the start and its daemon write to a console no one sees.
            var info = new ProcessStartInfo(launch.Executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = launch.WorkingDirectory };
            foreach (var argument in launch.Arguments) info.ArgumentList.Add(argument);
            info.Environment.Clear();
            foreach (var (key, value) in launch.Environment) info.Environment[key] = value;
            using var process = Process.Start(info);
            if (process is null) return false;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { try { process.Kill(entireProcessTree: false); } catch (InvalidOperationException) { } return false; }
            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException) { return false; }
    }

    /// The running daemon's own record first; a link's URL is only a hint.
    public async Task<OuroborosDashboardEndpoint?> EndpointAsync(Uri? hint)
    {
        try
        {
            var file = new FileInfo(StatePath);
            if (file.Exists && file.Length < MaximumStateBytes && OuroborosDashboard.Endpoint(await File.ReadAllBytesAsync(StatePath).ConfigureAwait(false)) is { } endpoint) return endpoint;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return hint is null ? null : OuroborosDashboard.Endpoint(hint);
    }

    public async Task<OuroborosRunsAnswer> SummariesAsync(OuroborosDashboardEndpoint? endpoint, CancellationToken cancellation = default)
    {
        if (endpoint is null || await transport.GetAsync(OuroborosDashboard.RunsURL(endpoint), TimeSpan.FromSeconds(4), cancellation).ConfigureAwait(false) is not { } response) return OuroborosRunsAnswer.NoAnswer;
        return new(true, response.Status, response.Status == 200 ? OuroborosDashboard.Summaries(response.Body) : null);
    }

    /// The newest complete board the stream sent before it went quiet.
    public async Task<OuroborosBoard?> BoardAsync(OuroborosDashboardEndpoint endpoint, string executionID, CancellationToken cancellation = default)
    {
        if (OuroborosDashboard.EventsURL(endpoint, executionID) is not { } url
            || await transport.ReadEventsAsync(url, FirstEventDeadline, TimeSpan.FromSeconds(1.2), TimeSpan.FromSeconds(8), MaximumBoardBytes, cancellation).ConfigureAwait(false) is not { } data) return null;
        return OuroborosDashboard.SseData(Encoding.UTF8.GetString(data)).LastOrDefault() is { } frame ? OuroborosDashboard.Board(Encoding.UTF8.GetBytes(frame)) : null;
    }

    /// Boards for several executions, at most <see cref="BoardConcurrency"/> streams open at once, so one quiet run
    /// does not hold up the others.
    public async Task<Dictionary<string, OuroborosBoard>> BoardsAsync(OuroborosDashboardEndpoint endpoint, IEnumerable<string> executionIDs, CancellationToken cancellation = default)
    {
        var result = new Dictionary<string, OuroborosBoard>(StringComparer.Ordinal);
        using var gate = new SemaphoreSlim(BoardConcurrency, BoardConcurrency);
        await Task.WhenAll(executionIDs.Distinct(StringComparer.Ordinal).Select(async id =>
        {
            await gate.WaitAsync(cancellation).ConfigureAwait(false);
            try { if (await BoardAsync(endpoint, id, cancellation).ConfigureAwait(false) is { } board) lock (result) result[id] = board; }
            finally { gate.Release(); }
        })).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// Argument array for the tool python; null for an id that is not strict. <c>-I</c> keeps Python from reading
    /// <c>PYTHON*</c> variables, the user site and the working directory; the id is its own argument after <c>--run</c>.
    /// </summary>
    public static IReadOnlyList<string>? EnsureArguments(string? executionID) =>
        executionID is null ? ["-I", "-m", "ouroboros.dashboard_web"] : OuroborosExecutionLinks.ValidID(executionID) ? ["-I", "-m", "ouroboros.dashboard_web", "--run", executionID] : null;

    /// <summary>
    /// Only what Python and Ouroboros need, never the app's whole environment (an agent's <c>PYTHONPATH</c> and the
    /// like). The two <c>PYTHON*</c> flags reach the detached daemon, which Ouroboros starts without <c>-I</c>. On
    /// Windows a Python process also needs the system's own folders and shell (<c>ComSpec</c>, <c>PATHEXT</c>: a start
    /// through <c>subprocess</c> or <c>shutil.which</c> finds nothing without them), the user's folders and the
    /// machine's processor and user names, which the standard library reads from the environment there.
    /// </summary>
    public static Dictionary<string, string> DaemonEnvironment(string home, IReadOnlyDictionary<string, string> inherited, bool windows)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal) { ["HOME"] = home, ["PYTHONSAFEPATH"] = "1", ["PYTHONNOUSERSITE"] = "1" };
        if (windows)
        {
            environment["USERPROFILE"] = home;
            foreach (var key in new[] { "SystemRoot", "SystemDrive", "windir", "ComSpec", "PATHEXT", "APPDATA", "LOCALAPPDATA", "ProgramData", "TEMP", "TMP", "USERNAME", "NUMBER_OF_PROCESSORS", "PROCESSOR_ARCHITECTURE" })
                if (inherited.GetValueOrDefault(key) is { Length: > 0 } value) environment[key] = value;
            environment["PATH"] = inherited.GetValueOrDefault("SystemRoot") is { Length: > 0 } root ? Path.Combine(root, "System32") + ";" + root : "";
            return environment;
        }
        environment["PATH"] = "/usr/bin:/bin:/usr/sbin:/sbin";
        foreach (var key in new[] { "LANG", "LC_ALL", "TMPDIR" })
            if (inherited.GetValueOrDefault(key) is { Length: > 0 } value) environment[key] = value;
        if (!environment.ContainsKey("LANG") && !environment.ContainsKey("LC_ALL")) environment["LANG"] = "en_US.UTF-8";
        return environment;
    }

    /// Ouroboros' own singleton election brings the daemon up (or reuses it); no shell, a fixed argument array and
    /// environment, and a timeout.
    /// A cancelled wait stops waiting at once; a start already running is left to its own timeout.
    public async Task<bool> EnsureDaemonAsync(string? executionID, CancellationToken cancellation = default)
    {
        if (EnsureArguments(executionID) is not { } arguments || PythonCandidates.FirstOrDefault(File.Exists) is not { } python) return false;
        var inherited = System.Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>().ToDictionary(entry => (string)entry.Key, entry => entry.Value as string ?? "", StringComparer.OrdinalIgnoreCase);
        return await launcher(new(python, arguments, DaemonEnvironment(home, inherited, OperatingSystem.IsWindows()), DaemonDirectory)).WaitAsync(cancellation).ConfigureAwait(false);
    }
}

/// <summary>
/// HTTP against loopback only (macOS LoopbackDashboardTransport): no proxy, no cookies and no redirects, so a
/// response can never send the app anywhere else.
/// </summary>
public sealed class LoopbackDashboardTransport : IOuroborosDashboardTransport
{
    public const int MaximumBytes = 4 * 1024 * 1024;
    private static readonly HttpClient Client = new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, UseCookies = false, ConnectTimeout = TimeSpan.FromSeconds(4) }) { Timeout = Timeout.InfiniteTimeSpan };

    public async Task<(int Status, byte[] Body)?> GetAsync(Uri url, TimeSpan timeout, CancellationToken cancellation) =>
        await Read(url, timeout, null, null, MaximumBytes, cancellation).ConfigureAwait(false);

    public async Task<byte[]?> ReadEventsAsync(Uri url, TimeSpan firstEvent, TimeSpan quiet, TimeSpan deadline, int maximumBytes, CancellationToken cancellation) =>
        await Read(url, deadline, quiet, firstEvent, maximumBytes, cancellation).ConfigureAwait(false) is { Status: 200 } response ? response.Body : null;

    /// <summary>
    /// One read. A plain GET is its whole body within <paramref name="timeout"/> and under
    /// <paramref name="maximumBytes"/>, or nothing. An event stream (<paramref name="quiet"/> set) is what arrived
    /// when it went quiet, sent no event in time, ran out its time or filled its room.
    /// </summary>
    private static async Task<(int Status, byte[] Body)?> Read(Uri url, TimeSpan timeout, TimeSpan? quiet, TimeSpan? firstEvent, int maximumBytes, CancellationToken cancellation)
    {
        if (OuroborosDashboard.Endpoint(url) is null) return null;
        var started = Stopwatch.StartNew();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        limit.CancelAfter(timeout);
        // An event stream that has not even answered by its first-event deadline has nothing to show yet.
        using var opening = CancellationTokenSource.CreateLinkedTokenSource(limit.Token);
        if (quiet is not null && firstEvent is { } first) opening.CancelAfter(first);
        var body = new MemoryStream();
        var status = 0;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, opening.Token).ConfigureAwait(false);
            var stream = await response.Content.ReadAsStreamAsync(opening.Token).ConfigureAwait(false);
            await using var _ = stream.ConfigureAwait(false);
            status = (int)response.StatusCode;
            var buffer = new byte[16 * 1024];
            TimeSpan? lastEvent = null;
            while (true)
            {
                using var step = CancellationTokenSource.CreateLinkedTokenSource(limit.Token);
                if (quiet is { } still) step.CancelAfter(Positive(lastEvent is { } seen ? seen + still - started.Elapsed : (firstEvent ?? timeout) - started.Elapsed));
                int read;
                try { read = await stream.ReadAsync(buffer, step.Token).ConfigureAwait(false); }
                // The stream went quiet or sent nothing in time: what arrived is the answer.
                catch (OperationCanceledException) when (quiet is not null && !cancellation.IsCancellationRequested) { break; }
                if (read == 0) break;
                // An event ends at a blank line, written with LF or CRLF line ends: one may straddle two reads.
                var overlap = (int)Math.Min(body.Length, 3);
                body.Write(buffer, 0, read);
                if (quiet is not null && EndsEvent(body.GetBuffer().AsSpan((int)body.Length - read - overlap, read + overlap))) lastEvent = started.Elapsed;
                if (body.Length >= maximumBytes) { if (quiet is null) return null; break; }
            }
        }
        catch (OperationCanceledException) { if (quiet is null || cancellation.IsCancellationRequested) return null; }
        catch (Exception ex) when (ex is HttpRequestException or IOException) { if (quiet is null) return null; }
        return status != 0 && !cancellation.IsCancellationRequested ? (status, body.ToArray()) : null;
    }

    private static TimeSpan Positive(TimeSpan value) => value > TimeSpan.FromMilliseconds(1) ? value : TimeSpan.FromMilliseconds(1);
    private static bool EndsEvent(ReadOnlySpan<byte> fresh) => fresh.IndexOf("\n\n"u8) >= 0 || fresh.IndexOf("\r\n\r\n"u8) >= 0;
}

// ── the pane's executions ─────────────────────────────────────────────────────

/// <summary>
/// Live state of the background Ouroboros executions a pane started, keyed by <see cref="OuroborosExecutionLink.Key"/>
/// (macOS MightyGraphOuroborosModel). The diagram owns one and reads it only while it shows, so polling stops when
/// the graph leaves the screen or the pane closes. Daemon starts are counted process-wide
/// (<see cref="OuroborosDaemonStarts"/>), so a new one for the same pane does not start it again sooner. Not
/// thread-safe: one caller at a time, as the diagram's own thread is.
/// </summary>
public sealed class OuroborosExecutionMonitor(OuroborosDashboardClient client, OuroborosDaemonStarts? starts = null)
{
    private readonly OuroborosDaemonStarts starts = starts ?? OuroborosDaemonStarts.Shared;
    private readonly Dictionary<string, OuroborosExecutionSnapshot> snapshots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OuroborosExecutionPoll> polls = new(StringComparer.Ordinal);
    /// Blocks whose "open dashboard" click is still being handled, and those whose last click showed nothing.
    private readonly HashSet<string> opening = new(StringComparer.Ordinal), openFailed = new(StringComparer.Ordinal);

    public OuroborosExecutionSnapshot? Snapshot(string key) => snapshots.GetValueOrDefault(key);
    public bool Opening(string key) => opening.Contains(key);
    public bool OpenFailed(string key) => openFailed.Contains(key);

    /// <summary>
    /// The pane's executions as drawn, and the blocks whose AC list is open (only these read the board stream). A
    /// block that is gone takes its state with it; an AC list opened with nothing read yet reads now, not at the
    /// block's next (possibly slow) turn or never.
    /// </summary>
    public void Track(IReadOnlyList<OuroborosExecutionLink> links, IReadOnlySet<string> listKeys, DateTimeOffset now)
    {
        var keys = links.Select(link => link.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var gone in snapshots.Keys.Where(key => !keys.Contains(key)).ToArray()) snapshots.Remove(gone);
        foreach (var gone in polls.Keys.Where(key => !keys.Contains(key)).ToArray()) polls.Remove(gone);
        opening.IntersectWith(keys); openFailed.IntersectWith(keys);
        foreach (var link in links)
        {
            if (!polls.TryGetValue(link.Key, out var poll)) polls[link.Key] = new(now, OuroborosExecutionPoll.IsStale(link.Timestamp, now));
            else if (listKeys.Contains(link.Key) && snapshots.GetValueOrDefault(link.Key)?.Items is null) poll.Wake(now);
        }
    }

    /// The executions to read now, and when the next one is due: null once every block stopped (terminal, job-only
    /// or unanswered history).
    public List<OuroborosExecutionLink> Due(IReadOnlyList<OuroborosExecutionLink> links, DateTimeOffset now) => links.Where(link => polls.GetValueOrDefault(link.Key)?.IsDue(now) == true).ToList();
    public DateTimeOffset? NextAt(IReadOnlyList<OuroborosExecutionLink> links) => links.Select(link => polls.GetValueOrDefault(link.Key)?.NextAt).Where(at => at is not null).Min();

    /// <summary>
    /// One read of the executions that are due, in three steps so its network, daemon start and parsing run away from
    /// the monitor's own thread: <see cref="Begin"/> and <see cref="Apply"/> on that thread, <see cref="Read.RunAsync"/>
    /// on any. The read touches none of the monitor's state.
    /// </summary>
    public sealed class Read
    {
        private readonly OuroborosDashboardClient client;
        private readonly OuroborosDaemonStarts starts;
        private readonly IReadOnlySet<string> listKeys, mayEnsure;
        internal Read(OuroborosDashboardClient client, OuroborosDaemonStarts starts, IReadOnlyList<OuroborosExecutionLink> due, IReadOnlySet<string> listKeys, IReadOnlySet<string> mayEnsure)
        {
            this.client = client; this.starts = starts; Due = due; this.listKeys = listKeys; this.mayEnsure = mayEnsure;
        }
        internal IReadOnlyList<OuroborosExecutionLink> Due { get; }
        internal OuroborosDashboardEndpoint? Endpoint { get; private set; }
        internal OuroborosRunsAnswer Answer { get; private set; } = OuroborosRunsAnswer.NoAnswer;
        internal Dictionary<string, OuroborosBoard> Boards { get; private set; } = [];

        public async Task RunAsync(Func<DateTimeOffset> clock, CancellationToken cancellation = default)
        {
            var hint = Due.Select(link => link.DashboardURL).FirstOrDefault(url => url is not null);
            Endpoint = await client.EndpointAsync(hint).ConfigureAwait(false);
            Answer = await client.SummariesAsync(Endpoint, cancellation).ConfigureAwait(false);
            // Nothing answers: bring the daemon up for a live execution whose start announced a dashboard (so Ouroboros'
            // dashboard is not turned off), a few times per execution at most and never for history. A dashboard that
            // answered and cannot read its runs is up already.
            if (!Answer.Answered && !cancellation.IsCancellationRequested)
            {
                string? candidate = null;
                foreach (var link in Due)
                    if (link.ExecutionID is { } executionID && link.DashboardURL is not null && mayEnsure.Contains(link.Key) && starts.Claim(executionID, clock())) candidate ??= executionID;
                if (candidate is not null && !cancellation.IsCancellationRequested && await client.EnsureDaemonAsync(candidate, cancellation).ConfigureAwait(false))
                {
                    Endpoint = await client.EndpointAsync(hint).ConfigureAwait(false);
                    Answer = await client.SummariesAsync(Endpoint, cancellation).ConfigureAwait(false);
                }
            }
            // Boards for open AC lists, and for runs /api/runs no longer lists (it holds only the newest): their board
            // is all that is left.
            if (Endpoint is { } endpoint && Answer.Summaries is { } listed)
            {
                var ids = Due.Where(link => link.ExecutionID is { } id && (listKeys.Contains(link.Key) || !listed.ContainsKey(id))).Select(link => link.ExecutionID!).ToList();
                if (ids.Count > 0 && !cancellation.IsCancellationRequested) Boards = await client.BoardsAsync(endpoint, ids, cancellation).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Starts a read of the executions that are due, with the blocks whose AC list is open.</summary>
    public Read Begin(IReadOnlyList<OuroborosExecutionLink> due, IReadOnlySet<string> listKeys) =>
        new(client, starts, [.. due], listKeys.ToHashSet(StringComparer.Ordinal), due.Where(link => polls.GetValueOrDefault(link.Key)?.MayEnsureDaemon == true).Select(link => link.Key).ToHashSet(StringComparer.Ordinal));

    /// <summary>One read of the executions that are due. Returns whether any block's snapshot changed.</summary>
    public async Task<bool> TickAsync(IReadOnlyList<OuroborosExecutionLink> due, IReadOnlySet<string> listKeys, Func<DateTimeOffset> clock, CancellationToken cancellation = default)
    {
        var read = Begin(due, listKeys);
        await read.RunAsync(clock, cancellation);
        return !cancellation.IsCancellationRequested && Apply(read, clock());
    }

    /// <summary>What a finished read saw, given to its blocks. Returns whether any block's snapshot changed.</summary>
    public bool Apply(Read read, DateTimeOffset now)
    {
        var (due, endpoint, answer, boards) = (read.Due, read.Endpoint, read.Answer, read.Boards);
        var changed = false;
        foreach (var link in due)
        {
            // A block that left the diagram while this read was on its way has nothing to show.
            if (!polls.TryGetValue(link.Key, out var poll)) continue;
            var page = endpoint is null ? link.DashboardURL : OuroborosDashboard.PageURL(endpoint, link.ExecutionID);
            var previous = snapshots.GetValueOrDefault(link.Key);
            OuroborosExecutionSnapshot snapshot;
            if (link.ExecutionID is { } executionID && answer.Summaries is { } runs)
                snapshot = OuroborosExecutionSnapshot.Merge(runs.GetValueOrDefault(executionID), boards.GetValueOrDefault(executionID), previous?.Items, page);
            else if (link.ExecutionID is null) snapshot = new(OuroborosExecutionSnapshot.Sources.NoExecution, DashboardURL: page);
            // Keep what was last seen, but say the status is not known now.
            else snapshot = (previous ?? new(OuroborosExecutionSnapshot.Sources.Unreachable)) with
            {
                Source = answer.Answered ? OuroborosExecutionSnapshot.Sources.Unreadable : OuroborosExecutionSnapshot.Sources.Unreachable,
                Status = OuroborosExecutionStatus.Unknown, DashboardURL = page,
            };
            if (!snapshot.Equals(previous)) { snapshots[link.Key] = snapshot; changed = true; }
            poll.Record(snapshot, now);
        }
        return changed;
    }

    /// <summary>
    /// The "open dashboard" click. It may start the daemon even for history or with the dashboard turned off (the
    /// user asked for it), but never for one that answered. One click at a time per block; false when another is
    /// still being handled. <paramref name="open"/> says whether anything showed.
    /// </summary>
    public async Task<bool> OpenDashboardAsync(OuroborosExecutionLink link, Func<Uri, Task<bool>> open)
    {
        if (!opening.Add(link.Key)) return false;
        openFailed.Remove(link.Key);
        var opened = false;
        try
        {
            var endpoint = await client.EndpointAsync(link.DashboardURL);
            if (!(await client.SummariesAsync(endpoint)).Answered && await client.EnsureDaemonAsync(link.ExecutionID)) endpoint = await client.EndpointAsync(link.DashboardURL);
            if ((endpoint is null ? link.DashboardURL : OuroborosDashboard.PageURL(endpoint, link.ExecutionID)) is { } url) opened = await open(url);
        }
        finally
        {
            opening.Remove(link.Key);
            if (!opened) openFailed.Add(link.Key);
        }
        return true;
    }
}
