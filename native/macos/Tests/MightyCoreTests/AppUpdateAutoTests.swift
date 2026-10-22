import Foundation
import Testing
@testable import MightyCore

struct AppUpdateAutoTests {
    private let now = Date(timeIntervalSince1970: 1_800_000_000)

    @Test func theNumbersAreThePlannedOnes() {
        #expect(AppUpdateAuto.checkInterval == 6 * 60 * 60)
        #expect(AppUpdateAuto.tick == 10 * 60)
        #expect(AppUpdateAuto.userIdleDelay == 5 * 60)
        #expect(AppUpdateAuto.agentIdleDelay == 3 * 60)
        #expect(AppUpdateAuto.countdown == 60)
        #expect(AppUpdateAuto.countdownTick == 1)
        #expect(AppUpdateAuto.countdownGrace == 5)
        #expect(AppUpdateAuto.snooze == 4 * 60 * 60)
    }

    // MARK: checkDue

    @Test func aCheckIsDueAtFirstLaunchAndSixHoursAfterTheLastOne() {
        let interval = AppUpdateAuto.checkInterval
        #expect(AppUpdateAuto.checkDue(enabled: true, hasKey: true, lastCheck: nil, lastAttempt: nil, now: now))
        #expect(!AppUpdateAuto.checkDue(enabled: true, hasKey: true, lastCheck: now.addingTimeInterval(-interval + 1), lastAttempt: nil, now: now))
        #expect(AppUpdateAuto.checkDue(enabled: true, hasKey: true, lastCheck: now.addingTimeInterval(-interval), lastAttempt: nil, now: now))
        // A clock set back since the last check does not stop checks for good…
        #expect(AppUpdateAuto.checkDue(enabled: true, hasKey: true, lastCheck: now.addingTimeInterval(interval * 10), lastAttempt: nil, now: now))
        // …but an attempt since then still holds the next one back.
        #expect(!AppUpdateAuto.checkDue(enabled: true, hasKey: true, lastCheck: now.addingTimeInterval(interval * 10), lastAttempt: now.addingTimeInterval(-AppUpdateAuto.tick), now: now))
        #expect(AppUpdateAuto.checkDue(enabled: true, hasKey: true, lastCheck: now.addingTimeInterval(interval * 10), lastAttempt: now.addingTimeInterval(-interval), now: now))
    }

    @Test func aFailedAttemptIsRetriedAtTheNextCycleNotEveryTick() {
        let interval = AppUpdateAuto.checkInterval
        let lastCheck = now.addingTimeInterval(-interval * 2) // long due
        let attempt = now
        // Ticks after the failed attempt do not try again…
        for step in 1..<Int(interval / AppUpdateAuto.tick) {
            let later = attempt.addingTimeInterval(Double(step) * AppUpdateAuto.tick)
            #expect(!AppUpdateAuto.checkDue(enabled: true, hasKey: true, lastCheck: lastCheck, lastAttempt: attempt, now: later))
        }
        // …the next cycle does.
        #expect(AppUpdateAuto.checkDue(enabled: true, hasKey: true, lastCheck: lastCheck, lastAttempt: attempt, now: attempt.addingTimeInterval(interval)))
        // The later of the two times counts, whichever it is.
        #expect(!AppUpdateAuto.checkDue(enabled: true, hasKey: true, lastCheck: now.addingTimeInterval(-60), lastAttempt: now.addingTimeInterval(-interval * 3), now: now))
    }

    @Test func noCheckWhenSwitchedOffOrWithoutAKey() {
        #expect(!AppUpdateAuto.checkDue(enabled: false, hasKey: true, lastCheck: nil, lastAttempt: nil, now: now))
        #expect(!AppUpdateAuto.checkDue(enabled: true, hasKey: false, lastCheck: nil, lastAttempt: nil, now: now))
        #expect(!AppUpdateAuto.checkDue(enabled: false, hasKey: false, lastCheck: now.addingTimeInterval(-AppUpdateAuto.checkInterval * 4), lastAttempt: nil, now: now))
    }

    // MARK: idleBlockers

    @Test func nothingGoingOnMeansIdle() {
        #expect(AppUpdateAuto.idleBlockers(AppIdleFacts(), now: now).isEmpty)
        #expect(AppUpdateAuto.isIdle(AppIdleFacts(), now: now))
        let longAgo = AppIdleFacts(lastUserInput: now.addingTimeInterval(-AppUpdateAuto.userIdleDelay),
                                   lastAgentActivity: now.addingTimeInterval(-AppUpdateAuto.agentIdleDelay))
        #expect(AppUpdateAuto.idleBlockers(longAgo, now: now).isEmpty)
    }

    @Test func eachFactBlocksOnlyItsOwnReason() {
        let cases: [(AppIdleFacts, AppIdleBlocker)] = [
            (AppIdleFacts(agentRuns: 1), .agentRun),
            (AppIdleFacts(pendingCards: 2), .pendingCard),
            (AppIdleFacts(queuedInputs: 1), .queuedInput),
            (AppIdleFacts(drafts: 1), .draft),
            (AppIdleFacts(terminals: 1), .terminal),
            (AppIdleFacts(cliWork: true), .cliWork),
            (AppIdleFacts(screenSharing: true), .remote),
            (AppIdleFacts(phoneUploads: 1), .remote),
            (AppIdleFacts(remoteHostRuns: 3), .remote),
            (AppIdleFacts(sharingWorkspaces: true), .remote),
            (AppIdleFacts(blockingModal: true), .modal),
            (AppIdleFacts(lastUserInput: now.addingTimeInterval(-AppUpdateAuto.userIdleDelay + 1)), .userActive),
            (AppIdleFacts(lastAgentActivity: now.addingTimeInterval(-AppUpdateAuto.agentIdleDelay + 1)), .recentAgentActivity),
        ]
        for (facts, blocker) in cases {
            #expect(AppUpdateAuto.idleBlockers(facts, now: now) == [blocker], "\(blocker)")
            #expect(!AppUpdateAuto.isIdle(facts, now: now))
        }
        // Every reason has a case above.
        #expect(Set(cases.map(\.1)) == Set(AppIdleBlocker.allCases))
    }

    @Test func blockersComeInTheirFixedOrderAndTimesInTheFutureCountAsActive() {
        var facts = AppIdleFacts(agentRuns: 1, drafts: 1, blockingModal: true)
        facts.lastUserInput = now.addingTimeInterval(30) // clock moved back
        #expect(AppUpdateAuto.idleBlockers(facts, now: now) == [.agentRun, .draft, .modal, .userActive])
        #expect(AppIdleBlocker.allCases.map(\.rawValue) == ["agentRun", "pendingCard", "queuedInput", "draft", "terminal", "cliWork", "remote", "modal", "userActive", "recentAgentActivity"])
    }

    // MARK: restartOffer

    @Test func anIdleAppCountsDownThenRestarts() {
        let start = AppUpdateAuto.restartOffer(.idle, ready: true, automatic: true, blockers: [], now: now)
        let until = now.addingTimeInterval(AppUpdateAuto.countdown)
        #expect(start == AppUpdateRestartDecision(state: .countingDown(until: until), action: .startCountdown))
        let midway = AppUpdateAuto.restartOffer(start.state, ready: true, automatic: true, blockers: [], now: now.addingTimeInterval(30))
        #expect(midway == AppUpdateRestartDecision(state: .countingDown(until: until), action: .none))
        let done = AppUpdateAuto.restartOffer(midway.state, ready: true, automatic: true, blockers: [], now: until)
        #expect(done == AppUpdateRestartDecision(state: .idle, action: .restart))
    }

    @Test func theBannerTicksEverySecondAndRestartsOnTheSixtiethTick() {
        var state = AppUpdateRestartState.idle
        let start = AppUpdateAuto.restartOffer(state, ready: true, automatic: true, blockers: [], now: now)
        #expect(start.action == .startCountdown)
        state = start.state
        var actions: [AppUpdateRestartAction] = []
        for second in 1...Int(AppUpdateAuto.countdown) {
            // The user reaches for the banner on the tenth second: still counting.
            let blockers: [AppIdleBlocker] = second == 10 ? [.userActive] : []
            let step = AppUpdateAuto.restartOffer(state, ready: true, automatic: true, blockers: blockers, now: now.addingTimeInterval(Double(second) * AppUpdateAuto.countdownTick))
            actions.append(step.action); state = step.state
        }
        #expect(actions.dropLast().allSatisfy { $0 == .none })
        #expect(actions.last == .restart)
        #expect(state == .idle)
    }

    @Test func aRunStartingMidCountdownCancelsItAndTheNextIdleTickStartsAFreshOne() {
        var state = AppUpdateAuto.restartOffer(.idle, ready: true, automatic: true, blockers: [], now: now).state
        for second in 1...29 { state = AppUpdateAuto.restartOffer(state, ready: true, automatic: true, blockers: [], now: now.addingTimeInterval(Double(second))).state }
        let cancelled = AppUpdateAuto.restartOffer(state, ready: true, automatic: true, blockers: [.agentRun], now: now.addingTimeInterval(30))
        #expect(cancelled == AppUpdateRestartDecision(state: .idle, action: .cancel))
        // Busy again right after the run (recent activity): nothing starts.
        #expect(AppUpdateAuto.restartOffer(cancelled.state, ready: true, automatic: true, blockers: [.recentAgentActivity], now: now.addingTimeInterval(31)).action == .none)
        // Idle again: a full countdown, never the rest of the old one.
        let later = now.addingTimeInterval(600)
        #expect(AppUpdateAuto.restartOffer(.idle, ready: true, automatic: true, blockers: [], now: later) == AppUpdateRestartDecision(state: .countingDown(until: later.addingTimeInterval(AppUpdateAuto.countdown)), action: .startCountdown))
    }

    @Test func aCountdownTheMacSleptThroughIsCancelledOnWakeNotActedOn() {
        let until = now.addingTimeInterval(40)
        let counting = AppUpdateRestartState.countingDown(until: until)
        // A late tick (the main thread was busy) still restarts…
        #expect(AppUpdateAuto.restartOffer(counting, ready: true, automatic: true, blockers: [], now: until.addingTimeInterval(AppUpdateAuto.countdownGrace)) == AppUpdateRestartDecision(state: .idle, action: .restart))
        // …but waking long after the end cancels quietly, input or not.
        #expect(AppUpdateAuto.restartOffer(counting, ready: true, automatic: true, blockers: [], now: until.addingTimeInterval(AppUpdateAuto.countdownGrace + 1)) == AppUpdateRestartDecision(state: .idle, action: .cancel))
        #expect(AppUpdateAuto.restartOffer(counting, ready: true, automatic: true, blockers: [.userActive], now: until.addingTimeInterval(3_600)) == AppUpdateRestartDecision(state: .idle, action: .cancel))
        // Idle again later: a fresh, full countdown.
        let later = until.addingTimeInterval(3_600)
        #expect(AppUpdateAuto.restartOffer(.idle, ready: true, automatic: true, blockers: [], now: later) == AppUpdateRestartDecision(state: .countingDown(until: later.addingTimeInterval(AppUpdateAuto.countdown)), action: .startCountdown))
    }

    @Test func nothingIsOfferedWhileBusyNotReadyOrSwitchedOff() {
        #expect(AppUpdateAuto.restartOffer(.idle, ready: true, automatic: true, blockers: [.agentRun], now: now) == AppUpdateRestartDecision(state: .idle, action: .none))
        #expect(AppUpdateAuto.restartOffer(.idle, ready: false, automatic: true, blockers: [], now: now) == AppUpdateRestartDecision(state: .idle, action: .none))
        #expect(AppUpdateAuto.restartOffer(.idle, ready: true, automatic: false, blockers: [], now: now) == AppUpdateRestartDecision(state: .idle, action: .none))
    }

    @Test func aCountdownIsCancelledQuietlyWhenTheAppStopsBeingIdle() {
        let counting = AppUpdateRestartState.countingDown(until: now.addingTimeInterval(40))
        #expect(AppUpdateAuto.restartOffer(counting, ready: true, automatic: true, blockers: [.agentRun], now: now) == AppUpdateRestartDecision(state: .idle, action: .cancel))
        // Even when the time is already up, a busy app is not restarted.
        #expect(AppUpdateAuto.restartOffer(counting, ready: true, automatic: true, blockers: [.agentRun], now: now.addingTimeInterval(60)) == AppUpdateRestartDecision(state: .idle, action: .cancel))
    }

    @Test func inputDoesNotStartACountdownButDoesNotCancelOneEither() {
        // The user at the keyboard or mouse keeps the banner from appearing…
        #expect(AppUpdateAuto.restartOffer(.idle, ready: true, automatic: true, blockers: [.userActive], now: now) == AppUpdateRestartDecision(state: .idle, action: .none))
        // …but once it is up, reaching for its buttons does not take it away, and the time still runs out.
        let until = now.addingTimeInterval(40)
        let counting = AppUpdateRestartState.countingDown(until: until)
        #expect(AppUpdateAuto.restartOffer(counting, ready: true, automatic: true, blockers: [.userActive], now: now) == AppUpdateRestartDecision(state: counting, action: .none))
        #expect(AppUpdateAuto.restartOffer(counting, ready: true, automatic: true, blockers: [.userActive], now: until) == AppUpdateRestartDecision(state: .idle, action: .restart))
        // Every other reason still cancels, alone or together with input.
        for blocker in AppIdleBlocker.allCases where blocker != .userActive {
            #expect(AppUpdateAuto.restartOffer(counting, ready: true, automatic: true, blockers: [blocker], now: now) == AppUpdateRestartDecision(state: .idle, action: .cancel), "\(blocker)")
            #expect(AppUpdateAuto.restartOffer(counting, ready: true, automatic: true, blockers: [blocker, .userActive], now: now) == AppUpdateRestartDecision(state: .idle, action: .cancel), "\(blocker)")
        }
    }

    @Test func switchingOffOrLosingTheStagedUpdateCancelsACountdown() {
        let counting = AppUpdateRestartState.countingDown(until: now.addingTimeInterval(40))
        #expect(AppUpdateAuto.restartOffer(counting, ready: true, automatic: false, blockers: [], now: now) == AppUpdateRestartDecision(state: .idle, action: .cancel))
        #expect(AppUpdateAuto.restartOffer(counting, ready: false, automatic: true, blockers: [], now: now) == AppUpdateRestartDecision(state: .idle, action: .cancel))
    }

    @Test func laterSnoozesForFourHours() {
        let snoozed = AppUpdateAuto.later(now: now)
        let until = now.addingTimeInterval(AppUpdateAuto.snooze)
        #expect(snoozed == .snoozed(until: until))
        #expect(AppUpdateAuto.restartOffer(snoozed, ready: true, automatic: true, blockers: [], now: until.addingTimeInterval(-1)) == AppUpdateRestartDecision(state: snoozed, action: .none))
        // Switching off keeps the snooze; it is not a countdown to cancel.
        #expect(AppUpdateAuto.restartOffer(snoozed, ready: true, automatic: false, blockers: [], now: now) == AppUpdateRestartDecision(state: snoozed, action: .none))
        // Once it is over, an idle app is asked again.
        #expect(AppUpdateAuto.restartOffer(snoozed, ready: true, automatic: true, blockers: [], now: until) == AppUpdateRestartDecision(state: .countingDown(until: until.addingTimeInterval(AppUpdateAuto.countdown)), action: .startCountdown))
        #expect(AppUpdateAuto.restartOffer(snoozed, ready: true, automatic: true, blockers: [.agentRun], now: until) == AppUpdateRestartDecision(state: .idle, action: .none))
    }
}
