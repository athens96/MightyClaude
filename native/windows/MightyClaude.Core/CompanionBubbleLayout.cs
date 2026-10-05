namespace MightyClaude.Core;

[Flags]
public enum CompanionResizeEdges { None = 0, Left = 1, Right = 2, Top = 4, Bottom = 8 }
public sealed record CompanionBubbleResize(double Width, double Height, double MoveX, double MoveY);

/// Shares the Mac bubble's logical-point bounds. Pointer deltas are measured
/// in screen pixels; the opposite edge remains stationary at every DPI.
public static class CompanionBubbleLayout
{
    public const double DefaultWidth = 258, MinimumWidth = 220, MaximumWidth = 640, MinimumHeight = 100, MaximumHeight = 480;
    /// The pet's floating window around its bubble (M/ResizeEdges.swift:51-56, M/AgentCompanionViews.swift:129-145):
    /// 12 on each side of the bubble, 8 around the column, 2 between the bubble and the pet's 125 × 135 frame.
    public const double SidePadding = 12, Padding = 8, Gap = 2, PetWidth = 125, PetHeight = 135;
    /// Top padding, the gap under the bubble, the pet and the bottom padding.
    public const double Chrome = Padding + Gap + PetHeight + Padding;
    /// The window's height around a bubble that follows its content, and around an approval.
    public const double BaseHeight = 330, TallHeight = 494;
    public static double Width(double? value) => Math.Clamp(value is {} width && double.IsFinite(width) ? width : DefaultWidth, MinimumWidth, MaximumWidth);
    public static double? Height(double? value) => value is {} height && double.IsFinite(height) ? Math.Clamp(height, MinimumHeight, MaximumHeight) : null;
    /// An approval is never narrower than the default, so its buttons still fit (M/ResizeEdges.swift:71).
    public static double ApprovalWidth(double? value) => Math.Max(DefaultWidth, Width(value));
    /// The window for a bubble (M/ResizeEdges.swift:61-67): a null height keeps the bubble as tall as its
    /// content inside the base window; <paramref name="tall"/> is the approval, in its own fixed window.
    public static (double Width, double Height) PanelSize(double? width, double? height, bool tall)
    {
        var panelWidth = (tall ? ApprovalWidth(width) : Width(width)) + SidePadding * 2;
        if (tall) return (panelWidth, TallHeight);
        return (panelWidth, Math.Max(BaseHeight, Height(height) is {} fixedHeight ? fixedHeight + Chrome : 0));
    }
    public static bool Vertical(CompanionResizeEdges edges) => (edges & (CompanionResizeEdges.Top | CompanionResizeEdges.Bottom)) != 0;
    public static CompanionBubbleResize Resize(double width, double height, double dx, double dy, double scale, CompanionResizeEdges edges)
    {
        scale = double.IsFinite(scale) && scale > 0 ? scale : 1;
        dx = double.IsFinite(dx) ? dx / scale : 0; dy = double.IsFinite(dy) ? dy / scale : 0;
        var nextWidth = Width(width + (edges.HasFlag(CompanionResizeEdges.Right) ? dx : edges.HasFlag(CompanionResizeEdges.Left) ? -dx : 0));
        var nextHeight = Height(height + (edges.HasFlag(CompanionResizeEdges.Bottom) ? dy : edges.HasFlag(CompanionResizeEdges.Top) ? -dy : 0)) ?? MinimumHeight;
        return new(nextWidth, nextHeight, edges.HasFlag(CompanionResizeEdges.Left) ? width - nextWidth : 0, edges.HasFlag(CompanionResizeEdges.Top) ? height - nextHeight : 0);
    }
    /// Thin strips along the bubble's top, left and right edges, and its two top corners. The bottom
    /// meets the pet, so it has none (M/AgentCompanionViews.swift:484-505).
    public static CompanionResizeEdges Hit(double x, double y, double width, double height, bool vertical)
    {
        if (x < 0 || y < 0 || x >= width || y >= height) return CompanionResizeEdges.None;
        if (vertical && y < 12 && x < 12) return CompanionResizeEdges.Left | CompanionResizeEdges.Top;
        if (vertical && y < 12 && x >= width - 12) return CompanionResizeEdges.Right | CompanionResizeEdges.Top;
        var edges = x < 6 ? CompanionResizeEdges.Left : x >= width - 6 ? CompanionResizeEdges.Right : CompanionResizeEdges.None;
        if (vertical && y < 6) edges |= CompanionResizeEdges.Top;
        return edges;
    }
}
