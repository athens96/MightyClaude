using MightyClaude.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace MightyClaude.WinUI;

/// <summary>
/// The design-token brushes of one main window (its settings window and companion use the
/// same instance). The UI is built in code and its panes are reused across renders, so a
/// brush baked in at construction would keep the old theme's colour; instead every token use
/// takes one shared brush per (token, opacity) from here, and <see cref="Apply"/> recolours
/// each of them in place when the theme changes. This is the one place token colours become
/// WinUI colours (scripts/check-design-tokens.js exempts it).
/// </summary>
internal sealed class DesignBrushes
{
    private readonly Dictionary<(DesignToken Token, double Opacity), SolidColorBrush> brushes = [];
    private readonly Dictionary<string, SolidColorBrush> syntax = [];
    private readonly Dictionary<string, Brush> providers = [];
    private SolidColorBrush? subtle;
    /// <summary>The palette each window's title bar was last given, by <c>AppWindow.Id</c>.</summary>
    private readonly Dictionary<ulong, DesignPalette> titleBars = [];

    /// <summary>The palette the brushes carry now; dark until the first render, like the window before it.</summary>
    internal DesignPalette Palette { get; private set; } = DesignTokens.Dark;

    /// <summary>
    /// The shared brush for a token at an opacity (doc §2 "opacity uses", <see cref="DesignMetrics.Opacity"/>).
    /// Every caller gets the same instance, so a caller must never set its <c>Color</c> or
    /// <c>Opacity</c>: only <see cref="Apply"/> recolours it. A one-off colour is a new token use.
    /// </summary>
    internal SolidColorBrush Brush(DesignToken token, double opacity = 1)
    {
        if (!brushes.TryGetValue((token, opacity), out var brush)) brushes[(token, opacity)] = brush = new SolidColorBrush(ToColor(Palette[token], opacity));
        return brush;
    }

    /// <summary>The neutral wash for hover and selected rows: black by day, white by night, at 0.035. Shared: never mutate it.</summary>
    internal SolidColorBrush Subtle => subtle ??= new SolidColorBrush(ToColor(DesignTokens.Subtle(Palette), DesignMetrics.Opacity.Subtle));

    /// <summary>The files pane's colour for a source span kind (keyword, string, number, comment); null for plain text. Shared: never mutate it.</summary>
    internal SolidColorBrush? Syntax(string kind)
    {
        if (syntax.TryGetValue(kind, out var brush)) return brush;
        if (DesignTokens.Syntax(kind, Palette) is not { } color) return null;
        return syntax[kind] = new SolidColorBrush(ToColor(color));
    }

    /// <summary>
    /// A provider's brand mark fill (Core <see cref="ProviderMark.Colors"/>, held to the fixture's
    /// <c>windowsOnly.provider</c>): one colour, or Gemini's gradient from bottom-leading to
    /// top-trailing (M/ProviderIcon.swift:37-38). The same in both themes. Shared: never mutate it.
    /// </summary>
    internal Brush Provider(string id)
    {
        if (providers.TryGetValue(id, out var brush)) return brush;
        var colors = ProviderMark.Colors(id);
        if (colors.Count == 1) return providers[id] = new SolidColorBrush(ToColor(new DesignColor(colors[0])));
        var gradient = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0, 1), EndPoint = new Windows.Foundation.Point(1, 0) };
        for (var i = 0; i < colors.Count; i++) gradient.GradientStops.Add(new GradientStop { Color = ToColor(new DesignColor(colors[i])), Offset = (double)i / (colors.Count - 1) });
        return providers[id] = gradient;
    }

    /// <summary>Recolours every brush handed out so far to a palette, keeping each instance.</summary>
    internal void Apply(DesignPalette palette)
    {
        if (ReferenceEquals(palette, Palette)) return;
        Palette = palette;
        foreach (var ((token, opacity), brush) in brushes) brush.Color = ToColor(palette[token], opacity);
        if (subtle is not null) subtle.Color = ToColor(DesignTokens.Subtle(palette), DesignMetrics.Opacity.Subtle);
        foreach (var (kind, brush) in syntax) if (DesignTokens.Syntax(kind, palette) is { } color) brush.Color = ToColor(color);
    }

    /// <summary>
    /// The system title bar in the current palette (decision Q2: the system title bar stays and
    /// only takes the token colours): <c>sidebar</c> behind <c>ink</c>, <c>ink2</c> while the window
    /// is inactive, <c>line</c> / <c>track</c> under a hovered / pressed caption button. Values,
    /// not brushes, so the caller re-applies it on every render (M/MightyClaudeApp.swift:44-53); a
    /// window whose bar already carries this palette is skipped. Null (no window) does nothing.
    /// </summary>
    internal void ApplyTitleBar(Microsoft.UI.Windowing.AppWindow? window)
    {
        if (window is null || !Microsoft.UI.Windowing.AppWindowTitleBar.IsCustomizationSupported()) return;
        var p = Palette;
        if (titleBars.TryGetValue(window.Id.Value, out var applied) && ReferenceEquals(applied, p)) return;
        titleBars[window.Id.Value] = p; var bar = window.TitleBar;
        bar.BackgroundColor = ToColor(p.Sidebar); bar.InactiveBackgroundColor = ToColor(p.Sidebar);
        bar.ButtonBackgroundColor = ToColor(p.Sidebar); bar.ButtonInactiveBackgroundColor = ToColor(p.Sidebar);
        bar.ForegroundColor = ToColor(p.Ink); bar.ButtonForegroundColor = ToColor(p.Ink);
        bar.InactiveForegroundColor = ToColor(p.Ink2); bar.ButtonInactiveForegroundColor = ToColor(p.Ink2);
        bar.ButtonHoverBackgroundColor = ToColor(p.Line); bar.ButtonHoverForegroundColor = ToColor(p.Ink);
        bar.ButtonPressedBackgroundColor = ToColor(p.Track); bar.ButtonPressedForegroundColor = ToColor(p.Ink);
    }

    internal static Windows.UI.Color ToColor(DesignColor color, double opacity = 1) =>
        Windows.UI.Color.FromArgb((byte)Math.Round(Math.Clamp(opacity, 0, 1) * 255), color.R, color.G, color.B);

    /// <summary>
    /// The stock controls' theme resources pointed at the tokens: a <see cref="Windows.UI.Color"/>
    /// resource when <c>IsColor</c>, else a <see cref="SolidColorBrush"/>, at the opacity given.
    /// The accent ramp (Light1–3, Dark1–3) and the secondary/tertiary fills are what hover and
    /// pressed states read, so none of them falls back to the OS accent; the accent button's own
    /// keys are set as well (lightweight styling), since its style aliases the accent fill statically.
    /// </summary>
    internal static readonly IReadOnlyList<(string Key, DesignToken Token, double Opacity, bool IsColor)> ControlResources =
    [
        ("SystemAccentColor", DesignToken.Accent, 1, true),
        ("SystemAccentColorLight1", DesignToken.Accent, 1, true), ("SystemAccentColorLight2", DesignToken.Accent, 1, true), ("SystemAccentColorLight3", DesignToken.Accent, 1, true),
        ("SystemAccentColorDark1", DesignToken.Accent, 1, true), ("SystemAccentColorDark2", DesignToken.Accent, 1, true), ("SystemAccentColorDark3", DesignToken.Accent, 1, true),
        ("AccentFillColorDefaultBrush", DesignToken.Accent, 1, false),
        ("AccentFillColorSecondaryBrush", DesignToken.Accent, 0.9, false),
        ("AccentFillColorTertiaryBrush", DesignToken.Accent, 0.8, false),
        ("AccentButtonBackground", DesignToken.Accent, 1, false),
        ("AccentButtonBackgroundPointerOver", DesignToken.Accent, 0.9, false),
        ("AccentButtonBackgroundPressed", DesignToken.Accent, 0.8, false),
        ("TextControlBorderBrushFocused", DesignToken.Accent, 1, false),
        ("TextFillColorPrimaryBrush", DesignToken.Ink, 1, false),
        ("TextFillColorSecondaryBrush", DesignToken.Ink2, 1, false),
        ("CardStrokeColorDefaultBrush", DesignToken.Line, 1, false),
        ("ControlStrokeColorDefaultBrush", DesignToken.Line, 1, false),
        ("SolidBackgroundFillColorBaseBrush", DesignToken.Page, 1, false),
    ];

    /// <summary>The stock controls' font resource, set to <see cref="DesignMetrics.Font.Body"/> in both themes.</summary>
    internal const string ControlFontResource = "ContentControlThemeFontFamily";

    /// <summary>
    /// Writes <see cref="ControlResources"/> into the application's own <c>ThemeDictionaries</c>
    /// ("Light" and "Dark"), in code: the app's only XAML is App.xaml, which the local compile
    /// check cannot see. WinUI reads them before the merged <c>XamlControlsResources</c>; call
    /// it before any control is built. The GUI smoke checks a real accent button resolves them.
    /// </summary>
    internal static void ApplyControlResources(ResourceDictionary resources)
    {
        foreach (var (key, palette) in new[] { ("Light", DesignTokens.Light), ("Dark", DesignTokens.Dark) })
        {
            if (!resources.ThemeDictionaries.TryGetValue(key, out var found) || found is not ResourceDictionary theme)
                resources.ThemeDictionaries[key] = theme = new ResourceDictionary();
            foreach (var (name, token, opacity, isColor) in ControlResources)
                theme[name] = isColor ? ToColor(palette[token], opacity) : new SolidColorBrush(ToColor(palette[token], opacity));
            theme[ControlFontResource] = new FontFamily(DesignMetrics.Font.Body);
        }
    }
}
