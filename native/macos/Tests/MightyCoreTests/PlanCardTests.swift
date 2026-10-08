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

    @Test func answeredPlanBlocksTakeTheirSavedSizeAndThePendingPlanItsOwn() throws {
        let runs = [Self.run("g1", source: "run-a", status: "completed"), Self.run("g2", source: "run-b")]
        let planID = MightyGraphBlockSize.nodeID(runID: "g2", suffix: MightyGraphLayout.planSuffix)
        let recordID = MightyGraphBlockSize.nodeID(runID: "g1", suffix: MightyGraphLayout.planRecordSuffix + "p1")
        let sizes: [String: MightyGraphBlockSize] = [planID: .init(width: 820, height: 610), recordID: .init(width: 470, height: 260)]
        func layout(_ sizes: [String: MightyGraphBlockSize], expanded: Set<String> = [], planSize: MightyGraphBlockSize? = nil) -> MightyGraphLayout {
            MightyGraphLayout.make(runs: runs, draft: "", running: true, expanded: expanded, blockSizes: sizes,
                                   planRunID: "g2", planRecords: [.init(runID: "g1", recordID: "p1")], planSize: planSize)
        }
        // A per-block size under the plan's key is not read: the pane's one plan size is.
        #expect(layout(sizes).nodes.first { $0.id == planID }?.frame.size == CGSize(width: MightyGraphLayout.planWidth, height: MightyGraphLayout.planHeight))
        #expect(layout(sizes, planSize: .init(width: 820, height: 610)).nodes.first { $0.id == planID }?.frame.size == CGSize(width: 820, height: 610))
        // A dragged size wins over folded and opened alike.
        for expanded in [Set<String>(), [recordID]] {
            #expect(layout(sizes, expanded: expanded).nodes.first { $0.id == recordID }?.frame.size == CGSize(width: 470, height: 260))
        }
        // Without one, the record folds and opens to its own heights again.
        #expect(layout([:], expanded: [recordID]).nodes.first { $0.id == recordID }?.frame.size
                == CGSize(width: MightyGraphLayout.planRecordWidth, height: MightyGraphLayout.planRecordHeight(expanded: true)))
        // Its answers are in the composer, so the plan block may be as small as any block.
        #expect(MightyGraphLayout.planMinimumSize == CGSize(width: MightyGraphBlockSize.minimumWidth, height: MightyGraphBlockSize.minimumHeight))
        let small = layout([:], planSize: .init(width: 10, height: 10))
        #expect(small.nodes.first { $0.id == planID }?.frame.size == MightyGraphLayout.planMinimumSize)
        // An answered plan's least is its folded height: dragged from there it does not jump to the blocks' least.
        let folded = layout([:]).nodes.first { $0.id == recordID }
        #expect(folded?.minimumSize == MightyGraphLayout.planRecordMinimumSize)
        #expect(folded?.frame.height == MightyGraphLayout.planRecordMinimumSize.height)
        let short = layout([recordID: .init(width: 360, height: Double(MightyGraphLayout.planRecordMinimumSize.height))])
        #expect(short.nodes.first { $0.id == recordID }?.frame.size == CGSize(width: 360, height: MightyGraphLayout.planRecordMinimumSize.height))
    }

    // MARK: The plan block fits the pane like the newest result

    private static let planRuns = [run("g1", source: "run-a", status: "completed"), run("g2", source: "run-b")]
    private static let planNodeID = MightyGraphBlockSize.nodeID(runID: "g2", suffix: MightyGraphLayout.planSuffix)
    private static func planFrame(viewport: CGSize?, zoom: CGFloat? = 1, saved: MightyGraphBlockSize? = nil) -> CGRect? {
        MightyGraphLayout.make(runs: planRuns, draft: "", running: true, expanded: [], viewport: viewport, zoom: zoom,
                               planRunID: "g2", planSize: saved).nodes.first { $0.id == planNodeID }?.frame
    }

    @Test func withNothingSavedThePlanFitsTheWindow() {
        let viewport = CGSize(width: 1_200, height: 800)
        #expect(Self.planFrame(viewport: viewport)?.size == MightyGraphLayout.resultFitSize(viewport: viewport, filesPanelOpen: false))
        #expect(Self.planFrame(viewport: viewport)?.size == CGSize(width: 1_152, height: 752))
        let layout = MightyGraphLayout.make(runs: Self.planRuns, draft: "", running: true, expanded: [], viewport: viewport, zoom: 1, planRunID: "g2")
        #expect(layout.planWindowFit == CGSize(width: 1_152, height: 752) && layout.planLimit == CGSize(width: 1_152, height: 752))
        // No plan block, no plan limits.
        let none = MightyGraphLayout.make(runs: Self.planRuns, draft: "", running: true, expanded: [], viewport: viewport, zoom: 1)
        #expect(none.planWindowFit == nil && none.planLimit == nil)
    }

    @Test func thePlanShrinksWithThePaneAndGrowsBackToItsSavedSize() {
        let saved = MightyGraphBlockSize(width: 900, height: 700)
        #expect(Self.planFrame(viewport: CGSize(width: 1_400, height: 1_000), saved: saved)?.size == CGSize(width: 900, height: 700))
        // A smaller pane draws it within the pane, the saved size untouched…
        #expect(Self.planFrame(viewport: CGSize(width: 700, height: 500), saved: saved)?.size == CGSize(width: 652, height: 452))
        // …and zoomed in, within what the pane shows at that zoom.
        #expect(Self.planFrame(viewport: CGSize(width: 1_000, height: 800), zoom: 1.5, saved: saved)?.size
                == MightyGraphLayout.resultViewportLimit(viewport: CGSize(width: 1_000, height: 800), zoom: 1.5, filesPanelOpen: false))
        // Back in a large pane it is the saved size again; the window fit is capped the same way.
        #expect(Self.planFrame(viewport: CGSize(width: 1_400, height: 1_000), saved: saved)?.size == CGSize(width: 900, height: 700))
        #expect(Self.planFrame(viewport: CGSize(width: 600, height: 400), zoom: 2)?.size
                == MightyGraphLayout.resultViewportLimit(viewport: CGSize(width: 600, height: 400), zoom: 2, filesPanelOpen: false))
    }

    @Test func withoutAViewportThePlanIsItsSavedSizeOrTheDocumentSize() {
        #expect(Self.planFrame(viewport: nil, zoom: nil)?.size == CGSize(width: MightyGraphLayout.planWidth, height: MightyGraphLayout.planHeight))
        #expect(Self.planFrame(viewport: nil, zoom: nil, saved: .init(width: 700, height: 400))?.size == CGSize(width: 700, height: 400))
        // A broken saved size is the window fit, never a broken frame.
        #expect(Self.planFrame(viewport: CGSize(width: 1_200, height: 800), saved: .init(width: .nan, height: 400))?.size == CGSize(width: 1_152, height: 752))
    }

    @Test func aPlanDragSavesByTheResultsRules() {
        let viewport = CGSize(width: 1_000, height: 700)
        let fit = MightyGraphLayout.planSize(saved: nil, viewport: viewport, zoom: 1)
        // Released inside the pane: saved as released; past the pane's edge: never smaller than the limit.
        let inside = MightyGraphLayout.resultDrag(dragged: CGSize(width: 600, height: 420), edges: .bottomRight, phase: .finished,
                                                  saved: nil, limit: fit.limit, windowFit: fit.windowFit)
        #expect(inside.save == MightyGraphBlockSize(width: 600, height: 420))
        let past = MightyGraphLayout.resultDrag(dragged: CGSize(width: 2_000, height: 420), edges: .bottomRight, phase: .live,
                                                saved: nil, limit: fit.limit, windowFit: fit.windowFit)
        #expect(past.live == MightyGraphBlockSize(width: 952, height: 420) && past.save == nil)
    }

    // MARK: The plan's answers in the composer

    @Test func theComposerShowsOnlyTheAnswersWhileTheDiagramDrawsThePlan() {
        let plan = Self.planRequest(runId: "run-b")
        let runs = [Self.run("g1", source: "run-a", status: "completed"), Self.run("g2", source: "run-b")]
        #expect(PlanCardSupport.composerShowsPlanActions(plan, showsDiagram: true, runs: runs))
        #expect(PlanCardSupport.composerPlace(plan, showsDiagram: true, runs: runs, guided: false) == .barActions)
        #expect(PlanCardSupport.composerPlace(plan, showsDiagram: true, runs: runs, guided: true) == .panelActions)
        // The diagram does not draw it (default view, timeline, no request for it, request ended): the whole card docks.
        #expect(!PlanCardSupport.composerShowsPlanActions(plan, showsDiagram: false, runs: runs))
        for guided in [false, true] {
            #expect(PlanCardSupport.composerPlace(plan, showsDiagram: false, runs: runs, guided: guided) == .card)
            #expect(PlanCardSupport.composerPlace(Self.planRequest(runId: "run-z"), showsDiagram: true, runs: runs, guided: guided) == .card)
            #expect(PlanCardSupport.composerPlace(Self.planRequest(runId: "run-a"), showsDiagram: true, runs: runs, guided: guided) == .card)
            // Anything but a pending answerable plan has no plan place.
            #expect(PlanCardSupport.composerPlace(nil, showsDiagram: true, runs: runs, guided: guided) == nil)
            #expect(PlanCardSupport.composerPlace(Self.bash(), showsDiagram: true, runs: runs, guided: guided) == nil)
            #expect(PlanCardSupport.composerPlace(Self.planRequest(runId: "run-b", state: "allowed"), showsDiagram: true, runs: runs, guided: guided) == nil)
        }
    }

    @Test func onlyAClaudePaneShowingItsDiagramDrawsThePlan() {
        var session = RunSession(id: "s", workspaceId: "w", title: "t")
        #expect(!PlanCardSupport.showsDiagram(session))
        session.agentViewMode = "mighty"
        #expect(PlanCardSupport.showsDiagram(session))
        session.graphViewMode = .timeline
        #expect(!PlanCardSupport.showsDiagram(session))
        session.graphViewMode = nil; session.kind = "shell"
        #expect(!PlanCardSupport.showsDiagram(session))
    }

    @Test func thePlanSizeRoundTripsAndOldStateLoads() throws {
        var session = RunSession(id: "s1", workspaceId: "w1", title: "t")
        session.graphPlanSize = MightyGraphBlockSize(width: 820, height: 610)
        let decoded = try JSONDecoder().decode(RunSession.self, from: JSONEncoder().encode(session))
        #expect(decoded.graphPlanSize == MightyGraphBlockSize(width: 820, height: 610))
        let plain = try JSONSerialization.jsonObject(with: JSONEncoder().encode(RunSession(id: "s2", workspaceId: "w1", title: "t"))) as? [String: Any]
        #expect(plain?["graphPlanSize"] == nil)
        let old = try JSONDecoder().decode(RunSession.self, from: Data(#"{"id":"s3","workspaceId":"w1","title":"t"}"#.utf8))
        #expect(old.graphPlanSize == nil)
        // A damaged size never loses the pane.
        let damaged = try JSONDecoder().decode(RunSession.self, from: Data(#"{"id":"s4","workspaceId":"w1","title":"t","graphPlanSize":"wide"}"#.utf8))
        #expect(damaged.graphPlanSize == nil && damaged.id == "s4")
    }
}
