namespace MightyClaude.Core;

/// <summary>
/// The dots under the Mighty diagram (macOS <c>MightyGraphDotGrid</c>, M/MightyGraphView.swift:995-1015):
/// one <c>line</c>-coloured dot every 18pt × zoom on the <c>page</c> surface, laid from the camera's
/// offset so they travel with a pan, and none at all once the step is under 6pt.
/// </summary>
public static class MightyGraphDotGrid
{
    /// <summary>The step between two dots at 100%.</summary>
    public const double Spacing = 18;

    /// <summary>Below this step the dots would crowd into a tint, so none are drawn.</summary>
    public const double MinimumStep = 6;

    /// <summary>The smallest dot radius, so a zoomed-out grid still shows.</summary>
    public const double MinimumRadius = 0.6;

    /// <summary>The step between two dots at a zoom.</summary>
    public static double Step(double zoom) => Spacing * zoom;

    /// <summary>Whether the grid is drawn at a zoom.</summary>
    public static bool Shown(double zoom) => Step(zoom) >= MinimumStep;

    /// <summary>A dot's radius at a zoom: the zoom itself, never under <see cref="MinimumRadius"/>.</summary>
    public static double Radius(double zoom) => Math.Max(MinimumRadius, zoom);

    /// <summary>
    /// The first dot centre at or after 0 along one axis, for dots at <paramref name="offset"/> + n · <paramref name="step"/>:
    /// a value in [0, step). Dots before it, up to a radius off the edge, are one step back.
    /// </summary>
    public static double First(double offset, double step)
    {
        if (!(step > 0) || !double.IsFinite(offset)) return 0;
        var first = offset % step;
        return first < 0 ? first + step : first;
    }
}
