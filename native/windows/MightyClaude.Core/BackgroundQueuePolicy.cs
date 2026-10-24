namespace MightyClaude.Core;

/// <summary>
/// How new input meets a run that is busy, decided in one place for the composer and the phone
/// (M/BackgroundQueuePolicy.swift). New input to a busy pane always waits in the queue — while the turn runs, starts, is
/// held for an update, or is over but still waits on background work — and starts as the next request; nothing a person
/// sends joins the running turn. A style that starts every request in plan mode (docs/mighty-styles.md §1.17.4) records
/// plan mode on what it queues.
/// </summary>
public static class BackgroundQueuePolicy
{
    /// <summary>The queue's notice: what is queued starts only once the background work ends.</summary>
    public static bool WaitsOnBackground(BackgroundWork? work, int queued) => queued > 0 && work is { WaitingOnBackground: true };

    /// <summary>
    /// Run next hands the human's first queued request into the running Claude process as a new turn, at once: the pane's only
    /// work left is background work after its turn ended, the run is local and not stopping, and the request has no attachments
    /// (M/BackgroundQueuePolicy.swift steersRunNext). A refused steer leaves it queued.
    /// </summary>
    public static bool SteersRunNext(BackgroundWork? work, bool localClaude, bool stopping, int queued, bool firstHasAttachments) =>
        queued > 0 && !firstHasAttachments && localClaude && !stopping && work is { WaitingOnBackground: true };

    /// <summary>Stop on a run whose turn is already over (it only waited on background work) keeps the queue and starts its first request next.</summary>
    public static bool StopKeepsQueue(BackgroundWork? work) => work is { TurnEnded: true };

    /// <summary>The permission mode a queued item records for its run.</summary>
    public static string? QueuedOverride(bool launchesInPlan) => launchesInPlan ? "plan" : null;
}
