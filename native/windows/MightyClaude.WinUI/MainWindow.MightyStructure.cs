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
using Rectangle = Microsoft.UI.Xaml.Shapes.Rectangle;
using Storyboard = Microsoft.UI.Xaml.Media.Animation.Storyboard;
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
        /// <summary>The page inside the scroll view, the pane's height: a board narrower than the pane sits at its left (M/MightyStructureView.swift:378-407).</summary>
        private readonly Border structurePage = new()
        {
            Padding = new Thickness(DesignMetrics.Inset.GraphBlockBodyH, DesignMetrics.Spacing.Md, DesignMetrics.Inset.GraphBlockBodyH, 0),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Stretch,
        };
        /// <summary>The boards and what they were built from, rebuilt only when something they draw changed (M/MightyGraphView.swift:325-339).</summary>
        private MightyStructure.Signature? structureSignature;
        private IReadOnlyList<Board> structureBoards = [];
        private readonly MightyStructure.ItemCache structureItems = new();
        /// <summary>
        /// What the view keeps of the pane while it shows its diagram or timeline (M/MightyStructureView.swift:8-62): the texts
        /// opened past their folded lines, by key; the board shown; which of its columns follow their end; and the wheel steps
        /// that are the user's.
        /// </summary>
        private readonly HashSet<string> structureExpanded = [];
        private readonly MightyStructure.Navigation structureNavigation = new();
        private readonly MightyStructureFollow.ColumnWatch structureFollow = new();
        private readonly MightyStructureWheel.Tracker structureWheel = new();
        private bool structureShown, structureAppearing;
        /// <summary>The newest request and the board drawn, whether that board is the newest (only its columns follow), and the column the current or last drag, wheel or key moves (null for the page alone).</summary>
        private string? structureNewestId, structureBoardId, structureMovingColumn;
        private bool structureOnNewest;
        /// <summary>The navigation bar's controls, kept and changed in place so a press on one is never lost to a draw.</summary>
        private StackPanel? structureBar;
        private Button? structurePrevious, structureNext, structureListButton, structureBadge;
        private TextBlock? structurePosition, structureBadgeWords;
        private Flyout? structureListFlyout;
        private StackPanel? structureList;
        private string? structureBarLanguage;
        /// <summary>The board's header and its rule, and what the header was built from.</summary>
        private Border? structureHeader, structureRule;
        private string? structureHeaderKey;
        /// <summary>The frame (the board's columns), the column width it stands at, its columns and each card as last built.</summary>
        private string? structureFrameKey;
        private double structureWidth;
        private readonly Dictionary<string, StructureColumn> structureColumns = [];
        private readonly Dictionary<string, (object Key, FrameworkElement View)> structureCards = [];
        private readonly Dictionary<string, (RichEditBox View, string Source, bool Light)> structureAnswers = [];
        /// <summary>A left press on the page that may become a drag (where it went down, where the page stood, and the column pressed with where it stood), the drag it became, and when the last one ended, in seconds.</summary>
        private (Point Point, double X, StructureColumn? Column, double Y)? structurePress;
        private bool structurePanning, structurePanned;
        private double structurePanReleasedAt;
        /// <summary>The user's own scroll (M/MightyStructureView.swift:22-41): a press held on a scroll bar, and when the last scrolling key came or the bar was let go (the column may report it late).</summary>
        private bool structureBarHeld;
        private double structureInputAt;
        /// <summary>
        /// What a press went down on, until it comes up; whether a part was left as it was for a press or a selection, to be built
        /// once that ends; and a draw a card's own button asked for, which waits for nothing.
        /// </summary>
        private DependencyObject? structurePressed;
        private bool structureDeferred, structureForced;
        /// <summary>
        /// What runs on the board as the diagram shows it (M/MightyStructureView.swift:285, 583, 604, 619, 628, 708, 741): the capsules
        /// the pane's one timer waves (<see cref="UpdateActivityTimer"/>) and each outlined card's line and march, by card id (the
        /// header's under <see cref="StructureHeaderMarks"/>); and whether Windows animations were on for the draw.
        /// </summary>
        private readonly Dictionary<string, ScaleTransform[]> structureActivitySets = [];
        private readonly Dictionary<string, (string Kind, Rectangle Line, Storyboard? March)> structureOutlines = [];
        private bool structureMotion;
        private const string StructureHeaderMarks = "header";

        /// <summary>
        /// One column of the board: its id, its frame, its header's place and what that header was built from, its cards' panel in a
        /// scroll view of its own, whether a finger or its scroll bar moves it in steps, and the offset the follow rule last judged.
        /// </summary>
        private sealed class StructureColumn(string id, Grid view, Border header, ScrollViewer scroll, StackPanel cards)
        {
            internal readonly string Id = id;
            internal readonly Grid View = view;
            internal readonly Border Header = header;
            internal readonly ScrollViewer Scroll = scroll;
            internal readonly StackPanel Cards = cards;
            /// <summary>The column's state as a wash under its cards (<see cref="MightyStructure.Tone.OfColumn"/>), coloured with its header.</summary>
            internal readonly Border Wash = new() { CornerRadius = new CornerRadius(StructureCardCorner), IsHitTestVisible = false };
            internal object? HeaderKey;
            internal bool Scrolling;
            internal double JudgedY;
        }

        /// <summary>A chip's height, a card's and a sub-agent card's corners and a tool call's glyph column (M/MightyStructureView.swift:83-92); every card's status bar is <see cref="DesignMetrics.Pipeline.CardBarWidth"/>.</summary>
        private const double StructureChipHeight = 20, StructureCardCorner = DesignMetrics.Radius.Entry, StructureNodeCorner = DesignMetrics.Radius.Row, StructureGlyphWidth = 14;
        /// <summary>Type sizes: a card's texts, a sub-agent's output, its prompt (M/MightyStructureView.swift:93-96).</summary>
        private const double StructureTextSize = DesignMetrics.Type.Block, StructureOutputSize = DesignMetrics.Type.Pill, StructurePromptSize = 10.5;
        /// <summary>The list of boards (M/MightyStructureView.swift:97-99).</summary>
        private const double StructureListWidth = 340, StructureListHeight = 420;
        /// <summary>Points around a text's laid-out words that still press the text (M/MightyStructurePanning.swift:154-155).</summary>
        private const double StructureTextSlop = 4;

        // ── the view ──────────────────────────────────────────────────────────

        /// <summary>
        /// The structure view (M/MightyStructureView.swift:101-119, 371-424): ◀, the board's place with the list, ▶ and the badge
        /// of a newer board; the board's header and a rule; then the page on <c>cardRaised</c>, its content <c>Inset.GraphBlockBodyH</c>
        /// in and <c>Spacing.Md</c> down, which scrolls sideways only, when the columns do not fit; each column scrolls up and down on
        /// its own (<see cref="BuildStructureKanban"/>). Dragging the page's empty space moves it and the column pressed.
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
            structureBadgeWords = new TextBlock { TextSize = DesignMetrics.Type.Small, FontWeight = FontWeights.Bold, TextTrimming = TextTrimming.CharacterEllipsis };
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
            Grid.SetRow(structureHeader, 1); structureView.Children.Add(structureHeader);
            structureRule = new Border { Height = DesignMetrics.Stroke.Line, Background = b.Brush(DesignToken.Line), Visibility = Visibility.Collapsed };
            AutomationProperties.SetAccessibilityView(structureRule, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
            Grid.SetRow(structureRule, 2); structureView.Children.Add(structureRule);

            structureScroll = new ScrollViewer
            {
                Content = structurePage, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollMode = ScrollMode.Disabled, HorizontalScrollMode = ScrollMode.Auto, IsTabStop = true, Background = b.Brush(DesignToken.CardRaised),
            };
            AutomationProperties.SetAutomationId(structureScroll, "mighty-structure-page-" + id);
            AutomationProperties.SetName(structureScroll, Locale.Get("graph.view.structure"));
            // The columns share the pane's width: a new width sizes them again.
            structureScroll.SizeChanged += (_, args) => { if (args.NewSize.Width != args.PreviousSize.Width) QueueGraphRefresh(); };
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
            // A pane shown again on its tab waves its running cards' capsules again (as the diagram's, MainWindow.MightyGraph.cs:261).
            structureView.Loaded += (_, _) => UpdateActivityTimer();
            Grid.SetRow(structureView, 1); graphHost!.Children.Add(structureView);
        }

        /// <summary>
        /// Shows or hides the view. Shown again, the newest board's columns open at their end and a following one is taken to it by
        /// the next draw. The running cards' borders march only while it shows.
        /// </summary>
        private void ShowMightyStructure(bool shown)
        {
            if (structureView is null) return;
            structureView.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
            if (shown == structureShown) return;
            structureShown = shown;
            if (shown) structureAppearing = true;
            else
            {
                EndStructurePan(); structurePressed = null; structureBarHeld = false; structureListFlyout?.Hide();
                foreach (var column in structureColumns.Values) column.Scrolling = false;
            }
            foreach (var edge in structureOutlines.Values) if (edge.March is { } march) StartStructureMarch(edge.Line, march);
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
            structureDeferred = false; structureMotion = AnimationsEnabled;
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
            var changed = frameKey != structureFrameKey; var rebuilt = changed;
            // A frame built again for the same board keeps where each column's reader was.
            Dictionary<string, double> kept = [];
            if (changed)
            {
                // A new frame takes every card out of its column: not while a press is down on the page or a card holds a selection.
                if (structureFrameKey is not null && !structureForced && (structurePressed is not null || structureCards.Values.Any(part => StructureHeld(part.View)))) { structureDeferred = true; return; }
                structureFrameKey = frameKey;
                if (board?.Id == structureBoardId) kept = structureColumns.Values.ToDictionary(column => column.Id, column => column.Scroll.VerticalOffset);
                foreach (var column in structureColumns.Values) column.Cards.Children.Clear();
                structureColumns.Clear();
                structurePage.Child = board is null ? StructureEmpty() : BuildStructureKanban(board, width);
            }
            else if (width != structureWidth)
            {
                foreach (var column in structureColumns.Values) column.View.Width = width;
                changed = true;
            }
            structureWidth = width;
            // The newest board's columns follow their end; an older board follows nothing.
            structureOnNewest = index is { } shown && shown == boards.Count - 1;
            var drawn = new HashSet<string>();
            if (board is not null)
            {
                var catalog = PaneRuntime(pane.Provider)?.ModelCatalog?.Models;
                foreach (var column in board.Columns)
                {
                    if (!structureColumns.TryGetValue(column.Id, out var place)) continue;
                    var last = column.Id == board.Columns[^1].Id;
                    var tone = MightyStructure.Tone.OfColumn(column);
                    var headerKey = (StructureStepTitle(column.Step), column.Step.Step, column.Count, language, last, tone);
                    if (!Equals(place.HeaderKey, headerKey))
                    {
                        place.Header.Child = StructureColumnHeader(column, last); place.HeaderKey = headerKey;
                        // The step's state as a wash under its cards (M/MightyStructureView.swift columnTint); none while it is to come.
                        place.Wash.Background = tone is { } wash ? owner.brushes.Brush(DesignPalette.SoftToken(wash), DesignMetrics.Pipeline.ColumnTintOpacity) : owner.brushes.Transparent;
                        AutomationProperties.SetName(place.Scroll, StructureStepTitle(column.Step));
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
                        var (view, built) = StructureCardView(card, width, GraphRequestTitle(runs[card.Item.RunIndex], card.Item.Ordinal), language, catalog, MightyStructure.Tone.OfCard(card, column));
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
                structureCards.Remove(gone); DropStructureMarks(gone);
                if (gone.StartsWith("result:", StringComparison.Ordinal)) structureAnswers.Remove(gone["result:".Length..]);
            }
            // What the user opened follows the requests: only what the pane still holds is kept (as the timeline's, MainWindow.MightyTimeline.cs:276).
            if (structureExpanded.Count > 0) structureExpanded.IntersectWith(boards.SelectMany(b => b.Items).SelectMany(StructureTextKeys));
            RethemeStructureAnswers();
            DrawStructureBar(boards, index, board);
            if (board is not null) DrawStructureHeader(board, boards.Count);
            else { DropStructureMarks(StructureHeaderMarks); structureHeaderKey = null; }
            // The total counts the retained requests; loaded ones are named apart.
            graphTotal.Text = MightyGraphBlockModel.ToolbarSummary(retained) + (older.Count > 0 ? " · " + Locale.Get("graph.history.headerLoaded", new Dictionary<string, string> { ["count"] = older.Count.ToString() }) : "");
            var help = MightyGraphBlockModel.ToolbarHelp(retained);
            ToolTipService.SetToolTip(graphTotal, help); AutomationProperties.SetHelpText(graphTotal, help);

            var newest = runs.Count > 0 ? runs[^1].Id : null; var arrived = newest != structureNewestId;
            structureNewestId = newest;
            var moved = board?.Id != structureBoardId;
            structureBoardId = board?.Id;
            var appearing = structureAppearing; structureAppearing = false;
            if (board is not null)
            {
                // Shown again, or drawn in a new frame, each column appears (M/MightyStructureView.swift:491, 935-955).
                if (appearing || rebuilt) foreach (var place in structureColumns.Values) AppearStructureColumn(board.Id, place, !appearing && kept.TryGetValue(place.Id, out var y) ? y : null);
                // A new request on the newest board is the user's own: every column shows its end, and follows it from then on.
                else if (arrived && newest is not null && structureOnNewest) foreach (var place in structureColumns.Values) ScrollStructureColumn(place, structureFollow.NewRequest(board.Id, place.Id));
                // Shown again, another board, or a new request on the newest one: the page shows its first column again (M/MightyStructureView.swift:409-424).
                if (appearing || moved || arrived && structureOnNewest) { changed = true; ScrollStructureStart(); }
            }
            // A column shown again has the size it had: where each stands is reported once it is laid out.
            if (changed) Container.DispatcherQueue.TryEnqueue(() =>
            {
                if (!QueuePaneAlive || ShownStructureScroll is not { } scroll) return;
                scroll.UpdateLayout();
                foreach (var place in structureColumns.Values) ObserveStructureColumn(place, false);
            });
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
        /// The board's header (M/MightyStructureView.swift:280-304): the diagram's activity mark while it runs, its status pill and,
        /// for a pipeline, its name (13 bold in the heading font). Its steps are the columns' own headers below
        /// (<see cref="StructureColumnHeader"/>), so the pipeline is not drawn twice.
        /// </summary>
        private void DrawStructureHeader(Board board, int count)
        {
            if (structureHeader is null) return;
            var title = StructureTitle(board, count);
            var key = string.Join('\n', board.Id, board.Status, title, Locale.LanguagePreference, structureMotion.ToString());
            if (key == structureHeaderKey) return;
            structureHeaderKey = key;
            var b = owner.brushes;
            var lead = new PaneHeaderLine { Spacing = DesignMetrics.Spacing.Sm };
            DropStructureMarks(StructureHeaderMarks);
            if (StructureIndicator(board.Status, StructureHeaderMarks) is { } running) lead.Add(running, PaneHeaderLine.Role.Fixed);
            lead.Add(StatusPill(MightyGraphBlockModel.StateLabel(board.Status), StatusGlyph.Tone(board.Status), 18), PaneHeaderLine.Role.Fixed);
            // A request's first line is its 요청 card right below; a pipeline's name is not on any card (M/MightyStructureView.swift:302-306).
            if (board.Kind == MightyStructure.BoardKind.Pipeline)
            {
                var heading = StructureWords(title, DesignMetrics.Type.Title, b.Brush(DesignToken.Ink), FontWeights.Bold); heading.TextFont = TextArea.Heading;
                AutomationProperties.SetHeadingLevel(heading, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level2);
                lead.Add(heading, PaneHeaderLine.Role.Shrink);
            }
            structureHeader.Child = lead;
        }

        /// <summary>A step's fill, ink and mark (M/MightyStructureView.swift:333-342): done is green, current in its column's tone, pending grey; an error or a stop in its own fill.</summary>
        private (Brush Fill, Brush Ink, string Symbol) StructureLook(MightyStructure.Step step, DesignTone? tone)
        {
            var b = owner.brushes;
            return step switch
            {
                MightyStructure.Step.Done => (b.Fill(DesignTone.Done), b.FillInk(DesignTone.Done), "checkmark"),
                // The current step in its column's tone (MightyStructure.Tone.OfColumn): amber while its request waits, green once done.
                MightyStructure.Step.Current => (b.Fill(tone ?? DesignTone.Run), b.FillInk(tone ?? DesignTone.Run), "circle.fill"),
                MightyStructure.Step.Error => (b.Fill(DesignTone.Err), b.FillInk(DesignTone.Err), "xmark"),
                MightyStructure.Step.Stopped => (b.Fill(DesignTone.Stop), b.FillInk(DesignTone.Stop), "stop.fill"),
                _ => (b.Brush(DesignToken.Track), b.Brush(DesignToken.Ink2), "circle"),
            };
        }

        /// <summary>
        /// One step as a chip (M/MightyStructureView.swift:316-331): a capsule 20 tall, padding <c>Spacing.Md</c>, its 8pt bold mark
        /// and its 11pt title (bold while current) <c>Spacing.Xs</c> apart, in its step's look.
        /// </summary>
        private Border StructureChip(string title, MightyStructure.Step step, DesignTone? tone)
        {
            var (fill, ink, symbol) = StructureLook(step, tone);
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
            Text = Locale.Get("graph.timeline.empty"), TextSize = StructureTextSize, Foreground = owner.brushes.Brush(DesignToken.Ink2), TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, DesignMetrics.Spacing.Md, 0, DesignMetrics.Spacing.Md),
        };

        /// <summary>
        /// The board's columns (M/MightyStructureView.swift:426-530): one per step, <c>Spacing.Lg</c> apart, <paramref name="width"/>
        /// wide and the page's height, each its header and, <c>Spacing.Sm</c> under it, its cards <c>Spacing.Md</c> apart in a scroll
        /// view of their own, ending <c>Spacing.Md</c> under the last. The header stays put while the cards move. Every report of where
        /// a column stands decides whether it follows its end (<see cref="MightyStructureFollow.ColumnWatch"/>), and a move of the
        /// user's is judged the moment the column knows where it goes, not when it reports the move (M/MightyStructurePanning.swift:395-416).
        /// </summary>
        private StackPanel BuildStructureKanban(Board board, double width)
        {
            var kanban = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DesignMetrics.Spacing.Lg };
            AutomationProperties.SetAutomationId(kanban, "mighty-structure-board-" + board.Id);
            foreach (var column in board.Columns)
            {
                var header = new Border(); var cards = new StackPanel { Spacing = DesignMetrics.Spacing.Md, Padding = new Thickness(0, 0, 0, DesignMetrics.Spacing.Md) };
                var scroll = new ScrollViewer
                {
                    Content = cards, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    VerticalScrollMode = ScrollMode.Auto, HorizontalScrollMode = ScrollMode.Disabled,
                };
                AutomationProperties.SetAutomationId(scroll, "mighty-structure-column-scroll-" + column.Id);
                var view = new Grid { RowSpacing = DesignMetrics.Spacing.Sm, Width = width };
                view.RowDefinitions.Add(new() { Height = GridLength.Auto }); view.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
                Grid.SetRow(scroll, 1); view.Children.Add(header); view.Children.Add(scroll);
                AutomationProperties.SetAutomationId(view, "mighty-structure-column-" + column.Id);
                var place = structureColumns[column.Id] = new(column.Id, view, header, scroll, cards);
                // Under the cards, behind the column's scroll view: its state's wash (DrawMightyStructure colours it).
                Grid.SetRow(place.Wash, 1); view.Children.Insert(1, place.Wash);
                AutomationProperties.SetAccessibilityView(place.Wash, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
                scroll.ViewChanging += (_, args) =>
                {
                    var to = args.FinalView.VerticalOffset;
                    if (StructureUserMoving(place) && Math.Abs(to - place.JudgedY) > 0.5) structureFollow.UserScrolled(place.Id, to - place.JudgedY);
                    place.JudgedY = to;
                };
                scroll.ViewChanged += (_, args) =>
                {
                    // A column moves in steps only under the user's wheel, keys, finger or scroll bar: the view's own scrolls land at once.
                    if (args.IsIntermediate) place.Scrolling = true;
                    var user = StructureUserMoving(place);
                    if (!args.IsIntermediate) place.Scrolling = false;
                    ObserveStructureColumn(place, user);
                };
                // A new height (a taller composer) moves no card, yet may hide the column's end.
                scroll.SizeChanged += (_, args) => { if (args.NewSize.Height != args.PreviousSize.Height) ObserveStructureColumn(place, false); };
                cards.SizeChanged += (_, _) => ObserveStructureColumn(place, false);
                view.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler((_, args) => OnStructureColumnWheel(place, args)), true);
                kanban.Children.Add(view);
            }
            return kanban;
        }

        /// <summary>
        /// A column's header (M/MightyStructureView.swift:547-563): its step as the pipeline's chip in its state's colours
        /// (<see cref="StructureChip"/>, cut with an ellipsis when the column is narrow), its count (10 semibold <c>ink2</c> on a
        /// <c>track</c> capsule) and, but for the last column, a 2pt line on across the gap to the next column's chip, <c>done</c> once
        /// this step is done and <c>track</c> until then, so the chips read as one pipeline from column to column.
        /// </summary>
        private PaneHeaderLine StructureColumnHeader(MightyStructure.BoardColumn column, bool last)
        {
            var b = owner.brushes;
            var head = new PaneHeaderLine { Spacing = DesignMetrics.Spacing.Sm, Height = StructureChipHeight };
            head.Add(StructureChip(StructureStepTitle(column.Step), column.Step.Step, MightyStructure.Tone.OfColumn(column)), PaneHeaderLine.Role.Shrink);
            head.Add(new Border
            {
                Child = StructureWords(column.Count.ToString(CultureInfo.InvariantCulture), DesignMetrics.Type.Small, b.Brush(DesignToken.Ink2), FontWeights.SemiBold), Background = b.Brush(DesignToken.Track),
                CornerRadius = new CornerRadius(StructureChipHeight / 2), Padding = new Thickness(DesignMetrics.Spacing.Sm, 0, DesignMetrics.Spacing.Sm, 0), VerticalAlignment = VerticalAlignment.Center,
            }, PaneHeaderLine.Role.Fixed);
            if (!last)
            {
                var line = new Border
                {
                    Height = DesignMetrics.Stroke.Active, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false, Margin = new Thickness(0, 0, -DesignMetrics.Spacing.Lg, 0),
                    Background = b.Brush(column.Step.Step == MightyStructure.Step.Done ? DesignToken.Done : DesignToken.Track),
                };
                AutomationProperties.SetAutomationId(line, "mighty-structure-connector-" + column.Id);
                head.Add(line, PaneHeaderLine.Role.Fill);
            }
            AutomationProperties.SetAccessibilityView(head, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
            return head;
        }

        // ── follow ────────────────────────────────────────────────────────────

        private static double StructureNow => Environment.TickCount64 / 1000.0;

        /// <summary>
        /// The column is moving under the user's hand (M/MightyStructureView.swift:42-56): a finger or its scroll bar moving it in
        /// steps, as the system's scroll; or, when the current or last drag, wheel or key moves this column, a scroll bar held, a
        /// scrolling key whose scroll the column has yet to report, a drag or the moment after one, or a wheel step still settling
        /// (<see cref="MightyStructureWheel.UserMoving"/>).
        /// </summary>
        private bool StructureUserMoving(StructureColumn place)
        {
            var now = StructureNow;
            return place.Scrolling || structureMovingColumn == place.Id
                && MightyStructureWheel.UserMoving(structureBarHeld || now - structureInputAt < MightyStructurePan.ReleaseGrace, structurePanning, structurePanReleasedAt, structureWheel, now);
        }

        /// <summary>The column is the drawn board's while the view shows.</summary>
        private bool StructureColumnShown(StructureColumn place) =>
            QueuePaneAlive && ShownStructureScroll is not null && structureColumns.TryGetValue(place.Id, out var drawn) && ReferenceEquals(drawn, place);

        /// <summary>The column a press, a wheel or a key at <paramref name="source"/> belongs to: its header, its scroll bar or one of its cards.</summary>
        private StructureColumn? StructureColumnOf(object? source) =>
            source is UIElement element ? structureColumns.Values.FirstOrDefault(column => StructureInside(element, column.View)) : null;

        /// <summary>
        /// Reports where a column stands, in its own top-down coordinates (M/MightyStructureView.swift:482-490): its cards' frame,
        /// its height and its last card's bottom (the column's newest) in one report, so the follow rule never judges one against a
        /// stale other. An older board follows nothing.
        /// </summary>
        private void ObserveStructureColumn(StructureColumn place, bool userScrolling)
        {
            if (structureBoardId is not { } board || place.Scroll is not { ViewportHeight: > 0 } scroll || !place.Cards.IsLoaded || !StructureColumnShown(place)) return;
            var cards = place.Cards; var origin = -cards.TransformToVisual(scroll).TransformPoint(new Point()).Y;
            place.JudgedY = scroll.VerticalOffset;
            var page = new MightyStructureFollow.Page(origin, scroll.ViewportHeight, cards.ActualHeight, place.Id, cards.ActualHeight - cards.Padding.Bottom);
            ScrollStructureColumn(place, structureFollow.Observe(board, structureOnNewest, place.Id, page, userScrolling || StructureUserMoving(place)));
        }

        /// <summary>
        /// A column appeared, drawn in a new frame or shown again (M/MightyStructureView.swift:491, 935-955): a following column shows
        /// its end. Any other column of the newest board opens where its reader left it in a frame built again for the same board
        /// (<paramref name="kept"/>), else at its end, as the Mac's does, and is judged by its first report; an older board's opens
        /// at its top.
        /// </summary>
        private void AppearStructureColumn(string board, StructureColumn place, double? kept)
        {
            var target = structureFollow.Appeared(board, structureOnNewest, place.Id);
            if (target is not null || structureOnNewest && kept is null) ScrollStructureColumn(place, MightyStructureFollow.Scroll.Bottom);
            else PlaceStructureColumn(place, kept ?? 0);
        }

        /// <summary>
        /// A column at its end, after the current layout pass: a report arrives while the column is being laid out
        /// (M/MightyStructureView.swift:509-530). A column's newest card is its last, so either target of the follow rule is its end.
        /// Only the column moves, never the page around it.
        /// </summary>
        private void ScrollStructureColumn(StructureColumn place, MightyStructureFollow.Scroll? target)
        {
            if (target is null) return;
            Container.DispatcherQueue.TryEnqueue(() =>
            {
                // A drag, a wheel step or a scroll of the user's in the column is theirs: nothing scrolls under it, even a scroll asked for just before it began.
                if (StructureUserMoving(place) || !StructureColumnShown(place)) return;
                // Nor once the column stopped following since: a text opened in it, or the user's move, after the report that asked for this scroll (M/MightyStructureView.swift:513-515).
                if (!structureFollow.Following(place.Id)) return;
                place.Scroll.UpdateLayout(); place.Scroll.ChangeView(null, place.Scroll.ScrollableHeight, null, true);
            });
        }

        /// <summary>A column at <paramref name="y"/> after the current layout pass: an older board's at its top, or where its reader left it.</summary>
        private void PlaceStructureColumn(StructureColumn place, double y) => Container.DispatcherQueue.TryEnqueue(() =>
        {
            if (!StructureColumnShown(place)) return;
            place.Scroll.UpdateLayout(); place.Scroll.ChangeView(null, Math.Min(y, place.Scroll.ScrollableHeight), null, true);
        });

        /// <summary>The page back at its first column, after the current layout pass (M/MightyStructureView.swift:421-424).</summary>
        private void ScrollStructureStart() => Container.DispatcherQueue.TryEnqueue(() =>
        {
            if (!QueuePaneAlive || ShownStructureScroll is not { } scroll) return;
            scroll.UpdateLayout(); scroll.ChangeView(0, null, null, true);
        });

        /// <summary>
        /// A scrolling key is the user's scroll of the column it moves before the column reports it, and with Windows' animations off
        /// it moves the column in one change, never in steps: noted when the column can still move that way (-1 or 1).
        /// </summary>
        private void NoteStructureInput(StructureColumn? place, int down)
        {
            if (place?.Scroll is not { } scroll || !(down < 0 && scroll.VerticalOffset > 0.5 || down > 0 && scroll.VerticalOffset < scroll.ScrollableHeight - 0.5)) return;
            structureMovingColumn = place.Id; structureInputAt = StructureNow;
        }

        /// <summary>
        /// A wheel on the page outside the columns (M/MightyStructurePanning.swift:340-393): the page's scroll viewer moves itself,
        /// sideways only, so the wheel is only noted as the user's: a Windows wheel message, a mouse's notch or a touchpad's,
        /// carries no gesture, and is a step. A positive delta shows what is above, or what is to the right.
        /// </summary>
        private void OnStructureWheel(object sender, PointerRoutedEventArgs args)
        {
            if (structureScroll is not { } scroll || args.KeyModifiers.HasFlag(VirtualKeyModifiers.Control) || StructureColumnOf(args.OriginalSource) is not null) return;
            var wheel = args.GetCurrentPoint(scroll).Properties; double delta = wheel.MouseWheelDelta;
            structureMovingColumn = null;
            structureWheel.Feed(MightyStructureWheel.Stage.Step, true, wheel.IsHorizontalMouseWheel ? (-delta, 0) : (0, delta), StructureNow);
        }

        /// <summary>
        /// A wheel over a column (M/MightyStructurePanning.swift:340-393), noted as the user's move of that column, or of the page
        /// alone for a sideways one. The column's scroll viewer moves itself up and down; a step it does not take goes no further,
        /// since the page, which moves sideways only, would turn it sideways, and one over the header moves the column by its delta.
        /// </summary>
        private void OnStructureColumnWheel(StructureColumn place, PointerRoutedEventArgs args)
        {
            if (args.KeyModifiers.HasFlag(VirtualKeyModifiers.Control)) return;
            var wheel = args.GetCurrentPoint(place.View).Properties; double delta = wheel.MouseWheelDelta;
            var sideways = wheel.IsHorizontalMouseWheel || args.KeyModifiers.HasFlag(VirtualKeyModifiers.Shift);
            structureMovingColumn = sideways ? null : place.Id;
            structureWheel.Feed(MightyStructureWheel.Stage.Step, true, wheel.IsHorizontalMouseWheel ? (-delta, 0) : (0, delta), StructureNow);
            if (sideways || args.Handled) return;
            args.Handled = true;
            if (StructureInside(args.OriginalSource as UIElement, place.Scroll)) return;
            var scroll = place.Scroll;
            var to = MightyStructureWheel.Origin((0, scroll.VerticalOffset), (0, delta), MightyStructureWheel.Axis.Vertical, (0, 0), (0, scroll.ScrollableHeight));
            scroll.ChangeView(null, to.Y, null);
        }

        private void OnStructureKeyDown(object sender, KeyRoutedEventArgs args)
        {
            // The answer keeps its arrow keys for its caret.
            if (StructureHit<RichEditBox>(args.OriginalSource) is not null) return;
            switch (args.Key)
            {
                case VirtualKey.Up or VirtualKey.PageUp or VirtualKey.Home: NoteStructureInput(StructureColumnOf(args.OriginalSource), -1); break;
                case VirtualKey.Down or VirtualKey.PageDown or VirtualKey.End: NoteStructureInput(StructureColumnOf(args.OriginalSource), 1); break;
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
        /// What a press lands on (M/MightyStructurePanning.swift:205-226): a text where its words are laid out, a text box or a
        /// scroll bar takes its own drag; anything else inside the page, a button and the empty part of a text's frame too, is the
        /// page until the press turns out to be a click. A column's own scroll view is the page's: only one inside a card is an inner one.
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
                if (element is ScrollViewer viewer && !structureColumns.Values.Any(column => ReferenceEquals(column.Scroll, viewer))) return MightyStructurePan.Target.InnerScroll;
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
            var column = StructureColumnOf(args.OriginalSource);
            structureMovingColumn = column?.Id;
            if (args.Pointer.PointerDeviceType != PointerDeviceType.Mouse) return;
            // The page moves sideways and the column pressed up and down (M/MightyStructurePanning.swift:319-337): either may make the press a pan.
            var page = StructureRange(scroll, MightyStructureWheel.Axis.Horizontal);
            var movable = MightyStructurePan.Scrollable(page.Minimum, page.Maximum);
            if (column is not null) { var range = StructureRange(column.Scroll, MightyStructureWheel.Axis.Vertical); movable |= MightyStructurePan.Scrollable(range.Minimum, range.Maximum); }
            if (MightyStructurePan.MayPan(StructurePanTarget(args), args.KeyModifiers != VirtualKeyModifiers.None, movable))
                structurePress = (point.Position, scroll.HorizontalOffset, column, column?.Scroll.VerticalOffset ?? 0);
        }

        /// <summary>The origins a scroll view of the board may take (M/MightyStructurePanning.swift:263-270): the page sideways only, a column up and down only.</summary>
        private static ((double X, double Y) Minimum, (double X, double Y) Maximum) StructureRange(ScrollViewer scroll, MightyStructureWheel.Axis axis) =>
            MightyStructurePan.Confined((0, 0), (scroll.ScrollableWidth, scroll.ScrollableHeight), (scroll.HorizontalOffset, scroll.VerticalOffset), axis);

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
            // The grabbed point stays under the pointer, the page following it sideways and the column pressed up and down (M/MightyStructurePanning.swift:416-431).
            var page = StructureRange(scroll, MightyStructureWheel.Axis.Horizontal);
            scroll.ChangeView(MightyStructurePan.Origin((press.X, scroll.VerticalOffset), start, cursor, page.Minimum, page.Maximum).X, null, null, true);
            if (press.Column is { } column && StructureColumnShown(column))
            {
                var range = StructureRange(column.Scroll, MightyStructureWheel.Axis.Vertical);
                column.Scroll.ChangeView(null, MightyStructurePan.Origin((column.Scroll.HorizontalOffset, press.Y), start, cursor, range.Minimum, range.Maximum).Y, null, true);
            }
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
        private (FrameworkElement View, bool Built) StructureCardView(MightyStructure.Card card, double width, string heading, string language, IReadOnlyList<ModelOption>? catalog, DesignTone? tint)
        {
            var item = card.Item; var textWidth = width - 2 * DesignMetrics.Inset.GraphBlockBodyH;
            static bool Folds(MightyStructure.Excerpt? text, int lines, double size, double width) => text?.Folds(lines, MightyStructure.CharactersPerLine(width, size)) == true;
            bool Open(string part, string id) => structureExpanded.Contains(part + ":" + id);
            var requestFolds = Folds(item.Request, MightyStructure.RequestLines, StructureTextSize, textWidth);
            // A free board's 요청 card holds the text beside its reveal button (M/MightyStructureView.swift:592-594).
            var requestCardFolds = Folds(item.Request, MightyStructure.RequestLines, StructureTextSize, textWidth - DesignMetrics.Layout.HitTarget - DesignMetrics.Spacing.Sm);
            var messageFolds = Folds(item.Message, MightyStructure.MessageLines, StructureTextSize, textWidth);
            var activity = string.Join('\n', item.Activity);
            object key; Func<FrameworkElement> build;
            switch (card.Kind)
            {
                case MightyStructure.CardKind.Request:
                    key = (heading, item.Request, item.Input, item.NodeId, Open("request", item.RunId), requestCardFolds, language, tint);
                    build = () => StructureRequestCard(item, heading, requestCardFolds, tint);
                    break;
                case MightyStructure.CardKind.Execution:
                    key = (item.Ordinal, item.Status, item.Tally, item.Message, activity, item.Finished, Open("message", item.RunId), messageFolds, item.ExecutionNodeId, item.NodeId, language, structureMotion, tint);
                    build = () => StructureExecution(item, messageFolds, card.Id, tint);
                    break;
                case MightyStructure.CardKind.Result:
                    key = (item.Status, item.Finished, item.Answer, Open("answer", item.RunId), item.ExecutionNodeId, item.NodeId, language, tint);
                    build = () => StructureResult(item, tint);
                    break;
                case MightyStructure.CardKind.Summary:
                    var line = MightyStructure.Fold.ResultLine(item);
                    key = ((heading, item.Request, item.Input, item.NodeId, Open("request", item.RunId), requestFolds, language), (item.Ordinal, item.Status, item.Tally, item.Message, activity, item.Finished, Open("message", item.RunId), messageFolds), (item.ExecutionNodeId, line, structureMotion, tint));
                    build = () => StructureSummary(item, heading, requestFolds, messageFolds, line, card.Id, tint);
                    break;
                default:
                    var node = card.Node!; var open = Open("node", node.NodeId); var nodeWidth = MightyStructure.NodeTextWidth(width, node.Depth);
                    var folds = Folds(node.Prompt, MightyStructure.PromptLines, StructurePromptSize, nodeWidth) || Folds(node.Output, MightyStructure.OutputLines, StructureOutputSize, nodeWidth) || node.Activity.Count > 1;
                    var model = node.Model is { } named ? ModelUsageFormat.ShortName(named, catalog, versioned: true) : null;
                    key = (node, card.Parent, open, folds, model, language, structureMotion, tint);
                    build = () => StructureNodeCard(node, card.Parent, open, folds, model, card.Id, tint);
                    break;
            }
            if (structureCards.TryGetValue(card.Id, out var part))
            {
                if (Equals(part.Key, key)) return (part.View, false);
                if (StructureHeld(part.View)) { structureDeferred = true; return (part.View, false); }
            }
            DropStructureMarks(card.Id);
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

        /// <summary>
        /// A free board's request (M/MightyStructureView.swift:586-603): the column says 요청 and the bar above which one, so the card
        /// is what was asked beside its reveal button; "Request N · Claude" (12 bold) only when there is no text.
        /// </summary>
        private Border StructureRequestCard(MightyStructure.Item item, string heading, bool folds, DesignTone? tint)
        {
            var request = new Grid { ColumnSpacing = DesignMetrics.Spacing.Sm };
            request.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); request.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            request.Children.Add(item.Request is { } text
                ? StructureText(text, "request:" + item.RunId, MightyStructure.RequestLines, StructureTextSize, folds, Locale.Get("graph.structure.column.request"))
                : StructureWords(heading, StructureTextSize, owner.brushes.Brush(DesignToken.Ink), FontWeights.Bold));
            var reveal = StructureRevealButton(item.NodeId, item.NodeId); reveal.VerticalAlignment = VerticalAlignment.Top; Grid.SetColumn(reveal, 1); request.Children.Add(reveal);
            var card = StructureSurface(request, tint);
            AutomationProperties.SetAutomationId(card, "mighty-structure-request-" + item.RunId);
            AutomationProperties.SetName(card, Locale.Get("graph.structure.contentAccessibility", new Dictionary<string, string> { ["name"] = heading, ["text"] = item.Input }));
            return card;
        }

        /// <summary>
        /// The run part's header line: the gears and "Run · Request N" (11 bold), the diagram's activity mark while the main agent
        /// runs (M/MightyStructureView.swift:583, 619), its status pill and its tally (11 semibold <c>ink2</c>), and its reveal button.
        /// </summary>
        private Grid StructureExecutionHeader(MightyStructure.Item item, string marks, bool titled)
        {
            var b = owner.brushes;
            var lead = new Grid { ColumnSpacing = DesignMetrics.Spacing.Sm, VerticalAlignment = VerticalAlignment.Center };
            void Lead(FrameworkElement part, GridLength size)
            {
                Grid.SetColumn(part, lead.ColumnDefinitions.Count); lead.ColumnDefinitions.Add(new() { Width = size }); lead.Children.Add(part);
            }
            // A free board's 실행 card needs no title: its column says 실행 and the bar which request (M/MightyStructureView.swift:575-576).
            if (titled)
            {
                var name = Locale.Get("graph.block.executionOf", new Dictionary<string, string> { ["n"] = item.Ordinal.ToString(CultureInfo.InvariantCulture) });
                // The Mac's gearshape.2 (M/MightyGraphView.swift:577).
                var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = DesignMetrics.Spacing.Xs, VerticalAlignment = VerticalAlignment.Center };
                label.Children.Add(MightySymbols.Create("gearshape.2", StructureOutputSize, b.Brush(DesignToken.Ink))); label.Children.Add(StructureWords(name, StructureOutputSize, b.Brush(DesignToken.Ink), FontWeights.Bold));
                Lead(label, GridLength.Auto);
            }
            if (StructureIndicator(item.Status, marks) is { } running) Lead(running, GridLength.Auto);
            Lead(StatusPill(MightyGraphBlockModel.StateLabel(item.Status), StatusGlyph.Tone(item.Status), 18), GridLength.Auto);
            Lead(StructureTally(item), new GridLength(1, GridUnitType.Star));
            return StructureHeader(lead, StructureRevealButton(item.ExecutionNodeId, item.NodeId));
        }

        /// <summary>The main block's latest reply, or that it is waiting for one, and its last tool calls (M/MightyStructureView.swift:635-650).</summary>
        private void StructureProgress(Panel body, MightyStructure.Item item, bool folds)
        {
            if (item.Message is { } message) body.Children.Add(StructureText(message, "message:" + item.RunId, MightyStructure.MessageLines, StructureTextSize, folds, Locale.Get("graph.structure.latest")));
            else if (!item.Finished && item.Activity.Count == 0) body.Children.Add(new TextBlock { Text = Locale.Get("graph.block.waitingResponse"), TextSize = StructureTextSize, Foreground = owner.brushes.Brush(DesignToken.Ink2), TextWrapping = TextWrapping.Wrap });
            if (item.Activity.Count == 0) return;
            var calls = new StackPanel { Spacing = DesignMetrics.Spacing.Xxs };
            foreach (var line in item.Activity) calls.Children.Add(StructureActivityRow(line));
            AutomationProperties.SetName(calls, Locale.Get("graph.structure.activity"));
            body.Children.Add(calls);
        }

        /// <summary>
        /// A free board's 실행 card (M/MightyStructureView.swift:610-631): the main agent's status, tally, latest reply and tool calls,
        /// as the diagram's run block holds them, in the run block's edge while it runs or waits.
        /// </summary>
        private FrameworkElement StructureExecution(MightyStructure.Item item, bool folds, string marks, DesignTone? tint)
        {
            var body = new StackPanel { Spacing = DesignMetrics.Spacing.Sm };
            body.Children.Add(StructureExecutionHeader(item, marks, titled: false));
            StructureProgress(body, item, folds);
            var card = StructureSurface(body, tint);
            AutomationProperties.SetAutomationId(card, "mighty-structure-execution-" + item.RunId);
            AutomationProperties.SetName(card, Locale.Get("graph.block.executionOf", new Dictionary<string, string> { ["n"] = item.Ordinal.ToString(CultureInfo.InvariantCulture) }));
            return StructureOutlined(card, item.Status, StructureCardCorner, marks);
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
        private Border StructureResult(MightyStructure.Item item, DesignTone? tint)
        {
            structureAnswers.Remove(item.RunId);
            var reveal = StructureRevealButton(item.Finished ? MightyGraphBlockSize.NodeId(item.RunId, "result") : item.ExecutionNodeId, item.NodeId);
            FrameworkElement outcome = item.Answer is { } answer ? StructureAnswer(item, answer)
                : new TextBlock { Text = item.Finished ? MightyGraphBlockModel.StateLabel(item.Status) : Locale.Get("graph.structure.resultPending"), TextSize = StructureTextSize, Foreground = owner.brushes.Brush(DesignToken.Ink2), TextWrapping = TextWrapping.Wrap };
            FrameworkElement body;
            // The column already says 결과: only an error or a stop gets a title, in its colour (M/MightyStructureView.swift:611-623).
            if (item.Status is "error" or "stopped")
            {
                var titled = new StackPanel { Spacing = DesignMetrics.Spacing.Sm };
                titled.Children.Add(StructureHeader(StructureResultTitle(item), reveal)); titled.Children.Add(outcome);
                body = titled;
            }
            else
            {
                var plain = new Grid { ColumnSpacing = DesignMetrics.Spacing.Sm };
                plain.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); plain.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
                plain.Children.Add(outcome); reveal.VerticalAlignment = VerticalAlignment.Top; Grid.SetColumn(reveal, 1); plain.Children.Add(reveal);
                body = plain;
            }
            var card = StructureSurface(body, tint);
            AutomationProperties.SetAutomationId(card, "mighty-structure-result-" + item.RunId);
            return card;
        }

        /// <summary>
        /// A styled board's request (M/MightyStructureView.swift:564-604): "Request N · Claude" with its reveal button and what was
        /// asked; a rule; its 실행 part (the run's header line, latest reply and tool calls); and once it finished, how it ended on
        /// one line (11): the answer's first line in <c>ink2</c>, else the outcome in its ink, beside the result's reveal button. It
        /// wears the run block's edge while the run runs or waits (M/MightyStructureView.swift:603-604).
        /// </summary>
        private FrameworkElement StructureSummary(MightyStructure.Item item, string heading, bool requestFolds, bool messageFolds, string? line, string marks, DesignTone? tint)
        {
            var b = owner.brushes;
            var body = new StackPanel { Spacing = DesignMetrics.Spacing.Sm };
            body.Children.Add(StructureHeader(StructureWords(heading, StructureTextSize, b.Brush(DesignToken.Ink), FontWeights.Bold), StructureRevealButton(item.NodeId, item.NodeId)));
            if (item.Request is { } text) body.Children.Add(StructureText(text, "request:" + item.RunId, MightyStructure.RequestLines, StructureTextSize, requestFolds, Locale.Get("graph.structure.column.request")));
            var rule = new Border { Height = DesignMetrics.Stroke.Line, Background = b.Brush(DesignToken.Line) };
            AutomationProperties.SetAccessibilityView(rule, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
            body.Children.Add(rule);
            body.Children.Add(StructureExecutionHeader(item, marks, titled: true));
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
            var card = StructureSurface(body, tint);
            AutomationProperties.SetAutomationId(card, "mighty-structure-request-" + item.RunId);
            AutomationProperties.SetName(card, Locale.Get("graph.structure.contentAccessibility", new Dictionary<string, string> { ["name"] = heading, ["text"] = item.Input }));
            return StructureOutlined(card, item.Status, StructureCardCorner, marks);
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

        /// <summary>
        /// The app's own theme toggle: every answer on the page in the new colours, as the plan documents are (<see cref="RethemePlanViews"/>);
        /// a new text setting (<paramref name="always"/>) draws every answer again in its sizes and fonts.
        /// </summary>
        internal void RethemeStructureAnswers(bool always = false)
        {
            var light = !owner.DarkTheme;
            foreach (var (run, answer) in structureAnswers.Where(pair => always || pair.Value.Light != light).ToArray())
            {
                SetMarkdownRtf(answer.View, TranscriptRtf.RenderMarkdown(answer.Source, light));
                structureAnswers[run] = answer with { Light = light };
            }
        }

        /// <summary>
        /// A sub-agent or task as a small card (M/MightyStructureView.swift:675-739): radius 8 on <c>card</c> with a <c>line</c>
        /// edge and its status as a tint and a <see cref="DesignMetrics.Pipeline.CardBarWidth"/> bar in its mark colour; its kind's symbol (9) in its ink, its title (11 semibold, cut last)
        /// and "kind · model" (10 <c>ink2</c>) on one line; the block it runs under when it is nested (10 <c>ink2</c>); then what it
        /// was asked (10.5 <c>ink2</c>, two lines), its latest output (11, six lines) and its last tool call. Opened, it shows them
        /// whole and its last few tool calls. Each level of the tree stands <c>Spacing.Lg</c> further in, up to
        /// <see cref="MightyStructure.MaximumIndent"/>; <see cref="MightyStructure.NodeTextWidth"/> takes the indent and the padding off.
        /// While it runs or waits, the diagram's activity mark stands before its reveal button and the card wears the block's edge
        /// (M/MightyStructureView.swift:708, 741).
        /// </summary>
        private FrameworkElement StructureNodeCard(MightyStructure.Node node, string? parent, bool open, bool folds, string? model, string marks, DesignTone? tint)
        {
            var b = owner.brushes; var task = node.Kind == "task"; var steer = node.Kind == "steer"; var key = "node:" + node.NodeId;
            var head = new PaneHeaderLine { Spacing = DesignMetrics.Spacing.Sm };
            head.Add(MightySymbols.Create(BlockSymbol(node.Kind), 9, b.Brush(task ? DesignToken.TaskText : steer ? DesignToken.SteerText : DesignToken.AgentText), MightySymbols.Weight.Semibold), PaneHeaderLine.Role.Fixed);
            head.Add(StructureWords(node.Title, StructureOutputSize, b.Brush(DesignToken.Ink), FontWeights.SemiBold), PaneHeaderLine.Role.Shrink);
            // A task notice's title already names its kind (MightyStructure.Kept keeps no other steer).
            head.Add(StructureWords(string.Join(" · ", new[] { steer ? null : MightyTimeline.KindLabel(node.Kind), model }.Where(p => !string.IsNullOrEmpty(p))), DesignMetrics.Type.Small, b.Brush(DesignToken.Ink2)), PaneHeaderLine.Role.Fill);
            if (StructureIndicator(node.Status, marks) is { } running) head.Add(running, PaneHeaderLine.Role.Fixed);
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
            // The sub-agent card's own bar is the pipeline's bar (StructureTinted): one bar, with the card's tint under it; a sub-agent
            // still to come keeps its quiet idle bar without a tint, as the web shell's does.
            var face = StructureTinted(body, tint ?? DesignTone.Idle, StructureNodeCorner);
            var card = new Border
            {
                Child = face, CornerRadius = new CornerRadius(StructureNodeCorner), Background = b.Brush(DesignToken.Card), BorderThickness = new Thickness(BlockEdge), BorderBrush = b.Brush(DesignToken.Line),
                Margin = new Thickness(Math.Min(node.Depth, MightyStructure.MaximumIndent) * DesignMetrics.Spacing.Lg, 0, 0, 0),
            };
            AutomationProperties.SetAutomationId(card, "mighty-structure-node-" + node.NodeId);
            AutomationProperties.SetName(card, Locale.Get("graph.structure.nodeAccessibility", new Dictionary<string, string> { ["title"] = node.Title, ["status"] = MightyGraphBlockModel.StateLabel(node.Status) }));
            return StructureOutlined(card, node.Status, StructureNodeCorner, marks);
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
            if (line.Kind is "command" or "read" or "edit") words.TextFont = TextArea.Code;
            Grid.SetColumn(words, 1); row.Children.Add(words);
            ToolTipService.SetToolTip(row, line.Text);
            return row;
        }

        // ── parts ─────────────────────────────────────────────────────────────

        /// <summary>
        /// A card of the structure (M/MightyStructureView.swift:824-830): the diagram's block card (<c>card</c>, a 1pt
        /// <c>line</c> edge) at radius 10, its content <c>Inset.GraphBlockBodyH</c> by <c>Spacing.Sm</c> in from the card's edge.
        /// </summary>
        private Border StructureSurface(UIElement content, DesignTone? tint)
        {
            var b = owner.brushes;
            var padded = new Border
            {
                Child = content,
                Padding = new Thickness(DesignMetrics.Inset.GraphBlockBodyH - BlockEdge, DesignMetrics.Spacing.Sm - BlockEdge, DesignMetrics.Inset.GraphBlockBodyH - BlockEdge, DesignMetrics.Spacing.Sm - BlockEdge),
            };
            return new Border
            {
                Child = StructureTinted(padded, tint, StructureCardCorner), Background = b.Brush(DesignToken.Card), CornerRadius = new CornerRadius(StructureCardCorner),
                BorderThickness = new Thickness(BlockEdge), BorderBrush = b.Brush(DesignToken.Line),
            };
        }

        /// <summary>
        /// A 파이프라인 card's state (M/MightyStructureView.swift MightyStructureCardTint): the tone's soft tint at
        /// <see cref="DesignMetrics.Pipeline.CardTintOpacity"/> under the content and a <see cref="DesignMetrics.Pipeline.CardBarWidth"/>
        /// bar in its mark (<see cref="StructureMark"/>) down the leading edge, inside the card's edge with corners following the
        /// card's own; the content alone for a step still to come, and the idle bar without a tint for <see cref="DesignTone.Idle"/>
        /// (a sub-agent still to come). The outline and the pills stay as they are.
        /// </summary>
        private UIElement StructureTinted(UIElement content, DesignTone? tint, double corner)
        {
            if (tint is not { } tone) return content;
            var b = owner.brushes; var inner = corner - BlockEdge;
            var face = new Grid();
            // DesignPalette.SoftToken has no idle tint (it would read as the stop's), so an idle card gets the bar alone.
            if (tone != DesignTone.Idle)
            {
                var wash = new Border { Background = b.Brush(DesignPalette.SoftToken(tone), DesignMetrics.Pipeline.CardTintOpacity), CornerRadius = new CornerRadius(inner), IsHitTestVisible = false };
                AutomationProperties.SetAccessibilityView(wash, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
                face.Children.Add(wash);
            }
            var bar = new Border { Width = DesignMetrics.Pipeline.CardBarWidth, HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(inner, 0, 0, inner), Background = b.Brush(StructureMark(tone)), IsHitTestVisible = false };
            AutomationProperties.SetAccessibilityView(bar, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
            face.Children.Add(bar); face.Children.Add(content);
            return face;
        }

        /// <summary>The diagram's activity mark for a status (<see cref="BuildIndicator"/>), its capsules waved under <paramref name="marks"/>.</summary>
        private FrameworkElement? StructureIndicator(string status, string marks) =>
            BuildIndicator(MightyGraphViewModel.BlockIndicator(status, structureMotion), null, marks, structureActivitySets);

        /// <summary>
        /// A card in the diagram's edge while it runs or waits (macOS MightyGraphActivityOutline, <see cref="AddActivityOutline"/>):
        /// the marching 2pt <c>run</c> line over the <c>runSoft</c> halo, still and solid with Windows animations off, or the still
        /// <c>wait</c> line, at the card's own <paramref name="corner"/>. The indent stays outside the edge. The march is fitted to the
        /// card each time it grows (<see cref="StartStructureMarch"/>); any other card is returned as it is.
        /// </summary>
        private FrameworkElement StructureOutlined(Border card, string status, double corner, string marks)
        {
            var outline = MightyGraphActivity.Outline(status, structureMotion);
            if (outline == MightyGraphActivity.None) return card;
            var frame = new Grid { Margin = card.Margin }; card.Margin = default;
            if (MightyGraphActivity.HaloToken(outline) is { } halo)
            {
                var glow = ActivityHalo(halo, corner); glow.Margin = new Thickness(-2 * MightyGraphActivity.LineWidth);
                frame.Children.Add(glow);
            }
            frame.Children.Add(card);
            var (line, march) = ActivityLine(outline, corner);
            frame.Children.Add(line);
            structureOutlines[marks] = (outline, line, march);
            if (march is null) return frame;
            line.SizeChanged += (_, args) => { march.Stop(); FitActivityMarch(line, march, args.NewSize.Width, args.NewSize.Height, corner); StartStructureMarch(line, march); };
            line.Loaded += (_, _) => StartStructureMarch(line, march);
            // A card moved to another column is loaded again before the old place unloads it.
            line.Unloaded += (_, _) => { if (!line.IsLoaded) march.Stop(); };
            return frame;
        }

        /// <summary>
        /// Walks a card's dashes from where the wall clock has them, so a refit or a return to the view moves on without a jump (the
        /// Mac's loops are clock-aligned, M/LoopingLayerView.swift); only while the view shows and the line is laid out.
        /// </summary>
        private void StartStructureMarch(Rectangle line, Storyboard march)
        {
            march.Stop();
            if (!structureShown || !line.IsLoaded || line.ActualWidth <= 0) return;
            march.Begin(); march.Seek(MightyGraphActivity.Period * ActivityPhase());
        }

        /// <summary>A card's running marks go with it: its capsules leave the pane's timer and its march stops.</summary>
        private void DropStructureMarks(string marks)
        {
            structureActivitySets.Remove(marks);
            if (structureOutlines.Remove(marks, out var edge)) edge.March?.Stop();
        }

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
            Text = text, TextSize = size, Foreground = ink, FontWeight = weight ?? FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center,
        };

        /// <summary>
        /// A text shown in place (M/MightyStructureView.swift:832-840): wrapped to its column and selectable, at most
        /// <paramref name="lines"/> lines (0: all of it). Its frame ends with its words, so the card beside them still moves the page.
        /// </summary>
        private TextBlock StructureSelectable(string text, double size, DesignToken ink, int lines)
        {
            var words = new TextBlock
            {
                Text = text, TextSize = size, TextFont = TextArea.Transcript, Foreground = owner.brushes.Brush(ink), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, HorizontalAlignment = HorizontalAlignment.Left,
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
            var toggle = HeaderButton(new TextBlock { Text = label, TextSize = StructureOutputSize, FontWeight = FontWeights.Bold }, owner.brushes.Brush(DesignToken.Accent));
            toggle.HorizontalAlignment = HorizontalAlignment.Left;
            AutomationProperties.SetAutomationId(toggle, "mighty-structure-more-" + key); AutomationProperties.SetName(toggle, label);
            AutomationProperties.SetItemStatus(toggle, Locale.Get(open ? "graph.structure.expanded" : "graph.structure.collapsed"));
            toggle.Click += (_, _) => { if (!structurePanned) ToggleStructureText(key, toggle); };
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

        // An opened or folded text is being read: its column follows nothing until the reader comes back to its end (M/MightyStructureView.swift:500-506).
        private void ToggleStructureText(string key, Button toggle)
        {
            if (!structureExpanded.Add(key)) structureExpanded.Remove(key);
            if (StructureColumnOf(toggle) is { } column) structureFollow.Stop(column.Id);
            RedrawStructure("mighty-structure-more-" + key);
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
            var catalog = PaneRuntime(pane.Provider)?.ModelCatalog?.Models;
            var key = string.Join('\n', new[] { Locale.LanguagePreference, activeStyle?.Id, activeStyle?.Hash }.Concat((styles ?? []).Where(s => s.Runnable).SelectMany(s => new[] { s.Id, s.Hash }))
                .Concat((catalog ?? []).SelectMany(model => new[] { model.Value, model.DisplayName, model.ResolvedModel })));
            return owner.mobileStructures.Structure(pane.Id, runs, style, phase, pane.PlanHistory, pending, key,
                styles is null ? null : input => StylePresentation.RequestPrefix(styles, input), model => ModelUsageFormat.ShortName(model, catalog, versioned: true));
        }
    }
}
