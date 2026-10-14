using System.Globalization;
using System.Text.Json.Serialization;

namespace MightyClaude.Core;

[JsonConverter(typeof(DelegationNameConverter<DelegationDeliveryKind>))]
public enum DelegationDeliveryKind { Notice, FollowUp }

/// <summary>One notice or follow-up as delivery hands it to a pane (M/DelegationDelivery.swift:3-17).</summary>
/// <param name="Id">The notice's or follow-up's own id; its text names it too.</param>
/// <param name="ChildId">The child the notice is about, or the follow-up is for.</param>
/// <param name="SessionId">The pane it goes to: the child's parent for a notice, the child itself for a follow-up.</param>
/// <param name="Text">What the pane's run gets.</param>
public sealed record DelegationDeliveryItem(string Id, DelegationDeliveryKind Kind, string ChildId, string SessionId, string Text);

/// <summary>What one release of a pane's held items did (M/DelegationDelivery.swift:19-29).</summary>
public abstract record DelegationRelease
{
    /// <summary>Nothing is held for the pane: the caller sends or runs next as it would anyway.</summary>
    public sealed record NothingHeld : DelegationRelease;
    /// <summary>Every item held for the pane went to it, oldest first, in the one run <paramref name="RunId"/>.</summary>
    public sealed record Released(string RunId, IReadOnlyList<string> ItemIds) : DelegationRelease;
    /// <summary>No run started (or the pane is closed), so they all stay held.</summary>
    public sealed record NotStarted : DelegationRelease;
}

/// <summary>
/// A pane's queued list with delegation's held items in it (M/DelegationDelivery.swift:31-52). The items held for the pane come
/// first, oldest first, as they run first; the human's queued requests follow. Held rows cannot be removed, do not count toward
/// the queue's 16-item cap, and keep run next on offer while there are any. The phone gets them in its existing <c>queued</c> item shape.
/// </summary>
public static class DelegationQueueRows
{
    /// <summary>The pane's rows in the phone's <c>queued</c> array: held first, then the human's requests.</summary>
    public static IReadOnlyList<MobileQueuedItem> MobileItems(IEnumerable<DelegationDeliveryItem> held, IEnumerable<QueuedComposerInput> queued) =>
        [.. held.Select(item => new MobileQueuedItem(item.Id, item.Text)), .. queued.Select(item => new MobileQueuedItem(item.Id, item.Text))];

    /// <summary><c>held_not_removable</c> when <paramref name="itemId"/> is one of the pane's held rows, and the remove route changes nothing; null when it may go on.</summary>
    public static DelegationReasonCode? RemoveRefusal(string itemId, IEnumerable<DelegationDeliveryItem> held) => held.Any(item => item.Id == itemId) ? DelegationReasonCode.HeldNotRemovable : null;

    /// <summary>Whether the queued list offers run next: whenever items are held, otherwise only while the pane is not busy.</summary>
    public static bool OffersRunNext(bool busy, int held) => held > 0 || !busy;
}

public sealed partial record DelegationFile
{
    /// <summary>
    /// This file with every notice and follow-up still pending held, or null when none was. At launch nothing is delivered on
    /// its own: what the last launch left pending waits for the human's next send or run next.
    /// </summary>
    public DelegationFile? HoldingPending() => Notices.Any(notice => notice.Lane == DeliveryLane.Pending) || FollowUps.Any(followUp => followUp.Lane == DeliveryLane.Pending)
        ? this with
        {
            Notices = Notices.ConvertAll(notice => notice.Lane == DeliveryLane.Pending ? notice with { Lane = DeliveryLane.Held } : notice),
            FollowUps = FollowUps.ConvertAll(followUp => followUp.Lane == DeliveryLane.Pending ? followUp with { Lane = DeliveryLane.Held } : followUp),
        }
        : null;

    /// <summary>The items in <paramref name="lane"/>, notices first, each kind in the order its items were made.</summary>
    public IEnumerable<DelegationDeliveryItem> Items(DeliveryLane lane) =>
        Notices.Where(notice => notice.Lane == lane).Select(DeliveryItem).Concat(FollowUps.Where(followUp => followUp.Lane == lane).Select(DeliveryItem)).OfType<DelegationDeliveryItem>();

    /// <summary>
    /// The items held for each pane, oldest first: the rows its queued list shows. A pane is a parent with notices or a child
    /// with follow-ups, never both, so each list is in the order its items were made.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<DelegationDeliveryItem>> HeldItemsByPane() =>
        Items(DeliveryLane.Held).GroupBy(item => item.SessionId).ToDictionary(pane => pane.Key, pane => (IReadOnlyList<DelegationDeliveryItem>)pane.ToList());

    /// <summary>The held notices from the child <paramref name="childId"/>, oldest first.</summary>
    public IReadOnlyList<Notice> HeldNotices(string childId) => Notices.Where(notice => notice.ChildId == childId && notice.Lane == DeliveryLane.Held).ToList();

    /// <summary>The notice as its child's parent pane gets it; null without the child.</summary>
    public DelegationDeliveryItem? DeliveryItem(Notice notice) => Children.Find(child => child.Id == notice.ChildId) is { } child
        ? new(notice.Id, DelegationDeliveryKind.Notice, notice.ChildId, child.ParentSessionId, DelegationCoordinator.Text(notice)) : null;

    /// <summary>The follow-up as its child's pane gets it; null without the child.</summary>
    public DelegationDeliveryItem? DeliveryItem(FollowUp followUp) => Children.Find(child => child.Id == followUp.ChildId) is { } child
        ? new(followUp.Id, DelegationDeliveryKind.FollowUp, followUp.ChildId, followUp.ChildId, DelegationCoordinator.Text(followUp, ChildWorktree.ReportFileIn(child.WorktreePath))) : null;

    /// <summary>This file with the lane and receipt of each of <paramref name="items"/> changed.</summary>
    internal DelegationFile Updating(IEnumerable<DelegationDeliveryItem> items, Func<DeliveryLane, DeliveryReceipt?, (DeliveryLane Lane, DeliveryReceipt? Receipt)> change)
    {
        var file = this;
        foreach (var item in items)
        {
            if (item.Kind == DelegationDeliveryKind.Notice)
            {
                var index = file.Notices.FindIndex(notice => notice.Id == item.Id);
                if (index < 0) continue;
                var (lane, receipt) = change(file.Notices[index].Lane, file.Notices[index].Receipt);
                file = file with { Notices = file.Notices.SetItem(index, file.Notices[index] with { Lane = lane, Receipt = receipt }) };
            }
            else
            {
                var index = file.FollowUps.FindIndex(followUp => followUp.Id == item.Id);
                if (index < 0) continue;
                var (lane, receipt) = change(file.FollowUps[index].Lane, file.FollowUps[index].Receipt);
                file = file with { FollowUps = file.FollowUps.SetItem(index, file.FollowUps[index] with { Lane = lane, Receipt = receipt }) };
            }
        }
        return file;
    }
}

/// <summary>
/// Delivery (M/DelegationDelivery.swift:92-314): each notice goes to its child's parent pane and each follow-up to its child's
/// pane, exactly once. A new item is offered to its pane at once, by the first rule that applies:
/// 1. Items are held for the pane already: the item is held behind them, whatever the pane is doing, so it never reaches the pane before them.
/// 2. The pane runs Claude now: the item is steered into that run.
/// 3. The pane is idle after a run that finished normally since this launch, and a run can start in it now: the item starts its next run.
/// 4. Otherwise (the pane stopped, errored, has had no run since this launch, cannot start a run now, or is closed) the item is held.
/// Only the human's next send or run next releases held items, all of the pane's together, oldest first, in one run. A closed
/// parent's held notices stay on its children's cards. Before a pane gets an item, the item is saved delivered with its
/// receipt, so it is never handed over twice; should the pane not take it after all, it goes back as it was. Should the pane
/// not take a new item twice running, the item stays pending and is offered again with the next one, or when the app sees one
/// of its panes' runs start or end; until then the pane's newer items wait behind it, so none overtakes it. What is still
/// pending at launch is held.
/// </summary>
public sealed partial class DelegationCoordinator
{
    /// <summary>How often one new item is offered to its pane in a row: a run that ends just as an item is steered into it gets it as its next run.</summary>
    public const int DeliveryOffers = 2;

    /// <summary>The items held for the pane <paramref name="sessionId"/>, oldest first: rows its queued list shows, which cannot be removed.</summary>
    public IReadOnlyList<DelegationDeliveryItem> HeldItems(string sessionId) => Items(sessionId, DeliveryLane.Held);

    /// <summary>
    /// The held notices from the child <paramref name="childId"/>. When its parent pane is closed nothing releases them: they
    /// stay on the child's card, which counts them (<see cref="DelegationChildRow.HeldNotices"/>).
    /// </summary>
    public IReadOnlyList<Notice> HeldNotices(string childId) => File.HeldNotices(childId);

    /// <summary>
    /// The human's send of <paramref name="text"/> in the pane <paramref name="sessionId"/>. When items are held for it, all of
    /// them go with the send, oldest first and ahead of <paramref name="text"/>, in the one run the host starts, or
    /// <paramref name="start"/> when given: the app's send starts the run with the human's attachments too, gets the run's
    /// whole input and answers the new run's id, or null when none started. Otherwise nothing changes and the caller sends
    /// <paramref name="text"/> as it would anyway.
    /// </summary>
    public Task<DelegationRelease> Send(string text, string sessionId, Func<string, Task<string?>>? start = null) => Isolated(() => Release(sessionId, text, start));

    /// <summary>
    /// Run next in the pane <paramref name="sessionId"/>. When items are held for it, all of them and nothing else go to the
    /// pane, oldest first, in one run: steered into its running Claude run, or as its next run. The human's queued rows follow
    /// in later runs. Otherwise nothing changes and the caller runs its next queued row.
    /// </summary>
    public Task<DelegationRelease> RunNext(string sessionId) => Isolated(() => Release(sessionId, null, null));

    /// <summary>
    /// Offers every pending item to its pane, oldest first. The app calls it whenever one of its panes' runs starts or ends, so
    /// an item that a run still starting or already ending could not take is steered into the run once it is up, or goes by the
    /// pane's next run or is held once it ended. A call while a pass is going asks that pass for one more and returns at once.
    /// An item its pane still did not take keeps the pane's newer items waiting behind it for the rest of the pass, so none
    /// overtakes it; the next pass offers them again, oldest first.
    /// </summary>
    public Task DeliverPending() => Isolated(async () =>
    {
        wantsDelivery = true;
        if (isDelivering) return;
        isDelivering = true;
        try
        {
            while (wantsDelivery)
            {
                wantsDelivery = false;
                var pending = File.Items(DeliveryLane.Pending).Select(item => (item.Id, item.Kind)).ToList();
                var waiting = new HashSet<string>();
                foreach (var (id, kind) in pending)
                {
                    if (LookUp(id, kind, DeliveryLane.Pending) is not { } item || waiting.Contains(item.SessionId)) continue;
                    await Offer(id, kind);
                    if (LookUp(id, kind, DeliveryLane.Pending) is not null) waiting.Add(item.SessionId);
                }
            }
        }
        // Should the host fail, the pass ends, and the next one offers what is still pending.
        finally { isDelivering = false; }
    });

    /// <summary>Offers the pending item <paramref name="id"/> to its pane as the pane is now.</summary>
    private async Task Offer(string id, DelegationDeliveryKind kind)
    {
        for (var offer = 0; offer < DeliveryOffers; offer++)
        {
            if (LookUp(id, kind, DeliveryLane.Pending) is not { } found) return;
            var pane = await Host(() => host.PaneState(found.SessionId));
            var context = await PruneContext();
            // Nothing below suspends until the item's lane is saved, so nothing else moves it meanwhile.
            if (LookUp(id, kind, DeliveryLane.Pending) is not { } item) return;
            // Behind items already held for the pane it is held too, so the human's next send or run next releases them all, oldest first.
            if (Items(item.SessionId, DeliveryLane.Held).Count > 0 || AutomaticRoute(pane) is not { } route)
            {
                KeepMoving([item], DeliveryLane.Held, null, context);
                return;
            }
            // A card's merge or undo is changing the pane's checkout: no run starts there now, and the item is offered again once it is over.
            if (route == DeliveryRoute.Queue && cardPanes.Contains(item.SessionId)) return;
            var receipt = new DeliveryReceipt(Now(), route, route == DeliveryRoute.Steer ? pane?.RunId ?? "" : "");
            if (!SaveMoving([item], DeliveryLane.Delivered, receipt, context)) return;
            if (await Host(() => host.Deliver(item.Text, item.SessionId, route)) is { } runId)
            {
                await Received([item], runId, context);
                return;
            }
            KeepMoving([item], DeliveryLane.Pending, null, context);
        }
    }

    /// <summary>
    /// All of <paramref name="sessionId"/>'s held items, oldest first, ahead of <paramref name="text"/> (none for run next) in
    /// one run, which <paramref name="start"/> starts (the host when null). Run next steers them into the pane's running Claude run instead.
    /// </summary>
    private async Task<DelegationRelease> Release(string sessionId, string? text, Func<string, Task<string?>>? start)
    {
        if (Items(sessionId, DeliveryLane.Held).Count == 0) return new DelegationRelease.NothingHeld();
        if (await Host(() => host.PaneState(sessionId)) is not { } pane) return new DelegationRelease.NotStarted();
        var steers = text is null && pane.Activity == DelegationPaneActivity.Running && DelegationSwitch.IsClaudePane(pane.Kind, pane.Provider);
        var context = await PruneContext();
        // Nothing below suspends until the release is saved, so two sends at once never take the same item.
        var held = Items(sessionId, DeliveryLane.Held);
        if (held.Count == 0) return new DelegationRelease.NothingHeld();
        var receipt = new DeliveryReceipt(Now(), steers ? DeliveryRoute.Steer : DeliveryRoute.Queue, steers ? pane.RunId ?? "" : "");
        if (!SaveMoving(held, DeliveryLane.Delivered, receipt, context)) return new DelegationRelease.NotStarted();
        var input = string.Join("\n\n", held.Select(item => item.Text).Concat(string.IsNullOrEmpty(text) ? [] : [text]));
        var runId = steers ? await Host(() => host.Deliver(input, sessionId, DeliveryRoute.Steer)) : start is not null ? await Host(() => start(input)) : await Host(() => host.StartRun(sessionId, input));
        if (runId is null)
        {
            KeepMoving(held, DeliveryLane.Held, null, context);
            return new DelegationRelease.NotStarted();
        }
        await Received(held, runId, context);
        return new DelegationRelease.Released(runId, held.Select(item => item.Id).ToList());
    }

    /// <summary>The run <paramref name="runId"/> got <paramref name="items"/>: their receipts name it, and a follow-up's run is the one its child's parent now awaits.</summary>
    private async Task Received(IReadOnlyList<DelegationDeliveryItem> items, string runId, DelegationPruneContext context)
    {
        SaveOrKeep(File.Updating(items, (lane, receipt) => (lane, lane == DeliveryLane.Delivered && receipt is not null ? receipt with { RunId = runId } : receipt)), context);
        foreach (var childId in items.Where(item => item.Kind == DelegationDeliveryKind.FollowUp).Select(item => item.ChildId).Distinct().ToList()) await AwaitRun(runId, childId);
    }

    /// <summary>
    /// How an item reaches <paramref name="pane"/> with no human: steered into its running Claude run, or as the next run of a
    /// pane idle after a run that finished normally since this launch. null when it is held.
    /// </summary>
    public static DeliveryRoute? AutomaticRoute(DelegationPaneState? pane) => pane is null ? null : pane.Activity switch
    {
        DelegationPaneActivity.Running => DelegationSwitch.IsClaudePane(pane.Kind, pane.Provider) ? DeliveryRoute.Steer : null,
        DelegationPaneActivity.Finished => DeliveryRoute.Queue,
        _ => null,
    };

    /// <summary>A notice as its parent's run reads it: a pointer naming the notice and the child. The report itself is read through child_status.</summary>
    public static string Text(Notice notice)
    {
        var head = $"[Mighty Claude notice {notice.Id}]";
        return notice.Kind switch
        {
            NoticeKind.Reported => $"{head} Your child {notice.ChildId} reported revision {notice.ReportRevision}. Read the report with child_status; merge it with merge when it is right.",
            NoticeKind.EndedWithoutReport => $"{head} Your child {notice.ChildId}'s run ended without a new report; its report revision is still {notice.ReportRevision}. Call child_status to see where it stands.",
            _ => $"{head} Your child {notice.ChildId} could not be started. Only a human can discard it, from its card.",
        };
    }

    /// <summary>A follow-up as its child's run reads it.</summary>
    public static string Text(FollowUp followUp, string reportFile) =>
        $"[Mighty Claude follow-up {followUp.Id}] Your parent sent you this follow-up:\n\n{followUp.Text}\n\nWhen you are done, commit your work on your branch and write your updated report to {reportFile}.";

    /// <summary>The items for the pane <paramref name="sessionId"/> in <paramref name="lane"/>, oldest first.</summary>
    private IReadOnlyList<DelegationDeliveryItem> Items(string sessionId, DeliveryLane lane) => File.Items(lane).Where(item => item.SessionId == sessionId).ToList();

    private DelegationDeliveryItem? LookUp(string id, DelegationDeliveryKind kind, DeliveryLane lane) => kind == DelegationDeliveryKind.Notice
        ? File.Notices.Find(notice => notice.Id == id && notice.Lane == lane) is { } notice ? File.DeliveryItem(notice) : null
        : File.FollowUps.Find(followUp => followUp.Id == id && followUp.Lane == lane) is { } followUp ? File.DeliveryItem(followUp) : null;

    /// <summary>Saves <paramref name="items"/> moved to <paramref name="lane"/>; false, changing nothing, when the disk refuses it.</summary>
    private bool SaveMoving(IReadOnlyList<DelegationDeliveryItem> items, DeliveryLane lane, DeliveryReceipt? receipt, DelegationPruneContext context)
    {
        try { Commit(File.Updating(items, (_, _) => (lane, receipt)), context); return true; }
        catch (Exception ex) when (DelegationFileStore.IsSaveFailure(ex)) { return false; }
    }

    /// <summary>Moves <paramref name="items"/> to <paramref name="lane"/>; should the disk refuse it, the app goes on with the move and the next save writes it.</summary>
    private void KeepMoving(IReadOnlyList<DelegationDeliveryItem> items, DeliveryLane lane, DeliveryReceipt? receipt, DelegationPruneContext context) =>
        SaveOrKeep(File.Updating(items, (_, _) => (lane, receipt)), context);

    public static string Now() => DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
