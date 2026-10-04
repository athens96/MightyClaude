using MightyClaude.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace MightyClaude.WinUI;

public sealed partial class MainWindow
{
    /// <summary>
    /// Paints a button the Mac's <c>.plain</c> way through lightweight styling: <paramref name="normal"/>
    /// at rest and while disabled, <paramref name="hover"/> under the pointer and while pressed, the
    /// <paramref name="border"/> (none when null) in every state, the <paramref name="ink"/> (the
    /// template's when null) in every enabled state and <paramref name="disabledInk"/> (the template's
    /// when null) while disabled. The brushes are the window's shared ones, so a theme toggle recolours
    /// them in place. Call it once, when the button is built and before it enters the tree; a look
    /// that changes at runtime is drawn on the button's content (<see cref="SetResourcesOnce"/>).
    /// </summary>
    internal void PaintPlainButton(Button button, Brush normal, Brush hover, Brush? border = null, Brush? ink = null, Brush? disabledInk = null)
    {
        var edge = border ?? brushes.Transparent;
        var values = new List<(string, object)> { ("ButtonBackground", normal), ("ButtonBackgroundPointerOver", hover), ("ButtonBackgroundPressed", hover), ("ButtonBackgroundDisabled", normal) };
        foreach (var state in new[] { "", "PointerOver", "Pressed", "Disabled" }) values.Add(("ButtonBorderBrush" + state, edge));
        if (ink is not null) foreach (var state in new[] { "", "PointerOver", "Pressed" }) values.Add(("ButtonForeground" + state, ink));
        if (disabledInk is not null) values.Add(("ButtonForegroundDisabled", disabledInk));
        if (!SetResourcesOnce(button, values)) return;
        button.Background = normal; button.BorderBrush = edge;
        if (ink is not null) button.Foreground = ink;
    }

    /// <summary>The lightweight-styling values this window wrote into each element's own resources.</summary>
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<FrameworkElement, Dictionary<string, object>> writtenResources = new();

    /// <summary>
    /// Writes lightweight-styling values straight into the element's own <c>Resources</c> with the
    /// indexer, each key once, as stage 3's sidebar buttons did. A template reads its states'
    /// resources when it is applied, so this belongs where the element is built, before it enters
    /// the tree. Returns whether the element now holds exactly these values.
    /// <para>
    /// A key is never rewritten. WinUI's indexer replaces a key by removing its value <em>object</em>'s
    /// first entry, so with a shared brush under several keys it removed the wrong entry and the add
    /// threw E_DO_RESOURCE_KEYCONFLICT (0x800F0902); and the projection's <c>Add</c> and
    /// <c>ContainsKey</c> ask a lookup that also answers from the global theme resources. So the
    /// keys written are tracked here, not asked of the dictionary: writing the same brushes again
    /// is a no-op, and changing a written key is a programming error (a look that changes at
    /// runtime goes on the content) that throws with its context in a smoke run and is otherwise
    /// traced and ignored, leaving the element as it was.
    /// </para>
    /// </summary>
    internal bool SetResourcesOnce(FrameworkElement element, IReadOnlyList<(string Key, object Value)> values)
    {
        var written = writtenResources.GetOrCreateValue(element);
        var changed = values.Where(v => written.TryGetValue(v.Key, out var old) && !ReferenceEquals(old, v.Value)).Select(v => v.Key).Distinct().ToList();
        if (changed.Count > 0)
        {
            var context = $"Resources of {element.GetType().Name} '{AutomationProperties.GetAutomationId(element)}' are written once and may not change ({string.Join(", ", changed)}); a look that changes must be drawn on the content";
            if (options.SmokeTest) throw new InvalidOperationException(context);
            System.Diagnostics.Trace.TraceError(context);
            return false;
        }
        foreach (var (key, value) in values)
        {
            if (written.ContainsKey(key)) continue;
            element.Resources[key] = value; written[key] = value;
        }
        return true;
    }

    /// <summary>A lightweight-styling value in the element's own resources (null when absent).</summary>
    internal static object? OwnResource(FrameworkElement element, string key) =>
        element.Resources.TryGetValue(key, out var value) ? value : null;

    private sealed partial class PaneView
    {
        /// <summary>Below this width the header keeps its one line by showing the Default | Mighty switch as icons only.</summary>
        private const double NarrowHeader = 420;
        /// <summary>The onStatus wash under the pointer on the slim bar's … button (the agent header uses the subtle wash).</summary>
        private const double SlimHoverOpacity = 0.15;
        private Grid? paneHeader;
        /// <summary>The header's trailing controls: the Default | Mighty switch, the status-line toggle and the … menu, 10 apart (M/SessionPaneView.swift:235-238).</summary>
        private readonly StackPanel paneHeaderControls = new() { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        /// <summary>The pane title: 13 bold, tracking −0.1, in <c>ink</c> (M/SessionPaneView.swift:217).</summary>
        private readonly TextBlock headerTitle = new() { FontSize = DesignMetrics.Type.Title, FontWeight = Microsoft.UI.Text.FontWeights.Bold, CharacterSpacing = -8, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center };
        private Button? paneMenuButton;
        private bool paneHeaderLayoutQueued;
        /// <summary>A terminal pane (a shell, or an agent's terminal) wears the Mac's slim ink bar instead of the agent header.</summary>
        private bool slimHeader;
        /// <summary>The slim bar's kind symbol, kind words and status capsule (null on an agent header).</summary>
        private FontIcon? slimSymbol;
        private TextBlock? slimSubtitle;
        private Border? slimStatusPill;

        /// <summary>
        /// The agent pane's one 34pt line (M/SessionPaneView.swift:208-251): padding l14 r10 on <c>card</c>
        /// with a 1pt <c>line</c> under it; the glyph, the title, the state word, the figures (which give
        /// way first), then the controls. It runs edge to edge over the pane grid's 12pt padding, its top
        /// corners following the card's inner curve.
        /// </summary>
        private Grid BuildPaneHeader()
        {
            const double inset = 12;
            var inner = DesignMetrics.Radius.Pane - DesignMetrics.Stroke.Line;
            var header = new Grid
            {
                Height = DesignMetrics.Layout.PaneHeader, Margin = new Thickness(-inset, -inset, -inset, 0), Padding = new Thickness(14, 0, 10, 0), ColumnSpacing = 8,
                Background = owner.brushes.Brush(DesignToken.Card), BorderBrush = owner.brushes.Brush(DesignToken.Line), BorderThickness = new Thickness(0, 0, 0, DesignMetrics.Stroke.Line),
                CornerRadius = new CornerRadius(inner, inner, 0, 0),
            };
            foreach (var width in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto }) header.ColumnDefinitions.Add(new() { Width = width });
            headerTitle.Foreground = owner.brushes.Brush(DesignToken.Ink); elapsed.Foreground = owner.brushes.Brush(DesignToken.Ink2);
            var kind = Session.Kind;
            slimHeader = kind is "shell" or AgentIOPaneKind.Terminal;
            var parts = slimHeader ? SlimHeaderParts(header, kind) : new[] { headerMark.View, headerTitle, label, elapsed, paneHeaderControls };
            for (var column = 0; column < parts.Length; column++) { Grid.SetColumn(parts[column], column); header.Children.Add(parts[column]); }
            AutomationProperties.SetAutomationId(header, "pane-header-" + id); AutomationProperties.SetAutomationId(label, "pane-status-" + id);
            return paneHeader = header;
        }

        /// <summary>
        /// The slim ink bar over a terminal pane (M/PaneChrome.swift:99-122, M/SessionPaneView.swift:185-190):
        /// 34 high on the <c>idle</c> fill, radius 11, padding h13, set in h8 t8 from the pane's edge; the
        /// terminal symbol 11 semibold, the title 13 bold, the kind 11.5, then the status word in an 11 bold,
        /// 20-high capsule with a 1.5pt edge, all in <c>onStatus</c>, and the … menu. The status mark is still
        /// kept up to date (the header's word and its accessibility follow it) but not shown, as on the Mac.
        /// </summary>
        private FrameworkElement[] SlimHeaderParts(Grid header, string kind)
        {
            const double inset = 12, edge = 8;
            var onStatus = owner.brushes.Brush(DesignToken.OnStatus);
            header.Background = owner.brushes.Brush(DesignToken.Idle); header.BorderThickness = new Thickness(0);
            header.CornerRadius = new CornerRadius(DesignMetrics.Radius.Pane);
            header.Margin = new Thickness(edge - inset, edge - inset, edge - inset, 0); header.Padding = new Thickness(13, 0, 13, 0);
            headerTitle.Foreground = onStatus;
            label.Foreground = onStatus; label.FontSize = DesignMetrics.Type.Pill; label.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
            slimSymbol = new FontIcon { Glyph = "\uE756", FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = onStatus, VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetAccessibilityView(slimSymbol, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
            var words = kind == "shell" ? Locale.Get("dashboard.kind.shell") + " · " + Locale.Get("phone.card.localTerminal") : Locale.Get("dashboard.kind.agentTerminal");
            slimSubtitle = new TextBlock { Text = words, FontSize = DesignMetrics.Type.State, Foreground = onStatus, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center };
            slimStatusPill = new Border
            {
                Child = label, Height = 20, Padding = new Thickness(8, 0, 8, 0), CornerRadius = new CornerRadius(10),
                BorderThickness = new Thickness(DesignMetrics.Stroke.Focus), BorderBrush = onStatus, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
            };
            AutomationProperties.SetAutomationId(slimStatusPill, "pane-status-pill-" + id);
            return [slimSymbol, headerTitle, slimSubtitle, slimStatusPill, paneHeaderControls];
        }

        /// <summary>The slim bar's parts the design smoke reads (all null on an agent pane).</summary>
        internal (Grid? Header, FontIcon? Symbol, TextBlock Title, TextBlock? Subtitle, Border? Pill, TextBlock Word)? SlimHeaderForSmoke =>
            slimHeader ? (paneHeader, slimSymbol, headerTitle, slimSubtitle, slimStatusPill, label) : null;

        /// <summary>
        /// The header's … menu (M/SessionPaneView.swift:280-302): the pane's own menu with Copy placed
        /// before Close behind a separator (decision Q4), disabled while the pane has no logs, as on the
        /// Mac. Returns the Copy item and its separator, which a terminal pane hides together.
        /// </summary>
        private UIElement[] AddPaneMenu()
        {
            var copy = new MenuFlyoutItem { Text = Locale.Get("pane.menu.copyLog") };
            copy.Click += (_, _) => Copy(output.Text);
            AutomationProperties.SetAutomationId(copy, "pane-menu-copy-" + id);
            var separator = new MenuFlyoutSeparator();
            var menu = owner.SessionMenu(id, out var close);
            var at = menu.Items.IndexOf(close);
            menu.Items.Insert(at, separator); menu.Items.Insert(at, copy);
            menu.Opening += (_, _) => copy.IsEnabled = owner.service.Snapshot.Sessions.FirstOrDefault(p => p.Id == id)?.Logs.Count > 0;
            var button = paneMenuButton = new Button
            {
                Width = 22, Height = 24, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0), CornerRadius = new CornerRadius(DesignMetrics.Radius.Segment), BorderThickness = new Thickness(0),
                Content = new FontIcon { Glyph = "\uE712", FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.Bold }, Flyout = menu, VerticalAlignment = VerticalAlignment.Center,
            };
            // On the slim ink bar the menu takes the bar's onStatus ink, with a faint onStatus wash under the pointer.
            if (slimHeader) owner.PaintPlainButton(button, owner.brushes.Transparent, owner.brushes.Brush(DesignToken.OnStatus, SlimHoverOpacity), ink: owner.brushes.Brush(DesignToken.OnStatus));
            else owner.PaintPlainButton(button, owner.brushes.Transparent, owner.brushes.Subtle, ink: owner.brushes.Brush(DesignToken.Ink2));
            var name = Locale.Get("pane.menu.accessibility");
            AutomationProperties.SetName(button, name); ToolTipService.SetToolTip(button, name); AutomationProperties.SetAutomationId(button, "pane-menu-" + id);
            paneHeaderControls.Children.Add(button);
            return [copy, separator];
        }

        private void InitializeResponsiveHeader(Grid header)
        {
            header.Loaded += (_, _) => QueuePaneHeaderLayout();
            header.SizeChanged += (_, _) => QueuePaneHeaderLayout();
            label.SizeChanged += (_, _) => QueuePaneHeaderLayout();
            paneHeaderControls.SizeChanged += (_, _) => QueuePaneHeaderLayout();
        }

        /// <summary>
        /// Keeps the header one line at any width: a narrow pane shows the switch as icons, and the
        /// title trims to what the glyph, the word and the controls leave (the figures take the rest
        /// and trim first, as the Mac's fade). No control ever leaves the pane.
        /// </summary>
        private void QueuePaneHeaderLayout()
        {
            if (paneHeaderLayoutQueued || !QueuePaneAlive) return;
            paneHeaderLayoutQueued = true;
            if (!Container.DispatcherQueue.TryEnqueue(() =>
            {
                paneHeaderLayoutQueued = false;
                if (!QueuePaneAlive || paneHeader is not { IsLoaded: true, ActualWidth: > 0 } header) return;
                ShowModeWords(header.ActualWidth >= NarrowHeader);
                var unbounded = new Size(double.PositiveInfinity, double.PositiveInfinity);
                paneHeaderControls.Measure(unbounded); label.Measure(unbounded);
                // The slim bar's symbol, kind words and capsule stand where the agent header's mark and word do.
                double lead = headerMark.View.Width, word = label.DesiredSize.Width;
                if (slimHeader && slimSymbol is not null && slimSubtitle is not null && slimStatusPill is not null)
                {
                    slimSymbol.Measure(unbounded); slimSubtitle.Measure(unbounded); slimStatusPill.Measure(unbounded);
                    lead = slimSymbol.DesiredSize.Width; word = slimSubtitle.DesiredSize.Width + slimStatusPill.DesiredSize.Width;
                }
                var taken = header.Padding.Left + header.Padding.Right + header.ColumnSpacing * (header.ColumnDefinitions.Count - 1)
                    + lead + word + paneHeaderControls.DesiredSize.Width;
                var maxTitle = Math.Max(0, header.ActualWidth - taken);
                if (Math.Abs(headerTitle.MaxWidth - maxTitle) > .5 || double.IsPositiveInfinity(headerTitle.MaxWidth)) headerTitle.MaxWidth = maxTitle;
            })) paneHeaderLayoutQueued = false;
        }

        /// <summary>
        /// The header is still one 34pt row, every visible control (the … menu, both sides of the
        /// switch, the status-line toggle) lies inside it, and the switch shows its words only when
        /// the pane is not <paramref name="narrow"/>.
        /// </summary>
        private bool HeaderFitsSmoke(bool narrow)
        {
            if (paneHeader is not { ActualWidth: > 0 } header || modeSwitch is null || Math.Abs(header.ActualHeight - DesignMetrics.Layout.PaneHeader) > .5) return false;
            if (ModeWordsShown != !narrow) return false;
            return new FrameworkElement[] { paneMenuButton!, modeDefaultButton!, modeMightyButton!, statusLineToggle }.Where(control => control.Visibility == Visibility.Visible).All(control =>
            {
                if (!control.IsLoaded || control.ActualWidth <= 0 || control.ActualHeight <= 0) return false;
                var start = control.TransformToVisual(header).TransformPoint(new Point());
                var end = control.TransformToVisual(header).TransformPoint(new Point(control.ActualWidth, control.ActualHeight));
                return start.X >= -1 && end.X <= header.ActualWidth + 1 && start.Y >= -1 && end.Y <= header.ActualHeight + 1;
            });
        }
    }
}
