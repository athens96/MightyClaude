using System.Runtime.ExceptionServices;
using System.Threading.Channels;

namespace MightyClaude.Core;

/// <summary>What a child's card action did (M/AppStore+Delegation.swift:6-17).</summary>
public abstract record DelegationCardResult
{
    public sealed record Done : DelegationCardResult;
    /// <summary>Done in git, but the delegation file could not be saved (<paramref name="Reason"/>); the next save writes it.</summary>
    public sealed record DoneUnsaved(string Reason) : DelegationCardResult;
    /// <summary>Refused with this reason; nothing changed.</summary>
    public sealed record Refused(DelegationReasonCode Reason) : DelegationCardResult;
    public sealed record Failed(string Message) : DelegationCardResult;
    /// <summary>More worktrees are nested in the child's than its discard's confirmation named: nothing was removed, and it asks again with these.</summary>
    public sealed record Reconfirm(IReadOnlyList<string> Nested) : DelegationCardResult;
}

/// <summary>The last answer a child's card got, shown on the card until its next action (M/AppStore+Delegation.swift:4-21).</summary>
public sealed record DelegationCardNote(DelegationCardAction Action, DelegationCardResult Result);

/// <summary>A discard from a child's card waiting on the human's confirmation (M/AppStore+Delegation.swift:23-34).</summary>
/// <param name="Id">The child's id.</param>
/// <param name="Branch">The child's branch, <c>mighty/&lt;id&gt;</c>.</param>
/// <param name="Nested">
/// The worktrees nested in the child's that go with it, deepest first; null when they could not be listed, and then the child
/// cannot be discarded now: the confirmation says why and offers no discard.
/// </param>
public sealed record PendingChildDiscard(string Id, string Title, string Branch, IReadOnlyList<string>? Nested);

/// <summary>
/// A close of a parent pane whose children are still open, waiting on the human's confirmation (M/AppStore+Delegation.swift:36-45):
/// only the parent closes, and its children stay as cards under a 'parent closed' node.
/// </summary>
/// <param name="OpenChildren">How many of its children are open.</param>
public sealed record PendingParentClose(string Id, string Title, int OpenChildren);

/// <summary>
/// The app side of parent → child delegation between Claude panes (M/AppStore+Delegation.swift:47-51): the service is the
/// coordinator's <see cref="IDelegationHost"/>. The coordinator owns the records, the tools, delivery and the git actions; the
/// service only makes panes, starts and steers runs and reports what its panes do.
/// </summary>
public sealed partial class DesktopService : IDelegationHost
{
    private readonly Func<bool> delegationEnabled;
    private readonly ChildWorktreeMaker? worktrees;
    /// <summary>The id of each pane's run as delegation names it, and which panes finished a run normally since this launch.</summary>
    private readonly DelegationRunLedger delegationRuns = new();
    /// <summary>The child panes with a run going, whose end the coordinator hears even when the pane closes first.</summary>
    private readonly HashSet<string> childRuns = [];
    private readonly HashSet<string> cardBusy = [];
    private readonly Dictionary<string, DelegationCardNote> cardNotes = [];
    /// <summary>What closing each delegated child's pane did to its worktree and branch.</summary>
    private readonly Dictionary<string, Task<ChildCleanupOutcome>> childCloses = [];
    private CancellationTokenSource delegationWatchStop = new();
    private DelegationCoordinator? delegation;
    /// <summary>Child panes' run starts and ends on their way to <see cref="delegation"/>, in order.</summary>
    private DelegationRunEventPump? delegationRunEvents;
    private Task delegationWatch = Task.CompletedTask;

    /// <summary>
    /// Delegation's records, tools, delivery and git actions. Made at launch from the profile's delegation file whether or not
    /// the hidden switch is on: switching it off only detaches the delegation server, so existing children keep working. null
    /// when that file could not be read, and then delegation is unavailable this launch.
    /// </summary>
    public DelegationCoordinator? Delegation => delegation;
    /// <summary>
    /// Something delegation shows changed: the sidebar's tree, a pane's held rows, a child's card, a child's close, or a child's
    /// pane was made. Always raised later, on a thread-pool thread, never on the caller's; what a handler throws goes no further.
    /// </summary>
    public event Action? DelegationChanged;
    /// <summary>
    /// Set by the window: ends the pane's agent terminal and everything it started, and returns once they have exited. A child's
    /// discard and its close's cleanup wait for it before they look for links in its worktree. Called on thread-pool threads.
    /// </summary>
    public Func<string, Task>? EndAgentTerminal { get; set; }
    /// <summary>
    /// Set by the window: why it would refuse a run in the pane now (a CLI update, plugins changing, a sign-in), or null when
    /// one may start (M/AppStore.swift:1037-1045). Delegation then starts no run there, and what comes for a pane idle after a
    /// normal finish is held as a row of its queued list. Called on thread-pool threads.
    /// </summary>
    public Func<RunSession, string?>? DelegatedRunBlocked { get; set; }
    /// <summary>
    /// Set by the window: finishes the request of a run delegation starts on its own (a child's first run, a notice or
    /// follow-up that starts a pane's next run, run next) the way the window finishes its own sends, with the provider's
    /// registered models and a style's launch mode. Without it the request is the pane as saved.
    /// </summary>
    public Func<StartRunRequest, Task<StartRunRequest>>? DelegatedRunRequest { get; set; }

    private void StartDelegation()
    {
        if (delegation is not null) return;
        DelegationCoordinator coordinator;
        // An unreadable delegation file, or anything else that goes wrong here, leaves delegation unavailable this launch and
        // the app starts without it (M/AppStore+Delegation.swift:77-79).
        try { DelegationGit.UseProfile(store.DirectoryPath); coordinator = new(new DelegationFileStore(store.DirectoryPath), this, worktrees, delegationEnabled); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return; }
        delegation = coordinator; delegationRunEvents = new(coordinator); local.DelegationHandler = coordinator;
        // The sidebar's tree and the held rows of each pane's queued list follow the file, switch on or off.
        delegationWatch = Task.WhenAll(Watch(coordinator.ChildRows), Watch(coordinator.HeldRows));
    }

    private async Task Watch<T>(ChannelReader<T> rows)
    {
        try { await foreach (var _ in rows.ReadAllAsync(delegationWatchStop.Token)) RaiseDelegationChanged(); }
        catch (OperationCanceledException) { }
    }

    /// <summary>Raises <see cref="DelegationChanged"/> on the thread pool, each handler on its own: one that throws stops neither the others nor the caller.</summary>
    private void RaiseDelegationChanged()
    {
        if (DelegationChanged is not { } changed) return;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            foreach (var handler in changed.GetInvocationList().Cast<Action>())
                try { handler(); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
        });
    }

    /// <summary>
    /// For the delegation smoke only: drops the coordinator and makes it again from the delegation file as it is on disk now,
    /// as the Mac's smoke does (M/AppStore+DelegationSmoke.swift:69-76). No run of a child may be going.
    /// </summary>
    public async Task RestartDelegationAsync()
    {
        if (delegationRunEvents is { } events) await events.Finish();
        await delegationWatchStop.CancelAsync(); await delegationWatch;
        delegation = null; delegationRunEvents = null; local.DelegationHandler = null; delegationWatchStop = new();
        StartDelegation(); RaiseDelegationChanged();
    }

    /// <summary>Quitting: once every run has ended, children whose runs it ended are recorded interrupted, with no notice (M/AppStore.swift:1304-1305).</summary>
    private async Task StopDelegation()
    {
        if (delegationRunEvents is { } events) await events.Finish();
        Task[] closes; lock (sync) closes = [.. childCloses.Values];
        await Task.WhenAll(closes);
        await delegationWatchStop.CancelAsync(); await delegationWatch;
    }

    /// <summary>A new run starts in the pane, under <see cref="sync"/>: its id. A child's start sets it running and clears reported.</summary>
    private string DelegationRunBegan(RunSession pane)
    {
        var runId = delegationRuns.Begin(pane.Id);
        if (pane.ParentSessionId is not null) { childRuns.Add(pane.Id); delegationRunEvents?.Send(new DelegationRunEvent.Started(pane.Id, runId)); }
        return runId;
    }

    /// <summary>
    /// A run of the pane ended with <paramref name="status"/>, under <see cref="sync"/> (M/AppStore+Delegation.swift:82-92). A
    /// child's end reaches the coordinator after its start; while the app quits it counts as killed by quitting. False when no
    /// run of this launch was going there.
    /// </summary>
    private bool DelegationRunEnded(string sessionId, string status)
    {
        if (delegationRuns.End(sessionId, status, closing) is not { } ended) return false;
        if (childRuns.Remove(sessionId)) delegationRunEvents?.Send(new DelegationRunEvent.Ended(sessionId, ended.RunId, ended.End));
        return true;
    }

    /// <summary>Has delegation offer its pending notices and follow-ups to their panes as they are now: a running Claude run takes them by steer.</summary>
    private void OfferDelegationPending() { if (!closing && delegation is { } coordinator) _ = coordinator.DeliverPending(); }

    // The coordinator's host (M/AppStore+Delegation.swift:318-386).

    /// <summary>
    /// The child's Claude pane, as a tab beside its parent: in the mode the parent asked for, never the last-used pane's, linked
    /// to its parent and working in its worktree folder. The link is saved before its first run.
    /// </summary>
    async Task<bool> IDelegationHost.CreatePane(DelegationChildPane pane)
    {
        var task = delegation?.File.Copy(pane.SessionId, DelegationCopyKind.Task)?.Text; Task save;
        lock (sync)
        {
            if (closing || DelegationPanes.AddingChild(snapshot, pane, task) is not { } next) return false;
            snapshot = StateStore.Normalize(next, false); save = QueueSave();
        }
        // A save the disk refuses is reported as any other; the pane is open all the same.
        try { await save; } catch (Exception ex) when (ex is not OutOfMemoryException) { }
        RaiseDelegationChanged();
        lock (sync) return snapshot.Sessions.Any(s => s.Id == pane.SessionId);
    }

    Task<string?> IDelegationHost.StartRun(string sessionId, string input) => StartDelegatedRun(sessionId, input);

    /// <summary>
    /// A run with <paramref name="input"/> in the pane, the way a send starts one: its id once the run's process has it, or
    /// null when the pane cannot run now (a child without its worktree among them) or its start failed or was stopped, so what
    /// it carried is not lost. Its words are delegation's, so they never title the pane.
    /// </summary>
    private async Task<string?> StartDelegatedRun(string sessionId, string input)
    {
        RunSession? pane; lock (sync) pane = closing ? null : snapshot.Sessions.FirstOrDefault(s => s.Id == sessionId);
        if (pane is null || DelegatedRunBlocked?.Invoke(pane) is not null) return null;
        try
        {
            var request = new StartRunRequest(pane.Id, pane.WorkspaceId, pane.Kind, input, [], pane.Model, pane.Provider, pane.Settings, pane.ResumeId);
            if (DelegatedRunRequest is { } finish) request = await finish(request);
            return await Launch(request, "", true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
    }

    /// <summary><paramref name="input"/> steered into the pane's running Claude run, shown in its transcript like a steered send, or started as its next run.</summary>
    async Task<string?> IDelegationHost.Deliver(string input, string sessionId, DeliveryRoute route)
    {
        if (route == DeliveryRoute.Queue) return await StartDelegatedRun(sessionId, input);
        bool steerable;
        lock (sync) steerable = !closing && snapshot.Sessions.Any(s => s.Id == sessionId && s.Status == "running" && DelegationSwitch.IsClaudePane(s.Kind, s.Provider)) && delegationRuns.RunId(sessionId) is not null;
        if (!steerable || !await TrySteerAsync(sessionId, input)) return null;
        // The receipt names the run that took it, read once it has.
        lock (sync) return delegationRuns.RunId(sessionId);
    }

    /// <summary>
    /// The pane as delegation reads it. One idle after a normal finish whose start would be refused now reads as idle, so what
    /// comes for it is held as a row of its queued list instead of waiting unseen for a run it cannot take. Every notice and
    /// follow-up is a valid run input, so the start is asked about with a stand-in for its words.
    /// </summary>
    Task<DelegationPaneState?> IDelegationHost.PaneState(string sessionId)
    {
        RunSession? pane; Workspace? workspace; DelegationPaneActivity activity; string? runId; bool quitting;
        lock (sync)
        {
            pane = snapshot.Sessions.FirstOrDefault(s => s.Id == sessionId); workspace = pane is null ? null : snapshot.Workspaces.FirstOrDefault(w => w.Id == pane.WorkspaceId);
            activity = delegationRuns.Activity(sessionId, pane?.Status == "running" || routes.ContainsKey(sessionId)); runId = delegationRuns.RunId(sessionId); quitting = closing;
        }
        if (pane is null || workspace is null) return Task.FromResult<DelegationPaneState?>(null);
        var refused = activity == DelegationPaneActivity.Finished && (quitting || DelegationPanes.RunRefusal(pane) is not null || DelegatedRunBlocked?.Invoke(pane) is not null || !Runs(pane));
        return Task.FromResult<DelegationPaneState?>(DelegationPanes.PaneState(pane, workspace, runId, activity, refused));
    }

    private static bool Runs(RunSession pane)
    {
        try { new StartRunRequest(pane.Id, pane.WorkspaceId, pane.Kind, "notice", [], pane.Model, pane.Provider, pane.Settings, pane.ResumeId).Validate(); return true; }
        catch (ArgumentException) { return false; }
    }

    /// <summary>A discard's stop: the child's run and then its agent terminal, each ended before the discard looks at its worktree.</summary>
    async Task IDelegationHost.StopRun(string sessionId) { await StopAndWait(sessionId); await EndTerminal(sessionId); }

    /// <summary>
    /// Stops the pane's run, if one is going, and returns once it has ended: its CLI and everything the CLI started have exited.
    /// False when it had not ended after ten seconds.
    /// </summary>
    private async Task<bool> StopAndWait(string sessionId)
    {
        Route? route; lock (sync) route = routes.GetValueOrDefault(sessionId);
        if (route is null) return true;
        try { await StopAsync(sessionId); await route.Ended.Task.WaitAsync(TimeSpan.FromSeconds(10)); return true; }
        catch (TimeoutException) { return false; }
    }

    /// <summary>The pane's agent terminal ended by the window (<see cref="EndAgentTerminal"/>); a window that cannot end it changes nothing here.</summary>
    private async Task EndTerminal(string sessionId)
    {
        if (EndAgentTerminal is not { } end) return;
        try { await end(sessionId); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
    }

    // What the window calls and draws.

    /// <summary>The delegated children as the sidebar lists them: every child the file holds except discarded ones, in the order they were made.</summary>
    public IReadOnlyList<DelegationChildRow> DelegationChildren => delegation is { } coordinator ? DelegationSidebar.Rows(coordinator.File) : [];

    /// <summary>The sidebar's tree of one workspace of <paramref name="state"/>: each child under its parent pane, and a closed parent's children under a 'parent closed' node.</summary>
    public DelegationTree SidebarTree(AppSnapshot state, string workspaceId) => DelegationSidebar.Tree(workspaceId, state.Workspaces, state.Sessions, DelegationChildren);

    /// <summary>
    /// The items held for the pane, oldest first: rows of its queued list ahead of the human's, which run with the next send or
    /// run next and cannot be removed (<see cref="DelegationQueueRows"/>).
    /// </summary>
    public IReadOnlyList<DelegationDeliveryItem> HeldItems(string sessionId) => delegation?.HeldItems(sessionId) ?? [];

    /// <summary>The pane's run going now, or its last since this launch, as delegation's receipts name it.</summary>
    public string? RunId(string sessionId) => delegationRuns.RunId(sessionId);

    /// <summary>
    /// The human's send of <paramref name="request"/> in an idle pane, from the composer, the phone or the queue
    /// (M/AppStore+Delegation.swift:116-143). Items held for the pane go with it: all of them, oldest first and ahead of its
    /// text, with its attachments, in one run. What it did:
    /// <see cref="DelegationRelease.Released"/>: the run started with the held items;
    /// <see cref="DelegationRelease.NothingHeld"/>: nothing was held, and the request started as <see cref="StartAsync"/> starts it;
    /// <see cref="DelegationRelease.NotStarted"/>: no run started.
    /// A start that is refused or fails throws what <see cref="StartAsync"/> throws. When no run took the send nothing is
    /// taken: the held items stay held, and the caller keeps the draft in the composer or the request at the head of the queue.
    /// </summary>
    public async Task<DelegationRelease> SendAsync(StartRunRequest request)
    {
        // Nothing held for the pane: the send starts as StartAsync starts it, its route taken before anything is awaited, as the
        // Mac's send looks at the pane's held rows first (M/AppStore.swift:777).
        if (delegation is not { } coordinator || coordinator.HeldItems(request.SessionId).Count == 0) { await StartAsync(request); return new DelegationRelease.NothingHeld(); }
        Exception? failure = null;
        var release = await coordinator.Send(request.Input, request.SessionId, async whole =>
        {
            // The pane's title follows the human's words, not the items ahead of them.
            try { return await Launch(request with { Input = whole }, request.Input, true); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { failure = ex; return null; }
        });
        if (failure is not null) ExceptionDispatchInfo.Throw(failure);
        if (release is DelegationRelease.NothingHeld) await StartAsync(request);
        return release;
    }

    /// <summary>
    /// Run next (M/AppStore.swift:865-885). Items held for the pane go first, all of them and nothing else in one run, steered
    /// into its running Claude run when it has one; the queued rows follow in later runs.
    /// <see cref="DelegationRelease.NothingHeld"/>: the caller runs its first queued row as before;
    /// <see cref="DelegationRelease.NotStarted"/>: no run took them, and they stay held.
    /// </summary>
    public Task<DelegationRelease> RunNextAsync(string sessionId) => delegation?.RunNext(sessionId) ?? Task.FromResult<DelegationRelease>(new DelegationRelease.NothingHeld());

    /// <summary>
    /// A human's close of the pane asks first when it is a parent pane with open children (M/AppStore+Delegation.swift:204-213):
    /// what its confirmation shows, or null when the pane closes without asking. Confirmed, only the parent closes. The phone's
    /// close never asks.
    /// </summary>
    public PendingParentClose? ParentCloseQuestion(string id)
    {
        AppSnapshot state; lock (sync) state = snapshot;
        var open = DelegationSidebar.OpenChildCount(id, DelegationChildren, state.Sessions);
        return open > 0 && state.Sessions.FirstOrDefault(s => s.Id == id) is { } pane ? new(id, pane.Title, open) : null;
    }

    /// <summary>
    /// Closes the pane: its run is stopped and has ended before the pane goes. What closing a delegated child's pane did to its
    /// worktree and branch (<see cref="DelegationCoordinator.CloseChild"/>); null for any other pane. <paramref name="closed"/>
    /// is called once the pane has left the snapshot, before a child's close goes on, on the caller's context.
    /// </summary>
    public async Task<ChildCleanupOutcome?> CloseSessionAsync(string id, Action? closed = null)
    {
        await StopAndWait(id);
        await UpdateAsync(s => s with { Sessions = s.Sessions.Where(p => p.Id != id).ToList() });
        closed?.Invoke();
        return ChildClose(id) is { } close ? await close : null;
    }

    /// <summary>The close of a delegated child's pane, going or over; null when no child pane with this id was closed.</summary>
    public Task<ChildCleanupOutcome>? ChildClose(string id) { lock (sync) return childCloses.GetValueOrDefault(id); }

    /// <summary>
    /// The pane left the snapshot, however it was closed: by the window, from the phone or with its workspace
    /// (M/AppStore.swift:592-625). A delegated child's close reaches the coordinator once its run has ended, and a merged
    /// child's worktree and branch are then cleaned up when every rule holds. A parent's close closes only the parent.
    /// </summary>
    private void PaneClosed(RunSession pane)
    {
        if (delegation is not { } coordinator || pane.ParentSessionId is null && !coordinator.File.Children.Any(child => child.Id == pane.Id)) { delegationRuns.Forget(pane.Id); return; }
        var close = Task.Run<ChildCleanupOutcome>(async () =>
        {
            // A run that did not end keeps the child busy: its worktree is never cleaned up under a live CLI.
            var ended = await StopAndWait(pane.Id); await EndTerminal(pane.Id); delegationRuns.Forget(pane.Id);
            try { return await coordinator.CloseChild(pane.Id, !ended); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { return new ChildCleanupOutcome.Failed(ex.Message); }
            finally { RaiseDelegationChanged(); }
        });
        lock (sync) childCloses[pane.Id] = close;
    }

    /// <summary>
    /// <c>workspace_has_children</c> while delegation has open children or child worktrees not cleaned up in the workspace; null
    /// when it may be removed (M/AppStore+Delegation.swift:221-227). <see cref="RemoveWorkspaceAsync"/> refuses with it.
    /// </summary>
    public DelegationReasonCode? WorkspaceRemovalRefusal(string workspaceId)
    {
        if (delegation is not { } coordinator) return null;
        AppSnapshot state; lock (sync) state = snapshot;
        return state.Workspaces.FirstOrDefault(w => w.Id == workspaceId) is { } workspace
            ? coordinator.WorkspaceRemovalRefusal(state.Sessions.Where(s => s.WorkspaceId == workspaceId).Select(s => s.Id).ToHashSet(), workspace.Path) : null;
    }

    /// <summary>The last answer the child's card got, until its next action.</summary>
    public DelegationCardNote? CardNote(string id) { lock (sync) return cardNotes.GetValueOrDefault(id); }

    /// <summary>Whether an action of the child's card is going: its buttons wait.</summary>
    public bool IsCardBusy(string id) { lock (sync) return cardBusy.Contains(id); }

    /// <summary>The human's merge from the child's card. Its answer, kept for the card; null when an action of this card is going.</summary>
    public Task<DelegationCardNote?> MergeChildAsync(string id) => CardAction(id, DelegationCardAction.Merge, async coordinator => await coordinator.MergeFromCard(id) switch
    {
        ChildMergeOutcome.Merged => new DelegationCardResult.Done(),
        ChildMergeOutcome.MergedUnsaved unsaved => new DelegationCardResult.DoneUnsaved(unsaved.Reason),
        ChildMergeOutcome.Refused refused => new DelegationCardResult.Refused(refused.Reason),
        ChildMergeOutcome.Failed failed => new DelegationCardResult.Failed(failed.Message),
        _ => throw new InvalidOperationException(),
    });

    /// <summary>The human's undo of the child's last merge from its card.</summary>
    public Task<DelegationCardNote?> UndoChildMergeAsync(string id) => CardAction(id, DelegationCardAction.Undo, async coordinator => await coordinator.UndoMergeFromCard(id) switch
    {
        ChildUndoOutcome.Undone => new DelegationCardResult.Done(),
        ChildUndoOutcome.UndoneUnsaved unsaved => new DelegationCardResult.DoneUnsaved(unsaved.Reason),
        ChildUndoOutcome.Refused refused => new DelegationCardResult.Refused(refused.Reason),
        ChildUndoOutcome.Failed failed => new DelegationCardResult.Failed(failed.Message),
        _ => throw new InvalidOperationException(),
    });

    /// <summary>
    /// The human's discard from the child's card, first step: what its confirmation shows, which names the worktrees nested in
    /// the child's that go with it, or says it cannot be discarded now when they cannot be listed. null when the child is not
    /// listed or an action of its card is going.
    /// </summary>
    public async Task<PendingChildDiscard?> AskToDiscardChildAsync(string id)
    {
        if (delegation is not { } coordinator || DelegationChildren.FirstOrDefault(child => child.Id == id) is not { } row) return null;
        string? title;
        lock (sync) { if (!cardBusy.Add(id)) return null; cardNotes.Remove(id); title = snapshot.Sessions.FirstOrDefault(s => s.Id == id)?.Title; }
        RaiseDelegationChanged();
        try { return new(id, title ?? row.Task ?? Locale.Get("delegation.child.untitled"), ChildRecord.BranchName(id), await coordinator.NestedWorktrees(id)); }
        finally { lock (sync) cardBusy.Remove(id); RaiseDelegationChanged(); }
    }

    /// <summary>
    /// The discard the human confirmed, with the nested worktrees its confirmation named. When more are nested now, nothing is
    /// removed and the answer is <see cref="DelegationCardResult.Reconfirm"/> naming them all, to ask again. The child's pane,
    /// its worktree gone, closes once the child is discarded.
    /// </summary>
    public async Task<DelegationCardNote?> DiscardChildAsync(PendingChildDiscard confirmed)
    {
        var note = await CardAction(confirmed.Id, DelegationCardAction.Discard, async coordinator => await coordinator.DiscardFromCard(confirmed.Id, confirmed.Nested) switch
        {
            ChildCardDiscardOutcome.Discarded => new DelegationCardResult.Done(),
            ChildCardDiscardOutcome.NestedChanged changed => new DelegationCardResult.Reconfirm(changed.Nested),
            ChildCardDiscardOutcome.Failed failed => new DelegationCardResult.Failed(failed.Message),
            _ => throw new InvalidOperationException(),
        });
        if (note?.Result is not DelegationCardResult.Done) return note;
        lock (sync) cardNotes.Remove(confirmed.Id);
        await CloseSessionAsync(confirmed.Id); RaiseDelegationChanged();
        return note;
    }

    /// <summary>Runs one card action of the child, one at a time per child, and keeps its answer for the card.</summary>
    private async Task<DelegationCardNote?> CardAction(string id, DelegationCardAction action, Func<DelegationCoordinator, Task<DelegationCardResult>> work)
    {
        if (delegation is not { } coordinator) return null;
        lock (sync) { if (!cardBusy.Add(id)) return null; cardNotes.Remove(id); }
        RaiseDelegationChanged();
        DelegationCardNote note;
        try { note = new(action, await work(coordinator)); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { note = new(action, new DelegationCardResult.Failed(ex.Message)); }
        lock (sync) { cardBusy.Remove(id); cardNotes[id] = note; }
        RaiseDelegationChanged();
        return note;
    }
}
