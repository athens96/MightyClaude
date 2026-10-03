namespace MightyClaude.Core;

public static class ModelUsageFormat
{
    public static List<(string Model, GraphTokenUsage Usage)> BlockModels(IReadOnlyList<GraphResponseRecord> records)
    {
        var order = new List<string>();
        var sums = new Dictionary<string, GraphTokenUsage>();
        foreach (var r in records)
        {
            var key = r.Model ?? "";
            if (!sums.ContainsKey(key)) { order.Add(key); sums[key] = new(); }
            sums[key] = sums[key] + r.Usage;
        }
        return order.Select(m => (m, sums[m])).ToList();
    }

    public static string? BlockCapsule(GraphTokenUsage? usage, IReadOnlyList<GraphResponseRecord> records, string? nodeModelLabel, IReadOnlyList<ModelOption>? catalog = null, bool versioned = false)
    {
        if (versioned) nodeModelLabel = VersionedNodeLabel(nodeModelLabel);
        if (records.Count == 0) return nodeModelLabel;
        if (usage is null || usage.IsEmpty) return nodeModelLabel;
        var tokenStr = GraphTokenUsage.Compact(usage.Total);
        var models = BlockModels(records).Where(m => !string.IsNullOrEmpty(m.Model)).ToList();
        if (models.Count == 0) return tokenStr;
        var first = ShortName(models[0].Model, catalog, versioned);
        return models.Count == 1 ? $"{tokenStr} · {first}" : $"{tokenStr} · {first} +{models.Count - 1}";
    }

    public static string BlockCapsuleHelp(IReadOnlyList<GraphResponseRecord> records, IReadOnlyList<ModelOption>? catalog = null, bool versioned = false) =>
        string.Join("\n", BlockModels(records)
            .Where(m => !string.IsNullOrEmpty(m.Model))
            .Select(m => $"{ShortName(m.Model, catalog, versioned)} · {m.Usage.Detail}"));

    public static string? ActivitySuffix(string activityId, IReadOnlyList<GraphResponseRecord> records, GraphChildBlock? childBlock, IReadOnlyList<ModelOption>? catalog = null, bool versioned = false)
    {
        foreach (var record in records)
        {
            if (!record.ActivityIds.Contains(activityId)) continue;
            string callerPart;
            var attribution = CallerAttribution(activityId, [record]);
            if (attribution is not null)
            {
                var tokenStr = GraphTokenUsage.Compact(attribution.Value);
                callerPart = !string.IsNullOrEmpty(record.Model)
                    ? $"{ShortName(record.Model!, catalog, versioned)} · {tokenStr}"
                    : tokenStr;
            }
            else
            {
                callerPart = Locale.Get("usage.modelUsage.sameResponse");
            }
            if (childBlock is not null)
            {
                var childCapsule = BlockCapsule(childBlock.Usage, childBlock.Records, null, catalog, versioned);
                if (childCapsule is not null)
                {
                    var sub = Locale.Get("usage.modelUsage.subagentPrefix");
                    return $"{callerPart} · {sub} {childCapsule}";
                }
            }
            return callerPart;
        }
        if (childBlock is not null)
        {
            var childCapsule = BlockCapsule(childBlock.Usage, childBlock.Records, null, catalog, versioned);
            if (childCapsule is not null)
            {
                var sub = Locale.Get("usage.modelUsage.subagentPrefix");
                return $"{sub} {childCapsule}";
            }
        }
        return null;
    }

    public static long? CallerAttribution(string activityId, IReadOnlyList<GraphResponseRecord> records)
    {
        foreach (var record in records)
        {
            var index = record.ActivityIds.ToList().IndexOf(activityId);
            if (index < 0) continue;
            return index == 0 ? record.Usage.Total : null;
        }
        return null;
    }

    /// <summary>
    /// A saved <see cref="GraphModelLabel.NodeModelLabel"/> (<c>claude-opus-4-5</c> or
    /// <c>opus · 설정</c>) with its model part read through <see cref="ModelLabel"/>. A configured
    /// alias stays the bare family: today's catalogue does not say which version an older run used.
    /// </summary>
    public static string? VersionedNodeLabel(string? label)
    {
        if (label is null) return null;
        var marker = " " + Locale.Get("graph.nodeModel.configuredSuffix");
        var configured = label.EndsWith(marker, StringComparison.Ordinal);
        var text = ModelLabel.Text(configured ? label[..^marker.Length] : label);
        return configured ? text + marker : text;
    }

    /// <summary>
    /// Short display name for a model id looked up against the catalogue. <paramref name="versioned"/>
    /// (the drawn canvas) reads the id with <see cref="ModelLabel"/> first — <c>claude-opus-5-5</c> →
    /// <c>Opus 5.5</c>; the shared graph vectors keep the catalogue-only name, as on macOS.
    /// </summary>
    public static string ShortName(string modelId, IReadOnlyList<ModelOption>? catalog = null, bool versioned = false)
    {
        if (versioned && ModelLabel.Format(modelId) is { } label) return label;
        var match = catalog?.FirstOrDefault(o => o.Value == modelId || o.ResolvedModel == modelId);
        if (match is null) return versioned ? ModelLabel.Text(modelId) : modelId;
        return versioned ? ModelLabel.Option(match) : match.DisplayName;
    }
}
