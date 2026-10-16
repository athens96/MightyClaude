namespace MightyClaude.Core;

/// <summary>
/// Delegation's words in the app's language, as the sidebar's child rows and cards and the two confirmations say them
/// (M/WorkspaceView.swift:875-955). Git's and Windows' own output stays as it is, inside the sentence that names it.
/// </summary>
public static class DelegationText
{
    /// <summary>A child's state (<c>delegation.child.state.*</c>).</summary>
    public static string State(ChildState state) => Locale.Get(state switch
    {
        ChildState.Creating => "delegation.child.state.creating", ChildState.Running => "delegation.child.state.running", ChildState.Waiting => "delegation.child.state.waiting",
        ChildState.Reported => "delegation.child.state.reported", ChildState.Merged => "delegation.child.state.merged", ChildState.Ended => "delegation.child.state.ended",
        ChildState.Interrupted => "delegation.child.state.interrupted", ChildState.Failed => "delegation.child.state.failed", ChildState.Closed => "delegation.child.state.closed",
        _ => "delegation.child.state.discarded",
    });

    /// <summary>A card action's button (<c>delegation.card.*</c>).</summary>
    public static string Action(DelegationCardAction action) => Locale.Get(action switch
    {
        DelegationCardAction.Merge => "delegation.card.merge", DelegationCardAction.Undo => "delegation.card.undo", _ => "delegation.card.discard",
    });

    /// <summary>What a card action does to this child, for its tooltip and its accessible name.</summary>
    public static string ActionHelp(DelegationCardAction action, DelegationChildRow child) => action switch
    {
        DelegationCardAction.Merge => Locale.Get("delegation.card.mergeHelp", new Dictionary<string, string> { ["branch"] = Branch(child) }),
        DelegationCardAction.Undo => Locale.Get("delegation.card.undoHelp", new Dictionary<string, string> { ["branch"] = Branch(child) }),
        _ => Locale.Get("delegation.card.discardHelp", new Dictionary<string, string> { ["branch"] = ChildRecord.BranchName(child.Id) }),
    };

    /// <summary>The parent's branch as a card names it: the recorded one, or the words for it when none was recorded.</summary>
    private static string Branch(DelegationChildRow child) => child.ParentBranch.Length == 0 ? Locale.Get("delegation.card.parentBranch") : child.ParentBranch;

    /// <summary>
    /// The card's line for its last answer, in its tone; null when there is nothing to say: a discard that went through closes
    /// the pane and leaves the row, and one asked again shows its confirmation instead.
    /// </summary>
    public static (string Text, DesignTone Tone)? Note(DelegationCardNote note, DelegationChildRow child)
    {
        Dictionary<string, string> Words(params (string Key, string Value)[] pairs) => pairs.ToDictionary(pair => pair.Key, pair => pair.Value);
        var branch = Branch(child);
        return (note.Action, note.Result) switch
        {
            (DelegationCardAction.Merge, DelegationCardResult.Done) => (Locale.Get("delegation.card.merged", Words(("branch", branch))), DesignTone.Done),
            (DelegationCardAction.Undo, DelegationCardResult.Done) => (Locale.Get("delegation.card.undone", Words(("branch", branch))), DesignTone.Done),
            (DelegationCardAction.Merge, DelegationCardResult.DoneUnsaved unsaved) => (Locale.Get("delegation.card.mergedUnsaved", Words(("branch", branch), ("error", unsaved.Reason))), DesignTone.Err),
            (DelegationCardAction.Undo, DelegationCardResult.DoneUnsaved unsaved) => (Locale.Get("delegation.card.undoneUnsaved", Words(("branch", branch), ("error", unsaved.Reason))), DesignTone.Err),
            (DelegationCardAction.Discard, DelegationCardResult.Done or DelegationCardResult.DoneUnsaved) or (_, DelegationCardResult.Reconfirm) => null,
            // Said while the child works on it; once it reports again (or ends without a report) its state says the rest.
            (DelegationCardAction.Merge, DelegationCardResult.ConflictSent) => child.State is ChildState.Creating or ChildState.Running or ChildState.Waiting ? (Locale.Get("delegation.card.conflictSent", Words(("branch", branch))), DesignTone.Wait) : null,
            (DelegationCardAction.Merge, DelegationCardResult.ConflictNotStarted) => (Locale.Get("delegation.card.conflictNotStarted", Words(("branch", branch))), DesignTone.Err),
            (_, DelegationCardResult.ConflictSent or DelegationCardResult.ConflictNotStarted) => null,
            (DelegationCardAction.Merge, DelegationCardResult.Refused refused) => (Locale.Get("delegation.card.mergeRefused", Words(("reason", Reason(refused.Reason)))), DesignTone.Wait),
            (DelegationCardAction.Undo, DelegationCardResult.Refused refused) => (Locale.Get("delegation.card.undoRefused", Words(("reason", Reason(refused.Reason)))), DesignTone.Wait),
            (DelegationCardAction.Discard, DelegationCardResult.Refused refused) => (Locale.Get("delegation.card.discardFailed", Words(("error", Reason(refused.Reason)))), DesignTone.Err),
            (DelegationCardAction.Merge, DelegationCardResult.Failed failed) => (Locale.Get("delegation.card.mergeFailed", Words(("error", failed.Message))), DesignTone.Err),
            (DelegationCardAction.Undo, DelegationCardResult.Failed failed) => (Locale.Get("delegation.card.undoFailed", Words(("error", failed.Message))), DesignTone.Err),
            (_, DelegationCardResult.Failed failed) => (Locale.Get("delegation.card.discardFailed", Words(("error", failed.Message))), DesignTone.Err),
            _ => null,
        };
    }

    /// <summary>
    /// What closing the child's pane left, when the card says so: the Windows-only <c>link</c> hold, which keeps a merged
    /// child's worktree that a junction or directory link lies in; null for any other close, as on the Mac.
    /// </summary>
    public static string? CloseNote(ChildCleanupOutcome? outcome) => outcome is ChildCleanupOutcome.Kept { Hold: ChildCleanupHold.Link } ? Locale.Get("delegation.card.keptLink") : null;

    /// <summary>Why a card's merge or undo, or the human's run in a new child worktree, was refused, ending in its reason code.</summary>
    public static string Reason(DelegationReasonCode reason) => reason switch
    {
        DelegationReasonCode.NotGit => Locale.Get("delegation.reason.notGit"),
        DelegationReasonCode.DetachedHead => Locale.Get("delegation.reason.detachedHead"),
        DelegationReasonCode.UnbornBranch => Locale.Get("delegation.reason.unbornBranch"),
        DelegationReasonCode.WidthCap => Locale.Get("delegation.reason.widthCap"),
        DelegationReasonCode.LowDisk => Locale.Get("delegation.reason.lowDisk"),
        DelegationReasonCode.StoreFull => Locale.Get("delegation.reason.storeFull"),
        DelegationReasonCode.NotReported => Locale.Get("delegation.reason.notReported"),
        DelegationReasonCode.BranchNotCheckedOut => Locale.Get("delegation.reason.branchNotCheckedOut"),
        DelegationReasonCode.TrackedChanges => Locale.Get("delegation.reason.trackedChanges"),
        DelegationReasonCode.MergeConflict => Locale.Get("delegation.reason.mergeConflict"),
        DelegationReasonCode.UndoParentMoved => Locale.Get("delegation.reason.undoParentMoved"),
        DelegationReasonCode.ParentBusy => Locale.Get("delegation.reason.parentBusy"),
        DelegationReasonCode.MergeHumanOnly => Locale.Get("delegation.reason.mergeHumanOnly"),
        _ => Locale.Get("delegation.reason.other", new Dictionary<string, string> { ["code"] = DelegationNames.Of(reason) }),
    };

    /// <summary>The discard confirmation's title: its question, or that the child cannot be discarded now when the worktrees nested in it could not be listed.</summary>
    public static string DiscardTitle(PendingChildDiscard pending) => Locale.Get(pending.Nested is null ? "delegation.discard.unavailableTitle" : "delegation.discard.title");

    /// <summary>The discard confirmation's text: what goes, and the nested worktrees that go with it; or, when those could not be listed, why nothing can go now.</summary>
    public static string DiscardMessage(PendingChildDiscard pending)
    {
        if (pending.Nested is not { } paths) return Locale.Get("delegation.discard.unavailable", new Dictionary<string, string> { ["title"] = pending.Title });
        var nested = paths.Count == 0 ? Locale.Get("delegation.discard.noNested")
            : Locale.Get("delegation.discard.nested", new Dictionary<string, string> { ["count"] = paths.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), ["paths"] = string.Join("\n", paths) });
        return Locale.Get("delegation.discard.message", new Dictionary<string, string> { ["title"] = pending.Title, ["branch"] = pending.Branch }) + "\n\n" + nested;
    }

    /// <summary>The offer after a merge from the card: the merged work stays in the parent's branch; what the discard removes, and the nested worktrees that go with it.</summary>
    public static string MergedDiscardMessage(PendingChildDiscard pending)
    {
        var nested = pending.Nested is { Count: > 0 } paths
            ? "\n\n" + Locale.Get("delegation.discard.nested", new Dictionary<string, string> { ["count"] = paths.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), ["paths"] = string.Join("\n", paths) }) : "";
        return Locale.Get("delegation.mergedDiscard.message", new Dictionary<string, string> { ["title"] = pending.Title, ["branch"] = pending.Branch }) + nested;
    }

    /// <summary>The parent-close confirmation's text, with the count of its open children.</summary>
    public static string ParentCloseMessage(PendingParentClose pending) =>
        Locale.Get("delegation.parentClose.message", new Dictionary<string, string> { ["title"] = pending.Title, ["count"] = pending.OpenChildren.ToString(System.Globalization.CultureInfo.InvariantCulture) });
}
