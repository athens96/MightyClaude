namespace MightyClaude.Core;

/// <summary>"Copy run log" (M/SessionPaneView.swift:322-325, 962-964): every entry as <c>[role] body</c>, a blank line between entries.</summary>
public static class RunLogText
{
    /// <summary>Who an entry is from: the user, the agent that answered (the pane's when the entry names none), output, an error, or the app.</summary>
    public static string Role(LogEntry entry, string paneProvider) => entry.Kind switch
    {
        "user" => Locale.Get("pane.log.roleUser"),
        "assistant" => ProviderMark.Label(entry.Provider ?? paneProvider),
        "output" => Locale.Get("composer.sessionInfo.output"),
        "error" => Locale.Get("session.state.error"),
        _ => Locale.Get("pane.log.roleSystem"),
    };
    public static string Copy(RunSession pane) => string.Join("\n\n", pane.Logs.Select(entry => "[" + Role(entry, pane.Provider) + "] " + entry.Text));
}
