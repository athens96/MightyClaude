import CoreGraphics
import Foundation
import Testing
@testable import MightyCore

/// The delegate line on the branch from a block to its sub-agents and the return node at a request's result position
/// (`FlowDelegation`, `FlowReturn`): what they say, where the layout puts them, and that the camera's follow rule and
/// the live edges go on working with them.
struct FlowLinesTests {
    private func agent(_ id: String, parent: String? = nil, status: String = "running", background: Bool = false, kind: String? = nil, source: String? = nil,
                       requested: String? = nil, resolved: String? = nil, answered: String? = nil, type: String? = nil) -> MightyGraphAgent {
        let identity = GraphAgentIdentity(type: type, requestedModel: requested, resolvedModel: resolved)
        let records = answered.map { [GraphResponseRecord(responseId: id + "-m", model: $0, usage: GraphTokenUsage(inputTokens: 10, outputTokens: 1))] }
        return MightyGraphAgent(id: id, parentID: parent, title: id, input: "확인", status: status, kind: kind, responseRecords: records,
                                background: background ? true : nil, sourceBlockId: source, identity: identity.isEmpty ? nil : identity)
    }
    private func run(_ id: String, agents: [MightyGraphAgent] = [], status: String = "running", notice: Bool = false) -> MightyGraphRun {
        MightyGraphRun(id: id, input: "요청 " + id, status: status, rootEntries: [], agents: agents, finalOutput: status == "completed" ? "결과 " + id : nil,
                       noticeRun: notice ? true : nil)
    }
    private func layout(_ runs: [MightyGraphRun], running: Bool = true, opened: Set<String> = [], plan: String? = nil) -> MightyGraphLayout {
        MightyGraphLayout.make(runs: MightyGraphSupport.shownRuns(runs), draft: "", running: running, expanded: [], planRunID: plan,
                               splitsExecution: true, fanOut: .init(opened: opened))
    }
    private func id(_ run: MightyGraphRun, _ suffix: String) -> String { MightyGraphLayout.nodeID(run, suffix: suffix) }
    private func node(_ layout: MightyGraphLayout, _ id: String) -> MightyGraphLayout.Node? { layout.nodes.first { $0.id == id } }

    // MARK: The delegate line

    @Test func theLineSaysHowManyOnWhichModelAndHowManyStillRun() throws {
        try LocaleOverride.$language.withValue(.ko) { () throws in
            let three = [agent("a", answered: "claude-haiku-5-5"), agent("b", answered: "claude-haiku-5-5"), agent("c", status: "completed", answered: "claude-haiku-5-5")]
            let line = try #require(FlowDelegation.line(three))
            #expect(line.count == 3 && line.running == 2)
            #expect(line.head == "하위 에이전트 3개에 위임")
            #expect(line.model == "Haiku 5.5")
            #expect(line.progress == "2/3 실행 중")
            #expect(line.text == "하위 에이전트 3개에 위임 · Haiku 5.5 · 2/3 실행 중")
            // All ended: the count of those running goes.
            let ended = try #require(FlowDelegation.line(three.map { var agent = $0; agent.status = "completed"; return agent }))
            #expect(ended.progress == nil)
            #expect(ended.text == "하위 에이전트 3개에 위임 · Haiku 5.5")
            // One sub-agent is said in the singular, and one that names no model says none.
            let one = try #require(FlowDelegation.line([agent("solo", status: "completed")]))
            #expect(one.text == "하위 에이전트 1개에 위임")
            #expect(FlowDelegation.line([]) == nil)
        }
    }

    @Test func aSharedModelIsNamedAndSeveralAreCounted() {
        LocaleOverride.$language.withValue(.ko) {
            // Two models answered: their number, never one of the two.
            #expect(FlowDelegation.line([agent("a", answered: "claude-haiku-5-5"), agent("b", answered: "claude-opus-5-5")])?.model == "모델 2종")
            // The model the CLI resolved at launch stands in until the block answers.
            #expect(FlowDelegation.line([agent("a", resolved: "claude-haiku-5-5"), agent("b", answered: "claude-haiku-5-5")])?.model == "Haiku 5.5")
            // An alias that was only asked for is the same model as a named one of its family…
            #expect(FlowDelegation.line([agent("a", answered: "claude-haiku-5-5"), agent("b", requested: "haiku")])?.model == "Haiku 5.5")
            // …and another one when it is of another family.
            #expect(FlowDelegation.line([agent("a", answered: "claude-haiku-5-5"), agent("b", requested: "opus")])?.model == "모델 2종")
            // Nothing named yet: the alias, marked as requested.
            #expect(FlowDelegation.line([agent("a", requested: "haiku"), agent("b", requested: "haiku")])?.model == "Haiku · 요청")
            #expect(FlowDelegation.line([agent("a", requested: "haiku"), agent("b", requested: "sonnet")])?.model == "모델 2종")
            // A block that says nothing about its model does not make the others' model uncertain.
            #expect(FlowDelegation.line([agent("a", answered: "claude-haiku-5-5"), agent("b")])?.model == "Haiku 5.5")
            // A definition's model stands in for a call that asked for none; "inherit" asks for none.
            let definitions = ["reviewer": AgentDefinition(name: "reviewer", model: "sonnet"), "scout": AgentDefinition(name: "scout", model: "inherit")]
            #expect(FlowDelegation.line([agent("a", type: "reviewer")], definitions: definitions)?.model == "Sonnet · 요청")
            #expect(FlowDelegation.line([agent("a", type: "scout")], definitions: definitions)?.model == nil)
            #expect(FlowDelegation.line([agent("a", type: "reviewer")])?.model == nil)
        }
    }

    @Test func aBlocksDelegatesAreTheSubagentsRightUnderIt() {
        let agents = [agent("first"), agent("bg", background: true), agent("nested", parent: "first"), agent("deep", parent: "nested"),
                      agent("note", kind: "compact"), agent("task", background: true, kind: "task"), agent("orphan", parent: "ghost"), agent("self", parent: "self")]
        let shown = run("r", agents: agents).shown
        // The main session's: no parent, an unknown one, or itself, in launch order, background or not; never a block
        // that is no sub-agent.
        #expect(FlowDelegation.children(of: FlowDelegation.mainKey, in: shown).map(\.id) == ["first", "bg", "orphan", "self"])
        #expect(FlowDelegation.children(of: "first", in: shown).map(\.id) == ["nested"])
        #expect(FlowDelegation.children(of: "nested", in: shown).map(\.id) == ["deep"])
        #expect(FlowDelegation.children(of: "deep", in: shown).isEmpty)
        // A notice run's copies were delegated by the request, not by the notice turn.
        let notice = run("r:notice:1", agents: [agent("copy", status: "completed", background: true, source: "bg")], status: "completed", notice: true)
        #expect(FlowDelegation.children(of: FlowDelegation.mainKey, in: notice).isEmpty)
    }

    // MARK: The return node

    @Test func theWayBackSaysWhatIsTrueAtThatMoment() throws {
        try LocaleOverride.$language.withValue(.ko) { () throws in
            let agents = [agent("a"), agent("b", status: "completed"), agent("c", parent: "a")]
            let waiting = try #require(FlowReturn.state(run("r", agents: agents)))
            #expect(waiting.total == 3 && waiting.running == 2 && waiting.waiting)
            #expect(waiting.text == "하위 에이전트 2/3 실행 중 · 보고를 기다리는 중")
            // Every sub-agent ended while the main session's turn goes on.
            let back = try #require(FlowReturn.state(run("r", agents: agents.map { var agent = $0; agent.status = "completed"; return agent })))
            #expect(!back.waiting)
            #expect(back.text == "메인으로 복귀 · 검토 중")
            // A run that delegated to nobody has no way back to show, and neither has a notice run.
            #expect(FlowReturn.state(run("r")) == nil)
            #expect(FlowReturn.state(run("r", agents: [agent("task", background: true, kind: "task"), agent("note", kind: "compact")])) == nil)
            #expect(FlowReturn.state(run("r:notice:1", agents: [agent("copy", status: "completed", background: true, source: "x")], notice: true)) == nil)
            // A background sub-agent counts, through the run as views draw it.
            #expect(FlowReturn.state(run("r", agents: [agent("bg", background: true)]).shown)?.total == 1)
        }
    }

    // MARK: Layout

    @Test func theDelegateLineStandsBetweenABlockAndItsRow() throws {
        let request = run("r", agents: [agent("a"), agent("b", background: true), agent("c")])
        let graph = layout([request])
        let work = try #require(graph.nodes.first { $0.content == .work(0) })
        let line = try #require(node(graph, id(request, MightyGraphLayout.delegateSuffix + FlowDelegation.mainKey)))
        #expect(line.content == .delegate(0, FlowDelegation.mainKey))
        #expect(line.frame.size == FlowDelegation.size)
        #expect(line.frame.minY == work.frame.maxY + 18)
        #expect(abs(line.frame.midX - work.frame.midX) < 0.5)
        #expect(line.isFixed && !line.isResizable && !line.isAuxiliary && !line.isCompact)
        // The block's branch goes into the line, and the line fans out to every card of the row.
        #expect(graph.edges.contains { $0.source == work.id && $0.target == line.id })
        for child in ["a", "b", "c"] {
            let card = try #require(node(graph, id(request, "agent:" + child)))
            #expect(graph.edges.contains { $0.source == line.id && $0.target == card.id })
            #expect(!graph.edges.contains { $0.source == work.id && $0.target == card.id })
            #expect(card.frame.minY == line.frame.maxY + 26)
        }
        // Nothing overlaps: the line clears the block above and the row below.
        for other in graph.nodes where other.id != line.id { #expect(!other.frame.intersects(line.frame), "\(other.id)") }
    }

    @Test func aRunWithoutSubagentsAndALayoutWithoutAFanOutHaveNoLineAndNoReturnNode() {
        let plainRun = run("r", agents: [agent("note", status: "completed", kind: "compact")])
        let graph = layout([plainRun])
        #expect(!graph.nodes.contains { if case .delegate = $0.content { true } else { false } })
        #expect(!graph.nodes.contains { if case .returning = $0.content { true } else { false } })
        let request = run("r", agents: [agent("a")])
        let before = MightyGraphLayout.make(runs: [request], draft: "", running: true, expanded: [], splitsExecution: true)
        #expect(before.nodes.map(\.content) == [.request(0), .work(0), .agent(0, 0)])
        // The shared vectors' layout (the request holding its own work) is as untouched.
        #expect(MightyGraphLayout.make(runs: [request], draft: "", running: true, expanded: []).nodes.map(\.content) == [.request(0), .agent(0, 0)])
    }

    @Test func anOpenedSubagentThatDelegatedHasALineOfItsOwn() throws {
        let request = run("r", agents: [agent("lead"), agent("child", parent: "lead"), agent("note", parent: "lead", status: "completed", kind: "compact")])
        let leadID = id(request, "agent:lead")
        // Folded, the card draws nothing under it: no line either.
        #expect(node(layout([request]), id(request, MightyGraphLayout.delegateSuffix + "lead")) == nil)
        let graph = layout([request], opened: [leadID])
        let lead = try #require(node(graph, leadID))
        let line = try #require(node(graph, id(request, MightyGraphLayout.delegateSuffix + "lead")))
        #expect(line.content == .delegate(0, "lead"))
        #expect(line.frame.minY == lead.frame.maxY + 18)
        #expect(abs(line.frame.midX - lead.frame.midX) < 0.5)
        // Every block of the row hangs off the line, the compaction block too.
        for child in ["child", "note"] { #expect(graph.edges.contains { $0.source == line.id && $0.target == id(request, "agent:" + child) }, "\(child)") }
        // The main session's own line is still there, above.
        #expect(node(graph, id(request, MightyGraphLayout.delegateSuffix + FlowDelegation.mainKey)) != nil)
        // A block dragged narrower than the line still holds it within its own branch.
        let narrow = MightyGraphLayout.make(runs: MightyGraphSupport.shownRuns([request]), draft: "", running: true, expanded: [],
                                            blockSizes: [leadID: MightyGraphBlockSize(width: 300, height: 200)], splitsExecution: true, fanOut: .init(opened: [leadID]))
        let narrowLine = try #require(node(narrow, id(request, MightyGraphLayout.delegateSuffix + "lead")))
        for other in narrow.nodes where other.id != narrowLine.id { #expect(!other.frame.intersects(narrowLine.frame), "\(other.id)") }
    }

    @Test func theReturnNodeStandsWhereTheResultWillUntilItIsThere() throws {
        var request = run("r", agents: [agent("a"), agent("b")])
        let graph = layout([request])
        let back = try #require(node(graph, id(request, MightyGraphLayout.returnSuffix)))
        #expect(back.content == .returning(0))
        #expect(back.frame.size == FlowReturn.size)
        #expect(back.isFixed && !back.isResizable && !back.isAuxiliary)
        let cards = try ["a", "b"].map { try #require(node(graph, id(request, "agent:" + $0))) }
        // The row's branches converge into it, as they will into the result.
        for card in cards { #expect(graph.edges.contains { $0.source == card.id && $0.target == back.id && $0.joins }) }
        #expect(back.frame.minY == cards[0].frame.maxY + 32)
        let work = try #require(graph.nodes.first { $0.content == .work(0) })
        #expect(abs(back.frame.midX - work.frame.midX) < 0.5)
        #expect(node(graph, id(request, "result")) == nil)

        // The run ended with everything it draws: the result takes the node's place, at the same top.
        request.status = "completed"; request.finalOutput = "done"
        request.agents = request.agents.map { var agent = $0; agent.status = "completed"; return agent }
        let done = layout([request], running: false)
        #expect(node(done, id(request, MightyGraphLayout.returnSuffix)) == nil)
        let result = try #require(node(done, id(request, "result")))
        for child in ["a", "b"] { #expect(done.edges.contains { $0.source == id(request, "agent:" + child) && $0.target == result.id && $0.joins }) }
    }

    @Test func theAwaitingNoticeBlockTakesTheReturnNodesPlace() throws {
        // The request's own turn ended while its background agent runs: its result is the 알림 대기 block already.
        let request = run("r", agents: [agent("bg", background: true)], status: "completed")
        let graph = layout([request], running: true)
        #expect(node(graph, id(request, MightyGraphLayout.returnSuffix)) == nil)
        let result = try #require(node(graph, id(request, "result")))
        #expect(graph.edges.contains { $0.source == id(request, "agent:bg") && $0.target == result.id && $0.joins })
        #expect(BackgroundNoticeRuns.awaitingNotice(MightyGraphSupport.shownRuns([request])[0], in: MightyGraphSupport.shownRuns([request])) != nil)
        // While the turn still runs, the same background agent's card leads into the return node.
        let turn = run("r", agents: [agent("bg", background: true)], status: "running")
        let running = layout([turn])
        #expect(running.edges.contains { $0.source == id(turn, "agent:bg") && $0.target == id(turn, MightyGraphLayout.returnSuffix) })
    }

    @Test func aPlanWaitingForItsAnswerStandsThereInsteadAndANoticeRunHasNeither() {
        let request = run("r", agents: [agent("a", status: "completed")])
        let planned = layout([request], plan: "r")
        #expect(node(planned, id(request, MightyGraphLayout.returnSuffix)) == nil)
        #expect(node(planned, id(request, MightyGraphLayout.planSuffix)) != nil)
        // A notice run's copies are not delegated work: no line above them and no way back under them.
        let ended = run("r", agents: [agent("bg", status: "completed", background: true)], status: "completed")
        let notice = run("r:notice:1", agents: [agent("r:notice:1:bg", status: "completed", background: true, source: "bg")], status: "running", notice: true)
        let graph = layout([ended, notice])
        #expect(node(graph, id(notice, MightyGraphLayout.returnSuffix)) == nil)
        #expect(node(graph, id(notice, MightyGraphLayout.delegateSuffix + FlowDelegation.mainKey)) == nil)
        // The request above keeps its own line.
        #expect(node(graph, id(ended, MightyGraphLayout.delegateSuffix + FlowDelegation.mainKey)) != nil)
    }

    // MARK: Camera and live edges

    @Test func theFollowedBlockIsTheReturnNodeAndThenTheResult() throws {
        let viewport = CGSize(width: 900, height: 600)
        var request = run("r", agents: [agent("a"), agent("b")])
        let runs = MightyGraphSupport.shownRuns([request])
        let graph = MightyGraphLayout.make(runs: runs, draft: "", running: true, expanded: [], splitsExecution: true, fanOut: .init())
        // The lowest block of the newest work: the return node, never a delegate line.
        let followed = try #require(MightyGraphFollow.targetID(graph, runs: runs))
        #expect(followed == id(request, MightyGraphLayout.returnSuffix))
        let back = try #require(node(graph, followed))
        var watch = MightyGraphFollow.Watch()
        let camera = MightyGraphFollow.cameraOffset(for: back.frame, viewport: viewport, zoom: 1, alignTop: false, alignBottom: false, follows: true)
        watch.moved(targetID: followed, target: back.frame, camera: camera, viewport: viewport, zoom: 1)
        #expect(watch.following)
        // Nothing moved: the camera stays.
        #expect(watch.observe(targetID: followed, target: back.frame, camera: camera, viewport: viewport, zoom: 1) == nil)

        // The run ends: the result is the lowest block, and a following camera puts its bottom right above the composer.
        request.status = "completed"; request.finalOutput = "done"
        request.agents = request.agents.map { var agent = $0; agent.status = "completed"; return agent }
        let doneRuns = MightyGraphSupport.shownRuns([request])
        let done = MightyGraphLayout.make(runs: doneRuns, draft: "", running: false, expanded: [], splitsExecution: true, fanOut: .init())
        let next = try #require(MightyGraphFollow.targetID(done, runs: doneRuns))
        #expect(next == id(request, "result"))
        let result = try #require(node(done, next))
        let placed = watch.observe(targetID: next, target: result.frame, camera: camera, viewport: viewport, zoom: 1)
        let moved = try #require(placed)
        #expect(abs(moved.y + result.frame.maxY - (viewport.height - MightyGraphFollow.margin)) < 0.001)
        #expect(moved.x == camera.x)
        // A reader who scrolled away is left where they are when the return node goes.
        var away = MightyGraphFollow.Watch()
        away.moved(targetID: followed, target: back.frame, camera: CGPoint(x: camera.x, y: camera.y + 2_000), viewport: viewport, zoom: 1)
        #expect(!away.following)
        #expect(away.observe(targetID: next, target: result.frame, camera: CGPoint(x: camera.x, y: camera.y + 2_000), viewport: viewport, zoom: 1) == nil)
    }

    @Test func theCameraRulesTakeTheNewNodesAsAnyOther() {
        let request = run("r", agents: [agent("a")])
        let graph = layout([request])
        let ids = Set(graph.nodes.map(\.id))
        // A re-aim after the document moved goes to the request, never to a line or the return node.
        #expect(MightyGraphCamera.reaimAnchor(newestRunID: "r", selectedNodeID: nil, layoutNodeIDs: ids) == .reaim(nodeID: id(request, "request"), alignTop: true))
        #expect(graph.newestRequestTargetID([request]) == id(request, MightyGraphLayout.workSuffix))
        // The stranded rule reads frames alone: the same nodes under the same camera strand nobody.
        var watch = MightyGraphCamera.StrandedWatch()
        let frames = graph.nodes.map { ($0.id, $0.frame) }
        let camera = CGPoint(x: 0, y: 0)
        #expect(watch.observe(nodes: frames, camera: camera, zoom: 1, viewport: CGSize(width: 900, height: 600), newestRunID: "r", settled: true) == nil)
        #expect(watch.observe(nodes: frames, camera: camera, zoom: 1, viewport: CGSize(width: 900, height: 600), newestRunID: "r", settled: true) == nil)
        // Every node keeps its own frame: no two blocks, lines or cards overlap.
        for (index, first) in graph.nodes.enumerated() { for second in graph.nodes.dropFirst(index + 1) { #expect(!first.frame.intersects(second.frame), "\(first.id) × \(second.id)") } }
    }

    @Test func theEdgesIntoWhatStillRunsAreLive() throws {
        let request = run("r", agents: [agent("a"), agent("b", status: "completed", background: true)])
        let runs = MightyGraphSupport.shownRuns([request])
        let graph = MightyGraphLayout.make(runs: runs, draft: "", running: true, expanded: [], splitsExecution: true, fanOut: .init())
        let live = graph.liveFlowIDs(MightyGraphLayout.liveNodeIDs(runs, fanOut: true))
        let line = id(request, MightyGraphLayout.delegateSuffix + FlowDelegation.mainKey)
        // The running card, the line that leads to it, and the return node the run comes back into.
        #expect(live.contains(id(request, "agent:a")))
        #expect(live.contains(line))
        #expect(live.contains(id(request, MightyGraphLayout.returnSuffix)))
        #expect(!live.contains(id(request, "agent:b")))
        let split = graph.routes(into: live)
        // Into the 실행 block, the line, the running card and (from both cards) the return node; the ended card's branch is quiet.
        #expect(split.into.count == 5 && split.other.count == 1)
        // A row whose every card ended has a quiet line.
        let ended = run("r", agents: [agent("a", status: "completed"), agent("b", status: "completed", background: true)], status: "completed")
        let endedRuns = MightyGraphSupport.shownRuns([ended])
        let endedGraph = MightyGraphLayout.make(runs: endedRuns, draft: "", running: false, expanded: [], splitsExecution: true, fanOut: .init())
        #expect(endedGraph.liveFlowIDs(MightyGraphLayout.liveNodeIDs(endedRuns, fanOut: true)).isEmpty)
    }
}
