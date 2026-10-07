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
    private readonly Border sidebarSearchBox = new() { CornerRadius = new CornerRadius(DesignMetrics.Radius.Search), Padding = new Thickness(DesignMetrics.Inset.SidebarSearch), Margin = new Thickness(DesignMetrics.Spacing.Md, DesignMetrics.Spacing.Sm, DesignMetrics.Spacing.Md, 0) };
    private readonly FontIcon sidebarSearchIcon = new() { Glyph = "\uE721", FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
    /// <summary>The height of the search field's row: the line of AppKit's 12pt text field (M/WorkspaceView.swift:73).</summary>
    internal const double SearchRowHeight = 15;
    /// <summary>The count beside the section header's words, 10pt mono.</summary>
    private readonly TextBlock sessionsCount = new() { FontSize = DesignMetrics.Type.Small, FontFamily = new FontFamily(DesignMetrics.Font.Mono), VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel sessionsHeaderRow = new() { Orientation = Orientation.Horizontal, Spacing = DesignMetrics.Spacing.Sm, Margin = new Thickness(DesignMetrics.Inset.SidebarSectionH, DesignMetrics.Inset.SidebarSectionT, DesignMetrics.Inset.SidebarSectionH, DesignMetrics.Inset.SidebarSectionB) };
    /// <summary>What the list says when it lists no workspace: none match the search, or none yet (M/WorkspaceView.swift:89-93).</summary>
    private readonly TextBlock sidebarEmpty = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Padding = new Thickness(DesignMetrics.Spacing.Lg), Margin = new Thickness(DesignMetrics.Spacing.Sm, 0, DesignMetrics.Spacing.Sm, 0), Visibility = Visibility.Collapsed };
    /// <summary>The open-folder button's words, set again when the language changes.</summary>
    private readonly TextBlock addFolderLabel = new() { FontSize = 12, Margin = MacLine(), VerticalAlignment = VerticalAlignment.Center };
    private Border? sidebarFooterBeta;
    private Grid? sidebarFooter;

    /// <summary>
    /// The search field (M/WorkspaceView.swift:71-77): the magnifier in <c>sidebarInk2</c>, 7 apart
    /// from a borderless 12pt text box, padding 9 on the <c>subtle</c> wash at radius 7, h14 t10
    /// outside. The text box draws no fill or border in any state (lightweight resources), so the
    /// wash is the field; Ctrl+K still focuses it. The placeholder is AppKit's (M/WorkspaceView.swift:73, a
    /// plain TextField): the tertiary ink, dimmer than the magnifier.
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
        // The template takes the placeholder's ink from this property in every state; its own
        // TextControlPlaceholderForeground resources never reached the drawn text.
        search.PlaceholderForeground = brushes.Tertiary;
        search.Resources["TextControlBorderThemeThicknessFocused"] = new Thickness(0);
        // SF's magnifier draws 1.5 inside its frame and AppKit's 12pt line sets its text 1 higher than
        // Segoe's (measured on docs/design-system/crops/sidebar-top-*.webp: the symbol 10.5 and the words 31.5 from the field's edge).
        sidebarSearchIcon.Margin = new Thickness(1.5, 0, 0, 0); search.Margin = new Thickness(0, -1, 0, 0);
        // The row is the Mac text field's 12pt line, so the field is SearchRowHeight + 2 × Inset.SidebarSearch high as on the Mac.
        var row = new Grid { ColumnSpacing = DesignMetrics.Spacing.Sm, Height = SearchRowHeight };
        row.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(sidebarSearchIcon); Grid.SetColumn(search, 1); row.Children.Add(search);
        sidebarSearchBox.Child = row;
        return sidebarSearchBox;
    }

    /// <summary>The section header "워크스페이스 6": 10pt semibold words and a 10pt mono count, both <c>sidebarInk2</c>, padding h20 t20 b11.</summary>
    private FrameworkElement BuildSidebarSectionHeader()
    {
        sessionsHeader.Foreground = sessionsCount.Foreground = sidebarEmpty.Foreground = brushes.Brush(DesignToken.SidebarInk2);
        // The Mac's 10pt line is 12 high where Segoe's is 14.
        sessionsHeader.Margin = sessionsCount.Margin = MacLine();
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
        var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DesignMetrics.Spacing.Sm };
        label.Children.Add(new FontIcon { Glyph = "\uE8F4", FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        label.Children.Add(addFolderLabel); addFolderButton.Content = label;
        addFolderButton.Foreground = brushes.Brush(DesignToken.Ink);
        addFolderButton.HorizontalAlignment = HorizontalAlignment.Stretch; addFolderButton.HorizontalContentAlignment = HorizontalAlignment.Left;
        addFolderButton.FontSize = 12; addFolderButton.Padding = new Thickness(DesignMetrics.Spacing.Sm); addFolderButton.Margin = new Thickness(DesignMetrics.Spacing.Md, 0, DesignMetrics.Spacing.Md, DesignMetrics.Spacing.Md);
        PlainSidebarButton(addFolderButton, brushes.Subtle, brushes.Subtle, radius: DesignMetrics.Radius.Search);
    }

    /// <summary>
    /// The Windows-only layout picker above the footer (decision Q3), as a sidebar field: 12pt on
    /// the <c>subtle</c> wash, radius 7, no border, the glyph in <c>sidebarInk2</c>, h14 v8 outside.
    /// The pet controls that sat beside it are in the status bar, as on the Mac (M/WorkspaceView.swift:391).
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
        var tools = new Grid { Margin = new Thickness(DesignMetrics.Spacing.Md, DesignMetrics.Spacing.Sm, DesignMetrics.Spacing.Md, DesignMetrics.Spacing.Sm) };
        tools.Children.Add(layout);
        return tools;
    }

    /// <summary>
    /// The footer under a full-width <c>line</c> (M/WorkspaceView.swift:108-120): the app icon 20,
    /// <c>Spacing.Md</c> from "Mighty Claude" 12pt semibold with the app's 베타 capsule right after it, then the
    /// help, theme and settings symbols, plain; the current app version sits below the name in muted 10pt mono.
    /// Help is Windows' own (the Mac's is in its Help menu), set the same pitch before the theme symbol.
    /// </summary>
    private FrameworkElement BuildSidebarFooter()
    {
        // Mac groups the brand and the two global actions below the project tree.
        // Keep that grouping while retaining native focus, tooltip and UIA behavior.
        var footer = sidebarFooter = new Grid { Padding = SidebarFooterPadding, BorderThickness = new Thickness(0, DesignMetrics.Stroke.Line, 0, 0), BorderBrush = Separator };
        AutomationProperties.SetAutomationId(footer, "sidebar-footer");
        footer.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        // Left-aligned, so the name takes only its own width and the capsule stays right after it while the name still trims.
        var brand = new Grid { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, RowSpacing = DesignMetrics.Spacing.Xxs };
        brand.RowDefinitions.Add(new() { Height = GridLength.Auto }); brand.RowDefinitions.Add(new() { Height = GridLength.Auto });
        brand.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); brand.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); brand.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var image = new Image { Source = new BitmapImage(new Uri("ms-appx:///Assets/mightyclaude.png")), Width = 20, Height = 20, Margin = new Thickness(0, 0, DesignMetrics.Spacing.Md, 0) };
        AutomationProperties.SetAccessibilityView(image, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw); Grid.SetRowSpan(image, 2); brand.Children.Add(image);
        var title = new TextBlock { Text = "Mighty Claude", FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = brushes.Brush(DesignToken.Ink), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(title, 1); brand.Children.Add(title);
        var version = new TextBlock { Text = "v" + AppVersionText, FontSize = DesignMetrics.Type.Small, FontFamily = new FontFamily(DesignMetrics.Font.Mono), Foreground = brushes.Brush(DesignToken.SidebarInk2), TextTrimming = TextTrimming.CharacterEllipsis };
        AutomationProperties.SetAutomationId(version, "sidebar-app-version");
        Grid.SetColumn(version, 1); Grid.SetColumnSpan(version, 2); Grid.SetRow(version, 1); brand.Children.Add(version);
        // The whole Windows app is a beta: the same capsule as a beta agent's, 6 after the name (M/BetaBadge.swift, M/WorkspaceView.swift:225).
        brandBeta.Foreground = brushes.Brush(DesignToken.StopText);
        var beta = sidebarFooterBeta = new Border { Child = brandBeta, CornerRadius = new CornerRadius(7), Background = brushes.Brush(DesignToken.StopSoft), Padding = new Thickness(DesignMetrics.Spacing.Xs, 1, DesignMetrics.Spacing.Xs, 1), Margin = new Thickness(DesignMetrics.Spacing.Sm, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(beta, 2); brand.Children.Add(beta); footer.Children.Add(brand);
        var help = BuildSidebarHelpButton(); Grid.SetColumn(help, 1); footer.Children.Add(help);
        var theme = BuildSidebarThemeButton(); Grid.SetColumn(theme, 2); footer.Children.Add(theme);
        settingsButton.Content = new FontIcon { Glyph = "", FontSize = SidebarFooterGlyph };
        AutomationProperties.SetAutomationId(settingsButton, "sidebar-settings");
        // The Mac's two symbols are plain 13pt images. Each button keeps a hit area around its symbol,
        // reaching into the padding and a little over its neighbour, so the symbols land where the
        // Mac's do, centered beside the name and version.
        const double inset = (SidebarFooterButton - SidebarFooterGlyph) / 2, overhang = (SidebarFooterButton - 20) / 2;
        foreach (var button in new[] { help, theme, settingsButton })
        {
            button.Width = SidebarFooterButton; button.Height = SidebarFooterButton; button.MinWidth = 0; button.MinHeight = 0; button.Padding = new Thickness(0);
            button.Foreground = brushes.Brush(DesignToken.Ink); PlainSidebarButton(button, brushes.Transparent, brushes.Subtle, radius: DesignMetrics.Radius.Search);
        }
        help.Margin = new Thickness(DesignMetrics.Spacing.Lg - inset, -overhang, 0, -overhang);
        theme.Margin = new Thickness(SidebarFooterPitch - SidebarFooterButton, -overhang, 0, -overhang);
        settingsButton.Margin = new Thickness(SidebarFooterPitch - SidebarFooterButton, -overhang, SidebarFooterGearCentre - SidebarFooterButton / 2, -overhang);
        Grid.SetColumn(settingsButton, 3); footer.Children.Add(settingsButton);
        return footer;
    }

    /// <summary>
    /// The footer's symbols (M/WorkspaceView.swift:116-119): SF's moon and gearshape at the default 13pt draw
    /// 13 and 14 wide, as Segoe's glyphs do at 14; the hit area kept around each; and where the Mac sets
    /// them — the gear's centre 8 inside the padding, the theme symbol's 24.5 before it (measured on
    /// docs/design-system/screens/01-main-mighty-diagram-light.webp: 24 and 48.5 from the sidebar's dividing line).
    /// </summary>
    internal const double SidebarFooterGlyph = 14, SidebarFooterButton = 26, SidebarFooterGearCentre = 8, SidebarFooterPitch = 24.5;
    /// <summary>The footer's padding: <c>Spacing.Lg</c> at the sides, <c>Spacing.Md</c> over and under (M/WorkspaceView.swift:139).</summary>
    internal static readonly Thickness SidebarFooterPadding = new(DesignMetrics.Spacing.Lg, DesignMetrics.Spacing.Md, DesignMetrics.Spacing.Lg, DesignMetrics.Spacing.Md);
    /// <summary>The least height of a workspace row and of the add row under its panes (M/WorkspaceView.swift:189, 343).</summary>
    internal const double SidebarWorkspaceRowMin = 24, SidebarAddRowMin = 22;
    /// <summary>A pane row's indent under its workspace and its trailing room (M/WorkspaceView.swift:266).</summary>
    internal static readonly Thickness SidebarPaneRowsMargin = new(22, 0, DesignMetrics.Spacing.Xxs, 0);

    /// <summary>
    /// The margin that sets a line of 10 to 13pt words on the Mac's line: SF Pro's line is about 1.19 em
    /// (14.3 at 12pt) where Segoe's is 1.33 em (16), so the words give up 1pt above and below. A margin,
    /// not a height, so no glyph is clipped; a row whose height is its words' then measures as the Mac's
    /// (the workspace row 34, the add row 29).
    /// </summary>
    internal static Thickness MacLine(double left = 0) => new(left, -1, 0, -1);

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
        sidebarThemeButton.Content = new FontIcon { Glyph = service.Snapshot.Theme == "light" ? SidebarMoonGlyph : SidebarSunGlyph, FontSize = SidebarFooterGlyph };
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
            sidebarDrawnForDashboard = showsDashboard;
            workspaces.Children.Clear(); workspaceStatusCounts.Clear(); sidebarSessionButtons.Clear(); workspaceDisclosureButtons.Clear();
            sessionIndicators.Clear(); sidebarDetails.Clear(); sidebarMarks.Clear(); sidebarBetas.Clear(); sidebarTitles.Clear(); sidebarKindLines.Clear(); sidebarRowEdges.Clear();
            sessionsCount.Text = state.Workspaces.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            addFolderButton.Visibility = listed.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            sidebarEmpty.Text = listed.Count == 0 ? Locale.Get(search.Text.Trim().Length > 0 ? "sidebar.noSearchResults" : "dashboard.empty") : "";
            sidebarEmpty.Visibility = listed.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            RefreshSidebarThemeButton(); RefreshDashboardEntry();
            foreach (var workspace in listed)
            {
                var selected = !showsDashboard && state.ActiveWorkspaceId == workspace.Id;
                var isExpanded = expanded.Contains(workspace.Id);
                var group = new StackPanel { Spacing = DesignMetrics.Inset.ListGap, Margin = new Thickness(0, 0, 0, selected ? DesignMetrics.Spacing.Sm : 0) };
                AutomationProperties.SetAutomationId(group, "sidebar-workspace-" + workspace.Id);
                // The workspace row (M/WorkspaceView.swift:160-203): sidebarAccent × 0.10 at radius 7 while selected.
                var header = new Grid { CornerRadius = new CornerRadius(DesignMetrics.Radius.Search), Background = selected ? brushes.Brush(DesignToken.SidebarAccent, DesignMetrics.Opacity.SidebarWorkspaceSelected) : brushes.Transparent, ContextFlyout = WorkspaceMenu(workspace.Id) };
                header.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
                var label = new Grid { ColumnSpacing = DesignMetrics.Spacing.Md };
                label.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); label.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); label.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
                label.Children.Add(new FontIcon { Glyph = "\uE8B7", FontSize = 14, VerticalAlignment = VerticalAlignment.Center, Foreground = brushes.Brush(selected ? DesignToken.SidebarAccent : DesignToken.SidebarInk2) });
                var title = new TextBlock { Text = workspace.Name, FontSize = DesignMetrics.Type.Row, FontWeight = Microsoft.UI.Text.FontWeights.Medium, Foreground = brushes.Brush(DesignToken.Ink), TextTrimming = TextTrimming.CharacterEllipsis, Margin = MacLine(), VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(title, 1); label.Children.Add(title);
                var counts = new StatusCountsView(brushes, "workspace-running-" + workspace.Id); workspaceStatusCounts[workspace.Id] = counts;
                counts.Update(WorkDashboard.WorkspaceBadges(state.Sessions.Where(s => s.WorkspaceId == workspace.Id), DashboardAttention), DarkTheme);
                // The Mac's Spacer stands between the name and the counts (M/WorkspaceView.swift:168-174).
                counts.View.Margin = new Thickness(DesignMetrics.Spacing.Sm, 0, 0, 0);
                Grid.SetColumn(counts.View, 2); label.Children.Add(counts.View);
                var select = SidebarButton(workspace.Name, () => SelectWorkspace(workspace.Id)); select.Content = label;
                select.Padding = new Thickness(DesignMetrics.Spacing.Sm, DesignMetrics.Inset.SidebarRowV, DesignMetrics.Spacing.Xs, DesignMetrics.Inset.SidebarRowV); select.MinHeight = SidebarWorkspaceRowMin;
                PlainSidebarButton(select, brushes.Transparent, brushes.Transparent);
                AutomationProperties.SetAutomationId(select, "workspace-select-" + workspace.Id); header.Children.Add(select);
                Button disclosure = null!;
                disclosure = SidebarButton(isExpanded ? "⌄" : "›", () => ToggleWorkspaceDisclosure(workspace.Id, disclosure.FocusState == FocusState.Keyboard));
                // The chevron, 8pt semibold sidebarInk2 in an 18×18 box, 6 from the trailing edge.
                disclosure.Content = new FontIcon { Glyph = isExpanded ? "\uE70D" : "\uE76C", FontSize = 8, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = brushes.Brush(DesignToken.SidebarInk2) };
                disclosure.Width = 18; disclosure.Height = 18; disclosure.MinWidth = 0; disclosure.MinHeight = 0; disclosure.Padding = new Thickness(0);
                disclosure.Margin = new Thickness(0, 0, DesignMetrics.Spacing.Sm, 0); disclosure.VerticalAlignment = VerticalAlignment.Center; disclosure.HorizontalAlignment = HorizontalAlignment.Center;
                disclosure.HorizontalContentAlignment = HorizontalAlignment.Center; PlainSidebarButton(disclosure, brushes.Transparent, brushes.Transparent, radius: DesignMetrics.Radius.FileRow);
                Grid.SetColumn(disclosure, 1);
                var disclosureName = Locale.Get(isExpanded ? "workspace.collapseAccessibility" : "workspace.expandAccessibility", new Dictionary<string, string> { ["workspace"] = workspace.Name });
                AutomationProperties.SetName(disclosure, disclosureName); AutomationProperties.SetAutomationId(disclosure, "workspace-expand-" + workspace.Id); ToolTipService.SetToolTip(disclosure, disclosureName);
                header.Children.Add(disclosure); workspaceDisclosureButtons[workspace.Id] = disclosure;
                ToolTipService.SetToolTip(select, workspace.Path); group.Children.Add(header);
                if (isExpanded)
                {
                    // Each pane row is set in SidebarPaneRowsMargin, the group's list gap under the workspace row,
                    // between rows and over the add row (M/WorkspaceView.swift:178, 266).
                    var children = new StackPanel { Spacing = DesignMetrics.Inset.ListGap, Margin = SidebarPaneRowsMargin };
                    foreach (var session in state.Sessions.Where(session => session.WorkspaceId == workspace.Id))
                    {
                        var active = !showsDashboard && state.ActiveSessionId == session.Id;
                        var button = SidebarButton(session.Title, () => SelectLayoutSession(session.Id));
                        button.Content = SessionIndicator(session, active: active); button.ContextFlyout = SidebarPaneMenu(session.Id);
                        StyleSidebarPaneRow(button, active);
                        AutomationProperties.SetAutomationId(button, "sidebar-session-" + session.Id);
                        AutomationProperties.SetName(button, AutomationProperties.GetName((DependencyObject)button.Content));
                        sidebarSessionButtons[session.Id] = button; children.Children.Add(button);
                    }
                    if (children.Children.Count > 0) group.Children.Add(children);
                    // The add row, the list's last (M/WorkspaceView.swift:322-343): plus 10 semibold and 11pt words in sidebarAccent, padding l28 r12 and the row inset over and under.
                    var add = SidebarButton(Locale.Get("workspace.addPane"), () => Task.CompletedTask);
                    var addLabel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DesignMetrics.Spacing.Sm };
                    addLabel.Children.Add(new FontIcon { Glyph = "\uE710", FontSize = 10, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Width = 12, Foreground = brushes.Brush(DesignToken.SidebarAccent), VerticalAlignment = VerticalAlignment.Center });
                    addLabel.Children.Add(new TextBlock { Text = Locale.Get("workspace.addPane"), FontSize = DesignMetrics.Type.Pill, Foreground = brushes.Brush(DesignToken.SidebarAccent), Margin = MacLine(), VerticalAlignment = VerticalAlignment.Center });
                    add.Content = addLabel; add.Flyout = DashboardAddMenu(workspace.Id); add.FontSize = DesignMetrics.Type.Pill; add.Padding = new Thickness(28, DesignMetrics.Inset.SidebarRowV, DesignMetrics.Spacing.Lg, DesignMetrics.Inset.SidebarRowV); add.MinHeight = SidebarAddRowMin;
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
    /// surface while selected, <c>subtle</c> under the pointer, otherwise nothing; radius 8, padding
    /// <see cref="SidebarPaneRowPadding"/>. Never a status colour. The selected row's 0.5pt black hairline is an overlay on
    /// the row's content (<see cref="SessionIndicator"/>), as on the Mac, so the row keeps its height.
    /// </summary>
    private void StyleSidebarPaneRow(Button button, bool selected)
    {
        button.Padding = SidebarPaneRowPadding;
        PlainSidebarButton(button, selected ? brushes.Brush(DesignToken.Card) : brushes.Transparent, selected ? brushes.Brush(DesignToken.Card) : brushes.Subtle, radius: DesignMetrics.Radius.Row);
    }

    private Button SidebarButton(string title, Func<Task> action)
    {
        var button = SafeButton(title, action);
        button.HorizontalAlignment = HorizontalAlignment.Stretch; button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        button.Padding = new Thickness(DesignMetrics.Spacing.Sm, DesignMetrics.Inset.SidebarRowV, DesignMetrics.Spacing.Sm, DesignMetrics.Inset.SidebarRowV); button.BorderThickness = new Thickness(0); button.CornerRadius = new CornerRadius(DesignMetrics.Radius.Search);
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
/// StatusCounts (M/WorkspaceView.swift:455-514). The short form in the sidebar: what waits on the
/// user, what runs and what stopped on an error, in that order, each a 12pt glyph and an 11pt bold
/// tabular count in <c>ink</c>, 3 apart and 9 between; a zero is left out. The long form in the
/// workspace header also counts what has settled (done, stopped, idle) and names each state in
/// 11pt medium <c>ink2</c>, 1 further from its count: "✳ 1 running  ✓ 1 done".
/// </summary>
internal sealed class StatusCountsView
{
    internal StackPanel View { get; } = new() { Orientation = Orientation.Horizontal, Spacing = DesignMetrics.Spacing.Sm, VerticalAlignment = VerticalAlignment.Center };
    /// <summary>Each entry's parts; <c>Word</c> is the state's name, drawn only in the long form.</summary>
    internal IReadOnlyList<(DesignTone Tone, StatusMark Mark, TextBlock Count, StackPanel Entry, TextBlock? Word)> Entries { get; }
    /// <summary>What each entry last showed (questions and permissions for wait, the count otherwise) and the theme; -1 before the first update.</summary>
    private readonly (int A, int B, bool Dark)[] shown;
    private readonly string?[] labels;

    /// <summary>The tones counted, in the mockup's order: waiting, running, error, then (long only) done, stopped, idle (M/WorkspaceView.swift:491-502).</summary>
    internal static IReadOnlyList<DesignTone> Tones(bool longForm) => longForm
        ? [DesignTone.Wait, DesignTone.Run, DesignTone.Err, DesignTone.Done, DesignTone.Stop, DesignTone.Idle]
        : [DesignTone.Wait, DesignTone.Run, DesignTone.Err];

    /// <summary>A tone's count in a workspace's badges; waiting is its questions and permissions.</summary>
    internal static (int A, int B) Counted(WorkDashboard.Badges badges, DesignTone tone) => tone switch
    {
        DesignTone.Wait => (badges.Questions, badges.Permissions), DesignTone.Run => (badges.Running, 0), DesignTone.Err => (badges.Errors, 0),
        DesignTone.Done => (badges.Done, 0), DesignTone.Stop => (badges.Stopped, 0), _ => (badges.Idle, 0),
    };

    /// <summary>The status a tone's glyph is drawn for.</summary>
    private static string Status(DesignTone tone) => tone switch
    {
        DesignTone.Wait => "waiting", DesignTone.Run => "running", DesignTone.Err => "error",
        DesignTone.Done => "completed", DesignTone.Stop => "stopped", _ => "idle",
    };

    internal StatusCountsView(DesignBrushes brushes, string runningId, bool longForm = false)
    {
        Entries = Tones(longForm).Select(tone =>
        {
            var mark = new StatusMark(12);
            // The words stand on the Mac's 13pt line (MainWindow.Sidebar.cs, MacLine), so the counts never make a row taller than the Mac's.
            var count = new TextBlock { FontSize = DesignMetrics.Type.Pill, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = brushes.Brush(DesignToken.Ink), Margin = MainWindow.MacLine(), VerticalAlignment = VerticalAlignment.Center };
            Microsoft.UI.Xaml.Documents.Typography.SetNumeralAlignment(count, FontNumeralAlignment.Tabular);
            var entry = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DesignMetrics.Spacing.Xxs, Visibility = Visibility.Collapsed };
            entry.Children.Add(mark.View); entry.Children.Add(count);
            TextBlock? word = null;
            if (longForm)
            {
                word = new TextBlock { FontSize = DesignMetrics.Type.Pill, FontWeight = Microsoft.UI.Text.FontWeights.Medium, Foreground = brushes.Brush(DesignToken.Ink2), Margin = MainWindow.MacLine(left: 1), VerticalAlignment = VerticalAlignment.Center };
                entry.Children.Add(word);
            }
            AutomationProperties.SetAutomationId(entry, tone == DesignTone.Run ? runningId : "status-count-" + tone.ToString().ToLowerInvariant());
            View.Children.Add(entry);
            return (tone, mark, count, entry, word);
        }).ToArray();
        shown = Enumerable.Repeat((-1, -1, false), Entries.Count).ToArray();
        labels = new string?[Entries.Count];
        View.Visibility = Visibility.Collapsed;
    }

    /// <summary>Renames the running entry for the workspace the counts now describe (the header is reused across workspaces).</summary>
    internal void Identify(string runningId)
    {
        var running = Entries.First(e => e.Tone == DesignTone.Run).Entry;
        if (AutomationProperties.GetAutomationId(running) != runningId) AutomationProperties.SetAutomationId(running, runningId);
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
            var (tone, mark, count, entry, word) = Entries[i];
            var (a, b) = Counted(badges, tone);
            if (shown[i] == (a, b, dark)) continue;
            shown[i] = (a, b, dark);
            var total = a + b;
            entry.Visibility = total > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (total == 0) { mark.Clear(); labels[i] = null; continue; }
            mark.Update(Status(tone), "claude", 0, dark);
            count.Text = total.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (word is not null) word.Text = Locale.Get(StatusGlyph.WordKey(tone));
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
