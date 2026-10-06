using MightyClaude.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace MightyClaude.WinUI;

// Design stage 6 (.omc/plans/windows-design-conversion.md): the dashboard, the files pane, settings,
// the sheets and the popovers. The light pass opens each of them on fixture data and reads its colours
// and geometry off the element tree; the toggle to dark follows with the dashboard, the files pane and
// the settings window still open, and the dark pass reads the same instances again, so every colour it
// sees was recoloured in place. Every failure names the token, the expected hex and the actual one.
public sealed partial class MainWindow
{
    private const string PanelsDesignKey = "panelsDesign";

    /// <summary>The instances the light pass checked, which the dark pass must find again.</summary>
    private sealed record PanelsDesignViews(UIElement Dashboard, DashboardParts Parts, FrameworkElement SettingsFrame, ContentDialog Sheet, Grid FilesHead, Brush Keyword, Style Popover);

    private async Task<Dictionary<string, object?>> RunPanelsDesignSmoke(Workspace workspace)
    {
        var original = service.Snapshot;
        var previousTree = EffectiveLayout(original, workspace.Id); var previousMode = LayoutMode(original, workspace.Id);
        var filesPane = FilePaneKind.PaneId(workspace.Id);
        Task? settingsOpening = null;
        var checks = new Dictionary<string, object?>();
        try
        {
            // Two usage windows per Claude pane, one past 90% so a bar draws waitText.
            var now = DateTimeOffset.UtcNow;
            await service.UpdateAsync(s => s with
            {
                Theme = "light", ActiveWorkspaceId = workspace.Id, ClaudeDirectUsageLookupEnabled = false,
                Sessions = s.Sessions.Select(pane => pane.Kind != "shell" && pane.Provider == "claude"
                    ? pane with { SessionUsage = (pane.SessionUsage ?? new SessionUsage { Provider = "claude", Source = "smoke.fixture" }) with { RateLimits = [new("five_hour", 24, now.AddHours(3).ToString("O")), new("seven_day", 95)], RateLimitsUpdatedAt = now.ToString("O") } }
                    : pane).ToList(),
            });
            Render();
            var agent = service.Snapshot.ActiveSessionId is { } active && views.TryGetValue(active, out var activeView) && activeView.SessionForSmoke.Kind == "claude" ? activeView
                : throw new InvalidOperationException($"{PanelsDesignKey}: needs the active smoke pane to be an agent pane; active {service.Snapshot.ActiveSessionId ?? "none"}");
            await ApplyLayoutPreset("tabs");
            await WaitUI(() => agent.Container.IsLoaded, () => $"{PanelsDesignKey}: the agent pane never loaded");

            var popover = await RequirePopoversInTheme(agent, null);
            var sheet = ResumeChoiceDialog("claude");
            await RequireSheetInTheme(sheet);
            // A file no preview reads, for the "can't be previewed" card, and one whose name the tree has no room for.
            await File.WriteAllBytesAsync(Path.Combine(workspace.Path, PanelsUnsupportedFile), [0, 1, 2, 0, 255, 254, 0, 3]);
            await File.WriteAllTextAsync(Path.Combine(workspace.Path, PanelsLongName), "long name\n");
            await WritePanelsPicture(Path.Combine(workspace.Path, PanelsPicture));
            await OpenFilePane(workspace.Id);
            await WaitUI(() => views.TryGetValue(filesPane, out var opened) && opened.FilesHost is { IsLoaded: true }, () => $"{PanelsDesignKey}: the files pane never loaded");
            var files = views[filesPane];
            await WaitUI(() => files.FilesTree.Children.ContainsKey(""), () => $"{PanelsDesignKey}: the files tree never listed the workspace");
            // The workspace's tree was listed by an earlier stage; read it again for the file just written.
            await files.FilesSmokeRefresh();
            await WaitUI(() => files.FilesTree.Children[""].Any(e => e.Name == PanelsUnsupportedFile), () => $"{PanelsDesignKey}: the refreshed files tree never listed {PanelsUnsupportedFile}");
            files.FilesSmokeShowPlaceholder();
            await CapturePanels("files-empty");
            await files.FilesSmokeOpenFolder("Sources");
            await WaitUI(() => files.FilesTree.Children.ContainsKey("Sources"), () => $"{PanelsDesignKey}: the Sources folder never opened");
            await CaptureFilesPreview(files, "README.md", "files-markdown");
            // Select the row first: its debounced preview is then cancelled by the immediate one, so
            // nothing redraws the source after the light pass read its runs.
            await files.FilesSmokeSelect("Sources/App.swift");
            await files.FilesSmokePreview("Sources/App.swift");
            var (filesHead, keyword) = await files.RequireFilesDesignInTheme(null);
            await files.RequireFilesNameCutInMiddle(PanelsLongName);
            await CapturePanels("files-source");
            files.RequireFilesSplitDrags();
            var (dashboardContent, parts) = await RequireDashboardDesignInTheme(null);
            await CapturePanels("dashboard");
            settingsOpening = ShowCategorizedSettingsAsync();
            var frame = await RequireSettingsDesignInTheme(null);
            var light = new PanelsDesignViews(dashboardContent, parts, frame, sheet, filesHead, keyword, popover);

            await service.UpdateAsync(s => s with { Theme = "dark" }); Render();
            Require(ReferenceEquals(views[filesPane], files) && ReferenceEquals(views[agent.SessionId], agent), $"{PanelsDesignKey} (dark): the toggle rebuilt a pane instead of reusing it");
            await RequireSettingsDesignInTheme(light.SettingsFrame);
            settingsWindow?.Close(); await settingsOpening; settingsOpening = null;
            await RequireDashboardDesignInTheme(light);
            await CapturePanels("dashboard");
            HideDashboard(); root.UpdateLayout();
            await files.RequireFilesDesignInTheme(light);
            await CapturePanels("files-source");
            await CaptureFilesPreview(files, "README.md", "files-markdown");
            await CaptureFilesPreview(files, PanelsPicture, "files-image");
            await CaptureFilesPreview(files, PanelsUnsupportedFile, "files-unsupported");
            // A name filter lists the matches flat, each with its path after it.
            await files.FilesSmokeFilter("app");
            await CapturePanels("files-filter");
            await files.FilesSmokeFilter("");
            files.FilesSmokeShowPlaceholder();
            await CapturePanels("files-empty");
            await RequireSheetInTheme(light.Sheet);
            await SelectLayoutSession(agent.SessionId);
            await WaitUI(() => agent.Container.IsLoaded, () => $"{PanelsDesignKey} (dark): the agent pane never loaded again");
            await RequirePopoversInTheme(agent, light.Popover);
            checks["dashboard"] = true; checks["filesPane"] = true; checks["settings"] = true; checks["sheet"] = true; checks["popovers"] = true; checks["bothThemes"] = true;
            return checks;
        }
        finally
        {
            if (settingsOpening is not null) { settingsWindow?.Close(); await settingsOpening; }
            dashboardHeldForSmoke = false; HideDashboard();
            if (service.Snapshot.Sessions.Any(s => s.Id == filesPane)) await CloseSession(filesPane);
            foreach (var fixture in new[] { PanelsUnsupportedFile, PanelsLongName, PanelsPicture }) File.Delete(Path.Combine(workspace.Path, fixture));
            await service.UpdateAsync(s => SaveLayoutMode(SaveLayout(s with { Theme = original.Theme, Sessions = s.Sessions.Select(p => original.Sessions.FirstOrDefault(o => o.Id == p.Id) ?? p).ToList(), ClaudeDirectUsageLookupEnabled = original.ClaudeDirectUsageLookupEnabled }, workspace.Id, previousTree), workspace.Id, previousMode));
            usage?.SetDirectClaudeLookup(original.ClaudeDirectUsageLookupEnabled);
            Render();
            if (original.ActiveSessionId is { } previous && service.Snapshot.Sessions.Any(s => s.Id == previous)) await SelectLayoutSession(previous);
        }
    }

    /// <summary>The fixture file the files pane cannot preview (bytes that are no text and no image), one with a name wider than the tree, and a picture that shows on the page.</summary>
    private const string PanelsUnsupportedFile = "data.bin", PanelsLongName = "a-file-whose-name-is-far-too-long-for-the-tree-of-the-files-pane-at-the-width-it-opens-with.txt", PanelsPicture = "picture.png";

    /// <summary>A 64 × 48 picture in the accent colour, so the capture shows where an image lies (the files stage's own image is 40 × 30 clear pixels).</summary>
    private async Task WritePanelsPicture(string path)
    {
        const int width = 64, height = 48;
        var ink = brushes.Brush(DesignToken.Accent).Color; var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4) { pixels[i] = ink.B; pixels[i + 1] = ink.G; pixels[i + 2] = ink.R; pixels[i + 3] = byte.MaxValue; }
        using var png = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, png);
        encoder.SetPixelData(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied, width, height, 96, 96, pixels); await encoder.FlushAsync();
        using var reader = new Windows.Storage.Streams.DataReader(png.GetInputStreamAt(0)); await reader.LoadAsync((uint)png.Size);
        var bytes = new byte[(int)png.Size]; reader.ReadBytes(bytes);
        await File.WriteAllBytesAsync(path, bytes);
    }

    /// <summary>The window as it stands, written to <c>smoke-panels-{name}-{theme}.png</c> for the comparison with the Mac's pictures.</summary>
    private async Task CapturePanels(string name)
    {
        root.UpdateLayout(); await Task.Delay(180); root.UpdateLayout();
        await CaptureSmoke(Path.Combine(options.ProfileDirectory!, $"smoke-panels-{name}-{SmokeTheme}.png"));
    }

    /// <summary>Previews one fixture file in the files pane and captures the window once it shows.</summary>
    private async Task CaptureFilesPreview(PaneView files, string path, string name)
    {
        await files.FilesSmokeSelect(path);
        await files.FilesSmokePreview(path);
        await WaitUI(() => files.FilesShown?.RelativePath == path, () => $"{PanelsDesignKey}: the files pane never previewed {path}");
        await CapturePanels(name);
    }

    /// <summary>A brush is the very shared instance it should be, named for the message.</summary>
    private void RequireSame(Brush? actual, Brush expected, string what)
    {
        Require(ReferenceEquals(actual, expected), $"{PanelsDesignKey} ({SmokeTheme}): {what} must be the shared {Describe(expected)} brush; got {Describe(actual)}{(actual is null ? "" : " from another brush")}");
    }

    private static FrameworkElement? Ancestor(DependencyObject start, Func<DependencyObject, bool> match)
    {
        for (var node = VisualTreeHelper.GetParent(start); node is not null; node = VisualTreeHelper.GetParent(node))
            if (match(node)) return node as FrameworkElement;
        return null;
    }

    /// <summary>
    /// The dashboard (M/DashboardView.swift:94-338): the 29pt heading-font title, the three 92-high
    /// radius-18 tiles on run / wait / card with their inks and 0.05 shadows, the usage card's 6pt bars,
    /// a workspace's rows on one card with the subtle wash under the pointer, its 26-high capsules, and
    /// the sidebar's selected work-status entry with its 0.06 shadow.
    /// </summary>
    private async Task<(UIElement Content, DashboardParts Parts)> RequireDashboardDesignInTheme(PanelsDesignViews? light)
    {
        const string key = PanelsDesignKey; var theme = SmokeTheme;
        showsDashboard = true; RefreshDashboardEntry(); RenderDashboard(); root.UpdateLayout();
        await WaitUI(() => dashboardParts is { } p && p.Running.IsLoaded && p.Usage is { IsLoaded: true } && p.Rows is { IsLoaded: true } && p.FirstRow is { IsLoaded: true },
            () => $"{key} ({theme}): the dashboard's tiles, usage card and rows never loaded (usage card {(dashboardParts?.Usage is null ? "missing" : "present")})");
        // From the light pass's first draw on, the drawn dashboard is kept, so a pane changing status
        // cannot swap a row or capsule out from under the waits below, nor the views under the dark pass.
        dashboardHeldForSmoke = true;
        var parts = dashboardParts!; var content = (UIElement)dashboard!.Content;
        if (light is not null)
            Require(ReferenceEquals(content, light.Dashboard) && ReferenceEquals(parts, light.Parts),
                $"{key} ({theme}): the toggle rebuilt the dashboard instead of recolouring it in place");
        RequireFont(parts.Title, DesignMetrics.Type.DashTitle, Microsoft.UI.Text.FontWeights.Bold, $"({theme}) the dashboard title", key);
        Require(parts.Title.FontFamily?.Source == DesignMetrics.Font.Heading, $"{key} ({theme}): the dashboard title must be Font.Heading '{DesignMetrics.Font.Heading}'; got '{parts.Title.FontFamily?.Source}'");
        RequireBrush(parts.Title, e => ((TextBlock)e).Foreground, DesignToken.Ink, "the dashboard title", key: key);
        foreach (var (tile, host, fill, number, ink, name) in new[]
        {
            (parts.Running, parts.TileHosts[0], DesignToken.Run, DesignToken.OnStatus, DesignToken.OnStatus, "the running tile"),
            (parts.Waiting, parts.TileHosts[1], DesignToken.Wait, DesignToken.OnWait, DesignToken.OnWait, "the waiting tile"),
            (parts.Done, parts.TileHosts[2], DesignToken.Card, DesignToken.DoneText, DesignToken.Ink2, "the done tile"),
        })
        {
            RequireRadius(tile, DesignMetrics.Radius.Tile, $"{name} (Radius.Tile)", key);
            Require(tile.Height == DashboardTileHeight && tile.Padding == new Thickness(16, 12, 16, 13), $"{key} ({theme}): {name} must be {DashboardTileHeight} high with padding h16 t12 b13; got {tile.Height}, {tile.Padding}");
            RequireBrush(tile, e => ((Border)e).Background, fill, $"{name}'s fill", key: key);
            var body = (Grid)tile.Child; var figure = (TextBlock)body.Children[0]; var label = (TextBlock)body.Children[1];
            Require(figure.FontSize == DesignMetrics.Type.Tile && figure.FontFamily?.Source == DesignMetrics.Font.Heading && label.FontSize == DesignMetrics.Type.SideRow,
                $"{key} ({theme}): {name} must show its number in Font.Heading at {DesignMetrics.Type.Tile}pt over a {DesignMetrics.Type.SideRow}pt label; got {figure.FontSize}pt '{figure.FontFamily?.Source}', {label.FontSize}pt");
            RequireBrush(figure, e => ((TextBlock)e).Foreground, number, $"{name}'s number", key: key);
            RequireBrush(label, e => ((TextBlock)e).Foreground, ink, $"{name}'s label", key: key);
            RequireCardShadow(host, CardShadow.DashboardCard, name, key);
        }
        var usageCard = parts.Usage!;
        RequireRadius(usageCard, DesignMetrics.Radius.Tile, "the usage card (Radius.Tile)", key);
        RequireBrush(usageCard, e => ((Border)e).Background, DesignToken.Card, "the usage card", key: key);
        Require(usageCard.Padding == new Thickness(16, 11, 16, 11) && usageCard.MinHeight == DashboardTileHeight, $"{key} ({theme}): the usage card must have padding h16 v11 and be at least {DashboardTileHeight} high; got {usageCard.Padding}, {usageCard.MinHeight}");
        // Beside the usage card a tile stops at 220 and the card takes what the three leave (M/DashboardView.swift:144, 180);
        // on a page too narrow for that the tiles give way first, down to their 110.
        var tilesRow = (Grid)VisualTreeHelper.GetParent(parts.TileHosts[0]); var usageHost = (FrameworkElement)VisualTreeHelper.GetParent(usageCard);
        var tileWidth = Math.Clamp((tilesRow.ActualWidth - tilesRow.ColumnSpacing * parts.TileHosts.Count - DashboardUsageMinWidth) / parts.TileHosts.Count, DashboardTileMinWidth, DashboardTileMaxWidth);
        Require(tilesRow.ColumnSpacing == 12 && parts.TileHosts.All(h => Math.Abs(h.ActualWidth - tileWidth) <= 1) && Math.Abs(usageHost.ActualWidth - (tilesRow.ActualWidth - parts.TileHosts.Count * (tileWidth + tilesRow.ColumnSpacing))) <= 1,
            $"{key} ({theme}): in a {tilesRow.ActualWidth:F0}-wide row the tiles must be {tileWidth:F0} wide, 12 apart, and the usage card take the rest; got tiles {string.Join(", ", parts.TileHosts.Select(h => h.ActualWidth.ToString("F0")))}, card {usageHost.ActualWidth:F0}");
        // The chips' leading windows (session, then weekly, never the spend limit), read from Core as the card was.
        var rows = (usage?.LeadingBars() ?? []).SelectMany(c => c.Bars).ToList();
        Require(parts.UsageBars.Count == rows.Count && rows.Count > 0 && rows.Any(r => r.Warning), $"{key} ({theme}): the usage card must draw one bar per leading window, one of them past 90%; got {parts.UsageBars.Count} bars for {rows.Count} windows");
        for (var i = 0; i < rows.Count; i++) RequireUsageBar(parts.UsageBars[i], rows[i].Fraction, rows[i].Warning, $"usage bar {i} ({rows[i].Label} {rows[i].Percent})");
        var group = parts.Rows!;
        RequireRadius(group, DesignMetrics.Radius.Tile, "a workspace's rows card (Radius.Tile)", key);
        // The Mac's group is a filled, shadowed shape with no stroke (M/DashboardView.swift:249).
        RequireThickness(group, 0, "a workspace's rows card (no edge)", key);
        RequireBrush(group, e => ((Border)e).Background, DesignToken.Card, "a workspace's rows card", key: key);
        RequireCardShadow((Grid)VisualTreeHelper.GetParent(group), CardShadow.DashboardCard, "a workspace's rows card", key);
        var row = parts.FirstRow!;
        Require(row.Padding == new Thickness(14, 11, 14, 11), $"{key} ({theme}): a dashboard row's padding must be h14 v11; got {row.Padding}");
        RequireSubtle(await StateBackground(row, "PointerOver", brushes.Subtle, key), "a dashboard row's PointerOver key frame", key);
        RequireClear(OwnResource(row, "ButtonBackground") as Brush, "a dashboard row at rest", key);
        foreach (var (capsule, fill, hover, ink, name) in new[] { (parts.Files!, DesignToken.Card, DesignToken.CardRaised, DesignToken.Ink, "the files capsule"), (parts.Add!, DesignToken.Ink, DesignToken.Ink2, DesignToken.Card, "the add-pane capsule") })
        {
            Require(capsule.Height == DashboardCapsuleHeight && capsule.CornerRadius == new CornerRadius(DashboardCapsuleHeight / 2), $"{key} ({theme}): {name} must be a {DashboardCapsuleHeight}-high capsule; got {capsule.Height}, {capsule.CornerRadius}");
            RequireBrush(capsule, e => OwnResource(e, "ButtonBackground") as Brush, fill, $"{name}'s fill", key: key);
            RequireBrush(capsule, e => OwnResource(e, "ButtonForeground") as Brush, ink, $"{name}'s words", key: key);
            var frame = await StateBackground(capsule, "PointerOver", brushes.Brush(hover), key);
            RequireBrush(capsule, _ => frame, hover, $"{name}'s PointerOver key frame", key: key);
        }
        // The group head's parts are 10 apart (M/DashboardView.swift:208), the capsules among them.
        Require(VisualTreeHelper.GetParent(parts.Files!) is StackPanel { Spacing: 10 } actions && ReferenceEquals(VisualTreeHelper.GetParent(parts.Add!), actions) && actions.Children.IndexOf(parts.Files) < actions.Children.IndexOf(parts.Add),
            $"{key} ({theme}): the files capsule must stand 10 before the add-pane capsule");
        // On one line the head is as high as its capsules, and the rows' card stands the group's 10 under it (M/DashboardView.swift:207, 218).
        var underHead = parts.Rows!.TransformToVisual(parts.Files!).TransformPoint(new Windows.Foundation.Point()).Y - parts.Files!.ActualHeight;
        Require(Math.Abs(underHead - 10) < 0.6, $"{key} ({theme}): a group's card of rows must stand 10 under its head's capsules; got {underHead:F1}");
        // The pulse dot (M/DashboardView.swift:340-360) at rest: a 7 run dot over a 13 halo of run at 0.22, in a 14 frame.
        var pulse = PulseDot(brushes, animated: false); var discs = pulse.Children.OfType<Microsoft.UI.Xaml.Shapes.Ellipse>().ToList();
        Require(pulse is { Width: 14, Height: 14 } && discs.Count == 2 && discs[0] is { Width: 13, Height: 13 } && discs[1] is { Width: 7, Height: 7 },
            $"{key} ({theme}): the pulse dot must be a 7 dot over a 13 halo in a 14 frame; got {pulse.Width} × {pulse.Height} with discs {string.Join(", ", discs.Select(d => d.Width))}");
        RequireBrush(discs[0], e => ((Microsoft.UI.Xaml.Shapes.Ellipse)e).Fill, DesignToken.Run, "the pulse dot's halo", 0.22, key);
        RequireBrush(discs[1], e => ((Microsoft.UI.Xaml.Shapes.Ellipse)e).Fill, DesignToken.Run, "the pulse dot", key: key);
        await CaptureLaidOver(new Border { Child = pulse, Padding = new Thickness(8), Background = brushes.Brush(DesignToken.Card) }, $"pulse-dot-{theme}");
        // The sidebar's work-status entry while the dashboard shows: card with the 0.06 shadow.
        RequireBrush(dashboardEntry!, e => ((Border)e).Background, DesignToken.Card, "the selected work-status entry", key: key);
        Require(dashboardEntryShadow is { Visibility: Visibility.Visible }, $"{key} ({theme}): the selected work-status entry must show its shadow caster");
        RequireCardShadow(dashboardEntryHost!, CardShadow.SelectedEntry, "the selected work-status entry", key);
        // The caster lies under the button, as large as it: inside the button its rounded outline would clip the shadow's rim away.
        var entryCaster = dashboardEntryShadow!; var entryButton = dashboardButton!;
        Require(dashboardEntryHost!.Children.Count == 2 && ReferenceEquals(dashboardEntryHost.Children[0], entryCaster) && ReferenceEquals(dashboardEntryHost.Children[1], entryButton)
            && Math.Abs(entryCaster.ActualWidth - entryButton.ActualWidth) < 0.6 && Math.Abs(entryCaster.ActualHeight - entryButton.ActualHeight) < 0.6 && entryCaster.ActualHeight > 0,
            $"{key} ({theme}): the work-status entry's shadow caster must lie under its button, as large as it; got {dashboardEntryHost.Children.Count} parts, caster {entryCaster.ActualWidth:F1} × {entryCaster.ActualHeight:F1}, button {entryButton.ActualWidth:F1} × {entryButton.ActualHeight:F1}");
        return (content, parts);
    }

    /// <summary>A card's caster (its host's first child) casts the Mac's black shadow at <paramref name="opacity"/>, blur 1, one point down.</summary>
    private void RequireCardShadow(Grid host, double opacity, string what, string key)
    {
        var caster = host.Children.OfType<Microsoft.UI.Xaml.Shapes.Rectangle>().FirstOrDefault();
        var shadow = caster is null ? null : CardShadow.Of(caster);
        Require(shadow is not null && Math.Abs(shadow.Opacity - opacity) < 0.001 && shadow.BlurRadius == 1 && shadow.Offset.Y == 1 && shadow.Color.A == 255 && shadow.Color.R == 0 && shadow.Color.G == 0 && shadow.Color.B == 0,
            $"{key} ({SmokeTheme}): {what} must cast a black shadow at {opacity}, blur 1, y 1; got {(shadow is null ? "none" : $"{Hex(shadow.Color)} at {shadow.Opacity}, blur {shadow.BlurRadius}, y {shadow.Offset.Y}")}");
    }

    /// <summary>A usage bar: a 6pt runSoft capsule filled to its fraction in run, or waitText past 90%; the popover's is accent on the neutral progress track (M/StatusBarUsage.swift:259).</summary>
    private void RequireUsageBar(Grid track, double fraction, bool warning, string what, bool progress = false)
    {
        const string key = PanelsDesignKey; var theme = SmokeTheme;
        var fill = (Border)track.Children[0];
        Require(track.Height == UsageBarHeight && fill.Height == UsageBarHeight && track.CornerRadius == new CornerRadius(UsageBarHeight / 2),
            $"{key} ({theme}): {what} must be a {UsageBarHeight}pt capsule; got track {track.Height} at {track.CornerRadius}, fill {fill.Height}");
        if (progress) RequireBrush(track, e => ((Grid)e).Background, DesignToken.Ink, $"{what}'s track", ProgressTrackOpacity, key);
        else RequireBrush(track, e => ((Grid)e).Background, DesignToken.RunSoft, $"{what}'s track", key: key);
        RequireBrush(fill, e => ((Border)e).Background, warning ? DesignToken.WaitText : progress ? DesignToken.Accent : DesignToken.Run, $"{what}'s fill ({(warning ? "past 90%" : "under 90%")})", key: key);
        var want = track.ActualWidth * Math.Clamp(fraction, 0, 1);
        Require(track.ActualWidth > 0 && Math.Abs(fill.Width - want) <= 1, $"{key} ({theme}): {what} must be filled to {fraction:P0} of {track.ActualWidth:F1}; got {fill.Width:F1}");
    }

    /// <summary>
    /// The settings sheet (M/SettingsViews.swift:152-269, 325-338): 800×700 points of content, the page
    /// frame, the heading (padding 22 on card, the 21pt accent gear, the 17pt title, the round close mark),
    /// the 200-wide list on the sidebar surface whose chosen 30-high row is filled with accent under onAccent
    /// words and symbol, the grouped form on the sheet's card surface (boxes on cardRaised with a 1pt line at
    /// radius 6, rows 10 in with a rule between them, a segmented picker and a switch at the trailing edge),
    /// and the close row on card.
    /// </summary>
    private async Task<FrameworkElement> RequireSettingsDesignInTheme(FrameworkElement? light)
    {
        const string key = PanelsDesignKey; var theme = SmokeTheme;
        await WaitUI(() => settingsWindow?.Content is FrameworkElement { XamlRoot: not null, ActualWidth: > 0, IsLoaded: true }, () => $"{key} ({theme}): the settings window never opened");
        var frame = (Grid)settingsWindow!.Content;
        if (light is not null) Require(ReferenceEquals(frame, light), $"{key} ({theme}): the toggle replaced the open settings window's content instead of recolouring it");
        Require(frame.RequestedTheme == root.RequestedTheme, $"{key} ({theme}): the open settings window must follow the theme; got {frame.RequestedTheme}, window {root.RequestedTheme}");
        // The sheet's 800×700 points are the window's content (the title bar and frame come on top of it,
        // M/SettingsViews.swift:201), at the display scale, clamped to the display's work area (less the margin).
        var scale = root.XamlRoot?.RasterizationScale ?? 1; var size = settingsWindow.AppWindow.ClientSize;
        var area = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(settingsWindow.AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Nearest)?.WorkArea;
        var margin = Math.Round(2 * SettingsWindowMargin * scale);
        double Want(double points, int? free) => Math.Min(Math.Round(points * scale), free is { } f && f > margin ? f - margin : double.MaxValue);
        var (wantWidth, wantHeight) = (Want(SettingsWindowWidth, area?.Width), Want(SettingsWindowHeight, area?.Height));
        Require(Math.Abs(size.Width - wantWidth) <= 1 && Math.Abs(size.Height - wantHeight) <= 1,
            $"{key} ({theme}): the settings window's content must be min({SettingsWindowWidth}×{SettingsWindowHeight} points × {scale}, work area {area?.Width}×{area?.Height} less {margin}) = {wantWidth}×{wantHeight} px; got {size.Width}×{size.Height} px");
        RequireSame(frame.Background, brushes.Brush(DesignToken.Page), "the settings frame");
        RequireBrush(frame, e => ((Grid)e).Background, DesignToken.Page, "the settings frame", key: key);
        FrameworkElement Part(string id) => VisualChildren(frame).OfType<FrameworkElement>().FirstOrDefault(e => AutomationProperties.GetAutomationId(e) == id)
            ?? throw new InvalidOperationException($"{key} ({theme}): the settings window has no {id}");
        var heading = (Grid)Part("settings-heading");
        Require(heading.Padding == new Thickness(22), $"{key} ({theme}): the settings heading padding must be 22; got {heading.Padding}");
        Require(heading.Height == SettingsHeadingHeight && Math.Abs(heading.ActualHeight - SettingsHeadingHeight) < .5, $"{key} ({theme}): the settings heading must be {SettingsHeadingHeight} high with its rule; got {heading.Height} (laid out {heading.ActualHeight:F1})");
        RequireBrush(heading, e => ((Grid)e).Background, DesignToken.Card, "the settings heading", key: key);
        RequireBrush(heading, e => ((Grid)e).BorderBrush, DesignToken.Line, "the line under the settings heading", key: key);
        var gear = heading.Children.OfType<FontIcon>().Single(); var words = heading.Children.OfType<StackPanel>().Single();
        Require(gear.FontSize == 21, $"{key} ({theme}): the settings gear must be 21pt; got {gear.FontSize}");
        RequireBrush(gear, e => ((FontIcon)e).Foreground, DesignToken.Accent, "the settings gear", key: key);
        RequireFont((TextBlock)words.Children[0], DesignMetrics.Type.Header, Microsoft.UI.Text.FontWeights.SemiBold, $"({theme}) the settings title", key);
        RequireBrush((TextBlock)words.Children[1], e => ((TextBlock)e).Foreground, DesignToken.Ink2, "the settings subtitle", key: key);
        // The round close mark at the heading's trailing edge (M/SettingsViews.swift:336): a 17pt disc in the tertiary ink.
        var dismiss = (Button)Part("settings-heading-close");
        Require(dismiss.Width == 17 && dismiss.Height == 17 && AutomationProperties.GetName(dismiss) == Locale.Get("settings.closeButton"), $"{key} ({theme}): the heading's close mark must be a 17pt button named for closing; got {dismiss.Width}×{dismiss.Height} '{AutomationProperties.GetName(dismiss)}'");
        RequireSame(((Grid)dismiss.Content).Children.OfType<Microsoft.UI.Xaml.Shapes.Ellipse>().Single().Fill, brushes.Tertiary, "the heading's close mark");
        RequireBrush(dismiss, e => ((Grid)((Button)e).Content).Children.OfType<Microsoft.UI.Xaml.Shapes.Ellipse>().Single().Fill, DesignToken.Ink, "the heading's close mark", DesignBrushes.TertiaryOpacity, key);
        var host = (Border)Part("settings-navigation-host");
        Require(Math.Abs(host.ActualWidth - SettingsNavigationWidth) < 1, $"{key} ({theme}): the settings list must be {SettingsNavigationWidth} wide; got {host.ActualWidth:F1}");
        RequireBrush(host, e => ((Border)e).Background, DesignToken.Sidebar, "the settings list surface", key: key);
        var navigation = (ListView)Part("settings-navigation");
        RequireSame(OwnResource(navigation, "ListViewItemBackgroundSelected") as Brush, brushes.Brush(DesignToken.Accent), "the settings list's ListViewItemBackgroundSelected");
        RequireSame(OwnResource(navigation, "ListViewItemForegroundSelected") as Brush, brushes.Brush(DesignToken.OnAccent), "the settings list's ListViewItemForegroundSelected");
        RequireSubtle(OwnResource(navigation, "ListViewItemBackgroundPointerOver") as Brush, "the settings list's ListViewItemBackgroundPointerOver", key);
        // The checks below read the General tab (M/SettingsViews.swift:221-270), whichever tab was open last.
        var general = navigation.Items.OfType<ListViewItem>().First(item => ((SettingsCategory)item.Tag).Id == "general");
        if (!ReferenceEquals(navigation.SelectedItem, general)) { navigation.SelectedItem = general; await WaitUI(() => service.Snapshot.SettingsPane == "general", () => $"{key} ({theme}): the General tab was never chosen"); }
        frame.UpdateLayout();
        var selected = navigation.SelectedItem as ListViewItem;
        await WaitUI(() => selected is { IsLoaded: true } && VisualTreeHelper.GetChildrenCount(selected) > 0, () => $"{key} ({theme}): the chosen settings row never loaded");
        var presenter = VisualTreeHelper.GetChild(selected!, 0);
        Require(presenter is ListViewItemPresenter, $"{key} ({theme}): the settings row's template root must be a ListViewItemPresenter; got {presenter.GetType().Name}");
        RequireBrush(selected!, _ => ((ListViewItemPresenter)presenter).SelectedBackground, DesignToken.Accent, "the chosen settings row's resolved SelectedBackground", key: key);
        RequireBrush(selected!, _ => ((ListViewItemPresenter)presenter).SelectedForeground, DesignToken.OnAccent, "the chosen settings row's resolved SelectedForeground", key: key);
        // A row's fill is 30 high at radius 5 (the stock row draws it 2 in from its own top and bottom, so the row is 34 and
        // overlaps its neighbours by 2); its symbol is accent, and onAccent on the chosen row (M/SettingsViews.swift:208-209).
        Require(Math.Abs(selected!.ActualHeight - 2 * ListSelectionInsetY - SettingsNavigationRowHeight) < .5 && selected.Margin == new Thickness(0, -ListSelectionInsetY, 0, -ListSelectionInsetY) && selected.CornerRadius == new CornerRadius(DesignMetrics.Radius.FileRow),
            $"{key} ({theme}): a settings list row's fill must be {SettingsNavigationRowHeight} high at radius {DesignMetrics.Radius.FileRow}; got a row {selected.ActualHeight:F1} high with margin {selected.Margin}, {selected.CornerRadius}");
        Brush? Symbol(ListViewItem item) => ((Grid)((StackPanel)item.Content).Children[0]).Children[0] switch { IconElement icon => icon.Foreground, Microsoft.UI.Xaml.Shapes.Shape shape => shape.Fill, _ => null };
        RequireBrush(selected, e => Symbol((ListViewItem)e), DesignToken.OnAccent, "the chosen settings row's symbol", key: key);
        foreach (var other in navigation.Items.OfType<ListViewItem>().Where(item => !ReferenceEquals(item, selected)))
            RequireBrush(other, e => Symbol((ListViewItem)e), DesignToken.Accent, $"the {((SettingsCategory)other.Tag).Id} row's symbol", key: key);
        // The form sits on the sheet's own surface, its boxes slightly raised off it (the grouped form, M/SettingsViews.swift:192-193).
        RequireBrush(Part("settings-form"), e => ((Border)e).Background, DesignToken.Card, "the settings form surface", key: key);
        var card = VisualChildren(frame).OfType<StackPanel>().Select(s => s.Children.Count > 1 && s.Children[0] is TextBlock t && AutomationProperties.GetAutomationId(t).StartsWith("settings-section-", StringComparison.Ordinal) ? s.Children[1] as Border : null).FirstOrDefault(b => b is not null)
            ?? throw new InvalidOperationException($"{key} ({theme}): the settings window shows no section card");
        RequireRadius(card, SettingsCardRadius, "a settings section card", key);
        RequireThickness(card, DesignMetrics.Stroke.Line, "a settings section card", key);
        RequireBrush(card, e => ((Border)e).Background, DesignToken.CardRaised, "a settings section card", key: key);
        RequireBrush(card, e => ((Border)e).BorderBrush, DesignToken.Line, "a settings section card border", key: key);
        // Its rows: 10 in from the box and 10 above and below, a 1pt line over every row but the first
        // (whose padding counts the box's own line as its first point).
        var rows = ((StackPanel)card.Child).Children.OfType<Border>().Where(row => row.Visibility == Visibility.Visible).ToList();
        Require(rows.Count >= 2, $"{key} ({theme}): the General tab's first box must hold its rows; got {rows.Count}");
        for (var i = 0; i < rows.Count; i++)
        {
            var rule = i == 0 ? 0 : DesignMetrics.Stroke.Line; var over = i == 0 ? SettingsRowInset - card.BorderThickness.Top : SettingsRowInset;
            Require(rows[i].Margin == SettingsRowMargin && SettingsRowMargin.Left + card.BorderThickness.Left == SettingsRowInset && rows[i].Padding == new Thickness(0, over, 0, SettingsRowInset) && rows[i].BorderThickness == new Thickness(0, rule, 0, 0),
                $"{key} ({theme}): settings row {i} must be {SettingsRowInset} in from the box's edge, padded {over} above and {SettingsRowInset} below, with a {rule} rule over it; got margin {rows[i].Margin}, padding {rows[i].Padding}, border {rows[i].BorderThickness}");
            RequireBrush(rows[i], e => ((Border)e).BorderBrush, DesignToken.Line, $"settings row {i}'s rule", key: key);
        }
        // The theme is a segmented picker: a 20-high track, the chosen segment accent under onAccent words (M/SettingsViews.swift:222).
        var picker = (Grid)Part("settings-theme");
        var track = picker.Children.OfType<Border>().First();
        Require(picker.Height == SettingsControlHeight && ReferenceEquals(track.Background, brushes.SegmentOn) && track.CornerRadius == new CornerRadius(SettingsControlRadius),
            $"{key} ({theme}): the theme picker must be a {SettingsControlHeight}-high segmented track on the shared segmentOn brush at radius {SettingsControlRadius}; got {picker.Height}, {Describe(track.Background)}, {track.CornerRadius}");
        var chosen = (ToggleButton)Part("settings-theme-" + theme); var unchosen = (ToggleButton)Part("settings-theme-" + (theme == "light" ? "dark" : "light"));
        // The theme was toggled outside this window; its picker follows the window's theme.
        await WaitUI(() => chosen.IsChecked == true, () => $"{key} ({theme}): the theme picker never followed the theme toggled outside the settings window");
        Require(chosen.IsChecked == true && unchosen.IsChecked == false, $"{key} ({theme}): the theme picker must have the {theme} segment chosen; got {chosen.IsChecked}, other {unchosen.IsChecked}");
        RequireBrush(chosen, e => ((Border)((ToggleButton)e).Content).Background, DesignToken.Accent, "the chosen theme segment", key: key);
        RequireBrush(chosen, e => ((TextBlock)((Border)((ToggleButton)e).Content).Child).Foreground, DesignToken.OnAccent, "the chosen theme segment's words", key: key);
        RequireBrush(unchosen, e => ((TextBlock)((Border)((ToggleButton)e).Content).Child).Foreground, DesignToken.Ink, "the other theme segment's words", key: key);
        // The language picker offers System and the four languages, each language named in its own language.
        var languagePicker = (Grid)Part("settings-language");
        var languageSegments = languagePicker.Children.OfType<ToggleButton>().ToList();
        Require(languageSegments.Count == Locale.PickerChoices.Count, $"{key} ({theme}): the language picker must offer {Locale.PickerChoices.Count} choices; got {languageSegments.Count}");
        foreach (var (value, labelKey) in Locale.PickerChoices)
        {
            var label = ((TextBlock)((Border)((ToggleButton)Part("settings-language-" + value)).Content).Child).Text;
            Require(label == Locale.Get(labelKey), $"{key} ({theme}): the language picker's {value} segment must read {Locale.Get(labelKey)}; got {label}");
        }
        foreach (var (value, name) in new[] { ("en", "English"), ("zh", "简体中文"), ("ja", "日本語") })
            Require(Locale.Get(Locale.PickerChoices.Single(choice => choice.Value == value).LabelKey) == name, $"{key} ({theme}): the {value} choice must be named {name} in every language");
        // A switch is the Mac's 26×15 capsule with a 13pt knob, accent when on (M/SettingsViews.swift:232-239).
        var toggle = (ToggleButton)Part("settings-status-line"); var capsule = (Grid)toggle.Content; var knob = capsule.Children.OfType<Microsoft.UI.Xaml.Shapes.Ellipse>().Single();
        Require(toggle.Width == SettingsSwitchWidth && toggle.Height == SettingsSwitchHeight && knob.Width == SettingsSwitchKnob && capsule.CornerRadius == new CornerRadius(SettingsSwitchHeight / 2),
            $"{key} ({theme}): a settings switch must be a {SettingsSwitchWidth}×{SettingsSwitchHeight} capsule with a {SettingsSwitchKnob} knob; got {toggle.Width}×{toggle.Height}, knob {knob.Width}, {capsule.CornerRadius}");
        if (toggle.IsChecked == true) RequireBrush(toggle, _ => capsule.Background, DesignToken.Accent, "an on switch", key: key);
        else RequireBrush(toggle, _ => capsule.Background, DesignToken.Ink2, "an off switch", SettingsSwitchOffOpacity, key);
        var footer = (Grid)Part("settings-footer");
        RequireBrush(footer, e => ((Grid)e).Background, DesignToken.Card, "the settings close row", key: key);
        Require(footer.Padding == new Thickness(18), $"{key} ({theme}): the settings close row padding must be 18; got {footer.Padding}");
        return frame;
    }

    /// <summary>
    /// The start-new-or-resume sheet (M/ResumeSessionSheet.swift:13-50), shown for real: 400 wide, padding 20,
    /// radius 12 and as high as its content, with no stock title or buttons; the agent's mark (18, in the Mac's
    /// 1.15 frame and a 22-wide column) 10 from the 13pt bold title over the 12pt ink2 sentence; 16 under them its
    /// own buttons 8 apart, Cancel leading and, after the spacer, Resume… and the accent Start new; its
    /// template's surface the shared card brush and its border the line.
    /// </summary>
    private async Task RequireSheetInTheme(ContentDialog sheet)
    {
        const string key = PanelsDesignKey; var theme = SmokeTheme;
        Require(OwnResource(sheet, "ContentDialogMinWidth") is double min && min == ChoiceSheetWidth && OwnResource(sheet, "ContentDialogMaxWidth") is double max && max == ChoiceSheetWidth,
            $"{key} ({theme}): the choice sheet must be {ChoiceSheetWidth} wide; got min {OwnResource(sheet, "ContentDialogMinWidth")}, max {OwnResource(sheet, "ContentDialogMaxWidth")}");
        Require(OwnResource(sheet, "ContentDialogPadding") is Thickness padding && padding == new Thickness(SheetPadding), $"{key} ({theme}): the choice sheet padding must be {SheetPadding}; got {OwnResource(sheet, "ContentDialogPadding")}");
        RequireSame(OwnResource(sheet, "ContentDialogTopOverlay") as Brush, brushes.Brush(DesignToken.Card), "the sheet's ContentDialogTopOverlay");
        RequireSame(OwnResource(sheet, "ContentDialogBorderBrush") as Brush, brushes.Brush(DesignToken.Line), "the sheet's ContentDialogBorderBrush");
        Require(sheet.Title is null && string.IsNullOrEmpty(sheet.PrimaryButtonText) && string.IsNullOrEmpty(sheet.SecondaryButtonText) && string.IsNullOrEmpty(sheet.CloseButtonText),
            $"{key} ({theme}): the choice sheet draws its own title and buttons, laid out as the Mac's; it must carry no stock ones");
        Require(sheet.CornerRadius == new CornerRadius(SheetRadius) && OwnResource(sheet, "ContentDialogMinHeight") is double least && least == 0 && OwnResource(sheet, "ContentDialogSeparatorThickness") is Thickness rule && rule == new Thickness(0),
            $"{key} ({theme}): the choice sheet must have radius {SheetRadius}, no least height and no line under its content; got {sheet.CornerRadius}, {OwnResource(sheet, "ContentDialogMinHeight")}, {OwnResource(sheet, "ContentDialogSeparatorThickness")}");
        var body = sheet.Content as StackPanel ?? throw new InvalidOperationException($"{key} ({theme}): the choice sheet's content is not its two rows");
        var top = (Grid)body.Children[0]; var buttons = (Grid)body.Children[1];
        Require(body.Spacing == 16 && top.ColumnSpacing == 10 && buttons.ColumnSpacing == 8, $"{key} ({theme}): the choice sheet's rows must be 16 apart, the mark 10 from the words and the buttons 8 apart; got {body.Spacing}, {top.ColumnSpacing}, {buttons.ColumnSpacing}");
        var mark = top.Children.OfType<Microsoft.UI.Xaml.Shapes.Path>().Single();
        Require(Math.Abs(mark.Width - ResumeMarkSize * ProviderIconScale) < 0.001 && top.ColumnDefinitions[0].Width.Value == ResumeMarkColumn,
            $"{key} ({theme}): the sheet's agent mark must be {ResumeMarkSize} × {ProviderIconScale} in a {ResumeMarkColumn}-wide column; got {mark.Width} in {top.ColumnDefinitions[0].Width.Value}");
        var words = top.Children.OfType<StackPanel>().Single(); var title = (TextBlock)words.Children[0]; var sentence = (TextBlock)words.Children[1];
        Require(title.Text == Locale.Get("resume.choice.title", new Dictionary<string, string> { ["provider"] = ProviderMark.Label("claude") }) && AutomationProperties.GetName(sheet) == title.Text,
            $"{key} ({theme}): the sheet must be titled and named resume.choice.title for Claude; got '{title.Text}' / '{AutomationProperties.GetName(sheet)}'");
        RequireFont(title, DesignMetrics.Type.Title, Microsoft.UI.Text.FontWeights.Bold, $"({theme}) the sheet's title", key);
        RequireBrush(title, e => ((TextBlock)e).Foreground, DesignToken.Ink, "the sheet's title", key: key);
        Require(words.Spacing == 4 && sentence.FontSize == 12, $"{key} ({theme}): the sheet's sentence must be 12pt, 4 under the title; got {sentence.FontSize}pt, {words.Spacing}");
        RequireBrush(sentence, e => ((TextBlock)e).Foreground, DesignToken.Ink2, "the sheet's sentence", key: key);
        var order = buttons.Children.OfType<Button>().OrderBy(Grid.GetColumn).ToList();
        Require(order.Select(AutomationProperties.GetAutomationId).SequenceEqual(["add-pane-choice-cancel", "add-pane-choice-resume", "add-pane-choice-new"])
            && order.Select(Grid.GetColumn).SequenceEqual([0, 2, 3]) && buttons.ColumnDefinitions[1].Width.IsStar,
            $"{key} ({theme}): the sheet's buttons must be Cancel at the leading edge and, after a spacer, Resume… and Start new; got {string.Join(", ", order.Select(b => AutomationProperties.GetAutomationId(b) + "@" + Grid.GetColumn(b)))}");
        foreach (var button in order)
            Require(button.Height == PushButtonHeight && button.FontSize == DesignMetrics.Type.Body && button.Padding == new Thickness(8, 0, 8, 0),
                $"{key} ({theme}): a sheet button must be {PushButtonHeight} high with {DesignMetrics.Type.Body}pt words set in 8; got {button.Height}, {button.FontSize}pt, {button.Padding}");
        RequireBrush(order[2], e => OwnResource(e, "ButtonBackground") as Brush, DesignToken.Accent, "the Start new button's fill", key: key);
        RequireBrush(order[2], e => OwnResource(e, "ButtonForeground") as Brush, DesignToken.OnAccent, "the Start new button's words", key: key);
        Task<ContentDialogResult>? showing = null;
        try
        {
            dialogOpen = true;
            showing = sheet.ShowAsync().AsTask();
            FrameworkElement? surface = null;
            await WaitUI(() => sheet.IsLoaded && (surface = VisualChildren(sheet).OfType<FrameworkElement>().FirstOrDefault(e => e.Name == "BackgroundElement")) is { ActualWidth: > 0 },
                () => $"{key} ({theme}): the shown sheet has no laid-out BackgroundElement");
            var fill = surface switch { Border b => b.Background, Panel p => p.Background, Control c => c.Background, _ => null };
            RequireSame(fill, brushes.Brush(DesignToken.Card), "the shown sheet's surface");
            RequireBrush(surface!, _ => fill, DesignToken.Card, "the shown sheet's surface", key: key);
            Require(Math.Abs(surface!.ActualWidth - ChoiceSheetWidth) <= 1, $"{key} ({theme}): the shown choice sheet must be {ChoiceSheetWidth} wide; got {surface.ActualWidth:F1}");
            if (surface is Border border) RequireBrush(border, e => ((Border)e).BorderBrush, DesignToken.Line, "the shown sheet's border", key: key);
            // A dialog opens outside the window's tree: it must take the window's theme, or its stock controls draw the system's.
            // It follows on the window's ActualThemeChanged, raised after the layout pass that applies the toggle.
            await WaitUI(() => sheet.RequestedTheme == root.RequestedTheme, () => $"{key} ({theme}): the shown sheet must take the window's theme; got {sheet.RequestedTheme}, window {root.RequestedTheme}");
            // When the sheet takes the keyboard, its default (Enter) button holds it, never Cancel.
            Require(Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(sheet.XamlRoot) is not Button focused || !order.Contains(focused) || ReferenceEquals(focused, order[2]),
                $"{key} ({theme}): the shown choice sheet must put the focus on Start new, its default button");
            await Task.Delay(350); surface.UpdateLayout();
            await CaptureElement(surface, Path.Combine(options.ProfileDirectory!, $"smoke-panels-sheet-choice-{theme}.png"));
        }
        finally
        {
            if (showing is not null) { sheet.Hide(); try { await showing; } catch (Exception) { /* The sheet is closing; its own failure is not the check's. */ } }
            dialogOpen = false;
        }
    }

    /// <summary>
    /// The diagram's history block in its present state, written to <c>smoke-panels-history-{name}.png</c>. The block
    /// on the diagram is usually scrolled out of sight, so one built the same way is laid over the window for the capture.
    /// </summary>
    private async Task CaptureHistoryBlock(PaneView pane, string name)
    {
        if (pane.HistoryCardForCapture() is { } card) await CaptureLaidOver(card, "history-" + name);
    }

    /// <summary>An element that is on no page now, laid over the window's corner for as long as its capture to <c>smoke-panels-{name}.png</c> takes.</summary>
    private async Task CaptureLaidOver(FrameworkElement element, string name)
    {
        element.HorizontalAlignment = HorizontalAlignment.Left; element.VerticalAlignment = VerticalAlignment.Top; element.Margin = new Thickness(8);
        Grid.SetRowSpan(element, 3); Grid.SetColumnSpan(element, 2); root.Children.Add(element);
        try
        {
            root.UpdateLayout(); await Task.Delay(200);
            await CaptureElement(element, Path.Combine(options.ProfileDirectory!, $"smoke-panels-{name}.png"));
        }
        finally { root.Children.Remove(element); }
    }

    /// <summary>
    /// Shows a sheet for real, captures its surface to <c>smoke-panels-{name}-{theme}.png</c> in each theme, and
    /// hides it again. For a sheet the smoke otherwise answers without showing (<see cref="smokeResumeDialog"/>).
    /// </summary>
    private async Task CaptureSheet(ContentDialog sheet, string name)
    {
        var showing = sheet.ShowAsync().AsTask(); var before = service.Snapshot.Theme;
        try
        {
            FrameworkElement? surface = null;
            await WaitUI(() => sheet.IsLoaded && (surface = VisualChildren(sheet).OfType<FrameworkElement>().FirstOrDefault(e => e.Name == "BackgroundElement")) is { ActualWidth: > 0 },
                () => $"the {name} sheet never showed its surface");
            foreach (var theme in new[] { before, before == "light" ? "dark" : "light" })
            {
                await service.UpdateAsync(s => s with { Theme = theme }); Render();
                await Task.Delay(350); surface!.UpdateLayout();
                await CaptureElement(surface, Path.Combine(options.ProfileDirectory!, $"smoke-panels-{name}-{SmokeTheme}.png"));
            }
        }
        finally
        {
            await service.UpdateAsync(s => s with { Theme = before }); Render();
            sheet.Hide(); try { await showing; } catch (Exception) { /* The sheet is closing; its own failure is not the capture's. */ }
        }
    }

    /// <summary>
    /// The popovers (M/StatusBarUsage.swift:167-312, M/SessionInfoViews.swift): the usage and session
    /// popovers' presenter is the card at radius 10 with a 1pt line and padding 16, the usage rows draw
    /// 6pt bars, the session popover's ring block sits on accentSoft under an ink2 status line.
    /// </summary>
    private async Task<Style> RequirePopoversInTheme(PaneView agent, Style? light)
    {
        const string key = PanelsDesignKey; var theme = SmokeTheme;
        var style = CardFlyoutStyle;
        if (light is not null) Require(ReferenceEquals(style, light), $"{key} ({theme}): the popover style was replaced instead of recoloured");
        Require(usageButton is { Visibility: Visibility.Visible, Flyout: Flyout } && ReferenceEquals(((Flyout)usageButton.Flyout).FlyoutPresenterStyle, style),
            $"{key} ({theme}): the status bar's usage popover must show and use the card presenter style");
        var flyout = (Flyout)usageButton!.Flyout;
        flyout.ShowAt(usageButton);
        try
        {
            await WaitUI(() => usageDetailsOpen && usageDetails.IsLoaded && Ancestor(usageDetails, n => n is FlyoutPresenter) is not null, () => $"{key} ({theme}): the usage popover never opened");
            RequirePresenter((FlyoutPresenter)Ancestor(usageDetails, n => n is FlyoutPresenter)!, "the usage popover");
            var claude = usage!.Cards().First(c => c.Provider == "claude");
            for (var i = 0; i < claude.Windows.Count; i++)
            {
                var index = i;
                Grid? bar = null;
                await WaitUI(() => (bar = VisualChildren(usageDetails).OfType<Grid>().FirstOrDefault(g => AutomationProperties.GetAutomationId(g) == "statusbar-usage-bar-claude-" + index)) is { ActualWidth: > 0 },
                    () => $"{key} ({theme}): the usage popover never laid out Claude's bar {index}");
                RequireUsageBar(bar!, claude.Windows[i].Fraction, claude.Windows[i].Warning, $"the usage popover's Claude bar {i}", progress: true);
            }
        }
        finally { flyout.Hide(); }
        await agent.RequireSessionPopoverInTheme(style);
        return style;
    }

    private void RequirePresenter(FlyoutPresenter presenter, string what)
    {
        const string key = PanelsDesignKey; var theme = SmokeTheme;
        RequireSame(presenter.Background, brushes.Brush(DesignToken.Card), what + "'s surface");
        RequireBrush(presenter, e => ((Control)e).Background, DesignToken.Card, what + "'s surface", key: key);
        RequireBrush(presenter, e => ((Control)e).BorderBrush, DesignToken.Line, what + "'s border", key: key);
        Require(presenter.BorderThickness == new Thickness(DesignMetrics.Stroke.Line) && presenter.CornerRadius == new CornerRadius(DesignMetrics.Radius.Entry) && presenter.Padding == new Thickness(PopoverPadding),
            $"{key} ({theme}): {what} must be a 1pt, radius {DesignMetrics.Radius.Entry} card with padding {PopoverPadding}; got {presenter.BorderThickness}, {presenter.CornerRadius}, {presenter.Padding}");
    }

    private sealed partial class PaneView
    {
        internal string SessionId => id;

        /// <summary>The session popover opened from the context ring, read, and closed again.</summary>
        internal async Task RequireSessionPopoverInTheme(Style style)
        {
            const string key = PanelsDesignKey; var theme = owner.SmokeTheme;
            await ShowContext();
            try
            {
                var (flyout, status, block) = SessionInfoDesignParts;
                Require(flyout is not null && ReferenceEquals(flyout.FlyoutPresenterStyle, style), $"{key} ({theme}): the session popover must use the card presenter style");
                await WaitUI(() => sessionInfoOpen && status is { IsLoaded: true } && Ancestor(status, n => n is FlyoutPresenter) is not null, () => $"{key} ({theme}): the session popover never opened");
                owner.RequirePresenter((FlyoutPresenter)Ancestor(status!, n => n is FlyoutPresenter)!, "the session popover");
                owner.RequireBrush(status!, e => ((TextBlock)e).Foreground, DesignToken.Ink2, "the session popover's status line", key: key);
                // The context block's tint is accent × 0.07 (M/SessionInfoViews.swift:242).
                owner.RequireBrush(block!, e => ((Border)e).Background, DesignToken.Accent, "the session popover's context block", SessionInfoContextTint, key);
            }
            finally { sessionInfoFlyout?.Hide(); }
        }

        /// <summary>Puts the preview back to "choose a file on the left", as a pane with nothing chosen shows it.</summary>
        internal void FilesSmokeShowPlaceholder()
        {
            previewRequest++; previewCancel?.Cancel();
            Tree.PreviewPath = null; Tree.SelectedPath = null; shown = null;
            previewBanners?.Children.Clear(); ClearPreviewTools();
            ShowPlaceholder(); RenderTree();
        }

        /// <summary>Reads the tree again, as the refresh button does.</summary>
        internal Task FilesSmokeRefresh() => RefreshFiles();

        /// <summary>A name too long for the tree is cut in its middle, keeping its start and its end, extension included (M/FilePaneView.swift:111).</summary>
        internal async Task RequireFilesNameCutInMiddle(string name)
        {
            const string key = PanelsDesignKey; var theme = owner.SmokeTheme;
            var block = filesNames.FirstOrDefault(row => row.Full == name).Block;
            Require(block is not null, $"{key} ({theme}): the files tree shows no row named {name}");
            await WaitUI(() => block!.Text.Contains('…') && !block.IsTextTrimmed, () => $"{key} ({theme}): the long name was never cut in its middle; the tree shows '{block!.Text}' (trimmed {block.IsTextTrimmed}, room {block.MaxWidth:F0})");
            var cut = block!.Text.IndexOf('…');
            Require(cut > 0 && cut < block.Text.Length - 1 && name.StartsWith(block.Text[..cut], StringComparison.Ordinal) && name.EndsWith(block.Text[(cut + 1)..], StringComparison.Ordinal),
                $"{key} ({theme}): a name too long for the tree must keep its start and its end around one ellipsis; got '{block.Text}'");
        }

        /// <summary>
        /// The divider between the tree and the preview drags as the Mac's split view does (M/FilePaneView.swift:23-24):
        /// the tree never under 160 nor over 520, the preview never under 160; then it is put back to the even split.
        /// </summary>
        internal void RequireFilesSplitDrags()
        {
            const string key = PanelsDesignKey; var theme = owner.SmokeTheme;
            var host = filesHost!; var column = host.ColumnDefinitions[0]; var room = host.ActualWidth - DesignMetrics.Stroke.Line; var even = column.Width.Value;
            var widest = Math.Max(Math.Min(FilesTreeMin, room), Math.Min(FilesTreeMax, room - FilesPreviewMin));
            DragFilesSplit(column, -10_000);
            Require(Math.Abs(column.Width.Value - Math.Min(FilesTreeMin, room)) < 0.5, $"{key} ({theme}): dragged to the leading edge the files tree must stop at {FilesTreeMin}; got {column.Width.Value:F1} of {room:F0}");
            DragFilesSplit(column, 10_000);
            Require(Math.Abs(column.Width.Value - widest) < 0.5, $"{key} ({theme}): dragged to the trailing edge the files tree must stop at {widest:F0} (at most {FilesTreeMax}, leaving the preview {FilesPreviewMin}); got {column.Width.Value:F1} of {room:F0}");
            DragFilesSplit(column, 40 - (column.Width.Value - FilesTreeMin));
            Require(Math.Abs(column.Width.Value - (FilesTreeMin + 40)) < 0.5, $"{key} ({theme}): the files divider must follow a drag; got a tree {column.Width.Value:F1} wide, wanted {FilesTreeMin + 40}");
            filesTreeShare = FilesTreeShare; column.Width = new GridLength(FilesTreeWidth(host.ActualWidth));
            Require(Math.Abs(column.Width.Value - even) < 0.5, $"{key} ({theme}): the files tree did not return to the even split; got {column.Width.Value:F1}, was {even:F1}");
        }

        /// <summary>Types a name filter into the field and waits for the tree to take it (the field redraws the rows after a pause).</summary>
        internal async Task FilesSmokeFilter(string text)
        {
            if (filesFilter is null) return;
            filesFilter.Text = text;
            await WaitUI(() => Tree.Filter == text && renderingTree == false, () => $"{PanelsDesignKey}: the files filter never took '{text}'");
            owner.root.UpdateLayout();
        }

        /// <summary>Selects a tree row the way a click does, so the selection's look can be read.</summary>
        internal async Task FilesSmokeSelect(string path)
        {
            SelectPath(path);
            await WaitUI(() => filesList?.SelectedItem is ListViewItem { Tag: string tag, IsLoaded: true } && tag == path, () => $"{PanelsDesignKey}: the files tree never selected {path}");
        }

        /// <summary>
        /// The files pane (M/FilePaneView.swift:31-470): the filter row's wash and padding, the chosen row on
        /// accent × 0.18 at radius 5, the 40-high preview head on the wash, and the source's keyword and
        /// comment colours (the fixture's windowsOnly.syntax and ink2) on the very runs the light pass read.
        /// </summary>
        internal async Task<(Grid Head, Brush Keyword)> RequireFilesDesignInTheme(PanelsDesignViews? light)
        {
            const string key = PanelsDesignKey; var theme = owner.SmokeTheme; var b = owner.brushes;
            var (filter, tree, head, content) = FilesDesignParts;
            Require(filter is not null && tree is not null && head is not null && content is not null, $"{key} ({theme}): the files pane is missing its filter row, tree, preview head or content");
            owner.RequireSubtle(filter!.Background, "the files filter row", key);
            Require(filter.Padding == new Thickness(10, 7, 10, 7) && filter.Height == FilesFilterHeight && filter.ColumnSpacing == 6, $"{key} ({theme}): the files filter row must be {FilesFilterHeight} high with padding h10 v7 and its parts 6 apart; got {filter.Height}, {filter.Padding}, {filter.ColumnSpacing}");
            // The tree and the preview fill the pane card edge to edge and share it as the Mac's split view does:
            // evenly until the tree is dragged, the tree within 160…520 (M/FilePaneView.swift:22-25).
            var host = filesHost!; var treeWidth = host.ColumnDefinitions[0].ActualWidth;
            // The filter's symbol and its placeholder are the tertiary ink (M/FilePaneView.swift:34-35), the placeholder on the words the template draws.
            var filterSymbol = filter.Children.OfType<Microsoft.UI.Xaml.Shapes.Path>().First();
            RequireShared(filterSymbol.Stroke, b.Tertiary, "the files filter's symbol", key);
            owner.RequireBrush(filterSymbol, e => ((Microsoft.UI.Xaml.Shapes.Shape)e).Stroke, DesignToken.Ink, "the files filter's symbol", DesignBrushes.TertiaryOpacity, key);
            var drawnPlaceholder = VisualChildren(filesFilter!).OfType<TextBlock>().FirstOrDefault(part => part.Name == "PlaceholderTextContentPresenter");
            Require(ReferenceEquals(filesFilter!.PlaceholderForeground, b.Tertiary) && drawnPlaceholder is not null && ReferenceEquals(drawnPlaceholder.Foreground, b.Tertiary),
                $"{key} ({theme}): the files filter's placeholder must be drawn in the shared tertiary brush; got {Describe(filesFilter.PlaceholderForeground)}, drawn {Describe(drawnPlaceholder?.Foreground)}");
            // The split view's own line between the tree and the preview: line by day, black by night (M/FilePaneView.swift:22).
            var splitter = host.Children.OfType<Border>().First(border => Grid.GetColumn(border) == 1);
            owner.RequireSplitLine(splitter, e => ((Border)e).Background, DesignToken.Line, 1, "the files pane's splitter", key);
            // The files pane is part of its group's card, with no edge of its own (PaneView.ShowActive).
            Require(Container.BorderThickness == new Thickness(0) && Math.Abs(host.ActualWidth - Container.ActualWidth) <= 1, $"{key} ({theme}): the files pane must fill its {Container.ActualWidth:F0}-wide, edgeless card edge to edge; got {host.ActualWidth:F0} inside an edge of {Container.BorderThickness}");
            Require(filesTreeShare == FilesTreeShare && Math.Abs(treeWidth - Math.Clamp((host.ActualWidth - DesignMetrics.Stroke.Line) / 2, FilesTreeMin, FilesTreeMax)) <= 1,
                $"{key} ({theme}): the files tree must take half of the {host.ActualWidth:F0}-wide pane, within {FilesTreeMin}…{FilesTreeMax}; got {treeWidth:F0} (share {filesTreeShare})");
            RequireShared(OwnResource(tree!, "ListViewItemBackgroundSelected") as Brush, b.Brush(DesignToken.Accent, DesignMetrics.Opacity.FileSelection), "the files tree's ListViewItemBackgroundSelected", key);
            var selected = tree!.SelectedItem as ListViewItem;
            await WaitUI(() => selected is { IsLoaded: true } && VisualTreeHelper.GetChildrenCount(selected) > 0, () => $"{key} ({theme}): the chosen files row never loaded");
            Require(selected!.CornerRadius == new CornerRadius(DesignMetrics.Radius.FileRow), $"{key} ({theme}): the chosen files row must have Radius.FileRow {DesignMetrics.Radius.FileRow}; got {selected.CornerRadius}");
            // A row is v4 around its 13-high line, 21 in all, its wash 4 in from the tree's sides (M/FilePaneView.swift:118-121).
            // WinUI's item presenter draws the wash 4 and 2 inside the item, so the item reaches 2 over each neighbour.
            Require(OwnResource(tree, "ListViewItemCornerRadius") is CornerRadius washCorner && washCorner == new CornerRadius(DesignMetrics.Radius.FileRow),
                $"{key} ({theme}): the files rows' wash must be rounded by Radius.FileRow {DesignMetrics.Radius.FileRow}; got {OwnResource(tree, "ListViewItemCornerRadius")}");
            Require(selected.Padding == new Thickness(0, 4 + FilesRowWash, 12, 4 + FilesRowWash) && selected.Margin == new Thickness(0, -FilesRowWash, 0, -FilesRowWash) && Math.Abs(selected.ActualHeight - 2 * FilesRowWash - (FilesRowLine + 8)) <= 0.5,
                $"{key} ({theme}): a files row's wash must be {FilesRowLine + 8} high (v4 around its {FilesRowLine}-high line); got padding {selected.Padding}, margin {selected.Margin}, {selected.ActualHeight:F1} high");
            var presenter = VisualTreeHelper.GetChild(selected, 0);
            Require(presenter is ListViewItemPresenter, $"{key} ({theme}): the files row's template root must be a ListViewItemPresenter; got {presenter.GetType().Name}");
            owner.RequireBrush(selected, _ => ((ListViewItemPresenter)presenter).SelectedBackground, DesignToken.Accent, "the chosen files row's resolved SelectedBackground", DesignMetrics.Opacity.FileSelection, key);
            if (light is not null) Require(ReferenceEquals(head, light.FilesHead), $"{key} ({theme}): the toggle rebuilt the files preview instead of recolouring it");
            Require(head!.Height == DesignMetrics.Layout.PreviewHead && head.Visibility == Visibility.Visible, $"{key} ({theme}): the preview head must show at Layout.PreviewHead {DesignMetrics.Layout.PreviewHead}; got {head.Height}, {head.Visibility}");
            owner.RequireSubtle(head.Background, "the preview head", key);
            await WaitUI(() => content!.IsLoaded && VisualChildren(content).OfType<RichTextBlock>().Any(), () => $"{key} ({theme}): the source preview never loaded");
            var runs = VisualChildren(content!).OfType<RichTextBlock>().SelectMany(t => t.Blocks.OfType<Paragraph>()).SelectMany(p => p.Inlines.OfType<Run>()).ToList();
            // Read from the live preview right before the check; the dark pass proves recolouring in place by
            // finding the very brush the light pass read on it (a redraw would still use the shared brush).
            var keyword = runs.FirstOrDefault(r => r.Text == "import") ?? throw new InvalidOperationException($"{key} ({theme}): the source preview has no 'import' run; runs: {string.Join("|", runs.Take(8).Select(r => r.Text))}");
            if (light is not null) Require(ReferenceEquals(keyword.Foreground, light.Keyword), $"{key} ({theme}): the 'import' keyword's brush was replaced instead of recoloured; got {Describe(keyword.Foreground)}");
            var want = DesignFixture.Value.GetProperty("windowsOnly").GetProperty("syntax").GetProperty(theme).GetProperty("keyword").GetString()!;
            RequireShared(keyword.Foreground, b.Syntax("keyword")!, "the 'import' keyword", key);
            owner.RequireHex(keyword.Foreground, want, "windowsOnly.syntax.keyword", "the 'import' keyword", key);
            var comment = runs.FirstOrDefault(r => r.Text.StartsWith("//", StringComparison.Ordinal)) ?? throw new InvalidOperationException($"{key} ({theme}): the source preview has no comment run");
            owner.RequireBrush(head, _ => comment.Foreground, DesignToken.Ink2, "a source comment", key: key);
            // The source's lines and its gutter (M/FilePaneView.swift:392, 433, 477, 493-508): 12pt mono 15 apart, set in
            // 6 + 5 and 8; the numbers 10pt in the tertiary ink (tertiaryLabelColor, :493) in a gutter digits × 7 + 16 wide (30 for this file) closed by a line.
            var source = VisualChildren(content!).OfType<RichTextBlock>().First();
            Require(source.FontSize == 12 && source.LineHeight == FilesLineHeight && source.Margin == new Thickness(11, 8, 11, 8),
                $"{key} ({theme}): the source must be 12pt in lines of {FilesLineHeight}, set in 11 and 8; got {source.FontSize}pt, {source.LineHeight}, {source.Margin}");
            var gutter = VisualChildren(content!).OfType<Border>().FirstOrDefault(border => border.Child is TextBlock { TextAlignment: TextAlignment.Right })
                ?? throw new InvalidOperationException($"{key} ({theme}): the source preview has no line-number gutter");
            var numbers = (TextBlock)gutter.Child;
            Require(gutter.Width == 30 && gutter.BorderThickness == new Thickness(0, 0, DesignMetrics.Stroke.Line, 0) && numbers.FontSize == DesignMetrics.Type.Small && numbers.LineHeight == FilesLineHeight,
                $"{key} ({theme}): the gutter must be 30 wide with a 1pt line at its end, its numbers {DesignMetrics.Type.Small}pt in lines of {FilesLineHeight}; got {gutter.Width}, {gutter.BorderThickness}, {numbers.FontSize}pt, {numbers.LineHeight}");
            owner.RequireBrush(gutter, e => ((Border)e).BorderBrush, DesignToken.Line, "the gutter's line", key: key);
            RequireShared(numbers.Foreground, b.Tertiary, "the line numbers", key);
            owner.RequireBrush(numbers, e => ((TextBlock)e).Foreground, DesignToken.Ink, "the line numbers", DesignBrushes.TertiaryOpacity, key);
            return (head, keyword.Foreground);
        }
    }
}
