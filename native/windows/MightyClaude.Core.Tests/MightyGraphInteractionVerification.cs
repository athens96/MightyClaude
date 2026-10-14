using MightyClaude.Core;

/// The diagram's own interaction rules on Windows, as the Mac has them: a block resizes from every side the user
/// grabs (macOS ResizeEdgesTests), the layout that takes every block off screen is noticed (macOS
/// MightyGraphCameraTests, the stranded watch), and the reference bubble's size is kept for every pane.
internal static class MightyGraphInteractionVerification
{
    private static void Check(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
    private static readonly GraphRect Frame = new(100, 100, 300, 200);

    internal static Task TheBandReachesFurtherOutsideThanInside()
    {
        static ResizeEdges At(double x, double y) => ResizeEdgeRules.At((x, y), Frame, outside: 5, inside: 2);
        Check(At(97, 200) == ResizeEdges.Left && At(403, 200) == ResizeEdges.Right && At(250, 101) == ResizeEdges.Top && At(250, 298) == ResizeEdges.Bottom, "each border has its band");
        Check(At(101, 101) == (ResizeEdges.Left | ResizeEdges.Top), "near a corner that is both sides");
        // Inside the thin inner strip belongs to the box's own scrollers and text.
        Check(At(104, 200) == ResizeEdges.None && At(250, 200) == ResizeEdges.None && At(90, 200) == ResizeEdges.None, "past the band nothing resizes");
        // The band is 6pt outside the border at 100%, never under 4, and 2pt inside; the corner handle 22pt, never under 16.
        Check(ResizeEdgeRules.Band(1) == 6 && ResizeEdgeRules.Band(0.5) == 4 && ResizeEdgeRules.Band(1.5) == 9 && ResizeEdgeRules.BandInside == 2, "the band's reach at each zoom");
        Check(ResizeEdgeRules.HandleSide(1) == 22 && ResizeEdgeRules.HandleSide(0.5) == 16 && ResizeEdgeRules.HandleSide(1.5) == 33, "the corner handle's side at each zoom");
        return Task.CompletedTask;
    }

    internal static Task EverySideGrowsAwayFromTheBoxAndHonoursZoomAndBounds()
    {
        var (minimum, maximum, initial) = ((300.0, 140.0), (1400.0, 1200.0), (360.0, 240.0));
        Check(ResizeEdges.BottomRight.Size(initial, (90, 60), 1.5, minimum, maximum) == (420, 280), "the corner handle at 150%");
        Check(ResizeEdges.Left.Size(initial, (-40, 99), 1, minimum, maximum) == (400, 240), "the left side grows leftwards and leaves the height");
        Check(ResizeEdges.Top.Size(initial, (99, -30), 1, minimum, maximum) == (360, 270), "the top side grows upwards and leaves the width");
        Check((ResizeEdges.Left | ResizeEdges.Top).Size(initial, (4000, 4000), 1, minimum, maximum) == minimum, "never below the least");
        Check(ResizeEdges.Right.Size(initial, (9000, 0), 1, minimum, maximum).W == 1400, "never above the most");
        Check(ResizeEdges.Bottom.Size(initial, (0, 50), double.NaN, minimum, maximum) == (360, 290), "a broken zoom is 100%");
        Check(ResizeEdges.BottomRight.Horizontal() && ResizeEdges.BottomRight.Vertical() && ResizeEdges.Left.Horizontal() && !ResizeEdges.Left.Vertical() && !ResizeEdges.Top.Horizontal(), "which way each side moves");
        return Task.CompletedTask;
    }

    internal static Task TheCornerOppositeTheDraggedSidesStaysPut()
    {
        Check(ResizeEdges.BottomRight.PinnedUnit() == (0, 0) && ResizeEdges.Left.PinnedUnit() == (1, 0) && (ResizeEdges.Left | ResizeEdges.Top).PinnedUnit() == (1, 1), "the pinned corner");
        // A block 300 wide whose left side was dragged out to 400: the layout recentres it, the camera keeps its right edge where it was.
        var (zoom, camera) = (0.8, (40.0, -60.0));
        var unit = ResizeEdges.Left.PinnedUnit();
        var anchor = (camera.Item1 + (Frame.X + unit.X * Frame.W) * zoom, camera.Item2 + (Frame.Y + unit.Y * Frame.H) * zoom);
        var recentred = new GraphRect(50, 130, 400, 200);
        var pinned = ResizeEdgeRules.PinnedCamera(anchor, unit, recentred, zoom);
        Check(Math.Abs(pinned.X + recentred.MaxX * zoom - anchor.Item1) < 0.001 && Math.Abs(pinned.Y + recentred.Y * zoom - anchor.Item2) < 0.001, "the right edge and the top keep their place on screen");
        Check(ResizeEdgeRules.PinnedCamera(anchor, unit, Frame, zoom) is var same && Math.Abs(same.X - camera.Item1) < 0.001 && Math.Abs(same.Y - camera.Item2) < 0.001, "a block that did not move leaves the camera");
        return Task.CompletedTask;
    }

    internal static Task APressResizesByTheHandleThenByABand()
    {
        var under = ("under", new GraphRect(380, 280, 300, 200));
        (string Id, GraphRect Frame)[] frames = [("block", Frame), under];
        Check(ResizeEdgeRules.Target((395, 295), [("block", Frame)], 1) is { Id: "block", Edges: ResizeEdges.BottomRight }, "the corner handle is the right and the bottom");
        Check(ResizeEdgeRules.Target((250, 304), [("block", Frame)], 1) is { Id: "block", Edges: ResizeEdges.Bottom }, "6pt under the border is the bottom band");
        Check(ResizeEdgeRules.Target((250, 308), [("block", Frame)], 1) is null && ResizeEdgeRules.Target((250, 200), [("block", Frame)], 1) is null, "past the band, and inside the block, nothing resizes");
        // Two blocks' bands meet: the one drawn last takes the press.
        Check(ResizeEdgeRules.Target((381, 281), frames, 1) is { Id: "under", Edges: ResizeEdges.Left | ResizeEdges.Top }, "the block drawn last wins");
        // Zoomed in, the handle and the band are larger on screen.
        Check(ResizeEdgeRules.Target((372, 272), [("block", Frame)], 1.5) is { Edges: ResizeEdges.BottomRight } && ResizeEdgeRules.Target((372, 272), [("block", Frame)], 1) is null, "the handle grows with the zoom");
        // Only the flow's blocks and the answered plans resize.
        Check(new[] { "request", MightyGraphLayout.WorkKind, "agent", "result", "draft", "plan", "planRecord" }.All(MightyGraphLayout.IsResizable)
            && !new[] { "resultFiles", "history", MightyGraphLayout.ExecutionKind, MightyGraphLayout.ImagesKind }.Any(MightyGraphLayout.IsResizable), "which blocks resize");
        return Task.CompletedTask;
    }

    internal static Task TheStrandedWatchJudgesOnlyLayoutMovesUnderAStillCamera()
    {
        var viewport = (800.0, 600.0);
        var onScreen = new GraphRect(120, 240, 500, 280);
        var gone = new GraphRect(120, 9_000, 500, 280);
        var draft = (MightyGraphCamera.PendingNodeID, new GraphRect(120, 600, 500, 140));
        bool Observe(MightyGraphCamera.StrandedWatch watch, (string, GraphRect)[] nodes, double cameraY = -200, double zoom = 1, string? newest = "one", bool settled = true) =>
            watch.Observe(nodes, (-100, cameraY), zoom, viewport, newest, settled) is not null;
        // The first pass has nothing to compare with; the next one is judged, and it fires once per loss.
        var watch = new MightyGraphCamera.StrandedWatch();
        Check(!Observe(watch, [("a", onScreen)]) && Observe(watch, [("a", gone)]) && !Observe(watch, [("a", gone)]), "judged once");
        // A pass that arrives mid-drag is judged after the drag, not dropped, and says so.
        watch = new();
        Observe(watch, [("a", onScreen)]);
        Check(!Observe(watch, [("a", gone)], settled: false) && watch.IsWithholding, "an unsettled pass is withheld");
        Check(Observe(watch, [("a", gone)]) && !watch.IsWithholding, "and judged once settled");
        // A withheld move that went back to where it was leaves nothing to judge.
        watch = new();
        Observe(watch, [("a", onScreen)]); Observe(watch, [("a", gone)], settled: false);
        Check(!Observe(watch, [("a", onScreen)], settled: false) && !watch.IsWithholding, "back at the baseline there is nothing to judge");
        // Whoever moved the camera owns what it shows: no verdict on that pass, and the next compares against what the new camera saw.
        watch = new();
        Observe(watch, [("a", onScreen)]);
        Check(!Observe(watch, [("a", gone)], cameraY: -260) && !Observe(watch, [("a", gone)], cameraY: -260), "a moved camera is its mover's");
        watch = new();
        Observe(watch, [("a", onScreen)]);
        Check(!Observe(watch, [("a", gone)], zoom: 0.9), "so is a zoom");
        // A new newest request is aimed at by the run-id rule.
        watch = new();
        Observe(watch, [("a", onScreen)]);
        Check(!Observe(watch, [("a", gone), ("b", gone.OffsetBy(0, 400))], newest: "two"), "a new request has its own rule");
        // Clearing the composer removes the draft block the user was looking at; nothing moved.
        watch = new();
        Observe(watch, [("a", gone), draft]);
        Check(!Observe(watch, [("a", gone)]) && !Observe(watch, [("a", gone), draft]), "the draft block is never lost");
        watch = new();
        Observe(watch, [("a", gone), draft]);
        Check(!Observe(watch, [("a", gone.OffsetBy(0, 40))]), "nor what the user was looking at");
        return Task.CompletedTask;
    }

    internal static Task ALossNamesTheCardTheUserSawMostOf()
    {
        // Visible: (100, 200) 800 x 600. "big" fills far more of it than "edge".
        var (big, edge, away) = (new GraphRect(120, 240, 500, 480), new GraphRect(640, 700, 360, 240), new GraphRect(120, 9_000, 500, 280));
        var watch = new MightyGraphCamera.StrandedWatch();
        watch.Observe([("edge", edge), ("big", big)], (-100, -200), 1, (800, 600), "one", true);
        var loss = watch.Observe([("edge", away), ("big", away.OffsetBy(0, 600))], (-100, -200), 1, (800, 600), "one", true);
        Check(loss == new MightyGraphCamera.StrandedWatch.Loss("big", "one"), "the loss names the card seen most: " + loss);
        return Task.CompletedTask;
    }

    internal static async Task TheReferenceBubblesSizeIsKeptForEveryPane()
    {
        // Older state has none: the bubble opens 420 wide and as tall as the room.
        var legacy = System.Text.Json.JsonSerializer.Deserialize<AppSnapshot>("""{"version":1,"workspaces":[],"sessions":[],"layout":"grid","theme":"dark","sidebarWidth":300}""", Wire.Json)!;
        Check(legacy.ReferenceBubbleWidth is null && legacy.ReferenceBubbleHeight is null && ReferencePreview.BubbleDefaultWidth == 420, "old state opens the bubble at its default size");
        Check(ReferencePreview.BubbleSize(640, 480) == (640, 480) && ReferencePreview.BubbleSize(120, 90) == (ReferencePreview.BubbleMinimumWidth, ReferencePreview.BubbleMinimumHeight), "a saved size is kept, never under its least");
        Check(ReferencePreview.BubbleSize(double.NaN, 0) == (null, null) && ReferencePreview.BubbleSize(-5, double.PositiveInfinity) == (null, null) && ReferencePreview.BubbleSize(500, null) == (500, null), "a broken or absent side is none");
        var normalized = StateStore.Normalize(new AppSnapshot { ReferenceBubbleWidth = 10, ReferenceBubbleHeight = double.NaN }, false);
        Check(normalized.ReferenceBubbleWidth == ReferencePreview.BubbleMinimumWidth && normalized.ReferenceBubbleHeight is null, "normalize keeps the size within its least");
        var directory = Verification.Temp();
        try
        {
            var store = new StateStore(directory); await store.LoadAsync();
            await store.SaveAsync(new AppSnapshot { ReferenceBubbleWidth = 610, ReferenceBubbleHeight = 380 });
            var text = await File.ReadAllTextAsync(Path.Combine(directory, "workspace-state.json"));
            Check(text.Contains("\"referenceBubbleWidth\":610") && text.Contains("\"referenceBubbleHeight\":380"), "the saved file names the bubble's size: " + text);
            var restored = await new StateStore(directory).LoadAsync();
            Check(restored.ReferenceBubbleWidth == 610 && restored.ReferenceBubbleHeight == 380, "a reload restores the bubble's size");
            // Its corner's double click: back to none.
            await store.SaveAsync(restored with { ReferenceBubbleWidth = null, ReferenceBubbleHeight = null });
            restored = await new StateStore(directory).LoadAsync();
            Check(restored.ReferenceBubbleWidth is null && restored.ReferenceBubbleHeight is null, "a cleared size stays cleared");
            // A damaged size reads as none; the panes saved beside it all load.
            var workspace = store.ApproveLocal(directory);
            await store.SaveAsync(restored with { Workspaces = [workspace], Sessions = [new RunSession { Id = "bubble-pane", WorkspaceId = workspace.Id, Title = "kept" }] });
            var path = Path.Combine(directory, "workspace-state.json");
            var saved = await File.ReadAllTextAsync(path);
            foreach (var (width, height) in new[] { ("\"abc\"", "true"), ("{}", "[]"), ("null", "\"380\""), ("false", "{\"h\":1}") })
            {
                var damaged = saved[..saved.LastIndexOf('}')] + ",\"referenceBubbleWidth\":" + width + ",\"referenceBubbleHeight\":" + height + "}";
                await File.WriteAllTextAsync(path, damaged);
                var loaded = await new StateStore(directory).LoadAsync();
                Check(loaded.ReferenceBubbleWidth is null && loaded.ReferenceBubbleHeight is null && loaded.Sessions.SingleOrDefault()?.Id == "bubble-pane", $"a damaged bubble size ({width}, {height}) loads as none and keeps every pane: {string.Join(",", loaded.Sessions.Select(s => s.Id))}");
            }
        }
        finally { Directory.Delete(directory, true); }
    }
}
