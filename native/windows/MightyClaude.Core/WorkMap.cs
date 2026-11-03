using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace MightyClaude.Core;

/// <summary>
/// The work map of docs/work-map.md: the git history of a workspace split by version tag, read by platform, release state and
/// the user's own requests. The rules are held to native/contracts/fixtures/work-map.json, as MightyCore/WorkMap.swift holds them.
/// Everything here only reads; nothing is written to the repository.
/// </summary>
public sealed record WorkMapCommit(string Sha, string Date, IReadOnlyList<string> Lanes, string? Scope, string Subject, IReadOnlyList<string> Files, int FilesCount);
public sealed record WorkMapRequest(string Id, string PaneId, string Text, string? Date, IReadOnlyList<string> Files, IReadOnlyList<string> Commits);
public sealed record WorkMapTopic(string Title, string Summary, IReadOnlyList<string> Lanes, IReadOnlyList<string> Commits, IReadOnlyList<string> Requests);
public sealed record WorkMapVersion(string Key, string? Version, string? Tag, string? Date, string Status, string? ReleaseStep, string RangeEnd, int Total, bool Truncated,
    IReadOnlyDictionary<string, int> Counts, IReadOnlyList<WorkMapCommit> Commits, IReadOnlyList<WorkMapTopic>? Topics, bool TopicsStale, string? SummaryError, IReadOnlyList<WorkMapRequest> Requests);
public sealed record WorkMapModel(bool Available, string? Reason, string? Head, IReadOnlyList<string> Lanes, string? Summarizing, IReadOnlyList<WorkMapVersion> Versions)
{
    public static WorkMapModel Unavailable(string reason) => new(false, reason, null, WorkMap.Lanes, null, []);
}

/// <summary>One run of an open pane the user typed a request in, with what it edited: the raw material of <see cref="WorkMapRequest"/>.</summary>
public sealed record WorkMapRequestInput(string PaneId, string RunId, string Text, string? Date, IReadOnlyList<string> Files);

/// <summary>A saved topic summary of one version (<see cref="WorkMapSummaryStore"/>).</summary>
public sealed record WorkMapStoredSummary(string RangeEnd, IReadOnlyList<WorkMapTopic> Topics, string Created);

/// <summary>The work map's pane: a RunSession of its own kind that runs nothing and is never saved, one per workspace, placed as the files pane is.</summary>
public static class WorkMapPaneKind
{
    public const string Kind = "workmap";
    public static string PaneId(string workspaceId) => Kind + ":" + workspaceId;
    public static bool IsWorkMap(string? kind) => kind == Kind;
}

public static class WorkMap
{
    public const int MaximumVersions = 12;
    public const int MaximumCommitsPerVersion = 400;
    public const int MaximumFilesPerCommit = 50;
    public const int MaximumRequests = 200;
    public const int MaximumCommitsPerRequest = 20;
    public const int RequestTextLength = 200;
    public const double GitTimeoutSeconds = 10;
    public static readonly IReadOnlyList<string> Lanes = ["mac", "windows", "phone", "ci", "etc"];
    public static readonly IReadOnlyList<string> ReleaseSteps = ["check", "version", "push", "ci", "download", "manifest", "page", "upload", "github", "verify"];
    private static readonly Regex TagPattern = new(@"^v\d+\.\d+\.\d+$", RegexOptions.CultureInvariant);
    private static readonly Regex VersionPattern = new(@"^\d+\.\d+\.\d+$", RegexOptions.CultureInvariant);
    private static readonly Regex ParenScope = new(@"^\(([^)]*)\)\s*(.*)$", RegexOptions.Singleline | RegexOptions.CultureInvariant);
    private static readonly Regex ConventionalScope = new(@"^\w[\w-]*\(([^)]*)\)!?:\s*(.*)$", RegexOptions.Singleline | RegexOptions.CultureInvariant);

    /// <summary>The model as the page and the phone read it: camelCase, every field present (a missing one reads as null on both).</summary>
    public static readonly JsonSerializerOptions Json = new(Wire.Json) { DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    // ---- rules ----

    /// <summary>The platforms a commit title's scope names, in <see cref="Lanes"/> order, and the scope and title it leaves.</summary>
    public static (IReadOnlyList<string> Lanes, string? Scope, string Text) ParseSubject(string subject)
    {
        string? scope = null; var text = subject;
        if (ParenScope.Match(subject) is { Success: true } paren) { scope = paren.Groups[1].Value; text = paren.Groups[2].Value; }
        else if (ConventionalScope.Match(subject) is { Success: true } conventional) { scope = conventional.Groups[1].Value; text = conventional.Groups[2].Value; }
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var range in (scope ?? "").Split(','))
        {
            var word = range.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToLowerInvariant();
            var lane = word switch
            {
                "mac" or "macos" => "mac",
                "windows" or "web" or "winui" => "windows",
                "phone" or "mobile" or "android" => "phone",
                "ci" or "release" => "ci",
                _ => null,
            };
            if (lane is not null) found.Add(lane);
        }
        var lanes = found.Count == 0 ? ["etc"] : Lanes.Where(found.Contains).ToList();
        return (lanes, scope, text);
    }

    /// <summary>The release tags among <paramref name="names"/> (vX.Y.Z only), newest first by version number.</summary>
    public static IReadOnlyList<string> Tags(IEnumerable<string> names) =>
        names.Where(name => TagPattern.IsMatch(name)).OrderByDescending(TagVersion).ToList();

    private static (int, int, int) TagVersion(string name) => Numbers(name.TrimStart('v'));
    private static (int, int, int) Numbers(string text)
    {
        var parts = text.Split('.').Select(p => int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0).ToArray();
        return (parts[0], parts[1], parts[2]);
    }

    /// <summary>The name of the version being worked on: the VERSION file when it holds x.y.z above the newest tag, else null.</summary>
    public static string? VersionName(string? file, string? newestTag)
    {
        var text = file?.Trim();
        if (text is null || !VersionPattern.IsMatch(text)) return null;
        return newestTag is null || Numbers(text).CompareTo(TagVersion(newestTag)) > 0 ? text : null;
    }

    /// <summary>
    /// "releasing" and the step still to do when release/&lt;version&gt;/state.json says the push is done and the verify is not;
    /// otherwise "unreleased". <paramref name="stateJson"/> is the file's text; anything unreadable is not a release.
    /// </summary>
    public static (string Status, string? Step) ReleaseStatus(string? stateJson)
    {
        try
        {
            if (stateJson is null) return ("unreleased", null);
            using var document = JsonDocument.Parse(stateJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("done", out var done) || done.ValueKind != JsonValueKind.Object) return ("unreleased", null);
            bool Done(string step) => done.TryGetProperty(step, out var value) && value.ValueKind == JsonValueKind.True;
            if (!Done("push") || Done("verify")) return ("unreleased", null);
            return ("releasing", ReleaseSteps.FirstOrDefault(step => !Done(step)));
        }
        catch (JsonException) { return ("unreleased", null); }
    }

    /// <summary>The overview dot's diameter: 6 + 14·√(n / largest), 0 for no commits.</summary>
    public static int DotSize(int count, int largest) =>
        count <= 0 || largest <= 0 ? 0 : (int)Math.Round(6 + 14 * Math.Sqrt((double)count / largest), MidpointRounding.AwayFromZero);

    public static string Short(string sha) => sha.Length > 8 ? sha[..8] : sha;

    // ---- git ----

    private const string LogFormat = "--format=%x1e%H%x1f%aI%x1f%s";

    /// <summary>Reads the git history of <paramref name="root"/> into the model; the topic summaries and requests come from the caller.</summary>
    public static async Task<WorkMapModel> BuildAsync(string root, IReadOnlyList<WorkMapRequestInput> requests, IReadOnlyDictionary<string, WorkMapStoredSummary> stored,
        string? summarizing, IReadOnlyDictionary<string, string> errors)
    {
        var inside = await DelegationGit.Run(["rev-parse", "--is-inside-work-tree"], root, GitTimeoutSeconds);
        if (inside is null) return WorkMapModel.Unavailable("gitFailed");
        if (inside.ExitCode != 0) return WorkMapModel.Unavailable(inside.Stderr.Contains("not a git repository", StringComparison.OrdinalIgnoreCase) ? "notGit" : "gitFailed");
        var head = await DelegationGit.Run(["rev-parse", "HEAD"], root, GitTimeoutSeconds);
        if (head is null) return WorkMapModel.Unavailable("gitFailed");
        // A repository with no commit yet has nothing to show, and nothing has failed.
        if (head.ExitCode != 0) return new(true, null, null, Lanes, summarizing, []);
        var headSha = DelegationGit.Line(head.Stdout);

        var refs = await DelegationGit.Run(["for-each-ref", "refs/tags", "--format=%(refname:short)%1f%(creatordate:iso-strict)%1f%(*objectname)%1f%(objectname)"], root, GitTimeoutSeconds);
        if (refs is not { ExitCode: 0 }) return WorkMapModel.Unavailable("gitFailed");
        var tagInfo = new Dictionary<string, (string Date, string Sha)>(StringComparer.Ordinal);
        foreach (var line in refs.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.TrimEnd('\r').Split('\x1f');
            if (fields.Length < 4 || !TagPattern.IsMatch(fields[0])) continue;
            tagInfo[fields[0]] = (fields[1].Length >= 10 ? fields[1][..10] : fields[1], fields[2].Length > 0 ? fields[2] : fields[3]);
        }
        var tags = Tags(tagInfo.Keys);
        var visible = tags.Take(MaximumVersions).ToList();

        // Each version's range is read at once; a failing one fails the map.
        var reads = new List<Task<(IReadOnlyList<WorkMapCommit> Commits, int Total)?>>();
        for (var i = 0; i < visible.Count; i++)
            reads.Add(ReadCommitsAsync(root, i + 1 < tags.Count ? tags[i + 1] + ".." + visible[i] : visible[i]));
        var newest = tags.FirstOrDefault();
        reads.Add(ReadCommitsAsync(root, newest is null ? "HEAD" : newest + "..HEAD"));
        var results = await Task.WhenAll(reads);
        if (results.Any(r => r is null)) return WorkMapModel.Unavailable("gitFailed");

        var versions = new List<WorkMapVersion>();
        var next = results[^1]!.Value;
        if (next.Commits.Count > 0)
        {
            var name = VersionName(await ReadTextAsync(Path.Combine(root, "VERSION")), newest);
            var (status, step) = ("unreleased", (string?)null);
            if (name is not null) (status, step) = ReleaseStatus(await ReadTextAsync(Path.Combine(root, "release", name, "state.json")));
            versions.Add(Version("next", name, null, next.Commits[0].Date.Length >= 10 ? next.Commits[0].Date[..10] : null, status, step, headSha, next.Commits, next.Total));
        }
        for (var i = 0; i < visible.Count; i++)
        {
            var (date, sha) = tagInfo[visible[i]];
            versions.Add(Version(visible[i], visible[i].TrimStart('v'), visible[i], date, "released", null, sha, results[i]!.Value.Commits, results[i]!.Value.Total));
        }

        versions = Attach(versions, requests, newest is null ? null : tagInfo[newest].Date);
        versions = versions.Select(version =>
        {
            stored.TryGetValue(version.Key, out var saved);
            errors.TryGetValue(version.Key, out var error);
            return version with { Topics = saved?.Topics, TopicsStale = saved is not null && saved.RangeEnd != version.RangeEnd, SummaryError = error };
        }).ToList();
        return new(true, null, headSha[..Math.Min(8, headSha.Length)], Lanes, summarizing, versions);
    }

    private static WorkMapVersion Version(string key, string? name, string? tag, string? date, string status, string? step, string rangeEnd, IReadOnlyList<WorkMapCommit> commits, int total)
    {
        var counts = new Dictionary<string, int>();
        foreach (var lane in Lanes) if (commits.Count(c => c.Lanes.Contains(lane)) is > 0 and var n) counts[lane] = n;
        return new(key, name, tag, date, status, step, rangeEnd, total, total > commits.Count, counts, commits, null, false, null, []);
    }

    private static async Task<string?> ReadTextAsync(string path)
    {
        try { return File.Exists(path) ? await File.ReadAllTextAsync(path) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static async Task<(IReadOnlyList<WorkMapCommit>, int)?> ReadCommitsAsync(string root, string range)
    {
        var log = await DelegationGit.Run(["-c", "core.quotepath=false", "log", "--no-merges", LogFormat, "--name-only", "-n", MaximumCommitsPerVersion.ToString(CultureInfo.InvariantCulture), range, "--"], root, GitTimeoutSeconds);
        if (log is not { ExitCode: 0 }) return null;
        var commits = ParseLog(log.Stdout);
        var total = commits.Count;
        if (total >= MaximumCommitsPerVersion)
        {
            var count = await DelegationGit.Run(["rev-list", "--count", "--no-merges", range, "--"], root, GitTimeoutSeconds);
            if (count is not { ExitCode: 0 } || !int.TryParse(DelegationGit.Line(count.Stdout), NumberStyles.None, CultureInfo.InvariantCulture, out total)) return null;
        }
        return (commits, total);
    }

    /// <summary>The commits of <c>git log --format=%x1e%H%x1f%aI%x1f%s --name-only</c>, newest first as git lists them.</summary>
    public static IReadOnlyList<WorkMapCommit> ParseLog(string output)
    {
        var commits = new List<WorkMapCommit>();
        foreach (var chunk in output.Split('\x1e', StringSplitOptions.RemoveEmptyEntries))
        {
            var lines = chunk.Replace("\r", "").Split('\n');
            var fields = lines[0].Split('\x1f', 3);
            if (fields.Length < 3 || fields[0].Length < 8) continue;
            var files = lines.Skip(1).Where(l => l.Length > 0).ToList();
            var (lanes, scope, text) = ParseSubject(fields[2]);
            commits.Add(new(Short(fields[0]), fields[1], lanes, scope, text, files.Take(MaximumFilesPerCommit).ToList(), files.Count));
        }
        return commits;
    }

    // ---- requests ----

    /// <summary>Puts each request in the version of the oldest commit that followed it, as docs/work-map.md says.</summary>
    public static List<WorkMapVersion> Attach(List<WorkMapVersion> versions, IReadOnlyList<WorkMapRequestInput> inputs, string? newestTagDate)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var placed = new Dictionary<string, List<WorkMapRequest>>(StringComparer.Ordinal);
        var ordered = inputs.Select(i => (Input: i, Time: Time(i.Date))).OrderByDescending(p => p.Time ?? DateTimeOffset.MinValue).Take(MaximumRequests);
        // Every commit, oldest first, with the version it lies in.
        var commits = versions.SelectMany(v => v.Commits.Select(c => (Version: v, Commit: c, Time: Time(c.Date)))).OrderBy(p => p.Time ?? DateTimeOffset.MinValue).ToList();
        foreach (var (input, time) in ordered)
        {
            var files = input.Files.Select(f => f.Replace('\\', '/')).Distinct(comparer).ToList();
            var followed = files.Count == 0 ? [] : commits.Where(p => (time is null || p.Time > time) && p.Commit.Files.Any(f => files.Contains(f, comparer))).Take(MaximumCommitsPerRequest).ToList();
            string? key = null;
            if (followed.Count > 0) key = followed[0].Version.Key;
            else if (input.Date is { } when && time is not null && versions.Any(v => v.Key == "next") && (newestTagDate is null || string.CompareOrdinal(when[..Math.Min(10, when.Length)], newestTagDate) >= 0)) key = "next";
            if (key is null) continue;
            if (!placed.TryGetValue(key, out var list)) placed[key] = list = [];
            list.Add(new(input.PaneId + "/" + input.RunId, input.PaneId, input.Text, input.Date, files, followed.Select(p => p.Commit.Sha).ToList()));
        }
        return versions.Select(v => placed.TryGetValue(v.Key, out var list) ? v with { Requests = list } : v).ToList();
    }

    private static DateTimeOffset? Time(string? text) => DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value) ? value : null;

    /// <summary>
    /// The requests of the workspace's panes: every run of every pane with a graph that the user started (no notice run, no empty
    /// input), with the files it edited from <paramref name="store"/> under the workspace's folder.
    /// </summary>
    public static IReadOnlyList<WorkMapRequestInput> Requests(IEnumerable<RunSession> sessions, string workspaceId, string root, FileOriginalStore? store)
    {
        var inputs = new List<WorkMapRequestInput>();
        foreach (var session in sessions.Where(s => s.WorkspaceId == workspaceId && s.Kind == "claude"))
            foreach (var run in session.GraphRuns ?? [])
            {
                if (run.NoticeRun == true || string.IsNullOrWhiteSpace(run.Input) || !Wire.Identifier(run.Id)) continue;
                var text = run.Input.Trim();
                inputs.Add(new(session.Id, run.Id, text[..Math.Min(RequestTextLength, text.Length)], run.RootEntries.FirstOrDefault()?.Timestamp, store?.Paths(session.Id, run.SourceRunID ?? run.Id, root) ?? []));
            }
        return inputs;
    }
}
