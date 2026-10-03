using System.Text.Json;
using MightyClaude.Core;

/// <summary>
/// The Mighty result box fits the agent pane on Windows: the newest result card
/// shrinks with the pane (at any zoom, leaving room for the result files panel)
/// and grows back to the size the user dragged it to; a drag stops at the pane
/// edge and saves only the sides it moved inside the pane. Mirrors the "Kept
/// within the pane" and "Dragging the newest result" cases of
/// native/macos/Tests/MightyCoreTests/MightyGraphResultFitTests.swift.
/// </summary>
internal static class ResultFitVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static bool Near(double a, double b) => Math.Abs(a - b) < 0.001;

    private static MightyGraphRun Run(string id) =>
        new() { Id = id, Input = "요청 " + id, Status = "completed", FinalOutput = "결과 " + id };
    private static string ResultID(string run) => MightyGraphBlockSize.NodeId(run, "result");

    private static MightyGraphLayout Layout((double W, double H) viewport, double? zoom, GraphBlockSize? saved, bool files = false) =>
        MightyGraphViewModel.CanvasLayout([Run("one")], "", false, new HashSet<string>(), files ? "one" : null, viewport, zoom, saved);

    private static (double W, double H) Latest(MightyGraphLayout layout)
    {
        var frame = layout.Nodes.Single(n => n.Id == ResultID("one")).Frame;
        return (frame.W, frame.H);
    }

    private static readonly (double W, double H) Narrow = (600, 400);
    private static readonly GraphBlockSize BigSaved = new(900, 700);

    internal static Task ASavedSizeLargerThanThePaneIsKeptWithinIt()
    {
        Check(Latest(Layout(Narrow, 1, BigSaved)) == (552, 352), "a saved size larger than the pane is the pane less its margins");
        // Only one side too big: only that side shrinks.
        Check(Latest(Layout((1_200, 400), 1, BigSaved)) == (900, 352), "only the side the pane bounds shrinks");
        // No zoom given (the parity vectors): the saved size whole.
        Check(Latest(Layout(Narrow, null, BigSaved)) == (900, 700), "without a zoom the saved size stays whole");
        return Task.CompletedTask;
    }

    internal static Task TheCardShrinksWithThePaneAndGrowsBackToTheSavedSize()
    {
        var saved = new GraphBlockSize(900, 600);
        var wide = (1_200.0, 800.0);
        Check(Latest(Layout(wide, 1, saved)) == (900, 600), "a roomy pane shows the saved size");
        Check(Latest(Layout((500, 300), 1, saved)) == (452, 252), "a small pane shrinks the card");
        Check(Latest(Layout((800, 500), 1, saved)) == (752, 452), "a medium pane shrinks it less");
        // The saved size was never changed, so the card is back at it.
        Check(Latest(Layout(wide, 1, saved)) == (900, 600), "the card grows back to its saved size");
        // The window fit shrinks with a narrow pane the same way, below its own 500 × 200.
        Check(Latest(Layout((420, 230), 1, null)) == (372, 182), "the window fit shrinks with a narrow pane");
        Check(Latest(Layout(wide, 1, null)) == (1_152, 752), "the window fit fills a wide pane");
        return Task.CompletedTask;
    }

    internal static Task TheLimitIsInDiagramCoordinatesAtTheZoom()
    {
        var pane = (W: 900.0, H: 700.0);
        var saved = new GraphBlockSize(1_100, 900);
        Check(Latest(Layout(pane, 0.5, saved)) == (1_100, 900), "zoomed out, the saved size fits");
        var zoomedIn = Latest(Layout(pane, 1.5, saved));
        Check(Near(zoomedIn.W, 568) && Near(zoomedIn.H, 652 / 1.5), "zoomed in, (900 − 48) / 1.5 × (700 − 48) / 1.5");
        Check(Latest(Layout(pane, 0.5, null)) == (852, 652), "zoomed out, the window fit is the 100% fit");
        Check(Latest(Layout(pane, 1.5, null)) == zoomedIn, "zoomed in, the window fit is drawn inside the pane too");
        foreach (var zoom in new[] { 0.5, 1, 1.5 })
            foreach (var size in new[] { saved, null })
            {
                var card = Latest(Layout(pane, zoom, size));
                Check(card.W * zoom <= pane.W - 48 + 0.001 && card.H * zoom <= pane.H - 48 + 0.001, $"the card stays inside the pane at {zoom}");
            }
        return Task.CompletedTask;
    }

    internal static Task ATinyPaneKeepsTheBlockMinimum()
    {
        var minimum = (MightyGraphBlockSize.MinimumWidth, MightyGraphBlockSize.MinimumHeight);
        Check(MightyGraphLayout.ResultViewportLimit((200, 100), 1, false) == minimum, "a tiny pane keeps the block minimum");
        Check(MightyGraphLayout.ResultViewportLimit((400, 220), 1.5, false) == minimum, "a tiny zoomed pane keeps the block minimum");
        Check(Latest(Layout((200, 100), 1, BigSaved)) == minimum, "the card keeps the block minimum");
        // The files panel beside the card takes its share of the width, down to the minimum.
        Check(Latest(Layout((1_000, 600), 1, BigSaved, files: true)).W == 1_000 - 48 - 336, "the files panel takes its width");
        Check(Latest(Layout((600, 600), 1, BigSaved, files: true)).W == MightyGraphBlockSize.MinimumWidth, "down to the minimum");
        foreach (var zoom in new[] { 0, -1, double.NaN, double.PositiveInfinity })
            Check(MightyGraphLayout.ResultViewportLimit((600, 400), zoom, false) == (552, 352), "a broken zoom counts as 100%");
        return Task.CompletedTask;
    }

    private static (GraphBlockSize? Live, GraphBlockSize? Save) Drag((double W, double H) dragged, bool horizontal, bool vertical,
        MightyGraphLayout.ResizePhase phase, MightyGraphLayout on, GraphBlockSize? saved) =>
        MightyGraphLayout.ResultDrag(dragged, horizontal, vertical, phase, saved, on.ResultLimit, on.ResultWindowFit);

    internal static Task TheLayoutCarriesTheLimitsADragUses()
    {
        var graph = Layout(Narrow, 1, BigSaved);
        Check(graph.ResultLimit == (552, 352) && graph.ResultWindowFit == (552, 352), "the layout carries the limit and the fit");
        var wide = Layout((1_200, 800), 1.5, BigSaved, files: true);
        Check(wide.ResultLimit == MightyGraphLayout.ResultViewportLimit((1_200, 800), 1.5, true), "the limit counts the files panel and zoom");
        Check(wide.ResultWindowFit == MightyGraphLayout.ResultFitSize((1_200, 800), true), "the fit counts the files panel");
        Check(Layout(Narrow, null, BigSaved).ResultLimit is null, "no zoom, no limit");
        return Task.CompletedTask;
    }

    internal static Task ALiveDragFollowsTheCursorWithinThePaneOnly()
    {
        var graph = Layout(Narrow, 1, BigSaved);
        var live = MightyGraphLayout.ResizePhase.Live;
        Check(Drag((480, 300), true, true, live, graph, BigSaved).Live == new GraphBlockSize(480, 300), "inside the pane: the dragged size");
        Check(Drag((1_000, 300), true, true, live, graph, BigSaved).Live == new GraphBlockSize(552, 300), "past it: stops at the pane");
        var pushed = Drag((1_000, 900), true, true, live, graph, BigSaved);
        Check(pushed.Live == new GraphBlockSize(552, 352) && pushed.Save is null, "a live drag saves nothing");
        Check(Latest(Layout(Narrow, 1, pushed.Live)) == (552, 352), "the live size laid out is the card shown");
        Check(MightyGraphLayout.ResultDrag((1_000, 900), true, true, live, null, null, null).Live == new GraphBlockSize(1_000, 900),
            "without a limit the cursor is followed");
        return Task.CompletedTask;
    }

    internal static Task ReleasingSavesOnlyTheSidesMovedInsideThePane()
    {
        var finished = MightyGraphLayout.ResizePhase.Finished;
        var graph = Layout(Narrow, 1, BigSaved);
        // A corner drag that only changes the height keeps the saved width.
        var released = Drag((552, 300), true, true, finished, graph, BigSaved);
        Check(released.Live is null && released.Save == new GraphBlockSize(900, 300), "dx = 0 at the pane edge keeps the saved width");
        Check(Latest(Layout((1_200, 800), 1, released.Save)).W == 900, "grown back, the card is the saved width again");
        // Pushing past the pane keeps the larger saved size.
        Check(Drag((1_000, 900), true, true, finished, graph, BigSaved).Save == BigSaved, "pushing past the pane keeps the saved size");
        var small = new GraphBlockSize(400, 250);
        Check(Drag((800, 600), true, true, finished, Layout(Narrow, 1, small), small).Save == new GraphBlockSize(552, 352),
            "a smaller saved size pushed past the pane saves the pane's size");
        Check(Drag((900, 600), true, true, finished, Layout((420, 230), 1, null), null).Save == new GraphBlockSize(500, 200),
            "nothing saved and a narrow pane: the fit, not the shrunk size");
        // Inside the pane: what it was released at, side by side.
        Check(Drag((500, 300), true, true, finished, graph, BigSaved).Save == new GraphBlockSize(500, 300), "released inside the pane");
        Check(Drag((500, 352), true, false, finished, graph, BigSaved).Save == new GraphBlockSize(500, 700), "a right-side drag keeps the saved height");
        Check(Drag((552, 300), false, true, finished, graph, BigSaved).Save == new GraphBlockSize(900, 300), "a bottom drag keeps the saved width");
        var zoomed = Layout((900, 700), 1.5, BigSaved);
        Check(Drag((568, 300), true, true, finished, zoomed, BigSaved).Save == new GraphBlockSize(900, 300), "zoomed in, the limit is 568 wide");
        Check(Drag((520, 300), true, true, finished, zoomed, BigSaved).Save == new GraphBlockSize(520, 300), "zoomed in, inside the limit");
        // Cancelled drags save nothing; saves are clamped like every block size.
        Check(Drag((900, 180), true, true, MightyGraphLayout.ResizePhase.Cancelled, graph, BigSaved) == (null, null), "a cancelled drag saves nothing");
        Check(MightyGraphLayout.ResultDrag((100, 5_000), true, true, finished, null, null, (1_152, 752)).Save == new GraphBlockSize(300, 1_200),
            "saves are clamped to the block bounds");
        return Task.CompletedTask;
    }

    internal static Task ASavedCardThePaneBoundsFollowsThePaneOnResize()
    {
        var bound = Layout(Narrow, 1, BigSaved);
        Check(bound.FittedResultID is null && bound.ViewportBoundResultID == ResultID("one"), "a bounded saved card follows the pane");
        var frames = bound.Nodes.ToDictionary(n => n.Id, n => n.Frame);
        var anchor = MightyGraphCamera.ResizeAnchor(bound.FittedResultID ?? bound.ViewportBoundResultID, MightyGraphBlockSize.NodeId("one", "request"), false, frames);
        Check(anchor.NodeID == ResultID("one") && anchor.AlignTop, "resizing keeps a bounded card's top in view");
        Check(Layout((1_200, 800), 1, BigSaved).ViewportBoundResultID is null, "a card within the pane is not bounded");
        Check(Layout((1_200, 400), 1, BigSaved).ViewportBoundResultID == ResultID("one"), "bounded in one direction is enough");
        Check(Layout(Narrow, null, BigSaved).ViewportBoundResultID is null, "no zoom never bounds");
        var fitted = Layout((900, 700), 1.5, null);
        Check(fitted.FittedResultID == ResultID("one") && fitted.ViewportBoundResultID == ResultID("one"), "a bounded fitted card is reported both ways");
        return Task.CompletedTask;
    }

    /// The saved size uses the macOS field name, survives a restart clamped to
    /// the block bounds, and a shell pane or a missing value keeps none.
    internal static async Task TheSavedSizeIsKeptUnderTheMacField()
    {
        // The way back to the window fit uses the shared label.
        Check(MightyGraphViewModel.LocaleKeyResultFitToWindow == "graph.result.fitToWindow"
            && Locale.Get(MightyGraphViewModel.LocaleKeyResultFitToWindow) == "창에 맞추기", "the fit button is graph.result.fitToWindow");
        var workspace = new Workspace { Path = Path.GetTempPath() };
        var session = new RunSession { WorkspaceId = workspace.Id, Provider = "claude", AgentViewMode = "mighty", GraphResultSize = new(5_000, 700) };
        var shell = new RunSession { WorkspaceId = workspace.Id, Kind = "shell", GraphResultSize = new(900, 700) };
        var plain = new RunSession { WorkspaceId = workspace.Id, Provider = "claude" };
        var encoded = JsonSerializer.Serialize(new AppSnapshot { Version = 1, Workspaces = [workspace], Sessions = [session, shell, plain] }, Wire.Json);
        using (var document = JsonDocument.Parse(encoded))
        {
            var saved = document.RootElement.GetProperty("sessions")[0].GetProperty("graphResultSize");
            Check(saved.GetProperty("width").GetDouble() == 5_000 && saved.GetProperty("height").GetDouble() == 700, "graphResultSize is {width,height}");
            Check(!document.RootElement.GetProperty("sessions")[2].TryGetProperty("graphResultSize", out _), "no saved size writes nothing");
        }
        var directory = Verification.Temp();
        try
        {
            await StateStore.AtomicWriteAsync(Path.Combine(directory, "workspace-state.json"), System.Text.Encoding.UTF8.GetBytes(encoded));
            var loaded = await new StateStore(directory).LoadAsync();
            Check(loaded.Sessions.Single(s => s.Id == session.Id).GraphResultSize == new GraphBlockSize(1_400, 700), "the saved size survives clamped");
            Check(loaded.Sessions.Single(s => s.Id == shell.Id).GraphResultSize is null, "a shell pane keeps no result size");
            Check(loaded.Sessions.Single(s => s.Id == plain.Id).GraphResultSize is null, "no saved size loads as none");
        }
        finally { Directory.Delete(directory, true); }
    }
}
