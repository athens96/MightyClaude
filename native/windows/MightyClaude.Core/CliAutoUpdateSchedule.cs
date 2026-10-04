using System.Text.Json;

namespace MightyClaude.Core;

public sealed class CliAutoUpdateSchedule
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(6), Tick = TimeSpan.FromMinutes(1), IdleDelay = TimeSpan.FromMinutes(3), PluginBudget = TimeSpan.FromMinutes(5);
    private readonly HashSet<string> deferred = [];
    public DateTimeOffset? LastPass { get; private set; }
    public bool IsDue(DateTimeOffset now, bool enabled) => enabled && (LastPass is null || now - LastPass >= Interval);
    public void PassStarted(DateTimeOffset now) => LastPass = now;
    public void SkippedBusy(string provider) => deferred.Add(provider);
    public void Updated(string provider) => deferred.Remove(provider);
    public static bool IdleLongEnough(bool busy, bool queued, DateTimeOffset? lastActive, DateTimeOffset now) => !busy && !queued && (lastActive is null || now - lastActive >= IdleDelay);
    public string[] DueRetries(bool enabled, Func<string, bool> ready)
    {
        if (!enabled) { deferred.Clear(); return []; }
        var due = deferred.Where(ready).Order().ToArray(); deferred.ExceptWith(due); return due;
    }
}

public sealed record PluginAutoUpdateResult(string Status, string Detail, IReadOnlyList<string>? Updated = null, IReadOnlyList<string>? NeedsApproval = null, IReadOnlyList<string>? Failed = null)
{
    public static PluginAutoUpdateResult Busy => new("busy", Locale.Get("pluginAutoUpdate.busy"));
    public static PluginAutoUpdateResult Cancelled => new("cancelled", Locale.Get("pluginAutoUpdate.cancelled"));
    public static PluginAutoUpdateResult Failure => new("failed", Locale.Get("pluginAutoUpdate.failed"));
    public static string ClaudeOutcome(CliRunResult result)
    {
        if (result.TimedOut || System.Text.Encoding.UTF8.GetByteCount(result.Output) > 1_048_576) return "failed";
        var last = result.Output.Split('\n').LastOrDefault(line => !string.IsNullOrWhiteSpace(line));
        try
        {
            using var doc = JsonDocument.Parse(last ?? "");
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (doc.RootElement.TryGetProperty("shownCommand", out _)) return "needsApproval";
                if (doc.RootElement.Text("outcome") is { } outcome) return outcome == "ok" && result.ExitCode == 0 ? "updated" : "failed";
            }
        }
        catch (JsonException) { }
        return result.ExitCode == 0 ? "updated" : "failed";
    }
    internal static string Count(string key, int count) => Locale.Get(key, new Dictionary<string, string> { ["count"] = count.ToString() });
    internal static string Names(string key, IEnumerable<string> names) => Locale.Get(key, new Dictionary<string, string> { ["plugins"] = string.Join(", ", names) });
}
