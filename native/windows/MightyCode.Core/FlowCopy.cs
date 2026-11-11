namespace MightyCode.Core;

/// <summary>
/// The role view's words for the web page (native/windows/web/src/mighty/flow.ts): who each block is, a card's lines, the 실행 card, a step's card
/// and what it holds, the plan column, the delegate line, the return node and the session log, read with the very Core rules the Mac draws them by
/// (<see cref="FlowRoles"/>, <see cref="FlowCard"/>, <see cref="FlowSteps"/>, <see cref="FlowDelegation"/>, <see cref="FlowReturn"/>, <see cref="FlowLog"/>). The page lays
/// the diagram out itself and keeps what it reads against its own clock (a card's elapsed time) and its own drawing (which card is open, which row a
/// log click lands on); everything with a word or a model's name in it comes from here. Inks are design token names (<see cref="FlowRoles.Token"/>).
/// </summary>
public static class FlowCopy
{
    public sealed record Mark(string Name, string Help);
    /// <summary>Who a block is: its role, its ink, its type and the chip's words, the lead's effort, its marks and the whole of it in one line.</summary>
    public sealed record Role(string Name, string Ink, string? Type, string? TypeChip, string? Effort, string? EffortHelp, IReadOnlyList<Mark> Marks, string Summary, string? ModelLabel);
    /// <summary>A compact card. <c>Before</c>/<c>After</c> are the figures' parts on either side of its time, which the page reads against its own clock
    /// from <c>StartedAt</c>/<c>EndedAt</c> (milliseconds since 1970). <c>CopyNodeId</c>: where a finished background block's click goes. <c>Prose</c>: the
    /// activity is what the main session said, set as text rather than as a command. <c>Pending</c>: a step not begun, quiet, with no time running.
    /// <c>Step</c>: the step of the request's plan it was launched in ("2. 노트 작성", <see cref="FlowSteps.StepLabel"/>), on a line of its own;
    /// <c>StepHelp</c> says it in a sentence.</summary>
    public sealed record Card(string Title, string? Identity, string? Activity, string Status, string StateWord, IReadOnlyList<string> Before, IReadOnlyList<string> After,
        long? StartedAt, long? EndedAt, bool Running, int Nested, string? NestedHelp, IReadOnlyList<Mark> Marks, string Ink, string Summary, bool IsTask, string? CopyNodeId, string ActivateHelp,
        bool Prose = false, bool Pending = false, string? Step = null, string? StepHelp = null);
    public sealed record Part(string Text, string Ink);
    public sealed record Line(string Head, IReadOnlyList<Part> Parts, string? Model, string? Progress, string Text);
    public sealed record Back(string Text, bool Waiting);
    /// <summary>A step opened into its block (<see cref="FlowSteps"/>): its title and state, and the ids of the main session's records it holds, among the
    /// request's and its notice runs' root entries.</summary>
    public sealed record StepCopy(string Title, string StateWord, string Status, IReadOnlyList<string> EntryIds);
    /// <summary>A step's line in the progress panel. <c>NodeId</c>: the step's card, once it began. <c>State</c>: done, current, pending, stopped or failed.
    /// <c>StartedAt</c>/<c>EndedAt</c>: milliseconds since 1970, which the page reads against its own clock.</summary>
    public sealed record ProgressRow(int Index, string? NodeId, string Text, string State, long? StartedAt, long? EndedAt);
    /// <summary>A request's progress panel (<see cref="FlowSteps.ProgressOf"/>): "진행 상황", "요청 27", its first line, its state, and its plan's rows.
    /// <c>Hidden</c>: the steps the last line counts instead of listing; <c>More</c> its words. <c>RequestNodeId</c>: where the request line leads.</summary>
    public sealed record ProgressCopy(string Title, string Heading, string Request, string StateWord, string Status, string? Count, IReadOnlyList<ProgressRow> Rows,
        int Hidden, string? More, bool Running, string RequestNodeId, string RequestHelp);
    /// <summary>A block's role-view copy; each part only where it belongs.</summary>
    public sealed record Block(Role? Role = null, Card? Card = null, Line? Line = null, Back? Back = null, StepCopy? Step = null);
    public sealed record LogRow(string Id, string Clock, string Who, string Ink, string What, string NodeId);
    /// <summary>The session log strip: its status line in parts, and its rows while it is open.</summary>
    public sealed record Log(IReadOnlyList<Part> Status, string StatusText, IReadOnlyList<LogRow> Rows, bool Collapsed);

    private static List<Mark> Marks(IReadOnlyList<FlowMark> marks) => marks.Select(m => new Mark(m.ToString().ToLowerInvariant(), FlowRoles.Help(m))).ToList();
    private static string Ink(FlowInk ink) => FlowRoles.Token(ink);
    private static string Ink(FlowRole role) => FlowRoles.Token(FlowRoles.Ink(role));
    private static string Name(FlowRole role) => role.ToString().ToLowerInvariant();
    private static long? Milliseconds(DateTimeOffset? time) => time?.ToUnixTimeMilliseconds();

    private static Role RoleOf(FlowIdentity who) => new(Name(who.Role), Ink(who.Ink), who.Type, who.TypeChip, who.Effort,
        who.Effort is { } effort ? Locale.Get("composer.effort.menu", new Dictionary<string, string> { ["effort"] = effort }) : null, Marks(who.Marks), who.Summary,
        ModelUsageFormat.VersionedNodeLabel(who.ModelLabel));

    /// <summary>The run and suffix a node id names, among <paramref name="runs"/>.</summary>
    private static (MightyGraphRun Run, string Suffix)? Locate(string nodeId, IReadOnlyList<MightyGraphRun> runs)
    {
        foreach (var run in runs)
        {
            var prefix = MightyGraphLayout.NodeID(run, "");
            if (nodeId.StartsWith(prefix, StringComparison.Ordinal)) return (run, nodeId[prefix.Length..]);
        }
        return null;
    }

    /// <summary>
    /// The role-view copy of every node of <paramref name="layout"/> that has one. <paramref name="fanOut"/>: the pane draws the role view (a Claude pane):
    /// cards (the 실행 block's and the steps' too), delegate lines and the return node, and a background command's own identity. Without it a 실행 block still wears the
    /// lead's ink and effort and a sub-agent block its own, as on the Mac for every agent. <paramref name="definitions"/> are the custom agents the blocks'
    /// types name; <paramref name="output"/> a background command's last lines by its block id.
    /// </summary>
    public static Dictionary<string, Block> Blocks(MightyGraphLayout layout, IReadOnlyList<MightyGraphRun> runs, bool fanOut,
        IReadOnlyDictionary<string, AgentDefinition>? definitions = null, Func<string, IReadOnlyList<string>>? output = null)
    {
        definitions ??= new Dictionary<string, AgentDefinition>();
        var shown = MightyGraphSupport.ShownRuns(runs);
        var ordinals = MightyGraphSupport.RequestOrdinals(shown);
        var result = new Dictionary<string, Block>(StringComparer.Ordinal);
        AgentDefinition? Definition(MightyGraphAgent agent) => agent.Identity?.Type is { } type ? definitions.GetValueOrDefault(type) : null;
        foreach (var node in layout.Nodes)
        {
            if (Locate(node.Id, shown) is not var (run, suffix)) continue;
            switch (node.Kind)
            {
                case MightyGraphLayout.WorkKind:
                {
                    // The role view's 실행 card: who the main session is, what it is doing or answered, its figures.
                    var index = IndexOf(run, shown);
                    var lead = fanOut && index >= 0
                        ? CardOf(FlowSteps.LeadCard(run, BackgroundNoticeRuns.ExecutionTitle(run.NoticeRun == true, ordinals[index])), null, "graph.card.open") : null;
                    result[node.Id] = new(Role: RoleOf(FlowRoles.Lead(run)), Card: lead);
                    break;
                }
                case MightyGraphLayout.StepKind when fanOut && suffix.StartsWith(FlowSteps.Suffix, StringComparison.Ordinal):
                {
                    var index = IndexOf(run, shown);
                    var steps = FlowSteps.Steps(run);
                    var step = steps.ToList().FindIndex(s => FlowSteps.NodeSuffix(s) == suffix);
                    if (index < 0 || step < 0 || FlowSteps.Card(step, index, shown) is not { } content) break;
                    result[node.Id] = new(Card: CardOf(content, null, "graph.card.open"),
                        Step: new(content.Title, content.StateWord, content.Status, FlowSteps.Entries(step, index, shown).Select(e => e.Id).ToList()));
                    break;
                }
                case "agent" when suffix.StartsWith("agent:", StringComparison.Ordinal) && run.FlowAgents.FirstOrDefault(a => "agent:" + a.Id == suffix) is { } agent:
                {
                    var definition = Definition(agent);
                    var who = FlowRoles.Subagent(agent, definition) ?? (fanOut ? FlowRoles.Task(agent) : null);
                    Card? card = null;
                    if (fanOut && FlowCard.FoldsToCard(agent))
                    {
                        var content = FlowCard.Of(agent, run, definition, agent.IsTask ? output?.Invoke(agent.Id) ?? [] : []);
                        var copy = agent.Background == true && MightyGraphLayout.Terminal(agent.Status) ? FlowCard.CopyNodeID(agent, shown) : null;
                        card = CardOf(content, copy, copy is null ? "graph.card.open" : "graph.card.showCopy");
                        // A card launched in one of the request's steps names it (Mac MightyGraphAgentCard's step line).
                        if (FlowSteps.StepLabel(agent, run) is { } step)
                            card = card with { Step = step, StepHelp = Locale.Get("graph.card.inStep", new Dictionary<string, string> { ["step"] = step }) };
                    }
                    if (who is not null || card is not null) result[node.Id] = new(Role: who is null ? null : RoleOf(who), Card: card);
                    break;
                }
                case MightyGraphLayout.DelegateKind when fanOut && suffix.StartsWith(MightyGraphLayout.DelegateSuffix, StringComparison.Ordinal):
                    if (FlowDelegation.Of(FlowDelegation.Children(suffix[MightyGraphLayout.DelegateSuffix.Length..], run), definitions) is { } line)
                        result[node.Id] = new(Line: new(line.Head, line.Parts.Select(p => new Part(p.Text, Ink(p.Role))).ToList(), line.Model, line.Progress, line.Text));
                    break;
                case MightyGraphLayout.ReturnKind when fanOut:
                    if (FlowReturn.Of(run) is { } state) result[node.Id] = new(Back: new(state.Text, state.Waiting));
                    break;
            }
        }
        return result;
    }

    private static int IndexOf(MightyGraphRun run, IReadOnlyList<MightyGraphRun> runs)
    {
        for (var i = 0; i < runs.Count; i++) if (ReferenceEquals(runs[i], run) || runs[i].Id == run.Id) return i;
        return -1;
    }

    private static Card CardOf(FlowCard.Content content, string? copy, string activateKey)
    {
        var (before, after) = FlowCard.FigureParts(content);
        return new(content.Title, content.Identity, content.Activity, content.Status, content.StateWord, before, after,
            Milliseconds(content.StartedAt), Milliseconds(content.EndedAt), content.Running, content.Nested,
            content.Nested > 0 ? Locale.Get("graph.card.nested", new Dictionary<string, string> { ["count"] = content.Nested.ToString(System.Globalization.CultureInfo.InvariantCulture) }) : null,
            Marks(content.Marks), Ink(content.Ink), content.Summary, content.IsTask, copy, Locale.Get(activateKey), content.Prose, content.IsPending);
    }

    /// <summary>The progress panel's copy of every request of a pane (no notice run), by its run id (<see cref="FlowSteps.ProgressOf"/>): the page shows
    /// the one in view, or a new request until the user scrolls.</summary>
    public static Dictionary<string, ProgressCopy> ProgressOf(IReadOnlyList<MightyGraphRun> runs)
    {
        var shown = MightyGraphSupport.ShownRuns(runs);
        var ordinals = MightyGraphSupport.RequestOrdinals(shown);
        var result = new Dictionary<string, ProgressCopy>(StringComparer.Ordinal);
        for (var index = 0; index < shown.Count; index++)
        {
            var run = shown[index];
            if (run.NoticeRun == true) continue;
            var heading = Locale.Get("graph.timeline.requestOrdinal", new Dictionary<string, string> { ["n"] = ordinals[index].ToString(System.Globalization.CultureInfo.InvariantCulture) });
            var progress = FlowSteps.ProgressOf(index, shown, heading);
            var steps = FlowSteps.Steps(run);
            var rows = progress.Rows.Select(row => new ProgressRow(row.Index,
                row.State == FlowSteps.ProgressState.Pending ? null : MightyGraphLayout.NodeID(run, FlowSteps.NodeSuffix(steps[row.Index])),
                row.Text, row.State.ToString().ToLowerInvariant(), Milliseconds(row.StartedAt), Milliseconds(row.EndedAt))).ToList();
            var more = progress.Hidden > 0
                ? Locale.Get("graph.progress.more", new Dictionary<string, string> { ["count"] = progress.Hidden.ToString(System.Globalization.CultureInfo.InvariantCulture) }) : null;
            result[run.Id] = new(progress.Title, progress.Heading, progress.Request, progress.StateWord, progress.Status, progress.Count, rows, progress.Hidden, more,
                progress.Running, MightyGraphLayout.NodeID(run, "request"), Locale.Get("graph.progress.showRequest"));
        }
        return result;
    }

    /// <summary>The session log strip of a pane's runs, null for a pane without a request (the page draws none). <paramref name="collapsed"/>: the pane
    /// folded it (<see cref="RunSession.FlowLogCollapsed"/>), which reads no rows.</summary>
    public static Log? LogOf(IReadOnlyList<MightyGraphRun> runs, bool collapsed, IReadOnlyDictionary<string, AgentDefinition>? definitions = null)
    {
        var shown = MightyGraphSupport.ShownRuns(runs);
        if (FlowLog.StatusOf(shown) is not { } status) return null;
        var rows = collapsed ? [] : FlowLog.Rows(shown, definitions: definitions)
            .Select(row => new LogRow(row.Id, FlowLog.Clock(row.Time), row.Who, Ink(row.Ink), row.What, row.NodeID)).ToList();
        return new(status.Parts.Select(p => new Part(p.Text, Ink(p.Role))).ToList(), status.Text, rows, collapsed);
    }

    /// <summary>The pane with its log strip folded or open (<see cref="RunSession.FlowLogCollapsed"/>: written only when folded).</summary>
    public static RunSession Collapse(RunSession pane, bool collapsed) => pane with { FlowLogCollapsed = collapsed ? true : null };
}
