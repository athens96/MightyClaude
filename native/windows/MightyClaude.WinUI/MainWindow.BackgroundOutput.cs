using System.Collections.Concurrent;
using MightyClaude.Core;
using Microsoft.UI.Dispatching;

namespace MightyClaude.WinUI;

// A background run's live output (M/MightyClaude/AppStore+BackgroundOutput.swift): once a second the last lines of every running
// task block's output file are read off the window's thread into `backgroundOutput`. The web page's 흐름 blocks and 파이프라인
// cards, the strip above the composer (WinUI and web) and the phone read them. They are never saved: they can move every second,
// and the state file must not.
//
// A block that ended is read once more and then left alone; a block redrawn from history (or a save) that still has its file gets
// one read. A file that is missing, unreadable, not a regular file, or on another computer (a remote workspace's pane) gives no
// lines and no error.

public sealed partial class MainWindow
{
    /// <summary>
    /// The lines by pane, then by block id. Each pane's map is replaced whole when it changes, never edited, so a reader on
    /// another thread (the phone's digest) sees one map or the next, and a cache can tell a change by reference.
    /// </summary>
    private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>> backgroundOutput = new(StringComparer.Ordinal);
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> NoBackgroundOutput = new Dictionary<string, IReadOnlyList<string>>();
    /// <summary>Running task blocks being followed; the ones that owe their last (or, redrawn, their one) read; the ones read since they ended.</summary>
    private readonly HashSet<string> backgroundOutputPolling = new(StringComparer.Ordinal), backgroundOutputFinal = new(StringComparer.Ordinal), backgroundOutputRead = new(StringComparer.Ordinal);
    /// <summary>A block a read hung on, by the batch it hung in: skipped until that batch answers.</summary>
    private readonly Dictionary<int, string> backgroundOutputHung = [];
    private bool backgroundOutputReading;
    private int backgroundOutputBatch;
    private int? backgroundOutputShape;
    private BackgroundOutputProgress? backgroundOutputProgress;
    private BackgroundOutputReader backgroundOutputReader = new();
    private DispatcherQueueTimer? backgroundOutputTimer;

    /// <summary>A batch of reads still out after this on one file is given up on, so one hung read (a stalled network share) never stops the polling.</summary>
    private static readonly TimeSpan BackgroundOutputReadTimeout = TimeSpan.FromSeconds(10);

    /// <summary>One pane's lines by block id; empty when it has none.</summary>
    internal IReadOnlyDictionary<string, IReadOnlyList<string>> BackgroundOutputFor(string paneId) =>
        backgroundOutput.TryGetValue(paneId, out var lines) ? lines : NoBackgroundOutput;

    /// <summary>Its own 1 s timer: a tick with no background run costs one walk over the graphs.</summary>
    private void InitBackgroundOutput()
    {
        backgroundOutputTimer = DispatcherQueue.CreateTimer();
        backgroundOutputTimer.Interval = TimeSpan.FromSeconds(1); backgroundOutputTimer.IsRepeating = true;
        backgroundOutputTimer.Tick += (_, _) => BackgroundOutputTick();
        backgroundOutputTimer.Start();
    }

    private void BackgroundOutputTick()
    {
        if (closing) { backgroundOutputTimer?.Stop(); backgroundOutputReader.Complete(); return; }
        if (backgroundOutputReading)
        {
            // Only one file read for the whole timeout is given up on; a batch that is merely slow, moving from file to file, is waited for.
            if (backgroundOutputProgress?.Current is not { } stuck || DateTime.UtcNow - stuck.Since <= BackgroundOutputReadTimeout) return;
            // That block is skipped until its batch answers, under that batch's id; the batch id moves on so the answer reads as
            // stale, and later batches read on a thread of their own rather than wait behind it.
            backgroundOutputHung[backgroundOutputBatch] = stuck.Id;
            backgroundOutputBatch++;
            backgroundOutputReader.Complete(); backgroundOutputReader = new();
            backgroundOutputReading = false; backgroundOutputProgress = null;
        }
        var snapshot = service.Snapshot;
        // The panes whose files this PC can read: Claude panes in local workspaces.
        var remote = snapshot.Workspaces.Where(w => w.Remote is not null).Select(w => w.Id).ToHashSet(StringComparer.Ordinal);
        var sessions = snapshot.Sessions.Where(s => s.Kind == "claude" && s.Provider == "claude" && !remote.Contains(s.WorkspaceId)).ToList();
        var hung = backgroundOutputHung.Values.ToHashSet(StringComparer.Ordinal);
        // The graphs' shape: a pane, a request or a block that came or went.
        var shape = new HashCode();
        foreach (var session in snapshot.Sessions) shape.Add(session.Id);
        var queued = new HashSet<string>(StringComparer.Ordinal);
        var reads = new List<(string Id, string Pane, string Path)>();
        foreach (var session in sessions)
        {
            var held = BackgroundOutputFor(session.Id);
            foreach (var runs in BackgroundOutputRuns(session))
            {
                shape.Add(runs.Count);
                foreach (var run in runs)
                {
                    shape.Add(run.Id); shape.Add(run.Agents.Count);
                    foreach (var agent in run.Agents)
                    {
                        if (!agent.IsTask || agent.OutputFile is not { } path || queued.Contains(agent.Id)) continue;
                        if (!MightyGraphLayout.Terminal(agent.Status)) backgroundOutputPolling.Add(agent.Id);
                        // It owes its last read once it ended, or its one read when redrawn, until a read of it is applied.
                        else if (backgroundOutputPolling.Remove(agent.Id) || !backgroundOutputRead.Contains(agent.Id) && !held.ContainsKey(agent.Id)) backgroundOutputFinal.Add(agent.Id);
                        else if (!backgroundOutputFinal.Contains(agent.Id)) continue;
                        if (hung.Contains(agent.Id)) continue;
                        queued.Add(agent.Id);
                        reads.Add((agent.Id, session.Id, path));
                    }
                }
            }
        }
        var shapeKey = shape.ToHashCode();
        if (shapeKey != backgroundOutputShape)
        {
            backgroundOutputShape = shapeKey;
            PruneBackgroundOutput(sessions, snapshot.Sessions.Select(s => s.Id).ToHashSet(StringComparer.Ordinal));
        }
        if (reads.Count == 0) return;
        var batch = ++backgroundOutputBatch;
        var progress = new BackgroundOutputProgress();
        backgroundOutputProgress = progress; backgroundOutputReading = true;
        var queue = DispatcherQueue;
        // A thread of its own, never the shared pool: a read stalled on a dead share holds that one thread, not one the app needs.
        backgroundOutputReader.Post(() =>
        {
            var results = reads.Select(read =>
            {
                progress.Start(read.Id);
                return (read.Id, read.Pane, Lines: ReadBackgroundOutput(read.Path));
            }).ToList();
            progress.Finish();
            queue.TryEnqueue(() => ApplyBackgroundOutput(results, batch));
        });
    }

    /// <summary>The pane's saved runs and, when its view holds them, the requests read from its session record.</summary>
    private IEnumerable<IReadOnlyList<MightyGraphRun>> BackgroundOutputRuns(RunSession session)
    {
        if (views.TryGetValue(session.Id, out var view)) yield return view.BackgroundHistoryRuns(session);
        yield return session.GraphRuns ?? [];
    }

    /// <summary>Drops what belongs to blocks no longer in any graph (a closed pane, a trimmed request) and the lines of panes that closed. Run only when the graphs' shape moved.</summary>
    private void PruneBackgroundOutput(List<RunSession> sessions, HashSet<string> live)
    {
        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (var session in sessions)
            foreach (var runs in BackgroundOutputRuns(session))
                foreach (var run in runs)
                    foreach (var agent in run.Agents) if (agent.IsTask) present.Add(agent.Id);
        backgroundOutputPolling.IntersectWith(present);
        backgroundOutputRead.IntersectWith(present);
        backgroundOutputFinal.IntersectWith(present);
        foreach (var (batch, id) in backgroundOutputHung.ToArray()) if (!present.Contains(id)) backgroundOutputHung.Remove(batch);
        var changed = new List<string>();
        foreach (var (pane, lines) in backgroundOutput.ToArray())
        {
            // A remote or closed pane keeps nothing.
            var kept = live.Contains(pane) ? lines.Where(pair => present.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal) : new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            if (kept.Count == lines.Count) continue;
            if (kept.Count == 0) backgroundOutput.TryRemove(pane, out _); else backgroundOutput[pane] = kept;
            changed.Add(pane);
        }
        foreach (var pane in changed) BackgroundOutputChanged(pane);
    }

    /// <summary>
    /// The file's last lines, or null. Only a regular file is opened: a pipe or a device would block or answer forever. A link is
    /// followed first, so where it points is what is judged.
    /// </summary>
    private static IReadOnlyList<string>? ReadBackgroundOutput(string path)
    {
        try
        {
            if (!LocalFilePath(path)) return null;
            var file = new FileInfo(path);
            // FileInfo turns a legacy device name (C:\x\CON) into \\.\CON: checked again before the file system is touched.
            if (!LocalFilePath(file.FullName)) return null;
            if (file.ResolveLinkTarget(returnFinalTarget: true) is FileInfo target) file = target;
            if (!LocalFilePath(file.FullName) || !file.Exists || file.Attributes.HasFlag(FileAttributes.Device)) return null;
            return BackgroundOutputTail.Read(file.FullName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>
    /// A path on a local drive: a drive letter (<c>C:\…</c>), or the same behind <c>\\?\</c> (<c>\\?\C:\…</c>). The device namespaces
    /// (<c>\\.\…</c>, <c>\\?\GLOBALROOT\…</c>, <c>\\?\pipe\…</c>), UNC shares (<c>\\server\share</c>, <c>\\?\UNC\…</c>) and relative paths
    /// are not read: a pipe or a device may never answer, and a share is another computer's file.
    /// </summary>
    private static bool LocalFilePath(string path)
    {
        var rest = path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
        if (rest.StartsWith(@"\\", StringComparison.Ordinal) || rest.StartsWith("//", StringComparison.Ordinal)) return false;
        return rest.Length >= 3 && char.IsAsciiLetter(rest[0]) && rest[1] == ':' && rest[2] is '\\' or '/';
    }

    /// <summary>
    /// Publishes only what changed. A file that gave nothing keeps the lines read before it: the last lines stay once a run ended,
    /// and a missing file shows none. A batch given up on is dropped when it finally answers.
    /// </summary>
    private void ApplyBackgroundOutput(List<(string Id, string Pane, IReadOnlyList<string>? Lines)> results, int batch)
    {
        // Whatever this batch was stuck on may be read again, and a batch given up on brings stale lines.
        backgroundOutputHung.Remove(batch);
        if (batch != backgroundOutputBatch) return;
        backgroundOutputReading = false; backgroundOutputProgress = null;
        if (closing) return;
        var changed = new Dictionary<string, Dictionary<string, IReadOnlyList<string>>>(StringComparer.Ordinal);
        foreach (var (id, pane, lines) in results)
        {
            // A block dropped while its read was out stays dropped.
            var owed = backgroundOutputFinal.Remove(id);
            if (owed) backgroundOutputRead.Add(id);
            if (!owed && !backgroundOutputPolling.Contains(id) || lines is not { Count: > 0 }) continue;
            var current = changed.TryGetValue(pane, out var edited) ? edited : null;
            var before = (IReadOnlyDictionary<string, IReadOnlyList<string>>?)current ?? BackgroundOutputFor(pane);
            if (before.TryGetValue(id, out var old) && old.SequenceEqual(lines)) continue;
            current ??= changed[pane] = new Dictionary<string, IReadOnlyList<string>>(BackgroundOutputFor(pane), StringComparer.Ordinal);
            current[id] = lines;
        }
        foreach (var (pane, lines) in changed)
        {
            backgroundOutput[pane] = lines;
            BackgroundOutputChanged(pane);
        }
    }

    /// <summary>
    /// One pane's lines moved: only that pane is drawn again — its strip (WinUI and the page's card), its Mighty view on the page
    /// when shown — and the phone hears of it as of a graph change (the pane's digest hashes its lines).
    /// </summary>
    private void BackgroundOutputChanged(string paneId)
    {
        MightyDirty(paneId);
        if (views.TryGetValue(paneId, out var view) && service.Snapshot.Sessions.FirstOrDefault(s => s.Id == paneId) is { } session) view.RefreshBackgroundWork(session);
        mobileRouter?.Changed();
    }

    /// <summary>The block a batch of reads is on and since when, so a batch given up on names the one it is stuck on, and a merely slow batch is not.</summary>
    private sealed class BackgroundOutputProgress
    {
        private readonly object gate = new();
        private (string Id, DateTime Since)? value;
        internal (string Id, DateTime Since)? Current { get { lock (gate) return value; } }
        internal void Start(string id) { lock (gate) value = (id, DateTime.UtcNow); }
        internal void Finish() { lock (gate) value = null; }
    }

    /// <summary>
    /// One background thread running the reads in turn. Given up on, it is completed: a read hung on it still holds that one
    /// thread until it answers, and then the thread ends.
    /// </summary>
    private sealed class BackgroundOutputReader
    {
        private readonly BlockingCollection<Action> work = new();
        internal BackgroundOutputReader()
        {
            new Thread(() =>
            {
                foreach (var action in work.GetConsumingEnumerable())
                {
                    try { action(); }
                    catch (Exception ex) when (ex is not OutOfMemoryException) { System.Diagnostics.Trace.TraceWarning("background output: " + ex.GetType().Name); }
                }
                // Completed and drained: nothing reads it any more.
                work.Dispose();
            }) { IsBackground = true, Name = "MightyClaude background output" }.Start();
        }
        internal void Post(Action action) { try { work.Add(action); } catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { } }
        /// <summary>No more reads: the thread ends once the one it runs (if any) answers, and the collection goes with it.</summary>
        internal void Complete()
        {
            try { work.CompleteAdding(); } catch (ObjectDisposedException) { }
        }
    }

    private sealed partial class PaneView
    {
        /// <summary>The requests read from the session record that still attach above the saved ones (as <see cref="GraphHistoryRuns"/> shows them, without following a trim).</summary>
        internal IReadOnlyList<MightyGraphRun> BackgroundHistoryRuns(RunSession pane) =>
            graphHistory.Connects(pane.GraphRuns?.FirstOrDefault()?.Id, pane.ResumeId) ? graphHistory.Runs : [];
    }
}
