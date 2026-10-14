using System.Globalization;
using System.Runtime.InteropServices;
using MightyClaude.Core;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI.Text;
using PointOptions = Microsoft.UI.Text.PointOptions;
using Rectangle = Microsoft.UI.Xaml.Shapes.Rectangle;
using TextRangeUnit = Microsoft.UI.Text.TextRangeUnit;
using VirtualKey = Windows.System.VirtualKey;
using VirtualKeyModifiers = Windows.System.VirtualKeyModifiers;

namespace MightyClaude.WinUI;

public sealed partial class MainWindow
{
    private sealed partial class PaneView
    {
        private ScrollViewer? structureScroll;
        /// <summary>The page inside the scroll view: a page shorter than the pane sits right above the composer (M/MightyStructureView.swift:721-744).</summary>
        private readonly Border structurePage = new()
        {
            Padding = new Thickness(DesignMetrics.Inset.GraphBlockBodyH, DesignMetrics.Spacing.Md, DesignMetrics.Inset.GraphBlockBodyH, DesignMetrics.Spacing.Md),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom,
        };
        /// <summary>The model and what it was built from, rebuilt only when something it draws changed (M/MightyGraphView.swift:60-70, 326-337).</summary>
        private MightyStructure.Signature? structureSignature;
        private MightyStructure.Shape structureShape = MightyStructure.Shape.Free([]);
        private readonly MightyStructure.ItemCache structureItems = new();
        /// <summary>
        /// What the view keeps of the pane while it shows its diagram or timeline (M/MightyStructureView.swift:7-30): the texts
        /// opened past their folded lines, by key; the finished requests the user opened by hand, by run id; and whether the page
        /// follows the newest card.
        /// </summary>
        private readonly HashSet<string> structureExpanded = [];
        private HashSet<string> structureUnfolded = [];
        private readonly MightyStructureFollow.PageWatch structureFollow = new();
        private bool structureShown, structureAppearing;
        private string? structureNewestId;
        /// <summary>The frame (the rail and its columns, or the rows), the column width it stands at, and each request's card as last built.</summary>
        private string? structureFrameKey;
        private double structureWidth;
        private StackPanel? structureRail;
        private readonly Dictionary<string, StackPanel> structureColumns = [];
        private readonly Dictionary<string, StructureCard> structureCards = [];
        private readonly Dictionary<string, (RichEditBox View, string Source, bool Light)> structureAnswers = [];
        /// <summary>A left press on the page that may become a drag (where it went down and where the page stood), the drag it became, and when the last one ended.</summary>
        private (Point Point, double X, double Y)? structurePress;
        private bool structurePanning, structurePanned;
        private long structurePanReleasedAt;
        /// <summary>
        /// The user's own scroll (M/MightyStructureView.swift:733-739): the wheel, a key, a finger or the scroll bar moving the page
        /// in steps, a press held on the scroll bar, and when the last wheel notch or scrolling key came that the page may report late.
        /// </summary>
        private bool structureScrolling, structureBarHeld;
        private long structureInputAt;
        /// <summary>
        /// What a press went down on, until it comes up; whether a part was left as it was for a press or a selection, to be built
        /// once that ends; and a draw a card's own button asked for, which waits for nothing.
        /// </summary>
        private DependencyObject? structurePressed;
        private bool structureDeferred, structureForced;

        private readonly record struct StructureCardKey(MightyStructure.Item Item, bool Free, double Width, bool Folded, string Open, string Heading, string Language, IReadOnlyList<ModelOption>? Catalog);

        /// <summary>
        /// One request's card as last built: the element that keeps its place in its column, what the card and its frame (folded,
        /// open under a phase, or an open row) were built from, and its parts (the request, the run, the result, each sub-agent's
        /// card) with what each was built from, so a streamed step builds again only the part it changed. An open card's sub-agent
        /// trees stand in one panel; an open row's three cells are a grid whose columns a new width resizes.
        /// </summary>
        private sealed class StructureCard
        {
            internal readonly Grid View = new();
            internal StructureCardKey? Key;
            internal string Frame = "";
            internal readonly Dictionary<string, (object Key, FrameworkElement View)> Parts = [];
            internal StackPanel? Clusters;
            internal string ClustersKey = "";
            internal Grid? Cells;
        }

        /// <summary>A phase chip's height, a card's and a sub-agent card's corners, a sub-agent card's status bar and a tool call's glyph column (M/MightyStructureView.swift:60-68).</summary>
        private const double StructureChipHeight = 20, StructureCardCorner = DesignMetrics.Radius.Entry, StructureNodeCorner = DesignMetrics.Radius.Row, StructureStatusBar = DesignMetrics.Stroke.Rail, StructureGlyphWidth = 14;
        /// <summary>Type sizes: a card's texts, a sub-agent's output, its prompt (M/MightyStructureView.swift:71-74).</summary>
        private const double StructureTextSize = DesignMetrics.Type.Block, StructureOutputSize = DesignMetrics.Type.Pill, StructurePromptSize = 10.5;
        /// <summary>Points around a text's laid-out words that still press the text (M/MightyStructurePanning.swift:145-146).</summary>
        private const double StructureTextSlop = 4;

        // ── the page ──────────────────────────────────────────────────────────

        /// <summary>
        /// The structure page (M/MightyStructureView.swift:76-141): it scrolls both ways on <c>cardRaised</c>, its content
        /// <c>Inset.GraphBlockBodyH</c> by <c>Spacing.Md</c> in. Dragging its empty space moves it, and every report of where it
        /// stands decides whether it follows the newest card (<see cref="MightyStructureFollow.PageWatch"/>).
        /// </summary>
        private void BuildMightyStructure()
        {
            structureScroll = new ScrollViewer
            {
                Content = structurePage, Visibility = Visibility.Collapsed, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollMode = ScrollMode.Auto, HorizontalScrollMode = ScrollMode.Auto, IsTabStop = true, Background = owner.brushes.Brush(DesignToken.CardRaised),
            };
            AutomationProperties.SetAutomationId(structureScroll, "mighty-structure-" + id);
            AutomationProperties.SetName(structureScroll, Locale.Get("graph.view.structure"));
            structureScroll.ViewChanged += (_, args) =>
            {
                // The page moves in steps only under the user's wheel, keys, finger or scroll bar: the view's own scrolls land at once.
                if (args.IsIntermediate) structureScrolling = true;
                var user = StructureUserMoving;
                if (!args.IsIntermediate) structureScrolling = false;
                ObserveStructurePage(user);
            };
            // The columns share the pane's width: a new width sizes them again. A new height (a taller composer) moves no content, yet may hide the newest card.
            structureScroll.SizeChanged += (_, args) =>
            {
                if (args.NewSize.Width != args.PreviousSize.Width) QueueGraphRefresh();
                if (args.NewSize.Height != args.PreviousSize.Height) ObserveStructurePage(false);
            };
            structurePage.SizeChanged += (_, _) => ObserveStructurePage(false);
            structureScroll.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnStructurePointerPressed), true);
            structureScroll.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnStructurePointerMoved), true);
            structureScroll.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnStructurePointerReleased), true);
            structureScroll.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler((_, _) => { EndStructurePan(); EndStructurePress(); }), true);
            // The page's own capture ends a drag; another element's ends its press, unless the page took the pointer from it for a drag.
            structureScroll.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler((_, args) =>
            {
                if (ReferenceEquals(args.OriginalSource, structureScroll)) EndStructurePan();
                if (!structurePanning) EndStructurePress();
            }), true);
            structureScroll.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(OnStructureWheel), true);
            structureScroll.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(OnStructureKeyDown), true);
            Grid.SetRow(structureScroll, 1); graphHost!.Children.Add(structureScroll);
        }

        /// <summary>Shows or hides the page. Shown again, a following reader is taken to the newest card by the next draw (M/MightyStructureView.swift:119).</summary>
        private void ShowMightyStructure(bool shown)
        {
            if (structureScroll is null) return;
            structureScroll.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
            if (shown == structureShown) return;
            structureShown = shown;
            if (shown) structureAppearing = true;
            else { EndStructurePan(); structurePressed = null; structureBarHeld = structureScrolling = false; }
        }

        /// <summary>
        /// What the structure reads beside the runs: the pane's style, its phase as the phase bar shows it, and the request whose
        /// plan waits for an answer. A Windows plan request carries the pane's id, so that request is the one the diagram draws the
        /// plan under (<see cref="PlanCardSupport.DiagramPlanRunID"/>).
        /// </summary>
        private (StyleEvaluator? Style, StylePhase? Phase, string? PendingPlan) StructureInputs(IReadOnlyList<MightyGraphRun> runs, bool startingNew)
        {
            var style = activeStyle;
            return (style?.Evaluator, style is null ? null : style.Evaluator.EffectivePhase(GuidedPhase(style), startingNew), PlanCardSupport.DiagramPlanRunID(PendingPlan, true, runs));
        }

        /// <summary>
        /// Draws the pane's structure (M/MightyStructureView.swift:76-187, 251-261): a styled pane's phases as a rail of chips
        /// with a column of request cards under each, a free pane's requests as one request → run → result row each. A card is
        /// brought up to date part by part (<see cref="DrawStructureCard"/>), so the others keep their selection and their place;
        /// a new width is set on what is there.
        /// </summary>
        private void DrawMightyStructure(RunSession pane)
        {
            if (structureScroll is null) return;
            structureDeferred = false;
            var retained = (IReadOnlyList<MightyGraphRun>)(pane.GraphRuns ?? []);
            var older = GraphHistoryRuns(pane, retained);
            IReadOnlyList<MightyGraphRun> runs = [.. older, .. retained];
            var (style, phase, pending) = StructureInputs(runs, styleStartingNew);
            var signature = new MightyStructure.Signature(runs, style, phase, styleStartingNew, pane.PlanHistory, pending);
            if (signature != structureSignature)
            {
                structureShape = MightyStructure.Make(runs, style, phase, styleStartingNew, pane.PlanHistory, pending, structureItems.Items(runs, signature));
                structureSignature = signature;
            }
            var columns = structureShape.Columns; var free = columns is null; var language = Locale.LanguagePreference;
            // The pane's width, read from outside the page, so the columns' width never feeds back into what is measured.
            var viewport = structureScroll.ActualWidth > 0 ? structureScroll.ActualWidth : graphHost?.ActualWidth ?? 0;
            var width = MightyStructure.ColumnWidth(viewport - 2 * DesignMetrics.Inset.GraphBlockBodyH, columns?.Count ?? 3, DesignMetrics.Spacing.Lg);
            var frameKey = string.Join('\n', columns is null
                ? ["free", runs.Count == 0 ? "empty" : "", language]
                : new[] { "phases", language }.Concat(columns.SelectMany(c => new[] { c.Id, c.Phase.Title, MightyStructure.Word(c.Step) })));
            var changed = frameKey != structureFrameKey;
            if (changed)
            {
                // A new frame takes every card out of its column and back: not while a press is down on the page.
                if (structurePressed is not null && structureFrameKey is not null && !structureForced) { structureDeferred = true; return; }
                structureFrameKey = frameKey;
                foreach (var panel in structureColumns.Values) panel.Children.Clear();
                structureColumns.Clear(); structureRail = null;
                structurePage.Child = columns is null ? BuildStructureRows(runs.Count == 0) : BuildStructurePhases(columns, width);
            }
            else if (width != structureWidth)
            {
                if (structureRail is not null) SizeStructureRail(structureRail, width);
                foreach (var panel in structureColumns.Values) if (!free) panel.Width = width;
                changed = true;
            }
            structureWidth = width;
            var catalog = owner.Runtime(pane.Provider)?.ModelCatalog?.Models;
            var drawn = new HashSet<string>();
            void Fill(string column, IEnumerable<MightyStructure.Item> items)
            {
                var desired = new List<UIElement>();
                foreach (var item in items)
                {
                    // A run id the pane repeats (a damaged save) is drawn once.
                    if (!drawn.Add(item.RunId)) continue;
                    var key = new StructureCardKey(item, free, width, MightyStructure.Fold.IsFolded(item, structureUnfolded), StructureOpenKeys(item), GraphRequestTitle(runs[item.RunIndex], item.Ordinal), language, catalog);
                    if (!structureCards.TryGetValue(item.RunId, out var card)) structureCards[item.RunId] = card = new();
                    if ((card.Key is not { } built || built != key) && DrawStructureCard(card, key)) changed = true;
                    desired.Add(card.View);
                }
                var panel = structureColumns[column];
                for (var index = panel.Children.Count - 1; index >= 0; index--) if (!desired.Contains(panel.Children[index])) { panel.Children.RemoveAt(index); changed = true; }
                for (var index = 0; index < desired.Count; index++)
                {
                    if (index < panel.Children.Count && ReferenceEquals(panel.Children[index], desired[index])) continue;
                    // A request that moved to another phase takes its card with it.
                    if (desired[index] is FrameworkElement { Parent: Panel parent }) parent.Children.Remove(desired[index]);
                    panel.Children.Insert(index, desired[index]); changed = true;
                }
            }
            if (columns is null) Fill("", (structureShape.Rows ?? []).Select(row => row.Item));
            else foreach (var column in columns) Fill(column.Id, column.Items);
            foreach (var gone in structureCards.Keys.Where(run => !drawn.Contains(run)).ToArray()) { structureCards.Remove(gone); structureAnswers.Remove(gone); }
            // What the user opened follows the cards: only what is still drawn is kept (as the timeline's, MainWindow.MightyTimeline.cs:276).
            structureUnfolded.IntersectWith(drawn);
            if (structureExpanded.Count > 0) structureExpanded.IntersectWith(structureShape.Items.SelectMany(StructureTextKeys));
            RethemeStructureAnswers();
            // The total counts the retained requests; loaded ones are named apart.
            graphTotal.Text = MightyGraphBlockModel.ToolbarSummary(retained) + (older.Count > 0 ? " · " + Locale.Get("graph.history.headerLoaded", new Dictionary<string, string> { ["count"] = older.Count.ToString() }) : "");
            var help = MightyGraphBlockModel.ToolbarHelp(retained);
            ToolTipService.SetToolTip(graphTotal, help); AutomationProperties.SetHelpText(graphTotal, help);

            var newest = runs.Count > 0 ? runs[^1].Id : null; var arrived = newest != structureNewestId;
            structureNewestId = newest;
            // Shown again, the page opens at its end as the Mac's does (M/MightyStructureView.swift:729-732): a reader who had stopped
            // following is judged there, and follows again where the newest card ends the page.
            if (structureAppearing) { structureAppearing = false; changed = true; ScrollStructure(structureFollow.Appeared(newest) ?? MightyStructureFollow.Scroll.Bottom); }
            // A new request is the user's own: shown, and followed from then on.
            else if (arrived && newest is not null) { changed = true; ScrollStructure(structureFollow.NewRequest(newest)); }
            // A card that grew in a shorter column moves the newest card's bottom without resizing the page, and a page shown
            // again has the size it had: where it stands is reported once it is laid out.
            if (changed) Container.DispatcherQueue.TryEnqueue(() => { if (QueuePaneAlive && structureScroll is { Visibility: Visibility.Visible } scroll) { scroll.UpdateLayout(); ObserveStructurePage(false); } });
            AutoLoadGraphHistory(pane, retained);
        }

        /// <summary>The keys a request's texts are opened under: what was asked, the latest message, the answer and each sub-agent's card.</summary>
        private static IEnumerable<string> StructureTextKeys(MightyStructure.Item item) =>
            new[] { "request:" + item.RunId, "message:" + item.RunId, "answer:" + item.RunId }.Concat(item.Clusters.SelectMany(cluster => cluster.Nodes).Select(node => "node:" + node.NodeId));

        /// <summary>The texts of a request opened past their folded lines, as one key.</summary>
        private string StructureOpenKeys(MightyStructure.Item item) => structureExpanded.Count == 0 ? "" : string.Join('\n', StructureTextKeys(item).Where(structureExpanded.Contains));

        // ── follow ────────────────────────────────────────────────────────────

        /// <summary>
        /// The page is moving under the user's hand (M/MightyStructureView.swift:25-29): a drag or the moment after one, a scroll
        /// in steps, the scroll bar held, or a wheel notch or key whose scroll the page has yet to report.
        /// </summary>
        private bool StructureUserMoving
        {
            get
            {
                var now = Environment.TickCount64; const double grace = MightyStructurePan.ReleaseGrace * 1000;
                return structurePanning || structureScrolling || structureBarHeld || now - structurePanReleasedAt < grace || now - structureInputAt < grace;
            }
        }

        /// <summary>
        /// Reports where the page stands, in its own top-down coordinates (M/MightyStructureView.swift:110-118): the page's frame,
        /// the pane's height and the newest card's bottom in one report, so the follow rule never judges one against a stale other.
        /// </summary>
        private void ObserveStructurePage(bool userScrolling)
        {
            if (structureScroll is not { Visibility: Visibility.Visible, ViewportHeight: > 0 } scroll || !structurePage.IsLoaded || !QueuePaneAlive) return;
            double? bottom = null;
            if (structureNewestId is { } newest && structureCards.TryGetValue(newest, out var card) && card.View is { IsLoaded: true, ActualHeight: > 0 } view)
                bottom = view.TransformToVisual(structurePage).TransformPoint(new Point(0, view.ActualHeight)).Y;
            var origin = -structurePage.TransformToVisual(scroll).TransformPoint(new Point()).Y;
            ScrollStructure(structureFollow.Observe(new(origin, scroll.ViewportHeight, structurePage.ActualHeight, structureNewestId, bottom), userScrolling || StructureUserMoving));
        }

        /// <summary>
        /// The page's end, or the newest card's bottom at the viewport's bottom (in a styled pane its column in the middle),
        /// after the current layout pass: a report arrives while the page is being laid out (M/MightyStructureView.swift:143-156).
        /// </summary>
        private void ScrollStructure(MightyStructureFollow.Scroll? target)
        {
            if (target is not { } to) return;
            Container.DispatcherQueue.TryEnqueue(() =>
            {
                // A drag or a scroll of the user's is theirs: nothing scrolls under it, and a view change would stop the wheel's own.
                if (structurePanning || structureScrolling || !QueuePaneAlive || structureScroll is not { Visibility: Visibility.Visible } scroll) return;
                scroll.UpdateLayout();
                if (to.Card is { } run && structureCards.TryGetValue(run, out var card) && card.View is { IsLoaded: true, ActualHeight: > 0 } view)
                {
                    var corner = view.TransformToVisual(structurePage).TransformPoint(new Point(0, view.ActualHeight));
                    double? x = structureShape.Columns is null ? null : Math.Clamp(corner.X + view.ActualWidth / 2 - scroll.ViewportWidth / 2, 0, scroll.ScrollableWidth);
                    scroll.ChangeView(x, Math.Clamp(corner.Y - scroll.ViewportHeight, 0, scroll.ScrollableHeight), null, true);
                }
                else scroll.ChangeView(null, scroll.ScrollableHeight, null, true);
            });
        }

        /// <summary>
        /// A wheel notch or a scrolling key is the user's scroll before the page reports it, and with Windows' animations off it
        /// moves the page in one change, never in steps: noted when the page can still move that way (-1, 0 or 1 on each axis).
        /// </summary>
        private void NoteStructureInput(int across, int down)
        {
            if (structureScroll is not { } scroll) return;
            if (down < 0 && scroll.VerticalOffset > 0.5 || down > 0 && scroll.VerticalOffset < scroll.ScrollableHeight - 0.5
                || across < 0 && scroll.HorizontalOffset > 0.5 || across > 0 && scroll.HorizontalOffset < scroll.ScrollableWidth - 0.5) structureInputAt = Environment.TickCount64;
        }

        private void OnStructureWheel(object sender, PointerRoutedEventArgs args)
        {
            if (structureScroll is not { } scroll || args.KeyModifiers.HasFlag(VirtualKeyModifiers.Control)) return;
            var wheel = args.GetCurrentPoint(scroll).Properties; var step = Math.Sign(wheel.MouseWheelDelta);
            // A tilted wheel moves the page across; so does a shifted one, or any wheel on a page that cannot move down.
            if (wheel.IsHorizontalMouseWheel) NoteStructureInput(step, 0);
            else if (args.KeyModifiers.HasFlag(VirtualKeyModifiers.Shift) || scroll.ScrollableHeight <= 0.5) NoteStructureInput(-step, 0);
            else NoteStructureInput(0, -step);
        }

        private void OnStructureKeyDown(object sender, KeyRoutedEventArgs args)
        {
            // The answer keeps its arrow keys for its caret.
            if (StructureHit<RichEditBox>(args.OriginalSource) is not null) return;
            switch (args.Key)
            {
                case VirtualKey.Up or VirtualKey.PageUp or VirtualKey.Home: NoteStructureInput(0, -1); break;
                case VirtualKey.Down or VirtualKey.PageDown or VirtualKey.End: NoteStructureInput(0, 1); break;
                case VirtualKey.Left: NoteStructureInput(-1, 0); break;
                case VirtualKey.Right: NoteStructureInput(1, 0); break;
            }
        }

        // ── panning ───────────────────────────────────────────────────────────

        /// <summary>The nearest <typeparamref name="T"/> that <paramref name="source"/> is in, inside the page.</summary>
        private T? StructureHit<T>(object? source) where T : DependencyObject
        {
            if (source is not UIElement) return null;
            for (var element = (DependencyObject)source; element is not null && !ReferenceEquals(element, structureScroll); element = VisualTreeHelper.GetParent(element))
                if (element is T hit) return hit;
            return null;
        }

        private static bool StructureInside(DependencyObject? element, DependencyObject ancestor)
        {
            for (; element is not null; element = VisualTreeHelper.GetParent(element)) if (ReferenceEquals(element, ancestor)) return true;
            return false;
        }

        /// <summary>
        /// What a press lands on (M/MightyStructurePanning.swift:124-143): a text where its words are laid out, a text box or a
        /// scroll bar takes its own drag; anything else inside the page, a button and the empty part of a text's frame too, is the
        /// page until the press turns out to be a click.
        /// </summary>
        private MightyStructurePan.Target StructurePanTarget(PointerRoutedEventArgs args)
        {
            var source = args.OriginalSource;
            if (source is not UIElement) return source is null ? MightyStructurePan.Target.Outside : MightyStructurePan.Target.TextOrControl;
            // The answer's own scroll view is part of its text box.
            if (StructureHit<RichEditBox>(source) is { } answer)
                return StructureOnText(answer, args.GetCurrentPoint(answer).Position) ? MightyStructurePan.Target.TextOrControl : MightyStructurePan.Target.Page;
            for (var element = (DependencyObject)source; element is not null; element = VisualTreeHelper.GetParent(element))
            {
                if (ReferenceEquals(element, structureScroll)) return MightyStructurePan.Target.Page;
                if (element is TextBlock { IsTextSelectionEnabled: true } words)
                    return StructureOnText(words, args.GetCurrentPoint(words).Position) ? MightyStructurePan.Target.TextOrControl : MightyStructurePan.Target.Page;
                if (element is TextBox or ScrollBar) return MightyStructurePan.Target.TextOrControl;
                if (element is ScrollViewer) return MightyStructurePan.Target.InnerScroll;
            }
            return MightyStructurePan.Target.Outside;
        }

        /// <summary>The point is on the words a text block laid out: its measured text, not the frame it was given (M/MightyStructurePanning.swift:148-157).</summary>
        private static bool StructureOnText(TextBlock words, Point point) =>
            point.X <= words.DesiredSize.Width + StructureTextSlop && point.Y <= words.DesiredSize.Height + StructureTextSlop;

        /// <summary>
        /// The point is on the answer's laid-out text (M/MightyStructurePanning.swift:159-170): the character nearest to it, a
        /// character's width to either side, in the document's own points inside the text box's padding.
        /// </summary>
        private static bool StructureOnText(RichEditBox answer, Point point)
        {
            try
            {
                var at = new Point(point.X - answer.Padding.Left, point.Y - answer.Padding.Top);
                var range = answer.Document.GetRangeFromPoint(at, PointOptions.ClientCoordinates);
                range.Expand(TextRangeUnit.Character);
                range.GetRect(PointOptions.None, out var rect, out _);
                return at.X >= rect.X - rect.Width - StructureTextSlop && at.X <= rect.X + 2 * rect.Width + StructureTextSlop && at.Y >= rect.Y - StructureTextSlop && at.Y <= rect.Y + rect.Height + StructureTextSlop;
            }
            catch (COMException) { return true; }
        }

        private void OnStructurePointerPressed(object sender, PointerRoutedEventArgs args)
        {
            structurePress = null; structurePanned = false; structurePressed = null; structureBarHeld = false;
            if (structureScroll is not { } scroll) return;
            var point = args.GetCurrentPoint(scroll);
            if (!point.Properties.IsLeftButtonPressed) return;
            structurePressed = args.OriginalSource as UIElement;
            structureBarHeld = StructureHit<ScrollBar>(args.OriginalSource) is not null;
            if (args.Pointer.PointerDeviceType != PointerDeviceType.Mouse) return;
            var movable = MightyStructurePan.Scrollable((0, 0), (scroll.ScrollableWidth, scroll.ScrollableHeight));
            if (MightyStructurePan.MayPan(StructurePanTarget(args), args.KeyModifiers != VirtualKeyModifiers.None, movable))
                structurePress = (point.Position, scroll.HorizontalOffset, scroll.VerticalOffset);
        }

        private void OnStructurePointerMoved(object sender, PointerRoutedEventArgs args)
        {
            if (structurePressed is null && structurePress is null || structureScroll is not { } scroll) return;
            var point = args.GetCurrentPoint(scroll);
            // The button came up where the page could not see it.
            if (!point.Properties.IsLeftButtonPressed) { if (structurePanning) scroll.ReleasePointerCapture(args.Pointer); EndStructurePan(); EndStructurePress(); return; }
            if (structurePress is not { } press) return;
            var start = (press.Point.X, press.Point.Y); var cursor = (point.Position.X, point.Position.Y);
            if (!structurePanning)
            {
                // A shorter press is a click, and goes on to what was pressed.
                if (!MightyStructurePan.IsDrag(start, cursor)) return;
                // A drag: the page takes the pointer, so what was pressed is not clicked.
                structurePanning = structurePanned = true;
                scroll.CapturePointer(args.Pointer);
            }
            var origin = MightyStructurePan.Origin((press.X, press.Y), start, cursor, (0, 0), (scroll.ScrollableWidth, scroll.ScrollableHeight));
            scroll.ChangeView(origin.X, origin.Y, null, true);
            args.Handled = true;
        }

        private void OnStructurePointerReleased(object sender, PointerRoutedEventArgs args)
        {
            if (structurePanning) { structureScroll?.ReleasePointerCapture(args.Pointer); args.Handled = true; }
            EndStructurePan(); EndStructurePress();
        }

        private void EndStructurePan()
        {
            structurePress = null;
            if (!structurePanning) return;
            structurePanning = false; structurePanReleasedAt = Environment.TickCount64;
            // A click this release raises on what was pressed is the drag's, not the user's; forgotten once the release is over.
            Container.DispatcherQueue.TryEnqueue(() => structurePanned = false);
        }

        /// <summary>The press is over: the scroll bar's last step may still be reported, and a part it kept as it was is built now.</summary>
        private void EndStructurePress()
        {
            if (structureBarHeld) structureInputAt = Environment.TickCount64;
            structurePressed = null; structureBarHeld = false;
            ResumeStructure();
        }

        /// <summary>A text's selection changed: once nothing holds a part any more, what it waited for is drawn.</summary>
        private void OnStructureSelectionChanged(object sender, RoutedEventArgs args) { if (structurePressed is null) ResumeStructure(); }

        private void ResumeStructure()
        {
            if (!structureDeferred) return;
            structureDeferred = false; QueueGraphRefresh();
        }

        // ── the frame ─────────────────────────────────────────────────────────

        /// <summary>
        /// A styled pane (M/MightyStructureView.swift:170-187): the rail of phase chips, and <c>Spacing.Md</c> under it one column
        /// per phase, <c>Spacing.Lg</c> apart, its cards <c>Spacing.Md</c> apart.
        /// </summary>
        private StackPanel BuildStructurePhases(IReadOnlyList<MightyStructure.Column> columns, double width)
        {
            var frame = new StackPanel { Spacing = DesignMetrics.Spacing.Md };
            var rail = structureRail = StructureRail(columns.Select(column => (column.Phase.Title, column.Step)).ToList(), width);
            AutomationProperties.SetName(rail, Locale.Get("guidedPanel.phasesAccessibility"));
            frame.Children.Add(rail);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DesignMetrics.Spacing.Lg };
            foreach (var column in columns)
            {
                var stack = structureColumns[column.Id] = new StackPanel { Spacing = DesignMetrics.Spacing.Md, Width = width, VerticalAlignment = VerticalAlignment.Top };
                AutomationProperties.SetAutomationId(stack, "mighty-structure-phase-" + column.Id);
                AutomationProperties.SetName(stack, StructureStepName(column.Phase.Title, column.Step));
                row.Children.Add(stack);
            }
            frame.Children.Add(row);
            return frame;
        }

        /// <summary>A free pane (M/MightyStructureView.swift:251-261): one row per request, <c>Spacing.Xl</c> apart; with no request yet, the timeline's own words for that.</summary>
        private StackPanel BuildStructureRows(bool empty)
        {
            var rows = structureColumns[""] = new StackPanel { Spacing = DesignMetrics.Spacing.Xl };
            if (!empty) return rows;
            var frame = new StackPanel();
            frame.Children.Add(new TextBlock { Text = Locale.Get("graph.timeline.empty"), FontSize = StructureTextSize, Foreground = owner.brushes.Brush(DesignToken.Ink2), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, DesignMetrics.Spacing.Md, 0, DesignMetrics.Spacing.Md) });
            frame.Children.Add(rows);
            return frame;
        }

        /// <summary>
        /// The chips over the columns (M/MightyStructureView.swift:189-208), each in its column's width and joined to the next by
        /// a 2pt line that is <c>done</c> once the step before it is done, <c>track</c> until then.
        /// </summary>
        private StackPanel StructureRail(IReadOnlyList<(string Title, MightyStructure.Step Step)> steps, double width)
        {
            var b = owner.brushes;
            var rail = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DesignMetrics.Spacing.Lg };
            for (var index = 0; index < steps.Count; index++)
            {
                var cell = new Grid { Width = width };
                cell.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); cell.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
                cell.Children.Add(StructureChip(steps[index].Title, steps[index].Step, width));
                if (index < steps.Count - 1)
                {
                    // The line runs on through the gap to the next chip.
                    var line = new Border
                    {
                        Height = DesignMetrics.Stroke.Active, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, -DesignMetrics.Spacing.Lg, 0), IsHitTestVisible = false,
                        Background = b.Brush(steps[index].Step == MightyStructure.Step.Done ? DesignToken.Done : DesignToken.Track),
                    };
                    AutomationProperties.SetAccessibilityView(line, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
                    Grid.SetColumn(line, 1); cell.Children.Add(line);
                }
                rail.Children.Add(cell);
            }
            return rail;
        }

        /// <summary>A rail at a new column width: each cell, and the chip cut to it.</summary>
        private static void SizeStructureRail(StackPanel rail, double width)
        {
            foreach (var cell in rail.Children.OfType<Grid>())
            {
                cell.Width = width;
                if (cell.Children[0] is Border chip) chip.MaxWidth = width;
            }
        }

        /// <summary>
        /// One step as a chip (M/MightyStructureView.swift:210-236): a capsule 20 tall, padding <c>Spacing.Md</c>, its 8pt bold mark
        /// and its 11pt title (bold while current) <c>Spacing.Xs</c> apart. Done is the <c>done</c> fill, current <c>accent</c>,
        /// pending <c>track</c> with <c>ink2</c>; an error or a stop in its own fill. A long title is cut to its column; the whole
        /// title is in the tooltip.
        /// </summary>
        private Border StructureChip(string title, MightyStructure.Step step, double width)
        {
            var b = owner.brushes;
            var (fill, ink, symbol) = step switch
            {
                MightyStructure.Step.Done => (b.Fill(DesignTone.Done), b.FillInk(DesignTone.Done), "checkmark"),
                MightyStructure.Step.Current => (b.Brush(DesignToken.Accent), b.Brush(DesignToken.OnAccent), "circle.fill"),
                MightyStructure.Step.Error => (b.Fill(DesignTone.Err), b.FillInk(DesignTone.Err), "xmark"),
                MightyStructure.Step.Stopped => (b.Fill(DesignTone.Stop), b.FillInk(DesignTone.Stop), "stop.fill"),
                _ => (b.Brush(DesignToken.Track), b.Brush(DesignToken.Ink2), "circle"),
            };
            var face = new Grid { ColumnSpacing = DesignMetrics.Spacing.Xs, VerticalAlignment = VerticalAlignment.Center };
            face.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); face.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            face.Children.Add(MightySymbols.Create(symbol, 8, ink, MightySymbols.Weight.Bold));
            var words = StructureWords(title, DesignMetrics.Type.Pill, ink, step == MightyStructure.Step.Current ? FontWeights.Bold : FontWeights.SemiBold);
            Grid.SetColumn(words, 1); face.Children.Add(words);
            var chip = new Border
            {
                Child = face, Height = StructureChipHeight, MaxWidth = width, CornerRadius = new CornerRadius(StructureChipHeight / 2), Background = fill,
                Padding = new Thickness(DesignMetrics.Spacing.Md, 0, DesignMetrics.Spacing.Md, 0), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
            };
            ToolTipService.SetToolTip(chip, title); AutomationProperties.SetName(chip, StructureStepName(title, step));
            return chip;
        }

        private static string StructureStepName(string name, MightyStructure.Step step) => Locale.Get("graph.structure.stepAccessibility", new Dictionary<string, string>
        {
            ["name"] = name,
            ["state"] = Locale.Get(step switch
            {
                MightyStructure.Step.Done => "graph.structure.step.done", MightyStructure.Step.Current => "graph.structure.step.current",
                MightyStructure.Step.Pending => "graph.structure.step.pending", MightyStructure.Step.Error => "graph.state.error", _ => "graph.state.stopped",
            }),
        });

        // ── cards ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Brings a request's card up to <paramref name="key"/> (M/MightyStructureView.swift:263-379): folded to one compact card
        /// once it finished (<see cref="MightyStructure.Fold"/>), else open, in a free pane as wide as its three columns. Only a
        /// part whose content changed is built again, in its old place; a part with a press down in it or a selection in one of
        /// its texts waits (<see cref="StructureHeld"/>), and the whole card waits when its frame changes. A new width is set on
        /// what is there, and builds only a part whose folded lines it changes. Returns whether anything was built.
        /// </summary>
        private bool DrawStructureCard(StructureCard card, StructureCardKey key)
        {
            var item = key.Item; var free = key.Free; var width = key.Width; var heading = key.Heading; var language = key.Language;
            var frame = key.Folded ? "folded" : free ? "row" : item.Finished ? "finished" : "open";
            var reframed = frame != card.Frame;
            if (reframed && card.Parts.Values.Any(part => StructureHeld(part.View))) { structureDeferred = true; return false; }
            var live = new HashSet<string>(); var waits = false; var built = reframed;
            FrameworkElement Part(string name, object content, Func<FrameworkElement> build)
            {
                live.Add(name);
                if (card.Parts.TryGetValue(name, out var part))
                {
                    if (Equals(part.Key, content)) return part.View;
                    if (StructureHeld(part.View)) { waits = true; return part.View; }
                }
                var view = build();
                if (part.View?.Parent is Panel place) { Grid.SetColumn(view, Grid.GetColumn(part.View)); place.Children[place.Children.IndexOf(part.View)] = view; }
                card.Parts[name] = (content, view); built = true;
                return view;
            }
            card.View.Width = free ? 3 * width + 2 * DesignMetrics.Spacing.Lg : double.NaN;
            card.View.HorizontalAlignment = free ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
            if (key.Folded)
            {
                var answer = MightyStructure.Fold.ResultLine(item);
                var folded = Part("folded", (heading, item.Status, item.Tally, item.Input, answer, language), () => StructureFoldedCard(item, heading, answer));
                if (reframed) { card.View.Children.Clear(); Adopt(card.View, folded); }
            }
            else
            {
                var textWidth = width - 2 * DesignMetrics.Inset.GraphBlockBodyH;
                var row = free ? MightyStructure.RowFor(item) : null;
                var open = (Request: structureExpanded.Contains("request:" + item.RunId), Message: structureExpanded.Contains("message:" + item.RunId), Answer: structureExpanded.Contains("answer:" + item.RunId));
                var requestFolds = item.Request?.Folds(MightyStructure.RequestLines, MightyStructure.CharactersPerLine(free ? textWidth - DesignMetrics.Layout.HitTarget - DesignMetrics.Spacing.Xs : textWidth, StructureTextSize)) == true;
                var messageFolds = item.Message?.Folds(MightyStructure.MessageLines, MightyStructure.CharactersPerLine(textWidth, StructureTextSize)) == true;
                var request = Part("request", (heading, item.Finished, item.Request, item.Input, item.NodeId, open.Request, requestFolds, row?.Request, language),
                    () => StructureRequest(item, heading, requestFolds, row?.Request));
                var execution = Part("execution", (item.Ordinal, item.Status, item.Tally, item.Message, string.Join('\n', item.Activity), item.Finished, open.Message, messageFolds, row?.Execution, item.ExecutionNodeId, item.NodeId, language),
                    () => StructureExecution(item, messageFolds, row?.Execution));
                // Until the request finished there is no result block: a free row's button shows the run instead.
                var reveal = item.Finished ? MightyGraphBlockSize.NodeId(item.RunId, "result") : item.ExecutionNodeId;
                var result = free || item.Finished ? Part("result", (item.Status, item.Finished, item.Answer, open.Answer, reveal, item.NodeId, row?.Result, language), () => StructureResult(item, reveal, row?.Result)) : null;
                var head = free ? Part("head", (heading, item.Finished), () => StructureRowHead(item, heading)) : null;
                var rail = row is not null ? Part("rail", (row.Request, row.Execution, row.Result, language), () => StructureRail(StructureRowSteps(row), width)) : null;
                // Folds are judged at the width a sub-agent card's text really has at its depth.
                var nodes = item.Clusters.Select(cluster => cluster.Nodes.Select(node =>
                {
                    var nodeOpen = structureExpanded.Contains("node:" + node.NodeId); var nodeWidth = MightyStructure.NodeTextWidth(width, node.Depth);
                    var folds = node.Prompt?.Folds(MightyStructure.PromptLines, MightyStructure.CharactersPerLine(nodeWidth, StructurePromptSize)) == true
                        || node.Output?.Folds(MightyStructure.OutputLines, MightyStructure.CharactersPerLine(nodeWidth, StructureOutputSize)) == true || node.Activity.Count > 1;
                    var model = node.Model is { } id ? ModelUsageFormat.ShortName(id, key.Catalog, versioned: true) : null;
                    return Part("node:" + node.NodeId, (node, nodeOpen, folds, model, language), () => StructureNodeCard(node, nodeOpen, folds, model));
                }).ToList()).ToList();
                if (reframed) { card.Clusters = new StackPanel { Spacing = DesignMetrics.Spacing.Xs, Margin = new Thickness(DesignMetrics.Spacing.Lg, 0, 0, 0) }; card.ClustersKey = ""; }
                var clusters = card.Clusters!;
                var shape = string.Join('|', item.Clusters.Select(cluster => string.Join(',', cluster.Nodes.Select(node => node.NodeId))).Append(language));
                // A new tree or a new node lays the trees out again, moving their cards: not while one of them holds a press or a selection.
                if (shape != card.ClustersKey && !card.Parts.Where(part => part.Key.StartsWith("node:", StringComparison.Ordinal) && live.Contains(part.Key)).Any(part => StructureHeld(part.Value.View)))
                {
                    clusters.Children.Clear();
                    for (var index = 0; index < item.Clusters.Count; index++) clusters.Children.Add(StructureClusterBox(item.Clusters[index].Nodes.Count, nodes[index]));
                    card.ClustersKey = shape; built = true;
                }
                else if (shape != card.ClustersKey) waits = true;
                if (reframed)
                {
                    card.View.Children.Clear();
                    if (free)
                    {
                        var view = new StackPanel { Spacing = DesignMetrics.Spacing.Sm };
                        Adopt(view, head!); Adopt(view, rail!);
                        var cells = card.Cells = new Grid { ColumnSpacing = DesignMetrics.Spacing.Lg };
                        for (var index = 0; index < 3; index++) cells.ColumnDefinitions.Add(new() { Width = new GridLength(width) });
                        Adopt(cells, request);
                        var run = new StackPanel { Spacing = DesignMetrics.Spacing.Sm, VerticalAlignment = VerticalAlignment.Top };
                        Adopt(run, execution); if (item.Clusters.Count > 0) run.Children.Add(clusters);
                        Grid.SetColumn(run, 1); cells.Children.Add(run);
                        Grid.SetColumn(result!, 2); Adopt(cells, result!);
                        view.Children.Add(cells);
                        AutomationProperties.SetAutomationId(view, "mighty-structure-request-" + item.RunId);
                        card.View.Children.Add(view);
                    }
                    else
                    {
                        var stack = new StackPanel { Spacing = DesignMetrics.Spacing.Sm };
                        Adopt(stack, request); Adopt(stack, execution);
                        if (item.Clusters.Count > 0) stack.Children.Add(clusters);
                        if (result is not null) Adopt(stack, result);
                        card.View.Children.Add(stack);
                    }
                }
                // The trees' place comes after the run part once the request has a tree.
                else if ((item.Clusters.Count > 0) != (clusters.Parent is not null) && execution.Parent is Panel host)
                {
                    if (clusters.Parent is Panel holder) holder.Children.Remove(clusters);
                    else host.Children.Insert(host.Children.IndexOf(execution) + 1, clusters);
                }
                if (free && card.Cells is { } grid) { foreach (var column in grid.ColumnDefinitions) column.Width = new GridLength(width); SizeStructureRail((StackPanel)rail!, width); }
            }
            foreach (var gone in card.Parts.Keys.Where(name => !live.Contains(name)).ToArray()) card.Parts.Remove(gone);
            if (!live.Contains("result")) structureAnswers.Remove(item.RunId);
            card.Frame = frame;
            // A part that waited keeps the card's key open, so the next draw comes back for it.
            card.Key = waits ? null : key;
            if (waits) structureDeferred = true;
            return built;
        }

        /// <summary>Moves a part that is kept into its card's new frame.</summary>
        private static void Adopt(Panel panel, FrameworkElement part)
        {
            if (part.Parent is Panel old) old.Children.Remove(part);
            panel.Children.Add(part);
        }

        /// <summary>
        /// A press is down in the part, or one of its texts holds a selection: built again now, the drag would lose its text and
        /// the click its button, so the part waits until that ends (as the diagram and the timeline wait for a selection,
        /// MainWindow.MightyGraph.cs:411, MainWindow.MightyTimeline.cs:216). A draw a card's own button asked for waits for nothing.
        /// </summary>
        private bool StructureHeld(FrameworkElement part) => !structureForced && (StructureInside(structurePressed, part)
            || VisualChildren(part).Any(child => child is TextBlock { SelectedText.Length: > 0 } || child is RichEditBox { FocusState: not FocusState.Unfocused } answer && answer.Document.Selection.Length != 0));

        /// <summary>
        /// What was asked. Under a phase (M/MightyStructureView.swift:345-360) its card leads with "Request N · Claude" (12 bold),
        /// the fold button once the request finished and the reveal button; in a free row (M/MightyStructureView.swift:290-302)
        /// its column's card holds the text beside the reveal button, the row's heading standing over the rail.
        /// </summary>
        private Border StructureRequest(MightyStructure.Item item, string heading, bool folds, MightyStructure.Step? step)
        {
            var name = Locale.Get("graph.structure.column.request");
            if (step is { } stands)
            {
                var cell = new Grid { ColumnSpacing = DesignMetrics.Spacing.Xs };
                cell.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); cell.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
                if (item.Request is { } asked) cell.Children.Add(StructureText(asked, "request:" + item.RunId, MightyStructure.RequestLines, StructureTextSize, folds, name));
                var reveal = StructureRevealButton(item.NodeId, item.NodeId); reveal.VerticalAlignment = VerticalAlignment.Top;
                Grid.SetColumn(reveal, 1); cell.Children.Add(reveal);
                var surface = StructureSurface(cell); surface.VerticalAlignment = VerticalAlignment.Top;
                AutomationProperties.SetName(surface, StructureStepName(name, stands));
                return surface;
            }
            var request = new StackPanel { Spacing = DesignMetrics.Spacing.Sm };
            var title = StructureWords(heading, StructureTextSize, owner.brushes.Brush(DesignToken.Ink), FontWeights.Bold);
            request.Children.Add(item.Finished ? StructureHeader(title, StructureFoldButton(item), StructureRevealButton(item.NodeId, item.NodeId)) : StructureHeader(title, StructureRevealButton(item.NodeId, item.NodeId)));
            if (item.Request is { } text) request.Children.Add(StructureText(text, "request:" + item.RunId, MightyStructure.RequestLines, StructureTextSize, folds, name));
            var card = StructureSurface(request);
            AutomationProperties.SetAutomationId(card, "mighty-structure-request-" + item.RunId);
            AutomationProperties.SetName(card, Locale.Get("graph.structure.contentAccessibility", new Dictionary<string, string> { ["name"] = heading, ["text"] = item.Input }));
            return card;
        }

        /// <summary>
        /// A free row's heading (M/MightyStructureView.swift:281-286): 13 bold in the heading font, its fold button right after
        /// it once the request finished. A long heading is cut so the button stays on the line.
        /// </summary>
        private PaneHeaderLine StructureRowHead(MightyStructure.Item item, string heading)
        {
            var title = StructureWords(heading, DesignMetrics.Type.Title, owner.brushes.Brush(DesignToken.Ink), FontWeights.Bold); title.FontFamily = new FontFamily(DesignMetrics.Font.Heading);
            AutomationProperties.SetHeadingLevel(title, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level2);
            var head = new PaneHeaderLine { Spacing = DesignMetrics.Spacing.Sm };
            head.Add(title, PaneHeaderLine.Role.Shrink);
            if (item.Finished) head.Add(StructureFoldButton(item), PaneHeaderLine.Role.Fixed);
            return head;
        }

        /// <summary>A free row's three steps as its rail names them (M/MightyStructureView.swift:275-279).</summary>
        private static List<(string Title, MightyStructure.Step Step)> StructureRowSteps(MightyStructure.Row row) =>
        [
            (Locale.Get("graph.structure.column.request"), row.Request), (Locale.Get("graph.structure.column.execution"), row.Execution), (Locale.Get("graph.structure.column.result"), row.Result),
        ];

        /// <summary>
        /// The run part (M/MightyStructureView.swift:381-425): the main agent's status pill, its tally (11 semibold <c>ink2</c>),
        /// its latest reply and its last tool calls, as the diagram's run block holds them. With no <paramref name="step"/> it
        /// names itself (the gears and "Run · Request N", 11 bold); a free row's rail already does, and its card stands at the top of its cell.
        /// </summary>
        private Border StructureExecution(MightyStructure.Item item, bool folds, MightyStructure.Step? step)
        {
            var b = owner.brushes; var ink2 = b.Brush(DesignToken.Ink2);
            var name = Locale.Get("graph.block.executionOf", new Dictionary<string, string> { ["n"] = item.Ordinal.ToString(CultureInfo.InvariantCulture) });
            var lead = new Grid { ColumnSpacing = DesignMetrics.Spacing.Sm, VerticalAlignment = VerticalAlignment.Center };
            void Lead(FrameworkElement part, GridLength size)
            {
                Grid.SetColumn(part, lead.ColumnDefinitions.Count); lead.ColumnDefinitions.Add(new() { Width = size }); lead.Children.Add(part);
            }
            if (step is null)
            {
                // The Mac's gearshape.2 (M/MightyGraphView.swift:577).
                var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DesignMetrics.Spacing.Xs, VerticalAlignment = VerticalAlignment.Center };
                label.Children.Add(MightySymbols.Create("gearshape.2", StructureOutputSize, b.Brush(DesignToken.Ink))); label.Children.Add(StructureWords(name, StructureOutputSize, b.Brush(DesignToken.Ink), FontWeights.Bold));
                Lead(label, GridLength.Auto);
            }
            Lead(StatusPill(MightyGraphBlockModel.StateLabel(item.Status), StatusGlyph.Tone(item.Status), 18), GridLength.Auto);
            Lead(StructureTally(item), new GridLength(1, GridUnitType.Star));
            var body = new StackPanel { Spacing = DesignMetrics.Spacing.Sm };
            body.Children.Add(StructureHeader(lead, StructureRevealButton(item.ExecutionNodeId, item.NodeId)));
            if (item.Message is { } message) body.Children.Add(StructureText(message, "message:" + item.RunId, MightyStructure.MessageLines, StructureTextSize, folds, Locale.Get("graph.structure.latest")));
            else if (!item.Finished && item.Activity.Count == 0) body.Children.Add(new TextBlock { Text = Locale.Get("graph.block.waitingResponse"), FontSize = StructureTextSize, Foreground = ink2, TextWrapping = TextWrapping.Wrap });
            if (item.Activity.Count > 0)
            {
                var calls = new StackPanel { Spacing = DesignMetrics.Spacing.Xxs };
                foreach (var line in item.Activity) calls.Children.Add(StructureActivityRow(line));
                AutomationProperties.SetName(calls, Locale.Get("graph.structure.activity"));
                body.Children.Add(calls);
            }
            var card = StructureSurface(body);
            if (step is not null) card.VerticalAlignment = VerticalAlignment.Top;
            AutomationProperties.SetAutomationId(card, "mighty-structure-execution-" + item.RunId);
            AutomationProperties.SetName(card, step is { } stands ? StructureStepName(name, stands) : name);
            return card;
        }

        private TextBlock StructureTally(MightyStructure.Item item) => StructureWords(Locale.Get("graph.structure.tally", new Dictionary<string, string>
        {
            ["agents"] = item.Tally.Agents.ToString(CultureInfo.InvariantCulture), ["tools"] = item.Tally.Tools.ToString(CultureInfo.InvariantCulture),
        }), StructureOutputSize, owner.brushes.Brush(DesignToken.Ink2), FontWeights.SemiBold);

        /// <summary>
        /// The result card (M/MightyStructureView.swift:427-448): its header words and symbol (11 bold) in the outcome's ink once
        /// there is one, then the answer as the transcript renders a reply; without one, how the request ended (its last reply is
        /// in the run part already). In a free row it is named by its <paramref name="step"/> and stands at the top of its cell.
        /// </summary>
        private Border StructureResult(MightyStructure.Item item, string revealNodeId, MightyStructure.Step? step)
        {
            structureAnswers.Remove(item.RunId);
            var b = owner.brushes; var ink = item.Finished ? b.Text(StatusGlyph.Tone(item.Status)) : b.Brush(DesignToken.Ink2);
            var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DesignMetrics.Spacing.Xs, VerticalAlignment = VerticalAlignment.Center };
            label.Children.Add(MightySymbols.Create(item.Status switch { "error" => "exclamationmark.triangle", "stopped" => "stop.circle", _ => "checkmark.seal" }, StructureOutputSize, ink, MightySymbols.Weight.Bold));
            label.Children.Add(StructureWords(MightyGraphBlockModel.ResultTitle(item.Status), StructureOutputSize, ink, FontWeights.Bold));
            var body = new StackPanel { Spacing = DesignMetrics.Spacing.Sm };
            body.Children.Add(StructureHeader(label, StructureRevealButton(revealNodeId, item.NodeId)));
            if (item.Answer is { } answer) body.Children.Add(StructureAnswer(item, answer));
            else body.Children.Add(new TextBlock { Text = item.Finished ? MightyGraphBlockModel.StateLabel(item.Status) : Locale.Get("graph.structure.resultPending"), FontSize = StructureTextSize, Foreground = b.Brush(DesignToken.Ink2), TextWrapping = TextWrapping.Wrap });
            var card = StructureSurface(body);
            AutomationProperties.SetAutomationId(card, "mighty-structure-result-" + item.RunId);
            if (step is { } stands)
            {
                card.VerticalAlignment = VerticalAlignment.Top;
                AutomationProperties.SetName(card, StructureStepName(Locale.Get("graph.structure.column.result"), stands));
            }
            return card;
        }

        /// <summary>
        /// The answer as Markdown (M/MightyStructureView.swift:628-640). Folded, its preview: whole lines with any open code
        /// fence closed, so nothing is cut mid-line. The page scrolls, never the answer inside it.
        /// </summary>
        private FrameworkElement StructureAnswer(MightyStructure.Item item, MightyStructure.Excerpt answer)
        {
            var key = "answer:" + item.RunId; var open = structureExpanded.Contains(key); var source = open ? answer.Text : answer.Preview; var light = !owner.DarkTheme;
            var view = MarkdownView(TranscriptRtf.RenderMarkdown(source, light), 0);
            view.MinHeight = 0; view.Resources["TextControlBorderThemeThicknessFocused"] = new Thickness(0);
            ScrollViewer.SetVerticalScrollBarVisibility(view, ScrollBarVisibility.Disabled); ScrollViewer.SetVerticalScrollMode(view, ScrollMode.Disabled);
            AutomationProperties.SetAutomationId(view, "mighty-structure-answer-" + item.RunId); AutomationProperties.SetName(view, Locale.Get("graph.block.result"));
            view.SelectionChanged += OnStructureSelectionChanged; view.LostFocus += OnStructureSelectionChanged;
            structureAnswers[item.RunId] = (view, source, light);
            if (!answer.Cut) return view;
            var stack = new StackPanel { Spacing = DesignMetrics.Spacing.Xxs };
            stack.Children.Add(view); stack.Children.Add(StructureMoreButton(key, open));
            return stack;
        }

        /// <summary>The app's own theme toggle: every answer on the page in the new colours, as the plan documents are (<see cref="RethemePlanViews"/>).</summary>
        internal void RethemeStructureAnswers()
        {
            var light = !owner.DarkTheme;
            foreach (var (run, answer) in structureAnswers.Where(pair => pair.Value.Light != light).ToArray())
            {
                SetMarkdownRtf(answer.View, TranscriptRtf.RenderMarkdown(answer.Source, light));
                structureAnswers[run] = answer with { Light = light };
            }
        }

        /// <summary>
        /// One sub-agent tree (M/MightyStructureView.swift:450-472): its cards <c>Spacing.Xs</c> apart in a dashed <c>line</c> box of
        /// radius 8, padding <c>Spacing.Sm</c>. The trees stand <c>Spacing.Lg</c> in from the request's cards, <c>Spacing.Xs</c> apart;
        /// <see cref="MightyStructure.NodeTextWidth"/> takes these paddings off a node's width.
        /// </summary>
        private Grid StructureClusterBox(int count, IReadOnlyList<FrameworkElement> cards)
        {
            var nodes = new StackPanel { Spacing = DesignMetrics.Spacing.Xs, Padding = new Thickness(DesignMetrics.Spacing.Sm) };
            foreach (var node in cards) Adopt(nodes, node);
            var outline = new Rectangle
            {
                RadiusX = StructureNodeCorner, RadiusY = StructureNodeCorner, Stroke = owner.brushes.Brush(DesignToken.Line), StrokeThickness = DesignMetrics.Stroke.Line,
                StrokeDashArray = [4, 3], IsHitTestVisible = false,
            };
            AutomationProperties.SetAccessibilityView(outline, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
            var box = new Grid(); box.Children.Add(nodes); box.Children.Add(outline);
            AutomationProperties.SetName(box, Locale.Get("graph.structure.clusterAccessibility", new Dictionary<string, string> { ["count"] = count.ToString(CultureInfo.InvariantCulture) }));
            return box;
        }

        /// <summary>
        /// A sub-agent or task as a small card (M/MightyStructureView.swift:474-533): radius 8 on <c>card</c> with a <c>line</c>
        /// edge and its status as a 3pt bar in its mark colour; its kind's symbol (9) in its ink, its title (11 semibold, cut last)
        /// and "kind · model" (10 <c>ink2</c>) on one line; then what it was asked (10.5 <c>ink2</c>, two lines), its latest output
        /// (11, six lines) and its last tool call. Opened, it shows them whole and its last few tool calls. Each level of the tree
        /// stands <c>Spacing.Lg</c> further in, up to <see cref="MightyStructure.MaximumIndent"/>.
        /// </summary>
        private Border StructureNodeCard(MightyStructure.Node node, bool open, bool folds, string? model)
        {
            var b = owner.brushes; var task = node.Kind == "task"; var key = "node:" + node.NodeId;
            var head = new PaneHeaderLine { Spacing = DesignMetrics.Spacing.Sm };
            head.Add(MightySymbols.Create(BlockSymbol(node.Kind), 9, b.Brush(task ? DesignToken.TaskText : DesignToken.AgentText), MightySymbols.Weight.Semibold), PaneHeaderLine.Role.Fixed);
            head.Add(StructureWords(node.Title, StructureOutputSize, b.Brush(DesignToken.Ink), FontWeights.SemiBold), PaneHeaderLine.Role.Shrink);
            head.Add(StructureWords(MightyTimeline.KindLabel(node.Kind) + (model is null ? "" : " · " + model), DesignMetrics.Type.Small, b.Brush(DesignToken.Ink2)), PaneHeaderLine.Role.Fill);
            head.Add(StructureRevealButton(node.NodeId, node.NodeId), PaneHeaderLine.Role.Fixed);
            var body = new StackPanel
            {
                Spacing = DesignMetrics.Spacing.Xxs,
                Margin = new Thickness(DesignMetrics.Spacing.Md - BlockEdge, DesignMetrics.Spacing.Xs - BlockEdge, DesignMetrics.Spacing.Xs - BlockEdge, DesignMetrics.Spacing.Xs - BlockEdge),
            };
            body.Children.Add(head);
            if (node.Prompt is { } prompt)
            {
                var words = StructureSelectable(open ? prompt.Text : prompt.Preview, StructurePromptSize, DesignToken.Ink2, open ? 0 : MightyStructure.PromptLines);
                AutomationProperties.SetName(words, Locale.Get("graph.structure.prompt")); body.Children.Add(words);
            }
            if (node.Output is { } output)
            {
                var words = StructureSelectable(open ? output.Text : output.Preview, StructureOutputSize, DesignToken.Ink, open ? 0 : MightyStructure.OutputLines);
                AutomationProperties.SetName(words, Locale.Get("graph.structure.output")); body.Children.Add(words);
            }
            foreach (var line in open ? node.Activity : node.Activity.TakeLast(1)) body.Children.Add(StructureActivityRow(line));
            if (folds) body.Children.Add(StructureMoreButton(key, open));
            // Inside the card's edge, its left corners following the card's own.
            var bar = new Border { Width = StructureStatusBar, HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(StructureNodeCorner - BlockEdge, 0, 0, StructureNodeCorner - BlockEdge), Background = b.Brush(StructureMark(StatusGlyph.Tone(node.Status))), IsHitTestVisible = false };
            AutomationProperties.SetAccessibilityView(bar, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
            var face = new Grid(); face.Children.Add(bar); face.Children.Add(body);
            var card = new Border
            {
                Child = face, CornerRadius = new CornerRadius(StructureNodeCorner), Background = b.Brush(DesignToken.Card), BorderThickness = new Thickness(BlockEdge), BorderBrush = b.Brush(DesignToken.Line),
                Margin = new Thickness(Math.Min(node.Depth, MightyStructure.MaximumIndent) * DesignMetrics.Spacing.Lg, 0, 0, 0),
            };
            AutomationProperties.SetAutomationId(card, "mighty-structure-node-" + node.NodeId);
            AutomationProperties.SetName(card, Locale.Get("graph.structure.nodeAccessibility", new Dictionary<string, string> { ["title"] = node.Title, ["status"] = MightyGraphBlockModel.StateLabel(node.Status) }));
            return card;
        }

        /// <summary>The colour of a status as a mark (M/Palette.swift:94-103): 3:1 on page, card and sidebar.</summary>
        private static DesignToken StructureMark(DesignTone tone) => tone switch
        {
            DesignTone.Run => DesignToken.Run, DesignTone.Wait => DesignToken.WaitText, DesignTone.Done => DesignToken.Done,
            DesignTone.Err => DesignToken.Err, DesignTone.Stop => DesignToken.Stop, _ => DesignToken.Ink3,
        };

        /// <summary>
        /// One tool call (M/MightyStructureView.swift:535-549): its kind's glyph (10, in a 14 column; <c>errText</c> when it
        /// failed, <c>accent</c> while it runs, else <c>ink2</c>) and its words on one line, 11 <c>ink2</c>, mono for a command, a
        /// read or an edit. The whole line is in the tooltip.
        /// </summary>
        private Grid StructureActivityRow(MightyStructure.ActivityLine line)
        {
            var b = owner.brushes;
            var row = new Grid { ColumnSpacing = DesignMetrics.Spacing.Sm };
            row.ColumnDefinitions.Add(new() { Width = new GridLength(StructureGlyphWidth) }); row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            // M/AgentActivityView.swift:118-129.
            var symbol = line.Kind switch
            {
                "command" => "terminal", "read" => "doc.text.magnifyingglass", "edit" => "pencil.line", "search" => "magnifyingglass",
                "web" => "globe", "agent" => "person.2", "turn" => "sparkles", _ => "wrench.and.screwdriver",
            };
            var mark = MightySymbols.Create(symbol, 10, b.Brush(line.State == "error" ? DesignToken.ErrText : line.State == "running" ? DesignToken.Accent : DesignToken.Ink2));
            mark.HorizontalAlignment = HorizontalAlignment.Center; row.Children.Add(mark);
            var words = StructureWords(line.Text, StructureOutputSize, b.Brush(DesignToken.Ink2));
            words.IsTextSelectionEnabled = true; words.HorizontalAlignment = HorizontalAlignment.Left; words.SelectionChanged += OnStructureSelectionChanged;
            if (line.Kind is "command" or "read" or "edit") words.FontFamily = new FontFamily(DesignMetrics.Font.Mono);
            Grid.SetColumn(words, 1); row.Children.Add(words);
            ToolTipService.SetToolTip(row, line.Text);
            return row;
        }

        /// <summary>
        /// A finished request folded to one compact card (M/MightyStructureView.swift:553-587): its heading (12 bold, cut last),
        /// status pill and tally (cut first) over a chevron at the right; what was asked on one line (12); and how it ended on one
        /// line (11): the answer's first line in <c>ink2</c>, else the outcome in its ink. The whole card opens it; a drag on it
        /// moves the page.
        /// </summary>
        private Button StructureFoldedCard(MightyStructure.Item item, string heading, string? answer)
        {
            var b = owner.brushes; var state = MightyGraphBlockModel.StateLabel(item.Status);
            var lead = new PaneHeaderLine { Spacing = DesignMetrics.Spacing.Sm };
            lead.Add(StructureWords(heading, StructureTextSize, b.Brush(DesignToken.Ink), FontWeights.Bold), PaneHeaderLine.Role.Shrink);
            lead.Add(StatusPill(state, StatusGlyph.Tone(item.Status), 18), PaneHeaderLine.Role.Fixed);
            lead.Add(StructureTally(item), PaneHeaderLine.Role.Fill);
            lead.Add(MightySymbols.Create("chevron.down", 10, b.Brush(DesignToken.Ink2)), PaneHeaderLine.Role.Fixed);
            var lines = new StackPanel { Spacing = DesignMetrics.Spacing.Xxs };
            lines.Children.Add(lead);
            lines.Children.Add(StructureWords(item.Input, StructureTextSize, b.Brush(DesignToken.Ink)));
            lines.Children.Add(StructureWords(answer ?? state, StructureOutputSize, answer is null ? b.Text(StatusGlyph.Tone(item.Status)) : b.Brush(DesignToken.Ink2)));
            var button = new Button
            {
                Content = StructureSurface(lines), MinWidth = 0, MinHeight = 0, Padding = new Thickness(0), BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(StructureCardCorner),
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            };
            owner.PaintPlainButton(button, b.Transparent, b.Transparent);
            var hint = Locale.Get("graph.structure.unfoldCard");
            AutomationProperties.SetAutomationId(button, "mighty-structure-folded-" + item.RunId);
            AutomationProperties.SetName(button, Locale.Get("graph.structure.nodeAccessibility", new Dictionary<string, string> { ["title"] = heading, ["status"] = state }));
            AutomationProperties.SetItemStatus(button, Locale.Get("graph.structure.collapsed")); AutomationProperties.SetHelpText(button, hint);
            ToolTipService.SetToolTip(button, hint);
            button.Click += (_, _) => { if (!structurePanned) ToggleStructureFold(item); };
            return button;
        }

        // ── parts ─────────────────────────────────────────────────────────────

        /// <summary>
        /// A card of the structure (M/MightyStructureView.swift:676-680): the diagram's block card (<c>card</c>, a 1pt
        /// <c>line</c> edge) at radius 10, its content <c>Inset.GraphBlockBodyH</c> by <c>Spacing.Sm</c> in from the card's edge.
        /// </summary>
        private Border StructureSurface(UIElement content) => new()
        {
            Child = content, Background = owner.brushes.Brush(DesignToken.Card), CornerRadius = new CornerRadius(StructureCardCorner),
            BorderThickness = new Thickness(BlockEdge), BorderBrush = owner.brushes.Brush(DesignToken.Line),
            Padding = new Thickness(DesignMetrics.Inset.GraphBlockBodyH - BlockEdge, DesignMetrics.Spacing.Sm - BlockEdge, DesignMetrics.Inset.GraphBlockBodyH - BlockEdge, DesignMetrics.Spacing.Sm - BlockEdge),
        };

        /// <summary>A card's header line: what names it, then its small buttons at the right, <c>Spacing.Sm</c> apart.</summary>
        private static Grid StructureHeader(FrameworkElement lead, params Button[] buttons)
        {
            var header = new Grid { ColumnSpacing = DesignMetrics.Spacing.Sm };
            header.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            header.Children.Add(lead);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DesignMetrics.Spacing.Sm, VerticalAlignment = VerticalAlignment.Center };
            foreach (var button in buttons) row.Children.Add(button);
            Grid.SetColumn(row, 1); header.Children.Add(row);
            return header;
        }

        /// <summary>One line of words, cut with an ellipsis where its place ends.</summary>
        private static TextBlock StructureWords(string text, double size, Brush ink, FontWeight? weight = null) => new()
        {
            Text = text, FontSize = size, Foreground = ink, FontWeight = weight ?? FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center,
        };

        /// <summary>
        /// A text shown in place (M/MightyStructureView.swift:682-690): wrapped to its column and selectable, at most
        /// <paramref name="lines"/> lines (0: all of it). Its frame ends with its words, so the card beside them still moves the page.
        /// </summary>
        private TextBlock StructureSelectable(string text, double size, DesignToken ink, int lines)
        {
            var words = new TextBlock
            {
                Text = text, FontSize = size, Foreground = owner.brushes.Brush(ink), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, HorizontalAlignment = HorizontalAlignment.Left,
                MaxLines = lines, TextTrimming = lines > 0 ? TextTrimming.CharacterEllipsis : TextTrimming.None,
            };
            words.SelectionChanged += OnStructureSelectionChanged;
            return words;
        }

        /// <summary>
        /// Plain text (M/MightyStructureView.swift:614-626): its first lines folded, the whole of it (up to its cap) opened. The
        /// toggle shows whenever the folded lines may cut it at its width (<paramref name="folds"/>).
        /// </summary>
        private FrameworkElement StructureText(MightyStructure.Excerpt excerpt, string key, int lines, double size, bool folds, string name)
        {
            var open = structureExpanded.Contains(key);
            var words = StructureSelectable(open ? excerpt.Text : excerpt.Preview, size, DesignToken.Ink, open ? 0 : lines);
            AutomationProperties.SetName(words, name);
            if (!folds) return words;
            var stack = new StackPanel { Spacing = DesignMetrics.Spacing.Xxs };
            stack.Children.Add(words); stack.Children.Add(StructureMoreButton(key, open));
            return stack;
        }

        /// <summary>The toggle under a folded text (M/MightyStructureView.swift:642-654): "more" or "less", 11 bold <c>accent</c>.</summary>
        private Button StructureMoreButton(string key, bool open)
        {
            var label = Locale.Get(open ? "graph.structure.less" : "graph.structure.more");
            var toggle = HeaderButton(new TextBlock { Text = label, FontSize = StructureOutputSize, FontWeight = FontWeights.Bold }, owner.brushes.Brush(DesignToken.Accent));
            toggle.HorizontalAlignment = HorizontalAlignment.Left;
            AutomationProperties.SetAutomationId(toggle, "mighty-structure-more-" + key); AutomationProperties.SetName(toggle, label);
            AutomationProperties.SetItemStatus(toggle, Locale.Get(open ? "graph.structure.expanded" : "graph.structure.collapsed"));
            toggle.Click += (_, _) => { if (!structurePanned) ToggleStructureText(key); };
            return toggle;
        }

        /// <summary>
        /// A card's small header button (M/MightyStructureView.swift:589-601, 656-667): plain, <c>Layout.HitTarget</c> square, its
        /// 11pt symbol in <c>ink2</c>. A press that became a drag of the page is not its click.
        /// </summary>
        private Button StructureButton(string symbol, string name, string automationId, Action act) =>
            ZoomButton(MightySymbols.Create(symbol, 11, null), name, automationId, () => { if (!structurePanned) act(); });

        /// <summary>The header control of an open finished request that folds it back.</summary>
        private Button StructureFoldButton(MightyStructure.Item item)
        {
            var button = StructureButton("chevron.up", Locale.Get("graph.structure.foldCard"), "mighty-structure-fold-" + item.RunId, () => ToggleStructureFold(item));
            AutomationProperties.SetItemStatus(button, Locale.Get("graph.structure.expanded"));
            return button;
        }

        /// <summary>The small header button that shows the block in the diagram.</summary>
        private Button StructureRevealButton(string nodeId, string requestNodeId) =>
            StructureButton("arrow.up.right.square", Locale.Get("graph.structure.reveal"), "mighty-structure-reveal-" + nodeId, () => RevealStructureBlock(nodeId, requestNodeId));

        // An opened or folded text or request is being read: nothing follows until the reader comes back to the newest card.
        // A request that folds as it finishes is not the user's: the follow rule judges the page it leaves (M/MightyStructureView.swift:125-131).
        private void ToggleStructureText(string key)
        {
            if (!structureExpanded.Add(key)) structureExpanded.Remove(key);
            structureFollow.Stop(); RedrawStructure("mighty-structure-more-" + key);
        }

        private void ToggleStructureFold(MightyStructure.Item item)
        {
            structureUnfolded = MightyStructure.Fold.Toggled(item, structureUnfolded);
            structureFollow.Stop(); RedrawStructure((structureUnfolded.Contains(item.RunId) ? "mighty-structure-fold-" : "mighty-structure-folded-") + item.RunId);
        }

        /// <summary>
        /// Draws the page again for one of its own buttons, which its click ends the press of. That button is built again with
        /// its part, and focus on a control that leaves the tree moves on to the next one, which the page would bring into view
        /// (<see cref="ScrollViewer.BringIntoViewOnFocusChange"/>): focus waits on the page instead, and a keyboard's focus goes
        /// to the button built in the old one's place (<paramref name="automationId"/>).
        /// </summary>
        private void RedrawStructure(string automationId)
        {
            structurePressed = null;
            var held = structureScroll?.XamlRoot is { } root ? FocusManager.GetFocusedElement(root) as Control : null;
            var state = held is not null && StructureInside(held, structurePage) ? held.FocusState : FocusState.Unfocused;
            if (state != FocusState.Unfocused) structureScroll!.Focus(FocusState.Programmatic);
            structureForced = true;
            try { RefreshMightyView(Session); } finally { structureForced = false; }
            if (state is FocusState.Unfocused or FocusState.Pointer) return;
            Container.DispatcherQueue.TryEnqueue(() =>
            {
                if (!QueuePaneAlive || structureScroll is not { Visibility: Visibility.Visible } scroll) return;
                scroll.UpdateLayout();
                VisualChildren(structurePage).OfType<Button>().FirstOrDefault(button => AutomationProperties.GetAutomationId(button) == automationId)?.Focus(state);
            });
        }

        /// <summary>
        /// Shows a block in the diagram, its camera on it, as opening a timeline row aims it (M/MightyGraphView.swift:244-249). A
        /// block the diagram does not draw is shown as its request. The run-id rules are applied first, so a send this view has
        /// not been drawn for yet is followed before the reveal, which wins.
        /// </summary>
        private void RevealStructureBlock(string nodeId, string requestNodeId)
        {
            var pane = Session; var retained = (IReadOnlyList<MightyGraphRun>)(pane.GraphRuns ?? []);
            ObserveGraphRuns(pane);
            // The layout the diagram really draws: each request over its 실행 block, with what hangs beside the flow.
            var drawn = GraphCanvasLayout(pane, GraphHistoryRuns(pane, retained), retained, GraphViewportSize, OuroborosExecutionLinks.Extract(retained)).Nodes.Any(node => node.Id == nodeId);
            var target = drawn ? nodeId : requestNodeId;
            graphSelection = target; PublishGraphAim(MightyGraphCamera.Anchor.Reaim(target, false));
            _ = SetGraphPresentation("diagram");
        }

        // ── the phone ─────────────────────────────────────────────────────────

        /// <summary>
        /// The structure view this pane draws, for a phone that asked for it (M/MightyClaude/AppStore+MobileRemote.swift:441-459):
        /// the same runs, style, phase and plans, without the pane's own "start anew" choice. The key names every input beyond
        /// the graph: the language untitled blocks are named in, the styles the request titles are read from and the model
        /// catalogue the node labels are read from.
        /// </summary>
        internal MobileStructureCache.Built MobileStructureSnapshot()
        {
            var pane = Session; var runs = (IReadOnlyList<MightyGraphRun>)(pane.GraphRuns ?? []);
            var (style, phase, pending) = StructureInputs(runs, false);
            var styles = activeStyle is null || styleRegistry is null ? null : styleRegistry.Styles;
            var catalog = owner.Runtime(pane.Provider)?.ModelCatalog?.Models;
            var key = string.Join('\n', new[] { Locale.LanguagePreference, activeStyle?.Id, activeStyle?.Hash }.Concat((styles ?? []).Where(s => s.Runnable).SelectMany(s => new[] { s.Id, s.Hash }))
                .Concat((catalog ?? []).SelectMany(model => new[] { model.Value, model.DisplayName, model.ResolvedModel })));
            return owner.mobileStructures.Structure(pane.Id, runs, style, phase, pane.PlanHistory, pending, key,
                styles is null ? null : input => StylePresentation.RequestPrefix(styles, input), model => ModelUsageFormat.ShortName(model, catalog, versioned: true));
        }
    }
}
