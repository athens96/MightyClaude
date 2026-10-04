namespace MightyClaude.Core;

[Flags]
public enum CompanionResizeEdges { None = 0, Left = 1, Right = 2, Top = 4, Bottom = 8 }
public sealed record CompanionBubbleResize(double Width, double Height, double MoveX, double MoveY);

/// Shares the Mac bubble's logical-point bounds. Pointer deltas are measured
/// in screen pixels; the opposite edge remains stationary at every DPI.
public static class CompanionBubbleLayout
{
    public const double DefaultWidth = 258, MinimumWidth = 220, MaximumWidth = 640, MinimumHeight = 100, MaximumHeight = 480;
    public static double Width(double? value) => Math.Clamp(value is {} width && double.IsFinite(width) ? width : DefaultWidth, MinimumWidth, MaximumWidth);
    public static double? Height(double? value) => value is {} height && double.IsFinite(height) ? Math.Clamp(height, MinimumHeight, MaximumHeight) : null;
    public static bool Vertical(CompanionResizeEdges edges) => (edges & (CompanionResizeEdges.Top | CompanionResizeEdges.Bottom)) != 0;
    public static CompanionBubbleResize Resize(double width, double height, double dx, double dy, double scale, CompanionResizeEdges edges)
    {
        scale = double.IsFinite(scale) && scale > 0 ? scale : 1;
        dx = double.IsFinite(dx) ? dx / scale : 0; dy = double.IsFinite(dy) ? dy / scale : 0;
        var nextWidth = Width(width + (edges.HasFlag(CompanionResizeEdges.Right) ? dx : edges.HasFlag(CompanionResizeEdges.Left) ? -dx : 0));
        var nextHeight = Height(height + (edges.HasFlag(CompanionResizeEdges.Bottom) ? dy : edges.HasFlag(CompanionResizeEdges.Top) ? -dy : 0)) ?? MinimumHeight;
        return new(nextWidth, nextHeight, edges.HasFlag(CompanionResizeEdges.Left) ? width - nextWidth : 0, edges.HasFlag(CompanionResizeEdges.Top) ? height - nextHeight : 0);
    }
    public static CompanionResizeEdges Hit(double x, double y, double width, double height, bool vertical)
    {
        if (x < 0 || y < 0 || x >= width || y >= height) return CompanionResizeEdges.None;
        if (vertical && x >= width - 20 && y >= height - 20) return CompanionResizeEdges.Right | CompanionResizeEdges.Bottom;
        if (vertical && y < 12 && x < 12) return CompanionResizeEdges.Left | CompanionResizeEdges.Top;
        if (vertical && y < 12 && x >= width - 12) return CompanionResizeEdges.Right | CompanionResizeEdges.Top;
        var edges = x < 6 ? CompanionResizeEdges.Left : x >= width - 6 ? CompanionResizeEdges.Right : CompanionResizeEdges.None;
        if (vertical && y < 6) edges |= CompanionResizeEdges.Top;
        return edges;
    }
}
