using MightyClaude.Core;

// A Claude pane holding the id of a conversation Claude Code never wrote recovers after one failed run.
internal static class ClaudeResumeVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static LogEntry Log(string kind, string text) => new(Guid.NewGuid().ToString(), kind, text, "2026-10-23T00:00:00Z");

    internal static Task APaneForgetsAConversationClaudeCodeDoesNotHave()
    {
        const string id = "8cc9f04d-1f6e-487a-a773-41586e4f7f12";
        var stuck = new RunSession
        {
            Id = "pane", Kind = "claude", Provider = "claude", Status = "running", ResumeId = id,
            Logs = [Log("user", "first"), Log("output", "Error: Expected message role 'user', got 'undefined'"), Log("user", "again"),
                Log("output", ClaudeResume.MissingConversation + id), Log("error", ClaudeResume.MissingConversation + id)],
        };
        var ended = stuck.Apply(new RunEvent("pane", "status", Status: "error"));
        Check(ended.ResumeId is null, "a run refused for the pane's own missing conversation clears the resume id");
        Check(ended.Status == "error" && ended.Logs.Count == stuck.Logs.Count, "the failed run still shows as failed with its logs");

        var other = stuck with { ResumeId = "11111111-2222-3333-4444-555555555555" };
        Check(other.Apply(new RunEvent("pane", "status", Status: "error")).ResumeId == other.ResumeId, "a refusal naming another id keeps the pane's own");

        var earlier = stuck with { Logs = [.. stuck.Logs, Log("user", "new request"), Log("error", "rate limited")] };
        Check(earlier.Apply(new RunEvent("pane", "status", Status: "error")).ResumeId == id, "only the last run's logs count");

        Check(stuck.Apply(new RunEvent("pane", "status", Status: "stopped")).ResumeId == id, "a stopped run keeps the conversation");
        var codex = stuck with { Provider = "codex" };
        Check(codex.Apply(new RunEvent("pane", "status", Status: "error")).ResumeId == id, "only Claude Code's refusal is read");
        return Task.CompletedTask;
    }
}
