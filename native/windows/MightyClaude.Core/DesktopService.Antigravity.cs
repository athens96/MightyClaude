namespace MightyClaude.Core;

/// <summary>
/// Antigravity permission cards in the desktop service (M/MightyClaude/AppStore+AntigravityPermissions.swift): a headless agy turn is
/// refused what it would have asked about, so the pane asks once the turn is over. The card of each pane lives here, beside the rules a
/// local pane's continuation holds; an allow puts rules in agy's settings for a follow-up turn in the same conversation, and a remote
/// pane's host does that on its own computer, carried by the follow-up's start (<see cref="StartRunRequest.AntigravityGrant"/>).
/// </summary>
public sealed partial class DesktopService
{
    /// <summary>agy's settings, shared by this PC's panes and the runs it hosts for other computers.</summary>
    public AntigravityRuleLeases AntigravityRules { get; }
    private readonly object antigravityGate = new();
    private readonly AntigravityPermissionBoard antigravityBoard = new();
    /// <summary>Why a pane's last answer did not go through, shown on its card.</summary>
    private readonly Dictionary<string, string> antigravityErrors = new(StringComparer.Ordinal);
    /// <summary>Answers in flight, by pane, run and card.</summary>
    private readonly HashSet<string> antigravityResponses = new(StringComparer.Ordinal);
    /// <summary>The temporary rules each local pane's run holds, released when it ends.</summary>
    private readonly Dictionary<string, AntigravityRuleLeases.Lease> antigravityLeases = new(StringComparer.Ordinal);
    /// <summary>A pane's card came, went or changed its reason (by pane id). May be told under <c>sync</c>: subscribers hand work on (TryEnqueue).</summary>
    public event Action<string>? AntigravityCardChanged;

    public AntigravityPermissionRequest? AntigravityCard(string sessionId) { lock (antigravityGate) return antigravityBoard[sessionId]; }
    public AntigravityPermissionRequest? PendingAntigravityCard(string sessionId, string requestId, string runId) { lock (antigravityGate) return antigravityBoard.Pending(sessionId, requestId, runId); }
    public string? AntigravityError(string sessionId) { lock (antigravityGate) return antigravityErrors.GetValueOrDefault(sessionId); }
    private static string AnswerKey(string sessionId, AntigravityPermissionRequest card) => sessionId + "|" + card.RunId + "|" + card.Id;
    public bool AntigravityAnswering(string sessionId, AntigravityPermissionRequest card) { lock (antigravityGate) return antigravityResponses.Contains(AnswerKey(sessionId, card)); }
    /// <summary>The answer to <paramref name="card"/> starts; false when one is in flight already.</summary>
    public bool BeginAntigravityAnswer(string sessionId, AntigravityPermissionRequest card) { bool begun; lock (antigravityGate) begun = antigravityResponses.Add(AnswerKey(sessionId, card)); if (begun) AntigravityCardChanged?.Invoke(sessionId); return begun; }
    public void EndAntigravityAnswer(string sessionId, AntigravityPermissionRequest card) { lock (antigravityGate) antigravityResponses.Remove(AnswerKey(sessionId, card)); AntigravityCardChanged?.Invoke(sessionId); }
    /// <summary>The reason the card shows; null clears it.</summary>
    public void SetAntigravityError(string sessionId, string? reason)
    {
        lock (antigravityGate) { if (reason is null) antigravityErrors.Remove(sessionId); else antigravityErrors[sessionId] = reason; }
        AntigravityCardChanged?.Invoke(sessionId);
    }
    /// <summary>The card closes (denied, or its continuation is on its way).</summary>
    public AntigravityPermissionRequest? TakeAntigravityCard(string sessionId)
    {
        AntigravityPermissionRequest? card; lock (antigravityGate) card = antigravityBoard.Remove(sessionId);
        if (card is not null) AntigravityCardChanged?.Invoke(sessionId);
        return card;
    }
    /// <summary>Puts back a card whose answer could not run; the next run's end clears it as usual.</summary>
    public void RestoreAntigravityCard(string sessionId, AntigravityPermissionRequest card)
    {
        bool live; lock (sync) live = !closing && snapshot.Sessions.Any(s => s.Id == sessionId);
        if (!live) return;
        bool restored; lock (antigravityGate) restored = antigravityBoard.Restore(card, sessionId);
        if (restored) AntigravityCardChanged?.Invoke(sessionId);
    }
    /// <summary>Reads agy's settings the way an allow would, off the caller's thread, and changes nothing; throws why they cannot take a rule.</summary>
    public Task CheckAntigravitySettingsAsync() => Task.Run(AntigravityRules.Check);

    /// <summary>A card arrives before its run's final status; a later run's end clears it, and the run's own temporary rules leave.</summary>
    private void ReceiveAntigravity(RunEvent value)
    {
        var changed = false;
        if (value.Type == "antigravityPermission" && value.AntigravityPermission is { Actions.Count: > 0 } card)
        {
            bool agy; lock (sync) agy = !closing && snapshot.Sessions.Any(s => s.Id == value.SessionId && s.Kind == "claude" && s.Provider == "gemini");
            if (agy) { lock (antigravityGate) { antigravityBoard.Received(card, value.SessionId); antigravityErrors.Remove(value.SessionId); } changed = true; }
        }
        else if (value.Type == "status" && value.Status is "completed" or "error" or "stopped")
        {
            AntigravityRuleLeases.Lease? lease;
            lock (antigravityGate) { changed = antigravityBoard.RunEnded(value.SessionId); antigravityLeases.Remove(value.SessionId, out lease); }
            if (lease is not null) _ = Task.Run(() => AntigravityRules.Release(lease));
        }
        if (changed) AntigravityCardChanged?.Invoke(value.SessionId);
    }

    /// <summary>A local continuation's rules go in place before its run starts; held from here, the run's end (or its failed start) releases them.</summary>
    private async Task HoldAntigravityRules(StartRunRequest request)
    {
        if (request.AntigravityGrant is not { } grant || request.Provider != "gemini" || request.Kind != "claude") return;
        if (await Task.Run(() => AntigravityRules.Apply(grant)) is not { } lease) return;
        // Quitting meanwhile: the rules go again rather than outlive the app.
        bool ending; lock (sync) ending = closing;
        if (ending) { await Task.Run(() => AntigravityRules.Release(lease)); throw new OperationCanceledException(); }
        AntigravityRuleLeases.Lease? previous;
        lock (antigravityGate) { antigravityLeases.Remove(request.SessionId, out previous); antigravityLeases[request.SessionId] = lease; }
        if (previous is not null) _ = Task.Run(() => AntigravityRules.Release(previous));
    }

    /// <summary>An allowed card's continuation that could not start (a queued one too): the card comes back with the reason.</summary>
    private void ContinuationFailed(StartRunRequest request, string reason)
    {
        if (request.AntigravityCard is not { } card) return;
        RestoreAntigravityCard(request.SessionId, card);
        bool back; lock (antigravityGate) back = antigravityBoard[request.SessionId] == card;
        if (back) SetAntigravityError(request.SessionId, reason);
    }

    /// <summary>A closed pane's card and reason go with it.</summary>
    private void ForgetAntigravity(string sessionId)
    {
        bool had;
        lock (antigravityGate) { had = antigravityBoard.Remove(sessionId) is not null; antigravityErrors.Remove(sessionId); }
        if (had) AntigravityCardChanged?.Invoke(sessionId);
    }

    /// <summary>Quit: the temporary rules leave agy's settings with the runs that held them.</summary>
    private async Task ReleaseAntigravityRules()
    {
        lock (antigravityGate) antigravityLeases.Clear();
        await Task.Run(AntigravityRules.ReleaseAll);
    }
}
