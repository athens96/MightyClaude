using MightyClaude.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MightyClaude.WinUI;

// Design stage 6 (.omc/plans/windows-design-conversion.md): the chrome the dashboard, the files
// pane, settings, the sheets and the popovers share. Every colour is a shared DesignBrushes brush,
// so a theme toggle recolours an open sheet, popover or settings window in place; every
// lightweight-styling value is written once into the element's own resources before it is shown.
public sealed partial class MainWindow
{
    /// <summary>A sheet's padding, all round (<c>Inset.Sheet</c>; M/ResumeSessionSheet.swift).</summary>
    internal const double SheetPadding = DesignMetrics.Inset.Sheet;
    /// <summary>The start-new-or-resume choice sheet's width and the resume list's size (M/ResumeSessionSheet.swift).</summary>
    internal const double ChoiceSheetWidth = 400, ResumeSheetWidth = 560, ResumeSheetHeight = 520;
    /// <summary>A popover's padding (<c>Inset.Popover</c>; M/StatusBarUsage.swift:76).</summary>
    internal const double PopoverPadding = DesignMetrics.Inset.Popover;

    /// <summary>The corner of a sheet, as macOS rounds its own (measured on docs/design-system/crops/sheet-choice-light.webp; no Swift line sets it).</summary>
    internal const double SheetRadius = 12;
    /// <summary>What a fixed-size sheet leaves clear of the window's edges when the window is smaller than it, and the least it shrinks to.</summary>
    internal const double SheetMargin = DesignMetrics.Spacing.Xl, SheetMinimum = 240;
    /// <summary>A fixed sheet's side as it is drawn in a window with this much room: the side itself, or what the window leaves it.</summary>
    internal static double SheetFit(double side, double room) => room > 0 ? Math.Min(side, Math.Max(SheetMinimum, room - 2 * SheetMargin)) : side;
    /// <summary>A push button as the Mac draws its standard one: 20 high around 13pt words set in 8 (measured on the same crop).</summary>
    internal const double PushButtonHeight = 20;
    /// <summary>A button on the pane's question, permission and plan cards (M/PaneChrome.swift:165): 24 high, padded <c>Spacing.Lg</c> at the sides.</summary>
    internal const double CardButtonHeight = 24;
    internal static readonly Thickness CardButtonPadding = new(DesignMetrics.Spacing.Lg, 0, DesignMetrics.Spacing.Lg, 0);

    /// <summary>
    /// A ContentDialog drawn as the Mac's sheet: the <c>card</c> surface over the content and the
    /// buttons, a 1pt <c>line</c> border and separator, radius 12, padding 20, and when given a fixed
    /// width (and height). The values go into the dialog's own resources once, as it is built and before
    /// it is shown, and they are the window's shared brushes, so an open sheet follows the theme.
    /// The primary button stays the accent default (Enter) and the close button Esc. Call it as the
    /// dialog is built, with its content, before it is shown.
    /// <para>
    /// A <paramref name="bare"/> sheet draws its own title and buttons in its content, laid out as the Mac's
    /// (<see cref="SheetTitle"/>, <see cref="PushButton"/>): it has no stock title or buttons, so no line
    /// under the content, and without a fixed height it is as high as its content.
    /// </para>
    /// </summary>
    internal ContentDialog StyledDialog(ContentDialog dialog, double? width = null, double? height = null, bool bare = false)
    {
        var card = brushes.Brush(DesignToken.Card); var line = brushes.Brush(DesignToken.Line);
        var values = new List<(string Key, object Value)>
        {
            ("ContentDialogBackground", card), ("ContentDialogTopOverlay", card), ("ContentDialogBorderBrush", line), ("ContentDialogSeparatorBorderBrush", line),
            ("ContentDialogForeground", brushes.Brush(DesignToken.Ink)), ("ContentDialogPadding", new Thickness(SheetPadding)),
            // The template rounds the space inside the border by this resource; it follows the sheet's corner.
            ("OverlayCornerRadius", new CornerRadius(SheetRadius)),
        };
        // A window too small for the sheet still shows all of it, its buttons included: the sheet takes what the
        // window has, less SheetMargin a side, and the list inside it scrolls.
        if ((dialog.XamlRoot ?? root.XamlRoot)?.Size is { } room)
        {
            if (width is { } fullWidth) width = SheetFit(fullWidth, room.Width);
            if (height is { } fullHeight) height = SheetFit(fullHeight, room.Height);
        }
        if (width is { } w) { values.Add(("ContentDialogMinWidth", w)); values.Add(("ContentDialogMaxWidth", w)); }
        if (height is { } h) { values.Add(("ContentDialogMinHeight", h)); values.Add(("ContentDialogMaxHeight", h)); }
        if (bare)
        {
            values.Add(("ContentDialogSeparatorThickness", new Thickness(0)));
            if (height is null) values.Add(("ContentDialogMinHeight", 0d));
        }
        if (SetResourcesOnce(dialog, values))
        {
            dialog.Background = card; dialog.BorderBrush = line; dialog.Foreground = brushes.Brush(DesignToken.Ink); dialog.CornerRadius = new CornerRadius(SheetRadius);
            // A dialog opens in a popup outside the window's tree, where it would take the system's theme, not the
            // app's: its stock buttons, check boxes and fields then drew the other theme's inks on this surface.
            // It takes the window's theme as it opens, and follows a toggle while it is open.
            void Follow(FrameworkElement sender, object args) => dialog.RequestedTheme = root.RequestedTheme;
            dialog.Opened += (_, _) => { dialog.RequestedTheme = root.RequestedTheme; root.ActualThemeChanged -= Follow; root.ActualThemeChanged += Follow; };
            dialog.Closed += (_, _) => root.ActualThemeChanged -= Follow;
            dialog.RequestedTheme = root.RequestedTheme;
            // Nor does it inherit the window's language: it takes the tag itself, before it opens.
            dialog.Language = WindowLanguage();
        }
        // Its dimmed words take ink2 and its plain buttons the bordered card look, as in settings.
        ToneSecondaryText(dialog.Content as DependencyObject, paintButtons: true);
        return dialog;
    }

    /// <summary>A sheet's title in its content: the Mac's <c>.headline</c>, 13pt bold <c>ink</c> on a 16-high line.</summary>
    internal TextBlock SheetTitle(string text) => new()
    {
        Text = text, FontSize = DesignMetrics.Type.Title, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = brushes.Brush(DesignToken.Ink),
        LineHeight = 16, LineStackingStrategy = LineStackingStrategy.BlockLineHeight, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>
    /// A push button sized as the Mac's standard one, for a sheet's own button row and the like (<see cref="PushButtonHeight"/>, 13pt
    /// words, h8): the bordered card look of the app's other push buttons, or for the default action
    /// (<paramref name="prominent"/>) filled <c>accent</c> under <c>onAccent</c> words.
    /// </summary>
    internal Button PushButton(string title, bool prominent = false)
    {
        var button = new Button { Content = title, FontSize = DesignMetrics.Type.Body, Height = PushButtonHeight, MinHeight = 0, MinWidth = 0, Padding = new Thickness(DesignMetrics.Spacing.Md, 0, DesignMetrics.Spacing.Md, 0), VerticalAlignment = VerticalAlignment.Center };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, title);
        if (!prominent) return Toned(button);
        button.BorderThickness = new Thickness(0); button.CornerRadius = new CornerRadius(DesignMetrics.Radius.Segment);
        PaintPlainButton(button, brushes.Brush(DesignToken.Accent), brushes.Brush(DesignToken.Accent, 0.9), ink: brushes.Brush(DesignToken.OnAccent));
        return button;
    }

    /// <summary>
    /// A text box the Mac's <c>.textFieldStyle(.plain)</c> way: no fill and no edge in any state, <c>ink</c>
    /// words, AppKit's placeholder in the tertiary ink, and no room of its own around the line. Call it as the box is built.
    /// </summary>
    internal void PaintPlainTextBox(TextBox box)
    {
        box.BorderThickness = new Thickness(0); box.Padding = new Thickness(0); box.MinHeight = 0;
        // The template takes the placeholder's ink from the property in every state; the resources alone do not reach the drawn words (BuildSidebarSearch).
        box.PlaceholderForeground = brushes.Tertiary;
        var values = new List<(string Key, object Value)>();
        foreach (var key in new[] { "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused", "TextControlBackgroundDisabled", "TextControlBorderBrush", "TextControlBorderBrushPointerOver", "TextControlBorderBrushFocused", "TextControlBorderBrushDisabled" }) values.Add((key, brushes.Transparent));
        foreach (var key in new[] { "TextControlForeground", "TextControlForegroundPointerOver", "TextControlForegroundFocused" }) values.Add((key, brushes.Brush(DesignToken.Ink)));
        foreach (var key in new[] { "TextControlPlaceholderForeground", "TextControlPlaceholderForegroundPointerOver", "TextControlPlaceholderForegroundFocused" }) values.Add((key, brushes.Tertiary));
        values.Add(("TextControlBorderThemeThicknessFocused", new Thickness(0)));
        SetResourcesOnce(box, values);
    }

    private Style? cardFlyoutStyle;

    /// <summary>
    /// The popovers' presenter (M/StatusBarUsage.swift:167-312, M/SessionInfoViews.swift): the <c>card</c>
    /// surface, a 1pt <c>line</c>, radius <c>Radius.Entry</c> and padding <see cref="PopoverPadding"/>. One style per window, set on a Flyout as it
    /// is built; its setters hold the shared brushes, so an open popover follows the theme.
    /// </summary>
    internal Style CardFlyoutStyle => cardFlyoutStyle ??= NewCardFlyoutStyle();

    private Style NewCardFlyoutStyle()
    {
        var style = new Style(typeof(FlyoutPresenter));
        style.Setters.Add(new Setter(Control.BackgroundProperty, brushes.Brush(DesignToken.Card)));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, brushes.Brush(DesignToken.Line)));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(DesignMetrics.Stroke.Line)));
        style.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(DesignMetrics.Radius.Entry)));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(PopoverPadding)));
        style.Setters.Add(new Setter(Control.ForegroundProperty, brushes.Brush(DesignToken.Ink)));
        return style;
    }

    /// <summary>
    /// A usage bar (M/DashboardView.swift:205-218): a 6pt <c>runSoft</c> capsule filled to
    /// <paramref name="fraction"/> in <c>run</c>, or <c>waitText</c> once a limit is near. The fill is
    /// the track's only child; its width follows the track's. <paramref name="progress"/> is the usage
    /// popover's bar: the Mac's system progress view tinted <c>accent</c> on its neutral track
    /// (M/StatusBarUsage.swift:259).
    /// </summary>
    internal Grid UsageBar(double fraction, bool warning, bool progress = false)
    {
        var part = Math.Clamp(double.IsFinite(fraction) ? fraction : 0, 0, 1);
        var fill = new Border { Height = UsageBarHeight, CornerRadius = new CornerRadius(UsageBarHeight / 2), HorizontalAlignment = HorizontalAlignment.Left, Background = brushes.Brush(warning ? DesignToken.WaitText : progress ? DesignToken.Accent : DesignToken.Run), Width = 0 };
        var track = new Grid { Height = UsageBarHeight, CornerRadius = new CornerRadius(UsageBarHeight / 2), Background = progress ? brushes.Brush(DesignToken.Ink, ProgressTrackOpacity) : brushes.Brush(DesignToken.RunSoft), VerticalAlignment = VerticalAlignment.Center, MinWidth = 40 };
        track.Children.Add(fill);
        track.SizeChanged += (_, args) => fill.Width = args.NewSize.Width * part;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAccessibilityView(track, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        return track;
    }

    internal const double UsageBarHeight = 6;
    /// <summary>The neutral track of the system progress view, as the context ring draws its own (M/SessionInfoViews.swift:163).</summary>
    internal const double ProgressTrackOpacity = 0.12;

    /// <summary>
    /// The settings and popover rule for secondary words: a TextBlock that was dimmed with an opacity
    /// (and has no colour of its own) takes <c>ink2</c> instead, or <c>ink3</c> for the faintest (under
    /// 0.6), drawn at full opacity, so it keeps AA contrast on the card in both themes. Walks the
    /// element's own children (panels, borders, content); safe to run again as rows are added.
    /// </summary>
    internal void ToneSecondaryText(DependencyObject? node, bool paintButtons = false)
    {
        switch (node)
        {
            case Button button when button.GetType() == typeof(Button):
                if (paintButtons) PaintSettingsButton(button);
                ToneSecondaryText(button.Content as DependencyObject, paintButtons);
                break;
            case TextBlock text:
                if (text.ReadLocalValue(UIElement.OpacityProperty) is double opacity && opacity is > 0 and < 1 && text.ReadLocalValue(TextBlock.ForegroundProperty) == DependencyProperty.UnsetValue)
                {
                    text.Foreground = brushes.Brush(opacity < 0.6 ? DesignToken.Ink3 : DesignToken.Ink2);
                    text.Opacity = 1;
                }
                break;
            case Panel panel:
                foreach (var child in panel.Children) ToneSecondaryText(child, paintButtons);
                break;
            case Border border:
                ToneSecondaryText(border.Child, paintButtons);
                break;
            case ContentControl control:
                ToneSecondaryText(control.Content as DependencyObject, paintButtons);
                break;
        }
    }

    /// <summary>
    /// <see cref="ToneSecondaryText"/> with plain buttons painted, on a subtree that is not attached yet;
    /// returns it, so a row a section or dialog rebuilds later is styled before it is added, never after.
    /// </summary>
    internal T Toned<T>(T element) where T : DependencyObject
    {
        ToneSecondaryText(element, paintButtons: true);
        return element;
    }

    /// <summary>
    /// A stock settings button the Mac's bordered push-button way: <c>card</c> with a 1pt <c>line</c> at
    /// radius 6, the subtle wash under the pointer, <c>ink</c> words (<c>ink3</c> disabled). Only a plain
    /// button no one painted yet, and only while its section is being built, before it enters the tree.
    /// </summary>
    private void PaintSettingsButton(Button button)
    {
        if (button.Style is not null || (writtenResources.TryGetValue(button, out var written) && written.Count > 0)) return;
        button.BorderThickness = new Thickness(DesignMetrics.Stroke.Line); button.CornerRadius = new CornerRadius(DesignMetrics.Radius.Segment);
        PaintPlainButton(button, brushes.Brush(DesignToken.Card), brushes.Subtle, brushes.Brush(DesignToken.Line), brushes.Brush(DesignToken.Ink), brushes.Brush(DesignToken.Ink3));
    }

    /// <summary>
    /// A settings section (M/SettingsViews.swift:192-193, the grouped form): its 13pt semibold heading,
    /// 10 in from the box's edge and 10 over it (with the 베타 capsule after it when <paramref name="beta"/>),
    /// and the box of rows, slightly raised off the sheet (<c>cardRaised</c>) with a 1pt <c>line</c> at
    /// radius 6. The rows carry their own insets and rules (<see cref="SettingsRow"/>). The wrapper keeps
    /// the heading first and the box second, so a section swapped in later replaces <see cref="Border.Child"/>.
    /// </summary>
    private StackPanel BuildSectionContainer(string title, StackPanel body, bool beta = false)
    {
        var wrapper = new StackPanel { Spacing = SettingsHeadingGap };
        var heading = new TextBlock
        {
            Text = title, FontSize = DesignMetrics.Type.Title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = brushes.Brush(DesignToken.Ink),
            LineHeight = 16, LineStackingStrategy = LineStackingStrategy.BlockLineHeight, Margin = new Thickness(SettingsRowInset, 0, SettingsRowInset, 0),
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(heading, "settings-section-" + title);
        if (beta)
        {
            // M/ScreenShareSettingsSection.swift:48-52: the title and the capsule, 6 apart.
            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DesignMetrics.Spacing.Sm };
            line.Children.Add(heading); line.Children.Add(BetaBadgeView.Create(brushes));
            wrapper.Children.Add(line);
        }
        else wrapper.Children.Add(heading);
        var card = new Border
        {
            Child = body, CornerRadius = new CornerRadius(SettingsCardRadius),
            Background = brushes.Brush(DesignToken.CardRaised), BorderBrush = brushes.Brush(DesignToken.Line), BorderThickness = new Thickness(DesignMetrics.Stroke.Line),
        };
        ToneSecondaryText(body, paintButtons: true);
        // Rows a section adds once it has read its state (CLI updates, accounts, toolkit runs) are toned as they arrive.
        card.SizeChanged += (_, _) => ToneSecondaryText(card.Child);
        wrapper.Children.Add(card);
        return wrapper;
    }

    /// <summary>The settings section box's corner (M/SettingsViews.swift:192-193, the grouped form's 6pt box; crops/settings-general-*.webp).</summary>
    internal const double SettingsCardRadius = DesignMetrics.Radius.Segment;
}
