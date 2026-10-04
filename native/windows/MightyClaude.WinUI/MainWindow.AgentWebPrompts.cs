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
        /// <summary>The web-open card's accent tint (M/WebOpenChoicePanel.swift:43).</summary>
        private const double WebOpenTint = 0.055;
        private readonly StackPanel agentWebPromptHost = new() { Spacing = 5, Visibility = Visibility.Collapsed };
        private readonly ScrollViewer agentWebPromptScroll = new() { MaxHeight = 320, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Visibility = Visibility.Collapsed };
        private readonly Dictionary<string, FrameworkElement> agentWebPromptCards = [];
        private void InitializeAgentWebPrompts(StackPanel composer)
        {
            agentWebPromptScroll.Content = agentWebPromptHost;
            composer.Children.Insert(0, agentWebPromptScroll);
            AutomationProperties.SetAutomationId(agentWebPromptHost, "web-open-requests-" + id);
            agentWebPromptHost.Loaded += (_, _) => RenderAgentWebPrompts();
        }
        internal void RenderAgentWebPrompts()
        {
            var requests = owner.agentUrlPrompts.ForPane(id);
            var active = requests.Select(r => r.Id).ToHashSet();
            foreach (var stale in agentWebPromptCards.Keys.Where(key => !active.Contains(key)).ToArray())
            {
                agentWebPromptHost.Children.Remove(agentWebPromptCards[stale]); agentWebPromptCards.Remove(stale);
            }
            foreach (var request in requests)
            {
                if (agentWebPromptCards.ContainsKey(request.Id)) continue;
                var content = new StackPanel { Spacing = 8 };
                content.Children.Add(new TextBlock { Text = Locale.Get("agentTerminal.urlOpen.dialogTitle"), FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
                content.Children.Add(new TextBlock { Text = Locale.Get("agentTerminal.urlOpen.dialogMessage"), FontSize = 12, TextWrapping = TextWrapping.Wrap });
                var url = new TextBlock { Text = request.Url.AbsoluteUri, FontFamily = new FontFamily(DesignMetrics.Font.Mono), FontSize = 11, TextWrapping = TextWrapping.Wrap, MaxLines = 3, TextTrimming = TextTrimming.CharacterEllipsis, IsTextSelectionEnabled = true };
                AutomationProperties.SetAutomationId(url, "web-open-url"); ToolTipService.SetToolTip(url, request.Url.AbsoluteUri);
                content.Children.Add(new Border { Child = url, Padding = new Thickness(8, 6, 8, 6), CornerRadius = new CornerRadius(DesignMetrics.Radius.Segment), Background = owner.brushes.Subtle });
                var remember = new CheckBox { Content = Locale.Get("agentTerminal.urlOpen.rememberToggle"), FontSize = 11 };
                AutomationProperties.SetAutomationId(remember, "web-open-remember"); content.Children.Add(remember);
                content.Children.Add(new TextBlock { Text = Locale.Get("agentTerminal.urlOpen.fallbackHint", new Dictionary<string, string> { ["seconds"] = "30" }), FontSize = 10, Foreground = owner.brushes.Brush(DesignToken.Ink2), TextWrapping = TextWrapping.Wrap });
                var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
                Button Choice(string destination, string label, string automationId)
                {
                    var button = Button(label, () => { owner.agentUrlPrompts.Choose(request.Id, destination, remember.IsChecked == true); return Task.CompletedTask; });
                    button.FontSize = 11; button.Padding = new Thickness(8, 4, 8, 4); AutomationProperties.SetAutomationId(button, automationId); return button;
                }
                actions.Children.Add(Choice("external", Locale.Get("agentTerminal.urlOpen.externalButton"), "web-open-external"));
                var inApp = Choice("inApp", Locale.Get("agentTerminal.urlOpen.inAppButton"), "web-open-in-app");
                inApp.Style = (Style)Application.Current.Resources["AccentButtonStyle"]; actions.Children.Add(inApp); content.Children.Add(actions);
                // The accent tint with a line above it (M/WebOpenChoicePanel.swift:41-44).
                var card = new Border { Child = content, Padding = new Thickness(12), CornerRadius = new CornerRadius(DesignMetrics.Radius.Row), Background = owner.brushes.Brush(DesignToken.Accent, WebOpenTint), BorderThickness = new Thickness(0, DesignMetrics.Stroke.Line, 0, 0), BorderBrush = owner.brushes.Brush(DesignToken.Line) };
                AutomationProperties.SetAutomationId(card, "web-open-request-" + request.Id);
                agentWebPromptCards[request.Id] = card; agentWebPromptHost.Children.Add(card);
            }
            agentWebPromptHost.Visibility = requests.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            agentWebPromptScroll.Visibility = agentWebPromptHost.Visibility;
        }
    }
}
