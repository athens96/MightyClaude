namespace MightyClaude.Core;

/// <summary>
/// Tells its owner that something changed under a folder, so the styles and what they read are read again on a file
/// change and never on a timer (M/StyleStateWatcher.swift:4-8). Changes are gathered for <see cref="Latency"/> and handed
/// over together, on a worker thread. A folder that is not there yet is waited for at its nearest existing parent. When
/// the system loses track (its buffer overran, the folder was taken away) the watch is set up again and the owner is
/// handed an empty list: read everything again.
/// </summary>
public sealed class StyleChangeWatcher : IDisposable
{
    /// <summary>More changed paths than this in one report are a re-scan.</summary>
    private const int MaximumPaths = 256;
    private readonly object sync = new();
    private readonly string root;
    private readonly Action<IReadOnlyList<string>> changed;
    private readonly Timer timer;
    private readonly HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
    private FileSystemWatcher? watcher;
    private bool rescan, scheduled, disposed;
    /// <summary>How long changes are gathered before they are reported; the Mac's stream gathers for the same half second.</summary>
    public TimeSpan Latency { get; }

    public StyleChangeWatcher(string root, Action<IReadOnlyList<string>> changed, TimeSpan? latency = null)
    {
        this.root = Path.GetFullPath(root); this.changed = changed; Latency = latency ?? TimeSpan.FromMilliseconds(500);
        timer = new(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
        lock (sync) Arm();
    }

    /// <summary>
    /// Watches the folder with everything under it, or — while it is not there — its nearest existing parent for the
    /// next folder on the way to appear. A folder that appeared between the look and the watch is found by looking again.
    /// </summary>
    private void Arm()
    {
        for (var attempt = 0; attempt < 8 && !disposed; attempt++)
        {
            watcher?.Dispose(); watcher = null;
            var at = root; string? awaited = null;
            while (!Directory.Exists(at))
            {
                awaited = Path.GetFileName(at);
                if (Path.GetDirectoryName(at) is not { Length: > 0 } parent) return;
                at = parent;
            }
            FileSystemWatcher? next = null;
            try
            {
                next = new(at) { IncludeSubdirectories = awaited is null, InternalBufferSize = 64 * 1024 };
                if (awaited is null) next.NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime;
                else { next.NotifyFilter = NotifyFilters.DirectoryName; next.Filter = awaited; }
                var whole = awaited is null;
                FileSystemEventHandler seen = (_, args) => Seen(next, whole, args.FullPath);
                next.Changed += seen; next.Created += seen; next.Deleted += seen;
                next.Renamed += (_, args) => { Seen(next, whole, args.OldFullPath); Seen(next, whole, args.FullPath); };
                next.Error += (_, _) => Lost(next);
                next.EnableRaisingEvents = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { next?.Dispose(); return; }
            watcher = next;
            if (awaited is null ? Directory.Exists(at) : !Directory.Exists(Path.Combine(at, awaited))) return;
        }
    }

    /// <summary>One change. While the folder was only waited for, the next folder appearing moves the watch down to it.</summary>
    private void Seen(FileSystemWatcher source, bool whole, string path)
    {
        lock (sync)
        {
            if (disposed || !ReferenceEquals(source, watcher)) return;
            if (!whole) { Arm(); path = root; }
            if (paths.Count >= MaximumPaths) rescan = true; else paths.Add(path);
            Schedule();
        }
    }

    private void Lost(FileSystemWatcher source)
    {
        lock (sync)
        {
            if (disposed || !ReferenceEquals(source, watcher)) return;
            rescan = true; Arm(); Schedule();
        }
    }

    private void Schedule()
    {
        if (scheduled) return;
        scheduled = true; timer.Change(Latency, Timeout.InfiniteTimeSpan);
    }

    private void Flush()
    {
        IReadOnlyList<string> report;
        lock (sync)
        {
            if (disposed) return;
            report = rescan ? [] : paths.ToArray();
            paths.Clear(); rescan = false; scheduled = false;
        }
        changed(report);
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true; watcher?.Dispose(); watcher = null; timer.Dispose();
        }
    }

    /// <summary>
    /// Whether changes under a workspace can move what a pane's style panel shows: the workspace's own style files
    /// (<c>.claude/mighty-styles</c>) or a file source of the pane's style (<see cref="StyleStateEngine.Affects"/>). An empty
    /// list is the watcher's "read everything again".
    /// </summary>
    public static bool Concerns(IReadOnlyList<string> changed, string workspace, StyleManifest? manifest)
    {
        if (changed.Count == 0) return true;
        var styles = Path.Combine(workspace, ".claude", "mighty-styles");
        return changed.Any(path => Touches(path, styles) || manifest is not null && StyleStateEngine.Affects(manifest, workspace, path));
    }

    /// <summary>One path is the other, or lies under it: a change inside the folder, or one of its parents appearing.</summary>
    public static bool Touches(string changed, string folder)
    {
        static bool Under(string path, string parent) => path.Length > parent.Length && path.StartsWith(parent, StringComparison.OrdinalIgnoreCase) && path[parent.Length] is '\\' or '/';
        changed = Path.TrimEndingDirectorySeparator(Path.GetFullPath(changed)); folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        return string.Equals(changed, folder, StringComparison.OrdinalIgnoreCase) || Under(changed, folder) || Under(folder, changed);
    }
}
