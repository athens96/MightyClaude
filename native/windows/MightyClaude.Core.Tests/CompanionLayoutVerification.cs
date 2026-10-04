using MightyClaude.Core;

internal static class CompanionLayoutVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    internal static Task EdgesAndAnchors()
    {
        Check(CompanionBubbleLayout.Width(null) == 258 && CompanionBubbleLayout.Width(double.NaN) == 258 && CompanionBubbleLayout.Height(null) is null, "Default and invalid preferences preserve automatic height.");
        Check(CompanionBubbleLayout.Hit(1, 1, 258, 180, true) == (CompanionResizeEdges.Left | CompanionResizeEdges.Top), "Top-left corner resizes both axes.");
        Check(CompanionBubbleLayout.Hit(1, 90, 258, 180, true) == CompanionResizeEdges.Left && CompanionBubbleLayout.Hit(130, 1, 258, 180, true) == CompanionResizeEdges.Top, "Thin side and top strips leave central content interactive.");
        Check(CompanionBubbleLayout.Hit(130, 90, 258, 180, true) == CompanionResizeEdges.None && CompanionBubbleLayout.Hit(-1, 90, 258, 180, true) == CompanionResizeEdges.None, "Content and outside release cannot become resize handles.");
        Check(CompanionBubbleLayout.Hit(130, 1, 258, 180, false) == CompanionResizeEdges.None && CompanionBubbleLayout.Hit(1, 90, 258, 180, false) == CompanionResizeEdges.Left, "An approval card retains automatic height while its sides resize.");
        foreach (var dpi in new[] { 1.0, 1.25, 2.0, 4.0 })
        {
            var drag = CompanionBubbleLayout.Resize(258, 180, -60 * dpi, -80 * dpi, dpi, CompanionResizeEdges.Left | CompanionResizeEdges.Top);
            Check(drag.Width == 318 && drag.Height == 260 && drag.MoveX + drag.Width == 258 && drag.MoveY + drag.Height == 180, "Opposite edges stay stationary across DPI scales.");
            var clamp = CompanionBubbleLayout.Resize(258, 180, -10000 * dpi, -10000 * dpi, dpi, CompanionResizeEdges.Left | CompanionResizeEdges.Top);
            Check(clamp.Width == 640 && clamp.Height == 480 && clamp.MoveX + clamp.Width == 258 && clamp.MoveY + clamp.Height == 180, "Clamping does not move the pinned edges.");
            var side = CompanionBubbleLayout.Resize(258, 180, 20 * dpi, 100 * dpi, dpi, CompanionResizeEdges.Right);
            Check(side.Width == 278 && side.Height == 180 && side.MoveY == 0 && !CompanionBubbleLayout.Vertical(CompanionResizeEdges.Right), "A sideways drag ignores vertical drift and keeps the automatic-height policy.");
        }
        return Task.CompletedTask;
    }
}
