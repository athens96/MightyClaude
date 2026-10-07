import CoreGraphics
import Foundation
import Testing
@testable import MightyCore

/// What the plan card, its place in the Mighty diagram and the plan history read.
struct PlanCardTests {
    static func planRequest(id: String = "plan-1", runId: String = "run-1", plan: String = "# 계획\n\n1. 테스트", state: String = "pending") -> ToolPermissionRequest {
        let input = String(decoding: try! JSONSerialization.data(withJSONObject: ["plan": plan]), as: UTF8.self)
        return ToolPermissionRequest(id: id, runId: runId, toolUseId: "tool-" + id, toolName: ClaudePlanMode.toolName, inputJSON: input, summary: "plan",
                                     state: state, canAllow: false, canAnswerPlan: true, receivedAt: "2027-01-15T08:05:00.000Z")
    }
    static func bash(id: String = "perm-1") -> ToolPermissionRequest {
        ToolPermissionRequest(id: id, runId: "run-1", toolUseId: "tool-b", toolName: "Bash", inputJSON: #"{"command":"ls"}"#, summary: "ls")
    }
    static func run(_ id: String, source: String?, status: String = "running") -> MightyGraphRun {
        MightyGraphRun(id: id, input: "요청 " + id, status: status, sourceRunID: source)
    }
    static func record(_ id: String, runId: String, outcome: PlanOutcome = .approvedAuto) -> PlanRecord {
        PlanRecord(id: id, runId: runId, plan: "## 단계\n- 하나", receivedAt: "2027-01-15T08:00:00.000Z", decidedAt: "2027-01-15T08:01:00.000Z", outcome: outcome)
    }

    @Test func thePendingPlanIsTheFirstAnswerablePlanOnly() {
        #expect(PlanCardSupport.pendingPlan(nil) == nil)
        #expect(PlanCardSupport.pendingPlan([Self.bash()]) == nil)
        #expect(PlanCardSupport.pendingPlan([Self.planRequest(state: "allowed")]) == nil)
        var unanswerable = Self.planRequest(); unanswerable.canAnswerPlan = false
        #expect(PlanCardSupport.pendingPlan([unanswerable]) == nil)
        let plan = Self.planRequest(id: "plan-2")
        #expect(PlanCardSupport.pendingPlan([Self.bash(), plan, Self.planRequest(id: "plan-3")])?.id == "plan-2")
    }

    @Test func thePetShowsThePlanOfAPlanRequestOnly() {
        #expect(PlanCardSupport.companionPlan(Self.planRequest()) == "# 계획\n\n1. 테스트")
        #expect(PlanCardSupport.companionPlan(Self.bash()) == nil)
        var unanswerable = Self.planRequest(); unanswerable.canAnswerPlan = false
        #expect(PlanCardSupport.companionPlan(unanswerable) == nil)
        // A permission that answers plans but carries none (not ExitPlanMode) has nothing to show.
        var other = Self.bash(); other.canAnswerPlan = true
        #expect(PlanCardSupport.companionPlan(other) == nil)
    }

    @Test func theDiagramDrawsThePlanUnderItsUnfinishedRequestOnly() {
        let plan = Self.planRequest(runId: "run-b")
        let runs = [Self.run("g1", source: "run-a", status: "completed"), Self.run("g2", source: "run-b")]
        #expect(PlanCardSupport.diagramPlanRunID(plan, showsDiagram: true, runs: runs) == "g2")
        // Docked instead: not the diagram, no request drawn for its run, or the request already ended.
        #expect(PlanCardSupport.diagramPlanRunID(plan, showsDiagram: false, runs: runs) == nil)
        #expect(PlanCardSupport.diagramPlanRunID(Self.planRequest(runId: "run-z"), showsDiagram: true, runs: runs) == nil)
        #expect(PlanCardSupport.diagramPlanRunID(Self.planRequest(runId: "run-a"), showsDiagram: true, runs: runs) == nil)
        #expect(PlanCardSupport.diagramPlanRunID(nil, showsDiagram: true, runs: runs) == nil)
        #expect(PlanCardSupport.diagramPlanRunID(Self.bash(), showsDiagram: true, runs: runs) == nil)
    }

    @Test func historyBlocksFollowTheRequestsTheDiagramDraws() {
        let runs = [Self.run("g1", source: "run-a", status: "completed"), Self.run("g2", source: "run-b")]
        let blocks = PlanCardSupport.diagramRecords([Self.record("p1", runId: "run-a"), Self.record("p2", runId: "gone"), Self.record("p3", runId: "run-b", outcome: .revised)], runs: runs)
        #expect(blocks == [.init(runID: "g1", recordID: "p1"), .init(runID: "g2", recordID: "p3")])
        #expect(PlanCardSupport.diagramRecords(nil, runs: runs).isEmpty)
    }

    @Test func historyFollowsTheGraphRunIdAndShowsAsBlocksOnlyInTheDiagram() {
        let runs = [Self.run("g1", source: "graph-a", status: "completed")]
        var record = Self.record("p1", runId: "pane-run")
        #expect(PlanCardSupport.diagramRecords([record], runs: runs).isEmpty)
        record.graphRunId = "graph-a"
        #expect(PlanCardSupport.diagramRecords([record], runs: runs) == [.init(runID: "g1", recordID: "p1")])
        record.graphRunId = "bad id with spaces"
        #expect(ClaudePlanMode.normalizedHistory([record])?.first?.graphRunId == nil)
        #expect(PlanCardSupport.showsHistoryStrip(mightyDiagram: false) && !PlanCardSupport.showsHistoryStrip(mightyDiagram: true))
    }

    @Test func aChangeRequestThatIsTooLongSaysSo() {
        #expect(!PlanCardSupport.feedbackTooLong(""))
        #expect(!PlanCardSupport.feedbackTooLong("  \n "))
        #expect(!PlanCardSupport.feedbackTooLong("짧은 요청"))
        #expect(PlanCardSupport.feedbackTooLong(String(repeating: "가", count: ClaudePlanMode.maximumFeedbackBytes / 3 + 1)))
    }

    @Test func reviseNeedsTextCoreAccepts() {
        #expect(!PlanCardSupport.canSendRevise(""))
        #expect(!PlanCardSupport.canSendRevise(" \n\t "))
        #expect(PlanCardSupport.canSendRevise(" 테스트를 먼저 "))
        #expect(!PlanCardSupport.canSendRevise(String(repeating: "a", count: ClaudePlanMode.maximumFeedbackBytes + 1)))
    }

    @Test func outcomesTimesAndHeadlinesRead() {
        LocaleOverride.$language.withValue(.ko) {
            // Whatever the app's language, each outcome reads its own key.
            #expect(PlanCardSupport.outcomeTitle(.approvedAuto) == L("plan.outcome.approvedAuto"))
            #expect(PlanCardSupport.outcomeTitle(.approvedConfirm) == L("plan.outcome.approvedConfirm"))
            #expect(PlanCardSupport.outcomeTitle(.revised) == L("plan.outcome.revised"))
            #expect(PlanCardSupport.outcomeTitle(.cancelled) == L("plan.outcome.cancelled"))
            #expect(Set(PlanOutcome.allCases.map(PlanCardSupport.outcomeTitle)).count == 4)
            #expect(!PlanCardSupport.outcomeTitle(.revised).hasPrefix("plan."))
            #expect(PlanCardSupport.receivedText("2027-01-15T08:05:00.000Z", timeZone: TimeZone(identifier: "Asia/Seoul")!) == L("plan.card.received", ["time": "17:05"]))
            #expect(PlanCardSupport.timeText("2027-01-15T08:05:00.000Z", timeZone: TimeZone(identifier: "UTC")!) == "08:05")
            #expect(PlanCardSupport.timeText("not a time") == "not a time")
            #expect(PlanCardSupport.headline("\n\n## 로그인 고치기\n\n1. 원인") == "로그인 고치기")
            #expect(PlanCardSupport.headline("") == "")
        }
    }

    @Test func theHeaderSaysTheTurnIsDoneWhileBackgroundWorkRuns() {
        LocaleOverride.$language.withValue(.ko) {
            let task = BackgroundTask(id: "a", kind: "agent", description: "조사", startedAt: "2027-01-15T08:00:00.000Z")
            let done = BackgroundTask(id: "b", kind: "shell", description: "빌드", startedAt: "2027-01-15T08:00:00.000Z", status: "completed")
            #expect(PlanCardSupport.backgroundStatus(nil) == nil)
            #expect(PlanCardSupport.backgroundStatus(BackgroundWork(tasks: [task], turnEnded: false)) == nil)
            #expect(PlanCardSupport.backgroundStatus(BackgroundWork(tasks: [done], turnEnded: true)) == nil)
            #expect(PlanCardSupport.backgroundStatus(BackgroundWork(tasks: [task, done, BackgroundTask(id: "c", kind: "agent", description: "", startedAt: "2027-01-15T08:00:00.000Z")], turnEnded: true))
                    == L("plan.background.status", ["count": "2"]))
        }
    }

    @Test func aHiddenBackgroundLineDrawsNothingInEitherView() {
        let task = BackgroundTask(id: "a", kind: "agent", description: "look", startedAt: "2027-01-15T08:00:00.000Z")
        let waiting = BackgroundWork(tasks: [task], turnEnded: true)
        for mighty in [false, true] {
            #expect(PlanCardSupport.showsBackgroundStrip(waiting, mighty: mighty, styleDrawsTasks: false))
            #expect(PlanCardSupport.showsBackgroundStrip(waiting, mighty: mighty, styleDrawsTasks: false, enabled: true))
            #expect(!PlanCardSupport.showsBackgroundStrip(waiting, mighty: mighty, styleDrawsTasks: false, enabled: false))
        }
        #expect(!PlanCardSupport.showsBackgroundStrip(BackgroundWork(tasks: [task], turnEnded: false), mighty: true, styleDrawsTasks: false, enabled: false))
    }

    @Test func thePlanCardTakesTheResultsPlaceAndRecordsHangBesideTheRequest() throws {
        let agent = MightyGraphAgent(id: "a1", title: "조사")
        var running = Self.run("g2", source: "run-b"); running.agents = [agent]
        let runs = [Self.run("g1", source: "run-a", status: "completed"), running]
        let plain = MightyGraphLayout.make(runs: runs, draft: "", running: true, expanded: [])
        let layout = MightyGraphLayout.make(runs: runs, draft: "", running: true, expanded: [], planRunID: "g2",
                                            planRecords: [.init(runID: "g1", recordID: "p1"), .init(runID: "g2", recordID: "p2"), .init(runID: "missing", recordID: "p3")])
        let planID = MightyGraphBlockSize.nodeID(runID: "g2", suffix: MightyGraphLayout.planSuffix)
        let plan = try #require(layout.nodes.first { $0.id == planID })
        #expect(plan.content == .plan(1) && !plan.isAuxiliary)
        #expect(plan.frame.size == CGSize(width: MightyGraphLayout.planWidth, height: MightyGraphLayout.planHeight))
        let agentNode = try #require(layout.nodes.first { $0.content == .agent(1, 0) })
        #expect(plan.frame.minY == agentNode.frame.maxY + MightyGraphLayout.rowGap)
        #expect(layout.edges.contains { $0.source == agentNode.id && $0.target == planID && $0.joins })
        // Centred on the diagram's one centreline, and nothing above it moved.
        #expect(abs(plan.frame.midX - MightyGraphCamera.centreX) < 0.5)
        for node in plain.nodes { #expect(layout.nodes.first { $0.id == node.id }?.frame == node.frame) }
        // Answered plans are attachments beside their own request, which a drag resizes all the same.
        let records = layout.nodes.filter { if case .planRecord = $0.content { return true }; return false }
        #expect(records.count == 2 && records.allSatisfy(\.isAuxiliary) && records.allSatisfy(\.isResizable))
        #expect(records.allSatisfy { MightyGraphCamera.isAuxiliary(nodeID: $0.id) })
        #expect(plan.isResizable && plan.minimumSize == MightyGraphLayout.planMinimumSize)
        let firstRequest = try #require(layout.nodes.first { $0.content == .request(0) })
        let first = try #require(records.first { $0.content == .planRecord(0, "p1") })
        #expect(first.frame.minY == firstRequest.frame.minY && first.frame.minX > firstRequest.frame.maxX)
        #expect(first.frame.size == CGSize(width: MightyGraphLayout.planRecordWidth, height: MightyGraphLayout.planRecordHeight(expanded: false)))
        let opened = MightyGraphLayout.make(runs: runs, draft: "", running: true, expanded: [first.id], planRecords: [.init(runID: "g1", recordID: "p1")])
        #expect(opened.nodes.first { $0.id == first.id }?.frame.height == MightyGraphLayout.planRecordHeight(expanded: true))
        // A finished run never shows a pending plan: its result is there instead.
        let finished = MightyGraphLayout.make(runs: [Self.run("g1", source: "run-a", status: "completed")], draft: "", running: false, expanded: [], planRunID: "g1")
        #expect(!finished.nodes.contains { $0.content == .plan(0) })
    }

    @Test func planBlocksTakeTheirSavedSizeAndThePlanCardNeverShrinksPastItsAnswers() throws {
        let runs = [Self.run("g1", source: "run-a", status: "completed"), Self.run("g2", source: "run-b")]
        let planID = MightyGraphBlockSize.nodeID(runID: "g2", suffix: MightyGraphLayout.planSuffix)
        let recordID = MightyGraphBlockSize.nodeID(runID: "g1", suffix: MightyGraphLayout.planRecordSuffix + "p1")
        let sizes: [String: MightyGraphBlockSize] = [planID: .init(width: 820, height: 610), recordID: .init(width: 470, height: 260)]
        func layout(_ sizes: [String: MightyGraphBlockSize], expanded: Set<String> = []) -> MightyGraphLayout {
            MightyGraphLayout.make(runs: runs, draft: "", running: true, expanded: expanded, blockSizes: sizes,
                                   planRunID: "g2", planRecords: [.init(runID: "g1", recordID: "p1")])
        }
        #expect(layout(sizes).nodes.first { $0.id == planID }?.frame.size == CGSize(width: 820, height: 610))
        // A dragged size wins over folded and opened alike.
        for expanded in [Set<String>(), [recordID]] {
            #expect(layout(sizes, expanded: expanded).nodes.first { $0.id == recordID }?.frame.size == CGSize(width: 470, height: 260))
        }
        // Without one, the record folds and opens to its own heights again.
        #expect(layout([:], expanded: [recordID]).nodes.first { $0.id == recordID }?.frame.size
                == CGSize(width: MightyGraphLayout.planRecordWidth, height: MightyGraphLayout.planRecordHeight(expanded: true)))
        // A size below the plan card's least (saved elsewhere) is drawn at that least.
        let small = layout([planID: .init(width: 300, height: 140)])
        #expect(small.nodes.first { $0.id == planID }?.frame.size == MightyGraphLayout.planMinimumSize)
    }
}
