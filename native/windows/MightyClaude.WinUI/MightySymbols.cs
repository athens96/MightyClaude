using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using ShapePath = Microsoft.UI.Xaml.Shapes.Path;

namespace MightyClaude.WinUI;

/// <summary>
/// The SF Symbols of the Mighty view (macOS MightyGraphView.swift, MightyGraphTimelineView.swift,
/// MightyGraphActivityView.swift, MightyGraphResultFilesView.swift, MightyGraphReferenceBubble.swift),
/// asked for by their Mac names. A name with a close "Segoe Fluent Icons" glyph is that glyph; the
/// others (the three joined nodes, the indented list, the message with its arrow, the seal, the
/// expand and compress brackets, the two resize arrows, the circled marks and the two documents) are
/// drawn in a 16-unit box the way the Mac's look in docs/design-system/crops. A symbol is asked for at the
/// size the Mac gives it, <c>.font(.system(size: N))</c>: a glyph is set at N, and a drawn one in a box
/// 1.15 × N, which is how wide those symbols come out on the Mac (the 13pt seal and message are 14 to 15 across).
/// </summary>
internal static class MightySymbols
{
    /// <summary>The Mac's symbol weights, as the line width a drawn symbol takes (a glyph has one weight).</summary>
    internal enum Weight { Regular, Semibold, Bold }

    /// <summary>A drawn symbol's box, in its font size.</summary>
    internal const double DrawnScale = 1.15;

    /// <summary>The line of a drawn symbol: about a tenth of its size at regular weight (1.3 at 13), heavier for semibold and bold.</summary>
    private static double Line(Weight weight, double size) => Math.Max(1, size * weight switch { Weight.Bold => 0.155, Weight.Semibold => 0.135, _ => 0.1 });

    /// <summary>The Segoe Fluent Icons glyph that stands for an SF Symbol, or null when it is drawn.</summary>
    internal static string? Glyph(string symbol) => symbol switch
    {
        "minus.magnifyingglass" => "\uE71F", "plus.magnifyingglass" => "\uE8A3",
        "square.and.pencil" => "\uE932",
        "terminal" => "\uE756", "person.crop.square.filled.and.at.rectangle" => "\uE8D4", "text.bubble" => "\uE8BD",
        "questionmark.bubble" or "questionmark.bubble.fill" or "questionmark.square.dashed" or "questionmark.folder" or "doc.questionmark" => "\uE9CE",
        "exclamationmark.triangle" => "\uE7BA", "exclamationmark.triangle.fill" => "\uE814",
        "clock.arrow.circlepath" => "\uE81C", "chevron.down" => "\uE70D", "chevron.right" => "\uE76C", "xmark" => "\uE711",
        "arrow.clockwise" => "\uE72C", "flag" => "\uE7C1", "bolt" or "bolt.fill" => "\uE945",
        "doc.text" => "\uE8A5", "doc.richtext" => "\uE8A1", "globe" => "\uE774", "photo" => "\uE91B", "folder" => "\uE8B7",
        "arrow.up.forward.app" => "\uE8A7",
        "rectangle.righthalf.inset.filled.arrow.right" => "\uE90D", "rectangle.lefthalf.inset.filled.arrow.left" => "\uE90C",
        // The style manifest's closed icon list (M/MightyCore/Styles/StyleManifest.swift:219-227).
        "wand.and.stars" or "sparkles" => "\uF4A5", "leaf" => "\uE8BE", "play.fill" => "\uF5B0",
        "arrow.triangle.2.circlepath" => "\uE895", "gauge.with.dots.needle.33percent" => "\uEC4A", "lightbulb" => "\uEA80",
        "square.grid.2x2" => "\uF0E2", "arrow.right" => "\uEBE7", "arrow.triangle.branch" => "\uEA3C", "book" => "\uE82D",
        "bookmark" => "\uE8EC", "calendar" => "\uE787", "chart.bar" => "\uE9F9", "cube" or "tray" => "\uE7B8", "hammer" => "\uE90F",
        "list.bullet" => "\uE8FD", "magnifyingglass" => "\uE721", "map" => "\uE707", "paintbrush" => "\uE771", "puzzlepiece" => "\uEA86",
        // Drawn the other way round on Windows: the glyph's arrows run along the rising diagonal.
        "arrow.down.right.and.arrow.up.left" => "\uE73F",
        _ => null,
    };

    private static readonly HashSet<string> DrawnSymbols =
    [
        "point.3.connected.trianglepath.dotted", "list.bullet.indent", "arrow.up.message", "checkmark.seal", "checkmark.seal.fill",
        "stop.circle", "stop.circle.fill", "pause.circle", "arrow.up.circle", "rectangle.expand.vertical", "rectangle.compress.vertical",
        "arrow.up.left.and.arrow.down.right", "doc.on.doc", "doc.on.doc.fill",
    ];

    /// <summary>Whether <see cref="Create"/> can make this symbol; a style manifest's icon is asked before it is drawn.</summary>
    internal static bool Known(string? symbol) => symbol is not null && (Glyph(symbol) is not null || symbol == "infinity" || DrawnSymbols.Contains(symbol));

    /// <summary>
    /// A symbol at the Mac's font <paramref name="size"/>. <paramref name="ink"/> is one of the window's
    /// shared brushes (never mutated here); null leaves a glyph to inherit its button's foreground, so it
    /// follows the button's disabled ink. <paramref name="cut"/> is the surface showing through a
    /// filled symbol's detail (the check of a filled seal). Decorative: hidden from UI automation.
    /// </summary>
    internal static FrameworkElement Create(string symbol, double size, Brush? ink, Weight weight = Weight.Regular, Brush? cut = null)
    {
        FrameworkElement view;
        if (Glyph(symbol) is { } glyph)
        {
            var icon = new FontIcon { Glyph = glyph, FontSize = size, Width = size, Height = size };
            if (ink is not null) icon.Foreground = ink;
            if (symbol == "arrow.down.right.and.arrow.up.left") { icon.RenderTransformOrigin = new Point(.5, .5); icon.RenderTransform = new ScaleTransform { ScaleX = -1 }; }
            view = icon;
        }
        else if (symbol == "infinity")
        {
            var words = new TextBlock { Text = "\u221E", FontSize = size * 1.15, LineHeight = size, LineStackingStrategy = LineStackingStrategy.BlockLineHeight, TextAlignment = TextAlignment.Center, Width = size, Height = size };
            if (ink is not null) words.Foreground = ink;
            view = words;
        }
        else view = Drawn(symbol, Math.Round(size * DrawnScale * 2) / 2, ink, Line(weight, size), cut);
        view.VerticalAlignment = VerticalAlignment.Center; view.IsHitTestVisible = false;
        AutomationProperties.SetAccessibilityView(view, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        return view;
    }

    /// <summary>Recolours a symbol <see cref="Create"/> made, by handing it another shared brush.</summary>
    internal static void Paint(FrameworkElement symbol, Brush ink)
    {
        switch (symbol)
        {
            case IconElement icon: icon.Foreground = ink; break;
            case TextBlock words: words.Foreground = ink; break;
            case ShapePath path: if (path.Stroke is not null) path.Stroke = ink; if (path.Fill is not null) path.Fill = ink; break;
            // A drawn pair: only the parts in the symbol's own ink (tagged), never the cut-out detail.
            case Panel panel: foreach (var part in panel.Children.OfType<ShapePath>().Where(p => p.Tag is true)) Paint(part, ink); break;
        }
    }

    // ── drawn symbols ─────────────────────────────────────────────────────

    private static FrameworkElement Drawn(string symbol, double size, Brush? ink, double line, Brush? cut)
    {
        var scale = size / 16;
        ShapePath Stroke(string data) => new()
        {
            Data = Geometry(data, scale, false), Stroke = ink, StrokeThickness = line, Width = size, Height = size, Tag = true,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
        };
        ShapePath Fill(string data, Brush? brush, bool own = true) => new() { Data = Geometry(data, scale, true), Fill = brush, Width = size, Height = size, Tag = own };
        Grid Pair(params ShapePath[] parts)
        {
            var box = new Grid { Width = size, Height = size };
            foreach (var part in parts) box.Children.Add(part);
            return box;
        }
        // A detail cut out of a filled symbol: drawn over it in the colour of the surface under it.
        ShapePath CutStroke(string data)
        {
            var path = Stroke(data); path.Stroke = cut; path.StrokeThickness = line * 1.25; path.Tag = false;
            return path;
        }
        const string ring = "M 1.6 8 A 6.4 6.4 1 1 14.4 8 A 6.4 6.4 1 1 1.6 8 Z";
        const string check = "M 5.2 8.3 L 7.1 10.3 L 10.9 5.9";
        const string frontDocument = "M 7 4.6 L 12.6 4.6 A 1.6 1.6 0 1 14.2 6.2 L 14.2 13.4 A 1.6 1.6 0 1 12.6 15 L 7 15 A 1.6 1.6 0 1 5.4 13.4 L 5.4 6.2 A 1.6 1.6 0 1 7 4.6 Z";
        const string brackets = "M 4.6 3.9 L 2.6 3.9 A 1.7 1.7 0 0 0.9 5.6 L 0.9 10.4 A 1.7 1.7 0 0 2.6 12.1 L 4.6 12.1 "
            + "M 11.4 3.9 L 13.4 3.9 A 1.7 1.7 0 1 15.1 5.6 L 15.1 10.4 A 1.7 1.7 0 1 13.4 12.1 L 11.4 12.1 ";
        switch (symbol)
        {
            // point.3.connected.trianglepath.dotted: two rings above one, joined by dots.
            case "point.3.connected.trianglepath.dotted":
                return Stroke(Ring(2.9, 4.4, 2.2) + Ring(13.1, 4.4, 2.2) + Ring(8, 11.9, 2.2)
                    + Dot(6.2, 4.4) + Dot(8, 4.4) + Dot(9.8, 4.4) + Dot(4.9, 7.4) + Dot(6, 9) + Dot(11.1, 7.4) + Dot(10, 9));
            // list.bullet.indent: three bulleted lines, each a step further in.
            case "list.bullet.indent":
                return Stroke(string.Concat(Enumerable.Range(0, 3).Select(row => Dot(1.6 + 1.4 * row, 4 + 4 * row) + FormattableString.Invariant($"M {4.9 + 1.4 * row} {4 + 4 * row} L {12.4 + 1.4 * row} {4 + 4 * row} "))));
            // arrow.up.message: a round speech bubble, its tail at the bottom left, an arrow pointing up in it.
            case "arrow.up.message":
                return Stroke("M 5.73 12.59 A 6.6 5.7 1 0 2.6 10.32 L 1.9 14.3 Z M 8.2 10.4 L 8.2 4.6 M 5.7 6.9 L 8.2 4.4 L 10.7 6.9");
            case "checkmark.seal": return Stroke(Seal() + check);
            case "checkmark.seal.fill": return Pair(Fill(Seal(), ink), CutStroke(check));
            case "stop.circle": return Pair(Stroke(ring), Fill("M 5.9 5.9 L 10.1 5.9 L 10.1 10.1 L 5.9 10.1 Z", ink));
            case "stop.circle.fill": return Pair(Fill("M 0.9 8 A 7.1 7.1 1 1 15.1 8 A 7.1 7.1 1 1 0.9 8 Z", ink), Fill("M 5.7 5.7 L 10.3 5.7 L 10.3 10.3 L 5.7 10.3 Z", cut, false));
            case "pause.circle": return Stroke(ring + " M 6.3 5.6 L 6.3 10.4 M 9.7 5.6 L 9.7 10.4");
            case "arrow.up.circle": return Stroke(ring + " M 8 11.3 L 8 4.9 M 5.3 7.5 L 8 4.7 L 10.7 7.5");
            // rectangle.expand.vertical / rectangle.compress.vertical: two brackets, the arrows leaving or entering between them.
            case "rectangle.expand.vertical": return Stroke(brackets + "M 8 5.6 L 8 0.9 M 5.3 3.4 L 8 0.7 L 10.7 3.4 M 8 10.4 L 8 15.1 M 5.3 12.6 L 8 15.3 L 10.7 12.6");
            case "rectangle.compress.vertical": return Stroke(brackets + "M 8 0.7 L 8 5.4 M 5.3 2.9 L 8 5.6 L 10.7 2.9 M 8 15.3 L 8 10.6 M 5.3 13.1 L 8 10.4 L 10.7 13.1");
            // arrow.up.left.and.arrow.down.right: the block's corner handle.
            case "arrow.up.left.and.arrow.down.right":
                return Stroke("M 2.2 6.6 L 2.2 2.2 L 6.6 2.2 M 2.2 2.2 L 6.7 6.7 M 13.8 9.4 L 13.8 13.8 L 9.4 13.8 M 13.8 13.8 L 9.3 9.3");
            // doc.on.doc: one document in front of another; filled while its panel is open.
            case "doc.on.doc":
                return Stroke(frontDocument + " M 3.6 11.8 L 3.4 11.8 A 1.6 1.6 0 1 1.8 10.2 L 1.8 2.6 A 1.6 1.6 0 1 3.4 1 L 9 1 A 1.6 1.6 0 1 10.6 2.6 L 10.6 2.8");
            case "doc.on.doc.fill":
                return Fill(frontDocument + " M 1.8 2.6 A 1.6 1.6 0 1 3.4 1 L 9 1 A 1.6 1.6 0 1 10.6 2.6 L 10.6 3.4 L 6.2 3.4 A 2 2 0 0 4.2 5.4 L 4.2 11.8 L 3.4 11.8 A 1.6 1.6 0 1 1.8 10.2 Z", ink);
            default: throw new ArgumentOutOfRangeException(nameof(symbol), symbol, "no Mighty symbol by that name");
        }
    }

    private static string Ring(double x, double y, double radius) =>
        FormattableString.Invariant($"M {x - radius} {y} A {radius} {radius} 1 1 {x + radius} {y} A {radius} {radius} 1 1 {x - radius} {y} Z ");

    /// <summary>A dot as wide as the line: a stroke too short to have a length, between its two round caps.</summary>
    private static string Dot(double x, double y) => FormattableString.Invariant($"M {x} {y} L {x + 0.01} {y} ");

    /// <summary>checkmark.seal's outline: eight soft lobes round the centre of the box.</summary>
    private static string Seal()
    {
        const double centre = 8, valley = 5.5, lobe = 8.9;
        static (double X, double Y) At(double radius, double degrees) => (centre + radius * Math.Cos(degrees * Math.PI / 180), centre + radius * Math.Sin(degrees * Math.PI / 180));
        var start = At(valley, -112.5);
        var data = new System.Text.StringBuilder(FormattableString.Invariant($"M {start.X:F3} {start.Y:F3} "));
        for (var index = 0; index < 8; index++)
        {
            var control = At(lobe, -90 + index * 45); var end = At(valley, -67.5 + index * 45);
            data.Append(FormattableString.Invariant($"Q {control.X:F3} {control.Y:F3} {end.X:F3} {end.Y:F3} "));
        }
        return data.Append("Z ").ToString();
    }

    /// <summary>
    /// Path data in the 16-unit box, absolute, scaled to the symbol's size: <c>M x y</c>, <c>L x y</c>,
    /// <c>A rx ry large sweep x y</c> (sweep 1 is clockwise), <c>Q x1 y1 x y</c> and <c>Z</c>.
    /// </summary>
    private static PathGeometry Geometry(string data, double scale, bool filled)
    {
        var geometry = new PathGeometry { FillRule = FillRule.Nonzero };
        var parts = data.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var at = 0;
        double Number() => double.Parse(parts[at++], CultureInfo.InvariantCulture) * scale;
        Point Next() { var x = Number(); return new Point(x, Number()); }
        PathFigure? figure = null;
        while (at < parts.Length)
        {
            var command = parts[at++];
            if (command == "M") { geometry.Figures.Add(figure = new PathFigure { StartPoint = Next(), IsFilled = filled }); continue; }
            if (figure is null) throw new FormatException("symbol path data must start with M: " + data);
            switch (command)
            {
                case "L": figure.Segments.Add(new LineSegment { Point = Next() }); break;
                case "Q": { var control = Next(); figure.Segments.Add(new QuadraticBezierSegment { Point1 = control, Point2 = Next() }); break; }
                case "A":
                {
                    var radius = new Size(Number(), Number()); var large = parts[at++] == "1"; var clockwise = parts[at++] == "1";
                    figure.Segments.Add(new ArcSegment { Size = radius, IsLargeArc = large, SweepDirection = clockwise ? SweepDirection.Clockwise : SweepDirection.Counterclockwise, Point = Next() });
                    break;
                }
                case "Z": figure.IsClosed = true; break;
                default: throw new FormatException("unknown symbol path command '" + command + "' in: " + data);
            }
        }
        return geometry;
    }
}
