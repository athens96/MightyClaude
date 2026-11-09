using System.Text;
using System.Text.Json.Serialization;

namespace MightyClaude.Core;

/// <summary>
/// A shell code block from an agent's reply that the user ran (spec: run-shell-blocks; docs/windows-parity.md), saved with its
/// pane as <c>shellRuns</c>. <see cref="Key"/> is <c>&lt;entryId&gt;#&lt;n&gt;</c>, n the block's place among the runnable
/// blocks of that entry; <see cref="State"/> one of <see cref="ShellBlockRunSupport.States"/>; <see cref="Output"/> the run's
/// cleaned output, its last 16 KB.
/// </summary>
public sealed record ShellBlockRun(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("command")] string Command,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("exitCode")] int? ExitCode,
    [property: JsonPropertyName("output")] string Output,
    [property: JsonPropertyName("startedAt")] string StartedAt,
    [property: JsonPropertyName("endedAt")] string? EndedAt = null);

public static class ShellBlockRunSupport
{
    /// <summary>The output kept per run, in UTF-8 bytes (its end).</summary>
    public const int OutputLimitBytes = 16_384;
    /// <summary>The records kept per pane, the newest.</summary>
    public const int MaximumRuns = 64;
    /// <summary>What AgentTerminal.Run takes.</summary>
    public const int MaximumCommandBytes = 65_536;
    public static readonly IReadOnlySet<string> States = new HashSet<string>(StringComparer.Ordinal) { "running", "completed", "failed", "stopped", "interrupted" };

    /// <summary>Whether <paramref name="key"/> is <c>&lt;entryId&gt;#&lt;n&gt;</c>.</summary>
    public static bool IsKey(string? key) => EntryId(key) is not null;

    /// <summary>The entry id of a key, or null when it is not one.</summary>
    public static string? EntryId(string? key)
    {
        if (key is null || key.LastIndexOf('#') is var at && at <= 0) return null;
        var entry = key[..at]; var index = key[(at + 1)..];
        return Wire.Identifier(entry) && index.Length is > 0 and <= 4 && index.All(char.IsAsciiDigit) ? entry : null;
    }

    /// <summary>A command the pane's terminal takes: not blank, no NUL, within <see cref="MaximumCommandBytes"/>.</summary>
    public static bool IsCommand(string? command) => !string.IsNullOrWhiteSpace(command) && !command.Contains('\0') && Encoding.UTF8.GetByteCount(command) <= MaximumCommandBytes;

    public static ShellBlockRun Started(string key, string command, string? now = null) => new(key, command, "running", null, "", now ?? Wire.Now());

    /// <summary>A running record's end: stopped when the user stopped it, else completed for exit code 0 and failed for any other (or none).</summary>
    public static ShellBlockRun Finished(ShellBlockRun run, int? exitCode, bool stopped, string? now = null) => run.State != "running" ? run
        : run with { State = stopped ? "stopped" : exitCode == 0 ? "completed" : "failed", ExitCode = exitCode, EndedAt = now ?? Wire.Now() };

    /// <summary>The records with <paramref name="run"/> in the place of the one with its key (or after the rest), the newest <see cref="MaximumRuns"/>.</summary>
    public static List<ShellBlockRun> Replace(IReadOnlyList<ShellBlockRun>? runs, ShellBlockRun run)
    {
        var next = (runs ?? []).ToList(); var at = next.FindIndex(r => r.Key == run.Key);
        if (at >= 0) next[at] = run; else next.Add(run);
        return next.Count > MaximumRuns ? next.Skip(next.Count - MaximumRuns).ToList() : next;
    }

    /// <summary>The end of <paramref name="text"/> that fits <see cref="OutputLimitBytes"/> in UTF-8, never splitting a surrogate pair.</summary>
    public static string Tail(string text)
    {
        if (text.Length * 3 <= OutputLimitBytes || Encoding.UTF8.GetByteCount(text) <= OutputLimitBytes) return text;
        var bytes = 0; var start = text.Length;
        while (start > 0)
        {
            var width = char.IsLowSurrogate(text[start - 1]) && start >= 2 && char.IsHighSurrogate(text[start - 2]) ? 2 : 1;
            var size = width == 2 ? 4 : text[start - 1] < 0x80 ? 1 : text[start - 1] < 0x800 ? 2 : 3;
            if (bytes + size > OutputLimitBytes) break;
            bytes += size; start -= width;
        }
        return text[start..];
    }

    /// <summary>
    /// The saved records as the pane keeps them: valid ones only, one per key, the newest <see cref="MaximumRuns"/>, output within
    /// its limit. Restoring, a run still marked running was cut off by the app's quit: it reads interrupted.
    /// </summary>
    public static List<ShellBlockRun>? Normalized(List<ShellBlockRun>? runs, bool restoring)
    {
        if (runs is not { Count: > 0 }) return null;
        var kept = new List<ShellBlockRun>();
        foreach (var run in runs)
        {
            if (run is null || !IsKey(run.Key) || !IsCommand(run.Command) || run.State is null || !States.Contains(run.State) || string.IsNullOrEmpty(run.StartedAt)) continue;
            var state = restoring && run.State == "running" ? "interrupted" : run.State;
            var output = run.Output is null ? "" : Tail(run.Output);
            var value = state == run.State && ReferenceEquals(output, run.Output) && (run.EndedAt is null || run.EndedAt.Length <= 64) ? run
                : run with { State = state, Output = output, EndedAt = run.EndedAt is { Length: <= 64 } ? run.EndedAt : null };
            var at = kept.FindIndex(r => r.Key == value.Key);
            if (at >= 0) kept.RemoveAt(at);
            kept.Add(value);
        }
        if (kept.Count > MaximumRuns) kept = kept.Skip(kept.Count - MaximumRuns).ToList();
        return kept.Count > 0 ? kept : null;
    }

    /// <summary>The assistant entry of <paramref name="entryId"/> in the pane's transcript or its Mighty runs, or null.</summary>
    public static LogEntry? AssistantEntry(RunSession pane, string entryId)
    {
        static LogEntry? In(IEnumerable<LogEntry>? entries, string id) => entries?.LastOrDefault(e => e.Id == id && e.Kind == "assistant" && e.Activity is null);
        return In(pane.Logs, entryId) ?? pane.GraphRuns?.Select(run => In(run.RootEntries, entryId) ?? In(run.ResultEntries, entryId) ?? run.Agents.Select(agent => In(agent.Entries, entryId)).FirstOrDefault(e => e is not null)).FirstOrDefault(e => e is not null);
    }

    /// <summary>
    /// Whether <paramref name="command"/> is shown in that entry: every line of it is in the entry's text (a console block's
    /// commands lose their prompt, so the whole command need not be one run of the text). The page draws the button; this
    /// keeps the host from running anything the reply does not show.
    /// </summary>
    public static bool Shown(RunSession pane, string key, string command)
    {
        if (EntryId(key) is not { } id || AssistantEntry(pane, id) is not { } entry) return false;
        var text = entry.Text.Replace("\r\n", "\n");
        return command.Replace("\r\n", "\n").Split('\n').All(line => text.Contains(line, StringComparison.Ordinal));
    }
}

/// <summary>
/// One run's output as the block shows it: escape sequences removed as the agent's tool output has them removed
/// (<see cref="TerminalTextCleaner"/>), a carriage return's redraw resolved (the line starts again), the last 16 KB.
/// Not thread-safe: the caller holds its own lock.
/// </summary>
public sealed class ShellBlockOutput
{
    private readonly TerminalTextCleaner cleaner = new(keepCarriageReturns: true);
    private readonly StringBuilder text = new();
    private bool carriage;

    public void Append(string raw)
    {
        foreach (var c in cleaner.Clean(raw))
        {
            if (carriage)
            {
                carriage = false;
                if (c == '\n') { text.Append('\n'); continue; }
                var line = LastNewline() + 1; text.Remove(line, text.Length - line);
            }
            if (c == '\r') carriage = true; else text.Append(c);
        }
        // Keeps a few limits' worth, so the tail is cut once in a while rather than on every chunk.
        if (text.Length > ShellBlockRunSupport.OutputLimitBytes * 4) { var tail = ShellBlockRunSupport.Tail(text.ToString()); text.Clear().Append(tail); }
    }

    private int LastNewline()
    {
        for (var at = text.Length - 1; at >= 0; at--) if (text[at] == '\n') return at;
        return -1;
    }

    public string Text => ShellBlockRunSupport.Tail(text.ToString());
}
