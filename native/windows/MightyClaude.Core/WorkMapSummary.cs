using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MightyClaude.Core;

/// <summary>A summary that did not come out; <see cref="Key"/> is the locale key of the words the version's card shows.</summary>
public sealed class WorkMapSummaryException(string key) : Exception(key)
{
    public string Key { get; } = key;
    public static WorkMapSummaryException Cli => new("workMap.error.cli");
    public static WorkMapSummaryException Timeout => new("workMap.error.timeout");
    public static WorkMapSummaryException Output => new("workMap.error.output");
}

/// <summary>
/// The topic summary of docs/work-map.md: one headless Claude run per version (the model list's own launch, a temporary working
/// folder, 120 seconds), its answer read back by the rules of fixture <c>summaryCases</c>.
/// </summary>
public static class WorkMapSummary
{
    public const int MaximumTopics = 8;
    public const int TitleLength = 40;
    public const int SummaryLength = 160;
    public const int NotesLength = 8000;
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(120);

    /// <summary>The fixture's <c>prompt</c> (the checks compare them).</summary>
    public const string PromptTemplate = "You group one software version's commits into topics for a project overview.\nAnswer in {language}. Reply with JSON only, no prose: {\"topics\":[{\"title\":\"…\",\"summary\":\"…\",\"commits\":[\"sha8\"],\"requests\":[\"id\"]}]}\nRules: 3 to 6 topics; a title of at most 6 words names a user-visible feature or fix; a summary of one sentence says what changed for the user; every commit sha8 belongs to exactly one topic; put a request id under the topic it asked for.\nVersion: {version}\nCommits (sha8 [lanes] scope: subject):\n{commits}\nRequests the user sent (id: text):\n{requests}\nRelease notes:\n{notes}";

    public static string LanguageName(string language) => language switch { "ko" => "Korean", "ja" => "Japanese", "zh" => "Chinese", _ => "English" };

    public static string Prompt(string language, string version, IEnumerable<WorkMapCommit> commits, IEnumerable<WorkMapRequest> requests, string? notes)
    {
        var commitLines = string.Join("\n", commits.Select(c => $"{c.Sha} [{string.Join(",", c.Lanes)}] {(c.Scope is { Length: > 0 } scope ? scope + ": " : "")}{c.Subject}"));
        var requestLines = string.Join("\n", requests.Select(r => r.Id + ": " + r.Text.Replace('\n', ' ')));
        var values = new Dictionary<string, string>
        {
            ["language"] = language, ["version"] = version, ["commits"] = commitLines.Length == 0 ? "(none)" : commitLines,
            ["requests"] = requestLines.Length == 0 ? "(none)" : requestLines, ["notes"] = string.IsNullOrWhiteSpace(notes) ? "(none)" : Cut(notes, NotesLength),
        };
        // One pass, so nothing a commit title says is read as a placeholder.
        return Regex.Replace(PromptTemplate, @"\{(language|version|commits|requests|notes)\}", match => values[match.Groups[1].Value]);
    }

    /// <summary>The text cut to <paramref name="length"/> Unicode scalar values.</summary>
    public static string Cut(string text, int length)
    {
        var offset = 0; var count = 0;
        foreach (var rune in text.EnumerateRunes()) { if (count++ == length) return text[..offset]; offset += rune.Utf16SequenceLength; }
        return text;
    }

    /// <summary>The first <c>{</c> to its matching <c>}</c>, strings and escapes respected; null when it never closes.</summary>
    public static string? FirstObject(string answer)
    {
        var start = answer.IndexOf('{');
        if (start < 0) return null;
        var depth = 0; var inString = false; var escaped = false;
        for (var i = start; i < answer.Length; i++)
        {
            var c = answer[i];
            if (inString) { if (escaped) escaped = false; else if (c == '\\') escaped = true; else if (c == '"') inString = false; continue; }
            if (c == '"') inString = true;
            else if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return answer[start..(i + 1)];
        }
        return null;
    }

    /// <summary>The topics of an answer over <paramref name="commits"/> (sha, lanes) and <paramref name="requestIds"/>; throws <see cref="WorkMapSummaryException.Output"/> when none is readable.</summary>
    public static IReadOnlyList<WorkMapTopic> ParseAnswer(string answer, IReadOnlyList<(string Sha, IReadOnlyList<string> Lanes)> commits, IReadOnlySet<string> requestIds, string otherTitle)
    {
        if (FirstObject(answer) is not { } json) throw WorkMapSummaryException.Output;
        var known = commits.ToDictionary(c => c.Sha, c => c.Lanes, StringComparer.Ordinal);
        var taken = new HashSet<string>(StringComparer.Ordinal);
        var topics = new List<WorkMapTopic>();
        IReadOnlyList<string> LaneUnion(IEnumerable<string> shas) { var set = shas.SelectMany(s => known[s]).ToHashSet(); return WorkMap.Lanes.Where(set.Contains).ToList(); }
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("topics", out var rows) || rows.ValueKind != JsonValueKind.Array) throw WorkMapSummaryException.Output;
            foreach (var row in rows.EnumerateArray())
            {
                if (topics.Count == MaximumTopics) break;
                if (row.ValueKind != JsonValueKind.Object) continue;
                var title = Text(row, "title");
                if (title.Length == 0) continue;
                var shas = Strings(row, "commits").Where(s => known.ContainsKey(s) && taken.Add(s)).ToList();
                if (shas.Count == 0) continue;
                topics.Add(new(Cut(title, TitleLength), Cut(Text(row, "summary"), SummaryLength), LaneUnion(shas), shas, Strings(row, "requests").Where(requestIds.Contains).Distinct().ToList()));
            }
        }
        catch (JsonException) { throw WorkMapSummaryException.Output; }
        if (topics.Count == 0) throw WorkMapSummaryException.Output;
        var rest = commits.Select(c => c.Sha).Where(s => !taken.Contains(s)).ToList();
        if (rest.Count > 0) topics.Add(new(otherTitle, "", LaneUnion(rest), rest, []));
        return topics;
    }

    private static string Text(JsonElement row, string name) => row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()!.Trim() : "";
    private static IEnumerable<string> Strings(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!) : [];

    /// <summary>The text of the <c>result</c> event in Claude's stream-json output; null while there is none.</summary>
    public static string? ResultText(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.Text("type") != "result") return null;
            if (root.TryGetProperty("is_error", out var error) && error.ValueKind == JsonValueKind.True) throw WorkMapSummaryException.Output;
            return root.Text("result") ?? throw WorkMapSummaryException.Output;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Asks Claude once and returns what it answered. The folder it runs in is a temporary one, not the workspace.</summary>
    public static async Task<string> RunAsync(CliCommand command, string prompt, TimeSpan timeout, CancellationToken cancellation = default)
    {
        var folder = Path.Combine(Path.GetTempPath(), "mighty-work-map-" + Wire.Id());
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        limit.CancelAfter(timeout);
        try
        {
            Directory.CreateDirectory(folder);
            await using var child = ChildProcess.Start(ChildProcess.StartInfo(command.Binary, command.Prefix.Concat(ProviderCatalog.ClaudeHeadlessArguments), folder, ProviderCatalog.ClaudeHeadlessEnvironment()));
            var drain = ProviderCatalog.DrainAsync(child.Error, limit.Token); _ = drain.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            await child.Input.WriteLineAsync(JsonSerializer.Serialize(new { type = "user", message = new { role = "user", content = prompt } }, Wire.Json));
            await child.Input.FlushAsync(limit.Token);
            await foreach (var line in OutputParser.LinesAsync(child.Output, limit.Token))
                if (ResultText(line) is { } text) return text;
            throw WorkMapSummaryException.Output;
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { throw WorkMapSummaryException.Timeout; }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException) { throw WorkMapSummaryException.Cli; }
        finally { try { if (Directory.Exists(folder)) Directory.Delete(folder, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
    }
}

/// <summary>The saved summaries of one workspace, <c>&lt;data&gt;/work-map/&lt;sha256 of its real path&gt;.json</c>, written through a temporary name as the file originals are.</summary>
public sealed class WorkMapSummaryStore(string directory)
{
    private sealed record Saved(Dictionary<string, WorkMapStoredSummary>? Versions);
    private readonly object sync = new();

    public string PathFor(string root)
    {
        var real = WorkspaceFiles.RealPath(root) ?? root;
        return Path.Combine(directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(real))).ToLowerInvariant() + ".json");
    }

    public IReadOnlyDictionary<string, WorkMapStoredSummary> Read(string root)
    {
        lock (sync)
        {
            try
            {
                var saved = JsonSerializer.Deserialize<Saved>(File.ReadAllBytes(PathFor(root)), Wire.Json);
                return saved?.Versions?.Where(p => p.Value is { Topics: not null, RangeEnd: not null }).ToDictionary(p => p.Key, p => p.Value) ?? [];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { return new Dictionary<string, WorkMapStoredSummary>(); }
        }
    }

    public void Write(string root, string key, string rangeEnd, IReadOnlyList<WorkMapTopic> topics)
    {
        lock (sync)
        {
            var versions = Read(root).ToDictionary(p => p.Key, p => p.Value);
            versions[key] = new(rangeEnd, topics, Wire.Now());
            var path = PathFor(root); var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(directory);
                File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(new Saved(versions), Wire.Json));
                File.Move(temporary, path, true);
            }
            finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }
    }
}

public enum WorkMapStart { Started, Busy, UnknownKey, Unavailable }

/// <summary>
/// What the work map needs from a running app: the model of a workspace, and the one summary a workspace may be making at a time.
/// The phone and the web page both come here, so they see the same <see cref="WorkMapModel.Summarizing"/> and the same errors.
/// </summary>
public sealed class WorkMapService(string dataDirectory, Func<AppSnapshot> snapshot, FileOriginalStore originals, Func<CancellationToken, Task<CliCommand?>> findClaude,
    Func<CliCommand, string, TimeSpan, CancellationToken, Task<string>>? run = null)
{
    private readonly WorkMapSummaryStore store = new(Path.Combine(dataDirectory, "work-map"));
    private readonly Func<CliCommand, string, TimeSpan, CancellationToken, Task<string>> runner = run ?? WorkMapSummary.RunAsync;
    private readonly object sync = new();
    private readonly Dictionary<string, string> summarizing = [];
    private readonly Dictionary<string, Dictionary<string, string>> errors = [];

    /// <summary>A workspace's summary ended (done or failed); the host pushes the model again.</summary>
    public event Action<string>? Changed;

    public static bool IsLocal(Workspace workspace) => workspace.Remote is null;

    public async Task<WorkMapModel> ModelAsync(Workspace workspace)
    {
        if (!IsLocal(workspace)) return WorkMapModel.Unavailable("remote");
        string? busy; Dictionary<string, string> failed;
        lock (sync) { summarizing.TryGetValue(workspace.Id, out busy); failed = errors.TryGetValue(workspace.Id, out var e) ? new(e) : []; }
        var state = snapshot();
        return await WorkMap.BuildAsync(workspace.Path, await Task.Run(() => WorkMap.Requests(state.Sessions, workspace.Id, workspace.Path, originals)), store.Read(workspace.Path), busy, failed);
    }

    public async Task<WorkMapStart> StartAsync(Workspace workspace, string key)
    {
        if (!IsLocal(workspace)) return WorkMapStart.Unavailable;
        lock (sync) { if (summarizing.ContainsKey(workspace.Id)) return WorkMapStart.Busy; summarizing[workspace.Id] = key; }
        WorkMapVersion? version = null;
        try
        {
            var model = await ModelAsync(workspace);
            if (!model.Available) { Release(workspace.Id); return WorkMapStart.Unavailable; }
            version = model.Versions.FirstOrDefault(v => v.Key == key);
        }
        catch { Release(workspace.Id); throw; }
        if (version is null) { Release(workspace.Id); return WorkMapStart.UnknownKey; }
        _ = Task.Run(() => SummarizeAsync(workspace, version));
        return WorkMapStart.Started;
    }

    private void Release(string workspaceId) { lock (sync) summarizing.Remove(workspaceId); }

    private async Task SummarizeAsync(Workspace workspace, WorkMapVersion version)
    {
        string? failure = null;
        try
        {
            var command = await findClaude(CancellationToken.None) ?? throw WorkMapSummaryException.Cli;
            string? notes = null;
            if (version.Version is { } name)
                try { var path = Path.Combine(workspace.Path, "release", name + "-notes.md"); if (File.Exists(path)) notes = File.ReadAllText(path); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            var prompt = WorkMapSummary.Prompt(WorkMapSummary.LanguageName(Locale.ChosenLanguage()), version.Version ?? version.Key, version.Commits, version.Requests, notes);
            var answer = await runner(command, prompt, WorkMapSummary.Timeout, CancellationToken.None);
            var topics = WorkMapSummary.ParseAnswer(answer, version.Commits.Select(c => (c.Sha, c.Lanes)).ToList(), version.Requests.Select(r => r.Id).ToHashSet(StringComparer.Ordinal), Locale.Get("workMap.topic.other"));
            store.Write(workspace.Path, version.Key, version.RangeEnd, topics);
        }
        catch (WorkMapSummaryException ex) { failure = Locale.Get(ex.Key); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { failure = Locale.Get("workMap.error.output"); }
        lock (sync)
        {
            if (!errors.TryGetValue(workspace.Id, out var failed)) errors[workspace.Id] = failed = [];
            if (failure is null) failed.Remove(version.Key); else failed[version.Key] = failure;
            summarizing.Remove(workspace.Id);
        }
        Changed?.Invoke(workspace.Id);
    }
}
