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
    /// The same two for the pending plan block (<see cref="PlanSize"/>), for a drag
    /// of it; null without a plan block or a viewport (the limit also without a zoom).
    public (double W, double H)? PlanLimit { get; set; }
    public (double W, double H)? PlanWindowFit { get; set; }

    private const double SiblingGap = 20;
    private const double RowGap = 32;
    /// The top of the diagram: loads older requests from the session record.
    public const string HistoryNodeID = "history-top";
    public const double HistoryWidth = 380;
    public const double HistoryHeight = 44;
    /// <summary>A run's pending plan approval, in the flow where its result will go (macOS planSuffix).</summary>
    public const string PlanSuffix = "plan";
    /// <summary>The plan block without a viewport (the parity vectors); with one it fits the pane (<see cref="PlanSize"/>).</summary>
    public const double PlanWidth = 640;
    public const double PlanHeight = 520;
    /// <summary>
    /// The least a drag makes the plan block (macOS planMinimumSize): only the plan is in it (its answers are in the
    /// composer), so as small as any block.
    /// </summary>
    public const double PlanMinimumWidth = MightyGraphBlockSize.MinimumWidth;
    public const double PlanMinimumHeight = MightyGraphBlockSize.MinimumHeight;
    /// <summary>An answered plan's block, attached beside its request (macOS planRecordSuffix).</summary>
    public const string PlanRecordSuffix = "plan-record:";
    public const double PlanRecordWidth = 320;
    public static double PlanRecordHeight(bool expanded) => expanded ? 420 : 104;
    /// <summary>The 실행 block under a request (<see cref="Make"/> <c>splitsExecution</c>): its node id suffix (macOS workSuffix) and its node kind (macOS <c>Content.work</c>).</summary>
    public const string WorkSuffix = "execution";
    public const string WorkKind = "work";
    /// <summary>A background execution's block, attached beside the block that started it (macOS <c>Content.execution</c>, executionSuffix).</summary>
    public const string ExecutionKind = "execution";
    public const string ExecutionSuffix = "ouroboros-execution:";
    public const double ExecutionWidth = 380;
    /// <summary>Collapsed, it holds the goal, progress, counts, one phase line, a three-line note and the footer without clipping (macOS executionHeight).</summary>
    public static double ExecutionHeight(bool expanded) => expanded ? 470 : 290;
    /// <summary>A step's picture preview, attached beside the block that produced them (macOS <c>Content.images</c>, imagesSuffix).</summary>
    public const string ImagesKind = "images";
    public const string ImagesSuffix = "images:";
    public const double ImagesWidth = 304;
    /// <summary>Thumbnails per row and on screen: two rows folded, four unfolded; the last visible tile carries "+k" for the rest (macOS imagesColumns, visibleImages).</summary>
    public const int ImagesColumns = 3;
    public static int VisibleImages(int count, bool expanded) => Math.Min(count, ImagesColumns * (expanded ? 4 : 2));
    public const double ImagesTile = 88;
    public static double ImagesHeight(int count, bool expanded)
    {
        var rows = Math.Max(1, (VisibleImages(count, expanded) + ImagesColumns - 1) / ImagesColumns);
        return 38 + 24 + rows * ImagesTile + (rows - 1) * 8;
    }
    /// <summary>The height of a request block that holds its input alone, over its 실행 block (macOS requestHeight).</summary>
    public static double RequestHeight(bool expanded) => expanded ? 280 : 140;

    /// <summary>The least a drag may make a block of <paramref name="kind"/> (macOS Node.minimumSize).</summary>
    /// An answered plan's least is its folded height, so its first drag starts where it stands.
    public static (double W, double H) MinimumBlockSize(string kind) => kind switch
    {
        "plan" => (PlanMinimumWidth, PlanMinimumHeight),
        "planRecord" => (MightyGraphBlockSize.MinimumWidth, PlanRecordHeight(false)),
        _ => (MightyGraphBlockSize.MinimumWidth, MightyGraphBlockSize.MinimumHeight),
    };
    private const double AttachmentGap = 8;

    /// <summary>Attachments beside the flow: scrolled at once, with no selection (macOS Node.isAuxiliary).</summary>
    public static bool IsAuxiliary(string kind) => kind is "resultFiles" or "planRecord" or ExecutionKind or ImagesKind;
    /// <summary>
    /// Blocks with the corner handle and the edge bands (macOS Node.isResizable): the flow's own blocks, and the
    /// answered plans although they hang beside it.
    /// </summary>
    public static bool IsResizable(string kind) => kind is "request" or WorkKind or "agent" or "result" or "draft" or "plan" or "planRecord";
    /// <summary>The block that holds a point given in node coordinates, the first laid out when two overlap (macOS <c>frames.first { contains }</c>).</summary>
    public Node? NodeAt(double x, double y) => Nodes.FirstOrDefault(n => x >= n.Frame.X && x < n.Frame.MaxX && y >= n.Frame.Y && y < n.Frame.MaxY);

    public static bool Terminal(string state) =>
        state is "completed" or "error" or "failed" or "stopped" or "cancelled" or "interrupted";

    public static bool Finished(MightyGraphRun run) =>
        Terminal(run.Status) && run.Agents.All(a => Terminal(a.Status));

    public static string NodeID(MightyGraphRun run, string suffix) =>
        MightyGraphBlockSize.NodeId(run.Id, suffix);

    /// The block the camera stands on for the newest request: its 실행 block while
    /// it runs, where its work shows, else the request; null without a request
    /// (macOS <c>newestRequestTargetID</c>).
    public string? NewestRequestTargetID(IReadOnlyList<MightyGraphRun> runs)
    {
        if (runs.Count == 0) return null;
        var work = NodeID(runs[^1], WorkSuffix);
        return !Finished(runs[^1]) && Nodes.Any(n => n.Id == work) ? work : NodeID(runs[^1], "request");
    }

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

    /// The pending plan block's size, by the newest result card's rules (macOS <c>MightyGraphLayout.planSize</c>):
    /// the size the user last dragged it to (<paramref name="saved"/>, one per pane) or, with none, the window fit
    /// is the most it takes, kept within the pane at <paramref name="zoom"/> so it shrinks and grows back with the
    /// pane. The plan scrolls inside, so it never shrinks to its content. Without a viewport it is the saved size
    /// or <see cref="PlanWidth"/> × <see cref="PlanHeight"/>. Also returns the limit and the window fit in force,
    /// for a drag of it (<see cref="ResultDrag"/>).
    public static ((double W, double H) Size, (double W, double H)? Limit, (double W, double H)? WindowFit) PlanSize(
        GraphBlockSize? saved, (double W, double H)? viewport, double? zoom)
    {
        var savedSize = saved?.Normalized is { } s ? ((double W, double H)?)(s.Width, s.Height) : null;
        if (viewport is not { } vp) return (savedSize ?? (PlanWidth, PlanHeight), null, null);
        // No files panel stands beside a plan.
        var fit = ResultFitSize(vp, false);
        (double W, double H)? limit = zoom is { } z ? ResultViewportLimit(vp, z, false) : null;
        return (ResultCap(savedSize ?? fit, limit), limit, fit);
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

    /// <paramref name="splitsExecution"/> is the diagram's own (macOS <c>make(splitsExecution:)</c>): a request block
    /// holds its input alone and a 실행 block (<see cref="WorkKind"/>) under it holds the main agent's work, with the
    /// sub-agents and the answered plans hanging off it. Off, the request block holds both, as the shared graph
    /// vectors (native/contracts/graph-vectors.json) lay it out. <paramref name="galleries"/> are the steps that produced
    /// pictures (<see cref="MightyGraphImages.Galleries"/>) and <paramref name="executions"/> the background executions a
    /// request's agents started (<see cref="OuroborosExecutionLinks"/>): each gets a block attached beside the flow.
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
        IReadOnlyDictionary<string, GraphBlockSize>? blockSizes = null,
        string? planRunID = null,
        IReadOnlyList<(string RunID, string RecordID)>? planRecords = null,
        GraphBlockSize? planSize = null,
        bool splitsExecution = false,
        IReadOnlyList<(string RunID, string Step, int Count)>? galleries = null,
        IReadOnlyList<(string RunID, string Key)>? executions = null)
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
            if (blockSizes?.GetValueOrDefault(id)?.NormalizedFor(id) is { } custom) return (custom.Width, custom.Height);
            return (defaultW, defaultH);
        }

        var trees = new List<Tree>();
        ((double W, double H) Size, (double W, double H)? Limit, (double W, double H)? WindowFit)? planFit = null;
        for (var runIndex = 0; runIndex < runs.Count; runIndex++)
        {
            var run = runs[runIndex];
            var mainID = NodeID(run, "request");
            var mainSize = Size(mainID, MightyGraphCamera.RequestWidth, splitsExecution ? RequestHeight(expanded.Contains(mainID)) : expanded.Contains(mainID) ? 540 : 280);
            var mainHeight = mainSize.H;
            var workID = NodeID(run, WorkSuffix);
            (double W, double H)? workSize = splitsExecution ? Size(workID, MightyGraphCamera.RequestWidth, expanded.Contains(workID) ? 540 : 280) : null;
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
                var cardSize = Size(id, 360, expanded.Contains(id) ? 480 : agent.IsTask && agent.NoticeReply is not null ? 320 : 240);
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
            var treeWidth = Math.Max(Math.Max(mainSize.W, workSize?.W ?? 0), Math.Max(branchWidth, Finished(run) ? resultSize.W : 0));
            var mainNode = new Node(mainID, "request", new((treeWidth - mainSize.W) / 2, 0, mainSize.W, mainHeight));
            var tree = new Tree { Nodes = [mainNode], Width = treeWidth, Height = mainHeight, Top = mainID, Leaves = [mainID] };
            // The block the sub-agents branch from: the 실행 block right under the request, or the request itself.
            var parentID = mainID;
            if (workSize is { } work)
            {
                tree.Nodes.Add(new Node(workID, WorkKind, new((treeWidth - work.W) / 2, mainHeight + RowGap, work.W, work.H)));
                tree.Edges.Add(new Edge(mainID, workID));
                tree.Leaves = [workID];
                tree.Height += RowGap + work.H;
                parentID = workID;
            }
            if (branches.Count > 0)
            {
                tree.Leaves = [];
                var top = tree.Height + RowGap;
                var x = (treeWidth - branchWidth) / 2;
                foreach (var branch in branches)
                {
                    branch.Offset(x, top);
                    tree.Nodes.AddRange(branch.Nodes);
                    tree.Edges.Add(new Edge(parentID, branch.Top));
                    tree.Edges.AddRange(branch.Edges);
                    tree.Leaves.AddRange(branch.Leaves);
                    tree.Height = Math.Max(tree.Height, top + branch.Height);
                    x += branch.Width + SiblingGap;
                }
            }
            // A plan waiting for an answer goes where the result will: the run
            // is still going, so nothing is below it yet.
            if (run.Id == planRunID && !Finished(run))
            {
                var planID = NodeID(run, PlanSuffix);
                var fit = PlanSize(planSize, viewport, zoom);
                planFit = fit;
                var planBlock = fit.Size;
                if (planBlock.W > tree.Width)
                {
                    tree.Offset((planBlock.W - tree.Width) / 2, 0);
                    tree.Width = planBlock.W;
                }
                tree.Nodes.Add(new Node(planID, "plan", new((tree.Width - planBlock.W) / 2, tree.Height + RowGap, planBlock.W, planBlock.H)));
                tree.Edges.AddRange(tree.Leaves.Select(l => new Edge(l, planID, true)));
                tree.Leaves = [planID];
                tree.Height += RowGap + planBlock.H;
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
        int RunIndex(string runID) { for (var i = 0; i < runs.Count; i++) if (runs[i].Id == runID) return i; return -1; }
        double RightOf(double top, double height, GraphRect owner)
        {
            var sharing = layout.Nodes.Where(n => n.Frame.Y < top + height && n.Frame.MaxY > top).Select(n => n.Frame.MaxX).ToList();
            return Math.Max(sharing.Count > 0 ? sharing.Max() : owner.MaxX, owner.MaxX) + AttachmentGap;
        }
        void Grow(GraphRect frame) => layout.Size = (Math.Max(layout.Size.W, frame.MaxX + 24 - layout.OriginX), Math.Max(layout.Size.H, frame.MaxY + 24 - layout.OriginY));
        // A picture preview hangs off the top of the step that produced it, right of every card it would share rows
        // with: an attachment, like the file list (no edge, no centreline, nothing already placed moves).
        foreach (var gallery in galleries ?? [])
        {
            if (gallery.Count <= 0 || RunIndex(gallery.RunID) is not (>= 0 and var runIndex)) continue;
            // The request step's pictures come from the main agent's records: with a 실행 block they hang off it.
            var stepID = NodeID(runs[runIndex], splitsExecution && gallery.Step == MightyGraphImages.RequestStep ? WorkSuffix : gallery.Step);
            if (layout.Nodes.FirstOrDefault(n => n.Id == stepID && !IsAuxiliary(n.Kind)) is not { } step) continue;
            var id = NodeID(runs[runIndex], ImagesSuffix + gallery.Step);
            if (layout.Nodes.Any(n => n.Id == id)) continue;
            var height = ImagesHeight(gallery.Count, expanded.Contains(id));
            var frame = new GraphRect(RightOf(step.Frame.Y, height, step.Frame), step.Frame.Y, ImagesWidth, height);
            layout.Nodes.Add(new Node(id, ImagesKind, frame));
            Grow(frame);
        }
        // Execution blocks hang off their request's top (its 실행 block's, when there is one: the main agent started
        // them), stacked, right of every card they would share rows with.
        var nextY = new Dictionary<int, double>();
        foreach (var execution in executions ?? [])
        {
            if (RunIndex(execution.RunID) is not (>= 0 and var runIndex)) continue;
            if (layout.Nodes.FirstOrDefault(n => splitsExecution ? n.Kind == WorkKind && n.Id == NodeID(runs[runIndex], WorkSuffix) : n.Kind == "request" && n.Id == NodeID(runs[runIndex], "request")) is not { } owner) continue;
            var id = NodeID(runs[runIndex], ExecutionSuffix + execution.Key);
            if (layout.Nodes.Any(n => n.Id == id)) continue;
            var top = nextY.TryGetValue(runIndex, out var stacked) ? stacked : owner.Frame.Y;
            var height = ExecutionHeight(expanded.Contains(id));
            var frame = new GraphRect(RightOf(top, height, owner.Frame), top, ExecutionWidth, height);
            layout.Nodes.Add(new Node(id, ExecutionKind, frame));
            Grow(frame);
            nextY[runIndex] = frame.MaxY + AttachmentGap;
        }
        // Answered plans hang off the same block as well, below its execution blocks: attachments, nothing already
        // placed moves (macOS MightyGraphLayout planRecords).
        foreach (var (runID, recordID) in planRecords ?? [])
        {
            var runIndex = -1;
            for (var i = 0; i < runs.Count; i++) if (runs[i].Id == runID) { runIndex = i; break; }
            if (runIndex < 0) continue;
            var request = layout.Nodes.FirstOrDefault(n => splitsExecution ? n.Kind == WorkKind && n.Id == NodeID(runs[runIndex], WorkSuffix) : n.Kind == "request" && n.Id == NodeID(runs[runIndex], "request"));
            if (request is null) continue;
            var id = NodeID(runs[runIndex], PlanRecordSuffix + recordID);
            if (layout.Nodes.Any(n => n.Id == id)) continue;
            var top = nextY.TryGetValue(runIndex, out var stacked) ? stacked : request.Frame.Y;
            // A dragged size wins over folded or opened, as on the other blocks.
            var recordSize = Size(id, PlanRecordWidth, PlanRecordHeight(expanded.Contains(id)));
            var height = recordSize.H;
            var sharing = layout.Nodes.Where(n => n.Frame.Y < top + height && n.Frame.MaxY > top).Select(n => n.Frame.MaxX).ToList();
            var right = sharing.Count > 0 ? sharing.Max() : request.Frame.MaxX;
            var frame = new GraphRect(Math.Max(right, request.Frame.MaxX) + AttachmentGap, top, recordSize.W, height);
            layout.Nodes.Add(new Node(id, "planRecord", frame));
            layout.Size = (Math.Max(layout.Size.W, frame.MaxX + 24 - layout.OriginX), Math.Max(layout.Size.H, frame.MaxY + 24 - layout.OriginY));
            nextY[runIndex] = frame.MaxY + AttachmentGap;
        }
        layout.FittedResultID = FittedResultId(runs, viewport, hasSharedResultSize);
        layout.ResultLimit = resultLimit;
        layout.ResultWindowFit = autoFitResultSize;
        layout.PlanLimit = planFit?.Limit;
        layout.PlanWindowFit = planFit?.WindowFit;
        if (viewport is not null && resultMaximum is { } bounded && ResultCap(bounded, resultLimit) != bounded)
            layout.ViewportBoundResultID = latestResultID;
        return layout;
    }
}
