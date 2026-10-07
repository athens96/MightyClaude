using MightyClaude.Core;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace MightyClaude.WinUI;

public sealed partial class MainWindow
{
    private sealed partial class PaneView
    {
        /// <summary>The session popover's measures (M/SessionInfoViews.swift:188-192): 370 wide with the popover padding, a 32pt header, sections 10 apart and a body of at most 410.</summary>
        private const double SessionInfoWidth = 370, SessionInfoPadding = DesignMetrics.Inset.Popover, SessionInfoHeader = 32, SessionInfoBody = 410;
        /// <summary>The tint under the popover's context block: accent × 0.07 (M/SessionInfoViews.swift:242).</summary>
        internal const double SessionInfoContextTint = 0.07;

        private ContextUsageRing? contextIndicator, contextDetailRing;
        /// <summary>The toolbar's context ring: its circle and 2pt line fill the toolbar-high button (M/SessionInfoViews.swift SessionContextButton).</summary>
        internal const double ContextRingSize = DesignMetrics.Layout.Toolbar - 2;
        private Flyout? sessionInfoFlyout;
        private bool sessionInfoOpen;
        private bool sessionInfoUnloadHooked;
        private bool sessionInfoIdentifiersShown;
        private string? sessionInfoLocale, sessionInfoMarked;
        private TextBlock? sessionInfoHeading, sessionInfoStatus, sessionInfoProvider, sessionInfoModelLabel, sessionInfoTokens, sessionInfoNote, sessionInfoCostLabel, sessionInfoNoUsage, sessionInfoReported, sessionInfoReceived;
        private Grid? sessionInfoMark;
        private FrameworkElement? sessionInfoBeta, sessionInfoSource;
        private Microsoft.UI.Xaml.Shapes.Ellipse? sessionInfoDot;
        private Border? sessionInfoContextBlock;
        private StackPanel? sessionInfoIdentifiers;
        private ComposerGlyph? sessionInfoFolded, sessionInfoUnfolded;
        /// <summary>The session popover's parts the design smoke reads, once it was built.</summary>
        internal (Flyout? Flyout, TextBlock? Status, Border? Context) SessionInfoDesignParts => (sessionInfoFlyout, sessionInfoStatus, sessionInfoContextBlock);
        /// <summary>The open session popover's contents, for the smoke's picture.</summary>
        internal FrameworkElement? SessionInfoSurface => sessionInfoOpen ? sessionInfoFlyout?.Content as FrameworkElement : null;
        private readonly Dictionary<string, (FrameworkElement Host, TextBlock Value)> sessionInfoRows = [];

        private void RefreshContextIndicator()
        {
            var pane = owner.service.Snapshot.Sessions.FirstOrDefault(s => s.Id == id);
            if (pane is null) return;
            var usage = pane.SessionUsage?.Provider == pane.Provider ? pane.SessionUsage : null;
            contextIndicator ??= new(owner.brushes, ContextRingSize); contextIndicator.Update(usage?.ContextPercent);
            context.Content = contextIndicator.View;
            AutomationProperties.SetName(context, Locale.Get("composer.sessionInfo.title") + " · " + contextIndicator.Text);
            RefreshSessionInfo();
        }
        private Task ShowContext() => owner.Act(() =>
        {
            if (owner.dialogOpen) return Task.CompletedTask;
            if (sessionInfoOpen) { sessionInfoFlyout?.Hide(); return Task.CompletedTask; }
            if (sessionInfoFlyout is null || sessionInfoLocale != Locale.LanguagePreference + ":" + System.Globalization.CultureInfo.CurrentUICulture.Name) BuildSessionInfo();
            sessionInfoOpen = true; RefreshSessionInfo(); sessionInfoFlyout!.ShowAt(context);
            return Task.CompletedTask;
        });

        /// <summary>
        /// The session popover behind the context ring (M/SessionInfoViews.swift:211-322): the provider's mark, the
        /// title over the provider's name, and the state at the right; then, in a body that scrolls from 410, the
        /// context ring on its tinted block, the model, the latest request's clock, the workspace and its path, a
        /// rule, the tokens the CLI reported, the cost, the folded session identifiers and where the figures came from.
        /// </summary>
        private void BuildSessionInfo()
        {
            sessionInfoRows.Clear(); sessionInfoMarked = null; sessionInfoIdentifiersShown = false;
            sessionInfoLocale = Locale.LanguagePreference + ":" + System.Globalization.CultureInfo.CurrentUICulture.Name;
            var b = owner.brushes; var ink = b.Brush(DesignToken.Ink); var ink2 = b.Brush(DesignToken.Ink2); var tertiary = b.Tertiary;
            var mono = new FontFamily(DesignMetrics.Font.Mono);
            // The presenter pads its popovers 16 (CardFlyoutStyle); this one is padded 14, so its body takes the difference back.
            var body = new StackPanel { Spacing = DesignMetrics.Spacing.Md, Width = SessionInfoWidth - 2 * SessionInfoPadding, Margin = new Thickness(SessionInfoPadding - PopoverPadding), RequestedTheme = owner.root.RequestedTheme };

            var header = new Grid { Height = SessionInfoHeader, ColumnSpacing = DesignMetrics.Spacing.Sm };
            header.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); header.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            sessionInfoMark = new Grid { VerticalAlignment = VerticalAlignment.Center }; header.Children.Add(sessionInfoMark);
            var names = new StackPanel { Spacing = DesignMetrics.Spacing.Xxs, VerticalAlignment = VerticalAlignment.Center };
            sessionInfoHeading = new TextBlock { FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ink, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
            names.Children.Add(sessionInfoHeading);
            var providerLine = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DesignMetrics.Spacing.Sm };
            sessionInfoProvider = new TextBlock { FontSize = 11, Foreground = ink2, VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetAutomationId(sessionInfoProvider, "session-info-provider-" + id);
            sessionInfoBeta = BetaBadgeView.Create(b);
            providerLine.Children.Add(sessionInfoProvider); providerLine.Children.Add(sessionInfoBeta); names.Children.Add(providerLine);
            Grid.SetColumn(names, 1); header.Children.Add(names);
            // The state: the 5pt dot in its mark colour and the word in 10pt ink2 (M/SessionInfoViews.swift:227-228, M/Palette.swift:151-156).
            var state = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DesignMetrics.Spacing.Xs, VerticalAlignment = VerticalAlignment.Center };
            sessionInfoDot = new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 5, Height = 5, VerticalAlignment = VerticalAlignment.Center };
            sessionInfoStatus = new TextBlock { FontSize = 10, Foreground = ink2, VerticalAlignment = VerticalAlignment.Center };
            state.Children.Add(sessionInfoDot); state.Children.Add(sessionInfoStatus);
            Grid.SetColumn(state, 2); header.Children.Add(state);
            body.Children.Add(header);

            var details = new StackPanel { Spacing = DesignMetrics.Spacing.Sm };
            var contextLine = new Grid { ColumnSpacing = DesignMetrics.Spacing.Md };
            contextLine.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); contextLine.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            contextDetailRing = new(owner.brushes, 42); contextLine.Children.Add(contextDetailRing.View);
            var contextText = new StackPanel { Spacing = DesignMetrics.Spacing.Xs, VerticalAlignment = VerticalAlignment.Center };
            contextText.Children.Add(new TextBlock { Text = Locale.Get("composer.sessionInfo.contextUsage"), FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ink });
            var contextValue = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = ink2 };
            contextText.Children.Add(contextValue); Grid.SetColumn(contextText, 1); contextLine.Children.Add(contextText);
            sessionInfoContextBlock = new Border { Child = contextLine, Padding = new Thickness(DesignMetrics.Spacing.Md), CornerRadius = new CornerRadius(DesignMetrics.Radius.Entry), Background = b.Brush(DesignToken.Accent, SessionInfoContextTint) };
            AutomationProperties.SetAutomationId(sessionInfoContextBlock, "session-info-context-" + id);
            details.Children.Add(sessionInfoContextBlock);
            sessionInfoRows["context"] = (contextLine, contextValue);
            // One row (M/SessionInfoViews.swift:328-341): the name in ink2 and its value at the right, 11pt and 12 apart; a long value wraps under itself.
            TextBlock Add(string key, string label, Panel target, bool monospaced = false)
            {
                var row = new Grid { ColumnSpacing = DesignMetrics.Spacing.Md };
                row.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
                var name = new TextBlock { Text = label, FontSize = 11, Foreground = ink2, TextWrapping = TextWrapping.Wrap }; row.Children.Add(name);
                var value = new TextBlock { FontSize = 11, Foreground = ink, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Right, HorizontalAlignment = HorizontalAlignment.Right, IsTextSelectionEnabled = true };
                if (monospaced) value.FontFamily = mono;
                Grid.SetColumn(value, 1); row.Children.Add(value); target.Children.Add(row); sessionInfoRows[key] = (row, value);
                AutomationProperties.SetAutomationId(value, "session-info-" + key + "-" + id);
                return name;
            }
            sessionInfoModelLabel = Add("model", Locale.Get("composer.sessionInfo.selectedModel"), details);
            // The latest request's clock, as the pane header draws it: the clock mark and the figures in 10pt mono ink2 (M/AgentElapsedView.swift:17-21).
            var elapsedRow = new Grid { ColumnSpacing = DesignMetrics.Spacing.Md };
            elapsedRow.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); elapsedRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            elapsedRow.Children.Add(new TextBlock { Text = Locale.Get("composer.sessionInfo.latestRequestTime"), FontSize = 11, Foreground = ink2 });
            var clock = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DesignMetrics.Spacing.Xs, VerticalAlignment = VerticalAlignment.Center };
            clock.Children.Add(ComposerGlyph.Icon("", 10, 10, 12).Ink(ink2).View);
            var elapsedValue = new TextBlock { FontSize = 10, FontFamily = mono, Foreground = ink2, VerticalAlignment = VerticalAlignment.Center };
            Microsoft.UI.Xaml.Documents.Typography.SetNumeralAlignment(elapsedValue, FontNumeralAlignment.Tabular);
            AutomationProperties.SetAutomationId(elapsedValue, "session-info-elapsed-" + id);
            clock.Children.Add(elapsedValue); Grid.SetColumn(clock, 1); elapsedRow.Children.Add(clock);
            details.Children.Add(elapsedRow); sessionInfoRows["elapsed"] = (elapsedRow, elapsedValue);
            Add("workspace", Locale.Get("composer.sessionInfo.workspace"), details);
            var path = new TextBlock { FontSize = 10, FontFamily = mono, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, Foreground = ink2 };
            details.Children.Add(path); sessionInfoRows["path"] = (path, path); AutomationProperties.SetAutomationId(path, "session-info-path-" + id);
            details.Children.Add(new Border { Height = DesignMetrics.Stroke.Line, Background = b.Brush(DesignToken.Line) });
            sessionInfoTokens = new TextBlock { FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ink }; details.Children.Add(sessionInfoTokens);
            Add("input", Locale.Get("composer.sessionInfo.input"), details, true); Add("output", Locale.Get("composer.sessionInfo.output"), details, true);
            Add("cache-read", Locale.Get("composer.sessionInfo.cacheRead"), details, true); Add("cache-write", Locale.Get("composer.sessionInfo.cacheWrite"), details, true);
            Add("reasoning", Locale.Get("composer.sessionInfo.reasoning"), details, true); Add("total", Locale.Get("composer.sessionInfo.total"), details, true);
            // What the counts include: 10pt in the tertiary ink (M/SessionInfoViews.swift:272-273).
            sessionInfoNote = new TextBlock { Text = Locale.Get("composer.sessionInfo.note"), FontSize = 10, Foreground = tertiary, TextWrapping = TextWrapping.Wrap }; details.Children.Add(sessionInfoNote);
            sessionInfoCostLabel = Add("cost", "", details);
            sessionInfoNoUsage = new TextBlock { Text = Locale.Get("composer.sessionInfo.noUsage"), FontSize = 11, Foreground = ink2, TextWrapping = TextWrapping.Wrap }; details.Children.Add(sessionInfoNoUsage);

            // The session identifiers fold away behind a plain row: the 8pt chevron and "Session ID" in 11pt ink2 (M/SessionInfoViews.swift:282-295).
            sessionInfoFolded = ComposerGlyph.ChevronRight(8, 1.3).Ink(ink2); sessionInfoUnfolded = ComposerGlyph.ChevronDown(8, 1.3).Ink(ink2);
            var chevron = new Grid { Width = 8, Height = 12, VerticalAlignment = VerticalAlignment.Center };
            sessionInfoFolded.View.HorizontalAlignment = sessionInfoUnfolded.View.HorizontalAlignment = HorizontalAlignment.Center;
            chevron.Children.Add(sessionInfoFolded.View); chevron.Children.Add(sessionInfoUnfolded.View);
            var foldWords = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DesignMetrics.Spacing.Xs };
            foldWords.Children.Add(chevron); foldWords.Children.Add(new TextBlock { Text = Locale.Get("composer.sessionInfo.sessionId"), FontSize = 11, Foreground = ink2, VerticalAlignment = VerticalAlignment.Center });
            var fold = FoldButton(foldWords, Locale.Get("composer.sessionInfo.sessionId"));
            AutomationProperties.SetAutomationId(fold, "session-info-identifiers-" + id);
            details.Children.Add(fold);
            sessionInfoIdentifiers = new StackPanel { Spacing = DesignMetrics.Spacing.Sm };
            Add("identity", Locale.Get("composer.sessionInfo.mightySessionId"), sessionInfoIdentifiers, true);
            Add("cli-identity", Locale.Get("composer.sessionInfo.cliSessionId"), sessionInfoIdentifiers, true);
            details.Children.Add(sessionInfoIdentifiers);
            void Fold() { sessionInfoIdentifiersShown = fold.IsChecked == true; ShowSessionIdentifiers(); }
            fold.Checked += (_, _) => Fold(); fold.Unchecked += (_, _) => Fold();
            ShowSessionIdentifiers();

            // Where the figures came from, in 10pt in the tertiary ink (M/SessionInfoViews.swift:296-301).
            var source = new StackPanel { Spacing = DesignMetrics.Spacing.Xxs };
            sessionInfoReported = new TextBlock { FontSize = 10, Foreground = tertiary, TextWrapping = TextWrapping.Wrap };
            sessionInfoReceived = new TextBlock { FontSize = 10, Foreground = tertiary };
            source.Children.Add(sessionInfoReported); source.Children.Add(sessionInfoReceived); details.Children.Add(source); sessionInfoSource = source;

            // The body keeps a line 326 wide and leaves 16 for the scroll bar (M/SessionInfoViews.swift:305-310).
            details.Margin = new Thickness(0, 0, DesignMetrics.Spacing.Lg, 0);
            body.Children.Add(new ScrollViewer { Content = details, MaxHeight = SessionInfoBody, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            AutomationProperties.SetAutomationId(body, "session-info-" + id);
            sessionInfoFlyout = new Flyout { Content = body, Placement = FlyoutPlacementMode.Top, FlyoutPresenterStyle = owner.CardFlyoutStyle };
            sessionInfoFlyout.Closed += (_, _) => sessionInfoOpen = false;
            if (!sessionInfoUnloadHooked) { context.Unloaded += (_, _) => sessionInfoFlyout?.Hide(); sessionInfoUnloadHooked = true; }
        }

        private void ShowSessionIdentifiers()
        {
            if (sessionInfoIdentifiers is null) return;
            sessionInfoIdentifiers.Visibility = sessionInfoIdentifiersShown ? Visibility.Visible : Visibility.Collapsed;
            sessionInfoFolded!.View.Visibility = sessionInfoIdentifiersShown ? Visibility.Collapsed : Visibility.Visible;
            sessionInfoUnfolded!.View.Visibility = sessionInfoIdentifiersShown ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>The colour of a state's dot (M/Palette.swift:94-103): the tone's fill, <c>waitText</c> for the amber and <c>ink3</c> while idle.</summary>
        private static DesignToken MarkToken(DesignTone tone) => tone switch { DesignTone.Wait => DesignToken.WaitText, DesignTone.Idle => DesignToken.Ink3, _ => DesignPalette.FillToken(tone) };

        private void RefreshSessionInfo()
        {
            if (!sessionInfoOpen) return;
            var pane = owner.service.Snapshot.Sessions.FirstOrDefault(s => s.Id == id);
            if (pane is null || owner.closing) { sessionInfoFlyout?.Hide(); return; }
            var b = owner.brushes;
            var usage = pane.SessionUsage?.Provider == pane.Provider ? pane.SessionUsage : null;
            var workspace = owner.service.Snapshot.Workspaces.FirstOrDefault(w => w.Id == pane.WorkspaceId);
            if (sessionInfoFlyout?.Content is FrameworkElement surface) surface.RequestedTheme = owner.root.RequestedTheme;
            if (sessionInfoMarked != pane.Provider)
            {
                // The provider's mark at 20, drawn 1.15 times as large like every mark (M/SessionInfoViews.swift:217, M/ProviderIcon.swift:18).
                sessionInfoMarked = pane.Provider; sessionInfoMark!.Children.Clear(); sessionInfoMark.Children.Add(ProviderMarkView.Create(pane.Provider, 23));
            }
            sessionInfoHeading!.Text = pane.Title; ToolTipService.SetToolTip(sessionInfoHeading, pane.Title);
            sessionInfoProvider!.Text = ProviderMark.Label(pane.Provider);
            sessionInfoBeta!.Visibility = ProviderCatalog.IsBeta(pane.Provider) ? Visibility.Visible : Visibility.Collapsed;
            sessionInfoStatus!.Text = StateLabel(pane.Status); sessionInfoDot!.Fill = b.Brush(MarkToken(StatusGlyph.Tone(pane.Status)));
            AutomationProperties.SetName(sessionInfoHeading, Locale.Get("composer.sessionInfo.session") + " " + pane.Title);
            AutomationProperties.SetName(sessionInfoStatus, Locale.Get("composer.sessionInfo.status") + " " + ProviderCatalog.BetaLabel(pane.Provider, ProviderCatalog.Name(pane.Provider)) + " · " + sessionInfoStatus.Text);
            void Row(string key, string? value)
            {
                if (!sessionInfoRows.TryGetValue(key, out var row)) return;
                row.Host.Visibility = string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;
                if (row.Value.Text != value) row.Value.Text = value ?? "";
            }
            void Say(TextBlock words, string text) { if (words.Text != text) words.Text = text; }
            contextDetailRing!.Update(usage?.ContextPercent);
            var contextText = usage?.ContextUsedTokens is { } used && usage.ContextWindowTokens is { } limit
                ? Locale.Get("composer.sessionInfo.contextTokens", new Dictionary<string, string> { ["used"] = used.ToString("N0"), ["limit"] = limit.ToString("N0") })
                : usage?.ContextUsedTokens is { } usedOnly ? Locale.Get("composer.sessionInfo.contextUsedOnly", new Dictionary<string, string> { ["tokens"] = usedOnly.ToString("N0") })
                : usage?.ContextWindowTokens is { } limitOnly ? Locale.Get("composer.sessionInfo.contextLimitOnly", new Dictionary<string, string> { ["tokens"] = limitOnly.ToString("N0") })
                : Locale.Get("composer.sessionInfo.contextUnavailable");
            Row("context", contextText);
            AutomationProperties.SetName(sessionInfoContextBlock!, Locale.Get("composer.sessionInfo.contextUsage") + " " + contextDetailRing.Text + " · " + contextText);
            Say(sessionInfoModelLabel!, Locale.Get(usage?.Model is null ? "composer.sessionInfo.selectedModel" : "composer.sessionInfo.usedModel"));
            Row("model", usage?.Model is { } model ? ModelLabel.Text(model) : ModelLabel.Selection(pane, owner.Runtime(pane.Provider)?.ModelCatalog ?? ProviderCatalog.Fallback(pane.Provider)));
            Row("elapsed", pane.RunTiming is { IsValid: true } timing ? timing.Label() : null); Row("workspace", workspace?.Name); Row("path", workspace?.Path);
            var tokens = usage is not null && (usage.InputTokens ?? usage.OutputTokens ?? usage.CacheReadTokens ?? usage.CacheWriteTokens ?? usage.ReasoningTokens ?? usage.TotalTokens) is not null;
            sessionInfoTokens!.Visibility = tokens ? Visibility.Visible : Visibility.Collapsed;
            if (tokens) Say(sessionInfoTokens, Locale.Get("composer.sessionInfo.tokenScope", new Dictionary<string, string> { ["scope"] = UsageScope(usage!.TokenScope) }));
            Row("input", usage?.InputTokens?.ToString("N0")); Row("output", usage?.OutputTokens?.ToString("N0"));
            Row("cache-read", usage?.CacheReadTokens?.ToString("N0")); Row("cache-write", usage?.CacheWriteTokens?.ToString("N0"));
            Row("reasoning", usage?.ReasoningTokens?.ToString("N0")); Row("total", usage?.TotalTokens?.ToString("N0"));
            sessionInfoNote!.Visibility = tokens && (usage!.CacheReadTokens ?? usage.CacheWriteTokens ?? usage.ReasoningTokens) is not null ? Visibility.Visible : Visibility.Collapsed;
            var cost = usage?.CostUSD is { } spent && double.IsFinite(spent) && spent >= 0 ? spent : (double?)null;
            if (cost is not null) Say(sessionInfoCostLabel!, Locale.Get("composer.sessionInfo.costWithScope", new Dictionary<string, string> { ["scope"] = UsageScope(usage!.CostScope) }));
            Row("cost", cost is { } amount ? $"${amount:0.####}" : null);
            sessionInfoNoUsage!.Visibility = usage is null || !tokens && usage.CostUSD is null && usage.ContextUsedTokens is null && usage.ContextWindowTokens is null ? Visibility.Visible : Visibility.Collapsed;
            Row("identity", pane.Id); Row("cli-identity", usage?.ProviderSessionId ?? pane.ResumeId);
            sessionInfoSource!.Visibility = usage is null ? Visibility.Collapsed : Visibility.Visible;
            if (usage is not null)
            {
                Say(sessionInfoReported!, Locale.Get("composer.sessionInfo.reported", new Dictionary<string, string> { ["provider"] = ProviderMark.Label(pane.Provider) }));
                ToolTipService.SetToolTip(sessionInfoReported, usage.Source);
                var received = DateTimeOffset.TryParse(usage.UpdatedAt, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var at);
                sessionInfoReceived!.Visibility = received ? Visibility.Visible : Visibility.Collapsed;
                if (received) Say(sessionInfoReceived, Locale.Get("composer.sessionInfo.lastReceived", new Dictionary<string, string> { ["time"] = at.ToLocalTime().ToString("T", System.Globalization.CultureInfo.CurrentCulture) }));
            }
        }
        private static string UsageScope(string? scope) => Locale.Get(scope switch
        {
            "run" => "composer.sessionInfo.scopeRun", "session" => "composer.sessionInfo.scopeSession",
            "response" => "composer.sessionInfo.scopeResponse", _ => "composer.sessionInfo.scopeUnknown",
        });
    }
    private sealed class ContextUsageRing
    {
        internal Grid View { get; }
        private readonly TextBlock label;
        private readonly Microsoft.UI.Xaml.Shapes.Path arc;
        private readonly Ellipse full;
        private readonly double size;
        private readonly DesignBrushes brushes;
        internal string Text => label.Text;
        /// <summary>The track's and the filled arc's strokes, for the smoke.</summary>
        internal Brush? TrackStroke => (View.Children[0] as Ellipse)?.Stroke;
        internal Brush? ArcStroke => arc.Stroke;
        /// <summary>The track under the arc: the primary ink × 0.12 (M/SessionInfoViews.swift:163).</summary>
        internal const double TrackOpacity = 0.12;
        /// <summary>
        /// The context ring (M/SessionInfoViews.swift:151-175): a 2pt track in ink × 0.12 under an accent arc, <c>waitText</c>
        /// from 95%, around the figure in 8.5 semibold (13 on the large ring) — <c>ink</c>, or <c>ink2</c> while unknown.
        /// The Mac strokes the circle of the frame's size on its centre line, so the ring is the size plus the line wide.
        /// </summary>
        internal ContextUsageRing(DesignBrushes brushes, double size)
        {
            this.size = size; this.brushes = brushes; View = new Grid { Width = size + Line, Height = size + Line };
            View.Children.Add(new Ellipse { Stroke = brushes.Brush(DesignToken.Ink, TrackOpacity), StrokeThickness = Line });
            arc = new() { StrokeThickness = Line, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round }; View.Children.Add(arc);
            full = new() { StrokeThickness = Line, Visibility = Visibility.Collapsed }; View.Children.Add(full);
            label = new() { FontSize = size > 32 ? 13 : 8.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }; View.Children.Add(label);
            Microsoft.UI.Xaml.Documents.Typography.SetNumeralAlignment(label, FontNumeralAlignment.Tabular);
        }
        private const double Line = 2;
        internal void Update(double? percent)
        {
            var value = percent is { } p && double.IsFinite(p) ? p : (double?)null;
            label.Text = value is { } shown ? $"{shown:0}%" : "—"; label.Foreground = brushes.Brush(value is null ? DesignToken.Ink2 : DesignToken.Ink);
            var fraction = Math.Clamp((value ?? 0) / 100, 0, 1); var brush = brushes.Brush(value >= 95 ? DesignToken.WaitText : DesignToken.Accent);
            arc.Stroke = full.Stroke = brush; full.Visibility = fraction >= 1 ? Visibility.Visible : Visibility.Collapsed; arc.Visibility = fraction is > 0 and < 1 ? Visibility.Visible : Visibility.Collapsed;
            var radius = size / 2; var center = (size + Line) / 2; var angle = fraction * 2 * Math.PI - Math.PI / 2;
            var figure = new PathFigure { StartPoint = new Point(center, Line / 2) };
            figure.Segments.Add(new ArcSegment { Point = new Point(center + radius * Math.Cos(angle), center + radius * Math.Sin(angle)), Size = new Size(radius, radius), IsLargeArc = fraction > .5, SweepDirection = SweepDirection.Clockwise });
            var geometry = new PathGeometry(); geometry.Figures.Add(figure); arc.Data = geometry;
            AutomationProperties.SetName(View, label.Text);
        }
    }
}
