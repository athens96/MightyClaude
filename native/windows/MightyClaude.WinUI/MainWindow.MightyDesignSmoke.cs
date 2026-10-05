using MightyClaude.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Rectangle = Microsoft.UI.Xaml.Shapes.Rectangle;

namespace MightyClaude.WinUI;

// Design stage 5, the Mighty diagram, the timeline and the result card
// (.omc/plans/windows-design-conversion.md). The light pass draws a fixture of two requests (one
// finished with its result, one running with a waiting and a finished sub-agent) under the pane's
// draft, checks the diagram, then the timeline, and keeps them on screen; after the theme toggle the
// dark pass reads the same timeline and diagram again without a rebuild, so every colour it sees was
// recoloured in place. Every failure names the token, the expected hex and the actual one.
public sealed partial class MainWindow
{
    private const string MightyDesignKey = "mightyDesign";

    /// <summary>A WinUI colour as #AARRGGBB, the way <see cref="Describe"/> prints a brush.</summary>
    private static string Hex(Windows.UI.Color color) => $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";

    /// <summary>
    /// <see cref="WaitUI"/> for a condition, failing with <paramref name="message"/>, read at the timeout,
    /// so the annotation names what was seen rather than only the condition's source text.
    /// </summary>
    private static async Task WaitUI(Func<bool> condition, Func<string> message)
    {
        try { await WaitUI(condition); }
        catch (TimeoutException) { Require(false, message()); }
    }

    private sealed partial class PaneView
    {
        /// <summary>The elements the light pass checked, which the dark pass must find again (the same instances).</summary>
        internal sealed record MightyDesignViews(Border Viewport, MightyDotGrid Dots, Border Card, Rectangle Outline, Rectangle Draft, Border RowCard, Border ResultCard, Grid ResultHead);

        /// <summary>
        /// What puts this pane back as it is now (its runs, view modes and the animation setting). Taken
        /// before <see cref="BeginMightyDesignSmoke"/> changes anything, so a failure part way is undone too.
        /// </summary>
        internal Func<Task> MightyDesignRestore()
        {
            var original = Session; var animations = AnimationsEnabledOverride;
            return async () =>
            {
                AnimationsEnabledOverride = animations;
                await owner.Act(async () => { await Change(p => p with { GraphRuns = original.GraphRuns, AgentViewMode = original.AgentViewMode, GraphViewMode = original.GraphViewMode }); Refresh(); RefreshMightyView(Session); });
            };
        }

        /// <summary>Puts the fixture on this pane in the Mighty diagram with animations on.</summary>
        internal async Task BeginMightyDesignSmoke()
        {
            Require(!string.IsNullOrEmpty(Session.Draft), $"{MightyDesignKey}: the smoke pane needs a draft so the diagram draws its draft block");
            var now = Wire.Now();
            var done = new MightyGraphRun
            {
                Id = "design-smoke-done", Input = "Summarise the design notes", Status = "completed", Usage = new(1200, 300),
                FinalOutput = string.Join("\n", Enumerable.Range(1, 12).Select(i => "Result line " + i)),
                RootEntries = [new("design-smoke-done-root", "assistant", "Read the notes.", now)],
                Agents = [new() { Id = "design-smoke-done-agent", Title = "Reader", Status = "completed", Entries = [new("design-smoke-done-agent-answer", "assistant", "Done reading.", now)] }],
            };
            var live = new MightyGraphRun
            {
                Id = "design-smoke-live", Input = "Check the timeline colours", Status = "running", Usage = new(800, 100), NodeModelLabel = "Opus 5",
                RootEntries = [new("design-smoke-live-root", "assistant", "Checking the colours.", now)],
                Agents =
                [
                    new() { Id = "design-smoke-live-finished", Title = "Finder", Status = "completed", Entries = [new("design-smoke-live-finished-answer", "assistant", "Found them.", now)] },
                    new() { Id = "design-smoke-live-waiting", Title = "Asker", Status = "waiting", Entries = [new("design-smoke-live-waiting-question", "assistant", "Which theme first?", now)] },
                ],
            };
            MightyGraphSupport.RefreshResult(done);
            AnimationsEnabledOverride = true;
            await SetGraphRunsForSmoke([done, live]); await SetAgentViewMode("mighty"); await SetGraphPresentation("diagram");
        }

        /// <summary>
        /// The Mighty view in the theme just rendered. The light pass (<paramref name="before"/> null) checks
        /// the diagram, then the timeline, which it leaves showing; the dark pass checks that same timeline
        /// first, then the diagram, and requires each element the light pass saw to be the same instance.
        /// </summary>
        internal async Task<MightyDesignViews> RequireMightyDesignInTheme(MightyDesignViews? before)
        {
            var runs = Session.GraphRuns ?? [];
            Require(runs.Count == 2 && runs[0].Id == "design-smoke-done" && runs[1].Id == "design-smoke-live", $"{MightyDesignKey} ({owner.SmokeTheme}): the fixture runs are not on the pane: [{string.Join(", ", runs.Select(r => r.Id))}]");
            if (before is null)
            {
                var diagram = await RequireMightyDiagram(runs, null);
                var timeline = await RequireMightyTimeline(runs, null);
                // Nothing may redraw the timeline before the dark pass reads it: no scroll to the top loads older requests.
                timelineHistoryArmed = false;
                return new(diagram.Viewport, diagram.Dots, diagram.Card, diagram.Outline, diagram.Draft, timeline.RowCard, timeline.ResultCard, timeline.ResultHead);
            }
            await RequireMightyTimeline(runs, before);
            await RequireMightyDiagram(runs, before);
            return before;
        }

        private async Task<(Border Viewport, MightyDotGrid Dots, Border Card, Rectangle Outline, Rectangle Draft)> RequireMightyDiagram(IReadOnlyList<MightyGraphRun> runs, MightyDesignViews? before)
        {
            const string key = MightyDesignKey; var o = owner; var theme = o.SmokeTheme; var b = o.brushes;
            var (done, live) = (runs[0], runs[1]);
            var liveRequest = MightyGraphLayout.NodeID(live, "request"); var doneRequest = MightyGraphLayout.NodeID(done, "request");
            var doneResult = MightyGraphLayout.NodeID(done, "result"); var waiting = MightyGraphLayout.NodeID(live, "agent:design-smoke-live-waiting");
            var draftId = MightyGraphCamera.PendingNodeID;
            await SetGraphPresentation("diagram");
            await WaitUI(() => graphViewport is { Visibility: Visibility.Visible, ActualWidth: > 0 } && new[] { liveRequest, doneRequest, doneResult, waiting, draftId }.All(graphCards.ContainsKey)
                && graphCards[liveRequest].IsLoaded && graphDraftOutline is { IsLoaded: true },
                () => $"{key} ({theme}): the diagram did not draw the fixture's blocks and draft; blocks [{string.Join(", ", graphCards.Keys)}]");
            o.root.UpdateLayout();

            // The canvas surface: page, edge to edge.
            var viewport = graphViewport!;
            Require(ReferenceEquals(viewport.Background, b.Brush(DesignToken.Page)) && viewport.CornerRadius == new CornerRadius(0),
                $"{key} ({theme}): the diagram viewport must be the shared page brush with no radius; got {Describe(viewport.Background)}, radius {viewport.CornerRadius}");
            o.RequireBrush(viewport, e => ((Border)e).Background, DesignToken.Page, "the diagram viewport", key: key);

            // The dot grid: the Win2D surface under the canvas, the viewport's size, line dots every 18 x zoom.
            var dots = graphDotGrid ?? throw new InvalidOperationException($"{key} ({theme}): the diagram has no dot grid");
            Require(viewport.Child is Grid surface && surface.Children.Count == 2 && ReferenceEquals(surface.Children[0], dots.View) && ReferenceEquals(surface.Children[1], graphCanvas),
                $"{key} ({theme}): the dot grid must lie under the diagram canvas inside the viewport");
            Require(ReferenceEquals(dots.Ink, b.Brush(DesignToken.Line)), $"{key} ({theme}): the dot grid must draw with the shared line brush; got {Describe(dots.Ink)}");
            o.RequireBrush(dots.View, _ => dots.Ink, DesignToken.Line, "the dot grid's dots", key: key);
            Require(Math.Abs(dots.Step - MightyGraphDotGrid.Spacing * graphZoom) < 1e-9 && Math.Abs(dots.Radius - Math.Max(MightyGraphDotGrid.MinimumRadius, graphZoom)) < 1e-9 && dots.Shown == (MightyGraphDotGrid.Spacing * graphZoom >= MightyGraphDotGrid.MinimumStep),
                $"{key} ({theme}): the dots must be {MightyGraphDotGrid.Spacing} x zoom {graphZoom} apart with radius max(0.6, zoom); got step {dots.Step}, radius {dots.Radius}, shown {dots.Shown}");
            Require(Math.Abs(dots.View.ActualWidth - viewport.ActualWidth) < .5 && Math.Abs(dots.View.ActualHeight - viewport.ActualHeight) < .5,
                $"{key} ({theme}): the dot grid must cover exactly the viewport ({viewport.ActualWidth:F1}x{viewport.ActualHeight:F1}); got {dots.View.ActualWidth:F1}x{dots.View.ActualHeight:F1}");
            // It fills the surface with its one step x step tile in the theme's line colour, moved by the camera offset.
            var lineHex = "#FF" + FixtureHex(theme, DesignToken.Line)[1..];
            string Drawn() => $"filled {dots.LastDraw.Filled}, tile step {dots.LastDraw.Step}, {Hex(dots.LastDraw.Color)}, offset ({dots.LastDraw.OffsetX:F1}, {dots.LastDraw.OffsetY:F1}) on {dots.LastDraw.Width:F1}x{dots.LastDraw.Height:F1}, {dots.TileBuilds} tiles built, {dots.ResourceCreations} resource requests, {dots.Replacements} surfaces replaced";
            await WaitUI(() => dots.LastDraw.Filled && Math.Abs(dots.LastDraw.Step - dots.Step) < 1e-9 && Hex(dots.LastDraw.Color) == lineHex
                    && dots.LastDraw.OffsetX == graphPan.X && dots.LastDraw.OffsetY == graphPan.Y && Math.Abs(dots.LastDraw.Width - viewport.ActualWidth) < .5 && Math.Abs(dots.LastDraw.Height - viewport.ActualHeight) < .5,
                () => $"{key} ({theme}): the dot grid must fill the viewport with a {dots.Step} tile in token Line {lineHex} at the camera offset ({graphPan.X:F1}, {graphPan.Y:F1}); its last draw: {Drawn()}");
            // The dot stands on the middle of a device pixel in the tile's middle, so that pixel is all line colour (M/MightyGraphView.swift:1003-1008).
            var tilePixels = dots.Step * viewport.XamlRoot.RasterizationScale; var centre = dots.DotCentrePixels;
            Require(Math.Abs(centre - Math.Floor(centre) - 0.5) < 1e-6 && Math.Abs(centre - tilePixels / 2) <= 0.5 + 1e-6,
                $"{key} ({theme}): the tile's dot must stand on the middle of the device pixel in the tile's middle; got {centre:F3} px in a tile of {tilePixels:F3} px");
            // A pan moves the same tile: it draws again at the new offset without building another.
            // Win2D may ask for its resources again at any time (a theme change can reload the control),
            // which rebuilds the tile; only builds beyond those would come from the pan.
            var builds = dots.TileBuilds; var creations = dots.ResourceCreations;
            UserPan(7, 0);
            try
            {
                await WaitUI(() => dots.LastDraw.OffsetX == graphPan.X && dots.LastDraw.Filled,
                    () => $"{key} ({theme}): the dot grid did not follow a pan to x {graphPan.X:F1}; its last draw: {Drawn()}");
                Require(dots.TileBuilds - builds <= dots.ResourceCreations - creations,
                    $"{key} ({theme}): a pan must reuse the dot tile; {dots.TileBuilds - builds} more were built while Win2D asked for resources {dots.ResourceCreations - creations} times ({dots.Replacements} surfaces replaced)");
            }
            finally { UserPan(-7, 0); }

            // The Mighty bar (M/MightyGraphView.swift:175-201): one row, the view switch first, then the title and
            // the summary on the same line, the zoom at the right; padding h12 v10, its parts 10 apart, over a line.
            var bar = graphToolbar!; var lead = graphToolbarLead!; var track = graphViewSwitch!;
            Require(bar.Padding == new Thickness(12, 10, 12, 10) && bar.BorderThickness == new Thickness(0, 0, 0, DesignMetrics.Stroke.Line) && bar.ColumnSpacing == 10 && lead.Spacing == 10,
                $"{key} ({theme}): the Mighty bar must have padding h12 v10 and its parts 10 apart over a bottom Stroke.Line rule; got {bar.Padding}, {bar.BorderThickness}, spacing {bar.ColumnSpacing} / {lead.Spacing}");
            o.RequireBrush(bar, e => ((Grid)e).BorderBrush, DesignToken.Line, "the rule under the Mighty bar", key: key);
            Require(bar.RowDefinitions.Count == 0 && lead.Children.Count == 3 && ReferenceEquals(lead.Children[0], track) && ReferenceEquals(lead.Children[1], graphStyleHeader) && ReferenceEquals(lead.Children[2], graphStyleBadge)
                    && Grid.GetColumn(lead) == 0 && Grid.GetColumn(graphTotal) == 1 && Grid.GetColumn(graphZoomControls!) == 2,
                $"{key} ({theme}): the Mighty bar must be one row: the view switch, the title and its badge, the summary, then the zoom; got {lead.Children.Count} leading parts in columns {Grid.GetColumn(lead)} / {Grid.GetColumn(graphTotal)} / {Grid.GetColumn(graphZoomControls!)}");
            // 10 + the switch (22 in a 2pt track) + 10, over the rule.
            var barHeight = 2 * 10 + 22 + 2 * 2 + DesignMetrics.Stroke.Line;
            double Left(FrameworkElement part) => part.TransformToVisual(bar).TransformPoint(new Windows.Foundation.Point()).X;
            Require(Math.Abs(bar.ActualHeight - barHeight) < .5 && Left(track) < Left(graphStyleHeader) && Left(graphStyleHeader) < Left(graphTotal) && Left(graphTotal) < Left(graphZoomControls!),
                $"{key} ({theme}): the Mighty bar must be {barHeight} tall with the switch, the title, the summary and the zoom from left to right; got {bar.ActualHeight:F1} tall, at x {Left(track):F1} / {Left(graphStyleHeader):F1} / {Left(graphTotal):F1} / {Left(graphZoomControls!):F1}");
            Require(Math.Abs(Left(track) - 12) < .5 && Math.Abs(Left(graphStyleHeader) - (Left(track) + track.ActualWidth + 10)) < .5,
                $"{key} ({theme}): the view switch must lead the bar at its 12pt padding with the title 10 after it; got the switch at {Left(track):F1} ({track.ActualWidth:F1} wide), the title at {Left(graphStyleHeader):F1}");
            RequireFont(graphStyleHeader, DesignMetrics.Type.Block, FontWeights.Bold, $"({theme}) the Mighty bar title", key);
            o.RequireBrush(graphStyleHeader, e => ((TextBlock)e).Foreground, DesignToken.Ink, "the Mighty bar title", key: key);
            Require(graphTotal.FontSize == DesignMetrics.Type.Small, $"{key} ({theme}): the Mighty bar summary must be Type.Small {DesignMetrics.Type.Small}pt; got {graphTotal.FontSize}");
            o.RequireBrush(graphTotal, e => ((TextBlock)e).Foreground, DesignToken.Ink2, "the Mighty bar summary", key: key);
            Require(track.CornerRadius == new CornerRadius(DesignMetrics.Radius.Row) && track.Padding == new Thickness(2) && track.Spacing == 2, $"{key} ({theme}): the view switch track must be radius {DesignMetrics.Radius.Row}, padding 2, its options 2 apart; got {track.CornerRadius}, {track.Padding}, {track.Spacing}");
            o.RequireBrush(track, e => ((StackPanel)e).Background, DesignToken.Track, "the view switch track", key: key);
            Require(ViewWordsShown, $"{key} ({theme}): a bar this wide ({bar.ActualWidth:F0}) must show the view switch's words");
            foreach (var (option, selected, what) in new[] { (diagramButton!, true, "the chosen view (Diagram)"), (timelineButton!, false, "the other view (Timeline)") })
            {
                // The look is on the option's chip, symbol and words; the button keeps the resources written once.
                var (chip, optionSymbol, optionWords) = ViewOptionParts(option);
                Require(option.Height == 22 && option.Padding == new Thickness(0) && chip.Padding == new Thickness(9, 0, 9, 0) && option.CornerRadius == new CornerRadius(DesignMetrics.Radius.Segment) && chip.CornerRadius == option.CornerRadius,
                    $"{key} ({theme}): {what} must be 22 tall, its chip padding h9, both radius {DesignMetrics.Radius.Segment}; got {option.Height}, chip padding {chip.Padding} (button {option.Padding}), {option.CornerRadius} / {chip.CornerRadius}");
                RequireFont(optionWords, DesignMetrics.Type.Pill, selected ? FontWeights.Bold : FontWeights.SemiBold, $"({theme}) {what}", key);
                o.RequireBrush(optionWords, e => ((TextBlock)e).Foreground, selected ? DesignToken.Ink : DesignToken.Ink2, what + "'s words", key: key);
                // Each option carries its symbol before its word (M/MightyGraphTimelineView.swift:17-18), in the word's ink.
                Require(optionSymbol is Microsoft.UI.Xaml.Shapes.Path { IsLoaded: true, ActualWidth: > 0 } && Left(optionSymbol) < Left(optionWords),
                    $"{key} ({theme}): {what} must draw its symbol before its word; got {optionSymbol.GetType().Name} at {Left(optionSymbol):F1}, the word at {Left(optionWords):F1}");
                o.RequireBrush(optionSymbol, e => ((Microsoft.UI.Xaml.Shapes.Path)e).Stroke, selected ? DesignToken.Ink : DesignToken.Ink2, what + "'s symbol", key: key);
                if (selected) o.RequireBrush(chip, e => ((Border)e).Background, DesignToken.Card, what + "'s chip", key: key);
                else o.RequireClear(chip.Background, what + "'s chip", key);
                o.RequireClear(option.Background, what + "'s button at rest", key);
                o.RequireSubtle(await StateBackground(option, "PointerOver", b.Subtle, key), what + " under the pointer", key);
            }
            Require(zoomResetButton!.Width == 38 && zoomResetButton.Content is TextBlock { FontSize: DesignMetrics.Type.Body },
                $"{key} ({theme}): the zoom percentage must be 38 wide in the body size {DesignMetrics.Type.Body}; got {zoomResetButton.Width}, {(zoomResetButton.Content as TextBlock)?.FontSize}");
            foreach (var zoom in new[] { zoomOutButton!, zoomResetButton, zoomInButton! })
                o.RequireBrush(zoom, e => ((Control)e).Foreground, DesignToken.Ink2, "the zoom control " + AutomationProperties.GetAutomationId(zoom), key: key);

            // A block: the D card, its 38pt header over a line, the title and mark, the request band, the pill, the capsule.
            var view = graphCardViews[liveRequest]; var card = view.Card;
            RequireRadius(card, DesignMetrics.Radius.Block, $"({theme}) a block card (Radius.Block)", key);
            RequireThickness(card, DesignMetrics.Stroke.Line, $"({theme}) a block card (Stroke.Line)", key);
            Require(card.Padding == new Thickness(0), $"{key} ({theme}): a block card insets nothing, so its header and band run edge to edge; got padding {card.Padding}");
            o.RequireBrush(card, e => ((Border)e).Background, DesignToken.Card, "a block card", key: key);
            o.RequireBrush(card, e => ((Border)e).BorderBrush, DesignToken.Line, "a block card's edge", key: key);
            // The Mac strokes the card's 1pt edge over its content (M/MightyGraphActivityView.swift:142-146): the 38pt head
            // and its h12 padding are measured from the card's own edge, the edge included.
            var header = view.Header!; var edge = card.BorderThickness.Left;
            Require(header.Height + edge == DesignMetrics.Layout.BlockHead + DesignMetrics.Stroke.Line && header.Padding == new Thickness(12 - edge, 0, 12 - edge, 0) && header.ColumnSpacing == 7 && header.BorderThickness == new Thickness(0, 0, 0, DesignMetrics.Stroke.Line),
                $"{key} ({theme}): a block header must end Layout.BlockHead {DesignMetrics.Layout.BlockHead} from the card's top over a Stroke.Line rule, padding h12 from the card's edge, spacing 7; got {header.Height} under a {edge} edge, {header.Padding}, {header.ColumnSpacing}, {header.BorderThickness}");
            o.RequireBrush(header, e => ((Grid)e).BorderBrush, DesignToken.Line, "the rule under a block header", key: key);
            // The head starts with the block's symbol in its tint (a plain request: the message with its arrow, in accent).
            Require(view.Symbol is { IsLoaded: true } headed && Math.Abs(headed.ActualWidth - (headed is IconElement ? 13 : Math.Round(13 * MightySymbols.DrawnScale * 2) / 2)) < .5 && ReferenceEquals(header.Children[0], headed),
                $"{key} ({theme}): a block header must start with its 13pt symbol (a drawn one {MightySymbols.DrawnScale} times as wide, as on the Mac); got {view.Symbol?.GetType().Name ?? "none"} {view.Symbol?.ActualWidth}");
            o.RequireBrush(view.Symbol!, SymbolInk, TintToken(RequestStyleLook(live.Input).Tint), "the request block's symbol", key: key);
            foreach (var (agentId, token, what) in new[] { (waiting, DesignToken.AgentText, "the waiting sub-agent"), (MightyGraphLayout.NodeID(live, "agent:design-smoke-live-finished"), DesignToken.AgentText, "the finished sub-agent") })
                o.RequireBrush(graphCardViews[agentId].Symbol ?? throw new InvalidOperationException($"{key} ({theme}): {what}'s header has no symbol"), SymbolInk, token, what + "'s symbol", key: key);
            // It ends with the expand control, the last of its parts (M/MightyGraphView.swift:538-547), 12 from the card's edge.
            var expand = view.Expand ?? throw new InvalidOperationException($"{key} ({theme}): the request block's header has no expand control");
            var controls = (StackPanel)header.Children[^1];
            Require(ReferenceEquals(controls.Children[^1], expand) && controls.Spacing == 7 && AutomationProperties.GetAutomationId(expand) == "mighty-expand-" + liveRequest,
                $"{key} ({theme}): a block header must end with its expand control, its trailing parts 7 apart; got {controls.Children.Count} parts {controls.Spacing} apart");
            o.RequireBrush(expand, e => ((Control)e).Foreground, DesignToken.Ink2, "the expand control", key: key);
            var title = graphTitles[liveRequest];
            var words = Words(title);
            Require(words.Count > 0 && words.All(w => w.FontSize == DesignMetrics.Type.Block && w.FontWeight.Weight == FontWeights.Bold.Weight),
                $"{key} ({theme}): a request header's words must be Type.Block {DesignMetrics.Type.Block}pt bold; got {string.Join(", ", words.Select(w => $"{w.FontSize}pt/{w.FontWeight.Weight}"))}");
            foreach (var word in words) o.RequireBrush(word, e => ((TextBlock)e).Foreground, DesignToken.Ink, $"the request header word '{word.Text}'", key: key);
            if (ProviderMark.MarkedProvider(Session.Provider) is { } marked)
                Require(ProviderMarkView.LabelledProvider(title) == marked, $"{key} ({theme}): the request header must carry the {marked} mark before its name; got {ProviderMarkView.LabelledProvider(title) ?? "none"}");
            // The request keeps its place between the header and the transcript, its words 14 to 64 tall and scrolling within (M/MightyGraphView.swift:551-556, 1025-1042).
            var band = view.RequestBand!;
            Require(band.Visibility == Visibility.Visible && band.Padding == new Thickness(12 - edge, 8, 12 - edge, 8) && band.BorderThickness == new Thickness(0, 0, 0, DesignMetrics.Stroke.Line),
                $"{key} ({theme}): the request band must show with padding h12 (from the card's edge) v8 over a Stroke.Line rule; got {band.Visibility}, {band.Padding}, {band.BorderThickness}");
            Require(view.Body!.Children.Contains(band) && Grid.GetRow(band) == 1 && band.Child is ScrollViewer { MaxHeight: 64 } && view.Request!.MinHeight == 14 && graphBodies.TryGetValue(liveRequest, out var transcriptScroll) && Grid.GetRow(transcriptScroll) == 2,
                $"{key} ({theme}): the request band must stand between the header and the scrolling transcript, its words at most 64 tall; got row {Grid.GetRow(band)}, {band.Child?.GetType().Name}");
            o.RequireBrush(band, e => ((Border)e).Background, TintToken(RequestStyleLook(live.Input).Tint), "the request band (inputPreview)", DesignMetrics.Opacity.InputPreview, key);
            Require(view.Request!.FontSize == DesignMetrics.Type.Pill, $"{key} ({theme}): the request text must be {DesignMetrics.Type.Pill}pt; got {view.Request.FontSize}");
            o.RequireBrush(view.Request, e => ((TextBlock)e).Foreground, DesignToken.Ink, "the request text", key: key);
            RequirePill(view.StatePill, DesignTone.Run, 18, "the running request's status pill");
            RequirePill(graphCardViews[doneRequest].StatePill, DesignTone.Done, 18, "the finished request's status pill");
            RequirePill(graphCardViews[waiting].StatePill, DesignTone.Wait, 18, "the waiting sub-agent's status pill");
            var capsule = view.Capsule ?? throw new InvalidOperationException($"{key} ({theme}): the running request shows no usage capsule");
            Require(capsule.Padding == new Thickness(6, 2, 6, 2) && capsule.Child is TextBlock { FontSize: DesignMetrics.Type.Small } capsuleWords && capsuleWords.FontFamily?.Source == DesignMetrics.Font.Mono,
                $"{key} ({theme}): the usage capsule must be Font.Mono at Type.Small {DesignMetrics.Type.Small}pt with padding h6 v2; got {capsule.Padding}");
            o.RequireBrush(capsule, e => ((Border)e).Background, DesignToken.CardRaised, "the usage capsule", key: key);
            o.RequireBrush((TextBlock)capsule.Child, e => ((TextBlock)e).Foreground, DesignToken.Ink2, "the usage capsule's figures", key: key);

            // The activity mark: four run capsules, 3 wide and 2 apart, in an 18x14 box, each 12 tall and
            // scaled to its height by the pane's one running timer (no layout per tick).
            var activity = view.Activity ?? throw new InvalidOperationException($"{key} ({theme}): the running request shows no activity capsules (indicator {Session.GraphRuns?[1].Status})");
            var bars = activity.Children.OfType<Border>().ToList();
            Require(activity.Width == MightyGraphActivity.BarBoxWidth && activity.Height == MightyGraphActivity.BarBoxHeight && bars.Count == MightyGraphActivity.Bars && activity.Children.Count == MightyGraphActivity.Bars,
                $"{key} ({theme}): the activity mark must be {MightyGraphActivity.Bars} capsules in an {MightyGraphActivity.BarBoxWidth}x{MightyGraphActivity.BarBoxHeight} box; got {activity.Children.Count} in {activity.Width}x{activity.Height}");
            for (var i = 0; i < bars.Count; i++)
            {
                var scale = bars[i].RenderTransform as ScaleTransform;
                Require(bars[i].Width == MightyGraphActivity.BarWidth && bars[i].Height == MightyGraphActivity.BarTallest && Math.Abs(Canvas.GetLeft(bars[i]) - i * (MightyGraphActivity.BarWidth + MightyGraphActivity.BarGap)) < 1e-9
                    && Math.Abs(Canvas.GetTop(bars[i]) - 1) < 1e-9 && scale is not null && ReferenceEquals(scale, graphActivitySets.GetValueOrDefault(liveRequest)?[i]) && scale.ScaleY * MightyGraphActivity.BarTallest is >= 4 - 1e-6 and <= 12 + 1e-6,
                    $"{key} ({theme}): activity capsule {i} must be {MightyGraphActivity.BarWidth}x{MightyGraphActivity.BarTallest} at ({i * (MightyGraphActivity.BarWidth + MightyGraphActivity.BarGap)}, 1), scaled to 4..12 by the pane's wave; got {bars[i].Width}x{bars[i].Height} at ({Canvas.GetLeft(bars[i])}, {Canvas.GetTop(bars[i])}), scale {scale?.ScaleY.ToString() ?? "none"}");
                o.RequireBrush(bars[i], e => ((Border)e).Background, DesignToken.Run, $"activity capsule {i}", key: key);
            }
            Require(graphActivityTimer?.IsEnabled == true && graphActivitySets.Count == 1,
                $"{key} ({theme}): one pane timer must wave the one running block's capsules while the diagram shows; timer {(graphActivityTimer?.IsEnabled == true ? "running" : "stopped")}, {graphActivitySets.Count} sets");

            // The running outline: run at 2pt in the card's fitted 9/7 dashes, counted in stroke widths, over the runSoft halo.
            Require(graphOutlines.TryGetValue(liveRequest, out var marching) && marching.Kind == MightyGraphActivity.Marching, $"{key} ({theme}): the running request has no marching outline");
            var outline = marching.Line;
            var expectedDash = DesignMetrics.Dash.InStrokeUnits(MightyGraphActivity.DashForCard(card.Width, card.Height), MightyGraphActivity.LineWidth);
            var dash = outline.StrokeDashArray?.ToArray() ?? [];
            Require(outline.StrokeThickness == DesignMetrics.Dash.RunningWidth && dash.Length == 2 && dash.Zip(expectedDash).All(p => Math.Abs(p.First - p.Second) < 1e-6),
                $"{key} ({theme}): the running outline must be {DesignMetrics.Dash.RunningWidth}pt in Dash.InStrokeUnits(DashForCard({card.Width}, {card.Height}), 2) = [{string.Join(", ", expectedDash.Select(d => d.ToString("F4")))}]; got {outline.StrokeThickness}pt [{string.Join(", ", dash.Select(d => d.ToString("F4")))}]");
            o.RequireBrush(outline, e => ((Rectangle)e).Stroke, DesignToken.Run, "the running outline", key: key);
            var halo = graphOutlineViews[liveRequest].Halo ?? throw new InvalidOperationException($"{key} ({theme}): the running outline has no halo");
            Require(halo.StrokeThickness == 2 * MightyGraphActivity.LineWidth, $"{key} ({theme}): the run halo must be {2 * MightyGraphActivity.LineWidth}pt; got {halo.StrokeThickness}");
            o.RequireBrush(halo, e => ((Rectangle)e).Stroke, DesignToken.RunSoft, "the run halo", key: key);
            Require(graphOutlines.TryGetValue(waiting, out var still) && still.Kind == MightyGraphActivity.Waiting && (still.Line.StrokeDashArray?.Count ?? 0) == 0 && still.Line.StrokeThickness == 2,
                $"{key} ({theme}): the waiting sub-agent must keep a solid 2pt outline");
            o.RequireBrush(still.Line, e => ((Rectangle)e).Stroke, DesignToken.Wait, "the waiting outline", key: key);

            // The corner handle (M/MightyGraphView.swift:928-942): the two arrows in ink2 on card x 0.95, 22 square, radius 5, 2 from the card's corner.
            var grip = view.Grip ?? throw new InvalidOperationException($"{key} ({theme}): the request block has no corner handle");
            Require(grip.Width == 22 && grip.Height == 22 && grip.CornerRadius == new CornerRadius(DesignMetrics.Radius.FileRow) && grip.Margin == new Thickness(0, 0, 2 - edge, 2 - edge) && grip.HorizontalAlignment == HorizontalAlignment.Right && grip.VerticalAlignment == VerticalAlignment.Bottom,
                $"{key} ({theme}): a block's corner handle must be 22 square, radius {DesignMetrics.Radius.FileRow}, 2 from the card's bottom right corner; got {grip.Width}x{grip.Height}, {grip.CornerRadius}, {grip.Margin}");
            o.RequireBrush(grip, e => ((Border)e).Background, DesignToken.Card, "the corner handle's backing", GripOpacity, key);
            o.RequireBrush(view.GripSymbol!, SymbolInk, DesignToken.Ink2, "the corner handle's arrows", key: key);

            // The draft (M/MightyGraphView.swift:404-420): its dashed accent edge, [5, 4] pt at 1.5pt, stroked on the card's outline;
            // inside, 16 from the edge, "Next request" 12 semibold after its pencil, the state word 11 ink2, the draft 12 in four lines at most.
            var draft = graphDraftOutline!; var draftCard = graphCards[draftId]; var draftView = graphCardViews[draftId];
            var draftDash = draft.StrokeDashArray?.ToArray() ?? [];
            Require(draft.StrokeThickness == DesignMetrics.Dash.DraftWidth && draftDash.Length == 2 && Math.Abs(draftDash[0] - 5 / 1.5) < 1e-6 && Math.Abs(draftDash[1] - 4 / 1.5) < 1e-6,
                $"{key} ({theme}): the draft edge must be {DesignMetrics.Dash.DraftWidth}pt in [3.3333, 2.6667] stroke units ([5, 4] pt); got {draft.StrokeThickness}pt [{string.Join(", ", draftDash.Select(d => d.ToString("F4")))}]");
            Require(Math.Abs(draft.Width - (draftCard.Width + DesignMetrics.Dash.DraftWidth)) < 1e-9 && Math.Abs(Canvas.GetLeft(draft) - (Canvas.GetLeft(draftCard) - DesignMetrics.Dash.DraftWidth / 2)) < 1e-9 && draft.RadiusX == DesignMetrics.Radius.Block,
                $"{key} ({theme}): the draft's dashes must run on the card's radius-{DesignMetrics.Radius.Block} outline, half the line inside it; got {draft.Width} wide at {Canvas.GetLeft(draft)} for a {draftCard.Width} card at {Canvas.GetLeft(draftCard)}, radius {draft.RadiusX}");
            o.RequireBrush(draft, e => ((Rectangle)e).Stroke, DesignToken.Accent, "the draft's dashed edge (draft)", DesignMetrics.Opacity.Draft, key);
            Require(draftCard.BorderThickness == new Thickness(0), $"{key} ({theme}): the draft card shows only its dashes; got a {draftCard.BorderThickness} edge");
            var (draftTitle, draftState, draftText) = (draftView.DraftTitle!, draftView.DraftState!, draftView.Request!);
            RequireFont(draftTitle, DesignMetrics.Type.Block, FontWeights.SemiBold, $"({theme}) the draft's title", key);
            o.RequireBrush(draftTitle, e => ((TextBlock)e).Foreground, DesignToken.Ink, "the draft's title", key: key);
            o.RequireBrush(draftView.DraftSymbol!, SymbolInk, DesignToken.Ink, "the draft's pencil", key: key);
            Require(draftTitle.Text == MightyGraphBlockModel.DraftTitle(true) && draftState.Text == MightyGraphBlockModel.DraftState(Session.Draft) && draftState.FontSize == DesignMetrics.Type.Pill && draftText.Text == Session.Draft && draftText.FontSize == DesignMetrics.Type.Block && draftText.MaxLines == 4,
                $"{key} ({theme}): the draft must read '{MightyGraphBlockModel.DraftTitle(true)}' and '{MightyGraphBlockModel.DraftState(Session.Draft)}' (11pt) over the draft itself (12pt, four lines); got '{draftTitle.Text}', '{draftState.Text}' ({draftState.FontSize}pt), '{draftText.Text}' ({draftText.FontSize}pt, {draftText.MaxLines} lines)");
            o.RequireBrush(draftState, e => ((TextBlock)e).Foreground, DesignToken.Ink2, "the draft's state word", key: key);
            o.RequireBrush(draftText, e => ((TextBlock)e).Foreground, DesignToken.Ink, "the draft's words", key: key);
            double InCard(FrameworkElement part, bool vertical) { var at = part.TransformToVisual(draftCard).TransformPoint(new Windows.Foundation.Point()); return vertical ? at.Y : at.X; }
            Require(Math.Abs(InCard(draftView.DraftSymbol!, false) - 16) < .5 && Math.Abs(InCard(draftText, false) - 16) < .5 && Math.Abs(InCard(draftText, true) - (InCard(draftTitle, true) + draftTitle.ActualHeight + 10)) < 1.5 && draftView.Expand is null,
                $"{key} ({theme}): the draft's parts must sit 16 inside its card, the draft 10 under its title, with no expand control; got x {InCard(draftView.DraftSymbol!, false):F1} / {InCard(draftText, false):F1}, y {InCard(draftTitle, true):F1} + {draftTitle.ActualHeight:F1} then {InCard(draftText, true):F1}");

            // The result card's outcome strip: heroFill(done), its seal and every word heroInk. The Mac draws the strip as a
            // shape of the card's own radius, round at all four corners (docs/design-system/crops/result-card-*.webp).
            var result = graphCardViews[doneResult]; var strip = result.Header!; var resultEdge = result.Card.BorderThickness.Left;
            o.RequireBrush(strip, e => ((Grid)e).Background, DesignPalette.FillToken(DesignTone.Done), "the finished result's strip (heroFill)", key: key);
            Require(strip.Height + resultEdge == DesignMetrics.Layout.BlockHead && strip.BorderThickness == new Thickness(0) && strip.CornerRadius == new CornerRadius(DesignMetrics.Radius.Block - resultEdge),
                $"{key} ({theme}): the result strip must end Layout.BlockHead {DesignMetrics.Layout.BlockHead} from the card's top with no rule and the card's radius at every corner; got {strip.Height} under a {resultEdge} edge, {strip.BorderThickness}, {strip.CornerRadius}");
            foreach (var word in Words(graphTitles[doneResult]))
                o.RequireBrush(word, e => ((TextBlock)e).Foreground, DesignToken.OnStatus, "the result title on its strip (heroInk)", key: key);
            o.RequireBrush(result.Symbol ?? throw new InvalidOperationException($"{key} ({theme}): the result strip has no seal"), SymbolInk, DesignToken.OnStatus, "the result strip's seal (heroInk)", key: key);
            Require(result.StatePill is null, $"{key} ({theme}): the result strip shows no status pill");
            // Core gives a result block no usage capsule (it has no records of its own), as on the Mac.
            Require(result.Capsule is null, $"{key} ({theme}): the result strip shows no usage capsule");

            // Edges (M/MightyGraphView.swift:372-398): each turns once on its way down and ends under an arrow head 4 to
            // either side and 6 back; the quiet ones are one ink2 x 0.45 stroke at 1.5pt, those into a running block one run stroke at 2pt.
            var into = graphEdgeRoutes.FirstOrDefault(e => e.Target == liveRequest); var quiet = graphEdgeRoutes.FirstOrDefault(e => e.Target == draftId);
            Require(into.Route is not null && into.Live && quiet.Route is not null && !quiet.Live,
                $"{key} ({theme}): the diagram must draw a run edge into the running request and a quiet one into the draft; edges into [{string.Join(", ", graphEdgeRoutes.Select(e => e.Target + (e.Live ? " (run)" : "")))}]");
            foreach (var (edgeRoute, what) in new[] { (into, "the edge into the running request"), (quiet, "the edge into the draft") })
            {
                var (route, head) = (edgeRoute.Route, edgeRoute.Head);
                Require(route.Length == 4 && route[0].X == route[1].X && route[1].Y == route[2].Y && route[2].X == route[3].X && route[3].Y > route[0].Y
                        && head.Length == 3 && head[1] == route[3] && head[0] == new Windows.Foundation.Point(route[3].X - 4, route[3].Y - 6) && head[2] == new Windows.Foundation.Point(route[3].X + 4, route[3].Y - 6),
                    $"{key} ({theme}): {what} must go down, across and down to its block's top centre under a (±4, −6) arrow head; got route [{string.Join(" ", route.Select(p => $"({p.X:F1},{p.Y:F1})"))}] head [{string.Join(" ", head.Select(p => $"({p.X:F1},{p.Y:F1})"))}]");
            }
            foreach (var (stroke, live2, token, opacity, width, what) in new[] { (graphLiveEdges, true, DesignToken.Run, 1.0, DesignMetrics.Stroke.Active, "the stroke of the edges into running blocks"), (graphQuietEdges, false, DesignToken.Ink2, DesignMetrics.Opacity.Edge, DesignMetrics.Stroke.Focus, "the stroke of the quiet edges") })
            {
                var figures = (stroke.Data as PathGeometry)?.Figures.Count ?? -1; var expected = 2 * graphEdgeRoutes.Count(e => e.Live == live2);
                Require(stroke.StrokeThickness == width && figures == expected && graphCanvas.Children.Contains(stroke) && stroke.StrokeLineJoin == PenLineJoin.Round && stroke.StrokeEndLineCap == PenLineCap.Round,
                    $"{key} ({theme}): {what} must be one {width}pt round-capped path of {expected} figures (a route and a head for each edge); got {stroke.StrokeThickness}pt, {figures} figures");
                o.RequireBrush(stroke, e => ((Microsoft.UI.Xaml.Shapes.Path)e).Stroke, token, what, opacity, key);
            }

            // Selection (M/MightyGraphView.swift:498, 917-924, 932): a 2pt accent line on the card's outline (a point inside, a
            // point outside), the scroll hint in the header and the corner handle in accent; the card keeps its own 1pt line edge.
            Require(view.ScrollHint is { Visibility: Visibility.Collapsed } && (graphSelectionOutline is null || !graphCanvas.Children.Contains(graphSelectionOutline)),
                $"{key} ({theme}): with nothing selected no block shows the scroll hint or the selection outline");
            SelectGraphBlock(liveRequest);
            try
            {
                var selection = graphSelectionOutline ?? throw new InvalidOperationException($"{key} ({theme}): the selected block has no outline");
                Require(graphCanvas.Children.Contains(selection) && selection.StrokeThickness == DesignMetrics.Stroke.Active && selection.RadiusX == DesignMetrics.Radius.Block
                        && Math.Abs(selection.Width - (card.Width + 2)) < 1e-9 && Math.Abs(selection.Height - (card.Height + 2)) < 1e-9 && Math.Abs(Canvas.GetLeft(selection) - (Canvas.GetLeft(card) - 1)) < 1e-9 && Math.Abs(Canvas.GetTop(selection) - (Canvas.GetTop(card) - 1)) < 1e-9,
                    $"{key} ({theme}): the selection must be a {DesignMetrics.Stroke.Active}pt line on the card's radius-{DesignMetrics.Radius.Block} outline; got {selection.StrokeThickness}pt, radius {selection.RadiusX}, {selection.Width}x{selection.Height} at ({Canvas.GetLeft(selection)}, {Canvas.GetTop(selection)}) for a {card.Width}x{card.Height} card at ({Canvas.GetLeft(card)}, {Canvas.GetTop(card)})");
                o.RequireBrush(selection, e => ((Rectangle)e).Stroke, DesignToken.Accent, "the selected block's outline", key: key);
                RequireThickness(card, DesignMetrics.Stroke.Line, $"({theme}) the selected block's own edge (Stroke.Line)", key);
                o.RequireBrush(card, e => ((Border)e).BorderBrush, DesignToken.Line, "the selected block's own edge", key: key);
                Require(view.ScrollHint is { Visibility: Visibility.Visible, FontSize: DesignMetrics.Type.Badge }, $"{key} ({theme}): the selected block's header must show the 9pt scroll hint");
                o.RequireBrush(view.ScrollHint!, e => ((TextBlock)e).Foreground, DesignToken.Accent, "the scroll hint", key: key);
                o.RequireBrush(view.GripSymbol!, SymbolInk, DesignToken.Accent, "the selected block's corner handle", key: key);
            }
            finally { ClearGraphSelection(); }
            Require(!graphCanvas.Children.Contains(graphSelectionOutline!) && view.ScrollHint!.Visibility == Visibility.Collapsed, $"{key} ({theme}): clearing the selection must take its outline and scroll hint away");
            o.RequireBrush(view.GripSymbol!, SymbolInk, DesignToken.Ink2, "the corner handle after its selection cleared", key: key);
            RequireThickness(card, DesignMetrics.Stroke.Line, $"({theme}) a block after its selection cleared (Stroke.Line)", key);


            if (before is not null)
                Require(ReferenceEquals(viewport, before.Viewport) && ReferenceEquals(dots, before.Dots) && ReferenceEquals(card, before.Card) && ReferenceEquals(outline, before.Outline) && ReferenceEquals(draft, before.Draft),
                    $"{key} ({theme}): the theme toggle replaced the viewport, dot grid, block card, running outline or draft edge instead of recolouring them in place");
            await CaptureMightyDiagram(theme, liveRequest, doneResult);
            return (viewport, dots, card, outline, draft);
        }

        /// <summary>
        /// Pictures of the fixture's diagram for the design review, in the theme on screen: as the camera
        /// stands on the running request, the finished result with the request after it, and the running
        /// request with its sub-agents and the draft at 60%. The camera goes back where it stood.
        /// </summary>
        private async Task CaptureMightyDiagram(string theme, string liveRequest, string doneResult)
        {
            var directory = owner.options.ProfileDirectory!;
            var (zoom, panX, panY) = (graphZoom, graphPan.X, graphPan.Y);
            async Task Capture(string name) { await SettleDesktopCapture(Container); await owner.CaptureSmoke(Path.Combine(directory, $"smoke-mighty-diagram-{theme}{name}.png")); }
            try
            {
                await Capture("");
                graphPan.Y = 16 - Canvas.GetTop(graphCards[doneResult]) * graphZoom;
                await Capture("-result");
                SetGraphZoom(MightyGraphViewModel.ZoomLevels()[1]); owner.root.UpdateLayout();
                graphPan.X = (graphViewport!.ActualWidth - graphCanvas.Width * graphZoom) / 2; graphPan.Y = 8 - Canvas.GetTop(graphCards[liveRequest]) * graphZoom;
                await Capture("-all");
            }
            finally { SetGraphZoom(zoom); graphPan.X = panX; graphPan.Y = panY; owner.root.UpdateLayout(); }
        }

        private async Task<(Border RowCard, Border ResultCard, Grid ResultHead)> RequireMightyTimeline(IReadOnlyList<MightyGraphRun> runs, MightyDesignViews? before)
        {
            const string key = MightyDesignKey; var o = owner; var theme = o.SmokeTheme;
            var (done, live) = (runs[0], runs[1]);
            var liveRequest = MightyGraphLayout.NodeID(live, "request"); var doneResult = MightyGraphLayout.NodeID(done, "result");
            var waiting = MightyGraphLayout.NodeID(live, "agent:design-smoke-live-waiting"); var finished = MightyGraphLayout.NodeID(live, "agent:design-smoke-live-finished");
            // The dark pass reads the timeline the light pass left on screen: no redraw in between.
            if (before is null) await SetGraphPresentation("timeline");
            await WaitUI(() => timelineScroll is { Visibility: Visibility.Visible } && timelineHeads.ContainsKey(live.Id) && new[] { liveRequest, waiting, finished }.All(timelineRows.ContainsKey) && timelineResults.ContainsKey(doneResult)
                && timelineRows[liveRequest].Card.IsLoaded && timelineResults[doneResult].Card.IsLoaded,
                () => $"{key} ({theme}): the timeline did not show the fixture's open request, its rows and the finished result");
            Require(graphActivityTimer?.IsEnabled != true, $"{key} ({theme}): the capsule timer must stop while the timeline hides the diagram");
            o.root.UpdateLayout();
            if (before is not null)
                Require(ReferenceEquals(timelineRows[liveRequest].Card, before.RowCard) && ReferenceEquals(timelineResults[doneResult].Card, before.ResultCard) && ReferenceEquals(timelineResults[doneResult].Head, before.ResultHead),
                    $"{key} ({theme}): the theme toggle rebuilt the timeline instead of recolouring it in place");

            o.RequireBrush(timelineScroll!, e => ((ScrollViewer)e).Background, DesignToken.CardRaised, "the timeline list", key: key);

            // The request header: the heading font (decision Q1) at 16 in ink, the quiet chevron, request and tally, the 20pt pill.
            var head = timelineHeads[live.Id];
            var words = Words(head.Title);
            Require(words.Count > 0 && words.All(w => w.FontFamily?.Source == DesignMetrics.Font.Heading && w.FontSize == DesignMetrics.Type.Timeline && w.FontWeight.Weight == FontWeights.Bold.Weight),
                $"{key} ({theme}): the timeline request header must be Font.Heading '{DesignMetrics.Font.Heading}' bold at Type.Timeline {DesignMetrics.Type.Timeline}; got {string.Join(", ", words.Select(w => $"'{w.FontFamily?.Source}' {w.FontSize}pt/{w.FontWeight.Weight}"))}");
            foreach (var word in words) o.RequireBrush(word, e => ((TextBlock)e).Foreground, DesignToken.Ink, $"the timeline header word '{word.Text}'", key: key);
            if (ProviderMark.MarkedProvider(Session.Provider) is { } marked)
                Require(ProviderMarkView.LabelledProvider(head.Title) == marked, $"{key} ({theme}): the timeline request header must carry the {marked} mark; got {ProviderMarkView.LabelledProvider(head.Title) ?? "none"}");
            Require(head.Chevron.FontSize == 10, $"{key} ({theme}): the timeline chevron must be 10pt; got {head.Chevron.FontSize}");
            o.RequireBrush(head.Chevron, e => ((FontIcon)e).Foreground, DesignToken.Ink2, "the timeline chevron", key: key);
            Require(head.Prompt is { FontSize: DesignMetrics.Type.Block, MaxLines: 2 }, $"{key} ({theme}): the timeline request text must be {DesignMetrics.Type.Block}pt in at most two lines");
            o.RequireBrush(head.Prompt!, e => ((TextBlock)e).Foreground, DesignToken.Ink2, "the timeline request text", key: key);
            RequireFont(head.Tally, DesignMetrics.Type.Pill, FontWeights.SemiBold, $"({theme}) the timeline tally", key);
            o.RequireBrush(head.Tally, e => ((TextBlock)e).Foreground, DesignToken.Ink2, "the timeline tally", key: key);
            RequirePill(head.Pill, DesignTone.Run, 20, "the timeline request's status pill");

            // The open request's parts stand 8 apart and its rows 6 (M/MightyGraphView.swift:704-707): the header, the rows, no result yet.
            var group = timelineGroups[live.Id].View;
            Require(group.Spacing == 8 && group.Children.Count == 2 && group.Children[1] is StackPanel { Spacing: TimelineRowGap, Children.Count: 3 },
                $"{key} ({theme}): the open request must hold its header and its three rows, 8 and {TimelineRowGap} apart; got {group.Children.Count} parts {group.Spacing} apart, rows {(group.Children.Count > 1 && group.Children[1] is StackPanel listed ? $"{listed.Children.Count} at {listed.Spacing}" : "missing")}");

            // The rows: card r14, run 2pt while running, wait 2pt while waiting, line 1pt once finished, padding h11 v7 from the
            // card's own edge (the Mac strokes the edge over the row); the node on its rail with the block's symbol in heroInk.
            foreach (var (node, edge, width, tone, what) in new[] { (liveRequest, DesignToken.Run, DesignMetrics.Stroke.Active, DesignTone.Run, "the running row"), (waiting, DesignToken.Wait, DesignMetrics.Stroke.Active, DesignTone.Wait, "the waiting row"), (finished, DesignToken.Line, DesignMetrics.Stroke.Line, DesignTone.Done, "a finished row") })
            {
                var row = timelineRows[node];
                RequireRadius(row.Card, DesignMetrics.Radius.TimelineRow, $"({theme}) {what} (Radius.TimelineRow)", key);
                RequireThickness(row.Card, width, $"({theme}) {what}'s edge", key);
                o.RequireBrush(row.Card, e => ((Border)e).Background, DesignToken.Card, what, key: key);
                o.RequireBrush(row.Card, e => ((Border)e).BorderBrush, edge, what + "'s edge", key: key);
                Require(row.Card.Child is StackPanel { Children: [Button face, ..] } && face.Padding == new Thickness(11 - width, 7 - width, 11 - width, 7 - width),
                    $"{key} ({theme}): {what} must be padded h11 v7 from its card's edge; got {((row.Card.Child as StackPanel)?.Children[0] as Button)?.Padding} inside a {width} edge");
                Require(row.Node.Width == 24 && row.Node.BorderThickness == new Thickness(DesignMetrics.Stroke.Rail), $"{key} ({theme}): {what}'s node must be 24 with a Stroke.Rail ring; got {row.Node.Width}, {row.Node.BorderThickness}");
                o.RequireBrush(row.Node, e => ((Border)e).Background, DesignPalette.FillToken(tone), what + "'s node (heroFill)", key: key);
                o.RequireBrush(row.Node, e => ((Border)e).BorderBrush, DesignToken.CardRaised, what + "'s node ring", key: key);
                o.RequireBrush(row.Node.Child as FrameworkElement ?? throw new InvalidOperationException($"{key} ({theme}): {what}'s node has no symbol"), SymbolInk, DesignPalette.FillInkToken(tone), what + "'s node symbol (heroInk)", key: key);
                RequirePill(row.Pill, tone, 19, what + "'s status pill");
            }
            // A settled row is its title over one line, 46 high as the Mac's: v7 around the 13pt title on 16, 2, and the 11pt line on 14 (M/MightyGraphTimelineView.swift:160-180).
            var twoLines = 7 + TimelineTitleLine + 2 + TimelineMetaLine + 7;
            Require(Math.Abs(timelineRows[finished].Card.ActualHeight - twoLines) < .6, $"{key} ({theme}): a timeline row of a title and one line must be {twoLines} high; got {timelineRows[finished].Card.ActualHeight:F1}");
            // The running row's latest step follows the pulsing run dot, 7 across (M/MightyGraphTimelineView.swift:171).
            var pulse = VisualChildren(timelineRows[liveRequest].Card).OfType<Microsoft.UI.Xaml.Shapes.Ellipse>().FirstOrDefault(dot => dot.Width == 7)
                ?? throw new InvalidOperationException($"{key} ({theme}): the running row shows no pulse dot beside its latest step");
            o.RequireBrush(pulse, e => ((Microsoft.UI.Xaml.Shapes.Ellipse)e).Fill, DesignToken.Run, "the running row's pulse dot", key: key);
            Require(!VisualChildren(timelineRows[waiting].Card).OfType<Microsoft.UI.Xaml.Shapes.Ellipse>().Any(), $"{key} ({theme}): only a running row carries the pulse dot");

            // The result card: card r16 with no edge, its strip heroFill(done) with the seal, the title and the caption in heroInk.
            // The Mac draws the strip as a shape of the card's own radius, round at every corner (docs/design-system/crops/timeline-*.webp).
            var result = timelineResults[doneResult];
            RequireRadius(result.Card, DesignMetrics.Radius.Composer, $"({theme}) the timeline result card", key);
            RequireThickness(result.Card, 0, $"({theme}) the timeline result card (no edge)", key);
            o.RequireBrush(result.Card, e => ((Border)e).Background, DesignToken.Card, "the timeline result card", key: key);
            Require(result.Head.Padding == new Thickness(13, 6, 13, 6) && result.Head.CornerRadius == new CornerRadius(DesignMetrics.Radius.Composer),
                $"{key} ({theme}): the timeline result strip must have padding h13 v6 and the card's radius at every corner; got {result.Head.Padding}, {result.Head.CornerRadius}");
            o.RequireBrush(result.Head, e => ((Grid)e).Background, DesignPalette.FillToken(DesignTone.Done), "the timeline result strip (heroFill)", key: key);
            o.RequireBrush(((Panel)result.Head.Children[0]).Children[0] as FrameworkElement ?? throw new InvalidOperationException($"{key} ({theme}): the timeline result strip has no seal"), SymbolInk, DesignToken.OnStatus, "the timeline result's seal (heroInk)", key: key);
            // The answer is plain words, 12.5 in ink, eight lines until it is opened (M/MightyGraphTimelineView.swift:216, 234-236).
            Require(result.Body is { FontSize: TimelineResultBody, MaxLines: TimelineResultLines, TextWrapping: TextWrapping.Wrap } && result.Body.Text == MightyTimeline.Groups(runs)[0].Result?.Text,
                $"{key} ({theme}): the timeline result must show its answer as {TimelineResultBody}pt words in {TimelineResultLines} lines; got {result.Body?.FontSize}pt, {result.Body?.MaxLines} lines");
            o.RequireBrush(result.Body!, e => ((TextBlock)e).Foreground, DesignToken.Ink, "the timeline result's answer", key: key);
            RequireFont(result.Title, DesignMetrics.Type.Block, FontWeights.ExtraBold, $"({theme}) the timeline result title", key);
            o.RequireBrush(result.Title, e => ((TextBlock)e).Foreground, DesignToken.OnStatus, "the timeline result title (heroInk)", key: key);
            Require(result.Caption.FontSize == 10.5 && result.Caption.FontFamily?.Source == DesignMetrics.Font.Mono, $"{key} ({theme}): the timeline result caption must be Font.Mono at 10.5pt; got '{result.Caption.FontFamily?.Source}' at {result.Caption.FontSize}");
            o.RequireBrush(result.Caption, e => ((TextBlock)e).Foreground, DesignToken.OnStatus, "the timeline result caption (heroInk)", key: key);
            var more = VisualChildren(timelineScroll!).OfType<Button>().FirstOrDefault(button => AutomationProperties.GetAutomationId(button) == "mighty-timeline-result-more-" + doneResult)
                ?? throw new InvalidOperationException($"{key} ({theme}): the folded result shows no 'show all' link");
            RequireFont((TextBlock)more.Content, DesignMetrics.Type.Block, FontWeights.Bold, $"({theme}) the 'show all' link", key);
            o.RequireBrush(more, e => ((Control)e).Foreground, DesignToken.Accent, "the 'show all' link", key: key);
            // A picture of the fixture's timeline for the design review; nothing is redrawn for it.
            await SettleDesktopCapture(Container); await o.CaptureSmoke(Path.Combine(o.options.ProfileDirectory!, $"smoke-mighty-timeline-{theme}.png"));
            return (timelineRows[liveRequest].Card, result.Card, result.Head);
        }

        /// <summary>Every word of a title line: one text, or the head and name around an agent mark.</summary>
        private static List<TextBlock> Words(FrameworkElement line) => line is TextBlock single ? [single] : line is Panel panel ? panel.Children.OfType<TextBlock>().ToList() : [];

        /// <summary>The ink a Mighty symbol is drawn in: a glyph's foreground, a drawn symbol's stroke or fill (of its own parts, not a cut-out detail).</summary>
        private static Brush? SymbolInk(FrameworkElement symbol) => symbol switch
        {
            IconElement icon => icon.Foreground,
            TextBlock words => words.Foreground,
            Microsoft.UI.Xaml.Shapes.Path path => path.Stroke ?? path.Fill,
            Panel pair => pair.Children.OfType<Microsoft.UI.Xaml.Shapes.Path>().Where(part => part.Tag is true).Select(part => part.Stroke ?? part.Fill).FirstOrDefault(),
            _ => null,
        };

        /// <summary>A status pill: the tone's text ink on its soft tint, 10 bold, padding h7, <paramref name="height"/> tall.</summary>
        private void RequirePill(Border? pill, DesignTone tone, double height, string what)
        {
            const string key = MightyDesignKey; var o = owner; var theme = o.SmokeTheme;
            Require(pill is { Child: TextBlock }, $"{key} ({theme}): {what} is missing");
            var words = (TextBlock)pill!.Child;
            Require(pill.Height == height && pill.Padding == new Thickness(7, 0, 7, 0) && pill.CornerRadius == new CornerRadius(height / 2),
                $"{key} ({theme}): {what} must be a {height}-tall capsule with padding h7; got {pill.Height}, {pill.Padding}, {pill.CornerRadius}");
            RequireFont(words, DesignMetrics.Type.Small, FontWeights.Bold, $"({theme}) {what}", key);
            o.RequireBrush(pill, e => ((Border)e).Background, DesignPalette.SoftToken(tone), what, key: key);
            o.RequireBrush(words, e => ((TextBlock)e).Foreground, DesignPalette.TextToken(tone), what + "'s words", key: key);
        }
    }
}
