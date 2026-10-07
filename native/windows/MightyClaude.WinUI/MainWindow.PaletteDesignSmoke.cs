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
            // The Mighty pane's background strip, opened, so both theme passes walk its rows too.
            await views[mightyId].OpenBackgroundStripForSmoke();
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
                mighty.Refresh();
                await WaitUI(() => mighty.BackgroundHostForSmoke is { Visibility: Visibility.Visible, IsLoaded: true } && mighty.BackgroundRowsLoadedForSmoke,
                    () => $"{key} ({theme}): the Mighty pane's opened background strip never showed");
                root.UpdateLayout();
                // Both documents are drawn again once their boxes have painted over them for the theme.
                await WaitUI(() => ConversationInkAt(standard) is var (at, tokens) && RtfInkIs(standard.Transcript.View, at, tokens) && RtfInkIs(files.FilesMarkdownForSmoke!, 0, [DesignToken.Ink]),
                    () => $"{key} ({theme}): the conversation or the Markdown preview was not drawn again in this theme's colours; conversation {InkAt(standard.Transcript.View, ConversationInkAt(standard).At)}, Markdown {InkAt(files.FilesMarkdownForSmoke!, 0)}");
                var parts = RequireTerminalHeaderInTheme(terminal, light);
                RequireGroupSlimHeaderInTheme(files);
                checks[theme + ".dropScreenshot"] = await RequireDockInTheme();
                // The whole dock, wider than the window with its five panes side by side, for the parity review of the pane chrome.
                if (panes.Children.OfType<ScrollViewer>().FirstOrDefault()?.Content is FrameworkElement dock) checks[theme + ".chromeScreenshot"] = await CaptureElement(dock, Path.Combine(options.ProfileDirectory!, "smoke-chrome-panes-" + theme + ".png"));
                RequireConversationInTheme(standard, empty);
                RequireRtfInk(files.FilesMarkdownForSmoke!, 0, [DesignToken.Ink], "the Markdown preview's heading (files pane)", light?.Markdown);
                RequireEmptyStateBuilders();
                // The conversation, the empty agent pane and the Markdown preview as they are drawn, for the eye.
                await SettleDesktopCapture(root);
                await CaptureElement(standard.Container, Path.Combine(options.ProfileDirectory!, "smoke-transcript-" + theme + ".png"));
                await CaptureElement(empty.Container, Path.Combine(options.ProfileDirectory!, "smoke-transcript-empty-" + theme + ".png"));
                await CaptureMarkdownPreview(theme);

                var walked = WalkPalette(root, "main window");
                RequireVisited(walked, sidebarSurface, "the sidebar"); RequireVisited(walked, parts.Header, "the terminal header");
                RequireVisited(walked, standard.Transcript.View, "the Default conversation"); RequireVisited(walked, empty.EmptyOutputForSmoke!, "the empty agent pane");
                RequireVisited(walked, mighty.GraphViewportForSmoke!, "the Mighty diagram"); RequireVisited(walked, files.FilesHost!, "the files pane");
                RequireVisited(walked, mighty.BackgroundHostForSmoke!, "the background strip");
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
            // Last, as it redraws the dock: the divider's double-click.
            await RequireSplitReset(workspace.Id); checks["dividerReset"] = true;
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
    /// The terminal's slim ink bar (M/PaneChrome.swift:99-122): Layout.PaneHeader high on the shared <c>idle</c> brush,
    /// radius 11, padding Inset.PaneHeaderLeading; the 11pt symbol, the 13pt bold title, the 11.5pt kind words and the 11pt
    /// bold status word in its <see cref="SlimPillHeight"/>-high capsule with a 1.5pt edge, all <c>onStatus</c>.
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
        Require(header.CornerRadius == new CornerRadius(DesignMetrics.Radius.Pane) && header.Padding == new Thickness(DesignMetrics.Inset.PaneHeaderLeading, 0, DesignMetrics.Inset.PaneHeaderLeading, 0) && header.BorderThickness == new Thickness(0),
            $"{key} ({theme}): the terminal header must be radius {DesignMetrics.Radius.Pane}, padding h{DesignMetrics.Inset.PaneHeaderLeading}, no border; got {header.CornerRadius}, {header.Padding}, {header.BorderThickness}");
        RequirePaletteShared(header.Background, brushes.Brush(DesignToken.Idle), "the terminal header's fill");
        RequireBrush(header, e => ((Grid)e).Background, DesignToken.Idle, "the terminal header's fill", key: key);
        RequireBrush(slim.Symbol!, e => ((FontIcon)e).Foreground, DesignToken.OnStatus, "the terminal header's symbol", key: key);
        // The Mac's 11pt terminal symbol draws 12.7 wide; Segoe's CommandPrompt needs 13 to draw as large (PaneSymbol).
        Require(slim.Symbol!.FontSize == 13 && slim.Symbol.Width == 15 && slim.Symbol.FontWeight.Weight == FontWeights.SemiBold.Weight, $"{key} ({theme}): the terminal symbol must be the 13pt semibold glyph in its 15-wide slot; got {slim.Symbol.FontSize}pt in {slim.Symbol.Width}, weight {slim.Symbol.FontWeight.Weight}");
        RequireFont(slim.Title, DesignMetrics.Type.Title, FontWeights.Bold, $"({theme}) the terminal title", key);
        RequireBrush(slim.Title, e => ((TextBlock)e).Foreground, DesignToken.OnStatus, "the terminal title", key: key);
        Require(slim.Subtitle!.FontSize == DesignMetrics.Type.State && slim.Subtitle.Text.Length > 0, $"{key} ({theme}): the terminal kind words must be {DesignMetrics.Type.State}pt and not empty; got {slim.Subtitle.FontSize}pt '{slim.Subtitle.Text}'");
        RequireBrush(slim.Subtitle, e => ((TextBlock)e).Foreground, DesignToken.OnStatus, "the terminal kind words", key: key);
        RequireFont(slim.Word, DesignMetrics.Type.Pill, FontWeights.Bold, $"({theme}) the terminal status word", key);
        RequireBrush(slim.Word, e => ((TextBlock)e).Foreground, DesignToken.OnStatus, "the terminal status word", key: key);
        var pill = slim.Pill!;
        Require(pill.Height == SlimPillHeight && pill.CornerRadius == new CornerRadius(SlimPillHeight / 2) && pill.BorderThickness == new Thickness(DesignMetrics.Stroke.Focus) && ReferenceEquals(pill.Child, slim.Word),
            $"{key} ({theme}): the status capsule must be {SlimPillHeight} high, radius {SlimPillHeight / 2}, with a {DesignMetrics.Stroke.Focus}pt edge around the word; got {pill.Height}, {pill.CornerRadius}, {pill.BorderThickness}");
        // The edge is drawn inside the capsule's h Spacing.Md padding (M/PaneChrome.swift:41-42), so word and edge together stand that far in.
        Require(pill.Padding.Left + pill.BorderThickness.Left == DesignMetrics.Spacing.Md && pill.Padding.Right + pill.BorderThickness.Right == DesignMetrics.Spacing.Md, $"{key} ({theme}): the status word must stand {DesignMetrics.Spacing.Md} inside the capsule; got padding {pill.Padding} inside a {pill.BorderThickness} edge");
        RequireBrush(pill, e => ((Border)e).BorderBrush, DesignToken.OnStatus, "the status capsule's edge", key: key);
        // The bar is set in Spacing.Xs at the sides and top from the card's edge with Spacing.Xxs under it, and its parts stand in the Mac's order, Spacing.Md apart (M/PaneChrome.swift:107-119).
        var card = terminal.Container; var edge = card.BorderThickness.Left; const double setIn = DesignMetrics.Spacing.Xs;
        var at = header.TransformToVisual(card).TransformPoint(new Windows.Foundation.Point());
        Require(Math.Abs(at.X - edge - setIn) < .6 && Math.Abs(at.Y - edge - setIn) < .6 && Math.Abs(card.ActualWidth - at.X - header.ActualWidth - edge - setIn) < .6,
            $"{key} ({theme}): the terminal header must be set in {setIn} from the leading, top and trailing edges inside the card's border; got {at.X - edge:F1}, {at.Y - edge:F1}, {card.ActualWidth - at.X - header.ActualWidth - edge:F1}");
        double Left(FrameworkElement part) => part.TransformToVisual(header).TransformPoint(new Windows.Foundation.Point()).X;
        // Symbol, title, kind, then after at least SlimBarSpacer of room the capsule and the 22-wide menu against the trailing padding, each SlimSpacing from
        // the one before; a text that had to trim ends short of its place, so the texts are held to "at least".
        double End(FrameworkElement part) => Left(part) + part.ActualWidth;
        const double pad = DesignMetrics.Inset.PaneHeaderLeading, gap = PaneHeaderLine.SlimSpacing;
        Require(gap == DesignMetrics.Spacing.Md && Math.Abs(Left(slim.Symbol) - pad) < .6 && Math.Abs(Left(slim.Title) - (End(slim.Symbol) + gap)) < .6 && Left(slim.Subtitle) >= End(slim.Title) + gap - .6
            && Left(pill) >= End(slim.Subtitle) + gap + SlimBarSpacer + gap - .6 && Math.Abs(End(pill) - (header.ActualWidth - pad - 22 - gap)) < .6,
            $"{key} ({theme}): the terminal header must read symbol, title, kind, then the capsule {gap} before the menu, from its {pad} padding and {gap} apart; got symbol {Left(slim.Symbol):F1}..{End(slim.Symbol):F1}, title {Left(slim.Title):F1}..{End(slim.Title):F1}, kind {Left(slim.Subtitle):F1}..{End(slim.Subtitle):F1}, capsule {Left(pill):F1}..{End(pill):F1} of {header.ActualWidth:F1}");
        return (header, slim.Word);
    }

    /// <summary>
    /// The slim ink bar a tab group draws over a pane that is not a conversation (M/PaneChrome.swift:99-141,
    /// M/PaneDockView.swift:178-180), here the files pane's: between the tab strip and the pane, set in <see cref="SlimBarMargin"/>,
    /// Layout.PaneHeader high on <c>idle</c>, radius 11, padding Inset.PaneHeaderLeading; the folder, the title in 13pt bold and the kind in 11.5pt,
    /// all <c>onStatus</c>, with no status capsule and no menu. The pane under it draws no header and no outline.
    /// </summary>
    private void RequireGroupSlimHeaderInTheme(PaneView files)
    {
        const string key = PaletteDesignKey; var theme = SmokeTheme;
        var session = files.SessionForSmoke;
        Require(groupSlimHeaders.TryGetValue(session.Id, out var slim) && slim.Bar.IsLoaded, $"{key} ({theme}): the files pane's tab group draws no slim header over it");
        var bar = slim.Bar;
        Require(bar.Height == DesignMetrics.Layout.PaneHeader && Math.Abs(bar.ActualHeight - DesignMetrics.Layout.PaneHeader) < .5 && bar.Margin == new Thickness(DesignMetrics.Spacing.Xs, DesignMetrics.Spacing.Xs, DesignMetrics.Spacing.Xs, DesignMetrics.Spacing.Xxs)
            && bar.CornerRadius == new CornerRadius(DesignMetrics.Radius.Pane) && bar.Padding == new Thickness(DesignMetrics.Inset.PaneHeaderLeading, 0, DesignMetrics.Inset.PaneHeaderLeading, 0),
            $"{key} ({theme}): the files header must be {DesignMetrics.Layout.PaneHeader} high, set in h{DesignMetrics.Spacing.Xs} t{DesignMetrics.Spacing.Xs} b{DesignMetrics.Spacing.Xxs}, radius {DesignMetrics.Radius.Pane}, padding h{DesignMetrics.Inset.PaneHeaderLeading}; got {bar.Height} (laid out {bar.ActualHeight:F1}), {bar.Margin}, {bar.CornerRadius}, {bar.Padding}");
        RequirePaletteShared(bar.Background, brushes.Brush(DesignToken.Idle), "the files header's fill");
        RequireBrush(bar, e => ((Grid)e).Background, DesignToken.Idle, "the files header's fill", key: key);
        Require(slim.Symbol.Glyph == "\uE8B7" && slim.Symbol.FontSize == 12.5 && slim.Symbol.FontWeight.Weight == FontWeights.SemiBold.Weight, $"{key} ({theme}): the files header must lead with the 12.5pt semibold folder (the Mac's 11pt folder draws 12.3 wide); got {slim.Symbol.FontSize}pt");
        RequireBrush(slim.Symbol, e => ((FontIcon)e).Foreground, DesignToken.OnStatus, "the files header's symbol", key: key);
        RequireFont(slim.Title, DesignMetrics.Type.Title, FontWeights.Bold, $"({theme}) the files header's title", key);
        Require(slim.Title.Text == session.Title && slim.Subtitle.Text == Locale.Get("files.pane.title") && slim.Subtitle.FontSize == DesignMetrics.Type.State,
            $"{key} ({theme}): the files header must read the pane's title and its kind in {DesignMetrics.Type.State}pt; got '{slim.Title.Text}' and '{slim.Subtitle.Text}' at {slim.Subtitle.FontSize}pt");
        RequireBrush(slim.Title, e => ((TextBlock)e).Foreground, DesignToken.OnStatus, "the files header's title", key: key);
        RequireBrush(slim.Subtitle, e => ((TextBlock)e).Foreground, DesignToken.OnStatus, "the files header's kind", key: key);
        Require(!VisualChildren(bar).OfType<Button>().Any(), $"{key} ({theme}): the files header carries no button (M/PaneChrome.swift:131-141)");
        // Under the strip, over the pane; the pane itself shows no header and no outline of its own.
        var group = PaneLayout.Groups(EffectiveLayout(service.Snapshot, session.WorkspaceId)!).First(g => g.SessionIds.Contains(session.Id));
        var strip = tabStrips[group.Id].Strip;
        var top = bar.TransformToVisual(strip).TransformPoint(new Windows.Foundation.Point()).Y;
        var paneTop = files.Container.TransformToVisual(bar).TransformPoint(new Windows.Foundation.Point()).Y;
        Require(Math.Abs(top - (strip.ActualHeight + SlimBarMargin.Top)) < 1 && Math.Abs(paneTop - (bar.ActualHeight + SlimBarMargin.Bottom)) < 1 && SlimBarMargin.Top == DesignMetrics.Spacing.Xs && SlimBarMargin.Bottom == DesignMetrics.Spacing.Xxs,
            $"{key} ({theme}): the files header must sit {DesignMetrics.Spacing.Xs} under the tab strip and {DesignMetrics.Spacing.Xxs} over the pane; got {top - strip.ActualHeight:F1} and {paneTop - bar.ActualHeight:F1}");
        RequireClear(files.Container.BorderBrush, "the files pane's own outline", key);
    }

    /// <summary>
    /// The dock's dividers and drop previews in the theme just rendered. A divider (M/PaneDockView.swift:42,
    /// 125-133) is a <c>Layout.SplitDivider</c> strip that takes the pointer, with a 3x30 handle (30x3 between rows) of radius 2 in
    /// its middle, <c>line</c> until the pointer is on it. A drop preview (M/PaneDockDrag.swift:131-146,
    /// M/PaneDockView.swift:28-38) is the zone set in <see cref="DropHintInset"/> with radius 9: <c>accent</c> x 0.16 under a 2pt accent
    /// dash [6, 4], and the zone's words in 12pt semibold accent on a <c>page</c> x 0.95 capsule (h <c>Spacing.Md</c> v <c>Spacing.Sm</c>,
    /// radius 15) in its middle. Returns the capture of the dock showing three zones.
    /// </summary>
    private async Task<string> RequireDockInTheme()
    {
        const string key = PaletteDesignKey; var theme = SmokeTheme;
        Require(splitDividers.Count >= 2, $"{key} ({theme}): the side-by-side layout must hold dividers of Layout.SplitDivider {DesignMetrics.Layout.SplitDivider}; got {splitDividers.Count} of {DesignMetrics.Layout.SplitDivider}");
        foreach (var (splitId, (divider, handle)) in splitDividers)
        {
            var sideways = handle.Width < handle.Height;
            Require((sideways ? handle is { Width: 3, Height: 30 } : handle is { Width: 30, Height: 3 }) && handle.CornerRadius == new CornerRadius(2) && !handle.IsHitTestVisible,
                $"{key} ({theme}): the handle of divider {splitId} must be 3x30 (30x3 between rows), radius 2, and leave the pointer to its strip; got {handle.Width}x{handle.Height}, {handle.CornerRadius}, hit {handle.IsHitTestVisible}");
            var across = sideways ? divider.ActualWidth : divider.ActualHeight;
            var offset = handle.TransformToVisual(divider).TransformPoint(new Windows.Foundation.Point());
            Require(Math.Abs(across - DesignMetrics.Layout.SplitDivider) < .5 && Math.Abs(offset.X + handle.ActualWidth / 2 - divider.ActualWidth / 2) < .6 && Math.Abs(offset.Y + handle.ActualHeight / 2 - divider.ActualHeight / 2) < .6,
                $"{key} ({theme}): divider {splitId} must take the pointer over {DesignMetrics.Layout.SplitDivider} with its handle in the middle; got {across:F1} across, handle at ({offset.X:F1}, {offset.Y:F1}) of {divider.ActualWidth:F1}x{divider.ActualHeight:F1}");
        }
        // The pointer may rest on one divider while the check runs; that one is accent, every other is line.
        var resting = splitDividers.Values.Where(parts => ReferenceEquals(parts.Handle.Background, brushes.Brush(DesignToken.Line))).ToList();
        Require(resting.Count >= splitDividers.Count - 1 && splitDividers.Values.All(parts => ReferenceEquals(parts.Handle.Background, brushes.Brush(DesignToken.Line)) || ReferenceEquals(parts.Handle.Background, brushes.Brush(DesignToken.Accent))),
            $"{key} ({theme}): a divider's handle must be the shared line brush, accent under the pointer; got {string.Join(", ", splitDividers.Values.Select(parts => Describe(parts.Handle.Background)))}");
        RequireBrush(resting[0].Handle, e => ((Border)e).Background, DesignToken.Line, "a divider's handle at rest", key: key);

        var zones = new[] { "left", "center", "bottom" };
        Require(dropHints.Count >= zones.Length, $"{key} ({theme}): the side-by-side layout must hold {zones.Length} tab groups to show a drop preview on; got {dropHints.Count}");
        var hints = dropHints.Values.Take(zones.Length).ToList();
        try
        {
            for (var i = 0; i < zones.Length; i++) hints[i].Show(zones[i]);
            root.UpdateLayout();
            for (var i = 0; i < zones.Length; i++)
            {
                var (hint, edge, label, words, _) = hints[i]; var zone = zones[i]; var group = (FrameworkElement)hint.Parent;
                var at = hint.TransformToVisual(group).TransformPoint(new Windows.Foundation.Point());
                const double inset = DropHintInset;
                var (left, top, width, height) = zone switch
                {
                    "left" => (inset, inset, group.ActualWidth / 2 - 2 * inset, group.ActualHeight - 2 * inset),
                    "bottom" => (inset, group.ActualHeight / 2 + inset, group.ActualWidth - 2 * inset, group.ActualHeight / 2 - 2 * inset),
                    _ => (inset, inset, group.ActualWidth - 2 * inset, group.ActualHeight - 2 * inset),
                };
                Require(hint.Visibility == Visibility.Visible && !hint.IsHitTestVisible && Math.Abs(at.X - left) < .6 && Math.Abs(at.Y - top) < .6 && Math.Abs(hint.ActualWidth - width) < .6 && Math.Abs(hint.ActualHeight - height) < .6,
                    $"{key} ({theme}): the '{zone}' drop preview must cover its zone set in {inset}, {width:F1}x{height:F1} at ({left:F1}, {top:F1}); got {hint.ActualWidth:F1}x{hint.ActualHeight:F1} at ({at.X:F1}, {at.Y:F1}) in {group.ActualWidth:F1}x{group.ActualHeight:F1}");
                Require(edge.RadiusX == 9 && edge.RadiusY == 9 && edge.StrokeThickness == DesignMetrics.Stroke.Active && edge.StrokeDashArray.SequenceEqual([3, 2]),
                    $"{key} ({theme}): the '{zone}' drop preview must be radius 9 under a {DesignMetrics.Stroke.Active}pt dash of 6 on, 4 off (3 and 2 strokes); got radius {edge.RadiusX}, {edge.StrokeThickness}pt, [{string.Join(", ", edge.StrokeDashArray)}]");
                RequireBrush(edge, e => ((Microsoft.UI.Xaml.Shapes.Shape)e).Fill, DesignToken.Accent, $"the '{zone}' drop preview's wash", DropHintOpacity, key);
                RequireBrush(edge, e => ((Microsoft.UI.Xaml.Shapes.Shape)e).Stroke, DesignToken.Accent, $"the '{zone}' drop preview's dash", key: key);
                Require(label.Padding == new Thickness(DesignMetrics.Spacing.Md, DesignMetrics.Spacing.Sm, DesignMetrics.Spacing.Md, DesignMetrics.Spacing.Sm) && label.CornerRadius == new CornerRadius(15) && ReferenceEquals(label.Child, words),
                    $"{key} ({theme}): the '{zone}' drop preview's words must stand in a capsule of padding h{DesignMetrics.Spacing.Md} v{DesignMetrics.Spacing.Sm}, radius 15; got {label.Padding}, {label.CornerRadius}");
                RequireBrush(label, e => ((Border)e).Background, DesignToken.Page, $"the capsule under the '{zone}' drop preview's words", DropHintLabelOpacity, key);
                RequireFont(words, DesignMetrics.Type.Block, FontWeights.SemiBold, $"({theme}) the '{zone}' drop preview's words", key);
                RequireBrush(words, e => ((TextBlock)e).Foreground, DesignToken.Accent, $"the '{zone}' drop preview's words", key: key);
                var wordsWant = Locale.Get(zone switch { "left" => "layout.drop.left", "bottom" => "layout.drop.bottom", _ => "layout.drop.merge" });
                var middle = label.TransformToVisual(hint).TransformPoint(new Windows.Foundation.Point());
                Require(words.Text == wordsWant && Math.Abs(middle.X + label.ActualWidth / 2 - hint.ActualWidth / 2) < 1 && Math.Abs(middle.Y + label.ActualHeight / 2 - hint.ActualHeight / 2) < 1,
                    $"{key} ({theme}): the '{zone}' drop preview must read '{wordsWant}' in its middle; got '{words.Text}' at ({middle.X:F1}, {middle.Y:F1}), {label.ActualWidth:F1}x{label.ActualHeight:F1} in {hint.ActualWidth:F1}x{hint.ActualHeight:F1}");
            }
            var dock = (FrameworkElement)panes.Children.OfType<ScrollViewer>().First().Content;
            return await CaptureElement(dock, Path.Combine(options.ProfileDirectory!, "smoke-chrome-drop-" + theme + ".png"));
        }
        finally { foreach (var parts in dropHints.Values) parts.Hint.Visibility = Visibility.Collapsed; }
    }

    /// <summary>
    /// A double-click on a divider shares its split's room evenly again (M/PaneDockView.swift:146): a split
    /// moved off the middle comes back to a ratio of 0.5 through the call the double-click makes, and keeps
    /// its divider.
    /// </summary>
    private async Task RequireSplitReset(string workspace)
    {
        const string key = PaletteDesignKey;
        static PaneLayoutNode? Find(PaneLayoutNode node, string id) => node.Id == id ? node : node.Children.Select(child => Find(child, id)).FirstOrDefault(found => found is not null);
        double? Ratio(string id) => EffectiveLayout(service.Snapshot, workspace) is { } tree ? Find(tree, id)?.Ratio : null;
        Require(splitDividers.Count > 0, $"{key}: the layout holds no split to reset");
        var splitId = splitDividers.Keys.First();
        await service.UpdateAsync(s => EffectiveLayout(s, workspace) is { } current ? SaveLayout(s, workspace, PaneLayout.Resize(current, splitId, .3)) : s);
        Require(Ratio(splitId) is { } moved && Math.Abs(moved - .3) < .001, $"{key}: the split {splitId} did not take the ratio 0.3 before the reset; got {Ratio(splitId)}");
        await ResetSplit(workspace, splitId);
        Require(Ratio(splitId) is { } even && Math.Abs(even - .5) < .001 && splitDividers.ContainsKey(splitId),
            $"{key}: a double-click on a divider must share its split evenly (ratio 0.5) and keep its divider; got {Ratio(splitId)}, divider {splitDividers.ContainsKey(splitId)}");
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
        // The fixture's conversation opens with the request in its bubble (the card colour); any other first line is in one of the RTF's inks.
        var (inkAt, inks) = ConversationInkAt(standard);
        RequireRtfInk(view, inkAt, inks, inks.Length == 1 ? "the Default conversation's request bubble" : "the Default conversation's first line", null);
        RequireConversationType(standard);
        Require(standard.EmptyOutputForSmoke is { Visibility: Visibility.Collapsed }, $"{key} ({theme}): the empty state shows over a pane that has a conversation");
        var state0 = empty.EmptyOutputForSmoke!;
        Require(state0.Margin == new Thickness(PaneView.EmptyOutputPadding, PaneView.EmptyOutputPadding - DesignMetrics.Spacing.Sm, PaneView.EmptyOutputPadding, 0) && state0.Spacing == DesignMetrics.Spacing.Md,
            $"{key} ({theme}): the empty agent pane must sit {PaneView.EmptyOutputPadding} inside the output area (M/SessionPaneView.swift:543), its parts {DesignMetrics.Spacing.Md} apart; got margin {state0.Margin}, spacing {state0.Spacing}");
        var heading = state0.Children.OfType<StackPanel>().FirstOrDefault()?.Children.OfType<TextBlock>().FirstOrDefault()
            ?? throw new InvalidOperationException($"{key} ({theme}): the empty agent pane has no title line");
        var body = state0.Children.OfType<TextBlock>().LastOrDefault() ?? throw new InvalidOperationException($"{key} ({theme}): the empty agent pane has no explanation");
        Require(state0.Children.OfType<Microsoft.UI.Xaml.Shapes.Path>().Any(p => p.Width == 24), $"{key} ({theme}): the empty agent pane must lead with the agent's 24pt mark");
        // The Mac names the agent by its short label here: "Claude와 작업을 시작하세요" (M/SessionPaneView.swift:509).
        Require(heading.Text == Locale.Get("pane.empty.agentTitle", new Dictionary<string, string> { ["provider"] = ProviderMark.Label("claude") }) && body.Text == Locale.Get("pane.empty.agentBody"),
            $"{key} ({theme}): the empty agent pane's words differ from pane.empty.agentTitle / agentBody; got '{heading.Text}' / '{body.Text}'");
        Require(heading.TextWrapping == TextWrapping.Wrap && heading.ActualWidth <= state0.ActualWidth + 0.5,
            $"{key} ({theme}): the empty agent pane's title must wrap inside the pane; it is {heading.ActualWidth:F0} wide in {state0.ActualWidth:F0}");
        RequireFont(heading, 16, FontWeights.Medium, $"({theme}) the empty agent pane's title", key);
        RequireBrush(heading, e => ((TextBlock)e).Foreground, DesignToken.Ink, "the empty agent pane's title", key: key);
        Require(body.FontSize == DesignMetrics.Type.Block, $"{key} ({theme}): the empty agent pane's explanation must be {DesignMetrics.Type.Block}pt; got {body.FontSize}");
        RequireBrush(body, e => ((TextBlock)e).Foreground, DesignToken.Ink2, "the empty agent pane's explanation", key: key);
    }

    /// <summary>
    /// The files pane's Markdown preview as its renderer draws it, for the eye. Side by side with four other
    /// panes the files pane is too narrow to show one, so the Mac's own Markdown sample
    /// (M/AgentMarkdownView.swift:308-329) goes into a box set up as the pane's (padding
    /// <see cref="PaneView.FilesMarkdownInset"/>, M/FilePaneView.swift:246) over the window for the length of one capture.
    /// </summary>
    private async Task CaptureMarkdownPreview(string theme)
    {
        const string sample = "# \uC791\uC5C5\uC744 \uC815\uB9AC\uD588\uC5B4\uC694\n\n**\uB124\uC774\uD2F0\uBE0C \uD654\uBA74**\uC5D0\uC11C \uC77D\uAE30 \uD3B8\uD558\uAC8C \uD45C\uC2DC\uD569\uB2C8\uB2E4. `SessionPaneView.swift`\uC640 [Swift \uBB38\uC11C](https://www.swift.org/documentation/)\uB97C \uD655\uC778\uD558\uC138\uC694.\n\n## \uBCC0\uACBD \uC0AC\uD56D\n- \uC77D\uAE30 \uD3B8\uD55C \uC81C\uBAA9\uACFC \uBAA9\uB85D\n  - \uC911\uCCA9 \uD56D\uBAA9\uB3C4 \uC720\uC9C0\n- [x] \uC785\uB825\uACFC \uD130\uBBF8\uB110 \uC720\uC9C0\n\n1. \uCCAB\uC9F8\n2. \uB458\uC9F8\n\n> \uC791\uC131 \uC911\uC778 \uCD08\uC548\uACFC \uC2E4\uD589 \uC911\uC778 \uD130\uBBF8\uB110\uC740 \uADF8\uB300\uB85C \uC774\uC5B4\uC9D1\uB2C8\uB2E4.\n\n```swift\nlet message = \"\uC548\uB155\uD558\uC138\uC694\"\nprint(message)\n```\n\n| \uD56D\uBAA9 | \uC0C1\uD0DC |\n|:---|---:|\n| Markdown | \uC644\uB8CC |\n| \uC9C4\uD589 \uC0C1\uD0DC | \uD655\uC778 \uC911 |\n\n---\n\n### \uB9C8\uBB34\uB9AC\n\uB9C8\uC9C0\uB9C9 \uBB38\uB2E8\uC785\uB2C8\uB2E4.";
        var view = new RichEditBox { IsReadOnly = true, IsSpellCheckEnabled = false, IsTextPredictionEnabled = false, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), Background = brushes.Transparent, Padding = new Thickness(PaneView.FilesMarkdownInset) };
        var card = new Border
        {
            Width = 620, Height = 720, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Child = view,
            Background = brushes.Brush(DesignToken.Card), BorderBrush = brushes.Brush(DesignToken.Line), BorderThickness = new Thickness(DesignMetrics.Stroke.Line),
        };
        Grid.SetRowSpan(card, Math.Max(1, root.RowDefinitions.Count)); Grid.SetColumnSpan(card, Math.Max(1, root.ColumnDefinitions.Count));
        root.Children.Add(card);
        try
        {
            await WaitUI(() => view.IsLoaded, () => $"{PaletteDesignKey} ({theme}): the Markdown preview box never loaded");
            await Task.Delay(120);
            try { view.IsReadOnly = false; view.Document.SetText(TextSetOptions.FormatRtf, TranscriptRtf.RenderMarkdown(sample, theme == "light")); }
            finally { view.IsReadOnly = true; }
            await SettleDesktopCapture(card);
            await CaptureElement(card, Path.Combine(options.ProfileDirectory!, "smoke-markdown-preview-" + theme + ".png"));
        }
        finally { root.Children.Remove(card); }
    }

    /// <summary>
    /// The Default conversation as the Mac's concept D draws it (M/AgentTranscriptFormat.swift:174-248, 298-360):
    /// the words PaneInset + Inset.Transcript in from the pane's edge; the body at 13 exactly (9.75 typographic points: RTF counts
    /// points where the Mac counts epx), a level-two heading 18 semibold, a tool row's detail 11.5 mono with
    /// its timing at 10.5; no request heading; and behind the words the ink bubble, the reply card, the chip,
    /// the quote's bar and the code surface, in the shared token brushes at radius 11.
    /// </summary>
    private void RequireConversationType(PaneView standard)
    {
        const string key = PaletteDesignKey; var theme = SmokeTheme;
        var transcript = standard.Transcript; var view = transcript.View;
        view.Document.GetText(TextGetOptions.None, out var text);
        var inset = PaneView.TranscriptGutter + TranscriptRtf.Inset;
        Require(view.FontSize == DesignMetrics.Type.Body && view.Padding == new Thickness(inset, 0, inset, 0),
            $"{key} ({theme}): the conversation's box must be {DesignMetrics.Type.Body}pt with its words {inset} in from the pane's edge and its top and bottom insets in the document; got {view.FontSize}pt, padding {view.Padding}");
        (float Size, int Weight, string Name) Face(string words)
        {
            var at = text.IndexOf(words, StringComparison.Ordinal);
            Require(at >= 0, $"{key} ({theme}): the conversation does not show '{words}'");
            var format = view.Document.GetRange(at, at + 1).CharacterFormat; return (format.Size, format.Weight, format.Name);
        }
        void Sized(string what, (float Size, int Weight, string Name) face, double epx, int weight) =>
            Require(Math.Abs(face.Size - epx * 0.75) < 0.26 && face.Weight == weight, $"{key} ({theme}): {what} must be {epx} epx ({epx * 0.75:0.###} typographic points) at weight {weight}; got {face.Size}pt ({face.Size / 0.75:0.##} epx) at {face.Weight} in {face.Name}");
        var body = Face("\uB450 \uBC88\uC9F8 \uBB38\uB2E8");
        Require(Math.Abs(body.Size - DesignMetrics.Type.Body * 0.75) < 0.005, $"{key} ({theme}): the conversation's body must be exactly {DesignMetrics.Type.Body} epx ({DesignMetrics.Type.Body * 0.75} typographic points); got {body.Size}pt ({body.Size / 0.75:0.##} epx)");
        Sized("a paragraph", body, DesignMetrics.Type.Body, 400);
        Sized("a level-two heading", Face("Windows \uB124\uC774\uD2F0\uBE0C \uAC80\uC99D"), TranscriptRtf.HeadingSize(2), 600);
        var detail = Face("dotnet test");
        Sized("a tool row's detail", detail, 11.5, 400);
        Require(DesignMetrics.Font.Mono.Split(',').Select(family => family.Trim()).Contains(detail.Name), $"{key} ({theme}): a tool row's detail must be in the monospace family; got {detail.Name}");
        Sized("a tool row's timing", Face(Locale.Get("run.activity.durationSeconds", new Dictionary<string, string> { ["seconds"] = "12.3" })), 10.5, 400);
        Require(!text.Contains(Locale.Get("transcript.requestHeading"), StringComparison.Ordinal) && !text.Contains("HYPERLINK", StringComparison.Ordinal),
            $"{key} ({theme}): the request is a bubble with no heading over it, and no word of the conversation is a native link");
        var painted = transcript.Painted(); var round = new CornerRadius(TranscriptRtf.BlockRadius);
        foreach (var kind in new[] { TranscriptBlockKind.Bubble, TranscriptBlockKind.Card, TranscriptBlockKind.Chip, TranscriptBlockKind.Quote, TranscriptBlockKind.Code })
            Require(painted.Any(p => p.Kind == kind && p.Height > 0), $"{key} ({theme}): the conversation paints no {kind} behind its words; painted [{string.Join(", ", painted.Select(p => $"{p.Kind} {p.Height:F0}"))}]");
        foreach (var p in painted)
        {
            SolidColorBrush fill = p.Kind switch
            {
                TranscriptBlockKind.Bubble => brushes.Brush(DesignToken.Ink),
                TranscriptBlockKind.Card or TranscriptBlockKind.Chip => brushes.Brush(DesignToken.Card),
                TranscriptBlockKind.Code => brushes.Brush(DesignToken.CodeSurface),
                _ => brushes.Brush(DesignToken.Accent, TranscriptRtf.QuoteBarOpacity),
            };
            var edge = p.Kind is TranscriptBlockKind.Card or TranscriptBlockKind.Chip ? brushes.Brush(DesignToken.Line) : null;
            Require(ReferenceEquals(p.Fill, fill) && ReferenceEquals(p.Edge, edge) && p.Line == new Thickness(edge is null ? 0 : DesignMetrics.Stroke.Line) && p.Corners == (p.Kind == TranscriptBlockKind.Quote ? new CornerRadius(0) : round),
                $"{key} ({theme}): the conversation's {p.Kind} must be the shared {Describe(fill)} brush{(edge is null ? "" : " with a " + DesignMetrics.Stroke.Line + "pt " + Describe(edge) + " edge")} at radius {(p.Kind == TranscriptBlockKind.Quote ? 0 : TranscriptRtf.BlockRadius)}; got {Describe(p.Fill)}, edge {Describe(p.Edge)} {p.Line}, corners {p.Corners}");
        }
    }

    /// <summary>
    /// The character at <paramref name="at"/> of an RTF view is drawn in one of <paramref name="tokens"/> in the
    /// current theme: the document was rendered from this theme's colour table. Each of these inks differs
    /// between the themes, so a document left in the other theme fails.
    /// </summary>
    /// <summary>
    /// Where the Default conversation's ink is read: the request in its bubble, whose words are the card colour
    /// on the ink (M/AgentTranscriptFormat.swift:197-202), or else its first character in one of the RTF's inks.
    /// </summary>
    private (int At, DesignToken[] Tokens) ConversationInkAt(PaneView standard)
    {
        standard.Transcript.View.Document.GetText(TextGetOptions.None, out var conversation);
        var request = views.Where(pair => ReferenceEquals(pair.Value, standard)).Select(pair => service.Snapshot.Sessions.FirstOrDefault(s => s.Id == pair.Key)).FirstOrDefault()?.Logs.FirstOrDefault(e => e.Kind == "user")?.Text.Split('\n')[0];
        var requestAt = string.IsNullOrEmpty(request) ? -1 : conversation.IndexOf(request, StringComparison.Ordinal);
        return requestAt >= 0 ? (requestAt, [DesignToken.Card]) : (0, [DesignToken.Ink, DesignToken.Ink2, DesignToken.Accent, DesignToken.ErrText]);
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
