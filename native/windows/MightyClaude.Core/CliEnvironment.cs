namespace MightyClaude.Core;

/// Refreshes Windows' persisted CLI environment at launch time. A GUI process
/// can predate a changed user-level Bedrock token or PATH. Async CLI entrypoints
/// also refresh the normal terminal's trusted profiles. Values stay in memory.
public static class CliEnvironment
{
    private static readonly object sync = new();
    private static readonly WindowsShellEnvironment shell = new();
    private static readonly HashSet<string> previouslyPersisted = new(StringComparer.OrdinalIgnoreCase);
    private static bool Relevant(string key) => key.Equals("PATH", StringComparison.OrdinalIgnoreCase) || key.StartsWith("AWS_", StringComparison.OrdinalIgnoreCase) || key.StartsWith("ANTHROPIC_", StringComparison.OrdinalIgnoreCase) || key.StartsWith("CLAUDE_", StringComparison.OrdinalIgnoreCase) || key.StartsWith("CODEX_", StringComparison.OrdinalIgnoreCase) || key.StartsWith("OPENAI_", StringComparison.OrdinalIgnoreCase) || key.StartsWith("GEMINI_", StringComparison.OrdinalIgnoreCase) || key.StartsWith("GOOGLE_", StringComparison.OrdinalIgnoreCase);
    private static Dictionary<string, string> Read(EnvironmentVariableTarget target)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables(target)) if (entry.Key is string key && entry.Value is string value) result[key] = value;
        return result;
    }
    public static Dictionary<string, string> Current()
    {
        var baseline = Baseline();
        return OperatingSystem.IsWindows() ? shell.Current(baseline) : baseline;
    }
    public static async Task<Dictionary<string, string>> RefreshAsync(bool force = false, CancellationToken cancellation = default)
    {
        var baseline = Baseline();
        if (!OperatingSystem.IsWindows()) return baseline;
        return (await shell.ResolveAsync(baseline, force, cancellation).ConfigureAwait(false)).Values;
    }
    private static Dictionary<string, string> Baseline()
    {
        var process = Read(EnvironmentVariableTarget.Process);
        if (!OperatingSystem.IsWindows()) return process;
        lock (sync)
        {
            var persisted = Read(EnvironmentVariableTarget.Machine);
            var user = Read(EnvironmentVariableTarget.User);
            var machinePath = persisted.GetValueOrDefault("PATH", "");
            foreach (var pair in user) persisted[pair.Key] = pair.Value;
            if (user.TryGetValue("PATH", out var userPath)) persisted["PATH"] = machinePath + Path.PathSeparator + userPath;
            foreach (var removed in previouslyPersisted.Where(key => !persisted.ContainsKey(key))) process[removed] = "";
            foreach (var pair in persisted.Where(pair => Relevant(pair.Key))) { process[pair.Key] = Environment.ExpandEnvironmentVariables(pair.Value); previouslyPersisted.Add(pair.Key); }
            return process;
        }
    }
}
