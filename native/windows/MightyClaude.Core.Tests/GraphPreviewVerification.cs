using MightyClaude.Core;

internal static class GraphPreviewVerification
{
    private static void Check(bool value, string detail) { if (!value) throw new InvalidOperationException(detail); }
    internal static Task RememberedGraphSizesDriveLayoutAndNormalize()
    {
        var run = new MightyGraphRun { Id = "run", Status = "completed", Input = "request" };
        var request = MightyGraphBlockSize.NodeId(run.Id, "request");
        var pane = GraphBlockPreferences.Set(new RunSession(), request, new(777, 333));
        var layout = MightyGraphLayout.Make([run], "", false, new HashSet<string>(), blockSizes: pane.GraphBlockSizes);
        Check(layout.Nodes.Single(node => node.Id == request).Frame is { W: 777, H: 333 }, "saved dimensions control graph layout and edge positions");
        var restored = Wire.Clone(pane);
        Check(restored.GraphBlockSizes?[request] == new GraphBlockSize(777, 333), "custom sizes survive state serialization");
        pane = GraphBlockPreferences.Set(pane, request, null);
        Check(pane.GraphBlockSizes is null, "reset removes the remembered override");
        Check(GraphBlockPreferences.Normalize(new Dictionary<string, GraphBlockSize> { [request] = new(double.NaN, 100), ["other"] = new(1, 99999) }) is { Count: 1 } normalized && normalized["other"] == new GraphBlockSize(300, 1200), "corrupt saved preferences never reach layout");
        return Task.CompletedTask;
    }
    internal static Task LocalHtmlReadsOnlyItsOwnBoundedDirectory()
    {
        var root = Verification.Temp(); var docs = Path.Combine(root, "docs"); Directory.CreateDirectory(docs);
        try
        {
            File.WriteAllText(Path.Combine(docs, "한글.html"), "<h1>한글</h1><script src='chart.js'></script>");
            File.WriteAllText(Path.Combine(docs, "chart.js"), "document.title='chart'");
            File.WriteAllText(Path.Combine(root, "private.txt"), "outside document directory");
            var preview = new LocalHtmlDocument(root, "docs/한글.html");
            Check(preview.Read(preview.Address, "GET") is { MediaType: "text/html; charset=utf-8" }, "selected HTML renders locally");
            Check(preview.Read("https://" + preview.Host + "/chart.js", "GET") is { MediaType: "text/javascript; charset=utf-8" }, "sibling chart assets remain usable");
            Check(preview.Read("https://" + preview.Host + "/%2e%2e%2fprivate.txt", "GET") is null && preview.Read("https://example.com/chart.js", "GET") is null && preview.Read(preview.Address, "POST") is null, "escape, network and state-changing requests are refused");
            Check(!preview.Allows("file:///private.txt") && !preview.Allows("https://" + preview.Host + ":444/"), "navigation cannot leave the isolated origin");
            try { File.CreateSymbolicLink(Path.Combine(docs, "escape.txt"), Path.Combine(root, "private.txt")); }
            catch (Exception ex) when (OperatingSystem.IsWindows() && ex is IOException or UnauthorizedAccessException) { return Task.CompletedTask; }
            Check(preview.Read("https://" + preview.Host + "/escape.txt", "GET") is null, "asset symlinks cannot escape the document directory");
        }
        finally { Directory.Delete(root, true); }
        return Task.CompletedTask;
    }
}
