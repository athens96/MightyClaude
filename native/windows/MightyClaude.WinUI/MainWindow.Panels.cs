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
    /// <summary>A sheet's padding (M/ResumeSessionSheet.swift: 20 all round).</summary>
    internal const double SheetPadding = 20;
    /// <summary>The start-new-or-resume choice sheet's width and the resume list's size (M/ResumeSessionSheet.swift).</summary>
    internal const double ChoiceSheetWidth = 400, ResumeSheetWidth = 560, ResumeSheetHeight = 520;
    /// <summary>A popover's padding and corner (M/StatusBarUsage.swift:76; the slash palette's radius 10).</summary>
    internal const double PopoverPadding = 16;

    /// <summary>
    /// A ContentDialog drawn as the Mac's sheet: the <c>card</c> surface over the content and the
    /// buttons, a 1pt <c>line</c> border and separator, padding 20, and when given a fixed width
    /// (and height). The values go into the dialog's own resources once, as it is built and before
    /// it is shown, and they are the window's shared brushes, so an open sheet follows the theme.
    /// The primary button stays the accent default (Enter) and the close button Esc. Call it as the
    /// dialog is built, with its content, before it is shown.
    /// </summary>
    internal ContentDialog StyledDialog(ContentDialog dialog, double? width = null, double? height = null)
    {
        var card = brushes.Brush(DesignToken.Card); var line = brushes.Brush(DesignToken.Line);
        var values = new List<(string Key, object Value)>
        {
            ("ContentDialogBackground", card), ("ContentDialogTopOverlay", card), ("ContentDialogBorderBrush", line), ("ContentDialogSeparatorBorderBrush", line),
            ("ContentDialogForeground", brushes.Brush(DesignToken.Ink)), ("ContentDialogPadding", new Thickness(SheetPadding)),
        };
        if (width is { } w) { values.Add(("ContentDialogMinWidth", w)); values.Add(("ContentDialogMaxWidth", w)); }
        if (height is { } h) { values.Add(("ContentDialogMinHeight", h)); values.Add(("ContentDialogMaxHeight", h)); }
        if (SetResourcesOnce(dialog, values)) { dialog.Background = card; dialog.BorderBrush = line; dialog.Foreground = brushes.Brush(DesignToken.Ink); }
        // Its dimmed words take ink2 and its plain buttons the bordered card look, as in settings.
        ToneSecondaryText(dialog.Content as DependencyObject, paintButtons: true);
        return dialog;
    }

    private Style? cardFlyoutStyle;

    /// <summary>
    /// The popovers' presenter (M/StatusBarUsage.swift:167-312, M/SessionInfoViews.swift): the <c>card</c>
    /// surface, a 1pt <c>line</c>, radius 10 and padding 16. One style per window, set on a Flyout as it
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
    /// the track's only child; its width follows the track's.
    /// </summary>
    internal Grid UsageBar(double fraction, bool warning)
    {
        var part = Math.Clamp(double.IsFinite(fraction) ? fraction : 0, 0, 1);
        var fill = new Border { Height = UsageBarHeight, CornerRadius = new CornerRadius(UsageBarHeight / 2), HorizontalAlignment = HorizontalAlignment.Left, Background = brushes.Brush(warning ? DesignToken.WaitText : DesignToken.Run), Width = 0 };
        var track = new Grid { Height = UsageBarHeight, CornerRadius = new CornerRadius(UsageBarHeight / 2), Background = brushes.Brush(DesignToken.RunSoft), VerticalAlignment = VerticalAlignment.Center, MinWidth = 40 };
        track.Children.Add(fill);
        track.SizeChanged += (_, args) => fill.Width = args.NewSize.Width * part;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAccessibilityView(track, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        return track;
    }

    internal const double UsageBarHeight = 6;

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
    /// A settings section (M/SettingsViews.swift, the grouped form): its 13pt semibold heading over
    /// one <c>card</c> block with a 1pt <c>line</c> at radius 10. The wrapper keeps the heading first and
    /// the card second, so a section swapped in later replaces <see cref="Border.Child"/>.
    /// </summary>
    private StackPanel BuildSectionContainer(string title, StackPanel body)
    {
        var wrapper = new StackPanel { Spacing = 8, Padding = new(0, 0, 0, 20) };
        var heading = new TextBlock { Text = title, FontSize = DesignMetrics.Type.Title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = brushes.Brush(DesignToken.Ink) };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(heading, "settings-section-" + title);
        wrapper.Children.Add(heading);
        var card = new Border
        {
            Child = body, Padding = new Thickness(14, 12, 14, 12), CornerRadius = new CornerRadius(SettingsCardRadius),
            Background = brushes.Brush(DesignToken.Card), BorderBrush = brushes.Brush(DesignToken.Line), BorderThickness = new Thickness(DesignMetrics.Stroke.Line),
        };
        ToneSecondaryText(body, paintButtons: true);
        // Rows a section adds once it has read its state (CLI updates, accounts, toolkit runs) are toned as they arrive.
        card.SizeChanged += (_, _) => ToneSecondaryText(card.Child);
        wrapper.Children.Add(card);
        return wrapper;
    }

    /// <summary>The settings section card's corner (M/SettingsViews.swift, the grouped form; doc §6 settings sheet).</summary>
    internal const double SettingsCardRadius = DesignMetrics.Radius.Entry;
}
