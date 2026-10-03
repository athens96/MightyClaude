using MightyClaude.Core;

/// <summary>
/// A new Mighty result shows right above the composer on Windows: the newest
/// result card is as tall as its content up to the saved size (or the window
/// fit), both kept within the visible pane, and never writes that maximum
/// itself; when a request of the pane finishes while the diagram watches, the
/// camera scrolls once so the card's bottom sits 16pt above the composer.
/// Mirrors the "Size", "Camera" and "When" cases of
/// native/macos/Tests/MightyCoreTests/MightyGraphResultFitTests.swift.
/// </summary>
internal static class ResultRevealVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static bool Near(double a, double b) => Math.Abs(a - b) < 0.001;

    private static MightyGraphRun Run(string id, string status = "completed") =>
        new() { Id = id, Input = "요청 " + id, Status = status, FinalOutput = status == "completed" ? "결과 " + id : null };
    private static string ResultID(string run) => MightyGraphBlockSize.NodeId(run, "result");
    private static GraphRect? Frame(MightyGraphLayout layout, string id) => layout.Nodes.FirstOrDefault(n => n.Id == id)?.Frame;
    private static (double W, double H)? Size(MightyGraphLayout layout, string id) => Frame(layout, id) is { } f ? (f.W, f.H) : null;

    private static MightyGraphLayout Layout(IReadOnlyList<MightyGraphRun> runs, (double W, double H)? viewport, double? content,
        GraphBlockSize? saved = null, double? zoom = null) =>
        MightyGraphViewModel.CanvasLayout(runs, "", false, new HashSet<string>(), null, viewport, zoom, saved, resultContentHeight: content);

    // ── size ──────────────────────────────────────────────────────────────────

    internal static Task TheNewestResultIsAsTallAsItsContentUpToItsCap()
    {
        Check(MightyGraphLayout.ResultSize((1_100, 800), 260) == (1_100, 260), "a short answer shrinks below the saved size");
        Check(MightyGraphLayout.ResultSize((700, 350), 2_000) == (700, 350), "a long answer stops at the saved size");
        Check(MightyGraphLayout.ResultSize((700, 350), 60).H == MightyGraphLayout.MinimumResultHeight, "a tiny answer keeps the minimum height");
        Check(MightyGraphLayout.ResultSize((700, 350), 0).H == MightyGraphLayout.MinimumResultHeight, "an empty answer keeps the minimum height");
        foreach (var broken in new double?[] { null, double.NaN, double.PositiveInfinity, -5 })
            Check(MightyGraphLayout.ResultSize((700, 350), broken) == (700, 350), "unmeasured or broken content takes the cap: " + broken);
        Check(MightyGraphLayout.ResultSize((700, 900), 300.2).H == 301, "fractional content rounds up so the last line is never clipped");
        Check(MightyGraphLayout.MinimumResultHeight == MightyGraphBlockSize.MinimumHeight, "the result minimum is the block minimum");
        return Task.CompletedTask;
    }

    internal static Task TheSavedSizeIsAMaximumForEveryNewResult()
    {
        var viewport = (1_200.0, 800.0);
        var saved = new GraphBlockSize(900, 600);
        Check(Size(Layout([Run("one")], viewport, 180, saved), ResultID("one")) == (900, 180), "a short answer is as tall as its content at the saved width");
        Check(Size(Layout([Run("one"), Run("two")], viewport, 1_500, saved), ResultID("two")) == (900, 600), "a long answer is the saved size, scrolling inside");
        // Nothing saved: the window fit is the maximum.
        var fitted = Layout([Run("one")], viewport, 240);
        Check(Size(fitted, ResultID("one")) == (1_152, 240) && fitted.FittedResultID == ResultID("one"), "with nothing saved the window fit is the maximum");
        Check(Size(Layout([Run("one")], viewport, 5_000), ResultID("one")) == (1_152, 752), "a long answer stops at the window fit");
        return Task.CompletedTask;
    }

    internal static Task TheCapIsTheVisiblePane()
    {
        // A saved size larger than a narrow pane: the pane bounds the cap, the content fits under it.
        var saved = new GraphBlockSize(900, 700);
        Check(Size(Layout([Run("one")], (600, 400), 200, saved, zoom: 1), ResultID("one")) == (552, 200), "a short answer fits under the pane-bound cap");
        Check(Size(Layout([Run("one")], (600, 400), 5_000, saved, zoom: 1), ResultID("one")) == (552, 352), "a long answer stops at the visible pane");
        // At 150% the pane is smaller in diagram coordinates.
        var zoomed = Size(Layout([Run("one")], (900, 700), 5_000, saved, zoom: 1.5), ResultID("one"));
        Check(zoomed is { } z && Near(z.W, 568) && Near(z.H, 652.0 / 1.5), "zoomed in the cap is the pane at that zoom: " + zoomed);
        return Task.CompletedTask;
    }

    internal static Task ShrinkingToContentNeverWritesTheSavedSize()
    {
        var saved = new GraphBlockSize(900, 700);
        var viewport = (1_200.0, 800.0);
        // The card shows 180 tall (its content) under the saved 700 maximum.
        var shown = Size(Layout([Run("one")], viewport, 180, saved, zoom: 1), ResultID("one"))!.Value;
        Check(shown == (900, 180), "the card shows its content height");
        var layout = Layout([Run("one")], viewport, 180, saved, zoom: 1);
        (GraphBlockSize? Live, GraphBlockSize? Save) Release((double W, double H) dragged)
        {
            var sides = MightyGraphLayout.ResultDragSides(shown, dragged);
            return MightyGraphLayout.ResultDrag(dragged, sides.Horizontal, sides.Vertical, MightyGraphLayout.ResizePhase.Finished, saved, layout.ResultLimit, layout.ResultWindowFit);
        }
        // A press and release on the grip without moving keeps the maximum.
        Check(Release(shown).Save == saved, "a click on the grip saves the saved size, not the shrunk one");
        // Only the width moved: the height keeps the maximum, never the shown 180.
        Check(Release((1_000, 180)).Save == new GraphBlockSize(1_000, 700), "a width-only drag keeps the maximum height");
        // The height moved: that is the user's own choice.
        Check(Release((900, 320)).Save == new GraphBlockSize(900, 320), "a height drag saves what it was released at");
        Check(MightyGraphLayout.ResultDragSides((900, 180), (900.2, 180.3)) == (false, false), "sub-pixel jitter moves no side");
        // Cancelled saves nothing; a layout pass never changes what was saved.
        Check(MightyGraphLayout.ResultDrag((900, 180), true, true, MightyGraphLayout.ResizePhase.Cancelled, saved, layout.ResultLimit, layout.ResultWindowFit) is (null, null), "a cancelled drag saves nothing");
        Check(saved == new GraphBlockSize(900, 700), "laying out a shrunk card leaves the saved size alone");
        return Task.CompletedTask;
    }

    internal static Task AShorterResultMovesNothingAboveItAndPullsTheDraftUp()
    {
        var viewport = (1_200.0, 800.0);
        var full = Layout([Run("one")], viewport, null);
        var fitted = Layout([Run("one")], viewport, 200);
        var request = MightyGraphBlockSize.NodeId("one", "request");
        Check(Frame(full, request) == Frame(fitted, request), "the request above does not move");
        Check(Frame(full, ResultID("one"))!.Value.Y == Frame(fitted, ResultID("one"))!.Value.Y, "the result's top does not move");
        var lift = Frame(full, MightyGraphCamera.PendingNodeID)!.Value.Y - Frame(fitted, MightyGraphCamera.PendingNodeID)!.Value.Y;
        Check(Near(lift, 752 - 200), "the draft below moves up by what the result gave back: " + lift);
        // Older results and a layout without a viewport ignore the measurement.
        var two = Layout([Run("one"), Run("two")], viewport, 160);
        Check(Size(two, ResultID("one")) == (500, 200), "an older result keeps its default size");
        Check(Size(Layout([Run("one")], null, 160), ResultID("one")) == (500, 200), "no viewport, no measurement");
        return Task.CompletedTask;
    }

    // ── camera ────────────────────────────────────────────────────────────────

    internal static Task ANewResultSitsRightAboveTheComposer()
    {
        var viewport = (W: 900.0, H: 700.0);
        var card = new GraphRect(24, 1_800, 500, 300);
        foreach (var zoom in new[] { 0.5, 1, 1.5 })
        {
            var offset = MightyGraphCamera.RevealOffset(card, viewport, zoom);
            Check(Near(offset.Y + card.MaxY * zoom, viewport.H - 16), "the card's bottom lands 16pt above the composer at " + zoom);
            Check(offset.X == MightyGraphCamera.CameraOffset(card, viewport, zoom, alignTop: true).X, "it is centred as every re-aim centres it");
        }
        // Taller than the viewport: its top shows.
        var tall = new GraphRect(24, 1_800, 500, 900);
        var top = MightyGraphCamera.RevealOffset(tall, viewport, 1);
        Check(top == MightyGraphCamera.CameraOffset(tall, viewport, 1, alignTop: true) && Near(top.Y + tall.Y, 16), "a card taller than the viewport shows its top");
        var snug = new GraphRect(24, 400, 500, 668);
        Check(Near(MightyGraphCamera.RevealOffset(snug, viewport, 1).Y + snug.Y, 16), "exactly the viewport less both margins: top and bottom agree");
        // Align-bottom wins over align-top.
        var small = new GraphRect(24, 600, 500, 200);
        Check(MightyGraphCamera.CameraOffset(small, viewport, 1, alignTop: true, alignBottom: true) == MightyGraphCamera.RevealOffset(small, viewport, 1), "align-bottom wins");
        Check(MightyGraphCamera.CameraOffset(small, viewport, 1, alignTop: true, alignBottom: false) == MightyGraphCamera.CameraOffset(small, viewport, 1, alignTop: true), "without it align-top holds");
        return Task.CompletedTask;
    }

    // ── when ──────────────────────────────────────────────────────────────────

    private static List<MightyGraphCamera.ResultReveal.RunProgress> Progress(params (string Id, bool Finished)[] pairs) =>
        pairs.Select(p => new MightyGraphCamera.ResultReveal.RunProgress(p.Id, p.Finished)).ToList();
    private static string? Change(MightyGraphCamera.ResultReveal reveal, List<MightyGraphCamera.ResultReveal.RunProgress> previous,
        List<MightyGraphCamera.ResultReveal.RunProgress> current, bool measured = true) =>
        reveal.RunsChanged(previous, current, ResultID, _ => measured);

    internal static Task ARequestThatFinishesWhileWatchedIsRevealedOnce()
    {
        // Windows: the pane runs, then its finished diagram arrives.
        var reveal = new MightyGraphCamera.ResultReveal();
        reveal.PaneRunning(false, true);
        Check(Change(reveal, Progress(), Progress(("one", true))) == ResultID("one") && reveal.HoldingID == ResultID("one"), "the pane's own finished request is revealed");
        reveal.PaneRunning(true, false);
        Check(Change(reveal, Progress(("one", true)), Progress(("one", true))) == null, "redraws with the same runs reveal nothing again");
        // The next request's result is a new one.
        reveal.PaneRunning(false, true);
        Check(reveal.HoldingID is null, "a new request ends the hold");
        Check(Change(reveal, Progress(("one", true)), Progress(("one", true), ("two", true))) == ResultID("two"), "the next result is revealed");
        // The macOS shape, a run seen unfinished first, qualifies too.
        var mac = new MightyGraphCamera.ResultReveal();
        Check(Change(mac, Progress(("one", false)), Progress(("one", true))) == ResultID("one"), "a run seen unfinished then finished is revealed");
        Check(Change(mac, Progress(("one", true)), Progress(("one", true), ("two", false))) == null, "a request starting is not a result");
        return Task.CompletedTask;
    }

    internal static Task RunsAPaneOpensOrHydratesWithAreNeverRevealed()
    {
        var reveal = new MightyGraphCamera.ResultReveal();
        Check(Change(reveal, Progress(), Progress(("one", true), ("two", true))) == null, "launch or restore reveals nothing");
        Check(Change(reveal, Progress(("one", true)), Progress(("one", true), ("two", true))) == null, "a run that appeared finished with nothing in flight");
        Check(Change(reveal, Progress(), Progress()) == null && reveal.HoldingID is null, "nothing held");
        // A request in flight does not make an older run that appears the new result.
        reveal.PaneRunning(false, true);
        Check(Change(reveal, Progress(("two", true)), Progress(("one", true), ("two", true))) == null, "history loaded above is never revealed");
        // The newest result moving back to an earlier run.
        var back = new MightyGraphCamera.ResultReveal();
        Check(Change(back, Progress(("one", true), ("two", true)), Progress(("one", true), ("two", false))) == null, "a resumed newest run");
        Check(Change(back, Progress(("one", true), ("two", true)), Progress(("one", true))) == null && back.HoldingID is null, "a removed newest run");
        Check(MightyGraphCamera.ResultReveal.FinishedRunID(Progress(("a", false)), Progress(("a", true))) == "a"
            && MightyGraphCamera.ResultReveal.FinishedRunID(Progress(), Progress(("a", true))) is null
            && MightyGraphCamera.ResultReveal.FinishedRunID(Progress(), Progress(("a", true)), requestInFlight: true) == "a", "the run that just finished");
        return Task.CompletedTask;
    }

    internal static Task AnUnmeasuredCardIsHeldUntilItsHeightArrives()
    {
        var reveal = new MightyGraphCamera.ResultReveal();
        reveal.PaneRunning(false, true);
        Check(Change(reveal, Progress(), Progress(("one", true)), measured: false) is null, "no camera move before the card is measured");
        Check(reveal.HoldingID == ResultID("one") && reveal.AwaitingMeasure, "held, awaiting its height");
        Check(reveal.ViewportChanged() is null, "a resize does not place a card still awaiting its height");
        Check(reveal.ContentMeasured(ResultID("one")) == ResultID("one") && !reveal.AwaitingMeasure, "its height places it");
        Check(reveal.MeasureTimedOut(ResultID("one")) is null, "a late timeout does nothing");
        Check(reveal.ViewportChanged() == ResultID("one"), "a resize places it again");
        Check(reveal.ContentMeasured(ResultID("zero")) is null, "another card's height does nothing");
        // Never measured: placed at its cap on timeout, then again when measured.
        var never = new MightyGraphCamera.ResultReveal();
        never.PaneRunning(false, true);
        _ = Change(never, Progress(), Progress(("one", true)), measured: false);
        Check(never.MeasureTimedOut(ResultID("zero")) is null && never.MeasureTimedOut(ResultID("one")) == ResultID("one") && never.MeasureTimedOut(ResultID("one")) is null, "the timeout places it once");
        Check(never.ContentMeasured(ResultID("one")) == ResultID("one"), "its measurement places it again");
        return Task.CompletedTask;
    }

    internal static Task TheUsersOwnScrollZoomOrDraftEndsTheHold()
    {
        var reveal = new MightyGraphCamera.ResultReveal();
        reveal.PaneRunning(false, true);
        _ = Change(reveal, Progress(), Progress(("one", true)), measured: false);
        reveal.Cancel();
        Check(reveal.HoldingID is null && !reveal.AwaitingMeasure, "a scroll, drag or zoom ends the hold");
        Check(reveal.ContentMeasured(ResultID("one")) is null && reveal.MeasureTimedOut(ResultID("one")) is null && reveal.ViewportChanged() is null, "nothing places it after");
        reveal.PaneRunning(false, true);
        Check(Change(reveal, Progress(("one", true)), Progress(("one", true), ("two", true))) == ResultID("two"), "a later result is revealed again");
        // Typing a draft ends it; clearing one does not.
        reveal.DraftChanged(wasEmpty: false, isEmpty: true);
        Check(reveal.HoldingID == ResultID("two"), "clearing the draft keeps the hold");
        reveal.DraftChanged(wasEmpty: true, isEmpty: true);
        Check(reveal.HoldingID == ResultID("two"), "an empty draft keeps the hold");
        reveal.DraftChanged(wasEmpty: true, isEmpty: false);
        Check(reveal.HoldingID is null, "typing a draft ends the hold");
        return Task.CompletedTask;
    }
}
