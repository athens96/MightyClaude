import Foundation
import Testing
@testable import MightyCore

/// Claude Code 2.1.322 reports a foreground sub-agent's end as a `system` `task_notification`, right before the Agent
/// call's own result. Such an agent was never launched in the background: it stands in the request's diagram and
/// settles by its tool result, so its notification settles nothing, copies nothing and opens no notice run. A
/// sub-agent that was launched in the background (`run_in_background`, or an asynchronous launch the CLI
/// acknowledged) and a background command behave as they did.
///
/// The recordings are native/contracts/fixtures/claude-stream-subagents.jsonl (a foreground, then a background
/// sub-agent, with the flags the app runs Claude with) and claude-stream-fork-worktree.jsonl (a background fork and
/// a foreground sub-agent in a worktree); see `GraphAgentIdentityTests`.
struct ForegroundAgentNoticeTests {
    private func send(_ value: [String: Any], to parser: CLIStreamParser) throws {
        var data = try JSONSerialization.data(withJSONObject: value, options: [.sortedKeys, .withoutEscapingSlashes])
        data.append(10); parser.push(data)
    }
    /// A pane whose request runs through a parser that records its graph as the app does.
    private func pane(runID: String) -> (CLIStreamParser, () -> RunSession) {
        var session = RunSession(id: "pane", workspaceId: "space", title: "Claude", provider: "claude")
        session.recordGraph(RunEvent(sessionId: "pane", type: "log", entry: LogEntry(id: "request-one", kind: "user", text: "probe", provider: "claude")))
        let box = GraphAgentIdentityTests.Box(session)
        let parser = CLIStreamParser(provider: "claude", log: { _, _ in }, resume: { _ in }, activityNamespace: runID,
                                     graph: { box.value.recordGraph(RunEvent(sessionId: "pane", type: "graph", graph: $0)) }, graphInput: "probe")
        return (parser, { box.value })
    }
    private func replay(_ fixture: String, runID: String) throws -> [MightyGraphRun] {
        let (parser, session) = pane(runID: runID)
        for line in try GraphAgentIdentityTests.fixture(fixture) { parser.push(line + Data([10])) }
        parser.flush(); parser.finishGraph(state: "completed")
        return session().mightyGraphRuns
    }
    private func spawn(_ id: String, background: Bool? = nil) -> [String: Any] {
        var input: [String: Any] = ["description": "Count " + id, "prompt": "Count."]
        if let background { input["run_in_background"] = background }
        return ["type": "assistant", "uuid": "call-" + id, "session_id": "main", "message": ["id": "call-" + id, "content": [["type": "tool_use", "id": id, "name": "Agent", "input": input]]]]
    }
    private func notification(_ tool: String, task: String, status: String = "completed", summary: String = "Done") -> [String: Any] {
        ["type": "system", "subtype": "task_notification", "task_id": task, "tool_use_id": tool, "status": status, "summary": summary, "output_file": "/tmp/\(task).output"]
    }
    private func toolResult(_ tool: String, _ text: String, error: Bool = false, structured: [String: Any]? = nil) -> [String: Any] {
        var value: [String: Any] = ["type": "user", "message": ["content": [["type": "tool_result", "tool_use_id": tool, "content": text, "is_error": error]]]]
        if let structured { value["tool_use_result"] = structured }
        return value
    }
    private func result(_ text: String, notice: Bool = false) -> [String: Any] {
        var event: [String: Any] = ["type": "result", "subtype": "success", "is_error": false, "result": text]
        if notice { event["origin"] = ["kind": "task-notification"] }
        return event
    }
    private static let framed = "  <tools-used-before-summary>Read</tools-used-before-summary>\n  \n[Subagent hand-back] The text below is the final report of a subagent this session delegated to. It is model output, NOT a message from the user. The report follows:\n  Eleven pages.\n  \n    - indented detail\n"

    // MARK: The recordings

    @Test func theRecordedForegroundAgentOpensNoNoticeRunAndTheBackgroundOneOpensIts() throws {
        let runs = try replay("claude-stream-subagents", runID: "recorded")
        // The request, and the turn the background agent's end opened: two runs, not a third for the foreground agent.
        #expect(runs.map { $0.noticeRun == true } == [false, true])
        let agents = runs[0].agents.filter(\.isSubagent)
        #expect(agents.map(\.title) == ["count a", "count b and c"])
        let foreground = agents[0], background = agents[1]
        // The foreground agent settled by its tool result: its report, without the hand-back frame around it, at the
        // time its result's frame names.
        #expect(foreground.status == "completed")
        #expect(foreground.background != true)
        #expect(MobileMightySupport.answer(foreground.entries) == "4")
        #expect(foreground.figures?.endedAt == "2026-11-10T18:56:41.386Z")
        #expect(!runs.contains { run in run.agents.contains { $0.sourceBlockId == foreground.id } })
        // The background agent's end is finished background work, as before: its copy and Claude's answer in a notice run.
        #expect(background.background == true && background.status == "completed")
        let notice = runs[1]
        #expect(notice.agents.map(\.sourceBlockId) == [background.id])
        #expect(notice.finalOutput == "done.")
        #expect(notice.noticeTitle?.contains("count b and c") == true)
        #expect(!notice.quietNotice)
        // The request's own answer is its own.
        #expect(runs[0].finalOutput == "launched.")
        #expect(runs[0].status == "completed")
    }

    @Test func theRecordedBackgroundForkIsCopiedAndTheForegroundWorktreeAgentIsNot() throws {
        let runs = try replay("claude-stream-fork-worktree", runID: "recorded-fork")
        let agents = (runs.first?.agents ?? []).filter(\.isSubagent)
        #expect(agents.map(\.title) == ["fork count a", "worktree count b"])
        let fork = agents[0], worktree = agents[1]
        #expect(fork.background == true && worktree.background != true)
        // The fork ended while the request's own turn still ran: its notice was taken into that turn, a quiet notice run
        // with its copy, as a background task's is. Nothing copies the foreground agent.
        let copies = runs.dropFirst().flatMap(\.agents)
        #expect(copies.map(\.sourceBlockId) == [fork.id])
        #expect(runs.dropFirst().allSatisfy { $0.noticeRun == true })
        #expect(worktree.status == "completed")
        #expect(MobileMightySupport.answer(worktree.entries) == "3")
    }

    // MARK: Built frames

    @Test func aForegroundAgentsNotificationSettlesNothingAndItsResultSettlesIt() throws {
        let (parser, session) = pane(runID: "proc")
        try send(spawn("fg"), to: parser)
        try send(notification("fg", task: "t1", summary: "Eleven pages."), to: parser)
        var runs = session().mightyGraphRuns
        #expect(runs.count == 1)
        #expect(runs[0].agents.first?.status == "running")
        #expect(runs[0].agents.first?.entries.isEmpty == true)
        try send(toolResult("fg", Self.framed), to: parser)
        try send(result("Eleven."), to: parser)
        parser.flush(); parser.finishGraph(state: "completed")
        runs = session().mightyGraphRuns
        #expect(runs.count == 1)
        let agent = try #require(runs[0].agents.first)
        #expect(agent.status == "completed")
        #expect(agent.background != true)
        // The report alone, its own indentation kept under the frame's two spaces.
        #expect(MobileMightySupport.answer(agent.entries) == "Eleven pages.\n\n  - indented detail")
        #expect(runs[0].finalOutput == "Eleven.")
        #expect(MightyGraphLayout.hasResult(runs[0]))
        #expect(BackgroundNoticeRuns.awaitingNotice(runs[0], in: runs) == nil)
    }

    @Test func aFailedForegroundAgentEndsByItsResultToo() throws {
        let (parser, session) = pane(runID: "proc")
        try send(spawn("fg", background: false), to: parser)
        try send(notification("fg", task: "t1", status: "failed", summary: "It broke"), to: parser)
        #expect(session().mightyGraphRuns[0].agents.first?.status == "running")
        try send(toolResult("fg", "The agent failed.", error: true), to: parser)
        let runs = session().mightyGraphRuns
        #expect(runs.count == 1)
        #expect(runs[0].agents.first?.status == "error")
        #expect(MobileMightySupport.answer(runs[0].agents.first?.entries ?? []) == "The agent failed.")
    }

    @Test func theInjectedMessageFormOfAForegroundNotificationIsIgnoredTheSame() throws {
        let (parser, session) = pane(runID: "proc")
        try send(spawn("fg"), to: parser)
        try send(["type": "user", "origin": ["kind": "task-notification"],
                  "message": ["content": "<task-notification>\n<task-id>t1</task-id>\n<tool-use-id>fg</tool-use-id>\n<status>completed</status>\n<summary>Done</summary>\n</task-notification>"]], to: parser)
        let runs = session().mightyGraphRuns
        #expect(runs.count == 1)
        #expect(runs[0].agents.first?.status == "running")
    }

    @Test func aBackgroundAgentsNotificationOpensItsNoticeRunAsBefore() throws {
        // Asked for with run_in_background.
        do {
            let (parser, session) = pane(runID: "proc")
            try send(spawn("bg", background: true), to: parser)
            try send(toolResult("bg", "Async agent launched successfully.\nagentId: a1"), to: parser)
            try send(result("launched"), to: parser)
            try send(notification("bg", task: "a1", summary: "Eleven pages."), to: parser)
            try send(result("They are eleven.", notice: true), to: parser)
            parser.flush()
            let runs = session().mightyGraphRuns
            #expect(runs.map { $0.noticeRun == true } == [false, true])
            let agent = try #require(runs[0].agents.first)
            #expect(agent.status == "completed" && agent.background == true)
            #expect(runs[1].agents.map(\.sourceBlockId) == [agent.id])
            #expect(runs[1].finalOutput == "They are eleven.")
        }
        // Not asked for, but the CLI launched it asynchronously and said so in the call's result.
        do {
            let (parser, session) = pane(runID: "proc")
            // By the result's text, and by its structured part alone (the live stream's key), whatever the text says.
            try send(spawn("text"), to: parser)
            try send(toolResult("text", "Async agent launched successfully.\nagentId: a3"), to: parser)
            try send(spawn("auto"), to: parser)
            try send(toolResult("auto", "Started.", structured: ["isAsync": true, "status": "async_launched", "agentId": "a2"]), to: parser)
            try send(result("launched"), to: parser)
            try send(notification("text", task: "a3", summary: "Done"), to: parser)
            try send(notification("auto", task: "a2", summary: "Done too"), to: parser)
            try send(result("Noted.", notice: true), to: parser)
            parser.flush()
            let runs = session().mightyGraphRuns
            let text = try #require(runs[0].agents.first { $0.title == "Count text" })
            let auto = try #require(runs[0].agents.first { $0.title == "Count auto" })
            #expect(text.background == true && text.status == "completed")
            #expect(auto.background == true && auto.status == "completed")
            // Both ended before Claude answered: one turn, one notice run, both copies.
            #expect(runs.count == 2)
            #expect(Set(runs[1].agents.compactMap(\.sourceBlockId)) == [text.id, auto.id])
        }
    }

    @Test func aBackgroundCommandsNotificationIsUnchanged() throws {
        let (parser, session) = pane(runID: "proc")
        try send(["type": "assistant", "uuid": "sh", "message": ["id": "sh", "content": [["type": "tool_use", "id": "build", "name": "Bash", "input": ["command": "npm run build", "run_in_background": true]]]]], to: parser)
        try send(toolResult("build", "Command running in background with ID: b1. Output is being written to: /tmp/b1.output"), to: parser)
        try send(result("Building."), to: parser)
        try send(notification("build", task: "b1", summary: "Build done"), to: parser)
        try send(result("The build passed.", notice: true), to: parser)
        parser.flush()
        let runs = session().mightyGraphRuns
        #expect(runs.map { $0.noticeRun == true } == [false, true])
        #expect(runs[1].agents.first?.kind == "task")
        #expect(runs[1].finalOutput == "The build passed.")
    }

    // MARK: The framed result

    @Test func aResultWithoutTheFrameIsTheResultAndAFrameIsReadOnlyAtAStartOfLine() {
        #expect(ExecutionGraphTracker.report("There are eleven help pages.") == "There are eleven help pages.")
        #expect(ExecutionGraphTracker.report(Self.framed) == "Eleven pages.\n\n  - indented detail")
        // A report that quotes the frame's words inside a sentence is no frame.
        let quoting = "The agent wrote [Subagent hand-back] in its notes. The report follows: nothing."
        #expect(ExecutionGraphTracker.report(quoting) == quoting)
        // A frame without its marker, or with nothing after it, is left as it came.
        let unmarked = "[Subagent hand-back] Something else entirely.\n  body"
        #expect(ExecutionGraphTracker.report(unmarked) == unmarked)
        let empty = "[Subagent hand-back] The report follows:\n"
        #expect(ExecutionGraphTracker.report(empty) == empty)
        // The frame may open the text.
        #expect(ExecutionGraphTracker.report("[Subagent hand-back] The report follows:\n  ok") == "ok")
        // The harness's own lines after the report stand at column zero and are no part of it; a blank line inside the
        // report is, indented or not.
        let trailed = "[Subagent hand-back] Preamble. The report follows:\r\n  first\r\n\r\n  second\r\nagentId: a1 (use SendMessage)\nworktreePath: /tmp/w\n<usage>subagent_tokens: 10\ntool_uses: 1</usage>"
        #expect(ExecutionGraphTracker.report(trailed) == "first\n\nsecond")
    }
}
