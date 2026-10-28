using System.Globalization;

namespace MightyClaude.Core;

/// <summary>
/// New agent panes inherit choices, never a conversation identity (macOS SessionTemplate). A delegated child pane is never the
/// template: its mode was set for its task by its parent, not chosen by the human.
/// </summary>
public static class SessionTemplate
{
    public static DateTimeOffset LastUsed(RunSession session) => session.Logs.AsEnumerable().Reverse()
        .Select(log => Parse(log.Timestamp)).FirstOrDefault(time => time is not null)
        ?? Parse(session.CreatedAt) ?? DateTimeOffset.MinValue;

    private static DateTimeOffset? Parse(string? value) => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
        DateTimeStyles.AssumeUniversal, out var date) ? date : null;

    public static RunSession? Find(RunSession target, IEnumerable<RunSession> sessions) => target.Kind != "claude" ? null
        : sessions.Where(s => s.Id != target.Id && s.ParentSessionId is null && s.Kind == target.Kind && s.Provider == target.Provider)
            .OrderByDescending(LastUsed).ThenByDescending(s => s.CreatedAt, StringComparer.Ordinal).FirstOrDefault();

    public static RunSession Inherit(RunSession target, IEnumerable<RunSession> sessions) => Find(target, sessions) is { } source
        ? target with { Model = source.Model, Settings = source.Settings, AgentViewMode = source.AgentViewMode } : target;

    /// <summary>
    /// Another agent in the same pane (M/AppStore.swift:705-717): the new agent's default model and settings, no conversation to
    /// continue, none of the old agent's usage, and a transcript line that says so. A title that was the old agent's name follows.
    /// </summary>
    public static RunSession SwitchProvider(RunSession pane, string provider)
    {
        if (!Wire.Providers.Contains(provider) || pane.Provider == provider) return pane;
        var line = Locale.Get("pane.log.switchedProvider", new Dictionary<string, string> { ["provider"] = ProviderMark.Label(provider) });
        return pane with
        {
            Provider = provider, Title = pane.Title == ProviderCatalog.Name(pane.Provider) ? ProviderCatalog.Name(provider) : pane.Title,
            Model = "default", Settings = new(), ResumeId = null, SessionUsage = null,
            // Only a Claude agent asks other CLIs; another provider starts back on Claude (M/AppStore.swift switchProvider).
            AskTarget = null,
            Logs = TranscriptRetention.Trimmed([.. pane.Logs, new(Wire.Id(), "system", line, Wire.Now(), provider)]),
        };
    }
}
