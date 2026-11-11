namespace MightyCode.Core;

/// <summary>
/// The role view's words for the web page (native/windows/web/src/mighty/flow.ts): who each block is, a card's four lines, the delegate line, the
/// return node, the advisor's band and the session log, read with the very Core rules the Mac draws them by (<see cref="FlowRoles"/>,
/// <see cref="FlowCard"/>, <see cref="FlowDelegation"/>, <see cref="FlowReturn"/>, <see cref="FlowAdvisor"/>, <see cref="FlowLog"/>). The page lays
/// the diagram out itself and keeps what it reads against its own clock (a card's elapsed time) and its own drawing (which card is open, which row a
/// log click lands on); everything with a word or a model's name in it comes from here. Inks are design token names (<see cref="FlowRoles.Token"/>).
/// </summary>
public static class FlowCopy
{
    public sealed record Mark(string Name, string Help);
    /// <summary>Who a block is: its role, its ink, its type and the chip's words, the lead's effort, its marks and the whole of it in one line.</summary>
    public sealed record Role(string Name, string Ink, string? Type, string? TypeChip, string? Effort, string? EffortHelp, IReadOnlyList<Mark> Marks, string Summary, string? ModelLabel);
    /// <summary>A compact card. <c>Before</c>/<c>After</c> are the figures' parts on either side of its time, which the page reads against its own clock
    /// from <c>StartedAt</c>/<c>EndedAt</c> (milliseconds since 1970). <c>CopyNodeId</c>: where a finished background block's click goes.</summary>
    public sealed record Card(string Title, string? Identity, string? Activity, string Status, string StateWord, IReadOnlyList<string> Before, IReadOnlyList<string> After,
        long? StartedAt, long? EndedAt, bool Running, int Nested, string? NestedHelp, IReadOnlyList<Mark> Marks, string Ink, string Summary, bool IsTask, string? CopyNodeId, string ActivateHelp);
    public sealed record Part(string Text, string Ink);
    public sealed record Line(string Head, IReadOnlyList<Part> Parts, string? Model, string? Progress, string Text);
    public sealed record Back(string Text, bool Waiting);
    public sealed record AdvisorRow(string Id, string Caller, string Ink, string CallerNodeId, string When, string Line, string State, string Help);
    /// <summary>The advisor's band, every row it keeps: the page shows the newest that fit its height (<see cref="FlowAdvisor.Visible"/>).</summary>
    public sealed record Advisor(string Header, string? Model, string? State, bool Consulting, IReadOnlyList<AdvisorRow> Rows, int Earlier, int Calls, string Totals, string? TotalsHelp,
        string? Note, string? Empty, bool HidesText);
    /// <summary>A block's role-view copy; each part only where it belongs.</summary>
    public sealed record Block(Role? Role = null, Card? Card = null, Line? Line = null, Back? Back = null, Advisor? Advisor = null);
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
    /// cards, delegate lines, the return node and the advisor's band, and a background command's own identity. Without it a 실행 block still wears the
    /// lead's ink and effort and a sub-agent block its own, as on the Mac for every agent. <paramref name="definitions"/> are the custom agents the blocks'
    /// types name; <paramref name="output"/> a background command's last lines by its block id.
    /// </summary>
    public static Dictionary<string, Block> Blocks(MightyGraphLayout layout, IReadOnlyList<MightyGraphRun> runs, bool fanOut,
        IReadOnlyDictionary<string, AgentDefinition>? definitions = null, Func<string, IReadOnlyList<string>>? output = null)
    {
        definitions ??= new Dictionary<string, AgentDefinition>();
        var shown = MightyGraphSupport.ShownRuns(runs);
        var result = new Dictionary<string, Block>(StringComparer.Ordinal);
        AgentDefinition? Definition(MightyGraphAgent agent) => agent.Identity?.Type is { } type ? definitions.GetValueOrDefault(type) : null;
        foreach (var node in layout.Nodes)
        {
            if (Locate(node.Id, shown) is not var (run, suffix)) continue;
            switch (node.Kind)
            {
                case MightyGraphLayout.WorkKind:
                    result[node.Id] = new(Role: RoleOf(FlowRoles.Lead(run)));
                    break;
                case "agent" when suffix.StartsWith("agent:", StringComparison.Ordinal) && run.FlowAgents.FirstOrDefault(a => "agent:" + a.Id == suffix) is { } agent:
                {
                    var definition = Definition(agent);
                    var who = FlowRoles.Subagent(agent, definition) ?? (fanOut ? FlowRoles.Task(agent) : null);
                    Card? card = null;
                    if (fanOut && FlowCard.FoldsToCard(agent))
                    {
                        var content = FlowCard.Of(agent, run, definition, agent.IsTask ? output?.Invoke(agent.Id) ?? [] : []);
                        var copy = agent.Background == true && MightyGraphLayout.Terminal(agent.Status) ? FlowCard.CopyNodeID(agent, shown) : null;
                        var (before, after) = FlowCard.FigureParts(content);
                        card = new(content.Title, content.Identity, content.Activity, content.Status, content.StateWord, before, after,
                            Milliseconds(content.StartedAt), Milliseconds(content.EndedAt), content.Running, content.Nested,
                            content.Nested > 0 ? Locale.Get("graph.card.nested", new Dictionary<string, string> { ["count"] = content.Nested.ToString(System.Globalization.CultureInfo.InvariantCulture) }) : null,
                            Marks(content.Marks), Ink(content.Ink), content.Summary, content.IsTask, copy, Locale.Get(copy is null ? "graph.card.open" : "graph.card.showCopy"));
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
                case MightyGraphLayout.AdvisorKind when fanOut:
                    if (FlowAdvisor.Of(run, definitions) is { } band) result[node.Id] = new(Advisor: AdvisorOf(band));
                    break;
            }
        }
        return result;
    }

    private static Advisor AdvisorOf(FlowAdvisor.Band band)
    {
        var rows = band.Rows.Select(row =>
        {
            var when = row.Started is { } started ? FlowLog.Clock(started) + (row.Seconds is { } seconds ? " · " + StylePresentation.Elapsed(started, started.AddSeconds(seconds)) : "") : "";
            // The advice itself only when the CLI gave it as text.
            var help = string.Join("\n", new[] { row.Caller + " · " + row.Line, row.Text, Locale.Get("graph.advisor.reveal") }.OfType<string>());
            return new AdvisorRow(row.Id, row.Caller, Ink(row.Ink), row.CallerNodeID, when, row.Line, row.State, help);
        }).ToList();
        return new(band.Header, band.Model, band.Advising is { } state ? FlowAdvisor.Words(state) : null, band.Advising == FlowAdvisor.State.Consulting, rows, band.Earlier,
            band.Calls, band.Totals, band.Usage?.Detail, band.Note, band.Empty, band.HidesText);
    }

    /// <summary>The session log strip of a pane's runs, null for a pane without a request (the page draws none). <paramref name="collapsed"/>: the pane
    /// folded it (<see cref="RunSession.FlowLogCollapsed"/>), which reads no rows.</summary>
    public static Log? LogOf(IReadOnlyList<MightyGraphRun> runs, bool collapsed, IReadOnlyDictionary<string, AgentDefinition>? definitions = null)
    {
        var shown = MightyGraphSupport.ShownRuns(runs);
        if (FlowLog.StatusOf(shown, FlowAdvisor.Status(shown)) is not { } status) return null;
        var rows = collapsed ? [] : FlowLog.Rows(shown, definitions: definitions, advisor: FlowAdvisor.Notes(shown))
            .Select(row => new LogRow(row.Id, FlowLog.Clock(row.Time), row.Who, Ink(row.Ink), row.What, row.NodeID)).ToList();
        return new(status.Parts.Select(p => new Part(p.Text, Ink(p.Role))).ToList(), status.Text, rows, collapsed);
    }

    /// <summary>The pane with its log strip folded or open (<see cref="RunSession.FlowLogCollapsed"/>: written only when folded).</summary>
    public static RunSession Collapse(RunSession pane, bool collapsed) => pane with { FlowLogCollapsed = collapsed ? true : null };
}
