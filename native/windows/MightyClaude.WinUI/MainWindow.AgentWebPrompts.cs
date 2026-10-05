using MightyClaude.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MightyClaude.WinUI;

public sealed partial class MainWindow
{
    private void RefreshAgentWebPrompts() => DispatcherQueue.TryEnqueue(() =>
    {
        if (closing) return;
        foreach (var pane in views.Values) pane.RenderAgentWebPrompts();
    });

    private sealed partial class PaneView
    {
        /// <summary>The web-open card's accent tint, and the ink wash under its address (M/WebOpenChoicePanel.swift:43, 26).</summary>
        private const double WebOpenTint = 0.055, WebOpenAddressWash = 0.05;
        private readonly StackPanel agentWebPromptHost = new() { Visibility = Visibility.Collapsed };
        private readonly ScrollViewer agentWebPromptScroll = new() { MaxHeight = 320, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Visibility = Visibility.Collapsed };
        private readonly Dictionary<string, FrameworkElement> agentWebPromptCards = [];
        /// <summary>The choices sit over the composer, edge to edge in the pane (M/SessionPaneView.swift:157-159).</summary>
        private void InitializeAgentWebPrompts()
        {
            agentWebPromptScroll.Content = agentWebPromptHost;
            AutomationProperties.SetAutomationId(agentWebPromptHost, "web-open-requests-" + id);
            agentWebPromptHost.Loaded += (_, _) => RenderAgentWebPrompts();
        }
        internal void RenderAgentWebPrompts()
        {
            var b = owner.brushes;
            var requests = owner.agentUrlPrompts.ForPane(id);
            var active = requests.Select(r => r.Id).ToHashSet();
            foreach (var stale in agentWebPromptCards.Keys.Where(key => !active.Contains(key)).ToArray())
            {
                agentWebPromptHost.Children.Remove(agentWebPromptCards[stale]); agentWebPromptCards.Remove(stale);
            }
            foreach (var request in requests)
            {
                if (agentWebPromptCards.ContainsKey(request.Id)) continue;
                // One choice (M/WebOpenChoicePanel.swift:15-46): the accent globe and the 11pt semibold title, the question in 12, the
                // address in 11 mono on a wash at radius 6, the remember box, then the fallback note and the two small buttons.
                var content = new StackPanel { Spacing = 8 };
                var heading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
                heading.Children.Add(new FontIcon { Glyph = "", FontSize = 11, Foreground = b.Brush(DesignToken.Accent), VerticalAlignment = VerticalAlignment.Center });
                heading.Children.Add(new TextBlock { Text = Locale.Get("agentTerminal.urlOpen.dialogTitle"), FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = b.Brush(DesignToken.Ink), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
                content.Children.Add(heading);
                content.Children.Add(new TextBlock { Text = Locale.Get("agentTerminal.urlOpen.dialogMessage"), FontSize = 12, Foreground = b.Brush(DesignToken.Ink), TextWrapping = TextWrapping.Wrap });
                var url = new TextBlock { Text = request.Url.AbsoluteUri, FontFamily = new FontFamily(DesignMetrics.Font.Mono), FontSize = 11, Foreground = b.Brush(DesignToken.Ink), TextWrapping = TextWrapping.Wrap, MaxLines = 3, TextTrimming = TextTrimming.CharacterEllipsis, IsTextSelectionEnabled = true };
                AutomationProperties.SetAutomationId(url, "web-open-url"); ToolTipService.SetToolTip(url, request.Url.AbsoluteUri);
                content.Children.Add(new Border { Child = url, Padding = new Thickness(8, 6, 8, 6), CornerRadius = new CornerRadius(DesignMetrics.Radius.Segment), Background = b.Brush(DesignToken.Ink, WebOpenAddressWash) });
                var remember = new CheckBox { Content = Locale.Get("agentTerminal.urlOpen.rememberToggle"), FontSize = 11, MinHeight = 0, MinWidth = 0, Foreground = b.Brush(DesignToken.Ink) };
                AutomationProperties.SetAutomationId(remember, "web-open-remember"); content.Children.Add(remember);
                var actions = new Grid { ColumnSpacing = 8 };
                actions.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); actions.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); actions.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
                actions.Children.Add(new TextBlock { Text = Locale.Get("agentTerminal.urlOpen.fallbackHint", new Dictionary<string, string> { ["seconds"] = "30" }), FontSize = 10, Foreground = b.Brush(DesignToken.Ink2), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
                Button Answer(string destination, string label, string automationId, bool prominent, int column)
                {
                    var button = SmallButton(label, () => { owner.agentUrlPrompts.Choose(request.Id, destination, remember.IsChecked == true); return Task.CompletedTask; }, prominent);
                    AutomationProperties.SetAutomationId(button, automationId); Grid.SetColumn(button, column); return button;
                }
                actions.Children.Add(Answer("external", Locale.Get("agentTerminal.urlOpen.externalButton"), "web-open-external", false, 1));
                actions.Children.Add(Answer("inApp", Locale.Get("agentTerminal.urlOpen.inAppButton"), "web-open-in-app", true, 2));
                content.Children.Add(actions);
                // The accent tint, edge to edge, with a line above it (M/WebOpenChoicePanel.swift:42-44).
                var card = new Border { Child = content, Padding = new Thickness(12), Background = b.Brush(DesignToken.Accent, WebOpenTint), BorderThickness = new Thickness(0, DesignMetrics.Stroke.Line, 0, 0), BorderBrush = b.Brush(DesignToken.Line) };
                AutomationProperties.SetAutomationId(card, "web-open-request-" + request.Id);
                agentWebPromptCards[request.Id] = card; agentWebPromptHost.Children.Add(card);
            }
            agentWebPromptHost.Visibility = requests.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            agentWebPromptScroll.Visibility = agentWebPromptHost.Visibility;
        }
    }
}
