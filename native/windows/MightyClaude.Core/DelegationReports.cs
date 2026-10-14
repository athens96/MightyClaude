using System.Security.Cryptography;
using System.Text;

namespace MightyClaude.Core;

/// <summary>How a run in a child's pane ended, as the host saw it (M/DelegationReports.swift:5-15).</summary>
public enum ChildRunEnd
{
    /// <summary>It finished normally.</summary>
    Finished,
    /// <summary>A human stopped it.</summary>
    Stopped,
    /// <summary>It ended with an error.</summary>
    Errored,
    /// <summary>Quitting the app killed it.</summary>
    Quit,
}

/// <summary>
/// A child's REPORT.md as a run left it (M/DelegationReports.swift:17-45): the start its copy keeps, its whole size and the
/// SHA-256 of all of it, byte for byte.
/// </summary>
public sealed record ChildReportFile(byte[] Start, long TotalBytes, string Digest)
{
    /// <summary>
    /// The report at <paramref name="path"/>, or null when there is none: no regular file there (a link is not followed), it
    /// could not be read, or the part a copy keeps holds no text. Reads the file in pieces, so a huge one is never loaded whole.
    /// </summary>
    public static ChildReportFile? Read(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.LinkTarget is not null) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            const int keep = DelegationFileStore.MaximumCopyBytes + 1;
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); using var start = new MemoryStream();
            var piece = new byte[1 << 16]; long total = 0; int count;
            while ((count = stream.Read(piece)) > 0)
            {
                hasher.AppendData(piece, 0, count); total += count;
                if (start.Length < keep) start.Write(piece, 0, (int)Math.Min(count, keep - start.Length));
            }
            var kept = start.ToArray();
            return DelegationCopy.Decode(kept).Any(character => !char.IsWhiteSpace(character) && character != '﻿') ? new(kept, total, Convert.ToHexStringLower(hasher.GetHashAndReset())) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException) { return null; }
    }
}

public sealed partial record ChildRecord
{
    /// <summary>
    /// The record brought to its run <paramref name="runId"/> going, as a new run (M/DelegationReports.swift:47-69): any new run
    /// sets running and clears reported, and a child's first run is the one its parent awaits. When the run going had a
    /// different id, its end was never reported; the new one carries on what the parent awaited. null, changing nothing, when
    /// this run's end is recorded already or the child takes no runs (failed, closed or discarded).
    /// </summary>
    public ChildRecord? NotingRun(string runId)
    {
        if (RunId == runId) return State is ChildState.Running or ChildState.Waiting ? this : null;
        ChildRecord next;
        switch (State)
        {
            case ChildState.Creating:
                if (Applying(ChildStateEvent.StartRun) is not { } first) return null;
                next = first with { AwaitedRunId = runId }; break;
            case ChildState.Running or ChildState.Waiting:
                next = State == ChildState.Waiting ? Applied(ChildStateEvent.AnswerPermission) : this;
                if (RunId is { } last && AwaitedRunId == last) next = next with { AwaitedRunId = runId };
                break;
            default:
                if (Applying(ChildStateEvent.StartRun) is not { } started) return null;
                next = started; break;
        }
        return next with { RunId = runId };
    }
}

/// <summary>
/// Report revisions (M/DelegationReports.swift:71-236): the host tells the coordinator each run that starts and ends in a
/// child's pane, in order, and the coordinator records what the run left for the parent. These run events take turns in the
/// order they came, so one never sees another half recorded. Notices are saved pending in the delegation file, and then
/// delivery offers them to the parent's pane.
/// </summary>
public sealed partial class DelegationCoordinator
{
    /// <summary>
    /// A run <paramref name="runId"/> started in the child <paramref name="id"/>'s pane: the child is running it, which clears
    /// reported, so a merge waits for the next report. A run already recorded, or a child that takes no runs, changes nothing.
    /// </summary>
    public Task ChildRunStarted(string id, string runId) => Isolated(async () =>
    {
        await runTurns.Take();
        try
        {
            if (File.Children.Find(child => child.Id == id) is not { } found || found.RunId == runId) return;
            var context = await PruneContext();
            // Nothing below suspends until the start is saved.
            var index = File.Children.FindIndex(child => child.Id == id);
            if (index < 0 || File.Children[index].RunId == runId || File.Children[index].NotingRun(runId) is not { } running) return;
            SaveOrKeep(File.SettingChild(index, running), context);
        }
        finally { runTurns.Pass(); }
    });

    /// <summary>
    /// The run <paramref name="runId"/> in the child <paramref name="id"/>'s pane ended. The first rule that applies records it:
    /// 1. Quitting the app killed it: the child is interrupted, with no notice.
    /// 2. REPORT.md holds text that differs from the current revision's: the next revision is recorded at the head the child has
    ///    checked out, with a copy of the report, and the parent gets one reported notice.
    /// 3. Otherwise the child has ended, and when the run was the one its parent awaits (delegate's first run, or the run a
    ///    follow-up went to) the parent gets one ended_without_report notice.
    /// Only the run going now is recorded, or the child's first run, whose start the host may not have reported yet. A second
    /// report of the same end, or the end of any other run, changes nothing. The notice sent, if any, as it was made: delivery
    /// then offers it to the parent's pane.
    /// </summary>
    public Task<Notice?> ChildRunEnded(string id, string runId, ChildRunEnd end) => Isolated(async () =>
    {
        var notice = await RecordRunEnd(id, runId, end);
        if (notice is not null) await DeliverPending();
        return notice;
    });

    private async Task<Notice?> RecordRunEnd(string id, string runId, ChildRunEnd end)
    {
        await runTurns.Take();
        try
        {
            if (File.Children.Find(record => record.Id == id) is not { } found || !MayEnd(found, runId)) return null;
            ChildReportFile? report = null; string? head = null;
            if (end != ChildRunEnd.Quit)
            {
                var path = ChildWorktree.ReportFileIn(found.WorktreePath);
                report = await Task.Run(() => ChildReportFile.Read(path));
                // A report whose head git cannot read counts as no new report; its change is still there for the next run's end.
                if (report is not null && report.Digest != found.ReportDigest) head = await ChildMerge.Head(found.WorktreePath);
            }
            var context = await PruneContext();
            // Nothing below suspends until the end is saved, so the same end reported twice finds it recorded.
            var next = File; var index = next.Children.FindIndex(record => record.Id == id);
            if (index < 0 || !MayEnd(next.Children[index], runId) || next.Children[index].ReportDigest != found.ReportDigest) return null;
            if (next.Children[index].NotingRun(runId) is not { } child) return null;
            // The run's end settles any permission request it was waiting on.
            if (child.State == ChildState.Waiting) child = child.Applied(ChildStateEvent.AnswerPermission);
            Notice? notice = null;
            if (end == ChildRunEnd.Quit) child = child.Applied(ChildStateEvent.QuitApp);
            else if (report is not null && head is not null && report.Digest != child.ReportDigest && child.RecordingReport(head) is { } reported)
            {
                child = reported with { ReportDigest = report.Digest };
                next = next.SettingCopy(DelegationCopy.Of(id, DelegationCopyKind.Report, child.ReportRevision, report.Start, report.TotalBytes));
                notice = new(Wire.Id(), id, child.ReportRevision, NoticeKind.Reported, DeliveryLane.Pending);
            }
            else
            {
                child = child.Applied(ChildStateEvent.EndWithoutReport);
                if (child.AwaitedRunId == runId) notice = new(Wire.Id(), id, child.ReportRevision, NoticeKind.EndedWithoutReport, DeliveryLane.Pending);
            }
            next = next.SettingChild(index, child);
            if (notice is not null) next = next with { Notices = next.Notices.Add(notice) };
            SaveOrKeep(next, context);
            return notice;
        }
        finally { runTurns.Pass(); }
    }

    /// <summary>
    /// The run <paramref name="runId"/> in the child <paramref name="id"/>'s pane carries its parent's work: the run a follow-up
    /// went to, as its delivery learns it. Its end without a new report sends the parent one ended_without_report notice. When
    /// the host has reported that end already, the notice is sent now; a run awaited already, or one that ended with a report
    /// or by quitting, sends nothing more. A follow-up that started a new run in the child's idle pane (reported, ended,
    /// interrupted or merged) has the child running it at once, which clears reported, whether or not the host has reported
    /// that start yet; any other run's start and end stay the host's to report. The notice sent, if any, which delivery then
    /// offers to the parent's pane.
    /// </summary>
    public Task<Notice?> AwaitRun(string runId, string id) => Isolated(async () =>
    {
        var notice = await RecordAwait(runId, id);
        if (notice is not null) await DeliverPending();
        return notice;
    });

    private async Task<Notice?> RecordAwait(string runId, string id)
    {
        await runTurns.Take();
        try
        {
            if (File.Children.Find(record => record.Id == id) is not { } found || found.AwaitedRunId == runId) return null;
            var context = await PruneContext();
            // Nothing below suspends until the await is saved.
            var next = File; var index = next.Children.FindIndex(record => record.Id == id);
            if (index < 0 || next.Children[index].AwaitedRunId == runId) return null;
            var child = next.Children[index]; Notice? notice = null;
            if (child.RunId == runId && child.State == ChildState.Ended)
            {
                notice = new(Wire.Id(), id, child.ReportRevision, NoticeKind.EndedWithoutReport, DeliveryLane.Pending);
                next = next with { Notices = next.Notices.Add(notice) };
            }
            else if (child.RunId != runId && IsIdleBetweenRuns(child.State)) child = child.NotingRun(runId) ?? child;
            SaveOrKeep(next.SettingChild(index, child with { AwaitedRunId = runId }), context);
            return notice;
        }
        finally { runTurns.Pass(); }
    }

    /// <summary>
    /// At launch, before any pane runs again: every child still running or waiting had its run killed when the app quit. Each
    /// becomes interrupted, with no notice, and comes back stopped. The ids of those children.
    /// </summary>
    public Task<IReadOnlyList<string>> InterruptRunsKilledByQuit() => Isolated<IReadOnlyList<string>>(async () =>
    {
        await runTurns.Take();
        try
        {
            var context = await PruneContext();
            var next = File; var ids = new List<string>();
            for (var index = 0; index < next.Children.Count; index++)
            {
                var child = next.Children[index];
                if (child.State is not (ChildState.Running or ChildState.Waiting)) continue;
                if (child.State == ChildState.Waiting) child = child.Applied(ChildStateEvent.AnswerPermission);
                next = next.SettingChild(index, child.Applied(ChildStateEvent.QuitApp)); ids.Add(child.Id);
            }
            if (ids.Count > 0) SaveOrKeep(next, context);
            return ids;
        }
        finally { runTurns.Pass(); }
    });

    /// <summary>Whether a child in <paramref name="state"/> is idle in an open pane after a run, so a new run there moves it to running.</summary>
    public static bool IsIdleBetweenRuns(ChildState state) => state is ChildState.Reported or ChildState.Ended or ChildState.Interrupted or ChildState.Merged;

    /// <summary>Whether the end of the run <paramref name="runId"/> may be recorded for <paramref name="child"/>: it is the run going now, or the child's first run.</summary>
    public static bool MayEnd(ChildRecord child, string runId) => child.RunId == runId ? child.State is ChildState.Running or ChildState.Waiting : child.State == ChildState.Creating;
}
