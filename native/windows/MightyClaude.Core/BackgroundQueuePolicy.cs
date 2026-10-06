namespace MightyClaude.Core;

/// <summary>
/// How new input meets a run that is busy, decided in one place for the composer and the phone
/// (M/BackgroundQueuePolicy.swift). A turn that is over but still runs background work takes new input in the
/// same process — except in a style that starts every request in plan mode (docs/mighty-styles.md §1.17.4),
/// whose new request waits and then starts fresh in plan mode.
/// </summary>
public static class BackgroundQueuePolicy
{
    /// <summary>Enter (or Ctrl+Enter, <paramref name="steering"/>) in the composer joins the running turn.</summary>
    public static bool ComposerJoins(bool steering, BackgroundWork? work, bool launchesInPlan) => steering || work is { WaitingOnBackground: true } && !launchesInPlan;

    /// <summary>The phone's send steers unless it asked to queue — and never into a turn that only waits on background work in a plan-mode style.</summary>
    public static bool PhoneSteers(string? mode, BackgroundWork? work, bool launchesInPlan) => mode != "queue" && !(work is { WaitingOnBackground: true } && launchesInPlan);

    /// <summary>The queue's notice: what is queued starts only once the background work ends.</summary>
    public static bool WaitsOnBackground(BackgroundWork? work, bool launchesInPlan, int queued) => launchesInPlan && queued > 0 && work is { WaitingOnBackground: true };

    /// <summary>Stop on a run whose turn is already over (it only waited on background work) keeps the queue and starts its first request next.</summary>
    public static bool StopKeepsQueue(BackgroundWork? work) => work is { TurnEnded: true };

    /// <summary>The permission mode a queued item records for its run.</summary>
    public static string? QueuedOverride(bool launchesInPlan) => launchesInPlan ? "plan" : null;
}
