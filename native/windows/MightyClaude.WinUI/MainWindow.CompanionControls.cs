using MightyClaude.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;
using Windows.Graphics.Imaging;

namespace MightyClaude.WinUI;

public sealed partial class MainWindow
{
    private Button? companionToggleControl, companionStatusControl;
    private Flyout? companionStatusFlyout;
    private StackPanel? companionStatusRows, companionStatusBody;
    private TextBlock? companionStatusCount, companionStatusHeading;
    private bool companionStatusVisible;
    private readonly Dictionary<string, (Button Button, TextBlock Heading, TextBlock Detail, TextBlock Prompt, TextBlock Activity)> companionStatusItems = [];

    private FrameworkElement BuildCompanionControls()
    {
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        companionToggleControl = Button("", () => Act(async () =>
        {
            companionPreferences = companionPreferences with { Enabled = !companionPreferences.Enabled };
            SaveCompanionPreferences(); await ReloadCompanionPets(); RefreshCompanionControls();
        }));
        companionStatusControl = new Button();
        controls.Children.Add(companionToggleControl); controls.Children.Add(companionStatusControl);
        var body = companionStatusBody = new StackPanel { Spacing = 10, Width = 350, RequestedTheme = root.RequestedTheme };
        companionStatusHeading = new TextBlock { FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold }; body.Children.Add(companionStatusHeading);
        companionStatusCount = new TextBlock { FontSize = 11, Opacity = .7 }; body.Children.Add(companionStatusCount);
        companionStatusRows = new StackPanel { Spacing = 7 };
        body.Children.Add(new ScrollViewer { Content = companionStatusRows, MaxHeight = 350 });
        companionStatusFlyout = new Flyout { Content = body };
        companionStatusFlyout.Opening += (_, _) => { companionStatusVisible = true; RefreshCompanionControls(); };
        companionStatusFlyout.Closed += (_, _) => companionStatusVisible = false;
        companionStatusControl.Flyout = companionStatusFlyout;
        RefreshCompanionControls(); return controls;
    }
    private string CompanionStatus(RunSession session) => companionPermissions.Values.Any(p => p.RunId == session.Id) || session.CurrentActivity?.State == "waiting" ? "waiting" : session.Status;
    private void RefreshCompanionControls()
    {
        if (companionToggleControl is null || companionStatusControl is null || closing) return;
        if (companionStatusBody is not null) companionStatusBody.RequestedTheme = root.RequestedTheme;
        if (companionStatusHeading is not null) companionStatusHeading.Text = Locale.Get("companion.status.title");
        var sessions = service.Snapshot.Sessions.Where(s => s.Kind == "claude").ToArray();
        var busy = sessions.Count(s => CompanionStatus(s) is "running" or "waiting" or "starting" or "queued");
        companionToggleControl.Content = companionPreferences.Enabled ? "🐾" : "♧";
        AutomationProperties.SetName(companionToggleControl, Locale.Get("companion.settings.enabled"));
        ToolTipService.SetToolTip(companionToggleControl, Locale.Get(companionPreferences.Enabled ? "menu.hidePet" : "companion.settings.enabled"));
        companionStatusControl.Content = "◉ " + busy;
        AutomationProperties.SetName(companionStatusControl, Locale.Get("companion.status.title"));
        ToolTipService.SetToolTip(companionStatusControl, Locale.Get("companion.status.title"));
        if (!companionStatusVisible || companionStatusRows is null || companionStatusCount is null) return;
        companionStatusCount.Text = Locale.Get("companion.status.busyCount", new Dictionary<string,string> { ["count"] = busy.ToString() });
        foreach (var stale in companionStatusItems.Keys.Where(id => sessions.All(s => s.Id != id)).ToArray())
        { companionStatusRows.Children.Remove(companionStatusItems[stale].Button); companionStatusItems.Remove(stale); }
        foreach (var session in sessions)
        {
            if (!companionStatusItems.TryGetValue(session.Id, out var row))
            {
                var content = new StackPanel { Spacing = 3 }; var heading = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
                var detail = new TextBlock { FontSize = 10, Opacity = .7, TextWrapping = TextWrapping.Wrap };
                var prompt = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, MaxLines = 2 };
                var activity = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, MaxLines = 2, Opacity = .7 };
                foreach (var text in new[] { heading, detail, prompt, activity }) content.Children.Add(text);
                var sessionId = session.Id;
                var button = Button("", () => { companionStatusFlyout?.Hide(); FocusSession(sessionId); return Task.CompletedTask; });
                button.Content = content; button.HorizontalAlignment = HorizontalAlignment.Stretch; button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                row = (button, heading, detail, prompt, activity); companionStatusItems[session.Id] = row; companionStatusRows.Children.Add(button);
            }
            row.Heading.Text = session.Title + " · " + StateLabel(CompanionStatus(session));
            row.Detail.Text = string.Join(" · ", new[] { service.Snapshot.Workspaces.FirstOrDefault(w => w.Id == session.WorkspaceId)?.Name, ProviderCatalog.Name(session.Provider), session.RunTiming?.Label() }.Where(s => !string.IsNullOrEmpty(s)));
            row.Prompt.Text = session.Logs.LastOrDefault(l => l.Kind == "user")?.Text ?? "";
            row.Activity.Text = session.CurrentActivity?.Summary ?? StateLabel(CompanionStatus(session));
            AutomationProperties.SetName(row.Button, row.Heading.Text + ", " + row.Detail.Text);
        }
        if (sessions.Length == 0)
        {
            companionStatusRows.Children.Clear(); companionStatusRows.Children.Add(new TextBlock { Text = Locale.Get("companion.status.empty"), TextWrapping = TextWrapping.Wrap, FontSize = 12 });
        }
        else
        {
            foreach (var empty in companionStatusRows.Children.OfType<TextBlock>().ToArray()) companionStatusRows.Children.Remove(empty);
            // Reorder only when the sidebar order changes; normal timer updates
            // retain each real button so keyboard focus stays in the popover.
            for (var index = 0; index < sessions.Length; index++)
            {
                var button = companionStatusItems[sessions[index].Id].Button;
                if (companionStatusRows.Children.IndexOf(button) == index) continue;
                companionStatusRows.Children.Remove(button); companionStatusRows.Children.Insert(index, button);
            }
        }
    }
    private static async Task<BitmapImage> CompanionPreview(CompanionPet pet)
    {
        using var input = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(input.GetOutputStreamAt(0))) { writer.WriteBytes(pet.Image); await writer.StoreAsync(); writer.DetachStream(); }
        input.Seek(0); var decoder = await BitmapDecoder.CreateAsync(input);
        var pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            new BitmapTransform { Bounds = new BitmapBounds { X = 0, Y = 0, Width = 192, Height = 208 } }, ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        using var output = new InMemoryRandomAccessStream(); var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, 192, 208, 96, 96, pixels.DetachPixelData()); await encoder.FlushAsync(); output.Seek(0);
        var bitmap = new BitmapImage(); await bitmap.SetSourceAsync(output); return bitmap;
    }
}
