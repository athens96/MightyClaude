import CoreGraphics
import Foundation
import Testing
@testable import MightyCore

/// The flow's fan-out row (`MightyGraphLayout.make(fanOut:)`): every sub-agent of a run under the block that launched
/// it, a background one too, a running one as its compact card; and the figures the tracker keeps for the cards.
/// Nothing the other views read changes: `shownAgents`, the timeline, the pipeline and the phone see what they saw.
struct MightyGraphFanOutTests {
    private func agent(_ id: String, parent: String? = nil, status: String = "completed", background: Bool = false, kind: String? = nil, source: String? = nil) -> MightyGraphAgent {
        MightyGraphAgent(id: id, parentID: parent, title: id, input: "확인", status: status, kind: kind, background: background ? true : nil, sourceBlockId: source)
    }
    private func run(_ id: String, agents: [MightyGraphAgent] = [], status: String = "completed", notice: Bool = false) -> MightyGraphRun {
        MightyGraphRun(id: id, input: "요청 " + id, status: status, rootEntries: [], agents: agents, finalOutput: status == "completed" ? "결과 " + id : nil,
                       noticeRun: notice ? true : nil)
    }
    private func layout(_ runs: [MightyGraphRun], running: Bool = false, opened: Set<String> = [], expanded: Set<String> = [],
                        sizes: [String: MightyGraphBlockSize] = [:]) -> MightyGraphLayout {
        MightyGraphLayout.make(runs: MightyGraphSupport.shownRuns(runs), draft: "", running: running, expanded: expanded, blockSizes: sizes,
                               splitsExecution: true, fanOut: .init(opened: opened))
    }
    private func plain(_ runs: [MightyGraphRun], running: Bool = false) -> MightyGraphLayout {
        MightyGraphLayout.make(runs: MightyGraphSupport.shownRuns(runs), draft: "", running: running, expanded: [], splitsExecution: true)
    }
    private func id(_ run: MightyGraphRun, _ agent: String) -> String { MightyGraphLayout.nodeID(run, suffix: "agent:" + agent) }
    private func node(_ layout: MightyGraphLayout, _ id: String) -> MightyGraphLayout.Node? { layout.nodes.first { $0.id == id } }

    // MARK: What a shown run carries

    @Test func aShownRunCarriesItsBackgroundSubagentsForTheFlowAlone() {
        let request = run("r", agents: [agent("fg"), agent("bg", status: "running", background: true), agent("bg-child", parent: "bg", status: "running"),
                                        agent("task", status: "running", background: true, kind: "task"), agent("task-child", parent: "task"), agent("late")])
        let shown = request.shown
        // Every other view draws what it drew.
        #expect(shown.agents.map(\.id) == ["fg", "late"])
        #expect(BackgroundNoticeRuns.shownAgents(request).map(\.id) == ["fg", "late"])
        // The flow draws them too: the same blocks at the same indexes, then the background sub-agents and what is under
        // them. A background run and what is under it stay with the strip and the notice runs.
        #expect(shown.flowAgents.map(\.id) == ["fg", "late", "bg", "bg-child"])
        #expect(Array(shown.flowAgents.prefix(shown.agents.count)) == shown.agents)
        // Each stands where it was launched.
        #expect(shown.flowAgents.indices.map(shown.flowOrder) == [0, 5, 1, 2])
        #expect(BackgroundNoticeRuns.fanOut(request) == [1, 2])
        // Shown again, it is the same run.
        #expect(shown.shown == shown)
        #expect(shown.shown.flowAgents.map(\.id) == ["fg", "late", "bg", "bg-child"])
    }

    @Test func aRunThatHidesNothingAndANoticeRunHaveOnlyTheirOwnBlocks() {
        let quiet = run("r", agents: [agent("a"), agent("b", parent: "a")])
        #expect(quiet.shown.flowAgents == quiet.agents)
        #expect(quiet.flowOrder(1) == 1)
        #expect(BackgroundNoticeRuns.fanOut(quiet).isEmpty)
        let notice = run("r:notice:1", agents: [agent("copy", background: true, source: "bg")], notice: true)
        #expect(notice.shown.flowAgents.map(\.id) == ["copy"])
        #expect(BackgroundNoticeRuns.fanOut(notice).isEmpty)
        // Only tasks in the background: nothing fans out.
        #expect(run("t", agents: [agent("task", background: true, kind: "task")]).shown.flowAgents.isEmpty)
    }

    @Test func theStatusRulesReadTheSameWithAFanOut() {
        let request = run("r", agents: [agent("bg", status: "running", background: true)])
        let runs = MightyGraphSupport.shownRuns([request])
        // The request's turn ended while its background agent runs: it awaits the notice, as before.
        #expect(runs[0].settled)
        #expect(MightyGraphLayout.finished(runs[0]))
        #expect(MightyGraphLayout.hasResult(runs[0]))
        #expect(runs[0].backgroundWaiting == 1)
        #expect(BackgroundNoticeRuns.awaitingNotice(runs[0], in: runs) != nil)
        // The timeline and the pipeline still leave the block out.
        #expect(MightyTimeline.groups([request])[0].rows.count == 2)
        #expect(MightyStructure.clusters(request).isEmpty)
        #expect(MobileMightySupport.blocks(request, ordinal: 1).count == 1)
    }

    // MARK: Layout

    @Test func withoutAFanOutTheLayoutIsWhatItWas() {
        let request = run("r", agents: [agent("fg", status: "running"), agent("bg", status: "running", background: true)], status: "running")
        let before = plain([request], running: true)
        #expect(node(before, id(request, "bg")) == nil)
        let fg = node(before, id(request, "fg"))
        #expect(fg?.frame.size == CGSize(width: 360, height: 240))
        #expect(fg?.isCompact == false && fg?.isResizable == true && fg?.isFixed == false)
    }

    @Test func aBackgroundSubagentStandsUnderTheBlockThatLaunchedItAsACard() throws {
        let request = run("r", agents: [agent("bg", status: "running", background: true)], status: "running")
        let graph = layout([request], running: true)
        let card = try #require(node(graph, id(request, "bg")))
        #expect(card.isCompact)
        #expect(card.frame.size == FlowCard.size)
        #expect(!card.isResizable && card.isFixed)
        #expect(card.content == .agent(0, 0))
        let work = try #require(graph.nodes.first { $0.content == .work(0) })
        #expect(graph.edges.contains { $0.source == work.id && $0.target == card.id })
        #expect(card.frame.minY == work.frame.maxY + 32)
        #expect(abs(card.frame.midX - work.frame.midX) < 0.5)
        // The edge into a running card is a live one.
        #expect(MightyGraphLayout.liveNodeIDs(MightyGraphSupport.shownRuns([request]), fanOut: true).contains(card.id))
        #expect(!MightyGraphLayout.liveNodeIDs(MightyGraphSupport.shownRuns([request])).contains(card.id))
    }

    @Test func aFinishedBackgroundCardStaysAndLeadsIntoTheResult() throws {
        let request = run("r", agents: [agent("bg", background: true)])
        let graph = layout([request])
        let card = try #require(node(graph, id(request, "bg")))
        #expect(card.isCompact && card.frame.size == FlowCard.size)
        let result = try #require(graph.nodes.first { $0.content == .result(0) })
        #expect(graph.edges.contains { $0.source == card.id && $0.target == result.id && $0.joins })
        #expect(result.frame.minY == card.frame.maxY + 32)
    }

    @Test func aRunningForegroundSubagentIsACardAndItsFullBlockOnceItEnds() throws {
        var request = run("r", agents: [agent("fg", status: "running")], status: "running")
        let running = layout([request], running: true)
        #expect(node(running, id(request, "fg"))?.isCompact == true)
        #expect(node(running, id(request, "fg"))?.frame.size == FlowCard.size)
        request.agents[0].status = "completed"
        let ended = layout([request], running: true)
        let block = try #require(node(ended, id(request, "fg")))
        #expect(!block.isCompact && block.isResizable && !block.isFixed)
        #expect(block.frame.size == CGSize(width: 360, height: 240))
        // Exactly the block the layout always drew for it.
        #expect(block.frame == node(plain([request], running: true), id(request, "fg"))?.frame)
        // Its saved size and its opened height are back with it.
        let saved = layout([request], running: true, sizes: [block.id: MightyGraphBlockSize(width: 420, height: 300)])
        #expect(node(saved, block.id)?.frame.size == CGSize(width: 420, height: 300))
        #expect(node(layout([request], running: true, expanded: [block.id]), block.id)?.frame.height == 480)
    }

    @Test func aCardIgnoresASavedSizeAndTheOpenedHeight() {
        let request = run("r", agents: [agent("fg", status: "running")], status: "running")
        let cardID = id(request, "fg")
        let graph = layout([request], running: true, expanded: [cardID], sizes: [cardID: MightyGraphBlockSize(width: 420, height: 300)])
        #expect(node(graph, cardID)?.frame.size == FlowCard.size)
    }

    @Test func anOpenedCardIsTheFullBlockWithItsSavedSize() throws {
        let request = run("r", agents: [agent("fg", status: "running"), agent("bg", status: "running", background: true)], status: "running")
        let fg = id(request, "fg"), bg = id(request, "bg")
        let graph = layout([request], running: true, opened: [bg], sizes: [bg: MightyGraphBlockSize(width: 400, height: 260)])
        #expect(node(graph, fg)?.isCompact == true)
        let block = try #require(node(graph, bg))
        #expect(!block.isCompact && block.isResizable)
        #expect(block.frame.size == CGSize(width: 400, height: 260))
        // Cards and an opened block share the row, each top at the row's top.
        #expect(node(graph, fg)?.frame.minY == block.frame.minY)
    }

    @Test func aCardDrawsNothingUnderItAndAnOpenedOneDrawsItsChildren() throws {
        let agents = [agent("lead", status: "running"), agent("child", parent: "lead", status: "running"), agent("grand", parent: "child", status: "running"),
                      agent("note", parent: "lead", kind: "compact")]
        let request = run("r", agents: agents, status: "running")
        let folded = layout([request], running: true)
        #expect(node(folded, id(request, "lead"))?.isCompact == true)
        for hidden in ["child", "grand", "note"] { #expect(node(folded, id(request, hidden)) == nil, "\(hidden)") }
        // Request, 실행 and the one card: what is under the card is laid out nowhere, not even as a stray root.
        #expect(folded.nodes.count == 3)
        #expect(FlowCard.nested(under: agents[0], in: request) == 2)
        let lead = id(request, "lead")
        let opened = layout([request], running: true, opened: [lead])
        #expect(node(opened, lead)?.isCompact == false)
        // Its running child is a card of its own, which counts the grandchild; the compaction block is drawn as it was.
        let child = try #require(node(opened, id(request, "child")))
        #expect(child.isCompact)
        #expect(node(opened, id(request, "grand")) == nil)
        #expect(node(opened, id(request, "note"))?.isCompact == false)
        #expect(opened.edges.contains { $0.source == lead && $0.target == child.id })
        #expect(opened.nodes.count == 5)
    }

    @Test func theRowIsInLaunchOrderBackgroundOrNot() throws {
        let request = run("r", agents: [agent("first", status: "running"), agent("second", status: "running", background: true), agent("third", status: "running"),
                                        agent("fourth", status: "running", background: true)], status: "running")
        let graph = layout([request], running: true)
        let row = try ["first", "second", "third", "fourth"].map { try #require(node(graph, id(request, $0))) }
        #expect(row.map(\.frame.minX) == row.map(\.frame.minX).sorted())
        #expect(Set(row.map(\.frame.minY)).count == 1)
        for (left, right) in zip(row, row.dropFirst()) { #expect(right.frame.minX - left.frame.maxX == 20) }
        // The row is centred under the block that launched it.
        let work = try #require(graph.nodes.first { $0.content == .work(0) })
        #expect(abs((row[0].frame.minX + row[3].frame.maxX) / 2 - work.frame.midX) < 0.5)
        // `Content.agent` indexes the run's flow blocks: the visible ones first, at the indexes they always had.
        #expect(row.map(\.content) == [.agent(0, 0), .agent(0, 2), .agent(0, 1), .agent(0, 3)])
        let shown = MightyGraphSupport.shownRuns([request])[0]
        #expect(shown.flowAgents.map(\.id) == ["first", "third", "second", "fourth"])
    }

    @Test func aNoticeRunsCopiesAreTheFullBlocksTheyWere() throws {
        let request = run("r", agents: [agent("bg", background: true)])
        let notice = run("r:notice:1", agents: [agent("r:notice:1:bg", background: true, source: "bg")], notice: true)
        let graph = layout([request, notice])
        let copy = try #require(node(graph, id(notice, "r:notice:1:bg")))
        #expect(!copy.isCompact && copy.frame.size == CGSize(width: 360, height: 240))
        // Where and how large it always was; only lower, by the card row the request now has above it.
        let before = try #require(node(plain([request, notice]), copy.id))
        #expect(copy.frame.size == before.frame.size && copy.frame.minX == before.frame.minX)
        #expect(copy.frame.minY == before.frame.minY + FlowCard.size.height + 32)
        // The request keeps its card above, which leads to this copy.
        #expect(node(graph, id(request, "bg"))?.isCompact == true)
        let shown = MightyGraphSupport.shownRuns([request, notice])
        #expect(FlowCard.copyNodeID(of: shown[0].flowAgents[0], in: shown) == copy.id)
    }

    @Test func aBackgroundRunStaysOutOfTheRow() {
        let request = run("r", agents: [agent("task", status: "running", background: true, kind: "task"), agent("bg", status: "running", background: true)], status: "running")
        let graph = layout([request], running: true)
        #expect(node(graph, id(request, "task")) == nil)
        #expect(node(graph, id(request, "bg"))?.isCompact == true)
    }

    @Test func theFollowedBlockIsTheLowestOfTheNewestWorkCardsIncluded() throws {
        let request = run("r", agents: [agent("one", status: "running", background: true), agent("two", status: "running", background: true)], status: "running")
        let runs = MightyGraphSupport.shownRuns([request])
        let graph = MightyGraphLayout.make(runs: runs, draft: "", running: true, expanded: [], splitsExecution: true, fanOut: .init())
        // While the request runs, the cards are its lowest blocks: the one launched last is followed.
        #expect(MightyGraphFollow.targetID(graph, runs: runs) == id(request, "two"))
        // Its turn ended: the 알림 대기 block under the cards is the lowest.
        var ended = request; ended.status = "completed"; ended.finalOutput = "launched"
        let endedRuns = MightyGraphSupport.shownRuns([ended])
        let endedGraph = MightyGraphLayout.make(runs: endedRuns, draft: "", running: true, expanded: [], splitsExecution: true, fanOut: .init())
        #expect(MightyGraphFollow.targetID(endedGraph, runs: endedRuns) == MightyGraphLayout.nodeID(ended, suffix: "result"))
        #expect(endedGraph.newestRequestTargetID(endedRuns) == MightyGraphLayout.nodeID(ended, suffix: "request"))
        // A card is no place the camera is re-aimed at after a trim: the request is.
        #expect(MightyGraphCamera.reaimAnchor(newestRunID: ended.id, selectedNodeID: nil, layoutNodeIDs: Set(endedGraph.nodes.map(\.id)))
                == .reaim(nodeID: MightyGraphLayout.nodeID(ended, suffix: "request"), alignTop: true))
    }

    // MARK: The tracker's figures

    private func send(_ value: [String: Any], to parser: CLIStreamParser) throws {
        var data = try JSONSerialization.data(withJSONObject: value, options: [.sortedKeys, .withoutEscapingSlashes])
        data.append(10); parser.push(data)
    }
    private func latest(_ nodes: [ExecutionGraphNode], tool: String) throws -> ExecutionGraphNode {
        let id = ExecutionGraphSupport.agentNodeID(runId: "run", toolUseId: tool)
        return try #require(nodes.last { $0.id == id })
    }
    private func child(_ blocks: [[String: Any]], id: String, parent: String, at time: String? = nil) -> [String: Any] {
        var value: [String: Any] = ["type": "assistant", "uuid": id, "parent_tool_use_id": parent, "message": ["id": id, "content": blocks]]
        if let time { value["timestamp"] = time }
        return value
    }

    @Test func aSubagentBlockKeepsWhenItStartedAndEndedByItsFramesOwnTime() throws {
        var nodes: [ExecutionGraphNode] = []
        let parser = CLIStreamParser(provider: "claude", log: { _, _ in }, resume: { _ in }, activityNamespace: "run", graph: { nodes.append($0) }, graphInput: "request")
        try send(["type": "assistant", "uuid": "m1", "timestamp": "2026-11-10T18:56:44.496Z",
                  "message": ["id": "m1", "content": [["type": "tool_use", "id": "a", "name": "Agent", "input": ["description": "Count", "prompt": "Go"]]]]], to: parser)
        #expect(try latest(nodes, tool: "a").figures == GraphAgentFigures(startedAt: "2026-11-10T18:56:44.496Z"))
        try send(["type": "user", "timestamp": "2026-11-10T18:57:02.812Z", "message": ["content": [["type": "tool_result", "tool_use_id": "a", "content": "3"]]]], to: parser)
        let ended = try latest(nodes, tool: "a")
        #expect(ended.state == "completed")
        #expect(ended.figures == GraphAgentFigures(startedAt: "2026-11-10T18:56:44.496Z", endedAt: "2026-11-10T18:57:02.812Z"))
        // A later word about a block that ended does not move its end.
        try send(["type": "user", "timestamp": "2026-11-10T19:00:00.000Z", "message": ["content": [["type": "tool_result", "tool_use_id": "a", "content": "again"]]]], to: parser)
        #expect(try latest(nodes, tool: "a").figures?.endedAt == "2026-11-10T18:57:02.812Z")
        // A frame without a time of its own starts the block now.
        try send(["type": "assistant", "uuid": "m2", "message": ["id": "m2", "content": [["type": "tool_use", "id": "b", "name": "Agent", "input": ["description": "Live", "prompt": "Go"]]]]], to: parser)
        let live = try #require(try latest(nodes, tool: "b").figures?.started)
        #expect(abs(live.timeIntervalSinceNow) < 60)
        // The process ending ends what still ran, now.
        parser.finishGraph(state: "stopped")
        let stopped = try latest(nodes, tool: "b")
        #expect(stopped.state == "stopped")
        #expect(stopped.figures?.ended != nil)
        // Only a sub-agent block has figures.
        #expect(nodes.filter { $0.kind == "main" }.allSatisfy { $0.figures == nil })
    }

    @Test func toolCallsAreCountedOnceAndTheCLIsOwnCountWinsWhenItIsMore() throws {
        var nodes: [ExecutionGraphNode] = []
        let parser = CLIStreamParser(provider: "claude", log: { _, _ in }, resume: { _ in }, activityNamespace: "run", graph: { nodes.append($0) }, graphInput: "request")
        try send(["type": "assistant", "uuid": "m1", "message": ["id": "m1", "content": [["type": "tool_use", "id": "a", "name": "Agent", "input": ["description": "Count", "prompt": "Go"]]]]], to: parser)
        #expect(try latest(nodes, tool: "a").figures?.toolCalls == nil)
        let read = child([["type": "tool_use", "id": "r1", "name": "Read", "input": ["file_path": "a.txt"]]], id: "c1", parent: "a")
        try send(read, to: parser)
        #expect(try latest(nodes, tool: "a").figures?.toolCalls == 1)
        // The same message again is the same call.
        try send(read, to: parser)
        #expect(try latest(nodes, tool: "a").figures?.toolCalls == 1)
        try send(child([["type": "tool_use", "id": "r2", "name": "Read", "input": ["file_path": "b.txt"]], ["type": "tool_use", "id": "r3", "name": "Grep", "input": ["pattern": "x"]]], id: "c2", parent: "a"), to: parser)
        #expect(try latest(nodes, tool: "a").figures?.toolCalls == 3)
        // The CLI's progress report names more than the stream showed: its count is taken; a smaller one is not.
        try send(["type": "system", "subtype": "task_progress", "task_id": "t", "tool_use_id": "a", "description": "Reading", "usage": ["total_tokens": 100, "tool_uses": 5, "duration_ms": 10]], to: parser)
        #expect(try latest(nodes, tool: "a").figures?.toolCalls == 5)
        try send(["type": "system", "subtype": "task_progress", "task_id": "t", "tool_use_id": "a", "description": "Reading", "usage": ["tool_uses": 2]], to: parser)
        #expect(try latest(nodes, tool: "a").figures?.toolCalls == 5)
        // A report that comes before the message that makes the call does not count it twice.
        try send(["type": "assistant", "uuid": "m4", "message": ["id": "m4", "content": [["type": "tool_use", "id": "b", "name": "Agent", "input": ["description": "Other", "prompt": "Go"]]]]], to: parser)
        try send(["type": "system", "subtype": "task_progress", "task_id": "u", "tool_use_id": "b", "description": "Reading", "usage": ["tool_uses": 1]], to: parser)
        try send(child([["type": "tool_use", "id": "b-read", "name": "Read", "input": ["file_path": "a.txt"]]], id: "c3", parent: "b"), to: parser)
        #expect(try latest(nodes, tool: "b").figures?.toolCalls == 1)
        // The main session's own tool calls are not a sub-agent's.
        try send(["type": "assistant", "uuid": "m3", "message": ["id": "m3", "content": [["type": "tool_use", "id": "main-read", "name": "Read", "input": ["file_path": "c.txt"]]]]], to: parser)
        #expect(try latest(nodes, tool: "a").figures?.toolCalls == 5)
    }

    @Test func theRecordedBackgroundAgentHasItsStartItsToolCallsAndItsTokens() throws {
        var session = RunSession(id: "pane", workspaceId: "space", title: "Claude", provider: "claude")
        session.recordGraph(RunEvent(sessionId: "pane", type: "log", entry: LogEntry(id: "request-one", kind: "user", text: "probe", provider: "claude")))
        let box = GraphAgentIdentityTests.Box(session)
        var snapshots: [[MightyGraphRun]] = []
        let parser = CLIStreamParser(provider: "claude", log: { _, _ in }, resume: { _ in }, activityNamespace: "recorded",
                                     graph: { box.value.recordGraph(RunEvent(sessionId: "pane", type: "graph", graph: $0)); snapshots.append(box.value.mightyGraphRuns) }, graphInput: "probe")
        for line in try GraphAgentIdentityTests.fixture("claude-stream-subagents") { parser.push(line + Data([10])) }
        parser.flush(); parser.finishGraph(state: "completed")
        let runs = box.value.mightyGraphRuns
        let agents = runs[0].agents.filter(\.isSubagent)
        #expect(agents.map(\.title) == ["count a", "count b and c"])
        // Each started at the time of the frame that called it; the stream named no time for their ends, which are when
        // the app read them.
        #expect(agents.map { $0.figures?.startedAt } == ["2026-11-10T18:56:31.912Z", "2026-11-10T18:56:44.496Z"])
        #expect(agents.allSatisfy { $0.figures?.ended != nil })
        // The CLI reports a call before the message that makes it arrives: one call is counted once.
        #expect(agents.map { $0.figures?.toolCalls } == [1, 2])
        #expect(agents.allSatisfy { ($0.usage?.total ?? 0) > 0 })
        // While the background agent ran, the request's flow showed it as a running card with its figures so far.
        let live = snapshots.compactMap { runs -> FlowCard.Content? in
            let shown = MightyGraphSupport.shownRuns(runs)[0]
            guard let agent = shown.flowAgents.first(where: { $0.title == "count b and c" }), agent.status == "running", agent.figures?.toolCalls == 1 else { return nil }
            return FlowCard.content(agent, in: shown)
        }
        let card = try #require(live.last)
        #expect(card.running)
        #expect(card.identity == "Explore · Haiku 5.5")
        #expect(card.toolCalls == 1)
        #expect((card.tokens ?? 0) > 0)
        #expect(card.marks == [.background])
        #expect(card.activity?.isEmpty == false)
        // Once it ended it is still the request's card, and it leads to its copy in the notice run.
        let shown = MightyGraphSupport.shownRuns(runs)
        let ended = try #require(shown[0].flowAgents.first { $0.title == "count b and c" })
        #expect(FlowCard.isCompact(ended, opened: false))
        #expect(FlowCard.content(ended, in: shown[0]).activity == "b.txt: 3")
        let graph = MightyGraphLayout.make(runs: shown, draft: "", running: false, expanded: [], splitsExecution: true, fanOut: .init())
        let copy = try #require(FlowCard.copyNodeID(of: ended, in: shown))
        #expect(graph.nodes.contains { $0.id == copy && !$0.isCompact })
        #expect(graph.nodes.contains { $0.id == MightyGraphLayout.nodeID(shown[0], suffix: "agent:" + ended.id) && $0.isCompact })
        // The foreground agent ended as its full block.
        let foreground = try #require(shown[0].flowAgents.first { $0.title == "count a" })
        #expect(!FlowCard.isCompact(foreground, opened: false))
    }
}
