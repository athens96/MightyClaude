import Foundation

/// The rules of the fully automatic app update, kept free of the app so they
/// can be tested and mirrored one to one by the Windows client
/// (`MightyClaude.Core/AppUpdateAutoPolicy.cs`: the same constants and the
/// same names in C# casing — `AppIdleFacts`, `AppIdleBlocker`, `CheckDue`,
/// `IdleBlockers`, `RestartOffer`, `Later`).
///
/// Everything here sits behind the existing "automatic" switch (on by
/// default):
/// 1. **Checking.** At launch and then on every `tick`, the app asks
///    `checkDue`; a check is due `checkInterval` after the later of the last
///    successful check (persisted) and the last attempt (kept in memory, so a
///    failed check waits for the next cycle instead of retrying every tick).
/// 2. **Downloading.** A newer signed version found by a check is downloaded,
///    verified and staged without asking (the app's job, not this file's).
/// 3. **Installing.** A staged update is installed when the app quits (the
///    helper runs with `relaunch: false`), or, when the app has been idle,
///    after a `countdown`-second banner offering "restart now" and "later"
///    (`restartOffer`, driven every `countdownTick` while counting down).
///    "Later" snoozes the offer for `snooze`.
public enum AppUpdateAuto {
    /// How long after the last check (or attempt) the next one is due.
    public static let checkInterval: TimeInterval = 6 * 60 * 60
    /// How often the running app looks whether a check is due or the app is idle.
    public static let tick: TimeInterval = 10 * 60
    /// Keyboard or mouse input this recent means the user is at the app.
    public static let userIdleDelay: TimeInterval = 5 * 60
    /// A run that ended, or input sent to an agent, this recently means work may follow.
    public static let agentIdleDelay: TimeInterval = 3 * 60
    /// The restart banner's countdown.
    public static let countdown: TimeInterval = 60
    /// How often the app re-evaluates while the banner counts down.
    public static let countdownTick: TimeInterval = 1
    /// A countdown found more than this past its end was not watched to the
    /// end (the Mac slept through it): it is cancelled, never acted on.
    public static let countdownGrace: TimeInterval = 5
    /// "Later" keeps the banner away this long.
    public static let snooze: TimeInterval = 4 * 60 * 60

    /// Whether an automatic check should run now.
    /// - Parameters:
    ///   - enabled: the "automatic" switch.
    ///   - hasKey: the build can verify a manifest (it carries the public key
    ///     and knows the manifest address); unsigned builds never check on their own.
    ///   - lastCheck: the last successful check, persisted across launches.
    ///   - lastAttempt: the last attempt this run, successful or not (in memory).
    /// A time later than `now` (the clock was set back) is ignored, so a
    /// future `lastCheck` alone does not stop checks until the clock catches
    /// up, while a past `lastAttempt` still holds the next try back.
    public static func checkDue(enabled: Bool, hasKey: Bool, lastCheck: Date?, lastAttempt: Date?, now: Date) -> Bool {
        guard enabled, hasKey else { return false }
        guard let last = [lastCheck, lastAttempt].compactMap({ $0 }).filter({ $0 <= now }).max() else { return true }
        return now.timeIntervalSince(last) >= checkInterval
    }

    /// Every reason the app is not idle, in `AppIdleBlocker.allCases` order;
    /// empty when the app may restart for an update.
    public static func idleBlockers(_ facts: AppIdleFacts, now: Date) -> [AppIdleBlocker] {
        /// A time in the future (clock set back) counts as just now.
        func recent(_ date: Date?, within delay: TimeInterval) -> Bool {
            guard let date else { return false }
            return now.timeIntervalSince(date) < delay
        }
        return AppIdleBlocker.allCases.filter { blocker in
            switch blocker {
            case .agentRun: return facts.agentRuns > 0
            case .pendingCard: return facts.pendingCards > 0
            case .queuedInput: return facts.queuedInputs > 0
            case .draft: return facts.drafts > 0
            case .terminal: return facts.terminals > 0
            case .cliWork: return facts.cliWork
            case .remote: return facts.screenSharing || facts.phoneUploads > 0 || facts.remoteHostRuns > 0 || facts.sharingWorkspaces
            case .modal: return facts.blockingModal
            case .userActive: return recent(facts.lastUserInput, within: userIdleDelay)
            case .recentAgentActivity: return recent(facts.lastAgentActivity, within: agentIdleDelay)
            }
        }
    }

    public static func isIdle(_ facts: AppIdleFacts, now: Date) -> Bool { idleBlockers(facts, now: now).isEmpty }

    /// One step of the idle-restart offer, called on every tick (every
    /// `countdownTick` while counting down) with the current state.
    /// - Parameters:
    ///   - ready: an update is staged and no install is under way.
    ///   - automatic: the "automatic" switch.
    ///   - blockers: `idleBlockers(...)` right now.
    /// Rules: a ready, automatic app with no blocker at all starts a
    /// `countdown`; the countdown restarts the app when it runs out, and is
    /// cancelled quietly as soon as any blocker other than `.userActive`
    /// appears, the switch goes off or the update is no longer ready. Input
    /// alone does not cancel a running countdown, so the pointer can reach the
    /// banner's buttons ("restart now", "later"), which the app handles itself.
    /// A countdown found more than `countdownGrace` past its end (the Mac slept
    /// through it) is cancelled too, so waking never restarts the app at once;
    /// the next idle step starts a fresh one.
    /// A snooze holds until it ends, then the app is asked again the next time
    /// it is idle.
    public static func restartOffer(_ state: AppUpdateRestartState, ready: Bool, automatic: Bool, blockers: [AppIdleBlocker], now: Date) -> AppUpdateRestartDecision {
        switch state {
        case .countingDown(let until):
            guard ready, automatic, blockers.allSatisfy({ $0 == .userActive }), now.timeIntervalSince(until) <= countdownGrace else {
                return AppUpdateRestartDecision(state: .idle, action: .cancel)
            }
            return now >= until ? AppUpdateRestartDecision(state: .idle, action: .restart) : AppUpdateRestartDecision(state: state, action: .none)
        case .snoozed(let until) where now < until:
            return AppUpdateRestartDecision(state: state, action: .none)
        case .snoozed, .idle:
            guard ready, automatic, blockers.isEmpty else { return AppUpdateRestartDecision(state: .idle, action: .none) }
            return AppUpdateRestartDecision(state: .countingDown(until: now.addingTimeInterval(countdown)), action: .startCountdown)
        }
    }

    /// The state after the user picks "later" on the banner.
    public static func later(now: Date) -> AppUpdateRestartState { .snoozed(until: now.addingTimeInterval(snooze)) }
}

/// What the app is doing right now, as counts and flags (plus the two times
/// the idle delays are measured from). The app fills it in; `AppUpdateAuto`
/// decides. Each field feeds one `AppIdleBlocker`.
public struct AppIdleFacts: Sendable, Equatable {
    /// `.agentRun`: agent sessions running (any kind but `shell`, remote ones
    /// included), runs waiting to start, runs held for an update, delegation
    /// runs ending and delegations waiting to start.
    public var agentRuns: Int
    /// `.pendingCard`: permission prompts, Antigravity permission cards and
    /// requests to open a web page that wait for the user.
    public var pendingCards: Int
    /// `.queuedInput`: queued inputs of any pane. Inputs held by delegation are
    /// left out, since they survive a restart on disk.
    public var queuedInputs: Int
    /// `.draft`: composers with non-blank text, attachment drafts and
    /// attachments still importing.
    public var drafts: Int
    /// `.terminal`: shell sessions running, live agent processes, setup runs
    /// and terminals, and CLI sign-ins in progress.
    public var terminals: Int
    /// `.cliWork`: a CLI update, plugin change, model reset or tool install in progress.
    public var cliWork: Bool
    /// `.remote`: this Mac shares its screen.
    public var screenSharing: Bool
    /// `.remote`: files the phone is uploading.
    public var phoneUploads: Int
    /// `.remote`: runs this Mac hosts for another computer.
    public var remoteHostRuns: Int
    /// `.remote`: this Mac shares workspaces with another computer, runs or
    /// not; a restart would turn sharing off and revoke the client's key.
    public var sharingWorkspaces: Bool
    /// `.modal`: a blocking modal, the setup sheet or the settings are open.
    public var blockingModal: Bool
    /// `.userActive`: the last keyboard or mouse input anywhere in the session.
    public var lastUserInput: Date?
    /// `.recentAgentActivity`: the last time a run ended or input was sent to an agent.
    public var lastAgentActivity: Date?

    public init(agentRuns: Int = 0, pendingCards: Int = 0, queuedInputs: Int = 0, drafts: Int = 0, terminals: Int = 0, cliWork: Bool = false,
                screenSharing: Bool = false, phoneUploads: Int = 0, remoteHostRuns: Int = 0, sharingWorkspaces: Bool = false, blockingModal: Bool = false,
                lastUserInput: Date? = nil, lastAgentActivity: Date? = nil) {
        self.agentRuns = agentRuns; self.pendingCards = pendingCards; self.queuedInputs = queuedInputs; self.drafts = drafts
        self.terminals = terminals; self.cliWork = cliWork; self.screenSharing = screenSharing; self.phoneUploads = phoneUploads
        self.remoteHostRuns = remoteHostRuns; self.sharingWorkspaces = sharingWorkspaces; self.blockingModal = blockingModal; self.lastUserInput = lastUserInput; self.lastAgentActivity = lastAgentActivity
    }
}

/// Why the app is not idle. Raw values are stable (logs, the Windows mirror).
public enum AppIdleBlocker: String, CaseIterable, Sendable {
    case agentRun, pendingCard, queuedInput, draft, terminal, cliWork, remote, modal, userActive, recentAgentActivity
}

/// Where the idle-restart offer stands.
public enum AppUpdateRestartState: Sendable, Equatable {
    /// No banner; the app is offered a restart the next time it is idle.
    case idle
    /// The banner is up; the app restarts at `until` unless something other
    /// than user input got in the way.
    case countingDown(until: Date)
    /// The user chose "later"; no banner before `until`.
    case snoozed(until: Date)
}

/// What the app does after a `restartOffer` step.
public enum AppUpdateRestartAction: Sendable, Equatable {
    /// Nothing changes on screen.
    case none
    /// Show the countdown banner.
    case startCountdown
    /// Hide the banner and install the staged update, relaunching the app.
    /// (The banner's own "restart now" does the same without waiting.)
    case restart
    /// Hide the banner without a word.
    case cancel
}

public struct AppUpdateRestartDecision: Sendable, Equatable {
    public var state: AppUpdateRestartState
    public var action: AppUpdateRestartAction
    public init(state: AppUpdateRestartState, action: AppUpdateRestartAction) { self.state = state; self.action = action }
}
