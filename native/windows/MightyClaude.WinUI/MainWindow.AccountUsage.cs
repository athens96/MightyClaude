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
    private readonly StackPanel usageChips = new() { Orientation = Orientation.Horizontal, Spacing = DesignMetrics.Spacing.Sm, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel usageDetails = new() { Spacing = DesignMetrics.Spacing.Md, Width = UsagePopoverWidth - 2 * PopoverPadding };
    /// <summary>The usage popover's width over all: its padding 16 is inside the 320 (M/StatusBarUsage.swift:235, the padding before the frame).</summary>
    internal const double UsagePopoverWidth = 320;
    private Button? usageButton;
    private AccountUsageStatus? usage;
    private readonly CancellationTokenSource usageClosing = new();
    private bool usageRefreshQueued, usageDetailsOpen;
    /// <summary>The popover's direct-lookup switch, kept for the smoke (it sits beside its words, not under the popover's stack).</summary>
    private ToggleButton? usageDirectToggle;
    /// <summary>A chip's and a card's provider mark: ProviderIcon(size: 9) and (size: 12) are framed at size × 1.15 (M/ProviderIcon.swift:18, M/StatusBarUsage.swift:176, 243).</summary>
    internal const double UsageChipMark = 9 * 1.15, UsageCardMark = 12 * 1.15;
    /// <summary>A chip's height: the Mac's 10pt line (12) in v3 (M/StatusBarUsage.swift:190); its words are centred in it.</summary>
    internal const double UsageChipHeight = 18;

    /// Built into the status bar after the counts. Hidden until a provider
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
        usageButton = new Button { Content = usageChips, Padding = new Thickness(0), MinWidth = 0, MinHeight = 0, BorderThickness = new Thickness(0), VerticalAlignment = VerticalAlignment.Center, Flyout = flyout, Visibility = Visibility.Collapsed };
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
            // A chip (M/StatusBarUsage.swift:173-193): the provider's mark, then each leading window in 10pt
            // tabular ink2 (waitText once that window is near its limit), 5 apart, h7 v3 on a subtle capsule (18 high).
            var words = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DesignMetrics.Spacing.Xs };
            words.Children.Add(ProviderMarkView.Create(chip.Provider, UsageChipMark));
            foreach (var (text, warning) in UsageChipWords(chip))
            {
                var word = new TextBlock { Text = text, FontSize = DesignMetrics.Type.Small, Foreground = brushes.Brush(warning ? DesignToken.WaitText : DesignToken.Ink2), VerticalAlignment = VerticalAlignment.Center };
                Microsoft.UI.Xaml.Documents.Typography.SetNumeralAlignment(word, Microsoft.UI.Xaml.FontNumeralAlignment.Tabular);
                words.Children.Add(word);
            }
            var border = new Border { Height = UsageChipHeight, CornerRadius = new CornerRadius(UsageChipHeight / 2), Padding = new Thickness(DesignMetrics.Spacing.Sm, 0, DesignMetrics.Spacing.Sm, 0), Background = brushes.Subtle, Child = words };
            AutomationProperties.SetAutomationId(border, "statusbar-usage-" + chip.Provider);
            // The mark stands for the provider's name, which a screen reader still hears.
            AutomationProperties.SetName(border, ProviderCatalog.Name(chip.Provider) + " " + chip.Text);
            usageChips.Children.Add(border);
        }
        usageButton.Visibility = usage.Providers.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (usageDivider is not null) usageDivider.Visibility = usageButton.Visibility;
        if (usageDetailsOpen) RenderAccountUsageDetails();
        // A provider pane that just appeared is worth one read; Claude is only
        // among the targets once the user switched the direct lookup on.
        if (changed) QueueAccountUsageRefresh(false);
    }

    /// The chip's words. Core says what the chip reads; when its windows spell exactly
    /// that text, each window is its own word so it takes its own ink, as on macOS
    /// (a window at 90% or more is amber, the other stays quiet). Otherwise the
    /// chip's one text with Core's own warning.
    private IReadOnlyList<(string Text, bool Warning)> UsageChipWords(AccountUsageChip chip)
    {
        if (usage?.Snapshot(chip.Provider) is { Windows.Count: > 0 } snapshot)
        {
            var words = AccountUsageStatus.Leading(snapshot.Windows)
                .Select(window => (Text: AccountUsageSupport.WindowLabel(window.Kind) + " " + AccountUsageSupport.Percent(window.UsedPercent) + "%", Warning: window.UsedPercent >= 90)).ToList();
            if (words.Count > 0 && string.Join(" ", words.Select(word => word.Text)) == chip.Text) return words;
        }
        return [(chip.Text, chip.Warning)];
    }

    /// The details popover (M/StatusBarUsage.swift:207-237): the pie symbol and title with the
    /// refresh across from them, a card per provider, Claude's direct-lookup switch beside
    /// its words, then the shared-limits note; 12 apart, 320 wide with its padding.
    private void RenderAccountUsageDetails()
    {
        if (usage is null) return;
        var ink = brushes.Brush(DesignToken.Ink); var ink2 = brushes.Brush(DesignToken.Ink2);
        usageDetails.Children.Clear();
        var header = new Grid { ColumnSpacing = DesignMetrics.Spacing.Sm };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var pie = new FontIcon { Glyph = "", FontSize = DesignMetrics.Type.Title, Foreground = ink, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAccessibilityView(pie, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        header.Children.Add(pie);
        var heading = new TextBlock { Text = AccountUsageStrings.Title, FontSize = DesignMetrics.Type.Title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ink, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(heading, 1); header.Children.Add(heading);
        var refresh = Button("↻", () => { QueueAccountUsageRefresh(true); return Task.CompletedTask; });
        // The Mac swaps the arrow for a small spinner while a read runs. The button's hit area reaches into the popover's padding, so the arrow ends at the edge.
        void ShowReading()
        {
            // From the press that asks for the read, not only once it has begun.
            var reading = usage?.Refreshing == true || usageRefreshQueued;
            refresh.Content = reading ? new ProgressRing { IsActive = true, Width = 12, Height = 12, MinWidth = 12, MinHeight = 12 } : new FontIcon { Glyph = "\uE72C", FontSize = 12 };
            refresh.IsEnabled = !reading;
        }
        refresh.Click += (_, _) => ShowReading();
        refresh.MinWidth = 0; refresh.MinHeight = 0; refresh.Padding = new Thickness(DesignMetrics.Spacing.Xs); refresh.Margin = new Thickness(0, -DesignMetrics.Spacing.Xs, -DesignMetrics.Spacing.Xs, -DesignMetrics.Spacing.Xs); refresh.BorderThickness = new Thickness(0); refresh.CornerRadius = new CornerRadius(DesignMetrics.Radius.FileRow);
        PaintPlainButton(refresh, brushes.Transparent, brushes.Subtle, ink: ink2, disabledInk: brushes.Brush(DesignToken.Ink3));
        ShowReading();
        AutomationProperties.SetName(refresh, AccountUsageStrings.RefreshAccessibilityLabel);
        AutomationProperties.SetAutomationId(refresh, "statusbar-usage-refresh");
        ToolTipService.SetToolTip(refresh, AccountUsageStrings.RefreshTooltip);
        Grid.SetColumn(refresh, 2); header.Children.Add(refresh);
        usageDetails.Children.Add(header);

        foreach (var card in usage.Cards())
        {
            // A provider's card on the subtle wash at radius 9, padding 10, its parts 8 apart (M/StatusBarUsage.swift:239-286).
            var panel = new StackPanel { Spacing = DesignMetrics.Spacing.Sm, Padding = new Thickness(DesignMetrics.Spacing.Md), CornerRadius = new CornerRadius(DesignMetrics.Radius.CardButton), Background = brushes.Subtle };
            AutomationProperties.SetAutomationId(panel, "statusbar-usage-card-" + card.Provider);
            // The mark, the agent's name and its beta capsule 6 apart; the account and plan across from them.
            var title = new Grid { ColumnSpacing = DesignMetrics.Spacing.Sm };
            foreach (var width in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star) }) title.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
            title.Children.Add(ProviderMarkView.Create(card.Provider, UsageCardMark));
            var name = new TextBlock { Text = ProviderMark.Label(card.Provider), FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ink, VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(name, card.Title);
            Grid.SetColumn(name, 1); title.Children.Add(name);
            if (ProviderCatalog.IsBeta(card.Provider)) { var beta = BetaBadgeView.Create(brushes); Grid.SetColumn(beta, 2); title.Children.Add(beta); }
            if (card.Account is { Length: > 0 })
            {
                var account = new TextBlock { Text = card.Account, FontSize = DesignMetrics.Type.Small, Foreground = ink2, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(account, 3); title.Children.Add(account);
            }
            panel.Children.Add(title);
            for (var index = 0; index < card.Windows.Count; index++)
            {
                // A window: its name and "12% used" on one line, the bar, when it resets; 3 apart.
                var row = card.Windows[index];
                var window = new StackPanel { Spacing = DesignMetrics.Spacing.Xxs };
                var line = new Grid();
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                line.Children.Add(new TextBlock { Text = row.Label, FontSize = DesignMetrics.Type.Pill, Foreground = ink });
                var used = new TextBlock { Text = row.Used, FontSize = DesignMetrics.Type.Pill, Foreground = ink };
                Microsoft.UI.Xaml.Documents.Typography.SetNumeralAlignment(used, Microsoft.UI.Xaml.FontNumeralAlignment.Tabular);
                Grid.SetColumn(used, 1); line.Children.Add(used);
                window.Children.Add(line);
                var bar = UsageBar(row.Fraction, row.Warning, progress: true);
                AutomationProperties.SetAutomationId(bar, "statusbar-usage-bar-" + card.Provider + "-" + index);
                window.Children.Add(bar);
                if (row.Reset is { Length: > 0 }) window.Children.Add(new TextBlock { Text = row.Reset, FontSize = DesignMetrics.Type.Small, Foreground = ink2 });
                panel.Children.Add(window);
            }
            if (card.Detail is { Length: > 0 }) panel.Children.Add(new TextBlock { Text = card.Detail, FontSize = DesignMetrics.Type.Small, Foreground = ink2, TextWrapping = TextWrapping.Wrap });
            if (card.Note is { Length: > 0 }) panel.Children.Add(new TextBlock { Text = card.Note, FontSize = DesignMetrics.Type.Pill, Foreground = ink2, TextWrapping = TextWrapping.Wrap });
            // The reset rows come before the time of the reading, as on the Mac.
            if (card.Provider == "claude") RenderAccountUsageReset(panel);
            // When it was read: 9pt in the tertiary ink (M/StatusBarUsage.swift:275-276).
            if (card.CheckedAt is { Length: > 0 }) panel.Children.Add(new TextBlock { Text = card.CheckedAt, FontSize = DesignMetrics.Type.Badge, Foreground = brushes.Tertiary });
            usageDetails.Children.Add(panel);
        }

        usageDirectToggle = null;
        if (usage.Providers.Contains("claude"))
        {
            // The switch's words (11pt over a 10pt ink2 explanation, 2 apart) with the switch across from them.
            var row = new Grid { ColumnSpacing = DesignMetrics.Spacing.Sm };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var words = new StackPanel { Spacing = DesignMetrics.Spacing.Xxs, VerticalAlignment = VerticalAlignment.Center };
            words.Children.Add(new TextBlock { Text = AccountUsageStrings.ToggleLabel, FontSize = DesignMetrics.Type.Pill, Foreground = ink, TextWrapping = TextWrapping.Wrap });
            words.Children.Add(new TextBlock { Text = AccountUsageStrings.ToggleDescription, FontSize = DesignMetrics.Type.Small, Foreground = ink2, TextWrapping = TextWrapping.Wrap });
            row.Children.Add(words);
            // The Mac's mini switch (M/StatusBarUsage.swift:229), which is the form's: 26×15 with its 13pt knob, filled when off too.
            var toggle = usageDirectToggle = SettingsSwitch(AccountUsageStrings.ToggleLabel, usage.DirectClaudeLookupEnabled, "statusbar-usage-direct-toggle");
            AutomationProperties.SetHelpText(toggle, AccountUsageStrings.ToggleDescription);
            RoutedEventHandler switched = async (_, _) =>
            {
                var on = toggle.IsChecked == true;
                if (usage.DirectClaudeLookupEnabled == on) return;
                usage.SetDirectClaudeLookup(on);
                await service.UpdateAsync(s => s with { ClaudeDirectUsageLookupEnabled = on });
                RenderAccountUsageDetails();
                if (on) QueueAccountUsageRefresh(true);
            };
            toggle.Checked += switched; toggle.Unchecked += switched;
            Grid.SetColumn(toggle, 1); row.Children.Add(toggle);
            usageDetails.Children.Add(row);
        }
        usageDetails.Children.Add(new TextBlock { Text = AccountUsageStrings.SharedLimitsNote, FontSize = DesignMetrics.Type.Small, Foreground = ink2, TextWrapping = TextWrapping.Wrap });
    }

    /// The read-only reset-entitlement rows. Core decides what they say and whether they
    /// exist at all — an empty list means the direct lookup is off. There is no
    /// reset button and no claim here; the only action is the link, and it is
    /// live in every one of the seven states.
    /// On a card they are one part, 4 apart among themselves (M/StatusBarUsage.swift:297-314).
    private void RenderAccountUsageReset(StackPanel card)
    {
        var rows = usage?.ResetRows() ?? [];
        if (rows.Count == 0) return;
        var section = new StackPanel { Spacing = DesignMetrics.Spacing.Xs };
        RenderAccountUsageReset(section, rows);
        card.Children.Add(section);
    }

    /// The same builder, with the rows handed in: the smoke draws the real
    /// controls from an injected service's rows without touching the switch.
    private void RenderAccountUsageReset(StackPanel panel, IReadOnlyList<AccountResetRow> rows)
    {
        if (rows.Count == 0) return;
        // The line stands 8 from the title under it, as a card's parts do (the rows' own 4, and 4 more).
        panel.Children.Add(new Border { Height = DesignMetrics.Stroke.Line, Background = Separator, Margin = new Thickness(0, 0, 0, DesignMetrics.Spacing.Xs) });
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
            // i18n-exempt-begin: RunAccountUsageSmoke (--smoke-test) failure messages, written to smoke-result.json, not UI.
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
            // The switch sits beside its words in the popover's own row (M/StatusBarUsage.swift:223-229).
            Require(usageDirectToggle is { IsChecked: false } directToggle && usageDetails.Children.OfType<Grid>().Any(row => row.Children.Contains(directToggle)), "\uC9C1\uC811 \uC870\uD68C \uC2A4\uC704\uCE58\uAC00 \uAEBC\uC9C4 \uCC44\uB85C \uBCF4\uC5EC\uC57C \uD569\uB2C8\uB2E4.");
            Require(usageDetails.Children.OfType<TextBlock>().Any(t => t.Text == AccountUsageStrings.SharedLimitsNote), "\uACF5\uC720 \uD55C\uB3C4 \uC124\uBA85\uC774 \uD31D\uC624\uBC84\uC5D0 \uC5C6\uC2B5\uB2C8\uB2E4.");
            // i18n-exempt-end
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
