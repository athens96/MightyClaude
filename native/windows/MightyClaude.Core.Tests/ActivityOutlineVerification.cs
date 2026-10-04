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

    /// <summary>
    /// The colours are token reads (the Mac's run, wait and runSoft), the dash WinUI draws is the
    /// card's fitted [9,7] in 2pt stroke widths, and a whole number of periods still goes round.
    /// </summary>
    internal static Task OutlinesReadTokensAndDashInStrokeUnits()
    {
        Check(MightyGraphActivity.StrokeToken(MightyGraphActivity.Marching) == DesignToken.Run && MightyGraphActivity.StrokeToken(MightyGraphActivity.Solid) == DesignToken.Run, "running lines are run");
        Check(MightyGraphActivity.StrokeToken(MightyGraphActivity.Waiting) == DesignToken.Wait, "the waiting line is wait");
        Check(MightyGraphActivity.HaloToken(MightyGraphActivity.Marching) == DesignToken.RunSoft && MightyGraphActivity.HaloToken(MightyGraphActivity.Solid) == DesignToken.RunSoft, "the run halo is runSoft");
        Check(MightyGraphActivity.HaloToken(MightyGraphActivity.Waiting) is null && MightyGraphActivity.HaloToken(MightyGraphActivity.None) is null, "no halo while waiting or finished");
        foreach (var (w, h) in new[] { (300.0, 200.0), (420.0, 137.5), (612.3, 288.8) })
        {
            var dash = MightyGraphActivity.DashForCard(w, h);
            var units = DesignMetrics.Dash.InStrokeUnits(dash, MightyGraphActivity.LineWidth);
            Check(units.Length == 2 && Near(units[0] * MightyGraphActivity.LineWidth, dash[0]) && Near(units[1] * MightyGraphActivity.LineWidth, dash[1]), $"{w}x{h}: stroke units times the 2pt width give the pt dash back");
            var perimeter = MightyGraphActivity.Perimeter(w - MightyGraphActivity.LineWidth, h - MightyGraphActivity.LineWidth, MightyGraphActivity.CornerRadius - 1);
            var periods = perimeter / ((units[0] + units[1]) * MightyGraphActivity.LineWidth);
            Check(Math.Abs(periods - Math.Round(periods)) < 1e-9, $"{w}x{h}: a whole number of periods goes round the card; got {periods}");
        }
        var draft = DesignMetrics.Dash.InStrokeUnits(DesignMetrics.Dash.Draft, DesignMetrics.Dash.DraftWidth);
        Check(Near(draft[0], 5 / 1.5) && Near(draft[1], 4 / 1.5), "the draft dash is [5/1.5, 4/1.5] stroke units");
        return Task.CompletedTask;
    }

    /// <summary>macOS MightyGraphActivityIndicator: four 3pt capsules 2 apart in 18×14, 4 to 12 tall, a fifth of a cycle apart.</summary>
    internal static Task ActivityMarkIsFourCapsulesInAWave()
    {
        Check(MightyGraphActivity.Bars == 4, "four capsules");
        Check(Near(MightyGraphActivity.Bars * MightyGraphActivity.BarWidth + (MightyGraphActivity.Bars - 1) * MightyGraphActivity.BarGap, MightyGraphActivity.BarBoxWidth), "four 3pt capsules 2 apart fill the 18pt box");
        Check(Near(MightyGraphActivity.BarBoxHeight, 14) && Near(MightyGraphActivity.BarFramesPerSecond, 24), "14 tall, 24 frames a second");
        // 4 + 8 × (sin((phase + i/5) · 2π) + 1) / 2, as the Mac's barHeight.
        Check(Near(MightyGraphActivity.BarHeight(0, 0), 8), "the first capsule starts half way");
        Check(Near(MightyGraphActivity.BarHeight(0, .25), 12) && Near(MightyGraphActivity.BarHeight(0, .75), 4), "it rises to 12 and falls to 4");
        Check(Near(MightyGraphActivity.BarHeight(1, 0), MightyGraphActivity.BarHeight(0, .2)), "each capsule is a fifth of a cycle ahead of the one before");
        for (var i = 0; i < MightyGraphActivity.Bars; i++)
            for (var phase = 0.0; phase < 1; phase += .05)
                Check(MightyGraphActivity.BarHeight(i, phase) is >= 4 - 1e-9 and <= 12 + 1e-9 && MightyGraphActivity.BarHeight(i, phase) <= MightyGraphActivity.BarTallest + 1e-9, $"capsule {i} stays within 4..12 (never past BarTallest) at {phase}");
        return Task.CompletedTask;
    }

    /// <summary>macOS MightyGraphDotGrid: a dot every 18pt × zoom from the camera offset, radius max(0.6, zoom), none under a 6pt step.</summary>
    internal static Task DotGridFollowsTheZoomAndTheOffset()
    {
        Check(Near(MightyGraphDotGrid.Step(1), 18) && Near(MightyGraphDotGrid.Step(.5), 9) && Near(MightyGraphDotGrid.Step(1.5), 27), "the step is 18 × zoom");
        Check(MightyGraphDotGrid.Shown(.5) && MightyGraphDotGrid.Shown(1 / 3.0) && !MightyGraphDotGrid.Shown(.3), "hidden only under a 6pt step");
        Check(Near(MightyGraphDotGrid.Radius(.5), .6) && Near(MightyGraphDotGrid.Radius(1), 1) && Near(MightyGraphDotGrid.Radius(1.5), 1.5), "radius max(0.6, zoom)");
        Check(Near(MightyGraphDotGrid.First(0, 18), 0) && Near(MightyGraphDotGrid.First(40, 18), 4) && Near(MightyGraphDotGrid.First(-5, 18), 13), "the first dot at or after 0 lies on offset + n · step");
        Check(Near(MightyGraphDotGrid.First(36, 18), 0) && Near(MightyGraphDotGrid.First(-36, 18), 0), "a whole number of steps lands on 0");
        Check(MightyGraphDotGrid.First(double.NaN, 18) == 0 && MightyGraphDotGrid.First(5, 0) == 0, "a broken offset or step starts at 0");
        return Task.CompletedTask;
    }

    /// <summary>Each block carries the status its pill, strip and incoming edge take; the draft and the files panel none.</summary>
    internal static Task BlocksCarryTheirToneForThePillStripAndEdge()
    {
        var run = new MightyGraphRun { Id = "tone-run", Input = "hi", Status = "running", Agents = [new() { Id = "asker", Title = "Asker", Status = "waiting" }] };
        var blocks = MightyGraphBlockModel.Blocks(MightyGraphLayout.Make([run], "draft text", false, new HashSet<string>()), [run], "draft text", "Claude", true);
        Check(MightyGraphBlockModel.Tone(blocks.Single(b => b.Kind == "request")) == DesignTone.Run, "a running request is run");
        Check(MightyGraphBlockModel.Tone(blocks.Single(b => b.Id == MightyGraphLayout.NodeID(run, "agent:asker"))) == DesignTone.Wait, "a waiting sub-agent is wait");
        Check(blocks.Single(b => b.Kind == "draft").Status == "" && MightyGraphBlockModel.Tone(blocks.Single(b => b.Kind == "draft")) == DesignTone.Idle, "the draft has no status and stays idle");
        run.Status = "starting";
        Check(MightyGraphBlockModel.Tone(Request(MightyGraphLayout.Make([run], "", true, new HashSet<string>()), run, true)) == DesignTone.Run, "anything unfinished that is not waiting runs");
        foreach (var (status, tone) in new[] { ("completed", DesignTone.Done), ("failed", DesignTone.Err), ("interrupted", DesignTone.Stop) })
        {
            run.Status = status; run.Agents.Clear(); run.FinalOutput = "answer"; MightyGraphSupport.RefreshResult(run);
            var finished = MightyGraphBlockModel.Blocks(MightyGraphLayout.Make([run], "", false, new HashSet<string>()), [run], "", "Claude", true);
            Check(MightyGraphBlockModel.Tone(finished.Single(b => b.Kind == "result")) == tone, $"a {status} run's result strip is {tone}");
        }
        return Task.CompletedTask;
    }

    private static MightyGraphBlock Request(MightyGraphLayout layout, MightyGraphRun run, bool animations) =>
        MightyGraphBlockModel.Blocks(layout, [run], "", "Claude", animations).Single(b => b.Kind == "request");
}
