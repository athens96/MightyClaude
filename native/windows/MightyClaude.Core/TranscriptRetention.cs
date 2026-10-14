namespace MightyClaude.Core;

/// <summary>
/// How much of a pane's transcript survives (M/MightyCore/TranscriptRetention.swift:8-11). A run event trims on every append and the
/// state store trims again while it normalizes a snapshot, so the number is one number in one place: a store that kept fewer would
/// shorten history across a restart.
/// </summary>
public static class TranscriptRetention
{
    public const int MaximumEntries = 400;
    public static List<LogEntry> Trimmed(IEnumerable<LogEntry> entries) => entries.TakeLast(MaximumEntries).ToList();
}
