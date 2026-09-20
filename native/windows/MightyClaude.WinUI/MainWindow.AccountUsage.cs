using MightyClaude.Core;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace MightyClaude.WinUI;

// The account usage chips in the bottom status bar and their details popover.
// Every decision — which providers get a chip, what each chip says, what the
// popover rows are — is made by AccountUsageStatus in Core and proven on the
// Mac. This file only draws those rows and forwards the click, the refresh and
// the switch.
public sealed partial class MainWindow
{
    private readonly StackPanel usageChips = new() { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel usageDetails = new() { Spacing = 12, Width = 320 };
    private Button? usageButton;
    private AccountUsageStatus? usage;
    private readonly CancellationTokenSource usageClosing = new();
    private bool usageRefreshQueued, usageDetailsOpen;

    /// Built into the footer next to the status text. Hidden until a provider
    /// with a local AI pane exists, exactly as on macOS.
    private FrameworkElement BuildAccountUsage()
    {
        usage = new AccountUsageStatus(new AccountUsageService(AccountUsageRuntime.Probe(
            (provider, token) => service.Providers.FindAsync(provider, token),
            () => Environment.GetEnvironmentVariables().Keys.Cast<string>().ToDictionary(k => k, k => Environment.GetEnvironmentVariable(k) ?? ""),
            () => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Path.GetTempPath())));
        var flyout = new Flyout { Content = new ScrollViewer { Content = usageDetails, MaxHeight = 520 }, Placement = FlyoutPlacementMode.Top };
        flyout.Opened += (_, _) => { usageDetailsOpen = true; RenderAccountUsageDetails(); QueueAccountUsageRefresh(false); };
        flyout.Closed += (_, _) => usageDetailsOpen = false;
        usageButton = new Button { Content = usageChips, Padding = new Thickness(2), Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0), Flyout = flyout, Visibility = Visibility.Collapsed };
        AutomationProperties.SetName(usageButton, AccountUsageStrings.Title);
        AutomationProperties.SetAutomationId(usageButton, "statusbar-usage");
        ToolTipService.SetToolTip(usageButton, AccountUsageStrings.ChipsTooltip);
        return usageButton;
    }

    /// Called from Render, so the chips follow every real state change: app
    /// start, a run event, a pane added or closed, a workspace switched.
    private void RenderAccountUsage()
    {
        if (usage is null || usageButton is null) return;
        var changed = usage.Update(service.Snapshot);
        usageChips.Children.Clear();
        foreach (var chip in usage.Chips())
        {
            var text = new TextBlock { Text = ProviderCatalog.Name(chip.Provider) + " " + chip.Text, FontSize = 10, Opacity = chip.Warning ? 1 : .75 };
            if (chip.Warning) text.Foreground = new SolidColorBrush(Colors.Orange);
            var border = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(7, 3, 7, 3), Background = new SolidColorBrush(Windows.UI.Color.FromArgb(28, 128, 128, 128)), Child = text };
            AutomationProperties.SetAutomationId(border, "statusbar-usage-" + chip.Provider);
            usageChips.Children.Add(border);
        }
        usageButton.Visibility = usage.Providers.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (usageDetailsOpen) RenderAccountUsageDetails();
        // A provider pane that just appeared is worth one read; Claude is only
        // among the targets once the user switched the direct lookup on.
        if (changed) QueueAccountUsageRefresh(false);
    }

    private void RenderAccountUsageDetails()
    {
        if (usage is null) return;
        usageDetails.Children.Clear();
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock { Text = AccountUsageStrings.Title, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        var refresh = Button("↻", () => { QueueAccountUsageRefresh(true); return Task.CompletedTask; });
        refresh.IsEnabled = !usage.Refreshing;
        AutomationProperties.SetName(refresh, AccountUsageStrings.RefreshAccessibilityLabel);
        AutomationProperties.SetAutomationId(refresh, "statusbar-usage-refresh");
        ToolTipService.SetToolTip(refresh, AccountUsageStrings.RefreshTooltip);
        Grid.SetColumn(refresh, 1); header.Children.Add(refresh);
        usageDetails.Children.Add(header);

        foreach (var card in usage.Cards())
        {
            var panel = new StackPanel { Spacing = 6, Padding = new Thickness(10), CornerRadius = new CornerRadius(9), Background = new SolidColorBrush(Windows.UI.Color.FromArgb(22, 128, 128, 128)) };
            AutomationProperties.SetAutomationId(panel, "statusbar-usage-card-" + card.Provider);
            var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            title.Children.Add(new TextBlock { Text = card.Title, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            if (card.Account is { Length: > 0 }) title.Children.Add(new TextBlock { Text = card.Account, FontSize = 10, Opacity = .7, VerticalAlignment = VerticalAlignment.Center });
            panel.Children.Add(title);
            foreach (var row in card.Windows)
            {
                var line = new Grid();
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                line.Children.Add(new TextBlock { Text = row.Label, FontSize = 11 });
                var used = new TextBlock { Text = row.Used, FontSize = 11 };
                if (row.Warning) used.Foreground = new SolidColorBrush(Colors.Orange);
                Grid.SetColumn(used, 1); line.Children.Add(used);
                panel.Children.Add(line);
                panel.Children.Add(new ProgressBar { Value = row.Fraction * 100, Maximum = 100, Height = 4 });
                if (row.Reset is { Length: > 0 }) panel.Children.Add(new TextBlock { Text = row.Reset, FontSize = 10, Opacity = .65 });
            }
            if (card.Detail is { Length: > 0 }) panel.Children.Add(new TextBlock { Text = card.Detail, FontSize = 10, Opacity = .7, TextWrapping = TextWrapping.Wrap });
            if (card.Note is { Length: > 0 }) panel.Children.Add(new TextBlock { Text = card.Note, FontSize = 11, Opacity = .7, TextWrapping = TextWrapping.Wrap });
            if (card.CheckedAt is { Length: > 0 }) panel.Children.Add(new TextBlock { Text = card.CheckedAt, FontSize = 9, Opacity = .55 });
            usageDetails.Children.Add(panel);
        }

        if (usage.Providers.Contains("claude"))
        {
            var toggle = new ToggleSwitch { Header = AccountUsageStrings.ToggleLabel, IsOn = usage.DirectClaudeLookupEnabled, OffContent = "", OnContent = "" };
            AutomationProperties.SetAutomationId(toggle, "statusbar-usage-direct-toggle");
            toggle.Toggled += async (_, _) =>
            {
                if (usage.DirectClaudeLookupEnabled == toggle.IsOn) return;
                usage.SetDirectClaudeLookup(toggle.IsOn);
                await service.UpdateAsync(s => s with { ClaudeDirectUsageLookupEnabled = toggle.IsOn });
                RenderAccountUsageDetails();
                if (toggle.IsOn) QueueAccountUsageRefresh(true);
            };
            usageDetails.Children.Add(toggle);
            usageDetails.Children.Add(new TextBlock { Text = AccountUsageStrings.ToggleDescription, FontSize = 10, Opacity = .65, TextWrapping = TextWrapping.Wrap });
        }
        usageDetails.Children.Add(new TextBlock { Text = AccountUsageStrings.SharedLimitsNote, FontSize = 10, Opacity = .65, TextWrapping = TextWrapping.Wrap });
    }

    /// The read never runs on the UI thread and never runs twice at once; the
    /// window only comes back to redraw the rows.
    private void QueueAccountUsageRefresh(bool force)
    {
        if (usage is null || closing || options.SmokeTest || usageRefreshQueued || usage.Targets().Count == 0) return;
        usageRefreshQueued = true;
        _ = Task.Run(async () =>
        {
            try { await usage.RefreshAsync(force, usageClosing.Token); }
            catch (OperationCanceledException) { }
            catch { }
            finally
            {
                usageRefreshQueued = false;
                DispatcherQueue.TryEnqueue(() => { if (!closing) RenderAccountUsage(); });
            }
        });
    }

    /// Closing the app cancels the pending reads and drops the cached values.
    private async Task ShutdownAccountUsageAsync()
    {
        await usageClosing.CancelAsync();
        if (usage is not null) await usage.DisposeAsync();
    }

    // Drives the real chips and the real popover with fixture data, then puts
    // back the saved switch and the session usage it changed. The panes keep
    // their identity, so nothing else in the smoke run is disturbed. No
    // network, no CLI, no credentials file.
    internal async Task<AccountUsageSmokeOutcome> RunAccountUsageSmoke()
    {
        var before = service.Snapshot;
        try
        {
            var now = DateTimeOffset.UtcNow;
            await service.UpdateAsync(s => s with
            {
                ClaudeDirectUsageLookupEnabled = false,
                Sessions = s.Sessions.Select(pane => pane.Kind != "shell" && pane.Provider == "claude"
                    ? pane with
                    {
                        SessionUsage = (pane.SessionUsage ?? new SessionUsage { Provider = "claude", Source = "smoke.fixture" }) with
                        {
                            RateLimits = [new("five_hour", 12.5, now.AddHours(3).ToString("O")), new("seven_day", 44)],
                            RateLimitsUpdatedAt = now.AddMinutes(-1).ToString("O"),
                        }
                    }
                    : pane).ToList(),
            });
            Render();
            Require(usage is not null && usageButton is not null && usageButton.Visibility == Visibility.Visible, "계정 사용량 칩이 상태 줄에 보이지 않습니다.");
            RenderAccountUsageDetails();
            await WaitUI(() => usageChips.Children.Count > 0 && usageDetails.Children.Count > 0);

            var chips = usage!.Chips();
            var cards = usage.Cards();
            Require(usageChips.Children.Count == chips.Count && chips.Count == usage.Providers.Count, "실행 창이 있는 모든 실행기에 칩이 필요합니다.");
            Require(!usage.DirectClaudeLookupEnabled && !usage.Targets().Contains("claude"), "직접 조회는 기본적으로 꺼져 있어야 합니다.");
            var claude = chips.First(c => c.Provider == "claude").Text;
            Require(claude.Contains(AccountUsageStrings.WindowSession) && claude.Contains(AccountUsageStrings.WindowWeekly), "Claude 칩은 CLI가 보고한 한도를 보여야 합니다.");
            var gemini = cards.First(c => c.Provider == "gemini");
            Require(usageDetails.Children.OfType<ToggleSwitch>().Any(t => !t.IsOn), "직접 조회 스위치가 꺼진 채로 보여야 합니다.");
            Require(usageDetails.Children.OfType<TextBlock>().Any(t => t.Text == AccountUsageStrings.SharedLimitsNote), "공유 한도 설명이 팝오버에 없습니다.");
            return new AccountUsageSmokeOutcome
            {
                Chips = usageChips.Children.Count,
                Cards = cards.Count,
                WindowRows = cards.Sum(c => c.Windows.Count),
                DirectLookupDefaultOff = true,
                ClaudeChip = claude,
                GeminiNote = gemini.Detail ?? gemini.Note ?? "",
                Restored = true,
            };
        }
        finally
        {
            await service.UpdateAsync(s => s with { Sessions = before.Sessions, ClaudeDirectUsageLookupEnabled = before.ClaudeDirectUsageLookupEnabled });
            usage?.SetDirectClaudeLookup(before.ClaudeDirectUsageLookupEnabled);
            Render();
        }
    }
}
