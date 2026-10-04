using MightyClaude.Core;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace MightyClaude.WinUI;

public sealed partial class MainWindow
{
    private readonly Dictionary<string, Button> sidebarSessionButtons = [];
    private readonly Dictionary<string, Button> workspaceDisclosureButtons = [];
    /// <summary>Each sidebar pane row's title, whose weight follows the pane's status.</summary>
    private readonly Dictionary<string, TextBlock> sidebarTitles = [];
    private Button? sidebarThemeButton;
    /// <summary>The search field's rounded subtle wash around the magnifier and the text box (M/WorkspaceView.swift:71-77).</summary>
    private readonly Border sidebarSearchBox = new() { CornerRadius = new CornerRadius(DesignMetrics.Radius.Search), Padding = new Thickness(9), Margin = new Thickness(14, 10, 14, 0) };
    private readonly FontIcon sidebarSearchIcon = new() { Glyph = "\uE721", FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
    /// <summary>The count beside the section header's words, 10pt mono.</summary>
    private readonly TextBlock sessionsCount = new() { FontSize = DesignMetrics.Type.Small, FontFamily = new FontFamily(DesignMetrics.Font.Mono), VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel sessionsHeaderRow = new() { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(20, 20, 20, 11) };
    /// <summary>What the list says when it lists no workspace: none match the search, or none yet (M/WorkspaceView.swift:89-93).</summary>
    private readonly TextBlock sidebarEmpty = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Padding = new Thickness(16), Margin = new Thickness(9, 0, 9, 0), Visibility = Visibility.Collapsed };
    /// <summary>The open-folder button's words, set again when the language changes.</summary>
    private readonly TextBlock addFolderLabel = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private Border? sidebarFooterBeta;
    private Grid? sidebarFooter;

    /// <summary>
    /// The search field (M/WorkspaceView.swift:71-77): the magnifier in <c>sidebarInk2</c>, 7 apart
    /// from a borderless 12pt text box, padding 9 on the <c>subtle</c> wash at radius 7, h14 t10
    /// outside. The text box draws no fill or border in any state (lightweight resources), so the
    /// wash is the field; Ctrl+K still focuses it.
    /// </summary>
    private FrameworkElement BuildSidebarSearch()
    {
        sidebarSearchBox.Background = brushes.Subtle; sidebarSearchIcon.Foreground = brushes.Brush(DesignToken.SidebarInk2);
        AutomationProperties.SetAccessibilityView(sidebarSearchIcon, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        search.Background = brushes.Transparent; search.Foreground = brushes.Brush(DesignToken.Ink);
        foreach (var key in new[] { "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused", "TextControlBackgroundDisabled",
                                    "TextControlBorderBrush", "TextControlBorderBrushPointerOver", "TextControlBorderBrushFocused", "TextControlBorderBrushDisabled" })
            search.Resources[key] = brushes.Transparent;
        foreach (var key in new[] { "TextControlForeground", "TextControlForegroundPointerOver", "TextControlForegroundFocused" }) search.Resources[key] = brushes.Brush(DesignToken.Ink);
        foreach (var key in new[] { "TextControlPlaceholderForeground", "TextControlPlaceholderForegroundPointerOver", "TextControlPlaceholderForegroundFocused" }) search.Resources[key] = brushes.Brush(DesignToken.SidebarInk2);
        search.Resources["TextControlBorderThemeThicknessFocused"] = new Thickness(0);
        var row = new Grid { ColumnSpacing = 7 };
        row.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(sidebarSearchIcon); Grid.SetColumn(search, 1); row.Children.Add(search);
        sidebarSearchBox.Child = row;
        return sidebarSearchBox;
    }

    /// <summary>The section header "워크스페이스 6": 10pt semibold words and a 10pt mono count, both <c>sidebarInk2</c>, padding h20 t20 b11.</summary>
    private FrameworkElement BuildSidebarSectionHeader()
    {
        sessionsHeader.Foreground = sessionsCount.Foreground = sidebarEmpty.Foreground = brushes.Brush(DesignToken.SidebarInk2);
        sessionsHeaderRow.Children.Add(sessionsHeader); sessionsHeaderRow.Children.Add(sessionsCount);
        return sessionsHeaderRow;
    }

    /// <summary>
    /// "폴더 열기" while no workspace is listed (M/WorkspaceView.swift:99-105): the folder-plus
    /// symbol and 12pt words, padding 10 on the <c>subtle</c> wash at radius 7, h14 b12 outside,
    /// under the scrolling list.
    /// </summary>
    private void StyleSidebarOpenFolder()
    {
        var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        label.Children.Add(new FontIcon { Glyph = "\uE8F4", FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        label.Children.Add(addFolderLabel); addFolderButton.Content = label;
        addFolderButton.Foreground = brushes.Brush(DesignToken.Ink);
        addFolderButton.HorizontalAlignment = HorizontalAlignment.Stretch; addFolderButton.HorizontalContentAlignment = HorizontalAlignment.Left;
        addFolderButton.FontSize = 12; addFolderButton.Padding = new Thickness(10); addFolderButton.Margin = new Thickness(14, 0, 14, 12);
        PlainSidebarButton(addFolderButton, brushes.Subtle, brushes.Subtle, radius: DesignMetrics.Radius.Search);
    }

    /// <summary>
    /// The Windows-only layout picker and the pet controls above the footer (decision Q3): the
    /// picker as a sidebar field (12pt on the <c>subtle</c> wash, radius 7, no border, the glyph in
    /// <c>sidebarInk2</c>), the pet buttons plain like the footer's, h14 v8 outside.
    /// </summary>
    private FrameworkElement BuildSidebarTools()
    {
        layout.Background = brushes.Subtle; layout.BorderThickness = new Thickness(0); layout.CornerRadius = new CornerRadius(DesignMetrics.Radius.Search);
        layout.FontSize = 12; layout.Foreground = brushes.Brush(DesignToken.Ink);
        foreach (var key in new[] { "ComboBoxBackgroundPointerOver", "ComboBoxBackgroundPressed", "ComboBoxBackgroundFocused" }) layout.Resources[key] = brushes.Subtle;
        foreach (var key in new[] { "ComboBoxBorderBrush", "ComboBoxBorderBrushPointerOver", "ComboBoxBorderBrushPressed" }) layout.Resources[key] = brushes.Transparent;
        // Keyboard focus keeps a visible accent ring around the wash.
        layout.Resources["ComboBoxBackgroundBorderBrushFocused"] = brushes.Brush(DesignToken.Accent);
        foreach (var key in new[] { "ComboBoxForeground", "ComboBoxForegroundPointerOver", "ComboBoxForegroundPressed", "ComboBoxForegroundFocused", "ComboBoxForegroundFocusedPressed" }) layout.Resources[key] = brushes.Brush(DesignToken.Ink);
        foreach (var key in new[] { "ComboBoxDropDownGlyphForeground", "ComboBoxDropDownGlyphForegroundFocused", "ComboBoxDropDownGlyphForegroundFocusedPressed" }) layout.Resources[key] = brushes.Brush(DesignToken.SidebarInk2);
        AutomationProperties.SetAutomationId(layout, "sidebar-layout");
        var tools = new Grid { ColumnSpacing = 8, Margin = new Thickness(14, 8, 14, 8) };
        tools.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); tools.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        tools.Children.Add(layout);
        var companion = BuildCompanionControls(); companion.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(companion, 1); tools.Children.Add(companion);
        foreach (var button in new[] { companionToggleControl, companionStatusControl })
            if (button is not null) { button.FontSize = DesignMetrics.Type.Pill; button.Foreground = brushes.Brush(DesignToken.SidebarInk2); PlainSidebarButton(button, brushes.Transparent, brushes.Subtle, radius: DesignMetrics.Radius.Search); }
        return tools;
    }

    /// <summary>
    /// The footer under a full-width <c>line</c> (M/WorkspaceView.swift:107-118): the app icon 20,
    /// "Mighty Claude" 12pt semibold and the app's 베타 capsule, then the theme and settings
    /// buttons, plain; padding 16.
    /// </summary>
    private FrameworkElement BuildSidebarFooter()
    {
        // Mac groups the brand and the two global actions below the project tree.
        // Keep that grouping while retaining native focus, tooltip and UIA behavior.
        var footer = sidebarFooter = new Grid { ColumnSpacing = 4, Padding = new Thickness(16), BorderThickness = new Thickness(0, DesignMetrics.Stroke.Line, 0, 0), BorderBrush = brushes.Brush(DesignToken.Line) };
        AutomationProperties.SetAutomationId(footer, "sidebar-footer");
        footer.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var brand = new Grid { ColumnSpacing = 9, VerticalAlignment = VerticalAlignment.Center };
        brand.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); brand.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); brand.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var image = new Image { Source = new BitmapImage(new Uri("ms-appx:///Assets/mightyclaude.png")), Width = 20, Height = 20 };
        AutomationProperties.SetAccessibilityView(image, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw); brand.Children.Add(image);
        var title = new TextBlock { Text = "Mighty Claude", FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = brushes.Brush(DesignToken.Ink), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(title, 1); brand.Children.Add(title);
        // The whole Windows app is a beta: the same capsule as a beta agent's (M/BetaBadge.swift).
        brandBeta.Foreground = brushes.Brush(DesignToken.StopText);
        var beta = sidebarFooterBeta = new Border { Child = brandBeta, CornerRadius = new CornerRadius(7), Background = brushes.Brush(DesignToken.StopSoft), Padding = new Thickness(5, 1, 5, 1), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(beta, 2); brand.Children.Add(beta); footer.Children.Add(brand);
        var theme = BuildSidebarThemeButton(); Grid.SetColumn(theme, 1); footer.Children.Add(theme);
        settingsButton.Content = new SymbolIcon(Symbol.Setting) { Width = 16, Height = 16 };
        AutomationProperties.SetAutomationId(settingsButton, "sidebar-settings");
        foreach (var button in new[] { theme, settingsButton })
        {
            button.Width = 28; button.Height = 28; button.MinWidth = 0; button.Padding = new Thickness(4);
            button.Foreground = brushes.Brush(DesignToken.Ink); PlainSidebarButton(button, brushes.Transparent, brushes.Subtle, radius: DesignMetrics.Radius.Search);
        }
        Grid.SetColumn(settingsButton, 2); footer.Children.Add(settingsButton);
        return footer;
    }

    private Button BuildSidebarThemeButton()
    {
        sidebarThemeButton = SafeButton("", async () =>
        {
            await service.UpdateAsync(state => state with { Theme = state.Theme == "light" ? "dark" : "light" });
            Render();
        });
        AutomationProperties.SetAutomationId(sidebarThemeButton, "sidebar-toggle-theme");
        RefreshSidebarThemeButton();
        return sidebarThemeButton;
    }

    private void RefreshSidebarThemeButton()
    {
        if (sidebarThemeButton is null) return;
        var title = Locale.Get("sidebar.toggleTheme");
        // The Mac's sun.max in the dark theme, moon in the light one (Segoe Fluent Brightness / QuietHours).
        sidebarThemeButton.Content = new FontIcon { Glyph = service.Snapshot.Theme == "light" ? SidebarMoonGlyph : SidebarSunGlyph, FontSize = 14 };
        AutomationProperties.SetName(sidebarThemeButton, title); ToolTipService.SetToolTip(sidebarThemeButton, title);
    }

    private async Task ToggleWorkspaceDisclosure(string id, bool keyboardFocus)
    {
        await Act(async () =>
        {
            await service.UpdateAsync(state => WorkspaceDisclosure.Toggle(state, id));
            if (closing) return;
            RenderSidebar();
            if (keyboardFocus && workspaceDisclosureButtons.TryGetValue(id, out var disclosure))
            {
                if (disclosure.IsLoaded) disclosure.Focus(FocusState.Keyboard);
                else
                {
                    void RestoreFocus(object sender, RoutedEventArgs args)
                    {
                        disclosure.Loaded -= RestoreFocus;
                        if (!closing && ReferenceEquals(workspaceDisclosureButtons.GetValueOrDefault(id), disclosure)) disclosure.Focus(FocusState.Keyboard);
                    }
                    disclosure.Loaded += RestoreFocus;
                }
            }
        });
    }

    private void RenderWorkspaceSidebar()
    {
        var previous = rendering; rendering = true;
        try
        {
            var state = service.Snapshot;
            var listed = AddPaneMenu.Filtered(state.Workspaces, search.Text);
            var expanded = WorkspaceDisclosure.Expanded(state);
            workspaces.Children.Clear(); workspaceStatusCounts.Clear(); sidebarSessionButtons.Clear(); workspaceDisclosureButtons.Clear();
            sessionIndicators.Clear(); sidebarDetails.Clear(); sidebarMarks.Clear(); sidebarBetas.Clear(); sidebarTitles.Clear(); sidebarKindLines.Clear();
            sessionsCount.Text = state.Workspaces.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            addFolderButton.Visibility = listed.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            sidebarEmpty.Text = listed.Count == 0 ? Locale.Get(search.Text.Trim().Length > 0 ? "sidebar.noSearchResults" : "dashboard.empty") : "";
            sidebarEmpty.Visibility = listed.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            RefreshSidebarThemeButton(); RefreshDashboardEntry();
            foreach (var workspace in listed)
            {
                var selected = !showsDashboard && state.ActiveWorkspaceId == workspace.Id;
                var isExpanded = expanded.Contains(workspace.Id);
                var group = new StackPanel { Spacing = 3, Margin = new Thickness(0, 0, 0, selected ? 12 : 1) };
                AutomationProperties.SetAutomationId(group, "sidebar-workspace-" + workspace.Id);
                // The workspace row (M/WorkspaceView.swift:160-203): sidebarAccent × 0.10 at radius 7 while selected.
                var header = new Grid { CornerRadius = new CornerRadius(DesignMetrics.Radius.Search), Background = selected ? brushes.Brush(DesignToken.SidebarAccent, DesignMetrics.Opacity.SidebarWorkspaceSelected) : brushes.Transparent, ContextFlyout = WorkspaceMenu(workspace.Id) };
                header.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
                var label = new Grid { ColumnSpacing = 9 };
                label.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); label.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); label.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
                label.Children.Add(new FontIcon { Glyph = "\uE8B7", FontSize = 14, VerticalAlignment = VerticalAlignment.Center, Foreground = brushes.Brush(selected ? DesignToken.SidebarAccent : DesignToken.SidebarInk2) });
                var title = new TextBlock { Text = workspace.Name, FontSize = DesignMetrics.Type.Row, FontWeight = Microsoft.UI.Text.FontWeights.Medium, Foreground = brushes.Brush(DesignToken.Ink), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(title, 1); label.Children.Add(title);
                var counts = new StatusCountsView(brushes, "workspace-running-" + workspace.Id); workspaceStatusCounts[workspace.Id] = counts;
                counts.Update(WorkDashboard.WorkspaceBadges(state.Sessions.Where(s => s.WorkspaceId == workspace.Id), DashboardAttention), DarkTheme);
                Grid.SetColumn(counts.View, 2); label.Children.Add(counts.View);
                var select = SidebarButton(workspace.Name, () => SelectWorkspace(workspace.Id)); select.Content = label;
                select.Padding = new Thickness(11, 10, 4, 10); PlainSidebarButton(select, brushes.Transparent, brushes.Transparent);
                AutomationProperties.SetAutomationId(select, "workspace-select-" + workspace.Id); header.Children.Add(select);
                Button disclosure = null!;
                disclosure = SidebarButton(isExpanded ? "⌄" : "›", () => ToggleWorkspaceDisclosure(workspace.Id, disclosure.FocusState == FocusState.Keyboard));
                // The chevron, 8pt semibold sidebarInk2 in an 18×18 box, 6 from the trailing edge.
                disclosure.Content = new FontIcon { Glyph = isExpanded ? "\uE70D" : "\uE76C", FontSize = 8, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = brushes.Brush(DesignToken.SidebarInk2) };
                disclosure.Width = 18; disclosure.Height = 18; disclosure.MinWidth = 0; disclosure.MinHeight = 0; disclosure.Padding = new Thickness(0);
                disclosure.Margin = new Thickness(0, 0, 6, 0); disclosure.VerticalAlignment = VerticalAlignment.Center; disclosure.HorizontalAlignment = HorizontalAlignment.Center;
                disclosure.HorizontalContentAlignment = HorizontalAlignment.Center; PlainSidebarButton(disclosure, brushes.Transparent, brushes.Transparent, radius: DesignMetrics.Radius.FileRow);
                Grid.SetColumn(disclosure, 1);
                var disclosureName = Locale.Get(isExpanded ? "workspace.collapseAccessibility" : "workspace.expandAccessibility", new Dictionary<string, string> { ["workspace"] = workspace.Name });
                AutomationProperties.SetName(disclosure, disclosureName); AutomationProperties.SetAutomationId(disclosure, "workspace-expand-" + workspace.Id); ToolTipService.SetToolTip(disclosure, disclosureName);
                header.Children.Add(disclosure); workspaceDisclosureButtons[workspace.Id] = disclosure;
                ToolTipService.SetToolTip(select, workspace.Path); group.Children.Add(header);
                if (isExpanded)
                {
                    var children = new StackPanel { Spacing = 2, Margin = new Thickness(22, 0, 2, 0) };
                    foreach (var session in state.Sessions.Where(session => session.WorkspaceId == workspace.Id))
                    {
                        var active = !showsDashboard && state.ActiveSessionId == session.Id;
                        var button = SidebarButton(session.Title, () => SelectLayoutSession(session.Id));
                        button.Content = SessionIndicator(session, active: active); button.ContextFlyout = SessionMenu(session.Id);
                        StyleSidebarPaneRow(button, active);
                        AutomationProperties.SetAutomationId(button, "sidebar-session-" + session.Id);
                        AutomationProperties.SetName(button, AutomationProperties.GetName((DependencyObject)button.Content));
                        sidebarSessionButtons[session.Id] = button; children.Children.Add(button);
                    }
                    group.Children.Add(children);
                    // "창 추가", the list's last row (M/WorkspaceView.swift:322-341): plus 10 semibold and 11pt words in sidebarAccent, padding l26 r12 v8.
                    var add = SidebarButton(Locale.Get("workspace.addPane"), () => Task.CompletedTask);
                    var addLabel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                    addLabel.Children.Add(new FontIcon { Glyph = "\uE710", FontSize = 10, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Width = 12, Foreground = brushes.Brush(DesignToken.SidebarAccent), VerticalAlignment = VerticalAlignment.Center });
                    addLabel.Children.Add(new TextBlock { Text = Locale.Get("workspace.addPane"), FontSize = DesignMetrics.Type.Pill, Foreground = brushes.Brush(DesignToken.SidebarAccent), VerticalAlignment = VerticalAlignment.Center });
                    add.Content = addLabel; add.Flyout = DashboardAddMenu(workspace.Id); add.FontSize = DesignMetrics.Type.Pill; add.Padding = new Thickness(26, 8, 12, 8);
                    PlainSidebarButton(add, brushes.Transparent, brushes.Transparent);
                    AutomationProperties.SetAutomationId(add, "workspace-add-session-" + workspace.Id);
                    AutomationProperties.SetName(add, Locale.Get("workspace.addPaneAccessibility", new Dictionary<string, string> { ["workspace"] = workspace.Name }));
                    ToolTipService.SetToolTip(add, Locale.Get("workspace.addPaneHelp")); group.Children.Add(add);
                }
                workspaces.Children.Add(group);
            }
        }
        finally { rendering = previous; }
    }

    /// <summary>
    /// A pane row's wash (M/WorkspaceView.swift:516-529, SidebarRowHighlight): the <c>card</c>
    /// surface with a 0.5pt black × 0.07 hairline while selected, <c>subtle</c> under the pointer,
    /// otherwise nothing; radius 8, padding l8 r9 t6 b7. Never a status colour.
    /// </summary>
    private void StyleSidebarPaneRow(Button button, bool selected)
    {
        button.Padding = new Thickness(8, 6, 9, 7);
        button.BorderThickness = new Thickness(DesignMetrics.Stroke.Hairline);
        button.BorderBrush = selected ? brushes.RowSelectedBorder : brushes.Transparent;
        PlainSidebarButton(button, selected ? brushes.Brush(DesignToken.Card) : brushes.Transparent, selected ? brushes.Brush(DesignToken.Card) : brushes.Subtle,
            border: selected ? brushes.RowSelectedBorder : brushes.Transparent, radius: DesignMetrics.Radius.Row);
    }

    private Button SidebarButton(string title, Func<Task> action)
    {
        var button = SafeButton(title, action);
        button.HorizontalAlignment = HorizontalAlignment.Stretch; button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        button.Padding = new Thickness(8, 7, 8, 7); button.BorderThickness = new Thickness(0); button.CornerRadius = new CornerRadius(DesignMetrics.Radius.Search);
        // Unpainted: every caller paints it once with its own look (PlainSidebarButton writes each resource once).
        return button;
    }

    /// <summary>
    /// A sidebar button drawn the Mac's <c>.plain</c> way: <paramref name="normal"/> at rest,
    /// <paramref name="hover"/> under the pointer and while pressed, and no stock fill or border in
    /// any state; its own foreground, when set, in every enabled state (<see cref="PaintPlainButton"/>:
    /// lightweight styling in the button's own resources, written once, before it enters the tree).
    /// All the brushes are the window's shared ones, so a theme toggle recolours them in place.
    /// </summary>
    private void PlainSidebarButton(Button button, SolidColorBrush normal, SolidColorBrush hover, SolidColorBrush? border = null, double? radius = null)
    {
        if (border is null) button.BorderThickness = new Thickness(0);
        if (radius is { } r) button.CornerRadius = new CornerRadius(r);
        PaintPlainButton(button, normal, hover, border, button.ReadLocalValue(Control.ForegroundProperty) as SolidColorBrush);
    }

    /// <summary>The theme button's two glyphs (Segoe Fluent Icons): Brightness (sun) and QuietHours (moon).</summary>
    private const string SidebarSunGlyph = "\uE706", SidebarMoonGlyph = "\uE708";

    /// <summary>Recounts the work-status entry and every listed workspace's counts from one snapshot.</summary>
    private void RefreshSidebarStatusCounts(AppSnapshot state, bool dark)
    {
        dashboardCounts?.Update(WorkDashboard.WorkspaceBadges(state.Sessions, DashboardAttention), dark); NameDashboardEntry();
        foreach (var (id, counts) in workspaceStatusCounts) counts.Update(WorkDashboard.WorkspaceBadges(state.Sessions.Where(s => s.WorkspaceId == id), DashboardAttention), dark);
    }
}

/// <summary>
/// StatusCounts (M/WorkspaceView.swift:455-514), the short form in the sidebar: what waits on the
/// user, what runs and what stopped on an error, in that order, each a 12pt glyph and an 11pt bold
/// tabular count in <c>ink</c>, 3 apart and 9 between; a zero is left out.
/// </summary>
internal sealed class StatusCountsView
{
    internal StackPanel View { get; } = new() { Orientation = Orientation.Horizontal, Spacing = 9, VerticalAlignment = VerticalAlignment.Center };
    internal IReadOnlyList<(DesignTone Tone, StatusMark Mark, TextBlock Count, StackPanel Entry)> Entries { get; }
    /// <summary>What each entry last showed (questions and permissions for wait, the count otherwise) and the theme; -1 before the first update.</summary>
    private readonly (int A, int B, bool Dark)[] shown;
    private readonly string?[] labels;

    internal StatusCountsView(DesignBrushes brushes, string runningId)
    {
        Entries = new[] { DesignTone.Wait, DesignTone.Run, DesignTone.Err }.Select(tone =>
        {
            var mark = new StatusMark(12);
            var count = new TextBlock { FontSize = DesignMetrics.Type.Pill, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = brushes.Brush(DesignToken.Ink), VerticalAlignment = VerticalAlignment.Center };
            Microsoft.UI.Xaml.Documents.Typography.SetNumeralAlignment(count, FontNumeralAlignment.Tabular);
            var entry = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, Visibility = Visibility.Collapsed };
            entry.Children.Add(mark.View); entry.Children.Add(count);
            AutomationProperties.SetAutomationId(entry, tone == DesignTone.Run ? runningId : "status-count-" + tone.ToString().ToLowerInvariant());
            View.Children.Add(entry);
            return (tone, mark, count, entry);
        }).ToArray();
        shown = Enumerable.Repeat((-1, -1, false), Entries.Count).ToArray();
        labels = new string?[Entries.Count];
        View.Visibility = Visibility.Collapsed;
    }

    /// <summary>Forgets what was shown, so the next update writes every label again (the language changed).</summary>
    internal void Invalidate() => Array.Fill(shown, (-1, -1, false));

    /// <summary>The spoken labels of the counts that show, in order (macOS StatusCounts.labels).</summary>
    internal IEnumerable<string> Labels => labels.Where(label => label is not null)!;

    /// <summary>
    /// Shows each non-zero count with its glyph for the theme and a spoken label, and hides the
    /// rest with their marks cleared (so a hidden spark stops turning). Called every clock tick,
    /// so an entry whose value and theme did not change is left alone.
    /// </summary>
    internal void Update(WorkDashboard.Badges badges, bool dark)
    {
        for (var i = 0; i < Entries.Count; i++)
        {
            var (tone, mark, count, entry) = Entries[i];
            var value = tone switch { DesignTone.Wait => (badges.Questions, badges.Permissions, dark), DesignTone.Run => (badges.Running, 0, dark), _ => (badges.Errors, 0, dark) };
            if (shown[i] == value) continue;
            shown[i] = value;
            var total = value.Item1 + value.Item2;
            entry.Visibility = total > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (total == 0) { mark.Clear(); labels[i] = null; continue; }
            mark.Update(tone switch { DesignTone.Wait => "waiting", DesignTone.Run => "running", _ => "error" }, "claude", 0, dark);
            count.Text = total.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var label = tone == DesignTone.Wait ? WaitingLabel(badges)
                : Locale.Get("phone.dashboard.statLabel", new Dictionary<string, string> { ["label"] = Locale.Get(StatusGlyph.WordKey(tone)), ["count"] = count.Text });
            labels[i] = label;
            AutomationProperties.SetName(entry, label); ToolTipService.SetToolTip(entry, label);
        }
        View.Visibility = labels.Any(label => label is not null) ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Questions and permission requests in words (macOS StatusCounts.waitingLabel).</summary>
    private static string WaitingLabel(WorkDashboard.Badges badges) => string.Join(", ", new[]
    {
        badges.Questions > 0 ? Locale.Get("phone.card.questions", new Dictionary<string, string> { ["count"] = badges.Questions.ToString(System.Globalization.CultureInfo.InvariantCulture) }) : null,
        badges.Permissions > 0 ? Locale.Get("phone.card.permissions", new Dictionary<string, string> { ["count"] = badges.Permissions.ToString(System.Globalization.CultureInfo.InvariantCulture) }) : null,
    }.Where(text => text is not null));
}
