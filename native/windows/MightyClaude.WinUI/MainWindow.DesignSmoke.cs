using System.Text.Json;
using MightyClaude.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MightyClaude.WinUI;

// The design conversion's smoke assertions (.omc/plans/windows-design-conversion.md). Every
// colour is read off the element tree and compared with native/contracts/fixtures/design-tokens.json
// itself, not with the C# palette, and every failure names the token, the expected hex and the
// actual one, so the public check-run annotation alone says what went wrong.
public sealed partial class MainWindow
{
    private static readonly Lazy<JsonElement> DesignFixture = new(() =>
    {
        const string name = "MightyClaude.WinUI.DesignTokens.json";
        using var stream = typeof(MainWindow).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException(name + " is not embedded in the WinUI app");
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.Clone();
    });

    private string SmokeTheme => service.Snapshot.Theme == "light" ? "light" : "dark";

    private static string FixtureHex(string theme, DesignToken token) =>
        DesignFixture.Value.GetProperty(theme).GetProperty(char.ToLowerInvariant(token.ToString()[0]) + token.ToString()[1..]).GetString()!;

    private static string Describe(Brush? brush) => brush switch
    {
        SolidColorBrush solid => $"#{solid.Color.A:X2}{solid.Color.R:X2}{solid.Color.G:X2}{solid.Color.B:X2}",
        null => "no brush",
        _ => brush.GetType().Name,
    };

    /// <summary>
    /// A brush read off an element is the token's colour in the current theme at an opacity:
    /// alpha round(opacity × 255), each channel within 1 of the fixture's hex.
    /// </summary>
    private void RequireBrush(FrameworkElement element, Func<FrameworkElement, Brush?> get, DesignToken token, string what, double opacity = 1)
    {
        var theme = SmokeTheme;
        var expected = FixtureHex(theme, token);
        var actual = get(element);
        var rgb = Convert.ToUInt32(expected[1..], 16);
        var alpha = (int)Math.Round(opacity * 255);
        Require(actual is SolidColorBrush { Color: var c }
            && Math.Abs(c.A - alpha) <= 1 && Math.Abs(c.R - (int)(rgb >> 16 & 0xFF)) <= 1 && Math.Abs(c.G - (int)(rgb >> 8 & 0xFF)) <= 1 && Math.Abs(c.B - (int)(rgb & 0xFF)) <= 1,
            $"designTokens ({theme}): {what} must be token {token} {expected} at opacity {opacity}; got {Describe(actual)}");
    }

    private static void RequireRadius(Border border, double radius, string what) =>
        Require(border.CornerRadius == new CornerRadius(radius), $"designTokens: {what} must have radius {radius}; got {border.CornerRadius}");

    private static void RequireThickness(Border border, double width, string what) =>
        Require(border.BorderThickness == new Thickness(width), $"designTokens: {what} must have a {width} border; got {border.BorderThickness}");

    /// <summary>
    /// Stage 1 in the theme just rendered: the window background is the shared page brush with
    /// the fixture's page hex; every stock-control resource DesignBrushes writes is in both theme
    /// dictionaries with its token's fixture hex; and a real accent button in the tree resolves
    /// its background to the accent, which shows WinUI honours ThemeDictionaries added at runtime.
    /// </summary>
    private void RequireDesignTokensInTheme(Button accentProbe)
    {
        var theme = SmokeTheme;
        Require(root.RequestedTheme == (theme == "light" ? ElementTheme.Light : ElementTheme.Dark), $"designTokens ({theme}): the window did not take the {theme} theme");
        Require(ReferenceEquals(root.Background, brushes.Brush(DesignToken.Page)), $"designTokens ({theme}): the window background is not the shared page brush; got {Describe(root.Background)}");
        RequireBrush(root, e => ((Grid)e).Background, DesignToken.Page, "the window background");
        foreach (var key in new[] { "Light", "Dark" })
        {
            var themeResources = Application.Current.Resources.ThemeDictionaries.TryGetValue(key, out var dictionary) ? dictionary as ResourceDictionary : null;
            Require(themeResources is not null, $"designTokens ({theme}): Application.Resources.ThemeDictionaries has no {key} dictionary");
            foreach (var (name, token, opacity, isColor) in DesignBrushes.ControlResources)
            {
                var expected = FixtureHex(key.ToLowerInvariant(), token);
                var want = $"#{(int)Math.Round(opacity * 255):X2}{expected[1..]}";
                var value = themeResources!.TryGetValue(name, out var found) ? found : null;
                var got = value switch { Windows.UI.Color c => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}", Brush b => Describe(b), null => "missing", _ => value.GetType().Name };
                Require(got == want && (value is Windows.UI.Color) == isColor, $"designTokens ({theme}): ThemeDictionaries[{key}][{name}] must be token {token} {expected} at opacity {opacity}; got {got}");
            }
            var font = themeResources!.TryGetValue(DesignBrushes.ControlFontResource, out var family) ? (family as FontFamily)?.Source : null;
            Require(font == DesignMetrics.Font.Body, $"designTokens ({theme}): ThemeDictionaries[{key}][{DesignBrushes.ControlFontResource}] must be '{DesignMetrics.Font.Body}'; got '{font ?? "missing"}'");
        }
        accentProbe.UpdateLayout(); root.UpdateLayout();
        RequireBrush(accentProbe, e => ((Control)e).Background, DesignToken.Accent,
            "the resolved Background of a stock AccentButtonStyle button (anything else means WinUI ignored the ThemeDictionaries DesignBrushes.ApplyControlResources added at runtime, and the overrides must move to App.xaml)");
    }

    /// <summary>A stock accent button in the window, invisible and inert, that the theme checks read; removed by the caller.</summary>
    private Button AddAccentProbe()
    {
        // In the star row, top-left over the sidebar, so the auto rows and the layout keep their size.
        var probe = new Button { Content = "Aa", Style = (Style)Application.Current.Resources["AccentButtonStyle"], Opacity = 0, IsHitTestVisible = false, IsTabStop = false, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAccessibilityView(probe, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        Grid.SetRow(probe, 1); root.Children.Add(probe); probe.ApplyTemplate(); root.UpdateLayout();
        return probe;
    }

    /// <summary>
    /// After the toggle: the brushes handed out before it are the same instances, recoloured to
    /// the new theme's hex in place, so a reused pane follows the theme without being rebuilt.
    /// </summary>
    private void RequireDesignBrushesRecolouredInPlace(SolidColorBrush page, SolidColorBrush card, string before)
    {
        var theme = SmokeTheme;
        Require(theme != before, $"designTokens: the theme did not change from {before}");
        Require(ReferenceEquals(page, brushes.Brush(DesignToken.Page)) && ReferenceEquals(card, brushes.Brush(DesignToken.Card)), $"designTokens ({theme}): the page or card brush was replaced instead of recoloured");
        Require(ReferenceEquals(root.Background, page), $"designTokens ({theme}): the window background left the shared page brush; got {Describe(root.Background)}");
        var expected = FixtureHex(theme, DesignToken.Card);
        Require(Describe(card) == "#FF" + expected[1..] && expected != FixtureHex(before, DesignToken.Card), $"designTokens ({theme}): the card brush must hold token Card {expected} after the toggle from {before}; got {Describe(card)}");
    }
}
