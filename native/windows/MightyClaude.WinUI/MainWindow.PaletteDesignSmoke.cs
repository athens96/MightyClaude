using MightyClaude.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace MightyClaude.WinUI;

// Design stage 7, the final pass (.omc/plans/windows-design-conversion.md). One smoke key,
// paletteDesign, opens a terminal pane, an empty agent pane, a Default pane, a Mighty pane and the
// files pane side by side, checks the terminal's slim header, the conversation surface, the RTF
// colours and the empty states, then walks the live visual tree of the window (with the dashboard
// shown, too) and of the settings window, in both themes: every SolidColorBrush the app set on an
// element must be a shared design brush, transparent, or a colour of the current theme's palette
// (the status glyphs, provider marks and syntax colours are palette data drawn per theme). Failures
// name the element, the property, the expected token and the actual colour.
public sealed partial class MainWindow
{
    private const string PaletteDesignKey = "paletteDesign";

    private async Task<Dictionary<string, object?>> RunPaletteDesignSmoke(Workspace workspace, IReadOnlyList<RunSession> fixture)
    {
        const string key = PaletteDesignKey;
        var original = service.Snapshot;
        var previousTree = EffectiveLayout(original, workspace.Id); var previousMode = LayoutMode(original, workspace.Id);
        var filesPane = FilePaneKind.PaneId(workspace.Id);
        var terminalId = Wire.Id(); var emptyId = Wire.Id();
        Func<Task>? restoreMighty = null; Task? settingsOpening = null;
        var checks = new Dictionary<string, object?>();
        try
        {
            Require(fixture.Count >= 2 && views.ContainsKey(fixture[0].Id) && views.ContainsKey(fixture[1].Id), $"{key}: needs the two fixture agent panes; views {views.Count}");
            // The Mighty pane draws the stage 5 fixture under its draft; the Default pane keeps its fixture conversation.
            await service.UpdateAsync(s => s with
            {
                Theme = "light", ActiveWorkspaceId = workspace.Id,
                Sessions = s.Sessions.Select(p => p.Id == fixture[0].Id && string.IsNullOrEmpty(p.Draft) ? p with { Draft = "palette draft" } : p).ToList(),
            });
            Render();
            await AddPane("shell", shape: p => p with { Id = terminalId, Title = "Palette terminal" });
            await AddPane("claude", shape: p => p with { Id = emptyId, Title = "Palette empty agent", Logs = [], AgentViewMode = null });
            await OpenFilePane(workspace.Id);
            await ApplyLayoutPreset("columns");
            var mightyId = fixture[0].Id; var defaultId = fixture[1].Id;
            await WaitUI(() => new[] { mightyId, defaultId, terminalId, emptyId, filesPane }.All(id => views.TryGetValue(id, out var v) && v.Container.IsLoaded),
                () => $"{key}: the panes never loaded side by side: [{string.Join(", ", new[] { mightyId, defaultId, terminalId, emptyId, filesPane }.Select(id => id + "=" + (views.TryGetValue(id, out var v) && v.Container.IsLoaded)))}]");
            restoreMighty = views[mightyId].MightyDesignRestore();
            await views[mightyId].BeginMightyDesignSmoke();
            // Select the row first, so its debounced preview cannot redraw the Markdown after the light pass read it.
            await WaitUI(() => views[filesPane].FilesTree.Children.ContainsKey(""), () => $"{key}: the files tree never listed the workspace");
            await views[filesPane].FilesSmokeSelect("README.md");
            await views[filesPane].FilesSmokePreview("README.md");

            PaletteLandmarks? light = null;
            foreach (var theme in new[] { "light", "dark" })
            {
                if (theme == "dark") { await service.UpdateAsync(s => s with { Theme = "dark" }); Render(); }
                // The sidebar and tab marks follow the theme on the app's own one-second tick; run it now.
                RefreshRunningIndicators();
                // Every pane is re-read from the live dictionary: a render may have replaced what an earlier await held.
                var mighty = views[mightyId]; var standard = views[defaultId]; var terminal = views[terminalId]; var empty = views[emptyId]; var files = views[filesPane];
                await WaitUI(() => mighty.GraphViewportForSmoke is { Visibility: Visibility.Visible, IsLoaded: true } && files.FilesMarkdownForSmoke is { IsLoaded: true }
                    && terminal.SlimHeaderForSmoke?.Header is { IsLoaded: true } && empty.EmptyOutputForSmoke is { Visibility: Visibility.Visible, IsLoaded: true },
                    () => $"{key} ({theme}): the Mighty diagram, the Markdown preview, the terminal header or the empty agent pane never showed");
                root.UpdateLayout();
                // Both documents are drawn again once their boxes have painted over them for the theme.
                await WaitUI(() => ConversationInkAt(standard) is var (at, tokens) && RtfInkIs(standard.Transcript.View, at, tokens) && RtfInkIs(files.FilesMarkdownForSmoke!, 0, [DesignToken.Ink]),
                    () => $"{key} ({theme}): the conversation or the Markdown preview was not drawn again in this theme's colours; conversation {InkAt(standard.Transcript.View, ConversationInkAt(standard).At)}, Markdown {InkAt(files.FilesMarkdownForSmoke!, 0)}");
                var parts = RequireTerminalHeaderInTheme(terminal, light);
                RequireConversationInTheme(standard, empty);
                RequireRtfInk(files.FilesMarkdownForSmoke!, 0, [DesignToken.Ink], "the Markdown preview's heading (files pane)", light?.Markdown);
                RequireEmptyStateBuilders();

                var walked = WalkPalette(root, "main window");
                RequireVisited(walked, sidebarSurface, "the sidebar"); RequireVisited(walked, parts.Header, "the terminal header");
                RequireVisited(walked, standard.Transcript.View, "the Default conversation"); RequireVisited(walked, empty.EmptyOutputForSmoke!, "the empty agent pane");
                RequireVisited(walked, mighty.GraphViewportForSmoke!, "the Mighty diagram"); RequireVisited(walked, files.FilesHost!, "the files pane");
                checks[theme + ".mainWindow"] = walked.Summary;

                showsDashboard = true; RefreshDashboardEntry(); RenderDashboard(); root.UpdateLayout();
                await WaitUI(() => dashboard is { Visibility: Visibility.Visible, IsLoaded: true, Content: FrameworkElement { IsLoaded: true } }, () => $"{key} ({theme}): the dashboard never showed");
                var dashboardWalk = WalkPalette(root, "dashboard");
                RequireVisited(dashboardWalk, (DependencyObject)dashboard!.Content, "the dashboard");
                checks[theme + ".dashboard"] = dashboardWalk.Summary;
                HideDashboard(); root.UpdateLayout();

                settingsOpening = ShowCategorizedSettingsAsync();
                await WaitUI(() => settingsWindow?.Content is FrameworkElement { XamlRoot: not null, ActualWidth: > 0, IsLoaded: true }, () => $"{key} ({theme}): the settings window never opened");
                var frame = (FrameworkElement)settingsWindow!.Content; frame.UpdateLayout();
                var settingsWalk = WalkPalette(frame, "settings window");
                checks[theme + ".settings"] = settingsWalk.Summary;
                settingsWindow.Close(); await settingsOpening; settingsOpening = null;

                light ??= new(parts.Header, files.FilesMarkdownForSmoke!);
            }
            checks["terminalHeader"] = true; checks["conversationSurface"] = true; checks["rtfFollowsTheme"] = true; checks["emptyStates"] = true; checks["paletteWalk"] = true; checks["bothThemes"] = true;
            return checks;
        }
        finally
        {
            if (settingsOpening is not null) { settingsWindow?.Close(); await settingsOpening; }
            HideDashboard();
            if (restoreMighty is not null) await restoreMighty();
            foreach (var id in new[] { terminalId, emptyId, filesPane }) if (service.Snapshot.Sessions.Any(s => s.Id == id)) await CloseSession(id);
            await service.UpdateAsync(s => SaveLayoutMode(SaveLayout(s with { Theme = original.Theme, Sessions = s.Sessions.Select(p => original.Sessions.FirstOrDefault(o => o.Id == p.Id) ?? p).ToList() }, workspace.Id, previousTree), workspace.Id, previousMode));
            Render();
            if (original.ActiveSessionId is { } previous && service.Snapshot.Sessions.Any(s => s.Id == previous)) await SelectLayoutSession(previous);
        }
    }

    /// <summary>The instances the light pass saw, which the dark pass must find again.</summary>
    private sealed record PaletteLandmarks(Grid Header, RichEditBox Markdown);

    private void RequirePaletteShared(Brush? actual, Brush expected, string what) =>
        Require(ReferenceEquals(actual, expected), $"{PaletteDesignKey} ({SmokeTheme}): {what} must be the shared {Describe(expected)} brush; got {Describe(actual)}");

    /// <summary>
    /// The terminal's slim ink bar (M/PaneChrome.swift:99-122): 34 high on the shared <c>idle</c> brush,
    /// radius 11, padding h13; the 11pt symbol, the 13pt bold title, the 11.5pt kind words and the 11pt
    /// bold status word in its 20-high capsule with a 1.5pt edge, all <c>onStatus</c>.
    /// </summary>
    private (Grid Header, TextBlock Word) RequireTerminalHeaderInTheme(PaneView terminal, PaletteLandmarks? light)
    {
        const string key = PaletteDesignKey; var theme = SmokeTheme;
        var slim = terminal.SlimHeaderForSmoke ?? throw new InvalidOperationException($"{key} ({theme}): the terminal pane has no slim header");
        Require(slim.Header is not null && slim.Symbol is not null && slim.Subtitle is not null && slim.Pill is not null, $"{key} ({theme}): the terminal header is missing a part");
        var header = slim.Header!;
        if (light is not null) Require(ReferenceEquals(header, light.Header), $"{key} ({theme}): the toggle rebuilt the terminal header instead of recolouring it");
        Require(header.Height == DesignMetrics.Layout.PaneHeader && Math.Abs(header.ActualHeight - DesignMetrics.Layout.PaneHeader) < .5,
            $"{key} ({theme}): the terminal header must be Layout.PaneHeader {DesignMetrics.Layout.PaneHeader} high; got {header.Height} (laid out {header.ActualHeight:F1})");
        Require(header.CornerRadius == new CornerRadius(DesignMetrics.Radius.Pane) && header.Padding == new Thickness(13, 0, 13, 0) && header.BorderThickness == new Thickness(0),
            $"{key} ({theme}): the terminal header must be radius {DesignMetrics.Radius.Pane}, padding h13, no border; got {header.CornerRadius}, {header.Padding}, {header.BorderThickness}");
        RequirePaletteShared(header.Background, brushes.Brush(DesignToken.Idle), "the terminal header's fill");
        RequireBrush(header, e => ((Grid)e).Background, DesignToken.Idle, "the terminal header's fill", key: key);
        RequireBrush(slim.Symbol!, e => ((FontIcon)e).Foreground, DesignToken.OnStatus, "the terminal header's symbol", key: key);
        Require(slim.Symbol!.FontSize == 11, $"{key} ({theme}): the terminal symbol must be 11pt; got {slim.Symbol.FontSize}");
        RequireFont(slim.Title, DesignMetrics.Type.Title, FontWeights.Bold, $"({theme}) the terminal title", key);
        RequireBrush(slim.Title, e => ((TextBlock)e).Foreground, DesignToken.OnStatus, "the terminal title", key: key);
        Require(slim.Subtitle!.FontSize == DesignMetrics.Type.State && slim.Subtitle.Text.Length > 0, $"{key} ({theme}): the terminal kind words must be {DesignMetrics.Type.State}pt and not empty; got {slim.Subtitle.FontSize}pt '{slim.Subtitle.Text}'");
        RequireBrush(slim.Subtitle, e => ((TextBlock)e).Foreground, DesignToken.OnStatus, "the terminal kind words", key: key);
        RequireFont(slim.Word, DesignMetrics.Type.Pill, FontWeights.Bold, $"({theme}) the terminal status word", key);
        RequireBrush(slim.Word, e => ((TextBlock)e).Foreground, DesignToken.OnStatus, "the terminal status word", key: key);
        var pill = slim.Pill!;
        Require(pill.Height == 20 && pill.BorderThickness == new Thickness(DesignMetrics.Stroke.Focus) && ReferenceEquals(pill.Child, slim.Word),
            $"{key} ({theme}): the status capsule must be 20 high with a {DesignMetrics.Stroke.Focus}pt edge around the word; got {pill.Height}, {pill.BorderThickness}");
        RequireBrush(pill, e => ((Border)e).BorderBrush, DesignToken.OnStatus, "the status capsule's edge", key: key);
        return (header, slim.Word);
    }

    /// <summary>
    /// The Default conversation on the shared <c>cardRaised</c> surface (also under the pointer and in
    /// focus), its RTF drawn in the current theme's tokens (the request heading in <c>accent</c>), the
    /// empty state hidden while it has a conversation, and the empty agent pane's mark, 16pt medium
    /// <c>ink</c> line and 12pt <c>ink2</c> explanation.
    /// </summary>
    private void RequireConversationInTheme(PaneView standard, PaneView empty)
    {
        const string key = PaletteDesignKey; var theme = SmokeTheme;
        var view = standard.Transcript.View; var raised = brushes.Brush(DesignToken.CardRaised);
        Require(view.Visibility == Visibility.Visible, $"{key} ({theme}): the Default pane's conversation is not showing");
        RequirePaletteShared(view.Background, raised, "the Default conversation surface");
        RequireBrush(view, e => ((Control)e).Background, DesignToken.CardRaised, "the Default conversation surface", key: key);
        foreach (var state in new[] { "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused" })
            RequirePaletteShared(OwnResource(view, state) as Brush, raised, $"the conversation's {state}");
        // The fixture's conversation opens with the request heading (accent); any other first line is in one of the RTF's inks.
        var (inkAt, inks) = ConversationInkAt(standard);
        RequireRtfInk(view, inkAt, inks, inks.Length == 1 ? "the Default conversation's request heading" : "the Default conversation's first line", null);
        Require(standard.EmptyOutputForSmoke is { Visibility: Visibility.Collapsed }, $"{key} ({theme}): the empty state shows over a pane that has a conversation");
        var state0 = empty.EmptyOutputForSmoke!;
        var heading = state0.Children.OfType<StackPanel>().FirstOrDefault()?.Children.OfType<TextBlock>().FirstOrDefault()
            ?? throw new InvalidOperationException($"{key} ({theme}): the empty agent pane has no title line");
        var body = state0.Children.OfType<TextBlock>().LastOrDefault() ?? throw new InvalidOperationException($"{key} ({theme}): the empty agent pane has no explanation");
        Require(state0.Children.OfType<Microsoft.UI.Xaml.Shapes.Path>().Any(p => p.Width == 24), $"{key} ({theme}): the empty agent pane must lead with the agent's 24pt mark");
        Require(heading.Text == Locale.Get("pane.empty.agentTitle", new Dictionary<string, string> { ["provider"] = ProviderCatalog.Name("claude") }) && body.Text == Locale.Get("pane.empty.agentBody"),
            $"{key} ({theme}): the empty agent pane's words differ from pane.empty.agentTitle / agentBody; got '{heading.Text}' / '{body.Text}'");
        RequireFont(heading, 16, FontWeights.Medium, $"({theme}) the empty agent pane's title", key);
        RequireBrush(heading, e => ((TextBlock)e).Foreground, DesignToken.Ink, "the empty agent pane's title", key: key);
        Require(body.FontSize == DesignMetrics.Type.Block, $"{key} ({theme}): the empty agent pane's explanation must be {DesignMetrics.Type.Block}pt; got {body.FontSize}");
        RequireBrush(body, e => ((TextBlock)e).Foreground, DesignToken.Ink2, "the empty agent pane's explanation", key: key);
    }

    /// <summary>
    /// The character at <paramref name="at"/> of an RTF view is drawn in one of <paramref name="tokens"/> in the
    /// current theme: the document was rendered from this theme's colour table. Each of these inks differs
    /// between the themes, so a document left in the other theme fails.
    /// </summary>
    /// <summary>Where the Default conversation's ink is read: the request heading (accent), or else its first character in one of the RTF's inks.</summary>
    private static (int At, DesignToken[] Tokens) ConversationInkAt(PaneView standard)
    {
        standard.Transcript.View.Document.GetText(TextGetOptions.None, out var conversation);
        var requestAt = conversation.IndexOf(Locale.Get("transcript.requestHeading"), StringComparison.Ordinal);
        return requestAt >= 0 ? (requestAt, [DesignToken.Accent]) : (0, [DesignToken.Ink, DesignToken.Ink2, DesignToken.Accent, DesignToken.ErrText]);
    }

    private static string InkAt(RichEditBox view, int at)
    {
        view.Document.GetText(TextGetOptions.None, out var text);
        if (text.Trim().Length <= at) return "empty";
        view.Document.GetRange(at, at + 1).GetText(TextGetOptions.FormatRtf, out var rtf);
        return RtfCharacterInk(rtf);
    }

    /// <summary>Whether the character at <paramref name="at"/> is drawn in one of <paramref name="tokens"/> in the current theme.</summary>
    private bool RtfInkIs(RichEditBox view, int at, DesignToken[] tokens)
    {
        var actual = InkAt(view, at);
        return tokens.Any(token => FixtureHex(SmokeTheme, token) == actual);
    }

    private void RequireRtfInk(RichEditBox view, int at, DesignToken[] tokens, string what, RichEditBox? before)
    {
        const string key = PaletteDesignKey; var theme = SmokeTheme;
        if (before is not null) Require(ReferenceEquals(view, before), $"{key} ({theme}): the toggle replaced {what} instead of rendering it again in place");
        view.Document.GetText(TextGetOptions.None, out var text);
        Require(text.Trim().Length > at, $"{key} ({theme}): {what} is empty ({text.Length} characters)");
        // The character is read back as RTF, and its colour is the colour-table entry its \cf index
        // names: what the document holds, rather than CharacterFormat.ForegroundColor, which the
        // runner reports as black for a colour that is plainly set.
        view.Document.GetRange(at, at + 1).GetText(TextGetOptions.FormatRtf, out var rtf);
        var actual = RtfCharacterInk(rtf);
        var expected = tokens.Select(token => $"{token} {FixtureHex(theme, token)}").ToList();
        Require(tokens.Any(token => FixtureHex(theme, token) == actual),
            $"{key} ({theme}): {what} must be drawn in {string.Join(" or ", expected)} (the RTF colour table follows the theme); got {actual} from {(rtf.Length > 400 ? rtf[..400] + "…" : rtf)}");
    }

    /// <summary>
    /// The colour of the one character an RTF range holds: the <c>\cfN</c> it is drawn with, looked up in
    /// the range's own colour table (entry 0 is the automatic colour, written "auto").
    /// </summary>
    private static string RtfCharacterInk(string rtf)
    {
        var table = System.Text.RegularExpressions.Regex.Match(rtf, @"\{\\colortbl(?<body>[^}]*)\}");
        if (!table.Success) return "no colour table";
        var entries = table.Groups["body"].Value.Split(';');
        var after = rtf[(table.Index + table.Length)..];
        var index = System.Text.RegularExpressions.Regex.Matches(after, @"\\cf(\d+)").Select(m => int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).LastOrDefault();
        if (index <= 0) return "auto";
        if (index >= entries.Length) return $"cf{index} past the {entries.Length - 1} table entries";
        var rgb = System.Text.RegularExpressions.Regex.Match(entries[index], @"\\red(\d+)\s*\\green(\d+)\s*\\blue(\d+)");
        if (!rgb.Success) return $"cf{index} '{entries[index].Trim()}'";
        static string Hex(System.Text.RegularExpressions.Group g) => int.Parse(g.Value, System.Globalization.CultureInfo.InvariantCulture).ToString("X2", System.Globalization.CultureInfo.InvariantCulture);
        return "#" + Hex(rgb.Groups[1]) + Hex(rgb.Groups[2]) + Hex(rgb.Groups[3]);
    }

    /// <summary>The welcome (27pt semibold <c>ink</c> line) and the no-panes invitation (20pt semibold <c>ink</c>), built as the layout builds them.</summary>
    private void RequireEmptyStateBuilders()
    {
        const string key = PaletteDesignKey; var theme = SmokeTheme;
        var welcome = BuildWelcome();
        var stack = welcome.Children.OfType<StackPanel>().Single();
        var title = stack.Children.OfType<TextBlock>().First(t => AutomationProperties.GetAutomationId(t) == "welcome-title");
        Require(title.Text == Locale.Get("layout.welcome.title"), $"{key} ({theme}): the welcome must read layout.welcome.title; got '{title.Text}'");
        RequireFont(title, DesignMetrics.Type.Welcome, FontWeights.SemiBold, $"({theme}) the welcome title", key);
        RequirePaletteShared(title.Foreground, brushes.Brush(DesignToken.Ink), "the welcome title");
        Require(stack.Children.OfType<Button>().Any(b => AutomationProperties.GetAutomationId(b) == "welcome-open-folder"), $"{key} ({theme}): the welcome has no open-folder button");
        var none = BuildEmptyPanes(false);
        var line = none.Children.OfType<TextBlock>().First();
        Require(line.Text == Locale.Get("layout.empty.addPane"), $"{key} ({theme}): the no-panes invitation must read layout.empty.addPane; got '{line.Text}'");
        RequireFont(line, 20, FontWeights.SemiBold, $"({theme}) the no-panes title", key);
        RequirePaletteShared(line.Foreground, brushes.Brush(DesignToken.Ink), "the no-panes title");
    }

    private static void RequireVisited(PaletteWalk walk, DependencyObject landmark, string what) =>
        Require(walk.Visited.Contains(landmark), $"{PaletteDesignKey}: the palette walk of the {walk.Scope} never reached {what}, so it proves nothing about it");

    /// <summary>
    /// Walks one window's live tree in the current theme and fails, naming every offender, when the app
    /// set a SolidColorBrush that is not a shared design brush, transparent, or a colour of this theme's palette.
    /// </summary>
    private PaletteWalk WalkPalette(DependencyObject top, string scope)
    {
        var palette = DesignTokens.Palette(SmokeTheme);
        var walk = new PaletteWalk(scope, new HashSet<Brush>(brushes.HandedOut(), ReferenceEqualityComparer.Instance), PaletteColours(palette),
            new HashSet<DependencyObject>(views.Values.Select(v => (DependencyObject)v.StatusLineHost), ReferenceEqualityComparer.Instance));
        walk.Walk(top);
        Require(walk.Checked > 0, $"{PaletteDesignKey} ({SmokeTheme}): the palette walk of the {scope} read no brush at all");
        Require(walk.Violations.Count == 0,
            $"{PaletteDesignKey} ({SmokeTheme}): {walk.Violations.Count} brush(es) in the {scope} are neither a shared design brush, transparent, nor a {SmokeTheme} palette colour: {string.Join("; ", walk.Violations.Take(12))}");
        return walk;
    }

    /// <summary>
    /// The colours a non-shared brush may carry in a theme: every palette token, the subtle base and
    /// black (the selected-row hairline), the syntax colours, the status glyphs' inks and discs, and
    /// the provider marks. All are palette data, redrawn per theme by their owners.
    /// </summary>
    private static HashSet<uint> PaletteColours(DesignPalette palette)
    {
        var set = new HashSet<uint>();
        void Add(DesignColor color) => set.Add((uint)(color.R << 16 | color.G << 8 | color.B));
        foreach (var token in Enum.GetValues<DesignToken>()) Add(palette[token]);
        Add(DesignTokens.Subtle(palette)); Add(new DesignColor(0, 0, 0));
        foreach (var kind in new[] { "keyword", "string", "number", "comment" }) if (DesignTokens.Syntax(kind, palette) is { } syntax) Add(syntax);
        foreach (var tone in Enum.GetValues<DesignTone>()) { Add(palette.Glyph(tone)); Add(DesignTokens.Light.DiscFill(tone)); Add(DesignTokens.Light.DiscInk(tone)); }
        foreach (var provider in new[] { "claude", "codex", "gemini" }) foreach (var rgb in ProviderMark.Colors(provider)) Add(new DesignColor(rgb));
        return set;
    }

    /// <summary>
    /// The walk itself. Only values the app set locally on its own elements are read
    /// (<c>ReadLocalValue</c>): a stock control's template parts take the theme resources the
    /// stage 1 overrides point at the tokens, so the walk steps over a control's template and comes
    /// back in at its content (a content control's Content, an items control's UIElement items).
    /// Exempt: the runs of a status line (ANSI terminal output is data), web views and Win2D
    /// canvases (they draw no XAML brushes), collapsed subtrees (nothing of them shows), and menus
    /// and flyouts, which live in popups outside the window's tree (system material).
    /// </summary>
    private sealed class PaletteWalk(string scope, HashSet<Brush> shared, HashSet<uint> palette, HashSet<DependencyObject> ansiHosts)
    {
        private readonly HashSet<object> reentry = new(ReferenceEqualityComparer.Instance);
        private readonly List<string> path = [];
        internal string Scope { get; } = scope;
        internal HashSet<object> Visited { get; } = new(ReferenceEqualityComparer.Instance);
        internal List<string> Violations { get; } = [];
        internal int Checked { get; private set; }
        private int sharedCount, paletteCount, transparentCount, exempt;
        internal Dictionary<string, object?> Summary => new() { ["elements"] = Visited.Count, ["brushes"] = Checked, ["shared"] = sharedCount, ["palette"] = paletteCount, ["transparent"] = transparentCount, ["exemptSurfaces"] = exempt };

        internal void Walk(DependencyObject node, bool ours = true, bool ansi = false)
        {
            if (node is UIElement { Visibility: Visibility.Collapsed }) return;
            if (node.GetType().Name is "WebView2" or "CanvasControl" or "CanvasAnimatedControl" or "CanvasVirtualControl") { exempt++; return; }
            Visited.Add(node);
            path.Add(Name(node));
            ansi |= ansiHosts.Contains(node);
            if (ours) Check(node, ansi);
            switch (node)
            {
                case ContentControl { Content: UIElement content }: reentry.Add(content); break;
                case ContentPresenter { Content: UIElement presented }: reentry.Add(presented); break;
            }
            if (node is ItemsControl items) foreach (var item in items.Items) if (item is UIElement element) reentry.Add(element);
            // A control's visual children are its template, not the app's elements, until its content comes back.
            var inherit = ours && node is not Control;
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(node); index++)
            {
                var child = VisualTreeHelper.GetChild(node, index);
                Walk(child, inherit || reentry.Contains(child), ansi);
            }
            path.RemoveAt(path.Count - 1);
        }

        private static string Name(DependencyObject node)
        {
            var id = node is UIElement element ? AutomationProperties.GetAutomationId(element) : "";
            return string.IsNullOrEmpty(id) ? node.GetType().Name : node.GetType().Name + "'" + id + "'";
        }

        private void Check(DependencyObject node, bool ansi)
        {
            switch (node)
            {
                case Border: Read(node, Border.BackgroundProperty, "Background"); Read(node, Border.BorderBrushProperty, "BorderBrush"); break;
                case Panel:
                    Read(node, Panel.BackgroundProperty, "Background");
                    if (node is Grid) Read(node, Grid.BorderBrushProperty, "BorderBrush");
                    else if (node is StackPanel) Read(node, StackPanel.BorderBrushProperty, "BorderBrush");
                    break;
                case Control: Read(node, Control.BackgroundProperty, "Background"); Read(node, Control.ForegroundProperty, "Foreground"); Read(node, Control.BorderBrushProperty, "BorderBrush"); break;
                case ContentPresenter: Read(node, ContentPresenter.BackgroundProperty, "Background"); Read(node, ContentPresenter.ForegroundProperty, "Foreground"); Read(node, ContentPresenter.BorderBrushProperty, "BorderBrush"); break;
                case TextBlock text:
                    Read(node, TextBlock.ForegroundProperty, "Foreground");
                    if (!ansi) Inlines(text.Inlines, "Inline");
                    break;
                case RichTextBlock rich:
                    Read(node, RichTextBlock.ForegroundProperty, "Foreground");
                    if (!ansi) foreach (var paragraph in rich.Blocks.OfType<Paragraph>()) Inlines(paragraph.Inlines, "Inline");
                    break;
                case Microsoft.UI.Xaml.Shapes.Shape: Read(node, Microsoft.UI.Xaml.Shapes.Shape.FillProperty, "Fill"); Read(node, Microsoft.UI.Xaml.Shapes.Shape.StrokeProperty, "Stroke"); break;
                case IconElement: Read(node, IconElement.ForegroundProperty, "Foreground"); break;
            }
        }

        private void Inlines(InlineCollection inlines, string what)
        {
            foreach (var inline in inlines)
            {
                Read(inline, TextElement.ForegroundProperty, what + "(" + (inline is Run run ? Clip(run.Text) : inline.GetType().Name) + ").Foreground");
                if (inline is Span span) Inlines(span.Inlines, what);
            }
        }

        private static string Clip(string text) => text.Length <= 16 ? text : text[..16] + "…";

        private void Read(DependencyObject owner, DependencyProperty property, string what)
        {
            // Unset values, bindings and gradient or image brushes are not a solid colour the app chose.
            if (owner.ReadLocalValue(property) is not SolidColorBrush brush) return;
            Checked++;
            if (shared.Contains(brush)) { sharedCount++; return; }
            var color = brush.Color;
            if (color.A == 0) { transparentCount++; return; }
            if (palette.Contains((uint)(color.R << 16 | color.G << 8 | color.B))) { paletteCount++; return; }
            Violations.Add($"{string.Join("/", path.TakeLast(5))}.{what} = #{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}");
        }
    }

    private sealed partial class PaneView
    {
        /// <summary>The Mighty diagram's viewport, for the palette walk's coverage check.</summary>
        internal Border? GraphViewportForSmoke => graphViewport;

        /// <summary>The rendered Markdown preview on screen in the files pane (null when none shows).</summary>
        internal RichEditBox? FilesMarkdownForSmoke => markdownShown is { } markdown && ReferenceEquals(previewContent?.Child, markdown.View) ? markdown.View : null;
    }
}
