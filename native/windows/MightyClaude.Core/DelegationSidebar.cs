namespace MightyClaude.Core;

/// <summary>
/// One delegated child as the sidebar lists it (M/DelegationSidebar.swift:3-36): its record's state and parent link, its task in
/// one line for a row whose pane is gone, and how many of its notices are held.
/// </summary>
/// <param name="Id">The child pane's session id.</param>
/// <param name="Task">TASK.md shortened to one line, from its stored copy; null without one.</param>
/// <param name="ParentBranch">The parent's branch when the child was made.</param>
/// <param name="ParentCheckout">The top folder of the parent's checkout when the child was made.</param>
/// <param name="CanUndo">Whether the child's card can undo a merge (<see cref="DelegationFile.UndoableMerge"/>).</param>
/// <param name="HeldNotices">How many of the child's notices are held. With its parent pane closed nothing releases them, and the child's card shows the count.</param>
/// <param name="StartedByHuman">The human started it from its parent's composer.</param>
/// <param name="Agent">The agent CLI it runs (<see cref="ChildRecord.Agent"/>).</param>
public sealed record DelegationChildRow(string Id, string ParentSessionId, ChildState State, string? Task = null, string ParentBranch = "", string? ParentCheckout = null, bool CanUndo = false, int HeldNotices = 0,
    bool StartedByHuman = false, string Agent = "claude")
{
    /// <summary>The row of <paramref name="record"/>, its task read from <paramref name="file"/>'s stored copy.</summary>
    public static DelegationChildRow Of(ChildRecord record, DelegationFile file) => new(record.Id, record.ParentSessionId, record.State,
        file.Copy(record.Id, DelegationCopyKind.Task) is { } task ? PaneTitle.Shortened(task.Text) : null, record.ParentBranch, record.ParentCheckout,
        file.UndoableMerge(record.Id) is not null, file.HeldNotices(record.Id).Count, !record.NotifiesParent, record.Agent);

    /// <summary>
    /// The actions the child's card offers now, in this order (M/DelegationCards.swift:13-24): merge while it is reported, undo
    /// while its last merge can be undone, and discard until it is discarded.
    /// </summary>
    public IReadOnlyList<DelegationCardAction> CardActions =>
    [
        .. State == ChildState.Reported ? new[] { DelegationCardAction.Merge } : [],
        .. CanUndo ? new[] { DelegationCardAction.Undo } : [],
        .. State != ChildState.Discarded ? new[] { DelegationCardAction.Discard } : [],
    ];
}

/// <summary>The children of one closed parent pane (M/DelegationSidebar.swift:93-100).</summary>
/// <param name="Id">The closed parent's session id.</param>
/// <param name="ParentBranch">The parent's branch when its first child here was made.</param>
public sealed record DelegationParentClosed(string Id, string ParentBranch, IReadOnlyList<DelegationChildRow> Children);

/// <summary>One workspace's panes as the sidebar lists them (M/DelegationSidebar.swift:78-91).</summary>
/// <param name="Top">The panes at the top level, in the snapshot's order: every pane of the workspace that is not listed as a child.</param>
/// <param name="Children">Each open parent pane's children, by the parent's session id, in the order they were made.</param>
/// <param name="ParentClosed">A 'parent closed' node for each closed parent with children listed here, in the order its first child was made.</param>
public sealed record DelegationTree(IReadOnlyList<RunSession> Top, IReadOnlyDictionary<string, IReadOnlyList<DelegationChildRow>> Children, IReadOnlyList<DelegationParentClosed> ParentClosed);

/// <summary>
/// How the sidebar lists delegated children (M/DelegationSidebar.swift:38-141): each under its parent pane with its state, and
/// the children of a closed parent under a 'parent closed' node. The delegation file is the authority for each child's link and
/// state; the open panes come from the snapshot. The tree does not depend on the hidden switch, so existing children stay
/// listed with it off.
/// </summary>
public static class DelegationSidebar
{
    /// <summary>Every child the file holds except discarded ones, in the order they were made.</summary>
    public static IReadOnlyList<DelegationChildRow> Rows(DelegationFile file) => file.Children.Where(child => child.State != ChildState.Discarded).Select(child => DelegationChildRow.Of(child, file)).ToList();

    /// <summary>
    /// The open children of the parent pane <paramref name="parentId"/>, for its close to ask about: the open ones among
    /// <paramref name="children"/>, and the child panes in <paramref name="sessions"/> that <paramref name="children"/> does not
    /// list yet, as for a child made a moment ago whose row has not arrived.
    /// </summary>
    public static int OpenChildCount(string parentId, IEnumerable<DelegationChildRow> children, IEnumerable<RunSession> sessions)
    {
        var listed = children.Where(child => child.ParentSessionId == parentId).ToList();
        var known = listed.Select(child => child.Id).ToHashSet();
        return listed.Count(child => child.State.IsOpen()) + sessions.Count(session => session.ParentSessionId == parentId && !known.Contains(session.Id));
    }

    /// <summary>How many open children the human started from the parent pane <paramref name="parentId"/>, as the worktree button's menu counts them toward its cap.</summary>
    public static int OpenHumanChildCount(string parentId, IEnumerable<DelegationChildRow> children) =>
        children.Count(child => child.ParentSessionId == parentId && child.StartedByHuman && child.State.IsOpen());

    /// <summary>The state a child's row shows: waiting while a running child's pane has a request only a human can answer, otherwise its record's state.</summary>
    public static ChildState ShownState(ChildState state, bool asksHuman) => state == ChildState.Running && asksHuman ? ChildState.Waiting : state;

    /// <summary>The tone of a row's state word and glyph.</summary>
    public static DesignTone Tone(ChildState state) => state switch
    {
        ChildState.Creating or ChildState.Running => DesignTone.Run,
        ChildState.Waiting => DesignTone.Wait,
        ChildState.Reported or ChildState.Merged => DesignTone.Done,
        ChildState.Failed => DesignTone.Err,
        ChildState.Interrupted => DesignTone.Stop,
        _ => DesignTone.Idle,
    };

    /// <summary>
    /// The tree of the workspace <paramref name="workspaceId"/>. A child is listed once, under its parent pane while that is
    /// open, otherwise under its closed parent's node in the workspace of its own pane or, with that closed too, of its
    /// parent's checkout. A pane that is not listed as a child stays at the top level, so no pane ever drops out of the sidebar.
    /// </summary>
    public static DelegationTree Tree(string workspaceId, IReadOnlyList<Workspace> workspaces, IReadOnlyList<RunSession> sessions, IEnumerable<DelegationChildRow> children)
    {
        var panes = sessions.DistinctBy(session => session.Id).ToDictionary(session => session.Id);
        var open = new Dictionary<string, List<DelegationChildRow>>(); var closed = new List<(string Id, string ParentBranch, List<DelegationChildRow> Children)>();
        var listed = new HashSet<string>();
        foreach (var child in children)
        {
            if (panes.TryGetValue(child.ParentSessionId, out var parent))
            {
                if (parent.WorkspaceId != workspaceId) continue;
                if (!open.TryGetValue(parent.Id, out var rows)) open[parent.Id] = rows = [];
                rows.Add(child);
            }
            else
            {
                if (ListedWorkspaceId(child, workspaces, panes) != workspaceId) continue;
                if (closed.FindIndex(node => node.Id == child.ParentSessionId) is >= 0 and var index) closed[index].Children.Add(child);
                else closed.Add((child.ParentSessionId, child.ParentBranch, [child]));
            }
            listed.Add(child.Id);
        }
        return new(sessions.Where(session => session.WorkspaceId == workspaceId && !listed.Contains(session.Id)).ToList(),
            open.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<DelegationChildRow>)pair.Value),
            closed.Select(node => new DelegationParentClosed(node.Id, node.ParentBranch, node.Children)).ToList());
    }

    /// <summary>
    /// The workspace a child of a closed parent is listed in: its own pane's; with that closed, the local workspace at its parent's
    /// checkout, or else the first local one inside it; null when there is none. A remote workspace's folder is its host's path, never
    /// one on this PC, so it never matches. Folders compare the Windows way: without regard to case or to which separator is written.
    /// </summary>
    public static string? ListedWorkspaceId(DelegationChildRow child, IReadOnlyList<Workspace> workspaces, IReadOnlyDictionary<string, RunSession> panes)
    {
        if (panes.TryGetValue(child.Id, out var pane)) return pane.WorkspaceId;
        if (child.ParentCheckout is not { } recorded) return null;
        var checkout = DelegationPaths.Standardized(recorded);
        var paths = workspaces.Where(workspace => workspace.Remote is null).Select(workspace => (workspace.Id, Path: DelegationPaths.Standardized(workspace.Path))).ToList();
        var index = paths.FindIndex(workspace => DelegationPaths.Same(workspace.Path, checkout));
        if (index < 0) index = paths.FindIndex(workspace => DelegationPaths.IsInside(workspace.Path, checkout));
        return index < 0 ? null : paths[index].Id;
    }
}
