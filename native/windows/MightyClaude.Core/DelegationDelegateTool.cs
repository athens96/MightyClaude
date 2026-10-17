using System.Collections.Immutable;
using System.Text;

namespace MightyClaude.Core;

/// <summary>The answer to a human's run of a text in a new child worktree (M/DelegationDelegateTool.swift:3-13).</summary>
public abstract record HumanChildStart
{
    /// <summary>The child's record is saved in creating; its worktree, pane and first run follow in the background.</summary>
    public sealed record Started(ChildRecord Child) : HumanChildStart;
    /// <summary>Refused with exactly this reason before anything was made.</summary>
    public sealed record Refused(DelegationReasonCode Reason) : HumanChildStart;
    /// <summary>Nothing was made, for this reason in the app's language.</summary>
    public sealed record Failed(string Message) : HumanChildStart;
}

/// <summary>The first run of a child the human started: its whole input, and the app's note as the request that follows it when the text is a slash command.</summary>
public sealed record HumanChildFirstRun(string Input, string? FollowingNote);

/// <summary>How the app starts a child the human made from its parent's composer (M/DelegationDelegateTool.swift:24-35).</summary>
/// <param name="Start">Starts the child's first run in its pane: its run id, or null when none started.</param>
/// <param name="Failed">The child's worktree, its pane or its first run could not be made after the send was answered: the child is failed, and the human's text needs another way.</param>
public sealed record HumanChildStarter(Func<string, HumanChildFirstRun, Task<string?>> Start, Func<string, Task> Failed);

/// <summary>
/// The delegate tool (M/DelegationDelegateTool.swift): a parent Claude or Codex pane starts a child pane of its own agent on a
/// task, in the child's own worktree, one layer deep; and the human's second send starts one from any agent pane.
/// </summary>
public sealed partial class DelegationCoordinator
{
    /// <summary>The open children one parent may have at once.</summary>
    public const int MaximumOpenChildren = 3;
    /// <summary>How deep delegation goes: a parent's children cannot delegate.</summary>
    public const int MaximumDepth = 1;
    /// <summary>The starting modes a Claude parent may ask for, narrowest first; a parent of any agent asks for one of <see cref="DelegationProviders.StartingModes"/>.</summary>
    public static readonly ImmutableArray<string> StartingModes = [.. DelegationProviders.StartingModes("claude")];

    /// <summary>
    /// Whether <paramref name="mode"/> lets a <paramref name="provider"/> agent act no more freely than <paramref name="stored"/>
    /// does, by rank (Claude: plan &lt; manual &lt; acceptEdits &lt; auto &lt; fullAccess; Codex: manual &lt; acceptEdits &lt;
    /// onRequest &lt; fullAccess). A mode the provider lacks, on either side, is never no wider.
    /// </summary>
    public static bool IsNoWider(string mode, string stored, string provider = "claude") => DelegationProviders.IsNoWider(mode, stored, provider);

    /// <summary>Why the <paramref name="provider"/> agent cannot run a child, naming it, or null when it can.</summary>
    public static string? Unavailable(string provider, DelegationAgentAvailability availability) => availability switch
    {
        DelegationAgentAvailability.NotInstalled => $"{ProviderMark.Label(provider)} is not installed on this PC; the user can check it in Mighty Claude's Settings › CLI",
        DelegationAgentAvailability.SignedOut => $"{ProviderMark.Label(provider)} is not signed in on this PC; the user can check it in Mighty Claude's Settings › CLI",
        _ => null,
    };

    /// <summary>
    /// delegate(task, mode) from <paramref name="caller"/>. The first rule that applies answers:
    /// 1. A mode that is not one of the caller's agent's, a Codex mode in which the child could not change files (manual), or an
    ///    empty task, one with a NUL character or one over 64 KiB, is answered with an error. The child's first run input holds the whole task, and no run input may hold a NUL; 64 KiB is its TASK.md copy's
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
        var modes = DelegationProviders.StartingModes(caller.Provider);
        if (!modes.Contains(mode)) return DelegationResponse.Failure($"mode must be one of {string.Join(", ", modes)}.");
        if (!DelegationProviders.ChildCanWrite(caller.Provider, mode))
            return DelegationResponse.Failure($"A {ProviderMark.Label(caller.Provider)} child in {mode} mode cannot change files, so it could not do the task or leave its work for a merge. Ask for one of {string.Join(", ", modes.Where(writing => DelegationProviders.ChildCanWrite(caller.Provider, writing)))}, no wider than your own mode.");
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
        var child = ChildRecord.Make(id, parentId, worktrees.WorktreePath(id), start.ParentBranch, start.BaseCommit, mode, key, parentCheckout: start.Repository, provider: parent.Provider);
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
    /// The child's start, after delegate (or the human's send) has answered: its worktree, then its pane of the parent's agent in
    /// the starting mode, then its first run, which <paramref name="human"/> starts when the human's send made the child (with the
    /// send's attachments) and the host otherwise. The child moves to running once that run has started, as the run its parent
    /// awaits, and otherwise to failed, with one failed_to_start notice when the parent's delegate made it, or
    /// <paramref name="human"/>'s failure call when the human's send did. Whatever a failed start made stays until a human
    /// discards the child; the app never removes it on its own.
    /// </summary>
    private async Task StartChild(string id, ChildWorktreeBase start, string task, HumanChildStarter? human = null)
    {
        string? runId = null;
        try
        {
            if (File.Children.Find(record => record.Id == id) is not { State: ChildState.Creating } child) return;
            if (await worktrees.Make(id, start, task) is ChildWorktreeOutcome.Created { Worktree: var worktree })
            {
                var pane = new DelegationChildPane(id, child.ParentSessionId, child.StartingMode, worktree.WorkingFolder, !child.NotifiesParent, child.Agent);
                // A host that fails here has started nothing: the child is failed, never left creating.
                try
                {
                    if (await Host(() => host.CreatePane(pane)))
                        runId = human is not null ? await Host(() => human.Start(id, HumanFirstRun(task, worktree, child.Agent))) : await Host(() => host.StartRun(id, FirstInput(task, worktree, child.Agent)));
                }
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
                next = next.SettingChild(index, failed);
                if (failed.NotifiesParent) next = next with { Notices = next.Notices.Add(new(Wire.Id(), id, 0, NoticeKind.FailedToStart, DeliveryLane.Pending)) };
            }
            // The admitted record kept room for this child's report copy, far more than this needs. Should the disk still refuse,
            // the app goes on with the move and the next save writes it.
            SaveOrKeep(next, context);
            // A failed start's notice goes to the parent's pane while the child still counts as starting.
            if (runId is null) await DeliverPending();
            if (runId is null && human is not null) await Host(() => human.Failed(id));
        }
        finally { starting.Remove(id); }
    }

    /// <summary>Whether the child's start is still going: delegate answered, and its worktree, pane and first run are not all settled yet. A discard waits for it.</summary>
    public Task<bool> IsStarting(string id) => Isolated(() => Task.FromResult(starting.Contains(id)));

    /// <summary>
    /// The first message of a child's first run: its task and how it reports (M/DelegationDelegateTool.swift firstInput). A
    /// Claude child commits its work itself; a child of any other agent leaves it for the app's commit (<see cref="ChildAutoCommit"/>).
    /// It names a Claude parent as one.
    /// </summary>
    public static string FirstInput(string task, ChildWorktree worktree, string provider = "claude", string? parentAgent = null)
    {
        var from = (parentAgent ?? provider) == "claude" ? "Another Claude pane" : "Another pane";
        var done = provider == "claude"
            ? $"When you are done, commit your work on this branch, leaving no uncommitted changes to tracked files, and then write your report to {worktree.ReportFile}"
            : $"When you are done, write your report to {worktree.ReportFile}";
        var leave = provider == "claude" ? "" : " " + AppCommitNote(worktree);
        return $"{from} in Mighty Claude delegated this task to you. You work in your own git worktree on the branch {worktree.Branch}; the task is also saved in {worktree.TaskFile}.\n\n"
            + $"{done}: what you did, what is left and what your parent should check.{leave} Your parent gets one notice for each of your runs that ends with that file changed, and may merge your branch. Do not merge, push or switch branches.\n\n"
            + $"Task:\n{task}";
    }

    /// <summary>How a child of an agent other than Claude leaves its work: the app commits it on the child's branch when the child writes its report.</summary>
    public static string AppCommitNote(ChildWorktree worktree) =>
        $"Do not commit: leave your changes in this worktree. When your run ends with {worktree.ReportFile} changed, Mighty Claude commits every change .gitignore lets in on {worktree.Branch} for you.";

    /// <summary>
    /// The first run of a child the human started: their text as they typed it, then the app's note on where the child works and
    /// how it leaves its work for the human's merge from its card. A text that is a slash command (it starts with <c>/</c>) runs
    /// alone, as the human typed it, so the note never becomes the command's arguments; the note follows as the child's next request.
    /// </summary>
    public static HumanChildFirstRun HumanFirstRun(string text, ChildWorktree worktree, string provider = "claude", string? parentAgent = null)
    {
        var note = HumanNote(worktree, provider, parentAgent);
        return text.StartsWith('/') ? new(text, note) : new(text + "\n\n" + note, null);
    }

    /// <summary>The app's note to a child the human started. A Claude child commits its work itself; a child of any other agent leaves it for the app. It names a Claude parent as one.</summary>
    public static string HumanNote(ChildWorktree worktree, string provider = "claude", string? parentAgent = null)
    {
        var beside = (parentAgent ?? provider) == "claude" ? "another Claude pane" : "another pane";
        return provider == "claude"
            ? $"[Mighty Claude] This request runs in its own git worktree on the branch {worktree.Branch}, opened beside {beside} so that pane keeps working undisturbed. When you are done, commit your work on this branch, leaving no uncommitted changes to tracked files, and then write a short report to {worktree.ReportFile}: what you did and what is left. The person who sent this merges your branch from the app. Do not merge, push or switch branches."
            : $"[Mighty Claude] This request runs in its own git worktree on the branch {worktree.Branch}, opened beside {beside} so that pane keeps working undisturbed. When you are done, write a short report to {worktree.ReportFile}: what you did and what is left. {AppCommitNote(worktree)} The person who sent this merges your branch from the app. Do not merge, push or switch branches.";
    }

    /// <summary>
    /// The human's run of <paramref name="text"/> in a new child of the Claude, Codex or Antigravity pane
    /// <paramref name="parentId"/>, from its composer's second send (<see cref="ComposerSecondSend"/>; M/DelegationDelegateTool.swift
    /// startHumanChild). The child runs the parent's agent and is made as delegate makes one, in the parent's permission mode now
    /// (so never wider), or in plan mode when <paramref name="planFirst"/> (the parent's style starts every request in plan mode)
    /// and the agent has one. It is marked started by the human, so it sends the parent no notices and the parent's tools neither
    /// list it nor act on it; it has its own cap of 3 open children started by the human, apart from the 3 the parent's delegate
    /// may have. The first rule that applies answers, and nothing is made by any but the last:
    /// 1. Delegation is switched off, or <paramref name="text"/> is no task (empty, with a NUL character or over 64 KiB): a failure in the app's language.
    /// 2. <c>parent_closed</c> (no such pane), <c>claude_only</c> (no agent pane), a Codex or Antigravity mode in which the child
    ///    could not change files (a failure in the app's language), then delegate's own refusals: <c>child_cannot_delegate</c>,
    ///    <c>width_cap</c>, <c>wider_mode</c>; <c>not_git</c>, <c>detached_head</c>, <c>unborn_branch</c>, <c>low_disk</c>; <c>store_full</c>.
    /// 3. A delegation file the disk refuses: a failure.
    /// Otherwise the child's record is saved in creating and the answer names it, while its worktree, pane and first run follow in
    /// the background: <paramref name="starter"/> starts that run in the child's pane (<see cref="HumanFirstRun"/>), and hears when
    /// the worktree, the pane or that run could not be made after all.
    /// </summary>
    public Task<HumanChildStart> StartHumanChild(string text, string parentId, HumanChildStarter starter, bool planFirst = false) => Isolated(async () =>
    {
        if (!isSwitchOn()) return (HumanChildStart)new HumanChildStart.Failed(Locale.Get("delegation.human.error.switchedOff"));
        if (string.IsNullOrWhiteSpace(text) || text.Contains('\0') || Encoding.UTF8.GetByteCount(text) > DelegationFileStore.MaximumCopyBytes) return new HumanChildStart.Failed(Locale.Get("delegation.human.error.invalidTask"));
        if (await Host(() => host.PaneState(parentId)) is not { } parent) return new HumanChildStart.Refused(DelegationReasonCode.ParentClosed);
        if (!DelegationProviders.IsAgentPane(parent.Kind, parent.Provider)) return new HumanChildStart.Refused(DelegationReasonCode.ClaudeOnly);
        var mode = planFirst && DelegationProviders.StartingModes(parent.Provider).Contains("plan") ? "plan" : parent.PermissionMode;
        if (!DelegationProviders.ChildCanWrite(parent.Provider, mode)) return new HumanChildStart.Failed(Locale.Get("delegation.human.error.readOnlyMode"));
        if (DelegateRefusal(parentId, parent, mode, byHuman: true) is { } reason) return new HumanChildStart.Refused(reason);
        ChildWorktreeBase start;
        switch (await worktrees.Check(parent.Folder))
        {
            case ChildWorktreeCheck.Ready ready: start = ready.Base; break;
            case ChildWorktreeCheck.Refused refused: return new HumanChildStart.Refused(refused.Reason);
            default: throw new InvalidOperationException();
        }
        var context = await PruneContext();
        // Nothing below suspends until the record is saved, so two sends at once cannot both pass the width cap.
        if (DelegateRefusal(parentId, parent, mode, byHuman: true) is { } late) return new HumanChildStart.Refused(late);
        var id = Wire.Id();
        // No repeat to recognise: each send is its own child.
        var key = DelegationRequestKey.Make(parentId, "human-" + id, text, mode);
        var child = ChildRecord.Make(id, parentId, worktrees.WorktreePath(id), start.ParentBranch, start.BaseCommit, mode, key, parentCheckout: start.Repository, startedByHuman: true, provider: parent.Provider);
        try
        {
            if (File.Admitting(child, context, DelegationCopy.Of(id, DelegationCopyKind.Task, 0, Encoding.UTF8.GetBytes(text))) is not DelegationAdmission.Admitted admitted) return new HumanChildStart.Refused(DelegationReasonCode.StoreFull);
            Commit(admitted.File, context);
        }
        catch (DelegationFileException ex) when (ex.Error == DelegationFileError.Full) { return new HumanChildStart.Refused(DelegationReasonCode.StoreFull); }
        catch (Exception ex) when (DelegationFileStore.IsSaveFailure(ex)) { return new HumanChildStart.Failed(Locale.Get("delegation.human.error.notSaved")); }
        starting.Add(id);
        _ = turn.StartNew(() => StartChild(id, start, text, starter));
        return new HumanChildStart.Started(child);
    });

    /// <summary>The parent's child made by the same call: the same request key.</summary>
    private ChildRecord? SameCall(string key, string parentId) => File.Children.Find(child => child.ParentSessionId == parentId && child.RequestKey == key);

    /// <summary>
    /// Why the pane <paramref name="parentId"/>, now <paramref name="parent"/>, may not have a new child in <paramref name="mode"/>,
    /// or null when it may. The cap counts the open children of the same kind: those its delegate made, or (<paramref name="byHuman"/>)
    /// those the human started from its composer, so neither kind blocks the other.
    /// </summary>
    private DelegationReasonCode? DelegateRefusal(string parentId, DelegationPaneState parent, string mode, bool byHuman = false)
    {
        // A pane with a parent link is already one layer down.
        var depth = parent.ParentSessionId is not null || File.Children.Any(child => child.Id == parentId) ? 1 : 0;
        if (depth >= MaximumDepth) return DelegationReasonCode.ChildCannotDelegate;
        if (File.Children.Count(child => child.ParentSessionId == parentId && child.State.IsOpen() && child.NotifiesParent != byHuman) >= MaximumOpenChildren) return DelegationReasonCode.WidthCap;
        return IsNoWider(mode, parent.PermissionMode, parent.Provider) ? null : DelegationReasonCode.WiderMode;
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
