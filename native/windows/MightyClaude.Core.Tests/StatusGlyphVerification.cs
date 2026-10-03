using MightyClaude.Core;

/// <summary>
/// Status v2 (concept A, the glyph row) on Windows: one glyph per state, shared by the sidebar
/// rows, the pane tabs and the pane header, with the macOS colours and 16-unit geometry.
/// Mirrors native/macos/Tests/MightyCoreTests/StatusGlyphTests.swift.
/// </summary>
internal static class StatusGlyphVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    internal static Task EachStateHasItsOwnGlyph()
    {
        Check(StatusGlyph.Kind(DesignTone.Run) == StatusGlyphKind.Spark, "running must be the spark");
        Check(StatusGlyph.Kind(DesignTone.Wait) == StatusGlyphKind.Question, "waiting must be the ? disc");
        Check(StatusGlyph.Kind(DesignTone.Done) == StatusGlyphKind.Check, "finished must be the check");
        Check(StatusGlyph.Kind(DesignTone.Stop) == StatusGlyphKind.SlashedRing, "stopped must be the slashed ring");
        Check(StatusGlyph.Kind(DesignTone.Err) == StatusGlyphKind.Exclamation, "error must be the ! disc");
        Check(StatusGlyph.Kind(DesignTone.Idle) == StatusGlyphKind.Ring, "an idle agent must be the ring");
        var glyphs = Enum.GetValues<DesignTone>().Select(t => StatusGlyph.Kind(t)).ToHashSet();
        Check(glyphs.Count == Enum.GetValues<DesignTone>().Length, "every tone must have its own glyph");
        return Task.CompletedTask;
    }

    internal static Task APaneThatIsNotAnAgentsShowsItsOwnSymbolOnlyWhileIdle()
    {
        Check(StatusGlyph.Kind(DesignTone.Idle, "shell") == StatusGlyphKind.Pane, "an idle shell shows its own symbol");
        Check(StatusGlyph.Kind(DesignTone.Idle, "browser") == StatusGlyphKind.Pane, "an idle browser shows its own symbol");
        Check(StatusGlyph.Kind(DesignTone.Idle, "files") == StatusGlyphKind.Pane, "an idle files pane shows its own symbol");
        Check(StatusGlyph.Kind(DesignTone.Idle, "claude") == StatusGlyphKind.Ring, "an idle agent shows the ring");
        Check(StatusGlyph.Kind(DesignTone.Run, "shell") == StatusGlyphKind.Spark, "a running shell shows the spark");
        Check(StatusGlyph.Kind(DesignTone.Err, "shell") == StatusGlyphKind.Exclamation, "a failed shell shows the ! disc");
        return Task.CompletedTask;
    }

    internal static Task StatusStringsReachTheirGlyphs()
    {
        Check(StatusGlyph.Kind("waiting") == StatusGlyphKind.Question, "waiting");
        Check(StatusGlyph.Kind("failed") == StatusGlyphKind.Exclamation, "failed");
        Check(StatusGlyph.Kind("cancelled") == StatusGlyphKind.SlashedRing, "cancelled");
        Check(StatusGlyph.Kind("interrupted") == StatusGlyphKind.SlashedRing, "interrupted");
        Check(StatusGlyph.Kind("something-new") == StatusGlyphKind.Ring, "an unknown status is idle");
        // A running pane with a request pending for the user shows the amber "?", as on macOS.
        Check(StatusGlyph.Kind("running", pendingRequests: 1) == StatusGlyphKind.Question, "a pending request turns running into waiting");
        Check(StatusGlyph.Kind("running", pendingRequests: 0) == StatusGlyphKind.Spark, "no pending request keeps the spark");
        Check(StatusGlyph.DisplayStatus("completed", 0) == "completed", "display status keeps the pane's own status");
        return Task.CompletedTask;
    }

    internal static Task OnlyTheSparkTurnsAndOnlyWaitAndErrorAreDiscs()
    {
        var all = Enum.GetValues<StatusGlyphKind>();
        Check(all.Where(g => g.Turns()).SequenceEqual([StatusGlyphKind.Spark]), "only the spark turns");
        Check(all.Where(g => g.IsDisc()).ToHashSet().SetEquals([StatusGlyphKind.Question, StatusGlyphKind.Exclamation]), "only ? and ! are discs");
        Check(StatusGlyph.SparkTurnSeconds == 3.2, "the spark turns once every 3.2 s");
        return Task.CompletedTask;
    }

    internal static Task GlyphColoursFollowTheMacPalette()
    {
        Check(StatusGlyph.GlyphHex(DesignTone.Run, false) == "#2A5FEE" && StatusGlyph.GlyphHex(DesignTone.Done, false) == "#08804A" && StatusGlyph.GlyphHex(DesignTone.Stop, false) == "#667085", "day line marks take the tone's fill");
        Check(StatusGlyph.GlyphHex(DesignTone.Run, true) == "#7FA3FF" && StatusGlyph.GlyphHex(DesignTone.Done, true) == "#5BD49A" && StatusGlyph.GlyphHex(DesignTone.Stop, true) == "#A9B1C2", "night line marks take the tone's pale ink");
        Check(StatusGlyph.GlyphHex(DesignTone.Idle, false) == "#4F5869" && StatusGlyph.GlyphHex(DesignTone.Idle, true) == "#A9B1C2", "idle is the sidebar's quiet ink");
        foreach (var dark in new[] { false, true })
        {
            Check(StatusGlyph.GlyphHex(DesignTone.Wait, dark) == "#FFA81F" && StatusGlyph.GlyphHex(DesignTone.Err, dark) == "#D42F22", "the discs are the same amber and red in both modes");
        }
        Check(StatusGlyph.DiscFill(DesignTone.Wait) == "#FFA81F" && StatusGlyph.DiscInk(DesignTone.Wait) == "#2B1B00", "amber disc with its dark ink");
        Check(StatusGlyph.DiscFill(DesignTone.Err) == "#D42F22" && StatusGlyph.DiscInk(DesignTone.Err) == "#FFFFFF", "red disc with white ink");
        // The two discs' inks hold 4.5:1 on their discs (macOS discMarksClearTheirDiscs).
        foreach (var tone in new[] { DesignTone.Wait, DesignTone.Err })
        {
            var ratio = Contrast(StatusGlyph.DiscInk(tone), StatusGlyph.DiscFill(tone));
            Check(ratio >= 4.5, $"disc ink on {tone} is {ratio:0.00}");
        }
        return Task.CompletedTask;
    }

    internal static Task GlyphGeometryMatchesTheMockup()
    {
        Check(StatusGlyph.Strokes(StatusGlyphKind.Spark).Count == 4 && StatusGlyph.SparkDiagonals.Count == 4, "the spark has four arms and four fainter diagonals");
        Check(StatusGlyph.Strokes(StatusGlyphKind.Check) is [{ X: 3.2, Y: 8.5, Segments: [GlyphLine { X: 6.2, Y: 11.5 }, GlyphLine { X: 12.8, Y: 4.5 }] }], "the check is the mockup's polyline");
        Check(StatusGlyph.Strokes(StatusGlyphKind.Ring) is [{ Closed: true, Segments: [GlyphArc { Radius: 3.6 }, GlyphArc { Radius: 3.6 }] }], "the idle ring has radius 3.6");
        Check(StatusGlyph.Strokes(StatusGlyphKind.SlashedRing) is [{ Segments: [GlyphArc { Radius: 6 }, GlyphArc { Radius: 6 }] }, { X: 3.9, Y: 12.1 }], "the slashed ring is a radius-6 ring and its slash");
        Check(StatusGlyph.Strokes(StatusGlyphKind.Question) is [{ X: 5.9, Y: 6.1, Segments: [GlyphArc { X: 8.8, Y: 8.05, Radius: 2.1, LargeArc: true }, GlyphCurve, GlyphLine { X: 8, Y: 9.55 }] }], "the ? hook follows the mockup");
        Check(StatusGlyph.Dot(StatusGlyphKind.Question) == (8, 11.7, 1.05) && StatusGlyph.Dot(StatusGlyphKind.Exclamation) == (8, 11.6, 1.05), "the discs' dots");
        Check(StatusGlyph.Dot(StatusGlyphKind.Check) is null && StatusGlyph.Strokes(StatusGlyphKind.Pane).Count == 0, "line marks have no dot; the pane symbol has no strokes");
        foreach (var glyph in Enum.GetValues<StatusGlyphKind>())
            foreach (var figure in StatusGlyph.Strokes(glyph))
                Check(figure.Segments.All(s => s.X is >= 0 and <= 16 && s.Y is >= 0 and <= 16) && figure.X is >= 0 and <= 16 && figure.Y is >= 0 and <= 16, $"{glyph} stays on the 16-unit grid");
        return Task.CompletedTask;
    }

    internal static Task StatusWordsUseTheSharedLocaleKeys()
    {
        var keys = Enum.GetValues<DesignTone>().Select(StatusGlyph.WordKey).ToList();
        Check(keys.Distinct().Count() == keys.Count, "every tone has its own word");
        foreach (var key in keys) Check(Locale.Get(key) != key, $"{key} must be in the shared locale files");
        Check(StatusGlyph.WordKey(DesignTone.Wait) == "phone.dashboard.stat.waiting", "the waiting word is the macOS one");
        return Task.CompletedTask;
    }

    private static double Contrast(string a, string b)
    {
        static double Luminance(string hex)
        {
            static double Channel(int value) { var c = value / 255.0; return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4); }
            var rgb = Convert.ToInt32(hex[1..], 16);
            return 0.2126 * Channel(rgb >> 16 & 0xFF) + 0.7152 * Channel(rgb >> 8 & 0xFF) + 0.0722 * Channel(rgb & 0xFF);
        }
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }
}
