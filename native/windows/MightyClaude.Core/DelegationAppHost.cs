using System.Threading.Channels;

namespace MightyClaude.Core;

/// <summary>
/// How the app's <see cref="IDelegationHost"/> makes and runs a delegated child's pane (M/DelegationAppHost.swift:3-54). The
/// desktop service applies these rules; the checks test them without the app.
/// </summary>
public static class DelegationPanes
{
    /// <summary>
    /// A new Claude pane for <paramref name="pane"/> in the workspace <paramref name="workspaceId"/>. Its mode is the starting
    /// mode the parent asked for, written here once and never copied from the last-used pane; it is linked to its parent and
    /// works in its worktree folder.
    /// </summary>
    public static RunSession ChildSession(DelegationChildPane pane, string workspaceId, string title) =>
        new() { Id = pane.SessionId, WorkspaceId = workspaceId, Title = title, Kind = "claude", Provider = "claude", Settings = new(PermissionMode: pane.Mode), ParentSessionId = pane.ParentSessionId, WorkingFolder = pane.Folder };

    /// <summary>
    /// <paramref name="state"/> with the child's pane as a tab beside its parent (M/AppStore+Delegation.swift:320-347): its title is
    /// its task <paramref name="task"/>, kept as the pane's name, and its parent's tab group keeps showing what it showed. The
    /// layout is saved as it then is, and the workspace leaves a preset layout mode, as when a human adds a tab; focus stays
    /// focus. null, changing nothing, when no pane can be made: the app is at its pane cap, the id is taken, or the parent pane
    /// or its workspace is gone.
    /// </summary>
    public static AppSnapshot? AddingChild(AppSnapshot state, DelegationChildPane pane, string? task)
    {
        if (!PaneLayout.HasRoom(state) || state.Sessions.Any(session => session.Id == pane.SessionId) || state.Sessions.FirstOrDefault(session => session.Id == pane.ParentSessionId) is not { } parent
            || !state.Workspaces.Any(workspace => workspace.Id == parent.WorkspaceId)) return null;
        var child = ChildSession(pane, parent.WorkspaceId, PaneTitle.Shortened(task) ?? ProviderCatalog.Name("claude")) with { TitleMode = PaneTitle.Fixed };
        var open = state.Sessions.Where(session => session.WorkspaceId == parent.WorkspaceId).Select(session => session.Id).ToList();
        var mode = state.PaneLayoutModes?.GetValueOrDefault(parent.WorkspaceId) ?? state.Layout;
        var active = state.ActiveWorkspaceId == parent.WorkspaceId ? state.ActiveSessionId : state.PaneLayoutActiveSessionIds?.GetValueOrDefault(parent.WorkspaceId);
        var root = PaneLayout.Normalize(state.PaneLayouts?.GetValueOrDefault(parent.WorkspaceId) ?? PaneLayout.Preset(open, mode, active), open);
        if (PaneLayout.Groups(root).FirstOrDefault(tabs => tabs.SessionIds.Contains(parent.Id)) is not { } group) return null;
        // A tab moved into a group is selected there; the group goes back to the tab it showed.
        var next = PaneLayout.Move(PaneLayout.Normalize(root, open.Append(child.Id))!, child.Id, group.Id, index: group.SessionIds.IndexOf(parent.Id) + 1);
        next = PaneLayout.Select(next, group.SelectedSessionId ?? parent.Id);
        return state with
        {
            Sessions = [.. state.Sessions, child],
            PaneLayouts = new Dictionary<string, PaneLayoutNode>(state.PaneLayouts ?? []) { [parent.WorkspaceId] = next },
            PaneLayoutModes = new Dictionary<string, string>(state.PaneLayoutModes ?? []) { [parent.WorkspaceId] = mode == "focus" ? mode : next.Kind == "split" ? "custom" : "tabs" },
        };
    }

    /// <summary>
    /// The workspace a run of <paramref name="session"/> starts with. A child's run, its terminal tool and its MCP binding all
    /// work in the child's worktree folder; any other pane's in the workspace folder.
    /// </summary>
    public static Workspace RunWorkspace(RunSession session, Workspace workspace) => session.WorkingFolder is { } folder ? workspace with { Path = folder } : workspace;

    /// <summary>
    /// <c>worktree_missing</c> when <paramref name="session"/> is a child whose worktree folder is gone (or was never saved), so
    /// it may not run; null otherwise. A child never runs in its parent's checkout instead.
    /// </summary>
    public static DelegationReasonCode? RunRefusal(RunSession session, Func<string, bool>? folderExists = null)
    {
        if (session.ParentSessionId is null && session.WorkingFolder is null) return null;
        return session.WorkingFolder is { } folder && (folderExists ?? Directory.Exists)(folder) ? null : DelegationReasonCode.WorktreeMissing;
    }

    /// <summary>A working folder a saved pane may keep (macOS StateRepository.absolutePath): a full path of at most 4096 characters with no NUL.</summary>
    public static bool IsWorkingFolder(string? path) => path is { Length: <= 4096 } && !path.Contains('\0') && Path.IsPathFullyQualified(path);

    /// <summary>
    /// The pane <paramref name="session"/> of <paramref name="workspace"/> as the coordinator reads it.
    /// <paramref name="startRefused"/> says the app's start would refuse a run in the pane now: a CLI update is going, the pane
    /// is blocked (a child without its worktree among others), or its request does not validate. A pane idle after a normal
    /// finish then reads as idle, so what comes for it is held as a row of its queued list instead of being handed a run the
    /// app would refuse. A running pane still reads as running.
    /// </summary>
    public static DelegationPaneState PaneState(RunSession session, Workspace workspace, string? runId, DelegationPaneActivity activity, bool startRefused = false) =>
        new(session.Id, session.Settings.PermissionMode, RunWorkspace(session, workspace).Path, session.Kind, session.Provider, session.ParentSessionId, runId,
            activity == DelegationPaneActivity.Finished && startRefused ? DelegationPaneActivity.Idle : activity);
}

/// <summary>
/// The runs of the app's panes as delegation names them (M/DelegationAppHost.swift:56-100): each run the app starts gets an id,
/// and a pane that finished a run normally since this launch is told apart from one idle any other way.
/// </summary>
public sealed class DelegationRunLedger
{
    private readonly object gate = new();
    private readonly Dictionary<string, string> runIds = [];
    private readonly HashSet<string> going = [], finished = [];

    /// <summary>A new run starts in the pane <paramref name="sessionId"/>: its id.</summary>
    public string Begin(string sessionId)
    {
        var runId = "run-" + Wire.Id();
        lock (gate) { runIds[sessionId] = runId; going.Add(sessionId); finished.Remove(sessionId); }
        return runId;
    }

    /// <summary>The pane's run going now, or its last since this launch.</summary>
    public string? RunId(string sessionId) { lock (gate) return runIds.GetValueOrDefault(sessionId); }

    /// <summary>
    /// The pane's run ended with the app status <paramref name="status"/> ("completed", "stopped" or "error");
    /// <paramref name="quitting"/> while the app quits. The run and how it ended, or null when no run of this launch was going:
    /// a second end of the same run is not another end.
    /// </summary>
    public (string RunId, ChildRunEnd End)? End(string sessionId, string status, bool quitting)
    {
        lock (gate)
        {
            if (!runIds.TryGetValue(sessionId, out var runId) || !going.Remove(sessionId)) return null;
            var end = quitting ? ChildRunEnd.Quit : status == "completed" ? ChildRunEnd.Finished : status == "stopped" ? ChildRunEnd.Stopped : ChildRunEnd.Errored;
            if (end == ChildRunEnd.Finished) finished.Add(sessionId);
            return (runId, end);
        }
    }

    /// <summary>What the pane is doing: <paramref name="running"/> when a run is going or starting.</summary>
    public DelegationPaneActivity Activity(string sessionId, bool running)
    {
        lock (gate) return running ? DelegationPaneActivity.Running : finished.Contains(sessionId) ? DelegationPaneActivity.Finished : DelegationPaneActivity.Idle;
    }

    /// <summary>The pane closed.</summary>
    public void Forget(string sessionId) { lock (gate) { runIds.Remove(sessionId); going.Remove(sessionId); finished.Remove(sessionId); } }
}

/// <summary>A run event of a child's pane, as the host reports it (M/DelegationAppHost.swift:102-106).</summary>
public abstract record DelegationRunEvent
{
    public sealed record Started(string ChildId, string RunId) : DelegationRunEvent;
    public sealed record Ended(string ChildId, string RunId, ChildRunEnd End) : DelegationRunEvent;
}

/// <summary>
/// Hands a host's child run events on one at a time, in the order they happened (M/DelegationAppHost.swift:108-137), so a run's
/// end never overtakes its start.
/// </summary>
public sealed class DelegationRunEventPump
{
    private readonly Channel<DelegationRunEvent> events = Channel.CreateUnbounded<DelegationRunEvent>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task pump;

    public DelegationRunEventPump(Func<DelegationRunEvent, Task> deliver) =>
        pump = Task.Run(async () =>
        {
            // One event whose handling failed never stops the ones after it.
            await foreach (var change in events.Reader.ReadAllAsync())
                try { await deliver(change); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
        });

    /// <summary>Each event reaches the coordinator's record of its child's runs.</summary>
    public DelegationRunEventPump(DelegationCoordinator coordinator) : this(change => change switch
    {
        DelegationRunEvent.Started started => coordinator.ChildRunStarted(started.ChildId, started.RunId),
        DelegationRunEvent.Ended ended => coordinator.ChildRunEnded(ended.ChildId, ended.RunId, ended.End),
        _ => Task.CompletedTask,
    }) { }

    public void Send(DelegationRunEvent change) => events.Writer.TryWrite(change);

    /// <summary>Takes no more events and returns once every one sent is handed on.</summary>
    public Task Finish() { events.Writer.TryComplete(); return pump; }
}
