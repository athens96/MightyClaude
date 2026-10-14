namespace MightyClaude.Core;

/// Which sides of a box a drag is moving (macOS ResizeEdges). Coordinates are y-down.
[Flags]
public enum ResizeEdges { None = 0, Left = 1, Right = 2, Top = 4, Bottom = 8, BottomRight = Right | Bottom }

/// Port of macOS ResizeEdges: a diagram block resizes from its corner handle and from a band along each border.
public static class ResizeEdgeRules
{
    public static bool Horizontal(this ResizeEdges edges) => (edges & (ResizeEdges.Left | ResizeEdges.Right)) != 0;
    public static bool Vertical(this ResizeEdges edges) => (edges & (ResizeEdges.Top | ResizeEdges.Bottom)) != 0;

    /// How far outside a block's border a press still grabs that side, in screen points at <paramref name="zoom"/>
    /// (macOS <c>MightyGraphInteractionProbe.resizeBand</c>). Inside it the strip is thin: the block's own
    /// scroller and text sit there.
    public static double Band(double zoom) => Math.Max(4, 6 * zoom);
    public const double BandInside = 2;
    /// The corner handle's side on screen at <paramref name="zoom"/> (macOS <c>resizeHandleRect</c>).
    public static double HandleSide(double zoom) => Math.Max(16, 22 * zoom);

    /// The sides whose band holds the point: up to <paramref name="outside"/> points beyond the border and
    /// <paramref name="inside"/> points within it. Near a corner that is both sides.
    public static ResizeEdges At((double X, double Y) point, GraphRect frame, double outside, double inside)
    {
        if (point.X < frame.X - outside || point.X >= frame.MaxX + outside || point.Y < frame.Y - outside || point.Y >= frame.MaxY + outside) return ResizeEdges.None;
        bool Band(double distance) => distance >= -outside && distance <= inside;
        var edges = ResizeEdges.None;
        if (Band(point.X - frame.X)) edges |= ResizeEdges.Left; else if (Band(frame.MaxX - point.X)) edges |= ResizeEdges.Right;
        if (Band(point.Y - frame.Y)) edges |= ResizeEdges.Top; else if (Band(frame.MaxY - point.Y)) edges |= ResizeEdges.Bottom;
        return edges;
    }

    /// <summary>
    /// The block and sides a press at <paramref name="point"/> would resize (macOS <c>resizeTarget(at:)</c>): the
    /// bottom-right handle first, then a band along each border; the block drawn last wins. <paramref name="frames"/>
    /// are the resizable blocks' frames on screen, in drawing order.
    /// </summary>
    public static (string Id, GraphRect Frame, ResizeEdges Edges)? Target((double X, double Y) point, IReadOnlyList<(string Id, GraphRect Frame)> frames, double zoom)
    {
        var side = HandleSide(zoom);
        for (var i = frames.Count - 1; i >= 0; i--)
        {
            var (id, frame) = frames[i];
            var inside = point.X >= frame.X && point.X < frame.MaxX && point.Y >= frame.Y && point.Y < frame.MaxY;
            if (inside && point.X >= frame.MaxX - side && point.Y >= frame.MaxY - side) return (id, frame, ResizeEdges.BottomRight);
            var edges = At(point, frame, Band(zoom), BandInside);
            if (edges != ResizeEdges.None) return (id, frame, edges);
        }
        return null;
    }

    /// The size after the pointer moved <paramref name="delta"/> screen points since the drag began at
    /// <paramref name="initial"/>. A left or top side grows as the pointer moves away from the box, the same as a
    /// right or bottom one.
    public static (double W, double H) Size(this ResizeEdges edges, (double W, double H) initial, (double X, double Y) delta, double zoom, (double W, double H) minimum, (double W, double H) maximum)
    {
        var scale = double.IsFinite(zoom) && zoom > 0 ? zoom : 1;
        var (width, height) = initial;
        if (edges.HasFlag(ResizeEdges.Right)) width += delta.X / scale; else if (edges.HasFlag(ResizeEdges.Left)) width -= delta.X / scale;
        if (edges.HasFlag(ResizeEdges.Bottom)) height += delta.Y / scale; else if (edges.HasFlag(ResizeEdges.Top)) height -= delta.Y / scale;
        return (Math.Min(maximum.W, Math.Max(minimum.W, width)), Math.Min(maximum.H, Math.Max(minimum.H, height)));
    }

    /// The corner that stays put, as a fraction of the box: the one opposite the dragged sides.
    public static (double X, double Y) PinnedUnit(this ResizeEdges edges) => (edges.HasFlag(ResizeEdges.Left) ? 1 : 0, edges.HasFlag(ResizeEdges.Top) ? 1 : 0);

    /// <summary>
    /// The camera that keeps the pinned corner of a resized block at <paramref name="anchor"/>, its point in the
    /// viewport when the drag began (macOS <c>MightyGraphInteractionProbe.updateLayoutFrames</c>): dragging a left or
    /// top side moves that side, never the one across from it, however the layout recentres the block.
    /// </summary>
    public static (double X, double Y) PinnedCamera((double X, double Y) anchor, (double X, double Y) unit, GraphRect frame, double zoom) =>
        (anchor.X - (frame.X + unit.X * frame.W) * zoom, anchor.Y - (frame.Y + unit.Y * frame.H) * zoom);
}
