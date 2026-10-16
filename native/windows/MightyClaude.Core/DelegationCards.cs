namespace MightyClaude.Core;

/// <summary>What a human can do from a delegated child's card (M/DelegationCards.swift:3-11).</summary>
public enum DelegationCardAction
{
    /// <summary>Merge the reported head into the recorded parent branch.</summary>
    Merge,
    /// <summary>Undo the child's last merge.</summary>
    Undo,
    /// <summary>Remove the child's worktree and branch by force, after a confirmation.</summary>
    Discard,
}

/// <summary>The answer to a human's discard from a child's card, once confirmed (M/DelegationCards.swift:26-37).</summary>
public abstract record ChildCardDiscardOutcome
{
    /// <summary>The child's worktree, the worktrees nested in it and its branch are gone.</summary>
    public sealed record Discarded : ChildCardDiscardOutcome;
    /// <summary>
    /// More worktrees are nested in the child's now than the confirmation named, so nothing was removed. These are all of them,
    /// deepest first, for the card to ask again.
    /// </summary>
    public sealed record NestedChanged(IReadOnlyList<string> Nested) : ChildCardDiscardOutcome;
    /// <summary>
    /// Nothing after this step was removed; a later discard goes on from here. The words are in the app's language, with git's
    /// and Windows' own output kept as it is.
    /// </summary>
    public sealed record Failed(string Message) : ChildCardDiscardOutcome;
}

public sealed partial record DelegationFile
{
    /// <summary>
    /// The merge a child's card can undo (M/DelegationCards.swift:39-51): the child's last one, while the child is merged, or
    /// closed with that merge's head as the head it was closed at. null otherwise, so an older merge never comes back to undo.
    /// </summary>
    public MergeRecord? UndoableMerge(string id)
    {
        if (Children.Find(child => child.Id == id) is not { } child || Merges.FindLast(merge => merge.ChildId == id) is not { } record) return null;
        return child.State switch
        {
            ChildState.Merged => record,
            ChildState.Closed => string.Equals(child.ClosedHead, record.ChildHead, StringComparison.OrdinalIgnoreCase) ? record : null,
            _ => null,
        };
    }
}

/// <summary>
/// A human's merge, undo and discard from a child's card, and the nested worktrees its discard confirmation names
/// (M/DelegationCards.swift:53-180). The tools never reach these.
/// </summary>
public sealed partial class DelegationCoordinator
{
    /// <summary>
    /// A human's merge of the child from its card, by <see cref="ChildMerge.CardMerge"/> in the parent's checkout recorded when
    /// the child was made, else the parent pane's folder. It takes its turn with the tool's merges, so two never run git in one
    /// checkout at once, and while it runs delivery starts no run in the parent's pane or the child's. A refusal carries one
    /// reason code and changes nothing; with no parent checkout known it is <c>branch_not_checked_out</c>. A merge writes its
    /// MergeRecord and moves the child to merged; should the child have moved on meanwhile, the record is written all the same.
    /// </summary>
    public Task<ChildMergeOutcome> MergeFromCard(string id) => Isolated(async () =>
    {
        await mergeTurns.Take();
        ChildMergeOutcome outcome;
        try { outcome = await CardMerge(id); }
        finally { cardPanes.Clear(); mergeTurns.Pass(); }
        await DeliverPending();
        return outcome;
    });

    /// <summary>
    /// The request for the child <paramref name="id"/> to resolve its conflict with the parent branch
    /// (<see cref="ChildMerge.ConflictPrompt"/>), after its card's merge was refused with <c>merge_conflict</c>: once per report,
    /// so null when the child is not reported or was already asked at this report. A request that did not start is given back
    /// with <see cref="ConflictResolutionNotStarted"/>.
    /// </summary>
    public Task<string?> ConflictResolution(string id) => Isolated(() =>
    {
        if (File.Children.Find(record => record.Id == id) is not { State: ChildState.Reported } child
            || (conflictAsked.TryGetValue(id, out var asked) && asked == child.ReportRevision)) return Task.FromResult<string?>(null);
        conflictAsked[id] = child.ReportRevision;
        return Task.FromResult<string?>(ChildMerge.ConflictPrompt(child));
    });

    /// <summary>The child's pane did not start the request to resolve its conflict, so the next merge that conflicts at this report asks again.</summary>
    public Task ConflictResolutionNotStarted(string id) => Isolated(() => { conflictAsked.Remove(id); return Task.CompletedTask; });

    private async Task<ChildMergeOutcome> CardMerge(string id)
    {
        if (File.Children.Find(record => record.Id == id) is not { } child) return new ChildMergeOutcome.Failed(Locale.Get("delegation.card.error.noChild"));
        cardPanes.Clear(); cardPanes.Add(child.ParentSessionId); cardPanes.Add(child.Id);
        if (await ParentCheckout(child) is not { } checkout) return new ChildMergeOutcome.Refused(DelegationReasonCode.BranchNotCheckedOut);
        var outcome = await ChildMerge.CardMerge(child, checkout);
        if (outcome is not ChildMergeOutcome.Merged { Record: var record }) return outcome;
        KeepMerge(record);
        var context = await PruneContext();
        // The merge stands and stays on record in memory; the human hears that the file could not be saved, and the next save writes it.
        try { Commit(File, context); } catch (Exception ex) when (DelegationFileStore.IsSaveFailure(ex)) { return new ChildMergeOutcome.MergedUnsaved(record, UnsavedReason(ex)); }
        return outcome;
    }

    /// <summary>A merge that is made goes on record: with its child merged, or all the same should the child have moved on meanwhile.</summary>
    private void KeepMerge(MergeRecord record) =>
        File = File.RecordingMerge(record) ?? (File.Children.Any(child => child.Id == record.ChildId) ? File with { Merges = File.Merges.Add(record) } : File);

    /// <summary>Why the delegation file was not saved, for a card's note: in the app's language, with no closing stop, since the note goes on after it.</summary>
    public static string UnsavedReason(Exception error) => error is DelegationFileException known
        ? Locale.Get(known.Error == DelegationFileError.Full ? "delegation.card.error.fileFull" : "delegation.card.error.fileUnreadable")
        : error.Message.Trim('.', '。', ' ', '\r', '\n');

    /// <summary>The same in English, for the merge tool's agent.</summary>
    public static string UnsavedReasonForAgent(Exception error) => error is DelegationFileException known
        ? known.Error == DelegationFileError.Full ? "the delegation file is at its 4 MiB cap" : "the delegation file on disk cannot be read"
        : error.Message.Trim('.', '。', ' ', '\r', '\n');

    /// <summary>
    /// A human's undo from the child's card of the merge <see cref="DelegationFile.UndoableMerge"/> names, by
    /// <see cref="ChildMerge.Undo"/>. The parent pane's activity decides <c>parent_busy</c>, and a closed parent pane counts as
    /// idle; while the undo runs, delivery starts no run in the parent's pane, so it stays idle throughout. A refusal carries
    /// one reason code and changes nothing. An undo takes the merge record out of the file: a merged child is reported again,
    /// and a closed one stays closed, its branch made again at its recorded head.
    /// </summary>
    public Task<ChildUndoOutcome> UndoMergeFromCard(string id) => Isolated(async () =>
    {
        await mergeTurns.Take();
        ChildUndoOutcome outcome;
        try { outcome = await CardUndo(id); }
        finally { cardPanes.Clear(); mergeTurns.Pass(); }
        await DeliverPending();
        return outcome;
    });

    private async Task<ChildUndoOutcome> CardUndo(string id)
    {
        static ChildUndoOutcome NoMerge() => new ChildUndoOutcome.Failed(Locale.Get("delegation.card.error.noMergeToUndo"));
        if (File.Children.Find(record => record.Id == id) is not { } child || File.UndoableMerge(id) is null) return NoMerge();
        cardPanes.Clear(); cardPanes.Add(child.ParentSessionId);
        if (await ParentCheckout(child) is not { } checkout) return new ChildUndoOutcome.Refused(DelegationReasonCode.BranchNotCheckedOut);
        var activity = (await Host(() => host.PaneState(child.ParentSessionId)))?.Activity;
        // As it is now: the child may have moved on while the host answered.
        if (File.Children.Find(record => record.Id == id) is not { } current || File.UndoableMerge(id) is not { } merge) return NoMerge();
        var outcome = await ChildMerge.Undo(merge, current, checkout, activity);
        if (outcome is not ChildUndoOutcome.Undone { Record: var undone, RestoredBranch: var restored }) return outcome;
        // Undone on disk, so its record goes whatever the child did meanwhile.
        File = File.RecordingUndo(undone) ?? (File.Merges.FindLastIndex(merge => merge == undone) is >= 0 and var index ? File with { Merges = File.Merges.RemoveAt(index) } : File);
        var context = await PruneContext();
        try { Commit(File, context); } catch (Exception ex) when (DelegationFileStore.IsSaveFailure(ex)) { return new ChildUndoOutcome.UndoneUnsaved(undone, restored, UnsavedReason(ex)); }
        return outcome;
    }

    /// <summary>
    /// A human's discard from the child's card once its confirmation named <paramref name="confirmedNested"/> as the worktrees
    /// nested in the child's (null when it could not list them). The nested worktrees are listed before anything else: when
    /// they cannot be, the answer is a failure and nothing is stopped or removed. Then the child's run is stopped, so nothing
    /// nests another worktree in it, and the nested worktrees are listed again: when one is there that the confirmation did not
    /// name, nothing is removed and the answer names them all, to ask again. Otherwise <see cref="DiscardChild"/> removes the
    /// child's worktree, those nested in it and its branch by force, in its turn with the merges and undos.
    /// </summary>
    public Task<ChildCardDiscardOutcome> DiscardFromCard(string id, IEnumerable<string>? confirmedNested) => Isolated<ChildCardDiscardOutcome>(async () =>
    {
        if (!File.Children.Any(child => child.Id == id && child.State != ChildState.Discarded)) return new ChildCardDiscardOutcome.Failed(Locale.Get("delegation.card.error.cannotDiscard"));
        if (starting.Contains(id)) return new ChildCardDiscardOutcome.Failed(Locale.Get("delegation.card.error.stillStarting"));
        if (await NestedWorktrees(id) is null) return new ChildCardDiscardOutcome.Failed(Locale.Get("delegation.card.error.nestedUnlisted"));
        await Host(() => host.StopRun(id));
        await mergeTurns.Take();
        try
        {
            if (await NestedWorktrees(id) is not { } nested) return new ChildCardDiscardOutcome.Failed(Locale.Get("delegation.card.error.nestedUnlisted"));
            if (!nested.ToHashSet(DelegationPaths.Comparer).IsSubsetOf((confirmedNested ?? []).Select(DelegationPaths.Standardized))) return new ChildCardDiscardOutcome.NestedChanged(nested);
            return await DiscardChild(id) is ChildDiscardOutcome.Failed failed ? new ChildCardDiscardOutcome.Failed(failed.Message) : new ChildCardDiscardOutcome.Discarded();
        }
        finally { mergeTurns.Pass(); }
    });

    /// <summary>
    /// The registered worktrees nested in the child's, deepest first, for the discard confirmation to name; null when they
    /// could not be listed (the parent's checkout is not known, or git failed).
    /// </summary>
    public async Task<IReadOnlyList<string>?> NestedWorktrees(string id) =>
        File.Children.Find(record => record.Id == id) is { } child && await ParentCheckout(child) is { } checkout ? await ChildCleanup.NestedWorktrees(child, checkout) : null;
}
