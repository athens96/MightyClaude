using System.Globalization;
using System.Text;

namespace MightyClaude.Core;

/// <summary>
/// Mac MightyStructure (M/MightyCore/MightyStructure.swift): the Mighty graph drawn as its structure. A styled pane's
/// requests stand under the phase each belongs to, a free pane's as one request → run → result row each, and under every
/// request its sub-agent trees as clusters. The view shows it one board at a time (<see cref="Boards"/>); the phone reads the
/// shape as it is. It reads the runs the diagram and the timeline draw and stores nothing: a request's phase is worked out
/// again from its input on every read.
/// </summary>
public static partial class MightyStructure
{
    /// <summary>Where one phase, or one step of a free request, stands.</summary>
    public enum Step { Done, Current, Pending, Error, Stopped }
    /// <summary>A step as the phone is sent it (the Mac's raw value).</summary>
    public static string Word(Step step) => step.ToString().ToLowerInvariant();

    /// <summary>A text a card shows in place: up to its cap (a cut text ends with "…"), and the first lines a folded card shows.</summary>
    public sealed record Excerpt(string Text, string Preview)
    {
        /// <summary>The preview leaves some of the text out.</summary>
        public bool Cut => Preview != Text;
        /// <summary>Whether a folded card of <paramref name="lines"/> lines may leave some of the text out: it is cut, or its lines wrap past the limit.</summary>
        public bool Folds(int lines, int charactersPerLine) => Cut || VisualLines(Preview, charactersPerLine) > lines;
    }

    /// <summary>One tool call: the record's id, <see cref="AgentActivity.Kind"/> for the glyph, and its summary on one line (else its tool's name).</summary>
    public sealed record ActivityLine(string Id, string Kind, string State, string Text);

    /// <summary>One block of a cluster, flattened depth-first; <see cref="NodeId"/> is the diagram node it stands for.</summary>
    public sealed record Node(string AgentId, int AgentIndex, string NodeId, int Depth, string Title, string Status, string Kind,
        Excerpt? Prompt, Excerpt? Output, IReadOnlyList<ActivityLine> Activity, string? Model)
    {
        public bool Equals(Node? other) => other is not null && AgentId == other.AgentId && AgentIndex == other.AgentIndex && NodeId == other.NodeId && Depth == other.Depth
            && Title == other.Title && Status == other.Status && Kind == other.Kind && Prompt == other.Prompt && Output == other.Output && Model == other.Model && Activity.SequenceEqual(other.Activity);
        public override int GetHashCode() => HashCode.Combine(NodeId, Depth, Status, Activity.Count);
    }

    /// <summary>One root sub-agent's tree: the root first, then its descendants depth-first.</summary>
    public sealed record Cluster(IReadOnlyList<Node> Nodes)
    {
        public string Id => Nodes.Count > 0 ? Nodes[0].NodeId : "";
        public bool Equals(Cluster? other) => other is not null && Nodes.SequenceEqual(other.Nodes);
        public override int GetHashCode() => HashCode.Combine(Id, Nodes.Count);
    }

    /// <summary>What the request holds now, never what is to come.</summary>
    public sealed record Tally(int Agents, int Tools);

    /// <summary>
    /// One request as a card. <see cref="NodeId"/> is the request's own diagram node and <see cref="ExecutionNodeId"/> the run
    /// block under it; <see cref="Message"/> is the main block's latest reply, left out once the request finished with that
    /// very reply as its <see cref="Answer"/>.
    /// </summary>
    public sealed record Item(int RunIndex, string RunId, int Ordinal, string NodeId, string ExecutionNodeId, string Input, string Status, bool Finished, string? Result,
        Excerpt? Request, Excerpt? Message, IReadOnlyList<ActivityLine> Activity, Excerpt? Answer, Tally Tally, IReadOnlyList<Cluster> Clusters)
    {
        public bool Equals(Item? other) => ReferenceEquals(this, other) || other is not null && RunIndex == other.RunIndex && RunId == other.RunId && Ordinal == other.Ordinal && NodeId == other.NodeId
            && ExecutionNodeId == other.ExecutionNodeId && Input == other.Input && Status == other.Status && Finished == other.Finished && Result == other.Result && Request == other.Request
            && Message == other.Message && Answer == other.Answer && Tally == other.Tally && Activity.SequenceEqual(other.Activity) && Clusters.SequenceEqual(other.Clusters);
        public override int GetHashCode() => HashCode.Combine(RunId, RunIndex, Status, Finished, Activity.Count, Clusters.Count);
    }

    /// <summary>One phase of a styled pane (done, current or pending) and the requests under it, oldest first.</summary>
    public sealed record Column(StylePhase Phase, Step Step, IReadOnlyList<Item> Items)
    {
        public string Id => Phase.Id;
        public bool Equals(Column? other) => other is not null && Phase == other.Phase && Step == other.Step && Items.SequenceEqual(other.Items);
        public override int GetHashCode() => HashCode.Combine(Phase, Step, Items.Count);
    }

    /// <summary>One request of a free pane as request → run → result.</summary>
    public sealed record Row(Item Item, Step Request, Step Execution, Step Result);

    /// <summary>A style with phases: one column per phase, in order. No style, or one without phases: one row per request, oldest first.</summary>
    public sealed record Shape(IReadOnlyList<Column>? Columns, IReadOnlyList<Row>? Rows)
    {
        public static Shape Phases(IReadOnlyList<Column> columns) => new(columns, null);
        public static Shape Free(IReadOnlyList<Row> rows) => new(null, rows);
        /// <summary>Every card the shape holds, column by column or row by row.</summary>
        public IEnumerable<Item> Items => Columns?.SelectMany(c => c.Items) ?? (Rows ?? []).Select(r => r.Item);
        public bool Equals(Shape? other) => other is not null && (Columns is null ? other.Columns is null : other.Columns is not null && Columns.SequenceEqual(other.Columns))
            && (Rows is null ? other.Rows is null : other.Rows is not null && Rows.SequenceEqual(other.Rows));
        public override int GetHashCode() => HashCode.Combine(Columns?.Count, Rows?.Count);
    }

    /// <summary>
    /// What <see cref="Make"/> reads of the pane, cheap to compare: a view keeps the shape it built while this stays equal, so a
    /// streamed line that changes nothing drawn here does not rebuild it. Text is followed by its length, records by their count
    /// and a digest of their tail (<see cref="Tail"/>), a block's title and status as they are. The app's language is part of
    /// it: a block without a title is named in it (<see cref="MightyGraphSupport.BlockTitle"/>).
    /// </summary>
    public sealed record Signature
    {
        internal readonly record struct Block(string Id, string Title, string Status, string? ParentId, string? Kind, int Input, int Entries, int Tail, int Records);
        /// <summary>One request's part, which is all its card reads (<see cref="ItemCache"/> keys on it).</summary>
        internal sealed record Request(string Id, string Status, string? SourceRunId, int Input, int RootEntries, int RootTail, int ResultEntries, int Result, int? FinalOutput, Block[] Blocks)
        {
            public bool Equals(Request? other) => other is not null && Id == other.Id && Status == other.Status && SourceRunId == other.SourceRunId && Input == other.Input && RootEntries == other.RootEntries
                && RootTail == other.RootTail && ResultEntries == other.ResultEntries && Result == other.Result && FinalOutput == other.FinalOutput && Blocks.AsSpan().SequenceEqual(other.Blocks);
            public override int GetHashCode() => HashCode.Combine(Id, Status, Input, RootEntries, RootTail, Blocks.Length);
        }
        internal readonly Request[] Requests;
        private readonly string[] phases, plans;
        private readonly string? recognition, currentPhase, pendingPlanRunId;
        private readonly string language = Locale.LanguagePreference;
        private readonly bool startingNew;

        public Signature(IReadOnlyList<MightyGraphRun> runs, StyleEvaluator? style, StylePhase? currentPhase, bool startingNew = false,
            IReadOnlyList<PlanRecord>? planHistory = null, string? pendingPlanRunID = null)
        {
            Requests = runs.Select(run => new Request(run.Id, run.Status, run.SourceRunID, Bytes(run.Input), run.RootEntries.Count, Tail(run.RootEntries), run.ResultEntries.Count,
                Bytes(run.ResultEntries.LastOrDefault()?.Text), run.FinalOutput is { } final ? Bytes(final) : null,
                run.Agents.Select(a => new Block(a.Id, a.Title, a.Status, a.ParentID, a.Kind, Bytes(a.Input), a.Entries.Count, Tail(a.Entries), a.ResponseRecords?.Count ?? 0)).ToArray())).ToArray();
            phases = style is null ? [] : Ordered(style.Manifest).Select(p => p.Id).ToArray();
            recognition = style?.Manifest.Id; this.currentPhase = currentPhase?.Id; this.startingNew = startingNew;
            plans = (planHistory ?? []).Select(r => string.Join('\u001F', r.Id, r.Outcome, r.GraphRunId ?? "", r.RunId)).ToArray();
            pendingPlanRunId = pendingPlanRunID;
        }

        public bool Equals(Signature? other) => other is not null && recognition == other.recognition && currentPhase == other.currentPhase && startingNew == other.startingNew
            && pendingPlanRunId == other.pendingPlanRunId && language == other.language && phases.AsSpan().SequenceEqual(other.phases) && plans.AsSpan().SequenceEqual(other.plans) && Requests.AsSpan().SequenceEqual(other.Requests);
        public override int GetHashCode() => HashCode.Combine(Requests.Length, recognition, currentPhase, startingNew, pendingPlanRunId, plans.Length);
    }

    /// <summary>
    /// The cards of the last build, each built again only when its own request's part of the signature moved: while one
    /// request streams, the other cards are kept as they are rather than read again from their records.
    /// </summary>
    public sealed class ItemCache
    {
        private Dictionary<string, (Signature.Request Request, Item Item)> built = [];
        private string language = Locale.LanguagePreference;

        /// <summary>The cards of <paramref name="runs"/>, which <paramref name="signature"/> was taken of.</summary>
        public IReadOnlyList<Item> Items(IReadOnlyList<MightyGraphRun> runs, Signature signature)
        {
            // A card names an untitled block in the app's language: in another language every card is read again.
            if (language != Locale.LanguagePreference) { built = []; language = Locale.LanguagePreference; }
            var next = new Dictionary<string, (Signature.Request, Item)>(runs.Count); var items = new List<Item>(runs.Count);
            for (var index = 0; index < runs.Count; index++)
            {
                var run = runs[index]; var request = index < signature.Requests.Length ? signature.Requests[index] : null;
                // A trim of older requests moves a kept card's number, nothing else; a card that kept its place is the same card.
                var item = request is not null && built.TryGetValue(run.Id, out var kept) && kept.Request == request
                    ? kept.Item.RunIndex == index ? kept.Item : kept.Item with { RunIndex = index, Ordinal = index + 1 } : ItemFor(run, index);
                if (request is not null) next[run.Id] = (request, item);
                items.Add(item);
            }
            built = next; return items;
        }
    }

    /// <summary>
    /// The records a card reads, as one number: how many there are and how many of them are tool calls that settled (an older
    /// call finishing in place moves it), and the id, kind, text length and tool state and summary length of the last
    /// <see cref="TailEntries"/>. A text's length counts in <see cref="TailTextStep"/> steps, so a reply that streams in moves
    /// it every few lines, not on every word.
    /// </summary>
    private static int Tail(IReadOnlyList<LogEntry> entries)
    {
        var hash = new HashCode(); hash.Add(entries.Count);
        hash.Add(entries.Count(e => e.Activity is { } a && MightyGraphSupport.Terminal(a.State)));
        for (var index = Math.Max(0, entries.Count - TailEntries); index < entries.Count; index++)
        {
            var entry = entries[index];
            hash.Add(entry.Id); hash.Add(entry.Kind); hash.Add(Bytes(entry.Text) / TailTextStep);
            hash.Add(entry.Activity?.State); hash.Add(entry.Activity is { } activity ? Bytes(activity.Summary) : -1);
        }
        return hash.ToHashCode();
    }

    /// <summary>The deepest tree level a cluster follows, as the diagram's layout does.</summary>
    public const int MaximumDepth = 64;
    /// <summary>The bound of an input or result preview.</summary>
    private const int PreviewBytes = 400;
    /// <summary>The bounds of the texts a card shows in place, and the lines each shows folded (M/MightyCore/MightyStructure.swift:244-265).</summary>
    public const int RequestBytes = 4_096, RequestLines = 10, MessageBytes = 4_096, MessageLines = 8, AnswerBytes = 16_384, AnswerLines = 15,
        PromptBytes = 2_048, PromptLines = 2, OutputBytes = 4_096, OutputLines = 6;
    /// <summary>The tool calls a request's run card and a sub-agent's card list.</summary>
    public const int RequestActivity = 5, NodeActivity = 3;
    /// <summary>The narrowest kanban column; wider panes share their width out between the columns.</summary>
    public const double MinimumColumnWidth = 280;
    /// <summary>A folded line is cut at this many characters; a Markdown preview holds this many per line it shows.</summary>
    public const int PreviewLineCharacters = 200;
    /// <summary>The newest records the signature follows one by one, and the bytes a streamed text grows by before it moves.</summary>
    private const int TailEntries = 12, TailTextStep = 256;
    /// <summary>A tree's indent stops growing past this depth, so a deep chain stays inside its column.</summary>
    public const int MaximumIndent = 6;
    /// <summary>How full a wrapped line is counted: words wrap whole, so a line holds less than its width's worth of em-wide glyphs.</summary>
    private const double WrapFill = 0.75;
    /// <summary>A tool call's line, bounded as the phone's block summary is (M/MightyCore/Remote/MobileRemoteModels.swift maximumBlockSummary).</summary>
    private const int ActivityBytes = 1_000;
    /// <summary>The suffix of the diagram's run block under a request (M/MightyCore/MightyGraphLayout.swift:56).</summary>
    public const string ExecutionSuffix = "execution";
    /// <summary>Activity kinds that are a tool call, not the turn or a sub-agent's own line.</summary>
    private static readonly HashSet<string> ToolKinds = ["tool", "command", "read", "edit", "search", "web"];
    /// <summary>The characters the Mac reads as the end of a line (<c>Character.isNewline</c>).</summary>
    internal static readonly char[] Newlines = ['\n', '\r', '\u000B', '\u000C', '\u0085', (char)0x2028, (char)0x2029];

    private static int Bytes(string? text) => text is null ? 0 : Encoding.UTF8.GetByteCount(text);
    private static StylePhase[] Ordered(StyleManifest manifest) => manifest.Phases.OrderBy(p => p.Order).ToArray();

    /// <summary>
    /// The pane as the structure view draws it (M/MightyCore/MightyStructure.swift:286-304). <paramref name="currentPhase"/> is
    /// the pane's own phase, as its phase bar shows it; with <paramref name="startingNew"/> it is the phase the next request
    /// starts in and says nothing of the requests already there. The current column is the pane's phase; a column that holds a
    /// card is never pending; an empty column is done before the current one and pending after it.
    /// </summary>
    public static Shape Make(IReadOnlyList<MightyGraphRun> runs, StyleEvaluator? style, StylePhase? currentPhase, bool startingNew = false,
        IReadOnlyList<PlanRecord>? planHistory = null, string? pendingPlanRunID = null, IReadOnlyList<Item>? items = null)
    {
        var cards = items is not null && items.Count == runs.Count ? items : runs.Select(ItemFor).ToList();
        if (style is null || style.Manifest.Phases.Length == 0) return Shape.Free(cards.Select(RowFor).ToList());
        var ordered = Ordered(style.Manifest);
        var pane = currentPhase is not null && ordered.Any(p => p.Id == currentPhase.Id) ? currentPhase : null;
        var assigned = PhaseIDs(runs, style, startingNew ? null : pane, planHistory, pendingPlanRunID);
        // A pane that names no phase stands where its newest request does.
        var currentId = pane?.Id ?? assigned.LastOrDefault();
        var currentIndex = currentId is null ? -1 : Array.FindIndex(ordered, p => p.Id == currentId);
        var held = assigned.ToHashSet();
        return Shape.Phases(ordered.Select((phase, index) => new Column(phase,
            index == currentIndex ? Step.Current : held.Contains(phase.Id) || currentIndex >= 0 && index < currentIndex ? Step.Done : Step.Pending,
            cards.Where((_, at) => assigned[at] == phase.Id).ToList())).ToList());
    }

    /// <summary>
    /// Each run's phase id, in run order (M/MightyCore/MightyStructure.swift:322-347). A plan-state style (§1.17) reads each run
    /// as the pane's plan stage reads its latest one: the run whose plan waits for an answer is awaiting approval, a run whose
    /// latest answered plan was approved is executing, any other run is planning. Every other style, by the first rule that
    /// holds: the newest run takes <paramref name="currentPhase"/>; the phase the evaluator recognises from the run's input;
    /// the previous run's phase; for the first run, the style's default phase, else its first.
    /// </summary>
    public static IReadOnlyList<string> PhaseIDs(IReadOnlyList<MightyGraphRun> runs, StyleEvaluator style, StylePhase? currentPhase = null,
        IReadOnlyList<PlanRecord>? planHistory = null, string? pendingPlanRunID = null)
    {
        var manifest = style.Manifest; var ordered = Ordered(manifest);
        if (ordered.Length == 0) return [];
        var first = ordered[0].Id;
        if (StyleManifest.Text(manifest.Rules.GetProperty("phase"), "kind") == "planState")
        {
            string Phase(string stage) => style.CurrentPhase([], planStage: stage)?.Id ?? first;
            // The diagram's own match for the pending plan's request (PlanCardSupport.DiagramRecords).
            var waiting = -1;
            if (pendingPlanRunID is not null) for (var index = runs.Count - 1; index >= 0 && waiting < 0; index--) if (runs[index].SourceRunID == pendingPlanRunID || runs[index].Id == pendingPlanRunID) waiting = index;
            return runs.Select((run, index) => index == waiting ? Phase("awaitingApproval")
                : (planHistory ?? []).LastOrDefault(record => PlanRecordBelongs(record, run))?.Outcome is PlanOutcome.ApprovedAuto or PlanOutcome.ApprovedConfirm ? Phase("executing") : Phase("planning")).ToList();
        }
        // An empty history reads as the style's default phase.
        var initial = style.CurrentPhase([])?.Id ?? first;
        var pane = currentPhase is null ? null : manifest.Phases.FirstOrDefault(p => p.Id == currentPhase.Id)?.Id;
        var result = new List<string>(runs.Count);
        for (var index = 0; index < runs.Count; index++)
            result.Add((index == runs.Count - 1 ? pane : null) ?? RecognisedPhase(runs[index].Input, style) ?? result.LastOrDefault() ?? initial);
        return result;
    }

    /// <summary>The phase a prompt names, read as <see cref="StyleEvaluator.CurrentPhase"/> reads one: phase-less actions and free text name none.</summary>
    public static string? RecognisedPhase(string input, StyleEvaluator style)
    {
        var manifest = style.Manifest; string? phase;
        if (style.RecognisedAction(input) is { } action) phase = action.Phase;
        else
        {
            var name = StyleEvaluator.RecognisedName(manifest, input);
            phase = name is null ? null : StyleManifest.Items(manifest.Root, "aliases").Where(a => StyleManifest.Text(a, "name") == name).Select(a => StyleManifest.Text(a, "phase")).FirstOrDefault();
        }
        return phase is null ? null : manifest.Phases.FirstOrDefault(p => p.Id == phase)?.Id;
    }

    /// <summary>The match the pane's plan stage uses: a record's <c>GraphRunId</c> is its run's <c>SourceRunID</c> (or the block id), and an older Mac record names it as its <c>RunId</c>.</summary>
    private static bool PlanRecordBelongs(PlanRecord record, MightyGraphRun run) =>
        record.GraphRunId == run.Id || run.SourceRunID is not null && (record.GraphRunId == run.SourceRunID || record.RunId == run.SourceRunID);

    /// <summary>A column's width: the columns share what the pane has, but never go narrower than <see cref="MinimumColumnWidth"/> (the view scrolls sideways then).</summary>
    public static double ColumnWidth(double available, int count, double gap) =>
        count > 0 && double.IsFinite(available) ? Math.Max(MinimumColumnWidth, Math.Floor((available - gap * (count - 1)) / count)) : MinimumColumnWidth;

    /// <summary>
    /// The fewest characters a wrapped line of <paramref name="width"/> points holds at <paramref name="fontSize"/>, counting
    /// every glyph an em wide and lines as only partly filled, so a fold is never missed: Latin text holds more and only shows
    /// the toggle sooner.
    /// </summary>
    public static int CharactersPerLine(double width, double fontSize) =>
        double.IsFinite(width) && fontSize > 0 ? Math.Max(1, (int)Math.Floor(width / fontSize * WrapFill)) : 1;

    /// <summary>The width a sub-agent card's text has in a column: the tree's indent at <paramref name="depth"/> and the card's own padding taken off.</summary>
    public static double NodeTextWidth(double column, int depth) =>
        column - Math.Clamp(depth, 0, MaximumIndent) * DesignMetrics.Spacing.Lg - (DesignMetrics.Spacing.Md + DesignMetrics.Spacing.Xs);

    /// <summary>How many lines <paramref name="text"/> takes wrapped at <paramref name="charactersPerLine"/>.</summary>
    private static int VisualLines(string text, int charactersPerLine)
    {
        var width = Math.Max(1, charactersPerLine);
        return text.Split('\n').Sum(line => Math.Max(1, (Characters(line) + width - 1) / width));
    }

    /// <summary>Characters as the Mac counts them: whole grapheme clusters.</summary>
    private static int Characters(ReadOnlySpan<char> text)
    {
        var count = 0;
        for (; !text.IsEmpty; count++) text = text[StringInfo.GetNextTextElementLength(text)..];
        return count;
    }

    private static string Prefix(string text, int characters)
    {
        if (text.Length <= characters) return text;
        var end = 0;
        for (var taken = 0; taken < characters && end < text.Length; taken++) end += StringInfo.GetNextTextElementLength(text.AsSpan(end));
        return text[..end];
    }

    public static Item ItemFor(MightyGraphRun run, int index)
    {
        var finished = MightyGraphLayout.Finished(run);
        var answer = run.ResultEntries.LastOrDefault(e => e.Kind == "assistant" && e.Text.Length > 0)?.Text ?? run.FinalOutput;
        var result = finished && answer is not null && Preview(answer) is { Length: > 0 } line ? line : null;
        var full = finished && answer is not null ? ExcerptOf(answer, AnswerBytes, AnswerLines, markdown: true) : null;
        // RefreshResult keeps the final answer in the main block too: shown once, as the answer.
        var latest = Reply(run.RootEntries);
        var message = latest is null || full is not null && latest == answer ? null : ExcerptOf(latest, MessageBytes, MessageLines);
        // Counted over the blocks the clusters draw, so a steer's or a question's records add nothing.
        var blocks = Kept(run).Select(at => run.Agents[at]).ToList();
        var tools = run.RootEntries.Concat(blocks.SelectMany(block => block.Entries)).Count(e => e.Activity is { } activity && ToolKinds.Contains(activity.Kind));
        return new(index, run.Id, index + 1, MightyGraphLayout.NodeID(run, "request"), MightyGraphLayout.NodeID(run, ExecutionSuffix), Preview(run.Input), MightyTimeline.Status(run.Status),
            finished, result, ExcerptOf(run.Input, RequestBytes, RequestLines), message, Activity(run.RootEntries, RequestActivity), full,
            new(blocks.Count(block => MightyGraphSupport.BlockKind(block) == "agent"), tools), Clusters(run));
    }

    /// <summary>The request is always there; the run goes on until every block settled, unless the request failed or was stopped; the result waits until the request finished.</summary>
    public static Row RowFor(Item item)
    {
        Step? ended = item.Status switch { "error" => Step.Error, "stopped" => Step.Stopped, _ => null };
        return new(item, Step.Done, ended ?? (item.Finished ? Step.Done : Step.Current), item.Finished ? ended ?? Step.Done : Step.Pending);
    }

    /// <summary>
    /// One cluster per root of the run's sub-agent trees, by the diagram's rule (M/MightyCore/MightyStructure.swift:437-467): an
    /// agent whose parent is missing, unknown or itself is a root, a tree stops at <see cref="MaximumDepth"/>, and whatever a
    /// cycle or the cap left out starts a cluster of its own, so every block shows once. Steer, compaction and question blocks
    /// are the conversation's own moments, not work handed to a sub-agent, and are left out; tasks are work, so they stay, and
    /// so does a task's notice (a 작업 알림 steer): it and its reply are the run's work on that task's outcome.
    /// </summary>
    public static IReadOnlyList<Cluster> Clusters(MightyGraphRun run)
    {
        var kept = Kept(run); var known = kept.Select(at => run.Agents[at].Id).ToHashSet(); var children = new Dictionary<string, List<int>>();
        foreach (var index in kept)
        {
            if (run.Agents[index].ParentID is not { } parent) continue;
            if (!children.TryGetValue(parent, out var list)) children[parent] = list = [];
            list.Add(index);
        }
        var visited = new HashSet<int>(); var result = new List<Cluster>();
        void Walk(int index, int depth, List<Node> nodes)
        {
            if (depth >= MaximumDepth || !visited.Add(index)) return;
            var agent = run.Agents[index];
            nodes.Add(new(agent.Id, index, MightyGraphLayout.NodeID(run, "agent:" + agent.Id), depth, MightyGraphSupport.BlockTitle(agent), MightyTimeline.Status(agent.Status),
                MightyGraphSupport.BlockKind(agent), ExcerptOf(MightyGraphSupport.ShownInput(agent), PromptBytes, PromptLines), Reply(agent.Entries) is { } reply ? ExcerptOf(reply, OutputBytes, OutputLines) : null,
                Activity(agent.Entries, NodeActivity), agent.ResponseRecords?.Select(record => record.Model).FirstOrDefault(model => !string.IsNullOrEmpty(model))));
            foreach (var child in children.GetValueOrDefault(agent.Id) ?? []) if (child != index) Walk(child, depth + 1, nodes);
        }
        void Start(int index)
        {
            var nodes = new List<Node>(); Walk(index, 0, nodes);
            if (nodes.Count > 0) result.Add(new(nodes));
        }
        foreach (var index in kept)
        {
            var agent = run.Agents[index];
            if (agent.ParentID is not { } parent || !known.Contains(parent) || parent == agent.Id) Start(index);
        }
        foreach (var index in kept) if (!visited.Contains(index)) Start(index);
        return result;
    }

    /// <summary>The run's sub-agent, task and task-notice blocks, by index. A block id the run repeats (a damaged save) is kept at its first place only, so every node id is unique.</summary>
    private static List<int> Kept(MightyGraphRun run)
    {
        var seen = new HashSet<string>(); var kept = new List<int>();
        for (var index = 0; index < run.Agents.Count; index++)
            if ((MightyGraphSupport.BlockKind(run.Agents[index]) is "agent" or "task" || MightyGraphSupport.IsTaskNotice(run.Agents[index])) && seen.Add(run.Agents[index].Id)) kept.Add(index);
        return kept;
    }

    private static string Preview(string text) => ActivitySupport.Clean(text, PreviewBytes, singleLine: true);

    /// <summary>
    /// <paramref name="raw"/> cleaned as every shown text is and cut at <paramref name="maximumBytes"/>, with its first
    /// <paramref name="lines"/> lines as the folded preview; null when nothing is left. A plain preview cuts each line at
    /// <see cref="PreviewLineCharacters"/>; a <paramref name="markdown"/> one keeps whole lines within
    /// <paramref name="lines"/> × <see cref="PreviewLineCharacters"/> characters and closes a code fence left open, so the
    /// folded card renders what the opened one does, only shorter. Every line of the text ends with a line feed: the Windows
    /// composer ends a typed line with a bare carriage return, a pasted one with both.
    /// </summary>
    public static Excerpt? ExcerptOf(string raw, int maximumBytes, int lines, bool markdown = false)
    {
        // Cleaning reads the whole string; a text far over its cap is cut first.
        var cleaned = ActivitySupport.Clean(ActivitySupport.PrefixUtf8(raw, 2 * maximumBytes + 64), maximumBytes + 4).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (cleaned.Length == 0) return null;
        var text = Bytes(cleaned) > maximumBytes ? ActivitySupport.PrefixUtf8(cleaned, maximumBytes - 3).Trim() + "…" : cleaned;
        var kept = new List<string>();
        if (markdown)
        {
            var budget = lines * PreviewLineCharacters;
            foreach (var line in text.Split('\n').Take(lines))
            {
                var count = Characters(line);
                // Only a first line longer than the whole budget is cut.
                if (count > budget) { if (kept.Count == 0) kept.Add(Prefix(line, budget)); break; }
                kept.Add(line); budget -= count;
            }
        }
        else kept.AddRange(text.Split('\n').Take(lines).Select(line => Prefix(line, PreviewLineCharacters)));
        var shown = string.Join('\n', kept).Trim();
        if (markdown && shown != text && OpenFence(shown) is { } fence) shown += "\n" + fence;
        return new(text, shown);
    }

    /// <summary>The fence a Markdown text ends inside of (a line opening with ``` or ~~~ up to its matching close), null when every code block is closed.</summary>
    public static string? OpenFence(string markdown)
    {
        string? open = null;
        foreach (var line in markdown.Split('\n'))
        {
            var lead = line.AsSpan().TrimStart(' ');
            if (line.Length - lead.Length > 3 || lead.IsEmpty || lead[0] is not ('`' or '~')) continue;
            var mark = lead[0]; var run = 1;
            while (run < lead.Length && lead[run] == mark) run++;
            if (run < 3) continue;
            // A close is the same mark, at least as long, with nothing after it.
            if (open is null) open = new string(mark, run);
            else if (mark == open[0] && run >= open.Length && lead[run..].IsWhiteSpace()) open = null;
        }
        return open;
    }

    /// <summary>The block's latest reply: its last assistant record with words in it, a question's raw form left out.</summary>
    private static string? Reply(IReadOnlyList<LogEntry> entries) =>
        entries.LastOrDefault(e => e.Kind == "assistant" && e.Text.Length > 0 && !(e.Text[0] == '{' && UserQuestionnaire.Parse(e.Text) is not null))?.Text;

    /// <summary>The last <paramref name="limit"/> tool calls of a block, oldest first: each its summary, else its tool's name. The run's own lifecycle row ("turn") is not a tool call.</summary>
    private static List<ActivityLine> Activity(IReadOnlyList<LogEntry> entries, int limit)
    {
        var lines = new List<ActivityLine>();
        for (var index = entries.Count - 1; index >= 0 && lines.Count < limit; index--)
        {
            if (entries[index].Activity is not { } activity || activity.Kind == "turn") continue;
            var text = ActivitySupport.Clean(activity.Summary.Length == 0 ? activity.ToolName : activity.Summary, ActivityBytes, singleLine: true);
            if (text.Length > 0) lines.Add(new(entries[index].Id, activity.Kind, activity.State, text));
        }
        lines.Reverse(); return lines;
    }
}
