import CoreGraphics
import Foundation
import Testing
@testable import MightyCore

/// The Mighty timeline (concept D): rows from the diagram's own runs, the rail rule,
/// status → tone, the "블록 n개 · 끝남 m" tally, and the pane's saved view choice.
struct MightyTimelineTests {
    static func run(_ id: String, status: String = "running", agents: [MightyGraphAgent] = [], final: String? = nil) -> MightyGraphRun {
        var run = MightyGraphRun(id: id, input: "do " + id, status: status, agents: agents, finalOutput: final, provider: "claude")
        run.refreshResult()
        return run
    }

    static func step(_ id: String, summary: String, state: String = "running") -> LogEntry {
        LogEntry(id: id, kind: "system", text: summary, activity: AgentActivity(id: id, provider: "claude", kind: "tool", state: state, toolName: "Read", summary: summary))
    }

    @Test func rowsFollowTheDiagramsBlocksAndNodeIDs() {
        LocaleOverride.$language.withValue(.ko) {
            let agents = [
                MightyGraphAgent(id: "a1", title: "화면 구조 읽기", status: "completed"),
                MightyGraphAgent(id: "t1", title: "", status: "running", entries: [Self.step("s1", summary: "sh render.sh")], kind: "task"),
                MightyGraphAgent(id: "q1", title: "", status: "waiting", kind: "question"),
            ]
            let runs = [Self.run("r1", status: "completed", final: "끝"), Self.run("r2", agents: agents)]
            let groups = MightyTimeline.groups(runs)
            #expect(groups.map(\.ordinal) == [1, 2])
            #expect(groups.map(\.runIndex) == [0, 1])
            let rows = groups[1].rows
            // The request first, its 실행 block, then the agents in the order the graph recorded them.
            #expect(rows.map(\.block.kind) == ["main", MightyTimeline.executionKind, "agent", "task", "question"])
            #expect(rows.map(\.agentIndex) == [nil, nil, 0, 1, 2])
            #expect(rows.map(\.isExecution) == [false, true, false, false, false])
            #expect(rows[0].nodeID == MightyGraphBlockSize.nodeID(runID: "r2", suffix: "request"))
            #expect(rows[1].nodeID == MightyGraphBlockSize.nodeID(runID: "r2", suffix: MightyGraphLayout.workSuffix))
            #expect(rows[3].nodeID == MightyGraphBlockSize.nodeID(runID: "r2", suffix: "agent:t1"))
            // Titles are the core's, as the diagram and the phone name them.
            #expect(rows[3].block.title == MightyGraphSupport.blockTitle(agents[1]))
            #expect(rows[0].block.title == "요청 2")
            // The 실행 block names its request, so each one reads apart from the others.
            #expect(rows[1].block.title == "실행 · 요청 2")
            // Only a block in motion has a latest line.
            #expect(rows[3].latest == "sh render.sh")
            #expect(rows[2].latest == nil)
            // The node ids are ones the diagram actually lays out.
            let layout = MightyGraphLayout.make(runs: runs, draft: "", running: true, expanded: [], splitsExecution: true)
            let laid = Set(layout.nodes.map(\.id))
            for group in groups { for row in group.rows { #expect(laid.contains(row.nodeID)) } }
            #expect(laid.contains(groups[0].result?.nodeID ?? "missing"))
        }
    }

    @Test func theRequestRowHoldsTheInputAndTheExecutionRowTheWork() {
        var run = Self.run("r", agents: [MightyGraphAgent(id: "a", status: "running")])
        run.rootEntries = [Self.step("s1", summary: "rg render"), Self.step("s2", summary: "sed -n 1,40p view.swift")]
        run.nodeModelLabel = "Opus"
        let rows = MightyTimeline.group(run, index: 0).rows
        // The request: what was asked, in its neutral state, with nothing it did.
        #expect(rows[0].block.summary == "do r")
        #expect(rows[0].block.status == MightyTimeline.requestStatus)
        #expect(rows[0].node == .init(tone: .idle, ring: false, rail: nil))
        #expect(rows[0].block.activity == nil && rows[0].block.output == nil && rows[0].block.durationMs == nil && rows[0].block.nodeModelLabel == nil)
        #expect(rows[0].latest == nil)
        // Its 실행 block: the run's status, steps and model, and no request text.
        #expect(rows[1].block.summary == nil)
        #expect(rows[1].block.status == "running")
        #expect(rows[1].block.activity == ["rg render", "sed -n 1,40p view.swift"])
        #expect(rows[1].block.nodeModelLabel == "Opus")
        #expect(rows[1].latest == "sed -n 1,40p view.swift")
        #expect(rows[1].node == MightyTimeline.node(status: "running"))
        // A request that has nothing yet still has its 실행 row, waiting.
        let freshGroup = MightyTimeline.group(Self.run("n"), index: 0)
        let fresh = freshGroup.rows
        #expect(fresh.map(\.isExecution) == [false, true])
        #expect(fresh[1].block.status == "running" && fresh[1].block.activity == nil)
        // Only the 실행 block counts: "블록 1개 · 끝남 0", not the request as a finished block.
        #expect(freshGroup.tally == .init(total: 1, settled: 0, running: 1))
        // A request whose work failed or stopped never reads as a success, nor as the failure.
        for (status, tone) in [("error", DesignTone.err), ("stopped", .stop), ("completed", .done)] {
            let ended = MightyTimeline.group(Self.run("e", status: status), index: 0).rows
            #expect(ended[0].node.tone == .idle, "\(status)")
            #expect(ended[1].node.tone == tone, "\(status)")
        }
        // The phone is still sent one main block for both.
        let phone = MobileMightySupport.blocks(run, ordinal: 1)
        #expect(phone.map(\.kind) == ["main", "agent"])
        #expect(phone[0].summary == "do r" && phone[0].activity == ["rg render", "sed -n 1,40p view.swift"])
    }

    @Test func aResultCardAppearsExactlyWhenTheDiagramDrawsOne() {
        let done = MightyTimeline.group(Self.run("r", status: "completed", final: "정렬했습니다."), index: 0)
        #expect(done.result == MightyTimeline.Result(nodeID: MightyGraphBlockSize.nodeID(runID: "r", suffix: "result"), tone: .done, text: "정렬했습니다."))
        // A finished request whose child still runs is not finished in the diagram either.
        let open = MightyTimeline.group(Self.run("r", status: "completed", agents: [MightyGraphAgent(id: "a", status: "running")], final: "x"), index: 0)
        #expect(open.result == nil)
        #expect(MightyTimeline.group(Self.run("r"), index: 0).result == nil)
        let failed = MightyTimeline.group(Self.run("r", status: "error"), index: 0)
        #expect(failed.result?.tone == .err)
        #expect(failed.result?.text == nil)
        #expect(MightyTimeline.group(Self.run("r", status: "stopped"), index: 0).result?.tone == .stop)
    }

    @Test func theAwaitingNoticeHeadWaits() {
        func shown(_ taskStatus: String, run status: String = "completed") -> MightyGraphRun {
            let task = MightyGraphAgent(id: "t", status: taskStatus, kind: "task", background: true)
            return Self.run("r", status: status, agents: [task], final: "Built.").shown
        }
        // While the request awaits the notices of background work it started, the 기록 result head waits, then is done once they are in.
        #expect(MightyTimeline.group(shown("running"), index: 0).result?.tone == .wait)
        #expect(MightyTimeline.group(shown("running"), index: 0).result?.awaiting == true)
        #expect(MightyTimeline.group(shown("completed"), index: 0).result?.tone == .done)
        #expect(MightyTimeline.group(shown("completed"), index: 0).result?.awaiting == false)
        // A failed request says so whatever still runs.
        #expect(MightyTimeline.group(shown("running", run: "error"), index: 0).result?.tone == .err)
    }

    @Test func theRailIsLitOnlyUnderFinishedOrRunningBlocks() {
        #expect(MightyTimeline.node(status: "completed") == .init(tone: .done, ring: false, rail: .done))
        #expect(MightyTimeline.node(status: "running") == .init(tone: .run, ring: true, rail: .run))
        #expect(MightyTimeline.node(status: "waiting") == .init(tone: .wait, ring: false, rail: nil))
        #expect(MightyTimeline.node(status: "error") == .init(tone: .err, ring: false, rail: nil))
        #expect(MightyTimeline.node(status: "stopped") == .init(tone: .stop, ring: false, rail: nil))
        let agents = [MightyGraphAgent(id: "a", status: "completed"), MightyGraphAgent(id: "b", status: "error")]
        let rows = MightyTimeline.group(Self.run("r", agents: agents), index: 0).rows
        #expect(MightyTimeline.railAbove(rows, at: 0) == nil)
        // The request's node is neutral and leaves the track; its 실행 block runs.
        #expect(MightyTimeline.railAbove(rows, at: 1) == nil)
        #expect(MightyTimeline.railAbove(rows, at: 2) == .run)
        #expect(MightyTimeline.railAbove(rows, at: 3) == .done)
        #expect(MightyTimeline.railAbove(rows, at: 9) == nil)
    }

    @Test func blockStatusesMapToTonesThroughThePhonesBuckets() {
        let cases: [(String, DesignTone)] = [
            ("running", .run), ("idle", .run), ("starting", .run), ("queued", .run), ("brand-new", .run),
            ("waiting", .wait), ("completed", .done), ("error", .err), ("failed", .err),
            ("stopped", .stop), ("cancelled", .stop), ("interrupted", .stop),
        ]
        for (status, tone) in cases { #expect(DesignTone(blockStatus: status) == tone, "\(status)") }
    }

    @Test func theTallyCountsOnlyTheBlocksPresent() {
        let agents = ["completed", "error", "stopped", "running", "waiting"].enumerated().map { MightyGraphAgent(id: "a\($0)", status: $1) }
        let group = MightyTimeline.group(Self.run("r", agents: agents), index: 0)
        // The 실행 block (running) and the five agents; the request is not a block that settles.
        #expect(group.tally == .init(total: 6, settled: 3, running: 2))
        #expect(MightyTimeline.tally([]) == .init(total: 0, settled: 0, running: 0))
    }

    @Test func edgesIntoRunningBlocksAreSplitOut() {
        let agents = [MightyGraphAgent(id: "done", status: "completed"), MightyGraphAgent(id: "live", status: "running")]
        let runs = [Self.run("r", agents: agents)]
        let live = MightyGraphLayout.liveNodeIDs(runs)
        #expect(live == [MightyGraphBlockSize.nodeID(runID: "r", suffix: MightyGraphLayout.workSuffix), MightyGraphBlockSize.nodeID(runID: "r", suffix: "agent:live")])
        let layout = MightyGraphLayout.make(runs: runs, draft: "", running: true, expanded: [], splitsExecution: true)
        let split = layout.routes(into: live)
        #expect(split.into.count + split.other.count == layout.routes().count)
        // Request → 실행 and 실행 → the live agent; 실행 → the finished one is quiet.
        #expect(split.into.count == 2)
        // Nothing near the viewport: nothing drawn either way.
        let far = layout.routes(in: CGRect(x: 1e6, y: 1e6, width: 1, height: 1), into: live)
        #expect(far.into.isEmpty && far.other.isEmpty)
    }

    @Test func theViewChoiceDecodesAsTheDiagramWhenAbsentOrUnknown() throws {
        let legacy = Data(#"{"id":"s","workspaceId":"w"}"#.utf8)
        let decoded = try JSONDecoder().decode(RunSession.self, from: legacy)
        #expect(decoded.graphViewMode == nil)
        #expect(decoded.mightyViewMode == .diagram)
        let unknown = Data(#"{"id":"s","workspaceId":"w","graphViewMode":"gantt"}"#.utf8)
        #expect(try JSONDecoder().decode(RunSession.self, from: unknown).mightyViewMode == .diagram)
        // A pane that never chose saves nothing.
        let wire = try JSONSerialization.jsonObject(with: JSONEncoder().encode(decoded)) as? [String: Any]
        #expect(wire?["graphViewMode"] == nil)
    }

    @Test func theViewChoiceRoundTrips() throws {
        var session = RunSession(workspaceId: "w", title: "t")
        session.graphViewMode = .timeline
        let restored = try JSONDecoder().decode(RunSession.self, from: JSONEncoder().encode(session))
        #expect(restored.graphViewMode == .timeline)
        #expect(restored.mightyViewMode == .timeline)
        #expect(restored == session)
    }
}
