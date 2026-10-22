import Foundation
import Testing
@testable import MightyCore

/// How new input meets a busy run: it always waits in the queue, and a
/// plan-mode style (docs/mighty-styles.md §1.17.4) records plan mode on it;
/// Windows Core checks the same rules in `ClaudePlanStyleVerification.QueueAndStoredMode`.
struct BackgroundQueuePolicyTests {
    private let running = BackgroundTask(id: "b", kind: "agent", description: "x", startedAt: "2026-10-06T01:00:00.000Z")
    private var waiting: BackgroundWork { BackgroundWork(tasks: [running], turnEnded: true) }
    private var busy: BackgroundWork { BackgroundWork(tasks: [running], turnEnded: false) }

    @Test func theQueueSaysItWaitsOnBackgroundWorkInEveryStyle() {
        #expect(BackgroundQueuePolicy.waitsOnBackground(work: waiting, queued: 1))
        #expect(!BackgroundQueuePolicy.waitsOnBackground(work: waiting, queued: 0))
        #expect(!BackgroundQueuePolicy.waitsOnBackground(work: busy, queued: 1))
        #expect(!BackgroundQueuePolicy.waitsOnBackground(work: nil, queued: 1))
        LocaleOverride.$language.withValue(.ko) {
            #expect(L("queue.waitingOnBackground.mac") == "백그라운드 작업이 끝나야 시작합니다 · 지금 시작하려면 중지")
        }
    }

    @Test func stoppingATurnThatIsOverKeepsTheQueue() {
        #expect(BackgroundQueuePolicy.stopKeepsQueue(waiting))
        #expect(BackgroundQueuePolicy.stopKeepsQueue(BackgroundWork(tasks: [], turnEnded: true)))
        #expect(!BackgroundQueuePolicy.stopKeepsQueue(busy))
        #expect(!BackgroundQueuePolicy.stopKeepsQueue(nil))
    }

    @Test func aQueuedItemKeepsItsLaunchDecision() {
        #expect(BackgroundQueuePolicy.queuedOverride(launchesInPlan: true) == "plan")
        #expect(BackgroundQueuePolicy.queuedOverride(launchesInPlan: false) == nil)
        let item = QueuedInput(text: "결제", permissionModeOverride: BackgroundQueuePolicy.queuedOverride(launchesInPlan: true))
        #expect(item.permissionModeOverride == "plan" && QueuedInput(text: "x").permissionModeOverride == nil)
    }

    private func approved(_ override: String?) -> RunEvent {
        RunEvent(sessionId: "pane", type: "plan", plan: PlanRecord(id: "p", runId: "run", plan: "# Plan", receivedAt: "2026-10-06T01:00:00.000Z",
                                                                    decidedAt: "2026-10-06T01:01:00.000Z", outcome: .approvedAuto, launchOverride: override))
    }

    @Test func aStyleLaunchedPlanLeavesTheStoredModeAlone() {
        var stored = RunSession(id: "pane", workspaceId: "ws", title: "t")
        stored.settings.permissionMode = "plan"
        var overridden = stored
        overridden.recordPlanMode(approved("plan"))
        #expect(overridden.settings.permissionMode == "plan" && overridden.planHistory?.first?.launchOverride == "plan")
        // Without the override the stage-1 behaviour stays: a pane stored in plan takes the approved mode.
        stored.recordPlanMode(approved(nil))
        #expect(stored.settings.permissionMode == "acceptEdits")
        var manual = RunSession(id: "pane", workspaceId: "ws", title: "t")
        manual.recordPlanMode(approved("plan"))
        #expect(manual.settings.permissionMode == "manual")
    }

    @Test func savedRecordsKeepOnlyThePlanOverride() throws {
        let kept = PlanRecord(id: "a", runId: "run", plan: "# A", receivedAt: "2026-10-06T01:00:00.000Z", decidedAt: "2026-10-06T01:01:00.000Z", outcome: .approvedAuto, launchOverride: "plan")
        var odd = kept; odd.id = "b"; odd.launchOverride = "fullAccess"
        let history = try #require(ClaudePlanMode.normalizedHistory([kept, odd]))
        #expect(history.map(\.launchOverride) == ["plan", nil])
    }
}
