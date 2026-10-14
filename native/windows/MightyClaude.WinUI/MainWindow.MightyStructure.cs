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
using Board = MightyClaude.Core.MightyStructure.Board;
using PointOptions = Microsoft.UI.Text.PointOptions;
using TextRangeUnit = Microsoft.UI.Text.TextRangeUnit;
using VirtualKey = Windows.System.VirtualKey;
using VirtualKeyModifiers = Windows.System.VirtualKeyModifiers;

namespace MightyClaude.WinUI;

public sealed partial class MainWindow
{
    private sealed partial class PaneView
    {
        /// <summary>The view (M/MightyStructureView.swift:101-119): the navigation bar, the board's header and its rule over the page.</summary>
        private Grid? structureView;
        private ScrollViewer? structureScroll;
        /// <summary>The page inside the scroll view: a board shorter than the pane sits at its top left (M/MightyStructureView.swift:871-894).</summary>
        private readonly Border structurePage = new()
        {
            Padding = new Thickness(DesignMetrics.Inset.GraphBlockBodyH, DesignMetrics.Spacing.Md, DesignMetrics.Inset.GraphBlockBodyH, DesignMetrics.Spacing.Md),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
        };
        /// <summary>The boards and what they were built from, rebuilt only when something they draw changed (M/MightyGraphView.swift:325-339).</summary>
        private MightyStructure.Signature? structureSignature;
        private IReadOnlyList<Board> structureBoards = [];
        private readonly MightyStructure.ItemCache structureItems = new();
        /// <summary>
        /// What the view keeps of the pane while it shows its diagram or timeline (M/MightyStructureView.swift:8-48): the texts
        /// opened past their folded lines, by key; the board shown; whether the page follows the newest board's end; and the
        /// wheel steps that are the user's.
        /// </summary>
        private readonly HashSet<string> structureExpanded = [];
        private readonly MightyStructure.Navigation structureNavigation = new();
        private readonly MightyStructureFollow.PageWatch structureFollow = new();
        private readonly MightyStructureWheel.Tracker structureWheel = new();
        private bool structureShown, structureAppearing;
        /// <summary>The newest request, the board drawn, and that board's id while it is the newest (what the page follows).</summary>
        private string? structureNewestId, structureBoardId, structureFollowId;
        /// <summary>The navigation bar's controls, kept and changed in place so a press on one is never lost to a draw.</summary>
        private StackPanel? structureBar;
        private Button? structurePrevious, structureNext, structureListButton, structureBadge;
        private TextBlock? structurePosition, structureBadgeWords;
        private Flyout? structureListFlyout;
        private StackPanel? structureList;
        private string? structureBarLanguage;
        /// <summary>The board's header and its rule, the header's chips (cut to a new width in place) and what it was built from.</summary>
        private Border? structureHeader, structureRule;
        private readonly List<Border> structureChips = [];
        private string? structureHeaderKey;
        /// <summary>The frame (the board's columns), the column width it stands at, its columns and each card as last built.</summary>
        private string? structureFrameKey;
        private double structureWidth;
        private StackPanel? structureKanban;
        private readonly Dictionary<string, StructureColumn> structureColumns = [];
        private readonly Dictionary<string, (object Key, FrameworkElement View)> structureCards = [];
        private readonly Dictionary<string, (RichEditBox View, string Source, bool Light)> structureAnswers = [];
        /// <summary>A left press on the page that may become a drag (where it went down and where the page stood), the drag it became, and when the last one ended, in seconds.</summary>
        private (Point Point, double X, double Y)? structurePress;
        private bool structurePanning, structurePanned;
        private double structurePanReleasedAt;
        /// <summary>
        /// The user's own scroll (M/MightyStructureView.swift:22-41): a finger or the scroll bar moving the page in steps, a press
        /// held on the scroll bar, when the last scrolling key came or the bar was let go (the page may report it late), and the
        /// offset the follow rule last judged.
        /// </summary>
        private bool structureScrolling, structureBarHeld;
        private double structureInputAt, structureJudgedY;
        /// <summary>
        /// What a press went down on, until it comes up; whether a part was left as it was for a press or a selection, to be built
        /// once that ends; and a draw a card's own button asked for, which waits for nothing.
        /// </summary>
        private DependencyObject? structurePressed;
        private bool structureDeferred, structureForced;

        /// <summary>One column of the board: its frame, its header's place and what that header was built from, and its cards' panel.</summary>
        private sealed class StructureColumn(StackPanel view, Border header, StackPanel cards)
        {
            internal readonly StackPanel View = view, Cards = cards;
            internal readonly Border Header = header;
            internal object? HeaderKey;
        }

        /// <summary>A chip's height, a card's and a sub-agent card's corners, a sub-agent card's status bar and a tool call's glyph column (M/MightyStructureView.swift:83-92).</summary>
        private const double StructureChipHeight = 20, StructureCardCorner = DesignMetrics.Radius.Entry, StructureNodeCorner = DesignMetrics.Radius.Row, StructureStatusBar = DesignMetrics.Stroke.Rail, StructureGlyphWidth = 14;
        /// <summary>Type sizes: a card's texts, a sub-agent's output, its prompt (M/MightyStructureView.swift:93-96).</summary>
        private const double StructureTextSize = DesignMetrics.Type.Block, StructureOutputSize = DesignMetrics.Type.Pill, StructurePromptSize = 10.5;
        /// <summary>The list of boards (M/MightyStructureView.swift:97-99).</summary>
        private const double StructureListWidth = 340, StructureListHeight = 420;
        /// <summary>Points around a text's laid-out words that still press the text (M/MightyStructurePanning.swift:154-155).</summary>
        private const double StructureTextSlop = 4;

        // ── the view ──────────────────────────────────────────────────────────

        /// <summary>
        /// The structure view (M/MightyStructureView.swift:101-119, 357-440): ◀, the board's place with the list, ▶ and the badge
        /// of a newer board; the board's header and a rule; then the page, which scrolls both ways on <c>cardRaised</c>, its content
        /// <c>Inset.GraphBlockBodyH</c> by <c>Spacing.Md</c> in. Dragging its empty space moves it, and every report of where it
        /// stands decides whether it follows the newest board's end (<see cref="MightyStructureFollow.PageWatch"/>).
        /// </summary>
        private void BuildMightyStructure()
        {
            var b = owner.brushes;
            structureView = new Grid { Visibility = Visibility.Collapsed, Background = b.Brush(DesignToken.CardRaised) };
            for (var row = 0; row < 3; row++) structureView.RowDefinitions.Add(new() { Height = GridLength.Auto });
            structureView.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
            AutomationProperties.SetAutomationId(structureView, "mighty-structure-" + id);

            structurePrevious = StructureButton("chevron.left", "", "mighty-structure-previous-" + id, () => MoveStructureBoard(n => n.Step(-1, structureBoards), "mighty-structure-previous-" + id));
            structureNext = StructureButton("chevron.right", "", "mighty-structure-next-" + id, () => MoveStructureBoard(n => n.Step(1, structureBoards), "mighty-structure-next-" + id));
            var place = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DesignMetrics.Spacing.Xs, VerticalAlignment = VerticalAlignment.Center };
            place.Children.Add(structurePosition = StructureWords("", StructureTextSize, b.Brush(DesignToken.Ink), FontWeights.Bold));
            place.Children.Add(MightySymbols.Create("chevron.down", 9, b.Brush(DesignToken.Ink2), MightySymbols.Weight.Semibold));
            structureListButton = HeaderButton(place, b.Brush(DesignToken.Ink));
            AutomationProperties.SetAutomationId(structureListButton, "mighty-structure-list-" + id);
            structureList = new StackPanel { Spacing = DesignMetrics.Inset.ListGap, Width = StructureListWidth - 2 * PopoverPadding };
            structureListFlyout = new Flyout { Content = new ScrollViewer { Content = structureList, MaxHeight = StructureListHeight }, Placement = FlyoutPlacementMode.Bottom, FlyoutPresenterStyle = owner.CardFlyoutStyle };
            structureListFlyout.Opening += (_, _) => FillStructureList();
            structureListButton.Flyout = structureListFlyout;
            structureBadgeWords = new TextBlock { FontSize = DesignMetrics.Type.Small, FontWeight = FontWeights.Bold, TextTrimming = TextTrimming.CharacterEllipsis };
            structureBadge = new Button
            {
                Content = structureBadgeWords, MinWidth = 0, MinHeight = 0, Padding = new Thickness(DesignMetrics.Spacing.Sm, DesignMetrics.Spacing.Xxs, DesignMetrics.Spacing.Sm, DesignMetrics.Spacing.Xxs),
                CornerRadius = new CornerRadius(DesignMetrics.Layout.HitTarget / 2), BorderThickness = new Thickness(0), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed,
            };
            owner.PaintPlainButton(structureBadge, b.Brush(DesignToken.Accent), b.Brush(DesignToken.Accent), ink: b.Brush(DesignToken.OnAccent));
            AutomationProperties.SetAutomationId(structureBadge, "mighty-structure-newer-" + id);
            structureBadge.Click += (_, _) => MoveStructureBoard(n => n.ShowNewest(structureBoards), "mighty-structure-list-" + id);
            structureBar = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = DesignMetrics.Spacing.Sm, Visibility = Visibility.Collapsed,
                Padding = new Thickness(DesignMetrics.Inset.GraphBlockBodyH, DesignMetrics.Spacing.Sm, DesignMetrics.Inset.GraphBlockBodyH, 0),
            };
            foreach (var control in new UIElement[] { structurePrevious, structureListButton, structureNext, structureBadge }) structureBar.Children.Add(control);
            structureView.Children.Add(structureBar);

            structureHeader = new Border
            {
                Padding = new Thickness(DesignMetrics.Inset.GraphBlockBodyH, DesignMetrics.Spacing.Sm, DesignMetrics.Inset.GraphBlockBodyH, DesignMetrics.Spacing.Sm), Visibility = Visibility.Collapsed,
            };
            structureHeader.SizeChanged += (_, args) => { if (args.NewSize.Width != args.PreviousSize.Width) SizeStructureChips(args.NewSize.Width); };
            Grid.SetRow(structureHeader, 1); structureView.Children.Add(structureHeader);
            structureRule = new Border { Height = DesignMetrics.Stroke.Line, Background = b.Brush(DesignToken.Line), Visibility = Visibility.Collapsed };
            AutomationProperties.SetAccessibilityView(structureRule, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
            Grid.SetRow(structureRule, 2); structureView.Children.Add(structureRule);

            structureScroll = new ScrollViewer
            {
                Content = structurePage, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollMode = ScrollMode.Auto, HorizontalScrollMode = ScrollMode.Auto, IsTabStop = true, Background = b.Brush(DesignToken.CardRaised),
            };
            AutomationProperties.SetAutomationId(structureScroll, "mighty-structure-page-" + id);
            AutomationProperties.SetName(structureScroll, Locale.Get("graph.view.structure"));
            // A move of the user's is judged the moment the page knows where it goes, not when it reports the move (M/MightyStructurePanning.swift:309-314).
            structureScroll.ViewChanging += (_, args) =>
            {
                var to = args.FinalView.VerticalOffset;
                if (StructureUserMoving && Math.Abs(to - structureJudgedY) > 0.5) structureFollow.UserScrolled(to - structureJudgedY);
                structureJudgedY = to;
            };
            structureScroll.ViewChanged += (_, args) =>
            {
                // The page moves in steps only under the user's wheel, keys, finger or scroll bar: the view's own scrolls land at once.
                if (args.IsIntermediate) structureScrolling = true;
                var user = StructureUserMoving;
                if (!args.IsIntermediate) structureScrolling = false;
                ObserveStructurePage(user);
            };
            // The columns share the pane's width: a new width sizes them again. A new height (a taller composer) moves no content, yet may hide the board's end.
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
            Grid.SetRow(structureScroll, 3); structureView.Children.Add(structureScroll);
            Grid.SetRow(structureView, 1); graphHost!.Children.Add(structureView);
        }

        /// <summary>Shows or hides the view. Shown again, the page opens at its end and a following reader is taken to the newest board's end by the next draw.</summary>
        private void ShowMightyStructure(bool shown)
        {
            if (structureView is null) return;
            structureView.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
            if (shown == structureShown) return;
            structureShown = shown;
            if (shown) structureAppearing = true;
            else { EndStructurePan(); structurePressed = null; structureBarHeld = structureScrolling = false; structureListFlyout?.Hide(); }
        }

        /// <summary>The scroll view while the view shows.</summary>
        private ScrollViewer? ShownStructureScroll => structureView is { Visibility: Visibility.Visible } && structureScroll is { } scroll ? scroll : null;

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
        /// Draws the board shown (M/MightyStructureView.swift:101-119, 357-488): the navigation bar and the header changed in place,
        /// and one column per step with its cards. A card is built again only when what it shows changed
        /// (<see cref="StructureCardView"/>), so the others keep their selection and their place; a new width is set on what is there.
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
                structureBoards = MightyStructure.Boards(MightyStructure.Make(runs, style, phase, styleStartingNew, pane.PlanHistory, pending, structureItems.Items(runs, signature)));
                structureSignature = signature;
            }
            var boards = structureBoards; var index = structureNavigation.Index(boards); var board = index is { } at ? boards[at] : null;
            var language = Locale.LanguagePreference;
            // The pane's width, read from outside the page, so the columns' width never feeds back into what is measured.
            var viewport = structureScroll.ActualWidth > 0 ? structureScroll.ActualWidth : graphHost?.ActualWidth ?? 0;
            var width = MightyStructure.ColumnWidth(viewport - 2 * DesignMetrics.Inset.GraphBlockBodyH, board?.Columns.Count ?? 3, DesignMetrics.Spacing.Lg);
            var frameKey = string.Join('\n', board is null ? ["empty", language] : new[] { board.Id, language }.Concat(board.Columns.Select(c => c.Id)));
            var changed = frameKey != structureFrameKey;
            if (changed)
            {
                // A new frame takes every card out of its column: not while a press is down on the page or a card holds a selection.
                if (structureFrameKey is not null && !structureForced && (structurePressed is not null || structureCards.Values.Any(part => StructureHeld(part.View)))) { structureDeferred = true; return; }
                structureFrameKey = frameKey;
                foreach (var column in structureColumns.Values) column.Cards.Children.Clear();
                structureColumns.Clear(); structureKanban = null;
                structurePage.Child = board is null ? StructureEmpty() : BuildStructureKanban(board, width);
            }
            else if (width != structureWidth)
            {
                foreach (var column in structureColumns.Values) column.View.Width = width;
                changed = true;
            }
            structureWidth = width;
            // The newest board's end is what the page follows; an older board follows nothing.
            var followId = index is { } shown && shown == boards.Count - 1 ? board?.Id : null;
            structureFollowId = followId;
            var drawn = new HashSet<string>();
            if (board is not null)
            {
                var catalog = owner.Runtime(pane.Provider)?.ModelCatalog?.Models;
                foreach (var column in board.Columns)
                {
                    if (!structureColumns.TryGetValue(column.Id, out var place)) continue;
                    var headerKey = (StructureStepTitle(column.Step), column.Step.Step, column.Count, language);
                    if (!Equals(place.HeaderKey, headerKey))
                    {
                        place.Header.Child = StructureColumnHeader(column); place.HeaderKey = headerKey;
                        AutomationProperties.SetName(place.View, Locale.Get("graph.structure.board.columnAccessibility", new Dictionary<string, string>
                        {
                            ["name"] = StructureStepTitle(column.Step), ["state"] = StructureStepState(column.Step.Step), ["count"] = column.Count.ToString(CultureInfo.InvariantCulture),
                        }));
                    }
                    var desired = new List<UIElement>();
                    foreach (var card in column.Cards)
                    {
                        // A run id the pane repeats (a damaged save) is drawn once.
                        if (!drawn.Add(card.Id)) continue;
                        var (view, built) = StructureCardView(card, width, GraphRequestTitle(runs[card.Item.RunIndex], card.Item.Ordinal), language, catalog);
                        if (built) changed = true;
                        desired.Add(view);
                    }
                    var panel = place.Cards;
                    for (var slot = panel.Children.Count - 1; slot >= 0; slot--) if (!desired.Contains(panel.Children[slot])) { panel.Children.RemoveAt(slot); changed = true; }
                    for (var slot = 0; slot < desired.Count; slot++)
                    {
                        if (slot < panel.Children.Count && ReferenceEquals(panel.Children[slot], desired[slot])) continue;
                        // A request that moved to another phase takes its card with it.
                        if (desired[slot] is FrameworkElement { Parent: Panel parent }) parent.Children.Remove(desired[slot]);
                        panel.Children.Insert(slot, desired[slot]); changed = true;
                    }
                }
            }
            foreach (var gone in structureCards.Keys.Where(card => !drawn.Contains(card)).ToArray())
            {
                structureCards.Remove(gone);
                if (gone.StartsWith("result:", StringComparison.Ordinal)) structureAnswers.Remove(gone["result:".Length..]);
            }
            // What the user opened follows the requests: only what the pane still holds is kept (as the timeline's, MainWindow.MightyTimeline.cs:276).
            if (structureExpanded.Count > 0) structureExpanded.IntersectWith(boards.SelectMany(b => b.Items).SelectMany(StructureTextKeys));
            RethemeStructureAnswers();
            DrawStructureBar(boards, index, board);
            if (board is not null) DrawStructureHeader(board, boards.Count);
            // The total counts the retained requests; loaded ones are named apart.
            graphTotal.Text = MightyGraphBlockModel.ToolbarSummary(retained) + (older.Count > 0 ? " · " + Locale.Get("graph.history.headerLoaded", new Dictionary<string, string> { ["count"] = older.Count.ToString() }) : "");
            var help = MightyGraphBlockModel.ToolbarHelp(retained);
            ToolTipService.SetToolTip(graphTotal, help); AutomationProperties.SetHelpText(graphTotal, help);

            var newest = runs.Count > 0 ? runs[^1].Id : null; var arrived = newest != structureNewestId;
            structureNewestId = newest;
            var moved = board?.Id != structureBoardId;
            structureBoardId = board?.Id;
            // Shown again, the page opens at its end, as the Mac's does (M/MightyStructureView.swift:871-882): a reader who had
            // stopped following is judged there, and follows again where the newest board's end shows.
            if (structureAppearing) { structureAppearing = false; changed = true; ScrollStructure((followId is null ? null : structureFollow.Appeared(followId)) ?? MightyStructureFollow.Scroll.Bottom); }
            // Another board: the newest is followed, an older one opens at its top (M/MightyStructureView.swift:425-433).
            else if (moved)
            {
                changed = true;
                if (followId is not null) ScrollStructure(structureFollow.NewRequest(followId));
                else { structureFollow.Stop(); ScrollStructureTop(); }
            }
            // A new request on the newest board is the user's own: shown, and followed from then on.
            else if (arrived && newest is not null && followId is not null) { changed = true; ScrollStructure(structureFollow.NewRequest(followId)); }
            // A card that grew in a shorter column moves the board's end without resizing the page, and a page shown again has the
            // size it had: where it stands is reported once it is laid out.
            if (changed) Container.DispatcherQueue.TryEnqueue(() => { if (QueuePaneAlive && ShownStructureScroll is { } scroll) { scroll.UpdateLayout(); ObserveStructurePage(false); } });
            AutoLoadGraphHistory(pane, retained);
        }

        /// <summary>The keys a request's texts are opened under: what was asked, the latest message, the answer and each sub-agent's card.</summary>
        private static IEnumerable<string> StructureTextKeys(MightyStructure.Item item) =>
            new[] { "request:" + item.RunId, "message:" + item.RunId, "answer:" + item.RunId }.Concat(item.Clusters.SelectMany(cluster => cluster.Nodes).Select(node => "node:" + node.NodeId));

        // ── navigation ────────────────────────────────────────────────────────

        /// <summary>
        /// The navigation bar (M/MightyStructureView.swift:123-191): ◀ and ▶ off at the ends, "Request 3 / 7" (or "Pipeline 2 / 3")
        /// over the list, and the badge while a board newer than the one shown is unseen. Hidden with no board.
        /// </summary>
        private void DrawStructureBar(IReadOnlyList<Board> boards, int? index, Board? board)
        {
            var visibility = board is null ? Visibility.Collapsed : Visibility.Visible;
            foreach (var part in new FrameworkElement?[] { structureBar, structureHeader, structureRule }) if (part is not null) part.Visibility = visibility;
            if (board is null || index is not { } at || structurePrevious is null || structureNext is null || structureListButton is null || structureBadge is null) return;
            if (structureBarLanguage != Locale.LanguagePreference)
            {
                structureBarLanguage = Locale.LanguagePreference;
                foreach (var (control, key) in new[] { (structurePrevious, "graph.structure.board.previous"), (structureNext, "graph.structure.board.next"), (structureListButton, "graph.structure.board.list") })
                {
                    AutomationProperties.SetName(control, Locale.Get(key)); ToolTipService.SetToolTip(control, Locale.Get(key));
                }
                ToolTipService.SetToolTip(structureBadge, Locale.Get("graph.structure.board.showNewest")); AutomationProperties.SetHelpText(structureBadge, Locale.Get("graph.structure.board.showNewest"));
            }
            structurePrevious.IsEnabled = at > 0; structureNext.IsEnabled = at < boards.Count - 1;
            var position = StructurePosition(board, at, boards.Count);
            structurePosition!.Text = position; AutomationProperties.SetItemStatus(structureListButton, position);
            var unseen = structureNavigation.HasUnseen(boards);
            structureBadge.Visibility = unseen ? Visibility.Visible : Visibility.Collapsed;
            if (!unseen) return;
            var text = Locale.Get(board.Kind == MightyStructure.BoardKind.Request ? "graph.structure.board.newRequest" : "graph.structure.board.newPipeline");
            structureBadgeWords!.Text = text; AutomationProperties.SetName(structureBadge, text);
        }

        /// <summary>"Request 3 / 7" or "Pipeline 2 / 3" (M/MightyStructureView.swift:155-159).</summary>
        private static string StructurePosition(Board board, int index, int count) => Locale.Get(board.Kind == MightyStructure.BoardKind.Request ? "graph.structure.board.request" : "graph.structure.board.pipeline",
            new Dictionary<string, string> { ["n"] = (index + 1).ToString(CultureInfo.InvariantCulture), ["count"] = count.ToString(CultureInfo.InvariantCulture) });

        /// <summary>The board's words: its first request's first line, else the style's name, else its place (M/MightyStructureView.swift:240-244).</summary>
        private string StructureTitle(Board board, int count) =>
            board.Headline ?? (board.Kind == MightyStructure.BoardKind.Pipeline ? activeStyle?.Manifest.Name : null) ?? StructurePosition(board, board.Ordinal - 1, count);

        /// <summary>A navigation control moved the board: drawn now, its focus kept (<see cref="RedrawStructure"/>).</summary>
        private void MoveStructureBoard(Action<MightyStructure.Navigation> move, string automationId)
        {
            if (structurePanned) return;
            move(structureNavigation); RedrawStructure(automationId);
        }

        /// <summary>
        /// The list of boards (M/MightyStructureView.swift:193-238), built as it opens: every board, newest last, each its number,
        /// its status's mark, its title (bold for the one shown, on <c>track</c>) and its steps. A click shows it and closes the list.
        /// </summary>
        private void FillStructureList()
        {
            if (structureList is null) return;
            var b = owner.brushes; var boards = structureBoards; var shown = structureNavigation.Index(boards);
            structureList.Children.Clear();
            AutomationProperties.SetName(structureList, Locale.Get("graph.structure.board.list"));
            for (var index = 0; index < boards.Count; index++)
            {
                var board = boards[index]; var title = StructureTitle(board, boards.Count); var steps = StructureStepsSummary(board); var selected = index == shown;
                var face = new Grid { ColumnSpacing = DesignMetrics.Spacing.Sm };
                face.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); face.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); face.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
                var number = StructureWords((index + 1).ToString(CultureInfo.InvariantCulture), DesignMetrics.Type.Pill, b.Brush(DesignToken.Ink2), FontWeights.Bold); number.VerticalAlignment = VerticalAlignment.Top;
                face.Children.Add(number);
                var mark = MightySymbols.Create(StructureStatusSymbol(board.Status), DesignMetrics.Type.Pill, b.Brush(StructureMark(StatusGlyph.Tone(board.Status))), MightySymbols.Weight.Semibold, b.Brush(DesignToken.Card));
                mark.VerticalAlignment = VerticalAlignment.Top; Grid.SetColumn(mark, 1); face.Children.Add(mark);
                var words = new StackPanel { Spacing = DesignMetrics.Spacing.Xxs };
                words.Children.Add(StructureWords(title, StructureTextSize, b.Brush(DesignToken.Ink), selected ? FontWeights.Bold : FontWeights.Normal));
                words.Children.Add(StructureWords(steps, DesignMetrics.Type.Small, b.Brush(DesignToken.Ink2)));
                Grid.SetColumn(words, 2); face.Children.Add(words);
                var entry = new Button
                {
                    Content = face, MinWidth = 0, MinHeight = 0, BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(StructureNodeCorner),
                    Padding = new Thickness(DesignMetrics.Spacing.Sm, DesignMetrics.Spacing.Xs, DesignMetrics.Spacing.Sm, DesignMetrics.Spacing.Xs),
                    HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                };
                owner.PaintPlainButton(entry, selected ? b.Brush(DesignToken.Track) : b.Transparent, b.Subtle);
                AutomationProperties.SetAutomationId(entry, "mighty-structure-entry-" + board.Id);
                AutomationProperties.SetName(entry, StructurePosition(board, index, boards.Count) + ", " + title);
                AutomationProperties.SetItemStatus(entry, MightyGraphBlockModel.StateLabel(board.Status) + ", " + steps);
                var target = board.Id;
                entry.Click += (_, _) => { structureListFlyout?.Hide(); MoveStructureBoard(n => n.Select(target, structureBoards), "mighty-structure-list-" + id); };
                structureList.Children.Add(entry);
            }
            // The shown board is brought into view as the list opens.
            if (shown is { } at && at < structureList.Children.Count && structureList.Children[at] is FrameworkElement current)
                Container.DispatcherQueue.TryEnqueue(() => current.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0.5, AnimationDesired = false }));
        }

        /// <summary>"Request ✓ → Run ● → Result ○": each step and where it stands (M/MightyStructureView.swift:246-259).</summary>
        private static string StructureStepsSummary(Board board) => string.Join(" → ", board.Steps.Select(step => StructureStepTitle(step) + " " + step.Step switch
        {
            MightyStructure.Step.Done => "✓", MightyStructure.Step.Current => "●", MightyStructure.Step.Pending => "○", MightyStructure.Step.Error => "✕", _ => "■",
        }));

        /// <summary>A status as the list marks it (M/MightyStructureView.swift:261-269); Windows draws the waiting pause as an outline, and a running board's dotted circle as its ringed glyph.</summary>
        private static string StructureStatusSymbol(string status) => status switch
        {
            "completed" => "checkmark.circle.fill", "error" => "exclamationmark.triangle.fill", "stopped" => "stop.circle.fill", "waiting" => "pause.circle", _ => "circle.dotted.circle",
        };

        // ── the header ────────────────────────────────────────────────────────

        /// <summary>
        /// The board's header (M/MightyStructureView.swift:280-304): its status pill and its title (13 bold in the heading font),
        /// then its steps as chips joined by 2pt lines, <c>done</c> once the step before them is done, <c>track</c> until then.
        /// </summary>
        private void DrawStructureHeader(Board board, int count)
        {
            if (structureHeader is null) return;
            var title = StructureTitle(board, count);
            var key = string.Join('\n', new[] { board.Id, board.Status, title, Locale.LanguagePreference }.Concat(board.Steps.Select(step => step.Id + "\u001F" + step.Title + "\u001F" + MightyStructure.Word(step.Step))));
            if (key == structureHeaderKey) return;
            structureHeaderKey = key;
            var b = owner.brushes;
            var lead = new PaneHeaderLine { Spacing = DesignMetrics.Spacing.Sm };
            lead.Add(StatusPill(MightyGraphBlockModel.StateLabel(board.Status), StatusGlyph.Tone(board.Status), 18), PaneHeaderLine.Role.Fixed);
            var heading = StructureWords(title, DesignMetrics.Type.Title, b.Brush(DesignToken.Ink), FontWeights.Bold); heading.FontFamily = new FontFamily(DesignMetrics.Font.Heading);
            AutomationProperties.SetHeadingLevel(heading, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level2);
            lead.Add(heading, PaneHeaderLine.Role.Shrink);
            var steps = new Grid();
            structureChips.Clear();
            for (var index = 0; index < board.Steps.Count; index++)
            {
                var step = board.Steps[index];
                steps.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
                var chip = StructureChip(StructureStepTitle(step), step.Step);
                Grid.SetColumn(chip, steps.ColumnDefinitions.Count - 1); steps.Children.Add(chip); structureChips.Add(chip);
                if (index == board.Steps.Count - 1) continue;
                steps.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star), MinWidth = DesignMetrics.Spacing.Md });
                var line = new Border
                {
                    Height = DesignMetrics.Stroke.Active, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false,
                    Background = b.Brush(step.Step == MightyStructure.Step.Done ? DesignToken.Done : DesignToken.Track),
                };
                AutomationProperties.SetAccessibilityView(line, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
                Grid.SetColumn(line, steps.ColumnDefinitions.Count - 1); steps.Children.Add(line);
            }
            AutomationProperties.SetName(steps, Locale.Get("guidedPanel.phasesAccessibility"));
            var stack = new StackPanel { Spacing = DesignMetrics.Spacing.Sm };
            stack.Children.Add(lead); stack.Children.Add(steps);
            structureHeader.Child = stack;
            SizeStructureChips(structureHeader.ActualWidth);
        }

        /// <summary>A long step title is cut so every chip and the least line between them fit the header's width; the whole title is in the chip's tooltip.</summary>
        private void SizeStructureChips(double width)
        {
            var available = width - 2 * DesignMetrics.Inset.GraphBlockBodyH;
            if (structureChips.Count == 0 || available <= 0) return;
            var most = Math.Max(0, (available - (structureChips.Count - 1) * DesignMetrics.Spacing.Md) / structureChips.Count);
            foreach (var chip in structureChips) chip.MaxWidth = most;
        }

        /// <summary>A step's fill, ink and mark (M/MightyStructureView.swift:333-342): done is green, current the accent, pending grey; an error or a stop in its own fill.</summary>
        private (Brush Fill, Brush Ink, string Symbol) StructureLook(MightyStructure.Step step)
        {
            var b = owner.brushes;
            return step switch
            {
                MightyStructure.Step.Done => (b.Fill(DesignTone.Done), b.FillInk(DesignTone.Done), "checkmark"),
                MightyStructure.Step.Current => (b.Brush(DesignToken.Accent), b.Brush(DesignToken.OnAccent), "circle.fill"),
                MightyStructure.Step.Error => (b.Fill(DesignTone.Err), b.FillInk(DesignTone.Err), "xmark"),
                MightyStructure.Step.Stopped => (b.Fill(DesignTone.Stop), b.FillInk(DesignTone.Stop), "stop.fill"),
                _ => (b.Brush(DesignToken.Track), b.Brush(DesignToken.Ink2), "circle"),
            };
        }

        /// <summary>
        /// One step as a chip (M/MightyStructureView.swift:316-331): a capsule 20 tall, padding <c>Spacing.Md</c>, its 8pt bold mark
        /// and its 11pt title (bold while current) <c>Spacing.Xs</c> apart, in its step's look.
        /// </summary>
        private Border StructureChip(string title, MightyStructure.Step step)
        {
            var (fill, ink, symbol) = StructureLook(step);
            var face = new Grid { ColumnSpacing = DesignMetrics.Spacing.Xs, VerticalAlignment = VerticalAlignment.Center };
            face.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); face.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            face.Children.Add(MightySymbols.Create(symbol, 8, ink, MightySymbols.Weight.Bold));
            var words = StructureWords(title, DesignMetrics.Type.Pill, ink, step == MightyStructure.Step.Current ? FontWeights.Bold : FontWeights.SemiBold);
            Grid.SetColumn(words, 1); face.Children.Add(words);
            var chip = new Border
            {
                Child = face, Height = StructureChipHeight, CornerRadius = new CornerRadius(StructureChipHeight / 2), Background = fill,
                Padding = new Thickness(DesignMetrics.Spacing.Md, 0, DesignMetrics.Spacing.Md, 0), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
            };
            ToolTipService.SetToolTip(chip, title); AutomationProperties.SetName(chip, StructureStepName(title, step));
            return chip;
        }

        /// <summary>A free step's localized name, or the phase's title (M/MightyStructureView.swift:306-314).</summary>
        private static string StructureStepTitle(MightyStructure.BoardStep step) => step.Title ?? Locale.Get(step.Id switch
        {
            "request" => "graph.structure.column.request", "execution" => "graph.structure.column.execution", _ => "graph.structure.column.result",
        });

        private static string StructureStepState(MightyStructure.Step step) => Locale.Get(step switch
        {
            MightyStructure.Step.Done => "graph.structure.step.done", MightyStructure.Step.Current => "graph.structure.step.current",
            MightyStructure.Step.Pending => "graph.structure.step.pending", MightyStructure.Step.Error => "graph.state.error", _ => "graph.state.stopped",
        });

        private static string StructureStepName(string name, MightyStructure.Step step) =>
            Locale.Get("graph.structure.stepAccessibility", new Dictionary<string, string> { ["name"] = name, ["state"] = StructureStepState(step) });

        // ── the kanban ────────────────────────────────────────────────────────

        /// <summary>No request yet: the timeline's own words for that (M/MightyStructureView.swift:378-381).</summary>
        private TextBlock StructureEmpty() => new()
        {
            Text = Locale.Get("graph.timeline.empty"), FontSize = StructureTextSize, Foreground = owner.brushes.Brush(DesignToken.Ink2), TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, DesignMetrics.Spacing.Md, 0, DesignMetrics.Spacing.Md),
        };

        /// <summary>
        /// The board's columns (M/MightyStructureView.swift:467-488): one per step, <c>Spacing.Lg</c> apart and <paramref name="width"/>
        /// wide, each its header and, <c>Spacing.Sm</c> under it, its cards <c>Spacing.Md</c> apart.
        /// </summary>
        private StackPanel BuildStructureKanban(Board board, double width)
        {
            var kanban = structureKanban = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DesignMetrics.Spacing.Lg };
            AutomationProperties.SetAutomationId(kanban, "mighty-structure-board-" + board.Id);
            foreach (var column in board.Columns)
            {
                var header = new Border(); var cards = new StackPanel { Spacing = DesignMetrics.Spacing.Md };
                var view = new StackPanel { Spacing = DesignMetrics.Spacing.Sm, Width = width, VerticalAlignment = VerticalAlignment.Top };
                view.Children.Add(header); view.Children.Add(cards);
                AutomationProperties.SetAutomationId(view, "mighty-structure-column-" + column.Id);
                structureColumns[column.Id] = new(view, header, cards);
                kanban.Children.Add(view);
            }
            return kanban;
        }

        /// <summary>A column's header (M/MightyStructureView.swift:500-513): its step's mark (8 bold, in the step's fill, <c>ink2</c> while pending), its name (12 bold) and its count (10 semibold <c>ink2</c> on a <c>track</c> capsule).</summary>
        private PaneHeaderLine StructureColumnHeader(MightyStructure.BoardColumn column)
        {
            var b = owner.brushes; var (fill, _, symbol) = StructureLook(column.Step.Step);
            var head = new PaneHeaderLine { Spacing = DesignMetrics.Spacing.Sm, Height = StructureChipHeight };
            head.Add(MightySymbols.Create(symbol, 8, column.Step.Step == MightyStructure.Step.Pending ? b.Brush(DesignToken.Ink2) : fill, MightySymbols.Weight.Bold), PaneHeaderLine.Role.Fixed);
            head.Add(StructureWords(StructureStepTitle(column.Step), StructureTextSize, b.Brush(DesignToken.Ink), FontWeights.Bold), PaneHeaderLine.Role.Shrink);
            head.Add(new Border
            {
                Child = StructureWords(column.Count.ToString(CultureInfo.InvariantCulture), DesignMetrics.Type.Small, b.Brush(DesignToken.Ink2), FontWeights.SemiBold), Background = b.Brush(DesignToken.Track),
                CornerRadius = new CornerRadius(StructureChipHeight / 2), Padding = new Thickness(DesignMetrics.Spacing.Sm, 0, DesignMetrics.Spacing.Sm, 0), VerticalAlignment = VerticalAlignment.Center,
            }, PaneHeaderLine.Role.Fixed);
            AutomationProperties.SetAccessibilityView(head, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
            return head;
        }

        // ── follow ────────────────────────────────────────────────────────────

        private static double StructureNow => Environment.TickCount64 / 1000.0;

        /// <summary>
        /// The page is moving under the user's hand (M/MightyStructureView.swift:36-41): the scroll bar or a finger moving it in steps,
        /// the scroll bar held, or a scrolling key whose scroll the page has yet to report, as the system's scroll; a drag or the
        /// moment after one; or a wheel step still settling (<see cref="MightyStructureWheel.UserMoving"/>).
        /// </summary>
        private bool StructureUserMoving
        {
            get
            {
                var now = StructureNow;
                return MightyStructureWheel.UserMoving(structureScrolling || structureBarHeld || now - structureInputAt < MightyStructurePan.ReleaseGrace, structurePanning, structurePanReleasedAt, structureWheel, now);
            }
        }

        /// <summary>
        /// Reports where the page stands, in its own top-down coordinates (M/MightyStructureView.swift:398-416): the page's frame,
        /// the pane's height and the newest board's bottom in one report, so the follow rule never judges one against a stale other.
        /// An older board follows nothing.
        /// </summary>
        private void ObserveStructurePage(bool userScrolling)
        {
            if (ShownStructureScroll is not { ViewportHeight: > 0 } scroll || !structurePage.IsLoaded || !QueuePaneAlive) return;
            double? bottom = null;
            if (structureFollowId is not null && structureKanban is { IsLoaded: true, ActualHeight: > 0 } kanban)
                bottom = kanban.TransformToVisual(structurePage).TransformPoint(new Point(0, kanban.ActualHeight)).Y;
            var origin = -structurePage.TransformToVisual(scroll).TransformPoint(new Point()).Y;
            structureJudgedY = scroll.VerticalOffset;
            var target = structureFollow.Observe(new(origin, scroll.ViewportHeight, structurePage.ActualHeight, structureFollowId, bottom), userScrolling || StructureUserMoving);
            if (structureFollowId is not null) ScrollStructure(target);
        }

        /// <summary>
        /// The page's end, or the board's bottom at the viewport's bottom, at the page's left edge, after the current layout pass: a
        /// report arrives while the page is being laid out (M/MightyStructureView.swift:442-455).
        /// </summary>
        private void ScrollStructure(MightyStructureFollow.Scroll? target)
        {
            if (target is not { } to) return;
            Container.DispatcherQueue.TryEnqueue(() =>
            {
                // A drag, a wheel step or a scroll of the user's is theirs: nothing scrolls under it, even a scroll asked for just before it began.
                if (StructureUserMoving || !QueuePaneAlive || ShownStructureScroll is not { } scroll) return;
                scroll.UpdateLayout();
                if (to.Card is not null && structureKanban is { IsLoaded: true, ActualHeight: > 0 } kanban)
                {
                    var corner = kanban.TransformToVisual(structurePage).TransformPoint(new Point(0, kanban.ActualHeight));
                    scroll.ChangeView(0, Math.Clamp(corner.Y - scroll.ViewportHeight, 0, scroll.ScrollableHeight), null, true);
                }
                else scroll.ChangeView(0, scroll.ScrollableHeight, null, true);
            });
        }

        /// <summary>An older board opens at its top left.</summary>
        private void ScrollStructureTop() => Container.DispatcherQueue.TryEnqueue(() =>
        {
            if (!QueuePaneAlive || ShownStructureScroll is not { } scroll) return;
            scroll.UpdateLayout(); scroll.ChangeView(0, 0, null, true);
        });

        /// <summary>
        /// A scrolling key is the user's scroll before the page reports it, and with Windows' animations off it moves the page in
        /// one change, never in steps: noted when the page can still move that way (-1, 0 or 1 on each axis).
        /// </summary>
        private void NoteStructureInput(int across, int down)
        {
            if (structureScroll is not { } scroll) return;
            if (down < 0 && scroll.VerticalOffset > 0.5 || down > 0 && scroll.VerticalOffset < scroll.ScrollableHeight - 0.5
                || across < 0 && scroll.HorizontalOffset > 0.5 || across > 0 && scroll.HorizontalOffset < scroll.ScrollableWidth - 0.5) structureInputAt = StructureNow;
        }

        /// <summary>
        /// A wheel on the page (M/MightyStructurePanning.swift:247-289). The page's scroll viewer moves itself, chaining a wheel out
        /// of an inner scroll view at its edge, so the wheel is only noted as the user's: a Windows wheel message, a mouse's notch
        /// or a touchpad's, carries no gesture, and is a step. A positive delta shows what is above, or what is to the right.
        /// </summary>
        private void OnStructureWheel(object sender, PointerRoutedEventArgs args)
        {
            if (structureScroll is not { } scroll || args.KeyModifiers.HasFlag(VirtualKeyModifiers.Control)) return;
            var wheel = args.GetCurrentPoint(scroll).Properties; double delta = wheel.MouseWheelDelta;
            structureWheel.Feed(MightyStructureWheel.Stage.Step, true, wheel.IsHorizontalMouseWheel ? (-delta, 0) : (0, delta), StructureNow);
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
        /// What a press lands on (M/MightyStructurePanning.swift:133-152): a text where its words are laid out, a text box or a
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

        /// <summary>The point is on the words a text block laid out: its measured text, not the frame it was given (M/MightyStructurePanning.swift:157-166).</summary>
        private static bool StructureOnText(TextBlock words, Point point) =>
            point.X <= words.DesiredSize.Width + StructureTextSlop && point.Y <= words.DesiredSize.Height + StructureTextSlop;

        /// <summary>
        /// The point is on the answer's laid-out text (M/MightyStructurePanning.swift:168-179): the character nearest to it, a
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
            structurePanning = false; structurePanReleasedAt = StructureNow;
            // A click this release raises on what was pressed is the drag's, not the user's; forgotten once the release is over.
            Container.DispatcherQueue.TryEnqueue(() => structurePanned = false);
        }

        /// <summary>The press is over: the scroll bar's last step may still be reported, and a part it kept as it was is built now.</summary>
        private void EndStructurePress()
        {
            if (structureBarHeld) structureInputAt = StructureNow;
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

        // ── cards ─────────────────────────────────────────────────────────────

        /// <summary>
        /// A card of the board as it should be now (M/MightyStructureView.swift:515-525): built again only when what it shows changed,
        /// in its old place. Its folds are judged at the width its text really has, so a new width builds only a card whose folded
        /// lines it changes. A card with a press down in it or a selection in one of its texts waits (<see cref="StructureHeld"/>).
        /// </summary>
        private (FrameworkElement View, bool Built) StructureCardView(MightyStructure.Card card, double width, string heading, string language, IReadOnlyList<ModelOption>? catalog)
        {
            var item = card.Item; var textWidth = width - 2 * DesignMetrics.Inset.GraphBlockBodyH;
            static bool Folds(MightyStructure.Excerpt? text, int lines, double size, double width) => text?.Folds(lines, MightyStructure.CharactersPerLine(width, size)) == true;
            bool Open(string part, string id) => structureExpanded.Contains(part + ":" + id);
            var requestFolds = Folds(item.Request, MightyStructure.RequestLines, StructureTextSize, textWidth);
            var messageFolds = Folds(item.Message, MightyStructure.MessageLines, StructureTextSize, textWidth);
            var activity = string.Join('\n', item.Activity);
            object key; Func<FrameworkElement> build;
            switch (card.Kind)
            {
                case MightyStructure.CardKind.Request:
                    key = (heading, item.Request, item.Input, item.NodeId, Open("request", item.RunId), requestFolds, language);
                    build = () => StructureRequestCard(item, heading, requestFolds);
                    break;
                case MightyStructure.CardKind.Execution:
                    key = (item.Ordinal, item.Status, item.Tally, item.Message, activity, item.Finished, Open("message", item.RunId), messageFolds, item.ExecutionNodeId, item.NodeId, language);
                    build = () => StructureExecution(item, messageFolds);
                    break;
                case MightyStructure.CardKind.Result:
                    key = (item.Status, item.Finished, item.Answer, Open("answer", item.RunId), item.ExecutionNodeId, item.NodeId, language);
                    build = () => StructureResult(item);
                    break;
                case MightyStructure.CardKind.Summary:
                    var line = MightyStructure.Fold.ResultLine(item);
                    key = ((heading, item.Request, item.Input, item.NodeId, Open("request", item.RunId), requestFolds, language), (item.Ordinal, item.Status, item.Tally, item.Message, activity, item.Finished, Open("message", item.RunId), messageFolds), (item.ExecutionNodeId, line));
                    build = () => StructureSummary(item, heading, requestFolds, messageFolds, line);
                    break;
                default:
                    var node = card.Node!; var open = Open("node", node.NodeId); var nodeWidth = MightyStructure.NodeTextWidth(width, node.Depth);
                    var folds = Folds(node.Prompt, MightyStructure.PromptLines, StructurePromptSize, nodeWidth) || Folds(node.Output, MightyStructure.OutputLines, StructureOutputSize, nodeWidth) || node.Activity.Count > 1;
                    var model = node.Model is { } named ? ModelUsageFormat.ShortName(named, catalog, versioned: true) : null;
                    key = (node, card.Parent, open, folds, model, language);
                    build = () => StructureNodeCard(node, card.Parent, open, folds, model);
                    break;
            }
            if (structureCards.TryGetValue(card.Id, out var part))
            {
                if (Equals(part.Key, key)) return (part.View, false);
                if (StructureHeld(part.View)) { structureDeferred = true; return (part.View, false); }
            }
            var view = build();
            if (part.View?.Parent is Panel place) place.Children[place.Children.IndexOf(part.View)] = view;
            structureCards[card.Id] = (key, view);
            return (view, true);
        }

        /// <summary>
        /// A press is down in the card, or one of its texts holds a selection: built again now, the drag would lose its text and
        /// the click its button, so the card waits until that ends (as the diagram and the timeline wait for a selection,
        /// MainWindow.MightyGraph.cs:411, MainWindow.MightyTimeline.cs:216). A draw a card's own button asked for waits for nothing.
        /// </summary>
        private bool StructureHeld(FrameworkElement part) => !structureForced && (StructureInside(structurePressed, part)
            || VisualChildren(part).Any(child => child is TextBlock { SelectedText.Length: > 0 } || child is RichEditBox { FocusState: not FocusState.Unfocused } answer && answer.Document.Selection.Length != 0));

        /// <summary>A free board's request (M/MightyStructureView.swift:529-546): "Request N · Claude" (12 bold) with its reveal button, and what was asked.</summary>
        private Border StructureRequestCard(MightyStructure.Item item, string heading, bool folds)
        {
            var request = new StackPanel { Spacing = DesignMetrics.Spacing.Sm };
            request.Children.Add(StructureHeader(StructureWords(heading, StructureTextSize, owner.brushes.Brush(DesignToken.Ink), FontWeights.Bold), StructureRevealButton(item.NodeId, item.NodeId)));
            if (item.Request is { } text) request.Children.Add(StructureText(text, "request:" + item.RunId, MightyStructure.RequestLines, StructureTextSize, folds, Locale.Get("graph.structure.column.request")));
            var card = StructureSurface(request);
            AutomationProperties.SetAutomationId(card, "mighty-structure-request-" + item.RunId);
            AutomationProperties.SetName(card, Locale.Get("graph.structure.contentAccessibility", new Dictionary<string, string> { ["name"] = heading, ["text"] = item.Input }));
            return card;
        }

        /// <summary>The run part's header line: the gears and "Run · Request N" (11 bold), the main agent's status pill and its tally (11 semibold <c>ink2</c>), and its reveal button.</summary>
        private Grid StructureExecutionHeader(MightyStructure.Item item)
        {
            var b = owner.brushes;
            var name = Locale.Get("graph.block.executionOf", new Dictionary<string, string> { ["n"] = item.Ordinal.ToString(CultureInfo.InvariantCulture) });
            var lead = new Grid { ColumnSpacing = DesignMetrics.Spacing.Sm, VerticalAlignment = VerticalAlignment.Center };
            void Lead(FrameworkElement part, GridLength size)
            {
                Grid.SetColumn(part, lead.ColumnDefinitions.Count); lead.ColumnDefinitions.Add(new() { Width = size }); lead.Children.Add(part);
            }
            // The Mac's gearshape.2 (M/MightyGraphView.swift:577).
            var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DesignMetrics.Spacing.Xs, VerticalAlignment = VerticalAlignment.Center };
            label.Children.Add(MightySymbols.Create("gearshape.2", StructureOutputSize, b.Brush(DesignToken.Ink))); label.Children.Add(StructureWords(name, StructureOutputSize, b.Brush(DesignToken.Ink), FontWeights.Bold));
            Lead(label, GridLength.Auto);
            Lead(StatusPill(MightyGraphBlockModel.StateLabel(item.Status), StatusGlyph.Tone(item.Status), 18), GridLength.Auto);
            Lead(StructureTally(item), new GridLength(1, GridUnitType.Star));
            return StructureHeader(lead, StructureRevealButton(item.ExecutionNodeId, item.NodeId));
        }

        /// <summary>The main block's latest reply, or that it is waiting for one, and its last tool calls (M/MightyStructureView.swift:635-650).</summary>
        private void StructureProgress(Panel body, MightyStructure.Item item, bool folds)
        {
            if (item.Message is { } message) body.Children.Add(StructureText(message, "message:" + item.RunId, MightyStructure.MessageLines, StructureTextSize, folds, Locale.Get("graph.structure.latest")));
            else if (!item.Finished && item.Activity.Count == 0) body.Children.Add(new TextBlock { Text = Locale.Get("graph.block.waitingResponse"), FontSize = StructureTextSize, Foreground = owner.brushes.Brush(DesignToken.Ink2), TextWrapping = TextWrapping.Wrap });
            if (item.Activity.Count == 0) return;
            var calls = new StackPanel { Spacing = DesignMetrics.Spacing.Xxs };
            foreach (var line in item.Activity) calls.Children.Add(StructureActivityRow(line));
            AutomationProperties.SetName(calls, Locale.Get("graph.structure.activity"));
            body.Children.Add(calls);
        }

        /// <summary>A free board's 실행 card (M/MightyStructureView.swift:606-625): the main agent's status, tally, latest reply and tool calls, as the diagram's run block holds them.</summary>
        private Border StructureExecution(MightyStructure.Item item, bool folds)
        {
            var body = new StackPanel { Spacing = DesignMetrics.Spacing.Sm };
            body.Children.Add(StructureExecutionHeader(item));
            StructureProgress(body, item, folds);
            var card = StructureSurface(body);
            AutomationProperties.SetAutomationId(card, "mighty-structure-execution-" + item.RunId);
            AutomationProperties.SetName(card, Locale.Get("graph.block.executionOf", new Dictionary<string, string> { ["n"] = item.Ordinal.ToString(CultureInfo.InvariantCulture) }));
            return card;
        }

        private TextBlock StructureTally(MightyStructure.Item item) => StructureWords(Locale.Get("graph.structure.tally", new Dictionary<string, string>
        {
            ["agents"] = item.Tally.Agents.ToString(CultureInfo.InvariantCulture), ["tools"] = item.Tally.Tools.ToString(CultureInfo.InvariantCulture),
        }), StructureOutputSize, owner.brushes.Brush(DesignToken.Ink2), FontWeights.SemiBold);

        /// <summary>The result's header words and symbol (11 bold), in the outcome's ink once there is one (M/MightyStructureView.swift:652-662).</summary>
        private StackPanel StructureResultTitle(MightyStructure.Item item)
        {
            var b = owner.brushes; var ink = item.Finished ? b.Text(StatusGlyph.Tone(item.Status)) : b.Brush(DesignToken.Ink2);
            var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DesignMetrics.Spacing.Xs, VerticalAlignment = VerticalAlignment.Center };
            label.Children.Add(MightySymbols.Create(item.Status switch { "error" => "exclamationmark.triangle", "stopped" => "stop.circle", _ => "checkmark.seal" }, StructureOutputSize, ink, MightySymbols.Weight.Bold));
            label.Children.Add(StructureWords(MightyGraphBlockModel.ResultTitle(item.Status), StructureOutputSize, ink, FontWeights.Bold));
            return label;
        }

        /// <summary>
        /// A free board's 결과 card (M/MightyStructureView.swift:548-562, 664-673): its header words, then the answer as the transcript
        /// renders a reply; without one, how the request ended (its last reply is in the run part already) or that it is still to
        /// come. Until the request finished there is no result block: its button shows the run instead.
        /// </summary>
        private Border StructureResult(MightyStructure.Item item)
        {
            structureAnswers.Remove(item.RunId);
            var reveal = item.Finished ? MightyGraphBlockSize.NodeId(item.RunId, "result") : item.ExecutionNodeId;
            var body = new StackPanel { Spacing = DesignMetrics.Spacing.Sm };
            body.Children.Add(StructureHeader(StructureResultTitle(item), StructureRevealButton(reveal, item.NodeId)));
            if (item.Answer is { } answer) body.Children.Add(StructureAnswer(item, answer));
            else body.Children.Add(new TextBlock { Text = item.Finished ? MightyGraphBlockModel.StateLabel(item.Status) : Locale.Get("graph.structure.resultPending"), FontSize = StructureTextSize, Foreground = owner.brushes.Brush(DesignToken.Ink2), TextWrapping = TextWrapping.Wrap });
            var card = StructureSurface(body);
            AutomationProperties.SetAutomationId(card, "mighty-structure-result-" + item.RunId);
            return card;
        }

        /// <summary>
        /// A styled board's request (M/MightyStructureView.swift:564-604): "Request N · Claude" with its reveal button and what was
        /// asked; a rule; its 실행 part (the run's header line, latest reply and tool calls); and once it finished, how it ended on
        /// one line (11): the answer's first line in <c>ink2</c>, else the outcome in its ink, beside the result's reveal button.
        /// </summary>
        private Border StructureSummary(MightyStructure.Item item, string heading, bool requestFolds, bool messageFolds, string? line)
        {
            var b = owner.brushes;
            var body = new StackPanel { Spacing = DesignMetrics.Spacing.Sm };
            body.Children.Add(StructureHeader(StructureWords(heading, StructureTextSize, b.Brush(DesignToken.Ink), FontWeights.Bold), StructureRevealButton(item.NodeId, item.NodeId)));
            if (item.Request is { } text) body.Children.Add(StructureText(text, "request:" + item.RunId, MightyStructure.RequestLines, StructureTextSize, requestFolds, Locale.Get("graph.structure.column.request")));
            var rule = new Border { Height = DesignMetrics.Stroke.Line, Background = b.Brush(DesignToken.Line) };
            AutomationProperties.SetAccessibilityView(rule, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
            body.Children.Add(rule);
            body.Children.Add(StructureExecutionHeader(item));
            StructureProgress(body, item, messageFolds);
            if (item.Finished)
            {
                var ended = new Grid { ColumnSpacing = DesignMetrics.Spacing.Sm };
                ended.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); ended.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
                ended.Children.Add(StructureResultTitle(item));
                var words = StructureWords(line ?? MightyGraphBlockModel.StateLabel(item.Status), StructureOutputSize, line is null ? b.Text(StatusGlyph.Tone(item.Status)) : b.Brush(DesignToken.Ink2));
                if (line is not null) ToolTipService.SetToolTip(words, line);
                Grid.SetColumn(words, 1); ended.Children.Add(words);
                body.Children.Add(StructureHeader(ended, StructureRevealButton(MightyGraphBlockSize.NodeId(item.RunId, "result"), item.NodeId)));
            }
            var card = StructureSurface(body);
            AutomationProperties.SetAutomationId(card, "mighty-structure-request-" + item.RunId);
            AutomationProperties.SetName(card, Locale.Get("graph.structure.contentAccessibility", new Dictionary<string, string> { ["name"] = heading, ["text"] = item.Input }));
            return card;
        }

        /// <summary>
        /// The answer as Markdown (M/MightyStructureView.swift:778-790). Folded, its preview: whole lines with any open code
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
        /// A sub-agent or task as a small card (M/MightyStructureView.swift:675-739): radius 8 on <c>card</c> with a <c>line</c>
        /// edge and its status as a 3pt bar in its mark colour; its kind's symbol (9) in its ink, its title (11 semibold, cut last)
        /// and "kind · model" (10 <c>ink2</c>) on one line; the block it runs under when it is nested (10 <c>ink2</c>); then what it
        /// was asked (10.5 <c>ink2</c>, two lines), its latest output (11, six lines) and its last tool call. Opened, it shows them
        /// whole and its last few tool calls. Each level of the tree stands <c>Spacing.Lg</c> further in, up to
        /// <see cref="MightyStructure.MaximumIndent"/>; <see cref="MightyStructure.NodeTextWidth"/> takes the indent and the padding off.
        /// </summary>
        private Border StructureNodeCard(MightyStructure.Node node, string? parent, bool open, bool folds, string? model)
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
            if (parent is not null)
            {
                var under = new Grid { ColumnSpacing = DesignMetrics.Spacing.Xs };
                under.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); under.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
                under.Children.Add(MightySymbols.Create("arrow.turn.down.right", DesignMetrics.Type.Small, b.Brush(DesignToken.Ink2)));
                var words = StructureWords(Locale.Get("graph.structure.underParent", new Dictionary<string, string> { ["title"] = parent }), DesignMetrics.Type.Small, b.Brush(DesignToken.Ink2));
                Grid.SetColumn(words, 1); under.Children.Add(words);
                body.Children.Add(under);
            }
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
        /// One tool call (M/MightyStructureView.swift:741-755): its kind's glyph (10, in a 14 column; <c>errText</c> when it
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

        // ── parts ─────────────────────────────────────────────────────────────

        /// <summary>
        /// A card of the structure (M/MightyStructureView.swift:824-830): the diagram's block card (<c>card</c>, a 1pt
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
        /// A text shown in place (M/MightyStructureView.swift:832-840): wrapped to its column and selectable, at most
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
        /// Plain text (M/MightyStructureView.swift:762-776): its first lines folded, the whole of it (up to its cap) opened. The
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

        /// <summary>The toggle under a folded text (M/MightyStructureView.swift:792-804): "more" or "less", 11 bold <c>accent</c>.</summary>
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
        /// A small button of the view (M/MightyStructureView.swift:161-174, 806-817): plain, <c>Layout.HitTarget</c> square, its
        /// 11pt symbol in <c>ink2</c>. A press that became a drag of the page is not its click.
        /// </summary>
        private Button StructureButton(string symbol, string name, string automationId, Action act) =>
            ZoomButton(MightySymbols.Create(symbol, 11, null), name, automationId, () => { if (!structurePanned) act(); });

        /// <summary>The small header button that shows the block in the diagram.</summary>
        private Button StructureRevealButton(string nodeId, string requestNodeId) =>
            StructureButton("arrow.up.right.square", Locale.Get("graph.structure.reveal"), "mighty-structure-reveal-" + nodeId, () => RevealStructureBlock(nodeId, requestNodeId));

        // An opened or folded text is being read: nothing follows until the reader comes back to the board's end (M/MightyStructureView.swift:434-437).
        private void ToggleStructureText(string key)
        {
            if (!structureExpanded.Add(key)) structureExpanded.Remove(key);
            structureFollow.Stop(); RedrawStructure("mighty-structure-more-" + key);
        }

        /// <summary>
        /// Draws the view again for one of its own buttons, which its click ends the press of. A card's button is built again with
        /// its card, and focus on a control that leaves the tree moves on to the next one, which the page would bring into view
        /// (<see cref="ScrollViewer.BringIntoViewOnFocusChange"/>): focus waits on the page instead, and a keyboard's focus goes
        /// to the button built in the old one's place (<paramref name="automationId"/>).
        /// </summary>
        private void RedrawStructure(string automationId)
        {
            structurePressed = null;
            var held = structureScroll?.XamlRoot is { } root ? FocusManager.GetFocusedElement(root) as Control : null;
            var state = held is not null && structureView is not null && StructureInside(held, structureView) ? held.FocusState : FocusState.Unfocused;
            if (state != FocusState.Unfocused) structureScroll!.Focus(FocusState.Programmatic);
            structureForced = true;
            try { RefreshMightyView(Session); } finally { structureForced = false; }
            if (state is FocusState.Unfocused or FocusState.Pointer) return;
            Container.DispatcherQueue.TryEnqueue(() =>
            {
                if (!QueuePaneAlive || ShownStructureScroll is not { } scroll || structureView is null) return;
                scroll.UpdateLayout();
                VisualChildren(structureView).OfType<Button>().FirstOrDefault(button => AutomationProperties.GetAutomationId(button) == automationId)?.Focus(state);
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
