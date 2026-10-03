using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace MightyClaude.Core;

/// <summary>
/// An earlier Claude or Codex session recorded for a workspace folder that a new
/// pane can continue with <c>--resume</c> / <c>exec resume</c>.
/// </summary>
public sealed record ResumableSession(string Provider, string SessionID, string? Title, DateTimeOffset Modified, int? Requests, string? Model, string Path, bool Automated = false)
{
    public string Id => Provider + ":" + SessionID;
}

/// <summary>One listing: what to show, and how many automated sessions were left out.</summary>
public sealed record ResumableSessionListing(List<ResumableSession> Items, int Hidden)
{
    public static ResumableSessionListing Empty => new([], 0);
}

public sealed record ResumableSessionQuery(string WorkspacePath)
{
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();
    public string Home { get; init; } = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
    /// Session ids open panes already use (any provider, any case).
    public IReadOnlySet<string> Excluding { get; init; } = new HashSet<string>();
    /// Session ids the app's own panes started or resumed: never hidden.
    public IReadOnlySet<string> Known { get; init; } = new HashSet<string>();
    /// Lists automated sessions too ("모든 세션 보기").
    public bool IncludeAutomated { get; init; }
    public DateTimeOffset Now { get; init; } = DateTimeOffset.UtcNow;
    public TimeSpan MaximumAge { get; init; } = ResumableSessions.MaximumAge;
    /// Records read for a row per provider, newest first; excluded and hidden ones use no slot.
    public int MaximumCandidates { get; init; } = ResumableSessions.MaximumCandidates;
    /// Records looked at per provider at all, hidden ones included.
    public int MaximumScanned { get; init; } = ResumableSessions.MaximumScanned;
    public int MaximumSessions { get; init; } = ResumableSessions.MaximumSessions;
    /// Reads only what decides whether a record is listed: no request count, no
    /// model from the record's end. For "창 추가"'s yes/no look-up.
    public bool HeadOnly { get; init; }
}

/// <summary>
/// Lists the sessions a workspace folder's CLIs recorded, read-only and bounded.
/// Synchronous and file-bound: call it off the UI thread. Gemini is not listed:
/// the app does not read its records. Port of macOS MightyCore/ResumableSessions.swift.
/// </summary>
public static class ResumableSessions
{
    public static readonly string[] Providers = ["claude", "codex"];
    public static readonly TimeSpan MaximumAge = TimeSpan.FromDays(60);
    public const int MaximumCandidates = 600;
    public const int MaximumScanned = 6_000;
    public const int MaximumSessions = 200;
    /// A record written this recently may belong to a CLI still running.
    public static readonly TimeSpan BusyInterval = TimeSpan.FromSeconds(120);
    private const long HeadBytes = 2 * 1_048_576;
    private const long MetaBytes = 512 * 1_024;
    private const long CountBytes = 1_048_576;
    private const long CountBudget = 96 * 1_048_576;
    private const int TitleCharacters = 200;
    private static readonly Regex Uuid = new(@"^[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\z");
    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// Whether the menu asks "새로 시작 / 이어가기…" for this provider at all.
    public static bool OffersResume(string kind, string provider) => kind == "claude" && Providers.Contains(provider);

    public static ResumableSessionListing Listing(ResumableSessionQuery query, CancellationToken token = default)
    {
        var budget = CountBudget;
        var claude = Claude(query, ref budget, token); var codex = Codex(query, ref budget, token);
        var items = claude.Items.Concat(codex.Items).OrderByDescending(i => i.Modified).ThenBy(i => i.Id, StringComparer.Ordinal).ToList();
        return new(items, claude.Hidden + codex.Hidden);
    }

    /// One agent's sessions alone: the other agent's records are not read.
    public static ResumableSessionListing Listing(ResumableSessionQuery query, string provider, CancellationToken token = default)
    {
        var budget = CountBudget;
        return provider switch { "claude" => Claude(query, ref budget, token), "codex" => Codex(query, ref budget, token), _ => ResumableSessionListing.Empty };
    }

    /// <summary>
    /// A nested non-interactive run (an Ouroboros step's <c>claude --print</c>) sends a
    /// flattened transcript as its first prompt ("User: …" / "Assistant: …"); a
    /// person types no such thing. Codex <c>exec</c> records carry no marker that
    /// tells such a run from the app's own panes, so the same text rule decides there too.
    /// </summary>
    public static bool AutomatedPrompt(string? prompt) =>
        prompt is not null && (prompt.StartsWith("User: ", StringComparison.Ordinal) || prompt.StartsWith("Assistant: ", StringComparison.Ordinal)
            || prompt.StartsWith("User:\n", StringComparison.Ordinal) || prompt.StartsWith("Assistant:\n", StringComparison.Ordinal));

    /// Whether the record was written so recently that a CLI elsewhere may still run the session.
    public static bool MayBeRunning(ResumableSession item, DateTimeOffset now) => now - item.Modified < BusyInterval;

    /// Ids of sessions open panes continue, lowercased.
    public static HashSet<string> InUse(IEnumerable<RunSession> sessions) =>
        sessions.Where(s => s.Kind == "claude" && s.ResumeId is not null).Select(s => s.ResumeId!.ToLowerInvariant()).ToHashSet();

    /// Titles containing every word of the query, ignoring case and width.
    public static List<ResumableSession> Filter(IEnumerable<ResumableSession> items, string query)
    {
        var words = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return items.ToList();
        var compare = CultureInfo.InvariantCulture.CompareInfo;
        const CompareOptions options = CompareOptions.IgnoreCase | CompareOptions.IgnoreWidth | CompareOptions.IgnoreNonSpace;
        return items.Where(item => words.All(word => compare.IndexOf(item.Title ?? "", word, options) >= 0)).ToList();
    }

    /// Makes a freshly added agent pane continue the session: it resumes that
    /// session and is titled after its first request. The title stays automatic,
    /// so the next request retitles the pane as usual.
    public static RunSession Apply(ResumableSession item, RunSession session) =>
        session with { Provider = item.Provider, ResumeId = item.SessionID, Title = PaneTitle.Shortened(item.Title) ?? session.Title, TitleMode = PaneTitle.Automatic };

    /// "방금", "5분 전", "3시간 전", "12일 전".
    public static string RelativeTime(DateTimeOffset date, DateTimeOffset now)
    {
        var seconds = Math.Max(0, (now - date).TotalSeconds);
        static Dictionary<string, string> Count(double value) => new() { ["count"] = ((int)value).ToString(CultureInfo.InvariantCulture) };
        if (seconds < 60) return Locale.Get("resume.time.now");
        if (seconds < 3_600) return Locale.Get("resume.time.minutes", Count(seconds / 60));
        if (seconds < 86_400) return Locale.Get("resume.time.hours", Count(seconds / 3_600));
        return Locale.Get("resume.time.days", Count(seconds / 86_400));
    }

    /// The row's second line: when, how many requests, which model, and the
    /// "방금 수정됨" and "자동 실행 기록" notes (macOS ResumeSessionSheet).
    public static string Details(ResumableSession item, DateTimeOffset now)
    {
        var parts = new List<string> { RelativeTime(item.Modified, now) };
        if (item.Requests is { } requests) parts.Add(Locale.Get("resume.requests", new Dictionary<string, string> { ["count"] = requests.ToString(CultureInfo.InvariantCulture) }));
        if (item.Model is { Length: > 0 } model) parts.Add(ModelLabel.Text(model));
        if (item.Automated) parts.Add(Locale.Get("resume.automated"));
        if (MayBeRunning(item, now)) parts.Add(Locale.Get("resume.recentlyModified"));
        return string.Join(" · ", parts);
    }

    public static string RowTitle(ResumableSession item) => item.Title ?? Locale.Get("resume.untitled");

    // ── Claude ────────────────────────────────────────────────────────────

    private enum Outcome { Listed, Hidden, Skipped }

    private static ResumableSessionListing Claude(ResumableSessionQuery query, ref long budget, CancellationToken token)
    {
        var projects = System.IO.Path.Combine(SessionHistory.ClaudeConfig(query.Home, query.Environment), "projects");
        var paths = WorkspacePaths(query.WorkspacePath);
        var folders = paths.Select(SessionHistory.ClaudeProjectFolder).Distinct().ToList();
        var candidates = new List<(string Path, string Id, DateTimeOffset Modified)>();
        var seen = new HashSet<string>();
        foreach (var folder in folders)
            foreach (var (path, modified) in RecordFiles.Files(System.IO.Path.Combine(projects, folder), ".jsonl"))
            {
                var id = System.IO.Path.GetFileNameWithoutExtension(path);
                if (!Uuid.IsMatch(id) || !seen.Add(id.ToLowerInvariant())) continue;
                candidates.Add((path, id, modified));
            }
        var items = new List<ResumableSession>(); int hidden = 0, read = 0;
        foreach (var candidate in Recent(candidates, query))
        {
            if (items.Count >= query.MaximumSessions || read >= query.MaximumCandidates || token.IsCancellationRequested) break;
            if (query.Excluding.Contains(candidate.Id.ToLowerInvariant())) continue;
            var known = query.Known.Contains(candidate.Id.ToLowerInvariant());
            var (outcome, item) = ClaudeSession(candidate.Path, candidate.Id, candidate.Modified, paths, known, query.IncludeAutomated, query.HeadOnly, ref budget);
            if (outcome == Outcome.Listed) { read++; items.Add(item!); }
            else if (outcome == Outcome.Hidden) hidden++;
            else read++;
        }
        return new(items, hidden);
    }

    /// The head alone decides whether a record is listed: its first working
    /// folder and its first request. Only a listed record is read further.
    private static (Outcome, ResumableSession?) ClaudeSession(string path, string id, DateTimeOffset modified, IReadOnlyList<string> paths, bool known, bool includeAutomated, bool headOnly, ref long budget)
    {
        using var handle = RecordFiles.Open(path);
        if (handle is null) return (Outcome.Skipped, null);
        var size = RandomAccess.GetLength(handle);
        if (size <= 0) return (Outcome.Skipped, null);
        string? title = null; bool cwdChecked = false, foreign = false;
        var headComplete = RecordFiles.ForEachLine(handle, size, HeadBytes, line =>
        {
            if (!cwdChecked && HistoryScan.Contains(line, "\"cwd\":") && HistoryScan.Parse(line) is { } value && value.Text("cwd") is { } cwd)
            {
                cwdChecked = true;
                if (MetadataJson.Flag(value, "isSidechain") || !Matches(cwd, paths)) { foreign = true; return false; }
            }
            if (title is null && HistoryScan.OpeningOf(line, HistoryFormat.Claude) is { } opening) title = opening.Prompt;
            return title is null || !cwdChecked;
        });
        // A finished record without a single request has nothing to continue.
        if (foreign || (title is null && headComplete)) return (Outcome.Skipped, null);
        var automated = !known && AutomatedPrompt(title);
        if (automated && !includeAutomated) return (Outcome.Hidden, null);
        var requests = Count(handle, size, headOnly, ref budget, line => HistoryScan.OpeningOf(line, HistoryFormat.Claude) is not null);
        var model = headOnly ? null : RecordFiles.TailValue(handle, size, ClaudeModel);
        return (Outcome.Listed, new ResumableSession("claude", id, title is null ? null : OneLine(title), modified, requests, model, path, automated));
    }

    private static int? Count(SafeFileHandle handle, long size, bool headOnly, ref long budget, Func<byte[], bool> opens)
    {
        if (headOnly || size > CountBytes || budget < size) return null;
        budget -= size;
        var count = 0;
        var complete = RecordFiles.ForEachLine(handle, size, size, line => { if (opens(line)) count++; return true; });
        return complete ? count : null;
    }

    private static string? ClaudeModel(byte[] line)
    {
        if (!HistoryScan.Contains(line, "\"type\":\"assistant\"") || !HistoryScan.Contains(line, "\"model\":") || HistoryScan.Parse(line) is not { } value
            || value.Text("type") != "assistant" || MetadataJson.Flag(value, "isSidechain")) return null;
        var model = MetadataJson.Property(value, "message").Text("model");
        return model is not null && !model.StartsWith('<') && Wire.Model(model) ? model : null;
    }

    // ── Codex ─────────────────────────────────────────────────────────────

    private static ResumableSessionListing Codex(ResumableSessionQuery query, ref long budget, CancellationToken token)
    {
        if (RecordFiles.Sessions(CliAccountSupport.CodexHome(query.Home, query.Environment)) is not { } sessions) return ResumableSessionListing.Empty;
        var paths = WorkspacePaths(query.WorkspacePath);
        var candidates = RecordFiles.DayFolders(sessions).SelectMany(RecordFiles.Rollouts).Select(r => (r.Path, Id: r.Thread, r.Modified)).ToList();
        var items = new List<ResumableSession>(); int hidden = 0, read = 0;
        var seen = new HashSet<string>();
        foreach (var candidate in Recent(candidates, query))
        {
            if (items.Count >= query.MaximumSessions || read >= query.MaximumCandidates || token.IsCancellationRequested) break;
            var lowered = candidate.Id.ToLowerInvariant();
            if (!Wire.Identifier(candidate.Id) || query.Excluding.Contains(lowered) || seen.Contains(lowered)) continue;
            var (outcome, item) = CodexSession(candidate.Path, candidate.Id, candidate.Modified, paths, query.Known.Contains(lowered), query.IncludeAutomated, query.HeadOnly, ref budget);
            if (outcome == Outcome.Listed) { read++; seen.Add(lowered); items.Add(item!); }
            else if (outcome == Outcome.Hidden) { seen.Add(lowered); hidden++; }
            else read++;
        }
        return new(items, hidden);
    }

    private static (Outcome, ResumableSession?) CodexSession(string path, string id, DateTimeOffset modified, IReadOnlyList<string> paths, bool known, bool includeAutomated, bool headOnly, ref long budget)
    {
        using var handle = RecordFiles.Open(path);
        if (handle is null) return (Outcome.Skipped, null);
        var size = RandomAccess.GetLength(handle);
        if (size <= 0) return (Outcome.Skipped, null);
        // The first line alone decides; most of a long line is instructions.
        byte[]? first = null;
        RecordFiles.ForEachLine(handle, size, MetaBytes, line => { first = line; return false; });
        if (first is null || HistoryScan.Parse(first) is not { } meta || meta.Text("type") != "session_meta") return (Outcome.Skipped, null);
        var payload = MetadataJson.Property(meta, "payload");
        if (payload.Text("cwd") is not { } cwd || !Matches(cwd, paths)) return (Outcome.Skipped, null);
        if (MetadataJson.Property(payload, "source") is { ValueKind: JsonValueKind.Object } source && source.TryGetProperty("subagent", out _)) return (Outcome.Skipped, null);
        // The first turn's lines that can carry its user text, read until the
        // turn's text is certain or the next turn starts.
        string? title = null, headModel = null; var turn = new List<byte[]>(); var inTurn = false;
        var format = HistoryFormat.Codex(id);
        RecordFiles.ForEachLine(handle, size, HeadBytes, line =>
        {
            headModel ??= HistoryReplay.CodexModel([line]);
            if (HistoryScan.OpeningOf(line, format) is not null)
            {
                if (inTurn && HistoryScan.CodexPrompt(turn) is { } prompt) { title = prompt; return false; }
                inTurn = true; turn = [];
                return true;
            }
            if (!HistoryScan.CodexUserLine(line)) return true;
            turn.Add(line);
            // The item or the event is the turn's own text; nothing can beat it.
            if (HistoryScan.CodexUserItem(line) is not null || HistoryScan.CodexUserEvent(line) is not null) { title = HistoryScan.CodexPrompt(turn); return false; }
            return true;
        });
        title ??= HistoryScan.CodexPrompt(turn);
        var automated = !known && AutomatedPrompt(title);
        if (automated && !includeAutomated) return (Outcome.Hidden, null);
        var requests = Count(handle, size, headOnly, ref budget, line => { headModel ??= HistoryReplay.CodexModel([line]); return HistoryScan.OpeningOf(line, format) is not null; });
        var model = headOnly ? headModel : RecordFiles.TailValue(handle, size, line => HistoryReplay.CodexModel([line])) ?? headModel;
        return (Outcome.Listed, new ResumableSession("codex", id, title is null ? null : OneLine(title), modified, requests, model, path, automated));
    }

    // ── reading ───────────────────────────────────────────────────────────

    private static string Normalized(string path)
    {
        var root = System.IO.Path.GetPathRoot(path) ?? "";
        while (path.Length > Math.Max(1, root.Length) && (path.EndsWith('/') || (OperatingSystem.IsWindows() && path.EndsWith('\\')))) path = path[..^1];
        return path;
    }
    private static string? Resolved(string path)
    {
        try
        {
            var full = System.IO.Path.GetFullPath(path);
            var info = new DirectoryInfo(full);
            return info.LinkTarget is not null ? info.ResolveLinkTarget(true)?.FullName : full;
        }
        catch (IOException) { return null; } catch (UnauthorizedAccessException) { return null; } catch (ArgumentException) { return null; }
    }
    /// The workspace path as given and resolved, without a trailing separator.
    public static IReadOnlyList<string> WorkspacePaths(string path)
    {
        var paths = new List<string> { Normalized(path) };
        if (Resolved(path) is { } real && !paths.Contains(Normalized(real), StringComparer.FromComparison(PathComparison))) paths.Add(Normalized(real));
        return paths;
    }
    private static bool Matches(string cwd, IReadOnlyList<string> paths) =>
        paths.Any(p => string.Equals(p, Normalized(cwd), PathComparison)) || (Resolved(cwd) is { } real && paths.Any(p => string.Equals(p, Normalized(real), PathComparison)));

    /// Within the age limit, newest first, at most the scan limit.
    private static IEnumerable<(string Path, string Id, DateTimeOffset Modified)> Recent(List<(string Path, string Id, DateTimeOffset Modified)> candidates, ResumableSessionQuery query)
    {
        var oldest = query.Now - query.MaximumAge;
        return candidates.Where(c => c.Modified >= oldest).OrderByDescending(c => c.Modified).ThenBy(c => c.Id, StringComparer.Ordinal).Take(Math.Max(0, query.MaximumScanned));
    }

    public static string OneLine(string text)
    {
        // A pasted prompt can be very long; only its start can show.
        var limit = TitleCharacters * 8;
        var start = text.Length > limit ? text[..limit] : text;
        var collapsed = string.Join(' ', start.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var elements = StringInfo.ParseCombiningCharacters(collapsed);
        var cut = elements.Length > TitleCharacters || start.Length < text.Length;
        return cut ? (elements.Length > TitleCharacters ? collapsed[..elements[TitleCharacters]] : collapsed) + "…" : collapsed;
    }
}

/// <summary>
/// Session ids the app's own panes started or resumed, oldest first, kept in a
/// small file (<c>known-sessions.json</c>) in the app's data folder: the session
/// list never hides them as automated runs.
/// </summary>
public static class KnownSessionIDs
{
    public const int Maximum = 2_000;
    private const long MaximumFileBytes = 256 * 1_024;
    public const string FileName = "known-sessions.json";

    public static List<string> Load(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaximumFileBytes || info.LinkTarget is not null) return [];
            var ids = JsonSerializer.Deserialize<List<string>>(File.ReadAllBytes(path)) ?? [];
            return ids.Where(Wire.Identifier).TakeLast(Maximum).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return []; }
    }
    /// The list with the id added as its newest entry, dropping the oldest past
    /// the limit; null when nothing changes.
    public static List<string>? Adding(string id, IReadOnlyList<string> ids)
    {
        if (!Wire.Identifier(id) || ids.Any(known => string.Equals(known, id, StringComparison.OrdinalIgnoreCase))) return null;
        return ids.Append(id).TakeLast(Maximum).ToList();
    }
    public static void Save(IReadOnlyList<string> ids, string path)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(ids.TakeLast(Maximum).ToList()));
        File.Move(temporary, path, overwrite: true);
    }
}
