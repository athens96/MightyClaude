namespace MightyClaude.Core;

/// <summary>Bounded remembered card sizes; zoom and expanded state remain transient.</summary>
public static class GraphBlockPreferences
{
    /// <summary>
    /// Places in a pane's saved sizes (macOS maximumSavedSizes). A request and its 실행 block share one place,
    /// so splitting the request block costs a pane no other block's size.
    /// </summary>
    public const int MaximumSizes = 1_024;
    /// <summary>
    /// The diagram layout a pane's saved sizes are dragged in (<see cref="RunSession.GraphSizesLayout"/>; macOS splitLayout).
    /// Before it (null) the request block held the main agent's records as well, so its size belongs to the 실행
    /// block that holds them now.
    /// </summary>
    public const int SplitLayout = 1;

    private static GraphBlockSize? Clean(string id, GraphBlockSize? size) =>
        id.Length is > 0 and <= 512 && !id.Any(char.IsControl) ? size?.NormalizedFor(id) : null;

    /// <summary>The sizes that can be drawn, whoever owns them; a place holds two at most. A pane's saved state keeps only its own (<see cref="Normalize(IReadOnlyDictionary{string, GraphBlockSize}?, IReadOnlyList{MightyGraphRun})"/>).</summary>
    public static Dictionary<string, GraphBlockSize>? Normalize(IReadOnlyDictionary<string, GraphBlockSize>? values)
    {
        if (values is null) return null;
        var result = new Dictionary<string, GraphBlockSize>(StringComparer.Ordinal);
        foreach (var item in values.Take(2 * MaximumSizes))
            if (Clean(item.Key, item.Value) is { } size) result[item.Key] = size;
        return result.Count == 0 ? null : result;
    }

    /// <summary>
    /// Keep only nodes owned by this session, preferring its recent turns: requests, their 실행 blocks, results,
    /// agents and the answered plans (macOS <c>MightyGraphBlockSize.normalized(_:runs:)</c>). The pending plan card
    /// keeps the pane's one size (<see cref="RunSession.GraphPlanSize"/>), not one per run. Pending input is stable
    /// across turns even while its card is hidden.
    /// </summary>
    public static Dictionary<string, GraphBlockSize>? Normalize(IReadOnlyDictionary<string, GraphBlockSize>? values, IReadOnlyList<MightyGraphRun> runs)
    {
        if (values is null || values.Count == 0) return null;
        var result = new Dictionary<string, GraphBlockSize>(StringComparer.Ordinal);
        var places = 0;
        // The ids share one place: kept together, or not at all.
        void Retain(params string[] ids)
        {
            if (places >= MaximumSizes) return;
            var kept = false;
            foreach (var id in ids)
                if (Clean(id, values.GetValueOrDefault(id)) is { } size) { result[id] = size; kept = true; }
            if (kept) places++;
        }
        Retain(MightyGraphCamera.PendingNodeID);
        for (var i = runs.Count - 1; i >= 0; i--)
        {
            var run = runs[i];
            Retain(MightyGraphBlockSize.NodeId(run.Id, "request"), MightyGraphBlockSize.NodeId(run.Id, MightyGraphLayout.WorkSuffix));
            if (run.Settled) Retain(MightyGraphBlockSize.NodeId(run.Id, "result"));
            foreach (var agent in run.Agents) Retain(MightyGraphBlockSize.NodeId(run.Id, "agent:" + agent.Id));
            // Answered plans stay beside the request for good.
            var records = MightyGraphBlockSize.NodeId(run.Id, MightyGraphLayout.PlanRecordSuffix);
            foreach (var id in values.Keys.Where(key => key.StartsWith(records, StringComparison.Ordinal)).Order(StringComparer.Ordinal)) Retain(id);
            if (places >= MaximumSizes) break;
        }
        return result.Count == 0 ? null : result;
    }

    /// <summary>
    /// A pane's saved sizes from before the 실행 split, moved once (macOS splittingExecution): a request size becomes
    /// its 실행 block's (unless that block has one) and the request block goes back to its own, short size.
    /// </summary>
    public static Dictionary<string, GraphBlockSize>? SplittingExecution(IReadOnlyDictionary<string, GraphBlockSize>? values)
    {
        if (values is null) return null;
        var result = new Dictionary<string, GraphBlockSize>(values, StringComparer.Ordinal);
        foreach (var (id, size) in values)
        {
            if (MightyGraphBlockSize.Suffix(id) != "request") continue;
            result.Remove(id);
            result.TryAdd(id[..^"request".Length] + MightyGraphLayout.WorkSuffix, size);
        }
        return result.Count == 0 ? null : result;
    }

    public static RunSession Set(RunSession session, string id, GraphBlockSize? size)
    {
        var next = Normalize(session.GraphBlockSizes) ?? [];
        next.Remove(id);
        if (size?.NormalizedFor(id) is { } normalized)
        {
            if (next.Count >= 2 * MaximumSizes) next.Remove(next.Keys.First());
            next[id] = normalized;
        }
        return session with { GraphBlockSizes = Normalize(next) };
    }
}
