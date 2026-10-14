namespace MightyClaude.Core;

/// <summary>What pruning the delegation file needs to know from outside it (M/DelegationPruning.swift:3-15).</summary>
/// <param name="OpenPaneIds">Session ids of every pane open now. A parent missing here is closed.</param>
/// <param name="CanUndo">Whether a merge can still be undone: its recorded parent branch is checked out with its head at the merged
/// commit. Such a merge record, and its child's record (the card undo starts from), are never pruned.</param>
public sealed record DelegationPruneContext(IReadOnlySet<string> OpenPaneIds, Func<MergeRecord, bool> CanUndo);

/// <summary>The answer to delegate's request for room for one more child (M/DelegationPruning.swift:17-25).</summary>
public abstract record DelegationAdmission
{
    /// <summary>The file with the child's record (and its TASK copy) added, pruned so that the child's full report copy still fits. Saving it is the caller's step.</summary>
    public sealed record Admitted(DelegationFile File) : DelegationAdmission;
    /// <summary>No room for the child's record and a full report copy even after pruning: always store_full. Nothing changed.</summary>
    public sealed record Refused(DelegationReasonCode Reason) : DelegationAdmission;
}

public sealed partial record DelegationFile
{
    /// <summary>The room kept for one more child's record, beside its report copy.</summary>
    public const int RecordAllowance = 4 * 1024;
    private static readonly Lazy<int> ChildRoom = new(() => RecordAllowance + ReportCopyRoom(new string('x', 36)));

    /// <summary>The room the largest report copy of <paramref name="childId"/> takes in the file: cut at 64 KiB behind the marker, at the longest revision and file size.</summary>
    public static int ReportCopyRoom(string childId)
    {
        var longer = new byte[DelegationFileStore.MaximumCopyBytes + 1]; Array.Fill(longer, (byte)'x');
        return ElementBytes(DelegationCopy.Of(childId, DelegationCopyKind.Report, long.MaxValue, longer, long.MaxValue));
    }

    /// <summary>The room one more child takes: a record allowance and a full report copy.</summary>
    public static int NewChildRoom() => ChildRoom.Value;

    /// <summary>
    /// This file pruned until it, plus <paramref name="headroom"/> more bytes, fits under the 4 MiB cap, or null when it does not
    /// fit even after everything allowed is pruned (M/DelegationPruning.swift:41-78). A file that already fits comes back
    /// unchanged. Pruning goes oldest first (records keep the order they were made in) and stops as soon as the file fits:
    /// first the report copies of closed records (closed or discarded), then closed records that are fully delivered, each with
    /// its notices, follow-ups, merge records and copies. A notice held for a child whose parent pane is closed counts as
    /// delivered here. Never pruned: open children, failed children (a human discards what a failed start left from its card),
    /// held items of open panes, and merge records that can still be undone, with their child's record.
    /// </summary>
    public DelegationFile? Pruned(DelegationPruneContext context, long headroom = 0)
    {
        const long cap = DelegationFileStore.MaximumFileBytes;
        var file = this; long size = DelegationFileStore.Encode(file).Length;
        if (size + headroom <= cap) return file;
        // After a removal `size` may undercount by a separator per emptied array, so a fit is confirmed with the exact size before stopping.
        bool Fits()
        {
            if (size + headroom > cap) return false;
            size = DelegationFileStore.Encode(file).Length;
            return size + headroom <= cap;
        }
        var closed = Children.Where(child => child.State is ChildState.Closed or ChildState.Discarded).ToList();
        foreach (var child in closed)
        {
            var index = file.Copies.FindIndex(copy => copy.ChildId == child.Id && copy.Kind == DelegationCopyKind.Report);
            if (index < 0) continue;
            size -= ElementBytes(file.Copies[index]); file = file with { Copies = file.Copies.RemoveAt(index) };
            if (Fits()) return file;
        }
        foreach (var child in closed.Where(child => IsFullyDelivered(child, context) && !Merges.Any(merge => merge.ChildId == child.Id && context.CanUndo(merge))))
        {
            size -= file.EverythingBytes(child.Id); file = file.RemovingEverything(child.Id);
            if (Fits()) return file;
        }
        return null;
    }

    /// <summary>
    /// Room for one more child: the file with <paramref name="child"/>'s record (and its TASK copy) added and pruned until the
    /// child's full report copy still fits, or store_full when it does not fit even after pruning.
    /// </summary>
    public DelegationAdmission Admitting(ChildRecord child, DelegationPruneContext context, DelegationCopy? task = null)
    {
        var file = this with { Children = Children.Add(child) };
        if (task is not null) file = file.SettingCopy(task);
        return file.Pruned(context, ReportCopyRoom(child.Id)) is { } pruned ? new DelegationAdmission.Admitted(pruned) : new DelegationAdmission.Refused(DelegationReasonCode.StoreFull);
    }

    /// <summary>Every notice from the child and follow-up to it is delivered; a notice held for a child whose parent pane is closed counts as delivered.</summary>
    private bool IsFullyDelivered(ChildRecord child, DelegationPruneContext context)
    {
        var parentClosed = !context.OpenPaneIds.Contains(child.ParentSessionId);
        return Notices.All(notice => notice.ChildId != child.Id || notice.Lane == DeliveryLane.Delivered || notice.Lane == DeliveryLane.Held && parentClosed)
            && FollowUps.All(followUp => followUp.ChildId != child.Id || followUp.Lane == DeliveryLane.Delivered);
    }

    /// <summary>The bytes the child's record and everything kept for it take.</summary>
    private int EverythingBytes(string childId) => Children.Where(child => child.Id == childId).Sum(child => ElementBytes(child))
        + Notices.Where(notice => notice.ChildId == childId).Sum(notice => ElementBytes(notice)) + FollowUps.Where(followUp => followUp.ChildId == childId).Sum(followUp => ElementBytes(followUp))
        + Merges.Where(merge => merge.ChildId == childId).Sum(merge => ElementBytes(merge)) + Copies.Where(copy => copy.ChildId == childId).Sum(copy => ElementBytes(copy));

    /// <summary>This file without the child's record and everything kept for it.</summary>
    private DelegationFile RemovingEverything(string childId) => this with
    {
        Children = Children.RemoveAll(child => child.Id == childId), Notices = Notices.RemoveAll(notice => notice.ChildId == childId),
        FollowUps = FollowUps.RemoveAll(followUp => followUp.ChildId == childId), Merges = Merges.RemoveAll(merge => merge.ChildId == childId),
        Copies = Copies.RemoveAll(copy => copy.ChildId == childId),
    };

    /// <summary>The bytes one array element adds to the encoded file, its separator included.</summary>
    public static int ElementBytes<T>(T item) => DelegationFileStore.Encode(item).Length + 1;
}
