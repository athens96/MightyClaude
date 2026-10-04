namespace MightyClaude.Core;

/// <summary>
/// One opaque sRGB colour of the design palette, kept as plain data so the contrast rules can
/// be checked without WinUI. Mirrors macOS <c>DesignColor</c> (MightyCore/DesignTokens.swift).
/// </summary>
public readonly record struct DesignColor(byte R, byte G, byte B)
{
    public DesignColor(uint hex) : this((byte)((hex >> 16) & 0xFF), (byte)((hex >> 8) & 0xFF), (byte)(hex & 0xFF)) { }

    /// <summary>"#RRGGBB", upper case.</summary>
    public string Hex => $"#{R:X2}{G:X2}{B:X2}";

    /// <summary>WCAG 2.x relative luminance.</summary>
    public double RelativeLuminance => 0.2126 * Channel(R) + 0.7152 * Channel(G) + 0.0722 * Channel(B);

    /// <summary>WCAG 2.x contrast ratio, 1…21, whichever colour is lighter.</summary>
    public double Contrast(DesignColor other)
    {
        double a = RelativeLuminance, b = other.RelativeLuminance;
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    private static double Channel(byte value)
    {
        var c = value / 255.0;
        return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }
}

/// <summary>Every field of <see cref="DesignPalette"/>, by name: the key of the WinUI brush registry.</summary>
public enum DesignToken
{
    Page, Card, CardRaised, Sidebar, Line, Track,
    Ink, Ink2, Ink3, Accent, AccentSoft, OnAccent, SidebarInk2, SidebarAccent,
    Run, RunSoft, Wait, WaitSoft, OnWait, WaitText, Done, DoneSoft, DoneText, Err, ErrSoft, ErrText, Stop, StopSoft, StopText, Idle, OnStatus,
    Agent, AgentText, Task, TaskText, OnTask, SteerText, CompactText, QuestionText,
    CodeSurface, CodeText,
}

/// <summary>
/// The concept D ("card dashboard") palette for one appearance, field for field the macOS
/// <c>DesignPalette</c>. Surfaces (page, card, cardRaised, sidebar, the soft tints, codeSurface)
/// carry inks (ink*, accent, the *Text inks, the block inks, sidebarInk2/sidebarAccent) at
/// 4.5:1; fills (run…stop, idle, agent, task) are solid shapes carrying onStatus, onWait or
/// onTask, and marks at 3:1 on page, card and sidebar. Held to
/// native/contracts/fixtures/design-tokens.json on both platforms.
/// </summary>
public sealed record DesignPalette(
    DesignColor Page, DesignColor Card, DesignColor CardRaised, DesignColor Sidebar, DesignColor Line, DesignColor Track,
    DesignColor Ink, DesignColor Ink2, DesignColor Ink3, DesignColor Accent, DesignColor AccentSoft, DesignColor OnAccent,
    DesignColor SidebarInk2, DesignColor SidebarAccent,
    DesignColor Run, DesignColor RunSoft, DesignColor Wait, DesignColor WaitSoft, DesignColor OnWait, DesignColor WaitText,
    DesignColor Done, DesignColor DoneSoft, DesignColor DoneText, DesignColor Err, DesignColor ErrSoft, DesignColor ErrText,
    DesignColor Stop, DesignColor StopSoft, DesignColor StopText, DesignColor Idle, DesignColor OnStatus,
    DesignColor Agent, DesignColor AgentText, DesignColor Task, DesignColor TaskText, DesignColor OnTask,
    DesignColor SteerText, DesignColor CompactText, DesignColor QuestionText,
    DesignColor CodeSurface, DesignColor CodeText)
{
    public DesignColor this[DesignToken token] => token switch
    {
        DesignToken.Page => Page, DesignToken.Card => Card, DesignToken.CardRaised => CardRaised,
        DesignToken.Sidebar => Sidebar, DesignToken.Line => Line, DesignToken.Track => Track,
        DesignToken.Ink => Ink, DesignToken.Ink2 => Ink2, DesignToken.Ink3 => Ink3,
        DesignToken.Accent => Accent, DesignToken.AccentSoft => AccentSoft, DesignToken.OnAccent => OnAccent,
        DesignToken.SidebarInk2 => SidebarInk2, DesignToken.SidebarAccent => SidebarAccent,
        DesignToken.Run => Run, DesignToken.RunSoft => RunSoft,
        DesignToken.Wait => Wait, DesignToken.WaitSoft => WaitSoft, DesignToken.OnWait => OnWait, DesignToken.WaitText => WaitText,
        DesignToken.Done => Done, DesignToken.DoneSoft => DoneSoft, DesignToken.DoneText => DoneText,
        DesignToken.Err => Err, DesignToken.ErrSoft => ErrSoft, DesignToken.ErrText => ErrText,
        DesignToken.Stop => Stop, DesignToken.StopSoft => StopSoft, DesignToken.StopText => StopText,
        DesignToken.Idle => Idle, DesignToken.OnStatus => OnStatus,
        DesignToken.Agent => Agent, DesignToken.AgentText => AgentText,
        DesignToken.Task => Task, DesignToken.TaskText => TaskText, DesignToken.OnTask => OnTask,
        DesignToken.SteerText => SteerText, DesignToken.CompactText => CompactText, DesignToken.QuestionText => QuestionText,
        DesignToken.CodeSurface => CodeSurface, DesignToken.CodeText => CodeText,
        _ => throw new ArgumentOutOfRangeException(nameof(token), token, null),
    };

    /// <summary>The fill for a tone.</summary>
    public DesignColor Fill(DesignTone tone) => this[FillToken(tone)];

    /// <summary>The token <see cref="Fill"/> reads for a tone (the Mac's <c>heroFill</c>), so a brush registry can share one brush per tone fill.</summary>
    public static DesignToken FillToken(DesignTone tone) => tone switch
    {
        DesignTone.Run => DesignToken.Run, DesignTone.Wait => DesignToken.Wait, DesignTone.Done => DesignToken.Done,
        DesignTone.Err => DesignToken.Err, DesignTone.Stop => DesignToken.Stop, _ => DesignToken.Idle,
    };

    /// <summary>The ink drawn on a tone's fill (the Mac's <c>heroInk</c>): the amber takes its own dark ink, every other fill white.</summary>
    public static DesignToken FillInkToken(DesignTone tone) => tone == DesignTone.Wait ? DesignToken.OnWait : DesignToken.OnStatus;

    /// <summary>The text-safe ink for a tone: words and icons on page, card or raised strip.</summary>
    public DesignColor Text(DesignTone tone) => this[TextToken(tone)];

    /// <summary>The token <see cref="Text"/> reads for a tone, so a brush registry can share one brush per tone ink.</summary>
    public static DesignToken TextToken(DesignTone tone) => tone switch
    {
        DesignTone.Run => DesignToken.Accent, DesignTone.Wait => DesignToken.WaitText, DesignTone.Done => DesignToken.DoneText,
        DesignTone.Err => DesignToken.ErrText, DesignTone.Stop => DesignToken.StopText, _ => DesignToken.Ink2,
    };

    /// <summary>The soft tint behind a tone's ink.</summary>
    public DesignColor Soft(DesignTone tone) => this[SoftToken(tone)];

    /// <summary>The token <see cref="Soft"/> reads for a tone, so a brush registry can share one brush per tone tint.</summary>
    public static DesignToken SoftToken(DesignTone tone) => tone switch
    {
        DesignTone.Run => DesignToken.RunSoft, DesignTone.Wait => DesignToken.WaitSoft, DesignTone.Done => DesignToken.DoneSoft,
        DesignTone.Err => DesignToken.ErrSoft, _ => DesignToken.StopSoft,
    };

    /// <summary>A small mark (dot, node) for a tone: the fill where it holds 3:1, the ink for amber, the quiet ink for idle.</summary>
    public DesignColor Mark(DesignTone tone) => tone switch
    {
        DesignTone.Wait => WaitText, DesignTone.Idle => Ink3, _ => Fill(tone),
    };

    /// <summary>Whether this is the night palette: its words are lighter than its cards.</summary>
    public bool IsDark => Ink.RelativeLuminance > Card.RelativeLuminance;

    /// <summary>The track of a segmented switch on a card: the rail grey by day, the page navy by night.</summary>
    public DesignColor SegmentTrack => IsDark ? Page : Track;

    /// <summary>The chosen side of that switch: a card chip by day, the idle slate by night.</summary>
    public DesignColor SegmentOn => IsDark ? Idle : Card;

    /// <summary>
    /// The colour of a status glyph's lines (macOS <c>DesignPalette.glyph</c>): by day a line mark
    /// takes the tone's fill, by night the tone's pale ink; the two discs are <see cref="DiscFill"/>;
    /// idle is the sidebar's quiet ink.
    /// </summary>
    public DesignColor Glyph(DesignTone tone) => tone switch
    {
        DesignTone.Run or DesignTone.Done or DesignTone.Stop => IsDark ? Text(tone) : Fill(tone),
        DesignTone.Wait or DesignTone.Err => DiscFill(tone),
        _ => SidebarInk2,
    };

    /// <summary>The disc behind the "?" (amber) and the "!" (red); the same in both modes.</summary>
    public DesignColor DiscFill(DesignTone tone) => tone == DesignTone.Wait ? Wait : Err;

    /// <summary>The "?" or "!" drawn on its disc: the amber takes its own dark ink, the red white.</summary>
    public DesignColor DiscInk(DesignTone tone) => tone == DesignTone.Wait ? OnWait : OnStatus;
}

/// <summary>The two palettes, value for value macOS <c>DesignTokens</c> (MightyCore/DesignTokens.swift:181-217).</summary>
public static class DesignTokens
{
    public static readonly DesignPalette Light = new(
        Page: new(0xECEEF3), Card: new(0xFFFFFF), CardRaised: new(0xF5F7FB),
        Sidebar: new(0xE2E6ED), Line: new(0xDEE2EA), Track: new(0xDDE1E9),
        Ink: new(0x0E1320), Ink2: new(0x5A6377), Ink3: new(0x616A7C),
        Accent: new(0x2459E6), AccentSoft: new(0xE6EDFF), OnAccent: new(0xFFFFFF),
        SidebarInk2: new(0x4F5869), SidebarAccent: new(0x1F4FD1),
        Run: new(0x2A5FEE), RunSoft: new(0xE6EDFF),
        Wait: new(0xFFA81F), WaitSoft: new(0xFFF3DE), OnWait: new(0x2B1B00), WaitText: new(0x8A5300),
        Done: new(0x08804A), DoneSoft: new(0xE2F6EA), DoneText: new(0x06703F),
        Err: new(0xD42F22), ErrSoft: new(0xFDE6E4), ErrText: new(0xB42318),
        Stop: new(0x667085), StopSoft: new(0xEEF0F4), StopText: new(0x4F5869),
        Idle: new(0x0E1320), OnStatus: new(0xFFFFFF),
        Agent: new(0x8A5CF6), AgentText: new(0x6D3FD9),
        Task: new(0x0EA5B7), TaskText: new(0x0A7480), OnTask: new(0x0E1320),
        SteerText: new(0xA33D8F), CompactText: new(0x4B5BB8), QuestionText: new(0x8A5300),
        CodeSurface: new(0x0E1320), CodeText: new(0xD8DEEA));

    /// <summary>The night side: ink navy page (never black), slate cards; the inks lift to pale tints, the fills stay.</summary>
    public static readonly DesignPalette Dark = new(
        Page: new(0x0B0F19), Card: new(0x151B29), CardRaised: new(0x1D2435),
        Sidebar: new(0x0F1420), Line: new(0x283043), Track: new(0x1D2435),
        Ink: new(0xEEF1F7), Ink2: new(0xA9B1C2), Ink3: new(0x8E97AA),
        Accent: new(0x7FA3FF), AccentSoft: new(0x1A2750), OnAccent: new(0x0B0F19),
        SidebarInk2: new(0xA9B1C2), SidebarAccent: new(0x7FA3FF),
        Run: new(0x2A5FEE), RunSoft: new(0x1A2750),
        Wait: new(0xFFA81F), WaitSoft: new(0x3A2A0D), OnWait: new(0x2B1B00), WaitText: new(0xFFC45C),
        Done: new(0x08804A), DoneSoft: new(0x0F2E22), DoneText: new(0x5BD49A),
        Err: new(0xD42F22), ErrSoft: new(0x3A1A18), ErrText: new(0xFF8A80),
        Stop: new(0x667085), StopSoft: new(0x222939), StopText: new(0xA9B1C2),
        Idle: new(0x2A3347), OnStatus: new(0xFFFFFF),
        Agent: new(0x8A5CF6), AgentText: new(0xB9A0FF),
        Task: new(0x0EA5B7), TaskText: new(0x5ED3E0), OnTask: new(0x0E1320),
        SteerText: new(0xE58FD0), CompactText: new(0x9AA6F5), QuestionText: new(0xFFC45C),
        CodeSurface: new(0x05070D), CodeText: new(0xD8DEEA));

    /// <summary>The app's own theme setting (<c>snapshot.Theme</c>): only "light" is light, as on macOS.</summary>
    public static DesignPalette Palette(string theme) => theme == "light" ? Light : Dark;

    /// <summary>The palette for a dark flag, for callers that already hold one.</summary>
    public static DesignPalette Palette(bool dark) => dark ? Dark : Light;

    /// <summary>
    /// The base of the neutral wash for hover and selected rows (macOS <c>Palette.subtle</c>,
    /// <c>Color.primary.opacity(0.035)</c>, M/Palette.swift:30): black by day, white by night,
    /// drawn at <see cref="DesignMetrics.Opacity.Subtle"/>. Not a MightyCore token, so only the
    /// fixture's <c>windowsOnly</c> section holds it.
    /// </summary>
    public static DesignColor Subtle(DesignPalette palette) => palette.IsDark ? new(0xFFFFFF) : new(0x000000);

    /// <summary>
    /// The files pane's source colours (M/FilePaneView.swift:447-450): the AppKit system pink,
    /// orange and purple per appearance, and <c>ink2</c> for comments; null for plain text.
    /// </summary>
    public static DesignColor? Syntax(string kind, DesignPalette palette) => kind switch
    {
        "keyword" => palette.IsDark ? new(0xFF375F) : new(0xFF2D55),
        "string" => palette.IsDark ? new(0xFF9F0A) : new(0xFF9500),
        "number" => palette.IsDark ? new(0xBF5AF2) : new(0xAF52DE),
        "comment" => palette.Ink2,
        _ => null,
    };
}
