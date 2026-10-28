using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MightyClaude.Core;

/// <summary>
/// <c>mighty.structure</c> (M/MightyCore/Remote/MobileStructure.swift): the host's own structure view
/// (<see cref="MightyStructure.Make"/>) of a pane, cut down for the phone. The phone draws it and never works a phase or a
/// step out for itself, so the two screens cannot disagree about where a request belongs. Versioned (<see cref="V"/>) and
/// bounded hard: every text has a cap well under the desktop card's, only the newest <see cref="MaximumItems"/> requests
/// travel, a request carries at most <see cref="MaximumNodes"/> sub-agent rows, and the whole value stays under
/// <see cref="BudgetBytes"/> of JSON, older requests dropped first.
/// </summary>
/// <param name="Shape">"phases" (a style with phases: <paramref name="Columns"/>) or "free" (no style: <paramref name="Rows"/>).</param>
/// <param name="Omitted">Older requests left out of the payload; absent when none were.</param>
public sealed record MobileStructure(int V, string Shape, IReadOnlyList<MobileStructure.Column>? Columns = null, IReadOnlyList<MobileStructure.Row>? Rows = null, int? Omitted = null)
{
    /// <summary>The shape this build writes. A phone that reads another <c>v</c> shows nothing of it.</summary>
    public const int Version = 1;

    /// <summary>One phase of a styled pane and its requests, oldest first; <paramref name="Step"/> is done, current or pending.</summary>
    public sealed record Column(string Id, string Title, string Step, IReadOnlyList<Item> Items);
    /// <summary>One request of a free pane as request → run → result, each a <see cref="MightyStructure.Step"/>.</summary>
    public sealed record Row(Item Item, string Request, string Execution, string Result);
    /// <param name="Title">The style's own name for the request.</param>
    /// <param name="Message">The main agent's latest reply, left out once that reply is the answer.</param>
    /// <param name="Answer">The final answer as Markdown, once the request finished with one.</param>
    /// <param name="OmittedNodes">Sub-agent rows left out past <see cref="MaximumNodes"/>; absent when none were.</param>
    /// <param name="NoticeRun"><c>true</c> for Claude's reply to finished background work (<see cref="MightyGraphRun.NoticeRun"/>); absent otherwise.</param>
    public sealed record Item(string RunId, int Ordinal, string? Title, string Status, bool Finished, string? Request, string? Message, IReadOnlyList<Activity> Activity,
        string? Answer, Tally Tally, IReadOnlyList<Cluster> Clusters, int? OmittedNodes, bool? NoticeRun = null);
    public sealed record Activity(string Kind, string State, string Text);
    /// <summary>What the request holds now, counted over all of it even when nodes were left out.</summary>
    public sealed record Tally(int Agents, int Tools);
    /// <summary>One root sub-agent's tree, the root first and its descendants depth-first.</summary>
    public sealed record Cluster(IReadOnlyList<Node> Nodes);
    /// <param name="Depth">0 for the cluster's root, never past <see cref="MaximumDepth"/>.</param>
    /// <param name="Activity">Its last tool call.</param>
    /// <param name="Background"><c>true</c> for a background sub-agent and every task; absent otherwise.</param>
    /// <param name="OutputTail">A task's last output lines (at most 6), read on the host; never its output file.</param>
    /// <param name="NoticeReply">Claude's reply to the task's notice, bounded like <paramref name="Output"/>.</param>
    public sealed record Node(string Id, int Depth, string Title, string Status, string Kind, string? Model, string? Prompt, string? Output, Activity? Activity,
        bool? Background = null, IReadOnlyList<string>? OutputTail = null, string? NoticeReply = null);

    // ── bounds ────────────────────────────────────────────────────────────────

    public const int RequestBytes = 1_024, MessageBytes = 1_024, AnswerBytes = 4_096, PromptBytes = 300, OutputBytes = 1_024, ActivityBytes = 200, TitleBytes = 160, ModelBytes = 80;
    public const int MaximumItems = 20, MaximumNodes = 60;
    /// <summary>Deeper nodes are drawn at this depth, as the desktop stops indenting there.</summary>
    public const int MaximumDepth = MightyStructure.MaximumIndent;
    public const int MaximumActivity = MightyStructure.RequestActivity;
    /// <summary>The JSON the whole value may take, as the host sends it. Older requests go first; a newest request over it alone sheds its last nodes.</summary>
    public const int BudgetBytes = 192 * 1_024;
    /// <summary>What a request adds to the JSON beside its own object: the comma before it, and a free row's wrapper with its three steps. Only an estimate; <see cref="Make"/> checks the real size.</summary>
    private const int ItemOverhead = 96;

    // ── projection ────────────────────────────────────────────────────────────

    /// <summary>One request in its wire form with its JSON size, kept between builds so a rebuild encodes only the requests that moved.
    /// <c>Tails</c> is the output lines of the request's tasks it was built with, as one key (<see cref="TailsKey"/>).</summary>
    public sealed record Wired(MightyStructure.Item Source, string? Title, Item Item, int Size, string Tails = "");

    /// <summary>
    /// The pane's structure for the phone (M/MightyCore/Remote/MobileStructure.swift:148-212). Null when there is nothing to
    /// draw: no request yet and no style phase to show. <paramref name="title"/> is the style's name for the request with this
    /// run id, <paramref name="model"/> a block's model id as the phone shows it, <paramref name="tails"/> a background run's last
    /// output lines by block id (host memory only), and <paramref name="wired"/> the requests of the last build, by run id,
    /// replaced by this build's.
    /// </summary>
    public static MobileStructure? Make(MightyStructure.Shape shape, int runCount, Func<string, string?>? title = null, Func<string, string>? model = null, Dictionary<string, Wired>? wired = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? tails = null)
    {
        if (shape.Columns is { } columns ? runCount == 0 && columns.Count == 0 : shape.Rows is not { Count: > 0 }) return null;
        var all = shape.Items.ToList();
        // A run id the pane repeats (a damaged save) travels once, as its newest request.
        var newest = new Dictionary<string, int>();
        foreach (var item in all) newest[item.RunId] = Math.Max(newest.GetValueOrDefault(item.RunId, item.RunIndex), item.RunIndex);
        var next = new Dictionary<string, Wired>();
        var requests = all.Where(item => newest[item.RunId] == item.RunIndex).OrderByDescending(item => item.RunIndex).Take(MaximumItems).Select(source =>
        {
            // A notice run is no request: it takes no style title.
            var name = source.NoticeRun ? null : title?.Invoke(source.RunId);
            var held = tails is { Count: > 0 } ? TailsKey(source, tails) : "";
            if (wired is null || !wired.TryGetValue(source.RunId, out var made) || made.Source != source || made.Title != name || made.Tails != held)
            {
                var item = ItemFor(source, name, model, tails);
                made = new(source, name, item, EncodedSize(item), held);
            }
            return next[source.RunId] = made;
        }).ToList();
        if (wired is not null) { wired.Clear(); foreach (var (id, made) in next) wired[id] = made; }

        // Newest first by the estimate, then the real size decides.
        var used = EncodedSize(Build(shape, [], newest, newest.Count)); var count = 0;
        foreach (var request in requests)
        {
            var cost = request.Size + ItemOverhead;
            if (count > 0 && used + cost > BudgetBytes) break;
            used += cost; count++;
        }
        var kept = requests.Take(count).ToDictionary(request => request.Source.RunId, request => request.Item);
        var order = requests.Take(count).Select(request => request.Source.RunId).ToList();
        var result = Build(shape, kept, newest, newest.Count - kept.Count);
        while (EncodedSize(result) > BudgetBytes)
        {
            if (order.Count > 1)
            {
                kept.Remove(order[^1]); order.RemoveAt(order.Count - 1);
                result = Build(shape, kept, newest, newest.Count - kept.Count);
                continue;
            }
            // The newest request alone is over: keep as many of its first nodes as fit.
            if (order.Count == 0 || !kept.TryGetValue(order[0], out var heavy)) break;
            MobileStructure Fitted(int nodes) => Build(shape, new Dictionary<string, Item>(kept) { [order[0]] = Keeping(heavy, nodes) }, newest, newest.Count - kept.Count);
            int low = 0, high = heavy.Clusters.Sum(cluster => cluster.Nodes.Count);
            while (low < high)
            {
                var middle = (low + high + 1) / 2;
                if (EncodedSize(Fitted(middle)) <= BudgetBytes) low = middle; else high = middle - 1;
            }
            result = Fitted(low);
            break;
        }
        return result;
    }

    /// <summary>The wire value holding <paramref name="kept"/> (by run id), each request once, where the desktop drew it.</summary>
    private static MobileStructure Build(MightyStructure.Shape shape, Dictionary<string, Item> kept, Dictionary<string, int> newest, int omitted)
    {
        Item? Carried(MightyStructure.Item item) => newest[item.RunId] == item.RunIndex ? kept.GetValueOrDefault(item.RunId) : null;
        int? left = omitted > 0 ? omitted : null;
        return shape.Columns is { } columns
            ? new(Version, "phases", Columns: columns.Select(column => new Column(column.Phase.Id, Capped(column.Phase.Title, TitleBytes, singleLine: true), MightyStructure.Word(column.Step),
                column.Items.Select(Carried).OfType<Item>().ToList())).ToList(), Omitted: left)
            : new(Version, "free", Rows: (shape.Rows ?? []).Select(row => Carried(row.Item) is { } item
                ? new Row(item, MightyStructure.Word(row.Request), MightyStructure.Word(row.Execution), MightyStructure.Word(row.Result)) : null).OfType<Row>().ToList(), Omitted: left);
    }

    /// <summary>The lines of <paramref name="tails"/> that belong to the request's tasks, as one comparable key.</summary>
    private static string TailsKey(MightyStructure.Item item, IReadOnlyDictionary<string, IReadOnlyList<string>> tails)
    {
        var key = new StringBuilder();
        foreach (var cluster in item.Clusters)
            foreach (var node in cluster.Nodes)
                if (node.Kind == "task" && tails.TryGetValue(node.AgentId, out var lines)) key.Append(node.AgentId).Append('\u001E').AppendJoin('\u001F', lines).Append('\u001D');
        return key.ToString();
    }

    /// <summary>One request in its wire form: its texts under the phone's caps and its first <see cref="MaximumNodes"/> nodes, the rest counted as left out.</summary>
    public static Item ItemFor(MightyStructure.Item item, string? title, Func<string, string>? model = null, IReadOnlyDictionary<string, IReadOnlyList<string>>? tails = null)
    {
        var nodes = 0; var clusters = new List<Cluster>();
        foreach (var cluster in item.Clusters)
        {
            if (nodes >= MaximumNodes) break;
            var taken = cluster.Nodes.Take(MaximumNodes - nodes).Select(node => new Node(node.AgentId, Math.Clamp(node.Depth, 0, MaximumDepth), Capped(node.Title, TitleBytes, singleLine: true),
                node.Status, node.Kind, node.Model is { } id ? Capped(model?.Invoke(id) ?? id, ModelBytes, singleLine: true) : null, node.Prompt is { } prompt ? Capped(prompt.Text, PromptBytes) : null,
                node.Output is { } output ? Capped(output.Text, OutputBytes) : null, node.Activity.Count > 0 ? ActivityFor(node.Activity[^1]) : null,
                node.Background ? true : (bool?)null, node.Kind == "task" ? MobileMightySupport.OutputTail(tails?.GetValueOrDefault(node.AgentId)) : null,
                node.NoticeReply is { } reply ? Capped(reply.Text, OutputBytes) : null)).ToList();
            nodes += taken.Count; clusters.Add(new(taken));
        }
        var total = item.Clusters.Sum(cluster => cluster.Nodes.Count);
        var named = title is null ? null : Capped(title, TitleBytes, singleLine: true);
        return new(item.RunId, item.Ordinal, named is { Length: > 0 } ? named : null, item.Status, item.Finished, item.Request is { } request ? Capped(request.Text, RequestBytes) : null,
            item.Message is { } message ? Capped(message.Text, MessageBytes) : null, item.Activity.TakeLast(MaximumActivity).Select(ActivityFor).ToList(),
            item.Answer is { } answer ? Capped(answer.Text, AnswerBytes, markdown: true) : null, new(item.Tally.Agents, item.Tally.Tools), clusters, total > nodes ? total - nodes : null,
            item.NoticeRun ? true : null);
    }

    private static Activity ActivityFor(MightyStructure.ActivityLine line) =>
        new(Capped(line.Kind, 40, singleLine: true), Capped(line.State, 40, singleLine: true), Capped(line.Text, ActivityBytes, singleLine: true));

    /// <summary>The request with only its first <paramref name="nodes"/> nodes, depth-first, the rest counted as left out.</summary>
    public static Item Keeping(Item item, int nodes)
    {
        var left = Math.Max(0, nodes); var dropped = 0; var clusters = new List<Cluster>();
        foreach (var cluster in item.Clusters)
        {
            var taken = cluster.Nodes.Take(left).ToList();
            dropped += cluster.Nodes.Count - taken.Count; left -= taken.Count;
            if (taken.Count > 0) clusters.Add(new(taken));
        }
        var omitted = (item.OmittedNodes ?? 0) + dropped;
        return item with { Clusters = clusters, OmittedNodes = omitted > 0 ? omitted : null };
    }

    /// <summary>
    /// <paramref name="text"/> within <paramref name="maximumBytes"/> of UTF-8, cut between characters (a combined emoji is
    /// never split) and marked with "…"; a cut Markdown text has a code fence it leaves open closed again, the closing line
    /// counted inside the cap.
    /// </summary>
    public static string Capped(string text, int maximumBytes, bool singleLine = false, bool markdown = false)
    {
        var flat = singleLine ? string.Join(' ', text.Split(MightyStructure.Newlines, StringSplitOptions.RemoveEmptyEntries)) : text;
        if (Encoding.UTF8.GetByteCount(flat) <= maximumBytes) return flat;
        for (var reserved = 0; ;)
        {
            var result = Prefix(flat, Math.Max(0, maximumBytes - 3 - reserved)).Trim() + "…";
            if (!markdown || MightyStructure.OpenFence(result) is not { } fence) return result;
            var closing = 1 + Encoding.UTF8.GetByteCount(fence);
            // A shorter cut can land inside an earlier, longer fence: reserve for that one and cut again.
            if (closing <= reserved) return result + "\n" + fence;
            reserved = closing;
        }
    }

    /// <summary>The longest run of whole characters of <paramref name="text"/> within <paramref name="bytes"/> of UTF-8.</summary>
    private static string Prefix(string text, int bytes)
    {
        var used = 0; var end = 0;
        while (end < text.Length)
        {
            var length = StringInfo.GetNextTextElementLength(text.AsSpan(end));
            var size = Encoding.UTF8.GetByteCount(text.AsSpan(end, length));
            if (used + size > bytes) break;
            used += size; end += length;
        }
        return text[..end];
    }

    private static int EncodedSize(object value) => JsonSerializer.SerializeToUtf8Bytes(value, Wire.Json).Length;

    /// <summary>
    /// The phone echoes this back (<c>?structure=&lt;tag&gt;</c>), so an unchanged value is not sent again: 1…999 999 999 999
    /// from the value's bytes, 0 for no value at all.
    /// </summary>
    public static long Tag(MobileStructure? value) => value is null ? 0
        : (long)(BinaryPrimitives.ReadUInt64BigEndian(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, Wire.Json))) % 999_999_999_999UL) + 1;
}

/// <summary>
/// The phone's structure per pane (M/MightyCore/Remote/MobileStructure.swift:327-380), built again only when what the
/// structure view reads moved (<see cref="MightyStructure.Signature"/>), and then only the cards of the requests that moved
/// (<see cref="MightyStructure.ItemCache"/>), as the desktop view keeps its own. A streamed token that changes nothing drawn
/// reuses the last value.
/// </summary>
public sealed class MobileStructureCache
{
    /// <summary>A pane's value and the tag the phone echoes for it.</summary>
    public sealed record Built(MobileStructure? Value, long Tag);
    private sealed class Entry
    {
        internal MightyStructure.Signature? Signature;
        internal string Key = "";
        internal IReadOnlyDictionary<string, IReadOnlyList<string>>? Tails;
        internal readonly MightyStructure.ItemCache Items = new();
        internal readonly Dictionary<string, MobileStructure.Wired> Wired = [];
        internal Built Built = new(null, 0);
    }
    private readonly Dictionary<string, Entry> entries = [];
    /// <summary>How many times a value was built rather than reused; read by the checks.</summary>
    public int Builds { get; private set; }

    /// <summary>
    /// <see cref="MightyStructure.Make"/> with the inputs the desktop view passes, turned into the wire shape.
    /// <paramref name="key"/> names whatever else the value reads: the styles whose request titles it carries and the model
    /// names it shows. <paramref name="title"/> reads a request's input. <paramref name="tails"/>: the pane's background runs' last
    /// output lines by block id, which only the host holds; a new map (the host replaces it on every change) builds again.
    /// </summary>
    public Built Structure(string pane, IReadOnlyList<MightyGraphRun> runs, StyleEvaluator? style, StylePhase? currentPhase, IReadOnlyList<PlanRecord>? planHistory = null,
        string? pendingPlanRunID = null, string key = "", Func<string, string?>? title = null, Func<string, string>? model = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? tails = null)
    {
        var signature = new MightyStructure.Signature(runs, style, currentPhase, planHistory: planHistory, pendingPlanRunID: pendingPlanRunID);
        if (!entries.TryGetValue(pane, out var entry)) entries[pane] = entry = new();
        if (entry.Signature == signature && entry.Key == key && ReferenceEquals(entry.Tails, tails)) return entry.Built;
        // Another key can name each request differently: nothing kept is reused.
        if (entry.Key != key) entry.Wired.Clear();
        var shape = MightyStructure.Make(runs, style, currentPhase, planHistory: planHistory, pendingPlanRunID: pendingPlanRunID, items: entry.Items.Items(runs, signature));
        var inputs = new Dictionary<string, string>();
        foreach (var run in runs) inputs[run.Id] = run.Input;
        var value = MobileStructure.Make(shape, runs.Count, title is null ? null : id => inputs.TryGetValue(id, out var input) ? title(input) : null, model, entry.Wired, tails);
        entry.Built = new(value, MobileStructure.Tag(value)); entry.Signature = signature; entry.Key = key; entry.Tails = tails;
        Builds++;
        return entry.Built;
    }

    /// <summary>Forgets the panes not in <paramref name="panes"/>.</summary>
    public void Retain(IReadOnlySet<string> panes)
    {
        foreach (var pane in entries.Keys.Where(pane => !panes.Contains(pane)).ToArray()) entries.Remove(pane);
    }
}
