using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
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

    private void InitializeCompanionGlyph()
    {
        var button = companionToggleControl!;
        button.Content = companionGlyph;
        AutomationProperties.SetAutomationId(button, "companion-toggle");
        AutomationProperties.SetAccessibilityView(companionGlyph, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        void BindForeground()
        {
            if (VisualChildren(button).OfType<ContentPresenter>().FirstOrDefault(p => ReferenceEquals(p.Content, companionGlyph)) is { } presenter)
                companionGlyph.SetBinding(IconElement.ForegroundProperty, new Binding { Source = presenter, Path = new PropertyPath("Foreground"), Mode = BindingMode.OneWay });
        }
        button.Loaded += (_, _) => BindForeground();
        button.ActualThemeChanged += (_, _) => button.DispatcherQueue.TryEnqueue(BindForeground);
    }

    private void CheckSidebarChromeForSmoke()
    {
        var presenter = VisualChildren(companionToggleControl!).OfType<ContentPresenter>().Single(p => ReferenceEquals(p.Content, companionGlyph));
        Require(companionGlyph.Foreground is SolidColorBrush actual && presenter.Foreground is SolidColorBrush inherited && actual.Color == inherited.Color,
            "Pet icon must use the native button foreground in each theme.");
        Require(ReferenceEquals(companionGlyph.Data, companionPreferences.Enabled ? companionGlyphOn : companionGlyphOff), "Pet icon must reflect its enabled state.");
        var footer = VisualChildren(root).OfType<Grid>().Single(g => AutomationProperties.GetAutomationId(g) == "sidebar-footer");
        var themeButton = sidebarThemeButton!;
        Require(settingsButton.IsLoaded && themeButton.IsLoaded && settingsButton.ActualWidth == 28 && themeButton.ActualWidth == 28,
            "Sidebar global actions must remain loaded native controls.");
        var settingsPoint = settingsButton.TransformToVisual(footer).TransformPoint(new Point());
        var themePoint = themeButton.TransformToVisual(footer).TransformPoint(new Point());
        Require(Math.Abs(settingsPoint.Y - themePoint.Y) < 1 && settingsPoint.X > themePoint.X && settingsPoint.X + settingsButton.ActualWidth <= footer.ActualWidth + 1,
            "Sidebar theme and settings must fit beside the footer brand.");
        Require(search.TransformToVisual(root).TransformPoint(new Point()).Y < footer.TransformToVisual(root).TransformPoint(new Point()).Y,
            "Sidebar search must stay above the bottom brand and settings group.");
    }
}
