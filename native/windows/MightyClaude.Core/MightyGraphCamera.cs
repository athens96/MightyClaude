using System.Text;

namespace MightyClaude.Core;

/// Port of macOS MightyGraphCamera.
public static class MightyGraphCamera
{
    public const double RequestWidth = 500;
    public const double Margin = 24;
    public const double CentreX = Margin + RequestWidth / 2; // 274

    public static double X(double treeWidth) => CentreX - treeWidth / 2;
    public static double OriginX(double leadingMinX) => Math.Min(0, leadingMinX - Margin);
    public static double CanvasWidth(double leading, double trailing) =>
        Math.Max(trailing + Margin, RequestWidth + Margin * 2) - OriginX(leading);

    public const string PendingNodeID = "pending-input";
    public static bool IsAuxiliary(string nodeID) => nodeID == PendingNodeID || nodeID == MightyGraphLayout.HistoryNodeID || nodeID.EndsWith(":result-files", StringComparison.Ordinal)
        || nodeID.Contains(":" + MightyGraphLayout.PlanRecordSuffix, StringComparison.Ordinal);

    /// <summary>
    /// The camera shows the top of the diagram (the history block, or the first
    /// card when there is none), so scrolling further up asks for older requests.
    /// <paramref name="cameraY"/> is the pan in node coordinates times the zoom
    /// (screen y = node y × zoom + cameraY); <paramref name="slack"/> is how far
    /// below that top the viewport may start (macOS MightyGraphCamera.showsTop).
    /// </summary>
    public static bool ShowsTop(double cameraY, double zoom, double top, double slack = 8) =>
        double.IsFinite(zoom) && zoom > 0 && double.IsFinite(cameraY) && double.IsFinite(top) && -cameraY / zoom <= top + slack;

    public static (double X, double Y) CameraOffset(GraphRect frame, (double W, double H) viewport, double zoom, bool alignTop) =>
        ((viewport.W - frame.W * zoom) / 2 - frame.X * zoom,
         (alignTop ? 16 : Math.Max(16, (viewport.H - frame.H * zoom) / 2)) - frame.Y * zoom);

    /// A newly finished result card (macOS <c>MightyGraphLayout.revealOffset</c>):
    /// its bottom 16pt above the viewport's bottom edge, right above the
    /// composer, or — taller than the viewport — its top where an align-top
    /// re-aim puts it, so its beginning is what shows.
    public static (double X, double Y) RevealOffset(GraphRect frame, (double W, double H) viewport, double zoom)
    {
        var top = CameraOffset(frame, viewport, zoom, alignTop: true);
        return (top.X, Math.Max(top.Y, viewport.H - 16 - frame.MaxY * zoom));
    }

    /// <paramref name="alignBottom"/> (a revealed result) wins over <paramref name="alignTop"/>.
    public static (double X, double Y) CameraOffset(GraphRect frame, (double W, double H) viewport, double zoom, bool alignTop, bool alignBottom) =>
        alignBottom ? RevealOffset(frame, viewport, zoom) : CameraOffset(frame, viewport, zoom, alignTop);

    /// <summary>
    /// When the camera puts a newly finished request's result card right above
    /// the composer (<see cref="RevealOffset"/>; macOS <c>MightyGraphCamera.ResultReveal</c>).
    /// It happens once, for a request of the pane's own that finishes while the
    /// diagram watches — never for runs a pane opens or hydrates with, nor for
    /// ones read back from the session record, nor when the newest result moves
    /// back to an older request. A card whose content was not measured yet is
    /// held without a camera move, so it lands once at its real height (or at
    /// its cap if the measurement never comes). While held, the card settling
    /// to a new height or the canvas changing size places it again. The user's
    /// own scroll, a drag, a zoom, a draft being typed or a new request ends
    /// the hold.
    ///
    /// Windows records a request's diagram only when it finishes, so a run is
    /// never seen unfinished first: a finished run that is new to the pane while
    /// one of its requests was in flight (<see cref="PaneRunning"/>) is the one
    /// that just finished, as an unfinished-then-finished run is on macOS.
    /// </summary>
    public sealed class ResultReveal
    {
        /// A request as the reveal rule sees it.
        public sealed record RunProgress(string Id, bool Finished);

        /// The card the camera is holding above the composer.
        public string? HoldingID { get; private set; }
        /// The held card was not placed yet: its measurement is awaited.
        public bool AwaitingMeasure { get; private set; }
        /// A request of the pane's own was seen running and its diagram has not arrived yet.
        public bool RequestInFlight { get; private set; }

        /// The run whose result has just appeared: the newest finished run,
        /// which the previous observation saw unfinished or — with a request in
        /// flight — did not have yet and which is the newest run. A run seen for
        /// the first time already finished with nothing in flight (a launch, a
        /// hydration, history) and a newest result moving back to an earlier run
        /// never qualify.
        public static string? FinishedRunID(IReadOnlyList<RunProgress> previous, IReadOnlyList<RunProgress> current, bool requestInFlight = false)
        {
            var latest = current.LastOrDefault(r => r.Finished);
            if (latest is null) return null;
            var before = previous.FirstOrDefault(r => r.Id == latest.Id);
            if (before is not null) return before.Finished ? null : latest.Id;
            return requestInFlight && current[^1].Id == latest.Id ? latest.Id : null;
        }

        /// The pane's running state, at every observation. A request starting
        /// is a new request: it ends any hold and marks a request in flight.
        public void PaneRunning(bool wasRunning, bool isRunning)
        {
            if (!isRunning) return;
            if (!wasRunning) Cancel();
            RequestInFlight = true;
        }

        /// The pane's own runs changed. <paramref name="resultID"/> maps a run to
        /// its result card; <paramref name="measured"/> says whether that card's
        /// height is known already. Returns the card to place now, if any.
        public string? RunsChanged(IReadOnlyList<RunProgress> previous, IReadOnlyList<RunProgress> current,
            Func<string, string> resultID, Func<string, bool> measured)
        {
            var run = FinishedRunID(previous, current, RequestInFlight);
            if (run is null) return null;
            RequestInFlight = false;
            var id = resultID(run);
            HoldingID = id;
            AwaitingMeasure = !measured(id);
            return AwaitingMeasure ? null : id;
        }

        /// A card's measured content height changed; returns it when it is the
        /// one held, since its new height moved its bottom.
        public string? ContentMeasured(string id)
        {
            if (id != HoldingID) return null;
            AwaitingMeasure = false;
            return id;
        }

        /// The measurement did not come (the card was never drawn): place it at
        /// its cap, which draws it, and its measurement places it again.
        public string? MeasureTimedOut(string id)
        {
            if (id != HoldingID || !AwaitingMeasure) return null;
            AwaitingMeasure = false;
            return id;
        }

        /// The canvas changed size; returns the held card to place again, unless
        /// it is still waiting for its first placement.
        public string? ViewportChanged() => AwaitingMeasure ? null : HoldingID;

        /// The composer started holding a draft: the draft's block is what the
        /// camera keeps from now on.
        public void DraftChanged(bool wasEmpty, bool isEmpty)
        {
            if (wasEmpty && !isEmpty) Cancel();
        }

        /// The user moved or zoomed the camera, or something else re-aimed it.
        public void Cancel() { HoldingID = null; AwaitingMeasure = false; }
    }

    public sealed class Anchor
    {
        public static readonly Anchor Hold = new("hold", null, false);
        public static Anchor Reaim(string nodeID, bool alignTop) => new("reaim", nodeID, alignTop);
        private Anchor(string kind, string? nodeID, bool alignTop) { Kind = kind; NodeID = nodeID; AlignTop = alignTop; }
        public string Kind { get; }
        public string? NodeID { get; }
        public bool AlignTop { get; }
        public override bool Equals(object? obj) => obj is Anchor a && Kind == a.Kind && NodeID == a.NodeID && AlignTop == a.AlignTop;
        public override int GetHashCode() => HashCode.Combine(Kind, NodeID, AlignTop);
    }

    public static Anchor TrimAnchor(IReadOnlyList<string> previousRunIDs, IReadOnlyList<string> runIDs, string? selectedNodeID, IReadOnlySet<string> layoutNodeIDs)
    {
        if (runIDs.Count == 0)
        {
            if (previousRunIDs.Count == 0) return Anchor.Hold;
            return ReaimAnchor(null, selectedNodeID, layoutNodeIDs);
        }
        var last = runIDs[^1];
        if (last != (previousRunIDs.Count > 0 ? previousRunIDs[^1] : null) || new HashSet<string>(previousRunIDs).SetEquals(runIDs)) return Anchor.Hold;
        return ReaimAnchor(last, selectedNodeID, layoutNodeIDs);
    }

    public static Anchor ReaimAnchor(string? newestRunID, string? selectedNodeID, IReadOnlySet<string> layoutNodeIDs)
    {
        if (selectedNodeID is not null && !IsAuxiliary(selectedNodeID) && layoutNodeIDs.Contains(selectedNodeID))
            return Anchor.Reaim(selectedNodeID, false);
        if (newestRunID is null)
        {
            if (!layoutNodeIDs.Contains(PendingNodeID)) return Anchor.Hold;
            return Anchor.Reaim(PendingNodeID, true);
        }
        var newest = MightyGraphBlockSize.NodeId(newestRunID, "request");
        if (!layoutNodeIDs.Contains(newest)) return Anchor.Hold;
        return Anchor.Reaim(newest, true);
    }

    public static Anchor ResizeAnchor(string? fittedResultID, string? targetID, bool targetAlignTop, IReadOnlyDictionary<string, GraphRect> frames)
    {
        if (fittedResultID is null || !frames.TryGetValue(fittedResultID, out var fitted)) return Anchor.Hold;
        if (targetID is not null && targetID != fittedResultID && frames.TryGetValue(targetID, out var target) && target.Y > fitted.Y)
            return Anchor.Reaim(targetID, targetAlignTop);
        return Anchor.Reaim(fittedResultID, true);
    }

    public static int? LostFrameIndex(IReadOnlyList<GraphRect> previousFrames, IReadOnlyList<GraphRect> currentFrames, (double X, double Y) camera, (double W, double H) viewport, double zoom)
    {
        if (!double.IsFinite(zoom) || zoom <= 0 || !double.IsFinite(camera.X) || !double.IsFinite(camera.Y)
            || viewport.W <= 0 || viewport.H <= 0 || !double.IsFinite(viewport.W) || !double.IsFinite(viewport.H)
            || currentFrames.Count == 0) return null;
        var visibleX = -camera.X / zoom;
        var visibleY = -camera.Y / zoom;
        var visibleW = viewport.W / zoom;
        var visibleH = viewport.H / zoom;
        double? Shown(GraphRect frame, double atLeast)
        {
            var ix = Math.Max(frame.X, visibleX); var iy = Math.Max(frame.Y, visibleY);
            var iw = Math.Min(frame.X + frame.W, visibleX + visibleW) - ix;
            var ih = Math.Min(frame.Y + frame.H, visibleY + visibleH) - iy;
            if (iw <= 0 || ih <= 0) return null;
            var least = atLeast / zoom;
            if (iw < Math.Min(least, frame.W) || ih < Math.Min(least, frame.H)) return null;
            return iw * ih;
        }
        const double strandedOverlap = 24, strandedResidue = 4;
        int? bestIndex = null; double bestArea = -1;
        for (var i = 0; i < previousFrames.Count; i++)
        {
            var area = Shown(previousFrames[i], strandedOverlap);
            if (area is { } a && a > bestArea) { bestIndex = i; bestArea = a; }
        }
        if (bestIndex is null) return null;
        return currentFrames.Any(f => Shown(f, strandedResidue) is not null) ? null : bestIndex;
    }

    public static string TrimToken(int sequence, string nodeID) => "trim:" + sequence + ":" + nodeID;

    public static (double X, double Y)? AdmittedCamera(string? targetToken, string? consumedToken, (double X, double Y)? targetCamera, (double X, double Y) current, (double X, double Y) requested)
    {
        if (targetToken is null || targetToken == consumedToken) return requested;
        if (targetCamera is null) return null;
        return (targetCamera.Value.X + requested.X - current.X, targetCamera.Value.Y + requested.Y - current.Y);
    }
}

public record struct GraphRect(double X, double Y, double W, double H)
{
    public double MaxX => X + W;
    public double MaxY => Y + H;
    public double MidX => X + W / 2;
    public double MidY => Y + H / 2;
    public GraphRect OffsetBy(double dx, double dy) => new(X + dx, Y + dy, W, H);
}
