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
        private ContextUsageRing? contextIndicator, contextDetailRing;
        private Flyout? sessionInfoFlyout;
        private bool sessionInfoOpen;
        private bool sessionInfoUnloadHooked;
        private string? sessionInfoLocale;
        private TextBlock? sessionInfoHeading, sessionInfoStatus;
        private readonly Dictionary<string, (FrameworkElement Host, TextBlock Value)> sessionInfoRows = [];

        private void RefreshContextIndicator()
        {
            var pane = owner.service.Snapshot.Sessions.FirstOrDefault(s => s.Id == id);
            if (pane is null) return;
            var usage = pane.SessionUsage?.Provider == pane.Provider ? pane.SessionUsage : null;
            contextIndicator ??= new(owner.brushes, 28); contextIndicator.Update(usage?.ContextPercent);
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
        private void BuildSessionInfo()
        {
            sessionInfoRows.Clear();
            sessionInfoLocale = Locale.LanguagePreference + ":" + System.Globalization.CultureInfo.CurrentUICulture.Name;
            var body = new StackPanel { Spacing = 9, Width = 330, RequestedTheme = owner.root.RequestedTheme };
            sessionInfoHeading = new TextBlock { FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
            sessionInfoStatus = new TextBlock { FontSize = 11, Opacity = .65 };
            body.Children.Add(sessionInfoHeading); body.Children.Add(sessionInfoStatus);
            var details = new StackPanel { Spacing = 8 };
            var contextLine = new Grid { ColumnSpacing = 11 };
            contextLine.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); contextLine.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            contextDetailRing = new(owner.brushes, 42); contextLine.Children.Add(contextDetailRing.View);
            var contextText = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            contextText.Children.Add(new TextBlock { Text = Locale.Get("composer.sessionInfo.context"), FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            var contextValue = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, Opacity = .7 };
            contextText.Children.Add(contextValue); Grid.SetColumn(contextText, 1); contextLine.Children.Add(contextText);
            details.Children.Add(new Border { Child = contextLine, Padding = new Thickness(10), CornerRadius = new CornerRadius(10), Background = new SolidColorBrush(Windows.UI.Color.FromArgb(18, 100, 149, 237)) });
            sessionInfoRows["context"] = (contextLine, contextValue);
            void Add(string key, string label, StackPanel target)
            {
                var row = new Grid { ColumnSpacing = 12 };
                row.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
                row.Children.Add(new TextBlock { Text = label, FontSize = 11, Opacity = .65 });
                var value = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Right, IsTextSelectionEnabled = true };
                Grid.SetColumn(value, 1); row.Children.Add(value); target.Children.Add(row); sessionInfoRows[key] = (row, value);
                AutomationProperties.SetAutomationId(value, "session-info-" + key + "-" + id);
            }
            Add("model", Locale.Get("composer.label.model"), details); Add("elapsed", Locale.Get("composer.sessionInfo.elapsed"), details);
            Add("workspace", Locale.Get("composer.sessionInfo.workspace"), details);
            var path = new TextBlock { FontSize = 10, FontFamily = new FontFamily(DesignMetrics.Font.Mono), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, Opacity = .65 };
            details.Children.Add(path); sessionInfoRows["path"] = (path, path); AutomationProperties.SetAutomationId(path, "session-info-path-" + id);
            details.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Windows.UI.Color.FromArgb(40, 135, 135, 135)) });
            Add("scope", Locale.Get("composer.sessionInfo.tokenScope"), details);
            Add("input", Locale.Get("composer.sessionInfo.input"), details); Add("output", Locale.Get("composer.sessionInfo.output"), details);
            Add("cache-read", Locale.Get("composer.sessionInfo.cacheRead"), details); Add("cache-write", Locale.Get("composer.sessionInfo.cacheWrite"), details);
            Add("reasoning", Locale.Get("composer.sessionInfo.reasoning"), details); Add("total", Locale.Get("composer.sessionInfo.total"), details);
            Add("cost", Locale.Get("composer.sessionInfo.cost"), details); Add("cost-scope", Locale.Get("composer.sessionInfo.costScope"), details);
            details.Children.Add(new TextBlock { Text = Locale.Get("composer.sessionInfo.note"), FontSize = 10, Opacity = .55, TextWrapping = TextWrapping.Wrap });
            var identifiers = new StackPanel { Spacing = 6 };
            Add("identity", Locale.Get("composer.sessionInfo.mightySessionId"), identifiers);
            Add("cli-identity", Locale.Get("composer.sessionInfo.cliSessionId"), identifiers);
            var expander = new Expander { Header = Locale.Get("composer.sessionInfo.sessionId"), Content = identifiers, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetAutomationId(expander, "session-info-identifiers-" + id); details.Children.Add(expander);
            Add("source", Locale.Get("composer.sessionInfo.source"), details);
            body.Children.Add(new ScrollViewer { Content = details, MaxHeight = 410, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            AutomationProperties.SetAutomationId(body, "session-info-" + id);
            sessionInfoFlyout = new Flyout { Content = body, Placement = FlyoutPlacementMode.Top };
            sessionInfoFlyout.Closed += (_, _) => sessionInfoOpen = false;
            if (!sessionInfoUnloadHooked) { context.Unloaded += (_, _) => sessionInfoFlyout?.Hide(); sessionInfoUnloadHooked = true; }
        }
        private void RefreshSessionInfo()
        {
            if (!sessionInfoOpen) return;
            var pane = owner.service.Snapshot.Sessions.FirstOrDefault(s => s.Id == id);
            if (pane is null || owner.closing) { sessionInfoFlyout?.Hide(); return; }
            var usage = pane.SessionUsage?.Provider == pane.Provider ? pane.SessionUsage : null;
            var workspace = owner.service.Snapshot.Workspaces.FirstOrDefault(w => w.Id == pane.WorkspaceId);
            if (sessionInfoFlyout?.Content is FrameworkElement surface) surface.RequestedTheme = owner.root.RequestedTheme;
            sessionInfoHeading!.Text = pane.Title;
            sessionInfoStatus!.Text = ProviderCatalog.BetaLabel(pane.Provider, ProviderCatalog.Name(pane.Provider)) + " · " + StateLabel(pane.Status);
            AutomationProperties.SetName(sessionInfoHeading, Locale.Get("composer.sessionInfo.session") + " " + pane.Title);
            AutomationProperties.SetName(sessionInfoStatus, Locale.Get("composer.sessionInfo.status") + " " + sessionInfoStatus.Text);
            void Row(string key, string? value)
            {
                if (!sessionInfoRows.TryGetValue(key, out var row)) return;
                row.Host.Visibility = string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;
                if (row.Value.Text != value) row.Value.Text = value ?? "";
            }
            contextDetailRing!.Update(usage?.ContextPercent);
            var contextText = usage?.ContextUsedTokens is { } used && usage.ContextWindowTokens is { } limit
                ? Locale.Get("composer.sessionInfo.contextTokens", new Dictionary<string, string> { ["used"] = used.ToString("N0"), ["limit"] = limit.ToString("N0") })
                : usage?.ContextUsedTokens is { } usedOnly ? Locale.Get("composer.sessionInfo.contextUsedOnly", new Dictionary<string, string> { ["tokens"] = usedOnly.ToString("N0") })
                : usage?.ContextWindowTokens is { } limitOnly ? Locale.Get("composer.sessionInfo.contextLimitOnly", new Dictionary<string, string> { ["tokens"] = limitOnly.ToString("N0") })
                : Locale.Get("composer.sessionInfo.contextUnavailable");
            Row("context", contextText); Row("model", usage?.Model is { } model ? ModelLabel.Text(model) : ModelLabel.Selection(pane, owner.Runtime(pane.Provider)?.ModelCatalog ?? ProviderCatalog.Fallback(pane.Provider)));
            Row("elapsed", pane.RunTiming?.Label()); Row("workspace", workspace?.Name); Row("path", workspace?.Path);
            Row("scope", usage is null ? null : UsageScope(usage.TokenScope));
            Row("input", usage?.InputTokens?.ToString("N0")); Row("output", usage?.OutputTokens?.ToString("N0"));
            Row("cache-read", usage?.CacheReadTokens?.ToString("N0")); Row("cache-write", usage?.CacheWriteTokens?.ToString("N0"));
            Row("reasoning", usage?.ReasoningTokens?.ToString("N0")); Row("total", usage?.TotalTokens?.ToString("N0"));
            Row("cost", usage?.CostUSD is { } cost ? $"${cost:0.####}" : null); Row("cost-scope", usage?.CostUSD is not null ? UsageScope(usage.CostScope) : null);
            Row("identity", pane.Id); Row("cli-identity", usage?.ProviderSessionId ?? pane.ResumeId);
            Row("source", usage is null ? null : usage.Source + " · " + usage.UpdatedAt);
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
        /// <summary>The context ring (M/SessionInfoViews.swift:157-172): a 2pt <c>line</c> track under an accent arc, <c>waitText</c> from 95%.</summary>
        internal ContextUsageRing(DesignBrushes brushes, double size)
        {
            this.size = size; this.brushes = brushes; View = new Grid { Width = size, Height = size };
            View.Children.Add(new Ellipse { Stroke = brushes.Brush(DesignToken.Line), StrokeThickness = 2, Margin = new Thickness(1) });
            arc = new() { StrokeThickness = 2, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round }; View.Children.Add(arc);
            full = new() { StrokeThickness = 2, Margin = new Thickness(1), Visibility = Visibility.Collapsed }; View.Children.Add(full);
            label = new() { FontSize = size > 32 ? 12 : 8, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }; View.Children.Add(label);
        }
        internal void Update(double? percent)
        {
            var value = percent is { } p && double.IsFinite(p) ? p : (double?)null;
            label.Text = value is { } shown ? $"{shown:0}%" : "—";
            var fraction = Math.Clamp((value ?? 0) / 100, 0, 1); var brush = brushes.Brush(value >= 95 ? DesignToken.WaitText : DesignToken.Accent);
            arc.Stroke = full.Stroke = brush; full.Visibility = fraction >= 1 ? Visibility.Visible : Visibility.Collapsed; arc.Visibility = fraction is > 0 and < 1 ? Visibility.Visible : Visibility.Collapsed;
            var radius = size / 2 - 2; var center = size / 2; var angle = fraction * 2 * Math.PI - Math.PI / 2;
            var figure = new PathFigure { StartPoint = new Point(center, 2) };
            figure.Segments.Add(new ArcSegment { Point = new Point(center + radius * Math.Cos(angle), center + radius * Math.Sin(angle)), Size = new Size(radius, radius), IsLargeArc = fraction > .5, SweepDirection = SweepDirection.Clockwise });
            var geometry = new PathGeometry(); geometry.Figures.Add(figure); arc.Data = geometry;
            AutomationProperties.SetName(View, label.Text);
        }
    }
}
