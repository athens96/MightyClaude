namespace MightyClaude.Core;

/// Port of macOS MightyGraphLayout.
public sealed class MightyGraphLayout
{
    public sealed record Node(string Id, string Kind, GraphRect Frame);
    public sealed record Edge(string Source, string Target, bool Joins = false);

    public List<Node> Nodes { get; init; } = [];
    public List<Edge> Edges { get; init; } = [];
    public (double W, double H) Size { get; set; }
    public double OriginX { get; set; }
    /// Older requests loaded from the session record sit above the first
    /// retained one, at negative y, so loading them moves nothing already on
    /// screen. The drawn canvas starts here, as it does at <see cref="OriginX"/>.
    public double OriginY { get; set; }
    public string? FittedResultID { get; set; }
    /// The newest result card's node id when the viewport limit, not its saved
    /// size or window fit, decides its size in some direction: it then follows
    /// the pane as a fitted card does. Null without a limit.
    public string? ViewportBoundResultID { get; set; }
    /// The viewport limit on the newest result card (<see cref="ResultViewportLimit"/>)
    /// and the window fit in force (<see cref="ResultFitSize"/>), for a drag of
    /// it; null without a viewport (the limit also without a zoom) or a result.
    public (double W, double H)? ResultLimit { get; set; }
    public (double W, double H)? ResultWindowFit { get; set; }

    private const double SiblingGap = 32;
    private const double RowGap = 52;
    /// The top of the diagram: loads older requests from the session record.
    public const string HistoryNodeID = "history-top";
    public const double HistoryWidth = 380;
    public const double HistoryHeight = 44;

    public static bool Terminal(string state) =>
        state is "completed" or "error" or "failed" or "stopped" or "cancelled" or "interrupted";

    public static bool Finished(MightyGraphRun run) =>
        Terminal(run.Status) && run.Agents.All(a => Terminal(a.Status));

    public static string NodeID(MightyGraphRun run, string suffix) =>
        MightyGraphBlockSize.NodeId(run.Id, suffix);

    public static string? LatestResultID(IReadOnlyList<MightyGraphRun> runs)
    {
        for (var i = runs.Count - 1; i >= 0; i--)
            if (Finished(runs[i])) return NodeID(runs[i], "result");
        return null;
    }

    public static string? FittedResultId(IReadOnlyList<MightyGraphRun> runs, (double W, double H)? viewport, bool hasSharedResultSize) =>
        (viewport is not null && !hasSharedResultSize) ? LatestResultID(runs) : null;

    // ── the newest result card within the pane (macOS 1d5a0db) ─────────────

    public const double MinimumResultHeight = MightyGraphBlockSize.MinimumHeight;

    /// The window fit: the canvas less 24pt margins and, with the result files
    /// panel open, that panel; never below 500 × 200.
    public static (double W, double H) ResultFitSize((double W, double H) viewport, bool filesPanelOpen) =>
        (Math.Max(500, viewport.W - 48 - (filesPanelOpen ? 336 : 0)), Math.Max(200, viewport.H - 48));

    /// The most of the pane the newest result card may take, in diagram
    /// coordinates at <paramref name="zoom"/>: the viewport less the 24pt
    /// margins (screen points, so they stay 24pt at any zoom) and, with the
    /// result files panel open, that panel and its gap. Never below a dragged
    /// block's minimum, so a tiny pane keeps a usable card. A broken zoom is 100%.
    public static (double W, double H) ResultViewportLimit((double W, double H) viewport, double zoom, bool filesPanelOpen)
    {
        var scale = double.IsFinite(zoom) && zoom > 0 ? zoom : 1;
        return (Math.Max(MightyGraphBlockSize.MinimumWidth, (viewport.W - 48) / scale - (filesPanelOpen ? 336 : 0)),
                Math.Max(MinimumResultHeight, (viewport.H - 48) / scale));
    }

    /// The cap in force: <paramref name="maximum"/> (the saved size, else the
    /// window fit) kept within <paramref name="limit"/> side by side; null keeps
    /// it whole. Only what is drawn shrinks — the saved size is left as it is,
    /// so the card grows back to it when the pane does.
    public static (double W, double H) ResultCap((double W, double H) maximum, (double W, double H)? limit) =>
        limit is { } l ? (Math.Min(maximum.W, l.W), Math.Min(maximum.H, l.H)) : maximum;

    /// The newest result card's size (macOS <c>MightyGraphLayout.resultSize</c>).
    /// <paramref name="cap"/> is the saved size or, with none saved, the window
    /// fit, both kept within the pane: a remembered maximum, not a fixed size.
    /// The card is as tall as its content up to that cap, where its answer
    /// scrolls inside, and never shorter than <see cref="MinimumResultHeight"/>.
    /// Content not measured yet (or a broken measurement) takes the cap. The
    /// width stays the cap's: the answer wraps to whatever width it is given.
    public static (double W, double H) ResultSize((double W, double H) cap, double? contentHeight)
    {
        if (contentHeight is not { } content || !double.IsFinite(content) || content < 0) return cap;
        return (cap.W, Math.Min(cap.H, Math.Max(MinimumResultHeight, Math.Ceiling(content))));
    }

    /// Which sides a corner drag of the newest result card moved. The card
    /// starts from the size it shows — shorter than the saved maximum when it
    /// fits a short answer — so a side the pointer did not move must keep the
    /// maximum (<see cref="ResultDrag"/>), never the shrunk size it showed. A
    /// press and release without moving moves nothing and saves nothing new.
    public static (bool Horizontal, bool Vertical) ResultDragSides((double W, double H) start, (double W, double H) dragged) =>
        (Math.Abs(dragged.W - start.W) > 0.5, Math.Abs(dragged.H - start.H) > 0.5);

    /// Whether the result files panel is open beside the newest result card.
    public static bool FilesPanelOpen(IReadOnlyList<MightyGraphRun> runs, string? resultFilesRunID)
    {
        for (var i = runs.Count - 1; i >= 0; i--)
            if (Finished(runs[i])) return resultFilesRunID is not null && runs[i].Id == resultFilesRunID && runs[i].Status == "completed";
        return false;
    }

    /// Where a block drag is: still moving, released, or abandoned (Escape,
    /// the pointer lost) with the block back at its starting size.
    public enum ResizePhase { Live, Finished, Cancelled }

    /// A drag of the newest result card, both what it shows and what it saves
    /// (macOS <c>MightyGraphLayout.resultDrag</c>). <paramref name="dragged"/> is
    /// the size under the cursor; <paramref name="horizontal"/>/<paramref name="vertical"/>
    /// say which sides the drag moves; <paramref name="limit"/> is the pane's
    /// and <paramref name="windowFit"/> the window fit in force.
    ///
    /// Live: the dragged size kept within the limit, so the card follows the
    /// cursor up to the pane's edge and stops there. Save (only on release,
    /// null when cancelled), side by side: a side the drag did not move keeps
    /// the maximum in force (saved, else the window fit); a moved side released
    /// at or past the limit keeps the larger of that maximum and the limit, so a
    /// narrow pane never overwrites a larger saved size; a moved side released
    /// inside the limit saves what it was released at.
    public static (GraphBlockSize? Live, GraphBlockSize? Save) ResultDrag(
        (double W, double H) dragged, bool horizontal, bool vertical, ResizePhase phase,
        GraphBlockSize? saved, (double W, double H)? limit, (double W, double H)? windowFit)
    {
        switch (phase)
        {
            case ResizePhase.Cancelled: return (null, null);
            case ResizePhase.Live:
                var shown = ResultCap(dragged, limit);
                return (new GraphBlockSize(shown.W, shown.H).Normalized, null);
            default:
                static double Side(bool moved, double dragged, double? saved, double? fit, double? limit)
                {
                    var maximum = saved ?? fit;
                    if (!moved) return maximum ?? dragged;
                    if (limit is not { } l || dragged < l - 0.5) return dragged;
                    return Math.Max(maximum ?? l, l);
                }
                var width = Side(horizontal, dragged.W, saved?.Width, windowFit?.W, limit?.W);
                var height = Side(vertical, dragged.H, saved?.Height, windowFit?.H, limit?.H);
                return (null, new GraphBlockSize(width, height).Normalized);
        }
    }

    private sealed class Tree
    {
        public List<Node> Nodes = [];
        public List<Edge> Edges = [];
        public double Width;
        public double Height;
        public string Top = "";
        public List<string> Leaves = [];
        public void Offset(double dx, double dy)
        {
            for (var i = 0; i < Nodes.Count; i++)
                Nodes[i] = Nodes[i] with { Frame = Nodes[i].Frame.OffsetBy(dx, dy) };
        }
    }

    public static MightyGraphLayout Make(
        IReadOnlyList<MightyGraphRun> runs,
        string draft,
        bool running,
        IReadOnlySet<string> expanded,
        string? resultFilesRunID = null,
        (double W, double H)? viewport = null,
        bool hasSharedResultSize = false,
        double? zoom = null,
        GraphBlockSize? sharedResultSize = null,
        int retainedStart = 0,
        bool history = false,
        double? resultContentHeight = null,
        IReadOnlyDictionary<string, GraphBlockSize>? blockSizes = null)
    {
        var latestResultID = LatestResultID(runs);
        var filesPanelOpenForLatest = FilesPanelOpen(runs, resultFilesRunID);
        var shared = sharedResultSize?.Normalized is { } savedSize ? (savedSize.Width, savedSize.Height) : ((double W, double H)?)null;
        hasSharedResultSize |= shared is not null;

        (double W, double H)? autoFitResultSize = viewport is { } vp && latestResultID is not null
            ? ResultFitSize(vp, filesPanelOpenForLatest) : null;
        // With a zoom the newest result card never exceeds the pane (macOS);
        // without one (the parity vectors) its cap stays whole.
        (double W, double H)? resultLimit = viewport is { } limitViewport && zoom is { } z && latestResultID is not null
            ? ResultViewportLimit(limitViewport, z, filesPanelOpenForLatest) : null;
        var resultMaximum = shared ?? (hasSharedResultSize ? null : autoFitResultSize);

        (double W, double H) Size(string id, double defaultW, double defaultH)
        {
            if (latestResultID is not null && id == latestResultID && viewport is not null)
            {
                // The cap is the most it takes; it shrinks to its measured content.
                if (resultMaximum is { } maximum) return ResultSize(ResultCap(maximum, resultLimit), resultContentHeight);
                if (hasSharedResultSize) return (defaultW, defaultH);
            }
            if (blockSizes?.GetValueOrDefault(id)?.Normalized is { } custom) return (custom.Width, custom.Height);
            return (defaultW, defaultH);
        }

        var trees = new List<Tree>();
        for (var runIndex = 0; runIndex < runs.Count; runIndex++)
        {
            var run = runs[runIndex];
            var mainID = NodeID(run, "request");
            var mainSize = Size(mainID, MightyGraphCamera.RequestWidth, expanded.Contains(mainID) ? 540 : 280);
            var mainHeight = mainSize.H;
            var resultID = NodeID(run, "result");
            var resultSize = Size(resultID, MightyGraphCamera.RequestWidth, expanded.Contains(resultID) ? 440 : 200);

            var visited = new HashSet<int>();
            var indexes = new Dictionary<string, int>();
            for (var ai = 0; ai < run.Agents.Count; ai++)
                if (!indexes.ContainsKey(run.Agents[ai].Id)) indexes[run.Agents[ai].Id] = ai;

            Tree? Branch(int index, int depth = 0)
            {
                if (depth >= 64 || !visited.Add(index)) return null;
                var agent = run.Agents[index];
                var id = NodeID(run, "agent:" + agent.Id);
                var cardSize = Size(id, 360, expanded.Contains(id) ? 480 : 240);
                var height = cardSize.H;
                var children = new List<Tree>();
                for (var ci = 0; ci < run.Agents.Count; ci++)
                {
                    if (ci != index && run.Agents[ci].ParentID == agent.Id)
                    {
                        var child = Branch(ci, depth + 1);
                        if (child is not null) children.Add(child);
                    }
                }
                var childrenWidth = children.Sum(c => c.Width) + Math.Max(0, children.Count - 1) * SiblingGap;
                var width = Math.Max(cardSize.W, childrenWidth);
                var node = new Node(id, "agent", new((width - cardSize.W) / 2, 0, cardSize.W, height));
                var result = new Tree { Nodes = [node], Width = width, Height = height, Top = id, Leaves = [id] };
                if (children.Count > 0)
                {
                    result.Leaves = [];
                    var x = (width - childrenWidth) / 2;
                    foreach (var child in children)
                    {
                        child.Offset(x, height + RowGap);
                        result.Nodes.AddRange(child.Nodes);
                        result.Edges.Add(new Edge(id, child.Top));
                        result.Edges.AddRange(child.Edges);
                        result.Leaves.AddRange(child.Leaves);
                        result.Height = Math.Max(result.Height, height + RowGap + child.Height);
                        x += child.Width + SiblingGap;
                    }
                }
                return result;
            }

            var branches = new List<Tree>();
            for (var ai = 0; ai < run.Agents.Count; ai++)
            {
                var parent = run.Agents[ai].ParentID;
                if (parent is null || !indexes.ContainsKey(parent) || parent == run.Agents[ai].Id)
                {
                    var t = Branch(ai);
                    if (t is not null) branches.Add(t);
                }
            }
            for (var ai = 0; ai < run.Agents.Count; ai++)
            {
                if (!visited.Contains(ai)) { var t = Branch(ai); if (t is not null) branches.Add(t); }
            }

            var branchWidth = branches.Sum(b => b.Width) + Math.Max(0, branches.Count - 1) * SiblingGap;
            var treeWidth = Math.Max(mainSize.W, Math.Max(branchWidth, Finished(run) ? resultSize.W : 0));
            var mainNode = new Node(mainID, "request", new((treeWidth - mainSize.W) / 2, 0, mainSize.W, mainHeight));
            var tree = new Tree { Nodes = [mainNode], Width = treeWidth, Height = mainHeight, Top = mainID, Leaves = [mainID] };
            if (branches.Count > 0)
            {
                tree.Leaves = [];
                var x = (treeWidth - branchWidth) / 2;
                foreach (var branch in branches)
                {
                    branch.Offset(x, mainHeight + RowGap);
                    tree.Nodes.AddRange(branch.Nodes);
                    tree.Edges.Add(new Edge(mainID, branch.Top));
                    tree.Edges.AddRange(branch.Edges);
                    tree.Leaves.AddRange(branch.Leaves);
                    tree.Height = Math.Max(tree.Height, mainHeight + RowGap + branch.Height);
                    x += branch.Width + SiblingGap;
                }
            }
            if (Finished(run))
            {
                var height = resultSize.H;
                tree.Nodes.Add(new Node(resultID, "result", new((treeWidth - resultSize.W) / 2, tree.Height + RowGap, resultSize.W, height)));
                tree.Edges.AddRange(tree.Leaves.Select(l => new Edge(l, resultID, true)));
                tree.Leaves = [resultID];
                tree.Height += RowGap + height;
            }
            trees.Add(tree);
        }

        var hasPending = (!running && (runs.Count == 0 || runs[^1] is var lastRun && Finished(lastRun) != false)) || !string.IsNullOrEmpty(draft);
        if (hasPending)
        {
            var pendingID = MightyGraphCamera.PendingNodeID;
            var pendingSize = Size(pendingID, MightyGraphCamera.RequestWidth, expanded.Contains(pendingID) ? 280 : 140);
            trees.Add(new Tree
            {
                Nodes = [new Node(pendingID, "draft", new(0, 0, pendingSize.W, pendingSize.H))],
                Width = pendingSize.W,
                Height = pendingSize.H,
                Top = pendingID,
                Leaves = [pendingID]
            });
        }

        var layout = new MightyGraphLayout();
        // The first retained tree starts where the first tree always started;
        // the older ones, loaded from the session record, stack upward from it.
        var split = Math.Min(Math.Max(0, retainedStart), runs.Count);
        var tops = new double[trees.Count];
        var y = 24.0;
        for (var i = split; i < trees.Count; i++) { tops[i] = y; y += trees[i].Height + RowGap; }
        var above = 24.0;
        for (var i = split - 1; i >= 0; i--) { above -= trees[i].Height + RowGap; tops[i] = above; }
        var previous = new List<string>();
        for (var i = 0; i < trees.Count; i++)
        {
            var tree = trees[i];
            tree.Offset(MightyGraphCamera.X(tree.Width), tops[i]);
            layout.Nodes.AddRange(tree.Nodes);
            layout.Edges.AddRange(previous.Select(p => new Edge(p, tree.Top, true)));
            layout.Edges.AddRange(tree.Edges);
            previous = tree.Leaves;
        }
        var bottom = split < trees.Count ? y - RowGap : 24 - RowGap;
        if (history)
        {
            var top = layout.Nodes.Count > 0 ? layout.Nodes.Min(n => n.Frame.Y) : 24;
            layout.Nodes.Insert(0, new Node(HistoryNodeID, "history", new(MightyGraphCamera.CentreX - HistoryWidth / 2, top - 28 - HistoryHeight, HistoryWidth, HistoryHeight)));
        }

        var leading = layout.Nodes.Count > 0 ? layout.Nodes.Min(n => n.Frame.X) : MightyGraphCamera.X(MightyGraphCamera.RequestWidth);
        var trailing = layout.Nodes.Count > 0 ? layout.Nodes.Max(n => n.Frame.MaxX) : MightyGraphCamera.CentreX + MightyGraphCamera.RequestWidth / 2;
        layout.OriginX = MightyGraphCamera.OriginX(leading);
        layout.OriginY = Math.Min(0, (layout.Nodes.Count > 0 ? layout.Nodes.Min(n => n.Frame.Y) : 24) - 24);
        layout.Size = (MightyGraphCamera.CanvasWidth(leading, trailing),
            Math.Max(188, Math.Max(bottom, layout.Nodes.Count > 0 ? layout.Nodes.Max(n => n.Frame.MaxY) : 0) + 24 - layout.OriginY));

        if (resultFilesRunID is not null)
        {
            var panelRunIndex = -1;
            for (var i = 0; i < runs.Count; i++) if (runs[i].Id == resultFilesRunID) { panelRunIndex = i; break; }
            if (panelRunIndex >= 0 && runs[panelRunIndex].Status == "completed" && Finished(runs[panelRunIndex]))
            {
                var resultNode = layout.Nodes.FirstOrDefault(n => n.Kind == "result" && n.Id == NodeID(runs[panelRunIndex], "result"));
                if (resultNode is not null)
                {
                    var panelID = NodeID(runs[panelRunIndex], "result-files");
                    var frame = new GraphRect(resultNode.Frame.MaxX + 16, resultNode.Frame.Y, 320, resultNode.Frame.H);
                    layout.Nodes.Add(new Node(panelID, "resultFiles", frame));
                    layout.Size = (Math.Max(layout.Size.W, frame.MaxX + 24 - layout.OriginX), layout.Size.H);
                }
            }
        }
        layout.FittedResultID = FittedResultId(runs, viewport, hasSharedResultSize);
        layout.ResultLimit = resultLimit;
        layout.ResultWindowFit = autoFitResultSize;
        if (viewport is not null && resultMaximum is { } bounded && ResultCap(bounded, resultLimit) != bounded)
            layout.ViewportBoundResultID = latestResultID;
        return layout;
    }
}
