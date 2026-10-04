using MightyClaude.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using ShapePath = Microsoft.UI.Xaml.Shapes.Path;

namespace MightyClaude.WinUI;

/// <summary>
/// A provider's own mark in its brand colours (macOS ProviderIcon.swift / ProviderBadgeIcon).
/// The outline, the colours and which rows get one are <see cref="ProviderMark"/> in Core; this
/// only draws them. Filled with the non-zero rule as on macOS; Gemini's sweep runs from the
/// bottom-left to the top-right corner.
/// </summary>
internal static class ProviderMarkView
{
    /// <summary>The sidebar size: macOS draws the mark at 11 pt beside the 11 pt name.</summary>
    internal const double RowSize = 11;

    /// <summary>
    /// The mark as a menu item's icon (the Mac's add-pane menu draws it at 12, M/WorkspaceView.swift:426),
    /// in the provider's brand fill from the window's shared provider brushes.
    /// </summary>
    internal static PathIcon MenuIcon(string provider, DesignBrushes brushes, double size = 12) =>
        new() { Data = Geometry(provider, size), Foreground = brushes.Provider(provider), Width = size, Height = size };

    private static PathGeometry Geometry(string provider, double size)
    {
        var scale = size / ProviderMark.Box;
        Point At(double x, double y) => new(x * scale, y * scale);
        var geometry = new PathGeometry { FillRule = FillRule.Nonzero };
        foreach (var source in ProviderMark.Figures(provider))
        {
            var figure = new PathFigure { StartPoint = At(source.X, source.Y), IsClosed = source.Closed, IsFilled = true };
            foreach (var segment in source.Segments)
                figure.Segments.Add(segment is GlyphCurve curve
                    ? new BezierSegment { Point1 = At(curve.X1, curve.Y1), Point2 = At(curve.X2, curve.Y2), Point3 = At(curve.X, curve.Y) }
                    : new LineSegment { Point = At(segment.X, segment.Y) });
            geometry.Figures.Add(figure);
        }
        return geometry;
    }

    internal static ShapePath Create(string provider, double size = RowSize)
    {
        var geometry = Geometry(provider, size);
        var colors = ProviderMark.Colors(provider);
        Brush fill;
        if (colors.Count == 1) fill = new SolidColorBrush(Color(colors[0]));
        else
        {
            var gradient = new LinearGradientBrush { StartPoint = new Point(0, 1), EndPoint = new Point(1, 0) };
            for (var i = 0; i < colors.Count; i++) gradient.GradientStops.Add(new GradientStop { Color = Color(colors[i]), Offset = (double)i / (colors.Count - 1) });
            fill = gradient;
        }
        var path = new ShapePath { Data = geometry, Fill = fill, Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
        // Decorative: the name beside it already says which agent it is.
        AutomationProperties.SetAccessibilityView(path, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        return path;
    }

    /// <summary>
    /// <paramref name="text"/> with the mark before its trailing provider name, <c>Request 3 · [mark] Claude</c>
    /// (macOS <c>ProviderBadgeIcon.labelled</c>), for the cards in the main area. The mark is sized
    /// to the line (<see cref="ProviderMark.InlineSize"/>). Text that does not end with the name,
    /// or a provider with no mark, stays one plain text. The whole line reads as
    /// <paramref name="text"/> to a screen reader.
    /// </summary>
    internal static FrameworkElement Labelled(string text, string? provider, double fontSize, Windows.UI.Text.FontWeight weight)
    {
        TextBlock Words(string value) => new() { Text = value, FontSize = fontSize, FontWeight = weight, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        if (ProviderMark.SplitTrailingLabel(text, provider) is not { } parts || ProviderMark.MarkedProvider(provider) is not { } marked) return Words(text);
        // head · mark · name; the name takes what is left so a narrow card trims it.
        var line = new Grid { VerticalAlignment = VerticalAlignment.Center, Tag = marked };
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        // The head's trailing space becomes a fixed gap: a text box may not measure it.
        var head = Words(parts.Head.TrimEnd());
        if (parts.Head.Length > 0) head.Margin = new Thickness(0, 0, 4, 0);
        line.Children.Add(head);
        var mark = Create(marked, ProviderMark.InlineSize(fontSize));
        mark.Margin = new Thickness(0, 0, 4, 0);
        Grid.SetColumn(mark, 1); line.Children.Add(mark);
        var name = Words(parts.Label);
        Grid.SetColumn(name, 2); line.Children.Add(name);
        AutomationProperties.SetName(line, text);
        ToolTipService.SetToolTip(line, text);
        return line;
    }

    /// <summary>The provider a line from <see cref="Labelled"/> carries the mark of, or null for plain text.</summary>
    internal static string? LabelledProvider(FrameworkElement line) =>
        line is Grid { Tag: string provider } grid && grid.Children.OfType<ShapePath>().Any() ? provider : null;

    private static Windows.UI.Color Color(uint rgb) => DesignBrushes.ToColor(new DesignColor(rgb));
}

public sealed partial class MainWindow
{
    /// <summary>The provider each sidebar row's mark stands for, by session id; rows with no mark are absent.</summary>
    private readonly Dictionary<string, string> sidebarMarks = [];

    /// <summary>
    /// The muted line under an agent row's title: <c>[mark] Claude · 4 days ago</c> (macOS WorkspaceView
    /// <c>paneMeta</c> leads with the provider part), 11pt with tabular digits, in <c>ink2</c> on the
    /// selected row and <c>sidebarInk2</c> on the others. Null for a shell, a browser or the files pane.
    /// </summary>
    private FrameworkElement? SidebarProviderLine(RunSession session, bool active)
    {
        if (ProviderMark.SidebarProvider(session) is not { } provider) return null;
        var ink = brushes.Brush(active ? DesignToken.Ink2 : DesignToken.SidebarInk2);
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center, MinHeight = 15 };
        line.Children.Add(ProviderMarkView.Create(provider));
        line.Children.Add(new TextBlock { Text = ProviderMark.Label(provider), FontSize = DesignMetrics.Type.Pill, Foreground = ink, VerticalAlignment = VerticalAlignment.Center });
        var details = new TextBlock { Text = SidebarMeta(session), FontSize = DesignMetrics.Type.Pill, Foreground = ink, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        Microsoft.UI.Xaml.Documents.Typography.SetNumeralAlignment(details, FontNumeralAlignment.Tabular);
        sidebarDetails[session.Id] = details; line.Children.Add(details);
        sidebarMarks[session.Id] = provider;
        return line;
    }

    /// <summary>The meta line's part after the provider name: <c>· 4 days ago</c>, or nothing when there is no detail.</summary>
    private string SidebarMeta(RunSession session) =>
        WorkDashboard.SidebarDetail(WorkDashboard.MakeCard(session, DashboardAttention(session.Id)), DateTimeOffset.UtcNow) is { Length: > 0 } detail ? "· " + detail : "";

    /// <summary>
    /// Smoke key <c>agentMark</c>: every agent row in the sidebar carries its own provider's mark
    /// before the provider's name, rows that are not an agent's carry none, and tabs carry none.
    /// </summary>
    private Dictionary<string, object?> RunAgentMarkSmoke()
    {
        var checks = new Dictionary<string, object?>();
        RenderSidebar();
        var state = service.Snapshot;
        var rows = state.Sessions.Where(s => s.WorkspaceId == state.ActiveWorkspaceId).ToList();
        var agents = rows.Where(s => ProviderMark.SidebarProvider(s) is not null).ToList();
        Require(agents.Count > 0, "agent mark smoke has no agent row");
        foreach (var session in rows)
        {
            var expected = ProviderMark.SidebarProvider(session);
            Require(sidebarMarks.GetValueOrDefault(session.Id) == expected, $"sidebar row {session.Id} mark is {sidebarMarks.GetValueOrDefault(session.Id) ?? "none"}, not {expected ?? "none"}");
        }
        Require(sidebarSessionButtons.Values.Select(b => b.Content).OfType<Grid>().All(g => g.Children[0] is Canvas && (g.Children[1] is TextBlock || g.Children[1] is Grid line && line.Children.Count == 2 && line.Children[0] is TextBlock && line.Children[1] is Border)), "the mark line moved the status glyph or the title");
        checks["providers"] = agents.Select(s => s.Provider).Distinct().OrderBy(p => p).ToList();
        checks["agentRowsCarryTheirMark"] = true;
        checks["otherRowsCarryNone"] = rows.Count - agents.Count;
        return checks;
    }
}
