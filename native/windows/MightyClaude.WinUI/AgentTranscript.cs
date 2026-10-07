using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MightyClaude.Core;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace MightyClaude.WinUI;

/// <summary>One native document permits selection and Ctrl+C across every message.</summary>
internal sealed partial class AgentTranscript
{
    /// <summary>
    /// The Mac's transcript text view (M/AgentTranscriptView.swift:153-185): the 13pt body, 15pt in from each
    /// side. The 15pt above the first line and under the last belong to the document (TranscriptRtf writes
    /// them), so they scroll away with it as the Mac's text container inset does.
    /// </summary>
    internal RichEditBox View { get; } = new() { IsReadOnly = true, IsSpellCheckEnabled = false, IsTextPredictionEnabled = false, TextWrapping = TextWrapping.Wrap, FontSize = DesignMetrics.Type.Body, MinHeight = 80, BorderThickness = new(0), Background = new SolidColorBrush(Colors.Transparent), Padding = new(TranscriptRtf.Inset, 0, TranscriptRtf.Inset, 0) };
    private string rendered = "", plain = "";
    private bool selecting;
    internal bool IsSelecting => selecting;
    internal Action? SelectionEnded { get; set; }
    private (RunSession Session, bool Light)? deferred;
    private (RunSession Session, bool Light)? last;
    private AgentPictures? pictures;
    private string? workspaceRoot;
    /// <summary>The entries of this transcript whose drawing ran out of time (<see cref="TranscriptLook.TimedOut"/>).</summary>
    private readonly Dictionary<string, string> timedOut = [];
    internal Func<string, int?, Task>? OpenReference { get; set; }
    /// <summary>
    /// Concept D's conversation (M/SessionPaneView.swift:490-493 <c>cards: true</c>): the request as an ink
    /// bubble, a reply on a white card under its speaker, each tool call as a chip, code on the ink surface.
    /// Off, the transcript keeps the plain look of the Mighty blocks (M/AgentTranscriptView.swift:22-24).
    /// </summary>
    internal bool Cards { get; set; }
    /// <summary>The space above the first line and under the last, in Mac points (M/AgentTranscriptView.swift:372-376; 30 under a block whose corner handle sits over the text).</summary>
    internal double TopInset { get; set; } = TranscriptRtf.Inset;
    internal double BottomInset { get; set; } = TranscriptRtf.Inset;
    /// <summary>The body size in Mac points (13, M/AgentTranscriptFormat.swift:307); the timeline's answer is 12.5 (M/MightyGraphTimelineView.swift:234).</summary>
    internal double BodySize { get; set; } = DesignMetrics.Type.Body;
    /// <summary>
    /// Draws a reply as its bare words: no speaker line and no Markdown, each line as it was written. The
    /// timeline's result card shows its answer so (M/MightyGraphTimelineView.swift:232-237).
    /// </summary>
    internal bool PlainAnswer { get; set; }
    /// <summary>The window's shared brushes for the painted blocks; a transcript without them keeps its own set, recoloured with its theme.</summary>
    internal DesignBrushes? Brushes { get; set; }
    internal AgentTranscript()
    {
        AutomationProperties.SetName(View, Locale.Get("transcript.accessibility"));
        // The stock text box draws an accent line under itself while it holds the focus; a transcript is read, not typed in.
        View.Resources["TextControlBorderThemeThicknessFocused"] = new Thickness(0);
        InitializeTranscriptActions();
        backdrop.Children.Add(layer); layer.RenderTransform = scrolled;
        backdrop.SizeChanged += (_, args) => backdrop.Clip = new RectangleGeometry { Rect = new Rect(0, 0, args.NewSize.Width, args.NewSize.Height) };
        // A RichEditBox paints its theme foreground over the whole document when the theme changes or it
        // is shown again, wiping the RTF's colours; the last session is drawn again once it has done so.
        void Repaint() { if (last is not null) View.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => { rendered = ""; Redraw(); }); }
        View.ActualThemeChanged += (_, _) => Repaint();
        View.Loaded += (_, _) => { AttachBackdrop(); Repaint(); };
        // The bubble's margin and a table's columns follow the width, and every painted block follows the wrapped lines.
        // A box drawn while it was hidden comes back at the size it had, with no size event: its viewport tells.
        // A box first loaded inside a collapsed parent has no template yet: its first size is when the painted layer can go in.
        View.SizeChanged += (_, _) => { AttachBackdrop(); RemeasureBackdrop(); Reflow(); };
        View.EffectiveViewportChanged += (_, _) => Reflow();
        // A new width draws everything again, and with it gives an entry remembered as out of time another go: what
        // ran out may have been a stalled machine's time, and nothing else would ever forget it.
        settled.Tick += (_, _) => { settled.Stop(); if (Outgrown) { timedOut.Clear(); Redraw(); } };
        ScrollViewer.SetVerticalScrollBarVisibility(View, ScrollBarVisibility.Auto);
        View.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) => { if (e.GetCurrentPoint(View).Properties.IsLeftButtonPressed) selecting = true; }), true);
        void Finish(object sender, PointerRoutedEventArgs args) { selecting = false; if (deferred is { } value) { deferred = null; Update(value.Session, value.Light); } SelectionEnded?.Invoke(); }
        View.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(Finish), true);
        View.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(Finish), true);
        View.AddHandler(UIElement.TappedEvent, new TappedEventHandler(async (_, args) =>
        {
            if (View.Document.Selection.Length != 0) return;
            if (PositionAt(args.GetPosition(TextOrigin)) is not { } at) return; // A blank area beside a path is not a link.
            if (await InvokeTranscriptAction(at)) { args.Handled = true; return; }
            // A line break inside a paragraph ends the clicked line as a paragraph's end does.
            if (OpenReference is null || ReferencePreview.TextTargetAt(plain.Replace(TranscriptRtf.LineBreak, '\r'), at) is not { } target) return;
            args.Handled = true;
            await OpenReference(target.Path, target.Line);
        }), true);
    }
    /// <summary>
    /// The conversation as text, for Copy and the checks. The drawn marks (a tool's state square, a speaker's
    /// mark) are layout, not words (M/SelectableTextView.swift:124-132), and a table reads as tab-separated rows.
    /// </summary>
    internal string Text { get { View.Document.GetText(TextGetOptions.None, out var value); return Words(value, 0, value == plain ? marks : NoMarks); } }
    private static readonly HashSet<int> NoMarks = [];
    /// <summary>
    /// Pictures the agent showed are drawn where they appeared (macOS AgentTranscriptView):
    /// thumbnails come from <paramref name="images"/>, Markdown pictures resolve against
    /// <paramref name="root"/>, and a finished thumbnail redraws the last session.
    /// </summary>
    internal void Update(RunSession session, bool light, AgentPictures? images, string? root)
    {
        if (images is not null && !ReferenceEquals(images, pictures)) images.Listen(this);
        pictures = images; workspaceRoot = root;
        Update(session, light);
    }
    internal void Redraw() { if (last is { } value) Update(value.Session, value.Light); }
    /// <summary>Redraws the last session in the other theme; nothing when it already shows this one.</summary>
    internal void Retheme(bool light) { if (last is { } value && value.Light != light) Update(value.Session, light); }
    internal void Update(RunSession session, bool light)
    {
        last = (session, light);
        if (selecting) { deferred = (session, light); return; }
        own?.Apply(DesignTokens.Palette(!light));
        // The second pass only runs when the document's paragraphs are not the ones the renderer counted, so
        // its blocks cannot be placed: the plain look keeps every word readable without them.
        for (var plainLook = false; ; plainLook = true)
        {
            var nextActions = new Dictionary<string, TranscriptAction>();
            // A box that is not laid out now is drawn for the width it last had.
            var width = TextWidth; if (width >= 1) knownWidth = width; else width = knownWidth;
            var look = new TranscriptLook { Cards = Cards && !plainLook, Width = width, Scale = View.XamlRoot?.RasterizationScale ?? 1, TopInset = TopInset, BottomInset = BottomInset, BodySize = BodySize, PlainAnswer = PlainAnswer, References = OpenReference is not null && !Cards, TimedOut = timedOut };
            var next = TranscriptRtf.Render(session, light, pictures, workspaceRoot, expandedTools, nextActions, look); actions = nextActions;
            drawnWidth = width;
            if (next == rendered) { ArrangeBackdrop(); return; }
            var selection = View.Document.Selection; var start = selection.StartPosition; var end = selection.EndPosition;
            var scroll = Descendant<ScrollViewer>(View); var offset = scroll?.VerticalOffset ?? 0;
            var follows = scroll is null || scroll.ScrollableHeight - offset < 32;
            var previous = plain;
            // WinUI also blocks programmatic document writes while IsReadOnly is true.
            // Keep this synchronous so no user input can run before protection returns.
            var readOnly = View.IsReadOnly;
            try
            {
                View.IsReadOnly = false;
                // A streaming answer only changes the document's end: the entries before it stay as RichEdit
                // holds them and the rest is written over the old tail, in front of the document's own last mark.
                var kept = KeptEntries(next, look);
                if (kept > 0)
                {
                    var from = look.Entries[kept];
                    View.Document.GetRange(paragraphStarts[from.Paragraph], plain.Length - 1).SetText(TextSetOptions.FormatRtf, string.Concat(next.AsSpan(0, look.Head), next.AsSpan(from.Offset, next.Length - from.Offset - look.Foot), "}"));
                    View.Document.GetText(TextGetOptions.None, out plain);
                    // A tail that did not join paragraph for paragraph is written again with the whole document, from then on.
                    if (plain.Count(c => c == '\r') != look.Paragraphs + 1) { tailWrites = false; kept = 0; }
                }
                if (kept == 0) { View.Document.SetText(TextSetOptions.FormatRtf, next); View.Document.GetText(TextGetOptions.None, out plain); }
                // RichEdit keeps a last paragraph mark of its own under whatever it reads, a body line high: it is made one pixel.
                if (plain.Length > 0) View.Document.GetRange(plain.Length - 1, plain.Length).ParagraphFormat.SetLineSpacing(LineSpacingRule.Exactly, TranscriptRtf.LastMark * 0.75f);
            }
            finally { View.IsReadOnly = readOnly; }
            rendered = next;
            var placed = Place(look);
            var prefix = 0; while (prefix < previous.Length && prefix < plain.Length && previous[prefix] == plain[prefix]) prefix++;
            var suffix = 0; while (suffix < previous.Length - prefix && suffix < plain.Length - prefix && previous[^(suffix + 1)] == plain[^(suffix + 1)]) suffix++;
            int Position(int position) => Math.Clamp(position <= prefix ? position : position >= previous.Length - suffix ? position + plain.Length - previous.Length : prefix + Math.Min(position - prefix, plain.Length - prefix - suffix), 0, Math.Max(0, plain.Length - 1));
            selection.SetRange(Position(start), Position(end));
            View.DispatcherQueue.TryEnqueue(() => { var viewer = Descendant<ScrollViewer>(View); viewer?.ChangeView(null, follows && start == end ? viewer.ScrollableHeight : offset, null, true); });
            if (placed || !look.Cards) return;
        }
    }
    /// <summary>
    /// How many leading entries of <paramref name="next"/> the document already shows, byte for byte: an
    /// entry's RTF runs to the next entry's, and the last one also carries the document's foot, so it never
    /// counts. 0 when the head (the fonts and the theme's colours) differs or the document was not counted.
    /// </summary>
    private int KeptEntries(string next, TranscriptLook look)
    {
        if (!tailWrites || shown is not { } old || paragraphStarts.Count != old.Paragraphs + 2 || old.Head != look.Head || rendered.Length < look.Head || string.CompareOrdinal(rendered, 0, next, 0, look.Head) != 0) return 0;
        var kept = 0;
        while (kept + 1 < old.Entries.Count && kept + 1 < look.Entries.Count)
        {
            var entry = old.Entries[kept]; var length = old.Entries[kept + 1].Offset - entry.Offset;
            if (look.Entries[kept] != entry || look.Entries[kept + 1].Offset - entry.Offset != length || string.CompareOrdinal(rendered, entry.Offset, next, entry.Offset, length) != 0) break;
            kept++;
        }
        return kept;
    }
    internal static T? Descendant<T>(DependencyObject value) where T : DependencyObject
    {
        if (value is T found) return found;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(value); i++) if (Descendant<T>(VisualTreeHelper.GetChild(value, i)) is { } child) return child;
        return null;
    }

    // ── the painted blocks ─────────────────────────────────────────────────────
    // A RichEdit paragraph has no background, border or corner of its own, so what the Mac draws as text
    // blocks (M/AgentTranscriptFormat.swift:486-509 RoundedTextBlock, and its plain NSTextBlocks) is painted
    // here, under the text: a clipped layer set into the box's own template beneath its text view, one
    // rounded shape per block, placed from the laid-out lines and moved with the box's scroll offset.

    private readonly Canvas backdrop = new() { IsHitTestVisible = false };
    private readonly Canvas layer = new();
    private readonly TranslateTransform scrolled = new();
    private ScrollViewer? scroller;
    private DesignBrushes? own;
    private bool reflowing;
    private List<(TranscriptBlock Block, int Start, int End)> blocks = [];
    /// <summary>Each block's measured top and bottom in the document; null until it is first looked at.</summary>
    private (double Top, double Bottom)?[] measured = [];
    /// <summary>The blocks that sit in no other: they run down the document without overlapping, each followed by the ones inside it.</summary>
    private List<int> outer = [];
    /// <summary>The stretch of the document the painted shapes cover.</summary>
    private double paintedFrom = double.NaN, paintedTo;
    /// <summary>Where each paragraph of the document starts, and the look it was drawn with.</summary>
    private List<int> paragraphStarts = [0];
    private TranscriptLook? shown;
    private bool tailWrites = true;
    /// <summary>Where the drawn marks sit in the document: object characters that are not the agent's pictures.</summary>
    private HashSet<int> marks = [];

    /// <summary>The element the document's coordinates are measured from: the box's text view, which its scroll viewer moves.</summary>
    private UIElement TextOrigin => scroller?.Content as UIElement ?? View;
    /// <summary>The width the text wraps at.</summary>
    private double TextWidth => Math.Max(0, View.ActualWidth - View.Padding.Left - View.Padding.Right);
    /// <summary>The last width the box was laid out at, and the width the document on screen was drawn for.</summary>
    private double knownWidth, drawnWidth = double.NaN;

    /// <summary>
    /// How long a new width must stand before the document is drawn for it. A drawing writes the whole
    /// conversation, and a window being dragged wider or narrower has a new width at every step.
    /// </summary>
    private static readonly TimeSpan ReflowDelay = TimeSpan.FromMilliseconds(120);
    private readonly DispatcherTimer settled = new() { Interval = ReflowDelay };

    /// <summary>
    /// Draws the document again when the box is not as wide as the width it was drawn for: at once for the
    /// first width it is laid out at (until then it was written for a guess), and for a width that changes
    /// once it has stood for <see cref="ReflowDelay"/>. Meanwhile the lines wrap to the new width by themselves
    /// and the painted blocks follow them; the bubble's margin and a table's columns are what wait.
    /// </summary>
    private void Reflow()
    {
        settled.Stop();
        if (!Outgrown) return;
        if (drawnWidth >= 1) { settled.Start(); return; }
        if (reflowing) return;
        reflowing = true;
        View.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => { reflowing = false; Redraw(); });
    }
    /// <summary>Whether the box is laid out at another width than the one the document on screen was drawn for.</summary>
    private bool Outgrown => TextWidth is >= 1 and var width && last is not null && !(Math.Abs(width - drawnWidth) < 0.5);
    private DesignBrushes Paints
    {
        get
        {
            if (Brushes is { } shared) return shared;
            if (own is null) { own = new DesignBrushes(); own.Apply(DesignTokens.Palette(last is { Light: false } or null)); }
            return own;
        }
    }
    /// <summary>Every block of the document as it is painted, wherever the box is scrolled, for the checks.</summary>
    internal IReadOnlyList<(TranscriptBlockKind Kind, Brush? Fill, Brush? Edge, Thickness Line, CornerRadius Corners, double Height)> Painted()
    {
        var all = new List<(TranscriptBlockKind, Brush?, Brush?, Thickness, CornerRadius, double)>(); var probe = new Border();
        for (var index = 0; index < blocks.Count; index++)
        {
            Paint(probe, blocks[index].Block);
            var (top, bottom) = scroller is null ? (0, 0) : Measure(index);
            all.Add((blocks[index].Block.Kind, probe.Background, probe.BorderBrush, probe.BorderThickness, probe.CornerRadius, bottom - top));
        }
        return all;
    }

    private void AttachBackdrop()
    {
        var found = Descendant<ScrollViewer>(View);
        if (found is null || ReferenceEquals(found, scroller) || VisualTreeHelper.GetParent(found) is not Panel host) return;
        scroller = found;
        if (backdrop.Parent is Panel old) old.Children.Remove(backdrop);
        Grid.SetRow(backdrop, Grid.GetRow(found)); Grid.SetColumn(backdrop, Grid.GetColumn(found));
        Grid.SetRowSpan(backdrop, Grid.GetRowSpan(found)); Grid.SetColumnSpan(backdrop, Grid.GetColumnSpan(found));
        host.Children.Insert(host.Children.IndexOf(found), backdrop);
        // The text view is drawn again at each offset on the UI thread, so the layer keeps step with it from here.
        found.ViewChanging += (_, args) => ArrangeBackdrop(args.NextView.VerticalOffset);
        found.ViewChanged += (_, _) => ArrangeBackdrop();
        // Lines that wrap again (a new width, a picture that arrived) change the document's height: measure afresh.
        found.RegisterPropertyChangedCallback(ScrollViewer.ExtentHeightProperty, (_, _) => RemeasureBackdrop());
        RemeasureBackdrop();
    }

    /// <summary>
    /// Resolves what the renderer painted by paragraph into the document's characters. False when the document
    /// does not hold the paragraphs the renderer counted: nothing can be placed then.
    /// </summary>
    private bool Place(TranscriptLook look)
    {
        var starts = new List<int>(look.Paragraphs + 2) { 0 };
        for (var index = 0; index < plain.Length; index++) if (plain[index] == '\r') starts.Add(index + 1);
        blocks = []; ranges = []; marks = []; outer = [];
        paragraphStarts = starts; shown = look;
        // Every paragraph ends in one mark, and the document's own last mark follows them.
        var counted = starts.Count == look.Paragraphs + 2;
        if (counted)
        {
            var covered = -1;
            foreach (var block in look.Blocks)
            {
                if (block.Last < block.First) continue;
                if (block.First > covered) { outer.Add(blocks.Count); covered = block.Last; }
                blocks.Add((block, starts[block.First], starts[block.Last + 1] - 1));
            }
            foreach (var link in look.Links)
            {
                int from = starts[link.First], to = starts[link.Last + 1] - 1;
                // Whole paragraphs take their last mark with them: a press beside a line's end lands on it, and the Mac's code attribute covers the line end too (M/AgentTranscriptFormat.swift:445-452).
                if (link.Label is null) { ranges.Add((from, to + 1, link.Key)); continue; }
                var at = to > from ? plain.LastIndexOf(link.Label, to - 1, to - from, StringComparison.Ordinal) : -1;
                if (at >= 0) ranges.Add((at, at + link.Label.Length, link.Key));
            }
            foreach (var paragraph in look.Marks) if (starts[paragraph] < plain.Length && plain[starts[paragraph]] == TranscriptRtf.ObjectCharacter) marks.Add(starts[paragraph]);
        }
        RemeasureBackdrop();
        return counted;
    }

    /// <summary>Forgets where the blocks were and paints them again from the lines as they are laid out now.</summary>
    private void RemeasureBackdrop()
    {
        measured = new (double Top, double Bottom)?[blocks.Count]; paintedFrom = double.NaN;
        ArrangeBackdrop();
    }

    /// <summary>A block's top and bottom in the document: its first paragraph's first character carries the space before, its last paragraph's mark the space after.</summary>
    private (double Top, double Bottom) Measure(int index)
    {
        if (measured[index] is { } known) return known;
        var (block, start, end) = blocks[index];
        View.Document.GetRange(start, start + 1).GetRect(PointOptions.ClientCoordinates, out var first, out _);
        View.Document.GetRange(end, end + 1).GetRect(PointOptions.ClientCoordinates, out var mark, out _);
        var edges = (first.Top + block.TrimTop, mark.Bottom - block.TrimBottom);
        measured[index] = edges;
        return edges;
    }

    /// <summary>
    /// The paragraph at a height of the document, for leaving unmeasured the blocks that end before it (the
    /// stretch's top) or start after it (its bottom). Only a character RichEdit itself puts beyond that height
    /// may rule a block out: without one, the answer is the first paragraph or past the last, which rules out none.
    /// </summary>
    private int ParagraphAt(double height, bool top)
    {
        if (top ? height <= 0 : height >= (scroller?.ExtentHeight ?? 0)) return top ? 0 : int.MaxValue;
        var position = Math.Clamp(View.Document.GetRangeFromPoint(new Point(0, height), PointOptions.ClientCoordinates).StartPosition, 0, Math.Max(0, plain.Length - 1));
        View.Document.GetRange(position, position + 1).GetRect(PointOptions.ClientCoordinates, out var line, out _);
        if (top ? line.Top > height : line.Bottom < height) return top ? 0 : int.MaxValue;
        var paragraph = paragraphStarts.BinarySearch(position);
        return paragraph >= 0 ? paragraph : ~paragraph - 1;
    }

    /// <summary>
    /// Paints the blocks in and near the viewport at a scroll offset (the box's own when none is given) and
    /// moves the layer there. A long conversation holds hundreds of blocks; only those on screen are measured
    /// and drawn, found by halving through the outer ones, and a scroll inside what is painted moves nothing else.
    /// A reply's card may itself hold hundreds (its code, its quotes) and is measured again after every write of
    /// a streaming answer: of those, only the ones in the paragraphs the painted stretch covers.
    /// </summary>
    private void ArrangeBackdrop(double? offset = null)
    {
        if (scroller is null) return;
        const double slack = 320;
        var at = offset ?? scroller.VerticalOffset; var height = Math.Max(0, scroller.ViewportHeight);
        scrolled.Y = -at;
        if (!double.IsNaN(paintedFrom) && at - slack / 2 >= paintedFrom && at + height + slack / 2 <= paintedTo) return;
        double from = at - slack, to = at + height + slack, width = TextWidth; var padding = View.Padding; var used = 0;
        int low = 0, high = outer.Count, head = 0, tail = int.MaxValue; var edged = false;
        while (low < high) { var middle = (low + high) / 2; if (Measure(outer[middle]).Bottom < from) low = middle + 1; else high = middle; }
        for (var group = low; group < outer.Count && Measure(outer[group]).Top <= to; group++)
        {
            var until = group + 1 < outer.Count ? outer[group + 1] : blocks.Count;
            // The stretch's own paragraphs are only asked for by a block with enough inside it to be worth the asking.
            if (!edged && until - outer[group] > 8) { head = ParagraphAt(from, true); tail = ParagraphAt(to, false); edged = true; }
            for (var index = outer[group]; index < until; index++)
            {
                var block = blocks[index].Block; Border shape;
                // Blocks stand in the order they open: one that ends above the stretch is passed over, and past the first that opens under it there is none left.
                if (block.Last < head) continue;
                if (block.First > tail) break;
                var (top, bottom) = Measure(index);
                if (bottom < from || top > to) continue;
                if (used < layer.Children.Count) shape = (Border)layer.Children[used]; else layer.Children.Add(shape = new Border());
                used++; Paint(shape, block);
                shape.Width = block.Kind == TranscriptBlockKind.Quote ? TranscriptRtf.QuoteBar : Math.Max(0, width - block.Left - block.Right);
                shape.Height = Math.Max(0, bottom - top);
                Canvas.SetLeft(shape, padding.Left + block.Left); Canvas.SetTop(shape, padding.Top + top);
            }
        }
        while (layer.Children.Count > used) layer.Children.RemoveAt(layer.Children.Count - 1);
        paintedFrom = from; paintedTo = to;
    }

    /// <summary>
    /// A block's paint (M/AgentTranscriptFormat.swift:174-228, 368-372, 425-444): the reply card and the chip
    /// are <c>card</c> with a 1pt <c>line</c> edge (<c>errText</c> round a failed call), the bubble <c>ink</c>,
    /// the code <c>codeSurface</c>, all at radius 11; the plain look's code box is the ink at 3.5% with a
    /// hairline, its request wash the accent at 7%, and a quote a 3pt accent bar at 55%.
    /// </summary>
    private void Paint(Border shape, TranscriptBlock block)
    {
        var b = Paints; var kind = block.Kind;
        Brush fill = kind switch
        {
            TranscriptBlockKind.Card or TranscriptBlockKind.Chip => b.Brush(DesignToken.Card),
            TranscriptBlockKind.Bubble => b.Brush(DesignToken.Ink),
            TranscriptBlockKind.Code => b.Brush(DesignToken.CodeSurface),
            TranscriptBlockKind.Box => b.Brush(DesignToken.Ink, TranscriptRtf.BoxWash),
            TranscriptBlockKind.Wash => b.Brush(DesignToken.Accent, TranscriptRtf.RequestWash),
            _ => b.Brush(DesignToken.Accent, TranscriptRtf.QuoteBarOpacity),
        };
        Brush? edge = kind switch
        {
            TranscriptBlockKind.Chip when block.Error => b.Brush(DesignToken.ErrText),
            TranscriptBlockKind.Card or TranscriptBlockKind.Chip or TranscriptBlockKind.Box => b.Brush(DesignToken.Line),
            _ => null,
        };
        var line = kind is TranscriptBlockKind.Card or TranscriptBlockKind.Chip ? DesignMetrics.Stroke.Line : kind == TranscriptBlockKind.Box ? DesignMetrics.Stroke.Hairline : 0;
        var corners = new CornerRadius(kind is TranscriptBlockKind.Card or TranscriptBlockKind.Chip or TranscriptBlockKind.Bubble or TranscriptBlockKind.Code ? TranscriptRtf.BlockRadius : 0);
        if (!ReferenceEquals(shape.Background, fill)) shape.Background = fill;
        if (!ReferenceEquals(shape.BorderBrush, edge)) shape.BorderBrush = edge;
        if (shape.BorderThickness.Left != line) shape.BorderThickness = new Thickness(line);
        if (shape.CornerRadius != corners) shape.CornerRadius = corners;
    }
}

/// <summary>What a transcript is drawn for, and what the renderer painted for it by paragraph.</summary>
internal sealed class TranscriptLook
{
    internal bool Cards { get; init; }
    /// <summary>The width the text wraps at, in epx; 0 until the box has been measured.</summary>
    internal double Width { get; init; }
    /// <summary>Device pixels per epx: the drawn marks are made at this scale.</summary>
    internal double Scale { get; init; } = 1;
    internal double TopInset { get; init; } = TranscriptRtf.Inset;
    internal double BottomInset { get; init; } = TranscriptRtf.Inset;
    internal double BodySize { get; init; } = DesignMetrics.Type.Body;
    internal bool PlainAnswer { get; init; }
    /// <summary>Whether file paths and web addresses in the words are drawn as links: only the Mighty blocks' transcripts do (M/AgentTranscriptFormat.swift:15-16).</summary>
    internal bool References { get; init; }
    /// <summary>
    /// The entries whose drawing ran out of time, by id, each with the text that did. A transcript keeps it
    /// from one drawing to the next, so such an entry is not tried again while its text stands
    /// (<see cref="TranscriptRtf"/>'s Render); without it every drawing tries every entry.
    /// </summary>
    internal Dictionary<string, string>? TimedOut { get; init; }
    internal List<TranscriptBlock> Blocks { get; } = [];
    internal List<TranscriptLink> Links { get; } = [];
    /// <summary>Where each entry starts in the RTF and which paragraph it opens: what a streaming update compares, to write only the changed end.</summary>
    internal List<(int Offset, int Paragraph)> Entries { get; } = [];
    /// <summary>The lengths of the RTF before the first entry (the fonts and colours) and after the last (the document's own last mark).</summary>
    internal int Head { get; set; }
    internal int Foot { get; set; }
    /// <summary>The paragraphs that open with a drawn mark.</summary>
    internal List<int> Marks { get; } = [];
    internal int Paragraphs { get; set; }
}

internal enum TranscriptBlockKind { Card, Bubble, Chip, Code, Box, Wash, Quote }

/// <summary>
/// One painted block: the paragraphs it runs over and its distance from the text's left and right edges.
/// <see cref="TrimTop"/> and <see cref="TrimBottom"/> are the padding of the blocks around it that the same
/// first and last paragraphs carry.
/// </summary>
internal sealed class TranscriptBlock(TranscriptBlockKind kind, int first, double left, double right, bool error)
{
    internal TranscriptBlockKind Kind { get; } = kind;
    internal int First { get; } = first;
    internal int Last { get; set; } = first - 1;
    internal double Left { get; } = left;
    internal double Right { get; } = right;
    internal bool Error { get; } = error;
    internal double TrimTop { get; set; }
    internal double TrimBottom { get; set; }
    internal double PadLeft { get; init; }
    internal double PadRight { get; init; }
    internal double PadTop { get; init; }
    internal double PadBottom { get; init; }
}

/// <summary>What a click acts on: the words <paramref name="Label"/> in a paragraph, or whole paragraphs (a picture, a code block).</summary>
internal sealed record TranscriptLink(int First, int Last, string? Label, string Key);

/// <summary>Bounded Markdown to RTF. All source text is escaped; no HTML, images,
/// file links, fields or executable RTF instructions from provider output run.</summary>
internal static class TranscriptRtf
{
    /// <summary>The text view's inset on every side (M/AgentTranscriptView.swift:168, 372-376).</summary>
    internal const double Inset = DesignMetrics.Inset.Transcript;
    /// <summary>The corner radius of the painted blocks (M/AgentTranscriptFormat.swift:487).</summary>
    internal const double BlockRadius = 11;
    /// <summary>A quote's bar: 3pt wide in the accent at 55% (M/AgentTranscriptFormat.swift:368-372).</summary>
    internal const double QuoteBar = 3, QuoteBarOpacity = 0.55;
    /// <summary>The plain look's washes: the label colour at 3.5% under code, the accent at 7% under a request (M/AgentTranscriptFormat.swift:10, 442).</summary>
    internal const double BoxWash = 0.035, RequestWash = 0.07;
    /// <summary>The height the box gives the paragraph mark RichEdit keeps at a document's end.</summary>
    internal const float LastMark = 1;
    /// <summary>The character a picture holds in the document.</summary>
    internal const char ObjectCharacter = (char)0xFFFC;
    /// <summary>The character RichEdit keeps for a line break inside a paragraph (<c>\line</c>).</summary>
    internal const char LineBreak = (char)0xB;
    /// <summary>The Mac's line spacing: 3pt in a transcript (M/AgentTranscriptFormat.swift:300), 4pt in the files pane's Markdown (M/AgentMarkdownView.swift:141).</summary>
    private const double Lead = 3, PreviewLead = 4;
    /// <summary>A boxed block's padding in the plain look and a wash's: the code block's inset and the box's hairline edge (M/AgentTranscriptFormat.swift:425-431).</summary>
    private const double BoxPad = DesignMetrics.Inset.CodeBlock + DesignMetrics.Stroke.Hairline;
    /// <summary>Longer sources are shown as literal text, not parsed (M/AgentMarkdownView.swift:52).</summary>
    private const int MaximumRenderBytes = 131_072;
    private const int MaximumDepth = 8;
    /// <summary>
    /// What one Markdown source may draw as tables. RichEdit drops a row's cells past the 63rd, and a cell,
    /// whatever it holds and a missing one too (a row is filled out to the head's columns), is about 200
    /// characters of RTF and a fifth of a millisecond for the box to lay out, each time its entry is written:
    /// 5 KB of pipes could ask for a million cells, 200 MB and minutes. So a table wider than
    /// <see cref="MaximumTableColumns"/>, or one that would take the tables of its source past
    /// <see cref="MaximumTableCells"/> cells in all (0.4 s a writing), is drawn as the lines it was written in.
    /// </summary>
    private const int MaximumTableColumns = 63, MaximumTableCells = 2_000;
    /// <summary>
    /// How long one Markdown source may take to parse and draw, in Stopwatch ticks: a quarter of a second,
    /// where the longest reply takes a few hundredths. Every pattern has a timeout of its own, but that bounds
    /// one match, and a hundred lines that each stay just under it would never be stopped.
    /// </summary>
    private static readonly long MarkdownBudget = System.Diagnostics.Stopwatch.Frequency / 4;
    /// <summary>
    /// Whether this process has drawn Markdown before. The first drawing also compiles the patterns and the
    /// code that draws, 150 to 200 ms where a later one takes under one: that is the process's cost, not the
    /// source's, so the first drawing is not held to <see cref="MarkdownBudget"/>.
    /// </summary>
    private static bool compiled;
    /// <summary>How many entries a transcript remembers as out of time; past that it forgets them all and learns again.</summary>
    private const int MaximumTimedOut = 64;
    /// <summary>The files preview's quote: its 3pt bar and the 11pt to its words (M/AgentMarkdownView.swift:170-171).</summary>
    private const double PreviewQuote = 14;
    /// <summary>The files preview's column at its widest: 860 less the 18pt on each side (M/FilePaneView.swift:246). A rule is drawn this wide and the box cuts it off at its own edge.</summary>
    private const double PreviewWidth = 824;

    // The colour table, 1-based.
    private const int Ink = 1, Ink2 = 2, Accent = 3, ErrText = 4, PageWash = 5, CardInk = 6, CodeText = 7, WaitText = 8, AccentSoft = 9, Rule = 10, CodeWash = 11, HeadWash = 12, Done = 13, Err = 14, Wait = 15, Run = 16, Stop = 17, QuoteInk = 18, Subtle = 19;
    // The font table: the body, the monospace, the symbols and the icons.
    private const int BodyFont = 0, MonoFont = 1, SymbolFont = 2, IconFont = 3;

    private static readonly Lazy<(string Body, string Mono, string Icons)> Fonts = new(() =>
    {
        HashSet<string> installed;
        try { installed = new(Microsoft.Graphics.Canvas.Text.CanvasTextFormat.GetSystemFontFamilies(), StringComparer.OrdinalIgnoreCase); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { installed = []; }
        string First(string families) => families.Split(',')[0].Trim();
        string Installed(string families) => families.Split(',').Select(f => f.Trim()).FirstOrDefault(installed.Contains) ?? families.Split(',')[^1].Trim();
        // SF Pro → the app's body family (RichEdit falls back by itself); SF Mono and SF Symbols → the first installed stand-in.
        return (First(DesignMetrics.Font.Body), Installed(DesignMetrics.Font.Mono), Installed("Segoe Fluent Icons, Segoe MDL2 Assets"));
    });

    private static readonly Regex Fence = new(@"^(\s{0,3})(`{3,}|~{3,})\s*([^`]*)$", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
    private static readonly Regex Heading = new(@"^\s{0,3}(#{1,6})\s+(.*?)(?:\s+#+)?\s*$", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
    private static readonly Regex ThematicBreak = new(@"^\s{0,3}(?:(?:\*\s*){3,}|(?:-\s*){3,}|(?:_\s*){3,})$", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
    private static readonly Regex ListItem = new(@"^(\s*)([-*+]|\d{1,9}[.)])(?:\s+(.*))?$", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
    private static readonly Regex TableDelimiter = new(@"^\s*\|?\s*:?-+:?\s*(?:\|\s*:?-+:?\s*)*\|?\s*$", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
    // An escaped mark, a picture, code, bold italic, bold, italic, strikethrough, a link, an address in angle brackets.
    private static readonly Regex Inline = new(
        @"(\\[\\`*_{}\[\]()#+\-.!~|>])|(!\[[^\]\r\n]*\]\([^\)\r\n]+\))|(``[^\r\n]+?``|`[^`\r\n]+`)|(\*\*\*[^*\r\n]+\*\*\*)" +
        @"|(\*\*[^\r\n]+?\*\*|(?<![\p{L}\p{N}])__[^\r\n]+?__(?![\p{L}\p{N}]))|((?<!\*)\*(?![\s*])[^*\r\n]+\*|(?<![\p{L}\p{N}_])_(?![\s_])[^_\r\n]+_(?![\p{L}\p{N}_]))" +
        @"|(~~[^~\r\n]+~~)|(\[[^\]\r\n]+\]\([^\)\r\n]+\))|(<(?:https?://|mailto:)[^>\s]+>)", RegexOptions.Compiled, TimeSpan.FromMilliseconds(150));

    /// <summary>Markdown pictures found in one line, drawn as their own paragraphs after it.</summary>
    private sealed class PictureContext(AgentPictures? pictures, string? root, IReadOnlySet<string>? expanded = null, Dictionary<string, TranscriptAction>? actions = null)
    {
        internal readonly AgentPictures? Pictures = pictures;
        internal readonly string? Root = root;
        internal readonly List<(AgentPictures.Picture Picture, TranscriptAction? Action)> Pending = [];
        internal readonly IReadOnlySet<string>? Expanded = expanded;
        internal readonly Dictionary<string, TranscriptAction>? Actions = actions;
    }

    /// <summary>A Mac measure in epx as twips: 1 epx is 1/96 in, a twip 1/1440 in.</summary>
    private static int Twips(double epx) => (int)Math.Round(epx * 15);
    /// <summary>
    /// A Mac font size in epx as RTF half-points: <c>\fs</c> counts typographic points (1/72 in), so 13 is
    /// <c>\fs20</c>, not <c>\fs26</c>. Halves round up; 22 and 18 come out exact.
    /// </summary>
    internal static int HalfPoints(double epx) => (int)Math.Round(epx * 1.5, MidpointRounding.AwayFromZero);
    /// <summary>A Markdown heading's size in Mac points: 22, 18, 15 and 13 for levels 1, 2, 3 and below (M/AgentTranscriptFormat.swift:356, M/AgentMarkdownView.swift:157).</summary>
    internal static double HeadingSize(int level) => level switch { 1 => 22, 2 => 18, 3 => 15, _ => 13 };

    /// <summary>Hangul and the other full-width scripts, which the Mac lays out on a taller line than Latin text.</summary>
    private static bool Wide(char c) => c >= 0x1100 && (c <= 0x11FF || c >= 0x2E80 && c <= 0xA4CF || c >= 0xAC00 && c <= 0xD7FF || c >= 0xF900 && c <= 0xFAFF || c >= 0xFE30 && c <= 0xFE4F || c >= 0xFF00 && c <= 0xFFEF);
    /// <summary>
    /// The height of a Mac text line of a font size, before its line spacing: the system font's own
    /// (ascender + descender, 1.193 em, rounded up: 16 for 13pt), or the taller fallback line Hangul gets
    /// (about 1.42 em: 18.5 for 13pt, as the Mac screenshots measure). The Windows fonts' own lines differ
    /// (Segoe UI 1.33 em, Malgun Gothic 1.72 em), so each paragraph is given this pitch exactly.
    /// </summary>
    private static double Line(double size, string? text) =>
        text is not null && text.Any(Wide) ? Math.Round(size * 1.42 * 2, MidpointRounding.AwayFromZero) / 2 : Math.Ceiling(size * 1.193 - 0.001);

    internal static string Render(RunSession session, bool light) => Render(session, light, null, null);
    internal static string Render(RunSession session, bool light, AgentPictures? pictures, string? root, IReadOnlySet<string>? expanded = null, Dictionary<string, TranscriptAction>? actions = null, TranscriptLook? look = null)
    {
        var palette = DesignTokens.Palette(!light);
        var w = new Writer(look ?? new TranscriptLook(), new PictureContext(pictures, root, expanded, actions), palette, preview: false);
        Open(w.Body, palette); w.Look.Head = w.Body.Length;
        // The Mac's 3pt line spacing sits under each line and RichEdit's exact pitch puts it above, so every
        // block and the document start 3pt higher and end 3pt lower: the words land where the Mac draws them.
        // A bare answer has no line spacing to move.
        var lead = w.Look.PlainAnswer ? 0 : Lead;
        w.Before(w.Look.TopInset - lead);
        var timedOut = w.Look.TimedOut;
        foreach (var entry in session.Logs)
        {
            w.Look.Entries.Add((w.Body.Length, w.Look.Paragraphs));
            var mark = w.Mark();
            // An entry known to run out of time is drawn as its plain lines without being tried again: every
            // redraw would pay the same time for the same failure.
            var known = timedOut is { Count: > 0 } && timedOut.TryGetValue(entry.Id, out var failed) ? failed : null;
            var plain = known is not null && known == entry.Text;
            // An entry that ran out of time and has grown since (a reply still streaming) is tried once per change,
            // not twice: it has had its second chance already.
            for (var again = known is not null; !plain; again = true)
            {
                try { Entry(w, session, entry); if (timedOut is { Count: > 0 }) timedOut.Remove(entry.Id); break; }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // A provider's malformed Markdown must remain readable, and nothing one entry holds may leave
                    // the drawing as an error: a refresh that throws ends the app. The timeouts are wall-clock,
                    // so the first may be the machine's doing (a stall, code compiled on its first run): before
                    // the entry is remembered as out of time it is tried once more, at once, and only a second
                    // failure running is taken for the text's own.
                    w.Rewind(mark);
                    if (ex is TimeoutException && timedOut is not null)
                    {
                        if (!again) continue;
                        if (timedOut.Count >= MaximumTimedOut && !timedOut.ContainsKey(entry.Id)) timedOut.Clear();
                        timedOut[entry.Id] = entry.Text;
                    }
                    plain = true;
                }
            }
            if (plain) foreach (var line in Lines(entry.Text)) { w.Open(Line(w.Look.BodySize, line) + Lead); w.Run(w.Face(w.Look.BodySize, Ink), line); w.Close(); }
            // A bare answer ends at its last line: the card it sits in has its own padding under it.
            if (!w.Look.PlainAnswer) Separation(w);
        }
        // The document's own last mark stands under the last line; the box makes it LastMark high.
        w.After(w.Look.BottomInset + lead - LastMark);
        var foot = w.Body.Length; var document = Close(w.Body); w.Look.Foot = document.Length - foot;
        return document;
    }
    /// <summary>One Markdown document (the files pane preview) with the transcript's renderer and colours, in the measures of the Mac's AgentMarkdownView.</summary>
    internal static string RenderMarkdown(string text, bool light)
    {
        var palette = DesignTokens.Palette(!light);
        var w = new Writer(new TranscriptLook(), new PictureContext(null, null), palette, preview: true);
        Open(w.Body, palette);
        var mark = w.Mark();
        for (var again = false; ; again = true)
        {
            try { Markdown(w, text, pictures: false); break; }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Out of time twice running (a first time may be the machine's doing, as in Render), or anything
                // else the source holds: the file is still read, as its plain lines.
                w.Rewind(mark);
                if (ex is TimeoutException && !again) continue;
                foreach (var line in Lines(text)) { w.Open(Line(DesignMetrics.Type.Body, line) + PreviewLead, 12); w.Run(w.Face(DesignMetrics.Type.Body, Ink), line); w.Close(); }
                break;
            }
        }
        return Close(w.Body);
    }

    private static void Open(StringBuilder body, DesignPalette palette)
    {
        var fonts = Fonts.Value;
        body.Append(@"{\rtf1\ansi\deff0\uc1{\fonttbl{\f0 ").Append(fonts.Body).Append(@";}{\f1 ").Append(fonts.Mono).Append(@";}{\f2 Segoe UI Symbol;}{\f3 ").Append(fonts.Icons).Append(";}}");
        ColorTable(body, palette);
        // No size here: text without one takes the box's own, exactly 13 epx, where \fs could only say 13.33.
        body.Append(@"\viewkind4\f0\cf1 ");
    }
    private static string Close(StringBuilder body) => body.Append('}').ToString();

    /// <summary>
    /// The document's colour table from the design palette of the theme it is drawn in: 1 <c>ink</c>,
    /// 2 <c>ink2</c>, 3 <c>accent</c>, 4 <c>errText</c>, 5 the files preview's code block wash, <c>page</c> at
    /// 65% over <c>card</c> (M/AgentMarkdownView.swift:258), 6 <c>card</c> (the bubble's words), 7 <c>codeText</c>,
    /// 8 <c>waitText</c>, 9 <c>accentSoft</c> (inline code on a card), 10 <c>line</c> (rules and table edges),
    /// 11 and 12 the ink at 5.5% and 4% over <c>card</c> (plain inline code, a table's head), 13 to 17 the state
    /// fills, 18 the accent at 60% over <c>card</c> (the files preview's quote bar), 19 the ink at 3.5% over
    /// <c>card</c> (the files preview's inline code and table head, M/AgentMarkdownView.swift:194, 284). An RTF document cannot point at the window's shared brushes, so a theme toggle draws it again:
    /// the Default transcript re-renders from PaneView.Refresh, Mighty transcripts through
    /// <see cref="AgentTranscript.Retheme"/>, the files pane through RethemeFilesMarkdown.
    /// </summary>
    private static void ColorTable(StringBuilder body, DesignPalette palette)
    {
        static DesignColor Over(DesignColor ground, DesignColor ink, double opacity) =>
            new((byte)Math.Round(ground.R + (ink.R - ground.R) * opacity), (byte)Math.Round(ground.G + (ink.G - ground.G) * opacity), (byte)Math.Round(ground.B + (ink.B - ground.B) * opacity));
        body.Append(@"{\colortbl;");
        foreach (var color in new[] { palette.Ink, palette.Ink2, palette.Accent, palette.ErrText, Over(palette.Card, palette.Page, 0.65), palette.Card, palette.CodeText, palette.WaitText, palette.AccentSoft, palette.Line,
                     Over(palette.Card, palette.Ink, 0.055), Over(palette.Card, palette.Ink, 0.04), palette.Done, palette.Err, palette.Wait, palette.Run, palette.Stop, Over(palette.Card, palette.Accent, 0.6),
                     Over(palette.Card, palette.Ink, 0.035) })
            body.Append(@"\red").Append(color.R.ToString(CultureInfo.InvariantCulture))
                .Append(@"\green").Append(color.G.ToString(CultureInfo.InvariantCulture))
                .Append(@"\blue").Append(color.B.ToString(CultureInfo.InvariantCulture)).Append(';');
        body.Append('}');
    }

    /// <summary>
    /// Writes paragraphs and keeps count of them, so what is painted or clicked can be named by paragraph:
    /// the document's characters are only known once RichEdit has read it.
    /// </summary>
    private sealed class Writer(TranscriptLook look, PictureContext context, DesignPalette palette, bool preview)
    {
        internal readonly StringBuilder Body = new();
        internal readonly TranscriptLook Look = look;
        internal readonly PictureContext Context = context;
        internal readonly DesignPalette Palette = palette;
        /// <summary>The files pane's Markdown: AgentMarkdownView's measures, every size written out, nothing painted behind.</summary>
        internal readonly bool Preview = preview;
        internal bool Cards => Look.Cards;
        /// <summary>The insets of the blocks the next paragraph sits in.</summary>
        internal double Left, Right;
        /// <summary>The width paragraphs wrap at; a guess until the box has been measured.</summary>
        internal double Width => Look.Width > 1 ? Look.Width : 480;
        private double before;
        /// <summary>The space over the paragraph being written: its own and what the blocks opened on it owe.</summary>
        internal double Above { get; private set; }
        private int afterAt = -1, afterLength;
        private double after;
        private readonly List<TranscriptBlock> closedOnLast = [];
        /// <summary>What the Markdown source being drawn may still cost: the cells its tables may yet draw, and when its time is up (<see cref="MaximumTableCells"/>, <see cref="MarkdownBudget"/>).</summary>
        internal int Cells;
        internal long Deadline = long.MaxValue;
        /// <summary>Ends the source's parsing or drawing once its time is up; whoever draws it then shows its plain lines.</summary>
        internal void Spend() { if (System.Diagnostics.Stopwatch.GetTimestamp() > Deadline) throw new TimeoutException(); }

        /// <summary>
        /// Opens a paragraph (M/AgentTranscriptFormat.swift:298-320): its line pitch exactly (0 leaves it to
        /// its tallest thing, a picture), the space after, the indent of every line and, with
        /// <paramref name="hanging"/>, a marker hung in front of the first.
        /// </summary>
        internal void Open(double pitch, double spacing = 8, double indent = 0, double hanging = 0, double spaceBefore = 0, char align = 'l')
        {
            Body.Append(@"{\pard\li").Append(Twips(Left + indent + hanging)).Append(@"\ri").Append(Twips(Right));
            if (hanging > 0) Body.Append(@"\fi-").Append(Twips(hanging)).Append(@"\tx").Append(Twips(Left + indent + hanging));
            Above = spaceBefore + before; before = 0;
            Body.Append(@"\sb").Append(Twips(Above));
            Body.Append(@"\sa"); afterAt = Body.Length; after = spacing; var value = Twips(spacing).ToString(CultureInfo.InvariantCulture); Body.Append(value); afterLength = value.Length;
            if (pitch > 0) Body.Append(@"\sl-").Append(Twips(pitch)).Append(@"\slmult0");
            if (align != 'l') Body.Append(align == 'r' ? @"\qr" : @"\qc");
            Body.Append(' ');
            closedOnLast.Clear();
        }
        internal void Close() { Body.Append(@"\par}"); Look.Paragraphs++; }
        /// <summary>Space above the next paragraph, on top of its own.</summary>
        internal void Before(double space) => before += space;
        /// <summary>The space the next paragraph owes, for a table row that writes its own paragraphs.</summary>
        internal double TakeBefore() { var space = before; before = 0; return space; }
        /// <summary>More space under the paragraph just written; the blocks that ended on it stop above that space.</summary>
        internal void After(double space)
        {
            if (afterAt < 0 || space == 0) return;
            foreach (var inner in closedOnLast) inner.TrimBottom += space;
            after += space; var value = Twips(after).ToString(CultureInfo.InvariantCulture);
            Body.Remove(afterAt, afterLength).Insert(afterAt, value); afterLength = value.Length;
        }
        /// <summary>A table row is written whole: its marks count as two paragraphs and none of them takes space after.</summary>
        internal void Row() { Look.Paragraphs += 2; afterAt = -1; closedOnLast.Clear(); }

        /// <summary>
        /// Starts a painted block round the paragraphs that follow (M/AgentTranscriptFormat.swift:174-228): its
        /// padding and border become their indents, and the space above its first and under its last one.
        /// </summary>
        internal TranscriptBlock Begin(TranscriptBlockKind kind, double padLeft, double padRight, double padTop, double padBottom, double margin = 0, bool error = false)
        {
            var block = new TranscriptBlock(kind, Look.Paragraphs, Left + margin, Right, error) { TrimTop = before, PadLeft = margin + padLeft, PadRight = padRight, PadTop = padTop, PadBottom = padBottom };
            before += padTop; Left += block.PadLeft; Right += padRight;
            if (!Preview) Look.Blocks.Add(block);
            return block;
        }
        internal void End(TranscriptBlock block)
        {
            Left -= block.PadLeft; Right -= block.PadRight;
            if (Look.Paragraphs == block.First) { before -= block.PadTop; Look.Blocks.Remove(block); return; }
            block.Last = Look.Paragraphs - 1;
            After(block.PadBottom); closedOnLast.Add(block);
        }

        /// <summary>
        /// The agent's or the user's own words. A transcript that opens references draws the file paths in them
        /// in the accent, underlined, and the web addresses in the accent (M/AgentTranscriptFormat.swift:268-288).
        /// </summary>
        internal void Text(string text)
        {
            if (!Look.References || text.AsSpan().IndexOfAny('/', '\\') < 0) { Escape(Body, text); return; }
            var at = 0;
            foreach (var (start, length, web) in References(text))
            {
                if (start < at) continue;
                Escape(Body, text[at..start]);
                Body.Append(@"{\cf").Append(Accent).Append(web ? " " : @"\ul "); Escape(Body, text.Substring(start, length)); Body.Append('}');
                at = start + length;
            }
            Escape(Body, text[at..]);
        }
        /// <summary>A run of such words in one face.</summary>
        internal void Words(string format, string text) { Body.Append('{').Append(format).Append(' '); Text(text); Body.Append('}'); }
        /// <summary>A run of the app's own words, or of code: drawn as written.</summary>
        internal void Run(string format, string text) { Body.Append('{').Append(format).Append(' '); Escape(Body, text); Body.Append('}'); }
        /// <summary>
        /// A run's font, size in Mac points, ink and weight. The body size is left out in a transcript: its box
        /// is 13 epx, which <c>\fs</c> cannot say; the files preview's box is not, so there every size is written.
        /// </summary>
        internal string Face(double size, int ink, int weight = 400, int font = BodyFont)
        {
            var format = new StringBuilder(@"\f").Append(font);
            if (Preview || size != DesignMetrics.Type.Body) format.Append(@"\fs").Append(HalfPoints(size));
            format.Append(@"\cf").Append(ink);
            if (weight >= 700) format.Append(@"\b"); else if (weight != 400) format.Append(@"\fweight").Append(weight);
            return format.ToString();
        }
        /// <summary>What a click or the context menu acts on, by paragraph.</summary>
        internal void Link(int first, int last, string? label, TranscriptAction action)
        {
            if (Context.Actions is null) return;
            var key = Context.Actions.Count.ToString(CultureInfo.InvariantCulture); Context.Actions[key] = action;
            Look.Links.Add(new(first, last, label, key));
        }

        internal (int Length, int Paragraphs, int Blocks, int Links, int Marks, int Actions, double Left, double Right, double Before) Mark() =>
            (Body.Length, Look.Paragraphs, Look.Blocks.Count, Look.Links.Count, Look.Marks.Count, Context.Actions?.Count ?? 0, Left, Right, before);
        /// <summary>Takes back everything written since <paramref name="mark"/>.</summary>
        internal void Rewind((int Length, int Paragraphs, int Blocks, int Links, int Marks, int Actions, double Left, double Right, double Before) mark)
        {
            Body.Length = mark.Length; Look.Paragraphs = mark.Paragraphs; afterAt = -1; closedOnLast.Clear(); Context.Pending.Clear();
            Look.Blocks.RemoveRange(mark.Blocks, Look.Blocks.Count - mark.Blocks); Look.Links.RemoveRange(mark.Links, Look.Links.Count - mark.Links); Look.Marks.RemoveRange(mark.Marks, Look.Marks.Count - mark.Marks);
            if (Context.Actions is { } actions) foreach (var key in actions.Keys.Where(k => int.Parse(k, CultureInfo.InvariantCulture) >= mark.Actions).ToList()) actions.Remove(key);
            Left = mark.Left; Right = mark.Right; before = mark.Before;
        }
    }

    // A file path needs a folder and an ASCII extension, so a version number, "and/or" and a bare name are left
    // alone, and nothing beside a web address is taken for one (M/ReferenceLinks.swift:35-39). On a PC a path may
    // also start at a drive and part its folders with backslashes, as the click on one already reads it.
    private static readonly Regex ReferencePath = new(@"(?<![A-Za-z0-9_/\\.:@~-])((?:[A-Za-z]:[\\/]|\.{1,2}[\\/]|[\\/])?(?:[\p{L}\p{N}_.@-]+[\\/])+[\p{L}\p{N}_.@-]+\.[A-Za-z0-9]{1,8})(?::(\d{1,6}))?(?![A-Za-z0-9_/\\])", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
    private static readonly Regex WebAddress = new(@"https?://[^\s<>""'`)\]]+", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
    private const int MaximumReferences = 200, MaximumPathBytes = 1_024;

    /// <summary>The web addresses and file paths in plain words, in the order they stand (M/ReferenceLinks.swift:41-64).</summary>
    private static List<(int Start, int Length, bool Web)> References(string text)
    {
        var found = new List<(int Start, int Length, bool Web)>();
        foreach (var match in WebAddress.Matches(text).Take(MaximumReferences))
        {
            // Sentence punctuation directly after an address is rarely part of it.
            var length = match.Length; while (length > 1 && ".,;:!?".Contains(text[match.Index + length - 1])) length--;
            if (Uri.TryCreate(text.Substring(match.Index, length), UriKind.Absolute, out var url) && url.Host.Length > 0) found.Add((match.Index, length, true));
        }
        var addresses = found.Count;
        foreach (var match in ReferencePath.Matches(text).Take(MaximumReferences))
        {
            if (Encoding.UTF8.GetByteCount(match.Groups[1].Value) > MaximumPathBytes) continue;
            var taken = false; for (var index = 0; index < addresses && !taken; index++) taken = match.Index < found[index].Start + found[index].Length && found[index].Start < match.Index + match.Length;
            if (!taken) found.Add((match.Index, match.Length, false));
        }
        found.Sort((a, b) => a.Start.CompareTo(b.Start));
        return found;
    }

    private static string[] Lines(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
    /// <summary>"오후 3:12" for an entry's ISO timestamp, empty when it does not parse (M/AgentTranscriptFormat.swift:87-90).</summary>
    private static string Time(string timestamp) =>
        DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date) ? date.ToLocalTime().ToString("t", CultureInfo.CurrentCulture) : "";

    /// <summary>A thin gap paragraph (M/AgentTranscriptFormat.swift:290-296): a 4pt line with 2pt under it, 10pt in all.</summary>
    private static void Separation(Writer w) { w.Open(10, 0); w.Body.Append(@"\fs6 "); w.Close(); }

    /// <summary>One log entry as the Mac's <c>AgentTranscriptFormat.entry</c> draws it (M/AgentTranscriptFormat.swift:24-85).</summary>
    private static void Entry(Writer w, RunSession session, LogEntry entry)
    {
        var cards = w.Cards; var secondary = Ink2;
        if (entry.Activity is { } activity) { if (cards) ToolChip(w, session, entry, activity); else ToolRow(w, session, entry, activity); }
        else if (entry.Kind == "user")
        {
            var lines = Lines(entry.Text);
            if (cards)
            {
                // The request: an ink bubble over the right 85% of the width, the card colour for its words, its time under them (:186-209).
                var bubble = w.Begin(TranscriptBlockKind.Bubble, DesignMetrics.Inset.UserBubbleH, DesignMetrics.Inset.UserBubbleH, DesignMetrics.Inset.UserBubbleT - Lead, DesignMetrics.Inset.UserBubbleB + Lead, margin: Math.Round(w.Width * 0.15));
                foreach (var line in lines) { w.Open(Line(w.Look.BodySize, line) + Lead, 3); w.Run(w.Face(w.Look.BodySize, CardInk), line); w.Close(); }
                if (Time(entry.Timestamp) is { Length: > 0 } time) { w.Open(Line(10.5, time) + Lead, 0, align: 'r'); w.Run(w.Face(10.5, CardInk), time); w.Close(); }
                w.End(bubble);
            }
            else
            {
                // The plain look: an up-right arrow and the request on the accent wash (:67-70).
                var wash = w.Begin(TranscriptBlockKind.Wash, BoxPad, BoxPad, BoxPad - Lead, BoxPad + Lead);
                for (var index = 0; index < lines.Length; index++)
                {
                    w.Open(Line(w.Look.BodySize, lines[index]) + Lead, 12);
                    if (index == 0) { w.Run(w.Face(11, Accent, 500, SymbolFont), "↗"); w.Run(w.Face(w.Look.BodySize, Ink), "  "); }
                    w.Words(w.Face(w.Look.BodySize, Ink), lines[index]); w.Close();
                }
                w.End(wash);
            }
        }
        else if (entry.Kind == "assistant" && session.Kind != "shell")
        {
            if (w.Look.PlainAnswer)
            {
                // Only the answer's words, line for line, at the line height SwiftUI gives them (no spacing of the transcript's).
                foreach (var line in Lines(entry.Text)) { w.Open(Line(w.Look.BodySize, line), 0); w.Run(w.Face(w.Look.BodySize, Ink), line); w.Close(); }
                return;
            }
            // The speaker and the whole reply share one white card (:53-66); the plain look draws them straight on the block.
            // The card's padding counts its 1pt edge, which the Mac draws inside the padding.
            const double edge = DesignMetrics.Stroke.Line;
            var card = cards ? w.Begin(TranscriptBlockKind.Card, DesignMetrics.Inset.ReplyCardH + edge, DesignMetrics.Inset.ReplyCardH + edge, DesignMetrics.Inset.ReplyCardT + edge - Lead, DesignMetrics.Inset.ReplyCardB + edge + Lead) : null;
            {
                var provider = entry.Provider ?? session.Provider; var time = Time(entry.Timestamp);
                var words = "  " + ProviderMark.Label(provider) + (time.Length == 0 ? "" : "  ·  " + time);
                var size = cards ? 12 : 11; var pitch = Line(size, words) + Lead;
                w.Open(pitch, 10);
                // The mark hangs 2pt under the baseline, the state square 2.5 (M/AgentTranscriptFormat.swift:97, 148).
                if (TranscriptMarks.Provider(provider, w.Look.Scale, MarkLift(w, pitch, TranscriptMarks.ProviderSize, 2)) is { } mark) { w.Look.Marks.Add(w.Look.Paragraphs); w.Body.Append(mark); }
                else w.Run(w.Face(size, Accent), "•");
                w.Run(w.Face(size, secondary, cards ? 600 : 500), words); w.Close();
            }
            if (UserQuestionnaire.Parse(entry.Text) is { } questions) Questionnaire(w, questions); else Markdown(w, entry.Text, pictures: true);
            if (card is not null) w.End(card);
        }
        else if (entry.Kind is "output" or "assistant") Code(w, entry.Text, null);
        else if (entry.Kind == "image")
        {
            // The pictures, then their source in small grey type (macOS: `Read · /path/shot.png`, :73-76).
            foreach (var image in entry.Images ?? []) Picture(w, w.Context.Pictures?.For(image) ?? new(AgentPictures.State.Unsupported), new("image", image.Source, image));
            foreach (var line in Lines(entry.Text)) { w.Open(Line(10, line) + Lead, 7); w.Run(w.Face(10, secondary), line); w.Close(); }
        }
        else
        {
            // A status line: a circled mark and the words at 12, in errText for an error (:77-82).
            var error = entry.Kind == "error"; var ink = error ? ErrText : secondary; var lines = Lines(entry.Text);
            for (var index = 0; index < lines.Length; index++)
            {
                w.Open(Line(12, lines[index]) + Lead, 7);
                if (index == 0) { w.Run(w.Face(11, ink, 500, IconFont), ((char)(error ? 0xE783 : 0xE946)).ToString()); w.Run(w.Face(12, ink), "  "); }
                w.Words(w.Face(12, ink), lines[index]); w.Close();
            }
        }
    }

    /// <summary>
    /// How far down its picture a drawn mark of <paramref name="size"/> sits so that it ends <paramref name="below"/>
    /// under the baseline of the line being written: RichEdit hangs a picture from the top of the line, space
    /// before included, and the words stand on the line's foot, their descent (about 3.3 epx at these sizes) under them.
    /// </summary>
    private static double MarkLift(Writer w, double pitch, double size, double below) => Math.Max(0, w.Above + pitch - 3.3 + below - size);

    /// <summary>A live or failed tool call's state word, shared by the chip and the plain row (M/AgentTranscriptFormat.swift:101-108).</summary>
    private static string ToolState(string state) => Locale.Get(state switch { "waiting" => "transcript.tool.waiting", "error" => "transcript.tool.failed", _ => "transcript.tool.running" });

    /// <summary>The words that open and close a tool's detail, and what they act on: the link of the Mac's row, drawn in the accent.</summary>
    private static void Disclosure(Writer w, LogEntry entry, double size)
    {
        var label = Locale.Get(w.Context.Expanded?.Contains(entry.Id) == true ? "transcript.tool.hideDetail" : "transcript.tool.showDetail");
        w.Run(w.Face(size, Ink2), "  "); w.Run(w.Face(size, Accent), label);
        w.Link(w.Look.Paragraphs, w.Look.Paragraphs, label, new("tool", entry.Id));
    }
    private static void ToolOutput(Writer w, LogEntry entry, AgentActivity activity)
    {
        if (string.IsNullOrWhiteSpace(activity.Output) || w.Context.Actions is not null && w.Context.Expanded?.Contains(entry.Id) != true) return;
        if (w.Cards) Separation(w);
        Code(w, activity.Output, null);
    }

    /// <summary>
    /// One tool call as a chip row (M/AgentTranscriptFormat.swift:211-248): its state as a small filled square,
    /// the tool's name 12.5 bold, the detail 11.5 mono in ink2, then the timing, the state and the disclosure
    /// at 10.5; on a card with a 1pt edge (errText when it failed), padded by <c>Inset.ToolChip</c>.
    /// </summary>
    private static void ToolChip(Writer w, RunSession session, LogEntry entry, AgentActivity activity)
    {
        var live = session.Status == "running" && activity.State is "running" or "waiting";
        var name = activity.ToolName?.Trim() ?? "";
        var detail = activity.Summary.Length == 0 ? name.Length == 0 ? Locale.Get("transcript.tool.fallback") : "" : activity.Summary;
        var duration = ActivitySupport.DurationLabel(activity);
        var state = live || activity.State == "error" ? ToolState(activity.State) : null;
        const double edge = DesignMetrics.Stroke.Line, side = DesignMetrics.Inset.ToolChipH + edge, over = DesignMetrics.Inset.ToolChipV + edge;
        var chip = w.Begin(TranscriptBlockKind.Chip, side, side, over - Lead, over + Lead, error: activity.State == "error");
        var pitch = Math.Max(Math.Max(Line(12.5, name), Line(11.5, detail)), Line(10.5, duration + state + Locale.Get("transcript.tool.showDetail"))) + Lead;
        w.Open(pitch, 0);
        if (TranscriptMarks.Status(activity.State, live, w.Palette, w.Look.Scale, MarkLift(w, pitch, TranscriptMarks.StatusSize, 2.5)) is { } square) { w.Look.Marks.Add(w.Look.Paragraphs); w.Body.Append(square); }
        else w.Run(w.Face(12.5, activity.State switch { "completed" => Done, "error" => Err, "waiting" when live => Wait, "running" when live => Run, _ => Stop }, 400, SymbolFont), "■");
        if (name.Length > 0) w.Run(w.Face(12.5, Ink, 700), "  " + name);
        if (detail.Length > 0) w.Run(w.Face(11.5, Ink2, 400, MonoFont), "  " + detail);
        if (duration is not null) w.Run(w.Face(10.5, Ink2), "  · " + duration);
        if (state is not null) w.Run(w.Face(10.5, activity.State == "error" ? ErrText : activity.State == "waiting" ? WaitText : Accent), "  · " + state);
        if (!string.IsNullOrWhiteSpace(activity.Output) && w.Context.Actions is not null) Disclosure(w, entry, 10.5);
        w.Close(); w.End(chip);
        ToolOutput(w, entry, activity);
    }

    /// <summary>
    /// The plain look's tool row (M/AgentTranscriptFormat.swift:32-52): the kind's symbol, the summary at 12
    /// (mono for a command, a read or an edit) and the timing, state and disclosure at 10, all in ink2.
    /// </summary>
    private static void ToolRow(Writer w, RunSession session, LogEntry entry, AgentActivity activity)
    {
        var live = session.Status == "running" && activity.State is "running" or "waiting";
        var ink = activity.State == "error" ? ErrText : live ? Accent : Ink2;
        var summary = activity.Summary.Length == 0 ? activity.ToolName ?? Locale.Get("transcript.tool.fallback") : activity.Summary;
        var duration = ActivitySupport.DurationLabel(activity);
        // SF Symbols terminal, doc.text.magnifyingglass, pencil.line, magnifyingglass, globe, person.2, sparkles, wrench.and.screwdriver (M/AgentActivityView.swift:116-127).
        var symbol = (char)(activity.Kind switch { "command" => 0xE756, "read" => 0xE8A5, "edit" => 0xE70F, "search" => 0xE721, "web" => 0xE774, "agent" => 0xE716, "turn" => 0xE945, _ => 0xE90F });
        w.Open(Line(12, summary + duration) + Lead, 6);
        w.Run(w.Face(11, ink, 500, IconFont), symbol.ToString());
        w.Words(w.Face(12, Ink2, 400, activity.Kind is "command" or "read" or "edit" ? MonoFont : BodyFont), "  " + summary);
        if (duration is not null) w.Run(w.Face(10, Ink2), "  · " + duration);
        if (live) w.Run(w.Face(10, Accent), "  · " + ToolState(activity.State));
        else if (activity.State == "error") w.Run(w.Face(10, ErrText), "  · " + ToolState(activity.State));
        if (!string.IsNullOrWhiteSpace(activity.Output) && w.Context.Actions is not null) Disclosure(w, entry, 10);
        w.Close();
        ToolOutput(w, entry, activity);
    }

    /// <summary>
    /// The questions an agent asks, read from its tool input (M/AgentTranscriptFormat.swift:385-399): the
    /// header at 11 semibold accent on the accent wash, the question at 14 semibold, each choice at 13 medium
    /// with its note at 12 in ink2.
    /// </summary>
    private static void Questionnaire(Writer w, UserQuestionnaire form)
    {
        for (var index = 0; index < form.Questions.Count; index++)
        {
            var question = form.Questions[index];
            var title = $"{index + 1}. {question.Header}  ·  {Locale.Get(question.MultiSelect ? "phone.questionnaire.multiple" : "phone.questionnaire.single")}";
            var wash = w.Begin(TranscriptBlockKind.Wash, BoxPad, BoxPad, BoxPad - Lead, BoxPad + Lead);
            w.Open(Line(11, title) + Lead, 6); w.Run(w.Face(11, Accent, 600), title); w.Close();
            w.End(wash);
            foreach (var line in Lines(question.Question)) { w.Open(Line(14, line) + Lead, 10); w.Run(w.Face(14, Ink, 600), line); w.Close(); }
            foreach (var option in question.Options)
            {
                w.Open(Line(13, option.Label) + Lead, 3); w.Run(w.Face(13, Ink, 400, SymbolFont), question.MultiSelect ? "☐" : "○"); w.Run(w.Face(13, Ink, 500), "  " + option.Label); w.Close();
                if (option.Description.Length > 0) foreach (var line in Lines(option.Description)) { w.Open(Line(12, line) + Lead, 10, indent: 20); w.Run(w.Face(12, Ink2), line); w.Close(); }
            }
            Separation(w);
        }
    }

    /// <summary>A thumbnail in its own paragraph, or the Mac's words while it loads or once it is gone; clicking it opens it larger. An svg is not drawn here.</summary>
    private static void Picture(Writer w, AgentPictures.Picture picture, TranscriptAction? action)
    {
        switch (picture.State)
        {
            case AgentPictures.State.Ready:
                w.Open(0, 4); w.Body.Append(picture.Rtf);
                if (action is not null) w.Link(w.Look.Paragraphs, w.Look.Paragraphs, null, action);
                w.Close();
                break;
            case AgentPictures.State.Loading: { var words = Locale.Get("images.loading"); w.Open(Line(11, words) + Lead, 4); w.Run(w.Face(11, Ink2), words); w.Close(); break; }
            case AgentPictures.State.Missing: { var words = Locale.Get("images.missing"); w.Open(Line(11, words) + Lead, 4); w.Run(w.Face(11, Ink2), words); w.Close(); break; }
        }
    }
    private static void Flush(Writer w)
    {
        if (w.Context.Pending.Count == 0) return;
        foreach (var picture in w.Context.Pending) Picture(w, picture.Picture, picture.Action);
        w.Context.Pending.Clear();
    }

    /// <summary>
    /// Code (M/AgentTranscriptFormat.swift:434-453): 12 mono, a line a paragraph. On a card it sits on the ink
    /// code surface in codeText, padded by <c>Inset.CodeBlock</c>; the plain look boxes it on the 3.5% wash, padded the same past its hairline; the files
    /// preview washes each line with the page colour. The whole block is what "copy code block" takes.
    /// </summary>
    private static void Code(Writer w, string code, string? language, double indent = 0, double gap = 12)
    {
        var first = w.Look.Paragraphs; var cards = w.Cards;
        const double pad = DesignMetrics.Inset.CodeBlock;
        var block = w.Preview ? null : cards ? w.Begin(TranscriptBlockKind.Code, pad, pad, pad - Lead, pad + Lead) : w.Begin(TranscriptBlockKind.Box, BoxPad, BoxPad, BoxPad - Lead, BoxPad + Lead);
        var ink = cards ? CodeText : Ink;
        var wash = w.Preview ? @"\highlight" + PageWash : "";
        if (language?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() is { } name)
        { w.Open(Line(10, name) + Lead, 5, indent); w.Run(w.Face(10, cards ? ink : Ink2, 500, MonoFont), name); w.Close(); }
        var lines = Lines(code.TrimEnd('\n', '\r'));
        for (var index = 0; index < lines.Length; index++)
        {
            // Code lines keep the Mac's 3pt in both; the files preview's stand together and part from the next block by its gap.
            w.Open(Line(12, lines[index]) + Lead, w.Preview ? index == lines.Length - 1 ? gap : 0 : cards ? 2 : 8, indent);
            w.Run(w.Face(12, ink, 400, MonoFont) + wash, lines[index]); w.Close();
        }
        if (block is not null) w.End(block);
        w.Link(first, w.Look.Paragraphs - 1, null, new("copy", code));
    }

    // ── Markdown ───────────────────────────────────────────────────────────────

    private abstract record Md;
    private sealed record MdParagraph(string Text) : Md;
    private sealed record MdHeading(int Level, string Text) : Md;
    private sealed record MdCode(string? Language, string Text) : Md;
    private sealed record MdQuote(List<Md> Children) : Md;
    private sealed record MdItem(int Ordinal, List<Md> Children);
    private sealed record MdList(bool Ordered, List<MdItem> Items) : Md;
    private sealed record MdRule : Md;
    private sealed record MdTable(char[] Alignments, List<string[]> Rows) : Md;
    /// <summary>Lines drawn as they were written, a paragraph each: a table too large to draw as one.</summary>
    private sealed record MdSource(List<string> Lines) : Md;

    private static void Markdown(Writer w, string text, bool pictures)
    {
        if (Encoding.UTF8.GetByteCount(text) > MaximumRenderBytes)
        {
            var size = w.Preview ? DesignMetrics.Type.Body : w.Look.BodySize;
            foreach (var line in Lines(text)) { w.Open(Line(size, line) + (w.Preview ? PreviewLead : Lead), w.Preview ? 12 : 8); w.Run(w.Face(size, Ink), line); w.Close(); }
            return;
        }
        w.Cells = MaximumTableCells; w.Deadline = compiled ? System.Diagnostics.Stopwatch.GetTimestamp() + MarkdownBudget : long.MaxValue;
        Blocks(w, Parse(w, [.. Lines(text)], 0), 0, false, w.Preview ? 12 : 8, pictures);
        compiled = true;
    }

    private static int IndentOf(string line) { var width = 0; foreach (var c in line) { if (c == ' ') width++; else if (c == '\t') width += 4; else break; } return width; }
    private static string Unindent(string line, int width) { var at = 0; while (at < line.Length && width > 0 && (line[at] == ' ' || line[at] == '\t')) { width -= line[at] == '\t' ? 4 : 1; at++; } return line[at..]; }
    private static bool Interrupts(string line) => Fence.IsMatch(line) || Heading.IsMatch(line) || ThematicBreak.IsMatch(line) || line.TrimStart().StartsWith('>') || ListItem.Match(line) is { Success: true } item && item.Groups[3].Success;
    /// <summary>Whether a table opens at a line: it holds a <c>|</c> and the line under it is a delimiter row of as many cells.</summary>
    private static bool TableStarts(List<string> lines, int i) =>
        lines[i].Contains('|') && i + 1 < lines.Count && lines[i + 1].Contains('-') && TableDelimiter.IsMatch(lines[i + 1]) && Cells(lines[i]).Length == Cells(lines[i + 1]).Length;
    private static string[] Cells(string row)
    {
        var text = row.Trim(); if (text.StartsWith('|')) text = text[1..]; if (text.EndsWith('|') && !text.EndsWith(@"\|", StringComparison.Ordinal)) text = text[..^1];
        // An escaped pipe is a pipe in its cell, but for the backslash a Windows path holds (InPath).
        static string Pipes(string cell)
        {
            if (!cell.Contains(@"\|", StringComparison.Ordinal)) return cell;
            var words = new StringBuilder(cell.Length); (int Start, int End, bool Path) word = default;
            for (var at = 0; at < cell.Length; at++) if (cell[at] != '\\' || at + 1 == cell.Length || cell[at + 1] != '|' || InPath(cell, at, ref word)) words.Append(cell[at]);
            return words.ToString();
        }
        return [.. Regex.Split(text, @"(?<!\\)\|", RegexOptions.None, TimeSpan.FromMilliseconds(100)).Select(cell => Pipes(cell.Trim()))];
    }

    /// <summary>
    /// Whether the word at <paramref name="at"/> (the run of non-whitespace round it) is a Windows path: it starts
    /// at a drive (<c>C:\</c>), or holds a backslash directly before a letter, a digit or a dot-name
    /// (<c>native\windows\_build</c>, <c>MightyClaude\.claude</c>, and a share's <c>\\server</c>). Such a word
    /// keeps every backslash. CommonMark reads <c>\.</c>, <c>\_</c>, <c>\-</c> and <c>\\</c> as escaped marks and
    /// drops the backslash, and the Mac's parser with it; on a PC that turns
    /// <c>C:\Work\.claude\settings.json</c> into a path that is not there, in the words and under a click.
    /// Ordinary prose (<c>snake\_case</c>, <c>\*literal\*</c>, <c>1\.</c>, a lone <c>\\</c>) still loses its
    /// escapes, and so does a path written with its backslashes doubled (<c>C:\\Users\\me</c>, <c>a\\b</c>): a
    /// pair inside a word is CommonMark's one backslash, not two separators, and only a pair the word starts
    /// with is a share's. A drive may stand behind an opening quote or bracket (<c>"C:\_x"</c>, <c>(D:\_y)</c>).
    /// <paramref name="word"/> keeps the last word looked at, so a long one is read once.
    /// </summary>
    private static bool InPath(string text, int at, ref (int Start, int End, bool Path) word)
    {
        if (at >= word.Start && at < word.End) return word.Path;
        int start = at, end = at;
        while (start > 0 && !char.IsWhiteSpace(text[start - 1])) start--;
        while (end < text.Length && !char.IsWhiteSpace(text[end])) end++;
        var run = text.AsSpan(start, end - start);
        var head = run.TrimStart("\"'([<");
        var path = head.Length >= 3 && char.IsAsciiLetter(head[0]) && head[1] == ':' && head[2] == '\\' && !(head.Length > 3 && head[3] == '\\');
        for (var index = 0; !path && index + 1 < run.Length; index++)
        {
            if (run[index] != '\\') continue;
            if (run[index + 1] == '\\' && index > run.Length - head.Length) { index++; continue; }
            path = char.IsLetterOrDigit(run[index + 1]) || run[index + 1] == '.' && index + 2 < run.Length && char.IsLetter(run[index + 2]);
        }
        word = (start, end, path);
        return path;
    }

    /// <summary>
    /// The blocks of a Markdown source, as the Mac's parser hands them over (CommonMark with tables and task
    /// items): blank lines part paragraphs, a line break inside one is a space, lists and quotes nest. A fence
    /// left open still is a code block, so an answer reads while it streams.
    /// </summary>
    private static List<Md> Parse(Writer w, List<string> lines, int depth)
    {
        var blocks = new List<Md>(); var i = 0;
        while (i < lines.Count)
        {
            w.Spend();
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) { i++; continue; }
            if (Fence.Match(line) is { Success: true } fence)
            {
                var mark = fence.Groups[2].Value; var code = new StringBuilder(); i++;
                while (i < lines.Count && !(lines[i].TrimStart().StartsWith(mark, StringComparison.Ordinal) && lines[i].Trim().Trim(mark[0]).Length == 0)) code.Append(Unindent(lines[i++], fence.Groups[1].Length)).Append('\n');
                if (i < lines.Count) i++;
                var info = fence.Groups[3].Value.Trim();
                blocks.Add(new MdCode(info.Length == 0 ? null : info, code.ToString().TrimEnd('\n'))); continue;
            }
            if (Heading.Match(line) is { Success: true } heading) { blocks.Add(new MdHeading(heading.Groups[1].Length, heading.Groups[2].Value)); i++; continue; }
            if (ThematicBreak.IsMatch(line)) { blocks.Add(new MdRule()); i++; continue; }
            if (depth < MaximumDepth && line.TrimStart().StartsWith('>'))
            {
                var inner = new List<string>();
                for (; i < lines.Count && lines[i].TrimStart().StartsWith('>'); i++) { var rest = lines[i].TrimStart()[1..]; inner.Add(rest.StartsWith(' ') ? rest[1..] : rest); }
                blocks.Add(new MdQuote(Parse(w, inner, depth + 1))); continue;
            }
            if (depth < MaximumDepth && ListItem.IsMatch(line)) { blocks.Add(List(w, lines, ref i, depth)); continue; }
            if (TableStarts(lines, i))
            {
                var head = i; var alignments = Cells(lines[i + 1]).Select(cell => cell.StartsWith(':') && cell.EndsWith(':') ? 'c' : cell.EndsWith(':') ? 'r' : 'l').ToArray();
                i += 2; while (i < lines.Count && !string.IsNullOrWhiteSpace(lines[i]) && lines[i].Contains('|')) i++;
                // Every row is drawn with the head's columns, so the table's cells are known before one is read (MaximumTableCells).
                var cells = (long)alignments.Length * (i - head - 1);
                if (alignments.Length > MaximumTableColumns || cells > w.Cells) { blocks.Add(new MdSource(lines.GetRange(head, i - head))); continue; }
                w.Cells -= (int)cells;
                var rows = new List<string[]> { Cells(line) };
                for (var row = head + 2; row < i; row++) rows.Add(Cells(lines[row]));
                blocks.Add(new MdTable(alignments, rows)); continue;
            }
            var paragraph = new StringBuilder(); var hard = false;
            // A paragraph also ends over a table's head row: the Mac's parser takes that row back out of the paragraph it was read into (swift-cmark table.c, try_inserting_table_header_paragraph).
            for (; i < lines.Count && !string.IsNullOrWhiteSpace(lines[i]) && (paragraph.Length == 0 || !Interrupts(lines[i]) && !TableStarts(lines, i)); i++)
            {
                w.Spend();
                if (paragraph.Length > 0) paragraph.Append(hard ? '\n' : ' ');
                // A backslash at a line's end breaks the line there, but the one a Windows path ends in is the path's own (InPath).
                var words = lines[i].Trim(); (int Start, int End, bool Path) word = default;
                var path = words.EndsWith('\\') && InPath(words, words.Length - 1, ref word);
                hard = lines[i].EndsWith("  ", StringComparison.Ordinal) || lines[i].EndsWith('\\') && !path;
                paragraph.Append(path ? words : words.TrimEnd('\\'));
            }
            blocks.Add(new MdParagraph(paragraph.ToString()));
        }
        return blocks;
    }

    private static MdList List(Writer w, List<string> lines, ref int i, int depth)
    {
        var opening = ListItem.Match(lines[i]); var margin = IndentOf(lines[i]); var ordered = char.IsDigit(opening.Groups[2].Value[0]);
        var ordinal = ordered && int.TryParse(opening.Groups[2].Value[..^1], NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : 1;
        var items = new List<MdItem>();
        bool Sibling(string line) => ListItem.Match(line) is { Success: true } item && IndentOf(line) is var indent && indent >= margin && indent <= margin + 1 && char.IsDigit(item.Groups[2].Value[0]) == ordered;
        while (i < lines.Count && Sibling(lines[i]))
        {
            var item = ListItem.Match(lines[i]); var inner = margin + item.Groups[2].Length + 1;
            var content = new List<string> { item.Groups[3].Value }; i++;
            while (i < lines.Count)
            {
                w.Spend();
                var line = lines[i];
                if (string.IsNullOrWhiteSpace(line))
                {
                    // Blank lines stay in the item only when an indented line follows them; the whole run is taken at once, looked through one time.
                    var next = i + 1; while (next < lines.Count && string.IsNullOrWhiteSpace(lines[next])) next++;
                    if (next >= lines.Count || IndentOf(lines[next]) < inner) break;
                    for (; i < next; i++) content.Add("");
                    continue;
                }
                if (IndentOf(line) >= inner) { content.Add(Unindent(line, inner)); i++; continue; }
                if (ListItem.IsMatch(line) || Interrupts(line)) break;
                content.Add(line.Trim()); i++;
            }
            items.Add(new MdItem(ordinal++, Parse(w, content, depth + 1)));
            var after = i; while (after < lines.Count && string.IsNullOrWhiteSpace(lines[after])) after++;
            if (after < lines.Count && Sibling(lines[after])) i = after;
        }
        return new MdList(ordered, items);
    }

    /// <summary>
    /// Draws blocks as the Mac does (M/AgentTranscriptFormat.swift:351-423; the files preview in the measures
    /// of M/AgentMarkdownView.swift:133-229, where every block is <paramref name="gap"/> from the next):
    /// paragraphs 8pt apart, headings 22 / 18 / 15 / 13 semibold with 5 or 2pt over and 10 under, list items
    /// 6pt apart behind an accent marker hung 22pt out, a quote in ink2 beside its accent bar, a 20-dash rule.
    /// </summary>
    private static void Blocks(Writer w, List<Md> blocks, double indent, bool quoted, double gap, bool pictures)
    {
        var preview = w.Preview; var lead = preview ? PreviewLead : Lead; var body = preview ? DesignMetrics.Type.Body : w.Look.BodySize;
        // The files preview paints nothing behind its text, so a quote's bar is a mark hung before each of its paragraphs.
        void OpenLine(string text)
        {
            if (preview && quoted) { w.Open(Line(body, text) + lead, gap, indent - PreviewQuote, PreviewQuote); w.Run(w.Face(body, QuoteInk, 400, SymbolFont), "▎"); w.Body.Append(@"\tab "); }
            else w.Open(Line(body, text) + lead, gap, indent);
        }
        foreach (var block in blocks)
        {
            w.Spend();
            switch (block)
            {
                case MdParagraph paragraph:
                    OpenLine(paragraph.Text);
                    w.Body.Append(w.Face(body, quoted ? Ink2 : Ink)).Append(' '); Spans(w, paragraph.Text, new(body, quoted ? Ink2 : Ink, pictures)); w.Close(); Flush(w);
                    break;
                case MdSource source:
                    // A table too large to be one: its rows as they were written, no mark in them read as Markdown.
                    foreach (var line in source.Lines) { OpenLine(line); w.Run(w.Face(body, quoted ? Ink2 : Ink), line); w.Close(); }
                    break;
                case MdHeading heading:
                    var size = HeadingSize(heading.Level);
                    w.Open(Line(size, heading.Text) + lead, preview ? gap : 10, indent, spaceBefore: heading.Level <= 2 ? preview ? 6 : 5 : 2);
                    w.Body.Append(w.Face(size, Ink, 600)).Append(' '); Spans(w, heading.Text, new(size, Ink, pictures)); w.Close(); Flush(w);
                    break;
                case MdList list:
                    // The Mac's transcript parts every item, the last one too, by 6; the preview's list is one block, 7 between its items.
                    for (var index = 0; index < list.Items.Count; index++) Item(w, list, list.Items[index], indent, quoted, !preview ? 6 : index == list.Items.Count - 1 ? gap : 7, gap, pictures);
                    break;
                case MdCode code:
                    if (!preview && UserQuestionnaire.Parse(code.Text) is { } questions) Questionnaire(w, questions); else Code(w, code.Text, code.Language, indent, gap);
                    break;
                case MdQuote quote:
                    if (preview)
                    {
                        // A 3pt bar and 11pt to the words, 8pt between the quote's blocks (M/AgentMarkdownView.swift:169-175).
                        Blocks(w, quote.Children, indent + PreviewQuote, true, 8, pictures);
                        w.After(gap - 8);
                        break;
                    }
                    const double quotePad = DesignMetrics.Inset.Quote;
                    var bar = w.Begin(TranscriptBlockKind.Quote, QuoteBar + quotePad, quotePad, quotePad - Lead, quotePad + Lead);
                    Blocks(w, quote.Children, indent + 3, true, gap, pictures);
                    w.End(bar);
                    break;
                case MdRule:
                    if (preview)
                    {
                        // A hairline over the whole column, 4pt clear of the blocks round it (M/AgentMarkdownView.swift:176-177):
                        // the top edge of one empty cell, whose other edges take the card's colour (one left unsaid is drawn black).
                        w.After(4);
                        w.Body.Append(@"{\trowd\trgaph0\trleft").Append(Twips(w.Left + indent)).Append(@"\clbrdrt\brdrs\brdrw8\brdrcf").Append(Rule);
                        foreach (var side in "lbr") w.Body.Append(@"\clbrdr").Append(side).Append(@"\brdrs\brdrw8\brdrcf").Append(CardInk);
                        w.Body.Append(@"\cellx").Append(Twips(PreviewWidth)).Append(@"\pard\intbl\sb0\sa").Append(Twips(4 + gap - 1)).Append(@"\sl-15\slmult0\fs2 \cell\row}"); w.Row();
                        break;
                    }
                    w.Open(Line(body, null) + lead, 10, indent); w.Run(w.Face(body, Rule, 400, SymbolFont), new string('─', 20)); w.Close();
                    break;
                case MdTable table:
                    Table(w, table, indent, gap);
                    break;
            }
        }
    }

    /// <summary>
    /// One list item (M/AgentTranscriptFormat.swift:401-423): its marker in the accent ("•", the number, or a
    /// task's box) hung before the first paragraph, whose lines and whose other blocks sit 22pt in (26 in the
    /// files preview: a 17pt marker column and 9pt, M/AgentMarkdownView.swift:205-221).
    /// </summary>
    private static void Item(Writer w, MdList list, MdItem item, double indent, bool quoted, double after, double gap, bool pictures)
    {
        var preview = w.Preview; var lead = preview ? PreviewLead : Lead; var body = preview ? DesignMetrics.Type.Body : w.Look.BodySize;
        var hang = preview ? 26.0 : 22; var children = item.Children.ToList();
        var marker = list.Ordered ? item.Ordinal.ToString(CultureInfo.InvariantCulture) + "." : "•"; var font = BodyFont; var ink = preview && list.Ordered ? Ink2 : Accent;
        var first = children.FirstOrDefault() as MdParagraph;
        if (first is not null && first.Text.Length >= 4 && first.Text[..4].ToLowerInvariant() is "[x] " or "[ ] ")
        {
            // A task item: the ticked box in the accent, the empty one as the Mac draws it.
            var ticked = first.Text[1] is 'x' or 'X'; marker = ticked ? "☑" : "☐"; font = SymbolFont; if (preview && !ticked) ink = Ink2;
            first = new MdParagraph(first.Text[4..]);
        }
        var more = children.Count > (first is null ? 0 : 1);
        if (first is not null)
        {
            w.Open(Line(body, first.Text) + lead, preview && more ? 7 : after, indent, hang);
            w.Run(w.Face(body, ink, 400, font), marker); w.Body.Append(w.Face(body, Ink)).Append(@"\tab "); Spans(w, first.Text, new(body, Ink, pictures)); w.Close(); Flush(w);
            children.RemoveAt(0);
        }
        else { w.Open(Line(body, marker) + lead, 3, indent); w.Run(w.Face(body, ink, 400, font), marker); w.Close(); }
        if (children.Count > 0) Blocks(w, children, indent + hang, quoted, preview ? after : gap, pictures);
    }

    /// <summary>
    /// A table. In a transcript it takes the width in equal columns (M/AgentTranscriptFormat.swift:455-478):
    /// 12pt cells padded <c>Spacing.Sm</c>, hairlines all round, the head row semibold on the ink at 4%. The files preview
    /// sizes each column to its words, 85 to 260 (M/AgentMarkdownView.swift:265-304): cells padded 12 by 9,
    /// a line under each row and round the whole. RichEdit measures a row in twips, so a transcript's is
    /// drawn again when its box changes width.
    /// </summary>
    private static void Table(Writer w, MdTable table, double indent, double gap)
    {
        var columns = table.Alignments.Length; if (columns == 0) return;
        var preview = w.Preview; var lead = preview ? PreviewLead : Lead;
        double padX = preview ? 12 : DesignMetrics.Spacing.Sm, padY = preview ? 9 : DesignMetrics.Spacing.Sm;
        var left = w.Left + indent; var edges = new double[columns];
        if (preview)
        {
            var at = left;
            for (var column = 0; column < columns; column++)
            {
                // The Mac measures each cell at 12pt medium: about 6.4 epx a Latin letter and 11.6 a Hangul one.
                var widest = table.Rows.Max(row => column < row.Length ? row[column].Sum(c => Wide(c) ? 11.6 : 6.4) : 0);
                at += Math.Min(260, Math.Max(85, Math.Ceiling(widest) + 24)); edges[column] = at;
            }
        }
        else
        {
            var each = Math.Max(40, (w.Width - left - w.Right) / columns);
            for (var column = 0; column < columns; column++) edges[column] = left + each * (column + 1);
        }
        var pending = w.TakeBefore();
        for (var index = 0; index < table.Rows.Count; index++)
        {
            var row = table.Rows[index]; var head = index == 0; var last = index == table.Rows.Count - 1;
            static string Edge(string side, int ink = Rule) => @"\clbrdr" + side + @"\brdrs\brdrw8\brdrcf" + ink;
            // RichEdit draws an edge left unsaid in black, so every one is said. The files preview has no lines between
            // its columns (M/AgentMarkdownView.swift:272-289): those edges take the cell's own colour, its head's or the card's.
            var own = head ? Subtle : CardInk;
            w.Body.Append(@"{\trowd\trgaph").Append(Twips(padX)).Append(@"\trleft").Append(Twips(left));
            for (var column = 0; column < columns; column++)
            {
                w.Body.Append(Edge("t")).Append(Edge("l", preview && column > 0 ? own : Rule)).Append(Edge("b")).Append(Edge("r", preview && column < columns - 1 ? own : Rule));
                if (head) w.Body.Append(@"\clcbpat").Append(preview ? Subtle : HeadWash);
                w.Body.Append(@"\cellx").Append(Twips(edges[column]));
            }
            for (var column = 0; column < columns; column++)
            {
                var cell = column < row.Length ? row[column] : "";
                // The space a block opened just before the table still owes goes over its first row.
                w.Body.Append(@"\pard\intbl\sb").Append(Twips(padY - lead + (index == 0 ? pending : 0))).Append(@"\sa").Append(Twips(padY + lead)).Append(@"\sl-").Append(Twips(Line(12, cell) + lead)).Append(@"\slmult0");
                w.Body.Append(table.Alignments[column] switch { 'r' => @"\qr", 'c' => @"\qc", _ => @"\ql" }).Append(' ');
                w.Body.Append('{').Append(w.Face(12, Ink, head ? 600 : 400)).Append(' '); Spans(w, cell, new(12, Ink, false)); w.Body.Append(@"}\cell ");
            }
            w.Body.Append(@"\row}"); w.Row();
        }
        if (preview) { w.Open(1, gap - 1); w.Body.Append(@"\fs2 "); w.Close(); } else Separation(w);
    }

    /// <summary>What an inline run is drawn in: the size its code may not exceed, its ink, and whether pictures are drawn.</summary>
    private readonly record struct Style(double Size, int Ink, bool Pictures);

    /// <summary>
    /// The inline marks of one paragraph (M/AgentTranscriptFormat.swift:322-349): code in mono at most 12 (the
    /// accent on its soft tint on a card, the ink on a 5.5% wash in a Mighty block), bold, italic, struck
    /// through, and links in the accent. Each run sits in its own group, so the paragraph's face returns after it.
    /// </summary>
    private static void Spans(Writer w, string text, Style style, int depth = 0)
    {
        var body = w.Body; var position = 0; (int Start, int End, bool Path) word = default;
        w.Spend();
        foreach (Match match in Inline.Matches(text))
        {
            w.Spend();
            // No escape is read inside a Windows path: its backslashes stay, and the words round them stay in one piece for the reference drawn under them (InPath).
            if (match.Groups[1].Success && InPath(text, match.Index, ref word)) continue;
            w.Text(text[position..match.Index]); var value = match.Value; position = match.Index + match.Length;
            void Nested(string format, string inner) { body.Append('{').Append(format).Append(' '); if (depth < MaximumDepth) Spans(w, inner, style, depth + 1); else w.Text(inner); body.Append('}'); }
            if (match.Groups[1].Success) w.Text(value[1..]);
            else if (match.Groups[2].Success) { if (style.Pictures) InlinePicture(w, value); else w.Run(@"\cf" + Ink2, AgentMarkdownImages.Extract(value).FirstOrDefault()?.Alt ?? value); }
            else if (match.Groups[3].Success)
            {
                var code = value.StartsWith("``", StringComparison.Ordinal) ? value[2..^2].Trim() : value[1..^1];
                var face = @"\f" + MonoFont + @"\fs" + HalfPoints(Math.Min(style.Size, 12));
                w.Words(w.Preview ? face + @"\highlight" + Subtle : w.Cards ? face + @"\cf" + Accent + @"\highlight" + AccentSoft : face + @"\highlight" + CodeWash, code);
            }
            else if (match.Groups[4].Success) Nested(@"\b\i", value[3..^3]);
            else if (match.Groups[5].Success) Nested(@"\b", value[2..^2]);
            else if (match.Groups[6].Success) Nested(@"\i", value[1..^1]);
            else if (match.Groups[7].Success) Nested(@"\strike", value[2..^2]);
            else if (match.Groups[9].Success) w.Run(@"\cf" + Accent, value[1..^1]);
            else
            {
                // A web address is the accent, as on the Mac; the document opens nothing itself, so the address follows it.
                // A file in the workspace is underlined as well: a click on its path opens the preview.
                var split = value.IndexOf("](", StringComparison.Ordinal); var label = value[1..split]; var link = value[(split + 2)..^1].Trim();
                if (Uri.TryCreate(link, UriKind.Absolute, out var url) && url.Scheme is "https" or "http" or "mailto") { Nested(@"\cf" + Accent, label); w.Text(" (" + link + ")"); }
                else if (ResultFiles.LocalPath(link) is { } local) { Nested(@"\cf" + Accent + @"\ul", label); w.Run(@"\cf" + Accent + @"\ul", " (" + local + ")"); }
                else if (depth < MaximumDepth) Spans(w, label, style, depth + 1); else w.Text(label);
            }
        }
        w.Text(text[position..]);
    }

    /// <summary>
    /// A Markdown picture under the macOS rule: a file inside the workspace or temporary
    /// folder, or a data URI, is drawn after the line; an http(s) picture is a link that is
    /// never fetched; anything else keeps its alt text and says why it is not shown.
    /// </summary>
    private static void InlinePicture(Writer w, string value)
    {
        var context = w.Context;
        var picture = AgentMarkdownImages.Extract(value).FirstOrDefault();
        if (picture is null) { w.Text(value); return; }
        // The note beside a picture that is not drawn is two points smaller than the words, never under 10 (M/AgentTranscriptFormat.swift:264).
        var alt = picture.Alt.Trim(); var quiet = @"\cf" + Ink2; var note = @"\fs" + HalfPoints(Math.Max(10, w.Look.BodySize - 2)) + quiet;
        // With pictures, the location comes from AgentPictures' cache: a render never asks the disk.
        AgentImageLocation location; AgentPictures.Picture? shown = null;
        if (context.Pictures is null) location = AgentImagePaths.Locate(picture.Source, context.Root);
        else if (context.Pictures.Locate(picture.Source, context.Root) is { } found) (location, shown) = found;
        else
        {
            // Still being found: the alt text now, the picture (or its link) once it is known.
            if (alt.Length > 0) w.Run(quiet, alt);
            context.Pending.Add((new(AgentPictures.State.Loading), null));
            return;
        }
        switch (location)
        {
            case AgentImageLocation.Remote remote:
                w.Run(@"\cf" + Accent, alt.Length > 0 ? alt : remote.Url.ToString()); w.Text(" (" + remote.Url + ")");
                w.Run(note, " (" + Locale.Get("images.remote") + ")");
                break;
            case AgentImageLocation.File or AgentImageLocation.Inline when context.Pictures is not null:
                if (alt.Length > 0) w.Run(quiet, alt);
                context.Pending.Add((shown ?? new(AgentPictures.State.Missing), new("image", picture.Source)));
                break;
            default:
                // The picture's name stays in the words' ink and says why it is not shown.
                w.Text(alt.Length > 0 ? alt : picture.Source.Length > 120 ? picture.Source[..120] : picture.Source);
                w.Run(note, " (" + Locale.Get("images.refused") + ")");
                break;
        }
    }

    internal static string Escape(string text) { var output = new StringBuilder(); Escape(output, text); return output.ToString(); }
    /// <summary>Text as RTF: every mark escaped, a line break kept inside its paragraph, the rest by code unit.</summary>
    private static void Escape(StringBuilder output, string text)
    {
        foreach (var c in text)
        {
            if (c is '\\' or '{' or '}') output.Append('\\').Append(c);
            else if (c == '\t') output.Append(@"\tab ");
            else if (c is '\n' or (char)0x2028 or (char)0x2029 or (char)0x85) output.Append(@"\line ");
            else if (c < ' ') { }
            else if (c <= 127) output.Append(c);
            else output.Append(@"\u").Append(unchecked((short)c)).Append('?');
        }
    }
}
