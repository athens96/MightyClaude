using System.Diagnostics;

namespace MightyClaude.WinUI;

/// <summary>
/// Timing of the UI thread's heavy steps, for finding what makes the app slow on a real machine. It writes only while
/// a <c>perf.log</c> file already stands in the state folder (create it to start, delete it to stop): each step that
/// held the UI thread 16 ms or more, and each mark, is appended to it with the time of day.
/// </summary>
internal static class PerfLog
{
    private static string? path;

    /// <summary>Looks once, at start, for the state folder's <c>perf.log</c>.</summary>
    internal static void Configure(string stateDirectory)
    {
        var file = Path.Combine(stateDirectory, "perf.log");
        path = File.Exists(file) ? file : null;
        Mark("app started");
    }

    internal static Scope Measure(string step) => new(path is null ? null : step, path is null ? 0 : Stopwatch.GetTimestamp(), path is null ? 0 : GC.GetAllocatedBytesForCurrentThread());

    /// <summary>A moment worth lining up with the steps around it, such as a request sent.</summary>
    internal static void Mark(string what) => Write(what, null);

    private static void Write(string what, double? milliseconds)
    {
        if (path is null) return;
        var line = $"{DateTime.Now:HH:mm:ss.fff} {(milliseconds is { } ms ? $"{ms,8:F1} ms" : "    mark   ")} {what}{Environment.NewLine}";
        try { File.AppendAllText(path, line); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>A step's time, and what it allocated on its thread (boxing included): written when it took 16 ms or allocated 1 MB.</summary>
    internal readonly struct Scope(string? step, long start, long allocated) : IDisposable
    {
        public void Dispose()
        {
            if (step is null) return;
            var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            var megabytes = (GC.GetAllocatedBytesForCurrentThread() - allocated) / 1048576.0;
            if (elapsed >= 16 || megabytes >= 1) Write($"{step} [alloc {megabytes:F1} MB]", elapsed);
        }
    }
}
