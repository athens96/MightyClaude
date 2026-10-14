using System.Text;

namespace MightyClaude.Core;

/// <summary>One follow-up as follow_up answers it (M/DelegationActionTools.swift:3-16).</summary>
/// <param name="Lane">Where delivery has it as the call is answered: delivered, held, or pending while the child's pane did not take it.</param>
/// <param name="Remaining">How many more follow-ups the child may still get.</param>
public sealed record DelegationFollowUpInfo(string Id, string Child, DeliveryLane Lane, int Remaining)
{
    public static DelegationFollowUpInfo Of(FollowUp followUp, int remaining) => new(followUp.Id, followUp.ChildId, followUp.Lane, remaining);
}

/// <summary>
/// The action tools (M/DelegationActionTools.swift:18-136): a parent merges a reported child into its recorded branch,
/// fast-forward only, and sends its children follow-ups.
/// </summary>
public sealed partial class DelegationCoordinator
{
    /// <summary>The follow-ups one child may get from its parent.</summary>
    public const int MaximumFollowUps = 2;
    /// <summary>
    /// The most a follow-up's text may take in the delegation file, its JSON escapes included: 48 KiB, so a child's two
    /// follow-ups, held and then released together by run next, always fit one run's input of at most 100,000 characters. A
    /// follow-up is kept whole until it is delivered, so it is never cut.
    /// </summary>
    public const int MaximumFollowUpBytes = 48 * 1024;

    /// <summary>
    /// merge(child, expected_head) from <paramref name="caller"/>. A child that is not the caller's is answered with an error.
    /// Otherwise the merge waits for its turn (one tool merge at a time) and runs <see cref="ChildMerge.ToolMerge"/> in the
    /// parent's checkout recorded when the child was made. Its refusal is answered with its one reason code
    /// (<c>not_reported</c>, <c>worktree_missing</c>, <c>head_moved</c>, <c>tracked_changes</c>, <c>branch_not_checked_out</c> or
    /// <c>diverged</c>) and changes nothing. A fast-forward writes its MergeRecord and moves the child to merged; should the
    /// child have moved on during the merge (its pane closed), the record is written all the same, so the merge can be undone
    /// from its card. Whether the call needs the human's approval first follows the parent pane's permission mode, in the
    /// Claude CLI, before the call reaches the app.
    /// </summary>
    private async Task<DelegationResponse> MergeNow(string id, string expectedHead, DelegationCaller caller)
    {
        id = id.Trim();
        if (!IsChild(id, caller)) return DelegationResponse.Failure(NotYourChildMessage(id));
        await mergeTurns.Take();
        try
        {
            // Read again after the wait: an earlier merge may have taken this child.
            if (File.Children.Find(record => record.Id == id) is not { } child) return DelegationResponse.Failure(NotYourChildMessage(id));
            if (await ParentCheckout(child) is not { } checkout) return DelegationResponse.Failure("The calling pane is closed.");
            switch (await ChildMerge.ToolMerge(child, expectedHead, checkout))
            {
                case ChildMergeOutcome.Refused refused: return DelegationResponse.Refusal(refused.Reason);
                case ChildMergeOutcome.Failed failed: return DelegationResponse.Failure($"Git did not merge the child: {failed.Message}");
                case ChildMergeOutcome.Merged { Record: var record }:
                    // The merge is made, so it is on record before anything else can run here, and stays so even should the disk
                    // refuse it now; the next save writes it, and the parent hears so.
                    KeepMerge(record);
                    var context = await PruneContext();
                    try { Commit(File, context); }
                    catch (Exception ex) when (DelegationFileStore.IsSaveFailure(ex))
                    {
                        return DelegationResponse.Failure($"The child is merged: {record.ParentBranch} now points at {record.MergedCommit}, and the app keeps the merge on record, but the delegation file could not be saved ({UnsavedReasonForAgent(ex)}). Mighty Claude saves it again by itself; nothing is needed from you. Do not merge this child again.");
                    }
                    return new(Merged: record);
                default: throw new InvalidOperationException();
            }
        }
        finally { mergeTurns.Pass(); }
    }

    /// <summary>
    /// follow_up(child, text) from <paramref name="caller"/>. The first rule that applies answers:
    /// 1. An empty text, a text over 48 KiB or with a NUL character (no run input may hold one, so a held one would block the
    ///    child's pane), or a child that is not the caller's is answered with an error.
    /// 2. <c>parent_closed</c>: the caller's pane is closed. A closed parent's children keep their cards, and nothing more is sent to them.
    /// 3. <c>child_closed</c>: the child is not open (its pane closed, its start failed or a human discarded it).
    /// 4. <c>follow_up_limit</c>: the child already has its two follow-ups.
    /// 5. <c>store_full</c>: even after pruning, the delegation file has no room for it.
    /// Every refusal changes nothing. Otherwise the follow-up is saved in the delegation file, pending, with its own id, and the
    /// child's count goes up by one; delivery then offers it to the child's pane and hands it over exactly once.
    /// </summary>
    private async Task<DelegationResponse> FollowUpNow(string id, string text, DelegationCaller caller)
    {
        if (string.IsNullOrWhiteSpace(text)) return DelegationResponse.Failure("follow_up needs a text: the instruction for the child.");
        // Escapes only add bytes, so the UTF-8 size rules out a huge text before it is encoded.
        if (Encoding.UTF8.GetByteCount(text) > MaximumFollowUpBytes || DelegationCopy.StoredBytes(text) > MaximumFollowUpBytes) return DelegationResponse.Failure("A follow-up's text may be at most 48 KiB.");
        if (text.Contains('\0')) return DelegationResponse.Failure("A follow-up's text may not contain a NUL character.");
        id = id.Trim();
        if (!IsChild(id, caller)) return DelegationResponse.Failure(NotYourChildMessage(id));
        // Asks the host about every parent with children, the caller among them.
        var context = await PruneContext();
        // Nothing below suspends until the follow-up is saved, so two calls at once cannot both pass the limit, and the caller's pane is as last seen.
        if (!context.OpenPaneIds.Contains(caller.PaneId)) return DelegationResponse.Refusal(DelegationReasonCode.ParentClosed);
        var index = File.Children.FindIndex(child => child.Id == id);
        if (index < 0) return DelegationResponse.Failure(NotYourChildMessage(id));
        if (!File.Children[index].State.IsOpen()) return DelegationResponse.Refusal(DelegationReasonCode.ChildClosed);
        var count = File.Children[index].FollowUpCount + 1;
        if (count > MaximumFollowUps) return DelegationResponse.Refusal(DelegationReasonCode.FollowUpLimit);
        var followUp = new FollowUp(Wire.Id(), id, text, DeliveryLane.Pending);
        try { Commit(File.SettingChild(index, File.Children[index] with { FollowUpCount = count }) with { FollowUps = File.FollowUps.Add(followUp) }, context); }
        catch (DelegationFileException ex) when (ex.Error == DelegationFileError.Full) { return DelegationResponse.Refusal(DelegationReasonCode.StoreFull); }
        catch (Exception ex) when (DelegationFileStore.IsSaveFailure(ex)) { return DelegationResponse.Failure("The delegation file could not be saved, so no follow-up was recorded."); }
        // Offered to the child's pane before the answer, which says where it went.
        await DeliverPending();
        return new(FollowUp: DelegationFollowUpInfo.Of(File.FollowUps.Find(saved => saved.Id == followUp.Id) ?? followUp, MaximumFollowUps - count));
    }

    /// <summary>Whether <paramref name="id"/> names one of <paramref name="caller"/>'s children.</summary>
    private bool IsChild(string id, DelegationCaller caller) => File.Children.Any(child => child.Id == id && child.ParentSessionId == caller.PaneId);
}
