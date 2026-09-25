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
    public string? FittedResultID { get; set; }

    private const double SiblingGap = 32;
    private const double RowGap = 52;

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
        bool hasSharedResultSize = false)
    {
        var latestResultID = LatestResultID(runs);
        var latestFinishedRunIndex = -1;
        for (var i = runs.Count - 1; i >= 0; i--)
            if (Finished(runs[i])) { latestFinishedRunIndex = i; break; }

        var filesPanelOpenForLatest = latestFinishedRunIndex >= 0
            && resultFilesRunID is not null
            && runs[latestFinishedRunIndex].Id == resultFilesRunID
            && runs[latestFinishedRunIndex].Status == "completed";

        (double W, double H)? autoFitResultSize = null;
        if (viewport is not null && latestResultID is not null)
        {
            var filesOffset = filesPanelOpenForLatest ? 336.0 : 0.0;
            autoFitResultSize = (Math.Max(500, viewport.Value.W - 48 - filesOffset), Math.Max(200, viewport.Value.H - 48));
        }

        (double W, double H) Size(string id, double defaultW, double defaultH)
        {
            if (latestResultID is not null && id == latestResultID && viewport is not null)
            {
                if (hasSharedResultSize) return (defaultW, defaultH); // shared size already passed as default
                if (autoFitResultSize is not null) return autoFitResultSize.Value;
            }
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
            trees.Add(new Tree
            {
                Nodes = [new Node(pendingID, "draft", new(0, 0, MightyGraphCamera.RequestWidth, 140))],
                Width = MightyGraphCamera.RequestWidth,
                Height = 140,
                Top = pendingID,
                Leaves = [pendingID]
            });
        }

        var layout = new MightyGraphLayout();
        var y = 24.0;
        var previous = new List<string>();
        foreach (var tree in trees)
        {
            tree.Offset(MightyGraphCamera.X(tree.Width), y);
            layout.Nodes.AddRange(tree.Nodes);
            layout.Edges.AddRange(previous.Select(p => new Edge(p, tree.Top, true)));
            layout.Edges.AddRange(tree.Edges);
            previous = tree.Leaves;
            y += tree.Height + RowGap;
        }

        var leading = layout.Nodes.Count > 0 ? layout.Nodes.Min(n => n.Frame.X) : MightyGraphCamera.X(MightyGraphCamera.RequestWidth);
        var trailing = layout.Nodes.Count > 0 ? layout.Nodes.Max(n => n.Frame.MaxX) : MightyGraphCamera.CentreX + MightyGraphCamera.RequestWidth / 2;
        layout.OriginX = MightyGraphCamera.OriginX(leading);
        layout.Size = (MightyGraphCamera.CanvasWidth(leading, trailing), Math.Max(188, y - RowGap + 24));

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
        return layout;
    }
}
