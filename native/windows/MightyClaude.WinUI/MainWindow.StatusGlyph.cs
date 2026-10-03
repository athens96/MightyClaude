using MightyClaude.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using ShapePath = Microsoft.UI.Xaml.Shapes.Path;

namespace MightyClaude.WinUI;

/// <summary>
/// The status mark in front of a pane's title in the sidebar rows, the pane tabs and the pane
/// header (status v2, concept A, the glyph row — macOS StatusGlyph.swift). Which glyph a state gets,
/// its colours and its 16-unit geometry are <see cref="StatusGlyph"/> in Core; this only draws
/// them. The spark turns once every 3.2 s and stands still when Windows animations are off.
/// </summary>
internal sealed class StatusMark
{
    internal static bool? AnimationsEnabledOverride;
    private static bool AnimationsEnabled => AnimationsEnabledOverride ?? new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;

    private readonly double size;
    private readonly Canvas canvas;
    private Storyboard? spin;
    private (StatusGlyphKind Glyph, DesignTone Tone, string Kind, bool Dark)? drawn;

    internal StatusMark(double size = StatusGlyph.RowSize)
    {
        this.size = size;
        canvas = new Canvas { Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        canvas.Loaded += (_, _) => spin?.Begin();
        canvas.Unloaded += (_, _) => spin?.Stop();
    }

    internal FrameworkElement View => canvas;
    internal StatusGlyphKind? Glyph => drawn?.Glyph;
    internal bool Spinning => spin is not null;

    /// <summary>Draws the mark for a pane's status; redraws only when the glyph, tone, kind or theme changed.</summary>
    internal void Update(string status, string kind, int pendingRequests, bool dark)
    {
        var tone = StatusGlyph.Tone(StatusGlyph.DisplayStatus(status, pendingRequests));
        var glyph = StatusGlyph.Kind(tone, kind);
        AutomationProperties.SetName(canvas, Locale.Get(StatusGlyph.WordKey(tone)));
        if (drawn == (glyph, tone, kind, dark)) return;
        drawn = (glyph, tone, kind, dark);
        spin?.Stop(); spin = null; canvas.RenderTransform = null; canvas.Children.Clear();
        var scale = size / 16;
        var ink = Paint(StatusGlyph.GlyphHex(tone, dark));
        if (glyph == StatusGlyphKind.Pane)
        {
            canvas.Children.Add(new FontIcon { Glyph = PaneSymbol(kind), FontSize = size * 0.78, Foreground = ink, Width = size, Height = size });
            return;
        }
        if (glyph.IsDisc())
        {
            var inset = 8 - StatusGlyph.DiscRadius;
            canvas.Children.Add(Circle(inset * scale, inset * scale, StatusGlyph.DiscRadius * 2 * scale, Paint(StatusGlyph.DiscFill(tone))));
            ink = Paint(StatusGlyph.DiscInk(tone));
        }
        canvas.Children.Add(Stroke(StatusGlyph.Strokes(glyph), ink, StatusGlyph.StrokeWidth(glyph) * scale, scale));
        if (StatusGlyph.Dot(glyph) is { } dot)
            canvas.Children.Add(Circle((dot.X - dot.Radius) * scale, (dot.Y - dot.Radius) * scale, dot.Radius * 2 * scale, ink));
        if (glyph.Turns())
        {
            var diagonals = Stroke(StatusGlyph.SparkDiagonals, ink, StatusGlyph.StrokeWidth(glyph) * scale, scale);
            diagonals.Opacity = StatusGlyph.SparkDiagonalOpacity; canvas.Children.Add(diagonals);
            if (AnimationsEnabled) Spin();
        }
    }

    private void Spin()
    {
        var turn = new RotateTransform { CenterX = size / 2, CenterY = size / 2 }; canvas.RenderTransform = turn;
        var animation = new DoubleAnimation { From = 0, To = 360, Duration = new Duration(TimeSpan.FromSeconds(StatusGlyph.SparkTurnSeconds)), RepeatBehavior = RepeatBehavior.Forever };
        Storyboard.SetTarget(animation, turn); Storyboard.SetTargetProperty(animation, "Angle");
        spin = new Storyboard(); spin.Children.Add(animation);
        if (canvas.IsLoaded) spin.Begin();
    }

    /// <summary>The pane's own symbol while it is idle and not an agent's (Segoe Fluent Icons).</summary>
    private static string PaneSymbol(string kind) => kind switch
    {
        "shell" => "",
        "browser" => "",
        "files" => "",
        _ => "",
    };

    private static ShapePath Stroke(IReadOnlyList<GlyphFigure> figures, Brush ink, double thickness, double scale) => new()
    {
        Data = Build(figures, scale), Stroke = ink, StrokeThickness = thickness,
        StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
    };

    private static Ellipse Circle(double left, double top, double diameter, Brush fill)
    {
        var circle = new Ellipse { Width = diameter, Height = diameter, Fill = fill };
        Canvas.SetLeft(circle, left); Canvas.SetTop(circle, top); return circle;
    }

    private static PathGeometry Build(IReadOnlyList<GlyphFigure> figures, double scale)
    {
        Point At(double x, double y) => new(x * scale, y * scale);
        var geometry = new PathGeometry();
        foreach (var source in figures)
        {
            var figure = new PathFigure { StartPoint = At(source.X, source.Y), IsClosed = source.Closed, IsFilled = false };
            foreach (var segment in source.Segments)
            {
                PathSegment piece = segment switch
                {
                    GlyphArc arc => new ArcSegment { Point = At(arc.X, arc.Y), Size = new Size(arc.Radius * scale, arc.Radius * scale), IsLargeArc = arc.LargeArc, SweepDirection = SweepDirection.Clockwise },
                    GlyphCurve curve => new BezierSegment { Point1 = At(curve.X1, curve.Y1), Point2 = At(curve.X2, curve.Y2), Point3 = At(curve.X, curve.Y) },
                    _ => new LineSegment { Point = At(segment.X, segment.Y) },
                };
                figure.Segments.Add(piece);
            }
            geometry.Figures.Add(figure);
        }
        return geometry;
    }

    private static SolidColorBrush Paint(string hex)
    {
        var rgb = Convert.ToInt32(hex[1..], 16);
        return new SolidColorBrush(Windows.UI.Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
    }
}

public sealed partial class MainWindow
{
    /// <summary>
    /// Smoke key <c>statusGlyph</c>: the sidebar rows, the tabs and the pane header draw design A's
    /// glyph (no spinning ring), each state reaches its own glyph, a pending request turns a running
    /// pane amber, and the spark turns only while Windows animations are on.
    /// </summary>
    private async Task<Dictionary<string, object?>> RunStatusGlyphSmoke()
    {
        var checks = new Dictionary<string, object?>();
        var id = service.Snapshot.Sessions[0].Id; var original = service.Snapshot.Sessions[0].Status;
        try
        {
            Require(sessionIndicators.Count > 0 && sessionIndicators.Values.All(v => v.Mark.View.Parent is not null), "sidebar rows have no status glyph");
            Require(sessionLinks.Children.OfType<Button>().All(b => b.Content is Grid row && !row.Children.OfType<ProgressRing>().Any() && row.Children[0] is Canvas), "a sidebar row still has the spinning ring");
            Require(tabIndicators.Count > 0 && tabIndicators.Values.All(v => v.Mark.View.Parent is not null), "tabs have no status glyph");
            checks["sidebarAndTabsDrawGlyphs"] = true;
            StatusMark.AnimationsEnabledOverride = false;
            var seen = new Dictionary<string, string>();
            foreach (var (status, expected) in new[] { ("running", StatusGlyphKind.Spark), ("completed", StatusGlyphKind.Check), ("stopped", StatusGlyphKind.SlashedRing), ("error", StatusGlyphKind.Exclamation), ("idle", StatusGlyphKind.Ring) })
            {
                await service.UpdateAsync(s => s with { Sessions = s.Sessions.Select(p => p.Id == id ? p with { Status = status } : p).ToList() });
                RefreshRunningIndicators(); views[id].Refresh();
                Require(sessionIndicators[id].Mark.Glyph == expected, $"sidebar row glyph for {status} is not {expected}");
                Require(views[id].HeaderMark.Glyph == expected, $"pane header glyph for {status} is not {expected}");
                seen[status] = expected.ToString();
            }
            checks["eachStateReachesItsGlyph"] = seen;
            // A running pane is the one whose spark would turn: with animations off it must stand still.
            await service.UpdateAsync(s => s with { Sessions = s.Sessions.Select(p => p.Id == id ? p with { Status = "running" } : p).ToList() });
            RefreshRunningIndicators(); views[id].Refresh();
            Require(sessionIndicators[id].Mark.Glyph == StatusGlyphKind.Spark && !sessionIndicators[id].Mark.Spinning, "the running sidebar glyph turns with animations off");
            Require(views[id].HeaderMark.Glyph == StatusGlyphKind.Spark && !views[id].HeaderMark.Spinning, "the running header glyph turns with animations off");
            checks["sparkStillWithAnimationsOff"] = true;
            StatusMark.AnimationsEnabledOverride = true;
            var turning = new StatusMark(); turning.Update("running", "claude", 0, true);
            Require(turning.Glyph == StatusGlyphKind.Spark && turning.Spinning, "the running glyph does not turn");
            var waiting = new StatusMark(); waiting.Update("running", "claude", 1, false);
            Require(waiting.Glyph == StatusGlyphKind.Question && !waiting.Spinning, "a running pane with a pending request is not the amber ?");
            var shell = new StatusMark(); shell.Update("idle", "shell", 0, false);
            Require(shell.Glyph == StatusGlyphKind.Pane, "an idle shell pane does not show its own symbol");
            checks["sparkTurnsPendingTurnsAmberShellShowsSymbol"] = true;
        }
        finally
        {
            StatusMark.AnimationsEnabledOverride = null;
            await service.UpdateAsync(s => s with { Sessions = s.Sessions.Select(p => p.Id == id ? p with { Status = original } : p).ToList() });
            RefreshRunningIndicators(); if (views.TryGetValue(id, out var pane)) pane.Refresh();
        }
        return checks;
    }

    private sealed partial class PaneView
    {
        /// <summary>The pane header's mark, before the state word (design A's one-line header).</summary>
        private readonly StatusMark headerMark = new(StatusGlyph.HeaderSize);
        internal int PendingRequests => toolPermissions.Count(p => p.State == "pending");
        internal StatusMark HeaderMark => headerMark;

        /// <summary>
        /// Redraws the header's mark and word for the pane's status and its pending requests,
        /// from the session and theme the caller already read (no snapshot copy per call).
        /// </summary>
        internal void RefreshHeaderStatus(RunSession pane, bool dark)
        {
            var pending = PendingRequests;
            headerMark.Update(pane.Status, pane.Kind, pending, dark);
            label.Text = StateLabel(StatusGlyph.DisplayStatus(pane.Status, pending));
        }
    }
}
