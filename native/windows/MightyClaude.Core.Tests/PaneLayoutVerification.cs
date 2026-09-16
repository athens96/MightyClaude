using System.Text.Json;
using MightyClaude.Core;

internal static class PaneLayoutVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    internal static async Task Run()
    {
        var one = new PaneLayoutNode { Id = "one", SessionIds = ["a", "b"], SelectedSessionId = "b" };
        var two = new PaneLayoutNode { Id = "two", SessionIds = ["c"] };
        var root = new PaneLayoutNode { Id = "split", Kind = "split", Axis = "horizontal", Ratio = .4, Children = [one, two] };
        var normalized = PaneLayout.Normalize(root, ["a", "b", "c"], "b")!;
        Check(PaneLayout.Groups(normalized).First().SelectedSessionId == "b", "Selected tab preserved");
        var reorder = PaneLayout.Move(normalized, "a", "one", index: 2);
        Check(PaneLayout.Groups(reorder).First().SessionIds.SequenceEqual(["b", "a"]), "Forward reorder accounts for removal");
        reorder = PaneLayout.Move(reorder, "a", "one", index: 0);
        Check(PaneLayout.Groups(reorder).First().SessionIds.SequenceEqual(["a", "b"]), "Backward reorder");
        var merged = PaneLayout.Move(normalized, "c", "one", index: 1);
        Check(merged.Kind == "tabs" && merged.SessionIds.SequenceEqual(["a", "c", "b"]) && merged.SelectedSessionId == "c", "Merge prunes empty source split");
        foreach (var edge in new[] { "left", "right", "top", "bottom" })
        {
            var divided = PaneLayout.Move(merged, "c", "one", edge);
            Check(divided.Kind == "split" && divided.Axis == (edge is "left" or "right" ? "horizontal" : "vertical"), "Same group tab splits along requested axis");
            Check(PaneLayout.Sessions(divided.Children[edge is "left" or "top" ? 0 : 1]).SequenceEqual(["c"]), "Split direction");
            Check(PaneLayout.Sessions(divided).Order().SequenceEqual(new[] { "a", "b", "c" }), "Every session appears exactly once");
        }
        Check(ReferenceEquals(PaneLayout.Move(two, "c", "two", "left"), two), "Single tab same group edge is no-op");
        Check(ReferenceEquals(PaneLayout.Move(root, "a", "missing"), root), "Missing target preserves source");
        Check(ReferenceEquals(PaneLayout.Move(root, "foreign", "one"), root), "External session rejected");
        Check(PaneLayout.Resize(root, "split", 5).Ratio == .85 && PaneLayout.Resize(root, "split", -2).Ratio == .15, "Resize clamps");
        Check(ReferenceEquals(PaneLayout.Resize(root, "split", double.NaN), root), "Invalid resize no-op");
        var malformed = root with { Ratio = double.PositiveInfinity, Children = [one with { SessionIds = ["a", "a", "outside"], SelectedSessionId = "outside" }, two with { Id = "one", SessionIds = ["a", "c"] }] };
        var repaired = PaneLayout.Normalize(malformed, ["a", "b", "c"], "b")!;
        Check(repaired.Ratio == .5 && PaneLayout.Sessions(repaired).SequenceEqual(["a", "b", "c"]), "Foreign and duplicate tabs removed, missing appended");
        Check(PaneLayout.Groups(repaired).Select(g => g.Id).Distinct().Count() == 2, "Duplicate node IDs repaired");
        var pruned = PaneLayout.Normalize(root, ["c"])!;
        Check(pruned.Kind == "tabs" && pruned.SessionIds.SequenceEqual(["c"]), "Closing tabs collapses empty groups");
        Check(PaneLayout.Normalize(root, []) is null, "Empty workspace has no tree");
        var deep = one;
        for (var depth = 0; depth < 15; depth++) deep = new() { Id = "depth-" + depth, Kind = "split", Children = [new() { Id = "sibling-" + depth, SessionIds = ["x" + depth] }, deep] };
        Check(ReferenceEquals(PaneLayout.Move(deep, "a", "one", "right"), deep), "Depth overflow preserves complete existing tree");
        foreach (var preset in new[] { "grid", "columns", "tabs", "focus" })
        {
            var presetRoot = PaneLayout.Preset(["a", "b", "c", "d", "e"], preset, "e")!;
            Check(PaneLayout.Sessions(presetRoot).SequenceEqual(["a", "b", "c", "d", "e"]), "Preset preserves sessions/order");
            if (preset is "tabs" or "focus") Check(presetRoot.SelectedSessionId == "e", "Tab preset active selection");
        }
        var legacy = JsonSerializer.Deserialize<AppSnapshot>("{\"version\":1,\"workspaces\":[],\"sessions\":[]}", Wire.Json)!;
        Check(legacy.PaneLayouts is null && !JsonSerializer.Serialize(legacy, Wire.Json).Contains("paneLayouts"), "Legacy optional field omitted");
        var path = Path.Combine(Path.GetTempPath(), "mighty-layout-test-" + Wire.Id()); Directory.CreateDirectory(path);
        try
        {
            var store = new StateStore(Path.Combine(path, "profile")); await store.LoadAsync(); var workspace = store.ApproveLocal(path);
            var state = new AppSnapshot { Workspaces = [workspace], Sessions = new[] { "a", "b", "c" }.Select(id => new RunSession { Id = id, WorkspaceId = workspace.Id }).ToList(), ActiveWorkspaceId = workspace.Id, ActiveSessionId = "b", Layout = "custom", PaneLayouts = new() { [workspace.Id] = root, ["foreign"] = root } };
            await store.SaveAsync(state); var restored = await new StateStore(Path.Combine(path, "profile")).LoadAsync();
            Check(restored.Layout == "custom" && restored.PaneLayouts!.Count == 1, "Per-workspace persistence filters stale keys");
            Check(JsonSerializer.Serialize(restored.PaneLayouts![workspace.Id], Wire.Json) == JsonSerializer.Serialize(normalized, Wire.Json), "Tree JSON survives restart exactly");
            var wire = JsonSerializer.Serialize(restored, Wire.Json); Check(wire.Contains("\"sessionIds\"") && wire.Contains("\"selectedSessionId\"") && wire.Contains("\"axis\":\"horizontal\""), "Shared camelCase wire");
            var focused = StateStore.Normalize(restored with { Layout = "focus" }, false);
            var custom = StateStore.Normalize(focused with { Layout = "custom" }, false);
            Check(JsonSerializer.Serialize(custom.PaneLayouts, Wire.Json) == JsonSerializer.Serialize(restored.PaneLayouts, Wire.Json), "Focus viewport leaves saved split layout intact");
        }
        finally { Directory.Delete(path, true); }
    }
}
