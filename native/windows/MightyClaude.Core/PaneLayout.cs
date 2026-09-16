namespace MightyClaude.Core;

/// <summary>A workspace layout consists of tab groups and binary splits.</summary>
public sealed record PaneLayoutNode
{
    public string Id { get; init; } = Wire.Id();
    public string Kind { get; init; } = "tabs";
    public List<string> SessionIds { get; init; } = [];
    public string? SelectedSessionId { get; init; }
    public string? Axis { get; init; }
    public double Ratio { get; init; } = .5;
    public List<PaneLayoutNode> Children { get; init; } = [];
}

public static class PaneLayout
{
    public static IEnumerable<PaneLayoutNode> Groups(PaneLayoutNode? node) => node is null ? [] : node.Kind == "tabs" ? [node] : node.Children.SelectMany(Groups);
    public static IEnumerable<string> Sessions(PaneLayoutNode? node) => Groups(node).SelectMany(g => g.SessionIds);
    private static PaneLayoutNode Tabs(IEnumerable<string> ids, string? active = null) { var values = ids.ToList(); return new() { SessionIds = values, SelectedSessionId = values.Contains(active ?? "") ? active : values.FirstOrDefault() }; }
    private static PaneLayoutNode Split(PaneLayoutNode a, PaneLayoutNode b, string axis, double ratio = .5) => new() { Kind = "split", Axis = axis, Ratio = ratio, Children = [a, b] };
    public static double ClampRatio(double ratio) => double.IsFinite(ratio) ? Math.Clamp(ratio, .15, .85) : .5;

    public static PaneLayoutNode? Normalize(PaneLayoutNode? root, IEnumerable<string> sessionIds, string? active = null)
    {
        var ordered = sessionIds.Take(128).Where(Wire.Identifier).Distinct().ToList();
        if (ordered.Count == 0) return null;
        var allowed = ordered.ToHashSet(); var seen = new HashSet<string>(); var nodes = new HashSet<string>(); var count = 0;
        PaneLayoutNode? Visit(PaneLayoutNode? node, int depth)
        {
            if (node is null || depth >= 16 || ++count > 255) return null;
            var id = Wire.Identifier(node.Id) && nodes.Add(node.Id) ? node.Id : Wire.Id(); nodes.Add(id);
            if (node.Kind == "tabs")
            {
                var ids = (node.SessionIds ?? []).Take(128).Where(id => allowed.Contains(id) && seen.Add(id)).ToList();
                return ids.Count == 0 ? null : new() { Id = id, SessionIds = ids, SelectedSessionId = ids.Contains(node.SelectedSessionId ?? "") ? node.SelectedSessionId : ids[0] };
            }
            if (node.Kind != "split") return null;
            var children = (node.Children ?? []).Take(2).Select(child => Visit(child, depth + 1)).OfType<PaneLayoutNode>().ToList();
            return children.Count switch { 0 => null, 1 => children[0], _ => new() { Id = id, Kind = "split", Axis = node.Axis == "vertical" ? "vertical" : "horizontal", Ratio = ClampRatio(node.Ratio), Children = children } };
        }
        var result = Visit(root, 0);
        var missing = ordered.Where(id => !seen.Contains(id)).ToList();
        if (result is null) result = Tabs(missing, active);
        else if (missing.Count > 0)
        {
            var target = Groups(result).First();
            result = Replace(result, target.Id, target with { SessionIds = target.SessionIds.Concat(missing).ToList() });
        }
        return active is null ? result : Select(result, active);
    }

    public static PaneLayoutNode? Preset(IEnumerable<string> sessionIds, string layout, string? active = null)
    {
        var ids = sessionIds.Take(128).Where(Wire.Identifier).Distinct().ToList(); if (ids.Count == 0) return null;
        if (layout is "focus" or "tabs" or "custom") return Tabs(ids, active);
        PaneLayoutNode Build(IReadOnlyList<string> values, int depth)
        {
            if (values.Count == 1) return Tabs(values);
            var middle = (values.Count + 1) / 2;
            return Split(Build(values.Take(middle).ToArray(), depth + 1), Build(values.Skip(middle).ToArray(), depth + 1), layout == "columns" || depth % 2 == 0 ? "horizontal" : "vertical", (double)middle / values.Count);
        }
        return Build(ids, 0);
    }

    public static PaneLayoutNode Select(PaneLayoutNode root, string sessionId) => root.Kind == "tabs"
        ? root.SessionIds.Contains(sessionId) ? root with { SelectedSessionId = sessionId } : root
        : root with { Children = root.Children.Select(child => Select(child, sessionId)).ToList() };
    public static PaneLayoutNode Resize(PaneLayoutNode root, string splitId, double ratio) => !double.IsFinite(ratio) ? root : root.Id == splitId && root.Kind == "split"
        ? root with { Ratio = ClampRatio(ratio) }
        : root with { Children = root.Children.Select(child => Resize(child, splitId, ratio)).ToList() };
    private static PaneLayoutNode Replace(PaneLayoutNode root, string id, PaneLayoutNode replacement) => root.Id == id ? replacement : root with { Children = root.Children.Select(child => Replace(child, id, replacement)).ToList() };
    private static PaneLayoutNode? Remove(PaneLayoutNode root, string sessionId)
    {
        if (root.Kind == "tabs")
        {
            var ids = root.SessionIds.Where(id => id != sessionId).ToList();
            return ids.Count == 0 ? null : root with { SessionIds = ids, SelectedSessionId = ids.Contains(root.SelectedSessionId ?? "") ? root.SelectedSessionId : ids[0] };
        }
        var children = root.Children.Select(child => Remove(child, sessionId)).OfType<PaneLayoutNode>().ToList();
        return children.Count switch { 0 => null, 1 => children[0], _ => root with { Children = children } };
    }

    /// <summary>Move one existing tab. Index is the insertion slot before removal.</summary>
    public static PaneLayoutNode Move(PaneLayoutNode root, string sessionId, string targetGroupId, string edge = "center", int? index = null)
    {
        var groups = Groups(root).ToList(); var source = groups.FirstOrDefault(g => g.SessionIds.Contains(sessionId)); var target = groups.FirstOrDefault(g => g.Id == targetGroupId);
        if (source is null || target is null || edge is not ("center" or "left" or "right" or "top" or "bottom")) return root;
        if (source.Id == target.Id)
        {
            if (edge == "center")
            {
                var oldIndex = target.SessionIds.IndexOf(sessionId); var slot = Math.Clamp(index ?? target.SessionIds.Count, 0, target.SessionIds.Count); if (oldIndex < slot) slot--;
                var ids = target.SessionIds.Where(id => id != sessionId).ToList(); ids.Insert(Math.Clamp(slot, 0, ids.Count), sessionId);
                return Replace(root, target.Id, target with { SessionIds = ids, SelectedSessionId = sessionId });
            }
            if (source.SessionIds.Count == 1) return root;
        }
        var remaining = Remove(root, sessionId)!;
        target = Groups(remaining).First(g => g.Id == targetGroupId);
        if (edge == "center")
        {
            var ids = target.SessionIds.ToList(); ids.Insert(Math.Clamp(index ?? ids.Count, 0, ids.Count), sessionId);
            return Replace(remaining, target.Id, target with { SessionIds = ids, SelectedSessionId = sessionId });
        }
        var tab = Tabs([sessionId]); var before = edge is "left" or "top";
        var result = Replace(remaining, target.Id, Split(before ? tab : target, before ? target : tab, edge is "left" or "right" ? "horizontal" : "vertical"));
        int Depth(PaneLayoutNode node) => node.Children.Count == 0 ? 0 : 1 + node.Children.Max(Depth);
        int Count(PaneLayoutNode node) => 1 + node.Children.Sum(Count);
        return Depth(result) >= 16 || Count(result) > 255 ? root : result;
    }
}
