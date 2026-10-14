using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace MightyClaude.Core;

/// <summary>What can happen to a delegated child (M/DelegationModel.swift:4-30). <see cref="ChildStates.After"/> says which state each event leads to, or refuses it.</summary>
public enum ChildStateEvent
{
    /// <summary>A run starts in the child's pane: its first run, or any new one (a follow-up or the human's own send).</summary>
    StartRun,
    /// <summary>The child could not be started.</summary>
    FailStart,
    /// <summary>The child's run asks for a permission that only a human can answer.</summary>
    AskPermission,
    /// <summary>That permission request was answered.</summary>
    AnswerPermission,
    /// <summary>The run ended with a changed REPORT.md.</summary>
    EndWithReport,
    /// <summary>The run finished, stopped or errored with no new report.</summary>
    EndWithoutReport,
    /// <summary>Quitting the app killed the run.</summary>
    QuitApp,
    /// <summary>A merge took the child's reported head.</summary>
    Merge,
    /// <summary>A human undid that merge from the card.</summary>
    UndoMerge,
    /// <summary>The child's pane closed.</summary>
    ClosePane,
    /// <summary>A human discarded the child from its card.</summary>
    Discard,
}

/// <summary>
/// Where a delegated child is (M/DelegationModel.swift:32-74). The events allow exactly these moves:
/// creating → running (its first run starts) | failed (the start failed); running ⇄ waiting (a permission request only a
/// human can answer); running → reported (the run ended with a changed REPORT.md) | ended (no new report) | interrupted
/// (quitting the app killed it); reported, ended, interrupted, merged → running (any new run in the open, idle pane, which
/// clears reported); reported → merged; merged → reported (undo while the pane is open; undo on a closed child leaves it
/// closed); every open state → closed; every state but discarded → discarded. Every other event is refused. Discarded is final.
/// </summary>
[JsonConverter(typeof(DelegationNameConverter<ChildState>))]
public enum ChildState { Creating, Running, Waiting, Reported, Ended, Interrupted, Merged, Failed, Closed, Discarded }

public static class ChildStates
{
    /// <summary>Open children count toward the cap: every state except failed, closed and discarded.</summary>
    public static bool IsOpen(this ChildState state) => state is not (ChildState.Failed or ChildState.Closed or ChildState.Discarded);

    /// <summary>The state <paramref name="change"/> leads to, or null when it is refused here.</summary>
    public static ChildState? After(this ChildState state, ChildStateEvent change) => (state, change) switch
    {
        (ChildState.Creating or ChildState.Reported or ChildState.Ended or ChildState.Interrupted or ChildState.Merged, ChildStateEvent.StartRun) => ChildState.Running,
        (ChildState.Creating, ChildStateEvent.FailStart) => ChildState.Failed,
        (ChildState.Running, ChildStateEvent.AskPermission) => ChildState.Waiting,
        (ChildState.Waiting, ChildStateEvent.AnswerPermission) => ChildState.Running,
        (ChildState.Running, ChildStateEvent.EndWithReport) => ChildState.Reported,
        (ChildState.Running, ChildStateEvent.EndWithoutReport) => ChildState.Ended,
        (ChildState.Running, ChildStateEvent.QuitApp) => ChildState.Interrupted,
        (ChildState.Reported, ChildStateEvent.Merge) => ChildState.Merged,
        (ChildState.Merged, ChildStateEvent.UndoMerge) => ChildState.Reported,
        (ChildState.Closed, ChildStateEvent.UndoMerge) => ChildState.Closed,
        (_, ChildStateEvent.ClosePane) when state.IsOpen() => ChildState.Closed,
        (_, ChildStateEvent.Discard) when state != ChildState.Discarded => ChildState.Discarded,
        _ => null,
    };

    /// <summary>Whether some event moves this state to <paramref name="next"/>, a different state.</summary>
    public static bool CanMove(this ChildState state, ChildState next) => next != state && Enum.GetValues<ChildStateEvent>().Any(change => state.After(change) == next);
}

/// <summary>One delegated child pane, as the delegation file records it (M/DelegationModel.swift:76-135).</summary>
/// <param name="Id">The child pane's session id.</param>
/// <param name="Branch">Always <c>mighty/&lt;session id&gt;</c>, flat.</param>
/// <param name="ParentBranch">The parent's branch when the child was made; <paramref name="BaseCommit"/> is its head then.</param>
/// <param name="StartingMode">The permission mode the parent asked for in delegate, written once.</param>
/// <param name="ReportRevision">0 until the first report; each report revision is one higher.</param>
/// <param name="ParentCheckout">The top folder of the parent's checkout when the child was made: the parent's side, where the child's
/// branch lives and where its cleanup and discard run git, even after the parent pane closed. null in a record kept before it was stored.</param>
/// <param name="ClosedHead">The head of the child's branch when a human closed its merged pane, stored with its REPORT and TASK copies before any cleanup.</param>
/// <param name="RunId">The run going in the child's pane, or its last one, as the host named it. null before the first.</param>
/// <param name="AwaitedRunId">The run that carries the parent's work: the child's first run, which delegate started, or the run a
/// follow-up went to. It stays named after that run ends, so its end sends the parent one notice, never two.</param>
/// <param name="ReportDigest">The SHA-256 of the whole REPORT.md at the current revision, so only a changed report makes the next one.</param>
public sealed partial record ChildRecord(string Id, string ParentSessionId, string WorktreePath, string Branch, string ParentBranch, string BaseCommit, string StartingMode, string RequestKey,
    int FollowUpCount, int ReportRevision, ChildState State, string? ReportHead = null, string? ParentCheckout = null, string? ClosedHead = null, string? RunId = null,
    string? AwaitedRunId = null, string? ReportDigest = null)
{
    public static ChildRecord Make(string id, string parentSessionId, string worktreePath, string parentBranch, string baseCommit, string startingMode, string requestKey,
        int followUpCount = 0, int reportRevision = 0, string? reportHead = null, ChildState state = ChildState.Creating, string? parentCheckout = null) =>
        new(id, parentSessionId, worktreePath, BranchName(id), parentBranch, baseCommit, startingMode, requestKey, followUpCount, reportRevision, state, reportHead, parentCheckout);

    public static string BranchName(string sessionId) => "mighty/" + sessionId;

    /// <summary>The child moved by <paramref name="change"/>, or null when the event is refused and nothing changes.</summary>
    public ChildRecord? Applying(ChildStateEvent change) => State.After(change) is { } next ? this with { State = next } : null;

    /// <summary>The child moved by <paramref name="change"/>, or as it is when the event is refused.</summary>
    public ChildRecord Applied(ChildStateEvent change) => Applying(change) ?? this;

    /// <summary>The run ended with a changed REPORT.md at <paramref name="head"/>: the child is reported with the next revision. null, changing nothing, unless it was running.</summary>
    public ChildRecord? RecordingReport(string head) => Applying(ChildStateEvent.EndWithReport) is { } reported ? reported with { ReportRevision = ReportRevision + 1, ReportHead = head } : null;
}

/// <summary>Where a notice or follow-up waits: pending (not yet sent), held (sent only by run next or the human's next send) or delivered (exactly once).</summary>
[JsonConverter(typeof(DelegationNameConverter<DeliveryLane>))]
public enum DeliveryLane { Pending, Held, Delivered }

/// <summary>How an item reached its pane: steered into a running Claude pane, or queued as the next run.</summary>
[JsonConverter(typeof(DelegationNameConverter<DeliveryRoute>))]
public enum DeliveryRoute { Steer, Queue }

/// <param name="Time">ISO 8601.</param>
/// <param name="RunId">The run that received the item.</param>
public sealed record DeliveryReceipt(string Time, DeliveryRoute Route, string RunId);

[JsonConverter(typeof(DelegationNameConverter<NoticeKind>))]
public enum NoticeKind { Reported, EndedWithoutReport, FailedToStart }

/// <summary>A pointer to one child event for the parent (M/DelegationModel.swift:155-174). The report body is read through child_status, never carried here.</summary>
public sealed record Notice(string Id, string ChildId, int ReportRevision, NoticeKind Kind, DeliveryLane Lane, DeliveryReceipt? Receipt = null);

/// <summary>A parent's instruction to one of its children (M/DelegationModel.swift:176-187).</summary>
public sealed record FollowUp(string Id, string ChildId, string Text, DeliveryLane Lane, DeliveryReceipt? Receipt = null);

[JsonConverter(typeof(DelegationNameConverter<MergeKind>))]
public enum MergeKind { ToolFastForward, CardFastForward, CardMergeCommit }

/// <summary>Written on every merge; undo reads it (M/DelegationModel.swift:189-207).</summary>
public sealed record MergeRecord(string ChildId, MergeKind Kind, string ParentBranch, string PreMergeCommit, string MergedCommit, string ChildHead);

/// <summary>
/// The idempotency key of one delegate call (M/DelegationModel.swift:209-224). The server derives it from the calling pane's
/// session id, that pane's current run id, the task text and the starting mode, with no extra argument, so a repeat within the
/// same parent run gets the same child. Each part is length-prefixed before hashing, so no two different part lists share a key.
/// </summary>
public static class DelegationRequestKey
{
    public static string Make(string parentSessionId, string parentRunId, string task, string startingMode)
    {
        using var bytes = new MemoryStream(); Span<byte> length = stackalloc byte[8];
        foreach (var part in new[] { parentSessionId, parentRunId, task, startingMode })
        {
            var utf8 = Encoding.UTF8.GetBytes(part);
            BinaryPrimitives.WriteUInt64BigEndian(length, (ulong)utf8.Length); bytes.Write(length); bytes.Write(utf8);
        }
        return Convert.ToHexStringLower(SHA256.HashData(bytes.ToArray()));
    }
}
