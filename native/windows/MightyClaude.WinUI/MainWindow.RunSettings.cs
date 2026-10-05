using System.Globalization;
using MightyClaude.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace MightyClaude.WinUI;

public sealed partial class MainWindow
{
    private Style? edgeFlyoutStyle;

    /// <summary>
    /// The card presenter (<see cref="CardFlyoutStyle"/>) with no padding or scrolling of its own, for a popover
    /// whose rules run edge to edge and whose middle scrolls by itself (the run settings).
    /// </summary>
    internal Style EdgeFlyoutStyle
    {
        get
        {
            if (edgeFlyoutStyle is not null) return edgeFlyoutStyle;
            var style = new Style(typeof(FlyoutPresenter)) { BasedOn = CardFlyoutStyle };
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
            style.Setters.Add(new Setter(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled));
            style.Setters.Add(new Setter(ScrollViewer.VerticalScrollModeProperty, ScrollMode.Disabled));
            return edgeFlyoutStyle = style;
        }
    }

    private sealed partial class PaneView
    {
        /// <summary>The run-settings popover's size (M/SettingsViews.swift:124): 360 wide, 445 high, or 500 where the runner has a web-search setting.</summary>
        private const double RunSettingsWidth = 360, RunSettingsHeight = 445, RunSettingsTall = 500;
        /// <summary>A limit's field: the Mac's rounded field at 12pt is 20 high (its 11pt one, the form's, is 19).</summary>
        private const double RunSettingsField = 20;
        private Flyout? runSettingsFlyout;
        private bool runSettingsUnloadHooked, runSettingsHeld;
        /// <summary>The open popover's settings and its Apply, which are off while the pane runs (M/SettingsViews.swift:114, 122).</summary>
        private ContentControl? runSettingsControls;
        private Button? runSettingsApply;
        /// <summary>The open run-settings popover's contents, for the smoke's picture and checks.</summary>
        internal Grid? RunSettingsBody { get; private set; }

        /// <summary>
        /// While the pane is busy the open popover keeps what was typed but changes nothing, as on the Mac, and takes
        /// its settings again when the run ends.
        /// </summary>
        private void HoldRunSettings(bool busy)
        {
            runSettingsHeld = busy;
            if (runSettingsControls is not null) runSettingsControls.IsEnabled = !busy;
            if (runSettingsApply is not null) runSettingsApply.IsEnabled = !busy;
        }

        /// <summary>
        /// The run settings of the … pill (M/SettingsViews.swift:33-125): the title with the provider at the right; the
        /// model and where its list came from; the permission mode in words; for Codex the web search and the shell's
        /// network; for Claude the run limits; and "applies to the next request" with Cancel and Apply. Nothing is
        /// saved until Apply, which checks the limits as the Mac does.
        /// </summary>
        private Task ShowRunSettings(FrameworkElement anchor) => owner.Act(() =>
        {
            if (owner.dialogOpen || !QueuePaneAlive) return Task.CompletedTask;
            runSettingsFlyout?.Hide();
            var b = owner.brushes; var ink = b.Brush(DesignToken.Ink); var ink2 = b.Brush(DesignToken.Ink2); var line = b.Brush(DesignToken.Line);
            var pane = Session; var caps = Capabilities; var runtime = owner.Runtime(pane.Provider);
            var catalog = runtime?.ModelCatalog ?? ProviderCatalog.Fallback(pane.Provider);
            var selected = catalog.Models.FirstOrDefault(m => m.Value == pane.Model);
            var fastMode = pane.Settings.FastMode; var networkAccess = pane.Settings.NetworkAccess; var webSearch = pane.Settings.WebSearch;

            TextBlock Words(string text, double size, Brush brush, bool medium = false) => new() { Text = text, FontSize = size, Foreground = brush, TextWrapping = TextWrapping.Wrap, FontWeight = medium ? Microsoft.UI.Text.FontWeights.Medium : Microsoft.UI.Text.FontWeights.Normal };
            Border Rule() => new() { Height = DesignMetrics.Stroke.Line, Background = line };

            // The title, with the provider and its beta capsule at the right, padding 16.
            var header = new Grid { Padding = new Thickness(16), ColumnSpacing = 8 };
            header.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            var title = Words(Locale.Get("settings.run.title"), 14, ink); title.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold; title.VerticalAlignment = VerticalAlignment.Center; header.Children.Add(title);
            var provider = Words(ProviderMark.Label(pane.Provider), 11, ink2); provider.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(provider, 1); header.Children.Add(provider);
            if (ProviderCatalog.IsBeta(pane.Provider)) { var beta = BetaBadgeView.Create(b); Grid.SetColumn(beta, 2); header.Children.Add(beta); }

            // The settings themselves: blocks 15 apart, padding 16, 12pt unless said otherwise.
            var blocks = new StackPanel { Spacing = 15, Margin = new Thickness(16) };
            var modelBlock = new StackPanel { Spacing = 6 };
            modelBlock.Children.Add(Words(ModelLabel.Selection(pane, catalog), 13, ink, medium: true));
            if (selected?.Description is { Length: > 0 } description) { var said = Words(description, 11, ink2); said.IsTextSelectionEnabled = true; modelBlock.Children.Add(said); }
            var source = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
            source.Children.Add(new FontIcon { Glyph = "", FontSize = 10, Foreground = ink2, VerticalAlignment = VerticalAlignment.Center });
            source.Children.Add(Words(Locale.Get(catalog.Source == "cli" ? "settings.run.modelSourceCli" : "settings.run.modelSourceDefault"), 10, ink2));
            modelBlock.Children.Add(source); blocks.Children.Add(modelBlock);
            blocks.Children.Add(Rule());
            var permissionBlock = new StackPanel { Spacing = 6 };
            permissionBlock.Children.Add(Words(PermissionLabel(pane.Provider, pane.Settings.PermissionMode), 12, ink, medium: true));
            permissionBlock.Children.Add(Words(PermissionDescription(pane.Provider, pane.Settings.PermissionMode), 11, ink2));
            if (pane.Settings.PermissionMode != "fullAccess")
                permissionBlock.Children.Add(Words(Locale.Get(pane.Provider == "claude" || pane.Provider == "codex" && pane.Settings.PermissionMode == "onRequest" ? "settings.run.approvalInApp" : "settings.run.approvalUnsupported"), 11, ink2));
            blocks.Children.Add(permissionBlock);
            if (caps.FastMode) blocks.Children.Add(Words(Locale.Get("settings.run.fastModeNote"), 11, ink2));

            ComboBox? search = null; ToggleButton? network = null; TextBox? turns = null, budget = null;
            if (caps.WebSearch)
            {
                blocks.Children.Add(Rule());
                var block = new StackPanel { Spacing = 7 };
                var row = new Grid { ColumnSpacing = 10 }; row.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
                var label = Words(Locale.Get("settings.run.webSearchLabel"), 12, ink); label.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(label);
                search = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 12, MinHeight = 28 };
                foreach (var (value, key) in new[] { ("default", "settings.run.webSearchDefault"), ("disabled", "settings.run.webSearchOff"), ("cached", "settings.run.webSearchCached"), ("live", "settings.run.webSearchLive") })
                    search.Items.Add(new ComboBoxItem { Content = Locale.Get(key), Tag = value, FontSize = 12 });
                search.SelectedItem = search.Items.OfType<ComboBoxItem>().FirstOrDefault(item => (string)item.Tag == webSearch) ?? search.Items[0];
                AutomationProperties.SetName(search, Locale.Get("settings.run.webSearchLabel")); AutomationProperties.SetAutomationId(search, "run-settings-web-search-" + id);
                Grid.SetColumn(search, 1); row.Children.Add(search); block.Children.Add(row);
                var explained = Words(SearchDescription(webSearch), 11, ink2); block.Children.Add(explained);
                search.SelectionChanged += (_, _) => { webSearch = (search.SelectedItem as ComboBoxItem)?.Tag as string ?? "default"; explained.Text = SearchDescription(webSearch); };
                blocks.Children.Add(block);
            }
            if (caps.NetworkAccess)
            {
                var block = new StackPanel { Spacing = 7 }; var allowed = pane.Settings.PermissionMode is "acceptEdits" or "onRequest";
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
                var label = Words(Locale.Get("settings.run.shellNetworkToggle"), 12, ink); label.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(label);
                // The Mac's small switch (M/SettingsViews.swift:79), the one the settings form and the other popovers draw.
                network = owner.SettingsSwitch(Locale.Get("settings.run.shellNetworkToggle"), networkAccess, "run-settings-network-" + id); network.IsEnabled = allowed;
                network.Checked += (_, _) => networkAccess = true; network.Unchecked += (_, _) => networkAccess = false;
                row.Children.Add(network); block.Children.Add(row);
                block.Children.Add(Words(Locale.Get(pane.Settings.PermissionMode == "fullAccess" ? "settings.run.shellNetworkFullAccess" : allowed ? "settings.run.shellNetworkAcceptEdits" : "settings.run.shellNetworkLocked"), 11, ink2));
                blocks.Children.Add(block);
            }
            if (caps.MaxTurns || caps.MaxBudgetUsd)
            {
                blocks.Children.Add(Rule());
                var block = new StackPanel { Spacing = 9 };
                block.Children.Add(Words(Locale.Get("settings.run.limitsTitle"), 12, ink, medium: true));
                // A limit is the Mac's rounded field in the popover's 12pt (M/SettingsViews.swift:92, 98, 114): the form's field in the body
                // font, 20 high with its words 9 in, as the 12pt field measures on docs/design-system/crops/menu-more-claude-dark.webp.
                TextBox Limit(string key, string? value, string automation)
                {
                    var field = new StackPanel { Spacing = 5 }; field.Children.Add(Words(Locale.Get(key), 11, ink));
                    var box = owner.SettingsField(new TextBox { Text = value ?? "", PlaceholderText = Locale.Get("settings.run.unlimitedPlaceholder") }, 12);
                    box.FontFamily = BodyFont; box.Height = RunSettingsField; box.Padding = new Thickness(8, 1, 8, 1);
                    AutomationProperties.SetName(box, Locale.Get(key)); AutomationProperties.SetAutomationId(box, automation);
                    field.Children.Add(box); block.Children.Add(field); return box;
                }
                if (caps.MaxTurns) turns = Limit("settings.run.maxTurnsLabel", pane.Settings.MaxTurns?.ToString(CultureInfo.InvariantCulture), "run-settings-max-turns-" + id);
                if (caps.MaxBudgetUsd) budget = Limit("settings.run.maxBudgetLabel", pane.Settings.MaxBudgetUsd?.ToString(CultureInfo.InvariantCulture), "run-settings-max-budget-" + id);
                block.Children.Add(Words(Locale.Get("settings.run.limitsNote"), 11, ink2));
                blocks.Children.Add(block);
            }
            // Settings saved for a runner that no longer takes them can only be cleared (M/SettingsViews.swift:104-112).
            if (fastMode && !caps.FastMode || webSearch != "default" && !caps.WebSearch || networkAccess && !caps.NetworkAccess)
            {
                blocks.Children.Add(Rule());
                var notice = Words(Locale.Get("settings.run.unsupportedNotice"), 11, ink2); blocks.Children.Add(notice);
                Button? clear = null;
                clear = SmallButton(Locale.Get("settings.run.unsupportedResetButton"), () =>
                {
                    if (!caps.FastMode) fastMode = false; if (!caps.WebSearch) webSearch = "default"; if (!caps.NetworkAccess) networkAccess = false;
                    notice.Visibility = Visibility.Collapsed; clear!.Visibility = Visibility.Collapsed; return Task.CompletedTask;
                });
                clear.HorizontalAlignment = HorizontalAlignment.Left; blocks.Children.Add(clear);
            }

            var problem = new TextBlock { FontSize = 11, Foreground = b.Brush(DesignToken.ErrText), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(16, 0, 16, 10), Visibility = Visibility.Collapsed };
            // "Applies to the next request", then Cancel and the prominent Apply, the Mac's small buttons (16 high, M/SettingsViews.swift:121-123), padding 14.
            var footer = new Grid { Padding = new Thickness(14), ColumnSpacing = 8 };
            footer.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); footer.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            var applies = Words(Locale.Get("settings.run.appliesNextRequest"), 10, ink2); applies.VerticalAlignment = VerticalAlignment.Center; footer.Children.Add(applies);
            var flyout = new Flyout { Placement = FlyoutPlacementMode.Top, FlyoutPresenterStyle = owner.EdgeFlyoutStyle };
            var cancel = owner.SettingsPush(Button(Locale.Get("settings.run.cancelButton"), () => { flyout.Hide(); return Task.CompletedTask; }), SettingsControlSize.Small);
            AutomationProperties.SetAutomationId(cancel, "run-settings-cancel-" + id); Grid.SetColumn(cancel, 1); footer.Children.Add(cancel);
            Task Save() => owner.Act(async () =>
            {
                if (!QueuePaneAlive || runSettingsHeld || Session.Status == "running") return;
                var current = Session;
                var setting = current.Settings with { FastMode = fastMode, WebSearch = webSearch, NetworkAccess = current.Settings.PermissionMode is ("acceptEdits" or "onRequest") && networkAccess, MaxTurns = null, MaxBudgetUsd = null };
                void Refuse(string message) { problem.Text = message; problem.Visibility = Visibility.Visible; }
                if (turns is not null && turns.Text.Trim() is { Length: > 0 } wanted)
                {
                    if (!int.TryParse(wanted, NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count is < 1 or > 1000) { Refuse(Locale.Get("settings.run.maxTurnsError")); return; }
                    setting = setting with { MaxTurns = count };
                }
                if (budget is not null && budget.Text.Trim() is { Length: > 0 } cap)
                {
                    if (!double.TryParse(cap, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount) || !double.IsFinite(amount) || amount <= 0 || amount > 10_000) { Refuse(Locale.Get("settings.run.maxBudgetError")); return; }
                    setting = setting with { MaxBudgetUsd = amount };
                }
                try { _ = new StartRunRequest(current.Id, current.WorkspaceId, current.Kind, "validation", [], current.Model, current.Provider, setting).Validate(); }
                catch (Exception ex) { Refuse(ex.Message); return; }
                await Change(p => p with { Settings = setting });
                flyout.Hide(); Refresh(); input.Focus(FocusState.Programmatic);
            });
            var apply = owner.SettingsPush(Button(Locale.Get("settings.run.applyButton"), Save), SettingsControlSize.Small, prominent: true);
            AutomationProperties.SetAutomationId(apply, "run-settings-apply-" + id); Grid.SetColumn(apply, 2); footer.Children.Add(apply);

            var body = new Grid { Width = RunSettingsWidth, Height = caps.WebSearch ? RunSettingsTall : RunSettingsHeight, RequestedTheme = owner.root.RequestedTheme };
            foreach (var height in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto, GridLength.Auto }) body.RowDefinitions.Add(new() { Height = height });
            var controls = new ContentControl { Content = blocks, IsTabStop = false, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Top };
            // The settings scroll with no bar of their own: the Mac's shows none while they rest.
            var parts = new FrameworkElement[] { header, Rule(), new ScrollViewer { Content = controls, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Disabled }, problem, Rule(), footer };
            for (var row = 0; row < parts.Length; row++) { Grid.SetRow(parts[row], row); body.Children.Add(parts[row]); }
            AutomationProperties.SetAutomationId(body, "run-settings-" + id); AutomationProperties.SetName(body, Locale.Get("settings.run.title"));
            // Enter applies, as the Mac's default action (M/SettingsViews.swift:122); Esc closes the popover, as its cancel action. A button or a picker that has the focus takes its own Enter first.
            body.KeyDown += async (_, args) => { if (args.Key != Windows.System.VirtualKey.Enter || args.Handled) return; args.Handled = true; await Save(); };
            flyout.Content = body; runSettingsFlyout = flyout; RunSettingsBody = body; runSettingsControls = controls; runSettingsApply = apply;
            // The popover opens with no field in focus, as the Mac's does: the focus goes to Apply without a ring, where Enter still applies.
            flyout.Opened += (_, _) => { if (apply.IsEnabled) apply.Focus(FocusState.Programmatic); };
            flyout.Closed += (_, _) => { if (ReferenceEquals(runSettingsFlyout, flyout)) { runSettingsFlyout = null; RunSettingsBody = null; runSettingsControls = null; runSettingsApply = null; runSettingsHeld = false; } };
            // A pane that leaves the window takes its popover with it. A new layout only moves the pane (unloaded and
            // loaded again at once): the popover and what was typed in it stay, so the pane is looked at a moment later.
            if (!runSettingsUnloadHooked) { Container.Unloaded += (_, _) => owner.DispatcherQueue.TryEnqueue(() => { if (!Container.IsLoaded) runSettingsFlyout?.Hide(); }); runSettingsUnloadHooked = true; }
            flyout.ShowAt(anchor);
            return Task.CompletedTask;
        });

        /// <summary>Closes the run-settings popover, if it is open.</summary>
        internal void HideRunSettings() => runSettingsFlyout?.Hide();

        /// <summary>What a web-search choice does (M/SettingsViews.swift:368-375).</summary>
        private static string SearchDescription(string value) => Locale.Get(value switch
        {
            "disabled" => "settings.run.webSearchDescriptionDisabled", "cached" => "settings.run.webSearchDescriptionCached",
            "live" => "settings.run.webSearchDescriptionLive", _ => "settings.run.webSearchDescriptionDefault",
        });

        /// <summary>A permission mode in words, as the run settings say it (M/SettingsViews.swift:348-366).</summary>
        private static string PermissionDescription(string provider, string mode)
        {
            if (provider == "claude") return Locale.Get(mode switch { "plan" => "permission.claude.plan", "acceptEdits" => "permission.claude.acceptEdits", "auto" => "permission.claude.auto", "fullAccess" => "permission.claude.fullAccess", _ => "permission.claude.default" });
            if (mode == "fullAccess") return Locale.Get("permission.other.fullAccess");
            if (provider == "codex") return Locale.Get(mode == "onRequest" ? "permission.codex.onRequest" : mode == "acceptEdits" ? "permission.codex.acceptEdits" : "permission.codex.default");
            return Locale.Get(mode switch { "plan" => "permission.other.plan", "acceptEdits" => "permission.other.acceptEdits", _ => "permission.other.default" });
        }
    }
}
