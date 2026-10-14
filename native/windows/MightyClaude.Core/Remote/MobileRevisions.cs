namespace MightyClaude.Core;

/// <summary>
/// Revision bookkeeping for the mobile protocol (M/MightyClaude/AppStore+MobileRemote.swift:11-49, 271-323). A session's
/// revision moves when anything the phone shows of it changed; the state revision moves only when a summary (status, title,
/// preview, pending counts), the pane order or a workspace changed, so the phone's list is not sent again for every reply.
/// </summary>
public sealed class MobileRevisions
{
    /// <summary>
    /// One pane as it stands. <paramref name="Editable"/> and <paramref name="Options"/> are whether the phone may use its
    /// settings pickers and a digest of what they would offer; <paramref name="Mighty"/> is the Mighty payload's own identity
    /// (<see cref="MobileMightySupport.Digest(RunSession)"/> and the guided panel's state), never its streamed text;
    /// <paramref name="Summary"/> is the pane's summary as it goes out, without its revision; <paramref name="Held"/> the ids of
    /// the rows delegation holds in its queued list, in order (M/AppStore+MobileRemote.swift:281-297).
    /// </summary>
    public sealed record Reading(RunSession Pane, bool Editable, int Options, int Mighty, string Summary, string Held = "");

    /// <summary>
    /// A cheap change detector for a pane: its metadata and the tail of its transcript, so a streamed reply and a tool's
    /// state are noticed without comparing every saved entry. The status line is deliberately absent: one that prints a
    /// clock or a token count would change on every run and the phone's poll would never sleep again.
    /// </summary>
    private sealed record Fingerprint(string Status, string Title, string Provider, string Model, string? ResumeId, int Count, string Tail, string? Usage, string Settings, string View, string? Limits, bool Editable, int Options, int Mighty, string Held)
    {
        internal static Fingerprint Of(Reading reading)
        {
            var pane = reading.Pane;
            return new(pane.Status, pane.Title, pane.Provider, pane.Model, pane.ResumeId, pane.Logs.Count,
                string.Join('\n', pane.Logs.Skip(Math.Max(0, pane.Logs.Count - 12)).Select(e => e.Id + ":" + e.Text.Length + ":" + (e.Activity?.State ?? ""))), pane.SessionUsage?.UpdatedAt,
                pane.Settings.Effort + "|" + pane.Settings.PermissionMode, (pane.AgentViewMode ?? "") + "|" + (pane.MightyStyle ?? ""), pane.SessionUsage?.RateLimitsUpdatedAt, reading.Editable, reading.Options, reading.Mighty, reading.Held);
        }
    }

    private readonly Dictionary<string, long> sessions = [];
    private Dictionary<string, Fingerprint> seen = [];
    private Dictionary<string, (string Summary, long Revision)> summaries = [];
    private string[] order = [];
    private string workspaces = "";
    public long State { get; private set; } = 1;
    public long Session(string id) => sessions.GetValueOrDefault(id, 1);

    /// <summary>
    /// Reads the panes again and returns the scopes whose revision moved — <c>state</c> first, then <c>session:&lt;id&gt;</c> for
    /// each pane — for the connected phones to be told. A pane whose summary alone moved has its own revision moved too.
    /// </summary>
    public IReadOnlyList<(string Scope, long Revision)> Observe(IEnumerable<Workspace> spaces, IReadOnlyList<Reading> panes)
    {
        var paneOrder = panes.Select(p => p.Pane.Id).ToArray(); var workspaceKey = string.Join('\n', spaces.Select(w => w.Id + "\u001f" + w.Name + "\u001f" + w.Path));
        var stateChanged = workspaceKey != workspaces || !order.SequenceEqual(paneOrder); var changed = new List<string>();
        var nextSeen = new Dictionary<string, Fingerprint>(); var nextSummaries = new Dictionary<string, (string, long)>();
        foreach (var reading in panes)
        {
            var id = reading.Pane.Id; var fingerprint = Fingerprint.Of(reading); var moved = !seen.TryGetValue(id, out var before) || before != fingerprint;
            if (moved) sessions[id] = sessions.GetValueOrDefault(id) + 1;
            var known = summaries.TryGetValue(id, out var previous);
            if (!known || previous.Summary != reading.Summary) { stateChanged = true; if (!moved) sessions[id] = sessions.GetValueOrDefault(id) + 1; }
            var revision = sessions.GetValueOrDefault(id, 1);
            if (!known || previous.Revision != revision) changed.Add(id);
            nextSeen[id] = fingerprint; nextSummaries[id] = (reading.Summary, revision);
        }
        foreach (var id in summaries.Keys.Where(id => !nextSummaries.ContainsKey(id)).ToArray()) sessions.Remove(id);
        seen = nextSeen; summaries = nextSummaries; workspaces = workspaceKey; order = paneOrder;
        if (stateChanged) State++;
        var result = new List<(string, long)>();
        if (stateChanged) result.Add(("state", State));
        result.AddRange(changed.Select(id => ("session:" + id, Session(id))));
        return result;
    }
}
