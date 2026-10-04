using MightyClaude.Core;
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
            // Only the light/dark brushes change, and both take the window's shared
            // token brushes (recoloured in place on a theme toggle): no fill at rest,
            // the subtle wash under the pointer, ink2 off and accent on, as the Mac's
            // header buttons. The empty HighContrast dictionary lets the platform's
            // system contrast resources resolve unchanged.
            var b = owner.brushes;
            foreach (var key in new[] { "Light", "Dark" })
            {
                var theme = new ResourceDictionary();
                foreach (var suffix in new[] { "", "Checked", "Disabled", "CheckedDisabled" })
                {
                    theme["ToggleButtonBackground" + suffix] = b.Transparent;
                    theme["ToggleButtonBorderBrush" + suffix] = b.Transparent;
                }
                foreach (var suffix in new[] { "PointerOver", "CheckedPointerOver", "Pressed", "CheckedPressed" })
                {
                    theme["ToggleButtonBackground" + suffix] = b.Subtle;
                    theme["ToggleButtonBorderBrush" + suffix] = b.Transparent;
                }
                foreach (var suffix in new[] { "", "PointerOver", "Pressed" })
                    theme["ToggleButtonForeground" + suffix] = b.Brush(DesignToken.Ink2);
                foreach (var suffix in new[] { "Checked", "CheckedPointerOver", "CheckedPressed" })
                    theme["ToggleButtonForeground" + suffix] = b.Brush(DesignToken.Accent);
                statusLineToggle.Resources.ThemeDictionaries[key] = theme;
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
