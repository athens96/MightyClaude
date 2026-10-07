using MightyClaude.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using ShapePath = Microsoft.UI.Xaml.Shapes.Path;

namespace MightyClaude.WinUI;

/// <summary>
/// One small symbol of the composer and of the cards around it. The Mac draws SF Symbols; here each is the
/// closest Segoe Fluent Icons glyph or, where the icon font has none or only a hairline (the paperclip, the
/// ellipsis, the brain, the half-filled shield, the bolt in both states, the sliders, the branch, the bold
/// arrows, the raised hand), a path drawn to the size and weight the Mac's symbol takes at its font size. A glyph has no colour of its own: its owner inks it with a shared
/// design brush (<see cref="Ink"/>), so a theme toggle recolours it in place.
/// </summary>
internal sealed class ComposerGlyph
{
    private readonly List<(Shape Shape, bool Filled)> shapes = [];
    private readonly FontIcon? font;
    private readonly Canvas? canvas;
    private readonly double left, top;

    /// <summary>The element to place: a box of the size asked for with the symbol centred in it.</summary>
    internal FrameworkElement View { get; }
    /// <summary>The brush the symbol was last inked with, for the smoke.</summary>
    internal Brush? Colour { get; private set; }

    private ComposerGlyph(string glyph, double size, double width, double height, Windows.UI.Text.FontWeight? weight)
    {
        font = new FontIcon { Glyph = glyph, FontSize = size, Width = width, Height = height, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center };
        if (weight is { } w) font.FontWeight = w;
        View = font; Decorative();
    }

    private ComposerGlyph(double width, double height, double boxWidth, double boxHeight, double turn = 0)
    {
        // A canvas never clips: the Mac's brain is wider than the 14pt box its pill gives it, and so is this one.
        canvas = new Canvas { Width = boxWidth, Height = boxHeight, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center };
        left = (boxWidth - width) / 2; top = (boxHeight - height) / 2;
        // A symbol drawn upright and shown leaning (the paperclip) turns about the middle of its box.
        if (turn != 0) { canvas.RenderTransformOrigin = new Point(0.5, 0.5); canvas.RenderTransform = new RotateTransform { Angle = turn }; }
        View = canvas; Decorative();
    }

    private void Decorative() => AutomationProperties.SetAccessibilityView(View, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);

    /// <summary>Draws the symbol in a brush; every part takes the same one.</summary>
    internal ComposerGlyph Ink(Brush brush)
    {
        Colour = brush;
        if (font is not null) font.Foreground = brush;
        foreach (var (shape, filled) in shapes)
        {
            if (shape.StrokeThickness > 0) shape.Stroke = brush;
            if (filled) shape.Fill = brush;
        }
        return this;
    }

    private ComposerGlyph Add(Shape shape, bool filled)
    {
        Canvas.SetLeft(shape, left); Canvas.SetTop(shape, top);
        canvas!.Children.Add(shape); shapes.Add((shape, filled));
        return this;
    }

    /// <summary>A stroked outline: <c>M x y</c>, <c>L x y</c>, <c>C x1 y1 x2 y2 x y</c> and <c>Z</c>, absolute, with round caps and joins.</summary>
    private ComposerGlyph Stroke(string data, double thickness, double scale = 1) =>
        Add(new ShapePath { Data = Geometry(data, scale, false), StrokeThickness = thickness, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round }, false);

    /// <summary>A filled shape; <paramref name="edge"/> strokes it as well, which rounds its corners.</summary>
    private ComposerGlyph Fill(string data, double edge = 0, double scale = 1) =>
        Add(new ShapePath { Data = Geometry(data, scale, true), StrokeThickness = edge, StrokeLineJoin = PenLineJoin.Round }, true);

    private ComposerGlyph Ring(double x, double y, double radius, double thickness)
    {
        var ring = new Ellipse { Width = radius * 2, Height = radius * 2, StrokeThickness = thickness };
        Canvas.SetLeft(ring, left + x - radius); Canvas.SetTop(ring, top + y - radius);
        canvas!.Children.Add(ring); shapes.Add((ring, false));
        return this;
    }

    private ComposerGlyph Block(double x, double y, double width, double height, double radius)
    {
        var block = new Rectangle { Width = width, Height = height, RadiusX = radius, RadiusY = radius };
        Canvas.SetLeft(block, left + x); Canvas.SetTop(block, top + y);
        canvas!.Children.Add(block); shapes.Add((block, true));
        return this;
    }

    private static PathGeometry Geometry(string data, double scale, bool filled)
    {
        var geometry = new PathGeometry { FillRule = FillRule.Nonzero };
        PathFigure? figure = null;
        Point At(double x, double y) => new(x * scale, y * scale);
        foreach (var (kind, v) in ProviderMark.Commands(data))
        {
            switch (kind)
            {
                case 'M' when v.Length == 2: figure = new PathFigure { StartPoint = At(v[0], v[1]), IsFilled = filled, IsClosed = false }; geometry.Figures.Add(figure); break;
                case 'L' when v.Length == 2 && figure is not null: figure.Segments.Add(new LineSegment { Point = At(v[0], v[1]) }); break;
                case 'C' when v.Length == 6 && figure is not null: figure.Segments.Add(new BezierSegment { Point1 = At(v[0], v[1]), Point2 = At(v[2], v[3]), Point3 = At(v[4], v[5]) }); break;
                case 'Z' when figure is not null: figure.IsClosed = true; break;
            }
        }
        return geometry;
    }

    /// <summary>A Segoe Fluent Icons glyph at a font size, centred in a box (14 × 14 unless given).</summary>
    internal static ComposerGlyph Icon(string glyph, double size, double width = 14, double height = 14, Windows.UI.Text.FontWeight? weight = null) => new(glyph, size, width, height, weight);

    // ── the pills (M/SessionPaneView.swift:372-444, M/ComposerControls.swift:16-19) ──

    private const string ClipOutline = "M0.65 7.7 L0.65 3.3 C0.65 0 5.45 0 5.45 3.3 L5.45 9.9 C5.45 11.9 3.05 11.9 3.05 9.9 L3.05 4.6";
    /// <summary><c>paperclip</c> at 12: about 12 × 13 with a 1.3pt line, leaning 45° (the icon font's clip is a hairline).</summary>
    internal static ComposerGlyph Paperclip() => new ComposerGlyph(6.1 * 1.4, 12.2 * 1.4, 14, 14, 45).Stroke(ClipOutline, 1.3, 1.4);
    /// <summary><c>ellipsis</c> at 12: three 2.2pt dots, 11 wide.</summary>
    internal static ComposerGlyph Ellipsis() => new ComposerGlyph(11, 2.2, 14, 14).Block(0, 0, 2.2, 2.2, 1.1).Block(4.4, 0, 2.2, 2.2, 1.1).Block(8.8, 0, 2.2, 2.2, 1.1);
    /// <summary><c>lock.open</c> at 12, the full-access permission.</summary>
    internal static ComposerGlyph Unlock() => Icon("", 12);

    private const string BrainOutline = "M1.2 7 C0.6 5.2 1.6 3.6 3.2 3.6 C3 2 4.6 0.9 6 1.6 C6.8 0.5 8.9 0.5 9.6 1.7 C11 1 12.8 1.9 12.8 3.5 C14.3 3.9 15.1 5.6 14.3 7 C14.9 8.3 14.2 9.9 12.7 9.9 L12.7 11.4 C12.7 12.3 11.5 12.6 11 11.8 L10.4 10.6 C9.4 11 8.2 10.8 7.6 10 C6.4 10.9 4.6 10.6 4.2 9.3 C2.6 9.6 1.2 8.6 1.2 7 Z";
    private const string BrainFolds = "M6 1.6 C6.9 2.6 6.9 3.9 6 4.7 M3.2 3.6 C4.2 3.7 4.9 4.4 5 5.3 M7.6 10 C7 8.9 7.6 7.6 8.9 7.3 C10.2 7 10.9 5.8 10.4 4.7 M12.8 3.5 C11.9 3.6 11.3 4.1 11.1 4.9 M4.2 9.3 C4 8.4 4.5 7.6 5.4 7.3";
    /// <summary><c>brain</c> at 12: the Mac's is about 16 × 13.4, wider than its 14pt box.</summary>
    internal static ComposerGlyph Brain() => new ComposerGlyph(16.2, 13.5, 14, 14).Stroke(BrainOutline, 1.1, 1.03).Stroke(BrainFolds, 1.1, 1.03);

    private const string ShieldOutline = "M5.2 0.7 L9.6 2.2 L9.6 6.2 C9.6 9 7.8 11 5.2 12 C2.6 11 0.8 9 0.8 6.2 L0.8 2.2 Z";
    private const string ShieldHalf = "M5.2 0.7 L0.8 2.2 L0.8 6.2 C0.8 9 2.6 11 5.2 12 Z";
    /// <summary><c>shield.lefthalf.filled</c> at 12: about 10.3 × 12.5.</summary>
    internal static ComposerGlyph Shield() => new ComposerGlyph(10.4, 12.7, 14, 14).Fill(ShieldHalf).Stroke(ShieldOutline, 1.1);

    private const string BoltOutline = "M5.3 0.7 L0.9 7.5 L3.9 7.5 L2.7 12.8 L7.1 5.9 L4.1 5.9 Z";
    /// <summary><c>bolt</c> or <c>bolt.fill</c> at 12: about 7.8 × 13.5.</summary>
    internal static ComposerGlyph Bolt(bool filled) => filled
        ? new ComposerGlyph(8, 13.5, 14, 14).Fill(BoltOutline, 1.1)
        : new ComposerGlyph(8, 13.5, 14, 14).Stroke(BoltOutline, 1.1);

    /// <summary><c>slider.horizontal.3</c> at 12: three rules, each with its knob.</summary>
    internal static ComposerGlyph Sliders() => new ComposerGlyph(13, 13, 14, 14)
        .Stroke("M0.6 2 L6.3 2 M9.7 2 L12.4 2 M0.6 6.5 L2.3 6.5 M5.7 6.5 L12.4 6.5 M0.6 11 L7.3 11 M10.7 11 L12.4 11", 1.15)
        .Ring(8, 2, 1.7, 1.15).Ring(4, 6.5, 1.7, 1.15).Ring(9, 11, 1.7, 1.15);

    /// <summary><c>chevron.down</c> at 7 semibold, in its 7pt-wide frame.</summary>
    internal static ComposerGlyph ChevronDown() => new ComposerGlyph(7, 4.1, 7, 14).Stroke("M0.7 0.7 L3.5 3.4 L6.3 0.7", 1.25);

    /// <summary><c>chevron.right</c> at <paramref name="size"/> (7 between the guide panel's phases, 8 on a disclosure).</summary>
    internal static ComposerGlyph ChevronRight(double size = 7, double thickness = 1.1) =>
        new ComposerGlyph(4.1 * size / 7, size, 4.1 * size / 7, size).Stroke("M0.7 0.7 L3.4 3.5 L0.7 6.3", thickness, size / 7);

    /// <summary><c>chevron.down</c> at <paramref name="size"/>, for an open disclosure.</summary>
    internal static ComposerGlyph ChevronDown(double size, double thickness) =>
        new ComposerGlyph(size, 4.1 * size / 7, size, size).Stroke("M0.7 0.7 L3.5 3.4 L6.3 0.7", thickness, size / 7);

    // ── the right cluster (M/SessionPaneView.swift:731-765) ──

    /// <summary><c>arrow.triangle.branch</c> at 11 in its 16pt-wide, toolbar-high frame: this pane continues an earlier conversation.</summary>
    internal static ComposerGlyph Branch() => new ComposerGlyph(10, 10.6, 16, DesignMetrics.Layout.Toolbar)
        .Stroke("M5 9.5 L5 6 C5 4.4 3.2 3.6 1.5 1.4 M5 6 C5 4.4 6.8 3.6 8.5 1.4 M1.2 3.7 L1.2 1.1 L3.8 1.1 M6.2 1.1 L8.8 1.1 L8.8 3.7", 1.15);

    /// <summary><c>arrow.up</c> at 14 semibold: about 10 × 12 with a 1.8pt stroke, centred in the toolbar-high send button.</summary>
    internal static ComposerGlyph ArrowUp() => new ComposerGlyph(10, 12.2, DesignMetrics.Layout.Toolbar, DesignMetrics.Layout.Toolbar).Stroke("M5 11.2 L5 1 M0.9 5.1 L5 1 L9.1 5.1", 1.8);

    /// <summary><c>text.badge.plus</c> at 13 semibold: the draft joins the queue.</summary>
    internal static ComposerGlyph QueueAdd() => new ComposerGlyph(13.6, 12.1, DesignMetrics.Layout.Toolbar, DesignMetrics.Layout.Toolbar)
        .Stroke("M2.7 0.8 L2.7 5.8 M0.2 3.3 L5.2 3.3 M7.4 3.3 L13.3 3.3 M0.8 7.6 L13.3 7.6 M0.8 11.3 L9.6 11.3", 1.5);

    /// <summary><c>stop.fill</c>: the rounded square, 10 on the toolbar-high button and 8.4 on the small one 4pt under it.</summary>
    internal static ComposerGlyph Stop(bool compact)
    {
        var side = compact ? 8.4 : 10; var box = compact ? DesignMetrics.Layout.Toolbar - 4 : DesignMetrics.Layout.Toolbar;
        return new ComposerGlyph(side, side, box, box).Block(0, 0, side, side, side * 0.22);
    }

    // ── the cards around the composer ──

    /// <summary><c>arrow.right</c> at 13 bold, on a next-action row (M/NextActionButtons.swift:27).</summary>
    internal static ComposerGlyph ArrowRight() => new ComposerGlyph(12, 10, 13, 13).Stroke("M1 5 L11 5 M6.9 0.9 L11 5 L6.9 9.1", 2);

    /// <summary><c>hand.raised.fill</c> at 11 heavy, on the wait badge of a permission request (M/PaneChrome.swift:172-180).</summary>
    internal static ComposerGlyph Hand() => new ComposerGlyph(10, 12.4, 22, 22)
        .Stroke("M2.4 6.6 L2.4 2.7 M4.3 6.6 L4.3 1.4 M6.2 6.6 L6.2 1 M8.1 6.6 L8.1 2.3", 1.75)
        .Stroke("M2.1 9.6 L0.95 7.4", 1.7)
        .Fill("M1.6 6 L8.9 6 L8.9 8.6 C8.9 10.6 7.4 11.8 5.4 11.8 C3.6 11.8 2.6 11 1.6 9.2 Z", 0.6);
}
