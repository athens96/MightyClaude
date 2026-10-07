using MightyClaude.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Ellipse = Microsoft.UI.Xaml.Shapes.Ellipse;
using ShapePath = Microsoft.UI.Xaml.Shapes.Path;

namespace MightyClaude.WinUI;

public sealed partial class MainWindow
{
    private string settingsCategory = "general";
    private Window? settingsWindow;
    private XamlRoot SettingsXamlRoot => (settingsWindow?.Content as FrameworkElement)?.XamlRoot ?? root.XamlRoot;
    private async Task ShowCategorizedSettingsAsync()
    {
        // Asked for again while it is open, the settings window comes forward.
        if (settingsWindow is { } open) { open.Activate(); return; }
        if (dialogOpen) return;
        dialogOpen = true;
        try
        {
            settingsCategory = service.Snapshot.SettingsPane;
            // The Mac's sidebar list (M/SettingsViews.swift:206-215): its rows' fill 10 in from the column's edges, 30 high.
            var navigation = new ListView { SelectionMode = ListViewSelectionMode.Single, Padding = new(SettingsRowInset - ListSelectionInsetX, SettingsRowInset, SettingsRowInset - ListSelectionInsetX, SettingsRowInset) };
            AutomationProperties.SetAutomationId(navigation, "settings-navigation");
            PaintSettingsNavigation(navigation);
            // The grouped form (M/SettingsViews.swift:192-193): 20 in from the column's edges, 30 between a box and the next heading.
            var content = new StackPanel { Spacing = SettingsGroupGap, Padding = new(SettingsFormInset) };
            var scroll = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            void Display(SettingsCategory category)
            {
                settingsCategory = category.Id;
                content.Children.Clear();
                // The account statuses are read again as their tab opens (M/CLIAccountsSettingsView.swift:15); its rows' rings turn meanwhile.
                var reads = category.Id == "cli" && !options.SmokeTest;
                if (reads) settingsAccountRefreshes++;
                foreach (var group in SettingsGroups(category.Id)) content.Children.Add(BuildSectionContainer(group.Title, group.Build(), group.Beta));
                scroll.ChangeView(null, 0, null, true);
                // Refresh only the still-visible account section; an async CLI
                // result must not replace the category selected in the meantime.
                if (reads) _ = RefreshVisibleSettingsAccountsAsync(content);
            }
            foreach (var category in SettingsNavigation.Available)
            {
                // The symbol sits in an 18-wide slot centred 16 from the row's edge, the label 30 from it (crops/settings-general-*.webp).
                var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DesignMetrics.Spacing.Xs, VerticalAlignment = VerticalAlignment.Center };
                var symbol = SettingsCategorySymbol(category.Id);
                label.Children.Add(new Grid { Width = 18, Children = { symbol }, VerticalAlignment = VerticalAlignment.Center });
                label.Children.Add(new TextBlock { Text = category.Title, FontSize = DesignMetrics.Type.Title, VerticalAlignment = VerticalAlignment.Center });
                var item = new ListViewItem
                {
                    Content = label, Tag = category, Padding = new(DesignMetrics.Spacing.Sm + ListSelectionInsetX, 0, DesignMetrics.Spacing.Sm + ListSelectionInsetX, 0), CornerRadius = new(DesignMetrics.Radius.FileRow),
                    MinHeight = 0, Height = SettingsNavigationRowHeight + 2 * ListSelectionInsetY, Margin = new(0, -ListSelectionInsetY, 0, -ListSelectionInsetY),
                };
                AutomationProperties.SetName(item, category.Title);
                AutomationProperties.SetAutomationId(item, "settings-nav-" + category.Id); navigation.Items.Add(item);
            }
            // The symbol is accent on every row but the chosen one, where it takes onAccent with the label (M/SettingsViews.swift:208).
            void PaintSymbols()
            {
                foreach (var item in navigation.Items.OfType<ListViewItem>())
                {
                    var ink = brushes.Brush(ReferenceEquals(item, navigation.SelectedItem) ? DesignToken.OnAccent : DesignToken.Accent);
                    if (item.Content is not StackPanel { Children: [Grid { Children: [var mark] }, ..] }) continue;
                    if (mark is IconElement icon) icon.Foreground = ink; else if (mark is ShapePath path) path.Fill = ink;
                }
            }
            navigation.SelectionChanged += async (_, _) =>
            {
                PaintSymbols();
                if (navigation.SelectedItem is not ListViewItem { Tag: SettingsCategory category }) return;
                Display(category);
                await Act(() => service.UpdateAsync(s => s with { SettingsPane = category.Id }));
            };
            navigation.SelectedItem = navigation.Items.OfType<ListViewItem>().FirstOrDefault(item => ((SettingsCategory)item.Tag).Id == settingsCategory) ?? navigation.Items[0];
            PaintSymbols();
            // A separate settings window leaves the main XamlRoot available
            // for account/reset confirmations (WinUI forbids nested dialogs).
            // The Mac's 800x700 sheet (M/SettingsViews.swift:186-201): the heading, the 200-wide list on the sidebar
            // surface, a 1pt rule, the grouped form on the sheet's own surface, and the close row under them.
            var frame = new Grid { RequestedTheme = root.RequestedTheme, Language = WindowLanguage(), Background = WindowBackground(), KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden };
            frame.RowDefinitions.Add(new() { Height = GridLength.Auto });
            frame.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
            frame.RowDefinitions.Add(new() { Height = GridLength.Auto });
            var window = new Window { Title = Locale.Get("settings.title"), Content = frame };
            settingsWindow = window;
            Task CloseWindow() { window.Close(); return Task.CompletedTask; }
            var heading = SettingsHeading(CloseWindow);
            frame.Children.Add(heading);
            var body = new Grid();
            body.ColumnDefinitions.Add(new() { Width = new(SettingsNavigationWidth) });
            body.ColumnDefinitions.Add(new() { Width = new(DesignMetrics.Stroke.Line) });
            body.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            var navigationHost = new Border { Child = navigation, Background = brushes.Brush(DesignToken.Sidebar) };
            AutomationProperties.SetAutomationId(navigationHost, "settings-navigation-host");
            body.Children.Add(navigationHost);
            var rule = new Border { Background = brushes.Brush(DesignToken.Line) };
            Grid.SetColumn(rule, 1); body.Children.Add(rule);
            var form = new Border { Child = scroll, Background = brushes.Brush(DesignToken.Card) };
            AutomationProperties.SetAutomationId(form, "settings-form");
            Grid.SetColumn(form, 2); body.Children.Add(form);
            Grid.SetRow(body, 1); frame.Children.Add(body);
            // The close row: padding 18 around the one button (M/SettingsViews.swift:199).
            var footer = new Grid { Padding = new(DesignMetrics.Spacing.Lg), Background = brushes.Brush(DesignToken.Card), BorderBrush = brushes.Brush(DesignToken.Line), BorderThickness = new(0, DesignMetrics.Stroke.Line, 0, 0) };
            AutomationProperties.SetAutomationId(footer, "settings-footer");
            Grid.SetRow(footer, 2); frame.Children.Add(footer);
            var close = SettingsPush(Button(Locale.Get("settings.closeButton"), CloseWindow));
            close.HorizontalAlignment = HorizontalAlignment.Right; AutomationProperties.SetAutomationId(close, "settings-close"); footer.Children.Add(close);
            // Esc closes the sheet, as the Mac's cancel action does; an open menu, pop-up or confirmation takes the key first.
            var escape = new KeyboardAccelerator { Key = VirtualKey.Escape };
            escape.Invoked += (_, args) =>
            {
                // A tooltip is a pop-up too, but it has no use for the key: only a menu, a picker's list or a confirmation takes it first.
                if (frame.XamlRoot is null || VisualTreeHelper.GetOpenPopupsForXamlRoot(frame.XamlRoot).Any(popup => popup.Child is not ToolTip)) return;
                args.Handled = true; window.Close();
            };
            frame.KeyboardAccelerators.Add(escape);
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            // Dropped at once, not after the awaiting continuation runs: a Render in between must not
            // touch the closed window's AppWindow.
            window.Closed += (_, _) => { if (ReferenceEquals(settingsWindow, window)) settingsWindow = null; closed.TrySetResult(); };
            window.AppWindow.ResizeClient(SettingsWindowSize(window.AppWindow)); brushes.ApplyTitleBar(window.AppWindow);
            window.Activate();
            await closed.Task;
        }
        finally { settingsWindow = null; dialogOpen = false; }
    }
    /// <summary>The settings sheet's content size in points and its side list's width (M/SettingsViews.swift:201, 214).</summary>
    internal const double SettingsWindowWidth = 800, SettingsWindowHeight = 700, SettingsNavigationWidth = 200;

    /// <summary>A side-list row's height: the sidebar list's 22 plus the label's 2 above and below (M/SettingsViews.swift:209).</summary>
    internal const double SettingsNavigationRowHeight = 22 + 2 * DesignMetrics.Spacing.Xxs;

    /// <summary>
    /// The stock list row draws its rounded fill 4 in from its sides and 2 in from its top and bottom. A
    /// settings row is set that much larger and overlaps its neighbours by it, so the fill is the Mac's
    /// 180×30 and the rows keep their 30 pitch.
    /// </summary>
    internal const double ListSelectionInsetX = 4, ListSelectionInsetY = 2;

    /// <summary>The space kept free around the settings window on a work area too small for it, in points.</summary>
    internal const double SettingsWindowMargin = DesignMetrics.Spacing.Xl;

    /// <summary>
    /// The settings window's content size in physical pixels: the Mac sheet's 800×700 points at the display
    /// scale (the title bar and frame come on top of it), but never more than the display's work area less
    /// <see cref="SettingsWindowMargin"/> on each side.
    /// </summary>
    internal Windows.Graphics.SizeInt32 SettingsWindowSize(Microsoft.UI.Windowing.AppWindow window)
    {
        var scale = root.XamlRoot?.RasterizationScale ?? 1;
        var area = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(window.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Nearest)?.WorkArea;
        var margin = (int)Math.Round(2 * SettingsWindowMargin * scale);
        int Fit(double points, int? available) => Math.Max(1, Math.Min((int)Math.Round(points * scale), available is { } free && free > margin ? free - margin : int.MaxValue));
        return new(Fit(SettingsWindowWidth, area?.Width), Fit(SettingsWindowHeight, area?.Height));
    }

    /// <summary>The side list's symbol per category, the Mac's SF Symbols in Segoe Fluent Icons (M/SettingsViews.swift:167-178).</summary>
    private static string SettingsCategoryGlyph(string id) => id switch
    {
        "general" => "", "models" => "", "styles" => "", "tools" => "",
        "cli" => "", "mobile" => "", "about" => "", _ => "",
    };

    /// <summary>
    /// A side-list row's symbol at the Mac's 14pt. Segoe Fluent Icons has no paw, so the pet row draws
    /// the outline paw the sidebar's pet button uses (the Mac's <c>pawprint</c>).
    /// </summary>
    private static FrameworkElement SettingsCategorySymbol(string id)
    {
        FrameworkElement symbol = id == "companion"
            ? new ShapePath { Data = CompanionPaw(false), Width = 14, Height = 14, Stretch = Stretch.Uniform }
            : new FontIcon { Glyph = SettingsCategoryGlyph(id), FontSize = 14 };
        symbol.HorizontalAlignment = HorizontalAlignment.Center; symbol.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetAccessibilityView(symbol, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        return symbol;
    }

    /// <summary>The heading's padding, all round.</summary>
    internal const double SettingsHeadingPadding = DesignMetrics.Spacing.Lg;
    /// <summary>The heading's height with its rule: its padding around the 17pt title over the 11pt subtitle (38 with the rule; crops/settings-general-*.webp).</summary>
    internal const double SettingsHeadingHeight = 2 * SettingsHeadingPadding + 38;

    /// <summary>
    /// The sheet heading (M/SettingsViews.swift:325-338): a 21pt light gear in <c>accent</c> in a 31-wide
    /// column, the 17pt semibold title over an 11pt <c>ink2</c> subtitle, the round 17pt close mark at the
    /// trailing edge, in <see cref="SettingsHeadingPadding"/>, on <c>card</c> with a <c>line</c> under it.
    /// </summary>
    private Grid SettingsHeading(Func<Task> close)
    {
        var heading = new Grid { Height = SettingsHeadingHeight, Padding = new(SettingsHeadingPadding), ColumnSpacing = DesignMetrics.Spacing.Md, Background = brushes.Brush(DesignToken.Card), BorderBrush = brushes.Brush(DesignToken.Line), BorderThickness = new(0, 0, 0, DesignMetrics.Stroke.Line) };
        heading.ColumnDefinitions.Add(new() { Width = new(31) });
        heading.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        heading.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var icon = new FontIcon { Glyph = "", FontSize = 21, FontWeight = Microsoft.UI.Text.FontWeights.Light, Foreground = brushes.Brush(DesignToken.Accent), VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAccessibilityView(icon, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        heading.Children.Add(icon);
        var words = new StackPanel { Spacing = DesignMetrics.Spacing.Xs, VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(new TextBlock { Text = Locale.Get("settings.title"), FontSize = DesignMetrics.Type.Header, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = brushes.Brush(DesignToken.Ink), LineHeight = 20, LineStackingStrategy = LineStackingStrategy.BlockLineHeight });
        words.Children.Add(new TextBlock { Text = "Windows · WinUI", FontSize = DesignMetrics.Type.Pill, Foreground = brushes.Brush(DesignToken.Ink2), LineHeight = 13, LineStackingStrategy = LineStackingStrategy.BlockLineHeight });
        Grid.SetColumn(words, 1); heading.Children.Add(words);
        // xmark.circle.fill at 17 in the tertiary ink (M/SettingsViews.swift:336): a disc with the cross cut out of it in the sheet's own surface.
        var mark = new Grid { Width = 17, Height = 17 };
        mark.Children.Add(new Ellipse { Fill = brushes.Tertiary });
        mark.Children.Add(new ShapePath
        {
            Data = new GeometryGroup { Children = { new LineGeometry { StartPoint = new(5.6, 5.6), EndPoint = new(11.4, 11.4) }, new LineGeometry { StartPoint = new(11.4, 5.6), EndPoint = new(5.6, 11.4) } } },
            Stroke = brushes.Brush(DesignToken.Card), StrokeThickness = 1.6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
        });
        var name = Locale.Get("settings.closeButton");
        var dismiss = Button(name, close);
        dismiss.Content = mark; dismiss.Width = 17; dismiss.Height = 17; dismiss.MinWidth = 0; dismiss.MinHeight = 0; dismiss.Padding = new(0); dismiss.BorderThickness = new(0); dismiss.CornerRadius = new(8.5); dismiss.VerticalAlignment = VerticalAlignment.Center;
        // The symbol's own side bearing keeps the Mac's disc a point inside the heading's padding.
        dismiss.Margin = new(0, 0, 1, 0);
        PaintPlainButton(dismiss, brushes.Transparent, brushes.Transparent);
        ToolTipService.SetToolTip(dismiss, name); AutomationProperties.SetAutomationId(dismiss, "settings-heading-close");
        Grid.SetColumn(dismiss, 2); heading.Children.Add(dismiss);
        AutomationProperties.SetAutomationId(heading, "settings-heading");
        return heading;
    }

    /// <summary>
    /// The side list's rows the Mac's sidebar way: the chosen one filled with <c>accent</c> under
    /// <c>onAccent</c> words and symbol, the others <c>ink</c> with the subtle wash under the pointer,
    /// and no stock selection bar. Written once into the list's own resources, before it is shown.
    /// </summary>
    private void PaintSettingsNavigation(ListView navigation)
    {
        var accent = brushes.Brush(DesignToken.Accent); var onAccent = brushes.Brush(DesignToken.OnAccent); var ink = brushes.Brush(DesignToken.Ink);
        var values = new List<(string Key, object Value)>();
        foreach (var state in new[] { "", "PointerOver", "Pressed" })
        {
            values.Add(("ListViewItemBackgroundSelected" + state, accent));
            values.Add(("ListViewItemForegroundSelected" + state, onAccent));
            values.Add(("ListViewItemForeground" + state, ink));
            values.Add(("ListViewItemSelectionIndicator" + state + "Brush", brushes.Transparent));
        }
        values.Add(("ListViewItemBackgroundPointerOver", brushes.Subtle)); values.Add(("ListViewItemBackgroundPressed", brushes.Subtle));
        SetResourcesOnce(navigation, values);
    }

    private async Task RefreshVisibleSettingsAccountsAsync(StackPanel content)
    {
        try { await RefreshCliAccounts(); }
        finally
        {
            settingsAccountRefreshes--;
            // Drawn again whether or not the read worked, so the rows' rings stop with it. A section keeps its own
            // panel and draws its rows again inside it (the call is the panel's Tag): a button pressed meanwhile
            // goes on working on the rows that show. The CLI rows follow the runtime the same read brought.
            if (!closing && dialogOpen && settingsCategory == "cli")
                foreach (var title in new[] { SettingsSections.ProvidersTitle, CliAccountStrings.SectionTitle })
                    if (content.Children.OfType<StackPanel>().FirstOrDefault(section => section.Children.OfType<TextBlock>().FirstOrDefault()?.Text == title) is { Children: [_, Border { Child: StackPanel { Tag: Action redraw } }, ..] }) redraw();
        }
    }

    // ── The grouped form (M/SettingsViews.swift:192-193, .formStyle(.grouped)) ────────────────────────────
    // A heading over a box of rows; every row is one line with its words at the left and its control at the
    // right, SettingsRowInset in from the box and above and below, a 1pt rule between rows. The control sizes are
    // read off docs/design-system/crops/settings-general-*.webp and screens/10-settings-*.webp (2 px = 1 pt).

    /// <summary>The form's inset from its column, and the space from one box to the next heading.</summary>
    internal const double SettingsFormInset = DesignMetrics.Spacing.Lg, SettingsGroupGap = DesignMetrics.Spacing.Xl;
    /// <summary>The space under a heading, a row's inset from its box (and its padding above and below), and the least height of a row's content.</summary>
    internal const double SettingsHeadingGap = DesignMetrics.Spacing.Sm, SettingsRowInset = DesignMetrics.Spacing.Md, SettingsRowContent = 16;
    /// <summary>The Mac's control heights: a push button, pop-up or segmented picker, their small size, and the mini one.</summary>
    internal const double SettingsControlHeight = 20, SettingsSmallHeight = 16, SettingsMiniHeight = 14;
    /// <summary>The Mac's rounded text field, with its border (screens/10-settings-models-*.webp).</summary>
    internal const double SettingsFieldHeight = 19;
    /// <summary>The Mac's switch: a 26×15 capsule around a 13pt knob.</summary>
    internal const double SettingsSwitchWidth = 26, SettingsSwitchHeight = 15, SettingsSwitchKnob = 13;
    /// <summary>The corner of a push button and of a segmented picker.</summary>
    internal const double SettingsControlRadius = 5;
    /// <summary>The share of a row a segmented picker takes (the Mac draws it 295 wide in a 538 row).</summary>
    internal const double SettingsControlShare = 0.55;

    internal enum SettingsControlSize { Regular, Small, Mini }

    /// <summary>
    /// Words of the form at the Mac's sizes and line pitch (SF's 13 on 16, 12 on 15, 11 on 14, 10 on 13):
    /// the label 13 <c>ink</c>, an explanation 11 <c>ink2</c>. The row heights follow the pitch.
    /// </summary>
    internal TextBlock SettingsText(string text, double size = 13, DesignToken ink = DesignToken.Ink, bool medium = false, bool mono = false, bool selectable = false)
    {
        var words = new TextBlock
        {
            Text = text, FontSize = size, Foreground = brushes.Brush(ink), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
            LineHeight = size + 3, LineStackingStrategy = LineStackingStrategy.BlockLineHeight, IsTextSelectionEnabled = selectable,
        };
        if (medium) words.FontWeight = Microsoft.UI.Text.FontWeights.Medium;
        if (mono) words.FontFamily = new FontFamily(DesignMetrics.Font.Mono);
        return words;
    }

    /// <summary>
    /// Words of the form in the Mac's tertiary ink (<see cref="DesignBrushes.Tertiary"/>, its <c>.foregroundStyle(.tertiary)</c>):
    /// a path, where a thing comes from, a word for what is not there.
    /// </summary>
    internal TextBlock SettingsTertiary(string text, double size = 10, bool mono = false, bool selectable = false)
    {
        var words = SettingsText(text, size, mono: mono, selectable: selectable); words.Foreground = brushes.Tertiary;
        return words;
    }

    /// <summary>A switch's words (M/SettingsViews.swift:233-237): the label over its 11pt <c>ink2</c> explanation, 2 apart.</summary>
    internal StackPanel SettingsTitled(string title, string help)
    {
        var words = new StackPanel { Spacing = DesignMetrics.Spacing.Xxs, VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(SettingsText(title)); words.Children.Add(SettingsText(help, 11, DesignToken.Ink2));
        return words;
    }

    /// <summary>
    /// A row's content the form's way: the words at the left taking what is left, the control at the right
    /// (at the top beside a label with an explanation). A segmented picker takes <see cref="SettingsControlShare"/> of the row.
    /// </summary>
    internal Grid SettingsLabeled(FrameworkElement label, FrameworkElement control, bool share = false, bool top = false)
    {
        var line = new Grid { ColumnSpacing = share ? 0 : DesignMetrics.Spacing.Md };
        line.ColumnDefinitions.Add(new() { Width = new(share ? 1 - SettingsControlShare : 1, GridUnitType.Star) });
        line.ColumnDefinitions.Add(new() { Width = share ? new(SettingsControlShare, GridUnitType.Star) : GridLength.Auto });
        line.Children.Add(label);
        if (!share) control.HorizontalAlignment = HorizontalAlignment.Right;
        // Beside two lines the Mac sets the switch on the label's own line, a point under its top.
        control.VerticalAlignment = top ? VerticalAlignment.Top : VerticalAlignment.Center;
        if (top) control.Margin = new Thickness(0, 1, 0, 0);
        // A 20-high picker stands a point into the row's top padding, as on the Mac.
        else if (share) control.Margin = new Thickness(0, -1, 0, 0);
        Grid.SetColumn(control, 1); line.Children.Add(control);
        return line;
    }

    /// <summary>
    /// Adds a row to a box's rows: <see cref="SettingsRowInset"/> in from the box and above and below its content, and a 1pt
    /// <c>line</c> over every row but the first one showing (<see cref="RuleSettingsRows"/>).
    /// Hide a row by its returned border, so the rule and the padding go with it.
    /// </summary>
    internal Border SettingsRow(StackPanel rows, FrameworkElement content, int? index = null, object? tag = null)
    {
        var row = new Border
        {
            Child = content, Tag = tag, Margin = SettingsRowMargin, Padding = new Thickness(0, SettingsRowInset, 0, SettingsRowInset),
            BorderBrush = brushes.Brush(DesignToken.Line),
        };
        if (index is { } at && at >= 0 && at <= rows.Children.Count) rows.Children.Insert(at, row); else rows.Children.Add(row);
        row.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, (_, _) => RuleSettingsRows(rows));
        RuleSettingsRows(rows);
        return row;
    }

    /// <summary>A row's margin in its box: <see cref="SettingsRowInset"/> from the box's outer edge, of which the box's own 1pt line is the first point.</summary>
    internal static readonly Thickness SettingsRowMargin = new(SettingsRowInset - DesignMetrics.Stroke.Line, 0, SettingsRowInset - DesignMetrics.Stroke.Line, 0);

    /// <summary>
    /// Draws the rule over every showing row but the first, and holds each row to the form's least height.
    /// The first row's padding starts at the box's outer edge, so the box's own 1pt line is its first point
    /// (the Mac's line is half a point, and its first row's content sits 10.5 under the box's top).
    /// </summary>
    internal static void RuleSettingsRows(StackPanel rows)
    {
        var first = true;
        foreach (var row in rows.Children.OfType<Border>())
        {
            if (row.Visibility != Visibility.Visible) continue;
            var line = first ? 0 : DesignMetrics.Stroke.Line;
            var over = first ? SettingsRowInset - DesignMetrics.Stroke.Line : SettingsRowInset; first = false;
            if (row.BorderThickness.Top != line) row.BorderThickness = new Thickness(0, line, 0, 0);
            if (row.Padding.Top != over) row.Padding = new Thickness(0, over, 0, SettingsRowInset);
            var least = line + over + SettingsRowInset + SettingsRowContent;
            if (row.MinHeight != least) row.MinHeight = least;
        }
    }

    /// <summary>
    /// Replaces the rows of one kind in a box: those carrying <paramref name="tag"/> go and the new ones
    /// take their place, before <paramref name="before"/> (at the end when it is null or gone).
    /// </summary>
    internal void ReplaceSettingsRows(StackPanel rows, string tag, IEnumerable<FrameworkElement> contents, UIElement? before = null)
    {
        foreach (var old in rows.Children.OfType<Border>().Where(row => Equals(row.Tag, tag)).ToList()) rows.Children.Remove(old);
        var at = before is null ? -1 : rows.Children.IndexOf(before);
        if (at < 0) at = rows.Children.Count;
        foreach (var content in contents) SettingsRow(rows, content, at++, tag);
        RuleSettingsRows(rows);
    }

    /// <summary>A toggle's own fill and border, cleared in every state: its look is drawn on its content.</summary>
    private void ClearToggleChrome(ToggleButton toggle)
    {
        var clear = brushes.Transparent; var values = new List<(string Key, object Value)>();
        foreach (var state in new[] { "", "PointerOver", "Pressed", "Disabled", "Checked", "CheckedPointerOver", "CheckedPressed", "CheckedDisabled", "Indeterminate", "IndeterminatePointerOver", "IndeterminatePressed", "IndeterminateDisabled" })
        { values.Add(("ToggleButtonBackground" + state, clear)); values.Add(("ToggleButtonBorderBrush" + state, clear)); }
        if (SetResourcesOnce(toggle, values)) { toggle.Background = clear; toggle.BorderBrush = clear; }
    }

    /// <summary>
    /// The form's switch, the Mac's small one: a 26×15 capsule, <c>accent</c> when on, with a 13pt knob.
    /// A ToggleButton, so it keeps the keyboard (Space) and the UIA Toggle pattern; the capsule and the
    /// knob are its content, redrawn as it is switched.
    /// </summary>
    internal ToggleButton SettingsSwitch(string name, bool on, string? automationId = null)
    {
        var b = brushes;
        var knob = new Ellipse { Width = SettingsSwitchKnob, Height = SettingsSwitchKnob, Margin = new Thickness((SettingsSwitchHeight - SettingsSwitchKnob) / 2), VerticalAlignment = VerticalAlignment.Center };
        var track = new Grid { Width = SettingsSwitchWidth, Height = SettingsSwitchHeight, CornerRadius = new CornerRadius(SettingsSwitchHeight / 2), Children = { knob } };
        var toggle = new ToggleButton
        {
            Content = track, IsChecked = on, Width = SettingsSwitchWidth, Height = SettingsSwitchHeight, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0),
            BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(SettingsSwitchHeight / 2), VerticalAlignment = VerticalAlignment.Center,
        };
        ClearToggleChrome(toggle);
        void Paint()
        {
            var isOn = toggle.IsChecked == true;
            track.Background = isOn ? b.Brush(DesignToken.Accent) : b.Brush(DesignToken.Ink2, SettingsSwitchOffOpacity);
            knob.Fill = b.Brush(isOn ? DesignToken.OnAccent : DesignToken.OnStatus);
            knob.HorizontalAlignment = isOn ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            track.Opacity = toggle.IsEnabled ? 1 : SettingsDisabledOpacity;
        }
        toggle.Checked += (_, _) => Paint(); toggle.Unchecked += (_, _) => Paint(); toggle.IsEnabledChanged += (_, _) => Paint(); Paint();
        AutomationProperties.SetName(toggle, name);
        if (automationId is not null) AutomationProperties.SetAutomationId(toggle, automationId);
        return toggle;
    }

    /// <summary>The off switch's capsule: <c>ink2</c> at 0.3, a quiet grey on the box in both themes.</summary>
    internal const double SettingsSwitchOffOpacity = 0.3;
    /// <summary>A disabled control of the form keeps its look at half strength, as AppKit dims it.</summary>
    internal const double SettingsDisabledOpacity = 0.5;

    /// <summary>
    /// The form's segmented picker (the Mac's <c>.pickerStyle(.segmented)</c>, M/SettingsViews.swift:222-230):
    /// equal segments on a control-coloured track with a 1pt <c>line</c>, the chosen one filled with
    /// <c>accent</c> under <c>onAccent</c> words, a hairline between two unchosen neighbours. One tab stop;
    /// the arrow keys move between the segments and Space or Enter chooses. Each segment is a ToggleButton
    /// (checked = chosen), so automation can read and choose it; <paramref name="automationId"/> names the
    /// picker and, with the value appended, each segment.
    /// </summary>
    internal Grid SettingsSegmented(string name, string automationId, IReadOnlyList<(string Value, string Label)> options, string selected, Func<string, Task> pick, bool small = false, bool enabled = true)
    {
        var b = brushes; var radius = SettingsControlRadius; var font = small ? 11.0 : 13.0;
        var host = new Grid
        {
            Height = small ? SettingsSmallHeight : SettingsControlHeight, TabFocusNavigation = KeyboardNavigationMode.Once,
            XYFocusKeyboardNavigation = XYFocusKeyboardNavigationMode.Enabled, Opacity = enabled ? 1 : SettingsDisabledOpacity,
        };
        AutomationProperties.SetName(host, name); AutomationProperties.SetAutomationId(host, automationId);
        var track = new Border { Background = b.SegmentOn, BorderBrush = b.Brush(DesignToken.Line), BorderThickness = new Thickness(DesignMetrics.Stroke.Line), CornerRadius = new CornerRadius(radius) };
        Grid.SetColumnSpan(track, options.Count); host.Children.Add(track);
        var current = selected; var painting = false;
        var parts = new List<(ToggleButton Segment, Border Chip, TextBlock Words, Border? Rule)>();
        void Paint()
        {
            painting = true;
            for (var i = 0; i < parts.Count; i++)
            {
                var chosen = options[i].Value == current; var (segment, chip, words, rule) = parts[i];
                segment.IsChecked = chosen; chip.Background = chosen ? b.Brush(DesignToken.Accent) : b.Transparent;
                words.Foreground = b.Brush(chosen ? DesignToken.OnAccent : DesignToken.Ink);
                if (rule is not null) rule.Visibility = chosen || options[i - 1].Value == current ? Visibility.Collapsed : Visibility.Visible;
            }
            painting = false;
        }
        async Task Choose(string value)
        {
            if (value == current) { Paint(); return; }
            var previous = current; current = value; Paint();
            try { await pick(value); }
            catch { current = previous; Paint(); throw; }
        }
        for (var i = 0; i < options.Count; i++)
        {
            host.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            var (value, label) = options[i]; var last = options.Count - 1;
            var words = new TextBlock { Text = label, FontSize = font, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
            var corners = new CornerRadius(i == 0 ? radius : 0, i == last ? radius : 0, i == last ? radius : 0, i == 0 ? radius : 0);
            var chip = new Border { Child = words, CornerRadius = corners };
            var segment = new ToggleButton
            {
                Content = chip, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0), BorderThickness = new Thickness(0), CornerRadius = corners, IsEnabled = enabled,
                HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch,
            };
            ClearToggleChrome(segment);
            AutomationProperties.SetName(segment, label); AutomationProperties.SetAutomationId(segment, automationId + "-" + value);
            AutomationProperties.SetPositionInSet(segment, i + 1); AutomationProperties.SetSizeOfSet(segment, options.Count);
            segment.Checked += async (_, _) => { if (!painting) await Choose(value); };
            // The chosen segment cannot be switched off; choosing another one moves the choice.
            segment.Unchecked += (_, _) => { if (!painting && value == current) Paint(); };
            Grid.SetColumn(segment, i); host.Children.Add(segment);
            Border? rule = null;
            if (i > 0)
            {
                rule = new Border { Width = DesignMetrics.Stroke.Line, Background = b.Brush(DesignToken.Line), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, DesignMetrics.Spacing.Xs, 0, DesignMetrics.Spacing.Xs), IsHitTestVisible = false };
                Grid.SetColumn(rule, i); host.Children.Add(rule);
            }
            parts.Add((segment, chip, words, rule));
        }
        Paint();
        // The picker shows a choice made elsewhere through this, without asking for it to be made again.
        host.Tag = new Action<string>(value => { current = value; Paint(); });
        return host;
    }

    /// <summary>Shows <paramref name="value"/> as a segmented picker's choice (one made outside it) without running its action.</summary>
    internal static void ShowSegment(Grid picker, string value) => (picker.Tag as Action<string>)?.Invoke(value);

    /// <summary>
    /// A pop-up of the form the Mac's inline way (M/PhaseModelSettingsView.swift:135-150): as wide as its
    /// chosen title, 20 high, with no box until the pointer is over it — or, <paramref name="bordered"/>,
    /// the Mac's pop-up button outside a form: the same height on the control fill with a 1pt <c>line</c>.
    /// Still a ComboBox, with its own keyboard and automation; its sizes and state brushes are written
    /// once, before it is shown.
    /// </summary>
    internal ComboBox SettingsPopup(ComboBox picker, bool bordered = false)
    {
        var b = brushes; var fill = bordered ? b.SegmentOn : b.Transparent; var line = bordered ? b.Brush(DesignToken.Line) : b.Transparent;
        picker.Height = SettingsControlHeight; picker.MinHeight = 0; picker.MinWidth = 0; picker.FontSize = 13; picker.Padding = new Thickness(DesignMetrics.Spacing.Sm, 0, 0, 0);
        picker.BorderThickness = new Thickness(bordered ? DesignMetrics.Stroke.Line : 0); picker.CornerRadius = new CornerRadius(SettingsControlRadius);
        picker.HorizontalAlignment = HorizontalAlignment.Center; picker.VerticalAlignment = VerticalAlignment.Center;
        var values = new List<(string Key, object Value)> { ("ComboBoxMinHeight", SettingsControlHeight), ("ComboBoxThemeMinWidth", 0d) };
        foreach (var state in new[] { "", "Disabled", "Focused" }) values.Add(("ComboBoxBackground" + state, fill));
        // The inline pop-up stays bare under the pointer, as the form's does; the bordered one takes the subtle wash.
        foreach (var state in new[] { "PointerOver", "Pressed" }) values.Add(("ComboBoxBackground" + state, bordered ? b.Subtle : b.Transparent));
        foreach (var state in new[] { "", "PointerOver", "Pressed", "Disabled", "Focused" }) values.Add(("ComboBoxBorderBrush" + state, line));
        foreach (var state in new[] { "", "PointerOver", "Pressed", "Focused" }) values.Add(("ComboBoxForeground" + state, b.Brush(DesignToken.Ink)));
        // The inline pop-up's own chevron is cleared: its chip is drawn over that place (SettingsPopupFrame).
        if (!bordered) foreach (var state in new[] { "", "Disabled", "Focused", "FocusedPressed" }) values.Add(("ComboBoxDropDownGlyphForeground" + state, b.Transparent));
        if (SetResourcesOnce(picker, values)) { picker.Background = fill; picker.BorderBrush = line; }
        return picker;
    }

    /// <summary>
    /// An inline pop-up (<see cref="SettingsPopup"/>) with the Mac's chip after its words
    /// (screens/10-settings-models-*.webp): a frame as large as the ComboBox with the 16pt chip of the up
    /// and down chevrons drawn 4 after the chosen title, where the ComboBox's own chevron stood. The chip
    /// takes no pointer, so the whole frame still opens the list. Place the frame; the ComboBox keeps its
    /// id, name and handlers.
    /// </summary>
    internal Grid SettingsPopupFrame(ComboBox picker)
    {
        static PathFigure Chevron(double tipY, double endY)
        {
            var figure = new PathFigure { StartPoint = new(5, endY), IsClosed = false };
            figure.Segments.Add(new LineSegment { Point = new(8, tipY) }); figure.Segments.Add(new LineSegment { Point = new(11, endY) });
            return figure;
        }
        var chevrons = new PathGeometry(); chevrons.Figures.Add(Chevron(3.6, 6.4)); chevrons.Figures.Add(Chevron(12.4, 9.6));
        var chip = SettingsChip(new ShapePath
        {
            Data = chevrons, Width = 16, Height = 16, Stroke = brushes.Brush(DesignToken.Ink), StrokeThickness = 1.4,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
        });
        chip.HorizontalAlignment = HorizontalAlignment.Right; chip.Margin = new Thickness(0, 0, SettingsPopupChipInset, 0); chip.IsHitTestVisible = false;
        var frame = new Grid { HorizontalAlignment = picker.HorizontalAlignment, VerticalAlignment = picker.VerticalAlignment, Margin = picker.Margin };
        picker.HorizontalAlignment = HorizontalAlignment.Stretch; picker.Margin = new Thickness(0);
        frame.Children.Add(chip); frame.Children.Add(picker);
        return frame;
    }

    /// <summary>
    /// How far the chip stands from a pop-up's trailing edge: a stock ComboBox keeps 39 after its words for
    /// its own chevron, and the Mac's 16pt chip follows the words by 4.
    /// </summary>
    internal const double SettingsPopupChipInset = 19;

    /// <summary>
    /// A text field of the form, the Mac's <c>.roundedBorder</c> field: 19 high on <c>card</c> with a 1pt
    /// <c>line</c> at radius 5, its words 11pt mono (an address, a model name), AppKit's placeholder in the tertiary ink.
    /// Written once, before it is shown.
    /// </summary>
    internal TextBox SettingsField(TextBox field, double size = 11)
    {
        var b = brushes; var card = b.Brush(DesignToken.Card); var line = b.Brush(DesignToken.Line);
        field.Height = SettingsFieldHeight; field.MinHeight = 0; field.MinWidth = 0; field.Padding = new Thickness(DesignMetrics.Spacing.Sm, 1, DesignMetrics.Spacing.Sm, 1); field.FontSize = size;
        field.FontFamily = new FontFamily(DesignMetrics.Font.Mono); field.CornerRadius = new CornerRadius(SettingsControlRadius); field.VerticalAlignment = VerticalAlignment.Center;
        // The template takes the placeholder's ink from the property in every state (BuildSidebarSearch).
        field.PlaceholderForeground = b.Tertiary;
        var values = new List<(string Key, object Value)>();
        foreach (var state in new[] { "", "PointerOver", "Focused", "Disabled" }) values.Add(("TextControlBackground" + state, card));
        foreach (var state in new[] { "", "PointerOver", "Disabled" }) values.Add(("TextControlBorderBrush" + state, line));
        if (SetResourcesOnce(field, values)) { field.Background = card; field.BorderBrush = line; }
        return field;
    }

    /// <summary>
    /// A push button of the form, the Mac's bordered button: 20 high with 13pt words (16 with 11 small, 14
    /// with 9 mini) on the control fill with a 1pt <c>line</c>, <c>errText</c> words for a destructive one,
    /// or <c>accent</c> under <c>onAccent</c> when prominent. Painted once, as the button is built.
    /// </summary>
    internal Button SettingsPush(Button button, SettingsControlSize size = SettingsControlSize.Regular, bool prominent = false, bool destructive = false)
    {
        var b = brushes;
        // The words stand 7 (5.5 small, 5 mini) from the button's edge, of which its 1pt line is the first point.
        var (height, font, inset, radius) = size switch
        {
            SettingsControlSize.Small => (SettingsSmallHeight, 11.0, 4.5, 4.0),
            SettingsControlSize.Mini => (SettingsMiniHeight, 9.0, 4.0, 3.5),
            _ => (SettingsControlHeight, 13.0, 6.0, SettingsControlRadius),
        };
        button.Height = height; button.MinHeight = 0; button.MinWidth = 0; button.Padding = new Thickness(inset, 0, inset, 0); button.FontSize = font;
        button.CornerRadius = new CornerRadius(radius); button.BorderThickness = new Thickness(prominent ? 0 : DesignMetrics.Stroke.Line); button.VerticalAlignment = VerticalAlignment.Center;
        if (prominent)
        {
            PaintPlainButton(button, b.Brush(DesignToken.Accent), b.Brush(DesignToken.Accent, 0.9), ink: b.Brush(DesignToken.OnAccent), disabledInk: b.Brush(DesignToken.OnAccent));
            button.IsEnabledChanged += (_, _) => button.Opacity = button.IsEnabled ? 1 : SettingsDisabledOpacity;
            button.Opacity = button.IsEnabled ? 1 : SettingsDisabledOpacity;
        }
        else PaintPlainButton(button, b.SegmentOn, b.Subtle, b.Brush(DesignToken.Line), b.Brush(destructive ? DesignToken.ErrText : DesignToken.Ink), b.Brush(DesignToken.Ink3));
        return button;
    }

    /// <summary>A push button's content with a leading symbol (the Mac's <c>Label</c> in a button): the glyph and the words, 4 apart.</summary>
    internal static StackPanel SettingsGlyphLabel(string glyph, string label, double font)
    {
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DesignMetrics.Spacing.Xs, VerticalAlignment = VerticalAlignment.Center };
        line.Children.Add(new FontIcon { Glyph = glyph, FontSize = font, VerticalAlignment = VerticalAlignment.Center });
        line.Children.Add(new TextBlock { Text = label, FontSize = font, VerticalAlignment = VerticalAlignment.Center });
        return line;
    }

    /// <summary>A symbol-only button the Mac's <c>.plain</c> way: no fill, the subtle wash under the pointer, <c>ink</c> (<c>ink3</c> disabled).</summary>
    internal Button SettingsIconButton(string glyph, string name, Func<Task> action, double size = 13)
    {
        var button = Button(name, action);
        button.Content = new FontIcon { Glyph = glyph, FontSize = size };
        button.Width = SettingsControlHeight; button.Height = SettingsControlHeight; button.MinWidth = 0; button.MinHeight = 0; button.Padding = new Thickness(0);
        button.BorderThickness = new Thickness(0); button.CornerRadius = new CornerRadius(SettingsControlRadius); button.VerticalAlignment = VerticalAlignment.Center;
        PaintPlainButton(button, brushes.Transparent, brushes.Subtle, ink: brushes.Brush(DesignToken.Ink), disabledInk: brushes.Brush(DesignToken.Ink3));
        ToolTipService.SetToolTip(button, name);
        return button;
    }

    /// <summary>
    /// The chip the Mac sets after a pull-down's or a pop-up's words (screens/10-settings-cli-dark.webp,
    /// 10-settings-models-*.webp): 16pt, radius 4, a faint tint of the ink, its chevron 8pt.
    /// </summary>
    internal Border SettingsChip(string glyph) => SettingsChip(new FontIcon { Glyph = glyph, FontSize = 8, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });

    /// <summary>The chip around a mark of its own (the pop-up's up and down chevrons).</summary>
    internal Border SettingsChip(FrameworkElement mark) => new()
    {
        Width = 16, Height = 16, CornerRadius = new CornerRadius(4), Background = brushes.Brush(DesignToken.Ink, SettingsChipTint), VerticalAlignment = VerticalAlignment.Center, Child = mark,
    };

    /// <summary>The chip's tint: AppKit draws it about 0.055 of black by day and 0.106 of white by night.</summary>
    internal const double SettingsChipTint = 0.08;

    /// <summary>A small capsule of words on a tint (a state or a badge): padding h5 v1, as the Mac's.</summary>
    internal Border SettingsCapsule(string text, Brush ink, Brush fill, double size = 10, double radius = 8)
    {
        var words = new TextBlock { Text = text, FontSize = size, Foreground = ink, LineHeight = size + 3, LineStackingStrategy = LineStackingStrategy.BlockLineHeight };
        if (size < 10) words.FontWeight = Microsoft.UI.Text.FontWeights.Medium;
        return new Border { Child = words, Padding = new Thickness(DesignMetrics.Spacing.Xs, 1, DesignMetrics.Spacing.Xs, 1), CornerRadius = new CornerRadius(radius), Background = fill, VerticalAlignment = VerticalAlignment.Center };
    }

    /// <summary>A symbol of the form in an ink, hidden from automation: the words beside it say what it marks.</summary>
    internal FontIcon SettingsSymbol(string glyph, double size, DesignToken ink)
    {
        var icon = new FontIcon { Glyph = glyph, FontSize = size, Foreground = brushes.Brush(ink), VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAccessibilityView(icon, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        return icon;
    }

    /// <summary>
    /// Every element of a settings subtree by the logical tree (a panel's children, a border's child, a
    /// control's content), so a section that is not on screen can be read as well as a shown one.
    /// </summary>
    internal static IEnumerable<FrameworkElement> SettingsElements(FrameworkElement? element)
    {
        if (element is null) yield break;
        yield return element;
        var children = element switch
        {
            Panel panel => panel.Children.OfType<FrameworkElement>().ToArray(),
            Border border => new[] { border.Child as FrameworkElement },
            ContentControl control => new[] { control.Content as FrameworkElement },
            _ => Array.Empty<FrameworkElement?>(),
        };
        foreach (var child in children) foreach (var descendant in SettingsElements(child)) yield return descendant;
    }

    /// <summary>The element of a settings subtree that carries an automation id.</summary>
    internal static T SettingsElement<T>(FrameworkElement top, string automationId) where T : FrameworkElement =>
        SettingsElements(top).OfType<T>().First(element => AutomationProperties.GetAutomationId(element) == automationId);
}
