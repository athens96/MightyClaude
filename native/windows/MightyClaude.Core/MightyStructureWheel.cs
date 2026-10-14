namespace MightyClaude.Core;

/// <summary>
/// Wheel and trackpad scrolling of the structure view (M/MightyCore/MightyStructureWheel.swift). On the Mac the page takes the
/// wheel events that land on it and moves the scroll view they belong to: a trackpad gesture (the fingers' scroll, then its
/// momentum) is decided once, at its first movement, by the innermost scroll view under the pointer that can still move that
/// way, else the page, and keeps to the axis it started along; a mouse wheel's steps have no gesture and are decided one by
/// one. Either is the user's own move, so the follow rule never scrolls under it (<see cref="MightyStructureFollow.PageWatch"/>).
/// On Windows the page's own scroll viewer moves itself, chaining a wheel out of an inner scroll view at its edge and keeping a
/// touchpad pan to its rail, so the app reads only <see cref="Tracker.UserScrolling"/> and <see cref="UserMoving"/>: a Windows
/// wheel message carries no gesture, and is a step.
/// </summary>
public static class MightyStructureWheel
{
    /// <summary>After a wheel step, or the last event of a gesture, the page is still the user's this long, in seconds: its report of the last step may arrive later, and momentum starts only after the fingers lift.</summary>
    public const double SettleGrace = 0.3;
    /// <summary>A gesture that sent nothing for this long, in seconds, is over, even if its end never came.</summary>
    public const double StaleAfter = 1.0;

    /// <summary>Where a wheel event is in its gesture.</summary>
    public enum Stage
    {
        /// <summary>A mouse wheel's step: no gesture around it.</summary>
        Step,
        /// <summary>Fingers touched the trackpad, or started to move.</summary>
        Began,
        /// <summary>The fingers moved.</summary>
        Changed,
        /// <summary>The fingers lifted; momentum may follow.</summary>
        Ended,
        /// <summary>The gesture was cancelled: nothing follows.</summary>
        Cancelled,
        /// <summary>The momentum after the fingers lifted.</summary>
        Momentum,
        /// <summary>The momentum ran out or was stopped.</summary>
        MomentumEnded,
    }

    /// <summary>The stage of a wheel event from its AppKit phase raw values: began 1, stationary 2, changed 4, ended 8, cancelled 16, may begin 32.</summary>
    public static Stage StageOf(uint phase, uint momentumPhase)
    {
        if (momentumPhase != 0) return (momentumPhase & (8 | 16)) != 0 ? Stage.MomentumEnded : Stage.Momentum;
        if (phase == 0) return Stage.Step;
        if ((phase & 16) != 0) return Stage.Cancelled;
        if ((phase & 8) != 0) return Stage.Ended;
        if ((phase & (1 | 32)) != 0) return Stage.Began;
        return Stage.Changed;
    }

    public enum Axis { Horizontal, Vertical }

    /// <summary>The axis a movement is mostly along; null for no movement.</summary>
    public static Axis? AxisOf((double X, double Y) delta) =>
        delta.X == 0 && delta.Y == 0 ? null : Math.Abs(delta.X) > Math.Abs(delta.Y) ? Axis.Horizontal : Axis.Vertical;

    /// <summary>
    /// A scroll view's origin (top-down, as the page scrolls) after a wheel movement of <paramref name="delta"/> points: a
    /// positive y shows what is above, a positive x what is to the left. Only <paramref name="axis"/> moves when there is one;
    /// the origin stops at the edges, and an axis with no valid range keeps the origin it had.
    /// </summary>
    public static (double X, double Y) Origin((double X, double Y) current, (double X, double Y) delta, Axis? axis, (double X, double Y) minimum, (double X, double Y) maximum)
    {
        var dx = axis == Axis.Vertical ? 0 : delta.X; var dy = axis == Axis.Horizontal ? 0 : delta.Y;
        return (Clamp(current.X - dx, minimum.X, maximum.X, current.X), Clamp(current.Y - dy, minimum.Y, maximum.Y, current.Y));
    }

    /// <summary>The scroll view can still move the way <paramref name="delta"/> asks.</summary>
    public static bool CanMove((double X, double Y) current, (double X, double Y) delta, Axis? axis, (double X, double Y) minimum, (double X, double Y) maximum)
    {
        var next = Origin(current, delta, axis, minimum, maximum);
        return Math.Abs(next.X - current.X) > 0.5 || Math.Abs(next.Y - current.Y) > 0.5;
    }

    /// <summary>Which of the scroll views under the pointer takes a movement, innermost first and the page last: the first that can still move that way, otherwise the page.</summary>
    public static int Taker(IReadOnlyList<bool> canMove)
    {
        for (var index = 0; index < canMove.Count; index++) if (canMove[index]) return index;
        return Math.Max(0, canMove.Count - 1);
    }

    /// <summary>The page is moving under the user's hand: a scroll the system reports, a drag on the page or the moment after it (<see cref="MightyStructurePan.ReleaseGrace"/>), or a wheel.</summary>
    public static bool UserMoving(bool scrollPhase, bool panning, double panReleasedAt, Tracker wheel, double now) =>
        scrollPhase || panning || now - panReleasedAt < MightyStructurePan.ReleaseGrace || wheel.UserScrolling(now);

    /// <summary>A page's wheel gestures, one at a time.</summary>
    public sealed class Tracker
    {
        /// <summary>What the page does with a wheel event.</summary>
        public enum Action
        {
            /// <summary>Not this page's: the event goes on as it came.</summary>
            Pass,
            /// <summary>This page's, with nothing to move: the event is kept from the dock.</summary>
            Hold,
            /// <summary>This page's first movement (every wheel step is one): choose the scroll view that takes it (<see cref="Taker"/>) and move it.</summary>
            Choose,
            /// <summary>Move the scroll view the gesture chose.</summary>
            Move,
        }

        /// <summary>The current gesture began on this page.</summary>
        private bool owned, chosen;
        /// <summary>Fingers on the trackpad or momentum running.</summary>
        private bool live;
        private double lastEvent = double.NegativeInfinity;
        /// <summary>The axis the current gesture keeps; null for a wheel step, or before it moved.</summary>
        public Axis? Axis { get; private set; }

        /// <summary>A wheel event at <paramref name="now"/>, in seconds. <paramref name="overPage"/> says it landed on this page (a gesture keeps the page it began on); <paramref name="delta"/> is its movement.</summary>
        public Action Feed(Stage stage, bool overPage, (double X, double Y) delta, double now)
        {
            var moves = delta.X != 0 || delta.Y != 0;
            switch (stage)
            {
                case Stage.Step:
                    owned = false; chosen = false; live = false; Axis = null;
                    if (!overPage) return Action.Pass;
                    lastEvent = now;
                    return moves ? Action.Choose : Action.Hold;
                case Stage.Began:
                    owned = overPage; chosen = false; live = overPage; Axis = null;
                    if (!owned) return Action.Pass;
                    lastEvent = now;
                    if (!moves) return Action.Hold;
                    chosen = true; Axis = AxisOf(delta);
                    return Action.Choose;
                default:
                    // Momentum starts right after the fingers lift: a gesture that ended without it is over, and later momentum is another view's.
                    if (owned && !live && now - lastEvent >= SettleGrace) { owned = false; chosen = false; }
                    if (!owned) return Action.Pass;
                    lastEvent = now;
                    live = stage is Stage.Changed or Stage.Momentum;
                    var action = !moves ? Action.Hold : chosen ? Action.Move : Action.Choose;
                    if (action == Action.Choose) { chosen = true; Axis = AxisOf(delta); }
                    if (stage is Stage.Cancelled or Stage.MomentumEnded) { owned = false; chosen = false; }
                    return action;
            }
        }

        /// <summary>The user's wheel or fingers are moving this page: a gesture that is still going, or the moment after its last event.</summary>
        public bool UserScrolling(double now)
        {
            var since = now - lastEvent;
            return (live && since < StaleAfter) || since < SettleGrace;
        }
    }

    private static double Clamp(double value, double low, double high, double current) =>
        double.IsFinite(value) && double.IsFinite(low) && double.IsFinite(high) && low <= high ? Math.Min(Math.Max(value, low), high) : current;
}
