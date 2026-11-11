namespace MightyCode.Core;

/// Port of macOS MightyGraphLayout.
public sealed class MightyGraphLayout
{
    /// <param name="IsCompact">A sub-agent or a background command drawn as its compact card (<see cref="FlowCard"/>), not as its block.</param>
    public sealed record Node(string Id, string Kind, GraphRect Frame, bool IsCompact = false);
    /// <param name="Side">From a spine block's trailing edge to a block it launched, beside it (<see cref="FlowSteps"/>).</param>
    public sealed record Edge(string Source, string Target, bool Joins = false, bool Side = false);

    public List<Node> Nodes { get; init; } = [];
    public List<Edge> Edges { get; init; } = [];

    /// <summary>The role view of a Claude pane's request (<see cref="Make"/> <c>fanOut</c>): every sub-agent and background command of the run stands under
    /// the block that launched it, a background one too, which the other views leave to the strip and the notice runs; a running one is a compact card
    /// (<see cref="FlowCard"/>) unless the user opened it (<paramref name="Opened"/>: the nodes opened from their card into the full block). <paramref name="OpenAll"/>
    /// lays every card out as its block, for a reader that wants every block's node (the web page's copy) (macOS FanOut).</summary>
    public sealed record FanOut(IReadOnlySet<string>? Opened = null, bool OpenAll = false)
    {
        public bool IsOpened(string id) => OpenAll || Opened?.Contains(id) == true;
    }
    /// <summary>The delegate line's node: its kind and its suffix, <c>delegate:&lt;key&gt;</c> (<see cref="FlowDelegation"/>); the return node's
    /// (<see cref="FlowReturn"/>); a request's step (<see cref="FlowSteps"/>).</summary>
    public const string DelegateKind = "delegate", DelegateSuffix = "delegate:";
    public const string ReturnKind = "return", ReturnSuffix = "return";
    public const string StepKind = "step";
    /// <summary>From a block's bottom to its delegate line, and from the line to the row under it.</summary>
    public const double DelegateGap = 18, DelegateRowGap = 26;
    /// <summary>Neither selected nor resized: a click goes to what it holds and a wheel pans past it. The history block at the top, the role view's line,
    /// return node and advisor band, and a compact card, which has nothing to scroll (macOS Node.isFixed).</summary>
    public static bool IsFixed(Node node) => node.IsCompact || node.Kind is "history" or DelegateKind or ReturnKind;
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
    public static bool IsResizable(string kind) => kind is "request" or WorkKind or "agent" or "result" or "draft" or "plan" or "planRecord" or StepKind;
    /// <summary>A laid-out node with the corner handle: a resizable kind that is no compact card, which is never dragged (macOS Node.isResizable).</summary>
    public static bool IsResizable(Node node) => !node.IsCompact && IsResizable(node.Kind);
    /// <summary>The block that holds a point given in node coordinates, the first laid out when two overlap (macOS <c>frames.first { contains }</c>).</summary>
    public Node? NodeAt(double x, double y) => Nodes.FirstOrDefault(n => x >= n.Frame.X && x < n.Frame.MaxX && y >= n.Frame.Y && y < n.Frame.MaxY);

    public static bool Terminal(string state) =>
        state is "completed" or "error" or "failed" or "stopped" or "cancelled" or "interrupted";

    /// <summary>Ended with every block a view draws for it (<see cref="BackgroundNoticeRuns.ShownAgents"/>): background work the request
    /// started runs on in the strip and is not waited for.</summary>
    public static bool Finished(MightyGraphRun run) =>
        Terminal(run.Status) && BackgroundNoticeRuns.ShownAgents(run).All(a => Terminal(a.Status));
    /// <summary>Has a result card under it: every finished run but a notice run without a reply of its own (<see cref="MightyGraphRun.QuietNotice"/>),
    /// whose card would hold nothing; and a notice run a later turn answers in again, which keeps showing its earlier reply
    /// (<see cref="MightyGraphRun.ReopenedWithReply"/>).</summary>
    public static bool HasResult(MightyGraphRun run) => Finished(run) && !run.QuietNotice || run.ReopenedWithReply;

    public static string NodeID(MightyGraphRun run, string suffix) =>
        MightyGraphBlockSize.NodeId(run.Id, suffix);

    /// The block the camera stands on for the newest request: its 실행 block while
    /// it runs, where its work shows, else the request; null without a request
    /// (macOS <c>newestRequestTargetID</c>).
    public string? NewestRequestTargetID(IReadOnlyList<MightyGraphRun> runs)
    {
        // A notice run after it is not a request (MightyGraphSupport.NewestRequestRun).
        if (MightyGraphSupport.NewestRequestRun(runs) is not { } last) return null;
        var work = NodeID(last, WorkSuffix);
        return !Finished(last) && Nodes.Any(n => n.Id == work) ? work : NodeID(last, "request");
    }

    public static string? LatestResultID(IReadOnlyList<MightyGraphRun> runs)
    {
        for (var i = runs.Count - 1; i >= 0; i--)
            if (HasResult(runs[i])) return NodeID(runs[i], "result");
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
            if (HasResult(runs[i])) return resultFilesRunID is not null && runs[i].Id == resultFilesRunID && runs[i].Status == "completed";
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
        IReadOnlyList<(string RunID, string Key)>? executions = null,
        FanOut? fanOut = null)
    {
        // The runs as views draw them: a request's background blocks show only in the notice run that reports them (BackgroundNoticeRuns).
        runs = MightyGraphSupport.ShownRuns(runs);
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
            // With a fan-out the run's background sub-agents and commands are laid out too, each where it was launched (macOS make(fanOut:)).
            var agents = fanOut is null ? run.Agents : run.FlowAgents;
            List<int> Ordered(IEnumerable<int> values) => fanOut is null ? values.ToList() : values.OrderBy(run.FlowOrder).ToList();
            // The blocks launched from the main session in one of the request's steps: their cards name the step (FlowSteps.StepLabel).
            var inStep = fanOut is null ? [] : Enumerable.Range(0, agents.Count).Where(i => FlowSteps.StepLabel(agents[i], run) is not null).ToHashSet();
            var indexes = new Dictionary<string, int>();
            for (var ai = 0; ai < agents.Count; ai++)
                if (!indexes.ContainsKey(agents[ai].Id)) indexes[agents[ai].Id] = ai;
            // A card counts the blocks under it instead of drawing them: they are laid out nowhere.
            void Fold(int index, int depth)
            {
                if (depth >= 64 || !visited.Add(index)) return;
                for (var ci = 0; ci < agents.Count; ci++) if (ci != index && agents[ci].ParentID == agents[index].Id) Fold(ci, depth + 1);
            }

            Tree? Branch(int index, int depth = 0)
            {
                if (depth >= 64 || !visited.Add(index)) return null;
                var agent = agents[index];
                var id = NodeID(run, "agent:" + agent.Id);
                var compact = fanOut is not null && FlowCard.IsCompact(agent, fanOut.IsOpened(id));
                (double W, double H) cardSize = compact ? (FlowCard.Width, inStep.Contains(index) ? FlowCard.StepHeight : FlowCard.Height) : Size(id, 360, expanded.Contains(id) ? 480 : agent.IsTask && agent.NoticeReply is not null ? 320 : 240);
                var height = cardSize.H;
                var below = Ordered(Enumerable.Range(0, agents.Count).Where(ci => ci != index && agents[ci].ParentID == agent.Id));
                if (compact) foreach (var ci in below) Fold(ci, depth + 1);
                var children = compact ? [] : below.Select(ci => Branch(ci, depth + 1)).OfType<Tree>().ToList();
                var childrenWidth = children.Sum(c => c.Width) + Math.Max(0, children.Count - 1) * SiblingGap;
                // A block that delegated says so on the branch to its row (FlowDelegation).
                var delegates = fanOut is not null && children.Count > 0 && FlowDelegation.Children(agent.Id, run).Count > 0;
                var width = Math.Max(Math.Max(cardSize.W, childrenWidth), delegates ? FlowDelegation.Width : 0);
                var node = new Node(id, "agent", new((width - cardSize.W) / 2, 0, cardSize.W, height), compact);
                var result = new Tree { Nodes = [node], Width = width, Height = height, Top = id, Leaves = [id] };
                if (children.Count > 0)
                {
                    result.Leaves = [];
                    var source = id;
                    var top = height + RowGap;
                    if (delegates)
                    {
                        var line = NodeID(run, DelegateSuffix + agent.Id);
                        var frame = new GraphRect((width - FlowDelegation.Width) / 2, height + DelegateGap, FlowDelegation.Width, FlowDelegation.Height);
                        result.Nodes.Add(new Node(line, DelegateKind, frame));
                        result.Edges.Add(new Edge(id, line));
                        source = line;
                        top = frame.MaxY + DelegateRowGap;
                    }
                    var x = (width - childrenWidth) / 2;
                    foreach (var child in children)
                    {
                        child.Offset(x, top);
                        result.Nodes.AddRange(child.Nodes);
                        result.Edges.Add(new Edge(source, child.Top));
                        result.Edges.AddRange(child.Edges);
                        result.Leaves.AddRange(child.Leaves);
                        result.Height = Math.Max(result.Height, top + child.Height);
                        x += child.Width + SiblingGap;
                    }
                }
                return result;
            }

            bool IsTop(int ai) => agents[ai].ParentID is not { } parent || !indexes.ContainsKey(parent) || parent == agents[ai].Id;
            // A plan waiting for its answer stands where the result will.
            var planWaits = run.Id == planRunID && !Finished(run);
            // The role view (FanOut): the request, what happened between it and its answer, and the answer. While there is no answer yet the main
            // session's card stands in its place (the 실행 block, a card until opened) (macOS make(fanOut:)).
            var endsInWork = fanOut is not null && splitsExecution && !HasResult(run) && !planWaits;
            var workCompact = fanOut is not null && !fanOut.IsOpened(workID);
            (double W, double H)? workShown = workCompact ? (FlowSteps.CardWidth, FlowSteps.CardHeight) : workSize;
            // The request's steps that began, by their index among all its steps; one not begun is only in the plan column.
            var started = fanOut is null ? [] : FlowSteps.Steps(run).Select((step, index) => (Index: index, Step: step)).Where(s => !s.Step.IsPending).ToList();
            double treeWidth;
            Tree tree;
            if (started.Count > 0)
            {
                // The request's spine: the request, then each step that began, every block launched from the main session beside the one whose time it
                // was launched in (FlowSteps.Slot; before the first step, beside the request).
                var spineSteps = started.Select(s => s.Step).ToList();
                var slots = Enumerable.Range(0, started.Count + 1).Select(_ => new List<Tree>()).ToList();
                foreach (var ai in Ordered(Enumerable.Range(0, agents.Count)).Where(IsTop))
                    if (Branch(ai) is { } t) slots[FlowSteps.Slot(agents[ai], spineSteps)].Add(t);
                for (var ai = 0; ai < agents.Count; ai++)
                    if (!visited.Contains(ai) && Branch(ai) is { } t) slots[0].Add(t);
                var spine = new List<(string Id, string Kind, (double W, double H) Size, bool Compact)> { (mainID, "request", mainSize, false) };
                foreach (var (index, step) in started)
                {
                    var id = NodeID(run, FlowSteps.NodeSuffix(step));
                    var compact = !fanOut!.IsOpened(id);
                    spine.Add((id, StepKind, compact ? (FlowSteps.CardWidth, FlowSteps.CardHeight) : Size(id, 360, expanded.Contains(id) ? 480 : 240), compact));
                }
                // The spine stands on the run's centreline: the column beside it is matched by room on the other side.
                var half = Math.Max(HasResult(run) ? resultSize.W : 0, endsInWork ? workShown?.W ?? 0 : 0) / 2;
                for (var slot = 0; slot < spine.Count; slot++)
                {
                    var column = slots[slot].Count > 0 ? slots[slot].Max(t => t.Width) : 0;
                    half = Math.Max(half, spine[slot].Size.W / 2 + (column > 0 ? FlowSteps.SideGap + column : 0));
                }
                treeWidth = 2 * half;
                tree = new Tree { Width = treeWidth, Height = 0, Top = mainID, Leaves = [] };
                var spineY = 0.0;
                string? previousBlock = null;
                for (var slot = 0; slot < spine.Count; slot++)
                {
                    var block = spine[slot];
                    var frame = new GraphRect(half - block.Size.W / 2, previousBlock is null ? 0 : spineY + RowGap, block.Size.W, block.Size.H);
                    tree.Nodes.Add(new Node(block.Id, block.Kind, frame, block.Compact));
                    if (previousBlock is not null) tree.Edges.Add(new Edge(previousBlock, block.Id));
                    double lowest = frame.MaxY, top = frame.Y;
                    foreach (var child in slots[slot])
                    {
                        child.Offset(frame.MaxX + FlowSteps.SideGap, top);
                        tree.Nodes.AddRange(child.Nodes);
                        tree.Edges.Add(new Edge(block.Id, child.Top, Side: true));
                        tree.Edges.AddRange(child.Edges);
                        lowest = Math.Max(lowest, top + child.Height);
                        top += child.Height + SiblingGap;
                    }
                    spineY = lowest;
                    previousBlock = block.Id;
                }
                tree.Height = spineY;
                tree.Leaves = [previousBlock ?? mainID];
            }
            else
            {
                var branches = new List<Tree>();
                foreach (var ai in Ordered(Enumerable.Range(0, agents.Count)).Where(IsTop))
                    if (Branch(ai) is { } t) branches.Add(t);
                for (var ai = 0; ai < agents.Count; ai++)
                {
                    if (!visited.Contains(ai)) { var t = Branch(ai); if (t is not null) branches.Add(t); }
                }
                var branchWidth = branches.Sum(b => b.Width) + Math.Max(0, branches.Count - 1) * SiblingGap;
                // The role view draws the 실행 block only where the answer will be (below); every other view right under the request.
                var workUnder = fanOut is null ? workShown : null;
                treeWidth = new[] { mainSize.W, workUnder?.W ?? 0, branchWidth, HasResult(run) ? resultSize.W : 0, endsInWork ? workShown?.W ?? 0 : 0 }.Max();
                var mainNode = new Node(mainID, "request", new((treeWidth - mainSize.W) / 2, 0, mainSize.W, mainHeight));
                tree = new Tree { Nodes = [mainNode], Width = treeWidth, Height = mainHeight, Top = mainID, Leaves = [mainID] };
                // The block the sub-agents branch from: the 실행 block right under the request, or the request itself.
                var parentID = mainID;
                if (workUnder is { } work)
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
                    // The main session's delegate line stands between it and the row (FlowDelegation).
                    if (fanOut is not null && FlowDelegation.Children(FlowDelegation.MainKey, run).Count > 0)
                    {
                        var line = NodeID(run, DelegateSuffix + FlowDelegation.MainKey);
                        var frame = new GraphRect((treeWidth - FlowDelegation.Width) / 2, tree.Height + DelegateGap, FlowDelegation.Width, FlowDelegation.Height);
                        tree.Nodes.Add(new Node(line, DelegateKind, frame));
                        tree.Edges.Add(new Edge(parentID, line));
                        parentID = line;
                        top = frame.MaxY + DelegateRowGap;
                    }
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
            }
            // A plan waiting for an answer goes where the result will: the run
            // is still going, so nothing is below it yet.
            if (planWaits)
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
            // The role view: the main session at work, where its answer will come, until it is there.
            if (endsInWork && workShown is { } atWork)
            {
                tree.Nodes.Add(new Node(workID, WorkKind, new((treeWidth - atWork.W) / 2, tree.Height + RowGap, atWork.W, atWork.H), workCompact));
                tree.Edges.AddRange(tree.Leaves.Select(l => new Edge(l, workID, true)));
                tree.Leaves = [workID];
                tree.Height += RowGap + atWork.H;
            }
            if (HasResult(run))
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
            if (panelRunIndex >= 0 && runs[panelRunIndex].Status == "completed" && HasResult(runs[panelRunIndex]))
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
        // The block attachments hang off: the 실행 block (the main agent started them), else the request, which the role view's 실행 card
        // stands under only until the answer is there.
        Node? Anchor(int runIndex) =>
            (splitsExecution ? layout.Nodes.FirstOrDefault(n => n.Kind == WorkKind && n.Id == NodeID(runs[runIndex], WorkSuffix)) : null)
            ?? layout.Nodes.FirstOrDefault(n => n.Kind == "request" && n.Id == NodeID(runs[runIndex], "request"));
        void Grow(GraphRect frame) => layout.Size = (Math.Max(layout.Size.W, frame.MaxX + 24 - layout.OriginX), Math.Max(layout.Size.H, frame.MaxY + 24 - layout.OriginY));
        // A picture preview hangs off the top of the step that produced it, right of every card it would share rows
        // with: an attachment, like the file list (no edge, no centreline, nothing already placed moves).
        foreach (var gallery in galleries ?? [])
        {
            if (gallery.Count <= 0 || RunIndex(gallery.RunID) is not (>= 0 and var runIndex)) continue;
            // The request step's pictures come from the main agent's records: with a 실행 block they hang off it.
            var stepID = NodeID(runs[runIndex], splitsExecution && gallery.Step == MightyGraphImages.RequestStep ? WorkSuffix : gallery.Step);
            // The role view draws no 실행 block once the answer is there: the request's pictures hang off the request then.
            var requestID = NodeID(runs[runIndex], "request");
            if ((layout.Nodes.FirstOrDefault(n => n.Id == stepID && !IsAuxiliary(n.Kind))
                    ?? (gallery.Step == MightyGraphImages.RequestStep ? layout.Nodes.FirstOrDefault(n => n.Id == requestID) : null)) is not { } step) continue;
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
            if (Anchor(runIndex) is not { } owner) continue;
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
            if (Anchor(runIndex) is not { } request) continue;
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
