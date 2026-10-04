namespace MightyClaude.Core;

/// The same deterministic 3/60/30-second measurement fixture as macOS. Its
/// document and terminal are synthetic; no existing application receives input.
public static class ScreenShareReferenceScene
{
    public const double PrerollSeconds = 3, MotionSeconds = 60, StillSeconds = 30, TotalSeconds = 93;
    public const double ScrollPointsPerSecond = 90, TypedCharactersPerSecond = 18;
    public sealed record Moment(string Phase, int SecondsLeft, double MotionElapsed);
    public static Moment At(double elapsed)
    {
        var t = double.IsFinite(elapsed) ? Math.Max(0, elapsed) : 0;
        var motion = Math.Clamp(t - PrerollSeconds, 0, MotionSeconds);
        var (phase, end) = t < 3 ? ("preroll", 3.0) : t < 63 ? ("motion", 63.0) : t < 93 ? ("still", 93.0) : ("done", t);
        return new(phase, (int)Math.Ceiling(end - t), motion);
    }
    public static double ScrollOffset(double motionElapsed, double contentHeight) => double.IsFinite(contentHeight) && contentHeight > 0 && double.IsFinite(motionElapsed)
        ? Math.Max(0, motionElapsed) * ScrollPointsPerSecond % contentHeight : 0;
    public static int TypedCount(double motionElapsed) => double.IsFinite(motionElapsed) ? (int)Math.Min(int.MaxValue, Math.Floor(Math.Max(motionElapsed, 0) * TypedCharactersPerSecond)) : 0;
    public static IReadOnlyDictionary<string, string> Announcement(string phase)
        => phase is "preroll" or "motion" or "still" or "done" ? new Dictionary<string, string> { ["t"] = "scene", ["phase"] = phase } : throw new ArgumentException("Unknown scene phase.", nameof(phase));
}
