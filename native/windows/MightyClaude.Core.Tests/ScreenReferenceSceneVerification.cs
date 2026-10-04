using MightyClaude.Core;

internal static class ScreenReferenceSceneVerification
{
    internal static Task TimingAndStillFrame()
    {
        void Check(bool value) { if (!value) throw new InvalidOperationException("Measurement scene diverges from macOS timing."); }
        Check(ScreenShareReferenceScene.At(-1) == new ScreenShareReferenceScene.Moment("preroll", 3, 0));
        Check(ScreenShareReferenceScene.At(double.NaN) == ScreenShareReferenceScene.At(0));
        Check(ScreenShareReferenceScene.At(3) == new ScreenShareReferenceScene.Moment("motion", 60, 0));
        Check(ScreenShareReferenceScene.At(62.5) == new ScreenShareReferenceScene.Moment("motion", 1, 59.5));
        Check(ScreenShareReferenceScene.At(63) == new ScreenShareReferenceScene.Moment("still", 30, 60));
        Check(ScreenShareReferenceScene.At(93) == new ScreenShareReferenceScene.Moment("done", 0, 60));
        foreach (var elapsed in new[] { 63.0, 70, 80, 92.9 })
        {
            var moment = ScreenShareReferenceScene.At(elapsed); Check(moment.MotionElapsed == 60);
            Check(ScreenShareReferenceScene.TypedCount(moment.MotionElapsed) == 1080 && ScreenShareReferenceScene.ScrollOffset(moment.MotionElapsed, 6000) == 5400);
        }
        foreach (var phase in new[] { "preroll", "motion", "still", "done" }) Check(ScreenShareReferenceScene.Announcement(phase)["phase"] == phase);
        return Task.CompletedTask;
    }
}
