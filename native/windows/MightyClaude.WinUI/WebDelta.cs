using System.Text.Json;
using System.Text.Json.Nodes;
using MightyClaude.Core;

namespace MightyClaude.WinUI;

/// <summary>
/// One pane's change as the web shell's delta carries it (native/windows/web/src/store/delta.ts, whose <c>diffList</c> and
/// <c>diffSession</c> are these rules and are tested there): the fields whose JSON changed (<c>set</c>), the ones now absent
/// (<c>unset</c>), and the two long lists, <c>logs</c> and <c>graphRuns</c>, as what happened to them by id. A streamed answer
/// is its one entry in the message, however long the conversation.
/// </summary>
internal static class WebDelta
{
    /// <summary>
    /// What happened to a list of records with ids: the first <see cref="Drop"/> went, <see cref="Replaced"/> took the places
    /// of their ids, <see cref="Added"/> came after; or, for any other change, the whole id <see cref="Order"/>.
    /// </summary>
    internal sealed class ListChange<T>
    {
        internal int Drop;
        internal readonly List<T> Replaced = [], Added = [];
        internal List<string>? Order;
        internal bool Empty => Drop == 0 && Replaced.Count == 0 && Added.Count == 0 && Order is null;
    }

    /// <summary>delta.ts diffList: the usual changes (the last entry streamed, entries appended, the oldest trimmed) need no order.</summary>
    internal static ListChange<T> Diff<T>(IReadOnlyList<T> previous, IReadOnlyList<T> next, Func<T, string> id, Func<T, T, bool> same)
    {
        var change = new ListChange<T>();
        var drop = previous.Count;
        if (next.Count > 0) { var first = id(next[0]); for (var index = 0; index < previous.Count; index++) if (id(previous[index]) == first) { drop = index; break; } }
        var kept = previous.Count - drop;
        var simple = next.Count >= kept;
        for (var index = 0; simple && index < kept; index++) simple = id(next[index]) == id(previous[drop + index]);
        if (simple && drop > 0 && next.Count > kept)
        {
            // An added record must be new: one of the dropped ones coming back is a reordering.
            var dropped = new HashSet<string>(StringComparer.Ordinal); for (var index = 0; index < drop; index++) dropped.Add(id(previous[index]));
            for (var index = kept; simple && index < next.Count; index++) simple = !dropped.Contains(id(next[index]));
        }
        if (simple)
        {
            change.Drop = drop;
            for (var index = 0; index < kept; index++) if (!same(previous[drop + index], next[index])) change.Replaced.Add(next[index]);
            for (var index = kept; index < next.Count; index++) change.Added.Add(next[index]);
            return change;
        }
        var old = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var record in previous) old[id(record)] = record;
        change.Order = new List<string>(next.Count);
        foreach (var record in next)
        {
            var key = id(record); change.Order.Add(key);
            if (!old.TryGetValue(key, out var before)) change.Added.Add(record);
            else if (!same(before, record)) change.Replaced.Add(record);
        }
        return change;
    }

    private static bool SameList<T>(List<T>? a, List<T>? b, Func<T, T, bool> same)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null || a.Count != b.Count) return false;
        for (var index = 0; index < a.Count; index++) if (!same(a[index], b[index])) return false;
        return true;
    }

    /// <summary>
    /// Whether two entries write the same JSON. A streamed event replaces only its entry, but a normalize of a running pane
    /// (StateStore.Normalize) makes every entry anew with the same values: by reference alone, each would be sent again.
    /// </summary>
    internal static bool SameEntry(LogEntry a, LogEntry b) => ReferenceEquals(a, b)
        || a.Id == b.Id && a.Kind == b.Kind && a.Text == b.Text && a.Timestamp == b.Timestamp && a.Provider == b.Provider
            && Equals(a.Activity, b.Activity) && SameList(a.Images, b.Images, (x, y) => Equals(x, y));

    /// <summary>The same for a graph run (a class, made anew by every normalize): every field <see cref="MightyGraphRun"/> writes.</summary>
    internal static bool SameRun(MightyGraphRun a, MightyGraphRun b) => ReferenceEquals(a, b)
        || a.Id == b.Id && a.Input == b.Input && a.Status == b.Status && a.SourceRunID == b.SourceRunID && a.FinalOutput == b.FinalOutput
            && a.Provider == b.Provider && a.NodeModelLabel == b.NodeModelLabel && a.ConfiguredModel == b.ConfiguredModel && a.NoticeRun == b.NoticeRun && a.NoticeTitle == b.NoticeTitle && Equals(a.Usage, b.Usage)
            && SameList(a.RootEntries, b.RootEntries, SameEntry) && SameList(a.ResultEntries, b.ResultEntries, SameEntry)
            && SameList(a.Agents, b.Agents, SameAgent) && SameList(a.ResponseRecords, b.ResponseRecords, (x, y) => Equals(x, y));

    private static bool SameAgent(MightyGraphAgent a, MightyGraphAgent b) => ReferenceEquals(a, b)
        || a.Id == b.Id && a.ParentID == b.ParentID && a.Title == b.Title && a.Input == b.Input && a.Status == b.Status && a.Kind == b.Kind
            && Equals(a.Usage, b.Usage) && a.ActivityGeneration == b.ActivityGeneration
            && a.Background == b.Background && a.NoticeReply == b.NoticeReply && a.SourceBlockId == b.SourceBlockId
            && SameList(a.Entries, b.Entries, SameEntry) && SameList(a.ResponseRecords, b.ResponseRecords, (x, y) => Equals(x, y));

    /// <summary>A run as the page holds it: a task block's output file stays on this side (never shown, never on the page).</summary>
    internal static MightyGraphRun PageRun(MightyGraphRun run)
    {
        if (!run.Agents.Any(agent => agent.OutputFile is not null)) return run;
        var copy = run.Copy();
        foreach (var agent in copy.Agents) agent.OutputFile = null;
        return copy;
    }

    /// <summary><see cref="PageRun"/> over a pane's runs; the same list when none holds an output file.</summary>
    internal static List<MightyGraphRun>? PageRuns(List<MightyGraphRun>? runs) =>
        runs is null || !runs.Any(run => run.Agents.Any(agent => agent.OutputFile is not null)) ? runs : runs.Select(PageRun).ToList();

    /// <summary>The state as the page is sent it whole: every pane's runs without their output files.</summary>
    internal static AppSnapshot PageSnapshot(AppSnapshot snapshot) =>
        snapshot with { Sessions = snapshot.Sessions.Select(session => PageRuns(session.GraphRuns) is var runs && ReferenceEquals(runs, session.GraphRuns) ? session : session with { GraphRuns = runs }).ToList() };

    /// <summary>A pane's fields but its two lists, as the page's <c>RunSession</c> holds them.</summary>
    internal static JsonObject Fields(RunSession session)
    {
        var node = JsonSerializer.SerializeToNode(session with { Logs = [], GraphRuns = null }, Wire.Json)!.AsObject();
        node.Remove("logs"); node.Remove("graphRuns");
        return node;
    }

    /// <summary>
    /// Writes <paramref name="next"/> as a change from <paramref name="previous"/> (whose fields were <paramref name="before"/>),
    /// or nothing when nothing the page holds changed. Gives back whether it wrote.
    /// </summary>
    internal static bool Write(Utf8JsonWriter writer, RunSession previous, JsonObject before, RunSession next, JsonObject after)
    {
        var set = after.Where(field => !before.TryGetPropertyValue(field.Key, out var old) || !JsonNode.DeepEquals(old, field.Value)).ToList();
        var unset = before.Select(field => field.Key).Where(key => !after.ContainsKey(key)).ToList();
        var logs = Diff(previous.Logs, next.Logs, entry => entry.Id, SameEntry);
        var runs = Diff(previous.GraphRuns ?? [], next.GraphRuns ?? [], run => run.Id, SameRun);
        if (set.Count == 0 && unset.Count == 0 && logs.Empty && runs.Empty) return false;
        writer.WriteStartObject(); writer.WriteString("id", next.Id);
        if (set.Count > 0)
        {
            writer.WriteStartObject("set");
            foreach (var (key, value) in set) { writer.WritePropertyName(key); if (value is null) writer.WriteNullValue(); else value.WriteTo(writer, Wire.Json); }
            writer.WriteEndObject();
        }
        if (unset.Count > 0) { writer.WritePropertyName("unset"); JsonSerializer.Serialize(writer, unset, Wire.Json); }
        if (!logs.Empty) WriteList(writer, "logs", logs);
        if (!runs.Empty)
        {
            for (var index = 0; index < runs.Replaced.Count; index++) runs.Replaced[index] = PageRun(runs.Replaced[index]);
            for (var index = 0; index < runs.Added.Count; index++) runs.Added[index] = PageRun(runs.Added[index]);
            WriteList(writer, "graphRuns", runs);
        }
        writer.WriteEndObject();
        return true;
    }

    private static void WriteList<T>(Utf8JsonWriter writer, string name, ListChange<T> change)
    {
        writer.WriteStartObject(name);
        if (change.Drop > 0) writer.WriteNumber("drop", change.Drop);
        if (change.Replaced.Count > 0) { writer.WritePropertyName("replaced"); JsonSerializer.Serialize(writer, change.Replaced, Wire.Json); }
        if (change.Added.Count > 0) { writer.WritePropertyName("added"); JsonSerializer.Serialize(writer, change.Added, Wire.Json); }
        if (change.Order is not null) { writer.WritePropertyName("order"); JsonSerializer.Serialize(writer, change.Order, Wire.Json); }
        writer.WriteEndObject();
    }
}
