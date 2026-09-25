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

    public static string? BlockCapsule(GraphTokenUsage? usage, IReadOnlyList<GraphResponseRecord> records, string? nodeModelLabel, IReadOnlyList<ModelOption>? catalog = null)
    {
        if (records.Count == 0) return nodeModelLabel;
        if (usage is null || usage.IsEmpty) return nodeModelLabel;
        var tokenStr = GraphTokenUsage.Compact(usage.Total);
        var models = BlockModels(records).Where(m => !string.IsNullOrEmpty(m.Model)).ToList();
        if (models.Count == 0) return tokenStr;
        var first = ShortName(models[0].Model, catalog);
        return models.Count == 1 ? $"{tokenStr} · {first}" : $"{tokenStr} · {first} +{models.Count - 1}";
    }

    public static string BlockCapsuleHelp(IReadOnlyList<GraphResponseRecord> records, IReadOnlyList<ModelOption>? catalog = null) =>
        string.Join("\n", BlockModels(records)
            .Where(m => !string.IsNullOrEmpty(m.Model))
            .Select(m => $"{ShortName(m.Model, catalog)} · {m.Usage.Detail}"));

    public static string? ActivitySuffix(string activityId, IReadOnlyList<GraphResponseRecord> records, GraphChildBlock? childBlock, IReadOnlyList<ModelOption>? catalog = null)
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
                    ? $"{ShortName(record.Model!, catalog)} · {tokenStr}"
                    : tokenStr;
            }
            else
            {
                callerPart = Locale.Get("usage.modelUsage.sameResponse");
            }
            if (childBlock is not null)
            {
                var childCapsule = BlockCapsule(childBlock.Usage, childBlock.Records, null, catalog);
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
            var childCapsule = BlockCapsule(childBlock.Usage, childBlock.Records, null, catalog);
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

    public static string ShortName(string modelId, IReadOnlyList<ModelOption>? catalog = null)
    {
        if (catalog is not null)
        {
            var match = catalog.FirstOrDefault(o => o.Value == modelId || o.ResolvedModel == modelId);
            if (match is not null) return match.DisplayName;
        }
        return modelId;
    }
}
