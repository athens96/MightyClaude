using MightyClaude.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace MightyClaude.WinUI;

public sealed partial class MainWindow
{
    private async Task<Dictionary<string, object?>> RunDesktopSurfaceSmoke()
    {
        var original = service.Snapshot; var first = original.Sessions.First(p => p.Kind == "claude");
        var existing = views[first.Id]; var captures = new List<string>(); var categoryCount = 0;
        var savedCompanion = companionPreferences;
        var workspace = original.Workspaces.First(w => w.Id == first.WorkspaceId);
        try
        {
            await PrepareCompanionSettingsSmoke();
            dashboardGit[workspace.Id] = (workspace.Path, new WorkspaceGitInfo("feature/parity", "0123456789abcdef", true, 2, 1), DateTimeOffset.UtcNow);
            foreach (var theme in new[] { "dark", "light" })
            {
                await service.UpdateAsync(state => state with { Theme = theme }); Render();
                showsDashboard = true; RenderDashboard();
                await WaitUI(() => dashboard?.Visibility == Visibility.Visible && dashboard.ActualWidth > 0 && panes.Visibility == Visibility.Collapsed);
                var dashboardView = dashboard!.Content;
                RefreshRunningIndicators();
                Require(ReferenceEquals(dashboardView, dashboard.Content), "clock ticks must preserve dashboard controls");
                // On the second theme pass the ScrollViewer retains its old
                // ActualWidth while its newly assigned content is unrealized.
                // Wait for the actual provider controls, not that outer size.
                root.UpdateLayout();
                await WaitUI(() => original.Sessions.Where(s => s.Kind == "claude").All(agent =>
                    VisualChildren(dashboard).OfType<FrameworkElement>().Any(v => v.IsLoaded && v.ActualWidth > 0 && v.ActualHeight > 0
                        && AutomationProperties.GetAutomationId(v) == "dashboard-provider-" + agent.Id
                        && ProviderMarkView.LabelledProvider(v) == agent.Provider)), "Dashboard provider marks must be loaded and arranged for " + theme);
                var drawn = VisualChildren(dashboard).OfType<FrameworkElement>().ToArray();
                foreach (var agent in original.Sessions.Where(s => s.Kind == "claude"))
                    Require(drawn.Any(v => AutomationProperties.GetAutomationId(v) == "dashboard-provider-" + agent.Id && ProviderMarkView.LabelledProvider(v) == agent.Provider), "Dashboard agent rows must carry their provider marks.");
                foreach (var project in original.Workspaces)
                {
                    // The path is cut in the middle when it is long (as on the Mac); its tooltip always carries all of it.
                    Require(drawn.OfType<TextBlock>().Any(v => v.Text.Length > 0 && ToolTipService.GetToolTip(v) as string == project.Path && (v.Text == project.Path || v.Text.Contains('…'))), "Dashboard workspace path is missing.");
                    Require(drawn.OfType<Button>().Any(v => AutomationProperties.GetAutomationId(v) == "dashboard-open-files-" + project.Id), "Dashboard workspace Files action is missing.");
                    var add = drawn.OfType<Button>().Single(v => AutomationProperties.GetAutomationId(v) == "dashboard-add-session-" + project.Id);
                    Require(add.Flyout is MenuFlyout menu && menu.Items.Count == AddPaneMenu.Entries().Count, "Dashboard Add Pane must offer the full shared menu.");
                }
                Require(dashboardGitLabels[workspace.Id] is { Capsule.Visibility: Visibility.Visible, Dirty.Visibility: Visibility.Visible, Ahead.Text: "↑2", Behind.Text: "↓1" } git && git.Label.Text.Contains("feature/parity", StringComparison.Ordinal), "Dashboard must display its workspace Git status.");
                await SettleDesktopCapture(root);
                CheckSidebarChromeForSmoke();
                captures.Add(await CaptureSmoke(Path.Combine(options.ProfileDirectory!, "smoke-dashboard-" + theme + ".png")));
                await SelectLayoutSession(first.Id);
                Require(!showsDashboard && ReferenceEquals(existing, views[first.Id]), "dashboard navigation must retain the same editor and pane");

                var opening = ShowCategorizedSettingsAsync();
                await WaitUI(() => settingsWindow?.Content is FrameworkElement element && element.XamlRoot is not null && element.ActualWidth > 0);
                var frame = (FrameworkElement)settingsWindow!.Content;
                var navigation = VisualChildren(frame).OfType<ListView>().Single(view => AutomationProperties.GetAutomationId(view) == "settings-navigation");
                Require(navigation.Items.Count == SettingsNavigation.Available.Count && navigation.Items.Count == 8, "all macOS settings categories must be visible");
                foreach (var item in navigation.Items.OfType<ListViewItem>())
                {
                    navigation.SelectedItem = item;
                    var category = (SettingsCategory)item.Tag;
                    await WaitUI(() => service.Snapshot.SettingsPane == category.Id);
                    if (category.Id == "models") await CheckPhaseModelsSettingsActions(frame);
                    if (category.Id == "styles")
                        await WaitUI(() => new[] { "ouroboros", "paperthin", "superpowers" }.All(id => VisualChildren(frame).OfType<StackPanel>().Any(row => row.IsLoaded && AutomationProperties.GetAutomationId(row) == "settings-style-" + id)));
                    if (category.Id == "companion")
                        await WaitUI(() => VisualChildren(frame).OfType<ComboBox>().Any(picker => AutomationProperties.GetAutomationId(picker) == "settings-companion-pet" && picker.Items.Count > 0 && picker.SelectedItem is not null)
                            && VisualChildren(frame).OfType<Image>().Any(preview => AutomationProperties.GetAutomationId(preview) == "settings-companion-preview" && preview.Source is BitmapImage { PixelWidth: > 0 }));
                    frame.UpdateLayout();
                    var headings = VisualChildren(frame).OfType<TextBlock>().Where(text => AutomationProperties.GetAutomationId(text).StartsWith("settings-section-", StringComparison.Ordinal)).Select(text => text.Text).ToArray();
                    // The tab's boxes in the Mac's order (M/SettingsViews.swift:218-322), every slot Core gives the tab among them.
                    var expected = SettingsGroups(category.Id).Select(group => group.Title).ToArray();
                    var slots = category.Sections.Select(id => SettingsSections.Windows.Single(section => section.Id == id).WindowsTitle!).ToArray();
                    Require(headings.SequenceEqual(expected) && slots.All(headings.Contains), "settings category contains missing or misplaced sections: " + category.Id + " shows " + string.Join(", ", headings));
                    Require(navigation.SelectedItems.Count == 1 && ReferenceEquals(navigation.SelectedItem, item), "Only the displayed settings category may be selected.");
                    categoryCount++;
                    // Every tab is captured in both themes, to be read next to docs/design-system/screens/10-settings-*.webp.
                    await SettleDesktopCapture(frame);
                    captures.Add(await CaptureElement(frame, Path.Combine(options.ProfileDirectory!, "smoke-settings-" + category.Id + "-" + theme + ".png")));
                    // A tab longer than the sheet is captured page by page to its end (the Mac's 10-settings-cli-2 and 10-settings-models-2).
                    var form = (ScrollViewer)((Border)VisualChildren(frame).OfType<FrameworkElement>().Single(element => AutomationProperties.GetAutomationId(element) == "settings-form")).Child;
                    for (var page = 2; form.VerticalOffset < form.ScrollableHeight - 1; page++)
                    {
                        var next = Math.Min(form.VerticalOffset + form.ViewportHeight, form.ScrollableHeight);
                        form.ChangeView(null, next, null, true);
                        await WaitUI(() => Math.Abs(form.VerticalOffset - next) < 1, () => "the settings form never scrolled to " + next + " in " + category.Id);
                        await SettleDesktopCapture(frame);
                        captures.Add(await CaptureElement(frame, Path.Combine(options.ProfileDirectory!, "smoke-settings-" + category.Id + "-" + theme + "-" + page + ".png")));
                    }
                    if (category.Id == "styles") captures.Add(await CaptureStyleApprovalSheet(frame, theme));
                }
                settingsWindow.Close(); await opening;
            }
            await CheckDashboardActions(first, original.Workspaces.Last());
            return new() { ["nativeSidebarChrome"] = true, ["phaseModelsControlsPersist"] = true, ["dashboardRetainsPane"] = true, ["clockPreservesDashboardControls"] = true, ["dashboardWorkspaceActions"] = true, ["dashboardProviderMarks"] = true, ["settingsLoadedBeforeCapture"] = true, ["allSettingsCategories"] = categoryCount == 16, ["bothThemes"] = true, ["screenshots"] = captures };
        }
        finally
        {
            settingsWindow?.Close(); HideDashboard(); dashboardGit.Remove(workspace.Id); companionPreferences = savedCompanion; await service.UpdateAsync(_ => original); Render();
        }
    }
    private async Task CheckDashboardActions(RunSession first, Workspace other)
    {
        await SelectLayoutSession(first.Id); showsDashboard = true; RenderDashboard(); root.UpdateLayout();
        await WaitUI(() => VisualChildren(dashboard!).OfType<Button>().Any(button => button.IsLoaded && button.ActualWidth > 0 && AutomationProperties.GetAutomationId(button) == "dashboard-open-files-" + other.Id));
        var files = VisualChildren(dashboard!).OfType<Button>().Single(button => AutomationProperties.GetAutomationId(button) == "dashboard-open-files-" + other.Id);
        ((IInvokeProvider)new ButtonAutomationPeer(files).GetPattern(PatternInterface.Invoke)).Invoke();
        await WaitUI(() => !showsDashboard && service.Snapshot.Sessions.Any(pane => pane.Kind == FilePaneKind.Kind && pane.WorkspaceId == other.Id) && service.Snapshot.ActiveWorkspaceId == other.Id);
        await SelectLayoutSession(first.Id); showsDashboard = true; RenderDashboard();
        var before = service.Snapshot.Sessions.Count;
        await DashboardAddPane(other.Id, AddPaneMenu.Shell);
        Require(!showsDashboard && service.Snapshot.Sessions.Count == before + 1 && service.Snapshot.Sessions.Last() is { Kind: "shell" } added && added.WorkspaceId == other.Id,
            "Dashboard Add Pane must add to the chosen workspace, not the previously active workspace.");
        await SelectLayoutSession(first.Id); before = service.Snapshot.Sessions.Count;
        await DashboardAddPane("removed-workspace", AddPaneMenu.Shell);
        Require(service.Snapshot.Sessions.Count == before && service.Snapshot.ActiveWorkspaceId == first.WorkspaceId, "A stale dashboard menu must not create a pane in another workspace.");
    }
    /// <summary>
    /// Opens a bundled style's approval sheet for reading, as its 내용 보기 button does, checks it is the
    /// Mac's sheet (M/StyleApprovalSheet.swift:39-134: 640×560, the heading, the sections, the one close
    /// button and no allow button while only reading) and captures it over the settings window.
    /// </summary>
    private async Task<string> CaptureStyleApprovalSheet(FrameworkElement frame, string theme)
    {
        var workspace = service.Snapshot.Workspaces.FirstOrDefault(w => w.Id == service.Snapshot.ActiveWorkspaceId)?.Path ?? StateDirectory;
        var style = (await Task.Run(() => StyleRegistry.Load(StateDirectory, workspace))).Styles.First(s => s.Source == "bundled");
        var showing = ShowStyleApproval(style, readOnly: true);
        ContentDialog? sheet = null;
        await WaitUI(() => (sheet = VisualTreeHelper.GetOpenPopupsForXamlRoot(frame.XamlRoot).Select(popup => popup.Child).OfType<ContentDialog>().FirstOrDefault()) is { IsLoaded: true, Content: FrameworkElement { ActualWidth: > 0 } },
            () => "the style approval sheet never opened over the settings window");
        try
        {
            // The Mac's size, or what a smaller settings window leaves it.
            var fits = frame.XamlRoot.Size;
            Require(AutomationProperties.GetAutomationId(sheet!) == "style-approval-sheet" && OwnResource(sheet!, "ContentDialogMinWidth") is double width && width == SheetFit(StyleApprovalWidth, fits.Width) && OwnResource(sheet!, "ContentDialogMaxHeight") is double height && height == SheetFit(StyleApprovalHeight, fits.Height),
                $"the style approval sheet must be {StyleApprovalWidth}×{StyleApprovalHeight}, or as much of that as the {fits.Width}×{fits.Height} window leaves; got {OwnResource(sheet!, "ContentDialogMinWidth")}×{OwnResource(sheet!, "ContentDialogMaxHeight")}");
            var parts = SettingsElements((FrameworkElement)sheet!.Content).ToList();
            string[] Ids(string prefix) => parts.Select(AutomationProperties.GetAutomationId).Where(id => id.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
            Require(Ids("style-approval-autoAllow").Length == 1 && Ids("style-approval-raw").Length == 1 && Ids("style-approval-dismiss").Length == 1 && Ids("style-approval-allow").Length == 0,
                "a style opened for reading must show its auto-allow list, its raw JSON and a close button, and no allow button: " + string.Join(", ", Ids("style-approval-")));
            var dismiss = parts.OfType<Button>().Single(button => AutomationProperties.GetAutomationId(button) == "style-approval-dismiss");
            Require((string)dismiss.Content == Locale.Get("settings.closeButton"), "a style opened for reading closes with the close button; got " + dismiss.Content);
            await SettleDesktopCapture(sheet);
            return await CaptureElement(sheet, Path.Combine(options.ProfileDirectory!, "smoke-style-approval-" + theme + ".png"));
        }
        finally { sheet!.Hide(); await showing; }
    }
    private static async Task SettleDesktopCapture(FrameworkElement frame)
    {
        frame.UpdateLayout();
        // WinUI selection and toggle transitions run on the compositor after
        // IsSelected/IsOn and layout have settled. Capture their final visuals.
        await Task.Delay(350);
        frame.UpdateLayout();
    }
    private static IEnumerable<DependencyObject> VisualChildren(DependencyObject root)
    {
        yield return root;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in VisualChildren(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
    private static async Task<string> CaptureElement(FrameworkElement element, string path)
    {
        var bitmap = new RenderTargetBitmap(); await bitmap.RenderAsync(element);
        if (bitmap.PixelWidth == 0 || bitmap.PixelHeight == 0) throw new InvalidOperationException("The settings window could not be captured.");
        var buffer = await bitmap.GetPixelsAsync(); using var reader = DataReader.FromBuffer(buffer); var pixels = new byte[buffer.Length]; reader.ReadBytes(pixels);
        using var stream = new InMemoryRandomAccessStream(); var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels); await encoder.FlushAsync();
        using var source = stream.GetInputStreamAt(0); using var output = new DataReader(source); await output.LoadAsync((uint)stream.Size); var png = new byte[(int)stream.Size]; output.ReadBytes(png); await File.WriteAllBytesAsync(path, png); return path;
    }
}
