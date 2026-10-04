using System.Globalization;

namespace MightyClaude.Core;

/// <summary>New agent panes inherit choices, never a conversation identity (macOS SessionTemplate).</summary>
public static class SessionTemplate
{
    public static DateTimeOffset LastUsed(RunSession session) => session.Logs.AsEnumerable().Reverse()
        .Select(log => Parse(log.Timestamp)).FirstOrDefault(time => time is not null)
        ?? Parse(session.CreatedAt) ?? DateTimeOffset.MinValue;

    private static DateTimeOffset? Parse(string? value) => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
        DateTimeStyles.AssumeUniversal, out var date) ? date : null;

    public static RunSession? Find(RunSession target, IEnumerable<RunSession> sessions) => target.Kind != "claude" ? null
        : sessions.Where(s => s.Id != target.Id && s.Kind == target.Kind && s.Provider == target.Provider)
            .OrderByDescending(LastUsed).ThenByDescending(s => s.CreatedAt, StringComparer.Ordinal).FirstOrDefault();

    public static RunSession Inherit(RunSession target, IEnumerable<RunSession> sessions) => Find(target, sessions) is { } source
        ? target with { Model = source.Model, Settings = source.Settings, AgentViewMode = source.AgentViewMode } : target;
}
