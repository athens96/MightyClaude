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
}
