namespace MightyClaude.Core;

/// <summary>
/// The edge a Mighty diagram block in motion draws over its card (macOS
/// <c>MightyGraphActivityOutline</c>): a running block's 2pt run-blue border is a dashed
/// line marching around the card, one dash-and-gap period per cycle; with Windows
/// animations off it holds still as a solid line. A block waiting on the user keeps a
/// still amber line. Every other block has no outline.
/// </summary>
public static class MightyGraphActivity
{
    /// <summary>The outline choices WinUI draws.</summary>
    public const string Marching = "marching", Solid = "solid", Waiting = "waiting", None = "none";

    /// <summary>9pt dashes, 7pt gaps, drawn with flat caps so the dashes are as long as they say.</summary>
    public static readonly IReadOnlyList<double> Dash = [9, 7];

    /// <summary>One dash-and-gap period goes by every 1.6 s.</summary>
    public static readonly TimeSpan Period = TimeSpan.FromSeconds(1.6);

    /// <summary>The border's width; the run halo outside it is twice that.</summary>
    public const double LineWidth = 2;

    /// <summary>The card's corner radius; the outline sits 1pt inside it.</summary>
    public const double CornerRadius = 12;

    /// <summary>
    /// A block's outline from its status (bucketed the way macOS
    /// <c>DesignTone(blockStatus:)</c> does: anything unfinished that is not waiting runs).
    /// </summary>
    public static string Outline(string blockStatus, bool animationsEnabled) => blockStatus switch
    {
        "waiting" => Waiting,
        "completed" or "error" or "failed" or "stopped" or "cancelled" or "interrupted" => None,
        _ => animationsEnabled ? Marching : Solid,
    };

    /// <summary>How far through the cycle the dashes are; always 0 when animations are off.</summary>
    public static double Phase(TimeSpan elapsed, bool animationsEnabled)
    {
        if (!animationsEnabled) return 0;
        var cycle = elapsed.Ticks % Period.Ticks;
        return (double)(cycle < 0 ? cycle + Period.Ticks : cycle) / Period.Ticks;
    }

    /// <summary>The length of a rounded rectangle's outline with circular corners.</summary>
    public static double Perimeter(double width, double height, double cornerRadius)
    {
        var radius = Math.Max(0, Math.Min(cornerRadius, Math.Min(width / 2, height / 2)));
        return 2 * (width + height) - 8 * radius + 2 * Math.PI * radius;
    }

    /// <summary>
    /// <see cref="Dash"/> stretched or squeezed so a whole number of periods fits the
    /// outline: where the path starts and ends there is no short dash or double gap.
    /// </summary>
    public static IReadOnlyList<double> DashFitting(double perimeter)
    {
        var period = Dash.Sum();
        if (perimeter <= 0) return Dash;
        var fitted = perimeter / Math.Max(1, Math.Round(perimeter / period, MidpointRounding.AwayFromZero));
        return Dash.Select(d => d * fitted / period).ToList();
    }

    /// <summary>
    /// The dash offset at a phase: one period per cycle, so the line moves on without a
    /// jump when the cycle wraps; a negative offset walks the dashes forward.
    /// </summary>
    public static double DashOffset(double phase, IReadOnlyList<double> dash) => -phase * dash.Sum();

    /// <summary>
    /// The outline drawn on a card of this size: the card inset by 1pt, its radius 1pt
    /// smaller, and the dash fitted to that path.
    /// </summary>
    public static IReadOnlyList<double> DashForCard(double width, double height) =>
        DashFitting(Perimeter(Math.Max(0, width - LineWidth), Math.Max(0, height - LineWidth), CornerRadius - 1));

    /// <summary>The outline's token: run blue, or wait amber while the block waits (the same in both modes).</summary>
    public static DesignToken StrokeToken(string outline) => outline == Waiting ? DesignToken.Wait : DesignToken.Run;

    /// <summary>The token of the soft halo outside a running block's border; waiting has none.</summary>
    public static DesignToken? HaloToken(string outline) => outline is Marching or Solid ? DesignToken.RunSoft : null;

    /// <summary>The outline's colour (the design tokens' run blue and wait amber, the same in both modes).</summary>
    public static string StrokeHex(string outline) => DesignTokens.Light[StrokeToken(outline)].Hex;

    /// <summary>The soft run halo outside a running block's border, per mode; waiting has none.</summary>
    public static string? HaloHex(string outline, bool dark) =>
        HaloToken(outline) is { } halo ? DesignTokens.Palette(dark)[halo].Hex : null;

    // The running block's activity mark beside its state (macOS MightyGraphActivityIndicator):
    // four run-blue capsules, 3 wide and 2 apart in an 18×14 box, rising and falling in a wave
    // once per 1.6 s; with animations off a still bolt stands in their place.

    /// <summary>How many capsules the activity mark has.</summary>
    public const int Bars = 4;

    /// <summary>A capsule's width and the gap between two.</summary>
    public const double BarWidth = 3, BarGap = 2;

    /// <summary>The box the capsules stand in.</summary>
    public const double BarBoxWidth = 18, BarBoxHeight = 14;

    /// <summary>The tallest a capsule gets; each is drawn this tall and scaled down to its <see cref="BarHeight"/>.</summary>
    public const double BarTallest = 12;

    /// <summary>The capsules redraw at most this often (the Mac's <c>TimelineView(.animation(minimumInterval: 1/24))</c>).</summary>
    public const double BarFramesPerSecond = 24;

    /// <summary>A capsule's height at a phase of the cycle: 4 to 12, each a fifth of a cycle behind the one before.</summary>
    public static double BarHeight(int index, double phase) =>
        4 + 8 * (Math.Sin((phase + index / 5.0) * 2 * Math.PI) + 1) / 2;
}
