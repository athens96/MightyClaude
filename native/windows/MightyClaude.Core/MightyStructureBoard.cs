namespace MightyClaude.Core;

public static partial class MightyStructure
{
    /// <summary>
    /// One page of the structure view's kanban (M/MightyCore/MightyStructureBoard.swift): one request of a free pane, or one
    /// pipeline of a styled pane. The view shows one board at a time, with the steps it went through as its header and one
    /// column of cards per step under it. <see cref="Id"/> is the first request's run id: requests only join a board's end, so
    /// it holds while new runs arrive; a trim that drops a pipeline's first request names the pipeline by its next one
    /// (<see cref="Navigation"/> follows it by the runs it remembers).
    /// </summary>
    public sealed record Board(string Id, BoardKind Kind, int Ordinal, IReadOnlyList<Item> Items, string? Headline, string Status, IReadOnlyList<BoardStep> Steps, IReadOnlyList<BoardColumn> Columns)
    {
        public IReadOnlyList<string> RunIds => Items.Select(item => item.RunId).ToList();
        /// <summary>Only notice runs taken into a running turn (<see cref="Item.Quiet"/>): the view never moves on to it by itself.</summary>
        public bool Quiet => Items.Count > 0 && Items.All(item => item.Quiet);
        /// <summary>The current step's place, else the last step that is over.</summary>
        public int? CurrentStepIndex
        {
            get
            {
                for (var index = 0; index < Steps.Count; index++) if (Steps[index].Step == Step.Current) return index;
                for (var index = Steps.Count - 1; index >= 0; index--) if (Steps[index].Step != Step.Pending) return index;
                return null;
            }
        }
    }

    /// <summary>A free pane's request (요청 → 실행 → 결과), or a styled pane's run of requests through the style's phases.</summary>
    public enum BoardKind { Request, Pipeline }

    /// <summary>One step of a board's header: <see cref="Id"/> is "request", "execution" or "result" on a free board, else the phase id; <see cref="Title"/> is the phase's title, null for a free board's steps, which the view names.</summary>
    public sealed record BoardStep(string Id, string? Title, Step Step);

    /// <summary>One kanban column: a step and its cards, top to bottom. <see cref="Count"/> is the header's: a styled column's requests, a free column's cards (요청 1, 실행 the main card and its sub-agents, 결과 1 once there is a result).</summary>
    public sealed record BoardColumn(BoardStep Step, IReadOnlyList<Card> Cards, int Count)
    {
        public string Id => Step.Id;
    }

    /// <summary>What a card shows: a free board's request, its 실행 part or its result; a styled board's request with its 실행 part and result line; or a sub-agent or task.</summary>
    public enum CardKind { Request, Execution, Result, Summary, Agent }

    /// <summary>One card of a column, of request <see cref="Item"/>. An agent card carries its <see cref="Node"/> and the title of the block it runs under (<see cref="Parent"/>, null for a tree's root).</summary>
    public sealed record Card(CardKind Kind, Item Item, Node? Node = null, string? Parent = null)
    {
        public string Id => Kind == CardKind.Agent ? "agent:" + Node!.NodeId : Kind.ToString().ToLowerInvariant() + ":" + Item.RunId;
    }

    /// <summary>
    /// The boards of <paramref name="shape"/>, oldest first (M/MightyCore/MightyStructureBoard.swift:84-106). A free pane has
    /// one board per request. A styled pane has one board per pipeline: a run of consecutive requests whose phase never goes
    /// back. The first request starts a pipeline, and so does every request whose phase comes earlier in the style's order than
    /// the request before it; requests read their phase as <see cref="Make"/> gives it, so a request that names no phase stays
    /// in the pipeline of the one before it. A pipeline's steps are the style's phases judged within it: the newest pipeline's
    /// current phase is the pane's unless the pane's comes before its last request; an older pipeline's is its last request's,
    /// done once its requests are.
    /// </summary>
    public static IReadOnlyList<Board> Boards(Shape shape) =>
        shape.Columns is { } columns ? PipelineBoards(columns) : (shape.Rows ?? []).Select((row, index) => FreeBoard(row, index + 1)).ToList();

    private static Board FreeBoard(Row row, int ordinal)
    {
        var item = row.Item;
        BoardStep[] steps = [new("request", null, row.Request), new("execution", null, row.Execution), new("result", null, row.Result)];
        var agents = AgentCards(item);
        BoardColumn[] columns =
        [
            new(steps[0], [new(CardKind.Request, item)], 1),
            new(steps[1], [new(CardKind.Execution, item), .. agents], 1 + agents.Count),
            new(steps[2], [new(CardKind.Result, item)], item.Finished ? 1 : 0),
        ];
        return new(item.RunId, BoardKind.Request, ordinal, [item], FirstLine(item), item.Status, steps, columns);
    }

    private static List<Board> PipelineBoards(IReadOnlyList<Column> columns)
    {
        var phaseOf = new Dictionary<int, int>(); var items = new List<Item>();
        for (var phase = 0; phase < columns.Count; phase++)
            foreach (var item in columns[phase].Items) { phaseOf[item.RunIndex] = phase; items.Add(item); }
        items.Sort((a, b) => a.RunIndex.CompareTo(b.RunIndex));
        // Split where the phase goes back.
        var groups = new List<List<Item>>(); int? previous = null;
        foreach (var item in items)
        {
            var phase = phaseOf.GetValueOrDefault(item.RunIndex);
            if (previous is { } before && phase >= before) groups[^1].Add(item); else groups.Add([item]);
            previous = phase;
        }
        int? pane = null;
        for (var index = 0; index < columns.Count && pane is null; index++) if (columns[index].Step == Step.Current) pane = index;
        return groups.Select((group, index) =>
        {
            var last = phaseOf.GetValueOrDefault(group[^1].RunIndex);
            var newest = index == groups.Count - 1;
            var current = newest && pane is { } at && at >= last ? at : last;
            var over = !newest && group.All(item => item.Finished);
            var boardColumns = columns.Select((column, phase) =>
            {
                var step = phase < current ? Step.Done : phase > current ? Step.Pending : over ? Step.Done : Step.Current;
                var held = group.Where(item => phaseOf.GetValueOrDefault(item.RunIndex) == phase).ToList();
                return new BoardColumn(new(column.Phase.Id, column.Phase.Title, step), held.SelectMany(item => (IEnumerable<Card>)[new(CardKind.Summary, item), .. AgentCards(item)]).ToList(), held.Count);
            }).ToList();
            var status = group.Any(item => item.Status == "running") ? "running" : group.Any(item => item.Status == "waiting") ? "waiting" : group[^1].Status;
            return new Board(group[0].RunId, BoardKind.Pipeline, index + 1, group, FirstLine(group[0]), status, boardColumns.Select(column => column.Step).ToList(), boardColumns);
        }).ToList();
    }

    /// <summary>A request's sub-agent cards, each tree depth-first, each card naming the block it runs under.</summary>
    private static List<Card> AgentCards(Item item)
    {
        var cards = new List<Card>();
        foreach (var cluster in item.Clusters)
        {
            var titles = new List<string>();
            foreach (var node in cluster.Nodes)
            {
                var depth = Math.Min(node.Depth, titles.Count);
                titles.RemoveRange(depth, titles.Count - depth);
                cards.Add(new(CardKind.Agent, item, node, titles.Count > 0 ? titles[^1] : null));
                titles.Add(node.Title);
            }
        }
        return cards;
    }

    /// <summary>The request's first line with words in it.</summary>
    private static string? FirstLine(Item item) =>
        (item.Request?.Text ?? item.Input).Split(Newlines).Select(line => line.Trim()).FirstOrDefault(line => line.Length > 0);

    /// <summary>
    /// Which board the structure view shows (M/MightyCore/MightyStructureBoard.swift:181-240). While the user is on the newest
    /// board the view moves on with it: a new request (or pipeline) is shown as it arrives. A board the user picks that is not
    /// the newest is kept by its id, and by the requests it held when picked, so new runs never move it; a newer board that
    /// arrives meanwhile is unseen until the user goes to the newest board again.
    /// </summary>
    public sealed class Navigation
    {
        /// <summary>The board the user picked, null while the view shows the newest one.</summary>
        public string? Chosen { get; private set; }
        private IReadOnlyList<string> chosenRuns = [];
        /// <summary>The newest board when the user left it.</summary>
        private string? seenNewest;

        /// <summary>The newest board: the last one, but a quiet notice's board after it (<see cref="Board.Quiet"/>), which the view never
        /// moves on to by itself (<see cref="MightyGraphSupport.FollowRunIndex"/>).</summary>
        public static int? NewestIndex(IReadOnlyList<Board> boards)
        {
            for (var index = boards.Count - 1; index >= 0; index--) if (!boards[index].Quiet) return index;
            return boards.Count > 0 ? boards.Count - 1 : null;
        }

        /// <summary>The place of the board shown in <paramref name="boards"/>, null when there is none. A picked board that is gone is found by a request it held, else the oldest board is shown.</summary>
        public int? Index(IReadOnlyList<Board> boards)
        {
            if (boards.Count == 0) return null;
            if (Chosen is null) return NewestIndex(boards);
            for (var index = 0; index < boards.Count; index++) if (boards[index].Id == Chosen) return index;
            var runs = chosenRuns.ToHashSet();
            for (var index = 0; index < boards.Count; index++) if (boards[index].Items.Any(item => runs.Contains(item.RunId))) return index;
            return 0;
        }

        /// <summary>The shown board is the newest one.</summary>
        public bool OnNewest(IReadOnlyList<Board> boards) => Index(boards) is not { } index || index == NewestIndex(boards);

        /// <summary>A board newer than the one shown arrived since the user left the newest board.</summary>
        public bool HasUnseen(IReadOnlyList<Board> boards) => !OnNewest(boards) && (NewestIndex(boards) is { } newest ? boards[newest].Id : null) != seenNewest;

        /// <summary>Shows board <paramref name="id"/>. The newest board is shown as "the newest", so the view moves on with new ones again.</summary>
        public void Select(string id, IReadOnlyList<Board> boards)
        {
            if (boards.FirstOrDefault(board => board.Id == id) is not { } target) return;
            var newest = NewestIndex(boards) is { } at ? boards[at].Id : null;
            if (target.Id == newest) { Chosen = null; chosenRuns = []; seenNewest = null; return; }
            if (OnNewest(boards)) seenNewest = newest;
            Chosen = target.Id; chosenRuns = target.RunIds;
        }

        /// <summary>Shows the board <paramref name="offset"/> places from the shown one, stopping at the ends.</summary>
        public void Step(int offset, IReadOnlyList<Board> boards)
        {
            if (Index(boards) is not { } index) return;
            Select(boards[Math.Clamp(index + offset, 0, boards.Count - 1)].Id, boards);
        }

        /// <summary>Shows the newest board, and moves on with new ones from now on.</summary>
        public void ShowNewest(IReadOnlyList<Board> boards)
        {
            if (NewestIndex(boards) is not { } newest) { Chosen = null; return; }
            Select(boards[newest].Id, boards);
        }
    }
}
