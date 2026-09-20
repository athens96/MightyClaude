namespace MightyClaude.Core;

// Mirrors AgentCompanion.swift lines 198-213: pure decision logic over run events.
// Tracks which sessions received status "running" and answers whether a completed
// event should trigger a notification and with what title.
// The notification contains no prompt, tool arguments or project path.
public sealed class CompletionNotificationDecision
{
    private readonly HashSet<string> _activeRuns = [];

    // Returns null when no notification should fire.
    // Returns the notification title (workspace · session title, or session title
    // alone) when a notification should fire.
    public string? Process(RunEvent ev, AppSnapshot snapshot, bool enabled)
    {
        if (ev.Type != "status") return null;
        if (ev.Status == "running")
        {
            _activeRuns.Add(ev.SessionId);
            return null;
        }
        if (ev.Status is "error" or "stopped")
        {
            _activeRuns.Remove(ev.SessionId);
            return null;
        }
        if (ev.Status == "completed")
        {
            var wasRunning = _activeRuns.Remove(ev.SessionId);
            if (!wasRunning || !enabled) return null;
            var session = snapshot.Sessions.FirstOrDefault(s => s.Id == ev.SessionId);
            var title = session?.Title ?? ev.SessionId;
            var workspace = session is not null
                ? snapshot.Workspaces.FirstOrDefault(w => w.Id == session.WorkspaceId)?.Name
                : null;
            return workspace is not null ? workspace + " · " + title : title;
        }
        return null;
    }
}
