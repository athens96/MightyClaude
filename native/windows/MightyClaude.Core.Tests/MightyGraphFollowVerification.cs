using MightyClaude.Core;

/// The diagram's follow mode (MightyGraphFollow; macOS MightyGraphFollowTests): the newest block's bottom stays
/// right above the composer while the reader is there, and a reader who moved away is left where they are.
internal static class MightyGraphFollowVerification
{
    private static readonly (double W, double H) Viewport = (900, 600);
    private static void Check(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
    private static string Id(string run, string suffix) => MightyGraphBlockSize.NodeId(run, suffix);
    private static MightyGraphAgent Agent(string id, string? parent = null, string status = "running") => new() { Id = id, ParentID = parent, Title = id, Status = status };
    private static MightyGraphRun Run(string id, string status, params MightyGraphAgent[] agents) => new() { Id = id, Input = "request " + id, Status = status, Agents = [.. agents] };
    private static MightyGraphLayout Layout(IReadOnlyList<MightyGraphRun> runs, string draft = "", bool running = true) =>
        MightyGraphLayout.Make(runs, draft, running, new HashSet<string>(), splitsExecution: true);
    private static GraphRect Frame(MightyGraphLayout layout, string? id) => layout.Nodes.Single(n => n.Id == id).Frame;
    /// Where the block's bottom is on screen under the camera.
    private static double Bottom(GraphRect frame, (double X, double Y) camera, double zoom = 1) => camera.Y + frame.MaxY * zoom;

    internal static Task TypingADraftNeverChangesWhatTheCameraFollows()
    {
        // Symptom: pressing a key or Enter jumped the view. The draft block came and went with the composer's text.
        MightyGraphRun[] runs = [Run("a", "running")];
        var (idle, typing) = (Layout(runs), Layout(runs, draft: "다음 요청"));
        Check(typing.Nodes.Any(n => n.Kind == "draft"), "a typed draft has its block");
        Check(MightyGraphFollow.TargetID(typing, runs) == Id("a", MightyGraphLayout.WorkSuffix) && MightyGraphFollow.TargetID(idle, runs) == MightyGraphFollow.TargetID(typing, runs),
            "the 실행 block is followed with or without a draft");
        // A finished pane always shows the draft block below its result; the result is followed.
        MightyGraphRun[] done = [Run("a", "completed")];
        Check(MightyGraphFollow.TargetID(Layout(done, running: false), done) == Id("a", "result") && MightyGraphFollow.TargetID(Layout(done, "x", false), done) == Id("a", "result"),
            "the result is followed, never the draft under it");
        return Task.CompletedTask;
    }

    internal static Task TheFollowedBlockIsTheNewestRequestsLowestBlock()
    {
        // Symptom: a new sub-agent or result never moved the view.
        MightyGraphRun[] running = [Run("old", "completed"), Run("a", "running", Agent("one"), Agent("deep", "one"))];
        Check(MightyGraphFollow.TargetID(Layout(running), running) == Id("a", "agent:deep"), "the deepest sub-agent");
        MightyGraphRun[] finished = [running[0], Run("a", "completed", Agent("one", status: "completed"), Agent("deep", "one", "completed"))];
        Check(MightyGraphFollow.TargetID(Layout(finished, running: false), finished) == Id("a", "result"), "then the result");
        // Siblings in one row: the one added last.
        MightyGraphRun[] siblings = [Run("a", "running", Agent("left"), Agent("right"))];
        Check(MightyGraphFollow.TargetID(Layout(siblings), siblings) == Id("a", "agent:right"), "of one row the block added last");
        // No request yet: the draft block is all there is.
        Check(MightyGraphFollow.TargetID(Layout([], running: false), []) == MightyGraphCamera.PendingNodeID, "with no request the draft block");
        // A plan waiting for an answer is in the flow; an answered plan hung beside it is not.
        MightyGraphRun[] planning = [Run("a", "running")];
        var plan = MightyGraphLayout.Make(planning, "", true, new HashSet<string>(), planRunID: "a", planRecords: [("a", "earlier")], splitsExecution: true);
        Check(plan.Nodes.Any(n => n.Kind == "planRecord") && MightyGraphFollow.TargetID(plan, planning) == Id("a", MightyGraphLayout.PlanSuffix), "the waiting plan, not the answered one beside the flow");
        return Task.CompletedTask;
    }

    internal static Task ASendPutsTheNewRequestsBottomJustAboveTheComposer()
    {
        // Symptom: a sent request was put at the top of the canvas, its 실행 block and everything after it far below.
        MightyGraphRun[] runs = [Run("old", "completed"), Run("new", "running")];
        var graph = Layout(runs);
        var target = MightyGraphFollow.TargetID(graph, runs);
        Check(target == Id("new", MightyGraphLayout.WorkSuffix), "a sent request is followed to its 실행 block");
        var work = Frame(graph, target);
        foreach (var zoom in new[] { 1.0, 0.7 })
        {
            var camera = MightyGraphFollow.CameraOffset(work, Viewport, zoom, alignTop: false, alignBottom: false, follows: true);
            Check(Math.Abs(Bottom(work, camera, zoom) - (Viewport.H - MightyGraphFollow.Margin)) < 0.001 && camera.X == MightyGraphFollow.CentredX(Viewport, zoom),
                "its bottom 16pt above the composer on the centreline at " + zoom);
        }
        // Anything else keeps the shared rule.
        Check(MightyGraphFollow.CameraOffset(work, Viewport, 1, alignTop: true, alignBottom: false, follows: false) == MightyGraphCamera.CameraOffset(work, Viewport, 1, alignTop: true),
            "any other aim is the shared rule's");
        return Task.CompletedTask;
    }

    internal static Task AFollowingCameraPutsEachNewBlockRightAboveTheComposer()
    {
        var watch = new MightyGraphFollow.Watch();
        var work = new GraphRect(24, 200, 500, 280);
        var camera = MightyGraphFollow.Camera(work, Viewport, 1, x: 50);
        watch.Start();
        Check(watch.Observe("work", work, camera, Viewport, 1) is null, "placed, nothing moves");
        // A sub-agent appears below: its bottom goes where the 실행 block's was.
        var agent = new GraphRect(94, 512, 360, 240);
        var moved = watch.Observe("agent", agent, camera, Viewport, 1);
        Check(moved is { } to && Bottom(agent, to) == Viewport.H - MightyGraphFollow.Margin && to.X == camera.X, "a new block right above the composer, only up and down: " + moved);
        Check(watch.Observe("agent", agent, moved!.Value, Viewport, 1) is null && watch.Following, "and then it stays");
        return Task.CompletedTask;
    }

    internal static Task ATallerComposerKeepsTheFollowedBlockAboveIt()
    {
        // Symptom: typing a second line or pressing Enter re-aimed the camera.
        var watch = new MightyGraphFollow.Watch();
        var block = new GraphRect(24, 900, 500, 280);
        var camera = MightyGraphFollow.Camera(block, Viewport, 1, x: 0);
        watch.Start();
        watch.Observe("b", block, camera, Viewport, 1);
        var shorter = (Viewport.W, Viewport.H - 40);
        var kept = watch.Observe("b", block, camera, shorter, 1);
        Check(kept is { } k && Bottom(block, k) == shorter.Item2 - MightyGraphFollow.Margin && k.X == camera.X, "a shorter canvas keeps the block above the composer");
        // The composer shrinking back after the send gives the room back.
        Check(watch.Observe("b", block, kept!.Value, Viewport, 1) == camera, "and back");
        return Task.CompletedTask;
    }

    internal static Task AReaderWhoMovedAwayIsNeverPulledBack()
    {
        var watch = new MightyGraphFollow.Watch();
        var block = new GraphRect(24, 900, 500, 280);
        // Panned up: the block's bottom is 200pt below the composer.
        var up = (0.0, Viewport.H - MightyGraphFollow.Margin - block.MaxY + 200);
        Check(!MightyGraphFollow.IsAtNewest(block, up, Viewport, 1), "200pt behind the composer is not at the newest");
        watch.Moved("b", block, up, Viewport, 1);
        Check(!watch.Following, "a move away stops following");
        var next = new GraphRect(24, 1_212, 500, 200);
        Check(watch.Observe("c", next, up, Viewport, 1) is null && watch.Observe("c", next, up, (900, 500), 1) is null, "neither a new block nor a new viewport moves the reader");
        // Panned far past the newest block into empty canvas: not following either.
        Check(!MightyGraphFollow.IsAtNewest(block, (0, -block.MaxY - 50), Viewport, 1), "past the newest block is not at it");
        // A broken camera, viewport or zoom never counts as there.
        Check(!MightyGraphFollow.IsAtNewest(block, (0, double.NaN), Viewport, 1) && !MightyGraphFollow.IsAtNewest(block, up, (0, 600), 1) && !MightyGraphFollow.IsAtNewest(block, up, Viewport, 0), "broken input is never at the newest");
        return Task.CompletedTask;
    }

    internal static Task AReaderWhoScrolledUpBeforeALayoutLandedIsNotFollowedThroughIt()
    {
        // The reader scrolls up 120pt while the layout of a trim is still on its way, so the move is judged on the frames
        // they saw. The trim then moves every block up by thousands of points, and a new block appears; neither may take
        // the camera back to the newest block.
        var watch = new MightyGraphFollow.Watch();
        var viewport = (1_020.0, 611.0);
        var block = new GraphRect(24, 30_000, 360, 240);
        var aligned = MightyGraphFollow.Camera(block, viewport, 1, x: 236);
        watch.Start();
        Check(watch.Observe("b", block, aligned, viewport, 1) is null, "settled");
        var up = (aligned.X, aligned.Y + 120);
        watch.Moved("b", block, up, viewport, 1);
        Check(!watch.Following, "120pt up is away");
        var trimmed = block.OffsetBy(0, -10_112);
        Check(watch.Observe("b", trimmed, up, viewport, 1) is null, "a trim does not pull the reader back");
        Check(watch.Observe("late", new GraphRect(640, trimmed.Y, 360, 240), up, viewport, 1) is null && !watch.Following, "nor does a new block");
        return Task.CompletedTask;
    }

    internal static Task AReaderWhoComesBackToTheNewestBlockFollowsAgain()
    {
        var watch = new MightyGraphFollow.Watch();
        var block = new GraphRect(24, 900, 500, 280);
        watch.Stop();
        Check(!watch.Following, "stopped");
        // Within the slack below the composer still counts as there.
        var near = (0.0, Viewport.H - MightyGraphFollow.Margin - block.MaxY + MightyGraphFollow.Slack - 1);
        watch.Moved("b", block, near, Viewport, 1);
        Check(watch.Following && MightyGraphFollow.Slack == 36 && MightyGraphFollow.Margin == 16, "back within the slack follows again");
        // The block stays where the reader left it until a new one arrives.
        Check(watch.Observe("b", block, near, Viewport, 1) is null, "the block stays where the reader left it");
        var next = new GraphRect(24, 1_212, 500, 200);
        Check(watch.Observe("c", next, near, Viewport, 1) is { } moved && Bottom(next, moved) == Viewport.H - MightyGraphFollow.Margin, "a new block goes right above the composer");
        return Task.CompletedTask;
    }

    internal static Task AReaderBelowTheNewestBlockKeepsTheirPlaceWhileItSettles()
    {
        var watch = new MightyGraphFollow.Watch();
        var result = new GraphRect(24, 900, 500, 400);
        // The reader panned down: the result's bottom is mid-screen, over the draft block.
        var camera = (0.0, 300 - result.MaxY);
        watch.Moved("r", result, camera, Viewport, 1);
        Check(watch.Following, "the newest block's bottom on screen is at the newest");
        // Measured, the result is shorter: its bottom keeps its place on screen.
        var measured = new GraphRect(24, 900, 500, 250);
        Check(watch.Observe("r", measured, camera, Viewport, 1) is { } kept && Bottom(measured, kept) == 300, "a block settling to its height keeps its bottom where it was");
        return Task.CompletedTask;
    }

    internal static Task ATrimAboveKeepsTheFollowedBlockWhereItWas()
    {
        var watch = new MightyGraphFollow.Watch();
        var block = new GraphRect(24, 9_000, 500, 280);
        var camera = MightyGraphFollow.Camera(block, Viewport, 0.8, x: 10);
        watch.Start();
        watch.Observe("b", block, camera, Viewport, 0.8);
        var trimmed = block.OffsetBy(0, -6_000);
        Check(watch.Observe("b", trimmed, camera, Viewport, 0.8) is { } kept && Math.Abs(Bottom(trimmed, kept, 0.8) - (Viewport.H - MightyGraphFollow.Margin)) < 0.001, "a trim above keeps the followed block above the composer");
        return Task.CompletedTask;
    }

    internal static Task OpeningOrResizingABlockNeverMovesTheCameraAndKeepsFollowOnlyAtTheNewest()
    {
        var watch = new MightyGraphFollow.Watch();
        var block = new GraphRect(24, 900, 500, 280);
        var camera = MightyGraphFollow.Camera(block, Viewport, 1, x: 0);
        watch.Start();
        Check(watch.Observe("b", block, camera, Viewport, 1) is null, "settled");
        // Opened, the block is taller; its bottom is still in view: nothing moves, still following.
        watch.UserChangedLayout();
        Check(watch.Observe("b", new GraphRect(24, 900, 500, 300), camera, Viewport, 1) is null && watch.Following, "opened within the slack: nothing moves, still following");
        // Opened far past the composer: nothing moves, and following stops.
        watch.UserChangedLayout();
        Check(watch.Observe("b", new GraphRect(24, 900, 500, 700), camera, Viewport, 1) is null && !watch.Following, "opened past the composer: nothing moves, following stops");
        // A reader who is not following is not made to by opening a block.
        watch.UserChangedLayout();
        Check(watch.Observe("b", block, camera, Viewport, 1) is null && !watch.Following, "a layout change never starts following");
        return Task.CompletedTask;
    }

    internal static Task ABlockTallerThanTheViewportShowsItsBottom()
    {
        var tall = new GraphRect(24, 100, 500, 2_000);
        var camera = MightyGraphFollow.Camera(tall, Viewport, 1, x: 0);
        Check(Bottom(tall, camera) == Viewport.H - MightyGraphFollow.Margin && MightyGraphFollow.IsAtNewest(tall, camera, Viewport, 1), "a tall block shows its bottom");
        // Scrolled up inside it, past the slack: the reader is reading it.
        Check(!MightyGraphFollow.IsAtNewest(tall, (0, camera.Y + 400), Viewport, 1), "scrolled up inside it the reader is reading");
        return Task.CompletedTask;
    }

    internal static Task AnAimElsewhereStopsFollowingUntilTheNextSend()
    {
        var watch = new MightyGraphFollow.Watch();
        watch.Stop();
        var block = new GraphRect(24, 900, 500, 280);
        Check(watch.Observe("b", block, (0, 0), Viewport, 1) is null, "stopped, nothing follows");
        watch.Start();
        Check(watch.Observe("b", block, (0, 0), Viewport, 1) is { } placed && Bottom(block, placed) == Viewport.H - MightyGraphFollow.Margin && placed.X == 0, "a send follows again");
        return Task.CompletedTask;
    }

    internal static Task TheRunIdRulesFollowASendAndReaimATrimInEveryView()
    {
        // Symptom: a request sent from the 구조 view was followed only when the diagram next drew, over a block revealed since.
        Check(MightyGraphFollow.RunsChanged(null, ["a"], false) == MightyGraphFollow.RunChange.First && MightyGraphFollow.RunsChanged(null, [], true) == MightyGraphFollow.RunChange.First, "the first observation follows");
        Check(MightyGraphFollow.RunsChanged(["a", "b"], ["a", "b"], false) == MightyGraphFollow.RunChange.None && MightyGraphFollow.RunsChanged(["a", "b"], ["a", "b"], true) == MightyGraphFollow.RunChange.None,
            "the same list asks nothing, so a block revealed after it keeps the camera");
        // A send follows whether or not the camera did.
        Check(MightyGraphFollow.RunsChanged(["a"], ["a", "b"], false) == MightyGraphFollow.RunChange.Follow && MightyGraphFollow.RunsChanged([], ["a"], false) == MightyGraphFollow.RunChange.Follow
            && MightyGraphFollow.RunsChanged(["a", "b"], ["b", "c"], true) == MightyGraphFollow.RunChange.Follow, "a new last request is a send");
        // A trim, or older requests loaded above, re-aim a reader who is not following; a following camera keeps its block itself.
        Check(MightyGraphFollow.RunsChanged(["a", "b", "c"], ["b", "c"], false) == MightyGraphFollow.RunChange.Trim && MightyGraphFollow.RunsChanged(["b", "c"], ["z", "b", "c"], false) == MightyGraphFollow.RunChange.Trim
            && MightyGraphFollow.RunsChanged(["a"], [], false) == MightyGraphFollow.RunChange.Trim, "any other change is a trim");
        Check(MightyGraphFollow.RunsChanged(["a", "b", "c"], ["b", "c"], true) == MightyGraphFollow.RunChange.None && MightyGraphFollow.RunsChanged(["a"], [], true) == MightyGraphFollow.RunChange.None, "a following camera needs no re-aim");
        return Task.CompletedTask;
    }

    /// The camera rules the follow pass leans on: a trim under a reader who is not following, and the zoom's anchor.
    internal static Task OlderRequestsLoadedAboveAndAZoomKeepTheReadersPlace()
    {
        var nodes = new HashSet<string> { Id("b", "request"), Id("c", "request") };
        // Older requests loaded above the first one move nothing already placed (macOS MightyGraphCamera.trimAnchor).
        Check(MightyGraphCamera.TrimAnchor(["b", "c"], ["z", "a", "b", "c"], null, nodes).Equals(MightyGraphCamera.Anchor.Hold), "loading older requests holds");
        // The oldest dropped, the last unchanged: the reader is re-aimed.
        Check(MightyGraphCamera.TrimAnchor(["a", "b", "c"], ["b", "c"], null, nodes).Equals(MightyGraphCamera.Anchor.Reaim(Id("c", "request"), true)), "a trim re-aims at the newest request");
        // Longer, but not the old list with more above it.
        Check(MightyGraphCamera.TrimAnchor(["a", "c"], ["x", "b", "c"], null, nodes).Equals(MightyGraphCamera.Anchor.Reaim(Id("c", "request"), true)), "a different list re-aims");
        // What stood in the middle of the viewport stays there (macOS MightyGraphCanvas onChange(of: zoom)).
        var pan = (-120.0, -900.0);
        foreach (var (from, to) in new[] { (1.0, 1.5), (1.5, 0.5), (0.7, 0.8) })
        {
            var zoomed = MightyGraphViewModel.ZoomAnchored(pan, Viewport, from, to);
            var (beforeX, beforeY) = ((Viewport.W / 2 - pan.Item1) / from, (Viewport.H / 2 - pan.Item2) / from);
            Check(Math.Abs(beforeX * to + zoomed.X - Viewport.W / 2) < 0.001 && Math.Abs(beforeY * to + zoomed.Y - Viewport.H / 2) < 0.001, $"the centre keeps its place from {from} to {to}");
        }
        Check(MightyGraphViewModel.ZoomAnchored(pan, Viewport, 0, 1) == pan && MightyGraphViewModel.ZoomAnchored(pan, Viewport, 1, double.NaN) == pan, "a broken zoom leaves the pan");
        return Task.CompletedTask;
    }
}
