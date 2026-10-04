using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace MightyClaude.WinUI;

public sealed partial class MainWindow
{
    private sealed partial class PaneView
    {
        private readonly PathIcon statusLineGlyph = new() { Width = 16, Height = 12, IsHitTestVisible = false };
        private readonly PathGeometry statusLineGlyphOff = StatusLineGeometry(false), statusLineGlyphOn = StatusLineGeometry(true);

        private static PathGeometry StatusLineGeometry(bool enabled)
        {
            var shape = new PathGeometry { FillRule = FillRule.EvenOdd };
            void Rect(double x, double y, double width, double height)
            {
                var path = new PathFigure { StartPoint = new Point(x, y), IsClosed = true, IsFilled = true };
                path.Segments.Add(new LineSegment { Point = new Point(x + width, y) });
                path.Segments.Add(new LineSegment { Point = new Point(x + width, y + height) });
                path.Segments.Add(new LineSegment { Point = new Point(x, y + height) }); shape.Figures.Add(path);
            }
            Rect(0, 0, 16, 12); Rect(1.2, 1.2, 13.6, 9.6);
            if (enabled) Rect(2.5, 7.5, 11, 2);
            return shape;
        }

        private void InitializeStatusLineGlyph()
        {
            // Retain the native ToggleButton template, focus and UIA Toggle pattern.
            // Only light/dark brushes change; the empty HighContrast dictionary
            // lets the platform's system contrast resources resolve unchanged.
            foreach (var dark in new[] { false, true })
            {
                var theme = new ResourceDictionary();
                SolidColorBrush Brush(byte alpha, byte shade) => new(Windows.UI.Color.FromArgb(alpha, shade, shade, shade));
                foreach (var suffix in new[] { "", "Checked", "Disabled", "CheckedDisabled" })
                {
                    theme["ToggleButtonBackground" + suffix] = new SolidColorBrush(Colors.Transparent);
                    theme["ToggleButtonBorderBrush" + suffix] = new SolidColorBrush(Colors.Transparent);
                }
                foreach (var suffix in new[] { "PointerOver", "CheckedPointerOver", "Pressed", "CheckedPressed" })
                {
                    theme["ToggleButtonBackground" + suffix] = Brush(suffix.Contains("Pressed", StringComparison.Ordinal) ? (byte)30 : (byte)18, dark ? (byte)255 : (byte)0);
                    theme["ToggleButtonBorderBrush" + suffix] = new SolidColorBrush(Colors.Transparent);
                }
                foreach (var suffix in new[] { "Checked", "CheckedPointerOver", "CheckedPressed" })
                    theme["ToggleButtonForeground" + suffix] = OutlineBrush(dark ? "#7FA3FF" : "#2A5FEE");
                statusLineToggle.Resources.ThemeDictionaries[dark ? "Dark" : "Light"] = theme;
            }
            statusLineToggle.Resources.ThemeDictionaries["HighContrast"] = new ResourceDictionary();
            statusLineToggle.Content = statusLineGlyph;
            AutomationProperties.SetAccessibilityView(statusLineGlyph, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
            AutomationProperties.SetAutomationId(statusLineToggle, "status-line-toggle-" + id);
            void BindGlyph()
            {
                // Read the template's foreground, including hover/pressed/contrast
                // states, instead of pinning a color directly on the icon.
                if (VisualChildren(statusLineToggle).OfType<ContentPresenter>().FirstOrDefault(p => ReferenceEquals(p.Content, statusLineGlyph)) is { } presenter)
                    statusLineGlyph.SetBinding(IconElement.ForegroundProperty, new Binding { Source = presenter, Path = new PropertyPath("Foreground"), Mode = BindingMode.OneWay });
            }
            statusLineToggle.Loaded += (_, _) => BindGlyph();
            statusLineToggle.ActualThemeChanged += (_, _) => statusLineToggle.DispatcherQueue.TryEnqueue(BindGlyph);
            statusLineToggle.Checked += (_, _) => UpdateStatusLineGlyph();
            statusLineToggle.Unchecked += (_, _) => UpdateStatusLineGlyph();
            UpdateStatusLineGlyph();
        }

        private void UpdateStatusLineGlyph() => statusLineGlyph.Data = statusLineToggle.IsChecked == true ? statusLineGlyphOn : statusLineGlyphOff;
    }
}
