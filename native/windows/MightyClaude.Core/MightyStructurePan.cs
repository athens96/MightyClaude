namespace MightyClaude.Core;

/// <summary>
/// Dragging the structure view's empty space moves its page, as dragging the diagram's empty canvas moves its camera
/// (M/MightyCore/MightyStructurePan.swift): the point that was grabbed stays under the pointer, and the page stops at its
/// edges as a scroll does. A press on selectable text or a native control is theirs: text selects, a scroll bar scrolls.
/// Any other press on the page waits until it moves <see cref="ClickSlop"/> points; a shorter press is a click and goes on
/// to what was pressed, so buttons click as they always did. A pan is the user's own move, as a wheel scroll is
/// (<see cref="MightyStructureFollow.PageWatch"/>).
/// </summary>
public static class MightyStructurePan
{
    /// <summary>Points a press moves before it is a drag.</summary>
    public const double ClickSlop = 3;
    /// <summary>After a release the page is still the user's for this long, in seconds: the page's report of the last drag step may arrive after the button is up.</summary>
    public const double ReleaseGrace = 0.25;

    /// <summary>What a press lands on.</summary>
    public enum Target
    {
        /// <summary>The page: its background, the gaps between columns and cards, a card's header and padding, or a text that cannot be selected.</summary>
        Page,
        /// <summary>Selectable text or a native control such as a scroll bar: it takes its own drag.</summary>
        TextOrControl,
        /// <summary>A scroll view inside the page.</summary>
        InnerScroll,
        /// <summary>Not this page: another pane, the composer, chrome laid over the page.</summary>
        Outside,
    }

    /// <summary>Whether a left press may start a pan: on the page itself, with no modifier key, on a page that can move at all. A page that cannot move keeps every press.</summary>
    public static bool MayPan(Target target, bool modified, bool scrollable) => target == Target.Page && !modified && scrollable;

    /// <summary>The press has moved far enough from where it went down to be a drag.</summary>
    public static bool IsDrag((double X, double Y) start, (double X, double Y) point) =>
        Math.Max(Math.Abs(point.X - start.X), Math.Abs(point.Y - start.Y)) >= ClickSlop;

    /// <summary>The page can move along either axis between these origins.</summary>
    public static bool Scrollable((double X, double Y) minimum, (double X, double Y) maximum) =>
        maximum.X - minimum.X > 0.5 || maximum.Y - minimum.Y > 0.5;

    /// <summary>
    /// The page's origin for the pointer at <paramref name="cursor"/> after it went down at <paramref name="press"/> with the
    /// page at <paramref name="start"/>: the grabbed point stays under the pointer, clamped to the page's edges. An axis that
    /// cannot move keeps the origin it had.
    /// </summary>
    public static (double X, double Y) Origin((double X, double Y) start, (double X, double Y) press, (double X, double Y) cursor, (double X, double Y) minimum, (double X, double Y) maximum) =>
        (Clamp(start.X - (cursor.X - press.X), minimum.X, maximum.X, start.X), Clamp(start.Y - (cursor.Y - press.Y), minimum.Y, maximum.Y, start.Y));

    private static double Clamp(double value, double low, double high, double current) =>
        double.IsFinite(value) && double.IsFinite(low) && double.IsFinite(high) && low <= high ? Math.Min(Math.Max(value, low), high) : current;
}
