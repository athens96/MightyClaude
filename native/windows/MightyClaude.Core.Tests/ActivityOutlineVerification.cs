using MightyClaude.Core;

/// <summary>
/// The running block's moving dashed outline in the Windows Mighty diagram, with the rules
/// of macOS <c>MightyGraphActivityStyle</c> / <c>MightyGraphActivityOutline</c>: 9/7 dashes
/// fitted to the card, one period per 1.6 s, still under reduced motion, amber while waiting.
/// </summary>
internal static class ActivityOutlineVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static bool Near(double a, double b) => Math.Abs(a - b) < 1e-9;

    internal static Task RunningMarchesWaitingStaysStillAndFinishedHasNone()
    {
        Check(MightyGraphActivity.Outline("running", true) == MightyGraphActivity.Marching, "running must march");
        Check(MightyGraphActivity.Outline("starting", true) == MightyGraphActivity.Marching, "starting buckets as running");
        Check(MightyGraphActivity.Outline("queued", true) == MightyGraphActivity.Marching, "queued buckets as running");
        Check(MightyGraphActivity.Outline("running", false) == MightyGraphActivity.Solid, "reduced motion holds a solid line");
        Check(MightyGraphActivity.Outline("waiting", true) == MightyGraphActivity.Waiting, "waiting keeps the amber line");
        Check(MightyGraphActivity.Outline("waiting", false) == MightyGraphActivity.Waiting, "waiting is the same under reduced motion");
        foreach (var done in new[] { "completed", "error", "failed", "stopped", "cancelled", "interrupted" })
            Check(MightyGraphActivity.Outline(done, true) == MightyGraphActivity.None, done + " has no outline");
        Check(MightyGraphActivity.StrokeHex(MightyGraphActivity.Marching) == "#2A5FEE", "run blue");
        Check(MightyGraphActivity.StrokeHex(MightyGraphActivity.Waiting) == "#FFA81F", "wait amber");
        Check(MightyGraphActivity.HaloHex(MightyGraphActivity.Marching, false) == "#E6EDFF"
            && MightyGraphActivity.HaloHex(MightyGraphActivity.Solid, true) == "#1A2750", "run halo per mode");
        Check(MightyGraphActivity.HaloHex(MightyGraphActivity.Waiting, false) is null, "waiting has no halo");
        return Task.CompletedTask;
    }

    internal static Task DashesAreNineSevenAndFitTheOutline()
    {
        Check(MightyGraphActivity.Dash.SequenceEqual([9.0, 7.0]), "dash must be 9/7");
        Check(Near(MightyGraphActivity.Perimeter(100, 50, 0), 300), "square perimeter");
        Check(Near(MightyGraphActivity.Perimeter(20, 20, 10), 2 * Math.PI * 10), "a full circle");
        Check(Near(MightyGraphActivity.Perimeter(20, 10, 30), MightyGraphActivity.Perimeter(20, 10, 5)), "radius is clamped to half the short side");
        foreach (var perimeter in new[] { 100.0, 333.3, 517.9, 1234.5 })
        {
            var dash = MightyGraphActivity.DashFitting(perimeter);
            var periods = perimeter / dash.Sum();
            Check(Near(periods, Math.Round(periods)), "a whole number of periods must fit " + perimeter);
            Check(Near(dash[0] / dash[1], 9.0 / 7), "the 9:7 ratio is kept at " + perimeter);
        }
        Check(MightyGraphActivity.DashFitting(0).SequenceEqual([9.0, 7.0]), "an empty outline keeps the plain dash");
        Check(Near(MightyGraphActivity.DashFitting(4).Sum(), 4), "a tiny outline still fits one period");
        // The card's outline is the card inset by 1pt with an 11pt radius.
        var card = MightyGraphActivity.DashForCard(300, 200);
        var inner = MightyGraphActivity.Perimeter(298, 198, 11);
        Check(Near(inner / card.Sum(), Math.Round(inner / card.Sum())), "the card dash fits the inset outline");
        return Task.CompletedTask;
    }

    internal static Task OnePeriodPassesEveryOnePointSixSecondsAndStaysStillUnderReducedMotion()
    {
        Check(MightyGraphActivity.Period == TimeSpan.FromSeconds(1.6), "the cycle is 1.6 s");
        Check(Near(MightyGraphActivity.Phase(TimeSpan.FromSeconds(0.4), true), 0.25), "a quarter cycle");
        Check(Near(MightyGraphActivity.Phase(TimeSpan.FromSeconds(2.0), true), 0.25), "the cycle wraps");
        Check(Near(MightyGraphActivity.Phase(TimeSpan.FromSeconds(1.6), true), 0), "a whole cycle is back at 0");
        Check(MightyGraphActivity.Phase(TimeSpan.FromSeconds(0.4), false) == 0, "reduced motion holds still");
        var dash = MightyGraphActivity.DashFitting(512);
        Check(MightyGraphActivity.DashOffset(0, dash) == 0, "the cycle starts at offset 0");
        Check(Near(MightyGraphActivity.DashOffset(1, dash), -dash.Sum()), "one cycle walks exactly one period forward");
        Check(Near(MightyGraphActivity.DashOffset(0.5, dash), -dash.Sum() / 2), "half a cycle, half a period");
        return Task.CompletedTask;
    }

    internal static Task DiagramBlocksCarryTheirOutline()
    {
        var run = new MightyGraphRun { Id = "outline-run", Input = "hi", Status = "running" };
        var layout = MightyGraphLayout.Make([run], "", true, new HashSet<string>());
        Check(Request(layout, run, true).Outline == MightyGraphActivity.Marching, "a running request marches");
        Check(Request(layout, run, false).Outline == MightyGraphActivity.Solid, "with animations off it is solid");
        run.Status = "waiting";
        Check(Request(layout, run, true).Outline == MightyGraphActivity.Waiting, "a waiting request is amber");
        run.Status = "completed";
        var finished = MightyGraphBlockModel.Blocks(MightyGraphLayout.Make([run], "", false, new HashSet<string>()), [run], "", "Claude", true);
        Check(finished.All(b => b.Outline == MightyGraphActivity.None), "finished blocks, the draft and the result have no outline");
        return Task.CompletedTask;
    }

    private static MightyGraphBlock Request(MightyGraphLayout layout, MightyGraphRun run, bool animations) =>
        MightyGraphBlockModel.Blocks(layout, [run], "", "Claude", animations).Single(b => b.Kind == "request");
}
