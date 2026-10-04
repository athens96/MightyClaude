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
                var drawn = VisualChildren(dashboard).OfType<FrameworkElement>().ToArray();
                foreach (var agent in original.Sessions.Where(s => s.Kind == "claude"))
                    Require(drawn.Any(v => AutomationProperties.GetAutomationId(v) == "dashboard-provider-" + agent.Id && ProviderMarkView.LabelledProvider(v) == agent.Provider), "Dashboard agent rows must carry their provider marks.");
                foreach (var project in original.Workspaces)
                {
                    Require(drawn.OfType<TextBlock>().Any(v => v.Text == project.Path), "Dashboard workspace path is missing.");
                    Require(drawn.OfType<Button>().Any(v => AutomationProperties.GetAutomationId(v) == "dashboard-open-files-" + project.Id), "Dashboard workspace Files action is missing.");
                    var add = drawn.OfType<Button>().Single(v => AutomationProperties.GetAutomationId(v) == "dashboard-add-session-" + project.Id);
                    Require(add.Flyout is MenuFlyout menu && menu.Items.Count == AddPaneMenu.Entries().Count, "Dashboard Add Pane must offer the full shared menu.");
                }
                Require(dashboardGitLabels[workspace.Id].Text.Contains("feature/parity", StringComparison.Ordinal), "Dashboard must display its workspace Git status.");
                await SettleDesktopCapture(root);
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
                    if (category.Id == "styles")
                        await WaitUI(() => new[] { "ouroboros", "paperthin", "superpowers" }.All(id => VisualChildren(frame).OfType<StackPanel>().Any(row => row.IsLoaded && AutomationProperties.GetAutomationId(row) == "settings-style-" + id)));
                    if (category.Id == "companion")
                        await WaitUI(() => VisualChildren(frame).OfType<ComboBox>().Any(picker => AutomationProperties.GetAutomationId(picker) == "settings-companion-pet" && picker.Items.Count > 0 && picker.SelectedItem is not null)
                            && VisualChildren(frame).OfType<Image>().Any(preview => AutomationProperties.GetAutomationId(preview) == "settings-companion-preview" && preview.Source is BitmapImage { PixelWidth: > 0 }));
                    frame.UpdateLayout();
                    var headings = VisualChildren(frame).OfType<TextBlock>().Where(text => AutomationProperties.GetAutomationId(text).StartsWith("settings-section-", StringComparison.Ordinal)).Select(text => text.Text).ToArray();
                    var expected = category.Sections.Select(id => SettingsSections.Windows.Single(section => section.Id == id).WindowsTitle!).ToArray();
                    Require(headings.SequenceEqual(expected), "settings category contains missing or misplaced sections: " + category.Id);
                    Require(navigation.SelectedItems.Count == 1 && ReferenceEquals(navigation.SelectedItem, item), "Only the displayed settings category may be selected.");
                    categoryCount++;
                    if (category.Id is "general" or "styles" or "mobile" or "companion")
                    {
                        await SettleDesktopCapture(frame);
                        captures.Add(await CaptureElement(frame, Path.Combine(options.ProfileDirectory!, "smoke-settings-" + category.Id + "-" + theme + ".png")));
                    }
                }
                settingsWindow.Close(); await opening;
            }
            await CheckDashboardActions(first, original.Workspaces.Last());
            return new() { ["dashboardRetainsPane"] = true, ["clockPreservesDashboardControls"] = true, ["dashboardWorkspaceActions"] = true, ["dashboardProviderMarks"] = true, ["settingsLoadedBeforeCapture"] = true, ["allSettingsCategories"] = categoryCount == 16, ["bothThemes"] = true, ["screenshots"] = captures };
        }
        finally
        {
            settingsWindow?.Close(); HideDashboard(); dashboardGit.Remove(workspace.Id); companionPreferences = savedCompanion; await service.UpdateAsync(_ => original); Render();
        }
    }
    private async Task CheckDashboardActions(RunSession first, Workspace other)
    {
        await SelectLayoutSession(first.Id); showsDashboard = true; RenderDashboard(); root.UpdateLayout();
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
