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
    private readonly Dictionary<string, TextBlock> gitBadges = [];
    private CancellationTokenSource? gitCancellation;
    private string? gitWorkspaceKey;
    private WorkspaceGitInfo? gitInfo;
    private bool gitReading;

    private void ShowLaunchSplash()
    {
        launchSplash = new Grid { Background = WindowBackground() };
        var stack = new StackPanel { Spacing = 18, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var icon = new Image { Source = new BitmapImage(new Uri("ms-appx:///Assets/mightyclaude.png")), Width = 104, Height = 104 };
        AutomationProperties.SetAccessibilityView(icon, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        stack.Children.Add(icon);
        stack.Children.Add(new TextBlock { Text = "Mighty Claude", FontSize = 30, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
        stack.Children.Add(new TextBlock { Text = Locale.Get("launch.preparing"), FontSize = 13, Opacity = .65, HorizontalAlignment = HorizontalAlignment.Center });
        var progress = new ProgressRing { IsActive = true, Width = 24, Height = 24, Margin = new Thickness(0, 10, 0, 0) };
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
        Bind(VirtualKey.K, () => { search.Focus(FocusState.Keyboard); search.SelectAll(); return Task.CompletedTask; });
        Bind(VirtualKey.T, () => service.Snapshot.ActiveWorkspaceId is null ? Task.CompletedTask : AddPane("shell"));
        Bind((VirtualKey)188, OpenSettings); // OEM comma, the macOS ⌘, counterpart.
    }

    private void InitWorkspaceGit()
    {
        if (options.SmokeTest) return;
        gitClock.Tick += (_, _) => RefreshWorkspaceGit(force: true); gitClock.Start();
    }

    private FrameworkElement? WorkspaceGitBadge(Workspace workspace)
    {
        if (workspace.Id != service.Snapshot.ActiveWorkspaceId) return null;
        var label = new TextBlock { FontSize = DesignMetrics.Type.Mono, Foreground = brushes.Brush(DesignToken.Ink2), TextTrimming = TextTrimming.CharacterEllipsis, Visibility = Visibility.Collapsed };
        gitBadges[workspace.Id] = label; AutomationProperties.SetAutomationId(label, "workspace-git-info");
        if (gitWorkspaceKey == workspace.Id + "|" + workspace.Path) UpdateGitBadge(label);
        return label;
    }

    private void UpdateGitBadge(TextBlock label)
    {
        label.Text = gitInfo?.Badge ?? ""; label.Visibility = gitInfo is null ? Visibility.Collapsed : Visibility.Visible;
        if (gitInfo is not { } value) return;
        var help = "Git · " + value.Label + " · " + Locale.Get(value.IsDirty ? "git.dirty" : "git.clean") + "\n" + Locale.Get("git.localUpstream");
        ToolTipService.SetToolTip(label, help); AutomationProperties.SetName(label, help);
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
