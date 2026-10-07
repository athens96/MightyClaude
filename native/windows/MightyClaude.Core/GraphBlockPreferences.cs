namespace MightyClaude.Core;

/// <summary>Bounded remembered card sizes; zoom and expanded state remain transient.</summary>
public static class GraphBlockPreferences
{
    public const int MaximumSizes = 512;
    public static Dictionary<string, GraphBlockSize>? Normalize(IReadOnlyDictionary<string, GraphBlockSize>? values)
    {
        if (values is null) return null;
        var result = new Dictionary<string, GraphBlockSize>(StringComparer.Ordinal);
        foreach (var item in values.Take(MaximumSizes))
            if (item.Key.Length is > 0 and <= 512 && !item.Key.Any(char.IsControl) && item.Value?.NormalizedFor(item.Key) is { } size) result[item.Key] = size;
        return result.Count == 0 ? null : result;
    }
    public static RunSession Set(RunSession session, string id, GraphBlockSize? size)
    {
        var next = Normalize(session.GraphBlockSizes) ?? [];
        next.Remove(id);
        if (size?.NormalizedFor(id) is { } normalized)
        {
            if (next.Count >= MaximumSizes) next.Remove(next.Keys.First());
            next[id] = normalized;
        }
        return session with { GraphBlockSizes = Normalize(next) };
    }
}
