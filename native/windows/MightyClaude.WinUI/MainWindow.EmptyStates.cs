using MightyClaude.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MightyClaude.WinUI;

// The empty states of the design conversion's final stage (.omc/plans/windows-design-conversion.md,
// stage 7): the welcome with no project open, a project with no panes, and an agent pane with no
// conversation yet. Every colour is a shared token brush, so a theme toggle recolours them in place.
public sealed partial class MainWindow
{
    /// <summary>
    /// The welcome while no project is open (M/WorkspaceView.swift:351-366): a 50pt split-window symbol
    /// in <c>accent</c>, the 27pt semibold line, the 14pt <c>ink2</c> explanation, the accent "open a
    /// project folder" button, the shortcut line and, at the foot, what the app is, both 11pt <c>ink3</c>.
    /// </summary>
    private Grid BuildWelcome()
    {
        var page = new Grid();
        page.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        page.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var stack = new StackPanel { Spacing = 18, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(32) };
        stack.Children.Add(new FontIcon { Glyph = "", FontSize = 50, Foreground = brushes.Brush(DesignToken.Accent), HorizontalAlignment = HorizontalAlignment.Center });
        var title = new TextBlock
        {
            Text = Locale.Get("layout.welcome.title"), FontSize = DesignMetrics.Type.Welcome, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = brushes.Brush(DesignToken.Ink), TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
        };
        AutomationProperties.SetAutomationId(title, "welcome-title");
        stack.Children.Add(title);
        stack.Children.Add(new TextBlock
        {
            Text = Locale.Get("layout.welcome.body"), FontSize = 14, LineHeight = 24, Foreground = brushes.Brush(DesignToken.Ink2),
            TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Center,
        });
        var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        label.Children.Add(new FontIcon { Glyph = "", FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
        label.Children.Add(new TextBlock { Text = Locale.Get("menu.openProject"), VerticalAlignment = VerticalAlignment.Center });
        var open = new Button { Content = label, Style = (Style)Application.Current.Resources["AccentButtonStyle"], Padding = new Thickness(16, 7, 16, 7), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) };
        open.Click += async (_, _) => await PickFolder();
        AutomationProperties.SetName(open, Locale.Get("menu.openProject")); AutomationProperties.SetAutomationId(open, "welcome-open-folder");
        stack.Children.Add(open);
        stack.Children.Add(new TextBlock { Text = Locale.Get("layout.welcome.shortcuts"), FontSize = DesignMetrics.Type.Pill, Foreground = brushes.Brush(DesignToken.Ink3), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0) });
        page.Children.Add(stack);
        var footer = new TextBlock { Text = Locale.Get("layout.welcome.footer"), FontSize = DesignMetrics.Type.Pill, Foreground = brushes.Brush(DesignToken.Ink3), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 25) };
        Grid.SetRow(footer, 1); page.Children.Add(footer);
        AutomationProperties.SetAutomationId(page, "welcome");
        return page;
    }

    /// <summary>
    /// A project with no panes (M/WorkspaceView.swift:368-374, the Mac's ContentUnavailableView): a
    /// stacked-panes symbol in <c>ink2</c>, the 20pt semibold line, the 13pt <c>ink2</c> explanation and,
    /// while a project is chosen, the accent button that opens the add-pane menu.
    /// </summary>
    private StackPanel BuildEmptyPanes(bool canAdd)
    {
        var stack = new StackPanel { Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(32) };
        stack.Children.Add(new FontIcon { Glyph = "", FontSize = 36, Foreground = brushes.Brush(DesignToken.Ink2), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 6) });
        stack.Children.Add(new TextBlock { Text = Locale.Get("layout.empty.addPane"), FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = brushes.Brush(DesignToken.Ink), TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap });
        stack.Children.Add(new TextBlock { Text = Locale.Get("layout.empty.addPaneDetail"), FontSize = DesignMetrics.Type.Body, Foreground = brushes.Brush(DesignToken.Ink2), TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 });
        if (canAdd)
        {
            var add = new Button { Content = Locale.Get("layout.empty.addPaneButton"), Flyout = NewSessionMenu(), Style = (Style)Application.Current.Resources["AccentButtonStyle"], HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) };
            AutomationProperties.SetName(add, Locale.Get("layout.empty.addPaneButton")); AutomationProperties.SetAutomationId(add, "empty-panes-add");
            stack.Children.Add(add);
        }
        AutomationProperties.SetAutomationId(stack, "empty-panes");
        return stack;
    }

    private sealed partial class PaneView
    {
        /// <summary>The empty agent pane's words and mark, over the conversation while it has none.</summary>
        private StackPanel? emptyOutput;
        /// <summary>The provider the empty state was drawn for; another provider redraws it.</summary>
        private string? emptyOutputProvider;

        /// <summary>The empty state the design smoke reads (null on a pane that is not an agent's).</summary>
        internal StackPanel? EmptyOutputForSmoke => emptyOutput;

        /// <summary>
        /// The Default conversation's surface (M/SessionPaneView.swift:494-497): concept D sets the
        /// conversation on the raised grey, edge to edge under the header, its words where they were.
        /// The transcript is a stock RichEditBox, so its state resources take the same shared brush,
        /// written once here, while the pane is built and before it enters the tree.
        /// </summary>
        private void PaintTranscriptSurface(Grid grid)
        {
            const double inset = 12;
            var raised = owner.brushes.Brush(DesignToken.CardRaised);
            var view = output.View;
            var top = grid.RowSpacing - (slimHeader ? 2 : 0);
            view.Margin = new Thickness(-inset, -top, -inset, 0); view.Padding = new Thickness(2 * inset, top + inset, 2 * inset, inset);
            view.CornerRadius = new CornerRadius(0);
            owner.SetResourcesOnce(view, [("TextControlBackground", raised), ("TextControlBackgroundPointerOver", raised), ("TextControlBackgroundFocused", raised), ("TextControlBackgroundDisabled", raised)]);
            view.Background = raised;
            AutomationProperties.SetAutomationId(view, "pane-transcript-" + id);
        }

        /// <summary>
        /// The empty agent pane (M/SessionPaneView.swift:502-516): the agent's 24pt mark, "start working
        /// with" its name in 16pt medium <c>ink</c> (with the beta capsule for a beta agent) and the 12pt
        /// <c>ink2</c> explanation, padding 24, over the conversation in the Default view while it is empty.
        /// Not hit-testable, so it never takes a click from the transcript under it.
        /// </summary>
        private void InitializeEmptyOutput(Grid grid)
        {
            if (Session.Kind != "claude") return;
            emptyOutput = new StackPanel { Spacing = 10, Margin = new Thickness(12, 12, 12, 0), VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
            AutomationProperties.SetAutomationId(emptyOutput, "pane-empty-" + id);
            Grid.SetRow(emptyOutput, 1); grid.Children.Add(emptyOutput);
            output.View.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, (_, _) =>
            {
                if (owner.service.Snapshot.Sessions.FirstOrDefault(s => s.Id == id) is { } pane) RefreshEmptyOutput(pane);
            });
        }

        /// <summary>Shows the empty state while the Default conversation shows and has nothing in it, drawn for the pane's agent.</summary>
        private void RefreshEmptyOutput(RunSession pane)
        {
            if (emptyOutput is null) return;
            var shown = output.View.Visibility == Visibility.Visible && pane.Logs.Count == 0;
            emptyOutput.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
            // Drawn again for another agent or another language (the language switches live).
            var drawnFor = pane.Provider + "|" + Locale.LanguagePreference;
            if (!shown || emptyOutputProvider == drawnFor) return;
            emptyOutputProvider = drawnFor;
            emptyOutput.Children.Clear();
            if (ProviderMark.MarkedProvider(pane.Provider) is { } marked)
            {
                var mark = ProviderMarkView.Create(marked, 24);
                mark.HorizontalAlignment = HorizontalAlignment.Left; mark.Margin = new Thickness(0, 0, 0, 5);
                emptyOutput.Children.Add(mark);
            }
            var heading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            heading.Children.Add(new TextBlock
            {
                Text = Locale.Get("pane.empty.agentTitle", new Dictionary<string, string> { ["provider"] = ProviderCatalog.Name(pane.Provider) }),
                FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.Medium, Foreground = owner.brushes.Brush(DesignToken.Ink), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
            });
            if (ProviderCatalog.ShowsBetaBadge(pane)) heading.Children.Add(BetaBadgeView.Create(owner.brushes));
            emptyOutput.Children.Add(heading);
            emptyOutput.Children.Add(new TextBlock { Text = Locale.Get("pane.empty.agentBody"), FontSize = DesignMetrics.Type.Block, LineHeight = 20, Foreground = owner.brushes.Brush(DesignToken.Ink2), TextWrapping = TextWrapping.Wrap });
        }
    }
}
