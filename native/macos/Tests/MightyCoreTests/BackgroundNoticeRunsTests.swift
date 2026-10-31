import Foundation
import Testing
@testable import MightyCore

/// A notice turn is a run of its own (`BackgroundNoticeRuns`): the projection marks it, keeps the copies' source
/// blocks, and views draw background blocks only there.
struct BackgroundNoticeRunsTests {
    private func send(_ value: [String: Any], to parser: CLIStreamParser) throws {
        var data = try JSONSerialization.data(withJSONObject: value, options: [.sortedKeys, .withoutEscapingSlashes])
        data.append(10); parser.push(data)
    }
    private func assistant(_ blocks: [[String: Any]], id: String) -> [String: Any] {
        ["type": "assistant", "uuid": id, "session_id": "main-session", "message": ["id": id, "content": blocks]]
    }
    private func text(_ value: String, id: String) -> [String: Any] { assistant([["type": "text", "text": value]], id: id) }
    private func command(_ id: String, _ command: String, description: String) -> [String: Any] {
        assistant([["type": "tool_use", "id": id, "name": "Bash", "input": ["command": command, "description": description, "run_in_background": true]]], id: "call-" + id)
    }
    private func launched(_ tool: String, task: String) -> [String: Any] {
        ["type": "user", "message": ["content": [["type": "tool_result", "tool_use_id": tool,
                                                  "content": "Command running in background with ID: \(task). Output is being written to: /tmp/\(task).output"]]]]
    }
    private func notification(_ task: String, summary: String) -> [String: Any] {
        ["type": "user", "origin": ["kind": "task-notification"],
         "message": ["content": "<task-notification>\n<task-id>\(task)</task-id>\n<output-file>/tmp/\(task).output</output-file>\n<status>completed</status>\n<summary>\(summary)</summary>\n</task-notification>"]]
    }
    private func result(_ value: String, notice: Bool = false) -> [String: Any] {
        var event: [String: Any] = ["type": "result", "subtype": "success", "is_error": false, "result": value]
        if notice { event["origin"] = ["kind": "task-notification"] }
        return event
    }
    /// A pane whose request runs through a parser that records its lines and graph as the app does.
    private func pane(_ request: String, runID: String, id: String) -> (CLIStreamParser, () -> RunSession) {
        let box = SessionBox(RunSession(id: "pane", workspaceId: "space", title: "Claude", provider: "claude"))
        box.session.recordGraph(RunEvent(sessionId: "pane", type: "log", entry: LogEntry(id: id, kind: "user", text: request, provider: "claude")))
        let parser = CLIStreamParser(provider: "claude", log: { kind, text in box.session.recordGraph(RunEvent(sessionId: "pane", type: "log", entry: LogEntry(kind: kind, text: text, provider: "claude"))) },
                                     resume: { _ in }, activityNamespace: runID,
                                     graph: { box.session.recordGraph(RunEvent(sessionId: "pane", type: "graph", graph: $0)) }, graphInput: request)
        return (parser, { box.session })
    }
    private final class SessionBox { var session: RunSession; init(_ session: RunSession) { self.session = session } }

    @Test func runIDsNameTheOriginalAndTheirNumber() {
        #expect(BackgroundNoticeRuns.runID(original: "run-1", index: 2) == "run-1:notice:2")
        #expect(BackgroundNoticeRuns.isNoticeRun("run-1:notice:2"))
        #expect(!BackgroundNoticeRuns.isNoticeRun("run-1"))
        #expect(CoreValidation.identifier(BackgroundNoticeRuns.runID(original: UUID().uuidString, index: 12)))
    }

    @Test func backgroundBlocksShowOnlyInsideANoticeRun() {
        let agents = [MightyGraphAgent(id: "fg", title: "Read"), MightyGraphAgent(id: "bg", title: "Build", kind: "task", background: true),
                      MightyGraphAgent(id: "bg-child", parentID: "bg", title: "Inner"), MightyGraphAgent(id: "agent", title: "Survey", background: true)]
        let request = MightyGraphRun(id: "r", agents: agents)
        #expect(BackgroundNoticeRuns.shownAgents(request).map(\.id) == ["fg"])
        let notice = MightyGraphRun(id: "r:notice:1", agents: agents, noticeRun: true)
        #expect(BackgroundNoticeRuns.shownAgents(notice).map(\.id) == ["fg", "bg", "bg-child", "agent"])
        #expect(BackgroundNoticeRuns.shownAgents(MightyGraphRun(id: "plain", agents: [agents[0]])).map(\.id) == ["fg"])
    }

    /// The projection opens a run for the notice turn after the request: flagged, named by its finished work, with
    /// Claude's reply as its result and the reply's lines in it; the request keeps its own answer and its block.
    @Test func theProjectionOpensAFlaggedRunWithTheCopies() throws {
        try LocaleOverride.$language.withValue(.ko) { () throws in
            let (parser, session) = pane("build it", runID: "proc-1", id: "request-one")
            try send(command("build", "npm run build", description: "Build the app"), to: parser)
            try send(launched("build", task: "b1"), to: parser)
            try send(text("Building.", id: "m1"), to: parser)
            try send(result("Building."), to: parser)
            try send(notification("b1", summary: "Build done"), to: parser)
            try send(text("The build passed.", id: "m2"), to: parser)
            try send(result("The build passed.", notice: true), to: parser)
            parser.flush()
            let runs = session().mightyGraphRuns
            #expect(runs.count == 2)
            let request = try #require(runs.first), notice = try #require(runs.last)
            #expect(request.noticeRun == nil); #expect(request.finalOutput == "Building."); #expect(request.agents.count == 1)
            #expect(request.agents.first?.background == true); #expect(BackgroundNoticeRuns.shownAgents(request).isEmpty)
            #expect(notice.noticeRun == true); #expect(notice.id == "proc-1:notice:1"); #expect(notice.sourceRunID == "proc-1:notice:1")
            #expect(notice.input == "Build the app · 완료"); #expect(notice.status == "completed"); #expect(notice.finalOutput == "The build passed.")
            // The projection carries the main block's title, named after the finished work; a request has none.
            #expect(notice.noticeTitle == "백그라운드 작업 완료 · Build the app"); #expect(request.noticeTitle == nil)
            #expect(BackgroundNoticeRuns.heading(notice) == notice.noticeTitle)
            let copy = try #require(notice.agents.first)
            #expect(copy.sourceBlockId == request.agents.first?.id); #expect(copy.kind == "task"); #expect(copy.background == true)
            #expect(copy.title == "Build the app"); #expect(copy.input == "npm run build"); #expect(copy.status == "completed")
            #expect(copy.outputFile == "/tmp/b1.output"); #expect(BackgroundNoticeRuns.shownAgents(notice).count == 1)
            // The reply's own lines are the notice run's; the request's stay its own.
            #expect(notice.rootEntries.contains { $0.kind == "assistant" && $0.text == "The build passed." })
            #expect(!request.rootEntries.contains { $0.text == "The build passed." })
        }
    }

    /// A request sent while the earlier one still waits on background work stays the newest: the notice run goes
    /// before it, and the new request's process still binds to it.
    @Test func aNoticeRunGoesBeforeARequestStillWaitingForItsRun() throws {
        let box = SessionBox(RunSession(id: "pane", workspaceId: "space", title: "Claude", provider: "claude"))
        func record(_ event: RunEvent) { box.session.recordGraph(event) }
        record(RunEvent(sessionId: "pane", type: "log", entry: LogEntry(id: "request-one", kind: "user", text: "build it", provider: "claude")))
        let tracker = ExecutionGraphTracker(runID: "proc-1", input: "build it", emit: { record(RunEvent(sessionId: "pane", type: "graph", graph: $0)) })
        for event in [command("build", "npm run build", description: "Build the app"), launched("build", task: "b1"), result("Building.")] { tracker.consume(event) }
        record(RunEvent(sessionId: "pane", type: "log", entry: LogEntry(id: "request-two", kind: "user", text: "and lint", provider: "claude")))
        tracker.consume(notification("b1", summary: "Build done"))
        _ = ExecutionGraphTracker(runID: "proc-2", input: "and lint", emit: { record(RunEvent(sessionId: "pane", type: "graph", graph: $0)) })
        let runs = box.session.mightyGraphRuns
        #expect(runs.map(\.id) == ["request-one", "proc-1:notice:1", "request-two"])
        #expect(runs.map(\.noticeRun) == [nil, true, nil])
        #expect(runs.last?.sourceRunID == "proc-2")
    }

    /// A notification taken into the request's own turn: its finished notice run comes last while the request still
    /// runs, yet the request stays the newest request, and its later lines, status and usage are still its own.
    @Test func anAbsorbedNoticeRunAfterARunningRequestLeavesItTheNewestRequest() throws {
        let box = SessionBox(RunSession(id: "pane", workspaceId: "space", title: "Claude", provider: "claude"))
        func record(_ event: RunEvent) { box.session.recordGraph(event) }
        record(RunEvent(sessionId: "pane", type: "log", entry: LogEntry(id: "request-one", kind: "user", text: "build it", provider: "claude")))
        let tracker = ExecutionGraphTracker(runID: "proc-1", input: "build it", emit: { record(RunEvent(sessionId: "pane", type: "graph", graph: $0)) })
        for event in [command("build", "npm run build", description: "Build the app"), launched("build", task: "b1"), notification("b1", summary: "Build done")] {
            tracker.consume(event)
        }
        var runs = box.session.mightyGraphRuns
        #expect(runs.map(\.id) == ["request-one", "proc-1:notice:1"]); #expect(runs.last?.status == "completed")
        #expect(MightyGraphSupport.newestRequestRun(runs)?.id == "request-one"); #expect(MightyGraphSupport.requestRuns(runs).map(\.id) == ["request-one"])
        #expect(box.session.withoutNoticeRuns.graphRuns?.map(\.id) == ["request-one"])
        record(RunEvent(sessionId: "pane", type: "log", entry: LogEntry(id: "line-1", kind: "assistant", text: "Built and done.", provider: "claude")))
        record(RunEvent(sessionId: "pane", type: "usage", usage: SessionUsage(provider: "claude", source: "stream", model: "claude-opus-4-5")))
        tracker.consume(result("Built and done."))
        record(RunEvent(sessionId: "pane", type: "status", status: "completed"))
        runs = box.session.mightyGraphRuns
        let request = try #require(runs.first), notice = try #require(runs.last)
        #expect(request.rootEntries.contains { $0.text == "Built and done." }); #expect(notice.rootEntries.isEmpty)
        #expect(request.status == "completed"); #expect(request.finalOutput == "Built and done.")
        #expect(notice.finalOutput == nil)
        #expect(request.nodeModelLabel == GraphModelLabel.nodeModelLabel(cliReportedModel: "claude-opus-4-5", configuredModel: request.configuredModel ?? "default"))
    }

    /// A notice turn's lines and status never reach a request still waiting for its own run.
    @Test func aRunningNoticeTurnKeepsItsLinesFromAWaitingRequest() throws {
        let box = SessionBox(RunSession(id: "pane", workspaceId: "space", title: "Claude", provider: "claude"))
        func record(_ event: RunEvent) { box.session.recordGraph(event) }
        record(RunEvent(sessionId: "pane", type: "log", entry: LogEntry(id: "request-one", kind: "user", text: "build it", provider: "claude")))
        let tracker = ExecutionGraphTracker(runID: "proc-1", input: "build it", emit: { record(RunEvent(sessionId: "pane", type: "graph", graph: $0)) })
        for event in [command("build", "npm run build", description: "Build the app"), launched("build", task: "b1"), result("Building.")] { tracker.consume(event) }
        box.session.beginGraphRun(input: "and lint", id: "request-two", configuredModel: "default", status: "starting")
        tracker.consume(notification("b1", summary: "Build done"))
        record(RunEvent(sessionId: "pane", type: "log", entry: LogEntry(id: "line-1", kind: "assistant", text: "The build passed.", provider: "claude")))
        var runs = box.session.mightyGraphRuns
        #expect(runs.map(\.id) == ["request-one", "proc-1:notice:1", "request-two"])
        #expect(runs[1].status == "running"); #expect(runs[1].rootEntries.contains { $0.text == "The build passed." })
        #expect(runs[2].status == "starting"); #expect(runs[2].rootEntries.isEmpty)
        record(RunEvent(sessionId: "pane", type: "status", status: "completed"))
        runs = box.session.mightyGraphRuns
        #expect(runs[2].status == "starting"); #expect(runs[2].finalOutput == nil)
        #expect(MightyGraphSupport.newestRequestRun(runs)?.id == "request-two")
    }

    @Test func theFlagsSurviveCodingAndOldSavesReadAsAbsent() throws {
        let run = MightyGraphRun(id: "r:notice:1", agents: [MightyGraphAgent(id: "c", kind: "task", background: true, sourceBlockId: "graph-source")], noticeRun: true)
        let decoded = try JSONDecoder().decode(MightyGraphRun.self, from: JSONEncoder().encode(run))
        #expect(decoded == run); #expect(decoded.noticeRun == true); #expect(decoded.agents.first?.sourceBlockId == "graph-source")
        let old = Data(#"{"id":"r","input":"","status":"completed","rootEntries":[],"agents":[{"id":"a","title":"A","input":"","status":"completed","entries":[]}],"resultEntries":[]}"#.utf8)
        let legacy = try JSONDecoder().decode(MightyGraphRun.self, from: old)
        #expect(legacy.noticeRun == nil); #expect(legacy.agents.first?.sourceBlockId == nil)
        let node = ExecutionGraphNode(id: "n", runId: "r:notice:1", kind: "task", state: "completed", title: "T", sourceBlockId: "graph-source")
        #expect(try JSONDecoder().decode(ExecutionGraphNode.self, from: JSONEncoder().encode(node)).sourceBlockId == "graph-source")
        var budget = 1_000_000
        #expect(MightyGraphSupport.normalized([run], restoring: true, budget: &budget).first?.noticeRun == true)
    }

    /// A notice run without a title of its own (an older save) reads the generic heading; one with a title reads it.
    @Test func theHeadingIsTheRunsTitleOrTheGenericOne() {
        LocaleOverride.$language.withValue(.ko) {
            #expect(BackgroundNoticeRuns.genericTitle == L("graph.background.noticeRun"))
            #expect(BackgroundNoticeRuns.heading(MightyGraphRun(id: "r:notice:1", noticeRun: true)) == BackgroundNoticeRuns.genericTitle)
            #expect(BackgroundNoticeRuns.heading(MightyGraphRun(id: "r:notice:1", noticeRun: true, noticeTitle: "  \n")) == BackgroundNoticeRuns.genericTitle)
            let named = MightyGraphRun(id: "r:notice:1", noticeRun: true, noticeTitle: "하위 에이전트 메시지 · scout")
            #expect(BackgroundNoticeRuns.heading(named) == "하위 에이전트 메시지 · scout")
            // A title a view carries reads the same way, trimmed.
            #expect(BackgroundNoticeRuns.heading(title: nil) == BackgroundNoticeRuns.genericTitle)
            #expect(BackgroundNoticeRuns.heading(title: " \n") == BackgroundNoticeRuns.genericTitle)
            #expect(BackgroundNoticeRuns.heading(title: " 위임 알림 \n") == "위임 알림")
        }
    }

    /// The title is saved with the run; a save without it reads nil; a request run keeps none through normalization.
    @Test func theNoticeTitleSurvivesCodingAndOldSavesReadNil() throws {
        let run = MightyGraphRun(id: "r:notice:1", status: "completed", noticeRun: true, noticeTitle: "백그라운드 작업 완료 · Build")
        let decoded = try JSONDecoder().decode(MightyGraphRun.self, from: JSONEncoder().encode(run))
        #expect(decoded == run); #expect(decoded.noticeTitle == "백그라운드 작업 완료 · Build")
        let old = Data(#"{"id":"r:notice:1","input":"","status":"completed","rootEntries":[],"agents":[],"resultEntries":[],"noticeRun":true}"#.utf8)
        #expect(try JSONDecoder().decode(MightyGraphRun.self, from: old).noticeTitle == nil)
        var budget = 1_000_000
        let request = MightyGraphRun(id: "r", status: "completed", noticeTitle: "stray")
        let normalized = MightyGraphSupport.normalized([request, run], restoring: true, budget: &budget)
        #expect(normalized.map(\.noticeTitle) == [nil, "백그라운드 작업 완료 · Build"])
    }

    // MARK: Views of notice runs

    private static func layout(_ runs: [MightyGraphRun], running: Bool = false) -> MightyGraphLayout {
        .make(runs: runs, draft: "", running: running, expanded: [], splitsExecution: true)
    }
    private static func node(_ run: String, _ suffix: String) -> String { MightyGraphBlockSize.nodeID(runID: run, suffix: suffix) }
    /// A request whose background command still runs, a notice run taken into its turn (no reply), and one with a reply.
    private static func runs(answered: Bool = true, requestStatus: String = "completed") -> [MightyGraphRun] {
        let task = MightyGraphAgent(id: "t", title: "Build", input: "npm run build", status: "running", kind: "task", background: true)
        var request = MightyGraphRun(id: "r", input: "build it", status: requestStatus, agents: [MightyGraphAgent(id: "fg", title: "Read", status: "completed"), task],
                                     finalOutput: requestStatus == "completed" ? "Started." : nil)
        request.refreshResult()
        var copy = task; copy.id = "r:notice:1:t"; copy.status = "completed"; copy.sourceBlockId = "t"
        var quiet = MightyGraphRun(id: "r:notice:1", input: "Build · 완료", status: "completed", agents: [copy], noticeRun: true)
        quiet.refreshResult()
        var reply = MightyGraphRun(id: "r:notice:2", input: "Build · 완료", status: answered ? "completed" : "running", agents: [copy],
                                   finalOutput: answered ? "The build passed." : nil, noticeRun: true)
        reply.agents[0].id = "r:notice:2:t"
        reply.refreshResult()
        return [request, quiet, reply]
    }

    @Test func theRunRulesNameTheNewestRequestTheFollowedRunAndTheNumbers() {
        let all = Self.runs()
        #expect(MightyGraphSupport.newestRequestIndex(all) == 0)
        // A quiet notice run is never followed; one with a reply, or still running, is.
        #expect(all[1].quietNotice && !all[2].quietNotice && !all[0].quietNotice)
        #expect(MightyGraphSupport.followRunIndex(all) == 2)
        #expect(MightyGraphSupport.followRunIndex(Self.runs(answered: false)) == 2)
        #expect(MightyGraphSupport.followRunIndex(Array(all.prefix(2))) == 0)
        // A request after the notice runs is the newest again; notice runs take no number.
        let next = all + [MightyGraphRun(id: "n", status: "starting")]
        #expect(MightyGraphSupport.newestRequestIndex(next) == 3 && MightyGraphSupport.followRunIndex(next) == 3)
        #expect(MightyGraphSupport.requestOrdinals(next) == [1, 1, 1, 2])
        #expect(MightyGraphSupport.requestOrdinals([all[1], all[0]]) == [1, 1])
        #expect(MightyGraphSupport.newestRequestIndex([all[1]]) == nil)
        // Views draw the request without its background block; data keeps it.
        let shown = MightyGraphSupport.shownRuns(all)
        #expect(shown[0].agents.map(\.id) == ["fg"] && all[0].agents.count == 2 && shown[2].agents.count == 1)
    }

    @Test func aRequestWhoseBackgroundWorkRunsOnHasItsResultAndAQuietNoticeRunHasNone() {
        let all = Self.runs()
        // The request's own turn ended: its result shows while its background command still runs in the strip.
        #expect(all[0].settled && MightyGraphLayout.finished(all[0]) && all[0].resultEntries.first?.text == "Started.")
        let graph = Self.layout(MightyGraphSupport.shownRuns(all))
        let ids = Set(graph.nodes.map(\.id))
        #expect(ids.contains(Self.node("r", "result")))
        #expect(!ids.contains(Self.node("r", "agent:t")))
        #expect(!ids.contains(Self.node("r:notice:1", "result")) && ids.contains(Self.node("r:notice:1", "agent:r:notice:1:t")))
        #expect(ids.contains(Self.node("r:notice:2", "result")))
        // The latest result is the newest run that has one, never the quiet run's empty card.
        #expect(MightyGraphLayout.latestResultID(runs: all) == Self.node("r:notice:2", "result"))
        #expect(MightyGraphLayout.latestResultID(runs: Array(all.prefix(2))) == Self.node("r", "result"))
        #expect(MightyGraphLayout.fittedResultID(runs: Array(all.prefix(2)), viewport: CGSize(width: 900, height: 600), sharedResultSize: nil) == Self.node("r", "result"))
        #expect(MightyGraphLayout.filesPanelOpen(runs: Array(all.prefix(2)), resultFilesRunID: "r"))
        // 기록 gives the quiet run no result card either, and numbers the request alone.
        let groups = MightyTimeline.groups(MightyGraphSupport.shownRuns(all))
        #expect(groups.map(\.ordinal) == [1, 1, 1] && groups.map(\.noticeRun) == [false, true, true])
        #expect(groups[1].result == nil && groups[2].result?.text == "The build passed.")
        #expect(groups[0].rows.map(\.nodeID) == [Self.node("r", "request"), Self.node("r", MightyGraphLayout.workSuffix), Self.node("r", "agent:fg")])
    }

    @Test func theFollowedBlockMovesOnToANoticeRunWithAReplyButNotToAQuietOne() {
        let all = MightyGraphSupport.shownRuns(Self.runs())
        #expect(MightyGraphFollow.targetID(Self.layout(all), runs: all) == Self.node("r:notice:2", "result"))
        // Still running, its newest block is followed.
        let open = MightyGraphSupport.shownRuns(Self.runs(answered: false))
        #expect(MightyGraphFollow.targetID(Self.layout(open, running: true), runs: open) == Self.node("r:notice:2", "agent:r:notice:2:t"))
        // A quiet notice run after a running request leaves the camera on the request.
        let running = MightyGraphSupport.shownRuns(Array(Self.runs(requestStatus: "running").prefix(2)))
        let graph = Self.layout(running, running: true)
        #expect(MightyGraphFollow.targetID(graph, runs: running) == Self.node("r", "agent:fg"))
        #expect(graph.newestRequestTargetID(running) == Self.node("r", MightyGraphLayout.workSuffix))
        #expect(graph.newestRequestTargetID(all) == Self.node("r", "request"))
    }

    @Test func aNoticeRunStaysInThePhaseBeforeItAndTheNewestRequestTakesThePanes() throws {
        let style = try #require(BundledStyles.shared.evaluator("ouroboros"))
        let request = MightyGraphRun(id: "r1", input: "ooo interview 할 일 앱", status: "completed")
        let notice = MightyGraphRun(id: "r1:notice:1", input: "ooo seed · 완료", status: "completed", finalOutput: "done", noticeRun: true)
        // The pane's phase is its newest request's, not the notice run's after it; the notice run's lines name no phase.
        #expect(MightyStructure.phaseIDs(runs: [request, notice], style: style, currentPhase: style.manifest.phase("seed")) == ["seed", "seed"])
        #expect(MightyStructure.phaseIDs(runs: [request, notice], style: style) == ["interview", "interview"])
        let plan = try #require(BundledStyles.shared.evaluator("claude-plan"))
        let executed = MightyGraphRun(id: "p1", input: "go", status: "completed", sourceRunID: "s1")
        let record = PlanRecord(id: "x", runId: "c", plan: "# plan", receivedAt: "2026-10-12T00:00:00.000Z", decidedAt: "2026-10-12T00:01:00.000Z",
                                outcome: .approvedAuto, graphRunId: "s1")
        #expect(MightyStructure.phaseIDs(runs: [executed, MightyGraphRun(id: "s1:notice:1", status: "completed", noticeRun: true)], style: plan, planHistory: [record])
                == ["execute", "execute"])
        // The structure leaves the request's background block out and numbers the request alone.
        let all = Self.runs()
        let items = MightyStructure.make(runs: all, style: nil, currentPhase: nil)
        let rows: [MightyStructure.Row]
        if case .free(let free) = items { rows = free } else { rows = [] }
        #expect(rows.map(\.item.ordinal) == [1, 1, 1] && rows.map(\.item.noticeRun) == [false, true, true])
        #expect(rows[0].item.clusters.flatMap(\.nodes).map(\.agentID) == ["fg"] && rows[1].item.clusters.flatMap(\.nodes).map(\.agentID) == ["r:notice:1:t"])
        // The board a view moves on with is never a quiet notice's.
        let boards = MightyStructure.boards(MightyStructure.make(runs: Array(all.prefix(2)), style: nil, currentPhase: nil))
        #expect(boards.map(\.quiet) == [false, true])
        var navigation = MightyStructure.Navigation()
        #expect(navigation.index(in: boards) == 0 && navigation.onNewest(in: boards))
        navigation.step(1, in: boards)
        #expect(navigation.index(in: boards) == 1 && !navigation.onNewest(in: boards))
        navigation.showNewest(in: boards)
        #expect(navigation.index(in: boards) == 0)
    }

    /// 전송 중 ends, and the request leaves "starting", by one rule: only the request's own lines, statuses and blocks.
    @Test func onlyTheNewestRequestsOwnEventsStartIt() {
        var notice = MightyGraphRun(id: "proc-1:notice:1", status: "running", sourceRunID: "proc-1:notice:1", noticeRun: true)
        notice.agents = []
        let runs = [MightyGraphRun(id: "one", status: "completed", sourceRunID: "proc-1"), notice, MightyGraphRun(id: "two", status: "starting")]
        let line = RunEvent(sessionId: "pane", type: "log", entry: LogEntry(id: "l", kind: "assistant", text: "x", provider: "claude"))
        let status = RunEvent(sessionId: "pane", type: "status", status: "running")
        #expect(!MightyGraphSupport.newestRequestOwns(line, runs: runs) && !MightyGraphSupport.newestRequestOwns(status, runs: runs))
        let old = RunEvent(sessionId: "pane", type: "graph", graph: ExecutionGraphNode(id: "b", runId: "proc-1", kind: "task", state: "completed", title: "B"))
        let fresh = RunEvent(sessionId: "pane", type: "graph", graph: ExecutionGraphNode(id: "m", runId: "proc-2", kind: "main", state: "running", title: "M"))
        #expect(!MightyGraphSupport.newestRequestOwns(old, runs: runs) && MightyGraphSupport.newestRequestOwns(fresh, runs: runs))
        // The notice turn over, the process's lines are the new request's.
        var ended = runs; ended[1].status = "completed"
        #expect(MightyGraphSupport.newestRequestOwns(line, runs: ended) && MightyGraphSupport.newestRequestOwns(status, runs: ended))
        #expect(MightyGraphSupport.newestRequestOwns(line, runs: []))
    }

    /// A request whose background command still runs, or ended with its notice turn not answered yet, awaits notices: its
    /// result is the 알림 대기 block (알림 대기 중, the command's row and "알림 n/m") in the diagram, 기록, the pipeline and on
    /// the phone, and the pane reads 알림 대기; once the notice turn is answered it is 최종 결과 again. A failed or stopped
    /// request awaits nothing (Windows BackgroundNoticeRunsVerification.ARequestAwaitingNoticesIsTheAwaitingNoticeBlock).
    @Test func aRequestAwaitingNoticesIsTheAwaitingNoticeBlock() throws {
        try LocaleOverride.$language.withValue(.ko) { () throws in
            var all = Self.runs(answered: false)
            #expect(BackgroundNoticeRuns.originalRunID("r:notice:2") == "r" && BackgroundNoticeRuns.originalRunID("r") == nil)
            let awaiting = try #require(BackgroundNoticeRuns.awaitingNotice(all[0], in: all))
            #expect(awaiting.tasks == [BackgroundNoticeRuns.AwaitedTask(id: "t", title: "Build", status: "running")] && awaiting.delivered == 0)
            #expect(awaiting.tasks[0].word == "끝나면 알림" && awaiting.progress == "알림 0/1")
            #expect(MightyGraphSupport.resultTitle(status: "completed", awaiting: true) == "알림 대기 중")
            #expect(MightyGraphSupport.resultTitle(status: "completed") == "최종 결과")
            #expect(MightyGraphSupport.resultTitle(status: "error", awaiting: true) == L("graph.block.resultError"))
            // The shown run carries what it read of the notice runs.
            #expect(BackgroundNoticeRuns.awaitingNotice(MightyGraphSupport.shownRuns(all)[0], in: []) == awaiting)
            #expect(MightyTimeline.groups(all)[0].result?.awaiting == true && MightyTimeline.groups(all)[0].result?.tone == .wait)
            if case .free(let rows) = MightyStructure.make(runs: all, style: nil, currentPhase: nil) {
                #expect(rows.map(\.item.awaiting) == [true, false, false])
            } else { Issue.record("a free pane makes rows") }
            func resultColumn() -> DesignTone? {
                let board = MightyStructure.boards(MightyStructure.make(runs: all, style: nil, currentPhase: nil))[0]
                return MightyStructure.Tone.column(board.columns[2])
            }
            #expect(resultColumn() == .wait)
            // The phone: the block's rows, and the running count for an older phone.
            let mobile = MobileMightySupport.runs(all)
            #expect(mobile[0].awaitingNotice == MobileAwaitingNotice(delivered: 0, total: 1, tasks: [MobileAwaitedTask(title: "Build", status: "running")]))
            #expect(mobile[0].backgroundWaiting == 1 && mobile[1].awaitingNotice == nil && mobile[2].awaitingNotice == nil)
            let json = try #require(JSONSerialization.jsonObject(with: JSONEncoder().encode(mobile[0])) as? [String: Any])
            let wire = try #require(json["awaitingNotice"] as? [String: Any])
            #expect(wire["delivered"] as? Int == 0 && wire["total"] as? Int == 1 && (wire["tasks"] as? [[String: Any]])?.first?["status"] as? String == "running")
            // The pane is alive only for the notice: the pet and the header say 알림 대기.
            var pane = RunSession(id: "pane", workspaceId: "space", title: "Claude", provider: "claude")
            pane.status = "running"; pane.graphRuns = all
            #expect(BackgroundNoticeRuns.awaitsNotice(pane) && BackgroundNoticeRuns.stateWord == "알림 대기")
            pane.status = "completed"
            #expect(!BackgroundNoticeRuns.awaitsNotice(pane))
            // The command ended and its notice turn runs: still awaiting, its notice delivered.
            let digest = MobileMightySupport.digest(all)
            all[0].agents[1].status = "completed"
            let delivered = try #require(BackgroundNoticeRuns.awaitingNotice(all[0], in: all))
            #expect(delivered.delivered == 1 && delivered.progress == "알림 1/1" && delivered.tasks[0].word == "알림 전달됨" && all[0].backgroundWaiting == 0)
            #expect(MobileMightySupport.runs(all)[0].awaitingNotice?.delivered == 1 && MobileMightySupport.digest(all) != digest)
            // The notice turn answered: the normal 최종 결과.
            all[2].status = "completed"; all[2].finalOutput = "The build passed."
            #expect(BackgroundNoticeRuns.awaitingNotice(all[0], in: all) == nil)
            #expect(MightyTimeline.groups(all)[0].result?.awaiting == false && MightyTimeline.groups(all)[0].result?.tone == .done)
            #expect(MobileMightySupport.runs(all)[0].awaitingNotice == nil)
            #expect(resultColumn() == .done)
            // Failed or stopped work keeps its word; a failed or stopped request, or a notice run, awaits nothing.
            all[0].agents[1].status = "error"; all[2].status = "running"
            #expect(BackgroundNoticeRuns.awaitingNotice(all[0], in: all)?.tasks.first?.word == "실패")
            all[0].agents[1].status = "stopped"
            #expect(BackgroundNoticeRuns.awaitingNotice(all[0], in: all)?.tasks.first?.word == "중단됨")
            #expect(BackgroundNoticeRuns.awaitingNotice(all[2], in: all) == nil)
            for status in ["error", "stopped", "running"] {
                let other = Self.runs(answered: false, requestStatus: status)
                #expect(BackgroundNoticeRuns.awaitingNotice(other[0], in: other) == nil, "\(status)")
            }
        }
    }

    /// The 실행 block still says the request waits on its background work after views dropped those blocks.
    @Test func aShownRequestStillCountsTheBackgroundWorkItWaitsOn() throws {
        let all = Self.runs()
        #expect(all[0].backgroundWaiting == 1)
        let shown = MightyGraphSupport.shownRuns(all)
        #expect(shown[0].agents.map(\.id) == ["fg"] && shown[0].backgroundWaiting == 1)
        #expect(MightyGraphSupport.shownRuns(Self.runs(requestStatus: "running"))[0].backgroundWaiting == 0)
        // The count is never saved: the state file holds the run as it is.
        let data = try JSONEncoder().encode(shown[0])
        #expect(!String(decoding: data, as: UTF8.self).contains("shownBackgroundWaiting"))
        #expect(try JSONDecoder().decode(MightyGraphRun.self, from: JSONEncoder().encode(all[0])) == all[0])
    }

    /// Only a notice run that opened complete without a reply is quiet: a notice turn that failed or was stopped before
    /// its reply keeps its card and is followed; one a later turn reopens keeps showing its earlier reply.
    @Test func aFailedNoticeTurnIsNotQuietAndAReopenedOneKeepsItsReply() {
        var failed = MightyGraphRun(id: "r:notice:1", status: "error", noticeRun: true)
        failed.refreshResult()
        #expect(!failed.quietNotice && MightyGraphLayout.hasResult(failed))
        let stopped = MightyGraphRun(id: "r:notice:1", status: "stopped", noticeRun: true)
        #expect(!stopped.quietNotice)
        let request = MightyGraphRun(id: "r", status: "completed", finalOutput: "Started.")
        #expect(MightyGraphSupport.followRunIndex([request, failed]) == 1)
        var reopened = MightyGraphRun(id: "r:notice:1", status: "running", finalOutput: "The build passed.", noticeRun: true)
        reopened.refreshResult()
        #expect(reopened.reopenedWithReply && MightyGraphLayout.hasResult(reopened) && reopened.resultEntries.first?.text == "The build passed.")
        let graph = Self.layout([request, reopened], running: true)
        #expect(graph.nodes.contains { $0.id == Self.node("r:notice:1", "result") })
        // The reopened turn failed or was stopped: the card keeps the earlier reply; a request's error result still shows none.
        for status in ["error", "stopped"] {
            var ended = reopened
            ended.status = status
            ended.refreshResult()
            #expect(MightyGraphLayout.hasResult(ended) && ended.resultEntries.first?.text == "The build passed.")
        }
        var failedRequest = MightyGraphRun(id: "r", status: "error", finalOutput: "Started.")
        failedRequest.refreshResult()
        #expect(failedRequest.resultEntries.isEmpty)
        // A first notice turn still waiting for its reply has no card yet.
        var opening = MightyGraphRun(id: "r:notice:2", status: "running", noticeRun: true)
        opening.refreshResult()
        #expect(!MightyGraphLayout.hasResult(opening) && opening.resultEntries.isEmpty)
    }

    /// A process that ends in the middle of a notice turn ends that turn's run, so the request waiting behind it is
    /// started by its own process's lines and 전송 중 cannot stay.
    @Test func aProcessEndingInANoticeTurnEndsItsRun() throws {
        let box = SessionBox(RunSession(id: "pane", workspaceId: "space", title: "Claude", provider: "claude"))
        func record(_ event: RunEvent) { box.session.recordGraph(event) }
        record(RunEvent(sessionId: "pane", type: "log", entry: LogEntry(id: "request-one", kind: "user", text: "build it", provider: "claude")))
        let tracker = ExecutionGraphTracker(runID: "proc-1", input: "build it", emit: { record(RunEvent(sessionId: "pane", type: "graph", graph: $0)) })
        for event in [command("build", "npm run build", description: "Build the app"), launched("build", task: "b1"), result("Building.")] { tracker.consume(event) }
        box.session.beginGraphRun(input: "and lint", id: "request-two", configuredModel: "default", status: "starting")
        tracker.consume(notification("b1", summary: "Build done"))
        let line = RunEvent(sessionId: "pane", type: "log", entry: LogEntry(id: "line-2", kind: "assistant", text: "Linting.", provider: "claude"))
        #expect(box.session.mightyGraphRuns[1].status == "running")
        #expect(!MightyGraphSupport.newestRequestOwns(line, runs: box.session.graphRuns ?? []))
        tracker.finish(state: "stopped")
        var runs = box.session.mightyGraphRuns
        #expect(runs.map(\.id) == ["request-one", "proc-1:notice:1", "request-two"])
        #expect(runs[1].status == "stopped" && !runs[1].quietNotice)
        #expect(MightyGraphSupport.newestRequestOwns(line, runs: box.session.graphRuns ?? []))
        record(line)
        runs = box.session.mightyGraphRuns
        #expect(runs[2].status == "running" && runs[2].rootEntries.contains { $0.text == "Linting." })
    }
}
