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
        var flyout = new Flyout { Content = new ScrollViewer { Content = usageDetails, MaxHeight = 520 }, Placement = FlyoutPlacementMode.Top, FlyoutPresenterStyle = CardFlyoutStyle };
        flyout.Opened += (_, _) => { usageDetailsOpen = true; RenderAccountUsageDetails(); QueueAccountUsageRefresh(false); };
        flyout.Closed += (_, _) => usageDetailsOpen = false;
        usageButton = new Button { Content = usageChips, Padding = new Thickness(0), MinHeight = 0, BorderThickness = new Thickness(0), Flyout = flyout, Visibility = Visibility.Collapsed };
        PaintPlainButton(usageButton, brushes.Transparent, brushes.Transparent);
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
        AutomationProperties.SetName(usageButton, AccountUsageStrings.Title);
        ToolTipService.SetToolTip(usageButton, AccountUsageStrings.ChipsTooltip);
        var changed = usage.Update(service.Snapshot);
        usageChips.Children.Clear();
        foreach (var chip in usage.Chips())
        {
            // A capsule of the subtle wash; ink2 words, waitText when a limit is near (M/StatusBarUsage.swift:180-191).
            var text = new TextBlock { Text = ProviderCatalog.Name(chip.Provider) + " " + chip.Text, FontSize = DesignMetrics.Type.Small, Foreground = brushes.Brush(chip.Warning ? DesignToken.WaitText : DesignToken.Ink2) };
            var border = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(7, 3, 7, 3), Background = brushes.Subtle, Child = text };
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
        header.Children.Add(new TextBlock { Text = AccountUsageStrings.Title, FontSize = DesignMetrics.Type.Title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = brushes.Brush(DesignToken.Ink), VerticalAlignment = VerticalAlignment.Center });
        var refresh = Button("↻", () => { QueueAccountUsageRefresh(true); return Task.CompletedTask; });
        refresh.Content = new FontIcon { Glyph = "\uE72C", FontSize = 12 }; refresh.MinWidth = 0; refresh.MinHeight = 0; refresh.Padding = new Thickness(5, 3, 5, 3); refresh.BorderThickness = new Thickness(0); refresh.CornerRadius = new CornerRadius(DesignMetrics.Radius.FileRow);
        PaintPlainButton(refresh, brushes.Transparent, brushes.Subtle, ink: brushes.Brush(DesignToken.Ink2), disabledInk: brushes.Brush(DesignToken.Ink3));
        refresh.IsEnabled = !usage.Refreshing;
        AutomationProperties.SetName(refresh, AccountUsageStrings.RefreshAccessibilityLabel);
        AutomationProperties.SetAutomationId(refresh, "statusbar-usage-refresh");
        ToolTipService.SetToolTip(refresh, AccountUsageStrings.RefreshTooltip);
        Grid.SetColumn(refresh, 1); header.Children.Add(refresh);
        usageDetails.Children.Add(header);

        foreach (var card in usage.Cards())
        {
            // A provider's block on the subtle wash at radius 9 (M/StatusBarUsage.swift:240-285).
            var panel = new StackPanel { Spacing = 6, Padding = new Thickness(10), CornerRadius = new CornerRadius(DesignMetrics.Radius.CardButton), Background = brushes.Subtle };
            AutomationProperties.SetAutomationId(panel, "statusbar-usage-card-" + card.Provider);
            var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            title.Children.Add(ProviderMarkView.Create(card.Provider, 12));
            title.Children.Add(new TextBlock { Text = card.Title, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = brushes.Brush(DesignToken.Ink) });
            if (card.Account is { Length: > 0 }) title.Children.Add(new TextBlock { Text = card.Account, FontSize = DesignMetrics.Type.Small, Foreground = brushes.Brush(DesignToken.Ink2), VerticalAlignment = VerticalAlignment.Center });
            panel.Children.Add(title);
            for (var index = 0; index < card.Windows.Count; index++)
            {
                var row = card.Windows[index];
                var line = new Grid();
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                line.Children.Add(new TextBlock { Text = row.Label, FontSize = DesignMetrics.Type.Pill, Foreground = brushes.Brush(DesignToken.Ink) });
                var used = new TextBlock { Text = row.Used, FontSize = DesignMetrics.Type.Pill, Foreground = brushes.Brush(row.Warning ? DesignToken.WaitText : DesignToken.Ink) };
                Microsoft.UI.Xaml.Documents.Typography.SetNumeralAlignment(used, Microsoft.UI.Xaml.FontNumeralAlignment.Tabular);
                Grid.SetColumn(used, 1); line.Children.Add(used);
                panel.Children.Add(line);
                var bar = UsageBar(row.Fraction, row.Warning);
                AutomationProperties.SetAutomationId(bar, "statusbar-usage-bar-" + card.Provider + "-" + index);
                panel.Children.Add(bar);
                if (row.Reset is { Length: > 0 }) panel.Children.Add(new TextBlock { Text = row.Reset, FontSize = DesignMetrics.Type.Small, Foreground = brushes.Brush(DesignToken.Ink2) });
            }
            if (card.Detail is { Length: > 0 }) panel.Children.Add(new TextBlock { Text = card.Detail, FontSize = DesignMetrics.Type.Small, Foreground = brushes.Brush(DesignToken.Ink2), TextWrapping = TextWrapping.Wrap });
            if (card.Note is { Length: > 0 }) panel.Children.Add(new TextBlock { Text = card.Note, FontSize = DesignMetrics.Type.Pill, Foreground = brushes.Brush(DesignToken.Ink2), TextWrapping = TextWrapping.Wrap });
            if (card.CheckedAt is { Length: > 0 }) panel.Children.Add(new TextBlock { Text = card.CheckedAt, FontSize = DesignMetrics.Type.Badge, Foreground = brushes.Brush(DesignToken.Ink3) });
            if (card.Provider == "claude") RenderAccountUsageReset(panel);
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
            usageDetails.Children.Add(new TextBlock { Text = AccountUsageStrings.ToggleDescription, FontSize = DesignMetrics.Type.Small, Foreground = brushes.Brush(DesignToken.Ink2), TextWrapping = TextWrapping.Wrap });
        }
        usageDetails.Children.Add(new TextBlock { Text = AccountUsageStrings.SharedLimitsNote, FontSize = DesignMetrics.Type.Small, Foreground = brushes.Brush(DesignToken.Ink2), TextWrapping = TextWrapping.Wrap });
    }

    /// The read-only reset-entitlement rows. Core decides what they say and whether they
    /// exist at all — an empty list means the direct lookup is off. There is no
    /// reset button and no claim here; the only action is the link, and it is
    /// live in every one of the seven states.
    private void RenderAccountUsageReset(StackPanel panel) => RenderAccountUsageReset(panel, usage?.ResetRows() ?? []);

    /// The same builder, with the rows handed in: the smoke draws the real
    /// controls from an injected service's rows without touching the switch.
    private void RenderAccountUsageReset(StackPanel panel, IReadOnlyList<AccountResetRow> rows)
    {
        if (rows.Count == 0) return;
        panel.Children.Add(new Border { Height = DesignMetrics.Stroke.Line, Background = brushes.Brush(DesignToken.Line), Margin = new Thickness(0, 2, 0, 2) });
        panel.Children.Add(new TextBlock { Text = ClaudeResetEntitlements.Title, FontSize = DesignMetrics.Type.Pill, FontWeight = Microsoft.UI.Text.FontWeights.Medium, Foreground = brushes.Brush(DesignToken.Ink) });
        foreach (var row in rows)
        {
            var line = new StackPanel { Spacing = 1 };
            AutomationProperties.SetAutomationId(line, "statusbar-usage-reset-" + row.Program);
            line.Children.Add(new TextBlock { Text = row.Label, FontSize = DesignMetrics.Type.Small, Foreground = brushes.Brush(DesignToken.Ink2) });
            line.Children.Add(new TextBlock { Text = row.Text, FontSize = DesignMetrics.Type.Pill, Foreground = brushes.Brush(DesignToken.Ink), TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(line);
        }
        var link = new HyperlinkButton
        {
            Content = ClaudeResetEntitlements.LinkLabel,
            NavigateUri = new Uri(ClaudeResetEntitlements.LinkTarget),
            FontSize = 11, Padding = new Thickness(0), IsEnabled = true,
        };
        AutomationProperties.SetAutomationId(link, "statusbar-usage-reset-link");
        panel.Children.Add(link);
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
            Require(usage is not null && usageButton is not null && usageButton.Visibility == Visibility.Visible, "\uACC4\uC815 \uC0AC\uC6A9\uB7C9 \uCE69\uC774 \uC0C1\uD0DC \uC904\uC5D0 \uBCF4\uC774\uC9C0 \uC54A\uC2B5\uB2C8\uB2E4.");
            RenderAccountUsageDetails();
            await WaitUI(() => usageChips.Children.Count > 0 && usageDetails.Children.Count > 0);

            var chips = usage!.Chips();
            var cards = usage.Cards();
            Require(usageChips.Children.Count == chips.Count && chips.Count == usage.Providers.Count, "\uC2E4\uD589 \uCC3D\uC774 \uC788\uB294 \uBAA8\uB4E0 \uC2E4\uD589\uAE30\uC5D0 \uCE69\uC774 \uD544\uC694\uD569\uB2C8\uB2E4.");
            Require(!usage.DirectClaudeLookupEnabled && !usage.Targets().Contains("claude"), "\uC9C1\uC811 \uC870\uD68C\uB294 \uAE30\uBCF8\uC801\uC73C\uB85C \uAEBC\uC838 \uC788\uC5B4\uC57C \uD569\uB2C8\uB2E4.");
            var claude = chips.First(c => c.Provider == "claude").Text;
            Require(claude.Contains(AccountUsageStrings.WindowSession) && claude.Contains(AccountUsageStrings.WindowWeekly), "Claude \uCE69\uC740 CLI\uAC00 \uBCF4\uACE0\uD55C \uD55C\uB3C4\uB97C \uBCF4\uC5EC\uC57C \uD569\uB2C8\uB2E4.");
            var gemini = cards.First(c => c.Provider == "gemini");
            Require(usageDetails.Children.OfType<ToggleSwitch>().Any(t => !t.IsOn), "\uC9C1\uC811 \uC870\uD68C \uC2A4\uC704\uCE58\uAC00 \uAEBC\uC9C4 \uCC44\uB85C \uBCF4\uC5EC\uC57C \uD569\uB2C8\uB2E4.");
            Require(usageDetails.Children.OfType<TextBlock>().Any(t => t.Text == AccountUsageStrings.SharedLimitsNote), "\uACF5\uC720 \uD55C\uB3C4 \uC124\uBA85\uC774 \uD31D\uC624\uBC84\uC5D0 \uC5C6\uC2B5\uB2C8\uB2E4.");
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
