namespace MightyClaude.Core;

/// <summary>
/// Port of macOS MightyGraphFollow, the diagram's half. While the reader is at the
/// newest work, the newest block's bottom sits right above the composer: a new
/// block or a block that settles to a new height is shown as it arrives. A reader
/// who scrolls, pans or zooms away is left exactly where they are until they come
/// back to the newest work or send a request. Cameras and frames are in node
/// coordinates (screen y = node y × zoom + camera y). The shared camera rules
/// (<see cref="MightyGraphCamera"/>, native/contracts/graph-vectors.json) are left as
/// they are.
/// </summary>
public static class MightyGraphFollow
{
    /// Screen points between the followed block's bottom and the composer: the
    /// gap a revealed result card always had (<see cref="MightyGraphCamera.RevealOffset"/>).
    public const double Margin = 16;
    /// How far the followed block's bottom may hide behind the composer and the
    /// reader still counts as at the newest work (macOS <c>TranscriptViewport.followSlack</c>).
    public const double Slack = 36;

    /// <summary>
    /// The block a following camera keeps above the composer: the newest request's
    /// lowest block in the flow (its result, a plan waiting for an answer, its
    /// deepest sub-agent, its 실행 block or the request itself) and of blocks in one
    /// row the one added last. Blocks hung beside the flow and the next-request
    /// draft never count, so typing a draft never moves the view; with no request
    /// the draft block is the only block there is.
    /// </summary>
    public static string? TargetID(MightyGraphLayout layout, IReadOnlyList<MightyGraphRun> runs)
    {
        if (runs.Count == 0) return layout.Nodes.FirstOrDefault(n => n.Kind == "draft")?.Id;
        var newest = MightyGraphLayout.NodeID(runs[^1], "");
        MightyGraphLayout.Node? best = null;
        foreach (var node in layout.Nodes)
            if (node.Kind is "request" or MightyGraphLayout.WorkKind or "result" or "plan" or "agent" && node.Id.StartsWith(newest, StringComparison.Ordinal)
                && node.Frame.MaxY >= (best?.Frame.MaxY ?? double.NegativeInfinity)) best = node;
        return best?.Id;
    }

    /// The camera shows the newest work: the followed block's bottom is on screen,
    /// or hidden behind the composer by no more than <see cref="Slack"/>.
    public static bool IsAtNewest(GraphRect target, (double X, double Y) camera, (double W, double H) viewport, double zoom)
    {
        if (!Valid(camera, viewport, zoom)) return false;
        var bottom = camera.Y + target.MaxY * zoom;
        return bottom >= 0 && bottom <= viewport.H - Margin + Slack;
    }

    /// The camera x that puts the diagram's centreline, which every request is
    /// on, in the middle of the viewport.
    public static double CentredX((double W, double H) viewport, double zoom) => viewport.W / 2 - MightyGraphCamera.CentreX * zoom;

    /// The camera that puts <paramref name="target"/>'s bottom <paramref name="gap"/> points above the
    /// viewport's bottom, at camera x <paramref name="x"/>. A block taller than the viewport shows its
    /// bottom, where its newest content is.
    public static (double X, double Y) Camera(GraphRect target, (double W, double H) viewport, double zoom, double x, double gap = Margin) =>
        (x, viewport.H - gap - target.MaxY * zoom);

    /// A camera target's offset. <paramref name="follows"/> (the first placement, a send) puts the
    /// block's bottom right above the composer on the centreline; anything else is
    /// placed by the shared rule.
    public static (double X, double Y) CameraOffset(GraphRect frame, (double W, double H) viewport, double zoom, bool alignTop, bool alignBottom, bool follows) =>
        follows ? Camera(frame, viewport, zoom, CentredX(viewport, zoom)) : MightyGraphCamera.CameraOffset(frame, viewport, zoom, alignTop, alignBottom);

    private static bool Valid((double X, double Y) camera, (double W, double H) viewport, double zoom) =>
        double.IsFinite(zoom) && zoom > 0 && double.IsFinite(camera.X) && double.IsFinite(camera.Y)
        && viewport.W > 0 && viewport.H > 0 && double.IsFinite(viewport.W) && double.IsFinite(viewport.H);

    /// <summary>What a change of the diagram's run ids asks of the camera (<see cref="RunsChanged"/>).</summary>
    public enum RunChange { None, First, Follow, Trim }

    /// <summary>
    /// The run-id rules (macOS MightyGraphView <c>onChange(of: runs.map(\.id))</c>), watched in every view so a send from
    /// the 구조 view or the timeline is followed in the diagram. <paramref name="previous"/> is null before the first
    /// observation, which follows. A new last request is the user's own send: follow it. Any other change of the list
    /// (a trim, older requests loaded above) is a trim to re-aim through, unless the camera follows, which keeps its
    /// block in place itself. The same list asks nothing.
    /// </summary>
    public static RunChange RunsChanged(IReadOnlyList<string>? previous, IReadOnlyList<string> current, bool following)
    {
        if (previous is null) return RunChange.First;
        if (previous.SequenceEqual(current)) return RunChange.None;
        if (current.Count > 0 && (previous.Count > 0 ? previous[^1] : null) != current[^1]) return RunChange.Follow;
        return following ? RunChange.None : RunChange.Trim;
    }

    /// <summary>The diagram camera's follow state between layout passes (macOS <c>MightyGraphFollow.Watch</c>).</summary>
    public sealed class Watch
    {
        public bool Following { get; private set; } = true;
        /// The block the last pass followed, and its bottom's distance above the
        /// viewport's bottom then.
        private string? targetID;
        private double? gap;
        /// The user changed the layout themselves; the next pass judges where that left them.
        private bool judging;

        /// A send or the first placement: follow, with the newest block right above the composer.
        public void Start() { Following = true; targetID = null; gap = null; judging = false; }
        /// The app aimed somewhere the user asked for, or the user zoomed: nothing
        /// follows until the reader comes back to the newest work or sends a request.
        public void Stop() { Following = false; targetID = null; gap = null; judging = false; }
        /// The user opened, folded or resized a block. Nothing moves for it: the next
        /// pass follows on only if the newest block's bottom is still in view, from
        /// where it is now.
        public void UserChangedLayout() { if (Following) judging = true; }

        /// The user moved the camera, or an aim landed: follow only when the
        /// camera shows the newest work, keeping the block where it is now.
        public void Moved(string? targetID, GraphRect? target, (double X, double Y) camera, (double W, double H) viewport, double zoom)
        {
            if (targetID is null || target is not { } frame || !IsAtNewest(frame, camera, viewport, zoom)) { Stop(); return; }
            Following = true;
            this.targetID = targetID;
            gap = viewport.H - (camera.Y + frame.MaxY * zoom);
        }

        /// <summary>
        /// A layout pass, a new followed block or a new viewport size. Returns the
        /// camera to move to, null to stay. A new block is put right above the
        /// composer; the same block keeps its distance from the composer, so a
        /// block settling to its height, a trim above it or a taller composer never
        /// takes it off screen. Only y moves: the reader's x is theirs.
        /// </summary>
        public (double X, double Y)? Observe(string? targetID, GraphRect? target, (double X, double Y) camera, (double W, double H) viewport, double zoom)
        {
            if (!Following || targetID is null || target is not { } frame || !Valid(camera, viewport, zoom)) return null;
            if (judging)
            {
                judging = false;
                Moved(targetID, frame, camera, viewport, zoom);
                return null;
            }
            var kept = targetID == this.targetID ? gap ?? Margin : Margin;
            this.targetID = targetID;
            gap = kept;
            var next = Camera(frame, viewport, zoom, camera.X, kept);
            return Math.Abs(next.Y - camera.Y) > 0.5 ? next : null;
        }
    }
}
