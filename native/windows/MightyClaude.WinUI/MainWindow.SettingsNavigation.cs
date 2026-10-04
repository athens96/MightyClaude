using MightyClaude.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace MightyClaude.WinUI;

public sealed partial class MainWindow
{
    private string settingsCategory = "general";
    private Window? settingsWindow;
    private XamlRoot SettingsXamlRoot => (settingsWindow?.Content as FrameworkElement)?.XamlRoot ?? root.XamlRoot;
    private async Task ShowCategorizedSettingsAsync()
    {
        if (dialogOpen) return;
        dialogOpen = true;
        try
        {
            settingsCategory = service.Snapshot.SettingsPane;
            var navigation = new ListView { SelectionMode = ListViewSelectionMode.Single, Padding = new(10, 12, 10, 12) };
            AutomationProperties.SetAutomationId(navigation, "settings-navigation");
            PaintSettingsNavigation(navigation);
            var content = new StackPanel { Spacing = 4, Padding = new(24, 20, 24, 8) };
            var scroll = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            void Display(SettingsCategory category)
            {
                settingsCategory = category.Id;
                content.Children.Clear();
                foreach (var id in category.Sections)
                {
                    var section = SettingsSections.Windows.FirstOrDefault(s => s.Id == id);
                    if (section is null) continue;
                    content.Children.Add(BuildSectionContainer(section.WindowsTitle!, BuilderFor(id)()));
                }
                scroll.ChangeView(null, 0, null, true);
                // Refresh only the still-visible account section; an async CLI
                // result must not replace the category selected in the meantime.
                if (category.Id == "cli" && !options.SmokeTest) _ = RefreshVisibleSettingsAccountsAsync(content);
            }
            foreach (var category in SettingsNavigation.Available)
            {
                var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9, VerticalAlignment = VerticalAlignment.Center };
                label.Children.Add(new FontIcon { Glyph = SettingsCategoryGlyph(category.Id), FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
                label.Children.Add(new TextBlock { Text = category.Title, FontSize = DesignMetrics.Type.Title, VerticalAlignment = VerticalAlignment.Center });
                var item = new ListViewItem { Content = label, Tag = category, Padding = new(10, 6, 10, 6), MinHeight = 32, CornerRadius = new(DesignMetrics.Radius.Segment), Margin = new(0, 1, 0, 1) };
                AutomationProperties.SetName(item, category.Title);
                AutomationProperties.SetAutomationId(item, "settings-nav-" + category.Id); navigation.Items.Add(item);
            }
            navigation.SelectionChanged += async (_, _) =>
            {
                if (navigation.SelectedItem is not ListViewItem { Tag: SettingsCategory category }) return;
                Display(category);
                await Act(() => service.UpdateAsync(s => s with { SettingsPane = category.Id }));
            };
            navigation.SelectedItem = navigation.Items.OfType<ListViewItem>().FirstOrDefault(item => ((SettingsCategory)item.Tag).Id == settingsCategory) ?? navigation.Items[0];
            // A separate settings window leaves the main XamlRoot available
            // for account/reset confirmations (WinUI forbids nested dialogs).
            // The Mac's 800x700 sheet (M/SettingsViews.swift:152-269): the heading, the 200-wide list on
            // the sidebar surface beside the grouped cards on the page, and the close row under them.
            var frame = new Grid { RequestedTheme = root.RequestedTheme, Background = WindowBackground() };
            frame.RowDefinitions.Add(new() { Height = GridLength.Auto });
            frame.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
            frame.RowDefinitions.Add(new() { Height = GridLength.Auto });
            var heading = SettingsHeading();
            frame.Children.Add(heading);
            var body = new Grid();
            body.ColumnDefinitions.Add(new() { Width = new(SettingsNavigationWidth) });
            body.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            var navigationHost = new Border { Child = navigation, Background = brushes.Brush(DesignToken.Sidebar), BorderBrush = brushes.Brush(DesignToken.Line), BorderThickness = new(0, 0, DesignMetrics.Stroke.Line, 0) };
            AutomationProperties.SetAutomationId(navigationHost, "settings-navigation-host");
            body.Children.Add(navigationHost);
            Grid.SetColumn(scroll, 1); body.Children.Add(scroll);
            Grid.SetRow(body, 1); frame.Children.Add(body);
            var footer = new Grid { Padding = new(18), Background = brushes.Brush(DesignToken.Card), BorderBrush = brushes.Brush(DesignToken.Line), BorderThickness = new(0, DesignMetrics.Stroke.Line, 0, 0) };
            AutomationProperties.SetAutomationId(footer, "settings-footer");
            Grid.SetRow(footer, 2); frame.Children.Add(footer);
            var window = new Window { Title = Locale.Get("settings.title"), Content = frame };
            settingsWindow = window;
            var close = Button(Locale.Get("settings.closeButton"), () => { window.Close(); return Task.CompletedTask; });
            close.HorizontalAlignment = HorizontalAlignment.Right; AutomationProperties.SetAutomationId(close, "settings-close"); footer.Children.Add(close);
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            // Dropped at once, not after the awaiting continuation runs: a Render in between must not
            // touch the closed window's AppWindow.
            window.Closed += (_, _) => { if (ReferenceEquals(settingsWindow, window)) settingsWindow = null; closed.TrySetResult(); };
            window.AppWindow.Resize(SettingsWindowSize(window.AppWindow)); brushes.ApplyTitleBar(window.AppWindow);
            window.Activate();
            await closed.Task;
        }
        finally { settingsWindow = null; dialogOpen = false; }
    }
    /// <summary>The settings sheet's size in points and its side list's width (M/SettingsViews.swift:186, 199).</summary>
    internal const double SettingsWindowWidth = 800, SettingsWindowHeight = 700, SettingsNavigationWidth = 200;

    /// <summary>The space kept free around the settings window on a work area too small for it, in points.</summary>
    internal const double SettingsWindowMargin = 24;

    /// <summary>
    /// The settings window's size in physical pixels: the Mac sheet's 800×700 points at the display
    /// scale, but never more than the display's work area less <see cref="SettingsWindowMargin"/> on each side.
    /// </summary>
    internal Windows.Graphics.SizeInt32 SettingsWindowSize(Microsoft.UI.Windowing.AppWindow window)
    {
        var scale = root.XamlRoot?.RasterizationScale ?? 1;
        var area = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(window.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Nearest)?.WorkArea;
        var margin = (int)Math.Round(2 * SettingsWindowMargin * scale);
        int Fit(double points, int? available) => Math.Max(1, Math.Min((int)Math.Round(points * scale), available is { } free && free > margin ? free - margin : int.MaxValue));
        return new(Fit(SettingsWindowWidth, area?.Width), Fit(SettingsWindowHeight, area?.Height));
    }

    /// <summary>The side list's symbol per category, the Mac's SF Symbols in Segoe Fluent Icons (M/SettingsViews.swift:166-177).</summary>
    private static string SettingsCategoryGlyph(string id) => id switch
    {
        "general" => "\uE713", "models" => "\uE950", "styles" => "\uE790", "tools" => "\uE7B8",
        "cli" => "\uE756", "mobile" => "\uE8EA", "companion" => "\uE76E", "about" => "\uE946", _ => "\uE713",
    };

    /// <summary>
    /// The sheet heading (M/SettingsViews.swift:325-338): a 21pt light gear in <c>accent</c> in a 31-wide
    /// column, the 17pt semibold title over an 11pt <c>ink2</c> subtitle, padding 22, on <c>card</c> with a
    /// <c>line</c> under it.
    /// </summary>
    private Grid SettingsHeading()
    {
        var heading = new Grid { Padding = new(22), ColumnSpacing = 12, Background = brushes.Brush(DesignToken.Card), BorderBrush = brushes.Brush(DesignToken.Line), BorderThickness = new(0, 0, 0, DesignMetrics.Stroke.Line) };
        heading.ColumnDefinitions.Add(new() { Width = new(31) });
        heading.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        var icon = new FontIcon { Glyph = "\uE713", FontSize = 21, FontWeight = Microsoft.UI.Text.FontWeights.Light, Foreground = brushes.Brush(DesignToken.Accent), VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAccessibilityView(icon, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        heading.Children.Add(icon);
        var words = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(new TextBlock { Text = Locale.Get("settings.title"), FontSize = DesignMetrics.Type.Header, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = brushes.Brush(DesignToken.Ink) });
        words.Children.Add(new TextBlock { Text = "Windows · WinUI", FontSize = DesignMetrics.Type.Pill, Foreground = brushes.Brush(DesignToken.Ink2) });
        Grid.SetColumn(words, 1); heading.Children.Add(words);
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
        await RefreshCliAccounts();
        if (!dialogOpen || settingsCategory != "cli") return;
        var wrapper = content.Children.OfType<StackPanel>().FirstOrDefault(section => section.Children.OfType<TextBlock>().FirstOrDefault()?.Text == CliAccountStrings.SectionTitle);
        // The section's card stays; only the rows inside it are replaced.
        if (wrapper is not null && wrapper.Children.Count > 1 && wrapper.Children[1] is Border card) card.Child = Toned(BuildCliAccountsSectionFromState());
    }
}
