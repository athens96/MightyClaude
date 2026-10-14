namespace MightyClaude.Core;

/// <summary>The answer to a merge of a delegated child (M/DelegationMerge.swift:3-18).</summary>
public abstract record ChildMergeOutcome
{
    /// <summary>The recorded parent branch now holds the child's head. The record is what <see cref="DelegationFile.RecordingMerge"/> writes.</summary>
    public sealed record Merged(MergeRecord Record) : ChildMergeOutcome;
    /// <summary>A card's merge is made and on record in memory, but the delegation file could not be saved (why, in the app's language); the next save writes it.</summary>
    public sealed record MergedUnsaved(MergeRecord Record, string Reason) : ChildMergeOutcome;
    /// <summary>Refused before any git write; nothing changed.</summary>
    public sealed record Refused(DelegationReasonCode Reason) : ChildMergeOutcome;
    /// <summary>
    /// Git was missing or failed. A fast-forward git refuses leaves the parent as it was. The words are English for the merge
    /// tool's agent and in the app's language for a card, with git's own output kept as it is.
    /// </summary>
    public sealed record Failed(string Message) : ChildMergeOutcome;
}

/// <summary>The answer to a human's undo of a merge, from the child's card (M/DelegationMerge.swift:20-37).</summary>
public abstract record ChildUndoOutcome
{
    /// <summary>
    /// The recorded parent branch, its index and its files are back at the pre-merge commit; <paramref name="RestoredBranch"/>
    /// says the child's deleted branch was made again at its recorded head. <see cref="DelegationFile.RecordingUndo"/> writes it.
    /// </summary>
    public sealed record Undone(MergeRecord Record, bool RestoredBranch) : ChildUndoOutcome;
    /// <summary>A card's undo is made and its record taken out in memory, but the delegation file could not be saved (why, in the app's language); the next save writes it.</summary>
    public sealed record UndoneUnsaved(MergeRecord Record, bool RestoredBranch, string Reason) : ChildUndoOutcome;
    /// <summary>Refused before any git write; nothing changed.</summary>
    public sealed record Refused(DelegationReasonCode Reason) : ChildUndoOutcome;
    /// <summary>
    /// The record is no undoable merge of this child, or git was missing or failed. The steps already taken are put back. The
    /// words are in the app's language, with git's own output kept as it is.
    /// </summary>
    public sealed record Failed(string Message) : ChildUndoOutcome;
}

/// <summary>Merges a delegated child into its recorded parent branch, and undoes it (M/DelegationMerge.swift:39-231).</summary>
public static class ChildMerge
{
    /// <summary>The longest the one git write may take, well inside the limit of a tool call.</summary>
    public const double FastForwardTimeout = 30;
    /// <summary>The longest one git step of a card merge or undo may take. A human waits on these, not a tool call.</summary>
    public const double CardTimeout = 120;

    /// <summary>
    /// The parent's merge tool, fast-forward only. <paramref name="parentCheckout"/> is the parent pane's folder. The first rule
    /// that applies answers, and every refusal comes before any git write:
    /// 1. <c>not_reported</c>: the child is not reported (a new run, an end without a report or a merge clears it).
    /// 2. <c>worktree_missing</c>: the child's worktree is gone.
    /// 3. <c>head_moved</c>: <paramref name="expectedHead"/> is not the reported head, or the child's worktree is no longer on its own branch at that head.
    /// 4. <c>tracked_changes</c>: the child's worktree has changes to tracked files.
    /// 5. <c>branch_not_checked_out</c>: the parent checkout does not have the recorded parent branch checked out.
    /// 6. <c>tracked_changes</c>: the parent checkout has changes to tracked files.
    /// 7. <c>diverged</c>: the parent branch's head is not an ancestor of the expected head, so it cannot fast-forward to it.
    /// Otherwise its only git write is one <c>git merge --ff-only</c> of the expected head in the parent checkout. The child's branch is only read.
    /// </summary>
    public static async Task<ChildMergeOutcome> ToolMerge(ChildRecord child, string expectedHead, string parentCheckout)
    {
        static ChildMergeOutcome Refused(DelegationReasonCode reason) => new ChildMergeOutcome.Refused(reason);
        static ChildMergeOutcome Failed(string message) => new ChildMergeOutcome.Failed(message);
        if (child.State != ChildState.Reported || child.ReportHead is not { } reported) return Refused(DelegationReasonCode.NotReported);
        if (!IsFolder(child.WorktreePath)) return Refused(DelegationReasonCode.WorktreeMissing);
        if (DelegationGit.Executable is null) return Failed("Git is missing.");
        var expected = expectedHead.Trim().ToLowerInvariant();
        if (!DelegationGit.IsObjectName(expected) || expected != reported.ToLowerInvariant()) return Refused(DelegationReasonCode.HeadMoved);

        if (await DelegationGit.Run(["symbolic-ref", "-q", "HEAD"], child.WorktreePath) is not { } childBranch) return Failed("Git did not finish.");
        if (childBranch.ExitCode is not (0 or 1)) return Refused(DelegationReasonCode.WorktreeMissing);
        if (childBranch.ExitCode != 0 || DelegationGit.Line(childBranch.Stdout) != "refs/heads/" + child.Branch || await Head(child.WorktreePath) != expected) return Refused(DelegationReasonCode.HeadMoved);
        switch (await HasTrackedChanges(child.WorktreePath))
        {
            case null: return Failed("Git could not read the child's changes.");
            case true: return Refused(DelegationReasonCode.TrackedChanges);
        }

        if (await CheckedOutHead(child.ParentBranch, parentCheckout) is not { } preMerge) return Refused(DelegationReasonCode.BranchNotCheckedOut);
        switch (await HasTrackedChanges(parentCheckout))
        {
            case null: return Failed("Git could not read the parent's changes.");
            case true: return Refused(DelegationReasonCode.TrackedChanges);
        }
        if (await DelegationGit.Run(["merge-base", "--is-ancestor", preMerge, expected], parentCheckout) is not { } ancestor) return Failed("Git did not finish.");
        if (ancestor.ExitCode == 1) return Refused(DelegationReasonCode.Diverged);
        if (ancestor.ExitCode != 0) return Failed(DelegationGit.Message(ancestor));

        if (await DelegationGit.Run(["merge", "--ff-only", "--no-autostash", "--no-stat", "-q", expected], parentCheckout, FastForwardTimeout) is not { } merged) return Failed("Git did not finish.");
        if (merged.ExitCode != 0) return Failed(DelegationGit.Message(merged));
        if (await Head(parentCheckout) != expected) return Failed("The parent branch is not at the merged head.");
        return new ChildMergeOutcome.Merged(new(child.Id, MergeKind.ToolFastForward, child.ParentBranch, preMerge, expected, expected));
    }

    /// <summary>
    /// A human's merge from the child's card. The reported head goes into the recorded parent branch by fast-forward when the
    /// branch's head is an ancestor of it, and otherwise as exactly one merge commit whose parents are the old parent head and
    /// the child's head. <paramref name="parentCheckout"/> is the parent's folder. The first rule that applies answers, and
    /// every refusal comes before any git write:
    /// 1. <c>not_reported</c>: the child is not reported.
    /// 2. <c>branch_not_checked_out</c>: the parent checkout does not have the recorded parent branch checked out.
    /// 3. <c>tracked_changes</c>: the parent checkout has changes to tracked files.
    /// 4. <c>merge_conflict</c>: <c>git merge-tree</c> finds conflicts between the parent's head and the child's.
    /// The merge commit holds exactly the tree merge-tree checked (made with <c>git commit-tree</c>). The branch, index and files
    /// then move to it, or to the child's head, by one <c>git merge --ff-only</c>, which never overwrites an untracked file. The
    /// child's branch is only read.
    /// </summary>
    public static async Task<ChildMergeOutcome> CardMerge(ChildRecord child, string parentCheckout)
    {
        static ChildMergeOutcome Refused(DelegationReasonCode reason) => new ChildMergeOutcome.Refused(reason);
        static ChildMergeOutcome Failed(string message) => new ChildMergeOutcome.Failed(message);
        if (child.State != ChildState.Reported || child.ReportHead?.ToLowerInvariant() is not { } reported || !DelegationGit.IsObjectName(reported)) return Refused(DelegationReasonCode.NotReported);
        if (DelegationGit.Executable is null) return Failed(Locale.Get("delegation.card.error.gitMissing"));
        if (await CheckedOutHead(child.ParentBranch, parentCheckout) is not { } preMerge) return Refused(DelegationReasonCode.BranchNotCheckedOut);
        switch (await HasTrackedChanges(parentCheckout))
        {
            case null: return Failed(Locale.Get("delegation.card.error.parentChangesUnread"));
            case true: return Refused(DelegationReasonCode.TrackedChanges);
        }
        if (await DelegationGit.Run(["merge-tree", "--write-tree", preMerge, reported], parentCheckout, CardTimeout) is not { } preview) return Failed(Locale.Get("delegation.card.error.gitUnfinished"));
        if (preview.ExitCode == 1) return Refused(DelegationReasonCode.MergeConflict);
        var tree = DelegationGit.Line(preview.Stdout).Split('\n')[0].TrimEnd('\r');
        if (preview.ExitCode != 0 || !DelegationGit.IsObjectName(tree)) return Failed(DelegationGit.Message(preview));

        if (await DelegationGit.Run(["merge-base", "--is-ancestor", preMerge, reported], parentCheckout) is not { } ancestor) return Failed(Locale.Get("delegation.card.error.gitUnfinished"));
        string target; MergeKind kind;
        switch (ancestor.ExitCode)
        {
            case 0: target = reported; kind = MergeKind.CardFastForward; break;
            case 1:
                var message = $"Merge branch '{child.Branch}' into {child.ParentBranch}";
                if (await DelegationGit.Run(["commit-tree", tree, "-p", preMerge, "-p", reported, "-m", message], parentCheckout, CardTimeout) is not { } made) return Failed(Locale.Get("delegation.card.error.gitUnfinished"));
                var commit = DelegationGit.Line(made.Stdout);
                if (made.ExitCode != 0 || !DelegationGit.IsObjectName(commit)) return Failed(DelegationGit.Message(made));
                target = commit; kind = MergeKind.CardMergeCommit; break;
            default: return Failed(DelegationGit.Message(ancestor));
        }
        if (await DelegationGit.Run(["merge", "--ff-only", "--no-autostash", "--no-stat", "-q", target], parentCheckout, CardTimeout) is not { } moved) return Failed(Locale.Get("delegation.card.error.gitUnfinished"));
        if (moved.ExitCode != 0) return Failed(DelegationGit.Message(moved));
        if (await Head(parentCheckout) != target) return Failed(Locale.Get("delegation.card.error.parentNotAtMerge"));
        return new ChildMergeOutcome.Merged(new(child.Id, kind, child.ParentBranch, preMerge, target, reported));
    }

    /// <summary>
    /// A human's undo of <paramref name="record"/>, a merge of <paramref name="child"/>, from the child's card.
    /// <paramref name="parentActivity"/> is the parent pane's, null when that pane is closed (a closed parent counts as idle).
    /// The child must be merged, or closed after the merge; any other child, or another child's record, fails with nothing
    /// changed. The first rule that applies answers, and every refusal comes before any git write:
    /// 1. <c>branch_not_checked_out</c>: the parent checkout does not have the recorded parent branch checked out.
    /// 2. <c>undo_parent_moved</c>: that branch's head is not the merged commit.
    /// 3. <c>parent_busy</c>: the parent pane is running.
    /// 4. <c>tracked_changes</c>: the parent checkout has changes to tracked files.
    /// Otherwise a deleted child branch is made again at its recorded head, the index and files go back to the pre-merge commit
    /// by <c>git read-tree -m -u</c> (never <c>reset --hard</c>: untracked files stay, and one in the way fails the undo), and the
    /// branch moves back from the merged commit by compare-and-swap. A step that fails puts the earlier ones back.
    /// </summary>
    public static async Task<ChildUndoOutcome> Undo(MergeRecord record, ChildRecord child, string parentCheckout, DelegationPaneActivity? parentActivity)
    {
        static ChildUndoOutcome Refused(DelegationReasonCode reason) => new ChildUndoOutcome.Refused(reason);
        static ChildUndoOutcome Failed(string key) => new ChildUndoOutcome.Failed(Locale.Get(key));
        if (record.ChildId != child.Id || child.State.After(ChildStateEvent.UndoMerge) is null) return Failed("delegation.card.error.noMergeToUndo");
        var merged = record.MergedCommit.ToLowerInvariant(); var preMerge = record.PreMergeCommit.ToLowerInvariant(); var childHead = record.ChildHead.ToLowerInvariant();
        if (!new[] { merged, preMerge, childHead }.All(DelegationGit.IsObjectName)) return Failed("delegation.card.error.badMergeRecord");
        if (DelegationGit.Executable is null) return Failed("delegation.card.error.gitMissing");
        if (await CheckedOutHead(record.ParentBranch, parentCheckout) is not { } head) return Refused(DelegationReasonCode.BranchNotCheckedOut);
        if (head != merged) return Refused(DelegationReasonCode.UndoParentMoved);
        if (parentActivity == DelegationPaneActivity.Running) return Refused(DelegationReasonCode.ParentBusy);
        switch (await HasTrackedChanges(parentCheckout))
        {
            case null: return Failed("delegation.card.error.parentChangesUnread");
            case true: return Refused(DelegationReasonCode.TrackedChanges);
        }

        var reason = $"mighty: undo the merge of {child.Branch}";
        var childRef = "refs/heads/" + child.Branch; var parentRef = "refs/heads/" + record.ParentBranch;
        if (await DelegationGit.Run(["rev-parse", "-q", "--verify", childRef], parentCheckout) is not { ExitCode: 0 or 1 } found) return Failed("delegation.card.error.childBranchUnread");
        var restoresBranch = found.ExitCode == 1;
        if (restoresBranch)
        {
            // An empty old value: made only while no such branch exists.
            if (await DelegationGit.Run(["update-ref", "-m", reason, childRef, childHead, ""], parentCheckout) is not { } made) return Failed("delegation.card.error.gitUnfinished");
            if (made.ExitCode != 0) return new ChildUndoOutcome.Failed(DelegationGit.Message(made));
        }
        async Task PutBranchBack() { if (restoresBranch) await DelegationGit.Run(["update-ref", "-m", reason, "-d", childRef, childHead], parentCheckout); }

        // Fresh file stamps, so read-tree compares what the files hold.
        await DelegationGit.Run(["update-index", "-q", "--refresh"], parentCheckout, CardTimeout);
        if (await DelegationGit.Run(["read-tree", "-m", "-u", merged, preMerge], parentCheckout, CardTimeout) is not { ExitCode: 0 })
        {
            await PutBranchBack(); return Failed("delegation.card.error.filesNotRestored");
        }
        if (await DelegationGit.Run(["update-ref", "-m", reason, parentRef, preMerge, merged], parentCheckout) is not { ExitCode: 0 })
        {
            await DelegationGit.Run(["read-tree", "-m", "-u", preMerge, merged], parentCheckout, CardTimeout);
            await PutBranchBack(); return Failed("delegation.card.error.parentMovedDuringUndo");
        }
        return new ChildUndoOutcome.Undone(record, restoresBranch);
    }

    /// <summary>The head of <paramref name="branch"/> when <paramref name="folder"/> is a checkout with that branch checked out, else null.</summary>
    public static async Task<string?> CheckedOutHead(string branch, string folder)
    {
        if (!Path.IsPathFullyQualified(folder) || folder.Contains('\0') || !IsFolder(folder)
            || await DelegationGit.Run(["symbolic-ref", "-q", "HEAD"], folder) is not { ExitCode: 0 } current || DelegationGit.Line(current.Stdout) != "refs/heads/" + branch) return null;
        return await Head(folder);
    }

    /// <summary>The commit checked out in <paramref name="folder"/>, or null.</summary>
    public static async Task<string?> Head(string folder)
    {
        if (await DelegationGit.Run(["rev-parse", "-q", "--verify", "HEAD^{commit}"], folder) is not { ExitCode: 0 } result) return null;
        var sha = DelegationGit.Line(result.Stdout);
        return DelegationGit.IsObjectName(sha) ? sha : null;
    }

    /// <summary>
    /// Whether tracked files in <paramref name="folder"/>'s checkout differ from its HEAD, staged or not; untracked and ignored
    /// files do not count. null when git failed. Git compares through the checkout's own line-ending settings, so files it
    /// wrote with CRLF read as unchanged.
    /// </summary>
    public static async Task<bool?> HasTrackedChanges(string folder) =>
        await DelegationGit.Run(["status", "--porcelain", "--untracked-files=no"], folder) is { ExitCode: 0 } status ? status.Stdout.Length > 0 : null;

    public static bool IsFolder(string path) => Directory.Exists(path);
}

public sealed partial record DelegationFile
{
    /// <summary>
    /// A finished merge written: its MergeRecord joins the file and the child moves from reported to merged. null, changing
    /// nothing, when no child here has the record's id or that child cannot move to merged.
    /// </summary>
    public DelegationFile? RecordingMerge(MergeRecord record)
    {
        var index = Children.FindIndex(child => child.Id == record.ChildId);
        return index >= 0 && Children[index].Applying(ChildStateEvent.Merge) is { } merged ? SettingChild(index, merged) with { Merges = Merges.Add(record) } : null;
    }

    /// <summary>
    /// A finished undo written: the merge record leaves the file, a merged child goes back to reported and a closed one stays
    /// closed. null, changing nothing, when the record is not here or its child cannot be undone.
    /// </summary>
    public DelegationFile? RecordingUndo(MergeRecord record)
    {
        var recordIndex = Merges.FindLastIndex(merge => merge == record); var index = Children.FindIndex(child => child.Id == record.ChildId);
        return recordIndex >= 0 && index >= 0 && Children[index].Applying(ChildStateEvent.UndoMerge) is { } undone ? SettingChild(index, undone) with { Merges = Merges.RemoveAt(recordIndex) } : null;
    }
}
