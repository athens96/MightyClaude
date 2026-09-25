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
    public static bool IsAuxiliary(string nodeID) => nodeID == PendingNodeID || nodeID.EndsWith(":result-files", StringComparison.Ordinal);

    public static (double X, double Y) CameraOffset(GraphRect frame, (double W, double H) viewport, double zoom, bool alignTop) =>
        ((viewport.W - frame.W * zoom) / 2 - frame.X * zoom,
         (alignTop ? 16 : Math.Max(16, (viewport.H - frame.H * zoom) / 2)) - frame.Y * zoom);

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
