using System.Text.Json;
using MightyCode.Core;

/// <summary>
/// The role view's layout for the runs of native/contracts/fixtures/flow-vectors.json, which the web page's layout reads too
/// (native/windows/web/src/mighty/flow.test.ts): every node with its kind, frame and card, every edge (and whether it leaves a spine block's side), the canvas
/// size and origin, within 0.001. The Core's layout is what the WinUI host reads the page's copy from, so both sides must lay out the same nodes.
/// </summary>
internal static class FlowVectorVerification
{
    private static readonly Lazy<JsonElement> Fixture = new(() =>
    {
        const string name = "MightyCode.Core.Tests.FlowVectors.json";
        using var stream = typeof(FlowVectorVerification).Assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException(name + " is not embedded in MightyCode.Core.Tests");
        return JsonDocument.Parse(stream).RootElement.Clone();
    });

    private static void Check(bool value, string why) { if (!value) throw new InvalidOperationException(why); }
    private static void Near(double actual, double expected, string why) { if (Math.Abs(actual - expected) > 0.001) throw new InvalidOperationException($"{why}: expected {expected} but was {actual}"); }

    internal static Task TheSharedCasesLayOutAsThePageDoes()
    {
        var cases = Fixture.Value.GetProperty("cases").EnumerateArray().ToList();
        Check(cases.Count >= 7, "the fixture has its cases; got " + cases.Count);
        foreach (var item in cases)
        {
            var name = item.GetProperty("name").GetString() ?? "";
            var runs = JsonSerializer.Deserialize<List<MightyGraphRun>>(item.GetProperty("runs").GetRawText(), Wire.Json) ?? throw new InvalidOperationException(name + ": no runs");
            var opened = item.GetProperty("opened").EnumerateArray().Select(e => e.GetString() ?? "")
                .SelectMany(suffix => runs.Select(run => MightyGraphLayout.NodeID(run, suffix))).ToHashSet(StringComparer.Ordinal);
            var layout = MightyGraphLayout.Make(runs, "", item.GetProperty("running").GetBoolean(), new HashSet<string>(), splitsExecution: true, fanOut: new MightyGraphLayout.FanOut(opened));
            var expect = item.GetProperty("expect");
            var nodes = expect.GetProperty("nodes").EnumerateArray().ToList();
            Check(layout.Nodes.Count == nodes.Count, $"{name}: {nodes.Count} nodes expected, {layout.Nodes.Count} laid out: {string.Join(", ", layout.Nodes.Select(n => n.Id))}");
            for (var index = 0; index < nodes.Count; index++)
            {
                var (node, want) = (layout.Nodes[index], nodes[index]);
                var label = $"{name}: node {index}";
                Check(node.Id == want.GetProperty("id").GetString(), $"{label}: id {want.GetProperty("id").GetString()} expected, {node.Id} laid out");
                Check(node.Kind == want.GetProperty("kind").GetString(), $"{label}: kind {want.GetProperty("kind").GetString()} expected, {node.Kind} laid out");
                Check(node.IsCompact == (want.TryGetProperty("compact", out var compact) && compact.GetBoolean()), $"{label}: card");
                Near(node.Frame.X, want.GetProperty("x").GetDouble(), label + " x");
                Near(node.Frame.Y, want.GetProperty("y").GetDouble(), label + " y");
                Near(node.Frame.W, want.GetProperty("w").GetDouble(), label + " w");
                Near(node.Frame.H, want.GetProperty("h").GetDouble(), label + " h");
            }
            var edges = expect.GetProperty("edges").EnumerateArray().Select(e => (e.GetProperty("source").GetString(), e.GetProperty("target").GetString(), e.GetProperty("joins").GetBoolean(),
                e.TryGetProperty("side", out var side) && side.GetBoolean())).ToList();
            var laid = layout.Edges.Select(e => ((string?)e.Source, (string?)e.Target, e.Joins, e.Side)).ToList();
            Check(laid.SequenceEqual(edges), $"{name}: edges expected [{string.Join(", ", edges)}], laid out [{string.Join(", ", laid)}]");
            Near(layout.Size.W, expect.GetProperty("size").GetProperty("w").GetDouble(), name + ": width");
            Near(layout.Size.H, expect.GetProperty("size").GetProperty("h").GetDouble(), name + ": height");
            Near(layout.OriginX, expect.GetProperty("originX").GetDouble(), name + ": origin x");
            Near(layout.OriginY, expect.GetProperty("originY").GetDouble(), name + ": origin y");
        }
        return Task.CompletedTask;
    }
}
