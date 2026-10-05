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
    /// project folder" button, the shortcut line and, at the foot, what the app is, both 11pt in the tertiary ink (:361, :363).
    /// </summary>
    private Grid BuildWelcome()
    {
        var page = new Grid();
        page.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        page.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var stack = new StackPanel { Spacing = 18, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(32) };
        // rectangle.split.2x1 at 50pt ultraLight: a hairline rounded rectangle, wider than tall, split down its middle.
        var accent = brushes.Brush(DesignToken.Accent);
        var symbol = new Grid { Width = WelcomeSymbolWidth, Height = WelcomeSymbolHeight, HorizontalAlignment = HorizontalAlignment.Center };
        symbol.Children.Add(new Border { BorderBrush = accent, BorderThickness = new Thickness(WelcomeSymbolStroke), CornerRadius = new CornerRadius(8) });
        symbol.Children.Add(new Border { Width = WelcomeSymbolStroke, Background = accent, HorizontalAlignment = HorizontalAlignment.Center });
        AutomationProperties.SetAccessibilityView(symbol, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        stack.Children.Add(symbol);
        // The lines stand on the Mac's: SF's 27pt line is 32 high where Segoe's is 36, its 14pt lines 22 apart
        // (17 and the 5 of line spacing) where Segoe's took 24, its 11pt line 13 where Segoe's is 15. The margins
        // give the difference back, so the 18 between the parts measures as the Mac's (M/WorkspaceView.swift:352-361).
        var title = new TextBlock
        {
            Text = Locale.Get("layout.welcome.title"), FontSize = DesignMetrics.Type.Welcome, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = brushes.Brush(DesignToken.Ink), TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, -3, 0, -1),
        };
        AutomationProperties.SetAutomationId(title, "welcome-title");
        stack.Children.Add(title);
        stack.Children.Add(new TextBlock
        {
            Text = Locale.Get("layout.welcome.body"), FontSize = 14, LineHeight = WelcomeBodyLine, Foreground = brushes.Brush(DesignToken.Ink2), Margin = new Thickness(0, -2, 0, -3),
            TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 520, HorizontalAlignment = HorizontalAlignment.Center,
        });
        // The Mac's prominent button around a 13pt label padded h10 v5 (M/WorkspaceView.swift:359): 32 high, radius 6.
        var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        label.Children.Add(new FontIcon { Glyph = "", FontSize = DesignMetrics.Type.Body, VerticalAlignment = VerticalAlignment.Center });
        label.Children.Add(new TextBlock { Text = Locale.Get("layout.welcome.openProject"), FontSize = DesignMetrics.Type.Body, VerticalAlignment = VerticalAlignment.Center });
        var open = new Button { Content = label, Style = (Style)Application.Current.Resources["AccentButtonStyle"], Height = WelcomeButtonHeight, MinHeight = 0, Padding = new Thickness(18, 0, 18, 0), CornerRadius = new CornerRadius(DesignMetrics.Radius.Segment), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) };
        open.Click += async (_, _) => await PickFolder();
        AutomationProperties.SetName(open, Locale.Get("layout.welcome.openProject")); AutomationProperties.SetAutomationId(open, "welcome-open-folder");
        stack.Children.Add(open);
        stack.Children.Add(new TextBlock { Text = Locale.Get("layout.welcome.shortcuts"), FontSize = DesignMetrics.Type.Pill, Foreground = brushes.Tertiary, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10 - 2, 0, 0) });
        page.Children.Add(stack);
        // The Mac's column keeps its spacing between the lower Spacer and the foot line (18, less the 2 Segoe's line is taller), so the parts stand that much above the middle.
        var footer = new TextBlock { Text = Locale.Get("layout.welcome.footer"), FontSize = DesignMetrics.Type.Pill, Foreground = brushes.Tertiary, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 16, 0, 25) };
        Grid.SetRow(footer, 1); page.Children.Add(footer);
        // The sidebar button at the top-leading corner, where the workspace header has it (padding l24 t14).
        var toggle = NewSidebarToggle("welcome-sidebar-toggle");
        toggle.HorizontalAlignment = HorizontalAlignment.Left; toggle.VerticalAlignment = VerticalAlignment.Top; toggle.Margin = new Thickness(24, 14, 0, 0);
        page.Children.Add(toggle);
        AutomationProperties.SetAutomationId(page, "welcome");
        return page;
    }

    /// <summary>The welcome symbol as the Mac's 50pt ultraLight rectangle.split.2x1 draws: about 62×47 with a 1.5pt line.</summary>
    internal const double WelcomeSymbolWidth = 62, WelcomeSymbolHeight = 47, WelcomeSymbolStroke = 1.5;
    /// <summary>The welcome's 14pt lines stand 22 apart (SF's 17pt line and lineSpacing 5, M/WorkspaceView.swift:357); its button is the Mac's 22pt push button around a label padded v5 (M/WorkspaceView.swift:359).</summary>
    internal const double WelcomeBodyLine = 22, WelcomeButtonHeight = 32;

    /// <summary>
    /// A project with no panes (M/WorkspaceView.swift:368-374, the Mac's ContentUnavailableView): a
    /// stacked-panes symbol in <c>ink2</c>, the 20pt semibold line, the 13pt <c>ink2</c> explanation and,
    /// while a project is chosen, the accent "새 Claude 실행 창" button that adds one at once, as Ctrl+N does.
    /// </summary>
    private StackPanel BuildEmptyPanes(bool canAdd)
    {
        var stack = new StackPanel { Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(32) };
        // square.stack.3d.up: layers stacked in depth (Segoe Fluent Icons MapLayers).
        stack.Children.Add(new FontIcon { Glyph = "", FontSize = 36, Foreground = brushes.Brush(DesignToken.Ink2), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 6) });
        stack.Children.Add(new TextBlock { Text = Locale.Get("layout.empty.addPane"), FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = brushes.Brush(DesignToken.Ink), TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap });
        stack.Children.Add(new TextBlock { Text = Locale.Get("layout.empty.addPaneDetail"), FontSize = DesignMetrics.Type.Body, Foreground = brushes.Brush(DesignToken.Ink2), TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 });
        if (canAdd)
        {
            var title = Locale.Get("workspace.newAgentPane", new Dictionary<string, string> { ["provider"] = ProviderMark.Label(AddPaneMenu.NewPaneShortcutProvider) });
            var add = new Button { Content = title, Style = (Style)Application.Current.Resources["AccentButtonStyle"], FontSize = DesignMetrics.Type.Body, CornerRadius = new CornerRadius(DesignMetrics.Radius.Segment), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) };
            add.Click += async (_, _) => await AddPaneFromShortcut();
            AutomationProperties.SetName(add, title); AutomationProperties.SetAutomationId(add, "empty-panes-add");
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
        /// <summary>Gives the empty state's title the width the pane has for it, so it wraps instead of running out of the pane.</summary>
        private Action? fitEmptyTitle;

        /// <summary>The empty state the design smoke reads (null on a pane that is not an agent's).</summary>
        internal StackPanel? EmptyOutputForSmoke => emptyOutput;
        /// <summary>The empty state's padding (M/SessionPaneView.swift:515 <c>.padding(24)</c>).</summary>
        internal const double EmptyOutputPadding = 24;

        /// <summary>The pane's own inset, which the Mac's output area keeps beside its text view (M/SessionPaneView.swift:497 <c>.padding(.horizontal, 12)</c>).</summary>
        internal const double TranscriptGutter = 12;
        /// <summary>
        /// The Default conversation's surface (M/SessionPaneView.swift:490-499): concept D sets the
        /// conversation on the raised grey, edge to edge under the header and down to where the composer's
        /// region starts, which is its own 12 over the composer card (M/SessionPaneView.swift:660), drawn as
        /// bubbles, reply cards and tool chips (<c>cards: true</c>). Its words stand 12 + 15 in from
        /// the pane's edge: the output area's padding and the text view's own inset (M/AgentTranscriptView.swift:168),
        /// whose 15 over the first line and under the last scroll with the document.
        /// The transcript is a stock RichEditBox, so its state resources take the same shared brush,
        /// written once here, while the pane is built and before it enters the tree.
        /// </summary>
        private void PaintTranscriptSurface(Grid grid)
        {
            var raised = owner.brushes.Brush(DesignToken.CardRaised);
            var view = output.View;
            var top = grid.RowSpacing - (slimHeader ? 2 : 0);
            view.Margin = new Thickness(-TranscriptGutter, -top, -TranscriptGutter, 0);
            view.Padding = new Thickness(TranscriptGutter + TranscriptRtf.Inset, 0, TranscriptGutter + TranscriptRtf.Inset, 0);
            output.Cards = true; output.Brushes = owner.brushes;
            view.CornerRadius = new CornerRadius(0);
            owner.SetResourcesOnce(view, [("TextControlBackground", raised), ("TextControlBackgroundPointerOver", raised), ("TextControlBackgroundFocused", raised), ("TextControlBackgroundDisabled", raised)]);
            view.Background = raised;
            AutomationProperties.SetAutomationId(view, "pane-transcript-" + id);
        }

        /// <summary>
        /// The empty agent pane (M/SessionPaneView.swift:502-516): the agent's 24pt mark, "start working
        /// with" its name in 16pt medium <c>ink</c> (with the beta capsule for a beta agent) and the 12pt
        /// <c>ink2</c> explanation, padding 24 inside the output area's own 12, over the conversation in the
        /// Default view while it is empty: 36 in from the pane's edge and 24 under the header.
        /// Not hit-testable, so it never takes a click from the transcript under it.
        /// </summary>
        private void InitializeEmptyOutput(Grid grid)
        {
            if (Session.Kind != "claude") return;
            // The pane grid already insets its rows by 12 and parts them from the header by its row spacing.
            emptyOutput = new StackPanel { Spacing = 10, Margin = new Thickness(EmptyOutputPadding, EmptyOutputPadding - grid.RowSpacing, EmptyOutputPadding, 0), VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
            AutomationProperties.SetAutomationId(emptyOutput, "pane-empty-" + id);
            emptyOutput.SizeChanged += (_, _) => fitEmptyTitle?.Invoke();
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
            // "Claude와 작업을 시작하세요": the agent's short name, as the Mac says it (M/SessionPaneView.swift:509 ProviderOptions.label).
            var title = new TextBlock
            {
                Text = Locale.Get("pane.empty.agentTitle", new Dictionary<string, string> { ["provider"] = ProviderMark.Label(pane.Provider) }),
                FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.Medium, Foreground = owner.brushes.Brush(DesignToken.Ink), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
            };
            heading.Children.Add(title);
            var badge = ProviderCatalog.ShowsBetaBadge(pane) ? BetaBadgeView.Create(owner.brushes) : null;
            if (badge is not null) heading.Children.Add(badge);
            // A row of a stack never wraps its words; the title takes what the pane leaves beside the badge, as the Mac's HStack does.
            var state = emptyOutput;
            fitEmptyTitle = () => title.MaxWidth = Math.Max(0, state.ActualWidth - (badge is null ? 0 : badge.ActualWidth + heading.Spacing));
            heading.Loaded += (_, _) => fitEmptyTitle?.Invoke();
            emptyOutput.Children.Add(heading);
            emptyOutput.Children.Add(new TextBlock { Text = Locale.Get("pane.empty.agentBody"), FontSize = DesignMetrics.Type.Block, LineHeight = 20, Foreground = owner.brushes.Brush(DesignToken.Ink2), TextWrapping = TextWrapping.Wrap });
        }
    }
}
