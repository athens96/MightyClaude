using System.Text;

namespace MightyClaude.Core;

/// <summary>
/// One picture a graph step shows in its preview block (macOS AgentImageItem): a picture the agent returned, kept by
/// reference (<paramref name="Stored"/>), or a Markdown picture that can be read (<paramref name="Located"/>, with
/// its alt text and the source as written).
/// </summary>
public sealed record AgentImageItem(string Id, AgentImageRef? Stored, AgentImageLocation? Located = null, string Alt = "", string Source = "");

/// Port of macOS MightyGraphImages: which steps of the Mighty diagram get a picture preview block, and what it shows.
public static class MightyGraphImages
{
    /// The step names a preview block attaches to.
    public const string RequestStep = "request";
    public static string AgentStep(string agentID) => "agent:" + agentID;

    /// <summary>
    /// Pictures a step produced, in order, once each: stored tool pictures, then readable Markdown pictures (local
    /// files under the path rule and data URIs). Remote and refused Markdown targets are not previewed.
    /// </summary>
    public static List<AgentImageItem> Items(IReadOnlyList<LogEntry> entries, string? root) => Items(entries, source => AgentImagePaths.Locate(source, root));

    private static List<AgentImageItem> Items(IReadOnlyList<LogEntry> entries, Func<string, AgentImageLocation> locate)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<AgentImageItem>();
        foreach (var entry in entries)
        {
            foreach (var image in entry.Images ?? [])
                if (seen.Add("hash:" + image.Hash)) result.Add(new("hash:" + image.Hash, image));
            if (entry.Kind != "assistant") continue;
            foreach (var image in AgentMarkdownImages.Extract(entry.Text))
            {
                var location = locate(image.Source);
                var id = location switch
                {
                    AgentImageLocation.File file => "file:" + file.Path,
                    AgentImageLocation.Inline inline => "data:" + AgentImageSupport.Sha256(Encoding.UTF8.GetBytes(inline.Base64)),
                    _ => null,
                };
                if (id is not null && seen.Add(id)) result.Add(new(id, null, location, image.Alt, image.Source));
            }
        }
        return result;
    }

    /// <summary>
    /// A step's pictures, worked out once per step and remembered until its entries or status change: the graph
    /// asks on every redraw, and finding Markdown pictures touches the file system. A file that appears later is
    /// picked up when the step settles.
    /// </summary>
    public static List<AgentImageItem> Items(MightyGraphRun run, string step, string? root)
    {
        if (step == RequestStep) return Remembered(run.Id, step, run.RootEntries, run.Status, root);
        return step.StartsWith("agent:", StringComparison.Ordinal) && run.Agents.FirstOrDefault(a => AgentStep(a.Id) == step) is { } agent
            ? Remembered(run.Id, step, agent.Entries, agent.Status, root) : [];
    }

    /// <summary>
    /// Every step with at least one picture, for the layout. The first <paramref name="settled"/> runs never change
    /// (requests read back from the session record), so their memo is keyed without hashing every entry.
    /// </summary>
    public static List<(string RunID, string Step, int Count)> Galleries(IReadOnlyList<MightyGraphRun> runs, string? root, int settled = 0)
    {
        var result = new List<(string RunID, string Step, int Count)>();
        for (var index = 0; index < runs.Count; index++)
        {
            var run = runs[index];
            var main = Remembered(run.Id, RequestStep, run.RootEntries, run.Status, root, index < settled).Count;
            if (main > 0) result.Add((run.Id, RequestStep, main));
            foreach (var agent in run.Agents)
            {
                var count = Remembered(run.Id, AgentStep(agent.Id), agent.Entries, agent.Status, root, index < settled).Count;
                if (count > 0) result.Add((run.Id, AgentStep(agent.Id), count));
            }
        }
        return result;
    }

    private const int MaximumRemembered = 1_024;
    /// <summary>
    /// Each step's answer, and where each Markdown source it read was found while the step kept its status: a streaming
    /// step changes its text on every redraw, and only a source it had not named before is looked up then.
    /// </summary>
    private static readonly Dictionary<string, (int Signature, string Status, List<AgentImageItem> Items, Dictionary<string, AgentImageLocation> Located)> Memo = [];

    private static List<AgentImageItem> Remembered(string runID, string step, IReadOnlyList<LogEntry> entries, string status, string? root, bool settled = false)
    {
        var signature = new HashCode();
        signature.Add(status); signature.Add(entries.Count);
        // Only entries that can carry pictures; their text by length, not content.
        if (!settled)
            foreach (var entry in entries.Where(e => e.Kind == "assistant" || e.Images is not null))
            {
                signature.Add(entry.Id); signature.Add(entry.Kind); signature.Add(entry.Text.Length);
                foreach (var image in entry.Images ?? []) signature.Add(image.Hash);
            }
        var (key, value) = (runID + "\u001F" + step + "\u001F" + (root ?? ""), signature.ToHashCode());
        Dictionary<string, AgentImageLocation> located;
        lock (Memo)
        {
            if (Memo.TryGetValue(key, out var hit) && hit.Signature == value) return hit.Items;
            // A step that settled looks every file up again, so one that appeared meanwhile is picked up.
            located = hit.Located is { } known && hit.Status == status ? new(known, StringComparer.Ordinal) : new(StringComparer.Ordinal);
        }
        var items = Items(entries, source => located.TryGetValue(source, out var place) ? place : located[source] = AgentImagePaths.Locate(source, root));
        lock (Memo)
        {
            if (Memo.Count >= MaximumRemembered) Memo.Clear();
            Memo[key] = (value, status, items, located);
        }
        return items;
    }
}
