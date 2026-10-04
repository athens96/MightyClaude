namespace MightyClaude.Core;

/// <summary>
/// A pane's state as one of six tones, read from its status word. Mirrors macOS
/// <c>DesignTone(status:)</c> in MightyCore/DesignTokens.swift.
/// </summary>
public enum DesignTone { Run, Wait, Done, Err, Stop, Idle }

/// <summary>
/// The small mark in front of a pane's title (status v2, concept A, the glyph row): one glyph
/// per state, the same in the sidebar rows, the pane tabs and the pane header. Only the
/// amber "?" and the red "!" are filled discs; the rest are line marks, so the colour left
/// on screen is what wants a look. Replaces the macOS concept-D coloured edge cards and
/// the status-filled hero; on Windows it replaces the spinning ring and the bare status
/// word. Mirrors macOS <c>StatusGlyphKind</c> (MightyCore/StatusGlyph.swift).
/// </summary>
public enum StatusGlyphKind
{
    /// <summary>Running: an eight-armed spark that turns slowly (still when animations are off).</summary>
    Spark,
    /// <summary>Waiting on the user: the amber disc with a "?".</summary>
    Question,
    /// <summary>Finished: a check.</summary>
    Check,
    /// <summary>Stopped by the user: a ring with a slash.</summary>
    SlashedRing,
    /// <summary>Stopped by an error: the red disc with a "!".</summary>
    Exclamation,
    /// <summary>An idle agent pane: a small ring.</summary>
    Ring,
    /// <summary>An idle pane that is not an agent's (a shell, a browser, the files pane): the pane's own symbol.</summary>
    Pane,
}

public static class StatusGlyph
{
    public static DesignTone Tone(string status) => status switch
    {
        "running" => DesignTone.Run,
        "waiting" => DesignTone.Wait,
        "completed" => DesignTone.Done,
        "error" or "failed" => DesignTone.Err,
        "stopped" or "cancelled" or "interrupted" => DesignTone.Stop,
        _ => DesignTone.Idle,
    };

    /// <summary>
    /// What a pane shows as its state: "waiting" while it has requests pending for the user
    /// (macOS <c>WorkDashboard.displayStatus</c>), else its own status.
    /// </summary>
    public static string DisplayStatus(string status, int pendingRequests) => pendingRequests > 0 ? "waiting" : status;

    /// <summary>
    /// The glyph for a pane's tone. A pane that is not an agent's ("claude" kind) shows its own
    /// symbol while it is idle; any other state shows the state's glyph.
    /// </summary>
    public static StatusGlyphKind Kind(DesignTone tone, string kind = "claude") => tone switch
    {
        DesignTone.Run => StatusGlyphKind.Spark,
        DesignTone.Wait => StatusGlyphKind.Question,
        DesignTone.Done => StatusGlyphKind.Check,
        DesignTone.Stop => StatusGlyphKind.SlashedRing,
        DesignTone.Err => StatusGlyphKind.Exclamation,
        _ => kind == "claude" ? StatusGlyphKind.Ring : StatusGlyphKind.Pane,
    };

    public static StatusGlyphKind Kind(string status, string kind = "claude", int pendingRequests = 0) => Kind(Tone(DisplayStatus(status, pendingRequests)), kind);

    /// <summary>Only the running spark moves.</summary>
    public static bool Turns(this StatusGlyphKind glyph) => glyph == StatusGlyphKind.Spark;

    /// <summary>A filled disc carrying its own ink, rather than a line mark.</summary>
    public static bool IsDisc(this StatusGlyphKind glyph) => glyph is StatusGlyphKind.Question or StatusGlyphKind.Exclamation;

    /// <summary>The spark turns once every 3.2 s, as on macOS.</summary>
    public const double SparkTurnSeconds = 3.2;

    /// <summary>The mark's size in the sidebar rows; the tabs draw <see cref="TabSize"/>, the pane header <see cref="HeaderSize"/>.</summary>
    public const double RowSize = 14;
    /// <summary>The pane header's mark: 14, as in the sidebar (design doc §5, M/SessionPaneView.swift:216).</summary>
    public const double HeaderSize = 14;
    /// <summary>A tab's mark (M/PaneDockView.swift:249).</summary>
    public const double TabSize = 12;

    /// <summary>A tone's status word: the glyph's accessible name and the pane header's word (shared locale keys).</summary>
    public static string WordKey(DesignTone tone) => tone switch
    {
        DesignTone.Run => "session.state.running",
        DesignTone.Wait => "phone.dashboard.stat.waiting",
        DesignTone.Done => "session.state.completed",
        DesignTone.Err => "session.state.error",
        DesignTone.Stop => "session.state.stopped",
        _ => "session.state.idle",
    };

    /// <summary>
    /// The colour of a glyph's lines (#RRGGBB), from the design palette
    /// (<see cref="DesignPalette.Glyph"/>, macOS <c>DesignPalette.glyph</c>): by day a line mark takes
    /// the tone's fill, by night the tone's pale ink; the two discs are <see cref="DiscFill"/>; idle
    /// is the sidebar's quiet ink.
    /// </summary>
    public static string GlyphHex(DesignTone tone, bool dark) => DesignTokens.Palette(dark).Glyph(tone).Hex;

    /// <summary>The disc behind the "?" (amber) and the "!" (red); the same in both modes.</summary>
    public static string DiscFill(DesignTone tone) => DesignTokens.Light.DiscFill(tone).Hex;

    /// <summary>The "?" or "!" drawn on its disc: the amber takes its own dark ink, the red white.</summary>
    public static string DiscInk(DesignTone tone) => DesignTokens.Light.DiscInk(tone).Hex;

    /// <summary>
    /// The glyph's strokes on its 16-unit grid (the mockup's SVG), drawn with round caps and
    /// joins at <see cref="StrokeWidth"/>. A disc glyph also has its disc (<see cref="DiscRadius"/>,
    /// centred) and dot (<see cref="Dot"/>); the spark's fainter diagonals are
    /// <see cref="SparkDiagonals"/>. Empty for <see cref="StatusGlyphKind.Pane"/>, which draws
    /// the pane's own symbol.
    /// </summary>
    public static IReadOnlyList<GlyphFigure> Strokes(StatusGlyphKind glyph) => glyph switch
    {
        StatusGlyphKind.Spark => [Line(8, 1.6, 8, 5.6), Line(8, 10.4, 8, 14.4), Line(1.6, 8, 5.6, 8), Line(10.4, 8, 14.4, 8)],
        // The hook of the "?": M5.9 6.1 a2.1 2.1 0 1 1 2.9 1.95 c-.5.22-.8.6-.8 1.15 v.35.
        StatusGlyphKind.Question => [new(5.9, 6.1, [new GlyphArc(8.8, 8.05, 2.1, LargeArc: true), new GlyphCurve(8.3, 8.27, 8, 8.65, 8, 9.2), new GlyphLine(8, 9.55)])],
        StatusGlyphKind.Check => [new(3.2, 8.5, [new GlyphLine(6.2, 11.5), new GlyphLine(12.8, 4.5)])],
        StatusGlyphKind.SlashedRing => [Circle(6), Line(3.9, 12.1, 12.1, 3.9)],
        StatusGlyphKind.Exclamation => [Line(8, 4.3, 8, 8.9)],
        StatusGlyphKind.Ring => [Circle(3.6)],
        _ => [],
    };

    public static readonly IReadOnlyList<GlyphFigure> SparkDiagonals = [Line(4.6, 4.6, 6.1, 6.1), Line(9.9, 9.9, 11.4, 11.4), Line(4.6, 11.4, 6.1, 9.9), Line(9.9, 6.1, 11.4, 4.6)];

    /// <summary>The spark's diagonals are drawn at this opacity.</summary>
    public const double SparkDiagonalOpacity = 0.7;

    private static GlyphFigure Line(double x1, double y1, double x2, double y2) => new(x1, y1, [new GlyphLine(x2, y2)]);

    /// <summary>A ring about the grid's centre, as two clockwise half arcs.</summary>
    private static GlyphFigure Circle(double radius) => new(8, 8 - radius, [new GlyphArc(8, 8 + radius, radius), new GlyphArc(8, 8 - radius, radius)], Closed: true);

    public static double StrokeWidth(StatusGlyphKind glyph) => glyph switch
    {
        StatusGlyphKind.Spark => 2,
        StatusGlyphKind.Question => 1.7,
        StatusGlyphKind.Exclamation => 1.9,
        _ => 1.8,
    };

    /// <summary>The disc of the "?" and "!": inset 0.8 of the 16-unit grid.</summary>
    public const double DiscRadius = 7.2;

    /// <summary>The dot of the "?" or "!" on the 16-unit grid (centre and radius); null for a line mark.</summary>
    public static (double X, double Y, double Radius)? Dot(StatusGlyphKind glyph) => glyph switch
    {
        StatusGlyphKind.Question => (8, 11.7, 1.05),
        StatusGlyphKind.Exclamation => (8, 11.6, 1.05),
        _ => null,
    };
}

/// <summary>One stroke of a status glyph on its 16-unit grid: a start point and the segments after it.</summary>
public sealed record GlyphFigure(double X, double Y, IReadOnlyList<GlyphSegment> Segments, bool Closed = false);

public abstract record GlyphSegment(double X, double Y);
/// <summary>A straight line to (X, Y).</summary>
public sealed record GlyphLine(double X, double Y) : GlyphSegment(X, Y);
/// <summary>A clockwise (y down) circular arc to (X, Y).</summary>
public sealed record GlyphArc(double X, double Y, double Radius, bool LargeArc = false) : GlyphSegment(X, Y);
/// <summary>A cubic curve to (X, Y) through two control points.</summary>
public sealed record GlyphCurve(double X1, double Y1, double X2, double Y2, double X, double Y) : GlyphSegment(X, Y);
