using MightyClaude.Core;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.System;

namespace MightyClaude.WinUI;

public sealed partial class MainWindow
{
    private Grid? launchSplash;
    private readonly DispatcherTimer gitClock = new() { Interval = TimeSpan.FromSeconds(10) };
    private readonly Dictionary<string, Border> gitBadges = [];
    private CancellationTokenSource? gitCancellation;
    private string? gitWorkspaceKey;
    private WorkspaceGitInfo? gitInfo;
    private bool gitReading;
    /// <summary>The splash's glow: accent × 0.09 at the centre fading to nothing between 30 and 360 points out (M/LaunchSplashView.swift:9-14).</summary>
    internal const double SplashGlowOpacity = 0.09, SplashGlowInner = 30, SplashGlowOuter = 360;
    /// <summary>The splash icon's size and its shadow: accent × 0.10, blurred 24 and set 8 down (M/LaunchSplashView.swift:20-21).</summary>
    internal const double SplashIcon = 104, SplashShadowOpacity = 0.10, SplashShadowBlur = 24, SplashShadowDrop = 8;

    /// <summary>
    /// The launch splash (M/LaunchSplashView.swift:6-44): the page under a faint accent glow from its
    /// centre; the 104pt app icon over its soft accent shadow, 24 over "Mighty Claude" (30 semibold,
    /// tracking −0.6, <c>ink</c>), 10 over the 13pt <c>ink2</c> line, 28 over a small spinner.
    /// </summary>
    private void ShowLaunchSplash()
    {
        launchSplash = new Grid { Background = WindowBackground() };
        // The glow is a fixed disc about the centre, so its radii are the Mac's points at any window size.
        var accent = brushes.Palette[DesignToken.Accent];
        var glow = new RadialGradientBrush { Center = new Windows.Foundation.Point(0.5, 0.5), GradientOrigin = new Windows.Foundation.Point(0.5, 0.5), RadiusX = 0.5, RadiusY = 0.5 };
        glow.GradientStops.Add(new GradientStop { Color = DesignBrushes.ToColor(accent, SplashGlowOpacity), Offset = 0 });
        glow.GradientStops.Add(new GradientStop { Color = DesignBrushes.ToColor(accent, SplashGlowOpacity), Offset = SplashGlowInner / SplashGlowOuter });
        glow.GradientStops.Add(new GradientStop { Color = DesignBrushes.ToColor(accent, 0), Offset = 1 });
        launchSplash.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 2 * SplashGlowOuter, Height = 2 * SplashGlowOuter, Fill = glow, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false });
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(48) };
        var icon = new Image { Source = new BitmapImage(new Uri("ms-appx:///Assets/mightyclaude.png")), Width = SplashIcon, Height = SplashIcon };
        AutomationProperties.SetAccessibilityView(icon, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        // The round icon's shadow is a blurred accent disc, which is a radial fade: half strength at the icon's
        // rim, gone the blur's width outside it. It is painted under the icon, 8 down, as the glow is painted.
        var reach = SplashIcon / 2 + SplashShadowBlur;
        var shade = new RadialGradientBrush { Center = new Windows.Foundation.Point(0.5, 0.5), GradientOrigin = new Windows.Foundation.Point(0.5, 0.5), RadiusX = 0.5, RadiusY = 0.5 };
        shade.GradientStops.Add(new GradientStop { Color = DesignBrushes.ToColor(accent, SplashShadowOpacity), Offset = (SplashIcon / 2 - SplashShadowBlur) / reach });
        shade.GradientStops.Add(new GradientStop { Color = DesignBrushes.ToColor(accent, SplashShadowOpacity / 2), Offset = SplashIcon / 2 / reach });
        shade.GradientStops.Add(new GradientStop { Color = DesignBrushes.ToColor(accent, 0), Offset = 1 });
        var badge = new Grid { Width = SplashIcon, Height = SplashIcon, Margin = new Thickness(0, 0, 0, 24) };
        badge.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 2 * reach, Height = 2 * reach, Margin = new Thickness(-SplashShadowBlur, SplashShadowDrop - SplashShadowBlur, -SplashShadowBlur, -SplashShadowDrop - SplashShadowBlur), Fill = shade, IsHitTestVisible = false });
        badge.Children.Add(icon); stack.Children.Add(badge);
        // Tracking −0.6pt at 30pt is −20 thousandths of an em. SF's 30pt line is 36 high where Segoe's is 40, and its
        // 13pt line 15.5 where Segoe's is 17: the margins give the difference back, so the column measures as the Mac's.
        stack.Children.Add(new TextBlock { Text = "Mighty Claude", FontSize = 30, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, CharacterSpacing = -20, Foreground = brushes.Brush(DesignToken.Ink), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, -4, 0, 10) });
        stack.Children.Add(new TextBlock { Text = Locale.Get("launch.preparing"), FontSize = DesignMetrics.Type.Body, Foreground = brushes.Brush(DesignToken.Ink2), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, -2, 0, 0) });
        var progress = new ProgressRing { IsActive = true, Width = 16, Height = 16, MinWidth = 16, MinHeight = 16, Margin = new Thickness(0, 28, 0, 0) };
        AutomationProperties.SetName(progress, Locale.Get("launch.loading")); stack.Children.Add(progress);
        launchSplash.Children.Add(stack); Grid.SetRowSpan(launchSplash, 3); Grid.SetColumnSpan(launchSplash, 2);
        AutomationProperties.SetAutomationId(launchSplash, "launch-splash"); root.Children.Add(launchSplash);
    }

    private void HideLaunchSplash()
    {
        if (launchSplash is null) return;
        root.Children.Remove(launchSplash); launchSplash = null;
    }

    private void InitParityShortcuts()
    {
        void Bind(VirtualKey key, Func<Task> action)
        {
            var shortcut = new KeyboardAccelerator { Key = key, Modifiers = VirtualKeyModifiers.Control };
            shortcut.Invoked += async (_, args) => { args.Handled = true; if (!dialogOpen && launchSplash is null) await action(); };
            root.KeyboardAccelerators.Add(shortcut);
        }
        // The search lives in the sidebar: a folded sidebar opens first.
        Bind(VirtualKey.K, () => Act(async () =>
        {
            var save = SetSidebarCollapsed(false); root.UpdateLayout();
            search.Focus(FocusState.Keyboard); search.SelectAll();
            await save;
        }));
        Bind(VirtualKey.B, ToggleSidebar);
        Bind(VirtualKey.T, () => service.Snapshot.ActiveWorkspaceId is null ? Task.CompletedTask : AddPane("shell"));
        Bind((VirtualKey)188, OpenSettings); // OEM comma, the macOS ⌘, counterpart.
    }

    private void InitWorkspaceGit()
    {
        if (options.SmokeTest) return;
        gitClock.Tick += (_, _) => RefreshWorkspaceGit(force: true); gitClock.Start();
    }

    /// <summary>The Git capsule's widest (M/WorkspaceGitView.swift:50) and its height, a 10pt line in v3 (M/WorkspaceGitView.swift:47-48).</summary>
    internal const double GitBadgeMaxWidth = 260, GitBadgeHeight = 18;

    /// <summary>
    /// The workspace header's Git capsule (M/WorkspaceGitView.swift:36-56): the branch symbol, the branch
    /// name, an accent dot while there are uncommitted changes and the ahead / behind counts, 5 apart in
    /// 10pt medium <c>ink2</c>, padding h7 v3 on a subtle capsule at most 260 wide.
    /// </summary>
    private FrameworkElement? WorkspaceGitBadge(Workspace workspace)
    {
        if (workspace.Id != service.Snapshot.ActiveWorkspaceId) return null;
        // 18 high: the Mac's 10pt line (12) in v3; the words are centred in it.
        var badge = new Border { Height = GitBadgeHeight, Padding = new Thickness(7, 0, 7, 0), CornerRadius = new CornerRadius(GitBadgeHeight / 2), Background = brushes.Subtle, MaxWidth = GitBadgeMaxWidth, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        gitBadges[workspace.Id] = badge; AutomationProperties.SetAutomationId(badge, "workspace-git-info");
        if (gitWorkspaceKey == workspace.Id + "|" + workspace.Path) UpdateGitBadge(badge);
        return badge;
    }

    private void UpdateGitBadge(Border badge)
    {
        badge.Child = null; badge.Visibility = gitInfo is null ? Visibility.Collapsed : Visibility.Visible;
        if (gitInfo is not { } value) return;
        var ink = brushes.Brush(DesignToken.Ink2);
        TextBlock Words(string text) => new() { Text = text, FontSize = DesignMetrics.Type.Small, FontWeight = Microsoft.UI.Text.FontWeights.Medium, Foreground = ink, VerticalAlignment = VerticalAlignment.Center };
        // arrow.triangle.branch on a 10-unit box: the trunk between two rings, a third ring on the branch that leaves it.
        var branch = new Canvas { Width = 10, Height = 10, VerticalAlignment = VerticalAlignment.Center };
        foreach (var (x, y) in new[] { (2.6, 1.9), (2.6, 8.1), (7.4, 2.9) })
        {
            var ring = new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 2.6, Height = 2.6, Stroke = ink, StrokeThickness = 1 };
            Canvas.SetLeft(ring, x - 1.3); Canvas.SetTop(ring, y - 1.3); branch.Children.Add(ring);
        }
        foreach (var points in new[] { new[] { (2.6, 3.2), (2.6, 6.8) }, new[] { (7.4, 4.2), (7.4, 4.9), (5.9, 6.1), (2.6, 6.1) } })
        {
            var line = new Microsoft.UI.Xaml.Shapes.Polyline { Stroke = ink, StrokeThickness = 1, StrokeLineJoin = PenLineJoin.Round };
            foreach (var (x, y) in points) line.Points.Add(new(x, y));
            branch.Children.Add(line);
        }
        var parts = new List<FrameworkElement> { branch };
        var label = Words(value.Label); label.TextTrimming = TextTrimming.CharacterEllipsis; label.TextWrapping = TextWrapping.NoWrap; parts.Add(label);
        if (value.IsDirty) parts.Add(new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 5, Height = 5, Fill = brushes.Brush(DesignToken.Accent), VerticalAlignment = VerticalAlignment.Center });
        foreach (var (count, arrow) in new[] { (value.Ahead, "↑"), (value.Behind, "↓") })
        {
            if (count is not > 0) continue;
            var words = Words(arrow + count.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Microsoft.UI.Xaml.Documents.Typography.SetNumeralAlignment(words, FontNumeralAlignment.Tabular); parts.Add(words);
        }
        // Only the name gives way: left-aligned, its star column takes just the name's width, or what the other parts leave, and trims.
        var row = new Grid { ColumnSpacing = 5, HorizontalAlignment = HorizontalAlignment.Left };
        for (var column = 0; column < parts.Count; column++)
        {
            row.ColumnDefinitions.Add(new() { Width = column == 1 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
            Grid.SetColumn(parts[column], column); row.Children.Add(parts[column]);
        }
        badge.Child = row;
        var help = "Git · " + value.Label + " · " + Locale.Get(value.IsDirty ? "git.dirty" : "git.clean") + "\n" + Locale.Get("git.localUpstream");
        ToolTipService.SetToolTip(badge, help); AutomationProperties.SetName(badge, help);
    }

    private async void RefreshWorkspaceGit(bool force = false)
    {
        if (closing || options.SmokeTest) return;
        var workspace = service.Snapshot.Workspaces.FirstOrDefault(w => w.Id == service.Snapshot.ActiveWorkspaceId);
        var key = workspace is null ? null : workspace.Id + "|" + workspace.Path;
        if (key != gitWorkspaceKey)
        {
            gitCancellation?.Cancel(); gitCancellation?.Dispose(); gitCancellation = null;
            gitWorkspaceKey = key; gitInfo = null; gitReading = false;
            foreach (var stale in gitBadges.Keys.Where(id => id != workspace?.Id).ToArray()) gitBadges.Remove(stale);
        }
        else if (!force) return;
        if (workspace is null || gitReading) return;
        gitReading = true; var cancellation = new CancellationTokenSource(); gitCancellation = cancellation;
        try
        {
            var value = await WorkspaceGitInfo.ReadAsync(workspace.Path, cancellation.Token);
            if (closing || cancellation.IsCancellationRequested || gitWorkspaceKey != key) return;
            gitInfo = value;
            if (gitBadges.TryGetValue(workspace.Id, out var label)) UpdateGitBadge(label);
        }
        catch (Exception) { /* An unavailable Git executable never blocks workspace navigation. */ }
        finally { if (ReferenceEquals(gitCancellation, cancellation)) gitReading = false; }
    }

    private void StopWorkspaceGit() { gitClock.Stop(); gitCancellation?.Cancel(); StopDashboardGit(); }
}
