using MightyClaude.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
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
        try
        {
            foreach (var theme in new[] { "dark", "light" })
            {
                await service.UpdateAsync(state => state with { Theme = theme }); Render();
                showsDashboard = true; RenderDashboard();
                await WaitUI(() => dashboard?.Visibility == Visibility.Visible && dashboard.ActualWidth > 0 && panes.Visibility == Visibility.Collapsed);
                var dashboardView = dashboard!.Content;
                RefreshRunningIndicators();
                Require(ReferenceEquals(dashboardView, dashboard.Content), "clock ticks must preserve dashboard controls");
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
                    frame.UpdateLayout();
                    var headings = VisualChildren(frame).OfType<TextBlock>().Where(text => AutomationProperties.GetAutomationId(text).StartsWith("settings-section-", StringComparison.Ordinal)).Select(text => text.Text).ToArray();
                    var expected = category.Sections.Select(id => SettingsSections.Windows.Single(section => section.Id == id).WindowsTitle!).ToArray();
                    Require(headings.SequenceEqual(expected), "settings category contains missing or misplaced sections: " + category.Id);
                    categoryCount++;
                    if (category.Id is "general" or "styles" or "mobile" or "companion")
                        captures.Add(await CaptureElement(frame, Path.Combine(options.ProfileDirectory!, "smoke-settings-" + category.Id + "-" + theme + ".png")));
                }
                settingsWindow.Close(); await opening;
            }
            return new() { ["dashboardRetainsPane"] = true, ["clockPreservesDashboardControls"] = true, ["allSettingsCategories"] = categoryCount == 16, ["bothThemes"] = true, ["screenshots"] = captures };
        }
        finally
        {
            settingsWindow?.Close(); HideDashboard(); await service.UpdateAsync(_ => original); Render();
        }
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
