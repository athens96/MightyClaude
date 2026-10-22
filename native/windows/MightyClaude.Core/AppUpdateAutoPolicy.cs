namespace MightyClaude.Core;

/// The rules of the fully automatic app update, the Windows mirror of
/// M/AppUpdateAuto.swift and M/AppUpdateAutoSteps.swift: the same constants and
/// the same names in C# casing (`AppIdleFacts`, `AppIdleBlocker`, `CheckDue`,
/// `IdleBlockers`, `RestartOffer`, `Later`, `InstallsOnQuit`).
///
/// Everything here sits behind the existing "automatic" switch (on by default):
/// 1. **Checking.** At launch and then on every `Tick`, the app asks
///    `CheckDue`; a check is due `CheckInterval` after the later of the last
///    successful check (saved) and the last attempt (kept in memory, so a
///    failed check waits for the next cycle instead of retrying every tick).
/// 2. **Downloading.** A newer signed version found by a check is downloaded,
///    verified and staged without asking (`AppUpdateCoordinator`).
/// 3. **Installing.** A staged update is installed when the app quits (the
///    helper runs with `--relaunch 0`), or, when the app has been idle, after a
///    `Countdown` banner offering "restart now" and "later" (`RestartOffer`,
///    driven every `CountdownTick` while counting down). "Later" snoozes the
///    offer for `Snooze`.
public static class AppUpdateAuto
{
    /// How long after the last check (or attempt) the next one is due.
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);
    /// How often the running app looks whether a check is due or the app is idle.
    public static readonly TimeSpan Tick = TimeSpan.FromMinutes(10);
    /// Keyboard or mouse input this recent means the user is at the app.
    public static readonly TimeSpan UserIdleDelay = TimeSpan.FromMinutes(5);
    /// A run that ended, or input sent to an agent, this recently means work may follow.
    public static readonly TimeSpan AgentIdleDelay = TimeSpan.FromMinutes(3);
    /// The restart banner's countdown.
    public static readonly TimeSpan Countdown = TimeSpan.FromSeconds(60);
    /// How often the app re-evaluates while the banner counts down.
    public static readonly TimeSpan CountdownTick = TimeSpan.FromSeconds(1);
    /// "Later" keeps the banner away this long.
    public static readonly TimeSpan Snooze = TimeSpan.FromHours(4);

    /// Whether an automatic check should run now. `hasKey`: the build can verify
    /// a manifest; a key-less build never checks on its own. A time later than
    /// `now` (the clock was set back) is ignored, so a future `lastCheck` alone
    /// does not stop checks until the clock catches up, while a past
    /// `lastAttempt` still holds the next try back.
    public static bool CheckDue(bool enabled, bool hasKey, DateTimeOffset? lastCheck, DateTimeOffset? lastAttempt, DateTimeOffset now)
    {
        if (!enabled || !hasKey) return false;
        var past = new[] { lastCheck, lastAttempt }.Where(date => date is { } value && value <= now).Select(date => date!.Value).ToList();
        if (past.Count == 0) return true;
        return now - past.Max() >= CheckInterval;
    }

    /// Every reason the app is not idle, in `AppIdleBlocker` order; empty when
    /// the app may restart for an update.
    public static IReadOnlyList<AppIdleBlocker> IdleBlockers(AppIdleFacts facts, DateTimeOffset now)
    {
        // A time in the future (clock set back) counts as just now.
        bool Recent(DateTimeOffset? date, TimeSpan delay) => date is { } value && now - value < delay;
        return Enum.GetValues<AppIdleBlocker>().Where(blocker => blocker switch
        {
            AppIdleBlocker.AgentRun => facts.AgentRuns > 0,
            AppIdleBlocker.PendingCard => facts.PendingCards > 0,
            AppIdleBlocker.QueuedInput => facts.QueuedInputs > 0,
            AppIdleBlocker.Draft => facts.Drafts > 0,
            AppIdleBlocker.Terminal => facts.Terminals > 0,
            AppIdleBlocker.CliWork => facts.CliWork,
            AppIdleBlocker.Remote => facts.ScreenSharing || facts.PhoneUploads > 0 || facts.RemoteHostRuns > 0,
            AppIdleBlocker.Modal => facts.BlockingModal,
            AppIdleBlocker.UserActive => Recent(facts.LastUserInput, UserIdleDelay),
            AppIdleBlocker.RecentAgentActivity => Recent(facts.LastAgentActivity, AgentIdleDelay),
            _ => false,
        }).ToList();
    }

    public static bool IsIdle(AppIdleFacts facts, DateTimeOffset now) => IdleBlockers(facts, now).Count == 0;

    /// One step of the idle-restart offer, called on every tick (every
    /// `CountdownTick` while counting down) with the current state.
    /// `ready`: an update is staged and no install is under way.
    /// Rules: a ready, automatic app with no blocker at all starts a
    /// `Countdown`; the countdown restarts the app when it runs out, and is
    /// cancelled quietly as soon as any blocker other than `UserActive`
    /// appears, the switch goes off or the update is no longer ready. Input
    /// alone does not cancel a running countdown, so the pointer can reach the
    /// banner's buttons. A snooze holds until it ends, then the app is asked
    /// again the next time it is idle.
    public static AppUpdateRestartDecision RestartOffer(
        AppUpdateRestartState state, bool ready, bool automatic, IReadOnlyList<AppIdleBlocker> blockers, DateTimeOffset now)
    {
        switch (state.Kind)
        {
            case AppUpdateRestartKind.CountingDown:
                if (!ready || !automatic || blockers.Any(blocker => blocker != AppIdleBlocker.UserActive))
                    return new(AppUpdateRestartState.Idle, AppUpdateRestartAction.Cancel);
                return now >= state.Until
                    ? new(AppUpdateRestartState.Idle, AppUpdateRestartAction.Restart)
                    : new(state, AppUpdateRestartAction.None);
            case AppUpdateRestartKind.Snoozed when now < state.Until:
                return new(state, AppUpdateRestartAction.None);
            default:
                if (!ready || !automatic || blockers.Count > 0) return new(AppUpdateRestartState.Idle, AppUpdateRestartAction.None);
                return new(AppUpdateRestartState.CountingDown(now + Countdown), AppUpdateRestartAction.StartCountdown);
        }
    }

    /// The state after the user picks "later" on the banner.
    public static AppUpdateRestartState Later(DateTimeOffset now) => AppUpdateRestartState.Snoozed(now + Snooze);

    /// Whether quitting the app installs the staged update (the helper runs
    /// with `--relaunch 0`). `ready`: staged and no install under way (an
    /// install already started quits on its own). `automatic` off: only
    /// "restart now" installs. `hasKey`: the build verifies updates and this is
    /// not a smoke run. `poweringOff`: Windows is signing out, restarting or
    /// shutting down; the swap could be cut off half-way, so the update waits
    /// for the next quit.
    public static bool InstallsOnQuit(bool ready, bool automatic, bool hasKey, bool poweringOff) =>
        ready && automatic && hasKey && !poweringOff;
}

/// What the app is doing right now, as counts and flags (plus the two times the
/// idle delays are measured from). The app fills it in; `AppUpdateAuto`
/// decides. Each field feeds one `AppIdleBlocker`.
public sealed record AppIdleFacts
{
    /// `AgentRun`: agent sessions running (any kind but a shell, remote ones
    /// included), runs waiting to start, runs held for an update, delegation
    /// runs ending and delegations waiting to start.
    public int AgentRuns { get; init; }
    /// `PendingCard`: permission prompts, questions, plan cards and requests to
    /// open a web page that wait for the user.
    public int PendingCards { get; init; }
    /// `QueuedInput`: queued inputs of any pane, without those delegation holds
    /// (they survive a restart on disk).
    public int QueuedInputs { get; init; }
    /// `Draft`: composers with non-blank text, attachment drafts and attachments still importing.
    public int Drafts { get; init; }
    /// `Terminal`: shell sessions running a command, live agent processes,
    /// setup runs and terminals, and CLI sign-ins in progress.
    public int Terminals { get; init; }
    /// `CliWork`: a CLI update, plugin change, model reset or tool install in progress.
    public bool CliWork { get; init; }
    /// `Remote`: this computer shares its screen.
    public bool ScreenSharing { get; init; }
    /// `Remote`: files the phone is uploading.
    public int PhoneUploads { get; init; }
    /// `Remote`: runs this computer hosts for another computer.
    public int RemoteHostRuns { get; init; }
    /// `Modal`: a blocking dialog, the setup screen or the settings are open.
    public bool BlockingModal { get; init; }
    /// `UserActive`: the last keyboard or mouse input anywhere in the session.
    public DateTimeOffset? LastUserInput { get; init; }
    /// `RecentAgentActivity`: the last time a run ended or input was sent to an agent.
    public DateTimeOffset? LastAgentActivity { get; init; }
}

/// Why the app is not idle, in the macOS order (M/AppUpdateAuto.swift `AppIdleBlocker`).
public enum AppIdleBlocker
{
    AgentRun, PendingCard, QueuedInput, Draft, Terminal, CliWork, Remote, Modal, UserActive, RecentAgentActivity,
}

public enum AppUpdateRestartKind
{
    /// No banner; the app is offered a restart the next time it is idle.
    Idle,
    /// The banner is up; the app restarts at `Until` unless something other than user input got in the way.
    CountingDown,
    /// The user chose "later"; no banner before `Until`.
    Snoozed,
}

/// Where the idle-restart offer stands. `Until` is unused while idle.
public sealed record AppUpdateRestartState(AppUpdateRestartKind Kind, DateTimeOffset Until = default)
{
    public static AppUpdateRestartState Idle { get; } = new(AppUpdateRestartKind.Idle);
    public static AppUpdateRestartState CountingDown(DateTimeOffset until) => new(AppUpdateRestartKind.CountingDown, until);
    public static AppUpdateRestartState Snoozed(DateTimeOffset until) => new(AppUpdateRestartKind.Snoozed, until);
}

/// What the app does after a `RestartOffer` step.
public enum AppUpdateRestartAction
{
    /// Nothing changes on screen.
    None,
    /// Show the countdown banner.
    StartCountdown,
    /// Hide the banner and install the staged update, relaunching the app.
    Restart,
    /// Hide the banner without a word.
    Cancel,
}

public sealed record AppUpdateRestartDecision(AppUpdateRestartState State, AppUpdateRestartAction Action);
