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

    /// <summary>The kinds whose tab group draws the slim bar over the pane, which then shows no header of its own (M/PaneDockView.swift:171, 178-180).</summary>
    private static readonly HashSet<string> GroupSlimHeaderKinds = ["browser", AgentIOPaneKind.Terminal, AgentIOPaneKind.Browser, FilePaneKind.Kind];
    /// <summary>The least room between a slim bar's words and what trails them (M/PaneChrome.swift:112).</summary>
    private const double SlimBarSpacer = 6;
    /// <summary>
    /// The body font (SF Pro's stand-in) for words that stand outside a stock control. Those do not take
    /// the controls' font resource: left alone they follow the system language, which on a Korean system
    /// is Malgun Gothic for Latin letters too, and draws a path's backslash as a won sign.
    /// </summary>
    private static readonly FontFamily BodyFont = new(DesignMetrics.Font.Body);
    /// <summary>Each group's slim bar and its parts, by the session it stands over (read by the paneChromeDesign smoke).</summary>
    private readonly Dictionary<string, (Grid Bar, FontIcon Symbol, TextBlock Title, TextBlock Subtitle)> groupSlimHeaders = [];

    /// <summary>
    /// The symbol of a pane that is not a conversation (the Mac's paneSymbol, M/DashboardView.swift:81-87):
    /// Segoe Fluent Icons CommandPrompt, Globe or Folder. <paramref name="macSize"/> is the SF Symbol's
    /// font size; the glyph is sized to draw as large as that symbol does (SF's terminal at 11 draws
    /// 12.7×10.2pt, its folder 12.3×10) and sits in a slot as wide as the symbol's own, so what follows
    /// it starts where it does on the Mac.
    /// </summary>
    private static FontIcon PaneSymbol(string kind, double macSize, Brush ink, bool semibold = false)
    {
        var (glyph, scale, slot) = kind switch
        {
            "browser" or AgentIOPaneKind.Browser => ("\uE774", 1.04, 1.27),
            FilePaneKind.Kind => ("\uE8B7", 1.12, 1.36),
            _ => ("\uE756", 1.2, 1.4),
        };
        var icon = new FontIcon { Glyph = glyph, FontSize = Math.Round(macSize * scale * 2) / 2, Width = Math.Round(macSize * slot), Foreground = ink, VerticalAlignment = VerticalAlignment.Center };
        if (semibold) icon.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        AutomationProperties.SetAccessibilityView(icon, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        return icon;
    }

    /// <summary>A pane's kind in words, as its slim bar says it (the Mac's DashboardText.kind, M/DashboardView.swift:52-62).</summary>
    private static string PaneKindWords(string kind) => kind switch
    {
        "claude" => Locale.Get("dashboard.kind.agent"),
        "shell" => Locale.Get("dashboard.kind.shell"),
        "browser" => Locale.Get("browser.tab.title"),
        AgentIOPaneKind.Terminal => Locale.Get("dashboard.kind.agentTerminal"),
        AgentIOPaneKind.Browser => Locale.Get("dashboard.kind.agentBrowser"),
        FilePaneKind.Kind => Locale.Get("files.pane.title"),
        _ => kind,
    };

    /// <summary>A slim bar's fill and shape: <c>idle</c>, radius 11, 34 high, padding h13 (M/PaneChrome.swift:117-118).</summary>
    private static void PaintSlimBar(Grid bar, DesignBrushes brushes)
    {
        bar.Height = DesignMetrics.Layout.PaneHeader; bar.Padding = new Thickness(13, 0, 13, 0);
        bar.Background = brushes.Brush(DesignToken.Idle); bar.BorderThickness = new Thickness(0);
        bar.CornerRadius = new CornerRadius(DesignMetrics.Radius.Pane);
    }

    /// <summary>A slim bar's kind words: 11.5, trimmed at the end (M/PaneChrome.swift:111).</summary>
    private static TextBlock SlimBarWords(string text, Brush ink) =>
        new() { Text = text, FontFamily = BodyFont, FontSize = DesignMetrics.Type.State, Foreground = ink, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center };

    /// <summary>
    /// The slim ink bar a tab group draws over a pane that is not a conversation (M/PaneChrome.swift:99-141,
    /// M/PaneDockView.swift:178-180): the kind's symbol 11 semibold, the title 13 bold and the kind in words
    /// 11.5, 8 apart in <c>onStatus</c> on the <c>idle</c> fill, set in h8 t8 b2. It carries no status and no menu.
    /// </summary>
    private Grid BuildGroupSlimHeader(RunSession session)
    {
        var onStatus = brushes.Brush(DesignToken.OnStatus);
        var bar = new Grid { Margin = new Thickness(8, 8, 8, 2) };
        PaintSlimBar(bar, brushes);
        var symbol = PaneSymbol(session.Kind, 11, onStatus, semibold: true);
        var title = new TextBlock { Text = session.Title, FontFamily = BodyFont, FontSize = DesignMetrics.Type.Title, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = onStatus, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center };
        var subtitle = SlimBarWords(PaneKindWords(session.Kind), onStatus);
        var line = new PaneHeaderLine();
        line.Add(symbol, PaneHeaderLine.Role.Fixed); line.Add(title, PaneHeaderLine.Role.Shrink); line.Add(subtitle, PaneHeaderLine.Role.Shrink); line.Add(new Border(), PaneHeaderLine.Role.Fill, SlimBarSpacer);
        bar.Children.Add(line);
        AutomationProperties.SetAutomationId(bar, "pane-slim-header-" + session.Id);
        groupSlimHeaders[session.Id] = (bar, symbol, title, subtitle);
        return bar;
    }

    private sealed partial class PaneView
    {
        /// <summary>
        /// The header width the timeline smoke waits to see again after its 320 px check. The header has
        /// no narrow mode of its own: the Mac's switch keeps its words at every width (M/SessionPaneView.swift:312-315).
        /// </summary>
        private const double NarrowHeader = 420;
        /// <summary>The onStatus wash under the pointer on the slim bar's … button (the agent header uses the subtle wash).</summary>
        private const double SlimHoverOpacity = 0.15;
        /// <summary>Figures that run out of room fade over their last 18pt instead of ending in an ellipsis (M/PaneChrome.swift:66-70).</summary>
        private const double FiguresFade = 18;
        private Grid? paneHeader;
        /// <summary>The header's one line, whose parts give way in the Mac's order (<see cref="PaneHeaderLine"/>).</summary>
        private PaneHeaderLine? headerLine;
        /// <summary>
        /// The header's trailing controls, 10 apart behind 6 (M/SessionPaneView.swift:235-238, 250-278): the
        /// Default | Mighty switch, the plugin button, the agent's terminal button and the … menu.
        /// </summary>
        private readonly StackPanel paneHeaderControls = new() { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        /// <summary>The pane title: 13 bold, tracking −0.1, in <c>ink</c> (M/SessionPaneView.swift:217).</summary>
        private readonly TextBlock headerTitle = new() { FontFamily = BodyFont, FontSize = DesignMetrics.Type.Title, FontWeight = Microsoft.UI.Text.FontWeights.Bold, CharacterSpacing = -8, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center };
        private Button? paneMenuButton, pluginButton, terminalButton;
        /// <summary>The figures after the clock, " · 41% · $0.38 · 도구 12", in the clock's own mono (M/PaneChrome.swift:61-62); <c>elapsed</c> holds the clock.</summary>
        private readonly TextBlock figuresRest = new() { FontSize = DesignMetrics.Type.Mono, FontFamily = new FontFamily(DesignMetrics.Font.Mono), TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center };
        private Grid? figuresHost;
        private StackPanel? figuresWords;
        private LinearGradientBrush? figuresFadeBrush;
        private string figuresHelp = "";
        private ToolTip? figuresTip;
        /// <summary>The pane's kind, which never changes: read once when the header is built, for what the card and the header draw per kind.</summary>
        private string paneKind = "";
        /// <summary>A shell pane wears the Mac's slim ink bar inside its card instead of the agent header (M/SessionPaneView.swift:184-190).</summary>
        private bool slimHeader;
        /// <summary>The slim bar's kind symbol, kind words and status capsule (null on an agent header).</summary>
        private FontIcon? slimSymbol;
        private TextBlock? slimSubtitle;
        private Border? slimStatusPill;

        /// <summary>
        /// The agent pane's one 34pt line (M/SessionPaneView.swift:208-251): padding l14 r10 on <c>card</c>
        /// with a 1pt <c>line</c> under it; the glyph, the title, the state word, the figures (which give
        /// way first), then the controls, 8 apart. It runs edge to edge over the pane grid's 12pt padding,
        /// its top corners following the card's inner curve. A pane that is not a conversation shows none
        /// of its own: its tab group draws the slim bar over it (M/PaneDockView.swift:178-180).
        /// </summary>
        private Grid BuildPaneHeader()
        {
            const double inset = 12;
            var inner = DesignMetrics.Radius.Pane - DesignMetrics.Stroke.Line;
            var header = new Grid
            {
                Height = DesignMetrics.Layout.PaneHeader, Margin = new Thickness(-inset, -inset, -inset, 0), Padding = new Thickness(14, 0, 10, 0),
                Background = owner.brushes.Brush(DesignToken.Card), BorderBrush = owner.brushes.Brush(DesignToken.Line), BorderThickness = new Thickness(0, 0, 0, DesignMetrics.Stroke.Line),
                CornerRadius = new CornerRadius(inner, inner, 0, 0),
            };
            headerTitle.Foreground = owner.brushes.Brush(DesignToken.Ink); elapsed.Foreground = figuresRest.Foreground = owner.brushes.Brush(DesignToken.Ink2);
            label.FontFamily = BodyFont;
            var kind = paneKind = Session.Kind;
            slimHeader = kind == "shell";
            var line = headerLine = new PaneHeaderLine();
            if (slimHeader) SlimHeaderParts(header, line);
            else
            {
                line.Add(headerMark.View, PaneHeaderLine.Role.Fixed); line.Add(headerTitle, PaneHeaderLine.Role.Shrink); line.Add(label, PaneHeaderLine.Role.Soft);
                line.Add(BuildHeaderFigures(), PaneHeaderLine.Role.Fill); line.Add(paneHeaderControls, PaneHeaderLine.Role.Fixed);
            }
            header.Children.Add(line);
            if (GroupSlimHeaderKinds.Contains(kind)) header.Visibility = Visibility.Collapsed;
            AutomationProperties.SetAutomationId(header, "pane-header-" + id); AutomationProperties.SetAutomationId(label, "pane-status-" + id);
            return paneHeader = header;
        }

        /// <summary>
        /// The figures (M/PaneChrome.swift:50-75): the clock and what follows it in 11 mono <c>ink2</c>, never
        /// trimmed. The line clips them where their room ends and their last 18pt fade into the card.
        /// </summary>
        private Grid BuildHeaderFigures()
        {
            elapsed.TextTrimming = TextTrimming.None;
            var words = figuresWords = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
            words.Children.Add(elapsed); words.Children.Add(figuresRest);
            figuresFadeBrush = new LinearGradientBrush { StartPoint = new Point(0, .5), EndPoint = new Point(1, .5) };
            figuresFadeBrush.GradientStops.Add(new GradientStop { Offset = 0 }); figuresFadeBrush.GradientStops.Add(new GradientStop { Offset = 1 });
            PaintHeaderFade();
            var fade = new Microsoft.UI.Xaml.Shapes.Rectangle { Width = FiguresFade, HorizontalAlignment = HorizontalAlignment.Right, IsHitTestVisible = false, Fill = figuresFadeBrush };
            AutomationProperties.SetAccessibilityView(fade, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
            var host = figuresHost = new Grid(); host.Children.Add(words); host.Children.Add(fade);
            host.SizeChanged += (_, args) => host.Clip = new RectangleGeometry { Rect = new Rect(0, 0, args.NewSize.Width, args.NewSize.Height) };
            return host;
        }

        /// <summary>
        /// The fade over the figures' last 18pt runs from clear to the header's <c>card</c>. Gradient stops
        /// hold colours rather than the shared brushes, so every render gives them the theme's card again.
        /// </summary>
        private void PaintHeaderFade()
        {
            if (figuresFadeBrush is null) return;
            var card = owner.brushes.Palette[DesignToken.Card];
            figuresFadeBrush.GradientStops[0].Color = DesignBrushes.ToColor(card, 0); figuresFadeBrush.GradientStops[1].Color = DesignBrushes.ToColor(card);
        }

        /// <summary>
        /// What the agent header shows beyond its mark, title and word, redrawn with them on every refresh
        /// and on the one-second tick: the count of what waits on the user in the word ("응답 대기 2",
        /// M/SessionPaneView.swift:222), the figures, and which of the plugin and terminal buttons show.
        /// </summary>
        private void RefreshHeaderLine(RunSession pane)
        {
            if (slimHeader || figuresHost is null) return;
            if (PendingRequests is var pending and > 0) label.Text = Locale.Get("phone.card.attention", new Dictionary<string, string> { ["count"] = pending.ToString(System.Globalization.CultureInfo.InvariantCulture) });
            // The turn is over while its background agents still run (M/SessionPaneView.swift agentHeader).
            else if (pane.Status == "running" && PlanCardSupport.BackgroundStatus(pane.BackgroundWork) is { } waiting) label.Text = waiting;
            RefreshHeaderFigures(pane);
            if (pluginButton is not null)
            {
                // Plugins: the Mighty view of a Claude or Codex pane (M/SessionPaneView.swift:258-267).
                var shown = MightyGraphViewModel.ShowsModeSwitch(pane) && pane.AgentViewMode == "mighty" && pane.Provider is "claude" or "codex";
                pluginButton.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
                var name = PluginStrings.TitleTemplate.Replace("{provider}", CliUpdateService.ProviderLabel(pane.Provider));
                if (shown && AutomationProperties.GetName(pluginButton) != name) { AutomationProperties.SetName(pluginButton, name); ToolTipService.SetToolTip(pluginButton, name); }
            }
            // The terminal button: once the agent has run a command in its own terminal (M/SessionPaneView.swift:269-276).
            if (terminalButton is not null) terminalButton.Visibility = owner.agentTerminals.ContainsKey(id) ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>
        /// The figures a pane has, in the Mac's order (C/PaneHero.swift:21-30, M/PaneChrome.swift:58-74): the
        /// clock of the latest request (drawn by <c>elapsed</c>), how full the context is, what the session
        /// has cost and the tool calls since the latest request, the last one with its name ("도구 12").
        /// A figure the pane has no number for is left out. The tooltip and the accessible name carry
        /// every value with its name, then the agent and its model; the tooltip then says what the clock counts.
        /// </summary>
        private void RefreshHeaderFigures(RunSession pane)
        {
            var invariant = System.Globalization.CultureInfo.InvariantCulture;
            static string Named(string key, string value) => Locale.Get("pane.hero.figure", new Dictionary<string, string> { ["label"] = Locale.Get(key), ["value"] = value });
            var shown = new List<string>(); var named = new List<string>();
            // The clock is the figure RefreshElapsed writes into `elapsed`.
            var timed = pane.Kind != "shell" && pane.RunTiming is not null;
            if (timed) named.Add(Named("phone.session.hero.elapsed", pane.RunTiming!.Label()));
            // A reading from another provider (the pane was switched) does not describe this one.
            var usage = pane.SessionUsage?.Provider == pane.Provider ? pane.SessionUsage : null;
            if (usage?.ContextPercent is { } percent && double.IsFinite(percent))
            {
                var value = Math.Round(Math.Clamp(percent, 0, 100), MidpointRounding.AwayFromZero).ToString(invariant) + "%";
                shown.Add(value); named.Add(Named("phone.session.hero.context", value));
            }
            if (usage?.CostUSD is { } cost && double.IsFinite(cost) && cost >= 0)
            {
                var value = "$" + cost.ToString("0.00", invariant);
                shown.Add(value); named.Add(Named("phone.session.hero.cost", value));
            }
            var request = pane.Logs.FindLastIndex(entry => entry.Kind == "user");
            if (request >= 0)
            {
                var tools = pane.Logs.Skip(request + 1).Count(entry => entry.Activity is { } activity && activity.Kind != "turn");
                var value = Named("phone.session.hero.tools", tools.ToString(invariant));
                shown.Add(value); named.Add(value);
            }
            var rest = string.Join(" · ", shown);
            rest = rest.Length == 0 || !timed ? rest : " · " + rest;
            if (figuresRest.Text != rest) figuresRest.Text = rest;
            named.Add(ProviderCatalog.Name(pane.Provider));
            var catalog = owner.Runtime(pane.Provider)?.ModelCatalog ?? ProviderCatalog.Fallback(pane.Provider);
            // The pane's own choice when it names one, else what the CLI reported.
            var model = !string.IsNullOrWhiteSpace(pane.Model) && pane.Model.Trim() != "default" ? ModelLabel.Selection(pane, catalog) : ModelLabel.ReportedModel(pane) is { } reported ? ModelLabel.Text(reported) : null;
            if (model is not null) named.Add(model);
            var full = string.Join(" · ", named);
            // Under them the tooltip says what the clock counts (M/PaneChrome.swift:71, M/AgentElapsedView.swift:13-15).
            var help = !timed ? full : full + "\n" + Locale.Get(pane.RunTiming!.IsApproximate ? "pane.hero.elapsed.approximate" : pane.RunTiming.FinishedAt is null ? "pane.hero.elapsed.running" : "pane.hero.elapsed.finished");
            if (help == figuresHelp) return;
            figuresHelp = help;
            // One tooltip whose words change: a new one each second would close the one the pointer is holding open.
            if (figuresTip is null) ToolTipService.SetToolTip(figuresWords!, figuresTip = new ToolTip());
            figuresTip.Content = help; AutomationProperties.SetName(figuresHost!, full);
            AutomationProperties.SetAutomationId(figuresHost!, (timed ? "agent-elapsed-" : "pane-figures-") + id);
        }

        /// <summary>
        /// The slim ink bar of a shell pane (M/PaneChrome.swift:99-122, M/SessionPaneView.swift:185-190):
        /// 34 high on the <c>idle</c> fill, radius 11, padding h13, set in h8 t8 from the pane's edge; the
        /// terminal symbol 11 semibold, the title 13 bold, the kind 11.5, then after what room is left the
        /// status word in an 11 bold, 20-high capsule with a 1.5pt edge, all in <c>onStatus</c>, and the …
        /// menu, 8 apart. The status mark is still kept up to date (the header's word and its accessibility
        /// follow it) but not shown, as on the Mac.
        /// </summary>
        private void SlimHeaderParts(Grid header, PaneHeaderLine line)
        {
            const double inset = 12, edge = 8;
            var onStatus = owner.brushes.Brush(DesignToken.OnStatus);
            PaintSlimBar(header, owner.brushes);
            header.Margin = new Thickness(edge - inset, edge - inset, edge - inset, 0);
            // The slim bar's title is 13 bold without the agent header's tracking (M/PaneChrome.swift:109).
            headerTitle.Foreground = onStatus; headerTitle.CharacterSpacing = 0;
            label.Foreground = onStatus; label.FontSize = DesignMetrics.Type.Pill; label.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
            slimSymbol = PaneSymbol("shell", 11, onStatus, semibold: true);
            slimSubtitle = SlimBarWords(Locale.Get("dashboard.kind.shell") + " · " + Locale.Get("phone.card.localTerminal"), onStatus);
            // The capsule's edge is drawn inside its h8 padding, as the Mac's strokeBorder is (M/PaneChrome.swift:41-42).
            slimStatusPill = new Border
            {
                Child = label, Height = 20, Padding = new Thickness(8 - DesignMetrics.Stroke.Focus, 0, 8 - DesignMetrics.Stroke.Focus, 0), CornerRadius = new CornerRadius(10),
                BorderThickness = new Thickness(DesignMetrics.Stroke.Focus), BorderBrush = onStatus, VerticalAlignment = VerticalAlignment.Center,
            };
            AutomationProperties.SetAutomationId(slimStatusPill, "pane-status-pill-" + id);
            // The menu follows the capsule at the bar's own 8, without the agent header's 6 before its controls.
            paneHeaderControls.Margin = new Thickness(0);
            line.Add(slimSymbol, PaneHeaderLine.Role.Fixed); line.Add(headerTitle, PaneHeaderLine.Role.Shrink); line.Add(slimSubtitle, PaneHeaderLine.Role.Shrink);
            line.Add(new Border(), PaneHeaderLine.Role.Fill, SlimBarSpacer); line.Add(slimStatusPill, PaneHeaderLine.Role.Fixed); line.Add(paneHeaderControls, PaneHeaderLine.Role.Fixed);
        }

        /// <summary>The slim bar's parts the design smoke reads (all null on an agent pane).</summary>
        internal (Grid? Header, FontIcon? Symbol, TextBlock Title, TextBlock? Subtitle, Border? Pill, TextBlock Word)? SlimHeaderForSmoke =>
            slimHeader ? (paneHeader, slimSymbol, headerTitle, slimSubtitle, slimStatusPill, label) : null;

        /// <summary>
        /// The header's buttons after the switch, each 22×24 in the header's ink (M/SessionPaneView.swift:258-302):
        /// plugins and the agent's terminal on an agent pane (shown by <see cref="RefreshHeaderLine"/>), then
        /// the … menu. The menu is the Mac's: rename, focus or back to the previous layout, a shell's earlier
        /// command log, copy the run log (decision Q4; off while there is none), start a new conversation
        /// (an agent's; off while it runs or has nothing to resume), and close behind a separator.
        /// </summary>
        private void AddPaneMenu()
        {
            var b = owner.brushes; var shell = Session.Kind == "shell";
            // On the slim ink bar the buttons take the bar's onStatus ink, with a faint onStatus wash under the pointer.
            var ink = b.Brush(slimHeader ? DesignToken.OnStatus : DesignToken.Ink2); var wash = slimHeader ? b.Brush(DesignToken.OnStatus, SlimHoverOpacity) : b.Subtle;
            Button IconButton(string glyph, double size, Windows.UI.Text.FontWeight weight, string automationId)
            {
                var button = new Button
                {
                    Width = 22, Height = 24, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0), CornerRadius = new CornerRadius(DesignMetrics.Radius.Segment), BorderThickness = new Thickness(0),
                    Content = new FontIcon { Glyph = glyph, FontSize = size, FontWeight = weight }, VerticalAlignment = VerticalAlignment.Center,
                };
                owner.PaintPlainButton(button, b.Transparent, wash, ink: ink);
                AutomationProperties.SetAutomationId(button, automationId);
                return button;
            }
            if (!slimHeader)
            {
                // Segoe Fluent Icons Puzzle and CommandPrompt, sized to draw as large as the Mac's 12pt semibold symbols.
                pluginButton = IconButton("\uEA86", 13, Microsoft.UI.Text.FontWeights.SemiBold, "mighty-plugins-" + id);
                pluginButton.Click += async (_, _) => { if (!owner.dialogOpen) await owner.OpenPluginBrowser(Session.Provider); };
                pluginButton.Visibility = Visibility.Collapsed;
                // Before anything already among the controls, so it follows the switch (which joins them first of all).
                paneHeaderControls.Children.Insert(0, pluginButton);
                terminalButton = IconButton("\uE756", 14.5, Microsoft.UI.Text.FontWeights.SemiBold, "agent-terminal-open-" + id);
                var open = Locale.Get("agentTerminal.terminalPane.open");
                AutomationProperties.SetName(terminalButton, open); ToolTipService.SetToolTip(terminalButton, open);
                terminalButton.Click += async (_, _) => { if (!owner.dialogOpen) await owner.OpenAgentTerminalPane(id); };
                terminalButton.Visibility = Visibility.Collapsed;
                paneHeaderControls.Children.Add(terminalButton);
            }

            var menu = new MenuFlyout();
            MenuFlyoutItem Add(string text, Func<Task> action)
            {
                var item = new MenuFlyoutItem { Text = text }; item.Click += async (_, _) => await action(); menu.Items.Add(item); return item;
            }
            var rename = Add(Locale.Get("menu.rename"), () => owner.RenameSession(id));
            var focus = Add(Locale.Get("menu.focusPane"), () => owner.TogglePaneFocus(id));
            if (shell) Add(Locale.Get("terminal.history.title") + "…", () => owner.Act(() => owner.ShowTerminalHistory(id)));
            var copy = Add(Locale.Get("pane.menu.copyLog"), () => { Copy(output.Text); return Task.CompletedTask; });
            AutomationProperties.SetAutomationId(copy, "pane-menu-copy-" + id);
            var restart = shell ? null : Add(Locale.Get("pane.menu.newConversation"), () => owner.Act(ResetConversation));
            menu.Items.Add(new MenuFlyoutSeparator());
            Add(Locale.Get("menu.closePane"), () => owner.CloseSession(id));
            menu.Opening += (_, _) =>
            {
                var state = owner.service.Snapshot; var pane = state.Sessions.FirstOrDefault(p => p.Id == id);
                rename.IsEnabled = !owner.dialogOpen;
                focus.Text = Locale.Get(LayoutMode(state, state.ActiveWorkspaceId) == "focus" ? "pane.menu.restoreLayout" : "menu.focusPane");
                copy.IsEnabled = pane?.Logs.Count > 0;
                if (restart is not null) restart.IsEnabled = pane is { ResumeId: not null } && pane.Status != "running" && !starting && !queueStarting;
            };
            // The Mac's ellipsis at 13 bold draws 12.5 wide; Segoe's More needs 15 to draw as wide.
            var more = paneMenuButton = IconButton("\uE712", 15, Microsoft.UI.Text.FontWeights.Bold, "pane-menu-" + id);
            more.Flyout = menu;
            var name = Locale.Get("pane.menu.accessibility");
            AutomationProperties.SetName(more, name); ToolTipService.SetToolTip(more, name);
            paneHeaderControls.Children.Add(more);
        }

        /// <summary>Asks the header line to lay itself out again after a part changed outside it (the switch joining the controls).</summary>
        private void QueuePaneHeaderLayout() => headerLine?.InvalidateMeasure();

        /// <summary>
        /// The header is still one 34pt row and every visible control (the … menu, both sides of the
        /// switch, the status-line toggle) lies inside it. The switch shows its words at every width, as
        /// on the Mac, so <paramref name="narrow"/> asks nothing more of a narrow pane than of a wide one.
        /// </summary>
        private bool HeaderFitsSmoke(bool narrow)
        {
            narrow = false;
            if (paneHeader is not { ActualWidth: > 0 } header || modeSwitch is null || Math.Abs(header.ActualHeight - DesignMetrics.Layout.PaneHeader) > .5) return false;
            if (ModeWordsShown != !narrow) return false;
            return new FrameworkElement[] { paneMenuButton!, modeDefaultButton!, modeMightyButton! }.Where(control => control.Visibility == Visibility.Visible).All(control =>
            {
                if (!control.IsLoaded || control.ActualWidth <= 0 || control.ActualHeight <= 0) return false;
                var start = control.TransformToVisual(header).TransformPoint(new Point());
                var end = control.TransformToVisual(header).TransformPoint(new Point(control.ActualWidth, control.ActualHeight));
                return start.X >= -1 && end.X <= header.ActualWidth + 1 && start.Y >= -1 && end.Y <= header.ActualHeight + 1;
            });
        }

        /// <summary>
        /// The agent header at the width it has now, for the design smoke: null when all is well, else what
        /// is wrong. Its parts stand in the Mac's order without overlapping; the controls are whole and
        /// against the trailing padding, the plugin and terminal buttons too when they show; and the parts
        /// gave way in the Mac's order (M/SessionPaneView.swift:215-239) — the figures before the title, the
        /// title (trimmed with an ellipsis) before the state word, the controls never.
        /// </summary>
        internal string? HeaderGivesWaySmoke()
        {
            if (paneHeader is not { ActualWidth: > 0 } header || headerLine is not { } line || figuresHost is not { } figures || slimHeader) return "the pane shows no agent header";
            (double Left, double Right) Span(FrameworkElement part)
            {
                var left = part.TransformToVisual(header).TransformPoint(new Point()).X;
                return (left, left + part.ActualWidth);
            }
            var parts = new (string Name, FrameworkElement View)[] { ("mark", headerMark.View), ("title", headerTitle), ("state word", label), ("figures", figures), ("controls", paneHeaderControls) };
            var spans = parts.Select(part => Span(part.View)).ToArray();
            for (var i = 1; i < spans.Length; i++)
                if (spans[i].Left < spans[i - 1].Right - .5) return $"the {parts[i].Name} starts at {spans[i].Left:F1}, before the {parts[i - 1].Name} ends at {spans[i - 1].Right:F1}";
            var edge = header.ActualWidth - header.Padding.Right;
            if (Math.Abs(spans[4].Right - edge) > .5) return $"the controls end at {spans[4].Right:F1}, not against the trailing padding at {edge:F1}";
            if (paneHeaderControls.ActualWidth + paneHeaderControls.Margin.Left < line.Ideal(paneHeaderControls) - .5) return $"the controls were squeezed to {paneHeaderControls.ActualWidth:F1} of {line.Ideal(paneHeaderControls):F1}";
            foreach (var button in new[] { pluginButton, terminalButton, paneMenuButton }.OfType<Button>().Where(button => button.Visibility == Visibility.Visible))
            {
                var (left, right) = Span(button);
                if (left < -.5 || right > header.ActualWidth + .5 || button.ActualWidth < button.Width - .5) return $"the header button {AutomationProperties.GetAutomationId(button)} is cut: {left:F1}..{right:F1} of {header.ActualWidth:F1}";
            }
            // A text's own width is fractional while the width it asks for is rounded up to a whole point, so a point of slack is not a cut.
            var titleCut = headerTitle.ActualWidth < line.Ideal(headerTitle) - 1.01; var wordCut = label.ActualWidth < line.Ideal(label) - 1.01;
            if (titleCut && figures.ActualWidth > .5) return $"the title was trimmed to {headerTitle.ActualWidth:F1} of {line.Ideal(headerTitle):F1} while the figures still hold {figures.ActualWidth:F1}";
            if (titleCut && headerTitle.ActualWidth > 12 && !headerTitle.IsTextTrimmed) return $"the title was cut to {headerTitle.ActualWidth:F1} of {line.Ideal(headerTitle):F1} without an ellipsis";
            if (wordCut && headerTitle.ActualWidth > .5) return $"the state word was cut to {label.ActualWidth:F1} of {line.Ideal(label):F1} while the title still holds {headerTitle.ActualWidth:F1}";
            return null;
        }
    }
}

/// <summary>
/// One line of a pane header, laid out as the Mac's HStack lays its parts out (M/SessionPaneView.swift:215-239,
/// M/PaneChrome.swift:107-115): 8 apart, centred. As the pane narrows the parts give way in this order.
/// First the <see cref="Role.Fill"/> part (an agent header's figures, a slim bar's spacer) down to its
/// least width. Then the <see cref="Role.Shrink"/> texts, which trim with an ellipsis and share what is
/// left as an HStack does: each up to an equal share, the shorter one served first. Only when those are
/// gone, and the gaps they kept with them, does the <see cref="Role.Soft"/> state word go as well.
/// <see cref="Role.Fixed"/> parts (the mark, the controls) are never squeezed, and what follows the Fill
/// part stays against the trailing edge. A Grid cannot do this: its Auto columns never give way, so a
/// long title pushed the controls out of the pane.
/// </summary>
internal sealed partial class PaneHeaderLine : Panel
{
    internal enum Role { Fixed, Soft, Shrink, Fill }

    /// <summary>The HStack's spacing (M/SessionPaneView.swift:215, M/PaneChrome.swift:107).</summary>
    private const double Spacing = 8;
    private readonly List<(UIElement Part, Role Role, double Least)> parts = [];
    private readonly Dictionary<UIElement, double> ideals = new(ReferenceEqualityComparer.Instance);

    /// <summary>Adds the next part of the line; <paramref name="least"/> is the width a Fill part never goes under.</summary>
    internal void Add(UIElement part, Role role, double least = 0) { parts.Add((part, role, least)); Children.Add(part); }

    /// <summary>The width a part asked for at the last measure, before the line gave it less (read by the design smoke).</summary>
    internal double Ideal(UIElement part) => ideals.GetValueOrDefault(part);

    private List<(UIElement Part, Role Role, double Least)> Shown() => parts.Where(part => part.Part.Visibility == Visibility.Visible).ToList();

    protected override Size MeasureOverride(Size availableSize)
    {
        var shown = Shown();
        var unbounded = new Size(double.PositiveInfinity, availableSize.Height);
        ideals.Clear();
        foreach (var (part, role, least) in shown)
        {
            if (role == Role.Fill) { ideals[part] = least; continue; }
            part.Measure(unbounded); ideals[part] = part.DesiredSize.Width;
        }
        var natural = ideals.Values.Sum() + Spacing * Math.Max(0, shown.Count - 1);
        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : natural;
        var widths = Solve(shown, width, out _);
        double height = 0;
        for (var i = 0; i < shown.Count; i++)
        {
            var (part, role, _) = shown[i];
            // A part given less than it asked for is measured again at what it gets, so a text trims itself.
            if (role == Role.Fill || widths[i] < ideals[part]) part.Measure(new Size(widths[i], availableSize.Height));
            height = Math.Max(height, part.DesiredSize.Height);
        }
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var shown = Shown();
        var widths = Solve(shown, finalSize.Width, out var gaps);
        double x = 0;
        for (var i = 0; i < shown.Count; i++)
        {
            shown[i].Part.Arrange(new Rect(x, 0, widths[i], finalSize.Height));
            x += widths[i] + gaps[i];
        }
        return finalSize;
    }

    /// <summary>Each shown part's width, and the gap after it, on a line <paramref name="width"/> wide.</summary>
    private double[] Solve(List<(UIElement Part, Role Role, double Least)> shown, double width, out double[] gaps)
    {
        var count = shown.Count; var widths = new double[count]; gaps = new double[count];
        for (var i = 0; i < count; i++) { widths[i] = ideals.GetValueOrDefault(shown[i].Part); gaps[i] = i < count - 1 ? Spacing : 0; }
        double Total(Role role) { double sum = 0; for (var i = 0; i < count; i++) if (shown[i].Role == role) sum += widths[i]; return sum; }
        var fill = shown.FindIndex(part => part.Role == Role.Fill);
        // What the trimming texts may take: the line less its gaps and every part that keeps its width.
        var room = width - gaps.Sum() - Total(Role.Fixed) - Total(Role.Soft) - Total(Role.Fill);
        var wanted = Total(Role.Shrink);
        if (room >= wanted)
        {
            if (fill >= 0) widths[fill] += room - wanted;
            return widths;
        }
        if (room > 0)
        {
            var order = Enumerable.Range(0, count).Where(i => shown[i].Role == Role.Shrink).OrderBy(i => widths[i]).ToList();
            for (var k = 0; k < order.Count; k++)
            {
                // A text asks for its width rounded up to a whole point, so one that is within a point of its share
                // still fits whole: it is not trimmed for a fraction of a point.
                var share = room / (order.Count - k);
                var given = widths[order[k]] <= share + 1 ? Math.Min(widths[order[k]], room) : share;
                widths[order[k]] = given; room -= given;
            }
            return widths;
        }
        // No room for the texts at all: they and the Fill part go, and their gaps with them. If the rest
        // still does not fit, the Soft part goes too, whole: a word cut mid-letter reads as a fault, and the
        // mark before it says the same. What is then spare stays empty where the Fill part stood, so the
        // parts after it keep the trailing edge: figures never show in a line that has lost its title.
        var (sizes, spaces) = (widths, gaps);
        void Drop(int i)
        {
            sizes[i] = 0;
            if (i < count - 1) spaces[i] = 0; else if (i > 0) spaces[i - 1] = 0;
        }
        double Spare() => width - sizes.Sum() - spaces.Sum();
        for (var i = 0; i < count; i++) if (shown[i].Role is Role.Shrink or Role.Fill) Drop(i);
        for (var i = count - 1; i >= 0 && Spare() < 0; i--) if (shown[i].Role == Role.Soft) Drop(i);
        if (fill >= 0 && Spare() > 0) gaps[fill] = Spare();
        return widths;
    }
}
