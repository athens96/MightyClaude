using MightyClaude.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
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
    private readonly Dictionary<string, (Button Button, TextBlock Heading, TextBlock Detail, TextBlock Prompt, TextBlock Activity, TextBlock State, TextBlock Elapsed, StatusMark Mark, FrameworkElement PromptRow)> companionStatusItems = [];
    /// <summary>The status capsule (M/AgentCompanionViews.swift:14-18) and its parts: the waveform while agents work, else the 2×2 dots, and the count.</summary>
    private Border? companionStatusCapsule;
    private FrameworkElement? companionStatusBusy, companionStatusIdle;
    private readonly TextBlock companionStatusNumber = new() { FontSize = DesignMetrics.Type.Small, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
    /// <summary>The popover's "show the pet" switch and its words (M/AgentCompanionViews.swift:65).</summary>
    private ToggleButton? companionStatusPet;
    private TextBlock? companionStatusPetLabel, companionStatusEmpty;
    /// <summary>The agent status popover's width over all (its padding 18 is inside it), the spacing of its parts and the height its list scrolls at (M/AgentCompanionViews.swift:30, 62, 66).</summary>
    internal const double CompanionPopoverWidth = 350, CompanionPopoverSpacing = 14, CompanionPopoverListHeight = 330;
    /// <summary>
    /// The capsule's symbols are drawn in a 12-unit box, as the Mac's 10pt waveform.path and circle.grid.2x2
    /// are. The capsule is 21 high: the symbol's 13-high image in v4 (M/AgentCompanionViews.swift:15-17), the
    /// tallest part of the status bar, which is 37 high on every Mac screen (docs/design-system/screens, 74px at 2x).
    /// </summary>
    internal const double CompanionSymbol = 12, CompanionCapsuleHeight = 21;

    /// <summary>
    /// AgentStatusControls at the trailing end of the status bar (M/AgentCompanionViews.swift:5-25): the
    /// paw (accent and filled while the pet shows, <c>ink2</c> and outlined otherwise), 10 from the status
    /// capsule — its symbol and, while agents work, their count, padding h8 v4 on the subtle wash —
    /// which opens the agent status popover above it.
    /// </summary>
    private FrameworkElement BuildCompanionControls()
    {
        var ink = brushes.Brush(DesignToken.Ink2);
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        companionToggleControl = Button("", ToggleCompanionPet);
        InitializeCompanionGlyph();
        // waveform.path: one line swinging widest at its middle. circle.grid.2x2: four small rings.
        companionStatusBusy = new Polyline
        {
            Points = [new(0, 6), new(1.85, 6), new(2.3, 4.2), new(3.08, 8.75), new(4.06, 2.35), new(4.98, 11.1), new(5.94, 0.4), new(6.92, 11.1), new(7.85, 2.35), new(8.83, 8.75), new(9.78, 4.2), new(10.2, 6), new(12, 6)],
            // The Mac's line is 0.6 to 0.7 wide and its swings 0.9 apart (docs/design-system/screens at 2x): a heavier line closes them up.
            Stroke = ink, StrokeThickness = 0.7, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            Width = CompanionSymbol, Height = CompanionSymbol, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed,
        };
        var dots = new Canvas { Width = CompanionSymbol, Height = CompanionSymbol, VerticalAlignment = VerticalAlignment.Center };
        foreach (var (left, top) in new[] { (0.9, 0.9), (6.7, 0.9), (0.9, 6.7), (6.7, 6.7) })
        {
            var ring = new Ellipse { Width = 4.4, Height = 4.4, Stroke = ink, StrokeThickness = 1 };
            Canvas.SetLeft(ring, left); Canvas.SetTop(ring, top); dots.Children.Add(ring);
        }
        companionStatusIdle = dots;
        companionStatusNumber.Foreground = ink;
        Microsoft.UI.Xaml.Documents.Typography.SetNumeralAlignment(companionStatusNumber, FontNumeralAlignment.Tabular);
        var face = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
        face.Children.Add(companionStatusBusy); face.Children.Add(companionStatusIdle); face.Children.Add(companionStatusNumber);
        companionStatusCapsule = new Border { Child = face, Height = CompanionCapsuleHeight, Padding = new Thickness(8, 0, 8, 0), CornerRadius = new CornerRadius(CompanionCapsuleHeight / 2), Background = brushes.Subtle };
        companionStatusControl = new Button { Content = companionStatusCapsule };
        foreach (var button in new[] { companionToggleControl, companionStatusControl })
        {
            button.MinWidth = 0; button.MinHeight = 0; button.Padding = new Thickness(0); button.BorderThickness = new Thickness(0); button.VerticalAlignment = VerticalAlignment.Center;
            button.CornerRadius = new CornerRadius(CompanionCapsuleHeight / 2);
            PaintPlainButton(button, brushes.Transparent, brushes.Transparent);
            controls.Children.Add(button);
        }
        AutomationProperties.SetAutomationId(companionStatusControl, "companion-status");

        // AgentStatusPopover (M/AgentCompanionViews.swift:27-68): the title and the working count on one
        // line, the agents' cards, a line, then the pet switch; 350 wide with its padding 18, 14 apart.
        var body = companionStatusBody = new StackPanel { Spacing = CompanionPopoverSpacing, Width = CompanionPopoverWidth - 2 * CompanionPopoverPadding, Margin = new Thickness(CompanionPopoverPadding - PopoverPadding), RequestedTheme = root.RequestedTheme };
        var head = new Grid { ColumnSpacing = 8 };
        head.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); head.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        companionStatusHeading = new TextBlock { FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = brushes.Brush(DesignToken.Ink), TextTrimming = TextTrimming.CharacterEllipsis };
        head.Children.Add(companionStatusHeading);
        companionStatusCount = new TextBlock { FontSize = DesignMetrics.Type.Pill, Foreground = ink, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(companionStatusCount, 1); head.Children.Add(companionStatusCount);
        body.Children.Add(head);
        companionStatusEmpty = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = ink, Margin = new Thickness(0, 18, 0, 18), Visibility = Visibility.Collapsed };
        body.Children.Add(companionStatusEmpty);
        companionStatusRows = new StackPanel { Spacing = 8 };
        body.Children.Add(new ScrollViewer { Content = companionStatusRows, MaxHeight = CompanionPopoverListHeight, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        body.Children.Add(new Border { Height = DesignMetrics.Stroke.Line, Background = Separator });
        // A SwiftUI switch stands right after its words (M/AgentCompanionViews.swift:65), not across from them.
        var pet = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        companionStatusPetLabel = new TextBlock { FontSize = DesignMetrics.Type.Body, Foreground = brushes.Brush(DesignToken.Ink), VerticalAlignment = VerticalAlignment.Center };
        pet.Children.Add(companionStatusPetLabel);
        // The Mac's mini switch (M/AgentCompanionViews.swift:65), which is the form's: 26×15 with its 13pt knob, filled when off too.
        var toggle = companionStatusPet = SettingsSwitch(Locale.Get("companion.status.petSwitch"), companionPreferences.Enabled, "companion-status-pet");
        RoutedEventHandler switched = async (_, _) => { if ((toggle.IsChecked == true) != companionPreferences.Enabled) await ToggleCompanionPet(); };
        toggle.Checked += switched; toggle.Unchecked += switched;
        pet.Children.Add(toggle);
        body.Children.Add(pet);
        companionStatusFlyout = new Flyout { Content = body, Placement = FlyoutPlacementMode.Top, FlyoutPresenterStyle = CardFlyoutStyle };
        companionStatusFlyout.Opening += (_, _) => { companionStatusVisible = true; RefreshCompanionControls(); };
        companionStatusFlyout.Closed += (_, _) => companionStatusVisible = false;
        companionStatusControl.Flyout = companionStatusFlyout;
        RefreshCompanionControls(); return controls;
    }

    /// <summary>The agent status popover's own padding (M/AgentCompanionViews.swift:66), reached from the shared popover padding by a margin.</summary>
    internal const double CompanionPopoverPadding = 18;

    private Task ToggleCompanionPet() => Act(async () =>
    {
        companionPreferences = companionPreferences with { Enabled = !companionPreferences.Enabled };
        SaveCompanionPreferences();
        // Turned off, the pet goes and the paw says so at once; the catalog is read again behind them.
        if (!companionPreferences.Enabled) companionOverlay?.Show(false);
        RefreshCompanionControls(); await ReloadCompanionPets(); RefreshCompanionControls();
    });

    private string CompanionStatus(RunSession session) => companionPermissions.Values.Any(p => p.RunId == session.Id) || session.CurrentActivity?.State == "waiting" ? "waiting" : session.Status;
    private void RefreshCompanionControls()
    {
        if (companionToggleControl is null || companionStatusControl is null || closing) return;
        if (companionStatusBody is not null) companionStatusBody.RequestedTheme = root.RequestedTheme;
        if (companionStatusHeading is not null) companionStatusHeading.Text = Locale.Get("companion.status.title");
        var sessions = service.Snapshot.Sessions.Where(s => s.Kind == "claude").ToArray();
        var busy = sessions.Count(s => CompanionStatus(s) is "running" or "waiting" or "starting" or "queued");
        PaintCompanionGlyph(companionPreferences.Enabled);
        // The Mac's words (M/AgentCompanionViews.swift:12): "Hide pet" or "Show pet" as the help, "Toggle pet" as the name.
        AutomationProperties.SetName(companionToggleControl, Locale.Get("companion.status.togglePet"));
        ToolTipService.SetToolTip(companionToggleControl, Locale.Get(companionPreferences.Enabled ? "menu.hidePet" : "companion.status.showPet"));
        // The count shows only while something works, beside the waveform; at rest the dots stand alone.
        var busyText = busy.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (companionStatusNumber.Text != busyText) companionStatusNumber.Text = busyText;
        companionStatusNumber.Visibility = busy > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (companionStatusBusy is not null) companionStatusBusy.Visibility = busy > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (companionStatusIdle is not null) companionStatusIdle.Visibility = busy > 0 ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetName(companionStatusControl, Locale.Get("companion.status.title") + " · " + Locale.Get("companion.status.busyCount", new Dictionary<string,string> { ["count"] = busyText }));
        ToolTipService.SetToolTip(companionStatusControl, Locale.Get("companion.status.title"));
        if (!companionStatusVisible || companionStatusRows is null || companionStatusCount is null) return;
        companionStatusCount.Text = Locale.Get("companion.status.busyCount", new Dictionary<string,string> { ["count"] = busyText });
        if (companionStatusPetLabel is not null) companionStatusPetLabel.Text = Locale.Get("companion.status.petSwitch");
        if (companionStatusPet is not null)
        {
            if ((companionStatusPet.IsChecked == true) != companionPreferences.Enabled) companionStatusPet.IsChecked = companionPreferences.Enabled;
            AutomationProperties.SetName(companionStatusPet, Locale.Get("companion.status.petSwitch"));
        }
        foreach (var stale in companionStatusItems.Keys.Where(id => sessions.All(s => s.Id != id)).ToArray())
        { companionStatusRows.Children.Remove(companionStatusItems[stale].Button); companionStatusItems.Remove(stale); }
        var dark = service.Snapshot.Theme != "light";
        foreach (var session in sessions)
        {
            if (!companionStatusItems.TryGetValue(session.Id, out var row)) { row = BuildCompanionStatusRow(session.Id); companionStatusItems[session.Id] = row; companionStatusRows.Children.Add(row.Button); }
            var state = CompanionStatus(session);
            row.Mark.Update(state, session.Kind, 0, dark);
            row.Heading.Text = session.Title; row.State.Text = StateLabel(state);
            row.Detail.Text = service.Snapshot.Workspaces.FirstOrDefault(w => w.Id == session.WorkspaceId)?.Name ?? "";
            row.Elapsed.Text = session.RunTiming?.Label() ?? ""; row.Elapsed.Visibility = row.Elapsed.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            row.Prompt.Text = Wire.Clean(session.Logs.LastOrDefault(l => l.Kind == "user")?.Text ?? "", 400);
            row.PromptRow.Visibility = row.Prompt.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            row.Activity.Text = Wire.Clean(session.CurrentActivity?.Summary ?? StateLabel(state), 400);
            AutomationProperties.SetName(row.Button, string.Join(". ", new[] { row.Heading.Text + " · " + row.State.Text, string.Join(" · ", new[] { row.Detail.Text, ProviderCatalog.Name(session.Provider), row.Elapsed.Text }.Where(text => text.Length > 0)), row.Prompt.Text, row.Activity.Text }.Where(text => text.Length > 0)));
        }
        if (companionStatusEmpty is not null)
        {
            companionStatusEmpty.Text = Locale.Get("companion.status.empty");
            companionStatusEmpty.Visibility = sessions.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        // Reorder only when the sidebar order changes; normal timer updates
        // retain each real button so keyboard focus stays in the popover.
        for (var index = 0; index < sessions.Length; index++)
        {
            var button = companionStatusItems[sessions[index].Id].Button;
            if (companionStatusRows.Children.IndexOf(button) == index) continue;
            companionStatusRows.Children.Remove(button); companionStatusRows.Children.Insert(index, button);
        }
    }

    /// <summary>
    /// One agent's card in the popover (M/AgentCompanionViews.swift:42-59): its status mark, the title
    /// 12 medium with the state 10 <c>ink2</c> across from it, the workspace and the run clock 10
    /// <c>ink2</c>, the request 11 behind an arrow, the current work 11 <c>ink2</c> and a 9pt open arrow in
    /// the tertiary ink (:57); padding 10 on the subtle wash at radius 10. Pressing it opens the pane.
    /// </summary>
    private (Button Button, TextBlock Heading, TextBlock Detail, TextBlock Prompt, TextBlock Activity, TextBlock State, TextBlock Elapsed, StatusMark Mark, FrameworkElement PromptRow) BuildCompanionStatusRow(string sessionId)
    {
        var ink = brushes.Brush(DesignToken.Ink); var ink2 = brushes.Brush(DesignToken.Ink2);
        var mark = new StatusMark(); mark.View.VerticalAlignment = VerticalAlignment.Top; mark.View.Margin = new Thickness(0, 2, 0, 0);
        var heading = new TextBlock { FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.Medium, Foreground = ink, TextTrimming = TextTrimming.CharacterEllipsis };
        var state = new TextBlock { FontSize = DesignMetrics.Type.Small, Foreground = ink2, VerticalAlignment = VerticalAlignment.Center };
        var detail = new TextBlock { FontSize = DesignMetrics.Type.Small, Foreground = ink2, TextTrimming = TextTrimming.CharacterEllipsis };
        var elapsed = new TextBlock { FontSize = DesignMetrics.Type.Small, FontFamily = new FontFamily(DesignMetrics.Font.Mono), Foreground = ink2, VerticalAlignment = VerticalAlignment.Center };
        var prompt = new TextBlock { FontSize = DesignMetrics.Type.Pill, Foreground = ink, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis };
        var activity = new TextBlock { FontSize = DesignMetrics.Type.Pill, Foreground = ink2, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid Across(FrameworkElement leading, FrameworkElement trailing)
        {
            var line = new Grid { ColumnSpacing = 8 };
            line.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); line.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            line.Children.Add(leading); Grid.SetColumn(trailing, 1); line.Children.Add(trailing);
            return line;
        }
        // arrow.up.right and arrow.up.forward: a plain arrow to the upper right, drawn on a 10-unit box.
        Grid Arrow(double size, Brush stroke)
        {
            var arrow = new Grid { Width = size, Height = size, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) };
            var unit = size / 10;
            foreach (var points in new[] { new[] { (1.6, 8.4), (8.2, 1.8) }, new[] { (3.2, 1.8), (8.2, 1.8), (8.2, 6.8) } })
            {
                var line = new Polyline { Stroke = stroke, StrokeThickness = 1.1 * unit, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
                foreach (var (x, y) in points) line.Points.Add(new(x * unit, y * unit));
                arrow.Children.Add(line);
            }
            AutomationProperties.SetAccessibilityView(arrow, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
            return arrow;
        }
        var promptRow = new Grid { ColumnSpacing = 6 };
        promptRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); promptRow.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        promptRow.Children.Add(Arrow(10, ink));
        Grid.SetColumn(prompt, 1); promptRow.Children.Add(prompt);
        var words = new StackPanel { Spacing = 4 };
        words.Children.Add(Across(heading, state)); words.Children.Add(Across(detail, elapsed)); words.Children.Add(promptRow); words.Children.Add(activity);
        var content = new Grid { ColumnSpacing = 10 };
        content.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); content.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); content.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        content.Children.Add(mark.View); Grid.SetColumn(words, 1); content.Children.Add(words);
        var open = Arrow(9, brushes.Tertiary);
        Grid.SetColumn(open, 2); content.Children.Add(open);
        var button = Button("", () => { companionStatusFlyout?.Hide(); FocusSession(sessionId); return Task.CompletedTask; });
        button.Content = content; button.HorizontalAlignment = HorizontalAlignment.Stretch; button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        button.Padding = new Thickness(10); button.BorderThickness = new Thickness(0); button.CornerRadius = new CornerRadius(DesignMetrics.Radius.Entry);
        PaintPlainButton(button, brushes.Subtle, brushes.Subtle, ink: ink);
        return (button, heading, detail, prompt, activity, state, elapsed, mark, promptRow);
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
