using MightyClaude.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace MightyClaude.WinUI;

public sealed partial class MainWindow
{
    private readonly PathIcon companionGlyph = new() { Width = 16, Height = 16, IsHitTestVisible = false };
    private readonly GeometryGroup companionGlyphOn = CompanionPaw(true), companionGlyphOff = CompanionPaw(false);

    private static GeometryGroup CompanionPaw(bool filled)
    {
        var shape = new GeometryGroup { FillRule = FillRule.EvenOdd };
        foreach (var center in new[] { new Point(2.3, 5.5), new Point(6, 2.8), new Point(10, 2.8), new Point(13.7, 5.5) })
        {
            shape.Children.Add(new EllipseGeometry { Center = center, RadiusX = 1.7, RadiusY = 2 });
            if (!filled) shape.Children.Add(new EllipseGeometry { Center = center, RadiusX = .7, RadiusY = 1 });
        }
        var pad = new PathGeometry { FillRule = FillRule.EvenOdd };
        void AddPad(double scale)
        {
            Point P(double x, double y) => new(8 + (x - 8) * scale, 11 + (y - 11) * scale);
            var figure = new PathFigure { StartPoint = P(8, 6.5), IsClosed = true, IsFilled = true };
            figure.Segments.Add(new BezierSegment { Point1 = P(5.7, 6.5), Point2 = P(2, 10.8), Point3 = P(2, 12.8) });
            figure.Segments.Add(new BezierSegment { Point1 = P(2, 16.7), Point2 = P(6, 14.5), Point3 = P(8, 14.5) });
            figure.Segments.Add(new BezierSegment { Point1 = P(10, 14.5), Point2 = P(14, 16.7), Point3 = P(14, 12.8) });
            figure.Segments.Add(new BezierSegment { Point1 = P(14, 10.8), Point2 = P(10.3, 6.5), Point3 = P(8, 6.5) });
            pad.Figures.Add(figure);
        }
        AddPad(1); if (!filled) AddPad(.73); shape.Children.Add(pad); return shape;
    }

    /// <summary>The paw's drawn size: the Mac's 10pt pawprint is about 10.6 across (M/AgentCompanionViews.swift:10).</summary>
    internal const double CompanionGlyphSize = 10.6;

    private void InitializeCompanionGlyph()
    {
        var button = companionToggleControl!;
        // The outline is drawn on a 16-unit box; the viewbox brings it to the status bar's 10pt symbols.
        button.Content = new Viewbox { Width = CompanionGlyphSize, Height = CompanionGlyphSize, Child = companionGlyph };
        AutomationProperties.SetAutomationId(button, "companion-toggle");
        AutomationProperties.SetAccessibilityView(companionGlyph, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
    }

    /// <summary>
    /// The paw as the Mac draws it (M/AgentCompanionViews.swift:10-11): filled and <c>accent</c> while the
    /// pet shows, outlined and <c>ink2</c> otherwise. Both are shared brushes set on the symbol itself
    /// (a look that changes at runtime goes on the content), so a theme toggle recolours it in place.
    /// </summary>
    private void PaintCompanionGlyph(bool enabled)
    {
        companionGlyph.Data = enabled ? companionGlyphOn : companionGlyphOff;
        companionGlyph.Foreground = brushes.Brush(enabled ? DesignToken.Accent : DesignToken.Ink2);
    }

}
