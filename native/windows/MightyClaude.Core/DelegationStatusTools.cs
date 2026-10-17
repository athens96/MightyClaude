namespace MightyClaude.Core;

/// <summary>One child as list_children shows it (M/DelegationStatusTools.swift DelegationChildSummary).</summary>
/// <param name="Revision">The current report revision: 0 until the first report.</param>
/// <param name="Agent">The agent the child runs, as delegate's <c>agent</c> names it.</param>
public sealed record DelegationChildSummary(string Id, ChildState State, int Revision, string Agent = "claude")
{
    public static DelegationChildSummary Of(ChildRecord child) => new(child.Id, child.State, child.ReportRevision, DelegationProviders.AgentName(child.Agent));
}

/// <summary>One child as child_status shows it: where it stands and what it last reported (M/DelegationStatusTools.swift DelegationChildStatus).</summary>
/// <param name="Revision">The current report revision: 0 until the first report.</param>
/// <param name="Head">The head the current report was made at, which merge takes as its expected_head. null until the first report.</param>
/// <param name="Report">The stored copy of the current revision's REPORT.md, cut at 64 KiB with a visible marker. null until the first report, or once pruning dropped it.</param>
/// <param name="ReportTruncated">The report is longer than its copy; the whole file stays in the child's worktree until cleanup.</param>
/// <param name="Agent">The agent the child runs, as delegate's <c>agent</c> names it.</param>
public sealed record DelegationChildStatus(string Id, ChildState State, int Revision, string? Head, string? Report, bool ReportTruncated, string Agent = "claude")
{
    /// <summary><paramref name="child"/> with <paramref name="report"/>, its stored REPORT.md copy, when that copy holds the current revision.</summary>
    public static DelegationChildStatus Of(ChildRecord child, DelegationCopy? report)
    {
        var current = report is not null && report.ChildId == child.Id && report.Kind == DelegationCopyKind.Report && report.Revision == child.ReportRevision && child.ReportRevision > 0 ? report : null;
        return new(child.Id, child.State, child.ReportRevision, child.ReportHead, current?.Text, current?.Truncated ?? false, DelegationProviders.AgentName(child.Agent));
    }
}

/// <summary>
/// The status tools (M/DelegationStatusTools.swift:39-58): a parent reads where its children stand. Both only read the
/// delegation file, so neither changes anything.
/// </summary>
public sealed partial class DelegationCoordinator
{
    /// <summary>list_children from <paramref name="caller"/>: its children in the order they were made, each with its id, agent, state and current report revision. None is an empty list.</summary>
    private DelegationResponse ListChildren(DelegationCaller caller) =>
        new(Children: File.Children.Where(child => child.ParentSessionId == caller.PaneId && child.NotifiesParent).Select(DelegationChildSummary.Of).ToList());

    /// <summary>
    /// child_status(child) from <paramref name="caller"/>: that child's state, the head of its current report and that report's
    /// body. An id that is not one of the caller's children is answered with an error.
    /// </summary>
    private DelegationResponse ChildStatus(string id, DelegationCaller caller)
    {
        id = id.Trim();
        return File.Children.Find(record => record.Id == id && record.ParentSessionId == caller.PaneId && record.NotifiesParent) is { } child
            ? new(Status: DelegationChildStatus.Of(child, File.Copy(child.Id, DelegationCopyKind.Report))) : DelegationResponse.Failure(NotYourChildMessage(id));
    }

    public static string NotYourChildMessage(string id) => $"No child \"{DelegationNames.Prefix(id, 80)}\" of yours. list_children names your children.";
}
