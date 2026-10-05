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
        private readonly PathIcon statusLineGlyph = new() { Width = 14, Height = 11, IsHitTestVisible = false };
        private readonly PathGeometry statusLineGlyphOff = StatusLineGeometry(false), statusLineGlyphOn = StatusLineGeometry(true);

        /// <summary>
        /// The Mac's <c>rectangle</c> (off) and <c>rectangle.bottomthird.inset.filled</c> (on) at 12pt
        /// (M/SessionPaneView.swift:126): a rounded outline about 13.6 × 10.6 with a 1pt line; while on, a bar
        /// fills its lower third, inset from the outline.
        /// </summary>
        private static PathGeometry StatusLineGeometry(bool enabled)
        {
            var shape = new PathGeometry { FillRule = FillRule.EvenOdd };
            void Rounded(double x, double y, double width, double height, double radius)
            {
                var corner = new Size(radius, radius);
                var path = new PathFigure { StartPoint = new Point(x + radius, y), IsClosed = true, IsFilled = true };
                path.Segments.Add(new LineSegment { Point = new Point(x + width - radius, y) });
                path.Segments.Add(new ArcSegment { Point = new Point(x + width, y + radius), Size = corner, SweepDirection = SweepDirection.Clockwise });
                path.Segments.Add(new LineSegment { Point = new Point(x + width, y + height - radius) });
                path.Segments.Add(new ArcSegment { Point = new Point(x + width - radius, y + height), Size = corner, SweepDirection = SweepDirection.Clockwise });
                path.Segments.Add(new LineSegment { Point = new Point(x + radius, y + height) });
                path.Segments.Add(new ArcSegment { Point = new Point(x, y + height - radius), Size = corner, SweepDirection = SweepDirection.Clockwise });
                path.Segments.Add(new LineSegment { Point = new Point(x, y + radius) });
                path.Segments.Add(new ArcSegment { Point = new Point(x + radius, y), Size = corner, SweepDirection = SweepDirection.Clockwise });
                shape.Figures.Add(path);
            }
            Rounded(0.2, 0.2, 13.6, 10.6, 2.2); Rounded(1.2, 1.2, 11.6, 8.6, 1.2);
            if (enabled) Rounded(2.7, 6.2, 8.6, 2.1, 0.6);
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

        private void UpdateStatusLineGlyph()
        {
            var on = statusLineToggle.IsChecked == true;
            statusLineGlyph.Data = on ? statusLineGlyphOn : statusLineGlyphOff;
            // The help says what a press does next (M/SessionPaneView.swift:130).
            ToolTipService.SetToolTip(statusLineToggle, Locale.Get(on ? "composer.statusLine.hide" : "composer.statusLine.show"));
        }
    }
}
