namespace MightyClaude.Core;

/// <summary>
/// A Claude pane whose saved conversation no longer exists. Claude Code names a session in its first event, before it has
/// accepted the user message, so a run refused at that point (as Claude Code 2.1.289 refused a frame without a role) leaves the
/// pane holding the id of a conversation that was never written. Every later run resumes it and Claude Code exits at once with
/// "No conversation found with session ID: …". When the last run ended that way for the id the pane holds, the pane forgets it,
/// and the next request starts a new conversation instead of failing forever.
/// </summary>
public static class ClaudeResume
{
    /// <summary>What Claude Code prints, to stdout and stderr, when <c>--resume</c> names no conversation it has.</summary>
    public const string MissingConversation = "No conversation found with session ID: ";

    /// <summary>
    /// True when <paramref name="pane"/> is a Claude pane holding a resume id, and a log of its last run (after the last
    /// request) says Claude Code has no conversation with exactly that id.
    /// </summary>
    public static bool Lost(RunSession pane)
    {
        if (pane.Kind != "claude" || pane.Provider != "claude" || pane.ResumeId is not { Length: > 0 } id) return false;
        var start = pane.Logs.FindLastIndex(log => log.Kind == "user");
        var refusal = MissingConversation + id;
        for (var index = start + 1; index < pane.Logs.Count; index++)
            if (pane.Logs[index].Kind is "error" or "output" && pane.Logs[index].Text.Contains(refusal, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
