using System.Text.Json;

namespace MightyClaude.Core;

/// <summary>
/// Where one user action is (natural submit A1, M/MightyCore/PendingDisplay.swift PendingState): nothing asked yet, waiting for its
/// answer, answered, or failed with the message the block shows in place. <c>Since</c> is when the action began, <c>At</c> when it
/// ended. A failure without <c>At</c> reads as failing now (native/contracts/fixtures/pending.json <c>displayRule</c>). The Mac, the
/// Windows page and the phone carry the same four states; <see cref="PendingDisplay"/> decides what each shows.
/// </summary>
public abstract record PendingState
{
    public sealed record Idle : PendingState;
    public sealed record Pending(DateTimeOffset Since) : PendingState;
    public sealed record Done(DateTimeOffset Since, DateTimeOffset At) : PendingState;
    public sealed record Failed(string Message, DateTimeOffset Since, DateTimeOffset? At) : PendingState;
}

/// <summary>What the pending area shows now: nothing, the shared loading, or the failure's message.</summary>
public abstract record PendingIndicator
{
    public sealed record Hidden : PendingIndicator;
    public sealed record Loading : PendingIndicator;
    public sealed record Failed(string Message) : PendingIndicator;
}

/// <summary>
/// The pending display rule, a pure function of the action's start, its end and now, shared with the Mac (MightyCore
/// PendingDisplay), the Windows page and the phone through native/contracts/fixtures/pending.json (<c>display</c>;
/// scripts/check-design-tokens.js holds the same rule as <c>pendingVisible</c>). Loading shows only once the action has lasted
/// <see cref="DesignMetrics.Motion.PendingShowAfter"/>; an action over by then never shows it; once shown it stays while the action
/// is pending and until showAfter + <see cref="DesignMetrics.Motion.PendingMinVisible"/> (inclusive), then hides at once. A failure
/// follows that rule until it ends, then shows its message at once with no hold and keeps it. Reduce Motion changes only the
/// movement, never this timing.
/// </summary>
public static class PendingDisplay
{
    /// <summary>The rule in milliseconds on one clock. <paramref name="endedAt"/> null: still pending, or for a failure a failure now.</summary>
    public static bool Visible(double startedAt, double? endedAt, double now, bool failed = false,
        int showAfter = DesignMetrics.Motion.PendingShowAfter, int minVisible = DesignMetrics.Motion.PendingMinVisible)
    {
        var shownAt = startedAt + showAfter;
        if (failed) return now >= (endedAt ?? now) || now >= shownAt;
        if (now < shownAt) return false;
        if (endedAt is not { } ended) return true;
        if (ended <= shownAt) return false;
        return now < ended || now <= shownAt + minVisible;
    }

    /// <summary>Whether <paramref name="state"/> shows its pending area at <paramref name="now"/>.</summary>
    public static bool Visible(PendingState state, DateTimeOffset now,
        int showAfter = DesignMetrics.Motion.PendingShowAfter, int minVisible = DesignMetrics.Motion.PendingMinVisible) =>
        Indicator(state, now, showAfter, minVisible) is not PendingIndicator.Hidden;

    /// <summary>What <paramref name="state"/> shows at <paramref name="now"/>: a failure shows loading until it ends (if the rule shows loading by then) and its message from then on.</summary>
    public static PendingIndicator Indicator(PendingState state, DateTimeOffset now,
        int showAfter = DesignMetrics.Motion.PendingShowAfter, int minVisible = DesignMetrics.Motion.PendingMinVisible)
    {
        switch (state)
        {
            case PendingState.Pending pending:
                return Visible(0, null, Milliseconds(now, pending.Since), false, showAfter, minVisible) ? new PendingIndicator.Loading() : new PendingIndicator.Hidden();
            case PendingState.Done done:
                return Visible(0, Milliseconds(done.At, done.Since), Milliseconds(now, done.Since), false, showAfter, minVisible) ? new PendingIndicator.Loading() : new PendingIndicator.Hidden();
            case PendingState.Failed failure:
                var elapsed = Milliseconds(now, failure.Since);
                var ended = failure.At is { } at ? Milliseconds(at, failure.Since) : elapsed;
                if (elapsed >= ended) return new PendingIndicator.Failed(failure.Message);
                return Visible(0, ended, elapsed, true, showAfter, minVisible) ? new PendingIndicator.Loading() : new PendingIndicator.Hidden();
            default:
                return new PendingIndicator.Hidden();
        }
    }

    /// <summary><paramref name="date"/> after <paramref name="since"/> in milliseconds; ticks are whole 100 ns, so a boundary named in milliseconds is exact.</summary>
    internal static double Milliseconds(DateTimeOffset date, DateTimeOffset since) => (date - since).Ticks / (double)TimeSpan.TicksPerMillisecond;
}

/// <summary>What a submit did that a page or a phone matches its waiting block by (natural submit B5): started, queued or steered.</summary>
public enum SubmitOutcome { Started, Queued, Steered }

/// <summary>
/// A host's answer to a submit as a page or a phone reads it (natural submit B5, M/MightyCore/PendingDisplay.swift SubmitResponse):
/// the Mac and Windows hosts' <c>/m1/sessions/{id}/submit</c> and the Windows page's <c>send</c> bridge, old and new (fixture
/// pending.json <c>submitResponses</c>, rule <c>submitRule</c>).
/// </summary>
/// <param name="Accepted">Whether the host took the request.</param>
/// <param name="EntryId">The request's user entry, so an optimistic block is matched to it; null from an old host or for a queued request, which has no block yet.</param>
/// <param name="RunId">The graph run the request started or went into.</param>
/// <param name="Outcome">What happened: started, queued or steered.</param>
public sealed record SubmitResponse(bool Accepted, string? EntryId = null, string? RunId = null, SubmitOutcome? Outcome = null)
{
    /// <summary>
    /// Reads a body; one that is missing or not an object reads as not accepted. The outcome is the body's <c>outcome</c> when it is
    /// started, queued or steered (any other word is ignored), else its <c>accepted</c> when that is one of those words (old hosts);
    /// accepted when there is an outcome or <c>accepted</c> is the JSON <c>true</c>. Ids count only as non-empty strings; everything
    /// else in the body is ignored. Kept for contract parity (pending.json <c>submitResponses</c>): no Windows code calls it.
    /// </summary>
    public static SubmitResponse Parse(JsonElement? json)
    {
        if (json is not { ValueKind: JsonValueKind.Object } body) return new(false);
        var outcome = Word(body, "outcome") ?? Word(body, "accepted");
        var acceptedTrue = body.TryGetProperty("accepted", out var accepted) && accepted.ValueKind == JsonValueKind.True;
        return new(outcome is not null || acceptedTrue, Id(body, "entryId"), Id(body, "runId"), outcome);
    }

    /// <summary>Reads a raw body; one that is not JSON reads as not accepted.</summary>
    public static SubmitResponse Parse(string text)
    {
        try { using var document = JsonDocument.Parse(text); return Parse(document.RootElement.Clone()); }
        catch (JsonException) { return new(false); }
    }

    /// <summary>The word a body carries for <paramref name="outcome"/>.</summary>
    public static string Word(SubmitOutcome outcome) => outcome switch
    {
        SubmitOutcome.Queued => "queued",
        SubmitOutcome.Steered => "steered",
        _ => "started",
    };

    private static SubmitOutcome? Word(JsonElement body, string name)
    {
        if (!body.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String) return null;
        switch (value.GetString())
        {
            case "started": return SubmitOutcome.Started;
            case "queued": return SubmitOutcome.Queued;
            case "steered": return SubmitOutcome.Steered;
            default: return null;
        }
    }

    private static string? Id(JsonElement body, string name) =>
        body.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : null;
}

/// <summary>
/// What a Windows submit did, as the host answers it (natural submit B5): <see cref="Outcome"/> is the composer's word (started,
/// queued, notStarted, worktree, answered, builtin, blocked or none; the phone's submit only ever started or queued), with the
/// request's user entry and graph run when it started a run at once (<see cref="RunStartReceipt"/>). A send that went out with
/// items delegation was holding answers without ids, as the Mac's does: another run may have started meanwhile, and a wrong id is
/// worse than none (the page and the phone then match by text and time).
/// </summary>
public sealed record SubmitReceipt(string Outcome, string? EntryId = null, string? RunId = null)
{
    /// <summary>The composer took the request: it runs, waits in the queue, runs in a new child worktree, answered a question or ran a built-in.</summary>
    public bool Accepted => Outcome is "started" or "queued" or "steered" or "worktree" or "answered" or "builtin";

    /// <summary>
    /// The phone's reply to <c>/m1/sessions/{id}/submit</c> and <c>guided</c>: <c>accepted</c> stays the outcome word, which older
    /// phones read, beside <c>outcome</c> and the ids; a missing id is left out (M/MightyCore/Remote/MobileRemoteModels.swift MobileSubmitResult).
    /// </summary>
    public MobileSubmitReply MobileReply() => new(1, Outcome, Outcome, EntryId, RunId);

    /// <summary>
    /// The page's <c>send</c> answer: <c>accepted</c> as a boolean (<see cref="Accepted"/>), the composer's <c>outcome</c> word,
    /// the ids when known, and the page's own <paramref name="clientId"/> echoed when it is an identifier, beside the draft and the
    /// composer state the page already read.
    /// </summary>
    public ComposerSendAnswer SendAnswer(string? clientId, string draft, object? state) =>
        new(Accepted, Outcome, EntryId, RunId, Wire.Identifier(clientId) ? clientId : null, draft, state);
}

/// <summary>The phone's submit reply (<see cref="SubmitReceipt.MobileReply"/>); <see cref="Wire.Json"/> leaves a null id out.</summary>
public sealed record MobileSubmitReply(int Protocol, string Accepted, string Outcome, string? EntryId, string? RunId);

/// <summary>The page's <c>send</c> answer (<see cref="SubmitReceipt.SendAnswer"/>); <see cref="Wire.Json"/> leaves a null id out.</summary>
public sealed record ComposerSendAnswer(bool Accepted, string Outcome, string? EntryId, string? RunId, string? ClientId, string Draft, object? State);

/// <summary>
/// Where a view moves when a request block appears (natural submit B2; fixture pending.json <c>scrollTargets</c>, rule
/// <c>scrollRule</c>; M/MightyCore/PendingDisplay.swift SubmitScroll). The caller decides <c>nearBottom</c> with its own threshold
/// (Windows 32 px from the bottom; in the diagram the follow camera being on; in the timeline and the pipeline being at the newest end).
/// </summary>
public static class SubmitScroll
{
    /// <summary>The four views of an agent pane: the default transcript, the diagram, the timeline and the pipeline (structure).</summary>
    public enum View { Basic, Diagram, Timeline, Pipeline }

    public enum Target
    {
        /// <summary>Move to the new request block, whatever the position.</summary>
        ToRequest,
        /// <summary>Today's follow: keep the newest in view.</summary>
        Follow,
        /// <summary>Stay where the user is reading.</summary>
        None,
    }

    /// <param name="Scroll">Where the view moves.</param>
    /// <param name="ResetBoard">The pipeline goes back to the newest board first.</param>
    public sealed record Decision(Target Scroll, bool ResetBoard);

    /// <summary>
    /// The view a pane shows now. <paramref name="showsMighty"/> is whether it draws the Mighty view at all (the caller's own rule:
    /// <c>AgentViewMode</c> "mighty" on a provider that has it); false is the default transcript, whatever the mode says.
    /// <paramref name="mightyViewMode"/> is the pane's <c>GraphViewMode</c>, read as <see cref="MightyTimeline.Mode"/> reads it (an
    /// unknown or missing word is the diagram).
    /// </summary>
    public static View ViewFor(bool showsMighty, string? mightyViewMode)
    {
        if (!showsMighty) return View.Basic;
        return MightyTimeline.NormalizeMode(mightyViewMode) switch
        {
            "timeline" => View.Timeline,
            "structure" => View.Pipeline,
            _ => View.Diagram,
        };
    }

    /// <summary>
    /// <paramref name="own"/>: this composer's Enter, a queued request that now runs, run next or a selected Ctrl+Enter child. An own
    /// request always moves to its block, back to the newest pipeline board when an older one is chosen. Anything else follows only
    /// near the bottom, and never on an older pipeline board, which the user chose (its badge says there is more).
    /// <paramref name="chosenBoardIsNewest"/> matters only in the pipeline.
    /// </summary>
    public static Decision Decide(View view, bool own, bool chosenBoardIsNewest, bool nearBottom)
    {
        var olderBoard = view == View.Pipeline && !chosenBoardIsNewest;
        if (own) return new(Target.ToRequest, olderBoard);
        if (olderBoard) return new(Target.None, false);
        return new(nearBottom ? Target.Follow : Target.None, false);
    }
}
