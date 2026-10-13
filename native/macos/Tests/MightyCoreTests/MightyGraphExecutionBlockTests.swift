import CoreGraphics
import Foundation
import Testing
@testable import MightyCore

/// The Mac diagram's 실행 block (`MightyGraphLayout.make(splitsExecution:)`): a request
/// block holds its input alone, and the main agent's work sits in a 실행 block right
/// under it, with the sub-agents and the request's attachments hanging off it.
struct MightyGraphExecutionBlockTests {
    private func agent(_ id: String, parent: String? = nil, status: String = "completed") -> MightyGraphAgent {
        MightyGraphAgent(id: id, parentID: parent, title: id, input: "check", status: status)
    }
    private func run(_ id: String, agents: [MightyGraphAgent] = [], status: String = "completed", entries: [LogEntry] = []) -> MightyGraphRun {
        MightyGraphRun(id: id, input: "request " + id, status: status, rootEntries: entries, agents: agents,
                       finalOutput: status == "completed" ? "result " + id : nil)
    }
    private func split(_ runs: [MightyGraphRun], running: Bool = false, expanded: Set<String> = [], blockSizes: [String: MightyGraphBlockSize] = [:],
                       executions: [MightyGraphLayout.Execution] = [], galleries: [MightyGraphLayout.ImageGallery] = [],
                       planRecords: [MightyGraphLayout.PlanRecordBlock] = []) -> MightyGraphLayout {
        .make(runs: runs, draft: "", running: running, expanded: expanded, blockSizes: blockSizes, executions: executions, galleries: galleries,
              planRecords: planRecords, splitsExecution: true)
    }
    private func id(_ run: String, _ suffix: String) -> String { MightyGraphBlockSize.nodeID(runID: run, suffix: suffix) }
    private func frame(_ layout: MightyGraphLayout, _ id: String) throws -> CGRect {
        try #require(layout.nodes.first { $0.id == id }).frame
    }

    @Test func theExecutionBlockHasAStableNodeID() {
        #expect(MightyGraphLayout.workSuffix == "execution")
        #expect(MightyGraphBlockSize.nodeID(runID: "one", suffix: MightyGraphLayout.workSuffix) == "3:one:execution")
        #expect(MightyGraphLayout.nodeID(run("one"), suffix: MightyGraphLayout.workSuffix) == id("one", "execution"))
        // Neither an attachment nor an Ouroboros execution panel.
        #expect(!MightyGraphCamera.isAuxiliary(nodeID: id("one", "execution")))
        #expect(id("one", "execution") != id("one", MightyGraphLayout.executionSuffix + "exec_1"))
        // Laid out twice, it is the same node in the same place.
        let runs = [run("one", agents: [agent("a")])]
        #expect(split(runs).nodes.map(\.id) == split(runs).nodes.map(\.id))
        #expect(split(runs).nodes.first { $0.content == .work(0) }?.id == id("one", "execution"))
    }

    @Test func theRequestSitsOverItsExecutionBlockInOneColumn() throws {
        let layout = split([run("one")])
        // Then the next request's draft, as under any finished request.
        #expect(layout.nodes.map(\.content) == [.request(0), .work(0), .result(0), .draft])
        let request = try frame(layout, id("one", "request"))
        let work = try frame(layout, id("one", "execution"))
        let result = try frame(layout, id("one", "result"))
        // The request holds its input alone, so it is the short block; the work is the tall one.
        #expect(request.height == MightyGraphLayout.requestHeight(expanded: false))
        #expect(work.height == 280)
        #expect(work.minY == request.maxY + MightyGraphLayout.rowGap)
        #expect(request.midX == work.midX && work.midX == result.midX)
        #expect(abs(work.midX - MightyGraphCamera.centreX) < 0.5)
        #expect(result.minY == work.maxY + MightyGraphLayout.rowGap)
        #expect(layout.edges.map(\.id) == [id("one", "request") + "→" + id("one", "execution"), id("one", "execution") + "→" + id("one", "result"),
                                           id("one", "result") + "→" + MightyGraphCamera.pendingNodeID])
        #expect(layout.edges.first?.joins == false)
        // Both are blocks a drag resizes and opening grows.
        #expect(layout.nodes.filter { [.request(0), .work(0)].contains($0.content) }.allSatisfy { $0.isResizable && !$0.isAuxiliary })
        let opened = split([run("one")], expanded: [id("one", "request"), id("one", "execution")])
        #expect(try frame(opened, id("one", "request")).height == MightyGraphLayout.requestHeight(expanded: true))
        #expect(try frame(opened, id("one", "execution")).height == 540)
        // A saved size wins, and a taller request pushes its 실행 block down.
        let sized = split([run("one")], blockSizes: [id("one", "request"): .init(width: 640, height: 300)])
        #expect(try frame(sized, id("one", "request")).size == CGSize(width: 640, height: 300))
        #expect(try frame(sized, id("one", "execution")).minY == 24 + 300 + MightyGraphLayout.rowGap)
    }

    @Test func subAgentsBranchFromTheExecutionBlock() throws {
        let agents = [agent("root"), agent("left", parent: "root"), agent("solo")]
        let layout = split([run("one", agents: agents)])
        let work = id("one", "execution")
        let request = id("one", "request")
        #expect(layout.edges.contains { $0.source == work && $0.target == id("one", "agent:root") })
        #expect(layout.edges.contains { $0.source == work && $0.target == id("one", "agent:solo") })
        #expect(layout.edges.contains { $0.source == id("one", "agent:root") && $0.target == id("one", "agent:left") })
        // The request's only edge is into its 실행 block.
        #expect(layout.edges.filter { $0.source == request }.map(\.target) == [work])
        let workFrame = try frame(layout, work)
        for agentID in ["root", "solo"] { #expect(try frame(layout, id("one", "agent:" + agentID)).minY == workFrame.maxY + MightyGraphLayout.rowGap) }
        // The result joins the leaves, not the 실행 block.
        let result = id("one", "result")
        #expect(Set(layout.edges.filter { $0.target == result }.map(\.source)) == [id("one", "agent:left"), id("one", "agent:solo")])
        // Resizing the 실행 block moves the agents under it.
        let taller = split([run("one", agents: agents)], blockSizes: [work: .init(width: 500, height: 400)])
        #expect(try frame(taller, id("one", "agent:root")).minY == workFrame.minY + 400 + MightyGraphLayout.rowGap)
        #expect(try frame(taller, request) == frame(layout, request))
    }

    @Test func anEmptyRunStillShowsItsExecutionBlock() throws {
        // Just sent: no record yet, no agents, nothing finished.
        let layout = split([run("new", status: "running")], running: true)
        #expect(layout.nodes.map(\.content) == [.request(0), .work(0)])
        #expect(layout.edges.map(\.target) == [id("new", "execution")])
        // Its edge runs blue while it works, as a running block's does.
        let live = MightyGraphLayout.liveNodeIDs([run("new", status: "running")])
        #expect(live == [id("new", "execution")])
        #expect(layout.routes(into: live).into.count == 1)
        // Settled, it is no longer live.
        #expect(MightyGraphLayout.liveNodeIDs([run("done")]).isEmpty)
    }

    @Test func theCameraStandsOnTheExecutionBlockWhileTheRequestRuns() {
        let running = [run("one"), run("two", agents: [agent("child", status: "running")], status: "running")]
        #expect(split(running, running: true).newestRequestTargetID(running) == id("two", "execution"))
        // A run whose own status ended but whose child still works is still running.
        var ending = running
        ending[1].status = "completed"
        #expect(split(ending, running: true).newestRequestTargetID(ending) == id("two", "execution"))
        // Finished, the request is where it starts.
        let done = [run("one"), run("two")]
        #expect(split(done).newestRequestTargetID(done) == id("two", "request"))
        // Without a 실행 block (the shared layout) it is the request, and nothing without a request.
        #expect(MightyGraphLayout.make(runs: running, draft: "", running: true, expanded: []).newestRequestTargetID(running) == id("two", "request"))
        #expect(split([]).newestRequestTargetID([]) == nil)
        // A new request still aims at its request block, top first, with the 실행 block right below it.
        #expect(MightyGraphCamera.reaimAnchor(newestRunID: "two", selectedNodeID: nil, layoutNodeIDs: Set(split(running, running: true).nodes.map(\.id)))
                == .reaim(nodeID: id("two", "request"), alignTop: true))
    }

    @Test func theRequestsAttachmentsHangOffItsExecutionBlock() throws {
        let runs = [run("one", agents: (0..<3).map { agent("branch-\($0)") }), run("two")]
        let plain = split(runs)
        let layout = split(runs, executions: [.init(runID: "one", key: "exec_1")],
                           galleries: [.init(runID: "one", step: MightyGraphImages.requestStep, count: 2)],
                           planRecords: [.init(runID: "one", recordID: "p1")])
        // Attachments only: the flow is where it was.
        for node in plain.nodes { #expect(layout.nodes.first { $0.id == node.id }?.frame == node.frame) }
        #expect(layout.edges.map(\.id) == plain.edges.map(\.id))
        let work = try frame(layout, id("one", "execution"))
        // The step's pictures keep their id and sit beside the 실행 block.
        let gallery = try frame(layout, id("one", MightyGraphLayout.imagesSuffix + "request"))
        #expect(gallery.minY == work.minY && gallery.minX > work.maxX)
        let execution = try frame(layout, id("one", MightyGraphLayout.executionSuffix + "exec_1"))
        #expect(execution.minY == work.minY && execution.minX > work.maxX)
        let record = try frame(layout, id("one", MightyGraphLayout.planRecordSuffix + "p1"))
        #expect(record.minY > execution.maxY && record.minX > work.maxX)
        for attachment in [gallery, execution, record] {
            for node in layout.nodes where node.frame != attachment { #expect(!node.frame.intersects(attachment), "\(node.id)") }
        }
    }

    @Test func withoutTheSplitTheSharedLayoutIsUnchanged() {
        let runs = [run("one", agents: [agent("a")]), run("two", status: "running")]
        let shared = MightyGraphLayout.make(runs: runs, draft: "", running: true, expanded: [])
        #expect(!shared.nodes.contains { if case .work = $0.content { return true }; return false })
        #expect(shared.edges.contains { $0.source == id("one", "request") && $0.target == id("one", "agent:a") })
        #expect(shared.nodes.first { $0.id == id("one", "request") }?.frame.height == 280)
    }

    @Test func aSizeSavedBeforeTheSplitMovesToTheExecutionBlockOnce() throws {
        let dragged = MightyGraphBlockSize(width: 1_100, height: 700)
        let own = MightyGraphBlockSize(width: 640, height: 400)
        let workspace = Workspace(id: "workspace", name: "Graph", path: "/tmp")
        // A pane saved before the split: no layout marker, a request block dragged large.
        var session = RunSession(id: "old", workspaceId: workspace.id, title: "Old")
        session.graphRuns = [MightyGraphRun(id: "a", status: "completed"), MightyGraphRun(id: "b", status: "completed")]
        session.graphBlockSizes = [id("a", "request"): dragged, id("b", "request"): dragged, id("b", "execution"): own]
        var wire = try #require(JSONSerialization.jsonObject(with: JSONEncoder().encode(session)) as? [String: Any])
        wire.removeValue(forKey: "graphSizesLayout")
        let legacy = try JSONDecoder().decode(RunSession.self, from: JSONSerialization.data(withJSONObject: wire))
        #expect(legacy.graphSizesLayout == nil)
        let migrated = try #require(StateRepository.normalize(AppSnapshot(workspaces: [workspace], sessions: [legacy]), restoring: true).sessions.first)
        // The request goes back to its own short size; its old size is the 실행 block's,
        // unless that block already had one.
        #expect(migrated.graphBlockSizes == [id("a", "execution"): dragged, id("b", "execution"): own])
        #expect(migrated.graphSizesLayout == MightyGraphBlockSize.splitLayout)
        let laid = split(migrated.graphRuns ?? [], blockSizes: migrated.graphBlockSizes ?? [:])
        #expect(try frame(laid, id("a", "request")).size == CGSize(width: MightyGraphCamera.requestWidth, height: MightyGraphLayout.requestHeight(expanded: false)))
        #expect(try frame(laid, id("a", "execution")).size == CGSize(width: 1_100, height: 700))
        // Once: a request size dragged after the split stays the request's.
        var later = migrated
        later.graphBlockSizes?[id("a", "request")] = own
        let again = try #require(StateRepository.normalize(AppSnapshot(workspaces: [workspace], sessions: [later]), restoring: true).sessions.first)
        #expect(again.graphBlockSizes?[id("a", "request")] == own)
        #expect(again.graphBlockSizes?[id("a", "execution")] == dragged)
        // A new pane is in the current layout from the start, and keeps that through a save.
        var fresh = RunSession(workspaceId: workspace.id, title: "New")
        #expect(fresh.graphSizesLayout == MightyGraphBlockSize.splitLayout)
        fresh.graphRuns = [MightyGraphRun(id: "a", status: "completed")]
        fresh.graphBlockSizes = [id("a", "request"): own]
        let decoded = try JSONDecoder().decode(RunSession.self, from: JSONEncoder().encode(fresh))
        let saved = try #require(StateRepository.normalize(AppSnapshot(workspaces: [workspace], sessions: [decoded]), restoring: true).sessions.first)
        #expect(saved.graphBlockSizes == [id("a", "request"): own])
    }

    @Test func aRequestAndItsExecutionBlockShareOneSavedSizePlace() throws {
        let size = MightyGraphBlockSize(width: 620, height: 360)
        let runs = (0..<MightyGraphBlockSize.maximumSavedSizes).map { MightyGraphRun(id: "run-\($0)") }
        var values: [String: MightyGraphBlockSize] = [:]
        for run in runs {
            values[id(run.id, "request")] = size
            values[id(run.id, "execution")] = size
        }
        let saved = try #require(MightyGraphBlockSize.normalized(values, runs: runs))
        // Every request keeps both sizes: the pair spends one place, not two.
        #expect(saved.count == 2 * MightyGraphBlockSize.maximumSavedSizes)
        #expect(saved[id("run-0", "request")] == size && saved[id("run-0", "execution")] == size)
    }

    @Test func anExecutionBlocksSavedSizeIsKept() throws {
        let size = MightyGraphBlockSize(width: 620, height: 360)
        let runs = [MightyGraphRun(id: "live", status: "running")]
        let saved = try #require(MightyGraphBlockSize.normalized([id("live", "execution"): size, id("gone", "execution"): size], runs: runs))
        #expect(saved == [id("live", "execution"): size])
    }
}
