using System.Collections.Immutable;
using System.Text;

namespace MightyClaude.Core;

/// <summary>
/// The delegate tool (M/DelegationDelegateTool.swift): a parent Claude pane starts a child Claude pane on a task, in the child's
/// own worktree, one layer deep.
/// </summary>
public sealed partial class DelegationCoordinator
{
    /// <summary>The open children one parent may have at once.</summary>
    public const int MaximumOpenChildren = 3;
    /// <summary>How deep delegation goes: a parent's children cannot delegate.</summary>
    public const int MaximumDepth = 1;
    /// <summary>The starting modes a parent may ask for, narrowest first.</summary>
    public static readonly ImmutableArray<string> StartingModes = ["plan", "manual", "acceptEdits", "auto", "fullAccess"];

    /// <summary>
    /// Whether <paramref name="mode"/> lets Claude act no more freely than <paramref name="stored"/> does, by rank
    /// (plan &lt; manual &lt; acceptEdits &lt; auto &lt; fullAccess). An unknown mode on either side is never no wider.
    /// </summary>
    public static bool IsNoWider(string mode, string stored)
    {
        var asked = StartingModes.IndexOf(mode); var own = StartingModes.IndexOf(stored);
        return asked >= 0 && own >= 0 && asked <= own;
    }

    /// <summary>
    /// delegate(task, mode) from <paramref name="caller"/>. The first rule that applies answers:
    /// 1. A mode that is not a starting mode, or an empty task, one with a NUL character or one over 64 KiB, is answered with an
    ///    error. The child's first run input holds the whole task, and no run input may hold a NUL; 64 KiB is its TASK.md copy's
    ///    cap, which keeps that input well under a run input's 100,000 characters.
    /// 2. The same call earlier in the caller's current run (the same request key) gets that same child, in whatever state it is now.
    /// 3. <c>child_cannot_delegate</c> (the caller has a parent link), <c>width_cap</c> (it has three open children),
    ///    <c>wider_mode</c> (the mode is wider than the caller's stored mode now).
    /// 4. <c>not_git</c>, <c>detached_head</c>, <c>unborn_branch</c>, <c>low_disk</c>: the caller's folder cannot host a child worktree.
    /// 5. <c>store_full</c>: even after pruning, the delegation file has no room for the child's record and its report copy.
    /// Every refusal comes before anything is made and stores no key. Otherwise the child's record is saved in creating, with
    /// the call's request key and a copy of its TASK.md, and the answer names it while its start goes on in the background.
    /// </summary>
    private async Task<DelegationResponse> DelegateNow(string task, string mode, DelegationCaller caller)
    {
        if (!StartingModes.Contains(mode)) return DelegationResponse.Failure($"mode must be one of {string.Join(", ", StartingModes)}.");
        if (string.IsNullOrWhiteSpace(task)) return DelegationResponse.Failure("delegate needs a task: what the child should do.");
        if (task.Contains('\0')) return DelegationResponse.Failure("A task may not contain a NUL character.");
        if (Encoding.UTF8.GetByteCount(task) > DelegationFileStore.MaximumCopyBytes) return DelegationResponse.Failure("A task may be at most 64 KiB.");
        var parentId = caller.PaneId;
        if (await Host(() => host.PaneState(parentId)) is not { } parent) return DelegationResponse.Failure("The calling pane is closed.");
        var key = DelegationRequestKey.Make(parentId, parent.RunId ?? "", task, mode);
        if (SameCall(key, parentId) is { } same) return DelegationResponse.Delegated(same);
        if (DelegateRefusal(parentId, parent, mode) is { } reason) return DelegationResponse.Refusal(reason);
        ChildWorktreeBase start;
        switch (await worktrees.Check(parent.Folder))
        {
            case ChildWorktreeCheck.Ready ready: start = ready.Base; break;
            case ChildWorktreeCheck.Refused refused: return DelegationResponse.Refusal(refused.Reason);
            default: throw new InvalidOperationException();
        }
        var context = await PruneContext();
        // Nothing below suspends until the record is saved, so two calls at once cannot both pass these checks.
        if (SameCall(key, parentId) is { } repeated) return DelegationResponse.Delegated(repeated);
        if (DelegateRefusal(parentId, parent, mode) is { } late) return DelegationResponse.Refusal(late);
        var id = Wire.Id();
        var child = ChildRecord.Make(id, parentId, worktrees.WorktreePath(id), start.ParentBranch, start.BaseCommit, mode, key, parentCheckout: start.Repository);
        try
        {
            if (File.Admitting(child, context, DelegationCopy.Of(id, DelegationCopyKind.Task, 0, Encoding.UTF8.GetBytes(task))) is not DelegationAdmission.Admitted admitted) return DelegationResponse.Refusal(DelegationReasonCode.StoreFull);
            Commit(admitted.File, context);
        }
        catch (DelegationFileException ex) when (ex.Error == DelegationFileError.Full) { return DelegationResponse.Refusal(DelegationReasonCode.StoreFull); }
        catch (Exception ex) when (DelegationFileStore.IsSaveFailure(ex)) { return DelegationResponse.Failure("The delegation file could not be saved, so no child was made."); }
        starting.Add(id);
        _ = turn.StartNew(() => StartChild(id, start, task));
        return DelegationResponse.Delegated(child);
    }

    /// <summary>
    /// The child's start, after delegate has answered: its worktree, then its pane in the starting mode, then its first run. The
    /// child moves to running once that run has started, as the run its parent awaits, and otherwise to failed with one
    /// failed_to_start notice. Whatever a failed start made stays until a human discards the child; the app never removes it on its own.
    /// </summary>
    private async Task StartChild(string id, ChildWorktreeBase start, string task)
    {
        try
        {
            if (File.Children.Find(record => record.Id == id) is not { State: ChildState.Creating } child) return;
            string? runId = null;
            if (await worktrees.Make(id, start, task) is ChildWorktreeOutcome.Created { Worktree: var worktree })
            {
                var pane = new DelegationChildPane(id, child.ParentSessionId, child.StartingMode, worktree.WorkingFolder);
                // A host that fails here has started nothing: the child is failed, never left creating.
                try { if (await Host(() => host.CreatePane(pane))) runId = await Host(() => host.StartRun(id, FirstInput(task, worktree))); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { runId = null; }
            }
            var context = await PruneContext();
            var next = File; var index = next.Children.FindIndex(record => record.Id == id);
            if (index < 0) return;
            if (runId is not null)
            {
                // Once the host has reported this run's start, or even its end, the record is past creating.
                if (next.Children[index].State != ChildState.Creating || next.Children[index].NotingRun(runId) is not { } running) return;
                next = next.SettingChild(index, running);
            }
            else
            {
                if (next.Children[index].Applying(ChildStateEvent.FailStart) is not { } failed) return;
                next = next.SettingChild(index, failed) with { Notices = next.Notices.Add(new(Wire.Id(), id, 0, NoticeKind.FailedToStart, DeliveryLane.Pending)) };
            }
            // The admitted record kept room for this child's report copy, far more than this needs. Should the disk still refuse,
            // the app goes on with the move and the next save writes it.
            SaveOrKeep(next, context);
            // A failed start's notice goes to the parent's pane while the child still counts as starting.
            if (runId is null) await DeliverPending();
        }
        finally { starting.Remove(id); }
    }

    /// <summary>Whether the child's start is still going: delegate answered, and its worktree, pane and first run are not all settled yet. A discard waits for it.</summary>
    public Task<bool> IsStarting(string id) => Isolated(() => Task.FromResult(starting.Contains(id)));

    /// <summary>The first message of a child's first run: its task and how it reports.</summary>
    public static string FirstInput(string task, ChildWorktree worktree) =>
        $"Another Claude pane in Mighty Claude delegated this task to you. You work in your own git worktree on the branch {worktree.Branch}; the task is also saved in {worktree.TaskFile}.\n\n"
        + $"When you are done, commit your work on this branch, leaving no uncommitted changes to tracked files, and then write your report to {worktree.ReportFile}: what you did, what is left and what your parent should check. Your parent gets one notice for each of your runs that ends with that file changed, and may merge your branch. Do not merge, push or switch branches.\n\n"
        + $"Task:\n{task}";

    /// <summary>The parent's child made by the same call: the same request key.</summary>
    private ChildRecord? SameCall(string key, string parentId) => File.Children.Find(child => child.ParentSessionId == parentId && child.RequestKey == key);

    /// <summary>Why the pane <paramref name="parentId"/>, now <paramref name="parent"/>, may not delegate in <paramref name="mode"/>, or null when it may.</summary>
    private DelegationReasonCode? DelegateRefusal(string parentId, DelegationPaneState parent, string mode)
    {
        // A pane with a parent link is already one layer down.
        var depth = parent.ParentSessionId is not null || File.Children.Any(child => child.Id == parentId) ? 1 : 0;
        if (depth >= MaximumDepth) return DelegationReasonCode.ChildCannotDelegate;
        if (File.Children.Count(child => child.ParentSessionId == parentId && child.State.IsOpen()) >= MaximumOpenChildren) return DelegationReasonCode.WidthCap;
        return IsNoWider(mode, parent.PermissionMode) ? null : DelegationReasonCode.WiderMode;
    }

    /// <summary>
    /// What pruning may assume now: which of the file's parents still have an open pane, and that any merge of a child not
    /// discarded may still be undone, so no save here ever drops one.
    /// </summary>
    private async Task<DelegationPruneContext> PruneContext()
    {
        var open = new HashSet<string>();
        foreach (var parent in File.Children.Select(child => child.ParentSessionId).Distinct().ToList())
            if (await Host(() => host.PaneState(parent)) is not null) open.Add(parent);
        var discarded = File.Children.Where(child => child.State == ChildState.Discarded).Select(child => child.Id).ToHashSet();
        return new(open, merge => !discarded.Contains(merge.ChildId));
    }

    /// <summary>Saves <paramref name="next"/>, pruning first when it nears the cap, and holds what was written. Throws, holding the file as it was, when it cannot be saved.</summary>
    private void Commit(DelegationFile next, DelegationPruneContext context) => File = Store.Save(next, context);

    /// <summary>Saves <paramref name="next"/>; should the disk refuse it, the app goes on with it and the next save writes it.</summary>
    private void SaveOrKeep(DelegationFile next, DelegationPruneContext context)
    {
        try { Commit(next, context); } catch (Exception ex) when (DelegationFileStore.IsSaveFailure(ex)) { File = next; }
    }

    /// <summary>
    /// Saves the file as the coordinator holds it now: what a save the disk refused earlier left unwritten is written. Throws
    /// when it still cannot be saved.
    /// </summary>
    public Task Save() => Isolated(async () =>
    {
        var context = await PruneContext();
        Commit(File, context);
    });
}
